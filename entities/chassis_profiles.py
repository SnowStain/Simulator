#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from copy import deepcopy


INFANTRY_CHASSIS_SUBTYPE_DEFAULT = 'balance_legged'

INFANTRY_CHASSIS_SUBTYPE_OPTIONS = (
    ('balance_legged', '平衡串联腿步兵'),
    ('omni_wheel', '全向轮步兵'),
)

INFANTRY_CHASSIS_SUBTYPE_LABELS = dict(INFANTRY_CHASSIS_SUBTYPE_OPTIONS)


_INFANTRY_CHASSIS_PRESETS = {
    'balance_legged': {
        'chassis_subtype': 'balance_legged',
        'body_shape': 'box',
        'wheel_style': 'legged',
        'suspension_style': 'four_bar',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'balance_leg',
        'body_length_m': 0.49,
        'body_width_m': 0.42,
        'body_height_m': 0.16,
        'body_clearance_m': 0.16,
        'wheel_radius_m': 0.06,
        'body_render_width_scale': 0.73,
        'wheel_count': 2,
        'custom_wheel_positions_m': [[0.0, -0.245], [0.0, 0.245]],
        'rear_climb_assist_upper_length_m': 0.24,
        'rear_climb_assist_lower_length_m': 0.42,
        'rear_climb_assist_upper_width_m': 0.024,
        'rear_climb_assist_upper_height_m': 0.032,
        'rear_climb_assist_lower_width_m': 0.022,
        'rear_climb_assist_lower_height_m': 0.028,
        'rear_climb_assist_mount_offset_x_m': 0.18,
        'rear_climb_assist_mount_height_m': 0.245,
        'rear_climb_assist_inner_offset_m': 0.03,
        'rear_climb_assist_upper_pair_gap_m': 0.07,
        'rear_climb_assist_hinge_radius_m': 0.018,
        'armor_orbit_yaws_deg': [0.0, 180.0, 90.0, 270.0],
        'armor_self_yaws_deg': [0.0, 180.0, 90.0, 270.0],
        'armor_light_orbit_yaws_deg': [0.0, 180.0, 90.0, 270.0],
        'armor_light_self_yaws_deg': [0.0, 180.0, 90.0, 270.0],
        'wheel_orbit_yaws_deg': [0.0, 0.0],
        'wheel_self_yaws_deg': [0.0, 0.0],
        'chassis_supports_jump': True,
        'chassis_speed_scale': 1.00,
        'chassis_drive_power_limit_w': 150.0,
        'chassis_drive_idle_draw_w': 16.0,
        'chassis_drive_rpm_coeff': 0.000050,
        'chassis_drive_accel_coeff': 0.013,
    },
    'omni_wheel': {
        'chassis_subtype': 'omni_wheel',
        'body_shape': 'octagon',
        'wheel_style': 'omni',
        'suspension_style': 'none',
        'front_climb_assist_style': 'none',
        'rear_climb_assist_style': 'none',
        'body_length_m': 0.52,
        'body_width_m': 0.52,
        'body_height_m': 0.16,
        'body_clearance_m': 0.09,
        'wheel_radius_m': 0.065,
        'body_render_width_scale': 0.94,
        'wheel_count': 4,
        'custom_wheel_positions_m': [[0.185, 0.0], [0.0, 0.185], [-0.185, 0.0], [0.0, -0.185]],
        'armor_orbit_yaws_deg': [45.0, 135.0, 225.0, 315.0],
        'armor_self_yaws_deg': [45.0, 135.0, 225.0, 315.0],
        'armor_light_orbit_yaws_deg': [45.0, 135.0, 225.0, 315.0],
        'armor_light_self_yaws_deg': [45.0, 135.0, 225.0, 315.0],
        'wheel_orbit_yaws_deg': [0.0, 90.0, 180.0, 270.0],
        'wheel_self_yaws_deg': [0.0, 90.0, 0.0, 90.0],
        'chassis_supports_jump': False,
        'chassis_speed_scale': 1.08,
        'chassis_drive_power_limit_w': 210.0,
        'chassis_drive_idle_draw_w': 20.0,
        'chassis_drive_rpm_coeff': 0.000036,
        'chassis_drive_accel_coeff': 0.010,
    },
}


