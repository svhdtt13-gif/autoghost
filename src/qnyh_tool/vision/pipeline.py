"""Replaceable template/OCR recognition pipeline for calibrated profiles."""

from __future__ import annotations

import re
import unicodedata
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Protocol

from ..profiles import InterfaceProfile, Locator
from ..quests import QuestCatalog
from .recognition import Recognition, RecognitionStatus, VisionError, match_template


@dataclass(frozen=True, slots=True)
class OcrResult:
    text: str
    confidence: float
    bounds: tuple[int, int, int, int] | None = None


class OcrReader(Protocol):
    def read(self, frame: Any, reference: str) -> OcrResult:
        raise NotImplementedError


@dataclass(frozen=True, slots=True)
class RecognitionReport:
    profile_id: str
    results: tuple[Recognition, ...]

    @property
    def status(self) -> RecognitionStatus:
        statuses = {result.status for result in self.results}
        if RecognitionStatus.UNKNOWN in statuses:
            return RecognitionStatus.UNKNOWN
        if RecognitionStatus.LOW_CONFIDENCE in statuses:
            return RecognitionStatus.LOW_CONFIDENCE
        return RecognitionStatus.MATCHED


def recognize_profile(frame: Any, profile: InterfaceProfile, *, template_loader: Callable[[str], Any] | None = None, template_matcher: Callable[..., Recognition] = match_template, ocr_reader: OcrReader | None = None) -> RecognitionReport:
    results = tuple(_recognize_locator(frame, locator, template_loader=template_loader, template_matcher=template_matcher, ocr_reader=ocr_reader) for locator in profile.locators)
    return RecognitionReport(profile.profile_id, results)


def snapshot_from_report(report: RecognitionReport, *, quest_catalog: QuestCatalog | None = None) -> dict[str, Any]:
    snapshot: dict[str, Any] = {"profile_id": report.profile_id, "recognition_status": report.status.value, "recognized_elements": {result.locator_name: result.status.value for result in report.results}}
    if quest_catalog is not None:
        for result in report.results:
            if not result.text:
                continue
            quest_id = match_quest_alias(result.text, quest_catalog)
            if quest_id is not None:
                snapshot["recognized_quest_id"] = quest_id
                snapshot["recognition_confidence"] = result.confidence
                break
    return snapshot


def match_quest_alias(text: str, catalog: QuestCatalog) -> str | None:
    normalized = _normalize_text(text)
    if not normalized:
        return None
    aliases = [(_normalize_text(alias), quest.quest_id) for quest in catalog.quests for alias in quest.aliases]
    for alias, quest_id in sorted(aliases, key=lambda item: len(item[0]), reverse=True):
        if alias and alias in normalized:
            return quest_id
    return None


def _recognize_locator(frame: Any, locator: Locator, *, template_loader: Callable[[str], Any] | None, template_matcher: Callable[..., Recognition], ocr_reader: OcrReader | None) -> Recognition:
    if locator.kind in {"anchor", "template"}:
        if template_loader is None:
            return Recognition(locator.name, 0.0, locator.min_confidence, False)
        try:
            return template_matcher(frame, template_loader(locator.reference), locator_name=locator.name, min_confidence=locator.min_confidence)
        except (OSError, ValueError, VisionError):
            return Recognition(locator.name, 0.0, locator.min_confidence, False)
    if locator.kind == "ocr":
        if ocr_reader is None:
            return Recognition(locator.name, 0.0, locator.min_confidence, False)
        try:
            result = ocr_reader.read(frame, locator.reference)
        except (OSError, ValueError, VisionError):
            return Recognition(locator.name, 0.0, locator.min_confidence, False)
        return Recognition(locator.name, max(0.0, min(1.0, result.confidence)), locator.min_confidence, bool(result.text.strip()), result.bounds, result.text)
    return Recognition(locator.name, 0.0, locator.min_confidence, False)


def _normalize_text(text: str) -> str:
    return re.sub(r"\s+", " ", unicodedata.normalize("NFKC", text).casefold()).strip()
