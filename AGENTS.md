# Working on Poser

These are the rules for changing Poser: its code, its documentation, its issues and its pull requests. They apply to people and to AI agents alike.

## Write so anyone can read it

Everything written for this project must be readable by someone who has never opened the code: issues, pull requests, commit messages, documentation, code comments and names in the code.

- Use short, ordinary sentences. One idea in each.
- Say what a person does and what they then see. "Press Apply. The animation plays."
- Use the words the interface shows. If the tab is called Full body, write Full body.
- Do not use a word that only the code uses. If you need one, say in the same sentence what it means.
- Describe how things are now. Leave out how they used to be and how the work went.
- Cut what the reader does not need.
- Say the idea first, in one or two plain sentences, the way you would say it to someone. Details come after. For example: "An MCDF is never supposed to be edited: it is an appearance that never changes. A scene is a project you save, load and keep working on, so saving and loading a scene whose actors' appearance you own shouldn't lock anything."

If a line reads like notes someone wrote for themselves, rewrite it.

## Nothing personal

Never write anything about the owner or the owner's own work. This applies to issues, pull requests, comments, release notes, documentation, code, tests and commit messages.

- No names of the owner's characters, or of any character in their game.
- No names of their scenes, poses, MCDFs, library files or Penumbra collections, and nothing describing what is in their scenes or screenshots.
- No local paths that contain a user name.

Call actors "Actor 1", "Actor 2" and so on, in texts and in test data. Describe what was tested in neutral words: "a test scene with three actors", "Actor 2 had an MCDF applied". Read every text for this before you post or commit it.

## Code

- **One way to do each thing.** Before writing something, look for the code that already does it and use that. When two pieces of code do the same job, keep one and delete the other.
- **Three parts.** The interface (`Poser.UI`) draws and passes on what was pressed. Poser's own logic (`Poser.Application`) decides what happens. The game code (`Poser.Game`) reads and writes the game. Behaviour does not belong in the interface.
- **Names.** A name says what the thing is in plain words. Do not invent vocabulary.
- **Checks.** Only check for something that can really happen and that matters when it does. Before keeping a check, find the action that leads to it.
  - Do not guard against things a person cannot do, or would only do on purpose. Someone holding a drag cannot press Reset. Undo exists.
  - Do not catch errors around code that does not throw.
  - Do not write a fallback for a value that is always there.
  - Keep checks for things outside Poser's control: an actor that left, a body being redrawn, a file that is missing, a game function that moved.
- **Comments.** A comment says why the code is there, in plain words. Do not write a comment that repeats the code, tells the story of a change, or needs another file open to make sense. Put it next to the decision it explains.
- **Interface.** Build panes from the shared controls in `Poser.UI/Widgets`, `Poser.UI/Controls` and `Poser.UI/Compositions`. Take sizes and spacing from the theme, not from numbers in a pane. Report a failed action through `UserNotices.Report`, under the name of the control that was used.
- **Tests.** Add a test only when it protects behaviour someone depends on. More tests is not better coverage. The limits are in [testing](docs/process/testing.md#unit-test-budget-and-relevance).
- **Brio and Ktisis.** They do the same job as Poser. Look at how Brio talks to the game and how Ktisis lets a person pose before deciding how Poser should.

## Leave a file better than you found it

When you change a file, bring the whole file in line with these rules in the same change, not only the lines you came for:

- rewrite comments that are wrong, out of date or hard to read;
- remove checks that have no reason to exist;
- remove code nothing uses;
- fix names that say the wrong thing.

## Documentation

- The pages under `docs/features` are for people who use Poser. The pages under `docs/architecture` and `docs/process` are for people who change it. [docs/README.md](docs/README.md) lists them.
- Each thing is explained in one place. Other pages link to it.
- Write down what is true and worth knowing. Do not write a page per class.
- Plans, audits, notes and records of what was tested do not go in the repository. Work is tracked in GitHub issues. What was tested goes in the pull request.
- Delete text that is no longer true. Git keeps the history.

## Issues and pull requests

- Start with what goes wrong, then what should happen. End with how to check it.
- Link the code that shows the problem, as a GitHub link to a published commit and its lines. Say in a few words what the reader will find there.
- Say whether something was seen in the game, read in the code, or is a guess.
- Leave out line counts, model names and the story of the session.
- Use the [issue template](.github/ISSUE_TEMPLATE/work_item.md).

## Commits and branches

- The first line says in plain words what changed, with the issue number in brackets when there is one. No `fix:` or `docs:` prefixes. One change per commit.
- Name a branch after what it is for: `feature/`, `bug/`, `ui/`, `docs/`, `test/`, `perf/`, `chore/` or `release/`.
- Work is merged into `main`. A release is built from its own `release/<version>` branch. The steps are in [the release runbook](docs/release/runbook.md).

## Building and trying a change

- Build Release to check that the code compiles and the tests pass.
- Build Debug only to try a change in the game. The game loads Poser from the Debug output of the main checkout, so a Debug build there replaces what is running. The steps are in [testing](docs/process/testing.md).
- Try every change in the game before calling it done. A passing build is not proof that it works.

## For AI agents

- Do the work in the session you are in. Hand part of it to another agent only when asked to. If asked and no model is named, use Sol (`gpt-5.6-sol`).
- Do not merge a pull request or publish a release unless you were asked to.
- When something needs trying by hand, give the exact steps: where to start, what to press, and what should be seen.
