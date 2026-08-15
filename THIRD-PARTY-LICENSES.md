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
| KamiToolKit | 1.1.17 | **MIT** | Direct (`Poser.Game.csproj`) | Package `.nuspec` declares no license, but its `<repository url>` is `https://github.com/MidoriKami/KamiToolKit`; that repo's `LICENSE` reads "MIT License / Copyright (c) 2024 MidoriKami". Fetched from `raw.githubusercontent.com/MidoriKami/KamiToolKit/master/LICENSE`. |
| SixLabors.ImageSharp | 3.1.12 | **Apache-2.0** (granted under the Six Labors Split License) | Transitive, via KamiToolKit | See the split-license determination below. |
| K4os.Compression.LZ4.Legacy | 1.3.8 | **MIT** | Direct (`Poser.Game.csproj`) | The `.nuspec` carries only a `licenseUrl` pointing at `github.com/MiloszKrajewski/K4os.Compression.LZ4/blob/master/LICENSE`; that file reads "MIT License / Copyright (c) 2017 Milosz Krajewski". Fetched. |
| K4os.Compression.LZ4 | 1.3.8 | **MIT** | Transitive, via `.Legacy` | Same repository and LICENSE file. |
| Microsoft.Extensions.DependencyInjection | 10.0.10 | **MIT** | Direct (`Poser.csproj`) | `.nuspec` in the NuGet cache: `<license type="expression">MIT</license>`. |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.10 | **MIT** | Transitive | `.nuspec`: `<license type="expression">MIT</license>`. |

All six are GPL-3.0-compatible. MIT is permissive; Apache-2.0 is one-way
compatible with GPLv3 (not GPLv2), and Poser is v3.

### SixLabors.ImageSharp — split-license determination

ImageSharp 3.x ships under the **Six Labors Split License, Version 1.0**, which
grants either Apache-2.0 or a paid commercial license depending on how the
consumer qualifies. The license file bundled in the package
(`~/.nuget/packages/sixlabors.imagesharp/3.1.12/LICENSE`) grants Apache-2.0 when,
among other criteria:

- "You are consuming the Work in for use in software licensed under an Open Source or Source Available license."
- "You are consuming the Work as a Transitive Package Dependency."

**Verdict: Apache-2.0. Not ambiguous.** Poser qualifies on *both* clauses
independently — it is GPL-3.0-only open-source software, and ImageSharp reaches
it only as a transitive dependency of KamiToolKit (`Poser/packages.lock.json`
lists it as `"type": "Transitive"` under KamiToolKit's dependency group; no Poser
csproj references it). No commercial license is required and none is implied.

The split license adds: "Once granted, You must reference the granted license
only in all documentation." That is why this file names Apache-2.0 and not
"Six Labors Split License" as ImageSharp's license for this use.

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
