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
- Follow the unit-test budget and relevance rules in
  [testing](docs/process/testing.md#unit-test-budget-and-relevance). Increasing
  the passing test count is not evidence of better coverage.
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

## Writing

Write everything — issues, PRs, commits, docs and code comments — for Midona
and other people using Poser, in short, ordinary sentences. If it reads like a
lab report or one agent talking to another, rewrite it. The detailed rules
live in [issue implementation and review](docs/process/external-implementation-review-loop.md#writing-issues-and-pull-requests).

- **Issues and PRs.** Start with what goes wrong for the user, then what
  should happen instead. Link the code that shows the problem with GitHub
  permalinks to a published commit and the relevant lines, each with a short
  label saying what the reader will find there. End with a short way to check
  it. Say plainly whether something was reproduced, read in the source, or is
  a guess. Leave out line counts, reviewer or model names, deployment status
  and the story of the session. Use the
  [work-item template](.github/ISSUE_TEMPLATE/work_item.md).
- **Commits.** Author `midona-rhel <midona.rhel@gmail.com>`, with no Claude
  co-author or session lines. The subject says in plain words what changed
  ("Release scene-save appearance packages on every exit"), with the issue
  number in parentheses when there is one. No `refactor:`/`docs:` prefixes.
  Keep each commit to one change, and record live checks in their own commit.
- **Code comments.** Say what the code is for and what it returns, in the
  words a user would use: "undo step", "redraw", "bone IDs", not
  "re-keyed footprint" or "ordering barrier". Put an explanation beside the
  decision it explains, once. Mention which thread code runs on when it
  matters. No capitals for emphasis, no investigation notes, no comments that
  only say where a behavior does *not* happen. A search for a behavior should
  land on its implementation.
- **Code.** Prefer one shared implementation over parallel copies, and delete
  the copy rather than leaving it beside its replacement. Before keeping a
  guard, trace who can actually reach it; remove branches that only protect
  states production code cannot create, and keep checks for real external
  failures. Tests use the production path, and new tests follow the
  [unit-test budget](docs/process/testing.md#unit-test-budget-and-relevance).
- **Docs.** Update the existing normative home in plain language and delete
  superseded prose. Feature pages describe what the user sees, not class
  inventories. Dated evidence and live-check records go in `docs/validation/`.
