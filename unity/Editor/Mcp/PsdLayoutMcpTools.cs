using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace PsdLayout
{
    // Vỏ custom tool unityMCP cho psd-mcp (Claude gọi qua MCP). Logic nằm ở PsdLayoutCore để sau này gắn thêm menu.

    [McpForUnityTool("match_layout_spec", Description = "psd-mcp: ghép node của layout spec (psd_to_spec) với object UI đang có (prefab, hoặc object trong scene) theo vị trí, tên, chữ, sprite; ghi target + match_score vào spec.")]
    public static class MatchLayoutSpecTool
    {
        public class Parameters
        {
            [ToolParameter("Đường dẫn tuyệt đối spec.json")] public string spec { get; set; }
            [ToolParameter("Prefab (Assets/...); bỏ trống = theo spec", Required = false)] public string prefab { get; set; }
            [ToolParameter("Scene (Assets/....unity) khi UI nằm thẳng trong scene", Required = false)] public string scene { get; set; }
            [ToolParameter("Đường dẫn object UI trong scene, vd. Canvas/ShopPanel", Required = false)] public string root { get; set; }
            [ToolParameter("Ghép lại cả node đã có target", Required = false, DefaultValue = "false")] public bool overwrite { get; set; }
            [ToolParameter("Điểm tối thiểu 0..1 để nhận ghép", Required = false, DefaultValue = "0.35")] public float min_score { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            try
            {
                var r = PsdLayoutCore.Match((string)p["spec"], (string)p["prefab"], (string)p["scene"], (string)p["root"], (bool?)p["overwrite"] ?? false, (float?)p["min_score"] ?? .35f);
                return new SuccessResponse("Đã ghép spec với prefab", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }

    [McpForUnityTool("apply_layout_spec", Description = "psd-mcp: áp layout spec lên UI đang có (spec.prefab, hoặc spec.scene + spec.root): rect, sprite, font, cỡ, màu, chữ; giữ anchor/pivot. dry_run mặc định true = chỉ liệt kê thay đổi, không lưu. Scene: ghi Undo, không tự Save.")]
    public static class ApplyLayoutSpecTool
    {
        public class Parameters
        {
            [ToolParameter("Đường dẫn tuyệt đối spec.json")] public string spec { get; set; }
            [ToolParameter("true = chỉ liệt kê thay đổi", Required = false, DefaultValue = "true")] public bool dry_run { get; set; }
            [ToolParameter("Tạo GameObject cho node chưa có target", Required = false, DefaultValue = "false")] public bool create_new { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            try
            {
                var r = PsdLayoutCore.Apply((string)p["spec"], (bool?)p["dry_run"] ?? true, (bool?)p["create_new"] ?? false);
                return new SuccessResponse("Đã áp spec", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }

    [McpForUnityTool("build_layout_spec", Description = "psd-mcp: dựng UI MỚI từ layout spec: prefab mới (prefab=Assets/...prefab) hoặc object mới trong scene (scene + parent). Anchor tự suy theo vị trí; có UIFitHeight thì bọc Content. Ghi target vào spec để lần sau dùng apply_layout_spec. dry_run mặc định true.")]
    public static class BuildLayoutSpecTool
    {
        public class Parameters
        {
            [ToolParameter("Đường dẫn tuyệt đối spec.json")] public string spec { get; set; }
            [ToolParameter("Prefab mới cần tạo (Assets/....prefab)", Required = false)] public string prefab { get; set; }
            [ToolParameter("Scene (Assets/....unity) khi dựng thẳng trong scene", Required = false)] public string scene { get; set; }
            [ToolParameter("Object cha trong scene, vd. Canvas", Required = false)] public string parent { get; set; }
            [ToolParameter("Tên object root; bỏ trống = tên prefab", Required = false)] public string name { get; set; }
            [ToolParameter("true = dựng thử rồi bỏ, chỉ liệt kê", Required = false, DefaultValue = "true")] public bool dry_run { get; set; }
            [ToolParameter("Ghi đè prefab đã có", Required = false, DefaultValue = "false")] public bool overwrite { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            try
            {
                var r = PsdLayoutCore.Build((string)p["spec"], (string)p["prefab"], (string)p["scene"], (string)p["parent"], (string)p["name"],
                    (bool?)p["dry_run"] ?? true, (bool?)p["overwrite"] ?? false);
                return new SuccessResponse("Đã dựng UI từ spec", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }

    [McpForUnityTool("render_prefab", Description = "psd-mcp: render UI ra PNG (mặc định 1080x1920) để so với PSD bằng psd_compare: prefab, hoặc object trong scene (scene + root). states = {\"đường/dẫn\": true|false} bật tắt object trước khi chụp, không lưu gì.")]
    public static class RenderPrefabTool
    {
        public class Parameters
        {
            [ToolParameter("Prefab (Assets/...)", Required = false)] public string prefab { get; set; }
            [ToolParameter("Scene (Assets/....unity) khi UI nằm trong scene", Required = false)] public string scene { get; set; }
            [ToolParameter("Đường dẫn object UI trong scene", Required = false)] public string root { get; set; }
            [ToolParameter("File PNG ra (đường dẫn tuyệt đối, nên ở scratchpad)")] public string @out { get; set; }
            [ToolParameter("Rộng", Required = false, DefaultValue = "1080")] public int width { get; set; }
            [ToolParameter("Cao", Required = false, DefaultValue = "1920")] public int height { get; set; }
            [ToolParameter("Object bật/tắt trước khi chụp: {\"Panel/Invader\": true}", Required = false)] public object states { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            try
            {
                var states = p["states"] is JObject o ? o : p["states"]?.Type == JTokenType.String ? JObject.Parse((string)p["states"]) : null;
                var r = PsdLayoutCore.Render((string)p["prefab"], (string)p["scene"], (string)p["root"], (string)p["out"], (int?)p["width"] ?? 1080, (int?)p["height"] ?? 1920, states);
                return new SuccessResponse("Đã render", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }
}
