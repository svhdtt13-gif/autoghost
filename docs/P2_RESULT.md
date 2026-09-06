# AutoGhost V1 — P2 Result

Status: **P2 FINAL RESULT — PASS**

## Delivered

- `src/AutoGhost.ActionModel`
  - `ActionDefinition`, `ActionStep`, normalized coordinates, delays, mouse, keyboard, and semantic hook definitions.
  - JSON `ActionDefinitionStore` with schema validation and atomic replacement.
  - `WindowsInputRecorder` using foreground-only low-level observation.
  - `DryRunReplayEngine` with exact Client ID/Role ID guard, pause, stop, and Kill Switch suppression.
- `src/AutoGhost.App`
  - P2 recorder panel with target client selection, start/stop, save, dry-run, pause/resume, and stop controls.
  - P2 action files are stored below `%LOCALAPPDATA%\\AutoGhost\\actions`.
  - Runtime binding is rechecked while recorder callbacks arrive.
- `tests/AutoGhost.ActionModel.Tests`
  - normalized coordinate round-trip;
  - action validation and JSON round-trip;
  - identity mismatch fail-closed;
  - Kill Switch suppression, pause, and stop.

## Verification

```text
dotnet build AutoGhost.sln --configuration Release
0 warnings, 0 errors

P0 smoke tests: PASS (6/6)
P1 registry persistence + disarmed startup: PASS
P2 action model + dry-run tests: PASS (4/4)
```

## Not claimed yet

P2 is not marked `DONE` until a live qnyh foreground recording is performed and its action JSON/log evidence is reviewed. No live replay or task automation is enabled.

## DEC-010 acceptance attempt

The DEC-010 probe sampled foreground state repeatedly for 30 seconds before installing the recorder hook. The exact bound HWND was never stable foreground:

- bound Role ID: `3488404011`;
- bound HWND: `0xA619B0`;
- 480+ foreground samples;
- `GetForegroundWindow() = 0x0`;
- `hwndActive = 0x0`, `hwndFocus = 0x0`;
- `GetGUIThreadInfo = false`;
- target process session `1`, desktop `Default`;
- recorder hook: **NOT STARTED**;
- input dispatched: **0**.

Evidence from this attempt:

- `artifacts/p2-live-acceptance-2026-09-06-162259.json`;
- `artifacts/p2-live-acceptance-2026-09-06-162259.log`.

This is a fail-closed observation blocker, not a reason to weaken the exact-HWND foreground guard.

## DEC-011 context diagnostic

A read-only diagnostic was built and run without dispatching input. The result is
`BLOCKED_BY_INTERACTIVE_CONTEXT`:

- diagnostic session `1` matched the active console session `1`;
- window station was `WinSta0`;
- diagnostic thread desktop was `CodexSandboxDesktop-136c02e7d2eb55176b1196f0cf9bb1c6`, while the input desktop was `Default`;
- `currentThreadOnInputDesktop = false`;
- diagnostic integrity was `Medium`/not elevated, while both qnyh processes were `High`/elevated;
- both qnyh processes and Explorer were in session `1`;
- bound qnyh windows were visible on desktop `Default`, but neither exact HWND was foreground;
- `GetForegroundWindow() = 0x0`.

Evidence:

- `artifacts/p2-context-diagnostic-2026-09-06-162950.json`;
- `artifacts/p2-context-diagnostic-2026-09-06-162950.log`.

The next acceptance attempt must run the diagnostic and P2 harness manually from the visible interactive Windows desktop, with the normal Windows launch context aligned to the qnyh client. No `SwitchDesktop`, injection, driver, bypass, live replay, or task automation is permitted.

## DEC-012 P2 harness attempt

The manual P2 harness reached the visible desktop process but stopped fail-closed before scanning or starting the recorder:

