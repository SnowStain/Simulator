import math
from copy import deepcopy


def calculate_hit_probability(engine, shooter, target, distance=None):
    if getattr(target, 'invincible_timer', 0.0) > 0 or getattr(target, 'dynamic_invincible', False):
        return 0.0
    if engine.is_base_shielded(target):
        return 0.0

    if engine._can_use_hero_structure_lob_fire(shooter, target) and getattr(target, 'type', None) in {'outpost', 'base'}:
        raw_probability = calculate_hero_deployment_hit_probability(engine, shooter, target, distance)
        return engine._stabilize_hit_probability(
            shooter,
            target,
            raw_probability,
            field_name='hero_deployment_hit_probability',
            target_field_name='hero_deployment_hit_probability_target_id',
            time_field_name='hero_deployment_hit_probability_updated_at',
        )

    if distance is None:
        distance = math.hypot(
            target.position['x'] - shooter.position['x'],
            target.position['y'] - shooter.position['y'],
        )
    max_distance = engine.get_range(shooter.type)
    if distance > max_distance:
        return 0.0
    assessment = engine.evaluate_auto_aim_target(shooter, target, distance=distance, require_fov=False)
    if not assessment.get('can_track'):
        return engine._stabilize_hit_probability(
            shooter,
            target,
            0.0,
            field_name='auto_aim_hit_probability',
            target_field_name='auto_aim_hit_probability_target_id',
            time_field_name='auto_aim_hit_probability_updated_at',
        )

    probability = get_auto_aim_accuracy(engine, distance, target)
    if getattr(shooter, 'robot_type', '') == '英雄' and not engine._can_use_hero_structure_lob_fire(shooter, target):
        translating_speed = engine._meters_to_world_units(engine.rules['shooting'].get('motion_thresholds', {}).get('translating_target_speed_mps', 0.45))
        shooter_speed = math.hypot(float(getattr(shooter, 'velocity', {}).get('vx', 0.0)), float(getattr(shooter, 'velocity', {}).get('vy', 0.0)))
        if shooter_speed >= translating_speed:
            probability *= float(engine.rules['shooting'].get('hero_mobile_accuracy_mult', 0.7))
    probability *= hit_probability_multiplier(engine, shooter)
    if is_fast_spinning_target(engine, target):
        probability *= float(engine.rules['shooting'].get('fast_spin_hit_multiplier', 0.6))
    half_fov = max(1.0, float(engine.rules['shooting'].get('auto_aim_fov_deg', 50.0)) * 0.5)
    angle_diff = abs(float(assessment.get('angle_diff', 0.0)))
    if angle_diff > half_fov:
        decay_limit = half_fov * max(1.2, float(engine.rules['shooting'].get('hit_probability_tracking_decay_fov_mult', 2.4)))
        min_tracking_mult = engine._clamp01(engine.rules['shooting'].get('hit_probability_min_tracking_mult', 0.58))
        if angle_diff >= decay_limit:
            probability *= min_tracking_mult
        else:
            progress = (angle_diff - half_fov) / max(decay_limit - half_fov, 1e-6)
            probability *= 1.0 - (1.0 - min_tracking_mult) * engine._smoothstep01(progress)
    return engine._stabilize_hit_probability(
        shooter,
        target,
        engine._clamp01(probability),
        field_name='auto_aim_hit_probability',
        target_field_name='auto_aim_hit_probability_target_id',
        time_field_name='auto_aim_hit_probability_updated_at',
    )


def calculate_hero_deployment_hit_probability(engine, shooter, target, distance=None):
    if not engine._can_use_hero_structure_lob_fire(shooter, target):
        return 0.0
    if target is None or not target.is_alive() or target.team == shooter.team or target.type not in {'outpost', 'base'}:
        return 0.0
    if not engine.has_line_of_sight(shooter, target):
        return 0.0
    if distance is None:
        distance = math.hypot(target.position['x'] - shooter.position['x'], target.position['y'] - shooter.position['y'])
    deployment_rules = engine.rules.get('shooting', {}).get('hero_deployment_structure_fire', {})
    max_probability = float(deployment_rules.get('max_hit_probability', 0.7))
    min_probability = float(deployment_rules.get('min_hit_probability', 0.2))
    optimal_distance = engine._meters_to_world_units(float(deployment_rules.get('optimal_distance_m', 8.0)))
    falloff_end_distance = engine._meters_to_world_units(float(deployment_rules.get('falloff_end_distance_m', 20.0)))
    if distance <= optimal_distance:
        return max_probability
    if distance >= falloff_end_distance:
        return min_probability
    progress = (distance - optimal_distance) / max(falloff_end_distance - optimal_distance, 1e-6)
    return max_probability + (min_probability - max_probability) * progress


