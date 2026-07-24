# IActorManager (ActorManager)

**Source:** `PosingCore/Services/IActorManager.cs`, `Poser.Game/LegacyRuntime/ActorManager.cs`

**Purpose:** Tracks the lifecycle of actors visible in GPose — discovery, refresh, and teardown. It scans the Dalamud object table's GPose slot range (indices 201–439) each frame, rebuilds its `IActor` list when the set of native addresses changes, and clears everything on GPose exit. Selection is explicitly out of scope (that is the application `SelectionSession`); this service is purely "what actors exist right now".

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `Actors` | `IReadOnlyList<IActor> Actors { get; }` | Current GPose actor list (read-only wrapper over the internal list). |
| `RefreshActors` | `void RefreshActors()` | Disposes and rebuilds the actor list from the object table, then publishes `ActorListChangedEvent`. |
| `GetGPoseTarget` | `IActor? GetGPoseTarget()` | Maps `ITargetManager.GPoseTarget` (the orbit focus) to a tracked `IActor` by address. |
| `Dispose` | `void Dispose()` | Unsubscribes, disposes all actors, publishes a final empty-list event. |

**Events:**
- **Published:** `ActorListChangedEvent(IReadOnlyList<IActor>)` — after every `RefreshActors()` and after `ClearActors()` (GPose exit, dispose).
- **Consumed:** `GPoseStateChangedEvent` — on entry sets a pending-refresh flag (processed on the *next* framework tick so actors have time to initialize); on exit clears the list.

**Dependencies:**
- Dalamud: `IObjectTable` (slot iteration), `ITargetManager` (GPose target), `IFramework` (per-frame change detection).
- PosingCore: `IGPoseService`, `IEventBus`.

**Game surface:** No hooks or sig scans. Two soft dependencies on game layout via Dalamud:
- The GPose object-table slot range constants `GPoseStart = 201` / `GPoseEnd = 439`. If Square Enix resizes the object table or Dalamud renumbers it, discovery silently finds nothing.
- Actor identity is the raw `IGameObject.Address` (`nint`), stored in `ActorBase` and compared as a `HashSet<nint>` per frame.

**Brio counterpart:** `Brio/Brio/Entities/EntityActorManager.cs` + `Brio/Brio/Game/Core/ObjectMonitorService.cs`. Brio attaches/detaches individual actor entities into a persistent entity graph in response to object-table events; PosingCore does a full rebuild whenever the address set differs. Rebuild is simpler and avoids Brio's incremental-sync edge cases, at the cost of new `ActorBase` instances (and new `EntityId`s derived from `GameObjectId`) on every change — consumers must not hold `IActor` references across `ActorListChangedEvent` (see `GazeService` risk).

**Known risks:**
- `ToActorKind` maps `Mount`, `Ornament`, and `Retainer` kinds, but `GetGPoseCharacters()` filters to `Pc/BattleNpc/EventNpc/Companion/Retainer` only — mounts and ornaments are never yielded, so part of the mapping is dead code and mounts don't appear as actors.
- Full-rebuild refresh disposes `ActorBase` instances that other services may still hold (skeletons are re-fetched by address so `SkeletonService` survives, but reference-keyed dictionaries elsewhere go stale).
- Per-frame `HashSet` allocation in `GetGPoseCharacterAddresses()` (GC churn, ~240 slot iteration every tick while in GPose).

**Test coverage:** Change-detection logic, actor-kind mapping, and event sequencing are headless-testable with a mocked `IObjectTable`. The actual slot range (201–439 really containing GPose copies) and name/index formatting against live actors need in-game verification (docs/process/in-game-verification.md).
