# ARTINX A-Soul Simulator

面向 RoboMaster / RMUC 2026 规则的本地与局域网对战模拟器。当前主线围绕 `rmuc2026` 精细地形地图推进，地图、设施、碰撞、增益、能量机关、自瞄、弹丸、HUD、房间和 LAN 同步都应接入同一套运行时管线。

## 快速启动

```powershell
dotnet run --project src\Simulator.ThreeD\Simulator.ThreeD.csproj -- --start-match
```

Linux 当前没有 `Microsoft.WindowsDesktop.App`，不能直接运行 `net10.0-windows` 的 ThreeD 主入口。需要在 Linux 上打开操作手视角时使用原生 OpenTK 入口：

```bash
dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026
```

默认会先进入本地操作手席位登陆房间，可点击席位或按 `1/2/3/4/7` 选择英雄、工程、步兵或哨兵，`Q/E` 切换红蓝方，方向键切换出生点；按 `Enter` 后只生成已登陆席位对应的机器人，直接从该出生点切入车内第一视角并进入 5 秒倒计时，倒计时结束后进入比赛。常用操作：`M` 捕获/释放鼠标视角，`WASD` 移动，左键开火，右键/`Tab` 自瞄，`F` 启动能量机关测试，`Alt` 或窗口失焦释放鼠标，`Esc` 退出。需要跳过登陆房间并直接从操作手第一视角进入 5 秒倒计时可加 `--start-match`。

大能量机关旋转检测可直接启动到 180 秒后的大符状态，诊断日志写入 `/tmp/simulator_energy_rotation.log`：

```bash
dotnet run --project src/Simulator.LinuxOperator/Simulator.LinuxOperator.csproj -- --map rmuc2026 --large-energy-test
```

常用验证命令：

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore
robocopy "src\Simulator.ThreeD\bin\Debug\net10.0-windows" "build_verify\launcher_builds\debug\threeD" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
```

`robocopy` 返回码 `0` 或 `1` 都视为同步成功。

## 目录职责

- `src/Simulator.Core/`：规则状态、血量、增益、能量机关、弹丸、命中判定和世界实体模型。
- `src/Simulator.ThreeD/`：OpenGK/OpenTK 对局窗口、HUD、P/O 面板、本地房间、LAN 同步、GPU/CPU 渲染桥接和运行时输入。
- `src/Simulator.LinuxOperator/`：Linux 原生 OpenTK 操作手窗口，复用 `Simulator.Core`/`Simulator.Assets` 的规则、地图和世界状态，作为不能运行 Windows Desktop Runtime 时的本机可视化入口。
- `src/Simulator.LoadLargeTerrain/`：GLB 精细地图加载、组件标注导入导出、碰撞体积和地图编辑器底层能力。
- `src/Simulator.Assets/`：地图、规则、外观配置读取。
- `src/Simulator.Editors/`：编辑器共享控件与工具逻辑。
- `maps/rmuc2026/`：RMUC 2026 地图、设施、增益、碰撞体积和分片组件标注。
- `规则/`：比赛规则图片参考，代码实现优先对齐这里的确定规则。
- `docs/`：长期技术文档、需求总结和项目记录。

## 当前运行规则

- 房间座位是机器人生成的唯一来源；未加入座位的机器人不能进入 `World.Entities`、小地图、HUD、弹丸、碰撞和网络快照。
- 本地模式使用局内 `O` 面板，LAN 裁判端使用 `P` 面板；准备阶段、裁判系统自检和倒计时阶段都允许呼出。
- 本地模式按 `Enter` 可以跳过准备阶段、裁判系统自检和 5 秒倒计时；LAN 模式不能用本地按键跳过权威流程。
- P/O 面板关闭时必须禁用隐藏按钮命中区；打开面板时只保留必要 HUD 和面板绘制，降低背景视野掉帧。
- Linux 操作手入口默认不捕获鼠标，按 `M` 后才捕获视角；按 `Alt` 或切到其他应用会释放并清空操作输入，避免影响其他窗口。需要旧行为时可加 `--lock-mouse`。
- 基地外板只能使用地图组合体中已标注的三块外板组件，不允许程序临时生成替代外板；开板来源包括血量低于 2000、敌方堡垒占领 20 秒、O/P 面板手动打开。
- 能量机关待命中标识、灯臂、灯臂中段箭头和命中常亮都使用同一套精细地图互动组件坐标；待命中圆心应锚定 10 环组件中心。
- 能量机关裁判控制单独放在 P/O 面板的“能量机关”页，可分别设置红/蓝、小/大能量机关的待激活灯臂、已命中灯臂、激活数量和完全激活。
- 可通过地形先判定垂直落差，再判定水平通过性；麦轮最高爬升高度为 25cm。
- LAN 主机是权威模拟端，客户端发送输入和配置，接收权威快照并做本地视觉预测。

## 管线约束

新增内容优先接入现有管线，避免临时旁路：

- 渲染模式与帧率计时统一走 `src/Simulator.ThreeD/Simulator3dForm.Pipeline.cs`。
- 地图组件、设施、buff、碰撞体积统一走 `ComponentAnnotationImporter/Exporter` 与 `FineTerrainAnnotationDocument`。
- 地图运行时碰撞统一进入 `RuntimeGridData`、`TerrainMotionService` 和 `ProjectileObstacleResolver`。
- 精细地形视觉统一走 `FineTerrainVisualCache`、`Simulator3dForm.FineTerrainActors.cs` 和 GPU/CPU 共享绘制路径。
- 机器人外观与碰撞优先复用 `RobotAppearanceProfile` 和外观编辑器输出。
- HUD、P/O 面板、局内交互统一在 `Simulator3dForm` 现有输入与 overlay 管线处理。

## 地图标注

`maps/rmuc2026/RMUC2026_MAP.component_roles.json` 是组件标注入口。组件数量较大时会拆分到：

```text
maps/rmuc2026/RMUC2026_MAP.component_roles.parts/components_*.json
```

运行时加载器必须支持 `ComponentFiles` 分片，否则能量机关、前哨站、基地装甲板等标注会退回旧坐标或粗略坐标，导致自瞄和显示错位。

## 开发提示

- 先定位归属模块，再窄幅修改。
- 搜索优先使用 `rg`。
- 修改规则或渲染后至少构建 `Simulator.ThreeD.csproj`。
- 不提交 `bin/`、`obj/`、launcher 输出和个人配置，除非明确作为发布产物。
