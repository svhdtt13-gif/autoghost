"""Read-only discovery of visible qnyh windows on Windows."""

from __future__ import annotations

import ntpath
from dataclasses import dataclass
from typing import Any


class DiscoveryError(RuntimeError):
    """Raised when the Windows discovery dependencies are unavailable."""


@dataclass(frozen=True, slots=True)
class WindowInfo:
    """Observable metadata for one top-level qnyh window."""

    hwnd: int
    pid: int
    title: str
    width: int
    height: int
    visible: bool
    state: str
    executable: str


def window_state(*, visible: bool, minimized: bool) -> str:
    """Convert Win32 visibility flags into a stable display state."""

    if minimized:
        return "minimized"
    return "visible" if visible else "hidden"


def window_dimensions(rect: tuple[int, int, int, int]) -> tuple[int, int]:
    """Return non-negative width and height from a Win32 window rectangle."""

    left, top, right, bottom = rect
    return max(0, right - left), max(0, bottom - top)


def is_target_executable(path: str, executable_name: str = "qnyh.exe") -> bool:
    """Match an executable by filename without trusting its directory."""

    return ntpath.basename(path).casefold() == executable_name.casefold()


def discover_qnyh_windows(executable_name: str = "qnyh.exe") -> list[WindowInfo]:
    """Enumerate top-level windows owned by the requested executable.

    This function only reads process and window metadata. It never activates,
    focuses, resizes, or sends input to a window.
    """

    try:
        import win32api
        import win32con
        import win32gui
        import win32process
    except ImportError as exc:
        raise DiscoveryError("Windows discovery requires the 'windows' dependency extra") from exc

    windows: list[WindowInfo] = []

    def visit(hwnd: int, _extra: Any) -> None:
        _thread_id, pid = win32process.GetWindowThreadProcessId(hwnd)
        if not pid:
            return
        executable = _get_executable_path(pid, win32api=win32api, win32con=win32con, win32process=win32process)
        if not executable or not is_target_executable(executable, executable_name):
            return
        rect = win32gui.GetWindowRect(hwnd)
        width, height = window_dimensions(rect)
        visible = bool(win32gui.IsWindowVisible(hwnd))
        windows.append(WindowInfo(hwnd=hwnd, pid=pid, title=win32gui.GetWindowText(hwnd).strip(), width=width, height=height, visible=visible, state=window_state(visible=visible, minimized=bool(win32gui.IsIconic(hwnd))), executable=executable))

    win32gui.EnumWindows(visit, None)
    return sorted(windows, key=lambda item: (item.title.casefold(), item.pid, item.hwnd))


def _get_executable_path(pid: int, *, win32api: Any, win32con: Any, win32process: Any) -> str:
    """Read a process image path and close the temporary process handle."""

    access = win32con.PROCESS_QUERY_INFORMATION | win32con.PROCESS_VM_READ
    handle = None
    try:
        handle = win32api.OpenProcess(access, False, pid)
        return str(win32process.GetModuleFileNameEx(handle, 0))
    except (OSError, win32api.error):
        return ""
    finally:
        if handle is not None:
            win32api.CloseHandle(handle)
