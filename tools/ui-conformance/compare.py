from __future__ import annotations

import argparse
import html
import json
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter


def bbox(mask: np.ndarray) -> list[int] | None:
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return None
    return [int(xs.min()), int(ys.min()), int(xs.max() + 1), int(ys.max() + 1)]


def centroid(mask: np.ndarray) -> list[float] | None:
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return None
    return [round(float(xs.mean()), 2), round(float(ys.mean()), 2)]


def best_profile_shift(reference: np.ndarray, candidate: np.ndarray) -> tuple[int, int]:
    def score_shift(a: np.ndarray, b: np.ndarray) -> int:
        best_shift, best_score = 0, float("inf")
        for shift in range(-8, 9):
            shifted = np.roll(b, shift)
            if shift < 0:
                shifted[shift:] = 0
            elif shift > 0:
                shifted[:shift] = 0
            score = float(np.abs(a - shifted).sum())
            if score < best_score:
                best_shift, best_score = shift, score
        return best_shift

    return (
        score_shift(reference.sum(axis=0), candidate.sum(axis=0)),
        score_shift(reference.sum(axis=1), candidate.sum(axis=1)),
    )


def connected_regions(mask: np.ndarray) -> list[list[int]]:
    # Expand sparse antialias differences so one glyph/control becomes one region.
    expanded = np.asarray(
        Image.fromarray((mask * 255).astype(np.uint8))
        .filter(ImageFilter.MaxFilter(7))
    ) > 0
    height, width = expanded.shape
    seen = np.zeros_like(expanded)
    regions: list[list[int]] = []
    for start_y, start_x in zip(*np.nonzero(expanded & ~seen)):
        if seen[start_y, start_x]:
            continue
        queue = deque([(int(start_y), int(start_x))])
        seen[start_y, start_x] = True
        xs: list[int] = []
        ys: list[int] = []
        while queue:
            y, x = queue.popleft()
            xs.append(x)
            ys.append(y)
            for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                if (
                    0 <= ny < height
                    and 0 <= nx < width
                    and expanded[ny, nx]
                    and not seen[ny, nx]
                ):
                    seen[ny, nx] = True
                    queue.append((ny, nx))
        if len(xs) >= 9:
            regions.append([
                max(0, min(xs) - 3),
                max(0, min(ys) - 3),
                min(width, max(xs) + 4),
                min(height, max(ys) + 4),
            ])
    return sorted(
        regions,
        key=lambda r: (r[2] - r[0]) * (r[3] - r[1]),
        reverse=True,
    )[:12]


