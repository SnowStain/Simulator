using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Simulator.Core;

namespace Simulator.ThreeD;

internal sealed class FineTerrainAnnotationDocument
{
    private const int ComponentShardSize = 50000;
    private const string ComponentShardDirectorySuffix = ".parts";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public required string SourcePath { get; init; }

    public string SourceModel { get; init; } = string.Empty;

    public DateTimeOffset ExportedUtc { get; init; }

    public int TotalComponents { get; init; }

    public required FineTerrainWorldScale WorldScale { get; init; }

    public required List<int> ActorComponentIds { get; init; }

    public required List<FineTerrainCompositeAnnotation> Composites { get; init; }

    public required List<FineTerrainComponentAnnotation> Components { get; init; }

    public required List<FineTerrainCollisionShapeAnnotation> CollisionShapes { get; init; }

    public IReadOnlyDictionary<int, FineTerrainComponentAnnotation> ComponentsById
        => _componentsById ??= Components.ToDictionary(component => component.Id);

    private Dictionary<int, FineTerrainComponentAnnotation>? _componentsById;

    public static FineTerrainAnnotationDocument? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        FineTerrainAnnotationPayload? payload;
        try
        {
            using FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            payload = JsonSerializer.Deserialize<FineTerrainAnnotationPayload>(stream, JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SimulatorRuntimeLog.Append(
                "terrain_annotation_load.log",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} load_failed path={path} {exception.GetType().Name}:{exception.Message}");
            return null;
        }

        if (payload?.WorldScale is null)
        {
            return null;
        }

        FineTerrainComponentAnnotation[] components = LoadComponents(path, payload)
            .Select(CloneComponent)
            .ToArray();
        Vector3 modelCenter = payload.WorldScale.ModelCenter?.ToVector3()
            ?? ResolveModelCenter(components);
        FineTerrainWorldScale worldScale = new(
            payload.WorldScale.MapLengthXMeters,
            payload.WorldScale.MapLengthZMeters,
            payload.WorldScale.XMetersPerModelUnit,
            payload.WorldScale.YMetersPerModelUnit,
            payload.WorldScale.ZMetersPerModelUnit,
            modelCenter,
            payload.WorldScale.ModelMinY ?? modelCenter.Y);

        return new FineTerrainAnnotationDocument
        {
            SourcePath = Path.GetFullPath(path),
            SourceModel = payload.SourceModel ?? string.Empty,
            ExportedUtc = payload.ExportedUtc,
            TotalComponents = payload.TotalComponents > 0
                ? payload.TotalComponents
                : components.Length,
            WorldScale = worldScale,
            ActorComponentIds = payload.ActorComponentIds?.ToList() ?? new List<int>(),
            Composites = (payload.Composites ?? Array.Empty<FineTerrainCompositePayload>())
                .Select(composite => CloneComposite(composite, worldScale))
                .ToList(),
            Components = components.ToList(),
            CollisionShapes = ((payload.CollisionShapes ?? payload.CollisionShapesSnakeCase) ?? Array.Empty<FineTerrainCollisionShapePayload>())
                .Where(shape => shape.Id > 0)
                .Select(CloneCollisionShape)
                .ToList(),
        };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SourcePath) ?? AppContext.BaseDirectory);
        using FileStream stream = File.Create(SourcePath);
        var payload = new FineTerrainAnnotationPayload
        {
            SourceModel = SourceModel,
            ExportedUtc = DateTimeOffset.UtcNow,
            TotalComponents = Math.Max(TotalComponents, Components.Count),
            WorldScale = new FineTerrainWorldScalePayload
            {
                MapLengthXMeters = WorldScale.MapLengthXMeters,
                MapLengthZMeters = WorldScale.MapLengthZMeters,
                XMetersPerModelUnit = WorldScale.XMetersPerModelUnit,
                YMetersPerModelUnit = WorldScale.YMetersPerModelUnit,
                ZMetersPerModelUnit = WorldScale.ZMetersPerModelUnit,
                ModelCenter = FineTerrainVector3.From(WorldScale.ModelCenter),
                ModelMinY = WorldScale.ModelMinY,
            },
            ActorComponentIds = ActorComponentIds.OrderBy(id => id).ToArray(),
            Composites = Composites
                .OrderBy(composite => composite.Id)
                .Select(CloneCompositePayload)
                .ToArray(),
            Components = Components
                .OrderBy(component => component.Id)
                .Select(CloneComponent)
                .ToArray(),
            CollisionShapes = CollisionShapes
                .OrderBy(shape => shape.Id)
                .Select(CloneCollisionShapePayload)
                .ToArray(),
        };

        FineTerrainComponentAnnotation[] orderedComponents = payload.Components;
        string[] componentFiles = WriteComponentShardsIfNeeded(SourcePath, orderedComponents);
        if (componentFiles.Length > 0)
        {
            payload = new FineTerrainAnnotationPayload
            {
                SourceModel = payload.SourceModel,
                ExportedUtc = payload.ExportedUtc,
                TotalComponents = payload.TotalComponents,
                WorldScale = payload.WorldScale,
                ActorComponentIds = payload.ActorComponentIds,
                Composites = payload.Composites,
                Components = null,
                ComponentFiles = componentFiles,
                CollisionShapes = payload.CollisionShapes,
            };
        }

        JsonSerializer.Serialize(stream, payload, JsonOptions);
    }

    private static IEnumerable<FineTerrainComponentAnnotation> LoadComponents(
        string manifestPath,
        FineTerrainAnnotationPayload payload)
    {
        if (payload.Components is not null)
        {
            foreach (FineTerrainComponentAnnotation component in payload.Components)
            {
                yield return component;
            }
        }

        if (payload.ComponentFiles is null || payload.ComponentFiles.Length == 0)
        {
            yield break;
        }

        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        foreach (string relativePath in payload.ComponentFiles)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            string shardPath = Path.GetFullPath(Path.Combine(manifestDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(shardPath))
            {
                continue;
            }

            using FileStream stream = File.Open(shardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            FineTerrainComponentAnnotation[]? shard = JsonSerializer.Deserialize<FineTerrainComponentAnnotation[]>(stream, JsonOptions);
            if (shard is null)
            {
                continue;
            }

            foreach (FineTerrainComponentAnnotation component in shard)
            {
                yield return component;
            }
        }
    }

    private static string[] WriteComponentShardsIfNeeded(
        string manifestPath,
        IReadOnlyList<FineTerrainComponentAnnotation> components)
    {
        if (components.Count <= ComponentShardSize)
        {
            return Array.Empty<string>();
        }

        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        string shardDirectoryName = Path.GetFileNameWithoutExtension(manifestPath) + ComponentShardDirectorySuffix;
        string shardDirectory = Path.Combine(manifestDirectory, shardDirectoryName);
        Directory.CreateDirectory(shardDirectory);

        foreach (string staleShard in Directory.EnumerateFiles(shardDirectory, "components_*.json", SearchOption.TopDirectoryOnly))
        {
            File.Delete(staleShard);
        }

        var relativeFiles = new List<string>((components.Count + ComponentShardSize - 1) / ComponentShardSize);
        for (int start = 0, shardIndex = 0; start < components.Count; start += ComponentShardSize, shardIndex++)
        {
            int count = Math.Min(ComponentShardSize, components.Count - start);
            FineTerrainComponentAnnotation[] shard = components.Skip(start).Take(count).ToArray();
            string shardPath = Path.Combine(shardDirectory, $"components_{shardIndex:000}.json");
            using (FileStream shardStream = File.Create(shardPath))
            {
                JsonSerializer.Serialize(shardStream, shard, JsonOptions);
            }

            relativeFiles.Add(Path.GetRelativePath(manifestDirectory, shardPath).Replace('\\', '/'));
        }

        return relativeFiles.ToArray();
    }

    private static FineTerrainCompositeAnnotation CloneComposite(
        FineTerrainCompositePayload source,
        FineTerrainWorldScale worldScale)
    {
        Vector3 rotationYprDegrees = source.YprDegrees?.ToVector3() ?? Vector3.Zero;
        Vector3 positionModel = source.PositionModel?.ToVector3()
            ?? (source.PositionMeters is not null
                ? MetersToModel(source.PositionMeters.ToVector3(), worldScale)
                : Vector3.Zero);
        Vector3 pivotModel = source.PivotModel?.ToVector3() ?? positionModel;
        Vector3 coordinateYprDegrees = source.CoordinateYprDegrees?.ToVector3() ?? rotationYprDegrees;
        return new FineTerrainCompositeAnnotation
        {
            Id = source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? $"组合体 {source.Id}" : source.Name,
            Role = string.IsNullOrWhiteSpace(source.Role) ? "actor" : source.Role,
            CoordinateSystemMode = NormalizeCoordinateSystemMode(source.CoordinateSystemMode),
            ComponentIds = source.ComponentIds ?? Array.Empty<int>(),
            InteractionUnits = (source.InteractionUnits ?? Array.Empty<FineTerrainInteractionUnitPayload>())
                .Select(unit => new FineTerrainInteractionUnitAnnotation
                {
                    Id = unit.Id,
                    Name = string.IsNullOrWhiteSpace(unit.Name) ? $"互动单元 {unit.Id}" : unit.Name,
                    ComponentIds = unit.ComponentIds ?? Array.Empty<int>(),
                })
                .ToArray(),
            PositionMeters = FineTerrainVector3.From(ModelToMeters(positionModel, worldScale)),
            PositionModel = FineTerrainVector3.From(positionModel),
            PivotModel = FineTerrainVector3.From(pivotModel),
            YprDegrees = FineTerrainVector3.From(rotationYprDegrees),
            CoordinateYprDegrees = FineTerrainVector3.From(coordinateYprDegrees),
        };
    }

    private static FineTerrainCompositePayload CloneCompositePayload(FineTerrainCompositeAnnotation source)
    {
        return new FineTerrainCompositePayload
        {
            Id = source.Id,
            Name = source.Name,
            Role = source.Role,
            CoordinateSystemMode = NormalizeCoordinateSystemMode(source.CoordinateSystemMode),
            ComponentIds = source.ComponentIds.ToArray(),
            InteractionUnits = source.InteractionUnits
                .Select(unit => new FineTerrainInteractionUnitPayload
                {
                    Id = unit.Id,
                    Name = unit.Name,
                    ComponentIds = unit.ComponentIds.ToArray(),
                })
                .ToArray(),
            PositionMeters = source.PositionMeters,
            PositionModel = source.PositionModel,
            PivotModel = source.PivotModel,
            YprDegrees = source.YprDegrees,
            CoordinateYprDegrees = source.CoordinateYprDegrees,
        };
    }

    private static FineTerrainComponentAnnotation CloneComponent(FineTerrainComponentAnnotation source)
    {
        return new FineTerrainComponentAnnotation
        {
            Id = source.Id,
            NodeIndex = source.NodeIndex,
            MeshIndex = source.MeshIndex,
            PrimitiveIndex = source.PrimitiveIndex,
            Name = source.Name,
            Role = source.Role,
            Bounds = source.Bounds,
        };
    }

    private static FineTerrainCollisionShapeAnnotation CloneCollisionShape(FineTerrainCollisionShapePayload source)
    {
        string shapeType = !string.IsNullOrWhiteSpace(source.ShapeType)
            ? source.ShapeType!
            : source.ShapeTypePascal ?? string.Empty;
        if (string.IsNullOrWhiteSpace(shapeType))
        {
            shapeType = "box";
        }

        FineTerrainVector3 sizeModel = source.SizeModel ?? source.SizeModelPascal ?? FineTerrainVector3.From(Vector3.One);
        Vector3 size = sizeModel.ToVector3();
        bool radialShape = shapeType.Equals("cylinder", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("hex_prism", StringComparison.OrdinalIgnoreCase)
            || shapeType.Equals("hexagon_prism", StringComparison.OrdinalIgnoreCase);
        float radius = source.RadiusModel > 0.0f
            ? source.RadiusModel
            : source.RadiusModelPascal > 0.0f
                ? source.RadiusModelPascal
                : (radialShape
                        ? MathF.Max(0.02f, MathF.Max(MathF.Abs(size.X), MathF.Abs(size.Z)) * 0.5f)
                        : 1.0f);
        float rawHeight = source.HeightModel > 0.0f
            ? source.HeightModel
            : source.HeightModelPascal > 0.0f
                ? source.HeightModelPascal
                : 0.0f;
        float sizeHeight = MathF.Abs(size.Y);
        float height = !radialShape && sizeHeight > 0.02f && (rawHeight <= 0.02f || rawHeight < sizeHeight * 0.25f)
            ? sizeHeight
            : rawHeight > 0.02f
                ? rawHeight
                : sizeHeight > 0.02f
                    ? sizeHeight
                    : 1.0f;

        return new FineTerrainCollisionShapeAnnotation
        {
            Id = source.Id,
            Name = string.IsNullOrWhiteSpace(source.Name) ? $"Collision {source.Id}" : source.Name,
            ShapeType = shapeType.Trim(),
            PositionModel = source.PositionModel ?? source.PositionModelPascal ?? new FineTerrainVector3(),
            SizeModel = sizeModel,
            RadiusModel = radius,
            HeightModel = height,
            YprDegrees = source.YprDegrees ?? source.YprDegreesPascal ?? new FineTerrainVector3(),
            TerrainLabel = (source.TerrainLabel ?? source.TerrainLabelPascal)?.Trim() ?? string.Empty,
            VerticesModel = (source.VerticesModel ?? source.VerticesModelPascal ?? Array.Empty<FineTerrainVector3>())
                .Select(vertex => FineTerrainVector3.From(vertex.ToVector3()))
                .ToList(),
        };
    }

    private static FineTerrainCollisionShapePayload CloneCollisionShapePayload(FineTerrainCollisionShapeAnnotation source)
    {
        return new FineTerrainCollisionShapePayload
        {
            Id = source.Id,
            Name = source.Name,
            ShapeType = source.ShapeType,
            PositionModel = source.PositionModel,
            SizeModel = source.SizeModel,
            RadiusModel = source.RadiusModel,
            HeightModel = source.HeightModel,
            YprDegrees = source.YprDegrees,
            TerrainLabel = source.TerrainLabel,
            VerticesModel = source.VerticesModel
                .Select(vertex => FineTerrainVector3.From(vertex.ToVector3()))
                .ToArray(),
        };
    }

    private static Vector3 ResolveModelCenter(IReadOnlyList<FineTerrainComponentAnnotation> components)
    {
        if (components.Count == 0)
        {
            return Vector3.Zero;
        }

        bool initialized = false;
        Vector3 min = Vector3.Zero;
        Vector3 max = Vector3.Zero;
        foreach (FineTerrainComponentAnnotation component in components)
        {
            if (component.Bounds.Min.Length < 3 || component.Bounds.Max.Length < 3)
            {
                continue;
            }

            Vector3 componentMin = new(component.Bounds.Min[0], component.Bounds.Min[1], component.Bounds.Min[2]);
            Vector3 componentMax = new(component.Bounds.Max[0], component.Bounds.Max[1], component.Bounds.Max[2]);
            if (!initialized)
            {
                min = componentMin;
                max = componentMax;
                initialized = true;
                continue;
            }

            min = Vector3.Min(min, componentMin);
            max = Vector3.Max(max, componentMax);
        }

        return initialized ? (min + max) * 0.5f : Vector3.Zero;
    }

    private static Vector3 ModelToMeters(Vector3 modelPosition, FineTerrainWorldScale worldScale)
    {
        Vector3 center = worldScale.ModelCenter;
        return new Vector3(
            (modelPosition.X - center.X) * worldScale.XMetersPerModelUnit,
            (modelPosition.Y - center.Y) * worldScale.YMetersPerModelUnit,
            (modelPosition.Z - center.Z) * worldScale.ZMetersPerModelUnit);
    }

    private static Vector3 MetersToModel(Vector3 meterPosition, FineTerrainWorldScale worldScale)
    {
        Vector3 center = worldScale.ModelCenter;
        return new Vector3(
            center.X + meterPosition.X / MathF.Max(worldScale.XMetersPerModelUnit, 1e-6f),
            center.Y + meterPosition.Y / MathF.Max(worldScale.YMetersPerModelUnit, 1e-6f),
            center.Z + meterPosition.Z / MathF.Max(worldScale.ZMetersPerModelUnit, 1e-6f));
    }

    private static string NormalizeCoordinateSystemMode(string? raw)
    {
        return string.Equals(raw, "custom", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "local", StringComparison.OrdinalIgnoreCase)
            ? "custom"
            : "world";
    }
}

internal readonly record struct FineTerrainWorldScale(
    float MapLengthXMeters,
    float MapLengthZMeters,
    float XMetersPerModelUnit,
    float YMetersPerModelUnit,
    float ZMetersPerModelUnit,
    Vector3 ModelCenter,
    float ModelMinY);

internal sealed class FineTerrainCompositeAnnotation
{
    public int Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = "actor";

    public string CoordinateSystemMode { get; set; } = "world";

    public int[] ComponentIds { get; set; } = Array.Empty<int>();

    public FineTerrainInteractionUnitAnnotation[] InteractionUnits { get; set; } = Array.Empty<FineTerrainInteractionUnitAnnotation>();

    public FineTerrainVector3 PositionMeters { get; set; } = new();

    public FineTerrainVector3 PositionModel { get; set; } = new();

    public FineTerrainVector3 PivotModel { get; set; } = new();

    public FineTerrainVector3 YprDegrees { get; set; } = new();

    public FineTerrainVector3 CoordinateYprDegrees { get; set; } = new();
}

internal sealed class FineTerrainInteractionUnitAnnotation
{
    public int Id { get; init; }

    public string Name { get; set; } = string.Empty;

    public int[] ComponentIds { get; set; } = Array.Empty<int>();
}

internal sealed class FineTerrainComponentAnnotation
{
    public int Id { get; init; }

    public int NodeIndex { get; init; }

    public int MeshIndex { get; init; }

    public int PrimitiveIndex { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Role { get; init; } = "static";

    public FineTerrainBoundsAnnotation Bounds { get; init; } = new();
}

internal sealed class FineTerrainBoundsAnnotation
{
    public float[] Min { get; init; } = Array.Empty<float>();

    public float[] Max { get; init; } = Array.Empty<float>();
}

internal sealed class FineTerrainCollisionShapeAnnotation
{
    [ReadOnly(true)]
    [DisplayName("ID")]
    public int Id { get; init; }

    [DisplayName("名称")]
    public string Name { get; set; } = string.Empty;

    [DisplayName("形状类型")]
    [Description("box/quad_prism 为长方体，cylinder 为圆柱。")]
    public string ShapeType { get; set; } = "box";

    [DisplayName("位置 XYZ")]
    [Description("碰撞体中心点，单位为地图模型坐标。")]
    public FineTerrainVector3 PositionModel { get; set; } = new();

    [DisplayName("长宽高 XYZ")]
    [Description("长方体尺寸，单位为地图模型坐标。圆柱只使用高度和半径。")]
    public FineTerrainVector3 SizeModel { get; set; } = FineTerrainVector3.From(Vector3.One);

    [DisplayName("圆柱半径")]
    [Description("圆柱半径，单位为地图模型坐标。")]
    public float RadiusModel { get; set; } = 1.0f;

    [DisplayName("圆柱高度")]
    [Description("圆柱高度，单位为地图模型坐标。")]
    public float HeightModel { get; set; } = 1.0f;

    [DisplayName("旋转 YPR")]
    [Description("Yaw/Pitch/Roll，单位为度。")]
    public FineTerrainVector3 YprDegrees { get; set; } = new();

    [DisplayName("地形标签")]
    public string TerrainLabel { get; set; } = string.Empty;

    [Browsable(false)]
    public List<FineTerrainVector3> VerticesModel { get; set; } = new();
}

internal sealed class FineTerrainVector3
{
    [DisplayName("X")]
    public float X { get; set; }

    [DisplayName("Y")]
    public float Y { get; set; }

    [DisplayName("Z")]
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);

    public static FineTerrainVector3 From(Vector3 value)
        => new()
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z,
        };
}

