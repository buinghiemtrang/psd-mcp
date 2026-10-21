---
name: psd-to-ugui
description: Ráp hoặc cập nhật UI prefab uGUI trong Unity từ file PSD của art, dùng MCP psd2ui (psd_*) và các tool Unity match_layout_spec / apply_layout_spec / render_prefab. Dùng khi user đưa file .psd (kèm JPG) và nói ráp màn, cập nhật UI theo PSD, so prefab với design, xuất sprite từ PSD.
---

# Ráp UI Unity từ PSD

Người dùng chỉ cần nói kiểu: "Ráp màn Victory từ `<file>.psd` (ảnh `<file>.jpg`) vào prefab `Assets/.../X.prefab`". Phần còn lại Claude làm theo các bước dưới, hỏi lại đúng những chỗ cần người quyết.

## Kiểm tra trước
1. Có tool `psd_tree` (MCP psd2ui). Không có: báo cài plugin psd-mcp (README).
2. Có tool `match_layout_spec` (unityMCP + package `com.trangbn.psd-layout`). Không có: vẫn làm được bước đọc / xuất PSD, báo cài package Unity.
3. Thiếu đường dẫn PSD, prefab đích, hoặc thư mục sprite mới: hỏi một lần, gom thành danh sách đánh số.

## Các bước
1. `psd_tree(path, depth=2)`: xem cây, font, fx. Đọc kết quả gọn, không in lại cho user.
2. `psd_states(path)`: layer ẩn trùng chỗ = state khác (disabled, bản cũ). Tên trùng / không rõ state nào là bản mới: hỏi user.
3. `psd_to_spec(path, out_dir=<scratchpad>/spec_<màn>, prefab=..., jpg=<ảnh art>, rules=...)`.
   - Có JPG thì luôn truyền: psd-tools không vẽ GradientOverlay / InnerGlow của chữ, màu lấy từ JPG.
   - `rules`: `flatten` (gộp group thành 1 ảnh), `button`, `image_text` (chữ cố định xuất thành ảnh), `dynamic` / `static`, `skip`.
4. `psd_match_sprites(sprite_dirs=[thư mục sprite UI của project, tuyệt đối], spec=...)`: dùng lại sprite cũ khi giống, tránh thêm sprite trùng.
5. `match_layout_spec(spec)`: ghép node với object prefab. Node điểm thấp hoặc "chưa có": đọc lại, sửa `target` trong spec bằng tay, hoặc hỏi user.
6. Ghi `sprite_dir` vào spec nếu có sprite mới (hỏi user nếu chưa biết).
7. `apply_layout_spec(spec, dry_run=true)`: tóm tắt danh sách thay đổi cho user. Node cần tạo mới (`create_new`) phải hỏi trước.
8. User đồng ý → `apply_layout_spec(spec, dry_run=false)`. Sau đó `refresh_unity` + `read_console` lọc Error.
9. So ảnh: `psd_render(path, out, hide=[layer nền])` và `render_prefab(prefab, out, states={"Panel": true, "Panel/Dim": false})` → `psd_compare`. Chỉ đọc ảnh ghép đã thu nhỏ. Lệch thì sửa spec, lặp 7–9.
10. Kết luận ngắn: đã đổi gì, sprite mới nào, object mới nào cần gán `[SerializeField]`, user cần Play chỗ nào.

## Quy tắc
- Chỉ cập nhật prefab đang có: giữ anchor, pivot, reference script, Animator. Không tạo UI bằng code lúc chạy.
- Prefab Mode đang mở đúng prefab và có thay đổi chưa lưu: dừng, nhắc user lưu hoặc đóng.
- Không copy `.meta` từ nơi khác. Không ghi đè sprite cũ (PNG mới mang hậu tố `_v2`).
- Chữ có số / nội dung đổi lúc chạy → TMP (`dynamic`). Chữ cố định đã có ảnh chữ → giữ ảnh.
- File tạm (spec, PNG, ảnh render) để ở scratchpad, xoá sau khi xong.
- Ảnh chênh còn đỏ ở chữ động (số đếm, giá) là bình thường.
