#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
import random
from copy import deepcopy
from typing import Any, Dict, List

from rules.combat_rules import calculate_damage as calculate_damage_impl
from rules.combat_rules import calculate_hero_deployment_hit_probability as calculate_hero_deployment_hit_probability_impl
from rules.combat_rules import calculate_hit_probability as calculate_hit_probability_impl
from rules.combat_rules import classify_target_motion as classify_target_motion_impl
from rules.combat_rules import damage_dealt_multiplier as damage_dealt_multiplier_impl
from rules.combat_rules import damage_taken_multiplier as damage_taken_multiplier_impl
from rules.combat_rules import describe_target_motion as describe_target_motion_impl
from rules.combat_rules import build_projectile_trace_payload as build_projectile_trace_payload_impl
from rules.combat_rules import find_projectile_hit_target as find_projectile_hit_target_impl
from rules.combat_rules import find_projectile_hit_target_metric_segment as find_projectile_hit_target_metric_segment_impl
from rules.combat_rules import get_auto_aim_accuracy as get_auto_aim_accuracy_impl
from rules.combat_rules import hit_probability_multiplier as hit_probability_multiplier_impl
from rules.combat_rules import is_fast_spinning_target as is_fast_spinning_target_impl
from rules.combat_rules import point_to_segment_distance_3d as point_to_segment_distance_3d_impl
from rules.combat_rules import projectile_collision_height_m as projectile_collision_height_m_impl
from rules.combat_rules import projectile_hits_obstacle as projectile_hits_obstacle_impl
from rules.combat_rules import reflect_projectile_direction as reflect_projectile_direction_impl
from rules.combat_rules import resolve_posture_effect as resolve_posture_effect_impl
from rules.combat_rules import resolve_projectile_aim_point as resolve_projectile_aim_point_impl
from rules.combat_rules import resolve_projectile_damage_key as resolve_projectile_damage_key_impl
from rules.combat_rules import simulate_ballistic_projectile as simulate_ballistic_projectile_impl
from rules.combat_rules import simulate_player_projectile as simulate_player_projectile_impl
from rules.combat_rules import spawn_projectile_trace as spawn_projectile_trace_impl
from rules.combat_rules import trigger_evasive_spin as trigger_evasive_spin_impl
from rules.combat_rules import update_heat_mechanism as update_heat_mechanism_impl
from rules.combat_rules import update_projectile_traces as update_projectile_traces_impl
from rules.combat_rules import update_radar_marks as update_radar_marks_impl
from rules.combat_rules import update_sentry_posture_and_heat as update_sentry_posture_and_heat_impl
from rules.facility_rules import apply_buff_region as apply_buff_region_impl
from rules.facility_rules import apply_dead_zone_penalty as apply_dead_zone_penalty_impl
from rules.facility_rules import buff_access_allowed as buff_access_allowed_impl
from rules.facility_rules import claim_supply_ammo as claim_supply_ammo_impl
from rules.facility_rules import energy_activation_anchor as energy_activation_anchor_impl
from rules.facility_rules import energy_front_descriptor as energy_front_descriptor_impl
from rules.facility_rules import energy_large_reward as energy_large_reward_impl
from rules.facility_rules import energy_virtual_hits_per_sec as energy_virtual_hits_per_sec_impl
from rules.facility_rules import expire_buff_path_progress as expire_buff_path_progress_impl
from rules.facility_rules import finish_traversal_if_needed as finish_traversal_if_needed_impl
from rules.facility_rules import get_energy_mechanism_snapshot as get_energy_mechanism_snapshot_impl
from rules.facility_rules import grant_large_energy_buff as grant_large_energy_buff_impl
from rules.facility_rules import grant_small_energy_buff as grant_small_energy_buff_impl
from rules.facility_rules import handle_exchange_zone as handle_exchange_zone_impl
from rules.facility_rules import handle_mining_zone as handle_mining_zone_impl
from rules.facility_rules import handle_paired_buff_region as handle_paired_buff_region_impl
from rules.facility_rules import is_in_team_supply_zone as is_in_team_supply_zone_impl
from rules.facility_rules import is_valid_energy_activator as is_valid_energy_activator_impl
from rules.facility_rules import make_energy_team_state as make_energy_team_state_impl
from rules.facility_rules import minerals_per_trip as minerals_per_trip_impl
from rules.facility_rules import mining_duration as mining_duration_impl
from rules.facility_rules import paired_buff_timeout_by_key as paired_buff_timeout_by_key_impl
from rules.facility_rules import purchase_manual_role_ammo as purchase_manual_role_ammo_impl
from rules.facility_rules import region_contains_point as region_contains_point_impl
from rules.facility_rules import region_has_enemy_occupant as region_has_enemy_occupant_impl
from rules.facility_rules import reset_dynamic_effects as reset_dynamic_effects_impl
from rules.facility_rules import structure_alive as structure_alive_impl
from rules.facility_rules import supply_ammo_gain as supply_ammo_gain_impl
from rules.facility_rules import supply_claimable_ammo as supply_claimable_ammo_impl
from rules.facility_rules import team_energy_anchor as team_energy_anchor_impl
from rules.facility_rules import terrain_access_allowed as terrain_access_allowed_impl
from rules.facility_rules import try_purchase_role_ammo as try_purchase_role_ammo_impl
from rules.facility_rules import update_energy_mechanism_control as update_energy_mechanism_control_impl
from rules.facility_rules import update_facility_effects as update_facility_effects_impl
from rules.facility_rules import update_occupied_facilities as update_occupied_facilities_impl
from rules.facility_rules import update_traversal_progress as update_traversal_progress_impl
from rules.respawn_rules import calculate_respawn_read_duration as calculate_respawn_read_duration_impl
from rules.respawn_rules import clear_negative_states as clear_negative_states_impl
from rules.respawn_rules import handle_destroy as handle_destroy_impl
from rules.respawn_rules import is_fast_respawn_context as is_fast_respawn_context_impl
from rules.respawn_rules import is_respawn_weak as is_respawn_weak_impl
from rules.respawn_rules import respawn_safe_zone_reached as respawn_safe_zone_reached_impl
from rules.respawn_rules import respawn_entity as respawn_entity_impl


