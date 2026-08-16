# Poser Domain tests

This target is the final pure-Domain test home introduced by Slice 1. It
references only `Poser.Domain`; transitional Application characterization
remains in `Poser.ContractTests` until Slice 2.

The skipped tests are compile-safe characterization markers for the accepted
Slice 1 gaps. They are deliberately not committed as failing tests; the pure
Domain production lane will replace each marker with its structural contract
once the organizer assigns the path-exclusive lane.

Run the target with:

```powershell
dotnet test Poser.Domain.Tests/Poser.Domain.Tests.csproj -c Release --no-restore --nologo
```
