#!/usr/bin/env python3
"""
compose-sprite-sheet.py — pack per-frame PNGs into a single sprite-sheet PNG + JSON manifest.

Usage:
    python compose-sprite-sheet.py <entity-dir> [output-dir]

Reads <entity-dir>/<entity>.json (the per-frame manifest the Artist agent writes) and
the per-frame PNGs it references, then produces:
  - <output-dir>/<entity>.png       — packed sprite-sheet atlas (rows = animations)
  - <output-dir>/<entity>.atlas.json — Phaser-compatible frame map

Why this exists (art-pipeline-sprite-sheet-composition todo, 2026-05-12):
The Artist agent generates per-frame PNGs because the Azure OpenAI image-gen API
returns one image per call and adding ImageMagick montage in-line is failure-prone
for the agent. For PRODUCTION builds, a packed sprite sheet (single PNG atlas + JSON
frame map) is more efficient: fewer HTTP requests, better GPU texture-atlas reuse,
smaller total payload. This script runs OFFLINE (CI / pre-deploy step) so the
agent's per-frame output stays the source-of-truth and the packed atlas is a
generated build artifact.

Generality rule: this script does NOT hardcode entity names or animation lists.
It reads everything from the per-frame manifests the Artist commits.

Dependencies: Python 3.9+, Pillow (pip install pillow).
NOTE: Pillow is used for IMAGE COMPOSITION ONLY here (resizing + atlas packing).
This is explicitly allowed by AC #6 of any image-gen task — the no-fabrication
rule applies to ART CONTENT GENERATION, not to geometric atlas packing of
already-generated frames.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

try:
    from PIL import Image
except ImportError:
    print("ERROR: Pillow is required. Install with: pip install pillow", file=sys.stderr)
    sys.exit(2)


@dataclass(frozen=True)
class FrameRef:
    animation: str
    frame_index: int
    path: Path
    duration_ms: int


def load_manifest(entity_dir: Path) -> dict[str, Any]:
    """Find the entity's per-frame manifest. Convention: {entity-dir}/{entity-name}.json."""
    candidates = list(entity_dir.glob("*.json"))
    # Prefer a JSON named after the directory (e.g. goblin/goblin.json).
    preferred = entity_dir / f"{entity_dir.name}.json"
    if preferred.exists():
        return json.loads(preferred.read_text(encoding="utf-8"))
    # Fall back to the only JSON file present.
    if len(candidates) == 1:
        return json.loads(candidates[0].read_text(encoding="utf-8"))
    raise FileNotFoundError(
        f"Could not locate a unique manifest in {entity_dir} "
        f"(expected {preferred.name} or a single .json file)"
    )


def collect_frames(entity_dir: Path, manifest: dict[str, Any]) -> list[FrameRef]:
    """Walk the manifest's animations and produce one FrameRef per declared frame."""
    frames: list[FrameRef] = []
    animations = manifest.get("animations", [])
    if not animations:
        raise ValueError(f"Manifest {entity_dir.name}.json has no 'animations' array")

    for anim in animations:
        anim_name = anim.get("name")
        frame_count = anim.get("frames", 0)
        duration = anim.get("frame-duration-ms", anim.get("frameDuration", 100))
        if not anim_name or frame_count <= 0:
            print(f"WARN: skipping malformed animation entry: {anim}", file=sys.stderr)
            continue
        for i in range(frame_count):
            for stem_pattern in (f"{anim_name}-{i}.png", f"{anim_name}_{i}.png"):
                p = entity_dir / stem_pattern
                if p.exists():
                    frames.append(FrameRef(anim_name, i, p, duration))
                    break
            else:
                print(
                    f"WARN: missing frame {anim_name}-{i}.png in {entity_dir}; skipping",
                    file=sys.stderr,
                )
    return frames


def pack_sheet(frames: list[FrameRef], cell_size: tuple[int, int] | None = None) -> tuple[Image.Image, dict[str, Any]]:
    """Pack frames into a row-per-animation atlas. All frames are normalized to cell_size."""
    if not frames:
        raise ValueError("No frames to pack — manifest declared 0 frames or all frames missing")

    # Determine cell size from the first frame if not specified.
    if cell_size is None:
        with Image.open(frames[0].path) as im0:
            cell_size = im0.size
    cell_w, cell_h = cell_size

    # Group by animation, preserving manifest order via dict insertion order.
    grouped: dict[str, list[FrameRef]] = {}
    for f in frames:
        grouped.setdefault(f.animation, []).append(f)

    # Layout: one row per animation, columns = max frame count.
    cols = max(len(row) for row in grouped.values())
    rows = len(grouped)

    sheet = Image.new("RGBA", (cols * cell_w, rows * cell_h), (0, 0, 0, 0))

    atlas: dict[str, Any] = {
        "frame-size": {"width": cell_w, "height": cell_h},
        "animations": [],
    }

    for row_index, (anim_name, anim_frames) in enumerate(grouped.items()):
        anim_atlas: dict[str, Any] = {
            "name": anim_name,
            "frame-duration-ms": anim_frames[0].duration_ms if anim_frames else 100,
            "frames": [],
        }
        for col_index, ref in enumerate(anim_frames):
            with Image.open(ref.path) as im:
                if im.size != (cell_w, cell_h):
                    im = im.resize((cell_w, cell_h), Image.LANCZOS)
                sheet.paste(im, (col_index * cell_w, row_index * cell_h))
            anim_atlas["frames"].append(
                {
                    "index": ref.frame_index,
                    "x": col_index * cell_w,
                    "y": row_index * cell_h,
                    "width": cell_w,
                    "height": cell_h,
                }
            )
        atlas["animations"].append(anim_atlas)

    return sheet, atlas


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print("usage: compose-sprite-sheet.py <entity-dir> [output-dir]", file=sys.stderr)
        return 2

    entity_dir = Path(argv[1]).resolve()
    if not entity_dir.is_dir():
        print(f"ERROR: '{entity_dir}' is not a directory", file=sys.stderr)
        return 2

    output_dir = Path(argv[2]).resolve() if len(argv) > 2 else entity_dir.parent / "sheets"
    output_dir.mkdir(parents=True, exist_ok=True)

    manifest = load_manifest(entity_dir)
    frames = collect_frames(entity_dir, manifest)
    sheet, atlas = pack_sheet(frames)

    sheet_path = output_dir / f"{entity_dir.name}.png"
    atlas_path = output_dir / f"{entity_dir.name}.atlas.json"
    sheet.save(sheet_path, "PNG", optimize=True)
    atlas_path.write_text(json.dumps(atlas, indent=2), encoding="utf-8")

    print(f"OK: packed {len(frames)} frames into {sheet_path} ({sheet.size[0]}x{sheet.size[1]})")
    print(f"OK: atlas manifest at {atlas_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
