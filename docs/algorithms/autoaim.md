# 自瞄、吊射与统一控制链路

## 1. 目标

这份文档说明当前项目里自瞄的完整实现目标：

- 视觉调试结果、蓝色 target 准星、云台控制、自动扳机、真实发弹，必须使用同一份解算结果
- 装甲板、前哨站、基地、能量机关都统一建模为可计算的目标板
- 先从观测得到稳定状态，再做未来位姿预测，再做弹道与提前量求解
- 目标短时消失时不立刻暂停，而是沿上一时刻的状态继续预测，重新出现后再校正

代码主入口：

- `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`
- `src/Simulator.ThreeD/TerrainMotionService.cs`
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- `src/Simulator.ThreeD/Simulator3dForm.LiveControl.cs`
- `src/Simulator.ThreeD/Simulator3dForm.AppearanceModel.cs`

---

## 2. 统一数据结构

所有可攻击目标最终统一成 `ArmorPlateTarget`：

```csharp
public readonly record struct ArmorPlateTarget(
    string Id,
    double X,
    double Y,
    double HeightM,
    double YawDeg,
    double SideLengthM = 0.13,
    double WidthM = 0.0,
    double HeightSpanM = 0.0,
    int EnergyRingScore = 0);
```

含义：

- `X, Y, HeightM`：目标中心点
- `YawDeg`：目标法向朝向
- `WidthM, HeightSpanM`：目标可击中尺寸
- `EnergyRingScore`：能量机关圆盘环号

统一输出结构是 `AutoAimSolution`：

```csharp
public readonly record struct AutoAimSolution(
    double YawDeg,
    double PitchDeg,
    double Accuracy,
    double DistanceCoefficient,
    double MotionCoefficient,
    double LeadTimeSec,
    double LeadDistanceM,
    string PlateDirection,
    double AimPointX = 0.0,
    double AimPointY = 0.0,
    double AimPointHeightM = 0.0,
    double ObservedVelocityXMps = 0.0,
    double ObservedVelocityYMps = 0.0,
    double ObservedVelocityZMps = 0.0,
    double ObservedAngularVelocityRadPerSec = 0.0);
```

这份结果会同时驱动：

- F8/F3/F5 调试显示
- 云台 `yaw / pitch`
- 自动扳机
- 最终发弹角度与提前量

---

## 3. 坐标与单位

平面坐标使用 `world-space`，高度直接使用米。

记：

```math
s = \text{MetersPerWorldUnit}
```

则平面位移换算为：

```math
p_{xy}^{(m)} = s \cdot p_{xy}^{(world)}
```

这意味着：

- `X / Y` 做速度、距离、预测前，要先乘 `MetersPerWorldUnit`
- `HeightM` 始终直接按米计算

如果把这两套单位混用，就会出现“画面对了，但云台/弹道/碰撞错位”的问题。

---

## 4. 目标建模

### 4.1 车体装甲板

车体装甲板由车体中心和局部偏移生成：

```math
p_{plate} = p_{body} + f \cdot d_f + r \cdot d_r + u \cdot d_u
```

其中：

- `f`：底盘前向
- `r`：底盘右向
- `u`：底盘上向
- `d_f, d_r, d_u`：装甲板局部偏移

这样做的作用是：

- 车体俯仰/横滚后，板子仍然贴合真实模型
- F8 看到的板位姿与控制解算一致

### 4.2 前哨站旋转装甲板

前哨站中部三块旋转装甲板采用统一旋转函数：

```math
\theta(t) = \theta_0 + \omega t
```

前哨站死亡后不是瞬停，而是阻尼收敛：

```math
\theta(t) = \omega t_d + \frac{\omega}{\lambda}\left(1 - e^{-\lambda (t - t_d)}\right), \quad t > t_d
```

其中：

- `t_d`：死亡时刻
- `\omega = 0.8\pi`
- `\lambda = 2.2`

现在实现里加了 settle cutoff：

- 死亡后约 `2.4s` 视为视觉上已经停稳
- 之后直接使用固定残余角，不再继续做指数衰减