def get_auto_aim_accuracy(engine, distance, target):
    profile = engine.rules['shooting'].get('auto_aim_accuracy', {})
    near_limit = engine._meters_to_world_units(1.0)
    mid_limit = engine._meters_to_world_units(5.0)
    far_limit = max(mid_limit + 1.0, float(engine.auto_aim_max_distance))

    thresholds = engine.rules['shooting'].get('motion_thresholds', {})
    translating_speed = max(engine._meters_to_world_units(thresholds.get('translating_target_speed_mps', 0.45)), 1e-6)
    spinning_speed = max(float(thresholds.get('spinning_angular_velocity_deg', 45.0)), 1e-6)
    linear_speed = math.hypot(float(target.velocity['vx']), float(target.velocity['vy']))
    angular_speed = abs(float(getattr(target, 'angular_velocity', 0.0)))

    translating_factor = engine._smoothstep01((linear_speed - translating_speed * 0.2) / max(translating_speed * 0.9, 1e-6))
    spinning_factor = engine._smoothstep01((angular_speed - spinning_speed * 0.25) / max(spinning_speed * 0.9, 1e-6))

    near_probability = float(profile.get('near_all', 0.30))
    mid_fixed = float(profile.get('mid_fixed', 0.60))
    mid_spin = float(profile.get('mid_spin', mid_fixed))
    mid_translating = float(profile.get('mid_translating_spin', mid_spin))
    far_fixed = float(profile.get('far_fixed', 0.10))
    far_spin = float(profile.get('far_spin', far_fixed))
    far_translating = float(profile.get('far_translating_spin', far_spin))

    mid_probability = mid_fixed + (mid_spin - mid_fixed) * spinning_factor
    mid_probability += (mid_translating - mid_probability) * translating_factor
    far_probability = far_fixed + (far_spin - far_fixed) * spinning_factor
    far_probability += (far_translating - far_probability) * translating_factor

    if distance <= near_limit:
        return near_probability
    if distance <= mid_limit:
        blend = engine._smoothstep01((float(distance) - near_limit) / max(mid_limit - near_limit, 1e-6))
        return near_probability + (mid_probability - near_probability) * blend

    blend = engine._smoothstep01((float(distance) - mid_limit) / max(far_limit - mid_limit, 1e-6))
    return mid_probability + (far_probability - mid_probability) * blend


def classify_target_motion(engine, target):
    thresholds = engine.rules['shooting'].get('motion_thresholds', {})
    translating_speed = engine._meters_to_world_units(thresholds.get('translating_target_speed_mps', 0.45))
    spinning_angular_velocity = thresholds.get('spinning_angular_velocity_deg', 45.0)

    linear_speed = math.hypot(target.velocity['vx'], target.velocity['vy'])
    angular_speed = abs(getattr(target, 'angular_velocity', 0.0))
    chassis_state = getattr(target, 'chassis_state', 'normal')
    is_spinning = angular_speed >= spinning_angular_velocity or chassis_state in {'spin', 'fast_spin'}
    is_translating = linear_speed >= translating_speed

    if is_spinning and is_translating:
        return 'translating_spin'
    if is_spinning:
        return 'spin'
    if is_translating:
        return 'translating_spin'
    return 'fixed'


def is_fast_spinning_target(engine, target):
    if target is None:
        return False
    if float(getattr(target, 'evasive_spin_timer', 0.0)) > 0.0:
        return True
    if getattr(target, 'chassis_state', 'normal') == 'fast_spin':
        return True
    threshold = float(engine.rules['shooting'].get('fast_spin_threshold_deg_per_sec', 300.0))
    return abs(float(getattr(target, 'angular_velocity', 0.0))) >= threshold


