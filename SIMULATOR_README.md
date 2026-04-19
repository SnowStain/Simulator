# RoboMaster 2026 哨兵机器人决策与控制仿真模拟器

## 项目概述

本模拟器旨在提供一个基于RoboMaster 2026超级对抗赛规则的哨兵机器人决策与控制仿真环境。模拟器支持手动操作和AI接入，可用于算法开发、策略验证和训练测试。

## 核心功能

- **地图系统**：基于场地俯视图，支持地形识别和碰撞检测
- **实体管理**：完整的红蓝双方实体部署和状态跟踪
- **状态机模拟**：哨兵机器人全功能状态机实现
- **手动控制**：支持键盘/手柄实时操作
- **AI接入**：标准化接口支持算法接入
- **物理引擎**：支持基本物理模拟（速度、加速度、碰撞）
- **规则系统**：完整实现比赛规则（伤害机制、血量机制等）

## 地图系统

### 地图来源
- 基于 `场地-俯视图.png` 创建坐标系统
- 分辨率：需根据实际地图尺寸设定

### 地形定义
```json
{
"terrain_types": [
    "平地", "起伏路段", "台阶", "二级台阶", "墙", "飞坡", "狗洞", 
    "边界", "补给区", "梯形高地", "坡", "中央高地", "基地", "能量机关"
]
}
```

### 坐标系统
- 原点：地图左下角
- 单位：厘米 (cm)
- 坐标系：右手坐标系，X轴向右，Y轴向上

## 实体定义

### 友军（红方）实体
```json
{
"allied_entities": [
    {"type": "robot", "id": 1, "role": "步兵"},
    {"type": "robot", "id": 2, "role": "步兵"},
    {"type": "robot", "id": 3, "role": "步兵"},
    {"type": "robot", "id": 4, "role": "步兵"},
    {"type": "robot", "id": 7, "role": "工程"},
    {"type": "uav", "id": 1, "role": "无人机"},
    {"type": "sentry", "id": 1, "role": "哨兵"},
    {"type": "outpost", "id": 1, "role": "前哨站"},
    {"type": "base", "id": 1, "role": "基地"},
    {"type": "dart", "id": 1, "role": "飞镖"},
    {"type": "radar", "id": 1, "role": "雷达"}
]
}
```

### 敌军（蓝方）实体
```json
{
"enemy_entities": [
    {"type": "robot", "id": 1, "role": "步兵"},
    {"type": "robot", "id": 2, "role": "步兵"},
    {"type": "robot", "id": 3, "role": "步兵"},
    {"type": "robot", "id": 4, "role": "步兵"},
    {"type": "robot", "id": 7, "role": "工程"},
    {"type": "uav", "id": 1, "role": "无人机"},
    {"type": "sentry", "id": 1, "role": "哨兵"},
    {"type": "outpost", "id": 1, "role": "前哨站"},
    {"type": "base", "id": 1, "role": "基地"},
    {"type": "dart", "id": 1, "role": "飞镖"},
    {"type": "radar", "id": 1, "role": "雷达"}
]
}
```

### 初始位置配置
```json
{
"initial_positions": {
    "red": {
    "robot_1": {"x": 1000, "y": 2000, "angle": 0},
    "robot_2": {"x": 1200, "y": 2000, "angle": 0},
    "robot_3": {"x": 1400, "y": 2000, "angle": 0},
    "robot_4": {"x": 1600, "y": 2000, "angle": 0},
    "robot_7": {"x": 1800, "y": 2000, "angle": 0},
    "uav": {"x": 2000, "y": 1500, "height": 500},
    "sentry": {"x": 3000, "y": 1000, "angle": 90},
    "outpost": {"x": 2500, "y": 1500},
    "base": {"x": 3500, "y": 1500},
    "dart": {"x": 2800, "y": 1200},
    "radar": {"x": 3200, "y": 1200}
    },
    "blue": {
    "robot_1": {"x": 6000, "y": 2000, "angle": 180},
    "robot_2": {"x": 5800, "y": 2000, "angle": 180},
    "robot_3": {"x": 5600, "y": 2000, "angle": 180},
    "robot_4": {"x": 5400, "y": 2000, "angle": 180},
    "robot_7": {"x": 5200, "y": 2000, "angle": 180},
    "uav": {"x": 5000, "y": 1500, "height": 500},
    "sentry": {"x": 4000, "y": 1000, "angle": 270},
    "outpost": {"x": 4500, "y": 1500},
    "dart": {"x": 4200, "y": 1200},
    "radar": {"x": 4800, "y": 1200}
    }
}
}
```

