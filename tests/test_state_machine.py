import json
from collections.abc import Iterator

from qnyh_tool.checkpoint import CheckpointStore
from qnyh_tool.config import SafetyMode
from qnyh_tool.executor import Action, ClientState, DryRunPort, StateMachine, Step, build_port
from qnyh_tool.observability import StructuredLogger


def make_step(**kwargs):
    values = {"name": "open-quest", "action": Action("open_quest", {"quest_id": "daily-training"}), "precondition": lambda snapshot: snapshot.get("ready") is True, "verify": lambda snapshot: snapshot.get("opened") is True}
    values.update(kwargs)
    return Step(**values)


def provider(snapshots: list[dict[str, object]]):
    iterator: Iterator[dict[str, object]] = iter(snapshots)
    return lambda: next(iterator)


def test_dry_run_records_action_and_completes_after_verification():
    port = DryRunPort()
    result = StateMachine(port, provider([{"ready": True}, {"opened": True}])).run("client_1", (make_step(),))
    assert result.state is ClientState.COMPLETED
    assert result.completed_steps == ("open-quest",)
    assert result.actions[0].simulated is True
    assert port.actions[0].action.name == "open_quest"


def test_observation_mode_blocks_actions_and_safe_stops():
    result = StateMachine(build_port(SafetyMode.OBSERVATION), provider([{"ready": True}])).run("client_1", (make_step(),))
    assert result.state is ClientState.SAFE_STOP
    assert result.failed_step == "open-quest"
    assert result.reason is not None
    assert "blocks action" in result.reason
    assert result.actions == ()


def test_failed_precondition_safe_stops_before_action():
    port = DryRunPort()
    result = StateMachine(port, provider([{"ready": False}])).run("client_1", (make_step(),))
    assert result.state is ClientState.SAFE_STOP
    assert result.reason == "precondition failed"
    assert port.actions == []


def test_verification_failure_retries_with_new_snapshot():
    port = DryRunPort()
    result = StateMachine(port, provider([{"ready": True}, {"opened": False}, {"ready": True}, {"opened": True}])).run("client_1", (make_step(max_retries=1),))
    assert result.state is ClientState.COMPLETED
    assert result.attempts == 2
    assert len(result.actions) == 2


def test_snapshot_failure_safe_stops():
    result = StateMachine(build_port(SafetyMode.DRY_RUN), lambda: (_ for _ in ()).throw(RuntimeError("lost window"))).run("client_1", (make_step(),))
    assert result.state is ClientState.SAFE_STOP
    assert result.failed_step == "initial"
    assert result.reason == "snapshot error: lost window"


def test_timeout_safe_stops_after_action_exceeds_deadline():
    port = DryRunPort()
    clock_values = iter([0.0, 0.0, 2.0])
    result = StateMachine(port, provider([{"ready": True}, {"opened": True}]), clock=lambda: next(clock_values)).run("client_1", (make_step(timeout_seconds=1.0),))
    assert result.state is ClientState.SAFE_STOP
    assert result.reason == "step timeout"


def test_state_machine_writes_events_and_checkpoints(tmp_path):
    log_path = tmp_path / "events.jsonl"
    with CheckpointStore(tmp_path / "checkpoints.db") as checkpoints:
        result = StateMachine(DryRunPort(), provider([{"ready": True}, {"opened": True}]), logger=StructuredLogger(log_path), checkpoints=checkpoints, session_id="session-1").run("client_1", (make_step(),))
        latest = checkpoints.latest("client_1", "session-1")
    events = [json.loads(line) for line in log_path.read_text(encoding="utf-8").splitlines()]
    assert result.state is ClientState.COMPLETED
    assert latest is not None
    assert latest.state == "completed"
    assert [event["event"] for event in events] == ["state_machine_started", "action_requested", "step_completed", "state_machine_completed"]
