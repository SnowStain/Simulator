# RM ARTINX A-Soul 模拟器

RM ARTINX A-Soul 模拟器是一个 RoboMaster 风格的 3D 对局模拟器与编辑器工具集。项目重点包括 C# 3D 对局运行时、Python 地形与外观编辑器、规则配置、设施与地形建模、弹丸与反弹、AI 决策、功率控制、能量机关交互以及对局日志调试。

## 项目状态

- 主要对局端：C# WinForms + OpenGL GPU 渲染。
- 主要编辑器：`py_client/` 下的 Python `pygame-ce` 地形编辑器与外观编辑器。
- 启动入口：仓库根目录的 `open_csharp_project.py`。
- 数据资源：`maps/`、`appearance_presets/`、`rules/`、`behavior_presets/` 等目录中的 JSON 配置。
- 目标平台：Windows。

## 环境准备

需要安装：

- Windows 10 或更新版本。
- .NET SDK 9.0 或更新版本。
- Python 3.12 或 3.13。
- 支持 OpenGL 的显卡驱动。
- 可选：Visual Studio 2022，用于打开和编辑 C# 解决方案。

创建 Python 虚拟环境并安装依赖：

```powershell
py -3.12 -m venv .venv
.venv\Scripts\python.exe -m pip install -U pip
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

## 快速启动

启动主入口：

```powershell
.venv\Scripts\python.exe open_csharp_project.py
```

启动器提供以下选项：

- C# 模拟器。
- C# 地形编辑器。
- C# 外观编辑器。
- 打开 C# 解决方案。
- Python 模拟器或编辑器入口。

也可以直接启动 C# 3D 对局：

```powershell
dotnet run --project src\Simulator.ThreeD\Simulator.ThreeD.csproj -- --start-match
```

## Python 编辑器

运行地形编辑器：

```powershell
.venv\Scripts\python.exe py_client\terrain_editor.py
```

运行外观编辑器：

```powershell
.venv\Scripts\python.exe py_client\appearance_editor.py
```

Python 端现在主要负责启动器、地形编辑和外观编辑。正式对局运行时以 C# 3D 模拟器为主。

## 功能层级

### 对局运行层

- 支持第一人称、第三人称和指挥模式。
- 指挥模式可选择单位并指定攻击目标。
- 地形、设施、单位、弹丸、弹道轨迹和 F4 弹道预测尽量走 GPU 渲染。
- HUD、事件提示、暂停菜单和编辑式控制界面保留 GDI 覆盖层。
- 运行日志写入运行目录下的 `logs/`，便于检查帧率、发弹、命中和事件。

### 仿真规则层

- 支持底盘运动、云台瞄准、弹丸发射、弹丸反弹、装甲板命中和伤害事件。
- 支持底盘功率上限、缓冲能量、超级电容、超功率断电和功率曲线显示。
- 支持热量、弹药、补给、无敌、死亡复活、虚弱状态、枪管锁定和补给区解锁。
- 支持装甲板命中检测、能量机关圆盘命中检测和场地增益。

### AI 层

- 按英雄、工程、步兵、哨兵、基地、前哨站、能量机关等角色组织行为。
- 导航和决策刷新应避免阻塞主渲染循环。
- AI 可前往目标点、前哨站、基地、补给区和战术点。

### 渲染层

- 地形使用暴露面和地形面片优化渲染。
- 动态单位、设施、弹丸、能量机关和狗洞设施尽量合并到 GPU 批处理路径。
- F4 弹道预测使用 GPU 绘制，并在预测命中面绘制命中圈。
- 机器人世界血条使用 GPU 3D 绘制，受深度测试遮挡，不穿透墙体或模型显示。
- 顶部 HUD 显示对局时间、金币、基地血量、前哨站血量、机器人血量、编号和剩余弹药。

### 地图层

- 地图主要位于 `maps/<preset>/map.json`。
- 旧预设可能位于 `map_presets/*.json`。
- 地图数据包含设施、地形栅格、功能栅格、运行时栅格、地形表面和可选导出资源。
- 地形编辑器使用原子写入保存 JSON，降低强制退出导致地图文件损坏的风险。

### 设施层

- 设施包括基地、前哨站、补给区、能量机关、墙体、斜坡、起伏路段、狗洞和自定义区域。
- 狗洞是无下梁的框洞结构，由左立柱、右立柱和上梁组成。
- 公路侧狗洞参数：
- 朝向：大地坐标系 `90°`。
- 底面：高于地面 `0.20 m`。
- 净宽：`0.80 m`。
- 净高：`0.25 m`。
- 深度：`0.25 m`。
- 侧柱厚度：`0.065 m`。
- 上梁厚度：`0.115 m`。
- 红方和蓝方公路侧狗洞都可在地形编辑器设施列表中放置。

### 外观层

- 外观预设位于 `appearance_presets/`。
- 角色参数文件位于 `appearance_presets/profiles/`。
- 外观编辑器支持机器人车体、轮腿、装甲板、云台、枪管、基地、前哨站、能量机关和科技单元参数。
- 目标是让 Python 编辑器预览、C# 编辑器预览和 C# 局内模型保持所见即所得。

### 规则层

- 规则资源位于 `规则/` 目录以及相关 JSON 配置文件。
- 能量机关时序、激活顺序、环数计分、增益、伤害、复活和场地 buff 尽量由规则配置驱动。
- 事件日志和伤害日志用于排查规则不一致、异常伤害和命中判定问题。

## 常用按键

- `WASD`：移动当前控制机器人。
- 鼠标移动：第一人称或第三人称下控制云台和视角。
- 鼠标左键：开火。
- 鼠标右键：自瞄或锁定。
- `Q`：切换自瞄目标类型。
- 按住 `C`：启用超级电容加速。
- `F`：按规则激活能量机关。
- `F4`：显示预测弹道和命中圈。
- `F5`：显示键位帮助。
- `F7`：显示功率遥测。
- `H`：切换指挥模式。
- `ESC`：暂停、释放鼠标并显示暂停操作。

## 目录说明

- `src/Simulator.Core/`：核心规则、世界状态、战斗数学、弹丸和 AI 数据结构。
- `src/Simulator.ThreeD/`：C# 3D 对局、GPU 渲染、WinForms 界面和 C# 编辑器。
- `src/Simulator.Assets/`：资源加载与共享资产。
- `src/Simulator.Editors/`：C# 编辑器相关支持。
- `py_client/`：Python 启动器和编辑器代码。
- `maps/`：地图预设与地形数据。
- `appearance_presets/`：外观预设和角色参数。
- `rules/` 与 `规则/`：规则配置和参考资料。

## 开发建议

- 修改对局逻辑后先运行 `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj`。
- 修改 Python 编辑器后先运行 `python -m py_compile` 检查语法。
- 不要手动编辑 `bin/`、`obj/`、`build_verify/` 中的生成文件。
- 地图和外观 JSON 建议通过编辑器保存，避免格式错误。
- 如果出现异常命中、异常伤害或卡顿，优先查看运行目录下的日志文件。
