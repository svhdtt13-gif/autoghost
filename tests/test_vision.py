import pytest

from qnyh_tool.calibration import CalibrationRecorder
from qnyh_tool.profiles import load_profiles
from qnyh_tool.quests import load_catalog
from qnyh_tool.vision import CaptureError, ObservationService, OcrResult, Recognition, RecognitionStatus, capture_window, recognize_profile, select_profile, snapshot_from_report
from qnyh_tool.vision.capture import CapturedFrame, frame_to_rgb
from qnyh_tool.windows import WindowInfo

WINDOW = WindowInfo(100, 10, "Qnyh", 800, 600, True, "visible", "qnyh.exe")


def test_select_profile_uses_dimensions_and_nearest_scale():
    profiles = load_profiles("profiles/default.json")
    selected = select_profile(profiles, language="VI", skin="default", layout="standard", scale=1.02)
    assert selected is not None
    assert selected.profile_id == "vi-default"
    assert select_profile(profiles, language="en", skin="default", layout="standard", scale=1.0) is None


def test_recognition_distinguishes_unknown_low_confidence_and_match():
    assert Recognition("x", 0.0, 0.8, False).status is RecognitionStatus.UNKNOWN
    assert Recognition("x", 0.7, 0.8, True).status is RecognitionStatus.LOW_CONFIDENCE
    assert Recognition("x", 0.9, 0.8, True).status is RecognitionStatus.MATCHED


def test_capture_rejects_invalid_handle_before_optional_imports():
    with pytest.raises(CaptureError, match="positive"):
        capture_window(0)


def test_observation_service_returns_state_machine_compatible_snapshot():
    frame = CapturedFrame(100, 0, 0, 800, 600, object())
    result = ObservationService(capture=lambda _hwnd: frame).inspect(WINDOW)
    assert result.captured is True
    assert result.snapshot["capture_status"] == "ok"
    assert result.snapshot["capture_width"] == 800


def test_observation_service_saves_calibration_with_injected_encoder(tmp_path):
    frame = CapturedFrame(100, 0, 0, 800, 600, object())
    sample = ObservationService(capture=lambda _hwnd: frame, encode_png=lambda _frame: b"png-data").save_calibration(WINDOW, profile_id="vi-default", recorder=CalibrationRecorder(tmp_path), sample_id="sample-1")
    assert sample.image_path.read_bytes() == b"png-data"


def test_frame_to_rgb_converts_mss_rgb_bytes():
    class Image:
        rgb = bytes([1, 2, 3, 4, 5, 6])
        size = (2, 1)
    result = frame_to_rgb(CapturedFrame(100, 0, 0, 2, 1, Image()))
    assert result.shape == (1, 2, 3)
    assert result.tolist() == [[[1, 2, 3], [4, 5, 6]]]


def test_recognition_pipeline_builds_dry_run_snapshot_from_ocr_alias():
    profile, catalog = load_profiles("profiles/default.json")[0], load_catalog("tasks/catalog.json")
    class Reader:
        def read(self, frame, reference):
            del frame, reference
            return OcrResult("Daily Training", 0.93)
    report = recognize_profile(object(), profile, template_loader=lambda _reference: object(), template_matcher=lambda *_args, **kwargs: Recognition(kwargs["locator_name"], 0.95, kwargs["min_confidence"], True), ocr_reader=Reader())
    snapshot = snapshot_from_report(report, quest_catalog=catalog)
    assert report.status is RecognitionStatus.MATCHED
    assert snapshot["recognized_quest_id"] == "daily-training"
    assert snapshot["recognition_confidence"] == 0.93


def test_missing_recognition_provider_is_unknown_and_not_a_match():
    report = recognize_profile(object(), load_profiles("profiles/default.json")[0])
    assert report.status is RecognitionStatus.UNKNOWN
    assert all(result.status is RecognitionStatus.UNKNOWN for result in report.results)


def test_observation_service_recognizes_captured_frame_for_dry_run():
    profile, catalog = load_profiles("profiles/default.json")[0], load_catalog("tasks/catalog.json")
    class Image:
        rgb = bytes([1, 2, 3, 4, 5, 6])
        size = (2, 1)
    class Reader:
        def read(self, frame, reference):
            del frame, reference
            return OcrResult("Daily Training", 0.96)
    result, report = ObservationService(capture=lambda _hwnd: CapturedFrame(100, 0, 0, 2, 1, Image())).recognize(WINDOW, profile, quest_catalog=catalog, template_loader=lambda _reference: object(), template_matcher=lambda *_args, **kwargs: Recognition(kwargs["locator_name"], 0.95, kwargs["min_confidence"], True), ocr_reader=Reader())
    assert report is not None
    assert result.snapshot["recognized_quest_id"] == "daily-training"
    assert result.snapshot["recognition_confidence"] == 0.96
