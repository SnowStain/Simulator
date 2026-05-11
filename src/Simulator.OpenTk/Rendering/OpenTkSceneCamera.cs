using OpenTK.Mathematics;

namespace Simulator.OpenTk.Rendering;

public sealed record OpenTkSceneCamera(
    Vector3 Position,
    Vector3 Target,
    Vector3 Up);
