# 局域网架构

## 现状

项目里现有联机主链路已经比较清晰：

- `LanMultiplayerSession`：TCP 会话与消息收发
- `Simulator3dForm.LanMultiplayer.cs`：房间、输入缓冲、快照回填、裁判回报
- `Simulator3dHost`：权威世界构建、对局配置、落地仿真
- `TerrainMotionService` / `SimulationCombatMath`：运动、碰撞、自瞄、交互与判定

也就是说，联机不是“单个大包硬推”，而是已经有了房间层、比赛层、同步层的雏形。接下来要做的是把消息职责收紧。

## 建议分层

### 1. 会话层

负责连接、版本、心跳、重连、房间状态。

- 创建房间
- 加入房间
- 角色分配
- 版本校验
- 断线提示

### 2. 比赛层

负责比赛开始、暂停、回合、裁判判罚、复活、补给、胜负。

- 开局参数
- 阵营和车位
- 规则模式
- 判罚结果
- 可靠事件广播

### 3. 同步层

负责高频输入、位姿、弹丸、装甲板、场地交互态。

- 输入流：低延迟、可丢帧、按序号去重
- 位姿流：可回填、可纠偏
- 弹丸流：短生命周期、可裁剪
- 交互流：装甲板、基地、前哨、能量机关

### 4. 观测层

负责大厅 UI、F8/F3 调试、局内 HUD、回放快照。

- 只读
- 不反向改仿真
- 只消费权威快照或本地预测结果

## 推荐链路

### 上行

建议拆成 5 类：

1. `InputFrame`
2. `RobotPoseFrame`
3. `ProjectileSpawnFrame`
4. `InteractionFrame`
5. `RefereeReportFrame`

### 下行

建议拆成 5 类：

1. `MatchConfigFrame`
2. `WorldSnapshotFrame`
3. `ProjectileSnapshotFrame`
4. `FacilityStateFrame`
5. `RefereeDecisionFrame`

## 你这份字段草案的落点

### 1. 机器人位姿

建议统一成一个固定点块，按 `uint16` 打包，语义固定，不临时扩字段。

建议包含：

- 位置：`x, y, z`
- 姿态：`yaw, pitch, roll`
- 线速度：`vx, vy, vz`
- 线加速度：`ax, ay, az`
- 角速度：`wz`
- 云台：`gimbal_yaw, gimbal_pitch`

如果要压到固定 12 槽，建议把“机器人本体”和“云台”拆成两个子块，而不是硬挤在一个块里。

### 2. 弹丸

不要只传“瞬时全部 30 发满包”，建议改为：

- `spawn`：新弹丸生成
- `update`：活跃弹丸位置更新
- `despawn`：失活回收

这样能省带宽，也更适合回放和补包。

### 3. 装甲板

建议不要只用 `big/small/hitted/unhitted`，而是统一成：

- `plate_id`
- `plate_type`
- `hit_state`
- `pose_id`
- `damage_scale`

这样自瞄、F8 框、伤害倍率、命中回传可以共用同一份目标定义。

### 4. 场地交互

建议按设施类型统一：

- 前哨站
- 基地
- 能量机关
- buff 地块

每类都带：

- `facility_id`
- `state`
- `owner_team`
- `progress`
- `active_targets`

### 5. 裁判系统

建议只保留“判罚结果”和“状态变化”两类下行：

- 超功率
- 超热量
- 死亡
- 复活
- 补弹药
- 补能量
- 等级变化

## 现有工程里建议保留的策略

- 房间与比赛分离
- 权威端只认一个仿真源
- 输入可以延迟，位姿必须带序号
- 快照必须可重放、可校验
- 裁判判罚必须走可靠通道

## 5v5 带宽预算

当前代码把协议常量、消息分层和带宽估算收口在：

- `src/Simulator.ThreeD/LanProtocol.cs`
- `LanProtocolMessageTypes`
- `LanProtocolMetadata`
- `LanBandwidthBudget`

按完整 5v5 估算：

```text
玩家输入：10 人 * 56B * 60Hz = 33.6KB/s 汇入主机
机器人位姿：10 台 * 36B * 20Hz = 7.2KB/s / 客户端
活跃弹丸：30 发 * 24B * 20Hz = 14.4KB/s / 客户端
设施/裁判/可靠事件：约 1.7KB/s / 客户端
推荐预算：约 26.7KB/s / 客户端，主机下行约 267KB/s = 2.1Mbps
```

上面是二进制/定点打包目标。当前 JSON/TCP 链路需要按 `3x` 左右预留：

```text
主机下行建议至少 6.5Mbps，留余量按 10Mbps 设计。
每个客户端上下行建议至少 1Mbps，实际局域网百兆/千兆足够。
```

优化顺序：

1. 输入帧保持高频小包，只发按键、轴、鼠标增量、功能位。
2. 机器人位姿改为固定点块，位置、速度、yaw、云台 yaw/pitch 用定点量化。
3. 弹丸按 `spawn/update/despawn` 拆流，不做每帧全量满包。
4. 设施状态只在变化或低频心跳时发，观测 HUD 不反向写仿真。
5. JSON 仅保留调试和早期兼容链路，正式对战链路再切二进制帧。

## 一句话目标

把联机做成：

`房间控制` + `高频同步` + `权威仿真` + `裁判回调` + `只读观测`

而不是把所有东西都塞进一条杂糅的消息流里。