def trigger_evasive_spin(engine, target, shooter):
    if target is None or target.type not in {'robot', 'sentry'}:
        return
    ammo_type = getattr(shooter, 'ammo_type', '17mm') if shooter is not None else '17mm'
    if ammo_type not in {'17mm', '42mm'}:
        return
    target.last_damage_source_id = getattr(shooter, 'id', None)
    target.evasive_spin_timer = float(engine.rules['shooting'].get('evasive_spin_duration', 1.8))
    target.evasive_spin_rate_deg = float(engine.rules['shooting'].get('evasive_spin_rate_deg', 420.0))
    target_id = getattr(target, 'id', 0)
    try:
        parity_seed = int(target_id)
    except (TypeError, ValueError):
        parity_seed = sum(ord(char) for char in str(target_id))
    direction = -1.0 if parity_seed % 2 == 0 else 1.0
    if shooter is not None:
        facing_rad = math.radians(float(getattr(target, 'angle', 0.0)))
        relative_x = target.position['x'] - shooter.position['x']
        relative_y = target.position['y'] - shooter.position['y']
        cross = relative_x * math.sin(facing_rad) - relative_y * math.cos(facing_rad)
        direction = 1.0 if cross >= 0.0 else -1.0
    target.evasive_spin_direction = direction
    target.chassis_state = 'fast_spin'


def describe_target_motion(engine, target):
    labels = {
        'fixed': '固定靶',
        'spin': '小陀螺',
        'translating_spin': '平动靶',
    }
    return labels.get(classify_target_motion(engine, target), '固定靶')


def hit_probability_multiplier(engine, shooter):
    multiplier = 1.0
    if getattr(shooter, 'fort_buff_active', False):
        multiplier *= engine.rules['fort']['hit_probability_mult']
    if getattr(shooter, 'terrain_buff_timer', 0.0) > 0:
        multiplier *= engine.rules['terrain_cross']['hit_probability_mult']
    if engine._is_respawn_weak(shooter):
        multiplier *= 0.75
    return multiplier


def calculate_damage(engine, shooter, target):
    if getattr(target, 'invincible_timer', 0.0) > 0 or getattr(target, 'dynamic_invincible', False):
        return 0.0
    if engine.is_base_shielded(target):
        return 0.0

    target_damage_table = engine.damage_system.get(target.type, {})
    projectile_key = resolve_projectile_damage_key(engine, shooter)
    damage = float(target_damage_table.get(projectile_key, 0))
    damage *= damage_dealt_multiplier(engine, shooter)
    damage *= damage_taken_multiplier(engine, target)
    return max(0.0, round(damage, 2))


def resolve_projectile_damage_key(engine, shooter):
    if getattr(shooter, 'ammo_type', '17mm') == '42mm':
        return 'bullet_42mm'
    if shooter.type == 'sentry':
        return 'bullet_17mm'

    robot_type = getattr(shooter, 'robot_type', '') or ''
    if robot_type == '英雄':
        return 'bullet_42mm'
    return 'bullet_17mm'


def damage_dealt_multiplier(engine, shooter):
    multiplier = float(getattr(shooter, 'dynamic_damage_dealt_mult', 1.0))
    if getattr(shooter, 'terrain_buff_timer', 0.0) > 0:
        multiplier *= engine.rules['terrain_cross']['damage_dealt_mult']
    if engine._is_respawn_weak(shooter):
        multiplier *= engine.rules['respawn']['weaken_damage_dealt_mult']
    return multiplier


def damage_taken_multiplier(engine, target):
    multiplier = float(getattr(target, 'dynamic_damage_taken_mult', 1.0))
    if target.type == 'sentry':
        multiplier *= engine._resolve_posture_effect(target)['damage_mult']
    if getattr(target, 'robot_type', '') == '工程' and engine.game_time >= 180.0:
        multiplier *= 0.5
    if getattr(target, 'fort_buff_active', False):
        multiplier *= engine.rules['fort']['damage_taken_mult']
    if getattr(target, 'terrain_buff_timer', 0.0) > 0:
        multiplier *= engine.rules['terrain_cross']['damage_taken_mult']
    if engine._is_respawn_weak(target):
        multiplier *= engine.rules['respawn']['weaken_damage_taken_mult']
    if engine.radar_marks.get(target.id, 0.0) >= 1.0:
        multiplier *= engine.rules['radar']['vulnerability_mult']
    return multiplier