def compare(reference_path: Path, candidate_path: Path, output: Path,
            component: str, scale: str, reference_manifest_hash: str,
            candidate_hash: str, candidate_commit: str,
            candidate_dirty: bool) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    reference_image = Image.open(reference_path).convert("RGBA")
    candidate_image = Image.open(candidate_path).convert("RGBA")
    original_sizes = {
        "reference": list(reference_image.size),
        "candidate": list(candidate_image.size),
    }
    width = max(reference_image.width, candidate_image.width)
    height = max(reference_image.height, candidate_image.height)

    def normalize(image: Image.Image) -> Image.Image:
        canvas = Image.new("RGBA", (width, height), image.getpixel((0, 0)))
        canvas.alpha_composite(image, (0, 0))
        return canvas

    reference_image = normalize(reference_image)
    candidate_image = normalize(candidate_image)
    reference = np.asarray(reference_image, dtype=np.int16)[:, :, :3]
    candidate = np.asarray(candidate_image, dtype=np.int16)[:, :, :3]
    channel_delta = np.abs(reference - candidate)
    delta = channel_delta.max(axis=2)
    exact = delta > 0
    significant = delta > 8
    reference_bg = reference[0, 0]
    candidate_bg = candidate[0, 0]
    reference_fg = np.max(
        np.abs(reference - reference_bg), axis=2) > 3
    candidate_fg = np.max(
        np.abs(candidate - candidate_bg), axis=2) > 3
    missing = reference_fg & ~candidate_fg
    extra = candidate_fg & ~reference_fg
    overlap = reference_fg & candidate_fg

    reference_box = bbox(reference_fg)
    candidate_box = bbox(candidate_fg)
    reference_center = centroid(reference_fg)
    candidate_center = centroid(candidate_fg)
    shift_x, shift_y = best_profile_shift(reference_fg, candidate_fg)

    reference_luma = (
        reference[:, :, 0] * 0.2126
        + reference[:, :, 1] * 0.7152
        + reference[:, :, 2] * 0.0722
    )
    candidate_luma = (
        candidate[:, :, 0] * 0.2126
        + candidate[:, :, 1] * 0.7152
        + candidate[:, :, 2] * 0.0722
    )
    reference_bright = reference_luma > 145
    candidate_bright = candidate_luma > 145
    bright_reference_center = centroid(reference_bright)
    bright_candidate_center = centroid(candidate_bright)

    diagnoses: list[str] = []
    if original_sizes["reference"] != original_sizes["candidate"]:
        diagnoses.append(
            f"Canvas size differs: reference {original_sizes['reference'][0]}×"
            f"{original_sizes['reference'][1]}, candidate "
            f"{original_sizes['candidate'][0]}×{original_sizes['candidate'][1]}."
        )
    if reference_box != candidate_box:
        diagnoses.append(
            f"Foreground bounds differ: reference {reference_box}, "
            f"candidate {candidate_box}."
        )
    if shift_x or shift_y:
        diagnoses.append(
            f"Best whole-foreground alignment moves the candidate "
            f"{shift_x:+d}px horizontally and {shift_y:+d}px vertically."
        )
    if bright_reference_center and bright_candidate_center:
        bright_dy = bright_candidate_center[1] - bright_reference_center[1]
        if abs(bright_dy) >= 0.35:
            diagnoses.append(
                f"Bright/text ink centroid is {bright_dy:+.2f}px vertically "
                "from the reference (candidate − reference)."
            )
    reference_count = max(1, int(reference_fg.sum()))
    if missing.any():
        diagnoses.append(
            f"{int(missing.sum())} reference foreground pixels are missing "
            f"({missing.sum() / reference_count * 100:.2f}% of reference ink)."
        )
    if extra.any():
        diagnoses.append(
            f"{int(extra.sum())} candidate foreground pixels are extra."
        )
    if overlap.any():
        overlap_mean = float(channel_delta[overlap].mean())
        if overlap_mean >= 1:
            diagnoses.append(
                f"Overlapping foreground differs by {overlap_mean:.2f} "
                "RGB levels per channel on average."
            )
    if not diagnoses and exact.any():
        diagnoses.append("Only sub-threshold antialias/color rounding differs.")
    if not exact.any():
        diagnoses.append("Exact pixel match.")

    gray = np.clip(
        candidate.mean(axis=2, keepdims=True) * 0.42,
        0,
        255,
    )
    heat = np.repeat(gray, 3, axis=2).astype(np.uint8)
    strength = np.clip(delta.astype(np.float32) / 48.0, 0.25, 1.0)
    heat[exact, 0] = 255
    heat[exact, 1] = (heat[exact, 1] * (1 - strength[exact])).astype(np.uint8)
    heat[exact, 2] = (heat[exact, 2] * (1 - strength[exact])).astype(np.uint8)
    heat_image = Image.fromarray(heat, "RGB").convert("RGBA")
    regions = connected_regions(significant)
    draw = ImageDraw.Draw(heat_image)
    for region in regions:
        draw.rectangle(
            [region[0], region[1], region[2] - 1, region[3] - 1],
            outline=(255, 42, 42, 255),
            width=1,
        )

    reference_image.save(output / "reference.png")
    candidate_image.save(output / "candidate.png")
    heat_image.save(output / "diff.png")
    metrics = {
        "component": component,
        "scale": scale,
        "referenceManifestSha256": reference_manifest_hash,
        "candidateManifestSha256": candidate_hash,
        "candidateCommit": candidate_commit,
        "candidateDirty": candidate_dirty,
        "passed": not exact.any(),
        "exactDifferentPixels": int(exact.sum()),
        "exactDifferentPercent": round(float(exact.mean() * 100), 4),
        "significantDifferentPixels": int(significant.sum()),
        "significantDifferentPercent": round(float(significant.mean() * 100), 4),
        "maximumChannelDelta": int(delta.max()),
        "meanChannelDelta": round(float(channel_delta.mean()), 4),
        "referenceBounds": reference_box,
        "candidateBounds": candidate_box,
        "referenceCentroid": reference_center,
        "candidateCentroid": candidate_center,
        "bestAlignment": {"x": shift_x, "y": shift_y},
        "regions": regions,
        "diagnoses": diagnoses,
    }
    (output / "report.json").write_text(
        json.dumps(metrics, indent=2), encoding="utf-8")
    (output / "index.html").write_text(
        single_report_html(metrics), encoding="utf-8")
    return metrics