class RulesEngine:
    @staticmethod
    def build_rule_config(raw_rules=None):
        defaults = {
            'game_duration': 420,
            'economy': {
                'initial_gold': 400.0,
            },
            'combat': {
                'disengage_duration': 6.0,
            },
            'collision': {
                'damage_cooldown': 0.5,
                'min_impact_speed': 1.0,
            },
            'robot_profiles': {
                'hero': {
                    'max_health': 150,
                    'initial_health': 150,
                    'max_power': 100,
                    'power_recovery_rate': 1.0,
                    'max_heat': 100,
                    'heat_gain_per_shot': 10,
                    'heat_dissipation_rate': 5,
                    'ammo_type': '42mm',
                    'initial_allowed_ammo_17mm': 0,
                    'initial_allowed_ammo_42mm': 0,
                },
                'engineer': {
                    'max_health': 250,
                    'initial_health': 250,
                    'max_power': 120,
                    'power_recovery_rate': 1.2,
                    'max_heat': 0,
                    'heat_gain_per_shot': 0,
                    'heat_dissipation_rate': 0,
                    'fire_rate_hz': 0.0,
                    'ammo_per_shot': 0,
                    'ammo_type': 'none',
                    'initial_allowed_ammo_17mm': 0,
                    'initial_allowed_ammo_42mm': 0,
                },
                'infantry': {
                    'max_health': 100,
                    'initial_health': 100,
                    'max_power': 60,
                    'power_recovery_rate': 1.5,
                    'max_heat': 60,
                    'heat_gain_per_shot': 6,
                    'heat_dissipation_rate': 3,
                    'ammo_type': '17mm',
                    'initial_allowed_ammo_17mm': 0,
                    'initial_allowed_ammo_42mm': 0,
                },
            },
            'sentry': {
                'default_mode': 'auto',
                'posture_cooldown': 5.0,
                'posture_decay_time': 180.0,
                'modes': {
                    'auto': {
                        'max_health': 400,
                        'initial_health': 400,
                        'max_power': 100,
                        'power_recovery_rate': 2.0,
                        'max_heat': 260,
                        'heat_gain_per_shot': 12,
                        'heat_dissipation_rate': 30,
                        'ammo_type': '17mm',
                        'initial_allowed_ammo_17mm': 300,
                        'initial_allowed_ammo_42mm': 0,
                    },
                    'semi_auto': {
                        'max_health': 200,
                        'initial_health': 200,
                        'max_power': 60,
                        'power_recovery_rate': 1.6,
                        'max_heat': 100,
                        'heat_gain_per_shot': 12,
                        'heat_dissipation_rate': 10,
                        'ammo_type': '17mm',
                        'initial_allowed_ammo_17mm': 200,
                        'initial_allowed_ammo_42mm': 0,
                    },
                },
                'exchange': {
                    'ammo_cost': 10,
                    'interval_sec': 2.0,
                    'remote_ammo_cost': 100,
                    'hp_cost': 20,
                    'remote_hp_cost': 30,
                    'revive_now_cost': 80,
                    'auto_intervention_cost': 50,
                    'semi_auto_intervention_cost': 0,
                    'local_17mm_unit': 10,
                    'local_42mm_unit': 1,
                    'remote_17mm_unit': 100,
                    'remote_42mm_unit': 10,
                    'remote_delay': 6.0,
                    'remote_hp_gain_ratio': 0.6,
                    'hp_gain_ratio': 0.6,
                },
            },
            'base': {
                'max_health': 2000,
            },
            'outpost': {
                'max_health': 1500,
            },
            'radar': {
                'range': 260,
                'mark_gain_per_sec': 1.0,
                'mark_decay_per_sec': 0.5,
                'vulnerability_mult': 1.25,
            },
            'shooting': {
                'fire_rate_hz': 8.0,
                'ammo_per_shot': 1,
                'power_per_shot': 6.0,
                'heat_per_shot': 12.0,
                'projectile_speed_17mm_mps': 23.0,
                'projectile_speed_42mm_mps': 17.0,
                'projectile_gravity_mps2': 9.81,
                'projectile_drag_17mm': 0.018,
                'projectile_drag_42mm': 0.026,
                'projectile_simulation_dt_sec': 0.01,
                'heat_gain_17mm': 10.0,
                'heat_gain_42mm': 100.0,
                'heat_detection_hz': 10.0,
                'heat_soft_lock_margin_17mm': 100.0,
                'heat_soft_lock_margin_42mm': 200.0,
                'overheat_lock_duration': 5.0,
                'armor_center_height_m': 0.15,
                'turret_axis_height_m': 0.50,
                'camera_height_m': 0.50,
                'max_pitch_up_deg': 30.0,
                'max_pitch_down_deg': 30.0,
                'los_sample_step_m': 0.25,
                'los_clearance_m': 0.02,
                'los_pitch_margin_deg': 0.8,
                'los_pitch_probe_distance_m': 0.45,
                'los_terrain_pitch_margin_deg': 1.2,
                'auto_aim_max_distance_m': 8.0,
                'auto_aim_fov_deg': 60.0,
                'auto_aim_track_speed_deg_per_sec': 180.0,
                'motion_thresholds': {
                    'spinning_angular_velocity_deg': 45.0,
                    'translating_target_speed_mps': 0.45,
                },
                'auto_aim_accuracy': {
                    'near_all': 0.30,
                    'mid_fixed': 1.00,
                    'mid_spin': 0.80,
                    'mid_translating_spin': 0.60,
                    'far_fixed': 0.90,
                    'far_spin': 0.40,
                    'far_translating_spin': 0.10,
                },
                'base_hit_probability': 0.88,
                'min_hit_probability': 0.1,
                'range_falloff': 0.65,
                'movement_penalty_factor': 0.04,
                'aim_angle_penalty_deg': 90.0,
                'autoaim_lock_duration': 0.6,
                'fast_spin_hit_multiplier': 0.6,
                'fast_spin_threshold_deg_per_sec': 300.0,
                'evasive_spin_duration': 1.8,
                'evasive_spin_rate_deg': 420.0,
                'hero_deployment_structure_fire': {
                    'max_hit_probability': 0.7,
                    'min_hit_probability': 0.2,
                    'optimal_distance_m': 8.0,
                    'falloff_end_distance_m': 20.0,
                    'target_types': ['outpost', 'base'],
                },
                'hero_mobile_accuracy_mult': 0.7,
            },
            'control_modes': {
                'chassis': {
                    'health_priority': {
                        'min_power_ratio': 0.22,
                        'base_fire_rate_mult': 0.92,
                        'low_hp_ratio': 0.35,
                        'low_hp_fire_rate_mult': 0.78,
                        'power_cost_mult': 0.9,
                    },
                    'power_priority': {
                        'min_power_ratio': 0.08,
                        'base_fire_rate_mult': 1.0,
                        'low_hp_ratio': 0.2,
                        'low_hp_fire_rate_mult': 0.92,
                        'power_cost_mult': 1.05,
                    },
                },
                'gimbal': {
                    'cooling_priority': {
                        'base_fire_rate_mult': 0.9,
                        'soft_heat_ratio': 0.72,
                        'min_fire_rate_ratio': 0.28,
                    },
                    'burst_priority': {
                        'base_fire_rate_mult': 1.15,
                        'soft_heat_ratio': 0.88,
                        'min_fire_rate_ratio': 0.45,
                    },
                },
            },
            'performance_profiles': {
                'hero': {
                    'weapon_modes': {
                        'melee_priority': {
                            '1': {'max_health': 200, 'initial_health': 200, 'max_power': 70, 'max_heat': 140, 'heat_dissipation_rate': 12, 'ammo_type': '42mm'},
                            '2': {'max_health': 225, 'initial_health': 225, 'max_power': 75, 'max_heat': 150, 'heat_dissipation_rate': 14, 'ammo_type': '42mm'},
                            '3': {'max_health': 250, 'initial_health': 250, 'max_power': 80, 'max_heat': 160, 'heat_dissipation_rate': 16, 'ammo_type': '42mm'},
                            '4': {'max_health': 275, 'initial_health': 275, 'max_power': 85, 'max_heat': 170, 'heat_dissipation_rate': 18, 'ammo_type': '42mm'},
                            '5': {'max_health': 300, 'initial_health': 300, 'max_power': 90, 'max_heat': 180, 'heat_dissipation_rate': 20, 'ammo_type': '42mm'},
                            '6': {'max_health': 325, 'initial_health': 325, 'max_power': 95, 'max_heat': 190, 'heat_dissipation_rate': 22, 'ammo_type': '42mm'},
                            '7': {'max_health': 350, 'initial_health': 350, 'max_power': 100, 'max_heat': 200, 'heat_dissipation_rate': 24, 'ammo_type': '42mm'},
                            '8': {'max_health': 375, 'initial_health': 375, 'max_power': 105, 'max_heat': 210, 'heat_dissipation_rate': 26, 'ammo_type': '42mm'},
                            '9': {'max_health': 400, 'initial_health': 400, 'max_power': 110, 'max_heat': 220, 'heat_dissipation_rate': 28, 'ammo_type': '42mm'},
                            '10': {'max_health': 450, 'initial_health': 450, 'max_power': 120, 'max_heat': 240, 'heat_dissipation_rate': 30, 'ammo_type': '42mm'},
                        },
                        'ranged_priority': {
                            '1': {'max_health': 150, 'initial_health': 150, 'max_power': 50, 'max_heat': 100, 'heat_dissipation_rate': 20, 'ammo_type': '42mm'},
                            '2': {'max_health': 165, 'initial_health': 165, 'max_power': 55, 'max_heat': 102, 'heat_dissipation_rate': 23, 'ammo_type': '42mm'},
                            '3': {'max_health': 180, 'initial_health': 180, 'max_power': 60, 'max_heat': 104, 'heat_dissipation_rate': 26, 'ammo_type': '42mm'},
                            '4': {'max_health': 195, 'initial_health': 195, 'max_power': 65, 'max_heat': 106, 'heat_dissipation_rate': 29, 'ammo_type': '42mm'},
                            '5': {'max_health': 210, 'initial_health': 210, 'max_power': 70, 'max_heat': 108, 'heat_dissipation_rate': 32, 'ammo_type': '42mm'},
                            '6': {'max_health': 225, 'initial_health': 225, 'max_power': 75, 'max_heat': 110, 'heat_dissipation_rate': 35, 'ammo_type': '42mm'},
                            '7': {'max_health': 240, 'initial_health': 240, 'max_power': 80, 'max_heat': 115, 'heat_dissipation_rate': 38, 'ammo_type': '42mm'},
                            '8': {'max_health': 255, 'initial_health': 255, 'max_power': 85, 'max_heat': 120, 'heat_dissipation_rate': 41, 'ammo_type': '42mm'},
                            '9': {'max_health': 270, 'initial_health': 270, 'max_power': 90, 'max_heat': 125, 'heat_dissipation_rate': 44, 'ammo_type': '42mm'},
                            '10': {'max_health': 300, 'initial_health': 300, 'max_power': 100, 'max_heat': 130, 'heat_dissipation_rate': 50, 'ammo_type': '42mm'},
                        },
                    },
                },
                'infantry': {
                    'chassis_modes': {
                        'power_priority': {
                            '1': {'max_health': 150, 'initial_health': 150, 'max_power': 60},
                            '2': {'max_health': 175, 'initial_health': 175, 'max_power': 65},
                            '3': {'max_health': 200, 'initial_health': 200, 'max_power': 70},
                            '4': {'max_health': 225, 'initial_health': 225, 'max_power': 75},
                            '5': {'max_health': 250, 'initial_health': 250, 'max_power': 80},
                            '6': {'max_health': 275, 'initial_health': 275, 'max_power': 85},
                            '7': {'max_health': 300, 'initial_health': 300, 'max_power': 90},
                            '8': {'max_health': 325, 'initial_health': 325, 'max_power': 95},
                            '9': {'max_health': 350, 'initial_health': 350, 'max_power': 100},
                            '10': {'max_health': 400, 'initial_health': 400, 'max_power': 100},
                        },
                        'health_priority': {
                            '1': {'max_health': 200, 'initial_health': 200, 'max_power': 45},
                            '2': {'max_health': 225, 'initial_health': 225, 'max_power': 50},
                            '3': {'max_health': 250, 'initial_health': 250, 'max_power': 55},
                            '4': {'max_health': 275, 'initial_health': 275, 'max_power': 60},
                            '5': {'max_health': 300, 'initial_health': 300, 'max_power': 65},
                            '6': {'max_health': 325, 'initial_health': 325, 'max_power': 70},
                            '7': {'max_health': 350, 'initial_health': 350, 'max_power': 75},
                            '8': {'max_health': 375, 'initial_health': 375, 'max_power': 80},
                            '9': {'max_health': 400, 'initial_health': 400, 'max_power': 90},
                            '10': {'max_health': 400, 'initial_health': 400, 'max_power': 100},
                        },
                    },
                    'gimbal_modes': {
                        'burst_priority': {
                            '1': {'max_heat': 170, 'heat_dissipation_rate': 5, 'ammo_type': '17mm'},
                            '2': {'max_heat': 180, 'heat_dissipation_rate': 7, 'ammo_type': '17mm'},
                            '3': {'max_heat': 190, 'heat_dissipation_rate': 9, 'ammo_type': '17mm'},
                            '4': {'max_heat': 200, 'heat_dissipation_rate': 11, 'ammo_type': '17mm'},
                            '5': {'max_heat': 210, 'heat_dissipation_rate': 12, 'ammo_type': '17mm'},
                            '6': {'max_heat': 220, 'heat_dissipation_rate': 13, 'ammo_type': '17mm'},
                            '7': {'max_heat': 230, 'heat_dissipation_rate': 14, 'ammo_type': '17mm'},
                            '8': {'max_heat': 240, 'heat_dissipation_rate': 16, 'ammo_type': '17mm'},
                            '9': {'max_heat': 250, 'heat_dissipation_rate': 18, 'ammo_type': '17mm'},
                            '10': {'max_heat': 260, 'heat_dissipation_rate': 20, 'ammo_type': '17mm'},
                        },
                        'cooling_priority': {
                            '1': {'max_heat': 40, 'heat_dissipation_rate': 12, 'ammo_type': '17mm'},
                            '2': {'max_heat': 48, 'heat_dissipation_rate': 14, 'ammo_type': '17mm'},
                            '3': {'max_heat': 56, 'heat_dissipation_rate': 16, 'ammo_type': '17mm'},
                            '4': {'max_heat': 64, 'heat_dissipation_rate': 18, 'ammo_type': '17mm'},
                            '5': {'max_heat': 72, 'heat_dissipation_rate': 20, 'ammo_type': '17mm'},
                            '6': {'max_heat': 80, 'heat_dissipation_rate': 22, 'ammo_type': '17mm'},
                            '7': {'max_heat': 88, 'heat_dissipation_rate': 24, 'ammo_type': '17mm'},
                            '8': {'max_heat': 96, 'heat_dissipation_rate': 26, 'ammo_type': '17mm'},
                            '9': {'max_heat': 114, 'heat_dissipation_rate': 28, 'ammo_type': '17mm'},
                            '10': {'max_heat': 120, 'heat_dissipation_rate': 30, 'ammo_type': '17mm'},
                        },
                    },
                },
            },
            'mining': {
                'mine_duration_min_sec': 10.0,
                'mine_duration_max_sec': 15.0,
                'exchange_duration_min_sec': 10.0,
                'exchange_duration_max_sec': 15.0,
                'minerals_per_trip': 1,
                'gold_per_mineral': 120.0,
            },
            'energy_mechanism': {
                'activation_distance_min_m': 4.0,
                'activation_distance_max_m': 7.0,
                'activation_anchor_radius_m': 2.2,
                'activation_hold_sec': 10.0,
                'activation_window_sec': 20.0,
                'front_angle_deg': 55.0,
                'buff_duration_sec': 45.0,
                'damage_dealt_mult': 1.15,
                'cooling_mult': 1.2,
                'power_recovery_mult': 1.15,
                'allowed_role_keys': ['hero', 'infantry', 'sentry'],
                'small_opportunity_times_sec': [0.0, 90.0],
                'large_opportunity_times_sec': [180.0, 255.0, 330.0],
                'small_defense_mult': 0.75,
                'small_buff_duration_sec': 45.0,
                'large_duration_by_hits': {'5': 30.0, '6': 35.0, '7': 40.0, '8': 45.0, '9': 50.0, '10': 60.0},
                'virtual_hits_per_sec': {'hero': 0.45, 'infantry': 0.36, 'sentry': 0.32},
            },
            'ammo_purchase': {
                'purchase_interval_sec': 2.0,
                '17mm_batch': 200,
                '42mm_batch': 10,
                '17mm_cost': 200.0,
                '42mm_cost': 100.0,
                'max_allowed_17mm': 200,
                'max_allowed_42mm': 10,
                'opening_targets': {
                    'hero_42mm': 10,
                    'infantry_17mm': 100,
                },
            },
            'supply': {
                    'ammo_gain': 100,
                    'ammo_gain_17mm': 100,
                    'ammo_gain_42mm': 10,
                    'ammo_interval': 60.0,
                    'heal_ratio_per_sec': 0.10,
                    'late_heal_ratio_per_sec': 0.25,
                'late_heal_start_time': 240.0,
            },
                'buff_zones': {
                    'buff_base': {'damage_taken_mult': 0.5, 'clear_weak': True, 'team_locked': True, 'allowed_entity_types': ['robot']},
                    'buff_central_highland': {'damage_taken_mult': 0.75, 'allowed_role_keys': ['hero', 'infantry', 'sentry'], 'exclusive_control': True},
                    'buff_trapezoid_highland': {'damage_taken_mult': 0.5, 'team_locked': True, 'allowed_entity_types': ['robot']},
                    'buff_outpost': {
                        'damage_taken_mult': 0.75,
                        'clear_weak': True,
                        'team_locked': True,
                        'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'],
                        'require_owner_outpost_alive': True,
                        'allow_enemy_capture_after_outpost_destroyed_sec': 300.0,
                        'enemy_allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'],
                    },
                    'buff_assembly': {
                        'team_locked': True,
                        'engineer_only': True,
                        'invincible': True,
                        'max_duration_sec': 45.0,
                    },
                    'buff_hero_deployment': {
                        'team_locked': True,
                        'hero_only': True,
                        'activation_delay_sec': 2.0,
                        'damage_taken_mult': 0.75,
                        'damage_dealt_mult': 1.5,
                    },
                    'buff_terrain_highland_red_start': {'pair_role': 'start', 'pair_key': 'terrain_highland_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 5.0},
                    'buff_terrain_highland_red_end': {'pair_role': 'end', 'pair_key': 'terrain_highland_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 5.0, 'timed_effects': {'terrain_highland_defense': 30.0}},
                    'buff_terrain_highland_blue_start': {'pair_role': 'start', 'pair_key': 'terrain_highland_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 5.0},
                    'buff_terrain_highland_blue_end': {'pair_role': 'end', 'pair_key': 'terrain_highland_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 5.0, 'timed_effects': {'terrain_highland_defense': 30.0}},
                    'buff_terrain_road_red_start': {'pair_role': 'start', 'pair_key': 'terrain_road_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0},
                    'buff_terrain_road_red_end': {'pair_role': 'end', 'pair_key': 'terrain_road_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0, 'timed_effects': {'terrain_road_cooling': 5.0}, 'cooldown_key': 'terrain_road_lockout', 'cooldown_duration_sec': 15.0},
                    'buff_terrain_road_blue_start': {'pair_role': 'start', 'pair_key': 'terrain_road_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0},
                    'buff_terrain_road_blue_end': {'pair_role': 'end', 'pair_key': 'terrain_road_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0, 'timed_effects': {'terrain_road_cooling': 5.0}, 'cooldown_key': 'terrain_road_lockout', 'cooldown_duration_sec': 15.0},
                    'buff_terrain_fly_slope_red_start': {'pair_role': 'start', 'pair_key': 'terrain_fly_slope_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0},
                    'buff_terrain_fly_slope_red_end': {'pair_role': 'end', 'pair_key': 'terrain_fly_slope_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0, 'timed_effects': {'terrain_fly_slope_defense': 30.0}},
                    'buff_terrain_fly_slope_blue_start': {'pair_role': 'start', 'pair_key': 'terrain_fly_slope_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0},
                    'buff_terrain_fly_slope_blue_end': {'pair_role': 'end', 'pair_key': 'terrain_fly_slope_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 3.0, 'timed_effects': {'terrain_fly_slope_defense': 30.0}},
                    'buff_terrain_slope_red_start': {'pair_role': 'start', 'pair_key': 'terrain_slope_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 10.0},
                    'buff_terrain_slope_red_end': {'pair_role': 'end', 'pair_key': 'terrain_slope_red', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 10.0, 'timed_effects': {'terrain_slope_defense': 10.0, 'terrain_slope_cooling': 120.0}},
                    'buff_terrain_slope_blue_start': {'pair_role': 'start', 'pair_key': 'terrain_slope_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 10.0},
                    'buff_terrain_slope_blue_end': {'pair_role': 'end', 'pair_key': 'terrain_slope_blue', 'team_locked': True, 'allowed_role_keys': ['hero', 'engineer', 'infantry', 'sentry'], 'sequence_timeout_sec': 10.0, 'timed_effects': {'terrain_slope_defense': 10.0, 'terrain_slope_cooling': 120.0}},
                    'buff_supply': {'acts_as_supply': True, 'team_locked': True, 'allowed_entity_types': ['robot'], 'engineer_invincible': True},
                    'buff_fort': {
                        'acts_as_fort': True,
                        'team_locked': True,
                        'allowed_role_keys': ['infantry', 'sentry'],
                        'require_owner_outpost_destroyed': True,
                        'allow_enemy_capture_after_outpost_destroyed_sec': 180.0,
                        'enemy_allowed_role_keys': ['infantry', 'sentry'],
                    },
                },
            'fort': {
                'damage_taken_mult': 0.85,
                'hit_probability_mult': 1.1,
                'cooling_mult': 1.2,
            },
            'terrain_cross': {
                'duration': 15.0,
                'min_hold_time': 1.0,
                'completion_ratio': 0.55,
                'damage_taken_mult': 0.9,
                'damage_dealt_mult': 1.1,
                'hit_probability_mult': 1.08,
                'cooling_mult': 1.15,
            },
            'terrain_access': {
                'undulating_road': {'allowed_role_keys': []},
            },
            'respawn': {
                'robot_delay': 10.0,
                'sentry_delay': 10.0,
                'invincible_duration': 3.0,
                'weaken_duration': 30.0,
                'invalid_duration': 30.0,
                'invalid_min_elapsed_before_release': 10.0,
                'invalid_release_delay_after_safe_zone': 10.0,
                'respawn_formula_remaining_time_threshold': 420.0,
                'respawn_formula_remaining_time_divisor': 10.0,
                'respawn_formula_instant_revive_addition': 20.0,
                'fast_respawn_rate': 4.0,
                'weaken_damage_taken_mult': 1.15,
                'weaken_damage_dealt_mult': 0.7,
            },
            'health': {
                'robot': {'max_health': 100, 'initial_health': 100},
                'sentry': {'max_health': 400, 'initial_health': 400},
                'uav': {'max_health': 50, 'initial_health': 50},
                'outpost': {'max_health': 1500, 'initial_health': 1500},
                'base': {'max_health': 2000, 'initial_health': 2000},
                'dart': {'max_health': 10, 'initial_health': 10},
                'radar': {'max_health': 80, 'initial_health': 80},
            },
            'damage': {
                'robot': {'bullet_17mm': 20, 'bullet_42mm': 200, 'dart': 200, 'collision': 2},
                'sentry': {'bullet_17mm': 20, 'bullet_42mm': 200, 'dart': 200, 'collision': 2},
                'uav': {'bullet_17mm': 20, 'bullet_42mm': 200, 'dart': 200, 'collision': 2},
                'outpost': {'bullet_17mm': 20, 'bullet_42mm': 200, 'dart': 750, 'collision': 0},
                'base': {
                    'bullet_17mm': 20,
                    'bullet_17mm_front_upper': 5,
                    'bullet_42mm': 200,
                    'dart': 625,
                    'dart_fixed_target': 200,
                    'dart_random_fixed_target': 300,
                    'dart_terminal_moving_target': 1000,
                    'collision': 0,
                },
                'dart': {'bullet_17mm': 0, 'bullet_42mm': 0, 'dart': 0, 'collision': 0},
                'radar': {'bullet_17mm': 20, 'bullet_42mm': 200, 'dart': 200, 'collision': 2},
            },
        }

        def merge_dict(base, override):
            merged = deepcopy(base)
            if not isinstance(override, dict):
                return merged
            for key, value in override.items():
                if key in merged and isinstance(merged[key], dict) and isinstance(value, dict):
                    merged[key] = merge_dict(merged[key], value)
                else:
                    merged[key] = deepcopy(value)
            return merged

        return merge_dict(defaults, raw_rules or {})

    def __init__(self, config):
        self.config = config
        self.rules = self.build_rule_config(config.get('rules', {}))
        self.config['rules'] = self.rules
        self.damage_system = self.load_damage_system()
        self.health_system = self.load_health_system()
        self.game_over = False
        self.winner = None
        self.game_engine: Any = None
        self.stage = 'running'
        self.initial_gold = float(self.rules['economy'].get('initial_gold', 400.0))
        self.team_gold = {'red': self.initial_gold, 'blue': self.initial_gold}
        self.team_minerals = {'red': 0, 'blue': 0}
        self.posture_effects = {
            'mobile': {
                'power_mult': 1.5,
                'cool_mult': 1.0 / 3.0,
                'damage_mult': 1.25,
                'decay': {'power_mult': 1.2},
            },
            'attack': {
                'power_mult': 0.5,
                'cool_mult': 3.0,
                'damage_mult': 1.25,
                'decay': {'cool_mult': 2.0},
            },
            'defense': {
                'power_mult': 0.5,
                'cool_mult': 1.0 / 3.0,
                'damage_mult': 0.5,
                'decay': {'damage_mult': 0.75},
            },
        }
        self.radar_marks: Dict[str, float] = {}
        self.energy_activation_progress = {
            'red': self._make_energy_team_state(),
            'blue': self._make_energy_team_state(),
        }
        self.occupied_facilities = {
            'red': {'base': [], 'outpost': [], 'fly_slope': [], 'undulating_road': [], 'rugged_road': [], 'first_step': [], 'dog_hole': [], 'second_step': [], 'supply': [], 'fort': [], 'energy_mechanism': [], 'mining_area': [], 'mineral_exchange': []},
            'blue': {'base': [], 'outpost': [], 'fly_slope': [], 'undulating_road': [], 'rugged_road': [], 'first_step': [], 'dog_hole': [], 'second_step': [], 'supply': [], 'fort': [], 'energy_mechanism': [], 'mining_area': [], 'mineral_exchange': []},
        }
        self.terrain_cross_types = {'fly_slope', 'undulating_road', 'first_step', 'second_step', 'dog_hole'}
        self.auto_aim_max_distance = self._meters_to_world_units(self.rules['shooting']['auto_aim_max_distance_m'])
        self.projectile_traces = []
        self.projectile_trace_limit = int(max(32, self.rules.get('shooting', {}).get('projectile_trace_limit', 192)))
        self.collision_damage_cooldowns = {}
        self.game_time = 0.0
        self.game_duration = float(self.rules.get('game_duration', 420.0))
        self._frame_cache_token = None
        self._auto_aim_eval_cache = {}
        self._line_of_sight_cache = {}
        self._armor_target_cache = {}
        self._facility_update_accumulator = 0.0
        self._facility_update_interval = 0.04

    def start_frame(self, frame_token):
        if frame_token == self._frame_cache_token:
            return
        self._frame_cache_token = frame_token
        self._auto_aim_eval_cache.clear()
        self._line_of_sight_cache.clear()
        self._armor_target_cache.clear()

    def _entity_pose_cache_key(self, entity, include_turret=False):
        key = (
            getattr(entity, 'id', None),
            round(float(entity.position['x']), 2),
            round(float(entity.position['y']), 2),
            round(float(getattr(entity, 'angle', 0.0)), 2),
        )
        if include_turret:
            key += (round(float(getattr(entity, 'turret_angle', getattr(entity, 'angle', 0.0))), 2),)
        return key

    def _line_of_sight_cache_key(self, shooter, target):
        map_manager = self._map_manager()
        raster_version = getattr(map_manager, 'raster_version', 0)
        return (self._entity_pose_cache_key(shooter), self._entity_pose_cache_key(target), raster_version)

    def _auto_aim_cache_key(self, shooter, target, distance, require_fov):
        return (
            self._entity_pose_cache_key(shooter, include_turret=True),
            self._entity_pose_cache_key(target),
            round(float(distance), 2),
            bool(require_fov),
        )

    def load_damage_system(self):
        return deepcopy(self.rules['damage'])

    def load_health_system(self):
        return deepcopy(self.rules['health'])

    def get_current_cooling_rate(self, entity):
        base_rate = float(getattr(entity, 'heat_dissipation_rate', 0.0))
        base_rate *= float(getattr(entity, 'dynamic_cooling_mult', 1.0))
        if entity.type != 'sentry':
            return base_rate

        posture_effect = self._resolve_posture_effect(entity)
        cooling_mult = posture_effect['cool_mult']
        if getattr(entity, 'fort_buff_active', False):
            cooling_mult *= self.rules['fort']['cooling_mult']
        if getattr(entity, 'terrain_buff_timer', 0.0) > 0:
            cooling_mult *= self.rules['terrain_cross']['cooling_mult']
        return base_rate * cooling_mult

    def _heat_gain_per_shot(self, entity):
        ammo_type = getattr(entity, 'ammo_type', None)
        shooting_rules = self.rules.get('shooting', {})
        if ammo_type == '42mm':
            return float(shooting_rules.get('heat_gain_42mm', 100.0))
        if ammo_type == '17mm':
            return float(shooting_rules.get('heat_gain_17mm', 10.0))
        return float(getattr(entity, 'heat_gain_per_shot', shooting_rules.get('heat_per_shot', 0.0)))

    def _heat_soft_lock_threshold(self, entity):
        shooting_rules = self.rules.get('shooting', {})
        ammo_type = getattr(entity, 'ammo_type', None)
        margin = shooting_rules.get('heat_soft_lock_margin_42mm', 200.0) if ammo_type == '42mm' else shooting_rules.get('heat_soft_lock_margin_17mm', 100.0)
        return float(getattr(entity, 'max_heat', 0.0)) + float(margin)

    def _set_heat_lock_state(self, entity, state, reason=''):
        entity.heat_lock_state = state
        entity.heat_lock_reason = reason if state != 'normal' else ''
        entity.heat_ui_disabled = state != 'normal'
        if state != 'normal':
            entity.fire_control_state = 'idle'
            entity.auto_aim_locked = False

    def get_entity_rule_snapshot(self, entity):
        shooting = self.rules['shooting']
        control_modes = self.rules['control_modes']
        chassis_mode = getattr(entity, 'chassis_mode', 'health_priority')
        gimbal_mode = getattr(entity, 'gimbal_mode', 'cooling_priority')
        armor_height = self._target_armor_height_m(entity) - self._entity_ground_height_m(entity)
        turret_height = self._shooter_view_height_m(entity) - self._entity_ground_height_m(entity)
        return {
            'fire_rate_hz': float(getattr(entity, 'fire_rate_hz', shooting['fire_rate_hz'])),
            'effective_fire_rate_hz': float(self.get_effective_fire_rate_hz(entity)),
            'ammo_per_shot': int(getattr(entity, 'ammo_per_shot', shooting['ammo_per_shot'])),
            'power_per_shot': float(self.get_effective_power_per_shot(entity)),
            'heat_per_shot': float(self._heat_gain_per_shot(entity)),
            'overheat_lock_duration': float(shooting['overheat_lock_duration']),
            'armor_center_height_m': float(armor_height),
            'turret_axis_height_m': float(turret_height),
            'camera_height_m': float(turret_height),
            'max_pitch_up_deg': float(getattr(entity, 'max_pitch_up_deg', shooting.get('max_pitch_up_deg', 30.0))),
            'max_pitch_down_deg': float(getattr(entity, 'max_pitch_down_deg', shooting.get('max_pitch_down_deg', 30.0))),
            'auto_aim_max_distance_m': float(shooting['auto_aim_max_distance_m']),
            'auto_aim_max_distance_world': float(self.auto_aim_max_distance),
            'auto_aim_fov_deg': float(shooting['auto_aim_fov_deg']),
            'auto_aim_track_speed_deg_per_sec': float(shooting.get('auto_aim_track_speed_deg_per_sec', 180.0)),
            'current_cooling_rate': float(self.get_current_cooling_rate(entity)),
            'chassis_mode': chassis_mode,
            'gimbal_mode': gimbal_mode,
            'chassis_profile': dict(control_modes['chassis'].get(chassis_mode, {})),
            'gimbal_profile': dict(control_modes['gimbal'].get(gimbal_mode, {})),
        }

    def _chassis_mode_profile(self, entity):
        mode = getattr(entity, 'chassis_mode', 'health_priority')
        profiles = self.rules['control_modes']['chassis']
        return profiles.get(mode, profiles['health_priority'])

    def _gimbal_mode_profile(self, entity):
        mode = getattr(entity, 'gimbal_mode', 'cooling_priority')
        profiles = self.rules['control_modes']['gimbal']
        return profiles.get(mode, profiles['cooling_priority'])

    def get_effective_power_per_shot(self, entity):
        base_cost = float(getattr(entity, 'power_per_shot', self.rules['shooting'].get('power_per_shot', 0.0)))
        chassis_profile = self._chassis_mode_profile(entity)
        return base_cost * float(chassis_profile.get('power_cost_mult', 1.0))

    def _next_shot_would_overheat(self, entity):
        next_shot_heat = float(self._heat_gain_per_shot(entity))
        return float(getattr(entity, 'heat', 0.0)) + next_shot_heat > float(getattr(entity, 'max_heat', 0.0)) + 1e-6

    def get_effective_fire_rate_hz(self, entity):
        if getattr(entity, 'heat_lock_state', 'normal') != 'normal':
            return 0.0
        if self._next_shot_would_overheat(entity):
            return 0.0
        base_rate = float(getattr(entity, 'fire_rate_hz', self.rules['shooting']['fire_rate_hz']))
        chassis_profile = self._chassis_mode_profile(entity)
        gimbal_profile = self._gimbal_mode_profile(entity)
        fire_rate = base_rate
        fire_rate *= float(chassis_profile.get('base_fire_rate_mult', 1.0))
        fire_rate *= float(gimbal_profile.get('base_fire_rate_mult', 1.0))

        max_power = max(float(getattr(entity, 'max_power', 0.0)), 1e-6)
        power_ratio = float(getattr(entity, 'power', 0.0)) / max_power
        if power_ratio < float(chassis_profile.get('min_power_ratio', 0.0)):
            return 0.0

        max_health = max(float(getattr(entity, 'max_health', 0.0)), 1e-6)
        health_ratio = float(getattr(entity, 'health', 0.0)) / max_health
        if health_ratio < float(chassis_profile.get('low_hp_ratio', 0.0)):
            fire_rate *= float(chassis_profile.get('low_hp_fire_rate_mult', 1.0))

        max_heat = max(float(getattr(entity, 'max_heat', 0.0)), 1e-6)
        heat_ratio = float(getattr(entity, 'heat', 0.0)) / max_heat
        soft_heat_ratio = float(gimbal_profile.get('soft_heat_ratio', 1.0))
        if heat_ratio >= soft_heat_ratio:
            progress = min(1.0, max(0.0, (heat_ratio - soft_heat_ratio) / max(1.0 - soft_heat_ratio, 1e-6)))
            min_fire_rate_ratio = float(gimbal_profile.get('min_fire_rate_ratio', 0.25))
            fire_rate *= max(min_fire_rate_ratio, 1.0 - progress * (1.0 - min_fire_rate_ratio))

        return max(0.0, fire_rate)

    def _map_manager(self):
        if self.game_engine is None:
            return None
        return getattr(self.game_engine, 'map_manager', None)

    def _entity_ground_height_m(self, entity):
        map_manager = self._map_manager()
        if map_manager is None:
            return 0.0
        return float(map_manager.get_terrain_height_m(entity.position['x'], entity.position['y']))

    def _entity_base_height_m(self, entity):
        ground_height = self._entity_ground_height_m(entity)
        return max(ground_height, float(getattr(entity, 'position', {}).get('z', ground_height)))

    def _entity_vertical_scale(self, entity):
        return max(1.0, float(getattr(entity, 'vertical_scale_m', 1.0)))

    def _meters_to_world_units(self, meters):
        map_manager = self._map_manager()
        if map_manager is None:
            field_length_m = float(self.config.get('map', {}).get('field_length_m', 28.0))
            field_width_m = float(self.config.get('map', {}).get('field_width_m', 15.0))
            map_width = float(self.config.get('map', {}).get('width', 1576))
            map_height = float(self.config.get('map', {}).get('height', 873))
            pixels_per_meter_x = map_width / max(field_length_m, 1e-6)
            pixels_per_meter_y = map_height / max(field_width_m, 1e-6)
            return float(meters) * ((pixels_per_meter_x + pixels_per_meter_y) * 0.5)
        return float(map_manager.meters_to_world_units(float(meters)))

    def _world_units_to_meters(self, world_units):
        map_manager = self._map_manager()
        if map_manager is None:
            field_length_m = float(self.config.get('map', {}).get('field_length_m', 28.0))
            field_width_m = float(self.config.get('map', {}).get('field_width_m', 15.0))
            map_width = float(self.config.get('map', {}).get('width', 1576))
            map_height = float(self.config.get('map', {}).get('height', 873))
            pixels_per_meter_x = map_width / max(field_length_m, 1e-6)
            pixels_per_meter_y = map_height / max(field_width_m, 1e-6)
            world_units_per_meter = max((pixels_per_meter_x + pixels_per_meter_y) * 0.5, 1e-6)
            return float(world_units) / world_units_per_meter
        world_units_per_meter = max(float(map_manager.meters_to_world_units(1.0)), 1e-6)
        return float(world_units) / world_units_per_meter

    def _projectile_speed_mps(self, ammo_type):
        shooting_rules = self.rules.get('shooting', {})
        if ammo_type == '42mm':
            return float(shooting_rules.get('projectile_speed_42mm_mps', 16.0))
        return float(shooting_rules.get('projectile_speed_17mm_mps', 20.0))

    def _projectile_diameter_m(self, ammo_type):
        shooting_rules = self.rules.get('shooting', {})
        if ammo_type == '42mm':
            return float(shooting_rules.get('projectile_diameter_42mm_m', 0.042))
        return float(shooting_rules.get('projectile_diameter_17mm_m', 0.017))

    def _projectile_drag_coefficient(self, ammo_type):
        shooting_rules = self.rules.get('shooting', {})
        if ammo_type == '42mm':
            return float(shooting_rules.get('projectile_drag_42mm', 0.026))
        return float(shooting_rules.get('projectile_drag_17mm', 0.018))

    def _projectile_gravity_mps2(self):
        return float(self.rules.get('shooting', {}).get('projectile_gravity_mps2', 9.81))

    def _metric_point_from_world(self, point3d):
        return (
            self._world_units_to_meters(float(point3d[0])),
            self._world_units_to_meters(float(point3d[1])),
            float(point3d[2]),
        )

    def _world_point_from_metric(self, point3d):
        return (
            self._meters_to_world_units(float(point3d[0])),
            self._meters_to_world_units(float(point3d[1])),
            float(point3d[2]),
        )

    def _shooter_muzzle_point(self, shooter, pitch_deg=None):
        """Return the world (x,y,z) position of the muzzle (barrel center).

        If pitch_deg is provided, include barrel vertical extension and use
        barrel horizontal component when computing the muzzle position.
        If pitch_deg is None, return the baseline view height without barrel
        vertical offset (used as an initial guess for pitch solving).
        """
        yaw_rad = math.radians(float(getattr(shooter, 'turret_angle', shooter.angle)))

        # offsets and dimensions (meters)
        gimbal_offset_x_m = float(getattr(shooter, 'gimbal_offset_x_m', 0.0))
        gimbal_offset_y_m = float(getattr(shooter, 'gimbal_offset_y_m', 0.0))
        gimbal_length_m = float(getattr(shooter, 'gimbal_length_m', 0.0))
        barrel_length_m = float(getattr(shooter, 'barrel_length_m', 0.0))

        # pitch influences barrel horizontal/vertical components
        pitch_rad = 0.0 if pitch_deg is None else math.radians(float(pitch_deg))
        barrel_horizontal_m = barrel_length_m * max(0.0, math.cos(pitch_rad))

        # forward distance from entity origin to muzzle (meters), use half gimbal length as turret center
        muzzle_forward_m = gimbal_offset_x_m + gimbal_length_m * 0.5 + barrel_horizontal_m
        local_forward = self._meters_to_world_units(muzzle_forward_m)
        local_right = self._meters_to_world_units(gimbal_offset_y_m)

        world_x = float(shooter.position['x']) + math.cos(yaw_rad) * local_forward - math.sin(yaw_rad) * local_right
        world_y = float(shooter.position['y']) + math.sin(yaw_rad) * local_forward + math.cos(yaw_rad) * local_right

        anchor_height = self._shooter_view_height_m(shooter)
        if pitch_deg is None:
            return (world_x, world_y, anchor_height)

        # apply barrel vertical offset (meters), scale by entity vertical scale
        barrel_vertical_m = barrel_length_m * math.sin(pitch_rad) * self._entity_vertical_scale(shooter)
        muzzle_height = anchor_height + barrel_vertical_m
        return (world_x, world_y, muzzle_height)

    def _shooter_view_height_m(self, shooter):
        shooting_rules = self.rules.get('shooting', {})
        view_height = float(getattr(shooter, 'gimbal_height_m', shooting_rules.get('turret_axis_height_m', shooting_rules.get('camera_height_m', 0.45))))
        view_height *= self._entity_vertical_scale(shooter)
        return self._entity_base_height_m(shooter) + view_height

    def _target_armor_height_m(self, target):
        vertical_scale = self._entity_vertical_scale(target)
        armor_height = (float(getattr(target, 'body_clearance_m', 0.10)) + float(getattr(target, 'body_height_m', 0.18)) * 0.55) * vertical_scale
        if getattr(target, 'type', None) == 'outpost':
            armor_height = max(armor_height, 0.45)
        elif getattr(target, 'type', None) == 'base':
            armor_height = max(armor_height, 0.60)
        return self._entity_base_height_m(target) + armor_height

    def _estimate_local_terrain_pitch_rad(self, map_manager, start_x, start_y, end_x, end_y):
        distance = math.hypot(end_x - start_x, end_y - start_y)
        if distance <= 1e-6:
            return 0.0

        sample_distance = max(
            map_manager.meters_to_world_units(self.rules.get('shooting', {}).get('los_pitch_probe_distance_m', 0.45)),
            1.0,
        )
        sample_distance = min(sample_distance, max(distance * 0.45, 1.0))
        if sample_distance <= 1e-6:
            return 0.0

        dir_x = (end_x - start_x) / distance
        dir_y = (end_y - start_y) / distance
        probe_x = start_x + dir_x * sample_distance
        probe_y = start_y + dir_y * sample_distance
        start_height = float(map_manager.get_terrain_height_m(start_x, start_y))
        probe_height = float(map_manager.get_terrain_height_m(probe_x, probe_y))
        meters_per_world_unit = max(float(map_manager.meters_to_world_units(1.0)), 1e-6)
        sample_distance_m = sample_distance / meters_per_world_unit
        return math.atan2(probe_height - start_height, max(sample_distance_m, 1e-6))

    def _estimate_target_side_pitch_rad(self, map_manager, start_x, start_y, end_x, end_y):
        # Probe from the target back toward the shooter to catch see-saw terrain near the target.
        return self._estimate_local_terrain_pitch_rad(map_manager, end_x, end_y, start_x, start_y)

    def is_base_shielded(self, base_entity):
        if base_entity is None or getattr(base_entity, 'type', None) != 'base':
            return False
        if getattr(base_entity, 'invincible_timer', 0.0) > 0 or getattr(base_entity, 'dynamic_invincible', False):
            return True
        own_team = getattr(base_entity, 'team', None)
        if own_team not in {'red', 'blue'}:
            return False
        own_outpost = self._find_entity(getattr(self, '_latest_entities', []), own_team, 'outpost')
        return own_outpost is not None and own_outpost.is_alive()

    def _normalize_angle_diff(self, angle_diff):
        while angle_diff > 180:
            angle_diff -= 360
        while angle_diff < -180:
            angle_diff += 360
        return angle_diff

    def _clamp01(self, value):
        return max(0.0, min(1.0, float(value)))

    def _smoothstep01(self, value):
        clamped = self._clamp01(value)
        return clamped * clamped * (3.0 - 2.0 * clamped)

    def _desired_turret_angle(self, shooter, target):
        dx = target.position['x'] - shooter.position['x']
        dy = target.position['y'] - shooter.position['y']
        return math.degrees(math.atan2(dy, dx))

    def _simulate_pitch_error_m(self, shooter, start_point, target_point, pitch_deg):
        horizontal_distance_m = self._world_units_to_meters(
            math.hypot(float(target_point[0]) - float(start_point[0]), float(target_point[1]) - float(start_point[1]))
        )
        if horizontal_distance_m <= 1e-6:
            return float(target_point[2]) - float(start_point[2])
        ammo_type = getattr(shooter, 'ammo_type', '17mm')
        speed_mps = max(1e-6, self._projectile_speed_mps(ammo_type))
        drag = max(0.0, self._projectile_drag_coefficient(ammo_type))
        gravity = self._projectile_gravity_mps2()
        dt = max(0.002, min(0.02, float(self.rules.get('shooting', {}).get('projectile_simulation_dt_sec', 0.01))))
        pitch_rad = math.radians(float(pitch_deg))
        horizontal_speed = math.cos(pitch_rad) * speed_mps
        vertical_speed = math.sin(pitch_rad) * speed_mps
        horizontal_pos = 0.0
        vertical_pos = float(start_point[2])
        target_height = float(target_point[2])
        max_time = max(1.0, horizontal_distance_m / max(horizontal_speed, 1e-6) * 2.5)
        elapsed = 0.0
        while elapsed < max_time:
            total_speed = math.hypot(horizontal_speed, vertical_speed)
            drag_accel_x = -drag * total_speed * horizontal_speed
            drag_accel_z = -gravity - drag * total_speed * vertical_speed
            next_horizontal = horizontal_pos + horizontal_speed * dt + 0.5 * drag_accel_x * dt * dt
            next_vertical = vertical_pos + vertical_speed * dt + 0.5 * drag_accel_z * dt * dt
            if next_horizontal >= horizontal_distance_m:
                denom = max(next_horizontal - horizontal_pos, 1e-6)
                ratio = (horizontal_distance_m - horizontal_pos) / denom
                intercept_height = vertical_pos + (next_vertical - vertical_pos) * ratio
                return intercept_height - target_height
            horizontal_speed += drag_accel_x * dt
            vertical_speed += drag_accel_z * dt
            horizontal_pos = next_horizontal
            vertical_pos = next_vertical
            if horizontal_speed <= 1e-6 and horizontal_pos < horizontal_distance_m:
                break
            elapsed += dt
        return vertical_pos - target_height

    def _solve_ballistic_pitch_deg(self, shooter, start_point, target_point, preferred_pitch_deg=0.0):
        horizontal_distance = math.hypot(float(target_point[0]) - float(start_point[0]), float(target_point[1]) - float(start_point[1]))
        if horizontal_distance <= 1e-6:
            return self._normalize_angle_diff(float(preferred_pitch_deg))
        max_up = float(getattr(shooter, 'max_pitch_up_deg', self.rules.get('shooting', {}).get('max_pitch_up_deg', 30.0)))
        max_down = float(getattr(shooter, 'max_pitch_down_deg', self.rules.get('shooting', {}).get('max_pitch_down_deg', 30.0)))
        low = -max_down
        high = max_up
        best_pitch = max(low, min(high, float(preferred_pitch_deg)))
        best_error = abs(self._simulate_pitch_error_m(shooter, start_point, target_point, best_pitch))
        coarse_steps = 48
        for index in range(coarse_steps + 1):
            candidate = low + (high - low) * (index / max(coarse_steps, 1))
            error = self._simulate_pitch_error_m(shooter, start_point, target_point, candidate)
            if abs(error) < best_error:
                best_pitch = candidate
                best_error = abs(error)
        window = max(2.0, (high - low) / max(coarse_steps, 1))
        for _ in range(3):
            sample_low = max(low, best_pitch - window)
            sample_high = min(high, best_pitch + window)
            for index in range(8):
                candidate = sample_low + (sample_high - sample_low) * (index / 7.0)
                error = self._simulate_pitch_error_m(shooter, start_point, target_point, candidate)
                if abs(error) < best_error:
                    best_pitch = candidate
                    best_error = abs(error)
            window *= 0.45
        return max(low, min(high, float(best_pitch)))

    def get_aim_angles_to_point(self, shooter, point_x, point_y, point_z):
        target_point = (float(point_x), float(point_y), float(point_z))
        preferred_pitch = float(getattr(shooter, 'gimbal_pitch_deg', 0.0))

        # Start with a reasonable muzzle point using the preferred pitch,
        # then iterate a couple times because muzzle position depends on pitch.
        start_point = self._shooter_muzzle_point(shooter, pitch_deg=preferred_pitch)
        yaw_deg = math.degrees(math.atan2(target_point[1] - start_point[1], target_point[0] - start_point[0]))
        pitch_deg = preferred_pitch
        for _ in range(2):
            pitch_deg = self._solve_ballistic_pitch_deg(
                shooter,
                start_point,
                target_point,
                preferred_pitch_deg=preferred_pitch,
            )
            start_point = self._shooter_muzzle_point(shooter, pitch_deg=pitch_deg)
            yaw_deg = math.degrees(math.atan2(target_point[1] - start_point[1], target_point[0] - start_point[0]))
        return yaw_deg, pitch_deg

    def get_entity_armor_plate_targets(self, target):
        cache_key = self._entity_pose_cache_key(target)
        cached_targets = self._armor_target_cache.get(cache_key)
        if cached_targets is not None:
            return [dict(item) for item in cached_targets]
        base_height = self._entity_base_height_m(target)
        vertical_scale = self._entity_vertical_scale(target)
        half_length = self._meters_to_world_units(float(getattr(target, 'body_length_m', getattr(target, 'body_size_m', 0.42))) * 0.5)
        half_width = self._meters_to_world_units(float(getattr(target, 'body_width_m', getattr(target, 'body_size_m', 0.42))) * 0.5)
        plate_gap = self._meters_to_world_units(float(getattr(target, 'armor_plate_gap_m', 0.02)))
        plate_center_height = base_height + (float(getattr(target, 'body_clearance_m', 0.10)) + float(getattr(target, 'body_height_m', 0.18)) * 0.55) * vertical_scale
        yaw_rad = math.radians(float(getattr(target, 'angle', 0.0)))
        heading_x = math.cos(yaw_rad)
        heading_y = math.sin(yaw_rad)
        side_x = -heading_y
        side_y = heading_x
        specs = (
            ('front', heading_x * (half_length + plate_gap), heading_y * (half_length + plate_gap)),
            ('rear', -heading_x * (half_length + plate_gap), -heading_y * (half_length + plate_gap)),
            ('left', side_x * (half_width + plate_gap), side_y * (half_width + plate_gap)),
            ('right', -side_x * (half_width + plate_gap), -side_y * (half_width + plate_gap)),
        )
        plates = []
        for plate_id, offset_x, offset_y in specs:
            plates.append({
                'id': plate_id,
                'x': float(target.position['x']) + offset_x,
                'y': float(target.position['y']) + offset_y,
                'z': plate_center_height,
            })
        self._armor_target_cache[cache_key] = tuple(dict(item) for item in plates)
        return [dict(item) for item in plates]

    def _projectile_target_broad_radius_world(self, target, hit_radius):
        half_length = self._meters_to_world_units(float(getattr(target, 'body_length_m', getattr(target, 'body_size_m', 0.42))) * 0.5)
        half_width = self._meters_to_world_units(float(getattr(target, 'body_width_m', getattr(target, 'body_size_m', 0.42))) * 0.5)
        plate_gap = self._meters_to_world_units(float(getattr(target, 'armor_plate_gap_m', 0.02)))
        return math.hypot(half_length + plate_gap + hit_radius, half_width + plate_gap + hit_radius)

    def _stabilize_hit_probability(self, shooter, target, raw_probability, *, field_name, target_field_name, time_field_name):
        now = float(getattr(self, 'game_time', 0.0))
        target_id = getattr(target, 'id', None) if target is not None else None
        previous_probability = float(getattr(shooter, field_name, 0.0))
        previous_target_id = getattr(shooter, target_field_name, None)
        previous_updated_at = float(getattr(shooter, time_field_name, -1e9))
        hold_duration = float(self.rules['shooting'].get('hit_probability_hold_sec', 0.18))
        zero_decay = float(self.rules['shooting'].get('hit_probability_zero_decay', 0.78))
        rise_blend = float(self.rules['shooting'].get('hit_probability_rise_blend', 0.55))
        fall_blend = float(self.rules['shooting'].get('hit_probability_fall_blend', 0.28))

        if target_id is None:
            stabilized = 0.0
        elif previous_target_id != target_id:
            stabilized = float(raw_probability)
        elif raw_probability > previous_probability:
            stabilized = previous_probability + (float(raw_probability) - previous_probability) * rise_blend
        elif raw_probability > 0.0:
            stabilized = previous_probability + (float(raw_probability) - previous_probability) * fall_blend
        elif now - previous_updated_at <= hold_duration:
            stabilized = previous_probability * zero_decay
        else:
            stabilized = 0.0

        stabilized = self._clamp01(stabilized)
        setattr(shooter, field_name, stabilized)
        setattr(shooter, target_field_name, target_id)
        setattr(shooter, time_field_name, now)
        return stabilized

    def has_line_of_sight(self, shooter, target):
        return self.has_line_of_sight_to_point(
            shooter,
            float(target.position['x']),
            float(target.position['y']),
            self._target_armor_height_m(target),
            cache_key=self._line_of_sight_cache_key(shooter, target),
        )

    def has_line_of_sight_to_point(self, shooter, end_x, end_y, target_height_m, cache_key=None):
        map_manager = self._map_manager()
        if map_manager is None:
            return True

        if cache_key is not None:
            cached_result = self._line_of_sight_cache.get(cache_key)
            if cached_result is not None:
                return cached_result

        start_x = float(shooter.position['x'])
        start_y = float(shooter.position['y'])
        distance = math.hypot(end_x - start_x, end_y - start_y)
        if distance <= 1e-6:
            if cache_key is not None:
                self._line_of_sight_cache[cache_key] = True
            return True

        shooter_height = self._shooter_view_height_m(shooter)
        target_height = float(target_height_m)

        meters_per_world_unit = max(float(map_manager.meters_to_world_units(1.0)), 1e-6)
        distance_m = distance / meters_per_world_unit
        target_pitch_rad = math.atan2(target_height - shooter_height, max(distance_m, 1e-6))
        target_pitch_deg = math.degrees(target_pitch_rad)
        max_pitch_up_deg = float(self.rules['shooting'].get('max_pitch_up_deg', 30.0))
        max_pitch_down_deg = float(self.rules['shooting'].get('max_pitch_down_deg', 30.0))
        if target_pitch_deg > max_pitch_up_deg or target_pitch_deg < -max_pitch_down_deg:
            if cache_key is not None:
                self._line_of_sight_cache[cache_key] = False
            return False

        pitch_margin_deg = float(self.rules['shooting'].get('los_pitch_margin_deg', 0.8))
        pitch_margin_rad = math.radians(max(0.0, pitch_margin_deg))
        terrain_pitch_rad = self._estimate_local_terrain_pitch_rad(map_manager, start_x, start_y, end_x, end_y)
        reverse_pitch_rad = self._estimate_target_side_pitch_rad(map_manager, start_x, start_y, end_x, end_y)
        terrain_pitch_margin_deg = float(self.rules['shooting'].get('los_terrain_pitch_margin_deg', 1.2))
        terrain_pitch_margin_rad = math.radians(max(0.0, terrain_pitch_margin_deg))

        if target_pitch_rad + terrain_pitch_margin_rad < terrain_pitch_rad:
            if cache_key is not None:
                self._line_of_sight_cache[cache_key] = False
            return False
        if target_pitch_rad + terrain_pitch_margin_rad < reverse_pitch_rad:
            if cache_key is not None:
                self._line_of_sight_cache[cache_key] = False
            return False

        line_clear = map_manager.is_vision_line_clear((start_x, start_y), (end_x, end_y), include_start=False, include_end=False)
        if cache_key is not None:
            self._line_of_sight_cache[cache_key] = bool(line_clear)
        return bool(line_clear)

    def evaluate_auto_aim_target(self, shooter, target, distance=None, require_fov=True):
        if target is None or not target.is_alive() or target.team == shooter.team:
            return {
                'valid': False,
                'distance': float('inf'),
                'in_range': False,
                'line_of_sight': False,
                'angle_diff': 180.0,
                'within_fov': False,
                'can_track': False,
                'can_auto_aim': False,
            }

        if (
            getattr(shooter, 'type', None) == 'robot'
            and getattr(shooter, 'robot_type', '') == '英雄'
            and getattr(target, 'type', None) in {'outpost', 'base'}
            and not bool(getattr(shooter, 'trapezoid_highground_active', False))
        ):
            return {
                'valid': True,
                'distance': float('inf') if distance is None else float(distance),
                'in_range': False,
                'line_of_sight': False,
                'angle_diff': 180.0,
                'within_fov': False,
                'can_track': False,
                'can_auto_aim': False,
            }

        if distance is None:
            distance = math.hypot(target.position['x'] - shooter.position['x'], target.position['y'] - shooter.position['y'])
        cache_key = self._auto_aim_cache_key(shooter, target, distance, require_fov)
        cached_assessment = self._auto_aim_eval_cache.get(cache_key)
        if cached_assessment is not None:
            return cached_assessment

        max_distance = self.get_range(shooter.type)
        in_range = distance <= max_distance
        line_of_sight = in_range and self.has_line_of_sight(shooter, target)
        desired_angle = self._desired_turret_angle(shooter, target)
        current_angle = getattr(shooter, 'turret_angle', shooter.angle)
        angle_diff = self._normalize_angle_diff(desired_angle - current_angle)
        half_fov = float(self.rules['shooting'].get('auto_aim_fov_deg', 50.0)) / 2.0
        within_fov = abs(angle_diff) <= half_fov
        can_track = in_range and line_of_sight
        can_auto_aim = can_track and (within_fov if require_fov else True)
        assessment = {
            'valid': True,
            'distance': distance,
            'in_range': in_range,
            'line_of_sight': line_of_sight,
            'desired_angle': desired_angle,
            'angle_diff': angle_diff,
            'within_fov': within_fov,
            'can_track': can_track,
            'can_auto_aim': can_auto_aim,
        }
        self._auto_aim_eval_cache[cache_key] = assessment
        return assessment

    def can_auto_aim_target(self, shooter, target, distance=None):
        return self.evaluate_auto_aim_target(shooter, target, distance=distance, require_fov=True).get('can_auto_aim', False)

    def can_track_target(self, shooter, target, distance=None):
        return self.evaluate_auto_aim_target(shooter, target, distance=distance, require_fov=False).get('can_track', False)

    def update(self, entities, map_manager=None, dt=0.02, game_time=0.0, game_duration=None):
        self.dt = dt
        self.game_time = game_time
        if game_duration is not None:
            self.game_duration = float(game_duration)
        self._latest_entities = list(entities)
        self._update_projectile_traces(dt)
        for entity in entities:
            if entity.type in {'robot', 'sentry'}:
                entity.auto_aim_limit = self.auto_aim_max_distance
        self._update_entity_timers(entities, dt)
        self._update_gold(entities, dt)
        self._update_sentry_posture_and_heat(entities, dt)
        self._update_heat_mechanism(entities, dt)

        if map_manager is not None:
            self._facility_update_accumulator += dt
            if self._facility_update_accumulator + 1e-9 >= self._facility_update_interval:
                facility_dt = self._facility_update_accumulator
                self._facility_update_accumulator = 0.0
                self._update_occupied_facilities(entities, map_manager)
                self._update_facility_effects(entities, map_manager, facility_dt)
                self._update_energy_mechanism_control(entities, map_manager, facility_dt)
                self._update_radar_marks(entities, map_manager, facility_dt)

        self.check_damage(entities)
        self.check_health(entities)

        if game_duration and game_time >= game_duration:
            self.stage = 'ended'
            self.game_over = True
            self.winner = self._winner_by_base_health(entities)
    def _winner_by_base_health(self, entities):
        red_base = self._find_entity(entities, 'red', 'base')
        blue_base = self._find_entity(entities, 'blue', 'base')
        red_hp = red_base.health if red_base else 0
        blue_hp = blue_base.health if blue_base else 0
        if red_hp > blue_hp:
            return 'red'
        if blue_hp > red_hp:
            return 'blue'
        return 'draw'

    def _find_entity(self, entities, team, entity_type):
        for entity in entities:
            if entity.team == team and entity.type == entity_type:
                return entity
        return None

    def _update_entity_timers(self, entities, dt):
        for entity in entities:
            refreshed_attackers = []
            for attacker in list(getattr(entity, 'recent_attackers', [])):
                attacker['time_left'] = max(0.0, float(attacker.get('time_left', 0.0)) - dt)
                if attacker['time_left'] > 0.0:
                    refreshed_attackers.append(attacker)
            entity.recent_attackers = refreshed_attackers
            self._expire_buff_path_progress(entity)
            entity.shot_cooldown = max(0.0, getattr(entity, 'shot_cooldown', 0.0) - dt)
            entity.overheat_lock_timer = max(0.0, getattr(entity, 'overheat_lock_timer', 0.0) - dt)
            entity.invincible_timer = max(0.0, getattr(entity, 'invincible_timer', 0.0) - dt)
            entity.weak_timer = max(0.0, getattr(entity, 'weak_timer', 0.0) - dt)
            if getattr(entity, 'respawn_invalid_timer', 0.0) > 0.0:
                entity.respawn_invalid_timer = max(0.0, float(getattr(entity, 'respawn_invalid_timer', 0.0)) - dt)
                entity.respawn_invalid_elapsed = float(getattr(entity, 'respawn_invalid_elapsed', 0.0)) + dt
            entity.terrain_buff_timer = max(0.0, getattr(entity, 'terrain_buff_timer', 0.0) - dt)
            entity.supply_cooldown = max(0.0, getattr(entity, 'supply_cooldown', 0.0) - dt)
            entity.exchange_cooldown = max(0.0, getattr(entity, 'exchange_cooldown', 0.0) - dt)
            entity.respawn_recovery_timer = max(0.0, getattr(entity, 'respawn_recovery_timer', 0.0) - dt)
            entity.role_purchase_cooldown = max(0.0, getattr(entity, 'role_purchase_cooldown', 0.0) - dt)
            entity.energy_small_buff_timer = max(0.0, getattr(entity, 'energy_small_buff_timer', 0.0) - dt)
            entity.energy_large_buff_timer = max(0.0, getattr(entity, 'energy_large_buff_timer', 0.0) - dt)
            entity.timed_buffs = {
                key: max(0.0, float(value) - dt)
                for key, value in dict(getattr(entity, 'timed_buffs', {})).items()
                if max(0.0, float(value) - dt) > 0.0
            }
            entity.buff_cooldowns = {
                key: max(0.0, float(value) - dt)
                for key, value in dict(getattr(entity, 'buff_cooldowns', {})).items()
                if max(0.0, float(value) - dt) > 0.0
            }

            self._process_pending_rule_events(entity)

            if entity.state in {'respawning', 'destroyed'} and getattr(entity, 'respawn_timer', 0.0) > 0:
                progress_rate = float(self.rules['respawn'].get('fast_respawn_rate', 4.0)) if self._is_fast_respawn_context(entity) else 1.0
                entity.respawn_timer = max(0.0, entity.respawn_timer - dt * progress_rate)
                if entity.respawn_timer <= 0:
                    self._respawn_entity(entity)

            if entity.state not in {'respawning', 'destroyed'}:
                if entity.invincible_timer > 0:
                    entity.state = 'invincible'
                elif self._is_respawn_weak(entity):
                    entity.state = 'weak'
                elif entity.state in {'invincible', 'weak'}:
                    entity.state = 'idle'

    def _update_gold(self, entities, dt):
        for entity in entities:
            if entity.type in {'robot', 'sentry'}:
                entity.gold = self.team_gold[entity.team]

    def _primary_ammo_key(self, entity):
        ammo_type = getattr(entity, 'ammo_type', None)
        if ammo_type == '42mm':
            return 'allowed_ammo_42mm'
        if ammo_type == '17mm':
            return 'allowed_ammo_17mm'
        return None

    def _sync_legacy_ammo(self, entity):
        ammo_key = self._primary_ammo_key(entity)
        entity.ammo = int(getattr(entity, ammo_key, 0)) if ammo_key else 0

    def _available_ammo(self, entity):
        ammo_key = self._primary_ammo_key(entity)
        if ammo_key is None:
            return 0
        return int(getattr(entity, ammo_key, 0))

    def _add_allowed_ammo(self, entity, amount, ammo_type=None):
        ammo_kind = ammo_type or getattr(entity, 'ammo_type', None)
        if ammo_kind == '42mm':
            entity.allowed_ammo_42mm = max(0, int(getattr(entity, 'allowed_ammo_42mm', 0) + amount))
        elif ammo_kind == '17mm':
            entity.allowed_ammo_17mm = max(0, int(getattr(entity, 'allowed_ammo_17mm', 0) + amount))
        self._sync_legacy_ammo(entity)

    def _consume_allowed_ammo(self, entity, amount):
        ammo_key = self._primary_ammo_key(entity)
        if ammo_key is None:
            return 0
        current = int(getattr(entity, ammo_key, 0))
        consumed = min(current, int(amount))
        setattr(entity, ammo_key, current - consumed)
        self._sync_legacy_ammo(entity)
        return consumed

    def entity_has_barrel(self, entity):
        if entity.type == 'sentry':
            return True
        if entity.type != 'robot':
            return False
        return getattr(entity, 'robot_type', '') != '工程'

    def _can_use_hero_deployment_fire(self, shooter):
        return (
            getattr(shooter, 'type', None) == 'robot'
            and getattr(shooter, 'robot_type', '') == '英雄'
            and bool(getattr(shooter, 'hero_deployment_active', False))
            and bool(getattr(shooter, 'hero_deployment_zone_active', False))
            and bool(getattr(shooter, 'trapezoid_highground_active', False))
            and self._available_ammo(shooter) > 0
        )

    def _can_use_hero_structure_lob_fire(self, shooter, target=None):
        if getattr(shooter, 'type', None) != 'robot' or getattr(shooter, 'robot_type', '') != '英雄':
            return False
        if self._available_ammo(shooter) <= 0:
            return False
        if self._can_use_hero_deployment_fire(shooter):
            return target is None or getattr(target, 'type', None) in {'outpost', 'base'}
        if not bool(getattr(shooter, 'hero_structure_lob_active', False)):
            return False
        desired_target_type = str(getattr(shooter, 'hero_structure_lob_target_type', '') or '').strip()
        if desired_target_type not in {'outpost', 'base'}:
            return False
        if target is None:
            return True
        if not target.is_alive() or target.team == shooter.team or target.type != desired_target_type:
            return False
        if target.type == 'base' and self.is_base_shielded(target):
            return False
        return True

    def _resolve_hero_deployment_target(self, shooter, entities):
        if not self._can_use_hero_structure_lob_fire(shooter):
            return None
        deployment_rules = self.rules.get('shooting', {}).get('hero_deployment_structure_fire', {})
        allowed_types = tuple(deployment_rules.get('target_types', ['outpost', 'base']))
        desired_target_type = str(getattr(shooter, 'hero_structure_lob_target_type', '') or '').strip()
        candidates = []
        for entity in entities:
            if entity.team == shooter.team or not entity.is_alive() or entity.type not in allowed_types:
                continue
            if desired_target_type in {'outpost', 'base'} and entity.type != desired_target_type:
                continue
            if entity.type == 'base' and self.is_base_shielded(entity):
                continue
            if not self.has_line_of_sight(shooter, entity):
                continue
            if desired_target_type in {'outpost', 'base'}:
                priority = 0
            else:
                priority = 0 if entity.type == 'outpost' else 1
            distance = math.hypot(entity.position['x'] - shooter.position['x'], entity.position['y'] - shooter.position['y'])
            candidates.append((priority, distance, entity))
        if not candidates:
            return None
        candidates.sort(key=lambda item: (item[0], item[1]))
        return candidates[0][2]

    def is_out_of_combat(self, entity):
        disengage_duration = float(self.rules.get('combat', {}).get('disengage_duration', 6.0))
        return (self.game_time - float(getattr(entity, 'last_combat_time', -1e9))) >= disengage_duration

    def _mark_in_combat(self, entity):
        entity.last_combat_time = self.game_time

    def _track_recent_attack(self, target, shooter, damage):
        if target is None or shooter is None:
            return
        retained = []
        for attacker in list(getattr(target, 'recent_attackers', [])):
            if float(attacker.get('time_left', 0.0)) > 0.0 and attacker.get('id') != getattr(shooter, 'id', None):
                retained.append(attacker)
        retained.append({
            'id': getattr(shooter, 'id', None),
            'team': getattr(shooter, 'team', None),
            'type': getattr(shooter, 'type', None),
            'robot_type': getattr(shooter, 'robot_type', None),
            'damage': float(damage),
            'time_left': 2.2,
        })
        target.recent_attackers = retained[-6:]

    def _projectile_speed_world_units(self, ammo_type):
        return self._meters_to_world_units(self._projectile_speed_mps(ammo_type))

    def _spawn_projectile_trace(self, shooter, target, trace_payload=None):
        spawn_projectile_trace_impl(self, shooter, target, trace_payload=trace_payload)

    def _point_to_segment_distance_3d(self, point, start, end):
        return point_to_segment_distance_3d_impl(self, point, start, end)

    def _projectile_collision_height_m(self, sample):
        return projectile_collision_height_m_impl(self, sample)

    def _projectile_hits_obstacle(self, point3d):
        return projectile_hits_obstacle_impl(self, point3d)

    def _reflect_projectile_direction(self, previous_point, direction, step_length):
        return reflect_projectile_direction_impl(self, previous_point, direction, step_length)

    def _find_projectile_hit_target(self, shooter, start_point, end_point, entities, preferred_target=None):
        return find_projectile_hit_target_impl(self, shooter, start_point, end_point, entities, preferred_target=preferred_target)

    def _build_projectile_trace_payload(self, shooter, path_points, speed_scale=1.0):
        return build_projectile_trace_payload_impl(self, shooter, path_points, speed_scale=speed_scale)

    def _resolve_projectile_aim_point(self, shooter, target):
        return resolve_projectile_aim_point_impl(self, shooter, target)

    def _find_projectile_hit_target_metric_segment(self, shooter, start_point_m, end_point_m, entities, preferred_target=None):
        return find_projectile_hit_target_metric_segment_impl(self, shooter, start_point_m, end_point_m, entities, preferred_target=preferred_target)

    def _simulate_ballistic_projectile(self, shooter, entities, target=None, aim_point=None, allow_ricochet=False):
        return simulate_ballistic_projectile_impl(self, shooter, entities, target=target, aim_point=aim_point, allow_ricochet=allow_ricochet)

    def _simulate_player_projectile(self, shooter, entities, target=None, aim_point=None, allow_ricochet=False):
        return simulate_player_projectile_impl(self, shooter, entities, target=target, aim_point=aim_point, allow_ricochet=allow_ricochet)

    def _update_projectile_traces(self, dt):
        update_projectile_traces_impl(self, dt)

    def _queue_pending_rule_event(self, entity, event_type, delay, payload):
        queued_event = {
            'type': event_type,
            'time_left': float(delay),
            'payload': deepcopy(payload),
        }
        entity.pending_rule_events.append(queued_event)
        game_engine = getattr(self, 'game_engine', None)
        message_bus = getattr(game_engine, 'message_bus', None) if game_engine is not None else None
        if message_bus is not None:
            message_bus.publish(
                'rule_event_queued',
                {
                    'entity_id': getattr(entity, 'id', ''),
                    'event_type': event_type,
                    'delay_sec': float(delay),
                    'payload': deepcopy(payload),
                },
            )

    def _process_pending_rule_events(self, entity):
        events = []
        for event in list(getattr(entity, 'pending_rule_events', [])):
            event['time_left'] = max(0.0, float(event.get('time_left', 0.0)) - self.dt)
            if event['time_left'] > 0.0:
                events.append(event)
                continue
            payload = event.get('payload', {})
            game_engine = getattr(self, 'game_engine', None)
            message_bus = getattr(game_engine, 'message_bus', None) if game_engine is not None else None
            if message_bus is not None:
                message_bus.publish(
                    'rule_event_dispatch',
                    {
                        'entity_id': getattr(entity, 'id', ''),
                        'event_type': event.get('type'),
                        'payload': deepcopy(payload),
                    },
                )
            if event.get('type') == 'remote_ammo':
                self._add_allowed_ammo(entity, int(payload.get('amount', 0)), payload.get('ammo_type'))
            elif event.get('type') == 'remote_hp':
                entity.heal(float(payload.get('amount', 0.0)))
        entity.pending_rule_events = events

    def _supply_claimable_ammo(self, entity):
        return supply_claimable_ammo_impl(self, entity)

    def _supply_ammo_gain(self, entity):
        return supply_ammo_gain_impl(self, entity)

    def _claim_supply_ammo(self, entity):
        return claim_supply_ammo_impl(self, entity)

    def _mining_duration(self, exchange=False):
        return mining_duration_impl(self, exchange=exchange)

    def _reset_dynamic_effects(self, entity):
        reset_dynamic_effects_impl(self, entity)

    def _make_energy_team_state(self):
        return make_energy_team_state_impl(self)

    def get_energy_mechanism_snapshot(self, team):
        return get_energy_mechanism_snapshot_impl(self, team)

    def _energy_virtual_hits_per_sec(self, entity):
        return energy_virtual_hits_per_sec_impl(self, entity)

    def _energy_large_reward(self, hit_count):
        return energy_large_reward_impl(self, hit_count)

    def _grant_small_energy_buff(self, entities, team):
        grant_small_energy_buff_impl(self, entities, team)

    def _grant_large_energy_buff(self, entities, team, hit_count):
        grant_large_energy_buff_impl(self, entities, team, hit_count)

    def _clear_negative_states(self, entity):
        clear_negative_states_impl(self, entity)

    def _is_respawn_weak(self, entity):
        return is_respawn_weak_impl(self, entity)

    def _respawn_safe_zone_reached(self, entity, regions):
        return respawn_safe_zone_reached_impl(self, entity, regions)

    def _is_fast_respawn_context(self, entity):
        return is_fast_respawn_context_impl(self, entity)

    def _calculate_respawn_read_duration(self, entity):
        return calculate_respawn_read_duration_impl(self, entity)

    def _entity_role_key(self, entity):
        if entity.type == 'sentry':
            return 'sentry'
        role_map = {
            '英雄': 'hero',
            '工程': 'engineer',
            '步兵': 'infantry',
        }
        return role_map.get(getattr(entity, 'robot_type', ''), entity.type)

    def _paired_buff_timeout_by_key(self, pair_key):
        return paired_buff_timeout_by_key_impl(self, pair_key)

    def _expire_buff_path_progress(self, entity):
        expire_buff_path_progress_impl(self, entity)

    def _terrain_access_allowed(self, entity, facility):
        return terrain_access_allowed_impl(self, entity, facility)

    def _energy_front_descriptor(self, facility):
        return energy_front_descriptor_impl(self, facility)

    def _energy_activation_anchor(self, facility):
        return energy_activation_anchor_impl(self, facility)

    def _team_energy_anchor(self, team, facility):
        return team_energy_anchor_impl(self, team, facility)

    def _is_valid_energy_activator(self, entity, facility):
        return is_valid_energy_activator_impl(self, entity, facility)

    def _update_energy_mechanism_control(self, entities, map_manager, dt):
        update_energy_mechanism_control_impl(self, entities, map_manager, dt)

    def _structure_alive(self, team, entity_type):
        return structure_alive_impl(self, team, entity_type)

    def _region_has_enemy_occupant(self, region, entity):
        return region_has_enemy_occupant_impl(self, region, entity)

    def _region_contains_point(self, region, x, y):
        return region_contains_point_impl(self, region, x, y)

    def _buff_access_allowed(self, entity, region, buff_rules):
        return buff_access_allowed_impl(self, entity, region, buff_rules)

    def _handle_paired_buff_region(self, entity, region, buff_rules):
        return handle_paired_buff_region_impl(self, entity, region, buff_rules)

    def _handle_mining_zone(self, entity, region, dt):
        handle_mining_zone_impl(self, entity, region, dt)

    def _handle_exchange_zone(self, entity, region, dt):
        handle_exchange_zone_impl(self, entity, region, dt)

    def _minerals_per_trip(self):
        return minerals_per_trip_impl(self)

    def _try_purchase_role_ammo(self, entity):
        return try_purchase_role_ammo_impl(self, entity)

    def is_in_team_supply_zone(self, entity, map_manager=None):
        return is_in_team_supply_zone_impl(self, entity, map_manager=map_manager)

    def purchase_manual_role_ammo(self, entity, amount, map_manager=None):
        return purchase_manual_role_ammo_impl(self, entity, amount, map_manager=map_manager)

    def _apply_buff_region(self, entity, region, dt, active_regions=None):
        apply_buff_region_impl(self, entity, region, dt, active_regions=active_regions)

    def _resolve_posture_effect(self, sentry):
        return resolve_posture_effect_impl(self, sentry)

    def _update_sentry_posture_and_heat(self, entities, dt):
        update_sentry_posture_and_heat_impl(self, entities, dt)

    def _update_heat_mechanism(self, entities, dt):
        update_heat_mechanism_impl(self, entities, dt)

    def _update_occupied_facilities(self, entities, map_manager):
        update_occupied_facilities_impl(self, entities, map_manager)

    def _update_facility_effects(self, entities, map_manager, dt):
        update_facility_effects_impl(self, entities, map_manager, dt)

    def _apply_dead_zone_penalty(self, entity):
        apply_dead_zone_penalty_impl(self, entity)

    def _update_traversal_progress(self, entity, facility, dt):
        update_traversal_progress_impl(self, entity, facility, dt)

    def _finish_traversal_if_needed(self, entity):
        finish_traversal_if_needed_impl(self, entity)

    def _update_radar_marks(self, entities, map_manager, dt):
        update_radar_marks_impl(self, entities, map_manager, dt)

    def check_damage(self, entities):
        for shooter in entities:
            if shooter.type not in {'robot', 'sentry'} or not shooter.is_alive():
                continue
            if self._is_respawn_weak(shooter):
                continue
            if not self.entity_has_barrel(shooter):
                continue
            if shooter.type == 'sentry' and getattr(shooter, 'front_gun_locked', False):
                continue
            if getattr(shooter, 'fire_control_state', 'idle') != 'firing':
                continue
            if getattr(shooter, 'respawn_timer', 0.0) > 0 or getattr(shooter, 'shot_cooldown', 0.0) > 0:
                continue
            if getattr(shooter, 'overheat_lock_timer', 0.0) > 0 or self._available_ammo(shooter) <= 0:
                continue
            if getattr(shooter, 'heat_lock_state', 'normal') != 'normal':
                continue
            if self._next_shot_would_overheat(shooter):
                continue
            if self.get_effective_fire_rate_hz(shooter) <= 0.0:
                continue

            if bool(getattr(shooter, 'player_controlled', False)):
                target = None
                if isinstance(getattr(shooter, 'target', None), dict):
                    target_id = shooter.target.get('id')
                    target = next((entity for entity in entities if entity.id == target_id and entity.team != shooter.team and entity.is_alive()), None)
                self._consume_shot(shooter)
                simulation = self._simulate_player_projectile(
                    shooter,
                    entities,
                    target=target,
                    aim_point=getattr(shooter, 'manual_aim_point', None),
                    allow_ricochet=bool(self.config.get('simulator', {}).get('player_projectile_ricochet_enabled', True)),
                )
                if simulation.get('trace') is not None:
                    self._spawn_projectile_trace(shooter, target, trace_payload=simulation['trace'])
                hit_target = simulation.get('hit_target')
                if hit_target is not None:
                    damage = self.calculate_damage(shooter, hit_target)
                    if damage > 0:
                        self._mark_in_combat(shooter)
                        self._mark_in_combat(hit_target)
                        hit_target.take_damage(damage)
                        self._track_recent_attack(hit_target, shooter, damage)
                        self._trigger_evasive_spin(hit_target, shooter)
                continue

            target = self._resolve_autoaim_target(shooter, entities)
            if target is None:
                continue

            self._consume_shot(shooter)
            aim_point = self._resolve_projectile_aim_point(shooter, target)
            simulation = self._simulate_ballistic_projectile(
                shooter,
                entities,
                target=target,
                aim_point=aim_point,
                allow_ricochet=False,
            )
            if simulation.get('trace') is not None:
                self._spawn_projectile_trace(shooter, target, trace_payload=simulation['trace'])
            hit_target = simulation.get('hit_target')
            if hit_target is not None:
                damage = self.calculate_damage(shooter, hit_target)
                if damage > 0:
                    self._mark_in_combat(shooter)
                    self._mark_in_combat(hit_target)
                    hit_target.take_damage(damage)
                    self._track_recent_attack(hit_target, shooter, damage)
                    self._trigger_evasive_spin(hit_target, shooter)

    def _resolve_autoaim_target(self, shooter, entities):
        if self._can_use_hero_structure_lob_fire(shooter):
            return self._resolve_hero_deployment_target(shooter, entities)
        max_distance = self.get_range(shooter.type)
        locked_target = self._get_locked_autoaim_target(shooter, entities, max_distance)
        if locked_target is not None:
            return locked_target
        target_id = None
        if isinstance(getattr(shooter, 'target', None), dict):
            target_id = shooter.target.get('id')

        if target_id is not None:
            for entity in entities:
                if entity.id == target_id and entity.team != shooter.team and entity.is_alive():
                    distance = math.hypot(entity.position['x'] - shooter.position['x'], entity.position['y'] - shooter.position['y'])
                    if distance <= max_distance and self.can_track_target(shooter, entity, distance):
                        shooter.autoaim_locked_target_id = entity.id
                        shooter.autoaim_lock_timer = float(self.rules['shooting'].get('autoaim_lock_duration', 0.6))
                        return entity

        nearest = None
        nearest_distance = None
        for entity in entities:
            if entity.team == shooter.team or not entity.is_alive():
                continue
            distance = math.hypot(entity.position['x'] - shooter.position['x'], entity.position['y'] - shooter.position['y'])
            if distance > max_distance:
                continue
            if not self.can_track_target(shooter, entity, distance):
                continue
            if nearest_distance is None or distance < nearest_distance:
                nearest = entity
                nearest_distance = distance
        if nearest is not None:
            shooter.autoaim_locked_target_id = nearest.id
            shooter.autoaim_lock_timer = float(self.rules['shooting'].get('autoaim_lock_duration', 0.6))
        return nearest

    def _get_locked_autoaim_target(self, shooter, entities, max_distance):
        locked_target_id = getattr(shooter, 'autoaim_locked_target_id', None)
        if locked_target_id is None or float(getattr(shooter, 'autoaim_lock_timer', 0.0)) <= 0.0:
            return None
        for entity in entities:
            if entity.id != locked_target_id or entity.team == shooter.team or not entity.is_alive():
                continue
            distance = math.hypot(entity.position['x'] - shooter.position['x'], entity.position['y'] - shooter.position['y'])
            if distance <= max_distance and self.can_track_target(shooter, entity, distance):
                shooter.autoaim_lock_timer = float(self.rules['shooting'].get('autoaim_lock_duration', 0.6))
                return entity
        shooter.autoaim_locked_target_id = None
        shooter.autoaim_lock_timer = 0.0
        return None

    def _consume_shot(self, shooter):
        self._consume_allowed_ammo(shooter, getattr(shooter, 'ammo_per_shot', self.rules['shooting']['ammo_per_shot']))
        effective_fire_rate = self.get_effective_fire_rate_hz(shooter)
        shooter.shot_cooldown = 1.0 / max(effective_fire_rate, 1e-6)
        shooter.power = max(0.0, float(getattr(shooter, 'power', 0.0)) - self.get_effective_power_per_shot(shooter))
        shooter.heat += self._heat_gain_per_shot(shooter)
        self._mark_in_combat(shooter)
        soft_limit = float(getattr(shooter, 'max_heat', 0.0))
        hard_limit = self._heat_soft_lock_threshold(shooter)
        if shooter.heat > hard_limit + 1e-6:
            if getattr(shooter, 'heat_lock_state', 'normal') != 'match_locked':
                self._set_heat_lock_state(shooter, 'match_locked', '热量超过致命阈值')
                self._log(f'{shooter.id} 热量超过上限保护阈值，发射机构本局永久锁定', shooter.team)
        elif shooter.heat > soft_limit + 1e-6:
            if getattr(shooter, 'heat_lock_state', 'normal') == 'normal':
                self._set_heat_lock_state(shooter, 'cooling_unlock', '热量超限待冷却归零')
                self._log(f'{shooter.id} 热量超限，发射机构锁定直至热量冷却归零', shooter.team)

    def get_range(self, entity_type):
        if entity_type in {'robot', 'sentry'}:
            return self.auto_aim_max_distance
        return self.auto_aim_max_distance

    def calculate_hit_probability(self, shooter, target, distance=None):
        return calculate_hit_probability_impl(self, shooter, target, distance=distance)

    def _calculate_hero_deployment_hit_probability(self, shooter, target, distance=None):
        return calculate_hero_deployment_hit_probability_impl(self, shooter, target, distance=distance)

    def _get_turret_angle_diff(self, shooter, target):
        desired_angle = self._desired_turret_angle(shooter, target)
        turret_angle = getattr(shooter, 'turret_angle', shooter.angle)
        return self._normalize_angle_diff(desired_angle - turret_angle)

    def _get_auto_aim_accuracy(self, distance, target):
        return get_auto_aim_accuracy_impl(self, distance, target)

    def classify_target_motion(self, target):
        return classify_target_motion_impl(self, target)

    def _is_fast_spinning_target(self, target):
        return is_fast_spinning_target_impl(self, target)

    def _trigger_evasive_spin(self, target, shooter):
        trigger_evasive_spin_impl(self, target, shooter)

    def describe_target_motion(self, target):
        return describe_target_motion_impl(self, target)

    def _hit_probability_multiplier(self, shooter):
        return hit_probability_multiplier_impl(self, shooter)

    def calculate_damage(self, shooter, target):
        return calculate_damage_impl(self, shooter, target)

    def _resolve_projectile_damage_key(self, shooter):
        return resolve_projectile_damage_key_impl(self, shooter)

    def handle_collision_damage(self, entity1, entity2, impact_speed):
        min_impact_speed = float(self.rules.get('collision', {}).get('min_impact_speed', 1.0))
        if impact_speed < min_impact_speed:
            return

        pair_key = tuple(sorted((entity1.id, entity2.id)))
        cooldown = float(self.rules.get('collision', {}).get('damage_cooldown', 0.5))
        last_time = self.collision_damage_cooldowns.get(pair_key, -1e9)
        if self.game_time - last_time < cooldown:
            return
        self.collision_damage_cooldowns[pair_key] = self.game_time

        for source, target in ((entity1, entity2), (entity2, entity1)):
            damage = float(self.damage_system.get(target.type, {}).get('collision', 0))
            if damage > 0 and target.is_alive():
                self._mark_in_combat(source)
                self._mark_in_combat(target)
                target.take_damage(damage)

    def _damage_dealt_multiplier(self, shooter):
        return damage_dealt_multiplier_impl(self, shooter)

    def _damage_taken_multiplier(self, target):
        return damage_taken_multiplier_impl(self, target)

    def check_health(self, entities):
        for entity in entities:
            if entity.health > 0:
                continue
            entity.health = 0
            self.handle_destroy(entity)

    def handle_destroy(self, entity):
        handle_destroy_impl(self, entity)

    def _respawn_entity(self, entity, respawn_mode='normal'):
        respawn_entity_impl(self, entity, respawn_mode=respawn_mode)

    def get_initial_health(self, entity_type):
        if entity_type in self.health_system:
            return self.health_system[entity_type]['initial_health']
        return 100

    def get_max_health(self, entity_type):
        if entity_type in self.health_system:
            return self.health_system[entity_type]['max_health']
        return 100

    def request_posture_change(self, sentry, posture):
        if sentry.type != 'sentry':
            return {'ok': False, 'code': 'NOT_SENTRY'}
        if posture not in self.posture_effects:
            return {'ok': False, 'code': 'INVALID_POSTURE'}
        if getattr(sentry, 'posture_cooldown', 0.0) > 0:
            return {'ok': False, 'code': 'POSTURE_COOLDOWN'}

        exchange = self.rules['sentry']['exchange']
        intervention_key = 'semi_auto_intervention_cost' if getattr(sentry, 'sentry_mode', 'auto') == 'semi_auto' else 'auto_intervention_cost'
        intervention_cost = float(exchange.get(intervention_key, 0.0))
        if sentry.gold + 1e-6 < intervention_cost:
            return {'ok': False, 'code': 'INSUFFICIENT_GOLD', 'need': intervention_cost, 'have': sentry.gold}
        if intervention_cost > 0.0:
            sentry.gold -= intervention_cost
            self.team_gold[sentry.team] = sentry.gold

        sentry.posture = posture
        sentry.posture_cooldown = self.rules['sentry']['posture_cooldown']
        sentry.posture_active_time = 0.0
        return {'ok': True, 'code': 'POSTURE_SWITCHED', 'posture': posture, 'cost': intervention_cost}

    def request_exchange(self, sentry, exchange_type, amount=1, target_entity=None):
        if sentry.type != 'sentry':
            return {'ok': False, 'code': 'NOT_SENTRY'}
        if getattr(sentry, 'exchange_cooldown', 0.0) > 0:
            return {'ok': False, 'code': 'EXCHANGE_COOLDOWN', 'time_left': getattr(sentry, 'exchange_cooldown', 0.0)}

        exchange = self.rules['sentry']['exchange']
        costs = {
            'ammo': exchange['ammo_cost'],
            'remote_ammo': exchange['remote_ammo_cost'],
            'hp': exchange['hp_cost'],
            'remote_hp': exchange['remote_hp_cost'],
            'revive_now': exchange['revive_now_cost'],
        }
        if exchange_type not in costs:
            return {'ok': False, 'code': 'INVALID_EXCHANGE_TYPE'}

        if exchange_type == 'revive_now' and sentry.is_alive():
            return {'ok': False, 'code': 'ALREADY_ALIVE'}

        if exchange_type in {'remote_ammo', 'remote_hp'}:
            if target_entity is None:
                return {'ok': False, 'code': 'TARGET_REQUIRED'}
            if target_entity.team != sentry.team:
                return {'ok': False, 'code': 'INVALID_TARGET_TEAM'}
            if not target_entity.is_alive():
                return {'ok': False, 'code': 'TARGET_NOT_ALIVE'}

        intervention_key = 'semi_auto_intervention_cost' if getattr(sentry, 'sentry_mode', 'auto') == 'semi_auto' else 'auto_intervention_cost'
        intervention_cost = float(exchange.get(intervention_key, 0.0)) * amount
        total_cost = intervention_cost
        if sentry.gold < total_cost:
            return {'ok': False, 'code': 'INSUFFICIENT_GOLD', 'need': total_cost, 'have': sentry.gold}

        sentry.gold -= total_cost
        self.team_gold[sentry.team] = sentry.gold
        sentry.exchange_cooldown = float(exchange.get('interval_sec', 2.0))

        if exchange_type in {'ammo', 'remote_ammo'}:
            ammo_receiver = target_entity if exchange_type == 'remote_ammo' else sentry
            ammo_type = getattr(ammo_receiver, 'ammo_type', '17mm')
            if ammo_type == '42mm':
                gain = exchange['remote_42mm_unit'] if exchange_type == 'remote_ammo' else exchange['local_42mm_unit']
            else:
                gain = exchange['remote_17mm_unit'] if exchange_type == 'remote_ammo' else exchange['local_17mm_unit']
            if exchange_type == 'remote_ammo':
                self._queue_pending_rule_event(
                    ammo_receiver,
                    'remote_ammo',
                    exchange['remote_delay'],
                    {'amount': int(gain * amount), 'ammo_type': ammo_type},
                )
            else:
                self._add_allowed_ammo(sentry, int(gain * amount), ammo_type)
        elif exchange_type in {'hp', 'remote_hp'}:
            hp_receiver = target_entity if exchange_type == 'remote_hp' else sentry
            heal_amount = hp_receiver.max_health * float(exchange['remote_hp_gain_ratio' if exchange_type == 'remote_hp' else 'hp_gain_ratio']) * amount
            if exchange_type == 'remote_hp':
                self._queue_pending_rule_event(
                    hp_receiver,
                    'remote_hp',
                    exchange['remote_delay'],
                    {'amount': float(heal_amount)},
                )
            else:
                sentry.heal(heal_amount)
        elif exchange_type == 'revive_now':
            sentry.instant_respawn_count = int(getattr(sentry, 'instant_respawn_count', 0)) + 1
            self._respawn_entity(sentry, respawn_mode='instant')

        return {
            'ok': True,
            'code': 'EXCHANGE_SUCCESS',
            'exchange_type': exchange_type,
            'cost': total_cost,
            'target_id': getattr(target_entity, 'id', sentry.id),
        }

    def confirm_respawn(self, sentry):
        if sentry.type != 'sentry':
            return {'ok': False, 'code': 'NOT_SENTRY'}
        if sentry.is_alive():
            return {'ok': False, 'code': 'ALREADY_ALIVE'}
        if getattr(sentry, 'respawn_timer', 0.0) > 0:
            return {'ok': False, 'code': 'RESPAWN_NOT_READY', 'time_left': sentry.respawn_timer}

        exchange = self.rules['sentry']['exchange']
        intervention_key = 'semi_auto_intervention_cost' if getattr(sentry, 'sentry_mode', 'auto') == 'semi_auto' else 'auto_intervention_cost'
        intervention_cost = float(exchange.get(intervention_key, 0.0))
        if sentry.gold + 1e-6 < intervention_cost:
            return {'ok': False, 'code': 'INSUFFICIENT_GOLD', 'need': intervention_cost, 'have': sentry.gold}
        if intervention_cost > 0.0:
            sentry.gold -= intervention_cost
            self.team_gold[sentry.team] = sentry.gold

        self._respawn_entity(sentry)
        return {'ok': True, 'code': 'RESPAWN_CONFIRMED', 'cost': intervention_cost}

    def get_referee_message(self, entities, map_manager, game_time, game_duration, focus_team='red'):
        sentry = self._find_entity(entities, focus_team, 'sentry')
        if sentry is None:
            return {}

        enemy_team = 'blue' if focus_team == 'red' else 'red'
        base = self._find_entity(entities, focus_team, 'base')
        outpost = self._find_entity(entities, focus_team, 'outpost')
        marked_enemy_ids: List[str] = []
        max_mark_progress = 0.0
        for entity in entities:
            if entity.team == enemy_team:
                progress = self.radar_marks.get(entity.id, 0.0)
                if progress > 0:
                    marked_enemy_ids.append(entity.id)
                max_mark_progress = max(max_mark_progress, progress)

        effect = self._resolve_posture_effect(sentry)
        gains = {
            'occupied_facilities': self.occupied_facilities.get(focus_team, {}),
            'fort_active': getattr(sentry, 'fort_buff_active', False),
            'terrain_cross_active': getattr(sentry, 'terrain_buff_timer', 0.0) > 0,
            'terrain_cross_time_left': round(getattr(sentry, 'terrain_buff_timer', 0.0), 2),
            'supply_cooldown': round(getattr(sentry, 'supply_cooldown', 0.0), 2),
        }

        team_info = {}
        for entity in entities:
            if entity.team != focus_team:
                continue
            team_info[entity.id] = {
                'type': entity.type,
                'hp': entity.health,
                'max_hp': entity.max_health,
                'pos': (entity.position['x'], entity.position['y'], entity.position.get('z', 0)),
                'alive': entity.is_alive(),
                'state': entity.state,
                'respawn_timer': round(getattr(entity, 'respawn_timer', 0.0), 2),
                'invincible_timer': round(getattr(entity, 'invincible_timer', 0.0), 2),
                'weak_timer': round(getattr(entity, 'weak_timer', 0.0), 2),
            }

        return {
            'game_status': {
                'stage': self.stage,
                'time_left': max(0.0, game_duration - game_time),
                'match_time': game_duration,
            },
            'robot_status': {
                'hp': sentry.health,
                'heat': sentry.heat,
                'ammo': getattr(sentry, 'ammo', 0),
                'gold': round(sentry.gold, 2),
                'team_minerals': int(self.team_minerals.get(focus_team, 0)),
                'posture': getattr(sentry, 'posture', 'mobile'),
                'posture_cooldown': round(getattr(sentry, 'posture_cooldown', 0.0), 2),
                'height': sentry.position.get('z', 0),
                'respawn_timer': round(getattr(sentry, 'respawn_timer', 0.0), 2),
                'invincible_timer': round(getattr(sentry, 'invincible_timer', 0.0), 2),
                'weak_timer': round(getattr(sentry, 'weak_timer', 0.0), 2),
                'auto_aim_limit': round(self.auto_aim_max_distance, 2),
            },
            'power_heat_data': {
                'power_mult': effect['power_mult'],
                'cool_mult': effect['cool_mult'],
                'damage_mult': effect['damage_mult'],
            },
            'event_data': {
                'base_hp': base.health if base else 0,
                'outpost_hp': outpost.health if outpost else 0,
                'gains': gains,
                'team_gold': round(self.team_gold.get(focus_team, 0.0), 2),
                'team_minerals': int(self.team_minerals.get(focus_team, 0)),
                'facility_summary': map_manager.get_facility_summary() if map_manager else {},
                'radar_marked_enemies': marked_enemy_ids,
            },
            'radar_data': {
                'marked_progress_P': round(max_mark_progress, 3),
                'vulnerability': self.rules['radar']['vulnerability_mult'] if max_mark_progress >= 1.0 else 1.0,
            },
            'team_info': team_info,
        }

    def _log(self, message, team='system'):
        if self.game_engine is not None:
            self.game_engine.add_log(message, team)
