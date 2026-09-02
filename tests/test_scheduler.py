from datetime import datetime
from zoneinfo import ZoneInfo

from qnyh_tool.quests import load_catalog
from qnyh_tool.scheduler import CompletionRecord, eligible_quests, next_quest

LOCAL = ZoneInfo("Asia/Ho_Chi_Minh")


def catalog():
    return load_catalog("tasks/catalog.json")


def test_priority_wins_when_windows_overlap():
    quests = eligible_quests(catalog(), datetime(2026, 9, 1, 13, 0, tzinfo=LOCAL))
    assert [quest.quest_id for quest in quests] == ["weekly-party", "daily-training"]
    result = next_quest(catalog(), datetime(2026, 9, 1, 13, 0, tzinfo=LOCAL))
    assert result is not None
    assert result.quest_id == "weekly-party"


def test_completed_daily_quest_is_suppressed_for_local_day():
    current = datetime(2026, 9, 1, 13, 0, tzinfo=LOCAL)
    history = [CompletionRecord("daily-training", datetime(2026, 9, 1, 8, 0, tzinfo=LOCAL))]
    result = next_quest(catalog(), current, history)
    assert result is not None
    assert result.quest_id == "weekly-party"


def test_weekly_completion_is_suppressed_across_same_iso_week():
    current = datetime(2026, 9, 3, 13, 0, tzinfo=LOCAL)
    history = [CompletionRecord("weekly-party", datetime(2026, 9, 1, 13, 0, tzinfo=LOCAL)), CompletionRecord("daily-training", datetime(2026, 9, 3, 8, 0, tzinfo=LOCAL))]
    assert next_quest(catalog(), current, history) is None


def test_overnight_window_is_open_before_expiry():
    current = datetime(2026, 9, 1, 0, 10, tzinfo=LOCAL)
    result = next_quest(catalog(), current)
    assert result is not None
    assert result.quest_id == "weekly-party"
