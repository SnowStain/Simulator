#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math
import numpy as np
import os
import sys
import threading
import time
from copy import deepcopy
from heapq import heappop, heappush

try:
    from pygame_compat import pygame
except ModuleNotFoundError:
    project_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if project_root not in sys.path:
        sys.path.insert(0, project_root)
    from pygame_compat import pygame

class MapManager:
    def __init__(self, config):
        self.config = config
        self.map_image = None
        self.map_surface = None
        self.terrain_data = {}
        self.scale = config.get('simulator', {}).get('scale', 1.0)
        self.origin_x = config.get('map', {}).get('origin_x', 0)
        self.origin_y = config.get('map', {}).get('origin_y', 0)
        self.source_map_width = int(config.get('map', {}).get('source_width', config.get('map', {}).get('width', 1576)))
        self.source_map_height = int(config.get('map', {}).get('source_height', config.get('map', {}).get('height', 873)))
        self.strict_scale_enabled = bool(config.get('map', {}).get('strict_scale', False))
        self.map_width = int(config.get('map', {}).get('width', self.source_map_width))
        self.map_height = int(config.get('map', {}).get('height', self.source_map_height))
        self.field_length_m = config.get('map', {}).get('field_length_m', 28.0)
        self.field_width_m = config.get('map', {}).get('field_width_m', 15.0)
        self.vertical_height_scale = 1.0
        configured_terrain_grid_cell_size = max(0.2, float(config.get('map', {}).get('terrain_grid', {}).get('cell_size', 8)))
        configured_function_grid_cell_size = max(0.2, float(config.get('map', {}).get('function_grid', {}).get('cell_size', configured_terrain_grid_cell_size)))
        self.configured_terrain_grid_cell_size = configured_terrain_grid_cell_size
        self.configured_function_grid_cell_size = configured_function_grid_cell_size
        self.grid_precision_cell_size = max(0.2, float(config.get('map', {}).get('grid_precision_cell_size', 4)))
        self.terrain_grid_cell_size = 1.0 if self.strict_scale_enabled else min(configured_terrain_grid_cell_size, self.grid_precision_cell_size)
        self.function_grid_cell_size = 1.0 if self.strict_scale_enabled else min(configured_function_grid_cell_size, self.grid_precision_cell_size)
        self.runtime_grid_resolution_m = 0.01
        self.runtime_grid_width = 0
        self.runtime_grid_height = 0
        self.runtime_cell_width_world = 0.0
        self.runtime_cell_height_world = 0.0
        self.runtime_grid_bundle = {}
        self.runtime_grid_loaded = False
        self._config_coordinate_space_hint = None
        self.facilities = []
        self.terrain_grid_overrides = {}
        self.function_grid_overrides = {}
        self.function_pass_mode_by_code = {
            'passable': 0,
            'conditional': 1,
            'blocked': 2,
        }
        self.function_pass_mode_label_by_code = {
            0: '可通过',
            1: '条件通过',
            2: '不可通过',
        }
        self.terrain_code_by_type = {
            'flat': 0,
            'boundary': 1,
            'wall': 2,
            'dog_hole': 3,
            'second_step': 4,
            'first_step': 5,
            'fly_slope': 6,
            'undulating_road': 7,
            'supply': 8,
            'fort': 9,
            'outpost': 10,
            'base': 11,
            'custom_terrain': 12,
            'rugged_road': 13,
            'dead_zone': 14,
        }
        self.terrain_type_by_code = {code: terrain_type for terrain_type, code in self.terrain_code_by_type.items()}
        self.terrain_label_by_code = {
            0: '平地',
            1: '边界',
            2: '墙',
            3: '狗洞',
            4: '二级台阶',
            5: '一级台阶',
            6: '飞坡',
            7: '起伏路段',
            8: '补给区',
            9: '堡垒',
            10: '前哨站',
            11: '基地',
            12: '自定义地形',
            13: '起伏路段',
            14: '死区',
        }
        self.height_map = np.zeros((1, 1), dtype=np.float32)
        self.terrain_type_map = np.zeros((1, 1), dtype=np.uint8)
        self.move_block_map = np.zeros((1, 1), dtype=np.bool_)
        self.movement_block_map = self.move_block_map
        self.scene_obstacle_map = np.zeros((1, 1), dtype=np.bool_)
        self.vision_block_map = np.zeros((1, 1), dtype=np.bool_)
        self.vision_block_height_map = np.zeros((1, 1), dtype=np.float32)
        self.function_pass_map = np.zeros((1, 1), dtype=np.uint8)
        self.function_heading_map = np.full((1, 1), np.nan, dtype=np.float32)
        self.priority_map = np.zeros((1, 1), dtype=np.uint8)
        self.raster_dirty = True
        self.raster_version = 0
        self.terrain_priority = [
            'boundary',
            'wall',
            'rugged_road',
            'dog_hole',
            'dead_zone',
            'second_step',
            'first_step',
            'fly_slope',
            'undulating_road',
            'supply',
            'fort',
            'outpost',
            'base',
        ]
        self.priority_rank = {terrain_type: index for index, terrain_type in enumerate(self.terrain_priority)}
        self._region_query_default_rank = len(self.terrain_priority) + 32
        self._regions_query_cache = {}
        self._regions_query_cache_version = -1
        self._fov_cache = {}
        self._macro_path_cache = {}
        self._macro_path_cache_lock = threading.Lock()
        self._lpa_planner_cache = {}
        self._path_result_cache = {}
        self._path_result_cache_lock = threading.Lock()
        self._path_result_cache_max_entries = max(32, int(config.get('ai', {}).get('path_cache_max_entries', 192)))
        self._path_failure_cache_ttl_sec = max(0.1, float(config.get('ai', {}).get('path_failure_cache_ttl_sec', 0.35)))
        self._raster_batch_depth = 0
        self._raster_batch_pending = False
        self._edit_state_lock = threading.RLock()
        self.facility_version = 0
        self.terrain_overlay_revision = 0
        self._terrain_overlay_dirty_keys = set()
        self._terrain_overlay_reset_required = True
        self._raster_rebuild_lock = threading.Lock()
        self._raster_rebuild_event = threading.Event()
        self._raster_rebuild_stop_event = threading.Event()
        self._pending_raster_rebuild_job = None
        self._raster_rebuild_requested_at = 0.0
        self._raster_rebuild_debounce_sec = max(0.0, float(config.get('simulator', {}).get('terrain_raster_rebuild_debounce_sec', 0.35) or 0.35))
        self._raster_rebuild_thread = threading.Thread(target=self._raster_rebuild_worker_loop, name='map-manager-raster-rebuild', daemon=True)
        self._raster_rebuild_thread.start()
        preserved_runtime_grid_bundle = dict(config.get('map', {}).get('runtime_grid', {}) or {})
        self._refresh_runtime_grid_metrics()
        self._load_facilities_from_config()
        self._load_terrain_grid_from_config()
        self._load_function_grid_from_config()
        if preserved_runtime_grid_bundle:
            self.runtime_grid_bundle = preserved_runtime_grid_bundle
            self.config.setdefault('map', {})['runtime_grid'] = dict(preserved_runtime_grid_bundle)
            self._refresh_runtime_grid_metrics()

    def begin_raster_batch(self):
        self._raster_batch_depth += 1

    def shutdown(self):
        self._raster_rebuild_stop_event.set()
        self._raster_rebuild_event.set()
        worker = self._raster_rebuild_thread
        if worker is not None and worker.is_alive():
            worker.join(timeout=1.0)

    def end_raster_batch(self):
        if self._raster_batch_depth <= 0:
            return
        self._raster_batch_depth -= 1
        if self._raster_batch_depth == 0 and self._raster_batch_pending:
            self._raster_batch_pending = False
            self._mark_raster_dirty(force=True)

    def _mark_raster_dirty(self, force=False):
        if not force and self._raster_batch_depth > 0:
            self._raster_batch_pending = True
            return
        self.raster_dirty = True
        self.runtime_grid_loaded = False
        self.runtime_grid_bundle = {
            'resolution_m': float(self.runtime_grid_resolution_m),
            'shape': list(self._runtime_grid_shape()),
            'height_scale_baked_in': 1.0,
            'channels': {},
        }
        self.config.setdefault('map', {})['runtime_grid'] = dict(self.runtime_grid_bundle)
        self.raster_version += 1
        self._regions_query_cache.clear()
        self._regions_query_cache_version = self.raster_version
        self._fov_cache.clear()
        with self._macro_path_cache_lock:
            self._macro_path_cache.clear()
        self._lpa_planner_cache.clear()
        with self._path_result_cache_lock:
            self._path_result_cache.clear()
        if self._has_raster_layers():
            self._schedule_async_raster_rebuild()

    def _has_raster_layers(self):
        expected_shape = self._runtime_grid_shape()
        arrays = (
            self.height_map,
            self.terrain_type_map,
            self.movement_block_map,
            self.vision_block_map,
            self.vision_block_height_map,
            self.function_pass_map,
            self.function_heading_map,
            self.priority_map,
        )
        return all(array is not None and tuple(array.shape) == expected_shape for array in arrays)

    def _clear_raster_layers(self):
        self.height_map = None
        self.terrain_type_map = None
        self.movement_block_map = None
        self.move_block_map = None
        self.vision_block_map = None
        self.vision_block_height_map = None
        self.function_pass_map = None
        self.function_heading_map = None
        self.priority_map = None

    def _mark_facility_overlay_dirty(self):
        self.facility_version += 1

    def _mark_terrain_overlay_dirty(self, keys=None, reset=False):
        with self._edit_state_lock:
            self.terrain_overlay_revision += 1
            if reset:
                self._terrain_overlay_dirty_keys.clear()
                self._terrain_overlay_reset_required = True
                return
            if keys:
                for key in keys:
                    if key is not None:
                        self._terrain_overlay_dirty_keys.add(str(key))

    def consume_terrain_overlay_dirty_state(self):
        with self._edit_state_lock:
            state = {
                'revision': int(self.terrain_overlay_revision),
                'reset': bool(self._terrain_overlay_reset_required),
                'keys': tuple(self._terrain_overlay_dirty_keys),
            }
            self._terrain_overlay_dirty_keys.clear()
            self._terrain_overlay_reset_required = False
            return state

    def _snapshot_raster_rebuild_state(self):
        with self._edit_state_lock:
            return {
                'generation': int(self.raster_version),
                'map_width': int(self.map_width),
                'map_height': int(self.map_height),
                'terrain_grid_cell_size': float(self.terrain_grid_cell_size),
                'runtime_grid_width': int(self.runtime_grid_width),
                'runtime_grid_height': int(self.runtime_grid_height),
                'runtime_cell_width_world': float(self.runtime_cell_width_world),
                'runtime_cell_height_world': float(self.runtime_cell_height_world),
                'runtime_grid_resolution_m': float(self.runtime_grid_resolution_m),
                'facilities': deepcopy(self.facilities),
                'terrain_grid_overrides': {key: dict(cell) for key, cell in self.terrain_grid_overrides.items()},
                'function_grid_overrides': {key: dict(cell) for key, cell in self.function_grid_overrides.items()},
            }

    def _build_raster_state_from_snapshot(self, snapshot):
        worker_state = object.__new__(MapManager)
        worker_state.config = self.config
        worker_state.map_width = int(snapshot['map_width'])
        worker_state.map_height = int(snapshot['map_height'])
        worker_state.terrain_grid_cell_size = float(snapshot['terrain_grid_cell_size'])
        worker_state.runtime_grid_width = int(snapshot['runtime_grid_width'])
        worker_state.runtime_grid_height = int(snapshot['runtime_grid_height'])
        worker_state.runtime_cell_width_world = float(snapshot['runtime_cell_width_world'])
        worker_state.runtime_cell_height_world = float(snapshot['runtime_cell_height_world'])
        worker_state.runtime_grid_resolution_m = float(snapshot['runtime_grid_resolution_m'])
        worker_state.facilities = snapshot['facilities']
        worker_state.terrain_grid_overrides = snapshot['terrain_grid_overrides']
        worker_state.function_grid_overrides = snapshot['function_grid_overrides']
        worker_state.terrain_priority = self.terrain_priority
        worker_state.priority_rank = self.priority_rank
        worker_state.terrain_code_by_type = self.terrain_code_by_type
        worker_state.function_pass_mode_by_code = self.function_pass_mode_by_code
        worker_state.runtime_grid_bundle = {
            'resolution_m': float(worker_state.runtime_grid_resolution_m),
            'shape': [int(worker_state.runtime_grid_height), int(worker_state.runtime_grid_width)],
            'height_scale_baked_in': 1.0,
            'channels': {},
        }
        worker_state.runtime_grid_loaded = False
        worker_state._create_raster_layers()
        for facility_type in reversed(worker_state.terrain_priority):
            for region in worker_state.facilities:
                if region.get('type') == facility_type:
                    worker_state._apply_region_to_raster(region)
        for cell in worker_state.terrain_grid_overrides.values():
            worker_state._apply_terrain_override_to_raster(cell)
        for cell in worker_state.function_grid_overrides.values():
            worker_state._apply_function_override_to_raster(cell)
        worker_state._ensure_scene_obstacle_map()
        worker_state.move_block_map = worker_state.movement_block_map
        return {
            'generation': int(snapshot['generation']),
            'height_map': worker_state.height_map,
            'terrain_type_map': worker_state.terrain_type_map,
            'movement_block_map': worker_state.movement_block_map,
            'scene_obstacle_map': worker_state.scene_obstacle_map,
            'vision_block_map': worker_state.vision_block_map,
            'vision_block_height_map': worker_state.vision_block_height_map,
            'function_pass_map': worker_state.function_pass_map,
            'function_heading_map': worker_state.function_heading_map,
            'priority_map': worker_state.priority_map,
            'runtime_grid_bundle': dict(worker_state.runtime_grid_bundle),
            'runtime_grid_loaded': False,
        }

    def _install_raster_state(self, raster_state):
        self.height_map = raster_state['height_map']
        self.terrain_type_map = raster_state['terrain_type_map']
        self.movement_block_map = raster_state['movement_block_map']
        self.move_block_map = self.movement_block_map
        self.scene_obstacle_map = raster_state.get('scene_obstacle_map')
        self.vision_block_map = raster_state['vision_block_map']
        self.vision_block_height_map = raster_state['vision_block_height_map']
        self.function_pass_map = raster_state['function_pass_map']
        self.function_heading_map = raster_state['function_heading_map']
        self.priority_map = raster_state['priority_map']
        self.runtime_grid_bundle = dict(raster_state.get('runtime_grid_bundle', {}))
        self.runtime_grid_loaded = bool(raster_state.get('runtime_grid_loaded', False))
        self.raster_dirty = False
        self._ensure_scene_obstacle_map()

    def _schedule_async_raster_rebuild(self):
        if self._raster_rebuild_stop_event.is_set():
            return
        current_generation = int(self.raster_version)
        with self._raster_rebuild_lock:
            if self._pending_raster_rebuild_job == current_generation:
                return
            self._pending_raster_rebuild_job = current_generation
            self._raster_rebuild_requested_at = time.perf_counter()
        self._raster_rebuild_event.set()

    def _raster_rebuild_worker_loop(self):
        while not self._raster_rebuild_stop_event.is_set():
            self._raster_rebuild_event.wait(0.2)
            if self._raster_rebuild_stop_event.is_set():
                return
            self._raster_rebuild_event.clear()
            while not self._raster_rebuild_stop_event.is_set():
                with self._raster_rebuild_lock:
                    job = self._pending_raster_rebuild_job
                    requested_at = float(self._raster_rebuild_requested_at)
                    self._pending_raster_rebuild_job = None
                if job is None:
                    break
                wait_sec = self._raster_rebuild_debounce_sec - (time.perf_counter() - requested_at)
                if wait_sec > 1e-3:
                    self._raster_rebuild_event.wait(wait_sec)
                    if self._raster_rebuild_stop_event.is_set():
                        return
                    self._raster_rebuild_event.clear()
                    with self._raster_rebuild_lock:
                        if self._pending_raster_rebuild_job is not None:
                            continue
                raster_state = self._build_raster_state_from_snapshot(self._snapshot_raster_rebuild_state())
                if self._raster_rebuild_stop_event.is_set():
                    return
                with self._raster_rebuild_lock:
                    if int(job) != int(self.raster_version) or int(raster_state.get('generation', -1)) != int(self.raster_version):
                        continue
                    self._install_raster_state(raster_state)
                break

    def _terrain_cell_key(self, grid_x, grid_y):
        return f'{int(grid_x)},{int(grid_y)}'

    def _decode_terrain_cell_key(self, key):
        grid_x, grid_y = key.split(',', 1)
        return int(grid_x), int(grid_y)

    def _function_cell_key(self, grid_x, grid_y):
        return f'{int(grid_x)},{int(grid_y)}'

    def _decode_function_cell_key(self, key):
        grid_x, grid_y = key.split(',', 1)
        return int(grid_x), int(grid_y)

    def _normalize_heading_deg(self, heading_deg):
        value = float(heading_deg or 0.0) % 360.0
        if value < 0.0:
            value += 360.0
        return round(value, 1)

    def _heading_between_points_deg(self, start, end):
        if start is None or end is None:
            return 0.0
        dx = float(end[0]) - float(start[0])
        dy = float(end[1]) - float(start[1])
        if abs(dx) <= 1e-6 and abs(dy) <= 1e-6:
            return 0.0
        return self._normalize_heading_deg(math.degrees(math.atan2(dy, dx)))

    def _heading_delta_deg(self, heading_a, heading_b):
        delta = abs(self._normalize_heading_deg(heading_a) - self._normalize_heading_deg(heading_b))
        return min(delta, 360.0 - delta)

    def _function_override_payload(self, grid_x, grid_y, pass_mode='passable', heading_deg=None):
        normalized_mode = str(pass_mode or 'passable')
        if normalized_mode not in self.function_pass_mode_by_code:
            normalized_mode = 'passable'
        payload = {
            'x': int(grid_x),
            'y': int(grid_y),
            'pass_mode': normalized_mode,
        }
        if normalized_mode == 'conditional':
            payload['heading_deg'] = self._normalize_heading_deg(heading_deg)
        return payload

    def _set_function_override_cell(self, grid_x, grid_y, pass_mode='passable', heading_deg=None):
        with self._edit_state_lock:
            key = self._function_cell_key(grid_x, grid_y)
            normalized_mode = str(pass_mode or 'passable')
            if normalized_mode == 'passable':
                return self.function_grid_overrides.pop(key, None) is not None
            payload = self._function_override_payload(grid_x, grid_y, pass_mode=normalized_mode, heading_deg=heading_deg)
            previous = self.function_grid_overrides.get(key)
            if previous == payload:
                return False
            self.function_grid_overrides[key] = payload
            return True

    def _serialized_grid_cell_size(self, cell_size):
        value = round(float(cell_size), 4)
        if abs(value - round(value)) <= 1e-6:
            return int(round(value))
        return value

    def _resolved_grid_cell_size(self, configured_cell_size):
        if self.strict_scale_enabled:
            return 1.0
        return min(max(0.2, float(configured_cell_size)), float(self.grid_precision_cell_size))

    def terrain_scene_sample_cell_size(self):
        return max(
            float(self.terrain_grid_cell_size),
            float(getattr(self, 'configured_terrain_grid_cell_size', self.terrain_grid_cell_size)),
        )

    def _grid_cell_center(self, grid_x, grid_y):
        x1, y1, x2, y2 = self._grid_cell_bounds(grid_x, grid_y)
        return (x1 + x2) / 2.0, (y1 + y2) / 2.0

    def _grid_dimensions(self):
        return (
            math.ceil(self.map_width / max(self.terrain_grid_cell_size, 1)),
            math.ceil(self.map_height / max(self.terrain_grid_cell_size, 1)),
        )

    def _world_to_grid(self, world_x, world_y):
        cell_size = max(self.terrain_grid_cell_size, 1)
        return int(math.floor(float(world_x) / cell_size)), int(math.floor(float(world_y) / cell_size))

    def _grid_cell_bounds(self, grid_x, grid_y):
        cell_size = max(self.terrain_grid_cell_size, 1)
        x1 = int(math.floor(grid_x * cell_size))
        y1 = int(math.floor(grid_y * cell_size))
        x2 = min(self.map_width - 1, int(math.ceil((grid_x + 1) * cell_size) - 1))
        y2 = min(self.map_height - 1, int(math.ceil((grid_y + 1) * cell_size) - 1))
        return x1, y1, x2, y2

    def _refresh_runtime_grid_metrics(self):
        runtime_grid = self.config.get('map', {}).get('runtime_grid', {})
        terrain_grid = self.config.get('map', {}).get('terrain_grid', {})
        resolution_m = runtime_grid.get('resolution_m', terrain_grid.get('resolution_m', 0.01))
        self.runtime_grid_resolution_m = max(0.01, float(resolution_m or 0.01))
        self.runtime_grid_width = max(1, int(math.ceil(float(self.field_length_m) / self.runtime_grid_resolution_m)))
        self.runtime_grid_height = max(1, int(math.ceil(float(self.field_width_m) / self.runtime_grid_resolution_m)))
        if self.strict_scale_enabled:
            self.map_width = int(self.runtime_grid_width)
            self.map_height = int(self.runtime_grid_height)
        self.runtime_cell_width_world = float(self.map_width) / max(self.runtime_grid_width, 1)
        self.runtime_cell_height_world = float(self.map_height) / max(self.runtime_grid_height, 1)
        self.runtime_grid_bundle = dict(runtime_grid or {})

    def _world_scale_x(self):
        return float(self.map_width) / max(float(self.source_map_width), 1e-6)

    def _world_scale_y(self):
        return float(self.map_height) / max(float(self.source_map_height), 1e-6)

    def _scale_world_x_from_source(self, value):
        return int(round(float(value) * self._world_scale_x()))

    def _scale_world_y_from_source(self, value):
        return int(round(float(value) * self._world_scale_y()))

    def _scale_region_to_current_map(self, region):
        if self.source_map_width == self.map_width and self.source_map_height == self.map_height:
            return dict(region)
        scaled = dict(region)
        for field_name in ('x1', 'x2', 'cx'):
            if field_name in scaled:
                scaled[field_name] = self._scale_world_x_from_source(scaled[field_name])
        for field_name in ('y1', 'y2', 'cy'):
            if field_name in scaled:
                scaled[field_name] = self._scale_world_y_from_source(scaled[field_name])
        if 'radius' in scaled:
            average_scale = (self._world_scale_x() + self._world_scale_y()) * 0.5
            scaled['radius'] = round(float(scaled['radius']) * average_scale, 1)
        if 'thickness' in scaled:
            average_scale = (self._world_scale_x() + self._world_scale_y()) * 0.5
            scaled['thickness'] = max(1, int(round(float(scaled['thickness']) * average_scale)))
        if isinstance(scaled.get('points'), list):
            scaled['points'] = [
                [self._scale_world_x_from_source(point[0]), self._scale_world_y_from_source(point[1])]
                for point in scaled['points']
                if isinstance(point, (list, tuple)) and len(point) >= 2
            ]
        return scaled

    def _scale_grid_coordinate_from_source(self, grid_x, grid_y, source_cell_size, target_cell_size):
        world_x = float(grid_x) * float(source_cell_size) * self._world_scale_x()
        world_y = float(grid_y) * float(source_cell_size) * self._world_scale_y()
        return int(round(world_x / max(float(target_cell_size), 1e-6))), int(round(world_y / max(float(target_cell_size), 1e-6)))

    def _read_explicit_coordinate_space(self):
        coordinate_space = str(self.config.get('map', {}).get('coordinate_space', '') or '').strip().lower()
        if coordinate_space in {'world', 'current', 'map'}:
            return 'world'
        if coordinate_space in {'source', 'preset'}:
            return 'source'
        return None

    def _infer_coordinate_space_from_facilities(self, facilities):
        if not facilities:
            return None
        source_x = max(1.0, float(self.source_map_width - 1))
        source_y = max(1.0, float(self.source_map_height - 1))
        target_x = max(1.0, float(self.map_width - 1))
        target_y = max(1.0, float(self.map_height - 1))

        for region in facilities:
            if not isinstance(region, dict):
                continue
            if region.get('type') != 'boundary':
                continue
            if 'x2' not in region or 'y2' not in region:
                continue
            boundary_x = float(region.get('x2', 0.0))
            boundary_y = float(region.get('y2', 0.0))
            source_delta = abs(boundary_x - source_x) + abs(boundary_y - source_y)
            target_delta = abs(boundary_x - target_x) + abs(boundary_y - target_y)
            return 'source' if source_delta <= target_delta else 'world'

        max_x = 0.0
        max_y = 0.0
        for region in facilities:
            if not isinstance(region, dict):
                continue
            for key in ('x1', 'x2', 'cx'):
                if key in region:
                    max_x = max(max_x, float(region.get(key, 0.0)))
            for key in ('y1', 'y2', 'cy'):
                if key in region:
                    max_y = max(max_y, float(region.get(key, 0.0)))
            points = region.get('points', [])
            if isinstance(points, list):
                for point in points:
                    if not isinstance(point, (list, tuple)) or len(point) < 2:
                        continue
                    max_x = max(max_x, float(point[0]))
                    max_y = max(max_y, float(point[1]))

        if max_x > source_x * 1.05 or max_y > source_y * 1.05:
            return 'world'
        if max_x <= source_x + 1.0 and max_y <= source_y + 1.0:
            return 'source'
        if max_x <= target_x + 1.0 and max_y <= target_y + 1.0:
            return 'world'
        return 'source'

    def _infer_coordinate_space_from_grid_cells(self, cells, source_cell_size, target_cell_size):
        if not cells:
            return None
        source_cols = max(1, int(math.ceil(float(self.source_map_width) / max(float(source_cell_size), 1e-6))))
        source_rows = max(1, int(math.ceil(float(self.source_map_height) / max(float(source_cell_size), 1e-6))))
        target_cols = max(1, int(math.ceil(float(self.map_width) / max(float(target_cell_size), 1e-6))))
        target_rows = max(1, int(math.ceil(float(self.map_height) / max(float(target_cell_size), 1e-6))))

        max_x = 0
        max_y = 0
        for cell in cells:
            if not isinstance(cell, dict):
                continue
            max_x = max(max_x, int(cell.get('x', 0)))
            max_y = max(max_y, int(cell.get('y', 0)))

        if max_x >= source_cols or max_y >= source_rows:
            return 'world'
        if max_x >= target_cols or max_y >= target_rows:
            return 'source'
        return None

    def _configured_coordinate_space(self):
        if self._config_coordinate_space_hint is not None:
            return self._config_coordinate_space_hint

        explicit = self._read_explicit_coordinate_space()
        if explicit is not None:
            self._config_coordinate_space_hint = explicit
            return explicit

        map_config = self.config.get('map', {})
        facilities = map_config.get('facilities', []) or []
        inferred = self._infer_coordinate_space_from_facilities(facilities)
        if inferred is not None:
            self._config_coordinate_space_hint = inferred
            return inferred

        terrain_grid = map_config.get('terrain_grid', {}) or {}
        terrain_cells = terrain_grid.get('cells', []) or []
        terrain_source_cell_size = max(0.2, float(terrain_grid.get('cell_size', self.terrain_grid_cell_size)))
        terrain_target_cell_size = self._resolved_grid_cell_size(terrain_source_cell_size)
        inferred = self._infer_coordinate_space_from_grid_cells(terrain_cells, terrain_source_cell_size, terrain_target_cell_size)
        if inferred is not None:
            self._config_coordinate_space_hint = inferred
            return inferred

        self._config_coordinate_space_hint = 'source'
        return self._config_coordinate_space_hint

    def _scaled_grid_cell_ranges_from_source(self, grid_x, grid_y, source_cell_size, target_cell_size):
        target_cell_size = max(float(target_cell_size), 1e-6)
        world_x1 = float(grid_x) * float(source_cell_size) * self._world_scale_x()
        world_y1 = float(grid_y) * float(source_cell_size) * self._world_scale_y()
        world_x2 = float(grid_x + 1) * float(source_cell_size) * self._world_scale_x()
        world_y2 = float(grid_y + 1) * float(source_cell_size) * self._world_scale_y()

        grid_width = max(1, int(math.ceil(float(self.map_width) / target_cell_size)))
        grid_height = max(1, int(math.ceil(float(self.map_height) / target_cell_size)))

        start_x = max(0, min(grid_width - 1, int(math.floor(world_x1 / target_cell_size))))
        start_y = max(0, min(grid_height - 1, int(math.floor(world_y1 / target_cell_size))))
        end_x = max(start_x, min(grid_width - 1, int(math.ceil(world_x2 / target_cell_size) - 1)))
        end_y = max(start_y, min(grid_height - 1, int(math.ceil(world_y2 / target_cell_size) - 1)))
        return start_x, end_x, start_y, end_y

    def _grid_cell_ranges_from_world_space(self, grid_x, grid_y, source_cell_size, target_cell_size):
        target_cell_size = max(float(target_cell_size), 1e-6)
        world_x1 = float(grid_x) * float(source_cell_size)
        world_y1 = float(grid_y) * float(source_cell_size)
        world_x2 = float(grid_x + 1) * float(source_cell_size)
        world_y2 = float(grid_y + 1) * float(source_cell_size)

        grid_width = max(1, int(math.ceil(float(self.map_width) / target_cell_size)))
        grid_height = max(1, int(math.ceil(float(self.map_height) / target_cell_size)))

        start_x = max(0, min(grid_width - 1, int(math.floor(world_x1 / target_cell_size))))
        start_y = max(0, min(grid_height - 1, int(math.floor(world_y1 / target_cell_size))))
        end_x = max(start_x, min(grid_width - 1, int(math.ceil(world_x2 / target_cell_size) - 1)))
        end_y = max(start_y, min(grid_height - 1, int(math.ceil(world_y2 / target_cell_size) - 1)))
        return start_x, end_x, start_y, end_y

    def _iter_scaled_grid_cells(self, cell, source_cell_size, target_cell_size, coordinate_space):
        if coordinate_space == 'source':
            start_x, end_x, start_y, end_y = self._scaled_grid_cell_ranges_from_source(
                cell.get('x', 0),
                cell.get('y', 0),
                source_cell_size,
                target_cell_size,
            )
        else:
            start_x, end_x, start_y, end_y = self._grid_cell_ranges_from_world_space(
                cell.get('x', 0),
                cell.get('y', 0),
                source_cell_size,
                target_cell_size,
            )
        for grid_y in range(start_y, end_y + 1):
            for grid_x in range(start_x, end_x + 1):
                yield grid_x, grid_y

    def _create_blank_map_surface(self, width=None, height=None):
        width = max(1, int(width or self.map_width or 1576))
        height = max(1, int(height or self.map_height or 873))
        surface = pygame.Surface((width, height))
        surface.fill((241, 244, 248))
        pygame.draw.rect(surface, (210, 216, 224), pygame.Rect(0, 0, width, height), 2)
        step = max(24, min(72, int(round(min(width, height) / 18))))
        line_color = (228, 233, 239)
        for x in range(step, width, step):
            pygame.draw.line(surface, line_color, (x, 0), (x, height), 1)
        for y in range(step, height, step):
            pygame.draw.line(surface, line_color, (0, y), (width, y), 1)
        return surface

    def _runtime_grid_shape(self):
        return self.runtime_grid_height, self.runtime_grid_width

    def _world_to_runtime_cell(self, world_x, world_y):
        cell_x = int(float(world_x) / max(self.runtime_cell_width_world, 1e-6))
        cell_y = int(float(world_y) / max(self.runtime_cell_height_world, 1e-6))
        cell_x = max(0, min(self.runtime_grid_width - 1, cell_x))
        cell_y = max(0, min(self.runtime_grid_height - 1, cell_y))
        return cell_x, cell_y

    def _runtime_cell_center_world(self, cell_x, cell_y):
        center_x = (float(cell_x) + 0.5) * self.runtime_cell_width_world
        center_y = (float(cell_y) + 0.5) * self.runtime_cell_height_world
        return (
            max(0.0, min(float(self.map_width - 1), center_x)),
            max(0.0, min(float(self.map_height - 1), center_y)),
        )

    def _runtime_bounds_to_cell_ranges(self, x1, y1, x2, y2):
        min_x = max(0.0, min(float(x1), float(x2)))
        max_x = min(float(self.map_width - 1), max(float(x1), float(x2)))
        min_y = max(0.0, min(float(y1), float(y2)))
        max_y = min(float(self.map_height - 1), max(float(y1), float(y2)))
        grid_x1 = max(0, min(self.runtime_grid_width - 1, int(min_x / max(self.runtime_cell_width_world, 1e-6))))
        grid_x2 = max(0, min(self.runtime_grid_width - 1, int(max_x / max(self.runtime_cell_width_world, 1e-6))))
        grid_y1 = max(0, min(self.runtime_grid_height - 1, int(min_y / max(self.runtime_cell_height_world, 1e-6))))
        grid_y2 = max(0, min(self.runtime_grid_height - 1, int(max_y / max(self.runtime_cell_height_world, 1e-6))))
        return grid_x1, grid_x2, grid_y1, grid_y2

    def _runtime_world_coordinate_axes(self, grid_x1, grid_x2, grid_y1, grid_y2):
        cell_xs = (np.arange(grid_x1, grid_x2 + 1, dtype=np.float32) + 0.5) * float(self.runtime_cell_width_world)
        cell_ys = (np.arange(grid_y1, grid_y2 + 1, dtype=np.float32) + 0.5) * float(self.runtime_cell_height_world)
        return cell_xs, cell_ys

    def _map_preset_directory(self):
        preset_path = self.config.get('map', {}).get('_preset_path')
        if preset_path:
            return os.path.dirname(os.path.abspath(preset_path))
        config_path = self.config.get('_config_path')
        if config_path:
            return os.path.join(os.path.dirname(os.path.abspath(config_path)), 'maps')
        return os.getcwd()

    def _resolve_runtime_bundle_path(self, relative_path):
        if not relative_path:
            return None
        if os.path.isabs(relative_path):
            return relative_path
        return os.path.join(self._map_preset_directory(), relative_path)

    def _runtime_channel_names(self):
        return {
            'height_map': ('height_map', np.float32),
            'terrain_type_map': ('terrain_type_map', np.uint8),
            'movement_block_map': ('movement_block_map', np.bool_),
            'vision_block_map': ('vision_block_map', np.bool_),
            'vision_block_height_map': ('vision_block_height_map', np.float32),
            'priority_map': ('priority_map', np.uint8),
            'function_pass_map': ('function_pass_map', np.uint8),
            'function_heading_map': ('function_heading_map', np.float32),
        }

    def _try_load_runtime_grid_bundle(self):
        runtime_grid = dict(self.config.get('map', {}).get('runtime_grid', {}) or {})
        channels = runtime_grid.get('channels', {}) if isinstance(runtime_grid, dict) else {}
        if not channels:
            return False
        expected_shape = tuple(int(value) for value in runtime_grid.get('shape', self._runtime_grid_shape()))
        if expected_shape != self._runtime_grid_shape():
            return False

        loaded_arrays = {}
        for channel_name, (_, dtype) in self._runtime_channel_names().items():
            channel_path = self._resolve_runtime_bundle_path(channels.get(channel_name))
            if channel_path is None or not os.path.exists(channel_path):
                return False
            loaded = np.load(channel_path, allow_pickle=False)
            if tuple(loaded.shape) != expected_shape:
                return False
            loaded_arrays[channel_name] = loaded.astype(dtype, copy=False)

        baked_height_scale = max(1.0, float(runtime_grid.get('height_scale_baked_in', 1.0)))
        target_height_scale = 1.0
        height_scale_ratio = target_height_scale / baked_height_scale
        if abs(height_scale_ratio - 1.0) > 1e-6:
            loaded_arrays['height_map'] = (loaded_arrays['height_map'].astype(np.float32, copy=False) * height_scale_ratio).astype(np.float32, copy=False)
            loaded_arrays['vision_block_height_map'] = (loaded_arrays['vision_block_height_map'].astype(np.float32, copy=False) * height_scale_ratio).astype(np.float32, copy=False)

        self.height_map = loaded_arrays['height_map']
        self.terrain_type_map = loaded_arrays['terrain_type_map']
        self.movement_block_map = loaded_arrays['movement_block_map']
        self.move_block_map = self.movement_block_map
        self.vision_block_map = loaded_arrays['vision_block_map']
        self.vision_block_height_map = loaded_arrays['vision_block_height_map']
        self.priority_map = loaded_arrays['priority_map']
        self.function_pass_map = loaded_arrays['function_pass_map']
        self.function_heading_map = loaded_arrays['function_heading_map']
        runtime_grid['height_scale_baked_in'] = 1.0
        self.runtime_grid_bundle = runtime_grid
        self.config.setdefault('map', {})['runtime_grid'] = dict(runtime_grid)
        self.runtime_grid_loaded = True
        self.raster_dirty = False
        return True

    def _terrain_override_payload(self, grid_x, grid_y, terrain_type, height_m, team='neutral', blocks_movement=None, blocks_vision=None):
        return {
            'x': int(grid_x),
            'y': int(grid_y),
            'type': terrain_type,
            'team': team,
            'height_m': round(float(height_m), 2),
            'blocks_movement': bool(blocks_movement) if blocks_movement is not None else terrain_type in {'wall', 'boundary', 'dog_hole'},
            'blocks_vision': bool(blocks_vision) if blocks_vision is not None else terrain_type == 'wall',
        }

    def _set_terrain_override_cell(self, grid_x, grid_y, terrain_type, height_m, team='neutral', blocks_movement=None, blocks_vision=None):
        with self._edit_state_lock:
            key = self._terrain_cell_key(grid_x, grid_y)
            payload = self._terrain_override_payload(
                grid_x,
                grid_y,
                terrain_type,
                height_m,
                team=team,
                blocks_movement=blocks_movement,
                blocks_vision=blocks_vision,
            )
            if self.terrain_grid_overrides.get(key) == payload:
                return False
            self.terrain_grid_overrides[key] = payload
        self._mark_terrain_overlay_dirty((key,))
        return True

    def _grid_ranges_from_world_bounds(self, x1, y1, x2, y2):
        cell_size = max(self.terrain_grid_cell_size, 1)
        min_x = max(0, min(int(x1), int(x2)))
        max_x = min(self.map_width - 1, max(int(x1), int(x2)))
        min_y = max(0, min(int(y1), int(y2)))
        max_y = min(self.map_height - 1, max(int(y1), int(y2)))
        grid_x1 = max(0, int(math.floor(min_x / cell_size)))
        grid_x2 = min(self._grid_dimensions()[0] - 1, int(math.floor(max_x / cell_size)))
        grid_y1 = max(0, int(math.floor(min_y / cell_size)))
        grid_y2 = min(self._grid_dimensions()[1] - 1, int(math.floor(max_y / cell_size)))
        return grid_x1, grid_x2, grid_y1, grid_y2

    def _point_in_polygon_simple(self, x, y, points):
        if len(points) < 3:
            return False
        inside = False
        previous_x, previous_y = points[-1]
        for current_x, current_y in points:
            intersects = ((current_y > y) != (previous_y > y)) and (
                x < current_x + (previous_x - current_x) * (y - current_y) / (previous_y - current_y)
            )
            if intersects:
                inside = not inside
            previous_x, previous_y = current_x, current_y
        return inside

    def _point_on_polygon_edge(self, x, y, points):
        if len(points) < 2:
            return False
        previous_x, previous_y = points[-1]
        for current_x, current_y in points:
            if self._orientation(previous_x, previous_y, current_x, current_y, x, y) == 0 and self._point_on_segment(previous_x, previous_y, current_x, current_y, x, y):
                return True
            previous_x, previous_y = current_x, current_y
        return False

    def _point_in_polygon(self, x, y, points):
        if len(points) < 3:
            return False
        if self._point_on_polygon_edge(x, y, points):
            return True
        return self._point_in_polygon_simple(x, y, points)

    def _point_in_rect_bounds(self, x, y, x1, y1, x2, y2):
        return x1 <= x <= x2 and y1 <= y <= y2

    def _orientation(self, ax, ay, bx, by, cx, cy):
        value = (by - ay) * (cx - bx) - (bx - ax) * (cy - by)
        if abs(value) <= 1e-6:
            return 0
        return 1 if value > 0 else 2

    def _point_on_segment(self, ax, ay, bx, by, px, py):
        return (
            min(ax, bx) - 1e-6 <= px <= max(ax, bx) + 1e-6
            and min(ay, by) - 1e-6 <= py <= max(ay, by) + 1e-6
        )

    def _segments_intersect(self, p1, p2, q1, q2):
        ax, ay = p1
        bx, by = p2
        cx, cy = q1
        dx, dy = q2
        o1 = self._orientation(ax, ay, bx, by, cx, cy)
        o2 = self._orientation(ax, ay, bx, by, dx, dy)
        o3 = self._orientation(cx, cy, dx, dy, ax, ay)
        o4 = self._orientation(cx, cy, dx, dy, bx, by)

        if o1 != o2 and o3 != o4:
            return True
        if o1 == 0 and self._point_on_segment(ax, ay, bx, by, cx, cy):
            return True
        if o2 == 0 and self._point_on_segment(ax, ay, bx, by, dx, dy):
            return True
        if o3 == 0 and self._point_on_segment(cx, cy, dx, dy, ax, ay):
            return True
        if o4 == 0 and self._point_on_segment(cx, cy, dx, dy, bx, by):
            return True
        return False

    def _polygon_intersects_rect(self, points, x1, y1, x2, y2):
        if len(points) < 3:
            return False

        rect_corners = [
            (x1, y1),
            (x2, y1),
            (x2, y2),
            (x1, y2),
        ]
        rect_edges = [
            (rect_corners[0], rect_corners[1]),
            (rect_corners[1], rect_corners[2]),
            (rect_corners[2], rect_corners[3]),
            (rect_corners[3], rect_corners[0]),
        ]

        for corner_x, corner_y in rect_corners:
            if self._point_in_polygon_simple(corner_x, corner_y, points):
                return True

        for point_x, point_y in points:
            if self._point_in_rect_bounds(point_x, point_y, x1, y1, x2, y2):
                return True

        previous_point = points[-1]
        for current_point in points:
            for edge_start, edge_end in rect_edges:
                if self._segments_intersect(previous_point, current_point, edge_start, edge_end):
                    return True
            previous_point = current_point

        return False

    def _polygon_selected_cells(self, normalized_points):
        if len(normalized_points) < 3:
            return []
        x1, y1, x2, y2 = self._polygon_bounds(normalized_points)
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(x1, y1, x2, y2)
        selected_cells = []
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                sample_x, sample_y = self._grid_cell_center(grid_x, grid_y)
                if self._point_in_polygon(sample_x, sample_y, normalized_points):
                    selected_cells.append((grid_x, grid_y))
        return selected_cells

    def _distance_to_segment_points(self, x, y, start, end):
        x1 = float(start[0])
        y1 = float(start[1])
        x2 = float(end[0])
        y2 = float(end[1])
        dx = x2 - x1
        dy = y2 - y1
        if dx == 0 and dy == 0:
            return math.hypot(x - x1, y - y1)
        t = ((x - x1) * dx + (y - y1) * dy) / max(dx * dx + dy * dy, 1e-6)
        t = max(0.0, min(1.0, t))
        closest_x = x1 + t * dx
        closest_y = y1 + t * dy
        return math.hypot(x - closest_x, y - closest_y)

    def _sample_line_average_height(self, start, end, samples=16):
        sample_count = max(2, int(samples))
        total_height = 0.0
        for index in range(sample_count):
            factor = index / max(sample_count - 1, 1)
            sample_x = start[0] + (end[0] - start[0]) * factor
            sample_y = start[1] + (end[1] - start[1]) * factor
            total_height += self.get_terrain_height_m(sample_x, sample_y)
        return round(total_height / sample_count, 2)

    def _sample_grid_height(self, grid_x, grid_y):
        x1, y1, x2, y2 = self._grid_cell_bounds(grid_x, grid_y)
        return round(self.get_terrain_height_m((x1 + x2) / 2.0, (y1 + y2) / 2.0), 2)

    def _load_terrain_grid_from_config(self):
        terrain_grid = self.config.get('map', {}).get('terrain_grid', {})
        self._refresh_runtime_grid_metrics()
        source_cell_size = max(0.2, float(terrain_grid.get('cell_size', self.terrain_grid_cell_size)))
        self.terrain_grid_cell_size = self._resolved_grid_cell_size(source_cell_size)
        coordinate_space = self._configured_coordinate_space()
        self.terrain_grid_overrides = {}
        for cell in terrain_grid.get('cells', []):
            for grid_x, grid_y in self._iter_scaled_grid_cells(
                cell,
                source_cell_size,
                self.terrain_grid_cell_size,
                coordinate_space,
            ):
                normalized = {
                    'x': grid_x,
                    'y': grid_y,
                    'type': cell.get('type', 'flat'),
                    'team': cell.get('team', 'neutral'),
                    'height_m': round(float(cell.get('height_m', 0.0)), 2),
                    'blocks_movement': bool(cell.get('blocks_movement', False)),
                    'blocks_vision': bool(cell.get('blocks_vision', False)),
                }
                self.terrain_grid_overrides[self._terrain_cell_key(grid_x, grid_y)] = normalized
        self._mark_terrain_overlay_dirty(reset=True)
        self._mark_raster_dirty()

    def _load_function_grid_from_config(self):
        function_grid = self.config.get('map', {}).get('function_grid', {})
        source_cell_size = max(0.2, float(function_grid.get('cell_size', self.terrain_grid_cell_size)))
        self.function_grid_cell_size = self._resolved_grid_cell_size(source_cell_size)
        coordinate_space = self._configured_coordinate_space()
        self.function_grid_overrides = {}
        for cell in function_grid.get('cells', []):
            pass_mode = str(cell.get('pass_mode', 'passable') or 'passable')
            if pass_mode not in self.function_pass_mode_by_code or pass_mode == 'passable':
                continue
            for grid_x, grid_y in self._iter_scaled_grid_cells(
                cell,
                source_cell_size,
                self.function_grid_cell_size,
                coordinate_space,
            ):
                self.function_grid_overrides[self._function_cell_key(grid_x, grid_y)] = self._function_override_payload(
                    grid_x,
                    grid_y,
                    pass_mode=pass_mode,
                    heading_deg=cell.get('heading_deg'),
                )
        self._mark_raster_dirty()

    def export_terrain_grid_config(self):
        with self._edit_state_lock:
            cells = []
            for key in sorted(self.terrain_grid_overrides.keys(), key=lambda item: tuple(map(int, item.split(',')))):
                cell = dict(self.terrain_grid_overrides[key])
                cells.append(cell)
            return {
                'cell_size': self._serialized_grid_cell_size(self.terrain_grid_cell_size),
                'cells': cells,
            }

    def export_function_grid_config(self):
        with self._edit_state_lock:
            cells = []
            for key in sorted(self.function_grid_overrides.keys(), key=lambda item: tuple(map(int, item.split(',')))):
                cells.append(dict(self.function_grid_overrides[key]))
            return {
                'cell_size': self._serialized_grid_cell_size(self.function_grid_cell_size or self.terrain_grid_cell_size),
                'cells': cells,
            }

    def export_runtime_grid_config(self):
        with self._edit_state_lock:
            runtime_grid = dict(self.runtime_grid_bundle or {})
            runtime_grid['resolution_m'] = float(self.runtime_grid_resolution_m)
            runtime_grid['shape'] = list(self._runtime_grid_shape())
            runtime_grid['height_scale_baked_in'] = 1.0
            runtime_grid.setdefault('channels', {})
            return runtime_grid

    def persist_runtime_grid_bundle(self, preset_name, preset_path=None):
        self._ensure_raster_layers(wait=True)
        directory = os.path.dirname(os.path.abspath(preset_path)) if preset_path else self._map_preset_directory()
        os.makedirs(directory, exist_ok=True)
        safe_name = str(preset_name or 'map').strip() or 'map'
        channel_files = {}
        for channel_name, (attribute_name, _) in self._runtime_channel_names().items():
            file_name = f'{safe_name}.{channel_name}.npy'
            file_path = os.path.join(directory, file_name)
            np.save(file_path, getattr(self, attribute_name), allow_pickle=False)
            channel_files[channel_name] = file_name
        self.runtime_grid_bundle = {
            'resolution_m': float(self.runtime_grid_resolution_m),
            'shape': list(self._runtime_grid_shape()),
            'height_scale_baked_in': 1.0,
            'channels': channel_files,
        }
        self.config.setdefault('map', {})['runtime_grid'] = dict(self.runtime_grid_bundle)
        return dict(self.runtime_grid_bundle)

    def paint_terrain_grid(self, world_x, world_y, terrain_type, height_m=0.0, brush_radius=0, team='neutral', blocks_movement=None, blocks_vision=None):
        center_grid_x, center_grid_y = self._world_to_grid(world_x, world_y)
        max_x, max_y = self._grid_dimensions()
        changed = False
        for grid_y in range(max(0, center_grid_y - brush_radius), min(max_y, center_grid_y + brush_radius + 1)):
            for grid_x in range(max(0, center_grid_x - brush_radius), min(max_x, center_grid_x + brush_radius + 1)):
                if math.hypot(grid_x - center_grid_x, grid_y - center_grid_y) > brush_radius + 0.25:
                    continue
                changed = self._set_terrain_override_cell(grid_x, grid_y, terrain_type, height_m, team=team, blocks_movement=blocks_movement, blocks_vision=blocks_vision) or changed
        if changed:
            self._mark_raster_dirty()

    def paint_function_grid(self, world_x, world_y, pass_mode='passable', brush_radius=0, heading_deg=None):
        center_grid_x, center_grid_y = self._world_to_grid(world_x, world_y)
        max_x, max_y = self._grid_dimensions()
        changed = False
        for grid_y in range(max(0, center_grid_y - brush_radius), min(max_y, center_grid_y + brush_radius + 1)):
            for grid_x in range(max(0, center_grid_x - brush_radius), min(max_x, center_grid_x + brush_radius + 1)):
                if math.hypot(grid_x - center_grid_x, grid_y - center_grid_y) > brush_radius + 0.25:
                    continue
                changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_terrain_rect(self, x1, y1, x2, y2, terrain_type, height_m=0.0, team='neutral', blocks_movement=None, blocks_vision=None):
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(x1, y1, x2, y2)
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                changed = self._set_terrain_override_cell(
                    grid_x,
                    grid_y,
                    terrain_type,
                    height_m,
                    team=team,
                    blocks_movement=blocks_movement,
                    blocks_vision=blocks_vision,
                ) or changed
        if changed:
            self._mark_raster_dirty()

    def paint_function_rect(self, x1, y1, x2, y2, pass_mode='passable', heading_deg=None):
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(x1, y1, x2, y2)
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_terrain_circle(self, center_x, center_y, radius_world, terrain_type, height_m=0.0, team='neutral', blocks_movement=None, blocks_vision=None):
        radius_world = max(0.0, float(radius_world))
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(center_x - radius_world, center_y - radius_world, center_x + radius_world, center_y + radius_world)
        cell_size = max(self.terrain_grid_cell_size, 1)
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                sample_x, sample_y = self._grid_cell_center(grid_x, grid_y)
                if math.hypot(sample_x - center_x, sample_y - center_y) <= radius_world + cell_size * 0.35:
                    changed = self._set_terrain_override_cell(grid_x, grid_y, terrain_type, height_m, team=team, blocks_movement=blocks_movement, blocks_vision=blocks_vision) or changed
        if changed:
            self._mark_raster_dirty()

    def paint_function_circle(self, center_x, center_y, radius_world, pass_mode='passable', heading_deg=None):
        radius_world = max(0.0, float(radius_world))
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(center_x - radius_world, center_y - radius_world, center_x + radius_world, center_y + radius_world)
        cell_size = max(self.terrain_grid_cell_size, 1)
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                sample_x, sample_y = self._grid_cell_center(grid_x, grid_y)
                if math.hypot(sample_x - center_x, sample_y - center_y) <= radius_world + cell_size * 0.35:
                    changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_terrain_polygon(self, points, terrain_type, height_m=0.0, team='neutral', blocks_movement=None, blocks_vision=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return False
        changed = False
        for grid_x, grid_y in self._polygon_selected_cells(normalized_points):
            changed = self._set_terrain_override_cell(grid_x, grid_y, terrain_type, height_m, team=team, blocks_movement=blocks_movement, blocks_vision=blocks_vision) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_function_polygon(self, points, pass_mode='passable', heading_deg=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return False
        changed = False
        for grid_x, grid_y in self._polygon_selected_cells(normalized_points):
            changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_terrain_line(self, start_x, start_y, end_x, end_y, terrain_type, height_m=0.0, brush_radius=0, team='neutral', blocks_movement=None, blocks_vision=None):
        line_start = (int(start_x), int(start_y))
        line_end = (int(end_x), int(end_y))
        cell_size = max(self.terrain_grid_cell_size, 1)
        line_width = max(cell_size * 0.45, (float(brush_radius) + 0.5) * cell_size)
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(
            min(line_start[0], line_end[0]) - line_width,
            min(line_start[1], line_end[1]) - line_width,
            max(line_start[0], line_end[0]) + line_width,
            max(line_start[1], line_end[1]) + line_width,
        )
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                x1, y1, x2, y2 = self._grid_cell_bounds(grid_x, grid_y)
                sample_x = (x1 + x2) / 2.0
                sample_y = (y1 + y2) / 2.0
                if self._distance_to_segment_points(sample_x, sample_y, line_start, line_end) <= line_width:
                    self._set_terrain_override_cell(grid_x, grid_y, terrain_type, height_m, team=team, blocks_movement=blocks_movement, blocks_vision=blocks_vision)
                    changed = True
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_function_line(self, start_x, start_y, end_x, end_y, pass_mode='passable', brush_radius=0, heading_deg=None):
        line_start = (int(start_x), int(start_y))
        line_end = (int(end_x), int(end_y))
        cell_size = max(self.terrain_grid_cell_size, 1)
        line_width = max(cell_size * 0.45, (float(brush_radius) + 0.5) * cell_size)
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(
            min(line_start[0], line_end[0]) - line_width,
            min(line_start[1], line_end[1]) - line_width,
            max(line_start[0], line_end[0]) + line_width,
            max(line_start[1], line_end[1]) + line_width,
        )
        changed = False
        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                x1, y1, x2, y2 = self._grid_cell_bounds(grid_x, grid_y)
                sample_x = (x1 + x2) / 2.0
                sample_y = (y1 + y2) / 2.0
                if self._distance_to_segment_points(sample_x, sample_y, line_start, line_end) <= line_width:
                    changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
        if changed:
            self._mark_raster_dirty()
        return changed

    def paint_function_slope_polygon(self, points, pass_mode='conditional', direction_start=None, direction_end=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return {'changed': False, 'cell_count': 0, 'heading_deg': 0.0}
        heading_deg = self._heading_between_points_deg(direction_start, direction_end)
        changed = False
        cell_count = 0
        for grid_x, grid_y in self._polygon_selected_cells(normalized_points):
            changed = self._set_function_override_cell(grid_x, grid_y, pass_mode=pass_mode, heading_deg=heading_deg) or changed
            cell_count += 1
        if changed:
            self._mark_raster_dirty()
        return {
            'changed': changed,
            'cell_count': cell_count,
            'heading_deg': heading_deg,
        }

    def paint_terrain_slope(self, line1_start, line1_end, line2_start, line2_end, terrain_type, team='neutral', blocks_movement=None, blocks_vision=None):
        first_start = (int(line1_start[0]), int(line1_start[1]))
        first_end = (int(line1_end[0]), int(line1_end[1]))
        second_start = (int(line2_start[0]), int(line2_start[1]))
        second_end = (int(line2_end[0]), int(line2_end[1]))

        same_direction_cost = math.hypot(first_start[0] - second_start[0], first_start[1] - second_start[1]) + math.hypot(first_end[0] - second_end[0], first_end[1] - second_end[1])
        swapped_direction_cost = math.hypot(first_start[0] - second_end[0], first_start[1] - second_end[1]) + math.hypot(first_end[0] - second_start[0], first_end[1] - second_start[1])
        if swapped_direction_cost < same_direction_cost:
            second_start, second_end = second_end, second_start

        polygon = [first_start, first_end, second_end, second_start]
        if len(polygon) < 4:
            return {'changed': False, 'start_height': 0.0, 'end_height': 0.0}

        start_height = self._sample_line_average_height(first_start, first_end)
        end_height = self._sample_line_average_height(second_start, second_end)
        x1, y1, x2, y2 = self._polygon_bounds(polygon)
        grid_x1, grid_x2, grid_y1, grid_y2 = self._grid_ranges_from_world_bounds(x1, y1, x2, y2)
        cell_size = max(self.terrain_grid_cell_size, 1)
        edge_bias = cell_size * 0.35
        changed = False

        for grid_y in range(grid_y1, grid_y2 + 1):
            for grid_x in range(grid_x1, grid_x2 + 1):
                sample_x, sample_y = self._grid_cell_center(grid_x, grid_y)
                if not self._point_in_polygon_simple(sample_x, sample_y, polygon):
                    continue
                distance_to_first = max(0.0, self._distance_to_segment_points(sample_x, sample_y, first_start, first_end) - edge_bias)
                distance_to_second = max(0.0, self._distance_to_segment_points(sample_x, sample_y, second_start, second_end) - edge_bias)
                blend_total = distance_to_first + distance_to_second
                blend = 0.5 if blend_total <= 1e-6 else distance_to_first / blend_total
                height_value = round(start_height + (end_height - start_height) * blend, 2)
                self._set_terrain_override_cell(grid_x, grid_y, terrain_type, height_value, team=team, blocks_movement=blocks_movement, blocks_vision=blocks_vision)
                changed = True

        if changed:
            self._mark_raster_dirty()
        return {
            'changed': changed,
            'start_height': start_height,
            'end_height': end_height,
        }

    def paint_terrain_slope_polygon(self, points, terrain_type, team='neutral', blocks_movement=None, blocks_vision=None, filter_iterations=12, direction_start=None, direction_end=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return {'changed': False, 'cell_count': 0, 'min_height': 0.0, 'max_height': 0.0}

        slope_info = self.analyze_terrain_slope_polygon(normalized_points, direction_start=direction_start, direction_end=direction_end)
        if not slope_info.get('changed'):
            return {'changed': False, 'cell_count': 0, 'min_height': 0.0, 'max_height': 0.0}

        selected_cells = slope_info['selected_cells']
        min_height = slope_info['min_height']
        max_height = slope_info['max_height']
        low_center_x, low_center_y = slope_info['low_point']
        high_center_x, high_center_y = slope_info['high_point']
        axis_x = high_center_x - low_center_x
        axis_y = high_center_y - low_center_y
        axis_length_sq = axis_x * axis_x + axis_y * axis_y

        for grid_x, grid_y in selected_cells:
            x1, y1, x2, y2 = self._grid_cell_bounds(grid_x, grid_y)
            center_x = (x1 + x2) / 2.0
            center_y = (y1 + y2) / 2.0
            if axis_length_sq <= 1e-6:
                blend = 0.0
            else:
                blend = ((center_x - low_center_x) * axis_x + (center_y - low_center_y) * axis_y) / axis_length_sq
                blend = max(0.0, min(1.0, blend))
            height_value = round(min_height + (max_height - min_height) * blend, 2)
            self._set_terrain_override_cell(
                grid_x,
                grid_y,
                terrain_type,
                height_value,
                team=team,
                blocks_movement=blocks_movement,
                blocks_vision=blocks_vision,
            )

        self._mark_raster_dirty()
        return {
            'changed': True,
            'cell_count': len(selected_cells),
            'min_height': round(min_height, 2),
            'max_height': round(max_height, 2),
        }

    def analyze_terrain_slope_polygon(self, points, direction_start=None, direction_end=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return {'changed': False, 'cell_count': 0, 'min_height': 0.0, 'max_height': 0.0}

        selected_cells = self._polygon_selected_cells(normalized_points)
        if not selected_cells:
            return {'changed': False, 'cell_count': 0, 'min_height': 0.0, 'max_height': 0.0}

        original_heights = {cell: self._sample_grid_height(cell[0], cell[1]) for cell in selected_cells}
        low_cell = min(selected_cells, key=lambda cell: (original_heights[cell], cell[1], cell[0]))
        high_cell = max(selected_cells, key=lambda cell: (original_heights[cell], cell[1], cell[0]))
        min_height = original_heights[low_cell]
        max_height = original_heights[high_cell]

        low_x1, low_y1, low_x2, low_y2 = self._grid_cell_bounds(low_cell[0], low_cell[1])
        high_x1, high_y1, high_x2, high_y2 = self._grid_cell_bounds(high_cell[0], high_cell[1])
        low_point = ((low_x1 + low_x2) / 2.0, (low_y1 + low_y2) / 2.0)
        high_point = ((high_x1 + high_x2) / 2.0, (high_y1 + high_y2) / 2.0)
        direction_points = self._normalize_points([direction_start, direction_end]) if direction_start is not None and direction_end is not None else []
        if len(direction_points) == 2:
            start_point, end_point = direction_points
            if math.hypot(end_point[0] - start_point[0], end_point[1] - start_point[1]) > 1e-6:
                low_point = (float(start_point[0]), float(start_point[1]))
                high_point = (float(end_point[0]), float(end_point[1]))
        return {
            'changed': True,
            'selected_cells': selected_cells,
            'cell_count': len(selected_cells),
            'min_height': round(min_height, 2),
            'max_height': round(max_height, 2),
            'low_point': low_point,
            'high_point': high_point,
            'low_cell': low_cell,
            'high_cell': high_cell,
        }

    def erase_terrain_grid(self, world_x, world_y, brush_radius=0):
        center_grid_x, center_grid_y = self._world_to_grid(world_x, world_y)
        max_x, max_y = self._grid_dimensions()
        removed = False
        with self._edit_state_lock:
            for grid_y in range(max(0, center_grid_y - brush_radius), min(max_y, center_grid_y + brush_radius + 1)):
                for grid_x in range(max(0, center_grid_x - brush_radius), min(max_x, center_grid_x + brush_radius + 1)):
                    if math.hypot(grid_x - center_grid_x, grid_y - center_grid_y) > brush_radius + 0.25:
                        continue
                    key = self._terrain_cell_key(grid_x, grid_y)
                    removed_now = self.terrain_grid_overrides.pop(key, None) is not None
                    if removed_now:
                        self._mark_terrain_overlay_dirty((key,))
                    removed = removed_now or removed
        if removed:
            self._mark_raster_dirty()
        return removed

    def erase_function_grid(self, world_x, world_y, brush_radius=0):
        center_grid_x, center_grid_y = self._world_to_grid(world_x, world_y)
        max_x, max_y = self._grid_dimensions()
        removed = False
        with self._edit_state_lock:
            for grid_y in range(max(0, center_grid_y - brush_radius), min(max_y, center_grid_y + brush_radius + 1)):
                for grid_x in range(max(0, center_grid_x - brush_radius), min(max_x, center_grid_x + brush_radius + 1)):
                    if math.hypot(grid_x - center_grid_x, grid_y - center_grid_y) > brush_radius + 0.25:
                        continue
                    removed = self.function_grid_overrides.pop(self._function_cell_key(grid_x, grid_y), None) is not None or removed
        if removed:
            self._mark_raster_dirty()
        return removed

    def remove_terrain_grid_cell(self, grid_x, grid_y):
        with self._edit_state_lock:
            key = self._terrain_cell_key(grid_x, grid_y)
            removed = self.terrain_grid_overrides.pop(key, None)
            if removed is not None:
                self._mark_terrain_overlay_dirty((key,))
        if removed is not None:
            self._mark_raster_dirty()
            return True
        return False

    def remove_function_grid_cell(self, grid_x, grid_y):
        with self._edit_state_lock:
            removed = self.function_grid_overrides.pop(self._function_cell_key(grid_x, grid_y), None)
        if removed is not None:
            self._mark_raster_dirty()
            return True
        return False

    def smooth_terrain_cells(self, cell_keys, intensity=1):
        strength = max(1, min(3, int(intensity)))
        normalized_cells = []
        for item in cell_keys or []:
            if isinstance(item, str):
                grid_x, grid_y = self._decode_terrain_cell_key(item)
            else:
                grid_x, grid_y = int(item[0]), int(item[1])
            key = self._terrain_cell_key(grid_x, grid_y)
            if key in self.terrain_grid_overrides:
                normalized_cells.append((grid_x, grid_y))
        normalized_cells = list(dict.fromkeys(normalized_cells))
        if not normalized_cells:
            return {'changed': False, 'cell_count': 0}

        changed = False
        for _ in range(strength):
            source_heights = {}
            for grid_x, grid_y in normalized_cells:
                cell = self.terrain_grid_overrides.get(self._terrain_cell_key(grid_x, grid_y))
                if cell is None:
                    continue
                source_heights[(grid_x, grid_y)] = round(float(cell.get('height_m', 0.0)), 2)

            if not source_heights:
                break

            updated_heights = {}
            for grid_x, grid_y in normalized_cells:
                neighbors = []
                for sample_y in range(grid_y - 1, grid_y + 2):
                    for sample_x in range(grid_x - 1, grid_x + 2):
                        if (sample_x, sample_y) in source_heights:
                            neighbors.append(source_heights[(sample_x, sample_y)])
                if not neighbors:
                    continue
                current_height = source_heights.get((grid_x, grid_y), 0.0)
                average_height = sum(neighbors) / len(neighbors)
                updated_heights[(grid_x, grid_y)] = round(current_height * 0.35 + average_height * 0.65, 2)

            for (grid_x, grid_y), new_height in updated_heights.items():
                cell = self.terrain_grid_overrides.get(self._terrain_cell_key(grid_x, grid_y))
                if cell is None:
                    continue
                if abs(float(cell.get('height_m', 0.0)) - new_height) <= 1e-6:
                    continue
                self._set_terrain_override_cell(
                    grid_x,
                    grid_y,
                    cell.get('type', 'custom_terrain'),
                    new_height,
                    team=cell.get('team', 'neutral'),
                    blocks_movement=cell.get('blocks_movement'),
                    blocks_vision=cell.get('blocks_vision'),
                )
                changed = True

        if changed:
            self._mark_raster_dirty()
        return {'changed': changed, 'cell_count': len(normalized_cells), 'strength': strength}

    def get_terrain_grid_cell(self, world_x, world_y):
        grid_x, grid_y = self._world_to_grid(world_x, world_y)
        return self.terrain_grid_overrides.get(self._terrain_cell_key(grid_x, grid_y))

    def get_function_grid_cell(self, world_x, world_y):
        grid_x, grid_y = self._world_to_grid(world_x, world_y)
        return self.function_grid_overrides.get(self._function_cell_key(grid_x, grid_y))

    def _runtime_function_cell(self, world_x, world_y):
        self._ensure_raster_layers()
        cell_x, cell_y = self._world_to_runtime_cell(world_x, world_y)
        pass_code = int(self.function_pass_map[cell_y, cell_x])
        pass_mode = next(
            (label for label, code in self.function_pass_mode_by_code.items() if code == pass_code),
            'passable',
        )
        heading_deg = float(self.function_heading_map[cell_y, cell_x])
        if np.isnan(heading_deg):
            heading_deg = None
        return {
            'x': cell_x,
            'y': cell_y,
            'pass_mode': pass_mode,
            'heading_deg': heading_deg,
        }

    def function_cell_blocks_movement(self, cell):
        return isinstance(cell, dict) and str(cell.get('pass_mode', 'passable')) == 'blocked'

    def _is_function_heading_passable(self, cell, heading_deg, tolerance_deg=35.0):
        if not isinstance(cell, dict):
            return True
        if str(cell.get('pass_mode', 'passable')) != 'conditional':
            return True
        return self._heading_delta_deg(float(cell.get('heading_deg', 0.0)), heading_deg) <= float(tolerance_deg)

    def is_directionally_passable_segment(self, from_x, from_y, to_x, to_y, collision_radius=0.0, sample_stride=None, tolerance_deg=35.0):
        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        if distance <= 1e-6:
            return True
        heading_deg = self._heading_between_points_deg((from_x, from_y), (to_x, to_y))
        start_height = float(self.get_terrain_height_m(from_x, from_y))
        end_height = float(self.get_terrain_height_m(to_x, to_y))
        allow_step_descent = end_height <= start_height + 1e-6 and self._segment_touches_step_surface(from_x, from_y, to_x, to_y)
        stride = max(1.0, float(sample_stride or self.terrain_grid_cell_size * 0.35))
        sample_count = max(1, int(math.ceil(distance / stride)))
        radius = max(0.0, float(collision_radius))
        normal_x = -(float(to_y) - float(from_y)) / distance
        normal_y = (float(to_x) - float(from_x)) / distance
        for sample_index in range(sample_count + 1):
            ratio = sample_index / sample_count
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            sample_cell = self._runtime_function_cell(sample_x, sample_y)
            if self.function_cell_blocks_movement(sample_cell):
                return False
            if not self._is_function_heading_passable(sample_cell, heading_deg, tolerance_deg=tolerance_deg):
                if allow_step_descent:
                    continue
                return False
            if radius > 1.0:
                for edge_x, edge_y in (
                    (sample_x + normal_x * radius, sample_y + normal_y * radius),
                    (sample_x - normal_x * radius, sample_y - normal_y * radius),
                ):
                    edge_cell = self._runtime_function_cell(edge_x, edge_y)
                    if self.function_cell_blocks_movement(edge_cell):
                        return False
                    if not self._is_function_heading_passable(edge_cell, heading_deg, tolerance_deg=tolerance_deg):
                        if allow_step_descent:
                            continue
                        return False
        return True

    def _bresenham_line_cells(self, start_cell, end_cell):
        x0, y0 = int(start_cell[0]), int(start_cell[1])
        x1, y1 = int(end_cell[0]), int(end_cell[1])
        dx = abs(x1 - x0)
        sx = 1 if x0 < x1 else -1
        dy = -abs(y1 - y0)
        sy = 1 if y0 < y1 else -1
        error = dx + dy
        cells = []
        while True:
            if 0 <= x0 < self.runtime_grid_width and 0 <= y0 < self.runtime_grid_height:
                cells.append((x0, y0))
            if x0 == x1 and y0 == y1:
                break
            error2 = error * 2
            if error2 >= dy:
                error += dy
                x0 += sx
            if error2 <= dx:
                error += dx
                y0 += sy
        return cells

    def is_vision_line_clear(self, start_point, end_point, include_start=False, include_end=False):
        self._ensure_raster_layers()
        if start_point is None or end_point is None:
            return False
        start_cell = self._world_to_runtime_cell(start_point[0], start_point[1])
        end_cell = self._world_to_runtime_cell(end_point[0], end_point[1])
        cells = self._bresenham_line_cells(start_cell, end_cell)
        last_index = len(cells) - 1
        for index, (cell_x, cell_y) in enumerate(cells):
            if index == 0 and not include_start:
                continue
            if index == last_index and not include_end:
                continue
            if bool(self.vision_block_map[cell_y, cell_x]):
                return False
        return True

    def is_terrain_line_clear_3d(self, start_point, end_point, clearance_m=0.02, include_end=False, blocking_height_scale=1.0):
        self._ensure_raster_layers()
        if start_point is None or end_point is None:
            return False
        start_x, start_y, start_z = float(start_point[0]), float(start_point[1]), float(start_point[2])
        end_x, end_y, end_z = float(end_point[0]), float(end_point[1]), float(end_point[2])
        delta_x = end_x - start_x
        delta_y = end_y - start_y
        delta_z = end_z - start_z
        distance_world = math.hypot(delta_x, delta_y)
        if distance_world <= 1e-6:
            return True
        step_world = max(2.0, float(getattr(self, 'terrain_grid_cell_size', 8.0)) * 0.5)
        sample_steps = max(1, int(math.ceil(distance_world / max(step_world, 1e-6))))
        for step_index in range(1, sample_steps + 1):
            if step_index == sample_steps and not include_end:
                break
            ratio = step_index / max(sample_steps, 1)
            sample_x = start_x + delta_x * ratio
            sample_y = start_y + delta_y * ratio
            sample_z = start_z + delta_z * ratio
            sample = self.sample_raster_layers(sample_x, sample_y)
            if sample.get('terrain_type') == 'boundary':
                return False
            ground_height = float(sample.get('height_m', 0.0)) * float(blocking_height_scale)
            blocking_height = ground_height
            if bool(sample.get('vision_blocked', False)):
                blocking_height = max(blocking_height, float(sample.get('vision_block_height_m', 0.0)) * float(blocking_height_scale))
            if sample_z <= blocking_height + float(clearance_m):
                return False
        return True

    def compute_fov_visibility(self, origin_point, heading_deg, fov_deg, max_distance_world, angle_step_deg=1.0, include_mask=False, origin_height_m=None, terrain_height_scale=1.0, max_pitch_up_deg=30.0, max_pitch_down_deg=30.0, terrain_pitch_margin_deg=1.2):
        self._ensure_raster_layers()
        if origin_point is None:
            return {'polygon_world': tuple(), 'visible_mask': None}
        if self.height_map is None or self.vision_block_map is None:
            return {'polygon_world': tuple(), 'visible_mask': None}

        cache_key = None
        if not include_mask:
            cache_key = (
                self._world_to_runtime_cell(origin_point[0], origin_point[1]),
                round(float(heading_deg), 2),
                round(float(fov_deg), 2),
                round(float(max_distance_world), 2),
                round(float(angle_step_deg), 2),
                round(float(origin_height_m if origin_height_m is not None else -1.0), 2),
                round(float(terrain_height_scale), 2),
                round(float(max_pitch_up_deg), 2),
                round(float(max_pitch_down_deg), 2),
                int(self.raster_version),
            )
            cached = self._fov_cache.get(cache_key)
            if cached is not None:
                return cached

        origin_cell = self._world_to_runtime_cell(origin_point[0], origin_point[1])
        origin_ground_height = float(self.get_terrain_height_m(origin_point[0], origin_point[1]))
        effective_origin_height = float(origin_height_m) if origin_height_m is not None else origin_ground_height
        effective_origin_height = origin_ground_height * float(terrain_height_scale) + (effective_origin_height - origin_ground_height)
        meters_per_world_unit = max(float(self.meters_to_world_units(1.0)), 1e-6)
        terrain_pitch_margin_rad = math.radians(max(0.0, float(terrain_pitch_margin_deg)))
        height_map = self.height_map
        vision_block_map = self.vision_block_map
        safe_step = max(0.25, float(angle_step_deg))
        sample_count = max(1, int(math.ceil(max(0.0, float(fov_deg)) / safe_step)))
        start_angle = float(heading_deg) - float(fov_deg) * 0.5
        visible_mask = np.zeros(self._runtime_grid_shape(), dtype=np.bool_) if include_mask else None
        if visible_mask is not None:
            visible_mask[origin_cell[1], origin_cell[0]] = True

        polygon_points = [
            (float(origin_point[0]), float(origin_point[1])),
        ]
        for sample_index in range(sample_count + 1):
            angle_deg = start_angle + safe_step * sample_index
            angle_rad = math.radians(angle_deg)
            end_x = float(origin_point[0]) + math.cos(angle_rad) * float(max_distance_world)
            end_y = float(origin_point[1]) + math.sin(angle_rad) * float(max_distance_world)
            end_cell = self._world_to_runtime_cell(end_x, end_y)
            last_visible = origin_cell
            skyline_pitch_rad = -math.inf
            ray_cells = self._bresenham_line_cells(origin_cell, end_cell)
            for cell_index, (cell_x, cell_y) in enumerate(ray_cells):
                if cell_index == 0:
                    if visible_mask is not None:
                        visible_mask[cell_y, cell_x] = True
                    continue
                world_point = self._runtime_cell_center_world(cell_x, cell_y)
                horizontal_distance = math.hypot(float(world_point[0]) - float(origin_point[0]), float(world_point[1]) - float(origin_point[1]))
                if horizontal_distance > 1e-6:
                    terrain_height = float(height_map[cell_y, cell_x])
                    effective_terrain_height = terrain_height * float(terrain_height_scale)
                    target_pitch_rad = math.atan2(effective_terrain_height - effective_origin_height, horizontal_distance / meters_per_world_unit)
                    target_pitch_deg = math.degrees(target_pitch_rad)
                    if target_pitch_deg > float(max_pitch_up_deg) or target_pitch_deg < -float(max_pitch_down_deg):
                        break
                    if target_pitch_rad + terrain_pitch_margin_rad < skyline_pitch_rad:
                        break
                    skyline_pitch_rad = max(skyline_pitch_rad, target_pitch_rad)
                if bool(vision_block_map[cell_y, cell_x]):
                    break
                last_visible = (cell_x, cell_y)
                if visible_mask is not None:
                    visible_mask[cell_y, cell_x] = True
            polygon_points.append(self._runtime_cell_center_world(last_visible[0], last_visible[1]))

        polygon_points.append((float(origin_point[0]), float(origin_point[1])))
        result = {
            'polygon_world': tuple(polygon_points),
            'visible_mask': visible_mask,
        }
        if cache_key is not None:
            self._fov_cache[cache_key] = result
        return result

    def _create_raster_layers(self):
        shape = self._runtime_grid_shape()
        self.height_map = np.zeros(shape, dtype=np.float32)
        self.terrain_type_map = np.zeros(shape, dtype=np.uint8)
        self.movement_block_map = np.zeros(shape, dtype=np.bool_)
        self.move_block_map = self.movement_block_map
        self.scene_obstacle_map = np.zeros(shape, dtype=np.bool_)
        self.vision_block_map = np.zeros(shape, dtype=np.bool_)
        self.vision_block_height_map = np.zeros(shape, dtype=np.float32)
        self.function_pass_map = np.zeros(shape, dtype=np.uint8)
        self.function_heading_map = np.full(shape, np.nan, dtype=np.float32)
        self.priority_map = np.full(shape, fill_value=255, dtype=np.uint8)

    def _ensure_raster_layers(self, wait=False):
        if self.height_map is None or self.height_map.shape != self._runtime_grid_shape():
            if not self._try_load_runtime_grid_bundle():
                self._rebuild_raster_layers()
                self._ensure_scene_obstacle_map()
                return
        if self.raster_dirty:
            if wait or not self._has_raster_layers():
                self._rebuild_raster_layers()
                self._ensure_scene_obstacle_map()
                return
            else:
                self._schedule_async_raster_rebuild()
        self._ensure_scene_obstacle_map()

    def _ensure_scene_obstacle_map(self):
        shape = self._runtime_grid_shape()
        if self.scene_obstacle_map is not None and self.scene_obstacle_map.shape == shape:
            return
        if self.movement_block_map is None or self.height_map is None:
            self.scene_obstacle_map = np.zeros(shape, dtype=np.bool_)
            return
        if self.vision_block_map is None or self.vision_block_height_map is None or self.terrain_type_map is None:
            self.scene_obstacle_map = np.zeros(shape, dtype=np.bool_)
            return

        move_block = self.movement_block_map.astype(np.bool_)
        height_map = self.height_map
        vision_block_map = self.vision_block_map
        vision_block_height_map = self.vision_block_height_map
        terrain_type_map = self.terrain_type_map
        vision_wall = vision_block_map.astype(np.bool_) & ((vision_block_height_map - height_map) > 0.06)
        local_step = np.zeros(shape, dtype=np.bool_)
        cliff_edge = np.zeros(shape, dtype=np.bool_)
        step_height_threshold = max(
            0.10,
            float(self.config.get('physics', {}).get('normal_max_terrain_step_height_m', 0.35)),
        )
        step_codes = np.array([
            self.terrain_code_by_type.get('first_step', 255),
            self.terrain_code_by_type.get('second_step', 255),
            self.terrain_code_by_type.get('fly_slope', 255),
        ], dtype=np.uint8)
        current_step_like = np.isin(terrain_type_map, step_codes)
        for shift_y, shift_x in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            neighbor_height = np.roll(height_map, shift=(shift_y, shift_x), axis=(0, 1))
            neighbor_block = np.roll(move_block | vision_wall, shift=(shift_y, shift_x), axis=(0, 1))
            neighbor_type = np.roll(terrain_type_map, shift=(shift_y, shift_x), axis=(0, 1))
            neighbor_step_like = np.isin(neighbor_type, step_codes)
            height_delta = np.abs(neighbor_height - height_map)
            local_step |= neighbor_block & (height_delta > 0.10)
            cliff_edge |= (height_delta > step_height_threshold) & ~(current_step_like | neighbor_step_like)

        wall_like = vision_wall | local_step | cliff_edge
        expanded_wall_like = wall_like.copy()
        for shift_y in (-1, 0, 1):
            for shift_x in (-1, 0, 1):
                if shift_x == 0 and shift_y == 0:
                    continue
                expanded_wall_like |= np.roll(wall_like, shift=(shift_y, shift_x), axis=(0, 1))

        expanded_wall_like[0, :] = True
        expanded_wall_like[-1, :] = True
        expanded_wall_like[:, 0] = True
        expanded_wall_like[:, -1] = True
        self.scene_obstacle_map = move_block | expanded_wall_like

    def _clamp_bounds(self, x1, y1, x2, y2):
        return (
            max(0, int(x1)),
            max(0, int(y1)),
            min(self.map_width - 1, int(x2)),
            min(self.map_height - 1, int(y2)),
        )

    def _region_bounds(self, region):
        if region.get('shape') == 'line':
            thickness = int(region.get('thickness', 12)) + 1
            return self._clamp_bounds(
                min(region['x1'], region['x2']) - thickness,
                min(region['y1'], region['y2']) - thickness,
                max(region['x1'], region['x2']) + thickness,
                max(region['y1'], region['y2']) + thickness,
            )
        return self._clamp_bounds(region.get('x1', 0), region.get('y1', 0), region.get('x2', 0), region.get('y2', 0))

    def _region_mask(self, region, x1, y1, x2, y2):
        width = x2 - x1 + 1
        height = y2 - y1 + 1
        if width <= 0 or height <= 0:
            return None

        shape = region.get('shape')
        xs, ys = self._runtime_world_coordinate_axes(x1, x2, y1, y2)

        if shape == 'rect':
            if region.get('type') == 'boundary':
                thickness = int(region.get('thickness', 10))
                return (
                    (xs[np.newaxis, :] <= region['x1'] + thickness)
                    | (xs[np.newaxis, :] >= region['x2'] - thickness)
                    | (ys[:, np.newaxis] <= region['y1'] + thickness)
                    | (ys[:, np.newaxis] >= region['y2'] - thickness)
                )
            return np.ones((height, width), dtype=np.bool_)

        if shape == 'line':
            x_grid, y_grid = np.meshgrid(xs, ys)
            x1_line = float(region.get('x1', 0))
            y1_line = float(region.get('y1', 0))
            x2_line = float(region.get('x2', 0))
            y2_line = float(region.get('y2', 0))
            dx = x2_line - x1_line
            dy = y2_line - y1_line
            if dx == 0 and dy == 0:
                distances = np.hypot(x_grid - x1_line, y_grid - y1_line)
            else:
                denominator = max(dx * dx + dy * dy, 1e-6)
                t = ((x_grid - x1_line) * dx + (y_grid - y1_line) * dy) / denominator
                t = np.clip(t, 0.0, 1.0)
                closest_x = x1_line + t * dx
                closest_y = y1_line + t * dy
                distances = np.hypot(x_grid - closest_x, y_grid - closest_y)
            return distances <= float(region.get('thickness', 12))

        if shape == 'polygon':
            points = region.get('points', [])
            if len(points) < 3:
                return None
            x_grid, y_grid = np.meshgrid(xs, ys)
            mask = np.zeros((height, width), dtype=np.bool_)
            previous_x, previous_y = points[-1]
            for current_x, current_y in points:
                denominator = float(previous_y - current_y)
                intersects = np.zeros((height, width), dtype=np.bool_)
                if abs(denominator) > 1e-6:
                    crosses_scanline = (current_y > y_grid) != (previous_y > y_grid)
                    x_intersect = current_x + (previous_x - current_x) * (y_grid - current_y) / denominator
                    intersects = crosses_scanline & (x_grid < x_intersect)
                mask ^= intersects
                previous_x, previous_y = current_x, current_y
            return mask

        return None

    def _apply_region_to_raster(self, region):
        world_x1, world_y1, world_x2, world_y2 = self._region_bounds(region)
        grid_x1, grid_x2, grid_y1, grid_y2 = self._runtime_bounds_to_cell_ranges(world_x1, world_y1, world_x2, world_y2)
        if grid_x2 < grid_x1 or grid_y2 < grid_y1:
            return

        mask = self._region_mask(region, grid_x1, grid_y1, grid_x2, grid_y2)
        if mask is None or not mask.any():
            return

        rows = slice(grid_y1, grid_y2 + 1)
        cols = slice(grid_x1, grid_x2 + 1)
        priority = self.priority_rank.get(region.get('type', 'flat'), 254)
        priority_view = self.priority_map[rows, cols]
        replace_mask = mask & (priority <= priority_view)
        if replace_mask.any():
            scaled_height = float(region.get('height_m', 0.0))
            self.priority_map[rows, cols][replace_mask] = priority
            self.terrain_type_map[rows, cols][replace_mask] = self.terrain_code_by_type.get(region.get('type', 'flat'), 0)
            self.height_map[rows, cols][replace_mask] = scaled_height

            move_block = False
            if region.get('type') == 'wall':
                move_block = bool(region.get('blocks_movement', True))
            elif region.get('type') in {'boundary', 'dog_hole', 'rugged_road'}:
                move_block = True
            self.movement_block_map[rows, cols][replace_mask] = move_block

        vision_height = 0.0
        vision_block = region.get('type') == 'wall' and bool(region.get('blocks_vision', True))
        if vision_block:
            vision_height = float(region.get('height_m', 0.0))
            self.vision_block_map[rows, cols][mask] = True
        if vision_height > 0.0:
            self.vision_block_height_map[rows, cols] = np.maximum(
                self.vision_block_height_map[rows, cols],
                np.where(mask, vision_height, 0.0),
            )

    def _apply_terrain_override_to_raster(self, cell):
        grid_x = int(cell.get('x', 0))
        grid_y = int(cell.get('y', 0))
        cell_size = max(float(self.terrain_grid_cell_size), 1.0)
        x1 = float(grid_x) * cell_size
        y1 = float(grid_y) * cell_size
        x2 = min(float(self.map_width), float(grid_x + 1) * cell_size) - 1e-6
        y2 = min(float(self.map_height), float(grid_y + 1) * cell_size) - 1e-6
        runtime_x1, runtime_x2, runtime_y1, runtime_y2 = self._runtime_bounds_to_cell_ranges(x1, y1, x2, y2)
        rows = slice(runtime_y1, runtime_y2 + 1)
        cols = slice(runtime_x1, runtime_x2 + 1)
        terrain_type = cell.get('type', 'flat')
        override_height = round(float(cell.get('height_m', 0.0)), 2)
        override_move_block = bool(cell.get('blocks_movement', False))
        existing_move_block = self.movement_block_map[rows, cols].copy()
        preserve_block_mask = existing_move_block & (not override_move_block)

        self.priority_map[rows, cols][~preserve_block_mask] = 0
        self.terrain_type_map[rows, cols][~preserve_block_mask] = self.terrain_code_by_type.get(terrain_type, 0)
        self.height_map[rows, cols] = np.where(
            preserve_block_mask,
            np.maximum(self.height_map[rows, cols], override_height),
            override_height,
        )
        self.movement_block_map[rows, cols] = existing_move_block | override_move_block
        if bool(cell.get('blocks_vision', False)):
            self.vision_block_map[rows, cols] = True
            self.vision_block_height_map[rows, cols] = np.maximum(
                self.vision_block_height_map[rows, cols],
                override_height,
            )

    def _apply_function_override_to_raster(self, cell):
        grid_x = int(cell.get('x', 0))
        grid_y = int(cell.get('y', 0))
        cell_size = max(float(self.terrain_grid_cell_size), 1.0)
        x1 = float(grid_x) * cell_size
        y1 = float(grid_y) * cell_size
        x2 = min(float(self.map_width), float(grid_x + 1) * cell_size) - 1e-6
        y2 = min(float(self.map_height), float(grid_y + 1) * cell_size) - 1e-6
        runtime_x1, runtime_x2, runtime_y1, runtime_y2 = self._runtime_bounds_to_cell_ranges(x1, y1, x2, y2)
        rows = slice(runtime_y1, runtime_y2 + 1)
        cols = slice(runtime_x1, runtime_x2 + 1)
        pass_mode = str(cell.get('pass_mode', 'passable') or 'passable')
        pass_code = self.function_pass_mode_by_code.get(pass_mode, 0)
        self.function_pass_map[rows, cols] = pass_code
        if pass_mode == 'conditional' and cell.get('heading_deg') is not None:
            self.function_heading_map[rows, cols] = float(cell.get('heading_deg', 0.0))
        else:
            self.function_heading_map[rows, cols] = np.nan

    def _rebuild_raster_layers(self):
        raster_state = self._build_raster_state_from_snapshot(self._snapshot_raster_rebuild_state())
        self._install_raster_state(raster_state)

    def get_raster_layers(self):
        self._ensure_raster_layers()
        return {
            'shape': self._runtime_grid_shape(),
            'resolution_m': float(self.runtime_grid_resolution_m),
            'height_map': self.height_map,
            'terrain_type_map': self.terrain_type_map,
            'move_block_map': self.movement_block_map,
            'movement_block_map': self.movement_block_map,
            'scene_obstacle_map': self.scene_obstacle_map,
            'vision_block_map': self.vision_block_map,
            'vision_block_height_map': self.vision_block_height_map,
            'function_pass_map': self.function_pass_map,
            'function_heading_map': self.function_heading_map,
            'terrain_code_by_type': dict(self.terrain_code_by_type),
            'terrain_label_by_code': dict(self.terrain_label_by_code),
        }

    def sample_raster_layers(self, x, y):
        map_x = int(x)
        map_y = int(y)
        if not (0 <= map_x < self.map_width and 0 <= map_y < self.map_height):
            return {
                'terrain_type': 'boundary',
                'terrain_code': self.terrain_code_by_type['boundary'],
                'terrain_label': '边界',
                'height_m': 0.0,
                'move_blocked': True,
                'vision_block_height_m': 0.0,
                'function_pass_mode': 'blocked',
                'function_pass_mode_label': self.function_pass_mode_label_by_code[self.function_pass_mode_by_code['blocked']],
                'function_heading_deg': None,
            }

        self._ensure_raster_layers()
        runtime_x, runtime_y = self._world_to_runtime_cell(map_x, map_y)
        terrain_code = int(self.terrain_type_map[runtime_y, runtime_x])
        function_pass_code = int(self.function_pass_map[runtime_y, runtime_x])
        function_pass_mode = next(
            (label for label, code in self.function_pass_mode_by_code.items() if code == function_pass_code),
            'passable',
        )
        function_heading = float(self.function_heading_map[runtime_y, runtime_x])
        if np.isnan(function_heading):
            function_heading = None
        return {
            'terrain_type': self.terrain_type_by_code.get(terrain_code, 'flat'),
            'terrain_code': terrain_code,
            'terrain_label': self.terrain_label_by_code.get(terrain_code, '平地'),
            'height_m': round(float(self.height_map[runtime_y, runtime_x]), 2),
            'move_blocked': bool(self.scene_obstacle_map[runtime_y, runtime_x]) or function_pass_code == self.function_pass_mode_by_code['blocked'],
            'vision_blocked': bool(self.vision_block_map[runtime_y, runtime_x]),
            'vision_block_height_m': round(float(self.vision_block_height_map[runtime_y, runtime_x]), 2),
            'function_pass_mode': function_pass_mode,
            'function_pass_mode_label': self.function_pass_mode_label_by_code[self.function_pass_mode_by_code.get(function_pass_mode, 0)],
            'function_heading_deg': function_heading,
        }

    def _hard_movement_block_view(self, min_x, max_x, min_y, max_y):
        blocked_code = self.function_pass_mode_by_code['blocked']
        boundary_code = self.terrain_code_by_type['boundary']
        return (
            self.movement_block_map[min_y:max_y + 1, min_x:max_x + 1]
            | (self.terrain_type_map[min_y:max_y + 1, min_x:max_x + 1] == boundary_code)
            | (self.function_pass_map[min_y:max_y + 1, min_x:max_x + 1] == blocked_code)
        )

    def _is_hard_blocked_world(self, x, y):
        if not (0 <= int(x) < self.map_width and 0 <= int(y) < self.map_height):
            return True
        runtime_x, runtime_y = self._world_to_runtime_cell(x, y)
        block_view = self._hard_movement_block_view(runtime_x, runtime_x, runtime_y, runtime_y)
        return bool(block_view[0, 0])

    def _normalize_points(self, points):
        normalized = []
        for point in points or []:
            if isinstance(point, dict):
                px = point.get('x', 0)
                py = point.get('y', 0)
            else:
                px, py = point[0], point[1]
            normalized.append((int(px), int(py)))
        return normalized

    def _polygon_bounds(self, points):
        xs = [point[0] for point in points]
        ys = [point[1] for point in points]
        return min(xs), min(ys), max(xs), max(ys)

    def _default_height_for_region(self, region):
        if region.get('type') == 'wall':
            return 1.0
        return 0.0

    def _normalize_region(self, region):
        normalized = dict(region)
        shape = normalized.get('shape', 'rect')
        normalized['shape'] = shape

        if shape == 'polygon':
            normalized['points'] = self._normalize_points(normalized.get('points', []))
            if len(normalized['points']) >= 3:
                x1, y1, x2, y2 = self._polygon_bounds(normalized['points'])
                normalized['x1'] = x1
                normalized['y1'] = y1
                normalized['x2'] = x2
                normalized['y2'] = y2

        if normalized.get('type') == 'wall' or normalized.get('shape') == 'line':
            normalized['type'] = 'wall'
            normalized['shape'] = 'line'
            normalized['blocks_movement'] = bool(normalized.get('blocks_movement', True))
            normalized['blocks_vision'] = bool(normalized.get('blocks_vision', True))
            normalized['height_m'] = float(normalized.get('height_m', 1.0))
            normalized['thickness'] = int(normalized.get('thickness', 12))
        elif normalized.get('type') != 'boundary':
            normalized['height_m'] = round(float(normalized.get('height_m', self._default_height_for_region(normalized))), 2)
        return normalized
    
    def load_map(self):
        """加载地图图像"""
        map_path = self.config.get('map', {}).get('image_path', '') or ''
        self._config_coordinate_space_hint = None
        preserved_runtime_grid_bundle = dict(self.config.get('map', {}).get('runtime_grid', {}) or {})
        self._refresh_runtime_grid_metrics()
        target_width = int(self.map_width)
        target_height = int(self.map_height)
        resolved_path = map_path
        if resolved_path and not os.path.isabs(resolved_path):
            base_dirs = []
            preset_path = self.config.get('map', {}).get('_preset_path')
            if preset_path:
                base_dirs.append(os.path.dirname(os.path.abspath(preset_path)))
            config_path = self.config.get('_config_path')
            if config_path:
                base_dirs.append(os.path.dirname(os.path.abspath(config_path)))
            base_dirs.append(os.getcwd())
            for base_dir in base_dirs:
                candidate = os.path.join(base_dir, resolved_path)
                if os.path.exists(candidate):
                    resolved_path = candidate
                    break
            if not os.path.exists(resolved_path):
                fallback_candidates = []
                for base_dir in base_dirs:
                    fallback_candidates.append(os.path.join(base_dir, '场地-俯视图.png'))
                    fallback_candidates.append(os.path.join(base_dir, '俯视图.png'))
                    fallback_candidates.append(os.path.join(base_dir, 'maps', 'basicMap', '场地-俯视图.png'))
                for candidate in fallback_candidates:
                    if os.path.exists(candidate):
                        resolved_path = candidate
                        break

        if resolved_path and os.path.exists(resolved_path):
            try:
                loaded_image = pygame.image.load(resolved_path)
                if (loaded_image.get_width(), loaded_image.get_height()) != (target_width, target_height):
                    self.map_image = pygame.transform.smoothscale(loaded_image, (target_width, target_height))
                else:
                    self.map_image = loaded_image
                self.map_width = target_width
                self.map_height = target_height
                self.map_surface = pygame.transform.scale(self.map_image, (int(target_width * self.scale), int(target_height * self.scale)))
                self._refresh_runtime_grid_metrics()
                map_config = self.config.setdefault('map', {})
                map_config['width'] = int(self.map_width)
                map_config['height'] = int(self.map_height)
                map_config['source_width'] = int(self.source_map_width)
                map_config['source_height'] = int(self.source_map_height)
                map_config['strict_scale'] = bool(self.strict_scale_enabled)
                if preserved_runtime_grid_bundle.get('channels'):
                    self.runtime_grid_bundle = preserved_runtime_grid_bundle
                    self.config.setdefault('map', {})['runtime_grid'] = dict(preserved_runtime_grid_bundle)
                    self._clear_raster_layers()
                    self.raster_dirty = False
                    self.runtime_grid_loaded = False
                else:
                    self._mark_raster_dirty()
                print(f"地图加载成功: {resolved_path}")
            except pygame.error as e:
                print(f"地图加载失败: {e}")
        else:
            width = target_width
            height = target_height
            self.map_width = width
            self.map_height = height
            self.map_image = self._create_blank_map_surface(width, height)
            self.map_surface = pygame.transform.scale(self.map_image, (int(width * self.scale), int(height * self.scale)))
            self._refresh_runtime_grid_metrics()
            map_config = self.config.setdefault('map', {})
            map_config['width'] = int(self.map_width)
            map_config['height'] = int(self.map_height)
            map_config['source_width'] = int(self.source_map_width)
            map_config['source_height'] = int(self.source_map_height)
            map_config['strict_scale'] = bool(self.strict_scale_enabled)
            if preserved_runtime_grid_bundle.get('channels'):
                self.runtime_grid_bundle = preserved_runtime_grid_bundle
                self.config.setdefault('map', {})['runtime_grid'] = dict(preserved_runtime_grid_bundle)
                self._clear_raster_layers()
                self.raster_dirty = False
                self.runtime_grid_loaded = False
            else:
                self._mark_raster_dirty()
            if map_path:
                print(f"地图文件不存在，已回退为空白地图: {resolved_path}")
            else:
                print("未配置地图底图，已创建空白地图")

    def _load_facilities_from_config(self):
        """加载设施区域定义（优先使用配置，缺省使用内置俯视图标定）。"""
        map_config = self.config.get('map', {})
        if 'facilities' in map_config:
            configured = map_config.get('facilities', []) or []
            coordinate_space = self._configured_coordinate_space()
            with self._edit_state_lock:
                if coordinate_space == 'source':
                    self.facilities = [self._normalize_region(self._scale_region_to_current_map(self._normalize_region(region))) for region in configured]
                else:
                    self.facilities = [self._normalize_region(region) for region in configured]
            self._mark_facility_overlay_dirty()
            self._mark_raster_dirty()
            return

        # 坐标依据 1576x873 俯视图进行标定；用于裁判系统与地形判定。
        self.facilities = [
            {'id': 'red_base', 'type': 'base', 'team': 'red', 'shape': 'rect', 'x1': 95, 'y1': 360, 'x2': 230, 'y2': 500},
            {'id': 'red_outpost', 'type': 'outpost', 'team': 'red', 'shape': 'rect', 'x1': 380, 'y1': 360, 'x2': 505, 'y2': 500},
            {'id': 'center_energy_mechanism', 'type': 'energy_mechanism', 'team': 'neutral', 'shape': 'rect', 'x1': 738, 'y1': 398, 'x2': 838, 'y2': 478},
            {'id': 'red_fly_slope', 'type': 'fly_slope', 'team': 'red', 'shape': 'rect', 'x1': 560, 'y1': 600, 'x2': 1020, 'y2': 790},
            {'id': 'red_second_step', 'type': 'second_step', 'team': 'red', 'shape': 'rect', 'x1': 435, 'y1': 650, 'x2': 515, 'y2': 725},
            {'id': 'red_first_step', 'type': 'first_step', 'team': 'red', 'shape': 'rect', 'x1': 345, 'y1': 635, 'x2': 555, 'y2': 760},
            {'id': 'red_supply', 'type': 'supply', 'team': 'red', 'shape': 'rect', 'x1': 130, 'y1': 620, 'x2': 300, 'y2': 815},
            {'id': 'red_mineral_exchange', 'type': 'mineral_exchange', 'team': 'red', 'shape': 'rect', 'x1': 145, 'y1': 540, 'x2': 265, 'y2': 605},
            {'id': 'red_mining_area', 'type': 'mining_area', 'team': 'red', 'shape': 'rect', 'x1': 420, 'y1': 545, 'x2': 540, 'y2': 625},
            {'id': 'red_undulating_road', 'type': 'undulating_road', 'team': 'red', 'shape': 'rect', 'x1': 230, 'y1': 245, 'x2': 740, 'y2': 635},
            {'id': 'red_dog_hole', 'type': 'dog_hole', 'team': 'red', 'shape': 'rect', 'x1': 640, 'y1': 610, 'x2': 930, 'y2': 665},
            {'id': 'red_fort', 'type': 'fort', 'team': 'red', 'shape': 'rect', 'x1': 245, 'y1': 320, 'x2': 360, 'y2': 520},
            {'id': 'blue_base', 'type': 'base', 'team': 'blue', 'shape': 'rect', 'x1': 1330, 'y1': 360, 'x2': 1470, 'y2': 500},
            {'id': 'blue_outpost', 'type': 'outpost', 'team': 'blue', 'shape': 'rect', 'x1': 1070, 'y1': 360, 'x2': 1195, 'y2': 500},
            {'id': 'blue_fly_slope', 'type': 'fly_slope', 'team': 'blue', 'shape': 'rect', 'x1': 560, 'y1': 95, 'x2': 1020, 'y2': 275},
            {'id': 'blue_second_step', 'type': 'second_step', 'team': 'blue', 'shape': 'rect', 'x1': 1075, 'y1': 115, 'x2': 1155, 'y2': 185},
            {'id': 'blue_first_step', 'type': 'first_step', 'team': 'blue', 'shape': 'rect', 'x1': 1020, 'y1': 105, 'x2': 1230, 'y2': 235},
            {'id': 'blue_supply', 'type': 'supply', 'team': 'blue', 'shape': 'rect', 'x1': 1276, 'y1': 58, 'x2': 1446, 'y2': 253},
            {'id': 'blue_mineral_exchange', 'type': 'mineral_exchange', 'team': 'blue', 'shape': 'rect', 'x1': 1311, 'y1': 268, 'x2': 1431, 'y2': 333},
            {'id': 'blue_mining_area', 'type': 'mining_area', 'team': 'blue', 'shape': 'rect', 'x1': 1036, 'y1': 248, 'x2': 1156, 'y2': 328},
            {'id': 'blue_undulating_road', 'type': 'undulating_road', 'team': 'blue', 'shape': 'rect', 'x1': 840, 'y1': 245, 'x2': 1345, 'y2': 635},
            {'id': 'blue_dog_hole', 'type': 'dog_hole', 'team': 'blue', 'shape': 'rect', 'x1': 640, 'y1': 215, 'x2': 930, 'y2': 265},
            {'id': 'blue_fort', 'type': 'fort', 'team': 'blue', 'shape': 'rect', 'x1': 1215, 'y1': 320, 'x2': 1330, 'y2': 520},
            {'id': 'boundary_outer', 'type': 'boundary', 'team': 'neutral', 'shape': 'rect', 'x1': 0, 'y1': 0, 'x2': 1575, 'y2': 872, 'thickness': 14},
        ]
        with self._edit_state_lock:
            self.facilities = [self._normalize_region(self._scale_region_to_current_map(region)) for region in self.facilities]
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()

    def _in_rect(self, x, y, region):
        return region['x1'] <= x <= region['x2'] and region['y1'] <= y <= region['y2']

    def _in_boundary_band(self, x, y, region):
        thickness = region.get('thickness', 10)
        if not self._in_rect(x, y, region):
            return False
        return (
            x <= region['x1'] + thickness
            or x >= region['x2'] - thickness
            or y <= region['y1'] + thickness
            or y >= region['y2'] - thickness
        )

    def _distance_to_segment(self, x, y, region):
        x1 = float(region.get('x1', 0))
        y1 = float(region.get('y1', 0))
        x2 = float(region.get('x2', 0))
        y2 = float(region.get('y2', 0))
        dx = x2 - x1
        dy = y2 - y1
        if dx == 0 and dy == 0:
            return math.hypot(x - x1, y - y1)
        t = ((x - x1) * dx + (y - y1) * dy) / max(dx * dx + dy * dy, 1e-6)
        t = max(0.0, min(1.0, t))
        closest_x = x1 + t * dx
        closest_y = y1 + t * dy
        return math.hypot(x - closest_x, y - closest_y)

    def _on_line(self, x, y, region):
        return self._distance_to_segment(x, y, region) <= float(region.get('thickness', 12))

    def _in_polygon(self, x, y, region):
        points = region.get('points', [])
        if len(points) < 3:
            return False

        inside = False
        previous_x, previous_y = points[-1]
        for current_x, current_y in points:
            denominator = previous_y - current_y
            intersects = False
            if ((current_y > y) != (previous_y > y)) and abs(denominator) > 1e-6:
                x_intersect = current_x + (previous_x - current_x) * (y - current_y) / denominator
                intersects = x < x_intersect
            if intersects:
                inside = not inside
            previous_x, previous_y = current_x, current_y
        return inside

    def _region_contains_point(self, x, y, region):
        shape = region.get('shape')
        facility_type = region.get('type')
        if shape == 'rect':
            if facility_type == 'boundary':
                return self._in_boundary_band(x, y, region)
            return self._in_rect(x, y, region)
        if shape == 'polygon':
            return self._in_polygon(x, y, region)
        if shape == 'line':
            return self._on_line(x, y, region)
        return False

    def _next_variant_id(self, base_id, suffix):
        existing_ids = {region.get('id') for region in self.facilities}
        index = 1
        candidate = f'{base_id}_{suffix}_{index}'
        while candidate in existing_ids:
            index += 1
            candidate = f'{base_id}_{suffix}_{index}'
        return candidate

    def get_facility_at(self, x, y):
        """返回指定位置命中的设施定义；未命中返回None。"""
        regions = self.get_regions_at(x, y)
        if regions:
            return regions[0]
        return None

    def get_regions_at(self, x, y, region_types=None):
        if self._regions_query_cache_version != self.raster_version:
            self._regions_query_cache.clear()
            self._regions_query_cache_version = self.raster_version

        requested_types = set(region_types) if region_types else None
        requested_types_key = tuple(sorted(requested_types)) if requested_types else None
        cache_key = (round(float(x), 2), round(float(y), 2), requested_types_key)
        cached_hits = self._regions_query_cache.get(cache_key)
        if cached_hits is not None:
            return cached_hits

        hits = []
        for region in self.facilities:
            facility_type = region.get('type')
            if requested_types is not None and facility_type not in requested_types:
                continue
            if self._region_contains_point(x, y, region):
                hits.append(region)
        hits.sort(key=lambda region: (self.priority_rank.get(region.get('type'), self._region_query_default_rank), str(region.get('id', ''))))
        result = tuple(hits)
        self._regions_query_cache[cache_key] = result
        return result

    def get_facility_regions(self, facility_type=None):
        """获取设施区域列表，可按类型过滤。"""
        if facility_type is None:
            return list(self.facilities)
        return [f for f in self.facilities if f.get('type') == facility_type]

    def get_facility_by_id(self, facility_id):
        for facility in self.facilities:
            if facility.get('id') == facility_id:
                return facility
        return None

    def facility_center(self, facility):
        return int((facility['x1'] + facility['x2']) / 2), int((facility['y1'] + facility['y2']) / 2)

    def upsert_facility_region(self, facility_id, facility_type, x1, y1, x2, y2, team='neutral'):
        """新增或更新矩形设施区域。"""
        existing = self.get_facility_by_id(facility_id)
        if existing is not None and facility_type == 'dead_zone' and existing.get('type') == facility_type:
            facility_id = self._next_variant_id(facility_id, 'rect')
            existing = None
        normalized = {
            'id': facility_id,
            'type': facility_type,
            'team': team,
            'shape': 'rect',
            'x1': int(min(x1, x2)),
            'y1': int(min(y1, y2)),
            'x2': int(max(x1, x2)),
            'y2': int(max(y1, y2)),
            'height_m': float(existing.get('height_m', 0.0)) if existing else 0.0,
        }

        with self._edit_state_lock:
            for index, region in enumerate(self.facilities):
                if region.get('id') == facility_id:
                    self.facilities[index] = normalized
                    self._mark_facility_overlay_dirty()
                    self._mark_raster_dirty()
                    return normalized

            self.facilities.append(normalized)
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()
        return normalized

    def add_polygon_region(self, facility_type, points, team='neutral', base_id=None):
        normalized_points = self._normalize_points(points)
        if len(normalized_points) < 3:
            return None

        x1, y1, x2, y2 = self._polygon_bounds(normalized_points)
        base = base_id or facility_type
        region = {
            'id': self._next_variant_id(base, 'poly'),
            'type': facility_type,
            'team': team,
            'shape': 'polygon',
            'points': normalized_points,
            'x1': x1,
            'y1': y1,
            'x2': x2,
            'y2': y2,
            'height_m': 0.0,
        }
        normalized = self._normalize_region(region)
        with self._edit_state_lock:
            self.facilities.append(normalized)
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()
        return normalized

    def add_wall_line(self, x1, y1, x2, y2, thickness=12):
        existing_ids = {
            region.get('id')
            for region in self.facilities
            if str(region.get('id', '')).startswith('wall_')
        }
        index = 1
        while f'wall_{index}' in existing_ids:
            index += 1
        region = {
            'id': f'wall_{index}',
            'type': 'wall',
            'team': 'neutral',
            'shape': 'line',
            'x1': int(x1),
            'y1': int(y1),
            'x2': int(x2),
            'y2': int(y2),
            'thickness': int(thickness),
            'blocks_movement': True,
            'blocks_vision': True,
            'height_m': 1.0,
        }
        normalized = self._normalize_region(region)
        with self._edit_state_lock:
            self.facilities.append(normalized)
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()
        return normalized

    def update_wall_properties(self, wall_id, blocks_movement=None, blocks_vision=None, height_m=None):
        facility = self.get_facility_by_id(wall_id)
        if facility is None or facility.get('type') != 'wall':
            return None
        with self._edit_state_lock:
            if blocks_movement is not None:
                facility['blocks_movement'] = bool(blocks_movement)
            if blocks_vision is not None:
                facility['blocks_vision'] = bool(blocks_vision)
            if height_m is not None:
                facility['height_m'] = max(0.0, round(float(height_m), 2))
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()
        return facility

    def update_facility_height(self, facility_id, height_m):
        facility = self.get_facility_by_id(facility_id)
        if facility is None or facility.get('type') == 'boundary':
            return None
        with self._edit_state_lock:
            facility['height_m'] = max(0.0, round(float(height_m), 2))
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()
        return facility

    def remove_facility_region(self, facility_id):
        """删除设施区域。"""
        with self._edit_state_lock:
            self.facilities = [region for region in self.facilities if region.get('id') != facility_id]
        self._mark_facility_overlay_dirty()
        self._mark_raster_dirty()

    def export_facilities_config(self):
        """导出设施配置。"""
        with self._edit_state_lock:
            return [dict(region) for region in self.facilities]

    def get_facility_summary(self):
        """返回按设施类型分组的区域概要，供裁判系统使用。"""
        summary = {}
        for region in self.facilities:
            facility_type = region.get('type', 'unknown')
            summary.setdefault(facility_type, []).append(region.get('id'))
        return summary
    
    def get_terrain_type(self, x, y):
        """获取指定位置的地形类型"""
        map_x = int(x)
        map_y = int(y)

        if not (0 <= map_x < self.map_width and 0 <= map_y < self.map_height):
            return "边界"

        return self.sample_raster_layers(map_x, map_y)['terrain_label']

    def get_terrain_height_m(self, x, y):
        return self.sample_raster_layers(x, y)['height_m']

    def _is_runtime_cell_blocked(self, cell, traversal_profile=None):
        self._ensure_raster_layers()
        cell_x, cell_y = int(cell[0]), int(cell[1])
        if not (0 <= cell_x < self.runtime_grid_width and 0 <= cell_y < self.runtime_grid_height):
            return True
        if bool(self.scene_obstacle_map[cell_y, cell_x]):
            return True
        if int(self.function_pass_map[cell_y, cell_x]) == self.function_pass_mode_by_code['blocked']:
            return True
        collision_radius = float((traversal_profile or {}).get('collision_radius', 0.0))
        if collision_radius <= max(self.runtime_cell_width_world, self.runtime_cell_height_world) * 0.45:
            return False
        center_x, center_y = self._runtime_cell_center_world(cell_x, cell_y)
        return not self.is_position_valid_for_radius(center_x, center_y, collision_radius=collision_radius)

    def _find_nearest_passable_runtime_cell(self, cell, traversal_profile=None, search_radius=8):
        if not self._is_runtime_cell_blocked(cell, traversal_profile=traversal_profile):
            return int(cell[0]), int(cell[1])
        limit = max(1, int(search_radius))
        for radius in range(1, limit + 1):
            for offset_y in range(-radius, radius + 1):
                for offset_x in range(-radius, radius + 1):
                    if abs(offset_x) != radius and abs(offset_y) != radius:
                        continue
                    candidate = (int(cell[0]) + offset_x, int(cell[1]) + offset_y)
                    if self._is_runtime_cell_blocked(candidate, traversal_profile=traversal_profile):
                        continue
                    return candidate
        return None

    def _iter_runtime_neighbors(self, cell):
        for offset_y in (-1, 0, 1):
            for offset_x in (-1, 0, 1):
                if offset_x == 0 and offset_y == 0:
                    continue
                yield int(cell[0]) + offset_x, int(cell[1]) + offset_y

    def _runtime_transition_cost(self, current_cell, neighbor_cell):
        delta_x = abs(int(neighbor_cell[0]) - int(current_cell[0]))
        delta_y = abs(int(neighbor_cell[1]) - int(current_cell[1]))
        if delta_x == 0 and delta_y == 0:
            return 0.0
        width = float(self.runtime_cell_width_world)
        height = float(self.runtime_cell_height_world)
        if delta_x == 1 and delta_y == 1:
            return math.hypot(width, height)
        if delta_x == 1:
            return width
        if delta_y == 1:
            return height
        return math.hypot(delta_x * width, delta_y * height)

    def _runtime_transition_passable(self, current_cell, neighbor_cell, max_height_delta_m=0.05, traversal_profile=None):
        traversal_profile = traversal_profile or {}
        if self._is_runtime_cell_blocked(neighbor_cell, traversal_profile=traversal_profile):
            return False
        delta_x = int(neighbor_cell[0]) - int(current_cell[0])
        delta_y = int(neighbor_cell[1]) - int(current_cell[1])
        if abs(delta_x) > 1 or abs(delta_y) > 1:
            return False
        if delta_x != 0 and delta_y != 0:
            side_a = (int(current_cell[0]) + delta_x, int(current_cell[1]))
            side_b = (int(current_cell[0]), int(current_cell[1]) + delta_y)
            if self._is_runtime_cell_blocked(side_a, traversal_profile=traversal_profile):
                return False
            if self._is_runtime_cell_blocked(side_b, traversal_profile=traversal_profile):
                return False
        current_world = self._runtime_cell_center_world(current_cell[0], current_cell[1])
        neighbor_world = self._runtime_cell_center_world(neighbor_cell[0], neighbor_cell[1])
        if not self.is_directionally_passable_segment(
            current_world[0],
            current_world[1],
            neighbor_world[0],
            neighbor_world[1],
            collision_radius=float(traversal_profile.get('collision_radius', 0.0)),
            sample_stride=max(self.runtime_cell_width_world, self.runtime_cell_height_world),
        ):
            return False
        current_height = float(self.height_map[int(current_cell[1]), int(current_cell[0])])
        neighbor_height = float(self.height_map[int(neighbor_cell[1]), int(neighbor_cell[0])])
        if float(neighbor_height) - float(current_height) <= float(max_height_delta_m) + 1e-6:
            return True
        return self._segment_touches_step_surface(
            current_world[0],
            current_world[1],
            neighbor_world[0],
            neighbor_world[1],
        )

    def _runtime_path_heuristic(self, cell, goal_cell):
        current_world = self._runtime_cell_center_world(cell[0], cell[1])
        goal_world = self._runtime_cell_center_world(goal_cell[0], goal_cell[1])
        return math.hypot(goal_world[0] - current_world[0], goal_world[1] - current_world[1])

    def _sparsify_runtime_path(self, points, max_height_delta_m=0.05, collision_radius=0.0):
        if len(points) <= 2:
            return points
        simplified = [points[0]]
        anchor_index = 0
        sample_stride = max(1.0, min(self.runtime_cell_width_world, self.runtime_cell_height_world))
        while anchor_index < len(points) - 1:
            best_index = anchor_index + 1
            for candidate_index in range(anchor_index + 1, len(points)):
                anchor_point = points[anchor_index]
                candidate_point = points[candidate_index]
                result = self.evaluate_movement_path(
                    anchor_point[0],
                    anchor_point[1],
                    candidate_point[0],
                    candidate_point[1],
                    max_height_delta_m=max_height_delta_m,
                    collision_radius=collision_radius,
                )
                if not result.get('ok'):
                    break
                if not self.is_segment_valid_for_radius(
                    anchor_point[0],
                    anchor_point[1],
                    candidate_point[0],
                    candidate_point[1],
                    collision_radius=collision_radius,
                    sample_stride=sample_stride,
                ):
                    break
                best_index = candidate_index
            simplified.append(points[best_index])
            anchor_index = best_index
        return simplified

    def _reconstruct_runtime_path(self, came_from, current_cell, start_point, end_point, max_height_delta_m=0.05, collision_radius=0.0):
        cells = [current_cell]
        while current_cell in came_from:
            current_cell = came_from[current_cell]
            cells.append(current_cell)
        cells.reverse()
        points = [start_point]
        points.extend(self._runtime_cell_center_world(cell[0], cell[1]) for cell in cells[1:-1])
        points.append(end_point)
        return self._sparsify_runtime_path(
            points,
            max_height_delta_m=max_height_delta_m,
            collision_radius=collision_radius,
        )

    def _runtime_astar_find_path(self, start_point, end_point, max_height_delta_m=0.05, traversal_profile=None, max_iterations=None, max_runtime_sec=None):
        traversal_profile = traversal_profile or {}
        start_cell = self._world_to_runtime_cell(start_point[0], start_point[1])
        goal_cell = self._world_to_runtime_cell(end_point[0], end_point[1])
        start_cell = self._find_nearest_passable_runtime_cell(start_cell, traversal_profile=traversal_profile)
        goal_cell = self._find_nearest_passable_runtime_cell(goal_cell, traversal_profile=traversal_profile)
        if start_cell is None or goal_cell is None:
            return []
        if start_cell == goal_cell:
            return [start_point, end_point]

        iteration_limit = max(1, int(max_iterations) if max_iterations is not None else 12000)
        deadline = None
        if max_runtime_sec is not None and float(max_runtime_sec) > 0.0:
            deadline = time.perf_counter() + float(max_runtime_sec)

        frontier_heap = []
        start_h = self._runtime_path_heuristic(start_cell, goal_cell)
        heappush(frontier_heap, (start_h, 0.0, start_cell))
        came_from = {}
        g_score = {start_cell: 0.0}
        closed = set()
        iterations = 0

        while frontier_heap:
            if deadline is not None and time.perf_counter() >= deadline:
                break
            iterations += 1
            if iterations > iteration_limit:
                break

            _, current_g, current_cell = heappop(frontier_heap)
            best_g = float(g_score.get(current_cell, float('inf')))
            if current_g > best_g + 1e-6:
                continue
            if current_cell in closed:
                continue
            if current_cell == goal_cell:
                return self._reconstruct_runtime_path(
                    came_from,
                    current_cell,
                    start_point,
                    end_point,
                    max_height_delta_m=max_height_delta_m,
                    collision_radius=float(traversal_profile.get('collision_radius', 0.0)),
                )
            closed.add(current_cell)

            for neighbor_cell in self._iter_runtime_neighbors(current_cell):
                if neighbor_cell in closed:
                    continue
                if self._is_runtime_cell_blocked(neighbor_cell, traversal_profile=traversal_profile):
                    continue
                if not self._runtime_transition_passable(
                    current_cell,
                    neighbor_cell,
                    max_height_delta_m=max_height_delta_m,
                    traversal_profile=traversal_profile,
                ):
                    continue
                tentative_g = best_g + self._runtime_transition_cost(current_cell, neighbor_cell)
                if tentative_g >= float(g_score.get(neighbor_cell, float('inf'))) - 1e-6:
                    continue
                came_from[neighbor_cell] = current_cell
                g_score[neighbor_cell] = tentative_g
                heuristic = self._runtime_path_heuristic(neighbor_cell, goal_cell)
                heappush(frontier_heap, (tentative_g + heuristic, tentative_g, neighbor_cell))
        return []

    def find_path(self, start_point, end_point, max_height_delta_m=0.05, grid_step=None, traversal_profile=None, max_iterations=None, max_runtime_sec=None):
        self._ensure_raster_layers()
        if start_point is None or end_point is None:
            return []

        traversal_profile = traversal_profile or {}
        collision_radius = float(traversal_profile.get('collision_radius', 0.0))

        direct_segment = self.evaluate_movement_path(
            start_point[0],
            start_point[1],
            end_point[0],
            end_point[1],
            max_height_delta_m=max_height_delta_m,
            collision_radius=collision_radius,
        )
        if direct_segment.get('ok'):
            return [start_point, end_point]

        start_cell = self._world_to_runtime_cell(start_point[0], start_point[1])
        goal_cell = self._world_to_runtime_cell(end_point[0], end_point[1])
        cache_key = (
            start_cell,
            goal_cell,
            round(float(self.runtime_grid_resolution_m), 3),
            int(self.raster_version),
            self._nav_profile_signature(traversal_profile, max_height_delta_m),
        )
        cached_path = self._get_cached_path_result(cache_key)
        if cached_path is not None:
            return cached_path

        path = self._runtime_astar_find_path(
            start_point=start_point,
            end_point=end_point,
            max_height_delta_m=max_height_delta_m,
            traversal_profile=traversal_profile,
            max_iterations=max_iterations,
            max_runtime_sec=max_runtime_sec,
        )
        self._store_path_result(cache_key, path)
        return path

    def _macro_nav_span(self, step):
        target_world_size = max(float(step) * 3.0, 48.0)
        return max(2, int(round(target_world_size / max(float(step), 1.0))))

    def _nav_cell_to_macro_cell(self, cell, span):
        return int(cell[0]) // int(span), int(cell[1]) // int(span)

    def _macro_cell_bounds(self, macro_cell, span):
        min_x = int(macro_cell[0]) * int(span)
        min_y = int(macro_cell[1]) * int(span)
        max_x = min_x + int(span) - 1
        max_y = min_y + int(span) - 1
        return min_x, max_x, min_y, max_y

    def _macro_grid_dimensions(self, step, span):
        fine_x = math.ceil(self.map_width / max(int(step), 1))
        fine_y = math.ceil(self.map_height / max(int(step), 1))
        return math.ceil(fine_x / max(int(span), 1)), math.ceil(fine_y / max(int(span), 1))

    def _iter_macro_neighbors(self, macro_cell):
        yield macro_cell[0] - 1, macro_cell[1]
        yield macro_cell[0] + 1, macro_cell[1]
        yield macro_cell[0], macro_cell[1] - 1
        yield macro_cell[0], macro_cell[1] + 1

    def _macro_path_cache_key(self, start_macro, goal_macro, step, span, traversal_profile, max_height_delta_m):
        return (
            start_macro,
            goal_macro,
            int(step),
            int(span),
            int(self.raster_version),
            self._nav_profile_signature(traversal_profile, max_height_delta_m),
        )

    def _get_cached_macro_path(self, cache_key):
        now = time.perf_counter()
        with self._macro_path_cache_lock:
            entry = self._macro_path_cache.get(cache_key)
            if entry is None:
                return None
            entry['last_used'] = now
            return tuple(entry.get('route', ()))

    def _store_macro_path(self, cache_key, route):
        now = time.perf_counter()
        with self._macro_path_cache_lock:
            self._macro_path_cache[cache_key] = {
                'route': tuple(route or ()),
                'last_used': now,
            }
            overflow = len(self._macro_path_cache) - 96
            if overflow > 0:
                ordered_keys = sorted(
                    self._macro_path_cache,
                    key=lambda key: self._macro_path_cache[key].get('last_used', 0.0),
                )
                for old_key in ordered_keys[:overflow]:
                    self._macro_path_cache.pop(old_key, None)

    def _is_macro_cell_passable(self, macro_cell, step, span, traversal_profile=None):
        macro_width, macro_height = self._macro_grid_dimensions(step, span)
        if not (0 <= macro_cell[0] < macro_width and 0 <= macro_cell[1] < macro_height):
            return False
        min_x, max_x, min_y, max_y = self._macro_cell_bounds(macro_cell, span)
        fine_width = math.ceil(self.map_width / max(int(step), 1))
        fine_height = math.ceil(self.map_height / max(int(step), 1))
        max_x = min(max_x, fine_width - 1)
        max_y = min(max_y, fine_height - 1)
        for cell_y in range(min_y, max_y + 1):
            for cell_x in range(min_x, max_x + 1):
                if self._is_nav_cell_passable((cell_x, cell_y), step, traversal_profile=traversal_profile):
                    return True
        return False

    def _find_nearest_passable_macro_cell(self, macro_cell, step, span, traversal_profile=None, search_radius=2):
        if self._is_macro_cell_passable(macro_cell, step, span, traversal_profile=traversal_profile):
            return macro_cell
        macro_width, macro_height = self._macro_grid_dimensions(step, span)
        for radius in range(1, max(1, int(search_radius)) + 1):
            for offset_y in range(-radius, radius + 1):
                for offset_x in range(-radius, radius + 1):
                    candidate = (macro_cell[0] + offset_x, macro_cell[1] + offset_y)
                    if not (0 <= candidate[0] < macro_width and 0 <= candidate[1] < macro_height):
                        continue
                    if self._is_macro_cell_passable(candidate, step, span, traversal_profile=traversal_profile):
                        return candidate
        return None

    def _macro_boundary_nav_pairs(self, current_macro, neighbor_macro, span):
        current_min_x, current_max_x, current_min_y, current_max_y = self._macro_cell_bounds(current_macro, span)
        neighbor_min_x, neighbor_max_x, neighbor_min_y, neighbor_max_y = self._macro_cell_bounds(neighbor_macro, span)
        delta_x = int(neighbor_macro[0]) - int(current_macro[0])
        delta_y = int(neighbor_macro[1]) - int(current_macro[1])
        pairs = []
        if abs(delta_x) + abs(delta_y) != 1:
            return pairs
        if delta_x != 0:
            current_x = current_max_x if delta_x > 0 else current_min_x
            neighbor_x = neighbor_min_x if delta_x > 0 else neighbor_max_x
            overlap_y1 = max(current_min_y, neighbor_min_y)
            overlap_y2 = min(current_max_y, neighbor_max_y)
            for cell_y in range(overlap_y1, overlap_y2 + 1):
                pairs.append(((current_x, cell_y), (neighbor_x, cell_y)))
        else:
            current_y = current_max_y if delta_y > 0 else current_min_y
            neighbor_y = neighbor_min_y if delta_y > 0 else neighbor_max_y
            overlap_x1 = max(current_min_x, neighbor_min_x)
            overlap_x2 = min(current_max_x, neighbor_max_x)
            for cell_x in range(overlap_x1, overlap_x2 + 1):
                pairs.append(((cell_x, current_y), (cell_x, neighbor_y)))
        return pairs

    def _is_macro_transition_passable(self, current_macro, neighbor_macro, step, span, traversal_profile=None, max_height_delta_m=0.05):
        if not self._is_macro_cell_passable(current_macro, step, span, traversal_profile=traversal_profile):
            return False
        if not self._is_macro_cell_passable(neighbor_macro, step, span, traversal_profile=traversal_profile):
            return False
        for current_cell, neighbor_cell in self._macro_boundary_nav_pairs(current_macro, neighbor_macro, span):
            if not self._is_nav_cell_passable(current_cell, step, traversal_profile=traversal_profile):
                continue
            if not self._is_nav_cell_passable(neighbor_cell, step, traversal_profile=traversal_profile):
                continue
            if not self._is_nav_transition_passable(current_cell, neighbor_cell, step, traversal_profile=traversal_profile):
                continue
            current_world = self._nav_cell_center(current_cell, step)
            neighbor_world = self._nav_cell_center(neighbor_cell, step)
            current_height = float(self.get_terrain_height_m(current_world[0], current_world[1]))
            neighbor_height = float(self.get_terrain_height_m(neighbor_world[0], neighbor_world[1]))
            if float(neighbor_height) - float(current_height) <= float(max_height_delta_m) + 1e-6:
                return True
            if self._segment_touches_step_surface(current_world[0], current_world[1], neighbor_world[0], neighbor_world[1]):
                return True
        return False

    def _reconstruct_macro_route(self, parents, current):
        route = [current]
        while current in parents:
            current = parents[current]
            route.append(current)
        route.reverse()
        return tuple(route)

    def _find_macro_corridor(self, start, goal, step, max_height_delta_m=0.05, traversal_profile=None):
        span = self._macro_nav_span(step)
        macro_passable_cache = {}
        macro_transition_cache = {}

        def is_macro_passable(cell):
            if cell not in macro_passable_cache:
                macro_passable_cache[cell] = self._is_macro_cell_passable(cell, step, span, traversal_profile=traversal_profile)
            return macro_passable_cache[cell]

        def is_macro_transition_passable(current_macro, neighbor_macro):
            cache_key = (current_macro, neighbor_macro)
            if cache_key not in macro_transition_cache:
                macro_transition_cache[cache_key] = self._is_macro_transition_passable(
                    current_macro,
                    neighbor_macro,
                    step,
                    span,
                    traversal_profile=traversal_profile,
                    max_height_delta_m=max_height_delta_m,
                )
            return macro_transition_cache[cache_key]

        start_macro = self._nav_cell_to_macro_cell(start, span)
        goal_macro = self._nav_cell_to_macro_cell(goal, span)
        start_macro = self._find_nearest_passable_macro_cell(start_macro, step, span, traversal_profile=traversal_profile)
        goal_macro = self._find_nearest_passable_macro_cell(goal_macro, step, span, traversal_profile=traversal_profile)
        if start_macro is None or goal_macro is None:
            return (), span
        if start_macro == goal_macro:
            return (start_macro,), span
        cache_key = self._macro_path_cache_key(start_macro, goal_macro, step, span, traversal_profile, max_height_delta_m)
        cached = self._get_cached_macro_path(cache_key)
        if cached is not None:
            return cached, span

        frontier_heap = []
        start_h = math.hypot(goal_macro[0] - start_macro[0], goal_macro[1] - start_macro[1])
        heappush(frontier_heap, (start_h, 0.0, start_macro))
        parents = {}
        g_score = {start_macro: 0.0}
        closed = set()

        while frontier_heap:
            _, current_g, current = heappop(frontier_heap)
            if current in closed:
                continue
            if current == goal_macro:
                route = self._reconstruct_macro_route(parents, current)
                self._store_macro_path(cache_key, route)
                return route, span
            closed.add(current)
            for neighbor in self._iter_macro_neighbors(current):
                if neighbor in closed:
                    continue
                if not is_macro_passable(neighbor):
                    continue
                if not is_macro_transition_passable(current, neighbor):
                    continue
                tentative_g = current_g + 1.0
                if tentative_g >= float(g_score.get(neighbor, float('inf'))) - 1e-6:
                    continue
                parents[neighbor] = current
                g_score[neighbor] = tentative_g
                heuristic = math.hypot(goal_macro[0] - neighbor[0], goal_macro[1] - neighbor[1])
                heappush(frontier_heap, (tentative_g + heuristic, tentative_g, neighbor))

        self._store_macro_path(cache_key, ())
        return (), span

    def _macro_route_search_bounds(self, macro_route, step, span, margin=1):
        if not macro_route:
            return None
        min_macro_x = min(cell[0] for cell in macro_route) - int(margin)
        max_macro_x = max(cell[0] for cell in macro_route) + int(margin)
        min_macro_y = min(cell[1] for cell in macro_route) - int(margin)
        max_macro_y = max(cell[1] for cell in macro_route) + int(margin)
        fine_width = max(1, math.ceil(self.map_width / max(int(step), 1)))
        fine_height = max(1, math.ceil(self.map_height / max(int(step), 1)))
        min_cell_x = max(0, min_macro_x * int(span))
        max_cell_x = min(fine_width - 1, (max_macro_x + 1) * int(span) - 1)
        min_cell_y = max(0, min_macro_y * int(span))
        max_cell_y = min(fine_height - 1, (max_macro_y + 1) * int(span) - 1)
        return min_cell_x, max_cell_x, min_cell_y, max_cell_y

    def _coarse_route_to_world_points(self, macro_route, step, span, start_point, end_point):
        if not macro_route:
            return []
        points = [start_point]
        for macro_cell in macro_route[1:-1]:
            min_x, max_x, min_y, max_y = self._macro_cell_bounds(macro_cell, span)
            center_cell = ((min_x + max_x) // 2, (min_y + max_y) // 2)
            passable_center = self._find_nearest_passable_nav_cell(center_cell, step, search_radius=max(2, span))
            if passable_center is None:
                continue
            points.append(self._nav_cell_center(passable_center, step))
        points.append(end_point)
        return self._sparsify_nav_path(points, max(step, step * span))

    def _hierarchical_find_path(self, start, goal, start_point, end_point, step, max_height_delta_m=0.05, traversal_profile=None, max_iterations=None, max_runtime_sec=None):
        return self._astar_find_path(
            start,
            goal,
            start_point,
            end_point,
            step,
            max_height_delta_m=max_height_delta_m,
            traversal_profile=traversal_profile,
            max_iterations=max_iterations,
            max_runtime_sec=max_runtime_sec,
        )

    def _nav_profile_signature(self, traversal_profile, max_height_delta_m):
        traversal_profile = traversal_profile or {}
        return (
            round(float(max_height_delta_m), 3),
            round(float(traversal_profile.get('collision_radius', 0.0)), 2),
            bool(traversal_profile.get('can_climb_steps', False)),
        )

    def _planner_cache_key(self, goal, step, traversal_profile, max_height_delta_m):
        return (
            goal,
            int(step),
            int(self.raster_version),
            self._nav_profile_signature(traversal_profile, max_height_delta_m),
        )

    def _path_result_cache_key(self, start, goal, step, traversal_profile, max_height_delta_m):
        return (
            start,
            goal,
            int(step),
            int(self.raster_version),
            self._nav_profile_signature(traversal_profile, max_height_delta_m),
        )

    def _get_cached_path_result(self, cache_key):
        now = time.perf_counter()
        with self._path_result_cache_lock:
            entry = self._path_result_cache.get(cache_key)
            if entry is None:
                return None
            retry_after = float(entry.get('retry_after', 0.0))
            path = tuple(entry.get('path', ()))
            if not path and retry_after <= now:
                self._path_result_cache.pop(cache_key, None)
                return None
            entry['last_used'] = now
        return list(path)

    def _store_path_result(self, cache_key, path):
        now = time.perf_counter()
        normalized_path = tuple((float(point[0]), float(point[1])) for point in (path or ()))
        entry = {
            'path': normalized_path,
            'retry_after': 0.0 if normalized_path else now + self._path_failure_cache_ttl_sec,
            'last_used': now,
        }
        with self._path_result_cache_lock:
            self._path_result_cache[cache_key] = entry
            overflow = len(self._path_result_cache) - self._path_result_cache_max_entries
            if overflow > 0:
                ordered_keys = sorted(
                    self._path_result_cache,
                    key=lambda key: self._path_result_cache[key].get('last_used', 0.0),
                )
                for old_key in ordered_keys[:overflow]:
                    self._path_result_cache.pop(old_key, None)

    def _get_lpa_planner(self, goal, step, max_height_delta_m, traversal_profile=None):
        cache_key = self._planner_cache_key(goal, step, traversal_profile, max_height_delta_m)
        planner = self._lpa_planner_cache.get(cache_key)
        if planner is None:
            planner = {
                'goal': goal,
                'step': int(step),
                'traversal_profile': dict(traversal_profile or {}),
                'max_height_delta_m': float(max_height_delta_m),
                'g': {},
                'rhs': {goal: 0.0},
                'open_heap': [],
                'inconsistent': {goal},
                'query_start': goal,
                'passable_cache': {},
                'height_cache': {},
                'edge_cost_cache': {},
                'transition_cache': {},
                'last_used': time.perf_counter(),
            }
            self._lpa_rebuild_open_heap(planner)
            self._lpa_planner_cache[cache_key] = planner
            self._trim_lpa_planner_cache()
        planner['last_used'] = time.perf_counter()
        return planner

    def _trim_lpa_planner_cache(self):
        max_planners = 24
        if len(self._lpa_planner_cache) <= max_planners:
            return
        ordered = sorted(self._lpa_planner_cache.items(), key=lambda item: item[1].get('last_used', 0.0))
        for cache_key, _ in ordered[:-max_planners]:
            self._lpa_planner_cache.pop(cache_key, None)

    def _lpa_find_path(self, planner, start, start_point, end_point, max_iterations=None, max_runtime_sec=None):
        self._lpa_set_query_start(planner, start)
        solved = self._lpa_compute_shortest_path(
            planner,
            start,
            max_iterations=max_iterations,
            max_runtime_sec=max_runtime_sec,
        )
        if not solved:
            return []
        return self._lpa_reconstruct_path(planner, start, start_point, end_point)

    def _lpa_set_query_start(self, planner, start):
        if planner.get('query_start') == start:
            return
        planner['query_start'] = start
        self._lpa_rebuild_open_heap(planner)

    def _lpa_rebuild_open_heap(self, planner):
        planner['open_heap'] = []
        for node in tuple(planner.get('inconsistent', ())):
            if not self._lpa_is_inconsistent(planner, node):
                planner['inconsistent'].discard(node)
                continue
            key = self._lpa_calculate_key(planner, node)
            heappush(planner['open_heap'], (key[0], key[1], node))

    def _lpa_g(self, planner, node):
        return float(planner['g'].get(node, float('inf')))

    def _lpa_rhs(self, planner, node):
        return float(planner['rhs'].get(node, float('inf')))

    def _lpa_is_inconsistent(self, planner, node):
        return abs(self._lpa_g(planner, node) - self._lpa_rhs(planner, node)) > 1e-6 or (
            math.isinf(self._lpa_g(planner, node)) != math.isinf(self._lpa_rhs(planner, node))
        )

    def _lpa_calculate_key(self, planner, node):
        query_start = planner.get('query_start') or planner['goal']
        best = min(self._lpa_g(planner, node), self._lpa_rhs(planner, node))
        return (
            best + self._nav_heuristic(node, query_start, planner['step']),
            best,
        )

    def _lpa_key_less(self, key_a, key_b):
        return key_a[0] < key_b[0] - 1e-6 or (
            abs(key_a[0] - key_b[0]) <= 1e-6 and key_a[1] < key_b[1] - 1e-6
        )

    def _lpa_peek_open(self, planner):
        while planner['open_heap']:
            key1, key2, node = planner['open_heap'][0]
            if node not in planner['inconsistent']:
                heappop(planner['open_heap'])
                continue
            current_key = self._lpa_calculate_key(planner, node)
            if abs(key1 - current_key[0]) > 1e-6 or abs(key2 - current_key[1]) > 1e-6:
                heappop(planner['open_heap'])
                heappush(planner['open_heap'], (current_key[0], current_key[1], node))
                continue
            return key1, key2, node
        return None

    def _lpa_pop_open(self, planner):
        top = self._lpa_peek_open(planner)
        if top is None:
            return None
        heappop(planner['open_heap'])
        return top

    def _lpa_compute_shortest_path(self, planner, start, max_iterations=None, max_runtime_sec=None):
        iteration_limit = int(max_iterations) if max_iterations is not None else 2500
        if iteration_limit <= 0:
            iteration_limit = 1
        deadline = None
        if max_runtime_sec is not None:
            runtime = float(max_runtime_sec)
            if runtime > 0:
                deadline = time.perf_counter() + runtime

        iteration_count = 0
        start_key = self._lpa_calculate_key(planner, start)
        while True:
            top = self._lpa_peek_open(planner)
            start_inconsistent = self._lpa_is_inconsistent(planner, start)
            if top is None:
                break
            top_key = (top[0], top[1])
            start_key = self._lpa_calculate_key(planner, start)
            if not (self._lpa_key_less(top_key, start_key) or start_inconsistent):
                break
            iteration_count += 1
            if iteration_count > iteration_limit:
                break
            if deadline is not None and time.perf_counter() >= deadline:
                break

            _, _, node = self._lpa_pop_open(planner)
            if self._lpa_g(planner, node) > self._lpa_rhs(planner, node):
                planner['g'][node] = self._lpa_rhs(planner, node)
                planner['inconsistent'].discard(node)
                for predecessor in self._lpa_predecessors(planner, node):
                    self._lpa_update_vertex(planner, predecessor)
            else:
                planner['g'][node] = float('inf')
                planner['inconsistent'].discard(node)
                self._lpa_update_vertex(planner, node)
                for predecessor in self._lpa_predecessors(planner, node):
                    self._lpa_update_vertex(planner, predecessor)

        return not self._lpa_is_inconsistent(planner, start) and not math.isinf(self._lpa_g(planner, start))

    def _lpa_update_vertex(self, planner, node):
        if node != planner['goal']:
            best_rhs = float('inf')
            for successor in self._lpa_successors(planner, node):
                edge_cost = self._lpa_edge_cost(planner, node, successor)
                if math.isinf(edge_cost):
                    continue
                candidate_rhs = edge_cost + self._lpa_g(planner, successor)
                if candidate_rhs < best_rhs:
                    best_rhs = candidate_rhs
            planner['rhs'][node] = best_rhs
        if self._lpa_is_inconsistent(planner, node):
            planner['inconsistent'].add(node)
            key = self._lpa_calculate_key(planner, node)
            heappush(planner['open_heap'], (key[0], key[1], node))
        else:
            planner['inconsistent'].discard(node)

    def _lpa_is_cell_passable(self, planner, cell):
        cache = planner['passable_cache']
        if cell in cache:
            return cache[cell]
        passable = self._is_nav_cell_passable(cell, planner['step'], traversal_profile=planner['traversal_profile'])
        cache[cell] = passable
        return passable

    def _lpa_cell_height(self, planner, cell):
        cache = planner['height_cache']
        if cell in cache:
            return cache[cell]
        world = self._nav_cell_center(cell, planner['step'])
        height = float(self.get_terrain_height_m(world[0], world[1]))
        cache[cell] = height
        return height

    def _lpa_transition(self, planner, current, neighbor):
        key = (current, neighbor)
        cache = planner['transition_cache']
        if key in cache:
            return cache[key]
        current_world = self._nav_cell_center(current, planner['step'])
        neighbor_world = self._nav_cell_center(neighbor, planner['step'])
        transition = self.get_step_transition(
            current_world[0],
            current_world[1],
            neighbor_world[0],
            neighbor_world[1],
            max_height_delta_m=planner['max_height_delta_m'],
        )
        cache[key] = transition
        return transition

    def _lpa_edge_cost(self, planner, current, neighbor):
        cache_key = (current, neighbor)
        cache = planner['edge_cost_cache']
        if cache_key in cache:
            return cache[cache_key]
        cost = float('inf')
        delta_x = int(neighbor[0]) - int(current[0])
        delta_y = int(neighbor[1]) - int(current[1])
        if abs(delta_x) <= 1 and abs(delta_y) <= 1 and (delta_x != 0 or delta_y != 0):
            if self._lpa_is_cell_passable(planner, current) and self._lpa_is_cell_passable(planner, neighbor):
                if self._is_nav_transition_passable(current, neighbor, planner['step'], traversal_profile=planner['traversal_profile']):
                    height_delta = self._lpa_cell_height(planner, neighbor) - self._lpa_cell_height(planner, current)
                    current_world = self._nav_cell_center(current, planner['step'])
                    neighbor_world = self._nav_cell_center(neighbor, planner['step'])
                    if height_delta <= planner['max_height_delta_m'] + 1e-6 or self._segment_touches_step_surface(current_world[0], current_world[1], neighbor_world[0], neighbor_world[1]):
                        cost = math.hypot(neighbor_world[0] - current_world[0], neighbor_world[1] - current_world[1])
        cache[cache_key] = cost
        return cost

    def _lpa_successors(self, planner, node):
        for neighbor in self._iter_nav_neighbors(node):
            if not math.isinf(self._lpa_edge_cost(planner, node, neighbor)):
                yield neighbor

    def _lpa_predecessors(self, planner, node):
        for neighbor in self._iter_nav_neighbors(node):
            if not math.isinf(self._lpa_edge_cost(planner, neighbor, node)):
                yield neighbor

    def _lpa_reconstruct_path(self, planner, start, start_point, end_point):
        if math.isinf(self._lpa_g(planner, start)):
            return []
        cells = [start]
        current = start
        visited = {start}
        max_cells = max(16, math.ceil(self.map_width / planner['step']) * math.ceil(self.map_height / planner['step']))
        while current != planner['goal'] and len(cells) <= max_cells:
            best_neighbor = None
            best_score = float('inf')
            for neighbor in self._lpa_successors(planner, current):
                edge_cost = self._lpa_edge_cost(planner, current, neighbor)
                successor_cost = self._lpa_g(planner, neighbor)
                if math.isinf(edge_cost) or math.isinf(successor_cost):
                    continue
                total_score = edge_cost + successor_cost
                if total_score < best_score - 1e-6:
                    best_score = total_score
                    best_neighbor = neighbor
                elif abs(total_score - best_score) <= 1e-6 and best_neighbor is not None:
                    current_goal_distance = self._nav_heuristic(neighbor, planner['goal'], planner['step'])
                    best_goal_distance = self._nav_heuristic(best_neighbor, planner['goal'], planner['step'])
                    if current_goal_distance < best_goal_distance:
                        best_neighbor = neighbor
            if best_neighbor is None or best_neighbor in visited:
                return []
            visited.add(best_neighbor)
            cells.append(best_neighbor)
            current = best_neighbor
        if current != planner['goal']:
            return []
        points = [start_point]
        points.extend(self._nav_cell_center(cell, planner['step']) for cell in cells[1:-1])
        points.append(end_point)
        return self._sparsify_nav_path(points, planner['step'])

    def _nav_heuristic(self, cell, target_cell, step):
        cell_world = self._nav_cell_center(cell, step)
        target_world = self._nav_cell_center(target_cell, step)
        return math.hypot(target_world[0] - cell_world[0], target_world[1] - cell_world[1])

    def _astar_find_path(self, start, goal, start_point, end_point, step, max_height_delta_m=0.05, traversal_profile=None, max_iterations=None, max_runtime_sec=None, allowed_bounds=None):
        traversal_profile = traversal_profile or {}
        if max_iterations is not None:
            iteration_limit = int(max_iterations)
        elif allowed_bounds is not None:
            min_x, max_x, min_y, max_y = allowed_bounds
            area = max(1, (max_x - min_x + 1) * (max_y - min_y + 1))
            iteration_limit = min(1400, max(160, area * 3))
        else:
            iteration_limit = 1200
        if iteration_limit <= 0:
            iteration_limit = 1
        deadline = None
        if max_runtime_sec is not None:
            runtime = float(max_runtime_sec)
            if runtime > 0.0:
                deadline = time.perf_counter() + runtime

        passable_cache = {}
        height_cache = {}
        edge_cost_cache = {}

        def is_passable(cell):
            if cell in passable_cache:
                return passable_cache[cell]
            passable_cache[cell] = self._is_nav_cell_passable(cell, step, traversal_profile=traversal_profile)
            return passable_cache[cell]

        def cell_height(cell):
            if cell in height_cache:
                return height_cache[cell]
            world = self._nav_cell_center(cell, step)
            height_cache[cell] = float(self.get_terrain_height_m(world[0], world[1]))
            return height_cache[cell]

        def edge_cost(current, neighbor):
            cache_key = (current, neighbor)
            if cache_key in edge_cost_cache:
                return edge_cost_cache[cache_key]
            cost = float('inf')
            delta_x = int(neighbor[0]) - int(current[0])
            delta_y = int(neighbor[1]) - int(current[1])
            if abs(delta_x) <= 1 and abs(delta_y) <= 1 and (delta_x != 0 or delta_y != 0):
                if is_passable(current) and is_passable(neighbor):
                    if self._is_nav_transition_passable(current, neighbor, step, traversal_profile=traversal_profile):
                        current_world = self._nav_cell_center(current, step)
                        neighbor_world = self._nav_cell_center(neighbor, step)
                        height_delta = cell_height(neighbor) - cell_height(current)
                        if height_delta <= float(max_height_delta_m) + 1e-6 or self._segment_touches_step_surface(current_world[0], current_world[1], neighbor_world[0], neighbor_world[1]):
                            cost = math.hypot(neighbor_world[0] - current_world[0], neighbor_world[1] - current_world[1])
            edge_cost_cache[cache_key] = cost
            return cost

        frontier_heap = []
        start_h = self._nav_heuristic(start, goal, step)
        heappush(frontier_heap, (start_h, start_h, 0.0, start))
        came_from = {}
        g_score = {start: 0.0}
        closed = set()
        iterations = 0

        while frontier_heap:
            if deadline is not None and time.perf_counter() >= deadline:
                break
            iterations += 1
            if iterations > iteration_limit:
                break

            _, heuristic, current_g, current = heappop(frontier_heap)
            best_g = float(g_score.get(current, float('inf')))
            if current_g > best_g + 1e-6:
                continue
            if current in closed:
                continue
            if current == goal:
                return self._reconstruct_nav_path(came_from, current, step, start_point, end_point)
            closed.add(current)

            for neighbor in self._iter_nav_neighbors(current):
                if neighbor in closed:
                    continue
                if allowed_bounds is not None:
                    min_x, max_x, min_y, max_y = allowed_bounds
                    if not (min_x <= neighbor[0] <= max_x and min_y <= neighbor[1] <= max_y):
                        continue
                transition_cost = edge_cost(current, neighbor)
                if math.isinf(transition_cost):
                    continue
                tentative_g = best_g + transition_cost
                if tentative_g >= float(g_score.get(neighbor, float('inf'))) - 1e-6:
                    continue
                neighbor_h = self._nav_heuristic(neighbor, goal, step)
                came_from[neighbor] = current
                g_score[neighbor] = tentative_g
                heappush(frontier_heap, (tentative_g + neighbor_h, neighbor_h, tentative_g, neighbor))

        return []

    def _expand_greedy_frontier(self, frontier_heap, parents, seen, closed, target_cell, step, max_height_delta_m, traversal_profile=None):
        traversal_profile = traversal_profile or {}
        current = None
        while frontier_heap:
            _, candidate = heappop(frontier_heap)
            if candidate in closed:
                continue
            current = candidate
            break
        if current is None:
            return None

        closed.add(current)
        if current == target_cell:
            return current

        current_world = self._nav_cell_center(current, step)
        current_height = self.get_terrain_height_m(current_world[0], current_world[1])
        for neighbor in self._iter_nav_neighbors(current):
            if neighbor in closed or neighbor in seen:
                continue
            if not self._is_nav_cell_passable(neighbor, step, traversal_profile=traversal_profile):
                continue
            if not self._is_nav_transition_passable(current, neighbor, step, traversal_profile=traversal_profile):
                continue
            neighbor_world = self._nav_cell_center(neighbor, step)
            neighbor_height = self.get_terrain_height_m(neighbor_world[0], neighbor_world[1])
            if float(neighbor_height) - float(current_height) > float(max_height_delta_m) + 1e-6:
                if not self._segment_touches_step_surface(current_world[0], current_world[1], neighbor_world[0], neighbor_world[1]):
                    continue
            parents[neighbor] = current
            seen.add(neighbor)
            heappush(frontier_heap, (self._nav_heuristic(neighbor, target_cell, step), neighbor))
        return None

    def _point_to_nav_cell(self, point, step):
        x = max(0, min(self.map_width - 1, int(point[0])))
        y = max(0, min(self.map_height - 1, int(point[1])))
        return x // step, y // step

    def _nav_cell_center(self, cell, step):
        max_x = max(0, self.map_width - 1)
        max_y = max(0, self.map_height - 1)
        center_x = min(max_x, cell[0] * step + step // 2)
        center_y = min(max_y, cell[1] * step + step // 2)
        return center_x, center_y

    def _iter_nav_neighbors(self, cell):
        for offset_y in (-1, 0, 1):
            for offset_x in (-1, 0, 1):
                if offset_x == 0 and offset_y == 0:
                    continue
                yield cell[0] + offset_x, cell[1] + offset_y

    def _nav_cell_probe_points(self, center, clearance_radius):
        probes = [(center[0], center[1])]
        if clearance_radius <= 1.0:
            return probes
        ring_scales = (1.0, 0.66, 0.33)
        for scale in ring_scales:
            radius = clearance_radius * scale
            if radius <= 1.0:
                continue
            for angle_deg in range(0, 360, 45):
                angle_rad = math.radians(angle_deg)
                probe_x = min(max(0, int(round(center[0] + math.cos(angle_rad) * radius))), self.map_width - 1)
                probe_y = min(max(0, int(round(center[1] + math.sin(angle_rad) * radius))), self.map_height - 1)
                probes.append((probe_x, probe_y))
        return probes

    def _is_nav_cell_passable(self, cell, step, traversal_profile=None):
        max_cell_x = math.ceil(self.map_width / step)
        max_cell_y = math.ceil(self.map_height / step)
        if not (0 <= cell[0] < max_cell_x and 0 <= cell[1] < max_cell_y):
            return False
        center = self._nav_cell_center(cell, step)
        traversal_profile = traversal_profile or {}
        collision_radius = max(0.0, float(traversal_profile.get('collision_radius', 0.0)))
        clearance_radius = collision_radius
        for probe_x, probe_y in self._nav_cell_probe_points(center, clearance_radius):
            sample = self.sample_raster_layers(probe_x, probe_y)
            if sample['move_blocked']:
                return False
        return True

    def _find_nearest_passable_nav_cell(self, cell, step, search_radius=4, traversal_profile=None):
        if self._is_nav_cell_passable(cell, step, traversal_profile=traversal_profile):
            return cell
        for radius in range(1, search_radius + 1):
            for offset_y in range(-radius, radius + 1):
                for offset_x in range(-radius, radius + 1):
                    candidate = cell[0] + offset_x, cell[1] + offset_y
                    if self._is_nav_cell_passable(candidate, step, traversal_profile=traversal_profile):
                        return candidate
        return None

    def _is_nav_transition_passable(self, current, neighbor, step, traversal_profile=None):
        traversal_profile = traversal_profile or {}
        delta_x = int(neighbor[0]) - int(current[0])
        delta_y = int(neighbor[1]) - int(current[1])
        if abs(delta_x) > 1 or abs(delta_y) > 1:
            return False
        if delta_x != 0 and delta_y != 0:
            side_a = (current[0] + delta_x, current[1])
            side_b = (current[0], current[1] + delta_y)
            if not self._is_nav_cell_passable(side_a, step, traversal_profile=traversal_profile):
                return False
            if not self._is_nav_cell_passable(side_b, step, traversal_profile=traversal_profile):
                return False
        start_world = self._nav_cell_center(current, step)
        end_world = self._nav_cell_center(neighbor, step)
        traversal = self.describe_segment_traversal(
            start_world[0],
            start_world[1],
            end_world[0],
            end_world[1],
        )
        if isinstance(traversal, dict) and str(traversal.get('facility_type', '')) == 'fly_slope':
            if str(traversal.get('direction', 'forward')) != 'forward':
                return False
        collision_radius = float(traversal_profile.get('collision_radius', 0.0))
        return self.is_segment_valid_for_radius(
            start_world[0],
            start_world[1],
            end_world[0],
            end_world[1],
            collision_radius=collision_radius,
            sample_stride=max(2.0, float(step) * 0.35),
        )

    def _reconstruct_nav_path(self, came_from, current, step, start_point, end_point):
        cells = [current]
        while current in came_from:
            current = came_from[current]
            cells.append(current)
        cells.reverse()
        points = [start_point]
        points.extend(self._nav_cell_center(cell, step) for cell in cells[1:-1])
        points.append(end_point)
        return self._sparsify_nav_path(points, step)

    def _sparsify_nav_path(self, points, step):
        if len(points) <= 2:
            return points
        min_spacing = max(float(step) * 1.6, 24.0)
        collinear_tol = max(float(step) * 0.35, 6.0)
        simplified = [points[0]]
        for index in range(1, len(points) - 1):
            previous = simplified[-1]
            current = points[index]
            nxt = points[index + 1]
            if math.hypot(current[0] - previous[0], current[1] - previous[1]) < min_spacing:
                continue
            if self._is_almost_collinear(previous, current, nxt, collinear_tol):
                continue
            simplified.append(current)
        simplified.append(points[-1])
        if len(simplified) > 2:
            compact = [simplified[0]]
            for point in simplified[1:-1]:
                if math.hypot(point[0] - compact[-1][0], point[1] - compact[-1][1]) >= min_spacing:
                    compact.append(point)
            compact.append(simplified[-1])
            simplified = compact
        return simplified

    def _is_almost_collinear(self, point_a, point_b, point_c, tolerance):
        ab_x = float(point_b[0]) - float(point_a[0])
        ab_y = float(point_b[1]) - float(point_a[1])
        ac_x = float(point_c[0]) - float(point_a[0])
        ac_y = float(point_c[1]) - float(point_a[1])
        cross = abs(ab_x * ac_y - ab_y * ac_x)
        baseline = max(math.hypot(ac_x, ac_y), 1e-6)
        return (cross / baseline) <= float(tolerance)

    def _step_ascent_direction(self, facility):
        team = facility.get('team')
        if team == 'red':
            return -1
        if team == 'blue':
            return 1
        center_y = (facility['y1'] + facility['y2']) / 2.0
        return -1 if center_y >= self.map_height / 2.0 else 1

    def _segment_intersects_rect_region(self, start_point, end_point, region, padding=0.0):
        x1 = min(float(region['x1']), float(region['x2'])) - float(padding)
        x2 = max(float(region['x1']), float(region['x2'])) + float(padding)
        y1 = min(float(region['y1']), float(region['y2'])) - float(padding)
        y2 = max(float(region['y1']), float(region['y2'])) + float(padding)
        start = (float(start_point[0]), float(start_point[1]))
        end = (float(end_point[0]), float(end_point[1]))
        if x1 <= start[0] <= x2 and y1 <= start[1] <= y2:
            return True
        if x1 <= end[0] <= x2 and y1 <= end[1] <= y2:
            return True
        edges = (
            ((x1, y1), (x2, y1)),
            ((x2, y1), (x2, y2)),
            ((x2, y2), (x1, y2)),
            ((x1, y2), (x1, y1)),
        )
        return any(self._segments_intersect(start, end, edge_start, edge_end) for edge_start, edge_end in edges)

    def _step_transition_for_facility(self, facility, from_x, from_y, to_x, to_y, max_height_delta_m=None):
        if facility.get('type') not in {'first_step', 'second_step'}:
            return None
        direction = self._step_ascent_direction(facility)
        center_x, center_y = self.facility_center(facility)
        width = max(1.0, abs(facility['x2'] - facility['x1']))
        margin = max(float(self.terrain_grid_cell_size) * 1.5, 12.0)
        left_x = min(float(facility['x1']), float(facility['x2']))
        right_x = max(float(facility['x1']), float(facility['x2']))
        side_padding = min(width * 0.28, max(margin * 0.75, 10.0))
        usable_left = min(center_x, right_x - 1.0) if right_x - left_x <= side_padding * 2.0 else left_x + side_padding
        usable_right = max(center_x, left_x + 1.0) if right_x - left_x <= side_padding * 2.0 else right_x - side_padding
        aligned_x = max(usable_left, min(usable_right, (float(from_x) + float(to_x)) * 0.5))
        align_margin = max(width * 0.55, margin)
        if abs(float(from_x) - aligned_x) > align_margin and abs(float(to_x) - aligned_x) > align_margin:
            return None

        if direction < 0:
            entry_y = float(facility['y2'])
            top_y = float(facility['y1'])
            moving_ok = float(to_y) < float(from_y)
            crossing_ok = float(from_y) >= entry_y - margin and float(to_y) <= top_y + margin
            near_entry = abs(float(from_y) - entry_y) <= margin * 1.6 or abs(float(to_y) - entry_y) <= margin * 1.6
            near_top = abs(float(from_y) - top_y) <= margin * 1.6 or abs(float(to_y) - top_y) <= margin * 1.6
            approach_point = (int(round(aligned_x)), int(min(self.map_height - 1, float(facility['y2']) + margin)))
            top_point = (int(round(aligned_x)), int(min(float(facility['y2']) - 1.0, float(facility['y1']) + margin)))
        else:
            entry_y = float(facility['y1'])
            top_y = float(facility['y2'])
            moving_ok = float(to_y) > float(from_y)
            crossing_ok = float(from_y) <= entry_y + margin and float(to_y) >= top_y - margin
            near_entry = abs(float(from_y) - entry_y) <= margin * 1.6 or abs(float(to_y) - entry_y) <= margin * 1.6
            near_top = abs(float(from_y) - top_y) <= margin * 1.6 or abs(float(to_y) - top_y) <= margin * 1.6
            approach_point = (int(round(aligned_x)), int(max(0, float(facility['y1']) - margin)))
            top_point = (int(round(aligned_x)), int(max(float(facility['y1']) + 1.0, float(facility['y2']) - margin)))

        crosses_stair = self._segment_intersects_rect_region((from_x, from_y), (to_x, to_y), facility, padding=margin * 0.4)
        if not moving_ok or not crosses_stair or not (crossing_ok or (near_entry and near_top)):
            return None

        approach_height_m = self.get_terrain_height_m(approach_point[0], approach_point[1])
        top_height_m = self.get_terrain_height_m(top_point[0], top_point[1])
        step_height_m = abs(float(top_height_m) - float(approach_height_m))
        if step_height_m <= 1e-3:
            return None
        allowed_height = float(max_height_delta_m) if max_height_delta_m is not None else None
        if facility.get('type') == 'second_step' and allowed_height is not None:
            # 二级台阶允许更大的高度差，避免卡在第二级边缘。
            allowed_height *= 1.35
        if allowed_height is not None and step_height_m > allowed_height + 1e-6:
            return None

        climb_points = [top_point]

        return {
            'facility_id': facility.get('id'),
            'facility_type': facility.get('type'),
            'approach_point': approach_point,
            'top_point': top_point,
            'climb_points': tuple(climb_points),
            'direction': direction,
            'step_height_m': round(step_height_m, 3),
        }

    def _is_step_terrain_type(self, terrain_type):
        return str(terrain_type) in {'first_step', 'second_step'}

    def _segment_touches_step_surface(self, from_x, from_y, to_x, to_y):
        sample_points = (
            (float(from_x), float(from_y)),
            ((float(from_x) + float(to_x)) * 0.5, (float(from_y) + float(to_y)) * 0.5),
            (float(to_x), float(to_y)),
        )
        for sample_x, sample_y in sample_points:
            sample = self.sample_raster_layers(sample_x, sample_y)
            if self._is_step_terrain_type(sample.get('terrain_type', 'flat')):
                return True

        padding = max(float(self.terrain_grid_cell_size) * 0.4, 4.0)
        for facility_type in ('first_step', 'second_step'):
            for facility in self.get_facility_regions(facility_type):
                if self._segment_intersects_rect_region((from_x, from_y), (to_x, to_y), facility, padding=padding):
                    return True
        return False

    def _segment_step_alignment_heading_deg(self, from_x, from_y, to_x, to_y):
        delta_x = float(to_x) - float(from_x)
        delta_y = float(to_y) - float(from_y)
        if abs(delta_x) <= 1e-6 and abs(delta_y) <= 1e-6:
            return None
        return math.degrees(math.atan2(delta_y, delta_x))

    def _step_direction_label(self, facility, from_y, to_y):
        flow_direction = self._step_ascent_direction(facility)
        delta_y = float(to_y) - float(from_y)
        return 'up' if delta_y * flow_direction > 0.0 else 'down'

    def _segment_crosses_facility_channel(self, facility, from_x, from_y, to_x, to_y, padding=0.0):
        if not self._segment_intersects_rect_region((from_x, from_y), (to_x, to_y), facility, padding=padding):
            return False
        center_y = (float(facility.get('y1', 0.0)) + float(facility.get('y2', 0.0))) * 0.5
        flow_direction = self._step_ascent_direction(facility)
        start_side = (float(from_y) - center_y) * flow_direction
        end_side = (float(to_y) - center_y) * flow_direction
        threshold = max(2.0, float(padding))
        if abs(start_side) <= threshold or abs(end_side) <= threshold:
            return True
        return start_side * end_side < 0.0

    def _step_corridor_points(self, facility, direction_label, from_x, to_x):
        direction = self._step_ascent_direction(facility)
        center_x, _ = self.facility_center(facility)
        width = max(1.0, abs(float(facility.get('x2', 0.0)) - float(facility.get('x1', 0.0))))
        margin = max(float(self.terrain_grid_cell_size) * 1.5, 12.0)
        left_x = min(float(facility.get('x1', 0.0)), float(facility.get('x2', 0.0)))
        right_x = max(float(facility.get('x1', 0.0)), float(facility.get('x2', 0.0)))
        side_padding = min(width * 0.28, max(margin * 0.75, 10.0))
        usable_left = min(center_x, right_x - 1.0) if right_x - left_x <= side_padding * 2.0 else left_x + side_padding
        usable_right = max(center_x, left_x + 1.0) if right_x - left_x <= side_padding * 2.0 else right_x - side_padding
        aligned_x = max(usable_left, min(usable_right, (float(from_x) + float(to_x)) * 0.5))
        if direction < 0:
            lower_point = (int(round(aligned_x)), int(min(self.map_height - 1, float(facility['y2']) + margin)))
            upper_point = (int(round(aligned_x)), int(min(float(facility['y2']) - 1.0, float(facility['y1']) + margin)))
        else:
            lower_point = (int(round(aligned_x)), int(max(0, float(facility['y1']) - margin)))
            upper_point = (int(round(aligned_x)), int(max(float(facility['y1']) + 1.0, float(facility['y2']) - margin)))
        if direction_label == 'up':
            return lower_point, upper_point
        return upper_point, lower_point

    def _fly_slope_direction_label(self, facility, from_y, to_y):
        flow_direction = self._step_ascent_direction(facility)
        delta_y = float(to_y) - float(from_y)
        return 'forward' if delta_y * flow_direction > 0.0 else 'reverse'

    def _fly_slope_channel_points(self, facility, direction_label):
        center_x, _ = self.facility_center(facility)
        margin = max(float(self.terrain_grid_cell_size) * 1.35, 18.0)
        direction = self._step_ascent_direction(facility)
        if direction < 0:
            launch_point = (int(round(center_x)), int(min(self.map_height - 1, float(facility['y2']) + margin * 0.5)))
            landing_point = (int(round(center_x)), int(max(0, float(facility['y1']) - margin * 0.5)))
        else:
            launch_point = (int(round(center_x)), int(max(0, float(facility['y1']) - margin * 0.5)))
            landing_point = (int(round(center_x)), int(min(self.map_height - 1, float(facility['y2']) + margin * 0.5)))
        if direction_label == 'forward':
            return launch_point, landing_point
        return landing_point, launch_point

    def describe_segment_traversal(self, from_x, from_y, to_x, to_y, max_height_delta_m=None):
        terrain_transition = self._terrain_step_transition(from_x, from_y, to_x, to_y, max_height_delta_m=max_height_delta_m)
        if terrain_transition is not None:
            direction_label = 'up' if float(to_y) < float(from_y) else 'down'
            approach_point = terrain_transition.get('approach_point')
            top_point = terrain_transition.get('top_point')
            return {
                **terrain_transition,
                'direction': direction_label,
                'entry_point': approach_point,
                'exit_point': top_point,
            }

        padding = max(float(self.terrain_grid_cell_size) * 0.45, 6.0)
        for facility_type in ('first_step', 'second_step'):
            for facility in self.get_facility_regions(facility_type):
                transition = self._step_transition_for_facility(facility, from_x, from_y, to_x, to_y, max_height_delta_m=max_height_delta_m)
                if transition is not None:
                    direction_label = 'up'
                    return {
                        **transition,
                        'direction': direction_label,
                        'entry_point': transition.get('approach_point'),
                        'exit_point': transition.get('top_point'),
                    }
                if not self._segment_crosses_facility_channel(facility, from_x, from_y, to_x, to_y, padding=padding):
                    continue
                direction_label = self._step_direction_label(facility, from_y, to_y)
                entry_point, exit_point = self._step_corridor_points(facility, direction_label, from_x, to_x)
                entry_height = self.get_terrain_height_m(entry_point[0], entry_point[1])
                exit_height = self.get_terrain_height_m(exit_point[0], exit_point[1])
                return {
                    'facility_id': facility.get('id'),
                    'facility_type': facility_type,
                    'direction': direction_label,
                    'entry_point': entry_point,
                    'exit_point': exit_point,
                    'approach_point': entry_point,
                    'top_point': exit_point,
                    'climb_points': (exit_point,),
                    'step_height_m': round(abs(float(exit_height) - float(entry_height)), 3),
                }

        for facility in self.get_facility_regions('fly_slope'):
            if not self._segment_crosses_facility_channel(facility, from_x, from_y, to_x, to_y, padding=padding):
                continue
            direction_label = self._fly_slope_direction_label(facility, from_y, to_y)
            entry_point, exit_point = self._fly_slope_channel_points(facility, direction_label)
            return {
                'facility_id': facility.get('id'),
                'facility_type': 'fly_slope',
                'direction': direction_label,
                'entry_point': entry_point,
                'exit_point': exit_point,
                'landing_point': exit_point,
                'climb_points': (exit_point,),
            }
        return None

    def _terrain_step_transition(self, from_x, from_y, to_x, to_y, max_height_delta_m=None):
        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        max_step_span = max(float(self.terrain_grid_cell_size) * 3.2, 56.0)
        if distance <= 2.0 or distance > max_step_span:
            return None

        start_sample = self.sample_raster_layers(from_x, from_y)
        end_sample = self.sample_raster_layers(to_x, to_y)
        if start_sample['move_blocked'] or end_sample['move_blocked']:
            return None

        step_limit = float(max_height_delta_m if max_height_delta_m is not None else 0.23)
        total_height = float(end_sample['height_m']) - float(start_sample['height_m'])
        if total_height <= 1e-3 or total_height > step_limit + 1e-6:
            return None

        stride = max(1.0, float(self.terrain_grid_cell_size) * 0.35)
        sample_count = max(3, int(math.ceil(distance / stride)))
        samples = []
        for sample_index in range(sample_count + 1):
            ratio = sample_index / sample_count
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            sample = self.sample_raster_layers(sample_x, sample_y)
            samples.append({
                'x': sample_x,
                'y': sample_y,
                'blocked': bool(sample['move_blocked']),
                'height_m': float(sample['height_m']),
            })

        if any(sample['blocked'] for sample in samples):
            return None

        edge_start = None
        edge_end = None
        edge_threshold = max(0.02, min(step_limit * 0.35, 0.08))
        for index in range(1, len(samples)):
            previous = samples[index - 1]
            current = samples[index]
            delta = abs(float(current['height_m']) - float(previous['height_m']))
            if delta >= edge_threshold:
                if edge_start is None:
                    edge_start = max(0, index - 1)
                edge_end = index

        if edge_start is None:
            return None

        edge_samples = max(1, edge_end - edge_start + 1)
        if edge_samples > max(3, int(sample_count * 0.25)):
            return None

        approach_index = max(0, edge_start)
        top_index = min(len(samples) - 1, edge_end)
        approach = samples[approach_index]
        top = samples[top_index]
        edge_span = math.hypot(float(top['x']) - float(approach['x']), float(top['y']) - float(approach['y']))
        if edge_span > max(float(self.terrain_grid_cell_size) * 1.8, 28.0):
            return None

        step_height_m = abs(float(top['height_m']) - float(approach['height_m']))
        if step_height_m <= 1e-3 or step_height_m > step_limit + 1e-6:
            return None

        return {
            'facility_id': 'terrain_step',
            'facility_type': 'terrain_step',
            'approach_point': (int(round(approach['x'])), int(round(approach['y']))),
            'top_point': (int(round(top['x'])), int(round(top['y']))),
            'direction': 0,
            'step_height_m': round(step_height_m, 3),
        }

    def get_step_transition(self, from_x, from_y, to_x, to_y, max_height_delta_m=None):
        terrain_transition = self._terrain_step_transition(from_x, from_y, to_x, to_y, max_height_delta_m=max_height_delta_m)
        if terrain_transition is not None:
            return terrain_transition
        for facility_type in ('first_step', 'second_step'):
            for facility in self.get_facility_regions(facility_type):
                transition = self._step_transition_for_facility(facility, from_x, from_y, to_x, to_y, max_height_delta_m=max_height_delta_m)
                if transition is not None:
                    return transition
        return None

    def evaluate_movement_path(self, from_x, from_y, to_x, to_y, max_height_delta_m=0.05, collision_radius=0.0, angle_deg=None, body_length_m=None, body_width_m=None, body_clearance_m=0.0):
        start_sample = self.sample_raster_layers(from_x, from_y)
        end_sample = self.sample_raster_layers(to_x, to_y)
        use_chassis_box = body_length_m is not None and body_width_m is not None and float(body_length_m) > 0.0 and float(body_width_m) > 0.0
        if use_chassis_box:
            pose_valid = self.is_position_valid_for_chassis(
                to_x,
                to_y,
                angle_deg if angle_deg is not None else 0.0,
                body_length_m,
                body_width_m,
                body_clearance_m=body_clearance_m,
            )
        else:
            pose_valid = self.is_position_valid_for_radius(to_x, to_y, collision_radius=collision_radius)
        if not pose_valid:
            return {
                'ok': False,
                'reason': 'blocked',
                'start_height_m': start_sample['height_m'],
                'end_height_m': end_sample['height_m'],
            }

        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        sample_stride = max(1.0, self.terrain_grid_cell_size * 0.5)
        sample_count = max(1, int(math.ceil(distance / sample_stride)))
        previous_height = float(start_sample['height_m'])
        previous_x = float(from_x)
        previous_y = float(from_y)
        requires_step_alignment = False
        step_heading_deg = None

        for step_index in range(1, sample_count + 1):
            ratio = step_index / sample_count
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            if not self.is_directionally_passable_segment(previous_x, previous_y, sample_x, sample_y, collision_radius=collision_radius, sample_stride=sample_stride):
                return {
                    'ok': False,
                    'reason': 'conditional_direction',
                    'start_height_m': start_sample['height_m'],
                    'end_height_m': end_sample['height_m'],
                }
            if use_chassis_box:
                sample_pose_valid = self.is_position_valid_for_chassis(
                    sample_x,
                    sample_y,
                    angle_deg if angle_deg is not None else 0.0,
                    body_length_m,
                    body_width_m,
                    body_clearance_m=body_clearance_m,
                )
            else:
                sample_pose_valid = self.is_position_valid_for_radius(sample_x, sample_y, collision_radius=collision_radius)
            if not sample_pose_valid:
                return {
                    'ok': False,
                    'reason': 'blocked',
                    'start_height_m': start_sample['height_m'],
                    'end_height_m': end_sample['height_m'],
                }
            sample = self.sample_raster_layers(sample_x, sample_y)
            if sample['move_blocked']:
                return {
                    'ok': False,
                    'reason': 'blocked',
                    'start_height_m': start_sample['height_m'],
                    'end_height_m': sample['height_m'],
                }
            height_delta = float(sample['height_m']) - previous_height
            if height_delta > float(max_height_delta_m) + 1e-6:
                if self._segment_touches_step_surface(previous_x, previous_y, sample_x, sample_y):
                    requires_step_alignment = True
                    if step_heading_deg is None:
                        step_heading_deg = self._segment_step_alignment_heading_deg(previous_x, previous_y, sample_x, sample_y)
                else:
                    return {
                        'ok': False,
                        'reason': 'height_delta',
                        'start_height_m': start_sample['height_m'],
                        'end_height_m': sample['height_m'],
                        'height_delta_m': round(height_delta, 3),
                    }
            previous_height = float(sample['height_m'])
            previous_x = sample_x
            previous_y = sample_y

        if requires_step_alignment and step_heading_deg is None:
            step_heading_deg = self._segment_step_alignment_heading_deg(from_x, from_y, to_x, to_y)

        return {
            'ok': True,
            'reason': 'ok',
            'start_height_m': start_sample['height_m'],
            'end_height_m': end_sample['height_m'],
            'height_delta_m': round(max(0.0, float(end_sample['height_m']) - float(start_sample['height_m'])), 3),
            'requires_step_alignment': requires_step_alignment,
            'step_heading_deg': step_heading_deg,
        }

    def trace_movement_obstacle(self, from_x, from_y, to_x, to_y, max_height_delta_m=0.05, collision_radius=0.0, max_distance=None):
        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        if distance <= 1e-6:
            return None

        trace_distance = distance
        if max_distance is not None:
            trace_distance = min(trace_distance, max(0.0, float(max_distance)))
        if trace_distance <= 1e-6:
            return None

        ratio_limit = min(1.0, trace_distance / max(distance, 1e-6))
        start_sample = self.sample_raster_layers(from_x, from_y)
        previous_height = float(start_sample['height_m'])
        previous_x = float(from_x)
        previous_y = float(from_y)
        sample_stride = max(1.0, float(self.terrain_grid_cell_size) * 0.35)
        sample_count = max(1, int(math.ceil(trace_distance / sample_stride)))

        for step_index in range(1, sample_count + 1):
            ratio = ratio_limit * (step_index / sample_count)
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            if not self.is_directionally_passable_segment(previous_x, previous_y, sample_x, sample_y, collision_radius=collision_radius, sample_stride=sample_stride):
                return {
                    'reason': 'conditional_direction',
                    'point': (float(sample_x), float(sample_y)),
                    'previous_point': (float(previous_x), float(previous_y)),
                    'distance': trace_distance * (step_index / sample_count),
                }
            if not self.is_position_valid_for_radius(sample_x, sample_y, collision_radius=collision_radius):
                return {
                    'reason': 'blocked',
                    'point': (float(sample_x), float(sample_y)),
                    'previous_point': (float(previous_x), float(previous_y)),
                    'distance': trace_distance * (step_index / sample_count),
                }
            sample = self.sample_raster_layers(sample_x, sample_y)
            if sample['move_blocked']:
                return {
                    'reason': 'blocked',
                    'point': (float(sample_x), float(sample_y)),
                    'previous_point': (float(previous_x), float(previous_y)),
                    'distance': trace_distance * (step_index / sample_count),
                }
            height_delta = float(sample['height_m']) - previous_height
            if height_delta > float(max_height_delta_m) + 1e-6:
                if not self._segment_touches_step_surface(previous_x, previous_y, sample_x, sample_y):
                    return {
                        'reason': 'height_delta',
                        'point': (float(sample_x), float(sample_y)),
                        'previous_point': (float(previous_x), float(previous_y)),
                        'distance': trace_distance * (step_index / sample_count),
                    }
            previous_height = float(sample['height_m'])
            previous_x = sample_x
            previous_y = sample_y
        return None

    def estimate_obstacle_extension_direction(self, obstacle_point, approach_point=None, sample_radius=None, sample_step=None):
        self._ensure_raster_layers()
        if obstacle_point is None:
            return None

        center_x = float(obstacle_point[0])
        center_y = float(obstacle_point[1])
        radius = max(float(sample_radius or 0.0), float(self.terrain_grid_cell_size) * 2.5, 20.0)
        step = max(1, int(round(sample_step or max(2.0, float(self.terrain_grid_cell_size) * 0.5))))
        blocked_points = []
        min_x = max(0, int(math.floor(center_x - radius)))
        max_x = min(self.map_width - 1, int(math.ceil(center_x + radius)))
        min_y = max(0, int(math.floor(center_y - radius)))
        max_y = min(self.map_height - 1, int(math.ceil(center_y + radius)))
        for sample_y in range(min_y, max_y + 1, step):
            for sample_x in range(min_x, max_x + 1, step):
                if math.hypot(sample_x - center_x, sample_y - center_y) > radius + step * 0.5:
                    continue
                if self.sample_raster_layers(sample_x, sample_y)['move_blocked']:
                    blocked_points.append((float(sample_x), float(sample_y)))

        tangent = None
        if len(blocked_points) >= 2:
            samples = np.array(blocked_points, dtype=np.float32)
            centered = samples - np.mean(samples, axis=0, keepdims=True)
            covariance = centered.T @ centered
            eigenvalues, eigenvectors = np.linalg.eigh(covariance)
            principal = eigenvectors[:, int(np.argmax(eigenvalues))]
            norm = float(np.linalg.norm(principal))
            if norm > 1e-6:
                tangent = (float(principal[0] / norm), float(principal[1] / norm))

        if tangent is None and approach_point is not None:
            approach_dx = float(obstacle_point[0]) - float(approach_point[0])
            approach_dy = float(obstacle_point[1]) - float(approach_point[1])
            approach_length = math.hypot(approach_dx, approach_dy)
            if approach_length > 1e-6:
                tangent = (-approach_dy / approach_length, approach_dx / approach_length)

        if tangent is None:
            return None

        normal = (-float(tangent[1]), float(tangent[0]))
        if approach_point is not None:
            outward = (float(approach_point[0]) - center_x, float(approach_point[1]) - center_y)
            if normal[0] * outward[0] + normal[1] * outward[1] < 0.0:
                normal = (-normal[0], -normal[1])
        return {
            'tangent': tangent,
            'normal': normal,
        }
    
    def is_position_valid(self, x, y):
        """检查位置是否有效（不在障碍物上）"""
        sample = self.sample_raster_layers(x, y)
        return not sample['move_blocked']

    def _chassis_half_extents_world(self, body_length_m, body_width_m):
        half_length = max(0.0, float(self.meters_to_world_units(float(body_length_m) * 0.5)))
        half_width = max(0.0, float(self.meters_to_world_units(float(body_width_m) * 0.5)))
        return half_length, half_width

    def _chassis_sample_points(self, x, y, angle_deg, body_length_m, body_width_m, sample_step_world=None):
        half_length, half_width = self._chassis_half_extents_world(body_length_m, body_width_m)
        if half_length <= 1e-6 or half_width <= 1e-6:
            return ((float(x), float(y), 0.0, 0.0),)
        sample_step = max(
            max(self.runtime_cell_width_world, self.runtime_cell_height_world) * 0.8,
            float(sample_step_world or self.meters_to_world_units(0.10)),
            1.0,
        )
        length_steps = max(1, int(math.ceil((half_length * 2.0) / max(sample_step, 1e-6))))
        width_steps = max(1, int(math.ceil((half_width * 2.0) / max(sample_step, 1e-6))))
        yaw_rad = math.radians(float(angle_deg))
        forward_x = math.cos(yaw_rad)
        forward_y = math.sin(yaw_rad)
        right_x = -forward_y
        right_y = forward_x
        samples = []
        for length_index in range(-length_steps, length_steps + 1):
            local_x = half_length * (float(length_index) / max(length_steps, 1))
            for width_index in range(-width_steps, width_steps + 1):
                local_y = half_width * (float(width_index) / max(width_steps, 1))
                sample_x = float(x) + forward_x * local_x + right_x * local_y
                sample_y = float(y) + forward_y * local_x + right_y * local_y
                samples.append((sample_x, sample_y, local_x, local_y))
        return tuple(samples)

    def _chassis_bottom_plane_height(self, x, y, angle_deg, body_length_m, body_width_m, body_clearance_m):
        center_height = float(self.get_terrain_height_m(x, y))
        half_length, half_width = self._chassis_half_extents_world(body_length_m, body_width_m)
        if half_length <= 1e-6 or half_width <= 1e-6:
            return center_height + float(body_clearance_m), 0.0, 0.0
        yaw_rad = math.radians(float(angle_deg))
        forward_x = math.cos(yaw_rad)
        forward_y = math.sin(yaw_rad)
        right_x = -forward_y
        right_y = forward_x
        front_height = float(self.get_terrain_height_m(float(x) + forward_x * half_length, float(y) + forward_y * half_length))
        rear_height = float(self.get_terrain_height_m(float(x) - forward_x * half_length, float(y) - forward_y * half_length))
        left_height = float(self.get_terrain_height_m(float(x) + right_x * half_width, float(y) + right_y * half_width))
        right_height = float(self.get_terrain_height_m(float(x) - right_x * half_width, float(y) - right_y * half_width))
        forward_slope = (front_height - rear_height) / max(half_length * 2.0, 1e-6)
        right_slope = (left_height - right_height) / max(half_width * 2.0, 1e-6)
        return center_height + float(body_clearance_m), forward_slope, right_slope

    def is_position_valid_for_chassis(self, x, y, angle_deg, body_length_m, body_width_m, body_clearance_m=0.0):
        self._ensure_raster_layers()
        half_length, half_width = self._chassis_half_extents_world(body_length_m, body_width_m)
        if half_length <= 1e-6 or half_width <= 1e-6:
            return self.is_position_valid_for_radius(x, y, collision_radius=0.0)
        base_bottom_height, forward_slope, right_slope = self._chassis_bottom_plane_height(x, y, angle_deg, body_length_m, body_width_m, body_clearance_m)
        height_slop = max(0.02, float(body_clearance_m) * 0.1)
        for sample_x, sample_y, local_x, local_y in self._chassis_sample_points(x, y, angle_deg, body_length_m, body_width_m):
            if not (0 <= int(sample_x) < self.map_width and 0 <= int(sample_y) < self.map_height):
                return False
            if self._is_hard_blocked_world(sample_x, sample_y):
                return False
            sample = self.sample_raster_layers(sample_x, sample_y)
            terrain_height = float(sample.get('height_m', 0.0))
            support_height = base_bottom_height + forward_slope * local_x + right_slope * local_y
            if terrain_height > support_height + height_slop:
                return False
        return True

    def is_segment_valid_for_chassis(self, from_x, from_y, to_x, to_y, angle_deg, body_length_m, body_width_m, body_clearance_m=0.0, collision_radius=0.0, sample_stride=None):
        self._ensure_raster_layers()
        if not self.is_directionally_passable_segment(from_x, from_y, to_x, to_y, collision_radius=collision_radius, sample_stride=sample_stride):
            return False
        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        stride = max(1.0, float(sample_stride or self.terrain_grid_cell_size * 0.35))
        sample_count = max(1, int(math.ceil(distance / stride)))
        for sample_index in range(sample_count + 1):
            ratio = sample_index / max(sample_count, 1)
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            if not self.is_position_valid_for_chassis(sample_x, sample_y, angle_deg, body_length_m, body_width_m, body_clearance_m=body_clearance_m):
                return False
        return True

    def is_position_valid_for_radius(self, x, y, collision_radius=0.0):
        self._ensure_raster_layers()
        if not (0 <= int(x) < self.map_width and 0 <= int(y) < self.map_height):
            return False
        clearance_radius = max(0.0, float(collision_radius))
        center_x, center_y = self._world_to_runtime_cell(x, y)
        radius_cells_x = max(0, int(math.ceil(clearance_radius / max(self.runtime_cell_width_world, 1e-6))))
        radius_cells_y = max(0, int(math.ceil(clearance_radius / max(self.runtime_cell_height_world, 1e-6))))
        min_x = max(0, center_x - radius_cells_x)
        max_x = min(self.runtime_grid_width - 1, center_x + radius_cells_x)
        min_y = max(0, center_y - radius_cells_y)
        max_y = min(self.runtime_grid_height - 1, center_y + radius_cells_y)
        block_view = self._hard_movement_block_view(min_x, max_x, min_y, max_y)
        if not block_view.any():
            return True
        if clearance_radius <= 1e-6:
            return not bool(block_view[center_y - min_y, center_x - min_x])

        cell_xs, cell_ys = self._runtime_world_coordinate_axes(min_x, max_x, min_y, max_y)
        x_grid, y_grid = np.meshgrid(cell_xs, cell_ys)
        distance_limit = clearance_radius + max(self.runtime_cell_width_world, self.runtime_cell_height_world) * 0.55
        within_radius = np.hypot(x_grid - float(x), y_grid - float(y)) <= distance_limit
        return not bool(np.any(block_view & within_radius))

    def is_segment_valid_for_radius(self, from_x, from_y, to_x, to_y, collision_radius=0.0, sample_stride=None):
        self._ensure_raster_layers()
        if not self.is_directionally_passable_segment(from_x, from_y, to_x, to_y, collision_radius=collision_radius, sample_stride=sample_stride):
            return False
        distance = math.hypot(float(to_x) - float(from_x), float(to_y) - float(from_y))
        stride = max(1.0, float(sample_stride or self.terrain_grid_cell_size * 0.35))
        sample_count = max(1, int(math.ceil(distance / stride)))
        radius = max(0.0, float(collision_radius))
        normal_x = 0.0
        normal_y = 0.0
        if distance > 1e-6:
            normal_x = -(float(to_y) - float(from_y)) / distance
            normal_y = (float(to_x) - float(from_x)) / distance
        for sample_index in range(sample_count + 1):
            ratio = sample_index / sample_count
            sample_x = float(from_x) + (float(to_x) - float(from_x)) * ratio
            sample_y = float(from_y) + (float(to_y) - float(from_y)) * ratio
            if not self.is_position_valid_for_radius(sample_x, sample_y, collision_radius=radius):
                return False
            if radius > 1.0 and distance > 1e-6:
                edge_points = (
                    (sample_x + normal_x * radius, sample_y + normal_y * radius),
                    (sample_x - normal_x * radius, sample_y - normal_y * radius),
                )
                for edge_x, edge_y in edge_points:
                    if not self.is_position_valid_for_radius(edge_x, edge_y, collision_radius=0.0):
                        return False
        return True

    def find_nearest_passable_point(self, point, collision_radius=0.0, search_radius=96, step=6):
        self._ensure_raster_layers()
        if point is None:
            return None
        center_x = int(round(point[0]))
        center_y = int(round(point[1]))
        if self.is_position_valid_for_radius(center_x, center_y, collision_radius=collision_radius):
            return center_x, center_y
        step = max(1, int(step))
        radius_limit = max(step, int(search_radius))
        for radius in range(step, radius_limit + step, step):
            for offset_y in range(-radius, radius + 1, step):
                for offset_x in range(-radius, radius + 1, step):
                    if abs(offset_x) != radius and abs(offset_y) != radius:
                        continue
                    candidate_x = min(max(0, center_x + offset_x), self.map_width - 1)
                    candidate_y = min(max(0, center_y + offset_y), self.map_height - 1)
                    if self.is_position_valid_for_radius(candidate_x, candidate_y, collision_radius=collision_radius):
                        return candidate_x, candidate_y
        return center_x, center_y
    
    def convert_world_to_screen(self, world_x, world_y):
        """将世界坐标转换为屏幕坐标"""
        screen_x = int((world_x - self.origin_x) * self.scale)
        screen_y = int((world_y - self.origin_y) * self.scale)
        return screen_x, screen_y
    
    def convert_screen_to_world(self, screen_x, screen_y):
        """将屏幕坐标转换为世界坐标"""
        world_x = (screen_x / self.scale) + self.origin_x
        world_y = (screen_y / self.scale) + self.origin_y
        return world_x, world_y

    def pixels_per_meter_x(self):
        return self.map_width / max(self.field_length_m, 1e-6)

    def pixels_per_meter_y(self):
        return self.map_height / max(self.field_width_m, 1e-6)

    def meters_to_world_units(self, meters):
        avg_pixels_per_meter = (self.pixels_per_meter_x() + self.pixels_per_meter_y()) / 2.0
        return meters * avg_pixels_per_meter
