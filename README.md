# RoboMaster 2026 哨兵行为模拟器

这是一个面向 RoboMaster 2026 场景推演的 2D 俯视角模拟器，覆盖红蓝双方基地、前哨站、英雄、工程、步兵、哨兵，以及地图设施、移动、射击、补给、复活、采矿兑矿、能量机关、地形增益、雷达标记和 AI 行为树决策。

当前版本的重点不是“静态规则演示”，而是“可运行对局 + 可视化 AI 决策 + 可编辑地图/规则 + 可保存复盘”。

## 快速启动

### C# 3D 预览运行（新增）

如果你要直接运行 C# 版本 3D 程序，不依赖 Python 入口：

```powershell
dotnet run --project src/Simulator.ThreeD/Simulator.ThreeD.csproj -- --preset basicMap --backend moderngl
```

可选参数：

- `--preset <地图名>`：指定地图预设（例如 `basicMap`）
- `--backend opengl|moderngl|native_cpp`：设置 3D 后端模式标签
- `--dt <秒>`：设置每帧仿真步长（默认 `0.2`）
- `--team red|blue`：预设大厅默认队伍
- `--entity <entityId>`：预设大厅默认主控实体（例如 `red_robot_1`）
- `--ricochet on|off`：预设弹丸反弹开关
- `--start-match`：跳过菜单与大厅，直接进入对局

窗口内操作：

- 主菜单：选择后端与地图，点击 `Enter Lobby`
- 大厅：选择队伍与主控实体，点击 `Start Match`
- 对局：`RMB` 旋转相机、`MMB` 平移、滚轮缩放
- 对局：`TAB` 切换跟随实体，`F` 开关跟随
- 对局：`SPACE` 暂停/继续，`N` 单步，`R` 重置
- 对局：`PgUp/PgDn` 切图，`1/2/3` 切后端标签，`L` 返回大厅

### Windows

1. 创建虚拟环境

```powershell
py -3.13 -m venv .venv
```

1. 安装依赖

```powershell
.venv\Scripts\python.exe -m pip install -U pip
.venv\Scripts\python.exe -m pip install -r requirements.txt
```

1. 启动项目大厅

```powershell
.venv\Scripts\python.exe project_hall.py
```

大厅中可直接选择：

- 2D 模拟器
- 3D 模拟器
- 行为编辑器
- 外观编辑器
- 功能编辑器
- 伤害机制测试

1. 启动独立 3D ModernGL 对局程序

```powershell
.venv\Scripts\python.exe -m simulator3d
```

1. 可选：构建 C++ 原生渲染/Bullet 桥接层

```powershell
.venv\Scripts\python.exe -m pip install pybind11
build_native_3d.bat
```

如果你已经有一份可用的 `build/native` Conan/CMake 产物，也可以单独重配/重编 `cpp/` 兼容模块：

```powershell
C:/Program Files/CMake/bin/cmake.exe -S cpp -B cpp/.tmp_build -G "Visual Studio 17 2022" -A x64 -DCMAKE_TOOLCHAIN_FILE=build/native/conan_toolchain.cmake -DCMAKE_PREFIX_PATH=build/native -DPython_EXECUTABLE=.venv/Scripts/python.exe
C:/Program Files/CMake/bin/cmake.exe --build cpp/.tmp_build --config Release
```

默认产物位于 `cpp/.tmp_build/Release/rm26_native.cp312-win_amd64.pyd`。

1. 可选：打包独立 3D 项目

```powershell
package_3d_simulator.bat
```

1. 启动独立地形/预设编辑器

```powershell
.venv\Scripts\python.exe terrain_editor.py
```

1. 启动独立伤害机制测试工具

```powershell
.venv\Scripts\python.exe sentry_curve_analyzer.py
```

这个入口会启动一个本地 Flask 服务，并自动在浏览器中打开伤害机制测试页，用来对比：

