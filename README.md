# Qnyh UI Automation Tool

Windows-first desktop tool for observing and, only after explicit confirmation,
automating selected `qnyh.exe` clients through visible UI interactions.

## Current status

The MVP currently provides an observation-only desktop window for discovering
and selecting `qnyh.exe` clients, plus validated interface profiles, a finite
quest catalog, timezone-aware daily/weekly scheduling, a guarded single-client
state machine, logic-only party coordination, and a read-only vision/calibration
flow. The desktop UI can inspect selected windows and capture one calibration
sample for a chosen profile, or recognize selected windows with the configured
OCR adapter. The safe default mode is `observation`; this mode
sends no mouse or keyboard input. UI adapters for active actions and packaging
are implemented incrementally in later phases.
State transitions and party failures can be persisted as redacted JSONL events
and SQLite checkpoints; the desktop app exposes the latest checkpoints in its
`Checkpoints` tab.

## Safety boundary

This project is limited to UI automation. It must not read or write game
memory, inject DLLs, modify game binaries, impersonate network traffic, or
bypass anti-cheat controls. Unknown visual states must stop safely. Use only
accounts and servers where automation is permitted.

The existing `360Auto` and AutoGhostStory configuration are reference material
only. The new tool has its own versioned configuration and does not require
their tokens, cookies, sessions, or runtime protocols.

## Configuration

The loader accepts a local JSON object with `version`, `safety_mode`,
`timezone`, `selected_clients`, and `paths` fields. Allowed modes are
`observation`, `dry_run`, and `active`.

Configuration files must never contain tokens, cookies, sessions, passwords, or
API keys. Use environment variables for any future external service
integration.

## Safe first run

1. Install Python 3.11 or newer.
2. Create a virtual environment and install the desktop and test extras with
   `python -m pip install -e ".[desktop,test]"`.
3. Run `python -m qnyh_tool` to open the observation window.
4. Run `python -m pytest`.
5. Do not add active UI actions until profile and single-client calibration
   phases are complete.

Profile examples are in `profiles/default.json`; the versioned quest catalog is
in `tasks/catalog.json`. Scheduling suppresses quests completed in the current
local day or ISO week and orders overlapping quests by descending priority.
Vision capture requires the `vision` extra and stores calibration metadata under
an application-selected directory; recognition must meet its configured
confidence threshold or remain unknown.
The PaddleOCR adapter is available through `qnyh_tool.vision.PaddleOcrReader`;
install the `ocr` extra and the platform-supported PaddlePaddle runtime before
using it. Verify engine initialization with `python -m qnyh_tool.vision.ocr`.
The dry-run quest runner consumes a recognized snapshot and simulates
`select_quest`, `travel`, and `claim_reward` transitions entirely in memory.

## Planned phases

1. Window discovery and per-language/skin/layout profiles. (implemented)
2. Finite quest catalog, dynamic visual recognition, and daily/weekly
   scheduling with user-defined priority. (catalog and scheduling foundation implemented)
3. Single-client state machine followed by leader/follower party coordination.
   (guarded state-machine and fail-stop coordinator foundations implemented)
4. Group fail-stop, checkpoint recovery, logs, tests, and Windows packaging.
   (structured logging and checkpoint foundations implemented)
