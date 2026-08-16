# Release runbook

Use this procedure only from the approved `main` commit. It records a release;
it does not make a release safe by itself. Stop at the first failed check.

## 1. Record the release identity

Run from the repository root. Choose the Git tag only after the manifest and
project version agree. A prerelease label belongs in the tag and release notes;
the Dalamud assembly version remains numeric.

For the current first beta, the public tag and changelog identity are
`v0.9.0-beta.1` and `0.9.0-beta.1`; they map to the numeric manifest and
assembly version `0.9.0.0`.

```powershell
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path .).Path
$Manifest = Get-Content (Join-Path $Repo 'Poser/Poser.json') -Raw | ConvertFrom-Json
$Project = [xml](Get-Content (Join-Path $Repo 'Poser/Poser.csproj'))
$ProjectVersion = $Project.Project.PropertyGroup.Version | Where-Object { $_ }
$ReleaseTag = 'v0.9.0-beta.1'
$ReleaseNotesVersion = $ReleaseTag.TrimStart('v')
$Changelog = Get-Content (Join-Path $Repo 'docs/release/CHANGELOG.md') -Raw

$Manifest.AssemblyVersion
$ProjectVersion
if ($Manifest.AssemblyVersion -ne $ProjectVersion) {
    throw 'Poser.json and Poser.csproj have different versions.'
}
if ($Manifest.AssemblyVersion -ne '0.9.0.0') {
    throw 'The current beta requires assembly version 0.9.0.0.'
}
if ($Changelog -notmatch [regex]::Escape("## $ReleaseNotesVersion")) {
    throw 'The changelog does not match the proposed release tag.'
}
if ([string]::IsNullOrWhiteSpace($Manifest.RepoUrl)) {
    throw 'RepoUrl must name the published repository.'
}
```

An icon URL is optional. If one is set, verify that its target is published
before the release; remove a stale URL rather than shipping a broken one.

## 2. Build and test the approved commit

The tree must be clean. Every command below explicitly selects Release because
a Debug build deploys to the live game.

```powershell
git status --short
git rev-parse HEAD

dotnet build Poser.slnx -c Release --nologo -m:1 `
  -p:UseSharedCompilation=false --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

dotnet test Poser.slnx -c Release --nologo -m:1 `
  -p:UseSharedCompilation=false --disable-build-servers --no-build
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }
```

## 3. Check the tree before packaging

Review the tracked tree and the release exclusion manifest together. Do not
print secrets or personal paths into release logs; use redacted scan output.

- `dist/`, archives, build output, logs, dumps, local configuration, and game
  data must be untracked.
- `PosingCore/Data/GameData/` is excluded because it is game-derived data;
  `PosingCore/` itself is an active project and must remain.
- `THIRD-PARTY-LICENSES.md` must cover every redistributed dependency and its
  required notice.
- The final release notes must describe the exact approved tree, not an older
  branch or draft scope.

## 4. Check the final package separately

The source tree is not the package. Build the approved Release package, stage
the produced archive in an isolated directory, and inspect its canonical file
list, duplicate names, traversal-safe extraction targets, hashes, licenses,
and debug metadata with the approved platform-backed verifier. Do not reuse the
rejected PowerShell verifier.

Run the online dependency check against the approved lock/assets graph:

```powershell
dotnet list Poser.slnx package --vulnerable --include-transitive --no-restore `
  --source https://api.nuget.org/v3/index.json --format json
if ($LASTEXITCODE -ne 0) { throw 'Online vulnerability audit did not complete.' }
```

Generate and validate an SPDX SBOM from that staged package with a pinned
Microsoft SBOM tool. Keep the SBOM beside the archive, not inside it. Resolve
every shipped package with incomplete license metadata from its authoritative
notice before publication.

## 5. Publish only after approval

The repository distribution is unpublished until a matching, verified release
manifest, checksum set, archive, SBOM, and GitHub release are staged together.
The owner reviews visible packaging changes before publication. Record the
approved commit SHA, tag, package checksum, SBOM checksum, and release URL.

## Open final gates

Canonical history/tree scanning, final-archive scanning, online vulnerability
results, SBOM validation, third-party notice reconciliation, and release
manifest/checksum review all apply to the final approved commit. A successful
local build does not close them.
