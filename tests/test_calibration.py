import json

from qnyh_tool.calibration import CalibrationRecorder


def test_calibration_recorder_writes_sample_and_redacts_metadata(tmp_path):
    sample = CalibrationRecorder(tmp_path).save_sample(sample_id="sample-1", client_id="client_1", profile_id="vi-default", screenshot=b"fake-png", observations={"quest_panel": {"x": 10}, "session": "private-value"})
    assert sample.image_path.read_bytes() == b"fake-png"
    metadata = json.loads(sample.metadata_path.read_text(encoding="utf-8"))
    assert metadata["observations"]["quest_panel"] == {"x": 10}
    assert metadata["observations"]["session"] == "[REDACTED]"
    assert "private-value" not in sample.metadata_path.read_text(encoding="utf-8")
