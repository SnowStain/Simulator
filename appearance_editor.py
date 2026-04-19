#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import math
import os
import sys
from copy import deepcopy
from typing import Any, cast

import numpy as np
from pygame_compat import pygame

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.config_manager import ConfigManager
from entities.chassis_profiles import build_infantry_profile_payload, infantry_chassis_options, infantry_chassis_preset, normalize_infantry_chassis_subtype, normalize_infantry_component_profile, resolve_infantry_subtype_profile

try:
    import moderngl
    from rendering.terrain_scene_backends import _terrain_scene_look_at, _terrain_scene_perspective_matrix
    MODERNGL_PREVIEW_ERROR = None
except Exception as exc:
    moderngl = None
    MODERNGL_PREVIEW_ERROR = str(exc)


ROLE_ORDER = (
    ('hero', '英雄'),
    ('engineer', '工程'),
    ('infantry', '步兵'),
    ('sentry', '哨兵'),
)

PART_LABELS = {
    'body': '底盘',
    'wheel': '车轮',
    'front_climb': '前上台阶机构',
    'rear_climb': '后腿机构',
    'mount': '连接件',
    'turret': '云台',
    'barrel': '枪管',
    'armor': '装甲',
    'armor_light': '装甲灯条',
    'barrel_light': '枪管灯条',
}

_BASE_PROFILE_TEMPLATES = {
    'hero': {
        'body_length_m': 0.65,
        'body_width_m': 0.55,
        'body_height_m': 0.19,
        'body_clearance_m': 0.07,
        'wheel_radius_m': 0.085,
        'gimbal_length_m': 0.34,
        'gimbal_width_m': 0.20,
        'gimbal_body_height_m': 0.14,
        'gimbal_mount_gap_m': 0.10,
        'gimbal_mount_length_m': 0.14,
        'gimbal_mount_width_m': 0.15,
        'gimbal_mount_height_m': 0.14,
        'barrel_length_m': 0.14,
        'barrel_radius_m': 0.026,
        'gimbal_height_m': 0.435,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.29,
        'armor_plate_length_m': 0.29,
        'armor_plate_height_m': 0.16,
        'armor_plate_gap_m': 0.005,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.08,
        'barrel_light_length_m': 0.13,
        'barrel_light_width_m': 0.015,
        'barrel_light_height_m': 0.05,
        'body_render_width_scale': 0.82,
        'wheel_style': 'mecanum',
        'suspension_style': 'four_bar',
        'arm_style': 'none',
        'front_climb_assist_style': 'belt_lift',
        'rear_climb_assist_style': 'balance_leg',
    },
    'engineer': {
        'body_length_m': 0.55,
        'body_width_m': 0.50,
        'body_height_m': 0.16,
        'body_clearance_m': 0.11,
        'wheel_radius_m': 0.08,
        'gimbal_length_m': 0.0,
        'gimbal_width_m': 0.0,
        'gimbal_body_height_m': 0.0,
        'gimbal_mount_gap_m': 0.0,
        'gimbal_mount_length_m': 0.0,
        'gimbal_mount_width_m': 0.0,
        'gimbal_mount_height_m': 0.0,
        'barrel_length_m': 0.0,
        'barrel_radius_m': 0.0,
        'gimbal_height_m': 0.42,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.17,
        'armor_plate_length_m': 0.17,
        'armor_plate_height_m': 0.15,
        'armor_plate_gap_m': 0.005,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.08,
        'barrel_light_length_m': 0.10,
        'barrel_light_width_m': 0.02,
        'barrel_light_height_m': 0.02,
        'body_render_width_scale': 0.82,
        'wheel_style': 'mecanum',
        'suspension_style': 'none',
        'arm_style': 'fixed_7',
        'front_climb_assist_style': 'belt_lift',
        'rear_climb_assist_style': 'balance_leg',
    },
    'infantry': {
        'chassis_subtype': 'balance_legged',
        'body_shape': 'box',
        'body_length_m': 0.49,
        'body_width_m': 0.42,
        'body_height_m': 0.16,
        'body_clearance_m': 0.16,
        'wheel_radius_m': 0.06,
        'gimbal_length_m': 0.30,
        'gimbal_width_m': 0.15,
        'gimbal_body_height_m': 0.11,
        'gimbal_mount_gap_m': 0.10,
        'gimbal_mount_length_m': 0.09,
        'gimbal_mount_width_m': 0.07,
        'gimbal_mount_height_m': 0.11,
        'barrel_length_m': 0.12,
        'barrel_radius_m': 0.015,
        'gimbal_height_m': 0.47,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.16,
        'armor_plate_length_m': 0.16,
        'armor_plate_height_m': 0.16,
        'armor_plate_gap_m': 0.005,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.08,
        'barrel_light_length_m': 0.095,
        'barrel_light_width_m': 0.005,
        'barrel_light_height_m': 0.03,
        'body_render_width_scale': 0.73,
        'wheel_style': 'legged',
        'suspension_style': 'four_bar',
        'arm_style': 'none',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'balance_leg',
    },
    'sentry': {
        'body_length_m': 0.55,
        'body_width_m': 0.50,
        'body_height_m': 0.18,
        'body_clearance_m': 0.07,
        'wheel_radius_m': 0.08,
        'gimbal_length_m': 0.30,
        'gimbal_width_m': 0.15,
        'gimbal_body_height_m': 0.11,
        'gimbal_mount_gap_m': 0.10,
        'gimbal_mount_length_m': 0.10,
        'gimbal_mount_width_m': 0.10,
        'gimbal_mount_height_m': 0.10,
        'barrel_length_m': 0.12,
        'barrel_radius_m': 0.015,
        'gimbal_height_m': 0.39,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.16,
        'armor_plate_length_m': 0.16,
        'armor_plate_height_m': 0.16,
        'armor_plate_gap_m': 0.005,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.08,
        'barrel_light_length_m': 0.095,
        'barrel_light_width_m': 0.005,
        'barrel_light_height_m': 0.03,
        'body_render_width_scale': 0.76,
        'wheel_style': 'mecanum',
        'suspension_style': 'four_bar',
        'arm_style': 'none',
        'front_climb_assist_style': 'belt_lift',
        'rear_climb_assist_style': 'balance_leg',
    },
}


def _default_color_profile():
    return {
        'body_color_rgb': [166, 174, 186],
        'turret_color_rgb': [232, 232, 236],
        'armor_color_rgb': [224, 229, 234],
        'wheel_color_rgb': [44, 44, 44],
    }


def _climb_assist_defaults():
    return {
        'front_climb_assist_top_length_m': 0.05,
        'front_climb_assist_bottom_length_m': 0.03,
        'front_climb_assist_plate_width_m': 0.018,
        'front_climb_assist_plate_height_m': 0.18,
        'front_climb_assist_forward_offset_m': 0.04,
        'front_climb_assist_inner_offset_m': 0.06,
        'rear_climb_assist_upper_length_m': 0.09,
        'rear_climb_assist_lower_length_m': 0.08,
        'rear_climb_assist_upper_width_m': 0.016,
        'rear_climb_assist_upper_height_m': 0.016,
        'rear_climb_assist_lower_width_m': 0.016,
        'rear_climb_assist_lower_height_m': 0.016,
        'rear_climb_assist_mount_offset_x_m': 0.03,
        'rear_climb_assist_mount_height_m': 0.22,
        'rear_climb_assist_inner_offset_m': 0.03,
        'rear_climb_assist_upper_pair_gap_m': 0.06,
        'rear_climb_assist_hinge_radius_m': 0.016,
        'rear_climb_assist_knee_min_deg': 42.0,
        'rear_climb_assist_knee_max_deg': 132.0,
    }


def _normalize_rgb_triplet(value, fallback):
    if not isinstance(value, (list, tuple)) or len(value) != 3:
        return list(fallback)
    return [max(0, min(255, int(round(float(channel))))) for channel in value]


def _normalize_balance_leg_knee_direction(value, fallback='rear'):
    normalized = str(value or fallback).strip().lower()
    return normalized if normalized in {'front', 'rear'} else str(fallback).strip().lower()


def _build_default_wheel_positions(profile):
    wheel_y = round(float(profile['body_width_m']) * 0.5 + float(profile['wheel_radius_m']) * 0.58, 3)
    if str(profile.get('wheel_style', 'mecanum')) == 'legged':
        return [[0.0, -wheel_y], [0.0, wheel_y]]
    if str(profile.get('wheel_style', 'mecanum')) == 'omni':
        wheel_x = round(float(profile['body_length_m']) * 0.36, 3)
        wheel_y = round(float(profile['body_width_m']) * 0.36, 3)
        return [[wheel_x, 0.0], [0.0, wheel_y], [-wheel_x, 0.0], [0.0, -wheel_y]]
    wheel_x = round(float(profile['body_length_m']) * 0.39, 3)
    return [[-wheel_x, -wheel_y], [wheel_x, -wheel_y], [-wheel_x, wheel_y], [wheel_x, wheel_y]]


def _apply_climb_assist_defaults(role_key, profile):
    for key, value in _climb_assist_defaults().items():
        profile.setdefault(key, value)
    if role_key in {'hero', 'sentry'}:
        profile.setdefault('front_climb_assist_style', 'belt_lift')
        profile.setdefault('rear_climb_assist_style', 'balance_leg')
        profile.setdefault('suspension_style', 'four_bar')
    elif role_key == 'engineer':
        profile.setdefault('front_climb_assist_style', 'belt_lift')
        profile.setdefault('rear_climb_assist_style', 'balance_leg')
        profile.setdefault('suspension_style', 'four_bar')
    else:
        profile.setdefault('front_climb_assist_style', 'none')
        profile.setdefault('rear_climb_assist_style', 'none')


def _normalize_profile_constraints(role_key, profile, forced_subtype=None):
    normalized = deepcopy(_BASE_PROFILE_TEMPLATES.get(role_key, _BASE_PROFILE_TEMPLATES['infantry']))
    if isinstance(profile, dict):
        normalized.update(deepcopy(profile))
    normalized.update({key: deepcopy(value) for key, value in _default_color_profile().items() if key not in normalized})
    _apply_climb_assist_defaults(role_key, normalized)

    legacy_front_length = float(normalized.get('front_climb_assist_plate_length_m', normalized.get('front_climb_assist_top_length_m', 0.05)))
    normalized['front_climb_assist_top_length_m'] = float(normalized.get('front_climb_assist_top_length_m', legacy_front_length))
    normalized['front_climb_assist_bottom_length_m'] = float(normalized.get('front_climb_assist_bottom_length_m', max(0.02, legacy_front_length * 0.6)))

    legacy_bar_width = float(normalized.get('rear_climb_assist_bar_width_m', 0.016))
    normalized['rear_climb_assist_upper_width_m'] = float(normalized.get('rear_climb_assist_upper_width_m', legacy_bar_width))
    normalized['rear_climb_assist_upper_height_m'] = float(normalized.get('rear_climb_assist_upper_height_m', legacy_bar_width))
    normalized['rear_climb_assist_lower_width_m'] = float(normalized.get('rear_climb_assist_lower_width_m', legacy_bar_width))
    normalized['rear_climb_assist_lower_height_m'] = float(normalized.get('rear_climb_assist_lower_height_m', legacy_bar_width))
    normalized['rear_climb_assist_mount_offset_x_m'] = float(normalized.get('rear_climb_assist_mount_offset_x_m', normalized.get('rear_climb_assist_upper_offset_m', 0.03)))
    normalized['rear_climb_assist_mount_height_m'] = float(normalized.get('rear_climb_assist_mount_height_m', float(normalized.get('body_clearance_m', 0.0)) + float(normalized.get('body_height_m', 0.0)) * 0.92))
    normalized['rear_climb_assist_upper_pair_gap_m'] = float(normalized.get('rear_climb_assist_upper_pair_gap_m', max(0.04, normalized['rear_climb_assist_upper_length_m'] * 0.28)))
    normalized['rear_climb_assist_hinge_radius_m'] = float(normalized.get('rear_climb_assist_hinge_radius_m', max(0.012, normalized['rear_climb_assist_upper_width_m'] * 0.8)))
    normalized['rear_climb_assist_knee_min_deg'] = float(normalized.get('rear_climb_assist_knee_min_deg', 42.0))
    normalized['rear_climb_assist_knee_max_deg'] = float(max(normalized['rear_climb_assist_knee_min_deg'], normalized.get('rear_climb_assist_knee_max_deg', 132.0)))

    if role_key in {'hero', 'engineer', 'sentry'}:
        normalized['suspension_style'] = 'four_bar'
        normalized['rear_climb_assist_style'] = 'balance_leg'

    if role_key == 'infantry' and normalize_infantry_chassis_subtype(forced_subtype or normalized.get('chassis_subtype')) == 'balance_legged':
        normalized['suspension_style'] = 'four_bar'
        normalized['rear_climb_assist_style'] = 'balance_leg'

    normalized['body_color_rgb'] = _normalize_rgb_triplet(normalized.get('body_color_rgb'), _default_color_profile()['body_color_rgb'])
    normalized['turret_color_rgb'] = _normalize_rgb_triplet(normalized.get('turret_color_rgb'), _default_color_profile()['turret_color_rgb'])
    normalized['armor_color_rgb'] = _normalize_rgb_triplet(normalized.get('armor_color_rgb'), _default_color_profile()['armor_color_rgb'])
    normalized['wheel_color_rgb'] = _normalize_rgb_triplet(normalized.get('wheel_color_rgb'), _default_color_profile()['wheel_color_rgb'])

    if float(normalized.get('gimbal_length_m', 0.0)) <= 1e-6 or float(normalized.get('gimbal_body_height_m', 0.0)) <= 1e-6:
        normalized['gimbal_length_m'] = 0.0
        normalized['gimbal_width_m'] = 0.0
        normalized['gimbal_body_height_m'] = 0.0
        normalized['gimbal_mount_length_m'] = 0.0
        normalized['gimbal_mount_width_m'] = 0.0
        normalized['gimbal_mount_height_m'] = 0.0
        normalized['barrel_length_m'] = 0.0
        normalized['barrel_radius_m'] = 0.0

    wheel_positions = normalized.get('custom_wheel_positions_m')
    expected_count = 2 if str(normalized.get('wheel_style', 'mecanum')) == 'legged' else 4
    if not isinstance(wheel_positions, list) or len(wheel_positions) != expected_count:
        normalized['custom_wheel_positions_m'] = _build_default_wheel_positions(normalized)
    else:
        normalized['custom_wheel_positions_m'] = [
            [round(float(position[0]), 3), round(float(position[1]), 3)]
            for position in wheel_positions
            if isinstance(position, (list, tuple)) and len(position) >= 2
        ]
        if len(normalized['custom_wheel_positions_m']) != expected_count:
            normalized['custom_wheel_positions_m'] = _build_default_wheel_positions(normalized)
    if role_key == 'infantry':
        normalized = normalize_infantry_component_profile(normalized, forced_subtype or normalized.get('chassis_subtype'))
    default_knee_direction = 'front' if role_key in {'hero', 'engineer', 'sentry'} else 'rear'
    normalized['rear_climb_assist_knee_direction'] = _normalize_balance_leg_knee_direction(normalized.get('rear_climb_assist_knee_direction'), default_knee_direction)
    if float(normalized.get('gimbal_length_m', 0.0)) > 1e-6 and float(normalized.get('gimbal_body_height_m', 0.0)) > 1e-6:
        normalized['gimbal_height_m'] = _profile_turret_center_height(normalized)
    else:
        normalized['gimbal_height_m'] = 0.0
    return normalized


def _default_profile(role_key):
    return _normalize_profile_constraints(role_key, deepcopy(_BASE_PROFILE_TEMPLATES[role_key]))


def _front_climb_lengths(profile):
    top_length = float(profile.get('front_climb_assist_top_length_m', profile.get('front_climb_assist_plate_length_m', 0.05)))
    bottom_length = float(profile.get('front_climb_assist_bottom_length_m', max(0.02, top_length * 0.6)))
    return top_length, bottom_length


def _profile_mount_center_height(profile):
    body_top = float(profile.get('body_clearance_m', 0.0)) + float(profile.get('body_height_m', 0.0))
    mount_gap = max(0.0, float(profile.get('gimbal_mount_gap_m', 0.0)))
    mount_height = max(0.0, float(profile.get('gimbal_mount_height_m', 0.0)))
    return body_top + (mount_gap + mount_height) * 0.5


def _profile_turret_center_height(profile):
    body_top = float(profile.get('body_clearance_m', 0.0)) + float(profile.get('body_height_m', 0.0))
    mount_gap = max(0.0, float(profile.get('gimbal_mount_gap_m', 0.0)))
    mount_height = max(0.0, float(profile.get('gimbal_mount_height_m', 0.0)))
    turret_half_height = max(0.0, float(profile.get('gimbal_body_height_m', 0.0)) * 0.5)
    return body_top + mount_gap + mount_height + turret_half_height


def _knee_internal_angle_deg(anchor_point, knee_point, foot_point):
    anchor_vec = (float(anchor_point[0]) - float(knee_point[0]), float(anchor_point[1]) - float(knee_point[1]))
    foot_vec = (float(foot_point[0]) - float(knee_point[0]), float(foot_point[1]) - float(knee_point[1]))
    anchor_len = math.hypot(anchor_vec[0], anchor_vec[1])
    foot_len = math.hypot(foot_vec[0], foot_vec[1])
    if anchor_len <= 1e-6 or foot_len <= 1e-6:
        return 180.0
    dot = (anchor_vec[0] * foot_vec[0] + anchor_vec[1] * foot_vec[1]) / max(anchor_len * foot_len, 1e-6)
    dot = max(-1.0, min(1.0, dot))
    return math.degrees(math.acos(dot))


def _clamp_knee_blend_ratio(anchor_point, folded_knee, straight_knee, foot_point, desired_ratio, min_angle_deg, max_angle_deg):
    ratio = max(0.0, min(1.0, float(desired_ratio)))
    min_angle_deg = max(5.0, float(min_angle_deg))
    max_angle_deg = max(min_angle_deg, float(max_angle_deg))
    current_angle = _knee_internal_angle_deg(anchor_point, folded_knee, foot_point)
    if min_angle_deg <= current_angle <= max_angle_deg:
        pass
    else:
        return 0.0
    candidate_angle = _knee_internal_angle_deg(
        anchor_point,
        (
            float(folded_knee[0]) + (float(straight_knee[0]) - float(folded_knee[0])) * ratio,
            float(folded_knee[1]) + (float(straight_knee[1]) - float(folded_knee[1])) * ratio,
        ),
        foot_point,
    )
    if min_angle_deg <= candidate_angle <= max_angle_deg:
        return ratio
    low = 0.0
    high = ratio
    for _ in range(12):
        mid = (low + high) * 0.5
        mid_angle = _knee_internal_angle_deg(
            anchor_point,
            (
                float(folded_knee[0]) + (float(straight_knee[0]) - float(folded_knee[0])) * mid,
                float(folded_knee[1]) + (float(straight_knee[1]) - float(folded_knee[1])) * mid,
            ),
            foot_point,
        )
        if min_angle_deg <= mid_angle <= max_angle_deg:
            low = mid
        else:
            high = mid
    return low