- 英雄 / 步兵 / 哨兵的安全控热出伤
- 基地 / 前哨站 / 步兵 / 英雄 / 哨兵 / 工程的受击曲线
- 交互式单图曲线，可悬停查看时间点上的 DPS / 总伤害 / 热量 / 目标血量 / 累计发弹等参数
- 攻击方与受击方的规则增益 / 减益
- 对基地 / 前哨站可开启结构暴击，对比 0.5% - 30% 暴击率，当前按 1.50x 期望伤害折算
- 堡垒按 Δ = 己方基地血量上限 - 当前基地血量计算额外冷却，同类攻击 / 防御 / 易伤 / 冷却增益按规则只取最大效果
- 本地规则图片入口，包括增益说明与伤害机制截图

也可以直接双击：

- start.bat
- start_3d_simulator.bat
- start_sentry_curve_analyzer.bat
- start_terrain_editor.bat
- export_robot_venue_map_asset.bat

1. 启动公网部署版伤害机制测试工具

这个版本用于部署到云服务器、Render、Railway、Fly.io 或任意支持 Python Web 服务的平台。终端用户只需要打开网页，不需要本地代码，也不会看到规则图片或本地目录按钮。

本地验证命令：

```powershell
.venv\Scripts\python.exe serve_damage_lab_public.py
```

独立部署最小依赖：

```powershell
pip install -r requirements-web.txt
```

平台启动命令：

```powershell
python serve_damage_lab_public.py
```

说明：

- 服务会读取平台注入的 HOST / PORT 环境变量，默认监听 0.0.0.0:8000
- Procfile 已提供 `web: python serve_damage_lab_public.py`
- 公网模式默认隐藏规则图片、图片路由和本地目录打开接口
- build_native_3d.bat
- package_3d_simulator.bat

### Windows 迁移到 3.12 / 3.13

如果你当前机器已经装了多个 Python 版本，务必避免直接使用裸 `pip`，因为它很可能指向错误的解释器。

推荐直接运行仓库内脚本：

```powershell
setup_windows_env.bat 3.13
```

如果本机只有 3.12，可改为：

```powershell
setup_windows_env.bat 3.12
```

如果 `py` 启动器找不到解释器，也可以直接传入绝对路径：

```powershell
setup_windows_env.bat C:\Users\kylin\AppData\Local\Programs\Python\Python312\python.exe
```

脚本会自动：

- 删除旧 `.venv`
- 用指定的 3.12/3.13 解释器重建 `.venv`
- 用 `.venv\Scripts\python.exe -m pip` 安装全部依赖

安装完成后，统一使用下面这些命令启动，不要再用系统全局 `python` 或 `pip`：

```powershell
.venv\Scripts\python.exe simulator.py
.venv\Scripts\python.exe -m simulator3d
.venv\Scripts\python.exe terrain_editor.py
```

### 依赖说明

- 项目使用 pygame-ce。
- requirements.txt 中包含 moderngl 和 glcontext；如果你的 Python 版本没有对应 wheel，优先改用 Python 3.12 或 3.13，或者安装 MSVC Build Tools。
- 原生 3D/Bullet 桥接层使用 CMake + pybind11；如果你要构建 rm26_native，需要额外安装 CMake 和 C++ 编译工具链。
- 如果你看到“当前解释器未安装 pygame 或 pygame-ce”，通常不是没装，而是装到了另一个 Python 版本里。请始终使用 `当前解释器 -m pip` 安装依赖。
- 如果 PowerShell 拒绝执行 Activate.ps1，可以直接用 .venv\Scripts\python.exe 运行，无需激活环境。

## 独立 3D 程序

3D 对局程序现在作为单独的 `simulator3d` 包维护，入口与 2D 主模拟器分离：

- 2D 主模拟器继续使用 `simulator.py`
- 3D 对局程序使用 `python -m simulator3d`
- `simulator_3d.py` 仅保留为兼容包装入口

3D 包负责：

- 优先请求 C++ 原生场景后端，功能级别不足时自动回退到 ModernGL 地形后端
- 独立的主菜单、赛前选机与局内第一/第三人称流程
- 独立的运行策略配置，例如赛前配置要求、手控时 AI 冻结范围与调度节奏
- 为原生 OpenGL/Bullet 迁移提供单独的 Python 绑定边界

当前原生迁移入口：

