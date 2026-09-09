# Testing

- Native GPose lifetime investigations use `[GPoseLifetime]` log entries at
  entry/exit, after entry settles, and around actor/companion/overlay/IK teardown.
  Model, skeleton, owner and bounded parent-chain addresses are read with
  `ReadProcessMemory`, not by dereferencing potentially stale pointers. These
  breadcrumbs correlate lifetime events; they do not establish fault ownership.
  Collect the full Dalamud log and crash dump after recurrence. A crash before
  the framework observes entry may leave only the preceding exit breadcrumbs.
  There is no continuous per-frame logging or change to cleanup behavior.

- The Debug-only loopback bridge and `tools/poser_mcp.py` can add a scene
  through `SceneWorkflow`, inspect load progress/resources/native rig counts,
  capture the rendered game via Dalamud's viewport texture API, and queue
  ImGui input. Scene automation is additive and never clears existing entities.
  UI input is not OS/game input; button/key presses require a matching release.
  Screenshots include the main game viewport and its plugin UI, not detached
  external viewports. These probes are diagnostics, not acceptance verdicts.

- The in-game harness is the wiring/native gate. `/poser test basic` runs
  these eight scenarios once: `selection.actor-bone-clear`,
  `transform.actor-components`, `transform.actor-undo-redo`,
  `posing.bone-components`, `posing.animation-interference`,
  `posing.reset-region`, `posing.copy-paste-pose`, and `posing.ik-bake`.
  A scenario id narrows diagnosis; `/poser test full` runs all eight at the
  acceptance repetition count; `status` and `cancel` manage runs.
- The harness drives production command routes in GPose, snapshots boundaries,
  checks shared invariants, and restores the user's actors and selection. It
  does not judge visual UI conformance. `run.json` is the authoritative,
  atomically written verdict; only `Succeeded` is success, and
  `AcceptanceQualified` additionally requires the repetition count. Never
  infer success from chat text or file existence.
- `tools/Test-PoserLiveRun.ps1` reads the persisted verdict outside the game
  (exit 0 success, 1 failure, 2 running, 3 invalid). Artifacts per run are
  `live-tests/<UTC>/run.json`, `events.jsonl`, `report.json`, `summary.md`,
  and `snapshots/`.
- Visual and behavioral acceptance belongs to the user in the running game.
  Give a short starting-state/actions/expected-result card and ask for observed
  pass/fail and reproduction details. No video verification or recording is
  required; screenshots are optional diagnostic evidence, not an acceptance gate.
  Synthetic component catalogs and capture labs are not product evidence.
- Never contract-test UI visual, layout, rendering, presentation, or wiring
  contracts; validate UI with Release compilation plus explicit live visual
  test cards.
- Non-deployment validation uses Release only:
  `dotnet build Poser.slnx -c Release --no-restore --nologo` and
  `dotnet test Poser.slnx -c Release --no-restore --nologo`.
  Never use Debug as an ordinary compile or test substitute.
- Before handing each issue to the user for in-game testing, the organizer
  announces deployment, builds Debug from the exact reviewed head that passed
  Release gates, and deploys its matching runtime output to
  `C:\Users\Midona\OneDrive\Dokument\GitHub\Poser\Poser\bin\Debug`.
  The entry DLL is `Poser.dll` there; include its matching dependency assemblies,
  manifest, and runtime content. A build left in another worktree is not deployed.
  Verify destination hashes against the source build and report the head and
  deployment result. Only one candidate may own this output at a time.
- Deployment triggers automatic reload. Confirm successful loading from the
  logs; do not ask the user to reload manually unless automatic reload failed.
  The user tests that deployed candidate and gives the acceptance verdict.
  Passing builds/tests or agent inspection do not replace that verdict; do not
  mark the issue accepted or merge it before the user's confirmation.
- Visual and native behavior still requires the applicable in-game acceptance
  card; compilation is not runtime proof.
