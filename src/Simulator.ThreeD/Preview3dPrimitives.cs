using System.Numerics;

namespace Simulator.ThreeD;

internal readonly record struct Preview3dFace(Vector3[] Vertices, Color BaseColor);

internal static class Preview3dPrimitives
{
    public static void AddOrientedBox(
        ICollection<Preview3dFace> faces,
        Vector3 center,
        Vector3 forward,
        Vector3 right,
        Vector3 up,
        float length,
        float width,
        float height,
        Color color)
    {
        Vector3 halfForward = Vector3.Normalize(forward) * (length * 0.5f);
        Vector3 halfRight = Vector3.Normalize(right) * (width * 0.5f);
        Vector3 halfUp = Vector3.Normalize(up) * (height * 0.5f);
        Vector3[] corners =
        {
            center - halfForward - halfRight - halfUp,
            center + halfForward - halfRight - halfUp,
            center + halfForward + halfRight - halfUp,
            center - halfForward + halfRight - halfUp,
            center - halfForward - halfRight + halfUp,
            center + halfForward - halfRight + halfUp,
            center + halfForward + halfRight + halfUp,
            center - halfForward + halfRight + halfUp,
        };

        AddQuad(faces, corners[0], corners[1], corners[2], corners[3], color);
        AddQuad(faces, corners[4], corners[5], corners[6], corners[7], color);
        AddQuad(faces, corners[0], corners[1], corners[5], corners[4], color);
        AddQuad(faces, corners[1], corners[2], corners[6], corners[5], color);
        AddQuad(faces, corners[2], corners[3], corners[7], corners[6], color);
        AddQuad(faces, corners[3], corners[0], corners[4], corners[7], color);
    }

