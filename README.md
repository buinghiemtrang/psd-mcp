# psd-mcp: PSD → Unity uGUI với Claude Code

Đưa file PSD của art cho Claude, Claude làm UI uGUI theo đúng design: vị trí, kích thước, sprite, font, màu chữ, rồi render ra ảnh để so với PSD. Làm được cả 3 trường hợp:

- **Cập nhật prefab đã có** theo PSD mới (giữ script, anchor, Animator).
- **Dựng prefab mới** khi UI chưa có: tạo object, sprite, chữ, tự suy anchor.
- **UI nằm thẳng trong scene**: dựng mới hoặc cập nhật, có Undo, bạn tự Save.

Bạn không cần nhớ tên tool. Cài xong, chỉ cần nói:

> Ráp màn Victory từ `D:/design/victory.psd` (ảnh `victory.jpg`) vào prefab `Assets/UI/Victory.prefab`.

> Làm UI mới từ `shop.psd`, lưu `Assets/UI/Shop.prefab`.

> Dựng `shop.psd` vào Canvas của scene `Lobby`.

Claude đọc PSD, cho bạn xem danh sách thay đổi (hoặc cây object sẽ tạo) trước, bạn đồng ý mới lưu.

*English: a Claude Code plugin (Python MCP server + skill) plus a Unity package. It reads PSD layers, exports alpha-trimmed sprites and builds a layout spec, then updates an existing uGUI prefab, builds a new prefab, or builds / updates UI directly in a scene (with Undo), applying rects, sprites, TMP fonts, colors and auto anchors, and renders the result for a side-by-side diff with the PSD.*

## Gồm những gì

| Phần | Ở đâu | Việc |
| --- | --- | --- |
| MCP server `psd2ui` | `psd_mcp/` (Python) | Đọc PSD, xuất sprite, sinh layout spec, so ảnh |
| Skill `psd-to-ugui` | `skills/` | Dạy Claude quy trình ráp UI, chỗ nào phải hỏi bạn |
| Package Unity | `unity/` | 4 tool trong Editor: dựng UI mới, ghép node, áp spec, render (prefab hoặc scene) |

## Cần có

- [Claude Code](https://claude.com/claude-code)
- [uv](https://docs.astral.sh/uv/) (chạy server Python, tự cài thư viện lần đầu)
- Unity 2022.3 trở lên, có [unityMCP](https://github.com/CoplayDev/unity-mcp) (`com.coplaydev.unity-mcp`) đã kết nối với Claude Code. Thiếu unityMCP thì vẫn dùng được phần đọc / xuất PSD.

## Cài đặt

**1. Plugin Claude Code** (MCP server + skill):

```bash
claude plugin marketplace add buinghiemtrang/psd-mcp
claude plugin install psd-mcp@psd-mcp-marketplace
```

Mở lại Claude Code, gõ `/mcp` thấy server `psd2ui` là xong.

**2. Package Unity**: Package Manager → `+` → Add package from git URL:

```
https://github.com/buinghiemtrang/psd-mcp.git?path=/unity
```

Xong. Không cần config: font chữ tự chọn theo tên font trong PSD (vd. `UTM-Eremitage` → `UTM EREMITAGE SDF`), thư mục sprite mới Claude sẽ hỏi.

**Tuỳ chọn:** muốn ép font / material cụ thể hoặc cố định thư mục sprite thì Import sample "Config mẫu" (Package Manager → PSD Layout → Samples), đặt `PsdLayoutConfig.json` trong `Assets/` và sửa `fonts`, `sprite_dir`, `import_like`.

## Dùng thế nào

Nói với Claude bằng lời thường. Ví dụ:

| Bạn nói | Claude làm |
| --- | --- |
| "Xem file `shop.psd` có những gì" | Liệt kê layer, font, cỡ chữ, màu, layer ẩn |
| "Xuất các nút trong `shop.psd` ra PNG" | PNG đã cắt sát alpha + toạ độ |
| "Ráp `shop.psd` vào `Assets/UI/Shop.prefab`" | Cập nhật prefab đã có theo PSD |
| "Làm UI mới từ `shop.psd`, lưu `Assets/UI/Shop.prefab`" | Dựng prefab mới: object, sprite, chữ, anchor |
| "Dựng `shop.psd` vào Canvas của scene `Lobby`" | Dựng thẳng trong scene (có Undo, bạn tự Save) |
| "So prefab Shop với PSD" | Ảnh ghép PSD, Unity, chỗ lệch |

Quy trình ráp (Claude tự chạy, bạn chỉ trả lời khi được hỏi):

1. Đọc PSD, tìm layer ẩn (state disabled, bản cũ).
2. Sinh layout spec: mỗi layer thành ảnh, chữ (TMP) hoặc nút; màu chữ gradient lấy từ JPG.
3. Tìm sprite cũ giống để dùng lại, tránh thêm sprite trùng.
4. UI đã có: ghép từng node với object theo vị trí, tên, nội dung chữ, sprite. UI mới: đặt tên object theo vai trò, tự suy anchor.
5. **Chạy thử**: liệt kê thay đổi / cây sẽ tạo. Bạn xem rồi đồng ý.
6. Lưu prefab (giữ anchor, pivot, script, Animator), hoặc đổi trong scene để bạn xem rồi tự Save.
7. Render, so với PSD, chỉnh nếu lệch.

## Giới hạn

- psd-tools không vẽ được GradientOverlay / InnerGlow của chữ. Hãy kèm ảnh JPG art xuất để lấy đúng màu.
- Anchor của UI dựng mới là tự suy (neo trên / giữa / dưới theo vị trí), màn hình lạ nên xem lại. Script vẫn phải tự gán field cho object mới.
- Toạ độ tính theo canvas PSD (vd. 1080×1920), nên khớp Reference Resolution của CanvasScaler.

## Tool (tham khảo)

| Tool | Bên | Việc |
| --- | --- | --- |
| `psd_tree` | psd2ui | Cây layer: id, kind, rect thật (cắt alpha), ẩn, fx, font / cỡ / màu chữ |
| `psd_states` | psd2ui | Layer ẩn ghép với layer đang hiện cùng chỗ |
| `psd_export` | psd2ui | Xuất layer ra PNG cắt alpha + rect |
| `psd_render` | psd2ui | Ảnh composite cả màn / vài layer |
| `psd_compare` | psd2ui | Ghép PSD, Unity, ảnh chênh |
| `psd_to_spec` | psd2ui | Sinh `spec.json` + PNG |
| `psd_match_sprites` | psd2ui | Tìm sprite cũ giống PNG xuất |
| `psd_text_tint` | psd2ui | Màu / gradient chữ lấy từ JPG |
| `psd_split_text` | psd2ui | Tách ảnh gộp (icon + chữ) |
| `psd_variant` | psd2ui | Sinh bản xám / tối cho nút disable |
| `build_layout_spec` | Unity | Dựng UI mới: prefab mới hoặc trong scene (mặc định chạy thử) |
| `match_layout_spec` | Unity | Ghép node spec với object đã có (prefab / scene) |
| `apply_layout_spec` | Unity | Áp spec lên UI đã có (mặc định chạy thử) |
| `render_prefab` | Unity | Render prefab / object trong scene ra PNG |

## Phát triển

```bash
uv sync
claude mcp add -s user psd2ui -- uv --directory "$PWD" run psd-mcp   # chạy bản local không qua plugin
claude plugin validate .
```

MIT License.
