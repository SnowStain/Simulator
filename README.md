# ARTINX A-Soul Simulator

面向 RoboMaster / RMUC 2026 规则的本地与局域网对战模拟器。当前主线是 `rmuc2026` 精细地形地图，使用 `glb + component_roles.json + terraincache.lz4` 作为地图资源，规则、渲染、自瞄、碰撞和局域网同步都围绕同一套世界坐标推进。

## 快速启动

```powershell
dotnet run --project src\Simulator.ThreeD\Simulator.ThreeD.csproj -- --start-match
```

常用验证命令：

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore
robocopy "src\Simulator.ThreeD\bin\Debug\net10.0-windows" "build_verify\launcher_builds\debug\threeD" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
```

如果主程序或编辑器占用输出文件，可先用：

```powershell
dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore /p:BuildProjectReferences=false
```

## 目录职责

- `src/Simulator.Core/`：规则状态、血量/增益/能量机关、弹丸、命中判定、世界实体模型。
- `src/Simulator.ThreeD/`：OpenGK/OpenTK 对局窗口、HUD、P/O 面板、本地房间、局域网同步、GPU/CPU 渲染桥接。
- `src/Simulator.LoadLargeTerrain/`：GLB 精细地图加载、组件标注、碰撞体积、地图编辑器底层能力。
- `src/Simulator.Assets/`：地图、规则和资源配置读取。
- `src/Simulator.Editors/`：编辑器共享控件与工具逻辑。
- `maps/rmuc2026/`：RMUC 2026 地图、设施、增益、碰撞标注和分片组件标注。
- `规则/`：按比赛规则整理的图片参考，代码实现应优先对齐这里的确定规则。
- `docs/`：长期技术文档和项目记录。

## 当前框架约定

- 房间座位是机器人生成的唯一来源：未加入座位的机器人不能只隐藏，必须不进入对局实体集合。
- 本地模式使用局内 `O` 面板，功能对齐联机裁判 `P` 面板；联机裁判仍使用 `P`。
- 局内准备、自检、倒计时期间应允许打开配置/裁判面板。
- 地图编辑器中的 collision/buff/设施体积必须通过统一标注链路进入运行时碰撞、触发和调试显示。
- 自瞄、命中判定、GPU/CPU 可视化必须共用同一组装甲板/能量机关世界坐标，不能出现显示和锁定错位。
- 局域网主机是权威模拟端；客户端发送输入和模式配置，接收权威快照并做本地视角预测。

## RMUC 规则重点

- 能量机关：
  - 小能量机关每次点亮 1 个灯盘。
  - 大能量机关每轮点亮 2 个灯盘，命中任意一个后进入 1 秒补击窗口，随后进入下一轮。
  - 大能量机关按 5 轮显示 `1/5` 到 `5/5` 进度，待激活盘应显示规则图 5-21 的多环红色灯盘效果。
- 基地、前哨站、堡垒、增益区、补给区等规则以 `规则/` 下图片和 `maps/rmuc2026/facilities/*.json` 为准。
- 性能体系和车辆构型配置在准备阶段完成，局内配置改动需要按比例调整当前血量。

## 地图与标注

`maps/rmuc2026/RMUC2026_MAP.component_roles.json` 是组件标注入口。组件数量较大时会拆分到：

```text
maps/rmuc2026/RMUC2026_MAP.component_roles.parts/components_*.json
```

运行时加载器必须支持 `ComponentFiles` 分片，否则能量机关、前哨站、基地装甲板等标注会退回旧坐标或粗略坐标，导致自瞄和显示错位。

## 开发提示

- 优先改现有链路，不新增平行系统。
- 优先使用 `rg` 搜索归属模块。
- 修改规则后至少构建 `Simulator.ThreeD.csproj`。
- 不提交 `bin/`、`obj/`、launcher 输出和个人配置，除非明确需要发布构建产物。
