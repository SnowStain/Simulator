#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import os
from copy import deepcopy

from simulator3d.native_bridge import get_native_runtime_status


_RENDERER_BACKEND_BY_MODE = {
    'gpu': 'native_cpp',
    'opengl': 'editor_opengl',
    'moderngl': 'pyglet_moderngl',
    'native_cpp': 'native_cpp',
}
_MODE_BY_RENDERER_BACKEND = {
    'editor_opengl': 'opengl',
    'opengl_grid': 'opengl',
    'terrain_editor_opengl': 'opengl',
    'terrain_editor_gl': 'opengl',
    'moderngl': 'moderngl',
    'pyglet_moderngl': 'moderngl',
    'pyglet-moderngl': 'moderngl',
    'gpu': 'gpu',
    'wgl_opengl': 'gpu',
    'opengl_gpu': 'gpu',
    'native_cpp': 'native_cpp',
    'cpp': 'native_cpp',
    'opengl_cpp': 'native_cpp',
    'rm26_native': 'native_cpp',
}


def _normalize_sim3d_renderer_mode(selected):
    mode = str(selected or '').strip().lower()
    if mode in _RENDERER_BACKEND_BY_MODE:
        return mode
    return _MODE_BY_RENDERER_BACKEND.get(mode, 'moderngl')


def _backend_for_sim3d_mode(mode):
    return _RENDERER_BACKEND_BY_MODE.get(_normalize_sim3d_renderer_mode(mode), 'pyglet_moderngl')


def _runtime_grid_channels_valid(config_manager, preset_name, config_path):
    preset_map = config_manager.load_map_preset(preset_name, config_path)
    if not isinstance(preset_map, dict):
        return False
    runtime_grid = preset_map.get('runtime_grid', {}) if isinstance(preset_map.get('runtime_grid', {}), dict) else {}
    channels = runtime_grid.get('channels', {}) if isinstance(runtime_grid, dict) else {}
    if not channels:
        return False
    preset_path = preset_map.get('_preset_path')
    preset_dir = os.path.dirname(os.path.abspath(preset_path)) if preset_path else os.path.dirname(os.path.abspath(config_path))
    for channel_path in channels.values():
        if not channel_path:
            return False
        resolved_path = str(channel_path)
        if not os.path.isabs(resolved_path):
            resolved_path = os.path.join(preset_dir, resolved_path)
        if not os.path.exists(resolved_path):
            return False
    return True


def _resolve_sim3d_map_preset(config_manager, config_path, base_config):
    presets = config_manager.list_map_presets(config_path)
    if not presets:
        return str(base_config.get('map', {}).get('preset') or 'basicMap')
    simulator = base_config.get('simulator', {}) if isinstance(base_config, dict) else {}
    explicit_choice = str(simulator.get('sim3d_map_preset') or '').strip()
    configured_map = str(base_config.get('map', {}).get('preset') or '').strip()
    candidates = []
    if explicit_choice:
        candidates.append(explicit_choice)
    if configured_map and configured_map not in {'blankCanvas'}:
        candidates.append(configured_map)
    if 'basicMap' in presets:
        candidates.append('basicMap')
    candidates.extend(presets)
    seen = set()
    for candidate in candidates:
        if not candidate or candidate in seen or candidate not in presets:
            continue
        seen.add(candidate)
        if _runtime_grid_channels_valid(config_manager, candidate, config_path):
            return candidate
    return 'basicMap' if 'basicMap' in presets else presets[0]


