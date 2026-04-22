#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math

import numpy as np

from pygame_compat import pygame

try:
    import moderngl
    MODERNGL_IMPORT_ERROR = None
except Exception as exc:
    moderngl = None
    MODERNGL_IMPORT_ERROR = str(exc)

try:
    import pyglet
    pyglet.options['shadow_window'] = False
    PYGLET_IMPORT_ERROR = None
except Exception as exc:
    pyglet = None
    PYGLET_IMPORT_ERROR = str(exc)


_NATIVE_CPP_BACKENDS = {'native_cpp', 'cpp', 'opengl_cpp', 'rm26_native', 'gpu', 'wgl_opengl', 'opengl_gpu'}
_EDITOR_OPENGL_BACKENDS = {'editor_opengl', 'opengl_grid', 'terrain_editor_opengl', 'terrain_editor_gl'}


def create_terrain_scene_backend(name, config=None):
    selected = str(name or 'auto').strip().lower()
    if selected in _EDITOR_OPENGL_BACKENDS:
        try:
            if pyglet is not None and moderngl is not None:
                return EditorOpenGLTerrainSceneBackend()
            if moderngl is not None:
                fallback = ModernGLTerrainSceneBackend()
                fallback.name = 'editor_opengl'
                fallback.status_label = 'editor_opengl'
                return fallback
            reason = f'moderngl unavailable: {MODERNGL_IMPORT_ERROR or "import failed"}'
            return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
        except Exception as exc:
            reason = f'editor opengl init failed: {exc}'
            return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    if selected in _NATIVE_CPP_BACKENDS:
        try:
            from rendering.native_cpp_scene_backend import NativeCppTerrainSceneBackend

            return NativeCppTerrainSceneBackend(config)
        except Exception as exc:
            reason = f'native cpp unavailable: {exc}'
            if moderngl is not None and pyglet is not None:
                try:
                    fallback = PygletModernGLTerrainSceneBackend()
                    fallback.status_label = f'{getattr(fallback, "status_label", fallback.name)} | {reason}'
                    return fallback
                except Exception:
                    pass
            return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    if selected in {'auto', 'pyglet', 'pyglet_moderngl', 'pyglet-moderngl'} and moderngl is not None and pyglet is not None:
        try:
            return PygletModernGLTerrainSceneBackend()
        except Exception as exc:
            if selected in {'pyglet', 'pyglet_moderngl', 'pyglet-moderngl'}:
                raise
            reason = f'pyglet+moderngl init failed: {exc}'
            return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    if selected in {'pyglet', 'pyglet_moderngl', 'pyglet-moderngl'}:
        if pyglet is None:
            reason = f'pyglet unavailable: {PYGLET_IMPORT_ERROR or "import failed"}'
        else:
            reason = f'moderngl unavailable: {MODERNGL_IMPORT_ERROR or "import failed"}'
        return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    if selected in {'auto', 'moderngl'} and moderngl is not None:
        try:
            return ModernGLTerrainSceneBackend()
        except Exception as exc:
            if selected == 'moderngl':
                raise
            reason = f'moderngl init failed: {exc}'
            return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    if selected in {'auto', 'moderngl'}:
        reason = f'moderngl unavailable: {MODERNGL_IMPORT_ERROR or "import failed"}'
        return SoftwareTerrainSceneBackend(reason=reason, requested=selected)
    return SoftwareTerrainSceneBackend(requested=selected)


def _terrain_scene_base_cell_size(map_manager):
    getter = getattr(map_manager, 'terrain_scene_sample_cell_size', None)
    if callable(getter):
        return max(1.0, float(getter()))
    return max(1.0, float(getattr(map_manager, 'terrain_grid_cell_size', 1.0)))


