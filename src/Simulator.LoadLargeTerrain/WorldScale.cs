using System.Numerics;

namespace LoadLargeTerrain;

internal sealed class WorldScale
{
    public const float RealLengthXMeters = 28.0f;
    public const float RealLengthZMeters = 15.0f;

    private readonly BoundingBox _bounds;
    private readonly float _xMetersPerUnit;
    private readonly float _zMetersPerUnit;
    private readonly float _yMetersPerUnit;

    public WorldScale(BoundingBox bounds)
    {
        _bounds = bounds;
        _xMetersPerUnit = RealLengthXMeters / MathF.Max(bounds.Size.X, 0.0001f);
        _zMetersPerUnit = RealLengthZMeters / MathF.Max(bounds.Size.Z, 0.0001f);
        _yMetersPerUnit = (_xMetersPerUnit + _zMetersPerUnit) * 0.5f;
    }

    public float XMetersPerUnit => _xMetersPerUnit;

    public float YMetersPerUnit => _yMetersPerUnit;

    public float ZMetersPerUnit => _zMetersPerUnit;

    public Vector3 ModelToMeters(Vector3 modelPosition)
    {
        var center = _bounds.Center;
        return new Vector3(
            (modelPosition.X - center.X) * _xMetersPerUnit,
            (modelPosition.Y - center.Y) * _yMetersPerUnit,
            (modelPosition.Z - center.Z) * _zMetersPerUnit);
    }

    public Vector3 MetersToModel(Vector3 meterPosition)
    {
        var center = _bounds.Center;
        return new Vector3(
            center.X + (meterPosition.X / _xMetersPerUnit),
            center.Y + (meterPosition.Y / _yMetersPerUnit),
            center.Z + (meterPosition.Z / _zMetersPerUnit));
    }
}
