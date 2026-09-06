# P3 Canonical — Review Package

Date: 2026-09-06
Review status: P3 FINAL PASS — officially accepted in Issue #1 comment #5559678616
Decision basis: DEC-026 / Issue #1
Source flow: [SƠ ĐỒ.docx](https://github.com/svhdtt13-gif/autoghost/blob/main/S%C6%A0%20%C4%90%E1%BB%92.docx)

## Scope and target

- Phase: P3 Canonical Vận chuyển.
- Target process: `qnyh.exe`.
- Role ID: `3488404011`.
- HWND: `10885552`.
- Observed title: `Ghost Story Engine [3.739133] Version [3.739133] Server [S1 - Yixiao Naihe] Role [3488404011]`.
- Capture scale after foreground recovery: `1920x1032`.

This package records a supervised Computer Use verification. The AutoGhost P3 application implementation remains dry-run only; this run does not claim that the app emitted live input.

## Canonical state progression

1. Opened `Hoạt Động`; transport task showed `0/1` and the already-received/failed branch was visible.
2. Opened the failed transport detail and observed the instruction to abandon the failed task.
3. Selected `Bỏ nhiệm vụ`, confirmed the abandon dialog, and verified the `Đã bỏ nhiệm vụ` result.
4. Reopened `Hoạt Động`, selected `Tham gia`, and verified teleport to `Xóm Lá`.
5. Selected the NPC option `[Bang] Vận Chuyển` only; excluded `[Bảo Thường] Gửi Hàng` and `Xem đơn hàng`.
6. Continued the NPC dialogue once; verified the `Chuyển hàng` screen and the complete 8-slot item list.
7. Selected each item slot read-only to capture its name and required quantity. No item was purchased or inserted.

## Item list captured for this client

| Slot | Item | Required | Observed |
|---:|---|---:|---|
| 1 | Trân Châu | 2 | 0/2 |
| 2 | Hồng Hoa Hoàn | 20 | 0/20 |
| 3 | Xích Thước Hoàn | 20 | 0/20 |
| 4 | Bánh Củ Hoàng | 20 | 0/20 |
| 5 | Đào Hoa Thạch | 1 | 0/1 |
| 6 | Lăng Tiêu Hoàn | 20 | 0/20 |
| 7 | Canh Ngũ Sắc | 20 | 0/20 |
| 8 | Gà Hoa Tuyết | 5 | 0/5 |

## Special-item comparison

Status: `PENDING_USER_SPECIAL_ITEM_LIST`.

No special-item list exists yet. The current run therefore emits no special-item alert and does not infer that any captured item is special. After V1 is complete, the user will provide the list in the app; comparison must be performed per client before issuing a client-specific warning.

## Safety result

- Navigation and the documented failed-task recovery branch were performed under supervised guards.
- No `Mua Và Đưa Vào` action was taken.
- No `Cầu viện` action was taken.
- No item was inserted/submitted.
- No quest was turned in/returned.
- No P4+ action was performed.
- Current state: `SAFE_STOP` after item-list capture.

## Evidence files

- [Per-client item note](../artifacts/p3-canonical-role-3488404011-transport-items-20260906.json)
- [Full evidence manifest](../artifacts/p3-canonical-role-3488404011-evidence-20260906.json)
- [Final Chuyển hàng frame](../artifacts/p3-canonical-role-3488404011-after-transport-dialogue-20260906.jpg)
- Per-slot hậu kiểm: `artifacts/p3-canonical-role-3488404011-scan-item-02-20260906.jpg` through `scan-item-08-20260906.jpg`.

## Review request

Please review:

1. Whether the observed state progression matches the B1 flow.
2. Whether all eight names and quantities are transcribed correctly.
3. Whether the pending special-item status is correct while the user list is not yet available.
4. Whether the safety boundary and final `SAFE_STOP` are correctly recorded.

## Official acceptance

P3 Canonical Vận chuyển was officially accepted as `FINAL PASS` in Issue #1 comment `#5559678616`. The remaining gates passed: exact `RoleId + LocalDate` note/history persistence, per-client Special Items comparison with `PendingUserList / Match / NoMatch`, and isolation between Roles `3488404011` and `16711204011` with leakage `false`.

The real user-provided Special Items list is still absent by design; no live client-specific alert is inferred until the user supplies it. This commit publishes the approved P3/P3.1 evidence package. Source implementation is not claimed as audited from the GitHub default branch; P4+ remains unauthorized.
