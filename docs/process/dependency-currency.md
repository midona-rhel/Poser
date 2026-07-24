# Dependency Currency

Recurring check that project dependencies stay current. Reference-project
changes are reviewed only when they affect the focused behavior recorded in
`../reference/brio-ktisis-posing.md`.

## Procedure

```powershell
$projects = @(
  "Poser.Domain/Poser.Domain.csproj",
  "Poser.Application/Poser.Application.csproj",
  "PosingCore/PosingCore.csproj",
  "Poser.Game/Poser.Game.csproj",
  "Poser.UI/Poser.UI.csproj",
  "Poser/Poser.csproj"
)
$projects | ForEach-Object { dotnet list $_ package --outdated }
```

Also check [Dalamud.NET.SDK on NuGet](https://www.nuget.org/packages/Dalamud.NET.SDK)
against every project that pins it. Bump the production, UI, and standalone
projects together, then run the full build. If the dependency changes the UI,
present the running plugin to the user for in-game review.

Policy: patch/minor bumps are applied immediately (bump + build + chase errors). Major bumps get a quick changelog read first. SDK-injected packages (e.g. `DotNet.ReproducibleBuilds`, which appears in `--outdated` output but is declared nowhere in our files) are owned by the Dalamud SDK — not ours to bump.

## Log

| Date | Findings | Action |
|---|---|---|
| 2026-07-15 | Dalamud.NET.SDK 15.0.0 = latest on NuGet (all 4 projects). `Microsoft.Extensions.DependencyInjection` 10.0.2 → 10.0.10 (Poser). `DotNet.ReproducibleBuilds` 1.2.39 → 2.0.5 flagged but SDK-injected. dotnet SDK 10.0.301 installed. | Bumped MEDI to 10.0.10; build clean (0 errors). ReproducibleBuilds left to the SDK. |
