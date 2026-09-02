"""Read-only screen capture and confidence-based visual recognition."""

from .capture import CapturedFrame, CaptureError, capture_window, frame_to_png, frame_to_rgb
from .observation import ObservationResult, ObservationService
from .ocr import OcrAdapterError, PaddleOcrReader
from .pipeline import OcrReader, OcrResult, RecognitionReport, match_quest_alias, recognize_profile, snapshot_from_report
from .recognition import Recognition, RecognitionStatus, VisionError, match_template, select_profile

__all__ = ["CaptureError", "CapturedFrame", "ObservationResult", "ObservationService", "OcrAdapterError", "OcrReader", "OcrResult", "PaddleOcrReader", "Recognition", "RecognitionReport", "RecognitionStatus", "VisionError", "capture_window", "frame_to_png", "frame_to_rgb", "match_quest_alias", "match_template", "recognize_profile", "select_profile", "snapshot_from_report"]
