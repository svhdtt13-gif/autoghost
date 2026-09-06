# AutoGhost V1 — P0 RESULT (historical checkpoint)

This document contains the earlier P0 verification checkpoint. The published
C# source and current cross-phase status are summarized in
`docs/PLAN_V1_IMPLEMENTATION_MANIFEST_DEC032.md`; this historical evidence
must not be read as a claim that P1-P3 source was present at that checkpoint.

Date: 2026-09-05 18:30 ICT
State: `VERIFYING` — **P0 is not DONE and P1 is not authorized**

## Implementation

- Added a Windows-only .NET 8 P0 probe under `src/AutoGhost.P0Probe`.
- Enumerates every top-level window across the desktops in the current Windows station, then maps each owner PID to a Toolhelp process snapshot and ancestry.
- Records desktop, title, class, visibility, enabled state, owner/parent HWND, thread, PID, process path, parent PID, ancestry, minimized/cloaked/foreground state and window/client rectangles.
- Resolves qnyh process-tree relations for target, child and ancestor processes; a direct `qnyh.exe.MainWindowHandle` is no longer required.
- Treats title-bar Role ID as a configurable hypothesis. No regex means no identity and no bind.
- Classifies `MATCHED`, `MISMATCH`, `ROLE_ID_UNAVAILABLE` and `DUPLICATE_IDENTITY` fail-closed states.
- Added `BindingReconciler` for initial bind, same-session verification, PID/HWND rebind, delayed Role ID, changed Role ID and recreated-window cases.
- Added `InputGuard` invariant: identity must be verified, the session must still match, the binding must be READY, kill switch must be off and P0 input mode must be enabled. The P0 executable never dispatches OS input; it only evaluates the decision.

## Automated verification

```text
dotnet build AutoGhost.sln --configuration Release
Build succeeded — 0 warnings, 0 errors

dotnet run --project tests/AutoGhost.P0Probe.Tests/AutoGhost.P0Probe.Tests.csproj --configuration Release
P0 smoke tests: PASS (6/6)
```

Coverage includes title hypothesis parsing, exact match/mismatch, duplicate identity, kill switch, dry-run guard, stale PID/HWND blocking, PID/HWND rebind with a new session ID, delayed Role ID and changed Role ID.

## DEC-002 live runtime evidence

The refined probe was run in dry-run mode with `Role \[(?<roleId>\d+)\]` and wrote
[p0-dec002-live-2026-09-05.json](../artifacts/p0-dec002-live-2026-09-05.json).

```text
Target process-tree entries: 6
Top-level windows enumerated system-wide: 535
Target process-tree windows: 112
Input mode: DRY-RUN; InputGuard is not permitted to emit OS input.
```

The live process tree was:

```text
Level Up.exe (PID 32712)
├─ qnyh.exe (PID 21616) -> HWND 0xA08AE, Role 3488404011
└─ qnyh.exe (PID 20700) -> HWND 0x230A24, Role 16711204011
```

Both windows were on desktop `Default`, class `UnityWndClass`, visible,
enabled, not minimized and not cloaked. The raw titles were captured exactly:

```text
Ghost Story Engine [3.739133] Version [3.739133] Server [S1 - Yixiao Naihe] Role [3488404011]
Ghost Story Engine [3.739133] Version [3.739133] Server [S1 - Yixiao Naihe] Role [16711204011]
```

Role ID parsing is therefore observed PASS for this title format. Binding was
not claimed because no enabled real Client Registry IDs were supplied to this
run; both observations were classified `MISMATCH` and InputGuard remained
fail-closed. No OS input was emitted.

## Evidence status

| P0 item | Result | Notes |
|---|---|---|
| Probe builds on Windows/.NET 8 | PASS | Release build clean |
| Identity parsing and fail-closed classification | PASS | 6/6 smoke tests |
| PID/HWND/session reconciliation logic | PASS | Unit/smoke tested |
| Discover multiple real `qnyh.exe` instances | PASS | Two target PIDs observed |
| Map real `qnyh.exe` PID/HWND/title/Role ID | PASS | System-wide desktop enumeration mapped both Unity windows |
| Parse title-bar Role ID | PASS | `3488404011` and `16711204011` parsed from raw titles |
| Bind against real Client Registry IDs | NOT VERIFIED | No enabled real Client IDs were supplied; both fail-closed as `MISMATCH` |
| Foreground/background/minimized behavior on `qnyh.exe` | PENDING | Requires target runtime and controlled test matrix |
| Real guarded input/capture behavior on `qnyh.exe` | PENDING | No OS input emitted by design in P0 |

The project remains at P0 VERIFYING/BLOCKED. The next authorized step is to
repeat with enabled Client Registry IDs and then execute the separate
foreground/background/minimized capture and guarded-input matrix. P1 remains
unauthorized.
