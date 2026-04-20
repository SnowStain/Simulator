# Simulator C# 迁移状态

## 迁移目标
- 新项目路径：`E:/Artinx/260111new/Simulator`
- 目标：在新 C# 解决方案下承载 3D/局内模拟器相关资产与编辑入口，并保持可运行、可编辑、可扩展。

## C# 解决方案结构
- `Simulator.sln`
- `src/Simulator.Core`
  - 项目布局发现与配置读写（`ProjectLayout`, `ConfigurationService`）
  - 规则模型与加载校验（`RuleSet`, `RuleSetLoader`）
  - 场地设施模型与仿真状态（`FacilityRegion`, `SimulationWorldState`）
  - 场地交互引擎与规则仿真（`ArenaInteractionService`, `RuleSimulationService`）
- `src/Simulator.Assets`
  - 资产完整性目录扫描、地图预设枚举、规则文件枚举（`AssetCatalogService`）
  - 地图预设解析与点位设施查询（`MapPresetService`）
- `src/Simulator.Editors`
  - 外观编辑服务（`AppearanceEditorService`）
  - 场地预设编辑服务（`TerrainEditorService`）
  - 规则路径写入编辑服务（`RuleEditorService`）
- `src/Simulator.Runtime`
  - 可执行 CLI 入口：状态检查、地图切换、外观编辑、规则系统、场地交互探测、规则仿真
- `src/Simulator.ThreeD`
  - C# 原生 3D 可执行入口（WinForms）：主菜单/大厅/对局三态流程、地图与后端切换、队伍与实体选择、相机交互、逐帧规则仿真驱动

## 已迁移目录与文件
- 编辑器与配置：
  - `appearance_editor.py`
  - `terrain_editor.py`
  - `CommonSetting.json`
  - `config.json`
  - `settings.json`
- 规则与地图资产：
  - `rules/`
  - `规则/`
  - `map/`
  - `map_presets/`
  - `maps/`
  - `appearance_presets/`
  - `robot_venue_map_asset/`
- 3D 运行链与原生参考：
  - `simulator3d/`
  - `Engine/`
  - `cpp/`

## 已完成验证
- `dotnet build Simulator.sln -c Release`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- status`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- terrain list`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- rules list`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- rules validate`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- arena probe 120 700`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- simulate run 20 0.2`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- appearance show`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- terrain set basicMap`：通过
- `dotnet run --project src/Simulator.ThreeD/Simulator.ThreeD.csproj -- --preset basicMap --backend moderngl`：启动通过（窗口持续运行）
- `dotnet run --project src/Simulator.ThreeD/Simulator.ThreeD.csproj -- --preset basicMap --backend moderngl --start-match`：启动通过（窗口持续运行）

## 新项目可用命令
- `status`
- `terrain list`
- `terrain set <preset>`
- `appearance show`
- `appearance set <topLevelKey> <jsonLiteral>`
- `rules list`
- `rules show`
- `rules validate`
- `rules set <path> <jsonLiteral>`
- `arena probe <x> <y> [preset]`
- `simulate run [durationSec] [dtSec] [preset]`
- `dotnet run --project src/Simulator.ThreeD/Simulator.ThreeD.csproj -- [--preset <name>] [--backend opengl|moderngl|native_cpp] [--dt <sec>] [--team red|blue] [--entity <id>] [--ricochet on|off] [--start-match]`

## 说明
- 本次迁移已完成“完整资产搬迁 + C# 可运行编辑入口 + 规则引擎 + 场地交互体系 + 仿真验证 + C# 3D 首版可运行入口”。
- 原 Python/C++ 目录已同步迁入新项目根目录，便于继续做 C# 深度替换与功能对齐。