def _sample_terrain_scene_data(renderer, map_manager, map_rgb):
    base_cell_size = _terrain_scene_base_cell_size(map_manager)
    full_grid_width = max(1, int(math.ceil(float(map_manager.map_width) / base_cell_size)))
    full_grid_height = max(1, int(math.ceil(float(map_manager.map_height) / base_cell_size)))
    effective_max_cells_getter = getattr(renderer, '_effective_terrain_scene_max_cells', None)
    if callable(effective_max_cells_getter):
        max_scene_cells = max(6000, int(effective_max_cells_getter()))
    else:
        max_scene_cells = max(12000, int(getattr(renderer, 'terrain_scene_max_cells', 90000)))
    scene_step = max(1, int(math.ceil(math.sqrt(max((full_grid_width * full_grid_height) / max(max_scene_cells, 1), 1.0)))))
    cache_key = (
        int(getattr(map_manager, 'raster_version', 0)),
        float(base_cell_size),
        int(map_manager.map_width),
        int(map_manager.map_height),
        int(max_scene_cells),
        int(scene_step),
        id(map_rgb) if map_rgb is not None else None,
        str(getattr(renderer, 'terrain_editor_tool', 'terrain')),
        bool(getattr(renderer, 'terrain_scene_force_dark_gray', False)),
        int(getattr(map_manager, 'facility_version', 0)),
        _terrain_surface_signature(getattr(renderer, 'game_engine', None)),
    )
    if getattr(renderer, 'terrain_scene_sample_cache_key', None) == cache_key:
        cached = getattr(renderer, 'terrain_scene_sample_cache', None)
        if cached is not None:
            return cached

    layers = map_manager.get_raster_layers()
    height_map = layers['height_map']
    terrain_type_map = layers['terrain_type_map']
    grid_width = max(1, int(math.ceil(full_grid_width / scene_step)))
    grid_height = max(1, int(math.ceil(full_grid_height / scene_step)))
    cell_size = base_cell_size * scene_step
    world_center_xs = np.minimum(
        map_manager.map_width - 1,
        (np.arange(grid_width, dtype=np.float32) * scene_step + scene_step * 0.5) * base_cell_size,
    )
    world_center_ys = np.minimum(
        map_manager.map_height - 1,
        (np.arange(grid_height, dtype=np.float32) * scene_step + scene_step * 0.5) * base_cell_size,
    )
    center_xs = np.clip(
        (world_center_xs / max(float(map_manager.runtime_cell_width_world), 1e-6)).astype(np.int32),
        0,
        max(0, int(map_manager.runtime_grid_width) - 1),
    )
    center_ys = np.clip(
        (world_center_ys / max(float(map_manager.runtime_cell_height_world), 1e-6)).astype(np.int32),
        0,
        max(0, int(map_manager.runtime_grid_height) - 1),
    )
    sampled_heights = height_map[np.ix_(center_ys, center_xs)]
    sampled_codes = terrain_type_map[np.ix_(center_ys, center_xs)]
    force_dark_gray = bool(getattr(renderer, 'terrain_scene_force_dark_gray', False))
    if force_dark_gray:
        sampled_base_colors = np.full((grid_height, grid_width, 3), (124, 128, 134), dtype=np.uint8)
    elif map_rgb is not None:
        sampled_base_colors = map_rgb[
            np.ix_(
                np.clip(world_center_ys.astype(np.int32), 0, map_rgb.shape[0] - 1),
                np.clip(world_center_xs.astype(np.int32), 0, map_rgb.shape[1] - 1),
            )
        ]
    else:
        sampled_base_colors = np.full((grid_height, grid_width, 3), 214, dtype=np.uint8)

    if getattr(renderer, 'terrain_editor_tool', 'terrain') == 'facility':
        sampled_heights = np.zeros_like(sampled_heights)
        sampled_codes = np.zeros_like(sampled_codes)

    cell_colors = sampled_base_colors.copy()

    data = {
        'grid_width': grid_width,
        'grid_height': grid_height,
        'cell_size': cell_size,
        'scene_step': scene_step,
        'height_scene_scale': float(map_manager.meters_to_world_units(1.0)) / max(cell_size, 1e-6),
        'sampled_heights': sampled_heights,
        'sampled_codes': sampled_codes,
        'sampled_base_colors': sampled_base_colors,
        'blended_colors': cell_colors,
        'cell_colors': cell_colors,
    }
    renderer.terrain_scene_sample_cache_key = cache_key
    renderer.terrain_scene_sample_cache = data
    return data


def _terrain_scene_focus_grid(renderer, map_manager, grid_width, grid_height, scene_step=1):
    focus_world = getattr(renderer, 'terrain_scene_focus_world', None)
    if focus_world is None:
        return (grid_width - 1) / 2.0, (grid_height - 1) / 2.0
    sampled_cell_size = max(_terrain_scene_base_cell_size(map_manager) * max(1, int(scene_step)), 1e-6)
    focus_grid_x = int(math.floor(float(focus_world[0]) / sampled_cell_size))
    focus_grid_y = int(math.floor(float(focus_world[1]) / sampled_cell_size))
    focus_grid_x = max(0, min(grid_width - 1, focus_grid_x))
    focus_grid_y = max(0, min(grid_height - 1, focus_grid_y))
    return float(focus_grid_x), float(focus_grid_y)


def _terrain_scene_perspective_matrix(fov_y, aspect, near, far):
    factor = 1.0 / math.tan(fov_y / 2.0)
    matrix = np.zeros((4, 4), dtype='f4')
    matrix[0, 0] = factor / max(aspect, 1e-6)
    matrix[1, 1] = factor
    matrix[2, 2] = (far + near) / (near - far)
    matrix[2, 3] = (2.0 * far * near) / (near - far)
    matrix[3, 2] = -1.0
    return matrix


def _terrain_scene_look_at(eye, target, up):
    forward = target - eye
    forward /= max(np.linalg.norm(forward), 1e-6)
    right = np.cross(forward, up)
    right /= max(np.linalg.norm(right), 1e-6)
    true_up = np.cross(right, forward)

    matrix = np.identity(4, dtype='f4')
    matrix[0, :3] = right
    matrix[1, :3] = true_up
    matrix[2, :3] = -forward
    matrix[0, 3] = -np.dot(right, eye)
    matrix[1, 3] = -np.dot(true_up, eye)
    matrix[2, 3] = np.dot(forward, eye)
    return matrix


def build_terrain_scene_camera_state(renderer, map_manager, size, grid_width, grid_height, max_height, scene_step=1):
    width, height = int(size[0]), int(size[1])
    aspect = width / max(height, 1)
    scene_zoom = max(1.0, float(getattr(renderer, 'terrain_scene_zoom', 1.0)))
    distance = (max(float(grid_width), float(grid_height)) * 1.28 + max_height * 2.4 + 6.0) / scene_zoom
    yaw = renderer.terrain_3d_camera_yaw
    pitch = max(0.20, min(1.15, renderer.terrain_3d_camera_pitch))
    focus_grid_x, focus_grid_y = _terrain_scene_focus_grid(renderer, map_manager, grid_width, grid_height, scene_step=scene_step)
    target = np.array([
        focus_grid_x - grid_width / 2.0 + 0.5,
        max_height * 0.18 + float(getattr(renderer, 'terrain_3d_camera_focus_height', 0.0)),
        focus_grid_y - grid_height / 2.0 + 0.5,
    ], dtype='f4')
    camera = np.array([
        math.sin(yaw) * math.cos(pitch) * distance,
        math.sin(pitch) * distance + max_height * 0.55 + 2.0,
        math.cos(yaw) * math.cos(pitch) * distance,
    ], dtype='f4')
    camera += target
    projection = _terrain_scene_perspective_matrix(math.radians(52.0), aspect, 0.1, max(distance * 4.0, 200.0))
    view = _terrain_scene_look_at(camera, target, np.array([0.0, 1.0, 0.0], dtype='f4'))
    return {
        'target': target,
        'camera': camera,
        'projection': projection,
        'view': view,
        'mvp': projection @ view,
        'distance': distance,
        'pitch': pitch,
        'yaw': yaw,
        'focus_grid_x': focus_grid_x,
        'focus_grid_y': focus_grid_y,
        'max_height': max_height,
    }


