# Testing

## Building

You need the .NET 10 SDK and a Dalamud install. `Poser`, `Poser.Game` and `Poser.UI` compile against Dalamud. The Dalamud SDK looks for it in XIVLauncher's `addon\Hooks\dev` folder, or in the folder named by the `DALAMUD_HOME` environment variable.

```powershell
dotnet build Poser.slnx -c Release --nologo
```

Use Release to check that the code compiles. A Debug build is for trying a change in the game (see below), never just to compile.

The Release build writes the plugin to `Poser/bin/Release` and the installable archive to `Poser/bin/Release/Poser/latest.zip`.

## Running the tests

```powershell
dotnet test Poser.slnx -c Release --no-build
```

There are three test projects:

- `Poser.Domain.Tests` and `Poser.Application.Tests` test Poser's own logic. They need no Dalamud install.
- `Poser.Game.Tests` tests the game-facing code against stand-ins for the game. It needs Dalamud to build, but it never runs the game.

`Poser.Application.Tests` also contains `ReleaseIdentityTests`, which fails when the version numbers, download links and release notes of a release disagree (see the [runbook](../release/runbook.md)).

CI runs `.github/workflows/architecture.yml` on every pull request and on pushes to `main`. It has two jobs:

- **portable** checks which projects reference which (`tools/Test-ProjectBoundaries.ps1`), then runs the Domain and Application tests without Dalamud.
- **full** downloads Dalamud, builds the whole solution in Release and runs every test project.

When tests fail in CI, `tools/Report-TestFailures.ps1` lists each failed test in the checks view.

## What the tests are for

Tests check that Poser's logic does what it should: what a command changes, how it undoes, how a scene loads, how a file is read and written. They do not show how the interface looks or behaves, or what the game does with a write. Those are checked in the game.

Do not write tests for the look, layout or wiring of the interface. A larger number of passing tests is not better evidence.

## Unit-test budget and relevance

- For every new unit test, remove at least three existing ones in the same change. Prefer extending a relevant existing test over adding one. If there is nothing that is right to remove, do not add the test.
- Remove checks that repeat each other, tests of behaviour that no longer exists, assertions that only repeat the code, and cases no real caller can reach. Say for each removed test what it covered and why that is no longer needed or is covered elsewhere.
- Do not remove a useful check just to pay for an unrelated new test, and do not merge unrelated tests to lower the count. Each added case of a parameterised test counts as an added test.
- Report how many tests were added and removed, and the net change.
- A test must check a real behaviour. A stand-in for the game is useful when it reproduces a failure the game can really produce.

## Checking a change in the game

How the interface looks and what the game does with a change can only be checked in the running game.

1. Build Debug from the exact commit that passed the Release build and tests.
2. Deploy it. Dalamud loads Poser as a dev plugin from the main checkout's `Poser/bin/Debug` folder and reloads it when the files there change. A Debug build in another worktree is not loaded: copy its whole `Poser/bin/Debug` output there. Only one change can be deployed at a time.
3. Check the Dalamud log that Poser reloaded.
4. Try the change. When someone else is to try it, write down where to start, what to do and what should happen.

A change is not finished until it has been tried in the game.

## The debug bridge

Debug builds start a small HTTP server on `127.0.0.1:47999` (`Poser/Bridge/DebugBridge.cs`). Release builds do not contain it. `tools/poser_mcp.py` offers its endpoints as MCP tools, and `.mcp.json` registers that script. The bridge can read the scene, undo and redo, load and save scenes, take a screenshot of the game, and send mouse and key input to Poser's own windows. It uses the same commands as the interface.

The bridge helps to investigate a problem and to try a change. It does not show how the interface looks.

The tools that read game memory while investigating, such as finding an animation's clock or recording the camera frame by frame, are also in Debug builds only. They live in `Poser.Game/Debug`, and the bridge calls them.
