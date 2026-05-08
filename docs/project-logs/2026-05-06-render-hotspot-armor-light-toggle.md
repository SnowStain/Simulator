# 2026-05-06 渲染热点、装甲板与光影开关

## 背景
- 用户反馈局内常态帧率只有约 20fps，并要求检查日志热点。
- 同时要求装甲板不能被裁剪、增加光影开关按钮、所有摩擦轮方向反向。

## 日志结论
- `render_perf.log` 中常见帧为 `unit=20-35ms`，`detail(full=9/20-35ms)` 是持续热点。
- `frame_pump.log` 显示掉帧阶段 `gapAvg` 经常在 40ms 左右，和渲染日志中的完整单位模型耗时一致。
- overlay / modal 偶发上传帧会把单帧推到 49ms 甚至更高，但不是持续 20fps 的唯一来源。

## 修改
- `src/Simulator.ThreeD/Simulator3dForm.AppearanceModel.cs`
  - 装甲板完整绘制，不再通过 `IsArmorPlateVisibleFromCamera(...)` 过滤。
  - 去掉装甲板每帧 `List` 收集和距离排序，减少完整模型路径上的 CPU 开销。
  - 摩擦轮 `SpinSign` 整体反向，英雄多轮组和普通双摩擦轮保持一致。
- `src/Simulator.ThreeD/Simulator3dHost.cs`
  - 新增 `LightingEnabled` 和 `ToggleLightingEnabled()`，复用现有 `Simulator3dLightingSettings` 持久化。
- `src/Simulator.ThreeD/Simulator3dForm.cs`
  - 主菜单编辑器分组新增 `光影：开 / 光影：关` 按钮。
- `src/Simulator.ThreeD/Simulator3dForm.OpenGkUi.cs`
  - OpenGk 主菜单同步新增光影开关按钮与命中区域。

## 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o build_verify\\launcher_builds\\debug\\threeD`
- 结果：通过，`0 warnings / 0 errors`。