这有两个作用：

- 视觉上仍然是“慢慢停下”
- 不再为已经停稳的目标持续做无意义阻尼计算

### 4.3 基地顶部装甲板

基地顶部装甲板是沿轨道往返平移：

```math
\Delta s(t) = A \sin(\omega t)
```

其中：

- `A = 0.34m`
- `\omega = 0.7\pi`

因此基地顶部目标不是静止点，而是轨道上的移动板。

### 4.4 能量机关圆盘

能量机关是“观测环”和“攻击环”分离的目标。

当前规则：

- 用同一旋臂上的 `1 环` 做视觉观测
- 用同一旋臂上的 `10 环` 做真实攻击目标

记第 `i` 个旋臂：

```math
p_{obs} = p_{arm_i,\ ring1}
```

```math
p_{aim} = p_{arm_i,\ ring10}
```

这样做的原因：

- `1 环` 更稳定，适合作为观测参考
- `10 环` 才是最终击中目标

现在代码里已经明确区分：

- 搜索时优先用 `1 环` 观测
- 发弹提前量最终落在对应 `10 环`

同时，运行时动态生成的能量机关目标只允许在“请求时刻与运行时缓存时刻一致”时复用。
如果在求未来时刻 `t + \Delta t` 的几何，就必须重新按规则状态构造未来圆盘，而不能误用当前帧缓存。

---

## 5. 目标搜索与锁定

### 5.1 搜索锥

当前自瞄搜索只在准星附近角域内进行。综合角误差定义为：

```math
e = \sqrt{\Delta \psi^2 + (w_p \Delta \phi)^2}
```

其中：

- `\Delta \psi`：yaw 误差
- `\Delta \phi`：pitch 误差
- 普通模式 `w_p = 0.70`
- 吊射模式 `w_p = 0.42`

保留条件：

```math
e \le 25^\circ
```

### 5.2 评分函数

普通装甲板候选评分近似为：

```math
score = 1.85 \Delta \psi^2 + 2.30 \Delta \phi^2 + 0.08 d
```

能量机关候选评分更偏向快速锁盘：

```math
score_{energy} = 1.45 \Delta \psi^2 + 1.85 \Delta \phi^2 + 0.04 d
```

其中 `d` 是距离，分数越低越优先。

### 5.3 抗乱跳策略

为了避免第一人称突然跳板，评分里还会叠加：

- 已锁定同一块板的保留奖励
- 同一目标内换板惩罚
- 不同目标之间更高的切换惩罚
- 刚转出来的新板奖励
- 快转走的板退出惩罚

因此当前策略是：

- 先保留当前锁定目标
- 再在同目标内谨慎换板
- 最后才考虑换到其他目标

---

## 6. 观测滤波

观测更新现在有两条并行链路：

- 旧链路：`TerrainMotionService.UpdateAutoAimObservationState(...)`
- 新链路：`TerrainMotionService.UpdateAutoAimObservationStateThirdOrderEkf(...)`

这样做的目的不是重复造轮子，而是为了在不中断旧功能的前提下，把新的三阶 EKF 位姿估计链路独立接入，便于：

- 对比新旧效果
- 快速回退
- 避免一次性替换把旧链路直接打坏

### 6.1 旧链路：常速度 Kalman

旧链路里，每个轴用一个常速度 Kalman 过滤器：

状态：

```math
x_k =
\begin{bmatrix}
p_k \\
v_k
\end{bmatrix}
```

预测：

```math
x_{k|k-1} = A x_{k-1|k-1}
```

```math
A =
\begin{bmatrix}
1 & \Delta t \\
0 & 1
\end{bmatrix}
```

校正：

```math
x_{k|k} = x_{k|k-1} + K_k \left(z_k - H x_{k|k-1}\right)
```

其中观测量是当前目标板中心位置，输出得到：

- 平面位置
- 平面速度
- 高度
- 高度速度

### 6.2 新链路：三阶 EKF 位姿估计

新链路的核心类是：

