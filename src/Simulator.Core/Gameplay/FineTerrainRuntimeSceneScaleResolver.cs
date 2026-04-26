using System.Buffers.Binary;
using System.Numerics;
using K4os.Compression.LZ4.Streams;
using Simulator.Core.Map;

namespace Simulator.Core.Gameplay;

internal readonly record struct FineTerrainRuntimeSceneScale(
    Vector3 ModelCenter,
    double XMetersPerModelUnit,
    double YMetersPerModelUnit,
    double ZMetersPerModelUnit);

internal static class FineTerrainRuntimeSceneScaleResolver
{
    private const string Magic = "LTCH";
    private const int ReferenceCacheVersion = 2;
    private const int LegacyHeaderBytes = 44;

    public static bool TryResolve(
        MapPresetDefinition mapPreset,
        out FineTerrainRuntimeSceneScale scale)
    {
        scale = default;
        string terrainCachePath = ResolveTerrainCachePath(mapPreset);
        if (string.IsNullOrWhiteSpace(terrainCachePath) || !File.Exists(terrainCachePath))
        {
            return false;
        }

        try
        {
            using FileStream file = File.OpenRead(terrainCachePath);
            using Stream decoded = LZ4Stream.Decode(file);
            using var reader = new BinaryReader(decoded);

            byte[] prefix = reader.ReadBytes(sizeof(int) * 2 + sizeof(long));
            if (prefix.Length != sizeof(int) * 2 + sizeof(long))
            {
                return false;
            }

            string magic = System.Text.Encoding.ASCII.GetString(prefix, 0, 4);
            if (!string.Equals(magic, Magic, StringComparison.Ordinal))
            {
                return false;
            }

            int discriminator = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));
            long sourceTicks = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8, 8));

            float minX;
            float minY;
            float minZ;
            float maxX;
            float maxY;
            float maxZ;

            if (discriminator == ReferenceCacheVersion && IsPlausibleSourceTicks(sourceTicks))
            {
                if (!TryReadBounds(reader, out minX, out minY, out minZ, out maxX, out maxY, out maxZ))
                {
                    return false;
                }
            }
            else
            {
                byte[] header = new byte[LegacyHeaderBytes];
                Buffer.BlockCopy(prefix, 0, header, 0, prefix.Length);
                byte[] headerTail = reader.ReadBytes(LegacyHeaderBytes - prefix.Length);
                if (headerTail.Length != LegacyHeaderBytes - prefix.Length)
                {
                    return false;
                }

                Buffer.BlockCopy(headerTail, 0, header, prefix.Length, headerTail.Length);
                minX = BitConverter.ToSingle(header, 16);
                minY = BitConverter.ToSingle(header, 20);
                minZ = BitConverter.ToSingle(header, 24);
                maxX = BitConverter.ToSingle(header, 28);
                maxY = BitConverter.ToSingle(header, 32);
                maxZ = BitConverter.ToSingle(header, 36);
            }

            double sizeX = Math.Max(1e-6, Math.Abs(maxX - minX));
            double sizeZ = Math.Max(1e-6, Math.Abs(maxZ - minZ));
            double xMetersPerModelUnit = Math.Max(1e-6, mapPreset.FieldLengthM / sizeX);
            double zMetersPerModelUnit = Math.Max(1e-6, mapPreset.FieldWidthM / sizeZ);
            double yMetersPerModelUnit = (xMetersPerModelUnit + zMetersPerModelUnit) * 0.5;

            scale = new FineTerrainRuntimeSceneScale(
                new Vector3(
                    (minX + maxX) * 0.5f,
                    (minY + maxY) * 0.5f,
                    (minZ + maxZ) * 0.5f),
                xMetersPerModelUnit,
                yMetersPerModelUnit,
                zMetersPerModelUnit);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBounds(
        BinaryReader reader,
        out float minX,
        out float minY,
        out float minZ,
        out float maxX,
        out float maxY,
        out float maxZ)
    {
        minX = 0f;
        minY = 0f;
        minZ = 0f;
        maxX = 0f;
        maxY = 0f;
        maxZ = 0f;
        byte[] buffer = reader.ReadBytes(sizeof(float) * 6);
        if (buffer.Length != sizeof(float) * 6)
        {
            return false;
        }

        minX = BitConverter.ToSingle(buffer, 0);
        minY = BitConverter.ToSingle(buffer, 4);
        minZ = BitConverter.ToSingle(buffer, 8);
        maxX = BitConverter.ToSingle(buffer, 12);
        maxY = BitConverter.ToSingle(buffer, 16);
        maxZ = BitConverter.ToSingle(buffer, 20);
        return true;
    }

    private static bool IsPlausibleSourceTicks(long ticks)
        => ticks >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
            && ticks <= new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private static string ResolveTerrainCachePath(MapPresetDefinition mapPreset)
    {
        if (mapPreset.RuntimeGrid is null || string.IsNullOrWhiteSpace(mapPreset.RuntimeGrid.SourcePath))
        {
            return string.Empty;
        }

        string? mapDirectory = Path.GetDirectoryName(mapPreset.SourcePath);
        if (string.IsNullOrWhiteSpace(mapDirectory))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(mapPreset.RuntimeGrid.SourcePath)
            ? mapPreset.RuntimeGrid.SourcePath
            : Path.GetFullPath(Path.Combine(mapDirectory, mapPreset.RuntimeGrid.SourcePath));
    }
}
