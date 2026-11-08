# Testing

## Unit-test budget and relevance

Effective 2026-10-25: for every new unit test, remove at least three existing
unit tests in the same change. Prefer extending a relevant existing test over
adding another. If no justified removals are available, do not add a new test.

Remove redundant checks, tests of deleted behavior, assertions that merely
repeat implementation, and scenarios with no reachable supported caller.
Explain what each removed test covered and why that check is unnecessary or
already covered. Do not remove a distinct useful regression check just to pay
for an unrelated new test, or combine unrelated tests merely to hide the count.
Count added parameterized cases as additions rather than using them to evade
the budget. Report added, removed and net discovered test counts.

A retained or new test must check a meaningful behavior or concrete correctness
requirement. Fake ports are useful when they exercise a real failure boundary;
an invented rapid-click sequence alone does not establish that boundary.
Use live acceptance for UI behavior and native effects that unit tests cannot
establish. A larger passing suite is not a substitute for relevant evidence.

The user is considering a 200-test cap; that cap is not yet an instruction to
delete tests down to 200. The three-removals-per-addition rule applies now.

## Runtime validation and deployment

- Debug bridge `/camera?camera=NAME&action=probe-native-pan&x=VALUE&y=VALUE`
  changes the native camera pan so CameraRestRecorder can observe a pending move.
  Use it to test the five-second resting window and ordering before another edit.
  Ordinary `/camera` actions use the undoable camera controls instead.

- Debug bridge `/lifetimetrace?path=<new-absolute-file>` opts into a bounded
  120-second read-only native skeleton-list trace; `stop=1` ends it early.
  It uses safe process-memory reads and flushes changed owner/header rows
  plus frame markers to a local file. It does not repair or suppress native
  updates. Native offsets are explained at the reader. `/scene?cancel=1`
  invokes the same cancellation command as the scene UI.

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
  ImGui input. Scene automation is additive by default; `clear=true` explicitly
  exercises the production clear-first load contract in a disposable scene.
  UI input is not OS/game input; button/key presses require a matching release.
  Screenshots include the main game viewport and its plugin UI, not detached
  external viewports. These probes are diagnostics, not acceptance verdicts.
  `/uiwindows` lists every registered Poser window and can open, close, move,
  or resize an exact named test window; it cannot target other plugins' windows.
  A `published` readback distinguishes a registered window from one that has
  an existing native ImGui window; it does not prove that window drew this frame.
  `/uidrag` queues a bounded continuous ImGui gesture (at most eight seconds),
  releasing its button and restoring native event admission on completion,
  cancellation or disposal. Its bounded trace verifies the injected input,
  not whether the target control behaved correctly; measure that separately.
  `/worldsources` lists the portal's current object, effect, and world-light
  candidate IDs and acquires only through the same incarnation-validated
  application service as the UI; native addresses are never bridge identity.
  Debug scene-autosave failure tests select their isolated root through a
  validated process override and a plugin reload; the running store is never
  swapped, and clearing the override plus another reload restores the default.

- The Debug bridge's `/gazeprobe?actor&start=1` records at most 128 native
  pre/post-pose samples over two seconds; it never writes bones. `/gazepose`
  and `/gazeparts` use production gaze commands. `/undo?selected=1` and
  `/redo?selected=1` use the same scoped facade as the shortcuts.

- Validation is the Release build plus every test project, run locally and in
  CI (`.github/workflows/architecture.yml`), plus the user's in-game test card.
  There is no in-game automated harness; wiring and native behavior are judged
  by the user against that card in the running game.
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
