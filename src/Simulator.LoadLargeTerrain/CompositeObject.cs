using System.Numerics;

namespace LoadLargeTerrain;

internal sealed class CompositeObject
{
    private Vector3 _pivotModel;
    private Vector3 _positionModel;
    private Vector3 _rotationYprDegrees;
    private bool _modelMatrixDirty = true;
    private bool _boundsDirty = true;
    private Matrix4x4 _cachedModelMatrix;
    private BoundingBox _cachedBounds;

    public required int Id { get; init; }

    public required string Name { get; set; }

    public bool IsActor { get; set; } = true;

    public Vector3 PivotModel
    {
        get => _pivotModel;
        set
        {
            if (_pivotModel == value)
            {
                return;
            }

            _pivotModel = value;
            InvalidateTransformCache();
        }
    }

    public Vector3 PositionModel
    {
        get => _positionModel;
        set
        {
            if (_positionModel == value)
            {
                return;
            }

            _positionModel = value;
            InvalidateTransformCache();
        }
    }

    public Vector3 RotationYprDegrees
    {
        get => _rotationYprDegrees;
        set
        {
            if (_rotationYprDegrees == value)
            {
                return;
            }

            _rotationYprDegrees = value;
            InvalidateTransformCache();
        }
    }

    public Vector3 CoordinateYprDegrees { get; set; }

    public CoordinateSystemMode CoordinateSystemMode { get; set; } = CoordinateSystemMode.World;

    public int NextInteractionUnitId { get; set; } = 1;

    public HashSet<int> ComponentIds { get; } = new();

    public List<InteractionUnitObject> InteractionUnits { get; } = new();

    public Matrix4x4 ModelMatrix
    {
        get
        {
            if (_modelMatrixDirty)
            {
                var yaw = MathF.PI / 180.0f * RotationYprDegrees.X;
                var pitch = MathF.PI / 180.0f * RotationYprDegrees.Y;
                var roll = MathF.PI / 180.0f * RotationYprDegrees.Z;

                _cachedModelMatrix =
                    Matrix4x4.CreateTranslation(-PivotModel) *
                    Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                    Matrix4x4.CreateTranslation(PositionModel);
                _modelMatrixDirty = false;
            }

            return _cachedModelMatrix;
        }
    }

    public BoundingBox ComputeBounds(IReadOnlyDictionary<int, ComponentData> componentsById)
    {
        if (_boundsDirty)
        {
            var bounds = BoundingBox.CreateEmpty();
            var matrix = ModelMatrix;

            foreach (var componentId in ComponentIds)
            {
                if (componentsById.TryGetValue(componentId, out var component))
                {
                    bounds.Include(BoundingBox.Transform(component.Bounds, matrix));
                }
            }

            _cachedBounds = bounds;
            _boundsDirty = false;
        }

        return _cachedBounds;
    }

    public void InvalidateBoundsCache()
    {
        _boundsDirty = true;
    }

    private void InvalidateTransformCache()
    {
        _modelMatrixDirty = true;
        _boundsDirty = true;
    }
}

internal enum CoordinateSystemMode
{
    World,
    Custom,
}

internal sealed class InteractionUnitObject
{
    public required int Id { get; init; }

    public required string Name { get; set; }

    public HashSet<int> ComponentIds { get; } = new();
}
