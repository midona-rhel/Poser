# Release runbook

`main` is where work is merged. A release is built from its own branch, `release/<version>`, cut from one chosen `main` commit. Releases are betas: version 0.9.24 has the tag `v0.9.24-beta` and the assembly version `0.9.24.0`.

## 1. Cut the branch

1. Squash-merge the pull requests that go into the release, close the issues they fix, and open new issues for anything left over.
2. Write down the `main` commit the release is built from and branch from that commit, not from whatever `origin/main` is by now: `git switch -c release/<version> <commit>`.

## 2. Write the release notes

The notes are for people who use Poser. Read the previous release's notes first and follow their wording and order.

- One sentence per bullet, in the past tense, starting with what happened: Added, Fixed, Made, Improved, Removed, Kept. Write "Fixed actors that couldn't be moved after …", not "PosingService seeds its actor map".
- Use the words the interface uses. No issue numbers, class names, offsets, or how it was built. Leave out work that changes nothing a user sees.
- Group the bullets under `###` headings by area (Animation, Scenes and files, Interface, …) and give the release a short theme.

The same notes go in four places:

| Where | Form |
|---|---|
| `docs/release/CHANGELOG.md`, newest at the top | `## <version>-beta — <theme>`, then the `###` groups and bullets |
| `ReleaseNotesHistory` in `Poser.Application/Settings/ReleaseNotesHistory.cs`, newest first; keep the older entries | the theme and the bullets, without groups. This is what Poser shows in its release notes window. |
| `Changelog` in `Poser/Poser.json` and in `repo.json` | the bullets as `- ` lines, without groups |
| The GitHub release text | `## Poser <version> — <theme>`, then the groups and bullets |

## 3. Set the version

On the release branch:

- `<Version>` in `Poser/Poser.csproj`, as four numbers (`0.9.24.0`).
- `AssemblyVersion` in `Poser/Poser.json` and in `repo.json`.
- The three download links in `repo.json` (`DownloadLinkInstall`, `DownloadLinkTesting`, `DownloadLinkUpdate`), pointing at `…/releases/download/v<version>-beta/latest.zip`.
- `LastUpdate` in `repo.json`, as Unix time in seconds.

`ReleaseIdentityTests` fails if the version numbers, the download links, the newest `ReleaseNotesHistory` entry, the two `Changelog` fields and the newest `CHANGELOG.md` section do not match. It does not check `LastUpdate`.

Commit and push the release branch.

## 4. Check the release

Run these on the clean release branch:

```powershell
dotnet build Poser.slnx -c Release
dotnet test Poser.slnx -c Release --no-build
dotnet list Poser.slnx package --vulnerable --include-transitive
```

Never use a Debug build for a release: it deploys to the running game. Stop if you are on `main`, the checkout has uncommitted changes, the versions or links do not match, or the tag already exists.

Then:

1. Look through the changes since the previous release tag for passwords, keys and local paths, and check the files against the [exclusions list](exclusions.md).
2. Open `Poser/bin/Release/Poser/latest.zip` and compare it with the previous release:
   - the same set of files, or a reason for each difference;
   - no file path that climbs out of the archive, no links, and no damaged entries;
   - no logs or debug output;
   - no debug bridge in the assemblies (it is only compiled into Debug builds);
   - `Poser.json` shows the new version;
   - `Poser.dll` carries the release commit's short hash (it is written into the build as `BuildMetadata.Commit`).
3. Check that every dependency shipped in the archive is listed in [THIRD-PARTY-LICENSES.md](../../THIRD-PARTY-LICENSES.md). The archive carries the license texts from `Poser/Data/Licenses`.
4. Make the software bill of materials with Microsoft SBOM Tool 4.1.5 from the unzipped archive and the release checkout, and check it:

   ```bash
   sbom-tool generate -b <unzipped archive> -bc . -pn Poser -pv <version> -ps Midona -nsb https://github.com/midona-rhel/Poser
   sbom-tool validate -b <unzipped archive> -o sbom-validation.json -mi SPDX:2.2
   ```

   The tool writes `<unzipped archive>/_manifest/spdx_2.2/manifest.spdx.json`. Copy it out as `Poser.spdx.json`, next to the archive, not inside it.
5. Write `release-manifest.json`: the tag, version, branch, the `main` commit the release was cut from, the release commit, the commit that was tried in the game, every file's path, size and SHA-256, the SHA-256 of the archive and the SBOM, and the results of the checks above.
6. Write `SHA256SUMS.txt` with the SHA-256 of the archive, the SBOM and the manifest.

## 5. Publish

```bash
git tag v<version>-beta <release commit>
git push origin v<version>-beta
gh release create v<version>-beta --prerelease --target release/<version> --title "Poser <version> beta" --notes-file <notes.md> latest.zip Poser.spdx.json release-manifest.json SHA256SUMS.txt
```

1. Download the published files and check them against `SHA256SUMS.txt`.
2. Open a pull request from `release/<version>` into `main` and squash-merge it. Say in it which issues the release closes and which checks were skipped or failed. Once merged, `repo.json` on `main` points at the new release.
3. Never rebuild or re-tag a release from `main`. The squash commit on `main` is a different commit from the release commit; the tag, the release branch and the published files are the record of the release.

## Download count

`.github/workflows/update-download-count.yml` runs every day at 06:00 UTC and can also be started by hand. It adds up the downloads of every `latest.zip` (or `Poser.zip`) attached to a published release, prereleases included. Checksum, manifest and SBOM downloads are not counted. The number is downloads, not users, and includes the downloads made to check a release.

If the number changed, the workflow opens a pull request that changes only `DownloadCount` in `repo.json`. It closes any older download-count pull request first. It then approves the waiting CI run for its pull request and turns on auto-merge, so the pull request merges once the `portable` check passes. A green workflow run means the merge is queued, not done. A download-count pull request that is still open after a day needs a look.

For this to work, GitHub Actions must be allowed to create pull requests and the repository must allow auto-merge.