def _append_face(vertices, p0, p1, p2, p3, color):
    vertices.extend((*p0, *color, *p1, *color, *p2, *color))
    vertices.extend((*p0, *color, *p2, *color, *p3, *color))


def _extrude_cell_box_vertices(vertices, x0, x1, y0, y1, z0, z1, top_color):
    bottom_color = list(top_color)
    left_color = list(top_color)
    right_color = list(top_color)
    front_color = list(top_color)
    back_color = list(top_color)

    top_nw = (x0, y1, z0)
    top_ne = (x1, y1, z0)
    top_se = (x1, y1, z1)
    top_sw = (x0, y1, z1)
    bottom_nw = (x0, y0, z0)
    bottom_ne = (x1, y0, z0)
    bottom_se = (x1, y0, z1)
    bottom_sw = (x0, y0, z1)

    _append_face(vertices, top_nw, top_ne, top_se, top_sw, top_color)
    _append_face(vertices, bottom_sw, bottom_se, bottom_ne, bottom_nw, bottom_color)
    _append_face(vertices, top_sw, top_nw, bottom_nw, bottom_sw, left_color)
    _append_face(vertices, top_ne, top_se, bottom_se, bottom_ne, right_color)
    _append_face(vertices, top_nw, top_ne, bottom_ne, bottom_nw, back_color)
    _append_face(vertices, top_se, top_sw, bottom_sw, bottom_se, front_color)


def _parse_hex_color(value, fallback=(75, 79, 85)):
    text = str(value or '').strip()
    if text.startswith('#'):
        text = text[1:]
    if len(text) == 3:
        text = ''.join(ch * 2 for ch in text)
    if len(text) < 6:
        return tuple(int(channel) for channel in fallback)
    try:
        return (
            int(text[0:2], 16),
            int(text[2:4], 16),
            int(text[4:6], 16),
        )
    except ValueError:
        return tuple(int(channel) for channel in fallback)


def _terrain_surface_config(game_engine):
    if game_engine is None:
        return {}
    getter = getattr(game_engine, 'get_terrain_surface_config', None)
    if callable(getter):
        return dict(getter() or {})
    return dict(game_engine.config.get('map', {}).get('terrain_surface', {}) or {})


def _terrain_surface_signature(game_engine):
    surface_config = _terrain_surface_config(game_engine)
    primitives = []
    for primitive in list(surface_config.get('surface_primitives') or []):
        if not isinstance(primitive, dict):
            continue
        primitives.append((
            str(primitive.get('id') or ''),
            round(float(primitive.get('x1', 0.0)), 3),
            round(float(primitive.get('y1', 0.0)), 3),
            round(float(primitive.get('x2', 0.0)), 3),
            round(float(primitive.get('y2', 0.0)), 3),
            round(float(primitive.get('z_bottom_m', 0.0)), 3),
            round(float(primitive.get('height_m', 0.0)), 3),
            str(primitive.get('side_color') or ''),
        ))
    return tuple(primitives)


def _sample_map_rgb_color(map_rgb, world_x, world_y, fallback=(208, 212, 216)):
    if map_rgb is None or getattr(map_rgb, 'shape', None) is None or len(map_rgb.shape) < 3:
        return tuple(int(channel) for channel in fallback)
    sample_x = max(0, min(int(round(float(world_x))), map_rgb.shape[1] - 1))
    sample_y = max(0, min(int(round(float(world_y))), map_rgb.shape[0] - 1))
    sample = map_rgb[sample_y, sample_x]
    return tuple(int(sample[index]) for index in range(3))


def _terrain_surface_scene_boxes(renderer, game_engine, data, map_rgb=None):
    surface_config = _terrain_surface_config(game_engine)
    scene_step = max(1, int(data.get('scene_step', 1)))
    sampled_cell_size = _terrain_scene_base_cell_size(game_engine.map_manager) * scene_step
    center_offset_x = data['grid_width'] / 2.0
    center_offset_y = data['grid_height'] / 2.0
    vertical_scale = float(data.get('height_scene_scale', 1.0))
    fallback_side_color = surface_config.get('primitive_side_color') or surface_config.get('side_color') or '#4B4F55'
    selected_id = str(getattr(renderer, 'selected_surface_primitive_id', '') or '')
    boxes = []
    for primitive in list(surface_config.get('surface_primitives') or []):
        if not isinstance(primitive, dict):
            continue
        x1 = float(primitive.get('x1', 0.0))
        x2 = float(primitive.get('x2', x1))
        y1 = float(primitive.get('y1', 0.0))
        y2 = float(primitive.get('y2', y1))
        if abs(x2 - x1) < 1e-3 or abs(y2 - y1) < 1e-3:
            continue
        z_bottom_m = max(0.0, float(primitive.get('z_bottom_m', 0.0)))
        height_m = max(0.05, float(primitive.get('height_m', 0.6)))
        center_world_x = (x1 + x2) * 0.5
        center_world_y = (y1 + y2) * 0.5
        top_color = _sample_map_rgb_color(map_rgb, center_world_x, center_world_y)
        side_color = _parse_hex_color(primitive.get('side_color') or fallback_side_color)
        if selected_id and str(primitive.get('id') or '') == selected_id:
            side_color = tuple(min(255, int(channel * 0.72 + 70)) for channel in side_color)
        boxes.append({
            'id': str(primitive.get('id') or ''),
            'x0': min(x1, x2) / sampled_cell_size - center_offset_x,
            'x1': max(x1, x2) / sampled_cell_size - center_offset_x,
            'z0': min(y1, y2) / sampled_cell_size - center_offset_y,
            'z1': max(y1, y2) / sampled_cell_size - center_offset_y,
            'y0': z_bottom_m * vertical_scale,
            'y1': (z_bottom_m + height_m) * vertical_scale,
            'top_color': tuple(channel / 255.0 for channel in top_color),
            'side_color': tuple(channel / 255.0 for channel in side_color),
            'top_color_rgb': top_color,
            'side_color_rgb': side_color,
        })
    return boxes


