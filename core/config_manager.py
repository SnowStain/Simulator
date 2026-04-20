#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import json
import os
from copy import deepcopy
from datetime import datetime

class ConfigManager:
    DEFAULT_COMMON_SETTINGS_NAME = 'CommonSetting.json'
    LEGACY_SETTINGS_NAME = 'settings.json'

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

    def _workspace_root(self, config_path=None):
        path = config_path or self.config.get('_config_path', 'config.json')
        return os.path.dirname(os.path.abspath(path))
    
    def _read_json(self, config_path):
        # Use utf-8-sig so files saved with BOM can still be parsed.
        with open(config_path, 'r', encoding='utf-8-sig') as f:
            return json.load(f)

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
            return preset_ref
        workspace_root = self._workspace_root(config_path)
        normalized_ref = preset_ref.replace('\\', '/').strip('/')
        if '/' in normalized_ref:
            if not normalized_ref.lower().endswith('.json'):
                normalized_ref = f'{normalized_ref}.json'
            return os.path.join(workspace_root, normalized_ref)
        folder_candidate = os.path.join(workspace_root, 'maps', preset_ref, 'map.json')
        if os.path.exists(folder_candidate):
            return folder_candidate
        if not preset_ref.lower().endswith('.json'):
            preset_ref = f'{preset_ref}.json'
        legacy_candidate = os.path.join(workspace_root, 'map_presets', preset_ref)
        if os.path.exists(legacy_candidate):
            return legacy_candidate
        return self._map_folder_preset_path(preset_ref, config_path)

    def list_map_presets(self, config_path=None):
        workspace_root = self._workspace_root(config_path)
        discovered = []
        seen = set()

        maps_dir = os.path.join(workspace_root, 'maps')
        if os.path.isdir(maps_dir):
            for entry in sorted(os.listdir(maps_dir)):
                candidate = os.path.join(maps_dir, entry, 'map.json')
                if not os.path.isfile(candidate):
                    continue
                if entry not in seen:
                    discovered.append(entry)
                    seen.add(entry)

        legacy_dir = os.path.join(workspace_root, 'map_presets')
        if os.path.isdir(legacy_dir):
            for entry in sorted(os.listdir(legacy_dir)):
                if not entry.lower().endswith('.json'):
                    continue
                name = os.path.splitext(entry)[0]
                if name not in seen:
                    discovered.append(name)
                    seen.add(name)
        return discovered

    def load_map_preset(self, preset_name, config_path=None):
        preset_path = self._resolve_map_preset_path(preset_name, config_path)
        if preset_path is None or not os.path.exists(preset_path):
            return None
        payload = self._read_json(preset_path)
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
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(self.config, f, ensure_ascii=False, indent=2)

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
                'field_length_m': map_config.get('field_length_m'),
                'field_width_m': map_config.get('field_width_m'),
                'facilities': deepcopy(map_config.get('facilities', [])),
                'terrain_grid': deepcopy(map_config.get('terrain_grid', {})),
                'function_grid': deepcopy(map_config.get('function_grid', {})),
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
        payload = self.build_map_preset_payload(config or self.config, preset_name=name)
        if map_manager is not None:
            runtime_grid = map_manager.persist_runtime_grid_bundle(name, preset_path=preset_path)
            payload.setdefault('map', {})['runtime_grid'] = runtime_grid
        with open(preset_path, 'w', encoding='utf-8') as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
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
        with open(preset_path, 'w', encoding='utf-8') as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        return preset_path

    def save_settings(self, settings_path=None, payload=None):
        """保存本地 setting 文件。"""
        path = settings_path or self.config.get('_settings_path') or self.default_settings_path(self.config.get('_config_path'))
        directory = os.path.dirname(path)
        if directory:
            os.makedirs(directory, exist_ok=True)

        content = payload if payload is not None else self.build_local_settings_payload(self.config)
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(content, f, ensure_ascii=False, indent=2)
