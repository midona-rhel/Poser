# Testing

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
- UI conformance is a separate existing standalone capture/comparison harness
  under [`tools/ui-conformance`](../../tools/ui-conformance/README.md). It
  covers component sheets and pixel/interaction evidence; it is not a native
  runtime or live-game acceptance substitute.
- Non-deployment validation uses Release only:
  `dotnet build Poser.slnx -c Release --no-restore --nologo` and
  `dotnet test Poser.slnx -c Release --no-restore --nologo`.
  A Debug build auto-deploys the plugin to the live game; run it only once as
  the announced deployment action for the exact reviewed head after readiness
  is confirmed. Never use Debug as an ordinary compile or test substitute.
- Visual and native behavior still requires the applicable in-game acceptance
  card; compilation and standalone UI capture are not runtime proof.
