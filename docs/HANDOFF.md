# Poser — successor agent handoff

Written 2026-08-15, at the end of the convergence program and the first bug
wave. It is for the next coding agent Midona hands this project to. Read it
before touching anything; it is the map, the method, and the open ledger.

---

## 1. What Poser is, and what state it is in

Poser is a Dalamud plugin for FINAL FANTASY XIV's group pose: bone posing,
whole-scene staging, lights, cameras, gaze, expressions, a pose library,
world-object borrowing, on-screen text nodes and reference pictures. It is
openly derivative of **Anamnesis**, **Ktisis** and **Brio**, and says so in its
README, its manifest and a first-run notice the user must accept.

- **Released**: `v0.9.0-beta.1`, then `v0.9.1-beta` (this bug wave).
- **Repository**: one branch, `main`. The public history was reset to a single
  root commit at release; every development branch and the backup bundle were
  deleted at the owner's instruction. **There is no older history anywhere.**
- **License**: GPL-3.0-only (Ktisis and Brio are GPL-3.0 with no "or later";
  Poser derives mechanisms from both). Evidence per dependency in
  `THIRD-PARTY-LICENSES.md`.
- **Distribution**: self-hosted Dalamud repo. `dist/latest.zip` +
  `dist/repo.json`, installed by adding
  `https://raw.githubusercontent.com/midona-rhel/Poser/main/dist/repo.json`
  in Dalamud's custom plugin repositories.
- **Gate at time of writing**: Release build 0 warnings / 0 errors; 1415 tests
  passing, 4 pre-existing explicit skips.

`docs/release/runbook.md` is the operator procedure for cutting a release.
`docs/release/CHANGELOG.md` is user-facing and must be re-swept against the
tree before any release — over-claiming in a beta is the defect users notice
first.

---

## 2. How to work on it (the method that produced this state)

**Build and test gate — the only one that counts.** Run both, foreground,
from the repository root:

```
dotnet build Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers
dotnet test  Poser.slnx -c Release --nologo -m:1 -p:UseSharedCompilation=false --disable-build-servers --no-build
```

0 warnings and 0 failures, always. Never run these in the background inside a
subagent; they are the thing you are waiting for.

**Deploying to the live game.** A Debug build of `Poser/Poser.csproj` writes to
`Poser/bin/Debug`, which is the Dalamud dev-plugin path the game loads from.
Build Debug and the user reloads in game. Do not run a Debug build while
proving a Release gate.

**Repackaging for distribution.** The Release build produces
`Poser/bin/Release/Poser/latest.zip` via DalamudPackager. Copy it to
`dist/latest.zip`, refresh `dist/repo.json` (version, changelog, description,
`LastUpdate`) from `Poser/bin/Release/Poser/Poser.json`, commit, tag.

**Parallel lanes.** Non-trivial work went to Opus subagents, one concern each,
in their own git worktrees under `C:\tmp\Poser-<lane>`, branched off `main`.
The main loop reviews every diff, resolves conflicts itself, runs the gate,
and integrates by cherry-pick or fast-forward. Briefs must be surgical: exact
files, decisions pre-resolved, reference evidence required (file:line from the
Brio/Ktisis clones), foreground gates, a short report to
`C:\tmp\Poser-convergence-reports\<lane>.md`. Never let two lanes edit the
same surface.

**Reference clones** (read-only, never edited, never shipped):
`Poser/Brio/` (nested, gitignored) and `../Ktisis` (sibling). Fetch before
concluding a mechanism does not exist — a stale clone once cost a day.
Anamnesis is not cloned locally.

**House commit style.** First line is a full sentence about the behaviour
change, not a label; the body explains the mechanism and why. Read
`git log` before writing one.

**Documentation rule** (`AGENTS.md`): document durable concepts and non-obvious
invariants, one normative home per concept, no per-class documentation.

---

## 3. The user, and how to work with them

Midona is the FFXIV plugin developer who owns this. What was learned:

