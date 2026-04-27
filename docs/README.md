# 文档总览

## 先读什么

如果要快速进入代码，建议按下面顺序读：

1. [架构总览](architecture/README.md)
2. [给 C# 初学者的完整项目教学](tutorials/csharp-beginner-project-guide.md)
3. [地图处理与缓存链路](algorithms/map-processing.md)
4. [碰撞、运动与地形贴合](algorithms/terrain-motion.md)
5. [视觉自瞄、吊射与统一控制链路](algorithms/autoaim.md)
6. [组合体控制与互动组件运行时](algorithms/interactive-composites.md)
7. [能量机关渲染与交互](algorithms/energy-mechanism.md)
8. [弹丸与模型碰撞](algorithms/projectile-collision.md)
9. [经验、等级与左下角 HUD](algorithms/experience-hud.md)
10. [项目日志](project-log.md)
11. [文档维护工作流](documentation-workflow.md)

## 文档目标

这些文档不只是概念说明，而是用于回答下面四类问题：

- 地图资源是如何从 GLB/JSON 进入运行时的
- 机器人、地形、弹丸的碰撞是如何计算的
- F8 视觉自瞄是如何从观测值解算到控制输入的
- 能量机关、前哨站、基地等组合体是如何和互动组件一起运行的
- 后续每次功能更新时，哪些文档必须同步修改

如果你现在要改自瞄、吊射、自动扳机、提前量或能量机关目标建模，优先读：

- [视觉自瞄、吊射与统一控制链路](algorithms/autoaim.md)
  - 含目标建模、常速度 Kalman 与三阶 EKF 观测滤波、角速度估计、弹道方程、提前量联立迭代、自动扳机和命中修正的完整推导与源码对应

如果你是第一次看这个仓库，优先读：

- [给 C# 初学者的完整项目教学](tutorials/csharp-beginner-project-guide.md)
  - 用“仓库结构 -> 运行时主循环 -> 地图 -> 碰撞 -> 渲染 -> 自瞄 -> 编辑器”的顺序，带你建立整体心智模型

## 术语

- `组合体`
  - 地图中的一组可整体移动/旋转的模型
- `互动组件`
  - 组合体内部可被命中、可发光、可参与规则判定的子部件
- `world-space 规范化`
  - 将地图资源侧坐标转换为仿真世界统一坐标系后的结果
- `运行时目标`
  - 由当前模型位姿实时导出的装甲板/圆盘目标数据，供渲染、自瞄、命中判定复用
