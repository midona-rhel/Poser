# Dependency currency

Check now and then that Poser's NuGet packages are up to date:

```powershell
Get-ChildItem */*.csproj | ForEach-Object { dotnet list $_ package --outdated }
```

- The Dalamud SDK is not a package line. It is the `Sdk="Dalamud.NET.SDK/…"`
  attribute at the top of `Poser/Poser.csproj`, `Poser.Game/Poser.Game.csproj`
  and `Poser.UI/Poser.UI.csproj`. Compare it with the newest version on NuGet,
  update all three together, then build everything.
- Apply patch and minor updates straight away: update, build, fix what breaks.
  Read the release notes before a major update.
- `DalamudPackager` and `DotNet.ReproducibleBuilds` are added by the Dalamud
  SDK. Leave their versions to the SDK.
- If an update changes how the interface looks or behaves, show it to the owner
  in the game before merging.
