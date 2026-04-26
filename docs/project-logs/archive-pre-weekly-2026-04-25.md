# 项目日志

## 维护约定

- 以后每次功能更新，都要同步更新这份日志。
- 如果改动会影响运行方式、入口、键位、地图格式、调试方式，也要同时更新 `README.md`。
- 如果改动会影响算法、规则、碰撞、渲染、自瞄、地图处理或互动组件，还要同步更新对应的 `docs/algorithms/*.md` 或 `docs/architecture/*.md`。
- 如果只是临时试验，但已经合入主分支，也要记录“试验目的、影响范围、是否保留”。

---

## 2026-04-25

### 自瞄与观测

- 新增三阶 EKF 位姿链路，并且保留旧的常速度 Kalman 链路不动。
- 三阶 EKF 链路单独维护一组观测滤波状态，不覆盖旧字典，避免旧逻辑被直接破坏。
- 新链路输出位置、速度、加速度三类结果，并进入新的提前量求解函数。
- 自瞄预测对普通目标改为支持二阶平移外推：`p = p0 + v t + 0.5 a t^2`。
- 结构目标与能量机关仍然优先走“未来规则几何重建”，不退化成简单线性外推。

### F3 调试显示

- `F3` 下的实体碰撞体积在 GPU 渲染模式中改为走 GPU 几何批次，不再主要依赖 GDI 覆盖层。
- `F3` 下的局部地形碰撞调试也补上 GPU 端绘制。
- GPU 模式下，`F3` 保留 HUD 文字统计，但碰撞几何本体不再重复经过 CPU/GDI 绘制。

### 文档体系

- 补充并重写项目根 `README.md` 的目录、入口、推荐阅读和构建方式说明。
- 补充 `docs/README.md` 作为文档入口。
- 完善 `docs/algorithms/autoaim.md`，统一描述目标建模、观测、提前量、自动扳机与吊射链路。
- 新增面向 C# 初学者的项目级教学文档。
- 新增文档维护规范，要求后续功能更新必须同步写日志和对应技术文档。

### 代码规范化

- 对新三阶 EKF 链路补充局部中文注释。
- 对 `F3` GPU 碰撞调试链路补充局部中文注释。
- 清理本轮临时隔离构建输出目录，避免继续污染工作区。

### Hero 吊射调参与蓝方半场颜色

- 检查 `build_verify/launcher_builds/debug/threeD/logs/autoaim_compensation.log` 后，确认英雄 `42mm` 吊射打前哨站时，结构目标的有效预测时间 `lead_s + time_bias_s` 偏保守，是远距离命中率下滑的主要来源。
- `SimulationCombatMath.ResolveAutoAimLeadTimeBiasSec(...)` 对“英雄吊射 + 结构目标”加入正向时间偏置，旋转结构板会比之前更早按未来位姿解算，减少打到旧相位的问题。
- `SimulationCombatMath.ResolveAutoAimLeadScales(...)` 把“英雄吊射 + 结构目标”的回退运动模型从过度阻尼调回接近 `1.0` 的中性区间，避免视觉链路或回退链路再次出现明显欠提前。
- `RuleSimulationService` 提高了英雄部署模式对前哨站/基地顶部目标的命中概率下限，并加快命中后与未命中后的 `yaw / pitch` 经验修正收敛速度。
- 蓝方半场的地形与重着色统一切到更深的蓝色，入口文件包括 `Simulator3dForm.GpuRenderer.cs`、`FineTerrainBaseVisualCache.cs`、`FineTerrainOutpostVisualCache.cs`，去掉当前偏浅的蓝色观感。

---

## 后续记录模板

复制下面模板追加到文件末尾：

```md
## YYYY-MM-DD

### 模块名
- 做了什么？
- 改了哪些文件？
- 影响了什么行为？
- 是否需要同步更新其他文档？
```
## 2026-04-25

### 能量机关自瞄经验回调
- 重新检查了 `build_verify/launcher_builds/debug/threeD/logs/autoaim_compensation.log`。
- 将 `src/Simulator.Core/Gameplay/SimulationCombatMath.cs` 中的 `17mm_energy` 经验补偿略微回调，避免修正从“偏左”直接跨到“偏右”。
- 这次只收敛了未来时间偏置和小弹丸平移继承阻尼，没有直接重写整条预测链路。

### 能量机关旧标记清理
- 更新 `src/Simulator.ThreeD/FineTerrainEnergyMechanismVisualCache.cs`，去掉旧的 `中央-R标` 交互单元缓存。
- 删除 `src/Simulator.ThreeD/Simulator3dForm.Structures.cs` 与 `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs` 中旧的 CPU / GPU 待激活十字辅助绘制逻辑。
- 预期行为是：命中圆盘后不再出现旧的垂直十字，只保留 `4/7` 环待激活提示和命中闪烁。

