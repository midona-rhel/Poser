# IGazeService (GazeService)

**Source:** `PosingCore/Services/IGazeService.cs`, `Poser.Game/LegacyRuntime/GazeService.cs`

**Purpose:** Controls where actors look, per body part (Body / Head / Eyes),
by hooking the game's per-actor look-at loop and injecting targets into the
character's `LookAtController` every time the loop runs. Modes: `None` (game
default — no Poser override), `Forward` (a computed point ahead of the
actor), `Camera` (live camera position), `Entity` (another actor, targeted by
id). Individual participating parts can be *locked* at their current target
(freeze gaze while the camera or target moves). Based on Brio's
`ActorLookAtService`.

## Identity model (PBI-002)

Managed gaze state survives ordinary actor-list refreshes because nothing in
it is keyed by wrapper identity:

- The state map is keyed by **actor lineage** (`Guid`), not by `IActor`
  reference. `ActorManager` rebuilding wrapper instances on refresh/redraw
  cannot orphan a state.
- `GazeState.TargetLineage` stores the target actor's lineage `Guid` — never
  an `IActor` reference and never a captured native address. The target is
  resolved lineage → current actor → `GameObjectId` at each state
  transition.
- Native look-at holders remain keyed by `GameObjectId` (native truth).
- On every actor-list change the service reconciles: a source lineage that
  left the scene drops its state and holder; a target lineage that left the
  scene transitions that source's gaze to **Off** (one transition, no stale
  address is ever followed).

## Native application

- **Actor mode is id-based.** `LookAtTarget` carries the Brio/Ktisis-verified
  union at offset 0x10: the position `Vector3` overlaps a `GameObjectId`
  actor target, and `LookMode` value 1 is **Target** (id-following; the game
  tracks the object itself). Entity mode writes `LookMode.Target` plus the
  target's `GameObjectId` once per transition — the detour does not poll a
  captured address and holds no `GameObject*`. A despawned target id simply
  stops resolving inside the game's own controller.
- **Camera** writes the live camera position (`LookMode.Position`) each loop
  iteration for unlocked participating parts. **Forward** writes a computed
  point 10 units ahead and 1.5 units up from the actor's own position and
  facing — an observably different source from Camera.
- **Off** early-returns to the original function: no Poser write at all.
- A **lock** freezes one participating part at its actual current target
  position (`LookMode.Position` with the position captured at lock time). It
  does not change the mode, the participation mask, or the other parts —
  Brio's per-part `SetTargetLock` semantics. Unlocking returns the part to
  the active mode's source. Mode/part transitions never re-seed locked
  parts.
- Every UI action is **one state transition**. The detour allocates nothing
  per frame (the former per-loop `CreateObjectReference` call is gone); the
  two maps are guarded by one lock shared between the UI thread and the
  hooked game loop.

## Public API

| Member | What it does |
|---|---|
| `GetGazeState(Guid lineage)` | Gets/creates the managed state (Mode, Parts, TargetLineage). |
| `SetGazeMode(Guid lineage, GazeTargetMode)` | One transition; entering a non-Off mode with no participating parts defaults to all three; Entity mode without a resolved target writes nothing. |
| `SetGazeParts(Guid lineage, GazeTargetType)` | Changes participation only. Turning off the final active part transitions the mode to Off. |
| `SetGazeTarget(Guid lineage, Guid targetLineage)` | Resolves and applies the Entity target; rejects the source's own lineage. |
| `SetPartLock(Guid lineage, GazeTargetType part, bool locked)` | Freezes/unfreezes one participating part at its actual current target. |
| `ResetGaze(Guid lineage)` | Removes state and holder — full game default. |
| `IsPartLocked` / `IsGazeEnabled` | State queries. |

**Events:** none published. Consumes `GPoseStateChangedEvent` (exit clears all
state and holders) and `ActorListChangedEvent` (lineage reconciliation above).

**Dependencies:** Dalamud `IObjectTable`, `ISigScanner`,
`IGameInteropProvider`, `IPluginLog`; PosingCore `IGPoseService`,
`ICameraService`, `IEventBus`; the actor source used for lineage resolution.

## Game surface (WATCH — patch-sensitive)

- **Sig scan → function pointer** `_updateLookAt` (update face tracker):
  `"E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F"`,
  called as `(CharacterLookAtController*, LookAtTarget*, uint index, nint)`
  with indices Body=0, Head=1, Eyes=2.
- **Sig scan + hook** `_actorLookAtLoop`:
  `"E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48"`; the
  detour identifies the actor from `ContainerInterface->OwnerObject`, injects
  targets, then always calls the original (which runs the gaze IK).
- **Deliberately no try/catch** on these scans — if the sigs break, plugin
  load fails loudly instead of running with silently broken gaze.
- **Hand-written structs**: `LookAtSource` (Body/Head/Eyes/Unknown),
  `LookAtType` (`LookAtTarget` at offset **0x30**), `LookAtTarget`
  (`LookMode` at 0x08; **union at 0x10**: `Position` overlapped by the
  `GameObjectId` actor target — layout corroborated by Brio
  `ActorLookAtService` and Ktisis `ActorGaze`), `LookMode` enum
  (None=0 / Target=1 / Pivot=2 / Position=3).

**Brio counterpart:** `Brio/Brio/Game/Actor/ActorLookAtService.cs` — same two
signatures, same struct offsets, same union, same per-part lock concept.
Differences: Poser keys managed state by actor lineage and adds the
`Forward` computed-point mode; Brio ties look-at data to its capability
objects.

## Known risks

- Struct offsets (0x30 / 0x08 / 0x10) are unverified-by-compiler magic
  numbers; a layout change misreads without crashing.
- Fail-fast constructor means a broken sig blocks the whole plugin from
  loading (accepted trade-off).

## Verification

In-game only: camera-follow eyes; Forward visibly differs from Camera;
per-part lock (eyes locked, head free) survives mode changes; Entity
targeting follows a second actor, survives that actor's redraw, and safely
disables when the target despawns; Off restores game gaze and poseable head
bones.
