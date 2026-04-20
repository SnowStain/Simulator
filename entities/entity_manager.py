#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import os

from entities.chassis_profiles import INFANTRY_CHASSIS_SUBTYPE_DEFAULT, infantry_chassis_preset, normalize_infantry_chassis_subtype, resolve_infantry_subtype_profile
from entities.entity import Entity

class EntityManager:
    def __init__(self, config):
        self.config = config
        self.entities = []
        self.entity_map = {}
        self.editable_entity_keys = ['robot_1', 'robot_2', 'robot_3', 'robot_4', 'robot_7']
        self.enable_entity_movement = config.get('simulator', {}).get('enable_entity_movement', True)
        self.vertical_scale_m = 1.0
        self.appearance_preset_path = self._resolve_appearance_preset_path()
        self.appearance_presets = self._load_appearance_presets()

    def _resolve_appearance_preset_path(self):
        configured_path = str(self.config.get('entities', {}).get('appearance_preset_path', os.path.join('appearance_presets', 'latest_appearance.json')))
        if os.path.isabs(configured_path):
            return configured_path
        config_path = str(self.config.get('_config_path', 'config.json'))
        base_dir = os.path.dirname(os.path.abspath(config_path))
        return os.path.join(base_dir, configured_path)

    def _load_appearance_presets(self):
        if not os.path.exists(self.appearance_preset_path):
            return {'profiles': {}}
        try:
            with open(self.appearance_preset_path, 'r', encoding='utf-8') as file:
                payload = json.load(file)
        except Exception:
            return {'profiles': {}}
        if not isinstance(payload, dict):
            return {'profiles': {}}
        if not isinstance(payload.get('profiles'), dict):
            payload['profiles'] = {}
        return payload

    def _normalize_color_tuple(self, value):
        if not isinstance(value, (list, tuple)) or len(value) < 3:
            return None
        return tuple(max(0, min(255, int(round(float(channel))))) for channel in value[:3])

    def _resolve_robot_subtype(self, robot_type, robot_subtype=None):
        if robot_type != '步兵':
            return ''
        return normalize_infantry_chassis_subtype(robot_subtype or INFANTRY_CHASSIS_SUBTYPE_DEFAULT)

    def _resolve_gimbal_center_height(self, entity):
        body_top = float(getattr(entity, 'body_clearance_m', 0.0)) + float(getattr(entity, 'body_height_m', 0.0))
        mount_gap = max(0.0, float(getattr(entity, 'gimbal_mount_gap_m', 0.0)))
        mount_height = max(0.0, float(getattr(entity, 'gimbal_mount_height_m', 0.0)))
        turret_half_height = max(0.0, float(getattr(entity, 'gimbal_body_height_m', 0.0)) * 0.5)
        return body_top + mount_gap + mount_height + turret_half_height

    def _apply_appearance_profile(self, entity, entity_type, robot_type=None):
        profiles = self.appearance_presets.get('profiles', {}) if isinstance(self.appearance_presets, dict) else {}
        role_key = 'sentry' if entity_type == 'sentry' else self._robot_role_key(robot_type)
        profile = profiles.get(role_key, {})
        if not isinstance(profile, dict):
            return
        if role_key == 'infantry':
            profile = resolve_infantry_subtype_profile(profile, getattr(entity, 'chassis_subtype', None))
        else:
            profile = dict(profile)

        numeric_fields = (
            'collision_radius',
            'body_length_m',
            'body_width_m',
            'body_height_m',
            'body_clearance_m',
            'wheel_radius_m',
            'gimbal_height_m',
            'gimbal_length_m',
            'gimbal_width_m',
            'gimbal_body_height_m',
            'gimbal_mount_gap_m',
            'gimbal_mount_length_m',
            'gimbal_mount_width_m',
            'gimbal_mount_height_m',
            'barrel_length_m',
            'barrel_radius_m',
            'gimbal_offset_x_m',
            'gimbal_offset_y_m',
            'armor_plate_width_m',
            'armor_plate_length_m',
            'armor_plate_height_m',
            'armor_plate_gap_m',
            'armor_light_length_m',
            'armor_light_width_m',
            'armor_light_height_m',
            'barrel_light_length_m',
            'barrel_light_width_m',
            'barrel_light_height_m',
            'body_render_width_scale',
            'front_climb_assist_top_length_m',
            'front_climb_assist_bottom_length_m',
            'front_climb_assist_plate_width_m',
            'front_climb_assist_plate_height_m',
            'front_climb_assist_forward_offset_m',
            'front_climb_assist_inner_offset_m',
            'rear_climb_assist_upper_length_m',
            'rear_climb_assist_lower_length_m',
            'rear_climb_assist_upper_width_m',
            'rear_climb_assist_upper_height_m',
            'rear_climb_assist_lower_width_m',
            'rear_climb_assist_lower_height_m',
            'rear_climb_assist_mount_offset_x_m',
            'rear_climb_assist_mount_height_m',
            'rear_climb_assist_inner_offset_m',
            'rear_climb_assist_upper_pair_gap_m',
            'rear_climb_assist_hinge_radius_m',
            'rear_climb_assist_knee_min_deg',
            'rear_climb_assist_knee_max_deg',
            'chassis_speed_scale',
            'chassis_drive_power_limit_w',
            'chassis_drive_idle_draw_w',
            'chassis_drive_rpm_coeff',
            'chassis_drive_accel_coeff',
        )
        for field_name in numeric_fields:
            if field_name in profile:
                setattr(entity, field_name, float(profile[field_name]))

        for field_name in ('front_climb_assist_style', 'rear_climb_assist_style', 'wheel_style', 'suspension_style', 'arm_style', 'body_shape'):
            if field_name in profile:
                setattr(entity, field_name, str(profile[field_name]))
        if 'rear_climb_assist_knee_direction' in profile:
            entity.rear_climb_assist_knee_direction = str(profile['rear_climb_assist_knee_direction'])

        if 'chassis_subtype' in profile:
            entity.chassis_subtype = self._resolve_robot_subtype(robot_type, profile.get('chassis_subtype'))
        if 'chassis_supports_jump' in profile:
            entity.chassis_supports_jump = bool(profile.get('chassis_supports_jump'))

        for field_name in ('body_color_rgb', 'turret_color_rgb', 'armor_color_rgb', 'wheel_color_rgb'):
            if field_name in profile:
                setattr(entity, field_name, self._normalize_color_tuple(profile.get(field_name)))

        wheel_positions = profile.get('custom_wheel_positions_m')
        if isinstance(wheel_positions, list):
            normalized_positions = []
            for position in wheel_positions:
                if not isinstance(position, (list, tuple)) or len(position) < 2:
                    continue
                normalized_positions.append((float(position[0]), float(position[1])))
            entity.custom_wheel_positions_m = tuple(normalized_positions)
            if normalized_positions:
                entity.wheel_count = len(normalized_positions)

        for field_name in ('wheel_orbit_yaws_deg', 'wheel_self_yaws_deg', 'armor_orbit_yaws_deg', 'armor_self_yaws_deg', 'armor_light_orbit_yaws_deg', 'armor_light_self_yaws_deg'):
            values = profile.get(field_name)
            if isinstance(values, list):
                setattr(entity, field_name, tuple(round(float(value), 3) for value in values))

        if float(getattr(entity, 'gimbal_length_m', 0.0)) > 1e-6 and float(getattr(entity, 'gimbal_body_height_m', 0.0)) > 1e-6:
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
        else:
            entity.gimbal_height_m = 0.0
        entity.armor_plate_size_m = max(
            float(getattr(entity, 'armor_plate_width_m', 0.12)),
            float(getattr(entity, 'armor_plate_length_m', 0.12)),
            float(getattr(entity, 'armor_plate_height_m', 0.12)),
        )
        entity.body_size_m = max(float(getattr(entity, 'body_length_m', 0.42)), float(getattr(entity, 'body_width_m', 0.42)))
        self._enforce_role_geometry_constraints(entity, entity_type, robot_type)

    def _rule_health(self, entity_type, fallback_max, fallback_initial):
        health_config = self.config.get('rules', {}).get('health', {}).get(entity_type, {})
        dedicated_config = self.config.get('rules', {}).get(entity_type, {})
        return (
            health_config.get('max_health', dedicated_config.get('max_health', fallback_max)),
            health_config.get(
                'initial_health',
                dedicated_config.get('initial_health', health_config.get('max_health', dedicated_config.get('max_health', fallback_initial))),
            ),
        )

    def _robot_role_key(self, robot_type):
        type_map = {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
            '哨兵': 'sentry',
        }
        return type_map.get(robot_type, 'infantry')

    def _robot_profile(self, robot_type):
        profiles = self.config.get('rules', {}).get('robot_profiles', {})
        return profiles.get(self._robot_role_key(robot_type), {})

    def _enforce_role_geometry_constraints(self, entity, entity_type, robot_type=None):
        if entity_type == 'robot' and robot_type == '工程':
            entity.gimbal_length_m = 0.0
            entity.gimbal_width_m = 0.0
            entity.gimbal_body_height_m = 0.0
            entity.gimbal_mount_gap_m = 0.0
            entity.gimbal_mount_length_m = 0.0
            entity.gimbal_mount_width_m = 0.0
            entity.gimbal_mount_height_m = 0.0
            entity.barrel_length_m = 0.0
            entity.barrel_radius_m = 0.0
        if entity_type == 'sentry' or (entity_type == 'robot' and robot_type in {'英雄', '工程'}):
            entity.suspension_style = 'four_bar'
            entity.rear_climb_assist_style = 'balance_leg'
        if entity_type == 'robot' and robot_type == '步兵' and str(getattr(entity, 'wheel_style', 'standard')) == 'legged':
            entity.suspension_style = 'four_bar'
            entity.rear_climb_assist_style = 'balance_leg'

    def _sync_traversal_constraints(self, entity):
        wheel_radius = max(0.01, float(getattr(entity, 'wheel_radius_m', 0.08)))
        direct_step_height = max(0.01, float(getattr(entity, 'direct_terrain_step_height_m', 0.06)))
        climb_step_height = max(direct_step_height, float(getattr(entity, 'max_step_climb_height_m', wheel_radius)))
        entity.direct_terrain_step_height_m = direct_step_height
        entity.max_step_climb_height_m = climb_step_height
        entity.max_terrain_step_height_m = direct_step_height

    def _performance_rules(self):
        return self.config.get('rules', {}).get('performance_profiles', {})

    def _level_rule(self, table, level):
        if not isinstance(table, dict):
            return {}
        level_key = str(int(max(1, level)))
        if level_key in table:
            return table[level_key]
        numeric_keys = sorted(int(key) for key in table.keys() if str(key).isdigit())
        if not numeric_keys:
            return {}
        fallback_key = max([key for key in numeric_keys if key <= int(level)] or [numeric_keys[0]])
        return table.get(str(fallback_key), {})

    def refresh_entity_performance_profile(self, entity, preserve_state=True):
        if entity.type != 'robot':
            return

        base_profile = dict(self._robot_profile(entity.robot_type))
        if entity.robot_type == '英雄':
            hero_rules = self._performance_rules().get('hero', {})
            weapon_mode = getattr(entity, 'gimbal_mode', 'ranged_priority')
            mode_table = hero_rules.get('weapon_modes', {}).get(weapon_mode, {})
            base_profile.update(self._level_rule(mode_table, getattr(entity, 'level', 1)))
        elif entity.robot_type == '步兵':
            infantry_rules = self._performance_rules().get('infantry', {})
            chassis_mode = getattr(entity, 'chassis_mode', 'health_priority')
            gimbal_mode = getattr(entity, 'gimbal_mode', 'cooling_priority')
            base_profile.update(self._level_rule(infantry_rules.get('chassis_modes', {}).get(chassis_mode, {}), getattr(entity, 'level', 1)))
            base_profile.update(self._level_rule(infantry_rules.get('gimbal_modes', {}).get(gimbal_mode, {}), getattr(entity, 'level', 1)))

        previous_max_health = max(float(getattr(entity, 'max_health', 0.0)), 1e-6)
        previous_health_ratio = float(getattr(entity, 'health', previous_max_health)) / previous_max_health
        previous_power_ratio = float(getattr(entity, 'power', 0.0)) / max(float(getattr(entity, 'max_power', 0.0)), 1e-6)

        self._apply_profile(entity, base_profile)

        if preserve_state:
            entity.health = max(1.0, min(float(entity.max_health), float(entity.max_health) * previous_health_ratio)) if entity.is_alive() else entity.health
            entity.power = max(0.0, min(float(entity.max_power), float(entity.max_power) * previous_power_ratio))
        else:
            entity.health = float(base_profile.get('initial_health', entity.max_health))
            entity.power = float(entity.max_power)

        entity.max_heat = float(base_profile.get('max_heat', entity.max_heat))
        entity.heat = min(float(entity.heat), float(entity.max_heat))
        self._sync_legacy_ammo(entity)

    def _sentry_profile(self, team):
        sentry_rules = self.config.get('rules', {}).get('sentry', {})
        mode_map = self.config.get('entities', {}).get('sentry_modes', {})
        default_mode = sentry_rules.get('default_mode', 'auto')
        mode = mode_map.get(team, default_mode)
        modes = sentry_rules.get('modes', {})
        profile = modes.get(mode, modes.get(default_mode, {}))
        return mode, profile

    def _sync_legacy_ammo(self, entity):
        if getattr(entity, 'ammo_type', None) == '42mm':
            entity.ammo = int(getattr(entity, 'allowed_ammo_42mm', 0))
        elif getattr(entity, 'ammo_type', None) == '17mm':
            entity.ammo = int(getattr(entity, 'allowed_ammo_17mm', 0))
        else:
            entity.ammo = 0

    def _apply_profile(self, entity, profile):
        entity.max_health = profile.get('max_health', entity.max_health)
        entity.health = profile.get('initial_health', entity.max_health)
        entity.max_power = profile.get('max_power', entity.max_power)
        entity.power = entity.max_power
        entity.power_recovery_rate = profile.get('power_recovery_rate', entity.power_recovery_rate)
        entity.max_heat = profile.get('max_heat', entity.max_heat)
        entity.heat_gain_per_shot = profile.get('heat_gain_per_shot', entity.heat_gain_per_shot)
        entity.heat_dissipation_rate = profile.get('heat_dissipation_rate', entity.heat_dissipation_rate)
        entity.fire_rate_hz = profile.get('fire_rate_hz', entity.fire_rate_hz)
        entity.ammo_per_shot = profile.get('ammo_per_shot', entity.ammo_per_shot)
        entity.power_per_shot = profile.get('power_per_shot', entity.power_per_shot)
        entity.ammo_type = profile.get('ammo_type', entity.ammo_type)
        entity.allowed_ammo_17mm = int(profile.get('initial_allowed_ammo_17mm', entity.allowed_ammo_17mm))
        entity.allowed_ammo_42mm = int(profile.get('initial_allowed_ammo_42mm', entity.allowed_ammo_42mm))
        self._sync_legacy_ammo(entity)

    def _configure_mobility_profile(self, entity, entity_type, robot_type=None):
        entity.vertical_scale_m = self.vertical_scale_m
        entity.can_climb_steps = entity_type in {'robot', 'sentry'}
        entity.chassis_supports_jump = False
        entity.chassis_speed_scale = 1.0
        entity.chassis_drive_power_limit_w = 180.0
        entity.chassis_drive_idle_draw_w = 16.0
        entity.chassis_drive_rpm_coeff = 0.00005
        entity.chassis_drive_accel_coeff = 0.012
        entity.body_shape = 'box'
        entity.body_clearance_m = 0.10
        entity.wheel_radius_m = 0.08
        entity.gimbal_height_m = 0.50
        entity.gimbal_length_m = 0.30
        entity.gimbal_width_m = 0.10
        entity.gimbal_body_height_m = 0.10
        entity.gimbal_mount_gap_m = 0.10
        entity.gimbal_mount_length_m = 0.10
        entity.gimbal_mount_width_m = 0.10
        entity.gimbal_mount_height_m = 0.10
        entity.barrel_length_m = 0.36
        entity.barrel_radius_m = 0.015
        entity.front_climb_assist_style = 'none'
        entity.rear_climb_assist_style = 'none'
        entity.front_climb_assist_top_length_m = 0.05
        entity.front_climb_assist_bottom_length_m = 0.03
        entity.front_climb_assist_plate_width_m = 0.018
        entity.front_climb_assist_plate_height_m = 0.18
        entity.front_climb_assist_forward_offset_m = 0.04
        entity.front_climb_assist_inner_offset_m = 0.06
        entity.rear_climb_assist_upper_length_m = 0.09
        entity.rear_climb_assist_lower_length_m = 0.08
        entity.rear_climb_assist_upper_width_m = 0.016
        entity.rear_climb_assist_upper_height_m = 0.016
        entity.rear_climb_assist_lower_width_m = 0.016
        entity.rear_climb_assist_lower_height_m = 0.016
        entity.rear_climb_assist_mount_offset_x_m = 0.03
        entity.rear_climb_assist_mount_height_m = 0.22
        entity.rear_climb_assist_inner_offset_m = 0.03
        entity.rear_climb_assist_upper_pair_gap_m = 0.06
        entity.rear_climb_assist_hinge_radius_m = 0.016
        entity.rear_climb_assist_knee_min_deg = 42.0
        entity.rear_climb_assist_knee_max_deg = 132.0
        entity.rear_climb_assist_knee_direction = 'rear'
        entity.wheel_style = 'standard'
        entity.suspension_style = 'none'
        entity.arm_style = 'none'
        entity.armor_plate_size_m = 0.12
        entity.armor_plate_width_m = 0.12
        entity.armor_plate_length_m = 0.12
        entity.armor_plate_height_m = 0.12
        entity.armor_plate_gap_m = 0.02
        entity.armor_light_length_m = 0.10
        entity.armor_light_width_m = 0.02
        entity.armor_light_height_m = 0.02
        entity.barrel_light_length_m = 0.10
        entity.barrel_light_width_m = 0.02
        entity.barrel_light_height_m = 0.02
        entity.body_render_width_scale = 0.82
        entity.max_pitch_up_deg = 30.0
        entity.max_pitch_down_deg = 30.0
        entity.direct_terrain_step_height_m = 0.06
        entity.max_step_climb_height_m = 0.06
        if entity_type == 'sentry':
            entity.chassis_subtype = ''
            entity.step_climb_duration_sec = 1.0
            entity.max_step_climb_height_m = 0.35
            entity.collision_radius = 24.0
            entity.wheel_count = 4
            entity.body_size_m = 0.55
            entity.body_length_m = 0.55
            entity.body_width_m = 0.50
            entity.body_height_m = 0.20
            entity.body_clearance_m = 0.10
            entity.wheel_radius_m = 0.08
            entity.wheel_style = 'mecanum'
            entity.suspension_style = 'four_bar'
            entity.front_climb_assist_style = 'belt_lift'
            entity.rear_climb_assist_style = 'balance_leg'
            entity.armor_plate_size_m = 0.16
            entity.armor_plate_width_m = 0.16
            entity.armor_plate_length_m = 0.16
            entity.armor_plate_height_m = 0.16
            entity.gimbal_mount_gap_m = 0.10
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
            entity.barrel_length_m = 0.36
            entity.barrel_radius_m = 0.015
            self._apply_appearance_profile(entity, entity_type, robot_type=robot_type)
            self._sync_traversal_constraints(entity)
            return
        if entity_type != 'robot':
            entity.chassis_subtype = ''
            entity.can_climb_steps = False
            entity.step_climb_duration_sec = 0.0
            static_radius = {
                'uav': 10.0,
                'dart': 6.0,
                'radar': 15.0,
                'outpost': 50.0,
                'base': 100.0,
            }
            entity.collision_radius = static_radius.get(entity_type, 10.0)
            entity.wheel_count = 0
            return
        if robot_type == '步兵':
            entity.chassis_subtype = self._resolve_robot_subtype(robot_type, getattr(entity, 'chassis_subtype', None))
            subtype_preset = infantry_chassis_preset(entity.chassis_subtype)
            entity.step_climb_duration_sec = 0.75 if entity.chassis_subtype == 'omni_wheel' else 1.0
            entity.direct_terrain_step_height_m = 0.08 if entity.chassis_subtype == 'omni_wheel' else 0.06
            entity.max_step_climb_height_m = 0.14 if entity.chassis_subtype == 'omni_wheel' else 0.35
            entity.collision_radius = 18.0 if entity.chassis_subtype == 'omni_wheel' else 16.0
            entity.wheel_count = int(subtype_preset.get('wheel_count', 2))
            entity.body_size_m = max(float(subtype_preset.get('body_length_m', 0.50)), float(subtype_preset.get('body_width_m', 0.45)))
            entity.body_length_m = float(subtype_preset.get('body_length_m', 0.50))
            entity.body_width_m = float(subtype_preset.get('body_width_m', 0.45))
            entity.body_height_m = float(subtype_preset.get('body_height_m', 0.20))
            entity.body_clearance_m = float(subtype_preset.get('body_clearance_m', 0.20))
            entity.wheel_radius_m = float(subtype_preset.get('wheel_radius_m', 0.06))
            entity.body_render_width_scale = float(subtype_preset.get('body_render_width_scale', 0.73))
            entity.body_shape = str(subtype_preset.get('body_shape', 'box'))
            entity.wheel_style = str(subtype_preset.get('wheel_style', 'legged'))
            entity.suspension_style = str(subtype_preset.get('suspension_style', 'five_link'))
            entity.front_climb_assist_style = str(subtype_preset.get('front_climb_assist_style', 'none'))
            entity.rear_climb_assist_style = str(subtype_preset.get('rear_climb_assist_style', 'balance_leg'))
            entity.rear_climb_assist_knee_direction = 'rear'
            entity.custom_wheel_positions_m = tuple((float(position[0]), float(position[1])) for position in subtype_preset.get('custom_wheel_positions_m', ()))
            entity.wheel_orbit_yaws_deg = tuple(float(value) for value in subtype_preset.get('wheel_orbit_yaws_deg', ()))
            entity.wheel_self_yaws_deg = tuple(float(value) for value in subtype_preset.get('wheel_self_yaws_deg', ()))
            entity.armor_orbit_yaws_deg = tuple(float(value) for value in subtype_preset.get('armor_orbit_yaws_deg', (0.0, 180.0, 90.0, 270.0)))
            entity.armor_self_yaws_deg = tuple(float(value) for value in subtype_preset.get('armor_self_yaws_deg', (0.0, 180.0, 90.0, 270.0)))
            entity.armor_light_orbit_yaws_deg = tuple(float(value) for value in subtype_preset.get('armor_light_orbit_yaws_deg', entity.armor_orbit_yaws_deg))
            entity.armor_light_self_yaws_deg = tuple(float(value) for value in subtype_preset.get('armor_light_self_yaws_deg', entity.armor_self_yaws_deg))
            entity.chassis_supports_jump = bool(subtype_preset.get('chassis_supports_jump', True))
            entity.chassis_speed_scale = float(subtype_preset.get('chassis_speed_scale', 1.0))
            entity.chassis_drive_power_limit_w = float(subtype_preset.get('chassis_drive_power_limit_w', 150.0))
            entity.chassis_drive_idle_draw_w = float(subtype_preset.get('chassis_drive_idle_draw_w', 16.0))
            entity.chassis_drive_rpm_coeff = float(subtype_preset.get('chassis_drive_rpm_coeff', 0.00005))
            entity.chassis_drive_accel_coeff = float(subtype_preset.get('chassis_drive_accel_coeff', 0.012))
            entity.armor_plate_size_m = 0.16
            entity.armor_plate_width_m = 0.16
            entity.armor_plate_length_m = 0.16
            entity.armor_plate_height_m = 0.16
            entity.gimbal_length_m = 0.30
            entity.gimbal_width_m = 0.10
            entity.gimbal_body_height_m = 0.10
            entity.gimbal_mount_gap_m = 0.10
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
            entity.barrel_length_m = 0.36
            entity.barrel_radius_m = 0.015
        elif robot_type == '英雄':
            entity.chassis_subtype = ''
            entity.step_climb_duration_sec = 1.0
            entity.max_step_climb_height_m = 0.35
            entity.collision_radius = 20.0
            entity.wheel_count = 4
            entity.body_size_m = 0.60
            entity.body_length_m = 0.65
            entity.body_width_m = 0.55
            entity.body_height_m = 0.20
            entity.body_clearance_m = 0.10
            entity.wheel_radius_m = 0.08
            entity.wheel_style = 'mecanum'
            entity.suspension_style = 'four_bar'
            entity.front_climb_assist_style = 'belt_lift'
            entity.rear_climb_assist_style = 'balance_leg'
            entity.rear_climb_assist_knee_direction = 'front'
            entity.armor_plate_size_m = 0.24
            entity.armor_plate_width_m = 0.24
            entity.armor_plate_length_m = 0.24
            entity.armor_plate_height_m = 0.24
            entity.gimbal_length_m = 0.35
            entity.gimbal_width_m = 0.15
            entity.gimbal_body_height_m = 0.15
            entity.gimbal_mount_gap_m = 0.10
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
            entity.barrel_length_m = 0.48
            entity.barrel_radius_m = 0.020
        elif robot_type == '工程':
            entity.chassis_subtype = ''
            entity.step_climb_duration_sec = 1.0
            entity.max_step_climb_height_m = 0.35
            entity.collision_radius = 21.0
            entity.wheel_count = 4
            entity.body_size_m = 0.55
            entity.body_length_m = 0.55
            entity.body_width_m = 0.50
            entity.body_height_m = 0.20
            entity.body_clearance_m = 0.10
            entity.wheel_radius_m = 0.08
            entity.wheel_style = 'mecanum'
            entity.arm_style = 'fixed_7'
            entity.front_climb_assist_style = 'belt_lift'
            entity.rear_climb_assist_style = 'balance_leg'
            entity.rear_climb_assist_knee_direction = 'front'
            entity.armor_plate_size_m = 0.16
            entity.armor_plate_width_m = 0.16
            entity.armor_plate_length_m = 0.16
            entity.armor_plate_height_m = 0.16
            entity.gimbal_length_m = 0.0
            entity.gimbal_width_m = 0.0
            entity.gimbal_body_height_m = 0.0
            entity.gimbal_mount_gap_m = 0.0
            entity.gimbal_mount_length_m = 0.0
            entity.gimbal_mount_width_m = 0.0
            entity.gimbal_mount_height_m = 0.0
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
            entity.barrel_length_m = 0.0
            entity.barrel_radius_m = 0.0
        else:
            entity.chassis_subtype = ''
            entity.step_climb_duration_sec = 1.0
            entity.max_step_climb_height_m = 0.35
            entity.collision_radius = 18.0
            entity.wheel_count = 4
            entity.body_size_m = 0.50
            entity.body_length_m = 0.50
            entity.body_width_m = 0.45
            entity.body_height_m = 0.20
            entity.body_clearance_m = 0.20
            entity.wheel_radius_m = 0.06
            entity.wheel_style = 'legged'
            entity.suspension_style = 'four_bar'
            entity.rear_climb_assist_style = 'balance_leg'
            entity.rear_climb_assist_knee_direction = 'front' if robot_type == '哨兵' else 'rear'
            entity.armor_plate_size_m = 0.16
            entity.armor_plate_width_m = 0.16
            entity.armor_plate_length_m = 0.16
            entity.armor_plate_height_m = 0.16
            entity.gimbal_length_m = 0.30
            entity.gimbal_width_m = 0.10
            entity.gimbal_body_height_m = 0.10
            entity.gimbal_mount_gap_m = 0.10
            entity.gimbal_height_m = self._resolve_gimbal_center_height(entity)
            entity.barrel_length_m = 0.36
            entity.barrel_radius_m = 0.015
        self._apply_appearance_profile(entity, entity_type, robot_type=robot_type)
        self._sync_traversal_constraints(entity)
    
    def create_entity(self, entity_id, entity_type, team, position, angle=0, robot_type=None, robot_subtype=None):
        """创建单个实体"""
        entity = Entity(entity_id, entity_type, team, position, angle, robot_type)
        entity.chassis_subtype = self._resolve_robot_subtype(robot_type, robot_subtype)
        self.entities.append(entity)
        self.entity_map[entity_id] = entity

        robot_levels = self.config.get('entities', {}).get('robot_levels', {})
        entity.level = robot_levels.get(entity_id.replace(f'{team}_', ''), 1)
        entity.display_name = entity_id
        entity.movable = self.enable_entity_movement and entity_type in {'robot', 'sentry'}
        entity.collidable = entity_type in {'robot', 'sentry', 'uav', 'dart', 'radar'}
        self._configure_mobility_profile(entity, entity_type, robot_type)

        # 基础血量/弹量配置
        sentry_rules = self.config.get('rules', {}).get('sentry', {})
        heat_system = self.config.get('physics', {}).get('heat_system', {})
        sentry_heat_system = heat_system.get('sentry', {})
        if entity_type == 'sentry':
            sentry_mode, sentry_profile = self._sentry_profile(team)
            entity.sentry_mode = sentry_mode
            self._apply_profile(entity, sentry_profile)
            entity.heat = 0
            entity.display_name = f'{team}_7'
        elif entity_type == 'base':
            max_health, initial_health = self._rule_health('base', 2000, 2000)
            entity.max_health = max_health
            entity.health = initial_health
            entity.level = 0
            entity.display_name = f'{team}_base'
            entity.movable = False
            entity.collidable = False
        elif entity_type == 'outpost':
            max_health, initial_health = self._rule_health('outpost', 1500, 1500)
            entity.max_health = max_health
            entity.health = initial_health
            entity.level = 0
            entity.display_name = f'{team}_outpost'
            entity.movable = False
            entity.collidable = False
        elif entity_type == 'robot':
            if robot_type == '英雄':
                entity.gimbal_mode = 'ranged_priority'
            self._apply_profile(entity, self._robot_profile(robot_type))
            self.refresh_entity_performance_profile(entity, preserve_state=False)
            entity.display_name = entity_id
        elif entity_type in {'uav', 'dart', 'radar'}:
            max_health, initial_health = self._rule_health(entity_type, entity.max_health, entity.health)
            entity.max_health = max_health
            entity.health = initial_health
        
        # 设置功率和热量参数
        if entity_type == 'robot' and robot_type:
            power_system = self.config.get('physics', {}).get('power_system', {})
            key = self._robot_role_key(robot_type)
            
            if key in power_system and 'max_power' not in self._robot_profile(robot_type):
                entity.max_power = power_system[key]['max_power']
                entity.power = entity.max_power
                entity.power_recovery_rate = power_system[key]['power_recovery_rate']
            
            if key in heat_system and 'max_heat' not in self._robot_profile(robot_type):
                entity.max_heat = heat_system[key]['max_heat']
                entity.heat_gain_per_shot = heat_system[key]['heat_gain_per_shot']
                entity.heat_dissipation_rate = heat_system[key]['heat_dissipation_rate']

        if entity_type == 'sentry' and not sentry_rules.get('modes'):
            entity.max_heat = sentry_rules.get('max_heat', sentry_heat_system.get('max_heat', 150))
            entity.heat_gain_per_shot = sentry_heat_system.get('heat_gain_per_shot', entity.heat_gain_per_shot)
            entity.heat_dissipation_rate = sentry_heat_system.get('heat_dissipation_rate', entity.heat_dissipation_rate)
            entity.allowed_ammo_17mm = int(sentry_rules.get('initial_ammo', 300))
            self._sync_legacy_ammo(entity)
        
        return entity
    
    def create_entities(self):
        """根据配置创建所有实体"""
        initial_positions = self.config.get('entities', {}).get('initial_positions', {})
        robot_types = self.config.get('entities', {}).get('robot_types', {})
        robot_subtypes = self.config.get('entities', {}).get('robot_subtypes', {})
        
        # 创建红方实体
        red_positions = initial_positions.get('red', {})
        for name, pos in red_positions.items():
            if name.startswith('robot_'):
                entity_id = f"red_{name}"
                entity_type = "sentry" if name == 'robot_7' else "robot"
                robot_type = robot_types.get(name, "步兵")
                robot_subtype = robot_subtypes.get(name)
            else:
                continue
            
            position = {'x': pos['x'], 'y': pos['y'], 'z': pos.get('height', 0)}
            angle = pos.get('angle', 0)
            self.create_entity(entity_id, entity_type, "red", position, angle, robot_type, robot_subtype)
        
        # 创建蓝方实体
        blue_positions = initial_positions.get('blue', {})
        for name, pos in blue_positions.items():
            if name.startswith('robot_'):
                entity_id = f"blue_{name}"
                entity_type = "sentry" if name == 'robot_7' else "robot"
                robot_type = robot_types.get(name, "步兵")
                robot_subtype = robot_subtypes.get(name)
            else:
                continue
            
            position = {'x': pos['x'], 'y': pos['y'], 'z': pos.get('height', 0)}
            angle = pos.get('angle', 0)
            self.create_entity(entity_id, entity_type, "blue", position, angle, robot_type, robot_subtype)

        self._create_structure_entities()

    def _create_structure_entities(self):
        facility_map = {facility.get('id'): facility for facility in self.config.get('map', {}).get('facilities', [])}
        for entity_id, facility_id, entity_type, team in [
            ('red_base', 'red_base', 'base', 'red'),
            ('blue_base', 'blue_base', 'base', 'blue'),
            ('red_outpost', 'red_outpost', 'outpost', 'red'),
            ('blue_outpost', 'blue_outpost', 'outpost', 'blue'),
        ]:
            if entity_id in self.entity_map:
                continue
            facility = facility_map.get(facility_id)
            if facility is None:
                for candidate in self.config.get('map', {}).get('facilities', []):
                    if candidate.get('type') == entity_type and candidate.get('team') == team:
                        facility = candidate
                        break
            if not facility:
                continue
            position = {
                'x': int((facility['x1'] + facility['x2']) / 2),
                'y': int((facility['y1'] + facility['y2']) / 2),
                'z': 0,
            }
            self.create_entity(entity_id, entity_type, team, position, 0, None)
    
    def get_entity(self, entity_id):
        """根据ID获取实体"""
        return self.entity_map.get(entity_id)
    
    def get_entities_by_team(self, team):
        """获取指定队伍的所有实体"""
        return [e for e in self.entities if e.team == team]
    
    def get_entities_by_type(self, entity_type):
        """获取指定类型的所有实体"""
        return [e for e in self.entities if e.type == entity_type]
    
    def update(self, dt):
        """更新所有实体"""
        for entity in self.entities:
            entity.update(dt)

    def export_initial_positions(self):
        """导出当前实体位置为配置格式。"""
        initial_positions = {'red': {}, 'blue': {}}
        for entity in self.entities:
            team_positions = initial_positions.setdefault(entity.team, {})
            key = entity.id.replace(f"{entity.team}_", "")
            if key not in self.editable_entity_keys:
                continue
            payload = {
                'x': int(entity.position['x']),
                'y': int(entity.position['y']),
            }
            if entity.angle:
                payload['angle'] = entity.angle
            if entity.position.get('z', 0):
                payload['height'] = entity.position.get('z', 0)
            team_positions[key] = payload
        return initial_positions

    def export_robot_subtypes(self):
        robot_subtypes = {}
        for entity in self.entities:
            if entity.type != 'robot' or entity.robot_type != '步兵':
                continue
            key = entity.id.replace(f"{entity.team}_", "")
            if key not in self.editable_entity_keys:
                continue
            robot_subtypes[key] = self._resolve_robot_subtype(entity.robot_type, getattr(entity, 'chassis_subtype', None))
        return robot_subtypes

    def set_entity_chassis_subtype(self, entity, chassis_subtype, preserve_state=True):
        if entity is None or getattr(entity, 'type', None) != 'robot' or getattr(entity, 'robot_type', '') != '步兵':
            return False
        resolved_subtype = self._resolve_robot_subtype(entity.robot_type, chassis_subtype)
        if resolved_subtype == getattr(entity, 'chassis_subtype', None):
            return True
        entity.chassis_subtype = resolved_subtype
        self._configure_mobility_profile(entity, entity.type, entity.robot_type)
        self.refresh_entity_performance_profile(entity, preserve_state=preserve_state)
        return True

    def export_entity_states(self):
        """导出对局快照。"""
        states = []
        for entity in self.entities:
            states.append({
                'id': entity.id,
                'type': entity.type,
                'team': entity.team,
                'robot_type': entity.robot_type,
                'chassis_subtype': getattr(entity, 'chassis_subtype', ''),
                'position': dict(entity.position),
                'spawn_position': dict(entity.spawn_position),
                'angle': entity.angle,
                'spawn_angle': entity.spawn_angle,
                'turret_angle': entity.turret_angle,
                'velocity': dict(entity.velocity),
                'angular_velocity': entity.angular_velocity,
                'health': entity.health,
                'max_health': entity.max_health,
                'state': entity.state,
                'target': entity.target,
                'power': entity.power,
                'heat': entity.heat,
                'heat_lock_state': entity.heat_lock_state,
                'heat_lock_reason': entity.heat_lock_reason,
                'heat_ui_disabled': entity.heat_ui_disabled,
                'heat_cooling_accumulator': entity.heat_cooling_accumulator,
                'ammo': entity.ammo,
                'allowed_ammo_17mm': entity.allowed_ammo_17mm,
                'allowed_ammo_42mm': entity.allowed_ammo_42mm,
                'ammo_type': entity.ammo_type,
                'gold': entity.gold,
                'sentry_mode': entity.sentry_mode,
                'chassis_mode': entity.chassis_mode,
                'gimbal_mode': entity.gimbal_mode,
                'posture': entity.posture,
                'posture_cooldown': entity.posture_cooldown,
                'posture_active_time': entity.posture_active_time,
                'shot_cooldown': entity.shot_cooldown,
                'overheat_lock_timer': entity.overheat_lock_timer,
                'autoaim_locked_target_id': entity.autoaim_locked_target_id,
                'autoaim_lock_timer': entity.autoaim_lock_timer,
                'evasive_spin_timer': entity.evasive_spin_timer,
                'evasive_spin_direction': entity.evasive_spin_direction,
                'evasive_spin_rate_deg': entity.evasive_spin_rate_deg,
                'last_damage_source_id': entity.last_damage_source_id,
                'respawn_timer': entity.respawn_timer,
                'respawn_duration': entity.respawn_duration,
                'respawn_recovery_timer': entity.respawn_recovery_timer,
                'invincible_timer': entity.invincible_timer,
                'weak_timer': entity.weak_timer,
                'respawn_invalid_timer': entity.respawn_invalid_timer,
                'respawn_invalid_elapsed': entity.respawn_invalid_elapsed,
                'respawn_invalid_pending_release': entity.respawn_invalid_pending_release,
                'respawn_weak_active': entity.respawn_weak_active,
                'respawn_mode': entity.respawn_mode,
                'instant_respawn_count': entity.instant_respawn_count,
                'death_handled': entity.death_handled,
                'permanent_eliminated': entity.permanent_eliminated,
                'elimination_reason': entity.elimination_reason,
                'fort_buff_active': entity.fort_buff_active,
                'terrain_buff_timer': entity.terrain_buff_timer,
                'supply_cooldown': entity.supply_cooldown,
                'supply_ammo_claimed': entity.supply_ammo_claimed,
                'role_purchase_cooldown': entity.role_purchase_cooldown,
                'carried_minerals': entity.carried_minerals,
                'mining_timer': entity.mining_timer,
                'mining_target_duration': entity.mining_target_duration,
                'exchange_timer': entity.exchange_timer,
                'exchange_target_duration': entity.exchange_target_duration,
                'mining_zone_id': entity.mining_zone_id,
                'exchange_zone_id': entity.exchange_zone_id,
                'dynamic_damage_taken_mult': entity.dynamic_damage_taken_mult,
                'dynamic_damage_dealt_mult': entity.dynamic_damage_dealt_mult,
                'dynamic_cooling_mult': entity.dynamic_cooling_mult,
                'dynamic_power_recovery_mult': entity.dynamic_power_recovery_mult,
                'dynamic_power_capacity_mult': entity.dynamic_power_capacity_mult,
                'dynamic_invincible': entity.dynamic_invincible,
                'timed_buffs': dict(entity.timed_buffs),
                'buff_cooldowns': dict(entity.buff_cooldowns),
                    'buff_path_progress': dict(entity.buff_path_progress),
                'assembly_buff_time_used': entity.assembly_buff_time_used,
                'hero_deployment_target_id': entity.hero_deployment_target_id,
                'hero_deployment_hit_probability': entity.hero_deployment_hit_probability,
                'last_combat_time': entity.last_combat_time,
                'pending_rule_events': list(entity.pending_rule_events),
                'front_gun_locked': entity.front_gun_locked,
            })
        return states

    def import_entity_states(self, states):
        """从快照恢复对局状态。"""
        state_map = {state['id']: state for state in states}
        for entity in self.entities:
            if entity.id not in state_map:
                continue
            state = state_map[entity.id]
            restored_robot_type = state.get('robot_type', entity.robot_type)
            entity.robot_type = restored_robot_type
            entity.chassis_subtype = self._resolve_robot_subtype(restored_robot_type, state.get('chassis_subtype', getattr(entity, 'chassis_subtype', None)))
            self._configure_mobility_profile(entity, entity.type, entity.robot_type)
            entity.position = dict(state.get('position', entity.position))
            entity.spawn_position = dict(state.get('spawn_position', entity.spawn_position))
            entity.angle = state.get('angle', entity.angle)
            entity.spawn_angle = state.get('spawn_angle', entity.spawn_angle)
            entity.turret_angle = state.get('turret_angle', entity.turret_angle)
            entity.velocity = dict(state.get('velocity', entity.velocity))
            entity.angular_velocity = state.get('angular_velocity', entity.angular_velocity)
            entity.health = state.get('health', entity.health)
            entity.max_health = state.get('max_health', entity.max_health)
            entity.state = state.get('state', entity.state)
            entity.target = state.get('target', entity.target)
            entity.power = state.get('power', entity.power)
            entity.heat = state.get('heat', entity.heat)
            entity.heat_lock_state = state.get('heat_lock_state', entity.heat_lock_state)
            entity.heat_lock_reason = state.get('heat_lock_reason', entity.heat_lock_reason)
            entity.heat_ui_disabled = state.get('heat_ui_disabled', entity.heat_ui_disabled)
            entity.heat_cooling_accumulator = state.get('heat_cooling_accumulator', entity.heat_cooling_accumulator)
            entity.ammo = state.get('ammo', entity.ammo)
            entity.allowed_ammo_17mm = state.get('allowed_ammo_17mm', entity.allowed_ammo_17mm)
            entity.allowed_ammo_42mm = state.get('allowed_ammo_42mm', entity.allowed_ammo_42mm)
            entity.ammo_type = state.get('ammo_type', entity.ammo_type)
            entity.gold = state.get('gold', entity.gold)
            entity.sentry_mode = state.get('sentry_mode', entity.sentry_mode)
            entity.chassis_mode = state.get('chassis_mode', entity.chassis_mode)
            entity.gimbal_mode = state.get('gimbal_mode', entity.gimbal_mode)
            entity.posture = state.get('posture', entity.posture)
            entity.posture_cooldown = state.get('posture_cooldown', entity.posture_cooldown)
            entity.posture_active_time = state.get('posture_active_time', entity.posture_active_time)
            entity.shot_cooldown = state.get('shot_cooldown', entity.shot_cooldown)
            entity.overheat_lock_timer = state.get('overheat_lock_timer', entity.overheat_lock_timer)
            entity.autoaim_locked_target_id = state.get('autoaim_locked_target_id', entity.autoaim_locked_target_id)
            entity.autoaim_lock_timer = state.get('autoaim_lock_timer', entity.autoaim_lock_timer)
            entity.evasive_spin_timer = state.get('evasive_spin_timer', entity.evasive_spin_timer)
            entity.evasive_spin_direction = state.get('evasive_spin_direction', entity.evasive_spin_direction)
            entity.evasive_spin_rate_deg = state.get('evasive_spin_rate_deg', entity.evasive_spin_rate_deg)
            entity.last_damage_source_id = state.get('last_damage_source_id', entity.last_damage_source_id)
            entity.respawn_timer = state.get('respawn_timer', entity.respawn_timer)
            entity.respawn_duration = state.get('respawn_duration', entity.respawn_duration)
            entity.respawn_recovery_timer = state.get('respawn_recovery_timer', entity.respawn_recovery_timer)
            entity.invincible_timer = state.get('invincible_timer', entity.invincible_timer)
            entity.weak_timer = state.get('weak_timer', entity.weak_timer)
            entity.respawn_invalid_timer = state.get('respawn_invalid_timer', entity.respawn_invalid_timer)
            entity.respawn_invalid_elapsed = state.get('respawn_invalid_elapsed', entity.respawn_invalid_elapsed)
            entity.respawn_invalid_pending_release = state.get('respawn_invalid_pending_release', entity.respawn_invalid_pending_release)
            entity.respawn_weak_active = state.get('respawn_weak_active', entity.respawn_weak_active)
            entity.respawn_mode = state.get('respawn_mode', entity.respawn_mode)
            entity.instant_respawn_count = state.get('instant_respawn_count', entity.instant_respawn_count)
            entity.death_handled = state.get('death_handled', entity.death_handled)
            entity.permanent_eliminated = state.get('permanent_eliminated', entity.permanent_eliminated)
            entity.elimination_reason = state.get('elimination_reason', entity.elimination_reason)
            entity.fort_buff_active = state.get('fort_buff_active', entity.fort_buff_active)
            entity.terrain_buff_timer = state.get('terrain_buff_timer', entity.terrain_buff_timer)
            entity.supply_cooldown = state.get('supply_cooldown', entity.supply_cooldown)
            entity.supply_ammo_claimed = state.get('supply_ammo_claimed', entity.supply_ammo_claimed)
            entity.role_purchase_cooldown = state.get('role_purchase_cooldown', entity.role_purchase_cooldown)
            entity.carried_minerals = state.get('carried_minerals', entity.carried_minerals)
            entity.mining_timer = state.get('mining_timer', entity.mining_timer)
            entity.mining_target_duration = state.get('mining_target_duration', entity.mining_target_duration)
            entity.exchange_timer = state.get('exchange_timer', entity.exchange_timer)
            entity.exchange_target_duration = state.get('exchange_target_duration', entity.exchange_target_duration)
            entity.mining_zone_id = state.get('mining_zone_id', entity.mining_zone_id)
            entity.exchange_zone_id = state.get('exchange_zone_id', entity.exchange_zone_id)
            entity.dynamic_damage_taken_mult = state.get('dynamic_damage_taken_mult', entity.dynamic_damage_taken_mult)
            entity.dynamic_damage_dealt_mult = state.get('dynamic_damage_dealt_mult', entity.dynamic_damage_dealt_mult)
            entity.dynamic_cooling_mult = state.get('dynamic_cooling_mult', entity.dynamic_cooling_mult)
            entity.dynamic_power_recovery_mult = state.get('dynamic_power_recovery_mult', entity.dynamic_power_recovery_mult)
            entity.dynamic_power_capacity_mult = state.get('dynamic_power_capacity_mult', entity.dynamic_power_capacity_mult)
            entity.dynamic_invincible = state.get('dynamic_invincible', entity.dynamic_invincible)
            entity.timed_buffs = dict(state.get('timed_buffs', entity.timed_buffs))
            entity.buff_cooldowns = dict(state.get('buff_cooldowns', entity.buff_cooldowns))
            entity.buff_path_progress = dict(state.get('buff_path_progress', entity.buff_path_progress))
            entity.assembly_buff_time_used = state.get('assembly_buff_time_used', entity.assembly_buff_time_used)
            entity.hero_deployment_target_id = state.get('hero_deployment_target_id', entity.hero_deployment_target_id)
            entity.hero_deployment_hit_probability = state.get('hero_deployment_hit_probability', entity.hero_deployment_hit_probability)
            entity.last_combat_time = state.get('last_combat_time', entity.last_combat_time)
            entity.pending_rule_events = list(state.get('pending_rule_events', entity.pending_rule_events))
            entity.front_gun_locked = state.get('front_gun_locked', entity.front_gun_locked)
            self.refresh_entity_performance_profile(entity, preserve_state=True)
            self._sync_legacy_ammo(entity)
    
    def remove_entity(self, entity_id):
        """移除实体"""
        if entity_id in self.entity_map:
            entity = self.entity_map[entity_id]
            self.entities.remove(entity)
            del self.entity_map[entity_id]
