using System.Numerics;

namespace LoadLargeTerrain;

internal struct BoundingBox
{
    public Vector3 Min;
    public Vector3 Max;

    public BoundingBox(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    public void Include(Vector3 point)
    {
        Min = Vector3.Min(Min, point);
        Max = Vector3.Max(Max, point);
    }

    public void Include(BoundingBox other)
    {
        Min = Vector3.Min(Min, other.Min);
        Max = Vector3.Max(Max, other.Max);
    }

    public static BoundingBox CreateEmpty()
    {
        return new BoundingBox(
            new Vector3(float.PositiveInfinity),
            new Vector3(float.NegativeInfinity));
    }

    public static BoundingBox Transform(BoundingBox box, Matrix4x4 matrix)
    {
        var corners = new[]
        {
            new Vector3(box.Min.X, box.Min.Y, box.Min.Z),
            new Vector3(box.Max.X, box.Min.Y, box.Min.Z),
            new Vector3(box.Min.X, box.Max.Y, box.Min.Z),
            new Vector3(box.Max.X, box.Max.Y, box.Min.Z),
            new Vector3(box.Min.X, box.Min.Y, box.Max.Z),
            new Vector3(box.Max.X, box.Min.Y, box.Max.Z),
            new Vector3(box.Min.X, box.Max.Y, box.Max.Z),
            new Vector3(box.Max.X, box.Max.Y, box.Max.Z),
        };

        var transformed = CreateEmpty();

        for (var i = 0; i < 8; i++)
        {
            transformed.Include(Vector3.Transform(corners[i], matrix));
        }

        return transformed;
    }

    public bool IsValid()
    {
        return !float.IsInfinity(Min.X) &&
               !float.IsInfinity(Min.Y) &&
               !float.IsInfinity(Min.Z) &&
               !float.IsInfinity(Max.X) &&
               !float.IsInfinity(Max.Y) &&
               !float.IsInfinity(Max.Z);
    }
}
