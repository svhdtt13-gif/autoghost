import json

from qnyh_tool.checkpoint import CheckpointStore
from qnyh_tool.observability import StructuredLogger


def test_structured_logger_redacts_secret_keys_and_values(tmp_path):
    path = tmp_path / "logs" / "events.jsonl"
    logger = StructuredLogger(path, {"session_id": "safe-session"})
    logger.write("test_event", authorization="test-credential-value", nested={"cookie": "session=hidden-value"}, message="Cookie=another-hidden-value")
    line = path.read_text(encoding="utf-8")
    payload = json.loads(line)
    assert "test-credential-value" not in line
    assert "hidden-value" not in line
    assert payload["authorization"] == "[REDACTED]"
    assert payload["nested"]["cookie"] == "[REDACTED]"


def test_checkpoint_store_round_trips_latest_redacted_snapshot(tmp_path):
    with CheckpointStore(tmp_path / "state" / "checkpoints.db") as store:
        saved = store.save(client_id="client_1", session_id="session_1", state="safe_stop", step_name="open-quest", status="failed", snapshot={"visible": True, "session": "secret"}, reason="Cookie=hidden")
        latest = store.latest("client_1", "session_1")
    assert latest is not None
    assert latest.checkpoint_id == saved.checkpoint_id
    assert latest.snapshot == {"visible": True, "session": "[REDACTED]"}
    assert latest.reason == "[REDACTED]"
