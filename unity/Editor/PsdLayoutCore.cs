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

        // ------------------------------------------------------------------ phiên sửa (prefab có sẵn / prefab mới / scene)

        /// <summary>
        /// Một phiên sửa UI. Toạ độ PSD quy đổi qua <see cref="Frame"/>: RectTransform coi như canvas PSD, thu theo chiều cao
        /// (scale = Frame.rect.height / H, căn giữa ngang). Prefab: Frame = canvas world-space W×H tạm. Scene: Frame = canvas gốc
        /// (hoặc spec.frame). Scene ghi vào Undo; dry run = Discard (revert Undo / không lưu).
        /// </summary>
        sealed class Session : IDisposable
        {
            public enum Mode { Prefab, NewPrefab, Scene }

            public readonly Mode Kind;
            public readonly float W, H;
            public GameObject Root;
            public RectTransform Frame;
            public readonly List<string> Notes = new();
            readonly string _path;
            GameObject _canvas;
            Scene _preview, _scene;
            int _undo = -1;
            readonly List<(RectTransform rt, Vector2 min, Vector2 max, Vector2 oMin, Vector2 oMax)> _saved = new();

            Session(Mode kind, string path, float w, float h) { Kind = kind; _path = path; W = w; H = h; }

            /// <summary>Canvas world-space W×H ở gốc làm khung (1px PSD = 1 đơn vị).</summary>
            void MakeCanvas(Scene scene)
            {
                _canvas = new GameObject("PsdLayoutCanvas", typeof(RectTransform), typeof(Canvas));
                SceneManager.MoveGameObjectToScene(_canvas, scene);
                _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                Frame = (RectTransform)_canvas.transform;
                Frame.sizeDelta = new Vector2(W, H);
                Frame.position = Vector3.zero;
                Frame.localScale = Vector3.one;
            }

            public static Session OpenPrefab(string path, float w, float h)
            {
                CheckPrefabStage(path);
                var s = new Session(Mode.Prefab, path, w, h);
                s.Root = PrefabUtility.LoadPrefabContents(path);
                s.MakeCanvas(s.Root.scene);
                s.Root.transform.SetParent(s._canvas.transform, false);
                // Root + SafeArea tạm stretch kín canvas; trả lại trước khi Save.
                s.Stretch(s.Root.transform as RectTransform);
                foreach (var c in s.Root.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name.Contains("SafeArea")) s.Stretch(c.transform as RectTransform);
                Canvas.ForceUpdateCanvases();
                return s;
            }

            public static Session NewPrefab(string path, string name, float w, float h)
            {
                var s = new Session(Mode.NewPrefab, path, w, h);
                s._preview = EditorSceneManager.NewPreviewScene();
                s.MakeCanvas(s._preview);
                s.Root = new GameObject(name, typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(s.Root, s._preview);
                s.Root.transform.SetParent(s._canvas.transform, false);
                StretchFull((RectTransform)s.Root.transform);
                return s;
            }

            /// <summary>Mở object `rootPath` trong scene (mở additive nếu scene chưa mở). Object thuộc prefab instance thì báo sửa prefab gốc.</summary>
            public static Session OpenScene(string scenePath, string rootPath, string framePath, float w, float h)
            {
                var s = new Session(Mode.Scene, scenePath, w, h);
                s._scene = SceneManager.GetSceneByPath(scenePath);
                if (!s._scene.IsValid() || !s._scene.isLoaded)
                {
                    s._scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    s.Notes.Add($"Đã mở scene {scenePath} (additive) để sửa, xem xong thì Save hoặc đóng scene.");
                }
                s.Root = FindInScene(s._scene, rootPath)?.gameObject ?? throw new Exception($"Không thấy '{rootPath}' trong {scenePath}");
                if (PrefabUtility.IsPartOfPrefabInstance(s.Root))
                    throw new Exception($"'{rootPath}' thuộc prefab instance ({PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(s.Root)}): sửa prefab gốc (spec.prefab) thay vì scene.");
                if (!string.IsNullOrEmpty(framePath))
                    s.Frame = FindInScene(s._scene, framePath) as RectTransform ?? throw new Exception($"Không thấy frame '{framePath}'");
                else
                {
                    var canvas = s.Root.GetComponentInParent<Canvas>(true);
                    s.Frame = canvas != null ? (RectTransform)canvas.rootCanvas.transform : throw new Exception($"'{rootPath}' không nằm trong Canvas nào");
                }
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("PSD Layout");
                s._undo = Undo.GetCurrentGroup();
                Undo.RegisterFullObjectHierarchyUndo(s.Root, "PSD Layout");
                Canvas.ForceUpdateCanvases();
                return s;
            }

            static Transform FindInScene(Scene scene, string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                var parts = path.Split(new[] { '/' }, 2);
                var top = scene.GetRootGameObjects().FirstOrDefault(g => g.name == parts[0]);
                if (top == null) return null;
                return parts.Length == 1 ? top.transform : top.transform.Find(parts[1]);
            }

            public static string ScenePath(Transform t)
            {
                var parts = new List<string>();
                for (var x = t; x != null; x = x.parent) parts.Add(x.name);
                parts.Reverse();
                return string.Join("/", parts);
            }

            public void Created(GameObject go)
            {
                if (Kind == Mode.Scene) Undo.RegisterCreatedObjectUndo(go, "PSD Layout");
            }

            static void StretchFull(RectTransform rt)
            {
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            }

            void Stretch(RectTransform rt)
            {
                if (rt == null || _saved.Any(x => x.rt == rt)) return;
                _saved.Add((rt, rt.anchorMin, rt.anchorMax, rt.offsetMin, rt.offsetMax));
                StretchFull(rt);
            }

            float Scale => Frame.rect.height / H;

            public float[] ToPsd(RectTransform rt)
            {
                var c = new Vector3[4];
                rt.GetWorldCorners(c);
                for (int i = 0; i < 4; i++) c[i] = Frame.InverseTransformPoint(c[i]);
                float x0 = c.Min(v => v.x), x1 = c.Max(v => v.x), y0 = c.Min(v => v.y), y1 = c.Max(v => v.y);
                var fc = Frame.rect.center;
                float k = Scale;
                return new[] { (x0 - fc.x) / k + W / 2f, H / 2f - (y1 - fc.y) / k, (x1 - fc.x) / k + W / 2f, H / 2f - (y0 - fc.y) / k };
            }

            public void SetRect(RectTransform rt, float[] r, bool sizeOnly = false, bool moveOnly = false)
            {
                float k = Scale;
                var fs = Frame.lossyScale;
                float ww = (r[2] - r[0]) * k * fs.x, wh = (r[3] - r[1]) * k * fs.y;
                var ls = rt.lossyScale;
                if (!moveOnly)
                {
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, ww / Mathf.Max(1e-4f, ls.x));
                    rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, wh / Mathf.Max(1e-4f, ls.y));
                }
                if (sizeOnly) return;
                var cur = rt.rect.size;
                float cw = cur.x * ls.x, ch = cur.y * ls.y;
                var fc = Frame.rect.center;
                var world = Frame.TransformPoint(new Vector3(fc.x + ((r[0] + r[2]) / 2f - W / 2f) * k, fc.y + (H / 2f - (r[1] + r[3]) / 2f) * k, 0));
                rt.position = new Vector3(world.x + (rt.pivot.x - .5f) * cw, world.y + (rt.pivot.y - .5f) * ch, rt.position.z);
            }

            public void Save()
            {
                switch (Kind)
                {
                    case Mode.Prefab:
                        Restore();
                        PrefabUtility.SaveAsPrefabAsset(Root, _path, out bool ok);
                        if (!ok) throw new Exception("SaveAsPrefabAsset lỗi: " + _path);
                        break;
                    case Mode.NewPrefab:
                        Directory.CreateDirectory(Path.GetDirectoryName(_path));
                        Root.transform.SetParent(null, false);
                        PrefabUtility.SaveAsPrefabAsset(Root, _path, out bool ok2);
                        if (!ok2) throw new Exception("SaveAsPrefabAsset lỗi: " + _path);
                        break;
                    case Mode.Scene:
                        EditorSceneManager.MarkSceneDirty(Root.scene);
                        Undo.CollapseUndoOperations(_undo);
                        break;
                }
            }

            /// <summary>Bỏ thay đổi (dry run). Prefab: không lưu là xong; scene: revert Undo.</summary>
            public void Discard()
            {
                if (Kind == Mode.Scene && _undo >= 0) Undo.RevertAllDownToGroup(_undo);
            }

            void Restore()
            {
                foreach (var x in _saved)
                {
                    x.rt.anchorMin = x.min; x.rt.anchorMax = x.max;
                    x.rt.offsetMin = x.oMin; x.rt.offsetMax = x.oMax;
                }
                _saved.Clear();
                if (Root.transform.parent != null) Root.transform.SetParent(null, false);
            }

            public void Dispose()
            {
                if (Kind == Mode.Prefab)
                {
                    if (Root != null && Root.transform.parent != null) Root.transform.SetParent(null, false);
                    if (_canvas != null) Object.DestroyImmediate(_canvas);
                    if (Root != null) PrefabUtility.UnloadPrefabContents(Root);
                }
                else if (Kind == Mode.NewPrefab) EditorSceneManager.ClosePreviewScene(_preview);
            }
        }

        /// <summary>Mở phiên theo đích trong spec: `scene` + `root` (UI trong scene) hoặc `prefab`.</summary>
        static Session Open(JObject spec)
        {
            var canvas = spec["canvas"] as JArray;
            float w = (float)canvas[0], h = (float)canvas[1];
            string scene = (string)spec["scene"], prefab = (string)spec["prefab"];
            if (!string.IsNullOrEmpty(scene)) return Session.OpenScene(scene, (string)spec["root"], (string)spec["frame"], w, h);
            if (!string.IsNullOrEmpty(prefab)) return Session.OpenPrefab(prefab, w, h);
            throw new Exception("Spec chưa có đích: ghi `prefab` (Assets/...prefab) hoặc `scene` + `root` (đường dẫn object trong scene).");
        }

        static string TargetName(JObject spec) => !string.IsNullOrEmpty((string)spec["scene"]) ? $"{spec["scene"]}:{spec["root"]}" : (string)spec["prefab"];

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
        public static object Match(string specPath, string prefab, string scene, string root, bool overwrite, float minScore)
        {
            var spec = LoadJson(specPath);
            if (!string.IsNullOrEmpty(prefab)) { spec["prefab"] = prefab; spec.Remove("scene"); }
            if (!string.IsNullOrEmpty(scene)) { spec["scene"] = scene; spec["root"] = root; }
            var lines = new List<string>();
            using (var s = Open(spec))
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
                s.Discard();
            }
            SaveJson(specPath, spec);
            return new { spec = specPath, target = TargetName(spec), result = lines };
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
            ctx.PngDir = (string)ctx.Spec["png_dir"];
            ctx.SpriteDir = (string)ctx.Spec["sprite_dir"] ?? (string)ctx.Config["sprite_dir"];
            List<string> notes;
            using (var s = Open(ctx.Spec))
            {
                notes = s.Notes;
                ApplyNodes(ctx, s, ctx.Spec["nodes"] as JArray, s.Root.transform);
                if (dryRun) s.Discard();
                else
                {
                    s.Save();
                    if (ctx.Created.Count > 0) SaveJson(specPath, ctx.Spec);
                }
            }
            string prefab = (string)ctx.Spec["prefab"];
            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                if (string.IsNullOrEmpty((string)ctx.Spec["scene"])) WarnSceneOverrides(prefab, ctx.Warnings);
                else notes.Add("Scene đã đổi nhưng chưa Save: xem lại rồi Ctrl+S (Ctrl+Z để hoàn tác).");
            }
            return new { target = TargetName(ctx.Spec), dry_run = dryRun, changes = ctx.Changes, created = ctx.Created, warnings = ctx.Warnings, notes };
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
                    t = Create(s, kind, name, parent);
                    n["target"] = RelPath(s.Root.transform, t);
                    ctx.Created.Add($"{n["target"]} ({kind}) — thêm [SerializeField] trong script nếu cần gán");
                }
                if (t != null) ApplyNode(ctx, s, n, t);
                ApplyNodes(ctx, s, n["children"] as JArray, t ?? parent);
            }
        }

        static Transform Create(Session s, string kind, string name, Transform parent)
        {
            var go = new GameObject(UniqueName(parent, name), typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            s.Created(go);
            if (kind == "image" || kind == "button") go.AddComponent<Image>().raycastTarget = kind == "button";
            if (kind == "button") go.AddComponent<Button>();
            if (kind == "text") { var tmp = go.AddComponent<TextMeshProUGUI>(); tmp.alignment = TextAlignmentOptions.Center; tmp.raycastTarget = false; }
            return go.transform;
        }

        static string UniqueName(Transform parent, string name)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Node" : name.Trim();
            string n = name;
            for (int k = 2; parent.Find(n) != null; k++) n = $"{name}_{k}";
            return n;
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
                    var dup = $"'{font}' có nhiều font TMP trùng tên, đã chọn {AssetDatabase.GetAssetPath(fa)}; bản khác: {string.Join(", ", others?.Select(AssetDatabase.GetAssetPath) ?? Enumerable.Empty<string>())}";
                    if (others != null && !ctx.Warnings.Contains(dup)) ctx.Warnings.Add(dup);
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
            if (n["tracking"] != null)
            {
                float sp = (float)n["tracking"] / 10f; // PSD 1/1000 em → TMP 1/100 em
                if (Mathf.Abs(tmp.characterSpacing - sp) > .1f) { ctx.Changes.Add($"{path}: characterSpacing {tmp.characterSpacing:0.#} → {sp:0.#}"); tmp.characterSpacing = sp; }
            }
            var style = tmp.fontStyle;
            if (n["caps"] != null) style = (bool)n["caps"] ? style | FontStyles.UpperCase : style & ~FontStyles.UpperCase;
            if (n["underline"] != null) style = (bool)n["underline"] ? style | FontStyles.Underline : style & ~FontStyles.Underline;
            if (style != tmp.fontStyle) { ctx.Changes.Add($"{path}: fontStyle {tmp.fontStyle} → {style}"); tmp.fontStyle = style; }
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
            // Ưu tiên font của game, không lấy bản mẫu trong thư mục "TextMesh Pro/".
            hits = hits.OrderBy(f => AssetDatabase.GetAssetPath(f).Contains("/TextMesh Pro/") ? 1 : 0).ToList();
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

        // ------------------------------------------------------------------ build_layout_spec: dựng UI mới

        /// <summary>
        /// Dựng UI mới từ spec: prefab mới (`prefab`) hoặc object mới trong scene (`scene` + `parent`). Anchor tự suy theo vị trí trong
        /// khung cha (1/3 trên / giữa / dưới, dải gần full ngang thì stretch). Project có UIFitHeight thì bọc trong `Content` W×H neo mép trên.
        /// Ghi `target` từng node + đích vào spec để lần sau cập nhật bằng apply_layout_spec.
        /// </summary>
        public static object Build(string specPath, string prefab, string scene, string parent, string name, bool dryRun, bool overwrite)
        {
            var ctx = new Ctx { Spec = LoadJson(specPath), DryRun = dryRun, CreateNew = true };
            _fonts = null;
            var cfg = ConfigPath;
            ctx.Config = cfg != null ? LoadJson(cfg) : new JObject();
            ctx.PngDir = (string)ctx.Spec["png_dir"];
            ctx.SpriteDir = (string)ctx.Spec["sprite_dir"] ?? (string)ctx.Config["sprite_dir"];
            var canvas = ctx.Spec["canvas"] as JArray;
            float W = (float)canvas[0], H = (float)canvas[1];
            bool inScene = !string.IsNullOrEmpty(scene);
            if (!inScene)
            {
                if (string.IsNullOrEmpty(prefab) || !prefab.StartsWith("Assets/") || !prefab.EndsWith(".prefab"))
                    throw new Exception("Cần `prefab` = Assets/....prefab (prefab mới) hoặc `scene` + `parent` (dựng trong scene).");
                if (File.Exists(prefab) && !overwrite)
                    throw new Exception($"{prefab} đã có: dùng match_layout_spec + apply_layout_spec để cập nhật, hoặc overwrite=true để dựng lại.");
            }
            if (string.IsNullOrEmpty(name)) name = inScene ? "PsdLayout" : Path.GetFileNameWithoutExtension(prefab);

            List<string> notes;
            Transform root;
            using (var s = inScene ? Session.OpenScene(scene, parent, (string)ctx.Spec["frame"], W, H) : Session.NewPrefab(prefab, name, W, H))
            {
                notes = s.Notes;
                if (inScene)
                {
                    var go = new GameObject(UniqueName(s.Root.transform, name), typeof(RectTransform));
                    go.layer = s.Root.layer;
                    go.transform.SetParent(s.Root.transform, false);
                    s.Created(go);
                    var rrt = (RectTransform)go.transform;
                    rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = rrt.offsetMax = Vector2.zero;
                    root = go.transform;
                }
                else root = s.Root.transform;

                Transform container = root;
                var fit = FindType("UIFitHeight");
                if (fit != null)
                {
                    // Khối thiết kế W×H neo mép trên; UIFitHeight co cả khối khi Play. Toạ độ PSD tính thẳng trên khối này.
                    var content = new GameObject("Content", typeof(RectTransform));
                    content.layer = root.gameObject.layer;
                    content.transform.SetParent(root, false);
                    s.Created(content);
                    var crt = (RectTransform)content.transform;
                    crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(.5f, 1f);
                    crt.sizeDelta = new Vector2(W, H);
                    crt.anchoredPosition = Vector2.zero;
                    var comp = content.AddComponent(fit);
                    fit.GetField("designWidth")?.SetValue(comp, W);
                    fit.GetField("designHeight")?.SetValue(comp, H);
                    s.Frame = crt;
                    container = crt;
                    ctx.Created.Add($"Content ({fit.Name} {W}x{H}, neo mép trên)");
                }
                Canvas.ForceUpdateCanvases();
                BuildNodes(ctx, s, ctx.Spec["nodes"] as JArray, container, new[] { 0f, 0f, W, H }, root);

                if (inScene) { ctx.Spec["scene"] = scene; ctx.Spec["root"] = Session.ScenePath(root); ctx.Spec.Remove("prefab"); }
                else { ctx.Spec["prefab"] = prefab; ctx.Spec.Remove("scene"); ctx.Spec.Remove("root"); }

                if (dryRun) s.Discard();
                else
                {
                    s.Save();
                    SaveJson(specPath, ctx.Spec);
                }
            }
            if (!dryRun)
            {
                AssetDatabase.SaveAssets();
                if (inScene) notes.Add("Scene đã đổi nhưng chưa Save: xem lại rồi Ctrl+S (Ctrl+Z để hoàn tác).");
            }
            return new { target = TargetName(ctx.Spec), dry_run = dryRun, created = ctx.Created, changes = ctx.Changes, warnings = ctx.Warnings, notes };
        }

        /// <summary>Tắt xuống dòng (TMP mới: textWrappingMode, cũ: enableWordWrapping).</summary>
        static void NoWrap(TMP_Text tmp)
        {
            var mode = typeof(TMP_Text).GetProperty("textWrappingMode");
            if (mode != null) mode.SetValue(tmp, Enum.Parse(mode.PropertyType, "NoWrap"));
            else typeof(TMP_Text).GetProperty("enableWordWrapping")?.SetValue(tmp, false);
        }

        static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetTypes().FirstOrDefault(t => t.Name == name && typeof(Component).IsAssignableFrom(t)); } catch { return null; } })
            .FirstOrDefault(t => t != null);

        /// <summary>Anchor theo vị trí trong khung cha (toạ độ PSD): 1/3 trên → neo trên, 1/3 dưới → neo dưới, còn lại giữa; rộng ≥ 90% khung → stretch ngang.</summary>
        static string SetAnchors(RectTransform rt, float[] r, float[] pr)
        {
            float pw = Mathf.Max(1, pr[2] - pr[0]), ph = Mathf.Max(1, pr[3] - pr[1]);
            float fx = ((r[0] + r[2]) / 2f - pr[0]) / pw, fy = ((r[1] + r[3]) / 2f - pr[1]) / ph;
            bool wide = (r[2] - r[0]) >= .9f * pw;
            float ax = fx < 1f / 3f ? 0f : fx > 2f / 3f ? 1f : .5f;
            float ay = fy < 1f / 3f ? 1f : fy > 2f / 3f ? 0f : .5f;
            rt.anchorMin = new Vector2(wide ? 0f : ax, ay);
            rt.anchorMax = new Vector2(wide ? 1f : ax, ay);
            rt.pivot = new Vector2(.5f, .5f);
            string v = ay == 1f ? "trên" : ay == 0f ? "dưới" : "giữa";
            string h = wide ? "stretch ngang" : ax == 0f ? "trái" : ax == 1f ? "phải" : "giữa";
            return $"{v}-{h}";
        }

        static void BuildNodes(Ctx ctx, Session s, JArray nodes, Transform parent, float[] pr, Transform root)
        {
            if (nodes == null) return;
            foreach (JObject n in nodes)
            {
                var r = Rect(n["rect"]);
                if ((bool?)n["skip"] == true || r == null) continue;
                string kind = (string)n["kind"];
                var t = Create(s, kind, (string)n["name"], parent);
                var rt = (RectTransform)t;
                string path = RelPath(root, t), anchor = SetAnchors(rt, r, pr);
                if (kind == "text")
                {
                    var tmp = t.GetComponent<TMP_Text>();
                    tmp.text = (string)n["text"] ?? "";
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    NoWrap(tmp);
                    string align = (string)n["align"] ?? "center";
                    tmp.alignment = align == "left" ? TextAlignmentOptions.Left : align == "right" ? TextAlignmentOptions.Right : TextAlignmentOptions.Center;
                    ApplyText(ctx, n, tmp, path);
                    // Khung PSD sát glyph: nới cho chữ động dài hơn / dấu tiếng Việt, giữ mép theo căn lề.
                    float w = (r[2] - r[0]) * 1.1f + 16f, h = (r[3] - r[1]) * 1.35f;
                    float cy = (r[1] + r[3]) / 2f;
                    float x0 = align == "left" ? r[0] : align == "right" ? r[2] - w : (r[0] + r[2] - w) / 2f;
                    s.SetRect(rt, new[] { x0, cy - h / 2f, x0 + w, cy + h / 2f });
                }
                else
                {
                    var img = t.GetComponent<Image>();
                    if (img != null) ApplySprite(ctx, n, img, path);
                    if (kind == "button") t.GetComponent<Button>().targetGraphic = img;
                    s.SetRect(rt, r);
                }
                foreach (var st in (n["states"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    // State (disabled, invader…) = object con riêng, tắt sẵn.
                    var so = (JObject)st.Value;
                    var stT = (RectTransform)Create(s, "image", st.Name, t);
                    stT.anchorMin = Vector2.zero; stT.anchorMax = Vector2.one; stT.offsetMin = stT.offsetMax = Vector2.zero;
                    ApplySprite(ctx, so, stT.GetComponent<Image>(), RelPath(root, stT));
                    stT.gameObject.SetActive(false);
                    so["target"] = RelPath(root, stT);
                }
                n["target"] = path;
                ctx.Created.Add($"{path} ({kind}, neo {anchor})");
                BuildNodes(ctx, s, n["children"] as JArray, t, r, root);
            }
        }

        // ------------------------------------------------------------------ render_prefab

        /// <summary>
        /// Render UI vào PNG W×H qua camera ortho + canvas ScreenSpaceCamera trong preview scene, không lưu gì.
        /// Prefab: load prefab. Scene: copy canvas gốc chứa `root` sang preview scene, tắt các nhánh khác để giữ đúng layout.
        /// </summary>
        public static object Render(string prefab, string scene, string rootPath, string outPath, int width, int height, JObject states)
        {
            GameObject root = null, camGo = null, canvasGo = null;
            Scene preview = default;
            RenderTexture rtex = null;
            Texture2D tex = null;
            var missing = new List<string>();
            bool inScene = !string.IsNullOrEmpty(scene);
            try
            {
                Transform target;
                if (inScene)
                {
                    var sc = SceneManager.GetSceneByPath(scene);
                    if (!sc.IsValid() || !sc.isLoaded) sc = EditorSceneManager.OpenScene(scene, OpenSceneMode.Additive);
                    var parts = (rootPath ?? "").Split(new[] { '/' }, 2);
                    var top = sc.GetRootGameObjects().FirstOrDefault(g => g.name == parts[0]);
                    var src = top == null ? null : parts.Length == 1 ? top.transform : top.transform.Find(parts[1]);
                    if (src == null) throw new Exception($"Không thấy '{rootPath}' trong {scene}");
                    var rootCanvas = src.GetComponentInParent<Canvas>(true)?.rootCanvas;
                    if (rootCanvas == null) throw new Exception($"'{rootPath}' không nằm trong Canvas nào");
                    preview = EditorSceneManager.NewPreviewScene();
                    root = Object.Instantiate(rootCanvas.gameObject);
                    SceneManager.MoveGameObjectToScene(root, preview);
                    string rel = RelPath(rootCanvas.transform, src);
                    target = string.IsNullOrEmpty(rel) ? root.transform : root.transform.Find(rel);
                    target.gameObject.SetActive(true);
                    for (var x = target; x != root.transform; x = x.parent)
                    {
                        x.gameObject.SetActive(true);
                        foreach (Transform sib in x.parent) if (sib != x) sib.gameObject.SetActive(false);
                    }
                }
                else
                {
                    root = PrefabUtility.LoadPrefabContents(prefab);
                    target = root.transform;
                }
                foreach (var p in states?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var t = FindTarget(target, p.Name);
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

                Canvas canvas;
                if (inScene) canvas = root.GetComponent<Canvas>();
                else
                {
                    canvasGo = new GameObject("PsdLayoutRenderCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
                    SceneManager.MoveGameObjectToScene(canvasGo, root.scene);
                    canvas = canvasGo.GetComponent<Canvas>();
                    var scaler = canvasGo.GetComponent<CanvasScaler>();
                    scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                    scaler.referenceResolution = new Vector2(width, height);
                    scaler.matchWidthOrHeight = 1f;
                    root.transform.SetParent(canvasGo.transform, false);
                    var rrt = root.transform as RectTransform;
                    if (rrt != null) { rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = rrt.offsetMax = Vector2.zero; }
                }
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = 50;
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
                if (inScene)
                {
                    if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                }
                else
                {
                    if (root != null && root.transform.parent != null) root.transform.SetParent(null, false);
                    if (camGo) Object.DestroyImmediate(camGo);
                    if (canvasGo) Object.DestroyImmediate(canvasGo);
                    if (root != null) PrefabUtility.UnloadPrefabContents(root);
                }
                if (rtex) { rtex.Release(); Object.DestroyImmediate(rtex); }
                if (tex) Object.DestroyImmediate(tex);
            }
            return new { file = outPath, size = new[] { width, height }, missing_states = missing };
        }
    }
}
