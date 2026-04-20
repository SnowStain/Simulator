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


_NATIVE_CPP_BACKENDS = {'native_cpp', 'cpp', 'opengl_cpp', 'rm26_native'}
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
        self.scene_bounds = (
            max(1.0, float(grid_width)),
            max(1.0, float(grid_height)),
            max(1.0, float(np.max(sampled_heights) * vertical_scale + base_thickness)),
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