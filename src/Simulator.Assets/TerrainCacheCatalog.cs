using System.Buffers.Binary;
using K4os.Compression.LZ4.Streams;

namespace Simulator.Assets;

public readonly record struct TerrainCachePrimitiveBounds(
    float MinX,
    float MinY,
    float MinZ,
    float MaxX,
    float MaxY,
    float MaxZ);

public sealed class TerrainCacheCatalog
{
    public TerrainCacheCatalog(
        IReadOnlyList<TerrainCachePrimitiveBounds> primitives,
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ)
    {
        Primitives = primitives;
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        MaxX = maxX;
        MaxY = maxY;
        MaxZ = maxZ;
    }

    public IReadOnlyList<TerrainCachePrimitiveBounds> Primitives { get; }

    public float MinX { get; }

    public float MinY { get; }

    public float MinZ { get; }

    public float MaxX { get; }

    public float MaxY { get; }

    public float MaxZ { get; }
}

public sealed class TerrainCacheCatalogReader
{
    private const string Magic = "LTCH";
    private const int HeaderSize = 44;
    private const int CatalogEntryPrefixBytes = sizeof(int) * 4;
    private const int BoundsBytes = sizeof(float) * 6;
    private const int CatalogTailBytes = sizeof(int);

    public TerrainCacheCatalog Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Terrain cache path cannot be empty.", nameof(path));
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Stream decoded = LZ4Stream.Decode(file);
        using var reader = new BinaryReader(decoded);

        byte[] prefix = reader.ReadBytes(sizeof(int) * 2 + sizeof(long));
        if (prefix.Length != sizeof(int) * 2 + sizeof(long))
        {
            throw new InvalidDataException($"Terrain cache header is truncated: {path}");
        }

        string magic = System.Text.Encoding.ASCII.GetString(prefix, 0, 4);
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Terrain cache magic mismatch: expected '{Magic}', got '{magic}'.");
        }

        int discriminator = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));
        long sourceTicks = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8, 8));
        if (discriminator == TerrainCacheMeshReader.ReferenceCacheVersion
            && TerrainCacheMeshReader.IsPlausibleReferenceSourceTicks(sourceTicks))
        {
            return TerrainCacheMeshReader.ReadReferenceCatalog(reader, sourceTicksAlreadyRead: true);
        }

        byte[] header = new byte[HeaderSize];
        Buffer.BlockCopy(prefix, 0, header, 0, prefix.Length);
        byte[] headerTail = reader.ReadBytes(HeaderSize - prefix.Length);
        if (headerTail.Length != HeaderSize - prefix.Length)
        {
            throw new InvalidDataException($"Terrain cache header is truncated: {path}");
        }

        Buffer.BlockCopy(headerTail, 0, header, prefix.Length, headerTail.Length);
        int primitiveCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40, 4));
        if (primitiveCount <= 0)
        {
            throw new InvalidDataException($"Terrain cache contains no primitive catalog entries: {path}");
        }

        var primitives = new TerrainCachePrimitiveBounds[primitiveCount];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        byte[] boundsBuffer = new byte[BoundsBytes];
        for (int index = 0; index < primitiveCount; index++)
        {
            byte[] catalogPrefix = reader.ReadBytes(CatalogEntryPrefixBytes);
            if (catalogPrefix.Length != CatalogEntryPrefixBytes)
            {
                throw new InvalidDataException($"Terrain cache primitive prefix is truncated at index {index}.");
            }

            int nameLength = reader.ReadByte();
            if (nameLength > 0)
            {
                byte[] nameBytes = reader.ReadBytes(nameLength);
                if (nameBytes.Length != nameLength)
                {
                    throw new InvalidDataException($"Terrain cache primitive name is truncated at index {index}.");
                }
            }

            if (reader.Read(boundsBuffer, 0, boundsBuffer.Length) != boundsBuffer.Length)
            {
                throw new InvalidDataException($"Terrain cache primitive bounds are truncated at index {index}.");
            }

            float x1 = BitConverter.ToSingle(boundsBuffer, 0);
            float y1 = BitConverter.ToSingle(boundsBuffer, 4);
            float z1 = BitConverter.ToSingle(boundsBuffer, 8);
            float x2 = BitConverter.ToSingle(boundsBuffer, 12);
            float y2 = BitConverter.ToSingle(boundsBuffer, 16);
            float z2 = BitConverter.ToSingle(boundsBuffer, 20);
            float loX = MathF.Min(x1, x2);
            float loY = MathF.Min(y1, y2);
            float loZ = MathF.Min(z1, z2);
            float hiX = MathF.Max(x1, x2);
            float hiY = MathF.Max(y1, y2);
            float hiZ = MathF.Max(z1, z2);

            primitives[index] = new TerrainCachePrimitiveBounds(loX, loY, loZ, hiX, hiY, hiZ);
            minX = MathF.Min(minX, loX);
            minY = MathF.Min(minY, loY);
            minZ = MathF.Min(minZ, loZ);
            maxX = MathF.Max(maxX, hiX);
            maxY = MathF.Max(maxY, hiY);
            maxZ = MathF.Max(maxZ, hiZ);

        }

        byte[] tail = reader.ReadBytes(CatalogTailBytes);
        if (tail.Length != CatalogTailBytes)
        {
            throw new InvalidDataException("Terrain cache catalog tail is truncated.");
        }

        return new TerrainCacheCatalog(primitives, minX, minY, minZ, maxX, maxY, maxZ);
    }
}

