#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
from pathlib import Path

import numpy as np

from pygame_compat import pygame
from simulator3d.native_bridge import create_native_renderer_bridge, get_native_runtime_status, minimum_renderer_feature_level


class NativeCppTerrainSceneBackend:
    name = 'native_cpp'
    composites_player_scene = True

    def __init__(self, config):
        self.config = config or {}
        self.runtime_status = get_native_runtime_status(self.config)
        self.bridge = create_native_renderer_bridge(self.config)
        if self.bridge is None:
            required_level = minimum_renderer_feature_level(self.config)
            if self.runtime_status.available:
                reason = f'feature level {self.runtime_status.renderer_feature_level} < required {required_level}'
            else:
                reason = self.runtime_status.reason or 'native renderer bridge unavailable'
            raise RuntimeError(reason)
        build_info = self.runtime_status.build_info or {}
        self.status_label = f'native_cpp | {build_info.get("renderer_backend", "unknown")}'
        simulator_cfg = self.config.get('simulator', {}) if isinstance(self.config, dict) else {}
        self.fast_visibility = bool(simulator_cfg.get('native_cpp_fast_visibility', True))
        self.max_entity_distance_m = max(8.0, float(simulator_cfg.get('native_cpp_max_entity_distance_m', 42.0)))
        self.third_person_full_detail_m = max(2.0, float(simulator_cfg.get('native_cpp_third_person_full_detail_m', 10.5)))
        self.third_person_medium_detail_m = max(self.third_person_full_detail_m, float(simulator_cfg.get('native_cpp_third_person_medium_detail_m', 26.0)))
        self.first_person_full_detail_m = max(2.0, float(simulator_cfg.get('native_cpp_first_person_full_detail_m', 8.0)))
        self.first_person_medium_detail_m = max(self.first_person_full_detail_m, float(simulator_cfg.get('native_cpp_first_person_medium_detail_m', 22.0)))
        min_detail_mode = str(simulator_cfg.get('native_cpp_min_detail_mode', 'medium') or 'medium').strip().lower()
        self.min_detail_mode = min_detail_mode if min_detail_mode in {'proxy', 'medium'} else 'medium'

    def _color_rgba(self, values, fallback, alpha=236):
        source = values if isinstance(values, (list, tuple)) and len(values) >= 3 else fallback
        rgba = list(source[:4]) if isinstance(source, (list, tuple)) else list(fallback[:4])
        while len(rgba) < 4:
            rgba.append(alpha if len(rgba) == 3 else 0)
        return tuple(max(0, min(255, int(round(float(channel))))) for channel in rgba[:4])

    def _entity_distance_m(self, renderer, map_manager, camera_state, controlled, entity):
        world_eye = camera_state.get('world_eye') if isinstance(camera_state, dict) else None
        reference_x = None
        reference_y = None
        reference_z = None
        if isinstance(world_eye, (list, tuple)) and len(world_eye) >= 3:
            reference_x = float(world_eye[0])
            reference_y = float(world_eye[1])
            reference_z = float(world_eye[2])
        elif controlled is not None:
            reference_x = float(controlled.position.get('x', 0.0))
            reference_y = float(controlled.position.get('y', 0.0))
            reference_z = float(controlled.position.get('z', map_manager.get_terrain_height_m(reference_x, reference_y)))
        if reference_x is None or reference_y is None or reference_z is None:
            return None
        entity_z = float(getattr(entity, 'position', {}).get('z', map_manager.get_terrain_height_m(entity.position['x'], entity.position['y'])))
        planar_world = math.hypot(float(entity.position['x']) - reference_x, float(entity.position['y']) - reference_y)
        planar_m = float(renderer._world_units_to_meters(map_manager, planar_world))
        vertical_m = abs(entity_z - reference_z)
        return float(math.hypot(planar_m, vertical_m))

    def _entity_detail_mode(self, renderer, map_manager, camera_state, controlled, entity, distance_m=None):
        camera_mode = str(camera_state.get('camera_mode', 'first_person')) if isinstance(camera_state, dict) else 'first_person'
        if camera_mode == 'third_person' and controlled is not None and getattr(entity, 'id', None) == getattr(controlled, 'id', None):
            return 'full'
        resolved_distance_m = distance_m if distance_m is not None else self._entity_distance_m(renderer, map_manager, camera_state, controlled, entity)
        if resolved_distance_m is None:
            return 'medium'
        distance_m = float(resolved_distance_m)
        if camera_mode == 'third_person':
            if distance_m <= self.third_person_full_detail_m:
                return 'full'
            if distance_m <= self.third_person_medium_detail_m:
                return 'medium'
            if self.min_detail_mode == 'medium' and distance_m <= self.max_entity_distance_m:
                return 'medium'
            return 'proxy'
        if distance_m <= self.first_person_full_detail_m:
            return 'full'
        if distance_m <= self.first_person_medium_detail_m:
            return 'medium'
        if self.min_detail_mode == 'medium' and distance_m <= self.max_entity_distance_m:
            return 'medium'
        return 'proxy'

    def _scene_half_extents(self, renderer, map_manager, sample_data, entity):
        sampled_cell_size = max(float(sample_data.get('cell_size', 1.0) or 1.0), 1e-6)
        vertical_scale = float(renderer._entity_vertical_scale(entity))
        body_length_m = float(getattr(entity, 'body_length_m', getattr(entity, 'body_size_m', 0.42)))
        body_width_m = float(getattr(entity, 'body_width_m', getattr(entity, 'body_size_m', 0.36)))
        body_height_m = float(getattr(entity, 'body_height_m', 0.18)) * vertical_scale
        return (
            renderer._meters_to_world_units(map_manager, body_length_m * 0.5) / sampled_cell_size,
            renderer._meters_to_world_units(map_manager, body_height_m * 0.5) / sampled_cell_size,
            renderer._meters_to_world_units(map_manager, body_width_m * 0.5) / sampled_cell_size,
        )

    def _collect_entity_model_spec(self, renderer, map_manager, entity, detail_mode):
        vertical_scale = float(renderer._entity_vertical_scale(entity))
        render_width_scale = max(0.45, min(1.0, float(getattr(entity, 'body_render_width_scale', 0.82))))
        meters_to_world = lambda value: float(renderer._meters_to_world_units(map_manager, float(value)))

        body_clearance_m = float(getattr(entity, 'body_clearance_m', 0.10)) * vertical_scale
        body_height_m = float(getattr(entity, 'body_height_m', 0.18)) * vertical_scale
        body_center_m = body_clearance_m + body_height_m * 0.5
        body_half_length_wu = meters_to_world(float(getattr(entity, 'body_length_m', getattr(entity, 'body_size_m', 0.42))) * 0.5)
        body_half_width_wu = meters_to_world(float(getattr(entity, 'body_width_m', getattr(entity, 'body_size_m', 0.36))) * 0.5 * render_width_scale)

        team_body_fallback = (224, 82, 78, 236) if getattr(entity, 'team', '') == 'red' else (86, 130, 224, 236)
        body_color = self._color_rgba(getattr(entity, 'body_color_rgb', ()), team_body_fallback)
        turret_color = self._color_rgba(getattr(entity, 'turret_color_rgb', ()), (232, 232, 236, 236))
        armor_color = self._color_rgba(getattr(entity, 'armor_color_rgb', ()), (224, 229, 234, 238))
        wheel_color = self._color_rgba(getattr(entity, 'wheel_color_rgb', ()), (52, 54, 60, 236))

        armor_half_width_wu = max(1.0, meters_to_world(float(getattr(entity, 'armor_plate_width_m', getattr(entity, 'armor_plate_size_m', 0.12))) * 0.5))
        armor_half_height_m = max(0.05, float(getattr(entity, 'armor_plate_height_m', getattr(entity, 'armor_plate_size_m', 0.12))) * 0.5 * vertical_scale)
        armor_half_length_wu = max(1.0, meters_to_world(float(getattr(entity, 'armor_plate_length_m', getattr(entity, 'armor_plate_size_m', 0.12))) * 0.5))
        armor_thickness_wu = max(0.8, meters_to_world(float(getattr(entity, 'armor_plate_gap_m', 0.02))) + min(armor_half_width_wu, armor_half_length_wu) * 0.08)
        armor_center_offset_m = body_height_m * 0.02

        has_turret = float(getattr(entity, 'gimbal_length_m', 0.0)) > 1e-6 and float(getattr(entity, 'gimbal_body_height_m', 0.0)) > 1e-6
        has_mount = has_turret and (float(getattr(entity, 'gimbal_mount_gap_m', 0.0)) + float(getattr(entity, 'gimbal_mount_height_m', 0.0))) > 1e-6
        has_barrel = has_turret and float(getattr(entity, 'barrel_length_m', 0.0)) > 1e-6 and float(getattr(entity, 'barrel_radius_m', 0.0)) > 1e-6
        turret_center_m = float(renderer._resolve_entity_gimbal_center_height(entity)) * vertical_scale
        turret_center_offset_m = turret_center_m - body_center_m
        turret_offset_x_wu = meters_to_world(float(getattr(entity, 'gimbal_offset_x_m', 0.0)))
        turret_offset_y_wu = meters_to_world(float(getattr(entity, 'gimbal_offset_y_m', 0.0)))
        connector_height_m = (float(getattr(entity, 'gimbal_mount_gap_m', 0.0)) + float(getattr(entity, 'gimbal_mount_height_m', 0.0))) * vertical_scale
        mount_center_m = body_clearance_m + body_height_m + connector_height_m * 0.5
        mount_center_offset_m = mount_center_m - body_center_m

        turret_half_length_wu = max(1.2, meters_to_world(float(getattr(entity, 'gimbal_length_m', 0.30)) * 0.5))
        turret_half_width_wu = max(0.9, meters_to_world(float(getattr(entity, 'gimbal_width_m', 0.10)) * 0.5 * render_width_scale))
        turret_half_height_m = max(0.04, float(getattr(entity, 'gimbal_body_height_m', 0.10)) * 0.5 * vertical_scale)
        mount_half_length_wu = max(0.5, meters_to_world(float(getattr(entity, 'gimbal_mount_length_m', 0.10)) * 0.5))
        mount_half_width_wu = max(0.5, meters_to_world(float(getattr(entity, 'gimbal_mount_width_m', 0.10)) * 0.5 * render_width_scale))
        mount_half_height_m = max(0.03, connector_height_m * 0.5)

        barrel_length_m = float(getattr(entity, 'barrel_length_m', 0.36))
        barrel_pitch_rad = math.radians(float(getattr(entity, 'gimbal_pitch_deg', 0.0)))
        barrel_length_wu = meters_to_world(barrel_length_m * max(0.0, math.cos(barrel_pitch_rad)))
        barrel_vertical_m = barrel_length_m * math.sin(barrel_pitch_rad) * vertical_scale
        barrel_half_width_wu = max(0.28, meters_to_world(float(getattr(entity, 'barrel_radius_m', 0.015)) * 0.78))
        barrel_half_height_m = max(0.012, float(getattr(entity, 'barrel_radius_m', 0.015)) * 0.92 * vertical_scale)

        wheel_radius_m = float(getattr(entity, 'wheel_radius_m', 0.08))
        wheel_radius_wu = meters_to_world(wheel_radius_m)
        wheel_half_length_wu = max(1.0, wheel_radius_wu * 0.82)
        wheel_half_width_wu = max(0.8, meters_to_world(wheel_radius_m * (0.22 if str(getattr(entity, 'wheel_style', 'standard')) == 'omni' else 0.32)))
        wheel_half_height_m = wheel_radius_m * vertical_scale

        raw_positions = [
            position for position in (getattr(entity, 'custom_wheel_positions_m', ()) or ())
            if isinstance(position, (list, tuple)) and len(position) >= 2
        ]
        if not raw_positions:
            body_half_x_m = float(getattr(entity, 'body_length_m', getattr(entity, 'body_size_m', 0.42))) * 0.5
            body_half_y_m = float(getattr(entity, 'body_width_m', getattr(entity, 'body_size_m', 0.36))) * 0.5
            raw_positions = [
                (-body_half_x_m * 0.78, -body_half_y_m * 0.84),
                (-body_half_x_m * 0.78, body_half_y_m * 0.84),
                (body_half_x_m * 0.78, -body_half_y_m * 0.84),
                (body_half_x_m * 0.78, body_half_y_m * 0.84),
            ]
        orbit_yaws = tuple(getattr(entity, 'wheel_orbit_yaws_deg', ()) or ())

        rear_leg_pose = None
        dynamic_indices = set()
        assist_side_offset_wu = 0.0
        if str(getattr(entity, 'rear_climb_assist_style', 'none')) == 'balance_leg':
            rear_leg_pose = renderer._resolve_rear_leg_pose(entity, renderer._climb_assist_animation_state(entity))
            if str(getattr(entity, 'wheel_style', 'standard')) == 'legged' or len(raw_positions) <= 2:
                dynamic_indices = set(range(len(raw_positions)))
            else:
                dynamic_count = max(2, len(raw_positions) // 2)
                dynamic_indices = set(sorted(range(len(raw_positions)), key=lambda index: float(raw_positions[index][0]))[:dynamic_count])
            assist_side_offset_wu = max(1.0, body_half_width_wu + max(0.8, wheel_radius_wu * 0.32) * 0.45) - meters_to_world(float(getattr(entity, 'rear_climb_assist_inner_offset_m', 0.03)) * render_width_scale)
            assist_side_offset_wu = max(body_half_width_wu * 0.45, assist_side_offset_wu)

        wheel_centers = []
        for index, position in enumerate(raw_positions):
            if rear_leg_pose is not None and index in dynamic_indices:
                local_x_wu = meters_to_world(float(rear_leg_pose['foot_x_m']))
                local_y_wu = assist_side_offset_wu * (-1.0 if float(position[1]) < 0.0 else 1.0)
                center_m = float(rear_leg_pose['foot_h_m'])
            else:
                orbit_rad = math.radians(float(orbit_yaws[index])) if index < len(orbit_yaws) else 0.0
                rotated_x_m = float(position[0]) * math.cos(orbit_rad) - float(position[1]) * render_width_scale * math.sin(orbit_rad)
                rotated_y_m = float(position[0]) * math.sin(orbit_rad) + float(position[1]) * render_width_scale * math.cos(orbit_rad)
                local_x_wu = meters_to_world(rotated_x_m)
                local_y_wu = meters_to_world(rotated_y_m)
                center_m = wheel_radius_m
            wheel_centers.append({
                'x_wu': float(local_x_wu),
                'y_wu': float(local_y_wu),
                'h_offset_m': float(center_m - body_center_m),
            })

        leg_spec = {'enabled': False}
        if rear_leg_pose is not None and str(detail_mode or 'proxy') == 'full':
            upper_pair_gap_wu = meters_to_world(float(getattr(entity, 'rear_climb_assist_upper_pair_gap_m', 0.06)) * 0.5)
            upper_half_width_wu = meters_to_world(float(getattr(entity, 'rear_climb_assist_upper_width_m', 0.016)) * 0.5)
            lower_half_width_wu = meters_to_world(float(getattr(entity, 'rear_climb_assist_lower_width_m', 0.016)) * 0.5)
            upper_half_height_m = max(0.012, float(getattr(entity, 'rear_climb_assist_upper_height_m', 0.016)) * 0.5 * vertical_scale)
            lower_half_height_m = max(0.012, float(getattr(entity, 'rear_climb_assist_lower_height_m', 0.016)) * 0.5 * vertical_scale)
            hinge_half_forward_wu = max(0.4, meters_to_world(float(getattr(entity, 'rear_climb_assist_hinge_radius_m', 0.016)) / math.sqrt(2.0)))
            hinge_half_height_m = max(0.008, float(getattr(entity, 'rear_climb_assist_hinge_radius_m', 0.016)) / math.sqrt(2.0) * vertical_scale)
            foot_hub_half_right_wu = max(hinge_half_forward_wu, wheel_half_width_wu * 0.72)
            leg_spec = {
                'enabled': True,
                'side_offset_wu': float(assist_side_offset_wu),
                'upper_pair_gap_wu': float(upper_pair_gap_wu),
                'upper_half_width_wu': float(upper_half_width_wu),
                'upper_half_height_m': float(upper_half_height_m),
                'lower_half_width_wu': float(lower_half_width_wu),
                'lower_half_height_m': float(lower_half_height_m),
                'hinge_half_forward_wu': float(hinge_half_forward_wu),
                'hinge_half_height_m': float(hinge_half_height_m),
                'foot_hub_half_right_wu': float(foot_hub_half_right_wu),
                'upper_anchor_x_wu': float(meters_to_world(float(rear_leg_pose['upper_anchor_x_m']))),
                'upper_anchor_h_offset_m': float(float(rear_leg_pose['upper_anchor_h_m']) - body_center_m),
                'joint_x_wu': float(meters_to_world(float(rear_leg_pose['joint_x_m']))),
                'joint_h_offset_m': float(float(rear_leg_pose['joint_h_m']) - body_center_m),
                'foot_x_wu': float(meters_to_world(float(rear_leg_pose['foot_x_m']))),
                'foot_h_offset_m': float(float(rear_leg_pose['foot_h_m']) - body_center_m),
                'upper_color_rgba': (106, 110, 120, 236),
                'lower_color_rgba': (92, 96, 108, 236),
                'hinge_color_rgba': (148, 154, 168, 236),
            }

        return {
            'detail_mode': str(detail_mode or 'proxy'),
            'body_half_length_wu': float(body_half_length_wu),
            'body_half_width_wu': float(body_half_width_wu),
            'body_half_height_m': float(body_height_m * 0.5),
            'body_color_rgba': body_color,
            'armor_half_width_wu': float(armor_half_width_wu),
            'armor_half_height_m': float(armor_half_height_m),
            'armor_thickness_wu': float(armor_thickness_wu),
            'armor_center_offset_m': float(armor_center_offset_m),
            'armor_color_rgba': armor_color,
            'mount_enabled': bool(has_mount),
            'mount_half_length_wu': float(mount_half_length_wu),
            'mount_half_width_wu': float(mount_half_width_wu),
            'mount_half_height_m': float(mount_half_height_m),
            'mount_center_offset_m': float(mount_center_offset_m),
            'mount_color_rgba': (96, 100, 112, 236),
            'turret_enabled': bool(has_turret),
            'turret_half_length_wu': float(turret_half_length_wu),
            'turret_half_width_wu': float(turret_half_width_wu),
            'turret_half_height_m': float(turret_half_height_m),
            'turret_offset_x_wu': float(turret_offset_x_wu),
            'turret_offset_y_wu': float(turret_offset_y_wu),
            'turret_center_offset_m': float(turret_center_offset_m),
            'turret_yaw_delta_deg': float(getattr(entity, 'turret_angle', getattr(entity, 'angle', 0.0)) - getattr(entity, 'angle', 0.0)),
            'turret_color_rgba': turret_color,
            'barrel_enabled': bool(has_barrel),
            'barrel_length_wu': float(barrel_length_wu),
            'barrel_vertical_m': float(barrel_vertical_m),
            'barrel_half_width_wu': float(barrel_half_width_wu),
            'barrel_half_height_m': float(barrel_half_height_m),
            'barrel_color_rgba': (198, 200, 206, 236),
            'wheel_half_length_wu': float(wheel_half_length_wu),
            'wheel_half_width_wu': float(wheel_half_width_wu),
            'wheel_half_height_m': float(wheel_half_height_m),
            'wheel_color_rgba': wheel_color,
            'wheel_centers': wheel_centers,
            'leg': leg_spec,
        }

    def _collect_entities(self, renderer, game_engine, camera_state, sample_data):
        payload = []
        map_manager = game_engine.map_manager
        controlled_getter = getattr(game_engine, 'get_player_controlled_entity', None)
        controlled = controlled_getter() if callable(controlled_getter) else None
        controlled_id = getattr(controlled, 'id', None)
        camera_mode = str(camera_state.get('camera_mode', 'first_person'))
        for entity in getattr(game_engine.entity_manager, 'entities', []):
            if not entity.is_alive() or getattr(entity, 'type', None) not in {'robot', 'sentry'}:
                continue
            if controlled_id is not None and entity.id == controlled_id and camera_mode != 'third_person':
                continue
            distance_m = self._entity_distance_m(renderer, map_manager, camera_state, controlled, entity)
            if distance_m is not None and distance_m > self.max_entity_distance_m:
                continue
            if not self.fast_visibility:
                visibility = renderer._entity_visibility_state(entity, map_manager, camera_state)
                if not visibility.get('visible', True):
                    continue
            base_height = float(getattr(entity, 'position', {}).get('z', map_manager.get_terrain_height_m(entity.position['x'], entity.position['y'])))
            _, forward_basis, right_basis, up_basis = renderer._entity_scene_axes(entity, map_manager, sample_data, base_height)
            center_height_m = float(entity.position.get('z', 0.0))
            center_height_m += float(getattr(entity, 'body_clearance_m', 0.0))
            center_height_m += float(getattr(entity, 'body_height_m', 0.18)) * float(renderer._entity_vertical_scale(entity)) * 0.5
            scene_position = renderer._world_to_scene_point(
                map_manager,
                sample_data,
                float(entity.position.get('x', 0.0)),
                float(entity.position.get('y', 0.0)),
                center_height_m,
            )[:3]
            detail_mode = self._entity_detail_mode(renderer, map_manager, camera_state, controlled, entity, distance_m=distance_m)
            payload.append(
                {
                    'id': entity.id,
                    'type': getattr(entity, 'type', ''),
                    'team': getattr(entity, 'team', ''),
                    'scene_position': (float(scene_position[0]), float(scene_position[1]), float(scene_position[2])),
                    'half_extents': self._scene_half_extents(renderer, map_manager, sample_data, entity),
                    'basis_forward': (float(forward_basis[0]), float(forward_basis[1]), float(forward_basis[2])),
                    'basis_right': (float(right_basis[0]), float(right_basis[1]), float(right_basis[2])),
                    'basis_up': (float(up_basis[0]), float(up_basis[1]), float(up_basis[2])),
                    'yaw_deg': float(getattr(entity, 'angle', 0.0)),
                    'alive': bool(entity.is_alive()),
                    'model_spec': self._collect_entity_model_spec(renderer, map_manager, entity, detail_mode),
                }
            )
        return payload

    def _collect_projectile_traces(self, renderer, game_engine, sample_data):
        rules_engine = getattr(game_engine, 'rules_engine', None)
        traces = list(getattr(rules_engine, 'projectile_traces', ())) if rules_engine is not None else []
        map_manager = game_engine.map_manager
        payload = []
        for trace in traces:
            points = []
            for point in trace.get('path_points', ()):
                if not isinstance(point, (tuple, list)) or len(point) < 3:
                    continue
                scene_point = renderer._world_to_scene_point(
                    map_manager,
                    sample_data,
                    float(point[0]),
                    float(point[1]),
                    float(point[2]),
                )[:3]
                points.append((float(scene_point[0]), float(scene_point[1]), float(scene_point[2])))
            if points:
                payload.append({'team': str(trace.get('team', 'neutral')), 'points': points})
        return payload

    def render_scene(self, renderer, game_engine, size, map_rgb=None):
        from rendering.terrain_scene_backends import _sample_terrain_scene_data

        width, height = int(size[0]), int(size[1])
        map_manager = game_engine.map_manager
        data = _sample_terrain_scene_data(renderer, map_manager, map_rgb)
        cell_colors = np.ascontiguousarray(data.get('cell_colors', data['blended_colors']), dtype=np.uint8)
        sampled_heights = np.ascontiguousarray(data['sampled_heights'], dtype=np.float32)
        camera_state = getattr(renderer, 'terrain_scene_camera_override', None) or {}
        mvp = np.ascontiguousarray(camera_state.get('mvp', np.identity(4, dtype='f4')), dtype=np.float32)
        pitch_abs = abs(float(camera_state.get('pitch', 0.0)))
        simulator_cfg = self.config.get('simulator', {}) if isinstance(self.config, dict) else {}
        base_terrain_comp = float(simulator_cfg.get('native_cpp_terrain_light_compensation', 1.08))
        base_entity_comp = float(simulator_cfg.get('native_cpp_entity_light_compensation', 1.04))
        terrain_light_comp = max(0.72, min(1.30, base_terrain_comp + pitch_abs * 0.0022))
        entity_light_comp = max(0.72, min(1.24, base_entity_comp + pitch_abs * 0.0016))
        prefer_grid_terrain = bool(simulator_cfg.get('native_cpp_prefer_grid_terrain', True))
        asset_root = Path(__file__).resolve().parent.parent / 'robot_venue_map_asset'
        terrain_asset = asset_root / 'venue_map_pybullet.obj'
        scene_payload = {
            'terrain_revision': int(getattr(map_manager, 'raster_version', 0)),
            'grid_width': int(data['grid_width']),
            'grid_height': int(data['grid_height']),
            'cell_size': float(data['cell_size']),
            'scene_step': int(data['scene_step']),
            'height_scene_scale': float(data.get('height_scene_scale', 1.0)),
            'map_width': float(getattr(map_manager, 'map_width', width)),
            'map_height': float(getattr(map_manager, 'map_height', height)),
            'field_length_m': float(getattr(map_manager, 'field_length_m', 28.0)),
            'field_width_m': float(getattr(map_manager, 'field_width_m', 15.0)),
            'scene_units_per_meter_x': float(map_manager.pixels_per_meter_x()) / max(float(data['cell_size']), 1e-6),
            'scene_units_per_meter_y': float(map_manager.pixels_per_meter_y()) / max(float(data['cell_size']), 1e-6),
            'scene_height_units_per_meter': float(data.get('height_scene_scale', 1.0)),
            'prefer_grid_terrain': prefer_grid_terrain,
            'terrain_light_compensation': float(terrain_light_comp),
            'entity_light_compensation': float(entity_light_comp),
            'terrain_asset_obj_path': str(terrain_asset) if terrain_asset.exists() else '',
            'height_bytes': sampled_heights.tobytes(),
            'color_bytes': cell_colors.tobytes(),
            'entities': self._collect_entities(renderer, game_engine, camera_state, data),
            'projectile_traces': self._collect_projectile_traces(renderer, game_engine, data),
            'camera': {
                'mvp_bytes': mvp.tobytes(),
                'yaw': float(camera_state.get('yaw', 0.0)),
                'pitch': float(camera_state.get('pitch', 0.0)),
                'distance': float(camera_state.get('distance', 0.0)),
            },
        }
        self.bridge.set_scene(scene_payload)
        rgba = self.bridge.render_rgba(width, height)
        return pygame.image.fromstring(rgba, (width, height), 'RGBA')
