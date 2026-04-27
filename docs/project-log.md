# 项目日志

## 维护约定

- 以后每次功能更新，都要同步更新这份日志。
- 如果改动会影响运行方式、入口、键位、地图格式、调试方式，也要同时更新 `README.md`。
- 如果改动会影响算法、规则、碰撞、渲染、自瞄、地图处理或互动组件，还要同步更新对应的 `docs/algorithms/*.md` 或 `docs/architecture/*.md`。
- 如果只是临时试验，但已经合入主分支，也要记录“试验目的、影响范围、是否保留”。

---

## 2026-04-27 1v1 / 单位测试规则与局内反馈修正

- 详见 `docs/project-logs/2026-W18-02.md`。
- 本轮修正 1v1 与单位测试的场景和规则闭环：
  - 1v1 改为加载独立单人地图；敌方哨兵固定进攻模式、全自动并拥有无限弹药。
  - 单人 1v1 选车界面隐藏“智能体”开关，其他模式不受影响。
  - 单位测试能量机关命中会自动进入测试激活流程，首次命中的己方盘会同步为当前目标。
- 局内反馈同步优化：
  - 任务栏图标改为 `DarkLogo.png`。
  - 中心 HUD 左右对称显示弹速/发弹量与自瞄模式/强锁引导状态。
  - F3 增加实时 XY、总速度与旋转角速度。
  - 右上角事件流改为中文表达。
- 运动与弹道：
  - 弹丸初速加入向下随机波动，覆盖 `16.5m/s -> 15.9-16.5m/s` 与 `25m/s -> 24.4-25m/s` 这类范围。
  - 英雄吊射引导模式允许玩家手控发射。
  - 平衡步兵起跳前增加 `0.15s`、约 `1cm` 下沉，支持跳跃或后腿支撑的机器人高处落地加入阻尼回弹。
- 验证：
  - `dotnet build src\Simulator.Core\Simulator.Core.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.Core`
  - `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`
  - 结果：`0 warnings / 0 errors`。

## 2026-04-27 局内 120FPS 目标性能优化

- 详见 `docs/project-logs/2026-W18-02.md`。
- 根据 `frame_pump.log`、`render_perf.log`、`simulation_perf.log` 和 `motion_control_slow.log`，确认最近局内掉帧主要来自 GPU HUD 叠加层全屏位图绘制/上传、局内 `Thread.Sleep` 帧节拍，以及 AI 自瞄完整重扫尖峰。
- 本轮改动：
  - 局内活跃帧节拍改为低延迟让出 / 轻自旋，避免 120Hz 目标被系统睡眠粒度拖到 60-90FPS。
  - GPU 叠加层增加活跃局内性能缩放表面，并在 `render_perf.log` 拆分 `overlay_detail(uploaded/scale/draw/tex/present/phases)`。
  - 活跃局内 GPU 模式同步提交本帧绘制，减少 WinForms 消息队列合并导致的帧间隔抖动。
  - 左下状态面板的机器人小头像在活跃局内改为低成本即时朝向头像，避免每次 HUD 刷新重新跑完整 3D 模型投影。
  - 新增 `SimulatorRuntimeLog`，把运行日志集中为异步批量写入，降低日志 IO 对主线程的干扰。
  - AI 锁定装甲板后短时复用预测解，减少完整候选扫描和视线重算尖峰。
- 验证：
  - `dotnet build src\Simulator.Core\Simulator.Core.csproj -c Debug --no-restore -nologo`
  - `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -c Debug --no-restore -nologo`
  - `0 warnings / 0 errors`
  - `Simulator.ThreeD.exe --start-match --renderer gpu --mode duel` 实测进入实战后连续窗口为 `124.1 / 125.3 / 123.7 / 126.5 / 124.9 / 124.0 / 125.8 / 121.0 / 126.5 FPS`。

## 2026-04-25

## 2026-04-26 1v1 回合制记分与敌方 AI 强化

### 1v1 回合制
- `src/Simulator.ThreeD/Simulator3dHost.cs`
- 新增 `DuelMatchSnapshot`，统一管理：
  - 对局次数上限
  - 已完成局数
  - 红蓝双方得分
  - 是否已结束
  - 终局结果文案
- 选车界面现在可以设置 `1 / 3 / 5 / 7` 局。
- `1v1` 中只要检测到任一方机器人触发 `death` 生命周期事件，就立刻按阵营记分。
- 非终局时会直接清空弹丸并重置双方位置/血量/热量，进入下一局，不再走普通 respawn 节奏。
- 达到设定局数后标记终局，冻结 `1v1` 仿真并显示最终比分。

### 1v1 HUD 与终局覆盖层
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- `1v1` 顶部 HUD 现在额外显示：
  - 当前比分
  - 当前第几局 / 总局数
  - 终局后显示胜负结果
- 终局时不再走普通暂停面板，而是改成 `1v1` 专用结算覆盖层，只保留：
  - 最终比分
  - 胜负结果
  - 重新开始
  - 返回大厅

### 1v1 敌方 AI
- `src/Simulator.ThreeD/TerrainMotionService.cs`
- 敌方 AI 保持现有 `attack -> planned navigation -> autoaim` 主链路。
- 在仅有两台可动机器人、且敌方已看到玩家时，额外接入 `1v1` 专用平移侧移逻辑：
  - 远距离继续寻路追击玩家
  - 进入可视近距后切到带相位扰动的左右平移
  - 同时保留自动瞄准与火控
- `src/Simulator.ThreeD/Simulator3dHost.cs`
- `1v1` 敌方哨兵姿态固定到 `move`，强化主动压近与平移小陀螺效果。

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 1v1 场地重构

### 场地
- `src/Simulator.ThreeD/Simulator3dHost.cs`
- `1v1` 独立场地从 `5x5m` 扩大到 `10x10m`。
- 地面主色改成中性灰，弱化原先偏蓝灰的观感。

### 墙体
- 两道中部障碍改成真正的 `45°` 斜向布置。
- 每道墙长度改成 `3m`，并保留中部通行缺口。
- 墙体同时写入：
  - `FacilityRegion line wall`
  - `TerrainFacet rotated wall`
- 这样视觉、移动碰撞和弹丸阻挡使用同一组几何。

### 机器人数量
- `1v1` 世界中的可动机器人不再只是“停到场外”。
- 现在会在场景构建阶段直接裁掉其余可动机器人，只保留：
  - 当前主控机器人
  - 蓝方敌方 AI 机器人

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 固定模式地图切换 / 大厅加载 / 顶部 HUD / 启动流畅度

### 固定模式地图切换
- 修复 `unit_test / duel_1v1` 仍然显示大地图的问题。
- 根因一：主界面默认完整模式的占位世界与固定模式场景都可能是 `RuntimeGrid = null`，原先地形缓存只按 `_cachedRuntimeGrid` 引用判断是否需要重建，导致切到固定模式后旧地图缓存直接被复用。
- `src/Simulator.ThreeD/Simulator3dForm.cs` 为地形缓存新增 `MapPreset + MatchMode + 尺度` 组合签名；只要场地预设或模式变化，就强制重建地形/细地形/GPU 地形缓存。
- `src/Simulator.ThreeD/Simulator3dHost.cs` 同步修正 `RebuildWorld()`，同步重建世界时会把 `_requiresDeferredLobbyBootstrap` 复位为 `false`，避免旧的“待异步大厅构建”状态残留到 `unit_test / 1v1`。

### 主界面异步预构建改到选车界面
- 启动主界面时不再立即 `PreloadActiveMapTerrainAssets()`，也不再立刻排队完整模式大厅异步构建。
- 现在改为：进入选车界面时才根据模式决定是否加载。
  - `full`：进入大厅后再显示加载覆盖层，并异步准备大地图大厅世界。
  - `unit_test / duel_1v1`：直接使用固定场地并重建对应缓存。
- `src/Simulator.ThreeD/Simulator3dForm.MainMenuUi.cs` 新增主菜单入口对 `full` 的延迟大厅切换，调用 `SetMatchModeDeferred(...)`。
- `src/Simulator.ThreeD/Simulator3dHost.cs` 新增 `SetMatchModeDeferred(...)` 与 `RebuildDeferredLobbyPlaceholderWorld()`，用于在进入完整模式大厅前先回到轻量占位世界，避免主界面卡顿。

