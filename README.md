# ARTINX A-Soul Simulator

面向 RoboMaster / RMUC 2026 规则的本地与局域网对战模拟器。当前主线使用 `rmuc2026` 精细地形地图，地图、设施、碰撞、能量机关、自瞄、弹丸、HUD 和 LAN 同步都应围绕同一套世界坐标与运行时管线推进。

## 快速启动

```powershell
dotnet run --project src\Simulator.ThreeD\Simulator.ThreeD.csproj -- --start-match
```

常用验证命令：

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore
robocopy "src\Simulator.ThreeD\bin\Debug\net10.0-windows" "build_verify\launcher_builds\debug\threeD" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
```

## 目录职责

- `src/Simulator.Core/`：规则状态、血量、增益、能量机关、弹丸、命中判定和世界实体模型。
- `src/Simulator.ThreeD/`：OpenGK/OpenTK 对局窗口、HUD、P/O 面板、本地房间、LAN 同步、GPU/CPU 渲染桥接和运行时输入。
- `src/Simulator.LoadLargeTerrain/`：GLB 精细地图加载、组件标注导入导出、碰撞体积、地图编辑器底层能力。
- `src/Simulator.Assets/`：地图、规则、外观配置读取。
- `src/Simulator.Editors/`：编辑器共享控件与工具逻辑。
- `maps/rmuc2026/`：RMUC 2026 地图、设施、增益、碰撞标注和分片组件标注。
- `规则/`：比赛规则图片参考，代码实现优先对齐这里的确定规则。
- `docs/`：长期技术文档、需求总结和项目记录。

## 管线约束

新增功能不要另起临时链路，优先接入现有管线：

- 渲染模式与帧率计划统一走 `src/Simulator.ThreeD/Simulator3dForm.Pipeline.cs`。
- 地图组件、设施、buff、碰撞体积统一走 `ComponentAnnotationImporter/Exporter` 与 `FineTerrainAnnotationDocument`。
- 地图运行时碰撞统一进入 `RuntimeGridData`、`TerrainMotionService` 和 `ProjectileObstacleResolver`。
- 精细地形视觉统一走 `FineTerrainVisualCache`、`Simulator3dForm.FineTerrainActors.cs` 和 GPU/CPU 共享绘制路径。
- 机器人外观与碰撞优先复用 `RobotAppearanceProfile` 和外观编辑器输出，不再手写一套不一致的盒子。
- HUD、P/O 面板、局内交互统一在 `Simulator3dForm` 现有输入和 overlay 管线中处理，隐藏面板必须同步禁用鼠标命中区。

## 当前规则重点

- 房间座位是机器人生成的唯一来源，未加入座位的机器人不能进入 `World.Entities`、小地图、HUD、弹丸、碰撞和网络快照。
- 本地模式使用局内 `O` 面板，功能对齐 LAN 裁判端 `P` 面板；准备、自检、倒计时期间也允许打开。
- 基地血量低于 2000 后外板必须展开；精细地图外板按本地模型坐标相对初始位置移动，动态渲染还提供粗模展开兜底。
- 能量机关命中、自瞄和灯盘显示必须使用同一组盘面世界坐标；小能量机关待激活只亮盘面标志，激活后才亮灯臂。
- 弹丸命中能量机关时优先处理盘面命中；基地、前哨站、能量机关等精密结构命中后不做二次反弹进模型内部。
- 可通过地形先判定垂直落差，再判定水平通过性；麦轮最高爬升高度为 25cm。
- LAN 主机为权威模拟端，客户端发送输入和配置，接收权威快照并做本地视觉预测。

## 地图标注

`maps/rmuc2026/RMUC2026_MAP.component_roles.json` 是组件标注入口。组件数量较大时会拆分到：

```text
maps/rmuc2026/RMUC2026_MAP.component_roles.parts/components_*.json
```

运行时加载器必须支持 `ComponentFiles` 分片，否则能量机关、前哨站、基地装甲板等标注会退回旧坐标或粗略坐标，导致自瞄和显示错位。

## 开发提示

- 先定位归属模块，再窄幅修改。
- 优先使用 `rg` 搜索。
- 修改规则或渲染后至少构建 `Simulator.ThreeD.csproj`。
- 不提交 `bin/`、`obj/`、launcher 输出和个人配置，除非明确是发布产物。