def resolve_posture_effect(engine, sentry):
    posture = getattr(sentry, 'posture', 'mobile')
    effect = deepcopy(engine.posture_effects.get(posture, engine.posture_effects['mobile']))
    decay_time = engine.rules['sentry'].get('posture_decay_time', 180.0)
    if getattr(sentry, 'posture_active_time', 0.0) >= decay_time:
        for key, value in effect.get('decay', {}).items():
            effect[key] = value
    return effect


def update_sentry_posture_and_heat(engine, entities, dt):
    for entity in entities:
        if entity.type != 'sentry':
            continue

        entity.posture_cooldown = max(0.0, getattr(entity, 'posture_cooldown', 0.0) - dt)
        entity.posture_active_time = getattr(entity, 'posture_active_time', 0.0) + dt

        posture_effect = resolve_posture_effect(engine, entity)
        total_cooling_mult = posture_effect['cool_mult']
        entity.dynamic_power_capacity_mult = float(posture_effect.get('power_mult', 1.0))
        if getattr(entity, 'fort_buff_active', False):
            total_cooling_mult *= engine.rules['fort']['cooling_mult']
        if getattr(entity, 'terrain_buff_timer', 0.0) > 0:
            total_cooling_mult *= engine.rules['terrain_cross']['cooling_mult']

        effective_power_limit = max(0.0, float(getattr(entity, 'max_power', 0.0)) * float(getattr(entity, 'dynamic_power_capacity_mult', 1.0)))
        entity.power = min(float(getattr(entity, 'power', 0.0)), effective_power_limit)


def update_heat_mechanism(engine, entities, dt):
    detection_hz = max(1.0, float(engine.rules.get('shooting', {}).get('heat_detection_hz', 10.0)))
    tick_interval = 1.0 / detection_hz
    for entity in entities:
        if entity.type not in {'robot', 'sentry'}:
            continue
        if not entity.is_alive():
            continue
        if not engine.entity_has_barrel(entity):
            continue
        entity.heat_cooling_accumulator = float(getattr(entity, 'heat_cooling_accumulator', 0.0)) + dt
        tick_count = int(entity.heat_cooling_accumulator / tick_interval)
        if tick_count <= 0:
            continue
        entity.heat_cooling_accumulator -= tick_count * tick_interval
        cooling_per_tick = engine.get_current_cooling_rate(entity) / detection_hz
        if cooling_per_tick > 0.0:
            entity.heat = max(0.0, float(getattr(entity, 'heat', 0.0)) - cooling_per_tick * tick_count)
        if getattr(entity, 'heat_lock_state', 'normal') == 'cooling_unlock' and float(getattr(entity, 'heat', 0.0)) <= 1e-6:
            engine._set_heat_lock_state(entity, 'normal')
            engine._log(f'{entity.id} 发射机构冷却归零，解除热量锁定', entity.team)


def update_radar_marks(engine, entities, map_manager, dt):
    for entity_id in list(engine.radar_marks.keys()):
        decay = engine.rules['radar']['mark_decay_per_sec'] * dt
        engine.radar_marks[entity_id] = max(0.0, engine.radar_marks.get(entity_id, 0.0) - decay)

    for entity in entities:
        if entity.type != 'radar' or not entity.is_alive():
            continue

        radar_team = entity.team
        for target in entities:
            if target.team == radar_team or not target.is_alive():
                continue

            distance = math.hypot(target.position['x'] - entity.position['x'], target.position['y'] - entity.position['y'])
            if distance < engine.rules['radar']['range']:
                gain = engine.rules['radar']['mark_gain_per_sec'] * dt
                engine.radar_marks[target.id] = min(1.0, engine.radar_marks.get(target.id, 0.0) + gain)


