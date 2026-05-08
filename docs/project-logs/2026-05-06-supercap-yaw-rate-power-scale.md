# 2026-05-06 超电转速补偿随功率上调

## 背景
- 用户要求“功率提高后，超电补偿的转速也一样提高”。
- 原逻辑中哨兵 / 工程开启超电后，标准麦轮小陀螺的额外转速上限是固定 `840 deg/s`，没有继续跟随更高功率档位放大。

## 修改
- `src/Simulator.ThreeD/TerrainMotionService.cs`
  - 新增 `SuperCapYawRateReferencePowerW = 100W` 和 `SuperCapYawRateMaxPowerScale = 1.55`。
  - `ResolveStandardGyroYawRateCapDegPerSec(...)` 在超电开启时按当前有效底盘功率上限放大小陀螺转速上限。
  - 未提高功率时仍保持原有 `840 deg/s` 起点；功率提高后，超电转速补偿同步上升。

## 验证
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o artifacts\\buildcheck\\Simulator.ThreeD`
- `dotnet build src\\Simulator.ThreeD\\Simulator.ThreeD.csproj -p:UseSharedCompilation=false -nodeReuse:false -o build_verify\\launcher_builds\\debug\\threeD`
- 结果：通过，`0 warnings / 0 errors`。
