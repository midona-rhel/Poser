using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Poser.Domain.Posing;

namespace Poser.Game.Posing;

/// <summary>One persistent, headless physics world for one collision-enabled IK chain.</summary>
internal sealed class BepuIkCollisionState : IIkCollisionState
{
    private readonly BufferPool _pool = new();
    private Simulation? _simulation;
    private BodyHandle[] _links = [];
    private float[] _lengths = [];
    private (int Bone, ConstraintHandle Constraint, Vector3 Offset)[] _pins = [];
    private (IkCollider Collider, BodyHandle Body, TypedIndex Shape, Vector3 Center)[] _obstacles = [];
    private float _radius;
    private Vector3? _down;
    private int _handle;
    private Vector3[] _targets = [];
    private readonly Dictionary<int, RotationFrame> _rotationFrames = [];
    private readonly record struct RotationFrame(Vector3 Source, Vector3 Direction, Quaternion Authored, Quaternion Swing);
    private const float Dt = 1f / 240;
    private const int Steps = 4;

    public void Solve(Vector3[] positions, int handle, IReadOnlyList<ColliderGeometry> colliders,
        float radius, Vector3? down, IReadOnlyList<Vector3> restPose)
    {
        if (positions.Length < 2 || colliders.Count == 0) { Reset(); return; }
        radius = MathF.Max(.0001f, radius);
        var lengths = Enumerable.Range(0, positions.Length - 1)
            .Select(i => Vector3.Distance(restPose[i], restPose[i + 1])).ToArray();
        if (_simulation == null || _handle != handle || _down != down ||
            _lengths.Length != lengths.Length || lengths.Where((v, i) => MathF.Abs(v - _lengths[i]) > .0001f).Any())
            Initialize(restPose, lengths, handle, radius, down);
        var simulation = _simulation!;
        if (_radius != radius)
        {
            for (int i = 0; i < _links.Length; i++)
            {
                var body = simulation.Bodies[_links[i]];
                var old = body.Collidable.Shape;
                var capsule = new Capsule(radius, MathF.Max(.00001f, _lengths[i]));
                body.SetShape(simulation.Shapes.Add(capsule));
                body.SetLocalInertia(capsule.ComputeInertia(MathF.Max(.0001f, _lengths[i])));
                simulation.Shapes.RemoveAndDispose(old, _pool);
            }
            _radius = radius;
        }
        UpdateObstacles(colliders);

        // Fixed substeps preserve contacts while dragging. Never rebuild the
        // chain from the obstacle-free FABRIK/catenary result on the next frame.
        for (int step = 0; step < Steps; step++)
        {
            for (int i = 0; i < _pins.Length; i++)
            {
                var pin = _pins[i];
                if (_targets[i] == positions[pin.Bone]) continue;
                var target = Vector3.Lerp(_targets[i], positions[pin.Bone], (step + 1f) / Steps);
                simulation.Solver.ApplyDescription(pin.Constraint, Pin(pin.Offset, target, pin.Bone == handle));
            }
            for (int i = 0; i < _obstacles.Length; i++)
            {
                var obstacle = _obstacles[i];
                var body = simulation.Bodies[obstacle.Body];
                var transform = colliders[i].Description.Transform;
                var target = transform.Position + Vector3.Transform(obstacle.Center, transform.Rotation);
                var delta = target - body.Pose.Position;
                // Move through the requested transform with CCD, rather than
                // teleporting or letting the physics shape trail its overlay.
                float remainingTime = (Steps - step) * Dt;
                body.Velocity.Linear = delta / remainingTime;
                var rotation = Quaternion.Normalize(transform.Rotation * Quaternion.Conjugate(body.Pose.Orientation));
                if (rotation.W < 0) rotation = new(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
                var axis = new Vector3(rotation.X, rotation.Y, rotation.Z);
                if (delta.LengthSquared() > 1e-12f || axis.LengthSquared() > 1e-12f) body.Awake = true;
                float angle = 2 * MathF.Atan2(axis.Length(), rotation.W);
                body.Velocity.Angular = axis.LengthSquared() > 1e-12f
                    ? Vector3.Normalize(axis) * angle / remainingTime : Vector3.Zero;
            }
            simulation.Timestep(Dt);
        }
        for (int i = 0; i < _pins.Length; i++) _targets[i] = positions[_pins[i].Bone];
        ReadPositions(positions);
    }

    private void Initialize(IReadOnlyList<Vector3> source, float[] lengths, int handle, float radius, Vector3? down)
    {
        Reset();
        _lengths = lengths;
        _radius = radius;
        _down = down;
        _handle = handle;
        // This world owns at most one 50-link chain, not a game's worth of bodies.
        // Capacities can grow for additional scene colliders.
        _simulation = Simulation.Create(_pool, new Contacts(), new Integrator(down ?? Vector3.Zero),
            new SolveDescription(4, 4), initialAllocationSizes: new SimulationAllocationSizes
            {
                Bodies = 64, Statics = 1, Islands = 8, ShapesPerType = 64,
                Constraints = 128, ConstraintsPerTypeBatch = 64, ConstraintCountPerBodyEstimate = 4,
            });
        _links = new BodyHandle[lengths.Length];
        for (int i = 0; i < _links.Length; i++)
        {
            var shape = new Capsule(radius, MathF.Max(.00001f, lengths[i]));
            var direction = source[i + 1] - source[i];
            QuaternionEx.GetQuaternionBetweenNormalizedVectors(Vector3.UnitY,
                direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : Vector3.UnitY, out var orientation);
            _links[i] = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose((source[i] + source[i + 1]) * .5f, orientation), shape.ComputeInertia(MathF.Max(.0001f, lengths[i])),
                new CollidableDescription(_simulation.Shapes.Add(shape), radius, ContinuousDetection.Continuous()), new BodyActivityDescription(.000001f)));
            if (i > 0)
                _simulation.Solver.Add(_links[i - 1], _links[i], new BallSocket
                {
                    LocalOffsetA = Vector3.UnitY * lengths[i - 1] * .5f,
                    LocalOffsetB = -Vector3.UnitY * lengths[i] * .5f,
                    SpringSettings = new SpringSettings(360, 1),
                });
        }
        _pins = new[] { 0, handle, source.Count - 1 }.Distinct().Select(bone =>
        {
            int link = Math.Min(bone, _links.Length - 1);
            var offset = Vector3.UnitY * lengths[link] * (bone == source.Count - 1 ? .5f : -.5f);
            var constraint = _simulation.Solver.Add(_links[link], Pin(offset, source[bone], bone == handle));
            return (bone, constraint, offset);
        }).ToArray();
        _targets = _pins.Select(p => source[p.Bone]).ToArray();
    }

    private static OneBodyLinearServo Pin(Vector3 offset, Vector3 target, bool handle) => new()
    {
        LocalOffset = offset, Target = target,
        SpringSettings = new SpringSettings(300, 1),
        // A limited handle yields when the wrapped chain is taut, rather than
        // forcing its rigid links through an obstacle or pulling joints apart.
        ServoSettings = new ServoSettings(4, 0, handle ? 200 : 10000),
    };

    private void UpdateObstacles(IReadOnlyList<ColliderGeometry> colliders)
    {
        bool rebuild = _obstacles.Length != colliders.Count || _obstacles.Where((o, i) =>
            o.Collider.Shape != colliders[i].Description.Shape ||
            !ReferenceEquals(o.Collider.Mesh, colliders[i].Description.Mesh) ||
            o.Collider.Transform.Scale != colliders[i].Description.Transform.Scale).Any();
        if (!rebuild) return;
        foreach (var obstacle in _obstacles)
        {
            _simulation!.Bodies.Remove(obstacle.Body);
            _simulation.Shapes.RemoveAndDispose(obstacle.Shape, _pool);
        }
        _obstacles = colliders.Select(geometry =>
        {
            var collider = geometry.Description;
            var transform = collider.Transform;
            var scale = Vector3.Max(Vector3.Abs(transform.Scale), new Vector3(.0001f));
            Vector3 center = default;
            TypedIndex shape;
            if (collider.Shape == IkColliderShape.Mesh && collider.Mesh is { } captured)
            {
                // Mesh is concave, never a ConvexHull. Both windings let thin
                // clothing surfaces collide from either side; Bepu triangles
                // otherwise generate contacts on only their front face.
                _pool.Take<Triangle>(captured.Indices.Length / 3 * 2, out var triangles);
                for (int i = 0; i < captured.Indices.Length; i += 3)
                {
                    var a = captured.Vertices[captured.Indices[i]];
                    var b = captured.Vertices[captured.Indices[i + 1]];
                    var c = captured.Vertices[captured.Indices[i + 2]];
                    triangles[i / 3 * 2] = new Triangle(a, b, c);
                    triangles[i / 3 * 2 + 1] = new Triangle(a, c, b);
                }
                shape = _simulation!.Shapes.Add(new Mesh(triangles, transform.Scale, _pool));
            }
            else if (collider.Shape is IkColliderShape.Box or IkColliderShape.Plane)
                shape = _simulation!.Shapes.Add(new Box(scale.X, collider.Shape == IkColliderShape.Plane ? .0002f : scale.Y, scale.Z));
            else if (collider.Shape == IkColliderShape.Cylinder && MathF.Abs(scale.X - scale.Z) < .0001f)
                shape = _simulation!.Shapes.Add(new Cylinder(scale.X * .5f, scale.Y));
            else
            {
                var local = new ColliderGeometry(collider with { Transform = new(Vector3.Zero, Quaternion.Identity, transform.Scale) });
                shape = _simulation!.Shapes.Add(new ConvexHull(local.Vertices.AsSpan(), _pool, out center));
            }
            var body = _simulation.Bodies.Add(BodyDescription.CreateKinematic(
                new RigidPose(transform.Position + Vector3.Transform(center, transform.Rotation), transform.Rotation),
                new CollidableDescription(shape, ContinuousDetection.Continuous()), new BodyActivityDescription(.000001f)));
            return (collider, body, shape, center);
        }).ToArray();
    }

    private void ReadPositions(Vector3[] positions)
    {
        for (int i = 0; i < _links.Length; i++)
        {
            var pose = _simulation!.Bodies[_links[i]].Pose;
            var half = Vector3.Transform(Vector3.UnitY * _lengths[i] * .5f, pose.Orientation);
            positions[i] = pose.Position - half;
            if (i == _links.Length - 1) positions[i + 1] = pose.Position + half;
        }
    }

    public Quaternion ResolveRotation(int link, Vector3 authoredDirection, Vector3 solvedDirection, Quaternion authoredRotation)
    {
        if (authoredDirection.LengthSquared() < 1e-12f || solvedDirection.LengthSquared() < 1e-12f)
            return authoredRotation;
        var source = Vector3.Normalize(authoredDirection);
        var direction = Vector3.Normalize(solvedDirection);
        Quaternion swing;
        if (link > 0 && _rotationFrames.TryGetValue(link - 1, out var parent))
        {
            // Transport along THIS solved chain, not each link's independent path
            // through time: separate temporal frames accumulate different axial
            // rolls after a bend travels around an obstacle (geometric holonomy).
            // Remove the authored bend before applying the solved bend; the bone's
            // authored relative roll remains in authoredRotation.
            var authoredBend = Turn(parent.Source, source, parent.Authored);
            var solvedBend = Turn(parent.Direction, direction, parent.Swing * parent.Authored);
            swing = Quaternion.Normalize(solvedBend * parent.Swing * Quaternion.Conjugate(authoredBend));
        }
        else
        {
            var prior = _rotationFrames.TryGetValue(link, out var frame)
                ? frame : new RotationFrame(source, source, authoredRotation, Quaternion.Identity);
            // Only the root frame continues through time so folding past 180 degrees
            // stays continuous. All following links share that same roll reference.
            swing = Quaternion.Normalize(Turn(prior.Direction, direction, prior.Swing * authoredRotation) * prior.Swing);
        }
        _rotationFrames[link] = new(source, direction, authoredRotation, swing);
        return Quaternion.Normalize(swing * authoredRotation);
    }

    private static Quaternion Turn(Vector3 from, Vector3 to, Quaternion basis)
    {
        var cross = Vector3.Cross(from, to);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        Quaternion turn;
        if (dot < 0 && cross.LengthSquared() < 1e-12f)
        {
            // At an exact reversal there is no unique swing axis. Preserve
            // the bone's own frame, not an arbitrary world-axis fallback.
            var axis = Vector3.Transform(Vector3.UnitX, basis);
            axis -= from * Vector3.Dot(axis, from);
            if (axis.LengthSquared() < 1e-6f)
            {
                axis = Vector3.Transform(Vector3.UnitY, basis);
                axis -= from * Vector3.Dot(axis, from);
            }
            turn = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        else
            turn = Quaternion.Normalize(new Quaternion(cross, 1 + dot));
        return turn;
    }

    public void Reset()
    {
        _simulation?.Dispose();
        _simulation = null;
        _pool.Clear();
        _links = [];
        _lengths = [];
        _pins = [];
        _targets = [];
        _rotationFrames.Clear();
        _obstacles = [];
    }
    public void Dispose() => Reset();

    private struct Contacts : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation) { }
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            => (a.Mobility == CollidableMobility.Dynamic) != (b.Mobility == CollidableMobility.Dynamic);
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
            out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = new PairMaterialProperties(.15f, 4, new SpringSettings(360, 1));
            return true;
        }
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
            ref ConvexContactManifold manifold) => true;
        public void Dispose() { }
    }

    private struct Integrator(Vector3 gravity) : IPoseIntegratorCallbacks
    {
        private Vector3Wide _gravityDt;
        private Vector<float> _damping;
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;
        public void Initialize(Simulation simulation) { }
        public void PrepareForIntegration(float dt)
        {
            _gravityDt = Vector3Wide.Broadcast(gravity * (9.81f * dt));
            _damping = new Vector<float>(MathF.Exp(-15 * dt));
        }
        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
            BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear = (velocity.Linear + _gravityDt) * _damping;
            velocity.Angular *= _damping;
        }
    }
}
