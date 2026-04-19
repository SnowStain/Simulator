#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from core.config_manager import ConfigManager
from core.game_engine import GameEngine
from simulator3d.config import load_simulator3d_config
from simulator3d.native_bridge import get_native_runtime_status, minimum_renderer_feature_level
from simulator3d.renderer import ModernGLMatchRenderer


class ModernGLSimulator3DApp:
    def __init__(self, config_path='config.json', config_manager=None):
        self.config_path = config_path
        self.config_manager = config_manager or ConfigManager()

    def build_runtime(self):
        config = load_simulator3d_config(self.config_manager, self.config_path)
        native_cfg = config.get('native_backend', {})
        if bool(native_cfg.get('require_renderer', False)):
            status = get_native_runtime_status(config)
            required_level = minimum_renderer_feature_level(config)
            if not status.renderer_ready(required_level):
                reason = status.reason or f'renderer feature level {status.renderer_feature_level} < required {required_level}'
                raise RuntimeError(f'3D simulator requires the native renderer bridge: {reason}')
        game_engine = GameEngine(config, config_manager=self.config_manager, config_path=self.config_path)
        renderer = ModernGLMatchRenderer(game_engine, config)
        backend = renderer._get_terrain_scene_backend()
        if getattr(backend, 'name', 'software') == 'software':
            reason = getattr(backend, 'reason', None) or getattr(backend, 'status_label', '3D backend unavailable')
            raise RuntimeError(f'3D simulator requires a GPU terrain backend: {reason}')
        return game_engine, renderer

    def run(self):
        game_engine, renderer = self.build_runtime()
        game_engine.run(renderer)


def main():
    ModernGLSimulator3DApp().run()