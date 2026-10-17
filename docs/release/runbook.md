# Release runbook

`main` is development/integration. Cut `release/<version>` from the accepted
main SHA; record that base and build only the clean release-branch head.
Never rebuild an existing release from a later main commit. Beta tags are
`v<version>-beta` (0.9.19: tag `v0.9.19-beta`, assembly version `0.9.19.0`).

## 1. Accept and cut

1. The user accepts the work and authorizes the release. Merge the feature PRs
   into main (squash), close the issues they fix and move leftovers to new issues.
2. `git switch -c release/<version> origin/main` and record that main SHA.

## 2. Release notes

The notes are written for the people who use Poser. Each bullet says what they
can now do, or what no longer goes wrong, in the words of the interface:

- One sentence, past tense, starting with what happened: Added, Fixed, Made,
  Improved, Removed, Kept. "Fixed actors that couldn't be moved after …", not
  "PosingService seeds its actor map".
- No issue numbers, class or function names, offsets, or how it was built.
  Leave out internal work that changes nothing a user sees.
- Group the bullets under `###` headings by area (Animation, Scenes and files,
  Actors and posing, Interface, Fixes …) and give the release a short theme.
- Follow the previous release's wording and order; read its notes first.

`docs/release/CHANGELOG.md` holds the source section, newest on top:

```markdown
## <version>-beta — <theme>

### <area>

- <bullet>
```

Every other place carries the same text:

| Copy | Form |
|---|---|
| `ReleaseNotesHistory` (in-app, newest first; keep older entries) | the theme and the bullets, without groups |
| `Changelog` in `Poser/Poser.json` and `repo.json` | the bullets as `- ` lines, without groups |
| GitHub release body | `## Poser <version> — <theme>`, then the groups and bullets |

## 3. Identity

On the release branch set the numeric version in `Poser/Poser.csproj`,
`Poser/Poser.json` and `repo.json`, the three `repo.json` download links to the
new tag, and `repo.json` `LastUpdate` (Unix seconds). Add
`docs/release/<version>-acceptance.md`: the authorization, the accepted main
base and live runtime, the issues closed, and every check that was skipped or
not passed (never claim an unreported test).

`ReleaseIdentityTests` fails when the versions, links, in-app notes, manifests
and changelog disagree. Commit and push the release head.

## 4. Gates (on the clean release head)

```bash
dotnet build Poser.slnx -c Release
dotnet test Poser.slnx -c Release --no-build
dotnet list Poser.slnx package --vulnerable --include-transitive
```

Debug deploys to the live game and is never a release validation. Refuse to
package from main, a dirty checkout, mismatched versions/links, or an existing tag.

- Scan the diff since the previous tag for credentials and local paths, and the
  tree against [exclusions](exclusions.md).
- The build writes `Poser/bin/Release/Poser/latest.zip`. Inspect it against the
  previous release's manifest: the same file set (explain any change), unique
  relative paths without traversal or symlinks, valid CRCs, no debug output, no
  Debug bridge or probe types in the assemblies, `Poser.json` at the new version,
  and the release head's short SHA embedded in `Poser.dll`.
- Reconcile shipped dependencies with [notices](../../THIRD-PARTY-LICENSES.md);
  the archive carries the license texts.
- Generate the SPDX SBOM with pinned Microsoft SBOM Tool 4.1.5 from the unzipped
  archive and the release checkout, validate it against those files, and keep it
  outside the archive:

  ```bash
  sbom-tool generate -b <unzipped archive> -bc . -pn Poser -pv <version> -ps Midona -nsb https://github.com/midona-rhel/Poser
  ```

- Write `release-manifest.json`: tag, version, branch, accepted main, release
  commit, accepted runtime commit, each file's path/size/SHA-256, the archive and
  SBOM SHA-256, and the validation results above. `SHA256SUMS.txt` lists the
  archive, SBOM and manifest.

## 5. Publish

```bash
git tag v<version>-beta <release head>
git push origin v<version>-beta
gh release create v<version>-beta --prerelease --target release/<version> --title "Poser <version> beta" --notes-file <body.md> latest.zip Poser.spdx.json release-manifest.json SHA256SUMS.txt
```

Download the published assets and check their SHA-256 against `SHA256SUMS.txt`.
Then open a PR from `release/<version>` into main and squash-merge it, so
`repo.json` advertises the published asset. Main's squash commit differs from
the release commit; the tag, branch and artifacts keep the release identity.
Do not rebuild or retag from main. Record the release URL and checks.

## Download count

The download-count workflow runs daily at 06:00 UTC and can be dispatched manually.
Like Brio's daily refresh, it sums release download counters; Poser counts only
plugin ZIP assets across published releases, including prereleases, not checksums
or SBOMs. This measures downloads, not unique users, and includes verification downloads.
It updates only `repo.json`'s `DownloadCount` through a squash-merged bot PR, leaving
release identity, URLs and `LastUpdate` untouched. No rebuild or retag is involved.
Actions must be allowed to create PRs and the repository must allow auto-merge;
branch protection remains in force. Bot PRs cannot trigger `pull_request` workflows,
so the job dispatches the architecture workflow on its branch to produce the required
`portable` check, then queues squash auto-merge. A green job therefore means *queued*,
not merged: an update that fails its check stays open, and the next run closes it as
superseded and opens a fresh one. An open count PR older than a day needs a look.