def spawn_projectile_trace(engine, shooter, target, trace_payload=None):
    if shooter is None:
        return
    if trace_payload is not None:
        trace = dict(trace_payload)
        trace.setdefault('team', getattr(shooter, 'team', None))
        trace.setdefault('ammo_type', getattr(shooter, 'ammo_type', '17mm'))
        engine.projectile_traces.append(trace)
        if len(engine.projectile_traces) > engine.projectile_trace_limit:
            engine.projectile_traces = engine.projectile_traces[-engine.projectile_trace_limit:]
        return
    if target is None:
        return
    ammo_type = getattr(shooter, 'ammo_type', '17mm')
    start_x = float(shooter.position['x'])
    start_y = float(shooter.position['y'])
    end_x = float(target.position['x'])
    end_y = float(target.position['y'])
    start_height_m = engine._shooter_view_height_m(shooter)
    end_height_m = engine._target_armor_height_m(target)
    distance = math.hypot(end_x - start_x, end_y - start_y)
    speed = max(1.0, engine._projectile_speed_world_units(ammo_type))
    lifetime = min(0.75, max(0.10, distance / speed * 1.25))
    engine.projectile_traces.append({
        'team': getattr(shooter, 'team', None),
        'ammo_type': ammo_type,
        'start': (start_x, start_y),
        'end': (end_x, end_y),
        'start_height_m': start_height_m,
        'end_height_m': end_height_m,
        'elapsed': 0.0,
        'lifetime': lifetime,
    })
    if len(engine.projectile_traces) > engine.projectile_trace_limit:
        engine.projectile_traces = engine.projectile_traces[-engine.projectile_trace_limit:]


def point_to_segment_distance_3d(engine, point, start, end):
    del engine
    px, py, pz = point
    x1, y1, z1 = start
    x2, y2, z2 = end
    dx = x2 - x1
    dy = y2 - y1
    dz = z2 - z1
    length_sq = dx * dx + dy * dy + dz * dz
    if length_sq <= 1e-6:
        return math.sqrt((px - x1) ** 2 + (py - y1) ** 2 + (pz - z1) ** 2), start
    ratio = ((px - x1) * dx + (py - y1) * dy + (pz - z1) * dz) / length_sq
    ratio = max(0.0, min(1.0, ratio))
    closest = (x1 + dx * ratio, y1 + dy * ratio, z1 + dz * ratio)
    distance = math.sqrt((px - closest[0]) ** 2 + (py - closest[1]) ** 2 + (pz - closest[2]) ** 2)
    return distance, closest


def projectile_collision_height_m(engine, sample):
    del engine
    terrain_height = float(sample.get('height_m', 0.0))
    if bool(sample.get('vision_blocked', False)):
        return terrain_height + max(float(sample.get('vision_block_height_m', 0.0)), 0.05)
    if bool(sample.get('move_blocked', False)):
        return terrain_height + 0.35
    return terrain_height


def projectile_hits_obstacle(engine, point3d):
    map_manager = engine._map_manager()
    if map_manager is None:
        return False
    sample = map_manager.sample_raster_layers(point3d[0], point3d[1])
    if not bool(sample.get('move_blocked', False)) and not bool(sample.get('vision_blocked', False)):
        return False
    return float(point3d[2]) <= projectile_collision_height_m(engine, sample) + 0.02


def reflect_projectile_direction(engine, previous_point, direction, step_length):
    map_manager = engine._map_manager()
    if map_manager is None:
        return (-direction[0], -direction[1], direction[2] * 0.72)

    def probe(candidate_direction):
        probe_point = (
            previous_point[0] + candidate_direction[0] * max(step_length, 1.0),
            previous_point[1] + candidate_direction[1] * max(step_length, 1.0),
            previous_point[2] + candidate_direction[2] * max(step_length, 1.0),
        )
        return not projectile_hits_obstacle(engine, probe_point)

    candidates = [
        (-direction[0], direction[1], direction[2] * 0.72),
        (direction[0], -direction[1], direction[2] * 0.72),
        (-direction[0], -direction[1], direction[2] * 0.65),
    ]
    reflected = next((candidate for candidate in candidates if probe(candidate)), candidates[-1])
    length = math.sqrt(reflected[0] ** 2 + reflected[1] ** 2 + reflected[2] ** 2)
    if length <= 1e-6:
        return (-direction[0], -direction[1], 0.0)
    return (reflected[0] / length, reflected[1] / length, reflected[2] / length)


