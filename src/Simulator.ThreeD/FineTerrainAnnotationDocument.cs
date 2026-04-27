using System.ComponentModel;
using System.Numerics;
using System.Text.Json;

namespace Simulator.ThreeD;

internal sealed class FineTerrainAnnotationDocument
{
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

    public IReadOnlyDictionary<int, FineTerrainComponentAnnotation> ComponentsById
        => _componentsById ??= Components.ToDictionary(component => component.Id);

    private Dictionary<int, FineTerrainComponentAnnotation>? _componentsById;

    public static FineTerrainAnnotationDocument? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        FineTerrainAnnotationPayload? payload = JsonSerializer.Deserialize<FineTerrainAnnotationPayload>(stream, JsonOptions);
        if (payload?.WorldScale is null)
        {
            return null;
        }

        FineTerrainComponentAnnotation[] components = (payload.Components ?? Array.Empty<FineTerrainComponentAnnotation>())
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
        };

        JsonSerializer.Serialize(stream, payload, JsonOptions);
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

internal sealed class FineTerrainVector3
{
    public float X { get; set; }

    public float Y { get; set; }

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

internal sealed class FineTerrainInteractionUnitPayload
{
    public int Id { get; init; }

    public string? Name { get; init; }

    public int[]? ComponentIds { get; init; }
}
