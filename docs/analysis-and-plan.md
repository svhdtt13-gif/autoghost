# Qnyh UI Automation Tool

This document is the sanitized project analysis and implementation plan. It
contains no tokens, cookies, sessions, passwords, API keys, or runtime
captures.

## 1. Mục tiêu

Build an independent Windows desktop tool that observes and, only after an
explicit user decision, automates selected `qnyh.exe` clients through visible
UI interaction. The tool must support daily and weekly quests whose opening
time, location, language, skin, and button layout can change.

The tool must support both synchronized execution of equivalent quests and
cooperative party gameplay. A fatal error in one party member stops the whole
group. Unknown visual states fail closed.

The tool does not read or write game memory, inject DLLs, modify binaries,
impersonate game network traffic, bypass anti-cheat, or depend on the private
runtime protocol of 360Auto/AutoGhostStory.

## 2. Dữ liệu đã phân tích

The observed qnyh executable is under the user's `Level Up Games/Ghost Story`
directory. Multiple qnyh processes run at the same time and their window
titles expose client/character information and server context. The first
runtime must discover windows by process name and read PID, handle, title,
visibility, and dimensions without focusing or sending input.

The current workspace contains a Python MVP with these layers:

- `windows`: read-only Win32 discovery helpers.
- `profiles`: versioned language/skin/layout locator profiles.
- `vision`: optional screen capture, template matching, OCR pipeline, and
  confidence reporting.
- `quests`: finite, versioned quest catalog with aliases, destinations,
  completion signals, cadence, and priority.
- `scheduler`: timezone-aware daily/weekly eligibility and overlap priority.
- `executor`: guarded state machine and dry-run quest runner.
- `party`: leader/follower lifecycle, ready checks, heartbeats, checkpoints,
  and group-wide safe stop.
- `observability`: redacted JSONL events.
- `checkpoint`: SQLite checkpoint history.
- `calibration`: redacted reference screenshot metadata.
- `app`: observation-only desktop UI for selecting clients, inspecting windows,
  recognizing profiles, and capturing calibration samples.

The existing AutoGhostStory/360Auto installation was used only as a reference
for concepts such as activity catalogs, schedules, per-client profiles, team
members, retry timing, and enabled activities. Its credentials and private
runtime data are intentionally excluded.

The older PowerShell controller, when present in the original workspace,
controls client lifecycle through a remote controller and is not gameplay
automation. Its client start/stop schedule must remain separate from the quest
schedule.

## 3. Kiến trúc

### Window and profile layer

Discover visible top-level qnyh windows and normalize observations by client
area and scale. Select a profile from language, skin, layout, and scale. Use
replaceable anchors, templates, OCR aliases, and relative geometry rather than
one absolute coordinate map.

### Quest layer

Normalize the finite quest list into canonical `questId` values. A definition
contains aliases, icon references, objective type, dynamic destination
resolvers, completion signals, cadence, availability window, and user priority.

### Execution layer

Represent each task as guarded steps with a precondition, semantic action,
timeout, limited retry count, and post-action verification. The first runtime
ports are observation-only and dry-run. An active UI adapter is not enabled by
default.

### Party layer

Use a central coordinator with leader/follower roles, ready barriers, phase
checkpoints, heartbeats, and explicit resume after safe stop. The coordinator
groups clients by canonical quest ID but still verifies the UI independently on
each client.

### Scheduler layer

Store daily/weekly recurrence, opening and expiry times, timezone, priority,
eligible clients, and party size. Suppress tasks already completed in the
current local day or ISO week. Resolve overlaps by the user-defined priority
order.

## 4. Kế hoạch triển khai

### Phase 1: Nền tảng quan sát

1. Validate the local, secret-free configuration.
2. Discover qnyh windows without focus or input.
3. Add profile validation and calibration sample storage.
4. Verify observation UI and tests without opening the game.

### Phase 2: Nhận diện nhiệm vụ

1. Fill the catalog with the known daily/weekly quest types.
2. Capture one manual sample per language, skin, and layout variant.
3. Add OCR aliases, icon templates, and dynamic destination selectors.
4. Stop safely when a quest or screen is outside the catalog.

### Phase 3: Chạy một client

1. Run the task state machine against recognized snapshots.
2. Validate preconditions and post-action transitions.
3. Use dry-run first, then one active client only after explicit confirmation.
4. Record every action, retry, checkpoint, and failure reason.

### Phase 4: Scheduler ngày/tuần

1. Add the real quest availability windows.
2. Add custom priority ordering for overlapping quests.
3. Suppress completed daily/weekly tasks.
4. Handle expired, delayed, and missed tasks without blind retries.

### Phase 5: Party và đồng bộ

1. Select a leader and followers from the chosen clients.
2. Coordinate invite, ready check, travel, combat, interaction, and reward.
3. Synchronize equivalent quests at checkpoints rather than exact timestamps.
4. Stop all members when any member loses its window, times out, or reaches an
   unknown state.

### Phase 6: Kiểm thử và đóng gói

1. Build golden screenshot fixtures without account data.
2. Test one client, two-client party, then the selected-client group.
3. Test focus loss, window disappearance, OCR failure, timeout, and recovery.
4. Review logs for redaction and verify no secret enters configuration.
5. Package a reproducible Windows `.exe` only after observation and dry-run
   flows are stable.

## 5. Dữ liệu cần bổ sung

Before active execution, manually complete each known quest type once so the
tool can record states, controls, destinations, combat completion, and reward
signals. Record one complete party flow for leader and followers. Provide
quest schedules and custom priority order, but never provide tokens, cookies,
sessions, passwords, or API keys.

## 6. Trạng thái và tiêu chí hoàn thành

Current local verification:

- Python package scaffold and safe config are present.
- Observation desktop UI, profiles, quest catalog, scheduler, guarded executor,
  party coordinator, calibration, logs, and checkpoints are present.
- Full test suite: 51 tests passing.
- Syntax checks pass for the core Python modules.
- Observation and dry-run are the safe defaults.
- No active input adapter is enabled by default.

MVP is complete only when a selected client can be observed, a known dynamic
quest can be recognized across its supported UI variants, a dry-run can finish
without real input, and a two-client party can fail-stop as a group. Active
execution must remain a separate, explicit, user-approved step.