### 顶部 HUD 分模式显示
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- 完整模式：保持原有顶部双方基地/前哨站/单位信息。
- `1v1`：顶部改为简化版，只显示倒计时、我方血量、敌方 AI 血量。
- `unit_test`：顶部 HUD 整体不显示，避免与单位测试信息面板冲突。

### 1v1 强制接入 AI
- `src/Simulator.ThreeD/Simulator3dHost.cs`
- `1v1` 模式下即使全局 AI 选项关闭，蓝方敌机仍会作为 AI 目标参与运动与火控。
- 具体做法是把 `TerrainMotionService.Step(...)` 和 `RuleSimulationService` 的 `enableAiCombat` 都切到 `effectiveAiEnabled = _aiEnabled || IsDuelMode`。

### 启动与大厅流畅度
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- 主界面状态下不再每帧执行 `WarmTerrainCacheGpuAssets()`；GPU 地形预热改成仅在大厅和对局启动加载阶段执行。
- 这次优化的目标是去掉主界面刚启动时的 UI 卡顿，把地图预热成本后移到真正需要地图的界面。

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 1v1 误套用大地图修复

### 根因
- 主界面启动后会异步准备一次完整模式大厅世界，任务完成前如果直接切到 `1v1`，旧的大厅重建任务仍会继续存在。
- 后续帧里 `CompleteLobbyWorldRebuildIfReady()` 会把这份旧的完整模式结果重新 `ApplyPreparedLobbyWorld(...)`，从而把已经切好的 `1v1` 固定地图又覆盖回大地图。

### 修复
- `src/Simulator.ThreeD/Simulator3dForm.MainMenuUi.cs`
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- 主菜单进入非 `full` 模式前，先显式丢弃旧的 `_lobbyWorldRebuildTask`、清空对应提示文本，并取消“重建完成后自动开始对局”的标记。
- 这样 `1v1 / unit_test` 进入大厅后，不会再被启动阶段遗留的完整模式大厅异步结果回写覆盖。

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

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

## 2026-04-25（本轮补充三）

### 地图坐标统一
- 局内 GPU 静态地形、精细碰撞面、运行时栅格备用光栅化路径统一改回地图编辑器使用的“模型中心为原点，映射到场地中心”的坐标基准。
- 这次不再给单个组合体或互动组件硬加偏移，而是让静态地图、F3 碰撞体、组合体渲染、互动组件目标都使用同一个 `ModelCenter -> field center` 体系。
- 修复目标是解决“地图编辑器中组合体相对位置正确，但局内静态地图 / 互动组件整体偏移”的根因。

### 自瞄引导与能量机关预测
- 引导模式下中心准星不再被自瞄锁定点覆盖，右键自瞄时只绘制未来期望命中点提示，不再改变玩家视角准星。
- 能量机关 EKF 观测链路改为从运行时 5 个圆盘目标反解旋转中心，避免继续使用实体旧中心导致角速度和圆心抖动。
- 能量机关预测加入观测延迟补偿：先把 1 环观测点按观测速度 / 加速度前推，再反解圆心并预测到 `t_now + leadTime + latency`，目标仍然是对应圆盘的 10 环命中点。

### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-26

### 地形碰撞 / 能量机关自瞄 / 吊射副视角
- 地形碰撞重构为直接使用 terrain cache 表面三角面，不再对细碎构件自动外包盒，也不再对碰撞面做 1cm 外推；当前移动/射线/遮挡会落在与地图表面建模一致的三角面上。
- 能量机关自瞄链路去掉了“按未来规则时刻直接重建圆盘位姿”的捷径，主预测改为基于观测位置、观测速度、观测加速度和三阶 EKF 结果前推；竖直方向提前量现在显式参与飞行时间和重力下坠修正。
- 结构件/能量机关板点速度求解改为由观测平动速度与观测角速度分解得到，不再用未来时刻板点差分反推速度。
- 英雄吊射副视角替换为真实 3D 第二相机：主视角恢复 1x，左下副视角使用 2x 镜头；未按右键时沿主准星观察，按住右键或部署时副视角锁定预测装甲板中心。
- 英雄云台外观新增副相机组合体：在云台左上方增加 3x3x7 cm 长方体副相机机身，并通过两根 3 cm 圆柱支架以 45° 斜向连接。
- 验证：`dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

### F3 碰撞体偏移重试修正
- 继续按“只移动碰撞体，不移动视觉建模”的原则处理地形碰撞偏移。
- `TerrainCacheCollisionSurface` 的 terrain cache 三角面转换现在优先读取地图 annotation 的 `WorldScale`，使用 `ModelCenter / ModelMinY / XMetersPerModelUnit / YMetersPerModelUnit / ZMetersPerModelUnit` 作为碰撞层坐标来源。
- 碰撞层的场地中心不再使用 `preset.Width * 0.5 / preset.Height * 0.5`，而是使用 `fieldLengthM / metersPerWorldUnit / 2` 与 `fieldWidthM / metersPerWorldUnit / 2`，避免地图像素高宽比例和真实场地米制比例不一致时，碰撞体相对 GPU 视觉地形整体偏移。
- `F3` 中的 `buff/debuff` 区域改为更低透明度的填充加轮廓线，避免大面积蓝色调试区域被误认为地形碰撞块。

### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-26

### HUD 头像与 Buff 显示
- 左下角状态面板的圆形头像从随车体 yaw 旋转的俯视块状图，改为固定机器人侧面剪影，风格与选车/外观预览中的侧视图保持一致。
- Buff 图标行整体上移，倍率文字改为带半透明底的小字显示，避免被血条遮挡。

### F3 地形碰撞对齐
- `TerrainCacheCollisionSurface` 的模型坐标换算改为优先复用 GPU 视觉地形同源的 `RuntimeReferenceScene.WorldScale`，不再单独从 terrain cache catalog bounds 推导中心点。
- 精细设施的外层壳体碰撞代理收窄到细长且不可行走的零件；斜坡、台面等可行走面继续使用原始三角面，避免被 AABB 壳体包成与实际模型错位的碰撞块。

### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-25（本轮补充八）

### F3 碰撞体积与 Buff 区域
- `EntityCollisionModel` 的机器人/结构实体碰撞部件统一外扩 `1cm`，`TerrainCacheCollisionSurface` 的精细地形碰撞三角面也按法线外扩 `1cm`，使 `F3` 显示的碰撞体积就是实际参与碰撞的真实体积。
- 对三角数较高、投影尺寸属于精细设施零件的 terrain cache 组件，碰撞层不再导入内部所有三角面，而是以最外层 AABB 壳体参与碰撞，减少机器人穿入基地/前哨站内部细碎面后卡顿。
- `F3` GPU 调试显示现在额外绘制 `buff/debuff` 区域，调试碰撞时可以同时确认增益区和阻挡体的位置关系。

### 左下角 HUD 与经验体系
- 左下角状态面板改为新样式：圆形机器人剪影、左侧 `2π/3` 经验弧、右侧队伍色梯形血条、下方缓冲能量条、上方金色圆形 buff 图标。
- 能量机关命中按环数给射手机器人经验；小能量机关激活按 `1200` 经验总量分摊给己方存活可升级机器人，大能量机关激活按 `750` 经验总量分摊。
- 英雄和步兵等级上限接到 10 级阈值，阈值按规则图中的 `0/550/1100/.../5000` 使用。

### 坐标矩阵记录
- `SimulationWorldState` 新增 `RuntimeModelToSceneMatrix`、`RuntimeModelToWorldMatrix` 和 `RuntimeModelTransforms`，初始化时从 terrain cache 的模型中心与比例尺生成统一矩阵。
- 所有初始化机器人、基地、前哨站、能量机关实体同步保存这套矩阵，后续类似实体不再各自推导坐标变换。

### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-25（本轮补充五）
### 地图实现回退
- 按用户指定的 `435d45827e87f31f26834ab6e289e0fa1118bfd8` 对照当前工作区，严格回退地图加载、坐标换算、细地形视觉缓存、GPU 地形绘制、F3 地形碰撞显示、运行时地形网格、组合体/互动组件位姿同步和地形运动碰撞相关实现。
- 已恢复到该 commit 的文件包括 `FineTerrainRuntimeSceneScaleResolver.cs`、`ComponentAnnotationImporter.cs`、`RuntimeReferenceLoader.cs`、`FineTerrain*VisualCache.cs`、`Simulator3dForm.FineTerrainActors.cs`、`Simulator3dForm.GpuRenderer.cs`、`Simulator3dForm.cs`、`Simulator3dHost.cs`、`TerrainCacheCollisionSurface.cs`、`TerrainCacheRuntimeGridLoader.cs`、`TerrainMotionService.cs` 和 `EntityCollisionModel.cs`。
- 按要求没有回退 `maps/rmuc2026/map.json`，因此当前地图内新增或调整过的 buff/debuff 区域继续保留。
### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-25（本轮补充六）
### 地图渲染位移 / 互动组件目标同步继续回退
- 用户明确要求只恢复 `435d45827e87f31f26834ab6e289e0fa1118bfd8` 中正常的局内地图渲染、组合体位移和互动组件位置实现，不继续修复当前版本偏移 bug。
- 在上一轮地图加载与 GPU/F3/terrain cache 回退基础上，本轮继续同步回退 `SimulationCombatMath.cs`、`SimulationModels.cs`、`RuleSimulationService.cs`、`EnergyMechanismGeometry.cs`，移除当前版本中对运行时前哨站/基地/能量机关互动目标位姿同步链路的新增实现差异。
- 复核结果：地图渲染、位移变换、组合体/互动组件位置相关文件相对指定 commit 已无差异；`maps/rmuc2026/map.json` 仍保留当前版本，用于保留 buff/debuff 区域。
### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`

