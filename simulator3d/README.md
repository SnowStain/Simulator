# simulator3d

独立 3D 对局程序包。这个包把 3D 入口、3D 菜单、选机流程和局内 ModernGL 渲染运行策略从 2D 主模拟器里拆开，按单独项目入口维护。

## 入口

```powershell
.venv\Scripts\python.exe -m simulator3d
```

兼容入口 `simulator_3d.py` 仍然存在，但只做一层转发。

## 包职责

- 加载独立 3D 运行配置
- 优先请求 C++ 原生场景后端，功能级别不足时回退到 ModernGL
- 提供独立的主菜单和赛前选机大厅
- 为 3D 手控模式设置专用调度策略和 AI 冻结范围
- 为原生 OpenGL 渲染层和 Bullet 物理层提供 Python 桥接边界

## 原生层构建

```powershell
.venv\Scripts\python.exe -m pip install pybind11
build_native_3d.bat
```

如果你要输出可分发的独立 3D 包：

```powershell
package_3d_simulator.bat
```

当前 rm26_native 模块已经暴露：

- NativeRendererBridge
- NativePhysicsBridge

当前 Python 运行时会根据模块上报的功能级别决定是否真正切换到原生后端。默认阈值是 10，因此这次提交会优先建立稳定的原生接入面，而不是直接拿未达标的原生实现替换现有可运行路径。

## 共享边界

以下模块继续作为 2D/3D 共用仿真核心：

- `core/`
- `control/`
- `rules/`
- `entities/`
- `map/`

3D 包只负责运行时组织和 3D 专属渲染流程，不复制仿真规则或实体逻辑。
