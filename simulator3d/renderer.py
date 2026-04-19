#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math

from entities.chassis_profiles import infantry_chassis_label
from pygame_compat import pygame
from rendering.renderer import Renderer
from rendering.terrain_scene_backends import _terrain_scene_base_cell_size
from simulator3d.config import _backend_for_sim3d_mode, _normalize_sim3d_renderer_mode
from simulator3d.native_bridge import describe_native_runtime


class ModernGLMatchRenderer(Renderer):
    def __init__(self, game_engine, config):
        super().__init__(game_engine, config)
        simulator_config = config.setdefault('simulator', {})
        self.sim3d_state = 'main_menu'
        self.sim3d_renderer_mode = _normalize_sim3d_renderer_mode(simulator_config.get('sim3d_renderer_backend') or simulator_config.get('terrain_scene_backend'))
        self.sim3d_map_preset = str(simulator_config.get('sim3d_map_preset') or config.get('map', {}).get('preset') or 'basicMap')
        self.sim3d_selected_team = str(simulator_config.get('sim3d_selected_team', 'red') or 'red')
        if self.sim3d_selected_team not in {'red', 'blue'}:
            self.sim3d_selected_team = 'red'
        self.sim3d_selected_entity_id = str(simulator_config.get('sim3d_selected_entity_id', 'red_robot_1') or 'red_robot_1')
        self.sim3d_ricochet_enabled = bool(simulator_config.get('player_projectile_ricochet_enabled', True))
        self.player_terrain_render_scale = float(max(0.50, min(0.82, simulator_config.get('player_terrain_render_scale', self.player_terrain_render_scale))))
        self.player_motion_terrain_render_scale = float(max(0.38, min(self.player_terrain_render_scale, simulator_config.get('player_motion_terrain_render_scale', self.player_motion_terrain_render_scale))))
        self._active_player_terrain_render_scale = self.player_terrain_render_scale
        self.native_runtime_label = describe_native_runtime(config)
        self.terrain_scene_backend_requested = _backend_for_sim3d_mode(self.sim3d_renderer_mode)
        pygame.display.set_caption('RM26 3D 对局模拟器')

    def _available_sim3d_map_presets(self, game_engine):
        config_manager = getattr(game_engine, 'config_manager', None)
        if config_manager is None:
            return [self.sim3d_map_preset]
        presets = config_manager.list_map_presets(getattr(game_engine, 'config_path', 'config.json'))
        return presets or [self.sim3d_map_preset]

    def _persist_sim3d_settings(self, game_engine):
        simulator_cfg = self.config.setdefault('simulator', {})
        simulator_cfg['sim3d_renderer_backend'] = self.sim3d_renderer_mode
        simulator_cfg['terrain_scene_backend'] = self.terrain_scene_backend_requested
        simulator_cfg['sim3d_map_preset'] = self.sim3d_map_preset
        if isinstance(getattr(game_engine, 'config', None), dict):
            game_engine.config.setdefault('simulator', {}).update({
                'sim3d_renderer_backend': self.sim3d_renderer_mode,
                'terrain_scene_backend': self.terrain_scene_backend_requested,
                'sim3d_map_preset': self.sim3d_map_preset,
            })
            game_engine.config.setdefault('map', {})['preset'] = self.sim3d_map_preset
        config_manager = getattr(game_engine, 'config_manager', None)
        settings_path = getattr(game_engine, 'settings_path', None)
        if config_manager is not None and settings_path:
            config_manager.config = game_engine.config
            payload = config_manager.build_local_settings_payload(game_engine.config)
            config_manager.save_settings(settings_path, payload=payload)

    def _set_sim3d_renderer_mode(self, game_engine, mode):
        normalized = _normalize_sim3d_renderer_mode(mode)
        if normalized == self.sim3d_renderer_mode:
            return
        self.sim3d_renderer_mode = normalized
        self.terrain_scene_backend_requested = _backend_for_sim3d_mode(normalized)
        self.terrain_scene_backend = None
        self.terrain_scene_backend_active_request = None
        self._invalidate_terrain_scene_cache()
        self._persist_sim3d_settings(game_engine)

    def _set_sim3d_map_preset(self, game_engine, preset_name):
        preset = str(preset_name or '').strip()
        if not preset or preset == self.sim3d_map_preset:
            return
        previous_entity_id = self.sim3d_selected_entity_id
        previous_team = self.sim3d_selected_team
        if not game_engine.reload_map_preset(preset):
            game_engine.add_log(f'切换地图失败: {preset}', 'system')
            return
        self.sim3d_map_preset = preset
        self.sim3d_selected_team = previous_team
        self.sim3d_selected_entity_id = previous_entity_id
        self._ensure_sim3d_selection(game_engine)
        self._invalidate_terrain_scene_cache()
        self._persist_sim3d_settings(game_engine)

    def _cycle_sim3d_map_preset(self, game_engine, direction):
        presets = self._available_sim3d_map_presets(game_engine)
        if not presets:
            return
        current = self.sim3d_map_preset if self.sim3d_map_preset in presets else presets[0]
        current_index = presets.index(current)
        next_index = (current_index + int(direction)) % len(presets)
        self._set_sim3d_map_preset(game_engine, presets[next_index])

    def _effective_terrain_scene_max_cells(self):
        if getattr(self, 'terrain_scene_camera_override', None) is not None or bool(getattr(self, '_building_player_camera_override', False)):
            return max(7000, min(int(self.terrain_scene_max_cells), int(getattr(self, '_active_player_scene_max_cells', self.player_terrain_scene_max_cells))))
        return super()._effective_terrain_scene_max_cells()

    def _player_terrain_surface_cache_key(self, game_engine, rect):
        return super()._player_terrain_surface_cache_key(game_engine, rect)

    def _render_application_frame(self, game_engine):
        if self.sim3d_state != 'in_match':
            self._sync_player_mouse_capture(False)
            self._render_sim3d_program(game_engine)
            return True
        self.render_player_simulator(game_engine, top_offset=0, hud_top=0, draw_match_hud=True)
        return True

    def _handle_application_event(self, event, game_engine):
        if self.sim3d_state != 'in_match':
            return self._handle_sim3d_program_event(event, game_engine)
        return False

    def _sim3d_control_candidate_ids(self):
        return ('red_robot_1', 'red_robot_2', 'red_robot_3', 'red_robot_4', 'blue_robot_1', 'blue_robot_2', 'blue_robot_3', 'blue_robot_4')

    def _sim3d_control_candidates(self, game_engine, team=None):
        entity_map = {entity.id: entity for entity in getattr(game_engine.entity_manager, 'entities', [])}
        candidates = []
        for entity_id in self._sim3d_control_candidate_ids():
            entity = entity_map.get(entity_id)
            if entity is None or not entity.is_alive():
                continue
            if team is not None and entity.team != team:
                continue
            candidates.append(entity)
        return candidates

    def _ensure_sim3d_selection(self, game_engine):
        candidates = self._sim3d_control_candidates(game_engine, self.sim3d_selected_team)
        if candidates and self.sim3d_selected_entity_id not in {entity.id for entity in candidates}:
            self.sim3d_selected_entity_id = candidates[0].id
        elif not candidates:
            all_candidates = self._sim3d_control_candidates(game_engine)
            self.sim3d_selected_entity_id = all_candidates[0].id if all_candidates else None

    def _bind_selected_entity(self, game_engine):
        if self.sim3d_selected_entity_id and game_engine.set_player_controlled_entity(self.sim3d_selected_entity_id):
            self.selected_hud_entity_id = self.sim3d_selected_entity_id
            self.player_purchase_menu_open = False
            self.player_purchase_amount = 50
            return True
        return False

    def _start_sim3d_match(self, game_engine):
        self._ensure_sim3d_selection(game_engine)
        game_engine.config.setdefault('simulator', {})['player_projectile_ricochet_enabled'] = self.sim3d_ricochet_enabled
        game_engine.start_new_match()
        self._ensure_sim3d_selection(game_engine)
        if self._bind_selected_entity(game_engine):
            self.player_settings_menu_open = False
            self.pre_match_config_menu_open = bool(getattr(game_engine, 'pre_match_setup_required', False))
            self.sim3d_state = 'in_match'
            return
        game_engine.add_log('所选机器人当前不可接管', 'system')

    def _render_sim3d_program(self, game_engine):
        self._ensure_sim3d_selection(game_engine)
        background = pygame.Surface((self.window_width, self.window_height))
        background.fill((12, 16, 22))
        pygame.draw.circle(background, (26, 36, 48), (self.window_width // 2, self.window_height // 3), int(self.window_width * 0.33))
        pygame.draw.circle(background, (18, 24, 32), (self.window_width // 4, self.window_height // 2), int(self.window_width * 0.18))
        pygame.draw.circle(background, (18, 28, 38), (self.window_width * 3 // 4, self.window_height // 2), int(self.window_width * 0.22))
        self.screen.blit(background, (0, 0))
        if self.sim3d_state == 'main_menu':
            self._render_sim3d_main_menu(game_engine)
        else:
            self._render_sim3d_lobby(game_engine)

    def _render_sim3d_main_menu(self, game_engine):
        backend = self._get_terrain_scene_backend()
        backend_text = getattr(backend, 'status_label', getattr(backend, 'name', 'unknown'))
        title = self.hud_big_font.render('3D 对局模拟器', True, self.colors['white'])
        subtitle = self.small_font.render('3D 程序入口', True, (198, 204, 212))
        backend_label = self.tiny_font.render(f'场景后端: {backend_text}', True, (168, 176, 188))
        native_label = self.tiny_font.render(self.native_runtime_label, True, (144, 154, 168))
        self.screen.blit(title, title.get_rect(center=(self.window_width // 2, 154)))
        self.screen.blit(subtitle, subtitle.get_rect(center=(self.window_width // 2, 190)))
        self.screen.blit(backend_label, backend_label.get_rect(center=(self.window_width // 2, 218)))
        self.screen.blit(native_label, native_label.get_rect(center=(self.window_width // 2, 238)))

        panel_rect = pygame.Rect(0, 0, 560, 404)
        panel_rect.center = (self.window_width // 2, self.window_height // 2 + 82)
        pygame.draw.rect(self.screen, (28, 34, 42), panel_rect, border_radius=24)
        pygame.draw.rect(self.screen, self.colors['panel_border'], panel_rect, 1, border_radius=24)

        lines = [
            '开始前先选择地图和 3D 后端，再进入大厅选择主控机器人。'
        ]
        draw_y = panel_rect.y + 48
        for line in lines:
            rendered = self.small_font.render(line, True, (220, 225, 232))
            self.screen.blit(rendered, rendered.get_rect(center=(panel_rect.centerx, draw_y)))
            draw_y += 30

        backend_title = self.tiny_font.render('渲染后端', True, (190, 198, 208))
        self.screen.blit(backend_title, (panel_rect.x + 58, panel_rect.y + 106))
        button_width = 126
        button_gap = 12
        opengl_rect = pygame.Rect(panel_rect.x + 58, panel_rect.y + 130, button_width, 36)
        moderngl_rect = pygame.Rect(opengl_rect.right + button_gap, panel_rect.y + 130, button_width, 36)
        native_rect = pygame.Rect(moderngl_rect.right + button_gap, panel_rect.y + 130, button_width, 36)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'] if self.sim3d_renderer_mode == 'opengl' else self.colors['toolbar_button'], opengl_rect, border_radius=11)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'] if self.sim3d_renderer_mode == 'moderngl' else self.colors['toolbar_button'], moderngl_rect, border_radius=11)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'] if self.sim3d_renderer_mode == 'native_cpp' else self.colors['toolbar_button'], native_rect, border_radius=11)
        self.screen.blit(self.small_font.render('OpenGL', True, self.colors['white']), self.small_font.render('OpenGL', True, self.colors['white']).get_rect(center=opengl_rect.center))
        self.screen.blit(self.small_font.render('ModernGL', True, self.colors['white']), self.small_font.render('ModernGL', True, self.colors['white']).get_rect(center=moderngl_rect.center))
        self.screen.blit(self.small_font.render('Native C++', True, self.colors['white']), self.small_font.render('Native C++', True, self.colors['white']).get_rect(center=native_rect.center))
        self.panel_actions.append((opengl_rect, 'sim3d_backend:opengl'))
        self.panel_actions.append((moderngl_rect, 'sim3d_backend:moderngl'))
        self.panel_actions.append((native_rect, 'sim3d_backend:native_cpp'))

        presets = self._available_sim3d_map_presets(game_engine)
        map_title = self.tiny_font.render('地图预设', True, (190, 198, 208))
        self.screen.blit(map_title, (panel_rect.x + 58, panel_rect.y + 194))
        map_prev_rect = pygame.Rect(panel_rect.x + 58, panel_rect.y + 218, 44, 36)
        map_label_rect = pygame.Rect(panel_rect.x + 112, panel_rect.y + 218, panel_rect.width - 224, 36)
        map_next_rect = pygame.Rect(panel_rect.right - 102, panel_rect.y + 218, 44, 36)
        pygame.draw.rect(self.screen, self.colors['toolbar_button'], map_prev_rect, border_radius=10)
        pygame.draw.rect(self.screen, (38, 44, 54), map_label_rect, border_radius=10)
        pygame.draw.rect(self.screen, self.colors['toolbar_button'], map_next_rect, border_radius=10)
        pygame.draw.rect(self.screen, self.colors['panel_border'], map_label_rect, 1, border_radius=10)
        self.screen.blit(self.small_font.render('<', True, self.colors['white']), self.small_font.render('<', True, self.colors['white']).get_rect(center=map_prev_rect.center))
        self.screen.blit(self.small_font.render('>', True, self.colors['white']), self.small_font.render('>', True, self.colors['white']).get_rect(center=map_next_rect.center))
        map_text = self.small_font.render(self.sim3d_map_preset, True, self.colors['white'])
        self.screen.blit(map_text, map_text.get_rect(center=map_label_rect.center))
        self.panel_actions.append((map_prev_rect, 'sim3d_map:-1'))
        self.panel_actions.append((map_next_rect, 'sim3d_map:1'))
        map_hint = self.tiny_font.render(f'可用地图: {len(presets)}', True, (146, 156, 170))
        self.screen.blit(map_hint, (panel_rect.x + 58, panel_rect.y + 262))

        start_rect = pygame.Rect(panel_rect.x + 60, panel_rect.bottom - 132, panel_rect.width - 120, 42)
        settings_rect = pygame.Rect(panel_rect.x + 60, panel_rect.bottom - 82, panel_rect.width - 120, 34)
        exit_rect = pygame.Rect(panel_rect.x + 60, panel_rect.bottom - 40, panel_rect.width - 120, 34)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'], start_rect, border_radius=12)
        pygame.draw.rect(self.screen, (86, 96, 108), settings_rect, border_radius=10)
        pygame.draw.rect(self.screen, (64, 72, 84), exit_rect, border_radius=10)
        self.screen.blit(self.small_font.render('进入大厅', True, self.colors['white']), self.small_font.render('进入大厅', True, self.colors['white']).get_rect(center=start_rect.center))
        self.screen.blit(self.small_font.render('设置', True, self.colors['white']), self.small_font.render('设置', True, self.colors['white']).get_rect(center=settings_rect.center))
        self.screen.blit(self.small_font.render('退出程序', True, self.colors['white']), self.small_font.render('退出程序', True, self.colors['white']).get_rect(center=exit_rect.center))
        self.panel_actions.append((start_rect, 'sim3d_open_lobby'))
        self.panel_actions.append((settings_rect, 'toggle_player_settings'))
        self.panel_actions.append((exit_rect, 'sim3d_exit'))
        if self.player_settings_menu_open:
            self._render_player_settings_menu(game_engine, self.screen.get_rect())

    def _render_sim3d_lobby(self, game_engine):
        title = self.hud_mid_font.render('赛前大厅', True, self.colors['white'])
        self.screen.blit(title, title.get_rect(center=(self.window_width // 2, 70)))

        panel_rect = pygame.Rect(0, 0, 760, 420)
        panel_rect.center = (self.window_width // 2, self.window_height // 2 + 20)
        pygame.draw.rect(self.screen, (28, 34, 42), panel_rect, border_radius=24)
        pygame.draw.rect(self.screen, self.colors['panel_border'], panel_rect, 1, border_radius=24)

        team_y = panel_rect.y + 34
        for index, team in enumerate(('red', 'blue')):
            rect = pygame.Rect(panel_rect.x + 40 + index * 124, team_y, 104, 34)
            active = self.sim3d_selected_team == team
            color = self.colors['red'] if team == 'red' and active else self.colors['blue'] if team == 'blue' and active else (68, 76, 88)
            pygame.draw.rect(self.screen, color, rect, border_radius=10)
            label = '红方' if team == 'red' else '蓝方'
            rendered = self.small_font.render(label, True, self.colors['white'])
            self.screen.blit(rendered, rendered.get_rect(center=rect.center))
            self.panel_actions.append((rect, f'sim3d_team:{team}'))

        card_y = panel_rect.y + 94
        candidates = self._sim3d_control_candidates(game_engine, self.sim3d_selected_team)
        for index, entity in enumerate(candidates):
            card_rect = pygame.Rect(panel_rect.x + 42 + (index % 3) * 220, card_y + (index // 3) * 108, 190, 82)
            active = entity.id == self.sim3d_selected_entity_id
            pygame.draw.rect(self.screen, (42, 48, 58), card_rect, border_radius=14)
            pygame.draw.rect(self.screen, self.colors['yellow'] if active else self.colors['panel_border'], card_rect, 2 if active else 1, border_radius=14)
            title = self.small_font.render(entity.display_name, True, self.colors['white'])
            subtype_text = ''
            if getattr(entity, 'robot_type', '') == '步兵':
                subtype_text = f' | {infantry_chassis_label(getattr(entity, "chassis_subtype", ""))}'
            subtitle = self.tiny_font.render(f'{"红方" if entity.team == "red" else "蓝方"} {getattr(entity, "robot_type", "")}{subtype_text}', True, (198, 204, 212))
            self.screen.blit(title, (card_rect.x + 14, card_rect.y + 14))
            self.screen.blit(subtitle, (card_rect.x + 14, card_rect.y + 40))
            self.panel_actions.append((card_rect, f'sim3d_pick:{entity.id}'))

        ricochet_rect = pygame.Rect(panel_rect.x + 42, panel_rect.bottom - 100, 260, 42)
        settings_rect = pygame.Rect(panel_rect.x + 42, panel_rect.bottom - 52, 136, 34)
        back_rect = pygame.Rect(panel_rect.right - 348, panel_rect.bottom - 52, 136, 34)
        confirm_rect = pygame.Rect(panel_rect.right - 194, panel_rect.bottom - 58, 152, 42)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'] if self.sim3d_ricochet_enabled else self.colors['toolbar_button'], ricochet_rect, border_radius=12)
        pygame.draw.rect(self.screen, (70, 76, 88), settings_rect, border_radius=10)
        pygame.draw.rect(self.screen, (70, 76, 88), back_rect, border_radius=10)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'], confirm_rect, border_radius=12)
        ricochet_label = '弹丸碰障碍反弹: 开' if self.sim3d_ricochet_enabled else '弹丸碰障碍反弹: 关'
        self.screen.blit(self.small_font.render(ricochet_label, True, self.colors['white']), self.small_font.render(ricochet_label, True, self.colors['white']).get_rect(center=ricochet_rect.center))
        self.screen.blit(self.small_font.render('设置', True, self.colors['white']), self.small_font.render('设置', True, self.colors['white']).get_rect(center=settings_rect.center))
        self.screen.blit(self.small_font.render('返回', True, self.colors['white']), self.small_font.render('返回', True, self.colors['white']).get_rect(center=back_rect.center))
        self.screen.blit(self.small_font.render('确认开始', True, self.colors['white']), self.small_font.render('确认开始', True, self.colors['white']).get_rect(center=confirm_rect.center))
        self.panel_actions.append((ricochet_rect, 'sim3d_toggle_ricochet'))
        self.panel_actions.append((settings_rect, 'toggle_player_settings'))
        self.panel_actions.append((back_rect, 'sim3d_back_main'))
        self.panel_actions.append((confirm_rect, 'sim3d_confirm_start'))
        if self.player_settings_menu_open:
            self._render_player_settings_menu(game_engine, self.screen.get_rect())

    def _handle_sim3d_program_event(self, event, game_engine):
        if event.type == pygame.KEYDOWN:
            if event.key == pygame.K_ESCAPE:
                if self.sim3d_state == 'lobby':
                    self.sim3d_state = 'main_menu'
                else:
                    game_engine.running = False
                return True
            if event.key in {pygame.K_RETURN, pygame.K_KP_ENTER} and self.sim3d_state == 'main_menu':
                self.sim3d_state = 'lobby'
                return True
        if event.type == pygame.MOUSEBUTTONDOWN and event.button == 1:
            action = self._resolve_click_action(event.pos)
            if action:
                self._execute_action(game_engine, action)
            return True
        return False

    def _execute_action(self, game_engine, action):
        if action == 'sim3d_open_lobby':
            self.sim3d_state = 'lobby'
            return
        if action == 'sim3d_exit':
            game_engine.running = False
            return
        if action.startswith('sim3d_backend:'):
            self._set_sim3d_renderer_mode(game_engine, action.split(':', 1)[1])
            return
        if action.startswith('sim3d_map:'):
            try:
                direction = int(action.split(':', 1)[1])
            except ValueError:
                return
            self._cycle_sim3d_map_preset(game_engine, direction)
            return
        if action == 'sim3d_toggle_ricochet':
            self.sim3d_ricochet_enabled = not self.sim3d_ricochet_enabled
            game_engine.config.setdefault('simulator', {})['player_projectile_ricochet_enabled'] = self.sim3d_ricochet_enabled
            return
        if action == 'sim3d_back_main':
            self.player_settings_menu_open = False
            self.sim3d_state = 'main_menu'
            return
        if action == 'sim3d_return_lobby':
            self.player_settings_menu_open = False
            self.player_purchase_menu_open = False
            self.pre_match_config_menu_open = False
            self._sync_player_mouse_capture(False)
            game_engine.clear_player_controlled_entity()
            game_engine.paused = True
            game_engine.pre_match_setup_required = False
            game_engine.pre_match_countdown_remaining = 0.0
            self.sim3d_state = 'lobby'
            return
        if action == 'sim3d_confirm_start':
            self._start_sim3d_match(game_engine)
            return
        if action.startswith('sim3d_team:'):
            self.sim3d_selected_team = action.split(':', 1)[1]
            self._ensure_sim3d_selection(game_engine)
            return
        if action.startswith('sim3d_pick:'):
            self.sim3d_selected_entity_id = action.split(':', 1)[1]
            if self.sim3d_selected_entity_id.startswith('red_'):
                self.sim3d_selected_team = 'red'
            elif self.sim3d_selected_entity_id.startswith('blue_'):
                self.sim3d_selected_team = 'blue'
            return
        if action == 'start_match':
            self._start_sim3d_match(game_engine)
            return
        if action == 'toggle_player_view':
            if not self._bind_selected_entity(game_engine):
                game_engine.add_log('当前没有可接管的机器人', 'system')
            return
        super()._execute_action(game_engine, action)