## 2026-04-25（本轮补充七）
### 只移动碰撞体积
- 按用户要求不再移动视觉建模位置，只调整碰撞体积坐标映射。
- `TerrainCacheCollisionSurface.cs` 的 terrain cache 碰撞三角面从旧的 `catalog.MinX/MinZ -> 0` 原点映射改为 `model center -> field center` 中心映射，使 F3 碰撞体积和物理碰撞面移动到可见地图位置。
- `TerrainCacheRuntimeGridLoader.cs` 的高度/阻挡栅格 fallback 同步使用同一中心映射，避免后续回退到栅格路径时再次出现碰撞与视觉地图平移不一致。
### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`
## 2026-04-25

### Energy AutoAim Fine Tuning
- Re-checked `build_verify/launcher_builds/debug/threeD/logs/autoaim_compensation.log`.
- Rolled back the `17mm_energy` profile in `src/Simulator.Core/Gameplay/SimulationCombatMath.cs` so the energy disk solve no longer stays over-shifted to the outgoing side after the previous correction.
- Kept the change conservative: only the effective future-time bias and small-projectile translation damping were adjusted.

### Energy Marker Cleanup
- Updated `src/Simulator.ThreeD/FineTerrainEnergyMechanismVisualCache.cs` to strip the legacy `中央-R标` interaction unit from both the energy composite body mesh and the unit mesh cache.
- Removed the old CPU and GPU pending or activation energy marker helper methods from `src/Simulator.ThreeD/Simulator3dForm.Structures.cs` and `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`.
- Expected runtime result: the vertical center marker should no longer appear after an energy disk hit; only ring `4/7` pending indication and ring-flash hit feedback remain.

### Docs
- Updated `docs/algorithms/autoaim.md`.
- Updated `docs/algorithms/energy-mechanism.md`.

## 2026-04-25（周日志续更）

### 部署 / 吊射 / 能量机关自瞄
- 详细记录见 `docs/project-logs/2026-W17.md` 的“续九”条目。
- 本轮补齐部署模式三阶 EKF 提前量、能量机关 1 环观测反解圆心、未来 5 盘建模、英雄吊射 `<=45°` 法线窗口和引导模式不强控说明。
- 同步更新 `docs/algorithms/autoaim.md`，新增数学公式说明和局内提示解释。
## 2026-04-25（本轮补充）

### F3 / 吊射 / 引导模式
- 本轮详细记录见 `docs/project-logs/2026-W17.md` 的“续十”条目。
- 地形 GPU 渲染投影改为和碰撞面一致的 `Bounds.Min -> Bounds.Max` 线性映射，避免 F3 碰撞三角面与可见地图偏移。
- 精细前哨站 runtime 装甲板增加同步时间戳和未来旋转投影，修复 42mm 吊射 `lead_s > 0` 但 `lead_m = 0` 的提前量短路。
- 吊射自动扳机窗口使用完整 42mm lead time，并在 HUD 中显示建议发射时机。
- 引导模式下相机不再跟随自瞄锁点，保证云台、枪管、相机都不接收自瞄控制信号。

## 2026-04-25（本轮补充二）

### 互动组件位姿跟随
- 本轮详细记录见 `docs/project-logs/2026-W17.md` 的“续十一”条目。
- 精细能量机关、前哨站、基地的运行时互动目标统一从渲染端最终矩阵与场景偏移导出，避免只移动可见模型而命中框 / 自瞄目标仍停在旧位置。
- 能量机关未来目标投影会从运行时 5 臂圆盘反推当前视觉中心和转轴，不再绕旧注解中心做二次旋转。
- 前哨站未来目标投影会从运行时 `outpost_ring_*` 反推旋转中心，不再使用旧注解 pivot，避免 F8 虚影和可见旋转组件分离。
- 验证命令：`dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`。
 
## 2026-04-25（本轮补充四）

### 坐标权威源统一
- 运行时 terrain cache 引用加载现在优先采用地图编辑器 annotation 中导出的 `WorldScale.ModelCenter / XMetersPerModelUnit / YMetersPerModelUnit / ZMetersPerModelUnit / ModelMinY`，不再在传入 annotation 后仍用 GLB/cache bounds 重新推导比例尺。
- GPU 静态地形、精细碰撞面、能量机关、前哨站和基地顶部视觉缓存统一消费同一份 annotation world-scale，避免“编辑器正确、局内所有组合体/互动组件整体偏移”的混用问题。
- 这次没有给单个组合体硬加偏移，而是修复底层坐标来源，保持静态地图、组合体矩阵、互动组件目标和 F3 碰撞体处在同一套 `ModelCenter -> field center` 映射中。

### 自瞄提示与性能
- 右键自瞄锁定后，引导标记直接绘制 `AutoAimSolution.AimPoint` 中的未来期望命中点；能量机关不再在 HUD 绘制阶段重复调用圆盘姿态反解，降低按住右键/F8 时的 overlay 计算成本。
- 能量机关目标仍以 1 环观测、EKF 速度/加速度、观测延迟和弹丸飞行时间进入预测链路，最终显示与控制目标是对应圆盘 10 环的未来命中点。
- 能量机关未来目标优先使用当前运行时圆盘观测和 EKF 角速度积分重建，不再优先读取规则旋转相位；只有缺少运行时圆盘观测时才回退旧解析路径。

