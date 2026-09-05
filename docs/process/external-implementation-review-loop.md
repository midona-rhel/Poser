# Issue implementation and review

Work directly in the current task by default. The same task owns research,
implementation, diff review, validation, deployment, and feedback. Track work
as GitHub issues, not PBIs. Subtasks are opt-in at the user's request.

## Scope and ownership

Start from the issue's evidence, reproduction, and acceptance criteria.
Record the base commit and use a purpose-named branch. Inspect the working
tree before editing and preserve unrelated changes; do not require a clean
checkout by discarding or hiding them. Stage only issue-owned changes.

Consult Brio for native behavior and Ktisis for posing interaction. Review
the actual patch for identity, runtime ordering, product scope, and existing
UI conventions. Keep durable contracts in their existing documentation home.

## Validation and acceptance

Follow the [testing and deployment contract](testing.md): Release is the
non-deployment gate; Debug deploys to the live game. Explicit user restrictions
on builds or deployment override the normal workflow. Do not claim runtime
correctness from source review or compilation alone.

After verified deployment, provide the exact starting state, actions, and
expected result. Reload is automatic; the user reviews the running game.
No video verification is required. Fix reported problems in the current task.
Merge and close the issue only after acceptance; record deliberately deferred
work without claiming it was fixed.

## Optional delegation

Only when the user requests it, give a task a bounded specification and an
exact base. Prefer Sol unless the user chooses otherwise. It authors its
patch and reports checks and blockers back; the main task retains review,
deployment, acceptance, and finalization. Never assign concurrent writers
to the same subsystem.
