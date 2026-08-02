"""Pixel evidence for the reactive-dropdown verification.

Two questions the shell cannot answer on its own:

- ``delta``: how far apart two captures are, in the same terms the
  component sheets use (significant pixels at a max-channel delta above
  ``SIGNIFICANT_DELTA``). The scale sweep reports this rather than failing
  on it, because the reactive control's snapped geometry is a deliberate
  divergence from legacy's fractional rects at 1.25x and 1.5x.

- ``containment``: whether a SCROLLED menu keeps its row ink inside the
  scroll viewport. This is a property, not a comparison, so it can convict
  one capture and clear the other -- which is the point. The legacy control
  paints its row fills onto the POPUP's draw list while the rows themselves
  live in a scrolled child, so a row scrolled out of the viewport can still
  leave ink behind; the retained walker resolves the draw list inside the
  child instead. Either way the verdict comes from measured pixels, and the
  in-viewport comparison is what decides pass or fail.

The row-fill colour is never hardcoded: it is sampled from a filled row of
an UNSCROLLED capture of the same control at the same scale, so a theme or
token change moves the sample instead of breaking the check.
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

import numpy as np
from PIL import Image

# Matches sheets.py: at or below this a max-channel difference is encoder
# rounding or subpixel AA, above it the difference is real.
SIGNIFICANT_DELTA = 8


def load(path: Path) -> np.ndarray:
    return np.array(Image.open(path).convert("RGBA"), dtype=np.int16)


def bbox(mask: np.ndarray) -> str:
    """The tight box around a boolean mask, or ``none`` when it is empty."""
    if not mask.any():
        return "none"
    rows = np.flatnonzero(mask.any(axis=1))
    cols = np.flatnonzero(mask.any(axis=0))
    return (
        f"x{cols[0]}..{cols[-1]} y{rows[0]}..{rows[-1]} "
        f"({cols[-1] - cols[0] + 1}x{rows[-1] - rows[0] + 1})"
    )


def compare(a: np.ndarray, b: np.ndarray) -> tuple[int, int, np.ndarray]:
    """Significant-pixel count, max channel delta, and the significant mask."""
    if a.shape != b.shape:
        raise SystemExit(f"shape mismatch: {a.shape} vs {b.shape}")
    delta = np.abs(a - b)
    per_pixel = delta.max(axis=2)
    mask = per_pixel > SIGNIFICANT_DELTA
    return int(mask.sum()), int(per_pixel.max()), mask


def cmd_delta(args: argparse.Namespace) -> int:
    a, b = load(args.first), load(args.second)
    significant, peak, mask = compare(a, b)
    if peak == 0:
        print("equal")
    else:
        print(f"significant={significant} max={peak} bbox={bbox(mask)}")
    return 0


def sample_fill(sample: np.ndarray, view_top: int, row_height: int) -> tuple:
    """
    The row-fill colour, read off the selected row of an unscrolled capture.

    The row is inset vertically so its rounded corners and their AA cannot
    contribute, and the window background -- read from a corner pixel that
    no control can reach -- is excluded, which leaves the fill as the most
    common colour across the band. Glyph pixels and the panel's own inset
    are a minority of it by a wide margin.
    """
    inset = max(1, row_height // 6)
    band = sample[view_top + inset : view_top + row_height - inset, :, :]
    if band.size == 0:
        raise SystemExit("row-0 sample band is empty; check --view-top")
    background = tuple(int(channel) for channel in sample[2, 2])
    counts = Counter(
        tuple(int(channel) for channel in pixel)
        for pixel in band.reshape(-1, band.shape[2])
    )
    del counts[background]
    if not counts:
        raise SystemExit("no non-background colour in the row-0 sample band")
    return counts.most_common(1)[0][0]


def outside_ink(
    image: np.ndarray, fill: tuple, view_top: int, view_bottom: int
) -> tuple[int, str]:
    """
    Fill-coloured pixels outside the scroll viewport band.

    Isolated single-pixel-tall runs are dropped first. The panel's own 1px
    border composites to EXACTLY the row-fill colour -- both are the same
    white overlay over the same panel fill, so that is arithmetic rather
    than coincidence -- and it traces the top and bottom edges outside the
    viewport on every capture, scrolled or not. An escaped row fill is
    ``rowHeight`` tall, so requiring a vertical neighbour keeps every real
    escape and removes the border. The blind spot is an escape of exactly
    one pixel, which is a row clipped to its own last scanline.
    """
    exact = np.all(image == np.array(fill, dtype=np.int16), axis=2)
    neighbour = np.zeros_like(exact)
    neighbour[1:, :] |= exact[:-1, :]
    neighbour[:-1, :] |= exact[1:, :]
    outside = np.ones(exact.shape, dtype=bool)
    outside[view_top:view_bottom, :] = False
    mask = exact & neighbour & outside
    return int(mask.sum()), bbox(mask)


def cmd_containment(args: argparse.Namespace) -> int:
    reactive = load(args.reactive)
    legacy = load(args.legacy)
    sample = load(args.sample)
    fill = sample_fill(sample, args.view_top, args.row_height)
    print(f"row-fill rgba{fill} viewport y{args.view_top}..{args.view_bottom}")

    reactive_ink, reactive_box = outside_ink(
        reactive, fill, args.view_top, args.view_bottom
    )
    legacy_ink, legacy_box = outside_ink(
        legacy, fill, args.view_top, args.view_bottom
    )
    print(f"outside-viewport reactive={reactive_ink} bbox={reactive_box}")
    print(f"outside-viewport legacy={legacy_ink} bbox={legacy_box}")

    significant, peak, mask = compare(reactive, legacy)
    print(f"whole-frame significant={significant} max={peak} bbox={bbox(mask)}")

    band = np.zeros(mask.shape, dtype=bool)
    band[args.view_top : args.view_bottom, :] = True
    in_viewport = mask & band
    print(
        f"in-viewport significant={int(in_viewport.sum())} "
        f"bbox={bbox(in_viewport)}"
    )

    if reactive_ink or in_viewport.any():
        print("VERDICT FAIL reactive violates containment or diverges in-viewport")
        return 1
    if peak == 0:
        print("VERDICT IDENTICAL both captures contained, no divergence")
        return 0
    if not legacy_ink:
        print("VERDICT UNEXPLAINED divergence with neither capture leaking ink")
        return 2
    print("VERDICT EVIDENCE-DIVERGENCE legacy parent-list artifact only")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    delta = sub.add_parser("delta", help="significant-pixel comparison")
    delta.add_argument("first", type=Path)
    delta.add_argument("second", type=Path)
    delta.set_defaults(func=cmd_delta)

    contain = sub.add_parser("containment", help="scroll-viewport containment")
    contain.add_argument("--reactive", type=Path, required=True)
    contain.add_argument("--legacy", type=Path, required=True)
    contain.add_argument(
        "--sample",
        type=Path,
        required=True,
        help="unscrolled capture of the same control, for the fill colour",
    )
    contain.add_argument("--view-top", type=int, required=True)
    contain.add_argument("--view-bottom", type=int, required=True)
    contain.add_argument("--row-height", type=int, required=True)
    contain.set_defaults(func=cmd_containment)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
