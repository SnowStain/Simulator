# 2026-05-06 局内单位渲染缓存优化

## 目标

- 保持画质不变，压低局内单位全细节绘制耗时，减少帧时间尖峰。

## 改动

- `src/Simulator.ThreeD/Simulator3dForm.AppearanceModel.cs`
- 为装甲板和装甲灯条增加按 `RobotAppearanceProfile` 的派生几何缓存，避免每帧重复建表和分配。
- 轮组解析改为固定数组，去掉每帧 `HashSet` / `List` 分配。
- 枪管摩擦轮解析改为固定数组，去掉临时集合和重复小数组分配。
- 接入现成的装甲板可见性判定，背面或被遮挡的装甲板不再提交几何。

- `src/Simulator.ThreeD/Simulator3dForm.cs`
- GPU 圆柱绘制复用按段数缓存的单位圆采样点，减少大量 `sin/cos` 重算。

## 验证

- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`