def build_simulator3d_config(base_config, config_path='config.json', renderer_mode=None, map_preset=None):
    config = deepcopy(base_config)
    config['_config_path'] = config_path
    simulator = config.setdefault('simulator', {})
    requested_mode = renderer_mode or simulator.get('sim3d_renderer_backend') or simulator.get('terrain_scene_backend')
    resolved_mode = _normalize_sim3d_renderer_mode(requested_mode)
    if not str(requested_mode or '').strip():
        native_status = get_native_runtime_status(config)
        if native_status.renderer_ready():
            resolved_mode = 'native_cpp'
    simulator['sim3d_renderer_backend'] = resolved_mode
    simulator['terrain_scene_backend'] = _backend_for_sim3d_mode(resolved_mode)
    simulator['player_projectile_ricochet_enabled'] = bool(simulator.get('player_projectile_ricochet_enabled', True))
    simulator['terrain_scene_max_cells'] = int(max(22000, simulator.get('terrain_scene_max_cells', 42000)))
    simulator['player_terrain_scene_max_cells'] = int(max(7000, simulator.get('player_terrain_scene_max_cells', 10000)))
    simulator['player_terrain_render_scale'] = float(max(0.46, min(0.78, simulator.get('player_terrain_render_scale', 0.60))))
    simulator['player_motion_terrain_render_scale'] = float(max(0.34, min(simulator['player_terrain_render_scale'], simulator.get('player_motion_terrain_render_scale', 0.40))))
    simulator['player_camera_motion_threshold_m'] = float(max(0.008, simulator.get('player_camera_motion_threshold_m', 0.015)))
    simulator['player_terrain_precise_rendering'] = bool(simulator.get('player_terrain_precise_rendering', False))
    simulator['manual_control_ai_scope'] = 'combat_units'
    simulator['manual_control_dispatch_profile'] = 'exclusive_combat'
    simulator['require_pre_match_setup'] = True
    simulator['runtime_entrypoint'] = 'simulator3d'
    selected_entity_id = str(simulator.get('sim3d_selected_entity_id') or simulator.get('standalone_3d_selected_entity_id') or 'red_robot_1')
    simulator['sim3d_selected_entity_id'] = selected_entity_id
    if 'sim3d_selected_team' not in simulator:
        simulator['sim3d_selected_team'] = 'blue' if selected_entity_id.startswith('blue_') else 'red'
    if map_preset:
        simulator['sim3d_map_preset'] = str(map_preset)
        config.setdefault('map', {})['preset'] = str(map_preset)
    physics = config.setdefault('physics', {})
    physics['backend'] = 'pybullet'
    physics['infantry_jump_height_m'] = float(physics.get('infantry_jump_height_m', 0.40))
    physics['infantry_jump_launch_velocity_mps'] = float(physics.get('infantry_jump_launch_velocity_mps', 4.0))
    physics['jump_gravity_mps2'] = float(physics.get('jump_gravity_mps2', 20.0))
    native_backend = config.setdefault('native_backend', {})
    native_backend['module_name'] = str(native_backend.get('module_name') or 'rm26_native')
    native_backend['prefer_renderer'] = bool(native_backend.get('prefer_renderer', True))
    native_backend['prefer_physics'] = bool(native_backend.get('prefer_physics', True))
    native_backend['require_renderer'] = bool(native_backend.get('require_renderer', False))
    native_backend['require_physics'] = bool(native_backend.get('require_physics', False))
    native_backend['renderer_required_feature_level'] = max(1, int(native_backend.get('renderer_required_feature_level', 4) or 4))
    native_backend['physics_required_feature_level'] = max(1, int(native_backend.get('physics_required_feature_level', 4) or 4))
    native_backend['build_dir'] = str(native_backend.get('build_dir') or 'build/native')
    return config


def load_simulator3d_config(config_manager, config_path='config.json'):
    base_config = config_manager.load_config(config_path)
    renderer_mode = _normalize_sim3d_renderer_mode(
        base_config.get('simulator', {}).get('sim3d_renderer_backend') or base_config.get('simulator', {}).get('terrain_scene_backend')
    )
    selected_map = _resolve_sim3d_map_preset(config_manager, config_path, base_config)
    selected_preset = config_manager.load_map_preset(selected_map, config_path)
    if isinstance(selected_preset, dict):
        merged_map = config_manager._deep_merge(
            base_config.get('map', {}),
            {key: value for key, value in selected_preset.items() if key != '_preset_path'},
        )
        merged_map['preset'] = selected_map
        if selected_preset.get('_preset_path'):
            merged_map['_preset_path'] = selected_preset.get('_preset_path')
        base_config = deepcopy(base_config)
        base_config['map'] = merged_map
    return build_simulator3d_config(base_config, config_path=config_path, renderer_mode=renderer_mode, map_preset=selected_map)