internal sealed class FineTerrainCompositePropertyView
{
    private readonly FineTerrainCompositeAnnotation _model;

    public FineTerrainCompositePropertyView(FineTerrainCompositeAnnotation model)
    {
        _model = model;
    }

    [ReadOnly(true)]
    [DisplayName("ID")]
    public int Id => _model.Id;

    [DisplayName("Name")]
    public string Name
    {
        get => _model.Name;
        set => _model.Name = value ?? string.Empty;
    }

    [DisplayName("Role")]
    public string Role
    {
        get => _model.Role;
        set => _model.Role = string.IsNullOrWhiteSpace(value) ? "actor" : value.Trim();
    }

    [ReadOnly(true)]
    [DisplayName("Component Count")]
    public int ComponentCount => _model.ComponentIds.Length;

    [ReadOnly(true)]
    [DisplayName("Interaction Unit Count")]
    public int InteractionUnitCount => _model.InteractionUnits.Length;

    [DisplayName("Coordinate Mode")]
    public string CoordinateSystemMode
    {
        get => _model.CoordinateSystemMode;
        set => _model.CoordinateSystemMode = string.IsNullOrWhiteSpace(value) ? "world" : value.Trim();
    }

    [Category("Position Model")]
    public float PositionModelX
    {
        get => _model.PositionModel.X;
        set => _model.PositionModel.X = value;
    }

