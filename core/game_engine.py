#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
import pygame
import time
import threading
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy

import json
import os
import statistics
from collections import deque
from datetime import datetime
from map.map_manager import MapManager
from entities.chassis_profiles import infantry_chassis_label, infantry_chassis_options
from entities.entity_manager import EntityManager
from physics.physics_engine import PhysicsEngine
from rules.rules_engine import RulesEngine
from control.controller import Controller
from control.player_look import clamp_entity_pitch, get_player_mouse_input_settings, scale_player_mouse_motion, set_player_mouse_input_settings
from core.message_bus import MessageBus
from state_machine.sentry_state_machine import SentryStateMachine


class GameEngine:
    MATCH_MODES = {'full', 'single_unit_test'}
    SINGLE_UNIT_TEST_ENTITY_KEYS = ('robot_1', 'robot_2', 'robot_3', 'robot_4', 'robot_7')
    DEBUG_FEATURE_DEFAULTS = {
        'entity_update': True,
        'controller': True,
        'physics': True,
        'auto_aim': True,
        'rules': True,
        'controller.hero.pathfinding': True,
        'controller.hero.path_planning': True,
        'controller.hero.avoidance': True,
        'controller.infantry.pathfinding': True,
        'controller.infantry.path_planning': True,
        'controller.infantry.avoidance': True,
        'controller.sentry.pathfinding': True,
        'controller.sentry.path_planning': True,
        'controller.sentry.avoidance': True,
        'controller.engineer.pathfinding': True,
        'controller.engineer.path_planning': True,
        'controller.engineer.avoidance': True,
        'state_machine.hero': True,
        'state_machine.infantry': True,
        'state_machine.sentry': True,
        'state_machine.engineer': True,
    }

    def __init__(self, config, config_manager=None, config_path='config.json'):
        self.config = config
        self.config_manager = config_manager
        self.config_path = config_path
        if config.get('_settings_path'):
            self.settings_path = config.get('_settings_path')
        elif self.config_manager is not None:
            self.settings_path = self.config_manager.default_settings_path(config_path)
        else:
            self.settings_path = 'CommonSetting.json'
        self.running = False
        self.fps = config.get('simulator', {}).get('fps', 50)
        self.target_dt = 1.0 / max(float(self.fps), 1.0)
        self.dt = self.target_dt
        self.config['rules'] = RulesEngine.build_rule_config(self.config.get('rules', {}))
        self._auto_aim_worker_threads = max(1, int(self.config.get('ai', {}).get('auto_aim_worker_threads', 1) or 1))
        self._auto_aim_executor = None
        self._auto_aim_future = None
        self.debug_feature_toggles = self._load_debug_feature_toggles()

        # 初始化各系统
        self._create_systems()

        # 初始化状态机
        self.sentry_state_machine = SentryStateMachine()

        # 日志系统
        self.logs = []
        self.max_logs = 10
        self._auto_aim_update_interval = float(self.config.get('ai', {}).get('auto_aim_update_interval_sec', 0.08))
        self._auto_aim_switch_score_ratio = float(self.config.get('ai', {}).get('auto_aim_switch_score_ratio', 1.18))
        self._auto_aim_switch_score_bonus = float(self.config.get('ai', {}).get('auto_aim_switch_score_bonus', 90.0))
        self._last_auto_aim_update = {}
        self._frame_index = 0
        perf_window = int(max(30, self.config.get('simulator', {}).get('perf_sample_window', 20000)))
        self._perf_samples = deque(maxlen=perf_window)
        self.show_perf_overlay = bool(self.config.get('simulator', {}).get('show_perf_overlay', True))
        self.enable_perf_logging = bool(self.config.get('simulator', {}).get('enable_perf_logging', True))
        self.enable_perf_breakdown = bool(self.config.get('simulator', {}).get('enable_perf_breakdown', True))
        self.enable_perf_file_logging = bool(self.config.get('simulator', {}).get('enable_perf_file_logging', True))
        self.perf_log_path = self.config.get('simulator', {}).get('perf_log_path', os.path.join('perf_logs', 'perf_stats.csv'))
        self._perf_log_interval = float(self.config.get('simulator', {}).get('perf_log_interval_sec', 5.0))
        self._perf_last_log = time.perf_counter()
        self._perf_overlay_cache = {'ts': 0.0, 'stats': None}
        self._last_update_breakdown = None
        self._last_event_ms = 0.0
        self.current_fps = float(self.fps)
        self.current_frame_ms = 1000.0 / max(float(self.fps), 1.0)
        self._perf_log_session_id = None
        self._restart_auto_aim_executor(wait=False)

        # 游戏状态
        self.game_time = 0
        self.game_duration = self.config.get('rules', {}).get('game_duration', 420)
        self.score = {'red': 0, 'blue': 0}
        self.paused = True
        self.match_started = False
        self._game_over_announced = False
        self._configured_initial_positions = deepcopy(self.config.get('entities', {}).get('initial_positions', {}))
        simulator_config = self.config.setdefault('simulator', {})
        self.match_mode = str(simulator_config.get('match_mode', 'full') or 'full')
        self.single_unit_test_team = str(simulator_config.get('single_unit_test_team', 'red') or 'red')
        self.single_unit_test_entity_key = str(simulator_config.get('single_unit_test_entity_key', 'robot_1') or 'robot_1')
        self.player_controlled_entity_id = None
        self.player_control_enabled = False
        self.player_fire_pressed = False
        self.player_autoaim_pressed = False
        self.player_look_delta = [0.0, 0.0]
        self.player_view_aim_state = None
        self.player_step_climb_mode_active = False
        self.player_movement_input = {
            'forward': False,
            'backward': False,
            'left': False,
            'right': False,
            'jump': False,
            'small_gyro': False,
        }
        self.player_input_timing = {}
        self.recent_bus_messages = deque(maxlen=64)
        self.message_bus = MessageBus()
        self.player_camera_mode = str(simulator_config.get('player_camera_mode', 'first_person') or 'first_person')
        if self.player_camera_mode not in {'first_person', 'third_person'}:
            self.player_camera_mode = 'first_person'
        self.pre_match_setup_required = False
        self.pre_match_countdown_remaining = 0.0
        self.pre_match_config_applied = False
        self._normalize_match_mode_settings()

    def _load_debug_feature_toggles(self):
        stored = self.config.get('simulator', {}).get('debug_feature_toggles', {})
        toggles = dict(self.DEBUG_FEATURE_DEFAULTS)
        if isinstance(stored, dict):
            for key in toggles:
                if key in stored:
                    toggles[key] = bool(stored[key])
            if 'sentry' in stored:
                toggles['state_machine.sentry'] = bool(stored['sentry'])
        self.config.setdefault('simulator', {})['debug_feature_toggles'] = dict(toggles)
        return toggles

    def feature_enabled(self, feature_id):
        return bool(self.debug_feature_toggles.get(feature_id, True))

    def set_feature_enabled(self, feature_id, enabled):
        if feature_id not in self.DEBUG_FEATURE_DEFAULTS:
            return False
        value = bool(enabled)
        self.debug_feature_toggles[feature_id] = value
        self.config.setdefault('simulator', {}).setdefault('debug_feature_toggles', {})[feature_id] = value
        if feature_id == 'auto_aim' and not value:
            self._shutdown_auto_aim_worker(cancel_futures=True)
        return True

    def toggle_feature_enabled(self, feature_id):
        new_value = not self.feature_enabled(feature_id)
        if not self.set_feature_enabled(feature_id, new_value):
            return None
        return new_value

    def _restore_initial_positions_config(self):
        self.config.setdefault('entities', {})['initial_positions'] = deepcopy(self._configured_initial_positions)

    def _normalize_match_mode_settings(self):
        if self.match_mode not in self.MATCH_MODES:
            self.match_mode = 'full'
        if self.single_unit_test_team not in {'red', 'blue'}:
            self.single_unit_test_team = 'red'
        if self.single_unit_test_entity_key not in self.SINGLE_UNIT_TEST_ENTITY_KEYS:
            self.single_unit_test_entity_key = 'robot_1'
        simulator_config = self.config.setdefault('simulator', {})
        simulator_config['match_mode'] = self.match_mode
        simulator_config['single_unit_test_team'] = self.single_unit_test_team
        simulator_config['single_unit_test_entity_key'] = self.single_unit_test_entity_key

    def is_single_unit_test_mode(self):
        return self.match_mode == 'single_unit_test'

    def clear_all_forced_test_decisions(self):
        for entity in getattr(self.entity_manager, 'entities', ()): 
            entity.test_forced_decision_id = ''

    def set_match_mode(self, mode):
        normalized = str(mode or 'full')
        if normalized not in self.MATCH_MODES:
            normalized = 'full'
        if normalized == self.match_mode:
            return False
        self.match_mode = normalized
        if self.match_mode == 'full':
            self.clear_all_forced_test_decisions()
        self._normalize_match_mode_settings()
        return True

    def set_single_unit_test_focus(self, team=None, entity_key=None):
        changed = False
        if team is not None:
            normalized_team = str(team or 'red')
            if normalized_team in {'red', 'blue'} and normalized_team != self.single_unit_test_team:
                self.single_unit_test_team = normalized_team
                changed = True
        if entity_key is not None:
            normalized_key = str(entity_key or 'robot_1')
            if normalized_key in self.SINGLE_UNIT_TEST_ENTITY_KEYS and normalized_key != self.single_unit_test_entity_key:
                self.single_unit_test_entity_key = normalized_key
                changed = True
        if changed:
            self.clear_all_forced_test_decisions()
            self._normalize_match_mode_settings()
        return changed

    def get_single_unit_test_focus_id(self):
        self._normalize_match_mode_settings()
        return f'{self.single_unit_test_team}_{self.single_unit_test_entity_key}'

    def get_single_unit_test_focus_entity(self):
        return self.entity_manager.get_entity(self.get_single_unit_test_focus_id())

    def get_single_unit_test_focus_entity_id(self):
        entity = self.get_single_unit_test_focus_entity()
        return getattr(entity, 'id', None)

    def get_single_unit_test_controlled_entity_ids(self):
        focus_entity_id = self.get_single_unit_test_focus_entity_id()
        if not self.is_single_unit_test_mode() or not focus_entity_id:
            return None
        return {focus_entity_id}

    def get_player_controlled_entity(self):
        if not self.player_control_enabled or not self.player_controlled_entity_id:
            return None
        entity = self.entity_manager.get_entity(self.player_controlled_entity_id)
        if entity is None or not entity.is_alive():
            self.clear_player_controlled_entity()
            return None
        return entity

    def get_player_controlled_entity_ids(self):
        entity = self.get_player_controlled_entity()
        if entity is None:
            return set()
        return {entity.id}

    def set_player_controlled_entity(self, entity_id=None):
        entity = self.entity_manager.get_entity(entity_id) if entity_id else None
        for candidate in getattr(self.entity_manager, 'entities', ()):
            candidate.player_controlled = False
            candidate.step_climb_mode_active = False
            candidate.step_climb_lock_heading_deg = None
        self.player_step_climb_mode_active = False
        for key in self.player_movement_input:
            self.player_movement_input[key] = False
        if entity is None or not entity.is_alive() or getattr(entity, 'type', None) not in {'robot', 'sentry'}:
            self.player_control_enabled = False
            self.player_controlled_entity_id = None
            return False
        entity.player_controlled = True
        self.player_controlled_entity_id = entity.id
        self.player_control_enabled = True
        return True

    def clear_player_controlled_entity(self):
        for candidate in getattr(self.entity_manager, 'entities', ()):
            candidate.player_controlled = False
            candidate.step_climb_mode_active = False
            candidate.step_climb_lock_heading_deg = None
        self.player_control_enabled = False
        self.player_controlled_entity_id = None
        self.player_fire_pressed = False
        self.player_autoaim_pressed = False
        self.player_look_delta = [0.0, 0.0]
        self.player_view_aim_state = None
        self.player_step_climb_mode_active = False
        for key in self.player_movement_input:
            self.player_movement_input[key] = False
        self.player_input_timing = {}

    def _record_player_input_event(self, input_id, is_down=None, payload=None):
        input_key = str(input_id or '').strip()
        if not input_key:
            return None
        now_sec = float(self.game_time)
        previous = self.player_input_timing.get(input_key, {}) if isinstance(self.player_input_timing.get(input_key), dict) else {}
        was_down = bool(previous.get('is_down', False))
        next_state = dict(previous)
        if is_down is not None:
            next_state['is_down'] = bool(is_down)
            next_state['just_pressed'] = bool(is_down and not was_down)
            next_state['just_released'] = bool((not is_down) and was_down)
            if next_state['just_pressed']:
                next_state['last_pressed_at'] = now_sec
                next_state['press_count'] = int(previous.get('press_count', 0)) + 1
            else:
                next_state.setdefault('press_count', int(previous.get('press_count', 0)))
            if next_state['just_released']:
                next_state['last_released_at'] = now_sec
        if payload is not None:
            next_state['payload'] = deepcopy(payload)
        self.player_input_timing[input_key] = next_state
        self.message_bus.publish(
            'player_input',
            {
                'input_id': input_key,
                'is_down': next_state.get('is_down'),
                'time_sec': now_sec,
                'payload': deepcopy(payload) if payload is not None else None,
            },
        )
        return deepcopy(next_state)

    def _consume_player_input_timing_snapshot(self):
        snapshot = deepcopy(self.player_input_timing)
        for state in self.player_input_timing.values():
            if isinstance(state, dict):
                state['just_pressed'] = False
                state['just_released'] = False
        return snapshot

    def set_player_action_state(self, fire_pressed=None, autoaim_pressed=None):
        if fire_pressed is not None:
            self.player_fire_pressed = bool(fire_pressed)
            self._record_player_input_event('fire', is_down=self.player_fire_pressed)
        if autoaim_pressed is not None:
            self.player_autoaim_pressed = bool(autoaim_pressed)
            self._record_player_input_event('autoaim', is_down=self.player_autoaim_pressed)

    def set_player_movement_state(self, forward=None, backward=None, left=None, right=None, jump=None, small_gyro=None):
        state_map = {
            'forward': forward,
            'backward': backward,
            'left': left,
            'right': right,
            'jump': jump,
            'small_gyro': small_gyro,
        }
        event_names = {
            'forward': 'move_forward',
            'backward': 'move_backward',
            'left': 'move_left',
            'right': 'move_right',
            'jump': 'jump',
            'small_gyro': 'small_gyro',
        }
        for key, value in state_map.items():
            if value is None:
                continue
            normalized = bool(value)
            if self.player_movement_input.get(key) == normalized:
                continue
            self.player_movement_input[key] = normalized
            self._record_player_input_event(event_names[key], is_down=normalized)

    def set_player_view_aim_state(self, aim_state=None):
        self.player_view_aim_state = deepcopy(aim_state) if isinstance(aim_state, dict) else None

    def set_player_step_climb_mode(self, active):
        self.player_step_climb_mode_active = bool(active)
        self._record_player_input_event('step_climb_mode', is_down=self.player_step_climb_mode_active)
        entity = self.get_player_controlled_entity()
        if entity is not None:
            entity.step_climb_mode_active = self.player_step_climb_mode_active
            if self.player_step_climb_mode_active:
                entity.step_climb_lock_heading_deg = float(getattr(entity, 'angle', 0.0))
            else:
                entity.step_climb_lock_heading_deg = None
        return self.player_step_climb_mode_active

    def toggle_player_step_climb_mode(self):
        return self.set_player_step_climb_mode(not bool(getattr(self, 'player_step_climb_mode_active', False)))

    def set_player_camera_mode(self, mode):
        normalized = str(mode or 'first_person')
        if normalized not in {'first_person', 'third_person'}:
            normalized = 'first_person'
        self.player_camera_mode = normalized
        self._record_player_input_event('camera_mode', payload={'mode': self.player_camera_mode})

    def get_player_sensitivity_settings(self):
        settings = get_player_mouse_input_settings(self.config)
        return {
            'yaw': float(settings['yaw_sensitivity_deg']),
            'pitch': float(settings['pitch_sensitivity_deg']),
        }

    def set_player_sensitivity_settings(self, yaw_sensitivity_deg=None, pitch_sensitivity_deg=None):
        settings = set_player_mouse_input_settings(
            self.config,
            yaw_sensitivity_deg=yaw_sensitivity_deg,
            pitch_sensitivity_deg=pitch_sensitivity_deg,
        )
        controller = getattr(self, 'controller', None)
        if controller is not None and hasattr(controller, 'set_player_look_sensitivity'):
            controller.set_player_look_sensitivity(
                settings.get('yaw_sensitivity_deg'),
                settings.get('pitch_sensitivity_deg'),
            )

    def begin_pre_match_setup(self):
        if not self.match_started:
            return False
        self.pre_match_setup_required = True
        self.pre_match_countdown_remaining = 0.0
        self.pre_match_config_applied = False
        self.paused = True
        return True

    def begin_pre_match_countdown(self, seconds=5.0):
        if not self.match_started:
            return False
        if self.get_player_controlled_entity() is None:
            return False
        self.pre_match_setup_required = False
        self.pre_match_config_applied = True
        self.pre_match_countdown_remaining = max(0.0, float(seconds))
        self.paused = True
        self.add_log(f'赛前参数已锁定，{int(round(self.pre_match_countdown_remaining))} 秒后开始比赛。', 'system')
        return True

    def accumulate_player_look_delta(self, delta_x, delta_y):
        yaw_delta_deg, pitch_delta_deg = scale_player_mouse_motion(self.config, delta_x, delta_y)
        self.player_look_delta[0] += float(yaw_delta_deg)
        self.player_look_delta[1] += float(pitch_delta_deg)
        self.message_bus.publish(
            'player_look',
            {
                'yaw_delta_deg': float(yaw_delta_deg),
                'pitch_delta_deg': float(pitch_delta_deg),
                'time_sec': float(self.game_time),
            },
        )

    def consume_player_input_state(self):
        state = {
            'input_time_sec': float(self.game_time),
            'frame_dt': float(self.dt),
            'look_dx': float(self.player_look_delta[0]),
            'look_dy': float(self.player_look_delta[1]),
            'fire_pressed': bool(self.player_fire_pressed),
            'autoaim_pressed': bool(self.player_autoaim_pressed),
            'step_climb_mode_active': bool(getattr(self, 'player_step_climb_mode_active', False)),
            'movement': deepcopy(self.player_movement_input),
            'input_timing': self._consume_player_input_timing_snapshot(),
            'view_aim_state': deepcopy(self.player_view_aim_state) if isinstance(self.player_view_aim_state, dict) else None,
            'camera_mode': str(getattr(self, 'player_camera_mode', 'first_person')),
        }
        self.player_look_delta[0] = 0.0
        self.player_look_delta[1] = 0.0
        return state

    def is_player_in_supply_zone(self):
        entity = self.get_player_controlled_entity()
        if entity is None:
            return False
        return bool(self.rules_engine.is_in_team_supply_zone(entity, map_manager=self.map_manager))

    def purchase_player_ammo(self, amount):
        entity = self.get_player_controlled_entity()
        if entity is None:
            return {'ok': False, 'code': 'ENTITY_MISSING'}
        return self.rules_engine.purchase_manual_role_ammo(entity, amount, map_manager=self.map_manager)

    def _manual_control_ai_scope(self):
        simulator_config = self.config.setdefault('simulator', {})
        scope = str(simulator_config.get('manual_control_ai_scope') or '').strip()
        if not scope:
            scope = 'combat_units' if bool(simulator_config.get('standalone_3d_program', False)) else 'controlled_entities'
        if scope not in {'controlled_entities', 'combat_units'}:
            scope = 'controlled_entities'
        return scope

    def _requires_pre_match_setup(self):
        simulator_config = self.config.setdefault('simulator', {})
        required = simulator_config.get('require_pre_match_setup')
        if required is None:
            return bool(simulator_config.get('standalone_3d_program', False))
        return bool(required)

    def _player_ai_disabled_entity_ids(self):
        if not bool(getattr(self, 'player_control_enabled', False)):
            return set()
        if self._manual_control_ai_scope() != 'combat_units':
            return self.get_player_controlled_entity_ids()
        return {
            entity.id
            for entity in getattr(self.entity_manager, 'entities', ())
            if getattr(entity, 'type', None) in {'robot', 'sentry'}
        }

    def _freeze_player_mode_inactive_units(self, player_ids):
        if not bool(getattr(self, 'player_control_enabled', False)):
            return
        if self._manual_control_ai_scope() != 'combat_units':
            return
        for entity in getattr(self.entity_manager, 'entities', ()):
            if entity.id in player_ids:
                continue
            if getattr(entity, 'type', None) not in {'robot', 'sentry'}:
                continue
            entity.set_velocity(0.0, 0.0, 0.0)
            entity.angular_velocity = 0.0
            entity.target = None
            entity.fire_control_state = 'idle'
            entity.auto_aim_locked = False

    def _clear_single_unit_test_inactive_entity_state(self):
        if not self.is_single_unit_test_mode():
            return
        focus_entity_id = self.get_single_unit_test_focus_entity_id()
        for entity in getattr(self.entity_manager, 'entities', ()):
            if getattr(entity, 'type', None) not in {'robot', 'sentry'}:
                continue
            if entity.id == focus_entity_id:
                continue
            entity.ai_decision = '单兵种测试待机'
            entity.ai_behavior_node = ''
            entity.ai_decision_selected = ''
            entity.ai_decision_weights = ()
            entity.ai_decision_top3 = ()
            entity.ai_navigation_target = None
            entity.ai_movement_target = None
            entity.ai_navigation_waypoint = None
            entity.ai_path_preview = ()
            entity.ai_navigation_subgoals = ()
            entity.ai_navigation_path_valid = False
            entity.ai_navigation_radius = 0.0
            entity.target = None
            entity.auto_aim_locked = False
            entity.auto_aim_hit_probability = 0.0
            entity.fire_control_state = 'idle'
            entity.test_forced_decision_id = ''

    def _freeze_non_focus_single_unit_entities(self):
        if not self.is_single_unit_test_mode():
            return
        focus_entity_id = self.get_single_unit_test_focus_entity_id()
        for entity in getattr(self.entity_manager, 'entities', ()): 
            if getattr(entity, 'type', None) not in {'robot', 'sentry'}:
                continue
            if entity.id == focus_entity_id:
                continue
            entity.set_velocity(0.0, 0.0, 0.0)
            entity.angular_velocity = 0.0

    def get_single_unit_test_next_decision_specs(self):
        focus_entity = self.get_single_unit_test_focus_entity()
        role_key = self._role_key_for_entity(focus_entity)
        if role_key is None:
            return ()
        controller = getattr(self, 'controller', None)
        ai_controllers = getattr(controller, '_ai_controllers', ()) if controller is not None else ()
        if not ai_controllers:
            return ()
        specs = list(ai_controllers[0].role_decision_specs.get(role_key, ()))
        if not specs:
            return ()
        anchor_id = str(getattr(focus_entity, 'test_forced_decision_id', '') or getattr(focus_entity, 'ai_decision_selected', '') or '')
        anchor_index = -1
        for index, spec in enumerate(specs):
            if spec.get('id') == anchor_id:
                anchor_index = index
                break
        if anchor_index < 0:
            candidate_specs = specs[:3]
        else:
            candidate_specs = specs[anchor_index + 1:anchor_index + 4]
            if len(candidate_specs) < 3:
                candidate_specs.extend(specs[:max(0, 3 - len(candidate_specs))])
        return tuple({'id': spec.get('id', ''), 'label': spec.get('label', spec.get('id', ''))} for spec in candidate_specs if spec.get('id'))

    def _role_key_for_entity(self, entity):
        if entity is None:
            return None
        if getattr(entity, 'type', None) == 'sentry':
            return 'sentry'
        return {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
        }.get(getattr(entity, 'robot_type', ''), 'infantry')

    def get_single_unit_test_decision_specs(self):
        entity = self.get_single_unit_test_focus_entity()
        role_key = self._role_key_for_entity(entity)
        if role_key is None:
            return ()
        controller = getattr(self, 'controller', None)
        ai_controllers = getattr(controller, '_ai_controllers', ()) if controller is not None else ()
        if not ai_controllers:
            return ()
        specs = ai_controllers[0].role_decision_specs.get(role_key, ())
        return tuple({'id': spec.get('id', ''), 'label': spec.get('label', spec.get('id', ''))} for spec in specs if spec.get('id'))

    def set_single_unit_test_decision(self, decision_id=''):
        focus_entity = self.get_single_unit_test_focus_entity()
        if focus_entity is None:
            return False
        normalized = str(decision_id or '').strip()
        valid_ids = {spec['id'] for spec in self.get_single_unit_test_decision_specs()}
        if normalized and normalized not in valid_ids:
            return False
        self.clear_all_forced_test_decisions()
        focus_entity.test_forced_decision_id = normalized
        return True

    def set_structure_health(self, team, structure_type, health_value):
        entity = self.entity_manager.get_entity(f'{team}_{structure_type}')
        if entity is None:
            return False
        clamped = max(0.0, min(float(getattr(entity, 'max_health', 0.0)), float(health_value)))
        entity.health = clamped
        entity.state = 'idle' if clamped > 0.0 else 'destroyed'
        return True

    def adjust_structure_health(self, team, structure_type, delta):
        entity = self.entity_manager.get_entity(f'{team}_{structure_type}')
        if entity is None:
            return False
        return self.set_structure_health(team, structure_type, float(entity.health) + float(delta))

    def _create_systems(self):
        old_controller = getattr(self, 'controller', None)
        if old_controller is not None and hasattr(old_controller, 'shutdown'):
            old_controller.shutdown()
        old_map_manager = getattr(self, 'map_manager', None)
        if old_map_manager is not None and hasattr(old_map_manager, 'shutdown'):
            old_map_manager.shutdown()
        self._restart_auto_aim_executor(wait=True)
        self.map_manager = MapManager(self.config)
        self.entity_manager = EntityManager(self.config)
        self.physics_engine = PhysicsEngine(self.config)
        self.rules_engine = RulesEngine(self.config)
        self.rules_engine.game_engine = self
        self.controller = Controller(self.config)

    def _restart_auto_aim_executor(self, wait):
        self._shutdown_auto_aim_worker(cancel_futures=True)
        executor = getattr(self, '_auto_aim_executor', None)
        if executor is not None:
            executor.shutdown(wait=wait, cancel_futures=True)
        self._auto_aim_executor = ThreadPoolExecutor(max_workers=self._auto_aim_worker_threads)

    def _shutdown_auto_aim_worker(self, cancel_futures=False):
        future = getattr(self, '_auto_aim_future', None)
        if future is not None:
            if cancel_futures and not future.done():
                future.cancel()
            self._auto_aim_future = None

    def shutdown(self):
        self._shutdown_auto_aim_worker(cancel_futures=True)
        executor = getattr(self, '_auto_aim_executor', None)
        if executor is not None:
            executor.shutdown(wait=True, cancel_futures=True)
            self._auto_aim_executor = None
        controller = getattr(self, 'controller', None)
        if controller is not None and hasattr(controller, 'shutdown'):
            controller.shutdown()
        physics_engine = getattr(self, 'physics_engine', None)
        if physics_engine is not None and hasattr(physics_engine, 'shutdown'):
            physics_engine.shutdown()
        map_manager = getattr(self, 'map_manager', None)
        if map_manager is not None and hasattr(map_manager, 'shutdown'):
            map_manager.shutdown()
        message_bus = getattr(self, 'message_bus', None)
        if message_bus is not None:
            message_bus.shutdown()

    def _drain_message_bus(self):
        message_bus = getattr(self, 'message_bus', None)
        if message_bus is None:
            return
        for message in message_bus.poll(limit=128):
            self.recent_bus_messages.append(message)

    def _reset_runtime_state(self):
        self.game_time = 0
        self.game_duration = self.config.get('rules', {}).get('game_duration', 420)
        self.score = {'red': 0, 'blue': 0}
        self.paused = True
        self.match_started = False
        self.logs = []
        self._game_over_announced = False
        self._frame_index = 0
        perf_window = int(max(30, self.config.get('simulator', {}).get('perf_sample_window', 20000)))
        self._perf_samples = deque(maxlen=perf_window)
        self._perf_overlay_cache = {'ts': 0.0, 'stats': None}
        self._last_update_breakdown = None
        self._last_event_ms = 0.0
        self._perf_log_session_id = datetime.now().strftime('%Y%m%d_%H%M%S')
        self.pre_match_setup_required = False
        self.pre_match_countdown_remaining = 0.0
        self.pre_match_config_applied = False
        self.clear_player_controlled_entity()
    
    def initialize(self):
        """初始化游戏引擎"""
        # 加载地图
        self.map_manager.load_map()
        
        # 创建实体
        self.entity_manager.create_entities()
        self._stabilize_entity_positions()
        
        # 添加初始日志
        self.add_log('对局未开始，点击开始/重开进入 7 分钟对局。', 'system')

    def start_new_match(self):
        """按当前配置重开一局。"""
        self.config['rules'] = RulesEngine.build_rule_config(self.config.get('rules', {}))
        self._restore_initial_positions_config()
        self._create_systems()
        self._reset_runtime_state()
        self._perf_log_session_id = datetime.now().strftime('%Y%m%d_%H%M%S')
        self.initialize()
        self.game_duration = self.config.get('rules', {}).get('game_duration', 420)
        self.match_started = True
        self.paused = False
        if self._requires_pre_match_setup():
            self.begin_pre_match_setup()
            self.add_log('需要配置机器人参数，请按 P 打开赛前面板。', 'system')
        else:
            self.add_log('对局开始', 'system')

    def reload_map_preset(self, preset_name):
        if self.config_manager is None:
            return False
        preset = self.config_manager.load_map_preset(preset_name, self.config_path)
        if not isinstance(preset, dict):
            return False
        next_map = self.config_manager._deep_merge(
            self.config.get('map', {}),
            {key: value for key, value in preset.items() if key != '_preset_path'},
        )
        next_map['preset'] = str(preset_name)
        if preset.get('_preset_path'):
            next_map['_preset_path'] = preset.get('_preset_path')
        self.config['map'] = next_map
        self.config.setdefault('simulator', {})['sim3d_map_preset'] = str(preset_name)
        self._create_systems()
        self._reset_runtime_state()
        self.initialize()
        return True

    def end_match(self):
        """结束当前对局，但不关闭程序。"""
        self.rules_engine.game_over = True
        self.rules_engine.stage = 'ended'
        self.paused = True
        self.match_started = False
        self._flush_perf_samples_to_file('match_end')
        if not self._game_over_announced:
            self.add_log('对局已结束，可点击开始/重开重新开始。', 'system')
            self._game_over_announced = True

    def save_local_settings(self):
        """保存设施、站位和规则到本地 setting 文件。"""
        self.config['map']['facilities'] = self.map_manager.export_facilities_config()
        self.config['map']['terrain_grid'] = self.map_manager.export_terrain_grid_config()
        self.config['map']['function_grid'] = self.map_manager.export_function_grid_config()
        self.config['map']['runtime_grid'] = self.map_manager.export_runtime_grid_config()
        self.config['entities']['initial_positions'] = self.entity_manager.export_initial_positions()
        self.config['entities']['robot_subtypes'] = self.entity_manager.export_robot_subtypes()
        self._configured_initial_positions = deepcopy(self.config['entities']['initial_positions'])
        self.config['rules'] = RulesEngine.build_rule_config(self.config.get('rules', {}))
        if self.config_manager is not None:
            self.config_manager.config = self.config
            self.config_manager.save_settings(self.settings_path)
        self.add_log(f'本地设置已保存到 {self.settings_path}', 'system')
    
    def add_log(self, message, team="system"):
        """添加日志"""
        self.logs.append({'message': message, 'team': team, 'time': time.time()})
        # 限制日志数量
        if len(self.logs) > self.max_logs:
            self.logs.pop(0)

    def _record_perf_sample(self, update_ms, render_ms, frame_ms, breakdown=None, game_time=0.0):
        if not self.match_started:
            return
        sample = {
            'update_ms': float(update_ms),
            'render_ms': float(render_ms),
            'frame_ms': float(frame_ms),
            'game_time': float(game_time),
            'frame_index': int(self._frame_index),
            'event_ms': float(self._last_event_ms),
        }
        if breakdown:
            sample['breakdown'] = {k: float(v) for k, v in breakdown.items()}
        self._perf_samples.append(sample)

    def _compute_percentile(self, values, percentile):
        if not values:
            return 0.0
        ordered = sorted(values)
        rank = max(0, min(len(ordered) - 1, int(len(ordered) * percentile)))
        return ordered[rank]

    def _compute_perf_stats(self):
        if not self._perf_samples:
            return None
        update_values = [s['update_ms'] for s in self._perf_samples]
        render_values = [s['render_ms'] for s in self._perf_samples]
        frame_values = [s['frame_ms'] for s in self._perf_samples]
        event_values = [s.get('event_ms', 0.0) for s in self._perf_samples]
        breakdown_samples = [s['breakdown'] for s in self._perf_samples if 'breakdown' in s]
        breakdown_stats = None
        if breakdown_samples:
            keys = breakdown_samples[0].keys()
            breakdown_stats = {k: statistics.mean([sample.get(k, 0.0) for sample in breakdown_samples]) for k in keys}
        return {
            'update_avg_ms': statistics.mean(update_values),
            'render_avg_ms': statistics.mean(render_values),
            'frame_avg_ms': statistics.mean(frame_values),
            'event_avg_ms': statistics.mean(event_values),
            'update_p95_ms': self._compute_percentile(update_values, 0.95),
            'render_p95_ms': self._compute_percentile(render_values, 0.95),
            'frame_p95_ms': self._compute_percentile(frame_values, 0.95),
            'event_p95_ms': self._compute_percentile(event_values, 0.95),
            'breakdown': breakdown_stats,
        }

    def _maybe_log_perf(self):
        if not self.enable_perf_logging:
            return
        if self._perf_log_interval <= 0:
            return
        now = time.perf_counter()
        if (now - self._perf_last_log) < self._perf_log_interval:
            return
        stats = self._compute_perf_stats()
        if stats is None:
            return
        self.add_log(
            f"性能: 帧 {stats['frame_avg_ms']:.1f}ms(p95 {stats['frame_p95_ms']:.1f}) | 事件 {stats['event_avg_ms']:.1f}ms | 更新 {stats['update_avg_ms']:.1f}ms | 渲染 {stats['render_avg_ms']:.1f}ms",
            'system',
        )
        self._log_perf_to_file(stats)
        self._perf_last_log = now

    def _log_perf_to_file(self, stats):
        if not self.enable_perf_file_logging:
            return
        if not stats:
            return
        try:
            os.makedirs(os.path.dirname(self.perf_log_path), exist_ok=True)
            header = (
                'timestamp,frame_index,fps,'
                'event_avg_ms,event_p95_ms,'
                'frame_avg_ms,frame_p95_ms,'
                'update_avg_ms,update_p95_ms,'
                'render_avg_ms,render_p95_ms,'
                'entity_ms,controller_ms,physics_ms,auto_aim_ms,rules_ms,sentry_ms\n'
            )
            breakdown = stats.get('breakdown') or {}
            line = (
                f"{time.time():.3f},{self._frame_index},{self.fps},"
                f"{stats['event_avg_ms']:.3f},{stats['event_p95_ms']:.3f},"
                f"{stats['frame_avg_ms']:.3f},{stats['frame_p95_ms']:.3f},"
                f"{stats['update_avg_ms']:.3f},{stats['update_p95_ms']:.3f},"
                f"{stats['render_avg_ms']:.3f},{stats['render_p95_ms']:.3f},"
                f"{breakdown.get('entity_ms', 0.0):.3f},{breakdown.get('controller_ms', 0.0):.3f},"
                f"{breakdown.get('physics_ms', 0.0):.3f},{breakdown.get('auto_aim_ms', 0.0):.3f},"
                f"{breakdown.get('rules_ms', 0.0):.3f},{breakdown.get('sentry_ms', 0.0):.3f}\n"
            )
            file_exists = os.path.exists(self.perf_log_path)
            with open(self.perf_log_path, 'a', encoding='utf-8') as f:
                if not file_exists:
                    f.write(header)
                f.write(line)
        except Exception:
            # 失败时静默，避免卡顿
            return

    def _flush_perf_samples_to_file(self, reason='match_end'):
        if not self.enable_perf_file_logging:
            return
        if not self._perf_samples:
            return
        try:
            folder = os.path.join('saves', 'perf_logs')
            os.makedirs(folder, exist_ok=True)
            session_id = self._perf_log_session_id or datetime.now().strftime('%Y%m%d_%H%M%S')
            filename = f'perf_{session_id}_{reason}.csv'
            path = os.path.join(folder, filename)
            header = (
                'frame_index,game_time,event_ms,update_ms,render_ms,frame_ms,'
                'entity_ms,controller_ms,physics_ms,auto_aim_ms,rules_ms,sentry_ms\n'
            )
            with open(path, 'w', encoding='utf-8') as f:
                f.write(header)
                for sample in list(self._perf_samples):
                    breakdown = sample.get('breakdown') or {}
                    f.write(
                        f"{int(sample.get('frame_index', 0))},"
                        f"{sample.get('game_time', 0.0):.3f},"
                        f"{sample.get('event_ms', 0.0):.3f},"
                        f"{sample.get('update_ms', 0.0):.3f},"
                        f"{sample.get('render_ms', 0.0):.3f},"
                        f"{sample.get('frame_ms', 0.0):.3f},"
                        f"{breakdown.get('entity_ms', 0.0):.3f},"
                        f"{breakdown.get('controller_ms', 0.0):.3f},"
                        f"{breakdown.get('physics_ms', 0.0):.3f},"
                        f"{breakdown.get('auto_aim_ms', 0.0):.3f},"
                        f"{breakdown.get('rules_ms', 0.0):.3f},"
                        f"{breakdown.get('sentry_ms', 0.0):.3f}\n"
                    )
            self.add_log(f'性能日志已保存: {path}', 'system')
        except Exception:
            return

    def _should_measure_perf(self):
        return True

    def get_perf_overlay_stats(self):
        if not self._perf_samples:
            return None
        now = time.perf_counter()
        if (now - self._perf_overlay_cache['ts']) < 0.25 and self._perf_overlay_cache['stats'] is not None:
            return self._perf_overlay_cache['stats']
        stats = self._compute_perf_stats()
        self._perf_overlay_cache = {'ts': now, 'stats': stats}
        return stats
    
    def update(self):
        """更新游戏状态"""
        self._drain_message_bus()
        if self.match_started and self.pre_match_countdown_remaining > 0.0:
            self.pre_match_countdown_remaining = max(0.0, self.pre_match_countdown_remaining - self.dt)
            if self.pre_match_countdown_remaining <= 1e-6:
                self.pre_match_countdown_remaining = 0.0
                self.paused = False
                self.add_log('比赛开始', 'system')
        if self.paused or not self.match_started:
            self._last_update_breakdown = None
            return

        active_entity_ids = {entity.id for entity in self.entity_manager.entities if entity.is_alive()}
        stale_autoaim_ids = [entity_id for entity_id in self._last_auto_aim_update.keys() if entity_id not in active_entity_ids]
        for entity_id in stale_autoaim_ids:
            self._last_auto_aim_update.pop(entity_id, None)

        measure_perf = self._should_measure_perf()

        # 更新时间
        self.game_time += self.dt
        self._frame_index += 1
        self.rules_engine.start_frame(self._frame_index)

        self._freeze_non_focus_single_unit_entities()
        self._clear_single_unit_test_inactive_entity_state()
        
        # 更新实体状态
        entity_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('entity_update'):
            self.entity_manager.update(self.dt)
        entity_end = time.perf_counter() if measure_perf else 0.0
        
        # 控制处理
        controller_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('controller'):
            player_ids = self.get_player_controlled_entity_ids()
            ai_disabled_ids = self._player_ai_disabled_entity_ids()
            self.controller.update(
                self.entity_manager.entities,
                self.map_manager,
                self.rules_engine,
                self.game_time,
                self.game_duration,
                controlled_entity_ids=self.get_single_unit_test_controlled_entity_ids(),
                ai_excluded_entity_ids=ai_disabled_ids,
                manual_entity_ids=player_ids,
                manual_state=self.consume_player_input_state(),
            )
            self._freeze_player_mode_inactive_units(player_ids)
            self._freeze_non_focus_single_unit_entities()
            self._clear_single_unit_test_inactive_entity_state()
        controller_end = time.perf_counter() if measure_perf else 0.0

        # 物理模拟
        physics_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('physics'):
            self.physics_engine.update(self.entity_manager.entities, self.map_manager, self.rules_engine, dt=self.dt)
        physics_end = time.perf_counter() if measure_perf else 0.0

        # 通用自瞄：除工程外的战斗单位自动锁定最近敌人
        auto_aim_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('auto_aim'):
            self._update_general_auto_aim()
        else:
            self._shutdown_auto_aim_worker(cancel_futures=True)
        auto_aim_end = time.perf_counter() if measure_perf else 0.0

        # 规则检查
        rules_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('rules'):
            self.rules_engine.update(
                self.entity_manager.entities,
                map_manager=self.map_manager,
                dt=self.dt,
                game_time=self.game_time,
                game_duration=self.game_duration,
            )
        rules_end = time.perf_counter() if measure_perf else 0.0
        
        # 更新哨兵状态机
        sentry_start = time.perf_counter() if measure_perf else 0.0
        if self.feature_enabled('state_machine.sentry'):
            for entity in self.entity_manager.entities:
                if entity.type == 'sentry':
                    self.sentry_state_machine.update(entity)
        sentry_end = time.perf_counter() if measure_perf else 0.0

        self._last_update_breakdown = {
            'entity_ms': (entity_end - entity_start) * 1000.0,
            'controller_ms': (controller_end - controller_start) * 1000.0,
            'physics_ms': (physics_end - physics_start) * 1000.0,
            'auto_aim_ms': (auto_aim_end - auto_aim_start) * 1000.0,
            'rules_ms': (rules_end - rules_start) * 1000.0,
            'sentry_ms': (sentry_end - sentry_start) * 1000.0,
        }

    def entity_has_barrel(self, entity):
        if entity.type == 'sentry':
            return True
        if entity.type != 'robot':
            return False
        return entity.robot_type != '工程'

    def entity_supports_drive_modes(self, entity):
        if entity.type == 'sentry':
            return True
        if entity.type != 'robot':
            return False
        return entity.robot_type != '工程'

    def _poll_general_auto_aim_future(self):
        future = self._auto_aim_future
        if future is None or not future.done():
            return
        try:
            future.result()
        except Exception:
            pass
        self._auto_aim_future = None

    def _dispatch_general_auto_aim_update(self):
        if self._auto_aim_executor is None or self._auto_aim_future is not None:
            return
        self._auto_aim_future = self._auto_aim_executor.submit(self._update_general_auto_aim_sync)

    def _update_general_auto_aim(self):
        self._poll_general_auto_aim_future()
        self._dispatch_general_auto_aim_update()

    def _update_general_auto_aim_sync(self):
        max_distance = getattr(self.rules_engine, 'auto_aim_max_distance', 0.0)
        if max_distance <= 0:
            return

        track_speed = float(self.rules_engine.rules.get('shooting', {}).get('auto_aim_track_speed_deg_per_sec', 180.0))
        controlled_ids = self.get_single_unit_test_controlled_entity_ids()
        player_ids = self._player_ai_disabled_entity_ids()

        for entity in self.entity_manager.entities:
            if not entity.is_alive():
                continue
            if entity.type not in {'robot', 'sentry'}:
                continue
            if entity.id in player_ids:
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                continue
            if controlled_ids is not None and entity.id not in controlled_ids:
                entity.target = None
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                entity.fire_control_state = 'idle'
                self._clear_auto_aim_lock(entity)
                continue
            if getattr(entity, 'respawn_weak_active', False):
                entity.target = None
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                entity.fire_control_state = 'idle'
                self._clear_auto_aim_lock(entity)
                continue
            if entity.type == 'sentry' and getattr(entity, 'front_gun_locked', False):
                entity.target = None
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                entity.fire_control_state = 'idle'
                self._clear_auto_aim_lock(entity)
                continue
            if not self.entity_has_barrel(entity):
                entity.target = None
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                entity.fire_control_state = 'idle'
                self._clear_auto_aim_lock(entity)
                entity.ai_decision = '工程仅保留机械臂，不参与自动射击'
                continue
            entity.auto_aim_track_speed_deg_per_sec = track_speed
            entity.hero_deployment_target_id = None
            entity.hero_deployment_hit_probability = 0.0
            last_update = float(self._last_auto_aim_update.get(entity.id, -1e9))
            cached_target = self._resolve_cached_target_entity(entity)
            should_refresh_target = (self.game_time - last_update) >= self._auto_aim_update_interval
            base_decision = getattr(entity, 'ai_decision', '')

            if self.rules_engine._can_use_hero_deployment_fire(entity):
                deployment_target = self.rules_engine._resolve_hero_deployment_target(entity, self.entity_manager.entities)
                if deployment_target is not None:
                    distance = self._distance(entity, deployment_target)
                    entity.target = {
                        'id': deployment_target.id,
                        'type': deployment_target.type,
                        'x': deployment_target.position['x'],
                        'y': deployment_target.position['y'],
                        'distance': distance,
                    }
                    entity.hero_deployment_target_id = deployment_target.id
                    desired_angle = self.rules_engine._desired_turret_angle(entity, deployment_target)
                    current_angle = getattr(entity, 'turret_angle', entity.angle)
                    angle_diff = self.rules_engine._normalize_angle_diff(desired_angle - current_angle)
                    max_step = track_speed * self.dt
                    if abs(angle_diff) <= max_step:
                        entity.turret_angle = desired_angle
                    else:
                        entity.turret_angle = (current_angle + max_step * (1 if angle_diff > 0 else -1)) % 360
                    _, desired_pitch = self.rules_engine.get_aim_angles_to_point(
                        entity,
                        deployment_target.position['x'],
                        deployment_target.position['y'],
                        self.rules_engine._target_armor_height_m(deployment_target),
                    )
                    entity.gimbal_pitch_deg = clamp_entity_pitch(entity, desired_pitch, config=self.config)
                    hit_probability = self.rules_engine.calculate_hit_probability(entity, deployment_target, distance)
                    entity.hero_deployment_hit_probability = hit_probability
                    entity.auto_aim_locked = False
                    effective_fire_rate = self.rules_engine.get_effective_fire_rate_hz(entity)
                    entity.fire_control_state = 'firing' if hit_probability > 0.0 and getattr(entity, 'ammo', 0) > 0 and effective_fire_rate > 0 else 'idle'
                    deploy_text = f'部署吊射 {deployment_target.id}，命中率 {hit_probability * 100:.0f}%'
                    entity.ai_decision = f'{base_decision} | {deploy_text}' if base_decision else deploy_text
                else:
                    entity.target = None
                    entity.auto_aim_locked = False
                    entity.auto_aim_hit_probability = 0.0
                    entity.fire_control_state = 'idle'
                    wait_text = '部署模式待机，当前无可视敌方前哨/基地'
                    entity.ai_decision = f'{base_decision} | {wait_text}' if base_decision else wait_text
                continue

            if should_refresh_target:
                target = self._select_auto_aim_target(entity, max_distance)
                self._last_auto_aim_update[entity.id] = self.game_time
            else:
                target = cached_target or self._resolve_locked_auto_aim_target_entity(entity, max_distance)
            if target is None:
                entity.target = None
                entity.auto_aim_locked = False
                entity.auto_aim_hit_probability = 0.0
                entity.fire_control_state = 'idle'
                self._clear_auto_aim_lock(entity)
                entity.ai_decision = f'{base_decision} | 未发现满足地形可视条件的目标' if base_decision else '未发现满足地形可视条件的目标'
                continue

            self._refresh_auto_aim_lock(entity, target)

            distance = self._distance(entity, target)
            entity.target = {
                'id': target.id,
                'type': target.type,
                'x': target.position['x'],
                'y': target.position['y'],
                'distance': distance,
            }
            desired_angle = self.rules_engine._desired_turret_angle(entity, target)
            current_angle = getattr(entity, 'turret_angle', entity.angle)
            angle_diff = self.rules_engine._normalize_angle_diff(desired_angle - current_angle)
            max_step = track_speed * self.dt
            if abs(angle_diff) <= max_step:
                entity.turret_angle = desired_angle
            else:
                entity.turret_angle = (current_angle + max_step * (1 if angle_diff > 0 else -1)) % 360
            _, desired_pitch = self.rules_engine.get_aim_angles_to_point(
                entity,
                target.position['x'],
                target.position['y'],
                self.rules_engine._target_armor_height_m(target),
            )
            entity.gimbal_pitch_deg = clamp_entity_pitch(entity, desired_pitch, config=self.config)

            assessment = self.rules_engine.evaluate_auto_aim_target(entity, target, distance=distance, require_fov=True)
            entity.auto_aim_hit_probability = self.rules_engine.calculate_hit_probability(entity, target, distance)
            entity.auto_aim_locked = bool(assessment.get('can_auto_aim', False))
            effective_fire_rate = self.rules_engine.get_effective_fire_rate_hz(entity)
            entity.fire_control_state = 'firing' if entity.auto_aim_locked and getattr(entity, 'ammo', 0) > 0 and effective_fire_rate > 0 else 'idle'
            if entity.auto_aim_locked:
                lock_text = f'锁定高价值目标 {target.id}'
            else:
                lock_text = f'跟踪高价值目标 {target.id}，角差 {assessment.get("angle_diff", 0.0):.1f}°'
            entity.ai_decision = f'{base_decision} | {lock_text}' if base_decision else lock_text

    def _resolve_cached_target_entity(self, shooter):
        locked_target = self._resolve_locked_auto_aim_target_entity(
            shooter,
            getattr(self.rules_engine, 'auto_aim_max_distance', 0.0),
        )
        if locked_target is not None:
            return locked_target
        target_state = getattr(shooter, 'target', None)
        if not isinstance(target_state, dict):
            return None
        target_id = target_state.get('id')
        if not target_id:
            return None
        target = self.entity_manager.get_entity(target_id)
        if target is None or not target.is_alive() or target.team == shooter.team:
            return None
        distance = self._distance(shooter, target)
        if distance > getattr(self.rules_engine, 'auto_aim_max_distance', 0.0):
            return None
        if not self.rules_engine.can_track_target(shooter, target, distance):
            return None
        return target

    def _clear_auto_aim_lock(self, shooter):
        shooter.autoaim_locked_target_id = None
        shooter.autoaim_lock_timer = 0.0

    def _refresh_auto_aim_lock(self, shooter, target):
        if shooter is None or target is None:
            self._clear_auto_aim_lock(shooter)
            return
        shooter.autoaim_locked_target_id = target.id
        shooter.autoaim_lock_timer = max(
            0.05,
            float(self.rules_engine.rules.get('shooting', {}).get('autoaim_lock_duration', 0.6)),
        )

    def _resolve_locked_auto_aim_target_entity(self, shooter, max_distance):
        target_id = getattr(shooter, 'autoaim_locked_target_id', None)
        if target_id is None or float(getattr(shooter, 'autoaim_lock_timer', 0.0)) <= 0.0:
            return None
        target = self.entity_manager.get_entity(target_id)
        if target is None or not target.is_alive() or target.team == shooter.team:
            self._clear_auto_aim_lock(shooter)
            return None
        distance = self._distance(shooter, target)
        if distance > max_distance:
            self._clear_auto_aim_lock(shooter)
            return None
        assessment = self.rules_engine.evaluate_auto_aim_target(shooter, target, distance=distance, require_fov=False)
        if not assessment.get('can_track', False):
            self._clear_auto_aim_lock(shooter)
            return None
        return target

    def _auto_aim_role_key(self, entity):
        if entity.type == 'sentry':
            return 'sentry'
        if entity.type == 'outpost':
            return 'outpost'
        if entity.type == 'base':
            return 'base'
        return {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
        }.get(getattr(entity, 'robot_type', ''), 'infantry')

    def _auto_aim_target_score(self, shooter, target, max_distance, assessment=None):
        if target is None:
            return float('-inf')
        if assessment is None:
            distance = self._distance(shooter, target)
            assessment = self.rules_engine.evaluate_auto_aim_target(shooter, target, distance=distance, require_fov=False)
        if not assessment.get('can_track', False):
            return float('-inf')

        role_key = self._auto_aim_role_key(target)
        threat_score = {
            'hero': 320.0,
            'sentry': 300.0,
            'infantry': 240.0,
            'engineer': 100.0,
            'outpost': 150.0,
            'base': 110.0,
        }.get(role_key, 160.0)
        distance = float(assessment.get('distance', self._distance(shooter, target)))
        distance_score = max(0.0, 1.0 - distance / max(max_distance, 1e-6)) * 190.0
        hp_ratio = 1.0
        if float(getattr(target, 'max_health', 0.0)) > 0.0:
            hp_ratio = max(0.0, min(1.0, float(getattr(target, 'health', 0.0)) / float(target.max_health)))
        finish_score = (1.0 - hp_ratio) * 150.0
        pressure_score = 0.0
        if getattr(shooter, 'last_damage_source_id', None) == target.id:
            pressure_score += 120.0
        target_state = getattr(target, 'target', None)
        if isinstance(target_state, dict) and target_state.get('id') == shooter.id:
            pressure_score += 95.0
        if getattr(target, 'fire_control_state', 'idle') == 'firing':
            pressure_score += 70.0
        if bool(getattr(target, 'respawn_weak_active', False)):
            finish_score += 35.0
        if assessment.get('can_auto_aim', False):
            pressure_score += 65.0
        elif assessment.get('within_fov', False):
            pressure_score += 25.0
        return threat_score + distance_score + finish_score + pressure_score

    def _tracked_auto_aim_candidates(self, shooter, max_distance):
        candidates = []
        has_combat_target = False
        for entity in self.entity_manager.entities:
            if entity.team == shooter.team or not entity.is_alive():
                continue
            distance = self._distance(shooter, entity)
            if distance > max_distance:
                continue
            assessment = self.rules_engine.evaluate_auto_aim_target(shooter, entity, distance=distance, require_fov=False)
            if not assessment.get('can_track', False):
                continue
            if entity.type in {'robot', 'sentry'}:
                has_combat_target = True
            score = self._auto_aim_target_score(shooter, entity, max_distance, assessment=assessment)
            candidates.append((score, distance, entity))
        if has_combat_target:
            candidates = [candidate for candidate in candidates if candidate[2].type in {'robot', 'sentry'}]
        candidates.sort(key=lambda item: (item[0], -item[1]), reverse=True)
        return candidates

    def _stabilize_entity_positions(self):
        if self.map_manager is None:
            return
        for entity in self.entity_manager.entities:
            if not getattr(entity, 'collidable', False):
                continue
            collision_radius = float(getattr(entity, 'collision_radius', 0.0))
            if getattr(entity, 'type', None) in {'robot', 'sentry'}:
                pose_valid = self.map_manager.is_position_valid_for_chassis(
                    entity.position['x'],
                    entity.position['y'],
                    float(getattr(entity, 'angle', 0.0)),
                    float(getattr(entity, 'body_length_m', getattr(entity, 'body_size_m', 0.0))),
                    float(getattr(entity, 'body_width_m', getattr(entity, 'body_size_m', 0.0))),
                    body_clearance_m=float(getattr(entity, 'body_clearance_m', 0.0)),
                )
            else:
                pose_valid = self.map_manager.is_position_valid_for_radius(entity.position['x'], entity.position['y'], collision_radius=collision_radius)
            if pose_valid:
                entity.position['z'] = self.map_manager.get_terrain_height_m(entity.position['x'], entity.position['y'])
                entity.previous_position = dict(entity.position)
                entity.last_valid_position = dict(entity.position)
                entity.spawn_position = dict(entity.position)
                entity.respawn_position = dict(entity.position)
                continue
            fallback = self.map_manager.find_nearest_passable_point(
                (entity.position['x'], entity.position['y']),
                collision_radius=collision_radius,
                search_radius=max(96, int(collision_radius * 8.0)),
                step=max(4, int(round(self.map_manager.terrain_grid_cell_size))),
            )
            if fallback is None:
                continue
            entity.position['x'] = float(fallback[0])
            entity.position['y'] = float(fallback[1])
            entity.position['z'] = self.map_manager.get_terrain_height_m(fallback[0], fallback[1])
            entity.previous_position = dict(entity.position)
            entity.last_valid_position = dict(entity.position)
            entity.spawn_position = dict(entity.position)
            entity.respawn_position = dict(entity.position)

    def _select_auto_aim_target(self, shooter, max_distance):
        locked_target = self._resolve_locked_auto_aim_target_entity(shooter, max_distance)
        candidates = self._tracked_auto_aim_candidates(shooter, max_distance)
        if not candidates:
            return locked_target

        best_target = candidates[0][2]
        best_score = candidates[0][0]
        if locked_target is not None and locked_target.id != best_target.id:
            locked_score = self._auto_aim_target_score(shooter, locked_target, max_distance)
            switch_threshold = locked_score * self._auto_aim_switch_score_ratio + self._auto_aim_switch_score_bonus
            if best_score < switch_threshold:
                self._refresh_auto_aim_lock(shooter, locked_target)
                return locked_target

        self._refresh_auto_aim_lock(shooter, best_target)
        return best_target

    def _distance(self, entity_a, entity_b):
        return ((entity_a.position['x'] - entity_b.position['x']) ** 2 + (entity_a.position['y'] - entity_b.position['y']) ** 2) ** 0.5
    
    def run(self, renderer):
        """运行游戏主循环"""
        self.running = True
        self.initialize()
        
        clock = pygame.time.Clock()
        
        while self.running:
            elapsed_ms = clock.tick(self.fps)
            if elapsed_ms <= 0:
                self.dt = self.target_dt
            else:
                self.dt = max(0.001, min(0.10, float(elapsed_ms) / 1000.0))
            instantaneous_fps = 1.0 / max(float(self.dt), 1e-6)
            self.current_fps = float(self.current_fps) + (instantaneous_fps - float(self.current_fps)) * 0.18
            self.current_frame_ms = float(self.dt) * 1000.0
            frame_start = time.perf_counter()
            # 处理事件
            event_start = time.perf_counter()
            for event in pygame.event.get():
                if hasattr(renderer, 'handle_event'):
                    if renderer.handle_event(event, self):
                        continue
                if event.type == pygame.QUIT:
                    self.running = False
                elif event.type == pygame.KEYDOWN:
                    if event.key == pygame.K_ESCAPE:
                        self.toggle_pause()
            event_end = time.perf_counter()
            self._last_event_ms = (event_end - event_start) * 1000.0
            
            # 更新游戏状态
            update_start = time.perf_counter()
            self.update()
            breakdown = self._last_update_breakdown if self._should_measure_perf() else None
            update_end = time.perf_counter()
            
            # 检查游戏是否结束
            if self.rules_engine.game_over and not self._game_over_announced:
                if self.rules_engine.winner:
                    self.add_log(f"游戏结束：{self.rules_engine.winner}方获胜", 'system')
                else:
                    self.add_log('游戏结束', 'system')
                self.paused = True
                self._game_over_announced = True
                self._last_update_breakdown = None
            
            # 渲染画面
            renderer.render(self)
            frame_end = time.perf_counter()
            update_ms = (update_end - update_start) * 1000.0
            render_ms = (frame_end - update_end) * 1000.0
            frame_ms = (frame_end - frame_start) * 1000.0
            if self.match_started and not self.paused:
                self._record_perf_sample(update_ms, render_ms, frame_ms, breakdown=breakdown, game_time=self.game_time)
            self._maybe_log_perf()
        
        # 清理资源
        self._flush_perf_samples_to_file('quit')
        self.shutdown()
        pygame.quit()

    def save_match(self, save_path='saves/latest_match.json'):
        """保存对局快照。"""
        os.makedirs(os.path.dirname(save_path), exist_ok=True)
        payload = {
            'game_time': self.game_time,
            'game_duration': self.game_duration,
            'score': self.score,
            'logs': self.logs,
            'entities': self.entity_manager.export_entity_states(),
            'facilities': self.map_manager.export_facilities_config(),
        }
        with open(save_path, 'w', encoding='utf-8') as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        self.add_log(f'对局已保存: {save_path}', 'system')

    def load_match(self, save_path='saves/latest_match.json'):
        """载入对局快照。"""
        if not os.path.exists(save_path):
            self.add_log(f'存档不存在: {save_path}', 'system')
            return False

        with open(save_path, 'r', encoding='utf-8') as f:
            payload = json.load(f)

        self.game_time = payload.get('game_time', self.game_time)
        self.game_duration = payload.get('game_duration', self.game_duration)
        self.score = payload.get('score', self.score)
        self.logs = payload.get('logs', self.logs)
        facilities = payload.get('facilities')
        if facilities:
            self.map_manager.facilities = facilities
        self.entity_manager.import_entity_states(payload.get('entities', []))
        self.match_started = True
        self.paused = False
        self.add_log(f'对局已载入: {save_path}', 'system')
        return True

    def save_editor_config(self):
        """保存开发者模式下编辑后的设施与初始站位。"""
        self.save_local_settings()

    def toggle_pause(self):
        if not self.match_started:
            self.add_log('对局尚未开始，点击开始/重开后才能暂停。', 'system')
            return
        if self.pre_match_setup_required or self.pre_match_countdown_remaining > 0.0:
            return
        self.paused = not self.paused
        self.add_log('已暂停' if self.paused else '已继续', 'system')
    
    def get_game_state(self):
        """获取游戏状态（用于AI接入）"""
        state = {
            'timestamp': time.time(),
            'game_time': self.game_time,
            'game_duration': self.game_duration,
            'score': self.score,
            'entities': [],
            'referee': {
                'red': self.rules_engine.get_referee_message(
                    self.entity_manager.entities,
                    self.map_manager,
                    self.game_time,
                    self.game_duration,
                    focus_team='red',
                ),
                'blue': self.rules_engine.get_referee_message(
                    self.entity_manager.entities,
                    self.map_manager,
                    self.game_time,
                    self.game_duration,
                    focus_team='blue',
                ),
            }
        }
        
        for entity in self.entity_manager.entities:
            state['entities'].append({
                'id': entity.id,
                'type': entity.type,
                'team': entity.team,
                'position': entity.position,
                'angle': entity.angle,
                'health': entity.health,
                'state': entity.state
            })
        
        return state

    def get_match_hud_data(self):
        """返回顶部比赛 HUD 所需数据。"""
        remaining = max(0.0, self.game_duration - self.game_time)
        pixels_per_meter_x = self.map_manager.map_width / max(self.map_manager.field_length_m, 1e-6)
        pixels_per_meter_y = self.map_manager.map_height / max(self.map_manager.field_width_m, 1e-6)
        avg_pixels_per_meter = (pixels_per_meter_x + pixels_per_meter_y) / 2.0
        teams = {}
        roster_order = ['robot_1', 'robot_2', 'robot_3', 'robot_4', 'robot_7']
        label_map = {
            'robot_1': '1 英雄',
            'robot_2': '2 工程',
            'robot_3': '3 步兵',
            'robot_4': '4 步兵',
            'robot_7': '7 哨兵',
        }

        for team in ['red', 'blue']:
            entities = {entity.id: entity for entity in self.entity_manager.entities if entity.team == team}
            units = []
            for key in roster_order:
                entity = entities.get(f'{team}_{key}')
                if entity is None:
                    continue
                units.append({
                    'id': key,
                    'entity_id': entity.id,
                    'label': label_map.get(key, key),
                    'robot_type': entity.robot_type or '',
                    'bt_node': str(getattr(entity, 'ai_behavior_node', '') or ''),
                    'hp': int(entity.health),
                    'max_hp': int(entity.max_health),
                    'level': int(getattr(entity, 'level', 1)),
                    'alive': entity.is_alive(),
                    'has_barrel': self.entity_has_barrel(entity),
                })

            base = entities.get(f'{team}_base')
            outpost = entities.get(f'{team}_outpost')
            teams[team] = {
                'gold': int(self.rules_engine.team_gold.get(team, 0)),
                'base_hp': int(base.health) if base else 0,
                'base_max_hp': int(base.max_health) if base else 0,
                'outpost_hp': int(outpost.health) if outpost else 0,
                'outpost_max_hp': int(outpost.max_health) if outpost else 0,
                'units': units,
            }

        return {
            'remaining_time': remaining,
            'round_text': '未开始' if not self.match_started else ('已暂停' if self.paused else 'Round 1/5'),
            'scale_text': f'比例尺 1m≈{avg_pixels_per_meter:.2f}单位 | 8m≈{self.rules_engine.auto_aim_max_distance:.1f}',
            'red': teams['red'],
            'blue': teams['blue'],
        }

    def get_player_hud_data(self):
        entity = self.get_player_controlled_entity()
        if entity is None:
            return None
        detail = self.get_entity_detail_data(entity.id)
        if detail is None:
            return None
        return {
            'entity_id': entity.id,
            'label': detail['label'],
            'robot_type': detail['robot_type'],
            'ammo': int(detail['ammo']),
            'power': float(detail['power']),
            'max_power': float(detail['max_power']),
            'heat': float(detail['heat']),
            'max_heat': float(detail['max_heat']),
            'gold': float(getattr(entity, 'gold', 0.0)),
            'fire_control_state': detail['fire_control_state'],
            'auto_aim_locked': bool(getattr(entity, 'auto_aim_locked', False)),
            'supply_zone': self.is_player_in_supply_zone(),
            'pitch_deg': float(getattr(entity, 'gimbal_pitch_deg', 0.0)),
            'step_climb_mode_active': bool(getattr(entity, 'step_climb_mode_active', False)),
        }

    def get_entity_detail_data(self, entity_id):
        entity = self.entity_manager.get_entity(entity_id)
        if entity is None:
            return None

        def _point_or_none(point):
            if point is None:
                return None
            return (float(point[0]), float(point[1]))

        label_map = {
            'robot_1': '1 英雄',
            'robot_2': '2 工程',
            'robot_3': '3 步兵',
            'robot_4': '4 步兵',
            'robot_7': '7 哨兵',
        }
        short_id = entity.id.replace(f'{entity.team}_', '')
        physics = self.config.get('physics', {})
        power_system = physics.get('power_system', {})
        heat_system = physics.get('heat_system', {})
        type_key_map = {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
            '哨兵': 'sentry',
        }
        rule_type_key = 'sentry' if entity.type == 'sentry' else type_key_map.get(entity.robot_type, 'infantry')
        power_rule = power_system.get(rule_type_key, {})
        heat_rule = heat_system.get(rule_type_key, {})
        rule_snapshot = self.rules_engine.get_entity_rule_snapshot(entity)
        max_speed_world = float(self.physics_engine._max_speed_world_units()) if hasattr(self.physics_engine, '_max_speed_world_units') else 1.0
        current_speed = math.hypot(float(entity.velocity.get('vx', 0.0)), float(entity.velocity.get('vy', 0.0)))
        if self.map_manager is not None and hasattr(self.map_manager, 'pixels_per_meter_x'):
            pixels_per_meter = (float(self.map_manager.pixels_per_meter_x()) + float(self.map_manager.pixels_per_meter_y())) / 2.0
        else:
            pixels_per_meter = 1.0
        current_speed_mps = current_speed / max(pixels_per_meter, 1e-6)
        movement_power_ratio = 0.0 if max_speed_world <= 1e-6 else max(0.0, min(1.0, current_speed / max_speed_world))
        speed_cap_mps = float(getattr(entity, 'chassis_speed_limit_mps', 0.0) or 0.0)
        if speed_cap_mps <= 1e-6 and hasattr(self.physics_engine, '_resolved_entity_speed_limit_mps'):
            speed_cap_mps = float(self.physics_engine._resolved_entity_speed_limit_mps(entity))
        target = None
        if isinstance(getattr(entity, 'target', None), dict):
            target_id = entity.target.get('id')
            if target_id:
                target = self.entity_manager.get_entity(target_id)

        if entity.robot_type == '英雄':
            mode_labels = {
                'left_title': '底盘模式',
                'left_options': [('health_priority', '血量优先'), ('power_priority', '功率优先')],
                'right_title': '武器模式',
                'right_options': [('ranged_priority', '远程优先'), ('melee_priority', '近战优先')],
            }
        elif entity.robot_type == '步兵':
            mode_labels = {
                'left_title': '底盘模式',
                'left_options': [('health_priority', '血量优先'), ('power_priority', '功率优先')],
                'right_title': '云台模式',
                'right_options': [('cooling_priority', '冷却优先'), ('burst_priority', '爆发优先')],
            }
        else:
            mode_labels = {
                'left_title': '底盘模式',
                'left_options': [('health_priority', '血量优先'), ('power_priority', '功率优先')],
                'right_title': '云台模式',
                'right_options': [('cooling_priority', '冷却优先'), ('burst_priority', '爆发优先')],
            }

        return {
            'entity_id': entity.id,
            'team': entity.team,
            'label': label_map.get(short_id, short_id),
            'robot_type': entity.robot_type or ('哨兵' if entity.type == 'sentry' else entity.type),
            'position_x': float(entity.position.get('x', 0.0)),
            'position_y': float(entity.position.get('y', 0.0)),
            'position_z': float(entity.position.get('z', 0.0)),
            'is_hero': getattr(entity, 'robot_type', '') == '英雄',
            'hero_has_ammo': getattr(entity, 'robot_type', '') != '英雄' or int(getattr(entity, 'ammo', 0)) > 0,
            'sentry_mode': getattr(entity, 'sentry_mode', 'auto'),
            'state': entity.state,
            'alive': entity.is_alive(),
            'is_engineer': getattr(entity, 'robot_type', '') == '工程',
            'has_barrel': self.entity_has_barrel(entity),
            'front_gun_locked': bool(getattr(entity, 'front_gun_locked', False)),
            'out_of_combat': self.rules_engine.is_out_of_combat(entity),
            'supports_drive_modes': self.entity_supports_drive_modes(entity),
            'mode_labels': mode_labels,
            'chassis_subtype': getattr(entity, 'chassis_subtype', ''),
            'chassis_subtype_label': infantry_chassis_label(getattr(entity, 'chassis_subtype', '')) if getattr(entity, 'robot_type', '') == '步兵' else '',
            'chassis_subtype_options': list(infantry_chassis_options()) if getattr(entity, 'robot_type', '') == '步兵' else [],
            'target_id': target.id if target is not None else None,
            'chassis_state': getattr(entity, 'chassis_state', 'normal'),
            'decision_summary': getattr(entity, 'ai_decision', ''),
            'decision_selected_id': getattr(entity, 'ai_decision_selected', ''),
            'decision_weights': [dict(item) for item in getattr(entity, 'ai_decision_weights', ())],
            'decision_top3': [dict(item) for item in getattr(entity, 'ai_decision_top3', ())],
            'navigation_target': _point_or_none(getattr(entity, 'ai_navigation_target', None)),
            'movement_target': _point_or_none(getattr(entity, 'ai_movement_target', None)),
            'navigation_waypoint': _point_or_none(getattr(entity, 'ai_navigation_waypoint', None)),
            'navigation_subgoals': [_point_or_none(point) for point in getattr(entity, 'ai_navigation_subgoals', ())[:6]],
            'navigation_path_preview': [_point_or_none(point) for point in getattr(entity, 'ai_path_preview', ())[:10]],
            'navigation_path_valid': bool(getattr(entity, 'ai_navigation_path_valid', False)),
            'health': float(entity.health),
            'max_health': float(entity.max_health),
            'ammo': int(getattr(entity, 'ammo', 0)),
            'ammo_17mm': int(getattr(entity, 'allowed_ammo_17mm', 0)),
            'ammo_42mm': int(getattr(entity, 'allowed_ammo_42mm', 0)),
            'power': float(getattr(entity, 'power', 0.0)),
            'max_power': float(getattr(entity, 'max_power', power_rule.get('max_power', 0.0))),
            'power_recovery_rate': float(getattr(entity, 'power_recovery_rate', power_rule.get('power_recovery_rate', 0.0))),
            'power_limit': float(getattr(entity, 'max_power', power_rule.get('max_power', 0.0))) * float(getattr(entity, 'dynamic_power_capacity_mult', 1.0)),
            'movement_power_ratio': float(movement_power_ratio),
            'movement_speed_world': float(current_speed),
            'movement_speed_mps': float(current_speed_mps),
            'movement_speed_ratio': float(movement_power_ratio),
            'movement_speed_cap_mps': float(speed_cap_mps),
            'chassis_power_draw_w': float(getattr(entity, 'chassis_power_draw_w', 0.0)),
            'chassis_rpm': float(getattr(entity, 'chassis_rpm', 0.0)),
            'chassis_power_ratio': float(getattr(entity, 'chassis_power_ratio', 1.0)),
            'chassis_mode': getattr(entity, 'chassis_mode', 'health_priority'),
            'heat': float(getattr(entity, 'heat', 0.0)),
            'max_heat': float(getattr(entity, 'max_heat', heat_rule.get('max_heat', 0.0))),
            'heat_limit': float(getattr(entity, 'max_heat', heat_rule.get('max_heat', 0.0))),
            'heat_soft_lock_threshold': float(self.rules_engine._heat_soft_lock_threshold(entity)) if self.entity_has_barrel(entity) else 0.0,
            'heat_lock_state': getattr(entity, 'heat_lock_state', 'normal'),
            'heat_lock_reason': getattr(entity, 'heat_lock_reason', ''),
            'heat_ui_disabled': bool(getattr(entity, 'heat_ui_disabled', False)),
            'heat_gain_per_shot': float(rule_snapshot['heat_per_shot']),
            'base_heat_dissipation_rate': float(getattr(entity, 'heat_dissipation_rate', heat_rule.get('heat_dissipation_rate', 0.0))),
            'current_cooling_rate': float(rule_snapshot['current_cooling_rate']),
            'gimbal_mode': getattr(entity, 'gimbal_mode', 'cooling_priority'),
            'shot_cooldown': float(getattr(entity, 'shot_cooldown', 0.0)),
            'overheat_lock_timer': float(getattr(entity, 'overheat_lock_timer', 0.0)),
            'posture': getattr(entity, 'posture', 'mobile'),
            'posture_cooldown': float(getattr(entity, 'posture_cooldown', 0.0)),
            'invincible_timer': float(getattr(entity, 'invincible_timer', 0.0)),
            'weak_timer': float(getattr(entity, 'weak_timer', 0.0)),
            'respawn_invalid_timer': float(getattr(entity, 'respawn_invalid_timer', 0.0)),
            'respawn_weak_active': bool(getattr(entity, 'respawn_weak_active', False)),
            'fort_buff_active': bool(getattr(entity, 'fort_buff_active', False)),
            'terrain_buff_timer': float(getattr(entity, 'terrain_buff_timer', 0.0)),
            'energy_small_buff_timer': float(getattr(entity, 'energy_small_buff_timer', 0.0)),
            'energy_large_buff_timer': float(getattr(entity, 'energy_large_buff_timer', 0.0)),
            'energy_large_damage_dealt_mult': float(getattr(entity, 'energy_large_damage_dealt_mult', 1.0)),
            'energy_large_damage_taken_mult': float(getattr(entity, 'energy_large_damage_taken_mult', 1.0)),
            'energy_large_cooling_mult': float(getattr(entity, 'energy_large_cooling_mult', 1.0)),
            'energy_mechanism_state': dict(self.rules_engine.get_energy_mechanism_snapshot(entity.team)),
            'active_buff_labels': list(getattr(entity, 'active_buff_labels', [])),
            'carried_minerals': int(getattr(entity, 'carried_minerals', 0)),
            'carried_mineral_type': getattr(entity, 'carried_mineral_type', None),
            'mined_minerals_total': int(getattr(entity, 'mined_minerals_total', 0)),
            'exchanged_minerals_total': int(getattr(entity, 'exchanged_minerals_total', 0)),
            'exchanged_gold_total': float(getattr(entity, 'exchanged_gold_total', 0.0)),
            'hero_deployment_active': bool(getattr(entity, 'hero_deployment_active', False)),
            'hero_deployment_state': getattr(entity, 'hero_deployment_state', 'inactive'),
            'hero_deployment_charge': float(getattr(entity, 'hero_deployment_charge', 0.0)),
            'hero_deployment_target_id': getattr(entity, 'hero_deployment_target_id', None),
            'hero_deployment_hit_probability': float(getattr(entity, 'hero_deployment_hit_probability', 0.0)),
            'hero_deployment_delay': float(self.rules_engine.rules.get('buff_zones', {}).get('buff_hero_deployment', {}).get('activation_delay_sec', 2.0)),
            'fire_control_state': getattr(entity, 'fire_control_state', 'idle'),
            'fire_rate_hz': float(rule_snapshot['fire_rate_hz']),
            'effective_fire_rate_hz': float(rule_snapshot['effective_fire_rate_hz']),
            'ammo_per_shot': int(rule_snapshot['ammo_per_shot']),
            'power_per_shot': float(rule_snapshot['power_per_shot']),
            'armor_center_height_m': float(rule_snapshot['armor_center_height_m']),
            'camera_height_m': float(rule_snapshot['camera_height_m']),
            'overheat_lock_duration': float(rule_snapshot['overheat_lock_duration']),
            'auto_aim_max_distance_m': float(rule_snapshot['auto_aim_max_distance_m']),
            'auto_aim_max_distance_world': float(rule_snapshot['auto_aim_max_distance_world']),
            'auto_aim_fov_deg': float(rule_snapshot['auto_aim_fov_deg']),
            'chassis_profile': dict(rule_snapshot['chassis_profile']),
            'gimbal_profile': dict(rule_snapshot['gimbal_profile']),
        }
