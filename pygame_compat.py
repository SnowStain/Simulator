#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
pygame兼容性模块
兼容pygame-ce和标准pygame
"""

import sys

try:
    import pygame_ce as _pygame
except ImportError:
    try:
        import pygame as _pygame
    except ImportError as exc:
        python_exe = sys.executable or 'python'
        raise ImportError(
            '当前解释器未安装 pygame 或 pygame-ce。\n'
            f'当前 Python: {python_exe}\n'
            '请使用同一个解释器安装依赖，不要使用裸 pip。\n'
            '推荐命令:\n'
            f'  "{python_exe}" -m pip install -U pip\n'
            f'  "{python_exe}" -m pip install -r requirements.txt'
        ) from exc

# 重新导出所有pygame模块
pygame = _pygame

# 确保所有常用的pygame常量和函数都可用
QUIT = pygame.QUIT
KEYDOWN = pygame.KEYDOWN
K_ESCAPE = pygame.K_ESCAPE
K_w = pygame.K_w
K_s = pygame.K_s
K_a = pygame.K_a
K_d = pygame.K_d
K_q = pygame.K_q
K_e = pygame.K_e
K_SPACE = pygame.K_SPACE
K_LSHIFT = pygame.K_LSHIFT
K_RSHIFT = pygame.K_RSHIFT
MOUSEBUTTONDOWN = pygame.MOUSEBUTTONDOWN
MOUSEBUTTONUP = pygame.MOUSEBUTTONUP

# 重新导出常用函数
init = pygame.init
quit = pygame.quit
display = pygame.display
draw = pygame.draw
font = pygame.font
event = pygame.event
key = pygame.key
mouse = pygame.mouse
image = pygame.image
transform = pygame.transform
Surface = pygame.Surface
Color = pygame.Color
Rect = pygame.Rect