    public static void AddBox(ICollection<Preview3dFace> faces, Vector3 center, Vector3 halfExtents, Color color, float yawRad = 0f)
    {
        Vector3[] corners =
        {
            RotateAroundY(new Vector3(-halfExtents.X, -halfExtents.Y, -halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(halfExtents.X, -halfExtents.Y, -halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(halfExtents.X, -halfExtents.Y, halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(-halfExtents.X, -halfExtents.Y, halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(-halfExtents.X, halfExtents.Y, -halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(halfExtents.X, halfExtents.Y, -halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(halfExtents.X, halfExtents.Y, halfExtents.Z), yawRad) + center,
            RotateAroundY(new Vector3(-halfExtents.X, halfExtents.Y, halfExtents.Z), yawRad) + center,
        };

        AddQuad(faces, corners[0], corners[1], corners[2], corners[3], color);
        AddQuad(faces, corners[4], corners[5], corners[6], corners[7], color);
        AddQuad(faces, corners[0], corners[1], corners[5], corners[4], color);
        AddQuad(faces, corners[1], corners[2], corners[6], corners[5], color);
        AddQuad(faces, corners[2], corners[3], corners[7], corners[6], color);
        AddQuad(faces, corners[3], corners[0], corners[4], corners[7], color);
    }

    public static void AddPrism(ICollection<Preview3dFace> faces, IReadOnlyList<Vector3> bottom, IReadOnlyList<Vector3> top, Color color)
    {
        if (bottom.Count < 3 || top.Count < 3 || bottom.Count != top.Count)
        {
            return;
        }

        for (int index = 1; index < top.Count - 1; index++)
        {
            AddTriangle(faces, top[0], top[index], top[index + 1], color);
            AddTriangle(faces, bottom[0], bottom[index + 1], bottom[index], Darken(color, 0.84f));
        }

        for (int index = 0; index < bottom.Count; index++)
        {
            int next = (index + 1) % bottom.Count;
            AddQuad(faces, bottom[index], bottom[next], top[next], top[index], Darken(color, 0.92f));
        }
    }

    public static void AddCylinder(
        ICollection<Preview3dFace> faces,
        Vector3 center,
        Vector3 normalAxis,
        Vector3 upAxis,
        float radius,
        float thickness,
        Color color,
        int segments = 18)
    {
        Vector3 normal = normalAxis.LengthSquared() <= 1e-8f ? Vector3.UnitX : Vector3.Normalize(normalAxis);
        Vector3 up = upAxis.LengthSquared() <= 1e-8f ? Vector3.UnitY : Vector3.Normalize(upAxis);
        if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
        {
            up = Vector3.UnitY;
            if (MathF.Abs(Vector3.Dot(normal, up)) > 0.98f)
            {
                up = Vector3.UnitZ;
            }
        }

        Vector3 tangent = Vector3.Normalize(up - normal * Vector3.Dot(up, normal));
        Vector3 bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        float halfThickness = Math.Max(0.001f, thickness * 0.5f);
        segments = Math.Max(8, segments);
        Vector3 frontCenter = center - normal * halfThickness;
        Vector3 backCenter = center + normal * halfThickness;
        var front = new Vector3[segments];
        var back = new Vector3[segments];
        for (int index = 0; index < segments; index++)
        {
            float angle = MathF.Tau * index / segments;
            Vector3 offset = tangent * (MathF.Cos(angle) * radius) + bitangent * (MathF.Sin(angle) * radius);
            front[index] = frontCenter + offset;
            back[index] = backCenter + offset;
        }

        for (int index = 1; index < segments - 1; index++)
        {
            AddTriangle(faces, front[0], front[index], front[index + 1], color);
            AddTriangle(faces, back[0], back[index + 1], back[index], Darken(color, 0.88f));
        }

        for (int index = 0; index < segments; index++)
        {
            int next = (index + 1) % segments;
            AddQuad(faces, front[index], front[next], back[next], back[index], Darken(color, 0.82f));
        }
    }

    public static Vector3 RotateAroundY(Vector3 value, float yawRad)
    {
        float cos = MathF.Cos(yawRad);
        float sin = MathF.Sin(yawRad);
        return new Vector3(
            value.X * cos - value.Z * sin,
            value.Y,
            value.X * sin + value.Z * cos);
    }

    public static IEnumerable<(PointF[] Points, float Depth, Color Color)> ProjectFaces(
        IEnumerable<Preview3dFace> faces,
        Rectangle viewport,
        Vector3 sceneCenter,
        float yawRad,
        float pitchRad,
        float scale)
    {
        const float cameraDistance = 9.0f;
        var projected = new List<(PointF[] Points, float Depth, Color Color)>();
        Vector3 light = Vector3.Normalize(new Vector3(0.45f, 0.85f, 0.25f));

        foreach (Preview3dFace face in faces)
        {
            var rotated = new Vector3[face.Vertices.Length];
            bool invalidFace = false;
            for (int index = 0; index < face.Vertices.Length; index++)
            {
                Vector3 shifted = face.Vertices[index] - sceneCenter;
                if (!IsFinite(shifted))
                {
                    invalidFace = true;
                    break;
                }

                float x1 = shifted.X * MathF.Cos(yawRad) - shifted.Z * MathF.Sin(yawRad);
                float z1 = shifted.X * MathF.Sin(yawRad) + shifted.Z * MathF.Cos(yawRad);
                float y2 = shifted.Y * MathF.Cos(pitchRad) - z1 * MathF.Sin(pitchRad);
                float z2 = shifted.Y * MathF.Sin(pitchRad) + z1 * MathF.Cos(pitchRad);
                rotated[index] = new Vector3(x1, y2, z2);
                if (!IsFinite(rotated[index]))
                {
                    invalidFace = true;
                    break;
                }
            }

            if (invalidFace || rotated.Length < 3)
            {
                continue;
            }

            Vector3 cross = Vector3.Cross(rotated[1] - rotated[0], rotated[2] - rotated[0]);
            if (!IsFinite(cross) || cross.LengthSquared() <= 1e-8f)
            {
                continue;
            }

            Vector3 normal = Vector3.Normalize(cross);
            float diffuse = MathF.Abs(Vector3.Dot(normal, light));
            float skyLift = Math.Clamp(normal.Y * 0.5f + 0.5f, 0f, 1f);
            float lighting = Math.Clamp(0.46f + diffuse * 0.42f + skyLift * 0.20f, 0.28f, 1.12f);
            Color shaded = Multiply(face.BaseColor, lighting);
            PointF[] points = new PointF[rotated.Length];
            float depth = 0f;
            for (int index = 0; index < rotated.Length; index++)
            {
                Vector3 value = rotated[index];
                float perspective = cameraDistance / Math.Max(1.0f, cameraDistance + value.Z);
                if (!float.IsFinite(perspective))
                {
                    invalidFace = true;
                    break;
                }

                points[index] = new PointF(
                    viewport.X + viewport.Width * 0.5f + value.X * scale * perspective,
                    viewport.Y + viewport.Height * 0.62f - value.Y * scale * perspective);
                if (!float.IsFinite(points[index].X) || !float.IsFinite(points[index].Y))
                {
                    invalidFace = true;
                    break;
                }

                depth += value.Z;
            }

            if (invalidFace)
            {
                continue;
            }

            projected.Add((points, depth / rotated.Length, shaded));
        }

        return projected.OrderByDescending(item => item.Depth).ToArray();
    }

    public static PointF[] ProjectPoints(
        IReadOnlyList<Vector3> vertices,
        Rectangle viewport,
        Vector3 sceneCenter,
        float yawRad,
        float pitchRad,
        float scale)
    {
        const float cameraDistance = 9.0f;
        var points = new PointF[vertices.Count];
        for (int index = 0; index < vertices.Count; index++)
        {
            Vector3 shifted = vertices[index] - sceneCenter;
            float x1 = shifted.X * MathF.Cos(yawRad) - shifted.Z * MathF.Sin(yawRad);
            float z1 = shifted.X * MathF.Sin(yawRad) + shifted.Z * MathF.Cos(yawRad);
            float y2 = shifted.Y * MathF.Cos(pitchRad) - z1 * MathF.Sin(pitchRad);
            float z2 = shifted.Y * MathF.Sin(pitchRad) + z1 * MathF.Cos(pitchRad);
            float perspective = cameraDistance / Math.Max(1.0f, cameraDistance + z2);
            points[index] = new PointF(
                viewport.X + viewport.Width * 0.5f + x1 * scale * perspective,
                viewport.Y + viewport.Height * 0.62f - y2 * scale * perspective);
        }

        return points;
    }

    public static Color Multiply(Color color, float amount)
    {
        return Color.FromArgb(
            color.A,
            Math.Clamp((int)Math.Round(color.R * amount), 0, 255),
            Math.Clamp((int)Math.Round(color.G * amount), 0, 255),
            Math.Clamp((int)Math.Round(color.B * amount), 0, 255));
    }

    public static Color Darken(Color color, float amount) => Multiply(color, amount);

    public static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static void AddQuad(ICollection<Preview3dFace> faces, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color)
        => faces.Add(new Preview3dFace(new[] { a, b, c, d }, color));

    private static void AddTriangle(ICollection<Preview3dFace> faces, Vector3 a, Vector3 b, Vector3 c, Color color)
        => faces.Add(new Preview3dFace(new[] { a, b, c }, color));
}
