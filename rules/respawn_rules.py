def clear_negative_states(engine, entity):
    entity.weak_timer = 0.0
    entity.respawn_invalid_timer = 0.0
    entity.respawn_invalid_elapsed = 0.0
    entity.respawn_invalid_pending_release = False
    entity.respawn_weak_active = False
    entity.respawn_recovery_timer = 0.0
    if entity.state == 'weak':
        entity.state = 'idle'


def is_respawn_weak(engine, entity):
    return bool(getattr(entity, 'respawn_weak_active', False) or getattr(entity, 'weak_timer', 0.0) > 0.0)


def respawn_safe_zone_reached(engine, entity, regions):
    own_team = getattr(entity, 'team', None)
    for region in regions:
        if region.get('team') != own_team:
            continue
        if region.get('type') in {'supply', 'base'}:
            return True
        if region.get('type') == 'outpost' and engine._structure_alive(own_team, 'outpost'):
            return True
    return False


def is_fast_respawn_context(engine, entity):
    map_manager = engine._map_manager()
    if map_manager is not None and getattr(entity, 'respawn_position', None) is not None:
        regions = map_manager.get_regions_at(entity.respawn_position['x'], entity.respawn_position['y'])
        if any(region.get('type') == 'supply' and region.get('team') == entity.team for region in regions):
            return True
    own_base = engine._find_entity(getattr(engine, '_latest_entities', []), entity.team, 'base')
    return own_base is not None and float(getattr(own_base, 'health', 0.0)) < 2000.0


def calculate_respawn_read_duration(engine, entity):
    respawn_rules = engine.rules['respawn']
    base_delay = float(respawn_rules.get('robot_delay', 10.0))
    remaining_time = max(0.0, float(getattr(engine, 'game_duration', 0.0) or 0.0) - float(getattr(engine, 'game_time', 0.0)))
    remaining_threshold = float(respawn_rules.get('respawn_formula_remaining_time_threshold', 420.0))
    remaining_divisor = max(float(respawn_rules.get('respawn_formula_remaining_time_divisor', 10.0)), 1e-6)
    remaining_penalty = max(0.0, remaining_threshold - remaining_time) / remaining_divisor
    instant_penalty = float(respawn_rules.get('respawn_formula_instant_revive_addition', 20.0)) * int(getattr(entity, 'instant_respawn_count', 0))
    return max(base_delay, round(base_delay + remaining_penalty + instant_penalty))


def handle_destroy(engine, entity):
    if getattr(entity, 'death_handled', False):
        return

    entity.state = 'destroyed'
    entity.death_handled = True
    entity.target = None
    entity.fire_control_state = 'idle'
    entity.set_velocity(0, 0)
    entity.angular_velocity = 0
    entity.respawn_position = dict(getattr(entity, 'last_valid_position', entity.position))
    entity.invincible_timer = 0.0
    entity.weak_timer = 0.0
    entity.respawn_invalid_timer = 0.0
    entity.respawn_invalid_elapsed = 0.0
    entity.respawn_invalid_pending_release = False
    entity.respawn_weak_active = False
    entity.respawn_mode = 'normal'
    entity.respawn_recovery_timer = 0.0
    entity.fort_buff_active = False
    entity.terrain_buff_timer = 0.0
    entity.traversal_state = None
    entity.dynamic_invincible = False
    entity.active_buff_labels = []
    entity.timed_buffs = {}
    entity.buff_cooldowns = {}
    entity.buff_path_progress = {}
    entity.energy_small_buff_timer = 0.0
    entity.energy_large_buff_timer = 0.0
    entity.energy_large_damage_dealt_mult = 1.0
    entity.energy_large_damage_taken_mult = 1.0
    entity.energy_large_cooling_mult = 1.0
    entity.hero_deployment_charge = 0.0
    entity.hero_deployment_active = False
    entity.hero_deployment_zone_active = False
    entity.hero_deployment_state = 'inactive'
    entity.hero_deployment_target_id = None
    entity.hero_deployment_hit_probability = 0.0
    entity.heat_lock_state = 'normal'
    entity.heat_lock_reason = ''
    entity.heat_ui_disabled = False
    entity.heat_cooling_accumulator = 0.0
    entity.carried_minerals = 0
    entity.mining_timer = 0.0
    entity.exchange_timer = 0.0

    if entity.type == 'base':
        engine.game_over = True
        engine.winner = 'red' if entity.team == 'blue' else 'blue'
        engine.stage = 'ended'
        engine._log(f'{entity.team}基地被摧毁！游戏结束！{engine.winner}方获胜！', engine.winner)
        return

    if entity.type in {'robot', 'sentry'}:
        if getattr(entity, 'permanent_eliminated', False):
            entity.respawn_duration = 0.0
            entity.respawn_timer = 0.0
            entity.state = 'destroyed'
            entity.front_gun_locked = True
            entity.heat_lock_state = 'permanent_lock'
            entity.heat_lock_reason = 'dead_zone'
            entity.heat_ui_disabled = True
            engine._log(f'{entity.id} 进入死区，被直接罚下，本局无法再次上线', entity.team)
            return
        entity.respawn_duration = calculate_respawn_read_duration(engine, entity)
        entity.respawn_timer = entity.respawn_duration
        entity.state = 'respawning'
        if entity.type == 'sentry':
            entity.front_gun_locked = True
        engine._log(f'{entity.id} 被击毁，进入复活读条', entity.team)
        return

    if entity.type == 'outpost':
        engine._log(f'{entity.team}前哨站被摧毁！', entity.team)