def find_projectile_hit_target(engine, shooter, start_point, end_point, entities, preferred_target=None):
    map_manager = engine._map_manager()
    if map_manager is None:
        return None, None
    ammo_type = getattr(shooter, 'ammo_type', '17mm')
    hit_radius = max(0.5, engine._meters_to_world_units(engine._projectile_diameter_m(ammo_type) * 0.5))
    best_hit = None
    best_distance = float('inf')
    candidate_entities = []
    if preferred_target is not None:
        candidate_entities.append(preferred_target)
    candidate_entities.extend(entity for entity in entities if entity is not preferred_target)
    for target in candidate_entities:
        if target is None or not target.is_alive() or target.team == shooter.team or target.id == shooter.id:
            continue
        target_center_height = engine._target_armor_height_m(target)
        broad_radius = engine._projectile_target_broad_radius_world(target, hit_radius)
        distance_to_center, _ = point_to_segment_distance_3d(
            engine,
            (float(target.position['x']), float(target.position['y']), target_center_height),
            start_point,
            end_point,
        )
        if distance_to_center > broad_radius:
            continue
        for plate in engine.get_entity_armor_plate_targets(target):
            distance, hit_point = point_to_segment_distance_3d(engine, (plate['x'], plate['y'], plate['z']), start_point, end_point)
            if distance > hit_radius:
                continue
            travel_distance = math.sqrt((hit_point[0] - start_point[0]) ** 2 + (hit_point[1] - start_point[1]) ** 2 + (hit_point[2] - start_point[2]) ** 2)
            if travel_distance < best_distance:
                best_distance = travel_distance
                best_hit = (target, hit_point)
    return best_hit if best_hit is not None else (None, None)


def build_projectile_trace_payload(engine, shooter, path_points, speed_scale=1.0):
    points = [(float(point[0]), float(point[1]), float(point[2])) for point in path_points if point is not None]
    if len(points) < 2:
        return None
    total_length = 0.0
    for start, end in zip(points, points[1:]):
        total_length += math.sqrt(
            (engine._world_units_to_meters(end[0] - start[0])) ** 2
            + (engine._world_units_to_meters(end[1] - start[1])) ** 2
            + (end[2] - start[2]) ** 2
        )
    ammo_type = getattr(shooter, 'ammo_type', '17mm')
    speed = max(1.0, engine._projectile_speed_mps(ammo_type) * max(0.35, float(speed_scale)))
    return {
        'team': getattr(shooter, 'team', None),
        'ammo_type': ammo_type,
        'start': (points[0][0], points[0][1]),
        'end': (points[-1][0], points[-1][1]),
        'start_height_m': points[0][2],
        'end_height_m': points[-1][2],
        'path_points': tuple(points),
        'elapsed': 0.0,
        'lifetime': min(1.05, max(0.12, total_length / speed * 1.35)),
    }


def resolve_projectile_aim_point(engine, shooter, target):
    if shooter is None or target is None:
        return None
    best_plate = None
    best_distance = None
    for plate in engine.get_entity_armor_plate_targets(target):
        distance = math.hypot(float(plate['x']) - float(shooter.position['x']), float(plate['y']) - float(shooter.position['y']))
        if best_distance is None or distance < best_distance:
            best_distance = distance
            best_plate = plate
    if best_plate is None:
        return {
            'x': float(target.position['x']),
            'y': float(target.position['y']),
            'z': engine._target_armor_height_m(target),
            'target_id': target.id,
        }
    return {
        'x': float(best_plate['x']),
        'y': float(best_plate['y']),
        'z': float(best_plate['z']),
        'target_id': target.id,
        'plate_id': best_plate.get('id'),
    }


