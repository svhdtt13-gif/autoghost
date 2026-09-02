"""Timezone-aware quest scheduling without game or UI dependencies."""

from __future__ import annotations

from collections.abc import Iterable
from dataclasses import dataclass
from datetime import datetime, time
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from ..quests.catalog import Cadence, QuestCatalog, QuestDefinition


class ScheduleError(ValueError):
    """Raised when a catalog timezone or schedule input is invalid."""


@dataclass(frozen=True, slots=True)
class CompletionRecord:
    quest_id: str
    completed_at: datetime


def eligible_quests(catalog: QuestCatalog, now: datetime | None = None, history: Iterable[CompletionRecord] = ()) -> tuple[QuestDefinition, ...]:
    timezone = _get_timezone(catalog.timezone)
    local_now = _as_local(now or datetime.now(timezone), timezone)
    records = tuple(history)
    candidates = [quest for quest in catalog.quests if _in_window(local_now.time(), quest.opens_at, quest.expires_at) and not _completed_this_period(quest, local_now, records, timezone)]
    return tuple(sorted(candidates, key=lambda quest: (-quest.priority, quest.quest_id)))


def next_quest(catalog: QuestCatalog, now: datetime | None = None, history: Iterable[CompletionRecord] = ()) -> QuestDefinition | None:
    quests = eligible_quests(catalog, now, history)
    return quests[0] if quests else None


def _get_timezone(name: str) -> ZoneInfo:
    try:
        return ZoneInfo(name)
    except (ZoneInfoNotFoundError, ValueError) as exc:
        raise ScheduleError(f"unknown schedule timezone: {name}") from exc


def _as_local(value: datetime, timezone: ZoneInfo) -> datetime:
    if value.tzinfo is None:
        return value.replace(tzinfo=timezone)
    return value.astimezone(timezone)


def _in_window(current: time, opens_at: time, expires_at: time) -> bool:
    if opens_at < expires_at:
        return opens_at <= current < expires_at
    return current >= opens_at or current < expires_at


def _completed_this_period(quest: QuestDefinition, now: datetime, history: tuple[CompletionRecord, ...], timezone: ZoneInfo) -> bool:
    for record in history:
        if record.quest_id != quest.quest_id:
            continue
        completed_at = _as_local(record.completed_at, timezone)
        if quest.cadence is Cadence.DAILY and completed_at.date() == now.date():
            return True
        if quest.cadence is Cadence.WEEKLY and completed_at.isocalendar()[:2] == now.isocalendar()[:2]:
            return True
    return False