    [Category("Position Model")]
    public float PositionModelY
    {
        get => _model.PositionModel.Y;
        set => _model.PositionModel.Y = value;
    }

    [Category("Position Model")]
    public float PositionModelZ
    {
        get => _model.PositionModel.Z;
        set => _model.PositionModel.Z = value;
    }

    [Category("Pivot Model")]
    public float PivotModelX
    {
        get => _model.PivotModel.X;
        set => _model.PivotModel.X = value;
    }

    [Category("Pivot Model")]
    public float PivotModelY
    {
        get => _model.PivotModel.Y;
        set => _model.PivotModel.Y = value;
    }

    [Category("Pivot Model")]
    public float PivotModelZ
    {
        get => _model.PivotModel.Z;
        set => _model.PivotModel.Z = value;
    }

    [Category("Rotation YPR")]
    public float RotationYawDeg
    {
        get => _model.YprDegrees.X;
        set => _model.YprDegrees.X = value;
    }

    [Category("Rotation YPR")]
    public float RotationPitchDeg
    {
        get => _model.YprDegrees.Y;
        set => _model.YprDegrees.Y = value;
    }

    [Category("Rotation YPR")]
    public float RotationRollDeg
    {
        get => _model.YprDegrees.Z;
        set => _model.YprDegrees.Z = value;
    }

