# 碰撞、运动与地形贴合

## 问题范围

地形运动链路需要同时解决：

- 上下坡与上下台阶
- 机器人底盘与轮组贴地
- 不穿模
- 不被不存在的碰撞体阻挡
- 下台阶时不在边角卡死

这是整个工程里最容易因为“视觉正常、碰撞错误”而出问题的部分。

## 地形数据来源

对于 `rmuc2026` 这类精细地形，碰撞不应再依赖老的 CPU 栅格高度图。

新链路的目标是：

- 渲染使用三角面
- 碰撞也使用同一套三角面空间数据
- 通过空间索引降低候选面数量

这样渲染与碰撞至少共享同一套几何基础。

## 机器人运动更新

每帧机器人运动大致分成：

1. 根据输入得到期望平面速度
2. 预测下一个位置
3. 进行地形与实体碰撞检测
4. 对合法位移进行修正
5. 计算底盘最终高度、pitch、roll

关键点是“先判定，再修正”，而不是硬把车体推进地形里之后再回退。

## 台阶跨越

跨越高度不是统一常数，而是受车型约束：

- 常规底盘
- 平衡步兵
- 英雄/工程/哨兵

不同车型的最大可跨越高度不同，规则层通过实体参数控制。

当台阶高度在允许范围内时，应执行平滑上台阶，而不是瞬移或抖动。

## 轮组贴地与底盘姿态

底盘姿态构造遵循以下原则：

- 先确定可用支撑点
- 尽量让轮组贴合地面
- 用 3 个稳定支撑点解出底盘平面
- 剩余轮子跟随底盘约束

这样即使四轮无法完全共面，也能避免底盘上下闪动。

## 不穿模原则

为避免穿模，碰撞链路采用两层约束：

- 位移前检测趋势
- 位移后再次验证实体体积是否侵入地形

如果预测到侵入，则直接约束位移，不让实体进入非法位置。

当前策略已经去掉了过强的反弹项，重点改为“平滑阻止穿透”，而不是撞一下再弹回。

## F3 调试

`F3` 必须展示与实际参与碰撞计算一致的碰撞体积。

否则排查时会出现：

- 屏幕上显示一个碰撞体
- 实际阻挡来自另一套隐藏体积

这会直接让问题不可定位。

## 2026-04-25：真实碰撞体积外扩 1cm

当前约定是：`F3` 看到的碰撞体不是视觉模型本体，而是真正参与物理判定的安全碰撞体。为了避免浮点误差、模型缝隙和高速位移导致的轻微穿透，碰撞体积在生成阶段统一向外扩 `1cm`。

实体碰撞盒的处理方式是：

```text
length = length + 0.02
width  = width  + 0.02
height = height + 0.02
minHeight = minHeight - 0.01
```

精细地形三角面的处理方式是按三角面法线外扩：

```text
p' = p + n * 0.01m
```

其中平面 `x/z` 方向需要先按 `metersPerWorldUnit` 换算回 world-space，高度 `y` 方向直接使用米。

## 2026-04-25：精细设施只取最外层碰撞

基地、前哨站等模型可能包含大量内部装饰面。如果所有内部三角面都参与运动碰撞，机器人一旦穿进精细组件内部，就会同时接触很多方向互相矛盾的面，表现为卡顿、抖动或被异常推出。

当前策略是：

- 普通地形仍保留三角面碰撞。
- 三角数较高、尺寸像局部设施零件的组件，使用最外层 AABB 壳体作为碰撞体。
- 内部装饰面不参与机器人运动碰撞。

这不是降低视觉精度，而是把“视觉细节”和“运动可解的碰撞代理”分开。真实目标是稳定阻挡外轮廓，而不是让底盘和内部装饰面做高频碰撞。

## 2026-04-25：F3 同时显示 Buff/Debuff 区域

`F3` 现在不仅显示碰撞体积，也会在 GPU 调试几何中绘制地图上的 `buff/debuff` 区域。这样排查问题时可以同时看到：

- 机器人真实碰撞体
- 地形/设施碰撞面
- 增益和减益区域边界

如果后续出现“进入区域没有触发”或“碰撞箱与区域错位”，应优先打开 `F3` 对比这三类几何，而不是只看视觉贴图。

## 2026-04-26：碰撞坐标只跟随视觉地形的同源比例尺

如果视觉模型位置正确，但 `F3` 碰撞体整体偏移，不能移动视觉模型。正确做法是让碰撞层复用视觉地形的同一份模型中心和比例尺。

当前 `TerrainCacheCollisionSurface` 的优先级是：

1. 优先读取 `RuntimeReferenceScene.WorldScale`。
2. 使用其中的 `ModelCenter`、`XMetersPerUnit`、`YMetersPerUnit`、`ZMetersPerUnit` 和模型最低高度。
3. 只有加载失败时，才回退到 terrain cache catalog bounds 推导。

这样 GPU 可见地形和 F3 碰撞三角面会消费同一个模型坐标基准。

同时，外层 AABB 壳体代理只允许用于细长且不可行走的精细零件。斜坡和大面积台面必须保留原始三角面，否则 AABB 会把斜面包成一个方块，表现为“碰撞箱和实际模型有偏移”。

## 关键文件

- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`
- `src/Simulator.Core/Gameplay/SimulationModels.cs`
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`

## 后续方向

- 四叉树/分块候选面筛选继续前移到更早阶段
- 机器人 GPU 碰撞体与调试显示进一步统一
- 对复杂轮腿机器人继续强化“贴地解姿态”而不是只做包围体移动

## 2026-04-26：碰撞体坐标中心修正

地形碰撞体的显示和真实阻挡都必须服务于同一个目标：碰撞体贴合当前可见地图，但不能通过移动视觉地图来掩盖问题。

本轮修正规则如下：

```text
originXWorld = fieldLengthM / metersPerWorldUnit / 2
originZWorld = fieldWidthM  / metersPerWorldUnit / 2

worldX = originXWorld - (modelX - modelCenterX) * xMetersPerModelUnit / metersPerWorldUnit
worldZ = originZWorld - (modelZ - modelCenterZ) * zMetersPerModelUnit / metersPerWorldUnit
height = max(0, modelY - modelMinY) * yMetersPerModelUnit
```

其中 `modelCenter / modelMinY / metersPerModelUnit` 优先来自地图 annotation 的 `WorldScale`。只有 annotation 不存在或加载失败时，才回退到 terrain cache runtime scene 或 catalog bounds。

这样做的原因是：地图像素尺寸和真实场地尺寸不一定严格等比。例如 `map.height` 对应的像素范围经过平均 `metersPerWorldUnit` 换算后，可能不等于 `fieldWidthM`。如果碰撞层继续用 `preset.Height * 0.5` 做中心，而 GPU 视觉地形用 `fieldWidthM * 0.5` 做中心，就会出现固定方向的碰撞偏移。

`F3` 中的 buff/debuff 区域只用于调试，不等同于地形碰撞体。为了避免误判，区域显示使用低透明度填充和轮廓线；真实碰撞体仍以地形三角面或外层 AABB 壳体显示。