def _extrude_surface_box_vertices(vertices, box):
    side_color = list(box['side_color'])
    top_color = list(box['top_color'])
    bottom_color = [channel * 0.55 for channel in side_color]
    x0 = box['x0']
    x1 = box['x1']
    y0 = box['y0']
    y1 = box['y1']
    z0 = box['z0']
    z1 = box['z1']

    top_nw = (x0, y1, z0)
    top_ne = (x1, y1, z0)
    top_se = (x1, y1, z1)
    top_sw = (x0, y1, z1)
    bottom_nw = (x0, y0, z0)
    bottom_ne = (x1, y0, z0)
    bottom_se = (x1, y0, z1)
    bottom_sw = (x0, y0, z1)

    _append_face(vertices, top_nw, top_ne, top_se, top_sw, top_color)
    _append_face(vertices, bottom_sw, bottom_se, bottom_ne, bottom_nw, bottom_color)
    _append_face(vertices, top_sw, top_nw, bottom_nw, bottom_sw, side_color)
    _append_face(vertices, top_ne, top_se, bottom_se, bottom_ne, side_color)
    _append_face(vertices, top_nw, top_ne, bottom_ne, bottom_nw, side_color)
    _append_face(vertices, top_se, top_sw, bottom_sw, bottom_se, side_color)


def _facility_scene_default_height(facility_type):
    if facility_type == 'base':
        return 1.18
    if facility_type == 'outpost':
        return 1.58
    if facility_type == 'energy_mechanism':
        return 2.30
    return 0.60


def _facility_scene_color(region):
    facility_type = str(region.get('type', ''))
    team = str(region.get('team', 'neutral'))
    if facility_type == 'base':
        return (0.78, 0.16, 0.14) if team == 'red' else (0.16, 0.36, 0.82)
    if facility_type == 'outpost':
        return (0.82, 0.22, 0.18) if team == 'red' else (0.18, 0.44, 0.92)
    if facility_type == 'energy_mechanism':
        return (0.84, 0.70, 0.28)
    return (0.45, 0.48, 0.52)


def _facility_scene_to_local(data, map_manager, world_x, world_y):
    scene_step = max(1, int(data.get('scene_step', 1)))
    sampled_cell_size = _terrain_scene_base_cell_size(map_manager) * scene_step
    center_offset_x = data['grid_width'] / 2.0
    center_offset_y = data['grid_height'] / 2.0
    return (
        float(world_x) / max(sampled_cell_size, 1e-6) - center_offset_x,
        float(world_y) / max(sampled_cell_size, 1e-6) - center_offset_y,
        sampled_cell_size,
    )


def _rotated_xz(center_x, center_z, local_x, local_z, yaw):
    cos_y = math.cos(yaw)
    sin_y = math.sin(yaw)
    return (
        center_x + local_x * cos_y - local_z * sin_y,
        center_z + local_x * sin_y + local_z * cos_y,
    )


def _append_oriented_box(vertices, center_x, center_y, center_z, size_x, size_y, size_z, color, yaw=0.0):
    hx = max(0.001, float(size_x) * 0.5)
    hy = max(0.001, float(size_y) * 0.5)
    hz = max(0.001, float(size_z) * 0.5)
    corners = {}
    for name, lx, ly, lz in (
        ('tnw', -hx, hy, -hz), ('tne', hx, hy, -hz), ('tse', hx, hy, hz), ('tsw', -hx, hy, hz),
        ('bnw', -hx, -hy, -hz), ('bne', hx, -hy, -hz), ('bse', hx, -hy, hz), ('bsw', -hx, -hy, hz),
    ):
        px, pz = _rotated_xz(center_x, center_z, lx, lz, yaw)
        corners[name] = (px, center_y + ly, pz)
    c = list(color)
    dark = [channel * 0.58 for channel in c]
    side = [channel * 0.78 for channel in c]
    _append_face(vertices, corners['tnw'], corners['tne'], corners['tse'], corners['tsw'], c)
    _append_face(vertices, corners['bsw'], corners['bse'], corners['bne'], corners['bnw'], dark)
    _append_face(vertices, corners['tsw'], corners['tnw'], corners['bnw'], corners['bsw'], side)
    _append_face(vertices, corners['tne'], corners['tse'], corners['bse'], corners['bne'], side)
    _append_face(vertices, corners['tnw'], corners['tne'], corners['bne'], corners['bnw'], side)
    _append_face(vertices, corners['tse'], corners['tsw'], corners['bsw'], corners['bse'], side)


def _append_prism(vertices, center_x, y0, center_z, radius_x, radius_z, height, color, yaw=0.0, sides=6):
    sides = max(3, int(sides))
    y1 = y0 + max(0.001, float(height))
    top = []
    bottom = []
    for index in range(sides):
        angle = math.tau * index / sides + yaw
        scale = 0.72 if sides == 6 and index % 2 == 0 else 1.0
        px = center_x + math.cos(angle) * radius_x * scale
        pz = center_z + math.sin(angle) * radius_z * scale
        top.append((px, y1, pz))
        bottom.append((px, y0, pz))
    c = list(color)
    side = [channel * 0.76 for channel in c]
    dark = [channel * 0.52 for channel in c]
    for index in range(1, sides - 1):
        vertices.extend((*top[0], *c, *top[index], *c, *top[index + 1], *c))
        vertices.extend((*bottom[0], *dark, *bottom[index + 1], *dark, *bottom[index], *dark))
    for index in range(sides):
        next_index = (index + 1) % sides
        _append_face(vertices, top[index], top[next_index], bottom[next_index], bottom[index], side)


