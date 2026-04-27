# 优化日志摘要

## 2026-04-27 收敛版

### 性能
- 主菜单卡顿主要来自背景视频解码、HUD 全屏位图上传和同步日志 IO；已改为视频帧复用、GPU HUD 分层节流、运行日志异步批量写入。
- 局内 120FPS 目标主要靠三项收敛：活跃局内帧泵减少粗粒度 sleep、GPU overlay 降低重复上传、吊射副视角避免整图二次重绘。
- AI 慢帧主要来自锁定目标后的重复候选扫描；已加入短窗口自瞄解算复用、目标残留和卡死脱离逻辑。

### 吊射 / 自瞄
- 英雄吊射统一走结构目标预测链路：旋转前哨站/基地板使用未来板位姿，停止转动后按普通装甲板处理。
- 吊射副视角保留主副画面，小窗跟踪锁定板；部署态仍显示必要 UI、事件流和调试层。
- 自动扳机使用“弹道穿过预测板面 + 法线角度 + 中心容差 + 屏幕准星容差”的组合窗口；本轮放宽容差，提高实际出手机会。
- 第一人称相机、云台和枪管方向同源：相机始终沿枪管轴线观察，不再单独斜看锁定点。

### 地形 / 车体
- 所有可移动机器人落地支持整车缓冲，最大压缩 `4cm`；步兵起跳蓄力下沉 `2cm`，平衡步兵蓄力时长 `0.08s`。
- 轮组和后腿 IK 按地形支撑面修正，减少高地、坡面和台阶边缘的穿模/乱模。

### 规则 / UI
- 1v1 使用独立单人地图，敌方哨兵固定进攻、全自动、无限弹药；结算显示每局和总计数据。
- 哨兵可在补给区回血买弹，也可在基地区买弹；梯形/高地防御按受到伤害 `x0.50`。
- 局内事件流中文化，并移除命中率/概率字段。

### 验证口径
- 功能修改后至少构建：
  - `dotnet build src\Simulator.Core\Simulator.Core.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.Core`
  - `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`
  - `dotnet build src\Simulator.LoadLargeTerrain\LoadLargeTerrain.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false`