### 验证
- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts/buildcheck/Simulator.ThreeD`
## 2026-04-26

### 吊射性能 / F8 能量机关调试 / HUD 与编辑器颜色
- 吊射弹道校准预览增加局内缓存：自动扳机判断不再每帧重复扫描未来发射窗口，HUD 预览使用较低频率刷新，并将单次弹道积分步长从 10ms 放宽到 18ms，降低按住右键吊射时的 CPU 控制层负载。
- 英雄吊射结构目标的自瞄解算允许在短窗口内复用前一帧结果，避免前哨站/基地旋转装甲板每帧完整 EKF/弹道重算导致卡顿。
- F8 自瞄调试界面对能量机关补充显示视觉建模中心点与 5 个圆盘虚影，调试文本会显示当前跟踪盘序号与能量机关状态。
- 吊射状态左下角面板改为“2x 吊射副视角 / 弹道校准”：未按右键时显示与主准星同步的 2x 待机窗，按住右键锁定后显示命中点/偏移量/发射窗口信息。
- 左下角机器人状态 HUD 去掉外层方框，中心圆环和头像整体外扩，增加左右半透明细弧；速度信息移入 F3 调试面板，右侧弧显示弹丸初速和按热量/弹药估算的允许发弹量。
- 热量超限、底盘断电、复活读条改为准星附近同心圆进度条；机器人死亡时屏幕灰白偏暗，并禁用云台、底盘、火控输入直到复活；复活无敌期间右下角显示金色盾牌“无敌”图标。
- 地图组件编辑器增加组件颜色覆写：支持调色板、吸取当前主组件颜色、对选中组件应用/清除颜色；颜色覆写保存到组件标注 JSON，并在编辑器 GPU 绘制端拆出对应组件单独着色。

### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 地形碰撞编辑与机器人外观预览

### 地形编辑器支持 facet 碰撞体积
- `TerrainFacetDefinition` / `TerrainFacetEditorModel` 新增 facet 级碰撞参数：
  - `CollisionEnabled`
  - `CollisionExpandM`
  - `CollisionHeightOffsetM`
- `TerrainEditorService` 保存/加载这些参数，地图编辑器里的斜面 facet 现在可以直接单独调碰撞外扩与碰撞高度，而不必改视觉面本体。
- `MapPresetPreviewControl` / `RuntimeGridLoader` / `RuntimeGridData` 贯通了这组数据，运行时会把 facet 的视觉采样与碰撞采样分开处理。
- 运行时新增 `TrySampleFacetCollisionSurface(...)` 与 `SampleCollisionHeightWithFacets(...)`，地形运动/弹丸/遮挡相关路径会优先走 facet 的碰撞几何，而不是直接复用视觉斜面。

### 机器人外观编辑器 F6 平面驾驶预览
- `AppearanceEditorForm` 新增 `F6 Flat Drive Preview` 开关，并绑定 `F6` 热键。
- 启用后，机器人类配置的 GPU 预览会切到 `blankCanvas + single_unit_test`，可直接在编辑器里预览平面驾驶。
- 结构体（`base/outpost/energy_mechanism`）不会进入该模式，仍保留原本的静态结构预览。

### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 HUD 与观测链路收口

### 左下状态 HUD 收口
- `Simulator3dForm.cs`
  - 玩家状态面板在英雄吊射启用时改为避让左下副视角，不再压住副视角窗口。
  - 左下状态面板文字收口为仅保留机器人编号/名称、当前血量/血量上限、功率上限。
  - 血条与功率条统一改为右端轻微收口的直角梯形；功率条宽度缩短为血条的 `75%`。
  - 圆形机器人图改为复用选车界面的运行时外观预览，而不是旧的示意图。

### 吊射副视角与引导控制
- `Simulator3dForm.LiveControl.cs`
  - 吊射副视角覆盖层继续收口，只保留标准相机画面、细边框与世界引导点，不再堆叠说明文字。
- `Simulator3dForm.cs`
  - 英雄吊射在 `GuidanceOnly` 下不再强控 pitch / fire；此时保留玩家手动 pitch 与手动火控。
  - 只有部署态或非 `GuidanceOnly` 的右键硬锁链路才会进入自动 pitch / 自动扳机冻结逻辑。

### 自瞄/吊射观测缓存推进
- `TerrainMotionService.cs`
  - 补全 `AutoAimSolveCache`，新增观测板快照、观测状态、目标中心缓存。
  - 新增 retained 观测推进 helper：丢观测后改为从上一次观测的位姿/速度/加速度/角速度继续外推，不再回读当前项目装甲板位置来重建瞄准点。
  - `TryResolveRetainedHeroLobStructureSolution(...)` 与 `TryResolveRetainedArmorTrackingSolution(...)` 都改成基于缓存观测态继续推进，再把推进后的快照送入观测驱动解算器。
  - 这样旋转装甲板短时丢观测时，会继续保持预测逼近，等下一块板重新进入观测后再纠正。

### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 前哨站停转与 HUD 收口

### 前哨站 3 分钟停转
- `SimulationCombatMath.cs`
  - 前哨站存活阶段的环形装甲板旋转时间钳制到 `180s`，对局开始 3 分钟后停止继续旋转。
  - 停转只影响板位角度推进，不影响命中与受伤，后续仍可正常击打。

### HUD 文案与“允许发弹量”
- `Simulator3dForm.cs`
  - 修复本轮遗留的字符串/注释损坏，恢复 `Simulator.ThreeD` 正常构建。
  - 左下状态面板标题收口为“编号 + 等级”，去掉冗余角色文案。
  - 面板底行只保留弹种和锁定状态。
  - 右侧弧形信息与第一人称中心圆环旁的小字中，“允许发弹量”统一直接显示当前弹药库存，不再按热量换算。

### 构建验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 中文乱码修复

### UI 文案恢复
- `Simulator3dForm.cs`
  - 对本轮被错误转码的中文字符串做了一次批量恢复，再对主菜单、Lobby、暂停层、F3、左下/右下 HUD、决策栏、局内预览和战斗事件文案做了人工收尾。
  - 修复后主菜单按钮、地图选择、兵种选择、暂停按钮、前哨站/基地状态、自瞄提示、战斗日志等界面文本已恢复为正常中文。

### 构建验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射副视窗面积调整

### 左下副视窗
- `Simulator3dForm.LiveControl.cs`：左下吊射副视窗矩形从 `172x104` 调整到 `210x126`，按“窗口面积变大”处理，不改动相机倍率与锁板逻辑。
### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射主副视角再收口

### 视角与遮挡
- `Simulator3dForm.cs`：吊射模式下主视角恢复普通第一人称视场，不再做主视角放大。
- `Simulator3dForm.LiveControl.cs`：左下副视角固定回 `3x`，副视窗扩大到 `420x252`，并新增单独的极简 `LOB SUBVIEW` HUD，去掉原来那一大段吊射校准文案。
- 同文件副相机位置沿前向再前移 `2cm`。
- `Simulator3dForm.cs` / `Simulator3dForm.LiveControl.cs` / `Simulator3dForm.GpuRenderer.cs`：副视角渲染时临时跳过当前选中的英雄本体，避免镜头前方再看到自身黑色机体遮挡。
### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 前哨站停转 / 面板去冗余

### 前哨站旋转
- `SimulationCombatMath.ResolveOutpostRingRelativeRotationRad(...)` 改为：前哨站开局仅前 `180s` 旋转，之后即使存活也保持最后角度不再继续转动；装甲板与命中判定仍保留，仍可正常被击打。
### 右下状态面板
- `Simulator3dForm.cs` 的 Neo 面板继续收简：底部状态行改成仅显示口径和锁定状态，不再重复把弹药量也塞到底行。
- 左侧外弧文字改成两行：上行显示弹速，下行显示“允许发弹量”，并且该值现在直接读取弹药库存，不再按热量再做截断。
- 标题行间距轻微收紧，减少视觉杂讯。
### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射副视角补完

### 独立副相机 / 3x 主视角
- `Simulator3dForm.cs`：英雄进入吊射自瞄模式时，第一人称主视角同步切到 `3x` 视场，和左下副视角保持一致。
- `Simulator3dForm.LiveControl.cs`：左下副视角的 `TRACK / GUIDE` 状态改为按真实装甲板锁定状态显示，不再借用右键输入态。
- 同文件删除旧的 `DrawHeroLobHitPreview / DrawHeroLobMissPreview` 2D 预览路径，保证吊射副视角只走独立 3D 相机链路。
### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射开火窗 / 升级血量 / 中心环文字

### 副视角与建模
- 左下吊射副视角从 `3x` 进一步改为 `3.5x`，标签同步改成 `3.5x TRACK / GUIDE`。
- 英雄云台左上副相机的两根 45° 连接圆柱改为 `8cm` 长度，保留 `3cm` 直径。
### 吊射自动扳机
- `Simulator3dForm.cs` 的 `IsHeroLobReticleAlignedForAutoFire(...)` 不再只要“预测打到板”就放行。
- 现在必须同时满足：`FireWindowReady`、预测命中点靠近装甲板中心暴击区、准星落在预测板面附近，才允许自动开火。
- 没有拿到高质量 preview 时，屏幕多边形重叠和投影点阈值也都同步大幅收紧，避免蓝框还没到位就提前出手。
### 升级与 HUD
- `RuleSimulationService.ApplyResolvedRoleProfile(...)` 改为：升级导致 `MaxHealth` 变化时，当前血量按升级前血量比例映射到新上限，而不是只做绝对值截断。
- 第一人称中心圆环右侧新增半透明小字，显示当前弹丸初速和允许发弹量。
### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 能量机关命中修复

### 能量圆盘碰撞与命中吞掉
- `SimulationCombatMath.cs` 中能量圆盘求交的平面容差、径向容差和 fallback 平面容差再次放宽，优先保证视觉上已经擦中/打中的弹丸能被圆盘求交接受。
- `RuleSimulationService.cs` 中能量机关命中不再套普通装甲板那套全局去重节流。已经求交到 `energy_*` 圆盘的弹丸，现在不会再因为 17mm 的 `50ms` / 42mm 的 `200ms` 表面命中间隔被后处理吞掉。
- 这样处理后，能量机关一侧仍然保留“同一圆盘只记一次进度”的规则层限制，但不会再出现“明明打到了，规则层完全没响应”的问题。
- 构建验证：`dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射副相机建模与独立镜头

