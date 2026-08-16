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
  for the exact reviewed head when the user is ready to test in game.
- The organizer does not author or repair production code. Luna worktree tasks
  author every repository implementation and every accepted review fix. The
  organizer writes specifications, controls scope and ownership, reviews exact
  diffs, runs the authoritative Release build/test gates, triages findings, and
  manages deployment and acceptance. Implementation tasks need not run broad
  builds or tests unless the specification delegates a narrow diagnostic.
- Only one Luna implementation task edits a shared subsystem at a time. Luna
  review tasks remain independent and read-only, and send their final report
  directly to the organizer task. Build, test, or review failures go back to the
  implementation task; the organizer does not patch them.
- Every ongoing update and final handoff starts with a short, concrete TL;DR.
  When user testing is required, include an exact actionable test card with
  starting state, actions, expected result, and what evidence to report.
- Every Luna task must explicitly send its complete ongoing blocker or final
  report to the organizer task with the task-messaging tool before ending.
  A final answer left only in the child task is insufficient.
