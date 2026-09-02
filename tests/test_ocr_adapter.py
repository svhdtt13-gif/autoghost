import json

import pytest

from qnyh_tool.vision import PaddleOcrReader


class LegacyEngine:
    def ocr(self, _image, cls):
        assert cls is True
        return [[[[0, 0], [40, 0], [40, 10], [0, 10]], ("Daily", 0.9)], [[[0, 12], [40, 12], [40, 22], [0, 22]], ("Training", 0.8)]]


class ModernResult:
    @property
    def json(self):
        return json.dumps({"rec_texts": ["Nhiệm vụ"], "rec_scores": [0.91], "rec_boxes": [[1, 2, 31, 12]]})


class ModernEngine:
    def predict(self, _image):
        return [ModernResult()]


def test_adapter_parses_legacy_paddleocr_lines():
    result = PaddleOcrReader(engine=LegacyEngine()).read(object(), "quest_text")
    assert result.text == "Daily Training"
    assert result.confidence == pytest.approx(0.85)
    assert result.bounds == (0, 0, 40, 22)


def test_adapter_parses_modern_paddleocr_result():
    result = PaddleOcrReader(engine=ModernEngine()).read(object(), "quest_text")
    assert result.text == "Nhiệm vụ"
    assert result.confidence == 0.91
    assert result.bounds == (1, 2, 30, 10)


def test_adapter_resolves_semantic_region_before_reading():
    calls = []
    reader = PaddleOcrReader(engine=ModernEngine(), region_resolver=lambda frame, reference: calls.append((frame, reference)) or "region")
    reader.read("frame", "vi.quest_text")
    assert calls == [("frame", "vi.quest_text")]