public readonly record struct TerrainCacheVertex(
    float X,
    float Y,
    float Z,
    float NormalX,
    float NormalY,
    float NormalZ,
    byte R,
    byte G,
    byte B,
    byte A);

public sealed record TerrainCacheMeshChunkHeader(
    string Name,
    TerrainCachePrimitiveBounds Bounds,
    TerrainCachePrimitiveBounds DataBounds,
    int VertexCount,
    int IndexCount,
    int AuxiliaryRecordCount,
    int ChunkId,
    int Flags);

public sealed record TerrainCacheComponentRange(
    int ComponentId,
    int StartIndex,
    int IndexCount,
    TerrainCachePrimitiveBounds Bounds);

public sealed class TerrainCacheMeshReader
{
    private const string Magic = "LTCH";
    private const int HeaderSize = 44;
    private const int CatalogEntryPrefixBytes = sizeof(int) * 4;
    private const int CatalogTailBytes = sizeof(int);
    private const int BoundsBytes = sizeof(float) * 6;
    private const int ChunkIntsBytes = sizeof(int) * 6;
    private const int VertexBytes = sizeof(float) * 6 + 4;
    private const int AuxiliaryRecordBytes = 36;
    internal const int ReferenceCacheVersion = 2;

    public TerrainCacheCatalog Load(
        string path,
        Action<TerrainCacheCatalog, TerrainCacheMeshChunkHeader, TerrainCacheVertex[], int[]> onChunk)
        => Load(
            path,
            (catalog, chunk, vertices, indices, _) => onChunk(catalog, chunk, vertices, indices));