### 英雄吊射左下角 3D 副视角
- 副视角继续使用独立相机链路，不复用主相机；只要吊射自瞄已经锁定结构装甲板，副相机就直接跟踪该装甲板的实时位姿。
- 左下角预览框不再覆盖副视角本体，保留真实 3D 子视口画面。

### 副相机实体建模
- `Simulator3dForm.AppearanceModel.cs` 中英雄云台左上侧的副相机外形改成固定 `7cm x 3cm x 3cm` 长方体，朝向平行于云台。
- 副相机与云台之间改成两根直径 `3cm` 的斜向 `45°` 圆柱连接，不再使用之前那种随镜头朝向一起斜着摆放的机身。
- 构建验证：`dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 继续修复

### 地形碰撞 / 引导模式 / 吊射性能
- `TerrainCacheCollisionSurface` 的模型坐标到世界坐标变换改为优先使用 `RuntimeReferenceScene.WorldScale`，和当前局内可见地形的 runtime-reference 渲染基准保持一致，不再优先吃 annotation 的旧缩放参数。
- `TerrainMotionService.TrySampleTraversalCollisionSurface(...)` 增加保守兜底：高度带采样失败时，会再尝试一次邻域碰撞面采样，只接受落在允许爬升/下探范围内的结果，避免地形表面存在但支撑采样短暂失配时整车继续下沉。
- 第一人称相机在 `GuidanceOnly` 下不再收敛到自瞄锁点；引导模式保持“只显示提示，不写入相机/云台控制”。
- 英雄吊射预览缓存改为真实缓存：同一锁定目标、短时间窗口、炮口姿态变化很小时直接复用上一帧结果，不再每次 HUD / 自动扳机 / 副视角重复全量重算。
- 吊射副视角在 `GuidanceOnly` 下不再自动追目标，只保留长焦观察；按右键硬锁或部署模式时才允许副视角锁到预测装甲板。
- 构建验证：`dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26 吊射副视角 / 前哨站命中 / 卡顿

### 3x OpenGL 吊射副视角
- `Simulator3dForm.LiveControl.cs` 的吊射 HUD 面板不再把左下预览窗填成实心，给底层专用 3D 副视角留出透明窗口。
- 吊射副视角从原来的 2x 改成 3x，标签同步改为 `3x TRACK / 3x GUIDE`。
- `Simulator3dForm.GpuRenderer.cs` 的副视角增加了独立 `glViewport + glScissor` 裁剪，确保它是单独的 OpenGL 子视口，而不是被主视角内容污染。

### 吊射卡顿
- GPU 副视角不再重复完整重绘整套设施和高成本地形细节，只保留低成本地面上下文、结构体主体和目标相关动态几何，降低英雄吊射时的第二遍场景渲染开销。
- 配合上一轮的吊射预览缓存，当前主要热点从“重复算弹道 + 重复整图副渲染”收掉了一部分。

## 2026-04-26 HUD 定位 / 吊射副视图 / 中文乱码

### 副视图与状态面板定位
- `Simulator3dForm.cs` 新增固定的 `GetPlayerStatusPanelRect()`，玩家状态面板不再因为英雄吊射模式切换而挪位。
- `Simulator3dForm.LiveControl.cs` 的吊射副视图矩形改为锚定在状态面板正上方，并沿状态面板右侧对齐，保证 UI 位置稳定且互不遮挡。

### 吊射副视图画面
- `Simulator3dForm.GpuRenderer.cs` 的 `DrawGpuHeroLobSecondaryViewport(...)` 改回完整场景渲染路径，副窗口显示真实放大的周边环境，而不是简化示意图。
- 当前实现仍然是独立子视口二次渲染，不会移动主 HUD，也不会再用简化 2D 命中预览代替长焦观察画面。

### 局内中文修复
- `Simulator3dForm.cs` 修正了队伍卡片中的 `HudUnitLabelMap`，恢复 `1 英雄 / 2 工程 / 3 步兵 / 4 步兵 / 7 哨兵`。
- 同文件补正了 `待命`、`等级提升` 等局内状态文案。
- `Simulator3dForm.LiveControl.cs` 新增本地化的吊射校准绘制分支，避开历史编码污染代码段，保证局内吊射面板不再显示乱码。

### 前哨站命中检测
- `Simulator3dForm.FineTerrainActors.cs` 中前哨站运行时旋转装甲板的 `yaw` 不再优先取模型局部法线，而是优先取“旋转中心 -> 板中心”的径向方向，保证命中判定所用板正面和实际旋转板朝向一致。
- `SimulationCombatMath.cs` 对 `outpost_ring_*` 的小弹丸/大弹丸命中容差与回退半径各放宽了一档，减少视觉上已擦中/打中却被判丢失的情况。
- 构建验证：`dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`

## 2026-04-26（补充）

### F3 区域调试 / 经验反馈 / Buff 聚合
- `F3` 调试显示补齐了 buff / debuff 区域：GPU 调试面提高了区域填充和描边可见性，CPU / GDI 回退路径也会投影显示同一批区域，不再只看碰撞三角面。
- 经验系统现在把增量直接挂到运行时实体上，局内会弹出 `+XP` 浮字；升级时会追加等级提升提示，便于观察一局内经验变化。
- 战斗经验结算补到常规命中与击毁：机器人伤害、前哨站伤害、基地伤害分别按规则表倍率发放；击毁机器人按等级差公式结算击杀经验。
- 左下角 buff 图标改成按效果类别聚合，只保留同类里最强的一个，避免防御 / 冷却 / 攻击 / 回能类 buff 重复堆叠显示。

### 验证
- `dotnet build src\Simulator.ThreeD\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\buildcheck\Simulator.ThreeD`
## 2026-04-26 Migration Follow-up

### Unity first-person collision and assembly alignment
- Added runtime collision-surface sampling and wall-contact queries to the migration bridge session API.
- First-person camera now samples support height from multiple footprint points and backs off when terrain wall-contact is detected around the chassis envelope.
- Unity debug rendering now draws collision-part boxes from the same `EntityCollisionModel` used by projectile and movement obstacle resolution, so debug overlays and hit-chain geometry stay aligned.
- Mobile vehicle visual assembly now uses runtime wheel offsets, gimbal offsets, barrel forward placement, and body render width scale more directly instead of only symmetric fallback offsets.

## 2026-04-26 左下 HUD 与圆形车体预览

### 玩家状态 HUD
- `Simulator3dForm.cs` 将玩家状态面板从右下角改到左下角，并略微放宽矩形尺寸，给放大的圆形预览留出空间。
- 同文件去掉了这块 HUD 的外层底板填充，保留纯内容层，不再显示整块深色底框。

### 圆形车体预览
- 圆形机器人预览半径从 `40f` 提高到 `50f`。
- 内部 3D 车体预览相机同步拉近并收窄视场，使圆形中的车体显示更大。

## 2026-04-26 暂停菜单与吊射副视角倍率

### 暂停菜单
- `Simulator3dForm.cs` 删除了暂停弹窗中的 `F7 遥测` 与 `F1 指挥` 按钮，只保留 `继续 / 重新开始 / 返回大厅`。
- 观察者暂停条中的 `F7 遥测` 也同步移除，避免暂停界面继续承载这些入口。

### 吊射副视角
- `Simulator3dForm.LiveControl.cs` 将小相机窗口改为贴左下玩家 HUD 的上沿放置，和左下主 UI 保持同侧对齐。
- 常态副视角仍然保持 `3x` 语义。
- 按住右键且已锁定结构装甲板时，副视角会按装甲板宽高与镜头距离反推更小视场，持续放大到装甲板约占画面 `15%` 为止。

## 2026-04-26 吊射副视角卡顿回收

