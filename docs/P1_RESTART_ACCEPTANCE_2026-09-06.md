# P1 Restart Acceptance — 2026-09-06

Status: **PASS**

The acceptance was executed against the real WPF UI in interactive session 1 with the real `qnyh.exe` clients running.

## Evidence

- Before-restart UI: [p1-restart-before-configured.png](../artifacts/p1-restart-before-configured.png)
- After-restart UI: [p1-restart-acceptance-after.png](../artifacts/p1-restart-acceptance-after.png)
- Before-restart runtime JSON: [p1-restart-before-runtime.json](../artifacts/p1-restart-before-runtime.json)
- After-restart runtime JSON: [p1-restart-acceptance-after-runtime.json](../artifacts/p1-restart-acceptance-after-runtime.json)
- WPF lifecycle/restore/rebind log: [p1-wpf-startup.log](../artifacts/p1-wpf-startup.log)
- Persisted registry: `%LOCALAPPDATA%\\AutoGhost\\client-registry.json`

## Acceptance results

| Check | Result | Evidence |
| --- | --- | --- |
| Visible WPF window on interactive desktop | PASS | Before host PID `29572`, HWND `0x4015D8`; after host PID `21352`, HWND `0x4215D8`; both `isVisible=True`, `hwndNonZero=True`. |
| Registry restore after restart | PASS | `Registry.Restore` logged two configured IDs and `enabledCount=2`. |
| Role ID rebind after rediscovery | PASS | `Runtime.Rebind` logged `bindings=2`, `uniqueClientIds=2`, both states `READY`. |
| Exact live identities | PASS | `16711204011 → PID 10132 / HWND 0x841636`; `3488404011 → PID 45120 / HWND 0xA619B0`. |
| No stale/duplicate client rows | PASS | After UI shows exactly two enabled rows; runtime log reports two unique Client IDs. |
| Kill Switch startup state | PASS | UI and restore log show `KILL SWITCH: ON`. |
| Automation startup state | PASS | UI shows `DISARMED`; restore log shows `automationArmed=False`. |
| PID/HWND persistence safety | PASS | Registry contains configuration and Client IDs only; host PID/HWND changed across restart and were rediscovered at runtime. |

## Gate decision

```text
P0 PASS
P1 PASS
P2 READY FOR AUTHORIZATION
```

No P2 implementation, task automation, input dispatcher, driver, injection, memory access, or bypass work was started.
