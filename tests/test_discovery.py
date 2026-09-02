from qnyh_tool.windows.discovery import is_target_executable, window_dimensions, window_state


def test_matches_target_executable_by_filename_case_insensitively():
    assert is_target_executable(r"C:\Games\QNYH.EXE")
    assert not is_target_executable(r"C:\Games\other.exe")


def test_window_dimensions_never_return_negative_values():
    assert window_dimensions((10, 20, 810, 620)) == (800, 600)
    assert window_dimensions((10, 20, 5, 15)) == (0, 0)


def test_window_state_prioritizes_minimized_state():
    assert window_state(visible=True, minimized=False) == "visible"
    assert window_state(visible=False, minimized=False) == "hidden"
    assert window_state(visible=True, minimized=True) == "minimized"
