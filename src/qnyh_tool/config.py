"""Validated, secret-free configuration for the qnyh tool."""

from __future__ import annotations

import json
import re
from collections.abc import Mapping
from dataclasses import asdict, dataclass, field
from enum import StrEnum
from pathlib import Path
from typing import Any


class ConfigError(ValueError):
    """Raised when a tool configuration is missing or invalid."""


class SafetyMode(StrEnum):
    """Controls whether the runtime is allowed to send UI actions."""

    OBSERVATION = "observation"
    DRY_RUN = "dry_run"
    ACTIVE = "active"


_SECRET_KEY = re.compile(
    r"(?:token|secret|password|passwd|cookie|bearer|session|api[_-]?key)",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class ToolPaths:
    profiles: str = "profiles"
    task_catalog: str = "tasks/catalog.json"
    schedules: str = "schedules"
    logs: str = "logs"
    screenshots: str = "screenshots"
    checkpoints: str = "checkpoints"
    calibration: str = "calibration"


@dataclass(frozen=True)
class AppConfig:
    """Application configuration with no credential-bearing fields."""

    version: int = 1
    safety_mode: SafetyMode = SafetyMode.OBSERVATION
    timezone: str = "Asia/Ho_Chi_Minh"
    selected_clients: tuple[str, ...] = ()
    paths: ToolPaths = field(default_factory=ToolPaths)

    def __post_init__(self) -> None:
        if self.version != 1:
            raise ConfigError("unsupported config version; expected 1")
        if not self.timezone.strip():
            raise ConfigError("timezone must not be empty")
        if any(not client.strip() for client in self.selected_clients):
            raise ConfigError("selected_clients must contain non-empty client ids")
        if len(set(self.selected_clients)) != len(self.selected_clients):
            raise ConfigError("selected_clients must not contain duplicates")

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["safety_mode"] = self.safety_mode.value
        data["selected_clients"] = list(self.selected_clients)
        return data


def default_config() -> AppConfig:
    return AppConfig()


def load_config(path: str | Path) -> AppConfig:
    config_path = Path(path)
    try:
        raw = json.loads(config_path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise ConfigError(f"config file not found: {config_path}") from exc
    except json.JSONDecodeError as exc:
        raise ConfigError(f"invalid JSON in config: {config_path}") from exc

    if not isinstance(raw, Mapping):
        raise ConfigError("config root must be a JSON object")
    _reject_secret_keys(raw)

    version = raw.get("version", 1)
    mode_value = raw.get("safety_mode", SafetyMode.OBSERVATION.value)
    selected = raw.get("selected_clients", [])
    paths = raw.get("paths", {})
    timezone = raw.get("timezone", "Asia/Ho_Chi_Minh")

    if not isinstance(version, int) or isinstance(version, bool):
        raise ConfigError("version must be an integer")
    if not isinstance(mode_value, str):
        raise ConfigError("safety_mode must be a string")
    try:
        mode = SafetyMode(mode_value)
    except ValueError as exc:
        allowed = ", ".join(item.value for item in SafetyMode)
        raise ConfigError(f"invalid safety_mode; expected one of: {allowed}") from exc
    if not isinstance(selected, list) or any(not isinstance(item, str) for item in selected):
        raise ConfigError("selected_clients must be a JSON array of strings")
    if not isinstance(paths, Mapping):
        raise ConfigError("paths must be a JSON object")
    if not isinstance(timezone, str):
        raise ConfigError("timezone must be a string")

    known = set(ToolPaths.__dataclass_fields__)
    unknown_paths = set(paths) - known
    if unknown_paths:
        raise ConfigError(f"unknown path fields: {', '.join(sorted(unknown_paths))}")
    path_values = {key: value for key, value in paths.items()}
    if any(not isinstance(value, str) or not value.strip() for value in path_values.values()):
        raise ConfigError("all path values must be non-empty strings")

    return AppConfig(
        version=version,
        safety_mode=mode,
        timezone=timezone,
        selected_clients=tuple(selected),
        paths=ToolPaths(**path_values),
    )


def _reject_secret_keys(value: Any, location: str = "config") -> None:
    if isinstance(value, Mapping):
        for key, child in value.items():
            if _SECRET_KEY.search(str(key)):
                raise ConfigError(f"credential-like field is not allowed: {location}.{key}")
            _reject_secret_keys(child, f"{location}.{key}")
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _reject_secret_keys(child, f"{location}[{index}]")
