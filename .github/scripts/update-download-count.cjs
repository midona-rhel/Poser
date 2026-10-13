module.exports = async function updateDownloadCount({ github, context, core }) {
  const repo = context.repo;
  const base = context.payload.repository.default_branch;
  const releases = await github.paginate(github.rest.repos.listReleases, { ...repo, per_page: 100 });
  // Count installable archives, not the checksum, manifest, or SBOM downloads.
  const count = releases.filter(release => !release.draft)
    .flatMap(release => release.assets)
    .filter(asset => /^(latest|Poser)\.zip$/i.test(asset.name))
    .reduce((total, asset) => total + asset.download_count, 0);
  if (!Number.isSafeInteger(count) || count < 0) throw new Error('Invalid release download count');

  const { data: main } = await github.rest.git.getRef({ ...repo, ref: `heads/${base}` });
  const { data: file } = await github.rest.repos.getContent({ ...repo, path: 'repo.json', ref: main.object.sha });
  const original = Buffer.from(file.content, 'base64').toString('utf8');
  const entries = JSON.parse(original);
  if (entries.length !== 1 || entries[0].InternalName !== 'Poser')
    throw new Error('Expected a single Poser repository entry');
  core.info(`DownloadCount: ${entries[0].DownloadCount} -> ${count}`);

  // An earlier update that never merged is stale by now; replace it rather
  // than wedging every later run behind it.
  const pending = await github.paginate(github.rest.pulls.list, { ...repo, state: 'open', base, per_page: 100 });
  for (const stale of pending.filter(pr => pr.head.ref.startsWith('automation/download-count-') &&
    pr.user.login === 'github-actions[bot]')) {
    await github.rest.issues.createComment({ ...repo, issue_number: stale.number,
      body: 'Superseded by a newer download count update.' });
    await github.rest.pulls.update({ ...repo, pull_number: stale.number, state: 'closed' });
    await github.rest.git.deleteRef({ ...repo, ref: `heads/${stale.head.ref}` }).catch(() => {});
    core.info(`Closed stale ${stale.html_url}`);
  }
  if (entries[0].DownloadCount === count) return;

  // Preserve every other byte, including release versions, links and LastUpdate.
  const matches = [...original.matchAll(/("DownloadCount"\s*:\s*)\d+/g)];
  if (matches.length !== 1) throw new Error('Expected exactly one DownloadCount field');
  const updated = original.replace(/("DownloadCount"\s*:\s*)\d+/, (_, prefix) => prefix + count);
  entries[0].DownloadCount = count;
  if (JSON.stringify(JSON.parse(updated)) !== JSON.stringify(entries))
    throw new Error('Unexpected repository metadata change');

  const branch = `automation/download-count-${context.runId}-${process.env.GITHUB_RUN_ATTEMPT || '1'}`;
  await github.rest.git.createRef({ ...repo, ref: `refs/heads/${branch}`, sha: main.object.sha });
  const { data: commit } = await github.rest.repos.createOrUpdateFileContents({
    ...repo, path: 'repo.json', branch, sha: file.sha,
    message: 'Update download count', content: Buffer.from(updated).toString('base64'),
  });
  const { data: pr } = await github.rest.pulls.create({
    ...repo, base, head: branch, title: 'Update download count',
    body: `Refresh DownloadCount to ${count} from published plugin ZIP downloads, including prereleases. No version, URL, changelog, or release timestamp changes.`,
  });
  core.info(`Created ${pr.html_url}`);

  // main requires the "portable" check, but events from GITHUB_TOKEN do not
  // start pull_request workflows. A workflow_dispatch is the one event that
  // does, and its check run lands on the same head commit.
  await github.rest.actions.createWorkflowDispatch({ ...repo, workflow_id: 'architecture.yml', ref: branch });
  // Auto-merge waits for the required check; it never bypasses protections.
  await github.graphql(`mutation($id: ID!, $head: GitObjectID!) {
    enablePullRequestAutoMerge(input: { pullRequestId: $id, mergeMethod: SQUASH,
      commitHeadline: "Update download count", expectedHeadOid: $head }) { clientMutationId }
  }`, { id: pr.node_id, head: commit.commit.sha });
  await core.summary.addRaw(`Queued DownloadCount **${count}** via ${pr.html_url} (auto-merge after checks).`).write();
};
