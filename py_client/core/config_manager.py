#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import os
import tempfile
from copy import deepcopy
from datetime import datetime

class ConfigManager:
    DEFAULT_COMMON_SETTINGS_NAME = 'CommonSetting.json'
    LEGACY_SETTINGS_NAME = 'settings.json'
    MAP_DOCUMENT_EXTENSIONS = ('.json', '.lz4')

    def __init__(self):
        self.config = {}

    def _normalize_settings_path(self, settings_path, config_path=None):
        if not settings_path:
            return None
        if os.path.isabs(settings_path):
            return settings_path
        return os.path.join(self._workspace_root(config_path), str(settings_path))

    def default_settings_path(self, config_path=None):
        return os.path.join(self._workspace_root(config_path), self.DEFAULT_COMMON_SETTINGS_NAME)

    def resolve_settings_path(self, config_path=None, settings_path=None):
        explicit_path = self._normalize_settings_path(settings_path, config_path)
        if explicit_path:
            return explicit_path
        common_path = self.default_settings_path(config_path)
        if os.path.exists(common_path):
            return common_path
        legacy_path = self._normalize_settings_path(self.LEGACY_SETTINGS_NAME, config_path)
        if legacy_path and os.path.exists(legacy_path):
            return legacy_path
        return common_path

    def _map_folder_preset_path(self, preset_name, config_path=None):
        name = os.path.splitext(str(preset_name or '').strip())[0]
        return os.path.join(self._workspace_root(config_path), 'maps', name, 'map.json')

    def _candidate_map_document_paths(self, base_path):
        root, ext = os.path.splitext(str(base_path or ''))
        if ext.lower() in self.MAP_DOCUMENT_EXTENSIONS:
            yield base_path
            for candidate_ext in self.MAP_DOCUMENT_EXTENSIONS:
                if candidate_ext != ext.lower():
                    yield root + candidate_ext
            return

        for candidate_ext in self.MAP_DOCUMENT_EXTENSIONS:
            yield str(base_path or '') + candidate_ext

    def _workspace_root(self, config_path=None):
        path = config_path or self.config.get('_config_path', 'config.json')
        return os.path.dirname(os.path.abspath(path))
    
    def _read_json(self, config_path):
        # Use utf-8-sig so files saved with BOM can still be parsed.
        with open(config_path, 'r', encoding='utf-8-sig') as f:
            return json.load(f)

    def _read_map_document(self, path):
        ext = os.path.splitext(str(path or ''))[1].lower()
        if ext == '.lz4':
            try:
                import lz4.frame
            except ImportError as exc:
                raise RuntimeError('读取 .lz4 地图预设需要安装 Python 包 lz4') from exc
            with lz4.frame.open(path, 'rt', encoding='utf-8-sig') as f:
                return json.load(f)
        return self._read_json(path)

    def _atomic_write_json(self, path, payload):
        directory = os.path.dirname(os.path.abspath(path))
        if directory:
            os.makedirs(directory, exist_ok=True)
        fd, temp_path = tempfile.mkstemp(
            prefix=f'.{os.path.basename(path)}.',
            suffix='.tmp',
            dir=directory or None,
            text=True,
        )
        try:
            with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as f:
                json.dump(payload, f, ensure_ascii=False, indent=2)
                f.write('\n')
                f.flush()
                os.fsync(f.fileno())
            os.replace(temp_path, path)
            try:
                dir_fd = os.open(directory or '.', os.O_RDONLY)
                try:
                    os.fsync(dir_fd)
                finally:
                    os.close(dir_fd)
            except (AttributeError, OSError):
                pass
        except Exception:
            try:
                os.unlink(temp_path)
            except OSError:
                pass
            raise

    def _deep_merge(self, base, override):
        if not isinstance(base, dict) or not isinstance(override, dict):
            return deepcopy(override)

        merged = deepcopy(base)
        for key, value in override.items():
            if key in merged and isinstance(merged[key], dict) and isinstance(value, dict):
                merged[key] = self._deep_merge(merged[key], value)
            else:
                merged[key] = deepcopy(value)
        return merged

    def _resolve_map_preset_path(self, preset_name, config_path=None):
        if not preset_name:
            return None
        preset_ref = str(preset_name).strip()
        if not preset_ref:
            return None
        if os.path.isabs(preset_ref):
            for candidate in self._candidate_map_document_paths(preset_ref):
                if os.path.exists(candidate):
                    return candidate
            return preset_ref
        workspace_root = self._workspace_root(config_path)
        normalized_ref = preset_ref.replace('\\', '/').strip('/')
        if '/' in normalized_ref:
            relative_base = os.path.join(workspace_root, normalized_ref)
            for candidate in self._candidate_map_document_paths(relative_base):
                if os.path.exists(candidate):
                    return candidate
            return relative_base

        folder_base = os.path.join(workspace_root, 'maps', preset_ref, 'map')
        for candidate in self._candidate_map_document_paths(folder_base):
            if os.path.exists(candidate):
                return candidate

        legacy_base = os.path.join(workspace_root, 'map_presets', os.path.splitext(preset_ref)[0])
        for candidate in self._candidate_map_document_paths(legacy_base):
            if os.path.exists(candidate):
                return candidate

        return self._map_folder_preset_path(preset_ref, config_path)

    def list_map_presets(self, config_path=None):
        workspace_root = self._workspace_root(config_path)
        discovered = []
        seen = set()

        maps_dir = os.path.join(workspace_root, 'maps')
        if os.path.isdir(maps_dir):
            for entry in sorted(os.listdir(maps_dir)):
                map_base = os.path.join(maps_dir, entry, 'map')
                if not any(os.path.isfile(candidate) for candidate in self._candidate_map_document_paths(map_base)):
                    continue
                if entry not in seen:
                    discovered.append(entry)
                    seen.add(entry)

        legacy_dir = os.path.join(workspace_root, 'map_presets')
        if os.path.isdir(legacy_dir):
            for entry in sorted(os.listdir(legacy_dir)):
                if os.path.splitext(entry)[1].lower() not in self.MAP_DOCUMENT_EXTENSIONS:
                    continue
                name = os.path.splitext(entry)[0]
                if name not in seen:
                    discovered.append(name)
                    seen.add(name)
        return discovered

    def _load_map_preset_payload(self, preset_name, config_path=None, ancestry=None):
        ancestry = ancestry or set()
        ancestry_key = str(preset_name or '').strip().lower()
        if ancestry_key in ancestry:
            raise ValueError(f'地图预设继承出现循环: {preset_name}')
        ancestry.add(ancestry_key)

        preset_path = self._resolve_map_preset_path(preset_name, config_path)
        if preset_path is None or not os.path.exists(preset_path):
            return None, None
        payload = self._read_map_document(preset_path)
        if isinstance(payload, dict) and payload.get('extends'):
            base_payload, _ = self._load_map_preset_payload(payload.get('extends'), config_path, ancestry)
            if isinstance(base_payload, dict):
                payload = self._deep_merge(base_payload, payload)
        return payload, preset_path

    def load_map_preset(self, preset_name, config_path=None):
        payload, preset_path = self._load_map_preset_payload(preset_name, config_path)
        if not isinstance(payload, dict):
            return None
        if isinstance(payload, dict) and isinstance(payload.get('map'), dict):
            preset_map = payload['map']
        else:
            preset_map = payload
        if not isinstance(preset_map, dict):
            return None
        preset_map = deepcopy(preset_map)
        preset_map['_preset_path'] = preset_path
        return preset_map

    def _resolve_behavior_preset_path(self, preset_name, config_path=None):
        if not preset_name:
            return None
        preset_ref = str(preset_name).strip()
        if not preset_ref:
            return None
        if os.path.isabs(preset_ref):
            return preset_ref
        if not preset_ref.lower().endswith('.json'):
            preset_ref = f'{preset_ref}.json'
        return os.path.join(self._workspace_root(config_path), 'behavior_presets', preset_ref)

    def load_behavior_preset(self, preset_name, config_path=None):
        preset_path = self._resolve_behavior_preset_path(preset_name, config_path)
        if preset_path is None or not os.path.exists(preset_path):
            return None
        payload = self._read_json(preset_path)
        if not isinstance(payload, dict):
            return None
        return deepcopy(payload)

    def _apply_map_preset(self, config, config_path=None):
        preset_name = config.get('map', {}).get('preset')
        if not preset_name:
            return config
        preset_map = self.load_map_preset(preset_name, config_path)
        if not preset_map:
            return config
        merged = deepcopy(config)
        merged['map'] = self._deep_merge(merged.get('map', {}), preset_map)
        merged['map']['preset'] = preset_name
        if preset_map.get('_preset_path'):
            merged['map']['_preset_path'] = preset_map.get('_preset_path')
        return merged

    def load_config(self, config_path, settings_path=None):
        """加载基础配置，并叠加本地 setting 覆盖。"""
        if not os.path.exists(config_path):
            print(f"配置文件 {config_path} 不存在")
            self.config = {}
            return self.config

        base_config = self._read_json(config_path)
        resolved_settings_path = self.resolve_settings_path(config_path, settings_path)
        save_settings_path = self._normalize_settings_path(settings_path, config_path) if settings_path else self.default_settings_path(config_path)
        if resolved_settings_path and os.path.exists(resolved_settings_path):
            settings_config = self._read_json(resolved_settings_path)
            self.config = self._deep_merge(base_config, settings_config)
        else:
            self.config = base_config

        self.config = self._apply_map_preset(self.config, config_path)

        self.config['_config_path'] = config_path
        self.config['_settings_path'] = save_settings_path
        return self.config
    
    def get(self, key_path, default=None):
        """获取配置值，支持嵌套路径"""
        keys = key_path.split('.')
        value = self.config
        
        for key in keys:
            if isinstance(value, dict) and key in value:
                value = value[key]
            else:
                return default
        
        return value
    
    def set(self, key_path, value):
        """设置配置值，支持嵌套路径"""
        keys = key_path.split('.')
        current = self.config
        
        for key in keys[:-1]:
            if key not in current:
                current[key] = {}
            current = current[key]
        
        current[keys[-1]] = value

    def save_config(self, config_path=None):
        """保存配置文件"""
        path = config_path or self.config.get('_config_path', 'config.json')
        self._atomic_write_json(path, self.config)

    def build_local_settings_payload(self, config):
        """提取需要持久化到本地 setting 的运行时配置。"""
        map_config = config.get('map', {})
        preset_name = map_config.get('preset')
        map_payload = {}
        if preset_name:
            map_payload['preset'] = preset_name
        else:
            map_payload['coordinate_space'] = map_config.get('coordinate_space', 'world')
            map_payload['facilities'] = deepcopy(map_config.get('facilities', []))
            map_payload['terrain_grid'] = deepcopy(map_config.get('terrain_grid', {}))
            map_payload['function_grid'] = deepcopy(map_config.get('function_grid', {}))
            map_payload['runtime_grid'] = deepcopy(map_config.get('runtime_grid', {}))
        return {
            'simulator': deepcopy(config.get('simulator', {})),
            'ai': deepcopy(config.get('ai', {})),
            'map': map_payload,
            'physics': deepcopy(config.get('physics', {})),
            'entities': deepcopy(config.get('entities', {})),
            'rules': deepcopy(config.get('rules', {})),
        }

    def build_map_preset_payload(self, config, preset_name=None):
        map_config = config.get('map', {})
        payload = {
            'version': 1,
            'name': preset_name or map_config.get('preset') or 'unnamed',
            'saved_at': datetime.now().isoformat(timespec='seconds'),
            'map': {
                'map_type': map_config.get('map_type', 'terrain_surface_map'),
                'image_path': map_config.get('image_path'),
                'origin_x': map_config.get('origin_x', 0),
                'origin_y': map_config.get('origin_y', 0),
                'unit': map_config.get('unit', 'px'),
                'width': map_config.get('width'),
                'height': map_config.get('height'),
                'source_width': map_config.get('source_width', map_config.get('width')),
                'source_height': map_config.get('source_height', map_config.get('height')),
                'strict_scale': bool(map_config.get('strict_scale', False)),
                'coordinate_space': map_config.get('coordinate_space', 'world'),
                'coordinate_system': {
                    'coordinate_space': map_config.get('coordinate_space', 'world'),
                    'unit': map_config.get('unit', 'px'),
                    'origin_x': map_config.get('origin_x', 0),
                    'origin_y': map_config.get('origin_y', 0),
                    'field_length_m': map_config.get('field_length_m'),
                    'field_width_m': map_config.get('field_width_m'),
                },
                'field_length_m': map_config.get('field_length_m'),
                'field_width_m': map_config.get('field_width_m'),
                'facilities': deepcopy(map_config.get('facilities', [])),
                'terrain_grid': deepcopy(map_config.get('terrain_grid', {})),
                'function_grid': deepcopy(map_config.get('function_grid', {})),
                'terrain_surface': deepcopy(map_config.get('terrain_surface', {})),
                'runtime_grid': deepcopy(map_config.get('runtime_grid', {})),
            },
        }
        return payload

    def save_map_preset(self, preset_name, config=None, config_path=None, map_manager=None):
        name = str(preset_name or '').strip()
        if not name:
            raise ValueError('preset_name is required')
        preset_path = self._map_folder_preset_path(name, config_path)
        directory = os.path.dirname(preset_path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        source_config = deepcopy(config or self.config)
        if map_manager is not None:
            source_config.setdefault('map', {})['facilities'] = map_manager.export_facilities_config()
        payload = self.build_map_preset_payload(source_config, preset_name=name)
        if map_manager is not None:
            runtime_grid = map_manager.persist_runtime_grid_bundle(name, preset_path=preset_path)
            payload.setdefault('map', {})['runtime_grid'] = runtime_grid
            payload.setdefault('map', {})['terrain_surface'] = map_manager.export_terrain_surface_config(name)
        self._atomic_write_json(preset_path, payload)
        return preset_path

    def save_behavior_preset(self, preset_name, payload, config_path=None):
        name = str(preset_name or '').strip()
        if not name:
            raise ValueError('preset_name is required')
        preset_path = self._resolve_behavior_preset_path(name, config_path)
        if not preset_path:
            raise ValueError('unable to resolve behavior preset path')
        directory = os.path.dirname(preset_path)
        if directory:
            os.makedirs(directory, exist_ok=True)
        self._atomic_write_json(preset_path, payload)
        return preset_path

    def save_settings(self, settings_path=None, payload=None):
        """保存本地 setting 文件。"""
        path = settings_path or self.config.get('_settings_path') or self.default_settings_path(self.config.get('_config_path'))
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        content = payload if payload is not None else self.build_local_settings_payload(self.config)
        self._atomic_write_json(path, content)
