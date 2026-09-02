import json

import pytest

from qnyh_tool.checkpoint import CheckpointStore
from qnyh_tool.observability import StructuredLogger
from qnyh_tool.party import PartyCoordinator, PartyError, PartyMember, PartyRole, PartyState

MEMBERS = (PartyMember("leader-1", PartyRole.LEADER), PartyMember("follower-1", PartyRole.FOLLOWER))


def make_party(**kwargs):
    return PartyCoordinator(MEMBERS, **kwargs)


def ready_party(party):
    party.start_ready_check()
    party.mark_ready("leader-1", True)
    return party.mark_ready("follower-1", True)


def test_party_requires_exactly_one_leader():
    with pytest.raises(PartyError, match="exactly one leader"):
        PartyCoordinator((PartyMember("client-1", PartyRole.FOLLOWER),))


def test_ready_check_enters_running_only_after_all_members_are_ready():
    party = make_party()
    assert party.start_ready_check().state is PartyState.READY_CHECK
    assert party.mark_ready("leader-1", True).state is PartyState.READY_CHECK
    snapshot = party.mark_ready("follower-1", True)
    assert snapshot.state is PartyState.RUNNING
    assert snapshot.ready_members == ("leader-1", "follower-1")


def test_not_ready_member_stops_the_whole_party():
    party = make_party()
    party.start_ready_check()
    snapshot = party.mark_ready("follower-1", False)
    assert snapshot.state is PartyState.SAFE_STOP
    assert snapshot.reason == "member follower-1 is not ready"
    assert snapshot.stop_generation == 1


def test_failure_of_one_member_is_group_wide_and_requires_resume():
    party = make_party()
    ready_party(party)
    stopped = party.record_failure("leader-1", "unknown UI state")
    assert stopped.state is PartyState.SAFE_STOP
    assert stopped.last_stop_reason == "member leader-1: unknown UI state"
    resumed = party.resume()
    assert resumed.state is PartyState.READY_CHECK
    assert resumed.ready_members == ()
    assert resumed.last_stop_reason == "member leader-1: unknown UI state"


def test_stale_heartbeat_stops_party():
    party = make_party(heartbeat_timeout_seconds=5.0, clock=lambda: 0.0)
    ready_party(party)
    snapshot = party.check_heartbeats(now=5.1)
    assert snapshot.state is PartyState.SAFE_STOP
    assert snapshot.reason == "heartbeat timeout for member leader-1"


def test_heartbeat_keeps_member_alive_and_complete_is_explicit():
    party = make_party(heartbeat_timeout_seconds=5.0, clock=lambda: 0.0)
    ready_party(party)
    party.record_heartbeat("leader-1", at=4.0)
    party.record_heartbeat("follower-1", at=4.0)
    assert party.check_heartbeats(now=8.0).state is PartyState.RUNNING
    assert party.complete().state is PartyState.COMPLETED


def test_unknown_member_and_invalid_failure_reason_are_rejected():
    party = make_party()
    with pytest.raises(PartyError, match="unknown party member"):
        party.record_heartbeat("missing", at=0)
    with pytest.raises(PartyError, match="failure reason"):
        party.record_failure("leader-1", " ")


def test_party_failure_writes_event_and_checkpoint(tmp_path):
    log_path = tmp_path / "events.jsonl"
    with CheckpointStore(tmp_path / "checkpoints.db") as checkpoints:
        party = PartyCoordinator(MEMBERS, logger=StructuredLogger(log_path), checkpoints=checkpoints, session_id="session-1")
        party.start_ready_check()
        party.record_failure("follower-1", "lost window")
        latest = checkpoints.latest("party", "session-1")
    events = [json.loads(line) for line in log_path.read_text(encoding="utf-8").splitlines()]
    assert latest is not None
    assert latest.state == "safe_stop"
    assert events[-1]["event"] == "party_safe_stop"
