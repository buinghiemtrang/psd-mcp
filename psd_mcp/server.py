"""psd-mcp v1: đọc cây layer PSD, tìm state ẩn, xuất sprite đã cắt alpha, render và so ảnh.

Toạ độ luôn theo PSD: gốc trên-trái, rect = [x0, y0, x1, y1].
Layer được chỉ định bằng id (số `layer_id`) hoặc đường dẫn tên "Group/Sub/Layer".
"""

from __future__ import annotations

import json
import os
import re
from pathlib import Path

import numpy as np
from mcp.server.mcpserver import MCPServer
from PIL import Image, ImageChops, ImageDraw
from psd_tools import PSDImage

mcp = MCPServer("psd2ui", instructions="Đọc PSD của art: cây layer, state ẩn, xuất sprite cắt alpha, render, so ảnh với Unity. Toạ độ PSD gốc trên-trái.")

# PSD lớn mở mất vài giây: cache theo đường dẫn + mtime.
_cache: dict[str, tuple[float, PSDImage]] = {}


def _open(path: str) -> PSDImage:
    path = os.path.abspath(os.path.expanduser(path))
    mtime = os.path.getmtime(path)
    hit = _cache.get(path)
    if hit and hit[0] == mtime:
        return hit[1]
    psd = PSDImage.open(path)
    _cache[path] = (mtime, psd)
    _rects.clear()
    return psd


def _path_of(layer) -> str:
    names = []
    while layer is not None and layer.kind != "psdimage":
        names.append(layer.name)
        layer = layer.parent
    return "/".join(reversed(names))


def _find(psd: PSDImage, ref) -> object:
    """ref = layer_id (int hoặc chuỗi số) hoặc đường dẫn tên; tên trùng thì báo lỗi kèm danh sách id."""
    if isinstance(ref, int) or (isinstance(ref, str) and ref.isdigit()):
        lid = int(ref)
        for l in psd.descendants():
            if l.layer_id == lid:
                return l
        raise ValueError(f"Không có layer id {lid}")
    hits = [l for l in psd.descendants() if _path_of(l) == ref or (("/" not in ref) and l.name == ref)]
    if not hits:
        raise ValueError(f"Không có layer '{ref}'")
    if len(hits) > 1:
        raise ValueError(f"Tên '{ref}' trùng, dùng id: " + ", ".join(f"{l.layer_id} ({_path_of(l)})" for l in hits))
    return hits[0]


def _alpha_rect(layer, W: int, H: int):
    """Rect thật của layer lá theo alpha (bbox của smart object hay to bằng cả canvas). Layer ẩn vẫn tính."""
    x0, y0, x1, y1 = layer.bbox
    if x1 <= x0 or y1 <= y0:
        return None
    try:
        im = layer.composite(force=True, layer_filter=lambda l: True)
    except Exception:
        im = None
    if im is None:
        return [max(0, x0), max(0, y0), min(W, x1), min(H, y1)]
    bb = im.convert("RGBA").getchannel("A").getbbox()
    if bb is None:
        return None
    r = [x0 + bb[0], y0 + bb[1], x0 + bb[2], y0 + bb[3]]
    return [max(0, r[0]), max(0, r[1]), min(W, r[2]), min(H, r[3])]


_rects: dict[tuple, list | None] = {}


def _rect(layer, W: int, H: int):
    """Rect thật: lá = cắt alpha khi cần; group = hợp rect các con (cả con ẩn, vì bbox group ẩn = 0)."""
    key = (id(layer), layer.layer_id)
    if key in _rects:
        return _rects[key]
    if layer.is_group():
        rs = [r for r in (_rect(c, W, H) for c in layer) if r]
        r = [min(a[0] for a in rs), min(a[1] for a in rs), max(a[2] for a in rs), max(a[3] for a in rs)] if rs else None
    else:
        x0, y0, x1, y1 = layer.bbox
        big = (x1 - x0) * (y1 - y0) > 0.5 * W * H
        r = _alpha_rect(layer, W, H) if big or layer.kind == "smartobject" or x0 < 0 or y0 < 0 or x1 > W or y1 > H \
            else ([x0, y0, x1, y1] if x1 > x0 and y1 > y0 else None)
    _rects[key] = r
    return r


