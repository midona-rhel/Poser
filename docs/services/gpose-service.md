# IGPoseService (GPoseService)

**Source:** `PosingCore/Services/IGPoseService.cs`, `Poser.Game/LegacyRuntime/GPoseService.cs`

**Purpose:** Detects whether the client is in GPose and broadcasts transitions. It is the root lifecycle signal for the entire game-hook layer: nearly every other service subscribes to `GPoseStateChangedEvent` to initialize state on entry and tear down overrides (actors, skeletons, poses, lights, cameras, time/weather freezes) on exit. Detection is a simple per-frame poll of Dalamud's `IClientState.IsGPosing` with edge-detection — no hooks of its own.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `IsGPosing` | `bool IsGPosing { get; }` | Live pass-through of `IClientState.IsGPosing`. |
| `Dispose` | `void Dispose()` | Unsubscribes from `IFramework.Update`. |

**Events:**
- **Published:** `GPoseStateChangedEvent(bool IsGPosing)` — on the framework tick where the polled state differs from the last observed state (both entry and exit).
- **Consumed:** none.

**Dependencies:**
- Dalamud: `IClientState` (state source), `IFramework` (per-frame poll).
- PosingCore: `IEventBus`.

**Game surface:** None directly — no hooks, no sig scans, no native structs. Relies entirely on Dalamud's `IClientState.IsGPosing`. Effectively immune to game patches (Dalamud absorbs the breakage).

**Brio counterpart:** `Brio/Brio/Game/GPose/GPoseService.cs`. Brio's version is far larger: it hooks `EnterGPose`/`ExitGPose` UI events, supports "fake GPose" and exclusive-GPose input handling, and exposes C# events. PosingCore deliberately keeps only the boolean + event bus signal; entry/exit side effects live in the subscribing services instead of here.

**Known risks:**
- Edge detection is one frame late relative to the actual transition (poll-based). Services that need actors immediately on entry compensate themselves (see `ActorManager`'s deferred refresh).
- `_lastGPoseState` starts `false`; if the plugin loads while already in GPose, the first tick publishes an entry event — acceptable, but worth knowing.

**Test coverage:** Fully headless-testable: mock `IClientState`/`IFramework`/`IEventBus`, flip `IsGPosing`, assert one event per transition. Nothing requires in-game verification (docs/process/in-game-verification.md).
