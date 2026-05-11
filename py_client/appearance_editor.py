#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import math
import os
import subprocess
import sys
from copy import deepcopy
from typing import Any, cast

import numpy as np
from pygame_compat import pygame

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
WORKSPACE_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

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
    ('outpost', '前哨站'),
    ('base', '基地'),
    ('energy_mechanism', '能量机关'),
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
    'assembly': '装配机构',
    'turret': '云台',
    'barrel': '枪管',
    'armor': '装甲',
    'armor_light': '装甲灯条',
    'barrel_light': '枪管灯条',
    'rear_health_light': '血条灯条',
}

PART_LABELS.update({
    'hero_subview_camera': '小相机',
    'custom_primitive': '附加体',
    'custom_anchor': '锚点',
    'custom_link': '连杆',
})

PART_LABELS.update({
    'first_person_camera': '第一人称相机',
    'barrel_friction_wheel': '摩擦轮',
})

BALANCE_LEG_PARENT_PART_OPTIONS = [
    ('balance_leg', '平衡腿整体'),
    ('balance_leg_left', '平衡腿整体 左'),
    ('balance_leg_right', '平衡腿整体 右'),
    ('balance_leg_mount_front', '平衡腿前安装连杆'),
    ('balance_leg_mount_front_left', '平衡腿前安装连杆 左'),
    ('balance_leg_mount_front_right', '平衡腿前安装连杆 右'),
    ('balance_leg_mount_rear', '平衡腿后安装连杆'),
    ('balance_leg_mount_rear_left', '平衡腿后安装连杆 左'),
    ('balance_leg_mount_rear_right', '平衡腿后安装连杆 右'),
    ('balance_leg_mount_cross', '平衡腿安装横梁'),
    ('balance_leg_mount_cross_left', '平衡腿安装横梁 左'),
    ('balance_leg_mount_cross_right', '平衡腿安装横梁 右'),
    ('balance_leg_upper_front', '平衡腿前上连杆'),
    ('balance_leg_upper_front_left', '平衡腿前上连杆 左'),
    ('balance_leg_upper_front_right', '平衡腿前上连杆 右'),
    ('balance_leg_upper_rear', '平衡腿后上连杆'),
    ('balance_leg_upper_rear_left', '平衡腿后上连杆 左'),
    ('balance_leg_upper_rear_right', '平衡腿后上连杆 右'),
    ('balance_leg_upper_front_hinge', '平衡腿前上铰点'),
    ('balance_leg_upper_front_hinge_left', '平衡腿前上铰点 左'),
    ('balance_leg_upper_front_hinge_right', '平衡腿前上铰点 右'),
    ('balance_leg_upper_rear_hinge', '平衡腿后上铰点'),
    ('balance_leg_upper_rear_hinge_left', '平衡腿后上铰点 左'),
    ('balance_leg_upper_rear_hinge_right', '平衡腿后上铰点 右'),
    ('balance_leg_lower', '平衡腿下连杆'),
    ('balance_leg_lower_left', '平衡腿下连杆 左'),
    ('balance_leg_lower_right', '平衡腿下连杆 右'),
    ('balance_leg_knee_front_hinge', '平衡腿前膝铰点'),
    ('balance_leg_knee_front_hinge_left', '平衡腿前膝铰点 左'),
    ('balance_leg_knee_front_hinge_right', '平衡腿前膝铰点 右'),
    ('balance_leg_knee_rear_hinge', '平衡腿后膝铰点'),
    ('balance_leg_knee_rear_hinge_left', '平衡腿后膝铰点 左'),
    ('balance_leg_knee_rear_hinge_right', '平衡腿后膝铰点 右'),
    ('balance_leg_knee', '平衡腿膝点'),
    ('balance_leg_knee_left', '平衡腿膝点 左'),
    ('balance_leg_knee_right', '平衡腿膝点 右'),
    ('balance_leg_foot', '平衡腿足端'),
    ('balance_leg_foot_left', '平衡腿足端 左'),
    ('balance_leg_foot_right', '平衡腿足端 右'),
]

BALANCE_LEG_SEGMENT_OPTIONS = [
    ('balance_leg_upper_front', '上1连杆'),
    ('balance_leg_upper_rear', '上2连杆'),
    ('balance_leg_lower', '下连杆'),
]

BALANCE_LEG_PARENT_KEYS = {key for key, _label in BALANCE_LEG_PARENT_PART_OPTIONS}
BALANCE_LEG_PARENT_KEYS.update(key.replace('balance_leg', 'rear_leg', 1) for key, _label in BALANCE_LEG_PARENT_PART_OPTIONS)
BALANCE_LEG_PARENT_KEYS.update(key for key, _label in BALANCE_LEG_SEGMENT_OPTIONS)
BALANCE_LEG_PARENT_KEYS.update(key.replace('balance_leg', 'rear_leg', 1) for key, _label in BALANCE_LEG_SEGMENT_OPTIONS)

PART_LABELS.update(dict(BALANCE_LEG_PARENT_PART_OPTIONS))
PART_LABELS.update({
    key.replace('balance_leg', 'rear_leg', 1): label.replace('平衡腿', '后腿', 1)
    for key, label in BALANCE_LEG_PARENT_PART_OPTIONS
})

CUSTOM_PARENT_PART_OPTIONS = [
    ('body', '底盘'),
    ('wheel', '车轮'),
    ('front_climb', '前爬升'),
    ('rear_climb', '后机构'),
    ('mount', '连接件'),
    ('turret', '云台'),
    ('barrel', '枪管'),
    ('armor', '装甲板'),
    ('armor_light', '装甲灯条'),
    ('barrel_light', '枪管灯条'),
    ('rear_health_light', '血条灯条'),
    ('hero_subview_camera', '小相机'),
]

CUSTOM_PARENT_PART_OPTIONS.extend([
    ('first_person_camera', '第一人称相机'),
    ('barrel_friction_wheel', '摩擦轮'),
    ('balance_leg', '平衡腿'),
])

CUSTOM_PARENT_PART_STORAGE_OPTIONS = CUSTOM_PARENT_PART_OPTIONS + BALANCE_LEG_PARENT_PART_OPTIONS + [
    (key.replace('balance_leg', 'rear_leg', 1), label.replace('平衡腿', '后腿', 1))
    for key, label in BALANCE_LEG_PARENT_PART_OPTIONS
]

CUSTOM_PRIMITIVE_TYPE_OPTIONS = [
    ('box', '长方体'),
    ('cylinder', '圆柱'),
    ('sphere', '球体'),
]

CUSTOM_SCOPE_OPTIONS = [
    ('single', '单个'),
    ('all', '全部'),
]

ANCHOR_MODE_OPTIONS = [
    ('fixed', '固定锚点'),
    ('active', '活动锚点'),
]

COLOR_SWATCHES = [
    [44, 44, 44],
    [28, 32, 38],
    [92, 96, 108],
    [124, 128, 134],
    [166, 174, 186],
    [232, 232, 236],
    [224, 229, 234],
    [255, 255, 255],
    [228, 76, 76],
    [255, 114, 94],
    [170, 36, 48],
    [255, 140, 48],
    [58, 112, 232],
    [42, 158, 255],
    [40, 78, 178],
    [42, 210, 224],
    [236, 182, 84],
    [255, 220, 96],
    [160, 118, 42],
    [112, 196, 132],
    [56, 168, 98],
    [132, 224, 176],
    [170, 128, 214],
    [218, 118, 214],
    [112, 82, 184],
    [16, 122, 118],
]

HERO_SUBVIEW_CAMERA_BODY_LENGTH_M = 0.07
HERO_SUBVIEW_CAMERA_BODY_WIDTH_M = 0.03
HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M = 0.03
HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M = 0.08

