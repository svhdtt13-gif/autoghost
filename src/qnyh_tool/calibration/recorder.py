"""Persist calibration screenshots and locator observations safely."""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from ..observability.logs import redact


class CalibrationError(ValueError):
    """Raised when a calibration sample is invalid or cannot be written."""


@dataclass(frozen=True, slots=True)
class CalibrationSample:
    sample_id: str
    client_id: str
    profile_id: str
    image_path: Path
    metadata_path: Path
    created_at: str


class CalibrationRecorder:
    def __init__(self, root: str | Path) -> None:
        self._root = Path(root)
        self._root.mkdir(parents=True, exist_ok=True)

    def save_sample(self, *, sample_id: str, client_id: str, profile_id: str, screenshot: bytes, observations: dict[str, Any]) -> CalibrationSample:
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", sample_id):
            raise CalibrationError("sample_id must be a safe filename identifier")
        if not client_id.strip() or not profile_id.strip():
            raise CalibrationError("client_id and profile_id must not be empty")
        if not screenshot:
            raise CalibrationError("screenshot must not be empty")
        if not isinstance(observations, dict):
            raise CalibrationError("observations must be a JSON object")
        image_path, metadata_path = self._root / f"{sample_id}.png", self._root / f"{sample_id}.json"
        created_at = datetime.now(UTC).isoformat()
        metadata = redact({"sample_id": sample_id, "client_id": client_id, "profile_id": profile_id, "created_at": created_at, "observations": observations})
        image_path.write_bytes(screenshot)
        metadata_path.write_text(json.dumps(metadata, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        return CalibrationSample(sample_id, client_id, profile_id, image_path, metadata_path, created_at)
