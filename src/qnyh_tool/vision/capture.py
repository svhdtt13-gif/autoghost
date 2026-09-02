"""Optional read-only capture of a qnyh window."""

from __future__ import annotations

from dataclasses import dataclass
from importlib import import_module
from io import BytesIO
from typing import Any


class CaptureError(RuntimeError):
    """Raised when a window frame cannot be captured safely."""


@dataclass(frozen=True, slots=True)
class CapturedFrame:
    hwnd: int
    left: int
    top: int
    width: int
    height: int
    image: Any


def capture_window(hwnd: int) -> CapturedFrame:
    if hwnd <= 0:
        raise CaptureError("hwnd must be positive")
    try:
        mss = import_module("mss")
        import win32gui
    except ImportError as exc:
        raise CaptureError("window capture requires the 'vision' and 'windows' dependency extras") from exc
    if not win32gui.IsWindow(hwnd):
        raise CaptureError(f"window handle is no longer valid: {hwnd}")
    left, top, right, bottom = win32gui.GetWindowRect(hwnd)
    width, height = right - left, bottom - top
    if width <= 0 or height <= 0:
        raise CaptureError(f"window has no capturable area: {hwnd}")
    with mss.mss() as screen:
        image = screen.grab({"left": left, "top": top, "width": width, "height": height})
    return CapturedFrame(hwnd, left, top, width, height, image)


def frame_to_png(frame: CapturedFrame) -> bytes:
    try:
        image_module = import_module("PIL.Image")
        image = image_module.frombytes("RGB", frame.image.size, frame.image.rgb)
        output = BytesIO()
        image.save(output, format="PNG")
        return output.getvalue()
    except (AttributeError, OSError, ValueError) as exc:
        raise CaptureError("captured frame could not be encoded as PNG") from exc
    except ImportError as exc:
        raise CaptureError("PNG encoding requires the 'vision' dependency extra") from exc


def frame_to_rgb(frame: CapturedFrame) -> Any:
    try:
        numpy = import_module("numpy")
        raw = numpy.frombuffer(frame.image.rgb, dtype=numpy.uint8)
        return raw.reshape((frame.height, frame.width, 3))
    except (AttributeError, ValueError) as exc:
        raise CaptureError("captured frame could not be converted to RGB") from exc
    except ImportError as exc:
        raise CaptureError("RGB conversion requires the 'vision' dependency extra") from exc
