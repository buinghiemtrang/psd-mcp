using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PsdLayout
{
    /// <summary>
    /// Nửa Unity của psd-mcp: đọc layout spec (toạ độ PSD, gốc trên-trái) để ghép node, áp lên prefab uGUI đang có
    /// và render prefab ra PNG để so với PSD. Giữ anchor / pivot / reference script, chỉ đổi vị trí, size, sprite, chữ.
    /// </summary>
    public static class PsdLayoutCore
    {
        /// <summary>File config trong project (map font, thư mục sprite): file tên PsdLayoutConfig.json đầu tiên tìm thấy trong Assets.
        /// Tạo từ sample của package (Package Manager → PSD Layout → Samples) rồi sửa đường dẫn.</summary>
        public static string ConfigPath
        {
            get
            {
                foreach (var guid in AssetDatabase.FindAssets("PsdLayoutConfig", new[] { "Assets" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileName(p) == "PsdLayoutConfig.json") return p;
                }
                return null;
            }
        }

        // ------------------------------------------------------------------ phiên sửa prefab

        /// <summary>Mở prefab, đặt root vào canvas world-space W×H tại gốc để 1px PSD = 1 đơn vị world.</summary>
        sealed class Session : IDisposable
        {
            public readonly GameObject Root;
            public readonly float W, H;
            readonly string _path;
            readonly GameObject _canvas;
            readonly List<(RectTransform rt, Vector2 min, Vector2 max, Vector2 oMin, Vector2 oMax)> _saved = new();

            public Session(string prefabPath, float w, float h)
            {
                _path = prefabPath;
                W = w; H = h;
                Root = PrefabUtility.LoadPrefabContents(prefabPath);
                _canvas = new GameObject("PsdLayoutCanvas", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(_canvas, Root.scene);
                _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                var crt = (RectTransform)_canvas.transform;
                crt.sizeDelta = new Vector2(W, H);
                crt.position = Vector3.zero;
                crt.localScale = Vector3.one;
                Root.transform.SetParent(_canvas.transform, false);
                // Root + SafeArea tạm stretch kín canvas; trả lại trước khi Save.
                Stretch(Root.transform as RectTransform);
                foreach (var c in Root.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name.Contains("SafeArea")) Stretch(c.transform as RectTransform);
                Canvas.ForceUpdateCanvases();
            }

            void Stretch(RectTransform rt)
            {
                if (rt == null || _saved.Any(s => s.rt == rt)) return;
                _saved.Add((rt, rt.anchorMin, rt.anchorMax, rt.offsetMin, rt.offsetMax));
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            }

            public float[] ToPsd(RectTransform rt)
            {
                var c = new Vector3[4];
                rt.GetWorldCorners(c);
                float x0 = c.Min(v => v.x), x1 = c.Max(v => v.x), y0 = c.Min(v => v.y), y1 = c.Max(v => v.y);
                return new[] { x0 + W / 2f, H / 2f - y1, x1 + W / 2f, H / 2f - y0 };
            }

            public void SetRect(RectTransform rt, float[] r, bool sizeOnly = false, bool moveOnly = false)
            {
                float w = r[2] - r[0], h = r[3] - r[1];
                var ls = rt.lossyScale;
                if (!moveOnly)
                {
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w / Mathf.Max(1e-4f, ls.x));
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, h / Mathf.Max(1e-4f, ls.y));
                }
                if (sizeOnly) return;
                var cur = rt.rect.size;
                float cw = cur.x * ls.x, ch = cur.y * ls.y;
                float cx = (r[0] + r[2]) / 2f, cy = (r[1] + r[3]) / 2f;
                rt.position = new Vector3(cx - W / 2f + (rt.pivot.x - .5f) * cw, H / 2f - cy + (rt.pivot.y - .5f) * ch, rt.position.z);
            }

            public void Save()
            {
                Restore();
                PrefabUtility.SaveAsPrefabAsset(Root, _path, out bool ok);
                if (!ok) throw new Exception("SaveAsPrefabAsset lỗi: " + _path);
            }

            void Restore()
            {
                foreach (var s in _saved)
                {
                    s.rt.anchorMin = s.min; s.rt.anchorMax = s.max;
                    s.rt.offsetMin = s.oMin; s.rt.offsetMax = s.oMax;
                }
                _saved.Clear();
                if (Root.transform.parent != null) Root.transform.SetParent(null, false);
            }

            public void Dispose()
            {
                if (Root != null && Root.transform.parent != null) Root.transform.SetParent(null, false);
                if (_canvas != null) Object.DestroyImmediate(_canvas);
                if (Root != null) PrefabUtility.UnloadPrefabContents(Root);
            }
        }

        // ------------------------------------------------------------------ tiện ích

        static JObject LoadJson(string path) => JObject.Parse(File.ReadAllText(path));

        static void SaveJson(string path, JObject o) => File.WriteAllText(path, o.ToString(Newtonsoft.Json.Formatting.Indented));

        static float[] Rect(JToken t) => t is JArray a && a.Count == 4 ? a.Select(v => (float)v).ToArray() : null;

        static string RelPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static Transform FindTarget(Transform root, string target)
        {
            if (string.IsNullOrEmpty(target) || target == ".") return root;
            if (target.StartsWith(root.name + "/")) target = target.Substring(root.name.Length + 1);
            return root.Find(target);
        }

        static IEnumerable<JObject> Flatten(JArray nodes)
        {
            if (nodes == null) yield break;
            foreach (JObject n in nodes)
            {
                yield return n;
                foreach (var c in Flatten(n["children"] as JArray)) yield return c;
            }
        }

        static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>Dice trên cặp ký tự: 0..1.</summary>
        static float Similar(string a, string b)
        {
            a = Norm(a); b = Norm(b);
            if (a.Length == 0 || b.Length == 0) return 0f;
            if (a == b) return 1f;
            if (a.Length < 2 || b.Length < 2) return a.Contains(b) || b.Contains(a) ? .5f : 0f;
            var ba = Enumerable.Range(0, a.Length - 1).Select(i => a.Substring(i, 2)).ToList();
            var bb = Enumerable.Range(0, b.Length - 1).Select(i => b.Substring(i, 2)).ToList();
            int hit = 0;
            foreach (var g in ba) { int k = bb.IndexOf(g); if (k >= 0) { hit++; bb.RemoveAt(k); } }
            return 2f * hit / (ba.Count + b.Length - 1);
        }

        static float Iou(float[] a, float[] b)
        {
            float ix = Mathf.Max(0, Mathf.Min(a[2], b[2]) - Mathf.Max(a[0], b[0]));
            float iy = Mathf.Max(0, Mathf.Min(a[3], b[3]) - Mathf.Max(a[1], b[1]));
            float inter = ix * iy;
            float u = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter;
            return u > 0 ? inter / u : 0f;
        }

        /// <summary>Phần trăm rect a nằm trong b (chữ PSD là khung sát glyph, TMP thường rộng hơn).</summary>
        static float Inside(float[] a, float[] b)
        {
            float ix = Mathf.Max(0, Mathf.Min(a[2], b[2]) - Mathf.Max(a[0], b[0]));
            float iy = Mathf.Max(0, Mathf.Min(a[3], b[3]) - Mathf.Max(a[1], b[1]));
            float area = (a[2] - a[0]) * (a[3] - a[1]);
            return area > 0 ? ix * iy / area : 0f;
        }

        static Color Hex(string s, Color fallback) => ColorUtility.TryParseHtmlString(s, out var c) ? c : fallback;

        static string Kind(Transform t)
        {
            if (t.GetComponent<TMP_Text>()) return "text";
            if (t.GetComponent<Button>()) return "button";
            if (t.GetComponent<Image>()) return "image";
            return "group";
        }

        static void CheckPrefabStage(string prefab)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.assetPath == prefab && stage.scene.isDirty)
                throw new Exception("Prefab Mode đang mở " + prefab + " và có thay đổi chưa lưu: lưu hoặc đóng trước.");
        }

        // ------------------------------------------------------------------ match_layout_spec (v3)

        /// <summary>Ghép node spec với node prefab theo vị trí + tên + chữ + sprite. Ghi `target` + `match_score` vào spec.</summary>
        public static object Match(string specPath, string prefab, bool overwrite, float minScore)
        {
            var spec = LoadJson(specPath);
            prefab ??= (string)spec["prefab"];
            if (string.IsNullOrEmpty(prefab)) throw new Exception("Thiếu prefab (tham số hoặc spec.prefab).");
            var canvas = spec["canvas"] as JArray;
            var lines = new List<string>();
            using (var s = new Session(prefab, (float)canvas[0], (float)canvas[1]))
            {
                var cands = s.Root.GetComponentsInChildren<RectTransform>(true).Where(r => r.transform != s.Root.transform)
                    .Select(r => new
                    {
                        rt = r, path = RelPath(s.Root.transform, r), rect = s.ToPsd(r), kind = Kind(r),
                        text = r.GetComponent<TMP_Text>() ? r.GetComponent<TMP_Text>().text : null,
                        sprite = r.GetComponent<Image>() && r.GetComponent<Image>().sprite
                            ? AssetDatabase.GetAssetPath(r.GetComponent<Image>().sprite) : null,
                    }).ToList();
                var used = new HashSet<string>(Flatten(spec["nodes"] as JArray).Select(n => (string)n["target"]).Where(t => !string.IsNullOrEmpty(t)));

                void Visit(JArray nodes, string parentTarget)
                {
                    if (nodes == null) return;
                    foreach (JObject n in nodes)
                    {
                        string cur = (string)n["target"];
                        if (string.IsNullOrEmpty(cur) || overwrite)
                        {
                            if (!string.IsNullOrEmpty(cur)) used.Remove(cur);
                            var r = Rect(n["rect"]);
                            string kind = (string)n["kind"], name = (string)n["name"], text = (string)n["text"];
                            string reuse = ((string)n["sprite"])?.StartsWith("reuse:") == true ? ((string)n["sprite"]).Substring(6) : null;
                            // Group chỉ ghép với object rỗng (không Image/TMP) để không giành chỗ của node con.
                            var best = cands.Where(c => !used.Contains(c.path) && (kind == "group") == (c.kind == "group")).Select(c =>
                            {
                                float geo = kind == "text" ? Inside(r, c.rect) * .7f + Iou(r, c.rect) * .3f : Iou(r, c.rect);
                                float nm = Mathf.Max(Similar(name, c.rt.name), kind == "text" && c.text != null ? Similar(text, c.text) : 0f);
                                float kd = kind == c.kind || (kind == "button" && c.kind == "image") ? 1f : 0f;
                                float sp = reuse != null && reuse == c.sprite ? 1f : 0f;
                                float under = parentTarget != null && c.path.StartsWith(parentTarget + "/") ? .1f : 0f;
                                return (c, score: geo * .45f + nm * .25f + kd * .15f + sp * .3f + under);
                            }).OrderByDescending(x => x.score).FirstOrDefault();
                            if (best.c != null && best.score >= minScore)
                            {
                                n["target"] = best.c.path;
                                n["match_score"] = Math.Round(best.score, 2);
                                used.Add(best.c.path);
                                lines.Add($"{kind} \"{name}\" → {best.c.path} ({best.score:0.00})");
                            }
                            else lines.Add($"{kind} \"{name}\" → (chưa có, {(best.c != null ? best.c.path + " " + best.score.ToString("0.00") : "-")})");
                        }
                        Visit(n["children"] as JArray, (string)n["target"] ?? parentTarget);
                    }
                }

                Visit(spec["nodes"] as JArray, null);
            }
            spec["prefab"] = prefab;
            SaveJson(specPath, spec);
            return new { spec = specPath, prefab, result = lines };
        }

        // ------------------------------------------------------------------ apply_layout_spec (v2)

        sealed class Ctx
        {
            public JObject Spec, Config;
            public bool DryRun, CreateNew;
            public string PngDir, SpriteDir;
            public readonly List<string> Changes = new(), Warnings = new(), Created = new();
        }

        public static object Apply(string specPath, bool dryRun, bool createNew)
        {
            var ctx = new Ctx { Spec = LoadJson(specPath), DryRun = dryRun, CreateNew = createNew };
            _fonts = null;
            var cfg = ConfigPath;
            ctx.Config = cfg != null ? LoadJson(cfg) : new JObject();
            string prefab = (string)ctx.Spec["prefab"];
            if (string.IsNullOrEmpty(prefab)) throw new Exception("spec.prefab trống: ghi đường dẫn prefab cần áp.");
            CheckPrefabStage(prefab);
            ctx.PngDir = (string)ctx.Spec["png_dir"];
            ctx.SpriteDir = (string)ctx.Spec["sprite_dir"] ?? (string)ctx.Config["sprite_dir"];
            var canvas = ctx.Spec["canvas"] as JArray;

            using (var s = new Session(prefab, (float)canvas[0], (float)canvas[1]))
            {
                ApplyNodes(ctx, s, ctx.Spec["nodes"] as JArray, s.Root.transform);
                if (!dryRun)
                {
                    s.Save();
                    if (ctx.Created.Count > 0) SaveJson(specPath, ctx.Spec);
                }
            }
            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                WarnSceneOverrides(prefab, ctx.Warnings);
            }
            return new { prefab, dry_run = dryRun, changes = ctx.Changes, created = ctx.Created, warnings = ctx.Warnings };
        }

        static void ApplyNodes(Ctx ctx, Session s, JArray nodes, Transform parent)
        {
            if (nodes == null) return;
            foreach (JObject n in nodes)
            {
                if ((bool?)n["skip"] == true) continue;
                string kind = (string)n["kind"], name = (string)n["name"], target = (string)n["target"];
                Transform t = string.IsNullOrEmpty(target) ? null : FindTarget(s.Root.transform, target);
                if (t == null && !string.IsNullOrEmpty(target)) ctx.Warnings.Add($"Không thấy target '{target}' ({name})");
                if (t == null && string.IsNullOrEmpty(target))
                {
                    if (!ctx.CreateNew) { ctx.Changes.Add($"[bỏ qua] {kind} \"{name}\": chưa có target"); ApplyNodes(ctx, s, n["children"] as JArray, parent); continue; }
                    t = Create(kind, name, parent);
                    n["target"] = RelPath(s.Root.transform, t);
                    ctx.Created.Add($"{n["target"]} ({kind}) — thêm [SerializeField] trong script nếu cần gán");
                }
                if (t != null) ApplyNode(ctx, s, n, t);
                ApplyNodes(ctx, s, n["children"] as JArray, t ?? parent);
            }
        }

        static Transform Create(string kind, string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            if (kind == "image" || kind == "button") go.AddComponent<Image>().raycastTarget = kind == "button";
            if (kind == "button") go.AddComponent<Button>();
            if (kind == "text") { var tmp = go.AddComponent<TextMeshProUGUI>(); tmp.alignment = TextAlignmentOptions.Center; tmp.raycastTarget = false; }
            return go.transform;
        }

        static void ApplyNode(Ctx ctx, Session s, JObject n, Transform t)
        {
            var rt = t as RectTransform;
            string kind = (string)n["kind"], path = RelPath(s.Root.transform, t);
            var r = Rect(n["rect"]);
            if (rt == null || r == null) return;
            var before = s.ToPsd(rt);
            bool inLayout = t.parent != null && t.parent.GetComponent<LayoutGroup>() != null;

            if (kind == "text")
            {
                var tmp = t.GetComponent<TMP_Text>() ?? t.GetComponentInChildren<TMP_Text>(true);
                if (tmp == null && t.GetComponent<Image>() == null) { ctx.Warnings.Add($"{path}: không có TMP_Text / Image"); return; }
                // Chữ đã nướng thành ảnh: giữ sprite (ảnh có glow, khung PSD chỉ sát glyph), chỉ dời tâm.
                int ha = 0, va = 0;
                if (tmp != null)
                {
                    ApplyText(ctx, n, tmp, path);
                    var h0 = tmp.horizontalAlignment;
                    ha = h0 == HorizontalAlignmentOptions.Left ? -1 : h0 == HorizontalAlignmentOptions.Right ? 1 : 0;
                    var v0 = tmp.verticalAlignment;
                    va = v0 == VerticalAlignmentOptions.Top ? -1 : v0 == VerticalAlignmentOptions.Bottom ? 1 : 0;
                }
                // Khung PSD sát glyph: bám mép theo alignment của TMP, nới size nếu TMP nhỏ hơn chữ.
                float w = Mathf.Max(r[2] - r[0], before[2] - before[0]), h = Mathf.Max(r[3] - r[1], before[3] - before[1]);
                float x0 = ha < 0 ? r[0] : ha > 0 ? r[2] - w : (r[0] + r[2] - w) / 2f;
                float y0 = va < 0 ? r[1] : va > 0 ? r[3] - h : (r[1] + r[3] - h) / 2f;
                if (!inLayout) s.SetRect(rt, new[] { x0, y0, x0 + w, y0 + h });
            }
            else if (kind == "image" || kind == "button")
            {
                var img = t.GetComponent<Image>();
                if (img == null) { ctx.Warnings.Add($"{path}: không có Image"); return; }
                ApplySprite(ctx, n, img, path);
                s.SetRect(rt, r, sizeOnly: inLayout);
                foreach (var st in (n["states"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var so = (JObject)st.Value;
                    var stT = FindTarget(s.Root.transform, (string)so["target"]);
                    if (string.IsNullOrEmpty((string)so["target"]) || stT == null) { ctx.Changes.Add($"[state] {path}: '{st.Name}' chưa có target"); continue; }
                    var stImg = stT.GetComponent<Image>();
                    if (stImg == null) { ctx.Warnings.Add($"{RelPath(s.Root.transform, stT)}: state không có Image"); continue; }
                    ApplySprite(ctx, so, stImg, RelPath(s.Root.transform, stT));
                    if (Rect(so["rect"]) is float[] sr) s.SetRect((RectTransform)stT, sr, sizeOnly: stT.parent.GetComponent<LayoutGroup>() != null);
                }
            }
            else if ((bool?)n["apply_rect"] == true) s.SetRect(rt, r, sizeOnly: inLayout);

            var after = s.ToPsd(rt);
            if (before.Zip(after, (a, b) => Mathf.Abs(a - b)).Max() > .5f)
                ctx.Changes.Add($"{path}: rect [{string.Join(",", before.Select(v => Mathf.RoundToInt(v)))}] → [{string.Join(",", after.Select(v => Mathf.RoundToInt(v)))}]");
            if (inLayout) LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)t.parent);
        }

        static void ApplyText(Ctx ctx, JObject n, TMP_Text tmp, string path)
        {
            string font = (string)n["font"];
            var map = ctx.Config["fonts"] as JObject;
            var entry = map?.Properties().FirstOrDefault(p => p.Name == font)
                        ?? map?.Properties().FirstOrDefault(p => Norm(p.Name) == Norm(font));
            if (entry != null)
            {
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>((string)entry.Value["font"]);
                if (fa != null && tmp.font != fa) { ctx.Changes.Add($"{path}: font {(tmp.font ? tmp.font.name : "-")} → {fa.name}"); tmp.font = fa; }
                var mat = (string)entry.Value["material"];
                if (!string.IsNullOrEmpty(mat) && AssetDatabase.LoadAssetAtPath<Material>(mat) is Material m && tmp.fontSharedMaterial != m) tmp.fontSharedMaterial = m;
            }
            else if (!string.IsNullOrEmpty(font))
            {
                // Không có trong config: tự tìm font TMP trùng tên (bỏ hậu tố SDF), vd. "UTM-Eremitage" → "UTM EREMITAGE SDF".
                var fa = AutoFont(font, tmp.font, out var others);
                if (fa == null) ctx.Warnings.Add($"{path}: không tìm thấy font TMP nào giống '{font}' (thêm vào PsdLayoutConfig.json nếu cần)");
                else if (tmp.font != fa)
                {
                    ctx.Changes.Add($"{path}: font {(tmp.font ? tmp.font.name : "-")} → {AssetDatabase.GetAssetPath(fa)} (tự chọn)");
                    if (others != null) ctx.Warnings.Add($"{path}: '{font}' có nhiều font TMP trùng tên, đã chọn {AssetDatabase.GetAssetPath(fa)}; bản khác: {string.Join(", ", others.Select(AssetDatabase.GetAssetPath))}");
                    tmp.font = fa;
                }
            }

            if (n["size"] != null)
            {
                float size = (float)n["size"];
                if (Mathf.Abs(tmp.fontSize - size) > .5f) { ctx.Changes.Add($"{path}: fontSize {tmp.fontSize:0.#} → {size:0.#}"); tmp.fontSize = size; tmp.enableAutoSizing = false; }
            }
            if (n["gradient"] is JObject g)
            {
                Color top = Hex((string)g["top"], Color.white), bot = Hex((string)g["bottom"], Color.white);
                System.Func<Color, Color, bool> near = (a, b) => Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)) < .03f;
                var cg = tmp.colorGradient;
                if (!(tmp.enableVertexGradient && near(cg.topLeft, top) && near(cg.bottomLeft, bot) && near(tmp.color, Color.white)))
                {
                    tmp.color = Color.white;
                    tmp.enableVertexGradient = true;
                    tmp.colorGradient = new VertexGradient(top, top, bot, bot);
                    ctx.Changes.Add($"{path}: gradient {g["top"]} → {g["bottom"]}");
                }
            }
            else if (n["color"] != null)
            {
                var c = Hex((string)n["color"], tmp.color);
                bool same = Mathf.Max(Mathf.Abs(c.r - tmp.color.r), Mathf.Abs(c.g - tmp.color.g), Mathf.Abs(c.b - tmp.color.b)) < .03f;
                if (tmp.enableVertexGradient || !same)
                {
                    ctx.Changes.Add($"{path}: color → {n["color"]}");
                    tmp.enableVertexGradient = false;
                    tmp.color = c;
                }
            }
            if ((bool?)n["dynamic"] != true && n["text"] != null && tmp.text != (string)n["text"])
            {
                ctx.Changes.Add($"{path}: text \"{tmp.text}\" → \"{n["text"]}\"");
                tmp.text = (string)n["text"];
            }
        }

        static Dictionary<string, List<TMP_FontAsset>> _fonts;

        static string FontKey(string name)
        {
            var k = Norm(name);
            return k.EndsWith("sdf") ? k.Substring(0, k.Length - 3) : k;
        }

        /// <summary>Font TMP trùng tên font PSD. Font đang gắn khớp thì giữ; nhiều bản trùng tên thì lấy bản đầu và báo `others`.</summary>
        static TMP_FontAsset AutoFont(string psdFont, TMP_FontAsset current, out List<TMP_FontAsset> others)
        {
            others = null;
            if (_fonts == null)
            {
                _fonts = new Dictionary<string, List<TMP_FontAsset>>();
                foreach (var guid in AssetDatabase.FindAssets("t:TMP_FontAsset"))
                {
                    var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetDatabase.GUIDToAssetPath(guid));
                    if (fa == null) continue;
                    var key = FontKey(fa.name);
                    if (!_fonts.TryGetValue(key, out var list)) _fonts[key] = list = new List<TMP_FontAsset>();
                    list.Add(fa);
                }
            }
            var want = FontKey(psdFont);
            if (current != null && Similar(want, FontKey(current.name)) >= .8f) return current;
            if (!_fonts.TryGetValue(want, out var hits))
            {
                var best = _fonts.Select(kv => (list: kv.Value, score: Similar(want, kv.Key))).OrderByDescending(x => x.score).FirstOrDefault();
                if (best.score < .8f) return null;
                hits = best.list;
            }
            if (hits.Count > 1) others = hits.Skip(1).ToList();
            return hits[0];
        }

        static void ApplySprite(Ctx ctx, JObject n, Image img, string path)
        {
            string sp = (string)n["sprite"], png = (string)n["png"];
            Sprite sprite = null;
            if (!string.IsNullOrEmpty(sp) && sp.StartsWith("reuse:"))
            {
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sp.Substring(6));
                if (sprite == null) { ctx.Warnings.Add($"{path}: không load được {sp}"); return; }
            }
            else if (!string.IsNullOrEmpty(sp) && sp.StartsWith("Assets/"))
                sprite = AssetDatabase.LoadAssetAtPath<Sprite>(sp);
            else if (!string.IsNullOrEmpty(png) && sp != "keep")
                sprite = ImportSprite(ctx, n, png, img.sprite, path);
            if (sprite == null || img.sprite == sprite) return;
            ctx.Changes.Add($"{path}: sprite {(img.sprite ? img.sprite.name : "-")} → {sprite.name}");
            img.sprite = sprite;
            if (n["slice"] is JArray) img.type = Image.Type.Sliced;
        }

        static Sprite ImportSprite(Ctx ctx, JObject n, string png, Sprite old, string path)
        {
            if (string.IsNullOrEmpty(ctx.SpriteDir))
            {
                ctx.Warnings.Add($"{path}: cần sprite_dir (spec.sprite_dir hoặc {ConfigPath ?? "PsdLayoutConfig.json"}) để import {png}");
                return null;
            }
            string src = Path.Combine(ctx.PngDir ?? "", png);
            if (!File.Exists(src)) { ctx.Warnings.Add($"{path}: thiếu file {src}"); return null; }
            string dir = ctx.SpriteDir.TrimEnd('/');
            string dst = $"{dir}/{png}";
            var bytes = File.ReadAllBytes(src);
            for (int k = 2; File.Exists(dst) && !File.ReadAllBytes(dst).SequenceEqual(bytes); k++)
                dst = $"{dir}/{Path.GetFileNameWithoutExtension(png)}_{k}.png";
            if (File.Exists(dst))
            {
                if (!ctx.DryRun) n["sprite"] = dst;
                return AssetDatabase.LoadAssetAtPath<Sprite>(dst);
            }
            if (ctx.DryRun)
            {
                ctx.Changes.Add($"{path}: import {dst}");
                return null;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(dst, bytes);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
            var imp = (TextureImporter)AssetImporter.GetAtPath(dst);
            var likePath = old ? AssetDatabase.GetAssetPath(old) : (string)ctx.Config["import_like"];
            var like = string.IsNullOrEmpty(likePath) ? null : AssetImporter.GetAtPath(likePath) as TextureImporter;
            imp.textureType = TextureImporterType.Sprite;
            if (like != null)
            {
                var st = new TextureImporterSettings();
                like.ReadTextureSettings(st);
                st.spriteMode = (int)SpriteImportMode.Single;
                st.spriteBorder = Vector4.zero;
                imp.SetTextureSettings(st);
                imp.SetPlatformTextureSettings(like.GetDefaultPlatformTextureSettings());
                foreach (var plat in new[] { "Standalone", "Android", "iPhone", "WebGL" })
                {
                    var ps = like.GetPlatformTextureSettings(plat);
                    if (ps.overridden) imp.SetPlatformTextureSettings(ps);
                }
            }
            else imp.spriteImportMode = SpriteImportMode.Single;
            if (n["slice"] is JArray sl && sl.Count == 4) imp.spriteBorder = new Vector4((float)sl[0], (float)sl[1], (float)sl[2], (float)sl[3]);
            imp.SaveAndReimport();
            ctx.Changes.Add($"{path}: import {dst}" + (like != null ? $" (thiết lập theo {Path.GetFileName(likePath)})" : ""));
            n["sprite"] = dst;
            return AssetDatabase.LoadAssetAtPath<Sprite>(dst);
        }

        static void WarnSceneOverrides(string prefab, List<string> warnings)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)).Select(x => x.gameObject))
                {
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go) || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) != prefab) continue;
                    var mods = PrefabUtility.GetPropertyModifications(go);
                    int n = mods?.Count(m => m.target is Component c && c.transform != PrefabUtility.GetCorrespondingObjectFromSource(go).transform) ?? 0;
                    if (n > 0) warnings.Add($"{scene.name}/{go.name}: instance có {n} override trên con, có thể che thay đổi (kiểm tra GetPropertyModifications)");
                }
            }
        }

        // ------------------------------------------------------------------ render_prefab (v2)

        /// <summary>Render prefab UI vào PNG W×H qua camera ortho + canvas ScreenSpaceCamera trong preview scene. Không lưu prefab.</summary>
        public static object Render(string prefab, string outPath, int width, int height, JObject states)
        {
            var root = PrefabUtility.LoadPrefabContents(prefab);
            GameObject camGo = null, canvasGo = null;
            RenderTexture rtex = null;
            Texture2D tex = null;
            var missing = new List<string>();
            try
            {
                foreach (var p in states?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var t = FindTarget(root.transform, p.Name);
                    if (t == null) missing.Add(p.Name); else t.gameObject.SetActive((bool)p.Value);
                }
                camGo = new GameObject("PsdLayoutRenderCam", typeof(Camera));
                SceneManager.MoveGameObjectToScene(camGo, root.scene);
                var cam = camGo.GetComponent<Camera>();
                cam.scene = root.scene;
                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.nearClipPlane = .1f; cam.farClipPlane = 1000f;
                camGo.transform.position = new Vector3(0, 0, -100);
                rtex = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rtex;

                canvasGo = new GameObject("PsdLayoutRenderCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
                SceneManager.MoveGameObjectToScene(canvasGo, root.scene);
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 50;
                var scaler = canvasGo.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(width, height);
                scaler.matchWidthOrHeight = 1f;
                root.transform.SetParent(canvasGo.transform, false);
                var rrt = root.transform as RectTransform;
                if (rrt != null) { rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = rrt.offsetMax = Vector2.zero; }
                Canvas.ForceUpdateCanvases();
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rtex;
                tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new UnityEngine.Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllBytes(outPath, tex.EncodeToPNG());
            }
            finally
            {
                if (root != null && root.transform.parent != null) root.transform.SetParent(null, false);
                if (camGo) Object.DestroyImmediate(camGo);
                if (canvasGo) Object.DestroyImmediate(canvasGo);
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
                if (rtex) { rtex.Release(); Object.DestroyImmediate(rtex); }
                if (tex) Object.DestroyImmediate(tex);
            }
            return new { file = outPath, size = new[] { width, height }, missing_states = missing };
        }
    }
}
