# Qnyh UI Automation Tool

## Mục tiêu

Xây dựng một ứng dụng Windows độc lập hỗ trợ người dùng điều khiển các client `qnyh.exe` được chọn để thực hiện các nhiệm vụ ngày/tuần bằng UI automation nhìn thấy trên màn hình. Nhiệm vụ thuộc một catalog hữu hạn, nhưng thời điểm mở, vị trí, ngôn ngữ, skin và bố cục có thể thay đổi.

Ứng dụng chỉ tham khảo cấu trúc nhiệm vụ, lịch, profile và party của AutoGhostStory/360Auto. Không sao chép cơ chế nội bộ, không phụ thuộc vào phần mềm đó và không sử dụng token/session của phần mềm đó.

## Phạm vi chức năng

### FR-001: Phát hiện client
Ứng dụng MUST phát hiện các cửa sổ thuộc tiến trình `qnyh.exe` và hiển thị tối thiểu PID, handle, tiêu đề, kích thước và trạng thái quan sát được.

### FR-002: Chọn client
Người dùng MUST có thể chọn một hoặc nhiều client để chạy; client không được chọn MUST NOT nhận thao tác.

### FR-003: Profile giao diện
Ứng dụng MUST hỗ trợ profile theo ngôn ngữ, skin, bố cục và tỷ lệ cửa sổ. Profile MUST dùng selector/anchor/template/OCR có thể thay thế, không phụ thuộc một tọa độ tuyệt đối duy nhất.

### FR-004: Catalog và nhận diện nhiệm vụ
Ứng dụng MUST chuẩn hóa các nhiệm vụ hữu hạn thành `questId`, hỗ trợ alias đa ngôn ngữ, icon, mục tiêu, điểm đến và dấu hiệu hoàn thành. Vị trí hoặc nhiệm vụ thay đổi mỗi ngày MUST được đọc từ trạng thái UI hiện tại.

### FR-005: Lịch ngày/tuần
Ứng dụng MUST hỗ trợ lịch daily/weekly, giờ mở, giờ hết hạn, múi giờ `Asia/Ho_Chi_Minh`, lịch sử đã nhận thưởng và thứ tự ưu tiên do người dùng cấu hình. Khi lịch trùng nhau, nhiệm vụ ưu tiên cao hơn MUST được chọn trước.

### FR-006: UI automation an toàn
Ứng dụng MUST chỉ chụp màn hình, nhận diện và gửi thao tác chuột/phím qua lớp UI automation. Ứng dụng MUST NOT đọc/ghi bộ nhớ game, inject DLL, sửa binary, giả mạo giao thức game hoặc bypass anti-cheat.

### FR-007: State machine và fail-safe
Mỗi hành động MUST có precondition, timeout, retry giới hạn và post-action verification. Chế độ `observation` và `dry-run` MUST không gửi thao tác thật. Khi độ tin cậy nhận diện thấp hoặc trạng thái chưa biết, client MUST dừng an toàn.

### FR-008: Party coordination
Ứng dụng MUST hỗ trợ nhóm leader/follower cho các bước lập party, sẵn sàng, di chuyển và phối hợp gameplay trong catalog nhiệm vụ. Các client cùng nhiệm vụ có thể dùng chung task plan nhưng vẫn phải xác nhận UI riêng.

### FR-009: Dừng toàn nhóm
Một lỗi fatal của bất kỳ client nào trong party MUST chuyển cả nhóm sang `SAFE_STOP`, ngừng các thao tác mới và ghi rõ nguyên nhân. Nhóm chỉ được tiếp tục sau thao tác resume rõ ràng của người dùng.

### FR-010: Lịch sử và quan sát
Ứng dụng MUST ghi log có cấu trúc cho session, client, quest, checkpoint, hành động, kết quả, retry và lý do dừng. Log MUST không chứa token, cookie, session hoặc password. Ảnh lỗi MUST có tùy chọn redact.

### FR-011: Cấu hình độc lập
Schema nhiệm vụ, lịch, profile và party của tool MUST là cấu hình riêng, có version và validation. Việc tham khảo AutoGhostStory/360Auto chỉ là khảo sát read-only và không được làm dependency runtime.

### FR-012: Đóng gói
Ứng dụng MUST có hướng dẫn chạy, calibration, dry-run và chạy một client trước. Sau khi MVP ổn định, MUST có cấu hình đóng gói Windows `.exe` reproducible.

## Tiêu chí kịch bản

### SC-001: Quan sát client
Khi chạy observation, ứng dụng liệt kê các client `qnyh.exe`, không click hoặc gửi phím, và cho phép chọn client.

### SC-002: Dry-run nhiệm vụ
Khi chạy dry-run, ứng dụng nhận diện nhiệm vụ và mô phỏng state transition nhưng không gửi thao tác thật; log phải ghi rõ mọi hành động là mô phỏng.

### SC-003: Giao diện biến thể
Khi cùng một nhiệm vụ xuất hiện với ngôn ngữ, skin hoặc bố cục khác, profile phù hợp phải được chọn hoặc ứng dụng phải dừng an toàn nếu không đủ confidence.

### SC-004: Lịch xung đột
Khi hai nhiệm vụ cùng mở, scheduler chọn nhiệm vụ theo thứ tự ưu tiên người dùng cấu hình và không chạy lại nhiệm vụ đã hoàn thành.

### SC-005: Party fail-stop
Khi một member không ready, mất cửa sổ, timeout hoặc gặp trạng thái không biết, coordinator phát lệnh dừng logic cho toàn nhóm và lưu checkpoint lỗi.

## Ngoài phạm vi MVP

- Tự xử lý nhiệm vụ chưa có trong catalog.
- Đọc memory hoặc can thiệp tiến trình game.
- Điều khiển trực tiếp qua protocol riêng của AutoGhostStory/360Auto.
- Chạy song song không giới hạn hoặc tự bỏ qua lỗi party.

## Phi chức năng

- Nền tảng mục tiêu: Windows 10/11.
- Ưu tiên chạy tuần tự ở MVP để tránh focus contention.
- Mọi thao tác active phải có kill switch và giới hạn thời gian.
- Test logic phải chạy được không cần mở game thật.
- Credential phải nằm ngoài source/config mẫu và được redacted khỏi log.
