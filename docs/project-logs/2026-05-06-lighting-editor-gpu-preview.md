# 2026-05-06 光照编辑器 GPU 实时预览

- `局内光照编辑器` 从纯参数表单升级为左右分栏编辑器：左侧嵌入 GPU 实时预览画面，右侧保留 `PropertyGrid` 参数编辑。
- 预览窗口使用 `Simulator3dForm` 的 GPU 渲染路径，和地图编辑器的嵌入式运行时预览思路一致。
- 编辑器使用独立 GPU 预览 host，避免预览窗口推进或干扰主对局仿真；参数变更时会同时同步到主 host 和预览 host。
- `Reload / Apply / Reset / 刷新预览` 会即时更新预览画面；`Apply` 仍负责持久化光照设置。
- 编辑器界面、状态提示、引导说明和属性面板标题全部改为中文，`PropertyGrid` 也通过 `Category / DisplayName / Description` 补齐中文说明，方便直接上手调整主光、补光和材质高光。
- 修复打开编辑器后主程序卡死的问题：嵌入式 GPU 预览改为编辑器本地 `Timer` 低频刷新，不再注册全应用共享的 `Application.Idle` 帧泵，避免模态编辑器和主窗体在同一 UI 线程抢占渲染循环。
- 右侧新增实时调色板，颜色项可直接弹出颜色选择器并联动 R/G/B 滑条与数值框；每次变化都会立刻写入 host、预览 host 并强制刷新预览画面，避免“改了颜色但画面没反应”的感觉。

验证：

- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`