    [Category("Coordinate YPR")]
    public float CoordinateYawDeg
    {
        get => _model.CoordinateYprDegrees.X;
        set => _model.CoordinateYprDegrees.X = value;
    }

    [Category("Coordinate YPR")]
    public float CoordinatePitchDeg
    {
        get => _model.CoordinateYprDegrees.Y;
        set => _model.CoordinateYprDegrees.Y = value;
    }

    [Category("Coordinate YPR")]
    public float CoordinateRollDeg
    {
        get => _model.CoordinateYprDegrees.Z;
        set => _model.CoordinateYprDegrees.Z = value;
    }
}

internal sealed class FineTerrainInteractionUnitPropertyView
{
    private readonly FineTerrainInteractionUnitAnnotation _model;

    public FineTerrainInteractionUnitPropertyView(FineTerrainInteractionUnitAnnotation model)
    {
        _model = model;
    }

    [ReadOnly(true)]
    [DisplayName("ID")]
    public int Id => _model.Id;

    [DisplayName("Name")]
    public string Name
    {
        get => _model.Name;
        set => _model.Name = value ?? string.Empty;
    }

    [ReadOnly(true)]
    [DisplayName("Component Count")]
    public int ComponentCount => _model.ComponentIds.Length;

    [ReadOnly(true)]
    [DisplayName("Component IDs")]
    public string ComponentIds => string.Join(", ", _model.ComponentIds.Take(18)) + (_model.ComponentIds.Length > 18 ? " ..." : string.Empty);
}

