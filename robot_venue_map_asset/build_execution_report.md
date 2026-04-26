# Robot Venue Map Asset Build Report

- 执行时间: 2026-04-19T18:42:17
- 输入文件: E:\Artinx\260111new\sentryBehaviorSimulator\maps\basicMap\map.json
- 输入 schema: project_map_schema
- 场地尺寸: 28.0m x 15.0m
- 设施数量: 56

## 执行步骤

1. 读取输入文件副本并完成 schema 识别与标准化适配。
2. 生成地面与四周边界基础网格。
3. 按设施逐个构建 box / polygon prism / cylinder / ramp 网格。
4. 合并网格、清理重复顶点与退化面，生成视觉网格与碰撞网格。
5. 导出 GLB / OBJ / NPZ / JSON 资产，并回读校验。

## 校验摘要

- 最终状态: passed
- 主模型面数: 1144
- 主模型大小: 67764 bytes
- GLB 加载耗时: 4.133 ms
- 构建峰值内存: 38.113 MB

## 异常与告警

- info: 输入不是任务书标准 schema，已按项目地图格式自动适配。
- info [boundary_outer]: boundary 设施由基础边界墙体统一表示，单独设施网格跳过。

## 使用示例

### Ursina

```python
from ursina import Ursina, Entity
app = Ursina()
Entity(model='robot_venue_map_asset/venue_map_ursina.glb', collider='mesh')
app.run()
```

### PyBullet

```python
import pybullet as p
p.connect(p.GUI)
p.createCollisionShape(p.GEOM_MESH, fileName='robot_venue_map_asset/venue_map_pybullet.obj')
```

### ModernGL

```python
import numpy as np
payload = np.load('robot_venue_map_asset/venue_map_moderngl_data.npz')
vertices = payload['vertices']
indices = payload['indices']
```

### 障碍物查询接口示例

```python
import json
metadata = json.load(open('robot_venue_map_asset/venue_map_metadata.json', encoding='utf-8'))
blocking = [item for item in metadata['facilities'] if item.get('block_movement')]
```

### 增量修改示例

```python
# 修改某个 facility 后重新运行构建脚本即可；流程按设施线性处理，不需要全量栅格重建。
```