### GPU 副视角降负载
- `Simulator3dForm.GpuRenderer.cs` 中左下吊射副视角不再重复绘制详细地形缓存、设施批次、队伍顶灯与弹丸。
- 副视角仍保留真实地面基底、结构主体和动态实体，因此长焦观察画面还是真实场景，但不再做第二遍整图全量细节渲染。

## 2026-04-26 吊射副视角改为主画面裁剪

### 主画面裁剪副窗口
- `Simulator3dForm.GpuRenderer.cs` 中 GPU 吊射副视角不再走第二个 3D 相机重渲染。
- 新链路改为：主视角完成渲染后，从当前帧缓冲中按目标屏幕位置裁剪一块源图，再贴到左下副窗口放大显示。
- 常态副窗口仍按 `3x` 语义裁剪中心区域；按住右键并锁定结构装甲板时，会围绕装甲板屏幕包围框进一步缩小裁剪源区域，使装甲板在副窗口里约占 `15%` 画面。
- 这次改动的本质是“图像处理放大”，不是再渲染一次地图。

## 2026-04-26 单次离屏扩展渲染

### 副视角改单次场景渲染
- `Simulator3dForm.GpuRenderer.cs` 增加 FBO / Renderbuffer / 场景颜色纹理的初始化、重建、释放逻辑。
- 英雄吊射时，世界场景只渲染一次到离屏纹理；主视角显示该纹理顶部裁片，左下副视角显示同一张纹理里的目标裁片。

### 离屏画布按需扩展
- 离屏高度不再固定放大，而是按副视角目标裁切框的实际下探距离动态扩展。
- 当前策略只补足必要的下方区域，并做 16 像素对齐，避免额外浪费太多填充率。
- 投影矩阵改成仅向下扩展的非对称透视，保证主视角顶端裁片和原主窗口画面保持一致。

### GPU 主流程拆分
- `DrawGpuMatch(...)` 拆成“渲染世界场景”与“把场景纹理呈现到窗口”两段。
- HUD 仍然在最终窗口顶层绘制，不进入离屏纹理，因此不会被副视角扩展逻辑带偏。

### 构建验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 大厅固定场地 / 主菜单交互 / 文本裁剪

- 修复 `BeginMatchStartupSequence(...)` 开局时强制把模式改回 `full` 的问题，避免 `1v1 / 单位测试` 进入对局后重新接回大地图。
- `single_unit_test` 也改为固定场地逻辑，选车大厅中只有 `UC对战` 保留地图切换栏；其余模式统一显示固定场地文案。
- 大厅加载遮罩与场地标签改为按当前模式显示，不再在沙盒模式里泄露 `ActiveMapPreset` 的大地图名字。
- 主菜单按钮补充悬停高亮、展开收起过渡、面板渐变强调条，减少交互生硬感。
- 通用按钮文本改成 `TextRenderer` 居中绘制，并提高小按钮高度，缓解中文在上下边界内被裁掉的问题。
- 自瞄硬锁输出滤波按“玩家控制”分支加快响应，降低云台跟随迟滞感，但不改观测/EKF 链路。
- 选车大厅移除“对局模式”按钮区，模式切换只保留在主菜单入口，避免选车界面继续暴露模式选择。

## 2026-04-26 特殊对局模式链路接入

### 世界构建与设施布置
- `Simulator3dOptions.cs` 扩展 `match_mode` 归一化，补齐 `duel_1v1 / unit_test` 别名。
- `Simulator3dHost.cs` 新增基于 `blankCanvas` 的两类场景预设构建：
  - `duel_1v1`：平面地图，中轴线两道实体墙，同时补进弹丸阻挡用的 `wall` 障碍实体。
  - `unit_test`：平面地图，把蓝方基地、蓝方前哨站、中心能量机关重布到围绕玩家的 120° 测试布局。

### 运行时 AI / 主控链路
- `Simulator3dHost.cs` 将原 `single_unit_test` 主控焦点链路泛化到 `duel_1v1 / unit_test`。
- `duel_1v1` 运行时只保留红方主控与蓝方哨兵，蓝方哨兵会持续刷新 `attack` 战术目标并主动追击主控。
- `unit_test` / `duel_1v1` 下切换主控兵种时，会即时重排场景中的机器人位置，而不是沿用旧焦点的摆位。

### 伤害统计与能量机关测试
- `Simulator3dHost.cs` 新增单位测试伤害累计器：
  - 统计 `blue_outpost / blue_base` 的最近 1 秒伤害与总伤害。
  - 3 秒未继续受击时自动清零。
  - 每帧把基地/前哨站生命与存活状态拉回，保证可持续测试。
- `SimulationModels.cs` / `ArenaInteractionService.cs` 为能量机关测试补上 `EnergyTestAlwaysAvailable / EnergyTestForceLarge` 覆盖字段。
- `unit_test` 下能量机关支持重复激活；成功后自动退出 buff 态并回到可再次触发的待命态。

### UI 接入
- `Simulator3dForm.cs` 大厅新增 `完整 / 单兵 / 1v1 / 单测` 模式切换按钮。
- `Simulator3dForm.cs` 单位测试模式新增左上角场景面板，显示：
  - 前哨站 / 基地 最近 1 秒伤害、累计伤害。
  - 能量机关当前大小模式、状态、已激活圆盘数、最近环数、平均环数。

### 构建验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 吊射副视角 / 编辑器输入 / 强锁滤波修正

### 吊射副视角显示
- `Simulator3dForm.LiveControl.cs` 将常态吊射副视角固定回右下角小窗，部署态改为黑底全屏呈现。
- `Simulator3dForm.GpuRenderer.cs` 将部署态副视角改成“同一份低分辨率裁剪图放大全屏”，并保持最近邻采样，避免再次整场景重渲染。
- 部署态 GPU 子画面改用 `GL_LUMINANCE` 复制纹理，直接输出灰度图，保留低像素感。

### Python 外观编辑器输入
- `py_client/appearance_editor.py` 为字段行新增双击编辑入口：数值字段双击直接进入精确输入，枚举字段双击循环切换。
- 补充字段点击状态缓存，解决附加体 / 锚点 / 连杆参数点击后无法继续编辑的问题。
- 保留左右键原有统一写回链路，并通过 `py_compile` 验证脚本语法正常。