def normalize_infantry_chassis_subtype(value):
    normalized = str(value or INFANTRY_CHASSIS_SUBTYPE_DEFAULT).strip().lower()
    if normalized in _INFANTRY_CHASSIS_PRESETS:
        return normalized
    aliases = {
        'balance': 'balance_legged',
        'balanced_legged': 'balance_legged',
        'legged': 'balance_legged',
        'omni': 'omni_wheel',
        'omniwheel': 'omni_wheel',
        'omni_wheeled': 'omni_wheel',
    }
    return aliases.get(normalized, INFANTRY_CHASSIS_SUBTYPE_DEFAULT)


def infantry_chassis_options():
    return tuple(INFANTRY_CHASSIS_SUBTYPE_OPTIONS)


def infantry_chassis_label(value):
    subtype = normalize_infantry_chassis_subtype(value)
    return INFANTRY_CHASSIS_SUBTYPE_LABELS.get(subtype, INFANTRY_CHASSIS_SUBTYPE_LABELS[INFANTRY_CHASSIS_SUBTYPE_DEFAULT])


def infantry_chassis_preset(value):
    subtype = normalize_infantry_chassis_subtype(value)
    return deepcopy(_INFANTRY_CHASSIS_PRESETS[subtype])


def _component_count(profile, part):
    if part == 'wheel':
        wheel_positions = profile.get('custom_wheel_positions_m', ())
        if isinstance(wheel_positions, (list, tuple)) and wheel_positions:
            return len(wheel_positions)
        return int(profile.get('wheel_count', 4))
    if part in {'armor', 'armor_light'}:
        return 4
    return 0


def _default_component_angles(subtype, part):
    preset = _INFANTRY_CHASSIS_PRESETS[normalize_infantry_chassis_subtype(subtype)]
    if part == 'wheel':
        return preset['wheel_orbit_yaws_deg'], preset['wheel_self_yaws_deg']
    if part == 'armor':
        return preset['armor_orbit_yaws_deg'], preset['armor_self_yaws_deg']
    if part == 'armor_light':
        return preset['armor_light_orbit_yaws_deg'], preset['armor_light_self_yaws_deg']
    return (), ()


def normalize_component_angle_list(values, count, defaults):
    if not isinstance(values, (list, tuple)):
        values = []
    normalized = []
    for index in range(max(0, int(count))):
        try:
            normalized.append(round(float(values[index]), 3))
        except Exception:
            fallback = defaults[index] if index < len(defaults) else (defaults[-1] if defaults else 0.0)
            normalized.append(round(float(fallback), 3))
    return normalized


