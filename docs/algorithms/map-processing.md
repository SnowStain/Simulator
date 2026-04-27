# 地图处理与缓存链路

## 目标

新地图链路的目标不是“把模型显示出来”这么简单，而是保证下面四件事一致：

- 编辑器里看到的位置
- 对局里看到的位置
- 规则层使用的位置
- 自瞄/碰撞使用的位置

这要求地图资产必须先完成 world-space 规范化。

## 资源组成

`rmuc2026` 这类新地图通常由以下几部分组成：

- `glb`
  - 几何、材质、组合体初始结构
- `json`
  - 组合体、互动组件、锚点、坐标系等注释
- `terraincache.lz4`
  - 为运行时快速加载准备的缓存数据

## 处理阶段

### 1. 资源读取

首先由 `Simulator.LoadLargeTerrain` 读取原始 GLB 与注释 JSON。

这一步只做解析，不做规则绑定。

### 2. world-space 规范化

规范化阶段会统一：

- 地图原点
- 轴方向
- 长宽比例与米制映射
- 组合体本地坐标与世界坐标的关系

这是避免“编辑器位置对、对局位置歪”的关键步骤。

### 3. 运行时缓存

规范化后的几何与注释会被压入运行时缓存：

- 静态地形三角面
- 组合体列表
- 互动组件列表
- 组件边界与命名索引

缓存采用 LZ4 的目的不是改变数据语义，而是减少每次进入对局时的解析成本。

### 4. 渲染与规则接入

运行时会分别生成：

- 渲染侧静态/动态网格
- 规则侧可用的设施定义
- 自瞄/命中侧目标生成器

这三者虽然用途不同，但都必须引用同一份规范化坐标结果。

## 组合体与互动组件

新地图中的设施不是单个模型，而是：

- 组合体负责整体位姿
- 互动组件负责命中、发光、规则反馈

因此加载顺序必须是：

1. 先加载组合体
2. 再把互动组件绑定到组合体
3. 再在运行时根据组合体位姿更新互动组件

如果只加载互动组件而不加载组合体，就会出现挂点错位、旋转中心错误等问题。

## 编辑器一致性

地图编辑器、单位测试器、正式对局都应该共用同一套底层读取与坐标变换逻辑。

否则任何一边单独做一次坐标翻转、镜像、轴交换，都会导致：

- 编辑器看起来正确
- 对局中整体偏移或关于长轴对称

## 关键文件

- `src/Simulator.LoadLargeTerrain/`
- `src/Simulator.ThreeD/Simulator3dForm.FineTerrainActors.cs`
- `maps/rmuc2026/`
- `run_viewer.py`

## 2026-04-25：F3 碰撞面与 GPU 地形投影统一

本次修正的原则是：terrain cache 的可见 GPU 三角面、`RuntimeGridData`、`TerrainCacheCollisionSurface` 和 F3 调试碰撞面必须使用同一套模型坐标到场地坐标的映射。

旧的 GPU 地形渲染还在使用 `ModelCenter` 做半场居中和镜像投影：

```text
sceneX = fieldLength / 2 - (modelX - modelCenterX) * xMetersPerModelUnit
sceneZ = fieldWidth / 2 - (modelZ - modelCenterZ) * zMetersPerModelUnit
```

而碰撞面和运行时网格使用的是 terrain cache 边界的线性映射：

```text
sceneX = (modelX - boundsMinX) / (boundsMaxX - boundsMinX) * fieldLength
sceneZ = (modelZ - boundsMinZ) / (boundsMaxZ - boundsMinZ) * fieldWidth
sceneY = max(0, modelY - boundsMinY) * verticalScale
```

两套投影同时存在时，F3 显示的碰撞三角面会和实际可见模型发生整体偏移或镜像。现在 `Simulator3dForm.GpuRenderer.cs` 的 terrain cache GPU 构建改为和碰撞面一致的 `Bounds.Min -> Bounds.Max` 线性投影，后续如果继续调整地图坐标，必须同时核对 GPU 地形、碰撞面、运行时网格和互动组件目标点是否仍然消费同一个 world-space 结果。