    public TerrainCacheCatalog Load(
        string path,
        Action<TerrainCacheCatalog, TerrainCacheMeshChunkHeader, TerrainCacheVertex[], int[], IReadOnlyList<TerrainCacheComponentRange>> onChunk)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Terrain cache path cannot be empty.", nameof(path));
        }

        ArgumentNullException.ThrowIfNull(onChunk);

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Stream decoded = LZ4Stream.Decode(file);
        using var reader = new BinaryReader(decoded);

        byte[] prefix = ReadExact(reader, sizeof(int) * 2 + sizeof(long), "terrain cache header");
        string magic = System.Text.Encoding.ASCII.GetString(prefix, 0, 4);
        if (!string.Equals(magic, Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Terrain cache magic mismatch: expected '{Magic}', got '{magic}'.");
        }

        int discriminator = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(4, 4));
        long sourceTicks = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8, 8));
        if (discriminator == ReferenceCacheVersion
            && IsPlausibleReferenceSourceTicks(sourceTicks))
        {
            return LoadReferenceCache(reader, onChunk, sourceTicksAlreadyRead: true);
        }

        byte[] header = new byte[HeaderSize];
        Buffer.BlockCopy(prefix, 0, header, 0, prefix.Length);
        byte[] headerTail = ReadExact(reader, HeaderSize - prefix.Length, "terrain cache header");
        Buffer.BlockCopy(headerTail, 0, header, prefix.Length, headerTail.Length);
        int primitiveCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40, 4));
        TerrainCacheCatalog catalog = ReadCatalog(reader, primitiveCount);

        while (TryReadChunk(reader, out TerrainCacheMeshChunkHeader? chunk, out TerrainCacheVertex[]? vertices, out int[]? indices))
        {
            onChunk(catalog, chunk!, vertices!, indices!, Array.Empty<TerrainCacheComponentRange>());
        }

        return catalog;
    }

    internal static bool IsPlausibleReferenceSourceTicks(long ticks)
    {
        return ticks >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks
            && ticks <= new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    }

    internal static TerrainCacheCatalog ReadReferenceCatalog(BinaryReader reader, bool sourceTicksAlreadyRead = false)
    {
        if (!sourceTicksAlreadyRead)
        {
            SkipExact(reader, sizeof(long));
        }

        TerrainCachePrimitiveBounds sceneBounds = ReadReferenceBounds(reader);
        int componentCount = reader.ReadInt32();
        if (componentCount < 0)
        {
            throw new InvalidDataException("Reference terrain cache contains an invalid component count.");
        }

        var primitives = componentCount > 0
            ? new TerrainCachePrimitiveBounds[componentCount]
            : new[] { sceneBounds };
        for (int index = 0; index < componentCount; index++)
        {
            SkipExact(reader, sizeof(int) * 4);
            _ = reader.ReadString();
            primitives[index] = ReadReferenceBounds(reader);
        }

        return new TerrainCacheCatalog(
            primitives,
            sceneBounds.MinX,
            sceneBounds.MinY,
            sceneBounds.MinZ,
            sceneBounds.MaxX,
            sceneBounds.MaxY,
            sceneBounds.MaxZ);
    }

    private static TerrainCacheCatalog LoadReferenceCache(
        BinaryReader reader,
        Action<TerrainCacheCatalog, TerrainCacheMeshChunkHeader, TerrainCacheVertex[], int[], IReadOnlyList<TerrainCacheComponentRange>> onChunk,
        bool sourceTicksAlreadyRead = false)
    {
        TerrainCacheCatalog catalog = ReadReferenceCatalog(reader, sourceTicksAlreadyRead);
        int chunkCount = reader.ReadInt32();
        if (chunkCount < 0)
        {
            throw new InvalidDataException("Reference terrain cache contains an invalid chunk count.");
        }

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            string name = reader.ReadString();
            TerrainCachePrimitiveBounds bounds = ReadReferenceBounds(reader);
            int vertexCount = reader.ReadInt32();
            int indexCount = reader.ReadInt32();
            int rangeCount = reader.ReadInt32();
            if (vertexCount < 0 || indexCount < 0 || rangeCount < 0)
            {
                throw new InvalidDataException($"Invalid reference terrain cache chunk metadata for '{name}'.");
            }

            var componentRanges = new TerrainCacheComponentRange[rangeCount];
            for (int rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
            {
                int componentId = reader.ReadInt32();
                int startIndex = reader.ReadInt32();
                int rangeIndexCount = reader.ReadInt32();
                TerrainCachePrimitiveBounds rangeBounds = ReadReferenceBounds(reader);
                componentRanges[rangeIndex] = new TerrainCacheComponentRange(
                    componentId,
                    startIndex,
                    rangeIndexCount,
                    rangeBounds);
            }

            TerrainCacheVertex[] vertices = ReadReferenceVertices(reader, vertexCount, name);
            int[] indices = ReadReferenceIndices(reader, indexCount, name);
            var chunk = new TerrainCacheMeshChunkHeader(
                name,
                bounds,
                bounds,
                vertexCount,
                indexCount,
                0,
                chunkIndex,
                0);
            onChunk(catalog, chunk, vertices, indices, componentRanges);
        }

        return catalog;
    }

    private static TerrainCacheCatalog ReadCatalog(BinaryReader reader, int primitiveCount)
    {
        if (primitiveCount <= 0)
        {
            throw new InvalidDataException("Terrain cache contains no primitive catalog entries.");
        }

        var primitives = new TerrainCachePrimitiveBounds[primitiveCount];
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int index = 0; index < primitiveCount; index++)
        {
            SkipExact(reader, CatalogEntryPrefixBytes);
            int nameLength = reader.ReadByte();
            SkipExact(reader, nameLength);
            TerrainCachePrimitiveBounds bounds = ReadBounds(reader);
            primitives[index] = bounds;
            minX = MathF.Min(minX, bounds.MinX);
            minY = MathF.Min(minY, bounds.MinY);
            minZ = MathF.Min(minZ, bounds.MinZ);
            maxX = MathF.Max(maxX, bounds.MaxX);
            maxY = MathF.Max(maxY, bounds.MaxY);
            maxZ = MathF.Max(maxZ, bounds.MaxZ);
        }

        SkipExact(reader, CatalogTailBytes);
        return new TerrainCacheCatalog(primitives, minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static bool TryReadChunk(
        BinaryReader reader,
        out TerrainCacheMeshChunkHeader? chunk,
        out TerrainCacheVertex[]? vertices,
        out int[]? indices)
    {
        chunk = null;
        vertices = null;
        indices = null;

        int nameLength;
        try
        {
            nameLength = reader.ReadByte();
        }
        catch (EndOfStreamException)
        {
            return false;
        }

        if (nameLength <= 0)
        {
            return false;
        }

        string name = System.Text.Encoding.ASCII.GetString(ReadExact(reader, nameLength, "terrain cache chunk name"));
        if (!name.StartsWith("chunk_", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unexpected terrain cache chunk name: '{name}'.");
        }

        TerrainCachePrimitiveBounds bounds = ReadBounds(reader);
        byte[] intsBuffer = ReadExact(reader, ChunkIntsBytes, $"terrain cache chunk metadata '{name}'");
        int vertexCount = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(0, 4));
        int indexCount = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(4, 4));
        int auxiliaryRecordCount = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(8, 4));
        int chunkId = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(12, 4));
        int reserved = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(16, 4));
        int flags = BinaryPrimitives.ReadInt32LittleEndian(intsBuffer.AsSpan(20, 4));
        if (vertexCount < 0 || indexCount < 0 || auxiliaryRecordCount < 0 || reserved != 0)
        {
            throw new InvalidDataException($"Invalid terrain cache chunk metadata for '{name}'.");
        }

        TerrainCachePrimitiveBounds dataBounds = ReadBounds(reader);
        if (auxiliaryRecordCount > 1)
        {
            SkipExact(reader, checked((auxiliaryRecordCount - 1) * AuxiliaryRecordBytes));
        }

        vertices = ReadVertices(reader, vertexCount, name);
        indices = ReadIndices(reader, indexCount, name);
        chunk = new TerrainCacheMeshChunkHeader(
            name,
            bounds,
            dataBounds,
            vertexCount,
            indexCount,
            auxiliaryRecordCount,
            chunkId,
            flags);
        return true;
    }

    private static TerrainCacheVertex[] ReadVertices(BinaryReader reader, int count, string chunkName)
    {
        var vertices = new TerrainCacheVertex[count];
        byte[] buffer = new byte[VertexBytes];
        for (int index = 0; index < count; index++)
        {
            if (reader.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                throw new InvalidDataException($"Terrain cache vertex buffer is truncated in '{chunkName}'.");
            }

            vertices[index] = new TerrainCacheVertex(
                BitConverter.ToSingle(buffer, 0),
                BitConverter.ToSingle(buffer, 4),
                BitConverter.ToSingle(buffer, 8),
                BitConverter.ToSingle(buffer, 12),
                BitConverter.ToSingle(buffer, 16),
                BitConverter.ToSingle(buffer, 20),
                buffer[24],
                buffer[25],
                buffer[26],
                buffer[27]);
        }

        return vertices;
    }

    private static int[] ReadIndices(BinaryReader reader, int count, string chunkName)
    {
        var indices = new int[count];
        byte[] buffer = new byte[sizeof(int)];
        for (int index = 0; index < count; index++)
        {
            if (reader.Read(buffer, 0, buffer.Length) != buffer.Length)
            {
                throw new InvalidDataException($"Terrain cache index buffer is truncated in '{chunkName}'.");
            }

            indices[index] = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        }

        return indices;
    }

    private static TerrainCachePrimitiveBounds ReadBounds(BinaryReader reader)
    {
        byte[] boundsBuffer = ReadExact(reader, BoundsBytes, "terrain cache bounds");
        float x1 = BitConverter.ToSingle(boundsBuffer, 0);
        float y1 = BitConverter.ToSingle(boundsBuffer, 4);
        float z1 = BitConverter.ToSingle(boundsBuffer, 8);
        float x2 = BitConverter.ToSingle(boundsBuffer, 12);
        float y2 = BitConverter.ToSingle(boundsBuffer, 16);
        float z2 = BitConverter.ToSingle(boundsBuffer, 20);
        return new TerrainCachePrimitiveBounds(
            MathF.Min(x1, x2),
            MathF.Min(y1, y2),
            MathF.Min(z1, z2),
            MathF.Max(x1, x2),
            MathF.Max(y1, y2),
            MathF.Max(z1, z2));
    }

    private static TerrainCachePrimitiveBounds ReadReferenceBounds(BinaryReader reader)
    {
        float minX = reader.ReadSingle();
        float minY = reader.ReadSingle();
        float minZ = reader.ReadSingle();
        float maxX = reader.ReadSingle();
        float maxY = reader.ReadSingle();
        float maxZ = reader.ReadSingle();
        return new TerrainCachePrimitiveBounds(
            MathF.Min(minX, maxX),
            MathF.Min(minY, maxY),
            MathF.Min(minZ, maxZ),
            MathF.Max(minX, maxX),
            MathF.Max(minY, maxY),
            MathF.Max(minZ, maxZ));
    }

    private static TerrainCacheVertex[] ReadReferenceVertices(BinaryReader reader, int count, string chunkName)
    {
        var vertices = new TerrainCacheVertex[count];
        for (int index = 0; index < count; index++)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float normalX = reader.ReadSingle();
            float normalY = reader.ReadSingle();
            float normalZ = reader.ReadSingle();
            uint color = reader.ReadUInt32();
            vertices[index] = new TerrainCacheVertex(
                x,
                y,
                z,
                normalX,
                normalY,
                normalZ,
                (byte)(color & 0xFFu),
                (byte)((color >> 8) & 0xFFu),
                (byte)((color >> 16) & 0xFFu),
                (byte)((color >> 24) & 0xFFu));
        }

        return vertices;
    }

    private static int[] ReadReferenceIndices(BinaryReader reader, int count, string chunkName)
    {
        var indices = new int[count];
        for (int index = 0; index < count; index++)
        {
            uint value = reader.ReadUInt32();
            if (value > int.MaxValue)
            {
                throw new InvalidDataException($"Reference terrain cache index is too large in '{chunkName}'.");
            }

            indices[index] = (int)value;
        }

        return indices;
    }

    private static byte[] ReadExact(BinaryReader reader, int count, string description)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
        {
            throw new InvalidDataException($"{description} is truncated.");
        }

        return data;
    }

    private static void SkipExact(BinaryReader reader, int count)
    {
        if (count <= 0)
        {
            return;
        }

        const int BufferSize = 81920;
        byte[] buffer = new byte[Math.Min(BufferSize, count)];
        int remaining = count;
        while (remaining > 0)
        {
            int read = reader.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                throw new InvalidDataException("Terrain cache stream ended while skipping data.");
            }

            remaining -= read;
        }
    }
}