def normalize_infantry_component_profile(profile, subtype=None):
    normalized = deepcopy(profile) if isinstance(profile, dict) else {}
    resolved_subtype = normalize_infantry_chassis_subtype(subtype or normalized.get('chassis_subtype'))
    preset = infantry_chassis_preset(resolved_subtype)
    preset_wheel_positions = [
        [round(float(position[0]), 3), round(float(position[1]), 3)]
        for position in preset.get('custom_wheel_positions_m', [])
        if isinstance(position, (list, tuple)) and len(position) >= 2
    ]
    expected_wheel_count = len(preset_wheel_positions) or int(preset.get('wheel_count', 4))
    normalized['chassis_subtype'] = resolved_subtype
    for key, value in preset.items():
        if key in {
            'body_shape',
            'wheel_style',
            'suspension_style',
            'front_climb_assist_style',
            'rear_climb_assist_style',
            'wheel_count',
            'chassis_supports_jump',
            'chassis_speed_scale',
            'chassis_drive_power_limit_w',
            'chassis_drive_idle_draw_w',
            'chassis_drive_rpm_coeff',
            'chassis_drive_accel_coeff',
        }:
            normalized[key] = deepcopy(value)
        else:
            normalized.setdefault(key, deepcopy(value))

    wheel_positions = normalized.get('custom_wheel_positions_m')
    if not isinstance(wheel_positions, list) or len(wheel_positions) != expected_wheel_count:
        normalized['custom_wheel_positions_m'] = deepcopy(preset_wheel_positions)
    else:
        normalized['custom_wheel_positions_m'] = [
            [round(float(position[0]), 3), round(float(position[1]), 3)]
            for position in wheel_positions
            if isinstance(position, (list, tuple)) and len(position) >= 2
        ]
        if len(normalized['custom_wheel_positions_m']) != expected_wheel_count:
            normalized['custom_wheel_positions_m'] = deepcopy(preset_wheel_positions)

    for part, orbit_key, self_key in (
        ('wheel', 'wheel_orbit_yaws_deg', 'wheel_self_yaws_deg'),
        ('armor', 'armor_orbit_yaws_deg', 'armor_self_yaws_deg'),
        ('armor_light', 'armor_light_orbit_yaws_deg', 'armor_light_self_yaws_deg'),
    ):
        count = _component_count(normalized, part)
        default_orbit, default_self = _default_component_angles(resolved_subtype, part)
        normalized[orbit_key] = normalize_component_angle_list(normalized.get(orbit_key), count, default_orbit)
        normalized[self_key] = normalize_component_angle_list(normalized.get(self_key), count, default_self)

    normalized['wheel_count'] = int(expected_wheel_count)
    normalized['body_shape'] = str(normalized.get('body_shape', preset.get('body_shape', 'box')))
    normalized['wheel_style'] = str(normalized.get('wheel_style', preset.get('wheel_style', 'standard')))
    normalized['suspension_style'] = str(normalized.get('suspension_style', preset.get('suspension_style', 'none')))
    normalized['front_climb_assist_style'] = str(normalized.get('front_climb_assist_style', preset.get('front_climb_assist_style', 'none')))
    normalized['rear_climb_assist_style'] = str(normalized.get('rear_climb_assist_style', preset.get('rear_climb_assist_style', 'none')))
    normalized['chassis_supports_jump'] = bool(normalized.get('chassis_supports_jump', preset.get('chassis_supports_jump', True)))
    for numeric_key in (
        'body_render_width_scale',
        'chassis_speed_scale',
        'chassis_drive_power_limit_w',
        'chassis_drive_idle_draw_w',
        'chassis_drive_rpm_coeff',
        'chassis_drive_accel_coeff',
    ):
        normalized[numeric_key] = float(normalized.get(numeric_key, preset.get(numeric_key, 0.0)))
    return normalized


def resolve_infantry_subtype_profile(profile, subtype=None):
    base = deepcopy(profile) if isinstance(profile, dict) else {}
    resolved_subtype = normalize_infantry_chassis_subtype(subtype or base.get('chassis_subtype') or base.get('default_chassis_subtype'))
    subtype_profiles = base.get('subtype_profiles', {}) if isinstance(base.get('subtype_profiles'), dict) else {}
    base.pop('subtype_profiles', None)
    base.pop('default_chassis_subtype', None)
    if isinstance(subtype_profiles.get(resolved_subtype), dict):
        base.update(deepcopy(subtype_profiles[resolved_subtype]))
    base['chassis_subtype'] = resolved_subtype
    return normalize_infantry_component_profile(base, resolved_subtype)


def build_infantry_profile_payload(subtype_profiles, default_subtype):
    resolved_default = normalize_infantry_chassis_subtype(default_subtype)
    serialized_subprofiles = {}
    for subtype, _label in INFANTRY_CHASSIS_SUBTYPE_OPTIONS:
        source = subtype_profiles.get(subtype, {}) if isinstance(subtype_profiles, dict) else {}
        serialized_subprofiles[subtype] = normalize_infantry_component_profile(source, subtype)
    payload = deepcopy(serialized_subprofiles[resolved_default])
    payload['default_chassis_subtype'] = resolved_default
    payload['subtype_profiles'] = serialized_subprofiles
    return payload