## 哨兵状态机

### 底盘状态
```json
{
"chassis_states": [
    {"name": "上台阶", "conditions": "检测到台阶地形"},
    {"name": "飞坡", "conditions": "检测到飞坡地形"},
    {"name": "小陀螺", "conditions": "手动指令或AI决策"},
    {"name": "快速小陀螺", "conditions": "手动指令或AI决策"},
    {"name": "底盘跟随云台", "conditions": "云台锁定目标"}
]
}
```

### 云台状态
```json
{
"turret_states": [
    {"name": "自瞄", "conditions": "检测到目标"},
    {"name": "索敌", "conditions": "未检测到目标"}
]
}
```

### 狗腿状态
```json
{
"leg_states": [
    {"name": "平衡", "conditions": "始终保持"}
]
}
```

### 火控状态
```json
{
"fire_control_states": [
    {"name": "仅摩擦轮", "conditions": "hastarget = 0"},
    {"name": "发射", "conditions": "hastarget = 1"}
]
}
```

## 控制接口

### 手动控制接口
```python
# 键盘控制映射
key_mapping = {
    "W": "前进",
    "S": "后退",
    "A": "左移",
    "D": "右移",
    "Q": "左转",
    "E": "右转",
    "鼠标左键": "发射",
    "鼠标右键": "索敌",
    "空格": "小陀螺",
    "Shift": "快速小陀螺"
}
```

### AI接入接口

#### 输入数据结构
```json
{
"observation": {
    "timestamp": 1620000000.0,
    "self": {
    "position": {"x": 3000, "y": 1000, "angle": 90},
    "velocity": {"vx": 0, "vy": 0, "w": 0},
    "health": 100,
    "ammo": 100,
    "state": "索敌"
    },
    "allies": [
    {"id": 1, "type": "robot", "position": {"x": 1000, "y": 2000}, "health": 100},
    {"id": 2, "type": "robot", "position": {"x": 1200, "y": 2000}, "health": 100}
    ],
    "enemies": [
    {"id": 1, "type": "robot", "position": {"x": 6000, "y": 2000}, "health": 100, "detected": true},
    {"id": 2, "type": "robot", "position": {"x": 5800, "y": 2000}, "health": 100, "detected": false}
    ],
    "terrain": [
    {"type": "平地", "position": {"x": 3000, "y": 1000}},
    {"type": "坡", "position": {"x": 3500, "y": 1200}}
    ],
    "game_info": {
    "score": {"red": 0, "blue": 0},
    "time_remaining": 180,
    "mode": "normal"
    }
}
}
```

#### 输出动作格式
```json
{
"action": {
    "chassis": {
    "linear_x": 0.5,      # -1.0 到 1.0
    "linear_y": 0.0,      # -1.0 到 1.0
    "angular": 0.0        # -1.0 到 1.0
    },
    "turret": {
    "pitch": 0.0,         # -30 到 30 度
    "yaw": 90.0,          # 0 到 360 度
    "mode": "aiming"      # "aiming" 或 "searching"
    },
    "fire": {
    "enable": true,       # true 或 false
    "rate": 10            # 发射速率
    },
    "special": {
    "spin": false,        # 小陀螺
    "fast_spin": false,   # 快速小陀螺
    "follow_turret": true # 底盘跟随云台
    }
}
```

## 通信协议

### 实时通信
- **协议**: WebSocket
- **端口**: 8765
- **消息格式**: JSON
- **频率**: 50Hz

### 状态同步
```json
{
"type": "state_update",
"data": {
    "timestamp": 1620000000.0,
    "entities": [
    {
        "id": "red_sentry",
        "type": "sentry",
        "position": {"x": 3000, "y": 1000, "z": 0},
        "orientation": {"roll": 0, "pitch": 0, "yaw": 90},
        "velocity": {"vx": 0, "vy": 0, "vz": 0, "wx": 0, "wy": 0, "wz": 0},
        "health": 100,
        "ammo": 100,
        "state": "aiming",
        "target": {"id": "blue_robot_1", "position": {"x": 6000, "y": 2000}}
    }
    ]
}
```