def single_report_html(report: dict) -> str:
    status = "PASS" if report["passed"] else "FAIL"
    candidate_label = html.escape(report["candidateCommit"][:12])
    if report["candidateDirty"]:
        candidate_label += " + dirty"
    diagnoses = "".join(
        f"<li>{html.escape(item)}</li>" for item in report["diagnoses"])
    return f"""<!doctype html>
<html><head><meta charset="utf-8"><title>{html.escape(report['component'])}</title>
<style>{REPORT_CSS}</style></head><body>
<main><header><div><h1>{html.escape(report['component'])}</h1>
<p>Scale {html.escape(str(report['scale']))}</p></div>
<strong class="status {'pass' if report['passed'] else 'fail'}">{status}</strong>
</header>
<section class="metrics">
<span>Exact diff <b>{report['exactDifferentPixels']:,} px</b></span>
<span>Significant diff <b>{report['significantDifferentPixels']:,} px</b></span>
<span>Maximum channel delta <b>{report['maximumChannelDelta']}</b></span>
<span>Candidate <b>{candidate_label}</b></span>
</section>
<section class="images">
<figure><figcaption>Picto reference</figcaption><div class="viewport"><img src="reference.png"></div></figure>
<figure><figcaption>Current Crystarium</figcaption><div class="viewport"><img src="candidate.png"></div></figure>
<figure><figcaption>Automated red failure map</figcaption><div class="viewport"><img src="diff.png"></div></figure>
</section>
<section><h2>Measured diagnosis</h2><ul>{diagnoses}</ul></section>
</main>{inspection_script()}</body></html>"""


def inspection_script() -> str:
    return """<script>
document.querySelectorAll('.images,.triptych').forEach(group=>{
 const panes=[...group.querySelectorAll('.viewport')];
 let syncing=false;
 panes.forEach(source=>source.addEventListener('scroll',()=>{
  if(syncing)return;
  syncing=true;
  panes.forEach(target=>{
   if(target!==source){target.scrollLeft=source.scrollLeft;target.scrollTop=source.scrollTop;}
  });
  syncing=false;
 }));
});
</script>"""


REPORT_CSS = """
:root{color-scheme:dark;font-family:Segoe UI,sans-serif;background:#101114;color:#fff}
*{box-sizing:border-box}body{margin:0}main{max-width:1280px;margin:auto;padding:24px}
header{display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid #ffffff18}
h1{margin:0 0 4px;font-size:24px}p{margin:0 0 18px;color:#ffffff88}
.status{padding:6px 10px;border-radius:6px}.pass{background:#2e9f552c;color:#72df95}
.fail{background:#ff47572c;color:#ff7a86}.metrics{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}
.metrics span{background:#242528;padding:8px 10px;border-radius:6px}
.images{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:12px}
figure{margin:0;background:#18191b;border:1px solid #ffffff18;border-radius:8px;overflow:hidden}
figcaption{padding:8px 10px;background:#242528;color:#ffffffb8}
.viewport{overflow:auto;max-height:70vh}.viewport img{display:block;width:auto;height:auto;max-width:none;image-rendering:pixelated}
h2{font-size:15px;margin-top:22px}li{margin:7px 0;color:#ffffffc8}
"""