def _clamp_two_link_target_point(anchor_point, target_point, upper_length, lower_length, min_angle_deg, max_angle_deg):
    anchor_x, anchor_y = float(anchor_point[0]), float(anchor_point[1])
    target_x, target_y = float(target_point[0]), float(target_point[1])
    direction_x = target_x - anchor_x
    direction_y = target_y - anchor_y
    distance = math.hypot(direction_x, direction_y)
    if distance <= 1e-6:
        return (anchor_x, anchor_y + max(0.001, float(abs(upper_length - lower_length))))

    min_angle = max(5.0, min(175.0, float(min_angle_deg)))
    max_angle = max(min_angle, min(175.0, float(max_angle_deg)))

    def span_for_angle(angle_deg):
        angle_rad = math.radians(float(angle_deg))
        return math.sqrt(max(float(upper_length) ** 2 + float(lower_length) ** 2 - 2.0 * float(upper_length) * float(lower_length) * math.cos(angle_rad), 1e-8))

    span_min = span_for_angle(min_angle)
    span_max = span_for_angle(max_angle)
    low = max(abs(float(upper_length) - float(lower_length)) + 1e-6, min(span_min, span_max))
    high = min(float(upper_length) + float(lower_length) - 1e-6, max(span_min, span_max))
    clamped_distance = max(low, min(high, distance))
    scale = clamped_distance / distance
    return (anchor_x + direction_x * scale, anchor_y + direction_y * scale)


def _available_preview_actions(role_key, profile):
    if role_key == 'infantry':
        subtype = normalize_infantry_chassis_subtype(profile.get('chassis_subtype'))
        if subtype == 'balance_legged':
            return (('idle', '静态'), ('jump', '跳跃'))
        return (('idle', '静态'),)
    return (('idle', '静态'), ('step', '上台阶'))


def _resolve_two_link_joint(start_point, end_point, upper_length, lower_length):
    start_x, start_y = float(start_point[0]), float(start_point[1])
    end_x, end_y = float(end_point[0]), float(end_point[1])
    delta_x = end_x - start_x
    delta_y = end_y - start_y
    distance = math.hypot(delta_x, delta_y)
    if distance <= 1e-6:
        return ((start_x + end_x) * 0.5, min(start_y, end_y) - max(upper_length, lower_length) * 0.35)
    clamped_distance = max(abs(float(upper_length) - float(lower_length)) + 1e-6, min(distance, float(upper_length) + float(lower_length) - 1e-6))
    direction_x = delta_x / distance
    direction_y = delta_y / distance
    base_distance = (float(upper_length) ** 2 - float(lower_length) ** 2 + clamped_distance ** 2) / max(2.0 * clamped_distance, 1e-6)
    height = math.sqrt(max(float(upper_length) ** 2 - base_distance ** 2, 0.0))
    base_x = start_x + direction_x * base_distance
    base_y = start_y + direction_y * base_distance
    perp_x = -direction_y
    perp_y = direction_x
    candidate_a = (base_x + perp_x * height, base_y + perp_y * height)
    candidate_b = (base_x - perp_x * height, base_y - perp_y * height)
    preferred = candidate_a if candidate_a[0] >= candidate_b[0] else candidate_b
    alternate = candidate_b if preferred is candidate_a else candidate_a
    if preferred[0] < max(start_x, end_x):
        return alternate
    return preferred


def _resolve_two_link_joint_candidates(start_point, end_point, upper_length, lower_length):
    start_x, start_y = float(start_point[0]), float(start_point[1])
    end_x, end_y = float(end_point[0]), float(end_point[1])
    delta_x = end_x - start_x
    delta_y = end_y - start_y
    distance = math.hypot(delta_x, delta_y)
    if distance <= 1e-6:
        midpoint = ((start_x + end_x) * 0.5, min(start_y, end_y) - max(upper_length, lower_length) * 0.35)
        return (midpoint, midpoint)
    clamped_distance = max(abs(float(upper_length) - float(lower_length)) + 1e-6, min(distance, float(upper_length) + float(lower_length) - 1e-6))
    direction_x = delta_x / distance
    direction_y = delta_y / distance
    base_distance = (float(upper_length) ** 2 - float(lower_length) ** 2 + clamped_distance ** 2) / max(2.0 * clamped_distance, 1e-6)
    height = math.sqrt(max(float(upper_length) ** 2 - base_distance ** 2, 0.0))
    base_x = start_x + direction_x * base_distance
    base_y = start_y + direction_y * base_distance
    perp_x = -direction_y
    perp_y = direction_x
    candidate_a = (base_x + perp_x * height, base_y + perp_y * height)
    candidate_b = (base_x - perp_x * height, base_y - perp_y * height)
    return candidate_a, candidate_b


def _select_balance_leg_joint(anchor_point, foot_point, upper_length, lower_length, knee_direction='rear'):
    candidates = _resolve_two_link_joint_candidates(anchor_point, foot_point, upper_length, lower_length)
    anchor_x, anchor_y = float(anchor_point[0]), float(anchor_point[1])
    prefer_front = _normalize_balance_leg_knee_direction(knee_direction, 'rear') == 'front'

    def score(candidate):
        candidate_x, candidate_y = float(candidate[0]), float(candidate[1])
        direction_penalty = max(0.0, anchor_x - candidate_x) * 1000.0 if prefer_front else max(0.0, candidate_x - anchor_x) * 1000.0
        above_penalty = max(0.0, candidate_y - anchor_y) * 100.0
        x_bias = (-candidate_x if prefer_front else candidate_x) * 0.25
        return direction_penalty + above_penalty + x_bias

    return min(candidates, key=score)


def _rear_climb_points(profile, render_width_scale=1.0):
    body_half_x = float(profile['body_length_m']) * 0.5
    wheel_radius = max(0.018, float(profile['wheel_radius_m']))
    wheel_positions = profile.get('custom_wheel_positions_m', [])
    rear_wheel_x = min((float(position[0]) for position in wheel_positions if isinstance(position, (list, tuple)) and len(position) >= 2), default=-body_half_x * 0.78)
    wheel_outer = max((abs(float(position[1])) * render_width_scale for position in wheel_positions if isinstance(position, (list, tuple)) and len(position) >= 2), default=float(profile['body_width_m']) * 0.5 * render_width_scale + wheel_radius * 0.55)
    side_offset = max(float(profile['body_width_m']) * 0.5 * render_width_scale * 0.45, wheel_outer - float(profile.get('rear_climb_assist_inner_offset_m', 0.03)) * render_width_scale)
    mount_x = -body_half_x + float(profile.get('rear_climb_assist_mount_offset_x_m', 0.03))
    mount_y = float(profile.get('rear_climb_assist_mount_height_m', float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.92))
    foot_x = rear_wheel_x
    foot_y = wheel_radius
    upper_length = float(profile.get('rear_climb_assist_upper_length_m', 0.09))
    lower_length = float(profile.get('rear_climb_assist_lower_length_m', 0.08))
    joint_x, joint_y = _resolve_two_link_joint((mount_x, mount_y), (foot_x, foot_y), upper_length, lower_length)
    return {
        'mount': (mount_x, mount_y),
        'joint': (joint_x, joint_y),
        'foot': (foot_x, foot_y),
        'side_offset': side_offset,
    }


def _balance_leg_geometry(profile, render_width_scale=1.0):
    body_half_x = float(profile['body_length_m']) * 0.5
    wheel_radius = max(0.018, float(profile['wheel_radius_m']))
    wheel_positions = profile.get('custom_wheel_positions_m', [])
    foot_x = min(
        (float(position[0]) for position in wheel_positions if isinstance(position, (list, tuple)) and len(position) >= 2),
        default=-body_half_x * 0.78,
    )
    foot_x += float(profile.get('_preview_rear_foot_reach_m', 0.0))
    foot_y = wheel_radius + float(profile.get('_preview_rear_foot_raise_m', 0.0))
    wheel_outer = max(
        (abs(float(position[1])) * render_width_scale for position in wheel_positions if isinstance(position, (list, tuple)) and len(position) >= 2),
        default=float(profile['body_width_m']) * 0.5 * render_width_scale + wheel_radius * 0.55,
    )
    side_offset = max(
        float(profile['body_width_m']) * 0.5 * render_width_scale * 0.45,
        wheel_outer - float(profile.get('rear_climb_assist_inner_offset_m', 0.03)) * render_width_scale,
    )
    upper_anchor_x = -body_half_x + float(profile.get('rear_climb_assist_mount_offset_x_m', 0.03))
    upper_anchor_y = float(profile.get('rear_climb_assist_mount_height_m', float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.92))
    rearward_clearance = max(0.02, float(profile.get('rear_climb_assist_upper_length_m', 0.09)) * 0.14)
    foot_x = min(foot_x, upper_anchor_x - rearward_clearance)
    foot_x, foot_y = _clamp_two_link_target_point(
        (upper_anchor_x, upper_anchor_y),
        (foot_x, foot_y),
        float(profile.get('rear_climb_assist_upper_length_m', 0.09)),
        float(profile.get('rear_climb_assist_lower_length_m', 0.08)),
        profile.get('rear_climb_assist_knee_min_deg', 42.0),
        profile.get('rear_climb_assist_knee_max_deg', 132.0),
    )
    knee_x, knee_y = _select_balance_leg_joint(
        (upper_anchor_x, upper_anchor_y),
        (foot_x, foot_y),
        float(profile.get('rear_climb_assist_upper_length_m', 0.09)),
        float(profile.get('rear_climb_assist_lower_length_m', 0.08)),
        profile.get('rear_climb_assist_knee_direction', 'rear'),
    )
    upper_pair_gap = max(0.02, float(profile.get('rear_climb_assist_upper_pair_gap_m', 0.06)))
    hinge_radius = max(0.008, float(profile.get('rear_climb_assist_hinge_radius_m', 0.016)))
    half_gap = upper_pair_gap * 0.5
    return {
        'upper_anchor': (upper_anchor_x, upper_anchor_y),
        'upper_front': (upper_anchor_x + half_gap, upper_anchor_y),
        'upper_rear': (upper_anchor_x - half_gap, upper_anchor_y),
        'knee_center': (knee_x, knee_y),
        'knee_front': (knee_x + half_gap, knee_y),
        'knee_rear': (knee_x - half_gap, knee_y),
        'foot': (foot_x, foot_y),
        'side_offset': side_offset,
        'upper_pair_gap': upper_pair_gap,
        'hinge_radius': hinge_radius,
    }


def _append_preview_face(vertices, p0, p1, p2, p3, color, normal):
    vertices.extend((*p0, *color, *normal, *p1, *color, *normal, *p2, *color, *normal))
    vertices.extend((*p0, *color, *normal, *p2, *color, *normal, *p3, *color, *normal))


def _append_preview_triangle(vertices, p0, p1, p2, color, normal):
    vertices.extend((*p0, *color, *normal, *p1, *color, *normal, *p2, *color, *normal))


def _append_preview_box(vertices, center, half_extents, color_rgb, yaw_rad=0.0):
    cx, cy, cz = center
    half_x, half_y, half_z = half_extents
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    cos_yaw = math.cos(yaw_rad)
    sin_yaw = math.sin(yaw_rad)

    def rotate_point(point):
        point_x, point_y, point_z = point
        return (
            cx + point_x * cos_yaw - point_z * sin_yaw,
            cy + point_y,
            cz + point_x * sin_yaw + point_z * cos_yaw,
        )

    def rotate_normal(normal):
        normal_x, normal_y, normal_z = normal
        return (
            normal_x * cos_yaw - normal_z * sin_yaw,
            normal_y,
            normal_x * sin_yaw + normal_z * cos_yaw,
        )

    corners = {
        'lbn': rotate_point((-half_x, -half_y, -half_z)),
        'rbn': rotate_point((half_x, -half_y, -half_z)),
        'rbs': rotate_point((half_x, -half_y, half_z)),
        'lbs': rotate_point((-half_x, -half_y, half_z)),
        'ltn': rotate_point((-half_x, half_y, -half_z)),
        'rtn': rotate_point((half_x, half_y, -half_z)),
        'rts': rotate_point((half_x, half_y, half_z)),
        'lts': rotate_point((-half_x, half_y, half_z)),
    }
    face_specs = (
        (('ltn', 'rtn', 'rts', 'lts'), (0.0, 1.0, 0.0), 1.0),
        (('lbs', 'rbs', 'rbn', 'lbn'), (0.0, -1.0, 0.0), 0.42),
        (('lbn', 'rbn', 'rtn', 'ltn'), (0.0, 0.0, -1.0), 0.68),
        (('rbs', 'lbs', 'lts', 'rts'), (0.0, 0.0, 1.0), 0.82),
        (('rbn', 'rbs', 'rts', 'rtn'), (1.0, 0.0, 0.0), 0.76),
        (('lbs', 'lbn', 'ltn', 'lts'), (-1.0, 0.0, 0.0), 0.60),
    )
    for corner_keys, normal, shade in face_specs:
        shaded_color = tuple(max(0.0, min(1.0, channel * shade)) for channel in color)
        rotated_normal = rotate_normal(normal)
        _append_preview_face(
            vertices,
            corners[corner_keys[0]],
            corners[corner_keys[1]],
            corners[corner_keys[2]],
            corners[corner_keys[3]],
            shaded_color,
            rotated_normal,
        )


def _preview_face_normal(p0, p1, p2):
    vec1 = np.array(p1, dtype='f4') - np.array(p0, dtype='f4')
    vec2 = np.array(p2, dtype='f4') - np.array(p0, dtype='f4')
    normal = np.cross(vec1, vec2)
    norm = np.linalg.norm(normal)
    if norm <= 1e-6:
        return (0.0, 1.0, 0.0)
    return tuple((normal / norm).tolist())


def _append_preview_prism(vertices, bottom_points, top_points, color_rgb, yaw_rad=0.0):
    if len(bottom_points) != len(top_points) or len(bottom_points) < 3:
        return
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    cos_yaw = math.cos(yaw_rad)
    sin_yaw = math.sin(yaw_rad)

    def rotate_point(point):
        point_x, point_y, point_z = point
        return (
            point_x * cos_yaw - point_z * sin_yaw,
            point_y,
            point_x * sin_yaw + point_z * cos_yaw,
        )

    rotated_bottom = [rotate_point(point) for point in bottom_points]
    rotated_top = [rotate_point(point) for point in top_points]
    top_normal = _preview_face_normal(rotated_top[0], rotated_top[1], rotated_top[2])
    bottom_normal = _preview_face_normal(rotated_bottom[2], rotated_bottom[1], rotated_bottom[0])
    for index in range(1, len(rotated_top) - 1):
        _append_preview_triangle(vertices, rotated_top[0], rotated_top[index], rotated_top[index + 1], color, top_normal)
    bottom_color = tuple(max(0.0, channel * 0.42) for channel in color)
    for index in range(1, len(rotated_bottom) - 1):
        _append_preview_triangle(vertices, rotated_bottom[0], rotated_bottom[index + 1], rotated_bottom[index], bottom_color, bottom_normal)
    for index in range(len(rotated_bottom)):
        next_index = (index + 1) % len(rotated_bottom)
        shade = 0.60 + 0.22 * (((index % 4) + 1) / 4.0)
        p0 = rotated_bottom[index]
        p1 = rotated_bottom[next_index]
        p2 = rotated_top[next_index]
        p3 = rotated_top[index]
        normal = _preview_face_normal(p0, p1, p2)
        shaded_color = tuple(max(0.0, min(1.0, channel * shade)) for channel in color)
        _append_preview_face(vertices, p0, p1, p2, p3, shaded_color, normal)


def _append_preview_trapezoid_plate(vertices, center, top_length, bottom_length, height, thickness, color_rgb, yaw_rad=0.0):
    cx, cy, cz = center
    half_top = max(0.001, float(top_length) * 0.5)
    half_bottom = max(0.001, float(bottom_length) * 0.5)
    half_height = max(0.001, float(height) * 0.5)
    half_thickness = max(0.001, float(thickness) * 0.5)
    rear_x = cx - half_bottom
    front_top_x = rear_x + float(top_length)
    front_bottom_x = rear_x + float(bottom_length)
    bottom_points = [
        (rear_x, cy - half_height, cz - half_thickness),
        (front_bottom_x, cy - half_height, cz - half_thickness),
        (front_bottom_x, cy - half_height, cz + half_thickness),
        (rear_x, cy - half_height, cz + half_thickness),
    ]
    top_points = [
        (rear_x, cy + half_height, cz - half_thickness),
        (front_top_x, cy + half_height, cz - half_thickness),
        (front_top_x, cy + half_height, cz + half_thickness),
        (rear_x, cy + half_height, cz + half_thickness),
    ]
    _append_preview_prism(vertices, bottom_points, top_points, color_rgb, yaw_rad=yaw_rad)


def _append_preview_beam(vertices, start_point, end_point, height, thickness, color_rgb, yaw_rad=0.0):
    start_x, start_y, start_z = start_point
    end_x, end_y, end_z = end_point
    delta_x = end_x - start_x
    delta_y = end_y - start_y
    length = math.hypot(delta_x, delta_y)
    if length <= 1e-6:
        return
    side_x = -delta_y / length
    side_y = delta_x / length
    half_height = max(0.001, float(height) * 0.5)
    half_thickness = max(0.001, float(thickness) * 0.5)
    bottom_points = [
        (start_x + side_x * half_height, start_y + side_y * half_height, start_z - half_thickness),
        (end_x + side_x * half_height, end_y + side_y * half_height, end_z - half_thickness),
        (end_x - side_x * half_height, end_y - side_y * half_height, end_z - half_thickness),
        (start_x - side_x * half_height, start_y - side_y * half_height, start_z - half_thickness),
    ]
    top_points = [
        (start_x + side_x * half_height, start_y + side_y * half_height, start_z + half_thickness),
        (end_x + side_x * half_height, end_y + side_y * half_height, end_z + half_thickness),
        (end_x - side_x * half_height, end_y - side_y * half_height, end_z + half_thickness),
        (start_x - side_x * half_height, start_y - side_y * half_height, start_z + half_thickness),
    ]
    _append_preview_prism(vertices, bottom_points, top_points, color_rgb, yaw_rad=yaw_rad)