- **They verify in game; you never do.** There is no UI harness worth running.
  Build, deploy, and let them test. Their observations are ground truth; their
  guesses at causes are *hypotheses* — check them, and say so when they are
  wrong. Several were, and saying so was always the right call.
- **They want completeness, not carve-outs.** "Every feature" means every
  feature. A hedge like "v1 only" gets rejected. When a reference does
  something, either match it or write down with evidence why not.
- **Terminology is a defect surface.** "Bake", not "apply". "Scene", never
  "shot". "Objects", not "props". Imprecise labels get fixed on sight.
- **Silence is the worst failure mode.** Anything that can fail must say so —
  named refusals through `UserNotices` (Dalamud notifications), never a
  swallowed null. This principle found more bugs than any other.
- **They give feedback in long voice notes.** Parse every item; route each to a
  lane; do not lose the small ones — the padding, the icon, the wording.

---

## 4. Architecture in one page

- `Poser` — plugin entry, DI composition, all UI (windows, panes, views).
- `Poser.UI` — **Crystarium**, the design system: primitives, compositions
  (PageForm, ScrollRegion, FloatingSurface, SearchPicker, TexturePicker),
  theme tokens, the frame profiler. Never hand-roll layout; compose from here.
- `Poser.Game` — everything native: skeletons, spawning, lights, cameras,
  gaze, animation, scene runtime, world objects, overlay nodes, integrations.
- `Poser.Application` — session/orchestration layer (animation session,
  integration session, MCDF transaction, transforms).
- `Poser.Domain` — pure types.
- `Poser.Core` — posing core, files, config, library (renamed from
  `PosingCore` on 2026-08-15).

**Invariants that must not be broken:**

1. **The GPose write gate.** Native character writes go through
   `CanWriteCharacter`: non-zero address, `IsValid`, object index **201–439**.
   A GPose clone shares its `GameObjectId` with the overworld original, so an
   id-based lookup can return the *real* character — index-first always.
2. **Shell layout contract.** `AppShellView` owns scroll, gutter and origin.
   Panes draw into what they are given. One trailing gutter; the scrollbar
   lives in it.
3. **Derived widths use `UiWidth.Region`, never `UiWidth.Fixed`.** `Fixed`
   throws on ≤0 and a throw in layout math takes the whole window down.
4. **Adopted world objects are borrowed, never owned.** Initial transform
   captured at adoption; restored on release, GPose exit, unload and undo.
5. **Glamourer teardown is unlock + restore captured state.** Never
   `RevertStateName` — the clone and the player share one identity, and a
   revert wipes the user's real design.
6. **Layout math never throws mid-frame.** Guard non-positive spans and draw
   nothing.

---

## 5. Where the evidence lives

