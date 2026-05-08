using System.Numerics;
using Simulator.Core.Gameplay;

namespace Simulator.ThreeD;

internal readonly record struct RobotReferenceFrame(
    double YawDeg,
    Vector2 ForwardWorld,
    Vector2 RightWorld)
{
    public static RobotReferenceFrame FromYawDeg(double yawDeg)
    {
        double yawRad = yawDeg * Math.PI / 180.0;
        var forward = new Vector2((float)Math.Cos(yawRad), (float)Math.Sin(yawRad));
        var right = new Vector2(-forward.Y, forward.X);
        return new RobotReferenceFrame(SimulationCombatMath.NormalizeDeg(yawDeg), forward, right);
    }

    public Vector2 LocalToWorld(double forward, double right)
        => ForwardWorld * (float)forward + RightWorld * (float)right;

    public Vector2 WorldToLocal(double worldX, double worldY)
    {
        var world = new Vector2((float)worldX, (float)worldY);
        return new Vector2(Vector2.Dot(world, ForwardWorld), Vector2.Dot(world, RightWorld));
    }

    public double WorldYawToLocalDeg(double yawDeg)
        => SimulationCombatMath.NormalizeDeg(yawDeg - YawDeg);
}
