"""Validated quest catalog models independent of any game protocol."""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import time
from enum import StrEnum
from pathlib import Path
from typing import Any


class QuestError(ValueError):
    """Raised when a quest catalog is invalid."""


class Cadence(StrEnum):
    DAILY = "daily"
    WEEKLY = "weekly"


@dataclass(frozen=True, slots=True)
class QuestDefinition:
    quest_id: str
    aliases: tuple[str, ...]
    icon: str | None
    objective: str
    destinations: tuple[str, ...]
    completion_signals: tuple[str, ...]
    cadence: Cadence
    opens_at: time
    expires_at: time
    priority: int


@dataclass(frozen=True, slots=True)
class QuestCatalog:
    version: int
    timezone: str
    quests: tuple[QuestDefinition, ...]


_TIME_PATTERN = re.compile(r"^\d{2}:\d{2}$")


def load_catalog(path: str | Path) -> QuestCatalog:
    catalog_path = Path(path)
    try:
        raw = json.loads(catalog_path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise QuestError(f"quest catalog not found: {catalog_path}") from exc
    except json.JSONDecodeError as exc:
        raise QuestError(f"invalid JSON in quest catalog: {catalog_path}") from exc
    if not isinstance(raw, Mapping):
        raise QuestError("quest catalog root must be a JSON object")
    version = raw.get("version", 1)
    if version != 1 or isinstance(version, bool):
        raise QuestError("unsupported quest catalog version; expected 1")
    timezone = _text(raw.get("timezone", "Asia/Ho_Chi_Minh"), "timezone")
    entries = raw.get("quests")
    if not isinstance(entries, list) or not entries:
        raise QuestError("quests must be a non-empty JSON array")
    quests = tuple(_parse_quest(entry, index) for index, entry in enumerate(entries))
    ids = [quest.quest_id for quest in quests]
    if len(set(ids)) != len(ids):
        raise QuestError("quest_id values must be unique")
    return QuestCatalog(1, timezone, quests)


def _parse_quest(value: Any, index: int) -> QuestDefinition:
    location = f"quests[{index}]"
    if not isinstance(value, Mapping):
        raise QuestError(f"{location} must be a JSON object")
    quest_id = _text(value.get("questId"), f"{location}.questId")
    aliases = _text_list(value.get("aliases"), f"{location}.aliases")
    objective = _text(value.get("objective"), f"{location}.objective")
    destinations = _text_list(value.get("destinations"), f"{location}.destinations")
    signals = _text_list(value.get("completionSignals"), f"{location}.completionSignals")
    icon = value.get("icon")
    if icon is not None:
        icon = _text(icon, f"{location}.icon")
    cadence_value = _text(value.get("cadence"), f"{location}.cadence").lower()
    try:
        cadence = Cadence(cadence_value)
    except ValueError as exc:
        raise QuestError(f"{location}.cadence must be daily or weekly") from exc
    opens_at = _parse_time(value.get("opensAt"), f"{location}.opensAt")
    expires_at = _parse_time(value.get("expiresAt"), f"{location}.expiresAt")
    if opens_at == expires_at:
        raise QuestError(f"{location} opensAt and expiresAt must differ")
    priority = value.get("priority", 0)
    if isinstance(priority, bool) or not isinstance(priority, int):
        raise QuestError(f"{location}.priority must be an integer")
    return QuestDefinition(quest_id, aliases, icon, objective, destinations, signals, cadence, opens_at, expires_at, priority)


def _text(value: Any, location: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise QuestError(f"{location} must be a non-empty string")
    return value.strip()


def _text_list(value: Any, location: str) -> tuple[str, ...]:
    if not isinstance(value, list) or not value:
        raise QuestError(f"{location} must be a non-empty JSON array")
    result = tuple(_text(item, f"{location}[]") for item in value)
    if len(set(result)) != len(result):
        raise QuestError(f"{location} must not contain duplicates")
    return result


def _parse_time(value: Any, location: str) -> time:
    text = _text(value, location)
    if not _TIME_PATTERN.fullmatch(text):
        raise QuestError(f"{location} must use HH:MM format")
    try:
        return time.fromisoformat(text)
    except ValueError as exc:
        raise QuestError(f"{location} is not a valid time") from exc
