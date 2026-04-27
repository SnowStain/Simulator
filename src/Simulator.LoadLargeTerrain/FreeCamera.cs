using OpenTK.Mathematics;

namespace LoadLargeTerrain;

internal sealed class FreeCamera
{
    public Vector3 Position { get; set; }

    public float YawDegrees { get; set; } = -90.0f;

    public float PitchDegrees { get; set; } = -18.0f;

    public Vector3 Forward
    {
        get
        {
            var yaw = MathHelper.DegreesToRadians(YawDegrees);
            var pitch = MathHelper.DegreesToRadians(PitchDegrees);
            var forward = new Vector3(
                MathF.Cos(yaw) * MathF.Cos(pitch),
                MathF.Sin(pitch),
                MathF.Sin(yaw) * MathF.Cos(pitch));
            return forward.Normalized();
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Matrix4 GetViewMatrix()
    {
        return Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);
    }

    public void Rotate(float deltaX, float deltaY, float sensitivity)
    {
        YawDegrees += deltaX * sensitivity;
        PitchDegrees -= deltaY * sensitivity;
        PitchDegrees = Math.Clamp(PitchDegrees, -89.0f, 89.0f);
    }
}