internal sealed class FineTerrainAnnotationPayload
{
    public string? SourceModel { get; init; }

    public DateTimeOffset ExportedUtc { get; init; }

    public int TotalComponents { get; init; }

    public FineTerrainWorldScalePayload? WorldScale { get; init; }

    public int[]? ActorComponentIds { get; init; }

    public FineTerrainCompositePayload[]? Composites { get; init; }

    public FineTerrainComponentAnnotation[]? Components { get; init; }

    public string[]? ComponentFiles { get; init; }

    public FineTerrainCollisionShapePayload[]? CollisionShapes { get; init; }

    [JsonPropertyName("collision_shapes")]
    public FineTerrainCollisionShapePayload[]? CollisionShapesSnakeCase { get; init; }
}

internal sealed class FineTerrainWorldScalePayload
{
    public float MapLengthXMeters { get; init; }

    public float MapLengthZMeters { get; init; }

    public float XMetersPerModelUnit { get; init; }

    public float YMetersPerModelUnit { get; init; }

    public float ZMetersPerModelUnit { get; init; }

    public FineTerrainVector3? ModelCenter { get; init; }

    public float? ModelMinY { get; init; }
}

internal sealed class FineTerrainCompositePayload
{
    public int Id { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public string? CoordinateSystemMode { get; init; }

    public int[]? ComponentIds { get; init; }

    public FineTerrainInteractionUnitPayload[]? InteractionUnits { get; init; }

    public FineTerrainVector3? PositionMeters { get; init; }

    public FineTerrainVector3? PositionModel { get; init; }

    public FineTerrainVector3? PivotModel { get; init; }

    public FineTerrainVector3? YprDegrees { get; init; }

    public FineTerrainVector3? CoordinateYprDegrees { get; init; }
}

internal sealed class FineTerrainCollisionShapePayload
{
    public int Id { get; init; }

