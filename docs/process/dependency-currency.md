# Dependency currency

Recurring check that dependencies stay current.

```powershell
Get-ChildItem */*.csproj | ForEach-Object { dotnet list $_ package --outdated }
```

- Also compare `Dalamud.NET.SDK` on NuGet against every project pinning it;
  bump all projects together, then full build.
- Patch/minor bumps apply immediately (bump + build + chase errors); major
  bumps get a changelog read first.
- SDK-injected packages (e.g. `DotNet.ReproducibleBuilds`) belong to the
  Dalamud SDK — not ours to bump.
- A bump that changes UI is presented in game for review.
