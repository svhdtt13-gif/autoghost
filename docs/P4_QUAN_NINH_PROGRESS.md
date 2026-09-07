# P4 Quan Ninh + Scheduler

Status: `AUTHORIZED / IN PROGRESS`

This slice implements and tests the deterministic scheduler foundation for the
Quan Ninh task. It does not claim that qnyh-specific live UI semantics have
been discovered yet.

## Authorized scope

- Allowed days: Monday, Wednesday, Friday, and Sunday.
- Registration windows: `12:00–13:00` and `20:00–21:00` in Vietnam local time.
- Window end is exclusive: `13:00` and `21:00` are outside the window.
- Limit: at most three verified successful registrations per client per local
  calendar day.
- Failed attempts, abandoned tasks, and UNKNOWN states do not consume quota.
- History persists by exact `ClientId + Vietnam LocalDate` and is reloaded
  after restart.

## Live qnyh gates

The scheduler refuses registration input until both gates have verified live
evidence:

- `Q-QN-001`: observe and recognize the valid Quan Ninh registration slot.
- `Q-QN-002`: observe and verify the qnyh registration-success state.

Current state for both gates: `PENDING_LIVE_OBSERVATION`.

The live interaction guard additionally requires an exact Client ID/Role ID
match, a non-zero HWND, the exact target in the foreground, Kill Switch off,
and automation explicitly armed. No qnyh click or registration input is part
of this commit.

## Verification

The local P4 test project covers policy boundaries, Vietnam timezone/date
conversion, success-only quota accounting, restart persistence, client
isolation, fail-closed live gates, and input guards. The existing P0–P3
regression suite is run alongside it by PLAN V1 CI.

The current read-only qnyh probe found visible Unity targets, but the live
Role `16711204011` scan was not a unique safe target. Therefore no physical
input was sent and no raw qnyh title or credential data was stored in
evidence.

Provider login, token, or Auth0 access is not a runtime dependency of this
window-management/vision workflow. Provider-side credential rotation remains
a separate security follow-up.

P5 and later phases remain unauthorized.
