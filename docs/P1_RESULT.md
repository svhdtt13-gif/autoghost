# AutoGhost V1 — P1 Result

Status: **PASS — LIVE RESTART ACCEPTANCE VERIFIED**

Authorization: `Authorize P1`

## Scope delivered

- Added `src/AutoGhost.App`, a .NET 8 WPF application.
- Added Client Registry persistence at `%LOCALAPPDATA%\AutoGhost\client-registry.json`.
- Added process name and explicit Role ID title-regex configuration.
- Added Client ID add, remove, enable/disable, save, and selection management.
- Added read-only runtime refresh that reuses the proven P0 system-wide window inventory, title parsing, identity classification, and exact binding path.
- Added runtime fields for observed Role ID, PID, HWND, session, state, and fail-closed reason.
- Added P0 status and the known minimized GPU/frame capture limitation to the shell.
- Added a Kill Switch control that starts ON.

## DEC-006 scope alignment

P1 requires persistence for the Client Registry and its settings: process name, Role ID regex, Client IDs, and enable/disable state. This slice stores that configuration as JSON. Task history and scheduler history are explicitly deferred to later phases and are not P1 blockers.

DEC-007 safety rule is enforced by design: PID/HWND/session metadata is runtime-only, the Kill Switch starts ON, and the P1 shell exposes no automation dispatcher or persisted armed state. Every startup is explicitly DISARMED.

DEC-008 adds WPF host diagnostics. The app now logs OnStartup, MainWindow construction, Show return, Loaded, ContentRendered, Closed, startup exceptions, current session, same-session explorer processes, foreground context, and HWND/visibility state. It also logs registry restore and runtime rebind details. The live restart acceptance passed with a visible non-zero HWND before and after restart.

## Explicit non-scope

- No task/workflow automation was started.
- No game-specific action was added.
- No input dispatcher is wired in this P1 slice.
- No driver, injection, memory access, or anti-cheat bypass path was added.

## Verification

```text
dotnet build AutoGhost.sln --configuration Release
0 warnings, 0 errors

dotnet run --project tests/AutoGhost.P0Probe.Tests/AutoGhost.P0Probe.Tests.csproj --configuration Release
P0 smoke tests: PASS (6/6)

dotnet run --project tests/AutoGhost.App.Tests/AutoGhost.App.Tests.csproj --configuration Release
P1 registry persistence + disarmed startup: PASS

Live restart acceptance
P1_RESTART_ACCEPTANCE_2026-09-06.md: PASS
Two real qnyh Role IDs restored and rebound as READY; Kill Switch remained ON; automation remained DISARMED.
```

P1 is closed. P2 is ready for explicit authorization and has not been started.
