using System.Numerics;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4.Streams;

namespace LoadLargeTerrain;

internal static class SceneCache
{
    private const uint Magic = 0x4843544C; // LTCH
    private const int Version = 2;

    public static TerrainSceneData LoadOrBuild(string modelPath, string cachePath)
    {
        var sourceTicks = File.GetLastWriteTimeUtc(modelPath).Ticks;

        if (File.Exists(cachePath))
        {
            try
            {
                using var file = File.OpenRead(cachePath);
                using var decoded = LZ4Stream.Decode(file, leaveOpen: false);
                var cachedScene = ReadCache(decoded, sourceTicks);
                Console.WriteLine($"已读取压缩缓存，共 {cachedScene.Chunks.Count} 个合并分块。");
                return cachedScene;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"缓存读取失败，将重新生成：{ex.Message}");
            }
        }

        Console.WriteLine("正在从 GLB 生成合并地形缓存，首次运行可能需要一些时间...");
        var builtScene = GltfSceneBuilder.Build(modelPath, sourceTicks);

        var tempPath = cachePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        using (var file = File.Create(tempPath))
        using (var encoded = LZ4Stream.Encode(file, leaveOpen: false))
        {
            WriteCache(encoded, builtScene);
        }

        File.Move(tempPath, cachePath, overwrite: true);
        Console.WriteLine("压缩缓存写入成功。");

        return builtScene;
    }

    private static void WriteCache(Stream stream, TerrainSceneData scene)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(scene.SourceWriteTimeUtcTicks);
        WriteVector3(writer, scene.Bounds.Min);
        WriteVector3(writer, scene.Bounds.Max);
        writer.Write(scene.Components.Count);
        foreach (var component in scene.Components)
        {
            writer.Write(component.Id);
            writer.Write(component.NodeIndex);
            writer.Write(component.MeshIndex);
            writer.Write(component.PrimitiveIndex);
            writer.Write(component.Name);
            WriteVector3(writer, component.Bounds.Min);
            WriteVector3(writer, component.Bounds.Max);
        }

        writer.Write(scene.Chunks.Count);

        foreach (var chunk in scene.Chunks)
        {
            writer.Write(chunk.Name);
            WriteVector3(writer, chunk.Bounds.Min);
            WriteVector3(writer, chunk.Bounds.Max);
            writer.Write(chunk.Vertices.Length);
            writer.Write(chunk.Indices.Length);
            writer.Write(chunk.ComponentRanges.Length);
            foreach (var range in chunk.ComponentRanges)
            {
                writer.Write(range.ComponentId);
                writer.Write(range.StartIndex);
                writer.Write(range.IndexCount);
                WriteVector3(writer, range.Bounds.Min);
                WriteVector3(writer, range.Bounds.Max);
            }

            writer.Write(MemoryMarshal.AsBytes(chunk.Vertices.AsSpan()));
            writer.Write(MemoryMarshal.AsBytes(chunk.Indices.AsSpan()));
        }
    }

    private static TerrainSceneData ReadCache(Stream stream, long expectedSourceTicks)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException("缓存文件头不匹配。");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException($"不支持的缓存版本：{version}。");
        }

        var sourceTicks = reader.ReadInt64();
        if (sourceTicks != expectedSourceTicks)
        {
            throw new InvalidDataException("源 GLB 已变化，缓存已过期。");
        }

        var bounds = new BoundingBox(ReadVector3(reader), ReadVector3(reader));
        var componentCount = reader.ReadInt32();
        var components = new List<ComponentData>(componentCount);

        for (var i = 0; i < componentCount; i++)
        {
            components.Add(new ComponentData
            {
                Id = reader.ReadInt32(),
                NodeIndex = reader.ReadInt32(),
                MeshIndex = reader.ReadInt32(),
                PrimitiveIndex = reader.ReadInt32(),
                Name = reader.ReadString(),
                Bounds = new BoundingBox(ReadVector3(reader), ReadVector3(reader)),
            });
        }

        var chunkCount = reader.ReadInt32();
        var chunks = new List<TerrainChunkData>(chunkCount);

        for (var i = 0; i < chunkCount; i++)
        {
            var name = reader.ReadString();
            var chunkBounds = new BoundingBox(ReadVector3(reader), ReadVector3(reader));
            var vertexCount = reader.ReadInt32();
            var indexCount = reader.ReadInt32();
            var rangeCount = reader.ReadInt32();
            var componentRanges = new ComponentRangeData[rangeCount];

            for (var rangeIndex = 0; rangeIndex < rangeCount; rangeIndex++)
            {
                componentRanges[rangeIndex] = new ComponentRangeData
                {
                    ComponentId = reader.ReadInt32(),
                    StartIndex = reader.ReadInt32(),
                    IndexCount = reader.ReadInt32(),
                    Bounds = new BoundingBox(ReadVector3(reader), ReadVector3(reader)),
                };
            }

            var vertices = new VertexData[vertexCount];
            var indices = new uint[indexCount];

            ReadExactly(stream, MemoryMarshal.AsBytes(vertices.AsSpan()));
            ReadExactly(stream, MemoryMarshal.AsBytes(indices.AsSpan()));

            chunks.Add(new TerrainChunkData
            {
                Name = name,
                Bounds = chunkBounds,
                Vertices = vertices,
                Indices = indices,
                ComponentRanges = componentRanges,
            });
        }

        return new TerrainSceneData
        {
            Bounds = bounds,
            Chunks = chunks,
            Components = components,
            SourceWriteTimeUtcTicks = sourceTicks,
        };
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
}