```text
status: BLOCKED_REGISTRY_MISSING
registry: C:\Users\Admin\AppData\Local\AutoGhost\client-registry.json
```

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-163934.json`;
- `artifacts/p2-live-acceptance-2026-09-06-163934.log`.

This run does not provide foreground or recorder evidence. The Client Registry must first be created/restored in the same visible Windows user context, with explicit Client IDs, before the diagnostic and P2 acceptance can continue.

## DEC-012 context re-run

The diagnostic was then run from the visible elevated PowerShell on the interactive `Default` desktop and returned `CONTEXT_OBSERVED`:

- registry present with Client IDs `3488404011` and `16711204011`;
- session `1` matched the active console session;
- window station `WinSta0`;
- diagnostic thread desktop `Default` matched input desktop `Default`;
- diagnostic integrity `High/elevated` matched qnyh;
- `34` foreground samples were collected;
- exact foreground was `HWND 0xA619B0`, PID `45120`, Role ID `3488404011`;
- `exactBoundForegroundObserved = true` and `currentThreadOnInputDesktop = true`.

Evidence:

- `artifacts/p2-context-diagnostic-2026-09-06-165407.json`;
- `artifacts/p2-context-diagnostic-2026-09-06-165407.log`.

This authorizes the next P2 observation-only harness attempt. Live replay, dispatcher, and task automation remain disabled.

## P2 live acceptance attempt — 2026-09-06 16:55:52

The harness ran in the verified interactive context and confirmed:

- `44` foreground samples;
- exact bound HWND `0xA619B0` became stable foreground;
- binding, persistence reload, Role ID rebind, mismatch guard, disabled guard, stale-start rejection, and dry-run suppression all passed;
- recorder started in foreground-only observation mode with no dispatch.

The result remains `VERIFYING` because the observation window contained `0` recorded steps. Consequently there is no normalized point yet. This is an incomplete recording attempt, not an identity or context failure.

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-165552.json`;
- `artifacts/p2-live-acceptance-2026-09-06-165552.log`;
- `artifacts/p2-live-action-2026-09-06-165552.action.json`.

The next attempt (`16:58:00`) reproduced the same result: `44` stable foreground samples and all identity/guard/dry-run checks passed, but `steps = 0`. The harness now prints an explicit `RECORDER_STARTED` marker immediately after installing the observation-only hook so the next harmless qnyh action can be timed inside the recording window.

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-165800.json`;
- `artifacts/p2-live-acceptance-2026-09-06-165800.log`;
- `artifacts/p2-live-action-2026-09-06-165800.action.json`.

The following attempt (`16:59:54`) stopped before recorder start with `BLOCKED_NOT_FOREGROUND`: the exact qnyh HWND was not stable for the required consecutive samples. The harness now prints `FOREGROUND_SAMPLING_STARTED`, `FOREGROUND_STABLE`, and explicit blocked output to make the manual focus timing observable.

## P2 live acceptance attempt — 2026-09-06 17:06:59

This attempt proved the hook path is active and the identity guard is not the blocker:

- foreground stability: PASS;
- hook callbacks observed: `323`;
- rejected by foreground/identity guard: `0`;
- rejected as injected input: `323`;
- recorded steps: `0`;
- persistence, rebind, mismatch/disabled/stale guards, and dry-run suppression: PASS.

The recorder therefore correctly remained empty because it intentionally ignores injected input. No filter bypass or input-source exception is authorized. A P2 PASS requires at least one genuine non-injected user mouse/keyboard event in the verified qnyh foreground context, or a documented environment limitation if all available input is reported as injected.

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-170659.json`;
- `artifacts/p2-live-acceptance-2026-09-06-170659.log`;
- `artifacts/p2-live-action-2026-09-06-170659.action.json`.

## DEC-013 physical-input recheck — 2026-09-06 17:10:21

The direct physical-input recheck reproduced the limitation:

- exact foreground stability: PASS;
- hook callbacks: `219`;
- rejected by foreground/identity guard: `0`;
- rejected as injected: `219`;
- recorded steps: `0`;
- persistence, rebind, and dry-run suppression: PASS.

