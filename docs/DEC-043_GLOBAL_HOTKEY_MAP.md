# DEC-043 Global Hotkey Map

Status: `ACTIVE`

The qnyh panel toggles are canonical for every AutoGhost feature:

| Key | Panel |
| --- | --- |
| `M` | Bản đồ |
| `B` | Túi |
| `X` | Chat |
| `H` | Bảng Hoạt Động |
| `K` | Bảng Skill |
| `L` | Trạng thái nhiệm vụ |
| `I` | Phúc lợi |
| `Y` | Chợ / Tiệm cá nhân |
| `T` | Party |

These keys are toggles, not a normalization macro. A live executor must:

1. bind and verify the exact target;
2. capture the current full frame and identify the currently open panel;
3. if one panel is observed, send only that panel's canonical close key;
4. capture and verify the main frame, Hàng Châu, and main-world view in order;
5. stop on `UNKNOWN`, an inconsistent observation, or a failed post-action verification.

When the frame is already a verified Hàng Châu main-world view, normalization
must emit no panel toggle. No travel key is inferred by this mapping.

For the task flow, the first feature-specific action is `H` only after the
global checks pass. The app must then verify that Bảng Hoạt Động is open before
looking for a task; it must not assume the board was already open.

P4 live qnyh discovery remains subject to DEC-042. P5 and later phases remain
unauthorized.
