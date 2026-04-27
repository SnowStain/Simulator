# 自瞄 EKF 教程

这份文档说明当前项目的自瞄算法层如何组织，以及三阶 EKF 位姿滤波为什么这样写。它面向后续继续改自瞄、吊射、能量机关和旋转装甲板的人。

## 代码入口

当前自瞄算法层已经从运动控制服务里单独拆出：

- `src/Simulator.ThreeD/AutoAimSolverService.cs`：自瞄算法编排入口，负责选择旧常速度 Kalman 或三阶 EKF，并调用弹道求解。
- `src/Simulator.ThreeD/AutoAimObservedState.cs`：观测滤波后的统一输出，包含位置、速度、加速度和角速度。
- `src/Simulator.ThreeD/AutoAimObservationFilterState.cs`：旧常速度 Kalman 链路，保留用于对照和回退。
- `src/Simulator.ThreeD/AutoAimThirdOrderEkfPoseFilterState.cs`：三阶 EKF 位姿滤波器。
- `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`：目标建模、弹道、提前量、命中点求解。
- `src/Simulator.ThreeD/TerrainMotionService.cs`：现在只负责调用自瞄服务，并把 `AutoAimSolution` 输出到云台/实体状态。

核心调用顺序：

```text
TerrainMotionService
  -> AutoAimSolverService.ResolveObservationState(...)
  -> AutoAimSolverService.ComputeSolution(...)
  -> SimulationCombatMath.ComputeObservationDrivenAutoAimSolutionThirdOrderEkf(...)
  -> TerrainMotionService.ApplyAutoAimSolution(...)
```

## 自瞄分层

自瞄链路分成五层：

1. 目标层：把机器人、前哨站、基地、能量机关统一建模成 `ArmorPlateTarget`。
2. 可见性层：判断某块板是否能被当前射手看到。
3. 观测滤波层：把当前帧目标板位置变成稳定的位置、速度、加速度估计。
4. 弹道求解层：用观测状态和弹丸模型计算 yaw、pitch、lead time 和 aim point。
5. 输出控制层：把解算结果平滑、限速、写入云台和实体自瞄状态。

三阶 EKF 属于第 3 层。它不直接控制云台，也不直接判断是否开火。它只回答一个问题：目标当前可信位姿和短期运动趋势是什么。

## 观测量和单位

输入观测来自当前帧可见的 `ArmorPlateTarget`：

```text
z_world = (plate.X, plate.Y, plate.HeightM)
```

平面坐标 `X/Y` 使用 world-space，高度使用米。进入物理量计算时，平面坐标要乘：

```text
metersPerWorldUnit
```

所以：

```text
x_m = x_world * metersPerWorldUnit
y_m = y_world * metersPerWorldUnit
z_m = HeightM
```

这是整个自瞄里最容易出错的地方。平面是 world 单位，高度是米，不能直接混算。

## 旧 Kalman 链路

旧链路在 `AutoAimObservationFilterState` 中，每个轴都是常速度模型：

```text
state = [p, v]
```

预测：

```text
p' = p + v * dt
v' = v
```

这个模型适合普通机器人短时间平移，但对旋转装甲板、能量机关和加速度变化明显的目标会滞后。旧链路保留用于对照、回退，以及低动态目标的稳定解算。

## 三阶 EKF 状态

三阶 EKF 的平面状态是：

```text
x = [px, py, vx, vy, ax, ay]
```

高度状态是：

```text
z = [h, vh, ah]
```

平面状态使用 world-space 存位置、速度和加速度，公开输出时再换算成 m/s 和 m/s^2。高度状态全程使用米。

## 预测模型

三阶模型假设短时间内加速度近似连续：

```text
p(t + dt) = p + v * dt + 0.5 * a * dt^2
v(t + dt) = v + a * dt
a(t + dt) = a
```

矩阵形式：

```text
[p']   [1 dt 0.5dt^2] [p]
[v'] = [0 1  dt     ] [v]
[a']   [0 0  1      ] [a]
```

过程噪声来自 jerk，也就是加速度变化率。代码里通过 `WriteThirdOrderProcessNoiseBlock(...)` 把 jerk variance 写入协方差矩阵：

```text
Q(p,p) += dt^5 / 20 * q
Q(p,v) += dt^4 / 8  * q
Q(p,a) += dt^3 / 6  * q
Q(v,v) += dt^3 / 3  * q
Q(v,a) += dt^2 / 2  * q
Q(a,a) += dt       * q
```

目标越容易剧烈变化，jerk 噪声越大。当前策略是：能量机关最高，前哨站旋转板较高，小陀螺目标较高，普通目标较低。

## 为什么是 EKF

如果只观测 `x/y`，线性 Kalman 就够。但当前平面校正使用的是相对射手的极坐标观测：

```text
range = sqrt((px - sx)^2 + (py - sy)^2)
bearing = atan2(py - sy, px - sx)
```

这个观测函数是非线性的，所以需要 EKF 在当前预测点附近线性化。

雅可比矩阵核心项：