Historical state before the direct-local-input recheck was `VERIFYING / BLOCKED_BY_INJECTED_INPUT`. The injected filter remained unchanged throughout; the later P2.1 direct-local-input result is recorded below.

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-171021.json`;
- `artifacts/p2-live-acceptance-2026-09-06-171021.log`;
- `artifacts/p2-live-action-2026-09-06-171021.action.json`.

## P2.1 Input-Origin Diagnostic

P2.1 has been implemented as a separate read-only diagnostic. It registers Raw Input for mouse and keyboard with `RIDEV_INPUTSINK`, records Raw Input device handle/name evidence, and independently logs low-level injected flags across a Notepad stage and an exact-bound-qnyh stage. It does not use the action recorder, record steps, replay, or dispatch input.

Build verification:

```text
dotnet build AutoGhost.sln --configuration Release --no-restore
0 warnings, 0 errors
```

P2.1 has not changed the gate: a Raw Input device observation alone cannot make P2 PASS; at least one low-level non-injected event still remains required.

## P2.1 matrix attempt — 2026-09-06 17:36:45

Raw Input registration succeeded, and the qnyh stage was captured with the exact bound foreground (`HWND 0xA619B0`, Role ID `3488404011`). The qnyh stage contained `238` Raw Input messages and `245` low-level messages; all low-level messages were injected, with `0` non-injected events. Every captured Raw Input header had `hDevice = 0x0` and no device name was resolved.

The Notepad stage is not valid for comparison in this attempt: its foreground was `WindowsTerminal`, not Notepad, and the stage also contained qnyh foreground observations. Therefore the result confirms the qnyh side but does not yet support a Notepad-versus-qnyh conclusion. A repeat is required with an actual Notepad window focused during the `NOTEPAD` prompt.

Evidence:

- `artifacts/p2.1-input-origin-2026-09-06-173645.json`;
- `artifacts/p2.1-input-origin-2026-09-06-173645.log`.

## P2.1 valid matrix attempt — 2026-09-06 17:39:56

The repeat captured both requested foreground stages:

- Notepad foreground events: `335`; low-level injected `402/402`; non-injected `0`;
- exact qnyh foreground events: `235`; low-level injected `351/351`; non-injected `0`;
- Raw Input registration: PASS;
- Raw Input messages: present in both stages, but every captured header had `hDevice = 0x0` and no device name;
- recorder/replay/dispatch: not used.

This is environment-wide injected behavior across the Notepad and qnyh stages, not a qnyh-only difference. Because the Raw Input events have no device handle, they do not prove a physical HID source; the diagnostic now labels this case `RAW_INPUT_NO_DEVICE_HANDLE_LOWLEVEL_INJECTED_ONLY`. P2 remains blocked and the injected filter is unchanged.

Evidence:

- `artifacts/p2.1-input-origin-2026-09-06-173956.json`;
- `artifacts/p2.1-input-origin-2026-09-06-173956.log`.

## P2.1 physical-input path pass — 2026-09-06 17:47:54

The diagnostic was rerun on the direct local input path and passed the origin gate:

- Raw Input registration: `True`, error `0`;
- Notepad stage: `418` Raw Input messages, `2` resolved HID device names, `538` non-injected low-level events, `6` injected events, `465` foreground-Notepad events;
- qnyh stage: `442` Raw Input messages, the same `2` resolved HID device names, `550` non-injected low-level events, `0` injected events, `445` exact-bound-qnyh foreground events;
- exact qnyh target: Role ID `3488404011`, HWND `0xA619B0`;
- recorder/replay/dispatch: not used by P2.1.

The two resolved device paths were HID mouse/keyboard collections (`VID_046D&PID_C092` and `VID_0E6A&PID_1630`). This satisfies `PHYSICAL_INPUT_PATH PASS`; the completed live acceptance is recorded below. P3+ remains unauthorized.

Evidence:

- `artifacts/p2.1-input-origin-2026-09-06-174754.json`;
- `artifacts/p2.1-input-origin-2026-09-06-174754.log`.

## P2 FINAL RESULT — PASS — 2026-09-06 17:52:04

The local qnyh live acceptance completed all implemented P2 gates:

- exact target: Client/Role ID `3488404011`, PID `45120`, HWND `0xA619B0`;
- `23` foreground samples with exact bound HWND stable;
- `1` recorded non-injected mouse-button step;
- normalized point `(0.6293103448, 0.55)`;
- `delayBeforeMs = 20`;
- action JSON validation and persistence reload: PASS;
- re-resolved binding by Role ID after persistence reload: PASS;
- mismatch, disabled, stale-start, and Kill Switch guards: PASS;
- dry-run: `Completed`, `1` trace event, all dispatch suppressed;
- recorder callback diagnostics: `1` callback, `0` foreground/identity rejections, `0` injected rejections;
- live replay/dispatcher/task automation: not used.

Evidence:

- `artifacts/p2-live-acceptance-2026-09-06-175204.json`;
- `artifacts/p2-live-acceptance-2026-09-06-175204.log`;
- `artifacts/p2-live-action-2026-09-06-175204.action.json`.

P2 is closed as PASS for the implemented recorder/action-model acceptance. P3+ remains unauthorized; no live replay or dispatcher was enabled.