def _facility_region_bounds(region):
    if region.get('shape') == 'polygon' and region.get('points'):
        xs = [float(point[0]) for point in region.get('points', [])]
        ys = [float(point[1]) for point in region.get('points', [])]
        return min(xs), min(ys), max(xs), max(ys)
    return (
        float(region.get('x1', 0.0)),
        float(region.get('y1', 0.0)),
        float(region.get('x2', 0.0)),
        float(region.get('y2', 0.0)),
    )


def _append_facility_scene_vertices(vertices, renderer, game_engine, data, map_rgb=None):
    if game_engine is None or getattr(game_engine, 'map_manager', None) is None:
        return
    map_manager = game_engine.map_manager
    vertical_scale = float(data.get('height_scene_scale', 1.0))
    for region in map_manager.get_facility_regions():
        facility_type = str(region.get('type', ''))
        if facility_type not in {'base', 'outpost', 'energy_mechanism'}:
            continue
        x1, y1, x2, y2 = _facility_region_bounds(region)
        if abs(x2 - x1) < 1e-3 or abs(y2 - y1) < 1e-3:
            continue
        center_x, center_z, sampled_cell_size = _facility_scene_to_local(data, map_manager, (x1 + x2) * 0.5, (y1 + y2) * 0.5)
        footprint_x = max(abs(x2 - x1) / max(sampled_cell_size, 1e-6), 0.08)
        footprint_z = max(abs(y2 - y1) / max(sampled_cell_size, 1e-6), 0.08)
        model_scale = max(0.05, float(region.get('model_scale', 1.0)))
        height_m = max(0.05, float(region.get('height_m', _facility_scene_default_height(facility_type)))) * model_scale
        y0 = max(0.0, float(region.get('z_bottom_m', 0.0))) * vertical_scale
        height = height_m * vertical_scale
        yaw = math.radians(float(region.get('yaw_deg', 90.0 if facility_type == 'energy_mechanism' else 0.0)))
        color = _facility_scene_color(region)
        dark = tuple(max(0.02, channel * 0.42) for channel in color)
        armor = (0.18, 0.18, 0.18)

        if facility_type == 'base':
            _append_prism(vertices, center_x, y0, center_z, footprint_x * 0.52, footprint_z * 0.52, height * 0.62, color, yaw=yaw, sides=6)
            _append_prism(vertices, center_x, y0 + height * 0.58, center_z, footprint_x * 0.42, footprint_z * 0.42, height * 0.22, tuple(min(1.0, c * 1.12) for c in color), yaw=yaw, sides=6)
            _append_oriented_box(vertices, center_x, y0 + height * 0.88, center_z, footprint_x * 0.55, height * 0.08, footprint_z * 0.12, armor, yaw=yaw)
            _append_oriented_box(vertices, center_x, y0 + height * 0.72, center_z, footprint_x * 0.12, height * 0.40, footprint_z * 0.12, dark, yaw=yaw)
        elif facility_type == 'outpost':
            _append_prism(vertices, center_x, y0, center_z, footprint_x * 0.45, footprint_z * 0.45, height * 0.12, dark, yaw=yaw + math.pi / 4.0, sides=8)
            _append_prism(vertices, center_x, y0 + height * 0.08, center_z, footprint_x * 0.28, footprint_z * 0.28, height * 0.58, color, yaw=yaw, sides=8)
            _append_prism(vertices, center_x, y0 + height * 0.64, center_z, footprint_x * 0.22, footprint_z * 0.22, height * 0.16, tuple(min(1.0, c * 1.18) for c in color), yaw=yaw, sides=8)
            for index in range(4):
                angle = yaw + math.tau * index / 4.0
                px = center_x + math.cos(angle) * footprint_x * 0.31
                pz = center_z + math.sin(angle) * footprint_z * 0.31
                _append_oriented_box(vertices, px, y0 + height * 0.54, pz, footprint_x * 0.18, height * 0.06, footprint_z * 0.04, armor, yaw=angle)
        else:
            base_len = max(float(region.get('structure_base_length_m', 3.40)) / max(sampled_cell_size, 1e-6) * model_scale, footprint_x * 0.92)
            base_w = max(float(region.get('structure_base_width_m', 3.18)) / max(sampled_cell_size, 1e-6) * model_scale, footprint_z * 0.92)
            base_h = max(0.06, float(region.get('structure_base_height_m', 0.30)) * vertical_scale * model_scale)
            top_len = max(0.04, float(region.get('structure_base_top_length_m', 2.10)) / max(sampled_cell_size, 1e-6) * model_scale)
            top_w = max(0.04, float(region.get('structure_base_top_width_m', 1.08)) / max(sampled_cell_size, 1e-6) * model_scale)
            top_h = max(0.02, float(region.get('structure_base_top_height_m', 0.12)) * vertical_scale * model_scale)
            frame_h = max(height, base_h + top_h + 0.30 * vertical_scale)
            support_offset = max(0.04, float(region.get('structure_support_offset_m', 1.03)) / max(sampled_cell_size, 1e-6) * model_scale)
            pair_gap = max(0.04, float(region.get('structure_cantilever_pair_gap_m', 0.42)) / max(sampled_cell_size, 1e-6) * model_scale)
            rotor_radius = 1.40 / max(sampled_cell_size, 1e-6) * model_scale
            disk_radius = 0.15 / max(sampled_cell_size, 1e-6) * model_scale
            _append_prism(vertices, center_x, y0, center_z, base_len * 0.50, base_w * 0.50, base_h, dark, yaw=yaw, sides=8)
            _append_oriented_box(vertices, center_x, y0 + base_h + top_h * 0.5, center_z, top_len, top_h, top_w, color, yaw=yaw)
            for side in (-1.0, 1.0):
                col_x, col_z = _rotated_xz(center_x, center_z, side * support_offset, 0.0, yaw)
                _append_oriented_box(vertices, col_x, y0 + frame_h * 0.52, col_z, 0.08, frame_h, 0.12, dark, yaw=yaw)
            _append_oriented_box(vertices, center_x, y0 + frame_h, center_z, support_offset * 2.0 + 0.14, 0.08, 0.12, dark, yaw=yaw)
            for team_side, team_color in ((-1.0, (0.88, 0.16, 0.12)), (1.0, (0.12, 0.36, 0.92))):
                rotor_cx, rotor_cz = _rotated_xz(center_x, center_z, 0.0, team_side * pair_gap * 0.5, yaw)
                _append_prism(vertices, rotor_cx, y0 + frame_h * 0.72, rotor_cz, disk_radius * 0.72, disk_radius * 0.72, 0.035 * vertical_scale, team_color, yaw=yaw, sides=16)
                for arm_index in range(5):
                    angle = yaw + math.tau * arm_index / 5.0
                    arm_cx = rotor_cx + math.cos(angle) * rotor_radius * 0.52
                    arm_cz = rotor_cz + math.sin(angle) * rotor_radius * 0.52
                    _append_oriented_box(vertices, arm_cx, y0 + frame_h * 0.74, arm_cz, rotor_radius, 0.045 * vertical_scale, 0.045, team_color, yaw=angle)
                    disk_center_radius = rotor_radius + disk_radius * 0.42
                    disk_cx = rotor_cx + math.cos(angle) * disk_center_radius
                    disk_cz = rotor_cz + math.sin(angle) * disk_center_radius
                    _append_prism(vertices, disk_cx, y0 + frame_h * 0.715, disk_cz, disk_radius, disk_radius, 0.06 * vertical_scale, team_color, yaw=angle, sides=18)


