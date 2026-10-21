# psd-mcp: PSD → Unity uGUI với Claude Code

Đưa file PSD của art cho Claude, Claude ráp vào prefab uGUI đang có: đặt đúng vị trí, kích thước, sprite, font, màu chữ, rồi render prefab ra ảnh để so với PSD.

Bạn không cần nhớ tên tool. Cài xong, chỉ cần nói:

> Ráp màn Victory từ `D:/design/victory.psd` (ảnh `victory.jpg`) vào prefab `Assets/UI/Victory.prefab`.

Claude đọc PSD, ghép từng layer với object trong prefab, cho bạn xem danh sách thay đổi trước, bạn đồng ý mới lưu.

*English: a Claude Code plugin (Python MCP server + skill) plus a Unity package. It reads PSD layers, exports alpha-trimmed sprites, builds a layout spec, matches it to an existing uGUI prefab, applies rects / sprites / TMP fonts / colors, and renders the prefab for a side-by-side diff with the PSD.*

## Gồm những gì

| Phần | Ở đâu | Việc |
| --- | --- | --- |
| MCP server `psd` | `psd_mcp/` (Python) | Đọc PSD, xuất sprite, sinh layout spec, so ảnh |
| Skill `psd-to-ugui` | `skills/` | Dạy Claude quy trình ráp UI, chỗ nào phải hỏi bạn |
| Package Unity | `unity/` | 3 tool trong Editor: ghép node, áp spec lên prefab, render prefab |

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

Mở lại Claude Code, gõ `/mcp` thấy server `psd` là xong.

**2. Package Unity**: Package Manager → `+` → Add package from git URL:

```
https://github.com/buinghiemtrang/psd-mcp.git?path=/unity
```

**3. Config cho project**: Package Manager → PSD Layout → Samples → Import "Config mẫu". Chuyển `PsdLayoutConfig.json` vào đâu đó trong `Assets/`, sửa:

```json
{
  "fonts": { "Roboto-Bold": { "font": "Assets/Fonts/Roboto-Bold SDF.asset" } },
  "sprite_dir": "Assets/UI/Sprites",
  "import_like": "Assets/UI/Sprites/btn_ok.png"
}
```

- `fonts`: tên font trong PSD (Claude đọc ra được) → font asset TMP của bạn.
- `sprite_dir`: nơi đặt sprite mới. Để `null` thì Claude hỏi mỗi lần.
- `import_like`: sprite mẫu để chép thiết lập import (nén, max size…) cho sprite mới.

## Dùng thế nào

Nói với Claude bằng lời thường. Ví dụ:

| Bạn nói | Claude làm |
| --- | --- |
| "Xem file `shop.psd` có những gì" | Liệt kê layer, font, cỡ chữ, màu, layer ẩn |
| "Xuất các nút trong `shop.psd` ra PNG" | PNG đã cắt sát alpha + toạ độ |
| "Ráp `shop.psd` vào `Assets/UI/Shop.prefab`" | Cả quy trình bên dưới |
| "So prefab Shop với PSD" | Ảnh ghép PSD, Unity, chỗ lệch |

Quy trình ráp (Claude tự chạy, bạn chỉ trả lời khi được hỏi):

1. Đọc PSD, tìm layer ẩn (state disabled, bản cũ).
2. Sinh layout spec: mỗi layer thành ảnh, chữ (TMP) hoặc nút; màu chữ gradient lấy từ JPG.
3. Tìm sprite cũ giống để dùng lại, tránh thêm sprite trùng.
4. Ghép từng node với object trong prefab theo vị trí, tên, nội dung chữ, sprite.
5. **Chạy thử**: liệt kê thay đổi (rect, sprite, font, màu). Bạn xem rồi đồng ý.
6. Lưu prefab. Giữ anchor, pivot, script, Animator.
7. Render prefab, so với PSD, chỉnh nếu lệch.

## Giới hạn

- psd-tools không vẽ được GradientOverlay / InnerGlow của chữ. Hãy kèm ảnh JPG art xuất để lấy đúng màu.
- Chỉ cập nhật prefab đã có. Object mới chỉ tạo khi bạn đồng ý, script vẫn phải tự gán field.
- Toạ độ tính theo canvas PSD (vd. 1080×1920), nên khớp Reference Resolution của CanvasScaler.

## Tool (tham khảo)

| Tool | Bên | Việc |
| --- | --- | --- |
| `psd_tree` | psd | Cây layer: id, kind, rect thật (cắt alpha), ẩn, fx, font / cỡ / màu chữ |
| `psd_states` | psd | Layer ẩn ghép với layer đang hiện cùng chỗ |
| `psd_export` | psd | Xuất layer ra PNG cắt alpha + rect |
| `psd_render` | psd | Ảnh composite cả màn / vài layer |
| `psd_compare` | psd | Ghép PSD, Unity, ảnh chênh |
| `psd_to_spec` | psd | Sinh `spec.json` + PNG |
| `psd_match_sprites` | psd | Tìm sprite cũ giống PNG xuất |
| `psd_text_tint` | psd | Màu / gradient chữ lấy từ JPG |
| `psd_split_text` | psd | Tách ảnh gộp (icon + chữ) |
| `psd_variant` | psd | Sinh bản xám / tối cho nút disable |
| `match_layout_spec` | Unity | Ghép node spec với object prefab |
| `apply_layout_spec` | Unity | Áp spec lên prefab (mặc định chạy thử) |
| `render_prefab` | Unity | Render prefab ra PNG |

## Phát triển

```bash
uv sync
claude mcp add -s user psd -- uv --directory "$PWD" run psd-mcp   # chạy bản local không qua plugin
claude plugin validate .
```

MIT License.
