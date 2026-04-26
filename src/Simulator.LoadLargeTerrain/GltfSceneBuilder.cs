using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadLargeTerrain;

internal static class GltfSceneBuilder
{
    public static TerrainSceneData Build(string modelPath, long sourceWriteTimeUtcTicks)
    {
        var file = GlbFile.Load(modelPath);

        Console.WriteLine($"GLB 解析完成。节点={file.Root.Nodes?.Count ?? 0}，网格={file.Root.Meshes?.Count ?? 0}");

        var scenePlanner = new ScenePlanner(file.Root);
        var plan = scenePlanner.BuildPlan();

        Console.WriteLine($"世界包围盒：最小={plan.Bounds.Min}，最大={plan.Bounds.Max}");
        Console.WriteLine($"正在将 {plan.PrimitiveInstances.Count:N0} 个 primitive 实例合并为 {plan.CellsPerAxis}x{plan.CellsPerAxis} 个空间分块...");

        var accessorReader = new AccessorReader(file.Root, file.BinaryChunk);
        var chunks = new Dictionary<(int X, int Z), ChunkBuilder>();
        var primitiveIndex = 0;

        foreach (var primitiveInstance in plan.PrimitiveInstances)
        {
            primitiveIndex++;

            if (primitiveIndex % 25000 == 0 || primitiveIndex == plan.PrimitiveInstances.Count)
            {
                Console.WriteLine($"正在预处理 primitive {primitiveIndex:N0}/{plan.PrimitiveInstances.Count:N0}...");
            }

            var key = plan.GetCellKey(primitiveInstance.Bounds.Center);
            if (!chunks.TryGetValue(key, out var chunkBuilder))
            {
                chunkBuilder = new ChunkBuilder($"chunk_{key.X}_{key.Z}");
                chunks.Add(key, chunkBuilder);
            }

            AppendPrimitive(accessorReader, primitiveInstance, chunkBuilder);
        }

        var mergedChunks = chunks.Values
            .Where(chunk => chunk.VertexCount > 0 && chunk.IndexCount > 0)
            .OrderBy(chunk => chunk.Name, StringComparer.Ordinal)
            .Select(chunk => chunk.Build())
            .ToArray();

        Console.WriteLine($"已合并为 {mergedChunks.Length} 个渲染分块。");

        return new TerrainSceneData
        {
            Bounds = plan.Bounds,
            Chunks = mergedChunks,
            Components = plan.PrimitiveInstances.Select(instance => instance.Component).ToArray(),
            SourceWriteTimeUtcTicks = sourceWriteTimeUtcTicks,
        };
    }

    private static void AppendPrimitive(AccessorReader accessorReader, PrimitiveInstance primitiveInstance, ChunkBuilder chunkBuilder)
    {
        var positions = accessorReader.ReadVector3Accessor(primitiveInstance.PositionAccessorIndex);
        Vector3[]? normals = primitiveInstance.NormalAccessorIndex is int normalAccessor
            ? accessorReader.ReadVector3Accessor(normalAccessor)
            : null;
        Vector4[]? colors = primitiveInstance.ColorAccessorIndex is int colorAccessor
            ? accessorReader.ReadColorAccessor(colorAccessor)
            : null;
        var indices = primitiveInstance.IndicesAccessorIndex is int indicesAccessor
            ? accessorReader.ReadIndices(indicesAccessor)
            : BuildSequentialIndices(positions.Length);

        if (primitiveInstance.Mode != 4)
        {
            throw new NotSupportedException($"Primitive mode {primitiveInstance.Mode} is not supported. Only TRIANGLES is supported.");
        }

        var transformedPositions = new Vector3[positions.Length];
        var transformedNormals = new Vector3[positions.Length];

        Matrix4x4.Invert(primitiveInstance.WorldMatrix, out var inverseWorld);
        var normalMatrix = Matrix4x4.Transpose(inverseWorld);

        for (var i = 0; i < positions.Length; i++)
        {
            transformedPositions[i] = Vector3.Transform(positions[i], primitiveInstance.WorldMatrix);
        }

        if (normals is not null)
        {
            for (var i = 0; i < normals.Length; i++)
            {
                var transformed = Vector3.TransformNormal(normals[i], normalMatrix);
                transformedNormals[i] = transformed.LengthSquared() > 0.0f
                    ? Vector3.Normalize(transformed)
                    : Vector3.UnitY;
            }
        }

        if (primitiveInstance.FlippedWinding)
        {
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            }
        }

