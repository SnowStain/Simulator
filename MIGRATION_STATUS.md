# Simulator C# 迁移状态

## 迁移目标
- 新项目路径：`E:/Artinx/260111new/Simulator`
- 目标：在新 C# 解决方案下承载 3D/局内模拟器相关资产与编辑入口，并保持可运行、可编辑、可扩展。

## C# 解决方案结构
- `Simulator.sln`
- `src/Simulator.Core`
  - 项目布局发现与配置读写（`ProjectLayout`, `ConfigurationService`）
- `src/Simulator.Assets`
  - 资产完整性目录扫描、地图预设枚举、规则文件枚举（`AssetCatalogService`）
- `src/Simulator.Editors`
  - 外观编辑服务（`AppearanceEditorService`）
  - 场地预设编辑服务（`TerrainEditorService`）
- `src/Simulator.Runtime`
  - 可执行 CLI 入口：状态检查、地图切换、外观编辑、规则清单

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
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- appearance show`：通过
- `dotnet run --project src/Simulator.Runtime/Simulator.Runtime.csproj -- terrain set basicMap`：通过

## 新项目可用命令
- `status`
- `terrain list`
- `terrain set <preset>`
- `appearance show`
- `appearance set <topLevelKey> <jsonLiteral>`
- `rules list`

## 说明
- 本次迁移已完成“完整资产搬迁 + C# 可运行编辑入口 + 结构完整性检查”。
- 原 Python/C++ 目录已同步迁入新项目根目录，便于继续做 C# 深度替换与功能对齐。
