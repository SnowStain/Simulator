# 架构分层与 Message Bus

## 目标

把仓库逐步收敛成三层：

- `Engine`：3D 绘制、CPU/GPU 调度、物理与平台相关能力
- `Rules`：比赛规则、世界状态推进、命中判定、自瞄求解、设施交互
- `User`：玩家输入、AI 决策、编辑器和 HUD

## 调用方向

- `User -> Rules`：提交输入、战术命令、编辑操作
- `Rules -> Engine`：提交渲染所需的只读快照、调试可视化、物理查询请求
- `Engine -> User`：回传观测、窗口事件、渲染反馈

不推荐的方向：

- `Engine` 直接读写规则状态
- `User` 直接跳过 `Rules` 修改底层物理或渲染状态

## Message Bus 骨架

本次新增了 [`MessageBus.cs`](/E:/Artinx/260111new/Simulator/src/Simulator.Core/Architecture/MessageBus.cs)，先提供一个轻量级的层间消息边界：

- `IMessageBus.Subscribe<T>()`
- `IMessageBus.Publish<T>()`
- `BusEnvelope<TMessage>`：明确消息来源层、目标层、时间戳

当前它还是“骨架”而不是全量接线，目的是先把后续重构时最容易互相穿透的边界定出来。

## 建议的后续接线顺序

1. 先把 HUD/F8/调试可视化改成订阅 Rules 发出的只读解算结果。
2. 再把 AI 与玩家输入统一成 `UserCommandIssued` 类消息进入 Rules。
3. 最后再把渲染器和物理查询抽成 Engine 服务，只暴露消息或接口，不暴露内部状态对象。
