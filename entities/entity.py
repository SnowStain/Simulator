#!/usr/bin/env python3
# -*- coding: utf-8 -*-

class Entity:
    def __init__(self, entity_id, entity_type, team, position, angle=0, robot_type=None):
        self.id = entity_id
        self.type = entity_type
        self.team = team
        self.robot_type = robot_type  # 机器人类型：英雄、工程、步兵、哨兵
        self.level = 1
        self.display_name = entity_id
        self.position = position  # {'x': x, 'y': y, 'z': z}
        self.spawn_position = dict(position)
        self.respawn_position = dict(position)
        self.previous_position = dict(position)
        self.last_valid_position = dict(position)
        self.angle = angle  # 角度（度）
        self.spawn_angle = angle
        self.turret_angle = angle
        self.gimbal_pitch_deg = 0.0
        self.velocity = {'vx': 0, 'vy': 0, 'vz': 0}
        self.angular_velocity = 0
        self.health = 100
        self.max_health = 100
        self.state = "idle"
        self.target = None
        self.movable = True
        self.collidable = True
        self.collision_radius = 16.0
        self.wheel_count = 4
        self.body_size_m = 0.42
        self.body_length_m = 0.42
        self.body_width_m = 0.42
        self.body_height_m = 0.18
        self.body_clearance_m = 0.10
        self.wheel_radius_m = 0.08
        self.gimbal_height_m = 0.50
        self.gimbal_length_m = 0.30
        self.gimbal_width_m = 0.10
        self.gimbal_body_height_m = 0.10
        self.gimbal_mount_gap_m = 0.10
        self.gimbal_mount_length_m = 0.10
        self.gimbal_mount_width_m = 0.10
        self.gimbal_mount_height_m = 0.10
        self.barrel_length_m = 0.36
        self.barrel_radius_m = 0.015
        self.front_climb_assist_style = 'none'
        self.rear_climb_assist_style = 'none'
        self.front_climb_assist_top_length_m = 0.05
        self.front_climb_assist_bottom_length_m = 0.03
        self.front_climb_assist_plate_width_m = 0.018
        self.front_climb_assist_plate_height_m = 0.18
        self.front_climb_assist_forward_offset_m = 0.04
        self.front_climb_assist_inner_offset_m = 0.06
        self.rear_climb_assist_upper_length_m = 0.09
        self.rear_climb_assist_lower_length_m = 0.08
        self.rear_climb_assist_upper_width_m = 0.016
        self.rear_climb_assist_upper_height_m = 0.016
        self.rear_climb_assist_lower_width_m = 0.016
        self.rear_climb_assist_lower_height_m = 0.016
        self.rear_climb_assist_mount_offset_x_m = 0.03
        self.rear_climb_assist_mount_height_m = 0.22
        self.rear_climb_assist_inner_offset_m = 0.03
        self.rear_climb_assist_upper_pair_gap_m = 0.06
        self.rear_climb_assist_hinge_radius_m = 0.016
        self.rear_climb_assist_knee_min_deg = 42.0
        self.rear_climb_assist_knee_max_deg = 132.0
        self.rear_climb_assist_knee_direction = 'rear'
        self.chassis_subtype = 'balance_legged'
        self.body_shape = 'box'
        self.wheel_style = 'standard'
        self.suspension_style = 'four_bar'
        self.arm_style = 'none'
        self.wheel_orbit_yaws_deg = ()
        self.wheel_self_yaws_deg = ()
        self.armor_orbit_yaws_deg = ()
        self.armor_self_yaws_deg = ()
        self.armor_light_orbit_yaws_deg = ()
        self.armor_light_self_yaws_deg = ()
        self.armor_plate_size_m = 0.12
        self.armor_plate_width_m = 0.12
        self.armor_plate_length_m = 0.12
        self.armor_plate_height_m = 0.12
        self.armor_plate_gap_m = 0.02
        self.armor_light_length_m = 0.10
        self.armor_light_width_m = 0.02
        self.armor_light_height_m = 0.02
        self.barrel_light_length_m = 0.10
        self.barrel_light_width_m = 0.02
        self.barrel_light_height_m = 0.02
        self.body_render_width_scale = 0.82
        self.vertical_scale_m = 1.0
        self.max_pitch_up_deg = 30.0
        self.max_pitch_down_deg = 30.0
        self.body_color_rgb = None
        self.turret_color_rgb = None
        self.armor_color_rgb = None
        self.wheel_color_rgb = None
        self.custom_wheel_positions_m = ()
        self.gimbal_offset_x_m = 0.0
        self.gimbal_offset_y_m = 0.0
        self.collision_recovery_timer = 0.0
        self.collision_recovery_vector = (0.0, 0.0)
        self.active_buff_labels = []
        
        # 底盘功率系统
        self.power = 100
        self.max_power = 100
        self.power_recovery_rate = 1.0
        self.chassis_mode = 'health_priority'
        self.chassis_supports_jump = True
        self.chassis_speed_scale = 1.0
        self.chassis_drive_power_limit_w = 180.0
        self.chassis_drive_idle_draw_w = 16.0
        self.chassis_drive_rpm_coeff = 0.00005
        self.chassis_drive_accel_coeff = 0.012
        self.chassis_power_draw_w = 0.0
        self.chassis_rpm = 0.0
        self.chassis_speed_limit_mps = 0.0
        self.chassis_power_ratio = 1.0
        
        # 枪管热量系统
        self.heat = 0
        self.max_heat = 100
        self.heat_gain_per_shot = 10
        self.heat_dissipation_rate = 5
        self.heat_managed_by_rules = True
        self.heat_lock_state = 'normal'
        self.heat_lock_reason = ''
        self.heat_ui_disabled = False
        self.heat_cooling_accumulator = 0.0
        self.gimbal_mode = 'cooling_priority'
        self.hero_weapon_mode = 'ranged_priority'
        
        # 火控状态
        self.fire_control_state = 'idle'
        self.front_gun_locked = False
        self.auto_aim_locked = False

        # 裁判系统相关状态（哨兵重点使用）
        self.ammo = 0
        self.allowed_ammo_17mm = 0
        self.allowed_ammo_42mm = 0
        self.ammo_type = '17mm'
        self.gold = 0.0
        self.posture = 'mobile'
        self.sentry_mode = 'auto'
        self.posture_cooldown = 0.0
        self.posture_active_time = 0.0
        self.ai_decision = ''
        self.ai_behavior_node = ''
        self.ai_navigation_target = None
        self.ai_movement_target = None
        self.ai_navigation_waypoint = None
        self.ai_path_preview = ()
        self.ai_navigation_subgoals = ()
        self.ai_navigation_path_valid = False
        self.ai_navigation_radius = 0.0
        self.ai_navigation_velocity = (0.0, 0.0)
        self.ai_decision_weights = ()
        self.ai_decision_top3 = ()
        self.ai_decision_selected = ''
        self.test_forced_decision_id = ''
        self.search_angular_speed = 36.0
        self.fire_rate_hz = 8.0
        self.ammo_per_shot = 1
        self.power_per_shot = 6.0
        self.autoaim_locked_target_id = None
        self.autoaim_lock_timer = 0.0
        self.auto_aim_hit_probability = 0.0
        self.auto_aim_hit_probability_target_id = None
        self.auto_aim_hit_probability_updated_at = -1e9
        self.player_controlled = False
        self.manual_aim_point = None
        self.actor_state = 'idle'
        self.actor_state_tags = set()
        self.actor_state_context = {}
        self.shot_cooldown = 0.0
        self.overheat_lock_timer = 0.0
        self.evasive_spin_timer = 0.0
        self.evasive_spin_direction = 1.0
        self.evasive_spin_rate_deg = 420.0
        self.last_damage_source_id = None
        self.respawn_timer = 0.0
        self.respawn_duration = 0.0
        self.respawn_recovery_timer = 0.0
        self.invincible_timer = 0.0
        self.weak_timer = 0.0
        self.respawn_invalid_timer = 0.0
        self.respawn_invalid_elapsed = 0.0
        self.respawn_invalid_pending_release = False
        self.respawn_weak_active = False
        self.respawn_mode = 'normal'
        self.instant_respawn_count = 0
        self.death_handled = False
        self.permanent_eliminated = False
        self.elimination_reason = ''
        self.fort_buff_active = False
        self.trapezoid_highground_active = False
        self.terrain_buff_timer = 0.0
        self.fly_slope_airborne_timer = 0.0
        self.fly_slope_airborne_height_m = 0.0
        self.jump_airborne_height_m = 0.0
        self.jump_vertical_velocity_mps = 0.0
        self.jump_clearance_target_m = 0.0
        self.jump_requested = False
        self.player_jump_key_down = False
        self.player_key_timing = {}
        self.player_input_timing = {}
        self.small_gyro_active = False
        self.small_gyro_direction = 1.0
        self.step_climb_mode_active = False
        self.step_climb_lock_heading_deg = None
        self.fly_slope_immunity_armed = False
        self.supply_cooldown = 0.0
        self.supply_ammo_claimed = 0
        self.exchange_cooldown = 0.0
        self.last_combat_time = -1e9
        self.pending_rule_events = []
        self.traversal_state = None
        self.direct_terrain_step_height_m = 0.06
        self.max_step_climb_height_m = 0.23
        self.max_terrain_step_height_m = self.direct_terrain_step_height_m
        self.can_climb_steps = True
        self.step_climb_duration_sec = 2.0
        self.step_climb_state = None
        self.toppled = False
        self.topple_pitch_deg = 0.0
        self.topple_roll_deg = 0.0
        self.stability_pitch_limit_deg = 0.0
        self.stability_roll_limit_deg = 0.0
        self.dynamic_damage_taken_mult = 1.0
        self.dynamic_damage_dealt_mult = 1.0
        self.dynamic_cooling_mult = 1.0
        self.dynamic_power_recovery_mult = 1.0
        self.dynamic_power_capacity_mult = 1.0
        self.dynamic_invincible = False
        self.timed_buffs = {}
        self.buff_cooldowns = {}
        self.buff_path_progress = {}
        self.energy_small_buff_timer = 0.0
        self.energy_large_buff_timer = 0.0
        self.energy_large_damage_dealt_mult = 1.0
        self.energy_large_damage_taken_mult = 1.0
        self.energy_large_cooling_mult = 1.0
        self.assembly_buff_time_used = 0.0
        self.hero_deployment_charge = 0.0
        self.hero_deployment_active = False
        self.hero_deployment_zone_active = False
        self.hero_deployment_forced_off = False
        self.hero_deployment_state = 'inactive'
        self.hero_deployment_target_id = None
        self.hero_deployment_hit_probability = 0.0
        self.hero_deployment_hit_probability_target_id = None
        self.hero_deployment_hit_probability_updated_at = -1e9
        self.hero_structure_lob_active = False
        self.hero_structure_lob_target_type = None
        self.carried_minerals = 0
        self.carried_mineral_type = None
        self.mined_minerals_total = 0
        self.exchanged_minerals_total = 0
        self.exchanged_gold_total = 0.0
        self.mining_timer = 0.0
        self.mining_target_duration = 0.0
        self.exchange_timer = 0.0
        self.exchange_target_duration = 0.0
        self.mining_zone_id = None
        self.exchange_zone_id = None
        self.role_purchase_cooldown = 0.0
        self.recent_attackers = []
        self.damage_feedbacks = []
    
    def update(self, dt):
        """更新实体状态"""
        if getattr(self, 'robot_type', '') == '英雄' and bool(getattr(self, 'hero_deployment_active', False)):
            self.velocity = {'vx': 0.0, 'vy': 0.0, 'vz': 0.0}
            self.angular_velocity = 0.0

        if bool(getattr(self, 'toppled', False)):
            self.velocity = {'vx': 0.0, 'vy': 0.0, 'vz': 0.0}
            self.angular_velocity = 0.0

        # 更新位置
        if self.movable:
            self.previous_position = dict(self.position)
            self.position['x'] += self.velocity['vx'] * dt
            self.position['y'] += self.velocity['vy'] * dt
            self.position['z'] += self.velocity['vz'] * dt
        
        # 更新角度
        self.angle += self.angular_velocity * dt
        self.angle %= 360

        self.collision_recovery_timer = max(0.0, float(getattr(self, 'collision_recovery_timer', 0.0)) - dt)
        if self.collision_recovery_timer <= 0.0:
            self.collision_recovery_vector = (0.0, 0.0)

        self.autoaim_lock_timer = max(0.0, float(getattr(self, 'autoaim_lock_timer', 0.0)) - dt)
        if self.autoaim_lock_timer <= 0.0:
            self.autoaim_locked_target_id = None

        self.evasive_spin_timer = max(0.0, float(getattr(self, 'evasive_spin_timer', 0.0)) - dt)
        
        # 更新底盘功率（恢复）
        power_recovery_mult = max(0.0, float(getattr(self, 'dynamic_power_recovery_mult', 1.0)))
        self.power += self.power_recovery_rate * power_recovery_mult * dt
        power_capacity = float(getattr(self, 'max_power', 0.0)) * max(0.0, float(getattr(self, 'dynamic_power_capacity_mult', 1.0)))
        if self.power > power_capacity:
            self.power = power_capacity
        
        # 热量由规则引擎按 10Hz 规则统一处理，避免与实体逐帧冷却叠加。
        if not bool(getattr(self, 'heat_managed_by_rules', True)):
            cooling_mult = max(0.0, float(getattr(self, 'dynamic_cooling_mult', 1.0)))
            self.heat -= self.heat_dissipation_rate * cooling_mult * dt
            if self.heat < 0:
                self.heat = 0

        active_feedbacks = []
        for feedback in list(getattr(self, 'damage_feedbacks', [])):
            feedback['ttl'] = max(0.0, float(feedback.get('ttl', 0.0)) - dt)
            if feedback['ttl'] > 0.0:
                active_feedbacks.append(feedback)
        self.damage_feedbacks = active_feedbacks
    
    def set_position(self, x, y, z=0):
        """设置位置"""
        self.position = {'x': x, 'y': y, 'z': z}
        self.previous_position = dict(self.position)
        self.last_valid_position = dict(self.position)
        self.toppled = False
        self.topple_pitch_deg = 0.0
        self.topple_roll_deg = 0.0
    
    def set_velocity(self, vx, vy, vz=0):
        """设置速度"""
        if not self.movable:
            self.velocity = {'vx': 0, 'vy': 0, 'vz': 0}
            return
        if bool(getattr(self, 'toppled', False)):
            self.velocity = {'vx': 0.0, 'vy': 0.0, 'vz': 0.0}
            return
        if getattr(self, 'robot_type', '') == '英雄' and bool(getattr(self, 'hero_deployment_active', False)):
            self.velocity = {'vx': 0.0, 'vy': 0.0, 'vz': 0.0}
            return
        self.velocity = {'vx': vx, 'vy': vy, 'vz': vz}
    
    def take_damage(self, damage):
        """受到伤害"""
        damage_value = max(0.0, float(damage))
        if damage_value <= 0.0:
            return
        feedback_count = len(getattr(self, 'damage_feedbacks', ()))
        self.damage_feedbacks.append({
            'amount': damage_value,
            'ttl': 0.85,
            'total_ttl': 0.85,
            'rise_px': 34.0,
            'drift_px': -12.0 if feedback_count % 2 == 0 else 12.0,
        })
        if len(self.damage_feedbacks) > 6:
            self.damage_feedbacks = self.damage_feedbacks[-6:]
        self.health -= damage_value
        if self.health< 0:
            self.health = 0
            self.state = "destroyed"
    
    def heal(self, amount):
        """恢复生命值"""
        self.health += amount
        if self.health >self.max_health:
            self.health = self.max_health
    
    def is_alive(self):
        """检查是否存活"""
        return self.health > 0
