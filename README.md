# ARTINX A-Soul Simulator

面向 RoboMaster / RMUC 2026 规则的本地与局域网对战模拟器。当前主线围绕 `rmuc2026` 精细地形地图推进，地图、设施、碰撞、Buff、能量机关、自瞄、弹丸、HUD、房间和 LAN 同步都应接入同一套运行时管线。

## 快速启动

```powershell
dotnet run --project src\Simulator.ThreeD\Simulator.ThreeD.csproj -- --start-match
```

常用验证命令：

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore
robocopy "src\Simulator.ThreeD\bin\Debug\net10.0-windows" "build_verify\launcher_builds\debug\threeD" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
```

`robocopy` 返回码 `0` 到 `7` 都视为同步成功。

## 目录职责

- `src/Simulator.Core/`：规则状态、血量、Buff、能量机关、弹丸、命中判定和世界实体模型。
- `src/Simulator.ThreeD/`：OpenGK/OpenTK 对局窗口、HUD、O/P 裁判面板、本地房间、LAN 同步、GPU/CPU 渲染桥接和运行时输入。
- `src/Simulator.LoadLargeTerrain/`：GLB 精细地图加载、组件标注导入导出、碰撞体积和地图编辑器底层能力。
- `src/Simulator.Assets/`：地图、规则、外观配置读取。
- `src/Simulator.Editors/`：编辑器共享模型、服务和工具逻辑。
- `maps/rmuc2026/`：RMUC 2026 地图、设施、Buff、碰撞体积和分片组件标注。
- `规则/`：比赛规则图片参考，代码实现优先对齐这里的确定规则。
- `docs/`：长期技术文档、需求总结和项目记录。

## 当前运行规则

- 房间座位是机器人生成的唯一来源；未加入座位的机器人不能进入 `World.Entities`、小地图、HUD、弹丸、碰撞和网络快照。
- 本地模式使用局内 `O` 裁判窗口，LAN 裁判端使用 `P` 裁判窗口；准备阶段、裁判系统自检和倒计时阶段都允许呼出。
- 本地模式按 `Enter` 可以跳过准备阶段、裁判系统自检和 5 秒倒计时；LAN 模式不能用本地按键跳过权威流程。
- O/P 裁判窗口是独立工具窗，能量机关控制在“能量机关”分页中；局内 HUD 只保留必要状态，避免裁判窗口影响渲染。
- 第三人称和第一人称都默认捕获鼠标控制视角；只有按住 `Alt` 时释放鼠标。
- 基地上端装甲板只能使用地图组合体中已标注的组件，不允许程序临时生成替代装甲板。
- 能量机关待命中标识、灯臂、中段箭头和命中常亮应使用精细地图组件坐标；待命中圆心锚定 10 环组件中心。
- AI 寻路不得穿过地图设施类型为 `base` 或 `outpost` 的区域；路线扫描发现障碍后应重新规划。
- AI 战略目标优先级为：己方建筑被打时追击伤害源，否则优先上高地压前哨站，敌方前哨站死亡后进攻敌方基地。
- Buff 编辑器独立于地图编辑器，从主界面“编辑器 -> Buff 编辑器”进入，在俯视图框选区域后写入地图设施。
- 可通过地形先判定垂直落差，再判定水平通过性；麦轮最高爬升高度默认约为 25cm。
- LAN 主机是权威模拟端，客户端发送输入和配置，接收权威快照并做本地视觉预测。

## 管线约束

新增内容优先接入现有管线，避免临时旁路：

- 渲染模式与帧率计时统一走 `src/Simulator.ThreeD/Simulator3dForm.Pipeline.cs`。
- 地图组件、设施、Buff、碰撞体积统一走 `ComponentAnnotationImporter/Exporter` 与 `FineTerrainAnnotationDocument`。
- 地图运行时碰撞统一进入 `RuntimeGridData`、`TerrainMotionService` 和 `ProjectileObstacleResolver`。
- 精细地形视觉统一走 `FineTerrainVisualCache`、`Simulator3dForm.FineTerrainActors.cs` 和 GPU/CPU 共享绘制路径。
- 机器人外观与碰撞优先复用 `RobotAppearanceProfile`、外观编辑器输出和 `EntityCollisionModel`。
- HUD、O/P 裁判窗口、局内交互统一在 `Simulator3dForm` 现有输入与 overlay 管线处理。

## 地图标注

`maps/rmuc2026/RMUC2026_MAP.component_roles.json` 是组件标注入口。组件数量较大时会拆分到：

```text
maps/rmuc2026/RMUC2026_MAP.component_roles.parts/components_*.json
```

运行时加载器必须支持 `ComponentFiles` 分片，否则能量机关、前哨站、基地装甲板等标注会退回旧坐标或粗略坐标，导致自瞄和显示错位。

## 开发提示

- 先定位归属模块，再窄幅修改。
- 搜索优先使用 `rg`。
- 修改规则、AI、碰撞或渲染后至少构建 `Simulator.ThreeD.csproj`。
- 不提交 `bin/`、`obj/`、launcher 输出、WebView2 缓存和个人配置，除非明确作为发布产物。
- 构建后若要给 launcher 使用，再同步到 `build_verify\launcher_builds\debug\threeD`。
