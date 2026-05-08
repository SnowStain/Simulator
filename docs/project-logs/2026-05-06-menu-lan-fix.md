# 2026-05-06 主菜单入口与多人房间反馈修复

- 修复 OpenGk 主菜单皮肤下“编辑器”折叠项没有显示 `局内光照编辑器` 的问题；旧主菜单入口原本已存在，本次补齐当前默认主菜单路径。
- 补齐 OpenGk 菜单的点击命中逻辑，`局内光照编辑器` 会触发既有 `menu_open_lighting_editor` action。
- 多人房间入口打开时现在会立即显示“准备创建/准备加入”的明确状态。
- 创建房间、连接主机、失败提示改为正常中文，避免按钮点击后因为状态乱码或无可见反馈被误认为无响应。

验证：

- `dotnet build Simulator.sln -c Debug --no-restore -nologo`
- 结果：`0 warnings / 0 errors`
