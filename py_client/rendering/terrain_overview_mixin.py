#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
import threading

import numpy as np

from pygame_compat import pygame
from rendering.terrain_scene_backends import _sample_terrain_scene_data, _terrain_scene_focus_grid, _terrain_scene_look_at, _terrain_scene_perspective_matrix, build_terrain_scene_camera_state, create_terrain_scene_backend

try:
    from pygame._sdl2.video import Window as SdlWindow, Renderer as SdlRenderer, Texture as SdlTexture
except Exception:
    SdlWindow = None
    SdlRenderer = None
    SdlTexture = None


class TerrainOverviewMixin:
    def _terrain_editor_backend_mode(self):
        selected = str(getattr(self, 'terrain_editor_scene_backend_requested', 'editor_opengl') or 'editor_opengl').strip().lower()
        if selected in {'', 'follow', 'follow_settings', 'global', 'settings', 'default'}:
            return 'follow_settings'
        if selected in {'editor_opengl', 'opengl_grid', 'terrain_editor_opengl', 'terrain_editor_gl'}:
            return 'editor_opengl'
        return selected

    def _terrain_scene_backend_request_name(self):
        requested = str(getattr(self, 'terrain_scene_backend_requested', 'auto') or 'auto').strip().lower()
        if getattr(self, 'edit_mode', None) == 'terrain' and getattr(self, 'terrain_scene_camera_override', None) is None:
            editor_mode = self._terrain_editor_backend_mode()
            if editor_mode == 'follow_settings':
                return requested
            return editor_mode
        return requested

    def _persist_terrain_editor_backend(self, game_engine, requested):
        if isinstance(getattr(self, 'config', None), dict):
            self.config.setdefault('simulator', {})['terrain_editor_scene_backend'] = requested
        if isinstance(getattr(game_engine, 'config', None), dict):
            game_engine.config.setdefault('simulator', {})['terrain_editor_scene_backend'] = requested
            config_manager = getattr(game_engine, 'config_manager', None)
            settings_path = getattr(game_engine, 'settings_path', None)
            if config_manager is not None and settings_path:
                config_manager.config = game_engine.config
                payload = config_manager.build_local_settings_payload(game_engine.config)
                config_manager.save_settings(settings_path, payload=payload)

    def _set_terrain_editor_scene_backend(self, game_engine, requested):
        normalized = str(requested or 'editor_opengl').strip().lower()
        if normalized in {'', 'follow', 'global', 'settings', 'default'}:
            normalized = 'follow_settings'
        elif normalized in {'editor_opengl', 'opengl_grid', 'terrain_editor_opengl', 'terrain_editor_gl'}:
            normalized = 'editor_opengl'
        if normalized == getattr(self, 'terrain_editor_scene_backend_requested', None):
            return
        self.terrain_editor_scene_backend_requested = normalized
        self.terrain_scene_backend = None
        self.terrain_scene_backend_active_request = None
        self._invalidate_terrain_scene_cache()
        self._persist_terrain_editor_backend(game_engine, normalized)
        label = 'OpenGL 栅格地形' if normalized == 'editor_opengl' else '跟随全局设置'
        game_engine.add_log(f'地形编辑 3D 后端已切换为 {label}', 'system')

    def _terrain_scene_prewarm_key(self, game_engine):
        map_manager = game_engine.map_manager
        return (
            int(getattr(map_manager, 'raster_version', 0)),
            int(map_manager.map_width),
            int(map_manager.map_height),
            float(getattr(map_manager, 'terrain_grid_cell_size', 1.0)),
        )

    def _start_terrain_scene_prewarm(self, game_engine):
        key = self._terrain_scene_prewarm_key(game_engine)
        worker = getattr(self, 'terrain_scene_prewarm_thread', None)
        if worker is not None and worker.is_alive() and getattr(self, 'terrain_scene_prewarm_key', None) == key:
            return
        if getattr(self, 'terrain_scene_prewarm_ready_key', None) == key:
            return
        self.terrain_scene_prewarm_key = key
        self.terrain_scene_prewarm_error = None
        self.terrain_scene_prewarm_ready_key = None

        def _worker():
            try:
                game_engine.map_manager.get_raster_layers()
                self.terrain_scene_prewarm_ready_key = key
            except Exception as exc:
                self.terrain_scene_prewarm_error = str(exc)

        self.terrain_scene_prewarm_thread = threading.Thread(target=_worker, name='terrain-scene-prewarm', daemon=True)
        self.terrain_scene_prewarm_thread.start()

    def _terrain_scene_loading_active(self, game_engine):
        if self.terrain_view_mode != '3d':
            return False
        key = self._terrain_scene_prewarm_key(game_engine)
        if getattr(self, 'terrain_scene_prewarm_ready_key', None) == key:
            return False
        worker = getattr(self, 'terrain_scene_prewarm_thread', None)
        return worker is not None and worker.is_alive()

    def _build_terrain_scene_loading_surface(self, size, message='正在加载三维地形...'):
        width, height = int(size[0]), int(size[1])
        surface = pygame.Surface((max(1, width), max(1, height)))
        surface.fill((236, 240, 245))
        panel_rect = pygame.Rect(0, 0, min(360, width - 32), 118)
        panel_rect.center = (width // 2, height // 2)
        pygame.draw.rect(surface, self.colors['overlay_bg'], panel_rect, border_radius=10)
        pygame.draw.rect(surface, self.colors['panel_border'], panel_rect, 1, border_radius=10)
        title = self.small_font.render('3D 地形预热中', True, self.colors['white'])
        detail = self.tiny_font.render(message, True, self.colors['white'])
        hint = self.tiny_font.render('首帧完成后会自动切入 3D 视图', True, self.colors['white'])
        bar_rect = pygame.Rect(panel_rect.x + 24, panel_rect.bottom - 28, panel_rect.width - 48, 8)
        phase = (pygame.time.get_ticks() // 220) % 5
        fill_width = max(36, int(bar_rect.width * (0.24 + phase * 0.15)))
        pygame.draw.rect(surface, (70, 82, 100), bar_rect, border_radius=4)
        pygame.draw.rect(surface, (122, 194, 255), pygame.Rect(bar_rect.x, bar_rect.y, min(bar_rect.width, fill_width), bar_rect.height), border_radius=4)
        surface.blit(title, title.get_rect(center=(panel_rect.centerx, panel_rect.y + 30)))
        surface.blit(detail, detail.get_rect(center=(panel_rect.centerx, panel_rect.y + 58)))
        surface.blit(hint, hint.get_rect(center=(panel_rect.centerx, panel_rect.y + 78)))
        return surface

    def _terrain_overview_embedded_mode(self):
        return bool(getattr(self, 'terrain_overview_embedded', False))

    def _render_terrain_overview_host_header(self, surface, game_engine, width):
        return 0

    def _close_terrain_3d_window(self, exit_terrain_mode=False):
        if self.terrain_3d_window is not None:
            try:
                self.terrain_3d_window.hide()
            except Exception:
                pass
        self.terrain_3d_window = None
        self.terrain_3d_renderer = None
        self.terrain_3d_texture = None
        self.terrain_3d_render_key = None
        self.terrain_overview_ui = {'window_id': None, 'buttons': [], 'map_rect': None, 'scene_map_rect': None, 'scene_world_rect': None}
        self.terrain_overview_mouse_pos = None
        self.terrain_overview_viewport_drag_active = False
        self.terrain_3d_orbit_active = False
        self.terrain_3d_orbit_dragged = False
        self.terrain_3d_orbit_last_pos = None
        self.terrain_overview_window_open = False
        if exit_terrain_mode:
            self.edit_mode = 'none'

    def _get_terrain_scene_backend(self):
        requested = self._terrain_scene_backend_request_name()
        if getattr(self, 'terrain_scene_backend', None) is None or getattr(self, 'terrain_scene_backend_active_request', None) != requested:
            self.terrain_scene_backend = create_terrain_scene_backend(requested, config=getattr(self, 'config', None))
            self.terrain_scene_backend_active_request = requested
        return self.terrain_scene_backend

    def _render_terrain_scene_surface(self, game_engine, scene_rect, map_rgb):
        if self._terrain_scene_loading_active(game_engine):
            return self._build_terrain_scene_loading_surface(scene_rect.size)
        backend = self._get_terrain_scene_backend()
        try:
            camera_override = getattr(self, 'terrain_scene_camera_override', None)
            if isinstance(camera_override, dict):
                cache_key_getter = getattr(self, '_player_terrain_surface_cache_key', None)
                cache_key = cache_key_getter(game_engine, scene_rect) if callable(cache_key_getter) else None
                cached_surface = getattr(self, 'player_terrain_surface_cache', None)
                if cache_key is not None and getattr(self, 'player_terrain_surface_key', None) == cache_key and cached_surface is not None:
                    offset_getter = getattr(self, '_player_terrain_cache_blit_offset', None)
                    cached_meta = getattr(self, 'player_terrain_surface_meta', None)
                    if callable(offset_getter):
                        self._player_terrain_surface_blit_offset = offset_getter(game_engine.map_manager, scene_rect, camera_override, cached_meta)
                    else:
                        self._player_terrain_surface_blit_offset = (0, 0)
                    return cached_surface
                render_scale = max(0.35, min(1.0, float(getattr(self, '_active_player_terrain_render_scale', getattr(self, 'player_terrain_render_scale', 0.6)))))
                render_size = scene_rect.size
                if render_scale < 0.999:
                    render_size = (
                        max(320, int(scene_rect.width * render_scale)),
                        max(180, int(scene_rect.height * render_scale)),
                    )
                surface = backend.render_scene(self, game_engine, render_size, map_rgb)
                if render_size != scene_rect.size:
                    surface = pygame.transform.smoothscale(surface, scene_rect.size)
                self._player_terrain_surface_blit_offset = (0, 0)
                if cache_key is not None:
                    self.player_terrain_surface_cache = surface
                    self.player_terrain_surface_key = cache_key
                    self.player_terrain_surface_meta = {
                        'anchor_world': camera_override.get('world_anchor'),
                        'camera_state': {
                            'mvp': np.array(camera_override.get('mvp'), dtype='f4', copy=True),
                        },
                    }
                return surface
            cache_key_getter = getattr(self, '_terrain_scene_surface_cache_key', None)
            cache_key = cache_key_getter(game_engine, scene_rect, map_rgb) if callable(cache_key_getter) else None
            cached_surface = getattr(self, 'terrain_scene_surface_cache', None)
            if cache_key is not None and getattr(self, 'terrain_scene_surface_key', None) == cache_key and cached_surface is not None:
                return cached_surface
            surface = backend.render_scene(self, game_engine, scene_rect.size, map_rgb)
            if cache_key is not None:
                self.terrain_scene_surface_cache = surface
                self.terrain_scene_surface_key = cache_key
            return surface
        except Exception:
            if getattr(backend, 'name', 'software') == 'software':
                raise
            self.terrain_scene_backend = create_terrain_scene_backend('software')
            self.terrain_scene_backend_active_request = 'software'
            return self.terrain_scene_backend.render_scene(self, game_engine, scene_rect.size, map_rgb)

    def _invalidate_terrain_scene_cache(self):
        self.terrain_3d_texture = None
        self.terrain_3d_render_key = None
        self.player_terrain_surface_cache = None
        self.player_terrain_surface_key = None
        self.terrain_scene_surface_cache = None
        self.terrain_scene_surface_key = None
        if hasattr(self, 'player_projectile_overlay_surface'):
            self.player_projectile_overlay_surface = None
            self.player_projectile_overlay_size = None

    def _draw_terrain_scene_hover_panel(self, surface, rect, map_manager, world_pos):
        metrics = self._hover_grid_metrics(map_manager, world_pos)
        if metrics is None:
            return
        lines = [
            f'栅格高度 {metrics["height_m"]:.2f}m',
            f'栅格宽度 {metrics["cell_width_m"]:.3f}m',
        ]
        rendered_lines = [self.tiny_font.render(line, True, self.colors['white']) for line in lines]
        panel_width = max(text.get_width() for text in rendered_lines) + 18
        panel_height = sum(text.get_height() for text in rendered_lines) + 14 + (len(rendered_lines) - 1) * 3
        panel_rect = pygame.Rect(rect.right - panel_width - 12, rect.bottom - panel_height - 12, panel_width, panel_height)
        pygame.draw.rect(surface, self.colors['overlay_bg'], panel_rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], panel_rect, 1, border_radius=6)
        draw_y = panel_rect.y + 7
        for text in rendered_lines:
            surface.blit(text, (panel_rect.x + 9, draw_y))
            draw_y += text.get_height() + 3

    def _draw_terrain_scene_controls(self, surface, rect):
        return

    def _update_terrain_scene_navigation(self, game_engine):
        now_ms = pygame.time.get_ticks()
        last_ms = getattr(self, 'terrain_3d_navigation_last_ms', now_ms)
        self.terrain_3d_navigation_last_ms = now_ms
        if self.edit_mode != 'terrain' or self.terrain_view_mode != '3d':
            return False
        if self.active_numeric_input is not None or getattr(self, 'active_text_input', None) is not None:
            return False

        dt = max(0.0, min(0.05, (now_ms - last_ms) / 1000.0))
        if dt <= 0.0:
            return False

        pressed = pygame.key.get_pressed()
        move_x = 0.0
        move_y = 0.0
        if pressed[pygame.K_w]:
            move_y += 1.0
        if pressed[pygame.K_s]:
            move_y -= 1.0
        if pressed[pygame.K_d]:
            move_x += 1.0
        if pressed[pygame.K_a]:
            move_x -= 1.0
        height_delta = 0.0
        if pressed[pygame.K_f]:
            height_delta += 1.0
        if pressed[pygame.K_c]:
            height_delta -= 1.0
        if abs(move_x) <= 1e-6 and abs(move_y) <= 1e-6 and abs(height_delta) <= 1e-6:
            return False

        map_manager = game_engine.map_manager
        focus_world = getattr(self, 'terrain_scene_focus_world', None)
        if focus_world is None:
            focus_world = (map_manager.map_width * 0.5, map_manager.map_height * 0.5)
        yaw = self.terrain_3d_camera_yaw
        zoom = max(1.0, float(getattr(self, 'terrain_scene_zoom', 1.0)))
        move_speed = max(90.0, 540.0 / zoom)
        diagonal = math.hypot(move_x, move_y)
        if diagonal > 1e-6:
            move_x /= diagonal
            move_y /= diagonal
        forward_x = -math.sin(yaw)
        forward_y = -math.cos(yaw)
        right_x = math.cos(yaw)
        right_y = -math.sin(yaw)
        world_dx = (right_x * move_x + forward_x * move_y) * move_speed * dt
        world_dy = (right_y * move_x + forward_y * move_y) * move_speed * dt
        next_focus_x = max(0.0, min(float(map_manager.map_width - 1), float(focus_world[0]) + world_dx))
        next_focus_y = max(0.0, min(float(map_manager.map_height - 1), float(focus_world[1]) + world_dy))
        self.terrain_scene_focus_world = (next_focus_x, next_focus_y)
        if abs(height_delta) > 1e-6:
            self.terrain_3d_camera_focus_height = max(-20.0, min(80.0, float(getattr(self, 'terrain_3d_camera_focus_height', 0.0)) + height_delta * dt * 8.0))
        self._invalidate_terrain_scene_cache()
        return True

    def _terrain_backend_badge_text(self, backend):
        name = getattr(backend, 'name', 'software')
        reason = getattr(backend, 'reason', '') or ''
        if name == 'editor_opengl':
            return '渲染后端: OpenGL 栅格地形'
        if name in {'moderngl', 'pyglet_moderngl'}:
            return '渲染后端: GPU / ModernGL'
        if 'No module named' in reason and 'pyglet' in reason:
            return '渲染后端: 软件回退 / 缺少 Pyglet'
        if 'No module named' in reason and 'moderngl' in reason:
            return '渲染后端: 软件回退 / 缺少 ModernGL'
        if 'init failed' in reason:
            return '渲染后端: 软件回退 / OpenGL 初始化失败'
        if reason:
            return '渲染后端: 软件回退'
        return '渲染后端: 软件'

    def _fit_text_to_width(self, font, text, max_width):
        if font.size(text)[0] <= max_width:
            return text
        ellipsis = '...'
        trimmed = text
        while trimmed and font.size(trimmed + ellipsis)[0] > max_width:
            trimmed = trimmed[:-1]
        return (trimmed + ellipsis) if trimmed else ellipsis

    def _terrain_outline_color(self, terrain_type):
        base = self._terrain_color_by_type(terrain_type)
        return tuple(max(0, int(channel * 0.58)) for channel in base)

    def _terrain_cell_border_signature(self, cell):
        if cell is None:
            return None
        return (
            cell.get('type', 'flat'),
            round(float(cell.get('height_m', 0.0)), 2),
            bool(cell.get('blocks_movement', False)),
            bool(cell.get('blocks_vision', False)),
        )

    def _preview_slope_polygon_points(self):
        if not (self._terrain_shape_tool_active() and self.terrain_shape_mode in {'slope', 'slope_plane'}):
            return []
        points = self._slope_preview_polygon_points()
        if not points:
            return []
        preview_world = None
        if not self._slope_direction_mode_active() and self.mouse_world is not None:
            preview_world = self._current_terrain_target(self.mouse_world)
        elif not self._slope_direction_mode_active() and self.drag_current is not None:
            preview_world = self.drag_current
        if preview_world is not None:
            points.append(preview_world)
        return points

    def _draw_slope_direction_arrow(self, surface, map_manager, points, project_point):
        if len(points) < 3:
            return
        direction_start, direction_end = self._current_slope_direction_points()
        slope_info = map_manager.analyze_terrain_slope_polygon(points, direction_start=direction_start, direction_end=direction_end)
        if not slope_info.get('changed'):
            return
        if direction_start is not None and direction_end is not None:
            start = project_point(direction_start)
            end = project_point(direction_end)
        else:
            start = project_point(slope_info['low_point'])
            end = project_point(slope_info['high_point'])
        pygame.draw.line(surface, self.colors['blue'], start, end, 3)
        delta_x = end[0] - start[0]
        delta_y = end[1] - start[1]
        length = math.hypot(delta_x, delta_y)
        if length <= 1e-6:
            return
        unit_x = delta_x / length
        unit_y = delta_y / length
        arrow_size = 10
        left = (
            int(end[0] - unit_x * arrow_size - unit_y * arrow_size * 0.6),
            int(end[1] - unit_y * arrow_size + unit_x * arrow_size * 0.6),
        )
        right = (
            int(end[0] - unit_x * arrow_size + unit_y * arrow_size * 0.6),
            int(end[1] - unit_y * arrow_size - unit_x * arrow_size * 0.6),
        )
        pygame.draw.polygon(surface, self.colors['blue'], [end, left, right])
        label = self.tiny_font.render(
            f'{slope_info.get("min_height", 0.0):.2f}m -> {slope_info.get("max_height", 0.0):.2f}m',
            True,
            self.colors['blue'],
        )
        label_x = int((start[0] + end[0]) / 2 - label.get_width() / 2)
        label_y = int((start[1] + end[1]) / 2 - 16)
        bg_rect = pygame.Rect(label_x - 4, label_y - 2, label.get_width() + 8, label.get_height() + 4)
        pygame.draw.rect(surface, (*self.colors['white'], 210), bg_rect, border_radius=4)
        surface.blit(label, (label_x, label_y))

    def _render_terrain_grid_overlay(self, surface, map_manager, map_rect, view_x=0.0, view_y=0.0, view_width=None, view_height=None):
        if map_rect.width <= 0 or map_rect.height <= 0:
            return
        if view_width is None:
            view_width = map_manager.map_width
        if view_height is None:
            view_height = map_manager.map_height
        draw_outlines = self._terrain_overlay_draw_outlines(map_manager, map_rect, view_width=view_width, view_height=view_height)
        source_overlay = self._get_world_terrain_grid_overlay_surface(map_manager, draw_outlines=draw_outlines)
        source_rect = pygame.Rect(
            max(0, int(view_x)),
            max(0, int(view_y)),
            max(1, min(int(view_width), source_overlay.get_width() - max(0, int(view_x)))),
            max(1, min(int(view_height), source_overlay.get_height() - max(0, int(view_y)))),
        )
        if source_rect.width <= 0 or source_rect.height <= 0:
            return
        cache_key = (
            id(source_overlay),
            (source_rect.x, source_rect.y, source_rect.width, source_rect.height),
            tuple(map_rect.size),
            bool(draw_outlines),
            int(getattr(map_manager, 'terrain_overlay_revision', 0)),
        )
        if getattr(self, 'terrain_overview_scaled_overlay_key', None) != cache_key:
            self.terrain_overview_scaled_overlay_surface = pygame.transform.scale(source_overlay.subsurface(source_rect), map_rect.size)
            self.terrain_overview_scaled_overlay_key = cache_key
        scaled_overlay = getattr(self, 'terrain_overview_scaled_overlay_surface', None)
        surface.blit(scaled_overlay, map_rect.topleft)

    def _ensure_terrain_3d_window(self):
        if self._terrain_overview_embedded_mode():
            return True
        if not getattr(self, 'terrain_overview_window_open', False):
            return False
        if SdlWindow is None or SdlRenderer is None or SdlTexture is None:
            return False
        if self.terrain_3d_window is not None and self.terrain_3d_renderer is not None:
            return True
        self.terrain_3d_window = SdlWindow('地形三维总览', size=self.terrain_3d_window_size)
        self.terrain_3d_renderer = SdlRenderer(self.terrain_3d_window)
        self.terrain_3d_render_key = None
        return True

    def render_terrain_3d_window(self, game_engine):
        if self._terrain_overview_embedded_mode():
            return
        if self.edit_mode != 'terrain' or not getattr(self, 'terrain_overview_window_open', False):
            self._close_terrain_3d_window(exit_terrain_mode=False)
            return

        if not self._ensure_terrain_3d_window():
            return

        try:
            terrain_window = self.terrain_3d_window
            terrain_renderer = self.terrain_3d_renderer
            if terrain_window is None or terrain_renderer is None or SdlTexture is None:
                return

            terrain_window.show()
            if self.terrain_view_mode == '3d':
                self._start_terrain_scene_prewarm(game_engine)
            self._update_terrain_scene_navigation(game_engine)
            size = terrain_window.size
            map_manager = game_engine.map_manager
            scene_data_revision = (
                int(getattr(map_manager, 'raster_version', 0))
                if self.terrain_view_mode == '3d'
                else (
                    int(getattr(map_manager, 'terrain_overlay_revision', 0)),
                    int(getattr(map_manager, 'facility_version', 0)),
                )
            )
            render_key = (
                tuple(size),
                scene_data_revision,
                getattr(self._get_terrain_scene_backend(), 'name', 'software'),
                self.terrain_view_mode,
                round(self.terrain_scene_zoom, 3),
                self.terrain_scene_focus_world,
                self.terrain_editor_tool,
                getattr(self, 'terrain_workflow_mode', 'brush'),
                self.terrain_shape_mode,
                self.region_palette,
                tuple(self.polygon_points),
                tuple(self.slope_region_points),
                self.slope_direction_start,
                self.slope_direction_end,
                self.selected_facility_type,
                self.facility_draw_shape,
                round(self.terrain_brush['height_m'], 2),
                self.terrain_brush_radius,
                round(self.terrain_3d_camera_yaw, 3),
                round(self.terrain_3d_camera_pitch, 3),
                round(float(getattr(self, 'terrain_3d_camera_focus_height', 0.0)), 3),
                self.selected_terrain_cell_key,
                tuple(sorted(self._terrain_selection_keys())),
                self.selected_wall_id,
                self.selected_terrain_id,
                self.terrain_overview_mouse_pos,
                self.mouse_world,
                self.drag_start,
                self.drag_current,
            )
            now_ms = pygame.time.get_ticks()
            needs_rebuild = self.terrain_3d_texture is None or self.terrain_3d_render_key != render_key
            allow_rebuild = True
            if needs_rebuild and (self.terrain_painting or self.terrain_pan_active):
                allow_rebuild = now_ms - self.terrain_3d_last_build_ms >= 70
            if self._terrain_scene_loading_active(game_engine):
                surface = self._build_terrain_scene_loading_surface(size)
                self.terrain_3d_texture = SdlTexture.from_surface(terrain_renderer, surface)
                self.terrain_3d_render_key = None
            elif needs_rebuild and allow_rebuild:
                surface = self._build_full_terrain_3d_surface(game_engine, size)
                self.terrain_3d_texture = SdlTexture.from_surface(terrain_renderer, surface)
                self.terrain_3d_render_key = render_key
                self.terrain_3d_last_build_ms = now_ms
            self.terrain_overview_ui['window_id'] = terrain_window.id

            if self.terrain_3d_texture is None:
                return
            terrain_renderer.clear()
            terrain_renderer.blit(self.terrain_3d_texture)
            terrain_renderer.present()
        except Exception:
            self.terrain_3d_window = None
            self.terrain_3d_renderer = None
            self.terrain_3d_texture = None
            self.terrain_3d_render_key = None

    def _build_full_terrain_3d_surface(self, game_engine, size):
        width, height = int(size[0]), int(size[1])
        surface = pygame.Surface((width, height))
        surface.fill((228, 233, 239))
        self.terrain_overview_ui = {'window_id': self.terrain_overview_ui.get('window_id'), 'buttons': [], 'map_rect': None, 'scene_rect': None, 'scene_map_rect': None, 'scene_world_rect': None}
        header_offset = max(0, int(self._render_terrain_overview_host_header(surface, game_engine, width) or 0))
        if self.terrain_view_mode == '3d':
            self._start_terrain_scene_prewarm(game_engine)

        map_manager = game_engine.map_manager
        grid_width, grid_height = map_manager._grid_dimensions()
        top_y = 162 + header_offset
        if self.terrain_editor_tool == 'terrain' and self.terrain_workflow_mode == 'shape':
            top_y += 34
        footer_gap = 34
        bottom_margin = 22
        available_vertical = max(460, height - top_y - footer_gap - bottom_margin)
        scene_height = max(220, min(420, int(available_vertical * 0.56)))
        scene_rect = pygame.Rect(18, top_y, width - 36, scene_height)
        self.terrain_overview_ui['scene_rect'] = scene_rect
        pygame.draw.rect(surface, (236, 240, 245), scene_rect, border_radius=12)
        pygame.draw.rect(surface, self.colors['panel_border'], scene_rect, 1, border_radius=12)
        scene_hint_text = '上半区左键按当前形状设置范围；3D 模式右键拖动旋转。'
        if self.terrain_view_mode == '2d':
            scene_hint_text = '上半区为 2D 顶视图，可直接框选或多边形圈定范围。'
        elif self.terrain_view_mode == '3d':
            scene_hint_text = '3D: WASD 平移中心，滚轮缩放，F/C 升降，按住右键旋转。'
        scene_hint = self.tiny_font.render(scene_hint_text, True, self.colors['panel_text'])
        surface.blit(scene_hint, (scene_rect.x + 10, scene_rect.y + 8))
        if self.terrain_view_mode == '3d':
            map_rgb = self._get_terrain_3d_map_rgb(map_manager)
            scene_surface = self._render_terrain_scene_surface(game_engine, scene_rect, map_rgb)
        else:
            scene_surface = self._render_terrain_scene_2d_surface(game_engine, scene_rect)
        if scene_surface is not None:
            if scene_surface.get_size() != scene_rect.size:
                scene_surface = pygame.transform.smoothscale(scene_surface, scene_rect.size)
            surface.blit(scene_surface, scene_rect.topleft)
            pygame.draw.rect(surface, self.colors['panel_border'], scene_rect, 1, border_radius=12)
        self._draw_terrain_scene_controls(surface, scene_rect)

        zoom_label_rect = pygame.Rect(scene_rect.x + 12, scene_rect.bottom - 34, 70, 20)
        pygame.draw.rect(surface, self.colors['overlay_bg'], zoom_label_rect, border_radius=5)
        zoom_text = self.tiny_font.render(f'{self.terrain_scene_zoom:.1f}x', True, self.colors['white'])
        surface.blit(zoom_text, zoom_text.get_rect(center=zoom_label_rect.center))

        hover_scene_world = None
        if self.terrain_view_mode == '3d':
            hover_scene_world = self._terrain_overview_hover_world(game_engine)
            if hover_scene_world is not None:
                self._draw_terrain_scene_hover_panel(surface, scene_rect, map_manager, hover_scene_world)

        title = self.font.render('地图编辑总览', True, self.colors['panel_text'])
        surface.blit(title, (18, 16 + header_offset))
        subtitle = self.tiny_font.render('上半区 2D/3D 预览，下半区俯视编辑板。地图编辑统一在这一页完成。', True, self.colors['panel_text'])
        surface.blit(subtitle, (18, 46 + header_offset))
        if not self._terrain_overview_embedded_mode():
            close_button = pygame.Rect(width - 50, 14 + header_offset, 26, 26)
            self._draw_surface_button(surface, close_button, 'X', False)
            self.terrain_overview_ui['buttons'].append((close_button, 'terrain_window_close'))

        row1_y = 88 + header_offset
        row2_y = row1_y + 34
        row3_y = row2_y + 34
        terrain_button = pygame.Rect(18, row1_y, 88, 28)
        facility_button = pygame.Rect(114, row1_y, 88, 28)
        view_2d_button = pygame.Rect(218, row1_y, 56, 28)
        view_3d_button = pygame.Rect(280, row1_y, 56, 28)
        prev_button = pygame.Rect(348, row1_y, 28, 28)
        next_button = pygame.Rect(382, row1_y, 28, 28)
        self._draw_surface_button(surface, terrain_button, '地形刷', self.terrain_editor_tool == 'terrain')
        self._draw_surface_button(surface, facility_button, '设施', self.terrain_editor_tool == 'facility')
        self._draw_surface_button(surface, view_2d_button, '2D', self.terrain_view_mode == '2d')
        self._draw_surface_button(surface, view_3d_button, '3D', self.terrain_view_mode == '3d')
        self.terrain_overview_ui['buttons'].extend([
            (terrain_button, 'terrain_tool:terrain'),
            (facility_button, 'terrain_tool:facility'),
            (view_2d_button, 'terrain_view_mode:2d'),
            (view_3d_button, 'terrain_view_mode:3d'),
        ])

        if self.terrain_view_mode == '3d':
            backend_follow_button = pygame.Rect(width - 188, row2_y, 74, 28)
            backend_opengl_button = pygame.Rect(width - 106, row2_y, 88, 28)
            backend_mode = self._terrain_editor_backend_mode()
            self._draw_surface_button(surface, backend_follow_button, '跟随设置', backend_mode == 'follow_settings')
            self._draw_surface_button(surface, backend_opengl_button, 'OpenGL', backend_mode == 'editor_opengl')
            self.terrain_overview_ui['buttons'].extend([
                (backend_follow_button, 'terrain_editor_backend:follow_settings'),
                (backend_opengl_button, 'terrain_editor_backend:editor_opengl'),
            ])

        if self.terrain_editor_tool == 'terrain':
            radius_minus_rect = pygame.Rect(348, row1_y, 28, 28)
            radius_value_rect = pygame.Rect(382, row1_y, 76, 28)
            radius_plus_rect = pygame.Rect(464, row1_y, 28, 28)
            self._draw_surface_button(surface, radius_minus_rect, '-', False)
            self._draw_surface_button(surface, radius_value_rect, f'R {self.terrain_brush_radius}', False)
            self._draw_surface_button(surface, radius_plus_rect, '+', False)
            self.terrain_overview_ui['buttons'].extend([
                (radius_minus_rect, 'terrain_brush_radius:-1'),
                (radius_plus_rect, 'terrain_brush_radius:1'),
            ])

            height_minus_rect = pygame.Rect(504, row1_y, 28, 28)
            height_value_rect = pygame.Rect(538, row1_y, 92, 28)
            height_plus_rect = pygame.Rect(636, row1_y, 28, 28)
            height_active = self._is_numeric_input_active('terrain_brush', 'brush')
            height_text = self.active_numeric_input['text'] if height_active and self.active_numeric_input is not None else f'{self.terrain_brush.get("height_m", 0.0):.2f}m'
            self._draw_surface_button(surface, height_minus_rect, '-', False)
            self._draw_surface_button(surface, height_plus_rect, '+', False)
            self._draw_surface_button(surface, height_value_rect, height_text, height_active)
            self.terrain_overview_ui['buttons'].extend([
                (height_minus_rect, 'terrain_brush_height:-0.1'),
                (height_value_rect, 'height_input:terrain_brush:brush'),
                (height_plus_rect, 'terrain_brush_height:0.1'),
            ])

            workflow_label = {
                'select': '框选',
                'brush': '刷子',
                'erase': '橡皮擦',
                'shape': '高级形状',
            }.get(getattr(self, 'terrain_workflow_mode', 'brush'), getattr(self, 'terrain_workflow_mode', 'brush'))
            shape_label = {'circle': '圆形', 'rect': '矩形', 'polygon': '多边形', 'line': '直线', 'slope': '斜坡', 'slope_plane': '斜面', 'smooth': 'Smooth', 'smooth_polygon': 'Smooth多边形'}.get(self.terrain_shape_mode, self.terrain_shape_mode)
            selected_label = f'工具 {workflow_label}  高度 {self.terrain_brush["height_m"]:.2f}m'
            if self.terrain_workflow_mode == 'shape':
                selected_label += f'  形状 {shape_label}'
        else:
            self._draw_surface_button(surface, prev_button, '<', False)
            self._draw_surface_button(surface, next_button, '>', False)
            self.terrain_overview_ui['buttons'].extend([
                (prev_button, 'facility_select_delta:-1'),
                (next_button, 'facility_select_delta:1'),
            ])
            shape_label = {'circle': '圆形', 'rect': '矩形', 'polygon': '多边形'}.get(self.facility_draw_shape, self.facility_draw_shape)
            selected_region = self._selected_region_option()
            if selected_region is not None and selected_region['type'] == 'wall':
                shape_label = '线段'
            selected_label = f"{selected_region['label'] if selected_region is not None else '-'} / {shape_label}"
        selection_text = self.small_font.render(f'当前: {selected_label}', True, self.colors['panel_text'])
        surface.blit(selection_text, (18, 66 + header_offset))
        if self.terrain_editor_tool == 'facility':
            selected_region = game_engine.map_manager.get_facility_by_id(self.selected_terrain_id) if self.selected_terrain_id else None
            if selected_region is not None and selected_region.get('type') in {'base', 'outpost', 'energy_mechanism', 'dog_hole'}:
                selected_facility_text = (
                    f"已选设施: {selected_region.get('id', '-')}  "
                    f"高度 {float(selected_region.get('height_m', self._facility_model_param_default(selected_region, 'height_m'))):.2f}m  "
                    f"朝向 {float(selected_region.get('yaw_deg', self._facility_model_param_default(selected_region, 'yaw_deg'))):.1f}°"
                )
                selected_facility_render = self.tiny_font.render(selected_facility_text, True, self.colors['panel_text'])
                surface.blit(selected_facility_render, (430, 68 + header_offset))

        if self.terrain_editor_tool == 'terrain':
            select_button = pygame.Rect(18, row2_y, 56, 28)
            brush_button = pygame.Rect(80, row2_y, 56, 28)
            erase_button = pygame.Rect(142, row2_y, 64, 28)
            shape_button = pygame.Rect(212, row2_y, 64, 28)
            self._draw_surface_button(surface, select_button, '框选', self.terrain_workflow_mode == 'select')
            self._draw_surface_button(surface, brush_button, '刷子', self.terrain_workflow_mode == 'brush')
            self._draw_surface_button(surface, erase_button, '橡皮擦', self.terrain_workflow_mode == 'erase')
            self._draw_surface_button(surface, shape_button, '高级', self.terrain_workflow_mode == 'shape')
            self.terrain_overview_ui['buttons'].extend([
                (select_button, 'terrain_workflow:select'),
                (brush_button, 'terrain_workflow:brush'),
                (erase_button, 'terrain_workflow:erase'),
                (shape_button, 'terrain_workflow:shape'),
            ])
            if self.terrain_workflow_mode == 'shape':
                circle_button = pygame.Rect(18, row3_y, 50, 28)
                rect_button = pygame.Rect(74, row3_y, 50, 28)
                polygon_button = pygame.Rect(130, row3_y, 58, 28)
                line_button = pygame.Rect(194, row3_y, 50, 28)
                slope_button = pygame.Rect(250, row3_y, 58, 28)
                slope_plane_button = pygame.Rect(314, row3_y, 58, 28)
                smooth_button = pygame.Rect(378, row3_y, 74, 28)
                smooth_polygon_button = pygame.Rect(458, row3_y, 102, 28)
                self._draw_surface_button(surface, circle_button, '圆形', self.terrain_shape_mode == 'circle')
                self._draw_surface_button(surface, rect_button, '矩形', self.terrain_shape_mode == 'rect')
                self._draw_surface_button(surface, polygon_button, '多边形', self.terrain_shape_mode == 'polygon')
                self._draw_surface_button(surface, line_button, '直线', self.terrain_shape_mode == 'line')
                self._draw_surface_button(surface, slope_button, '斜坡', self.terrain_shape_mode == 'slope')
                self._draw_surface_button(surface, slope_plane_button, '斜面', self.terrain_shape_mode == 'slope_plane')
                self._draw_surface_button(surface, smooth_button, 'Smooth', self.terrain_shape_mode == 'smooth')
                self._draw_surface_button(surface, smooth_polygon_button, '平滑多边形', self.terrain_shape_mode == 'smooth_polygon')
                self.terrain_overview_ui['buttons'].extend([
                    (circle_button, 'terrain_shape:circle'),
                    (rect_button, 'terrain_shape:rect'),
                    (polygon_button, 'terrain_shape:polygon'),
                    (line_button, 'terrain_shape:line'),
                    (slope_button, 'terrain_shape:slope'),
                    (slope_plane_button, 'terrain_shape:slope_plane'),
                    (smooth_button, 'terrain_shape:smooth'),
                    (smooth_polygon_button, 'terrain_shape:smooth_polygon'),
                ])
                if self.terrain_shape_mode in {'smooth', 'smooth_polygon'}:
                    strength_x = smooth_polygon_button.right + 8
                    for value in range(4):
                        strength_rect = pygame.Rect(strength_x, row3_y, 32, 28)
                        self._draw_surface_button(surface, strength_rect, str(value), self.terrain_smooth_strength == value)
                        self.terrain_overview_ui['buttons'].append((strength_rect, f'terrain_smooth_strength:set:{value}'))
                        strength_x = strength_rect.right + 6
                    confirm_rect = pygame.Rect(strength_x + 4, row3_y, 56, 28)
                    self._draw_surface_button(surface, confirm_rect, '确定', True)
                    self.terrain_overview_ui['buttons'].append((confirm_rect, 'terrain_smooth_confirm'))
        elif (self._selected_region_option() or {}).get('type') != 'wall':
            circle_button = pygame.Rect(18, row2_y, 64, 28)
            rect_button = pygame.Rect(88, row2_y, 64, 28)
            polygon_button = pygame.Rect(158, row2_y, 74, 28)
            self._draw_surface_button(surface, circle_button, '圆形', self.facility_draw_shape == 'circle')
            self._draw_surface_button(surface, rect_button, '矩形', self.facility_draw_shape == 'rect')
            self._draw_surface_button(surface, polygon_button, '多边形', self.facility_draw_shape == 'polygon')
            self.terrain_overview_ui['buttons'].extend([
                (circle_button, 'facility_shape:circle'),
                (rect_button, 'facility_shape:rect'),
                (polygon_button, 'facility_shape:polygon'),
            ])

        footer_y = scene_rect.bottom + 6
        footer_left_text = f'格栅: {grid_width} x {grid_height}    单元边长: {map_manager.terrain_grid_cell_size}px'
        footer = self.tiny_font.render(footer_left_text, True, self.colors['panel_text'])
        surface.blit(footer, (18, footer_y))

        backend = self._get_terrain_scene_backend()
        badge_text = self._terrain_backend_badge_text(backend)
        max_badge_width = max(120, scene_rect.width - footer.get_width() - 80)
        badge_text = self._fit_text_to_width(self.tiny_font, badge_text, max_badge_width - 18)
        badge_width = min(max_badge_width, self.tiny_font.size(badge_text)[0] + 18)
        badge_rect = pygame.Rect(scene_rect.right - badge_width, scene_rect.bottom + 2, badge_width, 22)
        pygame.draw.rect(surface, self.colors['panel_row'], badge_rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], badge_rect, 1, border_radius=6)
        badge_render = self.tiny_font.render(badge_text, True, self.colors['panel_text'])
        surface.blit(badge_render, badge_render.get_rect(center=badge_rect.center))
        mini_fps = getattr(self, '_render_mini_fps_label', None)
        if callable(mini_fps):
            mini_fps(surface, game_engine, anchor='bottom_left', inset=10)

        editor_top = footer_y + 28
        editor_rect = pygame.Rect(18, editor_top, width - 36, max(220, height - editor_top - bottom_margin))
        self._render_terrain_overview_editor(surface, game_engine, editor_rect)
        hover_world = hover_scene_world if hover_scene_world is not None else self._terrain_overview_hover_world(game_engine)
        if hover_world is not None:
            hovered_region = self._hover_region_at_world(map_manager, hover_world)
            if hovered_region is not None:
                self.mouse_world = hover_world
                self._draw_region_hover_card(surface, hovered_region, self.terrain_overview_mouse_pos, clamp_rect=surface.get_rect(), map_manager=map_manager, world_pos=hover_world)
        return surface

    def _draw_surface_button(self, surface, rect, label, active):
        pygame.draw.rect(surface, self.colors['toolbar_button_active'] if active else self.colors['toolbar_button'], rect, border_radius=6)
        font = self.tiny_font if len(label) > 2 else self.small_font
        text = font.render(label, True, self.colors['white'])
        surface.blit(text, text.get_rect(center=rect.center))

    def _get_terrain_3d_map_rgb(self, map_manager):
        source = map_manager.map_image or map_manager.map_surface
        if source is None:
            self.terrain_3d_map_rgb_cache_key = None
            self.terrain_3d_map_rgb_cache = None
            return None
        cache_key = (id(source), source.get_width(), source.get_height())
        if self.terrain_3d_map_rgb_cache_key != cache_key:
            self.terrain_3d_map_rgb_cache = np.transpose(pygame.surfarray.array3d(source), (1, 0, 2))
            self.terrain_3d_map_rgb_cache_key = cache_key
        return self.terrain_3d_map_rgb_cache

    def _render_terrain_scene_2d_surface(self, game_engine, scene_rect):
        surface = pygame.Surface(scene_rect.size)
        surface.fill((236, 240, 245))
        map_manager = game_engine.map_manager
        show_terrain = getattr(self, 'terrain_editor_tool', 'terrain') == 'terrain'
        source = map_manager.map_image or map_manager.map_surface
        if source is None:
            self.terrain_overview_ui['scene_map_rect'] = None
            self.terrain_overview_ui['scene_world_rect'] = None
            return surface

        padding = 10
        content_rect = pygame.Rect(padding, padding, max(1, scene_rect.width - padding * 2), max(1, scene_rect.height - padding * 2))
        aspect = source.get_width() / max(source.get_height(), 1)
        zoom = max(1.0, float(getattr(self, 'terrain_scene_zoom', 1.0)))
        if zoom <= 1.001:
            draw_width = content_rect.width
            draw_height = int(draw_width / max(aspect, 1e-6))
            if draw_height > content_rect.height:
                draw_height = content_rect.height
                draw_width = int(draw_height * aspect)
            map_rect = pygame.Rect(
                content_rect.x + (content_rect.width - draw_width) // 2,
                content_rect.y + (content_rect.height - draw_height) // 2,
                draw_width,
                draw_height,
            )
            view_width = float(map_manager.map_width)
            view_height = float(map_manager.map_height)
        else:
            map_rect = content_rect.copy()
            cover_scale = max(content_rect.width / max(float(map_manager.map_width), 1.0), content_rect.height / max(float(map_manager.map_height), 1.0))
            effective_scale = max(cover_scale * zoom, 1e-6)
            view_width = max(1.0, min(float(map_manager.map_width), content_rect.width / effective_scale))
            view_height = max(1.0, min(float(map_manager.map_height), content_rect.height / effective_scale))
        focus_world = getattr(self, 'terrain_scene_focus_world', None)
        if focus_world is None:
            center_x = map_manager.map_width / 2.0
            center_y = map_manager.map_height / 2.0
        else:
            center_x = float(focus_world[0])
            center_y = float(focus_world[1])
        view_x = max(0.0, min(max(0.0, map_manager.map_width - view_width), center_x - view_width / 2.0))
        view_y = max(0.0, min(max(0.0, map_manager.map_height - view_height), center_y - view_height / 2.0))
        source_x = int(round(view_x))
        source_y = int(round(view_y))
        source_w = max(1, min(source.get_width() - source_x, int(round(view_width))))
        source_h = max(1, min(source.get_height() - source_y, int(round(view_height))))
        source_rect = pygame.Rect(source_x, source_y, source_w, source_h)
        surface.blit(pygame.transform.smoothscale(source.subsurface(source_rect), map_rect.size), map_rect.topleft)
        pygame.draw.rect(surface, self.colors['panel_border'], map_rect, 1)
        self.terrain_overview_ui['scene_map_rect'] = map_rect.move(scene_rect.x, scene_rect.y)
        self.terrain_overview_ui['scene_world_rect'] = (view_x, view_y, view_width, view_height)

        scale_x = map_rect.width / max(view_width, 1e-6)
        scale_y = map_rect.height / max(view_height, 1e-6)
        if show_terrain:
            self._render_terrain_grid_overlay(surface, map_manager, map_rect, view_x=view_x, view_y=view_y, view_width=view_width, view_height=view_height)
        facility_overlay = self._get_world_facility_overlay_surface(map_manager)
        facility_source_rect = pygame.Rect(source_x, source_y, source_w, source_h)
        surface.blit(pygame.transform.smoothscale(facility_overlay.subsurface(facility_source_rect), map_rect.size), map_rect.topleft)

        terrain_selection = self._terrain_selection_keys()
        for key in terrain_selection:
            grid_x, grid_y = map_manager._decode_terrain_cell_key(key)
            x1, y1, x2, y2 = map_manager._grid_cell_bounds(grid_x, grid_y)
            selected_rect = pygame.Rect(
                map_rect.x + int((x1 - view_x) * scale_x),
                map_rect.y + int((y1 - view_y) * scale_y),
                max(1, int((x2 - x1 + 1) * scale_x)),
                max(1, int((y2 - y1 + 1) * scale_y)),
            )
            pygame.draw.rect(surface, (*self.colors['yellow'], 110), selected_rect)
            pygame.draw.rect(surface, self.colors['yellow'], selected_rect, 2)

        if self.drag_start is not None and self.drag_current is not None:
            start = (map_rect.x + int((self.drag_start[0] - view_x) * scale_x), map_rect.y + int((self.drag_start[1] - view_y) * scale_y))
            end = (map_rect.x + int((self.drag_current[0] - view_x) * scale_x), map_rect.y + int((self.drag_current[1] - view_y) * scale_y))
            if self._terrain_select_mode_active():
                preview_rect = pygame.Rect(min(start[0], end[0]), min(start[1], end[1]), abs(end[0] - start[0]), abs(end[1] - start[1]))
                pygame.draw.rect(surface, self.colors['yellow'], preview_rect, 2)
            elif self._terrain_shape_tool_active() and self.terrain_shape_mode == 'circle':
                pygame.draw.circle(surface, self.colors['yellow'], start, max(1, int(math.hypot(end[0] - start[0], end[1] - start[1]))), 2)
            elif self._terrain_shape_tool_active() and self.terrain_shape_mode == 'line':
                pygame.draw.line(surface, self.colors['yellow'], start, end, 3)
            elif self._facility_edit_active() and (self._selected_region_option() or {}).get('type') == 'wall':
                pygame.draw.line(surface, self.colors['yellow'], start, end, 3)
            else:
                preview_rect = pygame.Rect(min(start[0], end[0]), min(start[1], end[1]), abs(end[0] - start[0]), abs(end[1] - start[1]))
                pygame.draw.rect(surface, self.colors['yellow'], preview_rect, 2)

        polygon_preview_points = self._slope_preview_polygon_points() if self._terrain_shape_tool_active() and self.terrain_shape_mode == 'slope' else self.polygon_points
        if polygon_preview_points:
            preview_points = [(map_rect.x + int((point[0] - view_x) * scale_x), map_rect.y + int((point[1] - view_y) * scale_y)) for point in polygon_preview_points]
            preview_world = None
            if self.mouse_world is not None:
                if self._terrain_shape_tool_active() and self.terrain_shape_mode in {'polygon', 'smooth_polygon'}:
                    preview_world = self._current_terrain_target(self.mouse_world)
                elif self._terrain_shape_tool_active() and self.terrain_shape_mode in {'slope', 'slope_plane'} and not self._slope_direction_mode_active():
                    preview_world = self._current_terrain_target(self.mouse_world)
                elif self._facility_edit_active() and self.facility_draw_shape == 'polygon':
                    preview_world = self._current_facility_target(self.mouse_world)
            elif self.drag_current is not None:
                preview_world = self.drag_current
            if preview_world is not None:
                preview_points.append((map_rect.x + int((preview_world[0] - view_x) * scale_x), map_rect.y + int((preview_world[1] - view_y) * scale_y)))
            if len(preview_points) >= 2:
                pygame.draw.lines(surface, self.colors['yellow'], False, preview_points, 2)
            for point in preview_points[:len(polygon_preview_points)]:
                pygame.draw.circle(surface, self.colors['yellow'], point, 4)
        slope_preview_points = self._preview_slope_polygon_points()
        if len(slope_preview_points) >= 3:
            self._draw_slope_direction_arrow(
                surface,
                map_manager,
                slope_preview_points,
                lambda point: (
                    map_rect.x + int((point[0] - view_x) * scale_x),
                    map_rect.y + int((point[1] - view_y) * scale_y),
                ),
            )

        return surface

    def _render_terrain_overview_tool_panel(self, surface, game_engine, rect):
        pygame.draw.rect(surface, self.colors['panel_row'], rect, border_radius=8)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=8)
        y = rect.y + 10
        if self.terrain_editor_tool == 'terrain':
            lines = [
                '地形建模工具',
                '1/2/3/4: 框选 / 刷子 / 橡皮擦 / 高级',
                '滚轮改半径，Shift+滚轮改高度',
                '高级模式保留圆形、矩形、多边形、直线、斜坡、斜面、平滑多边形',
                '斜坡/斜面: 先闭合区域，再左键两次设置坡向箭头',
                '平滑: 先框选或画平滑多边形，再在工具栏设置 0-3，最后点确定',
            ]
            for index, line in enumerate(lines):
                font = self.small_font if index == 0 else self.tiny_font
                text = font.render(line, True, self.colors['panel_text'])
                surface.blit(text, (rect.x + 10, y))
                y += 20 if index == 0 else 18

            surface.blit(self.tiny_font.render(f'笔刷高度: {self.terrain_brush.get("height_m", 0.0):.2f}m（顶部工具栏设置）', True, self.colors['panel_text']), (rect.x + 10, y + 4))
            y += 28

            surface.blit(self.tiny_font.render(f'笔刷半径: {self.terrain_brush_radius}（顶部工具栏设置）', True, self.colors['panel_text']), (rect.x + 10, y + 4))
            y += 28

            terrain_selection = self._terrain_selection_keys()
            if terrain_selection:
                surface.blit(self.tiny_font.render(f'已选格栅: {len(terrain_selection)}', True, self.colors['panel_text']), (rect.x + 10, y))
                y += 18
                if len(terrain_selection) == 1 and self.selected_terrain_cell_key:
                    grid_x, grid_y = game_engine.map_manager._decode_terrain_cell_key(self.selected_terrain_cell_key)
                    selected_cell = game_engine.map_manager.terrain_grid_overrides.get(self.selected_terrain_cell_key)
                    if selected_cell is not None:
                        for line in (
                            f'坐标: ({grid_x}, {grid_y})',
                            f'高度: {selected_cell.get("height_m", 0.0):.2f}m',
                        ):
                            surface.blit(self.tiny_font.render(line, True, self.colors['panel_text']), (rect.x + 10, y))
                            y += 18
                delete_rect = pygame.Rect(rect.x + 10, y + 4, 104, 24)
                pygame.draw.rect(surface, self.colors['red'], delete_rect, border_radius=5)
                delete_text = self.tiny_font.render('删除选中地形', True, self.colors['white'])
                surface.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
                self.terrain_overview_ui['buttons'].append((delete_rect, 'delete_selected_terrain'))
                y += 34
        else:
            lines = [
                '设施放置参数',
                f'当前设施: {(self._selected_region_option() or {}).get("label", "-")}',
                '左键放置；拖白框或按中键拖动画面可移动主视角',
            ]
            for index, line in enumerate(lines):
                font = self.small_font if index == 0 else self.tiny_font
                text = font.render(line, True, self.colors['panel_text'])
                surface.blit(text, (rect.x + 10, y))
                y += 20 if index == 0 else 18

    def _draw_surface_input_box(self, surface, rect, text, active):
        pygame.draw.rect(surface, self.colors['white'], rect, border_radius=4)
        border_color = self.colors['toolbar_button_active'] if active else self.colors['panel_border']
        pygame.draw.rect(surface, border_color, rect, 2 if active else 1, border_radius=4)
        rendered = self.tiny_font.render(text or '0.00', True, self.colors['panel_text'])
        surface.blit(rendered, (rect.x + 8, rect.y + 4))

    def _draw_surface_toggle_row(self, surface, rect, label, enabled):
        pygame.draw.rect(surface, self.colors['panel_row_active'] if enabled else self.colors['panel'], rect, border_radius=4)
        text = self.tiny_font.render(label, True, self.colors['panel_text'])
        surface.blit(text, (rect.x + 8, rect.y + 5))

    def _draw_surface_scrollbar(self, surface, rect, visible_count, total_count, scroll_value):
        if total_count <= visible_count or rect.height <= 0:
            return
        track_rect = pygame.Rect(rect.right - 8, rect.y + 2, 6, rect.height - 4)
        pygame.draw.rect(surface, self.colors['panel_border'], track_rect, border_radius=3)
        thumb_height = max(20, int(track_rect.height * (visible_count / max(total_count, 1))))
        max_scroll = max(1, total_count - visible_count)
        travel = max(0, track_rect.height - thumb_height)
        thumb_y = track_rect.y + int((scroll_value / max_scroll) * travel)
        thumb_rect = pygame.Rect(track_rect.x, thumb_y, track_rect.width, thumb_height)
        pygame.draw.rect(surface, self.colors['toolbar_button_active'], thumb_rect, border_radius=3)

    def _render_scrollable_side_panel(self, surface, game_engine, rect, render_content, content_height):
        self.overview_side_panel_rect = rect
        content_height = max(rect.height, int(content_height))
        content_surface = pygame.Surface((rect.width, content_height), pygame.SRCALPHA)
        content_surface.fill((0, 0, 0, 0))

        original_buttons = self.terrain_overview_ui['buttons']
        self.terrain_overview_ui['buttons'] = []
        self.wall_panel_rect = None
        self.terrain_panel_rect = None

        render_content(content_surface, game_engine, pygame.Rect(0, 0, rect.width, content_height))

        local_buttons = self.terrain_overview_ui['buttons']
        self.terrain_overview_ui['buttons'] = original_buttons
        self.wall_panel_rect = None
        self.terrain_panel_rect = None

        self.overview_side_scroll_max = max(0, content_height - rect.height)
        self.overview_side_scroll = max(0, min(self.overview_side_scroll, self.overview_side_scroll_max))
        source_rect = pygame.Rect(0, self.overview_side_scroll, rect.width, rect.height)
        surface.blit(content_surface, rect.topleft, source_rect)

        for local_rect, action in local_buttons:
            global_rect = local_rect.move(rect.x, rect.y - self.overview_side_scroll)
            clipped_rect = global_rect.clip(rect)
            if clipped_rect.width > 0 and clipped_rect.height > 0:
                self.terrain_overview_ui['buttons'].append((clipped_rect, action))

        if self.overview_side_scroll_max > 0:
            scrollbar_rect = pygame.Rect(rect.right - 10, rect.y + 8, 8, rect.height - 16)
            self._draw_surface_scrollbar(surface, scrollbar_rect, rect.height, content_height, self.overview_side_scroll)

    def _render_surface_terrain_preview(self, surface, game_engine, rect):
        pygame.draw.rect(surface, self.colors['panel_row'], rect, border_radius=6)
        title = self.small_font.render('格栅 3D 预览', True, self.colors['panel_text'])
        surface.blit(title, (rect.x + 8, rect.y + 8))

        if self.mouse_world is not None:
            center_grid_x, center_grid_y = game_engine.map_manager._world_to_grid(self.mouse_world[0], self.mouse_world[1])
        else:
            grid_width, grid_height = game_engine.map_manager._grid_dimensions()
            center_grid_x = grid_width // 2
            center_grid_y = grid_height // 2

        tile_w = 16
        tile_h = 8
        height_scale = 14
        preview_origin_x = rect.x + rect.width // 2
        preview_origin_y = rect.y + rect.height - 22
        radius = 4
        grid_width, grid_height = game_engine.map_manager._grid_dimensions()

        cells = []
        for grid_y in range(max(0, center_grid_y - radius), min(grid_height, center_grid_y + radius + 1)):
            for grid_x in range(max(0, center_grid_x - radius), min(grid_width, center_grid_x + radius + 1)):
                x1, y1, x2, y2 = game_engine.map_manager._grid_cell_bounds(grid_x, grid_y)
                sample = game_engine.map_manager.sample_raster_layers((x1 + x2) / 2, (y1 + y2) / 2)
                cells.append((grid_x, grid_y, sample))

        cells.sort(key=lambda item: item[0] + item[1])
        for grid_x, grid_y, sample in cells:
            iso_x = preview_origin_x + (grid_x - center_grid_x - (grid_y - center_grid_y)) * tile_w / 2
            iso_y = preview_origin_y + (grid_x - center_grid_x + grid_y - center_grid_y) * tile_h / 2
            height_px = sample['height_m'] * height_scale
            top = [
                (iso_x, iso_y - tile_h - height_px),
                (iso_x + tile_w / 2, iso_y - height_px),
                (iso_x, iso_y + tile_h - height_px),
                (iso_x - tile_w / 2, iso_y - height_px),
            ]
            left = [
                (iso_x - tile_w / 2, iso_y - height_px),
                (iso_x, iso_y + tile_h - height_px),
                (iso_x, iso_y + tile_h),
                (iso_x - tile_w / 2, iso_y),
            ]
            right = [
                (iso_x + tile_w / 2, iso_y - height_px),
                (iso_x, iso_y + tile_h - height_px),
                (iso_x, iso_y + tile_h),
                (iso_x + tile_w / 2, iso_y),
            ]
            top_color = self._terrain_color_by_code(sample['terrain_code'])
            left_color = tuple(max(0, int(channel * 0.72)) for channel in top_color)
            right_color = tuple(max(0, int(channel * 0.86)) for channel in top_color)
            pygame.draw.polygon(surface, left_color, left)
            pygame.draw.polygon(surface, right_color, right)
            pygame.draw.polygon(surface, top_color, top)
            pygame.draw.polygon(surface, self.colors['panel_border'], top, 1)

    def _render_overview_terrain_side_panel(self, surface, game_engine, rect):
        pygame.draw.rect(surface, self.colors['panel_row'], rect, border_radius=8)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=8)
        y = rect.y + 10
        for index, line in enumerate([
            '地形建模工具',
            '框选: 左键拖框选择已编辑格栅',
            '刷子/橡皮擦: 左键连续涂抹；高级保留形状工具',
            '斜坡/斜面: 先闭合区域，再左键两次设置箭头方向',
            '平滑: 先框选区域，再在工具栏设置 0-3，其中 0 为关闭',
        ]):
            font = self.small_font if index == 0 else self.tiny_font
            surface.blit(font.render(line, True, self.colors['panel_text']), (rect.x + 10, y))
            y += 20 if index == 0 else 18

        surface.blit(self.tiny_font.render('笔刷高度/半径请在上方工具栏直接调整。', True, self.colors['panel_text']), (rect.x + 10, y + 4))
        y += 28

        surface.blit(self.tiny_font.render(f'区域平滑强度: {self.terrain_smooth_strength}（工具栏设置，0=关闭）', True, self.colors['panel_text']), (rect.x + 10, y + 4))
        y += 30

        for line in (
            '地形刷不再区分类型，只控制高度与半径。',
            '墙体、补给区、堡垒等继续用“设施放置”编辑。',
            '统一地形默认可通行，用于快速塑形。',
        ):
            surface.blit(self.tiny_font.render(line, True, self.colors['panel_text']), (rect.x + 10, y))
            y += 18

        terrain_selection = self._terrain_selection_keys()
        if terrain_selection:
            surface.blit(self.tiny_font.render(f'已选中格栅: {len(terrain_selection)}', True, self.colors['panel_text']), (rect.x + 10, y))
            y += 18
            if len(terrain_selection) == 1 and self.selected_terrain_cell_key:
                grid_x, grid_y = game_engine.map_manager._decode_terrain_cell_key(self.selected_terrain_cell_key)
                selected_cell = game_engine.map_manager.terrain_grid_overrides.get(self.selected_terrain_cell_key)
                if selected_cell is not None:
                    for line in (
                        f'坐标: ({grid_x}, {grid_y})',
                        f'类型: {selected_cell.get("type", "flat")}',
                        f'高度: {selected_cell.get("height_m", 0.0):.2f}m',
                    ):
                        surface.blit(self.tiny_font.render(line, True, self.colors['panel_text']), (rect.x + 10, y))
                        y += 18
            delete_rect = pygame.Rect(rect.right - 118, y + 2, 104, 24)
            pygame.draw.rect(surface, self.colors['red'], delete_rect, border_radius=5)
            delete_text = self.tiny_font.render('删除选中地形', True, self.colors['white'])
            surface.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
            self.terrain_overview_ui['buttons'].append((delete_rect, 'delete_selected_terrain'))
            y += 34

        preview_rect = pygame.Rect(rect.x + 10, max(y + 4, rect.bottom - 144), rect.width - 20, min(136, rect.bottom - y - 12))
        if preview_rect.height >= 96:
            self._render_surface_terrain_preview(surface, game_engine, preview_rect)

    def _render_overview_facility_option_grid(self, surface, rect):
        if rect.width >= 320:
            cols = 4
        elif rect.width >= 240:
            cols = 3
        else:
            cols = 2
        row_height = 28
        col_gap = 8
        row_gap = 6
        col_width = max(72, (rect.width - col_gap * (cols - 1)) // cols)
        options = self._region_options()
        for index, facility in enumerate(options):
            col = index % cols
            row = index // cols
            button_rect = pygame.Rect(rect.x + col * (col_width + col_gap), rect.y + row * (row_height + row_gap), col_width, row_height)
            active = index == self._selected_region_index()
            pygame.draw.rect(surface, self.colors['panel_row_active'] if active else self.colors['panel'], button_rect, border_radius=5)
            label_text = self._fit_text_to_width(self.tiny_font, facility['label'], button_rect.width - 12)
            label = self.tiny_font.render(label_text, True, self.colors['panel_text'])
            surface.blit(label, label.get_rect(center=button_rect.center))
            self.terrain_overview_ui['buttons'].append((button_rect, f'facility:{index}'))
        rows = max(1, (len(options) + cols - 1) // cols)
        return rows * row_height + (rows - 1) * row_gap

    def _render_overview_wall_detail_panel(self, surface, game_engine, rect):
        walls = game_engine.map_manager.get_facility_regions('wall')
        self.wall_panel_rect = rect
        pygame.draw.rect(surface, self.colors['panel'], rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=6)
        surface.blit(self.small_font.render('已画墙', True, self.colors['panel_text']), (rect.x + 8, rect.y + 8))
        if not walls:
            surface.blit(self.tiny_font.render('当前还没有墙，先在场地上点击两点绘制。', True, self.colors['panel_text']), (rect.x + 8, rect.y + 36))
            self.selected_wall_id = None
            return

        wall_ids = [wall['id'] for wall in walls]
        if self.selected_wall_id not in wall_ids:
            self.selected_wall_id = wall_ids[0]

        list_height = max(48, min(82, rect.height - 126))
        list_rect = pygame.Rect(rect.x + 8, rect.y + 34, rect.width - 16, list_height)
        visible_count = max(1, list_height // 26)
        max_scroll = max(0, len(walls) - visible_count)
        self.wall_scroll = max(0, min(self.wall_scroll, max_scroll))
        for offset, wall in enumerate(walls[self.wall_scroll:self.wall_scroll + visible_count]):
            row_rect = pygame.Rect(list_rect.x, list_rect.y + offset * 26, list_rect.width - 10, 22)
            active = wall['id'] == self.selected_wall_id
            pygame.draw.rect(surface, self.colors['panel_row_active'] if active else self.colors['panel_row'], row_rect, border_radius=4)
            label = self.tiny_font.render(f"{wall['id']}  高 {wall.get('height_m', 1.0):.2f}m", True, self.colors['panel_text'])
            surface.blit(label, (row_rect.x + 8, row_rect.y + 4))
            self.terrain_overview_ui['buttons'].append((row_rect, f"wall_select:{wall['id']}"))
        self._draw_surface_scrollbar(surface, list_rect, visible_count, len(walls), self.wall_scroll)

        wall = game_engine.map_manager.get_facility_by_id(self.selected_wall_id)
        if wall is None:
            return

        y = list_rect.bottom + 8
        movement_rect = pygame.Rect(rect.x + 8, y, rect.width - 16, 24)
        vision_rect = pygame.Rect(rect.x + 8, y + 28, rect.width - 16, 24)
        self._draw_surface_toggle_row(surface, movement_rect, f"运动阻拦: {'开' if wall.get('blocks_movement', True) else '关'}", wall.get('blocks_movement', True))
        self._draw_surface_toggle_row(surface, vision_rect, f"视野阻拦: {'开' if wall.get('blocks_vision', True) else '关'}", wall.get('blocks_vision', True))
        self.terrain_overview_ui['buttons'].extend([
            (movement_rect, f"wall_toggle:{wall['id']}:movement"),
            (vision_rect, f"wall_toggle:{wall['id']}:vision"),
        ])

        surface.blit(self.tiny_font.render(f"墙高: {wall.get('height_m', 1.0):.2f}m", True, self.colors['panel_text']), (rect.x + 8, y + 64))
        input_rect = pygame.Rect(rect.right - 88, y + 60, 78, 22)
        active = self._is_numeric_input_active('wall', wall['id'])
        input_text = self.active_numeric_input['text'] if active and self.active_numeric_input is not None else f"{wall.get('height_m', 1.0):.2f}"
        self._draw_surface_input_box(surface, input_rect, input_text, active)
        self.terrain_overview_ui['buttons'].append((input_rect, f"height_input:wall:{wall['id']}"))
        hint_y = y + 88
        if hint_y + 14 <= rect.bottom - 32:
            surface.blit(self.tiny_font.render('点击输入，回车确认', True, self.colors['panel_text']), (rect.x + 8, hint_y))

        delete_y = min(rect.bottom - 30, y + 108)
        delete_rect = pygame.Rect(rect.right - 100, delete_y, 92, 24)
        pygame.draw.rect(surface, self.colors['red'], delete_rect, border_radius=5)
        delete_text = self.tiny_font.render('删除该墙', True, self.colors['white'])
        surface.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
        self.terrain_overview_ui['buttons'].append((delete_rect, f'delete_facility:{wall["id"]}'))

    def _render_overview_region_detail_panel(self, surface, game_engine, rect, selected_facility):
        regions = [
            region for region in game_engine.map_manager.get_facility_regions(selected_facility['type'])
            if region.get('type') == selected_facility['type']
        ]
        self.terrain_panel_rect = rect
        pygame.draw.rect(surface, self.colors['panel'], rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=6)
        surface.blit(self.small_font.render('地形高度', True, self.colors['panel_text']), (rect.x + 8, rect.y + 8))
        if not regions:
            surface.blit(self.tiny_font.render('当前类型还没有区域。矩形拖拽或多边形闭合后会出现在这里。', True, self.colors['panel_text']), (rect.x + 8, rect.y + 36))
            self.selected_terrain_id = None
            return

        region_ids = [region['id'] for region in regions]
        if self.selected_terrain_id not in region_ids:
            self.selected_terrain_id = region_ids[-1]

        list_height = max(48, min(82, rect.height - 126))
        list_rect = pygame.Rect(rect.x + 8, rect.y + 34, rect.width - 16, list_height)
        visible_count = max(1, list_height // 26)
        max_scroll = max(0, len(regions) - visible_count)
        self.terrain_scroll = max(0, min(self.terrain_scroll, max_scroll))
        for offset, region in enumerate(regions[self.terrain_scroll:self.terrain_scroll + visible_count]):
            row_rect = pygame.Rect(list_rect.x, list_rect.y + offset * 26, list_rect.width - 10, 22)
            active = region['id'] == self.selected_terrain_id
            pygame.draw.rect(surface, self.colors['panel_row_active'] if active else self.colors['panel_row'], row_rect, border_radius=4)
            shape_label = '多边形' if region.get('shape') == 'polygon' else '矩形'
            label = self.tiny_font.render(f"{region['id']}  {shape_label}  高 {region.get('height_m', 0.0):.2f}m", True, self.colors['panel_text'])
            surface.blit(label, (row_rect.x + 8, row_rect.y + 4))
            self.terrain_overview_ui['buttons'].append((row_rect, f"terrain_select:{region['id']}"))
        self._draw_surface_scrollbar(surface, list_rect, visible_count, len(regions), self.terrain_scroll)

        region = game_engine.map_manager.get_facility_by_id(self.selected_terrain_id)
        if region is None:
            return

        y = list_rect.bottom + 8
        surface.blit(self.tiny_font.render(f"形状: {'多边形' if region.get('shape') == 'polygon' else '矩形'}", True, self.colors['panel_text']), (rect.x + 8, y))
        surface.blit(self.tiny_font.render(f"地形高: {region.get('height_m', 0.0):.2f}m", True, self.colors['panel_text']), (rect.x + 8, y + 22))
        input_rect = pygame.Rect(rect.right - 88, y + 18, 78, 22)
        active = self._is_numeric_input_active('terrain', region['id'])
        input_text = self.active_numeric_input['text'] if active and self.active_numeric_input is not None else f"{region.get('height_m', 0.0):.2f}"
        self._draw_surface_input_box(surface, input_rect, input_text, active)
        self.terrain_overview_ui['buttons'].append((input_rect, f"height_input:terrain:{region['id']}"))
        hint_y = y + 48
        if hint_y + 14 <= rect.bottom - 54:
            surface.blit(self.tiny_font.render('点击输入，回车确认', True, self.colors['panel_text']), (rect.x + 8, hint_y))

        delete_y = min(rect.bottom - 56, y + 68)
        delete_rect = pygame.Rect(rect.right - 108, delete_y, 100, 24)
        pygame.draw.rect(surface, self.colors['red'], delete_rect, border_radius=5)
        delete_text = self.tiny_font.render('删除该地形', True, self.colors['white'])
        surface.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
        self.terrain_overview_ui['buttons'].append((delete_rect, f'delete_facility:{region["id"]}'))

        summary = f"顶点数: {len(region.get('points', []))}" if region.get('shape') == 'polygon' else f"范围: ({region['x1']}, {region['y1']}) -> ({region['x2']}, {region['y2']})"
        summary_y = delete_rect.bottom + 6
        if summary_y + 14 <= rect.bottom - 16:
            summary = self._fit_text_to_width(self.tiny_font, summary, rect.width - 16)
            surface.blit(self.tiny_font.render(summary, True, self.colors['panel_text']), (rect.x + 8, summary_y))
        if self.facility_draw_shape == 'polygon' and self.polygon_points and summary_y + 32 <= rect.bottom - 8:
            pending = f'当前多边形已记录 {len(self.polygon_points)} 个点'
            pending = self._fit_text_to_width(self.tiny_font, pending, rect.width - 16)
            surface.blit(self.tiny_font.render(pending, True, self.colors['panel_text']), (rect.x + 8, summary_y + 18))

    def _render_overview_facility_detail_panel(self, surface, game_engine, rect, selected_facility):
        facilities = [
            region for region in game_engine.map_manager.get_facility_regions(selected_facility['type'])
            if region.get('type') == selected_facility['type']
        ]
        self.terrain_panel_rect = rect
        pygame.draw.rect(surface, self.colors['panel'], rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=6)
        surface.blit(self.small_font.render('已放置设施', True, self.colors['panel_text']), (rect.x + 8, rect.y + 8))
        if not facilities:
            surface.blit(self.tiny_font.render('当前类型还没有设施。拖拽矩形或闭合多边形后会出现在这里。', True, self.colors['panel_text']), (rect.x + 8, rect.y + 36))
            self.selected_terrain_id = None
            return

        facility_ids = [region['id'] for region in facilities]
        if self.selected_terrain_id not in facility_ids:
            self.selected_terrain_id = facility_ids[-1]

        compact_mode = rect.height < 170
        list_height = max(26, min(108 if not compact_mode else 56, rect.height - (118 if not compact_mode else 86)))
        list_rect = pygame.Rect(rect.x + 8, rect.y + 34, rect.width - 16, list_height)
        visible_count = max(1, list_height // 26)
        max_scroll = max(0, len(facilities) - visible_count)
        self.terrain_scroll = max(0, min(self.terrain_scroll, max_scroll))
        for offset, facility in enumerate(facilities[self.terrain_scroll:self.terrain_scroll + visible_count]):
            row_rect = pygame.Rect(list_rect.x, list_rect.y + offset * 26, list_rect.width - 10, 22)
            active = facility['id'] == self.selected_terrain_id
            pygame.draw.rect(surface, self.colors['panel_row_active'] if active else self.colors['panel_row'], row_rect, border_radius=4)
            shape_label = '多边形' if facility.get('shape') == 'polygon' else '矩形'
            label = self._fit_text_to_width(self.tiny_font, f"{facility['id']}  {shape_label}", row_rect.width - 12)
            surface.blit(self.tiny_font.render(label, True, self.colors['panel_text']), (row_rect.x + 8, row_rect.y + 4))
            self.terrain_overview_ui['buttons'].append((row_rect, f"terrain_select:{facility['id']}"))
        self._draw_surface_scrollbar(surface, list_rect, visible_count, len(facilities), self.terrain_scroll)

        facility = game_engine.map_manager.get_facility_by_id(self.selected_terrain_id)
        if facility is None:
            return

        y = list_rect.bottom + 8
        shape_text = '多边形' if facility.get('shape') == 'polygon' else '矩形'
        surface.blit(self.tiny_font.render(f"类型: {selected_facility['label']}", True, self.colors['panel_text']), (rect.x + 8, y))
        surface.blit(self.tiny_font.render(f"形状: {shape_text}", True, self.colors['panel_text']), (rect.x + 8, y + 20))
        if not compact_mode:
            surface.blit(self.tiny_font.render(f"队伍: {facility.get('team', 'neutral')}", True, self.colors['panel_text']), (rect.x + 8, y + 40))

        param_y = y + (60 if not compact_mode else 42)
        if facility.get('type') in {'base', 'outpost', 'energy_mechanism', 'dog_hole'} and not compact_mode:
            model_specs = [
                ('height_m', '高度'),
                ('yaw_deg', '朝向'),
                ('z_bottom_m', '离地'),
                ('model_scale', '缩放'),
            ]
            if facility.get('type') == 'dog_hole':
                model_specs = [
                    ('height_m', '高度'),
                    ('model_yaw_deg', '朝向'),
                    ('model_bottom_offset_m', '底面'),
                    ('model_clear_width_m', '净宽'),
                    ('model_clear_height_m', '净高'),
                    ('model_depth_m', '厚度'),
                    ('model_frame_thickness_m', '侧梁'),
                    ('model_top_beam_thickness_m', '上梁'),
                ]
            if facility.get('type') == 'energy_mechanism':
                model_specs.extend([
                    ('structure_base_length_m', '底座长'),
                    ('structure_base_width_m', '底座宽'),
                    ('structure_support_offset_m', '支架距'),
                    ('structure_cantilever_pair_gap_m', '臂间距'),
                ])
            for field_name, label_text in model_specs:
                if param_y + 22 > rect.bottom - 36:
                    break
                value = float(facility.get(field_name, self._facility_model_param_default(facility, field_name)))
                surface.blit(self.tiny_font.render(f'{label_text}:', True, self.colors['panel_text']), (rect.x + 8, param_y + 4))
                input_rect = pygame.Rect(rect.right - 86, param_y, 78, 22)
                input_type = 'terrain' if field_name == 'height_m' else f'facility_param.{field_name}'
                active = self._is_numeric_input_active(input_type, facility['id'])
                input_text = self.active_numeric_input['text'] if active and self.active_numeric_input is not None else f'{value:.2f}'
                self._draw_surface_input_box(surface, input_rect, input_text, active)
                self.terrain_overview_ui['buttons'].append((input_rect, f'height_input:{input_type}:{facility["id"]}'))
                param_y += 24

        delete_y = min(rect.bottom - 32, max(param_y + 4, y + (64 if not compact_mode else 42)))
        delete_rect = pygame.Rect(rect.right - 108, delete_y, 100, 24)
        pygame.draw.rect(surface, self.colors['red'], delete_rect, border_radius=5)
        delete_text = self.tiny_font.render('删除该设施', True, self.colors['white'])
        surface.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
        self.terrain_overview_ui['buttons'].append((delete_rect, f'delete_facility:{facility["id"]}'))

        summary = f"顶点数: {len(facility.get('points', []))}" if facility.get('shape') == 'polygon' else f"范围: ({facility['x1']}, {facility['y1']}) -> ({facility['x2']}, {facility['y2']})"
        summary_y = delete_rect.bottom + 6
        if not compact_mode and summary_y + 14 <= rect.bottom - 16:
            summary = self._fit_text_to_width(self.tiny_font, summary, rect.width - 16)
            surface.blit(self.tiny_font.render(summary, True, self.colors['panel_text']), (rect.x + 8, summary_y))
        if not compact_mode and self.facility_draw_shape == 'polygon' and self.polygon_points and summary_y + 32 <= rect.bottom - 8:
            pending = self._fit_text_to_width(self.tiny_font, f'当前多边形已记录 {len(self.polygon_points)} 个点', rect.width - 16)
            surface.blit(self.tiny_font.render(pending, True, self.colors['panel_text']), (rect.x + 8, summary_y + 18))

    def _render_overview_facility_side_panel_content(self, surface, game_engine, rect):
        pygame.draw.rect(surface, self.colors['panel_row'], rect, border_radius=8)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=8)
        self.wall_panel_rect = None
        self.terrain_panel_rect = None
        y = rect.y + 10
        facility_rect = pygame.Rect(rect.x + 10, y, 76, 24)
        buff_rect = pygame.Rect(rect.x + 92, y, 76, 24)
        self._draw_surface_button(surface, facility_rect, '设施', self.region_palette == 'facility')
        self._draw_surface_button(surface, buff_rect, '增益', self.region_palette == 'buff')
        self.terrain_overview_ui['buttons'].extend([
            (facility_rect, 'region_palette:facility'),
            (buff_rect, 'region_palette:buff'),
        ])
        y += 32

        selected_facility = self._selected_region_option()
        if selected_facility is None:
            return
        wall_mode = selected_facility['type'] == 'wall'
        lines = [
            '设施放置参数',
            '矩形设施左键拖拽绘制' if not wall_mode and self.facility_draw_shape != 'polygon' else ('多边形左键逐点连接，右键/回车闭合' if not wall_mode else '墙体点一下起点，再点一下终点'),
            '右键删除当前光标下设施' if not wall_mode else '右键取消当前墙体或删除墙',
        ]
        for index, line in enumerate(lines):
            font = self.small_font if index == 0 else self.tiny_font
            surface.blit(font.render(line, True, self.colors['panel_text']), (rect.x + 10, y))
            y += 20 if index == 0 else 18

        if not wall_mode:
            rect_mode_rect = pygame.Rect(rect.x + 10, y, 64, 24)
            polygon_mode_rect = pygame.Rect(rect.x + 82, y, 74, 24)
            self._draw_surface_button(surface, rect_mode_rect, '矩形', self.facility_draw_shape == 'rect')
            self._draw_surface_button(surface, polygon_mode_rect, '多边形', self.facility_draw_shape == 'polygon')
            self.terrain_overview_ui['buttons'].extend([
                (rect_mode_rect, 'facility_shape:rect'),
                (polygon_mode_rect, 'facility_shape:polygon'),
            ])
            y += 34

        options_rect = pygame.Rect(rect.x + 10, y, rect.width - 20, max(28, rect.height - (y - rect.y) - 18))
        options_height = self._render_overview_facility_option_grid(surface, options_rect)
        y += options_height + 8

        detail_rect = pygame.Rect(rect.x + 10, y, rect.width - 20, rect.bottom - y - 10)
        if wall_mode:
            self._render_overview_wall_detail_panel(surface, game_engine, detail_rect)
        else:
            self._render_overview_facility_detail_panel(surface, game_engine, detail_rect, selected_facility)

    def _render_overview_facility_side_panel(self, surface, game_engine, rect):
        self._render_scrollable_side_panel(surface, game_engine, rect, self._render_overview_facility_side_panel_content, max(620, rect.height + 260))

    def _build_overview_view_rect(self, map_manager, map_rect):
        if self._terrain_overview_embedded_mode():
            return None
        if self.viewport is None:
            return None
        sidebar_width = self.panel_width if self.edit_mode != 'none' else 0
        content_rect = pygame.Rect(
            self.content_padding,
            self.toolbar_height + self.hud_height + self.content_padding,
            self.window_width - sidebar_width - self.content_padding * 2,
            self.window_height - self.toolbar_height - self.hud_height - self.content_padding * 2,
        )
        map_screen_rect = pygame.Rect(
            self.viewport['map_x'],
            self.viewport['map_y'],
            self.viewport['map_width'],
            self.viewport['map_height'],
        )
        visible_rect = map_screen_rect.clip(content_rect)
        if visible_rect.width <= 0 or visible_rect.height <= 0:
            return None
        scale = self.viewport['scale']
        world_x1 = max(0.0, (visible_rect.left - self.viewport['map_x']) / max(scale, 1e-6))
        world_y1 = max(0.0, (visible_rect.top - self.viewport['map_y']) / max(scale, 1e-6))
        world_x2 = min(map_manager.map_width, (visible_rect.right - self.viewport['map_x']) / max(scale, 1e-6))
        world_y2 = min(map_manager.map_height, (visible_rect.bottom - self.viewport['map_y']) / max(scale, 1e-6))
        scale_x = map_rect.width / max(map_manager.map_width, 1)
        scale_y = map_rect.height / max(map_manager.map_height, 1)
        return pygame.Rect(
            map_rect.x + int(world_x1 * scale_x),
            map_rect.y + int(world_y1 * scale_y),
            max(4, int((world_x2 - world_x1) * scale_x)),
            max(4, int((world_y2 - world_y1) * scale_y)),
        )

    def _overview_view_rect_handle_hit(self, pos):
        if self._terrain_overview_embedded_mode():
            return False
        view_rect = self.terrain_overview_ui.get('view_rect')
        if view_rect is None or not view_rect.collidepoint(pos):
            return False
        inner_rect = view_rect.inflate(-12, -12)
        return not inner_rect.collidepoint(pos)

    def _render_terrain_overview_editor(self, surface, game_engine, rect):
        pygame.draw.rect(surface, self.colors['panel'], rect, border_radius=10)
        pygame.draw.rect(surface, self.colors['panel_border'], rect, 1, border_radius=10)
        title = self.small_font.render('统一编辑板', True, self.colors['panel_text'])
        if self._terrain_overview_embedded_mode():
            hint_text = '下半区左键编辑，右键短按选中；这里直接就是主编辑区。'
        else:
            hint_text = '下半区滚轮缩放视图；左键编辑，右键短按选中，中键拖动主视角。'
        hint = self.tiny_font.render(hint_text, True, self.colors['panel_text'])
        surface.blit(title, (rect.x + 10, rect.y + 8))
        surface.blit(hint, (rect.x + 10, rect.y + 30))

        map_manager = game_engine.map_manager
        side_panel_width = min(460, max(360, int(rect.width * 0.40)))
        map_container_rect = pygame.Rect(rect.x + 10, rect.y + 54, rect.width - side_panel_width - 28, rect.height - 64)
        map_rect = map_container_rect.copy()
        source = map_manager.map_image or map_manager.map_surface
        if source is not None:
            aspect = source.get_width() / max(source.get_height(), 1)
            draw_width = map_rect.width
            draw_height = int(draw_width / max(aspect, 1e-6))
            if draw_height > map_rect.height:
                draw_height = map_rect.height
                draw_width = int(draw_height * aspect)
            map_rect = pygame.Rect(map_container_rect.x + (map_container_rect.width - draw_width) // 2, map_container_rect.y + (map_container_rect.height - draw_height) // 2, draw_width, draw_height)
            surface.blit(pygame.transform.smoothscale(source, map_rect.size), map_rect.topleft)
        pygame.draw.rect(surface, self.colors['panel_border'], map_rect, 1)
        self.terrain_overview_ui['map_rect'] = map_rect

        side_rect = pygame.Rect(map_container_rect.right + 8, rect.y + 54, side_panel_width, rect.height - 64)
        if self.terrain_editor_tool == 'terrain':
            self._render_scrollable_side_panel(surface, game_engine, side_rect, self._render_overview_terrain_side_panel, max(760, side_rect.height + 260))
        else:
            self._render_overview_facility_side_panel(surface, game_engine, side_rect)

        scale_x = map_rect.width / max(map_manager.map_width, 1)
        scale_y = map_rect.height / max(map_manager.map_height, 1)
        self._render_terrain_grid_overlay(surface, map_manager, map_rect)

        view_rect = self._build_overview_view_rect(map_manager, map_rect)
        self.terrain_overview_ui['view_rect'] = view_rect
        if view_rect is not None:
            pygame.draw.rect(surface, (*self.colors['white'], 40), view_rect)
            pygame.draw.rect(surface, self.colors['white'], view_rect, 2)

        for region in map_manager.get_facility_regions():
            if region.get('type') == 'boundary':
                continue
            color = self._wall_color(region) if region.get('type') == 'wall' else self._terrain_color_by_type(region.get('type', 'flat'))
            if region.get('shape') == 'line':
                start = (map_rect.x + int(region['x1'] * scale_x), map_rect.y + int(region['y1'] * scale_y))
                end = (map_rect.x + int(region['x2'] * scale_x), map_rect.y + int(region['y2'] * scale_y))
                pygame.draw.line(surface, color, start, end, max(2, int(region.get('thickness', 12) * min(scale_x, scale_y))))
            elif region.get('shape') == 'polygon':
                points = [(map_rect.x + int(point[0] * scale_x), map_rect.y + int(point[1] * scale_y)) for point in region.get('points', [])]
                if len(points) >= 3:
                    pygame.draw.polygon(surface, color, points, 2)
            else:
                facility_rect = pygame.Rect(
                    map_rect.x + int(region['x1'] * scale_x),
                    map_rect.y + int(region['y1'] * scale_y),
                    max(1, int((region['x2'] - region['x1']) * scale_x)),
                    max(1, int((region['y2'] - region['y1']) * scale_y)),
                )
                facility_type = str(region.get('type', ''))
                if facility_type == 'base':
                    cx = facility_rect.x + facility_rect.width // 2
                    cy = facility_rect.y + facility_rect.height // 2
                    points = [
                        (facility_rect.x + facility_rect.width * 0.26, facility_rect.y),
                        (facility_rect.x + facility_rect.width * 0.74, facility_rect.y),
                        (facility_rect.right, cy),
                        (facility_rect.x + facility_rect.width * 0.74, facility_rect.bottom),
                        (facility_rect.x + facility_rect.width * 0.26, facility_rect.bottom),
                        (facility_rect.x, cy),
                    ]
                    pygame.draw.polygon(surface, color, points, 2)
                    pygame.draw.rect(surface, color, pygame.Rect(cx - facility_rect.width * 0.18, cy - facility_rect.height * 0.08, facility_rect.width * 0.36, facility_rect.height * 0.16), 1)
                elif facility_type == 'outpost':
                    center = (facility_rect.x + facility_rect.width // 2, facility_rect.y + facility_rect.height // 2)
                    radius = max(4, min(facility_rect.width, facility_rect.height) // 2)
                    pygame.draw.circle(surface, color, center, radius, 2)
                    pygame.draw.circle(surface, color, center, max(2, radius - 5), 1)
                elif facility_type == 'energy_mechanism':
                    center = (facility_rect.x + facility_rect.width // 2, facility_rect.y + facility_rect.height // 2)
                    pygame.draw.rect(surface, color, facility_rect, 1)
                    pygame.draw.line(surface, color, (facility_rect.x, center[1]), (facility_rect.right, center[1]), 2)
                    for side in (-1, 1):
                        arm_center = (center[0], int(center[1] + side * facility_rect.height * 0.16))
                        pygame.draw.circle(surface, color, arm_center, max(3, min(facility_rect.width, facility_rect.height) // 8), 1)
                        for index in range(5):
                            angle = math.tau * index / 5.0
                            end = (
                                int(arm_center[0] + math.cos(angle) * facility_rect.width * 0.28),
                                int(arm_center[1] + math.sin(angle) * facility_rect.height * 0.28),
                            )
                            pygame.draw.line(surface, color, arm_center, end, 1)
                            pygame.draw.circle(surface, color, end, max(2, min(facility_rect.width, facility_rect.height) // 16), 1)
                else:
                    pygame.draw.rect(surface, color, facility_rect, 2)

        if self.terrain_overview_mouse_pos is not None:
            hover_world = self._terrain_overview_pos_to_world(self.terrain_overview_mouse_pos, map_manager)
            if hover_world is not None:
                self.mouse_world = hover_world

        terrain_selection = self._terrain_selection_keys()
        for key in terrain_selection:
            grid_x, grid_y = map_manager._decode_terrain_cell_key(key)
            x1, y1, x2, y2 = map_manager._grid_cell_bounds(grid_x, grid_y)
            selected_rect = pygame.Rect(
                map_rect.x + int(x1 * scale_x),
                map_rect.y + int(y1 * scale_y),
                max(1, int((x2 - x1 + 1) * scale_x)),
                max(1, int((y2 - y1 + 1) * scale_y)),
            )
            pygame.draw.rect(surface, (*self.colors['yellow'], 110), selected_rect)
            pygame.draw.rect(surface, self.colors['yellow'], selected_rect, 2)
        if self.mouse_world is not None and map_rect.collidepoint((map_rect.x + int(self.mouse_world[0] * scale_x), map_rect.y + int(self.mouse_world[1] * scale_y))):
            hover_x = map_rect.x + int(self.mouse_world[0] * scale_x)
            hover_y = map_rect.y + int(self.mouse_world[1] * scale_y)
            if self._terrain_brush_active():
                if self._terrain_shape_tool_active() and self.terrain_shape_mode in {'polygon', 'smooth_polygon'} and self.polygon_points:
                    points = [(map_rect.x + int(point[0] * scale_x), map_rect.y + int(point[1] * scale_y)) for point in self.polygon_points]
                    preview_world = self._current_terrain_target(self.mouse_world)
                    points.append((map_rect.x + int(preview_world[0] * scale_x), map_rect.y + int(preview_world[1] * scale_y)))
                    if len(points) >= 2:
                        pygame.draw.lines(surface, self.colors['yellow'], False, points, 2)
                elif self._terrain_shape_tool_active() and self.terrain_shape_mode in {'slope', 'slope_plane'} and self._slope_preview_polygon_points():
                    points = [(map_rect.x + int(point[0] * scale_x), map_rect.y + int(point[1] * scale_y)) for point in self._slope_preview_polygon_points()]
                    if not self._slope_direction_mode_active():
                        preview_world = self._current_terrain_target(self.mouse_world)
                        points.append((map_rect.x + int(preview_world[0] * scale_x), map_rect.y + int(preview_world[1] * scale_y)))
                    if len(points) >= 2:
                        pygame.draw.lines(surface, self.colors['yellow'], False, points, 2)
                elif self.drag_start is not None and self.drag_current is not None:
                    start = (map_rect.x + int(self.drag_start[0] * scale_x), map_rect.y + int(self.drag_start[1] * scale_y))
                    end = (map_rect.x + int(self.drag_current[0] * scale_x), map_rect.y + int(self.drag_current[1] * scale_y))
                    if self._terrain_select_mode_active():
                        preview_rect = pygame.Rect(min(start[0], end[0]), min(start[1], end[1]), abs(end[0] - start[0]), abs(end[1] - start[1]))
                        pygame.draw.rect(surface, self.colors['yellow'], preview_rect, 2)
                    elif self._terrain_shape_tool_active() and self.terrain_shape_mode == 'circle':
                        pygame.draw.circle(surface, self.colors['yellow'], start, max(1, int(math.hypot(end[0] - start[0], end[1] - start[1]))), 2)
                    elif self._terrain_shape_tool_active() and self.terrain_shape_mode == 'line':
                        pygame.draw.line(surface, self.colors['yellow'], start, end, 3)
                    else:
                        preview_rect = pygame.Rect(min(start[0], end[0]), min(start[1], end[1]), abs(end[0] - start[0]), abs(end[1] - start[1]))
                        pygame.draw.rect(surface, self.colors['yellow'], preview_rect, 2)
                else:
                    radius = max(2, int(self.terrain_brush_radius * map_manager.terrain_grid_cell_size * min(scale_x, scale_y)))
                    if self._terrain_paint_mode_active() or self._terrain_eraser_mode_active():
                        pygame.draw.circle(surface, self.colors['yellow'], (hover_x, hover_y), radius, 1)
            elif self._facility_edit_active() and self.drag_start is not None and self.drag_current is not None:
                start = (map_rect.x + int(self.drag_start[0] * scale_x), map_rect.y + int(self.drag_start[1] * scale_y))
                end = (map_rect.x + int(self.drag_current[0] * scale_x), map_rect.y + int(self.drag_current[1] * scale_y))
                if (self._selected_region_option() or {}).get('type') == 'wall':
                    pygame.draw.line(surface, self.colors['yellow'], start, end, 3)
                elif self.facility_draw_shape == 'polygon' and self.polygon_points:
                    points = [(map_rect.x + int(point[0] * scale_x), map_rect.y + int(point[1] * scale_y)) for point in self.polygon_points]
                    preview_world = self._current_facility_target(self.mouse_world) if self.mouse_world is not None else self.drag_current
                    if preview_world is not None:
                        end = (map_rect.x + int(preview_world[0] * scale_x), map_rect.y + int(preview_world[1] * scale_y))
                    points.append(end)
                    if len(points) >= 2:
                        pygame.draw.lines(surface, self.colors['yellow'], False, points, 2)
                elif self.facility_draw_shape == 'circle':
                    pygame.draw.circle(surface, self.colors['yellow'], start, max(1, int(math.hypot(end[0] - start[0], end[1] - start[1]))), 2)
                else:
                    preview_rect = pygame.Rect(min(start[0], end[0]), min(start[1], end[1]), abs(end[0] - start[0]), abs(end[1] - start[1]))
                    pygame.draw.rect(surface, self.colors['yellow'], preview_rect, 2)

        slope_preview_points = self._preview_slope_polygon_points()
        if len(slope_preview_points) >= 3:
            self._draw_slope_direction_arrow(
                surface,
                map_manager,
                slope_preview_points,
                lambda point: (
                    map_rect.x + int(point[0] * scale_x),
                    map_rect.y + int(point[1] * scale_y),
                ),
            )

    def _get_event_window_id(self, event):
        for attr in ('window', 'windowID', 'window_id'):
            if not hasattr(event, attr):
                continue
            value = getattr(event, attr)
            if hasattr(value, 'id'):
                return getattr(value, 'id')
            try:
                return int(value)
            except (TypeError, ValueError):
                return None
        return None

    def _terrain_overview_action_at(self, pos):
        for rect, action in self.terrain_overview_ui.get('buttons', []):
            if rect.collidepoint(pos):
                return action
        return None

    def _terrain_overview_pos_to_world(self, pos, map_manager):
        map_rect = self.terrain_overview_ui.get('map_rect')
        if map_rect is None or not map_rect.collidepoint(pos):
            return None
        local_x = (pos[0] - map_rect.x) / max(map_rect.width, 1)
        local_y = (pos[1] - map_rect.y) / max(map_rect.height, 1)
        world_x = max(0, min(map_manager.map_width - 1, int(local_x * map_manager.map_width)))
        world_y = max(0, min(map_manager.map_height - 1, int(local_y * map_manager.map_height)))
        return world_x, world_y

    def _terrain_overview_hover_world(self, game_engine):
        pos = self.terrain_overview_mouse_pos
        if pos is None:
            return None
        map_manager = game_engine.map_manager
        world = self._terrain_overview_pos_to_world(pos, map_manager)
        if world is not None:
            return world
        return self._terrain_scene_pos_to_world(pos, game_engine)

    def _drag_terrain_view_from_overview(self, pos, map_manager):
        world_pos = self._terrain_overview_pos_to_world(pos, map_manager)
        if world_pos is None:
            return
        self.mouse_world = world_pos
        self._set_terrain_view_center(map_manager, world_pos)

    def _terrain_scene_perspective_matrix(self, fov_y, aspect, near, far):
        return _terrain_scene_perspective_matrix(fov_y, aspect, near, far)

    def _terrain_scene_look_at(self, eye, target, up):
        return _terrain_scene_look_at(eye, target, up)

    def _terrain_scene_pos_to_world(self, pos, game_engine):
        scene_rect = self.terrain_overview_ui.get('scene_rect')
        if scene_rect is None or not scene_rect.collidepoint(pos):
            return None

        if self.terrain_view_mode == '2d':
            scene_map_rect = self.terrain_overview_ui.get('scene_map_rect')
            scene_world_rect = self.terrain_overview_ui.get('scene_world_rect')
            if scene_map_rect is None or scene_world_rect is None or not scene_map_rect.collidepoint(pos):
                return None
            map_manager = game_engine.map_manager
            local_x = (pos[0] - scene_map_rect.x) / max(scene_map_rect.width, 1)
            local_y = (pos[1] - scene_map_rect.y) / max(scene_map_rect.height, 1)
            view_x, view_y, view_width, view_height = scene_world_rect
            world_x = max(0, min(map_manager.map_width - 1, int(view_x + local_x * view_width)))
            world_y = max(0, min(map_manager.map_height - 1, int(view_y + local_y * view_height)))
            return world_x, world_y

        map_manager = game_engine.map_manager
        map_rgb = self._get_terrain_3d_map_rgb(map_manager)
        data = _sample_terrain_scene_data(self, map_manager, map_rgb)
        backend_name = getattr(self._get_terrain_scene_backend(), 'name', 'software')
        grid_width = data['grid_width']
        grid_height = data['grid_height']
        cell_size = data['cell_size']
        sampled_heights = data['sampled_heights']
        best = None

        if backend_name in {'moderngl', 'pyglet_moderngl'}:
            width, height = scene_rect.size
            vertical_scale = 0.82
            max_height = max(1.0, float(np.max(sampled_heights) * vertical_scale))
            camera_state = build_terrain_scene_camera_state(
                self,
                map_manager,
                scene_rect.size,
                grid_width,
                grid_height,
                max_height,
                scene_step=data.get('scene_step', 1),
            )
            inv_mvp = np.linalg.inv(camera_state['mvp']).astype('f4')
            ndc_x = ((pos[0] - scene_rect.x) / max(width, 1)) * 2.0 - 1.0
            ndc_y = 1.0 - ((pos[1] - scene_rect.y) / max(height, 1)) * 2.0
            near_clip = np.array([ndc_x, ndc_y, -1.0, 1.0], dtype='f4')
            far_clip = np.array([ndc_x, ndc_y, 1.0, 1.0], dtype='f4')
            near_world = inv_mvp @ near_clip
            far_world = inv_mvp @ far_clip
            if abs(float(near_world[3])) <= 1e-6 or abs(float(far_world[3])) <= 1e-6:
                return None
            near_world = near_world[:3] / near_world[3]
            far_world = far_world[:3] / far_world[3]
            ray_dir = far_world - near_world
            ray_length = np.linalg.norm(ray_dir)
            if ray_length <= 1e-6 or abs(float(ray_dir[1])) <= 1e-6:
                return None
            ray_dir /= ray_length
            center_offset_x = grid_width / 2.0
            center_offset_y = grid_height / 2.0
            distance_to_plane = -float(near_world[1]) / float(ray_dir[1])
            if distance_to_plane <= 0.0:
                return None
            hit = near_world + ray_dir * distance_to_plane
            grid_x = int(round(float(hit[0]) + center_offset_x - 0.5))
            grid_y = int(round(float(hit[2]) + center_offset_y - 0.5))
            grid_x = max(0, min(grid_width - 1, grid_x))
            grid_y = max(0, min(grid_height - 1, grid_y))
            terrain_plane_y = float(sampled_heights[grid_y, grid_x]) * vertical_scale
            distance_to_plane = (terrain_plane_y - float(near_world[1])) / float(ray_dir[1])
            if distance_to_plane > 0.0:
                hit = near_world + ray_dir * distance_to_plane
                grid_x = int(round(float(hit[0]) + center_offset_x - 0.5))
                grid_y = int(round(float(hit[2]) + center_offset_y - 0.5))
                grid_x = max(0, min(grid_width - 1, grid_x))
                grid_y = max(0, min(grid_height - 1, grid_y))
            best = (0.0, grid_x, grid_y)
        else:
            width, height = scene_rect.size
            zoom = max(1.0, float(getattr(self, 'terrain_scene_zoom', 1.0)))
            tile_w = max(4, min(48, int(width * 1.25 / max(grid_width + grid_height, 1) * zoom)))
            depth_scale = max(3.0, tile_w * 0.95)
            height_scale = max(10.0, tile_w * 4.4)
            origin_x = scene_rect.x + width // 2
            origin_y = scene_rect.y + height - 26
            yaw_cos = math.cos(self.terrain_3d_camera_yaw)
            yaw_sin = math.sin(self.terrain_3d_camera_yaw)
            pitch = max(0.18, min(1.15, self.terrain_3d_camera_pitch))
            center_offset_x, center_offset_y = _terrain_scene_focus_grid(self, map_manager, grid_width, grid_height)

            for grid_y in range(grid_height):
                for grid_x in range(grid_width):
                    local_x = grid_x - center_offset_x
                    local_y = grid_y - center_offset_y
                    rotated_x = local_x * yaw_cos - local_y * yaw_sin
                    depth = local_x * yaw_sin + local_y * yaw_cos
                    screen_x = origin_x + rotated_x * tile_w
                    screen_y = origin_y + depth * depth_scale * pitch - float(sampled_heights[grid_y, grid_x]) * height_scale
                    distance_sq = (screen_x - pos[0]) ** 2 + (screen_y - pos[1]) ** 2
                    if best is None or distance_sq < best[0]:
                        best = (distance_sq, grid_x, grid_y)

        if best is None:
            return None
        _, grid_x, grid_y = best
        world_x = min(map_manager.map_width - 1, float(grid_x) * float(cell_size) + float(cell_size) * 0.5)
        world_y = min(map_manager.map_height - 1, float(grid_y) * float(cell_size) + float(cell_size) * 0.5)
        return float(world_x), float(world_y)

    def _terrain_brush_height_step(self, delta):
        self.terrain_brush['height_m'] = round(max(0.0, min(5.0, self.terrain_brush.get('height_m', 0.0) + delta)), 2)

    def _handle_terrain_overview_event(self, event, game_engine):
        if self.edit_mode != 'terrain':
            return False
        embedded_mode = self._terrain_overview_embedded_mode()
        if not embedded_mode and self.terrain_3d_window is None:
            return False
        if not embedded_mode and self._get_event_window_id(event) != getattr(self.terrain_3d_window, 'id', None):
            return False

        close_events = {
            value for value in (
                getattr(pygame, 'WINDOWCLOSE', None),
                getattr(pygame, 'WINDOWCLOSED', None),
            ) if value is not None
        }
        if not embedded_mode and event.type in close_events:
            self._close_terrain_3d_window(exit_terrain_mode=False)
            return True

        if not embedded_mode and event.type == pygame.QUIT:
            self._close_terrain_3d_window(exit_terrain_mode=False)
            return True

        if event.type == pygame.KEYDOWN:
            if self._handle_numeric_input_keydown(event, game_engine):
                return True
            mods = pygame.key.get_mods()
            if event.key == pygame.K_z and mods & pygame.KMOD_CTRL and not (mods & pygame.KMOD_SHIFT) and hasattr(game_engine, 'undo_last_edit'):
                if game_engine.undo_last_edit():
                    self.map_cache_surface = None
                    self.map_cache_size = None
                    self.terrain_3d_texture = None
                    self.terrain_3d_render_key = None
                    return True
                return False
            if (((event.key == pygame.K_y) and mods & pygame.KMOD_CTRL) or ((event.key == pygame.K_z) and mods & pygame.KMOD_CTRL and mods & pygame.KMOD_SHIFT)) and hasattr(game_engine, 'redo_last_edit'):
                if game_engine.redo_last_edit():
                    self.map_cache_surface = None
                    self.map_cache_size = None
                    self.terrain_3d_texture = None
                    self.terrain_3d_render_key = None
                    return True
                return False
            if event.key == pygame.K_ESCAPE:
                if self.polygon_points or self.slope_region_points:
                    self._reset_slope_state()
                    self.drag_start = None
                    self.drag_current = None
                elif not embedded_mode:
                    self._close_terrain_3d_window(exit_terrain_mode=False)
                return True
            if event.key in {pygame.K_RETURN, pygame.K_KP_ENTER}:
                if self._facility_edit_active() and self.facility_draw_shape == 'polygon':
                    if len(self.polygon_points) >= 3:
                        self._commit_facility_polygon(game_engine)
                    return True
                if self._terrain_brush_active() and self.terrain_shape_mode == 'polygon':
                    if len(self.polygon_points) >= 3:
                        self._commit_terrain_polygon(game_engine)
                    return True
                if self._terrain_brush_active() and self.terrain_shape_mode == 'smooth_polygon':
                    if len(self.polygon_points) >= 3:
                        self._commit_terrain_smooth_polygon(game_engine)
                    return True
                if self._terrain_brush_active() and self.terrain_shape_mode == 'slope':
                    if self._slope_direction_mode_active():
                        self._commit_terrain_slope_polygon(game_engine)
                    elif len(self.polygon_points) >= 3:
                        self._begin_terrain_slope_direction(game_engine)
                    return True
                if self._terrain_brush_active() and self.terrain_shape_mode == 'smooth':
                    self._smooth_selected_terrain_cells(game_engine)
                    return True
            if self._terrain_brush_active():
                if event.key == pygame.K_LEFTBRACKET:
                    self.terrain_brush_radius = max(0, self.terrain_brush_radius - 1)
                    return True
                if event.key == pygame.K_RIGHTBRACKET:
                    self.terrain_brush_radius = min(8, self.terrain_brush_radius + 1)
                    return True
                if event.key in {pygame.K_MINUS, pygame.K_KP_MINUS}:
                    self._terrain_brush_height_step(-0.1)
                    return True
                if event.key in {pygame.K_EQUALS, pygame.K_KP_PLUS}:
                    self._terrain_brush_height_step(0.1)
                    return True
            return True

        if event.type == pygame.MOUSEWHEEL:
            pointer_pos = getattr(event, 'pos', None) or self.terrain_overview_mouse_pos
            scene_map_rect = self.terrain_overview_ui.get('scene_map_rect')
            scene_rect = self.terrain_overview_ui.get('scene_rect')
            if pointer_pos is not None:
                zoom_world = None
                if self.terrain_view_mode == '2d' and scene_map_rect is not None and scene_map_rect.collidepoint(pointer_pos):
                    zoom_world = self._terrain_scene_pos_to_world(pointer_pos, game_engine)
                elif self.terrain_view_mode == '3d' and scene_rect is not None and scene_rect.collidepoint(pointer_pos):
                    zoom_world = self._terrain_scene_pos_to_world(pointer_pos, game_engine)
                if zoom_world is not None:
                    self._zoom_terrain_view(game_engine.map_manager, event.y, focus_world=zoom_world)
                    self.map_cache_surface = None
                    self.map_cache_size = None
                    self._invalidate_terrain_scene_cache()
                    return True
            if pointer_pos is not None:
                if self.overview_side_panel_rect is not None and self.overview_side_panel_rect.collidepoint(pointer_pos) and self.overview_side_scroll_max > 0:
                    self.overview_side_scroll = max(0, min(self.overview_side_scroll_max, self.overview_side_scroll - event.y * 28))
                    return True
            if self._facility_edit_active() and pointer_pos is not None:
                if self.wall_panel_rect is not None and self.wall_panel_rect.collidepoint(pointer_pos):
                    wall_count = len(game_engine.map_manager.get_facility_regions('wall'))
                    max_scroll = max(0, wall_count - 3)
                    self.wall_scroll = max(0, min(max_scroll, self.wall_scroll - event.y))
                    return True
                if self.terrain_panel_rect is not None and self.terrain_panel_rect.collidepoint(pointer_pos):
                    selected_facility = self._selected_region_option()
                    if selected_facility is None:
                        return
                    region_count = len(game_engine.map_manager.get_facility_regions(selected_facility['type']))
                    max_scroll = max(0, region_count - 3)
                    self.terrain_scroll = max(0, min(max_scroll, self.terrain_scroll - event.y))
                    return True
            if self._terrain_brush_active():
                mods = pygame.key.get_mods()
                if mods & pygame.KMOD_SHIFT:
                    self._terrain_brush_height_step(event.y * 0.1)
                else:
                    self.terrain_brush_radius = max(0, min(8, self.terrain_brush_radius + event.y))
                return True
            return True

        if event.type == pygame.MOUSEMOTION:
            self.terrain_overview_mouse_pos = event.pos
            if self.terrain_3d_orbit_active and self.terrain_3d_orbit_last_pos is not None:
                delta_x = event.pos[0] - self.terrain_3d_orbit_last_pos[0]
                delta_y = event.pos[1] - self.terrain_3d_orbit_last_pos[1]
                if abs(delta_x) + abs(delta_y) > 0:
                    self.terrain_3d_orbit_dragged = True
                self.terrain_3d_camera_yaw += delta_x * 0.012
                self.terrain_3d_camera_pitch = max(0.18, min(1.15, self.terrain_3d_camera_pitch - delta_y * 0.006))
                self.terrain_3d_orbit_last_pos = event.pos
                self._invalidate_terrain_scene_cache()
                return True
            if self.terrain_overview_viewport_drag_active:
                self._drag_terrain_view_from_overview(event.pos, game_engine.map_manager)
                return True
            if self.terrain_pan_active and self.terrain_pan_origin is not None and self.terrain_pan_origin[0] == 'overview':
                self._handle_terrain_pan_motion(getattr(event, 'rel', (0, 0)))
                return True
            world_pos = self._terrain_overview_pos_to_world(event.pos, game_engine.map_manager)
            if world_pos is None:
                world_pos = self._terrain_scene_pos_to_world(event.pos, game_engine)
            if world_pos is not None:
                self.mouse_world = world_pos
                if self.drag_start is not None and self._facility_edit_active():
                    self.drag_current = self._current_facility_target(world_pos)
                elif self.drag_start is not None and self._terrain_brush_active():
                    self.drag_current = self._current_terrain_target(world_pos)
                if self._terrain_brush_active():
                    if self.terrain_painting:
                        self._paint_terrain_at(game_engine, world_pos)
                    elif self.terrain_erasing:
                        self._apply_terrain_erase(game_engine, world_pos)
            return True

        if event.type == pygame.MOUSEBUTTONDOWN:
            action = self._terrain_overview_action_at(event.pos)
            if self.active_numeric_input is not None:
                active_action = f"height_input:{self.active_numeric_input['type']}:{self.active_numeric_input['facility_id']}"
                if action is None or action != active_action:
                    self._commit_numeric_input(game_engine)
                    action = self._terrain_overview_action_at(event.pos)
            if action:
                self._execute_action(game_engine, action)
                return True
            scene_rect = self.terrain_overview_ui.get('scene_rect')
            if event.button == 3 and self.terrain_view_mode == '3d' and scene_rect is not None and scene_rect.collidepoint(event.pos):
                self.terrain_3d_orbit_active = True
                self.terrain_3d_orbit_dragged = False
                self.terrain_3d_orbit_last_pos = event.pos
                self.terrain_pan_origin = ('scene', event.pos, self._terrain_scene_pos_to_world(event.pos, game_engine))
                return True
            if event.button == 1 and self._overview_view_rect_handle_hit(event.pos):
                self.terrain_overview_viewport_drag_active = True
                self._drag_terrain_view_from_overview(event.pos, game_engine.map_manager)
                return True
            world_pos = self._terrain_overview_pos_to_world(event.pos, game_engine.map_manager)
            scene_world_pos = self._terrain_scene_pos_to_world(event.pos, game_engine)
            if world_pos is None:
                world_pos = scene_world_pos
            if world_pos is None:
                return True
            self.mouse_world = world_pos
            if event.button == 2:
                self.terrain_overview_viewport_drag_active = True
                self._drag_terrain_view_from_overview(event.pos, game_engine.map_manager)
                return True
            if event.button == 1:
                if self._terrain_brush_active():
                    self._handle_terrain_left_press(game_engine, world_pos)
                elif self._facility_edit_active():
                    self._handle_facility_left_press(game_engine, world_pos)
                return True
            if event.button == 3:
                self.terrain_pan_active = False
                self.terrain_pan_origin = ('overview', event.pos, world_pos)
                return True

        if event.type == pygame.MOUSEBUTTONUP:
            world_pos = self._terrain_overview_pos_to_world(event.pos, game_engine.map_manager)
            if world_pos is None:
                world_pos = self._terrain_scene_pos_to_world(event.pos, game_engine)
            if event.button == 3 and self.terrain_3d_orbit_active:
                orbit_was_dragged = self.terrain_3d_orbit_dragged
                self.terrain_3d_orbit_active = False
                self.terrain_3d_orbit_dragged = False
                self.terrain_3d_orbit_last_pos = None
                origin = self.terrain_pan_origin
                if not orbit_was_dragged and origin is not None and origin[2] is not None:
                    if self._terrain_brush_active():
                        self._handle_terrain_right_press(game_engine, origin[2])
                    elif self._facility_edit_active():
                        self._handle_facility_right_press(game_engine, origin[2])
                self.terrain_pan_origin = None
                return True
            if event.button in {1, 2}:
                if self.terrain_overview_viewport_drag_active:
                    self.terrain_overview_viewport_drag_active = False
                    return True
            if event.button == 1:
                if self._terrain_brush_active():
                    self.terrain_painting = False
                    self.last_terrain_paint_grid_key = None
                    self._handle_terrain_left_release(game_engine, world_pos)
                elif self._facility_edit_active():
                    self._handle_facility_left_release(game_engine, world_pos)
                return True
            if event.button == 3:
                origin = self.terrain_pan_origin
                if not self.terrain_pan_active and origin is not None:
                    origin_world = origin[2]
                    if origin_world is not None:
                        if self._terrain_brush_active():
                            self._handle_terrain_right_press(game_engine, origin_world)
                        elif self._facility_edit_active():
                            self._handle_facility_right_press(game_engine, origin_world)
                self.terrain_pan_active = False
                self.terrain_pan_origin = None
                return True
        return False