### 自瞄弹道调试
- 扩展 `SimulationShotEvent`，让每次自瞄发弹都保留发射瞬间的真实发射状态，供 `F8` 复原上一发弹道。
- 更新 `src/Simulator.ThreeD/Simulator3dForm.cs`，让 `F8` 同时绘制“上一发真实弹道”和“下一发预期弹道”。
- 同时绘制观测点虚影与提前量目标点，便于区分观测误差、提前量误差和弹道误差。

### 能量机关圆心建模链路
- 更新 `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`，新增能量机关圆心反解、旋转角速度/角加速度求解，以及基于圆心重建整队 5 个圆盘的建模函数。
- 更新 `src/Simulator.ThreeD/TerrainMotionService.cs`，将能量机关观测滤波对象从“圆盘点”改成“圆心”，再用滤波后的圆心回推当前目标圆盘的位姿、速度、加速度。
- 现在能量机关的提前量会优先使用“未来圆心 + 未来旋转轨迹 + 五圆盘重建”链路，再回退到旧的直接未来圆盘查询逻辑。
- 本轮已通过 `dotnet build src/Simulator.Core/Simulator.Core.csproj` 和 `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj`。

### 文档同步
- 更新 `docs/algorithms/autoaim.md`，补充能量机关“先反解圆心、再建模五圆盘、再做 Kalman / EKF 与提前量”的中文说明。
- 本文件中上一轮残留的英文条目已统一改为中文。

### 对局交互与暂停菜单
- 对局进行中点击底部暂停相关按钮时，运行层动作会被拦截，避免误触导致“重新开始”或“返回大厅”。
- 暂停浮层与观察者暂停浮层去掉了 `F7`、`H` 的按钮入口，只保留“继续 / 重新开始 / 返回大厅”。
- 指挥模式切换键统一改为 `F1`，局内 `1/2/3/4` 渲染切换入口继续保持移除状态。
- `F5` 键位说明改为按键盘区域顺序整理，并用半透明面板显示，避免和状态栏重复。

### 自瞄显示与英雄吊射控制
- `F8` 左上角自瞄解算栏改为保留最近一次有效快照，不再要求持续按住右键才显示。
- 普通 `T` 引导模式继续只显示引导点，不强制接管云台。
- 英雄吊射在 `T` 引导模式下改为“锁定 yaw、pitch 允许手控、手动扳机”；部署模式仍保持自动解算与自动开火。
- 状态栏去掉和 `F5` 帮助面板重复的按键提示，避免左下角信息重复堆叠。

### 能量机关命中判定
- 强化 `TryIntersectProjectileWithEnergyDisk(...)` 的圆盘半径、平面容差和 fallback 命中窗口，降低“视觉上穿过圆盘但未判定命中”的情况。
## 2026-04-25

### 自瞄自校准与回放
- 新增选车界面的 `Self-Cal` 开关，并把状态落盘到 `sim3d_autoaim_self_calibration_enabled`。
- 运行时实体新增 `AutoAimSelfCalibrationEnabled`，普通小弹丸与英雄部署修正现在都受这个开关控制。
- 新增 `SimulationAutoAimCalibrationEvent`，对每次自瞄发射记录观测点、预测点、实际命中或落点与误差。
- 局内会持续写出 `autoaim_training.log`，供离线复盘与经验调参使用。
- 新增 `src/Simulator.AutoAimCalibrationTool`，可直接读取 `autoaim_training.log` 输出分桶的提前量偏差建议。

### 能量机关与弹道显示
- 去掉了能量机关引导层里旧的外圈圆环画法，改成更小的预测点与观测-预测虚线，避免继续出现硬编码同心圆。
- `F8` 的上一发与下一发弹道线透明度和线宽已下调，减少遮挡视野。
- 17mm 和 42mm 对能量机关的提前量做了小幅回收，目标是把落点往十环中心收。

### 大弹丸与碰撞
- `42mm` 在非吊射与非部署链路下统一回到 `12m/s`，仅吊射链路保留更高初速。
- 小弹丸地形反弹改成“只保留合理掠射”，正撞地形会直接删弹，避免穿进精细模型后连续乱飞。
- 反弹脱离位移改成法线优先，减少卡进地形面后立即二次碰撞。

### GPU 光照
- 在现有 GPU 批处理渲染上增强了方向光、顶部点光和高光项，让磨砂与金属表面的层次更明显。
- 这次没有切到完整 shader/PBR，优先保持现有架构和帧率稳定。
## 2026-04-25

