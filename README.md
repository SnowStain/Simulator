# Simulator

这是一个以 RoboMaster 对局为目标的本地仿真工程，当前同时维护两套彼此隔离的地图体系：

- `blankCanvas`：老的粗略地图/规则实验场
- `rmuc2026`：新的精细地形地图，基于 `glb + json + lz4 cache` 组织

当前重点能力包括：

- 3D 对局与观察者模式
- 新地形地图加载、组合体加载、互动组件运行时控制
- 视觉自瞄、装甲板/圆盘位姿解算、统一控制链路
- 地形碰撞、机器人运动、弹丸碰撞与交互反馈
- C# / OpenTK 编辑器与 `Simulator.LoadLargeTerrain` 地图侧工具链

## 入口

- 启动 3D 对局：
  - `dotnet run --project src/Simulator.ThreeD/Simulator.ThreeD.csproj -- --start-match`
- 启动 C# 工程：
  - `.venv\Scripts\python.exe open_csharp_project.py`
- 启动 Python 地图查看/编辑辅助脚本：
  - `.venv\Scripts\python.exe run_viewer.py`
  - `.venv\Scripts\python.exe py_client\terrain_editor.py`

## 目录

- `src/Simulator.Core/`
  - 规则层、世界状态、弹道与自瞄数学、碰撞与实体模型
- `src/Simulator.ThreeD/`
  - 对局渲染、HUD、局内编辑、GPU/CPU 渲染桥接
- `src/Simulator.LoadLargeTerrain/`
  - 新地图读取、组合体/互动组件处理、编辑器底层能力
- `src/Simulator.Editors/`
  - 编辑器共享控件与公共逻辑
- `maps/`
  - 地图配置、注释、缓存、GLB 资源
- `docs/`
  - 架构与关键算法文档

## 推荐阅读

- [文档总览](docs/README.md)
- [给 C# 初学者的完整项目教学](docs/tutorials/csharp-beginner-project-guide.md)
- [地图处理与缓存链路](docs/algorithms/map-processing.md)
- [碰撞、运动与地形贴合](docs/algorithms/terrain-motion.md)
- [视觉自瞄、吊射与统一控制链路](docs/algorithms/autoaim.md)
- [能量机关渲染与交互](docs/algorithms/energy-mechanism.md)
- [组合体控制与互动组件运行时](docs/algorithms/interactive-composites.md)
- [弹丸与模型碰撞](docs/algorithms/projectile-collision.md)
- [新旧地图体系隔离](docs/map-system-isolation.md)
- [项目日志](docs/project-log.md)
- [文档维护工作流](docs/documentation-workflow.md)

其中如果要继续修改：

- 自瞄 / F8 / 自动扳机 / 提前量 / 英雄吊射
  - 先读 `docs/algorithms/autoaim.md`
  - 该文档已经补齐目标建模、Kalman 观测滤波、角速度估计、弹道求解、提前量联立迭代、命中修正和自动扳机判定的数学推导与工程解释

## 当前实现约束

- 新地图与老地图设施链路必须隔离：
  - 新地图设施只从新地图的组合体/互动组件/注释读取
  - 老地图规则设施不能混入 `rmuc2026`
- 结构目标、自瞄、渲染必须共用同一套世界位姿：
  - 不能出现“肉眼看到一个位置、自瞄锁另一个位置”
- 组合体运行时位姿要以 world-space 规范化结果为准：
  - 地图编辑器、单位测试器、正式对局必须一致

## 文档维护要求

- 每次功能更新后，至少同步更新：
  - `docs/project-log.md`
  - 对应主题的技术文档
  - 如果入口、用法、键位变化，还要更新 `README.md`
- 详细约束见：
  - `docs/documentation-workflow.md`

## 构建

- 常规构建：
  - `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj`
- 当主程序占用默认输出目录时，使用隔离输出目录：
  - `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug -p:OutDir=e:\Artinx\260111new\Simulator\build_verify\local\ -p:UseSharedCompilation=false`

## 本次更新关注点

- 自瞄新增独立三阶 EKF 位姿观测链路，旧的常速度 Kalman 链路保留
- `F3` 的碰撞体积和局部地形碰撞调试在 GPU 模式下改为 GPU 端绘制
- 能量机关待激活提示改为圆盘第 4 / 第 7 环常亮，不再使用十字灯条
- 前哨站血量归零后改为阻尼停转，并把阵营色组件压成黑色
- 前哨站的旋转角解算统一到规则层，渲染、自瞄、运行时目标同步复用