        if (normals is null)
        {
            GenerateNormals(transformedPositions, indices, transformedNormals);
        }

        var tint = primitiveInstance.BaseColor;
        var packedVertices = new VertexData[transformedPositions.Length];

        for (var i = 0; i < transformedPositions.Length; i++)
        {
            var vertexColor = colors is not null ? colors[i] : Vector4.One;
            var finalColor = new Vector4(
                tint.X * vertexColor.X,
                tint.Y * vertexColor.Y,
                tint.Z * vertexColor.Z,
                1.0f);

            packedVertices[i] = new VertexData(
                transformedPositions[i],
                transformedNormals[i],
                PackColor(finalColor));
        }

        chunkBuilder.Append(packedVertices, indices, primitiveInstance.Component.Id, primitiveInstance.Bounds);
    }

    private static uint[] BuildSequentialIndices(int vertexCount)
    {
        var indices = new uint[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            indices[i] = (uint)i;
        }

        return indices;
    }

    private static void GenerateNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<uint> indices, Span<Vector3> normals)
    {
        normals.Clear();

        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            var i0 = (int)indices[i];
            var i1 = (int)indices[i + 1];
            var i2 = (int)indices[i + 2];

            var edge1 = positions[i1] - positions[i0];
            var edge2 = positions[i2] - positions[i0];
            var normal = Vector3.Cross(edge1, edge2);
            if (normal.LengthSquared() <= 1e-10f)
            {
                continue;
            }

            normals[i0] += normal;
            normals[i1] += normal;
            normals[i2] += normal;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 0.0f
                ? Vector3.Normalize(normals[i])
                : Vector3.UnitY;
        }
    }

    private static uint PackColor(Vector4 color)
    {
        byte Clamp(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);

        var r = Clamp(color.X);
        var g = Clamp(color.Y);
        var b = Clamp(color.Z);
        var a = (byte)255;

        return (uint)(r | (g << 8) | (b << 16) | (a << 24));
    }

    private sealed class ChunkBuilder
    {
        private readonly List<VertexData> _vertices = new();
        private readonly List<uint> _indices = new();
        private readonly List<ComponentRangeData> _componentRanges = new();
        private BoundingBox _bounds = BoundingBox.CreateEmpty();

        public ChunkBuilder(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int VertexCount => _vertices.Count;

        public int IndexCount => _indices.Count;

        public void Append(ReadOnlySpan<VertexData> vertices, ReadOnlySpan<uint> indices, int componentId, BoundingBox componentBounds)
        {
            var baseVertex = (uint)_vertices.Count;
            var startIndex = _indices.Count;
            foreach (var vertex in vertices)
            {
                _vertices.Add(vertex);
                _bounds.Include(vertex.Position);
            }

            foreach (var index in indices)
            {
                _indices.Add(baseVertex + index);
            }

            _componentRanges.Add(new ComponentRangeData
            {
                ComponentId = componentId,
                StartIndex = startIndex,
                IndexCount = indices.Length,
                Bounds = componentBounds,
            });
        }

        public TerrainChunkData Build()
        {
            return new TerrainChunkData
            {
                Name = Name,
                Bounds = _bounds,
                Vertices = _vertices.ToArray(),
                Indices = _indices.ToArray(),
                ComponentRanges = _componentRanges.ToArray(),
            };
        }
    }

    private sealed class ScenePlanner
    {
        private readonly GltfRoot _root;
        private readonly List<PrimitiveInstance> _primitiveInstances = new();
        private BoundingBox _bounds = BoundingBox.CreateEmpty();

        public ScenePlanner(GltfRoot root)
        {
            _root = root;
        }

        public ScenePlan BuildPlan()
        {
            if (_root.Scenes is null || _root.Scenes.Count == 0 || _root.Nodes is null)
            {
                throw new InvalidDataException("GLB scene graph is empty.");
            }

            var sceneIndex = _root.Scene ?? 0;
            var scene = _root.Scenes[sceneIndex];

            foreach (var nodeIndex in scene.Nodes ?? [])
            {
                TraverseNode(nodeIndex, Matrix4x4.Identity);
            }

            if (!_bounds.IsValid() || _primitiveInstances.Count == 0)
            {
                throw new InvalidDataException("No renderable primitives were found in the GLB.");
            }

            var totalVertices = _primitiveInstances.Sum(instance => instance.EstimatedVertexCount);
            var cellsPerAxis = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(totalVertices / 40000.0f)), 6, 48);

            return new ScenePlan(_bounds, _primitiveInstances, cellsPerAxis);
        }

        private void TraverseNode(int nodeIndex, Matrix4x4 parentWorld)
        {
            var node = _root.Nodes![nodeIndex];
            var local = BuildNodeTransform(node);
            var world = local * parentWorld;

            if (node.Mesh is int meshIndex)
            {
                var mesh = _root.Meshes![meshIndex];

                var primitiveIndex = 0;
                foreach (var primitive in mesh.Primitives ?? [])
                {
                    if (primitive.Attributes is null || !primitive.Attributes.TryGetValue("POSITION", out var positionAccessorIndex))
                    {
                        primitiveIndex++;
                        continue;
                    }

                    var positionAccessor = _root.Accessors![positionAccessorIndex];
                    var localBounds = positionAccessor.TryGetBounds();
                    if (localBounds is null)
                    {
                        primitiveIndex++;
                        continue;
                    }

                    var worldBounds = BoundingBox.Transform(localBounds.Value, world);
                    _bounds.Include(worldBounds);

                    var material = primitive.Material is int materialIndex && _root.Materials is not null
                        ? _root.Materials[materialIndex]
                        : null;
                    var componentId = _primitiveInstances.Count;
                    var componentName = BuildComponentName(componentId, node, mesh, nodeIndex, meshIndex, primitiveIndex);

                    _primitiveInstances.Add(new PrimitiveInstance
                    {
                        Component = new ComponentData
                        {
                            Id = componentId,
                            NodeIndex = nodeIndex,
                            MeshIndex = meshIndex,
                            PrimitiveIndex = primitiveIndex,
                            Name = componentName,
                            Bounds = worldBounds,
                        },
                        Bounds = worldBounds,
                        WorldMatrix = world,
                        FlippedWinding = world.GetDeterminant() < 0.0f,
                        PositionAccessorIndex = positionAccessorIndex,
                        NormalAccessorIndex = primitive.Attributes.TryGetValue("NORMAL", out var normalAccessorIndex) ? normalAccessorIndex : null,
                        ColorAccessorIndex = primitive.Attributes.TryGetValue("COLOR_0", out var colorAccessorIndex) ? colorAccessorIndex : null,
                        IndicesAccessorIndex = primitive.Indices,
                        BaseColor = material?.PbrMetallicRoughness?.BaseColorFactorVector ?? Vector4.One,
                        Mode = primitive.Mode ?? 4,
                        EstimatedVertexCount = positionAccessor.Count,
                    });

                    primitiveIndex++;
                }
            }

            foreach (var childIndex in node.Children ?? [])
            {
                TraverseNode(childIndex, world);
            }
        }

        private static string BuildComponentName(int componentId, GltfNode node, GltfMesh mesh, int nodeIndex, int meshIndex, int primitiveIndex)
        {
            var nodeName = string.IsNullOrWhiteSpace(node.Name) ? $"node_{nodeIndex}" : node.Name;
            var meshName = string.IsNullOrWhiteSpace(mesh.Name) ? $"mesh_{meshIndex}" : mesh.Name;
            return $"{componentId}:{nodeName}/{meshName}/primitive_{primitiveIndex}";
        }

        private static Matrix4x4 BuildNodeTransform(GltfNode node)
        {
            if (node.Matrix is { Length: 16 } matrix)
            {
                return new Matrix4x4(
                    matrix[0], matrix[1], matrix[2], matrix[3],
                    matrix[4], matrix[5], matrix[6], matrix[7],
                    matrix[8], matrix[9], matrix[10], matrix[11],
                    matrix[12], matrix[13], matrix[14], matrix[15]);
            }

            var scale = node.ScaleVector ?? Vector3.One;
            var rotation = node.RotationQuaternion ?? Quaternion.Identity;
            var translation = node.TranslationVector ?? Vector3.Zero;

            return Matrix4x4.CreateScale(scale) *
                   Matrix4x4.CreateFromQuaternion(rotation) *
                   Matrix4x4.CreateTranslation(translation);
        }
    }

    private sealed record ScenePlan(BoundingBox Bounds, IReadOnlyList<PrimitiveInstance> PrimitiveInstances, int CellsPerAxis)
    {
        public (int X, int Z) GetCellKey(Vector3 center)
        {
            var size = Bounds.Size;
            var cellSizeX = size.X <= 0.001f ? 1.0f : size.X / CellsPerAxis;
            var cellSizeZ = size.Z <= 0.001f ? 1.0f : size.Z / CellsPerAxis;

            var x = Math.Clamp((int)((center.X - Bounds.Min.X) / cellSizeX), 0, CellsPerAxis - 1);
            var z = Math.Clamp((int)((center.Z - Bounds.Min.Z) / cellSizeZ), 0, CellsPerAxis - 1);
            return (x, z);
        }
    }

    private sealed class PrimitiveInstance
    {
        public required ComponentData Component { get; init; }
        public required BoundingBox Bounds { get; init; }
        public required Matrix4x4 WorldMatrix { get; init; }
        public required bool FlippedWinding { get; init; }
        public required int PositionAccessorIndex { get; init; }
        public required int? NormalAccessorIndex { get; init; }
        public required int? ColorAccessorIndex { get; init; }
        public required int? IndicesAccessorIndex { get; init; }
        public required Vector4 BaseColor { get; init; }
        public required int Mode { get; init; }
        public required int EstimatedVertexCount { get; init; }
    }

    private sealed class AccessorReader
    {
        private readonly GltfRoot _root;
        private readonly byte[] _binaryChunk;

        public AccessorReader(GltfRoot root, byte[] binaryChunk)
        {
            _root = root;
            _binaryChunk = binaryChunk;
        }

        public Vector3[] ReadVector3Accessor(int accessorIndex)
        {
            var accessor = _root.Accessors![accessorIndex];
            EnsureAccessorType(accessor, "VEC3");

            var values = new Vector3[accessor.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var element = ReadElement(accessor, i);
                values[i] = new Vector3(
                    ReadComponentAsFloat(element, accessor, 0),
                    ReadComponentAsFloat(element, accessor, 1),
                    ReadComponentAsFloat(element, accessor, 2));
            }

            return values;
        }

        public Vector4[] ReadColorAccessor(int accessorIndex)
        {
            var accessor = _root.Accessors![accessorIndex];
            var values = new Vector4[accessor.Count];

            if (accessor.Type == "VEC3")
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var element = ReadElement(accessor, i);
                    values[i] = new Vector4(
                        ReadComponentAsFloat(element, accessor, 0),
                        ReadComponentAsFloat(element, accessor, 1),
                        ReadComponentAsFloat(element, accessor, 2),
                        1.0f);
                }

                return values;
            }

            EnsureAccessorType(accessor, "VEC4");

            for (var i = 0; i < values.Length; i++)
            {
                var element = ReadElement(accessor, i);
                values[i] = new Vector4(
                    ReadComponentAsFloat(element, accessor, 0),
                    ReadComponentAsFloat(element, accessor, 1),
                    ReadComponentAsFloat(element, accessor, 2),
                    ReadComponentAsFloat(element, accessor, 3));
            }

            return values;
        }

        public uint[] ReadIndices(int accessorIndex)
        {
            var accessor = _root.Accessors![accessorIndex];
            EnsureAccessorType(accessor, "SCALAR");

            var values = new uint[accessor.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var element = ReadElement(accessor, i);
                values[i] = accessor.ComponentType switch
                {
                    5121 => element[0],
                    5123 => BitConverter.ToUInt16(element[..2]),
                    5125 => BitConverter.ToUInt32(element[..4]),
                    _ => throw new NotSupportedException($"Unsupported index component type {accessor.ComponentType}."),
                };
            }

            return values;
        }

        private Span<byte> ReadElement(GltfAccessor accessor, int index)
        {
            if (accessor.BufferView is null)
            {
                throw new NotSupportedException("Sparse accessors without a bufferView are not supported.");
            }

            var bufferView = _root.BufferViews![accessor.BufferView.Value];
            var componentSize = GetComponentSize(accessor.ComponentType);
            var componentCount = GetComponentCount(accessor.Type);
            var packedSize = componentSize * componentCount;
            var stride = bufferView.ByteStride ?? packedSize;
            var baseOffset = (bufferView.ByteOffset ?? 0) + (accessor.ByteOffset ?? 0) + (index * stride);

            return _binaryChunk.AsSpan(baseOffset, packedSize);
        }

        private static float ReadComponentAsFloat(ReadOnlySpan<byte> element, GltfAccessor accessor, int componentIndex)
        {
            var componentSize = GetComponentSize(accessor.ComponentType);
            var slice = element.Slice(componentIndex * componentSize, componentSize);

            return accessor.ComponentType switch
            {
                5126 => BitConverter.ToSingle(slice),
                5120 => ConvertSignedIntegerToFloat(MemoryMarshal.Read<sbyte>(slice), accessor.Normalized),
                5121 => ConvertUnsignedIntegerToFloat(slice[0], accessor.Normalized),
                5122 => ConvertSignedIntegerToFloat(BitConverter.ToInt16(slice), accessor.Normalized),
                5123 => ConvertUnsignedIntegerToFloat(BitConverter.ToUInt16(slice), accessor.Normalized),
                5125 => ConvertUnsignedIntegerToFloat(BitConverter.ToUInt32(slice), accessor.Normalized),
                _ => throw new NotSupportedException($"Unsupported accessor component type {accessor.ComponentType}."),
            };
        }

        private static float ConvertSignedIntegerToFloat(long value, bool normalized)
        {
            if (!normalized)
            {
                return value;
            }

            return value switch
            {
                >= sbyte.MinValue and <= sbyte.MaxValue => Math.Clamp(value / 127.0f, -1.0f, 1.0f),
                >= short.MinValue and <= short.MaxValue => Math.Clamp(value / 32767.0f, -1.0f, 1.0f),
                _ => value,
            };
        }

        private static float ConvertUnsignedIntegerToFloat(ulong value, bool normalized)
        {
            if (!normalized)
            {
                return value;
            }

            return value switch
            {
                <= byte.MaxValue => value / 255.0f,
                <= ushort.MaxValue => value / 65535.0f,
                <= uint.MaxValue => value / (float)uint.MaxValue,
                _ => value,
            };
        }

        private static void EnsureAccessorType(GltfAccessor accessor, string expectedType)
        {
            if (!string.Equals(accessor.Type, expectedType, StringComparison.Ordinal))
            {
                throw new NotSupportedException($"Accessor type mismatch. Expected {expectedType}, got {accessor.Type}.");
            }
        }

        private static int GetComponentCount(string type)
        {
            return type switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                "MAT4" => 16,
                _ => throw new NotSupportedException($"Unsupported accessor type {type}."),
            };
        }

        private static int GetComponentSize(int componentType)
        {
            return componentType switch
            {
                5120 => 1,
                5121 => 1,
                5122 => 2,
                5123 => 2,
                5125 => 4,
                5126 => 4,
                _ => throw new NotSupportedException($"Unsupported component type {componentType}."),
            };
        }
    }

    private sealed class GlbFile
    {
        public required GltfRoot Root { get; init; }
        public required byte[] BinaryChunk { get; init; }

        public static GlbFile Load(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            var magic = reader.ReadUInt32();
            if (magic != 0x46546C67)
            {
                throw new InvalidDataException("Not a valid GLB file.");
            }

            var version = reader.ReadUInt32();
            if (version != 2)
            {
                throw new InvalidDataException($"Unsupported GLB version {version}.");
            }

            var length = reader.ReadUInt32();
            _ = length;

            string? jsonText = null;
            byte[]? binaryChunk = null;

            while (stream.Position < stream.Length)
            {
                var chunkLength = reader.ReadInt32();
                var chunkType = reader.ReadUInt32();
                var chunkBytes = reader.ReadBytes(chunkLength);

                switch (chunkType)
                {
                    case 0x4E4F534A:
                        jsonText = Encoding.UTF8.GetString(chunkBytes).TrimEnd('\0', ' ');
                        break;
                    case 0x004E4942:
                        binaryChunk = chunkBytes;
                        break;
                }
            }

            if (jsonText is null || binaryChunk is null)
            {
                throw new InvalidDataException("GLB must contain both JSON and BIN chunks.");
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            var root = JsonSerializer.Deserialize<GltfRoot>(jsonText, options)
                ?? throw new InvalidDataException("Failed to deserialize GLB JSON.");

            return new GlbFile
            {
                Root = root,
                BinaryChunk = binaryChunk,
            };
        }
    }

    private sealed class GltfRoot
    {
        [JsonPropertyName("scene")]
        public int? Scene { get; set; }

        [JsonPropertyName("scenes")]
        public List<GltfScene>? Scenes { get; set; }

        [JsonPropertyName("nodes")]
        public List<GltfNode>? Nodes { get; set; }

        [JsonPropertyName("meshes")]
        public List<GltfMesh>? Meshes { get; set; }

        [JsonPropertyName("accessors")]
        public List<GltfAccessor>? Accessors { get; set; }

        [JsonPropertyName("bufferViews")]
        public List<GltfBufferView>? BufferViews { get; set; }

        [JsonPropertyName("materials")]
        public List<GltfMaterial>? Materials { get; set; }
    }

    private sealed class GltfScene
    {
        [JsonPropertyName("nodes")]
        public int[]? Nodes { get; set; }
    }

    private sealed class GltfNode
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("mesh")]
        public int? Mesh { get; set; }

        [JsonPropertyName("children")]
        public int[]? Children { get; set; }

        [JsonPropertyName("matrix")]
        public float[]? Matrix { get; set; }

        [JsonPropertyName("translation")]
        public float[]? Translation { get; set; }

        [JsonPropertyName("rotation")]
        public float[]? Rotation { get; set; }

        [JsonPropertyName("scale")]
        public float[]? Scale { get; set; }

        [JsonIgnore]
        public Vector3? TranslationVector => Translation is { Length: 3 }
            ? new Vector3(Translation[0], Translation[1], Translation[2])
            : null;

        [JsonIgnore]
        public Quaternion? RotationQuaternion => Rotation is { Length: 4 }
            ? new Quaternion(Rotation[0], Rotation[1], Rotation[2], Rotation[3])
            : null;

        [JsonIgnore]
        public Vector3? ScaleVector => Scale is { Length: 3 }
            ? new Vector3(Scale[0], Scale[1], Scale[2])
            : null;
    }

    private sealed class GltfMesh
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("primitives")]
        public List<GltfPrimitive>? Primitives { get; set; }
    }

    private sealed class GltfPrimitive
    {
        [JsonPropertyName("attributes")]
        public Dictionary<string, int>? Attributes { get; set; }

        [JsonPropertyName("indices")]
        public int? Indices { get; set; }

        [JsonPropertyName("material")]
        public int? Material { get; set; }

        [JsonPropertyName("mode")]
        public int? Mode { get; set; }
    }

    private sealed class GltfAccessor
    {
        [JsonPropertyName("bufferView")]
        public int? BufferView { get; set; }

        [JsonPropertyName("byteOffset")]
        public int? ByteOffset { get; set; }

        [JsonPropertyName("componentType")]
        public int ComponentType { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "SCALAR";

        [JsonPropertyName("normalized")]
        public bool Normalized { get; set; }

        [JsonPropertyName("min")]
        public float[]? Min { get; set; }

        [JsonPropertyName("max")]
        public float[]? Max { get; set; }

        public BoundingBox? TryGetBounds()
        {
            if (Type != "VEC3" || Min is not { Length: 3 } || Max is not { Length: 3 })
            {
                return null;
            }

            return new BoundingBox(
                new Vector3(Min[0], Min[1], Min[2]),
                new Vector3(Max[0], Max[1], Max[2]));
        }
    }

    private sealed class GltfBufferView
    {
        [JsonPropertyName("byteOffset")]
        public int? ByteOffset { get; set; }

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; set; }

        [JsonPropertyName("byteStride")]
        public int? ByteStride { get; set; }
    }

    private sealed class GltfMaterial
    {
        [JsonPropertyName("pbrMetallicRoughness")]
        public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
    }

    private sealed class GltfPbrMetallicRoughness
    {
        [JsonPropertyName("baseColorFactor")]
        public float[]? BaseColorFactor { get; set; }

        [JsonIgnore]
        public Vector4 BaseColorFactorVector => BaseColorFactor is { Length: 4 }
            ? new Vector4(BaseColorFactor[0], BaseColorFactor[1], BaseColorFactor[2], BaseColorFactor[3])
            : Vector4.One;
    }
}
