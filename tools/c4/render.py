#!/usr/bin/env python3
"""Render a C4 architecture model into one SVG per view, Markdown pages and a PowerPoint deck, each in a dark theme (the
default, under the plain file name) and a light one (the same name with -light before the extension).

The model (YAML) is the one source: its elements and relationships are drawn by every view that places them, so a
system context, its containers, their components, the dynamic views and the deployment can never disagree. Each view
places its elements by hand (centre x, y on a 1600 x 900 canvas), and every relationship is routed orthogonally from the
boxes it joins, or along the waypoints it names.

usage:
    python tools/c4/render.py <model.yaml>             render everything the model names
    python tools/c4/render.py <model.yaml> --check     exit 1 when a committed SVG or page differs from the model
    python tools/c4/render.py <model.yaml> --no-pptx   skip the deck

Needs PyYAML, Pillow (text measurement) and python-pptx (the deck). Text is measured with Segoe UI, the font both
outputs name; the icons are files under tools/c4/icons (see tools/c4/README.md).
"""

from __future__ import annotations

import argparse
import copy
import html
import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

import yaml

HERE = Path(__file__).resolve().parent
ICONS = HERE / "icons"

W, H = 1600, 900
FONT = "Segoe UI"
FONT_STACK = "'Segoe UI', 'Helvetica Neue', Helvetica, Arial, sans-serif"

# ----------------------------------------------------------------------------------------------------------- style ---


@dataclass(frozen=True)
class Theme:
    """The colours of one rendering. The elements keep C4's blues in both themes, so a view reads the same in either;
    the canvas, the text, the lines and the frames change."""

    name: str
    suffix: str  # what the theme adds to a file's name: dark is the default and adds nothing
    paper: str  # the canvas
    ink: str  # titles, labels, frame names
    ink2: str  # the key's text and group names
    muted: str  # technologies and frame types
    faint: str  # the footer
    line: str  # relationships and their arrows
    accent: str  # the level tag and "view inside"
    lead: str  # the description under the title
    tag_fill: str  # behind the level tag
    rule: str  # the line above the key
    card_fill: str  # the deck's map cards and buttons
    card_stroke: str
    node_fill: str  # a deployment node
    node_stroke: str
    boundary_fill: str
    boundary_stroke: str
    group_stroke: str
    label_fill: str  # behind a relationship's label
    chip: str  # behind an element's icon
    chip_infra: str
    on_chip: str  # a line icon on a chip, where the element gives no colour
    kinds: dict = field(default_factory=dict)  # kind: (fill, stroke, text, sub-text, icon colour)


THEMES = {
    "dark": Theme(
        name="dark", suffix="", paper="#0D1117", ink="#E6EDF3", ink2="#C9D1D9", muted="#8D96A0", faint="#6E7681",
        line="#7D8590", accent="#58A6FF", lead="#9DA7B3", tag_fill="#12253D", rule="#30363D", card_fill="#161B22",
        card_stroke="#30363D", node_fill="#10161D", node_stroke="#3D444D", boundary_fill="#0F141B",
        boundary_stroke="#6E7681", group_stroke="#4D5560", label_fill="#0D1117", chip="#EAF2FB", chip_infra="#DCE3EB",
        on_chip="#334155",
        kinds={
            "person": ("#08427B", "#2B6CB0", "#FFFFFF", "#D6E4F5", None),
            "system": ("#1168BD", "#3F8BD8", "#FFFFFF", "#DCEBFA", "#1168BD"),
            "container": ("#2F7CC7", "#5A9BDB", "#FFFFFF", "#E1EEFA", "#2869AA"),
            "component": ("#9CC8F2", "#C4DFF8", "#0B2540", "#1E3A5F", "#1F5F9E"),
            "external": ("#6B7280", "#8B929D", "#FFFFFF", "#E5E7EB", "#4B5563"),
            "infra": ("#161B22", "#484F58", "#E6EDF3", "#9DA7B3", "#334155"),
        },
    ),
    "light": Theme(
        name="light", suffix="-light", paper="#FFFFFF", ink="#0F172A", ink2="#334155", muted="#64748B",
        faint="#94A3B8", line="#475569", accent="#1168BD", lead="#475569", tag_fill="#E8F1FB", rule="#E2E8F0",
        card_fill="#FFFFFF", card_stroke="#CBD5E1", node_fill="#F8FAFC", node_stroke="#94A3B8",
        boundary_fill="#F7FAFD", boundary_stroke="#7C8A99", group_stroke="#A3AFBC", label_fill="#FFFFFF",
        chip="#FFFFFF", chip_infra="#F1F5F9", on_chip="#334155",
        kinds={
            "person": ("#08427B", "#073763", "#FFFFFF", "#D6E4F5", None),
            "system": ("#1168BD", "#0E5AA3", "#FFFFFF", "#DCEBFA", "#1168BD"),
            "container": ("#2F7CC7", "#2869AA", "#FFFFFF", "#E1EEFA", "#2869AA"),
            "component": ("#9CC8F2", "#6FA9DF", "#0B2540", "#1E3A5F", "#1F5F9E"),
            "external": ("#6B7280", "#5B626E", "#FFFFFF", "#E5E7EB", "#4B5563"),
            "infra": ("#FFFFFF", "#94A3B8", "#0F172A", "#475569", "#334155"),
        },
    ),
}
T = THEMES["dark"]
AI_FILL, AI_TEXT = "#F5A524", "#3A2500"


def use_theme(name: str) -> Theme:
    """Draw with the named theme from here on."""
    global T
    T = THEMES[name]
    return T


def themed(name: str) -> str:
    """A file's name in the current theme: dark keeps the name, light adds its suffix before the extension."""
    stem, dot, ext = name.rpartition(".")
    return f"{stem}{T.suffix}{dot}{ext}" if dot else f"{name}{T.suffix}"


SIZES = {
    "person": (230, 175),
    "system": (300, 170),
    "container": (290, 165),
    "component": (270, 150),
    "external": (290, 165),
    "infra": (250, 120),
}

NAME_SIZE, TYPE_SIZE, DESC_SIZE = 17.0, 12.0, 12.5
NAME_LH, TYPE_LH, DESC_LH = 21.0, 15.0, 16.0
# A compact view draws its elements smaller, to keep every description on a dense diagram.
SMALL = {"name": (15.0, 19.0), "type": (11.0, 14.0), "desc": (12.0, 15.0)}
LARGE = {"name": (NAME_SIZE, NAME_LH), "type": (TYPE_SIZE, TYPE_LH), "desc": (DESC_SIZE, DESC_LH)}
REL_SIZE, REL_TECH_SIZE = 12.0, 11.0
CHIP, ICON = 34.0, 24.0
CORNER_CHIP, CORNER_ICON = 26.0, 18.0

# ------------------------------------------------------------------------------------------------- text measuring ---