- `rm26_native` 的实现所有权现在在 `Engine/`
- `Engine/src/Core/NativeRuntimeShared.h` 提供共享基础类型与 pybind 数据桥
- `Engine/src/Renderer/NativeRenderer.cpp` 提供原生离屏 OpenGL 渲染
- `Engine/src/Physics/NativePhysics.cpp` 提供 Bullet heightfield 物理桥
- `Engine/src/Python/ModuleRuntime.cpp` 负责模块注册与 `build_info()`
- `cpp/` 保留兼容构建入口，但当前是直接编译上述 Engine 源文件，不再内嵌老式大 cpp 实现
- Python 绑定模块名仍为 `rm26_native`
- 一键构建脚本为 `build_native_3d.bat`
- 一键打包脚本为 `package_3d_simulator.bat`

当前已经验证通过的原生能力：

- 渲染后端：`wgl_offscreen_opengl`
- 物理后端：`bullet_heightfield_bridge`
- 渲染功能级别：`4/4`
- 物理功能级别：`4/4`

也就是说，当前 `simulator3d` 默认阈值下，原生渲染和原生 Bullet 物理都已经会被实际启用，不再只是“预留接入面”。

当前默认策略仍然是“原生优先，功能级别不足则回退”。差别在于：当前主线构建已经满足默认 `4/4` 阈值，所以独立 3D 对局会直接走原生后端；只有模块缺失、构建失败或能力回退时才会落回 ModernGL/Python 路径。

共享部分仍保留在 `core/`、`control/`、`rules/`、`entities/` 中，作为 2D/3D 共用的仿真核心。

## 3D 地图资产导出

项目现在包含独立的标准化 3D 地图资产构建脚本 [build_robot_venue_map_asset.py](build_robot_venue_map_asset.py)。

默认行为：

- 优先读取仓库根目录的 map.json
- 若根目录 map.json 不存在，则回退到当前 config/settings 激活的地图预设
- 输入可为任务书标准 schema，也可为当前项目的 map preset schema

命令行示例：

```powershell
.venv\Scripts\python.exe build_robot_venue_map_asset.py
.venv\Scripts\python.exe build_robot_venue_map_asset.py --input maps/basicMap/map.json
.venv\Scripts\python.exe build_robot_venue_map_asset.py --input map_presets/basicMap.json --output custom_asset_dir
.venv\Scripts\python.exe build_robot_venue_map_asset.py --help
```

一键入口：

- 双击 [export_robot_venue_map_asset.bat](export_robot_venue_map_asset.bat)
- 在地形编辑器中点击“导出3D资产”
- 在地形编辑器中按 Ctrl+E

独立示例文件：

- [examples/navigation_obstacle_query_example.py](examples/navigation_obstacle_query_example.py)
- [examples/incremental_facility_rebuild_example.py](examples/incremental_facility_rebuild_example.py)

## 当前覆盖的核心功能

### 1. 对局与单位系统

- 红蓝双方完整对局循环。
- 单位类型包括：英雄、工程、步兵、哨兵、基地、前哨站。
- 基地和前哨站保持静态，不参与碰撞漂移。
- 被击毁的机器人和哨兵不会立刻消失，会继续显示复活进度与状态。
- 每个单位都有独立的血量、热量、功率、弹药、姿态/模式、目标、AI 决策和移动状态。

### 2. 地图、设施与地形

- 地图采用俯视图世界坐标，支持设施区域和地形栅格。
- 地图运行时底层已细化为 `0.05m` 精度栅格，并拆分为独立二维通道连续存储。
- 当前运行时通道至少包括：`height_map`、`terrain_type_map`、`movement_block_map`、`vision_block_map`、`vision_block_height_map`、`function_pass_map`、`function_heading_map`、`priority_map`。
- 全地图运行时通道通过 NumPy `.npy` 二进制文件保存和加载，当前默认地图预设为 `basicMap`。
- 设施可包含基地、前哨站、补给区、能量机关、采矿区、兑矿区、飞坡、台阶、狗洞、边界等。
- MapManager 支持设施查询、区域命中、路径评估、最近可通点吸附和地图预设加载。
- 台阶翻越已结合高度变化与边缘检测，不再只依赖手工设施标记。
- 目标点若落在不可通区域，AI 会自动吸附到最近合法点，而不是盲目前往障碍中心。

