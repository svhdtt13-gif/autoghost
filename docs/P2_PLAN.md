# AutoGhost V1 — P2 Plan

Status: **AUTHORIZED / IMPLEMENTING**

## Scope

P2 adds the recorder and action model only:

- foreground-only mouse and keyboard observation for one already verified Client ID;
- normalized client-area coordinates and delay-before-action timing;
- JSON action definitions that contain Client ID but never PID/HWND/session identity;
- semantic/vision hook definitions as an extension point, not an unverified game-state reader;
- replay dry-run with identity guard, pause, stop, and Kill Switch suppression.

## Safety invariants

- Recording starts only when the selected row is `READY`, `ObservedRoleId == ClientId`, the HWND is valid, and that exact HWND is foreground.
- Every captured callback rechecks the foreground HWND and the live Client ID/HWND binding.
- Injected low-level input is ignored by the recorder.
- P2 has no live dispatcher. Dry-run never calls `SendInput`, `PostMessage`, a driver, an injection API, or a game memory API.
- Action files persist only action definitions and `TargetClientId`; PID/HWND/session are runtime-only.
- Any mismatch, stale/reused HWND, invalid action definition, cancellation, or Kill Switch request fails closed.

## Verification gate

Automated verification is complete for the action model and dry-run engine. A real qnyh foreground recording acceptance is still required before marking P2 `DONE`.
