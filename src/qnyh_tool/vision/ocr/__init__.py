"""OCR engine adapters."""

from .paddle import OcrAdapterError, PaddleOcrReader

__all__ = ["OcrAdapterError", "PaddleOcrReader"]