### 强锁输出滤波
- `TerrainMotionService.cs` 在硬锁输出层新增第二级滤波：先对 `pitch` 做输出限速+平滑，再对非吊射目标的 `yaw` 做额外限速+平滑。
- 英雄吊射结构目标继续保持 `yaw` 锁中心轴，不再跟着板子左右抖动，只平滑 `pitch` 输出。

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`
- `.venv\\Scripts\\python.exe -m py_compile py_client\\appearance_editor.py`

## 2026-04-26 英雄吊射锁轴 / 部署全屏副视角 / 比例读条 / 主界面中文化

### 英雄吊射锁轴
- `TerrainMotionService.cs`
  - 英雄吊射锁定前哨站/基地装甲板时，不再让 `yaw` 跟随旋转装甲板。
  - 现在统一锁到目标结构中心轴线方向，只保留 `pitch` 自动解算，等待装甲板转入最佳窗口再自动扳机。

### 部署模式全屏副视角
- `Simulator3dForm.LiveControl.cs`
  - 部署模式下，吊射副视角直接扩展为全屏。
  - 取消部署态下的小面板说明，减少重复 HUD 绘制负担。
  - 全屏副视角叠加灰度化遮罩，并额外绘制白色高亮的大弹丸预测轨迹。
- `Simulator3dForm.LiveControl.cs`
  - 吊射预览缓存窗口略放宽，减少高频重复解算带来的卡顿。

### 判罚读条
- `Simulator3dForm.cs`
  - 中心环与关键状态卡片的断电/锁管读条统一改成“恢复进度”比例，而不是剩余时间反向缩减。

### 主界面中文化
- `Simulator3dForm.cs`
  - 主菜单、选车大厅、预览卡片、部署提示、武器锁定标题等残留英文改为中文。

### 构建验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 视角圆环 / 越障上限 / 地形姿态 / 地图原色

### 第一视角中心圆环
- `Simulator3dForm.cs` 调大第一视角中心圆环半径，并把中心状态环与四分仪弧的透明度略微提高，保证中心 HUD 更明显但不至于遮挡视野。

### Hero / Sentry / Engineer 越障上限统一为 30cm
- `TerrainMotionService.cs` 新增统一的有效越障高度解析，标准麦轮车（英雄、哨兵、工程）无论寻路、碰撞检查还是实际 traversal，都强制封顶为 `0.30m`。
- 同时把 direct step 也纳入同一上限，避免局部逻辑绕过上限导致“理论 30cm、实际还能跨更高”的问题。

### 车体顺应地形姿态
- `TerrainMotionService.cs` 的 traversal 支撑姿态改为同时参考前后支撑高度和轮接触平面，不再只做单纯 pitch 插值。
- traversal 期间现在会一起解算 roll，并适当提高标准麦轮车的姿态响应速率，让车体在坡面、台阶、起伏地形上的俯仰/侧倾更自然。

### 蓝方地图光条保持模型原色
- `Simulator3dForm.cs` / `Simulator3dForm.GpuRenderer.cs` 关闭顶部额外 team neon 覆盖层。
- `Simulator3dForm.FineTerrainActors.cs` 去掉基地/前哨站光条的常态队伍追踪改色，保留锁定高亮、死亡熄灭、受击黑闪等必要状态反馈，其余时间直接使用模型原色。

### 构建验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 外观编辑链路补强

### 英雄小摄像头位姿
- `Simulator3dForm.AppearanceModel.cs` 调整英雄小摄像头挂点规则：位置仍挂在云台侧面，`yaw` 跟随云台，`pitch` 不再跟随枪管抬俯。
- 运行时外观渲染与 `py_client/appearance_editor.py` 的 3D 预览都同步补上了英雄小摄像头几何。

### 自定义外观数据通路
- `RobotAppearanceModels.cs` / `AppearanceProfileCatalog.cs` 新增 `custom_primitives`、`custom_anchors`、`custom_links` 三类共享外观数据，支持随外观 JSON 一起保存与加载。
- `Simulator3dForm.AppearanceModel.cs` 新增运行时自定义附加体渲染入口：可把长方体 / 圆柱附着到车体、云台、枪管、装甲板、小摄像头等父部件上，并支持锚点连杆显示。

### Python 外观编辑器
- `py_client/appearance_editor.py` 新增自定义附加体 / 锚点 / 连杆编辑入口，保存后会进入同一份外观预设。
- 新增颜色调色板快捷选择；保留原有数值通道编辑。
- 3D 预览补上了英雄小摄像头、车体后方灯条，以及自定义附加体 / 锚点 / 连杆的预览绘制。

### 构建验证
- `python -m py_compile py_client/appearance_editor.py`
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-26 吊射引导 / 副视角 / F8 中文化

### Python 外观编辑器崩溃修复
- `py_client/appearance_editor.py` 修复 `ModernGLAppearancePreview` 预览自定义附加体时误调应用层 `_profile_has_front_climb` 等方法导致的启动崩溃。
- 运行验证：从 `py_client` 目录直接启动 `appearance_editor.py`，程序可正常拉起并持续运行，未再出现该 `AttributeError`。

### 吊射引导与部署副视角
- `Simulator3dForm.cs` 与 `Simulator3dForm.LiveControl.cs` 调整英雄吊射控制分流：引导模式不再冻结 `pitch`，保留玩家手控 `pitch` 与手动火控；部署模式仍保留自动链路。
- 吊射副视角现在只在“按住右键”或“已进入部署模式”时显示，不再常驻。
- 部署模式下新增主视角黑场遮罩，副视角保留并叠加灰白观察层，同时显示“部署模式断电”提示。

### 副视角清晰度与 GPU 裁剪
- `Simulator3dForm.GpuRenderer.cs` 将吊射副视角的纹理采样切到最近邻，减少放大后发糊。
- GPU 主链路继续使用主画面裁剪方案，不增加第二次整图渲染。

### F8 中文化
- `Simulator3dForm.cs` 将 F8 左上角视觉解算面板改为中文标签，包括目标类型、观测来源、速度、角速度、提前量、经验修正与解算结果。
- 引导点旁的 `PRE` 文案同步改为“预测”。

## 2026-04-26 Python 外观编辑器左右键调值修复

### 字段边界容错
- `py_client/appearance_editor.py` 为字段编辑新增统一钳制逻辑：
  - `choice` 字段按选项数量自动钳制，不再依赖 `min/max`
  - 其他数字字段在缺失 `min/max` 时按当前值兜底，不再抛 `KeyError`
- `_adjust_selected(...)` 改为对 `step` 使用 `get('step', 1)`，避免动态字段缺省时左右键链路中断。

### 验证
- `python -m py_compile py_client/appearance_editor.py`
- 直接构造 `choice/custom_number` 字段调用 `_set_field_value(...)`，确认不再崩溃且值能写回。

## 2026-04-26 硬锁抗抖与左下 HUD 精简

### 硬锁自瞄输出滤波
- `TerrainMotionService.cs` 将 `ApplyAutoAimSolution(...)` 的硬锁输出层从“直接贴目标 / 纯一阶平滑”改成“死区 + 限速 + 平滑”三层：
  - 小角度抖动先走 yaw/pitch 死区，不再把观测微噪声直接放大到云台输出。
  - 对 yaw/pitch 增加每帧最大步进限制，避免硬锁时来回抽动。
  - 能量机关与普通装甲板分别使用不同的时间常数与速率上限。
- 这次没有改观测与预测链路，只改控制输出层，针对“预测点稳定但硬锁一开就抖”的问题。

### 左下 HUD
- `Simulator3dForm.cs` 将左下标题从“x号 + 角色名”改成纯角色名英文标题，例如 `Hero`。
- 机器人圆形头像上的编号徽章已移除。
- 经验弧重新接入左下 HUD，并在经验弧左端新增当前等级文本 `LvN`。

### 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`
## 2026-04-26 主菜单与吊射副视角

- 主菜单切到右侧折叠结构，保留单标题 `ARTINX A-Soul` 与三组入口：开始游戏、编辑器、退出。
- 主菜单背景接入 `tool_back_dark.mp4`，通过半透明黑色遮罩保持 50% 压暗效果。
- 吊射副视角常态回到左下角小窗，并对齐左下主状态面板上方。
- 部署模式下复用小窗裁切源画面做整屏最近邻放大，不再做第二次高分辨率重渲染，并叠加灰度化显示。
- 副视角锁板缩放逻辑改为部署模式同样参与，保证部署态也会围绕锁定装甲板做裁切。
- 选车大厅补充地图切换条，地图选择从主菜单移回大厅流程。
## 2026-04-26 主菜单统一回自绘 UI

- 删除 `Simulator.ThreeD` 对 `Microsoft.Web.WebView2` 的依赖，主菜单不再挂载 WinForms/WebView2 控件。
- `Simulator3dForm.MainMenuUi.cs` 改回轻量状态管理，只保留主菜单展开/折叠与模式切换状态。
- `Simulator3dForm.cs` 的主菜单改成统一自绘按钮链路，右侧折叠出：
  - 开始游戏 -> `UC对战 / 1v1对战 / 单位测试模式`
  - 编辑器 -> `地图编辑器 / 外观编辑器 / 规则编辑器`
  - 退出
- 特殊模式大厅文案补成固定场地显示：
  - `1v1 独立平面场地`
  - `单位测试独立场地`
- 确认 `duel_1v1` 与 `unit_test` 继续走 `blankCanvas` 派生的独立平面场景，不读取当前大地图 preset。
## 2026-04-26 大厅 UI / 视频恢复

- 定位到一个直接问题：启动时如果后台在准备大厅世界，`DrawLobbyWorldLoadingOverlay(...)` 会在非对局状态整屏覆盖，主菜单/大厅会表现成“没有 UI”。
- 现在改成：
  - `Lobby` 状态才使用整屏加载遮罩
  - `MainMenu` 状态只显示左上角小型加载角标，不再遮住菜单
