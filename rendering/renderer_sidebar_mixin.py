#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math

from pygame_compat import pygame


class RendererSidebarMixin:
    def render_sidebar(self, game_engine):
        if self.viewport is None:
            return

        panel_rect = pygame.Rect(
            self.viewport['sidebar_x'],
            self.toolbar_height + self.hud_height,
            self.panel_width,
            self.window_height - self.toolbar_height - self.hud_height,
        )
        pygame.draw.rect(self.screen, self.colors['panel'], panel_rect)
        pygame.draw.line(self.screen, self.colors['panel_border'], panel_rect.topleft, panel_rect.bottomleft, 1)
        title = self.font.render('对局控制' if self.edit_mode == 'none' else self._mode_label(self.edit_mode), True, self.colors['panel_text'])
        self.screen.blit(title, (panel_rect.x + 16, panel_rect.y + 16))

        if self.edit_mode == 'none':
            self.render_match_control_panel(game_engine, panel_rect)
        elif self.edit_mode == 'terrain':
            self.render_terrain_editor_panel(game_engine, panel_rect)
        elif self.edit_mode == 'entity':
            self.render_entity_panel(game_engine, panel_rect)
        elif self.edit_mode == 'rules':
            self.render_rules_panel(game_engine, panel_rect)

        if self.edit_mode == 'none' and game_engine.is_single_unit_test_mode():
            decision_rect = pygame.Rect(
                panel_rect.right,
                panel_rect.y,
                self.decision_panel_width,
                panel_rect.height,
            )
            pygame.draw.rect(self.screen, self.colors['panel'], decision_rect)
            pygame.draw.line(self.screen, self.colors['panel_border'], decision_rect.topleft, decision_rect.bottomleft, 1)
            decision_title = self.font.render('决策可视化', True, self.colors['panel_text'])
            self.screen.blit(decision_title, (decision_rect.x + 16, decision_rect.y + 16))
            self.render_single_unit_decision_panel(game_engine, decision_rect)

    def render_match_control_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        mouse_pos = pygame.mouse.get_pos()
        hover_lines = None
        intro_lines = [
            '完整模式保留标准对局推进。',
            '单兵种测试下仅主控兵种允许运动。',
        ]
        for line in intro_lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        y += 8
        mode_title = self.small_font.render('对局模式', True, self.colors['panel_text'])
        self.screen.blit(mode_title, (panel_rect.x + 16, y))
        y += 28

        full_rect = pygame.Rect(panel_rect.x + 16, y, 92, 28)
        single_rect = pygame.Rect(panel_rect.x + 116, y, 110, 28)
        self._draw_mode_button(full_rect, '完整', getattr(game_engine, 'match_mode', 'full') == 'full')
        self._draw_mode_button(single_rect, '单兵种测试', getattr(game_engine, 'match_mode', 'full') == 'single_unit_test')
        self.panel_actions.append((full_rect, 'match_mode:full'))
        self.panel_actions.append((single_rect, 'match_mode:single_unit_test'))
        if full_rect.collidepoint(mouse_pos):
            hover_lines = ['完整游戏对局，完全由程序控制对局']
        elif single_rect.collidepoint(mouse_pos):
            hover_lines = ['对单个兵种的实战决策检视']
        y += 42

        if not game_engine.is_single_unit_test_mode():
            summary_lines = [
                '当前不注入人工待办决策。',
                '若要检查单兵种决策，请切换到单兵种测试。',
            ]
            for line in summary_lines:
                text = self.tiny_font.render(line, True, self.colors['panel_text'])
                self.screen.blit(text, (panel_rect.x + 16, y))
                y += 18
            self.single_unit_decision_list_rect = None
            if hover_lines:
                self._draw_text_tooltip(panel_rect, hover_lines, mouse_pos)
            return

        focus_team = getattr(game_engine, 'single_unit_test_team', 'red')
        focus_key = getattr(game_engine, 'single_unit_test_entity_key', 'robot_1')
        focus_entity = game_engine.get_single_unit_test_focus_entity()

        team_title = self.small_font.render('主控方', True, self.colors['panel_text'])
        self.screen.blit(team_title, (panel_rect.x + 16, y))
        y += 28
        red_rect = pygame.Rect(panel_rect.x + 16, y, 72, 26)
        blue_rect = pygame.Rect(panel_rect.x + 96, y, 72, 26)
        self._draw_mode_button(red_rect, '红方', focus_team == 'red')
        self._draw_mode_button(blue_rect, '蓝方', focus_team == 'blue')
        self.panel_actions.append((red_rect, 'test_focus_team:red'))
        self.panel_actions.append((blue_rect, 'test_focus_team:blue'))
        y += 38

        unit_title = self.small_font.render('主控兵种', True, self.colors['panel_text'])
        self.screen.blit(unit_title, (panel_rect.x + 16, y))
        y += 28
        unit_specs = [
            ('robot_1', '英雄'),
            ('robot_2', '工程'),
            ('robot_3', '步兵1'),
            ('robot_4', '步兵2'),
            ('robot_7', '哨兵'),
        ]
        button_width = 88
        for index, (entity_key, label) in enumerate(unit_specs):
            row = index // 3
            column = index % 3
            rect = pygame.Rect(panel_rect.x + 16 + column * (button_width + 8), y + row * 34, button_width, 26)
            self._draw_mode_button(rect, label, focus_key == entity_key)
            self.panel_actions.append((rect, f'test_focus_entity:{entity_key}'))
        y += 76

        status_lines = [
            '测试控制',
            '浏览模式下可拖拽所有战斗单位位置。',
            f'当前主控: {getattr(focus_entity, "id", "未找到")}',
            '当前决策与待办决策见右侧决策栏。',
        ]
        for index, line in enumerate(status_lines):
            font = self.small_font if index == 0 else self.tiny_font
            text = font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 22 if index == 0 else 18

        y += 6
        base_title = self.small_font.render('基地血量', True, self.colors['panel_text'])
        self.screen.blit(base_title, (panel_rect.x + 16, y))
        y += 28
        for team in ('red', 'blue'):
            base = game_engine.entity_manager.get_entity(f'{team}_base')
            row_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
            pygame.draw.rect(self.screen, self.colors['panel_row'], row_rect, border_radius=5)
            label = '红方基地' if team == 'red' else '蓝方基地'
            hp_text = f'{int(getattr(base, "health", 0.0))}/{int(getattr(base, "max_health", 0.0))}'
            self.screen.blit(self.tiny_font.render(label, True, self.colors['panel_text']), (row_rect.x + 8, row_rect.y + 7))
            self.screen.blit(self.tiny_font.render(hp_text, True, self.colors['panel_text']), (row_rect.x + 92, row_rect.y + 7))
            minus_rect = pygame.Rect(row_rect.right - 60, row_rect.y + 4, 24, 20)
            plus_rect = pygame.Rect(row_rect.right - 30, row_rect.y + 4, 24, 20)
            pygame.draw.rect(self.screen, self.colors['toolbar_button'], minus_rect, border_radius=4)
            pygame.draw.rect(self.screen, self.colors['toolbar_button'], plus_rect, border_radius=4)
            self.screen.blit(self.tiny_font.render('-', True, self.colors['white']), (minus_rect.x + 8, minus_rect.y + 2))
            self.screen.blit(self.tiny_font.render('+', True, self.colors['white']), (plus_rect.x + 7, plus_rect.y + 2))
            self.panel_actions.append((minus_rect, f'test_base_hp:{team}:-250'))
            self.panel_actions.append((plus_rect, f'test_base_hp:{team}:250'))
            y += 34

        self.single_unit_decision_list_rect = None

        if hover_lines:
            self._draw_text_tooltip(panel_rect, hover_lines, mouse_pos)

    def render_single_unit_decision_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        focus_entity = game_engine.get_single_unit_test_focus_entity()
        decision_top3 = [] if focus_entity is None else [item for item in getattr(focus_entity, 'ai_decision_top3', ()) if isinstance(item, dict)]
        next_specs = list(game_engine.get_single_unit_test_next_decision_specs())
        decision_summary = '待机' if focus_entity is None else str(getattr(focus_entity, 'ai_decision', '') or '待机')
        current_decision = '' if focus_entity is None else str(getattr(focus_entity, 'ai_decision_selected', '') or '')
        forced_decision = '' if focus_entity is None else str(getattr(focus_entity, 'test_forced_decision_id', '') or '')

        summary_title = self.small_font.render('当下决策', True, self.colors['panel_text'])
        self.screen.blit(summary_title, (panel_rect.x + 16, y))
        y += 30

        summary_lines = [
            f'主控实体: {getattr(focus_entity, "id", "未找到")}',
            f'当前分支: {current_decision or "无"}',
            f'待办分支: {forced_decision or "未设置"}',
            decision_summary,
        ]
        for index, line in enumerate(summary_lines):
            rendered = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(rendered, (panel_rect.x + 16, y))
            y += 18

        y += 8
        top3_title = self.small_font.render('决策 Top3', True, self.colors['panel_text'])
        self.screen.blit(top3_title, (panel_rect.x + 16, y))
        y += 28
        if decision_top3:
            for item in decision_top3[:3]:
                row_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 30)
                pygame.draw.rect(self.screen, self.colors['panel_row'], row_rect, border_radius=5)
                label = str(item.get('label') or item.get('id') or '未命名决策')
                weight = float(item.get('weight', 0.0))
                matched = bool(item.get('matched', False))
                suffix = ' 命中' if matched else ''
                text = self.tiny_font.render(f'{label} | {weight * 100:.0f}%{suffix}', True, self.colors['panel_text'])
                self.screen.blit(text, (row_rect.x + 8, row_rect.y + 7))
                y += 34
        else:
            self.screen.blit(self.tiny_font.render('当前无可展示候选决策', True, self.colors['panel_text']), (panel_rect.x + 16, y))
            y += 22

        y += 6
        next_title = self.small_font.render('后续候选', True, self.colors['panel_text'])
        self.screen.blit(next_title, (panel_rect.x + 16, y))
        y += 28
        if next_specs:
            for spec in next_specs[:3]:
                row_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
                pygame.draw.rect(self.screen, self.colors['panel_row'], row_rect, border_radius=5)
                text = self.tiny_font.render(str(spec.get('label', spec.get('id', ''))), True, self.colors['panel_text'])
                self.screen.blit(text, (row_rect.x + 8, row_rect.y + 7))
                y += 32
        else:
            self.screen.blit(self.tiny_font.render('当前无法推断下一步候选', True, self.colors['panel_text']), (panel_rect.x + 16, y))
            y += 22

        y += 6
        decision_title = self.small_font.render('主控待办决策', True, self.colors['panel_text'])
        self.screen.blit(decision_title, (panel_rect.x + 16, y))
        clear_rect = pygame.Rect(panel_rect.right - 96, y - 2, 80, 24)
        self._draw_mode_button(clear_rect, '清除待办', False)
        self.panel_actions.append((clear_rect, 'test_decision_clear'))
        y += 30

        decision_specs = list(game_engine.get_single_unit_test_decision_specs())
        available_height = max(60, panel_rect.bottom - y - 16)
        row_height = 30
        max_visible = max(1, available_height // row_height)
        max_scroll = max(0, len(decision_specs) - max_visible)
        self.single_unit_decision_scroll = max(0, min(max_scroll, self.single_unit_decision_scroll))
        visible_count = min(len(decision_specs), max_visible)
        self.single_unit_decision_list_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, max(row_height, visible_count * row_height))
        visible_specs = decision_specs[self.single_unit_decision_scroll:self.single_unit_decision_scroll + max_visible]

        for visible_index, spec in enumerate(visible_specs):
            row_y = y + visible_index * row_height
            rect = pygame.Rect(panel_rect.x + 16, row_y, panel_rect.width - 32, 26)
            is_forced = spec.get('id') == forced_decision
            is_running = spec.get('id') == current_decision
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if (is_forced or is_running) else self.colors['panel_row'], rect, border_radius=5)
            suffix = ' [待办]' if is_forced else (' [当前]' if is_running else '')
            label = self.tiny_font.render(f'{spec.get("label", spec.get("id", ""))}{suffix}', True, self.colors['panel_text'])
            self.screen.blit(label, (rect.x + 8, rect.y + 6))
            self.panel_actions.append((rect, f'test_decision:{spec.get("id", "")}'))

    def render_debug_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        intro_lines = [
            '右侧按钮用于快速禁用高耗时模块。',
            '控制器和状态机支持按兵种折叠调试。',
        ]
        for line in intro_lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18
        header = self.small_font.render('运行系统', True, self.colors['panel_text'])
        self.screen.blit(header, (panel_rect.x + 16, y + 4))
        y += 28
        y = self._render_debug_toggle_row(panel_rect, y, '实体更新', 'debug_toggle:entity_update', game_engine.feature_enabled('entity_update'))
        y = self._render_debug_foldout_header(panel_rect, y, '控制器', 'debug_toggle:controller', game_engine.feature_enabled('controller'), 'controller')
        if self.debug_panel_expanded.get('controller', False):
            y = self._render_controller_role_toggles(panel_rect, y, game_engine)
        y = self._render_debug_toggle_row(panel_rect, y, '物理', 'debug_toggle:physics', game_engine.feature_enabled('physics'))
        y = self._render_debug_toggle_row(panel_rect, y, '自瞄', 'debug_toggle:auto_aim', game_engine.feature_enabled('auto_aim'))
        y = self._render_debug_toggle_row(panel_rect, y, '规则', 'debug_toggle:rules', game_engine.feature_enabled('rules'))
        y = self._render_debug_foldout_header(panel_rect, y, '状态机', None, True, 'state_machine')
        if self.debug_panel_expanded.get('state_machine', False):
            y = self._render_state_machine_role_toggles(panel_rect, y, game_engine)

        header = self.small_font.render('显示调试', True, self.colors['panel_text'])
        self.screen.blit(header, (panel_rect.x + 16, y + 4))
        y += 28
        y = self._render_debug_toggle_row(panel_rect, y, '实体渲染', 'toggle_entities', self.show_entities)
        y = self._render_debug_toggle_row(panel_rect, y, '设施标注', 'toggle_facilities', self.show_facilities)
        y = self._render_debug_toggle_row(panel_rect, y, '自瞄视场', 'toggle_aim_fov', self.show_aim_fov)
        self._render_debug_toggle_row(panel_rect, y, '性能浮层', 'toggle_perf_overlay', bool(getattr(game_engine, 'show_perf_overlay', False)))

    def _render_debug_toggle_row(self, panel_rect, y, label, action, enabled):
        row_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
        pygame.draw.rect(self.screen, self.colors['panel_row'], row_rect, border_radius=6)
        pygame.draw.rect(self.screen, self.colors['panel_border'], row_rect, 1, border_radius=6)
        label_text = self.tiny_font.render(label, True, self.colors['panel_text'])
        self.screen.blit(label_text, (row_rect.x + 10, row_rect.y + 7))
        button_rect = pygame.Rect(row_rect.right - 66, row_rect.y + 3, 56, 22)
        pygame.draw.rect(self.screen, self.colors['green'] if enabled else self.colors['toolbar_button'], button_rect, border_radius=5)
        button_text = self.tiny_font.render('开' if enabled else '关', True, self.colors['white'])
        self.screen.blit(button_text, button_text.get_rect(center=button_rect.center))
        self.panel_actions.append((button_rect, action))
        return y + 34

    def _render_debug_foldout_header(self, panel_rect, y, label, toggle_action, enabled, section_id):
        row_rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
        pygame.draw.rect(self.screen, self.colors['panel_row'], row_rect, border_radius=6)
        pygame.draw.rect(self.screen, self.colors['panel_border'], row_rect, 1, border_radius=6)
        expanded = bool(self.debug_panel_expanded.get(section_id, False))
        arrow_text = self.tiny_font.render('▼' if expanded else '▶', True, self.colors['panel_text'])
        self.screen.blit(arrow_text, (row_rect.x + 8, row_rect.y + 6))
        label_text = self.tiny_font.render(label, True, self.colors['panel_text'])
        self.screen.blit(label_text, (row_rect.x + 24, row_rect.y + 7))
        fold_rect = pygame.Rect(row_rect.x, row_rect.y, row_rect.width - 74, row_rect.height)
        self.panel_actions.append((fold_rect, f'debug_fold:{section_id}'))
        if toggle_action is not None:
            button_rect = pygame.Rect(row_rect.right - 66, row_rect.y + 3, 56, 22)
            pygame.draw.rect(self.screen, self.colors['green'] if enabled else self.colors['toolbar_button'], button_rect, border_radius=5)
            button_text = self.tiny_font.render('开' if enabled else '关', True, self.colors['white'])
            self.screen.blit(button_text, button_text.get_rect(center=button_rect.center))
            self.panel_actions.append((button_rect, toggle_action))
        return y + 34

    def _render_controller_role_toggles(self, panel_rect, y, game_engine):
        role_specs = [('英雄', 'hero'), ('步兵', 'infantry'), ('哨兵', 'sentry'), ('工程', 'engineer')]
        button_specs = [('寻路', 'pathfinding'), ('规划', 'path_planning'), ('避障', 'avoidance')]
        for role_label, role_key in role_specs:
            row_rect = pygame.Rect(panel_rect.x + 24, y, panel_rect.width - 48, 28)
            pygame.draw.rect(self.screen, self.colors['panel_row_active'], row_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['panel_border'], row_rect, 1, border_radius=6)
            label_text = self.tiny_font.render(role_label, True, self.colors['panel_text'])
            self.screen.blit(label_text, (row_rect.x + 8, row_rect.y + 7))
            button_x = row_rect.x + 58
            for button_label, feature_name in button_specs:
                feature_id = f'controller.{role_key}.{feature_name}'
                enabled = game_engine.feature_enabled(feature_id)
                button_rect = pygame.Rect(button_x, row_rect.y + 3, 52, 22)
                pygame.draw.rect(self.screen, self.colors['green'] if enabled else self.colors['toolbar_button'], button_rect, border_radius=5)
                text = self.tiny_font.render(button_label, True, self.colors['white'])
                self.screen.blit(text, text.get_rect(center=button_rect.center))
                self.panel_actions.append((button_rect, f'debug_toggle:{feature_id}'))
                button_x += 56
            y += 32
        return y + 4

    def _render_state_machine_role_toggles(self, panel_rect, y, game_engine):
        for role_label, role_key in (('英雄', 'hero'), ('步兵', 'infantry'), ('哨兵', 'sentry'), ('工程', 'engineer')):
            feature_id = f'state_machine.{role_key}'
            row_rect = pygame.Rect(panel_rect.x + 24, y, panel_rect.width - 48, 28)
            pygame.draw.rect(self.screen, self.colors['panel_row_active'], row_rect, border_radius=6)
            pygame.draw.rect(self.screen, self.colors['panel_border'], row_rect, 1, border_radius=6)
            label_text = self.tiny_font.render(role_label, True, self.colors['panel_text'])
            self.screen.blit(label_text, (row_rect.x + 8, row_rect.y + 7))
            button_rect = pygame.Rect(row_rect.right - 66, row_rect.y + 3, 56, 22)
            enabled = game_engine.feature_enabled(feature_id)
            pygame.draw.rect(self.screen, self.colors['green'] if enabled else self.colors['toolbar_button'], button_rect, border_radius=5)
            button_text = self.tiny_font.render('开' if enabled else '关', True, self.colors['white'])
            self.screen.blit(button_text, button_text.get_rect(center=button_rect.center))
            self.panel_actions.append((button_rect, f'debug_toggle:{feature_id}'))
            y += 32
        return y + 4

    def render_facility_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        self.wall_panel_rect = None
        self.terrain_panel_rect = None
        options = self._region_options()
        selected_facility = self._selected_region_option()
        if selected_facility is None:
            return

        facility_rect = pygame.Rect(panel_rect.x + 16, y, 92, 26)
        buff_rect = pygame.Rect(panel_rect.x + 116, y, 92, 26)
        self._draw_mode_button(facility_rect, '设施设置', self.region_palette == 'facility')
        self._draw_mode_button(buff_rect, '增益设置', self.region_palette == 'buff')
        self.panel_actions.append((facility_rect, 'region_palette:facility'))
        self.panel_actions.append((buff_rect, 'region_palette:buff'))
        y += 36

        wall_mode = selected_facility['type'] == 'wall'
        if wall_mode:
            lines = [
                '墙体点一下起点，再点一下终点',
                '右键取消当前墙体或删除墙',
                'Q / E 切换设施项',
                '点击保存设置写入本地 setting 文件',
            ]
        elif self.facility_draw_shape == 'polygon':
            lines = [
                '多边形左键逐点连接',
                '点回第一个点 / 回车 / 右键 可闭合',
                'Esc 取消当前多边形',
                'Q / E 切换设施项',
            ]
        else:
            lines = [
                '矩形设施左键拖拽绘制',
                '右键删除当前光标下设施',
                'Q / E 切换设施项',
                '点击保存设置写入本地 setting 文件',
            ]
        for line in lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        info_lines = []
        if self.mouse_world is not None:
            regions = game_engine.map_manager.get_regions_at(self.mouse_world[0], self.mouse_world[1])
            facility = regions[0] if regions else None
            if facility:
                info_lines = [
                    '当前命中设施',
                    f"ID: {facility.get('id', '-')}",
                    f"类型: {facility.get('type', '-')}",
                    f"队伍: {facility.get('team', '-')}",
                ]
                if facility.get('type') != 'boundary':
                    info_lines.append(f"高度: {facility.get('height_m', 0.0):.2f}m")
                if len(regions) > 1:
                    info_lines.append(f"叠加区域: {len(regions)}")

        y += 8
        if not wall_mode:
            rect_mode_rect = pygame.Rect(panel_rect.x + 16, y, 72, 26)
            polygon_mode_rect = pygame.Rect(panel_rect.x + 96, y, 88, 26)
            self._draw_mode_button(rect_mode_rect, '矩形', self.facility_draw_shape == 'rect')
            self._draw_mode_button(polygon_mode_rect, '多边形', self.facility_draw_shape == 'polygon')
            self.panel_actions.append((rect_mode_rect, 'facility_shape:rect'))
            self.panel_actions.append((polygon_mode_rect, 'facility_shape:polygon'))
            y += 38

        row_height = 34
        info_height = len(info_lines) * 18 + 18 if info_lines else 0
        available_bottom = panel_rect.bottom - y - 16
        min_visible_rows = 4
        editor_height = 0
        if wall_mode:
            editor_height = min(292, max(236, available_bottom - min_visible_rows * row_height))
        else:
            editor_height = min(280, max(220, available_bottom - min_visible_rows * row_height))
        reserved_bottom = max(info_height, max(0, editor_height))
        visible_height = panel_rect.bottom - y - reserved_bottom - 24
        max_visible = max(1, visible_height // row_height)
        max_scroll = max(0, len(options) - max_visible)
        self.facility_scroll = max(0, min(self.facility_scroll, max_scroll))
        end_index = min(len(options), self.facility_scroll + max_visible)

        for index in range(self.facility_scroll, end_index):
            facility = options[index]
            rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
            active = index == self._selected_region_index()
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel_row'], rect, border_radius=5)
            text = self.small_font.render(facility['label'], True, self.colors['panel_text'])
            self.screen.blit(text, (rect.x + 10, rect.y + 5))
            self.panel_actions.append((rect, f'facility:{index}'))
            y += row_height

        if len(options) > max_visible:
            self._render_panel_scrollbar(panel_rect, panel_rect.bottom - visible_height - 16, visible_height, max_visible, len(options), self.facility_scroll)

        if wall_mode:
            wall_panel_rect = pygame.Rect(panel_rect.x + 16, panel_rect.bottom - editor_height - 8, panel_rect.width - 32, editor_height)
            self.wall_panel_rect = wall_panel_rect
            self.render_wall_panel(game_engine, wall_panel_rect)
        elif selected_facility['type'] != 'boundary':
            terrain_panel_rect = pygame.Rect(panel_rect.x + 16, panel_rect.bottom - editor_height - 8, panel_rect.width - 32, editor_height)
            self.terrain_panel_rect = terrain_panel_rect
            self.render_terrain_panel(game_engine, terrain_panel_rect, selected_facility)
        elif info_lines:
            info_y = panel_rect.bottom - info_height - 8
            for line in info_lines:
                text = self.tiny_font.render(line, True, self.colors['panel_text'])
                self.screen.blit(text, (panel_rect.x + 16, info_y))
                info_y += 18

    def render_terrain_editor_panel(self, game_engine, panel_rect):
        toggle_y = panel_rect.y + 56
        terrain_rect = pygame.Rect(panel_rect.x + 16, toggle_y, 92, 28)
        facility_rect = pygame.Rect(panel_rect.x + 116, toggle_y, 92, 28)
        self._draw_mode_button(terrain_rect, '地形笔刷', self.terrain_editor_tool == 'terrain')
        self._draw_mode_button(facility_rect, '设施放置', self.terrain_editor_tool == 'facility')
        self.panel_actions.append((terrain_rect, 'terrain_tool:terrain'))
        self.panel_actions.append((facility_rect, 'terrain_tool:facility'))

        content_rect = pygame.Rect(panel_rect.x, toggle_y + 36, panel_rect.width, panel_rect.height - 36)
        if self.terrain_editor_tool == 'facility':
            self.render_facility_panel(game_engine, content_rect)
            return

        self.render_terrain_brush_panel(game_engine, content_rect)

    def render_terrain_brush_panel(self, game_engine, panel_rect):
        self.terrain_panel_rect = panel_rect
        self.terrain_preview_rect = None
        y = panel_rect.y + 56
        lines = [
            '左键拖拽涂抹格栅地形',
            '上半区右键拖动旋转3D，下半区右键短按选中',
            '设施编辑已合并到本模式顶部的“设施放置”',
            '总览终端下半区也可直接刷地形和放设施',
        ]
        for line in lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        brush = self._selected_terrain_brush_def()
        label = self.tiny_font.render(f"笔刷高度: {brush.get('height_m', 0.0):.2f}m（顶部工具栏设置）", True, self.colors['panel_text'])
        self.screen.blit(label, (panel_rect.x + 16, y + 5))
        y += 28

        radius_text = self.tiny_font.render(f'笔刷半径: {self.terrain_brush_radius}（顶部工具栏设置）', True, self.colors['panel_text'])
        self.screen.blit(radius_text, (panel_rect.x + 16, y + 5))
        y += 28

        smooth_text = self.tiny_font.render(f'区域平滑强度: {self.terrain_smooth_strength}（工具栏设置，0=关闭）', True, self.colors['panel_text'])
        self.screen.blit(smooth_text, (panel_rect.x + 16, y + 5))
        y += 32

        preview_height = 196
        preview_rect = pygame.Rect(panel_rect.x + 16, panel_rect.bottom - preview_height - 12, panel_rect.width - 32, preview_height)
        self.terrain_preview_rect = preview_rect
        info_block = [
            '地形刷不再区分类型，只控制高度与半径。',
            '墙体、补给区、堡垒等特殊语义继续用上方“设施”工具编辑。',
            '刷出的统一地形默认可通行，用于快速塑形。',
            '先用框选选中区域，再点 1/2/3 直接平滑。',
        ]
        for line in info_block:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        cell = None
        if self.mouse_world is not None:
            cell = game_engine.map_manager.get_terrain_grid_cell(self.mouse_world[0], self.mouse_world[1])
        info_lines = [
            f'当前笔刷: {brush["label"]}',
            '左键涂抹，右键选中/拖动画面',
        ]
        if cell is not None:
            info_lines.append(f'当前格: {cell["type"]} / {cell.get("height_m", 0.0):.2f}m')
        info_y = preview_rect.y - 42
        for line in info_lines[-2:]:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, info_y))
            info_y += 18

        if self.selected_terrain_cell_key:
            grid_x, grid_y = game_engine.map_manager._decode_terrain_cell_key(self.selected_terrain_cell_key)
            selected_cell = game_engine.map_manager.terrain_grid_overrides.get(self.selected_terrain_cell_key)
            if selected_cell is not None:
                selected_text = self.tiny_font.render(
                    f'已选中格栅 ({grid_x}, {grid_y})  {selected_cell.get("type", "flat")}  {selected_cell.get("height_m", 0.0):.2f}m',
                    True,
                    self.colors['panel_text'],
                )
                self.screen.blit(selected_text, (panel_rect.x + 16, preview_rect.y - 64))
                delete_rect = pygame.Rect(panel_rect.right - 110, preview_rect.y - 68, 94, 24)
                pygame.draw.rect(self.screen, self.colors['red'], delete_rect, border_radius=5)
                delete_text = self.tiny_font.render('删除选中地形', True, self.colors['white'])
                self.screen.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
                self.panel_actions.append((delete_rect, 'delete_selected_terrain'))

        self.render_terrain_preview(game_engine, preview_rect)

    def render_terrain_preview(self, game_engine, rect):
        pygame.draw.rect(self.screen, self.colors['panel_row'], rect, border_radius=6)
        title = self.small_font.render('格栅 3D 预览', True, self.colors['panel_text'])
        self.screen.blit(title, (rect.x + 8, rect.y + 8))

        if self.mouse_world is not None:
            center_grid_x, center_grid_y = game_engine.map_manager._world_to_grid(self.mouse_world[0], self.mouse_world[1])
        else:
            grid_width, grid_height = game_engine.map_manager._grid_dimensions()
            center_grid_x = grid_width // 2
            center_grid_y = grid_height // 2

        tile_w = 18
        tile_h = 9
        height_scale = 16
        preview_origin_x = rect.x + rect.width // 2
        preview_origin_y = rect.y + rect.height - 26
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
            pygame.draw.polygon(self.screen, left_color, left)
            pygame.draw.polygon(self.screen, right_color, right)
            pygame.draw.polygon(self.screen, top_color, top)
            pygame.draw.polygon(self.screen, self.colors['panel_border'], top, 1)

    def render_wall_panel(self, game_engine, rect):
        pygame.draw.rect(self.screen, self.colors['panel_row'], rect, border_radius=6)
        title = self.small_font.render('已画墙', True, self.colors['panel_text'])
        self.screen.blit(title, (rect.x + 8, rect.y + 8))

        walls = game_engine.map_manager.get_facility_regions('wall')
        if not walls:
            text = self.tiny_font.render('当前还没有墙，先在场地上点击两点绘制。', True, self.colors['panel_text'])
            self.screen.blit(text, (rect.x + 8, rect.y + 38))
            self.selected_wall_id = None
            return

        wall_ids = [wall['id'] for wall in walls]
        if self.selected_wall_id not in wall_ids:
            self.selected_wall_id = wall_ids[0]

        list_top = rect.y + 34
        row_height = 26
        list_height = min(104, max(52, rect.height - 154))
        max_visible = max(1, list_height // row_height)
        max_scroll = max(0, len(walls) - max_visible)
        self.wall_scroll = max(0, min(self.wall_scroll, max_scroll))
        end_index = min(len(walls), self.wall_scroll + max_visible)

        for index in range(self.wall_scroll, end_index):
            wall = walls[index]
            row_rect = pygame.Rect(rect.x + 8, list_top, rect.width - 24, 22)
            active = wall['id'] == self.selected_wall_id
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel'], row_rect, border_radius=4)
            label = self.tiny_font.render(f"{wall['id']}  高 {wall.get('height_m', 1.0):.2f}m", True, self.colors['panel_text'])
            self.screen.blit(label, (row_rect.x + 8, row_rect.y + 4))
            self.panel_actions.append((row_rect, f"wall_select:{wall['id']}"))
            list_top += row_height

        if len(walls) > max_visible:
            self._render_panel_scrollbar(rect, rect.y + 34, list_height, max_visible, len(walls), self.wall_scroll)

        wall = game_engine.map_manager.get_facility_by_id(self.selected_wall_id)
        if wall is None:
            return

        details_y = rect.y + 42 + list_height
        movement_rect = pygame.Rect(rect.x + 8, details_y, rect.width - 16, 26)
        vision_rect = pygame.Rect(rect.x + 8, details_y + 32, rect.width - 16, 26)
        self._draw_toggle_row(movement_rect, f"运动阻拦: {'开' if wall.get('blocks_movement', True) else '关'}", wall.get('blocks_movement', True))
        self._draw_toggle_row(vision_rect, f"视野阻拦: {'开' if wall.get('blocks_vision', True) else '关'}", wall.get('blocks_vision', True))
        self.panel_actions.append((movement_rect, f"wall_toggle:{wall['id']}:movement"))
        self.panel_actions.append((vision_rect, f"wall_toggle:{wall['id']}:vision"))

        height_label = self.tiny_font.render(f"墙高: {wall.get('height_m', 1.0):.2f}m", True, self.colors['panel_text'])
        self.screen.blit(height_label, (rect.x + 8, details_y + 72))
        input_rect = pygame.Rect(rect.right - 138, details_y + 68, 124, 22)
        active = self._is_numeric_input_active('wall', wall['id'])
        input_text = f"{wall.get('height_m', 1.0):.2f}"
        if active and self.active_numeric_input is not None:
            input_text = self.active_numeric_input['text']
        self._draw_input_box(input_rect, input_text, active)
        self.panel_actions.append((input_rect, f"height_input:wall:{wall['id']}"))
        hint = self.tiny_font.render('点击输入，回车确认', True, self.colors['panel_text'])
        self.screen.blit(hint, (rect.x + 8, details_y + 94))

        delete_rect = pygame.Rect(rect.right - 108, details_y + 90, 96, 24)
        pygame.draw.rect(self.screen, self.colors['red'], delete_rect, border_radius=5)
        delete_text = self.tiny_font.render('删除该墙', True, self.colors['white'])
        self.screen.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
        self.panel_actions.append((delete_rect, f'delete_facility:{wall["id"]}'))

        summary_lines = [
            f"长度: {math.hypot(wall['x2'] - wall['x1'], wall['y2'] - wall['y1']):.1f}",
            f"端点: ({wall['x1']}, {wall['y1']}) -> ({wall['x2']}, {wall['y2']})",
        ]
        detail_line_y = details_y + 122
        for line in summary_lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (rect.x + 8, detail_line_y))
            detail_line_y += 18

    def render_terrain_panel(self, game_engine, rect, selected_facility):
        pygame.draw.rect(self.screen, self.colors['panel_row'], rect, border_radius=6)
        height_editable_types = {'base', 'outpost', 'fly_slope', 'undulating_road', 'rugged_road', 'first_step', 'second_step', 'dog_hole', 'supply', 'fort'}
        show_height_editor = selected_facility.get('type') in height_editable_types
        title = self.small_font.render('区域详情' if not show_height_editor else '地形高度', True, self.colors['panel_text'])
        self.screen.blit(title, (rect.x + 8, rect.y + 8))

        regions = [
            region for region in game_engine.map_manager.get_facility_regions(selected_facility['type'])
            if region.get('type') == selected_facility['type']
        ]
        if not regions:
            text = self.tiny_font.render('当前类型还没有区域。矩形拖拽或多边形闭合后会出现在这里。', True, self.colors['panel_text'])
            self.screen.blit(text, (rect.x + 8, rect.y + 38))
            self.selected_terrain_id = None
            return

        region_ids = [region['id'] for region in regions]
        if self.selected_terrain_id not in region_ids:
            self.selected_terrain_id = region_ids[-1]

        list_top = rect.y + 34
        row_height = 26
        list_height = min(104, max(52, rect.height - 136))
        max_visible = max(1, list_height // row_height)
        max_scroll = max(0, len(regions) - max_visible)
        self.terrain_scroll = max(0, min(self.terrain_scroll, max_scroll))
        end_index = min(len(regions), self.terrain_scroll + max_visible)

        for index in range(self.terrain_scroll, end_index):
            region = regions[index]
            row_rect = pygame.Rect(rect.x + 8, list_top, rect.width - 24, 22)
            active = region['id'] == self.selected_terrain_id
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel'], row_rect, border_radius=4)
            shape_label = '多边形' if region.get('shape') == 'polygon' else '矩形'
            label_text = f"{region['id']}  {shape_label}"
            if show_height_editor:
                label_text += f"  高 {region.get('height_m', 0.0):.2f}m"
            label = self.tiny_font.render(label_text, True, self.colors['panel_text'])
            self.screen.blit(label, (row_rect.x + 8, row_rect.y + 4))
            self.panel_actions.append((row_rect, f"terrain_select:{region['id']}"))
            list_top += row_height

        if len(regions) > max_visible:
            self._render_panel_scrollbar(rect, rect.y + 34, list_height, max_visible, len(regions), self.terrain_scroll)

        region = game_engine.map_manager.get_facility_by_id(self.selected_terrain_id)
        if region is None:
            return

        details_y = rect.y + 42 + list_height
        shape_text = self.tiny_font.render(f"形状: {'多边形' if region.get('shape') == 'polygon' else '矩形'}", True, self.colors['panel_text'])
        self.screen.blit(shape_text, (rect.x + 8, details_y))
        delete_y = details_y + 20
        if show_height_editor:
            height_label = self.tiny_font.render(f"地形高: {region.get('height_m', 0.0):.2f}m", True, self.colors['panel_text'])
            self.screen.blit(height_label, (rect.x + 8, details_y + 22))

            input_rect = pygame.Rect(rect.right - 138, details_y + 18, 124, 22)
            active = self._is_numeric_input_active('terrain', region['id'])
            input_text = f"{region.get('height_m', 0.0):.2f}"
            if active and self.active_numeric_input is not None:
                input_text = self.active_numeric_input['text']
            self._draw_input_box(input_rect, input_text, active)
            self.panel_actions.append((input_rect, f"height_input:terrain:{region['id']}"))
            hint = self.tiny_font.render('点击输入，回车确认', True, self.colors['panel_text'])
            self.screen.blit(hint, (rect.x + 8, details_y + 48))
            delete_y = details_y + 44
        else:
            team_text = self.tiny_font.render(f"队伍: {region.get('team', 'neutral')}", True, self.colors['panel_text'])
            self.screen.blit(team_text, (rect.x + 8, details_y + 22))

        delete_rect = pygame.Rect(rect.right - 116, delete_y, 104, 24)
        pygame.draw.rect(self.screen, self.colors['red'], delete_rect, border_radius=5)
        delete_text = self.tiny_font.render('删除该区域', True, self.colors['white'])
        self.screen.blit(delete_text, delete_text.get_rect(center=delete_rect.center))
        self.panel_actions.append((delete_rect, f'delete_facility:{region["id"]}'))

        if region.get('shape') == 'polygon':
            summary = f"顶点数: {len(region.get('points', []))}"
        else:
            summary = f"范围: ({region['x1']}, {region['y1']}) -> ({region['x2']}, {region['y2']})"
        summary_text = self.tiny_font.render(summary, True, self.colors['panel_text'])
        self.screen.blit(summary_text, (rect.x + 8, delete_rect.bottom + 8))

        if self.facility_draw_shape == 'polygon' and self.polygon_points:
            pending_text = self.tiny_font.render(f'当前多边形已记录 {len(self.polygon_points)} 个点', True, self.colors['panel_text'])
            self.screen.blit(pending_text, (rect.x + 8, details_y + 94))

    def _draw_toggle_row(self, rect, label, enabled):
        pygame.draw.rect(self.screen, self.colors['panel_row_active'] if enabled else self.colors['panel'], rect, border_radius=4)
        text = self.tiny_font.render(label, True, self.colors['panel_text'])
        self.screen.blit(text, (rect.x + 8, rect.y + 5))

    def _draw_mode_button(self, rect, label, active):
        pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel_row'], rect, border_radius=4)
        text = self.tiny_font.render(label, True, self.colors['panel_text'])
        self.screen.blit(text, (rect.x + 10, rect.y + 5))

    def _draw_text_tooltip(self, clamp_rect, lines, anchor_pos):
        if not lines or anchor_pos is None:
            return
        rendered_lines = [self.tiny_font.render(str(line), True, self.colors['white']) for line in lines]
        width = max(line.get_width() for line in rendered_lines) + 20
        height = 12 + sum(line.get_height() + 4 for line in rendered_lines)
        tooltip = pygame.Surface((width, height), pygame.SRCALPHA)
        tooltip.fill((18, 24, 30, 228))
        pygame.draw.rect(tooltip, (245, 247, 250, 180), tooltip.get_rect(), 1, border_radius=8)
        y = 8
        for rendered in rendered_lines:
            tooltip.blit(rendered, (10, y))
            y += rendered.get_height() + 4
        box_x = min(clamp_rect.right - width - 8, max(clamp_rect.x + 8, anchor_pos[0] + 16))
        box_y = min(clamp_rect.bottom - height - 8, max(clamp_rect.y + 8, anchor_pos[1] + 16))
        self.screen.blit(tooltip, (box_x, box_y))

    def _draw_input_box(self, rect, text, active):
        pygame.draw.rect(self.screen, self.colors['white'], rect, border_radius=4)
        border_color = self.colors['toolbar_button_active'] if active else self.colors['panel_border']
        pygame.draw.rect(self.screen, border_color, rect, 2 if active else 1, border_radius=4)
        rendered = self.tiny_font.render(text or '0.00', True, self.colors['panel_text'])
        self.screen.blit(rendered, (rect.x + 8, rect.y + 4))

    def _terrain_color_by_type(self, terrain_type):
        color_map = {
            'flat': (214, 214, 214),
            'custom_terrain': (214, 156, 92),
            'wall': (50, 50, 50),
            'dead_zone': (122, 22, 22),
            'fly_slope': (240, 150, 60),
            'undulating_road': (120, 220, 120),
            'rugged_road': (86, 74, 62),
            'first_step': (190, 190, 255),
            'second_step': (255, 140, 140),
            'dog_hole': (255, 120, 220),
            'boundary': (255, 255, 255),
            'supply': (248, 214, 72),
            'fort': (145, 110, 80),
            'outpost': (80, 160, 255),
            'base': (255, 80, 80),
            'energy_mechanism': (255, 195, 64),
            'mining_area': (82, 201, 153),
            'mineral_exchange': (69, 137, 255),
            'buff_base': (255, 102, 102),
            'buff_outpost': (118, 174, 255),
            'buff_fort': (161, 129, 95),
            'buff_supply': (255, 229, 110),
            'buff_assembly': (255, 170, 66),
            'buff_hero_deployment': (255, 122, 122),
            'buff_central_highland': (176, 132, 255),
            'buff_trapezoid_highland': (214, 130, 255),
            'buff_terrain_highland_red_start': (255, 168, 168),
            'buff_terrain_highland_red_end': (214, 96, 96),
            'buff_terrain_highland_blue_start': (135, 198, 255),
            'buff_terrain_highland_blue_end': (74, 140, 214),
            'buff_terrain_road_red_start': (255, 194, 128),
            'buff_terrain_road_red_end': (224, 136, 72),
            'buff_terrain_road_blue_start': (148, 220, 255),
            'buff_terrain_road_blue_end': (70, 163, 214),
            'buff_terrain_fly_slope_red_start': (255, 152, 202),
            'buff_terrain_fly_slope_red_end': (214, 88, 150),
            'buff_terrain_fly_slope_blue_start': (170, 182, 255),
            'buff_terrain_fly_slope_blue_end': (102, 118, 214),
            'buff_terrain_slope_red_start': (255, 164, 117),
            'buff_terrain_slope_red_end': (214, 110, 66),
            'buff_terrain_slope_blue_start': (156, 255, 205),
            'buff_terrain_slope_blue_end': (86, 201, 145),
        }
        return color_map.get(terrain_type, (255, 255, 255))

    def _terrain_color_by_code(self, terrain_code):
        terrain_type = self.game_engine.map_manager.terrain_label_by_code.get(terrain_code, '平地')
        type_lookup = {
            '平地': 'flat',
            '自定义地形': 'custom_terrain',
            '边界': 'boundary',
            '墙': 'wall',
            '死区': 'dead_zone',
            '狗洞': 'dog_hole',
            '二级台阶': 'second_step',
            '一级台阶': 'first_step',
            '飞坡': 'fly_slope',
            '起伏路段': 'rugged_road',
            '补给区': 'supply',
            '堡垒': 'fort',
            '前哨站': 'outpost',
            '基地': 'base',
        }
        return self._terrain_color_by_type(type_lookup.get(terrain_type, 'flat'))

    def render_terrain_grid_overlay(self, map_manager):
        if self.viewport is None:
            return

        overlay_size = (int(self.window_width), int(self.window_height))
        if self.terrain_brush_overlay_surface is None or self.terrain_brush_overlay_size != overlay_size:
            self.terrain_brush_overlay_surface = pygame.Surface(overlay_size, pygame.SRCALPHA).convert_alpha()
            self.terrain_brush_overlay_size = overlay_size
        overlay = self.terrain_brush_overlay_surface
        overlay.fill((0, 0, 0, 0))
        grid_width, grid_height = map_manager._grid_dimensions()
        map_rect = self._map_rect()
        draw_outlines = self._terrain_overlay_draw_outlines(map_manager, map_rect)
        self._blit_world_surface(self._get_world_terrain_grid_overlay_surface(map_manager, draw_outlines=draw_outlines), map_rect, smooth=False)

        if self.mouse_world is not None:
            brush = self._selected_terrain_brush_def()
            center_grid_x, center_grid_y = map_manager._world_to_grid(self.mouse_world[0], self.mouse_world[1])
            color = self._terrain_color_by_type(brush['type'])
            for grid_y in range(max(0, center_grid_y - self.terrain_brush_radius), min(grid_height, center_grid_y + self.terrain_brush_radius + 1)):
                for grid_x in range(max(0, center_grid_x - self.terrain_brush_radius), min(grid_width, center_grid_x + self.terrain_brush_radius + 1)):
                    if math.hypot(grid_x - center_grid_x, grid_y - center_grid_y) > self.terrain_brush_radius + 0.25:
                        continue
                    x1, y1, x2, y2 = map_manager._grid_cell_bounds(grid_x, grid_y)
                    sx1, sy1 = self.world_to_screen(x1, y1)
                    sx2, sy2 = self.world_to_screen(x2 + 1, y2 + 1)
                    rect = pygame.Rect(sx1, sy1, max(1, sx2 - sx1), max(1, sy2 - sy1))
                    pygame.draw.rect(overlay, (*color, 72), rect)
                    pygame.draw.rect(overlay, (*self.colors['white'], 160), rect, 1)
        selection_keys = self._terrain_selection_keys()
        for key in sorted(selection_keys):
            grid_x, grid_y = map_manager._decode_terrain_cell_key(key)
            x1, y1, x2, y2 = map_manager._grid_cell_bounds(grid_x, grid_y)
            sx1, sy1 = self.world_to_screen(x1, y1)
            sx2, sy2 = self.world_to_screen(x2 + 1, y2 + 1)
            rect = pygame.Rect(sx1, sy1, max(1, sx2 - sx1), max(1, sy2 - sy1))
            pygame.draw.rect(overlay, (*self.colors['yellow'], 90), rect)
            pygame.draw.rect(overlay, self.colors['yellow'], rect, 2)
        self.screen.blit(overlay, (0, 0))

    def render_entity_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        lines = [
            '长按实体可直接拖拽站位',
            '左键空白处可放置当前实体',
            'R 旋转当前实体朝向',
            'Q / E 切换实体',
            '点击保存设置写入本地 setting 文件',
        ]
        for line in lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        y += 8
        for index, (team, key) in enumerate(self.entity_keys):
            rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
            active = index == self.selected_entity_index
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel_row'], rect, border_radius=5)
            labels = {
                'robot_1': '1英雄',
                'robot_2': '2工程',
                'robot_3': '3步兵',
                'robot_4': '4步兵',
                'robot_7': '7哨兵',
            }
            team_label = '红方' if team == 'red' else '蓝方'
            text = self.small_font.render(f'{team_label}{labels.get(key, key)}', True, self.colors['panel_text'])
            self.screen.blit(text, (rect.x + 10, rect.y + 5))
            self.panel_actions.append((rect, f'entity:{index}'))
            y += 34

        if 0 <= self.selected_entity_index < len(self.entity_keys):
            team, key = self.entity_keys[self.selected_entity_index]
            entity_id = f'{team}_{key}'
            detail = game_engine.get_entity_detail_data(entity_id)
            if detail is not None:
                y += 10
                title = self.small_font.render('当前实体状态', True, self.colors['panel_text'])
                self.screen.blit(title, (panel_rect.x + 16, y))
                y += 26
                speed_text = f"实时速度: {detail.get('movement_speed_mps', 0.0):.2f} m/s"
                hp_text = f"生命: {detail.get('health', 0.0):.0f}/{detail.get('max_health', 0.0):.0f}"
                pos_text = f"位置: ({detail.get('position_x', 0.0):.0f}, {detail.get('position_y', 0.0):.0f})"
                for line in (speed_text, hp_text, pos_text):
                    text = self.tiny_font.render(line, True, self.colors['panel_text'])
                    self.screen.blit(text, (panel_rect.x + 16, y))
                    y += 18

    def render_rules_panel(self, game_engine, panel_rect):
        y = panel_rect.y + 56
        lines = [
            '点击 +/- 调整数值',
            '方向键上下切换，左右调整',
            '保存设置后，下次运行自动按 setting 加载',
            '开始/重开可完整应用新规则',
        ]
        for line in lines:
            text = self.tiny_font.render(line, True, self.colors['panel_text'])
            self.screen.blit(text, (panel_rect.x + 16, y))
            y += 18

        y += 10
        row_height = 32
        numeric_rules = self._flatten_numeric_rules(game_engine.config.get('rules', {}))
        visible_height = panel_rect.bottom - y - 16
        max_visible = max(1, visible_height // row_height)
        start = min(self.rule_scroll, max(0, len(numeric_rules) - max_visible))
        end = min(len(numeric_rules), start + max_visible)

        for visible_index, item in enumerate(numeric_rules[start:end]):
            rule_index = start + visible_index
            rect = pygame.Rect(panel_rect.x + 16, y, panel_rect.width - 32, 28)
            active = rule_index == self.selected_rule_index
            pygame.draw.rect(self.screen, self.colors['panel_row_active'] if active else self.colors['panel_row'], rect, border_radius=5)
            label = self.tiny_font.render(self._format_rule_label(item['path']), True, self.colors['panel_text'])
            value = self.tiny_font.render(str(item['value']), True, self.colors['panel_text'])
            minus_rect = pygame.Rect(rect.right - 68, rect.y + 4, 24, 20)
            plus_rect = pygame.Rect(rect.right - 34, rect.y + 4, 24, 20)
            pygame.draw.rect(self.screen, self.colors['toolbar_button'], minus_rect, border_radius=4)
            pygame.draw.rect(self.screen, self.colors['toolbar_button'], plus_rect, border_radius=4)
            self.screen.blit(label, (rect.x + 8, rect.y + 7))
            self.screen.blit(value, (rect.right - 118, rect.y + 7))
            self.screen.blit(self.tiny_font.render('-', True, self.colors['white']), (minus_rect.x + 8, minus_rect.y + 2))
            self.screen.blit(self.tiny_font.render('+', True, self.colors['white']), (plus_rect.x + 7, plus_rect.y + 2))
            self.panel_actions.append((rect, f'rule_select:{rule_index}'))
            self.panel_actions.append((minus_rect, f'rule_adjust:{item["path"]}:-1'))
            self.panel_actions.append((plus_rect, f'rule_adjust:{item["path"]}:1'))
            y += row_height

    def _flatten_numeric_rules(self, data, prefix=''):
        items = []
        for key, value in data.items():
            path = f'{prefix}.{key}' if prefix else key
            if isinstance(value, dict):
                items.extend(self._flatten_numeric_rules(value, path))
            elif isinstance(value, (int, float)) and not isinstance(value, bool):
                items.append({'path': path, 'value': value})
        return items

    def _format_rule_label(self, path):
        return path.replace('.', ' / ')

    def _terrain_brush_active(self):
        return self.edit_mode == 'terrain' and self.terrain_editor_tool == 'terrain'

    def _terrain_select_mode_active(self):
        return self._terrain_brush_active() and getattr(self, 'terrain_workflow_mode', 'brush') == 'select'

    def _terrain_paint_mode_active(self):
        return self._terrain_brush_active() and getattr(self, 'terrain_workflow_mode', 'brush') == 'brush'

    def _terrain_eraser_mode_active(self):
        return self._terrain_brush_active() and getattr(self, 'terrain_workflow_mode', 'brush') == 'erase'

    def _terrain_shape_tool_active(self):
        return self._terrain_brush_active() and getattr(self, 'terrain_workflow_mode', 'brush') == 'shape'

    def _facility_edit_active(self):
        return self.edit_mode == 'terrain' and self.terrain_editor_tool == 'facility'

    def _mode_label(self, mode):
        labels = {
            'none': '浏览模式',
            'terrain': '地形编辑',
            'entity': '站位编辑',
            'rules': '规则编辑',
        }
        if mode == 'terrain':
            return '统一编辑(设施)' if self.terrain_editor_tool == 'facility' else '统一编辑(地形)'
        return labels.get(mode, mode)

    def _render_panel_scrollbar(self, panel_rect, top_y, height, visible_count, total_count, scroll_value):
        track_rect = pygame.Rect(panel_rect.right - 14, top_y, 6, height)
        pygame.draw.rect(self.screen, self.colors['panel_border'], track_rect, border_radius=3)

        thumb_height = max(28, int(height * (visible_count / max(total_count, 1))))
        max_scroll = max(1, total_count - visible_count)
        travel = max(0, height - thumb_height)
        thumb_y = top_y if total_count <= visible_count else top_y + int((scroll_value / max_scroll) * travel)
        thumb_rect = pygame.Rect(track_rect.x, thumb_y, track_rect.width, thumb_height)
        pygame.draw.rect(self.screen, self.colors['toolbar_button_active'], thumb_rect, border_radius=3)