#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from pygame_compat import pygame
import os
import sys

PROJECT_ROOT = os.path.dirname(os.path.abspath(__file__))
CORE_DIR = os.path.join(PROJECT_ROOT, 'core')


def _add_to_sys_path(path):
    if os.path.isdir(path) and path not in sys.path:
        sys.path.insert(0, path)


def _add_package_parent(package_name):
    for dirpath, dirnames, _ in os.walk(PROJECT_ROOT):
        if package_name in dirnames:
            _add_to_sys_path(dirpath)
            return


_add_to_sys_path(PROJECT_ROOT)
_add_to_sys_path(CORE_DIR)
_add_package_parent('state_machine')

from core.game_engine import GameEngine
from core.config_manager import ConfigManager
from rendering.renderer import Renderer


def main():
    config_manager = ConfigManager()
    config = config_manager.load_config('config.json')
    config['_config_path'] = 'config.json'
    config.setdefault('simulator', {})['standalone_3d_program'] = True
    config['simulator'].setdefault('terrain_scene_backend', 'pyglet_moderngl')
    config['simulator'].setdefault('player_projectile_ricochet_enabled', True)

    game_engine = GameEngine(config, config_manager=config_manager, config_path='config.json')
    renderer = Renderer(game_engine, config)
    pygame.display.set_caption('RM26 3D 对局模拟器')
    game_engine.run(renderer)


if __name__ == '__main__':
    main()