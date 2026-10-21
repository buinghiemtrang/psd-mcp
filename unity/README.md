# PSD Layout (Unity)

Nửa Unity của [psd-mcp](../README.md). Cài qua Package Manager → Add package from git URL:

```
https://github.com/buinghiemtrang/psd-mcp.git?path=/unity
```

- Cần [unityMCP](https://github.com/CoplayDev/unity-mcp) (`com.coplaydev.unity-mcp`) để Claude gọi được 3 tool `match_layout_spec`, `apply_layout_spec`, `render_prefab`. Không có unityMCP thì assembly `PsdLayout.Editor.Mcp` tự tắt, lõi `PsdLayoutCore` vẫn compile.
- Import sample "Config mẫu", chuyển `PsdLayoutConfig.json` vào đâu đó trong `Assets/` rồi sửa map font và thư mục sprite.