def aggregate(root: Path, reference_manifest_hash: str,
              candidate_hash: str) -> None:
    reports = []
    for path in sorted(root.glob("results/*/*/*/report.json")):
        report = json.loads(path.read_text(encoding="utf-8"))
        report["base"] = str(path.parent.relative_to(root)).replace("\\", "/")
        report["href"] = report["base"] + "/index.html"
        report["stale"] = (
            report.get("referenceManifestSha256") != reference_manifest_hash
            or report.get("candidateManifestSha256") != candidate_hash
        )
        reports.append(report)
    if not reports:
        raise RuntimeError("No comparison reports were generated.")

    def report_status(item: dict) -> tuple[str, str]:
        if item["stale"]:
            return "STALE", "stale"
        if item["passed"]:
            return "PASS", "pass"
        return "FAIL", "fail"

    def card_html(item: dict) -> str:
        state = report_status(item)
        return (
        f"<article class='card' data-name='{html.escape(item['component'])}'>"
        f"<header><div><a href='{html.escape(item['href'])}'>"
        f"<h2>{html.escape(item['component'])}</h2></a>"
        f"<p>Scale {html.escape(str(item['scale']))} · "
        f"{item['exactDifferentPixels']:,} different pixels</p></div>"
        f"<strong class='status {state[1]}'>{state[0]}</strong></header>"
        "<div class='triptych'>"
        f"<figure><figcaption>Picto</figcaption><div class='viewport'><img src='{item['base']}/reference.png'></div></figure>"
        f"<figure><figcaption>Crystarium</figcaption><div class='viewport'><img src='{item['base']}/candidate.png'></div></figure>"
        f"<figure><figcaption>Red diff</figcaption><div class='viewport'><img src='{item['base']}/diff.png'></div></figure>"
        f"</div><p class='finding'>{html.escape(item['diagnoses'][0])}</p>"
        "</article>")

    # Report presentation is independent of pixel generation. Refresh every
    # detail page while aggregating so viewer fixes never require recapturing
    # or recomputing otherwise-current evidence.
    for item in reports:
        report_path = root / item["base"] / "index.html"
        report_path.write_text(single_report_html(item), encoding="utf-8")

    cards = "".join(card_html(item) for item in reports)
    failed = sum(not item["passed"] and not item["stale"] for item in reports)
    stale = sum(item["stale"] for item in reports)
    document = f"""<!doctype html><html><head><meta charset="utf-8">
<title>Picto ↔ Crystarium conformance</title><style>{REPORT_CSS}
a{{color:#7db8ff;text-decoration:none}}header input{{width:260px;height:32px;padding:0 10px;border:1px solid #ffffff24;
border-radius:6px;background:#0003;color:white}}.catalog{{display:grid;gap:18px;margin-top:18px}}
.card{{background:#18191b;border:1px solid #ffffff18;border-radius:10px;padding:14px}}
.card>header{{border:0}}.card h2{{margin:0 0 4px;font-size:17px}}.triptych{{display:grid;
grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}}.finding{{color:#ffffffb8;margin:10px 0 0}}
.stale{{background:#d99b242c;color:#f2be5c}}
[hidden]{{display:none!important}}</style></head><body><main>
<header><div><h1>Picto ↔ Crystarium conformance</h1>
<p>Automated exact-pixel comparison; {failed} current captures fail,
{stale} captures are stale.</p></div>
<input id="filter" placeholder="Filter components…"></header>
<section class="catalog">{cards}</section></main>
<script>
const filter=document.getElementById('filter');
filter.addEventListener('input',()=>{{
 const q=filter.value.toLowerCase();
 document.querySelectorAll('.card').forEach(
  x=>x.hidden=!x.dataset.name.includes(q));
}});
</script>{inspection_script()}</body></html>"""
    (root / "index.html").write_text(document, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference", type=Path)
    parser.add_argument("--candidate", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--component")
    parser.add_argument("--scale", default="1")
    parser.add_argument("--aggregate", type=Path)
    parser.add_argument("--reference-manifest-hash", default="")
    parser.add_argument("--candidate-hash", default="")
    parser.add_argument("--candidate-commit", default="")
    parser.add_argument("--candidate-dirty", default="false")
    args = parser.parse_args()
    if args.aggregate:
        aggregate(
            args.aggregate,
            args.reference_manifest_hash,
            args.candidate_hash,
        )
        return
    required = [args.reference, args.candidate, args.output, args.component]
    if any(value is None for value in required):
        parser.error("comparison requires reference, candidate, output, and component")
    compare(
        args.reference,
        args.candidate,
        args.output,
        args.component,
        args.scale,
        args.reference_manifest_hash,
        args.candidate_hash,
        args.candidate_commit,
        args.candidate_dirty.lower() == "true",
    )


if __name__ == "__main__":
    main()
