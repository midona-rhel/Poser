# ConfigurationService

`PosingCore/Config/ConfigurationService.cs` plus the retained configuration
POCOs in `PosingCore/Config/`: `PoserConfiguration` (root,
`IPluginConfiguration`, `Version = 1`), `SkeletonConfiguration`,
`DisplayConfiguration`, `UIConfiguration`, and `UIColorEntry`.

## Purpose

Loads, holds, and persists the plugin configuration through Dalamud's `GetPluginConfig`/`SavePluginConfig`. Also provides per-section reset helpers and "anonymous mode" display-name substitution (stable random 5-char names per `EntityId`, for streaming/screenshots).

Configuration sections:

| Section | Contents |
|---|---|
| `Skeleton` | Overlay sizes (dot radius, line thickness/opacity, octahedra width) and colors (uint ABGR: bone, outline, selected, modified, hovered), `ShowSkeletonLines` |
| `Display` | `ShowNsfwBones`, `AnonymousMode` |
| `UI` | `UIColorEntry` per UI role (background, text, border, selection, title bar, buttons) — each either a custom `Vector4` or a live reference to an `ImGuiCol` theme slot, resolved via `Resolve()`/`ResolveU32()` |

## Public API

| Member | Signature | Notes |
|---|---|---|
| `Config` | `PoserConfiguration` | The live config object; sections mutated in place |
| `Instance` | `static ConfigurationService` | Set in constructor — service-locator escape hatch |
| `Save` | `void` | `SavePluginConfig` + fires `OnConfigurationChanged` |
| `ApplyChange` | `void (bool save = true)` | Optional save, fires `OnConfigurationChanged` |
| `Reset` / `ResetSkeleton` / `ResetDisplay` / `ResetUI` | `void` | Replace root or one section with defaults |
| `GetDisplayName` | `string (IEntity)` | Real name, or stable anonymous name when `Display.AnonymousMode` |

## Events

**Published:** plain C# event `OnConfigurationChanged` (not EventBus).

**Consumed:** none.

## Dependencies

`IDalamudPluginInterface` (persistence). `UIConfiguration`/`UIColorEntry` reference ImGui types (`ImGuiCol`, `ImGui.GetStyle()` at resolve time).

## Brio counterpart

`Brio/Brio/Config/ConfigurationService.cs` + `Configuration.cs` and per-domain configs (`PosingConfiguration`, `InterfaceConfiguration`, `AppearanceConfiguration`, …).

Differences:
- Same skeleton: `Instance` static, `Save`/`ApplyChange`/`Reset`, `OnConfigurationChanged`, ctor loads via `GetPluginConfig`. Poser adds per-section resets and anonymous mode; Brio adds theme bootstrapping, config-folder helpers, and repo URL constants.
- **Brio's `Save()` does not fire the changed event** (only `ApplyChange` does). Poser's `Save()` fires it, and `ApplyChange(save: true)` therefore fires it **twice** (once inside `Save`, once after).
- Brio's `Configuration` carries a schema version with migration expectations; Poser has `Version = 1` and no migration path yet.

## Known risks

- **Double event fire** from `ApplyChange(true)` (see above) — any expensive `OnConfigurationChanged` subscriber runs twice per change.
- `Instance` static singleton invites hidden dependencies; DI consumers and `Instance` users can disagree during teardown.
- `Dispose()` saves unconditionally — a crash-during-shutdown path can persist half-mutated config.
- No version migration: any future breaking change to a section needs a `Version` bump plus migration code that does not exist yet.
- `_anonymousNames` grows for the session and is never pruned; names are not stable across sessions (fine for its purpose, worth knowing).
- `UIColorEntry.Resolve()` reads `ImGui.GetStyle()` — safe only on the UI thread inside a frame.

## Verification

Configuration changes require a production build. Persistence, anonymous-name
behavior, reset behavior, and theme-slot resolution are inspected in the
running plugin when a change touches them. They are not part of the focused
posing live-test catalog unless a named scenario is added explicitly.
