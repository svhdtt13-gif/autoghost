"""PaddleOCR adapter with support for common 2.x and 3.x result shapes."""

from __future__ import annotations

import json
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from importlib import import_module
from numbers import Real
from typing import Any

from ..pipeline import OcrResult
from ..recognition import VisionError


class OcrAdapterError(VisionError):
    """Raised when PaddleOCR is unavailable or returns an unsupported result."""


@dataclass(frozen=True, slots=True)
class _Candidate:
    text: str
    confidence: float
    bounds: tuple[int, int, int, int] | None


class PaddleOcrReader:
    def __init__(self, *, lang: str = "vi", engine: Any | None = None, region_resolver: Callable[[Any, str], Any] | None = None) -> None:
        if not lang.strip():
            raise ValueError("OCR language must not be empty")
        if engine is None:
            try:
                paddle = import_module("paddleocr")
                engine = paddle.PaddleOCR(lang=lang)
            except ImportError as exc:
                raise OcrAdapterError("PaddleOCR is unavailable; install the 'ocr' dependency extra") from exc
            except Exception as exc:
                raise OcrAdapterError("PaddleOCR could not be initialized") from exc
        self._engine = engine
        self._region_resolver = region_resolver

    def read(self, frame: Any, reference: str) -> OcrResult:
        target = self._region_resolver(frame, reference) if self._region_resolver is not None else frame
        try:
            raw = self._engine.ocr(target, cls=True) if hasattr(self._engine, "ocr") else self._engine.predict(target) if hasattr(self._engine, "predict") else None
            if raw is None:
                raise OcrAdapterError("PaddleOCR engine has no ocr or predict method")
        except OcrAdapterError:
            raise
        except Exception as exc:
            raise OcrAdapterError("PaddleOCR failed to read the frame") from exc
        return _merge_candidates(_extract_candidates(raw))


def _extract_candidates(value: Any) -> list[_Candidate]:
    value = _json_value(value)
    if isinstance(value, Mapping):
        return _extract_mapping(value)
    if isinstance(value, (list, tuple)):
        line = _extract_v2_line(value)
        if line is not None:
            return [line]
        candidates: list[_Candidate] = []
        for child in value:
            candidates.extend(_extract_candidates(child))
        return candidates
    return []


def _extract_mapping(value: Mapping[str, Any]) -> list[_Candidate]:
    texts = _as_list(value.get("rec_texts", value.get("texts", value.get("text"))))
    scores = _as_list(value.get("rec_scores", value.get("scores", value.get("score"))))
    boxes = _as_list(value.get("rec_boxes", value.get("boxes")))
    if not texts and isinstance(value.get("rec_text"), str):
        texts = [value["rec_text"]]
    candidates = []
    for index, text in enumerate(texts):
        if isinstance(text, str):
            candidates.append(_Candidate(text, max(0.0, min(1.0, _number_at(scores, index, 0.0))), _bounds_at(boxes, index)))
    return candidates


def _extract_v2_line(value: list[Any] | tuple[Any, ...]) -> _Candidate | None:
    if len(value) < 2 or not isinstance(value[1], (list, tuple)) or len(value[1]) < 2 or not isinstance(value[1][0], str):
        return None
    return _Candidate(value[1][0], max(0.0, min(1.0, _number(value[1][1], 0.0))), _bounds(value[0]))


def _merge_candidates(candidates: list[_Candidate]) -> OcrResult:
    usable = [candidate for candidate in candidates if candidate.text.strip()]
    if not usable:
        return OcrResult("", 0.0)
    bounds = [candidate.bounds for candidate in usable if candidate.bounds is not None]
    merged = None
    if bounds:
        left, top = min(item[0] for item in bounds), min(item[1] for item in bounds)
        right, bottom = max(item[0] + item[2] for item in bounds), max(item[1] + item[3] for item in bounds)
        merged = (left, top, right - left, bottom - top)
    return OcrResult(" ".join(item.text.strip() for item in usable), sum(item.confidence for item in usable) / len(usable), merged)


def _json_value(value: Any) -> Any:
    if hasattr(value, "json"):
        value = value.json
    if callable(value):
        value = value()
    if isinstance(value, str):
        try:
            return json.loads(value)
        except json.JSONDecodeError:
            return value
    return value


def _as_list(value: Any) -> list[Any]:
    value = _json_value(value)
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return list(value)
    return [value]


def _number_at(values: list[Any], index: int, default: float) -> float:
    return _number(values[index], default) if index < len(values) else default


def _number(value: Any, default: float) -> float:
    return float(value) if isinstance(value, Real) and not isinstance(value, bool) else default


def _bounds_at(values: list[Any], index: int) -> tuple[int, int, int, int] | None:
    return _bounds(values[index]) if index < len(values) else None


def _bounds(value: Any) -> tuple[int, int, int, int] | None:
    value = _json_value(value)
    if not isinstance(value, (list, tuple)):
        return None
    if len(value) == 4 and all(isinstance(item, Real) for item in value):
        left, top, right, bottom = (int(item) for item in value)
        return (left, top, max(0, right - left), max(0, bottom - top))
    points = [point for point in value if isinstance(point, (list, tuple)) and len(point) >= 2]
    if not points:
        return None
    xs, ys = [int(point[0]) for point in points], [int(point[1]) for point in points]
    left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
    return (left, top, max(0, right - left), max(0, bottom - top))
