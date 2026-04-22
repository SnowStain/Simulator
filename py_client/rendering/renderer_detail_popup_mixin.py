#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math

from pygame_compat import pygame


class RendererDetailPopupMixin:
    def render_robot_detail_popup(self, game_engine):
        if not self.selected_hud_entity_id:
            self.robot_detail_rect = None
            return

        detail = game_engine.get_entity_detail_data(self.selected_hud_entity_id)
        if detail is None:
            self.selected_hud_entity_id = None
            self.robot_detail_rect = None
            return

        overlay = pygame.Surface((self.window_width, self.window_height), pygame.SRCALPHA)
        overlay.fill((10, 14, 20, 120))
        self.screen.blit(overlay, (0, 0))

        panel_width = min(920, self.window_width - 60)
        panel_height = min(720, self.window_height - 60)
        panel_rect = pygame.Rect((self.window_width - panel_width) // 2, (self.window_height - panel_height) // 2, panel_width, panel_height)
        self.robot_detail_rect = panel_rect
        pygame.draw.rect(self.screen, self.colors['hud_panel'], panel_rect, border_radius=16)
        pygame.draw.rect(self.screen, self.colors['panel_border'], panel_rect, 1, border_radius=16)
        detail_page = max(0, min(1, int(getattr(self, 'robot_detail_page', 0))))

        team_color = self.colors['red'] if detail['team'] == 'red' else self.colors['blue']
        title = self.font.render(f"{detail['team'].upper()} {detail['label']} | {detail['robot_type']}", True, self.colors['white'])
        self.screen.blit(title, (panel_rect.x + 22, panel_rect.y + 18))

        close_rect = pygame.Rect(panel_rect.right - 44, panel_rect.y + 14, 28, 28)
        pygame.draw.rect(self.screen, self.colors['toolbar_button'], close_rect, border_radius=8)
        close_text = self.small_font.render('X', True, self.colors['white'])
        self.screen.blit(close_text, close_text.get_rect(center=close_rect.center))
        self.hud_actions.append((close_rect, 'close_robot_detail'))

        tab_width = 104
        tab_gap = 8
        decision_tab_rect = pygame.Rect(close_rect.x - tab_width - 10, panel_rect.y + 14, tab_width, 28)
        overview_tab_rect = pygame.Rect(decision_tab_rect.x - tab_width - tab_gap, panel_rect.y + 14, tab_width, 28)
        self._draw_detail_tab(overview_tab_rect, '状态总览', detail_page == 0)
        self._draw_detail_tab(decision_tab_rect, '决策分析', detail_page == 1)
        self.hud_actions.append((overview_tab_rect, 'robot_detail_page:0'))
        self.hud_actions.append((decision_tab_rect, 'robot_detail_page:1'))

        status_items = [
            f"状态 {detail['state']}",
            '存活' if detail['alive'] else '已击毁',
            f"底盘 {self._format_chassis_state(detail.get('chassis_state', 'normal'))}",
        ]
        if not detail.get('is_engineer'):
            status_items.append('有枪管' if detail['has_barrel'] else '无枪管')
            status_items.append('枪管锁定' if detail.get('front_gun_locked') else '枪管可用')
            status_items.append(f"火控 {detail['fire_control_state']}")
        if detail.get('sentry_mode'):
            status_items.append(f"哨兵模式 {detail['sentry_mode']}")
        if detail.get('is_hero'):
            status_items.append(f"部署 {self._format_hero_deployment_state(detail.get('hero_deployment_state', 'inactive'))}")
            status_items.append('可吊射' if detail.get('hero_has_ammo') else '无弹禁吊射')
        if detail.get('heat_lock_state', 'normal') != 'normal':
            status_items.append(f"热量锁定 {self._format_heat_lock_state(detail.get('heat_lock_state', 'normal'))}")
        if detail['target_id']:
            status_items.append(f"目标 {detail['target_id']}")
        banner_rect = pygame.Rect(panel_rect.x + 20, panel_rect.y + 58, panel_rect.width - 40, 40)
        pygame.draw.rect(self.screen, team_color, banner_rect, border_radius=10)
        banner_text = self.tiny_font.render(' | '.join(status_items), True, self.colors['white'])
        self.screen.blit(banner_text, banner_text.get_rect(center=banner_rect.center))

        mode_y = panel_rect.y + 108
        left_x = panel_rect.x + 24
        right_x = panel_rect.centerx + 12
        if detail.get('supports_drive_modes', False):
            mode_labels = detail.get('mode_labels', {})
            left_title = mode_labels.get('left_title', '底盘模式')
            right_title = mode_labels.get('right_title', '云台模式')
            left_options = mode_labels.get('left_options', [('health_priority', '血量优先'), ('power_priority', '功率优先')])
            right_options = mode_labels.get('right_options', [('cooling_priority', '冷却优先'), ('burst_priority', '爆发优先')])

            chassis_label = self.tiny_font.render(left_title, True, self.colors['white'])
            gimbal_label = self.tiny_font.render(right_title, True, self.colors['white'])
            self.screen.blit(chassis_label, (left_x, mode_y))
            self.screen.blit(gimbal_label, (right_x, mode_y))

            chassis_hp_rect = pygame.Rect(left_x + 64, mode_y - 4, 78, 24)
            chassis_power_rect = pygame.Rect(left_x + 148, mode_y - 4, 78, 24)
            gimbal_cool_rect = pygame.Rect(right_x + 64, mode_y - 4, 78, 24)
            gimbal_burst_rect = pygame.Rect(right_x + 148, mode_y - 4, 78, 24)
            self._draw_mode_button(chassis_hp_rect, left_options[0][1], detail.get('chassis_mode') == left_options[0][0])
            self._draw_mode_button(chassis_power_rect, left_options[1][1], detail.get('chassis_mode') == left_options[1][0])
            self._draw_mode_button(gimbal_cool_rect, right_options[0][1], detail.get('gimbal_mode') == right_options[0][0])
            self._draw_mode_button(gimbal_burst_rect, right_options[1][1], detail.get('gimbal_mode') == right_options[1][0])
            self.hud_actions.extend([
                (chassis_hp_rect, f"entity_mode:{detail['entity_id']}:chassis_mode:{left_options[0][0]}"),
                (chassis_power_rect, f"entity_mode:{detail['entity_id']}:chassis_mode:{left_options[1][0]}"),
                (gimbal_cool_rect, f"entity_mode:{detail['entity_id']}:gimbal_mode:{right_options[0][0]}"),
                (gimbal_burst_rect, f"entity_mode:{detail['entity_id']}:gimbal_mode:{right_options[1][0]}"),
            ])

        bar_y = panel_rect.y + 150
        bar_left = panel_rect.x + 24
        bar_width = panel_rect.width - 48
        self._draw_stat_bar(bar_left, bar_y, bar_width, '血量', detail['health'], detail['max_health'], (182, 67, 67))
        self._draw_stat_bar(bar_left, bar_y + 34, bar_width, '热量', detail['heat'], detail['heat_limit'], (225, 145, 34))
        self._draw_stat_bar(bar_left, bar_y + 68, bar_width, '功率', detail.get('movement_power_ratio', 0.0), 1.0, (70, 176, 220), value_text=f"{detail.get('movement_speed_ratio', 0.0) * 100:.0f}% 负载")

        section_top = bar_y + 112
        column_width = (panel_rect.width - 52) // 2
        content_height = max(220, panel_rect.bottom - section_top - 44)
        left_rect = pygame.Rect(panel_rect.x + 20, section_top, column_width, content_height)
        right_rect = pygame.Rect(panel_rect.x + 32 + column_width, section_top, column_width, content_height)

        combat_lines = [
            f"姿态模式: {detail['posture']}",
            f"底盘构型: {detail.get('chassis_subtype_label') or detail.get('chassis_subtype') or '标准'}",
            f"底盘模式: {dict(detail.get('mode_labels', {}).get('left_options', [('health_priority', '血量优先'), ('power_priority', '功率优先')])).get(detail.get('chassis_mode'), detail.get('chassis_mode'))}",
            f"冷却模式: {dict(detail.get('mode_labels', {}).get('right_options', [('cooling_priority', '冷却优先'), ('burst_priority', '爆发优先')])).get(detail.get('gimbal_mode'), detail.get('gimbal_mode'))}",
            f"脱战状态: {'是' if detail.get('out_of_combat') else '否'}",
            f"当前移速: {detail.get('movement_speed_mps', 0.0):.2f} m/s",
            f"移速上限: {detail.get('movement_speed_cap_mps', 0.0):.2f} m/s",
            f"底盘功率: {detail.get('chassis_power_draw_w', 0.0):.1f} W | {detail.get('chassis_rpm', 0.0):.0f} rpm",
            f"功率恢复: {detail['power_recovery_rate']:.2f}/s",
            f"当前冷却: {detail['current_cooling_rate']:.2f}/s",
            f"基础冷却: {detail['base_heat_dissipation_rate']:.2f}/s",
            f"地形增益: {detail['terrain_buff_timer']:.2f}s",
            f"堡垒增益: {'是' if detail['fort_buff_active'] else '否'}",
            f"小能量增益: {detail.get('energy_small_buff_timer', 0.0):.2f}s",
            f"大能量增益: {detail.get('energy_large_buff_timer', 0.0):.2f}s",
        ]
        if detail.get('is_hero'):
            combat_lines.insert(3, f"部署状态: {self._format_hero_deployment_state(detail.get('hero_deployment_state', 'inactive'))}")
            combat_lines.insert(4, f"部署进度: {detail.get('hero_deployment_charge', 0.0):.1f}/{detail.get('hero_deployment_delay', 2.0):.1f}s")
            combat_lines.insert(5, f"部署目标: {detail.get('hero_deployment_target_id') or '无'}")
            combat_lines.insert(6, f"吊射命中率: {detail.get('hero_deployment_hit_probability', 0.0) * 100:.0f}%")
            combat_lines.insert(7, f"吊射弹药: {'满足' if detail.get('hero_has_ammo') else '不足'}")
        if detail_page == 0:
            if detail.get('is_engineer'):
                right_lines = [
                    f"携带矿物: {detail.get('carried_minerals', 0)}",
                    f"矿物类型: {detail.get('carried_mineral_type') or '无'}",
                    f"累计采矿: {detail.get('mined_minerals_total', 0)}",
                    f"累计兑矿: {detail.get('exchanged_minerals_total', 0)}",
                    f"累计金币: {detail.get('exchanged_gold_total', 0.0):.0f}",
                    f"目标: {detail.get('target_id') or '无'}",
                    f"无敌剩余: {detail['invincible_timer']:.2f}s",
                    f"无效剩余: {detail.get('respawn_invalid_timer', 0.0):.2f}s",
                    f"虚弱状态: {'是' if detail.get('respawn_weak_active') else '否'}",
                ]
                self._draw_info_section(left_rect, '机动状态', combat_lines)
                self._draw_info_section(right_rect, '采矿状态', right_lines)
            else:
                weapon_lines = [
                    f"当前弹药: {detail['ammo']}",
                    f"17mm 弹量: {detail.get('ammo_17mm', 0)}",
                    f"42mm 弹量: {detail.get('ammo_42mm', 0)}",
                    f"能量机关: {self._format_energy_state(detail.get('energy_mechanism_state', {}))}",
                    f"机关窗口: {self._format_energy_window(detail.get('energy_mechanism_state', {}))}",
                    f"机关计时: {float(detail.get('energy_mechanism_state', {}).get('window_timer', 0.0)):.2f}s",
                    f"可激活次数: 小 {int(detail.get('energy_mechanism_state', {}).get('small_tokens', 0))} / 大 {int(detail.get('energy_mechanism_state', {}).get('large_tokens', 0))}",
                    f"枪管状态: {'锁定' if detail.get('front_gun_locked') else '可用'}",
                    f"规则射速: {detail['fire_rate_hz']:.2f} 发/s",
                    f"当前射速: {detail['effective_fire_rate_hz']:.2f} 发/s",
                    f"单发耗弹: {detail['ammo_per_shot']}",
                    f"单发功率: {detail['power_per_shot']:.1f}",
                    f"单发加热: {detail['heat_gain_per_shot']:.1f}",
                    f"枪口冷却: {detail['shot_cooldown']:.2f}s",
                    f"热量锁定: {self._format_heat_lock_state(detail.get('heat_lock_state', 'normal'))}",
                    f"超限阈值: {detail.get('heat_limit', 0.0):.0f} / {detail.get('heat_soft_lock_threshold', 0.0):.0f}",
                    f"自瞄距离: {detail['auto_aim_max_distance_m']:.2f}m",
                    f"自瞄视场: {detail['auto_aim_fov_deg']:.1f}°",
                ]
                self._draw_info_section(left_rect, '机动状态', combat_lines)
                self._draw_info_section(right_rect, '武器状态', weapon_lines)
        else:
            self._draw_info_section(left_rect, '路径与子目标', self._build_navigation_lines(detail))
            self._draw_decision_section(right_rect, detail)

        buff_labels = detail.get('active_buff_labels', [])
        if buff_labels:
            buff_text = self.tiny_font.render('当前增益: ' + ' / '.join(buff_labels[:4]), True, self.colors['yellow'])
            self.screen.blit(buff_text, (panel_rect.x + 24, panel_rect.bottom - 22))

    def _format_energy_state(self, state):
        current_state = str((state or {}).get('state', 'inactive') or 'inactive')
        return {
            'inactive': '未激活',
            'activating': '激活中',
            'activated': '已生效',
        }.get(current_state, current_state)

    def _format_energy_window(self, state):
        window_type = str((state or {}).get('window_type', '') or '')
        if not window_type:
            return '无'
        return {
            'small': '小能量',
            'large': '大能量',
        }.get(window_type, window_type)

    def _draw_stat_bar(self, x, y, width, label, value, maximum, color, value_text=None):
        bar_rect = pygame.Rect(x, y + 16, width, 12)
        ratio = 0.0
        if isinstance(maximum, (int, float)) and maximum > 1e-6:
            ratio = max(0.0, min(1.0, float(value) / float(maximum)))
        label_text = self.small_font.render(label, True, self.colors['white'])
        if value_text is None:
            if isinstance(maximum, (int, float)) and maximum > 1e-6 and maximum != 1.0:
                value_text = f"{float(value):.0f} / {float(maximum):.0f}"
            else:
                value_text = f"{ratio * 100:.0f}%"
        value_surface = self.tiny_font.render(value_text, True, self.colors['white'])
        self.screen.blit(label_text, (x, y))
        self.screen.blit(value_surface, (x + width - value_surface.get_width(), y + 1))
        pygame.draw.rect(self.screen, self.colors['toolbar_button'], bar_rect, border_radius=6)
        fill_rect = pygame.Rect(bar_rect.x, bar_rect.y, max(6, int(bar_rect.width * ratio)) if ratio > 0.0 else 0, bar_rect.height)
        if fill_rect.width > 0:
            pygame.draw.rect(self.screen, color, fill_rect, border_radius=6)
        pygame.draw.rect(self.screen, self.colors['panel_border'], bar_rect, 1, border_radius=6)

    def _draw_info_section(self, rect, title, lines):
        line_y, bottom_limit = self._draw_section_shell(rect, title)
        for line in lines:
            if line_y + 18 > bottom_limit:
                break
            text = self.tiny_font.render(line, True, self.colors['white'])
            self.screen.blit(text, (rect.x + 12, line_y))
            line_y += 18

    def _draw_decision_section(self, rect, detail):
        line_y, bottom_limit = self._draw_section_shell(rect, '决策预判与权重图')
        decision_lines = self._build_decision_lines(detail)
        for line in decision_lines:
            if line_y + 18 > rect.y + 132:
                break
            text = self.tiny_font.render(line, True, self.colors['white'])
            self.screen.blit(text, (rect.x + 12, line_y))
            line_y += 18

        graph_rect = pygame.Rect(rect.x + 10, rect.y + 138, rect.width - 20, max(60, bottom_limit - (rect.y + 138)))
        self._draw_decision_polygon(graph_rect, detail)

    def _draw_decision_polygon(self, rect, detail):
        entries = [item for item in detail.get('decision_weights', []) if isinstance(item, dict)]
        if not entries:
            text = self.tiny_font.render('暂无决策权重数据', True, self.colors['gray'])
            self.screen.blit(text, text.get_rect(center=rect.center))
            return

        center = (rect.centerx, rect.centery + 8)
        radius = max(28.0, min(rect.width, rect.height) * 0.28)
        sides = max(3, len(entries))
        base_points = []
        value_points = []
        selected_id = str(detail.get('decision_selected_id') or '')

        for index, entry in enumerate(entries):
            angle = -math.pi / 2 + (math.tau * index / sides)
            outer_x = center[0] + math.cos(angle) * radius
            outer_y = center[1] + math.sin(angle) * radius
            base_points.append((outer_x, outer_y))
            weight = max(0.0, min(1.0, float(entry.get('weight', 0.0))))
            value_x = center[0] + math.cos(angle) * radius * weight
            value_y = center[1] + math.sin(angle) * radius * weight
            value_points.append((value_x, value_y))

        for ring_ratio in (0.25, 0.5, 0.75, 1.0):
            ring_points = []
            for outer_x, outer_y in base_points:
                ring_points.append((
                    center[0] + (outer_x - center[0]) * ring_ratio,
                    center[1] + (outer_y - center[1]) * ring_ratio,
                ))
            if len(ring_points) >= 3:
                pygame.draw.polygon(self.screen, self.colors['panel_border'], ring_points, 1)

        if len(base_points) >= 3:
            pygame.draw.polygon(self.screen, self.colors['panel_border'], base_points, 1)
        if len(value_points) >= 3:
            pygame.draw.polygon(self.screen, (82, 180, 220), value_points)
            pygame.draw.polygon(self.screen, self.colors['white'], value_points, 2)

        for index, entry in enumerate(entries):
            outer_x, outer_y = base_points[index]
            value_x, value_y = value_points[index]
            pygame.draw.line(self.screen, self.colors['panel_border'], center, (outer_x, outer_y), 1)
            active = entry.get('id') == selected_id
            point_color = self.colors['yellow'] if active else (self.colors['green'] if entry.get('matched') else self.colors['gray'])
            pygame.draw.circle(self.screen, point_color, (int(value_x), int(value_y)), 4 if active else 3)
            label = str(entry.get('label') or entry.get('id') or '')
            if len(label) > 5:
                label = label[:5]
            label_surface = self.tiny_font.render(label, True, point_color)
            label_x = center[0] + (outer_x - center[0]) * 1.18 - label_surface.get_width() / 2
            label_y = center[1] + (outer_y - center[1]) * 1.18 - label_surface.get_height() / 2
            self.screen.blit(label_surface, (label_x, label_y))

        legend = self.tiny_font.render('黄=当前执行  绿=条件命中  灰=未命中', True, self.colors['white'])
        self.screen.blit(legend, (rect.x + 8, rect.bottom - 18))

    def _draw_section_shell(self, rect, title):
        pygame.draw.rect(self.screen, self.colors['toolbar_button'], rect, border_radius=12)
        pygame.draw.rect(self.screen, self.colors['panel_border'], rect, 1, border_radius=12)
        title_surface = self.small_font.render(title, True, self.colors['white'])
        self.screen.blit(title_surface, (rect.x + 12, rect.y + 10))
        return rect.y + 38, rect.bottom - 10

    def _draw_detail_tab(self, rect, label, active):
        fill_color = self.colors['toolbar_button_active'] if active else self.colors['toolbar_button']
        border_color = self.colors['white'] if active else self.colors['panel_border']
        pygame.draw.rect(self.screen, fill_color, rect, border_radius=9)
        pygame.draw.rect(self.screen, border_color, rect, 1, border_radius=9)
        text = self.tiny_font.render(label, True, self.colors['white'])
        self.screen.blit(text, text.get_rect(center=rect.center))

    def _build_navigation_lines(self, detail):
        path_preview = [point for point in detail.get('navigation_path_preview', []) if point is not None]
        subgoals = [point for point in detail.get('navigation_subgoals', []) if point is not None]
        final_target = detail.get('navigation_target')
        waypoint = detail.get('navigation_waypoint')
        movement_target = detail.get('movement_target')
        lines = [
            f"路径状态: {'有效' if detail.get('navigation_path_valid') else '待重规划'}",
            f"最终目标: {self._format_point(final_target)}",
            f"当前移动目标: {self._format_point(movement_target)}",
            f"当前小目标点: {self._format_point(waypoint)}",
            f"预览路径点数: {len(path_preview)}",
            f"剩余分段小目标: {len(subgoals)}",
        ]
        for index, point in enumerate(subgoals[:3], start=1):
            lines.append(f"下一跳 {index}: {self._format_point(point)}")
        if not subgoals and path_preview:
            for index, point in enumerate(path_preview[1:4], start=1):
                lines.append(f"路径预览 {index}: {self._format_point(point)}")
        if not path_preview and final_target is None:
            lines.append('当前无导航任务')
        return lines

    def _build_decision_lines(self, detail):
        selected_id = str(detail.get('decision_selected_id') or '')
        top3 = [item for item in detail.get('decision_top3', []) if isinstance(item, dict)]
        summary = str(detail.get('decision_summary') or '待机')
        lines = [f"当前行为: {summary or '待机'}"]
        if not top3:
            lines.append('未来三步: 暂无评估结果')
            return lines
        for index, item in enumerate(top3, start=1):
            status = '执行中' if item.get('id') == selected_id else ('命中' if item.get('matched') else '候选')
            lines.append(f"{index}. {item.get('label', item.get('id', '未知'))} {float(item.get('weight', 0.0)) * 100:.0f}% {status}")
        return lines

    def _format_point(self, point):
        if point is None:
            return '无'
        return f"({int(point[0])}, {int(point[1])})"

    def _format_chassis_state(self, state):
        labels = {
            'normal': '常规',
            'spin': '小陀螺',
            'fast_spin': '高速小陀螺',
            'follow_turret': '跟随云台',
            'power_off': '断电部署',
        }
        return labels.get(state, state)

    def _format_hero_deployment_state(self, state):
        labels = {
            'inactive': '未部署',
            'deploying': '部署中',
            'deployed': '已部署',
        }
        return labels.get(state, state)

    def _format_heat_lock_state(self, state):
        labels = {
            'normal': '正常',
            'cooling_unlock': '冷却解锁',
            'match_locked': '本局锁死',
        }
        return labels.get(state, state)