_BASE_PROFILE_TEMPLATES = {
    'hero': {
        'body_length_m': 0.65,
        'body_width_m': 0.55,
        'body_height_m': 0.19,
        'body_clearance_m': 0.07,
        'wheel_radius_m': 0.005,
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
        'armor_light_height_m': 0.00,
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
        'wheel_radius_m': 0.00,
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
        'armor_light_height_m': 0.00,
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
        'armor_light_height_m': 0.00,
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
        'wheel_radius_m': 0.00,
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
        'armor_light_height_m': 0.00,
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
    'outpost': {
        'body_shape': 'octagon',
        'body_length_m': 0.65,
        'body_width_m': 0.55,
        'body_height_m': 1.578,
        'structure_body_top_height_m': 1.216,
        'structure_head_base_height_m': 1.318,
        'structure_lower_shoulder_height_m': 0.571,
        'structure_upper_shoulder_height_m': 1.446,
        'structure_tower_radius_m': 0.20,
        'structure_top_armor_center_height_m': 1.633,
        'structure_top_armor_offset_x_m': 0.0,
        'structure_top_armor_offset_z_m': 0.255,
        'structure_top_armor_tilt_deg': 45.0,
        'body_clearance_m': 0.0,
        'structure_base_lift_m': 0.40,
        'wheel_radius_m': 0.03,
        'gimbal_length_m': 0.0,
        'gimbal_width_m': 0.0,
        'gimbal_body_height_m': 0.0,
        'gimbal_mount_gap_m': 0.0,
        'gimbal_mount_length_m': 0.0,
        'gimbal_mount_width_m': 0.0,
        'gimbal_mount_height_m': 0.0,
        'barrel_length_m': 0.0,
        'barrel_radius_m': 0.0,
        'gimbal_height_m': 0.0,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.13,
        'armor_plate_length_m': 0.13,
        'armor_plate_height_m': 0.13,
        'armor_plate_gap_m': 0.035,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.00,
        'barrel_light_length_m': 0.0,
        'barrel_light_width_m': 0.0,
        'barrel_light_height_m': 0.0,
        'body_render_width_scale': 1.0,
        'wheel_style': 'structure',
        'suspension_style': 'none',
        'arm_style': 'none',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'none',
        'custom_wheel_positions_m': [],
        'armor_orbit_yaws_deg': [],
        'armor_self_yaws_deg': [],
        'body_color_rgb': [156, 160, 166],
        'turret_color_rgb': [196, 200, 206],
        'armor_color_rgb': [206, 212, 218],
        'wheel_color_rgb': [62, 68, 78],
    },
    'base': {
        'body_shape': 'octagon',
        'body_length_m': 1.881,
        'body_width_m': 1.609,
        'body_height_m': 1.181,
        'structure_hex_top_edge_m': 1.089,
        'structure_roof_height_m': 1.03,
        'structure_shoulder_height_m': 0.860,
        'structure_detector_width_m': 0.980,
        'structure_detector_height_m': 0.095,
        'structure_detector_bridge_center_height_m': 1.093,
        'structure_detector_sensor_center_height_m': 1.136,
        'structure_top_armor_center_height_m': 1.150,
        'structure_top_armor_offset_x_m': 0.0,
        'structure_top_armor_offset_z_m': 0.0,
        'structure_top_armor_tilt_deg': 27.5,
        'structure_side_armor_open_angle_deg': 27.5,
        'structure_side_armor_outward_offset_m': 0.12,
        'structure_core_column_height_m': 0.783,
        'body_clearance_m': 0.0,
        'structure_base_lift_m': 0.0,
        'wheel_radius_m': 0.03,
        'gimbal_length_m': 0.0,
        'gimbal_width_m': 0.0,
        'gimbal_body_height_m': 0.0,
        'gimbal_mount_gap_m': 0.0,
        'gimbal_mount_length_m': 0.0,
        'gimbal_mount_width_m': 0.0,
        'gimbal_mount_height_m': 0.0,
        'barrel_length_m': 0.0,
        'barrel_radius_m': 0.0,
        'gimbal_height_m': 0.0,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.13,
        'armor_plate_length_m': 0.13,
        'armor_plate_height_m': 0.13,
        'armor_plate_gap_m': 0.035,
        'armor_light_length_m': 0.04,
        'armor_light_width_m': 0.005,
        'armor_light_height_m': 0.00,
        'barrel_light_length_m': 0.0,
        'barrel_light_width_m': 0.0,
        'barrel_light_height_m': 0.0,
        'body_render_width_scale': 1.0,
        'wheel_style': 'structure',
        'suspension_style': 'none',
        'arm_style': 'none',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'none',
        'custom_wheel_positions_m': [],
        'armor_orbit_yaws_deg': [],
        'armor_self_yaws_deg': [],
        'body_color_rgb': [142, 148, 154],
        'turret_color_rgb': [196, 200, 206],
        'armor_color_rgb': [206, 212, 218],
        'wheel_color_rgb': [62, 68, 78],
    },
    'energy_mechanism': {
        'body_shape': 'box',
        'body_length_m': 2.06,
        'body_width_m': 1.30,
        'body_height_m': 2.30,
        'body_clearance_m': 0.0,
        'structure_ground_clearance_m': 0.0,
        'structure_base_lift_m': 0.0,
        'structure_base_height_m': 0.30,
        'structure_base_length_m': 3.40,
        'structure_base_width_m': 3.18,
        'structure_base_top_length_m': 2.10,
        'structure_base_top_width_m': 1.08,
        'structure_base_top_height_m': 0.12,
        'structure_frame_width_m': 2.06,
        'structure_frame_depth_m': 0.16,
        'structure_frame_height_m': 2.30,
        'structure_column_span_m': 2.06,
        'structure_support_offset_m': 1.03,
        'structure_frame_column_width_m': 0.10,
        'structure_frame_beam_height_m': 0.09,
        'structure_rotor_center_height_m': 1.45,
        'structure_rotor_phase_deg': 90.0,
        'structure_rotor_radius_m': 1.40,
        'structure_rotor_hub_radius_m': 0.09,
        'structure_rotor_arm_length_m': 1.12,
        'structure_rotor_arm_width_m': 0.06,
        'structure_rotor_arm_height_m': 0.04,
        'structure_lamp_length_m': 0.30,
        'structure_lamp_width_m': 0.30,
        'structure_lamp_height_m': 0.00,
        'structure_lower_module_width_m': 0.20,
        'structure_lower_module_height_m': 0.24,
        'structure_lower_module_depth_m': 0.18,
        'structure_lower_module_offset_x_m': 0.48,
        'structure_lower_module_center_height_m': 0.94,
        'structure_hanger_width_m': 0.24,
        'structure_hanger_height_m': 0.24,
        'structure_hanger_depth_m': 0.06,
        'structure_hanger_center_height_m': 1.45,
        'structure_cantilever_pair_gap_m': 2.34,
        'structure_cantilever_length_m': 0.28,
        'structure_cantilever_offset_y_m': -0.02,
        'structure_cantilever_height_m': 0.00,
        'structure_cantilever_depth_m': 0.00,
        'wheel_radius_m': 0.03,
        'gimbal_length_m': 0.0,
        'gimbal_width_m': 0.0,
        'gimbal_body_height_m': 0.0,
        'gimbal_mount_gap_m': 0.0,
        'gimbal_mount_length_m': 0.0,
        'gimbal_mount_width_m': 0.0,
        'gimbal_mount_height_m': 0.0,
        'barrel_length_m': 0.0,
        'barrel_radius_m': 0.0,
        'gimbal_height_m': 0.0,
        'gimbal_offset_x_m': 0.0,
        'gimbal_offset_y_m': 0.0,
        'armor_plate_width_m': 0.18,
        'armor_plate_length_m': 0.18,
        'armor_plate_height_m': 0.10,
        'armor_plate_gap_m': 0.020,
        'armor_light_length_m': 0.12,
        'armor_light_width_m': 0.03,
        'armor_light_height_m': 0.06,
        'barrel_light_length_m': 0.0,
        'barrel_light_width_m': 0.0,
        'barrel_light_height_m': 0.0,
        'body_render_width_scale': 1.0,
        'wheel_style': 'structure',
        'suspension_style': 'none',
        'arm_style': 'none',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'none',
        'custom_wheel_positions_m': [],
        'armor_orbit_yaws_deg': [],
        'armor_self_yaws_deg': [],
        'body_color_rgb': [124, 128, 134],
        'turret_color_rgb': [170, 174, 180],
        'armor_color_rgb': [68, 72, 78],
        'wheel_color_rgb': [64, 132, 255],
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
        'rear_climb_assist_lower_length_m': 0.00,
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


def _normalize_choice(value, options, fallback):
    allowed = {key for key, _label in options}
    normalized = str(value or fallback).strip().lower()
    return normalized if normalized in allowed else str(fallback).strip().lower()


def _is_balance_leg_parent_part(value):
    return str(value or '').strip().lower() in BALANCE_LEG_PARENT_KEYS


def _balance_leg_segment_from_parent_part(value):
    normalized = str(value or '').strip().lower()
    if normalized.startswith('rear_leg'):
        normalized = 'balance_leg' + normalized[len('rear_leg'):]
    for segment_key, _label in BALANCE_LEG_SEGMENT_OPTIONS:
        if normalized == segment_key:
            return segment_key
    if 'upper_rear' in normalized:
        return 'balance_leg_upper_rear'
    if 'lower' in normalized or 'foot' in normalized:
        return 'balance_leg_lower'
    return 'balance_leg_upper_front'


def _normalize_vector3(value, fallback):
    if not isinstance(value, (list, tuple)) or len(value) < 3:
        return [float(fallback[0]), float(fallback[1]), float(fallback[2])]
    result = []
    for index in range(3):
        try:
            result.append(round(float(value[index]), 3))
        except Exception:
            result.append(float(fallback[index]))
    return result


def _normalize_custom_primitive(item, index=0):
    source = deepcopy(item) if isinstance(item, dict) else {}
    return {
        'id': str(source.get('id') or f'primitive_{index + 1:02d}'),
        'name': str(source.get('name') or f'附加体 {index + 1}'),
        'parent_part': _normalize_choice(source.get('parent_part'), CUSTOM_PARENT_PART_STORAGE_OPTIONS, 'body'),
        'component_scope': _normalize_choice(source.get('component_scope'), CUSTOM_SCOPE_OPTIONS, 'single'),
        'component_index': max(0, int(source.get('component_index', 0) or 0)),
        'primitive_type': _normalize_choice(source.get('primitive_type'), CUSTOM_PRIMITIVE_TYPE_OPTIONS, 'box'),
        'size_m': _normalize_vector3(source.get('size_m'), (0.06, 0.04, 0.04)),
        'offset_m': _normalize_vector3(source.get('offset_m'), (0.0, 0.0, 0.0)),
        'rotation_ypr_deg': _normalize_vector3(source.get('rotation_ypr_deg'), (0.0, 0.0, 0.0)),
        'color_rgb': _normalize_rgb_triplet(source.get('color_rgb'), [188, 192, 198]),
    }


def _normalize_custom_anchor(item, index=0):
    source = deepcopy(item) if isinstance(item, dict) else {}
    return {
        'id': str(source.get('id') or f'anchor_{index + 1:02d}'),
        'name': str(source.get('name') or f'锚点 {index + 1}'),
        'parent_part': _normalize_choice(source.get('parent_part'), CUSTOM_PARENT_PART_STORAGE_OPTIONS, 'body'),
        'anchor_mode': _normalize_choice(source.get('anchor_mode'), ANCHOR_MODE_OPTIONS, 'fixed'),
        'parent_link_id': str(source.get('parent_link_id') or ''),
        'link_position_ratio': min(1.0, max(0.0, float(source.get('link_position_ratio', 0.5) or 0.5))),
        'component_scope': _normalize_choice(source.get('component_scope'), CUSTOM_SCOPE_OPTIONS, 'single'),
        'component_index': max(0, int(source.get('component_index', 0) or 0)),
        'offset_m': _normalize_vector3(source.get('offset_m'), (0.0, 0.0, 0.0)),
        'rotation_ypr_deg': _normalize_vector3(source.get('rotation_ypr_deg'), (0.0, 0.0, 0.0)),
    }


def _normalize_custom_link(item, anchor_ids, index=0):
    source = deepcopy(item) if isinstance(item, dict) else {}
    default_length = 0.20 if not source else 0.0
    fallback_start = anchor_ids[0] if anchor_ids else ''
    fallback_end = anchor_ids[1] if len(anchor_ids) > 1 else fallback_start
    start_anchor_id = str(source.get('start_anchor_id') or fallback_start)
    end_anchor_id = str(source.get('end_anchor_id') or fallback_end)
    if anchor_ids:
        if start_anchor_id not in anchor_ids:
            start_anchor_id = fallback_start
        if end_anchor_id not in anchor_ids:
            end_anchor_id = fallback_end
    return {
        'id': str(source.get('id') or f'link_{index + 1:02d}'),
        'name': str(source.get('name') or f'连杆 {index + 1}'),
        'start_anchor_id': start_anchor_id,
        'end_anchor_id': end_anchor_id,
        'radius_m': max(0.001, round(float(source.get('radius_m', 0.012) or 0.012), 3)),
        'width_m': max(0.001, round(float(source.get('width_m', (source.get('radius_m') or 0.012) * 2.0) or 0.024), 3)),
        'thickness_m': max(0.001, round(float(source.get('thickness_m', (source.get('radius_m') or 0.012) * 2.0) or 0.024), 3)),
        'length_m': max(0.0, round(float(source.get('length_m', default_length) or default_length), 3)),
        'color_rgb': _normalize_rgb_triplet(source.get('color_rgb'), [176, 182, 190]),
    }


def _normalize_custom_collections(profile):
    primitives = [_normalize_custom_primitive(item, index) for index, item in enumerate(profile.get('custom_primitives', []))]
    anchors = [_normalize_custom_anchor(item, index) for index, item in enumerate(profile.get('custom_anchors', []))]
    anchor_ids = [item['id'] for item in anchors]
    links = [_normalize_custom_link(item, anchor_ids, index) for index, item in enumerate(profile.get('custom_links', []))]
    profile['custom_primitives'] = primitives
    profile['custom_anchors'] = anchors
    profile['custom_links'] = links
    return profile


def _normalize_field_spec_bounds(fields):
    return fields


def _normalize_profile_constraints(role_key, profile, forced_subtype=None):
    normalized = deepcopy(_BASE_PROFILE_TEMPLATES.get(role_key, _BASE_PROFILE_TEMPLATES['infantry']))
    if isinstance(profile, dict):
        normalized.update(deepcopy(profile))
    normalized['role_key'] = role_key
    has_first_person_camera_x = 'first_person_camera_offset_x_m' in normalized
    has_first_person_camera_y = 'first_person_camera_offset_y_m' in normalized
    normalized.update({key: deepcopy(value) for key, value in _default_color_profile().items() if key not in normalized})
    normalized.setdefault('rear_health_light_length_m', 0.0)
    normalized.setdefault('rear_health_light_width_m', 0.0)
    normalized.setdefault('rear_health_light_height_m', 0.0)
    normalized.setdefault('rear_health_light_offset_x_m', 0.0)
    normalized.setdefault('rear_health_light_offset_y_m', 0.0)
    normalized.setdefault('rear_health_light_offset_z_m', 0.0)
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
    for key in (
        'body_front_tilt_deg',
        'body_rear_tilt_deg',
        'body_left_tilt_deg',
        'body_right_tilt_deg',
        'gimbal_relative_offset_x_m',
        'gimbal_relative_offset_y_m',
        'gimbal_relative_offset_z_m',
        'barrel_offset_x_m',
        'barrel_offset_y_m',
        'barrel_offset_z_m',
        'barrel_light_offset_x_m',
        'barrel_light_offset_y_m',
        'barrel_light_offset_z_m',
        'barrel_octagon_long_edge_m',
        'barrel_octagon_short_edge_m',
        'barrel_friction_wheel_radius_m',
        'barrel_friction_wheel_width_m',
        'barrel_friction_wheel_height_m',
        'barrel_friction_wheel_offset_x_m',
        'barrel_friction_wheel_offset_y_m',
        'barrel_friction_wheel_offset_z_m',
        'barrel_friction_wheel_yaw_deg',
        'barrel_friction_wheel_pitch_deg',
        'barrel_friction_wheel_roll_deg',
        'first_person_camera_offset_x_m',
        'first_person_camera_offset_y_m',
        'first_person_camera_offset_z_m',
        'first_person_camera_yaw_deg',
        'first_person_camera_pitch_deg',
        'first_person_camera_roll_deg',
    ):
        normalized[key] = float(normalized.get(key, 0.0))
    if not has_first_person_camera_x:
        normalized['first_person_camera_offset_x_m'] = 0.04
    if not has_first_person_camera_y:
        normalized['first_person_camera_offset_y_m'] = 0.06
    if normalized['barrel_octagon_long_edge_m'] <= 1e-9:
        normalized['barrel_octagon_long_edge_m'] = max(0.004, float(normalized.get('barrel_radius_m', 0.0)) * 1.80)
    if normalized['barrel_octagon_short_edge_m'] <= 1e-9:
        normalized['barrel_octagon_short_edge_m'] = max(0.002, float(normalized.get('barrel_radius_m', 0.0)) * 0.72)
    if normalized['barrel_friction_wheel_height_m'] <= 1e-9:
        normalized['barrel_friction_wheel_height_m'] = float(normalized.get('barrel_friction_wheel_width_m', 0.0))
    normalized['armor_plate_offsets_m'] = [
        _normalize_vector3(item, (0.0, 0.0, 0.0))
        for item in normalized.get('armor_plate_offsets_m', [])
        if isinstance(item, (list, tuple))
    ]
    normalized['armor_plate_rotations_ypr_deg'] = [
        _normalize_vector3(item, (0.0, 0.0, 0.0))
        for item in normalized.get('armor_plate_rotations_ypr_deg', [])
        if isinstance(item, (list, tuple))
    ]
    if 'armor_plate_thickness_m' not in normalized:
        normalized['armor_plate_thickness_m'] = max(
            0.004,
            float(normalized.get('armor_plate_gap_m', 0.005)) * 0.75,
            float(normalized.get('armor_plate_width_m', 0.16)) * 0.08,
        )
    normalized['armor_plate_thickness_m'] = float(normalized.get('armor_plate_thickness_m', 0.004))
    normalized['armor_light_offsets_m'] = [
        _normalize_vector3(item, (0.0, 0.0, 0.0))
        for item in normalized.get('armor_light_offsets_m', [])
        if isinstance(item, (list, tuple))
    ]
    normalized['armor_light_plate_distances_m'] = [
        max(0.0, float(item))
        for item in normalized.get('armor_light_plate_distances_m', [])
        if isinstance(item, (int, float))
    ]
    normalized['barrel_friction_wheel_offsets_m'] = [
        _normalize_vector3(item, (0.0, 0.0, 0.0))
        for item in normalized.get('barrel_friction_wheel_offsets_m', [])
        if isinstance(item, (list, tuple))
    ]
    _normalize_custom_collections(normalized)

    if role_key in {'outpost', 'base', 'energy_mechanism'}:
        normalized['body_shape'] = 'box' if role_key == 'energy_mechanism' else 'octagon'
        normalized['wheel_style'] = 'structure'
        normalized['suspension_style'] = 'none'
        normalized['front_climb_assist_style'] = 'none'
        normalized['rear_climb_assist_style'] = 'none'
        normalized['custom_wheel_positions_m'] = []
        normalized['armor_orbit_yaws_deg'] = []
        normalized['armor_self_yaws_deg'] = []
        normalized['structure_base_lift_m'] = max(
            0.0,
            min(1.2, float(normalized.get('structure_base_lift_m', 0.40 if role_key == 'outpost' else 0.0))),
        )
        normalized['gimbal_length_m'] = 0.0
        normalized['gimbal_width_m'] = 0.0
        normalized['gimbal_body_height_m'] = 0.0
        normalized['gimbal_mount_length_m'] = 0.0
        normalized['gimbal_mount_width_m'] = 0.0
        normalized['gimbal_mount_height_m'] = 0.0
        normalized['gimbal_mount_gap_m'] = 0.0
        normalized['barrel_length_m'] = 0.0
        normalized['barrel_radius_m'] = 0.0
        normalized['gimbal_height_m'] = 0.0
        return normalized

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
    return body_top + mount_gap + mount_height + turret_half_height + float(profile.get('gimbal_relative_offset_y_m', 0.0))


def _profile_mount_offset_x(profile):
    return float(profile.get('gimbal_offset_x_m', 0.0))


def _profile_mount_offset_z(profile):
    return float(profile.get('gimbal_offset_y_m', 0.0))


def _profile_turret_offset_x(profile):
    return _profile_mount_offset_x(profile) + float(profile.get('gimbal_relative_offset_x_m', 0.0))


def _profile_turret_offset_z(profile):
    return _profile_mount_offset_z(profile) + float(profile.get('gimbal_relative_offset_z_m', 0.0))


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
    if role_key == 'outpost':
        return (('idle', '静态'), ('rotate', '装甲旋转'))
    if role_key == 'base':
        return (('idle', '静态'), ('open', '开合预览'))
    if role_key == 'energy_mechanism':
        return (('idle', '静态'), ('rotate', '转臂旋转'))
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
    lower_length = float(profile.get('rear_climb_assist_lower_length_m', 0.00))
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
        float(profile.get('rear_climb_assist_lower_length_m', 0.00)),
        profile.get('rear_climb_assist_knee_min_deg', 42.0),
        profile.get('rear_climb_assist_knee_max_deg', 132.0),
    )
    knee_x, knee_y = _select_balance_leg_joint(
        (upper_anchor_x, upper_anchor_y),
        (foot_x, foot_y),
        float(profile.get('rear_climb_assist_upper_length_m', 0.09)),
        float(profile.get('rear_climb_assist_lower_length_m', 0.00)),
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


def _balance_leg_wheel_side_offset(profile, leg_geometry):
    wheel_radius = max(0.018, float(profile.get('rear_leg_wheel_radius_m', profile.get('wheel_radius_m', 0.08))))
    leg_half_width = max(
        float(profile.get('rear_climb_assist_upper_width_m', 0.016)),
        float(profile.get('rear_climb_assist_lower_width_m', 0.016)),
    ) * 0.5
    wheel_half_width = 0.020
    clearance = max(0.006, wheel_radius * 0.04)
    return float(leg_geometry['side_offset']) + leg_half_width + wheel_half_width + clearance


def _append_preview_attachment_pose(poses, part, index, center, yaw_rad=0.0, pitch_rad=0.0, roll_rad=0.0):
    poses.append({
        'part': part,
        'index': int(index),
        'center': tuple(float(value) for value in center),
        'yaw_rad': float(yaw_rad),
        'pitch_rad': float(pitch_rad),
        'roll_rad': float(roll_rad),
    })


def _append_preview_attachment_pose_aliases(poses, part, side_index, center, yaw_rad=0.0, pitch_rad=0.0, roll_rad=0.0):
    _append_preview_attachment_pose(poses, part, side_index, center, yaw_rad, pitch_rad, roll_rad)
    side_name = 'left' if int(side_index) == 0 else 'right'
    _append_preview_attachment_pose(poses, f'{part}_{side_name}', 0, center, yaw_rad, pitch_rad, roll_rad)
    if str(part).startswith('balance_leg'):
        rear_leg_part = 'rear_leg' + str(part)[len('balance_leg'):]
        _append_preview_attachment_pose(poses, rear_leg_part, side_index, center, yaw_rad, pitch_rad, roll_rad)
        _append_preview_attachment_pose(poses, f'{rear_leg_part}_{side_name}', 0, center, yaw_rad, pitch_rad, roll_rad)


def _append_balance_leg_preview_beam_pose(poses, part, side_index, start, end, side_z):
    start_x, start_y = float(start[0]), float(start[1])
    end_x, end_y = float(end[0]), float(end[1])
    dx = end_x - start_x
    dy = end_y - start_y
    if math.hypot(dx, dy) <= 1e-6:
        return
    center = ((start_x + end_x) * 0.5, (start_y + end_y) * 0.5, float(side_z))
    _append_preview_attachment_pose_aliases(poses, part, side_index, center, 0.0, math.atan2(dy, dx), 0.0)


def _append_balance_leg_preview_point_pose(poses, part, side_index, point, side_z):
    _append_preview_attachment_pose_aliases(poses, part, side_index, (float(point[0]), float(point[1]), float(side_z)))


def _extend_balance_leg_preview_attachment_poses(poses, profile, render_width_scale):
    leg = _balance_leg_geometry(profile, render_width_scale)
    body_side_offset = max(0.02, float(profile['body_width_m']) * render_width_scale * 0.5 * 0.98)
    for side_index, side_sign in enumerate((-1.0, 1.0)):
        side_z = float(leg['side_offset']) * side_sign
        mount_z = body_side_offset * side_sign
        aggregate_center = (
            (float(leg['upper_front'][0]) + float(leg['upper_rear'][0]) + float(leg['knee_center'][0]) + float(leg['foot'][0])) * 0.25,
            (float(leg['upper_front'][1]) + float(leg['upper_rear'][1]) + float(leg['knee_center'][1]) + float(leg['foot'][1])) * 0.25,
            side_z,
        )
        _append_preview_attachment_pose_aliases(poses, 'balance_leg', side_index, aggregate_center)
        _append_preview_attachment_pose(poses, 'rear_climb', side_index, aggregate_center)

        _append_preview_attachment_pose_aliases(poses, 'balance_leg_mount_front', side_index, (float(leg['upper_front'][0]), float(leg['upper_front'][1]), (mount_z + side_z) * 0.5), math.pi * 0.5)
        _append_preview_attachment_pose_aliases(poses, 'balance_leg_mount_rear', side_index, (float(leg['upper_rear'][0]), float(leg['upper_rear'][1]), (mount_z + side_z) * 0.5), math.pi * 0.5)
        _append_balance_leg_preview_beam_pose(poses, 'balance_leg_mount_cross', side_index, leg['upper_front'], leg['upper_rear'], mount_z)
        _append_balance_leg_preview_beam_pose(poses, 'balance_leg_upper_front', side_index, leg['upper_front'], leg['knee_front'], side_z)
        _append_balance_leg_preview_beam_pose(poses, 'balance_leg_upper_rear', side_index, leg['upper_rear'], leg['knee_rear'], side_z)
        _append_balance_leg_preview_beam_pose(poses, 'balance_leg_lower', side_index, leg['knee_center'], leg['foot'], side_z)

        _append_balance_leg_preview_point_pose(poses, 'balance_leg_upper_front_hinge', side_index, leg['upper_front'], side_z)
        _append_balance_leg_preview_point_pose(poses, 'balance_leg_upper_rear_hinge', side_index, leg['upper_rear'], side_z)
        _append_balance_leg_preview_point_pose(poses, 'balance_leg_knee_front_hinge', side_index, leg['knee_front'], side_z)
        _append_balance_leg_preview_point_pose(poses, 'balance_leg_knee_rear_hinge', side_index, leg['knee_rear'], side_z)
        _append_balance_leg_preview_point_pose(poses, 'balance_leg_knee', side_index, leg['knee_center'], side_z)
        _append_balance_leg_preview_point_pose(poses, 'balance_leg_foot', side_index, leg['foot'], side_z)


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


def _preview_vec3_tuple(vector):
    return (float(vector[0]), float(vector[1]), float(vector[2]))


def _normalize_preview_axis(vector, fallback):
    candidate = np.array(vector, dtype='f4')
    norm = float(np.linalg.norm(candidate))
    if norm > 1e-6:
        return candidate / norm
    fallback_vector = np.array(fallback, dtype='f4')
    fallback_norm = float(np.linalg.norm(fallback_vector))
    return fallback_vector / fallback_norm if fallback_norm > 1e-6 else np.array([1.0, 0.0, 0.0], dtype='f4')


def _rotate_preview_vector(vector, axis, angle_rad):
    if abs(float(angle_rad)) <= 1e-6:
        return np.array(vector, dtype='f4')
    normalized_axis = _normalize_preview_axis(axis, (0.0, 1.0, 0.0))
    source = np.array(vector, dtype='f4')
    cos_angle = math.cos(float(angle_rad))
    sin_angle = math.sin(float(angle_rad))
    return (
        source * cos_angle
        + np.cross(normalized_axis, source) * sin_angle
        + normalized_axis * float(np.dot(normalized_axis, source)) * (1.0 - cos_angle)
    )


def _resolve_preview_rotated_axes(base_yaw_rad, rotation_ypr_deg):
    cos_yaw = math.cos(float(base_yaw_rad))
    sin_yaw = math.sin(float(base_yaw_rad))
    forward = np.array([cos_yaw, 0.0, sin_yaw], dtype='f4')
    right = np.array([-sin_yaw, 0.0, cos_yaw], dtype='f4')
    up = np.array([0.0, 1.0, 0.0], dtype='f4')
    return _resolve_preview_rotated_basis(forward, right, up, rotation_ypr_deg)


def _resolve_preview_rotated_basis(base_forward, base_right, base_up, rotation_ypr_deg):
    forward = _normalize_preview_axis(base_forward, (1.0, 0.0, 0.0))
    right = _normalize_preview_axis(base_right, (0.0, 0.0, 1.0))
    up = _normalize_preview_axis(base_up, (0.0, 1.0, 0.0))
    rotation = list(rotation_ypr_deg or [0.0, 0.0, 0.0])
    while len(rotation) < 3:
        rotation.append(0.0)

    def rotate_basis(axis, angle_rad):
        nonlocal forward, right, up
        forward = _normalize_preview_axis(_rotate_preview_vector(forward, axis, angle_rad), forward)
        right = _normalize_preview_axis(_rotate_preview_vector(right, axis, angle_rad), right)
        up = _normalize_preview_axis(_rotate_preview_vector(up, axis, angle_rad), up)

    rotate_basis(up, math.radians(float(rotation[0])))
    rotate_basis(right, math.radians(float(rotation[1])))
    rotate_basis(forward, math.radians(float(rotation[2])))
    return forward, right, up


def _preview_local_point(parent_center, forward, right, up, local_offset):
    offset = list(local_offset or [0.0, 0.0, 0.0])
    while len(offset) < 3:
        offset.append(0.0)
    return (
        np.array(parent_center, dtype='f4')
        + np.array(forward, dtype='f4') * float(offset[0])
        + np.array(up, dtype='f4') * float(offset[1])
        + np.array(right, dtype='f4') * float(offset[2])
    )


def _resolve_fixed_link_end(start_point, end_point, fixed_length):
    start_vec = np.array(start_point, dtype='f4')
    end_vec = np.array(end_point, dtype='f4')
    axis = end_vec - start_vec
    distance = float(np.linalg.norm(axis))
    if distance <= 1e-6 or fixed_length <= 1e-6:
        return tuple(end_vec)
    return tuple(start_vec + axis / distance * float(fixed_length))


def _is_active_anchor(anchor):
    return str(anchor.get('anchor_mode', 'fixed')).lower() in {'active', 'link'}


def _resolve_preview_custom_anchor_point_variants(profile, poses):
    def matching_poses(parent_part, component_scope, component_index):
        for pose in poses:
            if pose['part'] != parent_part:
                continue
            if component_scope == 'all' or int(pose['index']) == int(component_index):
                yield pose

    def pose_basis(pose):
        return _resolve_preview_rotated_axes(
            float(pose.get('yaw_rad', 0.0)),
            [0.0, math.degrees(float(pose.get('pitch_rad', 0.0))), math.degrees(float(pose.get('roll_rad', 0.0)))],
        )

    def add_anchor_variant(target, anchor_id, resolved):
        if not anchor_id:
            return
        target.setdefault(anchor_id, []).append(resolved)

    def pair_anchor_variants(start_anchor_id, end_anchor_id):
        starts = anchor_points.get(str(start_anchor_id), [])
        ends = anchor_points.get(str(end_anchor_id), [])
        if not starts or not ends:
            return []
        count = max(len(starts), len(ends))
        return [
            (starts[min(index, len(starts) - 1)], ends[min(index, len(ends) - 1)])
            for index in range(count)
        ]

    anchor_points = {}
    anchor_fallbacks = {}
    for anchor in profile.get('custom_anchors', []):
        parent_part = str(anchor.get('parent_part', 'body'))
        component_scope = str(anchor.get('component_scope', 'single'))
        component_index = int(anchor.get('component_index', 0))
        anchor_id = str(anchor.get('id', ''))
        for pose in matching_poses(parent_part, component_scope, component_index):
            base_forward, base_right, base_up = pose_basis(pose)
            forward, right, up = _resolve_preview_rotated_basis(base_forward, base_right, base_up, anchor.get('rotation_ypr_deg', [0.0, 0.0, 0.0]))
            point = _preview_local_point(pose['center'], base_forward, base_right, base_up, anchor.get('offset_m', [0.0, 0.0, 0.0]))
            resolved = {
                'point': tuple(float(value) for value in point),
                'forward': tuple(float(value) for value in forward),
                'right': tuple(float(value) for value in right),
                'up': tuple(float(value) for value in up),
            }
            if _is_active_anchor(anchor):
                add_anchor_variant(anchor_fallbacks, anchor_id, resolved)
            else:
                add_anchor_variant(anchor_points, anchor_id, resolved)

    link_points = {}
    progress = True
    for _pass in range(5):
        if not progress:
            break
        progress = False
        for link in profile.get('custom_links', []):
            link_id = str(link.get('id', ''))
            if link_id in link_points:
                continue
            pairs = pair_anchor_variants(link.get('start_anchor_id', ''), link.get('end_anchor_id', ''))
            if not pairs:
                continue
            link_points[link_id] = [(start['point'], end['point']) for start, end in pairs]
            progress = True

        for anchor in profile.get('custom_anchors', []):
            anchor_id = str(anchor.get('id', ''))
            if not _is_active_anchor(anchor) or anchor_id in anchor_points:
                continue
            parent_link_id = str(anchor.get('parent_link_id', ''))
            link_poses = link_points.get(parent_link_id)
            if link_poses is None and not parent_link_id and link_points:
                link_poses = next(iter(link_points.values()))
            if not link_poses:
                continue
            for link_pose in link_poses:
                start_vec = np.array(link_pose[0], dtype='f4')
                end_vec = np.array(link_pose[1], dtype='f4')
                axis = end_vec - start_vec
                distance = float(np.linalg.norm(axis))
                if distance <= 1e-6:
                    continue
                forward = _normalize_preview_axis(axis, (1.0, 0.0, 0.0))
                up = np.array((0.0, 1.0, 0.0), dtype='f4')
                if abs(float(np.dot(forward, up))) >= 0.92:
                    up = np.array((1.0, 0.0, 0.0), dtype='f4')
                right = _normalize_preview_axis(np.cross(up, forward), (0.0, 0.0, 1.0))
                up = _normalize_preview_axis(np.cross(forward, right), (0.0, 1.0, 0.0))
                ratio = min(1.0, max(0.0, float(anchor.get('link_position_ratio', 0.5))))
                base_point = start_vec + axis * ratio
                forward, right, up = _resolve_preview_rotated_basis(forward, right, up, anchor.get('rotation_ypr_deg', [0.0, 0.0, 0.0]))
                point = _preview_local_point(base_point, forward, right, up, anchor.get('offset_m', [0.0, 0.0, 0.0]))
                add_anchor_variant(anchor_points, anchor_id, {
                    'point': tuple(float(value) for value in point),
                    'forward': tuple(float(value) for value in forward),
                    'right': tuple(float(value) for value in right),
                    'up': tuple(float(value) for value in up),
                })
                progress = True

    for anchor in profile.get('custom_anchors', []):
        anchor_id = str(anchor.get('id', ''))
        if _is_active_anchor(anchor) and anchor_id not in anchor_points and anchor_id in anchor_fallbacks:
            anchor_points[anchor_id] = anchor_fallbacks[anchor_id]

    return anchor_points


def _resolve_preview_custom_anchor_points(profile, poses):
    return {
        anchor_id: variants[0]
        for anchor_id, variants in _resolve_preview_custom_anchor_point_variants(profile, poses).items()
        if variants
    }


def _pair_preview_anchor_variants(anchor_variants, start_anchor_id, end_anchor_id):
    starts = anchor_variants.get(str(start_anchor_id), [])
    ends = anchor_variants.get(str(end_anchor_id), [])
    if not starts or not ends:
        return []
    count = max(len(starts), len(ends))
    return [
        (starts[min(index, len(starts) - 1)], ends[min(index, len(ends) - 1)])
        for index in range(count)
    ]


def _first_custom_link_id(profile):
    for link in profile.get('custom_links', []):
        link_id = str(link.get('id', '')).strip()
        if link_id:
            return link_id
    return ''


def _append_preview_oriented_box(vertices, center, half_extents, color_rgb, forward, right, up):
    center_vec = np.array(center, dtype='f4')
    forward_vec = _normalize_preview_axis(forward, (1.0, 0.0, 0.0))
    right_vec = _normalize_preview_axis(right, (0.0, 0.0, 1.0))
    up_vec = _normalize_preview_axis(up, (0.0, 1.0, 0.0))
    half_x, half_y, half_z = [max(0.001, float(value)) for value in half_extents]
    color = tuple(float(channel) / 255.0 for channel in color_rgb)

    def point(local_x, local_y, local_z):
        return _preview_vec3_tuple(center_vec + forward_vec * local_x + up_vec * local_y + right_vec * local_z)

    def normal(local_x, local_y, local_z):
        return _preview_vec3_tuple(_normalize_preview_axis(forward_vec * local_x + up_vec * local_y + right_vec * local_z, (0.0, 1.0, 0.0)))

    corners = {
        'lbn': point(-half_x, -half_y, -half_z),
        'rbn': point(half_x, -half_y, -half_z),
        'rbs': point(half_x, -half_y, half_z),
        'lbs': point(-half_x, -half_y, half_z),
        'ltn': point(-half_x, half_y, -half_z),
        'rtn': point(half_x, half_y, -half_z),
        'rts': point(half_x, half_y, half_z),
        'lts': point(-half_x, half_y, half_z),
    }
    face_specs = (
        (('ltn', 'rtn', 'rts', 'lts'), normal(0.0, 1.0, 0.0), 1.0),
        (('lbs', 'rbs', 'rbn', 'lbn'), normal(0.0, -1.0, 0.0), 0.42),
        (('lbn', 'rbn', 'rtn', 'ltn'), normal(0.0, 0.0, -1.0), 0.68),
        (('rbs', 'lbs', 'lts', 'rts'), normal(0.0, 0.0, 1.0), 0.82),
        (('rbn', 'rbs', 'rts', 'rtn'), normal(1.0, 0.0, 0.0), 0.76),
        (('lbs', 'lbn', 'ltn', 'lts'), normal(-1.0, 0.0, 0.0), 0.60),
    )
    for corner_keys, face_normal, shade in face_specs:
        shaded_color = tuple(max(0.0, min(1.0, channel * shade)) for channel in color)
        _append_preview_face(
            vertices,
            corners[corner_keys[0]],
            corners[corner_keys[1]],
            corners[corner_keys[2]],
            corners[corner_keys[3]],
            shaded_color,
            face_normal,
        )


def _append_preview_oriented_cylinder(vertices, center, radius, half_width, color_rgb, forward, right, up, segments=12):
    center_vec = np.array(center, dtype='f4')
    forward_vec = _normalize_preview_axis(forward, (1.0, 0.0, 0.0))
    right_vec = _normalize_preview_axis(right, (0.0, 0.0, 1.0))
    up_vec = _normalize_preview_axis(up, (0.0, 1.0, 0.0))
    radius = max(0.001, float(radius))
    half_width = max(0.001, float(half_width))
    segments = max(6, int(segments))
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    front_center = center_vec - forward_vec * half_width
    back_center = center_vec + forward_vec * half_width

    front_ring = []
    back_ring = []
    radial_normals = []
    for index in range(segments):
        angle = (math.tau * index) / segments
        radial = _normalize_preview_axis(up_vec * math.cos(angle) + right_vec * math.sin(angle), up_vec)
        radial_normals.append(radial)
        front_ring.append(_preview_vec3_tuple(front_center + radial * radius))
        back_ring.append(_preview_vec3_tuple(back_center + radial * radius))

    front_center_tuple = _preview_vec3_tuple(front_center)
    back_center_tuple = _preview_vec3_tuple(back_center)
    front_normal = _preview_vec3_tuple(-forward_vec)
    back_normal = _preview_vec3_tuple(forward_vec)
    for index in range(segments):
        next_index = (index + 1) % segments
        side_normal = _preview_vec3_tuple(_normalize_preview_axis(radial_normals[index] + radial_normals[next_index], up_vec))
        _append_preview_face(vertices, front_ring[index], front_ring[next_index], back_ring[next_index], back_ring[index], color, side_normal)
        _append_preview_triangle(vertices, front_center_tuple, front_ring[index], front_ring[next_index], tuple(max(0.0, channel * 0.84) for channel in color), front_normal)
        _append_preview_triangle(vertices, back_center_tuple, back_ring[next_index], back_ring[index], tuple(max(0.0, channel * 0.94) for channel in color), back_normal)


def _resolve_barrel_octagon_edges(profile, radius):
    radius = max(0.004, float(radius))
    long_edge = float(profile.get('barrel_octagon_long_edge_m', 0.0) or 0.0)
    short_edge = float(profile.get('barrel_octagon_short_edge_m', 0.0) or 0.0)
    if long_edge <= 1e-6:
        long_edge = radius * 1.80
    if short_edge <= 1e-6:
        short_edge = radius * 0.72
    return max(0.004, long_edge), max(0.002, short_edge)


def _barrel_octagon_section_points(long_edge, short_edge):
    diagonal = max(0.001, float(short_edge)) / math.sqrt(2.0)
    half_long = max(0.002, float(long_edge)) * 0.5
    half_extent = half_long + diagonal * 0.5
    return [
        (-half_long, half_extent),
        (half_long, half_extent),
        (half_long + diagonal, half_extent - diagonal),
        (half_long + diagonal, -half_extent + diagonal),
        (half_long, -half_extent),
        (-half_long, -half_extent),
        (-half_long - diagonal, -half_extent + diagonal),
        (-half_long - diagonal, half_extent - diagonal),
    ]


def _append_preview_octagonal_barrel(vertices, center, length, radius, color_rgb, profile):
    radius = max(0.004, float(radius))
    half_length = max(0.006, float(length) * 0.5)
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    long_edge, short_edge = _resolve_barrel_octagon_edges(profile, radius)
    section = _barrel_octagon_section_points(long_edge, short_edge)
    cx, cy, cz = center
    rear_ring = [(cx - half_length, cy + point_y, cz + point_z) for point_z, point_y in section]
    muzzle_ring = [(cx + half_length, cy + point_y, cz + point_z) for point_z, point_y in section]
    muzzle_normal = (1.0, 0.0, 0.0)
    rear_normal = (-1.0, 0.0, 0.0)
    for index in range(1, len(section) - 1):
        _append_preview_triangle(vertices, muzzle_ring[0], muzzle_ring[index], muzzle_ring[index + 1], tuple(min(1.0, channel * 0.94) for channel in color), muzzle_normal)
        _append_preview_triangle(vertices, rear_ring[0], rear_ring[index + 1], rear_ring[index], tuple(max(0.0, channel * 0.72) for channel in color), rear_normal)
    for index in range(len(section)):
        next_index = (index + 1) % len(section)
        shade = 0.74 if index in (0, 4) else (0.56 if index in (2, 6) else 0.64)
        side_normal = _preview_face_normal(rear_ring[index], rear_ring[next_index], muzzle_ring[next_index])
        _append_preview_face(
            vertices,
            rear_ring[index],
            rear_ring[next_index],
            muzzle_ring[next_index],
            muzzle_ring[index],
            tuple(max(0.0, min(1.0, channel * shade)) for channel in color),
            side_normal,
        )
    muzzle_center = (float(center[0]) + half_length + max(0.002, radius * 0.08), float(center[1]), float(center[2]))
    _append_preview_oriented_cylinder(
        vertices,
        muzzle_center,
        radius * 0.55,
        max(0.0025, radius * 0.10),
        [18, 20, 24],
        (1.0, 0.0, 0.0),
        (0.0, 0.0, 1.0),
        (0.0, 1.0, 0.0),
        segments=8,
    )


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


def _body_tilt_top_bounds(length_m, width_m, height_m, profile):
    half_length = max(0.001, float(length_m) * 0.5)
    half_width = max(0.001, float(width_m) * 0.5)
    height = max(0.001, float(height_m))

    def inset_from_tilt(key):
        tilt_deg = max(0.0, min(65.0, float(profile.get(key, 0.0))))
        return math.tan(math.radians(tilt_deg)) * height

    front_x = half_length - inset_from_tilt('body_front_tilt_deg')
    rear_x = -half_length + inset_from_tilt('body_rear_tilt_deg')
    right_z = half_width - inset_from_tilt('body_right_tilt_deg')
    left_z = -half_width + inset_from_tilt('body_left_tilt_deg')
    min_length = min(half_length * 1.6, max(0.02, half_length * 0.20))
    min_width = min(half_width * 1.6, max(0.02, half_width * 0.20))
    if front_x - rear_x < min_length:
        mid_x = (front_x + rear_x) * 0.5
        rear_x = mid_x - min_length * 0.5
        front_x = mid_x + min_length * 0.5
    if right_z - left_z < min_width:
        mid_z = (right_z + left_z) * 0.5
        left_z = mid_z - min_width * 0.5
        right_z = mid_z + min_width * 0.5
    return rear_x, front_x, left_z, right_z


def _append_preview_body_prism(vertices, center, length_m, width_m, height_m, color_rgb, profile):
    cx, cy, cz = center
    half_length = max(0.001, float(length_m) * 0.5)
    half_width = max(0.001, float(width_m) * 0.5)
    half_height = max(0.001, float(height_m) * 0.5)
    bottom_points = [
        (cx - half_length, cy - half_height, cz - half_width),
        (cx + half_length, cy - half_height, cz - half_width),
        (cx + half_length, cy - half_height, cz + half_width),
        (cx - half_length, cy - half_height, cz + half_width),
    ]
    rear_x, front_x, left_z, right_z = _body_tilt_top_bounds(length_m, width_m, height_m, profile)
    top_points = [
        (cx + rear_x, cy + half_height, cz + left_z),
        (cx + front_x, cy + half_height, cz + left_z),
        (cx + front_x, cy + half_height, cz + right_z),
        (cx + rear_x, cy + half_height, cz + right_z),
    ]
    _append_preview_prism(vertices, bottom_points, top_points, color_rgb)


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


def _append_preview_energy_pod(vertices, center, length, width, height, depth, color_rgb, yaw_rad=0.0):
    cx, cy, cz = center
    half_length = max(0.02, float(length) * 0.5)
    half_width = max(0.02, float(width) * 0.5)
    half_height = max(0.02, float(height) * 0.5)
    half_depth = max(0.01, float(depth) * 0.5)
    nose_x = half_length
    shoulder_x = half_length * 0.40
    tail_x = -half_length
    tail_inner_x = -half_length * 0.60
    top_cut = half_height * 0.34
    bottom_cut = half_height * 0.28

    def section(z_value):
        return [
            (cx + tail_inner_x, cy - half_height, cz + z_value),
            (cx + shoulder_x, cy - half_height, cz + z_value),
            (cx + nose_x, cy - bottom_cut, cz + z_value),
            (cx + nose_x, cy + bottom_cut, cz + z_value),
            (cx + shoulder_x, cy + half_height, cz + z_value),
            (cx + tail_inner_x, cy + half_height, cz + z_value),
            (cx + tail_x, cy + top_cut, cz + z_value),
            (cx + tail_x, cy - top_cut, cz + z_value),
        ]

    _append_preview_prism(vertices, section(-half_depth), section(half_depth), color_rgb, yaw_rad=yaw_rad)


def _append_preview_energy_arm(vertices, hub_center, yaw_rad, inner_radius, outer_radius, rail_gap, rail_width, rail_depth, color_rgb, accent_rgb=None, base_yaw_rad=0.0):
    hub_x, hub_y, hub_z = hub_center
    dir_x = math.cos(yaw_rad)
    dir_y = math.sin(yaw_rad)
    side_x = -dir_y
    side_y = dir_x
    rail_gap = max(0.012, float(rail_gap))
    rail_width = max(0.010, float(rail_width))
    rail_depth = max(0.008, float(rail_depth))
    inner_radius = max(0.02, float(inner_radius))
    outer_radius = max(inner_radius + 0.02, float(outer_radius))
    accent_rgb = accent_rgb or color_rgb

    shell_inner = inner_radius * 0.90
    shell_outer = max(shell_inner + 0.05, outer_radius - rail_width * 0.70)
    shell_mid = shell_inner + (shell_outer - shell_inner) * 0.58
    root_half = max(rail_gap * 0.78, rail_width * 0.75)
    waist_half = max(root_half * 1.18, rail_gap * 1.04)
    end_half = max(root_half * 0.82, rail_gap * 0.84)

    def shell_point(radius, half_offset, z_value):
        return (
            hub_x + dir_x * radius + side_x * half_offset,
            hub_y + dir_y * radius + side_y * half_offset,
            hub_z + z_value,
        )

    def shell_section(z_value):
        return [
            shell_point(shell_inner, root_half, z_value),
            shell_point(shell_mid, waist_half, z_value),
            shell_point(shell_outer, end_half, z_value),
            (hub_x + dir_x * (shell_outer + rail_width * 0.38), hub_y + dir_y * (shell_outer + rail_width * 0.38), hub_z + z_value),
            shell_point(shell_outer, -end_half, z_value),
            shell_point(shell_mid, -waist_half, z_value),
            shell_point(shell_inner, -root_half, z_value),
            (hub_x + dir_x * (shell_inner - rail_width * 0.35), hub_y + dir_y * (shell_inner - rail_width * 0.35), hub_z + z_value),
        ]

    _append_preview_prism(
        vertices,
        shell_section(-rail_depth * 0.52),
        shell_section(rail_depth * 0.52),
        [54, 58, 64],
        yaw_rad=base_yaw_rad,
    )

    root_center = (hub_x + dir_x * inner_radius, hub_y + dir_y * inner_radius, hub_z)
    end_center = (hub_x + dir_x * outer_radius, hub_y + dir_y * outer_radius, hub_z)
    root_a = (root_center[0] + side_x * rail_gap, root_center[1] + side_y * rail_gap, hub_z)
    root_b = (root_center[0] - side_x * rail_gap, root_center[1] - side_y * rail_gap, hub_z)
    end_a = (end_center[0] + side_x * rail_gap * 0.72, end_center[1] + side_y * rail_gap * 0.72, hub_z)
    end_b = (end_center[0] - side_x * rail_gap * 0.72, end_center[1] - side_y * rail_gap * 0.72, hub_z)
    shell_root_a = shell_point(shell_inner, root_half * 1.02, 0.0)
    shell_root_b = shell_point(shell_inner, -root_half * 1.02, 0.0)
    shell_end_a = shell_point(shell_outer, end_half * 1.04, 0.0)
    shell_end_b = shell_point(shell_outer, -end_half * 1.04, 0.0)
    _append_preview_beam(vertices, shell_root_a, shell_end_a, rail_width * 0.34, rail_depth * 1.10, accent_rgb, yaw_rad=base_yaw_rad)
    _append_preview_beam(vertices, shell_root_b, shell_end_b, rail_width * 0.34, rail_depth * 1.10, accent_rgb, yaw_rad=base_yaw_rad)
    _append_preview_beam(vertices, root_a, end_a, rail_width, rail_depth, color_rgb, yaw_rad=base_yaw_rad)
    _append_preview_beam(vertices, root_b, end_b, rail_width, rail_depth, color_rgb, yaw_rad=base_yaw_rad)
    _append_preview_beam(vertices, root_a, root_b, rail_width * 0.85, rail_depth, color_rgb, yaw_rad=base_yaw_rad)
    _append_preview_beam(vertices, end_a, end_b, rail_width * 1.10, rail_depth, color_rgb, yaw_rad=base_yaw_rad)


def _energy_platform_outline_points(length_m, width_m, y, corner_scale=0.28):
    half_l = max(0.12, float(length_m) * 0.5)
    half_w = max(0.12, float(width_m) * 0.5)
    cut_l = max(0.05, half_l * float(corner_scale))
    cut_w = max(0.05, half_w * float(corner_scale))
    return [
        (-half_l + cut_l, y, -half_w),
        (half_l - cut_l, y, -half_w),
        (half_l, y, -half_w + cut_w),
        (half_l, y, half_w - cut_w),
        (half_l - cut_l, y, half_w),
        (-half_l + cut_l, y, half_w),
        (-half_l, y, half_w - cut_w),
        (-half_l, y, -half_w + cut_w),
    ]


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


def _rotate_preview_vector(vector, axis, angle_rad):
    if abs(angle_rad) <= 1e-9:
        return vector
    axis_array = np.array(axis, dtype=float)
    axis_norm = np.linalg.norm(axis_array)
    if axis_norm <= 1e-9:
        return vector
    axis_array = axis_array / axis_norm
    vector_array = np.array(vector, dtype=float)
    cos_angle = math.cos(angle_rad)
    sin_angle = math.sin(angle_rad)
    rotated = (
        vector_array * cos_angle
        + np.cross(axis_array, vector_array) * sin_angle
        + axis_array * np.dot(axis_array, vector_array) * (1.0 - cos_angle)
    )
    return tuple(rotated.tolist())


def _preview_basis_from_ypr(yaw_rad=0.0, pitch_rad=0.0, roll_rad=0.0):
    forward = (0.0, 0.0, 1.0)
    right = (1.0, 0.0, 0.0)
    up = (0.0, 1.0, 0.0)

    def rotate_all(axis, angle_rad):
        nonlocal forward, right, up
        forward = _rotate_preview_vector(forward, axis, angle_rad)
        right = _rotate_preview_vector(right, axis, angle_rad)
        up = _rotate_preview_vector(up, axis, angle_rad)

    rotate_all(up, yaw_rad)
    rotate_all(right, pitch_rad)
    rotate_all(forward, roll_rad)
    return forward, right, up


def _append_preview_cylinder(vertices, center, radius, half_width, color_rgb, segments=12, yaw_rad=0.0, pitch_rad=0.0, roll_rad=0.0):
    cx, cy, cz = center
    color = tuple(float(channel) / 255.0 for channel in color_rgb)
    forward, right, up = _preview_basis_from_ypr(yaw_rad, pitch_rad, roll_rad)

    def rotate_point(point):
        point_x, point_y, point_z = point
        return (
            cx + right[0] * point_x + up[0] * point_y + forward[0] * point_z,
            cy + right[1] * point_x + up[1] * point_y + forward[1] * point_z,
            cz + right[2] * point_x + up[2] * point_y + forward[2] * point_z,
        )

    def rotate_normal(normal):
        normal_x, normal_y, normal_z = normal
        return (
            right[0] * normal_x + up[0] * normal_y + forward[0] * normal_z,
            right[1] * normal_x + up[1] * normal_y + forward[1] * normal_z,
            right[2] * normal_x + up[2] * normal_y + forward[2] * normal_z,
        )

    front_ring = []
    back_ring = []
    for index in range(segments):
        angle = (math.pi * 2.0 * index) / max(segments, 3)
        ring_x = math.cos(angle) * radius
        ring_y = math.sin(angle) * radius
        front_ring.append(rotate_point((ring_x, ring_y, -half_width)))
        back_ring.append(rotate_point((ring_x, ring_y, half_width)))
    front_center = rotate_point((0.0, 0.0, -half_width))
    back_center = rotate_point((0.0, 0.0, half_width))
    front_normal = rotate_normal((0.0, 0.0, -1.0))
    back_normal = rotate_normal((0.0, 0.0, 1.0))
    for index in range(segments):
        next_index = (index + 1) % segments
        normal_a = np.array(front_ring[index]) - np.array(front_center)
        normal_b = np.array(front_ring[next_index]) - np.array(front_center)
        average = normal_a + normal_b
        norm = np.linalg.norm(average)
        side_normal = tuple((average / norm).tolist()) if norm > 1e-6 else (1.0, 0.0, 0.0)
        _append_preview_face(vertices, front_ring[index], front_ring[next_index], back_ring[next_index], back_ring[index], color, side_normal)
        _append_preview_face(vertices, front_center, front_ring[next_index], front_ring[index], front_center, tuple(max(0.0, channel * 0.84) for channel in color), front_normal)
        _append_preview_face(vertices, back_center, back_ring[index], back_ring[next_index], back_center, tuple(max(0.0, channel * 0.94) for channel in color), back_normal)


def _append_preview_ringed_disk(vertices, center, radius, half_width, accent_rgb, ring_count=10, segments=20, yaw_rad=0.0):
    radius = max(0.02, float(radius))
    half_width = max(0.003, float(half_width))
    outer_gray = [86, 90, 96]
    inner_gray = [108, 112, 118]
    disk_segments = max(12, int(segments))
    _append_preview_cylinder(vertices, center, radius * 1.05, half_width * 0.55, accent_rgb, segments=disk_segments, yaw_rad=yaw_rad)
    for ring_index in range(max(2, int(ring_count))):
        t = ring_index / max(1, ring_count - 1)
        ring_radius = max(radius * 0.08, radius * (1.0 - t * 0.88))
        ring_half_width = max(0.0025, half_width * (0.90 - t * 0.42))
        ring_color = accent_rgb if ring_index == 0 else (outer_gray if ring_index % 2 == 0 else inner_gray)
        _append_preview_cylinder(vertices, center, ring_radius, ring_half_width, ring_color, segments=disk_segments, yaw_rad=yaw_rad)
    _append_preview_cylinder(vertices, center, max(radius * 0.08, radius * 0.11), half_width * 0.95, [58, 62, 68], segments=disk_segments, yaw_rad=yaw_rad)


def _rotate_xz(point_x, point_z, yaw_rad):
    cos_yaw = math.cos(yaw_rad)
    sin_yaw = math.sin(yaw_rad)
    return (
        point_x * cos_yaw - point_z * sin_yaw,
        point_x * sin_yaw + point_z * cos_yaw,
    )


def _alternating_hex_points(length_m, width_m, y, short_edge_m=None):
    half_l = max(0.00, float(length_m) * 0.5)
    half_w = max(0.00, float(width_m) * 0.5)
    short_edge = max(0.05, min(float(short_edge_m) if short_edge_m is not None else float(length_m) * 0.58, float(length_m) * 0.92))
    corner_x = short_edge * 0.5
    return [
        (-corner_x, y, -half_w),
        (corner_x, y, -half_w),
        (half_l, y, 0.0),
        (corner_x, y, half_w),
        (-corner_x, y, half_w),
        (-half_l, y, 0.0),
    ]


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


def _body_outline_support_distance(profile, outward_x, outward_z):
    points = _body_outline_points(profile)
    if not points:
        return 0.0
    normal_length = math.hypot(float(outward_x), float(outward_z))
    if normal_length <= 1e-6:
        return 0.0
    normal_x = float(outward_x) / normal_length
    normal_z = float(outward_z) / normal_length
    return max(point_x * normal_x + point_z * normal_z for point_x, point_z in points)


def _resolved_wheel_centers(profile):
    return [component['center'] for component in _resolved_wheel_components(profile)]


def _resolved_wheel_components(profile):
    render_width_scale = float(profile.get('body_render_width_scale', 0.82))
    self_values = list(profile.get('wheel_self_yaws_deg', []))
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
            center_z = _balance_leg_wheel_side_offset(profile, leg_geometry) * side_sign
            center_height_m = float(leg_geometry['foot'][1])
        else:
            center_x = float(position[0])
            center_z = float(position[1]) * render_width_scale
        spin_deg = float(self_values[index]) if index < len(self_values) else 0.0
        components.append({'center': (center_x, center_z), 'spin_rad': math.radians(spin_deg), 'center_height_m': center_height_m})
    return components


def _preview_cylinder_axes_from_runtime(axis_direction, radial_hint, spin_rad=0.0):
    axis = _normalize_preview_axis(axis_direction, (1.0, 0.0, 0.0))
    hint = np.array(radial_hint, dtype='f4')
    radial_a = hint - axis * float(np.dot(hint, axis))
    if float(np.linalg.norm(radial_a)) <= 1e-6:
        fallback = np.array((1.0, 0.0, 0.0), dtype='f4') if abs(float(np.dot(axis, (0.0, 1.0, 0.0)))) >= 0.92 else np.array((0.0, 1.0, 0.0), dtype='f4')
        radial_a = fallback - axis * float(np.dot(fallback, axis))
    radial_a = _normalize_preview_axis(radial_a, (0.0, 1.0, 0.0))
    radial_b = _normalize_preview_axis(np.cross(axis, radial_a), (0.0, 0.0, 1.0))
    if abs(float(spin_rad)) > 1e-6:
        spun_a = radial_a * math.cos(float(spin_rad)) + radial_b * math.sin(float(spin_rad))
        radial_a = _normalize_preview_axis(spun_a, radial_a)
        radial_b = _normalize_preview_axis(np.cross(axis, radial_a), radial_b)
    return (
        _preview_vec3_tuple(axis),
        _preview_vec3_tuple(radial_b),
        _preview_vec3_tuple(radial_a),
    )


def _preview_wheel_cylinder_axes(profile, wheel_component, spin_rad=None):
    wheel_x, wheel_z = wheel_component['center']
    axis = np.array((0.0, 0.0, 1.0), dtype='f4')
    radial_hint = np.array((1.0, 0.0, 0.0), dtype='f4')
    if str(profile.get('wheel_style', 'standard')).lower() == 'omni':
        inward = np.array((-float(wheel_x), 0.0, -float(wheel_z)), dtype='f4')
        if float(np.linalg.norm(inward)) > 1e-6:
            axis = inward
        radial_hint = np.array((0.0, 1.0, 0.0), dtype='f4')
    resolved_spin = float(wheel_component.get('spin_rad', 0.0) if spin_rad is None else spin_rad)
    return _preview_cylinder_axes_from_runtime(axis, radial_hint, resolved_spin)


def _resolve_armor_plate_thickness(profile):
    explicit = float(profile.get('armor_plate_thickness_m', -1.0))
    if explicit >= 0.0:
        return max(0.001, explicit)
    armor_gap = float(profile.get('armor_plate_gap_m', 0.005))
    return max(0.004, armor_gap * 0.75, float(profile.get('armor_plate_width_m', 0.16)) * 0.08)


def _resolved_armor_components(profile):
    armor_gap = float(profile.get('armor_plate_gap_m', 0.005))
    armor_thickness = _resolve_armor_plate_thickness(profile)
    armor_center_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.5
    orbit_values = list(profile.get('armor_orbit_yaws_deg', [0.0, 180.0, 90.0, 270.0]))
    self_values = list(profile.get('armor_self_yaws_deg', orbit_values))
    components = []
    for index in range(4):
        orbit_deg = float(orbit_values[index]) if index < len(orbit_values) else 0.0
        orbit_rad = math.radians(orbit_deg)
        outward_x = math.cos(orbit_rad)
        outward_z = math.sin(orbit_rad)
        support_distance = _body_outline_support_distance(profile, outward_x, outward_z)
        plate_distance = support_distance + armor_gap + armor_thickness * 0.5
        center = (outward_x * plate_distance, armor_center_y, outward_z * plate_distance)
        default_yaw = orbit_rad
        self_deg = float(self_values[index]) if index < len(self_values) else orbit_deg
        yaw_rad = math.radians(self_deg) if profile.get('armor_self_yaws_deg') else default_yaw
        offsets = profile.setdefault('armor_plate_offsets_m', [])
        while len(offsets) <= index:
            offsets.append([0.0, 0.0, 0.0])
        offset = _normalize_vector3(offsets[index], (0.0, 0.0, 0.0))
        offsets[index] = offset
        rotations = profile.setdefault('armor_plate_rotations_ypr_deg', [])
        while len(rotations) <= index:
            rotations.append([0.0, 0.0, 0.0])
        rotation = _normalize_vector3(rotations[index], (0.0, 0.0, 0.0))
        rotations[index] = rotation
        center = (center[0] + offset[0], center[1] + offset[1], center[2] + offset[2])
        components.append({
            'center': center,
            'yaw_rad': yaw_rad + math.radians(float(rotation[0])),
            'pitch_rad': math.radians(float(rotation[1])),
            'roll_rad': math.radians(float(rotation[2])),
        })
    return components


def _friction_wheel_count(profile):
    wheel_radius = max(0.0, float(profile.get('barrel_friction_wheel_radius_m', 0.0)))
    wheel_height = max(0.0, float(profile.get('barrel_friction_wheel_height_m', profile.get('barrel_friction_wheel_width_m', 0.0))))
    if wheel_radius <= 0.001 or wheel_height <= 0.001:
        return 0
    return 6 if str(profile.get('role_key', '')).lower() == 'hero' else 2


def _friction_wheel_offset(profile, index):
    offsets = profile.setdefault('barrel_friction_wheel_offsets_m', [])
    while len(offsets) <= index:
        offsets.append([0.0, 0.0, 0.0])
    offset = _normalize_vector3(offsets[index], (0.0, 0.0, 0.0))
    offsets[index] = offset
    return offset


def _friction_wheel_layout(profile, barrel_base_x, barrel_base_y, barrel_base_z, barrel_radius):
    role_key = str(profile.get('role_key', '')).lower()
    wheel_radius = max(0.0, float(profile.get('barrel_friction_wheel_radius_m', 0.0)))
    wheel_height = max(0.0, float(profile.get('barrel_friction_wheel_height_m', profile.get('barrel_friction_wheel_width_m', 0.0))))
    if wheel_radius <= 0.001 or wheel_height <= 0.001:
        return []
    offset_x = float(profile.get('barrel_friction_wheel_offset_x_m', 0.0))
    offset_y = float(profile.get('barrel_friction_wheel_offset_y_m', 0.0))
    side_offset = max(float(profile.get('barrel_friction_wheel_offset_z_m', 0.0)), float(barrel_radius) + wheel_height * 0.85)
    base_x = float(barrel_base_x) + offset_x
    base_y = float(barrel_base_y) + offset_y
    base_z = float(barrel_base_z)
    rotation_ypr = [
        float(profile.get('barrel_friction_wheel_yaw_deg', 0.0)),
        float(profile.get('barrel_friction_wheel_pitch_deg', 0.0)),
        float(profile.get('barrel_friction_wheel_roll_deg', 0.0)),
    ]
    axis_vec = np.array([1.0, 0.0, 0.0], dtype='f4')
    right_vec = np.array([0.0, 0.0, 1.0], dtype='f4')
    up_vec = np.array([0.0, 1.0, 0.0], dtype='f4')

    def wheel_offset_vector(index):
        offset = _friction_wheel_offset(profile, index)
        return axis_vec * float(offset[0]) + up_vec * float(offset[1]) + right_vec * float(offset[2])

    def wheel_orientation(forward_axis, radial_hint):
        wheel_forward = _normalize_preview_axis(forward_axis, (1.0, 0.0, 0.0))
        wheel_right = _normalize_preview_axis(radial_hint, (0.0, 0.0, 1.0))
        wheel_up = _normalize_preview_axis(np.cross(wheel_forward, wheel_right), (0.0, 1.0, 0.0))
        return {
            'kind': 'oriented_box',
            'forward': tuple(float(value) for value in wheel_forward),
            'right': tuple(float(value) for value in wheel_right),
            'up': tuple(float(value) for value in wheel_up),
        }

    def wheel_yaw_from_axis(forward_axis):
        forward_axis = _normalize_preview_axis(forward_axis, (1.0, 0.0, 0.0))
        return math.atan2(float(forward_axis[2]), float(forward_axis[0]))

    wheels = []
    if role_key == 'hero':
        ring_radius = max(side_offset, float(barrel_radius) + wheel_radius * 1.05)
        group_gap = max(wheel_height * 1.35, wheel_radius * 0.55)
        for group_index, group_shift in enumerate((-group_gap * 0.5, group_gap * 0.5)):
            group_center = np.array([base_x + group_shift, base_y, base_z], dtype='f4')
            for slot_index, angle_deg in enumerate((90.0, 210.0, 330.0)):
                angle = math.radians(angle_deg)
                index = group_index * 3 + slot_index
                radial = _normalize_preview_axis(up_vec * math.sin(angle) + right_vec * math.cos(angle), (0.0, 1.0, 0.0))
                tangent = _normalize_preview_axis(up_vec * math.cos(angle) - right_vec * math.sin(angle), (0.0, 0.0, 1.0))
                wheel_axis, wheel_radial, _ = _resolve_preview_rotated_basis(tangent, axis_vec, radial, rotation_ypr)
                center_vec = group_center + radial * ring_radius + wheel_offset_vector(index)
                orientation = wheel_orientation(wheel_axis, wheel_radial)
                center = tuple(float(value) for value in center_vec)
                wheels.append((center, (wheel_height * 0.5, wheel_radius, wheel_radius), (wheel_yaw_from_axis(wheel_axis), 0.0, 0.0), index, orientation))
    else:
        pair_axis, pair_radial, _ = _resolve_preview_rotated_basis(right_vec, axis_vec, up_vec, rotation_ypr)
        orientation = wheel_orientation(pair_axis, pair_radial)
        base_center = np.array([base_x, base_y, base_z], dtype='f4')
        left_center = base_center - right_vec * side_offset + wheel_offset_vector(0)
        right_center = base_center + right_vec * side_offset + wheel_offset_vector(1)
        pair_yaw = wheel_yaw_from_axis(pair_axis)
        wheels.append((tuple(float(value) for value in left_center), (wheel_height * 0.5, wheel_radius, wheel_radius), (pair_yaw, 0.0, 0.0), 0, orientation))
        wheels.append((tuple(float(value) for value in right_center), (wheel_height * 0.5, wheel_radius, wheel_radius), (pair_yaw, 0.0, 0.0), 1, orientation))
    return wheels


def _resolved_armor_light_components(profile):
    armor_components = _resolved_armor_components(profile)
    armor_half_width = float(profile.get('armor_plate_length_m', 0.16)) * 0.5
    light_half_width = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
    default_distance = max(0.004, float(profile.get('armor_plate_gap_m', 0.005)) * 0.15)
    light_offsets = profile.setdefault('armor_light_offsets_m', [])
    light_distances = profile.setdefault('armor_light_plate_distances_m', [])
    light_components = []
    for component in armor_components:
        light_index_a = len(light_components) * 2
        light_index_b = light_index_a + 1
        while len(light_offsets) <= light_index_b:
            light_offsets.append([0.0, 0.0, 0.0])
        while len(light_distances) <= light_index_b:
            light_distances.append(default_distance)
        offset_a = _normalize_vector3(light_offsets[light_index_a], (0.0, 0.0, 0.0))
        offset_b = _normalize_vector3(light_offsets[light_index_b], (0.0, 0.0, 0.0))
        light_offsets[light_index_a] = offset_a
        light_offsets[light_index_b] = offset_b
        distance_a = max(0.0, float(light_distances[light_index_a]))
        distance_b = max(0.0, float(light_distances[light_index_b]))
        light_distances[light_index_a] = distance_a
        light_distances[light_index_b] = distance_b
        forward, right, up = _resolve_preview_rotated_axes(
            float(component['yaw_rad']),
            [0.0, math.degrees(float(component.get('pitch_rad', 0.0))), math.degrees(float(component.get('roll_rad', 0.0)))],
        )
        center = np.array(component['center'], dtype='f4')
        forward_vec = np.array(forward, dtype='f4')
        right_vec = np.array(right, dtype='f4')
        up_vec = np.array(up, dtype='f4')
        center_a = center + right_vec * (armor_half_width + light_half_width + distance_a) + forward_vec * float(offset_a[0]) + up_vec * float(offset_a[1]) + right_vec * float(offset_a[2])
        center_b = center - right_vec * (armor_half_width + light_half_width + distance_b) + forward_vec * float(offset_b[0]) + up_vec * float(offset_b[1]) + right_vec * float(offset_b[2])
        light_components.append({
            'center_a': tuple(float(value) for value in center_a),
            'center_b': tuple(float(value) for value in center_b),
            'yaw_rad': float(component['yaw_rad']),
            'pitch_rad': float(component.get('pitch_rad', 0.0)),
            'roll_rad': float(component.get('roll_rad', 0.0)),
            'orientation': {
                'kind': 'oriented_box',
                'forward': tuple(float(value) for value in forward),
                'right': tuple(float(value) for value in right),
                'up': tuple(float(value) for value in up),
            },
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

    def _append_structure_outpost_geometry(self, vertices, profile):
        body_color = profile['body_color_rgb']
        turret_color = profile['turret_color_rgb']
        armor_color = profile['armor_color_rgb']
        dark_color = profile.get('wheel_color_rgb', [62, 68, 78])
        armor_spin = float(profile.get('_preview_outpost_armor_yaw_rad', 0.0))
        lift = float(profile.get('structure_base_lift_m', 0.40))
        tower_height = max(0.8, float(profile.get('body_height_m', 1.578)))
        base_width = max(0.30, float(profile.get('body_length_m', 0.65)))
        top_diameter = max(0.24, float(profile.get('body_width_m', 0.55)))
        tower_radius = max(0.12, float(profile.get('structure_tower_radius_m', max(0.18, top_diameter * 0.36))))
        lower_height = max(0.05, float(profile.get('structure_lower_shoulder_height_m', tower_height * (0.571 / 1.578))))
        body_top_height = max(lower_height + 0.04, float(profile.get('structure_body_top_height_m', tower_height * (1.216 / 1.578))))
        upper_height = max(body_top_height + 0.04, float(profile.get('structure_upper_shoulder_height_m', tower_height * (1.446 / 1.578))))
        head_base_height = max(upper_height + 0.03, float(profile.get('structure_head_base_height_m', tower_height * (1.318 / 1.578))))

        def polygon(radius, height, sides=8, yaw=0.0):
            return [
                (math.cos(yaw + index * math.tau / sides) * radius, lift + height, math.sin(yaw + index * math.tau / sides) * radius)
                for index in range(sides)
            ]

        def tapered(bottom_radius, top_radius, bottom_h, top_h, color, yaw=0.0):
            _append_preview_prism(vertices, polygon(bottom_radius, bottom_h, yaw=yaw), polygon(top_radius, top_h, yaw=yaw), color)

        _append_preview_box(vertices, (0.0, lift + 0.042, 0.0), (base_width * 0.50, 0.042, base_width * 0.50), body_color, yaw_rad=math.pi * 0.25)
        tapered(base_width * 0.46, 0.255, 0.005, lower_height, body_color)
        tapered(0.205, 0.175, lower_height, body_top_height, dark_color, yaw=math.pi / 8.0)
        tapered(0.220, 0.165, body_top_height, upper_height, turret_color)
        tapered(0.165, 0.120, upper_height, head_base_height, [min(255, int(c * 1.06)) for c in turret_color], yaw=math.pi / 8.0)
        tapered(tower_radius + 0.055, tower_radius + 0.055, head_base_height - 0.005, head_base_height - 0.055, [min(255, int(c * 1.10)) for c in body_color])

        _append_preview_box(vertices, (0.0, lift + head_base_height + 0.05, 0.0), (0.00, 0.05, 0.07), dark_color)
        _append_preview_box(vertices, (0.03, lift + tower_height - 0.05, 0.0), (0.105, 0.06, 0.09), [min(255, int(c * 1.04)) for c in turret_color])
        for side_sign in (-1.0, 1.0):
            _append_preview_box(vertices, (0.0, lift + head_base_height + 0.03, side_sign * 0.25), (0.05, 0.11, 0.008), dark_color, yaw_rad=0.0)
        _append_preview_box(vertices, (0.02, lift + tower_height - 0.025, 0.0), (0.028, 0.02, 0.028), [96, 255, 130])

        armor_side = max(0.04, float(profile.get('armor_plate_width_m', 0.13)))
        armor_half = armor_side * 0.5
        armor_thickness = _resolve_armor_plate_thickness(profile)
        radius = tower_radius + 0.055
        for index, yaw in enumerate([armor_spin + 0.0, armor_spin + math.tau / 3.0, armor_spin + math.tau * 2.0 / 3.0]):
            height = lift + head_base_height - 0.07 + [0.05, 0.0, -0.05][index]
            center = (math.cos(yaw) * radius, height, math.sin(yaw) * radius)
            _append_preview_box(vertices, center, (armor_thickness * 0.5, armor_half, armor_half), armor_color, yaw_rad=yaw)
        outpost_top_height = float(profile.get('structure_top_armor_center_height_m', lift + tower_height + 0.055))
        outpost_top_offset_x = float(profile.get('structure_top_armor_offset_x_m', 0.0))
        outpost_top_offset_z = float(profile.get('structure_top_armor_offset_z_m', radius))
        outpost_top_tilt_deg = float(profile.get('structure_top_armor_tilt_deg', 45.0))
        top_x = math.cos(armor_spin) * outpost_top_offset_z - math.sin(armor_spin) * outpost_top_offset_x
        top_z = math.sin(armor_spin) * outpost_top_offset_z + math.cos(armor_spin) * outpost_top_offset_x
        _append_preview_box(vertices, (top_x, outpost_top_height, top_z), (armor_half, armor_half, armor_thickness * 0.5), armor_color, yaw_rad=armor_spin + math.radians(outpost_top_tilt_deg))

    def _append_structure_base_geometry(self, vertices, profile):
        body_color = profile['body_color_rgb']
        armor_color = profile['armor_color_rgb']
        dark_color = profile.get('wheel_color_rgb', [62, 68, 78])
        open_ratio = max(0.0, min(1.0, float(profile.get('_preview_base_open_ratio', 0.0))))
        length = max(0.8, float(profile.get('body_length_m', 1.881)))
        width = max(0.7, float(profile.get('body_width_m', 1.609))) * max(0.4, float(profile.get('body_render_width_scale', 1.0)))
        height = max(0.5, float(profile.get('body_height_m', 1.181)))
        top_edge = max(0.12, min(length * 0.92, float(profile.get('structure_hex_top_edge_m', 1.089))))
        roof_height = max(0.3, float(profile.get('structure_roof_height_m', 1.03)))
        slab_h = min(0.20, height * 0.22)
        shoulder_h = min(max(slab_h + 0.12, float(profile.get('structure_shoulder_height_m', height * (0.860 / 1.181)))), roof_height - 0.05)

        _append_preview_prism(vertices, _alternating_hex_points(length, width, 0.0, short_edge_m=top_edge), _alternating_hex_points(length * 0.96, width * 0.94, slab_h, short_edge_m=top_edge * 0.98), dark_color)
        _append_preview_prism(vertices, _alternating_hex_points(length * 0.90, width * 0.88, slab_h, short_edge_m=top_edge * 0.90), _alternating_hex_points(length * 0.62, width * 0.56, shoulder_h, short_edge_m=top_edge * 0.62), body_color)
        _append_preview_prism(vertices, _alternating_hex_points(length * 0.62, width * 0.56, shoulder_h, short_edge_m=top_edge * 0.62), _alternating_hex_points(length * 0.40, width * 0.34, roof_height, short_edge_m=top_edge * 0.40), [min(255, int(c * 1.08)) for c in body_color])
        _append_preview_box(vertices, (0.0, height * 0.58, 0.0), (0.055, min(height * 0.33, float(profile.get('structure_core_column_height_m', 0.783)) * 0.5), 0.06), [188, 52, 46])

        armor_side = max(0.04, float(profile.get('armor_plate_width_m', 0.13)))
        armor_half = armor_side * 0.5
        armor_thickness = _resolve_armor_plate_thickness(profile)
        base_top_height = float(profile.get('structure_top_armor_center_height_m', height * (1.150 / 1.181)))
        base_top_offset_x = float(profile.get('structure_top_armor_offset_x_m', 0.0))
        base_top_offset_z = float(profile.get('structure_top_armor_offset_z_m', 0.0))
        _append_preview_box(vertices, (length * 0.04 + base_top_offset_x, base_top_height, base_top_offset_z), (armor_half, armor_thickness * 0.5, armor_half), armor_color)
        _append_preview_box(vertices, (length * 0.15, height * 0.70, 0.0), (armor_thickness * 0.5, armor_half, armor_half), armor_color)
        side_open_angle = math.radians(float(profile.get('structure_side_armor_open_angle_deg', 27.5)))
        side_outward_offset = float(profile.get('structure_side_armor_outward_offset_m', 0.12))
        for side in (-1.0, 1.0):
            side_shift = open_ratio * (width * 0.14 + side_outward_offset)
            side_raise = open_ratio * 0.06
            side_yaw = side * open_ratio * side_open_angle
            _append_preview_box(vertices, (-length * 0.07, height * 0.44 + side_raise, side * (width * 0.43 + side_shift)), (length * 0.18, height * 0.24, 0.035), armor_color, yaw_rad=side_yaw)
            _append_preview_box(vertices, (-length * 0.06, height * 0.62 + side_raise * 0.6, side * (width * 0.31 + side_shift * 0.72)), (length * 0.13, height * 0.15, 0.010), [255, 40, 40], yaw_rad=side_yaw)
        _append_preview_box(vertices, (0.02, float(profile.get('structure_detector_bridge_center_height_m', height * (1.093 / 1.181))), 0.0), (0.04, 0.022, min(float(profile.get('structure_detector_width_m', 0.98)) * 0.5, width * 0.30)), dark_color)
        _append_preview_box(vertices, (0.0, float(profile.get('structure_detector_sensor_center_height_m', height * (1.136 / 1.181))), 0.0), (0.03, max(0.030, float(profile.get('structure_detector_height_m', 0.095)) * 0.50), 0.03), [96, 255, 130])

    def _append_structure_energy_mechanism_geometry(self, vertices, profile):
        body_color = profile['body_color_rgb']
        frame_color = profile['turret_color_rgb']
        assembly_color = profile['armor_color_rgb']
        rotor_yaw = float(profile.get('_preview_energy_rotor_yaw_rad', 0.0))
        mechanism_yaw = -math.pi * 0.25

        base_height = max(0.00, float(profile.get('structure_base_height_m', 0.30)))
        ground_clearance = max(0.0, float(profile.get('structure_ground_clearance_m', 0.0)))
        frame_width = max(0.80, float(profile.get('structure_frame_width_m', 2.06)))
        frame_depth = max(0.06, float(profile.get('structure_frame_depth_m', 0.16)))
        frame_height = max(base_height + 0.60, float(profile.get('structure_frame_height_m', 2.30)))
        support_offset = max(0.10, float(profile.get('structure_support_offset_m', frame_width * 0.5)))
        column_w = max(0.04, float(profile.get('structure_frame_column_width_m', 0.10)))
        beam_h = max(0.04, float(profile.get('structure_frame_beam_height_m', 0.09)))
        rotor_center_h = max(base_height + ground_clearance + 0.40, float(profile.get('structure_rotor_center_height_m', 1.45)))
        rotor_phase_rad = math.radians(float(profile.get('structure_rotor_phase_deg', 90.0)))
        rotor_radius = max(0.18, float(profile.get('structure_rotor_radius_m', 1.40)))
        hub_radius = max(0.05, float(profile.get('structure_rotor_hub_radius_m', 0.09)))
        arm_width = max(0.04, float(profile.get('structure_rotor_arm_width_m', 0.06)))
        arm_height = max(0.03, float(profile.get('structure_rotor_arm_height_m', 0.04)))
        lamp_length = max(0.06, float(profile.get('structure_lamp_length_m', 0.30)))
        lamp_width = max(0.05, float(profile.get('structure_lamp_width_m', 0.30)))
        lamp_height = max(0.03, float(profile.get('structure_lamp_height_m', 0.00)))
        hanger_w = max(0.20, float(profile.get('structure_hanger_width_m', 0.24)))
        hanger_h = max(0.12, float(profile.get('structure_hanger_height_m', 0.24)))
        hanger_d = max(0.04, float(profile.get('structure_hanger_depth_m', 0.06)))
        hanger_center_h = max(base_height + 0.20, float(profile.get('structure_hanger_center_height_m', 1.45)))
        lower_module_w = max(0.04, float(profile.get('structure_lower_module_width_m', 0.20)))
        lower_module_h = max(0.04, float(profile.get('structure_lower_module_height_m', 0.24)))
        lower_module_d = max(0.04, float(profile.get('structure_lower_module_depth_m', 0.18)))
        lower_module_offset = max(0.05, float(profile.get('structure_lower_module_offset_x_m', 0.48)))
        lower_module_center_h = max(base_height + lower_module_h * 0.5, float(profile.get('structure_lower_module_center_height_m', 0.94)))
        cantilever_length = max(0.00, float(profile.get('structure_cantilever_length_m', 0.28)))
        cantilever_pair_gap = max(frame_width + cantilever_length, float(profile.get('structure_cantilever_pair_gap_m', frame_width + cantilever_length)))
        cantilever_offset_y = float(profile.get('structure_cantilever_offset_y_m', 0.0))
        cantilever_height = max(0.04, float(profile.get('structure_cantilever_height_m', 0.00)))
        cantilever_depth = max(0.04, float(profile.get('structure_cantilever_depth_m', 0.00)))
        rail_gap = max(0.026, arm_width * 0.68)
        arm_inner_radius = max(hub_radius * 1.35, 0.12)
        arm_length = max(0.10, float(profile.get('structure_rotor_arm_length_m', 1.12)))
        arm_outer_radius = max(arm_inner_radius + 0.04, min(arm_inner_radius + arm_length, rotor_radius - lamp_length * 0.15))
        lamp_disk_radius = max(lamp_length, lamp_width) * 0.50
        lamp_center_radius = rotor_radius + max(lamp_disk_radius * 0.42, max(arm_width * 1.10, rail_gap * 0.95))
        pod_depth = max(lamp_width * 0.18, frame_depth * 0.55)
        top_beam_y = ground_clearance + frame_height - beam_h * 0.5
        base_length = max(0.40, float(profile.get('structure_base_length_m', max(frame_width * 1.65, float(profile.get('body_length_m', 2.06)) * 1.72))))
        base_width = max(0.40, float(profile.get('structure_base_width_m', max(frame_depth * 6.0, float(profile.get('body_width_m', 1.30)) * 2.45))))
        post_height = max(1.90, frame_height - base_height)
        top_beam_width = max(support_offset * 2.0, frame_width)
        connector_center_y = hanger_center_h
        rotor_center_y = rotor_center_h + cantilever_offset_y
        rotor_axis_gap = max(
            frame_depth * 1.8,
            hub_radius * 2.6,
            min(cantilever_pair_gap, frame_width) * 0.42 + cantilever_length * 0.30,
        )
        rotor_centers = [
            (0.0, rotor_center_y, -rotor_axis_gap * 0.5),
            (0.0, rotor_center_y, rotor_axis_gap * 0.5),
        ]
        rotor_colors = ([228, 76, 76], [58, 112, 232])
        base_pad_length = max(0.20, float(profile.get('structure_base_top_length_m', base_length * 0.34)))
        base_pad_width = max(0.16, float(profile.get('structure_base_top_width_m', base_width * 0.24)))
        stem_height = max(0.12, top_beam_y - connector_center_y - beam_h * 0.5)
        hanger_block_w = max(0.08, hanger_w * 0.32)
        hanger_block_h = max(0.08, hanger_h * 0.22)
        hanger_block_d = max(0.06, hanger_d)
        assembly_rod_span = max(0.05, lower_module_offset)

        def rotate_xz(x, z):
            cos_y = math.cos(mechanism_yaw)
            sin_y = math.sin(mechanism_yaw)
            return (x * cos_y - z * sin_y, x * sin_y + z * cos_y)

        def p(x, y, z):
            rx, rz = rotate_xz(x, z)
            return (rx, y, rz)

        column_x = support_offset
        for side in (-1.0, 1.0):
            _append_preview_box(
                vertices,
                p(side * column_x, ground_clearance + base_height * 0.5, 0.0),
                (base_pad_length * 0.5, base_height * 0.5, base_pad_width * 0.5),
                body_color,
                yaw_rad=mechanism_yaw,
            )
        _append_preview_box(vertices, p(-column_x, ground_clearance + base_height + post_height * 0.5, 0.0), (column_w * 0.5, post_height * 0.5, frame_depth * 0.5), frame_color, yaw_rad=mechanism_yaw)
        _append_preview_box(vertices, p(column_x, ground_clearance + base_height + post_height * 0.5, 0.0), (column_w * 0.5, post_height * 0.5, frame_depth * 0.5), frame_color, yaw_rad=mechanism_yaw)
        _append_preview_box(vertices, p(0.0, top_beam_y, 0.0), (top_beam_width * 0.5, beam_h * 0.5, column_w * 0.5), frame_color, yaw_rad=mechanism_yaw)
        _append_preview_box(vertices, p(0.0, connector_center_y + stem_height * 0.5, 0.0), (column_w * 0.30, stem_height * 0.5, hanger_block_d * 0.35), frame_color, yaw_rad=mechanism_yaw)
        _append_preview_box(vertices, p(0.0, connector_center_y, 0.0), (hanger_block_w * 0.5, hanger_block_h * 0.5, hanger_block_d * 0.5), frame_color, yaw_rad=mechanism_yaw)

        for rotor_index, rotor_center in enumerate(rotor_centers):
            rotor_color = rotor_colors[rotor_index]
            cx, cy, cz = rotor_center
            _append_preview_beam(vertices, (0.0, connector_center_y, 0.0), (cx, cy, cz), cantilever_height * 0.72, cantilever_depth, frame_color, yaw_rad=mechanism_yaw)
            _append_preview_energy_pod(
                vertices,
                (cx, cy, cz),
                hub_radius * 3.1,
                hub_radius * 2.8,
                hub_radius * 2.8,
                max(frame_depth * 0.58, 0.045),
                [74, 78, 84],
                yaw_rad=mechanism_yaw + math.pi * 0.25,
            )
            _append_preview_ringed_disk(vertices, (cx, cy, cz), hub_radius * 0.98, max(frame_depth * 0.12, 0.012), rotor_color, ring_count=6, segments=24, yaw_rad=mechanism_yaw)
            for index in range(5):
                yaw = rotor_yaw + rotor_phase_rad + math.radians(72.0 * index)
                _append_preview_energy_arm(vertices, (cx, cy, cz), yaw, arm_inner_radius, arm_outer_radius, rail_gap, arm_width, arm_height, frame_color, accent_rgb=rotor_color, base_yaw_rad=mechanism_yaw)
                lamp_x = cx + math.cos(yaw) * lamp_center_radius
                lamp_y = cy + math.sin(yaw) * lamp_center_radius
                _append_preview_ringed_disk(vertices, (lamp_x, lamp_y, cz), lamp_disk_radius, max(0.005, pod_depth * 0.18), rotor_color, ring_count=10, segments=14, yaw_rad=mechanism_yaw)

        rod_top_y = connector_center_y - hanger_block_h * 0.65
        rod_bottom_y = lower_module_center_h + lower_module_h * 0.42
        module_side_offset = max(lower_module_w * 0.72, lower_module_offset)
        for side, accent in ((-1.0, [58, 112, 232]), (1.0, [228, 76, 76])):
            module_center_x = side * module_side_offset
            rod_x = side * assembly_rod_span
            _append_preview_beam(vertices, (rod_x, rod_top_y, 0.0), (module_center_x, rod_bottom_y, 0.0), 0.022, 0.020, frame_color, yaw_rad=mechanism_yaw)
            _append_preview_energy_pod(vertices, (module_center_x, lower_module_center_h, 0.0), lower_module_w, lower_module_h, lower_module_h, lower_module_d, assembly_color, yaw_rad=mechanism_yaw + (0.0 if side < 0.0 else math.pi))
            _append_preview_box(vertices, p(module_center_x, lower_module_center_h, 0.0), (lower_module_w * 0.10, lower_module_h * 0.28, lower_module_d * 0.10), accent, yaw_rad=mechanism_yaw)

    def _structure_bounds_radius(self, profile):
        role_key = str(profile.get('role_key', '')).lower()
        if role_key == 'energy_mechanism':
            return max(
                2.1,
                float(profile.get('body_length_m', 1.55)) * 1.45,
                float(profile.get('body_width_m', 0.98)) * 1.65,
                float(profile.get('structure_frame_width_m', 1.55)) * 1.20,
                float(profile.get('body_height_m', 2.30)) * 0.85,
                float(profile.get('structure_rotor_radius_m', 0.46)) + 0.75,
            )
        return max(
            1.2,
            float(profile.get('body_length_m', 1.0)) * 1.25,
            float(profile.get('body_width_m', 1.0)) * 1.25,
            float(profile.get('body_height_m', 1.0)) + float(profile.get('structure_base_lift_m', 0.0)) + 0.4,
        )

    def _build_structure_geometry(self, profile):
        vertices = []
        role_key = str(profile.get('role_key', '')).lower()
        if role_key == 'outpost':
            self._append_structure_outpost_geometry(vertices, profile)
        elif role_key == 'base':
            self._append_structure_base_geometry(vertices, profile)
        else:
            self._append_structure_energy_mechanism_geometry(vertices, profile)

        vertex_array = np.array(vertices, dtype='f4')
        self.bounds_radius = self._structure_bounds_radius(profile)
        if self.vao is not None:
            self.vao.release()
        if self.vbo is not None:
            self.vbo.release()
        self.vbo = self.ctx.buffer(vertex_array.tobytes())
        self.vao = self.ctx.vertex_array(self.program, [(self.vbo, '3f 3f 3f', 'in_position', 'in_color', 'in_normal')])

    def _preview_attachment_part_poses(self, profile):
        poses = []
        render_width_scale = float(profile.get('body_render_width_scale', 0.82))
        has_front_climb = str(profile.get('front_climb_assist_style', 'none')) != 'none'
        has_rear_climb = str(profile.get('rear_climb_assist_style', 'none')) != 'none'
        has_mount = (float(profile.get('gimbal_mount_height_m', 0.0)) + float(profile.get('gimbal_mount_gap_m', 0.0))) > 1e-6
        has_turret = (
            str(profile.get('role_key', '')).lower() == 'energy_mechanism'
            or (float(profile.get('gimbal_length_m', 0.0)) > 1e-6 and float(profile.get('gimbal_body_height_m', 0.0)) > 1e-6)
        )
        has_barrel = has_turret and float(profile.get('barrel_length_m', 0.0)) > 1e-6 and float(profile.get('barrel_radius_m', 0.0)) > 1e-6
        body_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.5
        poses.append({'part': 'body', 'index': 0, 'center': (0.0, body_y, 0.0), 'yaw_rad': 0.0})

        for index, component in enumerate(_resolved_armor_components(profile)):
            poses.append({'part': 'armor', 'index': index, 'center': tuple(component['center']), 'yaw_rad': float(component['yaw_rad']), 'pitch_rad': float(component.get('pitch_rad', 0.0)), 'roll_rad': float(component.get('roll_rad', 0.0))})
        for index, component in enumerate(_resolved_armor_light_components(profile)):
            poses.append({'part': 'armor_light', 'index': index * 2, 'center': tuple(component['center_a']), 'yaw_rad': float(component['yaw_rad']), 'pitch_rad': float(component.get('pitch_rad', 0.0)), 'roll_rad': float(component.get('roll_rad', 0.0))})
            poses.append({'part': 'armor_light', 'index': index * 2 + 1, 'center': tuple(component['center_b']), 'yaw_rad': float(component['yaw_rad']), 'pitch_rad': float(component.get('pitch_rad', 0.0)), 'roll_rad': float(component.get('roll_rad', 0.0))})

        rear_health_depth = float(profile.get('rear_health_light_width_m', 0.0))
        if rear_health_depth <= 1e-6:
            rear_health_depth = min(max(float(profile['body_length_m']) * 0.045, 0.018), 0.038)
        rear_health_height = float(profile.get('rear_health_light_height_m', 0.0))
        if rear_health_height <= 1e-6:
            rear_health_height = min(max(float(profile['body_width_m']) * 0.035, 0.010), 0.018)
        rear_health_center = (
            -float(profile['body_length_m']) * 0.5 - rear_health_depth * 0.24 + float(profile.get('rear_health_light_offset_x_m', 0.0)),
            float(profile['body_clearance_m']) + float(profile['body_height_m']) + rear_health_height * 0.58 + float(profile.get('rear_health_light_offset_y_m', 0.0)),
            float(profile.get('rear_health_light_offset_z_m', 0.0)),
        )
        poses.append({'part': 'rear_health_light', 'index': 0, 'center': rear_health_center, 'yaw_rad': 0.0})

        for index, wheel_component in enumerate(_resolved_wheel_components(profile)):
            wheel_x, wheel_z = wheel_component['center']
            poses.append({'part': 'wheel', 'index': index, 'center': (float(wheel_x), float(wheel_component.get('center_height_m', profile.get('wheel_radius_m', 0.08))), float(wheel_z)), 'yaw_rad': 0.0})

        if has_front_climb:
            body_half_x = float(profile['body_length_m']) * 0.5
            body_half_z = float(profile['body_width_m']) * 0.5 * render_width_scale
            wheel_outer_z = max((abs(float(wheel_y)) * render_width_scale for _, wheel_y in profile.get('custom_wheel_positions_m', [])), default=body_half_z + float(profile['wheel_radius_m']) * 0.55)
            _plate_top_length, plate_bottom_length = _front_climb_lengths(profile)
            plate_height = float(profile.get('front_climb_assist_plate_height_m', 0.18))
            plate_forward = float(profile.get('front_climb_assist_forward_offset_m', 0.04))
            plate_inner = float(profile.get('front_climb_assist_inner_offset_m', 0.06)) * render_width_scale
            plate_center_x = body_half_x + plate_forward + plate_bottom_length * 0.5
            plate_center_y = float(profile.get('wheel_radius_m', 0.08)) + plate_height * 0.5
            plate_center_z = max(body_half_z * 0.45, wheel_outer_z - plate_inner)
            poses.append({'part': 'front_climb', 'index': 0, 'center': (plate_center_x, plate_center_y, -plate_center_z), 'yaw_rad': 0.0})
            poses.append({'part': 'front_climb', 'index': 1, 'center': (plate_center_x, plate_center_y, plate_center_z), 'yaw_rad': 0.0})

        if has_rear_climb:
            if str(profile.get('rear_climb_assist_style', 'none')) == 'balance_leg':
                _extend_balance_leg_preview_attachment_poses(poses, profile, render_width_scale)
            else:
                rear_points = _rear_climb_points(profile, render_width_scale)
                upper_center = ((rear_points['mount'][0] + rear_points['joint'][0]) * 0.5, (rear_points['mount'][1] + rear_points['joint'][1]) * 0.5)
                poses.append({'part': 'rear_climb', 'index': 0, 'center': (upper_center[0], upper_center[1], -float(rear_points['side_offset'])), 'yaw_rad': 0.0})
                poses.append({'part': 'rear_climb', 'index': 1, 'center': (upper_center[0], upper_center[1], float(rear_points['side_offset'])), 'yaw_rad': 0.0})

        mount_offset_x = _profile_mount_offset_x(profile)
        mount_offset_z = _profile_mount_offset_z(profile)
        turret_offset_x = _profile_turret_offset_x(profile)
        turret_offset_z = _profile_turret_offset_z(profile)
        turret_center_y = _profile_turret_center_height(profile)
        if has_mount:
            poses.append({'part': 'mount', 'index': 0, 'center': (mount_offset_x, _profile_mount_center_height(profile), mount_offset_z), 'yaw_rad': 0.0})
        if has_turret:
            turret_center = (turret_offset_x, turret_center_y, turret_offset_z)
            poses.append({'part': 'turret', 'index': 0, 'center': turret_center, 'yaw_rad': 0.0})
            if has_barrel:
                barrel_base_x = turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + float(profile.get('barrel_offset_x_m', 0.0))
                barrel_base_y = turret_center_y + float(profile.get('barrel_offset_y_m', 0.0))
                barrel_base_z = turret_offset_z + float(profile.get('barrel_offset_z_m', 0.0))
                barrel_center = (
                    barrel_base_x + float(profile['barrel_length_m']) * 0.5,
                    barrel_base_y,
                    barrel_base_z,
                )
                poses.append({'part': 'barrel', 'index': 0, 'center': barrel_center, 'yaw_rad': 0.0})
                for center, _half_extents, wheel_ypr, wheel_index, _orientation in _friction_wheel_layout(profile, barrel_base_x, barrel_base_y, barrel_base_z, float(profile.get('barrel_radius_m', 0.0))):
                    poses.append({'part': 'barrel_friction_wheel', 'index': wheel_index, 'center': center, 'yaw_rad': wheel_ypr[0]})
                poses.append({
                    'part': 'first_person_camera',
                    'index': 0,
                    'center': (
                        barrel_base_x + float(profile.get('first_person_camera_offset_x_m', 0.04)),
                        barrel_base_y + float(profile.get('first_person_camera_offset_y_m', 0.06)),
                        barrel_base_z + float(profile.get('first_person_camera_offset_z_m', 0.0)),
                    ),
                    'yaw_rad': math.radians(float(profile.get('first_person_camera_yaw_deg', 0.0))),
                })
                barrel_light_center_x = barrel_base_x + float(profile['barrel_length_m']) * 0.45 + float(profile.get('barrel_light_offset_x_m', 0.0))
                barrel_light_center_y = barrel_base_y + float(profile.get('barrel_light_offset_y_m', 0.0))
                barrel_light_center_z = barrel_base_z + float(profile.get('barrel_light_offset_z_m', 0.0))
                barrel_light_offset = max(0.005, float(profile.get('barrel_light_width_m', 0.02)) * 3.0)
                poses.append({'part': 'barrel_light', 'index': 0, 'center': (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z + barrel_light_offset), 'yaw_rad': 0.0})
                poses.append({'part': 'barrel_light', 'index': 1, 'center': (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z - barrel_light_offset), 'yaw_rad': 0.0})

            if str(profile.get('role_key', '')).lower() == 'hero':
                turret_center_x = turret_offset_x
                turret_center_z = turret_offset_z
                turret_width = float(profile['gimbal_width_m']) * render_width_scale
                turret_height = float(profile['gimbal_body_height_m'])
                camera_center = (
                    turret_center_x + HERO_SUBVIEW_CAMERA_BODY_LENGTH_M * 0.5 - 0.004,
                    turret_center_y + max(0.010, turret_height * 0.18) + HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M * 0.707 + HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M * 0.5 - 0.002,
                    turret_center_z - max(0.018, turret_width * 0.46) - 0.006,
                )
                poses.append({'part': 'hero_subview_camera', 'index': 0, 'center': camera_center, 'yaw_rad': 0.0})
        return poses

    def _append_custom_preview_geometry(self, vertices, profile):
        poses = self._preview_attachment_part_poses(profile)

        def matching_poses(parent_part, component_scope, component_index):
            for pose in poses:
                if pose['part'] != parent_part:
                    continue
                if component_scope == 'all' or int(pose['index']) == int(component_index):
                    yield pose

        for primitive in profile.get('custom_primitives', []):
            parent_part = str(primitive.get('parent_part', 'body'))
            component_scope = str(primitive.get('component_scope', 'single'))
            component_index = int(primitive.get('component_index', 0))
            size_m = primitive.get('size_m', [0.06, 0.04, 0.04])
            offset_m = primitive.get('offset_m', [0.0, 0.0, 0.0])
            rotation_ypr_deg = primitive.get('rotation_ypr_deg', [0.0, 0.0, 0.0])
            color_rgb = primitive.get('color_rgb', [188, 192, 198])
            for pose in matching_poses(parent_part, component_scope, component_index):
                base_forward, base_right, base_up = _resolve_preview_rotated_axes(float(pose['yaw_rad']), [0.0, math.degrees(float(pose.get('pitch_rad', 0.0))), math.degrees(float(pose.get('roll_rad', 0.0)))])
                forward, right, up = _resolve_preview_rotated_basis(base_forward, base_right, base_up, rotation_ypr_deg)
                center = _preview_local_point(pose['center'], base_forward, base_right, base_up, offset_m)
                primitive_type = str(primitive.get('primitive_type', 'box'))
                sx, sy, sz = [max(0.002, float(value)) for value in size_m[:3]]
                if primitive_type == 'cylinder':
                    _append_preview_oriented_cylinder(vertices, center, max(sy, sz) * 0.5, sx * 0.5, color_rgb, forward, right, up, segments=12)
                else:
                    _append_preview_oriented_box(vertices, center, (sx * 0.5, sy * 0.5, sz * 0.5), color_rgb, forward, right, up)

        anchor_variants = _resolve_preview_custom_anchor_point_variants(profile, poses)
        for anchor in profile.get('custom_anchors', []):
            is_active = _is_active_anchor(anchor)
            for resolved_anchor in anchor_variants.get(str(anchor.get('id', '')), []):
                _append_preview_oriented_box(
                    vertices,
                    resolved_anchor['point'],
                    (0.011, 0.011, 0.011) if is_active else (0.008, 0.008, 0.008),
                    [96, 210, 255] if is_active else [232, 196, 92],
                    resolved_anchor['forward'],
                    resolved_anchor['right'],
                    resolved_anchor['up'])

        for link in profile.get('custom_links', []):
            for start_anchor, end_anchor in _pair_preview_anchor_variants(anchor_variants, link.get('start_anchor_id', ''), link.get('end_anchor_id', '')):
                start_point = start_anchor['point']
                end_point = end_anchor['point']
                radius = max(0.001, float(link.get('radius_m', 0.012)))
                width = max(0.001, float(link.get('width_m', radius * 2.0)))
                thickness = max(0.001, float(link.get('thickness_m', radius * 2.0)))
                fixed_length = max(0.0, float(link.get('length_m', 0.0) or 0.0))
                resolved_end = _resolve_fixed_link_end(start_point, end_point, fixed_length)
                _append_preview_beam(vertices, start_point, resolved_end, width, thickness, link.get('color_rgb', [176, 182, 190]), yaw_rad=0.0)

    def _build_geometry(self, profile):
        if str(profile.get('role_key', '')).lower() in {'outpost', 'base', 'energy_mechanism'}:
            self._build_structure_geometry(profile)
            return

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
            _append_preview_body_prism(
                vertices,
                (0.0, body_y, 0.0),
                float(profile['body_length_m']),
                float(profile['body_width_m']) * render_width_scale,
                float(profile['body_height_m']),
                profile['body_color_rgb'],
                profile,
            )
            _append_preview_box(
                vertices,
                (0.0, body_y + float(profile['body_height_m']) * 0.36, 0.0),
                (float(profile['body_length_m']) * 0.40, max(0.015, float(profile['body_height_m']) * 0.12), float(profile['body_width_m']) * 0.40 * render_width_scale),
                [max(0, min(255, int(channel * 0.82 + 20))) for channel in profile['body_color_rgb']],
            )

        wheel_radius = max(0.018, float(profile['wheel_radius_m']))
        wheel_half_z = 0.020
        for wheel_component in _resolved_wheel_components(profile):
            wheel_x, wheel_z = wheel_component['center']
            wheel_center_y = float(wheel_component.get('center_height_m', wheel_radius))
            wheel_axis, wheel_right, wheel_up = _preview_wheel_cylinder_axes(profile, wheel_component)
            _append_preview_oriented_cylinder(
                vertices,
                (float(wheel_x), wheel_center_y, float(wheel_z)),
                wheel_radius,
                wheel_half_z,
                profile['wheel_color_rgb'],
                wheel_axis,
                wheel_right,
                wheel_up,
                segments=14,
            )
            hub_axis, hub_right, hub_up = _preview_cylinder_axes_from_runtime(wheel_axis, (0.0, 1.0, 0.0), 0.0)
            hub_color = [max(0, min(255, int(channel * 0.84 + 38))) for channel in profile['wheel_color_rgb']]
            _append_preview_oriented_cylinder(
                vertices,
                (float(wheel_x), wheel_center_y, float(wheel_z)),
                max(0.007, wheel_radius * 0.24),
                max(0.008, wheel_half_z * 1.10),
                hub_color,
                hub_axis,
                hub_right,
                hub_up,
                segments=10,
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
        armor_thickness = _resolve_armor_plate_thickness(profile)
        armor_half_width = float(profile['armor_plate_length_m']) * 0.5
        for component in _resolved_armor_components(profile):
            armor_forward, armor_right, armor_up = _resolve_preview_rotated_axes(
                float(component['yaw_rad']),
                [0.0, math.degrees(float(component.get('pitch_rad', 0.0))), math.degrees(float(component.get('roll_rad', 0.0)))],
            )
            _append_preview_oriented_box(
                vertices,
                component['center'],
                (armor_thickness * 0.5, armor_half_h, armor_half_width),
                armor_color,
                armor_forward,
                armor_right,
                armor_up,
            )
        armor_light_color = [110, 168, 255]
        armor_light_half_x = float(profile.get('armor_light_length_m', 0.10)) * 0.5
        armor_light_half_y = max(0.005, float(profile.get('armor_light_height_m', 0.02)) * 0.5)
        armor_light_half_z = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
        for component in _resolved_armor_light_components(profile):
            orientation = component['orientation']
            _append_preview_oriented_box(vertices, component['center_a'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), armor_light_color, orientation['forward'], orientation['right'], orientation['up'])
            _append_preview_oriented_box(vertices, component['center_b'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), armor_light_color, orientation['forward'], orientation['right'], orientation['up'])

        rear_health_length = float(profile.get('rear_health_light_length_m', 0.0))
        if rear_health_length <= 1e-6:
            rear_health_length = max(0.08, float(profile['body_width_m']) * render_width_scale * 0.74)
        rear_health_width = float(profile.get('rear_health_light_width_m', 0.0))
        if rear_health_width <= 1e-6:
            rear_health_width = min(max(float(profile['body_length_m']) * 0.045, 0.018), 0.038)
        rear_health_height = float(profile.get('rear_health_light_height_m', 0.0))
        if rear_health_height <= 1e-6:
            rear_health_height = min(max(float(profile['body_width_m']) * 0.035, 0.010), 0.018)
        rear_health_center = (
            -float(profile['body_length_m']) * 0.5 - rear_health_width * 0.24 + float(profile.get('rear_health_light_offset_x_m', 0.0)),
            float(profile['body_clearance_m']) + float(profile['body_height_m']) + rear_health_height * 0.58 + float(profile.get('rear_health_light_offset_y_m', 0.0)),
            float(profile.get('rear_health_light_offset_z_m', 0.0)),
        )
        _append_preview_box(
            vertices,
            rear_health_center,
            (rear_health_width * 0.5, rear_health_height * 0.5, rear_health_length * 0.5),
            [255, 36, 40],
        )

        if has_turret:
            mount_offset_x = _profile_mount_offset_x(profile)
            mount_offset_z = _profile_mount_offset_z(profile)
            turret_offset_x = _profile_turret_offset_x(profile)
            turret_offset_z = _profile_turret_offset_z(profile)
            mount_center_y = _profile_mount_center_height(profile)
            turret_center_y = _profile_turret_center_height(profile)
            if (float(profile.get('gimbal_mount_height_m', 0.0)) + float(profile.get('gimbal_mount_gap_m', 0.0))) > 1e-6:
                connector_half_height = max(0.02, (float(profile.get('gimbal_mount_gap_m', 0.0)) + float(profile.get('gimbal_mount_height_m', 0.0))) * 0.5)
                _append_preview_box(
                    vertices,
                    (mount_offset_x, mount_center_y, mount_offset_z),
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
                barrel_base_x = turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + float(profile.get('barrel_offset_x_m', 0.0))
                barrel_base_y = turret_center_y + float(profile.get('barrel_offset_y_m', 0.0))
                barrel_base_z = turret_offset_z + float(profile.get('barrel_offset_z_m', 0.0))
                _append_preview_octagonal_barrel(
                    vertices,
                    (barrel_base_x + barrel_length * 0.5, barrel_base_y, barrel_base_z),
                    barrel_length,
                    barrel_radius,
                    profile['turret_color_rgb'],
                    profile,
                )
                for center, half_extents, _wheel_ypr, _wheel_index, orientation in _friction_wheel_layout(profile, barrel_base_x, barrel_base_y, barrel_base_z, barrel_radius):
                    _append_preview_oriented_cylinder(vertices, center, half_extents[1], half_extents[0], profile['turret_color_rgb'], orientation['forward'], orientation['right'], orientation['up'], segments=14)
                _append_preview_box(
                    vertices,
                    (
                        barrel_base_x + float(profile.get('first_person_camera_offset_x_m', 0.04)),
                        barrel_base_y + float(profile.get('first_person_camera_offset_y_m', 0.06)),
                        barrel_base_z + float(profile.get('first_person_camera_offset_z_m', 0.0)),
                    ),
                    (0.012, 0.012, 0.012),
                    [80, 210, 220],
                    yaw_rad=math.radians(float(profile.get('first_person_camera_yaw_deg', 0.0))),
                )
                barrel_light_half_x = float(profile.get('barrel_light_length_m', 0.10)) * 0.5
                barrel_light_half_y = max(0.005, float(profile.get('barrel_light_height_m', 0.02)) * 0.5)
                barrel_light_half_z = max(0.005, float(profile.get('barrel_light_width_m', 0.02)) * 0.5)
                barrel_light_center_x = barrel_base_x + barrel_length * 0.45 + float(profile.get('barrel_light_offset_x_m', 0.0))
                barrel_light_center_y = barrel_base_y + float(profile.get('barrel_light_offset_y_m', 0.0))
                barrel_light_center_z = barrel_base_z + float(profile.get('barrel_light_offset_z_m', 0.0))
                _append_preview_box(vertices, (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z + barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z), armor_light_color)
                _append_preview_box(vertices, (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z - barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z), armor_light_color)

            if str(profile.get('role_key', '')).lower() == 'hero':
                turret_width = float(profile['gimbal_width_m']) * render_width_scale
                turret_height = float(profile['gimbal_body_height_m'])
                camera_center = (
                    turret_offset_x + HERO_SUBVIEW_CAMERA_BODY_LENGTH_M * 0.5 - 0.004,
                    turret_center_y + max(0.010, turret_height * 0.18) + HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M * 0.707 + HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M * 0.5 - 0.002,
                    turret_offset_z - max(0.018, turret_width * 0.46) - 0.006,
                )
                _append_preview_box(
                    vertices,
                    camera_center,
                    (HERO_SUBVIEW_CAMERA_BODY_LENGTH_M * 0.5, HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M * 0.5, HERO_SUBVIEW_CAMERA_BODY_WIDTH_M * 0.5),
                    [156, 162, 174],
                )
                connector_bottom = (turret_offset_x - 0.006, turret_center_y + max(0.010, turret_height * 0.18), turret_offset_z - max(0.018, turret_width * 0.46))
                connector_top = (turret_offset_x + HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M * 0.24, turret_center_y + max(0.010, turret_height * 0.18) + HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M * 0.35, turret_offset_z - max(0.018, turret_width * 0.46) - 0.006)
                for side_sign in (-1.0, 1.0):
                    side_offset = HERO_SUBVIEW_CAMERA_BODY_WIDTH_M * 0.34 * side_sign
                    _append_preview_beam(
                        vertices,
                        (connector_bottom[0], connector_bottom[1], connector_bottom[2] + side_offset),
                        (connector_top[0], connector_top[1], connector_top[2] + side_offset),
                        0.015,
                        0.015,
                        [128, 134, 146],
                    )

        if str(profile.get('arm_style', 'none')) == 'fixed_7':
            _append_preview_box(vertices, (0.0, body_y + float(profile['body_height_m']) * 0.95, 0.0), (0.03, 0.22, 0.03), [172, 176, 184])
            _append_preview_box(vertices, (float(profile['body_length_m']) * 0.16, body_y + float(profile['body_height_m']) + 0.18, 0.0), (0.18, 0.03, 0.03), [188, 192, 198])

        rear_bar_length = max(0.018, float(profile['body_length_m']) * 0.045)
        rear_bar_height = max(0.010, float(profile['body_width_m']) * 0.035)
        _append_preview_box(
            vertices,
            (-float(profile['body_length_m']) * 0.5 - rear_bar_length * 0.24, float(profile['body_clearance_m']) + float(profile['body_height_m']) + rear_bar_height * 0.58, 0.0),
            (rear_bar_length * 0.5, rear_bar_height * 0.5, max(0.08, float(profile['body_width_m']) * render_width_scale * 0.37)),
            [36, 40, 48],
        )

        self._append_custom_preview_geometry(vertices, profile)

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

    def render_scene(self, profile, size, yaw=0.72, pitch=0.42, zoom=1.0):
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
        zoom = max(0.45, min(3.00, float(zoom)))
        distance = max(0.55, self.bounds_radius * 2.9 / zoom)
        eye = np.array([
            math.sin(yaw) * math.cos(pitch) * distance,
            math.sin(pitch) * distance + self.bounds_radius * 0.25,
            math.cos(yaw) * math.cos(pitch) * distance,
        ], dtype='f4') + target
        projection = _terrain_scene_perspective_matrix(math.radians(42.0), width / max(height, 1), 0.05, max(8.0, distance * 6.0))
        view = _terrain_scene_look_at(eye, target, np.array([0.0, 1.0, 0.0], dtype='f4'))
        mvp = projection @ view

        framebuffer.use()
        framebuffer.clear(0.91, 0.925, 0.945, 1.0)
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
        self.current_role = 'hero'
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
        self.preview_zoom = 1.0
        self._apply_role_preview_defaults(self.current_role)
        self.field_scroll = 0
        self.field_scroll_drag_active = False
        self.preview_drag_active = False
        self.preview_action_drag_active = False
        self.preview_mode_tabs = []
        self.preview_action_tabs = []
        self.infantry_subtype_tabs = []
        self.preview_part_hitboxes = []
        self._preview_surface_cache = {}
        self._preview_overlay_cache = {}
        self.component_control_actions = []
        self.custom_collection_actions = []
        self.color_palette_actions = []
        self.preview_action_slider_track_rect = None
        self.preview_action_slider_thumb_rect = None
        self.field_scrollbar_thumb_rect = None
        self.field_scrollbar_track_rect = None
        self.field_panel_rect = None
        self.preview_panel_rect = None
        self.preview_content_rect = None
        self.active_numeric_input = None
        self.last_field_click_ticks = 0
        self.last_field_click_index = -1
        self.runtime_preview_buttons = []
        self.runtime_preview_process = None
        self.runtime_preview_temp_path = None

        pygame.init()
        pygame.key.set_repeat(240, 40)
        pygame.display.set_caption('车辆外观编辑器')
        self.window_width = 1460
        self.window_height = 900
        self.screen = pygame.display.set_mode((self.window_width, self.window_height), pygame.RESIZABLE)
        self.clock = pygame.time.Clock()
        self.title_font = pygame.font.SysFont('microsoftyaheiui', 28)
        self.font = pygame.font.SysFont('microsoftyaheiui', 20)
        self.small_font = pygame.font.SysFont('microsoftyaheiui', 16)
        self.tiny_font = pygame.font.SysFont('microsoftyaheiui', 13)
        self.colors = {
            'bg': (226, 230, 235),
            'panel': (240, 243, 247),
            'panel_alt': (232, 236, 241),
            'panel_soft': (238, 241, 245),
            'field_row': (228, 232, 238),
            'field_row_active': (242, 245, 249),
            'panel_border': (172, 181, 193),
            'text': (28, 34, 42),
            'muted': (132, 141, 154),
            'value': (154, 162, 174),
            'accent': (255, 166, 72),
            'accent_dim': (255, 226, 188),
            'success': (88, 176, 118),
            'danger': (196, 92, 92),
            'preview_bg': (232, 236, 241),
            'grid': (196, 202, 212),
        }
        self.field_specs = self._build_field_specs()
        self.preview_renderer_3d = ModernGLAppearancePreview()

    def _resolve_preset_path(self):
        configured_path = str(self.config.get('entities', {}).get('appearance_preset_path', os.path.join('appearance_presets', 'latest_appearance.json')))
        if os.path.isabs(configured_path):
            return configured_path
        return os.path.join(os.path.dirname(os.path.abspath(self.config_path)), configured_path)

    def _profiles_dir(self):
        return os.path.join(os.path.dirname(self.preset_path), 'profiles')

    @staticmethod
    def _read_json_file(path, fallback=None):
        try:
            with open(path, 'r', encoding='utf-8-sig') as file:
                return json.load(file)
        except Exception:
            return fallback

    @staticmethod
    def _write_json_file(path, payload):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8-sig') as file:
            json.dump(payload, file, ensure_ascii=False, indent=2)

    def _profile_file_path(self, role_key):
        safe_name = ''.join(ch if ch.isalnum() or ch in {'_', '-'} else '_' for ch in str(role_key).strip().lower())
        return os.path.join(self._profiles_dir(), f'{safe_name}.json')

    def _migrate_latest_profiles_to_files(self):
        payload = self._read_json_file(self.preset_path, fallback={})
        stored_profiles = payload.get('profiles', {}) if isinstance(payload, dict) else {}
        if not isinstance(stored_profiles, dict):
            return {}

        os.makedirs(self._profiles_dir(), exist_ok=True)
        latest_mtime = os.path.getmtime(self.preset_path) if os.path.exists(self.preset_path) else 0.0
        migrated = {}
        for role_key, profile in stored_profiles.items():
            if not isinstance(profile, dict):
                continue
            profile_path = self._profile_file_path(role_key)
            if not os.path.exists(profile_path) or os.path.getmtime(profile_path) <= latest_mtime + 1e-6:
                self._write_json_file(profile_path, profile)
            migrated[role_key] = deepcopy(profile)
        return migrated

    def _load_profiles(self):
        profiles = {role_key: _default_profile(role_key) for role_key, _ in ROLE_ORDER}
        latest_profiles = self._migrate_latest_profiles_to_files() if os.path.exists(self.preset_path) else {}
        for role_key in profiles:
            profile_path = self._profile_file_path(role_key)
            override = self._read_json_file(profile_path, fallback=None)
            if not isinstance(override, dict):
                override = latest_profiles.get(role_key, {})
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
            self._write_json_file(self._profile_file_path(role_key), payload_profiles[role_key])
        self._write_json_file(self.preset_path, {'profiles': payload_profiles})
        self.status_text = f'已保存到 {self._profiles_dir()} 并同步 {self.preset_path}'

    def _build_profiles_payload(self):
        payload_profiles = {}
        for role_key in list(self.profiles.keys()):
            if role_key == 'infantry':
                store = self._ensure_infantry_profile_store()
                payload_profiles[role_key] = build_infantry_profile_payload(store.get('subtype_profiles', {}), self.current_infantry_subtype)
            else:
                payload_profiles[role_key] = _normalize_profile_constraints(role_key, self.profiles[role_key])
        return {'profiles': payload_profiles}

    def _runtime_preview_role(self):
        if self.current_role in {'base', 'outpost', 'energy_mechanism'}:
            return self.current_role
        return 'outpost'

    def _apply_role_preview_defaults(self, role_key):
        if role_key == 'energy_mechanism':
            self.preview_3d_yaw = -0.90
            self.preview_3d_pitch = 0.28
        elif role_key in {'base', 'outpost'}:
            self.preview_3d_yaw = 0.56
            self.preview_3d_pitch = 0.34
        else:
            self.preview_3d_yaw = 0.72
            self.preview_3d_pitch = 0.42
        self.preview_zoom = 1.0

    def _adjust_preview_zoom(self, wheel_delta):
        scale = 1.12 ** float(wheel_delta)
        self.preview_zoom = max(0.45, min(3.00, float(self.preview_zoom) * scale))

    def _runtime_preview_team(self):
        if self._runtime_preview_role() == 'energy_mechanism':
            return None
        simulator_config = self.config.get('simulator', {}) if isinstance(self.config, dict) else {}
        team = str(simulator_config.get('sim3d_selected_team', 'blue')).strip().lower()
        return team if team in {'red', 'blue'} else 'blue'

    def _runtime_preview_temp_file(self):
        if self.runtime_preview_temp_path:
            return self.runtime_preview_temp_path
        workspace_root = WORKSPACE_ROOT
        preview_dir = os.path.join(workspace_root, 'appearance_presets', '__runtime_preview__')
        os.makedirs(preview_dir, exist_ok=True)
        self.runtime_preview_temp_path = os.path.join(preview_dir, 'latest_appearance.runtime_preview.json')
        return self.runtime_preview_temp_path

    def _write_runtime_preview_file(self):
        preview_path = self._runtime_preview_temp_file()
        with open(preview_path, 'w', encoding='utf-8') as file:
            json.dump(self._build_profiles_payload(), file, ensure_ascii=False, indent=2)
        return preview_path

    def _runtime_preview_button_rects(self):
        preview_width = 116
        refresh_width = 116
        top = 22
        gap = 10
        refresh_rect = pygame.Rect(self.window_width - 28 - refresh_width, top, refresh_width, 34)
        preview_rect = pygame.Rect(refresh_rect.x - gap - preview_width, top, preview_width, 34)
        return (
            ('launch', '局内预览', preview_rect),
            ('refresh', '刷新预览', refresh_rect),
        )

    def _cleanup_runtime_preview_process(self):
        process = self.runtime_preview_process
        if process is None:
            return
        try:
            if process.poll() is None:
                process.terminate()
        except Exception:
            pass
        self.runtime_preview_process = None

    def _launch_runtime_preview(self, refresh=False):
        role_key = self._runtime_preview_role()
        preview_path = self._write_runtime_preview_file()
        if refresh:
            self._cleanup_runtime_preview_process()
        elif self.runtime_preview_process is not None and self.runtime_preview_process.poll() is None:
            self.status_text = 'C# 局内预览已在运行，可以直接点击刷新预览'
            return

        workspace_root = WORKSPACE_ROOT
        project_path = os.path.join(workspace_root, 'src', 'Simulator.Linux', 'Simulator.Linux.csproj')
        command = [
            'dotnet',
            'run',
            '--project',
            project_path,
            '--',
            '--map',
            'rmuc2026',
        ]
        creationflags = 0
        if os.name == 'nt':
            creationflags = getattr(subprocess, 'CREATE_NEW_PROCESS_GROUP', 0)
        try:
            self.runtime_preview_process = subprocess.Popen(command, cwd=workspace_root, creationflags=creationflags)
            self.status_text = f'C# 局内预览已启动：{role_key}'
        except Exception as exc:
            self.runtime_preview_process = None
            self.status_text = f'C# 局内预览启动失败：{exc}'

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
        if part == 'barrel_friction_wheel':
            return _friction_wheel_count(profile)
        if part == 'armor':
            return 4
        if part == 'armor_light':
            return 8
        if part == 'rear_health_light':
            return 1
        if part == 'custom_primitive':
            return len(profile.get('custom_primitives', []))
        if part == 'custom_anchor':
            return len(profile.get('custom_anchors', []))
        if part == 'custom_link':
            return len(profile.get('custom_links', []))
        return 0

    def _part_supports_component_selection(self, part):
        if part in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            return True
        if getattr(self, 'current_role', None) in {'outpost', 'base', 'energy_mechanism'}:
            return False
        return part in {'wheel', 'armor', 'armor_light', 'rear_health_light', 'barrel_friction_wheel'}

    def _custom_collection_key(self, part):
        mapping = {
            'custom_primitive': 'custom_primitives',
            'custom_anchor': 'custom_anchors',
            'custom_link': 'custom_links',
        }
        return mapping.get(part)

    def _current_custom_collection(self, part=None):
        target_part = part or self.selected_part
        key = self._custom_collection_key(target_part)
        if key is None:
            return []
        profile = self._current_profile()
        return profile.setdefault(key, [])

    def _ensure_custom_item_for_editing(self, part=None):
        target_part = part or self.selected_part
        if target_part not in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            return True
        self.selected_part = target_part
        profile = self._current_profile()
        key = self._custom_collection_key(target_part)
        if key is None:
            return True
        if target_part == 'custom_link':
            self._ensure_minimum_custom_anchors(2, profile=profile)
        collection = profile.setdefault(key, [])
        if collection:
            self._clamp_selected_component_index(profile)
            return True
        collection.append(self._custom_item_default(target_part, profile=profile))
        self.selected_component_index = 0
        self.selected_component_scope = 'single'
        _normalize_custom_collections(profile)
        self._persist_current_profile(profile)
        return True

    def _ensure_minimum_custom_anchors(self, count, profile=None):
        should_persist = profile is None
        profile = profile or self._current_profile()
        anchors = profile.setdefault('custom_anchors', [])
        while len(anchors) < count:
            anchors.append(_normalize_custom_anchor({
                'parent_part': 'body',
                'offset_m': [0.0, 0.04 * len(anchors), 0.0],
            }, len(anchors)))
        if should_persist:
            _normalize_custom_collections(profile)
            self._persist_current_profile(profile)

    def _custom_choice_options(self, spec, profile=None):
        if spec.get('choice_key') == 'parent_part':
            return CUSTOM_PARENT_PART_OPTIONS
        if spec.get('choice_key') == 'balance_leg_segment':
            return BALANCE_LEG_SEGMENT_OPTIONS
        if spec.get('choice_key') == 'component_scope':
            return CUSTOM_SCOPE_OPTIONS
        if spec.get('choice_key') == 'anchor_mode':
            return ANCHOR_MODE_OPTIONS
        if spec.get('choice_key') == 'primitive_type':
            return CUSTOM_PRIMITIVE_TYPE_OPTIONS
        if spec.get('choice_key') in {'start_anchor_id', 'end_anchor_id'}:
            profile = profile or self._current_profile()
            anchors = profile.get('custom_anchors', [])
            return [(str(anchor.get('id', '')), str(anchor.get('name') or anchor.get('id') or '锚点')) for anchor in anchors]
        if spec.get('choice_key') == 'parent_link_id':
            profile = profile or self._current_profile()
            links = profile.get('custom_links', [])
            options = [(str(link.get('id', '')), str(link.get('name') or link.get('id') or '连杆')) for link in links]
            return options or [('', '无可用连杆')]
        return []

    def _custom_item_default(self, part, profile=None):
        profile = profile or self._current_profile()
        if part == 'custom_primitive':
            return _normalize_custom_primitive({}, len(profile.get('custom_primitives', [])))
        if part == 'custom_anchor':
            return _normalize_custom_anchor({}, len(profile.get('custom_anchors', [])))
        if part == 'custom_link':
            anchor_ids = [item.get('id', '') for item in profile.get('custom_anchors', [])]
            return _normalize_custom_link({}, anchor_ids, len(profile.get('custom_links', [])))
        return {}

    def _mutate_custom_collection(self, part, action):
        key = self._custom_collection_key(part)
        if key is None:
            return
        self.selected_part = part
        self.selected_component_scope = 'single'
        self.selected_field_index = 0
        self.field_scroll = 0
        profile = self._current_profile()
        if part == 'custom_link':
            self._ensure_minimum_custom_anchors(2, profile=profile)
        collection = profile.setdefault(key, [])
        if action == 'add':
            collection.append(self._custom_item_default(part, profile=profile))
            self.selected_component_index = max(0, len(collection) - 1)
            self.status_text = f'已新建 {PART_LABELS.get(part, "部件")}'
        elif action == 'duplicate' and collection:
            index = max(0, min(self.selected_component_index, len(collection) - 1))
            duplicate = deepcopy(collection[index])
            duplicate['id'] = f"{duplicate.get('id', part)}_copy_{len(collection) + 1}"
            duplicate['name'] = f"{duplicate.get('name', '副本')} 副本"
            collection.insert(index + 1, duplicate)
            self.selected_component_index = index + 1
        elif action == 'delete' and collection:
            index = max(0, min(self.selected_component_index, len(collection) - 1))
            del collection[index]
            self.selected_component_index = max(0, min(index, len(collection) - 1))
        _normalize_custom_collections(profile)
        self._persist_current_profile(profile)
        self._clamp_selected_component_index(profile)

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
            {'part': 'body', 'label': '底盘长度', 'kind': 'number', 'key': 'body_length_m', 'min': 0.30, 'max': 10.00, 'step': 0.01},
            {'part': 'body', 'label': '底盘宽度', 'kind': 'number', 'key': 'body_width_m', 'min': 0.20, 'max': 10.00, 'step': 0.01},
            {'part': 'body', 'label': '视觉宽度系数', 'kind': 'number', 'key': 'body_render_width_scale', 'min': 0.45, 'max': 10.00, 'step': 0.01},
            {'part': 'body', 'label': '底盘高度', 'kind': 'number', 'key': 'body_height_m', 'min': 0.10, 'max': 2.40, 'step': 0.01},
            {'part': 'body', 'label': '离地间隙', 'kind': 'number', 'key': 'body_clearance_m', 'min': 0.02, 'max': 2.00, 'step': 0.01},
            {'part': 'body', 'label': '前面倾角', 'kind': 'number', 'key': 'body_front_tilt_deg', 'min': 0.0, 'max': 65.0, 'step': 1.0},
            {'part': 'body', 'label': '后面倾角', 'kind': 'number', 'key': 'body_rear_tilt_deg', 'min': 0.0, 'max': 65.0, 'step': 1.0},
            {'part': 'body', 'label': '左面倾角', 'kind': 'number', 'key': 'body_left_tilt_deg', 'min': 0.0, 'max': 65.0, 'step': 1.0},
            {'part': 'body', 'label': '右面倾角', 'kind': 'number', 'key': 'body_right_tilt_deg', 'min': 0.0, 'max': 65.0, 'step': 1.0},
            {'part': 'turret', 'label': '云台长度', 'kind': 'number', 'key': 'gimbal_length_m', 'min': 0.10, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台宽度', 'kind': 'number', 'key': 'gimbal_width_m', 'min': 0.05, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台厚度', 'kind': 'number', 'key': 'gimbal_body_height_m', 'min': 0.05, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台偏移 X', 'kind': 'number', 'key': 'gimbal_offset_x_m', 'min': -0.30, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '云台偏移 Y', 'kind': 'number', 'key': 'gimbal_offset_y_m', 'min': -0.30, 'max': 2.00, 'step': 0.01},
            {'part': 'turret', 'label': '相对连接件 X', 'kind': 'number', 'key': 'gimbal_relative_offset_x_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'turret', 'label': '相对连接件 Y', 'kind': 'number', 'key': 'gimbal_relative_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'turret', 'label': '相对连接件 Z', 'kind': 'number', 'key': 'gimbal_relative_offset_z_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件长度', 'kind': 'number', 'key': 'gimbal_mount_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件宽度', 'kind': 'number', 'key': 'gimbal_mount_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'mount', 'label': '连接件高度', 'kind': 'number', 'key': 'gimbal_mount_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管长度', 'kind': 'number', 'key': 'barrel_length_m', 'min': 0.00, 'max': 2.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管半径', 'kind': 'number', 'key': 'barrel_radius_m', 'min': 0.005, 'max': 2.00, 'step': 0.001},
            {'part': 'barrel', 'label': '八棱长边', 'kind': 'number', 'key': 'barrel_octagon_long_edge_m', 'min': 0.002, 'max': 0.20, 'step': 0.001},
            {'part': 'barrel', 'label': '八棱短边', 'kind': 'number', 'key': 'barrel_octagon_short_edge_m', 'min': 0.001, 'max': 0.20, 'step': 0.001},
            {'part': 'barrel', 'label': '枪管相对云台 X', 'kind': 'number', 'key': 'barrel_offset_x_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管相对云台 Y', 'kind': 'number', 'key': 'barrel_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'barrel', 'label': '枪管相对云台 Z', 'kind': 'number', 'key': 'barrel_offset_z_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮半径', 'kind': 'number', 'key': 'barrel_friction_wheel_radius_m', 'min': 0.00, 'max': 0.20, 'step': 0.001},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮高度', 'kind': 'number', 'key': 'barrel_friction_wheel_height_m', 'min': 0.00, 'max': 0.20, 'step': 0.001},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮 X', 'kind': 'number', 'key': 'barrel_friction_wheel_offset_x_m', 'min': -0.50, 'max': 0.50, 'step': 0.005},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮 Y', 'kind': 'number', 'key': 'barrel_friction_wheel_offset_y_m', 'min': -0.50, 'max': 0.50, 'step': 0.005},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮左右距', 'kind': 'number', 'key': 'barrel_friction_wheel_offset_z_m', 'min': 0.00, 'max': 0.50, 'step': 0.005},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮 Pitch', 'kind': 'number', 'key': 'barrel_friction_wheel_pitch_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮 Yaw', 'kind': 'number', 'key': 'barrel_friction_wheel_yaw_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'barrel_friction_wheel', 'label': '摩擦轮 Roll', 'kind': 'number', 'key': 'barrel_friction_wheel_roll_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'first_person_camera', 'label': '镜头 X', 'kind': 'number', 'key': 'first_person_camera_offset_x_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'first_person_camera', 'label': '镜头 Y', 'kind': 'number', 'key': 'first_person_camera_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'first_person_camera', 'label': '镜头 Z', 'kind': 'number', 'key': 'first_person_camera_offset_z_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'first_person_camera', 'label': '镜头 Yaw', 'kind': 'number', 'key': 'first_person_camera_yaw_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'first_person_camera', 'label': '镜头 Pitch', 'kind': 'number', 'key': 'first_person_camera_pitch_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'first_person_camera', 'label': '镜头 Roll', 'kind': 'number', 'key': 'first_person_camera_roll_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0},
            {'part': 'armor', 'label': '装甲宽度', 'kind': 'number', 'key': 'armor_plate_width_m', 'min': 0.00, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲长度', 'kind': 'number', 'key': 'armor_plate_length_m', 'min': 0.00, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲高度', 'kind': 'number', 'key': 'armor_plate_height_m', 'min': 0.00, 'max': 2.00, 'step': 0.01},
            {'part': 'armor', 'label': '装甲间距', 'kind': 'number', 'key': 'armor_plate_gap_m', 'min': 0.002, 'max': 2.00, 'step': 0.002},
            {'part': 'armor', 'label': '装甲厚度', 'kind': 'number', 'key': 'armor_plate_thickness_m', 'min': 0.000, 'max': 0.50, 'step': 0.001},
            {'part': 'armor_light', 'label': '灯条长度', 'kind': 'number', 'key': 'armor_light_length_m', 'min': 0.001, 'max': 2.00, 'step': 0.001},
            {'part': 'armor_light', 'label': '灯条宽度', 'kind': 'number', 'key': 'armor_light_width_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'armor_light', 'label': '灯条高度', 'kind': 'number', 'key': 'armor_light_height_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '枪管灯条长度', 'kind': 'number', 'key': 'barrel_light_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '枪管灯条宽度', 'kind': 'number', 'key': 'barrel_light_width_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '枪管灯条高度', 'kind': 'number', 'key': 'barrel_light_height_m', 'min': 0.005, 'max': 2.00, 'step': 0.005},
            {'part': 'barrel_light', 'label': '相对枪管 X', 'kind': 'number', 'key': 'barrel_light_offset_x_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'barrel_light', 'label': '相对枪管 Y', 'kind': 'number', 'key': 'barrel_light_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'barrel_light', 'label': '相对枪管 Z', 'kind': 'number', 'key': 'barrel_light_offset_z_m', 'min': -1.00, 'max': 1.00, 'step': 0.01},
            {'part': 'rear_health_light', 'label': '血条灯条长度', 'kind': 'number', 'key': 'rear_health_light_length_m', 'min': 0.0, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_health_light', 'label': '血条灯条宽度', 'kind': 'number', 'key': 'rear_health_light_width_m', 'min': 0.0, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_health_light', 'label': '血条灯条高度', 'kind': 'number', 'key': 'rear_health_light_height_m', 'min': 0.0, 'max': 2.00, 'step': 0.005},
            {'part': 'rear_health_light', 'label': '血条灯条 X', 'kind': 'number', 'key': 'rear_health_light_offset_x_m', 'min': -2.00, 'max': 2.00, 'step': 0.01},
            {'part': 'rear_health_light', 'label': '血条灯条 Y', 'kind': 'number', 'key': 'rear_health_light_offset_y_m', 'min': -2.00, 'max': 2.00, 'step': 0.01},
            {'part': 'rear_health_light', 'label': '血条灯条 Z', 'kind': 'number', 'key': 'rear_health_light_offset_z_m', 'min': -2.00, 'max': 2.00, 'step': 0.01},
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
            {'part': 'rear_climb', 'label': '平衡轮 X', 'kind': 'balance_wheel_x', 'step': 0.01},
        ]
        fields.append({'part': 'body', 'label': '结构整体离地', 'kind': 'number', 'key': 'structure_base_lift_m', 'min': 0.00, 'max': 1.20, 'step': 0.01})
        fields.extend([
            {'part': 'body', 'label': '前哨主体顶高', 'kind': 'number', 'key': 'structure_body_top_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'outpost'}},
            {'part': 'body', 'label': '前哨头部基准高', 'kind': 'number', 'key': 'structure_head_base_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'outpost'}},
            {'part': 'body', 'label': '前哨下肩高度', 'kind': 'number', 'key': 'structure_lower_shoulder_height_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'outpost'}},
            {'part': 'body', 'label': '前哨上肩高度', 'kind': 'number', 'key': 'structure_upper_shoulder_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'outpost'}},
            {'part': 'body', 'label': '前哨塔身半径', 'kind': 'number', 'key': 'structure_tower_radius_m', 'min': 0.00, 'max': 2.00, 'step': 0.01, 'roles': {'outpost'}},
            {'part': 'armor', 'label': '顶部装甲中心高', 'kind': 'number', 'key': 'structure_top_armor_center_height_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'outpost', 'base'}},
            {'part': 'armor', 'label': '顶部装甲前后偏移', 'kind': 'number', 'key': 'structure_top_armor_offset_x_m', 'min': -2.00, 'max': 2.00, 'step': 0.01, 'roles': {'outpost', 'base'}},
            {'part': 'armor', 'label': '顶部装甲左右偏移', 'kind': 'number', 'key': 'structure_top_armor_offset_z_m', 'min': -2.00, 'max': 2.00, 'step': 0.01, 'roles': {'outpost', 'base'}},
            {'part': 'armor', 'label': '顶部装甲倾角', 'kind': 'number', 'key': 'structure_top_armor_tilt_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0, 'roles': {'outpost', 'base'}},
            {'part': 'armor', 'label': '基地侧甲展开角', 'kind': 'number', 'key': 'structure_side_armor_open_angle_deg', 'min': 0.0, 'max': 120.0, 'step': 1.0, 'roles': {'base'}},
            {'part': 'armor', 'label': '基地侧甲外伸', 'kind': 'number', 'key': 'structure_side_armor_outward_offset_m', 'min': 0.00, 'max': 1.50, 'step': 0.01, 'roles': {'base'}},
            {'part': 'body', 'label': '基地屋顶高度', 'kind': 'number', 'key': 'structure_roof_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'body', 'label': '基地肩部高度', 'kind': 'number', 'key': 'structure_shoulder_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'body', 'label': '基地短边长度', 'kind': 'number', 'key': 'structure_hex_top_edge_m', 'min': 0.10, 'max': 3.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'mount', 'label': '探测桥宽度', 'kind': 'number', 'key': 'structure_detector_width_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'mount', 'label': '探测器高度', 'kind': 'number', 'key': 'structure_detector_height_m', 'min': 0.02, 'max': 2.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'mount', 'label': '探测桥中心高', 'kind': 'number', 'key': 'structure_detector_bridge_center_height_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'mount', 'label': '探测头中心高', 'kind': 'number', 'key': 'structure_detector_sensor_center_height_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'body', 'label': '核心柱高度', 'kind': 'number', 'key': 'structure_core_column_height_m', 'min': 0.10, 'max': 4.00, 'step': 0.01, 'roles': {'base'}},
            {'part': 'body', 'label': '机关总高', 'kind': 'number', 'key': 'body_height_m', 'min': 0.80, 'max': 4.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '底座高度', 'kind': 'number', 'key': 'structure_base_height_m', 'min': 0.05, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '离地高度', 'kind': 'number', 'key': 'structure_ground_clearance_m', 'min': 0.00, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上框总宽', 'kind': 'number', 'key': 'structure_frame_width_m', 'min': 0.40, 'max': 4.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上框纵深', 'kind': 'number', 'key': 'structure_frame_depth_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上框高度', 'kind': 'number', 'key': 'structure_frame_height_m', 'min': 0.40, 'max': 4.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '立柱间距', 'kind': 'number', 'key': 'structure_column_span_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '支架距中心', 'kind': 'number', 'key': 'structure_support_offset_m', 'min': 0.05, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '立柱宽度', 'kind': 'number', 'key': 'structure_frame_column_width_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '顶梁厚度', 'kind': 'number', 'key': 'structure_frame_beam_height_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转臂中心高度', 'kind': 'number', 'key': 'structure_rotor_center_height_m', 'min': 0.20, 'max': 4.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转盘相位角', 'kind': 'number', 'key': 'structure_rotor_phase_deg', 'min': -180.0, 'max': 180.0, 'step': 1.0, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转臂半径', 'kind': 'number', 'key': 'structure_rotor_radius_m', 'min': 0.10, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '中心轮毂半径', 'kind': 'number', 'key': 'structure_rotor_hub_radius_m', 'min': 0.04, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转臂长度', 'kind': 'number', 'key': 'structure_rotor_arm_length_m', 'min': 0.05, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转臂宽度', 'kind': 'number', 'key': 'structure_rotor_arm_width_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'turret', 'label': '转臂厚度', 'kind': 'number', 'key': 'structure_rotor_arm_height_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor_light', 'label': '灯臂长度', 'kind': 'number', 'key': 'structure_lamp_length_m', 'min': 0.04, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor_light', 'label': '灯臂宽度', 'kind': 'number', 'key': 'structure_lamp_width_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor_light', 'label': '灯臂厚度', 'kind': 'number', 'key': 'structure_lamp_height_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor', 'label': '下挂模块宽度', 'kind': 'number', 'key': 'structure_lower_module_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor', 'label': '下挂模块高度', 'kind': 'number', 'key': 'structure_lower_module_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor', 'label': '下挂模块厚度', 'kind': 'number', 'key': 'structure_lower_module_depth_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor', 'label': '下挂模块横向偏移', 'kind': 'number', 'key': 'structure_lower_module_offset_x_m', 'min': 0.05, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'armor', 'label': '下挂模块中心高', 'kind': 'number', 'key': 'structure_lower_module_center_height_m', 'min': 0.05, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '中间悬架宽度', 'kind': 'number', 'key': 'structure_hanger_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '中间悬架高度', 'kind': 'number', 'key': 'structure_hanger_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '中间悬架厚度', 'kind': 'number', 'key': 'structure_hanger_depth_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '中间悬架中心高', 'kind': 'number', 'key': 'structure_hanger_center_height_m', 'min': 0.05, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '两侧悬臂间距', 'kind': 'number', 'key': 'structure_cantilever_pair_gap_m', 'min': 0.10, 'max': 5.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '侧悬臂长度', 'kind': 'number', 'key': 'structure_cantilever_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '侧悬臂高度偏移', 'kind': 'number', 'key': 'structure_cantilever_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '侧悬臂高度', 'kind': 'number', 'key': 'structure_cantilever_height_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'mount', 'label': '侧悬臂厚度', 'kind': 'number', 'key': 'structure_cantilever_depth_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '下挂模块宽度', 'kind': 'number', 'key': 'structure_lower_module_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '下挂模块高度', 'kind': 'number', 'key': 'structure_lower_module_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '下挂模块厚度', 'kind': 'number', 'key': 'structure_lower_module_depth_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '下挂模块横向偏移', 'kind': 'number', 'key': 'structure_lower_module_offset_x_m', 'min': 0.05, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '下挂模块中心高', 'kind': 'number', 'key': 'structure_lower_module_center_height_m', 'min': 0.05, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '中间悬架宽度', 'kind': 'number', 'key': 'structure_hanger_width_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '中间悬架高度', 'kind': 'number', 'key': 'structure_hanger_height_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '中间悬架厚度', 'kind': 'number', 'key': 'structure_hanger_depth_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '中间悬架中心高', 'kind': 'number', 'key': 'structure_hanger_center_height_m', 'min': 0.05, 'max': 3.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '两侧悬臂间距', 'kind': 'number', 'key': 'structure_cantilever_pair_gap_m', 'min': 0.10, 'max': 5.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '侧悬臂长度', 'kind': 'number', 'key': 'structure_cantilever_length_m', 'min': 0.04, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '侧悬臂高度偏移', 'kind': 'number', 'key': 'structure_cantilever_offset_y_m', 'min': -1.00, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '侧悬臂高度', 'kind': 'number', 'key': 'structure_cantilever_height_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'assembly', 'label': '侧悬臂厚度', 'kind': 'number', 'key': 'structure_cantilever_depth_m', 'min': 0.02, 'max': 1.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
        ])
        fields.extend([
            {'part': 'body', 'label': '底座长度', 'kind': 'number', 'key': 'structure_base_length_m', 'min': 0.40, 'max': 8.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '底座宽度', 'kind': 'number', 'key': 'structure_base_width_m', 'min': 0.40, 'max': 8.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上层底座长度', 'kind': 'number', 'key': 'structure_base_top_length_m', 'min': 0.20, 'max': 8.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上层底座宽度', 'kind': 'number', 'key': 'structure_base_top_width_m', 'min': 0.20, 'max': 8.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
            {'part': 'body', 'label': '上层底座高度', 'kind': 'number', 'key': 'structure_base_top_height_m', 'min': 0.02, 'max': 2.00, 'step': 0.01, 'roles': {'energy_mechanism'}},
        ])
        for color_key, part, label in (
            ('body_color_rgb', 'body', '底盘'),
            ('turret_color_rgb', 'turret', '云台'),
            ('armor_color_rgb', 'armor', '装甲'),
            ('wheel_color_rgb', 'wheel', '车轮'),
        ):
            for channel_index, channel_label in enumerate(('R', 'G', 'B')):
                fields.append({'part': part, 'label': f'{label} {channel_label}', 'kind': 'color', 'color_key': color_key, 'channel': channel_index, 'min': 0, 'max': 255, 'step': 1})
        for channel_index, channel_label in enumerate(('R', 'G', 'B')):
            fields.append({'part': 'armor_light', 'label': f'灯臂 {channel_label}', 'kind': 'color', 'color_key': 'wheel_color_rgb', 'channel': channel_index, 'min': 0, 'max': 255, 'step': 1, 'roles': {'energy_mechanism'}})
        return _normalize_field_spec_bounds(fields)

    def _profile_has_turret(self, profile):
        if str(profile.get('role_key', '')).lower() == 'energy_mechanism':
            return True
        return float(profile.get('gimbal_length_m', 0.0)) > 1e-6 and float(profile.get('gimbal_body_height_m', 0.0)) > 1e-6

    def _profile_has_mount(self, profile):
        if str(profile.get('role_key', '')).lower() in {'base', 'energy_mechanism'}:
            return True
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
        if self.selected_part in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            self._ensure_custom_item_for_editing()
            profile = self._current_profile()
        self._clamp_selected_component_index(profile)
        if self.selected_part == 'custom_primitive':
            fields = [
                {'part': 'custom_primitive', 'label': '父部件', 'kind': 'choice', 'choice_key': 'parent_part'},
                {'part': 'custom_primitive', 'label': '作用范围', 'kind': 'choice', 'choice_key': 'component_scope'},
                {'part': 'custom_primitive', 'label': '部件索引', 'kind': 'custom_number', 'key': 'component_index', 'min': 0, 'max': 32, 'step': 1},
                {'part': 'custom_primitive', 'label': '形体类型', 'kind': 'choice', 'choice_key': 'primitive_type'},
                {'part': 'custom_primitive', 'label': '长度 X', 'kind': 'custom_vector', 'key': 'size_m', 'axis': 0, 'min': 0.002, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': '高度 Y', 'kind': 'custom_vector', 'key': 'size_m', 'axis': 1, 'min': 0.002, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': '宽度 Z', 'kind': 'custom_vector', 'key': 'size_m', 'axis': 2, 'min': 0.002, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': '偏移 X', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 0, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': '偏移 Y', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 1, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': '偏移 Z', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 2, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_primitive', 'label': 'Yaw', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 0, 'min': -180.0, 'max': 180.0, 'step': 1.0},
                {'part': 'custom_primitive', 'label': 'Pitch', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 1, 'min': -180.0, 'max': 180.0, 'step': 1.0},
                {'part': 'custom_primitive', 'label': 'Roll', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 2, 'min': -180.0, 'max': 180.0, 'step': 1.0},
            ]
            for channel_index, channel_label in enumerate(('R', 'G', 'B')):
                fields.append({'part': 'custom_primitive', 'label': f'颜色 {channel_label}', 'kind': 'custom_color', 'key': 'color_rgb', 'channel': channel_index, 'min': 0, 'max': 255, 'step': 1})
            item = self._custom_item_for_spec({'part': 'custom_primitive'}, profile=profile)
            if item is not None and _is_balance_leg_parent_part(item.get('parent_part')):
                fields.insert(1, {'part': 'custom_primitive', 'label': '腿部连杆', 'kind': 'choice', 'choice_key': 'balance_leg_segment'})
            return _normalize_field_spec_bounds(fields)
        if self.selected_part == 'custom_anchor':
            fields = [
                {'part': 'custom_anchor', 'label': '锚点模式', 'kind': 'choice', 'choice_key': 'anchor_mode'},
                {'part': 'custom_anchor', 'label': '父部件', 'kind': 'choice', 'choice_key': 'parent_part'},
                {'part': 'custom_anchor', 'label': '父连杆', 'kind': 'choice', 'choice_key': 'parent_link_id'},
                {'part': 'custom_anchor', 'label': '连杆位置', 'kind': 'custom_number', 'key': 'link_position_ratio', 'min': 0.0, 'max': 1.0, 'step': 0.01},
                {'part': 'custom_anchor', 'label': '作用范围', 'kind': 'choice', 'choice_key': 'component_scope'},
                {'part': 'custom_anchor', 'label': '部件索引', 'kind': 'custom_number', 'key': 'component_index', 'min': 0, 'max': 32, 'step': 1},
                {'part': 'custom_anchor', 'label': '偏移 X', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 0, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_anchor', 'label': '偏移 Y', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 1, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_anchor', 'label': '偏移 Z', 'kind': 'custom_vector', 'key': 'offset_m', 'axis': 2, 'min': -2.00, 'max': 2.00, 'step': 0.01},
                {'part': 'custom_anchor', 'label': 'Yaw', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 0, 'min': -180.0, 'max': 180.0, 'step': 1.0},
                {'part': 'custom_anchor', 'label': 'Pitch', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 1, 'min': -180.0, 'max': 180.0, 'step': 1.0},
                {'part': 'custom_anchor', 'label': 'Roll', 'kind': 'custom_vector', 'key': 'rotation_ypr_deg', 'axis': 2, 'min': -180.0, 'max': 180.0, 'step': 1.0},
            ]
            item = self._custom_item_for_spec({'part': 'custom_anchor'}, profile=profile)
            if item is not None and _is_balance_leg_parent_part(item.get('parent_part')):
                fields.insert(2, {'part': 'custom_anchor', 'label': '腿部连杆', 'kind': 'choice', 'choice_key': 'balance_leg_segment'})
            return _normalize_field_spec_bounds(fields)
        if self.selected_part == 'custom_link':
            fields = [
                {'part': 'custom_link', 'label': '起点锚点', 'kind': 'choice', 'choice_key': 'start_anchor_id'},
                {'part': 'custom_link', 'label': '终点锚点', 'kind': 'choice', 'choice_key': 'end_anchor_id'},
                {'part': 'custom_link', 'label': '半径', 'kind': 'custom_number', 'key': 'radius_m', 'min': 0.001, 'max': 0.20, 'step': 0.001},
                {'part': 'custom_link', 'label': '宽度', 'kind': 'custom_number', 'key': 'width_m', 'min': 0.001, 'max': 0.20, 'step': 0.001},
                {'part': 'custom_link', 'label': '厚度', 'kind': 'custom_number', 'key': 'thickness_m', 'min': 0.001, 'max': 0.20, 'step': 0.001},
                {'part': 'custom_link', 'label': '定长', 'kind': 'custom_number', 'key': 'length_m', 'min': 0.0, 'max': 2.00, 'step': 0.01},
            ]
            for channel_index, channel_label in enumerate(('R', 'G', 'B')):
                fields.append({'part': 'custom_link', 'label': f'颜色 {channel_label}', 'kind': 'custom_color', 'key': 'color_rgb', 'channel': channel_index, 'min': 0, 'max': 255, 'step': 1})
            return _normalize_field_spec_bounds(fields)
        if self.selected_part == 'turret' and not self._profile_has_turret(profile):
            return []
        if self.selected_part == 'mount' and not self._profile_has_mount(profile):
            return []
        if self.selected_part in {'barrel', 'barrel_light', 'barrel_friction_wheel', 'first_person_camera'} and not self._profile_has_barrel(profile):
            return []
        if self.selected_part == 'front_climb' and not self._profile_has_front_climb(profile):
            return []
        if self.selected_part == 'rear_climb' and not self._profile_has_rear_climb(profile):
            return []
        fields = [spec for spec in self.field_specs if spec.get('part') == self.selected_part]
        if self.current_role not in {'outpost', 'base', 'energy_mechanism'}:
            fields = [spec for spec in fields if spec.get('key') != 'structure_base_lift_m']
        fields = [spec for spec in fields if not spec.get('roles') or self.current_role in spec.get('roles', set())]
        if self.selected_part == 'wheel':
            if self.selected_component_scope == 'single' and profile.get('custom_wheel_positions_m'):
                fields.append({'part': 'wheel', 'label': f'轮 {self.selected_component_index + 1} X', 'kind': 'wheel_component', 'component_index': self.selected_component_index, 'axis': 0, 'min': -0.80, 'max': 2.00, 'step': 0.01})
                fields.append({'part': 'wheel', 'label': f'轮 {self.selected_component_index + 1} Y', 'kind': 'wheel_component', 'component_index': self.selected_component_index, 'axis': 1, 'min': -0.80, 'max': 2.00, 'step': 0.01})
        if self.selected_part == 'armor' and self.selected_component_scope == 'single':
            labels = ('装甲偏移 X', '装甲偏移 Y', '装甲偏移 Z')
            for axis, label in enumerate(labels):
                fields.append({'part': 'armor', 'label': f'{label} {self.selected_component_index + 1}', 'kind': 'component_vector', 'vector_key': 'armor_plate_offsets_m', 'component_index': self.selected_component_index, 'axis': axis, 'min': -2.00, 'max': 1.00, 'step': 0.01})
            rotation_labels = ('装甲 Yaw', '装甲 Pitch', '装甲 Roll')
            for axis, label in enumerate(rotation_labels):
                fields.append({'part': 'armor', 'label': f'{label} {self.selected_component_index + 1}', 'kind': 'component_vector', 'vector_key': 'armor_plate_rotations_ypr_deg', 'component_index': self.selected_component_index, 'axis': axis, 'min': -180.0, 'max': 180.0, 'step': 1.0})
        if self.selected_part == 'armor_light' and self.selected_component_scope == 'single':
            labels = ('灯条偏移 X', '灯条偏移 Y', '灯条偏移 Z')
            for axis, label in enumerate(labels):
                fields.append({'part': 'armor_light', 'label': f'{label} {self.selected_component_index + 1}', 'kind': 'component_vector', 'vector_key': 'armor_light_offsets_m', 'component_index': self.selected_component_index, 'axis': axis, 'min': -2.00, 'max': 2.00, 'step': 0.005})
            fields.append({'part': 'armor_light', 'label': f'距装甲板 {self.selected_component_index + 1}', 'kind': 'component_number', 'list_key': 'armor_light_plate_distances_m', 'component_index': self.selected_component_index, 'min': 0.000, 'max': 1.00, 'step': 0.001, 'default': max(0.004, float(profile.get('armor_plate_gap_m', 0.005)) * 0.15)})
        if self.selected_part == 'barrel_friction_wheel' and self.selected_component_scope == 'single':
            labels = ('摩擦轮单体 X', '摩擦轮单体 Y', '摩擦轮单体 Z')
            for axis, label in enumerate(labels):
                fields.append({'part': 'barrel_friction_wheel', 'label': f'{label} {self.selected_component_index + 1}', 'kind': 'component_vector', 'vector_key': 'barrel_friction_wheel_offsets_m', 'component_index': self.selected_component_index, 'axis': axis, 'min': -2.00, 'max': 2.00, 'step': 0.005})
        orbit_key, self_key = self._current_component_angle_keys()
        if orbit_key is not None and self.current_role not in {'outpost', 'base', 'energy_mechanism'}:
            orbit_label = '相对机器人轴心 Yaw'
            self_label = '相对自身轴心 Yaw'
            if self.selected_part == 'wheel':
                orbit_label = '轮安装偏航角'
                self_label = '轮自转角（自身 Z 轴）'
            fields.append({'part': self.selected_part, 'label': orbit_label, 'kind': 'component_angle', 'angle_key': orbit_key, 'min': -180.0, 'max': 180.0, 'step': 1.0})
            fields.append({'part': self.selected_part, 'label': self_label, 'kind': 'component_angle', 'angle_key': self_key, 'min': -180.0, 'max': 180.0, 'step': 1.0})
        return _normalize_field_spec_bounds(fields)

    def _current_profile(self):
        if self.current_role != 'infantry':
            profile = self.profiles[self.current_role]
            _normalize_custom_collections(profile)
            return profile
        store = self._ensure_infantry_profile_store()
        subtype_profiles = store.get('subtype_profiles', {})
        current_subtype = normalize_infantry_chassis_subtype(self.current_infantry_subtype)
        self.current_infantry_subtype = current_subtype
        profile = subtype_profiles[current_subtype]
        _normalize_custom_collections(profile)
        return profile

    def _persist_current_profile(self, profile):
        if self.current_role == 'infantry':
            store = self._ensure_infantry_profile_store()
            store['subtype_profiles'][self.current_infantry_subtype] = profile
            store['default_chassis_subtype'] = self.current_infantry_subtype
            self.profiles[self.current_role] = store
            return
        self.profiles[self.current_role] = profile

    def _custom_item_for_spec(self, spec, profile=None, create=False):
        profile = profile or self._current_profile()
        part = spec.get('part') or self.selected_part
        key = self._custom_collection_key(part)
        if key is None:
            return None
        if part == 'custom_link':
            self._ensure_minimum_custom_anchors(2, profile=profile)
        collection = profile.setdefault(key, [])
        if not collection and create:
            collection.append(self._custom_item_default(part, profile=profile))
            self.selected_component_index = 0
            self.selected_component_scope = 'single'
            self.selected_part = part
            _normalize_custom_collections(profile)
            collection = profile.setdefault(key, [])
        if not collection:
            return None
        index = max(0, min(self.selected_component_index, len(collection) - 1))
        return collection[index]

    def _field_value(self, spec):
        profile = self._current_profile()
        if spec['kind'] == 'choice':
            item = self._custom_item_for_spec(spec, profile=profile)
            if item is None:
                return 0
            options = self._custom_choice_options(spec, profile=profile)
            if spec.get('choice_key') == 'parent_part':
                raw_parent = str(item.get('parent_part', options[0][0] if options else ''))
                current = 'balance_leg' if _is_balance_leg_parent_part(raw_parent) else raw_parent
            elif spec.get('choice_key') == 'balance_leg_segment':
                current = _balance_leg_segment_from_parent_part(item.get('parent_part', 'balance_leg_upper_front'))
            else:
                current = str(item.get(spec['choice_key'], options[0][0] if options else ''))
            for index, (option_key, _label) in enumerate(options):
                if option_key == current:
                    return index
            return 0
        if spec['kind'] == 'custom_number':
            item = self._custom_item_for_spec(spec, profile=profile)
            if item is None:
                return float(spec.get('min', 0.0))
            return float(item.get(spec['key'], 0.0))
        if spec['kind'] == 'custom_vector':
            item = self._custom_item_for_spec(spec, profile=profile)
            if item is None:
                return 0.0
            return float(item.get(spec['key'], [0.0, 0.0, 0.0])[spec['axis']])
        if spec['kind'] == 'custom_color':
            item = self._custom_item_for_spec(spec, profile=profile)
            if item is None:
                return 0
            return int(item.get(spec['key'], [0, 0, 0])[spec['channel']])
        if spec['kind'] == 'number':
            return float(profile.get(spec['key'], 0.0))
        if spec['kind'] == 'wheel_component':
            return float(profile['custom_wheel_positions_m'][spec['component_index']][spec['axis']])
        if spec['kind'] == 'balance_wheel_x':
            positions = [position for position in profile.get('custom_wheel_positions_m', []) if isinstance(position, (list, tuple)) and len(position) >= 2]
            return min((float(position[0]) for position in positions), default=-float(profile.get('body_length_m', 0.60)) * 0.39)
        if spec['kind'] == 'component_vector':
            values = profile.setdefault(spec['vector_key'], [])
            index = int(spec.get('component_index', self.selected_component_index))
            while len(values) <= index:
                values.append([0.0, 0.0, 0.0])
            vector = _normalize_vector3(values[index], (0.0, 0.0, 0.0))
            values[index] = vector
            return float(vector[spec['axis']])
        if spec['kind'] == 'component_number':
            values = profile.setdefault(spec['list_key'], [])
            index = int(spec.get('component_index', self.selected_component_index))
            default_value = float(spec.get('default', 0.0))
            while len(values) <= index:
                values.append(default_value)
            values[index] = float(values[index])
            return float(values[index])
        if spec['kind'] == 'component_angle':
            values = profile.get(spec['angle_key'], [])
            if self.selected_component_scope == 'all':
                return float(values[0]) if values else 0.0
            index = max(0, min(self.selected_component_index, len(values) - 1)) if values else 0
            return float(values[index]) if values else 0.0
        return int(profile[spec['color_key']][spec['channel']])

    def _clamp_field_value(self, spec, value):
        if spec['kind'] == 'choice':
            options = self._custom_choice_options(spec)
            if not options:
                return 0.0
            return float(max(0, min(len(options) - 1, int(round(float(value))))))

        numeric_value = float(value)
        if spec.get('key') == 'component_index':
            return float(max(0, int(round(numeric_value))))
        if spec['kind'] in {'color', 'custom_color'}:
            return max(0.0, min(255.0, numeric_value))
        if spec.get('unbounded', True):
            return numeric_value
        min_value = spec.get('min', numeric_value)
        max_value = spec.get('max', numeric_value)
        min_value = numeric_value if min_value is None else float(min_value)
        max_value = numeric_value if max_value is None else float(max_value)
        if min_value > max_value:
            min_value, max_value = max_value, min_value
        return max(min_value, min(max_value, numeric_value))

    def _set_field_value(self, spec, value):
        clamped = self._clamp_field_value(spec, value)
        profile = self._current_profile()
        if spec['kind'] == 'choice':
            item = self._custom_item_for_spec(spec, profile=profile, create=True)
            if item is None:
                return
            options = self._custom_choice_options(spec, profile=profile)
            if not options:
                return
            option_index = max(0, min(int(round(clamped)), len(options) - 1))
            option_key = options[option_index][0]
            if spec.get('choice_key') == 'parent_part':
                if option_key == 'balance_leg':
                    item['parent_part'] = _balance_leg_segment_from_parent_part(item.get('parent_part', 'balance_leg_upper_front'))
                else:
                    item['parent_part'] = option_key
            elif spec.get('choice_key') == 'balance_leg_segment':
                item['parent_part'] = option_key
            else:
                item[spec['choice_key']] = option_key
                if spec.get('choice_key') == 'anchor_mode' and option_key == 'active' and not str(item.get('parent_link_id', '')).strip():
                    item['parent_link_id'] = _first_custom_link_id(profile)
            _normalize_custom_collections(profile)
            self._persist_current_profile(profile)
            return
        if spec['kind'] == 'custom_number':
            item = self._custom_item_for_spec(spec, profile=profile, create=True)
            if item is None:
                return
            item[spec['key']] = int(round(clamped)) if spec['step'] >= 1 else round(float(clamped), 3)
            _normalize_custom_collections(profile)
            self._persist_current_profile(profile)
            return
        if spec['kind'] == 'custom_vector':
            item = self._custom_item_for_spec(spec, profile=profile, create=True)
            if item is None:
                return
            values = list(item.get(spec['key'], [0.0, 0.0, 0.0]))
            while len(values) < 3:
                values.append(0.0)
            values[spec['axis']] = round(float(clamped), 3)
            item[spec['key']] = values
            _normalize_custom_collections(profile)
            self._persist_current_profile(profile)
            return
        if spec['kind'] == 'custom_color':
            item = self._custom_item_for_spec(spec, profile=profile, create=True)
            if item is None:
                return
            values = list(item.get(spec['key'], [0, 0, 0]))
            while len(values) < 3:
                values.append(0)
            values[spec['channel']] = int(round(clamped))
            item[spec['key']] = values
            _normalize_custom_collections(profile)
            self._persist_current_profile(profile)
            return
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
        if spec['kind'] == 'balance_wheel_x':
            positions = profile.setdefault('custom_wheel_positions_m', [])
            if not positions:
                profile['custom_wheel_positions_m'] = _build_default_wheel_positions(profile)
                positions = profile['custom_wheel_positions_m']
            for index, position in enumerate(list(positions)):
                if not isinstance(position, list) or len(position) < 2:
                    continue
                positions[index][0] = round(float(clamped), 3)
            self._persist_current_profile(profile)
            return
        if spec['kind'] == 'component_vector':
            values = profile.setdefault(spec['vector_key'], [])
            index = int(spec.get('component_index', self.selected_component_index))
            while len(values) <= index:
                values.append([0.0, 0.0, 0.0])
            vector = _normalize_vector3(values[index], (0.0, 0.0, 0.0))
            vector[spec['axis']] = round(float(clamped), 3)
            values[index] = vector
            return
        if spec['kind'] == 'component_number':
            values = profile.setdefault(spec['list_key'], [])
            index = int(spec.get('component_index', self.selected_component_index))
            default_value = float(spec.get('default', 0.0))
            while len(values) <= index:
                values.append(default_value)
            values[index] = round(float(clamped), 3)
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
        if self.selected_part in {'custom_primitive', 'custom_anchor', 'custom_link'} and not self._ensure_custom_item_for_editing():
            return
        visible_fields = self._visible_field_specs()
        if not visible_fields:
            return
        self.selected_field_index = max(0, min(self.selected_field_index, len(visible_fields) - 1))
        spec = visible_fields[self.selected_field_index]
        if spec['kind'] == 'choice':
            step = 1
            self._set_field_value(spec, self._field_value(spec) + direction * step)
            return
        step = spec.get('step', 1) * (5 if fast else 1)
        self._set_field_value(spec, self._field_value(spec) + direction * step)

    def _change_selected_component(self, delta):
        profile = self._current_profile()
        count = self._component_part_count(profile, self.selected_part)
        if count <= 0:
            return
        self.selected_component_index = (self.selected_component_index + int(delta)) % count
        self.active_numeric_input = None

    def _field_content_top_inset(self):
        if self.selected_part in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            return 132
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
            if current_spec['kind'] in {'color', 'custom_color'}:
                buffer_text = str(int(current_value))
            else:
                buffer_text = f'{float(current_value):.3f}'.rstrip('0').rstrip('.')
        if initial_text:
            buffer_text = initial_text
        self.active_numeric_input = {
            'field_index': self.selected_field_index,
            'buffer': buffer_text,
            'replace_on_next_text': not bool(initial_text),
        }
        return True

    def _begin_field_editor(self, field_index):
        if self.selected_part in {'custom_primitive', 'custom_anchor', 'custom_link'} and not self._ensure_custom_item_for_editing():
            return False
        visible_fields = self._visible_field_specs()
        if not (0 <= field_index < len(visible_fields)):
            return False
        self.selected_field_index = field_index
        spec = visible_fields[field_index]
        if spec['kind'] in {'choice'}:
            self._set_field_value(spec, self._field_value(spec) + 1)
            return True
        if spec['kind'] in {'number', 'color', 'custom_number', 'custom_vector', 'custom_color', 'wheel_component', 'balance_wheel_x', 'component_vector', 'component_angle'}:
            return self._begin_numeric_input()
        return False

    def _handle_field_row_click(self, field_panel, field_index):
        self.selected_field_index = field_index
        self._ensure_selected_field_visible(field_panel)
        now_ticks = pygame.time.get_ticks()
        double_click = self.last_field_click_index == field_index and (now_ticks - self.last_field_click_ticks) <= 360
        self.last_field_click_index = field_index
        self.last_field_click_ticks = now_ticks
        visible_fields = self._visible_field_specs()
        spec = visible_fields[field_index] if 0 <= field_index < len(visible_fields) else None
        begin_on_single_click = spec is not None and spec.get('kind') in {
            'choice',
            'number',
            'color',
            'custom_number',
            'custom_vector',
            'custom_color',
            'wheel_component',
            'balance_wheel_x',
            'component_vector',
            'component_angle',
        }
        if (double_click or begin_on_single_click) and self._begin_field_editor(field_index):
            return
        self.active_numeric_input = None

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
            parsed_value = int(buffer_text) if spec['kind'] in {'color', 'custom_color'} else float(buffer_text)
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
        if event.key in {pygame.K_LEFT, pygame.K_RIGHT}:
            direction = -1 if event.key == pygame.K_LEFT else 1
            modifiers = pygame.key.get_mods()
            self._commit_numeric_input()
            self._adjust_selected(direction, fast=bool(modifiers & pygame.KMOD_SHIFT))
            return True
        if event.key == pygame.K_BACKSPACE:
            self.active_numeric_input['buffer'] = str(self.active_numeric_input.get('buffer', ''))[:-1]
            return True
        text = str(getattr(event, 'unicode', '') or '')
        if text and text in '0123456789.-':
            buffer_text = str(self.active_numeric_input.get('buffer', ''))
            replace_buffer = bool(self.active_numeric_input.get('replace_on_next_text'))
            if replace_buffer:
                buffer_text = ''
            if text == '-' and buffer_text:
                return True
            if text == '.' and '.' in buffer_text:
                return True
            self.active_numeric_input['buffer'] = buffer_text + text
            self.active_numeric_input['replace_on_next_text'] = False
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
            'outpost_armor_yaw_rad': 0.0,
            'base_open_ratio': 0.0,
            'energy_rotor_yaw_rad': 0.0,
        }
        if self.preview_action_mode == 'rotate':
            spin = progress * math.tau * 1.6
            if self.current_role == 'outpost':
                state['outpost_armor_yaw_rad'] = spin
            elif self.current_role == 'energy_mechanism':
                state['energy_rotor_yaw_rad'] = spin
        elif self.preview_action_mode == 'open':
            state['base_open_ratio'] = progress
        elif self.preview_action_mode == 'step':
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
                    state['front_raise_m'] = 0.00
                    state['rear_foot_raise_m'] = -0.00
                    state['rear_foot_reach_m'] = -0.00
                else:
                    ratio = (progress - 0.7) / 0.3
                    state['front_drop_m'] = 0.18 - 0.10 * ratio
                    state['front_raise_m'] = 0.00 - 0.04 * ratio
                    state['rear_foot_raise_m'] = -0.00 * (1.0 - ratio)
                    state['rear_foot_reach_m'] = -0.00 * (1.0 - ratio)
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
        profile['_preview_outpost_armor_yaw_rad'] = motion['outpost_armor_yaw_rad']
        profile['_preview_base_open_ratio'] = motion['base_open_ratio']
        profile['_preview_energy_rotor_yaw_rad'] = motion['energy_rotor_yaw_rad']
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
        role_key = str(profile.get('role_key', '')).lower()
        if role_key in {'outpost', 'base', 'energy_mechanism'}:
            yield from self._iter_structure_3d_preview_primitives(profile, role_key)
            return

        render_width_scale = float(profile.get('body_render_width_scale', 0.82))
        has_turret = self._profile_has_turret(profile)
        has_mount = self._profile_has_mount(profile)
        has_barrel = self._profile_has_barrel(profile)
        has_front_climb = self._profile_has_front_climb(profile)
        has_rear_climb = self._profile_has_rear_climb(profile)
        body_y = float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.5
        yield ('body', (0.0, body_y, 0.0), (float(profile['body_length_m']) * 0.5, float(profile['body_height_m']) * 0.5, float(profile['body_width_m']) * 0.5 * render_width_scale))

        wheel_radius = max(0.018, float(profile['wheel_radius_m']))
        wheel_half_z = 0.020
        for wheel_component in _resolved_wheel_components(profile):
            wheel_x, wheel_z = wheel_component['center']
            wheel_axis, wheel_right, wheel_up = _preview_wheel_cylinder_axes(profile, wheel_component)
            yield (
                'wheel',
                (float(wheel_x), float(wheel_component.get('center_height_m', wheel_radius)), float(wheel_z)),
                (wheel_half_z, wheel_radius, wheel_radius),
                {'kind': 'oriented_box', 'forward': wheel_axis, 'right': wheel_right, 'up': wheel_up},
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
            plate_center_x = body_half_x + plate_forward + plate_top_length * 0.5
            plate_center_y = wheel_radius + plate_height * 0.5
            plate_center_z = max(body_half_z * 0.45, wheel_outer_z - plate_inner)
            span_length = max(plate_top_length, plate_bottom_length)
            for side_sign in (-1.0, 1.0):
                yield ('front_climb', (plate_center_x, plate_center_y, plate_center_z * side_sign), (span_length * 0.5, plate_height * 0.5, plate_width * 0.5))
                yield ('front_climb', (body_half_x * 0.78, body_y + float(profile['body_height_m']) * 0.22, plate_center_z * side_sign), (plate_top_length * 0.28, max(0.012, plate_height * 0.18), plate_width * 0.6))

        armor_half_h = float(profile['armor_plate_height_m']) * 0.5
        armor_thickness = _resolve_armor_plate_thickness(profile)
        armor_half_width = float(profile['armor_plate_length_m']) * 0.5
        for component in _resolved_armor_components(profile):
            armor_forward, armor_right, armor_up = _resolve_preview_rotated_axes(
                float(component['yaw_rad']),
                [0.0, math.degrees(float(component.get('pitch_rad', 0.0))), math.degrees(float(component.get('roll_rad', 0.0)))],
            )
            yield ('armor', component['center'], (armor_thickness * 0.5, armor_half_h, armor_half_width), {'kind': 'oriented_box', 'forward': tuple(float(value) for value in armor_forward), 'right': tuple(float(value) for value in armor_right), 'up': tuple(float(value) for value in armor_up)})

        armor_light_half_x = float(profile.get('armor_light_length_m', 0.10)) * 0.5
        armor_light_half_y = max(0.005, float(profile.get('armor_light_height_m', 0.02)) * 0.5)
        armor_light_half_z = max(0.005, float(profile.get('armor_light_width_m', 0.02)) * 0.5)
        for component in _resolved_armor_light_components(profile):
            yield ('armor_light', component['center_a'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), component['orientation'])
            yield ('armor_light', component['center_b'], (armor_light_half_z, armor_light_half_y, armor_light_half_x), component['orientation'])

        rear_health_length = float(profile.get('rear_health_light_length_m', 0.0))
        if rear_health_length <= 1e-6:
            rear_health_length = max(0.08, float(profile['body_width_m']) * render_width_scale * 0.74)
        rear_health_width = float(profile.get('rear_health_light_width_m', 0.0))
        if rear_health_width <= 1e-6:
            rear_health_width = min(max(float(profile['body_length_m']) * 0.045, 0.018), 0.038)
        rear_health_height = float(profile.get('rear_health_light_height_m', 0.0))
        if rear_health_height <= 1e-6:
            rear_health_height = min(max(float(profile['body_width_m']) * 0.035, 0.010), 0.018)
        yield (
            'rear_health_light',
            (
                -float(profile['body_length_m']) * 0.5 - rear_health_width * 0.24 + float(profile.get('rear_health_light_offset_x_m', 0.0)),
                float(profile['body_clearance_m']) + float(profile['body_height_m']) + rear_health_height * 0.58 + float(profile.get('rear_health_light_offset_y_m', 0.0)),
                float(profile.get('rear_health_light_offset_z_m', 0.0)),
            ),
            (rear_health_width * 0.5, rear_health_height * 0.5, rear_health_length * 0.5),
        )

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
                    yield ('rear_climb', (0.0, 0.0, 0.0), (0.001, 0.001, 0.001), {'kind': 'beam', 'start': (leg_geometry['upper_front'][0], leg_geometry['upper_front'][1], side_z), 'end': (leg_geometry['knee_front'][0], leg_geometry['knee_front'][1], side_z), 'height': upper_height, 'thickness': upper_width})
                    yield ('rear_climb', (0.0, 0.0, 0.0), (0.001, 0.001, 0.001), {'kind': 'beam', 'start': (leg_geometry['upper_rear'][0], leg_geometry['upper_rear'][1], side_z), 'end': (leg_geometry['knee_rear'][0], leg_geometry['knee_rear'][1], side_z), 'height': upper_height, 'thickness': upper_width})
                    yield ('rear_climb', (0.0, 0.0, 0.0), (0.001, 0.001, 0.001), {'kind': 'beam', 'start': (leg_geometry['knee_center'][0], leg_geometry['knee_center'][1], side_z), 'end': (leg_geometry['foot'][0], leg_geometry['foot'][1], side_z), 'height': lower_height, 'thickness': lower_width})
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
                    yield ('rear_climb', (0.0, 0.0, 0.0), (0.001, 0.001, 0.001), {'kind': 'beam', 'start': (rear_points['mount'][0], rear_points['mount'][1], side_z), 'end': (rear_points['joint'][0], rear_points['joint'][1], side_z), 'height': upper_height, 'thickness': upper_width})
                    yield ('rear_climb', (0.0, 0.0, 0.0), (0.001, 0.001, 0.001), {'kind': 'beam', 'start': (rear_points['joint'][0], rear_points['joint'][1], side_z), 'end': (rear_points['foot'][0], rear_points['foot'][1], side_z), 'height': lower_height, 'thickness': lower_width})
                    yield ('rear_climb', (rear_points['joint'][0], rear_points['joint'][1], side_z), (max(upper_height, lower_height) * 0.75, max(upper_height, lower_height) * 0.75, max(upper_width, lower_width) * 0.55))

        if has_mount or has_turret:
            mount_offset_x = _profile_mount_offset_x(profile)
            mount_offset_z = _profile_mount_offset_z(profile)
            turret_offset_x = _profile_turret_offset_x(profile)
            turret_offset_z = _profile_turret_offset_z(profile)
            mount_center_y = _profile_mount_center_height(profile)
            turret_center_y = _profile_turret_center_height(profile)
            if has_mount:
                connector_half_height = max(0.02, (float(profile.get('gimbal_mount_gap_m', 0.0)) + float(profile.get('gimbal_mount_height_m', 0.0))) * 0.5)
                yield (
                    'mount',
                    (mount_offset_x, mount_center_y, mount_offset_z),
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
                    barrel_base_x = turret_offset_x + float(profile['gimbal_length_m']) * 0.5 + float(profile.get('barrel_offset_x_m', 0.0))
                    barrel_base_y = turret_center_y + float(profile.get('barrel_offset_y_m', 0.0))
                    barrel_base_z = turret_offset_z + float(profile.get('barrel_offset_z_m', 0.0))
                    yield (
                        'barrel',
                        (barrel_base_x + barrel_length * 0.5, barrel_base_y, barrel_base_z),
                        (barrel_length * 0.5, barrel_radius, barrel_radius),
                    )
                    for center, half_extents, _wheel_ypr, _wheel_index, orientation in _friction_wheel_layout(profile, barrel_base_x, barrel_base_y, barrel_base_z, barrel_radius):
                        yield ('barrel_friction_wheel', center, half_extents, orientation)
                    yield (
                        'first_person_camera',
                        (
                            barrel_base_x + float(profile.get('first_person_camera_offset_x_m', 0.04)),
                            barrel_base_y + float(profile.get('first_person_camera_offset_y_m', 0.06)),
                            barrel_base_z + float(profile.get('first_person_camera_offset_z_m', 0.0)),
                        ),
                        (0.012, 0.012, 0.012),
                        math.radians(float(profile.get('first_person_camera_yaw_deg', 0.0))),
                    )
                    barrel_light_half_x = float(profile.get('barrel_light_length_m', 0.10)) * 0.5
                    barrel_light_half_y = max(0.005, float(profile.get('barrel_light_height_m', 0.02)) * 0.5)
                    barrel_light_half_z = max(0.005, float(profile.get('barrel_light_width_m', 0.02)) * 0.5)
                    barrel_light_center_x = barrel_base_x + barrel_length * 0.45 + float(profile.get('barrel_light_offset_x_m', 0.0))
                    barrel_light_center_y = barrel_base_y + float(profile.get('barrel_light_offset_y_m', 0.0))
                    barrel_light_center_z = barrel_base_z + float(profile.get('barrel_light_offset_z_m', 0.0))
                    yield ('barrel_light', (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z + barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z))
                    yield ('barrel_light', (barrel_light_center_x, barrel_light_center_y, barrel_light_center_z - barrel_light_half_z * 3.0), (barrel_light_half_x, barrel_light_half_y, barrel_light_half_z))

            if role_key == 'hero':
                turret_width = float(profile['gimbal_width_m']) * render_width_scale
                turret_height = float(profile['gimbal_body_height_m'])
                camera_center = (
                    turret_offset_x + HERO_SUBVIEW_CAMERA_BODY_LENGTH_M * 0.5 - 0.004,
                    turret_center_y + max(0.010, turret_height * 0.18) + HERO_SUBVIEW_CAMERA_CONNECTOR_LENGTH_M * 0.707 + HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M * 0.5 - 0.002,
                    turret_offset_z - max(0.018, turret_width * 0.46) - 0.006,
                )
                yield ('hero_subview_camera', camera_center, (HERO_SUBVIEW_CAMERA_BODY_LENGTH_M * 0.5, HERO_SUBVIEW_CAMERA_BODY_HEIGHT_M * 0.5, HERO_SUBVIEW_CAMERA_BODY_WIDTH_M * 0.5))

        attachment_pose_resolver = getattr(self.preview_renderer_3d, '_preview_attachment_part_poses', None)
        attachment_poses = attachment_pose_resolver(profile) if callable(attachment_pose_resolver) else []

        def matching_attachment_poses(parent_part, component_scope, component_index):
            for pose in attachment_poses:
                if pose['part'] != parent_part:
                    continue
                if component_scope == 'all' or int(pose['index']) == int(component_index):
                    yield pose

        for primitive_index, primitive in enumerate(profile.get('custom_primitives', [])):
            parent_part = str(primitive.get('parent_part', 'body'))
            component_scope = str(primitive.get('component_scope', 'single'))
            component_index = int(primitive.get('component_index', 0))
            size_m = primitive.get('size_m', [0.06, 0.04, 0.04])
            offset_m = primitive.get('offset_m', [0.0, 0.0, 0.0])
            rotation_ypr_deg = primitive.get('rotation_ypr_deg', [0.0, 0.0, 0.0])
            half_extents = (
                max(0.002, float(size_m[0])) * 0.5,
                max(0.002, float(size_m[1])) * 0.5,
                max(0.002, float(size_m[2])) * 0.5,
            )
            for pose in matching_attachment_poses(parent_part, component_scope, component_index):
                base_forward, base_right, base_up = _resolve_preview_rotated_axes(
                    float(pose['yaw_rad']),
                    [0.0, math.degrees(float(pose.get('pitch_rad', 0.0))), math.degrees(float(pose.get('roll_rad', 0.0)))],
                )
                forward, right, up = _resolve_preview_rotated_basis(base_forward, base_right, base_up, rotation_ypr_deg)
                center = _preview_local_point(pose['center'], base_forward, base_right, base_up, offset_m)
                yield (
                    'custom_primitive',
                    tuple(float(value) for value in center),
                    half_extents,
                    {
                        'kind': 'oriented_box',
                        'forward': tuple(float(value) for value in forward),
                        'right': tuple(float(value) for value in right),
                        'up': tuple(float(value) for value in up),
                        'component_index': primitive_index,
                    },
                )

        anchor_variants = _resolve_preview_custom_anchor_point_variants(profile, attachment_poses)
        for anchor_index, anchor in enumerate(profile.get('custom_anchors', [])):
            for resolved_anchor in anchor_variants.get(str(anchor.get('id', '')), []):
                yield (
                    'custom_anchor',
                    resolved_anchor['point'],
                    (0.011, 0.011, 0.011) if _is_active_anchor(anchor) else (0.008, 0.008, 0.008),
                    {
                        'kind': 'oriented_box',
                        'forward': tuple(float(value) for value in resolved_anchor['forward']),
                        'right': tuple(float(value) for value in resolved_anchor['right']),
                        'up': tuple(float(value) for value in resolved_anchor['up']),
                        'component_index': anchor_index,
                        'active_anchor': _is_active_anchor(anchor),
                    },
                )

        for link_index, link in enumerate(profile.get('custom_links', [])):
            for start_anchor, end_anchor in _pair_preview_anchor_variants(anchor_variants, link.get('start_anchor_id', ''), link.get('end_anchor_id', '')):
                start_point = start_anchor['point']
                end_point = end_anchor['point']
                radius = max(0.001, float(link.get('radius_m', 0.012)))
                fixed_length = max(0.0, float(link.get('length_m', 0.0) or 0.0))
                resolved_end = _resolve_fixed_link_end(start_point, end_point, fixed_length)
                yield (
                    'custom_link',
                    (0.0, body_y, 0.0),
                    (0.001, 0.001, 0.001),
                    {
                        'kind': 'beam',
                        'start': start_point,
                        'end': resolved_end,
                        'height': max(0.001, float(link.get('width_m', radius * 2.0))),
                        'thickness': max(0.001, float(link.get('thickness_m', radius * 2.0))),
                        'component_index': link_index,
                    },
                )

    def _iter_structure_3d_preview_primitives(self, profile, role_key):
        if role_key == 'outpost':
            lift = float(profile.get('structure_base_lift_m', 0.40))
            tower_height = max(0.8, float(profile.get('body_height_m', 1.578)))
            top_diameter = max(0.24, float(profile.get('body_width_m', 0.55)))
            tower_radius = max(0.18, top_diameter * 0.36)
            base_width = max(0.30, float(profile.get('body_length_m', 0.65)))
            armor_side = max(0.04, float(profile.get('armor_plate_width_m', 0.13)))
            armor_thickness = _resolve_armor_plate_thickness(profile)
            radius = tower_radius + 0.055
            head_base_height = tower_height * (1.318 / 1.578)
            armor_spin = float(profile.get('_preview_outpost_armor_yaw_rad', 0.0))
            yield ('body', (0.0, lift + tower_height * 0.48, 0.0), (base_width * 0.45, tower_height * 0.52, base_width * 0.45))
            yield ('body', (0.03, lift + tower_height - 0.05, 0.0), (0.105, 0.06, 0.09))
            yield ('armor', (math.cos(armor_spin) * radius, lift + tower_height - 0.055, math.sin(armor_spin) * radius), (armor_side * 0.5, armor_side * 0.5, armor_thickness * 0.5), armor_spin)
            for index, yaw in enumerate([0.0, math.tau / 3.0, math.tau * 2.0 / 3.0]):
                height = lift + head_base_height - 0.07 + [0.05, 0.0, -0.05][index]
                yield ('armor', (math.cos(yaw + armor_spin) * radius, height, math.sin(yaw + armor_spin) * radius), (armor_thickness * 0.5, armor_side * 0.5, armor_side * 0.5), yaw + armor_spin)
            yield ('armor_light', (radius * 0.68, lift + tower_height * 0.48, 0.0), (0.020, tower_height * 0.22, 0.000))
            return

        if role_key == 'energy_mechanism':
            frame_width = max(0.80, float(profile.get('structure_frame_width_m', 1.55)))
            frame_depth = max(0.06, float(profile.get('structure_frame_depth_m', 0.26)))
            base_height = max(0.00, float(profile.get('structure_base_height_m', 0.30)))
            ground_clearance = max(0.0, float(profile.get('structure_ground_clearance_m', 0.0)))
            frame_height = max(0.80, float(profile.get('structure_frame_height_m', 1.72)))
            column_span = max(0.20, min(frame_width, float(profile.get('structure_column_span_m', 1.40))))
            support_offset = max(0.10, float(profile.get('structure_support_offset_m', column_span * 0.5)))
            column_w = max(0.04, float(profile.get('structure_frame_column_width_m', 0.12)))
            rotor_center_h = max(base_height + ground_clearance + 0.20, float(profile.get('structure_rotor_center_height_m', 1.23)))
            rotor_phase_rad = math.radians(float(profile.get('structure_rotor_phase_deg', 90.0)))
            rotor_radius = max(0.18, float(profile.get('structure_rotor_radius_m', 0.46)))
            lamp_length = max(0.06, float(profile.get('structure_lamp_length_m', 0.16)))
            lamp_height = max(0.03, float(profile.get('structure_lamp_height_m', 0.00)))
            lamp_width = max(0.03, float(profile.get('structure_lamp_width_m', 0.07)))
            arm_width = max(0.04, float(profile.get('structure_rotor_arm_width_m', 0.06)))
            lamp_disk_radius = max(lamp_length, lamp_width) * 0.50
            lamp_center_radius = rotor_radius + max(lamp_disk_radius * 0.42, arm_width * 1.10)
            hanger_w = max(0.18, float(profile.get('structure_hanger_width_m', 0.62)))
            hanger_h = max(0.10, float(profile.get('structure_hanger_height_m', 0.38)))
            hanger_d = max(0.04, float(profile.get('structure_hanger_depth_m', 0.10)))
            hanger_center_h = max(hanger_h * 0.5, float(profile.get('structure_hanger_center_height_m', 1.18)))
            cantilever_pair_gap = max(frame_width + max(0.00, float(profile.get('structure_cantilever_length_m', 0.36))), float(profile.get('structure_cantilever_pair_gap_m', frame_width + 0.36)))
            cantilever_length = max(0.00, float(profile.get('structure_cantilever_length_m', 0.36)))
            cantilever_offset_y = float(profile.get('structure_cantilever_offset_y_m', 0.0))
            cantilever_height = max(0.04, float(profile.get('structure_cantilever_height_m', 0.12)))
            cantilever_depth = max(0.04, float(profile.get('structure_cantilever_depth_m', 0.10)))
            rotor_yaw = float(profile.get('_preview_energy_rotor_yaw_rad', 0.0))
            body_length = max(0.40, float(profile.get('structure_base_length_m', max(frame_width * 1.65, float(profile.get('body_length_m', 1.55)) * 1.72))))
            body_width = max(0.40, float(profile.get('structure_base_width_m', max(frame_depth * 6.0, float(profile.get('body_width_m', 0.98)) * 2.45))))
            base_pad_length = max(0.20, float(profile.get('structure_base_top_length_m', body_length * 0.34)))
            base_pad_width = max(0.16, float(profile.get('structure_base_top_width_m', body_width * 0.24)))
            post_height = max(0.80, frame_height - base_height)
            beam_h = max(0.04, float(profile.get('structure_frame_beam_height_m', 0.10)))
            top_beam_y = ground_clearance + frame_height - beam_h * 0.5
            for side_sign in (-1.0, 1.0):
                yield ('body', (side_sign * support_offset, ground_clearance + base_height * 0.5, 0.0), (base_pad_length * 0.5, base_height * 0.5, base_pad_width * 0.5))
                yield ('body', (side_sign * support_offset, ground_clearance + base_height + post_height * 0.5, 0.0), (column_w * 0.5, post_height * 0.5, frame_depth * 0.5))
            yield ('mount', (0.0, top_beam_y, 0.0), (max(frame_width, support_offset * 2.0) * 0.5, beam_h * 0.5, column_w * 0.6))
            lower_module_w = max(0.04, float(profile.get('structure_lower_module_width_m', 0.20)))
            lower_module_h = max(0.04, float(profile.get('structure_lower_module_height_m', 0.24)))
            lower_module_d = max(0.04, float(profile.get('structure_lower_module_depth_m', 0.18)))
            lower_module_offset = max(0.05, float(profile.get('structure_lower_module_offset_x_m', 0.48)))
            lower_module_center_h = max(base_height + lower_module_h * 0.5, float(profile.get('structure_lower_module_center_height_m', 0.94)))
            rotor_axis_gap = max(
                frame_depth * 1.8,
                max(0.05, float(profile.get('structure_rotor_hub_radius_m', 0.09))) * 2.6,
                min(cantilever_pair_gap, frame_width) * 0.42 + cantilever_length * 0.30,
            )
            for rotor_index, rotor_z in enumerate((-rotor_axis_gap * 0.5, rotor_axis_gap * 0.5)):
                rotor_center_x = 0.0
                rotor_center_y = rotor_center_h + cantilever_offset_y
                connector_center_y = hanger_center_h
                yield ('mount', (0.0, (connector_center_y + rotor_center_y) * 0.5, rotor_z * 0.5), (max(hanger_w * 0.5, 0.08), max(abs(rotor_center_y - connector_center_y) * 0.5, cantilever_height), max(abs(rotor_z) * 0.5, cantilever_depth * 0.5)))
                yield ('turret', (rotor_center_x, rotor_center_y, rotor_z), (rotor_radius, rotor_radius, max(frame_depth * 0.55, 0.06)))
                for index in range(5):
                    yaw = rotor_yaw + rotor_phase_rad + index * math.tau / 5.0
                    yield ('armor_light', (rotor_center_x + math.cos(yaw) * lamp_center_radius, rotor_center_y + math.sin(yaw) * lamp_center_radius, rotor_z), (lamp_disk_radius, lamp_height * 0.55, lamp_width * 0.30))
            module_side_offset = max(lower_module_w * 0.72, lower_module_offset)
            assembly_center_y = (hanger_center_h + lower_module_center_h) * 0.5
            assembly_half_y = max(0.06, abs(hanger_center_h - lower_module_center_h) * 0.5 + lower_module_h * 0.6)
            yield ('assembly', (0.0, assembly_center_y, 0.0), (module_side_offset + lower_module_w * 0.6, assembly_half_y, max(lower_module_d * 0.8, hanger_d * 0.5)))
            for side_sign in (-1.0, 1.0):
                yield ('assembly', (side_sign * module_side_offset, lower_module_center_h, 0.0), (lower_module_w * 0.6, lower_module_h * 0.5, lower_module_d * 0.6))
            return

        length = max(0.8, float(profile.get('body_length_m', 1.881)))
        width = max(0.7, float(profile.get('body_width_m', 1.609))) * max(0.4, float(profile.get('body_render_width_scale', 1.0)))
        height = max(0.5, float(profile.get('body_height_m', 1.181)))
        armor_side = max(0.04, float(profile.get('armor_plate_width_m', 0.13)))
        armor_thickness = _resolve_armor_plate_thickness(profile)
        open_ratio = max(0.0, min(1.0, float(profile.get('_preview_base_open_ratio', 0.0))))
        yield ('body', (0.0, height * 0.47, 0.0), (length * 0.50, height * 0.50, width * 0.50))
        yield ('body', (0.0, height * 0.58, 0.0), (0.055, min(height * 0.33, 0.3915), 0.06))
        yield ('armor', (length * 0.04, height * (1.150 / 1.181), 0.0), (armor_side * 0.5, armor_thickness * 0.5, armor_side * 0.5))
        yield ('armor', (length * 0.15, height * 0.70, 0.0), (armor_thickness * 0.5, armor_side * 0.5, armor_side * 0.5))
        for side in (-1.0, 1.0):
            side_shift = open_ratio * width * 0.14
            side_raise = open_ratio * 0.06
            yield ('armor', (-length * 0.07, height * 0.44 + side_raise, side * (width * 0.43 + side_shift)), (length * 0.18, height * 0.24, 0.035))
            yield ('armor_light', (-length * 0.07, height * 0.50 + side_raise, side * (width * 0.47 + side_shift)), (length * 0.12, height * 0.14, 0.012))
        yield ('body', (0.02, height * (1.093 / 1.181), 0.0), (0.04, 0.022, min(0.49, width * 0.30)))
        yield ('body', (0.0, height * (1.136 / 1.181), 0.0), (0.03, max(0.030, height * (0.095 / 1.181) * 0.50), 0.03))

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

    def _preview_3d_box_corners(self, center, half_extents, yaw_rad=0.0, pitch_rad=0.0, roll_rad=0.0):
        cx, cy, cz = [float(value) for value in center]
        hx, hy, hz = [max(0.001, float(value)) for value in half_extents]
        forward, right, up = _preview_basis_from_ypr(float(yaw_rad), float(pitch_rad), float(roll_rad))
        corners = []
        for local_x in (-hx, hx):
            for local_y in (-hy, hy):
                for local_z in (-hz, hz):
                    corners.append((
                        cx + right[0] * local_x + up[0] * local_y + forward[0] * local_z,
                        cy + right[1] * local_x + up[1] * local_y + forward[1] * local_z,
                        cz + right[2] * local_x + up[2] * local_y + forward[2] * local_z,
                    ))
        return corners

    def _preview_3d_oriented_box_corners(self, center, half_extents, forward, right, up):
        cx, cy, cz = [float(value) for value in center]
        hx, hy, hz = [max(0.001, float(value)) for value in half_extents]
        forward_vec = _normalize_preview_axis(forward, (1.0, 0.0, 0.0))
        right_vec = _normalize_preview_axis(right, (0.0, 0.0, 1.0))
        up_vec = _normalize_preview_axis(up, (0.0, 1.0, 0.0))
        corners = []
        center_vec = np.array([cx, cy, cz], dtype='f4')
        for local_x in (-hx, hx):
            for local_y in (-hy, hy):
                for local_z in (-hz, hz):
                    point = center_vec + forward_vec * local_x + up_vec * local_y + right_vec * local_z
                    corners.append(tuple(float(value) for value in point))
        return corners

    def _preview_3d_beam_corners(self, start_point, end_point, height, thickness):
        start_x, start_y, start_z = [float(value) for value in start_point]
        end_x, end_y, end_z = [float(value) for value in end_point]
        delta_x = end_x - start_x
        delta_y = end_y - start_y
        length = math.hypot(delta_x, delta_y)
        if length <= 1e-6:
            half = max(0.001, float(height) * 0.5)
            return self._preview_3d_box_corners(start_point, (half, half, max(0.001, float(thickness) * 0.5)))
        side_x = -delta_y / length
        side_y = delta_x / length
        half_height = max(0.001, float(height) * 0.5)
        half_thickness = max(0.001, float(thickness) * 0.5)
        return [
            (start_x + side_x * half_height, start_y + side_y * half_height, start_z - half_thickness),
            (end_x + side_x * half_height, end_y + side_y * half_height, end_z - half_thickness),
            (end_x - side_x * half_height, end_y - side_y * half_height, end_z - half_thickness),
            (start_x - side_x * half_height, start_y - side_y * half_height, start_z - half_thickness),
            (start_x + side_x * half_height, start_y + side_y * half_height, start_z + half_thickness),
            (end_x + side_x * half_height, end_y + side_y * half_height, end_z + half_thickness),
            (end_x - side_x * half_height, end_y - side_y * half_height, end_z + half_thickness),
            (start_x - side_x * half_height, start_y - side_y * half_height, start_z + half_thickness),
        ]

    def _preview_3d_primitive_corners(self, primitive):
        if len(primitive) >= 4 and isinstance(primitive[3], dict) and primitive[3].get('kind') == 'beam':
            return self._preview_3d_beam_corners(
                primitive[3]['start'],
                primitive[3]['end'],
                primitive[3]['height'],
                primitive[3]['thickness'],
            )
        if len(primitive) >= 4 and isinstance(primitive[3], dict) and primitive[3].get('kind') == 'oriented_box':
            return self._preview_3d_oriented_box_corners(
                primitive[1],
                primitive[2],
                primitive[3]['forward'],
                primitive[3]['right'],
                primitive[3]['up'],
            )
        part, center, half_extents = primitive[:3]
        ypr = primitive[3] if len(primitive) >= 4 and not isinstance(primitive[3], dict) else 0.0
        if isinstance(ypr, (list, tuple)) and len(ypr) >= 3:
            return self._preview_3d_box_corners(center, half_extents, ypr[0], ypr[1], ypr[2])
        return self._preview_3d_box_corners(center, half_extents, float(ypr))

    def _preview_3d_primitive_center(self, primitive):
        if len(primitive) >= 4 and isinstance(primitive[3], dict) and primitive[3].get('kind') == 'beam':
            start = primitive[3]['start']
            end = primitive[3]['end']
            return (
                (float(start[0]) + float(end[0])) * 0.5,
                (float(start[1]) + float(end[1])) * 0.5,
                (float(start[2]) + float(end[2])) * 0.5,
            )
        return primitive[1]

    def _preview_3d_primitive_component_index(self, primitive, part_counts):
        part = primitive[0]
        if len(primitive) >= 4 and isinstance(primitive[3], dict) and 'component_index' in primitive[3]:
            return int(primitive[3]['component_index'])
        component_index = part_counts.get(part, 0)
        part_counts[part] = component_index + 1
        return component_index

    def _resolve_3d_preview_camera(self, rect, profile, yaw=None, pitch=None):
        if '_terrain_scene_look_at' not in globals() or '_terrain_scene_perspective_matrix' not in globals():
            return None
        width, height = rect.size
        if width <= 1 or height <= 1:
            return None
        target = np.array([0.0, float(profile['body_clearance_m']) + float(profile['body_height_m']) * 0.45, 0.0], dtype='f4')
        bounds_radius = 0.6
        if self.preview_renderer_3d is not None:
            geometry_key = self.preview_renderer_3d._profile_geometry_key(profile)
            if geometry_key != self.preview_renderer_3d.geometry_key:
                self.preview_renderer_3d._build_geometry(profile)
                self.preview_renderer_3d.geometry_key = geometry_key
            bounds_radius = max(bounds_radius, float(getattr(self.preview_renderer_3d, 'bounds_radius', 0.6)))
        else:
            bounds_radius = max(
                bounds_radius,
                float(profile['body_length_m']) * 0.9,
                float(profile['body_width_m']) * 0.9,
                float(profile.get('gimbal_length_m', 0.0)) + float(profile.get('barrel_length_m', 0.0)) * 0.8,
                _profile_turret_center_height(profile) + 0.25,
            )
        zoom = max(0.45, min(3.00, float(getattr(self, 'preview_zoom', 1.0))))
        distance = max(0.55, bounds_radius * 2.9 / zoom)
        yaw = self.preview_3d_yaw if yaw is None else float(yaw)
        pitch = self.preview_3d_pitch if pitch is None else float(pitch)
        eye = np.array([
            math.sin(yaw) * math.cos(pitch) * distance,
            math.sin(pitch) * distance + bounds_radius * 0.25,
            math.cos(yaw) * math.cos(pitch) * distance,
        ], dtype='f4') + target
        projection = _terrain_scene_perspective_matrix(math.radians(42.0), width / max(height, 1), 0.05, max(8.0, distance * 6.0))
        view = _terrain_scene_look_at(eye, target, np.array([0.0, 1.0, 0.0], dtype='f4'))
        return projection @ view, eye

    def _build_3d_preview_hitboxes(self, rect, profile, yaw=None, pitch=None):
        for entry in self._get_3d_preview_overlay_entries(rect, profile, yaw=yaw, pitch=pitch):
            box = entry['box'].copy()
            box.move_ip(rect.x, rect.y)
            part = entry['part']
            component_index = entry['component_index']
            self.preview_part_hitboxes.append((part, box, component_index))

    def _draw_selected_preview_outlines(self, rect, profile, yaw=None, pitch=None):
        if self.selected_part is None:
            return
        edge_indices = (
            (0, 1), (0, 2), (0, 4), (3, 1), (3, 2), (3, 7),
            (5, 1), (5, 4), (5, 7), (6, 2), (6, 4), (6, 7),
        )
        for entry in self._get_3d_preview_overlay_entries(rect, profile, yaw=yaw, pitch=pitch):
            part = entry['part']
            component_index = entry['component_index']
            if part != self.selected_part:
                continue
            if (
                component_index is not None
                and self.selected_component_scope == 'single'
                and self._part_supports_component_selection(part)
                    and int(component_index) != int(self.selected_component_index)
            ):
                continue
            projected = entry['projected']
            for start_index, end_index in edge_indices:
                start = projected[start_index]
                end = projected[end_index]
                if start is None or end is None:
                    continue
                pygame.draw.line(
                    self.screen,
                    self.colors['accent'],
                    (rect.x + int(start[0]), rect.y + int(start[1])),
                    (rect.x + int(end[0]), rect.y + int(end[1])),
                    3,
                )

    def _preview_render_cache_key(self, profile, size, yaw, pitch):
        try:
            geometry_key = self.preview_renderer_3d._profile_geometry_key(profile) if self.preview_renderer_3d is not None else json.dumps(profile, sort_keys=True, ensure_ascii=True)
        except Exception:
            geometry_key = str(id(profile))
        return (
            geometry_key,
            int(size[0]),
            int(size[1]),
            round(float(yaw), 4),
            round(float(pitch), 4),
            round(float(self.preview_zoom), 4),
        )

    def _get_preview_surface(self, profile, size, yaw, pitch):
        if self.preview_renderer_3d is None:
            return None
        key = self._preview_render_cache_key(profile, size, yaw, pitch)
        cached = self._preview_surface_cache.get(key)
        if cached is not None:
            return cached
        surface = self.preview_renderer_3d.render_scene(profile, size, yaw=yaw, pitch=pitch, zoom=self.preview_zoom)
        if surface is not None:
            if len(self._preview_surface_cache) >= 8:
                self._preview_surface_cache.pop(next(iter(self._preview_surface_cache)))
            self._preview_surface_cache[key] = surface
        return surface

    def _get_3d_preview_overlay_entries(self, rect, profile, yaw=None, pitch=None):
        key = self._preview_render_cache_key(profile, rect.size, yaw if yaw is not None else self.preview_3d_yaw, pitch if pitch is not None else self.preview_3d_pitch)
        cached = self._preview_overlay_cache.get(key)
        if cached is not None:
            return cached
        camera = self._resolve_3d_preview_camera(rect, profile, yaw=yaw, pitch=pitch)
        if camera is None:
            return []
        mvp, eye = camera
        width, height = rect.size
        entries = []
        part_counts = {}
        for primitive in self._iter_3d_preview_primitives(profile):
            part, _center, _half_extents = primitive[:3]
            projected = []
            for corner in self._preview_3d_primitive_corners(primitive):
                point = self._project_3d_preview_point(corner, mvp, (width, height))
                projected.append(point)
            visible = [point for point in projected if point is not None]
            if not visible:
                continue
            xs = [point[0] for point in visible]
            ys = [point[1] for point in visible]
            box = pygame.Rect(int(min(xs)), int(min(ys)), max(6, int(max(xs) - min(xs))), max(6, int(max(ys) - min(ys))))
            distance_to_eye = float(np.linalg.norm(np.array(self._preview_3d_primitive_center(primitive), dtype='f4') - eye))
            component_index = self._preview_3d_primitive_component_index(primitive, part_counts)
            entries.append({
                'part': part,
                'component_index': component_index,
                'box': box,
                'projected': projected,
                'distance': distance_to_eye,
            })
        entries.sort(key=lambda item: item['distance'], reverse=True)
        if len(self._preview_overlay_cache) >= 8:
            self._preview_overlay_cache.pop(next(iter(self._preview_overlay_cache)))
        self._preview_overlay_cache[key] = entries
        return entries

    def _draw_projected_preview(self, rect, profile, *, yaw, pitch, title, hint=None, interactive=False):
        pygame.draw.rect(self.screen, self.colors['preview_bg'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        self._draw_text(title, self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        content_rect = pygame.Rect(rect.x + 10, rect.y + 44, rect.width - 20, rect.height - 56)
        preview_surface = self._get_preview_surface(profile, content_rect.size, yaw, pitch)
        pygame.draw.rect(self.screen, self.colors['preview_bg'], content_rect, border_radius=10)
        pygame.draw.rect(self.screen, self.colors['panel_border'], content_rect, 1, border_radius=10)
        if preview_surface is not None:
            self.screen.blit(preview_surface, content_rect.topleft)
            self._build_3d_preview_hitboxes(content_rect, profile, yaw=yaw, pitch=pitch)
            self._draw_selected_preview_outlines(content_rect, profile, yaw=yaw, pitch=pitch)
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
            lower_length = max(8, int(profile.get('rear_climb_assist_lower_length_m', 0.00) * scale))
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
        preview_surface = self._get_preview_surface(profile, content_rect.size, self.preview_3d_yaw, self.preview_3d_pitch)
        pygame.draw.rect(self.screen, self.colors['preview_bg'], content_rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], content_rect, 1, border_radius=12)
        if preview_surface is not None:
            self.screen.blit(preview_surface, content_rect.topleft)
            self._build_3d_preview_hitboxes(content_rect, profile, yaw=self.preview_3d_yaw, pitch=self.preview_3d_pitch)
            self._draw_selected_preview_outlines(content_rect, profile, yaw=self.preview_3d_yaw, pitch=self.preview_3d_pitch)
        else:
            title = '3D 预览不可用'
            detail = self.preview_renderer_3d.error if self.preview_renderer_3d is not None else MODERNGL_PREVIEW_ERROR
            self._draw_text(title, self.font, self.colors['text'], (content_rect.x + 18, content_rect.y + 18))
            if detail:
                self._draw_text(detail, self.small_font, self.colors['muted'], (content_rect.x + 18, content_rect.y + 52))
        hint = self.tiny_font.render('拖动鼠标旋转 3D 预览；上方可切换静态、上台阶、跳跃动作', True, self.colors['muted'])
        self.screen.blit(hint, (content_rect.x + 14, content_rect.bottom - 24))

    def _custom_tab_rects_for_panel(self, rect):
        custom_tab_x = rect.right - 228
        return [
            (part_key, pygame.Rect(custom_tab_x + index * 72, rect.y + 12, 64, 24))
            for index, part_key in enumerate(('custom_primitive', 'custom_anchor', 'custom_link'))
        ]

    def _custom_action_rects_for_panel(self, rect):
        if self.selected_part not in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            return []
        control_y = rect.y + 80
        return [
            ('custom:add', pygame.Rect(rect.x + 12, control_y, 68, 28)),
            ('custom:duplicate', pygame.Rect(rect.x + 88, control_y, 68, 28)),
            ('custom:delete', pygame.Rect(rect.x + 164, control_y, 68, 28)),
        ]

    def _select_custom_part(self, part):
        self.selected_part = part
        self.selected_component_scope = 'single'
        self.selected_field_index = 0
        self.field_scroll = 0
        self.active_numeric_input = None
        self._ensure_custom_item_for_editing(part)
        self._clamp_selected_component_index()

    def _handle_custom_control_click(self, pos, rect):
        for part_key, tab_rect in self._custom_tab_rects_for_panel(rect):
            if tab_rect.collidepoint(pos):
                self._select_custom_part(part_key)
                return True
        for action, button_rect in self._custom_action_rects_for_panel(rect):
            if button_rect.collidepoint(pos):
                self._mutate_custom_collection(self.selected_part, action.split(':', 1)[1])
                self.active_numeric_input = None
                return True
        return False

    def _custom_children_for_selected_part(self, profile):
        if self.selected_part in {None, 'custom_primitive', 'custom_anchor', 'custom_link'}:
            return []
        selected_part = str(self.selected_part)
        selected_index = int(self.selected_component_index)

        def matches_parent(item):
            if str(item.get('parent_part', 'body')) != selected_part:
                return False
            scope = str(item.get('component_scope', 'single'))
            return scope == 'all' or int(item.get('component_index', 0) or 0) == selected_index

        items = []
        anchors_by_id = {str(anchor.get('id', '')): anchor for anchor in profile.get('custom_anchors', [])}
        for index, primitive in enumerate(profile.get('custom_primitives', [])):
            if matches_parent(primitive):
                items.append(('custom_primitive', index, primitive.get('name') or primitive.get('id') or f'附加体 {index + 1}'))
        for index, anchor in enumerate(profile.get('custom_anchors', [])):
            if matches_parent(anchor):
                items.append(('custom_anchor', index, anchor.get('name') or anchor.get('id') or f'锚点 {index + 1}'))
        for index, link in enumerate(profile.get('custom_links', [])):
            start_anchor = anchors_by_id.get(str(link.get('start_anchor_id', '')))
            end_anchor = anchors_by_id.get(str(link.get('end_anchor_id', '')))
            if (isinstance(start_anchor, dict) and matches_parent(start_anchor)) or (isinstance(end_anchor, dict) and matches_parent(end_anchor)):
                items.append(('custom_link', index, link.get('name') or link.get('id') or f'连杆 {index + 1}'))
        return items

    def _draw_custom_children_panel(self, rect, child_items):
        if not child_items:
            return
        pygame.draw.rect(self.screen, self.colors['panel_alt'], rect, border_radius=8)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=8)
        self._draw_text('子部件', self.tiny_font, self.colors['muted'], (rect.x + 10, rect.y + 7))
        x = rect.x + 10
        y = rect.y + 28
        for part_key, index, label in child_items[:6]:
            button_rect = pygame.Rect(x, y, min(132, rect.right - x - 8), 24)
            pygame.draw.rect(self.screen, self.colors['field_row'], button_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['panel_border'], button_rect, 1, border_radius=6)
            text = f'{PART_LABELS.get(part_key, part_key)} {index + 1}'
            rendered = self.tiny_font.render(text, True, self.colors['text'])
            self.screen.blit(rendered, rendered.get_rect(midleft=(button_rect.x + 7, button_rect.centery)))
            self.custom_collection_actions.append((button_rect, f'select_item:{part_key}:{index}'))
            x = button_rect.right + 8
            if x + 96 > rect.right:
                x = rect.x + 10
                y += 28
                if y + 24 > rect.bottom - 4:
                    break

    def _draw_fields_panel(self, rect):
        pygame.draw.rect(self.screen, self.colors['panel'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        title = f'{PART_LABELS.get(self.selected_part, "可调")}参数' if self.selected_part is not None else '选择部件'
        self._draw_text(title, self.font, self.colors['text'], (rect.x + 14, rect.y + 12))
        self.component_control_actions = []
        self.custom_collection_actions = []
        self.color_palette_actions = []
        custom_tab_x = rect.right - 228
        for index, (part_key, label) in enumerate((('custom_primitive', '附加体'), ('custom_anchor', '锚点'), ('custom_link', '连杆'))):
            tab_rect = pygame.Rect(custom_tab_x + index * 72, rect.y + 12, 64, 24)
            active = self.selected_part == part_key
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_alt'], tab_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['panel_border'], tab_rect, 1, border_radius=6)
            rendered = self.tiny_font.render(label, True, (20, 22, 24) if active else self.colors['text'])
            self.screen.blit(rendered, rendered.get_rect(center=tab_rect.center))
            self.custom_collection_actions.append((tab_rect, f'select:{part_key}'))
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
            unit_text = '全部' if self.selected_component_scope == 'all' else f'单体位 {self.selected_component_index + 1}/{max(1, count)}'
            rendered = self.small_font.render(unit_text, True, self.colors['text'])
            self.screen.blit(rendered, rendered.get_rect(center=label_rect.center))
            for action, button_rect, label in (
                ('component_cycle:-1', prev_rect, '<'),
                ('component_cycle:1', next_rect, '>'),
            ):
                enabled = self.selected_component_scope != 'all' and count > 1
                pygame.draw.rect(self.screen, self.colors['panel_alt'] if enabled else (220, 225, 232), button_rect, border_radius=7)
                pygame.draw.rect(self.screen, self.colors['panel_border'], button_rect, 1, border_radius=7)
                rendered = self.small_font.render(label, True, self.colors['text'] if enabled else self.colors['muted'])
                self.screen.blit(rendered, rendered.get_rect(center=button_rect.center))
                if enabled:
                    self.component_control_actions.append((button_rect, action))
        if self.selected_part in {'custom_primitive', 'custom_anchor', 'custom_link'}:
            control_y = rect.y + 80
            for action, x_offset, label in (
                ('custom:add', 12, '+ 新建'),
                ('custom:duplicate', 88, '复制'),
                ('custom:delete', 164, '删除'),
            ):
                button_rect = pygame.Rect(rect.x + x_offset, control_y, 68, 28)
                pygame.draw.rect(self.screen, self.colors['panel_alt'], button_rect, border_radius=7)
                pygame.draw.rect(self.screen, self.colors['panel_border'], button_rect, 1, border_radius=7)
                rendered = self.small_font.render(label, True, self.colors['text'])
                self.screen.blit(rendered, rendered.get_rect(center=button_rect.center))
                self.custom_collection_actions.append((button_rect, action))
        visible_fields = self._visible_field_specs()
        active_color_spec = None
        if visible_fields:
            active_index = max(0, min(self.selected_field_index, len(visible_fields) - 1))
            candidate_spec = visible_fields[active_index]
            if candidate_spec['kind'] in {'color', 'custom_color'}:
                active_color_spec = candidate_spec
        content_top_inset = self._field_content_top_inset()
        palette_reserved_height = 62 if active_color_spec is not None else 0
        profile_for_children = self._current_profile()
        child_items = self._custom_children_for_selected_part(profile_for_children)
        child_reserved_height = 86 if child_items else 0
        content_rect = pygame.Rect(rect.x + 8, rect.y + content_top_inset, rect.width - 20, rect.height - content_top_inset - 12 - palette_reserved_height - child_reserved_height)
        pygame.draw.rect(self.screen, self.colors['panel_alt'], content_rect, border_radius=8)
        if self.selected_part is None:
            hint_lines = [
                '在右侧预览中点击部件后，这里会显示对应的长宽高、偏移和颜色参数。',
                '当前可选：底盘、车轮、前爬升板、后腿机构、云台、枪管、连接件、装甲板、灯条、摩擦轮和相机。',
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
            pygame.draw.rect(self.screen, self.colors['field_row_active'] if active else self.colors['field_row'], row_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['accent'] if active else self.colors['panel_border'], row_rect, 1, border_radius=6)
            value = self._field_value(spec)
            if isinstance(self.active_numeric_input, dict) and int(self.active_numeric_input.get('field_index', -1)) == field_index:
                value_text = str(self.active_numeric_input.get('buffer', ''))
                value_color = self.colors['accent']
            else:
                if spec['kind'] == 'choice':
                    options = self._custom_choice_options(spec)
                    option_index = max(0, min(int(value), len(options) - 1)) if options else 0
                    value_text = options[option_index][1] if options else '-'
                else:
                    value_text = f'{value:.3f}' if spec['kind'] not in {'color', 'custom_color'} else f'{int(value)}'
                value_color = self.colors['value']
            self._draw_text(spec['label'], self.small_font, self.colors['text'], (row_rect.x + 10, row_rect.y + 5))
            value_surface = self.small_font.render(value_text, True, value_color)
            self.screen.blit(value_surface, value_surface.get_rect(right=row_rect.right - 10, centery=row_rect.centery))
        self.screen.set_clip(old_clip)

        if child_items:
            child_rect = pygame.Rect(content_rect.x, content_rect.bottom + 8, content_rect.width, max(0, child_reserved_height - 10))
            self._draw_custom_children_panel(child_rect, child_items)

        if active_color_spec is not None:
            palette_x = rect.x + 16
            palette_y = rect.bottom - 58
            swatch_size = 20
            swatch_gap = 6
            preview_size = 44
            active_color = []
            for channel_index in range(3):
                spec_copy = dict(active_color_spec)
                spec_copy['channel'] = channel_index
                active_color.append(max(0, min(255, int(round(float(self._field_value(spec_copy)))))))
            preview_rect = pygame.Rect(palette_x, palette_y + 1, preview_size, preview_size)
            pygame.draw.rect(self.screen, tuple(active_color), preview_rect, border_radius=7)
            pygame.draw.rect(self.screen, self.colors['panel_border'], preview_rect, 1, border_radius=7)
            swatch_start_x = palette_x + preview_size + 12
            available_width = max(swatch_size, rect.right - swatch_start_x - 16)
            max_columns = max(1, min(len(COLOR_SWATCHES), (available_width + swatch_gap) // (swatch_size + swatch_gap)))
            for swatch_index, color_rgb in enumerate(COLOR_SWATCHES):
                swatch_col = swatch_index % max_columns
                swatch_row = swatch_index // max_columns
                swatch_rect = pygame.Rect(
                    swatch_start_x + swatch_col * (swatch_size + swatch_gap),
                    palette_y + swatch_row * (swatch_size + swatch_gap),
                    swatch_size,
                    swatch_size,
                )
                pygame.draw.rect(self.screen, tuple(color_rgb), swatch_rect, border_radius=5)
                pygame.draw.rect(self.screen, self.colors['panel_border'], swatch_rect, 1, border_radius=5)
                if list(color_rgb) == active_color:
                    pygame.draw.rect(self.screen, self.colors['accent'], swatch_rect.inflate(4, 4), 2, border_radius=7)
                self.color_palette_actions.append((swatch_rect, tuple(color_rgb)))

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
        self._draw_text('车辆外观编辑器', self.title_font, self.colors['text'], (28, 22))
        self._draw_text('保存后的预设会在后续创建单位时自动应用', self.small_font, self.colors['muted'], (30, 52))
        self.runtime_preview_buttons = self._runtime_preview_button_rects()
        for action, label, rect in self.runtime_preview_buttons:
            enabled = action == 'launch' or self.current_role in {'base', 'outpost', 'energy_mechanism'}
            fill = self.colors['accent'] if action == 'launch' and enabled else (self.colors['panel_alt'] if enabled else (220, 225, 232))
            pygame.draw.rect(self.screen, fill, rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=8)
            text_color = (20, 22, 24) if action == 'launch' and enabled else (self.colors['text'] if enabled else self.colors['muted'])
            text_surface = self.small_font.render(label, True, text_color)
            self.screen.blit(text_surface, text_surface.get_rect(center=rect.center))
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
        if isinstance(self.active_numeric_input, dict):
            field_panel_for_commit, _ = self._layout_panels()
            rows_for_commit, _ = self._field_rows(field_panel_for_commit, scroll_offset=self.field_scroll)
            clicked_active_field = any(
                row_type == 'field'
                and field_index == int(self.active_numeric_input.get('field_index', -1))
                and row_rect.collidepoint(pos)
                for row_type, _, row_rect, field_index in rows_for_commit)
            if not clicked_active_field:
                self._commit_numeric_input()
        for action, _, rect in self.runtime_preview_buttons:
            if rect.collidepoint(pos):
                self._launch_runtime_preview(refresh=action == 'refresh')
                self.active_numeric_input = None
                return
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
                self._apply_role_preview_defaults(role_key)
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
        field_panel, _ = self._layout_panels()
        if self._handle_custom_control_click(pos, field_panel):
            return
        for action_rect, action in self.component_control_actions:
            if action_rect.collidepoint(pos):
                if action.startswith('component_scope:'):
                    self.selected_component_scope = action.split(':', 1)[1]
                elif action.startswith('component_cycle:'):
                    self._change_selected_component(int(action.split(':', 1)[1]))
                self.active_numeric_input = None
                return
        for action_rect, action in self.custom_collection_actions:
            if action_rect.collidepoint(pos):
                if action.startswith('select_item:'):
                    _, part_key, index_text = action.split(':', 2)
                    self._select_custom_part(part_key)
                    self.selected_component_index = max(0, int(index_text))
                    self.active_numeric_input = None
                    return
                verb, payload = action.split(':', 1)
                if verb == 'select':
                    self._select_custom_part(payload)
                    return
                self._mutate_custom_collection(self.selected_part, payload)
                self.active_numeric_input = None
                return
        for swatch_rect, color_rgb in self.color_palette_actions:
            if swatch_rect.collidepoint(pos):
                visible_fields = self._visible_field_specs()
                if visible_fields:
                    active_spec = visible_fields[max(0, min(self.selected_field_index, len(visible_fields) - 1))]
                    if active_spec['kind'] in {'color', 'custom_color'}:
                        for channel_index, channel_value in enumerate(color_rgb):
                            spec_copy = dict(active_spec)
                            spec_copy['channel'] = channel_index
                            self._set_field_value(spec_copy, channel_value)
                self.active_numeric_input = None
                return
        if self.field_scrollbar_thumb_rect is not None and self.field_scrollbar_thumb_rect.collidepoint(pos):
            self.field_scroll_drag_active = True
            return
        if self.field_scrollbar_track_rect is not None and self.field_scrollbar_track_rect.collidepoint(pos):
            thumb_height = self.field_scrollbar_thumb_rect.height if self.field_scrollbar_thumb_rect is not None else 0
            relative = pos[1] - self.field_scrollbar_track_rect.y - thumb_height * 0.5
            ratio = relative / max(1, self.field_scrollbar_track_rect.height - thumb_height)
            self._set_field_scroll(field_panel, ratio * self._max_field_scroll(field_panel))
            return
        hit_candidates = []
        part_priority = {
            'barrel_friction_wheel': 0,
            'barrel_light': 1,
            'first_person_camera': 1,
            'armor_light': 2,
            'barrel': 3,
            'armor': 4,
            'turret': 5,
            'mount': 6,
            'body': 7,
        }
        for depth_order, item in enumerate(reversed(self.preview_part_hitboxes)):
            part = item[0]
            hitbox = item[1]
            component_index = item[2] if len(item) >= 3 else None
            if hitbox.collidepoint(pos):
                hit_candidates.append((part_priority.get(part, 10), hitbox.width * hitbox.height, depth_order, part, component_index))
        if hit_candidates:
            _, _, _, part, component_index = sorted(hit_candidates, key=lambda item: (item[0], item[1], item[2]))[0]
            self.selected_part = part
            self.selected_field_index = 0
            self.field_scroll = 0
            if component_index is not None and self._part_supports_component_selection(part):
                self.selected_component_scope = 'single'
                self.selected_component_index = int(component_index)
            else:
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
                self._handle_field_row_click(field_panel, field_index)
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
            mouse_pos = pygame.mouse.get_pos()
            if self.preview_content_rect is not None and self.preview_content_rect.collidepoint(mouse_pos):
                self._adjust_preview_zoom(event.y)
                return
            if self.field_panel_rect is not None and self.field_panel_rect.collidepoint(mouse_pos):
                self._set_field_scroll(self.field_panel_rect, self.field_scroll - event.y * 36)
                return
            self._adjust_preview_zoom(event.y)
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
            self._apply_role_preview_defaults(self.current_role)
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
        if event.key in {pygame.K_RETURN, pygame.K_KP_ENTER}:
            if self._begin_field_editor(self.selected_field_index):
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
                self._apply_role_preview_defaults(self.current_role)

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
        self._cleanup_runtime_preview_process()
        pygame.quit()


def main():
    app = AppearanceEditorApp()
    app.run()


if __name__ == '__main__':
    main()