`C:\tmp\Poser-convergence-reports\` holds ~45 lane reports: parity audits, per
capability reviews against Brio and Ktisis, every bug root cause, the perf
waves, the release prep. **Read the relevant one before re-investigating
anything.** Highlights:

- `parity-gap-audit.md`, `capability-review-{posing,scene,camera-ux}.md` —
  what the references have that Poser does not, with sizes.
- `perf-wave1.md`, `perf-wave2.md`, `perf-frame-profiler.md` — the frame-cost
  analysis and what is left.
- `bug-*.md` — the first bug wave's root causes.

These live outside the repository. If `C:\tmp` is ever cleared they are gone;
consider copying them somewhere durable early.

---

## 6. Open decisions (OWNER-DECIDES — do not decide these yourself)

| # | Decision | Evidence |
|---|---|---|
| 1 | **Backdrop blur toggle.** Dalamud's blur costs 7 viewport-sized GPU passes per glass surface per frame. A "reduce transparency" setting would swap to the existing opaque path. Highest-leverage perf lever; also the experiment that proves whether blur is the cost. | `perf-wave2.md` |
| 2 | **Overlay dot geometry.** 16-segment dots ⇒ ~54k verts and ~2.4k draw calls per frame at 4 actors; octahedra mode can overflow ImGui's 16-bit index ceiling. 8 segments is near-identical visually. | `perf-wave2.md` |
| 3 | **Armature block on selection.** Selecting an actor turns on a full per-frame native skeleton walk that draws *zero* dots until bones are opted visible — the −20 FPS repro. Fix is a semantic change to a shared cache's side effects. | `perf-frame-profiler.md` |
| 4 | **Shadow feather rings.** 24–36 rings per glass surface; `ShadowTokens.FeatherLayers = 10` is dead code the renderer ignores. Honouring it softens shadows slightly. | `perf-wave2.md` |
| 5 | **Undress toggles (Ktisis).** Visor and weapon-hide are pure draw-state (admissible under "appearance stays with Glamourer"); hat-hide needs a cached restore; unequip and glasses are appearance writes (excluded). | `capability-review-scene.md` |
| 6 | **World clicks commit on release**, both references commit on press. Poser's overlay-wide contract. | `bug-bone-overlap-menu.md` |
| 7 | **Pick order: nearest-camera first**, references use draw order. Decides which bone a plain click takes. | `bug-bone-overlap-menu.md` |
| 8 | **Tri-state overlay eye** styling under the no-chip vocabulary (proposal: filled glyph at partial presence). | `fix-round6b.md` |
| 9 | **Deferred features**: reference-image extras, per-category bone colours, per-race dot offsets, transform lock, draggable IK targets, actor-to-bone attach, scene-tree folders, world furniture/VFX **spawning** (borrowing is done), IPC provider. | capability reviews |

**Permanently excluded by the owner** — do not resurrect: keyframe timeline,
localization, IPC provider *(as a release blocker)*, appearance/customize/
equipment editing (Glamourer's job), cutscene/XAT camera.

---

## 7. Known issues and immediate next steps

1. **Scene save** — three silent killers were fixed (no notice on failure, no
   destination folder creation, losing the shared bone-refresh slot to the
   auto-save). Unproven which ate the user's saves, because the original
   failure was silent. **The next failed save names itself** in a toast and the
   log; that name is the next bug's address. Ask the user to save a scene.
2. **FPS with the UI open** — decision 3 above is the leading candidate, then
   decision 1. The **frame profiler** (Settings ▸ General ▸ DIAGNOSTICS ▸ "Show
   frame profiler") lists every draw unit by self-ms and peak. The prediction
   to test: `Window · Bone overlay` is the row that jumps on selection, not
   anything under `Shell · inspector rail`. The profiler measures CPU inside
   the draw callback only — GPU blur cost is invisible to it.
3. **`CleanSceneLifecycle.RefreshCore`** rebuilds full binding maps every 500 ms
   even when nothing changed, and `CanonicalSignature` re-runs the whole
   structural deep copy a second time per tick. Analysis in `perf-wave2.md`;
   the incremental redesign was scoped but never shipped.
4. **Push status** — `main` and both tags were still local at handoff. The push
   is the owner's (no GitHub credentials on this machine):
   `git push -u origin main` and `git push origin v0.9.0-beta.1 v0.9.1-beta`.

---

## 8. Bug triage protocol

1. **Get the exact repro from the user**, in their words, and the log
   (`%AppData%\XIVLauncher\dalamud.log`). Their timing observations are
   diagnostic gold — "only when holding shift" isolated a whole mechanism.
2. **Check the evidence directory** before investigating. Somebody may have
   already read that code.
3. **Prove the mechanism, do not pattern-match.** Several confident hypotheses
   were wrong; the lanes that refuted their own briefs found the real causes.
4. **Any silent failure path found on the way gets a voice** through
   `UserNotices`, in the same commit as the fix.
5. **Contract-test the bug's shape**, not just the fix, so it cannot return.
6. Integrate, gate, deploy, and tell the user exactly what to verify.

---

*Poser stands on Anamnesis, Ktisis and Brio. Where it matches their behaviour
that is deliberate; where it diverges, the reason is written down. Keep it that
way.*
