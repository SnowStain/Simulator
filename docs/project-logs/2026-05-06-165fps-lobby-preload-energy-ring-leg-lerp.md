# 2026-05-06 局内 165+ 预算 / 大厅预热 / 能量机关去重环 / 平衡腿插值

## 目标

- 把局内单位渲染开销压下来，争取把常态帧率推到 `165Hz+` 预算内。
- 让选车/大厅阶段先把完整地图预热好，再开放交互。
- 删除能量机关盘面外那层垂直 annulus，只保留原模型圆环。
- 平衡步兵腿的所有位置变化都走连续插值，不再瞬移。

## 这次改了什么

- `src/Simulator.ThreeD/Simulator3dForm.cs`
  - 恢复机器人/哨兵的 proxy 分流。
  - 近处、选中目标、锁定目标保留全细节，远处小目标切代理外观。
  - 增加大厅进入后的完整地图预热等待，先把地形和 GPU 资源准备完，再开放选车界面。
- `src/Simulator.ThreeD/Simulator3dForm.GpuRenderer.cs`
  - 禁用能量机关的规则高亮环绘制。
- `src/Simulator.ThreeD/Simulator3dForm.Structures.cs`
  - 禁用 CPU 回退路径的能量机关规则环。
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`
  - 禁用 fine-terrain 能量机关反馈中的额外环形覆盖层。
- `src/Simulator.ThreeD/Simulator3dForm.AppearanceModel.cs`
  - 平衡步兵腿改成更平滑的插值更新，并限制每帧最大位移。
- `src/Simulator.ThreeD/TerrainMotionService.cs`
  - traversal 开始时不再把可视腿目标强制重置为 NaN。

## 验证

- `dotnet build src/Simulator.ThreeD/Simulator.ThreeD.csproj -c Debug`
- 结果：`0 warnings / 0 errors`
