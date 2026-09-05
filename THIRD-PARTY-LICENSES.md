# Third-party licenses and attribution

Poser is licensed **GPL-3.0-only**. See [LICENSE](LICENSE).

This file records every third party whose work Poser derives from, redistributes,
or links against, with the license each of them actually publishes. Every license
verdict below was read from a primary source; the source is named in the evidence
column so it can be re-checked rather than believed.

---

## 1. Upstream projects Poser is derived from

Poser is derivative of, and heavily inspired by, Anamnesis, Ktisis and Brio. The
credit and maintainer names live in [README.md](README.md#attribution); this
section records the *licenses* and what was taken.

| Project | Repository | License | Relationship |
|---|---|---|---|
| Ktisis | https://github.com/ktisis-tools/Ktisis | **GPL-3.0-only** | Mechanisms ported/derived: posing runtime and bone model, selective/subtree/reference import, gaze modes, overlay and gizmo interaction model, free camera, the props catalog data file. Poser's UX flow is deliberately measured against Ktisis's. |
| Brio | https://github.com/Etheirys/Brio | **GPL-3.0-only** | Mechanisms ported/derived: actor spawning and ownership, pose file formats and transfer semantics, one-click expressions, environment/festival control, lighting model, MCDF import/export flow, rest-pose and bone-category data files. |
| Anamnesis | https://github.com/imchillin/Anamnesis | **MIT** | Mechanisms derived: the bone-matrix / numeric transform editing model and pose-file lineage that Ktisis and Brio also descend from. No Anamnesis source is redistributed. |

### Why Poser is GPL-3.0-only, not MIT

Poser is a derivative work of Ktisis and Brio. **Both are GPL-3.0**, and neither
grants the "or any later version" option, so a derivative must be conveyed under
version 3 exactly — hence the SPDX identifier `GPL-3.0-only` rather than
`GPL-3.0-or-later`. Anamnesis being MIT does not weaken this: MIT material may be
combined into a GPL work, but GPL material cannot be relicensed out.

### Evidence

| Claim | Evidence |
|---|---|
| Ktisis is GPL-3.0 | `LICENSE` at the root of the Ktisis clone (`origin https://github.com/ktisis-tools/Ktisis.git`, head `e6b3dd41`): 674 lines, "GNU GENERAL PUBLIC LICENSE / Version 3, 29 June 2007", MD5 `e62637ea8a114355b985fd86c9ffbd6e`. |
| Brio is GPL-3.0 | `LICENSE` at the root of the Brio clone (`origin https://github.com/Etheirys/Brio.git`, head `b445346d`): byte-identical to Ktisis's, same MD5 `e62637ea8a114355b985fd86c9ffbd6e`. **This corrects an earlier working assumption that Brio was MIT — it is not.** |
| Neither is "or later" | No occurrence of "any later version" in either clone's sources, csproj, or README; neither csproj declares a `PackageLicenseExpression`. Bare GPLv3 with no version-upgrade clause = `GPL-3.0-only`. |
| Anamnesis is MIT | `https://raw.githubusercontent.com/imchillin/Anamnesis/master/LICENSE` — "MIT License / Copyright (c) 2020–2023 W & A Walsh; Copyright (c) 2023–2025 Aetherworks Group". Fetched, not assumed; no local clone exists. |
| Poser's own GPLv3 text | Copied verbatim from the FSF text carried by both clones (MD5 `e62637ea8a114355b985fd86c9ffbd6e`); the project notice is prepended above it, the license document itself is unmodified. |

### Upstream data files redistributed verbatim

These are not "inspired by" — they are upstream GPL-3.0 files shipped inside
Poser's assemblies as embedded resources. They are the most concrete reason the
copyleft applies.

| File in this repo | Upstream | Upstream license |
|---|---|---|
| `PosingCore/Data/RestPoses/BrioAPose.pose`, `BrioTPose.pose` | Brio, `Resources/Embedded/Data` | GPL-3.0-only |
| `PosingCore/Data/BoneCategories/BoneCategories.json` | Brio, `Resources/Embedded/Data/BoneCategories.json` | GPL-3.0-only |
| `Poser.Game/Data/Festivals.json` | Brio (curated festival names/phases/exclusions) | GPL-3.0-only |
| `Poser.Game/Data/props.json` | Ktisis, `Data/Library/props.json` (community props library) | GPL-3.0-only |

`Poser.Game/Lighting/Data/gobos.csv` is Poser's own curation and is covered by
Poser's license.

---

## 2. NuGet packages redistributed in the plugin

These assemblies are copied into the plugin output and therefore ship to users.
Verified against `Poser/bin` output and the `packages.lock.json` graph.

| Package | Version | License | Direct/transitive | Evidence |
|---|---|---|---|---|
| KamiToolKit | 2.2.27 | **MIT** | Direct (`Poser.Game.csproj`) | Package `.nuspec` declares no license, but its `<repository url>` is `https://github.com/MidoriKami/KamiToolKit`; that repo's `LICENSE` reads "MIT License / Copyright (c) 2024 MidoriKami". Fetched from `raw.githubusercontent.com/MidoriKami/KamiToolKit/master/LICENSE`. |
| K4os.Compression.LZ4.Legacy | 1.3.8 | **MIT** | Direct (`Poser.Game.csproj`) | The `.nuspec` carries only a `licenseUrl` pointing at `github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE`; that file reads "MIT License / Copyright (c) 2017 Milosz Krajewski". Fetched. |
| K4os.Compression.LZ4 | 1.3.8 | **MIT** | Transitive, via `.Legacy` | Same repository and LICENSE file. |
| Microsoft.Extensions.DependencyInjection | 10.0.10 | **MIT** | Direct (`Poser.csproj`) | `.nuspec` in the NuGet cache: `<license type="expression">MIT</license>`. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | **MIT** | Transitive | `.nuspec`: `<license type="expression">MIT</license>`. |

Stagehand.Definitions 0.4.10 (UniversalConquistador) is also redistributed, under
AGPL-3.0-or-later, as declared in its NuGet metadata. Its corresponding source is
https://github.com/universalconquistador/Stagehand/tree/f0769049294e1e314b9316089bd9c0db15049c47.
The bundled AGPL text is from that commit. GPLv3 section 13 permits combining
GPLv3 and AGPLv3 code; the AGPL network-interaction provisions apply to the
combination. Poser source remains GPL-3.0-only; the Stagehand library retains
its own license. MIT notices remain applicable to the other shipped packages.

ImageSharp is no longer in the resolved runtime graph or the release archive.
The older split-license determination is superseded by this package inventory.

---

## 3. Build-time and host-provided dependencies (not redistributed)

Listed for completeness; Poser conveys none of these.

| Component | Role | License |
|---|---|---|
| Dalamud, `Dalamud.NET.SDK` 15.0.0, `DalamudPackager` | Plugin host and packaging SDK. Dalamud supplies `Dalamud.dll`, `FFXIVClientStructs`, `Lumina`, `Newtonsoft.Json`, `Serilog`, the ImGui bindings and `Microsoft.Extensions.ObjectPool` at runtime; Poser references them with `Private=false` and ships none of them. | **AGPL-3.0** — `raw.githubusercontent.com/goatcorp/Dalamud/master/LICENSE`, "GNU AFFERO GENERAL PUBLIC LICENSE / Version 3, 19 November 2007". Fetched. Components per their own repositories. |
| DotNet.ReproducibleBuilds 1.2.39 | Build-time only. | MIT (`.nuspec` license expression) |
| xunit.v3 2.0.3, xunit.runner.visualstudio 3.1.1 | Test projects only; never in the plugin output. | Apache-2.0 (`.nuspec` license expression) |
| Microsoft.NET.Test.Sdk 17.14.1 | Test projects only. | MIT (`.nuspec` license expression) |
| NSubstitute 5.3.0 | Test projects only. | BSD-3-Clause (`.nuspec` license expression) |

FINAL FANTASY XIV is © SQUARE ENIX CO., LTD. Poser is an unofficial,
non-commercial tool and is not affiliated with or endorsed by Square Enix.

---

## 4. Open item for the owner

Nothing in this file is blocked. One judgement call is recorded rather than made:

- **Source-header notices.** GPLv3 recommends a short copyright/permission header
  in each source file. Poser has none. The root `LICENSE`, this file and the
  README attribution satisfy the license's actual conveyance requirements; adding
  ~500 file headers is a separate mechanical change if the owner wants it.

---

## 5. Notices carried with the final release

### MIT notices

The following copyright notices use the MIT text below:

- Tabler Icons — Copyright (c) 2020-2025 Paweł Kuna. Poser embeds Tabler icon
  source in `Poser.UI/Icons`; source: https://github.com/tabler/tabler-icons.
- K4os.Compression.LZ4 and K4os.Compression.LZ4.Legacy — Copyright (c) 2017
  Milosz Krajewski.
- KamiToolKit — Copyright (c) 2024 MidoriKami.
- Microsoft.Extensions.DependencyInjection and
  Microsoft.Extensions.DependencyInjection.Abstractions — Copyright (c) .NET
  Foundation and Contributors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

### Required staged notices

The release archive carries these notices; the release manifest records each
file hash. The generated SBOM and archive checksum are separate release assets:

| Staged file | Required source and coverage |
|---|---|
| `THIRD-PARTY-LICENSES.md` | This attribution file, including the MIT notices above. |
| `Data/Licenses/Stagehand-AGPL-3.0.txt` | AGPLv3 text from the exact Stagehand source commit above. |
| `Data/Licenses/Geist-OFL.txt` | Complete SIL OFL notice supplied with the Geist fonts. |
| `Data/Licenses/KamiToolKit-MIT.txt` | MIT notice from package 2.2.27 repository commit `1a7682a106d0a71340c7aa11de76fae9a41041e5`. |
| `Data/Licenses/THIRD-PARTY-NOTICES.txt` | One byte-identical copy from Microsoft.Extensions.DependencyInjection 10.0.10 or Microsoft.Extensions.DependencyInjection.Abstractions 10.0.10; the manifest names both package IDs and versions. |

These staged files, their hashes, the online vulnerability audit, the SBOM,
the canonical tree/history scan, and the final ZIP scan are release-time gates.
They are not claimed to be present in an unpublished archive.

## Ktisis invisible-skin data

Bundled files (`Poser/Data/Integration`): `skin-paths.json` and
`mt_c0101b0001_a.mtrl`, from the Ktisis project's invisible-skin
feature. Licensed under the GNU General Public License, Version 3 —
the same license as Poser.

## Geist and Geist Mono

Bundled font files (`Poser/Data/Fonts`): Geist Regular / Medium /
Italic and Geist Mono Regular, by Vercel. Licensed under the SIL Open
Font License, Version 1.1 —
<https://github.com/vercel/geist-font/blob/main/LICENSE.TXT>.
