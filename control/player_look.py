#!/usr/bin/env python3
# -*- coding: utf-8 -*-


def _simulator_config(config):
    if not isinstance(config, dict):
        return {}
    simulator_cfg = config.get('simulator', {})
    return simulator_cfg if isinstance(simulator_cfg, dict) else {}


def get_player_mouse_input_settings(config):
    simulator_cfg = _simulator_config(config)
    yaw_sensitivity = float(
        simulator_cfg.get(
            'player_mouse_yaw_sensitivity_deg',
            simulator_cfg.get('player_yaw_sensitivity_deg', 0.08),
        )
    )
    pitch_sensitivity = float(
        simulator_cfg.get(
            'player_mouse_pitch_sensitivity_deg',
            simulator_cfg.get('player_pitch_sensitivity_deg', 0.08),
        )
    )
    return {
        'yaw_sensitivity_deg': yaw_sensitivity,
        'pitch_sensitivity_deg': pitch_sensitivity,
    }


def set_player_mouse_input_settings(config, yaw_sensitivity_deg=None, pitch_sensitivity_deg=None):
    if not isinstance(config, dict):
        config = {}
    simulator_cfg = config.setdefault('simulator', {})
    if yaw_sensitivity_deg is not None:
        simulator_cfg['player_mouse_yaw_sensitivity_deg'] = float(yaw_sensitivity_deg)
    if pitch_sensitivity_deg is not None:
        simulator_cfg['player_mouse_pitch_sensitivity_deg'] = float(pitch_sensitivity_deg)
    return get_player_mouse_input_settings(config)


def scale_player_mouse_motion(config, delta_x, delta_y):
    settings = get_player_mouse_input_settings(config)
    yaw_delta_deg = float(delta_x) * float(settings['yaw_sensitivity_deg'])
    pitch_delta_deg = -float(delta_y) * float(settings['pitch_sensitivity_deg'])
    return yaw_delta_deg, pitch_delta_deg


def clamp_entity_pitch(entity, desired_pitch, config=None):
    max_up = float(getattr(entity, 'max_pitch_up_deg', 30.0) or 30.0)
    max_down = float(getattr(entity, 'max_pitch_down_deg', 30.0) or 30.0)
    return max(-max_down, min(max_up, float(desired_pitch)))
