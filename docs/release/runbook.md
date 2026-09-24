# Release runbook

`main` is development/integration. Cut `release/<version>` from the accepted
main SHA; record that base and build only the clean release-branch head.
Never rebuild an existing release from a later main commit.

## Identity and gates

1. Set numeric versions in `Poser/Poser.csproj`, `Poser/Poser.json` and
   root `repo.json` on the release branch. Update the changelog and all three
   download URLs to the exact new tag. Beta tags use `v<version>-beta`;
   for 0.9.6 this is `v0.9.6-beta`, assembly version `0.9.6.0`.
2. Commit and push the release head. Refuse packaging from main, a dirty
   checkout, mismatched versions/URLs, or an existing mismatched tag.
3. Build and test that head with `dotnet build Poser.slnx -c Release` and
   `dotnet test Poser.slnx -c Release --no-build`. Debug is live deployment
   and is never a release validation substitute.
4. Review tracked files and release history for credentials, diagnostics,
   local paths and excluded data using [exclusions](exclusions.md).
5. Inspect the produced ZIP independently: canonical unique relative paths,
   no traversal/symlinks, valid CRCs, exact expected files, no debug output,
   correct assembly/manifest version and build SHA, and matching file hashes.
6. Run the online vulnerability audit against the resolved assets graph.
   Reconcile shipped dependencies against [notices](../../THIRD-PARTY-LICENSES.md).
   Carry required license texts in the archive.
7. Generate an SPDX SBOM with pinned Microsoft SBOM Tool 4.1.5 from the staged
   archive contents and the release checkout. Validate it against those files.
   Keep it outside the archive. Record the tag, branch, accepted main base,
   release SHA, per-file hashes, archive checksum and SBOM checksum in the
   release manifest.

## Publication

Tag the exact verified release head and create the GitHub prerelease targeting
its release branch. Upload the verified ZIP, checksum, SBOM and release
manifest. Verify the published asset digests and download.
Squash-merge the release pull request into main using the repository's configured
policy so `repo.json` advertises the published asset. Main's squash commit is
expected to differ from the release commit; it does not need to preserve that
commit's ancestry. The tag, release branch, build and published artifacts retain
their original release identity. Do not rebuild or retag them from main.
Record the release URL and checks. Never claim unreported visual tests passed.

## Download count

The download-count workflow runs daily at 06:00 UTC and can be dispatched manually.
Like Brio's daily refresh, it sums release download counters; Poser counts only
plugin ZIP assets across published releases, including prereleases, not checksums
or SBOMs. This measures downloads, not unique users, and includes verification downloads.
It updates only `repo.json`'s `DownloadCount` through a squash-merged bot PR, leaving
release identity, URLs and `LastUpdate` untouched. No rebuild or retag is involved.
Actions must be allowed to create PRs; branch protection remains in force. If a PR
cannot merge, the job fails with its link and avoids creating duplicate open PRs.