- `src/Simulator.ThreeD/AutoAimThirdOrderEkfPoseFilterState.cs`

平面状态显式包含位置、速度、加速度：

```math
\mathbf{x}_{xy} =
\begin{bmatrix}
x \\
y \\
v_x \\
v_y \\
a_x \\
a_y
\end{bmatrix}
```

高度状态为：

```math
\mathbf{x}_{z} =
\begin{bmatrix}
z \\
v_z \\
a_z
\end{bmatrix}
```

平面预测模型是常加速度模型：

```math
x_{k+1} = x_k + v_k \Delta t + \frac{1}{2} a_k \Delta t^2
```

```math
v_{k+1} = v_k + a_k \Delta t
```

观测不是直接用平面坐标，而是相对射手的距离和方位角：

```math
z =
\begin{bmatrix}
r \\
\beta
\end{bmatrix}
=
\begin{bmatrix}
\sqrt{(x-x_s)^2 + (y-y_s)^2} \\
\operatorname{atan2}(y-y_s, x-x_s)
\end{bmatrix}
```

因为这个观测方程是非线性的，所以这里使用 EKF 思路，在当前估计点对观测函数做线性化，再完成校正。

工程上这样做有几个直接收益：

- 对快速旋转目标的短时预测更稳
- 可以显式输出加速度给提前量求解
- 目标短时消失后，未来位姿不会立刻退化成“完全静止”

### 6.3 角速度估计

不管旧链路还是新链路，角速度都仍然用“绕目标中心的切向速度”估计：

```math
\omega =
\frac{r_x v_y - r_y v_x}{r_x^2 + r_y^2}
```

这对下列目标尤其重要：

- 小陀螺装甲板
- 前哨站旋转板
- 能量机关圆盘

---

## 7. 提前量解算

### 7.1 一阶思路

理想情况下，若目标匀速运动，则：

```math
p_{lead} = p_{obs} + v \cdot t_{lead}
```

但这里的 `t_{lead}` 不是常量，因为它取决于：

- 当前瞄准角
- 弹速
- 重力
- 空气阻力
- 目标未来位置

所以不能一次算完，必须迭代。

### 7.2 迭代流程

先给一个初值：

```math
t_0 = \frac{d}{v_0} + t_{latency}
```

然后循环：

1. 假设 `t = t_i`
2. 预测目标在 `t_i` 后的位置
3. 根据这个预测位置反解 `yaw / pitch`
4. 再用当前弹道模型估计真实飞行时间 `\hat t_i`
5. 用 `\hat t_i` 回写下一轮

即：

```math
t_{i+1} = \alpha t_i + (1-\alpha)\hat t_i
```

项目里：

- 17mm 通常迭代 4 次
- 42mm 通常迭代 6 次

### 7.3 目标未来位置

普通装甲板在旧链路中通常近似为：

```math
p_{future} = p + v_{target} t - v_{shooter} t \cdot k_{inherit} + \Delta p_{rot}
```

其中：

- `v_target`：目标速度
- `v_shooter`：射手自身速度
- `k_inherit`：继承速度补偿系数
- `\Delta p_{rot}`：由角速度带来的旋转位移

三阶 EKF 链路下，普通目标的平移预测升级为：

```math
p_{future} = p + v t + \frac{1}{2} a t^2 - v_{shooter} t \cdot k_{inherit} + \Delta p_{rot}
```

也就是说，新链路不只是在滤波阶段引入加速度，而是让下游提前量求解也使用同一套运动假设，避免“滤波器认为目标在加速，但求解器却还按匀速外推”的前后不一致。

结构目标和能量机关现在仍然优先使用“未来时刻的规则几何”直接解算，而不是只靠线性外推。

这样做的好处是：

- 前哨站/基地/能量机关的旋转或平移位姿，与规则层严格一致
- 不会把“当前帧目标”误当成“未来目标”

---

## 8. 保留预测

### 8.1 为什么需要

真实画面里会有短时遮挡、模糊、边缘丢板。
如果一旦“当前帧没识别到板”就立刻清空状态，会出现：