### 3. 运动、碰撞与导航

- 实体运动由 PhysicsEngine 和 AIController 联动驱动。
- enable_entity_movement=true 时，机器人和哨兵都会真实移动。
- 运动会考虑碰撞半径、墙体/边界阻挡、地形通行、台阶过渡与非法回退点修复。
- AI 导航会在可直达时直接走直线，不能直达时再进行路径搜索。
- 路径搜索默认直接基于 `movement_block_map` 可通行矩阵执行运行时 A*，邻域访问走 NumPy 通道采样。
- `find_path()` 会先尝试直达段短路，失败后再落到 0.05m runtime 栅格 A*，并复用路径缓存降低重复搜索开销。
- 所有兵种都会显示程序设定的轨迹：地图上可看到导航线、预览路径、当前小目标点和最终目标。

### 5. 视场角遮挡与视野检测

- 视野检测从观测点出发，按给定朝向、视场角和最大距离生成可视扇区。
- 底层实现使用 Bresenham 射线算法逐射线遍历 runtime 栅格，并用 `vision_block_map` 判断障碍遮挡。
- `compute_fov_visibility()` 会返回被障碍裁切后的可视多边形，必要时也可返回整张 `visible_mask` 布尔矩阵。
- `is_vision_line_clear()` 提供单条视线检测接口，供规则判定和渲染共用同一套遮挡逻辑。
- 当前渲染层显示的 FOV 区域已经直接复用这套 runtime 视野计算结果，而不是固定半径假扇形。

### 4. 分段路径与小目标点

这是当前版本的优先实现项，已经落地到真实逻辑与 UI。

- AI 不再只把“最终目标区域中心”当成唯一移动目标。
- 当前移动决策会把最终目标和当前位置之间的路径切分为多个小目标点。
- 机器人会先前往下一个小目标点，再按顺序抵达最终目标区域。
- Entity 中会记录：
  - 最终导航目标
  - 当前 movement target
  - 当前 navigation waypoint
  - 剩余 navigation subgoals
  - path preview
- 单兵详情面板会直接显示当前小目标点和剩余前 3 个分段小目标。

## 规则系统

### 1. 射击、热量与自瞄

- RulesEngine 负责统一射击规则、弹药校验、热量增长、冷却、锁枪和命中判定。
- 支持 17mm 和 42mm 两类弹药。
- 支持自动瞄准、视场角限制、最远距离限制、运动惩罚、旋转惩罚和距离衰减。
- 自瞄默认最大距离为 8 米。
- 视线判定会结合地形高度估计和枪口/装甲板高度，不是简单二维直线判定。
- 热量超限可进入冷却解锁或本局锁死等状态。

### 2. 功率与控制模式

- 底盘模式支持血量优先、功率优先。
- 云台模式支持冷却优先、爆发优先。
- 这些模式会影响功率保留、射速倍率、热量阈值和输出节奏。
- 单兵详情面板支持直接切换这些模式。

### 3. 补给、经济与兑换

- 双方都有金币系统与持续经济收入。
- 补给逻辑由 RulesEngine 管理，支持不同弹种的补给量。
- 支持本地兑换和远程兑换。
- 工程支持采矿、携矿、回家兑矿、累计金矿收益统计。
- 哨兵支持弹药兑换、血量兑换、远程支援和立即复活等请求型能力。

### 4. 复活与状态窗口

- 复活机制已经按规则图重构为完整流程。
- 普通复活包含读条、原地复活、无效态、虚弱态等阶段。
- 立即复活会提供满血和短暂无敌。
- 复活后的枪管锁定恢复也已经纳入规则判断。

### 5. 增益、机关与雷达

- 地形增益和堡垒增益会真实影响单位状态。
- 能量机关支持激活与状态机快照。
- 雷达系统支持标记累计、衰减和易伤倍率。
- HUD 和详情数据会显示当前增益与关键状态。