def _hex(values) -> str:
    # FillColor Values = [a, r, g, b] 0..1
    a, r, g, b = (float(v) for v in values)
    s = "#{:02X}{:02X}{:02X}".format(round(r * 255), round(g * 255), round(b * 255))
    return s if a >= 0.999 else s + "{:02X}".format(round(a * 255))


def _text_info(layer) -> dict:
    info = {"text": layer.text}
    try:
        st = layer.engine_dict["StyleRun"]["RunArray"][0]["StyleSheet"]["StyleSheetData"]
        fonts = layer.resource_dict["FontSet"]
        info["font"] = str(fonts[st.get("Font", 0)]["Name"]).strip("'\"")
        info["size"] = round(float(st["FontSize"]) * float(layer.transform[3]), 1)
        if "FillColor" in st:
            info["color"] = _hex(st["FillColor"]["Values"])
    except Exception:
        pass
    return info


def _layer_row(l, W, H, real_rect: bool) -> dict:
    row = {"id": l.layer_id, "path": _path_of(l), "kind": l.kind,
           "rect": _rect(l, W, H) if real_rect else list(l.bbox)}
    if not l.visible:
        row["hidden"] = True
    if l.has_effects():
        row["fx"] = sorted({e.__class__.__name__ for e in l.effects if e.enabled})
    if l.kind == "type":
        row.update(_text_info(l))
    return row


@mcp.tool()
def psd_tree(path: str, root: str | None = None, depth: int = 0, visible_only: bool = False,
             real_rect: bool = True) -> str:
    """Cây layer của PSD (hoặc của group `root`). Mỗi dòng: id, kind, đường dẫn, rect PSD [x0,y0,x1,y1],
    hidden, fx, và font/size/color/text với layer chữ. `depth` > 0 giới hạn số cấp tính từ root.
    `real_rect` cắt rect theo alpha cho smart object / layer to bằng canvas."""
    psd = _open(path)
    W, H = psd.width, psd.height
    base = _find(psd, root) if root else psd
    base_depth = 0 if base is psd else _path_of(base).count("/") + 1
    rows = []
    items = ([base] if base is not psd else []) + list(base.descendants())
    for l in items:
        d = _path_of(l).count("/") + 1 - base_depth
        if depth and d > depth:
            continue
        if visible_only and not l.is_visible():
            continue
        rows.append("  " * d + json.dumps(_layer_row(l, W, H, real_rect), ensure_ascii=False))
    return f"canvas {W}x{H}, {len(rows)} layer\n" + "\n".join(rows)


def _is_backdrop(r, W, H) -> bool:
    return (r[2] - r[0]) * (r[3] - r[1]) > 0.8 * W * H