def respawn_entity(engine, entity, respawn_mode='normal'):
    if getattr(entity, 'permanent_eliminated', False):
        return
    respawn_position = dict(getattr(entity, 'respawn_position', entity.position))
    entity.position = respawn_position
    entity.previous_position = dict(respawn_position)
    entity.last_valid_position = dict(respawn_position)
    entity.angle = entity.spawn_angle
    entity.turret_angle = entity.spawn_angle
    entity.health = entity.max_health
    entity.heat = 0.0
    entity.posture_active_time = 0.0
    entity.target = None
    entity.fire_control_state = 'idle'
    entity.velocity = {'vx': 0, 'vy': 0, 'vz': 0}
    entity.angular_velocity = 0
    entity.toppled = False
    entity.topple_pitch_deg = 0.0
    entity.topple_roll_deg = 0.0
    entity.jump_clearance_target_m = 0.0
    entity.jump_airborne_height_m = 0.0
    entity.jump_vertical_velocity_mps = 0.0
    entity.step_climb_state = None
    entity.respawn_timer = 0.0
    entity.respawn_duration = 0.0
    entity.respawn_mode = respawn_mode
    entity.respawn_invalid_elapsed = 0.0
    entity.respawn_invalid_pending_release = False
    entity.weak_timer = 0.0
    if respawn_mode == 'instant':
        entity.health = entity.max_health
        entity.respawn_recovery_timer = 0.0
        entity.invincible_timer = float(engine.rules['respawn']['invincible_duration'])
        entity.respawn_invalid_timer = 0.0
        entity.respawn_weak_active = False
    else:
        entity.health = max(1.0, float(entity.max_health) * 0.10)
        entity.respawn_recovery_timer = float(engine.rules['respawn'].get('invalid_duration', 30.0))
        entity.invincible_timer = 0.0
        entity.respawn_invalid_timer = float(engine.rules['respawn'].get('invalid_duration', 30.0))
        entity.respawn_weak_active = True
    entity.death_handled = False
    entity.dynamic_invincible = False
    entity.active_buff_labels = []
    entity.timed_buffs = {}
    entity.buff_cooldowns = {}
    entity.buff_path_progress = {}
    entity.energy_small_buff_timer = 0.0
    entity.energy_large_buff_timer = 0.0
    entity.energy_large_damage_dealt_mult = 1.0
    entity.energy_large_damage_taken_mult = 1.0
    entity.energy_large_cooling_mult = 1.0
    entity.hero_deployment_charge = 0.0
    entity.hero_deployment_active = False
    entity.hero_deployment_zone_active = False
    entity.hero_deployment_state = 'inactive'
    entity.hero_deployment_target_id = None
    entity.hero_deployment_hit_probability = 0.0
    entity.heat_lock_state = 'normal'
    entity.heat_lock_reason = ''
    entity.heat_ui_disabled = False
    entity.heat_cooling_accumulator = 0.0
    entity.carried_minerals = 0
    entity.mining_timer = 0.0
    entity.exchange_timer = 0.0
    entity.state = 'invincible' if entity.invincible_timer > 0 else ('weak' if is_respawn_weak(engine, entity) else 'idle')
    if entity.type == 'sentry':
        entity.front_gun_locked = is_respawn_weak(engine, entity)
    if respawn_mode == 'instant':
        engine._log(f'{entity.id} 已立即复活，获得 3 秒无敌并恢复满血', entity.team)
    else:
        engine._log(f'{entity.id} 已在原地复活，进入无效/虚弱阶段', entity.team)