## 2026-04-25：以地图编辑器坐标为最终基准

后续复查发现，地图编辑器、组合体注解和局内互动组件使用的是同一套中心坐标体系，而静态地形 GPU 与精细碰撞面被改成 `Bounds.Min -> Bounds.Max` 后，虽然二者彼此一致，但会和组合体/互动组件产生整体偏移。

当前最终约定改为以地图编辑器为准：

```text
centeredX = (modelX - modelCenterX) * xMetersPerModelUnit
centeredZ = (modelZ - modelCenterZ) * zMetersPerModelUnit
worldX = fieldLength / 2 - centeredX
worldY = fieldWidth / 2 - centeredZ
height = max(0, modelY - modelMinY) * yMetersPerModelUnit
```

这个约定必须同时用于：

- terrain cache GPU 静态地形顶点
- `TerrainCacheCollisionSurface` 精细碰撞三角面
- 运行时栅格备用光栅化
- 组合体模型矩阵后的点位换算
- 能量机关、前哨站、基地等互动组件目标点

如果以后再次看到“编辑器正确、局内偏移”，优先检查是否有路径绕过了这套 `ModelCenter -> field center` 转换，而不是给单个组合体继续叠加经验偏移。
 
## 2026-04-25：运行时坐标权威源

地图编辑器导出的 annotation 中 `WorldScale` 是当前新地图的权威坐标来源。局内不应在加载 terrain cache 后重新用 GLB/cache bounds 推导另一套中心点，否则会出现静态地形、组合体、互动组件和碰撞面各自“看起来都自洽”，但彼此存在固定偏移的问题。

当前约定如下：

```text
centeredX = (modelX - annotation.ModelCenter.X) * annotation.XMetersPerModelUnit
centeredZ = (modelZ - annotation.ModelCenter.Z) * annotation.ZMetersPerModelUnit
worldX = fieldLength / 2 - centeredX
worldY = fieldWidth / 2 - centeredZ
height = max(0, modelY - annotation.ModelMinY) * annotation.YMetersPerModelUnit
```

所有以下路径都必须使用同一套转换：

- terrain cache GPU 静态地形顶点。
- `TerrainCacheCollisionSurface` 精细碰撞三角面。
- 能量机关、前哨站、基地顶部组合体视觉缓存。
- 组合体矩阵变换后的互动组件命中点与自瞄目标点。

如果后续再次出现“编辑器位置正确，局内整体偏移”，优先检查是否有新路径绕过了 annotation `WorldScale`，不要先给单个组合体叠加经验位移。

## 2026-04-25：运行时统一记录模型变换矩阵

为避免后续机器人、组合体、互动组件再次各自推导坐标，本轮在世界初始化阶段显式记录两套矩阵：

```text
model_to_scene_metric
model_to_world_units
```

`model_to_scene_metric` 把资源模型坐标直接转成局内渲染使用的米制场景坐标：

```text
sceneX = fieldLength / 2 - (modelX - modelCenterX) * xMetersPerModelUnit
sceneY = (modelY - modelCenterY) * yMetersPerModelUnit
sceneZ = fieldWidth / 2 - (modelZ - modelCenterZ) * zMetersPerModelUnit
```

`model_to_world_units` 在 `sceneX/sceneZ` 的基础上再除以 `metersPerWorldUnit`，得到规则层使用的 world-space 坐标：

```text
worldX = sceneX / metersPerWorldUnit
worldY = sceneZ / metersPerWorldUnit
height = sceneY
```

这两套矩阵记录在 `SimulationWorldState.RuntimeModelTransforms` 中，同时同步到初始化出来的机器人、基地、前哨站和能量机关实体。后续新增类似实体时，应优先读取这套矩阵，而不是重新从模型边界、图片尺寸或经验偏移中各算一份。
