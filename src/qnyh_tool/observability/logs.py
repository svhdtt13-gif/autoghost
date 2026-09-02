"""Append-only JSONL logging with defensive credential redaction."""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from datetime import UTC, datetime
from pathlib import Path
from threading import Lock
from typing import Any

_SECRET_KEY = re.compile(r"(?:token|secret|password|passwd|cookie|bearer|session|api[_-]?key|authorization)", re.IGNORECASE)
_SECRET_VALUE = re.compile(r"(?i)(?:bearer\s+|(?:token|session|password|cookie)\s*[=:]\s*)[^\s,;]+")
_REDACTED = "[REDACTED]"


class StructuredLogger:
    """Write structured events without persisting credential-like values."""

    def __init__(self, path: str | Path, context: Mapping[str, Any] | None = None) -> None:
        self._path = Path(path)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._context = _redact(dict(context or {}))
        self._lock = Lock()

    def write(self, event: str, **fields: Any) -> None:
        if not event.strip():
            raise ValueError("event name must not be empty")
        payload = {"timestamp": datetime.now(UTC).isoformat(), "event": event, **self._context, **_redact(fields)}
        line = json.dumps(payload, ensure_ascii=False, sort_keys=True)
        with self._lock, self._path.open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")


def redact(value: Any) -> Any:
    return _redact(value)


def _redact(value: Any, key: str | None = None) -> Any:
    if key is not None and _SECRET_KEY.search(key):
        return _REDACTED
    if isinstance(value, Mapping):
        return {str(child_key): _redact(child, str(child_key)) for child_key, child in value.items()}
    if isinstance(value, list):
        return [_redact(child) for child in value]
    if isinstance(value, tuple):
        return [_redact(child) for child in value]
    if isinstance(value, str):
        return _SECRET_VALUE.sub(_REDACTED, value)
    return value
