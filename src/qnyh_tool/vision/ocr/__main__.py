"""Initialize the configured PaddleOCR engine for an environment check."""

from __future__ import annotations

import sys

from .paddle import OcrAdapterError, PaddleOcrReader


def main() -> int:
    try:
        PaddleOcrReader()
    except OcrAdapterError as exc:
        print(f"PaddleOCR initialization failed: {exc}", file=sys.stderr)
        return 1
    print("PaddleOCR initialized successfully.")
    return 0


raise SystemExit(main())
