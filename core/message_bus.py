#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import heapq
import queue
import threading
import time
from collections import deque
from copy import deepcopy


class MessageBus:
    def __init__(self, max_ready_messages=512):
        self._incoming = queue.Queue()
        self._ready = deque(maxlen=max(32, int(max_ready_messages)))
        self._scheduled = []
        self._sequence = 0
        self._lock = threading.Lock()
        self._stop_event = threading.Event()
        self._thread = threading.Thread(target=self._worker_loop, name='game-message-bus', daemon=True)
        self._thread.start()

    def publish(self, topic, payload=None, delay_sec=0.0):
        message = {
            'topic': str(topic or ''),
            'payload': deepcopy(payload) if payload is not None else None,
            'delay_sec': max(0.0, float(delay_sec)),
            'created_at': time.perf_counter(),
        }
        if float(message['delay_sec']) <= 1e-9:
            with self._lock:
                self._ready.append(message)
            return message
        self._incoming.put(message)
        return message

    def poll(self, limit=128):
        messages = []
        with self._lock:
            while self._ready and len(messages) < max(1, int(limit)):
                messages.append(self._ready.popleft())
        return messages

    def shutdown(self):
        self._stop_event.set()
        self._incoming.put(None)
        if self._thread.is_alive():
            self._thread.join(timeout=1.0)

    def _worker_loop(self):
        while not self._stop_event.is_set():
            now = time.perf_counter()
            timeout = 0.02
            if self._scheduled:
                timeout = max(0.0, min(timeout, self._scheduled[0][0] - now))
            try:
                incoming = self._incoming.get(timeout=timeout)
            except queue.Empty:
                incoming = None
            if incoming is None:
                self._flush_due_messages(time.perf_counter())
                continue
            deliver_at = float(incoming.get('created_at', time.perf_counter())) + float(incoming.get('delay_sec', 0.0))
            self._sequence += 1
            heapq.heappush(self._scheduled, (deliver_at, self._sequence, incoming))
            self._flush_due_messages(time.perf_counter())

    def _flush_due_messages(self, now):
        due_messages = []
        while self._scheduled and self._scheduled[0][0] <= now:
            _, _, message = heapq.heappop(self._scheduled)
            due_messages.append(message)
        if not due_messages:
            return
        with self._lock:
            for message in due_messages:
                self._ready.append(message)