def _iou(a, b) -> float:
    ix = max(0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    ua = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / ua if ua > 0 else 0.0


@mcp.tool()
def psd_states(path: str, root: str | None = None, min_overlap: float = 0.5) -> str:
    """Tìm layer/group ẩn (GD hay giữ state khác hoặc bản cũ ở dạng ẩn) và ghép với layer hiện ở cùng chỗ
    (IoU ≥ min_overlap). Kết quả để quyết định state nào là normal / disabled / invader..., trùng tên thì hỏi user."""
    psd = _open(path)
    W, H = psd.width, psd.height
    base = _find(psd, root) if root else psd
    layers = [l for l in base.descendants() if l.kind != "type"]
    hidden = [l for l in layers if not l.visible and (l.parent is None or l.parent.is_visible() or l.parent is base)]
    shown = [l for l in layers if l.is_visible() and l.is_group()] + \
            [l for l in layers if l.is_visible() and not l.is_group()]
    rect = {l.layer_id: _rect(l, W, H) for l in hidden + shown}
    out = []
    for h in hidden:
        rh = rect[h.layer_id]
        if not rh:
            continue
        anc = set()
        p = h.parent
        while p is not None:
            anc.add(id(p))
            p = p.parent
        best = sorted(((_iou(rh, rect[s.layer_id]), s) for s in shown
                       if rect[s.layer_id] and id(s) not in anc and not _is_backdrop(rect[s.layer_id], W, H)),
                      key=lambda t: -t[0])
        best = [(v, s) for v, s in best if v >= min_overlap][:3]
        out.append({
            "hidden": {"id": h.layer_id, "path": _path_of(h), "kind": h.kind, "rect": rh},
            "matches": [{"id": s.layer_id, "path": _path_of(s), "iou": round(v, 2)} for v, s in best],
        })
    return json.dumps(out, ensure_ascii=False, separators=(",", ":"))


def _slug(name: str) -> str:
    s = re.sub(r"[^0-9A-Za-z]+", "_", name).strip("_").lower()
    return s or "layer"


def _composite_layer(psd, t, hide_text: bool = False, pad: int = 40, skip: set | None = None):
    """Ảnh RGBA đã cắt alpha + rect PSD của layer/group `t` (cả khi `t` đang ẩn). FX của group cha vẫn áp.
    `skip` = id() các layer bỏ ra (vd. chữ con của nút)."""
    W, H = psd.width, psd.height
    anc = set()
    p = t.parent
    while p is not None and p is not psd:
        anc.add(id(p))
        p = p.parent
    sub = {id(d) for d in t.descendants()} if t.is_group() else set()

    def flt(l):
        if hide_text and l.kind == "type":
            return False
        if skip and id(l) in skip:
            return False
        if l is t or id(l) in anc:
            return True
        return id(l) in sub and l.visible

    r = _rect(t, W, H) or list(t.bbox)
    vp = (max(0, r[0] - pad), max(0, r[1] - pad), min(W, r[2] + pad), min(H, r[3] + pad))
    if vp[2] <= vp[0] or vp[3] <= vp[1]:
        return None, None
    im = psd.composite(viewport=vp, layer_filter=flt, force=True).convert("RGBA")
    bb = im.getchannel("A").getbbox()
    if bb is None:
        return None, None
    return im.crop(bb), [vp[0] + bb[0], vp[1] + bb[1], vp[0] + bb[2], vp[1] + bb[3]]


def _unique(out: Path, stem: str, used: set) -> Path:
    f, n = out / f"{stem}.png", 2
    while str(f) in used:
        f, n = out / f"{stem}_{n}.png", n + 1
    used.add(str(f))
    return f


@mcp.tool()
def psd_export(path: str, layers: list[str], out_dir: str, hide_text: bool = False, pad: int = 40,
               suffix: str = "") -> str:
    """Xuất từng layer/group ra PNG đã cắt alpha, kèm rect PSD. Composite cả document nhưng chỉ giữ cây con
    của layer + các group cha, nên FX của group cha vẫn có. Layer ẩn vẫn xuất được (để lấy state ẩn).
    `hide_text` bỏ layer chữ (chữ động sẽ làm bằng TMP). `pad` = lề viewport cho glow. `suffix` thêm vào tên file (vd. "_v2")."""
    psd = _open(path)
    out = Path(os.path.expanduser(out_dir))
    out.mkdir(parents=True, exist_ok=True)
    result, used = [], set()
    for ref in layers:
        t = _find(psd, ref)
        im, rect = _composite_layer(psd, t, hide_text, pad)
        if im is None:
            result.append({"layer": ref, "error": "trống sau khi cắt alpha"})
            continue
        file = _unique(out, _slug(t.name) + suffix, used)
        im.save(file)
        result.append({"layer": ref, "id": t.layer_id, "path": _path_of(t), "file": str(file),
                       "rect": rect, "size": [rect[2] - rect[0], rect[3] - rect[1]]})
    return json.dumps(result, ensure_ascii=False, separators=(",", ":"))


@mcp.tool()
def psd_render(path: str, out: str, layers: list[str] | None = None, viewport: list[int] | None = None,
               hide: list[str] | None = None, max_side: int = 0) -> str:
    """Render ảnh composite của PSD làm ảnh tham chiếu. Không truyền `layers` = các layer đang hiện;
    có `layers` = chỉ các layer/group đó (+ group cha). `hide` tắt thêm layer. `viewport` = [x0,y0,x1,y1].
    `max_side` > 0 thu nhỏ ảnh để đọc cho nhẹ."""
    psd = _open(path)
    keep = None
    if layers:
        keep = set()
        for ref in layers:
            t = _find(psd, ref)
            keep.add(id(t))
            keep |= {id(d) for d in t.descendants()} if t.is_group() else set()
            p = t.parent
            while p is not None and p is not psd:
                keep.add(id(p))
                p = p.parent
    drop = {id(_find(psd, r)) for r in (hide or [])}

    def flt(l):
        if id(l) in drop:
            return False
        if keep is None:
            return l.is_visible()
        return id(l) in keep and (l.visible or l.is_group())

    vp = tuple(viewport) if viewport else None
    im = psd.composite(viewport=vp, layer_filter=flt, force=True).convert("RGBA")
    if max_side and max(im.size) > max_side:
        im.thumbnail((max_side, max_side))
    dst = Path(os.path.expanduser(out))
    dst.parent.mkdir(parents=True, exist_ok=True)
    im.save(dst)
    return json.dumps({"file": str(dst), "size": list(im.size)})


def _fit_h(im: Image.Image, h: int) -> Image.Image:
    return im.resize((max(1, round(im.width * h / im.height)), h), Image.LANCZOS)


@mcp.tool()
def psd_compare(psd_png: str, unity_png: str, out: str, height: int = 960, threshold: float = 32) -> str:
    """Ghép cạnh nhau: ảnh PSD | ảnh Unity | ảnh chênh lệch (đỏ = khác), cùng chiều cao `height`.
    Pixel lệch quá `threshold` (0..255) mới tô đỏ, để bỏ nhiễu nén JPG. Trả điểm chênh trung bình và các vùng lệch lớn nhất theo toạ độ ảnh PSD."""
    a = Image.open(os.path.expanduser(psd_png)).convert("RGBA")
    b = Image.open(os.path.expanduser(unity_png)).convert("RGBA")
    if b.size != a.size:
        b = b.resize(a.size, Image.LANCZOS)
    bg = Image.new("RGBA", a.size, (0, 0, 0, 255))
    fa, fb = Image.alpha_composite(bg, a).convert("RGB"), Image.alpha_composite(bg, b).convert("RGB")
    diff = np.asarray(ImageChops.difference(fa, fb)).astype(np.float32).mean(axis=2)
    score = float(diff.mean())
    # Vùng lệch: chia lưới 12x12 ô, lấy 5 ô lệch nhất.
    gh, gw = 12, 12
    ch, cw = a.height / gh, a.width / gw
    cells = []
    for gy in range(gh):
        for gx in range(gw):
            y0, y1, x0, x1 = int(gy * ch), int((gy + 1) * ch), int(gx * cw), int((gx + 1) * cw)
            cells.append((float(diff[y0:y1, x0:x1].mean()), [x0, y0, x1, y1]))
    cells.sort(key=lambda c: -c[0])
    mask = Image.fromarray(((diff > threshold) * 255).astype(np.uint8))
    red = Image.new("RGB", a.size, (255, 40, 40))
    dimg = Image.composite(red, fa.point(lambda v: v // 3), mask)
    draw = ImageDraw.Draw(dimg)
    for v, r in cells[:5]:
        if v > 8:
            draw.rectangle(r, outline=(255, 255, 0), width=max(2, a.width // 300))
    tiles = [_fit_h(fa, height), _fit_h(fb, height), _fit_h(dimg, height)]
    sheet = Image.new("RGB", (sum(t.width for t in tiles) + 16, height), (40, 40, 40))
    x = 0
    for t in tiles:
        sheet.paste(t, (x, 0))
        x += t.width + 8
    dst = Path(os.path.expanduser(out))
    dst.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(dst)
    return json.dumps({"file": str(dst), "mean_diff": round(score, 2),
                       "worst_cells": [{"rect": r, "diff": round(v, 1)} for v, r in cells[:5]]})


# ---------------------------------------------------------------- v2: spec + ghép sprite

_TINT_FX = {"GradientOverlay", "ColorOverlay", "PatternOverlay", "InnerGlow"}


def _load_jpg(jpg: str, W: int, H: int) -> Image.Image:
    im = Image.open(os.path.expanduser(jpg)).convert("RGB")
    return im if im.size == (W, H) else im.resize((W, H), Image.LANCZOS)


def _text_tint(layer, ref: Image.Image) -> dict | None:
    """Màu chữ thật lấy từ ảnh JPG art xuất (psd-tools không vẽ gradient / glow của chữ).
    Lấy pixel trong lòng chữ (alpha cao, co viền 1px), trung vị cả chữ + dải trên / dải dưới cho TMP vertex gradient."""
    from PIL import ImageFilter
    try:
        g = layer.composite(force=True, layer_filter=lambda l: True)
    except Exception:
        return None
    if g is None:
        return None
    x0, y0 = layer.bbox[0], layer.bbox[1]
    a = g.convert("RGBA").getchannel("A")
    core = a.filter(ImageFilter.MinFilter(3))
    m = np.asarray(core) > 200
    if m.sum() < 20:
        m = np.asarray(a) > 128
    if m.sum() < 5:
        return None
    crop = np.asarray(ref.crop((x0, y0, x0 + a.width, y0 + a.height))).astype(np.float32)
    ys = np.where(m.any(axis=1))[0]
    top, bot = ys[0], ys[-1]
    band = max(1, (bot - top + 1) // 3)
    hexc = lambda c: "#{:02X}{:02X}{:02X}".format(*(int(round(v)) for v in c))
    allc = np.median(crop[m], axis=0)
    mt, mb = m.copy(), m.copy()
    mt[top + band:, :] = False
    mb[:bot - band + 1, :] = False
    ct = np.median(crop[mt], axis=0) if mt.any() else allc
    cb = np.median(crop[mb], axis=0) if mb.any() else allc
    out = {"color": hexc(allc)}
    if float(np.abs(ct - cb).max()) > 30:
        out["gradient"] = {"top": hexc(ct), "bottom": hexc(cb)}
    return out


def _match(name: str, path: str, rules: dict, key: str) -> bool:
    return any(r == path or r == name for r in rules.get(key, []))


def _is_button_name(name: str) -> bool:
    n = name.lower()
    return bool(re.search(r"(^|[^a-z])(btn|button)([^a-z]|$)", n)) or n.startswith("btn")


@mcp.tool()
def psd_to_spec(path: str, out_dir: str, root: str | None = None, prefab: str | None = None,
                jpg: str | None = None, rules: dict | None = None, suffix: str = "_v2") -> str:
    """Sinh layout spec JSON (+ PNG đã cắt alpha) từ PSD để tool Unity `apply_layout_spec` áp lên prefab.
    Quy tắc mặc định: layer chữ → node text (TMP), `dynamic` nếu có chữ số; group tên btn/button → node button
    (nền ghép, bỏ chữ) + chữ con; group chỉ có layer ảnh → gộp thành 1 image; layer nền to bằng canvas bị bỏ.
    Group/layer ẩn trùng chỗ với node đang hiện → `states` của node đó.
    `jpg` = ảnh art xuất, dùng để lấy màu chữ có gradient / glow. `rules` (danh sách path hoặc tên):
    flatten, skip, button, image_text (chữ xuất thành ảnh), dynamic, static, keep_backdrop.
    Ghi `<out_dir>/spec.json`; field `target` để trống, ghép bằng tool Unity `match_layout_spec` hoặc sửa tay."""
    psd = _open(path)
    W, H = psd.width, psd.height
    rules = rules or {}
    out = Path(os.path.expanduser(out_dir))
    out.mkdir(parents=True, exist_ok=True)
    ref = _load_jpg(jpg, W, H) if jpg else None
    used: set = set()
    skipped: list = []
    by_id: dict = {}

    def save(im, name):
        f = _unique(out, _slug(name) + suffix, used)
        im.save(f)
        return f.name

    def text_node(l):
        info = _text_info(l)
        n = {"name": l.name, "psd_id": l.layer_id, "psd_path": _path_of(l), "kind": "text",
             "rect": _rect(l, W, H), **{k: v for k, v in info.items()}}
        txt = info.get("text", "") or ""
        n["dynamic"] = _match(l.name, n["psd_path"], rules, "dynamic") or (
            not _match(l.name, n["psd_path"], rules, "static") and bool(re.search(r"\d", txt)))
        fx = sorted({e.__class__.__name__ for e in l.effects if e.enabled}) if l.has_effects() else []
        if fx:
            n["fx"] = fx
        if ref is not None and _TINT_FX & set(fx):
            t = _text_tint(l, ref)
            if t:
                n.update(t)
                n["color_from"] = "jpg"
        return n

    def image_node(l, kind="image", hide_text=False, skip=None):
        im, rect = _composite_layer(psd, l, hide_text, skip=skip)
        if im is None:
            return None
        return {"name": l.name, "psd_id": l.layer_id, "psd_path": _path_of(l), "kind": kind,
                "rect": rect, "png": save(im, l.name)}

    def build(l):
        p = _path_of(l)
        if not l.visible or _match(l.name, p, rules, "skip"):
            return None
        r = _rect(l, W, H)
        if not r:
            return None
        if _is_backdrop(r, W, H) and not _match(l.name, p, rules, "keep_backdrop"):
            skipped.append({"psd_path": p, "why": "nền to bằng canvas"})
            return None
        if l.kind == "type":
            n = image_node(l) if _match(l.name, p, rules, "image_text") else text_node(l)
        elif l.is_group():
            texts = [d for d in l.descendants() if d.kind == "type" and d.is_visible()
                     and not _match(d.name, _path_of(d), rules, "image_text")]
            if _match(l.name, p, rules, "button") or _is_button_name(l.name):
                n = image_node(l, "button", skip={id(t) for t in texts})
                if n is not None:
                    n["children"] = [text_node(t) for t in texts]
            elif _match(l.name, p, rules, "flatten") or not any(d.kind == "type" or (d.is_group() and _is_button_name(d.name))
                                                               for d in l.descendants()):
                n = image_node(l)
            else:
                n = {"name": l.name, "psd_id": l.layer_id, "psd_path": p, "kind": "group", "rect": r,
                     "children": [c for c in (build(x) for x in l) if c]}
        else:
            n = image_node(l)
        if n is not None:
            by_id[l.layer_id] = n
        return n

    base = _find(psd, root) if root else psd
    nodes = [c for c in (build(x) for x in base)] if base is psd or base.is_group() else [build(base)]
    nodes = [n for n in nodes if n]

    # State ẩn: group/layer ẩn (cha đang hiện) trùng chỗ node đang hiện → node.states
    shown = [n for n in by_id.values() if n["kind"] in ("image", "button")]
    for h in (base.descendants() if base is not psd else psd.descendants()):
        if h.visible or not (h.parent is None or h.parent.is_visible()) or h.kind == "type":
            continue
        rh = _rect(h, W, H)
        if not rh or _is_backdrop(rh, W, H):
            continue
        best = max(shown, key=lambda n: _iou(rh, n["rect"]), default=None)
        if best is None or _iou(rh, best["rect"]) < 0.6:
            skipped.append({"psd_path": _path_of(h), "why": "layer ẩn, không trùng chỗ node nào"})
            continue
        im, rect = _composite_layer(psd, h, hide_text=True)
        if im is not None:
            best.setdefault("states", {})[h.name] = {"png": save(im, best["name"] + "_" + h.name), "rect": rect,
                                                     "psd_path": _path_of(h)}

    spec = {"version": 1, "psd": os.path.abspath(os.path.expanduser(path)), "root": root, "canvas": [W, H],
            "png_dir": str(out), "prefab": prefab, "sprite_dir": None, "suffix": suffix, "nodes": nodes,
            "skipped": skipped}
    f = out / "spec.json"
    f.write_text(json.dumps(spec, ensure_ascii=False, indent=1))

    lines = []

    def outline(ns, d):
        for n in ns:
            extra = [n.get("png") or "", "dynamic" if n.get("dynamic") else "",
                     "states:" + ",".join(n["states"]) if n.get("states") else "",
                     n.get("color", "") if n["kind"] == "text" else ""]
            lines.append("  " * d + f'{n["kind"]} "{n["name"]}" {n["rect"]} ' + " ".join(e for e in extra if e))
            outline(n.get("children", []), d + 1)

    outline(nodes, 0)
    return f"spec: {f}\n" + "\n".join(lines) + (f"\nbỏ qua {len(skipped)}: " + "; ".join(
        f'{s["psd_path"]} ({s["why"]})' for s in skipped[:12]) if skipped else "")


_thumbs: dict[str, tuple[float, np.ndarray, float]] = {}


def _thumb(file: str):
    m = os.path.getmtime(file)
    hit = _thumbs.get(file)
    if hit and hit[0] == m:
        return hit[1], hit[2]
    im = Image.open(file).convert("RGBA")
    bb = im.getchannel("A").getbbox()
    if bb:
        im = im.crop(bb)
    ar = im.width / max(1, im.height)
    arr = np.asarray(im.resize((64, 64), Image.BILINEAR)).astype(np.float32)
    a = arr[..., 3:4] / 255.0
    arr = np.concatenate([arr[..., :3] * a + 128 * (1 - a), arr[..., 3:4]], axis=2)  # nền xám để so màu
    _thumbs[file] = (m, arr, ar)
    return arr, ar


def _assets_rel(p: str) -> str:
    p = p.replace(os.sep, "/")
    i = p.find("/Assets/")
    return p[i + 1:] if i >= 0 else p


@mcp.tool()
def psd_match_sprites(sprite_dirs: list[str], spec: str | None = None, pngs: list[str] | None = None,
                      max_diff: float = 10, top: int = 3) -> str:
    """So PNG xuất từ PSD với sprite đang có trong project (resize 64×64, chênh trung bình 0..255).
    Chênh < `max_diff` và cùng tỉ lệ → đề xuất dùng lại. Truyền `spec` = đường dẫn spec.json thì ghi
    `match` vào từng node và đặt `sprite: "reuse:Assets/..."` cho node chưa chọn sprite.
    `sprite_dirs` = thư mục tuyệt đối (quét đệ quy *.png)."""
    files = []
    for d in sprite_dirs:
        for r, _, fs in os.walk(os.path.expanduser(d)):
            files += [os.path.join(r, f) for f in fs if f.lower().endswith(".png")]
    if not files:
        return "Không thấy sprite nào trong " + ", ".join(sprite_dirs)
    thumbs = [_thumb(f) for f in files]
    stack = np.stack([t[0] for t in thumbs])
    ars = np.array([t[1] for t in thumbs])

    def best(png):
        q, qa = _thumb(png)
        diff = np.abs(stack - q).mean(axis=(1, 2, 3))
        same = np.abs(np.log(ars / qa)) < 0.05
        order = np.argsort(diff)[:top]
        return [{"sprite": _assets_rel(files[i]), "diff": round(float(diff[i]), 1), "same_ratio": bool(same[i])}
                for i in order]

    out = []
    if spec:
        sp = Path(os.path.expanduser(spec))
        data = json.loads(sp.read_text())
        png_dir = Path(data["png_dir"])

        def walk(ns):
            for n in ns:
                if n.get("png"):
                    m = best(str(png_dir / n["png"]))
                    n["match"] = m[0]
                    ok = m[0]["diff"] < max_diff and m[0]["same_ratio"]
                    if ok and not n.get("sprite"):
                        n["sprite"] = "reuse:" + m[0]["sprite"]
                    out.append(f'{n["name"]}: {m[0]["sprite"]} diff {m[0]["diff"]}' + (" → dùng lại" if ok else ""))
                for st in (n.get("states") or {}).values():
                    m = best(str(png_dir / st["png"]))
                    st["match"] = m[0]
                    if m[0]["diff"] < max_diff and m[0]["same_ratio"] and not st.get("sprite"):
                        st["sprite"] = "reuse:" + m[0]["sprite"]
                walk(n.get("children", []))

        walk(data["nodes"])
        sp.write_text(json.dumps(data, ensure_ascii=False, indent=1))
        return f"{len(files)} sprite đã quét, ghi vào {sp}\n" + "\n".join(out)
    for p in pngs or []:
        out.append({"png": p, "best": best(os.path.expanduser(p))})
    return json.dumps(out, ensure_ascii=False, separators=(",", ":"))


# ---------------------------------------------------------------- v3: tool phụ

@mcp.tool()
def psd_text_tint(path: str, layer: str, jpg: str) -> str:
    """Màu thật của layer chữ lấy từ ảnh JPG art xuất (khi chữ có GradientOverlay / InnerGlow mà psd-tools không vẽ).
    Trả `color` (trung vị) và `gradient` {top, bottom} nếu trên–dưới khác rõ, để set TMP vertex gradient."""
    psd = _open(path)
    t = _find(psd, layer)
    if t.kind != "type":
        return f"'{layer}' không phải layer chữ"
    r = _text_tint(t, _load_jpg(jpg, psd.width, psd.height))
    return json.dumps(r or {"error": "không lấy được pixel chữ"})


@mcp.tool()
def psd_split_text(out_dir: str, png: str | None = None, path: str | None = None, layer: str | None = None,
                   min_gap: int = 20, axis: str = "x") -> str:
    """Tách 1 ảnh gộp (vd. icon + chữ "INVINCIBLE FOR 5S") thành từng cụm theo khoảng trống alpha ≥ `min_gap` px
    trên trục `axis` ("x" ngang, "y" dọc). Nguồn: `png`, hoặc `path` + `layer` (khi đó rect theo toạ độ PSD)."""
    if png:
        im, origin = Image.open(os.path.expanduser(png)).convert("RGBA"), (0, 0)
        stem = Path(png).stem
    else:
        psd = _open(path)
        t = _find(psd, layer)
        im, rect = _composite_layer(psd, t)
        if im is None:
            return "layer trống"
        origin, stem = (rect[0], rect[1]), _slug(t.name)
    a = np.asarray(im.getchannel("A")) > 16
    prof = a.any(axis=0 if axis == "x" else 1)
    runs, start, gap = [], None, 0
    for i, v in enumerate(prof):
        if v:
            if start is None:
                start = i
            gap = 0
            end = i
        elif start is not None:
            gap += 1
            if gap >= min_gap:
                runs.append((start, end + 1))
                start = None
    if start is not None:
        runs.append((start, end + 1))
    out = Path(os.path.expanduser(out_dir))
    out.mkdir(parents=True, exist_ok=True)
    res = []
    for k, (s0, s1) in enumerate(runs):
        box = (s0, 0, s1, im.height) if axis == "x" else (0, s0, im.width, s1)
        piece = im.crop(box)
        bb = piece.getchannel("A").getbbox()
        piece = piece.crop(bb)
        x0, y0 = box[0] + bb[0] + origin[0], box[1] + bb[1] + origin[1]
        f = out / f"{stem}_part{k + 1}.png"
        piece.save(f)
        res.append({"file": str(f), "rect": [x0, y0, x0 + piece.width, y0 + piece.height]})
    return json.dumps(res, ensure_ascii=False, separators=(",", ":"))


@mcp.tool()
def psd_variant(png: str, mode: str = "gray", out_dir: str | None = None, brightness: float = 0.75) -> str:
    """Sinh bản nút disable khi PSD không có sẵn: `gray` = bỏ màu + tối bớt (`brightness`), `dark` = chỉ tối đi.
    Giữ alpha. Ghi `<tên>_off.png` (cùng thư mục hoặc `out_dir`)."""
    src = Path(os.path.expanduser(png))
    im = Image.open(src).convert("RGBA")
    rgb, a = im.convert("RGB"), im.getchannel("A")
    if mode == "gray":
        rgb = rgb.convert("L").convert("RGB")
    rgb = rgb.point(lambda v: int(v * brightness))
    res = rgb.convert("RGBA")
    res.putalpha(a)
    out = Path(os.path.expanduser(out_dir)) if out_dir else src.parent
    out.mkdir(parents=True, exist_ok=True)
    f = out / f"{src.stem}_off.png"
    res.save(f)
    return json.dumps({"file": str(f)})


def main():
    mcp.run()


if __name__ == "__main__":
    main()