def find_projectile_hit_target_metric_segment(engine, shooter, start_point_m, end_point_m, entities, preferred_target=None):
    ammo_type = getattr(shooter, 'ammo_type', '17mm')
    hit_radius_m = max(0.0085, engine._projectile_diameter_m(ammo_type) * 0.5)
    best_hit = None
    best_distance = float('inf')
    candidate_entities = []
    if preferred_target is not None:
        candidate_entities.append(preferred_target)
    candidate_entities.extend(entity for entity in entities if entity is not preferred_target)
    for target in candidate_entities:
        if target is None or not target.is_alive() or target.team == shooter.team or target.id == shooter.id:
            continue
        target_center = (
            engine._world_units_to_meters(float(target.position['x'])),
            engine._world_units_to_meters(float(target.position['y'])),
            engine._target_armor_height_m(target),
        )
        broad_radius_m = math.hypot(
            float(getattr(target, 'body_length_m', getattr(target, 'body_size_m', 0.42))) * 0.5 + float(getattr(target, 'armor_plate_gap_m', 0.02)) + hit_radius_m,
            float(getattr(target, 'body_width_m', getattr(target, 'body_size_m', 0.42))) * 0.5 + float(getattr(target, 'armor_plate_gap_m', 0.02)) + hit_radius_m,
        )
        distance_to_center, _ = point_to_segment_distance_3d(engine, target_center, start_point_m, end_point_m)
        if distance_to_center > broad_radius_m:
            continue
        for plate in engine.get_entity_armor_plate_targets(target):
            plate_point_m = (
                engine._world_units_to_meters(float(plate['x'])),
                engine._world_units_to_meters(float(plate['y'])),
                float(plate['z']),
            )
            distance, hit_point_m = point_to_segment_distance_3d(engine, plate_point_m, start_point_m, end_point_m)
            if distance > hit_radius_m:
                continue
            travel_distance = math.sqrt(
                (hit_point_m[0] - start_point_m[0]) ** 2
                + (hit_point_m[1] - start_point_m[1]) ** 2
                + (hit_point_m[2] - start_point_m[2]) ** 2
            )
            if travel_distance < best_distance:
                best_distance = travel_distance
                best_hit = (target, engine._world_point_from_metric(hit_point_m))
    return best_hit if best_hit is not None else (None, None)


