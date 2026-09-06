## Historical STATUS UPDATE

This file records the earlier P0 probe checkpoint. The current source/status
manifest is `docs/PLAN_V1_IMPLEMENTATION_MANIFEST_DEC032.md`; later live
acceptance evidence and final phase status must be read there and in Issue #1.

Phase: P0
State: VERIFYING

### Đã làm

- Tạo probe Windows/.NET 8 greenfield, chỉ trong phạm vi P0.
- Enumerate process theo executable name và map process -> top-level window bằng PID/HWND.
- Ghi PID, HWND, process name, title, window class, visible/minimized/foreground và geometry query.
- Implement title-bar Role ID resolver dạng hypothesis có regex cấu hình; không có regex thì raw title chỉ là evidence, không bind.
- Implement identity classification: MATCHED, MISMATCH, ROLE_ID_UNAVAILABLE, DUPLICATE_IDENTITY.
- Implement `BindingReconciler` cho initial bind, same-session verify, PID/HWND đổi, window recreate, delayed Role ID và changed Role ID.
- Implement `InputGuard`: chỉ cho phép guarded input khi identity đã verify, `RoleId == ClientId`, session PID/HWND còn khớp, binding READY, kill switch OFF. P0 executable không phát OS input.

### File/Module thay đổi

- `src/AutoGhost.P0Probe/Win32WindowInventory.cs`
- `src/AutoGhost.P0Probe/RoleIdResolver.cs`
- `src/AutoGhost.P0Probe/BindingEngine.cs`
- `src/AutoGhost.P0Probe/InputGuard.cs`
- `src/AutoGhost.P0Probe/Models.cs`
- `tests/AutoGhost.P0Probe.Tests/Program.cs`
- `docs/P0_PLAN.md`
- `docs/P0_RESULT.md`
- `artifacts/p0-scan-2026-09-05.json`

### Kiểm thử thực tế

- Test: `dotnet build AutoGhost.sln --configuration Release`
- Kết quả: PASS, 0 warning, 0 error.
- Test: `dotnet run --project tests/AutoGhost.P0Probe.Tests/AutoGhost.P0Probe.Tests.csproj --configuration Release`
- Kết quả: `P0 smoke tests: PASS (6/6)`.
- Coverage: title hypothesis, exact match/mismatch, duplicate identity, kill switch, dry-run guard, stale PID/HWND rejection, PID/HWND rebind tạo session ID mới, delayed/changed Role ID.
- Runtime command: probe `qnyh.exe` với `--dry-run --json artifacts/p0-scan-2026-09-05.json`.
- Evidence: `qnyh.exe` không chạy trên máy probe tại thời điểm test; phát hiện 0 window, 0 READY binding. Probe đã ghi warning và không claim runtime PASS.

### Chưa làm / chưa được claim

- Chưa có evidence thật với nhiều `qnyh.exe`.
- Chưa xác minh title format/parse Name + Role ID + Server trên client thật.
- Chưa xác minh foreground/background/minimized capture/input trên qnyh thật.
- Chưa phát OS input; P0 chỉ đánh giá guard decision.
- Chưa sang P1/full UI/task automation.

### Blocker / cần quyết định

- Cần chạy probe khi các instance `qnyh.exe` thật đang mở và đã login, kèm Client Registry không phải placeholder.
- Cần xác nhận format title bar thực tế để cấu hình regex hoặc mở Q-ROLE-001.
- Workspace hiện không cho tạo `.git/index.lock`, nên chưa tạo được local commit/hash; source và evidence đang nằm trong workspace.

### Next

1. User mở các client qnyh cần test và cung cấp/confirm title format + Client IDs.
2. Chạy lại P0 probe foreground/background/minimized và race/rebind matrix.
3. Đính kèm JSON/log/screenshot thực tế.
4. Chỉ khi identity + bind/rebind + input/capture đạt gate mới đề xuất chuyển P1.

Kết luận hiện tại: P0 implementation/smoke verification PASS; **P0 real qnyh gate vẫn VERIFYING, P1 chưa được phép**.
