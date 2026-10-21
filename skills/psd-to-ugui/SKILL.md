---
name: psd-to-ugui
description: Ráp UI uGUI trong Unity từ file PSD của art, dùng MCP psd2ui (psd_*) và các tool Unity build_layout_spec / match_layout_spec / apply_layout_spec / render_prefab. Ba trường hợp - cập nhật prefab đã có, dựng prefab mới, dựng hoặc cập nhật UI nằm thẳng trong scene. Dùng khi user đưa file .psd (kèm JPG) và nói ráp màn, làm UI mới theo PSD, cập nhật UI theo PSD, so UI với design, xuất sprite từ PSD.
---

# Ráp UI Unity từ PSD

Người dùng chỉ cần nói kiểu: "Ráp màn Victory từ `<file>.psd` (ảnh `<file>.jpg`) vào prefab `Assets/.../X.prefab`", "Làm UI mới từ `shop.psd`", hoặc "Dựng `shop.psd` vào Canvas của scene `Lobby`". Phần còn lại Claude làm theo các bước dưới, hỏi lại đúng những chỗ cần người quyết.

## Kiểm tra trước
1. Có tool `psd_tree` (MCP psd2ui). Không có: báo cài plugin psd-mcp (README).
2. Có tool `match_layout_spec` (unityMCP + package `com.trangbn.psd-layout`). Không có: vẫn làm được bước đọc / xuất PSD, báo cài package Unity.
3. Xác định đích, thiếu thì hỏi một lần (danh sách đánh số):
   - **Cập nhật**: prefab đã có, hoặc object UI đã có trong scene (`scene` + đường dẫn object, vd. `Canvas/ShopPanel`).
   - **Dựng mới**: đường dẫn prefab mới, hoặc scene + object cha (vd. `Canvas`).
   - Thư mục sprite mới (`sprite_dir`) nếu có sprite chưa có trong project.

## Bước chung
1. `psd_tree(path, depth=2)`: xem cây, font, fx. Đọc kết quả gọn, không in lại cho user.
2. `psd_states(path)`: layer ẩn trùng chỗ = state khác (disabled, bản cũ). Tên trùng / không rõ state nào là bản mới: hỏi user.
3. `psd_to_spec(path, out_dir=<scratchpad>/spec_<màn>, jpg=<ảnh art>, prefab=... | scene=..., scene_root=..., rules=...)`.
   - Có JPG thì luôn truyền: psd-tools không vẽ GradientOverlay / InnerGlow của chữ, màu lấy từ JPG.
   - `rules`: `flatten` (gộp group thành 1 ảnh), `button`, `image_text` (chữ cố định xuất thành ảnh), `dynamic` / `static`, `skip`.
4. `psd_match_sprites(sprite_dirs=[thư mục sprite UI của project, tuyệt đối], spec=...)`: dùng lại sprite cũ khi giống.
5. Ghi `sprite_dir` vào spec nếu còn sprite mới.

## A. Cập nhật UI đã có (prefab hoặc scene)
1. `match_layout_spec(spec)`: ghép node với object. Node điểm thấp hoặc "chưa có": sửa `target` trong spec bằng tay, hoặc hỏi user.
2. `apply_layout_spec(spec, dry_run=true)`: tóm tắt thay đổi cho user. Node cần tạo mới (`create_new`) phải hỏi trước.
3. User đồng ý → `apply_layout_spec(spec, dry_run=false)`.

## B. Dựng UI mới (prefab mới hoặc vào scene)
1. Đổi `name` các node vô nghĩa ("Layer 28 copy", "Group 3") theo vai trò (`Band`, `Title`, `BtnWatchAd`…) ngay trong spec; liệt kê tên mới cho user xem.
2. `build_layout_spec(spec, prefab=Assets/....prefab | scene=..., parent=Canvas, name=..., dry_run=true)`: xem cây sẽ tạo + anchor tự suy (1/3 trên neo trên, 1/3 dưới neo dưới, rộng ≥ 90% thì stretch ngang). Project có `UIFitHeight` thì tool bọc trong `Content` W×H neo mép trên.
3. User đồng ý → chạy với `dry_run=false`. Spec được ghi `target` + đích, lần sau art sửa PSD thì đi nhánh A.

## Sau khi áp / dựng
1. `refresh_unity` + `read_console` lọc Error.
2. So ảnh: `psd_render(path, out, hide=[layer nền])` hoặc dùng thẳng JPG art, với `render_prefab(prefab=... | scene=..., root=..., out, states={...})` → `psd_compare`. Chỉ đọc ảnh ghép đã thu nhỏ. Lệch thì sửa spec, lặp lại.
3. Kết luận ngắn: đã đổi / tạo gì, sprite mới nào, object nào cần gán `[SerializeField]`, user cần Play chỗ nào. Scene: nhắc user xem rồi tự Save (tool không Save scene, Ctrl+Z để hoàn tác).

## Quy tắc
- Cập nhật thì giữ anchor, pivot, reference script, Animator. Không tạo UI bằng code lúc chạy.
- Prefab Mode đang mở đúng prefab và có thay đổi chưa lưu: dừng, nhắc user lưu hoặc đóng.
- Object trong scene thuộc prefab instance: sửa prefab gốc (tool báo đường dẫn), không sửa trong scene.
- Không copy `.meta` từ nơi khác. Không ghi đè sprite cũ (PNG mới mang hậu tố `_v2`).
- Chữ có số / nội dung đổi lúc chạy → TMP (`dynamic`). Chữ cố định đã có ảnh chữ → giữ ảnh.
- File tạm (spec, PNG, ảnh render) để ở scratchpad, xoá sau khi xong.
- Ảnh chênh còn đỏ ở chữ động (số đếm, giá) và nền lobby là bình thường.
