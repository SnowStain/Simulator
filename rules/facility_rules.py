import math
import random


def supply_claimable_ammo(engine, entity):
    if engine._primary_ammo_key(entity) is None:
        return 0
    interval = float(engine.rules['supply'].get('ammo_interval', 60.0))
    ammo_gain = supply_ammo_gain(engine, entity)
    if interval <= 0:
        return 0
    generated = int(engine.game_time // interval) * ammo_gain
    claimed = int(getattr(entity, 'supply_ammo_claimed', 0))
    return max(0, generated - claimed)


def supply_ammo_gain(engine, entity):
    supply_rules = engine.rules.get('supply', {})
    default_gain = int(supply_rules.get('ammo_gain', 100))
    if getattr(entity, 'ammo_type', None) == '42mm':
        return int(supply_rules.get('ammo_gain_42mm', max(1, default_gain // 10)))
    return int(supply_rules.get('ammo_gain_17mm', default_gain))


def claim_supply_ammo(engine, entity):
    claimable = supply_claimable_ammo(engine, entity)
    if claimable <= 0:
        return 0
    engine._add_allowed_ammo(entity, claimable)
    entity.supply_ammo_claimed = int(getattr(entity, 'supply_ammo_claimed', 0)) + claimable
    return claimable


def mining_duration(engine, exchange=False):
    mining_rules = engine.rules.get('mining', {})
    if exchange:
        return random.uniform(
            float(mining_rules.get('exchange_duration_min_sec', 10.0)),
            float(mining_rules.get('exchange_duration_max_sec', 15.0)),
        )
    return random.uniform(
        float(mining_rules.get('mine_duration_min_sec', 10.0)),
        float(mining_rules.get('mine_duration_max_sec', 15.0)),
    )


def reset_dynamic_effects(engine, entity):
    entity.dynamic_damage_taken_mult = 1.0
    entity.dynamic_damage_dealt_mult = 1.0
    entity.dynamic_cooling_mult = 1.0
    entity.dynamic_power_recovery_mult = 1.0
    entity.dynamic_power_capacity_mult = 1.0
    entity.dynamic_invincible = False
    entity.active_buff_labels = []
    expire_buff_path_progress(engine, entity)

    pending_label_map = {
        'terrain_highland_red': '红方高地跨越起点已触发',
        'terrain_highland_blue': '蓝方高地跨越起点已触发',
        'terrain_road_red': '红方公路跨越起点已触发',
        'terrain_road_blue': '蓝方公路跨越起点已触发',
        'terrain_fly_slope_red': '红方飞坡跨越起点已触发',
        'terrain_fly_slope_blue': '蓝方飞坡跨越起点已触发',
        'terrain_slope_red': '红方陡道跨越起点已触发',
        'terrain_slope_blue': '蓝方陡道跨越起点已触发',
    }
    for pair_key in dict(getattr(entity, 'buff_path_progress', {})).keys():
        pending_label = pending_label_map.get(pair_key)
        if pending_label and pending_label not in entity.active_buff_labels:
            entity.active_buff_labels.append(pending_label)

    timed_buffs = getattr(entity, 'timed_buffs', {})
    if timed_buffs.get('terrain_highland_defense', 0.0) > 0:
        entity.dynamic_damage_taken_mult *= 0.75
        entity.active_buff_labels.append('高地地形增益')
    if timed_buffs.get('terrain_road_cooling', 0.0) > 0:
        entity.dynamic_cooling_mult *= 1.25
        entity.active_buff_labels.append('公路冷却增益')
    if timed_buffs.get('terrain_fly_slope_defense', 0.0) > 0:
        entity.dynamic_damage_taken_mult *= 0.75
        entity.active_buff_labels.append('飞坡防御增益')
    if timed_buffs.get('terrain_slope_defense', 0.0) > 0:
        entity.dynamic_damage_taken_mult *= 0.5
        entity.active_buff_labels.append('陡道防御增益')
    if timed_buffs.get('terrain_slope_cooling', 0.0) > 0:
        entity.dynamic_cooling_mult *= 2.0
        entity.active_buff_labels.append('陡道冷却增益')
    if timed_buffs.get('energy_mechanism_boost', 0.0) > 0:
        energy_rules = engine.rules.get('energy_mechanism', {})
        entity.dynamic_damage_dealt_mult *= float(energy_rules.get('damage_dealt_mult', 1.15))
        entity.dynamic_cooling_mult *= float(energy_rules.get('cooling_mult', 1.2))
        entity.dynamic_power_recovery_mult *= float(energy_rules.get('power_recovery_mult', 1.15))
        entity.active_buff_labels.append('中央能量机关增益')
    if getattr(entity, 'energy_small_buff_timer', 0.0) > 0.0:
        small_mult = float(engine.rules.get('energy_mechanism', {}).get('small_defense_mult', 0.75))
        entity.dynamic_damage_taken_mult *= small_mult
        entity.active_buff_labels.append('小能量机关护甲增益')
    if getattr(entity, 'energy_large_buff_timer', 0.0) > 0.0:
        entity.dynamic_damage_dealt_mult *= float(getattr(entity, 'energy_large_damage_dealt_mult', 1.0))
        entity.dynamic_damage_taken_mult *= float(getattr(entity, 'energy_large_damage_taken_mult', 1.0))
        entity.dynamic_cooling_mult *= float(getattr(entity, 'energy_large_cooling_mult', 1.0))
        entity.active_buff_labels.append('大能量机关增益')
    if getattr(entity, 'hero_deployment_active', False):
        entity.active_buff_labels.append('英雄部署模式')
    heat_lock_state = getattr(entity, 'heat_lock_state', 'normal')
    if heat_lock_state == 'cooling_unlock':
        entity.active_buff_labels.append('热量锁定')
    elif heat_lock_state == 'match_locked':
        entity.active_buff_labels.append('发射机构永久锁定')
    if getattr(entity, 'respawn_invalid_timer', 0.0) > 0.0:
        entity.active_buff_labels.append('复活无效态')
    if engine._is_respawn_weak(entity):
        entity.active_buff_labels.append('复活虚弱态')


def make_energy_team_state(engine):
    return {
        'small_tokens': 0,
        'large_tokens': 0,
        'small_awarded': 0,
        'large_awarded': 0,
        'state': 'inactive',
        'window_type': None,
        'window_timer': 0.0,
        'virtual_hits': 0.0,
        'last_hit_count': 0,
    }


def get_energy_mechanism_snapshot(engine, team):
    state = dict(engine.energy_activation_progress.get(team, make_energy_team_state(engine)))
    state['can_activate'] = bool(
        state.get('small_tokens', 0) > 0
        or state.get('large_tokens', 0) > 0
        or state.get('state') == 'activating'
    )
    return state


def energy_virtual_hits_per_sec(engine, entity):
    role_key = engine._entity_role_key(entity)
    rates = engine.rules.get('energy_mechanism', {}).get('virtual_hits_per_sec', {})
    return float(rates.get(role_key, rates.get('infantry', 0.36)))


def energy_large_reward(engine, hit_count):
    hit_count = max(5, min(10, int(hit_count)))
    if hit_count >= 9:
        return {'damage_dealt_mult': 3.0, 'damage_taken_mult': 0.5, 'cooling_mult': 5.0}
    if hit_count >= 8:
        return {'damage_dealt_mult': 2.0, 'damage_taken_mult': 0.75, 'cooling_mult': 3.0}
    if hit_count >= 7:
        return {'damage_dealt_mult': 2.0, 'damage_taken_mult': 0.75, 'cooling_mult': 2.0}
    return {'damage_dealt_mult': 1.5, 'damage_taken_mult': 0.75, 'cooling_mult': 2.0}


def grant_small_energy_buff(engine, entities, team):
    duration = float(engine.rules.get('energy_mechanism', {}).get('small_buff_duration_sec', 20.0))
    for entity in entities:
        if entity.team != team or entity.type not in {'robot', 'sentry'} or not entity.is_alive():
            continue
        entity.energy_small_buff_timer = max(float(getattr(entity, 'energy_small_buff_timer', 0.0)), duration)


def grant_large_energy_buff(engine, entities, team, hit_count):
    duration_map = engine.rules.get('energy_mechanism', {}).get('large_duration_by_hits', {})
    duration = float(duration_map.get(str(int(hit_count)), duration_map.get('5', 30.0)))
    reward = energy_large_reward(engine, hit_count)
    for entity in entities:
        if entity.team != team or entity.type not in {'robot', 'sentry'} or not entity.is_alive():
            continue
        entity.energy_large_buff_timer = max(float(getattr(entity, 'energy_large_buff_timer', 0.0)), duration)
        entity.energy_large_damage_dealt_mult = float(reward['damage_dealt_mult'])
        entity.energy_large_damage_taken_mult = float(reward['damage_taken_mult'])
        entity.energy_large_cooling_mult = float(reward['cooling_mult'])


def paired_buff_timeout_by_key(engine, pair_key):
    for buff_rules in engine.rules.get('buff_zones', {}).values():
        if buff_rules.get('pair_role') == 'start' and buff_rules.get('pair_key') == pair_key:
            return float(buff_rules.get('sequence_timeout_sec', 0.0))
    return 0.0


def expire_buff_path_progress(engine, entity):
    progress = dict(getattr(entity, 'buff_path_progress', {}))
    changed = False
    for pair_key, state in list(progress.items()):
        timeout = paired_buff_timeout_by_key(engine, pair_key)
        if timeout > 0.0 and engine.game_time - float(state.get('time', 0.0)) > timeout:
            progress.pop(pair_key, None)
            changed = True
    if changed:
        entity.buff_path_progress = progress


def terrain_access_allowed(engine, entity, facility):
    facility_type = facility.get('type') if isinstance(facility, dict) else str(facility)
    access_rules = dict(engine.rules.get('terrain_access', {}).get(facility_type, {}))
    role_key = engine._entity_role_key(entity)
    if 'allowed_entity_types' in access_rules:
        allowed_entity_types = set(access_rules.get('allowed_entity_types', []))
        if entity.type not in allowed_entity_types:
            return False
    if 'allowed_role_keys' in access_rules:
        allowed_role_keys = set(access_rules.get('allowed_role_keys', []))
        return role_key in allowed_role_keys
    return True


def energy_front_descriptor(engine, facility):
    if facility.get('type') == 'energy_mechanism' and facility.get('shape', 'rect') == 'rect':
        anchor_x, anchor_y = team_energy_anchor(engine, 'red', facility)
        return anchor_x, anchor_y, 0.0, 0.0
    center_x = (float(facility.get('x1', 0)) + float(facility.get('x2', 0))) / 2.0
    center_y = (float(facility.get('y1', 0)) + float(facility.get('y2', 0))) / 2.0
    points = list(facility.get('points', []))
    if len(points) >= 2:
        edge_mid_x = (float(points[0][0]) + float(points[-1][0])) / 2.0
        edge_mid_y = (float(points[0][1]) + float(points[-1][1])) / 2.0
        normal_x = edge_mid_x - center_x
        normal_y = edge_mid_y - center_y
    else:
        normal_x = 0.0
        normal_y = -1.0
    normal_len = math.hypot(normal_x, normal_y)
    if normal_len <= 1e-6:
        normal_x, normal_y = 0.0, -1.0
    else:
        normal_x /= normal_len
        normal_y /= normal_len
    return center_x, center_y, normal_x, normal_y


def energy_activation_anchor(engine, facility):
    center_x, center_y, normal_x, normal_y = energy_front_descriptor(engine, facility)
    energy_rules = engine.rules.get('energy_mechanism', {})
    anchor_distance_m = (
        float(energy_rules.get('activation_distance_min_m', 4.0))
        + float(energy_rules.get('activation_distance_max_m', 7.0))
    ) * 0.5
    anchor_distance = engine._meters_to_world_units(anchor_distance_m)
    return center_x + normal_x * anchor_distance, center_y + normal_y * anchor_distance


def team_energy_anchor(engine, team, facility):
    if facility.get('type') == 'energy_mechanism' and facility.get('shape', 'rect') == 'rect':
        if team == 'blue':
            return 970.0, 770.0
        return 579.0, 204.0
    return energy_activation_anchor(engine, facility)


def is_valid_energy_activator(engine, entity, facility):
    if entity.type not in {'robot', 'sentry'} or not entity.is_alive():
        return False
    if entity.type == 'robot' and getattr(entity, 'robot_type', '') == '工程':
        return False
    energy_rules = engine.rules.get('energy_mechanism', {})
    allowed_role_keys = set(energy_rules.get('allowed_role_keys', []))
    role_key = engine._entity_role_key(entity)
    if allowed_role_keys and role_key not in allowed_role_keys:
        return False
    if facility.get('type') == 'energy_mechanism' and facility.get('shape', 'rect') == 'rect':
        anchor_x, anchor_y = team_energy_anchor(engine, entity.team, facility)
        radius = engine._meters_to_world_units(float(energy_rules.get('activation_anchor_radius_m', 2.2)))
        return math.hypot(float(entity.position['x']) - anchor_x, float(entity.position['y']) - anchor_y) <= radius
    center_x, center_y, normal_x, normal_y = energy_front_descriptor(engine, facility)
    offset_x = float(entity.position['x']) - center_x
    offset_y = float(entity.position['y']) - center_y
    distance = math.hypot(offset_x, offset_y)
    min_distance = engine._meters_to_world_units(float(energy_rules.get('activation_distance_min_m', 4.0)))
    max_distance = engine._meters_to_world_units(float(energy_rules.get('activation_distance_max_m', 7.0)))
    if distance < min_distance or distance > max_distance:
        return False
    offset_len = max(distance, 1e-6)
    dot = (offset_x / offset_len) * normal_x + (offset_y / offset_len) * normal_y
    min_dot = math.cos(math.radians(float(energy_rules.get('front_angle_deg', 55.0))))
    return dot >= min_dot


def update_energy_mechanism_control(engine, entities, map_manager, dt):
    facilities = map_manager.get_facility_regions('energy_mechanism')
    if not facilities:
        return
    facility = facilities[0]
    energy_rules = engine.rules.get('energy_mechanism', {})
    window_sec = float(energy_rules.get('activation_window_sec', 20.0))
    small_times = list(energy_rules.get('small_opportunity_times_sec', [0.0, 90.0]))
    large_times = list(energy_rules.get('large_opportunity_times_sec', [180.0, 255.0, 330.0]))
    for team in ['red', 'blue']:
        state = engine.energy_activation_progress.setdefault(team, make_energy_team_state(engine))
        while state.get('small_awarded', 0) < len(small_times) and engine.game_time >= float(small_times[state['small_awarded']]):
            state['small_tokens'] = int(state.get('small_tokens', 0)) + 1
            state['small_awarded'] = int(state.get('small_awarded', 0)) + 1
        while state.get('large_awarded', 0) < len(large_times) and engine.game_time >= float(large_times[state['large_awarded']]):
            state['large_tokens'] = int(state.get('large_tokens', 0)) + 1
            state['large_awarded'] = int(state.get('large_awarded', 0)) + 1

        has_active_buff = any(
            other.team == team and other.is_alive() and (
                float(getattr(other, 'energy_small_buff_timer', 0.0)) > 0.0
                or float(getattr(other, 'energy_large_buff_timer', 0.0)) > 0.0
            )
            for other in entities
        )
        if state.get('state') == 'activated' and not has_active_buff:
            state['state'] = 'inactive'
            state['window_type'] = None

        valid_activators = [
            entity for entity in entities
            if entity.team == team and is_valid_energy_activator(engine, entity, facility)
        ]
        if state.get('state') == 'inactive' and valid_activators:
            window_type = None
            if int(state.get('small_tokens', 0)) > 0:
                state['small_tokens'] -= 1
                window_type = 'small'
            elif int(state.get('large_tokens', 0)) > 0:
                state['large_tokens'] -= 1
                window_type = 'large'
            if window_type is not None:
                state['state'] = 'activating'
                state['window_type'] = window_type
                state['window_timer'] = window_sec
                state['virtual_hits'] = 0.0
                state['last_hit_count'] = 0
                engine._log(f'{team} 方{"小" if window_type == "small" else "大"}能量机关进入正在激活状态', team)

        if state.get('state') != 'activating':
            continue

        if valid_activators:
            activator = valid_activators[0]
            state['virtual_hits'] = float(state.get('virtual_hits', 0.0)) + energy_virtual_hits_per_sec(engine, activator) * dt
        state['window_timer'] = float(state.get('window_timer', 0.0)) - dt
        if state['window_timer'] > 0.0:
            continue

        hit_count = max(0, min(10, int(round(float(state.get('virtual_hits', 0.0))))))
        state['last_hit_count'] = hit_count
        if state.get('window_type') == 'small' and hit_count >= 1:
            grant_small_energy_buff(engine, entities, team)
            state['state'] = 'activated'
            engine._log(f'{team} 方完成小能量机关激活，获得全队防御增益', team)
        elif state.get('window_type') == 'large' and hit_count >= 5:
            grant_large_energy_buff(engine, entities, team, hit_count)
            state['state'] = 'activated'
            engine._log(f'{team} 方完成大能量机关激活，命中环数 {hit_count}', team)
        else:
            state['state'] = 'failed'
            engine._log(f'{team} 方能量机关激活失败，窗口结束', team)
        state['window_type'] = None
        state['window_timer'] = 0.0
        state['virtual_hits'] = 0.0
        if state['state'] == 'failed':
            state['state'] = 'inactive'


def structure_alive(engine, team, entity_type):
    for entity in getattr(engine, '_latest_entities', []):
        if entity.team == team and entity.type == entity_type and entity.is_alive():
            return True
    return False


def region_has_enemy_occupant(engine, region, entity):
    for other in getattr(engine, '_latest_entities', []):
        if other.id == entity.id or not other.is_alive() or other.team == entity.team:
            continue
        if other.type not in {'robot', 'sentry'}:
            continue
        if region_contains_point(engine, region, other.position['x'], other.position['y']):
            return True
    return False


def region_contains_point(engine, region, x, y):
    shape = region.get('shape', 'rect')
    if shape == 'rect':
        return region.get('x1', 0) <= x <= region.get('x2', 0) and region.get('y1', 0) <= y <= region.get('y2', 0)
    if shape == 'line':
        x1 = float(region.get('x1', 0))
        y1 = float(region.get('y1', 0))
        x2 = float(region.get('x2', 0))
        y2 = float(region.get('y2', 0))
        dx = x2 - x1
        dy = y2 - y1
        if dx == 0 and dy == 0:
            return math.hypot(x - x1, y - y1) <= float(region.get('thickness', 12))
        t = ((x - x1) * dx + (y - y1) * dy) / max(dx * dx + dy * dy, 1e-6)
        t = max(0.0, min(1.0, t))
        closest_x = x1 + t * dx
        closest_y = y1 + t * dy
        return math.hypot(x - closest_x, y - closest_y) <= float(region.get('thickness', 12))
    points = region.get('points', [])
    if len(points) < 3:
        return False
    inside = False
    previous_x, previous_y = points[-1]
    for current_x, current_y in points:
        denominator = previous_y - current_y
        intersects = False
        if ((current_y > y) != (previous_y > y)) and abs(denominator) > 1e-6:
            x_intersect = current_x + (previous_x - current_x) * (y - current_y) / denominator
            intersects = x < x_intersect
        if intersects:
            inside = not inside
        previous_x, previous_y = current_x, current_y
    return inside


def buff_access_allowed(engine, entity, region, buff_rules):
    if engine._is_respawn_weak(entity):
        return False
    allowed_entity_types = set(buff_rules.get('allowed_entity_types', []))
    if allowed_entity_types and entity.type not in allowed_entity_types:
        return False

    allowed_role_keys = set(buff_rules.get('allowed_role_keys', []))
    role_key = engine._entity_role_key(entity)
    if allowed_role_keys and role_key not in allowed_role_keys:
        return False

    owner_team = region.get('team', 'neutral')
    if buff_rules.get('require_owner_outpost_alive') and owner_team not in {'neutral', None}:
        if owner_team == entity.team and not structure_alive(engine, owner_team, 'outpost'):
            return False

    if buff_rules.get('require_owner_outpost_destroyed') and owner_team not in {'neutral', None}:
        if owner_team == entity.team and structure_alive(engine, owner_team, 'outpost'):
            return False

    if buff_rules.get('team_locked') and owner_team not in {entity.team, 'neutral'}:
        unlock_time = float(buff_rules.get('allow_enemy_capture_after_outpost_destroyed_sec', 0.0))
        enemy_allowed_roles = set(buff_rules.get('enemy_allowed_role_keys', []))
        if unlock_time <= 0.0 or engine.game_time < unlock_time:
            return False
        if structure_alive(engine, owner_team, 'outpost'):
            return False
        if enemy_allowed_roles and role_key not in enemy_allowed_roles:
            return False

    if buff_rules.get('exclusive_control') and region_has_enemy_occupant(engine, region, entity):
        return False

    return True


def handle_paired_buff_region(engine, entity, region, buff_rules):
    pair_role = buff_rules.get('pair_role')
    pair_key = buff_rules.get('pair_key')
    if not pair_role or not pair_key:
        return False

    progress = dict(getattr(entity, 'buff_path_progress', {}))
    region_id = region.get('id')
    timeout = float(buff_rules.get('sequence_timeout_sec', 0.0))
    if pair_role == 'start':
        progress = {key: value for key, value in progress.items() if not str(key).startswith('terrain_') or key == pair_key}
        progress[pair_key] = {
            'region_id': region_id,
            'time': float(engine.game_time),
        }
        entity.buff_path_progress = progress
        return False

    start_state = progress.get(pair_key)
    if not start_state:
        return False
    if start_state.get('region_id') == region_id:
        return False
    if timeout > 0.0 and float(engine.game_time) - float(start_state.get('time', 0.0)) > timeout:
        progress.pop(pair_key, None)
        entity.buff_path_progress = progress
        return False

    progress.pop(pair_key, None)
    entity.buff_path_progress = progress
    return True


def handle_mining_zone(engine, entity, region, dt):
    if getattr(entity, 'robot_type', '') != '工程' or entity.type != 'robot':
        return
    if getattr(entity, 'carried_minerals', 0) > 0:
        entity.mining_timer = 0.0
        entity.mining_zone_id = None
        entity.mining_target_duration = 0.0
        return
    if entity.mining_zone_id != region.get('id'):
        entity.mining_zone_id = region.get('id')
        entity.mining_timer = 0.0
        entity.mining_target_duration = mining_duration(engine, exchange=False)
    entity.mining_timer += dt
    if entity.mining_timer < max(0.1, entity.mining_target_duration):
        return
    mined_amount = minerals_per_trip(engine)
    entity.carried_minerals += mined_amount
    entity.carried_mineral_type = '标准矿石'
    entity.mined_minerals_total = int(getattr(entity, 'mined_minerals_total', 0)) + mined_amount
    entity.mining_timer = 0.0
    entity.mining_target_duration = mining_duration(engine, exchange=False)
    engine.team_minerals[entity.team] = engine.team_minerals.get(entity.team, 0) + mined_amount
    engine._log(f'{entity.id} 在取矿区完成采矿，当前携带 {entity.carried_minerals} 单位矿物', entity.team)


def handle_exchange_zone(engine, entity, region, dt):
    if getattr(entity, 'robot_type', '') != '工程' or entity.type != 'robot':
        return
    if region.get('team') not in {entity.team}:
        return
    if getattr(entity, 'carried_minerals', 0) <= 0:
        entity.exchange_timer = 0.0
        entity.exchange_zone_id = None
        entity.exchange_target_duration = 0.0
        return
    if entity.exchange_zone_id != region.get('id'):
        entity.exchange_zone_id = region.get('id')
        entity.exchange_timer = 0.0
        entity.exchange_target_duration = mining_duration(engine, exchange=True)
    entity.exchange_timer += dt
    if entity.exchange_timer < max(0.1, entity.exchange_target_duration):
        return
    carried = int(getattr(entity, 'carried_minerals', 0))
    gold_gain = carried * float(engine.rules.get('mining', {}).get('gold_per_mineral', 120.0))
    engine.team_gold[entity.team] += gold_gain
    entity.gold = engine.team_gold[entity.team]
    engine.team_minerals[entity.team] = max(0, engine.team_minerals.get(entity.team, 0) - carried)
    entity.exchanged_minerals_total = int(getattr(entity, 'exchanged_minerals_total', 0)) + carried
    entity.exchanged_gold_total = float(getattr(entity, 'exchanged_gold_total', 0.0)) + gold_gain
    entity.carried_minerals = 0
    entity.carried_mineral_type = None
    entity.exchange_timer = 0.0
    entity.exchange_target_duration = mining_duration(engine, exchange=True)
    engine._log(f'{entity.id} 在兑矿区完成兑矿，队伍获得 {gold_gain:.0f} 金币', entity.team)


def minerals_per_trip(engine):
    raw = engine.rules.get('mining', {}).get('minerals_per_trip', 2)
    try:
        amount = int(raw)
    except (TypeError, ValueError):
        amount = 2
    return max(1, min(3, amount))


def try_purchase_role_ammo(engine, entity):
    if bool(getattr(entity, 'player_controlled', False)):
        return 0
    if entity.type != 'robot' or getattr(entity, 'robot_type', '') not in {'英雄', '步兵'}:
        return 0
    if getattr(entity, 'role_purchase_cooldown', 0.0) > 0:
        return 0
    purchase_rules = engine.rules.get('ammo_purchase', {})
    ammo_type = getattr(entity, 'ammo_type', '17mm')
    if ammo_type == '42mm':
        batch = int(purchase_rules.get('42mm_batch', 10))
        batch_cost = float(purchase_rules.get('42mm_cost', 20.0))
        max_allowed = int(purchase_rules.get('max_allowed_42mm', batch))
        opening_cap = int(purchase_rules.get('opening_targets', {}).get('hero_42mm', max_allowed))
    else:
        batch = int(purchase_rules.get('17mm_batch', 200))
        batch_cost = float(purchase_rules.get('17mm_cost', 12.0))
        max_allowed = int(purchase_rules.get('max_allowed_17mm', batch))
        opening_cap = int(purchase_rules.get('opening_targets', {}).get('infantry_17mm', max_allowed))
    stock_cap = opening_cap if engine.game_time <= 45.0 else max_allowed
    current_stock = engine._available_ammo(entity)
    purchase_amount = min(batch, max(0, stock_cap - current_stock))
    if batch <= 0 or purchase_amount <= 0:
        return 0
    unit_cost = batch_cost / max(batch, 1)
    total_cost = unit_cost * purchase_amount
    if engine.team_gold.get(entity.team, 0.0) + 1e-6 < total_cost:
        affordable_amount = int(engine.team_gold.get(entity.team, 0.0) / max(unit_cost, 1e-6))
        purchase_amount = min(purchase_amount, affordable_amount)
        total_cost = unit_cost * purchase_amount
    if purchase_amount <= 0:
        return 0
    engine.team_gold[entity.team] -= total_cost
    entity.gold = engine.team_gold[entity.team]
    engine._add_allowed_ammo(entity, purchase_amount, ammo_type)
    entity.role_purchase_cooldown = float(purchase_rules.get('purchase_interval_sec', 2.0))
    return purchase_amount


def is_in_team_supply_zone(engine, entity, map_manager=None):
    if entity is None or map_manager is None:
        return False
    for region in map_manager.get_regions_at(entity.position['x'], entity.position['y'], region_types={'supply', 'buff_supply'}):
        region_team = region.get('team')
        if region.get('type') == 'buff_supply' and region_team in {None, entity.team}:
            return True
        if region.get('type') == 'supply' and region_team == entity.team:
            return True
    return False


def purchase_manual_role_ammo(engine, entity, amount, map_manager=None):
    if entity is None:
        return {'ok': False, 'code': 'ENTITY_MISSING'}
    if entity.type != 'robot' or getattr(entity, 'robot_type', '') not in {'英雄', '步兵'}:
        return {'ok': False, 'code': 'ROLE_UNSUPPORTED'}
    if not is_in_team_supply_zone(engine, entity, map_manager=map_manager):
        return {'ok': False, 'code': 'NOT_IN_SUPPLY'}
    if getattr(entity, 'role_purchase_cooldown', 0.0) > 0.0:
        return {'ok': False, 'code': 'PURCHASE_COOLDOWN', 'cooldown': float(entity.role_purchase_cooldown)}

    try:
        requested_amount = int(amount)
    except (TypeError, ValueError):
        return {'ok': False, 'code': 'INVALID_AMOUNT'}
    if requested_amount <= 0:
        return {'ok': False, 'code': 'INVALID_AMOUNT'}

    purchase_rules = engine.rules.get('ammo_purchase', {})
    ammo_type = getattr(entity, 'ammo_type', '17mm')
    if ammo_type == '42mm':
        batch = int(purchase_rules.get('42mm_batch', 10))
        batch_cost = float(purchase_rules.get('42mm_cost', 20.0))
        max_allowed = int(purchase_rules.get('max_allowed_42mm', batch))
        opening_cap = int(purchase_rules.get('opening_targets', {}).get('hero_42mm', max_allowed))
    else:
        batch = int(purchase_rules.get('17mm_batch', 200))
        batch_cost = float(purchase_rules.get('17mm_cost', 12.0))
        max_allowed = int(purchase_rules.get('max_allowed_17mm', batch))
        opening_cap = int(purchase_rules.get('opening_targets', {}).get('infantry_17mm', max_allowed))

    stock_cap = opening_cap if engine.game_time <= 45.0 else max_allowed
    current_stock = engine._available_ammo(entity)
    purchase_amount = min(requested_amount, max(0, stock_cap - current_stock))
    if purchase_amount <= 0:
        return {'ok': False, 'code': 'STOCK_FULL', 'current_stock': current_stock, 'stock_cap': stock_cap}

    unit_cost = batch_cost / max(batch, 1)
    total_cost = unit_cost * purchase_amount
    team_gold = float(engine.team_gold.get(entity.team, 0.0))
    if team_gold + 1e-6 < total_cost:
        return {'ok': False, 'code': 'INSUFFICIENT_GOLD', 'need': total_cost, 'have': team_gold}

    engine.team_gold[entity.team] = team_gold - total_cost
    entity.gold = engine.team_gold[entity.team]
    engine._add_allowed_ammo(entity, purchase_amount, ammo_type)
    entity.role_purchase_cooldown = float(purchase_rules.get('purchase_interval_sec', 2.0))
    return {
        'ok': True,
        'amount': int(purchase_amount),
        'cost': float(total_cost),
        'team_gold': float(engine.team_gold.get(entity.team, 0.0)),
    }


def apply_buff_region(engine, entity, region, dt, active_regions=None):
    buff_rules = engine.rules.get('buff_zones', {}).get(region.get('type'), {})
    if not buff_rules:
        return
    label_map = {
        'buff_base': '基地增益',
        'buff_outpost': '前哨增益',
        'buff_fort': '堡垒增益点',
        'buff_supply': '补给增益点',
        'buff_assembly': '工程装配区',
        'buff_hero_deployment': '英雄部署区',
        'buff_central_highland': '中央高地',
        'buff_trapezoid_highland': '梯形高地',
        'buff_terrain_highland_red_start': '红方高地跨越起点',
        'buff_terrain_highland_red_end': '红方高地跨越终点',
        'buff_terrain_highland_blue_start': '蓝方高地跨越起点',
        'buff_terrain_highland_blue_end': '蓝方高地跨越终点',
        'buff_terrain_road_red_start': '红方公路跨越起点',
        'buff_terrain_road_red_end': '红方公路跨越终点',
        'buff_terrain_road_blue_start': '蓝方公路跨越起点',
        'buff_terrain_road_blue_end': '蓝方公路跨越终点',
        'buff_terrain_fly_slope_red_start': '红方飞坡跨越起点',
        'buff_terrain_fly_slope_red_end': '红方飞坡跨越终点',
        'buff_terrain_fly_slope_blue_start': '蓝方飞坡跨越起点',
        'buff_terrain_fly_slope_blue_end': '蓝方飞坡跨越终点',
        'buff_terrain_slope_red_start': '红方陡道跨越起点',
        'buff_terrain_slope_red_end': '红方陡道跨越终点',
        'buff_terrain_slope_blue_start': '蓝方陡道跨越起点',
        'buff_terrain_slope_blue_end': '蓝方陡道跨越终点',
    }
    if not buff_access_allowed(engine, entity, region, buff_rules):
        return
    if buff_rules.get('pair_role') == 'start':
        blocked_types = set(buff_rules.get('blocked_if_inside_facilities', ['base', 'outpost', 'supply', 'fort']))
        for other_region in active_regions or []:
            if other_region.get('id') == region.get('id'):
                continue
            if other_region.get('type') in blocked_types and other_region.get('team') in {entity.team, 'neutral'}:
                return
    if buff_rules.get('engineer_only') and getattr(entity, 'robot_type', '') != '工程':
        return
    if buff_rules.get('hero_only') and getattr(entity, 'robot_type', '') != '英雄':
        return

    label = label_map.get(region.get('type'))
    if label and label not in entity.active_buff_labels:
        entity.active_buff_labels.append(label)

    if region.get('type') == 'buff_hero_deployment':
        entity.hero_deployment_zone_active = True
        if bool(getattr(entity, 'hero_deployment_forced_off', False)):
            entity.hero_deployment_active = False
            entity.hero_deployment_state = 'inactive'
            entity.hero_deployment_charge = 0.0
            return
        delay_sec = float(buff_rules.get('activation_delay_sec', 2.0))
        entity.hero_deployment_charge = min(delay_sec, float(getattr(entity, 'hero_deployment_charge', 0.0)) + dt)
        if entity.hero_deployment_charge + 1e-6 < delay_sec:
            entity.hero_deployment_active = False
            entity.hero_deployment_state = 'deploying'
            if '英雄部署准备' not in entity.active_buff_labels:
                entity.active_buff_labels.append('英雄部署准备')
            return
        entity.hero_deployment_active = True
        entity.hero_deployment_state = 'deployed'
        entity.dynamic_damage_taken_mult *= float(buff_rules.get('damage_taken_mult', 0.75))
        entity.dynamic_damage_dealt_mult *= float(buff_rules.get('damage_dealt_mult', 1.5))
        return

    if buff_rules.get('pair_role'):
        if not handle_paired_buff_region(engine, entity, region, buff_rules):
            return

    damage_taken_mult = float(buff_rules.get('damage_taken_mult', 1.0))
    entity.dynamic_damage_taken_mult *= damage_taken_mult

    if region.get('type') == 'buff_trapezoid_highland':
        entity.trapezoid_highground_active = True

    if buff_rules.get('clear_weak'):
        engine._clear_negative_states(entity)

    if buff_rules.get('acts_as_fort'):
        entity.fort_buff_active = True

    if buff_rules.get('acts_as_supply'):
        entity.heal(entity.max_health * float(engine.rules['supply'].get('heal_ratio_per_sec', 0.10)) * dt)
        purchased = try_purchase_role_ammo(engine, entity)
        if buff_rules.get('engineer_invincible') and getattr(entity, 'robot_type', '') == '工程':
            entity.dynamic_invincible = True
        if purchased > 0:
            engine._log(f'{entity.id} 在增益补给点购买 {purchased} 发允许发弹量', entity.team)

    if buff_rules.get('invincible'):
        max_duration = float(buff_rules.get('max_duration_sec', 0.0))
        if max_duration <= 0.0 or getattr(entity, 'assembly_buff_time_used', 0.0) < max_duration:
            entity.dynamic_invincible = True
            if max_duration > 0.0:
                entity.assembly_buff_time_used = min(max_duration, getattr(entity, 'assembly_buff_time_used', 0.0) + dt)
            else:
                entity.assembly_buff_time_used = getattr(entity, 'assembly_buff_time_used', 0.0)

    timed_effects = dict(buff_rules.get('timed_effects', {}))
    cooldown_key = buff_rules.get('cooldown_key')
    if timed_effects:
        if cooldown_key and getattr(entity, 'buff_cooldowns', {}).get(cooldown_key, 0.0) > 0:
            return
        for effect_key, duration in timed_effects.items():
            entity.timed_buffs[effect_key] = max(float(entity.timed_buffs.get(effect_key, 0.0)), float(duration))
        if cooldown_key:
            entity.buff_cooldowns[cooldown_key] = float(buff_rules.get('cooldown_duration_sec', 0.0))


def update_occupied_facilities(engine, entities, map_manager):
    for team in ['red', 'blue']:
        for facility_type in engine.occupied_facilities[team]:
            engine.occupied_facilities[team][facility_type] = []

    controllable_types = {'base', 'outpost', 'fly_slope', 'undulating_road', 'rugged_road', 'first_step', 'dog_hole', 'second_step', 'supply', 'fort', 'energy_mechanism', 'mining_area', 'mineral_exchange'}
    for entity in entities:
        if entity.type not in {'robot', 'sentry', 'engineer'} or not entity.is_alive():
            continue
        if engine._is_respawn_weak(entity):
            continue

        regions = map_manager.get_regions_at(entity.position['x'], entity.position['y'])
        if not regions:
            continue
        for facility in regions:
            facility_type = facility.get('type')
            facility_id = facility.get('id')
            if facility_type in controllable_types and facility_id not in engine.occupied_facilities[entity.team].setdefault(facility_type, []):
                engine.occupied_facilities[entity.team][facility_type].append(facility_id)


def update_facility_effects(engine, entities, map_manager, dt):
    for entity in entities:
        if entity.type not in {'robot', 'sentry'}:
            continue
        if not entity.is_alive():
            entity.fort_buff_active = False
            entity.traversal_state = None
            continue

        reset_dynamic_effects(engine, entity)
        entity.fort_buff_active = False
        entity.trapezoid_highground_active = False
        entity.hero_deployment_zone_active = False
        entity.hero_deployment_target_id = None
        entity.hero_deployment_hit_probability = 0.0
        entity.fly_slope_airborne_timer = max(0.0, float(getattr(entity, 'fly_slope_airborne_timer', 0.0)) - dt)
        entity.fly_slope_airborne_height_m = max(0.0, float(getattr(entity, 'fly_slope_airborne_height_m', 0.0)))
        if getattr(entity, 'robot_type', '') != '英雄':
            entity.hero_deployment_charge = 0.0
            entity.hero_deployment_active = False
            entity.hero_deployment_state = 'inactive'

        regions = map_manager.get_regions_at(entity.position['x'], entity.position['y'])
        facility = regions[0] if regions else None
        if not any(region.get('type') == 'mining_area' for region in regions):
            entity.mining_timer = 0.0
            entity.mining_zone_id = None
        if not any(region.get('type') == 'mineral_exchange' for region in regions):
            entity.exchange_timer = 0.0
            entity.exchange_zone_id = None

        for region in regions:
            region_type = region.get('type')
            if region_type == 'dead_zone':
                if float(getattr(entity, 'fly_slope_airborne_timer', 0.0)) > 0.0 or float(getattr(entity, 'fly_slope_airborne_height_m', 0.0)) >= 0.20:
                    continue
                apply_dead_zone_penalty(engine, entity)
                break
            if engine._is_respawn_weak(entity) and engine._respawn_safe_zone_reached(entity, regions):
                invalid_elapsed = float(getattr(entity, 'respawn_invalid_elapsed', 0.0))
                invalid_timer = float(getattr(entity, 'respawn_invalid_timer', 0.0))
                engine._clear_negative_states(entity)
                min_elapsed = float(engine.rules['respawn'].get('invalid_min_elapsed_before_release', 10.0))
                post_safe_delay = float(engine.rules['respawn'].get('invalid_release_delay_after_safe_zone', 10.0))
                if invalid_elapsed >= min_elapsed:
                    entity.respawn_invalid_timer = 0.0
                else:
                    entity.respawn_invalid_timer = min(invalid_timer, post_safe_delay)
                entity.respawn_invalid_elapsed = invalid_elapsed
                entity.respawn_recovery_timer = entity.respawn_invalid_timer
                entity.state = 'idle'
                if entity.type == 'sentry':
                    entity.front_gun_locked = False
                engine._log(f'{entity.id} 到达己方安全区，解除复活虚弱', entity.team)
            if region_type == 'fort' and region.get('team') == entity.team:
                entity.fort_buff_active = True
            if region_type == 'supply' and region.get('team') == entity.team:
                if entity.type == 'sentry' and getattr(entity, 'front_gun_locked', False) and not engine._is_respawn_weak(entity):
                    entity.front_gun_locked = False
                    engine._log(f'{entity.id} 返回己方补给区，前管重新解锁', entity.team)
                heal_ratio = float(engine.rules['supply'].get('heal_ratio_per_sec', 0.10))
                if engine.game_time >= float(engine.rules['supply'].get('late_heal_start_time', 240.0)) and engine.is_out_of_combat(entity):
                    heal_ratio = float(engine.rules['supply'].get('late_heal_ratio_per_sec', 0.25))
                entity.heal(entity.max_health * heal_ratio * dt)
                claimable = claim_supply_ammo(engine, entity)
                purchased = try_purchase_role_ammo(engine, entity)
                if claimable > 0:
                    engine._log(f'{entity.id} 在补给区获得 {claimable} 发允许发弹量', entity.team)
                if purchased > 0:
                    engine._log(f'{entity.id} 使用队伍金币购买 {purchased} 发允许发弹量', entity.team)
            elif region_type == 'mining_area':
                handle_mining_zone(engine, entity, region, dt)
            elif region_type == 'mineral_exchange':
                handle_exchange_zone(engine, entity, region, dt)
            elif region_type.startswith('buff_'):
                apply_buff_region(engine, entity, region, dt, regions)

        if getattr(entity, 'robot_type', '') == '英雄' and not getattr(entity, 'hero_deployment_zone_active', False):
            entity.hero_deployment_charge = 0.0
            entity.hero_deployment_active = False
            entity.hero_deployment_state = 'inactive'
            entity.hero_deployment_target_id = None
            entity.hero_deployment_hit_probability = 0.0

        if not entity.is_alive():
            entity.traversal_state = None
            continue

        if facility and facility.get('type') in engine.terrain_cross_types and terrain_access_allowed(engine, entity, facility):
            update_traversal_progress(engine, entity, facility, dt)
        else:
            finish_traversal_if_needed(engine, entity)
            entity.traversal_state = None


def apply_dead_zone_penalty(engine, entity):
    if getattr(entity, 'permanent_eliminated', False):
        return
    entity.permanent_eliminated = True
    entity.elimination_reason = 'dead_zone'
    entity.health = 0.0
    entity.front_gun_locked = True
    engine.handle_destroy(entity)


def update_traversal_progress(engine, entity, facility, dt):
    state = getattr(entity, 'traversal_state', None)
    if state is None or state.get('facility_id') != facility.get('id'):
        entity.traversal_state = {
            'facility_id': facility.get('id'),
            'facility_type': facility.get('type'),
            'time': 0.0,
            'entry_x': entity.position['x'],
            'entry_y': entity.position['y'],
            'last_x': entity.position['x'],
            'last_y': entity.position['y'],
            'width': abs(facility['x2'] - facility['x1']),
            'height': abs(facility['y2'] - facility['y1']),
        }
        return

    state['time'] += dt
    state['last_x'] = entity.position['x']
    state['last_y'] = entity.position['y']


def finish_traversal_if_needed(engine, entity):
    state = getattr(entity, 'traversal_state', None)
    if not state:
        return

    min_hold_time = engine.rules['terrain_cross']['min_hold_time']
    completion_ratio = engine.rules['terrain_cross']['completion_ratio']
    travel_distance = math.hypot(state['last_x'] - state['entry_x'], state['last_y'] - state['entry_y'])
    facility_span = max(1.0, max(state['width'], state['height']))
    if state['time'] >= min_hold_time and travel_distance >= facility_span * completion_ratio:
        entity.terrain_buff_timer = engine.rules['terrain_cross']['duration']
        if state.get('facility_type') == 'fly_slope':
            entity.fly_slope_airborne_timer = max(float(getattr(entity, 'fly_slope_airborne_timer', 0.0)), 2.0)
        engine._log(f'{entity.id} 完整通过 {state["facility_type"]}，获得地形增益', entity.team)