- 自瞄暂停
- 准星抖动
- 重新出现时大幅回拉

### 8.2 当前实现

现在保留预测分两类：

- 英雄吊射结构目标保留解算
- 小弹丸装甲板锁定后的保留解算

当重新搜索失败时：

- 不立即清空锁定
- 继续沿当前锁定目标和锁定板求解
- 用上一时刻滤波器状态继续输出未来位置
- 目标重新进入可见状态后再用新观测修正

这条链路的意义是：

- “短时看不见”不等于“目标不存在”
- 解算可以持续，控制不暂停

---

## 9. 英雄吊射

### 9.1 吊射只打结构目标

英雄吊射只允许打：

- 前哨站旋转板
- 基地顶部装甲板

普通模式仍限制为近距离普通装甲板。

### 9.2 为什么要用未来板位姿

如果自动扳机拿“当前帧板子”判断是否重合，而真实发弹命中的是“飞行时间之后的板子”，就会出现：

- 看到准星对上了
- 实际在板子刚转走以后才发射

因此现在吊射自动扳机用的是：

```math
t_{plate} = t_{now} + t_{lead}
```

在 `t_plate` 时刻重新求出目标板位姿，再判断：

- 标定预览是否命中
- 屏幕中心是否落在未来板投影范围内

也就是说，自动扳机判断和真实 lead time 使用的是同一时刻的板。

### 9.3 自动扳机

吊射模式下：

- 右键按住后进入自动控制
- `yaw / pitch` 都由自瞄接管
- 当预测板与屏幕准心满足重合条件时自动发射
- 不再需要 `Ctrl` 作为单独发射键

---

## 10. 能量机关专项

### 10.1 观测与攻击分离

能量机关自瞄链路是：

1. 识别同一旋臂的 `1 环`
2. 对这个 `1 环` 做滤波和角速度估计
3. 未来时刻直接重建该旋臂的 `10 环`
4. 弹道最终对准 `10 环`

### 10.2 为什么要禁用“未来时刻误读当前缓存”

运行时 GPU/场景层会生成当前帧圆盘目标。
如果未来求解时仍然误用这份当前帧缓存，就等于：

- 显示是当前帧圆盘
- 解算却假装它是未来圆盘

这会直接导致命中率下降。

因此现在加了限制：

- 只有当请求时刻与缓存时刻一致时，才允许用 runtime 目标
- 求 `t + \Delta t` 时一律按规则层重新生成未来圆盘

### 10.3 残留十字问题

细地形动态着色已经使用圆环本身表达：

- 待激活态
- 命中闪烁
- 常亮环数

因此 CPU/GPU overlay 中额外的双面激活标记已经移除，避免再次出现垂直于圆盘面的残留十字。

---

## 11. 一条完整链路

把整条链路按时间顺序写出来：

1. 从规则层和运行时场景层得到 `ArmorPlateTarget`
2. 用常速度 Kalman 或三阶 EKF 过滤位置、速度，以及新链路里的加速度
3. 用切向速度估计角速度
4. 迭代求解 `leadTime`
5. 在 `t + leadTime` 求未来目标位姿
6. 反解 `yaw / pitch`
7. 输出 `AutoAimSolution`
8. 同一份 `AutoAimSolution` 驱动 HUD、F8、云台和发弹

核心原则只有一句：

> 目标建模、控制输出、自动扳机和发弹，必须共享同一份未来目标解算结果。

---

## 12. 本次收口后的关键改动

本轮实现重点补了 5 件事：

- 英雄吊射自动扳机改为使用 `t_now + leadTime` 的未来板位姿
- 去掉 `Ctrl` 发射，人工发射只保留左键
- 能量机关未来预测不再误用当前帧 runtime 圆盘缓存
- 小弹丸装甲板在短时丢板时继续沿锁定目标保留预测
- 前哨站死亡阻尼加入 settle cutoff，避免停稳后继续做无意义衰减计算

这些改动的目的都一样：

- 让“看到的目标”
- “控制用的目标”
- “发弹要打的目标”

