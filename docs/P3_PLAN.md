# AutoGhost P3 Plan

Status: P3 FINAL PASS — REVIEWED IN ISSUE #1

P3 scope is the task engine and scheduler from Issue #1:

- task definitions with steps, priorities, dependencies, schedules, retries, timeouts, and recovery;
- deterministic daily/weekly due-task selection;
- explicit task state transitions and persisted task history;
- dry-run execution and verification boundaries.
- V1 follow-up: operator-provided special-item lists, stored per client, compared against the captured Vận chuyển item names before any client-specific warning.
- Transport note/history is keyed by the exact `(RoleId, LocalDate)` pair; same-key saves replace one record atomically and never merge clients.
- `SpecialItems` is operator-provided per client. There are no built-in game-item defaults.

Safety invariants:

- P3 is dry-run only. The engine refuses live execution.
- No SendInput, PostMessage, replay, dispatcher, memory access, injection, or bypass is added.
- Kill Switch active always blocks execution.
- Task history stores task/run/step state only; PID, HWND, and session metadata are not persisted.
- P3 does not authorize P4 or later work.
- An empty/unconfigured special-item list produces a pending comparison state and no inferred alert.
- A canonical transport note requires exactly eight captured item requirements before persistence.

Acceptance target:

1. Scheduler filters disabled, not-due, and dependency-blocked tasks.
2. Due tasks are deterministic: priority descending, then task ID.
3. Dry-run executes a trace with all dispatch suppressed.
4. Verification failure retries from the beginning within the configured budget.
5. Timeout, cancellation, failure, and blocked states are persisted.
6. JSON history survives reload through an atomic file replacement.
7. Transport note/history reloads by exact `RoleId + LocalDate`, including same-key replacement and cross-client isolation.
8. Special Items covers `PendingUserList`, `Match`, and `NoMatch` without hard-coded item names.
9. Acceptance covers Role `3488404011` and Role `16711204011` with no note/history/alert leakage.
