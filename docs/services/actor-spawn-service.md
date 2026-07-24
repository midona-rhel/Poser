# IActorSpawnService (ActorSpawnService)

**Source:** `PosingCore/Services/IActorSpawnService.cs`, `Poser.Game/LegacyRuntime/ActorSpawnService.cs`

**Purpose:** Spawns clones of the local player as new GPose actors and destroys them again, tracking which object-table indices it owns so it never deletes game-owned actors. Also provides draw-visibility toggling for any actor. All spawned actors are force-destroyed on GPose exit and on dispose. Directly modeled on Brio's `ActorSpawnService`.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `SpawnPlayerClone` | `IActor? SpawnPlayerClone()` | Creates a battle character via `ClientObjectManager`, copies appearance/position from the local player (object-table index 0), names it "Poser One/Two/…", registers it with the GPose controller, enables draw, refreshes `IActorManager`, returns the matching `IActor` (null on any failure). |
| `DestroyActor` | `bool DestroyActor(IActor actor)` | Deletes the object by index — refuses actors not in `_spawnedIndexes`. Refreshes the actor list. |
| `SetVisibility` | `void SetVisibility(IActor, bool)` | Calls `EnableDraw()`/`DisableDraw()` on the native object; records an override. |
| `IsVisible` | `bool IsVisible(IActor)` | Returns the recorded override, else `IsReadyToDraw()`; defaults to `true` on native failure. |
| `IsSpawnedActor` | `bool IsSpawnedActor(IActor)` | True if the actor's `ClientObjectManager` index is in the spawned set. |
| `Dispose` | `void Dispose()` | Unsubscribes and destroys all spawned actors. |

**Events:**
- **Published:** none directly (actor-list changes surface via `IActorManager.RefreshActors()` → `ActorListChangedEvent`).
- **Consumed:** `GPoseStateChangedEvent` — on exit, `DestroyAllSpawned()`.

**Dependencies:**
- Dalamud: `IClientState` (unused beyond injection), `IObjectTable` (local-player address), `IPluginLog`.
- PosingCore: `IGPoseService`, `IActorManager`, `IEventBus`.

**Game surface:** No hooks or sig scans of its own — everything goes through FFXIVClientStructs, so breakage arrives as ClientStructs API/struct changes after a patch:
- `ClientObjectManager.Instance()`: `CreateBattleCharacter(0)` (0xFFFFFFFF = failure), `GetObjectByIndex`, `GetIndexByObject`, `DeleteObjectByIndex(idx, 0)`.
- `Character.CharacterSetup.CopyFromCharacter(...)` with `CopyFlags.WeaponHiding | CopyFlags.Position`, then a second self-copy with `CopyFlags.None` to trigger Penumbra-style redraws.
- `GameObject`: direct writes to `Position`, `Rotation`, `DefaultPosition`, `DefaultRotation`, raw byte writes into the 64-byte `Name` array, `EnableDraw()`, `DisableDraw()`, `IsReadyToDraw()`.
- `EventFramework.Instance()->EventSceneModule.EventGPoseController.AddCharacterToGPose(character)`.

**Brio counterpart:** `Brio/Brio/Game/Actor/ActorSpawnService.cs`. Same spawn recipe (CreateBattleCharacter → copy → AddCharacterToGPose → EnableDraw), including the double `CopyFromCharacter` redraw trick. Brio additionally supports spawn options (companion slots, disabled attachments), spawn-as-new-actor from appearance files, and uses its own naming ("Brio One"). PosingCore only clones the local player.

**Known risks:**
- `SetName` casts `char` → `byte` (ASCII only); non-ASCII player-facing names would mangle, though current names are generated.
- After spawn, the actor is located by comparing `_actorManager.Actors` addresses to the new object — if the actor list refresh misses it that frame, `SpawnPlayerClone` returns null while the native object still exists (it is tracked in `_spawnedIndexes`, so cleanup still works).
- `_visibilityOverrides` is keyed by address and only cleared on destroy/exit; a game-side despawn leaves stale entries.
- Broad `catch (Exception)` around native calls logs and continues — a partially constructed character could linger if `CreateBattleCharacter` succeeds but later steps throw.

**Test coverage:** Name generation (`ToPoserName`) and spawned-index bookkeeping are headless-testable. Everything else (spawn, copy-appearance, GPose registration, draw toggling, destroy) is in-game-only (docs/process/in-game-verification.md) — verify after each patch that a clone spawns, is posable, and disappears on GPose exit.


## Companions/mounts/ornaments (Phase B, 2026-07-18)
`SetCompanion(owner, CompanionAttachment)` / `DestroyCompanion` /
`GetCompanionInfo` — native attach via `CompanionData.SetupCompanion`,
`Mount.CreateAndSetupMount`, `OrnamentData.SetupOrnament`; draw-readiness waits
on a BOUNDED per-frame poll with a 1s timeout + log (no blind tick delays).
**Bug fixed:** `SpawnPlayerClone` now passes `CreateBattleCharacter(param: 1)`
to reserve the companion slot — the old `0` meant clones could never host
minions/mounts. Costs one extra object slot per clone.
