# 文档总览

## 先读什么

如果要快速进入代码，建议按下面顺序读：

1. [架构总览](architecture/README.md)
   - [局域网架构](architecture/lan-network-architecture.md)
2. [给 C# 初学者的完整项目教程](tutorials/csharp-beginner-project-guide.md)
3. [地图处理与缓存链路](algorithms/map-processing.md)
4. [碰撞、运动与地形贴合](algorithms/terrain-motion.md)
5. [视觉自瞄、吊射与统一控制链路](algorithms/autoaim.md)
6. [自瞄 EKF 教程](algorithms/autoaim-ekf-tutorial.md)
7. [组合体控制与互动组件运行时](algorithms/interactive-composites.md)
8. [能量机关渲染与交互](algorithms/energy-mechanism.md)
9. [弹丸与模型碰撞](algorithms/projectile-collision.md)
10. [经验、等级与左下角 HUD](algorithms/experience-hud.md)
11. [Linux / OpenTK 迁移 README](linux-opentk-port-readme.md)
    - 新 Linux 入口在 `src/Simulator.Linux`，运行：
      `dotnet run --project src/Simulator.Linux/Simulator.Linux.csproj -- --map rmuc2026 --size 1440x900`
    - 平台输入抽象在 `src/Simulator.Platform/Input`，Linux 入口不引用 Windows shell。
12. [项目日志](project-log.md)
13. [文档维护工作流](documentation-workflow.md)

## 文档目标

这些文档不只是概念说明，而是用来回答下面几类问题：

- 地图资源如何从 GLB/JSON 进入运行时。
- 机器人、地形、弹丸的碰撞如何计算。
- F8 视觉自瞄如何从观测值解算到控制输入。
- 能量机关、前哨站、基地等组合体如何和互动组件一起运行。
- 每次功能更新后，哪些文档必须同步修改。

## 当前项目结构

Linux 迁移时按下面的层级看项目：

| 路径 | 定位 | Linux 状态 |
| --- | --- | --- |
| `src/Simulator.Platform` | 跨平台输入和平台契约，包含 `GameInputSnapshot` | Linux 入口直接依赖 |
| `src/Simulator.Core` | 规则、实体、战斗、弹丸、增益、能量机关状态 | Linux 入口直接依赖 |
| `src/Simulator.Assets` | 配置、地图 preset、外观和资源加载 | Linux 入口直接依赖 |
| `src/Simulator.Linux` | 新 OpenTK-only Linux 操作端入口 | 当前 Linux 主入口 |
| `src/Simulator.Runtime` | CLI 和未来 runtime 抽取暂存区，目前仍是 Exe | 暂不让 Linux 入口依赖 |
| `src/Simulator.ThreeD` | Windows 兼容主程序，现有 UI/渲染/LAN 功能源 | 只能抽取代码，Linux 不直接引用 |
| `src/Simulator.LoadLargeTerrain` | 地图/地形编辑器入口 | 仍是 Windows/editor 工具 |
| `scripts/linux` | Linux 迁移验证和启动脚本 | Linux/交接必跑 |

原项目不能直接在 Linux 上完整运行的核心原因是主入口 `Simulator.ThreeD` 使用
`net10.0-windows`、WinForms、Windows OpenCV runtime，并且大量 OpenGK UI、LAN
面板、渲染和编辑器调用还在 Windows shell 内。当前 `linux` 分支已经把 Linux
入口收紧为：

```text
Simulator.Linux -> Simulator.Platform / Simulator.Core / Simulator.Assets
```

Linux 迁移继续推进时，应从 `Simulator.ThreeD` 抽取平台无关的状态、布局、渲染数据和规则，
不要把 `Simulator.ThreeD` 作为 Linux 项目引用。

如果现在要改自瞄、吊射、自动扳机、提前量或能量机关目标建模，优先读：

- [视觉自瞄、吊射与统一控制链路](algorithms/autoaim.md)
  - 包含目标建模、常速度 Kalman、三阶 EKF 观测滤波、角速度估计、弹道方程、提前量联立迭代、自动扳机和命中修正的源码对应。
- [自瞄 EKF 教程](algorithms/autoaim-ekf-tutorial.md)
  - 更聚焦解释自瞄算法层封装、三阶 EKF 状态、预测/校正模型、噪声调参和常见问题。

如果是第一次看这个仓库，优先读：

- [给 C# 初学者的完整项目教程](tutorials/csharp-beginner-project-guide.md)
  - 按“仓库结构 -> 运行时主循环 -> 地图 -> 碰撞 -> 渲染 -> 自瞄 -> 编辑器”的顺序建立整体模型。

## 术语

- `组合体`
  - 地图中的一组可整体移动或旋转的模型。
- `互动组件`
  - 组合体内部可被命中、可发光、可参与规则判定的子部件。
- `world-space 规范化`
  - 将地图资源侧坐标转换为仿真世界统一坐标系后的结果。
- `运行时目标`
  - 由当前模型位姿实时导出的装甲板、圆盘等目标数据，供渲染、自瞄、命中判定复用。
