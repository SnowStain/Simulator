#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import importlib
import os
import sys
from dataclasses import dataclass
from pathlib import Path


_DEFAULT_NATIVE_MODULE = 'rm26_native'
DEFAULT_RENDERER_FEATURE_LEVEL = 4
DEFAULT_PHYSICS_FEATURE_LEVEL = 4
_STATUS_CACHE = {}
_MODULE_CACHE = {}
_DLL_DIR_HANDLES = []


def _native_module_search_paths():
    package_dir = Path(__file__).resolve().parent
    py_root = package_dir.parent
    project_root = py_root.parent
    return [
        package_dir / 'native_bin',
        project_root / 'build' / 'native' / 'Release',
        project_root / 'build' / 'native' / 'RelWithDebInfo',
        project_root / 'build' / 'native' / 'Debug',
    ]


def _ensure_native_module_path():
    for candidate in _native_module_search_paths():
        candidate_str = str(candidate)
        if not candidate.is_dir():
            continue
        if candidate_str not in sys.path:
            sys.path.insert(0, candidate_str)
        if os.name == 'nt' and hasattr(os, 'add_dll_directory'):
            try:
                _DLL_DIR_HANDLES.append(os.add_dll_directory(candidate_str))
            except OSError:
                pass


@dataclass(frozen=True)
class NativeRuntimeStatus:
    available: bool
    module_name: str
    reason: str = ''
    build_info: dict | None = None
    renderer_feature_level: int = 0
    physics_feature_level: int = 0

    def renderer_ready(self, minimum_feature_level=DEFAULT_RENDERER_FEATURE_LEVEL):
        return bool(self.available and self.renderer_feature_level >= int(minimum_feature_level))

    def physics_ready(self, minimum_feature_level=DEFAULT_PHYSICS_FEATURE_LEVEL):
        return bool(self.available and self.physics_feature_level >= int(minimum_feature_level))


def _native_backend_config(config):
    if isinstance(config, dict):
        return config.get('native_backend', {}) or {}
    return {}


def native_module_name(config=None):
    native_cfg = _native_backend_config(config)
    return str(native_cfg.get('module_name') or _DEFAULT_NATIVE_MODULE)


def minimum_renderer_feature_level(config=None):
    native_cfg = _native_backend_config(config)
    return max(1, int(native_cfg.get('renderer_required_feature_level', DEFAULT_RENDERER_FEATURE_LEVEL) or DEFAULT_RENDERER_FEATURE_LEVEL))


def minimum_physics_feature_level(config=None):
    native_cfg = _native_backend_config(config)
    return max(1, int(native_cfg.get('physics_required_feature_level', DEFAULT_PHYSICS_FEATURE_LEVEL) or DEFAULT_PHYSICS_FEATURE_LEVEL))


def _normalize_build_info(module_name, build_info):
    payload = dict(build_info or {})
    payload.setdefault('module_name', module_name)
    payload.setdefault('renderer_backend', 'unknown')
    payload.setdefault('physics_backend', 'unknown')
    payload.setdefault('renderer_feature_level', 0)
    payload.setdefault('physics_feature_level', 0)
    return payload


def get_native_runtime_status(config=None, force_refresh=False):
    module_name = native_module_name(config)
    cache_key = (module_name,)
    if not force_refresh and cache_key in _STATUS_CACHE:
        return _STATUS_CACHE[cache_key]

    try:
        _ensure_native_module_path()
        module = importlib.import_module(module_name)
        _MODULE_CACHE[module_name] = module
        build_info_getter = getattr(module, 'build_info', None)
        build_info = _normalize_build_info(module_name, build_info_getter() if callable(build_info_getter) else {})
        status = NativeRuntimeStatus(
            available=True,
            module_name=module_name,
            reason='',
            build_info=build_info,
            renderer_feature_level=int(build_info.get('renderer_feature_level', 0) or 0),
            physics_feature_level=int(build_info.get('physics_feature_level', 0) or 0),
        )
    except Exception as exc:
        status = NativeRuntimeStatus(
            available=False,
            module_name=module_name,
            reason=str(exc),
            build_info=None,
            renderer_feature_level=0,
            physics_feature_level=0,
        )

    _STATUS_CACHE[cache_key] = status
    return status


def _loaded_native_module(config=None):
    module_name = native_module_name(config)
    module = _MODULE_CACHE.get(module_name)
    if module is None:
        _ensure_native_module_path()
        module = importlib.import_module(module_name)
        _MODULE_CACHE[module_name] = module
    return module


def create_native_renderer_bridge(config=None):
    status = get_native_runtime_status(config)
    if not status.renderer_ready(minimum_renderer_feature_level(config)):
        return None
    module = _loaded_native_module(config)
    bridge_type = getattr(module, 'NativeRendererBridge', None)
    if bridge_type is None:
        return None
    return bridge_type(config or {})


def create_native_physics_bridge(config=None):
    status = get_native_runtime_status(config)
    if not status.physics_ready(minimum_physics_feature_level(config)):
        return None
    module = _loaded_native_module(config)
    bridge_type = getattr(module, 'NativePhysicsBridge', None)
    if bridge_type is None:
        return None
    return bridge_type(config or {})


def describe_native_runtime(config=None):
    status = get_native_runtime_status(config)
    if not status.available:
        return '原生 C++ 模块未构建，当前回退 Python + ModernGL/PyBullet'
    build_info = status.build_info or {}
    return (
        f'原生模块 {build_info.get("module_name", status.module_name)} | '
        f'渲染 {build_info.get("renderer_backend", "unknown")} {status.renderer_feature_level}/{minimum_renderer_feature_level(config)} | '
        f'物理 {build_info.get("physics_backend", "unknown")} {status.physics_feature_level}/{minimum_physics_feature_level(config)}'
    )
