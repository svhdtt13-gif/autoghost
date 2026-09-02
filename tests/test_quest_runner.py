from qnyh_tool.executor import ClientState, DryRunQuestRunner
from qnyh_tool.quests import load_catalog


def daily_quest():
    return load_catalog("tasks/catalog.json").quests[0]


def test_dry_run_quest_runner_simulates_full_plan_without_mutating_input():
    snapshot = {"recognized_quest_id": "daily-training", "recognition_confidence": 0.95, "completion_signal": "reward-claimed"}
    result = DryRunQuestRunner(session_id="session-1").run("client_1", daily_quest(), snapshot)
    assert result.state is ClientState.COMPLETED
    assert result.completed_steps == ("select-quest", "travel-to-destination", "claim-reward")
    assert [action.action.name for action in result.actions] == ["select_quest", "travel", "claim_reward"]
    assert snapshot == {"recognized_quest_id": "daily-training", "recognition_confidence": 0.95, "completion_signal": "reward-claimed"}


def test_low_recognition_confidence_safe_stops_before_action():
    result = DryRunQuestRunner(recognition_threshold=0.8).run("client_1", daily_quest(), {"recognized_quest_id": "daily-training", "recognition_confidence": 0.79, "completion_signal": "reward-claimed"})
    assert result.state is ClientState.SAFE_STOP
    assert result.failed_step == "select-quest"
    assert result.actions == ()


def test_missing_completion_signal_stops_after_safe_partial_plan():
    result = DryRunQuestRunner().run("client_1", daily_quest(), {"recognized_quest_id": "daily-training", "recognition_confidence": 1.0})
    assert result.state is ClientState.SAFE_STOP
    assert result.completed_steps == ("select-quest", "travel-to-destination")
    assert result.failed_step == "claim-reward"
    assert result.reason == "precondition failed"
