"""SQLite checkpoint persistence with redacted JSON snapshots."""

from __future__ import annotations

import json
import re
import sqlite3
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from types import TracebackType
from typing import Any, Self
from uuid import uuid4

_SECRET_KEY = re.compile(r"(?:token|secret|password|passwd|cookie|bearer|session|api[_-]?key|authorization)", re.IGNORECASE)
_REDACTED = "[REDACTED]"


@dataclass(frozen=True, slots=True)
class Checkpoint:
    checkpoint_id: str
    client_id: str
    session_id: str
    state: str
    step_name: str
    status: str
    snapshot: Mapping[str, Any]
    reason: str | None
    created_at: str


class CheckpointStore:
    """Persist the latest and historical client/party execution checkpoints."""

    def __init__(self, path: str | Path) -> None:
        self._path = Path(path)
        self._path.parent.mkdir(parents=True, exist_ok=True)
        self._connection = sqlite3.connect(self._path)
        self._connection.row_factory = sqlite3.Row
        self._connection.execute("""CREATE TABLE IF NOT EXISTS checkpoints (checkpoint_id TEXT PRIMARY KEY, client_id TEXT NOT NULL, session_id TEXT NOT NULL, state TEXT NOT NULL, step_name TEXT NOT NULL, status TEXT NOT NULL, snapshot_json TEXT NOT NULL, reason TEXT, created_at TEXT NOT NULL)""")
        self._connection.commit()

    def save(self, *, client_id: str, session_id: str, state: str, step_name: str, status: str, snapshot: Mapping[str, Any], reason: str | None = None) -> Checkpoint:
        if not client_id.strip() or not session_id.strip():
            raise ValueError("client_id and session_id must not be empty")
        if not state.strip() or not step_name.strip() or not status.strip():
            raise ValueError("state, step_name and status must not be empty")
        if not isinstance(snapshot, Mapping):
            raise TypeError("snapshot must be a mapping")
        checkpoint = Checkpoint(str(uuid4()), client_id, session_id, state, step_name, status, _redact(snapshot), _redact(reason) if reason is not None else None, datetime.now(UTC).isoformat())
        self._connection.execute("INSERT INTO checkpoints (checkpoint_id, client_id, session_id, state, step_name, status, snapshot_json, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)", (checkpoint.checkpoint_id, checkpoint.client_id, checkpoint.session_id, checkpoint.state, checkpoint.step_name, checkpoint.status, json.dumps(checkpoint.snapshot, ensure_ascii=False, sort_keys=True), checkpoint.reason, checkpoint.created_at))
        self._connection.commit()
        return checkpoint

    def latest(self, client_id: str, session_id: str | None = None) -> Checkpoint | None:
        query = "SELECT * FROM checkpoints WHERE client_id = ?"
        parameters: list[str] = [client_id]
        if session_id is not None:
            query += " AND session_id = ?"
            parameters.append(session_id)
        row = self._connection.execute(query + " ORDER BY created_at DESC, rowid DESC LIMIT 1", parameters).fetchone()
        return _from_row(row) if row is not None else None

    def recent(self, limit: int = 50) -> tuple[Checkpoint, ...]:
        if limit <= 0:
            raise ValueError("checkpoint limit must be greater than zero")
        rows = self._connection.execute("SELECT * FROM checkpoints ORDER BY created_at DESC, rowid DESC LIMIT ?", (limit,)).fetchall()
        return tuple(_from_row(row) for row in rows)

    def close(self) -> None:
        self._connection.close()

    def __enter__(self) -> Self:
        return self

    def __exit__(self, _exc_type: type[BaseException] | None, _exc: BaseException | None, _traceback: TracebackType | None) -> None:
        self.close()


def _from_row(row: sqlite3.Row) -> Checkpoint:
    return Checkpoint(row["checkpoint_id"], row["client_id"], row["session_id"], row["state"], row["step_name"], row["status"], json.loads(row["snapshot_json"]), row["reason"], row["created_at"])


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
        return re.sub(r"(?i)(?:bearer\s+|(?:token|session|password|cookie)\s*[=:]\s*)[^\s,;]+", _REDACTED, value)
    return value
