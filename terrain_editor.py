#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import os
import re
import subprocess
import sys
import threading
from copy import deepcopy
from types import SimpleNamespace

from pygame_compat import pygame

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from core.config_manager import ConfigManager
from map.map_manager import MapManager
from rendering.renderer import Renderer


class TerrainEditorEngine:
    def __init__(self, config_path='config.json', settings_path=None):
        self.config_path = config_path
        self.config_manager = ConfigManager()
        self.config = self.config_manager.load_config(config_path, settings_path)
        self.config['_config_path'] = config_path
        self.settings_path = self.config.get('_settings_path', self.config_manager.default_settings_path(config_path))
        self.config['_settings_path'] = self.settings_path
        self.logs = []
        self.max_logs = 8
        self.running = False
        self.game_time = 0.0
        self.game_duration = 0.0
        self.score = {'red': 0, 'blue': 0}
        self.paused = False
        self.match_started = True
        self.entity_manager = SimpleNamespace(entities=[], get_entity=lambda _entity_id: None)
        self.rules_engine = SimpleNamespace(game_over=False, auto_aim_max_distance=0.0)
        self.preset_name = self._sanitize_preset_name(self.config.get('map', {}).get('preset', 'latest_map'))
        self.available_map_names = []
        self.map_manager = MapManager(self._build_editor_map_config())
        self.undo_stack = []
        self.redo_stack = []
        self.max_undo_steps = 50
        self._map_sync_lock = threading.Lock()
        self._map_sync_event = threading.Event()
        self._map_sync_stop_event = threading.Event()
        self._map_sync_generation = 0
        self._pending_map_sync_job = None
        self._map_sync_worker = threading.Thread(target=self._map_sync_worker_loop, name='terrain-editor-map-sync', daemon=True)
        self._map_sync_worker.start()
        self._save_lock = threading.Lock()
        self._save_event = threading.Event()
        self._save_stop_event = threading.Event()
        self._pending_save_job = None
        self._save_worker = threading.Thread(target=self._save_worker_loop, name='terrain-editor-map-save', daemon=True)
        self._save_worker.start()
        self._asset_export_thread = None
        self.refresh_available_maps()
        self.reload_map_manager()

    def _sanitize_preset_name(self, name):
        raw = str(name or 'latest_map').strip()
        sanitized = re.sub(r'[<>:"/\\|?*]+', '_', raw)
        return sanitized or 'latest_map'

    def _build_editor_map_config(self):
        editor_config = deepcopy(self.config)
        map_config = editor_config.setdefault('map', {})
        runtime_grid = dict(map_config.get('runtime_grid', {}) or {})
        runtime_grid['channels'] = {}
        runtime_grid['shape'] = []
        map_config['runtime_grid'] = runtime_grid
        return editor_config

    def reload_map_manager(self):
        with self._map_sync_lock:
            self._map_sync_generation += 1
            self._pending_map_sync_job = None
        old_map_manager = getattr(self, 'map_manager', None)
        if old_map_manager is not None and hasattr(old_map_manager, 'shutdown'):
            old_map_manager.shutdown()
        self.map_manager = MapManager(self._build_editor_map_config())
        self.map_manager.load_map()

    def shutdown(self):
        pending_save_job = None
        with self._save_lock:
            pending_save_job = self._pending_save_job
            self._pending_save_job = None
        if pending_save_job is not None:
            self._perform_save_job(pending_save_job)
        self._save_stop_event.set()
        self._save_event.set()
        save_worker = self._save_worker
        if save_worker is not None and save_worker.is_alive():
            save_worker.join(timeout=1.0)
        self._map_sync_stop_event.set()
        self._map_sync_event.set()
        worker = self._map_sync_worker
        if worker is not None and worker.is_alive():
            worker.join(timeout=1.0)
        if self.map_manager is not None and hasattr(self.map_manager, 'shutdown'):
            self.map_manager.shutdown()

    def refresh_available_maps(self):
        names = self.config_manager.list_map_presets(self.config_path)
        self.available_map_names = names or [self.preset_name]
        if self.preset_name not in self.available_map_names:
            self.available_map_names.append(self.preset_name)
            self.available_map_names.sort()

    def _copy_facility_regions(self):
        copied = []
        for region in self.map_manager.facilities:
            region_copy = dict(region)
            points = region_copy.get('points')
            if isinstance(points, list):
                region_copy['points'] = [list(point) if isinstance(point, (list, tuple)) else point for point in points]
            copied.append(region_copy)
        return copied

    def _build_map_metadata(self):
        map_config = self.config.setdefault('map', {})
        metadata = self._snapshot_metadata(map_config)
        metadata['width'] = int(self.map_manager.map_width)
        metadata['height'] = int(self.map_manager.map_height)
        metadata['source_width'] = int(self.map_manager.source_map_width)
        metadata['source_height'] = int(self.map_manager.source_map_height)
        metadata['strict_scale'] = bool(self.map_manager.strict_scale_enabled)
        metadata['coordinate_space'] = 'world'
        return metadata

    def _build_map_sync_payload_fast(self):
        return {
            'metadata': self._build_map_metadata(),
            'facilities': self.map_manager.export_facilities_config(),
            'terrain_grid': self.map_manager.export_terrain_grid_config(),
            'function_grid': self.map_manager.export_function_grid_config(),
            'runtime_grid': deepcopy(self.map_manager.export_runtime_grid_config()),
        }

    def _apply_map_sync_payload(self, payload):
        map_config = self.config.setdefault('map', {})
        for key, value in payload.get('metadata', {}).items():
            map_config[key] = deepcopy(value)
        map_config['facilities'] = payload.get('facilities', [])
        map_config['terrain_grid'] = payload.get('terrain_grid', {'cell_size': 8, 'cells': []})
        map_config['function_grid'] = payload.get('function_grid', {'cell_size': 8, 'cells': []})
        map_config['runtime_grid'] = payload.get('runtime_grid', {'resolution_m': 0.01, 'shape': [], 'channels': {}})
        map_config['coordinate_space'] = 'world'

    def _map_sync_worker_loop(self):
        while not self._map_sync_stop_event.is_set():
            self._map_sync_event.wait(0.2)
            if self._map_sync_stop_event.is_set():
                return
            self._map_sync_event.clear()
            with self._map_sync_lock:
                job = self._pending_map_sync_job
                self._pending_map_sync_job = None
                latest_generation = self._map_sync_generation
            if job is None:
                continue
            payload = self._build_map_sync_payload_fast()
            with self._map_sync_lock:
                if int(job) != self._map_sync_generation or latest_generation != self._map_sync_generation:
                    continue
                self._apply_map_sync_payload(payload)

    def queue_map_sync(self):
        with self._map_sync_lock:
            self._map_sync_generation += 1
            self._pending_map_sync_job = int(self._map_sync_generation)
        self._map_sync_event.set()

    def sync_map_config(self):
        payload = self._build_map_sync_payload_fast()
        with self._map_sync_lock:
            self._map_sync_generation += 1
            self._pending_map_sync_job = None
            self._apply_map_sync_payload(payload)

    def _snapshot_metadata(self, map_config):
        metadata_keys = (
            'image_path',
            'origin_x',
            'origin_y',
            'unit',
            'width',
            'height',
            'source_width',
            'source_height',
            'strict_scale',
            'field_length_m',
            'field_width_m',
            'coordinate_space',
        )
        return {key: deepcopy(map_config.get(key)) for key in metadata_keys if key in map_config}

    def _apply_snapshot(self, snapshot):
        map_config = self.config.setdefault('map', {})
        metadata = snapshot.get('metadata', {}) or {}
        for key, value in metadata.items():
            map_config[key] = deepcopy(value)
        map_config['facilities'] = deepcopy(snapshot.get('facilities', []))
        map_config['terrain_grid'] = deepcopy(snapshot.get('terrain_grid', {'cell_size': self.map_manager.terrain_grid_cell_size, 'cells': []}))
        map_config['function_grid'] = deepcopy(snapshot.get('function_grid', {'cell_size': self.map_manager.terrain_grid_cell_size, 'cells': []}))
        map_config['runtime_grid'] = deepcopy(snapshot.get('runtime_grid', {'resolution_m': 0.05, 'shape': [], 'channels': {}}))
        map_config['coordinate_space'] = 'world'

    def _trim_history(self):
        if len(self.undo_stack) > self.max_undo_steps:
            self.undo_stack = self.undo_stack[-self.max_undo_steps:]
        if len(self.redo_stack) > self.max_undo_steps:
            self.redo_stack = self.redo_stack[-self.max_undo_steps:]

    def capture_map_snapshot(self):
        return self._build_map_sync_payload_fast()

    def push_undo_snapshot(self, label='编辑'):
        snapshot = self.capture_map_snapshot()
        self.undo_stack.append({'label': str(label), 'snapshot': snapshot})
        self.redo_stack.clear()
        self._trim_history()

    def undo_last_edit(self):
        if not self.undo_stack:
            self.add_log('没有可撤销的地图编辑', 'system')
            return False
        current_snapshot = self.capture_map_snapshot()
        item = self.undo_stack.pop()
        self.redo_stack.append({'label': str(item.get('label', '编辑')), 'snapshot': current_snapshot})
        self._apply_snapshot(item['snapshot'])
        self.reload_map_manager()
        self._trim_history()
        self.add_log(f'已撤销: {item["label"]}', 'system')
        return True

    def redo_last_edit(self):
        if not self.redo_stack:
            self.add_log('没有可重做的地图编辑', 'system')
            return False
        current_snapshot = self.capture_map_snapshot()
        item = self.redo_stack.pop()
        self.undo_stack.append({'label': str(item.get('label', '编辑')), 'snapshot': current_snapshot})
        self._apply_snapshot(item['snapshot'])
        self.reload_map_manager()
        self._trim_history()
        self.add_log(f'已重做: {item["label"]}', 'system')
        return True

    def add_log(self, message, team='system'):
        self.logs.append({'message': str(message), 'team': team})
        if len(self.logs) > self.max_logs:
            self.logs.pop(0)

    def _build_save_job(self, preset_name, persist_settings=False):
        name = self._sanitize_preset_name(preset_name or self.preset_name)
        self.sync_map_config()
        self.preset_name = name
        self.config.setdefault('map', {})['preset'] = name
        self.refresh_available_maps()
        config_snapshot = deepcopy(self.config)
        settings_payload = None
        if persist_settings:
            settings_payload = self.config_manager.build_local_settings_payload(config_snapshot)
        preset_path = self.config_manager._map_folder_preset_path(name, self.config_path)
        return {
            'preset_name': name,
            'preset_path': preset_path,
            'config_path': self.config_path,
            'settings_path': self.settings_path,
            'config_snapshot': config_snapshot,
            'settings_payload': settings_payload,
            'persist_settings': bool(persist_settings),
        }

    def _queue_save_job(self, job):
        with self._save_lock:
            self._pending_save_job = job
        self._save_event.set()

    def _save_worker_loop(self):
        while not self._save_stop_event.is_set():
            self._save_event.wait(0.2)
            if self._save_stop_event.is_set():
                return
            self._save_event.clear()
            with self._save_lock:
                job = self._pending_save_job
                self._pending_save_job = None
            if job is None:
                continue
            self._perform_save_job(job)

    def _perform_save_job(self, job):
        worker_config_manager = ConfigManager()
        config_snapshot = deepcopy(job.get('config_snapshot', {}))
        temp_map_manager = MapManager(config_snapshot)
        try:
            preset_path = worker_config_manager.save_map_preset(
                job.get('preset_name'),
                config=config_snapshot,
                config_path=job.get('config_path', self.config_path),
                map_manager=temp_map_manager,
            )
            if job.get('persist_settings') and job.get('settings_payload') is not None:
                worker_config_manager.save_settings(job.get('settings_path', self.settings_path), payload=job.get('settings_payload'))
            saved_label = os.path.basename(preset_path) if preset_path else f"{job.get('preset_name')}.json"
            self.add_log(f'地图预设已后台保存: {saved_label}')
            if job.get('persist_settings'):
                self.add_log(f'主程序已切换到地图预设: {job.get("preset_name")}')
        except Exception as exc:
            self.add_log(f'地图保存失败: {exc}', 'system')
        finally:
            if hasattr(temp_map_manager, 'shutdown'):
                temp_map_manager.shutdown()

    def save_preset(self, preset_name=None):
        job = self._build_save_job(preset_name or self.preset_name, persist_settings=False)
        self._queue_save_job(job)
        saved_label = os.path.basename(job['preset_path']) if job.get('preset_path') else f"{job['preset_name']}.json"
        self.add_log(f'地图预设已加入后台保存: {saved_label}')
        return job['preset_path']

    def apply_preset(self, preset_name=None):
        job = self._build_save_job(preset_name or self.preset_name, persist_settings=True)
        self.config_manager.config = self.config
        self._queue_save_job(job)
        self.add_log(f'地图预设切换已加入后台保存: {job["preset_name"]}')

    def export_map_asset(self, preset_name=None):
        name = self._sanitize_preset_name(preset_name or self.preset_name)
        worker = getattr(self, '_asset_export_thread', None)
        if worker is not None and worker.is_alive():
            self.add_log('地图资产导出正在进行，请稍候', 'system')
            return False
        self.sync_map_config()
        self.preset_name = name
        self.config.setdefault('map', {})['preset'] = name
        self.refresh_available_maps()
        config_snapshot = deepcopy(self.config)
        self.add_log(f'已加入 3D 资产导出: {name}')
        worker = threading.Thread(
            target=self._export_map_asset_worker,
            args=(name, config_snapshot),
            name='terrain-editor-map-asset-export',
            daemon=True,
        )
        self._asset_export_thread = worker
        worker.start()
        return True

    def _export_map_asset_worker(self, preset_name, config_snapshot):
        workspace_root = os.path.dirname(os.path.abspath(self.config_path))
        script_path = os.path.join(workspace_root, 'build_robot_venue_map_asset.py')
        output_dir = os.path.join(workspace_root, 'robot_venue_map_asset')
        worker_config_manager = ConfigManager()
        temp_map_manager = MapManager(config_snapshot)
        try:
            preset_path = worker_config_manager.save_map_preset(
                preset_name,
                config=config_snapshot,
                config_path=self.config_path,
                map_manager=temp_map_manager,
            )
            command = [
                sys.executable,
                script_path,
                '--input',
                preset_path,
                '--output',
                output_dir,
            ]
            completed = subprocess.run(
                command,
                cwd=workspace_root,
                capture_output=True,
                text=True,
                encoding='utf-8',
                errors='replace',
                check=False,
            )
            if completed.returncode == 0:
                self.add_log(f'3D 资产导出完成: {os.path.join(workspace_root, "robot_venue_map_asset.zip")}')
            else:
                details = completed.stderr.strip() or completed.stdout.strip() or '未知错误'
                self.add_log(f'3D 资产导出失败: {details.splitlines()[-1]}', 'system')
        except Exception as exc:
            self.add_log(f'3D 资产导出失败: {exc}', 'system')
        finally:
            if hasattr(temp_map_manager, 'shutdown'):
                temp_map_manager.shutdown()

    def reload_preset(self, preset_name=None):
        name = self._sanitize_preset_name(preset_name or self.preset_name)
        preset_map = self.config_manager.load_map_preset(name, self.config_path)
        if not preset_map:
            self.add_log(f'未找到地图预设: {name}', 'system')
            return False
        current_map = self.config.setdefault('map', {})
        metadata_keys = [
            'image_path',
            'origin_x',
            'origin_y',
            'unit',
            'width',
            'height',
            'source_width',
            'source_height',
            'strict_scale',
            'field_length_m',
            'field_width_m',
            'coordinate_space',
        ]
        for key in metadata_keys:
            if key in preset_map:
                current_map[key] = preset_map[key]
        current_map['facilities'] = preset_map.get('facilities', [])
        current_map['terrain_grid'] = preset_map.get('terrain_grid', {'cell_size': current_map.get('terrain_grid', {}).get('cell_size', 8), 'cells': []})
        current_map['function_grid'] = preset_map.get('function_grid', {'cell_size': current_map.get('function_grid', {}).get('cell_size', current_map.get('terrain_grid', {}).get('cell_size', 8)), 'cells': []})
        current_map['runtime_grid'] = preset_map.get('runtime_grid', {})
        if preset_map.get('_preset_path'):
            current_map['_preset_path'] = preset_map.get('_preset_path')
        current_map['preset'] = name
        self.preset_name = name
        self.refresh_available_maps()
        self.undo_stack.clear()
        self.redo_stack.clear()
        self.reload_map_manager()
        self.add_log(f'已重载地图预设: {name}')
        return True

    def cycle_available_map(self, delta):
        self.refresh_available_maps()
        if not self.available_map_names:
            return None
        if self.preset_name not in self.available_map_names:
            self.preset_name = self.available_map_names[0]
            return self.preset_name
        current_index = self.available_map_names.index(self.preset_name)
        next_index = (current_index + int(delta)) % len(self.available_map_names)
        self.preset_name = self.available_map_names[next_index]
        return self.preset_name

    def run(self, renderer):
        self.running = True
        clock = pygame.time.Clock()
        self.add_log('独立地图编辑器已启动')

        while self.running:
            for event in pygame.event.get():
                if renderer.handle_event(event, self):
                    continue
                if event.type == pygame.QUIT:
                    self.running = False
                elif event.type == pygame.KEYDOWN and event.key == pygame.K_ESCAPE:
                    self.running = False

            renderer.render(self)
            clock.tick(max(30, int(self.config.get('simulator', {}).get('fps', 50))))

        self.shutdown()
        pygame.quit()


