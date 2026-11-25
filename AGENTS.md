# Working on Poser

These are the rules for anyone who changes Poser, people and AI agents alike. They cover the code, the documentation, commit messages, issues, pull requests and release notes.

## Write so anyone can read it

Everything written for this project must make sense to someone who has never opened the code and has never seen the work that led to it.

- Say the idea first, in one or two plain sentences, the way you would say it to someone. Details come after. For example: "An MCDF is never supposed to be edited: it is an appearance that never changes. A scene is a project you save, load and keep working on, so saving and loading a scene whose actors' appearance you own shouldn't lock anything."
- Write the way a person would explain it out loud: whole sentences that join related facts, not a string of fragments.
- Use the words the interface shows. If the tab is called Full body, write Full body. Call bones by the names the bone list shows, such as Neck or Cervical.
- Do not use a word that only the code uses. If you need one, say in the same sentence what it means.
- Say where things are, relative to something the reader can see: "the left side of the main window", "the toolbar at the bottom of the screen".
- Describe how things are now. Leave out how they used to be and how the work went.
- Leave out what the reader does not need, including changes nobody would notice.
- Write plainly. No slogans, no clever phrasing, nothing written to impress.

Read every text back before you post or commit it. If a line reads like notes someone wrote for themselves, rewrite it.

## Nothing personal

Poser is public. Nothing written for it may identify a person or show what is in their game.

- No character names, whether from the game, a scene, a screenshot or a message. This includes the names Poser shows for actors, because those are character names.
- No names of anyone's scenes, poses, MCDFs, library files or Penumbra collections, and nothing describing what is in their scenes or screenshots.
- No names of people who reported something, and no quotes from Discord or other chats. Describe the problem instead: "Mirroring does nothing when the right eye is selected."
- No local paths that contain a user name, and no e-mail addresses.

Call actors "Actor 1", "Actor 2" and so on, in texts, screenshots and test data. Describe what was tested in neutral words: "a test scene with three actors", "Actor 2 had an MCDF applied". This applies to issues, pull requests, release notes, documentation, code, tests and commit messages.

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
- A feature page walks through the interface in the order a person meets it. Check every statement about what happens in the code or in the game before writing it.
- Crop screenshots to the part of the window being described. Never show the game world or anything personal.
- Write each paragraph and each list item on one line. Do not break a line inside a paragraph; the editor wraps it.

## Issues and pull requests

- Use the [issue template](.github/ISSUE_TEMPLATE/work_item.md) and the [pull request template](.github/PULL_REQUEST_TEMPLATE.md), and fill in every section.
- Start with what goes wrong for the person using Poser, then what should happen. End with how to check it.
- Link the code that shows the problem, as a GitHub link to a published commit and its lines. Say in a few words what the reader will find there.
- Say whether something was seen in the game, read in the code, or is a guess. Say what was not tried.
- Give the steps to try it: where to start, what to press, and what should be seen.
- Report results as sentences a person can follow. Leave out line counts, model names and the story of the session.

## Commits and branches

- The first line says in plain words what changed, with the issue number in brackets when there is one. No `fix:` or `docs:` prefixes. One change per commit.
- Name a branch after what it is for: `feature/`, `bug/`, `ui/`, `docs/`, `test/`, `perf/`, `chore/` or `release/`.
- Work is merged into `main`. A release is built from its own `release/<version>` branch. The steps and the rules for release notes are in [the release runbook](docs/release/runbook.md).

## Building and trying a change

- Build Release to check that the code compiles and the tests pass.
- Build Debug only to try a change in the game. The game loads Poser from the Debug output of the main checkout, so a Debug build there replaces what is running. The steps are in [testing](docs/process/testing.md).
- Try every change in the game before calling it done. A passing build is not proof that it works.

## For AI agents

- Do the work in the session you are in. Hand part of it to another agent only when asked to. If asked and no model is named, use Sol (`gpt-5.6-sol`).
- Do not merge a pull request or publish a release unless you were asked to.