保持一致。

### 12.1 2026-04-25 吊射经验调参补充

结合 `build_verify/launcher_builds/debug/threeD/logs/autoaim_compensation.log` 的实测记录，这一轮又补了一次英雄吊射的经验参数，结论是：

- 对前哨站/基地这类结构目标，真正决定英雄吊射命中率的主量不是 `ang_scale`，而是有效预测时间 `lead_s + time_bias_s`
- 原先 `42mm` 打旋转结构板时使用负 `time_bias_s`，会让未来板位姿解算偏向“当前正在转离的旧位置”
- 因为结构目标本身已经走“未来规则几何重建”，所以这里优先调的是未来时间偏置和部署后的经验修正收敛速度，而不是只堆角速度缩放

这一轮具体改法：

- `SimulationCombatMath.ResolveAutoAimLeadTimeBiasSec(...)`
  - 对“英雄吊射 + 结构目标”加入正向时间偏置
  - 对旋转结构板使用更强的正向偏置，并随距离增加
- `SimulationCombatMath.ResolveAutoAimLeadScales(...)`
  - 将“英雄吊射 + 结构目标”的回退运动模型从过度阻尼调回接近 `1.0`
  - 目的不是替代未来几何解算，而是避免回退链路继续出现明显欠提前
- `RuleSimulationService`
  - 提高英雄部署模式对前哨站/基地顶部目标的命中概率下限
  - 加快 `yaw / pitch` 的命中后、未命中后经验修正收敛

实现上的判断原则可以浓缩成一句话：

```text
结构目标已经有未来位姿重建时，先修正“打未来多久”，再修正“往哪个方向多给一点速度”。
```
### 12.2 2026-04-25 能量圆盘微调

- 最新 `build_verify/launcher_builds/debug/threeD/logs/autoaim_compensation.log` 显示，`17mm_energy` 的修正已经从“偏左”跨到了“偏右”，因此本轮只做小幅回收。
- 调整方向是降低有效未来解算时间，并略微降低 `17mm_energy` 的角速度/平移提前量缩放，而不是重写能量机关预测链路。
- 原因是能量圆盘已经通过 `GetEnergyMechanismTargets(..., gameTime + compensatedLeadTimeSec, ...)` 做解析式未来位姿重建；这一类目标的过冲主要来自时间偏置，而不是通用角速度回退模型。

### 12.3 2026-04-25 F8 预测与发弹记录

- 每次自瞄发弹都会在 `SimulationShotEvent` 中记录目标类型、预测命中点、提前时间、提前距离、枪口位置和发射速度。
- `fire_events.log` 使用同一份 shot-time 数据输出，后续可以用来比对“上一发真实弹道”和“下一发预期弹道”。
- `F8` 会把当前 `AutoAimSolution` 的预测点画成虚影：能量机关显示 5 个未来圆盘模型，装甲板显示预测时刻装甲板模型。
- 引导模式只显示引导点，不向云台或相机下发控制信号；英雄吊射引导模式例外地冻结 yaw，但 pitch 和扳机仍由玩家控制。

---

## 13. 2026-04-25 部署 / 吊射 / 能量机关收口

### 13.1 部署模式为什么必须继续算提前量

部署模式不是“静止打静止板”。前哨站旋转装甲板、基地顶部滑移装甲板和能量机关圆盘都会在发弹飞行时间内继续运动，所以部署模式也必须使用同一套未来位姿：

```math
t_\text{hit}=t_\text{now}+t_\text{lead}
```

```math
P_\text{target}(t_\text{hit})=
P_0+V_0t_\text{lead}+\frac{1}{2}A_0t_\text{lead}^2
```

这一轮的代码原则是：`outpost_ring_*`、`base_top_slide`、`energy_disk` 永远不能因为当前观测速度短时接近零就短路成 `lead=0`。即使某一帧观测不到明显速度，也要从结构几何、运行时滑移轨迹或能量机关解析旋转中重建未来板位姿。

结构目标的蓝色预测点现在使用装甲板正中心：