class TerrainEditorRenderer(Renderer):
    def __init__(self, game_engine, config):
        super().__init__(game_engine, config)
        pygame.display.set_caption('RoboMaster 地图编辑器')
        self.edit_mode = 'terrain'
        self.terrain_editor_tool = 'terrain'
        self.terrain_overview_window_open = True
        self.terrain_overview_embedded = True
        self.active_text_input = None

    def _update_viewport(self, map_manager):
        available_rect = self._terrain_available_rect()
        available_width = available_rect.width
        available_height = available_rect.height
        scale = self._terrain_effective_scale(map_manager)
        draw_width = int(map_manager.map_width * scale)
        draw_height = int(map_manager.map_height * scale)
        map_x = available_rect.x + (available_width - draw_width) // 2
        map_y = available_rect.y + (available_height - draw_height) // 2
        map_x += int(self.terrain_view_offset[0])
        map_y += int(self.terrain_view_offset[1])
        self.viewport = {
            'map_x': map_x,
            'map_y': map_y,
            'map_width': draw_width,
            'map_height': draw_height,
            'scale': scale,
            'sidebar_x': self.window_width,
        }

    def _terrain_available_rect(self):
        return pygame.Rect(
            self.content_padding,
            self.toolbar_height + self.hud_height + self.content_padding,
            self.window_width - self.content_padding * 2,
            self.window_height - self.toolbar_height - self.hud_height - self.content_padding * 2,
        )

    def _set_terrain_view_center(self, map_manager, world_pos):
        available_rect = self._terrain_available_rect()
        available_width = available_rect.width
        available_height = available_rect.height
        scale = self._terrain_effective_scale(map_manager)
        draw_width = int(map_manager.map_width * scale)
        draw_height = int(map_manager.map_height * scale)
        base_map_x = available_rect.x + (available_width - draw_width) // 2
        base_map_y = available_rect.y + (available_height - draw_height) // 2
        content_center_x = available_rect.x + available_width / 2
        content_center_y = available_rect.y + available_height / 2
        self.terrain_view_offset[0] = int(content_center_x - world_pos[0] * scale - base_map_x)
        self.terrain_view_offset[1] = int(content_center_y - world_pos[1] * scale - base_map_y)

    def _build_overview_view_rect(self, map_manager, map_rect):
        if self.viewport is None:
            return None
        content_rect = pygame.Rect(
            self.content_padding,
            self.toolbar_height + self.hud_height + self.content_padding,
            self.window_width - self.content_padding * 2,
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

    def render_sidebar(self, game_engine):
        return

    def render(self, game_engine):
        self._refresh_editor_state(game_engine)
        self._update_terrain_scene_navigation(game_engine)
        self.screen.fill(self.colors['bg'])
        self.toolbar_actions = []
        self.hud_actions = []
        self.panel_actions = []
        self._update_viewport(game_engine.map_manager)
        overview_surface = self._build_full_terrain_3d_surface(game_engine, (self.window_width, self.window_height))
        self.screen.blit(overview_surface, (0, 0))
        pygame.display.flip()

    def _render_terrain_overview_host_header(self, surface, game_engine, width):
        header_rect = pygame.Rect(0, 0, width, 60)
        pygame.draw.rect(surface, self.colors['toolbar'], header_rect)

        buttons = [
            ('保存预设', 'editor_save_preset', False),
            ('应用到主程序', 'editor_apply_preset', False),
            ('导出3D资产', 'editor_export_map_asset', False),
            ('重载预设', 'editor_reload_preset', False),
        ]
        x = 12
        for label, action, active in buttons:
            text = self.small_font.render(label, True, self.colors['toolbar_text'])
            rect = pygame.Rect(x, 12, text.get_width() + 24, 36)
            pygame.draw.rect(
                surface,
                self.colors['toolbar_button_active'] if active else self.colors['toolbar_button'],
                rect,
                border_radius=6,
            )
            surface.blit(text, text.get_rect(center=rect.center))
            self.terrain_overview_ui['buttons'].append((rect, action))
            x = rect.right + 8

        prev_map_rect = pygame.Rect(x, 12, 32, 36)
        next_map_rect = pygame.Rect(x + 244, 12, 32, 36)
        name_box_rect = pygame.Rect(x + 40, 12, 196, 36)
        self._draw_surface_button(surface, prev_map_rect, '<', False)
        self._draw_surface_button(surface, next_map_rect, '>', False)
        pygame.draw.rect(surface, self.colors['white'], name_box_rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['panel_border'], name_box_rect, 1, border_radius=6)
        map_name = game_engine.preset_name or 'unnamed'
        map_rendered = self.small_font.render(map_name, True, self.colors['panel_text'])
        surface.blit(map_rendered, map_rendered.get_rect(center=name_box_rect.center))
        self.terrain_overview_ui['buttons'].append((prev_map_rect, 'editor_prev_map'))
        self.terrain_overview_ui['buttons'].append((next_map_rect, 'editor_next_map'))
        x = next_map_rect.right + 12

        name_label = self.tiny_font.render('预设名', True, self.colors['toolbar_text'])
        surface.blit(name_label, (x + 4, 8))
        input_rect = pygame.Rect(x, 24, 200, 24)
        active = self.active_text_input is not None
        pygame.draw.rect(surface, self.colors['white'], input_rect, border_radius=6)
        pygame.draw.rect(surface, self.colors['toolbar_button_active'] if active else self.colors['panel_border'], input_rect, 2 if active else 1, border_radius=6)
        preset_text = self.active_text_input['text'] if self.active_text_input is not None else game_engine.preset_name
        rendered = self.small_font.render(preset_text or 'latest_map', True, self.colors['panel_text'])
        surface.blit(rendered, (input_rect.x + 10, input_rect.y + 3))
        self.terrain_overview_ui['buttons'].append((input_rect, 'editor_focus_preset_name'))

        hint = self.tiny_font.render('Ctrl+S 保存 | Ctrl+Shift+S 应用 | Ctrl+E 导出3D资产 | Ctrl+Z 撤销 | Ctrl+Y 重做 | Tab 切换地形/设施', True, self.colors['toolbar_text'])
        surface.blit(hint, (width - hint.get_width() - 12, 22))
        return header_rect.height

    def render_toolbar(self, game_engine):
        pygame.draw.rect(self.screen, self.colors['toolbar'], (0, 0, self.window_width, self.toolbar_height))
        buttons = [
            ('保存预设', 'editor_save_preset', False),
            ('应用到主程序', 'editor_apply_preset', False),
            ('导出3D资产', 'editor_export_map_asset', False),
            ('重载预设', 'editor_reload_preset', False),
            ('上一张', 'editor_prev_map', False),
            ('下一张', 'editor_next_map', False),
            ('总览窗口', 'editor_toggle_overview', self.terrain_overview_window_open),
            ('地形刷', 'terrain_tool:terrain', self.terrain_editor_tool == 'terrain'),
            ('设施放置', 'terrain_tool:facility', self.terrain_editor_tool == 'facility'),
        ]

        x = 10
        for label, action, active in buttons:
            text = self.small_font.render(label, True, self.colors['toolbar_text'])
            rect = pygame.Rect(x, 10, text.get_width() + 22, self.toolbar_height - 20)
            pygame.draw.rect(
                self.screen,
                self.colors['toolbar_button_active'] if active else self.colors['toolbar_button'],
                rect,
                border_radius=6,
            )
            self.screen.blit(text, (rect.x + 11, rect.y + (rect.height - text.get_height()) // 2))
            self.toolbar_actions.append((rect, action))
            x = rect.right + 8

        input_rect = pygame.Rect(x + 8, 10, 180, self.toolbar_height - 20)
        active = self.active_text_input is not None
        pygame.draw.rect(self.screen, self.colors['white'], input_rect, border_radius=6)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'] if active else self.colors['panel_border'], input_rect, 2 if active else 1, border_radius=6)
        preset_text = self.active_text_input['text'] if self.active_text_input is not None else game_engine.preset_name
        rendered = self.small_font.render(preset_text or 'latest_map', True, self.colors['panel_text'])
        self.screen.blit(rendered, (input_rect.x + 10, input_rect.y + 9))
        self.toolbar_actions.append((input_rect, 'editor_focus_preset_name'))

        hint = self.tiny_font.render('Ctrl+S 保存预设 | Ctrl+Shift+S 应用 | Ctrl+E 导出3D资产 | Ctrl+Z 撤销 | Ctrl+Y 重做 | Tab 切换工具', True, self.colors['toolbar_text'])
        self.screen.blit(hint, (self.window_width - hint.get_width() - 12, 19))

    def render_match_hud(self, game_engine):
        return

    def render_entities(self, entities):
        return

    def render_aim_fov(self, game_engine):
        return

    def render_robot_detail_popup(self, game_engine):
        return

    def render_overlay_status(self, game_engine):
        return

    def _current_preset_name(self, game_engine):
        if self.active_text_input is not None:
            return game_engine._sanitize_preset_name(self.active_text_input.get('text', ''))
        return game_engine.preset_name

    def _commit_text_input(self, game_engine):
        if self.active_text_input is None:
            return False
        game_engine.preset_name = game_engine._sanitize_preset_name(self.active_text_input.get('text', ''))
        self.active_text_input = None
        return True

    def _cancel_text_input(self):
        self.active_text_input = None

    def _handle_text_input_keydown(self, event, game_engine):
        if self.active_text_input is None:
            return False
        if event.key in {pygame.K_RETURN, pygame.K_KP_ENTER}:
            self._commit_text_input(game_engine)
            return True
        if event.key == pygame.K_ESCAPE:
            self._cancel_text_input()
            return True
        if event.key == pygame.K_BACKSPACE:
            self.active_text_input['text'] = self.active_text_input.get('text', '')[:-1]
            return True
        if event.key == pygame.K_DELETE:
            self.active_text_input['text'] = ''
            return True
        character = event.unicode
        if character and character.isprintable() and character not in '<>:"/\\|?*':
            self.active_text_input['text'] = self.active_text_input.get('text', '') + character
            return True
        return True

    def handle_event(self, event, game_engine):
        if event.type == pygame.KEYDOWN:
            if self._handle_text_input_keydown(event, game_engine):
                return True
            mods = pygame.key.get_mods()
            if event.key == pygame.K_TAB:
                self.terrain_editor_tool = 'facility' if self.terrain_editor_tool == 'terrain' else 'terrain'
                return True
            if event.key == pygame.K_s and mods & pygame.KMOD_CTRL and mods & pygame.KMOD_SHIFT:
                self._commit_text_input(game_engine)
                game_engine.apply_preset(self._current_preset_name(game_engine))
                return True
            if event.key == pygame.K_s and mods & pygame.KMOD_CTRL:
                self._commit_text_input(game_engine)
                game_engine.save_preset(self._current_preset_name(game_engine))
                return True
            if event.key == pygame.K_e and mods & pygame.KMOD_CTRL:
                self._commit_text_input(game_engine)
                game_engine.export_map_asset(self._current_preset_name(game_engine))
                return True
            if event.key in {pygame.K_F5, pygame.K_F9, pygame.K_p, pygame.K_r}:
                return True

        if event.type == pygame.MOUSEBUTTONDOWN and event.button == 1 and self.active_text_input is not None:
            action = self._terrain_overview_action_at(event.pos) if self.edit_mode == 'terrain' else self._resolve_click_action(event.pos)
            if action != 'editor_focus_preset_name':
                self._commit_text_input(game_engine)

        return super().handle_event(event, game_engine)

    def _execute_action(self, game_engine, action):
        if action == 'editor_focus_preset_name':
            self.active_text_input = {'text': game_engine.preset_name}
            return
        if action == 'editor_save_preset':
            self._commit_text_input(game_engine)
            game_engine.save_preset(self._current_preset_name(game_engine))
            return
        if action == 'editor_apply_preset':
            self._commit_text_input(game_engine)
            game_engine.apply_preset(self._current_preset_name(game_engine))
            return
        if action == 'editor_export_map_asset':
            self._commit_text_input(game_engine)
            game_engine.export_map_asset(self._current_preset_name(game_engine))
            return
        if action == 'editor_reload_preset':
            self._commit_text_input(game_engine)
            game_engine.reload_preset(self._current_preset_name(game_engine))
            self.map_cache_surface = None
            self.map_cache_size = None
            self.terrain_3d_render_key = None
            return
        if action == 'editor_prev_map':
            selected = game_engine.cycle_available_map(-1)
            if selected:
                game_engine.reload_preset(selected)
                self.map_cache_surface = None
                self.map_cache_size = None
                self.terrain_3d_render_key = None
            return
        if action == 'editor_next_map':
            selected = game_engine.cycle_available_map(1)
            if selected:
                game_engine.reload_preset(selected)
                self.map_cache_surface = None
                self.map_cache_size = None
                self.terrain_3d_render_key = None
            return
        if action == 'editor_toggle_overview':
            self.terrain_overview_window_open = True
            return
        if action == 'terrain_window_close':
            self.terrain_overview_window_open = True
            return
        return super()._execute_action(game_engine, action)

    def _sync_terrain_grid_config(self, game_engine):
        if not self.terrain_paint_dirty:
            return
        if hasattr(game_engine, 'queue_map_sync'):
            game_engine.queue_map_sync()
        else:
            super()._sync_terrain_grid_config(game_engine)
        self.terrain_paint_dirty = False


def main():
    editor_engine = TerrainEditorEngine()
    renderer = TerrainEditorRenderer(editor_engine, editor_engine.config)
    editor_engine.run(renderer)


if __name__ == '__main__':
    main()