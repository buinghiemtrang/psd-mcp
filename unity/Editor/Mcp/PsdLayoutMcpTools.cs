using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;
using Newtonsoft.Json.Linq;

namespace PsdLayout
{
    // Vỏ custom tool unityMCP cho psd-mcp (Claude gọi qua MCP). Logic nằm ở PsdLayoutCore để sau này gắn thêm menu.

    [McpForUnityTool("match_layout_spec", Description = "psd-mcp: ghép node của layout spec (psd_to_spec) với node prefab uGUI theo vị trí, tên, chữ, sprite; ghi target + match_score vào spec.")]
    public static class MatchLayoutSpecTool
    {
        public class Parameters
        {
            [ToolParameter("Đường dẫn tuyệt đối spec.json")] public string spec { get; set; }
            [ToolParameter("Prefab (Assets/...); bỏ trống = spec.prefab", Required = false)] public string prefab { get; set; }
            [ToolParameter("Ghép lại cả node đã có target", Required = false, DefaultValue = "false")] public bool overwrite { get; set; }
            [ToolParameter("Điểm tối thiểu 0..1 để nhận ghép", Required = false, DefaultValue = "0.35")] public float min_score { get; set; }
        }

        public static object HandleCommand(JObject p)
        {
            try
            {
                var r = PsdLayoutCore.Match((string)p["spec"], (string)p["prefab"], (bool?)p["overwrite"] ?? false, (float?)p["min_score"] ?? .35f);
                return new SuccessResponse("Đã ghép spec với prefab", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }

    [McpForUnityTool("apply_layout_spec", Description = "psd-mcp: áp layout spec lên prefab uGUI đang có (rect, sprite, font, cỡ, màu, chữ; giữ anchor/pivot). dry_run mặc định true = chỉ liệt kê thay đổi, không lưu.")]
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

    [McpForUnityTool("render_prefab", Description = "psd-mcp: render prefab UI ra PNG (mặc định 1080x1920) để so với PSD bằng psd_compare. states = {\"đường/dẫn\": true|false} bật tắt object trước khi chụp, không lưu prefab.")]
    public static class RenderPrefabTool
    {
        public class Parameters
        {
            [ToolParameter("Prefab (Assets/...)")] public string prefab { get; set; }
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
                var r = PsdLayoutCore.Render((string)p["prefab"], (string)p["out"], (int?)p["width"] ?? 1080, (int?)p["height"] ?? 1920, states);
                return new SuccessResponse("Đã render", r);
            }
            catch (Exception e) { return new ErrorResponse(e.Message); }
        }
    }
}
