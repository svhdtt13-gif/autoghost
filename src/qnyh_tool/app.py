"""Observation-only desktop application for selecting qnyh clients."""

from __future__ import annotations

import sys
from importlib import import_module
from pathlib import Path
from typing import Any
from uuid import uuid4

from .calibration import CalibrationError, CalibrationRecorder
from .checkpoint import CheckpointStore
from .config import AppConfig, default_config
from .observability import StructuredLogger
from .profiles import ProfileError, load_profiles
from .quests import QuestError, load_catalog
from .vision import CaptureError, ObservationService, OcrAdapterError, PaddleOcrReader
from .windows.discovery import DiscoveryError, WindowInfo, discover_qnyh_windows


def session_paths(config: AppConfig) -> tuple[Path, Path]:
    """Return the event-log and checkpoint paths for an application session."""

    return (
        Path(config.paths.logs) / "events.jsonl",
        Path(config.paths.checkpoints) / "checkpoints.db",
    )


def main(argv: list[str] | None = None) -> int:
    """Start the observation window without sending input to any client."""

    try:
        qt_core = import_module("PySide6.QtCore")
        qt_widgets = import_module("PySide6.QtWidgets")
        QTimer = qt_core.QTimer
        QApplication = qt_widgets.QApplication
        QCheckBox = qt_widgets.QCheckBox
        QComboBox = qt_widgets.QComboBox
        QHBoxLayout = qt_widgets.QHBoxLayout
        QLabel = qt_widgets.QLabel
        QMainWindow = qt_widgets.QMainWindow
        QPushButton = qt_widgets.QPushButton
        QTableWidget = qt_widgets.QTableWidget
        QTableWidgetItem = qt_widgets.QTableWidgetItem
        QTabWidget = qt_widgets.QTabWidget
        QVBoxLayout = qt_widgets.QVBoxLayout
        QWidget = qt_widgets.QWidget
    except ImportError:
        print("The desktop UI requires the 'desktop' dependency extra.", file=sys.stderr)
        return 1

    class ClientWindow(QMainWindow):
        columns = ("Select", "PID", "Handle", "Title", "Size", "State")
        checkpoint_columns = ("Client", "Session", "State", "Step", "Status", "Reason")

        def __init__(self, config: AppConfig | None = None) -> None:
            super().__init__()
            self.setWindowTitle("Qnyh Client Observer")
            self.resize(1000, 680)
            self._windows: list[WindowInfo] = []
            self._config = config or default_config()
            self._observer = ObservationService()
            self._session_id = str(uuid4())
            self._ocr_reader: Any | None = None
            log_path, checkpoint_path = session_paths(self._config)
            self._logger = StructuredLogger(log_path, {"session_id": self._session_id, "mode": "observation"})
            self._checkpoints = CheckpointStore(checkpoint_path)
            self._write_event("application_started")
            try:
                self._profiles = load_profiles(Path(self._config.paths.profiles) / "default.json")
            except ProfileError:
                self._profiles = ()
            try:
                self._quest_catalog = load_catalog(self._config.paths.task_catalog)
            except QuestError:
                self._quest_catalog = None

            root = QWidget(self)
            layout = QVBoxLayout(root)
            header = QHBoxLayout()
            header.addWidget(QLabel("Mode: observation (no mouse or keyboard input)"))
            header.addStretch()
            header.addWidget(QLabel("Profile:"))
            self.profile_combo = QComboBox()
            self.profile_combo.addItems([profile.profile_id for profile in self._profiles])
            header.addWidget(self.profile_combo)
            inspect = QPushButton("Inspect selected")
            inspect.clicked.connect(self.inspect_selected)
            header.addWidget(inspect)
            recognize = QPushButton("Recognize selected")
            recognize.clicked.connect(self.recognize_selected)
            recognize.setEnabled(bool(self._profiles))
            header.addWidget(recognize)
            calibrate = QPushButton("Capture calibration")
            calibrate.clicked.connect(self.capture_calibration)
            calibrate.setEnabled(bool(self._profiles))
            header.addWidget(calibrate)
            refresh = QPushButton("Refresh")
            refresh.clicked.connect(self.refresh)
            header.addWidget(refresh)
            layout.addLayout(header)

            self.table = QTableWidget(0, len(self.columns), self)
            self.table.setHorizontalHeaderLabels(self.columns)
            self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
            self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
            self.table.horizontalHeader().setStretchLastSection(True)
            tabs = QTabWidget(self)
            clients_tab = QWidget(self)
            clients_layout = QVBoxLayout(clients_tab)
            clients_layout.addWidget(self.table)
            tabs.addTab(clients_tab, "Clients")

            self.checkpoint_table = QTableWidget(0, len(self.checkpoint_columns), self)
            self.checkpoint_table.setHorizontalHeaderLabels(self.checkpoint_columns)
            self.checkpoint_table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
            self.checkpoint_table.horizontalHeader().setStretchLastSection(True)
            checkpoints_tab = QWidget(self)
            checkpoints_layout = QVBoxLayout(checkpoints_tab)
            checkpoints_layout.addWidget(self.checkpoint_table)
            tabs.addTab(checkpoints_tab, "Checkpoints")
            layout.addWidget(tabs)

            self.status = QLabel("No clients discovered.")
            layout.addWidget(self.status)
            self.setCentralWidget(root)
            self.refresh()
            timer = QTimer(self)
            timer.timeout.connect(self.refresh)
            timer.start(3000)
            self._refresh_timer = timer

        def refresh(self) -> None:
            selected = self.selected_handles()
            try:
                windows = discover_qnyh_windows()
            except DiscoveryError as exc:
                self._windows = []
                self.status.setText(str(exc))
                self.table.setRowCount(0)
                self._write_event("client_discovery_failed", error=str(exc))
                return
            self._windows = windows
            self.table.setRowCount(len(windows))
            for row, window in enumerate(windows):
                checkbox = QCheckBox()
                checkbox.setChecked(window.hwnd in selected)
                checkbox.setToolTip("Selecting a client does not send any input")
                self.table.setCellWidget(row, 0, checkbox)
                values = (str(window.pid), f"0x{window.hwnd:X}", window.title or "(untitled)", f"{window.width} x {window.height}", window.state)
                for column, value in enumerate(values, start=1):
                    self.table.setItem(row, column, QTableWidgetItem(value))
            self._write_event("client_discovery", discovered=len(windows), selected=len(self.selected_handles()))
            self._update_status(len(windows))
            self._refresh_checkpoints()

        def selected_handles(self) -> set[int]:
            handles: set[int] = set()
            for row in range(self.table.rowCount()):
                checkbox = self.table.cellWidget(row, 0)
                if isinstance(checkbox, QCheckBox) and checkbox.isChecked():
                    handles.add(self._windows[row].hwnd)
            return handles

        def inspect_selected(self) -> None:
            selected = self.selected_handles()
            windows = [window for window in self._windows if window.hwnd in selected]
            if not windows:
                self.status.setText("Select at least one client to inspect.")
                return
            successful = 0
            for window in windows:
                result = self._observer.inspect(window)
                if result.captured:
                    successful += 1
                self._write_event("window_observed", client_id=f"hwnd:{window.hwnd}", captured=result.captured, error=result.error)
                self._save_observation_checkpoint(window, result)
            self._refresh_checkpoints()
            self.status.setText(f"Inspected {len(windows)} client(s); {successful} capture(s) succeeded.")

        def capture_calibration(self) -> None:
            selected = self.selected_handles()
            windows = [window for window in self._windows if window.hwnd in selected]
            if len(windows) != 1:
                self.status.setText("Select exactly one client for calibration.")
                return
            profile_id = self.profile_combo.currentText().strip()
            if not profile_id:
                self.status.setText("No interface profile is available.")
                return
            sample_id = f"sample-{windows[0].hwnd}-{uuid4().hex[:8]}"
            try:
                sample = self._observer.save_calibration(window=windows[0], profile_id=profile_id, recorder=CalibrationRecorder(self._config.paths.calibration), sample_id=sample_id)
            except (CalibrationError, CaptureError) as exc:
                self._write_event("calibration_failed", client_id=f"hwnd:{windows[0].hwnd}", error=str(exc))
                self.status.setText(f"Calibration failed: {exc}")
                return
            self._write_event("calibration_saved", client_id=f"hwnd:{windows[0].hwnd}", profile_id=profile_id, sample_id=sample.sample_id)
            self.status.setText(f"Calibration saved: {sample.sample_id}")

        def recognize_selected(self) -> None:
            selected = self.selected_handles()
            windows = [window for window in self._windows if window.hwnd in selected]
            if not windows:
                self.status.setText("Select at least one client to recognize.")
                return
            profile_id = self.profile_combo.currentText().strip()
            profile = next((item for item in self._profiles if item.profile_id == profile_id), None)
            if profile is None:
                self.status.setText("No interface profile is available.")
                return
            if self._ocr_reader is None:
                try:
                    self._ocr_reader = PaddleOcrReader()
                except OcrAdapterError as exc:
                    self._write_event("recognition_init_failed", error=str(exc))
                    self.status.setText(f"OCR initialization failed: {exc}")
                    return
            matched = 0
            for window in windows:
                try:
                    result, report = self._observer.recognize(window, profile, quest_catalog=self._quest_catalog, ocr_reader=self._ocr_reader)
                except CaptureError as exc:
                    self._write_event("recognition_failed", client_id=f"hwnd:{window.hwnd}", error=str(exc))
                    continue
                status = str(result.snapshot.get("recognition_status", "unknown"))
                if status == "matched":
                    matched += 1
                self._write_event("recognition", client_id=f"hwnd:{window.hwnd}", profile_id=profile.profile_id, status=status, recognized_quest_id=result.snapshot.get("recognized_quest_id"))
                self._save_observation_checkpoint(window, result, report=report)
            self._refresh_checkpoints()
            self.status.setText(f"Recognized {len(windows)} client(s); {matched} matched.")

        def _save_observation_checkpoint(self, window: WindowInfo, result: Any, report: Any | None = None) -> None:
            try:
                self._checkpoints.save(client_id=f"hwnd:{window.hwnd}", session_id=self._session_id, state=str(result.snapshot.get("recognition_status", "observed")), step_name="recognition" if report is not None else "observation", status=str(result.snapshot.get("recognition_status", "unknown")) if report is not None else ("capture_ok" if result.captured else "capture_failed"), snapshot=result.snapshot, reason=result.error)
            except Exception:
                return

        def _update_status(self, discovered: int | None = None) -> None:
            count = len(self._windows) if discovered is None else discovered
            self.status.setText(f"Discovered {count} qnyh window(s); selected {len(self.selected_handles())}. Observation-only.")

        def _refresh_checkpoints(self) -> None:
            checkpoints = self._checkpoints.recent()
            self.checkpoint_table.setRowCount(len(checkpoints))
            for row, checkpoint in enumerate(checkpoints):
                values = (checkpoint.client_id, checkpoint.session_id, checkpoint.state, checkpoint.step_name, checkpoint.status, checkpoint.reason or "")
                for column, value in enumerate(values):
                    self.checkpoint_table.setItem(row, column, QTableWidgetItem(value))

        def closeEvent(self, event: Any) -> None:
            try:
                self._write_event("application_stopped", selected=len(self.selected_handles()))
            finally:
                self._checkpoints.close()
            event.accept()

        def _write_event(self, event: str, **fields: Any) -> None:
            try:
                self._logger.write(event, **fields)
            except Exception:
                return

    app = QApplication(argv if argv is not None else sys.argv)
    window = ClientWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
