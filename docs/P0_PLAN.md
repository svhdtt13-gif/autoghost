# AutoGhost V1 — P0 PLAN

Status: authorized discovery/probe only. This is not P1 and does not control a game client.

## Objective

Prove or falsify the runtime assumptions before building the WPF/MVVM application:

1. discover multiple `qnyh.exe` processes;
2. map process IDs to top-level windows and record PID/HWND/title/class/visibility/minimized/foreground/geometry;
3. test title-bar Role ID resolution as a configurable hypothesis, never as an implicit identity source;
4. classify exact match, mismatch, unavailable identity and duplicate identity;
5. prove the InputGuard invariant: no input unless identity, runtime session and kill-switch conditions are valid;
6. record limitations for foreground/background/minimized capture/input behavior.

## Probe boundaries

- No memory read/write, DLL injection, binary patching or anti-cheat bypass.
- No full UI, task automation, recorder, scheduler, party or sync implementation.
- No OS input is emitted by the P0 probe. It only evaluates and logs the guard decision.
- A missing Role ID, duplicate Role ID, stale PID/HWND, changed title or uncertain state fails closed.

## Evidence required from a real qnyh run

- process/window inventory for every discovered instance;
- raw title and configured resolver result;
- registered Client ID list with secrets omitted;
- match/mismatch/duplicate/unresolved decision and reason;
- foreground, background and minimized observations;
- close/reopen, PID/HWND change, delayed title/Role ID and recreated-window observations;
- InputGuard decision for allowed candidate and every blocked case;
- known limitations and a clear P0 RESULT before P1 is considered.

## Current implementation

`src/AutoGhost.P0Probe` is a Windows-only .NET 8 console probe. It is intentionally small and dry-run only so the discovery evidence can be collected without activating automation.

## DEC-002 refinement — system-wide window discovery

The probe must not rely on `qnyh.exe.MainWindowHandle` or on a direct
`qnyh.exe` process-to-window assumption. It now enumerates every top-level
window visible to the current Windows session, records the owner PID and
window metadata, and maps each PID through a Toolhelp process snapshot.

For the qnyh process tree it records:

- target/child/ancestor relation;
- parent PID and process ancestry;
- process name and best-effort executable path;
- HWND, title, class, visibility, enabled state, owner/parent HWND and thread;
- window/client rectangles, minimized/cloaked state and foreground state.

Only windows in the qnyh process tree are eligible for identity classification
and binding. All system-wide window candidates remain in the JSON evidence so
that a visible game window owned by a launcher, child or helper can be traced
before any binding decision is made. Unknown or ambiguous identity remains
fail-closed, and the probe continues to emit no OS input.