- 新增 `Simulator3dForm.BackgroundVideo.cs`，把 `tool_back_dark.mp4` 直接走自绘视频背景链路，不再依赖 WinForms/WebView2 控件。
- `Simulator.ThreeD.csproj` 新增 `OpenCvSharp4` 与 `OpenCvSharp4.runtime.win`，用于只在主菜单/大厅解码背景视频帧。
- `DrawBackground(...)` 现在优先绘制视频帧，再叠加 50% 黑色遮罩；无视频帧时回退到原渐变背景。
- 背景视频显示改成“保持原比例 + 中心裁切铺满窗口”，不再直接拉伸变形。
- 去掉主线程每帧 `Bitmap.Clone()`，改成锁内直接绘制最新帧；同时将视频刷新节奏上限从 66fps 放宽到最高约 125fps，避免高帧视频被人为限速。
## 2026-04-26 独立场景 1v1 / 单位测试
### 场景预设
- `src/Simulator.ThreeD/Simulator3dHost.cs` 将 `duel_1v1` 与 `unit_test` 从 `blankCanvas` 派生逻辑改为真正的内存独立地图预设。
- `1v1` 改为 `5m x 5m` 平面场地，对局中间使用两段共线高墙形成中缝，不再依赖大地图设施坐标。
- `单位测试` 改为 `6m x 6m` 平面场地，仅保留蓝方基地、蓝方前哨站、能量机关三个交互装置，按 120 度围绕主控机器人摆放。
### 比例尺与碰撞
- `src/Simulator.Core/Gameplay/SimulationBootstrapService.cs` 与 `src/Simulator.ThreeD/Simulator3dHost.cs` 新增“内存场景使用自身比例尺”的分支，避免继续套用 `config.json` 中大地图的 `28m / 1576px` 比例。
- 场景地形改为 facet 平面与 raised wall facet 组合，保证平面渲染、移动支撑与墙体碰撞都在独立地图内闭环。
### 交互与热切换
- `src/Simulator.ThreeD/Simulator3dForm.cs` 在焦点沙盒模式下将 `1 / 2 / 3` 接为 `英雄 / 步兵 / 哨兵` 局内热切换。
- `单位测试` 左上面板补充热切换提示；伤害面板中的结构标签修正为 `前哨站 / 基地`。
### 构建验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`

## 2026-04-27 局内 3D 头像与吊射部署态优化

### 左下状态面板
- `Simulator3dForm.cs` 移除活跃 GPU 局内的简化朝向头像路径，恢复完整 3D 机器人头像。
- 完整头像改为按选中单位、阵营、外观和尺寸生成一次静态 bitmap 缓存；开局、重开和异步世界切换时失效，局内不再每帧重建 3D 小模型。

### 吊射部署态
- `Simulator3dForm.cs` 为英雄吊射部署新增副画面专用 HUD 分支，只保留副画面、部署提示、中心状态和左下状态面板，跳过锁框、战斗标记、事件流、调试层等局内额外内容。
- `Simulator3dForm.GpuRenderer.cs` 在部署态不再先贴主画面再二次拷贝灰度副画面，直接把副画面裁剪源呈现到整屏，并跳过血条、弹道拖尾和调试参考绘制。
- `Simulator3dForm.Structures.cs` 在部署态不再给被锁定装甲板绘制结构高亮框。

### UI 清晰度
- GPU HUD overlay 固定回 `1.0x` 逻辑分辨率并使用 1:1 纹理上传/最近邻呈现，避免局内 UI 因缩放表面而压缩发糊。
- `render_perf.log` 增加 `mode=deployment_subview` 标记，方便后续区分部署态帧耗时。

### 验证
- `dotnet build src\\Simulator.Core\\Simulator.Core.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.Core`
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -c Debug --no-restore -nologo -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`
- 结果：`0 warnings / 0 errors`。

## 2026-04-27 英雄吊射部署态主副画面回退
- 英雄部署态不再进入整屏副画面模式：主画面保留，吊射副画面恢复为左下小窗。
- 去掉部署态主画面黑场遮罩，恢复完整局内 overlay 链路，事件流、HUD 和调试层继续按正常局内流程绘制。
- GPU 扩展场景贴图路径改回“先主画面、再副画面小窗”，日志模式标记改为 `deployment_main_sub`。

## 2026-04-27 英雄装甲板中心点/前哨站停止/缓冲读条
- `SimulationCombatMath.cs` 新增前哨站环板实际角速度判定；环板停止后不再作为旋转关键结构处理，英雄不再强制轴线 yaw/等待旋转出手窗口，而是按普通装甲板直接瞄准。
- `Simulator3dForm.cs` 的 F8 蓝色瞄准点对英雄装甲板目标改为绘制当前装甲板几何中心，xy 直接取装甲板中心坐标。
- `TerrainMotionService.cs`、外观/碰撞/命中高度链路统一落地缓冲：所有可移动车体支持整车下压回弹，落地最大压缩 4cm，起跳蓄力压缩 2cm，第三人称相机跟随整车 body lift。
- 英雄退出部署模式时补中间读条；枪管热锁读条改为记录锁定起始热量后按比例推进，避免开头长时间不动。

## 2026-04-27 窗口图标 / 1v1 结算 / 缓冲与补给规则修正
- 详见 `docs/project-logs/2026-W18-02.md`。
- OpenTK 主窗口和 WinForms 运行窗体都加载 `DarkLogo.png` 作为窗口/任务栏图标。
- 平衡步兵起跳蓄力改为 `0.08s`，步兵蓄力下沉保持 `2cm`，落地缓冲保持 `4cm`，GPU 简模、第三人称相机、吊射副相机和云台挂点继续跟随整车 `BodyLiftM`。
- 1v1 结算框新增每局数据表和双方总计数据；局内事件流移除命中率显示。
- 吊射自动扳机放宽板面中心、深度和屏幕准星容差，让可发射窗口更容易触发。
- 哨兵可在补给区回血/买弹，也可在基地区买弹；梯形/高地防御按 `x0.50` 处理。
- 高地/坡面上的轮腿视觉高度和后腿 IK 目标增加保护，减少贴坡时的穿模和乱模。
- 验证：Core / ThreeD 两个 Debug 构建通过，`git diff --check -- <touched files>` 仅有仓库既有 CRLF 提示。

## 2026-04-27 编译修复 / 吊射窗口 / 相机云台同轴
- 详见 `docs/project-logs/2026-W18-02.md`。
- 修复 `LoadLargeTerrain` 入口可能在 `SimulationCombatMath.cs` 结构关键区判断处报 `CS0103: world` 的编译问题。
- 第一人称相机改为始终沿枪管轴线观察，云台 yaw、相机 yaw 和枪管朝向同源。
- 吊射发射窗口继续放宽：UI 层中心/深度/屏幕容差扩大，规则层加入弹丸直径余量和略宽的入射角窗口。
- 新增 `docs/project-logs/optimization-summary.md`，把近期性能、吊射、自瞄、地形、1v1 和验证日志提炼成简版摘要。
- 补充验证：`LoadLargeTerrain`、`Simulator.Core`、`Simulator.ThreeD` 独立输出目录构建均为 `0 warnings / 0 errors`；默认输出目录若被旧地形查看器占用，需要先关闭旧窗口再跑 `run_viewer.py`。

## 2026-04-27 中心准星 / 相机枪管锁死 / 右键自瞄
- 详见 `docs/project-logs/2026-W18-02.md`。
- 红色准星固定在窗口中心，不再跟随自瞄投影点漂移。
- 第一人称视角继续与云台/枪管轴线同源；硬锁靠自瞄输出转动云台让中心准星靠向锁定标记，引导模式只显示预测/锁定信息。
- 右键永远不作为开火输入：硬锁右键按住只跟踪，引导右键按住只显示自瞄框/预测提示；左键仍是唯一手动开火输入。
- 验证：Core / ThreeD 两个 Debug 独立输出目录构建通过，`0 warnings / 0 errors`。

## 2026-04-27 部署态副视角 / 吊射 pitch 保持
- 详见 `docs/project-logs/2026-W18-02.md`。
- 英雄吊射部署态改回黑底全屏副视角：只把锁定板附近的小窗源画面灰度化后最近邻放大到全屏，保留 HUD、事件流、调试层和大弹丸弹道高亮。
- GPU 路径不再把主画面贴回默认 framebuffer；扩展场景只提供副视角裁切源，默认画面清黑后贴副视角纹理。
- 吊射锁到第一块结构装甲板后保持云台 pitch，只有弹道在装甲板中心水平面上的高度误差过大才限速修正。
- 发射窗口同步检查中心水平面高度误差，避免窗口期弹丸偏离装甲板中心水平面。
- 验证：Core / ThreeD / LoadLargeTerrain 三个 Debug 独立输出目录构建通过，`0 warnings / 0 errors`。
