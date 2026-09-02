"""Connect read-only window capture to state-machine-compatible snapshots."""

from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Any

from ..calibration import CalibrationRecorder, CalibrationSample
from ..windows.discovery import WindowInfo
from .capture import CapturedFrame, CaptureError, capture_window, frame_to_png, frame_to_rgb
from .pipeline import RecognitionReport, recognize_profile, snapshot_from_report


@dataclass(frozen=True, slots=True)
class ObservationResult:
    window: WindowInfo
    captured: bool
    snapshot: Mapping[str, Any]
    frame: CapturedFrame | None
    error: str | None = None


class ObservationService:
    def __init__(self, capture: Callable[[int], CapturedFrame] = capture_window, encode_png: Callable[[CapturedFrame], bytes] = frame_to_png) -> None:
        self._capture = capture
        self._encode_png = encode_png

    def inspect(self, window: WindowInfo) -> ObservationResult:
        try:
            frame = self._capture(window.hwnd)
        except CaptureError as exc:
            return ObservationResult(window, False, self._base_snapshot(window, capture_status="failed", error=str(exc)), None, str(exc))
        return ObservationResult(window, True, self._base_snapshot(window, capture_status="ok", capture_width=frame.width, capture_height=frame.height), frame)

    def save_calibration(self, window: WindowInfo, *, profile_id: str, recorder: CalibrationRecorder, sample_id: str) -> CalibrationSample:
        result = self.inspect(window)
        if not result.captured or result.frame is None:
            raise CaptureError(result.error or "window capture failed")
        return recorder.save_sample(sample_id=sample_id, client_id=f"hwnd:{window.hwnd}", profile_id=profile_id, screenshot=self._encode_png(result.frame), observations=dict(result.snapshot))

    def recognize(self, window: WindowInfo, profile: Any, *, quest_catalog: Any | None = None, template_loader: Callable[[str], Any] | None = None, template_matcher: Callable[..., Any] | None = None, ocr_reader: Any | None = None) -> tuple[ObservationResult, RecognitionReport | None]:
        result = self.inspect(window)
        if not result.captured or result.frame is None:
            return result, None
        arguments: dict[str, Any] = {"template_loader": template_loader, "ocr_reader": ocr_reader}
        if template_matcher is not None:
            arguments["template_matcher"] = template_matcher
        report = recognize_profile(frame_to_rgb(result.frame), profile, **arguments)
        snapshot = dict(result.snapshot)
        snapshot.update(snapshot_from_report(report, quest_catalog=quest_catalog))
        return ObservationResult(result.window, True, snapshot, result.frame), report

    @staticmethod
    def _base_snapshot(window: WindowInfo, **extra: Any) -> dict[str, Any]:
        return {"hwnd": window.hwnd, "pid": window.pid, "title": window.title, "visible": window.visible, "window_state": window.state, "captured_at": datetime.now(UTC).isoformat(), **extra}
