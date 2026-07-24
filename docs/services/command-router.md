# Command router

## Purpose

`CommandRouter` is the narrow text adapter for the `/poser` command. It is not
a secondary product UI and does not expose internal services for manual
mutation.

Supported commands:

- `/poser` — toggle the main window;
- `/poser help` — print the focused command help;
- `/poser test [basic|full|scenario] [--iterations N]` — run the focused live
  acceptance harness;
- `/poser test status` — print the authoritative persisted verdict;
- `/poser test cancel` — request cancellation of the active run.

`selftest` remains only as an input alias for `test`.

## Dependencies

The router depends directly on:

- `IUIManager` for the bare toggle;
- `ILiveTestService` for live validation;
- `IChatGui` for feedback.

It must not depend on `IServiceProvider` or resolve arbitrary feature services
at runtime. Product features are reached through the main UI and application
facades. A feature that exists only as a debug subcommand is either unfinished
or out of scope and must not remain hidden in the command router.