### 引导模式与吊射链路修正
- `src/Simulator.ThreeD/Simulator3dForm.cs`：引导模式下不再冻结英雄吊射 yaw，第一人称相机也不再吸附自瞄目标点。
- `src/Simulator.ThreeD/TerrainMotionService.cs`：`GuidanceOnly` 不再向云台写入任何自动控制；硬锁吊射结构目标时，平时与部署态统一使用同一套平滑后的 yaw/pitch。
- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`：吊射结构目标发弹正式改为吃 `AutoAimSolution` 的 yaw/pitch，避免“算出了提前量但发弹没用上”。

### 吊射预览与自动开火窗
- `src/Simulator.ThreeD/Simulator3dForm.LiveControl.cs`：吊射校准预览新增推荐 yaw/pitch、当前姿态偏差、提前量、中心开火窗数据。
- 当前预览的落点改为按“当前物理炮口姿态 + 局内实时经验修正”回放，和真实发弹链路保持一致。
- 自动开火判定不再只要求“碰到板面”，而是优先要求弹道进入装甲板中心窗口，提升向中心暴击区收敛的概率。

### 出生与地形/设施碰撞修正
- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`：复活时清零速度、腾空高度、垂直速度、ledge launch、底盘 pitch/roll，减少复活后埋地和姿态残留。
- `src/Simulator.ThreeD/TerrainMotionService.cs`：当前穿模修正改用 `ResolveTraversalGroundHeight(...)` 的真实支撑面，不再先拿粗糙中心高度，出生/靠近精细设施时更稳定。
- 设施碰撞代理加厚：`FacilityCollisionInsetM` 从 `0.035` 收到 `0.022`，组合体测试盒 inset 从 `0.018` 收到 `0.010`，减少钻进前哨站/基地内部。
- 轮组支撑面放宽：非腿式底盘的轮高采样夹取从 `0.12m` 放宽到 `0.17m`，支撑平面上限从 `+0.12m` 放宽到 `+0.18m`，缓解前轮上台阶后后轮拖住、上下抖动卡死的问题。

### 构建验证
- 已顺序通过 `dotnet build src/Simulator.Core/Simulator.Core.csproj`
- 已顺序通过 `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj`
## 2026-04-25

### 开火手感调整
- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`：发弹冷却改为固定 `20ms`，不再按 `FireRateHz` 计算，直接缩短玩家和 AI 的开火响应间隔。
- 这是纯手感层改动，没有改热量、弹道、命中、后坐力和弹丸结算公式。
## 2026-04-25

### 吊射自动扳机、能量机关命中与 F8 透明度
- `src/Simulator.ThreeD/Simulator3dForm.cs`：英雄吊射在硬锁装甲板且右键硬锁时，进入自动扳机；部署态继续沿用自动扳机。
- `src/Simulator.Core/Gameplay/RuleSimulationService.cs`：能量机关命中不再受装甲最低伤害速度门槛限制；修正能量环数读取优先级，优先使用 `EnergyRingScore`/环命名；未计入进度时也会给交互提示，方便排查。
- `src/Simulator.ThreeD/Simulator3dForm.cs`：`F8` 弹道线、光晕和落点改成更淡的半透明显示，减少遮挡。
- `src/Simulator.ThreeD/TerrainMotionService.cs`：底盘离地净空检测抬高，并在净空修正时优先采样 collision surface，减少车体埋入地面。
## 2026-04-25

### 结构装甲板 EKF 预测链修复
- `src/Simulator.Core/Gameplay/SimulationModels.cs`：给 `RuntimeOutpostTargets`、`RuntimeBaseTargets` 增加 `GameTimeSec` 时间戳，后续未来位姿预测不再把“当前帧 runtime plate”误当成未来 plate。
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`：同步前哨站/基地 runtime 互动 plate 时写入 `_host.World.GameTimeSec`。
- `src/Simulator.Core/Gameplay/SimulationCombatMath.cs`：新增前哨站 runtime 环板旋转投影、基地顶部 runtime 滑移投影。这样 `GetAttackableArmorPlateTargets(..., futureTime)` 在新地图运行时组合体下也能真正返回未来位姿。
- 这次修改针对的直接问题是日志里前哨站环板 `lead_s > 0` 但 `lead_m = 0`、`pred == obs` 的异常。

### 吊射 retained 结构目标统一改走观测 EKF
- `src/Simulator.ThreeD/TerrainMotionService.cs`：`TryResolveRetainedHeroLobStructureSolution(...)` 不再使用旧的“缓存瞄点 + 手工旋转外推”，而是直接复用 `ResolveAutoAimObservationState(...)` 与三阶 EKF 解算。
- 目标是让小弹丸自瞄与大弹丸吊射在前哨站/基地装甲板短时遮挡、切板或保持硬锁时，仍然持续输出未来预测位姿，而不是退回零提前量。

### 能量机关微扰动抑制
- `src/Simulator.ThreeD/TerrainMotionService.cs`：能量机关锁定时从“每帧硬贴 yaw/pitch”改成“小误差轻微平滑、大误差立即跟随”，用于压掉上下圆弧附近的左右微扰动。

### 构建验证
- 已通过 `dotnet build src/Simulator.Core/Simulator.Core.csproj`
- 已通过 `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj`
- 当前仅剩已有的 `CS0162` 不可达代码警告，未引入新的构建错误。
