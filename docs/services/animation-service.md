# IAnimationService (AnimationService)

**Source:** `PosingCore/Services/IAnimationService.cs`, `Poser.Game/LegacyRuntime/AnimationService.cs`

**Purpose:** Controls actor animation playback and physics: per-actor freeze (speed 0) and arbitrary speed override via a hook on the game's speed calculation; **global** physics freeze (hair/cloth/breast) via NOP-patching game code, the Anamnesis/Brio technique; animation scrubbing (duration / current time / set time) by reading and writing havok animation controls; and base/blend animation overrides (`Timeline.BaseOverride` + `TimelineSequencer.PlayTimeline`) with original-state restoration. Everything resets on GPose exit.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `IsFrozen` / `Freeze` / `Unfreeze` / `ToggleFreeze` | `…(IActor)` | Freeze = speed override 0. Unfreeze removes the override, restores speed 1, and also lifts the global physics freeze. Publishes `FreezeStateChangedEvent`. |
| `IsPhysicsFrozen` / `FreezePhysics` / `UnfreezePhysics` / `TogglePhysicsFreeze` | `…(IActor)` | Global code-patch toggle (the `IActor` parameter is ignored for state — physics freeze affects all actors). `FreezePhysics` also freezes that actor's animation. Publishes `PhysicsFreezeStateChangedEvent`. |
| `GetSpeed` / `SetSpeed` / `ResetSpeed` | `float GetSpeed(IActor)` etc. | Speed override map + immediate write of `Timeline.OverallSpeed` and every havok `AnimationControl.PlaybackSpeed` (the latter fixes breathing continuing during freeze). |
| `GetAnimationDuration` | `float? GetAnimationDuration(IActor)` | `Duration` of the binding's animation on partial 0, control 0; null at any missing link. |
| `GetAnimationTime` | `float? GetAnimationTime(IActor)` | `hkaAnimationControl.LocalTime` of partial 0, control 0. |
| `SetAnimationTime` | `void SetAnimationTime(IActor, float)` | Writes `LocalTime` on **all** controls of all partials (consistent scrub). |
| `ApplyBaseAnimation` | `void ApplyBaseAnimation(IActor, ushort timelineId, bool interrupt)` | Saves original `(Mode, ModeParam, BaseOverride)` once, then `SetMode(AnimLock, 0)` + `Timeline.BaseOverride = id`; optional immediate blend to interrupt. |
| `StopBaseAnimation` | `void StopBaseAnimation(IActor)` | Restores saved mode/param/override, plays blend 3 (idle). |
| `HasBaseOverride` / `GetCurrentBaseAnimation` | `…(IActor)` | Override map check / `BaseOverride` else `TimelineSequencer.TimelineIds[0]`. |
| `PlayBlendAnimation` | `void PlayBlendAnimation(IActor, ushort)` | `Timeline.TimelineSequencer.PlayTimeline(id)` on top of the current animation. |
| `Dispose` | `void Dispose()` | Unhooks, unsubscribes, `ResetAllState()` (restores speeds and physics bytes). |

**Events:**
- **Published:** `FreezeStateChangedEvent(IActor, bool)` (Freeze/Unfreeze), `PhysicsFreezeStateChangedEvent(bool)` (physics toggle, incl. the implicit unfreeze inside `Unfreeze`).
- **Consumed:** `GPoseStateChangedEvent` — on exit, `ResetAllState()` (all speeds → 1, physics unpatch). Note `PosingService` consumes the `FreezeStateChangedEvent` published here.

**Dependencies:**
- Dalamud: `IFramework` (fallback reapply loop only if the speed hook fails), `ISigScanner`, `IGameInteropProvider`, `IPluginLog`, `Dalamud.Memory.MemoryHelper` (raw read/write + `ChangePermission`).
- PosingCore: `IGPoseService`, `IEventBus`.

**Game surface (WATCH — patch-sensitive):**
- **Sig scan + hook** `CalculateAndApplyOverallSpeed`: `"E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83"` — detour overwrites `TimelineContainer->OverallSpeed` with the per-actor override after the original runs.
- **Code patch** (not a hook): physics freeze sig `"0F 11 48 10 41 0F 10 44 24 ?? 0F 11 40 20 48 8B 46 28"` (lineage: Anamnesis → Brio). Enabling writes 4 NOPs at the address and 3 NOPs at address − 0x9 (`PhysicsFreezePatchOffset`), saving original bytes for restore; uses `MemoryHelper.ChangePermission(ExecuteReadWrite)`.
- **Native structs:** `Character.Timeline` (`OverallSpeed`, `BaseOverride`, `TimelineSequencer.{TimelineIds, PlayTimeline}`), `Character.{Mode, ModeParam, SetMode(CharacterModes.AnimLock, 0)}`, `TimelineContainer->OwnerObject`, and havok: `CharacterBase → Skeleton → PartialSkeletons[i].GetHavokAnimatedSkeleton(0) → AnimationControls[j] → {PlaybackSpeed, hkaAnimationControl.LocalTime, Binding→Animation→Duration}`.

**Brio counterpart:** `Brio/Brio/Game/Actor/ActionTimelineService.cs` (speed hook, base/blend overrides, scrubbing) and `Brio/Brio/Game/Posing/PhysicsService.cs` (the same two-site NOP patch). PosingCore merges both into one service and adds the freeze/physics cross-coupling policy (freezing physics freezes the actor; unfreezing the actor lifts the physics freeze). Brio keeps them independent and exposes richer timeline slot control (per-slot speeds).

**Known risks:**
- Runtime **code patching** is the most invasive technique in PosingCore: if the sig matches a shifted location after a patch, NOPs corrupt live code. The hardcoded `-0x9` second patch site doubles the fragility. Failure to *find* the sig is handled (feature disabled); finding the *wrong* address is not detectable.
- Scrubbing reads duration/time only from partial 0 / control 0 but writes all controls — mixed-length controls will disagree with the reported duration.
- `Freeze`'s dictionary access pattern (`!ContainsKey || [addr] != 0f`) and `_speedOverrides` being `float?` (nullable never actually null except by the fallback loop's benefit) are harmless but confusing.
- Base override restore writes `Mode`/`ModeParam` directly instead of `SetMode` — relies on direct field writes being safe for restoration.
- Speed overrides keyed by address; stale entries after actor despawn are written into reused memory until GPose exit.

**Test coverage:** Freeze/physics state-machine policy and event publication are headless-testable only with the native write path stubbed; as written every mutation touches `Character*`. In-game-only (docs/process/in-game-verification.md): after each patch verify the speed hook resolves, freeze stops motion *including breathing*, physics freeze stops hair/cloth (and restores cleanly — check bytes by toggling twice), scrubbing moves the pose, and base animation override + stop restores idle.
