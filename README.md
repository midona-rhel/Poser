# Poser

A posing and scene tool for FINAL FANTASY XIV's group pose, written as a
[Dalamud](https://github.com/goatcorp/Dalamud) plugin.

Poser is a **beta — a first release candidate**. It is not stable and it is not
finished.

## What it does

- Pose actors bone by bone: overlay handles, a graphical body map, and numeric
  transforms — every change one undo step. IK on any bone with a parent.
- Fill the frame: spawn actors and objects, or click a world handle to bring
  what is already standing there into the scene. The map's own objects are
  borrowed by reference and given back untouched.
- Stage the whole shot: save actors, objects, lights, camera and environment as
  one scene, and restore it with load options and a progress report.
- Light and shoot: custom lights, camera control, a free camera, snapping,
  group pivot and group moves.
- Faces: five gaze modes and one-click expressions.
- Compose: dialogue panels, chat bubbles and status lines drawn with the game's
  own UI nodes; reference pictures as floating, aspect-locked windows.
- Keep your work: a pose library with tags, authors and search; MCDF import and
  export; twenty-five rebindable actions with Poser, Brio and Ktisis presets.

The full release notes, and the list of what this beta does **not** do, are in
[docs/release/CHANGELOG.md](docs/release/CHANGELOG.md). The normative
documentation set is under [docs/](docs/).

## Installing

Poser installs through its own Dalamud plugin repository:

1. In the game, open Dalamud settings (`/xlsettings`) → **Experimental**.
2. Add this URL under **Custom Plugin Repositories** and save:

   ```text
   https://raw.githubusercontent.com/midona-rhel/Poser/master/dist/repo.json
   ```

3. Open the plugin installer (`/xlplugins`), search for **Poser**, install.

On first launch Poser states what it is — a beta, coded with the use of AI,
derivative of the projects credited below — and asks you to type `I accept`
before the workspace opens.

## Attribution

Poser is derivative of, and heavily inspired by, **Anamnesis**, **Ktisis** and
**Brio**. Those three are the real, mature projects if you are looking to pose;
this one is only here because of the work their contributors put in.

| Project | Repository | Maintainers and contributors |
|---|---|---|
| Anamnesis | https://github.com/imchillin/Anamnesis | ergoxiv and chirpxiv, after Yuki, Luminiari, Peebs-miqo and AsgardXIV |
| Ktisis | https://github.com/ktisis-tools/Ktisis | Chirp, Cazzar, Bwuny and contributors |
| Brio | https://github.com/Etheirys/Brio | Minmoose, Asgard and contributors |

Credits are the names each project publishes for itself, in its plugin manifest
or its README. Poser's own posing runtime, file formats and interaction model
were built by reading their work; where Poser matches their behaviour, that is
deliberate, and where it diverges the reason is written down in `docs/`.

## Coded with AI

Poser was coded with the use of artificial intelligence. That is stated up front
so you can decide for yourself. The plugin says the same thing on first launch
and asks you to confirm you have read it before the workspace opens.

## License

Poser is **GPL-3.0-only** — see [LICENSE](LICENSE).

It could not be anything else. Ktisis and Brio are both GPL-3.0 with no "or any
later version" option, and Poser derives mechanisms from both and ships data
files from both, so the copyleft carries through at version 3 exactly.
Anamnesis is MIT, which combines into a GPL work but does not loosen it.

Every upstream project, every NuGet package Poser redistributes, and the
evidence behind each license verdict are listed in
[THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
