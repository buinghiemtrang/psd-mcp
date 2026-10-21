# PSD Layout (Unity)

Nửa Unity của [psd-mcp](../README.md). Cài qua Package Manager → Add package from git URL:

```
https://github.com/buinghiemtrang/psd-mcp.git?path=/unity
```

- Cần [unityMCP](https://github.com/CoplayDev/unity-mcp) (`com.coplaydev.unity-mcp`) để Claude gọi được 4 tool `build_layout_spec`, `match_layout_spec`, `apply_layout_spec`, `render_prefab` (prefab hoặc UI trong scene). Không có unityMCP thì assembly `PsdLayout.Editor.Mcp` tự tắt, lõi `PsdLayoutCore` vẫn compile.
- Không cần config: font TMP tự chọn theo tên font PSD. Muốn ép font / material hoặc cố định thư mục sprite thì import sample "Config mẫu" (tuỳ chọn).
