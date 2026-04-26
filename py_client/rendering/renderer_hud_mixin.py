#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from pygame_compat import pygame


class RendererHudMixin:
    BT_NODE_LABELS = {
        'return_to_supply_unlock': '回补解锁',
        'sentry_opening_exchange': '开局换弹',
        'opening_supply': '开局补给',
        'must_restock': '强制补给',
        'hero_opening_highground': '抢高地',
        'sentry_opening_highground': '抢台阶高地',
        'sentry_fly_slope': '飞坡突入',
        'emergency_retreat': '紧急撤退',
        'recover_after_respawn': '复活回补',
        'claim_buff': '抢地形增益',
        'activate_energy': '开能量机关',
        'cross_terrain': '翻越地形',
        'force_push_base': '强制推基地',
        'support_structures': '保基地前哨',
        'defend_outpost_from_hero': '防英雄吊射',
        'intercept_enemy_engineer': '拦敌方工程',
        'protect_hero': '保护英雄',
        'support_infantry_push': '配合步兵推进',
        'support_engineer': '护送工程',
        'engineer_mining_cycle': '取矿兑矿',
        'engineer_exchange': '回家兑矿',
        'engineer_mine': '前往采矿',
        'engineer_cycle': '取矿循环',
        'support_sentry_screen': '跟哨兵护送',
        'teamfight_push': '团战推进',
        'teamfight_cover': '团战掩护',
        'pursue_enemy': '追击目标',
        'sentry_engage': '哨兵交战',
        'hero_lob_shot': '英雄吊射',
        'hero_seek_cover': '英雄找掩护',
        'hero_lob_outpost': '吊射前哨站',
        'hero_lob_base': '吊射基地',
        'hero_lob_structure': '结构吊射',
        'swarm_attack': '发现即集火',
        'highground_assault': '抢敌方高地',
        'push_outpost': '推进前哨站',
        'push_base': '推进基地',
        'patrol_key_facilities': '巡关键设施',
    }

    def render_match_hud(self, game_engine, top_offset=None):
        hud_top = self.toolbar_height if top_offset is None else int(top_offset)
        hud_rect = pygame.Rect(0, hud_top, self.window_width, self.hud_height)
        pygame.draw.rect(self.screen, self.colors['hud_bg'], hud_rect)

        hud_data = game_engine.get_match_hud_data()
        center_x = self.window_width // 2
        center_panel = pygame.Rect(center_x - 102, hud_top + 8, 204, 92)
        pygame.draw.rect(self.screen, self.colors['hud_center'], center_panel, border_radius=16)

        round_text = self.tiny_font.render(hud_data['round_text'], True, (225, 230, 236))
        self.screen.blit(round_text, round_text.get_rect(center=(center_x, center_panel.y + 14)))

        scale_text = self.tiny_font.render(hud_data['scale_text'], True, (192, 199, 206))
        self.screen.blit(scale_text, scale_text.get_rect(center=(center_x, center_panel.bottom - 8)))

        remaining = max(0, int(hud_data['remaining_time']))
        minutes = remaining // 60
        seconds = remaining % 60
        timer_text = self.hud_big_font.render(f'{minutes}:{seconds:02d}', True, self.colors['white'])
        self.screen.blit(timer_text, timer_text.get_rect(center=(center_x, center_panel.y + 42)))

        self._render_team_hud('red', '红方', pygame.Rect(10, hud_top + 8, center_x - 120, 96), hud_data['red'])
        self._render_team_hud('blue', '蓝方', pygame.Rect(center_x + 110, hud_top + 8, self.window_width - center_x - 120, 96), hud_data['blue'])

    def _render_team_hud(self, team_key, team_label, rect, team_data):
        team_color = self.colors['red'] if team_key == 'red' else self.colors['blue']
        pygame.draw.rect(self.screen, self.colors['hud_panel'], rect, border_radius=14)

        banner_rect = pygame.Rect(rect.x + 8, rect.y + 8, rect.width - 16, 24)
        pygame.draw.rect(self.screen, team_color, banner_rect, border_radius=10)
        banner_text = self.hud_mid_font.render(f'{team_label}  金币 {team_data["gold"]}', True, self.colors['white'])
        self.screen.blit(banner_text, banner_text.get_rect(center=banner_rect.center))

        structure_text = self.tiny_font.render(
            f'基地 {team_data["base_hp"]}/{team_data["base_max_hp"]}   前哨站 {team_data["outpost_hp"]}/{team_data["outpost_max_hp"]}',
            True,
            (232, 236, 242),
        )
        self.screen.blit(structure_text, (rect.x + 12, rect.y + 40))

        unit_area_y = rect.y + 56
        unit_card_width = max(56, (rect.width - 20) // max(1, len(team_data['units'])))
        for index, unit in enumerate(team_data['units']):
            card_rect = pygame.Rect(rect.x + 8 + index * unit_card_width, unit_area_y, unit_card_width - 6, 36)
            border_color = team_color if unit['alive'] else self.colors['gray']
            is_selected = self.selected_hud_entity_id == unit.get('entity_id')
            pygame.draw.rect(self.screen, (28, 33, 41), card_rect, border_radius=8)
            pygame.draw.rect(self.screen, self.colors['yellow'] if is_selected else border_color, card_rect, 2 if is_selected else 1, border_radius=8)
            name_text = self.tiny_font.render(unit['label'], True, self.colors['white'])
            hp_text = self.tiny_font.render(f'{unit["hp"]}', True, self.colors['hud_gold'] if unit['alive'] else self.colors['gray'])
            lv_text = self.tiny_font.render(f'Lv{unit["level"]}', True, self.colors['white'])
            node_label = self._format_bt_node_label(unit.get('bt_node', ''))
            node_text = self.tiny_font.render(node_label, True, self.colors['white'])
            self.screen.blit(name_text, (card_rect.x + 6, card_rect.y + 1))
            self.screen.blit(hp_text, (card_rect.x + 6, card_rect.y + 12))
            self.screen.blit(lv_text, (card_rect.right - lv_text.get_width() - 6, card_rect.y + 12))
            self.screen.blit(node_text, (card_rect.x + 6, card_rect.y + 22))
            if unit.get('has_barrel'):
                pygame.draw.circle(self.screen, self.colors['green'], (card_rect.right - 12, card_rect.y + 9), 3)
            self.hud_actions.append((card_rect, f'hud_unit:{unit.get("entity_id", "")}'))

    def _format_bt_node_label(self, node_name):
        raw = str(node_name or '').strip()
        if not raw:
            return '待机'
        if raw.startswith('_action_'):
            raw = raw[len('_action_'):]
        raw = self.BT_NODE_LABELS.get(raw, raw.replace('_', ' '))
        if len(raw) > 10:
            raw = raw[:10]
        return raw

    def render_overlay_status(self, game_engine):
        if self.viewport is None:
            return
        lines = [
            f'时间: {int(game_engine.game_time)}s / {int(game_engine.game_duration)}s',
            f'比分: 红方 {game_engine.score["red"]} | 蓝方 {game_engine.score["blue"]}',
            f'模式: {self._mode_label(self.edit_mode)}',
            f'视场: {"显示" if self.show_aim_fov else "隐藏"}',
            f'比例尺: 1m≈{((game_engine.map_manager.pixels_per_meter_x() + game_engine.map_manager.pixels_per_meter_y()) / 2.0):.2f}单位',
            f'8m距离: ≈{game_engine.rules_engine.auto_aim_max_distance:.1f}单位',
        ]
        if self.mouse_world is not None:
            lines.append(f'坐标: ({self.mouse_world[0]}, {self.mouse_world[1]})')

        box_key = tuple(lines)
        if self.overlay_status_box_key != box_key:
            box = pygame.Surface((280, 24 + len(lines) * 18), pygame.SRCALPHA).convert_alpha()
            box.fill(self.colors['overlay_bg'])
            for index, line in enumerate(lines):
                text = self.tiny_font.render(line, True, self.colors['white'])
                box.blit(text, (10, 8 + index * 18))
            self.overlay_status_box_surface = box
            self.overlay_status_box_key = box_key
        box = self.overlay_status_box_surface
        left_bottom_pos = (self.viewport['map_x'] + 8, self.viewport['map_y'] + self.viewport['map_height'] - box.get_height() - 8)
        self.screen.blit(box, left_bottom_pos)

        logs = game_engine.logs[-6:]
        if not logs:
            return
        log_key = tuple((log['team'], log['message']) for log in logs)
        if self.overlay_status_log_key != log_key:
            log_surface = pygame.Surface((460, 20 + len(logs) * 18), pygame.SRCALPHA).convert_alpha()
            log_surface.fill(self.colors['overlay_log_bg'])
            for index, log in enumerate(logs):
                color = self.colors['white']
                if log['team'] == 'red':
                    color = self.colors['red']
                elif log['team'] == 'blue':
                    color = self.colors['blue']
                text = self.tiny_font.render(log['message'], True, color)
                log_surface.blit(text, (10, 6 + index * 18))
            self.overlay_status_log_surface = log_surface
            self.overlay_status_log_key = log_key
        log_surface = self.overlay_status_log_surface
        self.screen.blit(log_surface, (self.viewport['map_x'] + self.viewport['map_width'] - log_surface.get_width() - 8, self.viewport['map_y'] + self.viewport['map_height'] - log_surface.get_height() - 8))