```math
P_\text{aim}=P_\text{plate-center}(t_\text{hit})
```

之前 42mm 的通用高度补偿会把结构目标抬高，导致视觉上蓝色点偏向装甲板上缘甚至越出板面；现在 `outpost_ring_*` 和 `base_top_slide` 对这项高度补偿取 `0`。

### 13.2 能量机关：从 1 环观测反解圆心

能量机关不能只把当前看到的圆盘当成自由移动点，否则大能量机关变速旋转时会出现“蓝色虚影落后 / 左右抖动 / 下行阶段欠提前”。正确链路是先利用规则几何反解机关圆心，再生成未来 5 个圆盘。

设当前观测到第 `i` 个旋臂的 1 环圆盘：

```math
P_{i,1}^{obs}(t_o)
```

规则文件中已知该圆盘在机关局部坐标中的偏移：

```math
r_{i,1}
```

设转轴单位向量为 `n`，机关在观测时刻的旋转角为：

```math
\theta_o=\theta(t_o)
```

则圆心可以由 1 环观测反解：

```math
C=P_{i,1}^{obs}(t_o)-R(n,\theta_o)r_{i,1}
```

其中 `R(n, theta)` 表示绕单位轴 `n` 旋转 `theta` 的旋转矩阵。拿到圆心 `C` 后，对任意旋臂 `j`、任意环数 `k`，未来时刻 `t_h` 的圆盘位置是：

```math
P_{j,k}(t_h)=C+R(n,\theta(t_h))r_{j,k}
```

自瞄最终优先使用 10 环命中点：

```math
P_\text{aim}=P_{j,10}(t_\text{now}+t_\text{lead})
```

这样做的好处是，观测误差只影响圆心估计，旋臂间距、五个圆盘的相对位置、变速旋转相位仍由解析模型约束，不会因为某一帧灯条闪烁或圆盘外观变化而让 5 个盘散开。

### 13.3 三阶 EKF 的状态量

三阶 EKF 链路保留位置、速度、加速度三类状态：

```math
x=[p_x,p_y,p_z,v_x,v_y,v_z,a_x,a_y,a_z]^T
```

预测步采用常加速度模型：

```math
p'=p+v\Delta t+\frac{1}{2}a\Delta t^2
```

```math
v'=v+a\Delta t
```

```math
a'=a
```

观测更新仍然只直接观测位置：

```math
z=[p_x,p_y,p_z]^T
```

EKF 的作用不是替代规则几何，而是把“观测噪声、短时丢板、速度/加速度估计”稳定下来。对能量机关这类目标，先用解析旋转重建未来圆盘，再把观测驱动的速度、加速度、角速度输入提前量求解；对前哨站/基地，则先用结构几何给出未来板面，再用 EKF 保留运动趋势。

### 13.4 弹道提前量求解

提前量的核心方程是：

```math
t_\text{lead} \approx \frac{\|P_\text{target}(t_\text{now}+t_\text{lead})-P_\text{muzzle}\|}{v_\text{projectile}}
```

但英雄 42mm 吊射不是直线弹道，实际求解还要把重力、空气阻力、枪口继承速度和经验时间偏置放进去。代码层会迭代求解 `leadTime`，再用同一份 `AutoAimSolution` 输出：

- `AimPointX / AimPointY / AimPointHeightM`
- `LeadTimeSec`
- `LeadDistanceM`
- `YawDeg / PitchDeg`

关键要求是“HUD 看到的预测点、规则层自动扳机判断、最终发弹用的枪口姿态”必须来自同一份解，不能显示一套、发弹一套。

### 13.5 英雄吊射的窗口判定

英雄吊射强锁不是有冷却就立刻开火，而是等待装甲板进入合适击打窗口。规则层现在检查三个条件。

第一，弹道要穿过预测装甲板平面附近。设弹丸速度方向为 `v_b`，装甲板外法线为 `n_p`，入射夹角为：

```math
\alpha=\arccos((-v_b)\cdot n_p)
```

只有：

