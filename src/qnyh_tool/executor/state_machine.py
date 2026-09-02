"""A UI-port-agnostic state machine with explicit safety boundaries."""

from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from enum import StrEnum
from time import monotonic
from typing import Any, Protocol

from ..checkpoint import CheckpointStore
from ..config import SafetyMode
from ..observability import StructuredLogger


class SafetyViolation(RuntimeError):
    """Raised when an action is attempted in a mode that forbids it."""


class ClientState(StrEnum):
    IDLE = "idle"
    RUNNING = "running"
    COMPLETED = "completed"
    SAFE_STOP = "safe_stop"


@dataclass(frozen=True, slots=True)
class Action:
    name: str
    parameters: Mapping[str, Any] = field(default_factory=dict)


@dataclass(frozen=True, slots=True)
class Step:
    name: str
    action: Action
    precondition: Callable[[Mapping[str, Any]], bool]
    verify: Callable[[Mapping[str, Any]], bool]
    timeout_seconds: float = 10.0
    max_retries: int = 0

    def __post_init__(self) -> None:
        if not self.name.strip():
            raise ValueError("step name must not be empty")
        if self.timeout_seconds <= 0:
            raise ValueError("step timeout must be greater than zero")
        if self.max_retries < 0:
            raise ValueError("step max_retries must not be negative")


class ActionPort(Protocol):
    def perform(self, client_id: str, action: Action) -> None:
        """Perform or simulate one semantic action."""


@dataclass(frozen=True, slots=True)
class RecordedAction:
    client_id: str
    action: Action
    simulated: bool


@dataclass(frozen=True, slots=True)
class ExecutionResult:
    state: ClientState
    completed_steps: tuple[str, ...]
    failed_step: str | None
    attempts: int
    reason: str | None
    actions: tuple[RecordedAction, ...]


class DryRunPort:
    simulated = True

    def __init__(self, on_action: Callable[[Action], None] | None = None) -> None:
        self.actions: list[RecordedAction] = []
        self._on_action = on_action

    def perform(self, client_id: str, action: Action) -> None:
        self.actions.append(RecordedAction(client_id, action, simulated=True))
        if self._on_action is not None:
            self._on_action(action)


class ObservationPort:
    simulated = False

    def perform(self, client_id: str, action: Action) -> None:
        raise SafetyViolation(f"observation mode blocks action '{action.name}' for client {client_id}")


def build_port(mode: SafetyMode) -> ActionPort:
    if mode is SafetyMode.OBSERVATION:
        return ObservationPort()
    if mode is SafetyMode.DRY_RUN:
        return DryRunPort()
    raise SafetyViolation("active mode has no UI action adapter implemented")


class StateMachine:
    def __init__(self, port: ActionPort, snapshot_provider: Callable[[], Mapping[str, Any]], clock: Callable[[], float] = monotonic, logger: StructuredLogger | None = None, checkpoints: CheckpointStore | None = None, session_id: str = "default") -> None:
        self._port = port
        self._snapshot_provider = snapshot_provider
        self._clock = clock
        self._logger = logger
        self._checkpoints = checkpoints
        self._session_id = session_id
        self._active_client_id = ""
        self._active_step = "initial"
        self._active_snapshot: Mapping[str, Any] = {}

    def run(self, client_id: str, steps: tuple[Step, ...]) -> ExecutionResult:
        if not client_id.strip():
            raise ValueError("client_id must not be empty")
        if not steps:
            raise ValueError("steps must not be empty")
        completed: list[str] = []
        actions: list[RecordedAction] = []
        total_attempts = 0
        self._active_client_id = client_id
        self._active_step = "initial"
        self._active_snapshot = {}
        self._emit("state_machine_started", steps=[step.name for step in steps])
        try:
            snapshot = self._safe_snapshot()
            self._active_snapshot = snapshot
        except Exception as exc:
            return self._stop(completed, actions, total_attempts, "initial", f"snapshot error: {exc}")
        for step in steps:
            self._active_step = step.name
            started = self._clock()
            try:
                if not step.precondition(snapshot):
                    return self._stop(completed, actions, total_attempts, step.name, "precondition failed")
            except Exception as exc:
                return self._stop(completed, actions, total_attempts, step.name, f"precondition error: {exc}")
            verified = False
            for attempt in range(step.max_retries + 1):
                total_attempts += 1
                if self._expired(started, step.timeout_seconds):
                    return self._stop(completed, actions, total_attempts, step.name, "step timeout")
                try:
                    if attempt:
                        snapshot = self._safe_snapshot()
                        self._active_snapshot = snapshot
                        if not step.precondition(snapshot):
                            return self._stop(completed, actions, total_attempts, step.name, "retry precondition failed")
                    self._port.perform(client_id, step.action)
                    if isinstance(self._port, DryRunPort):
                        actions.append(self._port.actions[-1])
                    self._emit("action_requested", step=step.name, action=step.action.name, attempt=attempt + 1, simulated=isinstance(self._port, DryRunPort))
                    if self._expired(started, step.timeout_seconds):
                        return self._stop(completed, actions, total_attempts, step.name, "step timeout")
                    snapshot = self._safe_snapshot()
                    self._active_snapshot = snapshot
                    verified = bool(step.verify(snapshot))
                except SafetyViolation as exc:
                    return self._stop(completed, actions, total_attempts, step.name, str(exc))
                except Exception as exc:
                    return self._stop(completed, actions, total_attempts, step.name, f"step error: {exc}")
                if verified:
                    break
            if not verified:
                return self._stop(completed, actions, total_attempts, step.name, "post-action verification failed")
            completed.append(step.name)
            self._emit("step_completed", step=step.name, attempts=total_attempts)
            self._save_checkpoint(state=ClientState.RUNNING.value, status="step_completed", reason=None)
        result = ExecutionResult(ClientState.COMPLETED, tuple(completed), None, total_attempts, None, tuple(actions))
        self._emit("state_machine_completed", completed_steps=completed)
        self._save_checkpoint(state=ClientState.COMPLETED.value, status="completed", reason=None)
        return result

    def _safe_snapshot(self) -> Mapping[str, Any]:
        snapshot = self._snapshot_provider()
        if not isinstance(snapshot, Mapping):
            raise TypeError("snapshot provider must return a mapping")
        return snapshot

    def _expired(self, started: float, timeout_seconds: float) -> bool:
        return self._clock() - started >= timeout_seconds

    def _stop(self, completed: list[str], actions: list[RecordedAction], attempts: int, failed_step: str, reason: str) -> ExecutionResult:
        self._emit("safe_stop", failed_step=failed_step, reason=reason, attempts=attempts)
        self._save_checkpoint(state=ClientState.SAFE_STOP.value, status="safe_stop", reason=reason)
        return ExecutionResult(ClientState.SAFE_STOP, tuple(completed), failed_step, attempts, reason, tuple(actions))

    def _emit(self, event: str, **fields: Any) -> None:
        if self._logger is None:
            return
        try:
            self._logger.write(event, client_id=self._active_client_id, session_id=self._session_id, **fields)
        except Exception:
            return

    def _save_checkpoint(self, *, state: str, status: str, reason: str | None) -> None:
        if self._checkpoints is None:
            return
        try:
            self._checkpoints.save(client_id=self._active_client_id, session_id=self._session_id, state=state, step_name=self._active_step, status=status, snapshot=self._active_snapshot, reason=reason)
        except Exception:
            return
