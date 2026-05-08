# 2026-05-06 GPU HUD primitives

## 追加：OpenGk 顶部 HUD 动态形状 GPU 化

- 继续处理“形状多、变化频繁”的局内 HUD：OpenGk 顶部 HUD 的基地/前哨血条、单位卡片血条、弹药小图标从 GDI 绘制路径迁移到 OpenGL 屏幕空间 primitive。
- 顶部 HUD 缓存键不再包含基地/前哨血量百分比和单位实时血量，避免血量持续变化时反复重建整张 GDI 缓存位图；文字、轮廓、剪影和交互命中区域仍保留在现有缓存路径。
- GPU 现在在 overlay 纹理贴图后直接绘制动态填充条和弹药图形，保持视觉质量，不压缩、不降低刷新精度，也不改变对局规则或交互效率。
- 非 GPU/快速平面回退路径仍保留 GDI 填充绘制，避免兼容模式下 HUD 缺失。
- 当前进程检查发现外部高占用主要来自 `WorldOfWarships64`、多个 `NVIDIA Overlay`、`KuGou`、`LZTray`、`baidupinyin` 等，不属于模拟器项目进程；项目侧继续优先降低 overlay 重绘/上传压力。
- 当前日志判断：`frame_pump.log` 仍显示目标 `targetHz=240.0`，不是代码层固定锁 60；`render_perf.log` 的尖峰主要来自 overlay draw/upload、单位绘制和局部 facility/terrain 构建，继续以减少 GDI overlay 动态重绘为主。

验证：
- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`

## 追加：OpenTK 启动窗口不可见修复

- 问题现象：启动后用户侧看不到窗口，容易判断为“窗口不弹出/报错”。检查事件日志发现历史崩溃来自 `OpenTK.Windowing.Desktop` 的 `GLFWException`，信息为 `WGL: Failed to clear current context: 句柄无效`，位置在 `SimulatorOpenTkApplication.Run()` 结束后的 `GameWindow.Dispose()`。
- 同时发现 OpenTK 启动路径使用 `StartVisible=false`，并在窗口可见前同步执行 `Simulator3dForm` 主菜单预热、地形/GPU cache 等重初始化；在 RMUC2026 大地图上这会造成窗口长期无句柄或不可见。
- 修复：OpenTK 窗口销毁改为显式 `try/finally`，对 GLFW/WGL 上下文清理异常写入 `logs/opentk_shutdown.log`，不再让清理阶段异常导致进程崩溃。
- 修复：主程序入口新增 OpenTK 启动失败回退，OpenTK 初始化失败时自动回退 WinForms 主窗体，保证至少有可见窗口。
- 修复：OpenTK 主窗体改为 `StartVisible=true`，并取消可见前的同步 `PrepareInitialPresentation()`；外部 OpenTK 兼容运行时也跳过构造阶段 `WarmStartMainMenuWorld()`，避免窗口显示前阻塞在大地图预热。
- 验证：重新构建后启动新进程，`Simulator.ThreeD` 获得窗口句柄 `1472442`，标题为 `RM ARTINX A-Soul模拟器`，进程 `Responding=True`。系统中仍存在一个旧的 40KB 无句柄残留进程，`Stop-Process` 返回拒绝访问，判断为此前崩溃/WER 残留，不属于新启动流程。

验证：
- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`

- 继续推进局内高频 HUD 元素的 GPU 化，保持画质、规则和对局效率不变，只减少 GDI+ 形状绘制与 overlay 重复提交压力。
- 第一人称基础准星已改为 OpenGL 直接绘制。
- 英雄部署蓄力环、复活/断电/热锁等状态进度环已将圆环本体迁到 OpenGL primitive；文字提示仍保留在 overlay 路径，避免改动信息表达密度。
- 中心四象限血量/功率/超电/热量环、缓冲能量短弧和自瞄引导圈继续迁到 OpenGL primitive；GPU 路径下 GDI 只保留对应文字说明，不再重复绘制这些高频变化形状。
- 设施绘制顺序继续使用缓存签名，避免每帧重复排序。

验证：

- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`
# 2026-05-06 GPU HUD primitives
