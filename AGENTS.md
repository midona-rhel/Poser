# Agent rules

- Documentation is required for **durable concepts and non-obvious
  invariants** — product boundaries, runtime ordering, identity/gesture
  contracts, compatibility conventions. Do NOT create a document per class,
  interface, service, or entity; member tables, constructor lists, and
  implementation flow belong to source and search. Before adding a concept,
  consult `docs/README.md` and extend the existing normative home; create a
  new file only when no home fits, and keep it around 10–40 lines.
- One normative home per contract; other documents link instead of
  restating. Delete superseded prose — Git preserves history.
- Consult Brio for native/backend behavior and Ktisis for posing
  interaction (Brio: robust backend, horrid UI; Ktisis: the reverse). Keep a
  reference citation in docs only when it explains an intentional
  compatibility decision.
- Unsafe offsets, native ordering, and surprising math are explained in
  tight comments beside the code, not in documentation essays.
- A **Debug build auto-deploys the plugin to the live game**. Never run Debug
  merely to check compilation, tests, or fault injection. Use Release for
  non-deployment validation. Run Debug only as the announced deployment action
  for the exact reviewed head before handing the issue to the user for testing.
  Follow [the deployment and acceptance contract](docs/process/testing.md):
  the user is the in-game reviewer, no video verification is required, and
  a worktree-local build is not deployment to the canonical live output.
- Work directly in the current task by default: implement, review, validate,
  and handle user feedback here. Do not spawn subtasks or delegate unless the
  user explicitly asks. Track work as GitHub issues, not PBIs.
- If the user requests delegation, prefer Sol (`gpt-5.6-sol`) unless they
  choose another model. Delegate bounded work; keep scope, review, deployment,
  acceptance, and repository finalization in the main task. Only one writer
  edits a shared subsystem at a time.
- Write concise, natural updates; do not require a TL;DR prefix or rigid template.
  Historical plans are reference material, not current agent instructions.
  When user testing is required, include an exact actionable test card with
  starting state, actions, expected result, and what evidence to report.
- Explicitly requested delegated tasks must send their blocker or final report
  back to the main task before ending; include the exact head, patch, checks,
  and outstanding work.
- Announce **deployed — ready for user testing** only after verified deployment,
  with the user's actionable test card. An undeployed build is never test-ready.
- `main` is the development and integration branch. Cut `release/<version>`
  from an accepted `main` commit; build, test, package, tag, and publish the
  exact release-branch head. Squash-merge release fixes and metadata back to
  `main` through a pull request, as configured in the repository. Main's squash
  commit need not preserve the release commit's ancestry; never rebuild a cut
  release from a later `main` commit.
- Name work branches and worktrees by purpose: `feature/`, `bug/`, `ui/`,
  `docs/`, `test/`, `perf/`, `release/`, or `chore/` as appropriate. Never
  use a `codex/` branch prefix or a generic Codex work name.
