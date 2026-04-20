#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import math


class PhysicsEngine:
    def __init__(self, config):
        self.config = config or {}
        physics_cfg = self.config.get('physics', {}) if isinstance(self.config, dict) else {}
        self.max_speed_mps = float(physics_cfg.get('max_speed', 3.5) or 3.5)

    def shutdown(self):
        return None

    def _meters_to_world_units(self, map_manager, value_m):
        if map_manager is not None and hasattr(map_manager, 'meters_to_world_units'):
            try:
                return float(map_manager.meters_to_world_units(float(value_m)))
            except Exception:
                pass
        return float(value_m)

    def _max_speed_world_units(self, map_manager=None):
        return self._meters_to_world_units(map_manager, self.max_speed_mps)

    def _resolved_entity_speed_limit_mps(self, entity):
        explicit = float(getattr(entity, 'chassis_speed_limit_mps', 0.0) or 0.0)
        if explicit > 1e-6:
            return explicit
        scale = max(0.0, float(getattr(entity, 'chassis_speed_scale', 1.0) or 1.0))
        return self.max_speed_mps * scale

    def _is_position_valid(self, map_manager, x, y, entity):
        if map_manager is None:
            return True
        if hasattr(map_manager, 'is_position_valid_for_radius'):
            try:
                radius = float(getattr(entity, 'collision_radius', 0.0) or 0.0)
                return bool(map_manager.is_position_valid_for_radius(x, y, collision_radius=radius))
            except Exception:
                pass
        if hasattr(map_manager, 'is_position_valid'):
            try:
                return bool(map_manager.is_position_valid(x, y))
            except Exception:
                pass
        return True

    def _clamp_to_map(self, map_manager, x, y):
        if map_manager is None:
            return x, y
        width = float(getattr(map_manager, 'map_width', x + 1.0))
        height = float(getattr(map_manager, 'map_height', y + 1.0))
        return max(0.0, min(width - 1.0, x)), max(0.0, min(height - 1.0, y))

    def update(self, entities, map_manager, rules_engine, dt=0.02):
        if dt <= 0:
            return

        for entity in entities:
            if not bool(getattr(entity, 'movable', False)):
                continue

            velocity = getattr(entity, 'velocity', None) or {}
            vx = float(velocity.get('vx', 0.0) or 0.0)
            vy = float(velocity.get('vy', 0.0) or 0.0)
            vz = float(velocity.get('vz', 0.0) or 0.0)

            speed_cap_mps = max(0.0, self._resolved_entity_speed_limit_mps(entity))
            speed_cap_world = self._meters_to_world_units(map_manager, speed_cap_mps)
            speed = math.hypot(vx, vy)
            if speed_cap_world > 1e-6 and speed > speed_cap_world:
                scale = speed_cap_world / max(speed, 1e-6)
                vx *= scale
                vy *= scale
                entity.velocity = {'vx': vx, 'vy': vy, 'vz': vz}

            x = float(getattr(entity, 'position', {}).get('x', 0.0))
            y = float(getattr(entity, 'position', {}).get('y', 0.0))
            z = float(getattr(entity, 'position', {}).get('z', 0.0))
            x, y = self._clamp_to_map(map_manager, x, y)

            if not self._is_position_valid(map_manager, x, y, entity):
                fallback = getattr(entity, 'last_valid_position', None) or getattr(entity, 'previous_position', None)
                if isinstance(fallback, dict):
                    x = float(fallback.get('x', x))
                    y = float(fallback.get('y', y))
                    z = float(fallback.get('z', z))
                entity.velocity = {'vx': 0.0, 'vy': 0.0, 'vz': 0.0}
            else:
                entity.last_valid_position = {'x': x, 'y': y, 'z': z}

            if map_manager is not None and hasattr(map_manager, 'get_terrain_height_m'):
                try:
                    terrain_h = float(map_manager.get_terrain_height_m(x, y))
                    if z < terrain_h:
                        z = terrain_h
                except Exception:
                    pass

            entity.position['x'] = x
            entity.position['y'] = y
            entity.position['z'] = z
