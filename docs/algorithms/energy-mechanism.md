# 能量机关渲染与交互

## 几何来源

能量机关优先使用新地图中的组合体与互动组件，不再依赖老的粗模替代。

运行时会读取：

- 组合体主体模型
- 圆盘互动组件
- 灯条互动组件
- 旋臂相关注释信息

## 运行时状态

每个阵营维护自己的能量机关状态，主要包括：

- 当前是否处于激活中
- 当前点亮了哪些圆盘
- 每个圆盘已经命中的环数
- 最近一次命中的圆盘编号与环数
- Buff 是否已经激活，以及剩余时间

## 待激活显示

本次已将旧的十字灯条待激活提示移除。

现在的待激活显示规则是：

- 仅使用圆盘自身的第 4 环和第 7 环
- 在待激活期间常亮
- 不再额外绘制十字覆盖层

这样做的目的有两个：

- 保证提示严格贴合圆盘面
- CPU/GDI 与 GPU 路径保持一致，避免两套叠加效果错位

## 命中与常亮规则

圆盘命中后，按命中的环数执行反馈：

- 最近命中的环数会按规则闪烁
- 闪烁结束后，该环保持阵营色常亮

旋臂外侧灯条按已激活圆盘数量控制进度：

- 1 个圆盘激活：亮到 20%
- 2 个圆盘激活：亮到 40%
- 3 个圆盘激活：亮到 60%
- 4 个圆盘激活：亮到 80%
- 5 个圆盘激活：亮满

当 Buff 完全激活后，机关保持激活态直到 Buff 结束。

## 渲染链路

能量机关有三条渲染相关链路：

1. 主体模型绘制
2. 互动组件着色
3. 规则高亮覆盖层

其中规则高亮覆盖层必须同时覆盖：

- GPU 路径
- CPU/GDI 路径
- FineTerrain 组合体路径

否则会出现按键切换渲染路径后状态层消失的问题。

## 与自瞄的关系

自瞄锁定的目标必须来自运行时圆盘位姿，而不是旧 CPU 粗略圆盘位置。

因此运行时会先根据组合体变换矩阵导出每个圆盘的世界坐标，再生成：

- 可视目标
- 命中检测目标
- F8 调试目标

三者共用同一份结果。

## 关键文件

- `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`
- `src/Simulator.ThreeD/Simulator3dForm.Structures.cs`
- `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`
- `src/Simulator.ThreeD/FineTerrainEnergyMechanismVisualCache.cs`
## 2026-04-25 center-mark cleanup

- The legacy vertical center marker is now treated as invalid runtime content for the new energy mechanism chain.
- During fine-terrain cache construction, any interaction unit named like `*中央*` / `*R标*` is stripped from both the composite body component set and the unit component set.
- The old CPU and GPU pending or activation marker helper methods were removed from the render path. The runtime state indicators now remain:
  - pending state: ring `4` and ring `7`
  - hit feedback: black triple flash, then steady team-colored ring
