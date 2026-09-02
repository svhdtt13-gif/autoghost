"""State-only party coordinator with group-wide fail-stop behavior."""

from __future__ import annotations

from collections.abc import Callable, Iterable
from dataclasses import dataclass
from enum import StrEnum
from time import monotonic
from typing import Any

from ..checkpoint import CheckpointStore
from ..observability import StructuredLogger


class PartyError(ValueError):
    """Raised when a party definition or lifecycle transition is invalid."""


class PartyRole(StrEnum):
    LEADER = "leader"
    FOLLOWER = "follower"


class PartyState(StrEnum):
    FORMING = "forming"
    READY_CHECK = "ready_check"
    RUNNING = "running"
    SAFE_STOP = "safe_stop"
    COMPLETED = "completed"


@dataclass(frozen=True, slots=True)
class PartyMember:
    client_id: str
    role: PartyRole


@dataclass(frozen=True, slots=True)
class PartySnapshot:
    state: PartyState
    ready_members: tuple[str, ...]
    reason: str | None
    last_stop_reason: str | None
    stop_generation: int


class PartyCoordinator:
    """Coordinate lifecycle state without directly controlling any client."""

    def __init__(self, members: Iterable[PartyMember], heartbeat_timeout_seconds: float = 15.0, clock: Callable[[], float] = monotonic, logger: StructuredLogger | None = None, checkpoints: CheckpointStore | None = None, session_id: str = "default") -> None:
        self._members = tuple(members)
        self._validate_members(self._members)
        if heartbeat_timeout_seconds <= 0:
            raise PartyError("heartbeat timeout must be greater than zero")
        self._member_ids = tuple(member.client_id for member in self._members)
        self._timeout = heartbeat_timeout_seconds
        self._clock = clock
        now = self._clock()
        self._heartbeats = {client_id: now for client_id in self._member_ids}
        self._ready: set[str] = set()
        self._state = PartyState.FORMING
        self._reason: str | None = None
        self._last_stop_reason: str | None = None
        self._stop_generation = 0
        self._logger = logger
        self._checkpoints = checkpoints
        self._session_id = session_id

    @staticmethod
    def _validate_members(members: tuple[PartyMember, ...]) -> None:
        if not members:
            raise PartyError("party must contain at least one member")
        ids = [member.client_id for member in members]
        if any(not isinstance(member.role, PartyRole) for member in members):
            raise PartyError("party member role must be leader or follower")
        if any(not client_id.strip() for client_id in ids):
            raise PartyError("party member client_id must not be empty")
        if len(set(ids)) != len(ids):
            raise PartyError("party member client_id values must be unique")
        if sum(member.role is PartyRole.LEADER for member in members) != 1:
            raise PartyError("party must contain exactly one leader")

    @property
    def members(self) -> tuple[PartyMember, ...]:
        return self._members

    def snapshot(self) -> PartySnapshot:
        return PartySnapshot(self._state, tuple(client_id for client_id in self._member_ids if client_id in self._ready), self._reason, self._last_stop_reason, self._stop_generation)

    def start_ready_check(self) -> PartySnapshot:
        self._require_state(PartyState.FORMING)
        self._ready.clear()
        self._reason = None
        self._state = PartyState.READY_CHECK
        self._refresh_heartbeats()
        self._emit("party_ready_check_started")
        self._save_checkpoint(status="ready_check", reason=None)
        return self.snapshot()

    def mark_ready(self, client_id: str, ready: bool) -> PartySnapshot:
        self._require_member(client_id)
        self._require_state(PartyState.READY_CHECK)
        if not ready:
            return self._safe_stop(f"member {client_id} is not ready")
        self._ready.add(client_id)
        if self._ready == set(self._member_ids):
            self._state = PartyState.RUNNING
            self._emit("party_running")
        else:
            self._emit("member_ready", member_id=client_id)
        self._save_checkpoint(status=self._state.value, reason=self._reason)
        return self.snapshot()

    def record_heartbeat(self, client_id: str, at: float | None = None) -> PartySnapshot:
        self._require_member(client_id)
        if self._state in {PartyState.SAFE_STOP, PartyState.COMPLETED}:
            return self.snapshot()
        self._heartbeats[client_id] = self._clock() if at is None else at
        self._emit("heartbeat", member_id=client_id)
        return self.snapshot()

    def check_heartbeats(self, now: float | None = None) -> PartySnapshot:
        if self._state not in {PartyState.READY_CHECK, PartyState.RUNNING}:
            return self.snapshot()
        current = self._clock() if now is None else now
        for client_id in self._member_ids:
            if current - self._heartbeats[client_id] >= self._timeout:
                return self._safe_stop(f"heartbeat timeout for member {client_id}")
        return self.snapshot()

    def record_failure(self, client_id: str, reason: str) -> PartySnapshot:
        self._require_member(client_id)
        if not reason.strip():
            raise PartyError("failure reason must not be empty")
        return self._safe_stop(f"member {client_id}: {reason.strip()}")

    def complete(self) -> PartySnapshot:
        self._require_state(PartyState.RUNNING)
        self._state = PartyState.COMPLETED
        self._emit("party_completed")
        self._save_checkpoint(status="completed", reason=None)
        return self.snapshot()

    def resume(self) -> PartySnapshot:
        self._require_state(PartyState.SAFE_STOP)
        self._ready.clear()
        self._reason = None
        self._state = PartyState.READY_CHECK
        self._refresh_heartbeats()
        self._emit("party_resumed")
        self._save_checkpoint(status="ready_check", reason=None)
        return self.snapshot()

    def _safe_stop(self, reason: str) -> PartySnapshot:
        self._state = PartyState.SAFE_STOP
        self._reason = reason
        self._last_stop_reason = reason
        self._stop_generation += 1
        self._emit("party_safe_stop", reason=reason)
        self._save_checkpoint(status="safe_stop", reason=reason)
        return self.snapshot()

    def _refresh_heartbeats(self) -> None:
        now = self._clock()
        self._heartbeats = {client_id: now for client_id in self._member_ids}

    def _require_member(self, client_id: str) -> None:
        if client_id not in self._member_ids:
            raise PartyError(f"unknown party member: {client_id}")

    def _require_state(self, expected: PartyState) -> None:
        if self._state is not expected:
            raise PartyError(f"invalid party transition from {self._state.value}; expected {expected.value}")

    def _emit(self, event: str, **fields: Any) -> None:
        if self._logger is None:
            return
        try:
            self._logger.write(event, session_id=self._session_id, **fields)
        except Exception:
            return

    def _save_checkpoint(self, *, status: str, reason: str | None) -> None:
        if self._checkpoints is None:
            return
        try:
            self._checkpoints.save(client_id="party", session_id=self._session_id, state=self._state.value, step_name="party", status=status, snapshot={"members": [{"client_id": member.client_id, "role": member.role.value} for member in self._members], "ready_members": list(self.snapshot().ready_members), "stop_generation": self._stop_generation}, reason=reason)
        except Exception:
            return