class SoftwareTerrainSceneBackend:
    name = 'software'

    def __init__(self, reason=None, requested=None):
        self.reason = reason
        self.requested = requested or 'software'
        if reason:
            self.status_label = f'software fallback | {reason}'
        else:
            self.status_label = 'software'

    def render_scene(self, renderer, game_engine, size, map_rgb=None):
        width, height = int(size[0]), int(size[1])
        surface = pygame.Surface((width, height))
        surface.fill((236, 240, 245))

        map_manager = game_engine.map_manager
        data = _sample_terrain_scene_data(renderer, map_manager, map_rgb)
        grid_width = data['grid_width']
        grid_height = data['grid_height']
        sampled_heights = data['sampled_heights']
        cell_colors = data.get('cell_colors', data['blended_colors'])

        padding = 20
        yaw_cos = math.cos(renderer.terrain_3d_camera_yaw)
        yaw_sin = math.sin(renderer.terrain_3d_camera_yaw)
        pitch = max(0.18, min(1.15, renderer.terrain_3d_camera_pitch))
        scene_zoom = max(1.0, float(getattr(renderer, 'terrain_scene_zoom', 1.0)))
        center_offset_x, center_offset_y = _terrain_scene_focus_grid(renderer, map_manager, grid_width, grid_height, scene_step=data['scene_step'])
        height_scene_scale = float(data.get('height_scene_scale', 1.0))
        base_thickness = 0.16 * height_scene_scale

        def build_entries(tile_width):
            depth_scale = max(3.0, tile_width * 0.95)
            height_scale = max(6.0, depth_scale) * height_scene_scale
            half_w = max(2.0, tile_width * 0.50)
            half_d = max(2.0, tile_width * 0.28 * pitch)
            entries = []
            min_x = float('inf')
            max_x = float('-inf')
            min_y = float('inf')
            max_y = float('-inf')

            for grid_y in range(grid_height):
                for grid_x in range(grid_width):
                    height_m = float(sampled_heights[grid_y, grid_x])
                    top_color = tuple(int(channel) for channel in cell_colors[grid_y, grid_x])
                    local_x = grid_x - center_offset_x
                    local_y = grid_y - center_offset_y
                    rotated_x = local_x * yaw_cos - local_y * yaw_sin
                    depth = local_x * yaw_sin + local_y * yaw_cos
                    screen_x = rotated_x * tile_width
                    screen_y = depth * depth_scale * pitch
                    top_y = screen_y - height_m * height_scale
                    bottom_y = screen_y + base_thickness * height_scale
                    top = [
                        (screen_x, top_y - half_d),
                        (screen_x + half_w, top_y),
                        (screen_x, top_y + half_d),
                        (screen_x - half_w, top_y),
                    ]
                    bottom = [
                        (screen_x, bottom_y - half_d),
                        (screen_x + half_w, bottom_y),
                        (screen_x, bottom_y + half_d),
                        (screen_x - half_w, bottom_y),
                    ]
                    for point_x, point_y in top + bottom:
                        min_x = min(min_x, point_x)
                        max_x = max(max_x, point_x)
                        min_y = min(min_y, point_y)
                        max_y = max(max_y, point_y)
                    entries.append((depth, screen_x, screen_y, top_y, bottom_y, top_color, half_w, half_d))

            return entries, (min_x, max_x, min_y, max_y)

        initial_fit = min((width - padding * 2) / max(grid_width + grid_height, 1), (height - padding * 2) / max((grid_width + grid_height) * 0.58, 1))
        tile_w = max(3.0, min(48.0, initial_fit * scene_zoom))
        cell_entries, bounds = build_entries(tile_w)
        span_x = max(1.0, bounds[1] - bounds[0])
        span_y = max(1.0, bounds[3] - bounds[2])
        max_ratio = max((width - padding * 2) / span_x, (height - padding * 2) / span_y)
        min_ratio = min((width - padding * 2) / span_x, (height - padding * 2) / span_y, 1.0)
        fit_ratio = min_ratio if scene_zoom <= 1.0 else min(1.0, max_ratio)
        if fit_ratio < 0.999 or fit_ratio > 1.001:
            tile_w = max(2.0, tile_w * fit_ratio)
            cell_entries, bounds = build_entries(tile_w)
        offset_x = (width - (bounds[1] - bounds[0])) / 2.0 - bounds[0]
        offset_y = (height - (bounds[3] - bounds[2])) / 2.0 - bounds[2]

        cell_entries.sort(key=lambda item: item[0])
        for _, screen_x, screen_y, top_y, bottom_y, top_color, half_w, half_d in cell_entries:
            screen_x += offset_x
            top_y += offset_y
            bottom_y += offset_y
            top = [
                (round(screen_x), round(top_y - half_d)),
                (round(screen_x + half_w), round(top_y)),
                (round(screen_x), round(top_y + half_d)),
                (round(screen_x - half_w), round(top_y)),
            ]
            bottom = [
                (round(screen_x), round(bottom_y - half_d)),
                (round(screen_x + half_w), round(bottom_y)),
                (round(screen_x), round(bottom_y + half_d)),
                (round(screen_x - half_w), round(bottom_y)),
            ]
            left = [
                top[3],
                top[2],
                bottom[2],
                bottom[3],
            ]
            right = [
                top[1],
                top[2],
                bottom[2],
                bottom[1],
            ]
            front = [
                top[2],
                top[3],
                bottom[3],
                bottom[2],
            ]
            back = [
                top[0],
                top[1],
                bottom[1],
                bottom[0],
            ]
            bottom_color = top_color
            left_color = top_color
            right_color = top_color
            front_color = top_color
            back_color = top_color
            pygame.draw.polygon(surface, bottom_color, bottom)
            pygame.draw.polygon(surface, back_color, back)
            pygame.draw.polygon(surface, front_color, front)
            pygame.draw.polygon(surface, left_color, left)
            pygame.draw.polygon(surface, right_color, right)
            pygame.draw.polygon(surface, top_color, top)
            pygame.draw.polygon(surface, renderer.colors['panel_border'], top, 1)

        depth_scale = max(3.0, tile_w * 0.95)
        height_scale = max(6.0, depth_scale) * height_scene_scale

        def project_point(local_x, local_z, height_value):
            rotated_x = local_x * yaw_cos - local_z * yaw_sin
            depth = local_x * yaw_sin + local_z * yaw_cos
            screen_x = rotated_x * tile_w + offset_x
            screen_y = depth * depth_scale * pitch - height_value * height_scale + offset_y
            return (round(screen_x), round(screen_y), depth)

        surface_boxes = []
        for box in _terrain_surface_scene_boxes(renderer, game_engine, data, map_rgb):
            x0 = box['x0']
            x1 = box['x1']
            z0 = box['z0']
            z1 = box['z1']
            y0 = box['y0']
            y1 = box['y1']
            top = [
                project_point(x0, z0, y1),
                project_point(x1, z0, y1),
                project_point(x1, z1, y1),
                project_point(x0, z1, y1),
            ]
            bottom = [
                project_point(x0, z0, y0),
                project_point(x1, z0, y0),
                project_point(x1, z1, y0),
                project_point(x0, z1, y0),
            ]
            avg_depth = sum(point[2] for point in top) / 4.0
            surface_boxes.append((avg_depth, box, top, bottom))

        surface_boxes.sort(key=lambda item: item[0])
        for _, box, top_points, bottom_points in surface_boxes:
            top = [(point[0], point[1]) for point in top_points]
            bottom = [(point[0], point[1]) for point in bottom_points]
            left = [top[3], top[0], bottom[0], bottom[3]]
            right = [top[1], top[2], bottom[2], bottom[1]]
            front = [top[2], top[3], bottom[3], bottom[2]]
            back = [top[0], top[1], bottom[1], bottom[0]]
            side_color = box['side_color_rgb']
            top_color = box['top_color_rgb']
            bottom_color = tuple(max(0, min(255, int(channel * 0.58))) for channel in side_color)
            pygame.draw.polygon(surface, bottom_color, bottom)
            pygame.draw.polygon(surface, side_color, back)
            pygame.draw.polygon(surface, side_color, front)
            pygame.draw.polygon(surface, side_color, left)
            pygame.draw.polygon(surface, side_color, right)
            pygame.draw.polygon(surface, top_color, top)
            pygame.draw.polygon(surface, renderer.colors['panel_border'], top, 1)

        return surface