```math
\alpha \le 45^\circ
```

才认为是正面可打窗口。这样如果热量刚降为 0，但装甲板已经准备转到背后，就会放弃这一枪，等待下一次正面窗口。

第二，弹道与预测板中心的局部偏差必须在板面容差内。将命中点投到装甲板局部坐标：

```math
\Delta u=(P_\text{impact}-P_\text{plate})\cdot u_p
```

```math
\Delta v=(P_\text{impact}-P_\text{plate})\cdot v_p
```

需要满足：

```math
|\Delta u|\le \frac{W}{2}+\epsilon
```

```math
|\Delta v|\le \frac{H}{2}+\epsilon
```

第三，当前命中点不能离蓝色预测点太远：

```math
\|P_\text{impact}-P_\text{aim}\|\le \epsilon_\text{aim}
```

这三条同时满足时，强锁 / 部署模式才允许自动扳机。引导模式只显示引导点和吊射校准面板，不会把任何 yaw、pitch、camera 或 fire 信号写回控制层。

### 13.6 引导模式下玩家应该看什么

引导模式的设计目标是“提示玩家打哪里”，不是“替玩家转云台”。因此画面中会保留：

- 屏幕中心准心，表示玩家当前真正控制的枪口方向。
- 黄色 `PRE` 引导点，表示自瞄预测出的未来命中点。
- 吊射校准面板，显示当前弹道是否穿过预测板面、偏移多少、法线夹角是否小于等于 `45°`。

如果面板显示“窗口：可发射”，说明当前弹道已经满足板面、中心和正面窗口；如果显示“等待装甲板正面/中心窗口”，玩家应继续等待板转入窗口或手动微调 pitch。
## 2026-04-25：42mm 吊射提前量与引导模式修正

### 精细前哨站装甲板的未来投影

42mm 吊射日志中出现过 `lead_s > 0` 但 `lead_m = 0` 的情况。根因是精细前哨站模型同步到 `RuntimeOutpostTargets` 后只保存了当前帧装甲板位置，规则层请求 `now + leadTime` 时仍然拿到当前帧目标，导致 EKF 链路虽然算出了飞行时间，但未来装甲板中心没有移动。

现在运行时会同时保存 `RuntimeOutpostTargetsGameTimeSec`。当规则层请求未来时刻时，会按前哨站旋转轴把 `outpost_ring_*` 从同步帧投影到目标时刻：

```text
theta = rotation(t_future) - rotation(t_source)
dx = plateX - pivotX
dy = plateY - pivotY
futureX = pivotX + dx * cos(theta) - dy * sin(theta)
futureY = pivotY + dx * sin(theta) + dy * cos(theta)
futureYaw = plateYaw + theta
```

这样 42mm 和 17mm 使用精细模型前哨站时，都能得到真实的未来板中心、未来法线和提前距离。`lead_m` 会反映装甲板在弹丸飞行时间内实际移动的距离，而不是继续显示 0。

### 吊射自动扳机窗口

强锁吊射仍然遵循“先锁结构中轴 yaw，之后只让 pitch 跟随弹道解”的原则。自动扳机只在当前弹道确实穿过预测板面附近时触发。判定使用完整的 42mm `LeadTimeSec`，上限放到 2.35s，避免远距离吊射只检查过短的未来时间。

窗口判定同时检查：

```text
normalAngle <= 45 deg
abs(horizontalOffset) <= plateWidth * 0.46 + leadTolerance
abs(verticalOffset) <= plateHeight * 0.46 + leadTolerance
distance(impactPoint, predictedAimPoint) <= max(plateWidth, plateHeight) * 0.52 + leadTolerance
```

`leadTolerance` 现在比旧版本更收敛，避免还没有到蓝色预测圈附近就提前开火。左下角吊射面板会额外扫描未来约 2.4s 的装甲板窗口，显示“现在发射”“等待 Ns 后发射”或“当前姿态 2.4s 内无合适窗口”，用于说明应该等到哪个窗口期再出手。

### 引导模式