## 物理引擎参数

### 运动参数
```json
{
"physics": {
    "max_speed": 3.5,              # 最大速度 (m/s)
    "max_acceleration": 2.0,       # 最大加速度 (m/s²)
    "max_angular_speed": 180,      # 最大角速度 (deg/s)
    "friction": 0.1,               # 摩擦系数
    "collision_damping": 0.8,      # 碰撞阻尼
    "step_height": 15,             # 最大台阶高度 (cm)
    "slope_limit": 30              # 最大爬坡角度 (deg)
}
}
```

## 规则系统

### 伤害机制
- 基于 `伤害机制.png` 实现
- 不同武器类型对应不同伤害值
- 装甲类型影响伤害减免

### 血量机制
- 基于 `血量机制.png` 实现
- 实体有初始血量和最大血量
- 血量为0时实体被摧毁

## AI模拟策略

### 蓝方AI行为模式
```json
{
"ai_strategy": {
    "infantry": {
    "behavior": "patrol",
    "waypoints": [{"x": 5000, "y": 1500}, {"x": 5500, "y": 2000}, {"x": 5000, "y": 2500}],
    "attack_range": 800,
    "retreat_health": 30
    },
    "engineer": {
    "behavior": "support",
    "repair_targets": ["outpost", "base"],
    "recharge_rate": 10
    },
    "sentry": {
    "behavior": "defend",
    "guard_area": {"center": {"x": 4000, "y": 1000}, "radius": 500},
    "priority_targets": ["sentry", "robot"]
    },
    "uav": {
    "behavior": "scout",
    "scan_pattern": "spiral",
    "altitude": 500,
    "detection_range": 1000
    }
}
}
```

## 使用说明

### 启动模拟器
```bash
# 启动模拟器服务器
python simulator.py --port 8765 --map "场地-俯视图.png"

# 启动AI客户端（红方哨兵）
python ai_client.py --role sentry --team red --host localhost --port 8765

# 启动手动控制客户端
python manual_client.py --role sentry --team red --host localhost --port 8765
```

### 配置文件示例
```json
{
"simulator_config": {
    "map_path": "场地-俯视图.png",
    "terrain_data": "terrain.json",
    "entity_config": "entities.json",
    "physics_params": "physics.json",
    "rules_config": "rules.json",
    "ai_strategy": "ai_strategy.json"
}
}
```

## 开发指南

### 添加新实体类型
```python
class NewEntity(Entity):
    def __init__(self, entity_id, position, **kwargs):
        super().__init__(entity_id, "new_type", position, **kwargs)
        self.special_property = kwargs.get("special_property", 0)
    
    def update(self, dt, environment):
        # 实现实体逻辑
        pass
```

### 接入自定义AI
```python
class CustomAI:
    def __init__(self):
        self.model = load_model("my_model.pth")
    
    def get_action(self, observation):
        # 使用模型预测动作
        state = self.preprocess_observation(observation)
        action = self.model.predict(state)
        return self.format_action(action)
```

## 性能要求

- **帧率**: 至少50 FPS
- **延迟**: 控制指令延迟 < 50ms
- **资源占用**: CPU < 20%, 内存 < 512MB

## 扩展计划

- [ ] 支持多机器人协同控制
- [ ] 添加视觉模拟（摄像头视角）
- [ ] 实现3D物理引擎
- [ ] 支持机器学习训练环境
- [ ] 添加比赛回放功能
- [ ] 实现自动战术分析

## 技术栈

- **核心语言**: Python 3.8+
- **物理引擎**: PyBullet 或 Box2D
- **网络通信**: WebSocket (asyncio)
- **图形渲染**: Pygame 或 OpenGL
- **数据处理**: NumPy, Pandas
- **AI框架**: PyTorch, TensorFlow (可选)

## 联系信息

- 项目维护者: RoboMaster Team
- 版本: v1.0.0
- 最后更新: 2026-03-24
- 基于规则: RoboMaster 2026 机甲大师超级对抗赛比赛规则手册V1.4.0