## 英雄吊射与弹药约束

这条规则现在已经在 AI 层和规则层双重生效。

- 英雄必须在有弹药的前提下，才能吊射和打弹。
- 这里的“有弹药”不是 UI 提示，而是实际决策前置条件。
- 具体来说：
  - 英雄普通射击前会检查可用弹药。
  - 英雄吊射前会检查可用弹药。
  - 英雄部署吊射资格本身也会检查可用弹药。
- 如果英雄没有弹药：
  - 不会进入吊射相关 AI 分支。
  - 不会通过规则系统进入部署吊射开火。
  - 单兵详情面板会明确显示“无弹禁吊射”或“吊射弹药不足”。

## AI 系统

### 1. 行为树框架

- AI 基于行为树运行。
- 每个兵种都通过统一的 role_decision_specs 定义“决策 id + 中文标签 + 条件 + 动作 + fallback”。
- 行为树执行和决策评估使用同一套规格，避免“显示逻辑”和“执行逻辑”脱节。
- 已补充一份 ROS2 / BehaviorTree.CPP 风格的等价 XML 总览，见 control/behavior_trees_btcpp.xml。
- 当前 controller 读取的兵种主逻辑已拆分到 control/behavior_trees/sentry_btcpp.xml、control/behavior_trees/infantry_btcpp.xml、control/behavior_trees/hero_btcpp.xml、control/behavior_trees/engineer_btcpp.xml。
- 对局中的兵种面板仍由 control/ai_controller.py 输出 decision ids / labels / top3，但顺序与标签现在优先跟随上述拆分 XML 装载。
- 已新增独立行为编辑器 behavior_editor.py，可通过 start_behavior_editor.bat 打开；支持按兵种/决策编辑时间窗口、条件表达式，以及在地图上用矩形/圆形/多边形绘制任务区域，并保存为 behavior_presets/*.json。

### 2. 当前兵种决策序列

#### 哨兵

优先级从高到低：

1. 回补解锁
2. 复活回补
3. 紧急撤退
4. 飞坡突入
5. 推进基地
6. 发现即集火
7. 保护英雄
8. 配合步兵推进
9. 护送工程
10. 拦截敌工
11. 推进前哨站
12. 团战掩护
13. 推进基地
14. 翻越地形
15. 巡关键设施

#### 步兵

优先级从高到低：

1. 复活回补
2. 强制补给
3. 推进基地
4. 紧急撤退
5. 常规补给
6. 发现即集火
7. 激活能量机关
8. 拦截敌工
9. 抢敌方高地
10. 推进前哨站
11. 团战推进
12. 推进基地
13. 翻越地形
14. 巡关键设施

#### 英雄

优先级从高到低：

1. 复活回补
2. 强制补给
3. 推进基地
4. 常规补给
5. 英雄找掩护
6. 开局抢高地
7. 抢敌方高地
8. 发现即集火
9. 激活能量机关
10. 吊射前哨站
11. 吊射基地
12. 翻越地形
13. 推进基地
14. 巡关键设施

#### 工程

优先级从高到低：

1. 复活回补
2. 紧急撤退
3. 回家兑矿
4. 前往采矿
5. 取矿兑矿循环

### 3. 动态决策权重

当前版本不只执行行为树，还会把“所有候选决策”的评估结果实时可视化。

- 所有兵种都会记录完整的决策权重列表。
- 每项决策都包含：
  - 决策 id
  - 中文标签
  - weight
  - matched
  - priority_index
- 当前已执行动作会被强制标记为 selected。
- 单兵详情面板会显示：
  - 接下来 3 步最有可能的决策
  - 当前执行动作
  - 每个候选决策的动态权重 N 边图

### 4. 单兵详情面板的决策可视化

点击 HUD 中的单位卡片后，会打开单兵详情面板。现在面板除了基础状态外，还新增了：

- 路径状态
- 最终目标
- 当前移动目标
- 当前小目标点
- 剩余分段小目标数量
- 后续 3 个小目标点
- 当前行为摘要
- 接下来 3 步决策
- 决策权重 N 边图

N 边图说明：

- N 表示该兵种当前总决策数。
- 黄点表示当前正在执行的决策。
- 绿点表示条件命中的候选决策。
- 灰点表示未命中的候选决策。

## 可视化与交互

### 1. HUD

- 顶部 HUD 显示回合、剩余时间、双方金币、基地/前哨血量。
- 双方单位卡片显示血量、等级、存活状态和当前行为节点。
- 点击单位卡片可打开详情面板。

### 2. 地图叠加层

- 支持设施显示开关。
- 支持 AI 导航覆盖层。
- 支持实体视野/自瞄相关叠加层开关。
- 已修复“只有箭头没有路径”的旧问题，只有 path valid 时才会显示有效导航结果。

### 3. 详情面板

- 基础属性：血量、热量、功率、姿态、移速、冷却、地形增益、堡垒增益。
- 武器属性：弹药、射速、单发功率、单发热量、自瞄距离、自瞄视场。
- 英雄属性：部署状态、部署进度、部署目标、吊射命中率、吊射弹药资格。
- 工程属性：携带矿物、矿物类型、累计采矿、累计兑矿、累计金币。
- AI 属性：路径与子目标、三步决策、决策权重图。

## 编辑器、配置与持久化

### 1. 主程序内编辑能力

主窗口工具栏支持：

- 开始/重开对局
- 结束对局
- 保存存档
- 载入存档
- 保存设置
- 浏览模式
- 设施编辑
- 站位编辑
- 规则编辑

### 2. 独立地形编辑器

- terrain_editor.py 会启动独立地图编辑器。
- 这个编辑器复用渲染和地图编辑逻辑，但不初始化完整对局系统。
- 适合专门调整设施区域、地形栅格和地图预设。

### 3. 地图预设与设置文件

- config.json 提供基础配置。
- CommonSetting.json 保存本地常用参数和覆盖配置。
- map_presets 目录保存地图预设。
- 当前配置管理器支持仅在 CommonSetting 中保存 map.preset 引用，再从 map_presets 反向装配设施与 terrain_grid。

### 4. 存档与性能日志

- F5 保存当前对局。
- F9 载入对局。
- P 暂停/继续。
- perf overlay 和 perf logging 可在 CommonSetting.json 中启用。
- 运行时会把性能日志保存到 perf_logs 或 saves/perf_logs。

## 重要实现约束

- CommonSetting.json 的本地覆盖优先级高于 config.json；如果运行结果和代码默认值不一致，先检查 CommonSetting.json。
- 当前项目里很多 AI/规则调参结果都以 CommonSetting.json 为准；若该文件不存在，会兼容读取旧的 settings.json，并在下次保存时迁移到 CommonSetting.json。
- 当前 basicMap 是活动地图预设时，编辑器和主程序都会以它为起点装配场地。

## 项目结构速览

- simulator.py: 主程序入口
- terrain_editor.py: 独立地图编辑器入口
- core/: 引擎、配置与主循环
- control/: AI、行为树、手动/自动控制器
- entities/: 单位定义与实体管理
- physics/: 运动、碰撞与地形过渡
- rules/: 裁判、射击、补给、复活、经济与机关规则
- rendering/: HUD、详情面板、侧边栏、地形概览与主渲染器
- map/: 地图管理、设施、路径与栅格
- map_presets/: 地图预设
- saves/: 对局存档与性能日志
- 规则/: 规则图与中文说明素材

## 当前版本最值得关注的新增点

1. 英雄必须有弹药，才能吊射和打弹。
2. 所有兵种都能在地图上显示程序设定的轨迹。
3. 单兵详情面板现在会展示接下来 3 步决策。
4. 单兵详情面板现在会用 N 边图动态显示每个决策的权重。
5. 移动决策已经改成“最终目标区域 + 多个小目标点”的分段导航，机器人会顺序经过这些小目标点，而不是只盯着终点硬走。

如果你要继续扩展，优先建议从 control/ai_controller.py、rules/rules_engine.py、core/game_engine.py 和 rendering/renderer_detail_popup_mixin.py 这几处开始看。