def simulate_ballistic_projectile(engine, shooter, entities, target=None, aim_point=None, allow_ricochet=False):
    game_engine = getattr(engine, 'game_engine', None)
    physics_engine = getattr(game_engine, 'physics_engine', None) if game_engine is not None else None
    backend_simulator = getattr(physics_engine, 'simulate_ballistic_projectile', None)
    if callable(backend_simulator):
        return backend_simulator(
            shooter,
            entities,
            engine,
            target=target,
            aim_point=aim_point,
            allow_ricochet=allow_ricochet,
        )
    map_manager = engine._map_manager()
    if aim_point is not None:
        preferred_pitch_deg = float(getattr(shooter, 'gimbal_pitch_deg', 0.0))
        start_point = engine._shooter_muzzle_point(shooter, pitch_deg=preferred_pitch_deg)
        target_point = (
            float(aim_point.get('x', shooter.position['x'])),
            float(aim_point.get('y', shooter.position['y'])),
            float(aim_point.get('z', start_point[2])),
        )
        yaw_deg = math.degrees(math.atan2(target_point[1] - start_point[1], target_point[0] - start_point[0]))
        pitch_deg = preferred_pitch_deg
        for _ in range(2):
            pitch_deg = engine._solve_ballistic_pitch_deg(
                shooter,
                start_point,
                target_point,
                preferred_pitch_deg=preferred_pitch_deg,
            )
            start_point = engine._shooter_muzzle_point(shooter, pitch_deg=pitch_deg)
            yaw_deg = math.degrees(math.atan2(target_point[1] - start_point[1], target_point[0] - start_point[0]))
    else:
        yaw_deg = float(getattr(shooter, 'turret_angle', shooter.angle))
        pitch_deg = float(getattr(shooter, 'gimbal_pitch_deg', 0.0))
        start_point = engine._shooter_muzzle_point(shooter, pitch_deg=pitch_deg)
    yaw_rad = math.radians(yaw_deg)
    pitch_rad = math.radians(pitch_deg)
    speed_mps = max(1e-6, engine._projectile_speed_mps(getattr(shooter, 'ammo_type', '17mm')))
    velocity_m = [
        math.cos(pitch_rad) * math.cos(yaw_rad) * speed_mps,
        math.cos(pitch_rad) * math.sin(yaw_rad) * speed_mps,
        math.sin(pitch_rad) * speed_mps,
    ]
    current_point_m = list(engine._metric_point_from_world(start_point))
    path_points = [start_point]
    max_range_m = engine._world_units_to_meters(float(engine.get_range(getattr(shooter, 'type', 'robot'))))
    simulation_dt = max(0.002, min(0.02, float(engine.rules.get('shooting', {}).get('projectile_simulation_dt_sec', 0.01))))
    gravity = engine._projectile_gravity_mps2()
    drag = max(0.0, engine._projectile_drag_coefficient(getattr(shooter, 'ammo_type', '17mm')))
    hit_target = None
    hit_point = None
    traveled_m = 0.0
    bounce_count = 0
    speed_scale = 1.0
    while traveled_m < max_range_m:
        speed = math.sqrt(velocity_m[0] ** 2 + velocity_m[1] ** 2 + velocity_m[2] ** 2)
        if speed <= 1e-6:
            break
        dt = min(simulation_dt, max(0.002, 0.08 / max(speed, 1e-6)))
        accel_x = -drag * speed * velocity_m[0]
        accel_y = -drag * speed * velocity_m[1]
        accel_z = -gravity - drag * speed * velocity_m[2]
        next_point_m = (
            current_point_m[0] + velocity_m[0] * dt + 0.5 * accel_x * dt * dt,
            current_point_m[1] + velocity_m[1] * dt + 0.5 * accel_y * dt * dt,
            current_point_m[2] + velocity_m[2] * dt + 0.5 * accel_z * dt * dt,
        )
        next_velocity = (
            velocity_m[0] + accel_x * dt,
            velocity_m[1] + accel_y * dt,
            velocity_m[2] + accel_z * dt,
        )
        next_point_world = engine._world_point_from_metric(next_point_m)
        candidate_target, candidate_hit_point = find_projectile_hit_target_metric_segment(
            engine,
            shooter,
            tuple(current_point_m),
            next_point_m,
            entities,
            preferred_target=target,
        )
        if candidate_target is not None and candidate_hit_point is not None:
            hit_target = candidate_target
            hit_point = candidate_hit_point
            path_points.append(hit_point)
            break
        if map_manager is not None and projectile_hits_obstacle(engine, next_point_world):
            path_points.append(next_point_world)
            if allow_ricochet and bounce_count == 0:
                segment_distance_m = math.sqrt(
                    (next_point_m[0] - current_point_m[0]) ** 2
                    + (next_point_m[1] - current_point_m[1]) ** 2
                    + (next_point_m[2] - current_point_m[2]) ** 2
                )
                reflected = reflect_projectile_direction(engine, path_points[-2], (velocity_m[0], velocity_m[1], velocity_m[2]), engine._meters_to_world_units(speed * dt))
                reflected_speed = max(0.1, math.sqrt(reflected[0] ** 2 + reflected[1] ** 2 + reflected[2] ** 2))
                velocity_m = [
                    reflected[0] / reflected_speed * speed * 0.62,
                    reflected[1] / reflected_speed * speed * 0.62,
                    reflected[2] / reflected_speed * speed * 0.52,
                ]
                current_point_m = list(engine._metric_point_from_world(next_point_world))
                traveled_m += segment_distance_m
                speed_scale *= 0.62
                bounce_count += 1
                continue
            break
        path_points.append(next_point_world)
        traveled_m += math.sqrt(
            (next_point_m[0] - current_point_m[0]) ** 2
            + (next_point_m[1] - current_point_m[1]) ** 2
            + (next_point_m[2] - current_point_m[2]) ** 2
        )
        current_point_m = [float(next_point_m[0]), float(next_point_m[1]), float(next_point_m[2])]
        velocity_m = [float(next_velocity[0]), float(next_velocity[1]), float(next_velocity[2])]
    trace_payload = build_projectile_trace_payload(engine, shooter, path_points, speed_scale=speed_scale)
    return {'trace': trace_payload, 'hit_target': hit_target, 'hit_point': hit_point}


def simulate_player_projectile(engine, shooter, entities, target=None, aim_point=None, allow_ricochet=False):
    return simulate_ballistic_projectile(engine, shooter, entities, target=target, aim_point=aim_point, allow_ricochet=allow_ricochet)


def update_projectile_traces(engine, dt):
    active_traces = []
    for trace in list(getattr(engine, 'projectile_traces', [])):
        trace['elapsed'] = float(trace.get('elapsed', 0.0)) + float(dt)
        if float(trace.get('elapsed', 0.0)) <= float(trace.get('lifetime', 0.0)):
            active_traces.append(trace)
    engine.projectile_traces = active_traces