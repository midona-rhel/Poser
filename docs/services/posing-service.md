# IPosingService (PosingService)

**Source:** `PosingCore/Services/IPosingService.cs`, `Poser.Game/LegacyRuntime/PosingService.cs`

**Purpose:** Applies whole-actor (model) transform overrides — position, rotation, scale of the entire draw object, as opposed to per-bone posing (`IBonePosingService`). Overrides are enforced two ways: a hook on the game's `GameObject.SetPosition` swallows the game's own reset attempts while an override exists, and a per-frame framework-update pass rewrites every override into the draw object as a belt-and-braces backup. Originals are remembered and restored on explicit clear or GPose exit. Animation freeze/playback state is deliberately independent from the model transform, matching Brio's `ModelPosingCapability` and Ktisis' actor `ITransform` target.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `GetTransformOverride` | `Transform? GetTransformOverride(IActor)` | The stored override, or null. |
| `SetTransformOverride` | `void SetTransformOverride(IActor, Transform)` | Stores the original (first time only), records the override, applies it immediately. |
| `SetPosition` / `SetRotation` / `SetScale` | `void Set…(IActor, …)` | Component-wise setters: read `GetEffectiveTransform`, replace one component, call `SetTransformOverride`. |
| `GetOriginalTransform` | `Transform GetOriginalTransform(IActor)` | Stored pre-override transform, else a live read from the game. |
| `GetEffectiveTransform` | `Transform GetEffectiveTransform(IActor)` | Override if present, else live game read. |
| `ClearTransformOverride` | `void ClearTransformOverride(IActor)` | Removes the override and re-applies the stored original. |
| `ClearAllOverrides` | `void ClearAllOverrides()` | Restores all originals, clears both maps. |
| `HasTransformOverride` | `bool HasTransformOverride(IActor)` | Membership test. |
| `Dispose` | `void Dispose()` | Disposes hook, unsubscribes, `ClearAllOverrides()`. |

**Events:**
- **Published:** none.
- **Consumed:** `GPoseStateChangedEvent` — on exit, `ClearAllOverrides()`.

**Dependencies:**
- Dalamud: `IPluginLog`, `IFramework`, `IGameInteropProvider`.
- PosingCore: `IGPoseService`, `IEventBus`.

**Game surface (WATCH — patch-sensitive):**
- **Hook** on `GameObject.SetPosition` resolved from **FFXIVClientStructs' address registry** (`StructsGameObject.Addresses.SetPosition.Value`) — no local sig scan, so a ClientStructs bump after a patch is the update vector. The detour *does not call the original* when an override exists for that object while in GPose.
- **Native struct writes:** `GameObject → DrawObject → Object.{Position, Rotation, Scale}` (draw object, not the game object — visual only); reads of `GameObject.Position`/`.Rotation` as fallback when no draw object exists.
- Hook failure at construction is caught; the per-frame reapply loop still enforces overrides without it.

**Brio counterpart:** `Brio/Brio/Game/Posing/ModelTransformService.cs` (model transform read/write) — Brio splits "read/write draw-object transform" from its posing capabilities and also hooks position resets. PosingCore folds override storage, the reset-blocking hook, and the per-frame enforcement into one service. Behavior matches Brio's approach of writing to the draw object so the game's server-side position is untouched.

**Known risks:**
- The per-frame reapply loop runs regardless of GPose state (`OnFrameworkUpdate` has no `IsGPosing` guard); overrides are cleared on GPose exit so the window is small, but an override set outside GPose would be enforced globally.
- Keyed by raw actor address; a despawned actor's address in `_transformOverrides` means writes into freed/reused memory until GPose exit clears the map (mitigated by null-checking `DrawObject`, not by validating the object).
- Blocking the original `SetPosition` entirely (rather than calling it and re-overriding) may skip game-side bookkeeping that `SetPosition` normally performs.
- `Transform` is a struct — `SetPosition/SetRotation/SetScale` mutate a local copy then store it; correct today, but the read-modify-write on `GetEffectiveTransform` makes concurrent modification from hooks racy in theory.

**Test coverage:** Override bookkeeping (store-original-once, effective-vs-original resolution, explicit-clear semantics) is headless-testable with the game-read path faked. The hook, draw-object writes, reset-blocking behavior, and independence from animation playback are in-game-only (docs/process/in-game-verification.md): after each patch verify a moved actor stays put while animation is playing, remains moved across freeze/unfreeze, and snaps back only on explicit reset or GPose exit.
