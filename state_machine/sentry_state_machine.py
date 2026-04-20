#!/usr/bin/env python3
# -*- coding: utf-8 -*-


class SentryStateMachine:
    def update(self, entity):
        if entity is None:
            return
        if not bool(getattr(entity, 'is_alive', lambda: False)()):
            entity.state = 'destroyed'
            entity.fire_control_state = 'idle'
            return
        if getattr(entity, 'target', None):
            entity.state = 'engage'
            entity.fire_control_state = 'fire'
        else:
            if str(getattr(entity, 'state', 'idle')) in {'idle', 'search', 'engage'}:
                entity.state = 'search'
            entity.fire_control_state = 'idle'
