# 2026-05-06 局内小地图 / 机器人面裁剪 / 选车页光照开关

## 背景

- 用户要求右下角小地图放大 `1.15`，并在原本数值圆环外侧实时显示底盘相对云台的方向。
- 用户要求选车界面的机器人选项改成单列，同时补上局内光照开关。
- 用户还要求继续压低局内渲染负担，优先砍掉不影响外观的冗余面。

## 改动

- `src/Simulator.ThreeD/Simulator3dForm.LiveControl.cs`
- 右下角小地图整体放大到 `1.15x`。
- `src/Simulator.ThreeD/Simulator3dForm.cs`
- 在中央数值圆环外圈增加 `15°` 白色圆弧，实时显示底盘相对云台的方向，正前方对应顶部，左右/前后偏转会跟随方位变化。
- `src/Simulator.ThreeD/Simulator3dForm.OpenGkUi.cs`
- 选车页机器人选项改成单列排列，避免双列压缩。
- 选车页增加局内光照切换按钮，和主菜单中的光照入口保持一致。
- `src/Simulator.ThreeD/Simulator3dForm.AppearanceModel.cs`
- 对机器人和结构体实体面做面向相机裁剪，尽量跳过背向相机的不可见外层面。
- `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`
- GPU 固体面提交同步做同样的背面裁剪，减少动态几何提交量。

## 验证

- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug -o build_verify/launcher_builds/debug/threeD`
- 结果：`0 warnings / 0 errors`