_FONTS: dict[tuple[str, float], object] = {}
_FONT_FILES = {
    "regular": ["segoeui.ttf", "Arial.ttf", "arial.ttf", "DejaVuSans.ttf"],
    "bold": ["segoeuib.ttf", "Arial Bold.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf"],
    "semibold": ["seguisb.ttf", "segoeuib.ttf", "arialbd.ttf", "DejaVuSans-Bold.ttf"],
    "italic": ["segoeuii.ttf", "ariali.ttf", "DejaVuSans-Oblique.ttf"],
}
_FONT_DIRS = [Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts", Path("/Library/Fonts"), Path("/usr/share/fonts/truetype/dejavu")]


def _font(weight: str, size: float):
    key = (weight, size)
    if key not in _FONTS:
        from PIL import ImageFont

        for name in _FONT_FILES[weight]:
            for folder in _FONT_DIRS:
                path = folder / name
                if path.exists():
                    _FONTS[key] = ImageFont.truetype(str(path), size=int(round(size * 4)))
                    break
            if key in _FONTS:
                break
        else:
            raise SystemExit(f"render.py: no font to measure {weight} text with; install Segoe UI or DejaVu Sans")
    return _FONTS[key]


def text_width(text: str, size: float, weight: str = "regular") -> float:
    return _font(weight, size).getlength(text) / 4.0


def wrap(text: str, width: float, size: float, weight: str = "regular") -> list[str]:
    lines: list[str] = []
    for paragraph in str(text).split("\n"):
        words = paragraph.split()
        line = ""
        for word in words:
            trial = f"{line} {word}".strip()
            if not line or text_width(trial, size, weight) <= width:
                line = trial
            else:
                lines.append(line)
                line = word
        lines.append(line)
    return lines


# --------------------------------------------------------------------------------------------------------- model ---


@dataclass
class Element:
    id: str
    kind: str
    name: str
    type: str = ""
    tech: str = ""
    description: str = ""
    shape: str = "box"
    icon: str = ""
    tags: list[str] = field(default_factory=list)
    zoom: str = ""
    ref: str = ""
    badge: str = ""
    detail: str = ""
    brief: str = ""


@dataclass
class Placed:
    el: Element
    x: float
    y: float
    w: float
    h: float
    zoom: str = ""
    compact: bool = False
    brief: bool = False
    text: str | None = None
    badge: str | None = None

    @property
    def description(self) -> str:
        if self.text is not None:
            return self.text
        return self.el.brief if self.brief and self.el.brief else self.el.description

    @property
    def chip(self) -> float:
        return CORNER_CHIP if self.compact else CHIP

    @property
    def icon(self) -> float:
        return CORNER_ICON if self.compact else ICON

    @property
    def left(self) -> float:
        return self.x - self.w / 2

    @property
    def right(self) -> float:
        return self.x + self.w / 2

    @property
    def top(self) -> float:
        return self.y - self.h / 2

    @property
    def bottom(self) -> float:
        return self.y + self.h / 2


@dataclass
class Frame:
    """A boundary (system or container), a deployment node or a group drawn around elements."""

    label: str
    type: str
    style: str
    x: float
    y: float
    w: float
    h: float
    icon: str = ""
    corner: object = "bottom-left"


@dataclass
class Route:
    points: list[tuple[float, float]]
    rel: dict
    label_at: tuple[float, float] = (0.0, 0.0)


TYPE_NAMES = {
    "person": "Person",
    "system": "Software System",
    "container": "Container",
    "component": "Component",
    "external": "Software System",
    "infra": "Infrastructure Node",
}


def load_model(path: Path) -> dict:
    with path.open(encoding="utf-8") as handle:
        model = yaml.safe_load(handle)
    elements = {}
    for key, raw in (model.get("elements") or {}).items():
        raw = dict(raw)
        kind = raw.pop("kind")
        if kind not in T.kinds:
            raise SystemExit(f"{path}: element {key} has kind {kind!r}; one of {sorted(T.kinds)}")
        if kind == "person":
            raw.setdefault("shape", "person")
        elements[key] = Element(id=key, kind=kind, **{k: v for k, v in raw.items()})
    model["elements"] = elements
    ids = [v["id"] for v in model["views"]]
    if len(ids) != len(set(ids)):
        raise SystemExit(f"{path}: two views share an id")
    for el in elements.values():
        if el.zoom and el.zoom not in ids:
            raise SystemExit(f"{path}: element {el.id} zooms into {el.zoom}, which is no view")
    return model


def type_line(el: Element, kind_override: str = "") -> str:
    kind = TYPE_NAMES[kind_override or el.kind]
    if el.type:
        kind = el.type
    return f"[{kind}: {el.tech}]" if el.tech else f"[{kind}]"


# ------------------------------------------------------------------------------------------------------- layout ---


def place(view: dict, elements: dict[str, Element]) -> dict[str, Placed]:
    placed: dict[str, Placed] = {}
    sizes = {**SIZES, **{k: tuple(v) for k, v in (view.get("sizes") or {}).items()}}
    for key, spec in (view.get("elements") or {}).items():
        if key not in elements:
            raise SystemExit(f"view {view['id']}: places {key}, which the model does not hold")
        el = elements[key]
        if isinstance(spec, dict):
            at = spec["at"]
            w, h = spec.get("size", sizes[el.kind])
            zoom = spec.get("zoom", el.zoom)
        else:
            at = spec[:2]
            w, h = (spec[2], spec[3]) if len(spec) >= 4 else sizes[el.kind]
            zoom = el.zoom
        if zoom == view["id"]:
            zoom = ""
        listed = view.get("brief") or []
        brief = bool(spec.get("brief")) if isinstance(spec, dict) and "brief" in spec else (listed == "*" or key in listed)
        text = spec.get("desc") if isinstance(spec, dict) else None
        badge = spec.get("badge") if isinstance(spec, dict) else None
        placed[key] = Placed(el, float(at[0]), float(at[1]), float(w), float(h), zoom, bool(view.get("compact")), brief, text, badge)
    return placed


def frames(view: dict, placed: dict[str, Placed]) -> list[Frame]:
    out = []
    for spec in view.get("boundaries") or []:
        pad = spec.get("pad", 26)
        if "rect" in spec:
            x, y, w, h = spec["rect"]
        else:
            members = [placed[m] for m in spec["members"]]
            top_pad = spec.get("top", 58 if spec.get("style") == "node" else pad)
            bottom_pad = spec.get("bottom", 46 if spec.get("style", "boundary") == "boundary" else pad)
            x = min(p.left for p in members) - pad
            y = min(p.top for p in members) - top_pad
            w = max(p.right for p in members) + pad - x
            h = max(p.bottom for p in members) + bottom_pad - y
        out.append(Frame(spec["label"], spec.get("type", ""), spec.get("style", "boundary"), x, y, w, h, spec.get("icon", ""), spec.get("labelAt", "bottom-left")))
    return out


def _side_point(p: Placed, side: str, offset: float = 0.0) -> tuple[float, float]:
    if side == "left":
        return (p.left, p.y + offset)
    if side == "right":
        return (p.right, p.y + offset)
    if side == "top":
        return (p.x + offset, p.top)
    return (p.x + offset, p.bottom)


def _toward(p: Placed, target: tuple[float, float]) -> tuple[float, float]:
    """Where a line leaving p's box toward target crosses its border, staying orthogonal when target lines up."""
    tx, ty = target
    if p.top < ty < p.bottom:
        return (p.right if tx > p.x else p.left, ty)
    if p.left < tx < p.right:
        return (tx, p.bottom if ty > p.y else p.top)
    return (p.right if tx > p.x else p.left, p.y)


def route(rel: dict, a: Placed, b: Placed) -> list[tuple[float, float]]:
    if "via" in rel:
        via = [tuple(map(float, v)) for v in rel["via"]]
        start = _side_point(a, *rel["exit"]) if "exit" in rel else _toward(a, via[0])
        end = _side_point(b, *rel["entry"]) if "entry" in rel else _toward(b, via[-1])
        return [start, *via, end]
    if "exit" in rel or "entry" in rel:
        start = _side_point(a, *rel.get("exit", [_auto_side(a, b)]))
        end = _side_point(b, *rel.get("entry", [_auto_side(b, a)]))
        return _orthogonal(start, end, rel.get("route", "auto"), rel.get("mid"))
    shift = float(rel.get("shift", 0))
    top, bottom = max(a.top, b.top), min(a.bottom, b.bottom)
    if bottom - top >= 24:
        y = (top + bottom) / 2 + shift
        return [(a.right, y), (b.left, y)] if b.x > a.x else [(a.left, y), (b.right, y)]
    left, right = max(a.left, b.left), min(a.right, b.right)
    if right - left >= 24:
        x = (left + right) / 2 + shift
        return [(x, a.bottom), (x, b.top)] if b.y > a.y else [(x, a.top), (x, b.bottom)]
    mode = rel.get("route", "hv")
    if mode == "vh":
        start = (a.x, a.bottom if b.y > a.y else a.top)
        end = (b.right if a.x > b.x else b.left, b.y)
        return [start, (a.x, b.y), end]
    if mode == "hvh":
        start = (a.right if b.x > a.x else a.left, a.y)
        end = (b.left if b.x > a.x else b.right, b.y)
        mid = rel.get("mid", (start[0] + end[0]) / 2)
        return [start, (mid, a.y), (mid, b.y), end]
    if mode == "vhv":
        start = (a.x, a.bottom if b.y > a.y else a.top)
        end = (b.x, b.top if b.y > a.y else b.bottom)
        mid = rel.get("mid", (start[1] + end[1]) / 2)
        return [start, (a.x, mid), (b.x, mid), end]
    start = (a.right if b.x > a.x else a.left, a.y)
    end = (b.x, b.top if b.y > a.y else b.bottom)
    return [start, (b.x, a.y), end]


def _auto_side(a: Placed, b: Placed) -> str:
    dx, dy = b.x - a.x, b.y - a.y
    if abs(dx) * a.h > abs(dy) * a.w:
        return "right" if dx > 0 else "left"
    return "bottom" if dy > 0 else "top"


def _orthogonal(start, end, mode, mid) -> list[tuple[float, float]]:
    (sx, sy), (ex, ey) = start, end
    if abs(sx - ex) < 0.5 or abs(sy - ey) < 0.5:
        return [start, end]
    if mode in ("vh",):
        return [start, (sx, ey), end]
    if mode in ("hv",):
        return [start, (ex, sy), end]
    if mode == "vhv":
        m = mid if mid is not None else (sy + ey) / 2
        return [start, (sx, m), (ex, m), end]
    m = mid if mid is not None else (sx + ex) / 2
    return [start, (m, sy), (m, ey), end]


def label_anchor(points: list[tuple[float, float]], rel: dict) -> tuple[float, float]:
    if "label" in rel and isinstance(rel.get("at"), list):
        return tuple(map(float, rel["at"]))
    segments = list(zip(points, points[1:]))
    if "seg" in rel:
        seg = segments[int(rel["seg"])]
    else:
        seg = max(segments, key=lambda s: abs(s[0][0] - s[1][0]) + abs(s[0][1] - s[1][1]))
    t = float(rel.get("t", 0.5))
    x = seg[0][0] + (seg[1][0] - seg[0][0]) * t + float(rel.get("dx", 0))
    y = seg[0][1] + (seg[1][1] - seg[0][1]) * t + float(rel.get("dy", 0))
    return (x, y)


def relationship_label(rel: dict) -> tuple[list[str], list[str], float, float]:
    width = float(rel.get("width", 190))
    label = wrap(rel.get("label", ""), width, REL_SIZE) if rel.get("label") else []
    tech = wrap(f"[{rel['tech']}]", width, REL_TECH_SIZE, "italic") if rel.get("tech") else []
    w = max([text_width(t, REL_SIZE) for t in label] + [text_width(t, REL_TECH_SIZE, "italic") for t in tech] + [0])
    h = len(label) * 15 + len(tech) * 14
    return label, tech, w, h


# ----------------------------------------------------------------------------------------------------- icons ---


class Icons:
    """The icon files, read once: Lucide line icons drawn in a colour, and Azure's own icons drawn as they are."""

    def __init__(self):
        self._cache: dict[str, tuple[str, str]] = {}

    def resolve(self, name: str) -> Path:
        family, _, icon = name.partition(":")
        path = ICONS / family / f"{icon}.svg"
        if not path.exists():
            raise SystemExit(f"render.py: icon {name} has no file {path}")
        return path

    def parts(self, name: str) -> tuple[str, str]:
        """The icon's viewBox and its inner markup, with every id made unique to the icon."""
        if name not in self._cache:
            text = self.resolve(name).read_text(encoding="utf-8")
            view_box = re.search(r'viewBox="([^"]+)"', text).group(1)
            inner = re.search(r"<svg[^>]*>(.*)</svg>", text, re.S).group(1)
            prefix = re.sub(r"[^a-z0-9]", "-", name.lower())
            inner = re.sub(r'id="([^"]+)"', lambda m: f'id="{prefix}-{m.group(1)}"', inner)
            inner = re.sub(r"url\(#([^)]+)\)", lambda m: f"url(#{prefix}-{m.group(1)})", inner)
            inner = re.sub(r'href="#([^"]+)"', lambda m: f'href="#{prefix}-{m.group(1)}"', inner)
            inner = re.sub(r"<title>.*?</title>", "", inner, flags=re.S)
            self._cache[name] = (view_box, inner.strip())
        return self._cache[name]

    def is_line(self, name: str) -> bool:
        return name.startswith("lucide:")

    def standalone(self, name: str, colour: str) -> bytes:
        """The icon as an SVG file of its own, in the colour asked for when it is a line icon (for the deck)."""
        text = self.resolve(name).read_text(encoding="utf-8")
        if self.is_line(name):
            text = text.replace("currentColor", colour)
        return text.encode("utf-8")

    def png(self, name: str) -> Path:
        family, _, icon = name.partition(":")
        path = ICONS / "png" / f"{family}-{icon}.png"
        if not path.exists():
            raise SystemExit(f"render.py: icon {name} has no PNG fallback {path}; run node tools/c4/rasterize-icons.mjs")
        return path


# --------------------------------------------------------------------------------------------------- SVG output ---


def esc(text: str) -> str:
    return html.escape(str(text), quote=True)


class Svg:
    def __init__(self, title: str, desc: str):
        self.body: list[str] = []
        self.symbols: dict[str, str] = {}
        self.title, self.desc = title, desc

    def add(self, markup: str):
        self.body.append(markup)

    def text(self, x, y, s, size, colour, weight="normal", anchor="middle", style=""):
        italic = ' font-style="italic"' if style == "italic" else ""
        self.add(
            f'<text x="{x:.1f}" y="{y:.1f}" font-size="{size}" fill="{colour}" font-weight="{weight}" '
            f'text-anchor="{anchor}"{italic}>{esc(s)}</text>'
        )

    def icon(self, icons: Icons, name: str, x, y, size, colour):
        symbol = "i-" + re.sub(r"[^a-z0-9]", "-", name.lower())
        if symbol not in self.symbols:
            view_box, inner = icons.parts(name)
            attrs = ' fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"' if icons.is_line(name) else ""
            self.symbols[symbol] = f'<symbol id="{symbol}" viewBox="{view_box}"{attrs}>{inner}</symbol>'
        self.add(
            f'<use href="#{symbol}" xlink:href="#{symbol}" x="{x:.1f}" y="{y:.1f}" width="{size}" height="{size}" '
            f'color="{colour}" style="color:{colour}"/>'
        )

    def render(self) -> str:
        defs = "".join(self.symbols[k] for k in sorted(self.symbols))
        marker = (
            '<marker id="arrow" viewBox="0 0 12 12" refX="11" refY="6" markerWidth="11" markerHeight="11" '
            f'markerUnits="userSpaceOnUse" orient="auto-start-reverse"><path d="M0,1 L12,6 L0,11 z" fill="{T.line}"/></marker>'
        )
        return (
            f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 {W} {H}" '
            f'width="{W}" height="{H}" role="img" aria-labelledby="title desc" font-family="{esc(FONT_STACK)}">\n'
            f'<title id="title">{esc(self.title)}</title>\n<desc id="desc">{esc(self.desc)}</desc>\n'
            f"<defs>{marker}{defs}</defs>\n"
            f'<rect width="{W}" height="{H}" fill="{T.paper}"/>\n' + "\n".join(self.body) + "\n</svg>\n"
        )


def text_block(el: Element, w: float, chip: float, description: str, corner: bool = False) -> tuple[list[str], list[str], list[str], float]:
    """The lines an element shows, and the height they take below its top padding."""
    inner = w - 26
    name = wrap(el.name, w - 2 * (8 + CORNER_CHIP + 6) if corner else inner, NAME_SIZE, "bold")
    tline = wrap(type_line(el), inner, TYPE_SIZE)
    desc = wrap(description, inner, DESC_SIZE) if description else []
    height = (chip + 10 if chip else 0) + len(name) * NAME_LH + len(tline) * TYPE_LH + (6 + len(desc) * DESC_LH if desc else 0)
    return name, tline, desc, height


@dataclass
class Layout:
    """Where an element's parts go: shared by the SVG and the deck so both draw the same thing."""

    body: tuple[float, float, float, float]
    head: tuple[float, float, float] | None
    cap: float
    chip: tuple[float, float] | None
    lines: list[tuple[str, float, float, str, str]]  # text, y baseline, size, weight, role
    overflow: float
    tx: float = 0.0
    anchor: str = "middle"
    text_w: float = 0.0


def element_layout(p: Placed) -> Layout:
    """Where an element's parts go. A compact view draws a card: the icon on the left, the text left-aligned beside
    it, in smaller type; any other view centres the icon above the text."""
    el = p.el
    x, y, w, h = p.left, p.top, p.w, p.h
    head = None
    if el.shape == "person":
        r = min(30.0, h * 0.18)
        head = (p.x, y + r, r)
        y, h = y + r * 1.55, h - r * 1.55
    cap = {"cylinder": 12.0, "folder": 10.0, "browser": 18.0, "terminal": 18.0}.get(el.shape, 0.0)
    if p.compact and el.shape == "cylinder":
        cap = 9.0
    if p.compact and el.shape in ("browser", "terminal"):
        cap = 14.0
    chip_on = bool(el.icon) and el.shape != "person"
    card = chip_on and p.compact
    sizes = SMALL if p.compact else LARGE
    (ns, nlh), (ts, tlh), (ds, dlh) = sizes["name"], sizes["type"], sizes["desc"]
    top_room = cap * (2 if el.shape == "cylinder" else 1)
    avail = h - top_room - 14
    if card:
        text_w = w - 12 - p.chip - 10 - 12
        tx, anchor = x + 12 + p.chip + 10, "start"
    else:
        text_w = w - 26
        tx, anchor = p.x, "middle"
    name = wrap(el.name, text_w, ns, "bold")
    tline = wrap(type_line(el), text_w, ts)
    desc = wrap(p.description, text_w, ds) if p.description else []
    height = len(name) * nlh + len(tline) * tlh + (6 + len(desc) * dlh if desc else 0)
    if chip_on and not card:
        height += p.chip + 10
    cursor = y + top_room + 7 + max(0.0, (avail - height) / 2)
    chip = None
    if card:
        chip = (x + 12, cursor + 1)
    elif chip_on:
        chip = (p.x - p.chip / 2, cursor)
        cursor += p.chip + 10
    lines = []
    for line in name:
        lines.append((line, cursor + ns * 0.95, ns, "bold", "name"))
        cursor += nlh
    for line in tline:
        lines.append((line, cursor + ts * 0.95, ts, "normal", "type"))
        cursor += tlh
    if desc:
        cursor += 6
        for line in desc:
            lines.append((line, cursor + ds * 0.95, ds, "normal", "desc"))
            cursor += dlh
    return Layout((x, y, w, h), head, cap, chip, lines, height - avail, tx, anchor, text_w)


def svg_element(svg: Svg, icons: Icons, p: Placed, link: str | None):
    el = p.el
    fill, stroke, text, sub, icon_colour = T.kinds[el.kind]
    lay = element_layout(p)
    x, y, w, h = lay.body
    if link:
        svg.add(f'<a href="{esc(link)}" xlink:href="{esc(link)}">')
    svg.add(f"<g><title>{esc(el.name)}: {esc(el.detail or el.description)}</title>")
    if lay.head:
        cx, cy, r = lay.head
        svg.add(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{r:.1f}" fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>')
        svg.add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{min(34, h / 3):.1f}" fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>')
    elif el.shape == "cylinder":
        c = lay.cap
        svg.add(
            f'<path d="M{x:.1f},{y + c:.1f} A{w / 2:.1f},{c:.1f} 0 0 1 {x + w:.1f},{y + c:.1f} L{x + w:.1f},{y + h - c:.1f} '
            f'A{w / 2:.1f},{c:.1f} 0 0 1 {x:.1f},{y + h - c:.1f} Z" fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>'
        )
        svg.add(f'<path d="M{x:.1f},{y + c:.1f} A{w / 2:.1f},{c:.1f} 0 0 0 {x + w:.1f},{y + c:.1f}" fill="none" stroke="{stroke}" stroke-width="1.2"/>')
    elif el.shape == "folder":
        c = lay.cap
        tab = min(110.0, w * 0.38)
        svg.add(
            f'<path d="M{x + 8:.1f},{y:.1f} L{x + tab - 6:.1f},{y:.1f} L{x + tab + 6:.1f},{y + c:.1f} L{x + w - 10:.1f},{y + c:.1f} '
            f'Q{x + w:.1f},{y + c:.1f} {x + w:.1f},{y + c + 10:.1f} L{x + w:.1f},{y + h - 10:.1f} Q{x + w:.1f},{y + h:.1f} {x + w - 10:.1f},{y + h:.1f} '
            f'L{x + 10:.1f},{y + h:.1f} Q{x:.1f},{y + h:.1f} {x:.1f},{y + h - 10:.1f} L{x:.1f},{y + 8:.1f} Q{x:.1f},{y:.1f} {x + 8:.1f},{y:.1f} Z" '
            f'fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>'
        )
    else:
        radius = 6 if el.shape in ("browser", "terminal") else 12
        svg.add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{radius}" fill="{fill}" stroke="{stroke}" stroke-width="1.2"/>')
        if el.shape in ("browser", "terminal"):
            c = lay.cap
            svg.add(f'<path d="M{x:.1f},{y + c:.1f} L{x + w:.1f},{y + c:.1f}" stroke="{sub}" stroke-opacity="0.55" stroke-width="1"/>')
            if el.shape == "browser":
                for i in range(3):
                    svg.add(f'<circle cx="{x + 13 + i * 11:.1f}" cy="{y + c / 2:.1f}" r="3" fill="{sub}" fill-opacity="0.8"/>')
            else:
                svg.text(x + 12, y + c / 2 + 4.2, ">_", 11, sub, "bold", "start")
    if lay.chip:
        cx, cy = lay.chip
        chip_fill = T.chip if el.kind != "infra" else T.chip_infra
        svg.add(f'<rect x="{cx:.1f}" y="{cy:.1f}" width="{p.chip}" height="{p.chip}" rx="{p.chip / 4.2:.1f}" fill="{chip_fill}"/>')
        svg.icon(icons, el.icon, cx + (p.chip - p.icon) / 2, cy + (p.chip - p.icon) / 2, p.icon, icon_colour or T.on_chip)
    for line, baseline, size, weight, role in lay.lines:
        colour = text if role == "name" else sub
        svg.text(lay.tx, baseline, line, size, colour, weight, lay.anchor)
    _svg_badges(svg, p)
    svg.add("</g>")
    if link:
        svg.add("</a>")


def badges(p: Placed) -> list[tuple[str, str, str]]:
    out = []
    if "ai" in p.el.tags:
        out.append(("AI", AI_FILL, AI_TEXT))
    badge = p.badge if p.badge is not None else p.el.badge
    if badge:
        out.append((badge, T.ink, T.paper))
    if p.zoom:
        out.append(("view inside \u203a", T.paper, T.accent))
    return out


def badge_boxes(p: Placed) -> list[tuple[float, float, float, float, str, str, str]]:
    lay_top = p.top + (min(30.0, p.h * 0.18) * 1.55 if p.el.shape == "person" else 0)
    right = p.right - 8
    out = []
    for text, fill, colour in badges(p):
        w = text_width(text, 10.5, "bold") + 14
        out.append((right - w, lay_top - 9, w, 18, text, fill, colour))
        right -= w + 5
    return out


def _svg_badges(svg: Svg, p: Placed):
    for x, y, w, h, text, fill, colour in badge_boxes(p):
        stroke = f' stroke="{T.accent}" stroke-width="1"' if colour == T.accent else ""
        svg.add(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h}" rx="9" fill="{fill}"{stroke}/>')
        svg.text(x + w / 2, y + 12.6, text, 10.5, colour, "bold")


def svg_frame(svg: Svg, icons: Icons, f: Frame):
    if f.style == "node":
        svg.add(f'<rect x="{f.x:.1f}" y="{f.y:.1f}" width="{f.w:.1f}" height="{f.h:.1f}" rx="14" fill="{T.node_fill}" stroke="{T.node_stroke}" stroke-width="1.3"/>')
        tx = f.x + 16
        if f.icon:
            svg.icon(icons, f.icon, f.x + 14, f.y + 13, 26, T.ink2)
            tx = f.x + 48
        svg.text(tx, f.y + 25, f.label, 14.5, T.ink, "bold", "start")
        if f.type:
            svg.text(tx, f.y + 42, f"[{f.type}]", 11.5, T.muted, "normal", "start")
        return
    dash = "7 5" if f.style == "boundary" else "3 4"
    fill = T.boundary_fill if f.style == "boundary" else "none"
    stroke = T.boundary_stroke if f.style == "boundary" else T.group_stroke
    svg.add(f'<rect x="{f.x:.1f}" y="{f.y:.1f}" width="{f.w:.1f}" height="{f.h:.1f}" rx="16" fill="{fill}" stroke="{stroke}" stroke-width="1.4" stroke-dasharray="{dash}"/>')
    if f.style == "group":
        svg.text(f.x + 16, f.y + 22, f.label, 13.5, T.ink2, "bold", "start")
        if f.type:
            svg.text(f.x + 16 + text_width(f.label, 13.5, "bold") + 8, f.y + 22, f"[{f.type}]", 11.5, T.muted, "normal", "start")
        return
    if isinstance(f.corner, list):
        tx, ty, anchor = float(f.corner[0]), float(f.corner[1]), "start"
    else:
        right = f.corner.endswith("right")
        tx, anchor = (f.x + f.w - 16, "end") if right else (f.x + 16, "start")
        ty = f.y + 24 if f.corner.startswith("top") else f.y + f.h - 26
    svg.text(tx, ty, f.label, 14.5, T.ink, "bold", anchor)
    if f.type:
        svg.text(tx, ty + 16, f"[{f.type}]", 11.5, T.muted, "normal", anchor)


def svg_relationship(svg: Svg, r: Route):
    rel = r.rel
    pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in r.points)
    dash = ' stroke-dasharray="6 5"' if rel.get("dashed") else ""
    both = ' marker-start="url(#arrow)"' if rel.get("both") else ""
    svg.add(f'<polyline points="{pts}" fill="none" stroke="{T.line}" stroke-width="1.6" stroke-linejoin="round"{dash}{both} marker-end="url(#arrow)"/>')


def svg_label(svg: Svg, r: Route):
    rel = r.rel
    label, tech, w, h = relationship_label(rel)
    if not label and not tech and "n" not in rel:
        return
    cx, cy = r.label_at
    step = rel.get("n")
    badge_w = 26 if step is not None else 0
    total = w + badge_w
    x0 = cx - total / 2
    svg.add(f'<rect x="{x0 - 5:.1f}" y="{cy - h / 2 - 3:.1f}" width="{total + 10:.1f}" height="{max(h, 22) + 6:.1f}" rx="5" fill="{T.label_fill}" fill-opacity="0.94"/>')
    if step is not None:
        by = cy if h <= 22 else cy - h / 2 + 11
        svg.add(f'<circle cx="{x0 + 11:.1f}" cy="{by:.1f}" r="11" fill="{T.ink}"/>')
        svg.text(x0 + 11, by + 4.3, str(step), 12, T.paper, "bold")
    tx = x0 + badge_w + w / 2
    yy = cy - h / 2
    for line in label:
        svg.text(tx, yy + 12, line, REL_SIZE, T.ink)
        yy += 15
    for line in tech:
        svg.text(tx, yy + 11, line, REL_TECH_SIZE, T.muted, "normal", "middle", "italic")
        yy += 14


def header(view: dict) -> tuple[str, str, list[str]]:
    return view["tag"], view["title"], wrap(view.get("description", ""), 1060, 15)


LEGEND_ORDER = [
    ("person", "Person"),
    ("system", "Software system in scope"),
    ("container", "Container"),
    ("component", "Component"),
    ("external", "External software system"),
    ("infra", "Infrastructure node"),
]
SHAPE_NAMES = {"cylinder": "Database", "folder": "File store", "browser": "Web application", "terminal": "Command-line tool"}
LEGEND_TOP, LEGEND_ROWS = 846.0, (865.0, 887.0)


def legend_items(view: dict, placed: dict[str, Placed], fr: list[Frame]) -> list[tuple[str, str]]:
    els = [p.el for p in placed.values()]
    kinds = {el.kind for el in els}
    items = [(k, label) for k, label in LEGEND_ORDER if k in kinds]
    shapes = {el.shape for el in els}
    items += [(f"shape:{s}", SHAPE_NAMES[s]) for s in SHAPE_NAMES if s in shapes]
    if any(el.icon for el in els):
        azure = any(el.icon.startswith("azure:") for el in els) or any(f.icon.startswith("azure:") for f in fr)
        items.append(("icon", "Icon: what it is" + (", or its Azure service" if azure else "")))
    if any("ai" in el.tags for el in els):
        items.append(("ai", "Calls or serves an AI model"))
    if any(p.zoom for p in placed.values()):
        items.append(("zoom", "Has a view of its inside"))
    if any(f.style == "boundary" for f in fr):
        items.append(("boundary", "Boundary"))
    if any(f.style == "node" for f in fr):
        items.append(("node", "Deployment node"))
    if any(f.style == "group" for f in fr):
        items.append(("group", "Group"))
    rels = view.get("relationships") or []
    if rels:
        items.append(("line", "Relationship: what it does [technology]"))
    if any(r.get("dashed") for r in rels):
        items.append(("dashed", "Optional, or on request"))
    if any("n" in r for r in rels):
        items.append(("step", "Step, in order"))
    return items


def legend_layout(items) -> list[tuple[str, str, float, float]]:
    x, row = 48.0, 0
    out = []
    for key, label in items:
        width = 30 + text_width(label, 12) + 22
        if x + width > W - 40 and row == 0:
            x, row = 48.0, 1
        out.append((key, label, x, LEGEND_ROWS[row]))
        x += width
    return out


def render_svg(model: dict, view: dict, icons: Icons) -> tuple[str, list[str]]:
    elements = model["elements"]
    placed = place(view, elements)
    fr = frames(view, placed)
    warnings: list[str] = []
    tag, title, desc = header(view)
    svg = Svg(f"{model['product']}: {title}", " ".join(desc))

    # Header: the level, the title and what the view shows.
    tw = text_width(tag, 11.5, "bold") + 22
    svg.add(f'<rect x="48" y="34" width="{tw:.1f}" height="22" rx="11" fill="{T.tag_fill}"/>')
    svg.text(48 + tw / 2, 49.5, tag, 11.5, T.accent, "bold")
    svg.text(48, 92, title, 28, T.ink, "600", "start")
    for i, line in enumerate(desc[:2]):
        svg.text(48, 120 + i * 20, line, 15, T.lead, "normal", "start")
    if len(desc) > 2:
        warnings.append(f"{view['id']}: the description runs to {len(desc)} lines; two fit")

    for f in sorted(fr, key=lambda f: -(f.w * f.h)):
        svg_frame(svg, icons, f)

    routes = []
    for rel in view.get("relationships") or []:
        a, b = placed.get(rel["from"]), placed.get(rel["to"])
        if a is None or b is None:
            raise SystemExit(f"view {view['id']}: a relationship joins {rel['from']} and {rel['to']}, not both placed")
        pts = route(rel, a, b)
        routes.append(Route(pts, rel, label_anchor(pts, rel)))
    for r in routes:
        svg_relationship(svg, r)
    for key, p in placed.items():
        link = f"{p.zoom}{T.suffix}.svg" if p.zoom else None
        svg_element(svg, icons, p, link)
        lay = element_layout(p)
        if lay.overflow > 0.5:
            warnings.append(f"{view['id']}: {key}'s text needs {lay.overflow:.0f} more units of height")
    for r in routes:
        svg_label(svg, r)

    # The key: what every colour, shape, icon, border and line of the view means.
    svg.add(f'<line x1="48" y1="{LEGEND_TOP}" x2="{W - 48}" y2="{LEGEND_TOP}" stroke="{T.rule}"/>')
    for key, label, x, y in legend_layout(legend_items(view, placed, fr)):
        _svg_legend_swatch(svg, icons, key, x, y)
        svg.text(x + 30, y + 4.3, label, 12, T.ink2, "normal", "start")
    _bounds_check(view, placed, routes, warnings, 104 + 20 * min(len(desc), 2) + 4)
    return svg.render(), warnings


def _svg_legend_swatch(svg: Svg, icons: Icons, key: str, x: float, y: float):
    if key in T.kinds:
        fill, stroke = T.kinds[key][0], T.kinds[key][1]
        if key == "person":
            svg.add(f'<circle cx="{x + 11:.1f}" cy="{y - 5:.1f}" r="4" fill="{fill}"/><rect x="{x + 3:.1f}" y="{y - 1:.1f}" width="16" height="9" rx="4" fill="{fill}"/>')
        else:
            svg.add(f'<rect x="{x:.1f}" y="{y - 8:.1f}" width="22" height="16" rx="4" fill="{fill}" stroke="{stroke}"/>')
    elif key == "shape:cylinder":
        svg.add(f'<path d="M{x + 2:.1f},{y - 5:.1f} A9,3 0 0 1 {x + 20:.1f},{y - 5:.1f} L{x + 20:.1f},{y + 6:.1f} A9,3 0 0 1 {x + 2:.1f},{y + 6:.1f} Z" fill="none" stroke="{T.ink2}" stroke-width="1.3"/>')
        svg.add(f'<path d="M{x + 2:.1f},{y - 5:.1f} A9,3 0 0 0 {x + 20:.1f},{y - 5:.1f}" fill="none" stroke="{T.ink2}" stroke-width="1.3"/>')
    elif key == "shape:folder":
        svg.add(f'<path d="M{x + 1:.1f},{y - 8:.1f} h7 l3,3 h10 v13 h-20 z" fill="none" stroke="{T.ink2}" stroke-width="1.3" stroke-linejoin="round"/>')
    elif key == "shape:browser":
        svg.add(f'<rect x="{x + 1:.1f}" y="{y - 8:.1f}" width="20" height="16" rx="2.5" fill="none" stroke="{T.ink2}" stroke-width="1.3"/>')
        svg.add(f'<path d="M{x + 1:.1f},{y - 3:.1f} h20" stroke="{T.ink2}" stroke-width="1.1"/>')
    elif key == "shape:terminal":
        svg.add(f'<rect x="{x + 1:.1f}" y="{y - 8:.1f}" width="20" height="16" rx="2.5" fill="none" stroke="{T.ink2}" stroke-width="1.3"/>')
        svg.text(x + 11, y + 4, ">_", 8.5, T.ink2, "bold")
    elif key == "icon":
        svg.add(f'<rect x="{x + 2:.1f}" y="{y - 9:.1f}" width="18" height="18" rx="4.5" fill="{T.chip}" stroke="{T.card_stroke}"/>')
        svg.icon(icons, "lucide:box", x + 5, y - 6, 12, T.on_chip)
    elif key == "ai":
        svg.add(f'<rect x="{x:.1f}" y="{y - 8:.1f}" width="22" height="16" rx="8" fill="{AI_FILL}"/>')
        svg.text(x + 11, y + 3.6, "AI", 9.5, AI_TEXT, "bold")
    elif key == "zoom":
        svg.add(f'<rect x="{x - 2:.1f}" y="{y - 8:.1f}" width="26" height="16" rx="8" fill="{T.paper}" stroke="{T.accent}"/>')
        svg.add(f'<path d="M{x + 8:.1f},{y - 4:.1f} l4,4 l-4,4" fill="none" stroke="{T.accent}" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>')
    elif key == "node":
        svg.add(f'<rect x="{x:.1f}" y="{y - 8:.1f}" width="22" height="16" rx="4" fill="{T.node_fill}" stroke="{T.node_stroke}"/>')
    elif key == "boundary":
        svg.add(f'<rect x="{x:.1f}" y="{y - 8:.1f}" width="22" height="16" rx="4" fill="{T.boundary_fill}" stroke="{T.boundary_stroke}" stroke-dasharray="4 3"/>')
    elif key == "group":
        svg.add(f'<rect x="{x:.1f}" y="{y - 8:.1f}" width="22" height="16" rx="4" fill="none" stroke="{T.group_stroke}" stroke-dasharray="2 3"/>')
    elif key == "line":
        svg.add(f'<line x1="{x:.1f}" y1="{y}" x2="{x + 20:.1f}" y2="{y}" stroke="{T.line}" stroke-width="1.6" marker-end="url(#arrow)"/>')
    elif key == "dashed":
        svg.add(f'<line x1="{x:.1f}" y1="{y}" x2="{x + 20:.1f}" y2="{y}" stroke="{T.line}" stroke-width="1.6" stroke-dasharray="5 4" marker-end="url(#arrow)"/>')
    elif key == "step":
        svg.add(f'<circle cx="{x + 11:.1f}" cy="{y}" r="9" fill="{T.ink}"/>')
        svg.text(x + 11, y + 4, "1", 10.5, T.paper, "bold")


def _bounds_check(view, placed, routes, warnings, top_limit=150.0):
    for key, p in placed.items():
        badge_top = p.top - 9 if badges(p) else p.top
        if p.left < 16 or p.right > W - 16 or badge_top < top_limit or p.bottom > LEGEND_TOP - 8:
            warnings.append(f"{view['id']}: {key} reaches outside the drawing area")
    keys = list(placed)
    for i, a in enumerate(keys):
        for b in keys[i + 1 :]:
            pa, pb = placed[a], placed[b]
            if pa.left < pb.right and pb.left < pa.right and pa.top < pb.bottom and pb.top < pa.bottom:
                warnings.append(f"{view['id']}: {a} and {b} overlap")
    for r in routes:
        for (x1, y1), (x2, y2) in zip(r.points, r.points[1:]):
            if abs(x1 - x2) > 0.5 and abs(y1 - y2) > 0.5:
                warnings.append(f"{view['id']}: {r.rel['from']} -> {r.rel['to']} has a diagonal segment")
            for key, p in placed.items():
                if key in (r.rel["from"], r.rel["to"]):
                    continue
                if _segment_hits(x1, y1, x2, y2, p):
                    warnings.append(f"{view['id']}: {r.rel['from']} -> {r.rel['to']} crosses {key}")
    name = lambda r: f"{r.rel['from']} -> {r.rel['to']}"
    for i, a in enumerate(routes):
        for b in routes[i + 1 :]:
            if _crossings(a, b):
                warnings.append(f"{view['id']}: {name(a)} crosses the line {name(b)}")
    rects = [(r, label_rect(r)) for r in routes]
    for i, (r, rect) in enumerate(rects):
        if rect is None:
            continue
        for key, p in placed.items():
            if _overlap(rect, (p.left, p.top, p.w, p.h)):
                warnings.append(f"{view['id']}: the label of {name(r)} covers {key}")
        for other, orect in rects[i + 1 :]:
            if orect is not None and _overlap(rect, orect):
                warnings.append(f"{view['id']}: the labels of {name(r)} and {name(other)} overlap")
        for other in routes:
            if other is r:
                continue
            for (x1, y1), (x2, y2) in zip(other.points, other.points[1:]):
                seg = (min(x1, x2) - 1, min(y1, y2) - 1, abs(x2 - x1) + 2, abs(y2 - y1) + 2)
                if _overlap(rect, seg):
                    warnings.append(f"{view['id']}: the label of {name(r)} sits on the line {name(other)}")
                    break


def label_rect(r: Route) -> tuple[float, float, float, float] | None:
    label, tech, w, h = relationship_label(r.rel)
    if not label and not tech and "n" not in r.rel:
        return None
    badge_w = 26 if r.rel.get("n") is not None else 0
    cx, cy = r.label_at
    total = w + badge_w
    return (cx - total / 2 - 5, cy - h / 2 - 3, total + 10, max(h, 22) + 6)


def _crossings(a: Route, b: Route) -> int:
    count = 0
    for (ax1, ay1), (ax2, ay2) in zip(a.points, a.points[1:]):
        for (bx1, by1), (bx2, by2) in zip(b.points, b.points[1:]):
            a_h, b_h = abs(ay1 - ay2) < 0.5, abs(by1 - by2) < 0.5
            if a_h == b_h:
                continue
            (hx1, hx2, hy), (vx, vy1, vy2) = ((min(ax1, ax2), max(ax1, ax2), ay1), (bx1, min(by1, by2), max(by1, by2))) if a_h else ((min(bx1, bx2), max(bx1, bx2), by1), (ax1, min(ay1, ay2), max(ay1, ay2)))
            if hx1 + 1 < vx < hx2 - 1 and vy1 + 1 < hy < vy2 - 1:
                count += 1
    return count


def _overlap(r1, r2) -> bool:
    return r1[0] < r2[0] + r2[2] and r2[0] < r1[0] + r1[2] and r1[1] < r2[1] + r2[3] and r2[1] < r1[1] + r1[3]


def _segment_hits(x1, y1, x2, y2, p: Placed) -> bool:
    lo_x, hi_x, lo_y, hi_y = min(x1, x2), max(x1, x2), min(y1, y2), max(y1, y2)
    return lo_x < p.right - 2 and hi_x > p.left + 2 and lo_y < p.bottom - 2 and hi_y > p.top + 2


# ------------------------------------------------------------------------------------------------- index view ---


def index_layout(model: dict) -> dict[str, tuple[float, float, float, float]]:
    """Each view's card on the map of views: one column per level, in model order."""
    columns: dict[int, list[dict]] = {}
    for view in model["views"]:
        columns.setdefault(int(view.get("column", 0)), []).append(view)
    boxes = {}
    count = max(columns) + 1
    col_w = (W - 96 - (count - 1) * 36) / count
    for c, views in columns.items():
        x = 48 + c * (col_w + 36)
        n = len(views)
        card_h = min(150.0, (640 - (n - 1) * 16) / n)
        y = 190.0
        for v in views:
            boxes[v["id"]] = (x, y, col_w, card_h)
            y += card_h + 16
    return boxes


def _view(model, vid):
    return next(v for v in model["views"] if v["id"] == vid)


# ------------------------------------------------------------------------------------------------------- pages ---


def rel_link(target: str, page_dir: Path, root: Path) -> str:
    return Path(os.path.relpath(root / target, page_dir)).as_posix()


def md_escape(text: str) -> str:
    return str(text).replace("|", "\\|").replace("\n", " ").replace("<", "&lt;").replace(">", "&gt;")


def render_page(model: dict, view: dict, root: Path, page_dir: Path, svg_dir: Path) -> str:
    elements = model["elements"]
    placed = place(view, elements)
    by_id = {v["id"]: v for v in model["views"]}
    out = []
    meta = view.get("page")
    if meta and model.get("pages", {}).get("frontmatter", False):
        front = {
            "id": meta["id"],
            "title": meta.get("title", view["title"]),
            "type": meta.get("type", "concept"),
            "summary": meta["summary"],
            "keywords": meta["keywords"],
        }
        if meta.get("related"):
            front["related"] = meta["related"]
        if meta.get("sourceRefs"):
            front["sourceRefs"] = meta["sourceRefs"]
        out.append("---\n" + yaml.safe_dump(front, sort_keys=False, allow_unicode=True, width=10000).strip() + "\n---\n")
    title = (meta or {}).get("title", view["title"])
    out.append(f"# {title}\n")
    out.append(f"<!-- Generated by tools/c4/render.py from {Path(os.path.relpath(model['_path'], root)).as_posix()}. Edit the model and render again. -->\n")
    out.append(breadcrumb(model, view, placed) + "\n")
    out.append(view.get("description", "") + "\n")
    if view.get("intro"):
        out.append(view["intro"].strip() + "\n")
    img = Path(os.path.relpath(svg_dir / f"{view['id']}.svg", page_dir)).as_posix()
    light = Path(os.path.relpath(svg_dir / f"{view['id']}{THEMES['light'].suffix}.svg", page_dir)).as_posix()
    out.append(f"[![{md_escape(view['title'])}]({img})]({img})\n")
    out.append(f"[Light version]({light})\n")
    if view.get("relationships") and any("n" in r for r in view["relationships"]):
        out.append("## The steps\n")
        out.append("| Step | From | To | What happens |\n| --- | --- | --- | --- |")
        for rel in sorted((r for r in view["relationships"] if "n" in r), key=lambda r: float(r["n"])):
            what = rel.get("explain") or rel.get("label", "")
            tech = f" ({rel['tech']})" if rel.get("tech") else ""
            out.append(f"| {rel['n']} | {md_escape(elements[rel['from']].name)} | {md_escape(elements[rel['to']].name)} | {md_escape(what)}{md_escape(tech)} |")
        out.append("")
    out.append("## What the view shows\n")
    out.append("| Element | Kind | Technology | What it is | Reference |\n| --- | --- | --- | --- | --- |")
    for key, p in placed.items():
        el = p.el
        name = el.name
        if p.zoom:
            name = f"[{el.name}]({by_id[p.zoom]['page_file']})"
        kind = el.type or TYPE_NAMES[el.kind]
        if "ai" in el.tags:
            kind += " (AI)"
        ref = f"[{Path(el.ref).parent.name}/{Path(el.ref).stem}]({rel_link(el.ref, page_dir, root)})" if el.ref else ""
        out.append(f"| {md_escape(name)} | {kind} | {md_escape(el.tech)} | {md_escape(el.detail or el.description)} | {ref} |")
    out.append("")
    if view.get("relationships") and not any("n" in r for r in view["relationships"]):
        out.append("## How they connect\n")
        out.append("| From | To | What | Technology |\n| --- | --- | --- | --- |")
        for rel in view["relationships"]:
            what = rel.get("explain") or rel.get("label", "")
            out.append(f"| {md_escape(elements[rel['from']].name)} | {md_escape(elements[rel['to']].name)} | {md_escape(what)} | {md_escape(rel.get('tech', ''))} |")
        out.append("")
    if view.get("notes"):
        out.append("## Notes\n")
        out.append(view["notes"].strip() + "\n")
    if (meta or {}).get("seeAlso"):
        out.append("## See also\n")
        for item in meta["seeAlso"]:
            out.append(f"- [{item['title']}]({rel_link(item['path'], page_dir, root)})")
        out.append("")
    out.append("---\n")
    out.append(neighbours(model, view) + "\n")
    return "\n".join(out).rstrip() + "\n"


def short(view: dict) -> str:
    """A view's name in a line of links: its `short`, or its title."""
    return view.get("short", view["title"])


def breadcrumb(model: dict, view: dict, placed: dict[str, Placed]) -> str:
    """The one line of navigation on top of a page: the start page, the views this one zooms out to, this view, and
    the views its elements zoom into (the ones marked "view inside" on the diagram)."""
    by_id = {v["id"]: v for v in model["views"]}
    chain = []
    up = view.get("up")
    while up:
        chain.insert(0, by_id[up])
        up = by_id[up].get("up")
    parts = [f"[{model.get('indexShort', 'All views')}]({model['index_file']})"]
    parts += [f"[{short(v)}]({v['page_file']})" for v in chain]
    parts.append(f"**{short(view)}**")
    line = " \u203a ".join(parts)
    order = [v["id"] for v in model["views"]]
    inside = sorted({p.zoom for p in placed.values() if p.zoom}, key=order.index)
    if inside:
        line += " \u00b7 Zoom in: " + ", ".join(f"[{short(by_id[v])}]({by_id[v]['page_file']})" for v in inside)
    return line


def neighbours(model: dict, view: dict) -> str:
    """The line at the foot of a page: the views before and after it in the start page's order."""
    views = model["views"]
    i = views.index(view)
    parts = []
    if i > 0:
        parts.append(f"Previous: [{short(views[i - 1])}]({views[i - 1]['page_file']})")
    parts.append(f"[{model.get('indexShort', 'All views')}]({model['index_file']})")
    if i + 1 < len(views):
        parts.append(f"Next: [{short(views[i + 1])}]({views[i + 1]['page_file']})")
    return " \u00b7 ".join(parts)


def render_index_page(model: dict, root: Path, page_dir: Path, svg_dir: Path) -> str:
    """The start page: what the diagrams are, every view in reading order, and the files they come as."""
    meta = model.get("indexPage", {})
    out = []
    if meta.get("id") and model.get("pages", {}).get("frontmatter", False):
        front = {k: meta[k] for k in ("id", "title", "type", "summary", "keywords", "related", "sourceRefs") if k in meta}
        out.append("---\n" + yaml.safe_dump(front, sort_keys=False, allow_unicode=True, width=10000).strip() + "\n---\n")
    out.append(f"# {meta.get('title', model.get('indexTitle', 'C4 model'))}\n")
    out.append(f"<!-- Generated by tools/c4/render.py from {Path(os.path.relpath(model['_path'], root)).as_posix()}. Edit the model and render again. -->\n")
    if meta.get("intro"):
        out.append(meta["intro"].strip() + "\n")
    out.append("## The views\n")
    out.append("| Level | View | What it answers |\n| --- | --- | --- |")
    for view in model["views"]:
        level = " ".join(w if w in ("C4", "AI") else w.capitalize() for w in view["tag"].split(" "))
        out.append(f"| {level} | [{view['title']}]({view['page_file']}) | {md_escape(view.get('teaser', ''))} |")
    out.append("")
    out.append("## The files\n")
    rel = lambda path: Path(os.path.relpath(path, page_dir)).as_posix()
    light_suffix = THEMES["light"].suffix
    deck = model.get("deck", {}).get("file")
    if deck:
        dark = Path(model["_dir"]) / deck
        light = dark.with_name(dark.stem + light_suffix + dark.suffix)
        out.append(f"- **The deck:** [{dark.name}]({rel(dark)}), and in light [{light.name}]({rel(light)}). One slide a view, in the order above; a click on an element marked *view inside* opens the slide of its inside, and every slide links back to the map of views.")
    out.append(f"- **The diagrams:** one SVG a view in [svg]({rel(svg_dir)}), named after its page (`<view>.svg`), and in light `<view>{light_suffix}.svg`. Each page shows the dark one and links the light one.")
    out.append(f"- **The model:** [{Path(model['_path']).name}]({rel(model['_path'])}), the one source: every element once, and every view that places them. Change it and render again; never edit an SVG, a page or a slide by hand.")
    if HERE.is_relative_to(root):
        out.append(f"- **The renderer:** [tools/c4]({rel(HERE)}), with how to render the model, the notation it follows, and the icons and their terms.")
    else:
        out.append("- **The renderer:** `tools/c4/render.py` in the OSDU Delivery repository, which draws every file here from the model.")
    out.append("")
    if meta.get("body"):
        out.append(meta["body"].strip() + "\n")
    if meta.get("seeAlso"):
        out.append("## See also\n")
        for item in meta["seeAlso"]:
            out.append(f"- [{item['title']}]({rel_link(item['path'], page_dir, root)})")
        out.append("")
    return "\n".join(out).rstrip() + "\n"


# -------------------------------------------------------------------------------------------------------- deck ---


def render_deck(model: dict, icons: Icons, out_path: Path):
    from lxml import etree
    from pptx import Presentation
    from pptx.dml.color import RGBColor
    from pptx.enum.shapes import MSO_CONNECTOR, MSO_SHAPE
    from pptx.enum.text import MSO_ANCHOR, MSO_AUTO_SIZE, PP_ALIGN
    from pptx.opc.constants import RELATIONSHIP_TYPE as RT
    from pptx.opc.package import Part
    from pptx.opc.packuri import PackURI
    from pptx.oxml.ns import qn
    from pptx.util import Emu, Pt

    U = 12192000 / W  # EMU per canvas unit

    def e(v: float) -> Emu:
        return Emu(int(round(v * U)))

    def pt(size: float) -> Pt:
        return Pt(size * U / 12700)

    def rgb(colour: str) -> RGBColor:
        return RGBColor.from_string(colour.lstrip("#"))

    prs = Presentation()
    prs.slide_width, prs.slide_height = Emu(12192000), Emu(6858000)
    blank = prs.slide_layouts[6]
    deck = model.get("deck", {})
    title_slide = prs.slides.add_slide(blank)
    index_slide = prs.slides.add_slide(blank)
    slides = {v["id"]: prs.slides.add_slide(blank) for v in model["views"]}
    svg_parts: dict[tuple[str, str], Part] = {}
    counter = [0]

    def no_effects(shape):
        sppr = shape._element.spPr
        if sppr.find(qn("a:effectLst")) is None:
            sppr.append(etree.SubElement(sppr, qn("a:effectLst")))
        style = shape._element.find(qn("p:style"))
        if style is not None:
            shape._element.remove(style)

    def box(slide, kind, x, y, w, h, fill, stroke=None, radius=0.0, width=1.2, dash=None):
        shape = slide.shapes.add_shape(kind, e(x), e(y), e(w), e(h))
        if fill:
            shape.fill.solid()
            shape.fill.fore_color.rgb = rgb(fill)
        else:
            shape.fill.background()
        if stroke:
            shape.line.color.rgb = rgb(stroke)
            shape.line.width = e(width)
            if dash:
                ln = shape.line._get_or_add_ln()
                prst = etree.SubElement(ln, qn("a:prstDash"))
                prst.set("val", dash)
        else:
            shape.line.fill.background()
        if radius and kind == MSO_SHAPE.ROUNDED_RECTANGLE:
            shape.adjustments[0] = min(0.5, radius / min(w, h))
        no_effects(shape)
        shape.text_frame.text = ""
        return shape

    def text(slide, x, y, w, h, runs, align="center", anchor="top", wrap_text=True, fill=None):
        tb = slide.shapes.add_textbox(e(x), e(y), e(w), e(h))
        tf = tb.text_frame
        tf.word_wrap = wrap_text
        tf.auto_size = MSO_AUTO_SIZE.NONE
        tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
        tf.vertical_anchor = {"top": MSO_ANCHOR.TOP, "middle": MSO_ANCHOR.MIDDLE, "bottom": MSO_ANCHOR.BOTTOM}[anchor]
        if fill:
            tb.fill.solid()
            tb.fill.fore_color.rgb = rgb(fill)
        first = True
        for para in runs:
            p = tf.paragraphs[0] if first else tf.add_paragraph()
            first = False
            p.alignment = {"center": PP_ALIGN.CENTER, "left": PP_ALIGN.LEFT, "right": PP_ALIGN.RIGHT}[align]
            line_h = para.get("line")
            if line_h:
                p.line_spacing = pt(line_h)
            if para.get("before"):
                p.space_before = pt(para["before"])
            r = p.add_run()
            r.text = para["text"]
            f = r.font
            f.name = FONT
            f.size = pt(para["size"])
            f.bold = para.get("bold", False)
            f.italic = para.get("italic", False)
            f.color.rgb = rgb(para["colour"])
        return tb

    def link(shape, target_slide):
        if target_slide is not None:
            shape.click_action.target_slide = target_slide

    def picture(slide, name, colour, x, y, size):
        pic = slide.shapes.add_picture(str(icons.png(name)), e(x), e(y), e(size), e(size))
        key = (name, colour if icons.is_line(name) else "")
        if key not in svg_parts:
            counter[0] += 1
            svg_parts[key] = Part(PackURI(f"/ppt/media/c4-icon-{counter[0]}.svg"), "image/svg+xml", prs.part.package, icons.standalone(name, colour))
        rid = slide.part.relate_to(svg_parts[key], RT.IMAGE)
        blip = pic._element.find(".//" + qn("a:blip"))
        ext_lst = etree.SubElement(blip, qn("a:extLst"))
        ext = etree.SubElement(ext_lst, qn("a:ext"))
        ext.set("uri", "{96DAC541-7B7A-43D3-8B79-37D633B846F1}")
        svg_blip = etree.SubElement(ext, "{http://schemas.microsoft.com/office/drawing/2016/SVG/main}svgBlip", nsmap={"asvg": "http://schemas.microsoft.com/office/drawing/2016/SVG/main"})
        svg_blip.set(qn("r:embed"), rid)
        return pic

    def polyline(slide, points, dashed=False, arrow=True, colour=T.line, width=1.6, both=False):
        if len(points) == 2:
            (x1, y1), (x2, y2) = points
            shape = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, e(x1), e(y1), e(x2), e(y2))
        else:
            builder = slide.shapes.build_freeform(e(points[0][0]), e(points[0][1]), scale=1.0)
            builder.add_line_segments([(e(x), e(y)) for x, y in points[1:]], close=False)
            shape = builder.convert_to_shape()
            shape.fill.background()
        shape.line.color.rgb = rgb(colour)
        shape.line.width = e(width)
        ln = shape.line._get_or_add_ln()
        if dashed:
            prst = etree.SubElement(ln, qn("a:prstDash"))
            prst.set("val", "dash")
        etree.SubElement(ln, qn("a:round"))
        if both:
            head = etree.SubElement(ln, qn("a:headEnd"))
            head.set("type", "triangle")
            head.set("w", "med")
            head.set("len", "med")
        if arrow:
            tail = etree.SubElement(ln, qn("a:tailEnd"))
            tail.set("type", "triangle")
            tail.set("w", "med")
            tail.set("len", "med")
        style = shape._element.find(qn("p:style"))
        if style is not None:
            shape._element.remove(style)
        return shape

    def chrome(slide, tag, title, desc, up_id=None, footer=""):
        tw = text_width(tag, 11.5, "bold") + 22
        pill = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, 48, 34, tw, 22, T.tag_fill, radius=11)
        text(slide, 48, 34, tw, 22, [{"text": tag, "size": 11.5, "bold": True, "colour": T.accent}], anchor="middle")
        text(slide, 46, 60, 1300, 40, [{"text": title, "size": 28, "bold": True, "colour": T.ink}], align="left", wrap_text=False)
        if desc:
            text(slide, 48, 104, 1100, 44, [{"text": " ".join(desc), "size": 15, "colour": T.lead, "line": 20}], align="left")
        nav = []
        if up_id:
            nav.append((f"Zoom out: {by_id[up_id]['title']}", slides[up_id]))
        if slide is not index_slide:
            nav.append(("All views", index_slide))
        nx = W - 48
        for label, target in reversed(nav):
            tw = text_width(label, 12.5, "semibold") + 24
            nx -= tw
            btn = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, nx, 34, tw, 26, T.card_fill, T.card_stroke, radius=13, width=1)
            btn.text_frame.text = ""
            tb = text(slide, nx, 34, tw, 26, [{"text": label, "size": 12.5, "bold": True, "colour": T.accent}], anchor="middle")
            link(btn, target)
            link(tb, target)
            nx -= 8
        if footer and up_id is None and slide is index_slide:
            text(slide, W - 760, 866, 712, 20, [{"text": footer, "size": 11.5, "colour": T.faint}], align="right")

    by_id = {v["id"]: v for v in model["views"]}
    for s in [index_slide, *slides.values()]:
        s.background.fill.solid()
        s.background.fill.fore_color.rgb = rgb(T.paper)

    # The title slide.
    slide = title_slide
    box(slide, MSO_SHAPE.RECTANGLE, 0, 0, W, H, "#0B2540")
    box(slide, MSO_SHAPE.RECTANGLE, 0, 0, 14, H, "#1168BD")
    text(slide, 110, 280, 1300, 30, [{"text": deck.get("kicker", "ARCHITECTURE"), "size": 15, "bold": True, "colour": "#9CC8F2"}], align="left")
    text(slide, 108, 318, 1380, 90, [{"text": deck.get("title", model["product"]), "size": 54, "bold": True, "colour": "#FFFFFF"}], align="left")
    text(slide, 110, 410, 860, 120, [{"text": deck.get("subtitle", ""), "size": 22, "colour": "#D6E4F5", "line": 30}], align="left")
    if deck.get("note"):
        text(slide, 110, 760, 1380, 60, [{"text": deck["note"], "size": 13, "colour": "#9FB3C8", "line": 19}], align="left")
    start = text(slide, 110, 560, 360, 40, [{"text": "Open the map of views  >", "size": 16, "bold": True, "colour": "#FFFFFF"}], align="left", anchor="middle")
    link(start, index_slide)
    levels = [("System context", "#1C4E80", 0), ("Containers", "#1F5F9E", 1), ("Components", "#2F7CC7", 2)]
    targets = [v["id"] for v in model["views"]]
    first_of = {int(v.get("column", 0)): v["id"] for v in reversed(model["views"])}
    for label, fill, i in levels:
        ox, oy, size = 1040 + i * 70, 200 + i * 70, 440 - i * 140
        frame = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, ox, oy, size, size, fill, "#9CC8F2", radius=18, width=1.2)
        tb = text(slide, ox + 18, oy + 12, size - 36, 24, [{"text": label.upper(), "size": 12, "bold": True, "colour": "#FFFFFF"}], align="left")
        if i in first_of:
            link(frame, slides[first_of[i]])
            link(tb, slides[first_of[i]])
    text(slide, 1040, 655, 440, 40, [{"text": "Each level zooms into the one around it.", "size": 13, "colour": "#9FB3C8"}], align="left")

    # The map of views.
    slide = index_slide
    chrome(slide, "C4 MODEL", model.get("indexTitle", "The views"), wrap(model.get("indexDescription", ""), 1060, 15)[:2], None, model.get("footer", ""))
    boxes = index_layout(model)
    for c, heading in enumerate(model.get("columns", [])):
        xs = [b for v, b in boxes.items() if int(by_id[v].get("column", 0)) == c]
        if xs:
            text(slide, xs[0][0], 164, xs[0][2], 18, [{"text": heading.upper(), "size": 11.5, "bold": True, "colour": T.muted}], align="left")
    for view in model["views"]:
        up = view.get("up")
        if up and up in boxes:
            (ax, ay, aw, ah), (bx, by, bw, bh) = boxes[up], boxes[view["id"]]
            if 0 < bx - (ax + aw) < 60:
                x1, y1, x2, y2 = ax + aw, ay + ah / 2, bx, by + bh / 2
                mid = x1 + 18
                pts = [(x1, y1), (mid, y1), (mid, y2), (x2, y2)] if abs(y1 - y2) > 0.5 else [(x1, y1), (x2, y2)]
                polyline(slide, pts, colour=T.card_stroke, width=1.5)
    for view in model["views"]:
        x, y, w, h = boxes[view["id"]]
        card = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y, w, h, T.card_fill, T.card_stroke, radius=12)
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y, 6, h, T.accent, radius=3)
        runs = [
            {"text": view["tag"], "size": 10.5, "bold": True, "colour": T.accent},
            {"text": view["title"], "size": 15, "bold": True, "colour": T.ink, "before": 4},
        ]
        if view.get("teaser"):
            runs.append({"text": view["teaser"], "size": 12, "colour": T.muted, "before": 4, "line": 16})
        tb = text(slide, x + 20, y + 12, w - 34, h - 18, runs, align="left")
        link(card, slides[view["id"]])
        link(tb, slides[view["id"]])
    slide.notes_slide.notes_text_frame.text = "Every card opens its view. Every view links back to this map and to the level above it."

    # One slide per view.
    for view in model["views"]:
        slide = slides[view["id"]]
        placed = place(view, model["elements"])
        fr = frames(view, placed)
        tag, title, desc = header(view)
        chrome(slide, tag, title, desc[:2], view.get("up"), model.get("footer", ""))
        for f in sorted(fr, key=lambda f: -(f.w * f.h)):
            if f.style == "node":
                box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, f.x, f.y, f.w, f.h, T.node_fill, T.node_stroke, radius=14, width=1.3)
                tx = f.x + 16
                if f.icon:
                    picture(slide, f.icon, T.ink2, f.x + 14, f.y + 13, 26)
                    tx = f.x + 48
                runs = [{"text": f.label, "size": 14.5, "bold": True, "colour": T.ink}]
                if f.type:
                    runs.append({"text": f"[{f.type}]", "size": 11.5, "colour": T.muted})
                text(slide, tx, f.y + 10, f.w - (tx - f.x) - 10, 40, runs, align="left")
            else:
                boundary = f.style == "boundary"
                box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, f.x, f.y, f.w, f.h, T.boundary_fill if boundary else None, T.boundary_stroke if boundary else T.group_stroke, radius=16, width=1.4, dash="dash" if boundary else "sysDot")
                if boundary:
                    runs = [{"text": f.label, "size": 14.5, "bold": True, "colour": T.ink}]
                    if f.type:
                        runs.append({"text": f"[{f.type}]", "size": 11.5, "colour": T.muted})
                    if isinstance(f.corner, list):
                        text(slide, float(f.corner[0]), float(f.corner[1]) - 18, 400, 40, runs, align="left")
                    else:
                        right = f.corner.endswith("right")
                        ty = f.y + 6 if f.corner.startswith("top") else f.y + f.h - 46
                        text(slide, f.x + 16, ty, f.w - 32, 40, runs, align="right" if right else "left", anchor="top" if f.corner.startswith("top") else "bottom")
                else:
                    label = f.label + (f"  [{f.type}]" if f.type else "")
                    text(slide, f.x + 16, f.y + 8, f.w - 32, 20, [{"text": label, "size": 13.5, "bold": True, "colour": T.ink2}], align="left")
        routes = []
        for rel in view.get("relationships") or []:
            pts = route(rel, placed[rel["from"]], placed[rel["to"]])
            routes.append(Route(pts, rel, label_anchor(pts, rel)))
            polyline(slide, pts, dashed=bool(rel.get("dashed")), both=bool(rel.get("both")))
        for key, p in placed.items():
            el = p.el
            fill, stroke, colour, sub, icon_colour = T.kinds[el.kind]
            lay = element_layout(p)
            x, y, w, h = lay.body
            target = slides.get(p.zoom) if p.zoom else None
            if lay.head:
                cx, cy, r = lay.head
                head = box(slide, MSO_SHAPE.OVAL, cx - r, cy - r, 2 * r, 2 * r, fill, stroke)
                link(head, target)
                body = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y, w, h, fill, stroke, radius=min(34, h / 3))
            elif el.shape == "cylinder":
                body = box(slide, MSO_SHAPE.CAN, x, y, w, h, fill, stroke)
                body.adjustments[0] = min(0.5, (2 * lay.cap) / min(w, h) * (min(w, h) / w) if w < h else (2 * lay.cap) / h)
            elif el.shape == "folder":
                tab = min(110.0, w * 0.38)
                body = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y + lay.cap, w, h - lay.cap, fill, stroke, radius=10)
                tab_shape = box(slide, MSO_SHAPE.ROUND_2_SAME_RECTANGLE, x, y, tab, lay.cap + 6, fill, stroke)
                tab_shape.adjustments[0] = 0.3
                link(tab_shape, target)
            else:
                body = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y, w, h, fill, stroke, radius=6 if el.shape in ("browser", "terminal") else 12)
                if el.shape in ("browser", "terminal"):
                    bar = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, e(x), e(y + lay.cap), e(x + w), e(y + lay.cap))
                    bar.line.color.rgb = rgb(sub)
                    bar.line.width = e(1)
                    if el.shape == "browser":
                        for i in range(3):
                            dot = box(slide, MSO_SHAPE.OVAL, x + 10 + i * 11, y + lay.cap / 2 - 3, 6, 6, sub)
                            link(dot, target)
                    else:
                        text(slide, x + 10, y + 2, 40, lay.cap - 2, [{"text": ">_", "size": 11, "bold": True, "colour": sub}], align="left", anchor="middle")
            link(body, target)
            if lay.chip:
                cx, cy = lay.chip
                chip = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, cx, cy, p.chip, p.chip, T.chip if el.kind != "infra" else T.chip_infra, radius=p.chip / 4.2)
                link(chip, target)
                pic = picture(slide, el.icon, icon_colour or T.on_chip, cx + (p.chip - p.icon) / 2, cy + (p.chip - p.icon) / 2, p.icon)
                link(pic, target)
            first = lay.lines[0][1] - lay.lines[0][2] * 0.95
            runs = []
            prev_role = None
            for line, baseline, size, weight, role in lay.lines:
                if role == prev_role:
                    runs[-1]["text"] += " " + line
                    continue
                run = {"text": line, "size": size, "bold": weight == "bold", "colour": colour if role == "name" else sub}
                run["line"] = size * 1.25
                if role == "desc":
                    run["before"] = 6
                runs.append(run)
                prev_role = role
            if lay.anchor == "start":
                tb = text(slide, lay.tx, first - 3, lay.text_w + 6, (y + h) - first, runs, align="left")
            else:
                tb = text(slide, x + 10, first - 3, w - 20, (y + h) - first, runs)
            link(tb, target)
            for bx, by, bw, bh, label, bfill, bcolour in badge_boxes(p):
                pill = box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, bx, by, bw, bh, bfill, T.accent if bcolour == T.accent else None, radius=9, width=1)
                tbb = text(slide, bx, by, bw, bh, [{"text": label, "size": 10.5, "bold": True, "colour": bcolour}], anchor="middle", wrap_text=False)
                link(pill, target)
                link(tbb, target)
        for r in routes:
            label, tech, w, h = relationship_label(r.rel)
            if not label and not tech and "n" not in r.rel:
                continue
            cx, cy = r.label_at
            step = r.rel.get("n")
            badge_w = 26 if step is not None else 0
            total = w + badge_w
            x0 = cx - total / 2
            box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x0 - 5, cy - h / 2 - 3, total + 10, max(h, 22) + 6, T.label_fill, radius=5)
            if step is not None:
                by_ = cy if h <= 22 else cy - h / 2 + 11
                circle = box(slide, MSO_SHAPE.OVAL, x0, by_ - 11, 22, 22, T.ink)
                text(slide, x0, by_ - 11, 22, 22, [{"text": str(step), "size": 12, "bold": True, "colour": T.paper}], anchor="middle", wrap_text=False)
            runs = []
            if label:
                runs.append({"text": " ".join(label), "size": REL_SIZE, "colour": T.ink, "line": 15})
            if tech:
                runs.append({"text": " ".join(tech), "size": REL_TECH_SIZE, "italic": True, "colour": T.muted, "line": 14})
            if runs:
                text(slide, x0 + badge_w - 2, cy - h / 2 - 1, w + 6, h + 4, runs)
        line = slide.shapes.add_connector(MSO_CONNECTOR.STRAIGHT, e(48), e(LEGEND_TOP), e(W - 48), e(LEGEND_TOP))
        line.line.color.rgb = rgb(T.rule)
        line.line.width = e(1)
        for key, label, lx, ly in legend_layout(legend_items(view, placed, fr)):
            _deck_swatch(slide, box, polyline, text, picture, key, lx, ly, MSO_SHAPE)
            text(slide, lx + 30, ly - 9, text_width(label, 12) + 12, 18, [{"text": label, "size": 12, "colour": T.ink2}], align="left", wrap_text=False)
        notes = [view.get("description", "")]
        if view.get("relationships") and any("n" in rel for rel in view["relationships"]):
            for rel in sorted((r for r in view["relationships"] if "n" in r), key=lambda r: float(r["n"])):
                notes.append(f"{rel['n']}. {model['elements'][rel['from']].name} to {model['elements'][rel['to']].name}: {rel.get('explain') or rel.get('label', '')}")
        notes.append("")
        for key, p in placed.items():
            ref = f" ({p.el.ref})" if p.el.ref else ""
            notes.append(f"{p.el.name} {type_line(p.el)}: {p.el.detail or p.el.description}{ref}")
        if view.get("notes"):
            notes.append("")
            notes.append(re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", view["notes"].strip()))
        slide.notes_slide.notes_text_frame.text = "\n".join(notes)
    prs.core_properties.title = deck.get("title", model["product"])
    prs.core_properties.subject = deck.get("subtitle", "")
    prs.core_properties.author = deck.get("author", model["product"])
    prs.save(str(out_path))


def _deck_swatch(slide, box, polyline, text, picture, key, x, y, MSO_SHAPE):
    if key in T.kinds:
        fill, stroke = T.kinds[key][0], T.kinds[key][1]
        if key == "person":
            box(slide, MSO_SHAPE.OVAL, x + 7, y - 9, 8, 8, fill)
            box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x + 3, y - 1, 16, 9, fill, radius=4)
        else:
            box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y - 8, 22, 16, fill, stroke, radius=4)
    elif key == "shape:cylinder":
        can = box(slide, MSO_SHAPE.CAN, x + 2, y - 8, 18, 16, None, T.ink2, width=1.3)
        can.adjustments[0] = 0.35
    elif key == "shape:folder":
        box(slide, MSO_SHAPE.FOLDED_CORNER, x + 1, y - 8, 20, 16, None, T.ink2, width=1.3)
    elif key in ("shape:browser", "shape:terminal"):
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x + 1, y - 8, 20, 16, None, T.ink2, radius=2.5, width=1.3)
        if key == "shape:browser":
            polyline(slide, [(x + 1, y - 3), (x + 21, y - 3)], arrow=False, colour=T.ink2, width=1.1)
        else:
            text(slide, x + 1, y - 8, 20, 16, [{"text": ">_", "size": 8.5, "bold": True, "colour": T.ink2}], anchor="middle", wrap_text=False)
    elif key == "icon":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x + 2, y - 9, 18, 18, T.chip, T.card_stroke, radius=4.5, width=1)
        picture(slide, "lucide:box", T.on_chip, x + 5, y - 6, 12)
    elif key == "ai":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y - 8, 22, 16, AI_FILL, radius=8)
        text(slide, x, y - 8, 22, 16, [{"text": "AI", "size": 9.5, "bold": True, "colour": AI_TEXT}], anchor="middle", wrap_text=False)
    elif key == "zoom":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x - 2, y - 8, 26, 16, T.paper, T.accent, radius=8, width=1)
        polyline(slide, [(x + 8, y - 4), (x + 12, y), (x + 8, y + 4)], arrow=False, colour=T.accent, width=1.6)
    elif key == "node":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y - 8, 22, 16, T.node_fill, T.node_stroke, radius=4, width=1)
    elif key == "boundary":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y - 8, 22, 16, T.boundary_fill, T.boundary_stroke, radius=4, width=1, dash="dash")
    elif key == "group":
        box(slide, MSO_SHAPE.ROUNDED_RECTANGLE, x, y - 8, 22, 16, None, T.group_stroke, radius=4, width=1, dash="sysDot")
    elif key == "line":
        polyline(slide, [(x, y), (x + 20, y)])
    elif key == "dashed":
        polyline(slide, [(x, y), (x + 20, y)], dashed=True)
    elif key == "step":
        box(slide, MSO_SHAPE.OVAL, x + 2, y - 9, 18, 18, T.ink)
        text(slide, x + 2, y - 9, 18, 18, [{"text": "1", "size": 10.5, "bold": True, "colour": T.paper}], anchor="middle", wrap_text=False)


