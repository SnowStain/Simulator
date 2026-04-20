#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
from types import SimpleNamespace

from control.player_look import clamp_entity_pitch


def _default_role_decision_specs():
    return {
        'hero': (
            {'id': 'hero_seek_cover', 'label': '英雄找掩护'},
            {'id': 'hero_lob_shot', 'label': '英雄吊射'},
            {'id': 'teamfight_push', 'label': '团战推进'},
        ),
        'engineer': (
            {'id': 'engineer_mining_cycle', 'label': '取矿兑矿'},
            {'id': 'engineer_exchange', 'label': '回家兑矿'},
            {'id': 'support_engineer', 'label': '护送工程'},
        ),
        'infantry': (
            {'id': 'patrol_key_facilities', 'label': '巡关键设施'},
            {'id': 'teamfight_cover', 'label': '团战掩护'},
            {'id': 'pursue_enemy', 'label': '追击目标'},
        ),
        'sentry': (
            {'id': 'sentry_opening_highground', 'label': '抢台阶高地'},
            {'id': 'sentry_engage', 'label': '哨兵交战'},
            {'id': 'support_structures', 'label': '保基地前哨'},
        ),
    }


class Controller:
    def __init__(self, config):
        self.config = config or {}
        self._ai_controllers = (SimpleNamespace(role_decision_specs=_default_role_decision_specs()),)

    def shutdown(self):
        return None

    def set_player_look_sensitivity(self, yaw_sensitivity_deg=None, pitch_sensitivity_deg=None):
        if not isinstance(self.config, dict):
            return
        simulator_cfg = self.config.setdefault('simulator', {})
        if yaw_sensitivity_deg is not None:
            simulator_cfg['player_mouse_yaw_sensitivity_deg'] = float(yaw_sensitivity_deg)
        if pitch_sensitivity_deg is not None:
            simulator_cfg['player_mouse_pitch_sensitivity_deg'] = float(pitch_sensitivity_deg)

    def _meters_to_world_units(self, map_manager, value_m):
        if map_manager is not None and hasattr(map_manager, 'meters_to_world_units'):
            try:
                return float(map_manager.meters_to_world_units(float(value_m)))
            except Exception:
                pass
        return float(value_m)

    def _role_key(self, entity):
        if getattr(entity, 'type', None) == 'sentry':
            return 'sentry'
        return {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
        }.get(getattr(entity, 'robot_type', ''), 'infantry')

    def _update_manual_entity(self, entity, map_manager, manual_state):
        movement = manual_state.get('movement', {}) if isinstance(manual_state, dict) else {}
        look_dx = float(manual_state.get('look_dx', 0.0) or 0.0) if isinstance(manual_state, dict) else 0.0
        look_dy = float(manual_state.get('look_dy', 0.0) or 0.0) if isinstance(manual_state, dict) else 0.0

        forward = 1.0 if movement.get('forward') else 0.0
        backward = 1.0 if movement.get('backward') else 0.0
        left = 1.0 if movement.get('left') else 0.0
        right = 1.0 if movement.get('right') else 0.0
        move_forward = forward - backward
        move_right = right - left

        speed_mps = 2.2
        speed_world = self._meters_to_world_units(map_manager, speed_mps)
        yaw_rad = math.radians(float(getattr(entity, 'angle', 0.0) or 0.0))
        dir_x = math.cos(yaw_rad) * move_forward - math.sin(yaw_rad) * move_right
        dir_y = math.sin(yaw_rad) * move_forward + math.cos(yaw_rad) * move_right

        length = math.hypot(dir_x, dir_y)
        if length > 1e-6:
            dir_x /= length
            dir_y /= length
        entity.set_velocity(dir_x * speed_world, dir_y * speed_world, 0.0)

        if look_dx != 0.0:
            entity.turret_angle = (float(getattr(entity, 'turret_angle', entity.angle)) + look_dx) % 360.0
        if look_dy != 0.0:
            desired_pitch = float(getattr(entity, 'gimbal_pitch_deg', 0.0)) + look_dy
            entity.gimbal_pitch_deg = clamp_entity_pitch(entity, desired_pitch, config=self.config)

        if movement.get('small_gyro'):
            entity.small_gyro_active = True

    def _update_idle_ai_hint(self, entity):
        if not getattr(entity, 'is_alive', lambda: False)():
            return
        role_key = self._role_key(entity)
        specs = self._ai_controllers[0].role_decision_specs.get(role_key, ())
        forced = str(getattr(entity, 'test_forced_decision_id', '') or '')
        selected = forced if forced else (specs[0]['id'] if specs else '')
        entity.ai_decision_selected = selected
        entity.ai_decision = selected or 'idle'
        entity.ai_behavior_node = selected
        top = []
        for index, spec in enumerate(specs[:3]):
            score = max(0.0, 1.0 - index * 0.2)
            top.append({'id': spec['id'], 'label': spec['label'], 'weight': score, 'matched': index == 0})
        entity.ai_decision_top3 = tuple(top)
        entity.ai_decision_weights = tuple(top)

    def update(
        self,
        entities,
        map_manager,
        rules_engine,
        game_time,
        game_duration,
        controlled_entity_ids=None,
        ai_excluded_entity_ids=None,
        manual_entity_ids=None,
        manual_state=None,
    ):
        manual_ids = set(manual_entity_ids or ())
        excluded_ids = set(ai_excluded_entity_ids or ())
        for entity in entities:
            if entity.id in manual_ids:
                self._update_manual_entity(entity, map_manager, manual_state or {})
            elif entity.id in excluded_ids:
                entity.set_velocity(0.0, 0.0, 0.0)
                entity.angular_velocity = 0.0
            else:
                self._update_idle_ai_hint(entity)