引导模式只做显示，不下发控制信号：不改 `TurretYawDeg`，不改 `GimbalPitchDeg`，不自动扳机，第一人称相机也不再把 `sightConvergence` 收敛到自瞄锁点。因此引导模式下枪管和相机保持玩家输入决定的相对位姿，屏幕中心准星、枪管方向和相机方向不会再与自瞄目标解耦。

## 2026-04-25：能量机关观测延迟与运行时圆心

能量机关自瞄的目标不允许直接读“理想圆心”作为真值。当前链路使用可观测的 1 环圆盘位置作为输入，再反解运行时旋转中心，最后重建 5 个圆盘的未来 10 环命中点。

### 运行时圆心反解

每一帧先从 GPU 同步出的运行时圆盘目标按臂分组，求每个臂的盘心：

```math
C_i=\frac{1}{N_i}\sum_j P_{i,j}
```

再用五个臂盘心的均值得到运行时旋转中心：

```math
O=\frac{1}{5}\sum_i C_i
```

角速度估计不再使用实体旧中心，而是使用 `O`：

```math
\omega=\frac{r_x v_y-r_y v_x}{r_x^2+r_y^2}
```

其中：

```math
r=P_\text{obs}-O
```

这样组合体被编辑器移动后，自瞄、命中框、F8 虚影和可见 GPU 圆盘都会围绕同一个运行时中心旋转。

### 观测延迟补偿

为了减少蓝色虚影落后实际圆盘，观测点会先按 EKF 输出前推一小段时间：

```math
\Delta t_\text{latency}=clamp(0.052+0.0015d,0.040,0.075)
```

常速链路：

```math
P_\text{obs}'=P_\text{obs}+v\Delta t_\text{latency}
```

三阶 EKF 链路：

```math
P_\text{obs}'=P_\text{obs}+v\Delta t_\text{latency}+\frac{1}{2}a\Delta t_\text{latency}^2
```

然后用 `P_obs'` 反解圆心，并预测到：

```math
t_\text{hit}=t_\text{now}+t_\text{flight}+\Delta t_\text{latency}
```

目标点仍然选对应圆盘的 10 环中心。竖直方向不再只看当前高度，而是由圆盘旋转造成的 `v_z`、`a_z`、弹丸飞行时间和弹道下坠共同进入 pitch 解算。
 
## 2026-04-25：右键未来命中点显示

右键自瞄进入锁定状态后，屏幕中的黄色引导标记必须显示规则层已经解算出的未来期望命中点：

```text
P_aim = AutoAimSolution.AimPoint
```

这个点已经包含：

- 目标观测位置。
- EKF 输出的速度与加速度。
- 弹丸飞行时间。
- 观测延迟补偿。
- 弹道下坠对应的 pitch 解算。
- 能量机关场景下从 1 环观测点反解并重建出的 10 环目标点。

因此 HUD 绘制阶段不再重新调用能量机关姿态反解函数。这样可以避免显示层与规则层再次分叉，也能降低按住右键/F8 时的重复计算成本。

能量机关的竖直方向提前量同样来自观测链路：

```math
z_{hit}=z_{obs}+v_z t+\frac{1}{2}a_z t^2
```

随后 `ComputeAimAnglesToPoint(...)` 使用弹道模型解算 pitch，补偿重力下坠。圆盘上行时，`v_z > 0` 会把期望命中点向上推；圆盘下行时，`v_z < 0` 会降低期望命中点，同时 pitch 解算继续补偿弹丸下坠。

能量机关未来 5 盘重建优先使用当前运行时圆盘观测：

```math
\Delta \theta = \omega_{EKF} \cdot t_{flight}
```

对当前运行时圆盘集合中的每个圆盘，以反解出的运行时中心 `O` 为 pivot 做旋转：

```math
P_i(t)=O+R(\Delta\theta)(P_i(0)-O)
```

这样未来目标点跟随 EKF 观测角速度，而不是优先读取规则层的理想旋转相位。只有运行时圆盘集合缺失时，才会回退到旧的 annotation 解析模型。
