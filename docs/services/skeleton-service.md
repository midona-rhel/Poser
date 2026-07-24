# ISkeletonService (SkeletonService)

**Source:** `PosingCore/Services/ISkeletonService.cs`, `Poser.Game/LegacyRuntime/SkeletonService.cs`

**Purpose:** Lazily creates and caches one `Skeleton` entity per actor (keyed by the actor's native address) and manages its lifetime. The service itself contains no native code — all game-memory reading (partial skeletons, havok poses, bone graphs) lives in the `Skeleton`/`Bone` entities it constructs. On successful creation the skeleton is attached as a child of the actor in the entity tree. The whole cache is dropped on GPose exit.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `GetSkeleton` | `ISkeleton? GetSkeleton(IActor actor)` | Returns the cached skeleton for the actor's address, or constructs a new `Skeleton(actor)`, validates it (`IsValid`), caches it, attaches it to the `ActorBase`, and returns it. Null if the actor has no address or construction fails. |
| `RefreshSkeleton` | `void RefreshSkeleton(IActor actor)` | Calls `Skeleton.Refresh()` on the cached instance (re-reads bones after e.g. gear changes). No-op if not cached. |
| `ClearAll` | `void ClearAll()` | Disposes and drops every cached skeleton. |
| `Dispose` | `void Dispose()` | Unsubscribes and `ClearAll()`. |

**Events:**
- **Published:** none.
- **Consumed:** `GPoseStateChangedEvent` — on exit, `ClearAll()`.

**Dependencies:**
- Dalamud: `IPluginLog`.
- PosingCore: `IGPoseService` (injected; only used via the event subscription), `IEventBus`, and the `Skeleton` entity (which is where the unsafe code lives).

**Game surface:** Indirect. The service holds raw actor addresses as dictionary keys and delegates to the `Skeleton` entity, which walks `Character → DrawObject → CharacterBase → Skeleton → PartialSkeletons[n].GetHavokPose(0)` via FFXIVClientStructs. Patch breakage surfaces inside `Skeleton`, not here; this file itself has no unsafe code, hooks, or sig scans.

**Brio counterpart:** `Brio/Brio/Game/Posing/SkeletonService.cs` — but only partially. Brio's SkeletonService is a monolith that owns skeleton caching *and* the pose-application hooks (`UpdateBonePhysics`, finalize). PosingCore splits that: caching here, hooks and transform application in `BonePosingService` (see `bone-posing-service.md`). The split keeps this class trivially small and testable.

**Known risks:**
- Cache is keyed by native address. If the game reuses an address for a different actor within one GPose session (despawn + spawn), `GetSkeleton` returns a stale skeleton built for the old actor. `ActorManager`'s full-rebuild does not invalidate this cache; only GPose exit does.
- There is no eviction when an actor despawns mid-session — disposed/invalid skeletons linger until exit.
- `RefreshSkeleton` silently does nothing for uncached actors (callers may expect creation).

**Test coverage:** Cache behavior (create-once, clear-on-exit, refresh routing) is headless-testable if `Skeleton` construction is abstracted or faked; as written, `new Skeleton(actor)` reads game memory, so any test that reaches creation is in-game-only (docs/process/in-game-verification.md). Post-patch check: skeleton bone counts look sane for a player actor and refresh survives a gear change.