    public string? Name { get; init; }

    [JsonPropertyName("shape_type")]
    public string? ShapeType { get; init; }

    [JsonPropertyName("ShapeType")]
    public string? ShapeTypePascal { get; init; }

    [JsonPropertyName("position_model")]
    public FineTerrainVector3? PositionModel { get; init; }

    [JsonPropertyName("PositionModel")]
    public FineTerrainVector3? PositionModelPascal { get; init; }

    [JsonPropertyName("size_model")]
    public FineTerrainVector3? SizeModel { get; init; }

    [JsonPropertyName("SizeModel")]
    public FineTerrainVector3? SizeModelPascal { get; init; }

    [JsonPropertyName("radius_model")]
    public float RadiusModel { get; init; }

    [JsonPropertyName("RadiusModel")]
    public float RadiusModelPascal { get; init; }

    [JsonPropertyName("height_model")]
    public float HeightModel { get; init; }

    [JsonPropertyName("HeightModel")]
    public float HeightModelPascal { get; init; }

    [JsonPropertyName("ypr_degrees")]
    public FineTerrainVector3? YprDegrees { get; init; }

    [JsonPropertyName("YprDegrees")]
    public FineTerrainVector3? YprDegreesPascal { get; init; }

    [JsonPropertyName("terrain_label")]
    public string? TerrainLabel { get; init; }

    [JsonPropertyName("TerrainLabel")]
    public string? TerrainLabelPascal { get; init; }

    [JsonPropertyName("vertices_model")]
    public FineTerrainVector3[]? VerticesModel { get; init; }

    [JsonPropertyName("VerticesModel")]
    public FineTerrainVector3[]? VerticesModelPascal { get; init; }
}

internal sealed class FineTerrainInteractionUnitPayload
{
    public int Id { get; init; }

    public string? Name { get; init; }

    public int[]? ComponentIds { get; init; }
}
