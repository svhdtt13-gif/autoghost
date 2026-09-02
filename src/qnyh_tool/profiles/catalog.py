"""Validated interface profiles with replaceable visual locators."""

from __future__ import annotations

import json
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Any


class ProfileError(ValueError):
    """Raised when an interface profile catalog is invalid."""


_LOCATOR_KINDS = frozenset({"selector", "anchor", "template", "ocr"})


@dataclass(frozen=True, slots=True)
class Locator:
    """A replaceable reference used to find a visual UI element."""

    name: str
    kind: str
    reference: str
    min_confidence: float = 0.8


@dataclass(frozen=True, slots=True)
class InterfaceProfile:
    """Profile dimensions used to select the appropriate UI recognizers."""

    profile_id: str
    language: str
    skin: str
    layout: str
    scale: float
    locators: tuple[Locator, ...]


def load_profiles(path: str | Path) -> tuple[InterfaceProfile, ...]:
    """Load and validate a versioned profile catalog from JSON."""

    profile_path = Path(path)
    try:
        raw = json.loads(profile_path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ProfileError(f"profile catalog not found: {profile_path}") from exc
    except json.JSONDecodeError as exc:
        raise ProfileError(f"invalid JSON in profile catalog: {profile_path}") from exc
    if not isinstance(raw, Mapping):
        raise ProfileError("profile catalog root must be a JSON object")
    version = raw.get("version", 1)
    if version != 1 or isinstance(version, bool):
        raise ProfileError("unsupported profile catalog version; expected 1")
    entries = raw.get("profiles")
    if not isinstance(entries, list) or not entries:
        raise ProfileError("profiles must be a non-empty JSON array")
    profiles = tuple(_parse_profile(entry, index) for index, entry in enumerate(entries))
    ids = [profile.profile_id for profile in profiles]
    if len(set(ids)) != len(ids):
        raise ProfileError("profile_id values must be unique")
    return profiles


def _parse_profile(value: Any, index: int) -> InterfaceProfile:
    if not isinstance(value, Mapping):
        raise ProfileError(f"profiles[{index}] must be a JSON object")
    profile_id = _required_text(value, "profile_id", f"profiles[{index}]")
    language = _required_text(value, "language", f"profiles[{index}]")
    skin = _required_text(value, "skin", f"profiles[{index}]")
    layout = _required_text(value, "layout", f"profiles[{index}]")
    scale = value.get("scale", 1.0)
    if isinstance(scale, bool) or not isinstance(scale, (int, float)) or not 0 < scale <= 4:
        raise ProfileError(f"profiles[{index}].scale must be greater than 0 and at most 4")
    raw_locators = value.get("locators")
    if not isinstance(raw_locators, list) or not raw_locators:
        raise ProfileError(f"profiles[{index}].locators must be a non-empty JSON array")
    locators = tuple(_parse_locator(locator, locator_index, index) for locator_index, locator in enumerate(raw_locators))
    names = [locator.name for locator in locators]
    if len(set(names)) != len(names):
        raise ProfileError(f"profiles[{index}].locator names must be unique")
    return InterfaceProfile(profile_id, language, skin, layout, float(scale), locators)


def _parse_locator(value: Any, locator_index: int, profile_index: int) -> Locator:
    location = f"profiles[{profile_index}].locators[{locator_index}]"
    if not isinstance(value, Mapping):
        raise ProfileError(f"{location} must be a JSON object")
    name = _required_text(value, "name", location)
    kind = _required_text(value, "kind", location).lower()
    if kind not in _LOCATOR_KINDS:
        raise ProfileError(f"{location}.kind must be one of: {', '.join(sorted(_LOCATOR_KINDS))}")
    reference = _required_text(value, "reference", location)
    confidence = value.get("min_confidence", 0.8)
    if isinstance(confidence, bool) or not isinstance(confidence, (int, float)) or not 0 <= confidence <= 1:
        raise ProfileError(f"{location}.min_confidence must be between 0 and 1")
    return Locator(name, kind, reference, float(confidence))


def _required_text(value: Mapping[str, Any], field: str, location: str) -> str:
    result = value.get(field)
    if not isinstance(result, str) or not result.strip():
        raise ProfileError(f"{location}.{field} must be a non-empty string")
    return result.strip()
