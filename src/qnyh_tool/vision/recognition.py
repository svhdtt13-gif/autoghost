"""Confidence-based visual recognition primitives."""

from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum
from importlib import import_module
from typing import Any

from ..profiles import InterfaceProfile


class VisionError(RuntimeError):
    """Raised when a recognition operation cannot be performed."""


class RecognitionStatus(StrEnum):
    MATCHED = "matched"
    LOW_CONFIDENCE = "low_confidence"
    UNKNOWN = "unknown"


@dataclass(frozen=True, slots=True)
class Recognition:
    locator_name: str
    confidence: float
    min_confidence: float
    found: bool
    bounds: tuple[int, int, int, int] | None = None
    text: str | None = None

    @property
    def status(self) -> RecognitionStatus:
        if not self.found or self.confidence <= 0:
            return RecognitionStatus.UNKNOWN
        if self.confidence < self.min_confidence:
            return RecognitionStatus.LOW_CONFIDENCE
        return RecognitionStatus.MATCHED


def select_profile(profiles: tuple[InterfaceProfile, ...], *, language: str, skin: str, layout: str, scale: float, scale_tolerance: float = 0.05) -> InterfaceProfile | None:
    if scale <= 0 or scale_tolerance < 0:
        raise ValueError("scale and scale_tolerance must be valid positive values")
    candidates = [profile for profile in profiles if profile.language.casefold() == language.casefold() and profile.skin.casefold() == skin.casefold() and profile.layout.casefold() == layout.casefold() and abs(profile.scale - scale) <= scale_tolerance]
    return min(candidates, key=lambda profile: abs(profile.scale - scale), default=None)


def match_template(frame: Any, template: Any, *, locator_name: str, min_confidence: float = 0.8) -> Recognition:
    if not 0 <= min_confidence <= 1:
        raise ValueError("min_confidence must be between 0 and 1")
    try:
        cv2 = import_module("cv2")
    except ImportError as exc:
        raise VisionError("template matching requires the 'vision' dependency extra") from exc
    try:
        result = cv2.matchTemplate(frame, template, cv2.TM_CCOEFF_NORMED)
        _minimum, maximum, _minimum_location, maximum_location = cv2.minMaxLoc(result)
        template_height, template_width = template.shape[:2]
    except (AttributeError, cv2.error, ValueError) as exc:
        raise VisionError("frame and template must be valid OpenCV images") from exc
    confidence = max(0.0, min(1.0, float(maximum)))
    left, top = maximum_location
    return Recognition(locator_name, confidence, min_confidence, True, (left, top, template_width, template_height))