```text
d range / d px = dx / range
d range / d py = dy / range
d bearing / d px = -dy / range^2
d bearing / d py =  dx / range^2
```

代码位置：`AutoAimThirdOrderEkfPoseFilterState.CorrectPlanar(...)`。

## 观测噪声

测量噪声用米描述，再根据距离转换成 bearing 噪声：

```text
bearingStdRad = measurementNoiseM / measuredRangeM
```

近距离时 bearing 误差影响大，远距离时角度更稳定。代码会 clamp，避免近距离无限放大。

当前测量噪声大致是：

- 旋转目标：`0.010m`
- 普通目标：`0.016m`

这不是物理真值，而是工程调参值。它表达的是“我们多信任当前帧观测”。

## 初始化和重置

滤波器第一次看到目标时：

1. 如果没有上一帧测量，速度初始化为 `0`。
2. 如果有上一帧测量，用两帧差分估计初速度。
3. 加速度初始化为 `0`。
4. 协方差给较大的速度/加速度不确定性。

观测 key 是：

```text
targetId:aimPlateId:observationPlateId
```

只要目标或板切换，或者超过约 `0.30s` 没更新，就重建滤波器。

## 角速度估计

旋转目标需要角速度。当前算法从过滤后的平面位置和速度反推：

```text
r = p_plate - p_target_center
omega = cross(r, v) / |r|^2
```

二维展开：

```text
omega = (rx * vy - ry * vx) / (rx^2 + ry^2)
```

能量机关、前哨站、前哨站顶部等结构目标允许更小角速度通过。普通车体目标如果角速度很小，会被压成 `0`，避免噪声导致假旋转。

## 输出状态

`AutoAimObservedState` 是观测滤波层唯一输出：

```csharp
internal readonly record struct AutoAimObservedState(
    double AimPointXWorld,
    double AimPointYWorld,
    double AimPointHeightM,
    double VelocityXMps,
    double VelocityYMps,
    double VelocityZMps,
    double AccelerationXMps2,
    double AccelerationYMps2,
    double AccelerationZMps2,
    double AngularVelocityRadPerSec);
```

注意：

- `AimPointXWorld/YWorld` 仍是 world-space。
- `Velocity*` 和 `Acceleration*` 已经是米制。
- `AngularVelocityRadPerSec` 是弧度每秒。

## 弹道提前量

EKF 不直接算最终 pitch。它把状态交给 `SimulationCombatMath.ComputeObservationDrivenAutoAimSolutionThirdOrderEkf(...)`。

弹道求解会考虑：

- 弹丸速度。
- 重力下坠。
- 射手自身速度继承。
- 目标速度。
- 目标加速度。
- 旋转目标角速度。
- 不同目标类型的补偿 profile。

输出是 `AutoAimSolution`，同一份结果驱动 HUD/F8 预测点、云台 yaw/pitch、自动扳机窗口和发弹角度。

## 能量机关特别规则

能量机关不是直接打观测点：

- 观测点倾向使用 1 环，因为它稳定。
- 命中点使用对应的 10 环，因为它是最终目标。
- 未来圆盘位置优先从运行时观测和 EKF 角速度积分得到。
- 缺少运行时观测时，才回退到解析规则模型。

这样避免“视觉圆盘在一处，自瞄点按另一套理想相位跑”的错位。

## 前哨站和基地

前哨站中部装甲板会旋转，死亡后阻尼停转。基地顶部装甲板会沿轨道往复运动。

这些结构目标的几何位置仍由 `SimulationCombatMath.GetAttackableArmorPlateTargets(...)` 给出，EKF 只负责稳定观测和短期运动趋势。不要把结构几何硬编码进 EKF。

## 调参顺序

如果自瞄抖动：

1. 先确认目标板 `ArmorPlateTarget` 是否稳定。
2. 再看 `AutoAimObservedState` 的速度/加速度是否异常。
3. 再调 `measurementNoiseM`。
4. 再调 `jerkNoiseMps3`。
5. 最后才调输出层平滑和限速。

如果自瞄滞后：

1. 看 `leadTime` 是否合理。
2. 看 EKF 速度方向是否正确。
3. 看加速度是否被压得太低。
4. 看目标是否被错误当成静态目标复用缓存。
5. 看弹道补偿 profile 是否过度保守。

## 常见错误

- 把 world-space 坐标当米使用。
- 高度和 X/Y 使用不同时间戳。
- 观测板和命中板混用。
- 切板后没有重置滤波器。
- 对旋转目标只用线性平移预测。
- 在 UI 绘制阶段重新计算自瞄，而不是复用 `AutoAimSolution`。
- 在输出层修抖动，却掩盖观测层错误。

## 后续优化建议

- 给 `AutoAimSolverService` 增加独立单元测试，覆盖静止、匀速、加速、圆周运动四类输入。
- 把测量噪声和 jerk 噪声提到配置或规则文件中。
- 把 `AutoAimSolution` 的缓存策略继续从 `TerrainMotionService` 拆到自瞄服务。
- 为 F8 增加观测点、滤波点、最终命中点三层显示，便于定位问题。
