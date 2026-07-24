# IGazeService (GazeService)

**Source:** `PosingCore/Services/IGazeService.cs`, `Poser.Game/LegacyRuntime/GazeService.cs`

**Purpose:** Controls where actors look, per body part (Body / Head / Eyes flags), by hooking the game's per-actor look-at loop and injecting target positions into the character's `LookAtController` every time the loop runs. Modes: `None` (game default), `Forward` (a computed point 10 units ahead of the actor), `Camera` (live camera position each frame), `Entity` (another actor's position). Individual parts can be *locked* at a fixed world position (freeze gaze while the camera moves), and gaze can be disabled entirely so head/eye bones follow bone posing instead. Based on Brio's `ActorLookAtService`.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `GetGazeState` | `GazeState GetGazeState(IActor)` | Gets/creates the managed-side state (Mode, TargetType, TargetEntity). |
| `SetGazeMode` | `void SetGazeMode(IActor, GazeTargetMode)` | Sets mode and re-applies. |
| `SetGazeTargetType` | `void SetGazeTargetType(IActor, GazeTargetType)` | Sets affected parts and re-applies. |
| `SetGazeTarget` | `void SetGazeTarget(IActor, IActor target)` | Sets entity target, switches mode to `Entity`. |
| `ResetGaze` | `void ResetGaze(IActor)` | Removes state and the native look-at handle — full game default. |
| `SetGazeState` | `void SetGazeState(IActor, GazeState)` | Replaces the whole state (clone-in); used for undo/history. |
| `LockGaze` | `void LockGaze(IActor, GazeTargetType = All)` | Freezes the given parts at the *current camera position*; forces mode to `Camera` so the detour applies the locked positions. Publishes `GazeLockChangedEvent(actor, true)`. |
| `SetTargetLock` | `void SetTargetLock(IActor, bool doLock, GazeTargetType, Vector3 position)` | Brio-style per-part lock/unlock at an explicit position; auto-enables Camera mode if gaze was off. |
| `DisableGaze` | `void DisableGaze(IActor)` | Mode/type → None, all locks off, `LookMode.None` on all parts; detour passes through to the game. Publishes `GazeLockChangedEvent(actor, false)`. |
| `UnlockGaze` | `void UnlockGaze(IActor)` | Clears all part locks, restores `LookMode.Position` (camera tracking resumes). |
| `IsGazeLocked` / `IsPartLocked` | `bool …(IActor[, GazeTargetType])` | Lock queries against the native handle. |
| `IsGazeEnabled` | `bool IsGazeEnabled(IActor)` | Handle exists and mode != None. |
| `EnableGaze` | `void EnableGaze(IActor)` | Initializes Camera mode / All parts (Brio's `StartLookAt`), syncs the managed `GazeState` too. |
| `Dispose` | `void Dispose()` (via `IDisposable` on the class; the interface itself does not extend `IDisposable`) | Disposes the hook, unsubscribes. |

**Events:**
- **Published:** `GazeLockChangedEvent(IActor, bool)` — from `LockGaze` (true), `DisableGaze`/`UnlockGaze` (false).
- **Consumed:** `GPoseStateChangedEvent` — on exit, clears all gaze states and look-at handles.

**Dependencies:**
- Dalamud: `IObjectTable` (`CreateObjectReference` for address→GameObjectId mapping), `ISigScanner`, `IGameInteropProvider`, `IPluginLog`.
- PosingCore: `IGPoseService`, `ICameraService` (camera position each application), `IEventBus`.

**Game surface (WATCH — patch-sensitive):**
- **Sig scan → function pointer** `_updateLookAt` (update face tracker): `"E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F"`, called as `(CharacterLookAtController*, LookAtTarget*, uint index, nint)` with indices Body=0, Head=1, Eyes=2.
- **Sig scan + hook** `_actorLookAtLoop`: `"E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48"`, detour reads `ContainerInterface->OwnerObject` to identify the actor, injects targets, then always calls the original (which runs the gaze IK).
- **Deliberately no try/catch** on these scans — if the sigs break, plugin load fails loudly instead of running with silently broken gaze.
- **Hand-written structs**: `LookAtSource` (Body/Head/Eyes/Unknown), `LookAtType` (`LookAtTarget` at offset **0x30**), `LookAtTarget` (`LookMode` at 0x08, `Position` at 0x10), `LookMode` enum (None/Frozen/Pivot/Position). Native access: `((Character*)addr)->LookAt.Controller`, `GameObject.Position/Rotation`.

**Brio counterpart:** `Brio/Brio/Game/Actor/ActorLookAtService.cs` — same two signatures, same struct offsets, same copy-to-local-`LookAtSource`-before-use trick, same per-part lock concept (`SetTargetLock`). Differences: PosingCore adds the explicit `DisableGaze`/`EnableGaze` pair and the `GazeTargetMode.Forward` computed-point mode, and keys native handles by `GameObjectId` while keeping a separate UI-facing `GazeState` map. Brio ties look-at data to its capability objects instead.

**Known risks:**
- `_gazeStates` is keyed by `IActor` **reference**. `ActorManager.RefreshActors()` rebuilds actors as new instances, so managed gaze state silently orphans after any actor-list change; the native `_lookAtHandles` (keyed by `GameObjectId`) survive, so behavior persists but the UI-visible `GetGazeState` resets. Real desync hazard.
- Detour work per actor per frame includes `IObjectTable.CreateObjectReference` (allocation) — runs inside a hot game loop.
- Struct offsets (0x30 / 0x08 / 0x10) are unverified-by-compiler magic numbers; a layout change misreads without crashing.
- Fail-fast constructor means a broken sig blocks the whole plugin from loading (accepted trade-off, but different from every other service here).

**Test coverage:** Managed-state transitions (`GazeState`, mode/type/lock flag logic) are headless-testable only if the `IObjectTable`/native handle path is faked; as written almost every method dereferences game memory or needs `CreateObjectReference`. Treat the service as in-game-only (docs/process/in-game-verification.md): after each patch verify camera-follow eyes, per-part lock (eyes locked, head free), entity targeting, and that `DisableGaze` lets head bones be posed.
