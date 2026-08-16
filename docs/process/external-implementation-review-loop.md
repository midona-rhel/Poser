# External implementation and review loop

## Purpose

This process lets an external implementation agent deliver one Product Backlog
Item while Codex reviews the result and the user remains the authority for
in-game UI and behavior. It prevents implementation, review, and unrelated
working-tree changes from becoming indistinguishable.

The loop is sequential. Claude and Codex do not edit the same checkout at the
same time.

## Roles

| Role | Responsibility |
|---|---|
| User | Chooses the PBI, supplies in-game observations, and accepts the UI. |
| Claude | Implements only the PBI on its feature branch and reports the exact commit range. |
| Codex | Reviews the PBI diff against its recorded base, checks architecture and reference behavior, and reports actionable findings. |

Codex does not claim visual correctness. The user inspects the running plugin.

## Entry gate

Implementation starts from a clean baseline identified by an immutable annotated
Git tag. The PBI records:

```text
Base ref: pbi-<id>-base
Feature branch: feature/pbi-<id>-<short-name>
```

The implementer resolves and reports the full commit with:

```powershell
$baseRef = '<exact Base ref value from the PBI control table>'
git rev-parse --verify "$baseRef^{commit}"
```

The tag avoids the impossible requirement for a versioned PBI to contain the
hash of the commit that contains that same PBI. The tag must never be moved
after implementation starts.

The current Poser reset must be reviewed and committed before the first
external implementation begins. Do not use a stash as the long-lived baseline:
stashes hide ownership and make later review fragile.

Claude must stop before editing if:

- `BASE_REF` is blank, missing, or does not resolve to a commit;
- `git status --short` is non-empty before the feature branch is created;
- the checked-out branch or merge base does not match the PBI;
- another agent is actively editing the same checkout.

## Branch and commit rules

1. Create the feature branch from the exact recorded base tag.
2. Never use `git reset --hard`, `git clean`, or checkout-based file
   restoration.
3. Never use `git add -A`. Stage explicit PBI-owned paths.
4. Preserve unrelated changes. If an owned file already changed unexpectedly,
   stop and report the overlap.
5. Prefer reviewable commits that each leave the project compiling:
   documentation/contract, application state, runtime adapter, UI wiring, then
   cleanup.
6. Do not amend or rebase commits after review starts. Address findings with
   new commits so each review round has an exact range.
7. Do not merge the branch until user acceptance.

## Implementation handoff

Claude reports:

```text
PBI:
Base commit:
Head commit:
Commits:
Changed paths:
Behavior implemented:
Architecture/docs added or changed:
Production build:
In-game checks still required:
Known deviations or open questions:
```

The handoff must distinguish completed behavior from behavior inferred only
from compilation. It must not claim that the UI looks correct.

## Review round

Codex reviews `BASE_REF..HEAD` and checks:

1. every acceptance criterion and explicit exclusion in the PBI;
2. Brio native-ordering requirements and Ktisis interaction requirements;
3. dependency direction and stable-identity boundaries;
4. use of the retained Poser/Picto UI primitives;
5. gesture atomicity, rollback, undo/redo, and invalidation behavior;
6. dead compatibility paths and duplicated state;
7. documentation accuracy;
8. production compilation.

Findings are reported by severity with an exact file and tight line range.
Questions are not disguised as defects. If the PBI itself is ambiguous, Codex
updates the PBI decision before Claude changes code.

## Fix round

Claude addresses accepted findings in new commits and reports the new range:

```text
PREVIOUS_REVIEW_HEAD..NEW_HEAD
```

Codex first reviews that range, then rechecks the complete
`BASE_REF..NEW_HEAD` diff for integration regressions. This repeats until
there are no blocking findings.

## In-game acceptance

The user reloads the plugin and follows the PBI checklist. UI feedback is
reported as observed behavior, expected behavior, active selection, and the
interaction that produced it. A screenshot is optional evidence supplied by
the user; it is never an automated acceptance oracle.

Native behavior uses the existing `/poser test` commands only when the PBI
requires them. Claude and Codex do not introduce npm, browser, screenshot,
pixel-diff, standalone UI, or generic unit-test harnesses.

## Completion

After user acceptance:

1. Claude adds a final fix commit if required.
2. Codex performs the final full-range review.
3. The PBI records the accepted head commit and any deliberately deferred work.
4. The branch is merged using the repository owner's preferred strategy.
5. The PBI becomes historical evidence; unfinished work becomes a new PBI
   instead of silently expanding the completed one.