# --------------------------------------------------------------------------------------------------------- main ---


def find_root(start: Path) -> Path:
    for folder in [start, *start.parents]:
        if (folder / ".git").exists():
            return folder
    return start


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("model", type=Path)
    parser.add_argument("--check", action="store_true", help="compare with the committed files instead of writing them")
    parser.add_argument("--no-pptx", action="store_true", help="skip the deck")
    parser.add_argument("--strict", action="store_true", help="fail on any layout warning")
    args = parser.parse_args(argv)

    model_path = args.model.resolve()
    model = load_model(model_path)
    model["_path"] = str(model_path)
    model["_dir"] = str(model_path.parent)
    root = find_root(model_path.parent)
    base = model_path.parent
    svg_dir = base / model.get("svgDir", "svg")
    page_dir = base / model.get("pages", {}).get("dir", ".")
    page_prefix = model.get("pages", {}).get("prefix", "")
    for view in model["views"]:
        view["page_file"] = f"{page_prefix}{view['id']}.md"
    model["index_file"] = model.get("pages", {}).get("index", f"{page_prefix}index.md")

    icons = Icons()
    outputs: dict[Path, str] = {}
    warnings: list[str] = []
    for theme in THEMES:
        use_theme(theme)
        for view in model["views"]:
            svg, warn = render_svg(model, view, icons)
            outputs[svg_dir / themed(f"{view['id']}.svg")] = svg
            if theme == "dark":
                warnings += warn
    use_theme("dark")
    if model.get("pages", {}).get("write", True):
        for view in model["views"]:
            outputs[page_dir / view["page_file"]] = render_page(model, view, root, page_dir, svg_dir)
        outputs[page_dir / model["index_file"]] = render_index_page(model, root, page_dir, svg_dir)

    for warning in warnings:
        print(f"warning: {warning}", file=sys.stderr)
    if args.strict and warnings:
        return 1
    for text in outputs.values():
        if "\u2014" in text:
            raise SystemExit("render.py: the model holds an em dash, which the repository forbids")

    if args.check:
        stale = [p for p, text in outputs.items() if not p.exists() or p.read_text(encoding="utf-8") != text]
        for path in stale:
            print(f"stale: {Path(os.path.relpath(path, root)).as_posix()}", file=sys.stderr)
        if stale:
            print("render.py: the diagrams differ from the model; run python tools/c4/render.py " + Path(os.path.relpath(model_path, root)).as_posix(), file=sys.stderr)
        return 1 if stale else 0

    for path, text in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
    deck_file = model.get("deck", {}).get("file")
    if deck_file and not args.no_pptx:
        for theme in THEMES:
            use_theme(theme)
            render_deck(model, icons, base / themed(deck_file))
        use_theme("dark")
    print(f"rendered {len(model['views'])} views of {model_path.name}: {len(outputs)} files" + ("" if args.no_pptx or not deck_file else " and the dark and light decks"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