class ModernGLTerrainSceneBackend:
    name = 'moderngl'
    default_status_label = 'moderngl'

    def __init__(self):
        if moderngl is None:
            raise RuntimeError('ModernGL is not available')
        self._pyglet_window = None
        self.ctx = self._create_context()
        self.program = self.ctx.program(
            vertex_shader='''
                #version 330
                in vec3 in_position;
                in vec3 in_color;
                uniform mat4 u_mvp;
                uniform vec3 u_light_dir;
                out vec3 v_color;
                void main() {
                    gl_Position = u_mvp * vec4(in_position, 1.0);
                    float light = 0.42 + max(dot(vec3(0.0, 1.0, 0.0), normalize(u_light_dir)), 0.0) * 0.58;
                    v_color = in_color * light;
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
        self.framebuffer = None
        self.framebuffer_size = None
        self.vbo = None
        self.vao = None
        self.geometry_key = None
        self.scene_bounds = (1.0, 1.0, 1.0, 1)
        self.status_label = self.default_status_label

    def _create_context(self):
        return moderngl.create_standalone_context()

    def _make_context_current(self):
        return None

    def render_scene(self, renderer, game_engine, size, map_rgb=None):
        width, height = int(size[0]), int(size[1])
        if width <= 0 or height <= 0:
            return pygame.Surface((1, 1))

        self._make_context_current()
        self._ensure_framebuffer((width, height))
        self._ensure_geometry(renderer, game_engine.map_manager, map_rgb)

        grid_width, grid_height, max_height, scene_step = self.scene_bounds
        camera_override = getattr(renderer, 'terrain_scene_camera_override', None)
        if isinstance(camera_override, dict) and camera_override.get('mvp') is not None:
            mvp = camera_override['mvp']
        else:
            mvp = build_terrain_scene_camera_state(
                renderer,
                game_engine.map_manager,
                size,
                int(grid_width),
                int(grid_height),
                float(max_height),
                scene_step=int(scene_step),
            )['mvp']

        self.framebuffer.use()
        self.framebuffer.clear(0.87, 0.90, 0.94, 1.0)
        self.ctx.enable(moderngl.DEPTH_TEST)
        # Top faces are generated as a single-sided height field. In the current
        # camera/axis convention, enabling face culling can reject the entire map.
        self.ctx.disable(moderngl.CULL_FACE)
        # CPU-side matrix math here is built in row-major form and also reused by
        # the picking path. ModernGL/OpenGL uniforms are consumed as column-major,
        # so transpose before upload to keep GPU rendering aligned with CPU math.
        self.program['u_mvp'].write(mvp.T.astype('f4').tobytes())
        self.program['u_light_dir'].value = (0.35, 0.92, 0.28)
        self.vao.render(moderngl.TRIANGLES)

        raw = self.framebuffer.read(components=3, alignment=1)
        surface = pygame.image.fromstring(raw, (width, height), 'RGB')
        return pygame.transform.flip(surface, False, True)

    def _ensure_framebuffer(self, size):
        if self.framebuffer is not None and self.framebuffer_size == size:
            return
        if self.framebuffer is not None:
            self.framebuffer.release()
        self.framebuffer = self.ctx.simple_framebuffer(size)
        self.framebuffer_size = size

    def _ensure_geometry(self, renderer, map_manager, map_rgb):
        data = _sample_terrain_scene_data(renderer, map_manager, map_rgb)
        scene_step = int(data.get('scene_step', 1))
        geometry_key = (
            map_manager.raster_version,
            map_manager.terrain_grid_cell_size,
            int(data.get('grid_width', 0)),
            int(data.get('grid_height', 0)),
            scene_step,
            round(float(data.get('height_scene_scale', 1.0)), 6),
            bool(getattr(renderer, 'terrain_scene_force_dark_gray', False)),
            id(map_rgb) if map_rgb is not None else None,
            int(getattr(map_manager, 'facility_version', 0)),
            _terrain_surface_signature(renderer.game_engine),
        )
        if self.geometry_key == geometry_key and self.vao is not None:
            return

        grid_width = data['grid_width']
        grid_height = data['grid_height']
        sampled_heights = data['sampled_heights']
        cell_colors = data.get('cell_colors', data['blended_colors'])
        center_offset_x = grid_width / 2.0
        center_offset_y = grid_height / 2.0
        vertical_scale = float(data.get('height_scene_scale', 1.0))
        base_thickness = 0.16 * vertical_scale
        vertices = []

        for grid_y in range(grid_height):
            for grid_x in range(grid_width):
                top_color = [channel / 255.0 for channel in cell_colors[grid_y, grid_x]]
                top_y = float(sampled_heights[grid_y, grid_x]) * vertical_scale
                bottom_y = -base_thickness
                x0 = grid_x - center_offset_x
                x1 = x0 + 1.0
                z0 = grid_y - center_offset_y
                z1 = z0 + 1.0
                _extrude_cell_box_vertices(vertices, x0, x1, bottom_y, top_y, z0, z1, top_color)

        for box in _terrain_surface_scene_boxes(renderer, renderer.game_engine, data, map_rgb):
            _extrude_surface_box_vertices(vertices, box)

        _append_facility_scene_vertices(vertices, renderer, renderer.game_engine, data, map_rgb)

        vertex_array = np.array(vertices, dtype='f4')
        if self.vao is not None:
            self.vao.release()
        if self.vbo is not None:
            self.vbo.release()
        self.vbo = self.ctx.buffer(vertex_array.tobytes())
        self.vao = self.ctx.vertex_array(
            self.program,
            [(self.vbo, '3f 3f', 'in_position', 'in_color')],
        )
        max_box_height = 0.0
        surface_boxes = _terrain_surface_scene_boxes(renderer, renderer.game_engine, data, map_rgb)
        if surface_boxes:
            max_box_height = max(float(box['y1']) for box in surface_boxes)

        self.scene_bounds = (
            max(1.0, float(grid_width)),
            max(1.0, float(grid_height)),
            max(1.0, float(np.max(sampled_heights) * vertical_scale + base_thickness), max_box_height + base_thickness),
            int(scene_step),
        )
        self.geometry_key = geometry_key

    def _perspective_matrix(self, fov_y, aspect, near, far):
        return _terrain_scene_perspective_matrix(fov_y, aspect, near, far)

    def _look_at(self, eye, target, up):
        return _terrain_scene_look_at(eye, target, up)


class PygletModernGLTerrainSceneBackend(ModernGLTerrainSceneBackend):
    name = 'pyglet_moderngl'
    default_status_label = 'pyglet+moderngl'

    def _create_context(self):
        if pyglet is None:
            raise RuntimeError('pyglet is not available')
        config_candidates = (
            {'double_buffer': False, 'major_version': 3, 'minor_version': 3, 'depth_size': 24},
            {'double_buffer': False, 'depth_size': 24},
            {},
        )
        last_error = None
        for config_kwargs in config_candidates:
            window = None
            try:
                gl_config = pyglet.gl.Config(**config_kwargs)
                window = pyglet.window.Window(width=16, height=16, visible=False, config=gl_config, caption='RM26 ModernGL Backend')
                window.switch_to()
                ctx = moderngl.create_context(require=330)
                self._pyglet_window = window
                return ctx
            except Exception as exc:
                last_error = exc
                try:
                    window.close()
                except Exception:
                    pass
        raise RuntimeError(f'pyglet context init failed: {last_error}')

    def _make_context_current(self):
        if self._pyglet_window is not None:
            self._pyglet_window.switch_to()


class EditorOpenGLTerrainSceneBackend(PygletModernGLTerrainSceneBackend):
    name = 'editor_opengl'
    default_status_label = 'editor_opengl'
