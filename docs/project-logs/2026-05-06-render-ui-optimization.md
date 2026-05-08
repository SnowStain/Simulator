# 2026-05-06 局内渲染、光照与 GPU UI 优化记录

## 背景

- 用户要求优化局内帧率，但不能通过压缩质量、降低对局效率或减少规则/命中判定精度来换帧率。
- 先查看 `docs/README.md`、`docs/architecture/README.md` 与 `docs/documentation-workflow.md`，确认渲染、HUD 和局内编辑器属于 `src/Simulator.ThreeD`，规则与世界状态属于 `src/Simulator.Core`。
- 查看 `frame_pump.log` 后确认当前局内并非稳定锁死 `60 FPS`：已有 `targetHz=144.0`、实际约 `110-130 FPS` 的窗口。
- 查看 `render_perf.log` 后确认剩余热点集中在 overlay 位图绘制/上传，以及设施、结构体路径的重复 CPU 准备工作。

## 局内光照编辑器

- 新增 `Simulator3dLightingSettings`，配置持久化到 `simulator.sim3d_lighting`。
- 新增 `LightingEditorForm`，支持局内调整主光、补光、材质高光、光源方向和启用状态。
- `Simulator3dHost` 提供 `GetLightingSettings()` 与 `UpdateLightingSettings()`，和现有配置服务同一路径保存。
- 主菜单“编辑器”下新增 `局内光照编辑器`。
- GPU 动态实体/设施批次每帧同步光照配置，继续使用 OpenGL 固定管线 `glLightfv`、`glMaterialfv`、`glColorMaterial` 与 normal array。
- `Enabled=false` 会真正关闭固定管线光照。

## GPU UI 优化

- 第一人称基础准星从 GDI+ overlay 改为 OpenGL primitive 直接绘制。
- `DrawGpuOverlayLayer()` 在贴完 overlay 纹理后调用 GPU HUD primitive 绘制，避免基础准星每次画进位图再上传。
- `DrawCrosshair()` 在 GPU overlay 路径下跳过基础准星 GDI 绘制，只保留蓄力环、锁框、引导标记等仍需要现有文字/投影逻辑的部分。
- 这是增量迁移方案：先把高频且形状简单的 UI 元素迁到 GPU，不一次性重写字体和复杂 HUD 布局。

## 设施渲染 CPU 开销

- CPU/回退设施渲染原先每帧执行 `OrderByDescending(FacilitySortDepth)`，会重复构建设施脚印并排序。
- 新增 `_facilityDrawBuffer` 和 `_facilityDrawOrderSignature`，按地图资产签名与相机位置量化值缓存设施绘制顺序。
- 地形/地图资产重建时清空设施绘制顺序签名，避免地图切换后复用旧顺序。

## 质量约束

- 不降低地形、模型、光照或 UI 分辨率。
- 不降低规则仿真频率、AI 决策频率或命中判定精度。
- 优先移除重复工作、缓存稳定数据、减少 GDI+ 到 OpenGL 的上传压力。

## 修改文件

- `src/Simulator.ThreeD/Simulator3dLightingSettings.cs`
- `src/Simulator.ThreeD/LightingEditorForm.cs`
- `src/Simulator.ThreeD/Simulator3dHost.cs`
- `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- `docs/project-log.md`

## 验证

- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`。
