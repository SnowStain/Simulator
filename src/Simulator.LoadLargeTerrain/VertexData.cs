using System.Numerics;
using System.Runtime.InteropServices;

namespace LoadLargeTerrain;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct VertexData
{
    public Vector3 Position;
    public Vector3 Normal;
    public uint Color;

    public VertexData(Vector3 position, Vector3 normal, uint color)
    {
        Position = position;
        Normal = normal;
        Color = color;
    }
}
