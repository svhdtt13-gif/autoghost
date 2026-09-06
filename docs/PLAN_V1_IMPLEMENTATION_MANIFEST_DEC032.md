# PLAN V1 Implementation Manifest — DEC-032

Date: 2026-09-06

## Source-of-truth audit

- Local C# workspace: `C:\Users\Admin\Documents\ChatGPT\GHOST`
- GitHub repository: `https://github.com/svhdtt13-gif/autoghost.git`
- Publication branch: `codex/p3-canonical-review-20260906`
- Publication PR: [#2](https://github.com/svhdtt13-gif/autoghost/pull/2)
- Before this publication, the local C# workspace had no commit and no
  configured remote. It was a separate workspace from the GitHub Python
  worktree, so the C# tree was not being tracked by GitHub. The local
  `.gitignore` only ignored build output; the primary gap was the wrong repo
  root/worktree, not hidden C# source.

The Python/PySide6 tree under `src/qnyh_tool` remains in the repository as a
legacy package. It is not used as evidence that the C# PLAN V1 implementation
exists.

## PLAN-to-source map

### P0 — qnyh feasibility probe

- Project: `src/AutoGhost.P0Probe/AutoGhost.P0Probe.csproj`
- Runtime files: `Program.cs`, `Models.cs`, `RoleIdResolver.cs`,
  `BindingEngine.cs`, `Win32WindowInventory.cs`, `CaptureProbe.cs`,
  `DesktopThreadRunner.cs`, `InputGuard.cs`, `InputProbe.cs`.
- Tests: `tests/AutoGhost.P0Probe.Tests/Program.cs` — role parsing,
  match/mismatch/duplicate identity, Kill Switch, stale binding, rebind, and
  delayed/changed Role ID cases.
- Native source: `native/SendInputProbe.cpp`; its local `.exe`/`.obj` outputs
  are deliberately excluded.
- Evidence/docs: `docs/P0_PLAN.md`, `docs/P0_RESULT.md`,
  `docs/P0_ISSUE_COMMENT.md`, and the local qnyh evidence referenced there.
- GitHub-runnable: compile and P0 smoke tests.
- Local-only: real qnyh process/window inventory, Role ID/HWND mapping,
  foreground/capture/input behavior, desktop/session/integrity context.

### P1 — Client Manager + WPF shell

- Project: `src/AutoGhost.App/AutoGhost.App.csproj`.
- UI and services: `App.xaml`, `MainWindow.xaml`, `ClientDialog.xaml`, their
  code-behind, `Services/ClientRegistryService.cs`,
  `Services/RuntimeProbeService.cs`, `Services/ActionRecordingService.cs`,
  `Services/WpfStartupDiagnostics.cs`, and the `ViewModels` files.
- Tests: `tests/AutoGhost.App.Tests/Program.cs` — registry CRUD/persistence,
  per-client Special Items persistence, runtime-only PID/HWND/session fields,
  Kill Switch ON, and disarmed startup.
- Evidence/docs: `docs/P1_RESULT.md`,
  `docs/P1_RESTART_ACCEPTANCE_2026-09-06.md`, plus local restart/runtime
  evidence.
- GitHub-runnable: WPF build and registry/disarmed-startup test.
- Local-only: visible WPF HWND, interactive desktop, and live qnyh rebind.

### P2/P2.1 — Vision/capture, recorder, action model

- Project: `src/AutoGhost.ActionModel/AutoGhost.ActionModel.csproj`.
- Runtime files: `ActionDefinitions.cs`, `DryRunReplay.cs`,
  `WindowsInputRecorder.cs`; P0 capture/input files provide the Windows
  observation path.
- Tests: `tests/AutoGhost.ActionModel.Tests/Program.cs` — normalized
  coordinates, JSON action validation/round-trip, identity mismatch guard,
  Kill Switch, pause, stop, and dispatch suppression.
- Diagnostics: `tests/AutoGhost.P2ContextDiagnostic`,
  `tests/AutoGhost.P2InputOriginDiagnostic`, and
  `tests/AutoGhost.P2LiveAcceptance` are included and compiled; they remain
  interactive/local acceptance harnesses, not unattended CI tests.
- Evidence/docs: `docs/P2_PLAN.md`, `docs/P2_RESULT.md`,
  `docs/P2.1_INPUT_ORIGIN.md`, plus local capture/input-origin evidence.
- GitHub-runnable: build and deterministic action/dry-run tests.
- Local-only: qnyh foreground capture, native desktop context, physical
  input-origin verification, and supervised recorder acceptance. TeamViewer
  can support read-only/observation work; physical-input acceptance requires
  the aligned local interactive context.

### P3 Foundation — task engine and scheduler

- Project: `src/AutoGhost.TaskEngine/AutoGhost.TaskEngine.csproj`.
- Runtime files: `Models.cs`, `TaskEngine.cs`, `TaskSchedulerService.cs`,
  `JsonTaskHistoryStore.cs`.
- Tests: `tests/AutoGhost.TaskEngine.Tests/Program.cs` — due/priority/
  dependency selection, dry-run history, Kill Switch/live guard, retry,
  timeout, and persistence.
- Scope: this is the P3 foundation. The source keeps live task execution
  fail-closed/dry-run-only; scheduler foundation is not P4 acceptance.

### P3 Canonical — Vận chuyển note/history and Special Items

- Runtime files: `src/AutoGhost.TaskEngine/TransportNotes.cs`,
  `src/AutoGhost.P0Probe/Models.cs` (`ClientDefinition.SpecialItems`), and
  the P1 client dialog/view-model files that persist the operator list.
- Tests: `tests/AutoGhost.TaskEngine.Tests/Program.cs` — exact
  `RoleId + LocalDate` persistence/replacement, eight-item validation,
  configured-list `Match`/`NoMatch`/`PendingUserList`, and isolation between
  Roles `3488404011` and `16711204011`.
- Evidence/docs: `docs/P3_CANONICAL_REVIEW_2026-09-06.md`, `docs/P3_PLAN.md`,
  `docs/P3_RESULT.md`, and the P3 artifact package already present in PR #2.
- Important boundary: the accepted real qnyh navigation/8-item capture is
  supervised evidence. The C# source published here contains the validated
  note/history, Special Items, and safety model, but it does not contain a
  live game-specific navigation/dispatcher implementation. No source claim
  should promote that evidence into unattended live automation.

## Publication exclusions

The publication includes `AutoGhost.sln`, C# source/project files, C# test and
diagnostic projects, the sample config, the native `.cpp` source, and phase
documentation. It excludes `bin/`, `obj/`, native `.exe`/`.obj`, caches, local
logs, credentials, and unrelated P4+ implementation.

## Verification before publication

Executed locally from the C# workspace:

```text
dotnet restore AutoGhost.sln                         PASS
dotnet build AutoGhost.sln -c Release                PASS (0 warnings, 0 errors)
P0 smoke tests                                       PASS (6/6)
P1 registry/disarmed-startup tests                   PASS
P2 action model/dry-run tests                        PASS (4/4)
P3 foundation/canonical tests                        PASS (8/8)
```

The GitHub workflow now treats the C# solution as the PLAN V1 CI target and
keeps the Python package in a separately named legacy regression job.

## Status boundary

```text
P0 PASS
P1 PASS
P2 PASS
P3 Foundation PASS
P3 Canonical FINAL PASS
P4+ UNAUTHORIZED
```
