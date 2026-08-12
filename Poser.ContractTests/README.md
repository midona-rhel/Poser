# Poser contract tests

This target is the clean-checkout home for Domain/Application contract
characterization and composition-facing fakes. It deliberately references no
Dalamud or native runtime assembly, so Release test discovery stays available
without a game installation.

Run the target with:

```powershell
dotnet test Poser.ContractTests/Poser.ContractTests.csproj -c Release --no-restore --nologo
```

The activation fixture is test-only until startup exposes an injectable
activation seam; it must not be read as production behavior.