def _append_preview_cylinder(vertices, center, radius, half_width, color_rgb, segments=12):
    cx, cy, cz = center
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    front_ring = []
    back_ring = []
    for index in range(segments):
        angle = (math.pi * 2.0 * index) / max(segments, 3)
        ring_x = math.cos(angle) * radius
        ring_y = math.sin(angle) * radius
        front_ring.append((cx + ring_x, cy + ring_y, cz - half_width))
        back_ring.append((cx + ring_x, cy + ring_y, cz + half_width))
    front_center = (cx, cy, cz - half_width)
    back_center = (cx, cy, cz + half_width)
    for index in range(segments):
        next_index = (index + 1) % segments
        normal_a = np.array(front_ring[index]) - np.array(front_center)
        normal_b = np.array(front_ring[next_index]) - np.array(front_center)
        average = normal_a + normal_b
        norm = np.linalg.norm(average)
        side_normal = tuple((average / norm).tolist()) if norm > 1e-6 else (1.0, 0.0, 0.0)
        _append_preview_face(vertices, front_ring[index], front_ring[next_index], back_ring[next_index], back_ring[index], color, side_normal)
        _append_preview_face(vertices, front_center, front_ring[next_index], front_ring[index], front_center, tuple(max(0.0, channel * 0.84) for channel in color), (0.0, 0.0, -1.0))
        _append_preview_face(vertices, back_center, back_ring[index], back_ring[next_index], back_center, tuple(max(0.0, channel * 0.94) for channel in color), (0.0, 0.0, 1.0))


def _rotate_xz(point_x, point_z, yaw_rad):
    cos_yaw = math.cos(yaw_rad)
    sin_yaw = math.sin(yaw_rad)
    return (
        point_x * cos_yaw - point_z * sin_yaw,
        point_x * sin_yaw + point_z * cos_yaw,
    )


def _body_outline_points(profile):
    render_width_scale = float(profile.get('body_render_width_scale', 0.82))
    half_x = float(profile['body_length_m']) * 0.5
    half_z = float(profile['body_width_m']) * 0.5 * render_width_scale
    if str(profile.get('body_shape', 'box')) != 'octagon':
        return [(-half_x, -half_z), (half_x, -half_z), (half_x, half_z), (-half_x, half_z)]
    chamfer = min(half_x, half_z) * 0.34
    return [
        (-half_x + chamfer, -half_z),
        (half_x - chamfer, -half_z),
        (half_x, -half_z + chamfer),
        (half_x, half_z - chamfer),
        (half_x - chamfer, half_z),
        (-half_x + chamfer, half_z),
        (-half_x, half_z - chamfer),
        (-half_x, -half_z + chamfer),
    ]


def _resolved_wheel_centers(profile):
    return [component['center'] for component in _resolved_wheel_components(profile)]


