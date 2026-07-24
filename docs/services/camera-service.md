# ICameraService (CameraService)

**Source:** `PosingCore/Services/ICameraService.cs`, `Poser.Game/LegacyRuntime/CameraService.cs`

**Purpose:** Stateless read-only math facade over the game's active camera: view/projection matrices, camera world position, and the projection helpers every overlay and gizmo depends on — `WorldToScreen` (Ktisis-style: rejects only behind-camera points, not off-screen ones, so gizmo lines can extend past the viewport), `ScreenToWorld` at a chosen depth (used by light placement), and camera-to-point distance. No hooks, no events, no caching — every call reads the live camera.

**Public API:**

| Member | Signature | What it does |
|---|---|---|
| `GetViewMatrix` | `Matrix4x4 GetViewMatrix()` | `SceneCamera.ViewMatrix` with `M44` forced to 1; `Identity` if no camera. |
| `GetProjectionMatrix` | `Matrix4x4 GetProjectionMatrix()` | `RenderCamera.ProjectionMatrix` with `M33`/`M43` recomputed from near/far planes (standard clip-range fix-up); `Identity` if unavailable. |
| `GetCameraPosition` | `Vector3 GetCameraPosition()` | `SceneCamera.Position`; `Zero` if unavailable. |
| `WorldToScreen` | `bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)` | Projects through view × raw projection into pixel coordinates using `ImGui.GetIO().DisplaySize`; returns false only when `w <= 0.001` (behind camera). |
| `ScreenToWorld` | `Vector3 ScreenToWorld(Vector2 screenPos, float depth)` | Unprojects NDC through the inverted view-projection to a ray, returns the point at `depth` along it; `Zero` on any degenerate case. |
| `GetDepthToPosition` | `float GetDepthToPosition(Vector3 worldPos)` | Euclidean distance from camera position. |

**Events:** none published or consumed.

**Dependencies:**
- Dalamud: none injected (uses `Dalamud.Bindings.ImGui.ImGui.GetIO()` statically for display size).
- PosingCore: none.
- FFXIVClientStructs: `CameraManager` (see below).

**Game surface:** No hooks or sig scans. Reads FFXIVClientStructs statics/structs each call:
- `CameraManager.Instance() → GetActiveCamera()`
- `Camera.CameraBase.SceneCamera.{ViewMatrix, Position, RenderCamera}`
- `RenderCamera.{ProjectionMatrix, NearPlane, FarPlane}`

Patch exposure is entirely via ClientStructs field layout for these camera structs — a bump that moves `ViewMatrix`/`RenderCamera` breaks every overlay at once (high-visibility failure, easy to spot).

**Reference behavior:** Brio separates projection helpers from its camera-update
hook. Poser retains projection only; virtual camera capture/control was removed
with the deferred camera workflow. The behind-camera rejection follows Ktisis,
so off-screen points can still produce clipped bone-connector lines at the
viewport edge.

**Known risks:**
- Every method must run on the main/framework thread (raw pointer reads + `ImGui.GetIO()`); nothing enforces this.
- `M44 = 1` and the `M33`/`M43` projection rewrites encode assumptions about how the game stores its matrices (reversed-infinite depth); if the engine changes projection conventions, results skew subtly rather than failing.
- `ScreenToWorld` returns `Vector3.Zero` for failure — a legitimate world position could also be Zero, and `LightingService` explicitly treats Zero as failure (documented coupling).
- ImGui display size assumes the game viewport equals the ImGui display (true under Dalamud, but a hidden assumption).

**Test coverage:** Every entry point dereferences `CameraManager.Instance()`, so
the service is accepted by live camera scenarios. They confirm that bone
overlay dots land on joints, the gizmo follows the camera, and light placement
tracks the cursor at correct depth.
