using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal sealed class BepuProjectileObstacleBackend : IDisposable
{
    private const float QueryMarginM = 0.024f;

    private readonly BufferPool _bufferPool = new();
    private readonly Simulation _simulation;
    private readonly CollidableProperty<int> _collidableMetadata;
    private readonly List<ColliderMetadata> _metadataStore = new();
    private readonly Dictionary<string, ColliderEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _staleEntityIds = new();
    private SimulationWorldState? _lastSyncedWorld;
    private double _lastSyncedGameTimeSec = double.NaN;

    public BepuProjectileObstacleBackend()
    {
        _simulation = Simulation.Create(
            _bufferPool,
            new MinimalNarrowPhaseCallbacks(),
            new MinimalPoseIntegratorCallbacks(),
            new SolveDescription(1, 1),
            new DefaultTimestepper(),
            null);
        _collidableMetadata = new CollidableProperty<int>(_simulation, _bufferPool);
    }

    public ProjectileObstacleHit? ResolveHit(
        SimulationWorldState world,
        RuntimeGridData? runtimeGrid,
        SimulationEntity shooter,
        SimulationProjectile projectile,
        double startX,
        double startY,
        double startHeightM,
        double endX,
        double endY,
        double endHeightM,
        IReadOnlyList<SimulationEntity>? obstacleCandidates,
        Func<SimulationEntity, RobotAppearanceProfile>? profileResolver = null)
    {
        ProjectileObstacleHit? terrainHit = ProjectileObstacleResolver.ResolveHit(
            world,
            runtimeGrid,
            shooter,
            projectile,
            startX,
            startY,
            startHeightM,
            endX,
            endY,
            endHeightM,
            Array.Empty<SimulationEntity>(),
            profileResolver);

        IReadOnlyList<SimulationEntity> candidates = obstacleCandidates ?? (IReadOnlyList<SimulationEntity>)world.Entities;
        if (!ReferenceEquals(_lastSyncedWorld, world)
            || Math.Abs(_lastSyncedGameTimeSec - world.GameTimeSec) > 1e-6)
        {
            SyncEntities(world, candidates);
            _lastSyncedWorld = world;
            _lastSyncedGameTimeSec = world.GameTimeSec;
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        Vector3 start = new((float)(startX * metersPerWorldUnit), (float)startHeightM, (float)(startY * metersPerWorldUnit));
        Vector3 end = new((float)(endX * metersPerWorldUnit), (float)endHeightM, (float)(endY * metersPerWorldUnit));
        Vector3 rayDirection = end - start;
        float maxDistance = rayDirection.Length();
        if (maxDistance <= 1e-5f)
        {
            return terrainHit;
        }

        rayDirection /= maxDistance;
        var hitHandler = new ClosestRayHitHandler(_collidableMetadata, _metadataStore, shooter.Id, projectile.PreferredTargetId);
        _simulation.RayCast(in start, in rayDirection, maxDistance, ref hitHandler, _entries.Count);

        ProjectileObstacleHit? entityHit = null;
        if (hitHandler.HasHit)
        {
            Vector3 hitPosition = start + rayDirection * hitHandler.HitDistanceM;
            float segmentT = Math.Clamp(hitHandler.HitDistanceM / maxDistance, 0f, 1f);
            Vector3 normal = hitHandler.HitNormal.LengthSquared() > 1e-8f
                ? Vector3.Normalize(hitHandler.HitNormal)
                : -rayDirection;
            entityHit = new ProjectileObstacleHit(
                hitPosition.X / metersPerWorldUnit,
                hitPosition.Z / metersPerWorldUnit,
                hitPosition.Y,
                normal.X,
                normal.Y,
                normal.Z,
                segmentT,
                SupportsRicochet: true,
                Kind: hitHandler.HitMetadata.EntityType);
        }

        if (terrainHit is null)
        {
            return entityHit;
        }

        if (entityHit is null || terrainHit.Value.SegmentT <= entityHit.Value.SegmentT)
        {
            return terrainHit;
        }

        return entityHit;
    }

    private void SyncEntities(SimulationWorldState world, IReadOnlyList<SimulationEntity> candidates)
    {
        _staleEntityIds.Clear();
        foreach (string entityId in _entries.Keys)
        {
            _staleEntityIds.Add(entityId);
        }

        double metersPerWorldUnit = Math.Max(world.MetersPerWorldUnit, 1e-6);
        foreach (SimulationEntity entity in candidates)
        {
            if (!ShouldRepresent(entity))
            {
                continue;
            }

            ColliderSpec spec = BuildColliderSpec(entity, metersPerWorldUnit);
            ColliderMetadata metadata = new(entity.Id, entity.EntityType, SimulationCombatMath.IsStructure(entity));

            if (_entries.TryGetValue(entity.Id, out ColliderEntry existing))
            {
                if (existing.CanReuse(spec))
                {
                    StaticDescription description = new(
                        spec.PositionM,
                        spec.Orientation,
                        existing.ShapeIndex);
                    _simulation.Statics.ApplyDescription(existing.Handle, in description);
                    _metadataStore[existing.MetadataIndex] = metadata;
                    _collidableMetadata[existing.Handle] = existing.MetadataIndex;
                    _entries[entity.Id] = existing with
                    {
                        PositionM = spec.PositionM,
                        Orientation = spec.Orientation,
                    };
                }
                else
                {
                    RemoveEntry(existing);
                    AddEntry(entity.Id, spec, metadata);
                }
            }
            else
            {
                AddEntry(entity.Id, spec, metadata);
            }

            _staleEntityIds.Remove(entity.Id);
        }

        for (int index = 0; index < _staleEntityIds.Count; index++)
        {
            string staleId = _staleEntityIds[index];
            if (_entries.TryGetValue(staleId, out ColliderEntry entry))
            {
                RemoveEntry(entry);
            }
        }
    }

    private void AddEntry(string entityId, ColliderSpec spec, ColliderMetadata metadata)
    {
        Box box = spec.Box;
        TypedIndex shapeIndex = _simulation.Shapes.Add(in box);
        StaticDescription description = new(spec.PositionM, spec.Orientation, shapeIndex);
        StaticHandle handle = _simulation.Statics.Add(in description);
        int metadataIndex = _metadataStore.Count;
        _metadataStore.Add(metadata);
        _collidableMetadata.Allocate(handle) = metadataIndex;
        _entries[entityId] = new ColliderEntry(
            entityId,
            handle,
            shapeIndex,
            metadataIndex,
            spec.WidthM,
            spec.HeightM,
            spec.LengthM,
            spec.PositionM,
            spec.Orientation);
    }

    private void RemoveEntry(ColliderEntry entry)
    {
        _simulation.Statics.Remove(entry.Handle);
        _simulation.Shapes.RemoveAndDispose(entry.ShapeIndex, _bufferPool);
        _entries.Remove(entry.EntityId);
    }

    private static bool ShouldRepresent(SimulationEntity entity)
    {
        if (entity.IsSimulationSuppressed)
        {
            return false;
        }

        if (SimulationCombatMath.IsLegacyMechanismCollisionSuppressed(entity))
        {
            return false;
        }

        if (SimulationCombatMath.IsStructure(entity))
        {
            return false;
        }

        // Keep robot damage authoritative in the armor-plate solver. If BEPU
        // registers the robot body first, it can turn a valid armor hit into a
        // ricochet/obstacle event with no health damage.
        return false;
    }

    private static ColliderSpec BuildColliderSpec(SimulationEntity entity, double metersPerWorldUnit)
    {
        ProjectileCollisionBroadphase.GetApproximateObstacleBounds(
            entity,
            metersPerWorldUnit,
            QueryMarginM,
            out Vector3 min,
            out Vector3 max);
        Vector3 size = Vector3.Max(Vector3.One * 0.04f, max - min);
        Vector3 position = (min + max) * 0.5f;
        float yawRad = (float)(entity.AngleDeg * Math.PI / 180.0);
        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawRad);
        return new ColliderSpec(
            position,
            orientation,
            size.X,
            size.Y,
            size.Z,
            new Box(size.X, size.Y, size.Z));
    }

    public void Dispose()
    {
        foreach (ColliderEntry entry in _entries.Values.ToArray())
        {
            RemoveEntry(entry);
        }

        _collidableMetadata.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        _lastSyncedWorld = null;
        _lastSyncedGameTimeSec = double.NaN;
    }

    private readonly record struct ColliderMetadata(
        string EntityId,
        string EntityType,
        bool IsStructure);

    private readonly record struct ColliderSpec(
        Vector3 PositionM,
        Quaternion Orientation,
        float WidthM,
        float HeightM,
        float LengthM,
        Box Box);

    private readonly record struct ColliderEntry(
        string EntityId,
        StaticHandle Handle,
        TypedIndex ShapeIndex,
        int MetadataIndex,
        float WidthM,
        float HeightM,
        float LengthM,
        Vector3 PositionM,
        Quaternion Orientation)
    {
        public bool CanReuse(ColliderSpec spec)
        {
            return MathF.Abs(WidthM - spec.WidthM) <= 0.001f
                && MathF.Abs(HeightM - spec.HeightM) <= 0.001f
                && MathF.Abs(LengthM - spec.LengthM) <= 0.001f;
        }
    }

    private struct ClosestRayHitHandler : IRayHitHandler
    {
        private readonly CollidableProperty<int> _metadataLookup;
        private readonly IReadOnlyList<ColliderMetadata> _metadataStore;
        private readonly string _shooterId;
        private readonly string? _preferredTargetId;

        public ClosestRayHitHandler(
            CollidableProperty<int> metadataLookup,
            IReadOnlyList<ColliderMetadata> metadataStore,
            string shooterId,
            string? preferredTargetId)
        {
            _metadataLookup = metadataLookup;
            _metadataStore = metadataStore;
            _shooterId = shooterId;
            _preferredTargetId = preferredTargetId;
            HitDistanceM = float.MaxValue;
            HitNormal = Vector3.Zero;
            HitMetadata = default;
            HasHit = false;
        }

        public bool HasHit { get; private set; }

        public float HitDistanceM { get; private set; }

        public Vector3 HitNormal { get; private set; }

        public ColliderMetadata HitMetadata { get; private set; }

        public bool AllowTest(CollidableReference collidable)
        {
            ColliderMetadata metadata = _metadataStore[_metadataLookup[collidable]];
            if (string.Equals(metadata.EntityId, _shooterId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (metadata.IsStructure
                && !string.Equals(metadata.EntityType, "energy_mechanism", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_preferredTargetId)
                && string.Equals(metadata.EntityId, _preferredTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        public bool AllowTest(CollidableReference collidable, int childIndex) => AllowTest(collidable);

        public void OnRayHit(
            in BepuPhysics.Trees.RayData ray,
            ref float maximumT,
            float t,
            in Vector3 normal,
            CollidableReference collidable,
            int childIndex)
        {
            HasHit = true;
            HitDistanceM = t;
            HitNormal = normal;
            HitMetadata = _metadataStore[_metadataLookup[collidable]];
            maximumT = Math.Min(maximumT, t);
        }
    }

    private struct MinimalNarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation)
        {
        }

        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
            => true;

        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
            => true;

        public bool ConfigureContactManifold<TManifold>(
            int workerIndex,
            CollidablePair pair,
            ref TManifold manifold,
            out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = new PairMaterialProperties
            {
                FrictionCoefficient = 1f,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30f, 1f),
            };
            return true;
        }

        public bool ConfigureContactManifold(
            int workerIndex,
            CollidablePair pair,
            int childIndexA,
            int childIndexB,
            ref ConvexContactManifold manifold)
        {
            return true;
        }

        public void Dispose()
        {
        }
    }

    private struct MinimalPoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        public bool AllowSubstepsForUnconstrainedBodies => false;

        public bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt)
        {
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices,
            Vector3Wide position,
            QuaternionWide orientation,
            BodyInertiaWide localInertia,
            Vector<int> integrationMask,
            int workerIndex,
            Vector<float> dt,
            ref BodyVelocityWide velocity)
        {
        }
    }
}