def _resolved_wheel_components(profile):
    render_width_scale = float(profile.get('body_render_width_scale', 0.82))
    orbit_values = list(profile.get('wheel_orbit_yaws_deg', []))
    self_values = list(profile.get('wheel_self_yaws_deg', orbit_values))
    rear_climb_style = str(profile.get('rear_climb_assist_style', 'none'))
    leg_geometry = _balance_leg_geometry(profile, render_width_scale) if rear_climb_style == 'balance_leg' else None
    raw_positions = [position for position in profile.get('custom_wheel_positions_m', []) if isinstance(position, (list, tuple)) and len(position) >= 2]
    dynamic_indices = set()
    if leg_geometry is not None:
        if str(profile.get('wheel_style', 'standard')) == 'legged' or len(raw_positions) <= 2:
            dynamic_indices = set(range(len(raw_positions)))
        else:
            dynamic_count = max(2, len(raw_positions) // 2)
            dynamic_indices = set(sorted(range(len(raw_positions)), key=lambda index: float(raw_positions[index][0]))[:dynamic_count])
    components = []
    for index, position in enumerate(raw_positions):
        center_height_m = float(profile['wheel_radius_m'])
        if leg_geometry is not None and index in dynamic_indices:
            side_sign = -1.0 if float(position[1]) < 0.0 else 1.0
            center_x = float(leg_geometry['foot'][0])
            center_z = float(leg_geometry['side_offset']) * side_sign
            center_height_m = float(leg_geometry['foot'][1])
        else:
            orbit_rad = math.radians(float(orbit_values[index])) if index < len(orbit_values) else 0.0
            center_x, center_z = _rotate_xz(float(position[0]), float(position[1]) * render_width_scale, orbit_rad)
        spin_deg = float(self_values[index]) if index < len(self_values) else 0.0
        components.append({'center': (center_x, center_z), 'spin_rad': math.radians(spin_deg), 'center_height_m': center_height_m})
    return components


def _resolved_armor_components(profile):
    render_width_scale = float(profile.get('body_render_width_scale', 0.82))
    body_half_x = float(profile['body_length_m']) * 0.5
    body_half_z = float(profile['body_width_m']) * 0.5 * render_width_scale
    armor_gap = float(profile.get('armor_plate_gap_m', 0.005))
    armor_thickness = max(0.012, armor_gap * 0.75)
    armor_center_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.55
    radius_x = body_half_x + armor_gap + armor_thickness * 1.35
    radius_z = body_half_z + armor_gap + armor_thickness * 1.35
    orbit_values = list(profile.get('armor_orbit_yaws_deg', [0.0, 180.0, 90.0, 270.0]))
    self_values = list(profile.get('armor_self_yaws_deg', orbit_values))
    components = []
    for index in range(4):
        orbit_deg = float(orbit_values[index]) if index < len(orbit_values) else 0.0
        self_deg = float(self_values[index]) if index < len(self_values) else orbit_deg
        orbit_rad = math.radians(orbit_deg)
        components.append({
            'center': (math.cos(orbit_rad) * radius_x, armor_center_y, math.sin(orbit_rad) * radius_z),
            'yaw_rad': math.radians(self_deg),
        })
    return components


def _resolved_armor_light_components(profile):
    armor_components = _resolved_armor_components(profile)
    armor_half_width = float(profile.get('armor_plate_width_m', 0.16)) * 0.5
    light_half_width = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
    light_offset = armor_half_width + light_half_width + max(0.004, float(profile.get('armor_plate_gap_m', 0.005)) * 0.15)
    light_components = []
    for component in armor_components:
        offset_x, offset_z = _rotate_xz(0.0, light_offset, float(component['yaw_rad']))
        center_x, center_y, center_z = component['center']
        light_components.append({
            'center_a': (center_x + offset_x, center_y, center_z + offset_z),
            'center_b': (center_x - offset_x, center_y, center_z - offset_z),
            'yaw_rad': float(component['yaw_rad']),
        })
    return light_components


class ModernGLAppearancePreview:
    def __init__(self):
        self.ctx: Any = None
        self.program: Any = None
        self.framebuffer: Any = None
        self.framebuffer_size = None
        self.vbo: Any = None
        self.vao: Any = None
        self.geometry_key = None
        self.bounds_radius = 1.0
        self.error = MODERNGL_PREVIEW_ERROR
        if moderngl is None:
            return
        try:
            self.ctx = moderngl.create_standalone_context()
            self.program = self.ctx.program(
                vertex_shader='''
                    #version 330
                    in vec3 in_position;
                    in vec3 in_color;
                    in vec3 in_normal;
                    uniform mat4 u_mvp;
                    uniform vec3 u_light_dir;
                    out vec3 v_color;
                    void main() {
                        vec3 normal = normalize(in_normal);
                        float light = 0.38 + max(dot(normal, normalize(u_light_dir)), 0.0) * 0.62;
                        v_color = in_color * light;
                        gl_Position = u_mvp * vec4(in_position, 1.0);
                    }
                ''',
                fragment_shader='''
                    #version 330
                    in vec3 v_color;
                    out vec4 fragColor;
                    void main() {
                        fragColor = vec4(v_color, 1.0);
                    }
                ''',
            )
            self.error = None
        except Exception as exc:
            self.error = str(exc)
            self.ctx = None
            self.program = None

    def _ensure_framebuffer(self, size):
        if self.ctx is None:
            return False
        if self.framebuffer is not None and self.framebuffer_size == size:
            return True
        if self.framebuffer is not None:
            self.framebuffer.release()
        self.framebuffer = self.ctx.simple_framebuffer(size)
        self.framebuffer_size = size
        return True

    def _profile_geometry_key(self, profile):
        return json.dumps(profile, sort_keys=True, ensure_ascii=True)

    def _build_geometry(self, profile):
        vertices = []
        render_width_scale = float(profile.get('body_render_width_scale', 0.82))
        has_turret = float(profile.get('gimbal_length_m', 0.0)) > 1e-6 and float(profile.get('gimbal_body_height_m', 0.0)) > 1e-6
        has_barrel = has_turret and float(profile.get('barrel_length_m', 0.0)) > 1e-6 and float(profile.get('barrel_radius_m', 0.0)) > 1e-6
        has_front_climb = str(profile.get('front_climb_assist_style', 'none')) != 'none'
        has_rear_climb = str(profile.get('rear_climb_assist_style', 'none')) != 'none'
        body_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.5
        body_half_height = float(profile['body_height_m']) * 0.5
        body_outline = _body_outline_points(profile)
        if str(profile.get('body_shape', 'box')) == 'octagon':
            _append_preview_prism(
                vertices,
                [(point_x, body_y - body_half_height, point_z) for point_x, point_z in body_outline],
                [(point_x, body_y + body_half_height, point_z) for point_x, point_z in body_outline],
                profile['body_color_rgb'],
            )
            top_scale = 0.78
            top_outline = [(point_x * top_scale, point_z * top_scale) for point_x, point_z in body_outline]
            cap_half_height = max(0.015, float(profile['body_height_m']) * 0.12)
            cap_center_y = body_y + float(profile['body_height_m']) * 0.36
            _append_preview_prism(
                vertices,
                [(point_x, cap_center_y - cap_half_height, point_z) for point_x, point_z in top_outline],
                [(point_x, cap_center_y + cap_half_height, point_z) for point_x, point_z in top_outline],
                [max(0, min(255, int(channel * 0.82 + 20))) for channel in profile['body_color_rgb']],
            )
        else:
            _append_preview_box(
                vertices,
                (0.0, body_y, 0.0),
                (float(profile['body_length_m']) * 0.5, body_half_height, float(profile['body_width_m']) * 0.5 * render_width_scale),
                profile['body_color_rgb'],
            )
            _append_preview_box(
                vertices,
                (0.0, body_y + float(profile['body_height_m']) * 0.36, 0.0),
                (float(profile['body_length_m']) * 0.40, max(0.015, float(profile['body_height_m']) * 0.12), float(profile['body_width_m']) * 0.40 * render_width_scale),
                [max(0, min(255, int(channel * 0.82 + 20))) for channel in profile['body_color_rgb']],
            )

        wheel_radius = max(0.018, float(profile['wheel_radius_m']))
        wheel_half_z = max(0.018, float(profile['wheel_radius_m']) * (0.22 if str(profile.get('wheel_style', 'standard')) == 'omni' else 0.32))
        for wheel_component in _resolved_wheel_components(profile):
            wheel_x, wheel_z = wheel_component['center']
            wheel_center_y = float(wheel_component.get('center_height_m', wheel_radius))
            _append_preview_cylinder(
                vertices,
                (float(wheel_x), wheel_center_y, float(wheel_z)),
                wheel_radius,
                wheel_half_z,
                profile['wheel_color_rgb'],
            )
            spoke_dx, spoke_dy = _rotate_xz(wheel_radius * 0.72, 0.0, float(wheel_component['spin_rad']))
            _append_preview_beam(
                vertices,
                (float(wheel_x) - spoke_dx, wheel_center_y - spoke_dy, float(wheel_z)),
                (float(wheel_x) + spoke_dx, wheel_center_y + spoke_dy, float(wheel_z)),
                max(0.006, wheel_radius * 0.18),
                max(0.006, wheel_half_z * 0.70),
                [188, 192, 198],
            )
            marker_dx, marker_dy = _rotate_xz(wheel_radius * 0.56, wheel_radius * 0.18, float(wheel_component['spin_rad']))
            marker_half = max(0.005, wheel_radius * 0.12)
            marker_half_z = max(0.004, wheel_half_z * 0.22)
            _append_preview_box(
                vertices,
                (float(wheel_x) + marker_dx, wheel_center_y + marker_dy, float(wheel_z)),
                (max(0.004, marker_half * 0.70), max(0.004, marker_half * 0.70), max(0.004, wheel_half_z * 0.88)),
                [236, 182, 84],
            )
            for marker_sign in (-1.0, 1.0):
                _append_preview_box(
                    vertices,
                    (float(wheel_x) + marker_dx, wheel_center_y + marker_dy, float(wheel_z) + wheel_half_z * 0.72 * marker_sign),
                    (marker_half, marker_half, marker_half_z),
                    [236, 182, 84],
                )

        body_half_x = float(profile['body_length_m']) * 0.5
        body_half_z = float(profile['body_width_m']) * 0.5 * render_width_scale
        wheel_outer_z = max((abs(float(wheel_y)) * render_width_scale for _, wheel_y in profile.get('custom_wheel_positions_m', [])), default=body_half_z + wheel_radius * 0.55)
        if has_front_climb:
            plate_top_length, plate_bottom_length = _front_climb_lengths(profile)
            plate_width = float(profile.get('front_climb_assist_plate_width_m', 0.018))
            plate_height = float(profile.get('front_climb_assist_plate_height_m', 0.18))
            plate_forward = float(profile.get('front_climb_assist_forward_offset_m', 0.04))
            plate_inner = float(profile.get('front_climb_assist_inner_offset_m', 0.06)) * render_width_scale
            plate_center_x = body_half_x + plate_forward + plate_bottom_length * 0.5
            plate_center_y = wheel_radius + plate_height * 0.5 - float(profile.get('_preview_front_drop_m', 0.0)) * 0.5 + float(profile.get('_preview_front_raise_m', 0.0)) * 0.2
            plate_center_z = max(body_half_z * 0.45, wheel_outer_z - plate_inner)
            for side_sign in (-1.0, 1.0):
                _append_preview_trapezoid_plate(vertices, (plate_center_x, plate_center_y, plate_center_z * side_sign), plate_top_length, plate_bottom_length, plate_height, plate_width, [92, 96, 108])
                _append_preview_box(vertices, (body_half_x * 0.78, body_y + float(profile['body_height_m']) * 0.22, plate_center_z * side_sign), (plate_bottom_length * 0.28, max(0.012, plate_height * 0.18), plate_width * 0.6), [122, 126, 136])

        if has_rear_climb:
            if str(profile.get('rear_climb_assist_style', 'none')) == 'balance_leg':
                leg_geometry = _balance_leg_geometry(profile, render_width_scale)
                upper_width = float(profile.get('rear_climb_assist_upper_width_m', 0.016))
                upper_height = float(profile.get('rear_climb_assist_upper_height_m', 0.016))
                lower_width = float(profile.get('rear_climb_assist_lower_width_m', 0.016))
                lower_height = float(profile.get('rear_climb_assist_lower_height_m', 0.016))
                hinge_radius = float(leg_geometry['hinge_radius'])
                for side_sign in (-1.0, 1.0):
                    side_z = float(leg_geometry['side_offset']) * side_sign
                    _append_preview_beam(vertices, (*leg_geometry['upper_front'], side_z), (*leg_geometry['knee_front'], side_z), upper_height, upper_width, [112, 118, 132])
                    _append_preview_beam(vertices, (*leg_geometry['upper_rear'], side_z), (*leg_geometry['knee_rear'], side_z), upper_height, upper_width, [102, 108, 122])
                    _append_preview_beam(vertices, (*leg_geometry['knee_center'], side_z), (*leg_geometry['foot'], side_z), lower_height, lower_width, [90, 96, 108])
                    for hinge_point in (leg_geometry['upper_front'], leg_geometry['upper_rear'], leg_geometry['knee_front'], leg_geometry['knee_rear']):
                        _append_preview_cylinder(vertices, (hinge_point[0], hinge_point[1], side_z), hinge_radius, max(0.004, upper_width * 0.55), [148, 154, 168], segments=10)
                    foot_hub_half = max(0.004, hinge_radius / math.sqrt(2.0))
                    _append_preview_box(vertices, (leg_geometry['foot'][0], leg_geometry['foot'][1], side_z), (foot_hub_half, foot_hub_half, max(0.004, wheel_half_z * 0.55)), [148, 154, 168])
            else:
                rear_points = _rear_climb_points(profile, render_width_scale)
                upper_width = float(profile.get('rear_climb_assist_upper_width_m', 0.016))
                upper_height = float(profile.get('rear_climb_assist_upper_height_m', 0.016))
                lower_width = float(profile.get('rear_climb_assist_lower_width_m', 0.016))
                lower_height = float(profile.get('rear_climb_assist_lower_height_m', 0.016))
                for side_sign in (-1.0, 1.0):
                    side_z = float(rear_points['side_offset']) * side_sign
                    _append_preview_beam(vertices, (rear_points['mount'][0], rear_points['mount'][1], side_z), (rear_points['joint'][0], rear_points['joint'][1], side_z), upper_height, upper_width, [106, 110, 120])
                    _append_preview_beam(vertices, (rear_points['joint'][0], rear_points['joint'][1], side_z), (rear_points['foot'][0], rear_points['foot'][1], side_z), lower_height, lower_width, [92, 96, 108])
                    _append_preview_box(vertices, (rear_points['joint'][0], rear_points['joint'][1], side_z), (max(upper_height, lower_height) * 0.75, max(upper_height, lower_height) * 0.75, max(upper_width, lower_width) * 0.55), [116, 120, 132])

        armor_half_h = float(profile['armor_plate_height_m']) * 0.5
        armor_color = profile['armor_color_rgb']
        armor_thickness = max(0.012, float(profile.get('armor_plate_gap_m', 0.005)) * 0.75)
        armor_half_width = float(profile['armor_plate_width_m']) * 0.5
        for component in _resolved_armor_components(profile):
            _append_preview_box(
                vertices,
                component['center'],
                (armor_thickness * 0.5, armor_half_h, armor_half_width),
                armor_color,
                yaw_rad=float(component['yaw_rad']),
            )
        armor_light_color = [110, 168, 255]
        armor_light_half_x = float(profile.get('armor_light_length_m', 0.10)) * 0.5
        armor_light_half_y = max(0.005, float(profile.get('armor_light_height_m', 0.02)) * 0.5)
        armor_light_half_z = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
        for component in _resolved_armor_light_components(profile):
            _append_preview_box(vertices, component['center_a'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), armor_light_color, yaw_rad=float(component['yaw_rad']))
            _append_preview_box(vertices, component['center_b'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), armor_light_color, yaw_rad=float(component['yaw_rad']))

        if has_turret:
            turret_offset_x = float(profile['gimbal_offset_x_m'])
            turret_offset_z = float(profile['gimbal_offset_y_m'])
            mount_center_y = _profile_mount_center_height(profile)
            turret_center_y = _profile_turret_center_height(profile)
            if (float(profile.get('gimbal_mount_height_m', 0.0)) + float(profile.get('gimbal_mount_gap_m', 0.0))) > 1e-6:
                connector_half_height = max(0.02, (float(profile.get('gimbal_mount_gap_m', 0.0)) + float(profile.get('gimbal_mount_height_m', 0.0))) * 0.5)
                _append_preview_box(
                    vertices,
                    (turret_offset_x, mount_center_y, turret_offset_z),
                    (max(0.02, float(profile['gimbal_mount_length_m']) * 0.5), connector_half_height, max(0.02, float(profile['gimbal_mount_width_m']) * 0.5 * render_width_scale)),
                    [96, 100, 112],
                )
            _append_preview_box(
                vertices,
                (turret_offset_x, turret_center_y, turret_offset_z),
                (float(profile['gimbal_length_m']) * 0.5, float(profile['gimbal_body_height_m']) * 0.5, float(profile['gimbal_width_m']) * 0.5 * render_width_scale),
                profile['turret_color_rgb'],
            )
            if has_barrel:
                barrel_length = float(profile['barrel_length_m'])
                barrel_radius = max(0.005, float(profile['barrel_radius_m']))
                _append_preview_box(
                    vertices,
                    (turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + barrel_length * 0.5, turret_center_y, turret_offset_z),
                    (barrel_length * 0.5, barrel_radius, barrel_radius),
                    profile['turret_color_rgb'],
                )
                barrel_light_half_x = float(profile.get('barrel_light_length_m', 0.10)) * 0.5
                barrel_light_half_y = max(0.005, float(profile.get('barrel_light_height_m', 0.02)) * 0.5)
                barrel_light_half_z = max(0.005, float(profile.get('barrel_light_width_m', 0.02)) * 0.5)
                barrel_light_center_x = turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + barrel_length * 0.45
                _append_preview_box(vertices, (barrel_light_center_x, turret_center_y, turret_offset_z + barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z), armor_light_color)
                _append_preview_box(vertices, (barrel_light_center_x, turret_center_y, turret_offset_z - barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z), armor_light_color)

        if str(profile.get('arm_style', 'none')) == 'fixed_7':
            _append_preview_box(vertices, (0.0, body_y + float(profile['body_height_m']) * 0.95, 0.0), (0.03, 0.22, 0.03), [172, 176, 184])
            _append_preview_box(vertices, (float(profile['body_length_m']) * 0.16, body_y + float(profile['body_height_m']) + 0.18, 0.0), (0.18, 0.03, 0.03), [188, 192, 198])

        vertex_array = np.array(vertices, dtype='f4')
        self.bounds_radius = max(
            0.6,
            float(profile['body_length_m']) * 0.9,
            float(profile['body_width_m']) * 0.9,
            float(profile.get('gimbal_length_m', 0.0)) + float(profile.get('barrel_length_m', 0.0)) * 0.8,
            _profile_turret_center_height(profile) + 0.25,
        )
        if self.vao is not None:
            self.vao.release()
        if self.vbo is not None:
            self.vbo.release()
        self.vbo = self.ctx.buffer(vertex_array.tobytes())
        self.vao = self.ctx.vertex_array(self.program, [(self.vbo, '3f 3f 3f', 'in_position', 'in_color', 'in_normal')])

    def render_scene(self, profile, size, yaw=0.72, pitch=0.42):
        width, height = int(size[0]), int(size[1])
        if width <= 1 or height <= 1:
            return None
        if moderngl is None or self.ctx is None or self.program is None or not self._ensure_framebuffer((width, height)):
            return None
        ctx = cast(Any, self.ctx)
        program = cast(Any, self.program)
        framebuffer = cast(Any, self.framebuffer)
        mgl = cast(Any, moderngl)
        geometry_key = self._profile_geometry_key(profile)
        if geometry_key != self.geometry_key:
            self._build_geometry(profile)
            self.geometry_key = geometry_key
        vao = cast(Any, self.vao)
        if vao is None:
            return None

        target = np.array([0.0, float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.45, 0.0], dtype='f4')
        distance = max(1.4, self.bounds_radius * 2.9)
        eye = np.array([
            math.sin(yaw) * math.cos(pitch) * distance,
            math.sin(pitch) * distance + self.bounds_radius * 0.25,
            math.cos(yaw) * math.cos(pitch) * distance,
        ], dtype='f4') + target
        projection = _terrain_scene_perspective_matrix(math.radians(42.0), width / max(height, 1), 0.05, max(8.0, distance * 6.0))
        view = _terrain_scene_look_at(eye, target, np.array([0.0, 1.0, 0.0], dtype='f4'))
        mvp = projection @ view

        framebuffer.use()
        framebuffer.clear(0.08, 0.10, 0.13, 1.0)
        ctx.enable(mgl.DEPTH_TEST)
        ctx.disable(mgl.CULL_FACE)
        program['u_mvp'].write(mvp.T.astype('f4').tobytes())
        program['u_light_dir'].value = (0.35, 0.92, 0.28)
        vao.render(mgl.TRIANGLES)

        raw = framebuffer.read(components=3, alignment=1)
        return pygame.transform.flip(pygame.image.fromstring(raw, (width, height), 'RGB'), False, True)


class AppearanceEditorApp:
    def __init__(self, config_path='config.json', settings_path=None):
        self.config_path = config_path
        self.config_manager = ConfigManager()
        self.config = self.config_manager.load_config(config_path, settings_path)
        self.config['_config_path'] = config_path
        self.settings_path = self.config.get('_settings_path', self.config_manager.default_settings_path(config_path))
        self.config['_settings_path'] = self.settings_path
        self.preset_path = self._resolve_preset_path()
        self.profiles = self._load_profiles()
        self.current_role = ROLE_ORDER[0][0]
        self.current_infantry_subtype = normalize_infantry_chassis_subtype(self.profiles.get('infantry', {}).get('default_chassis_subtype'))
        self.selected_part = None
        self.selected_field_index = 0
        self.selected_component_scope = 'single'
        self.selected_component_index = 0
        self.status_text = '右侧预览点击部件后编辑，左右方向键调整，直接键入数字可精确输入，Ctrl+S 保存，Tab 切换车型'
        self.running = True
        self.preview_mode = 'split'
        self.preview_action_mode = 'idle'
        self.preview_action_progress = 0.5
        self.preview_3d_yaw = 0.72
        self.preview_3d_pitch = 0.42
        self.field_scroll = 0
        self.field_scroll_drag_active = False
        self.preview_drag_active = False
        self.preview_action_drag_active = False
        self.preview_mode_tabs = []
        self.preview_action_tabs = []
        self.infantry_subtype_tabs = []
        self.preview_part_hitboxes = []
        self.component_control_actions = []
        self.preview_action_slider_track_rect = None
        self.preview_action_slider_thumb_rect = None
        self.field_scrollbar_thumb_rect = None
        self.field_scrollbar_track_rect = None
        self.field_panel_rect = None
        self.preview_panel_rect = None
        self.preview_content_rect = None
        self.active_numeric_input = None

        pygame.init()
        pygame.key.set_repeat(240, 40)
        pygame.display.set_caption('车辆外貌编辑器')
        self.window_width = 1460
        self.window_height = 900
        self.screen = pygame.display.set_mode((self.window_width, self.window_height), pygame.RESIZABLE)
        self.clock = pygame.time.Clock()
        self.title_font = pygame.font.SysFont('microsoftyaheiui', 28)
        self.font = pygame.font.SysFont('microsoftyaheiui', 20)
        self.small_font = pygame.font.SysFont('microsoftyaheiui', 16)
        self.tiny_font = pygame.font.SysFont('microsoftyaheiui', 13)
        self.colors = {
            'bg': (17, 21, 27),
            'panel': (26, 31, 38),
            'panel_alt': (32, 38, 46),
            'panel_border': (82, 92, 106),
            'text': (232, 237, 242),
            'muted': (166, 174, 184),
            'accent': (255, 166, 72),
            'accent_dim': (96, 68, 38),
            'success': (88, 176, 118),
            'danger': (196, 92, 92),
            'preview_bg': (13, 16, 21),
            'grid': (55, 61, 69),
        }
        self.field_specs = self._build_field_specs()
        self.preview_renderer_3d = ModernGLAppearancePreview()

    def _resolve_preset_path(self):
        configured_path = str(self.config.get('entities', {}).get('appearance_preset_path', os.path.join('appearance_presets', 'latest_appearance.json')))
        if os.path.isabs(configured_path):
            return configured_path
        return os.path.join(os.path.dirname(os.path.abspath(self.config_path)), configured_path)

    def _load_profiles(self):
        profiles = {role_key: _default_profile(role_key) for role_key, _ in ROLE_ORDER}
        if os.path.exists(self.preset_path):
            try:
                with open(self.preset_path, 'r', encoding='utf-8') as file:
                    payload = json.load(file)
            except Exception:
                payload = {}
            stored_profiles = payload.get('profiles', {}) if isinstance(payload, dict) else {}
            if isinstance(stored_profiles, dict):
                for role_key in profiles:
                    override = stored_profiles.get(role_key, {})
                    if isinstance(override, dict):
                        profiles[role_key].update(deepcopy(override))
                    profiles[role_key] = _normalize_profile_constraints(role_key, profiles[role_key])
        return profiles

    def _save_profiles(self):
        payload_profiles = {}
        for role_key in list(self.profiles.keys()):
            if role_key == 'infantry':
                store = self._ensure_infantry_profile_store()
                payload_profiles[role_key] = build_infantry_profile_payload(store.get('subtype_profiles', {}), self.current_infantry_subtype)
                self.profiles[role_key] = deepcopy(payload_profiles[role_key])
            else:
                normalized = _normalize_profile_constraints(role_key, self.profiles[role_key])
                payload_profiles[role_key] = normalized
                self.profiles[role_key] = deepcopy(normalized)
        os.makedirs(os.path.dirname(self.preset_path), exist_ok=True)
        with open(self.preset_path, 'w', encoding='utf-8') as file:
            json.dump({'profiles': payload_profiles}, file, ensure_ascii=False, indent=2)
        self.status_text = f'已保存到 {self.preset_path}'

    def _ensure_infantry_profile_store(self):
        container = self.profiles.setdefault('infantry', _default_profile('infantry'))
        default_subtype = normalize_infantry_chassis_subtype(container.get('default_chassis_subtype', container.get('chassis_subtype')))
        current_subtype = normalize_infantry_chassis_subtype(getattr(self, 'current_infantry_subtype', default_subtype) or default_subtype)
        resolved_root = resolve_infantry_subtype_profile(container, default_subtype)
        raw_subtype_profiles = container.get('subtype_profiles')
        subtype_profiles = raw_subtype_profiles if isinstance(raw_subtype_profiles, dict) else {}
        normalized_subprofiles = {}
        for subtype, _label in infantry_chassis_options():
            seed = subtype_profiles.get(subtype)
            if not isinstance(seed, dict):
                seed = resolved_root if subtype == default_subtype else infantry_chassis_preset(subtype)
            merged_seed = deepcopy(seed)
            for color_key, fallback in _default_color_profile().items():
                merged_seed.setdefault(color_key, deepcopy(resolved_root.get(color_key, fallback)))
            normalized_subprofiles[subtype] = _normalize_profile_constraints('infantry', merged_seed, forced_subtype=subtype)
        container = deepcopy(container)
        container['default_chassis_subtype'] = current_subtype
        container['subtype_profiles'] = normalized_subprofiles
        self.profiles['infantry'] = container
        self.current_infantry_subtype = current_subtype
        return container

    def _component_part_count(self, profile, part):
        if part == 'wheel':
            return len(profile.get('custom_wheel_positions_m', []))
        if part in {'armor', 'armor_light'}:
            return 4
        return 0

    def _part_supports_component_selection(self, part):
        return part in {'wheel', 'armor', 'armor_light'}

    def _clamp_selected_component_index(self, profile=None):
        if profile is None:
            profile = self._current_profile()
        count = self._component_part_count(profile, self.selected_part)
        if count <= 0:
            self.selected_component_index = 0
            return 0
        self.selected_component_index = max(0, min(int(self.selected_component_index), count - 1))
        return count

    def _current_component_angle_keys(self):
        mapping = {
            'wheel': ('wheel_orbit_yaws_deg', 'wheel_self_yaws_deg'),
            'armor': ('armor_orbit_yaws_deg', 'armor_self_yaws_deg'),
            'armor_light': ('armor_light_orbit_yaws_deg', 'armor_light_self_yaws_deg'),
        }
        if self.selected_part is None:
            return (None, None)
        return mapping.get(self.selected_part, (None, None))

    def _build_field_specs(self):
        fields = [
            {'part': 'body', 'label': '底盘长度', 'kind': 'number', 'key': 'body_length_m', 'min': 0.30, 'max': 2.00, 'step': 0.01},
            {'part': 'body', 'label': '底盘宽度', 'kind': 'number', 'key': 'body_width_m', 'min': 0.20, 'max': 2.00, 'step': 0.01},
            {'part': 'body', 'label': '视觉宽度系数', 'kind': 'number', 'key': 'body_render_width_scale', 'min': 0.45, 'max': 2.00, 'step': 0.01},
            {'part': 'body', 'label': '底盘高度', 'kind': 'number', 'key': 'body_height_m', 'min': 0.10, 'max': 0.60, 'step': 0.01},
            {'part': 'body', 'label': '离地间隙', 'kind': 'number', 'key': 'body_clearance_m', 'min': 0.02, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台长度', 'kind': 'number', 'key': 'gimbal_length_m', 'min': 0.10, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台宽度', 'kind': 'number', 'key': 'gimbal_width_m', 'min': 0.05, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台厚度', 'kind': 'number', 'key': 'gimbal_body_height_m', 'min': 0.05, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台偏移X', 'kind': 'number', 'key': 'gimbal_offset_x_m', 'min': -0.30, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台偏移Y', 'kind': 'number', 'key': 'gimbal_offset_y_m', 'min': -0.30, 'max': 2.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件长度', 'kind': 'number', 'key': 'gimbal_mount_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件宽度', 'kind': 'number', 'key': 'gimbal_mount_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件高度', 'kind': 'number', 'key': 'gimbal_mount_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管长度', 'kind': 'number', 'key': 'barrel_length_m', 'min': 0.08, 'max': 2.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管半径', 'kind': 'number', 'key': 'barrel_radius_m', 'min': 0.005, 'max': 2.00, 'step': 0.001},
            {'part': 'armor', 'label': '装甲宽度', 'kind': 'number', 'key': 'armor_plate_width_m', 'min': 0.08, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲长度', 'kind': 'number', 'key': 'armor_plate_length_m', 'min': 0.08, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲高度', 'kind': 'number', 'key': 'armor_plate_height_m', 'min': 0.08, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲间距', 'kind': 'number', 'key': 'armor_plate_gap_m', 'min': 0.002, 'max': 2.00, 'step': 0.002},
            {'part': 'armor_light', 'label': '灯条长度', 'kind': 'number', 'key': 'armor_light_length_m', 'min': 0.001, 'max': 2.00, 'step': 0.001},
            {'part': 'armor_light', 'label': '灯条宽度', 'kind': 'number', 'key': 'armor_light_width_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'armor_light', 'label': '灯条高度', 'kind': 'number', 'key': 'armor_light_height_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '灯条长度', 'kind': 'number', 'key': 'barrel_light_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '灯条宽度', 'kind': 'number', 'key': 'barrel_light_width_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '灯条高度', 'kind': 'number', 'key': 'barrel_light_height_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'wheel', 'label': '轮半径', 'kind': 'number', 'key': 'wheel_radius_m', 'min': 0.03, 'max': 2.00, 'step': 0.005},
            {'part': 'front_climb', 'label': '上底宽', 'kind': 'number', 'key': 'front_climb_assist_top_length_m', 'min': 0.02, 'max': 2.00, 'step': 0.005},
            {'part': 'front_climb', 'label': '下底宽', 'kind': 'number', 'key': 'front_climb_assist_bottom_length_m', 'min': 0.01, 'max': 2.00, 'step': 0.005},
            {'part': 'front_climb', 'label': '前板厚度', 'kind': 'number', 'key': 'front_climb_assist_plate_width_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'front_climb', 'label': '前板高度', 'kind': 'number', 'key': 'front_climb_assist_plate_height_m', 'min': 0.05, 'max': 2.00, 'step': 0.005},
            {'part': 'front_climb', 'label': '前板前伸', 'kind': 'number', 'key': 'front_climb_assist_forward_offset_m', 'min': 0.00, 'max': 2.00, 'step': 0.005},
            {'part': 'front_climb', 'label': '前板内缩', 'kind': 'number', 'key': 'front_climb_assist_inner_offset_m', 'min': 0.00, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_climb', 'label': '上连杆长度', 'kind': 'number', 'key': 'rear_climb_assist_upper_length_m', 'min': 0.03, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_climb', 'label': '下腿长度', 'kind': 'number', 'key': 'rear_climb_assist_lower_length_m', 'min': 0.03, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_climb', 'label': '上连杆厚度', 'kind': 'number', 'key': 'rear_climb_assist_upper_width_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '上连杆高度', 'kind': 'number', 'key': 'rear_climb_assist_upper_height_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '下腿厚度', 'kind': 'number', 'key': 'rear_climb_assist_lower_width_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '下腿高度', 'kind': 'number', 'key': 'rear_climb_assist_lower_height_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '上连杆间距', 'kind': 'number', 'key': 'rear_climb_assist_upper_pair_gap_m', 'min': 0.02, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '铰链半径', 'kind': 'number', 'key': 'rear_climb_assist_hinge_radius_m', 'min': 0.008, 'max': 2.00, 'step': 0.002},
            {'part': 'rear_climb', 'label': '上铰点前移', 'kind': 'number', 'key': 'rear_climb_assist_mount_offset_x_m', 'min': 0.00, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_climb', 'label': '上铰点高度', 'kind': 'number', 'key': 'rear_climb_assist_mount_height_m', 'min': 0.02, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_climb', 'label': '铰链内收', 'kind': 'number', 'key': 'rear_climb_assist_inner_offset_m', 'min': 0.00, 'max': 2.00, 'step': 0.005},
        ]
        for color_key, part, label in (
            ('body_color_rgb', 'body', '底盘'),
            ('turret_color_rgb', 'turret', '云台'),
            ('armor_color_rgb', 'armor', '装甲'),
            ('wheel_color_rgb', 'wheel', '车轮'),
        ):
            for channel_index, channel_label in enumerate(('R', 'G', 'B')):
                fields.append({'part': part, 'label': f'{label} {channel_label}', 'kind': 'color', 'color_key': color_key, 'channel': channel_index, 'min': 0, 'max': 255, 'step': 1})
        return fields

    def _profile_has_turret(self, profile):
        return float(profile.get('gimbal_length_m', 0.0)) > 1e-6 and float(profile.get('gimbal_body_height_m', 0.0)) > 1e-6

    def _profile_has_mount(self, profile):
        return self._profile_has_turret(profile) and (float(profile.get('gimbal_mount_height_m', 0.0)) + float(profile.get('gimbal_mount_gap_m', 0.0))) > 1e-6

    def _profile_has_barrel(self, profile):
        return self._profile_has_turret(profile) and float(profile.get('barrel_length_m', 0.0)) > 1e-6 and float(profile.get('barrel_radius_m', 0.0)) > 1e-6

    def _profile_has_front_climb(self, profile):
        return str(profile.get('front_climb_assist_style', 'none')) != 'none'

    def _profile_has_rear_climb(self, profile):
        return str(profile.get('rear_climb_assist_style', 'none')) != 'none'

    def _visible_field_specs(self):
        if self.selected_part is None:
            return []
        profile = self._current_profile()
        self._clamp_selected_component_index(profile)
        if self.selected_part == 'turret' and not self._profile_has_turret(profile):
            return []
        if self.selected_part == 'mount' and not self._profile_has_mount(profile):
            return []
        if self.selected_part in {'barrel', 'barrel_light'} and not self._profile_has_barrel(profile):
            return []
        if self.selected_part == 'front_climb' and not self._profile_has_front_climb(profile):
            return []
        if self.selected_part == 'rear_climb' and not self._profile_has_rear_climb(profile):
            return []
        fields = [spec for spec in self.field_specs if spec.get('part') == self.selected_part]
        if self.selected_part == 'wheel':
            if self.selected_component_scope == 'single' and profile.get('custom_wheel_positions_m'):
                fields.append({'part': 'wheel', 'label': f'轮 {self.selected_component_index + 1} X', 'kind': 'wheel_component', 'component_index': self.selected_component_index, 'axis': 0, 'min': -0.80, 'max': 2.00, 'step': 0.01})
                fields.append({'part': 'wheel', 'label': f'轮 {self.selected_component_index + 1} Y', 'kind': 'wheel_component', 'component_index': self.selected_component_index, 'axis': 1, 'min': -0.80, 'max': 2.00, 'step': 0.01})
        orbit_key, self_key = self._current_component_angle_keys()
        if orbit_key is not None:
            orbit_label = '相对机器人轴心 Yaw'
            self_label = '相对自身轴心 Yaw'
            if self.selected_part == 'wheel':
                orbit_label = '轮安装偏航角'
                self_label = '轮自转角（自身 Z 轴）'
            fields.append({'part': self.selected_part, 'label': orbit_label, 'kind': 'component_angle', 'angle_key': orbit_key, 'min': -180.0, 'max': 180.0, 'step': 1.0})
            fields.append({'part': self.selected_part, 'label': self_label, 'kind': 'component_angle', 'angle_key': self_key, 'min': -180.0, 'max': 180.0, 'step': 1.0})
        return fields

    def _current_profile(self):
        if self.current_role != 'infantry':
            return self.profiles[self.current_role]
        store = self._ensure_infantry_profile_store()
        subtype_profiles = store.get('subtype_profiles', {})
        current_subtype = normalize_infantry_chassis_subtype(self.current_infantry_subtype)
        self.current_infantry_subtype = current_subtype
        return subtype_profiles[current_subtype]

    def _field_value(self, spec):
        profile = self._current_profile()
        if spec['kind'] == 'number':
            return float(profile.get(spec['key'], 0.0))
        if spec['kind'] == 'wheel_component':
            return float(profile['custom_wheel_positions_m'][spec['component_index']][spec['axis']])
        if spec['kind'] == 'component_angle':
            values = profile.get(spec['angle_key'], [])
            if self.selected_component_scope == 'all':
                return float(values[0]) if values else 0.0
            index = max(0, min(self.selected_component_index, len(values) - 1)) if values else 0
            return float(values[index]) if values else 0.0
        return int(profile[spec['color_key']][spec['channel']])

    def _set_field_value(self, spec, value):
        clamped = max(spec['min'], min(spec['max'], value))
        profile = self._current_profile()
        if spec['kind'] == 'number':
            profile[spec['key']] = round(float(clamped), 3)
            if spec['key'] in {'body_length_m', 'body_width_m', 'wheel_radius_m'}:
                self._rebuild_default_wheel_layout_if_needed(profile)
            if self.current_role == 'infantry':
                store = self._ensure_infantry_profile_store()
                store['subtype_profiles'][self.current_infantry_subtype] = _normalize_profile_constraints(self.current_role, profile, forced_subtype=self.current_infantry_subtype)
                store['default_chassis_subtype'] = self.current_infantry_subtype
                self.profiles[self.current_role] = store
            else:
                self.profiles[self.current_role] = _normalize_profile_constraints(self.current_role, profile)
            return
        if spec['kind'] == 'wheel_component':
            profile['custom_wheel_positions_m'][spec['component_index']][spec['axis']] = round(float(clamped), 3)
            return
        if spec['kind'] == 'component_angle':
            values = list(profile.get(spec['angle_key'], []))
            if self.selected_component_scope == 'all':
                values = [round(float(clamped), 3) for _ in values]
            elif values:
                index = max(0, min(self.selected_component_index, len(values) - 1))
                values[index] = round(float(clamped), 3)
            profile[spec['angle_key']] = values
            return
        profile[spec['color_key']][spec['channel']] = int(round(clamped))

    def _rebuild_default_wheel_layout_if_needed(self, profile):
        current = profile.get('custom_wheel_positions_m', [])
        wheel_style = str(profile.get('wheel_style', 'standard'))
        wheel_count = 2 if wheel_style == 'legged' else 4
        if not isinstance(current, list) or len(current) != wheel_count:
            current = []
        wheel_y = round(float(profile['body_width_m']) * 0.5 + float(profile['wheel_radius_m']) * 0.55, 3)
        if wheel_style == 'legged':
            defaults = [
                [0.0, -wheel_y],
                [0.0, wheel_y],
            ]
        elif wheel_style == 'omni':
            wheel_x = round(float(profile['body_length_m']) * 0.36, 3)
            wheel_y = round(float(profile['body_width_m']) * 0.36, 3)
            defaults = [
                [wheel_x, 0.0],
                [0.0, wheel_y],
                [-wheel_x, 0.0],
                [0.0, -wheel_y],
            ]
        else:
            wheel_x = round(float(profile['body_length_m']) * 0.39, 3)
            defaults = [
                [-wheel_x, -wheel_y],
                [wheel_x, -wheel_y],
                [-wheel_x, wheel_y],
                [wheel_x, wheel_y],
            ]
        if not current or all(len(position) < 2 for position in current):
            profile['custom_wheel_positions_m'] = defaults

    def _adjust_selected(self, direction, fast=False):
        visible_fields = self._visible_field_specs()
        if not visible_fields:
            return
        self.selected_field_index = max(0, min(self.selected_field_index, len(visible_fields) - 1))
        spec = visible_fields[self.selected_field_index]
        step = spec['step'] * (5 if fast else 1)
        self._set_field_value(spec, self._field_value(spec) + direction * step)

    def _change_selected_component(self, delta):
        profile = self._current_profile()
        count = self._component_part_count(profile, self.selected_part)
        if count <= 0:
            return
        self.selected_component_index = (self.selected_component_index + int(delta)) % count
        self.active_numeric_input = None

    def _field_content_top_inset(self):
        return 88 if self._part_supports_component_selection(self.selected_part) else 52

    def _infantry_subtype_tab_rects(self):
        if self.current_role != 'infantry':
            return []
        tabs = []
        start_x = 28 + len(ROLE_ORDER) * 122 + 18
        for index, (subtype, label) in enumerate(infantry_chassis_options()):
            tabs.append((subtype, label, pygame.Rect(start_x + index * 160, 72, 148, 40)))
        return tabs

    def _begin_numeric_input(self, initial_text=''):
        visible_fields = self._visible_field_specs()
        if not visible_fields:
            return False
        self.selected_field_index = max(0, min(self.selected_field_index, len(visible_fields) - 1))
        current_spec = visible_fields[self.selected_field_index]
        existing = self.active_numeric_input if isinstance(self.active_numeric_input, dict) else None
        if existing is not None and existing.get('field_index') == self.selected_field_index:
            buffer_text = str(existing.get('buffer', ''))
        else:
            current_value = self._field_value(current_spec)
            buffer_text = str(int(current_value)) if current_spec['kind'] == 'color' else f'{float(current_value):.3f}'.rstrip('0').rstrip('.')
        if initial_text:
            buffer_text = initial_text
        self.active_numeric_input = {'field_index': self.selected_field_index, 'buffer': buffer_text}
        return True

    def _commit_numeric_input(self):
        if not isinstance(self.active_numeric_input, dict):
            return False
        visible_fields = self._visible_field_specs()
        field_index = int(self.active_numeric_input.get('field_index', -1))
        if not (0 <= field_index < len(visible_fields)):
            self.active_numeric_input = None
            return False
        spec = visible_fields[field_index]
        buffer_text = str(self.active_numeric_input.get('buffer', '')).strip()
        if not buffer_text or buffer_text in {'-', '.', '-.'}:
            self.active_numeric_input = None
            return False
        try:
            parsed_value = int(buffer_text) if spec['kind'] == 'color' else float(buffer_text)
        except ValueError:
            self.status_text = f'输入无效: {buffer_text}'
            self.active_numeric_input = None
            return False
        self._set_field_value(spec, parsed_value)
        self.active_numeric_input = None
        return True

    def _handle_numeric_input_keydown(self, event):
        if not isinstance(self.active_numeric_input, dict):
            return False
        if event.key in {pygame.K_RETURN, pygame.K_KP_ENTER}:
            self._commit_numeric_input()
            return True
        if event.key == pygame.K_ESCAPE:
            self.active_numeric_input = None
            return True
        if event.key == pygame.K_BACKSPACE:
            self.active_numeric_input['buffer'] = str(self.active_numeric_input.get('buffer', ''))[:-1]
            return True
        text = str(getattr(event, 'unicode', '') or '')
        if text and text in '0123456789.-':
            buffer_text = str(self.active_numeric_input.get('buffer', ''))
            if text == '-' and buffer_text:
                return True
            if text == '.' and '.' in buffer_text:
                return True
            self.active_numeric_input['buffer'] = buffer_text + text
            return True
        return False

    def _role_tabs(self):
        tabs = []
        start_x = 28
        for role_key, label in ROLE_ORDER:
            tabs.append((role_key, label, pygame.Rect(start_x, 72, 110, 40)))
            start_x += 122
        return tabs

    def _layout_panels(self):
        field_width = max(430, min(620, int(self.window_width * 0.36)))
        preview_x = 24 + field_width + 22
        preview_width = max(420, self.window_width - preview_x - 24)
        panel_height = self.window_height - 188
        self.field_panel_rect = pygame.Rect(24, 126, field_width, panel_height)
        self.preview_panel_rect = pygame.Rect(preview_x, 126, preview_width, panel_height)
        return self.field_panel_rect, self.preview_panel_rect

    def _field_rows(self, rect, scroll_offset=0):
        rows = []
        row_height = 28
        y = self._field_content_top_inset() - int(scroll_offset)
        row_width = rect.width - 30
        visible_fields = self._visible_field_specs()
        for index, spec in enumerate(visible_fields):
            rows.append(('field', spec, pygame.Rect(rect.x + 10, rect.y + y, row_width, row_height), index))
            y += row_height + 4
        content_height = max(0, y + 12)
        return rows, content_height

    def _max_field_scroll(self, rect):
        _, content_height = self._field_rows(rect, scroll_offset=0)
        visible_height = max(1, rect.height - 64)
        return max(0, content_height - visible_height)

    def _set_field_scroll(self, rect, value):
        self.field_scroll = max(0, min(self._max_field_scroll(rect), int(round(value))))

    def _ensure_selected_field_visible(self, rect):
        rows, _ = self._field_rows(rect, scroll_offset=self.field_scroll)
        target_rect = next((row_rect for row_type, _, row_rect, field_index in rows if row_type == 'field' and field_index == self.selected_field_index), None)
        content_top = rect.y + self._field_content_top_inset() - 8
        content_bottom = rect.bottom - 12
        if target_rect is None:
            return
        if target_rect.top < content_top:
            self._set_field_scroll(rect, self.field_scroll - (content_top - target_rect.top))
        elif target_rect.bottom > content_bottom:
            self._set_field_scroll(rect, self.field_scroll + (target_rect.bottom - content_bottom))

    def _preview_mode_rects(self, rect):
        tabs = []
        labels = (('split', '双视图'), ('top', '俯视'), ('side', '侧视'), ('3d', '3D'))
        x = rect.x + 12
        for mode_key, label in labels:
            tab_rect = pygame.Rect(x, rect.y + 10, 86, 30)
            tabs.append((mode_key, label, tab_rect))
            x += 94
        return tabs

    def _preview_action_rects(self, rect):
        tabs = []
        labels = _available_preview_actions(self.current_role, self._current_profile())
        x = rect.x + 12
        for mode_key, label in labels:
            tab_rect = pygame.Rect(x, rect.y + 48, 76, 28)
            tabs.append((mode_key, label, tab_rect))
            x += 84
        return tabs

    def _preview_action_state(self):
        available_modes = {mode_key for mode_key, _label in _available_preview_actions(self.current_role, self._current_profile())}
        if self.preview_action_mode not in available_modes:
            self.preview_action_mode = 'idle'
        progress = max(0.0, min(1.0, float(self.preview_action_progress)))
        state = {
            'body_lift_m': 0.0,
            'front_drop_m': 0.0,
            'front_raise_m': 0.0,
            'rear_foot_raise_m': 0.0,
            'rear_foot_reach_m': 0.0,
        }
        if self.preview_action_mode == 'step':
            if self.current_role in {'hero', 'engineer', 'sentry'}:
                state['rear_foot_raise_m'] = -0.40 * progress
                state['rear_foot_reach_m'] = 0.0
            else:
                if progress < 0.4:
                    ratio = progress / 0.4
                    state['front_drop_m'] = 0.10 + 0.18 * ratio
                    state['front_raise_m'] = 0.05 + 0.06 * ratio
                    state['rear_foot_raise_m'] = -0.06 * ratio
                    state['rear_foot_reach_m'] = -0.05 * ratio
                elif progress < 0.7:
                    state['front_drop_m'] = 0.18
                    state['front_raise_m'] = 0.08
                    state['rear_foot_raise_m'] = -0.08
                    state['rear_foot_reach_m'] = -0.08
                else:
                    ratio = (progress - 0.7) / 0.3
                    state['front_drop_m'] = 0.18 - 0.10 * ratio
                    state['front_raise_m'] = 0.08 - 0.04 * ratio
                    state['rear_foot_raise_m'] = -0.08 * (1.0 - ratio)
                    state['rear_foot_reach_m'] = -0.08 * (1.0 - ratio)
        elif self.preview_action_mode == 'jump':
            arc_ratio = math.sin(progress * math.pi)
            state['body_lift_m'] = 0.40 * max(0.0, arc_ratio)
            state['front_drop_m'] = 0.03 * max(0.0, arc_ratio)
            state['front_raise_m'] = 0.02 * max(0.0, arc_ratio)
            state['rear_foot_raise_m'] = -0.12 * max(0.0, arc_ratio)
            state['rear_foot_reach_m'] = -0.05 * max(0.0, arc_ratio)
        return state

    def _current_preview_profile(self):
        profile = deepcopy(self._current_profile())
        motion = self._preview_action_state()
        if motion['body_lift_m'] > 1e-6:
            profile['body_clearance_m'] = float(profile.get('body_clearance_m', 0.0)) + motion['body_lift_m']
            profile['rear_climb_assist_mount_height_m'] = float(profile.get('rear_climb_assist_mount_height_m', profile['body_clearance_m'] + profile['body_height_m'] * 0.92)) + motion['body_lift_m']
        profile['_preview_front_drop_m'] = motion['front_drop_m']
        profile['_preview_front_raise_m'] = motion['front_raise_m']
        profile['_preview_rear_foot_raise_m'] = motion['rear_foot_raise_m']
        profile['_preview_rear_foot_reach_m'] = motion['rear_foot_reach_m']
        return profile

    def _set_preview_action_progress_from_x(self, x_pos):
        if self.preview_action_slider_track_rect is None or self.preview_action_slider_thumb_rect is None:
            return
        track_rect = self.preview_action_slider_track_rect
        thumb_w = self.preview_action_slider_thumb_rect.width
        relative = x_pos - track_rect.x - thumb_w * 0.5
        ratio = relative / max(1, track_rect.width - thumb_w)
        self.preview_action_progress = max(0.0, min(1.0, float(ratio)))

    def _draw_text(self, text, font, color, pos):
        surface = font.render(text, True, color)
        self.screen.blit(surface, pos)

    def _iter_3d_preview_primitives(self, profile):
        render_width_scale = float(profile.get('body_render_width_scale', 0.82))
        has_turret = self._profile_has_turret(profile)
        has_mount = self._profile_has_mount(profile)
        has_barrel = self._profile_has_barrel(profile)
        has_front_climb = self._profile_has_front_climb(profile)
        has_rear_climb = self._profile_has_rear_climb(profile)
        body_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.5
        yield ('body', (0.0, body_y, 0.0), (float(profile['body_length_m']) * 0.5, float(profile['body_height_m']) * 0.5, float(profile['body_width_m']) * 0.5 * render_width_scale))

        wheel_radius = max(0.018, float(profile['wheel_radius_m']))
        wheel_half_z = max(0.018, float(profile['wheel_radius_m']) * (0.22 if str(profile.get('wheel_style', 'standard')) == 'omni' else 0.32))
        for wheel_component in _resolved_wheel_components(profile):
            wheel_x, wheel_z = wheel_component['center']
            yield ('wheel', (float(wheel_x), float(wheel_component.get('center_height_m', wheel_radius)), float(wheel_z)), (wheel_radius, wheel_radius, wheel_half_z))

        body_half_x = float(profile['body_length_m']) * 0.5
        body_half_z = float(profile['body_width_m']) * 0.5 * render_width_scale
        wheel_outer_z = max((abs(float(wheel_y)) * render_width_scale for _, wheel_y in profile.get('custom_wheel_positions_m', [])), default=body_half_z + wheel_radius * 0.55)
        if has_front_climb:
            plate_top_length, plate_bottom_length = _front_climb_lengths(profile)
            plate_width = float(profile.get('front_climb_assist_plate_width_m', 0.018))
            plate_height = float(profile.get('front_climb_assist_plate_height_m', 0.18))
            plate_forward = float(profile.get('front_climb_assist_forward_offset_m', 0.04))
            plate_inner = float(profile.get('front_climb_assist_inner_offset_m', 0.06)) * render_width_scale
            plate_center_x = body_half_x + plate_forward + plate_top_length * 0.5
            plate_center_y = wheel_radius + plate_height * 0.5
            plate_center_z = max(body_half_z * 0.45, wheel_outer_z - plate_inner)
            span_length = max(plate_top_length, plate_bottom_length)
            for side_sign in (-1.0, 1.0):
                yield ('front_climb', (plate_center_x, plate_center_y, plate_center_z * side_sign), (span_length * 0.5, plate_height * 0.5, plate_width * 0.5))
                yield ('front_climb', (body_half_x * 0.78, body_y + float(profile['body_height_m']) * 0.22, plate_center_z * side_sign), (plate_top_length * 0.28, max(0.012, plate_height * 0.18), plate_width * 0.6))

        armor_half_h = float(profile['armor_plate_height_m']) * 0.5
        armor_thickness = max(0.012, float(profile.get('armor_plate_gap_m', 0.005)) * 0.75)
        armor_half_width = float(profile['armor_plate_width_m']) * 0.5
        for component in _resolved_armor_components(profile):
            yield ('armor', component['center'], (armor_thickness * 0.5, armor_half_h, armor_half_width))

        armor_light_half_x = float(profile.get('armor_light_length_m', 0.10)) * 0.5
        armor_light_half_y = max(0.005, float(profile.get('armor_light_height_m', 0.02)) * 0.5)
        armor_light_half_z = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
        for component in _resolved_armor_light_components(profile):
            yield ('armor_light', component['center_a'], (armor_light_half_z, armor_light_half_y, armor_light_half_x))
            yield ('armor_light', component['center_b'], (armor_light_half_z, armor_light_half_y, armor_light_half_x))

        if has_rear_climb:
            if str(profile.get('rear_climb_assist_style', 'none')) == 'balance_leg':
                leg_geometry = _balance_leg_geometry(profile, render_width_scale)
                upper_width = float(profile.get('rear_climb_assist_upper_width_m', 0.016))
                upper_height = float(profile.get('rear_climb_assist_upper_height_m', 0.016))
                lower_width = float(profile.get('rear_climb_assist_lower_width_m', 0.016))
                lower_height = float(profile.get('rear_climb_assist_lower_height_m', 0.016))
                hinge_radius = float(leg_geometry['hinge_radius'])
                for side_sign in (-1.0, 1.0):
                    side_z = float(leg_geometry['side_offset']) * side_sign
                    yield ('rear_climb', ((leg_geometry['upper_front'][0] + leg_geometry['knee_front'][0]) * 0.5, (leg_geometry['upper_front'][1] + leg_geometry['knee_front'][1]) * 0.5, side_z), (float(profile.get('rear_climb_assist_upper_length_m', 0.09)) * 0.5, upper_height * 0.5, upper_width * 0.5))
                    yield ('rear_climb', ((leg_geometry['upper_rear'][0] + leg_geometry['knee_rear'][0]) * 0.5, (leg_geometry['upper_rear'][1] + leg_geometry['knee_rear'][1]) * 0.5, side_z), (float(profile.get('rear_climb_assist_upper_length_m', 0.09)) * 0.5, upper_height * 0.5, upper_width * 0.5))
                    yield ('rear_climb', ((leg_geometry['knee_center'][0] + leg_geometry['foot'][0]) * 0.5, (leg_geometry['knee_center'][1] + leg_geometry['foot'][1]) * 0.5, side_z), (float(profile.get('rear_climb_assist_lower_length_m', 0.08)) * 0.5, lower_height * 0.5, lower_width * 0.5))
                    yield ('rear_climb', (leg_geometry['upper_front'][0], leg_geometry['upper_front'][1], side_z), (hinge_radius, hinge_radius, hinge_radius))
                    yield ('rear_climb', (leg_geometry['upper_rear'][0], leg_geometry['upper_rear'][1], side_z), (hinge_radius, hinge_radius, hinge_radius))
                    yield ('rear_climb', (leg_geometry['knee_front'][0], leg_geometry['knee_front'][1], side_z), (hinge_radius, hinge_radius, hinge_radius))
                    yield ('rear_climb', (leg_geometry['knee_rear'][0], leg_geometry['knee_rear'][1], side_z), (hinge_radius, hinge_radius, hinge_radius))
            else:
                rear_points = _rear_climb_points(profile, render_width_scale)
                upper_width = float(profile.get('rear_climb_assist_upper_width_m', 0.016))
                upper_height = float(profile.get('rear_climb_assist_upper_height_m', 0.016))
                lower_width = float(profile.get('rear_climb_assist_lower_width_m', 0.016))
                lower_height = float(profile.get('rear_climb_assist_lower_height_m', 0.016))
                for side_sign in (-1.0, 1.0):
                    side_z = float(rear_points['side_offset']) * side_sign
                    yield ('rear_climb', ((rear_points['mount'][0] + rear_points['joint'][0]) * 0.5, (rear_points['mount'][1] + rear_points['joint'][1]) * 0.5, side_z), (float(profile.get('rear_climb_assist_upper_length_m', 0.09)) * 0.5, upper_height * 0.5, upper_width * 0.5))
                    yield ('rear_climb', ((rear_points['joint'][0] + rear_points['foot'][0]) * 0.5, (rear_points['joint'][1] + rear_points['foot'][1]) * 0.5, side_z), (float(profile.get('rear_climb_assist_lower_length_m', 0.08)) * 0.5, lower_height * 0.5, lower_width * 0.5))
                    yield ('rear_climb', (rear_points['joint'][0], rear_points['joint'][1], side_z), (max(upper_height, lower_height) * 0.75, max(upper_height, lower_height) * 0.75, max(upper_width, lower_width) * 0.55))

        if has_mount or has_turret:
            turret_offset_x = float(profile['gimbal_offset_x_m'])
            turret_offset_z = float(profile['gimbal_offset_y_m'])
            mount_center_y = _profile_mount_center_height(profile)
            turret_center_y = _profile_turret_center_height(profile)
            if has_mount:
                connector_half_height = max(0.02, (float(profile.get('gimbal_mount_gap_m', 0.0)) + float(profile.get('gimbal_mount_height_m', 0.0))) * 0.5)
                yield (
                    'mount',
                    (turret_offset_x, mount_center_y, turret_offset_z),
                    (max(0.02, float(profile['gimbal_mount_length_m']) * 0.5), connector_half_height, max(0.02, float(profile['gimbal_mount_width_m']) * 0.5 * render_width_scale)),
                )
            if has_turret:
                yield (
                    'turret',
                    (turret_offset_x, turret_center_y, turret_offset_z),
                    (float(profile['gimbal_length_m']) * 0.5, float(profile['gimbal_body_height_m']) * 0.5, float(profile['gimbal_width_m']) * 0.5 * render_width_scale),
                )
                if has_barrel:
                    barrel_length = float(profile['barrel_length_m'])
                    barrel_radius = max(0.005, float(profile['barrel_radius_m']))
                    yield (
                        'barrel',
                        (turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + barrel_length * 0.5, turret_center_y, turret_offset_z),
                        (barrel_length * 0.5, barrel_radius, barrel_radius),
                    )
                    barrel_light_half_x = float(profile.get('barrel_light_length_m', 0.10)) * 0.5
                    barrel_light_half_y = max(0.005, float(profile.get('barrel_light_height_m', 0.02)) * 0.5)
                    barrel_light_half_z = max(0.005, float(profile.get('barrel_light_width_m', 0.02)) * 0.5)
                    barrel_light_center_x = turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + barrel_length * 0.45
                    yield ('barrel_light', (barrel_light_center_x, turret_center_y, turret_offset_z + barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z))
                    yield ('barrel_light', (barrel_light_center_x, turret_center_y, turret_offset_z - barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z))

    def _project_3d_preview_point(self, point, mvp, size):
        clip = mvp @ np.array([float(point[0]), float(point[1]), float(point[2]), 1.0], dtype='f4')
        if abs(float(clip[3])) <= 1e-6:
            return None
        ndc = clip[:3] / float(clip[3])
        if float(ndc[2]) < -1.2 or float(ndc[2]) > 1.2:
            return None
        width, height = size
        screen_x = (float(ndc[0]) * 0.5 + 0.5) * width
        screen_y = (1.0 - (float(ndc[1]) * 0.5 + 0.5)) * height
        return (screen_x, screen_y)

    def _build_3d_preview_hitboxes(self, rect, profile, yaw=None, pitch=None):
        if '_terrain_scene_look_at' not in globals() or '_terrain_scene_perspective_matrix' not in globals():
            return
        width, height = rect.size
        if width <= 1 or height <= 1:
            return
        target = np.array([0.0, float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.45, 0.0], dtype='f4')
        bounds_radius = max(
            0.6,
            float(profile['body_length_m']) * 0.9,
            float(profile['body_width_m']) * 0.9,
            float(profile.get('gimbal_length_m', 0.0)) + float(profile.get('barrel_length_m', 0.0)) * 0.8,
            _profile_turret_center_height(profile) + 0.25,
        )
        distance = max(1.4, bounds_radius * 2.9)
        yaw = self.preview_3d_yaw if yaw is None else float(yaw)
        pitch = self.preview_3d_pitch if pitch is None else float(pitch)
        eye = np.array([
            math.sin(yaw) * math.cos(pitch) * distance,
            math.sin(pitch) * distance + bounds_radius * 0.25,
            math.cos(yaw) * math.cos(pitch) * distance,
        ], dtype='f4') + target
        projection = _terrain_scene_perspective_matrix(math.radians(42.0), width / max(height, 1), 0.05, max(8.0, distance * 6.0))
        view = _terrain_scene_look_at(eye, target, np.array([0.0, 1.0, 0.0], dtype='f4'))
        mvp = projection @ view
        hitboxes = []
        for part, center, half_extents in self._iter_3d_preview_primitives(profile):
            cx, cy, cz = center
            hx, hy, hz = half_extents
            projected = []
            for offset_x in (-hx, hx):
                for offset_y in (-hy, hy):
                    for offset_z in (-hz, hz):
                        point = self._project_3d_preview_point((cx + offset_x, cy + offset_y, cz + offset_z), mvp, (width, height))
                        if point is not None:
                            projected.append(point)
            if not projected:
                continue
            xs = [point[0] for point in projected]
            ys = [point[1] for point in projected]
            box = pygame.Rect(int(min(xs)), int(min(ys)), max(6, int(max(xs) - min(xs))), max(6, int(max(ys) - min(ys))))
            box.move_ip(rect.x, rect.y)
            distance_to_eye = float(np.linalg.norm(np.array(center, dtype='f4') - eye))
            hitboxes.append((distance_to_eye, part, box.inflate(8, 8)))
        for _, part, box in sorted(hitboxes, key=lambda item: item[0], reverse=True):
            self.preview_part_hitboxes.append((part, box))

    def _draw_projected_preview(self, rect, profile, *, yaw, pitch, title, hint=None, interactive=False):
        pygame.draw.rect(self.screen, self.colors['preview_bg'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        self._draw_text(title, self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        content_rect = pygame.Rect(rect.x + 10, rect.y + 44, rect.width - 20, rect.height - 56)
        preview_surface = self.preview_renderer_3d.render_scene(profile, content_rect.size, yaw=yaw, pitch=pitch) if self.preview_renderer_3d is not None else None
        pygame.draw.rect(self.screen, self.colors['preview_bg'], content_rect, border_radius=10)
        pygame.draw.rect(self.screen, self.colors['panel_border'], content_rect, 1, border_radius=10)
        if preview_surface is not None:
            self.screen.blit(preview_surface, content_rect.topleft)
            self._build_3d_preview_hitboxes(content_rect, profile, yaw=yaw, pitch=pitch)
        else:
            fallback = '3D 投影不可用'
            detail = self.preview_renderer_3d.error if self.preview_renderer_3d is not None else MODERNGL_PREVIEW_ERROR
            self._draw_text(fallback, self.font, self.colors['text'], (content_rect.x + 14, content_rect.y + 14))
            if detail:
                self._draw_text(detail, self.small_font, self.colors['muted'], (content_rect.x + 14, content_rect.y + 46))
        if hint:
            hint_surface = self.tiny_font.render(hint, True, self.colors['muted'])
            self.screen.blit(hint_surface, (content_rect.x + 8, content_rect.bottom - 22))

    def _draw_top_preview(self, rect, profile):
        pygame.draw.rect(self.screen, self.colors['preview_bg'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        self._draw_text('俯视预览', self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        center = (rect.centerx, rect.centery + 16)
        render_width_scale = float(profile.get('body_render_width_scale', 0.82))
        has_front_climb = self._profile_has_front_climb(profile)
        has_rear_climb = self._profile_has_rear_climb(profile)
        has_mount = self._profile_has_mount(profile)
        has_turret = self._profile_has_turret(profile)
        has_barrel = self._profile_has_barrel(profile)
        max_extent = max(profile['body_length_m'] * 0.75, profile['body_width_m'] * render_width_scale * 0.85, float(profile.get('gimbal_length_m', 0.0)) + float(profile.get('barrel_length_m', 0.0)), 0.45)
        scale = min((rect.width - 80) / max(max_extent * 2.0, 0.6), (rect.height - 100) / max(max_extent * 2.0, 0.6))

        def world_to_screen(point_x, point_y):
            return (int(center[0] + point_x * scale), int(center[1] + point_y * scale))

        def highlight_rect(target_rect, radius=8):
            pygame.draw.rect(self.screen, (244, 214, 72), target_rect.inflate(6, 6), 3, border_radius=radius)

        def register_hitbox(part, area_rect):
            self.preview_part_hitboxes.append((part, area_rect.inflate(8, 8)))

        body_color = tuple(profile['body_color_rgb'])
        turret_color = tuple(profile['turret_color_rgb'])
        armor_color = tuple(profile['armor_color_rgb'])
        wheel_color = tuple(profile['wheel_color_rgb'])
        team_light_color = (110, 168, 255)

        body_rect = pygame.Rect(0, 0, int(profile['body_length_m'] * scale), int(profile['body_width_m'] * render_width_scale * scale))
        body_rect.center = center
        pygame.draw.rect(self.screen, body_color, body_rect, border_radius=10)
        pygame.draw.rect(self.screen, (18, 20, 24), body_rect, 2, border_radius=10)
        register_hitbox('body', body_rect)
        if self.selected_part == 'body':
            highlight_rect(body_rect, radius=10)

        for wheel_x, wheel_y in profile['custom_wheel_positions_m']:
            wheel_pos = world_to_screen(wheel_x, wheel_y * render_width_scale)
            wheel_radius = max(6, int(profile['wheel_radius_m'] * scale * 0.55))
            pygame.draw.circle(self.screen, wheel_color, wheel_pos, wheel_radius)
            pygame.draw.circle(self.screen, self.colors['panel_border'], wheel_pos, wheel_radius, 1)
            pygame.draw.line(self.screen, self.colors['panel_border'], (wheel_pos[0] - wheel_radius // 2, wheel_pos[1] - wheel_radius // 2), (wheel_pos[0] + wheel_radius // 2, wheel_pos[1] + wheel_radius // 2), 1)
            pygame.draw.line(self.screen, self.colors['panel_border'], (wheel_pos[0] - wheel_radius // 2, wheel_pos[1] + wheel_radius // 2), (wheel_pos[0] + wheel_radius // 2, wheel_pos[1] - wheel_radius // 2), 1)
            register_hitbox('wheel', pygame.Rect(wheel_pos[0] - wheel_radius, wheel_pos[1] - wheel_radius, wheel_radius * 2, wheel_radius * 2))
            if self.selected_part == 'wheel':
                pygame.draw.circle(self.screen, (244, 214, 72), wheel_pos, wheel_radius + 4, 2)

        wheel_outer_y = max((abs(float(wheel_y)) * render_width_scale for _, wheel_y in profile.get('custom_wheel_positions_m', [])), default=profile['body_width_m'] * render_width_scale * 0.5 + profile['wheel_radius_m'] * 0.55)
        if has_front_climb:
            plate_top_length_m, plate_bottom_length_m = _front_climb_lengths(profile)
            plate_length = max(8, int(max(plate_top_length_m, plate_bottom_length_m) * scale))
            plate_width = max(6, int(profile.get('front_climb_assist_plate_width_m', 0.018) * scale * 2.0))
            plate_center_x = profile['body_length_m'] * 0.5 + profile.get('front_climb_assist_forward_offset_m', 0.04) + plate_bottom_length_m * 0.5
            plate_center_y = max(profile['body_width_m'] * render_width_scale * 0.28, wheel_outer_y - profile.get('front_climb_assist_inner_offset_m', 0.06) * render_width_scale)
            for side_sign in (-1.0, 1.0):
                front_rect = pygame.Rect(0, 0, plate_length, plate_width)
                front_rect.center = world_to_screen(plate_center_x, plate_center_y * side_sign)
                pygame.draw.rect(self.screen, (92, 96, 108), front_rect, border_radius=4)
                pygame.draw.rect(self.screen, (18, 20, 24), front_rect, 1, border_radius=4)
                register_hitbox('front_climb', front_rect)
                if self.selected_part == 'front_climb':
                    highlight_rect(front_rect, radius=4)

        if has_mount:
            mount_rect = pygame.Rect(0, 0, max(10, int(profile['gimbal_mount_length_m'] * scale)), max(10, int(profile['gimbal_mount_width_m'] * render_width_scale * scale)))
            mount_rect.center = world_to_screen(profile['gimbal_offset_x_m'], profile['gimbal_offset_y_m'])
            pygame.draw.rect(self.screen, (96, 100, 112), mount_rect, border_radius=6)
            pygame.draw.rect(self.screen, (18, 20, 24), mount_rect, 1, border_radius=6)
            register_hitbox('mount', mount_rect)
            if self.selected_part == 'mount':
                highlight_rect(mount_rect, radius=6)

        if has_turret:
            turret_rect = pygame.Rect(0, 0, max(12, int(profile['gimbal_length_m'] * scale)), max(12, int(profile['gimbal_width_m'] * render_width_scale * scale)))
            turret_rect.center = world_to_screen(profile['gimbal_offset_x_m'], profile['gimbal_offset_y_m'])
            pygame.draw.rect(self.screen, turret_color, turret_rect, border_radius=8)
            pygame.draw.rect(self.screen, (18, 20, 24), turret_rect, 2, border_radius=8)
            register_hitbox('turret', turret_rect)
            if self.selected_part == 'turret':
                highlight_rect(turret_rect, radius=8)
            if has_barrel:
                barrel_end = world_to_screen(profile['gimbal_offset_x_m'] + profile['gimbal_length_m'] * 0.5 + profile['barrel_length_m'], profile['gimbal_offset_y_m'])
                pygame.draw.line(self.screen, turret_color, turret_rect.center, barrel_end, max(4, int(profile['barrel_radius_m'] * scale * 6.0)))
                pygame.draw.line(self.screen, (18, 20, 24), turret_rect.center, barrel_end, 2)
                barrel_rect = pygame.Rect(min(turret_rect.centerx, barrel_end[0]), min(turret_rect.centery, barrel_end[1]) - 4, abs(barrel_end[0] - turret_rect.centerx), max(8, abs(barrel_end[1] - turret_rect.centery) + 8))
                register_hitbox('barrel', barrel_rect)
                if self.selected_part == 'barrel':
                    highlight_rect(barrel_rect, radius=6)
                barrel_light_width = max(3, int(profile['barrel_light_width_m'] * scale * 1.5))
                barrel_light_length = max(10, int(profile['barrel_light_length_m'] * scale))
                barrel_light_offset = max(5, int(profile['barrel_light_width_m'] * scale * 4.0))
                for direction in (-1, 1):
                    light_rect = pygame.Rect(0, 0, barrel_light_length, barrel_light_width)
                    light_rect.center = (int((turret_rect.centerx + barrel_end[0]) * 0.5), int((turret_rect.centery + barrel_end[1]) * 0.5 + direction * barrel_light_offset))
                    pygame.draw.rect(self.screen, team_light_color, light_rect, border_radius=4)
                    register_hitbox('barrel_light', light_rect)
                    if self.selected_part == 'barrel_light':
                        highlight_rect(light_rect, radius=4)

        armor_half_length = profile['body_length_m'] * 0.5 + profile['armor_plate_gap_m']
        armor_half_width = profile['body_width_m'] * render_width_scale * 0.5 + profile['armor_plate_gap_m']
        armor_w = max(8, int(profile['armor_plate_width_m'] * scale * 0.55))
        armor_l = max(8, int(profile['armor_plate_length_m'] * scale * 0.55))
        armor_specs = (
            (armor_half_length, 0.0, 8, armor_w),
            (-armor_half_length, 0.0, 8, armor_w),
            (0.0, armor_half_width, armor_l, 8),
            (0.0, -armor_half_width, armor_l, 8),
        )
        for offset_x, offset_y, width_px, height_px in armor_specs:
            armor_rect = pygame.Rect(0, 0, width_px, height_px)
            armor_rect.center = world_to_screen(offset_x, offset_y)
            pygame.draw.rect(self.screen, armor_color, armor_rect, border_radius=4)
            pygame.draw.rect(self.screen, (18, 20, 24), armor_rect, 1, border_radius=4)
            register_hitbox('armor', armor_rect)
            if self.selected_part == 'armor':
                highlight_rect(armor_rect, radius=4)
            light_length = max(8, int(profile['armor_light_length_m'] * scale))
            light_width = max(4, int(profile['armor_light_width_m'] * scale * 2.0))
            if width_px < height_px:
                light_a = pygame.Rect(armor_rect.centerx - light_width // 2, armor_rect.top - light_length, light_width, light_length)
                light_b = pygame.Rect(armor_rect.centerx - light_width // 2, armor_rect.bottom, light_width, light_length)
            else:
                light_a = pygame.Rect(armor_rect.left - light_length, armor_rect.centery - light_width // 2, light_length, light_width)
                light_b = pygame.Rect(armor_rect.right, armor_rect.centery - light_width // 2, light_length, light_width)
            for light_rect in (light_a, light_b):
                pygame.draw.rect(self.screen, team_light_color, light_rect, border_radius=4)
                register_hitbox('armor_light', light_rect)
                if self.selected_part == 'armor_light':
                    highlight_rect(light_rect, radius=4)

        if has_rear_climb:
            rear_points = _rear_climb_points(profile, render_width_scale)
            upper_length = max(8, int(profile.get('rear_climb_assist_upper_length_m', 0.09) * scale))
            lower_length = max(8, int(profile.get('rear_climb_assist_lower_length_m', 0.08) * scale))
            bar_width = max(6, int(max(profile.get('rear_climb_assist_upper_width_m', 0.016), profile.get('rear_climb_assist_lower_width_m', 0.016)) * scale * 2.0))
            for side_sign in (-1.0, 1.0):
                side_y = rear_points['side_offset'] * side_sign
                upper_rect = pygame.Rect(0, 0, upper_length, bar_width)
                upper_rect.center = world_to_screen((rear_points['mount'][0] + rear_points['joint'][0]) * 0.5, side_y)
                lower_rect = pygame.Rect(0, 0, lower_length, bar_width)
                lower_rect.center = world_to_screen((rear_points['joint'][0] + rear_points['foot'][0]) * 0.5, side_y)
                joint_center = world_to_screen(rear_points['joint'][0], side_y)
                joint_rect = pygame.Rect(0, 0, max(8, bar_width + 4), max(8, bar_width + 4))
                joint_rect.center = joint_center
                pygame.draw.rect(self.screen, (106, 110, 120), upper_rect, border_radius=4)
                pygame.draw.rect(self.screen, (92, 96, 108), lower_rect, border_radius=4)
                pygame.draw.rect(self.screen, (116, 120, 132), joint_rect, border_radius=4)
                for climb_rect in (upper_rect, lower_rect, joint_rect):
                    pygame.draw.rect(self.screen, (18, 20, 24), climb_rect, 1, border_radius=4)
                    register_hitbox('rear_climb', climb_rect)
                    if self.selected_part == 'rear_climb':
                        highlight_rect(climb_rect, radius=4)

    def _draw_side_preview(self, rect, profile):
        pygame.draw.rect(self.screen, self.colors['preview_bg'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        self._draw_text('侧视预览', self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        ground_y = rect.bottom - 42
        pygame.draw.line(self.screen, self.colors['grid'], (rect.x + 20, ground_y), (rect.right - 20, ground_y), 2)
        scale = min((rect.width - 80) / max(profile['body_length_m'] + float(profile.get('barrel_length_m', 0.0)) + 0.35, 0.5), (rect.height - 100) / max(_profile_turret_center_height(profile) + 0.4, 0.5))
        center_x = rect.centerx
        render_width_scale = float(profile.get('body_render_width_scale', 1.0))
        has_front_climb = self._profile_has_front_climb(profile)
        has_rear_climb = self._profile_has_rear_climb(profile)
        has_mount = self._profile_has_mount(profile)
        has_turret = self._profile_has_turret(profile)
        has_barrel = self._profile_has_barrel(profile)

        def register_hitbox(part, area_rect):
            self.preview_part_hitboxes.append((part, area_rect.inflate(8, 8)))

        wheel_radius = max(6, int(profile['wheel_radius_m'] * scale))
        body_width_px = max(40, int(profile['body_length_m'] * scale))
        body_height_px = max(20, int(profile['body_height_m'] * scale))
        clearance_px = max(4, int(profile['body_clearance_m'] * scale))
        wheel_components = _resolved_wheel_components(profile)
        leg_geometry = _balance_leg_geometry(profile, render_width_scale) if str(profile.get('rear_climb_assist_style', 'none')) == 'balance_leg' else None
        raw_positions = [position for position in profile.get('custom_wheel_positions_m', []) if isinstance(position, (list, tuple)) and len(position) >= 2]
        dynamic_indices = set()
        if leg_geometry is not None:
            if str(profile.get('wheel_style', 'standard')) == 'legged' or len(raw_positions) <= 2:
                dynamic_indices = set(range(len(wheel_components)))
            else:
                dynamic_count = max(2, len(raw_positions) // 2)
                dynamic_indices = set(sorted(range(len(raw_positions)), key=lambda index: float(raw_positions[index][0]))[:dynamic_count])
        wheel_centers = []
        for index, component in enumerate(wheel_components):
            wheel_center_y_m = float(leg_geometry['foot'][1]) if leg_geometry is not None and index in dynamic_indices else float(profile['wheel_radius_m'])
            wheel_centers.append((center_x + int(float(component['center'][0]) * scale), ground_y - int(wheel_center_y_m * scale)))
        wheel_centers = tuple(wheel_centers) or ((center_x, ground_y - wheel_radius),)
        for wheel_center in wheel_centers:
            pygame.draw.circle(self.screen, tuple(profile['wheel_color_rgb']), wheel_center, wheel_radius)
            pygame.draw.circle(self.screen, self.colors['panel_border'], wheel_center, wheel_radius, 1)
            pygame.draw.line(self.screen, self.colors['panel_border'], (wheel_center[0] - wheel_radius // 2, wheel_center[1] - wheel_radius // 2), (wheel_center[0] + wheel_radius // 2, wheel_center[1] + wheel_radius // 2), 1)
            pygame.draw.line(self.screen, self.colors['panel_border'], (wheel_center[0] - wheel_radius // 2, wheel_center[1] + wheel_radius // 2), (wheel_center[0] + wheel_radius // 2, wheel_center[1] - wheel_radius // 2), 1)
            register_hitbox('wheel', pygame.Rect(wheel_center[0] - wheel_radius, wheel_center[1] - wheel_radius, wheel_radius * 2, wheel_radius * 2))
            if self.selected_part == 'wheel':
                pygame.draw.circle(self.screen, (244, 214, 72), wheel_center, wheel_radius + 4, 2)
        body_rect = pygame.Rect(0, 0, body_width_px, body_height_px)
        body_rect.center = (center_x, ground_y - wheel_radius * 2 - clearance_px - body_height_px // 2 + 10)
        pygame.draw.rect(self.screen, tuple(profile['body_color_rgb']), body_rect, border_radius=10)
        pygame.draw.rect(self.screen, (18, 20, 24), body_rect, 2, border_radius=10)
        register_hitbox('body', body_rect)
        if self.selected_part == 'body':
            pygame.draw.rect(self.screen, (244, 214, 72), body_rect.inflate(6, 6), 3, border_radius=10)
        if has_front_climb:
            plate_top_length_m, plate_bottom_length_m = _front_climb_lengths(profile)
            plate_top = max(10, int(plate_top_length_m * scale))
            plate_bottom = max(8, int(plate_bottom_length_m * scale))
            plate_height = max(12, int(profile.get('front_climb_assist_plate_height_m', 0.18) * scale))
            rear_x = center_x + int((profile['body_length_m'] * 0.5 + profile.get('front_climb_assist_forward_offset_m', 0.04)) * scale)
            front_top_x = rear_x + plate_top
            front_bottom_x = rear_x + plate_bottom
            front_poly = [
                (rear_x, ground_y - plate_height),
                (front_top_x, ground_y - plate_height),
                (front_bottom_x, ground_y),
                (rear_x, ground_y),
            ]
            pygame.draw.polygon(self.screen, (92, 96, 108), front_poly)
            pygame.draw.polygon(self.screen, (18, 20, 24), front_poly, 1)
            front_bounds = pygame.Rect(min(point[0] for point in front_poly), min(point[1] for point in front_poly), max(point[0] for point in front_poly) - min(point[0] for point in front_poly), max(point[1] for point in front_poly) - min(point[1] for point in front_poly))
            register_hitbox('front_climb', front_bounds)
            if self.selected_part == 'front_climb':
                pygame.draw.rect(self.screen, (244, 214, 72), front_bounds.inflate(6, 6), 3, border_radius=6)
        if has_rear_climb:
            if str(profile.get('rear_climb_assist_style', 'none')) == 'balance_leg':
                leg_geometry = leg_geometry or _balance_leg_geometry(profile, render_width_scale)
                dog_leg_width = max(4, int(max(profile.get('rear_climb_assist_upper_height_m', 0.016), profile.get('rear_climb_assist_lower_height_m', 0.016)) * scale * 2.0))
                upper_front = (center_x + int(leg_geometry['upper_front'][0] * scale), ground_y - int(leg_geometry['upper_front'][1] * scale))
                upper_rear = (center_x + int(leg_geometry['upper_rear'][0] * scale), ground_y - int(leg_geometry['upper_rear'][1] * scale))
                knee_front = (center_x + int(leg_geometry['knee_front'][0] * scale), ground_y - int(leg_geometry['knee_front'][1] * scale))
                knee_rear = (center_x + int(leg_geometry['knee_rear'][0] * scale), ground_y - int(leg_geometry['knee_rear'][1] * scale))
                lower_tip = (center_x + int(leg_geometry['foot'][0] * scale), ground_y - int(leg_geometry['foot'][1] * scale))
                pygame.draw.line(self.screen, (106, 110, 120), upper_front, knee_front, dog_leg_width)
                pygame.draw.line(self.screen, (106, 110, 120), upper_rear, knee_rear, dog_leg_width)
                knee_center = ((knee_front[0] + knee_rear[0]) // 2, (knee_front[1] + knee_rear[1]) // 2)
                pygame.draw.line(self.screen, (92, 96, 108), knee_center, lower_tip, dog_leg_width)
                dog_leg_rect = pygame.Rect(min(upper_front[0], upper_rear[0], knee_front[0], knee_rear[0], lower_tip[0]), min(upper_front[1], upper_rear[1], knee_front[1], knee_rear[1], lower_tip[1]), max(10, max(upper_front[0], upper_rear[0], knee_front[0], knee_rear[0], lower_tip[0]) - min(upper_front[0], upper_rear[0], knee_front[0], knee_rear[0], lower_tip[0])), max(12, max(upper_front[1], upper_rear[1], knee_front[1], knee_rear[1], lower_tip[1]) - min(upper_front[1], upper_rear[1], knee_front[1], knee_rear[1], lower_tip[1])))
                register_hitbox('rear_climb', dog_leg_rect)
                if self.selected_part == 'rear_climb':
                    pygame.draw.rect(self.screen, (244, 214, 72), dog_leg_rect.inflate(8, 8), 3, border_radius=6)
            else:
                rear_points = _rear_climb_points(profile, render_width_scale)
                upper_anchor = (center_x + int(rear_points['mount'][0] * scale), ground_y - int(rear_points['mount'][1] * scale))
                joint = (center_x + int(rear_points['joint'][0] * scale), ground_y - int(rear_points['joint'][1] * scale))
                lower_tip = (center_x + int(rear_points['foot'][0] * scale), ground_y - int(rear_points['foot'][1] * scale))
                dog_leg_width = max(4, int(max(profile.get('rear_climb_assist_upper_height_m', 0.016), profile.get('rear_climb_assist_lower_height_m', 0.016)) * scale * 2.0))
                pygame.draw.line(self.screen, (106, 110, 120), upper_anchor, joint, dog_leg_width)
                pygame.draw.line(self.screen, (92, 96, 108), joint, lower_tip, dog_leg_width)
                dog_leg_rect = pygame.Rect(min(upper_anchor[0], joint[0], lower_tip[0]), min(upper_anchor[1], joint[1], lower_tip[1]), max(10, max(upper_anchor[0], joint[0], lower_tip[0]) - min(upper_anchor[0], joint[0], lower_tip[0])), max(12, max(upper_anchor[1], joint[1], lower_tip[1]) - min(upper_anchor[1], joint[1], lower_tip[1])))
                register_hitbox('rear_climb', dog_leg_rect)
                if self.selected_part == 'rear_climb':
                    pygame.draw.rect(self.screen, (244, 214, 72), dog_leg_rect.inflate(8, 8), 3, border_radius=6)
        if has_mount:
            mount_rect = pygame.Rect(0, 0, max(12, int(profile['gimbal_mount_length_m'] * scale)), max(10, int((profile.get('gimbal_mount_gap_m', 0.0) + profile.get('gimbal_mount_height_m', 0.0)) * scale)))
            mount_rect.center = (
                center_x + int(profile['gimbal_offset_x_m'] * scale),
                ground_y - int(_profile_mount_center_height(profile) * scale),
            )
            pygame.draw.rect(self.screen, (96, 100, 112), mount_rect, border_radius=5)
            pygame.draw.rect(self.screen, (18, 20, 24), mount_rect, 1, border_radius=5)
            register_hitbox('mount', mount_rect)
            if self.selected_part == 'mount':
                pygame.draw.rect(self.screen, (244, 214, 72), mount_rect.inflate(6, 6), 3, border_radius=6)
        if has_turret:
            turret_rect = pygame.Rect(0, 0, max(28, int(profile['gimbal_length_m'] * scale)), max(16, int(profile['gimbal_body_height_m'] * scale)))
            turret_center_y = ground_y - int(_profile_turret_center_height(profile) * scale)
            turret_rect.center = (center_x + int(profile['gimbal_offset_x_m'] * scale), turret_center_y)
            pygame.draw.rect(self.screen, tuple(profile['turret_color_rgb']), turret_rect, border_radius=8)
            pygame.draw.rect(self.screen, (18, 20, 24), turret_rect, 2, border_radius=8)
            register_hitbox('turret', turret_rect)
            if self.selected_part == 'turret':
                pygame.draw.rect(self.screen, (244, 214, 72), turret_rect.inflate(6, 6), 3, border_radius=8)
            if has_barrel:
                barrel_end = (turret_rect.right + max(18, int(profile['barrel_length_m'] * scale)), turret_rect.centery)
                pygame.draw.line(self.screen, tuple(profile['turret_color_rgb']), turret_rect.center, barrel_end, max(3, int(profile['barrel_radius_m'] * scale * 2.8)))
                pygame.draw.line(self.screen, (18, 20, 24), turret_rect.center, barrel_end, 2)
                barrel_rect = pygame.Rect(min(turret_rect.centerx, barrel_end[0]), min(turret_rect.centery, barrel_end[1]) - 4, abs(barrel_end[0] - turret_rect.centerx), 8)
                register_hitbox('barrel', barrel_rect)
                if self.selected_part == 'barrel':
                    pygame.draw.rect(self.screen, (244, 214, 72), barrel_rect.inflate(6, 6), 3, border_radius=6)

    def _draw_preview_panel(self, rect):
        profile = self._current_preview_profile()
        self.preview_part_hitboxes = []
        pygame.draw.rect(self.screen, self.colors['panel'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        self.preview_mode_tabs = self._preview_mode_rects(rect)
        self.preview_action_tabs = self._preview_action_rects(rect)
        available_action_modes = {mode_key for mode_key, _label, _tab_rect in self.preview_action_tabs}
        if self.preview_action_mode not in available_action_modes:
            self.preview_action_mode = 'idle'
        for mode_key, label, tab_rect in self.preview_mode_tabs:
            active = mode_key == self.preview_mode
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], tab_rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], tab_rect, 1, border_radius=8)
            text_color = (20, 22, 24) if active else self.colors['text']
            text_surface = self.small_font.render(label, True, text_color)
            self.screen.blit(text_surface, text_surface.get_rect(center=tab_rect.center))
        for mode_key, label, tab_rect in self.preview_action_tabs:
            active = mode_key == self.preview_action_mode
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], tab_rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], tab_rect, 1, border_radius=8)
            text_color = (20, 22, 24) if active else self.colors['text']
            text_surface = self.small_font.render(label, True, text_color)
            self.screen.blit(text_surface, text_surface.get_rect(center=tab_rect.center))
        if len(self.preview_action_tabs) > 1:
            slider_track_rect = pygame.Rect(rect.x + 272, rect.y + 53, rect.width - 286, 18)
            slider_thumb_w = 16
            slider_thumb_x = slider_track_rect.x + int((slider_track_rect.width - slider_thumb_w) * max(0.0, min(1.0, self.preview_action_progress)))
            slider_thumb_rect = pygame.Rect(slider_thumb_x, slider_track_rect.y - 3, slider_thumb_w, 24)
            self.preview_action_slider_track_rect = slider_track_rect
            self.preview_action_slider_thumb_rect = slider_thumb_rect
            pygame.draw.rect(self.screen, (52, 58, 66), slider_track_rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], slider_track_rect, 1, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['accent'], slider_thumb_rect, border_radius=8)
            progress_text = self.tiny_font.render(f'动作进度 {int(round(self.preview_action_progress * 100.0)):d}%', True, self.colors['muted'])
            self.screen.blit(progress_text, (slider_track_rect.x, slider_track_rect.y - 20))
        else:
            self.preview_action_slider_track_rect = None
            self.preview_action_slider_thumb_rect = None
        content_rect = pygame.Rect(rect.x + 12, rect.y + 90, rect.width - 24, rect.height - 102)
        self.preview_content_rect = content_rect
        if self.preview_mode == 'split':
            top_rect = pygame.Rect(content_rect.x, content_rect.y, content_rect.width, int(content_rect.height * 0.56))
            side_rect = pygame.Rect(content_rect.x, top_rect.bottom + 12, content_rect.width, content_rect.bottom - top_rect.bottom - 12)
            self._draw_projected_preview(top_rect, profile, yaw=0.72, pitch=1.04, title='俯视投影')
            self._draw_projected_preview(side_rect, profile, yaw=math.pi * 0.5, pitch=0.18, title='侧视投影')
            return
        if self.preview_mode == 'top':
            self._draw_projected_preview(content_rect, profile, yaw=0.72, pitch=1.04, title='俯视投影')
            return
        if self.preview_mode == 'side':
            self._draw_projected_preview(content_rect, profile, yaw=math.pi * 0.5, pitch=0.18, title='侧视投影')
            return
        preview_surface = self.preview_renderer_3d.render_scene(profile, content_rect.size, yaw=self.preview_3d_yaw, pitch=self.preview_3d_pitch) if self.preview_renderer_3d is not None else None
        pygame.draw.rect(self.screen, self.colors['preview_bg'], content_rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], content_rect, 1, border_radius=12)
        if preview_surface is not None:
            self.screen.blit(preview_surface, content_rect.topleft)
            self._build_3d_preview_hitboxes(content_rect, profile, yaw=self.preview_3d_yaw, pitch=self.preview_3d_pitch)
        else:
            title = '3D 预览不可用'
            detail = self.preview_renderer_3d.error if self.preview_renderer_3d is not None else MODERNGL_PREVIEW_ERROR
            self._draw_text(title, self.font, self.colors['text'], (content_rect.x + 18, content_rect.y + 18))
            if detail:
                self._draw_text(detail, self.small_font, self.colors['muted'], (content_rect.x + 18, content_rect.y + 52))
        hint = self.tiny_font.render('拖动鼠标旋转 3D 预览；上方可切换静态、上台阶、跳跃动作', True, self.colors['muted'])
        self.screen.blit(hint, (content_rect.x + 14, content_rect.bottom - 24))

    def _draw_fields_panel(self, rect):
        pygame.draw.rect(self.screen, self.colors['panel'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        title = f'{PART_LABELS.get(self.selected_part, "可调参数")}参数' if self.selected_part is not None else '选择部件'
        self._draw_text(title, self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        self.component_control_actions = []
        if self._part_supports_component_selection(self.selected_part):
            profile = self._current_profile()
            count = self._clamp_selected_component_index(profile)
            control_y = rect.y + 44
            single_rect = pygame.Rect(rect.x + 12, control_y, 66, 28)
            all_rect = pygame.Rect(rect.x + 86, control_y, 66, 28)
            prev_rect = pygame.Rect(rect.x + 176, control_y, 28, 28)
            next_rect = pygame.Rect(rect.x + 312, control_y, 28, 28)
            label_rect = pygame.Rect(rect.x + 212, control_y, 92, 28)
            for action, button_rect, label in (
                ('component_scope:single', single_rect, '单个'),
                ('component_scope:all', all_rect, '全部'),
            ):
                active = self.selected_component_scope == action.split(':', 1)[1]
                pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], button_rect, border_radius=7)
                pygame.draw.rect(self.screen, self.colors['panel_border'], button_rect, 1, border_radius=7)
                text_color = (20, 22, 24) if active else self.colors['text']
                rendered = self.small_font.render(label, True, text_color)
                self.screen.blit(rendered, rendered.get_rect(center=button_rect.center))
                self.component_control_actions.append((button_rect, action))
            pygame.draw.rect(self.screen, self.colors['panel_alt'], label_rect, border_radius=7)
            pygame.draw.rect(self.screen, self.colors['panel_border'], label_rect, 1, border_radius=7)
            unit_text = '全部' if self.selected_component_scope == 'all' else f'单位体 {self.selected_component_index + 1}/{max(1, count)}'
            rendered = self.small_font.render(unit_text, True, self.colors['text'])
            self.screen.blit(rendered, rendered.get_rect(center=label_rect.center))
            for action, button_rect, label in (
                ('component_cycle:-1', prev_rect, '<'),
                ('component_cycle:1', next_rect, '>'),
            ):
                enabled = self.selected_component_scope != 'all' and count > 1
                pygame.draw.rect(self.screen, self.colors['panel_alt'] if enabled else (42, 46, 52), button_rect, border_radius=7)
                pygame.draw.rect(self.screen, self.colors['panel_border'], button_rect, 1, border_radius=7)
                rendered = self.small_font.render(label, True, self.colors['text'] if enabled else self.colors['muted'])
                self.screen.blit(rendered, rendered.get_rect(center=button_rect.center))
                if enabled:
                    self.component_control_actions.append((button_rect, action))
        content_top_inset = self._field_content_top_inset()
        content_rect = pygame.Rect(rect.x + 8, rect.y + content_top_inset, rect.width - 20, rect.height - content_top_inset - 12)
        pygame.draw.rect(self.screen, self.colors['panel_alt'], content_rect, border_radius=8)
        if self.selected_part is None:
            hint_lines = [
                '右侧预览中点击部件后，这里才会出现对应的长宽高与颜色参数。',
                '当前可选：底盘、车轮、前爬升板、后腿机构、云台、枪管、连接件、装甲板、装甲灯条、枪管灯条。',
            ]
            for index, line in enumerate(hint_lines):
                self._draw_text(line, self.small_font, self.colors['muted'], (content_rect.x + 16, content_rect.y + 18 + index * 26))
            self.field_scrollbar_track_rect = None
            self.field_scrollbar_thumb_rect = None
            return
        old_clip = self.screen.get_clip()
        self.screen.set_clip(content_rect)
        rows, content_height = self._field_rows(rect, scroll_offset=self.field_scroll)
        self.field_scrollbar_track_rect = None
        self.field_scrollbar_thumb_rect = None
        for row_type, payload, row_rect, field_index in rows:
            if row_rect.bottom < content_rect.top or row_rect.top > content_rect.bottom:
                continue
            spec = payload
            active = field_index == self.selected_field_index
            pygame.draw.rect(self.screen, self.colors['panel_alt'] if active else (31, 36, 42), row_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_border'], row_rect, 1, border_radius=6)
            value = self._field_value(spec)
            if isinstance(self.active_numeric_input, dict) and int(self.active_numeric_input.get('field_index', -1)) == field_index:
                value_text = str(self.active_numeric_input.get('buffer', ''))
                value_color = self.colors['accent']
            else:
                value_text = f'{value:.3f}' if spec['kind'] != 'color' else f'{int(value)}'
                value_color = self.colors['muted']
            self._draw_text(spec['label'], self.small_font, self.colors['text'], (row_rect.x + 10, row_rect.y + 5))
            value_surface = self.small_font.render(value_text, True, value_color)
            self.screen.blit(value_surface, value_surface.get_rect(right=row_rect.right - 10, centery=row_rect.centery))
        self.screen.set_clip(old_clip)

        max_scroll = max(0, content_height - content_rect.height)
        if max_scroll > 0:
            track_rect = pygame.Rect(rect.right - 12, content_rect.y + 4, 6, content_rect.height - 8)
            thumb_height = max(34, int(track_rect.height * content_rect.height / max(content_height, 1)))
            thumb_y = track_rect.y + int((track_rect.height - thumb_height) * (self.field_scroll / max(max_scroll, 1)))
            thumb_rect = pygame.Rect(track_rect.x, thumb_y, track_rect.width, thumb_height)
            pygame.draw.rect(self.screen, (58, 64, 74), track_rect, border_radius=4)
            pygame.draw.rect(self.screen, self.colors['accent'], thumb_rect, border_radius=4)
            self.field_scrollbar_track_rect = track_rect
            self.field_scrollbar_thumb_rect = thumb_rect

    def _draw_header(self):
        self._draw_text('车辆外貌编辑器', self.title_font, self.colors['text'], (28, 22))
        self._draw_text('保存后的预设会在后续创建单位时自动应用', self.small_font, self.colors['muted'], (30, 52))
        for role_key, label, rect in self._role_tabs():
            active = role_key == self.current_role
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=8)
            text_surface = self.font.render(label, True, (20, 22, 24) if active else self.colors['text'])
            self.screen.blit(text_surface, text_surface.get_rect(center=rect.center))
        self.infantry_subtype_tabs = self._infantry_subtype_tab_rects()
        for subtype, label, rect in self.infantry_subtype_tabs:
            active = subtype == self.current_infantry_subtype
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=8)
            text_surface = self.small_font.render(label, True, (20, 22, 24) if active else self.colors['text'])
            self.screen.blit(text_surface, text_surface.get_rect(center=rect.center))

    def _draw_footer(self):
        footer_rect = pygame.Rect(24, self.window_height - 44, self.window_width - 48, 24)
        self._draw_text(self.status_text, self.small_font, self.colors['muted'], footer_rect.topleft)

    def _handle_click(self, pos):
        for mode_key, _, rect in self.preview_mode_tabs:
            if rect.collidepoint(pos):
                self.preview_mode = mode_key
                self.active_numeric_input = None
                return
        for action_key, _, rect in self.preview_action_tabs:
            if rect.collidepoint(pos):
                self.preview_action_mode = action_key
                self.active_numeric_input = None
                return
        if self.preview_action_slider_thumb_rect is not None and self.preview_action_slider_thumb_rect.collidepoint(pos):
            self.preview_action_drag_active = True
            return
        if self.preview_action_slider_track_rect is not None and self.preview_action_slider_track_rect.collidepoint(pos):
            self.preview_action_drag_active = True
            self._set_preview_action_progress_from_x(pos[0])
            return
        for role_key, _, rect in self._role_tabs():
            if rect.collidepoint(pos):
                self.current_role = role_key
                self.active_numeric_input = None
                return
        for subtype, _, rect in self.infantry_subtype_tabs:
            if rect.collidepoint(pos):
                self.current_infantry_subtype = subtype
                self.selected_component_index = 0
                self.active_numeric_input = None
                store = self._ensure_infantry_profile_store()
                store['default_chassis_subtype'] = subtype
                return
        for action_rect, action in self.component_control_actions:
            if action_rect.collidepoint(pos):
                if action.startswith('component_scope:'):
                    self.selected_component_scope = action.split(':', 1)[1]
                elif action.startswith('component_cycle:'):
                    self._change_selected_component(int(action.split(':', 1)[1]))
                self.active_numeric_input = None
                return
        field_panel, _ = self._layout_panels()
        if self.field_scrollbar_thumb_rect is not None and self.field_scrollbar_thumb_rect.collidepoint(pos):
            self.field_scroll_drag_active = True
            return
        if self.field_scrollbar_track_rect is not None and self.field_scrollbar_track_rect.collidepoint(pos):
            thumb_height = self.field_scrollbar_thumb_rect.height if self.field_scrollbar_thumb_rect is not None else 0
            relative = pos[1] - self.field_scrollbar_track_rect.y - thumb_height * 0.5
            ratio = relative / max(1, self.field_scrollbar_track_rect.height - thumb_height)
            self._set_field_scroll(field_panel, ratio * self._max_field_scroll(field_panel))
            return
        for part, hitbox in reversed(self.preview_part_hitboxes):
            if hitbox.collidepoint(pos):
                self.selected_part = part
                self.selected_field_index = 0
                self.field_scroll = 0
                self.selected_component_index = 0
                self.active_numeric_input = None
                return
        if self.preview_content_rect is not None and self.preview_content_rect.collidepoint(pos) and self.preview_mode != '3d':
            self.selected_part = None
            self.active_numeric_input = None
            return
        rows, _ = self._field_rows(field_panel, scroll_offset=self.field_scroll)
        for row_type, _, row_rect, field_index in rows:
            if row_type == 'field' and row_rect.collidepoint(pos):
                self.selected_field_index = field_index
                self._ensure_selected_field_visible(field_panel)
                self.active_numeric_input = None
                return
    def _reset_current_role(self):
        if self.current_role == 'infantry':
            store = self._ensure_infantry_profile_store()
            store['subtype_profiles'][self.current_infantry_subtype] = _normalize_profile_constraints('infantry', infantry_chassis_preset(self.current_infantry_subtype), forced_subtype=self.current_infantry_subtype)
            store['default_chassis_subtype'] = self.current_infantry_subtype
            self.profiles[self.current_role] = store
        else:
            self.profiles[self.current_role] = _default_profile(self.current_role)
        self.selected_part = None
        self.selected_component_index = 0
        self.active_numeric_input = None
        self.status_text = f'已重置 {dict(ROLE_ORDER)[self.current_role]} 默认外观'

    def handle_event(self, event):
        if event.type == pygame.QUIT:
            self.running = False
            return
        if event.type == pygame.VIDEORESIZE:
            self.window_width = max(1200, int(event.w))
            self.window_height = max(760, int(event.h))
            self.screen = pygame.display.set_mode((self.window_width, self.window_height), pygame.RESIZABLE)
            return
        if event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
            self._handle_click(event.pos)
            return
        if event.type == pygame.MOUSEBUTTONDOWN and event.button == 3:
            if self.preview_mode == '3d' and self.preview_content_rect is not None and self.preview_content_rect.collidepoint(event.pos):
                self.preview_drag_active = True
            return
        if event.type == pygame.MOUSEBUTTONUP and event.button in {1, 3}:
            self.field_scroll_drag_active = False
            self.preview_drag_active = False
            self.preview_action_drag_active = False
            return
        if event.type == pygame.MOUSEMOTION:
            if self.field_scroll_drag_active and self.field_panel_rect is not None and self.field_scrollbar_track_rect is not None and self.field_scrollbar_thumb_rect is not None:
                thumb_height = self.field_scrollbar_thumb_rect.height
                relative = event.pos[1] - self.field_scrollbar_track_rect.y - thumb_height * 0.5
                ratio = relative / max(1, self.field_scrollbar_track_rect.height - thumb_height)
                self._set_field_scroll(self.field_panel_rect, ratio * self._max_field_scroll(self.field_panel_rect))
                return
            if self.preview_action_drag_active:
                self._set_preview_action_progress_from_x(event.pos[0])
                return
            if self.preview_drag_active and self.preview_mode == '3d':
                rel_x, rel_y = getattr(event, 'rel', (0, 0))
                self.preview_3d_yaw += rel_x * 0.012
                self.preview_3d_pitch = max(0.12, min(1.12, self.preview_3d_pitch - rel_y * 0.010))
                return
        if event.type == pygame.MOUSEWHEEL:
            if self.field_panel_rect is not None and self.field_panel_rect.collidepoint(pygame.mouse.get_pos()):
                self._set_field_scroll(self.field_panel_rect, self.field_scroll - event.y * 36)
                return
            if self.preview_mode == '3d' and self.preview_content_rect is not None and self.preview_content_rect.collidepoint(pygame.mouse.get_pos()):
                self.preview_3d_pitch = max(0.12, min(1.12, self.preview_3d_pitch + event.y * 0.04))
                return
            self._adjust_selected(event.y, fast=bool(pygame.key.get_mods() & pygame.KMOD_SHIFT))
            return
        if event.type != pygame.KEYDOWN:
            return
        if self.active_numeric_input is not None and self._handle_numeric_input_keydown(event):
            return
        modifiers = pygame.key.get_mods()
        if event.key == pygame.K_ESCAPE:
            self.running = False
            return
        if event.key == pygame.K_TAB:
            role_keys = [role_key for role_key, _ in ROLE_ORDER]
            current_index = role_keys.index(self.current_role)
            self.current_role = role_keys[(current_index + 1) % len(role_keys)]
            self.selected_part = None
            self.selected_field_index = 0
            self.active_numeric_input = None
            return
        if self._part_supports_component_selection(self.selected_part):
            if event.key == pygame.K_a:
                self.selected_component_scope = 'all' if self.selected_component_scope == 'single' else 'single'
                self.active_numeric_input = None
                return
            if event.key == pygame.K_LEFTBRACKET:
                self._change_selected_component(-1)
                return
            if event.key == pygame.K_RIGHTBRACKET:
                self._change_selected_component(1)
                return
        if event.key == pygame.K_r:
            self._reset_current_role()
            return
        if event.key == pygame.K_s and modifiers & pygame.KMOD_CTRL:
            self._save_profiles()
            return
        numeric_text = str(getattr(event, 'unicode', '') or '')
        if numeric_text and not (modifiers & (pygame.KMOD_CTRL | pygame.KMOD_ALT)) and numeric_text in '0123456789.-':
            if self._begin_numeric_input(numeric_text):
                return
        if event.key == pygame.K_UP:
            visible_fields = self._visible_field_specs()
            if not visible_fields:
                return
            self.selected_field_index = max(0, self.selected_field_index - 1)
            if self.field_panel_rect is not None:
                self._ensure_selected_field_visible(self.field_panel_rect)
            return
        if event.key == pygame.K_DOWN:
            visible_fields = self._visible_field_specs()
            if not visible_fields:
                return
            self.selected_field_index = min(len(visible_fields) - 1, self.selected_field_index + 1)
            if self.field_panel_rect is not None:
                self._ensure_selected_field_visible(self.field_panel_rect)
            return
        if event.key in {pygame.K_LEFT, pygame.K_MINUS, pygame.K_KP_MINUS}:
            self._adjust_selected(-1, fast=bool(modifiers & pygame.KMOD_SHIFT))
            return
        if event.key in {pygame.K_RIGHT, pygame.K_EQUALS, pygame.K_PLUS, pygame.K_KP_PLUS}:
            self._adjust_selected(1, fast=bool(modifiers & pygame.KMOD_SHIFT))
            return
        if event.key in {pygame.K_1, pygame.K_2, pygame.K_3, pygame.K_4}:
            role_index = int(event.unicode) - 1 if event.unicode in {'1', '2', '3', '4'} else None
            if role_index is not None and 0 <= role_index < len(ROLE_ORDER):
                self.current_role = ROLE_ORDER[role_index][0]

    def render(self):
        self.screen.fill(self.colors['bg'])
        self._draw_header()
        field_panel, preview_panel = self._layout_panels()
        self._draw_fields_panel(field_panel)
        self._draw_preview_panel(preview_panel)
        self._draw_footer()
        pygame.display.flip()

    def run(self):
        while self.running:
            for event in pygame.event.get():
                self.handle_event(event)
            self.render()
            self.clock.tick(60)
        pygame.quit()


def main():
    app = AppearanceEditorApp()
    app.run()


if __name__ == '__main__':
    main()