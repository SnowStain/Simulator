using System.Drawing;
using System.Numerics;

namespace Simulator.Platform.Rendering;

public enum InteractionSceneRenderKind
{
    EnergyMechanism,
    BaseArmorPanel,
    Outpost,
    BuffVolume,
    CollisionDebug,
}

public enum InteractionScenePrimitiveKind
{
    Anchor,
    Polygon,
    Triangle,
    Label,
}

public sealed class InteractionSceneRenderList
{
    private readonly List<InteractionSceneRenderItem> _items = new();

    public IReadOnlyList<InteractionSceneRenderItem> Items => _items;

    public int Count => _items.Count;

    public void Clear()
        => _items.Clear();

    public void AddAnchor(
        InteractionSceneRenderKind kind,
        string id,
        string label,
        Vector3 center,
        string team = "",
        double progress = 0.0)
        => _items.Add(new InteractionSceneRenderItem(
            kind,
            InteractionScenePrimitiveKind.Anchor,
            id,
            label,
            team,
            [center],
            Color.Transparent,
            Color.Transparent,
            progress));

    public void AddLabel(
        InteractionSceneRenderKind kind,
        string id,
        string label,
        Vector3 center,
        Color color,
        string team = "",
        double progress = 0.0)
        => _items.Add(new InteractionSceneRenderItem(
            kind,
            InteractionScenePrimitiveKind.Label,
            id,
            label,
            team,
            [center],
            Color.Transparent,
            color,
            progress));

    public void AddPolygon(
        InteractionSceneRenderKind kind,
        string id,
        string label,
        IReadOnlyList<Vector3> points,
        Color fillColor,
        Color edgeColor,
        string team = "",
        double progress = 0.0)
    {
        if (points.Count < 3)
        {
            return;
        }

        _items.Add(new InteractionSceneRenderItem(
            kind,
            InteractionScenePrimitiveKind.Polygon,
            id,
            label,
            team,
            points.ToArray(),
            fillColor,
            edgeColor,
            progress));
    }

    public void AddTriangle(
        InteractionSceneRenderKind kind,
        string id,
        string label,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Color fillColor,
        Color edgeColor,
        string team = "",
        double progress = 0.0)
        => _items.Add(new InteractionSceneRenderItem(
            kind,
            InteractionScenePrimitiveKind.Triangle,
            id,
            label,
            team,
            [a, b, c],
            fillColor,
            edgeColor,
            progress));
}

public sealed record InteractionSceneRenderItem(
    InteractionSceneRenderKind Kind,
    InteractionScenePrimitiveKind PrimitiveKind,
    string Id,
    string Label,
    string Team,
    IReadOnlyList<Vector3> Points,
    Color FillColor,
    Color EdgeColor,
    double Progress);
