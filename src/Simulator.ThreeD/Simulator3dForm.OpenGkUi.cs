using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using Simulator.Core;
using Simulator.Core.Gameplay;
using Simulator.Platform.Ui;
using UiButton = Simulator.Platform.Ui.OpenGkUiButton;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private Bitmap? _openGkBackdropBitmap;
    private Size _openGkBackdropBitmapSize = Size.Empty;
    private string _openGkBackdropCacheKey = string.Empty;
    private long _openGkBackdropLastRenderTicks;
    private Bitmap? _openGkUcTopHudCacheBitmap;
    private Size _openGkUcTopHudCacheSize = Size.Empty;
    private string _openGkUcTopHudCacheKey = string.Empty;
    private readonly Dictionary<string, Bitmap> _openGkTopHudSilhouetteCache = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap? _lanRoomScreenCacheBitmap;
    private Rectangle _lanRoomScreenCacheRect = Rectangle.Empty;
    private string _lanRoomScreenCacheKey = string.Empty;
    private List<UiButton> _lanRoomScreenCachedButtons = new();

    private static readonly string[] OpenGkHudSlotOrder =
    {
        "robot_1",
        "robot_2",
        "robot_3",
        "robot_4",
        "robot_6",
        "robot_7",
    };

    private static readonly IReadOnlyDictionary<string, string> OpenGkHudUnitLabelMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["robot_1"] = "1",
            ["robot_2"] = "2",
            ["robot_3"] = "3",
            ["robot_4"] = "4",
            ["robot_6"] = "6",
            ["robot_7"] = "7",
        };
    private readonly record struct OpenGkHudUnitSlot(
        string SlotKey,
        string Label,
        SimulationEntity? Entity,
        bool IsPlaceholder);

    private sealed record LanRoomMemberState(
        string Key,
        string PlayerId,
        string PlayerName,
        string Team,
        string EntityKey,
        int SlotIndex,
        string MemberRole,
        bool IsLocal,
        bool Ready,
        int SpawnPointIndex = 0,
        string ChassisMode = "");

    private readonly Dictionary<string, LanRoomMemberState> _lanRoomMembers = new(StringComparer.OrdinalIgnoreCase);
    private bool _lanRoomPrivate;

    private static bool UseOpenGkMenuChrome()
        => true;

    private static bool UseOpenGkMatchHud()
        => true;

    private bool TryDrawCachedOpenGkUcTopHudV2(Graphics graphics)
    {
        Size size = new(Math.Max(1, ClientSize.Width), Math.Max(1, HudHeight));
        string cacheKey = BuildOpenGkUcTopHudCacheKeyV2(size);
        bool rebuilt = false;
        if (_openGkUcTopHudCacheBitmap is null
            || _openGkUcTopHudCacheSize != size
            || !string.Equals(_openGkUcTopHudCacheKey, cacheKey, StringComparison.Ordinal))
        {
            InvalidateOpenGkUcTopHudCache();
            _openGkUcTopHudCacheBitmap = BuildOpenGkUcTopHudCacheBitmapV2(size);
            _openGkUcTopHudCacheSize = size;
            _openGkUcTopHudCacheKey = cacheKey;
            rebuilt = true;
        }

        if (_openGkUcTopHudCacheBitmap is null)
        {
            return false;
        }

        graphics.DrawImageUnscaled(_openGkUcTopHudCacheBitmap, 0, 0);
        if (!rebuilt)
        {
            RegisterOpenGkUcTopHudButtonsV2();
        }

        return true;
    }

    private string BuildOpenGkUcTopHudCacheKeyV2(Size size)
    {
        OpenGkMatchScoreboardState scoreboard = ResolveOpenGkMatchScoreboardState();
        var builder = new System.Text.StringBuilder(768);
        builder.Append(size.Width).Append('x').Append(size.Height).Append('|');
        builder.Append(_paused ? 'p' : 'l').Append('|');
        builder.Append(scoreboard.RoundLabel).Append('|');
        builder.Append(scoreboard.TimerLabel).Append('|');
        builder.Append(scoreboard.RedWins).Append(':').Append(scoreboard.BlueWins).Append('|');
        builder.Append(scoreboard.RedCurrentGold).Append(':').Append(scoreboard.BlueCurrentGold).Append('|');
        builder.Append(scoreboard.RedTotalGold).Append(':').Append(scoreboard.BlueTotalGold).Append('|');
        builder.Append(_host.SelectedEntity?.Id ?? string.Empty).Append('|');

        foreach (string teamKey in new[] { "red", "blue" })
        {
            builder.Append(teamKey).Append('|');
            SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
            SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
            AppendOpenGkHealthCacheStamp(builder, baseEntity);
            builder.Append(':');
            AppendOpenGkHealthCacheStamp(builder, outpostEntity);
            builder.Append('|');

            foreach (OpenGkHudUnitSlot slot in BuildOpenGkHudUnitSlotsV2(teamKey))
            {
                builder.Append(slot.SlotKey).Append(':').Append(slot.Label).Append(':');
                if (slot.Entity is null)
                {
                    builder.Append("placeholder|");
                    continue;
                }

                builder.Append(slot.Entity.Id).Append(':')
                    .Append(slot.Entity.IsAlive ? '1' : '0').Append(':')
                    .Append((int)Math.Round(Math.Max(0.0, slot.Entity.Health) * 10.0)).Append(':')
                    .Append((int)Math.Round(Math.Max(0.0, slot.Entity.MaxHealth) * 10.0)).Append(':')
                    .Append(ResolveDisplayedAmmo(slot.Entity)).Append(':')
                    .Append((int)Math.Round(slot.Entity.RespawnTimerSec * 10.0)).Append('|');
            }
        }

        return builder.ToString();
    }

    private static void AppendOpenGkHealthCacheStamp(System.Text.StringBuilder builder, SimulationEntity? entity)
    {
        if (entity is null)
        {
            builder.Append("null");
            return;
        }

        builder.Append((int)Math.Round(Math.Max(0.0, entity.Health) * 10.0)).Append('/')
            .Append((int)Math.Round(Math.Max(0.0, entity.MaxHealth) * 10.0));
    }

    private Bitmap? BuildOpenGkUcTopHudCacheBitmapV2(Size size)
    {
        if (size.Width <= 1 || size.Height <= 1)
        {
            return null;
        }

        Bitmap bitmap = new(size.Width, size.Height);
        using Graphics cacheGraphics = Graphics.FromImage(bitmap);
        cacheGraphics.Clear(Color.Transparent);
        cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        cacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        cacheGraphics.InterpolationMode = InterpolationMode.Bilinear;
        cacheGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        cacheGraphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        DrawOpenGkUcTopHudV2(cacheGraphics);
        return bitmap;
    }

    private void RegisterOpenGkUcTopHudButtonsV2()
    {
        ResolveOpenGkUcHudLayoutV2(out Rectangle red, out _, out Rectangle blue);
        RegisterOpenGkUcTeamPanelButtonsV2(red, "red", mirrored: false);
        RegisterOpenGkUcTeamPanelButtonsV2(blue, "blue", mirrored: true);
    }

    private void RegisterOpenGkUcTeamPanelButtonsV2(Rectangle rect, string teamKey, bool mirrored)
    {
        IReadOnlyList<OpenGkHudUnitSlot> slots = BuildOpenGkHudUnitSlotsV2(teamKey);
        Rectangle[] cards = BuildOpenGkUcUnitCardRectsV2(rect, slots.Count, mirrored);
        for (int i = 0; i < slots.Count && i < cards.Length; i++)
        {
            OpenGkHudUnitSlot slot = slots[ResolveOpenGkUcSlotIndex(i, slots.Count, mirrored)];
            if (slot.Entity is null || slot.IsPlaceholder)
            {
                continue;
            }

            _uiButtons.Add(new UiButton(cards[i], $"match_select:{slot.Entity.Id}"));
        }
    }

    private void DrawOpenGkUcTopHudV2(Graphics graphics)
    {
        ResolveOpenGkUcHudLayoutV2(out Rectangle red, out Rectangle center, out Rectangle blue);
        using var topShade = new SolidBrush(Color.FromArgb(92, 3, 6, 10));
        graphics.FillRectangle(topShade, 0, 0, ClientSize.Width, HudHeight);

        DrawOpenGkUcTeamPanelV2(graphics, red, "red", "\u7ea2\u65b9", mirrored: false);
        DrawOpenGkUcCenterPanelV2(graphics, center, ResolveOpenGkMatchScoreboardState());
        DrawOpenGkUcTeamPanelV2(graphics, blue, "blue", "\u84dd\u65b9", mirrored: true);
    }

    private void ResolveOpenGkUcHudLayoutV2(out Rectangle red, out Rectangle center, out Rectangle blue)
    {
        int top = 5;
        int panelHeight = Math.Min(HudHeight - 6, 142);
        int maxGroupWidth = Math.Max(320, ClientSize.Width - 36);
        int minGroupWidth = Math.Min(maxGroupWidth, 980);
        int groupWidth = Math.Clamp((int)Math.Round(ClientSize.Width * 0.92), minGroupWidth, maxGroupWidth);
        int centerWidth = Math.Clamp((int)Math.Round(groupWidth * 0.18), 186, 228);
        int sideGap = 10;
        int sideWidth = (groupWidth - centerWidth - sideGap * 2) / 2;
        if (sideWidth * 2 + centerWidth + sideGap * 2 > groupWidth)
        {
            sideWidth = Math.Max(180, (groupWidth - centerWidth - sideGap * 2) / 2);
        }

        groupWidth = sideWidth * 2 + centerWidth + sideGap * 2;
        int groupLeft = Math.Max(18, (ClientSize.Width - groupWidth) / 2);
        red = new Rectangle(groupLeft, top, sideWidth, panelHeight);
        center = new Rectangle(red.Right + sideGap, top, centerWidth, panelHeight);
        blue = new Rectangle(center.Right + sideGap, top, sideWidth, panelHeight);
    }

    private OpenGkMatchScoreboardState ResolveOpenGkMatchScoreboardState()
    {
        int remainingSeconds = ResolveDisplayedMatchRemainingSeconds();
        string timerLabel = $"{remainingSeconds / 60}:{remainingSeconds % 60:00}";
        SimulationTeamState? redTeam = _host.World.Teams.TryGetValue("red", out SimulationTeamState? redState) ? redState : null;
        SimulationTeamState? blueTeam = _host.World.Teams.TryGetValue("blue", out SimulationTeamState? blueState) ? blueState : null;
        int redCurrentGold = (int)Math.Round(redTeam?.Gold ?? 0.0);
        int blueCurrentGold = (int)Math.Round(blueTeam?.Gold ?? 0.0);
        int redTotalGold = (int)Math.Round(redTeam?.TotalGoldEarned ?? redCurrentGold);
        int blueTotalGold = (int)Math.Round(blueTeam?.TotalGoldEarned ?? blueCurrentGold);

        if (_host.IsDuelMode)
        {
            Simulator3dHost.DuelMatchSnapshot snapshot = _host.GetDuelMatchSnapshot();
            int roundIndex = snapshot.Finished
                ? snapshot.RoundsCompleted
                : Math.Min(snapshot.RoundLimit, snapshot.RoundsCompleted + 1);
            return new OpenGkMatchScoreboardState(
                $"\u7b2c{Math.Max(1, roundIndex)}/{Math.Max(1, snapshot.RoundLimit)}\u5c40",
                timerLabel,
                snapshot.RedScore,
                snapshot.BlueScore,
                redCurrentGold,
                blueCurrentGold,
                redTotalGold,
                blueTotalGold);
        }

        return new OpenGkMatchScoreboardState(
            "\u7b2c1/1\u5c40",
            timerLabel,
            0,
            0,
            redCurrentGold,
            blueCurrentGold,
            redTotalGold,
            blueTotalGold);
    }

    private void DrawOpenGkUcCenterPanelV2(Graphics graphics, Rectangle rect, OpenGkMatchScoreboardState scoreboard)
    {
        Rectangle roundTag = new(rect.X + rect.Width / 2 - 52, rect.Y + 3, 104, 18);
        Rectangle timerPanel = new(rect.X + 16, rect.Y + 24, rect.Width - 32, 58);
        Rectangle goldPanel = new(rect.X + 18, rect.Y + 88, rect.Width - 36, 30);

        using (GraphicsPath roundPath = CreateRoundedRectangle(roundTag, 7))
        using (var fill = new SolidBrush(Color.FromArgb(206, 32, 44, 46)))
        using (var border = new Pen(Color.FromArgb(146, 214, 226, 230), 1f))
        {
            graphics.FillPath(fill, roundPath);
            graphics.DrawPath(border, roundPath);
        }

        using (GraphicsPath timerPath = CreateOpenGkHudCenterFramePathV2(timerPanel))
        using (var fill = new LinearGradientBrush(timerPanel, Color.FromArgb(226, 28, 40, 42), Color.FromArgb(212, 10, 14, 18), 90f))
        using (var border = new Pen(Color.FromArgb(162, 214, 226, 230), 1.1f))
        {
            graphics.FillPath(fill, timerPath);
            graphics.DrawPath(border, timerPath);
        }

        using (GraphicsPath goldPath = CreateOpenGkGoldPanelPathV2(goldPanel))
        using (var fill = new SolidBrush(Color.FromArgb(178, 10, 16, 24)))
        using (var border = new Pen(Color.FromArgb(108, 168, 182, 196), 1f))
        {
            graphics.FillPath(fill, goldPath);
            graphics.DrawPath(border, goldPath);
        }

        TextRenderer.DrawText(
            graphics,
            scoreboard.RoundLabel,
            _tinyHudFont,
            roundTag,
            Color.FromArgb(232, 236, 242, 246),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(
            graphics,
            scoreboard.TimerLabel,
            _hudBigFont,
            new Rectangle(timerPanel.X, timerPanel.Y + 9, timerPanel.Width, 34),
            Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        Rectangle scoreLine = new(timerPanel.X + 12, timerPanel.Bottom - 16, timerPanel.Width - 24, 15);
        TextRenderer.DrawText(
            graphics,
            $"{scoreboard.RedWins}  -  {scoreboard.BlueWins}",
            _tinyHudFont,
            scoreLine,
            Color.FromArgb(230, 232, 238, 244),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        DrawOpenGkUcGoldRowV2(graphics, new Rectangle(goldPanel.X + 10, goldPanel.Y + 6, goldPanel.Width - 20, 18), scoreboard.RedCurrentGold, scoreboard.BlueCurrentGold, string.Empty);
    }

    private void DrawOpenGkCompactBarV2(Graphics graphics, Rectangle rect, float ratio, Color color, string label, bool mirrored = false)
    {
        bool dynamicFillOnGpu = ShouldDrawOpenGkDynamicHudShapesOnGpu();
        using GraphicsPath path = CreateOpenGkHudStructureBarPathV2(rect, !mirrored);
        using var back = new SolidBrush(Color.FromArgb(156, 8, 12, 18));
        using var fill = new SolidBrush(Color.FromArgb(238, color));
        using var glow = new SolidBrush(Color.FromArgb(72, 255, 255, 255));
        using var border = new Pen(Color.FromArgb(142, 216, 224, 232), 1f);
        graphics.FillPath(back, path);
        if (!dynamicFillOnGpu)
        {
            GraphicsState state = graphics.Save();
            graphics.SetClip(path);
            int fillWidth = (int)Math.Round(rect.Width * Math.Clamp(ratio, 0f, 1f));
            int fillX = mirrored ? rect.Right - fillWidth : rect.X;
            graphics.FillRectangle(fill, fillX, rect.Y, fillWidth, rect.Height);
            graphics.FillRectangle(glow, fillX, rect.Y, Math.Max(0, fillWidth - 4), Math.Max(2, rect.Height / 3));
            graphics.Restore(state);
        }

        graphics.DrawPath(border, path);
        if (!string.IsNullOrWhiteSpace(label) && rect.Height >= 16 && rect.Width >= 24)
        {
            TextRenderer.DrawText(graphics, label, _tinyHudFont, rect, Color.WhiteSmoke, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawOpenGkUcGoldRowV2(Graphics graphics, Rectangle rect, int redValue, int blueValue, string label)
    {
        Rectangle labelRect = new(rect.X + rect.Width / 2 - 24, rect.Y, 48, rect.Height);
        TextRenderer.DrawText(graphics, redValue.ToString(), _smallHudFont, new Rectangle(rect.X, rect.Y, rect.Width / 2 - 30, rect.Height), Color.FromArgb(246, ResolveTeamColor("red")), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, blueValue.ToString(), _smallHudFont, new Rectangle(rect.X + rect.Width / 2 + 30, rect.Y, rect.Width / 2 - 30, rect.Height), Color.FromArgb(246, ResolveTeamColor("blue")), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        Rectangle coinRect = new(labelRect.X + labelRect.Width / 2 - 9, labelRect.Y + Math.Max(0, (labelRect.Height - 14) / 2), 18, 14);
        using var coinBrush = new SolidBrush(Color.FromArgb(236, 214, 184, 74));
        using var coinBorder = new Pen(Color.FromArgb(220, 255, 242, 176), 1f);
        graphics.FillEllipse(coinBrush, coinRect);
        graphics.DrawEllipse(coinBorder, coinRect);
        TextRenderer.DrawText(graphics, label, _tinyHudFont, labelRect, Color.FromArgb(222, 228, 234, 240), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkUcTeamPanelV2(Graphics graphics, Rectangle rect, string teamKey, string teamLabel, bool mirrored)
    {
        Color teamColor = ResolveTeamColor(teamKey);

        Rectangle teamTitle = new(rect.X + 18, rect.Y, rect.Width - 36, 20);
        TextRenderer.DrawText(
            graphics,
            teamLabel,
            _hudMidFont,
            teamTitle,
            Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
        SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
        Rectangle outpostBar = mirrored
            ? new Rectangle(rect.Right - Math.Clamp(rect.Width / 7, 72, 100) - 10, rect.Y + 25, Math.Clamp(rect.Width / 7, 72, 100), 26)
            : new Rectangle(rect.X + 10, rect.Y + 25, Math.Clamp(rect.Width / 7, 72, 100), 26);
        Rectangle baseBanner = mirrored
            ? new Rectangle(rect.X + 10, rect.Y + 30, rect.Width - outpostBar.Width - 28, 14)
            : new Rectangle(outpostBar.Right + 8, rect.Y + 30, rect.Width - outpostBar.Width - 28, 14);
        using GraphicsPath bannerPath = CreateOpenGkHudStructureBarPathV2(baseBanner, mirrored);
        using var bannerFill = new LinearGradientBrush(
            baseBanner,
            Color.FromArgb(214, teamColor),
            Color.FromArgb(104, teamColor),
            mirrored ? 180f : 0f);
        using var bannerBorder = new Pen(Color.FromArgb(180, 232, 238, 242), 1f);
        graphics.FillPath(bannerFill, bannerPath);
        graphics.DrawPath(bannerBorder, bannerPath);
        Rectangle baseBar = new(baseBanner.X + 34, baseBanner.Y + 4, Math.Max(42, baseBanner.Width - 40), 8);
        DrawOpenGkCompactBarV2(graphics, baseBar, ResolveHealthRatio(baseEntity), teamColor, string.Empty, mirrored);
        TextRenderer.DrawText(
            graphics,
            FormatStructureHpLabel("\u57fa\u5730", baseEntity),
            _tinyHudFont,
            new Rectangle(baseBanner.X + 4, baseBanner.Y - 18, baseBanner.Width - 8, 18),
            Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        using (GraphicsPath outpostPath = CreateOpenGkOutpostBadgePathV2(outpostBar, mirrored))
        using (var outpostFill = new LinearGradientBrush(outpostBar, Color.FromArgb(216, teamColor), Color.FromArgb(132, teamColor), 90f))
        using (var outpostBorder = new Pen(Color.FromArgb(180, 232, 238, 242), 1f))
        {
            graphics.FillPath(outpostFill, outpostPath);
            graphics.DrawPath(outpostBorder, outpostPath);
        }

        Rectangle outpostGauge = new(outpostBar.X + 7, outpostBar.Bottom - 9, outpostBar.Width - 14, 5);
        DrawOpenGkCompactBarV2(graphics, outpostGauge, ResolveHealthRatio(outpostEntity), teamColor, string.Empty, mirrored);
        TextRenderer.DrawText(
            graphics,
            FormatStructureHpLabel("\u524d\u54e8", outpostEntity),
            _tinyHudFont,
            new Rectangle(outpostBar.X + 8, outpostBar.Y - 14, outpostBar.Width - 16, 15),
            Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        IReadOnlyList<OpenGkHudUnitSlot> slots = BuildOpenGkHudUnitSlotsV2(teamKey);
        Rectangle[] cards = BuildOpenGkUcUnitCardRectsV2(rect, slots.Count, mirrored);
        for (int i = 0; i < slots.Count && i < cards.Length; i++)
        {
            DrawOpenGkUcUnitCardV2(graphics, cards[i], slots[ResolveOpenGkUcSlotIndex(i, slots.Count, mirrored)], teamColor, mirrored);
        }
    }

    private static int ResolveOpenGkUcSlotIndex(int visualIndex, int slotCount, bool mirrored)
        => mirrored ? visualIndex : slotCount - 1 - visualIndex;

    private Rectangle[] BuildOpenGkUcUnitCardRectsV2(Rectangle rect, int slotCount, bool mirrored)
    {
        int count = Math.Max(1, slotCount);
        int cardTop = rect.Y + 56;
        int cardHeight = Math.Clamp(rect.Bottom - cardTop - 3, 80, 88);
        int horizontalPadding = 14;
        int availableWidth = Math.Max(count, rect.Width - horizontalPadding * 2);
        int gap = count > 1
            ? Math.Clamp(rect.Width / 72, 5, 10)
            : 0;
        int cardWidth = count > 0
            ? Math.Max(44, (availableWidth - gap * (count - 1)) / count)
            : availableWidth;
        cardWidth = Math.Min(104, cardWidth);
        int usedWidth = cardWidth * count + gap * (count - 1);
        if (usedWidth > availableWidth && count > 1)
        {
            gap = Math.Max(2, (availableWidth - cardWidth * count) / (count - 1));
            usedWidth = cardWidth * count + gap * (count - 1);
        }

        int startX = rect.X + horizontalPadding + Math.Max(0, (availableWidth - usedWidth) / 2);
        Rectangle[] result = new Rectangle[count];
        for (int slotIndex = 0; slotIndex < count; slotIndex++)
        {
            int xOffset = slotIndex * (cardWidth + gap);
            int x = startX + xOffset;
            result[slotIndex] = new Rectangle(
                x,
                Math.Min(rect.Bottom - cardHeight - 4, cardTop),
                Math.Min(cardWidth, Math.Max(1, rect.Right - horizontalPadding - x)),
                cardHeight);
        }

        return result;
    }

    private IReadOnlyList<OpenGkHudUnitSlot> BuildOpenGkHudUnitSlotsV2(string teamKey)
    {
        Dictionary<string, SimulationEntity> byKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (SimulationEntity entity in _host.World.Entities)
        {
            if (!string.Equals(entity.Team, teamKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entity.IsSimulationSuppressed)
            {
                continue;
            }

            string entityKey = NormalizeLanDuelEntityKey(ExtractEntityKey(entity.Id));
            if (ShouldHideInactiveOpenGkHudUnitSlots()
                && !HasActiveRoomControlSource(teamKey, entityKey))
            {
                continue;
            }

            if (!string.Equals(entity.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entity.EntityType, "sentry", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byKey[entityKey] = entity;
        }

        bool hideInactiveSlots = ShouldHideInactiveOpenGkHudUnitSlots();
        var slots = new List<OpenGkHudUnitSlot>(OpenGkHudSlotOrder.Length);
        for (int index = 0; index < OpenGkHudSlotOrder.Length; index++)
        {
            string slotKey = OpenGkHudSlotOrder[index];
            byKey.TryGetValue(slotKey, out SimulationEntity? entity);
            if (hideInactiveSlots
                && (entity is null || !HasActiveRoomControlSource(teamKey, slotKey)))
            {
                continue;
            }

            string label = OpenGkHudUnitLabelMap.TryGetValue(slotKey, out string? mappedLabel)
                ? mappedLabel
                : slotKey;
            slots.Add(new OpenGkHudUnitSlot(slotKey, label, entity, entity is null));
        }

        return slots;
    }

    private bool ShouldHideInactiveOpenGkHudUnitSlots()
        => _appState == SimulatorAppState.InMatch
            && (IsLanMultiplayerActive || _localRoomMatchActive);

    private void DrawOpenGkUcUnitCardV2(Graphics graphics, Rectangle card, OpenGkHudUnitSlot slot, Color teamColor, bool mirrored)
    {
        bool selected = string.Equals(_host.SelectedEntity?.Id, slot.Entity?.Id, StringComparison.OrdinalIgnoreCase);
        bool alive = slot.Entity?.IsAlive ?? false;
        using GraphicsPath path = CreateOpenGkHudCardFramePathV2(card);
        using var fill = new SolidBrush(Color.FromArgb(slot.Entity is null ? 18 : alive ? 42 : 34, 8, 12, 18));
        graphics.FillPath(fill, path);
        if (selected)
        {
            using var selectedBorder = new Pen(Color.FromArgb(238, 242, 210, 82), 1.3f);
            graphics.DrawPath(selectedBorder, path);
        }

        int infoStripHeight = 22;
        Rectangle infoStrip = new(card.X + 4, card.Bottom - infoStripHeight - 3, card.Width - 8, infoStripHeight);
        Rectangle silhouetteRect = new(
            card.X,
            card.Y + 4,
            Math.Max(36, card.Width),
            Math.Max(42, infoStrip.Y - card.Y - 4));
        if (slot.Entity is not null)
        {
            DrawOpenGkUcUnitSilhouetteV2(graphics, silhouetteRect, slot.Entity, teamColor, mirrored, alive);
        }
        else
        {
            DrawOpenGkUcPlaceholderSilhouetteV2(graphics, silhouetteRect);
        }

        if (slot.Entity is not null)
        {
            DrawOpenGkUcUnitNumberBadgeV2(graphics, silhouetteRect, slot.Label, teamColor, mirrored);
        }

        using var stripFill = new SolidBrush(Color.FromArgb(116, 7, 10, 16));
        graphics.FillRectangle(stripFill, infoStrip);

        Rectangle hpRect = new(infoStrip.X + 3, infoStrip.Y + 3, Math.Max(10, infoStrip.Width - 6), 6);
        DrawOpenGkUcParallelogramBarV2(graphics, hpRect, ResolveHealthRatio(slot.Entity), teamColor, mirrored);

        Rectangle ammoRect = new(infoStrip.X + 3, infoStrip.Y + 10, Math.Max(1, infoStrip.Width - 6), 12);
        DrawOpenGkUcAmmoIndicatorV2(graphics, ammoRect, slot.Entity, alive);

        if (slot.Entity is not null)
        {
            _uiButtons.Add(new UiButton(card, $"match_select:{slot.Entity.Id}"));
        }
    }

    private void DrawOpenGkUcUnitSilhouetteV2(Graphics graphics, Rectangle rect, SimulationEntity entity, Color teamColor, bool mirrored, bool alive)
    {
        Bitmap? bitmap = GetOpenGkTopHudSilhouetteBitmapV2(entity, rect.Size, teamColor, faceRight: true);
        if (bitmap is null)
        {
            return;
        }

        if (mirrored)
        {
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(rect.Right, rect.Y);
            graphics.ScaleTransform(-1f, 1f);
            graphics.DrawImageUnscaled(bitmap, 0, 0);
            graphics.Restore(state);
        }
        else
        {
            graphics.DrawImageUnscaled(bitmap, rect.X, rect.Y);
        }

        if (!alive)
        {
            using var gray = new SolidBrush(Color.FromArgb(146, 116, 122, 128));
            graphics.FillRectangle(gray, rect);
        }
    }

    private void DrawOpenGkUcUnitNumberBadgeV2(Graphics graphics, Rectangle iconRect, string label, Color teamColor, bool mirrored)
    {
        Rectangle badge = mirrored
            ? new Rectangle(iconRect.Right - 20, iconRect.Bottom - 17, 19, 15)
            : new Rectangle(iconRect.Left + 1, iconRect.Bottom - 17, 19, 15);
        using GraphicsPath badgePath = CreateRoundedRectangle(badge, 4);
        using var fill = new SolidBrush(Color.FromArgb(226, teamColor));
        using var border = new Pen(Color.FromArgb(190, 255, 255, 255), 0.8f);
        graphics.FillPath(fill, badgePath);
        graphics.DrawPath(border, badgePath);
        TextRenderer.DrawText(
            graphics,
            label,
            _tinyHudFont,
            badge,
            Color.WhiteSmoke,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkUcAmmoIndicatorV2(Graphics graphics, Rectangle rect, SimulationEntity? entity, bool alive)
    {
        if (entity is null)
        {
            TextRenderer.DrawText(
                graphics,
                "\u9884\u7559",
                _tinyHudFont,
                rect,
                Color.FromArgb(162, 184, 194, 204),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        if (!alive)
        {
            TextRenderer.DrawText(
                graphics,
                $"\u590d\u6d3b {entity.RespawnTimerSec:0.0}s",
                _tinyHudFont,
                rect,
                Color.FromArgb(154, 220, 230, 240),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        if (string.Equals(entity.RoleKey, "engineer", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.AmmoType, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool largeAmmo = string.Equals(entity.AmmoType, "42mm", StringComparison.OrdinalIgnoreCase);
        int iconWidth = largeAmmo ? 11 : 24;
        int iconX = rect.X + Math.Max(0, (rect.Width - iconWidth - 22) / 2);
        Rectangle icon = new(iconX, rect.Y + Math.Max(0, (rect.Height - 8) / 2), iconWidth, 8);
        if (ShouldDrawOpenGkDynamicHudShapesOnGpu())
        {
            TextRenderer.DrawText(
                graphics,
                ResolveDisplayedAmmo(entity).ToString(),
                _tinyHudFont,
                new Rectangle(icon.Right + 3, rect.Y - 1, Math.Max(18, rect.Right - icon.Right - 2), rect.Height + 2),
                Color.WhiteSmoke,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        using var fill = new SolidBrush(Color.FromArgb(235, 248, 250, 252));
        using var shade = new SolidBrush(Color.FromArgb(82, 120, 132, 146));
        using var pen = new Pen(Color.FromArgb(210, 255, 255, 255), 0.8f);
        if (largeAmmo)
        {
            Rectangle ball = new(icon.X + 1, icon.Y, 8, 8);
            graphics.FillEllipse(fill, ball);
            graphics.DrawEllipse(pen, ball);
            graphics.FillEllipse(shade, ball.X + 2, ball.Y + 2, 1, 1);
            graphics.FillEllipse(shade, ball.X + 5, ball.Y + 2, 1, 1);
            graphics.FillEllipse(shade, ball.X + 4, ball.Y + 5, 1, 1);
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                int x = icon.X + i * 8;
                PointF[] bullet =
                [
                    new PointF(x, icon.Y + 2),
                    new PointF(x + 5, icon.Y + 2),
                    new PointF(x + 7, icon.Y + 4),
                    new PointF(x + 5, icon.Y + 6),
                    new PointF(x, icon.Y + 6),
                ];
                graphics.FillPolygon(fill, bullet);
            }
        }

        TextRenderer.DrawText(
            graphics,
            ResolveDisplayedAmmo(entity).ToString(),
            _tinyHudFont,
            new Rectangle(icon.Right + 3, rect.Y - 1, Math.Max(18, rect.Right - icon.Right - 2), rect.Height + 2),
            Color.WhiteSmoke,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkUcPlaceholderSilhouetteV2(Graphics graphics, Rectangle rect)
    {
        using var pen = new Pen(Color.FromArgb(142, 172, 180, 188), 1.2f);
        using var fill = new SolidBrush(Color.FromArgb(38, 164, 174, 184));
        PointF[] hex =
        [
            new(rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.18f),
            new(rect.Left + rect.Width * 0.72f, rect.Top + rect.Height * 0.18f),
            new(rect.Left + rect.Width * 0.88f, rect.Top + rect.Height * 0.50f),
            new(rect.Left + rect.Width * 0.72f, rect.Top + rect.Height * 0.82f),
            new(rect.Left + rect.Width * 0.28f, rect.Top + rect.Height * 0.82f),
            new(rect.Left + rect.Width * 0.12f, rect.Top + rect.Height * 0.50f),
        ];
        graphics.FillPolygon(fill, hex);
        graphics.DrawPolygon(pen, hex);
        TextRenderer.DrawText(graphics, "\u9884\u7559", _tinyHudFont, rect, Color.FromArgb(196, 214, 224, 234), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkUcParallelogramBarV2(Graphics graphics, Rectangle rect, float ratio, Color color, bool mirrored)
    {
        bool dynamicFillOnGpu = ShouldDrawOpenGkDynamicHudShapesOnGpu();
        float clamped = Math.Clamp(ratio, 0f, 1f);
        float skew = Math.Min(8f, rect.Width * 0.18f);
        using GraphicsPath outline = CreateOpenGkParallelogramPathV2(rect, skew, !mirrored);
        using var back = new SolidBrush(Color.FromArgb(162, 16, 22, 28));
        using var fill = new SolidBrush(Color.FromArgb(236, color));
        using var border = new Pen(Color.FromArgb(120, 202, 214, 224), 1f);
        graphics.FillPath(back, outline);
        if (!dynamicFillOnGpu)
        {
            GraphicsState state = graphics.Save();
            graphics.SetClip(outline);
            int fillWidth = (int)Math.Round(rect.Width * clamped);
            int fillX = mirrored ? rect.Right - fillWidth : rect.X;
            graphics.FillRectangle(fill, fillX, rect.Y, fillWidth, rect.Height);
            graphics.Restore(state);
        }

        graphics.DrawPath(border, outline);
    }

    private bool ShouldDrawOpenGkDynamicHudShapesOnGpu()
        => false;

    private static GraphicsPath CreateOpenGkHudCenterFramePathV2(Rectangle rect)
    {
        int bevelX = Math.Min(26, Math.Max(16, rect.Width / 7));
        int bevelY = Math.Min(18, Math.Max(10, rect.Height / 4));
        int footInset = Math.Min(18, Math.Max(10, rect.Width / 12));
        GraphicsPath path = new();
        path.AddPolygon(
        [
            new PointF(rect.Left + bevelX, rect.Top),
            new PointF(rect.Right - bevelX, rect.Top),
            new PointF(rect.Right, rect.Top + bevelY),
            new PointF(rect.Right - footInset, rect.Bottom - 6),
            new PointF(rect.Right - bevelX - 14, rect.Bottom),
            new PointF(rect.Left + bevelX + 14, rect.Bottom),
            new PointF(rect.Left + footInset, rect.Bottom - 6),
            new PointF(rect.Left, rect.Top + bevelY),
        ]);
        return path;
    }

    private static GraphicsPath CreateOpenGkHudSideFramePathV2(Rectangle rect, bool mirrored)
    {
        int tip = Math.Min(48, Math.Max(28, rect.Width / 10));
        int lowerInset = Math.Min(16, Math.Max(8, rect.Width / 22));
        GraphicsPath path = new();
        if (mirrored)
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + 22, rect.Top),
                new PointF(rect.Right - tip, rect.Top),
                new PointF(rect.Right, rect.Top + 24),
                new PointF(rect.Right - 26, rect.Bottom),
                new PointF(rect.Left, rect.Bottom),
                new PointF(rect.Left + lowerInset, rect.Top + 18),
            ]);
        }
        else
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + tip, rect.Top),
                new PointF(rect.Right - 22, rect.Top),
                new PointF(rect.Right - lowerInset, rect.Top + 18),
                new PointF(rect.Right, rect.Bottom),
                new PointF(rect.Left + 26, rect.Bottom),
                new PointF(rect.Left, rect.Top + 24),
            ]);
        }

        return path;
    }

    private static GraphicsPath CreateOpenGkHudCardFramePathV2(Rectangle rect)
    {
        GraphicsPath path = new();
        path.AddRectangle(rect);
        return path;
    }

    private static GraphicsPath CreateOpenGkHudStructureBarPathV2(Rectangle rect, bool mirrored)
    {
        int skew = Math.Min(8, Math.Max(4, rect.Height));
        GraphicsPath path = new();
        if (mirrored)
        {
            path.AddPolygon(
            [
                new PointF(rect.Left, rect.Top),
                new PointF(rect.Right - skew, rect.Top),
                new PointF(rect.Right, rect.Bottom),
                new PointF(rect.Left + skew, rect.Bottom),
            ]);
        }
        else
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + skew, rect.Top),
                new PointF(rect.Right, rect.Top),
                new PointF(rect.Right - skew, rect.Bottom),
                new PointF(rect.Left, rect.Bottom),
            ]);
        }

        return path;
    }

    private static GraphicsPath CreateOpenGkParallelogramPathV2(Rectangle rect, float skew, bool mirrored)
    {
        GraphicsPath path = new();
        if (mirrored)
        {
            path.AddPolygon(
            [
                new PointF(rect.Left, rect.Top),
                new PointF(rect.Right - skew, rect.Top),
                new PointF(rect.Right, rect.Bottom),
                new PointF(rect.Left + skew, rect.Bottom),
            ]);
        }
        else
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + skew, rect.Top),
                new PointF(rect.Right, rect.Top),
                new PointF(rect.Right - skew, rect.Bottom),
                new PointF(rect.Left, rect.Bottom),
            ]);
        }

        return path;
    }

    private static GraphicsPath CreateOpenGkGoldPanelPathV2(Rectangle rect)
    {
        int notch = Math.Min(16, Math.Max(10, rect.Height / 2));
        GraphicsPath path = new();
        path.AddPolygon(
        [
            new PointF(rect.Left + notch, rect.Top),
            new PointF(rect.Right - notch, rect.Top),
            new PointF(rect.Right, rect.Top + notch / 2f),
            new PointF(rect.Right - notch, rect.Bottom),
            new PointF(rect.Left + notch, rect.Bottom),
            new PointF(rect.Left, rect.Top + notch / 2f),
        ]);
        return path;
    }

    private static GraphicsPath CreateOpenGkOutpostBadgePathV2(Rectangle rect, bool mirrored)
    {
        GraphicsPath path = new();
        if (mirrored)
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + 10, rect.Top),
                new PointF(rect.Right - 12, rect.Top),
                new PointF(rect.Right, rect.Top + 12),
                new PointF(rect.Right - 8, rect.Bottom),
                new PointF(rect.Left, rect.Bottom),
                new PointF(rect.Left + 6, rect.Top + 10),
            ]);
        }
        else
        {
            path.AddPolygon(
            [
                new PointF(rect.Left + 12, rect.Top),
                new PointF(rect.Right - 10, rect.Top),
                new PointF(rect.Right - 6, rect.Top + 10),
                new PointF(rect.Right, rect.Bottom),
                new PointF(rect.Left + 8, rect.Bottom),
                new PointF(rect.Left, rect.Top + 12),
            ]);
        }

        return path;
    }

    private Bitmap? GetOpenGkTopHudSilhouetteBitmapV2(SimulationEntity entity, Size size, Color teamColor, bool faceRight)
    {
        if (size.Width <= 1 || size.Height <= 1)
        {
            return null;
        }

        Bitmap? staticIcon = GetStaticRobotIconBitmap(entity, size, faceRight);
        if (staticIcon is not null)
        {
            return staticIcon;
        }

        string key = BuildOpenGkTopHudSilhouetteCacheKeyV2(entity, size, teamColor, faceRight);
        if (_appState == SimulatorAppState.InMatch)
        {
            if (_openGkTopHudSilhouetteCache.TryGetValue(key, out Bitmap? placeholder))
            {
                return placeholder;
            }

            Bitmap placeholderBitmap = new(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using Graphics placeholderGraphics = Graphics.FromImage(placeholderBitmap);
            placeholderGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawStaticRobotIconUnavailable(placeholderGraphics, new Rectangle(Point.Empty, size), teamColor);
            _openGkTopHudSilhouetteCache[key] = placeholderBitmap;
            return placeholderBitmap;
        }

        if (_openGkTopHudSilhouetteCache.TryGetValue(key, out Bitmap? cached))
        {
            return cached;
        }

        Bitmap bitmap = new(size.Width, size.Height);
        using Graphics cacheGraphics = Graphics.FromImage(bitmap);
        cacheGraphics.Clear(Color.Transparent);
        cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        cacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        cacheGraphics.InterpolationMode = InterpolationMode.Bilinear;
        cacheGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        cacheGraphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        DrawOpenGkTopHudSilhouetteModelV2(cacheGraphics, entity, new Rectangle(Point.Empty, size), faceRight);
        _openGkTopHudSilhouetteCache[key] = bitmap;
        return bitmap;
    }

    private string BuildOpenGkTopHudSilhouetteCacheKeyV2(SimulationEntity entity, Size size, Color teamColor, bool faceRight)
    {
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        return string.Join(
            "|",
            "hud_preview_v5_static_gpu_fallback",
            entity.Id,
            entity.Team,
            entity.RoleKey,
            profile.RoleKey,
            profile.ChassisSubtype,
            $"{profile.BodyLengthM:0.000}",
            $"{profile.BodyWidthM:0.000}",
            $"{profile.BodyHeightM:0.000}",
            $"{profile.BodyClearanceM:0.000}",
            profile.WheelStyle,
            profile.SuspensionStyle,
            profile.ArmStyle,
            profile.FrontClimbAssistStyle,
            profile.RearClimbAssistStyle,
            teamColor.ToArgb(),
            faceRight ? "r1" : "r0",
            $"{size.Width}x{size.Height}");
    }

    private void DrawOpenGkTopHudSilhouetteModelV2(Graphics graphics, SimulationEntity entity, Rectangle viewport, bool faceRight)
    {
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        GraphicsState state = graphics.Save();
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;

        try
        {
            graphics.SetClip(viewport, CombineMode.Replace);
            _projectionViewportRect = viewport;
            _suppressEntityLabels = true;
            _useProfileColorsForVehiclePreview = true;
            entity.AngleDeg = faceRight ? 45.0 : -45.0;
            entity.TurretYawDeg = faceRight ? 18.0 : -18.0;
            entity.GimbalPitchDeg = -6.0;

            float previewExtent = Math.Max(
                0.45f,
                Math.Max(
                    profile.BodyLengthM + profile.BarrelLengthM * 0.8f,
                    Math.Max(profile.BodyWidthM, profile.GimbalHeightM + profile.BodyClearanceM)));
            _cameraTargetM = new Vector3(0f, Math.Max(0.22f, profile.BodyClearanceM + profile.BodyHeightM * 0.55f), 0f);
            float distance = Math.Clamp(previewExtent * 1.18f, 0.68f, 2.65f);
            _cameraPositionM = _cameraTargetM + new Vector3(distance * 0.86f, distance * 0.52f, distance * 1.08f);
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
            float aspect = Math.Max(0.60f, viewport.Width / (float)Math.Max(1, viewport.Height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.86f, aspect, 0.02f, 40f);
            DrawEntityAppearanceModelModern(graphics, entity, Vector3.Zero, profile);
        }
        finally
        {
            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
            graphics.Restore(state);
        }
    }

    private void DisposeOpenGkTopHudSilhouetteCache()
    {
        foreach (Bitmap bitmap in _openGkTopHudSilhouetteCache.Values)
        {
            bitmap.Dispose();
        }

        _openGkTopHudSilhouetteCache.Clear();
    }

    private readonly record struct OpenGkMatchScoreboardState(
        string RoundLabel,
        string TimerLabel,
        int RedWins,
        int BlueWins,
        int RedCurrentGold,
        int BlueCurrentGold,
        int RedTotalGold,
        int BlueTotalGold);

    private void DrawOpenGkMainMenu(Graphics graphics)
    {
        _ = UseModernMainMenuChrome();
        DrawOpenGkArenaBackdrop(graphics, dim: true);
        DrawOpenGkMainMenuForeground(graphics);
    }

    private void DrawOpenGkMainMenuForeground(Graphics graphics)
    {
        if (_localRoomPanelOpen || _lanRoomPanelOpen || _lanSession is not null)
        {
            DrawOpenGkLanRoomPanel(graphics, Rectangle.Empty, Color.FromArgb(64, 132, 226), 1f);
            return;
        }

        if (_openGkStartHubOpen)
        {
            DrawOpenGkUnifiedHomeMenu(graphics);
            if (_openGkCreateModePickerOpen)
            {
                DrawOpenGkCreateGameModePicker(graphics);
            }

            return;
        }

        DrawOpenGkMainHeader(graphics);
        DrawOpenGkMainActions(graphics);
    }

    private void DrawOpenGkUnifiedHomeMenu(Graphics graphics)
    {
        int width = Math.Max(1, ClientSize.Width);
        int height = Math.Max(1, ClientSize.Height);
        using StringFormat centered = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var titleBrush = new SolidBrush(Color.FromArgb(238, 244, 248, 252));
        using var mutedBrush = new SolidBrush(Color.FromArgb(190, 212, 222, 232));

        Rectangle title = new(width / 2 - 260, 18, 520, 32);
        graphics.DrawString("RoboMaster 2026 UC", _hudMidFont, titleBrush, title, centered);

        int listTop = Math.Max(96, height / 4);
        Rectangle list = new(width / 2 - Math.Clamp(width / 5, 280, 420), listTop, Math.Clamp(width * 2 / 5, 560, 840), Math.Max(170, height - listTop - 176));
        DrawOpenGkHomeRoomList(graphics, list);

        int checkY = height - 124;
        int checkX = width / 2 - 98;
        DrawOpenGkCheckbox(graphics, new Rectangle(checkX, checkY, 196, 30), "显示局域网游戏", _openGkShowLanRooms, "home_toggle_lan_rooms");

        int buttonY = height - 72;
        int buttonW = Math.Clamp(width / 11, 128, 180);
        int gap = 22;
        DrawOpenGkButton(graphics, new Rectangle(70, buttonY, buttonW, 36), "返回", "home_back_to_main", false, Color.FromArgb(70, 138, 154), enabled: true);

        int startX = Math.Max(260, width - (buttonW * 5 + gap * 4) - 70);
        DrawOpenGkButton(graphics, new Rectangle(startX, buttonY, buttonW, 36), "本地游戏", "home_local_game", false, Color.FromArgb(36, 160, 172), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(startX + (buttonW + gap), buttonY, buttonW, 36), "直接连接", "home_direct_connect", false, Color.FromArgb(36, 160, 172), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(startX + (buttonW + gap) * 2, buttonY, buttonW, 36), "刷新", "lan_discovery_refresh", false, Color.FromArgb(36, 160, 172), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(startX + (buttonW + gap) * 3, buttonY, buttonW, 36), "创建游戏", "home_create_game", false, Color.FromArgb(36, 160, 172), enabled: true);
        bool canJoin = _openGkSelectedDiscoveredRoomIndex >= 0 && _openGkSelectedDiscoveredRoomIndex < _lanDiscoveredRooms.Count;
        DrawOpenGkButton(graphics, new Rectangle(startX + (buttonW + gap) * 4, buttonY, buttonW, 36), "加入游戏", "home_join_selected_room", false, Color.FromArgb(128, 128, 128), enabled: canJoin);

        TextRenderer.DrawText(graphics, "点击刷新搜索房间；直接连接使用主机 IP 与端口。", _tinyHudFont, new Rectangle(70, buttonY - 28, Math.Max(1, width - 140), 20), mutedBrush.Color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkHomeRoomList(Graphics graphics, Rectangle rect)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 6);
        using var fill = new SolidBrush(Color.FromArgb(96, 4, 8, 14));
        using var border = new Pen(Color.FromArgb(88, 150, 178, 196), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        IReadOnlyList<LanDiscoveredRoom> rooms = _openGkShowLanRooms ? _lanDiscoveredRooms : Array.Empty<LanDiscoveredRoom>();
        if (rooms.Count == 0)
        {
            TextRenderer.DrawText(graphics, "暂无可加入房间，点击刷新搜索局域网主机。", _smallHudFont, rect, Color.FromArgb(168, 198, 210, 222), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        int rowHeight = 48;
        int rowGap = 8;
        int count = Math.Min(rooms.Count, Math.Max(1, (rect.Height - 22) / (rowHeight + rowGap)));
        for (int i = 0; i < count; i++)
        {
            LanDiscoveredRoom room = rooms[i];
            Rectangle row = new(rect.X + 18, rect.Y + 18 + i * (rowHeight + rowGap), rect.Width - 36, rowHeight);
            bool selected = i == _openGkSelectedDiscoveredRoomIndex;
            using GraphicsPath rowPath = CreateRoundedRectangle(row, 5);
            using var rowFill = new SolidBrush(selected ? Color.FromArgb(188, 38, 72, 96) : Color.FromArgb(132, 18, 24, 34));
            using var rowBorder = new Pen(selected ? Color.FromArgb(220, 236, 204, 86) : Color.FromArgb(98, 132, 158, 184), selected ? 1.5f : 1f);
            graphics.FillPath(rowFill, rowPath);
            graphics.DrawPath(rowBorder, rowPath);
            TextRenderer.DrawText(graphics, $"{room.RoomName}    {room.ConnectedPlayers}/{room.MaxPlayers}", _smallHudFont, new Rectangle(row.X + 14, row.Y + 4, row.Width / 2, 20), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(graphics, $"{room.HostAddress}:{room.Port}    {room.HostName}", _tinyHudFont, new Rectangle(row.X + 14, row.Y + 26, row.Width - 28, 16), Color.FromArgb(186, 210, 222, 232), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            _uiButtons.Add(new UiButton(Rectangle.Inflate(row, 6, 4), $"lan_discovered_room:{i}"));
        }
    }

    private void DrawOpenGkCreateGameModePicker(Graphics graphics)
    {
        using var dim = new SolidBrush(Color.FromArgb(138, 0, 0, 0));
        graphics.FillRectangle(dim, ClientRectangle);
        int width = Math.Min(560, Math.Max(420, ClientSize.Width - 96));
        Rectangle panel = new((ClientSize.Width - width) / 2, Math.Max(96, ClientSize.Height / 2 - 170), width, 340);
        int x = panel.X + 44;
        int y = panel.Y + 92;
        int w = panel.Width - 88;
        string title = string.Equals(_openGkModePickerIntent, "local", StringComparison.OrdinalIgnoreCase)
            ? "选择本地游戏模式"
            : "选择创建房间模式";
        DrawOpenGkModalPanel(graphics, panel, title);
        DrawOpenGkButton(graphics, new Rectangle(x, y, w, 44), "RoboMaster 2026 UC", string.Equals(_openGkModePickerIntent, "local", StringComparison.OrdinalIgnoreCase) ? "home_local_mode:uc" : "home_create_mode:uc", true, Color.FromArgb(64, 132, 226), enabled: true);
        y += 58;
        DrawOpenGkButton(graphics, new Rectangle(x, y, w, 44), "1v1 测试", string.Equals(_openGkModePickerIntent, "local", StringComparison.OrdinalIgnoreCase) ? "home_local_mode:1v1" : "home_create_mode:1v1", false, Color.FromArgb(88, 118, 172), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(x, panel.Bottom - 56, w, 34), "取消", "home_create_mode_cancel", false, Color.FromArgb(96, 96, 104), enabled: true);
    }

    private bool TryExecuteOpenGkHomeAction(string action)
    {
        if (!action.StartsWith("home_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        switch (action)
        {
            case "home_local_game":
                _openGkModePickerIntent = "local";
                _openGkCreateModePickerOpen = true;
                return true;
            case "home_back_to_main":
                _openGkCreateModePickerOpen = false;
                _openGkStartHubOpen = false;
                _openGkSelectedDiscoveredRoomIndex = -1;
                _lanRoomPanelOpen = false;
                if (_localRoomPanelOpen || _localRoomMatchActive)
                {
                    CloseLocalRoom();
                }

                return true;
            case "home_direct_connect":
                _openGkCreateModePickerOpen = false;
                _lanRoomHostMode = false;
                _lanRoomPanelOpen = true;
                _lanFocusedField = "host";
                _lanRoomStatusText = "输入主机 IP 与端口后加入房间。";
                return true;
            case "home_create_game":
                _openGkModePickerIntent = "create";
                _openGkCreateModePickerOpen = true;
                return true;
            case "home_create_mode_cancel":
                _openGkCreateModePickerOpen = false;
                return true;
            case "home_local_mode:uc":
                _openGkCreateModePickerOpen = false;
                _openGkStartHubOpen = false;
                OpenLocalRoom("uc");
                return true;
            case "home_local_mode:1v1":
                _openGkCreateModePickerOpen = false;
                _openGkStartHubOpen = false;
                OpenLocalRoom("1v1");
                return true;
            case "home_create_mode:uc":
                _openGkCreateModePickerOpen = false;
                _lanRoomMatchMode = "uc";
                _lanRoomSettings = _lanRoomSettings with { MatchMode = "uc" };
                _lanRoomHostMode = true;
                _lanRoomPanelOpen = true;
                _lanRoomNameText = "RoboMaster 2026 UC";
                _host.SetMatchModePreservingLoadedWorld("full");
                BeginLanRoomConnect();
                return true;
            case "home_create_mode:1v1":
                _openGkCreateModePickerOpen = false;
                _lanRoomMatchMode = "1v1";
                _lanRoomSettings = _lanRoomSettings with { MatchMode = "1v1" };
                _lanRoomHostMode = true;
                _lanRoomPanelOpen = true;
                _lanRoomNameText = "ARTINX 1v1";
                _host.SetMatchModePreservingLoadedWorld("duel_1v1");
                BeginLanRoomConnect();
                return true;
            case "home_join_selected_room":
                if (_openGkSelectedDiscoveredRoomIndex >= 0 && _openGkSelectedDiscoveredRoomIndex < _lanDiscoveredRooms.Count)
                {
                    LanDiscoveredRoom room = _lanDiscoveredRooms[_openGkSelectedDiscoveredRoomIndex];
                    _openGkCreateModePickerOpen = false;
                    _lanRoomHostMode = false;
                    _lanRoomPanelOpen = true;
                    _lanRoomNameText = room.RoomName;
                    _lanHostAddressText = room.HostAddress;
                    _lanPortText = room.Port.ToString(CultureInfo.InvariantCulture);
                    BeginLanRoomConnect();
                }

                return true;
            case "home_toggle_lan_rooms":
                _openGkShowLanRooms = !_openGkShowLanRooms;
                return true;
            case "home_role_host":
                _lanRoomHostMode = true;
                return true;
            case "home_role_guest":
                _lanRoomHostMode = false;
                return true;
            default:
                return true;
        }
    }

    private void DrawOpenGkArenaBackdrop(Graphics graphics, bool dim)
    {
        using var worldBack = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(12, 16, 22),
            Color.FromArgb(4, 6, 10),
            LinearGradientMode.Vertical);
        graphics.FillRectangle(worldBack, ClientRectangle);

        bool freezeLanBackdrop = ShouldSuppressLanBackgroundMapWork();
        if (!freezeLanBackdrop)
        {
            EnsureOpenGkBackdropBitmap();
        }

        if (_openGkBackdropBitmap is not null)
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(_openGkBackdropBitmap, ClientRectangle);
        }

        using var focusShade = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(dim ? 112 : 72, 0, 0, 0),
            Color.FromArgb(dim ? 70 : 42, 0, 0, 0),
            LinearGradientMode.Horizontal);
        graphics.FillRectangle(focusShade, ClientRectangle);
        using var grayWash = new SolidBrush(Color.FromArgb(dim ? 38 : 18, 128, 128, 128));
        graphics.FillRectangle(grayWash, ClientRectangle);

        using var vignette = new SolidBrush(Color.FromArgb(104, 0, 0, 0));
        graphics.FillRectangle(vignette, 0, 0, ClientSize.Width, 58);
        graphics.FillRectangle(vignette, 0, ClientSize.Height - 88, ClientSize.Width, 88);
    }

    private void EnsureOpenGkBackdropBitmap(bool allowSuppressedRefresh = false)
    {
        if (ShouldSuppressLanBackgroundMapWork() && !allowSuppressedRefresh)
        {
            return;
        }

        Size targetSize = new(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        string cacheKey = ResolveOpenGkBackdropCacheKey();
        long nowTicks = _frameClock.ElapsedTicks;
        bool sizeChanged = _openGkBackdropBitmap is null || _openGkBackdropBitmapSize != targetSize;
        bool cacheKeyChanged = !string.Equals(_openGkBackdropCacheKey, cacheKey, StringComparison.Ordinal);
        bool cacheExpired = nowTicks - _openGkBackdropLastRenderTicks >= Stopwatch.Frequency / 8;
        if (ShouldSuppressLanBackgroundMapWork() && allowSuppressedRefresh)
        {
            cacheExpired = false;
        }
        if (!sizeChanged && !cacheKeyChanged && !cacheExpired)
        {
            return;
        }

        if (sizeChanged)
        {
            _openGkBackdropBitmap?.Dispose();
            _openGkBackdropBitmap = new Bitmap(targetSize.Width, targetSize.Height);
            _openGkBackdropBitmapSize = targetSize;
        }

        if (_openGkBackdropBitmap is null)
        {
            return;
        }

        Bitmap? gpuBackdrop = null;
        if (!_externallyDrivenCompatibilityMode)
        {
            gpuBackdrop = RenderOpenGkBackdropGpu(ResolveOpenGkBackdropRenderSize(targetSize));
        }

        if (gpuBackdrop is not null)
        {
            CopyOpenGkBackdropToDisplayBitmap(gpuBackdrop, _openGkBackdropBitmap);
            gpuBackdrop.Dispose();
        }
        else
        {
            RenderOpenGkBackdropBitmap(_openGkBackdropBitmap);
        }
        _openGkBackdropCacheKey = cacheKey;
        _openGkBackdropLastRenderTicks = nowTicks;
    }

    private bool ShouldSuppressLanBackgroundMapWork()
        => false;

    private static Size ResolveOpenGkBackdropRenderSize(Size targetSize)
    {
        const float preferredScale = 1.35f;
        const float maxPixels = 6_500_000f;
        const float maxDimension = 4096f;

        float pixelScale = MathF.Sqrt(maxPixels / Math.Max(1f, targetSize.Width * targetSize.Height));
        float dimensionScale = MathF.Min(
            maxDimension / Math.Max(1, targetSize.Width),
            maxDimension / Math.Max(1, targetSize.Height));
        float scale = MathF.Min(preferredScale, MathF.Min(pixelScale, dimensionScale));
        if (scale <= 1.03f)
        {
            return targetSize;
        }

        return new Size(
            Math.Max(targetSize.Width, (int)MathF.Round(targetSize.Width * scale)),
            Math.Max(targetSize.Height, (int)MathF.Round(targetSize.Height * scale)));
    }

    private static void CopyOpenGkBackdropToDisplayBitmap(Bitmap source, Bitmap target)
    {
        using Graphics graphics = Graphics.FromImage(target);
        graphics.Clear(Color.FromArgb(6, 8, 12));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height));
    }

    private string ResolveOpenGkBackdropCacheKey()
    {
        SimulationEntity? focus = ResolveOpenGkShowcaseEntity();
        return string.Join(
            "|",
            _appState,
            _host.MatchMode,
            _host.SelectedTeam,
            _host.ActiveMapPreset,
            _host.World.MetersPerWorldUnit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            focus?.Id ?? string.Empty,
            focus?.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            focus?.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Math.Round(_mainMenuStartExpandedVisual, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            Math.Round(_mainMenuSingleExpandedVisual, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void RenderOpenGkBackdropBitmap(Bitmap bitmap)
    {
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        double previousGameTimeSec = _host.World.GameTimeSec;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(6, 8, 12));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        try
        {
            UpdateOpenGkBackdropCamera(bitmap.Size);
            _projectionViewportRect = new Rectangle(Point.Empty, bitmap.Size);
            _suppressEntityLabels = true;
            _useProfileColorsForVehiclePreview = true;
            _host.World.GameTimeSec = Math.Max(previousGameTimeSec, _frameClock.Elapsed.TotalSeconds);
            DrawOpenGkBackdropWorld(graphics);
        }
        finally
        {
            _host.World.GameTimeSec = previousGameTimeSec;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
        }
    }

    private void DrawOpenGkBackdropWorld(Graphics graphics)
    {
        DrawFloor(graphics);
        if (!IsLobbyScenePresentationReady())
        {
            return;
        }

        DrawFacilities(graphics);
        DrawEntities(graphics);
    }

    private void UpdateOpenGkBackdropCamera(Size viewportSize)
    {
        Vector3 mapCenter = ComputeMapCenterMeters();
        Vector3 target = ResolveOpenGkBackdropTarget(mapCenter);
        float orbitPhase = (float)(_frameClock.Elapsed.TotalSeconds * 0.22);
        float radius = Math.Clamp(ComputeDefaultCameraDistance() * 0.42f, 8f, 72f);
        Vector3 offset = new(
            MathF.Cos(orbitPhase) * radius,
            radius * 0.33f + 2.5f,
            MathF.Sin(orbitPhase) * radius * 0.92f);
        _cameraTargetM = target;
        _cameraPositionM = target + offset;
        _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);

        float aspect = Math.Max(1f, viewportSize.Width / (float)Math.Max(viewportSize.Height, 1));
        _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(0.60f, aspect, 0.03f, 900f);
    }

    private Vector3 ResolveOpenGkBackdropTarget(Vector3 mapCenter)
    {
        if (_appState == SimulatorAppState.MainMenu
            && TryComputeOpenGkTeamAnchor("red", out Vector3 redAnchor)
            && TryComputeOpenGkTeamAnchor("blue", out Vector3 blueAnchor))
        {
            float travel = 0.5f + 0.5f * MathF.Sin((float)(_frameClock.Elapsed.TotalSeconds * 0.24));
            return Vector3.Lerp(redAnchor, blueAnchor, travel) + new Vector3(0f, 0.32f, 0f);
        }

        SimulationEntity? focus = ResolveOpenGkShowcaseEntity();
        if (focus is not null)
        {
            return ToScenePoint(
                focus.X,
                focus.Y,
                (float)Math.Max(0.1, focus.GroundHeightM + focus.AirborneHeightM + 0.65));
        }

        return mapCenter + new Vector3(0f, 0.3f, 0f);
    }

    private bool TryComputeOpenGkTeamAnchor(string teamKey, out Vector3 anchor)
    {
        IReadOnlyList<SimulationEntity> units = _host.GetControlCandidates(teamKey);
        if (units.Count == 0)
        {
            float mapWidthM = (float)(_host.MapPreset.Width * Math.Max(1e-6, _host.World.MetersPerWorldUnit));
            float mapHeightM = (float)(_host.MapPreset.Height * Math.Max(1e-6, _host.World.MetersPerWorldUnit));
            float x = string.Equals(teamKey, "blue", StringComparison.OrdinalIgnoreCase) ? mapWidthM * 0.72f : mapWidthM * 0.28f;
            anchor = new Vector3(x, 0.55f, mapHeightM * 0.5f);
            return true;
        }

        double sumX = 0.0;
        double sumY = 0.0;
        double sumHeight = 0.0;
        foreach (SimulationEntity unit in units)
        {
            sumX += unit.X;
            sumY += unit.Y;
            sumHeight += Math.Max(0.1, unit.GroundHeightM + unit.AirborneHeightM + 0.65);
        }

        double count = units.Count;
        anchor = ToScenePoint(sumX / count, sumY / count, (float)(sumHeight / count));
        return true;
    }

    private void DrawOpenGkLobbyHud(Graphics graphics)
    {
        using var titleBrush = new SolidBrush(Color.FromArgb(244, 247, 250));
        using var metaBrush = new SolidBrush(Color.FromArgb(198, 210, 220, 230));
        using var hintBrush = new SolidBrush(Color.FromArgb(180, 196, 208, 220));

        string title = _host.IsDuelMode ? "7\u53f7\u54e8\u5175\u6d4b\u8bd5" : _host.IsUnitTestMode ? "\u5355\u4f4d\u6d4b\u8bd5" : "5v5 \u623f\u95f4\u9009\u8f66";
        int left = 36;
        int top = 154;
        int railWidth = Math.Clamp(ClientSize.Width / 4, 320, 380);
        int gridWidth = Math.Min(railWidth - 8, 348);
        int gridGap = 12;
        int labelColumnWidth = 96;
        int controlLeft = left + labelColumnWidth;
        int controlWidth = gridWidth - labelColumnWidth;
        int halfGridWidth = (controlWidth - gridGap) / 2;
        int previewWidth = Math.Clamp(ClientSize.Width / 3, 430, 560);
        int previewX = Math.Max(left + railWidth + 52, ClientSize.Width - previewWidth - 34);
        int mapHeaderY = Math.Max(154, top - 14);
        int previewTop = mapHeaderY + 54;
        int previewHeight = Math.Clamp(ClientSize.Height - previewTop - 148, 330, 520);
        Rectangle preview = new(previewX, previewTop, previewWidth, previewHeight);

        graphics.DrawString(title, _menuTitleFont, titleBrush, left, top);
        graphics.DrawString($"\u5730\u56fe  {ResolveLobbyMapLabel()}  |  \u6a21\u5f0f  {ResolveMatchModeLabel()}", _menuEyebrowFont, metaBrush, left, top + 38);
        graphics.DrawString("OpenGL \u5b9e\u65f6\u573a\u5730\u4e0e\u673a\u4eba\u9884\u89c8\u5df2\u52a0\u8f7d\u3002", _tinyHudFont, hintBrush, left, top + 62);

        bool redTeamActive = string.Equals(_host.SelectedTeam, "red", StringComparison.OrdinalIgnoreCase);
        bool blueTeamActive = string.Equals(_host.SelectedTeam, "blue", StringComparison.OrdinalIgnoreCase);
        graphics.DrawString("\u9635\u8425", _tinyHudFont, Brushes.Gainsboro, left, top + 100);
        Rectangle lobbyRedTeam = new(controlLeft, top + 94, halfGridWidth, 32);
        Rectangle lobbyBlueTeam = new(controlLeft + halfGridWidth + gridGap, top + 94, halfGridWidth, 32);
        DrawButton(graphics, lobbyRedTeam, "\u7ea2\u65b9", "lobby_team:red", redTeamActive, Color.FromArgb(174, 66, 66));
        DrawButton(graphics, lobbyBlueTeam, "\u84dd\u65b9", "lobby_team:blue", blueTeamActive, Color.FromArgb(64, 112, 200));
        _lobbyAutoAimSliderRect = Rectangle.Empty;

        int y = top + 148;
        graphics.DrawString("\u5175\u79cd\u7f16\u53f7", _menuSubtitleFont, Brushes.Gainsboro, left, y);
        y += 30;

        string selectedEntityKey = ExtractEntityKey(_host.SelectedEntity?.Id ?? _host.SingleUnitTestFocusId);
        (string EntityKey, string Label, Color Color, bool Enabled)[] roleButtons = _host.IsDuelMode
            ? new[]
            {
                ("robot_1", "1", Color.FromArgb(112, 126, 232), true),
                ("robot_2", "2", Color.FromArgb(92, 172, 126), true),
                ("robot_3", "3", Color.FromArgb(196, 132, 82), true),
                ("robot_4", "4", Color.FromArgb(196, 132, 82), true),
                ("robot_7", "7 \u54e8\u5175\u6d4b\u8bd5", Color.FromArgb(132, 110, 198), true),
            }
            : new[]
            {
                ("robot_1", "1", Color.FromArgb(112, 126, 232), true),
                ("robot_2", "2", Color.FromArgb(92, 172, 126), true),
                ("robot_3", "3", Color.FromArgb(196, 132, 82), true),
                ("robot_4", "4", Color.FromArgb(196, 132, 82), true),
                ("robot_7", "7 \u54e8\u5175\u6d4b\u8bd5", Color.FromArgb(132, 110, 198), true),
            };

        int roleButtonWidth = controlWidth;
        int roleButtonHeight = 36;
        int roleGap = 8;
        for (int index = 0; index < roleButtons.Length; index++)
        {
            Rectangle button = new(
                controlLeft,
                y + index * (roleButtonHeight + roleGap),
                roleButtonWidth,
                roleButtonHeight);
            DrawButton(
                graphics,
                button,
                roleButtons[index].Label,
                roleButtons[index].Enabled ? $"lobby_focus_entity:{roleButtons[index].EntityKey}" : string.Empty,
                roleButtons[index].Enabled && string.Equals(selectedEntityKey, roleButtons[index].EntityKey, StringComparison.OrdinalIgnoreCase),
                roleButtons[index].Color);
        }

        int fillY = y + roleButtons.Length * (roleButtonHeight + roleGap) + 8;
        if (string.Equals((_host.SelectedEntity?.RoleKey ?? string.Empty), "infantry", StringComparison.OrdinalIgnoreCase))
        {
            graphics.DrawString("步兵构型", _tinyHudFont, Brushes.Gainsboro, left, fillY + 6);
            int chassisWidth = (controlWidth - gridGap * 2) / 3;
            Rectangle chassisFull = new(controlLeft, fillY, chassisWidth, 30);
            Rectangle chassisMecanum = new(controlLeft + chassisWidth + gridGap, fillY, chassisWidth, 30);
            Rectangle chassisBalance = new(controlLeft + (chassisWidth + gridGap) * 2, fillY, controlLeft + controlWidth - (controlLeft + (chassisWidth + gridGap) * 2), 30);
            DrawButton(graphics, chassisFull, "全向轮", "lobby_infantry_mode:full", string.Equals(_host.InfantryMode, "full", StringComparison.OrdinalIgnoreCase), Color.FromArgb(92, 112, 132));
            DrawButton(graphics, chassisMecanum, "狗腿麦轮", "lobby_infantry_mode:mecanum", string.Equals(_host.InfantryMode, "mecanum", StringComparison.OrdinalIgnoreCase), Color.FromArgb(86, 138, 188));
            DrawButton(graphics, chassisBalance, "平衡串联腿", "lobby_infantry_mode:balance", string.Equals(_host.InfantryMode, "balance", StringComparison.OrdinalIgnoreCase), Color.FromArgb(98, 150, 118));
            fillY += 38;
        }

        if (!_host.IsFocusSandboxMode && !IsLanMultiplayerActive)
        {
            Rectangle aiFill = new(controlLeft, fillY, halfGridWidth, 30);
            Rectangle robotOnly = new(controlLeft + halfGridWidth + gridGap, fillY, halfGridWidth, 30);
            DrawButton(graphics, aiFill, "AI\u586b\u5145", "lobby_ai:on", _host.AiEnabled, Color.FromArgb(72, 146, 226));
            DrawButton(graphics, robotOnly, "\u4ec5\u673a\u5668\u4eba", "lobby_ai:off", !_host.AiEnabled, Color.FromArgb(92, 112, 132));
            fillY += 38;
        }
        else
        {
            fillY += 6;
        }

        Rectangle lighting = new(controlLeft, fillY, controlWidth, 30);
        DrawButton(
            graphics,
            lighting,
            _host.LightingEnabled ? "\u5149\u7167\uff1a\u5f00" : "\u5149\u7167\uff1a\u5173",
            "menu_toggle_lighting",
            _host.LightingEnabled,
            Color.FromArgb(96, 126, 146));

        int startY = fillY + 42;
        Rectangle start = new(controlLeft, startY, controlWidth, 48);
        string startLabel = _lobbyWorldRebuildTask is null ? "\u5f00\u59cb\u5bf9\u5c40" : "\u66f4\u65b0\u4e2d...";
        DrawButton(
            graphics,
            start,
            startLabel,
            "lobby_start_match",
            _lobbyWorldRebuildTask is null,
            Color.FromArgb(52, 132, 226));

        if (_lobbyWorldRebuildTask is not null)
        {
            using var statusBrush = new SolidBrush(Color.FromArgb(224, 182, 222, 255));
            graphics.DrawString(
                string.IsNullOrWhiteSpace(_lobbyWorldRebuildLabel) ? "\u6b63\u5728\u5e94\u7528\u914d\u7f6e..." : _lobbyWorldRebuildLabel,
                _smallHudFont,
                statusBrush,
                controlLeft,
                start.Bottom + 10);
        }

        _lobbyAutoAimSliderRect = Rectangle.Empty;
        _lobbyDisplayLatencySliderRect = Rectangle.Empty;

        bool fixedScenarioMap = _host.IsSingleUnitTestMode || _host.IsDuelMode || _host.IsUnitTestMode;
        graphics.DrawString(fixedScenarioMap ? "\u573a\u5730" : "\u5730\u56fe\u9009\u62e9", _menuSubtitleFont, Brushes.Gainsboro, preview.X, mapHeaderY);
        int mapControlY = mapHeaderY + 23;
        Rectangle mapCurrent = new(preview.X + 50, mapControlY, 248, 30);
        if (fixedScenarioMap)
        {
            DrawPanel(graphics, mapCurrent, alpha: 126);
            DrawUiButtonText(graphics, mapCurrent, ResolveLobbyMapLabel(), _smallHudFont, Color.FromArgb(232, 240, 246));
        }
        else
        {
            Rectangle mapPrev = new(preview.X, mapControlY, 42, 30);
            Rectangle mapNext = new(preview.X + 306, mapControlY, 42, 30);
            DrawButton(graphics, mapPrev, "<", "lobby_map_prev", true, Color.FromArgb(72, 110, 164));
            DrawPanel(graphics, mapCurrent, alpha: 126);
            DrawUiButtonText(graphics, mapCurrent, FormatMapPresetLabel(_host.ActiveMapPreset), _smallHudFont, Color.FromArgb(232, 240, 246));
            DrawButton(graphics, mapNext, ">", "lobby_map_next", true, Color.FromArgb(72, 110, 164));
        }

        DrawLobbyVehiclePreviewCard(graphics, preview, _host.SelectedEntity ?? ResolveOpenGkShowcaseEntity());

        Rectangle back = new(left, ClientSize.Height - 66, 180, 38);
        DrawButton(graphics, back, "\u8fd4\u56de", "lobby_back_main", false);

        graphics.DrawString(
            "Enter \u5f00\u59cb\uff0cEsc \u8fd4\u56de\uff0cT \u5207\u6362\u9635\u8425\u3002",
            _smallHudFont,
            Brushes.LightGray,
            left,
            ClientSize.Height - 28);
    }

    private bool TryResolveOpenGkMainMenuAction(Point point, out string? action)
    {
        action = null;
        if (_appState != SimulatorAppState.MainMenu
            || _lanRoomPanelOpen
            || _lanSession is not null
            || _openGkStartHubOpen)
        {
            return false;
        }

        static bool Hit(Rectangle rect, Point cursor)
            => Rectangle.Inflate(rect, 8, 6).Contains(cursor);

        ResolveOpenGkMainMenuLayout(out _, out int x, out int y, out int width, out int h, out int smallH, out int gap);

        if (Hit(new Rectangle(x, y, width, h), point))
        {
            action = "main_menu_toggle_start";
            return true;
        }

        y += h + gap;
        y += 4;
        Rectangle editor = new(x, y, width, h);
        if (Hit(editor, point))
        {
            action = "main_menu_toggle_editor";
            return true;
        }

        y += h + gap;
        if (_mainMenuEditorExpandedVisual > 0.05)
        {
            Rectangle terrain = new(x + 18, y, width - 18, smallH);
            if (Hit(terrain, point))
            {
                action = "menu_open_terrain_editor";
                return true;
            }

            y += smallH + 6;
            Rectangle appearance = new(x + 18, y, width - 18, smallH);
            if (Hit(appearance, point))
            {
                action = "menu_open_appearance_editor";
                return true;
            }

            y += smallH + 6;
            Rectangle rule = new(x + 18, y, width - 18, smallH);
            if (Hit(rule, point))
            {
                action = "menu_open_rule_editor";
                return true;
            }

            y += smallH + 6;
            Rectangle lighting = new(x + 18, y, width - 18, smallH);
            if (Hit(lighting, point))
            {
                action = "menu_open_lighting_editor";
                return true;
            }

            y += smallH + 6;
            Rectangle lightingToggle = new(x + 18, y, width - 18, smallH);
            if (Hit(lightingToggle, point))
            {
                action = "menu_toggle_lighting";
                return true;
            }

            y += smallH + gap;
        }

        y += 4;
        Rectangle exit = new(x, y, width, h);
        if (Hit(exit, point))
        {
            action = "menu_exit";
            return true;
        }

        return false;
    }

    private void DrawOpenGkArenaGrid(Graphics graphics, Rectangle field)
    {
        using var minor = new Pen(Color.FromArgb(34, 214, 224, 218), 1f);
        using var major = new Pen(Color.FromArgb(64, 228, 238, 232), 1.2f);
        for (int i = 1; i < 14; i++)
        {
            int x = field.Left + field.Width * i / 14;
            graphics.DrawLine(i == 7 ? major : minor, x, field.Top + 10, x, field.Bottom - 10);
        }

        for (int i = 1; i < 8; i++)
        {
            int y = field.Top + field.Height * i / 8;
            graphics.DrawLine(i == 4 ? major : minor, field.Left + 10, y, field.Right - 10, y);
        }

        using var centerPen = new Pen(Color.FromArgb(92, 230, 236, 232), 1.5f);
        Rectangle center = new(field.Left + field.Width / 2 - field.Height / 8, field.Top + field.Height / 2 - field.Height / 8, field.Height / 4, field.Height / 4);
        graphics.DrawEllipse(centerPen, center);
    }

    private void DrawOpenGkArenaFacilities(Graphics graphics, Rectangle field)
    {
        Rectangle redBase = new(field.Left + field.Width / 20, field.Top + field.Height * 3 / 8, field.Width / 8, field.Height / 4);
        Rectangle blueBase = new(field.Right - field.Width / 20 - field.Width / 8, redBase.Top, field.Width / 8, redBase.Height);
        Rectangle redOutpost = new(field.Left + field.Width * 19 / 48, field.Top + field.Height / 5, field.Width / 16, field.Height / 7);
        Rectangle blueOutpost = new(field.Right - field.Width * 19 / 48 - field.Width / 16, field.Bottom - field.Height / 5 - field.Height / 7, field.Width / 16, field.Height / 7);

        DrawOpenGkFacilityBlock(graphics, redBase, Color.FromArgb(214, 176, 46, 52), "R");
        DrawOpenGkFacilityBlock(graphics, blueBase, Color.FromArgb(214, 54, 116, 222), "B");
        DrawOpenGkFacilityBlock(graphics, redOutpost, Color.FromArgb(172, 218, 92, 82), "\u524d\u54e8");
        DrawOpenGkFacilityBlock(graphics, blueOutpost, Color.FromArgb(172, 88, 154, 238), "\u524d\u54e8");

        using var lanePen = new Pen(Color.FromArgb(78, 232, 214, 108), 2.0f);
        graphics.DrawLine(lanePen, field.Left + field.Width / 5, field.Top + field.Height * 2 / 7, field.Right - field.Width / 5, field.Bottom - field.Height * 2 / 7);
        graphics.DrawLine(lanePen, field.Left + field.Width / 5, field.Bottom - field.Height * 2 / 7, field.Right - field.Width / 5, field.Top + field.Height * 2 / 7);
    }

    private void DrawOpenGkFacilityBlock(Graphics graphics, Rectangle rect, Color color, string label)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        using var fill = new SolidBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        using var border = new Pen(Color.FromArgb(170, Color.White), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            graphics,
            label,
            _tinyHudFont,
            rect,
            Color.FromArgb(220, 244, 248, 252),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private Rectangle ResolveOpenGkMainPortraitRect()
    {
        ResolveOpenGkMainMenuLayout(out Rectangle headerRect, out int actionX, out _, out int actionWidth, out _, out _, out _);
        int leftReserved = Math.Max(headerRect.Right + 26, actionX + actionWidth + 84);
        int bottomReserved = Math.Clamp(ClientSize.Height / 5, 128, 170);
        int portraitWidth = Math.Max(360, ClientSize.Width - leftReserved - 90);
        int portraitHeight = Math.Max(300, ClientSize.Height - bottomReserved - 122);
        int portraitX = leftReserved + (ClientSize.Width - leftReserved - portraitWidth) / 2;
        int y = 86;
        return new Rectangle(portraitX, y, portraitWidth, portraitHeight);
    }

    private SimulationEntity? ResolveOpenGkShowcaseEntity()
    {
        SimulationEntity? selected = _host.SelectedEntity;
        if (selected is not null
            && !selected.IsSimulationSuppressed
            && (string.Equals(selected.EntityType, "robot", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selected.EntityType, "sentry", StringComparison.OrdinalIgnoreCase)))
        {
            return selected;
        }

        return _host.GetControlCandidates(_host.SelectedTeam).FirstOrDefault()
            ?? _host.GetControlCandidates().FirstOrDefault();
    }

    private IReadOnlyList<SimulationEntity> BuildOpenGkShowcaseRoster()
    {
        IReadOnlyList<SimulationEntity> selectedTeam = _host.GetControlCandidates(_host.SelectedTeam);
        if (selectedTeam.Count > 0)
        {
            return HudRosterOrder
                .Select(key => selectedTeam.FirstOrDefault(entity => string.Equals(ExtractEntityKey(entity.Id), key, StringComparison.OrdinalIgnoreCase)))
                .Where(entity => entity is not null)
                .Cast<SimulationEntity>()
                .ToArray();
        }

        return _host.GetControlCandidates().Take(5).ToArray();
    }

    private void DrawOpenGkSpawnPlate(Graphics graphics, Rectangle rect)
    {
        Rectangle plate = new(
            rect.Left + rect.Width / 7,
            rect.Bottom - Math.Max(76, rect.Height / 5),
            rect.Width * 5 / 7,
            Math.Max(76, rect.Height / 5));
        using GraphicsPath platePath = CreateRoundedRectangle(plate, 8);
        using var fill = new LinearGradientBrush(
            plate,
            Color.FromArgb(92, 38, 46, 52),
            Color.FromArgb(42, 108, 120, 110),
            LinearGradientMode.Vertical);
        using var border = new Pen(Color.FromArgb(126, 224, 226, 214), 1.2f);
        graphics.FillPath(fill, platePath);
        graphics.DrawPath(border, platePath);

        using var stripe = new Pen(Color.FromArgb(96, 236, 210, 92), 2f);
        graphics.DrawLine(stripe, plate.Left + 22, plate.Top + plate.Height / 2, plate.Right - 22, plate.Top + plate.Height / 2);
    }

    private void DrawOpenGkVehiclePortrait(Graphics graphics, Rectangle viewport, SimulationEntity entity, bool large)
    {
        RobotAppearanceProfile profile = _host.ResolveAppearanceProfile(entity);
        GraphicsState state = graphics.Save();
        Rectangle? previousViewport = _projectionViewportRect;
        Matrix4x4 previousView = _viewMatrix;
        Matrix4x4 previousProjection = _projectionMatrix;
        Vector3 previousCameraPosition = _cameraPositionM;
        Vector3 previousCameraTarget = _cameraTargetM;
        bool previousSuppressLabels = _suppressEntityLabels;
        bool previousUseProfileColors = _useProfileColorsForVehiclePreview;
        double previousAngle = entity.AngleDeg;
        double previousTurretYaw = entity.TurretYawDeg;
        double previousPitch = entity.GimbalPitchDeg;
        SmoothingMode previousSmoothing = graphics.SmoothingMode;

        try
        {
            graphics.SetClip(viewport, CombineMode.Intersect);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shadow = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
            graphics.FillEllipse(
                shadow,
                viewport.Left + viewport.Width * 0.25f,
                viewport.Bottom - viewport.Height * 0.26f,
                viewport.Width * 0.50f,
                viewport.Height * 0.10f);

            _projectionViewportRect = viewport;
            _suppressEntityLabels = true;
            _useProfileColorsForVehiclePreview = true;
            float spin = (float)Math.Sin(_mainMenuPulseTimeSec * 0.75);
            entity.AngleDeg = 45.0 + spin * 4.0;
            entity.TurretYawDeg = 18.0 + spin * 6.0;
            entity.GimbalPitchDeg = -6.0;

            float previewExtent = Math.Max(
                0.55f,
                Math.Max(
                    profile.BodyLengthM + profile.BarrelLengthM * 0.85f,
                    Math.Max(profile.BodyWidthM, profile.GimbalHeightM + profile.BodyClearanceM)));
            _cameraTargetM = new Vector3(0f, Math.Max(0.18f, profile.BodyClearanceM + profile.BodyHeightM * 0.55f), 0f);
            float distanceScale = large ? 1.55f : 1.75f;
            float distance = Math.Clamp(previewExtent * distanceScale, large ? 0.95f : 0.70f, large ? 3.7f : 2.5f);
            _cameraPositionM = _cameraTargetM + new Vector3(distance * 0.88f, distance * 0.50f, distance * 1.08f);
            _viewMatrix = Matrix4x4.CreateLookAt(_cameraPositionM, _cameraTargetM, Vector3.UnitY);
            float aspect = Math.Max(0.45f, viewport.Width / (float)Math.Max(1, viewport.Height));
            _projectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(large ? 0.72f : 0.82f, aspect, 0.02f, 48f);

            DrawEntityAppearanceModelModern(graphics, entity, Vector3.Zero, profile);
        }
        finally
        {
            entity.AngleDeg = previousAngle;
            entity.TurretYawDeg = previousTurretYaw;
            entity.GimbalPitchDeg = previousPitch;
            _projectionViewportRect = previousViewport;
            _viewMatrix = previousView;
            _projectionMatrix = previousProjection;
            _cameraPositionM = previousCameraPosition;
            _cameraTargetM = previousCameraTarget;
            _suppressEntityLabels = previousSuppressLabels;
            _useProfileColorsForVehiclePreview = previousUseProfileColors;
            graphics.SmoothingMode = previousSmoothing;
            graphics.Restore(state);
        }
    }

    private void DrawOpenGkVehicleNameplate(Graphics graphics, SimulationEntity entity, Rectangle portrait)
    {
        Rectangle plate = new(portrait.Left + 26, portrait.Bottom - 92, Math.Min(420, Math.Max(280, portrait.Width / 2)), 64);
        using GraphicsPath path = CreateRoundedRectangle(plate, 6);
        using var fill = new SolidBrush(Color.FromArgb(164, 7, 10, 15));
        using var border = new Pen(Color.FromArgb(124, ResolveTeamColor(entity.Team)), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string entityKey = ExtractEntityKey(entity.Id);
        string number = entityKey.StartsWith("robot_", StringComparison.OrdinalIgnoreCase) ? entityKey[6..] : entityKey;
        string title = $"{number}  {ResolveRoleLabel(entity)}";
        string detail = $"{ResolveTeamName(entity.Team)}  \u8840\u91cf {(int)Math.Max(0.0, entity.Health)}/{(int)Math.Max(1.0, entity.MaxHealth)}";
        TextRenderer.DrawText(graphics, title, _hudMidFont, new Rectangle(plate.X + 14, plate.Y + 8, plate.Width - 28, 24), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, detail, _tinyHudFont, new Rectangle(plate.X + 14, plate.Y + 34, plate.Width - 28, 18), Color.FromArgb(214, 218, 228, 236), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkMainHeader(Graphics graphics)
    {
        ResolveOpenGkMainMenuLayout(out Rectangle headerRect, out _, out _, out _, out _, out _, out _);
        using var titleBrush = new SolidBrush(Color.FromArgb(244, 247, 250));
        using var subBrush = new SolidBrush(Color.FromArgb(190, 204, 216, 226));
        using var accent = new SolidBrush(Color.FromArgb(220, 232, 194, 82));
        graphics.FillRectangle(accent, headerRect.X, headerRect.Y + 4, 132, 4);
        TextRenderer.DrawText(graphics, "\u6218\u672f\u6a21\u62df\u5e73\u53f0", _menuEyebrowFont, new Rectangle(headerRect.X, headerRect.Y + 16, headerRect.Width, 18), Color.FromArgb(214, 224, 232, 238), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, "ARTINX A-SOUL", _menuTitleFont, new Rectangle(headerRect.X, headerRect.Y + 34, headerRect.Width, 34), titleBrush.Color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, "OpenGK \u4e3b\u754c\u9762  /  RMUC 2026", _menuSubtitleFont, new Rectangle(headerRect.X, headerRect.Y + 70, headerRect.Width, 20), subBrush.Color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, "OpenGL \u573a\u5730\u4e0e\u673a\u4eba\u9884\u89c8\u5df2\u5e38\u9a7b\u540e\u53f0", _menuEyebrowFont, new Rectangle(headerRect.X, headerRect.Y + 90, headerRect.Width + 36, 18), Color.FromArgb(176, 196, 208, 220), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkMainActions(Graphics graphics)
    {
        ResolveOpenGkMainMenuLayout(out _, out int x, out int y, out int width, out int h, out int smallH, out int gap);
        float editorExpand = (float)_mainMenuEditorExpandedVisual;

        DrawOpenGkMenuButton(graphics, new Rectangle(x, y, width, h), "\u5f00\u59cb\u6e38\u620f", "main_menu_toggle_start", false, Color.FromArgb(74, 142, 226), 1f, primary: true);
        y += h + gap;

        DrawOpenGkMenuButton(graphics, new Rectangle(x, y, width, h), "\u7f16\u8f91\u5668", "main_menu_toggle_editor", _mainMenuEditorExpanded, Color.FromArgb(84, 112, 132), 1f);
        y += h + gap;
        if (editorExpand > 0.05f)
        {
            DrawOpenGkMenuButton(graphics, new Rectangle(x + 18, y, width - 18, smallH), "\u5730\u5f62\u7f16\u8f91\u5668", "menu_open_terrain_editor", false, Color.FromArgb(96, 126, 146), editorExpand);
            y += smallH + 6;
            DrawOpenGkMenuButton(graphics, new Rectangle(x + 18, y, width - 18, smallH), "\u5916\u89c2\u7f16\u8f91\u5668", "menu_open_appearance_editor", false, Color.FromArgb(96, 126, 146), editorExpand);
            y += smallH + 6;
            DrawOpenGkMenuButton(graphics, new Rectangle(x + 18, y, width - 18, smallH), "\u89c4\u5219\u7f16\u8f91\u5668", "menu_open_rule_editor", false, Color.FromArgb(96, 126, 146), editorExpand);
            y += smallH + 6;
            DrawOpenGkMenuButton(graphics, new Rectangle(x + 18, y, width - 18, smallH), "\u5c40\u5185\u5149\u7167\u7f16\u8f91\u5668", "menu_open_lighting_editor", false, Color.FromArgb(96, 126, 146), editorExpand);
            y += smallH + 6;
            DrawOpenGkMenuButton(graphics, new Rectangle(x + 18, y, width - 18, smallH), _host.LightingEnabled ? "\u5149\u5f71\uff1a\u5f00" : "\u5149\u5f71\uff1a\u5173", "menu_toggle_lighting", _host.LightingEnabled, Color.FromArgb(96, 126, 146), editorExpand);
            y += smallH + gap;
        }

        y += 4;
        DrawOpenGkMenuButton(graphics, new Rectangle(x, y, width, h), "\u9000\u51fa", "menu_exit", false, Color.FromArgb(92, 100, 112), 1f);
    }

    private void ResolveOpenGkMainMenuLayout(out Rectangle headerRect, out int x, out int y, out int width, out int h, out int smallH, out int gap)
    {
        x = 36;
        width = Math.Clamp(ClientSize.Width / 4, 324, 392);
        h = ClientSize.Height < 760 ? 40 : 46;
        smallH = ClientSize.Height < 760 ? 32 : 36;
        gap = ClientSize.Height < 760 ? 7 : 9;
        headerRect = new Rectangle(x, 26, Math.Min(width + 92, 520), 116);
        y = headerRect.Bottom + 22;
    }

    private void DrawOpenGkMenuButton(Graphics graphics, Rectangle rect, string label, string action, bool active, Color accentColor, float reveal, bool primary = false)
    {
        reveal = Math.Clamp(reveal, 0f, 1f);
        if (reveal <= 0.01f)
        {
            return;
        }

        float hover = ResolveUiHoverMix(action);
        Rectangle drawRect = hover > 0.01f ? Rectangle.Inflate(rect, 1, 1) : rect;
        using GraphicsPath path = CreateRoundedRectangle(drawRect, 6);
        Color baseColor = primary ? Color.FromArgb(212, 28, 42, 58) : Color.FromArgb(172, 14, 21, 30);
        Color fillColor = BlendUiColor(baseColor, accentColor, (active ? 0.48f : 0.16f) + hover * 0.22f);
        using var fill = new SolidBrush(ApplyUiAlpha(fillColor, reveal));
        using var border = new Pen(ApplyUiAlpha(active ? Color.FromArgb(230, 244, 220, 96) : Color.FromArgb(154, 170, 184, 198), reveal), active ? 1.6f : 1.0f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var accent = new SolidBrush(ApplyUiAlpha(accentColor, reveal * 0.88f));
        graphics.FillRectangle(accent, drawRect.X, drawRect.Y, 4, drawRect.Height);
        DrawUiButtonText(graphics, drawRect, label, rect.Height >= 40 ? _menuButtonFont : _smallHudFont, Color.FromArgb((int)(242 * reveal), 246, 248, 252));
        if (!string.IsNullOrWhiteSpace(action) && reveal > 0.25f)
        {
            _uiButtons.Add(new UiButton(Rectangle.Inflate(drawRect, 6, 4), action));
        }
    }

    private void DrawOpenGkVehicleCarousel(Graphics graphics)
    {
        IReadOnlyList<SimulationEntity> roster = BuildOpenGkShowcaseRoster();
        if (roster.Count == 0)
        {
            return;
        }

        int cardWidth = Math.Clamp(ClientSize.Width / 9, 112, 156);
        int cardHeight = Math.Clamp(ClientSize.Height / 8, 82, 106);
        int gap = 10;
        int totalWidth = roster.Count * cardWidth + (roster.Count - 1) * gap;
        int x = Math.Max(24, (ClientSize.Width - totalWidth) / 2);
        int y = ClientSize.Height - cardHeight - 22;
        using var labelBrush = new SolidBrush(Color.FromArgb(216, 228, 234, 238));
        graphics.DrawString("\u8f66\u8f86\u7acb\u7ed8", _menuEyebrowFont, labelBrush, x, y - 22);

        for (int i = 0; i < roster.Count; i++)
        {
            SimulationEntity entity = roster[i];
            Rectangle card = new(x + i * (cardWidth + gap), y, cardWidth, cardHeight);
            bool selected = string.Equals(_host.SelectedEntity?.Id, entity.Id, StringComparison.OrdinalIgnoreCase);
            DrawOpenGkVehicleCarouselCard(graphics, card, entity, selected);
        }
    }

    private void DrawOpenGkVehicleCarouselCard(Graphics graphics, Rectangle card, SimulationEntity entity, bool selected)
    {
        float hover = ResolveUiHoverMix($"main_showcase_pick:{entity.Id}");
        using GraphicsPath path = CreateRoundedRectangle(hover > 0.01f ? Rectangle.Inflate(card, 1, 1) : card, 6);
        using var fill = new SolidBrush(selected ? Color.FromArgb(214, 30, 38, 48) : Color.FromArgb(176, 8, 12, 18));
        using var border = new Pen(selected ? Color.FromArgb(236, 242, 210, 82) : BlendUiColor(Color.FromArgb(120, 126, 144, 160), Color.FromArgb(190, 218, 230, 244), hover), selected ? 1.7f : 1.0f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        Rectangle subtitleRect = new(card.X + 8, card.Bottom - 22, card.Width - 16, 18);
        Rectangle titleRect = new(card.X + 8, subtitleRect.Y - 20, card.Width - 16, 18);
        Rectangle icon = new(card.X + 12, card.Y + 10, card.Width - 24, Math.Max(20, titleRect.Y - card.Y - 14));
        DrawOpenGkVehicleSilhouette(graphics, icon, ResolveTeamColor(entity.Team), entity.IsAlive);

        string entityKey = ExtractEntityKey(entity.Id);
        string number = entityKey.StartsWith("robot_", StringComparison.OrdinalIgnoreCase) ? entityKey[6..] : entityKey;
        string label = $"{number} {ResolveRoleLabel(entity)}";
        TextRenderer.DrawText(graphics, label, _tinyHudFont, titleRect, Color.WhiteSmoke, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, ResolveInfantrySubtypeLabelSafe(entity), _tinyHudFont, subtitleRect, Color.FromArgb(174, 198, 208, 220), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        _uiButtons.Add(new UiButton(Rectangle.Inflate(card, 6, 4), $"main_showcase_pick:{entity.Id}"));
    }

    private static string ResolveInfantrySubtypeLabelSafe(SimulationEntity entity)
        => string.Equals(entity.RoleKey, "infantry", StringComparison.OrdinalIgnoreCase)
            ? ResolveInfantrySubtypeLabel(entity)
            : ResolveRoleLabel(entity.RoleKey);

    private void DrawOpenGkVehicleSilhouette(Graphics graphics, Rectangle rect, Color teamColor, bool alive)
    {
        Color body = alive ? Color.FromArgb(228, teamColor) : Color.FromArgb(156, 102, 108, 116);
        using var fill = new SolidBrush(body);
        using var dark = new SolidBrush(Color.FromArgb(alive ? 210 : 132, 8, 10, 14));
        using var edge = new Pen(Color.FromArgb(190, 230, 236, 242), 1f);
        Rectangle bodyRect = new(rect.X + rect.Width / 7, rect.Y + rect.Height / 3, rect.Width * 5 / 7, rect.Height / 3);
        Rectangle turret = new(rect.X + rect.Width * 2 / 5, rect.Y + rect.Height / 5, rect.Width / 4, rect.Height / 4);
        graphics.FillRectangle(fill, bodyRect);
        graphics.DrawRectangle(edge, bodyRect);
        graphics.FillRectangle(fill, turret);
        graphics.DrawRectangle(edge, turret);
        graphics.FillRectangle(fill, turret.Right - 2, turret.Y + turret.Height / 2 - 2, rect.Right - turret.Right - 4, 4);
        graphics.FillEllipse(dark, bodyRect.X + 5, bodyRect.Bottom - 4, 16, 12);
        graphics.FillEllipse(dark, bodyRect.Right - 21, bodyRect.Bottom - 4, 16, 12);
    }

    private void DrawOpenGkLanRoomPanel(Graphics graphics, Rectangle mainPanel, Color accentColor, float reveal)
    {
        if (!_localRoomPanelOpen && !_lanRoomPanelOpen && _lanSession is null)
        {
            return;
        }

        using var overlay = new SolidBrush(Color.FromArgb(118, 0, 0, 0));
        graphics.FillRectangle(overlay, ClientRectangle);

        if (_localRoomPanelOpen || _lanSession is not null)
        {
            DrawOpenGkLanRoomScreen(graphics);
        }
        else if (_lanRoomHostMode)
        {
            DrawOpenGkLanCreateModal(graphics);
        }
        else
        {
            DrawOpenGkLanJoinModal(graphics);
        }
    }

    private void DrawOpenGkLanCreateModal(Graphics graphics)
    {
        int width = Math.Min(540, Math.Max(420, ClientSize.Width - 80));
        int height = 392;
        Rectangle panel = new((ClientSize.Width - width) / 2, Math.Max(72, (ClientSize.Height - height) / 2), width, height);
        DrawOpenGkModalPanel(graphics, panel, "\u521b\u5efa\u623f\u95f4");

        int x = panel.X + 42;
        int y = panel.Y + 88;
        int contentWidth = panel.Width - 84;
        DrawOpenGkLanInput(graphics, new Rectangle(x, y, contentWidth, 48), "\u623f\u95f4\u540d\u79f0", "room", _lanRoomNameText, "ARTINX 5v5 \u623f\u95f4", enabled: true);
        y += 66;
        DrawOpenGkLanInput(graphics, new Rectangle(x, y, contentWidth, 48), "\u88c1\u5224\u540d\u79f0", "player", _lanPlayerNameText, Environment.UserName, enabled: true);
        y += 66;
        DrawOpenGkLanInput(graphics, new Rectangle(x, y, contentWidth, 48), "\u4e3b\u673a\u7aef\u53e3", "port", _lanPortText, "26011", enabled: true);
        y += 58;
        DrawOpenGkCheckbox(graphics, new Rectangle(x, y, contentWidth, 30), "\u79c1\u6709\u623f\u95f4", _lanRoomPrivate, "lan_toggle_private");

        using var statusBrush = new SolidBrush(Color.FromArgb(192, 206, 218, 228));
        graphics.DrawString(ResolveOpenGkLanStatusText(), _tinyHudFont, statusBrush, new RectangleF(x, panel.Bottom - 86, contentWidth, 18));

        int buttonWidth = (contentWidth - 14) / 2;
        DrawOpenGkButton(graphics, new Rectangle(x, panel.Bottom - 54, buttonWidth, 36), "\u53d6\u6d88", "lan_room_close", false, Color.FromArgb(78, 88, 100), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(x + buttonWidth + 14, panel.Bottom - 54, buttonWidth, 36), _lanRoomBusy ? "\u521b\u5efa\u4e2d..." : "\u521b\u5efa\u623f\u95f4", _lanRoomBusy ? string.Empty : "lan_room_connect", true, Color.FromArgb(64, 132, 226), enabled: !_lanRoomBusy);
    }

    private void DrawOpenGkLanJoinModal(Graphics graphics)
    {
        int width = Math.Min(640, Math.Max(460, ClientSize.Width - 96));
        int height = 352;
        Rectangle panel = new((ClientSize.Width - width) / 2, Math.Max(86, (ClientSize.Height - height) / 2), width, height);
        DrawOpenGkModalPanel(graphics, panel, "\u52a0\u5165\u623f\u95f4");

        int x = panel.X + 52;
        int y = panel.Y + 86;
        int contentWidth = panel.Width - 104;
        DrawOpenGkLanInput(graphics, new Rectangle(x, y, contentWidth, 54), "\u4e3b\u673a IP", "host", _lanHostAddressText, "192.168.1.10", enabled: true);
        y += 72;
        int half = (contentWidth - 14) / 2;
        DrawOpenGkLanInput(graphics, new Rectangle(x, y, half, 54), "\u7aef\u53e3", "port", _lanPortText, "26011", enabled: true);
        DrawOpenGkLanInput(graphics, new Rectangle(x + half + 14, y, half, 54), "\u73a9\u5bb6\u540d\u79f0", "player", _lanPlayerNameText, Environment.UserName, enabled: true);

        using var statusBrush = new SolidBrush(Color.FromArgb(196, 206, 218, 228));
        graphics.DrawString("直接输入主机 IP 与端口后加入。", _tinyHudFont, statusBrush, new RectangleF(x, y + 70, contentWidth, 18));
        graphics.DrawString(ResolveOpenGkLanStatusText(), _tinyHudFont, statusBrush, new RectangleF(x, panel.Bottom - 86, contentWidth, 18));

        int buttonWidth = (contentWidth - 14) / 2;
        DrawOpenGkButton(graphics, new Rectangle(x, panel.Bottom - 54, buttonWidth, 36), "\u53d6\u6d88", "lan_room_close", false, Color.FromArgb(78, 88, 100), enabled: true);
        DrawOpenGkButton(graphics, new Rectangle(x + buttonWidth + 14, panel.Bottom - 54, buttonWidth, 36), _lanRoomBusy ? "\u8fde\u63a5\u4e2d..." : "\u52a0\u5165\u623f\u95f4", _lanRoomBusy ? string.Empty : "lan_room_connect", true, Color.FromArgb(58, 112, 220), enabled: !_lanRoomBusy);
    }

    private void DrawOpenGkModalPanel(Graphics graphics, Rectangle panel, string title)
    {
        using GraphicsPath shadowPath = CreateRoundedRectangle(new Rectangle(panel.X + 8, panel.Y + 12, panel.Width, panel.Height), 10);
        using var shadow = new SolidBrush(Color.FromArgb(112, 0, 0, 0));
        graphics.FillPath(shadow, shadowPath);

        using GraphicsPath path = CreateRoundedRectangle(panel, 8);
        using var fill = new SolidBrush(Color.FromArgb(236, 7, 10, 15));
        using var border = new Pen(Color.FromArgb(164, 166, 178, 192), 1.1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var titleBrush = new SolidBrush(Color.FromArgb(244, 248, 252));
        using var accent = new SolidBrush(Color.FromArgb(230, 66, 136, 226));
        graphics.FillRectangle(accent, panel.X + 32, panel.Y + 28, 96, 4);
        TextRenderer.DrawText(graphics, title, _menuButtonFont, new Rectangle(panel.X, panel.Y + 40, panel.Width, 32), Color.WhiteSmoke, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkLanInput(Graphics graphics, Rectangle rect, string label, string field, string value, string placeholder, bool enabled)
    {
        Rectangle labelRect = new(rect.X, rect.Y, Math.Min(116, rect.Width / 3), rect.Height);
        Rectangle input = new(labelRect.Right + 10, rect.Y, rect.Right - labelRect.Right - 10, rect.Height);
        bool focused = string.Equals(_lanFocusedField, field, StringComparison.OrdinalIgnoreCase);
        float hover = ResolveUiHoverMix($"lan_field:{field}");

        TextRenderer.DrawText(graphics, label, _smallHudFont, labelRect, enabled ? Color.FromArgb(226, 232, 238) : Color.FromArgb(138, 150, 160), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using GraphicsPath path = CreateRoundedRectangle(input, 5);
        using var fill = new SolidBrush(focused ? Color.FromArgb(224, 24, 42, 62) : BlendUiColor(Color.FromArgb(182, 18, 24, 34), Color.FromArgb(196, 40, 62, 86), hover * 0.5f));
        using var border = new Pen(focused ? Color.FromArgb(230, 96, 170, 246) : Color.FromArgb(126, 124, 144, 164), focused ? 1.5f : 1.0f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string display = string.IsNullOrWhiteSpace(value) ? placeholder : value;
        Color textColor = string.IsNullOrWhiteSpace(value) ? Color.FromArgb(142, 176, 188, 202) : Color.WhiteSmoke;
        TextRenderer.DrawText(graphics, display, _smallHudFont, Rectangle.Inflate(input, -12, -2), textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (enabled)
        {
            _uiButtons.Add(new UiButton(input, $"lan_field:{field}"));
        }
    }

    private void DrawOpenGkCheckbox(Graphics graphics, Rectangle rect, string label, bool active, string action)
    {
        Rectangle box = new(rect.X, rect.Y + 5, 20, 20);
        using var fill = new SolidBrush(active ? Color.FromArgb(220, 64, 132, 226) : Color.FromArgb(186, 18, 24, 34));
        using var border = new Pen(Color.FromArgb(160, 168, 182, 196), 1f);
        graphics.FillRectangle(fill, box);
        graphics.DrawRectangle(border, box);
        if (active)
        {
            using var check = new Pen(Color.WhiteSmoke, 2f);
            graphics.DrawLines(check, new[] { new Point(box.X + 4, box.Y + 10), new Point(box.X + 8, box.Y + 15), new Point(box.X + 16, box.Y + 5) });
        }

        TextRenderer.DrawText(graphics, label, _smallHudFont, new Rectangle(box.Right + 10, rect.Y, rect.Width - box.Width - 10, rect.Height), Color.FromArgb(224, 232, 238), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        _uiButtons.Add(new UiButton(rect, action));
    }

    private void DrawOpenGkLanRoomScreen(Graphics graphics)
    {
        try
        {
            DrawOpenGkLanRoomScreenUnsafe(graphics);
        }
        catch (Exception exception)
        {
            LogLocalRoomException("draw_room_screen", exception);
            _lanRoomScreenCacheBitmap?.Dispose();
            _lanRoomScreenCacheBitmap = null;
            _lanRoomScreenCacheKey = string.Empty;
            _lanRoomScreenCachedButtons.Clear();

            Rectangle fallback = new(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            using var fill = new SolidBrush(Color.FromArgb(220, 6, 9, 14));
            graphics.FillRectangle(fill, fallback);
            TextRenderer.DrawText(
                graphics,
                "房间面板刷新失败，已阻止闪退；详情见 logs/local_room_crash.log",
                _smallHudFont,
                fallback,
                Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawOpenGkLanRoomScreenUnsafe(Graphics graphics)
    {
        RememberLocalLanRoomMember();
        if (ClientSize.Width < 360 || ClientSize.Height < 260)
        {
            Rectangle fallback = new(0, 0, Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
            TextRenderer.DrawText(
                graphics,
                "窗口过小，无法显示房间面板",
                _smallHudFont,
                fallback,
                Color.WhiteSmoke,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        Rectangle root = new(
            Math.Max(22, ClientSize.Width / 28),
            Math.Max(18, ClientSize.Height / 30),
            Math.Max(1, ClientSize.Width - Math.Max(44, ClientSize.Width / 14)),
            Math.Max(1, ClientSize.Height - Math.Max(36, ClientSize.Height / 15)));

        string cacheKey = BuildLanRoomScreenCacheKey(root);
        if (_lanRoomScreenCacheBitmap is not null
            && _lanRoomScreenCacheRect == root
            && string.Equals(_lanRoomScreenCacheKey, cacheKey, StringComparison.Ordinal))
        {
            graphics.DrawImageUnscaled(_lanRoomScreenCacheBitmap, root.Location);
            _uiButtons.AddRange(_lanRoomScreenCachedButtons);
            return;
        }

        _lanRoomScreenCacheBitmap?.Dispose();
        _lanRoomScreenCacheBitmap = new Bitmap(Math.Max(1, root.Width), Math.Max(1, root.Height));
        _lanRoomScreenCacheRect = root;
        _lanRoomScreenCacheKey = cacheKey;
        _lanRoomScreenCachedButtons = new List<UiButton>(48);
        int buttonStart = _uiButtons.Count;
        using (Graphics cacheGraphics = Graphics.FromImage(_lanRoomScreenCacheBitmap))
        {
            cacheGraphics.Clear(Color.Transparent);
            cacheGraphics.SmoothingMode = graphics.SmoothingMode;
            cacheGraphics.CompositingQuality = graphics.CompositingQuality;
            cacheGraphics.InterpolationMode = graphics.InterpolationMode;
            cacheGraphics.PixelOffsetMode = graphics.PixelOffsetMode;
            cacheGraphics.TextRenderingHint = graphics.TextRenderingHint;
            DrawOpenGkLanRoomScreenCore(cacheGraphics, new Rectangle(0, 0, root.Width, root.Height));
        }

        if (_uiButtons.Count > buttonStart)
        {
            _lanRoomScreenCachedButtons = _uiButtons
                .Skip(buttonStart)
                .Select(button =>
                {
                    Rectangle rect = button.Rect;
                    rect.Offset(root.Location);
                    return new UiButton(rect, button.Action);
                })
                .ToList();
            _uiButtons.RemoveRange(buttonStart, _uiButtons.Count - buttonStart);
        }

        graphics.DrawImageUnscaled(_lanRoomScreenCacheBitmap, root.Location);
        _uiButtons.AddRange(_lanRoomScreenCachedButtons);
    }

    private string BuildLanRoomScreenCacheKey(Rectangle root)
    {
        var builder = new System.Text.StringBuilder(2048);
        builder.Append(root.Width).Append('x').Append(root.Height).Append('|');
        builder.Append(_lanRoomMatchMode).Append('|');
        builder.Append(_localRoomPanelOpen ? "local_room" : "lan_room").Append('|');
        builder.Append(_lanRoomNameText).Append('|');
        builder.Append(_lanPortText).Append('|');
        builder.Append(_lanRoomPrivate ? '1' : '0').Append('|');
        builder.Append(_lanLocalReady ? '1' : '0').Append('|');
        builder.Append(_lanLocalMemberRole).Append('|');
        builder.Append(_lanLocalTeam).Append('|');
        builder.Append(_lanLocalEntityKey).Append('|');
        builder.Append(_lanLocalSpawnPointIndex).Append('|');
        builder.Append(_lanSession?.IsHost == true ? "host" : "guest").Append('|');
        builder.Append(_lanSession?.ConnectedPeerCount ?? 0).Append('|');
        builder.Append(_localRoomStatusText).Append('|');
        builder.Append(_lanStatusLine).Append('|');
        builder.Append(_lanRoomSettings.SmallBulletDamage).Append('|')
            .Append(_lanRoomSettings.LargeBulletDamage).Append('|')
            .Append(_lanRoomSettings.HeroStartLevel).Append('|')
            .Append(_lanRoomSettings.EngineerStartLevel).Append('|')
            .Append(_lanRoomSettings.InfantryStartLevel).Append('|')
            .Append(_lanRoomSettings.RedStartGold).Append('|')
            .Append(_lanRoomSettings.BlueStartGold).Append('|');
        foreach (LanRoomMemberState member in EnumerateLanRoomMembers().OrderBy(candidate => candidate.MemberRole).ThenBy(candidate => candidate.Team).ThenBy(candidate => candidate.SlotIndex).ThenBy(candidate => candidate.PlayerName))
        {
            builder.Append(member.PlayerId).Append(':')
                .Append(member.PlayerName).Append(':')
                .Append(member.MemberRole).Append(':')
                .Append(member.Team).Append(':')
                .Append(member.EntityKey).Append(':')
                .Append(member.SlotIndex).Append(':')
                .Append(member.Ready ? '1' : '0').Append(':')
                .Append(member.SpawnPointIndex).Append(':')
                .Append(member.ChassisMode).Append('|');
        }

        return builder.ToString();
    }

    private void DrawOpenGkLanRoomScreenCore(Graphics graphics, Rectangle root)
    {
        OpenGkRoomScreenLayout layout = OpenGkRoomLayout.Resolve(root);
        DrawOpenGkRoomTopBar(graphics, layout.TopBar);
        DrawOpenGkTeamRoomColumn(graphics, layout.RedTeam, "red", "\u7ea2\u65b9");
        DrawOpenGkTeamRoomColumn(graphics, layout.BlueTeam, "blue", "\u84dd\u65b9");
        DrawOpenGkRefereeAndSettingsPanel(graphics, layout.RefereeAndSettings);

        if (_localRoomPanelOpen && _lanSession is null)
        {
            DrawOpenGkButton(graphics, layout.LeftAction, "离开房间", "local_room_disconnect", false, Color.FromArgb(96, 84, 88), enabled: true);
            bool canStart = _localRoomSeats.Count > 0;
            DrawOpenGkButton(graphics, layout.RightAction, "开始本地对局", "local_room_start_match", true, Color.FromArgb(64, 132, 226), enabled: canStart);
        }
        else
        {
            DrawOpenGkButton(graphics, layout.LeftAction, "\u79bb\u5f00\u623f\u95f4", "lan_room_disconnect", false, Color.FromArgb(96, 84, 88), enabled: true);
            if (_lanSession?.IsHost == true)
            {
                bool canStart = CanLanHostStartRoomMatch();
                DrawOpenGkButton(graphics, layout.RightAction, "\u5f00\u59cb\u5bf9\u5c40", "lan_room_start_match", true, Color.FromArgb(64, 132, 226), enabled: canStart);
            }
            else
            {
                DrawOpenGkButton(graphics, layout.RightAction, _lanLocalReady ? "\u53d6\u6d88\u51c6\u5907" : "\u51c6\u5907", "lan_toggle_ready", true, _lanLocalReady ? Color.FromArgb(96, 118, 138) : Color.FromArgb(64, 132, 226), enabled: true);
            }
        }
    }

    private void DrawOpenGkRoomTopBar(Graphics graphics, Rectangle rect)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 6);
        using var fill = new SolidBrush(Color.FromArgb(210, 5, 8, 12));
        using var border = new Pen(Color.FromArgb(126, 150, 166, 184), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string roomName = string.IsNullOrWhiteSpace(_lanRoomNameText) ? "ARTINX 5v5 \u623f\u95f4" : _lanRoomNameText;
        string endpoint = _localRoomPanelOpen && _lanSession is null
            ? "本机离线房间"
            : _lanSession?.IsHost == true
            ? $"{LanMultiplayerSession.ResolvePreferredLocalAddress()}:{_lanSession.Port}"
            : $"{_lanHostAddressText}:{_lanPortText}";
        int statusWidth = Math.Clamp(rect.Width / 3, 210, 340);
        int titleWidth = Math.Max(80, rect.Width - statusWidth - 44);
        TextRenderer.DrawText(graphics, roomName, _menuButtonFont, new Rectangle(rect.X + 18, rect.Y + 9, titleWidth, 28), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, $"\u623f\u95f4\u5730\u5740  {endpoint}", _tinyHudFont, new Rectangle(rect.X + 20, rect.Y + 40, titleWidth, 18), Color.FromArgb(192, 206, 218, 228), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        string status = _localRoomPanelOpen && _lanSession is null
            ? $"本地配置  机器人 {BuildLocalRoomSeatStates().Length}/10"
            : _lanSession?.IsHost == true
            ? $"\u88c1\u5224\u4e3b\u673a  \u73a9\u5bb6 {_lanSession.ConnectedPeerCount}/{_lanSession.MaxPlayerClients}"
            : "\u5df2\u8fde\u63a5\u5230\u88c1\u5224\u4e3b\u673a";
        TextRenderer.DrawText(graphics, status, _smallHudFont, new Rectangle(rect.Right - statusWidth - 18, rect.Y + 14, statusWidth, 26), Color.FromArgb(232, 232, 238, 244), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkTeamRoomColumn(Graphics graphics, Rectangle rect, string teamKey, string teamLabel)
    {
        Color teamColor = ResolveTeamColor(teamKey);
        using GraphicsPath path = CreateRoundedRectangle(rect, 6);
        using var fill = new SolidBrush(Color.FromArgb(202, 6, 9, 14));
        using var border = new Pen(Color.FromArgb(146, teamColor), 1.2f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        IReadOnlyList<LanRoomMemberState> members = EnumerateLanRoomMembers()
            .Where(member => string.Equals(member.MemberRole, "player", StringComparison.OrdinalIgnoreCase))
            .Where(member => string.Equals(Simulator3dOptions.NormalizeTeam(member.Team), teamKey, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        using var banner = new SolidBrush(Color.FromArgb(210, teamColor));
        graphics.FillRectangle(banner, rect.X, rect.Y, rect.Width, 36);
        bool canJoinTeam = !_localRoomPanelOpen && _lanSession is not null && _lanSession.IsHost != true;
        int joinWidth = canJoinTeam ? Math.Clamp(rect.Width / 3, 74, 96) : 0;
        TextRenderer.DrawText(graphics, $"{teamLabel}  ({members.Count}/{LanRobotSeatCatalog.ControllableEntityKeys.Count} 控制席)", _hudMidFont, new Rectangle(rect.X + 12, rect.Y + 4, rect.Width - joinWidth - 28, 28), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (canJoinTeam)
        {
            DrawOpenGkButton(graphics, new Rectangle(rect.Right - joinWidth - 12, rect.Y + 5, joinWidth, 26), $"加入{teamLabel}", $"lan_join_team:{teamKey}", false, teamColor, enabled: true);
        }

        int slotTop = rect.Y + 52;
        string[] slotEntityKeys = LanUcRoomSeatEntityKeys;
        int slotHeight = Math.Max(38, Math.Min(54, (rect.Height - 66) / slotEntityKeys.Length - 7));
        for (int i = 0; i < slotEntityKeys.Length; i++)
        {
            int globalSlot = string.Equals(teamKey, "blue", StringComparison.OrdinalIgnoreCase) ? slotEntityKeys.Length + i : i;
            LanRoomMemberState? member = members.FirstOrDefault(item => item.SlotIndex == globalSlot);
            DrawOpenGkTeamSlot(graphics, new Rectangle(rect.X + 12, slotTop + i * (slotHeight + 8), rect.Width - 24, slotHeight), teamKey, teamColor, slotEntityKeys[i], member);
        }
    }

    private void DrawOpenGkTeamSlot(Graphics graphics, Rectangle rect, string teamKey, Color teamColor, string entityKey, LanRoomMemberState? member)
    {
        bool occupied = member is not null;
        bool local = member?.IsLocal == true;
        bool placeholderOnly = LanRobotSeatCatalog.IsPlaceholderOnly(entityKey);
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        using var fill = new SolidBrush(placeholderOnly ? Color.FromArgb(98, 20, 24, 30) : local ? Color.FromArgb(218, 30, 46, 62) : occupied ? Color.FromArgb(196, 18, 24, 34) : Color.FromArgb(122, 18, 24, 30));
        using var border = new Pen(placeholderOnly ? Color.FromArgb(82, 122, 134, 148) : local ? Color.FromArgb(224, 232, 194, 82) : occupied ? Color.FromArgb(154, teamColor) : Color.FromArgb(92, 102, 114, 126), local ? 1.4f : 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        int iconSize = Math.Clamp(rect.Height - 14, 28, 48);
        Rectangle icon = new(rect.X + 8, rect.Y + (rect.Height - iconSize) / 2, iconSize, iconSize);
        DrawOpenGkVehicleSilhouette(graphics, icon, placeholderOnly ? Color.FromArgb(84, 120, 132) : occupied ? teamColor : Color.FromArgb(96, 112, 122), occupied && !placeholderOnly);
        string seatLabel = ResolveOpenGkRoomSeatLabel(teamKey, entityKey);
        string title = occupied ? member!.PlayerName : seatLabel;
        string detail = occupied
            ? $"{ResolveOpenGkLanTeamLabel(member!.Team)}  {seatLabel}  {(member.Ready ? "\u5df2\u51c6\u5907" : "\u672a\u51c6\u5907")}"
            : placeholderOnly
            ? "云台手占位，暂不生成机器人"
            : (_localRoomPanelOpen && _lanSession is null ? "点击添加本地玩家或 AI" : "\u7b49\u5f85\u73a9\u5bb6\u52a0\u5165");
        int textX = icon.Right + 10;
        int textWidth = Math.Max(20, rect.Right - textX - 10);
        int titleY = rect.Y + Math.Max(5, (rect.Height - 38) / 2);
        TextRenderer.DrawText(graphics, title, _smallHudFont, new Rectangle(textX, titleY, textWidth, 20), occupied ? Color.WhiteSmoke : Color.FromArgb(154, 166, 178), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, detail, _tinyHudFont, new Rectangle(textX, titleY + 21, textWidth, 17), Color.FromArgb(184, 202, 212, 224), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        if (placeholderOnly)
        {
            return;
        }

        if (_localRoomPanelOpen && _lanSession is null)
        {
            _uiButtons.Add(new UiButton(Rectangle.Inflate(rect, 4, 3), $"local_room_slot:{teamKey}:{entityKey}"));
        }
        else if (_lanSession is not null
            && _lanSession.IsHost != true
            && (!occupied || local))
        {
            _uiButtons.Add(new UiButton(Rectangle.Inflate(rect, 4, 3), $"lan_select_seat:{teamKey}:{entityKey}"));
        }
    }

    private static string ResolveOpenGkRoomSeatLabel(string teamKey, string entityKey)
    {
        string[] roomSeats = LanRobotSeatCatalog.RoomSeatEntityKeys.ToArray();
        int index = Array.FindIndex(
            roomSeats,
            key => string.Equals(key, entityKey, StringComparison.OrdinalIgnoreCase));
        string teamLabel = string.Equals(teamKey, "blue", StringComparison.OrdinalIgnoreCase) ? "蓝方" : "红方";
        return index >= 0 ? $"{teamLabel}席位 {index + 1}" : $"{teamLabel}控制席";
    }

    private void DrawOpenGkRefereeAndSettingsPanel(Graphics graphics, Rectangle rect)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 6);
        using var fill = new SolidBrush(Color.FromArgb(202, 6, 9, 14));
        using var border = new Pen(Color.FromArgb(124, 150, 166, 184), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        int x = rect.X + 16;
        int y = rect.Y + 14;
        TextRenderer.DrawText(graphics, _localRoomPanelOpen && _lanSession is null ? "本地 / AI" : "\u88c1\u5224 / \u89c2\u4f17", _hudMidFont, new Rectangle(x, y, rect.Width - 32, 24), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        y += 36;

        IReadOnlyList<LanRoomMemberState> members = EnumerateLanRoomMembers().ToArray();
        if (_localRoomPanelOpen && _lanSession is null)
        {
            int playerCount = members.Count(member => member.IsLocal);
            int aiCount = members.Count(member => !member.IsLocal);
            DrawOpenGkRoleLine(graphics, new Rectangle(x, y, rect.Width - 32, 34), "本地玩家", $"{playerCount}/1", playerCount > 0);
            y += 42;
            DrawOpenGkRoleLine(graphics, new Rectangle(x, y, rect.Width - 32, 34), "AI 人机", $"{aiCount}/9", aiCount > 0);
            y += 42;
        }
        else
        {
            LanRoomMemberState[] referees = members.Where(member => string.Equals(member.MemberRole, "referee", StringComparison.OrdinalIgnoreCase)).ToArray();
            LanRoomMemberState[] spectators = members.Where(member => string.Equals(member.MemberRole, "spectator", StringComparison.OrdinalIgnoreCase)).ToArray();
            string refereeText = IsLanDuelRoomMode
                ? "1v1 无裁判"
                : (referees.Length == 0 ? "等待中" : string.Join(" / ", referees.Take(LanMaxRefereeCount).Select(member => member.PlayerName)));
            DrawOpenGkRoleLine(graphics, new Rectangle(x, y, rect.Width - 32, 34), "\u88c1\u5224", $"{refereeText}  ({referees.Length}/{(IsLanDuelRoomMode ? 0 : LanMaxRefereeCount)})", referees.Length > 0 || IsLanDuelRoomMode);
            y += 42;
            string spectatorText = spectators.Length == 0 ? "等待中" : string.Join(" / ", spectators.Take(2).Select(member => member.PlayerName));
            DrawOpenGkRoleLine(graphics, new Rectangle(x, y, rect.Width - 32, 34), "\u89c2\u4f17", $"{spectatorText}  ({spectators.Length}/{LanMaxSpectatorCount})", spectators.Length > 0);
            y += 42;
            if (_lanSession is not null && _lanSession.IsHost != true)
            {
                DrawOpenGkButton(graphics, new Rectangle(x, y, rect.Width - 32, 28), "\u4f5c\u4e3a\u89c2\u4f17", "lan_role_select:spectator", false, Color.FromArgb(88, 112, 138), enabled: true);
                y += 36;
                if (!IsLanDuelRoomMode)
                {
                    DrawOpenGkButton(graphics, new Rectangle(x, y, rect.Width - 32, 28), "\u4f5c\u4e3a\u88c1\u5224", "lan_role_select:referee", false, Color.FromArgb(122, 112, 88), enabled: true);
                    y += 36;
                }
            }
        }

        y += 20;
        using var accent = new SolidBrush(Color.FromArgb(222, 232, 194, 82));
        graphics.FillRectangle(accent, x, y, 74, 3);
        y += 12;
        TextRenderer.DrawText(graphics, "\u6e38\u620f\u8bbe\u7f6e", _hudMidFont, new Rectangle(x, y, rect.Width - 32, 24), Color.WhiteSmoke, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        y += 34;

        int lineWidth = rect.Width - 32;
        DrawOpenGkSettingLine(graphics, x, ref y, lineWidth, "\u8d5b\u5236", ResolveLanRoomModeLabel());
        DrawOpenGkSettingLine(graphics, x, ref y, lineWidth, "\u5730\u56fe", ResolveLobbyMapLabel());
        DrawOpenGkSettingLine(graphics, x, ref y, lineWidth, "\u623f\u95f4", _lanRoomPrivate ? "\u79c1\u6709" : "\u516c\u5f00");
        DrawOpenGkSettingLine(graphics, x, ref y, lineWidth, "\u5f39\u9053\u7269\u7406", ResolveOpenGkPhysicsBackendLabel(_host.ProjectilePhysicsBackend));
        DrawOpenGkSettingLine(graphics, x, ref y, lineWidth, "\u5ba2\u6237\u7aef", $"\u6700\u5927 {_lanSession?.MaxPlayerClients ?? 10}");
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u5c0f\u5f39\u4f24\u5bb3", _lanRoomSettings.SmallBulletDamage.ToString(CultureInfo.InvariantCulture), "small_damage", 1, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u5927\u5f39\u4f24\u5bb3", _lanRoomSettings.LargeBulletDamage.ToString(CultureInfo.InvariantCulture), "large_damage", 5, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u82f1\u96c4\u521d\u59cb\u7b49\u7ea7", _lanRoomSettings.HeroStartLevel.ToString(CultureInfo.InvariantCulture), "hero_level", 1, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u5de5\u7a0b\u521d\u59cb\u7b49\u7ea7", _lanRoomSettings.EngineerStartLevel.ToString(CultureInfo.InvariantCulture), "engineer_level", 1, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u6b65\u5175\u521d\u59cb\u7b49\u7ea7", _lanRoomSettings.InfantryStartLevel.ToString(CultureInfo.InvariantCulture), "infantry_level", 1, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u7ea2\u65b9\u521d\u59cb\u91d1\u5e01", _lanRoomSettings.RedStartGold.ToString(CultureInfo.InvariantCulture), "red_gold", 1, CanEditLanRoomSettings());
        DrawOpenGkAdjustableSettingLine(graphics, x, ref y, lineWidth, "\u84dd\u65b9\u521d\u59cb\u91d1\u5e01", _lanRoomSettings.BlueStartGold.ToString(CultureInfo.InvariantCulture), "blue_gold", 1, CanEditLanRoomSettings());

        string status = ResolveOpenGkLanStatusText();
        TextRenderer.DrawText(graphics, status, _tinyHudFont, new Rectangle(x, rect.Bottom - 56, rect.Width - 32, 44), Color.FromArgb(184, 202, 214, 226), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkRoleLine(Graphics graphics, Rectangle rect, string label, string value, bool active)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        using var fill = new SolidBrush(active ? Color.FromArgb(186, 22, 30, 42) : Color.FromArgb(112, 18, 24, 30));
        using var border = new Pen(active ? Color.FromArgb(152, 232, 194, 82) : Color.FromArgb(86, 106, 118, 130), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        int labelWidth = Math.Clamp(rect.Width / 4, 46, 72);
        TextRenderer.DrawText(graphics, label, _tinyHudFont, new Rectangle(rect.X + 10, rect.Y, labelWidth, rect.Height), Color.FromArgb(184, 202, 214, 226), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, value, _smallHudFont, new Rectangle(rect.X + labelWidth + 16, rect.Y, rect.Width - labelWidth - 26, rect.Height), active ? Color.WhiteSmoke : Color.FromArgb(154, 166, 178), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkSettingLine(Graphics graphics, int x, ref int y, int lineWidth, string label, string value)
    {
        Rectangle row = new(x, y, lineWidth, 24);
        int labelWidth = ResolveOpenGkSettingLabelWidth(lineWidth);
        Rectangle labelRect = new(row.X, row.Y + 1, labelWidth, row.Height - 2);
        Rectangle valueRect = new(row.X + labelWidth + 8, row.Y, Math.Max(24, row.Width - labelWidth - 8), row.Height);
        TextRenderer.DrawText(graphics, label, _tinyHudFont, labelRect, Color.FromArgb(172, 190, 202, 214), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        using GraphicsPath valuePath = CreateRoundedRectangle(valueRect, 4);
        using var fill = new SolidBrush(Color.FromArgb(118, 18, 24, 30));
        using var border = new Pen(Color.FromArgb(86, 106, 118, 130), 1f);
        graphics.FillPath(fill, valuePath);
        graphics.DrawPath(border, valuePath);
        TextRenderer.DrawText(graphics, value, _tinyHudFont, Rectangle.Inflate(valueRect, -10, -1), Color.FromArgb(226, 232, 238), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        y += 28;
    }

    private void DrawOpenGkAdjustableSettingLine(Graphics graphics, int x, ref int y, int lineWidth, string label, string value, string key, int step, bool editable)
    {
        Rectangle lineRect = new(x, y, lineWidth, 24);
        int labelWidth = ResolveOpenGkSettingLabelWidth(lineWidth);
        Rectangle labelRect = new(lineRect.X, lineRect.Y + 1, labelWidth, lineRect.Height - 2);
        Rectangle valueRect = new(lineRect.X + labelWidth + 8, lineRect.Y, Math.Max(24, lineRect.Width - labelWidth - 8), lineRect.Height);
        TextRenderer.DrawText(graphics, label, _tinyHudFont, labelRect, Color.FromArgb(172, 190, 202, 214), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        using GraphicsPath valuePath = CreateRoundedRectangle(valueRect, 4);
        using var fill = new SolidBrush(Color.FromArgb(118, 18, 24, 30));
        using var border = new Pen(Color.FromArgb(86, 106, 118, 130), 1f);
        graphics.FillPath(fill, valuePath);
        graphics.DrawPath(border, valuePath);
        int buttonReserve = editable ? 66 : 0;
        TextRenderer.DrawText(graphics, value, _tinyHudFont, new Rectangle(valueRect.X + 10, valueRect.Y + 2, Math.Max(18, valueRect.Width - buttonReserve - 16), 20), Color.FromArgb(226, 232, 238), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix);
        if (editable)
        {
            DrawOpenGkButton(graphics, new Rectangle(valueRect.Right - 58, valueRect.Y + 1, 24, 22), "-", $"lan_room_setting:{key}:{-step}", false, Color.FromArgb(82, 96, 118), enabled: true);
            DrawOpenGkButton(graphics, new Rectangle(valueRect.Right - 28, valueRect.Y + 1, 24, 22), "+", $"lan_room_setting:{key}:{step}", false, Color.FromArgb(82, 128, 168), enabled: true);
        }

        y += 28;
    }

    private static int ResolveOpenGkSettingLabelWidth(int lineWidth)
        => Math.Clamp((int)Math.Round(lineWidth * 0.42), 84, Math.Max(84, lineWidth - 86));

    private string ResolveLanRoomModeLabel()
        => IsLanDuelRoomMode ? "1v1" : "RoboMaster 2026 UC";

    private static string ResolveOpenGkPhysicsBackendLabel(string backend)
        => string.Equals(backend, "bepu", StringComparison.OrdinalIgnoreCase) ? "BEPU" : "\u539f\u751f";

    private void DrawOpenGkButton(Graphics graphics, Rectangle rect, string label, string action, bool active, Color accent, bool enabled)
    {
        float hover = enabled ? ResolveUiHoverMix(action) : 0f;
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        Color fillColor = enabled
            ? BlendUiColor(Color.FromArgb(192, 24, 31, 42), accent, (active ? 0.58f : 0.22f) + hover * 0.24f)
            : Color.FromArgb(120, 32, 38, 46);
        using var fill = new SolidBrush(fillColor);
        using var border = new Pen(enabled ? Color.FromArgb(146, 178, 196, 214) : Color.FromArgb(82, 112, 122, 132), active ? 1.4f : 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
        Font preferredFont = rect.Height <= 34 ? _smallHudFont : _menuSubtitleFont;
        DrawUiButtonText(graphics, rect, label, ResolveUiButtonFont(graphics, label, rect, preferredFont, _tinyHudFont), enabled ? Color.WhiteSmoke : Color.FromArgb(138, 150, 160));
        if (enabled && !string.IsNullOrWhiteSpace(action))
        {
            _uiButtons.Add(new UiButton(Rectangle.Inflate(rect, 6, 4), action));
        }
    }

    private string ResolveOpenGkLanStatusText()
    {
        if (_localRoomPanelOpen && _lanSession is null)
        {
            return string.IsNullOrWhiteSpace(_localRoomStatusText)
                ? "本地房间默认不填充机器人，点击席位添加本地玩家或 AI。"
                : _localRoomStatusText;
        }

        if (!IsLikelyMojibake(_lanStatusLine) && !string.IsNullOrWhiteSpace(_lanStatusLine))
        {
            return _lanStatusLine;
        }

        if (_lanSession is not null)
        {
            return _lanSession.IsHost
                ? $"\u623f\u95f4\u5df2\u521b\u5efa\uff0c\u6b63\u5728\u76d1\u542c {_lanSession.Port} \u7aef\u53e3\uff0c\u7b49\u5f85\u73a9\u5bb6\u52a0\u5165\u3002"
                : "\u5df2\u8fde\u63a5\u88c1\u5224\u4e3b\u673a\uff0c\u7b49\u5f85\u623f\u95f4\u540c\u6b65\u3002";
        }

        if (_lanRoomBusy)
        {
            return _lanRoomHostMode ? "\u6b63\u5728\u521b\u5efa\u623f\u95f4..." : "\u6b63\u5728\u8fde\u63a5\u4e3b\u673a...";
        }

        if (!IsLikelyMojibake(_lanRoomStatusText) && !string.IsNullOrWhiteSpace(_lanRoomStatusText))
        {
            return _lanRoomStatusText;
        }

        return _lanRoomHostMode
            ? "\u5728\u6b64\u521b\u5efa\u7531\u88c1\u5224\u4e3b\u673a\u7ba1\u7406\u7684\u623f\u95f4\u3002"
            : "\u8f93\u5165\u4e3b\u673a IP \u548c\u7aef\u53e3\u4ee5\u52a0\u5165\u3002";
    }

    private static bool IsLikelyMojibake(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> value = text.AsSpan();
        string[] markers =
        {
            "�",
            "鍒",
            "閫",
            "绔",
            "闂",
            "鏈",
            "宸",
            "瑁",
            "鍔",
            "鐜",
            "濂",
            "缁",
            "瀹",
            "閹",
            "鈧",
        };

        foreach (string marker in markers)
        {
            if (value.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveOpenGkLanTeamLabel(string team)
        => string.Equals(Simulator3dOptions.NormalizeTeam(team), "blue", StringComparison.OrdinalIgnoreCase) ? "\u84dd\u65b9" : "\u7ea2\u65b9";

    private static string ResolveOpenGkLanEntityLabel(string entityKey)
        => NormalizeLanDuelEntityKey(entityKey) switch
        {
            "robot_1" => "\u82f1\u96c4",
            "robot_2" => "\u5de5\u7a0b",
            "robot_4" => "\u6b65\u51752",
            "robot_6" => "云台手",
            "robot_7" => "\u54e8\u5175",
            _ => "\u6b65\u51751",
        };

    private void RememberLocalLanRoomMember()
    {
        if (_lanSession is null)
        {
            return;
        }

        string role = ResolveLanLocalMemberRole();
        int slot = string.Equals(role, "player", StringComparison.OrdinalIgnoreCase)
            ? ResolveLanSlotIndex(_lanLocalTeam, _lanLocalEntityKey)
            : -1;
        _lanRoomMembers["local"] = new LanRoomMemberState(
            "local",
            _lanLocalPlayerId,
            string.IsNullOrWhiteSpace(_lanPlayerNameText) ? Environment.UserName : _lanPlayerNameText.Trim(),
            _lanLocalTeam,
            _lanLocalEntityKey,
            slot,
            role,
            IsLocal: true,
            _lanLocalReady,
            _lanLocalSpawnPointIndex,
            ResolveLanPreparationChassisMode());
    }

    private void RememberLanRoomMember(LanLobbySelection selection)
        => RememberLanRoomMember(selection, ready: false);

    private void RememberLanRoomMember(LanLobbySelection selection, bool ready)
    {
        string name = string.IsNullOrWhiteSpace(selection.PlayerName) ? "\u73a9\u5bb6" : selection.PlayerName.Trim();
        string playerId = string.IsNullOrWhiteSpace(selection.PlayerId) ? string.Empty : selection.PlayerId.Trim();
        string role = string.IsNullOrWhiteSpace(selection.MemberRole) ? "player" : selection.MemberRole.Trim().ToLowerInvariant();
        string identity = !string.IsNullOrWhiteSpace(playerId)
            ? $"id:{playerId}"
            : $"name:{NormalizeLanPlayerName(name)}";
        string key = !string.IsNullOrWhiteSpace(playerId)
            ? playerId
            : $"{role}:{selection.SlotIndex}:{name}";
        foreach (string staleKey in _lanRoomMembers
                     .Where(pair => !pair.Value.IsLocal && string.Equals(BuildLanRoomMemberIdentity(pair.Value), identity, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lanRoomMembers.Remove(staleKey);
        }

        _lanRoomMembers[key] = new LanRoomMemberState(
            key,
            playerId,
            name,
            Simulator3dOptions.NormalizeTeam(selection.Team),
            NormalizeLanDuelEntityKey(selection.EntityKey),
            selection.SlotIndex,
            role,
            IsLocal: false,
            ready,
            selection.SpawnPointIndex,
            selection.ChassisMode ?? string.Empty);
    }

    private IEnumerable<LanRoomMemberState> EnumerateLanRoomMembers()
    {
        if (_localRoomPanelOpen && _lanSession is null)
        {
            return EnumerateLocalRoomMembers();
        }

        RememberLocalLanRoomMember();
        var uniqueMembers = new Dictionary<string, LanRoomMemberState>(StringComparer.OrdinalIgnoreCase);
        foreach (LanRoomMemberState member in _lanRoomMembers.Values
            .OrderByDescending(member => member.IsLocal)
            .ThenByDescending(member => member.Ready)
            .ThenByDescending(member => member.PlayerId.Length)
            .ThenByDescending(member => member.SlotIndex))
        {
            string identity = BuildLanRoomMemberIdentity(member);
            if (!uniqueMembers.ContainsKey(identity))
            {
                uniqueMembers[identity] = member;
            }
        }

        return uniqueMembers.Values
            .OrderBy(member => member.MemberRole, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.SlotIndex)
            .ThenBy(member => member.PlayerName, StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildLanRoomMemberIdentity(LanRoomMemberState member)
    {
        if (!string.IsNullOrWhiteSpace(member.PlayerId))
        {
            return $"id:{member.PlayerId.Trim()}";
        }

        string name = NormalizeLanPlayerName(member.PlayerName);
        return string.IsNullOrWhiteSpace(name)
            ? $"slot:{member.MemberRole}:{member.SlotIndex}"
            : $"name:{name}";
    }

    private bool TryDrawCachedOpenGkUcTopHud(Graphics graphics)
    {
        Size size = new(Math.Max(1, ClientSize.Width), Math.Max(1, HudHeight));
        string cacheKey = BuildOpenGkUcTopHudCacheKey(size);
        bool rebuilt = false;
        if (_openGkUcTopHudCacheBitmap is null
            || _openGkUcTopHudCacheSize != size
            || !string.Equals(_openGkUcTopHudCacheKey, cacheKey, StringComparison.Ordinal))
        {
            InvalidateOpenGkUcTopHudCache();
            _openGkUcTopHudCacheBitmap = BuildOpenGkUcTopHudCacheBitmap(size);
            _openGkUcTopHudCacheSize = size;
            _openGkUcTopHudCacheKey = cacheKey;
            rebuilt = true;
        }

        if (_openGkUcTopHudCacheBitmap is null)
        {
            return false;
        }

        graphics.DrawImageUnscaled(_openGkUcTopHudCacheBitmap, 0, 0);
        if (!rebuilt)
        {
            RegisterOpenGkUcTopHudButtons();
        }
        return true;
    }

    private string BuildOpenGkUcTopHudCacheKey(Size size)
    {
        var builder = new System.Text.StringBuilder(512);
        int remainingSeconds = ResolveDisplayedMatchRemainingSeconds();
        builder.Append(size.Width).Append('x').Append(size.Height).Append('|');
        builder.Append(_paused ? 'p' : 'l').Append('|');
        builder.Append(_matchStartupPhase).Append('|');
        builder.Append(remainingSeconds).Append('|');
        builder.Append((int)Math.Round(_host.World.Teams.TryGetValue("red", out SimulationTeamState? redTeam) ? redTeam.Gold : 0.0)).Append('|');
        builder.Append((int)Math.Round(_host.World.Teams.TryGetValue("blue", out SimulationTeamState? blueTeam) ? blueTeam.Gold : 0.0)).Append('|');
        builder.Append(_host.SelectedEntity?.Id ?? string.Empty).Append('|');

        foreach (string teamKey in new[] { "red", "blue" })
        {
            SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
            SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
            builder.Append(teamKey).Append(':')
                .Append((int)Math.Round(ResolveHealthRatio(baseEntity) * 100f)).Append(':')
                .Append((int)Math.Round(ResolveHealthRatio(outpostEntity) * 100f)).Append('|');

            foreach (SimulationEntity unit in BuildTeamHudUnits(teamKey))
            {
                builder.Append(unit.Id).Append(':')
                    .Append(unit.IsAlive ? '1' : '0').Append(':')
                    .Append((int)Math.Round(Math.Max(0.0, unit.Health))).Append(':')
                    .Append(ResolveDisplayedAmmo(unit)).Append(':')
                    .Append((int)Math.Round(unit.RespawnTimerSec * 10.0)).Append('|');
            }
        }

        return builder.ToString();
    }

    private Bitmap? BuildOpenGkUcTopHudCacheBitmap(Size size)
    {
        if (size.Width <= 1 || size.Height <= 1)
        {
            return null;
        }

        Bitmap bitmap = new(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using Graphics cacheGraphics = Graphics.FromImage(bitmap);
        cacheGraphics.Clear(Color.Transparent);
        cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        cacheGraphics.CompositingQuality = CompositingQuality.HighSpeed;
        cacheGraphics.InterpolationMode = InterpolationMode.Bilinear;
        cacheGraphics.PixelOffsetMode = PixelOffsetMode.Half;
        cacheGraphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        DrawOpenGkUcTopHud(cacheGraphics);
        return bitmap;
    }

    private void RegisterOpenGkUcTopHudButtons()
    {
        int top = 4;
        int height = Math.Min(HudHeight - 8, Math.Max(126, ClientSize.Height / 8));
        int centerWidth = Math.Clamp(ClientSize.Width / 7, 176, 244);
        Rectangle center = new(ClientSize.Width / 2 - centerWidth / 2, top, centerWidth, height - 8);
        Rectangle red = new(8, top, Math.Max(260, center.Left - 16), height - 8);
        Rectangle blue = new(center.Right + 8, top, Math.Max(260, ClientSize.Width - center.Right - 16), height - 8);
        RegisterOpenGkUcTeamPanelButtons(red, "red", mirrored: false);
        RegisterOpenGkUcTeamPanelButtons(blue, "blue", mirrored: true);
    }

    private void RegisterOpenGkUcTeamPanelButtons(Rectangle rect, string teamKey, bool mirrored)
    {
        IReadOnlyList<SimulationEntity> units = BuildTeamHudUnits(teamKey);
        if (units.Count == 0)
        {
            return;
        }

        Rectangle[] cards = BuildOpenGkUcUnitCardRects(rect, units.Count);
        for (int i = 0; i < units.Count && i < cards.Length; i++)
        {
            int logicalIndex = mirrored ? units.Count - 1 - i : i;
            _uiButtons.Add(new UiButton(cards[i], $"match_select:{units[logicalIndex].Id}"));
        }
    }

    private void InvalidateOpenGkUcTopHudCache()
    {
        _openGkUcTopHudCacheBitmap?.Dispose();
        _openGkUcTopHudCacheBitmap = null;
        _openGkUcTopHudCacheSize = Size.Empty;
        _openGkUcTopHudCacheKey = string.Empty;
    }

    private void DrawOpenGkUcTopHud(Graphics graphics)
    {
        int top = 4;
        int height = Math.Min(HudHeight - 8, Math.Max(126, ClientSize.Height / 8));
        int centerWidth = Math.Clamp(ClientSize.Width / 7, 176, 244);
        Rectangle center = new(ClientSize.Width / 2 - centerWidth / 2, top, centerWidth, height - 8);
        Rectangle red = new(8, top, Math.Max(260, center.Left - 16), height - 8);
        Rectangle blue = new(center.Right + 8, top, Math.Max(260, ClientSize.Width - center.Right - 16), height - 8);

        DrawOpenGkUcTeamPanel(graphics, red, "red", "\u7ea2\u65b9", mirrored: false);
        DrawOpenGkUcCenterPanel(graphics, center);
        DrawOpenGkUcTeamPanel(graphics, blue, "blue", "\u84dd\u65b9", mirrored: true);
    }

    private void DrawOpenGkUcCenterPanel(Graphics graphics, Rectangle rect)
    {
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        using var fill = new SolidBrush(Color.FromArgb(218, 4, 7, 12));
        using var border = new Pen(Color.FromArgb(150, 180, 190, 204), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        int remainingSeconds = ResolveDisplayedMatchRemainingSeconds();
        string state = ResolveDisplayedMatchStateLabel("UC", "\u51c6\u5907");
        string timer = $"{remainingSeconds / 60}:{remainingSeconds % 60:00}";
        TextRenderer.DrawText(graphics, state, _tinyHudFont, new Rectangle(rect.X, rect.Y + 6, rect.Width, 18), Color.FromArgb(204, 218, 228, 238), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(graphics, timer, _hudBigFont, new Rectangle(rect.X, rect.Y + 24, rect.Width, 34), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        double redGold = _host.World.Teams.TryGetValue("red", out SimulationTeamState? redTeam) ? redTeam.Gold : 0.0;
        double blueGold = _host.World.Teams.TryGetValue("blue", out SimulationTeamState? blueTeam) ? blueTeam.Gold : 0.0;
        TextRenderer.DrawText(graphics, $"{(int)redGold}  :  {(int)blueGold}", _smallHudFont, new Rectangle(rect.X, rect.Bottom - 26, rect.Width, 20), Color.FromArgb(238, 236, 208, 88), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawOpenGkUcTeamPanel(Graphics graphics, Rectangle rect, string teamKey, string teamLabel, bool mirrored)
    {
        Color teamColor = ResolveTeamColor(teamKey);
        using GraphicsPath path = CreateRoundedRectangle(rect, 5);
        using var fill = new SolidBrush(Color.FromArgb(196, 5, 8, 12));
        using var border = new Pen(Color.FromArgb(126, teamColor), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        Rectangle banner = new(rect.X, rect.Y, rect.Width, 24);
        using var bannerFill = new LinearGradientBrush(
            banner,
            Color.FromArgb(218, teamColor),
            Color.FromArgb(76, teamColor),
            mirrored ? 180f : 0f);
        graphics.FillRectangle(bannerFill, banner);
        TextRenderer.DrawText(graphics, teamLabel, _smallHudFont, new Rectangle(rect.X + 10, rect.Y + 2, rect.Width - 20, 20), Color.White, mirrored ? TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix : TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        SimulationEntity? baseEntity = FindEntityById($"{teamKey}_base");
        SimulationEntity? outpostEntity = FindEntityById($"{teamKey}_outpost");
        int structureY = rect.Y + 29;
        int barWidth = Math.Max(72, (rect.Width - 28) / 2);
        Rectangle baseBar = mirrored
            ? new Rectangle(rect.Right - 10 - barWidth, structureY, barWidth, 16)
            : new Rectangle(rect.X + 10, structureY, barWidth, 16);
        Rectangle outpostBar = mirrored
            ? new Rectangle(baseBar.Left - 8 - barWidth, structureY, barWidth, 16)
            : new Rectangle(baseBar.Right + 8, structureY, barWidth, 16);
        DrawOpenGkCompactBarV2(graphics, baseBar, ResolveHealthRatio(baseEntity), teamColor, FormatStructureHpLabel("\u57fa\u5730", baseEntity));
        DrawOpenGkCompactBarV2(graphics, outpostBar, ResolveHealthRatio(outpostEntity), teamColor, FormatStructureHpLabel("\u524d\u54e8", outpostEntity));

        IReadOnlyList<SimulationEntity> units = BuildTeamHudUnits(teamKey);
        if (units.Count == 0)
        {
            return;
        }

        Rectangle[] cards = BuildOpenGkUcUnitCardRects(rect, units.Count);
        for (int i = 0; i < units.Count && i < cards.Length; i++)
        {
            int logicalIndex = mirrored ? units.Count - 1 - i : i;
            DrawOpenGkUcUnitCard(graphics, cards[i], units[logicalIndex], teamColor, mirrored);
        }
    }

    private static Rectangle[] BuildOpenGkUcUnitCardRects(Rectangle rect, int unitCount)
    {
        int count = Math.Max(1, unitCount);
        int unitTop = rect.Y + 56;
        int horizontalPadding = 10;
        int availableWidth = Math.Max(count, rect.Width - horizontalPadding * 2);
        int gap = count > 1 ? Math.Clamp(rect.Width / 70, 4, 8) : 0;
        int cardWidth = Math.Max(20, (availableWidth - gap * (count - 1)) / count);
        int usedWidth = cardWidth * count + gap * (count - 1);
        int startX = rect.X + horizontalPadding + Math.Max(0, (availableWidth - usedWidth) / 2);
        int cardHeight = Math.Max(24, rect.Bottom - unitTop - 8);
        Rectangle[] cards = new Rectangle[count];
        for (int index = 0; index < count; index++)
        {
            cards[index] = new Rectangle(startX + index * (cardWidth + gap), unitTop, cardWidth, cardHeight);
        }

        return cards;
    }

    private void DrawOpenGkCompactBar(Graphics graphics, Rectangle rect, float ratio, Color color, string label)
    {
        using var back = new SolidBrush(Color.FromArgb(172, 0, 0, 0));
        using var fill = new SolidBrush(Color.FromArgb(220, color));
        using var border = new Pen(Color.FromArgb(112, 210, 218, 226), 1f);
        graphics.FillRectangle(back, rect);
        graphics.FillRectangle(fill, rect.X, rect.Y, (int)Math.Round(rect.Width * Math.Clamp(ratio, 0f, 1f)), rect.Height);
        graphics.DrawRectangle(border, rect);
        if (!string.IsNullOrWhiteSpace(label) && rect.Height >= 16 && rect.Width >= 24)
        {
            TextRenderer.DrawText(graphics, label, _tinyHudFont, rect, Color.WhiteSmoke, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawOpenGkUcUnitCard(Graphics graphics, Rectangle card, SimulationEntity unit, Color teamColor, bool mirrored)
    {
        bool selected = string.Equals(_host.SelectedEntity?.Id, unit.Id, StringComparison.OrdinalIgnoreCase);
        using GraphicsPath path = CreateRoundedRectangle(card, 4);
        using var fill = new SolidBrush(unit.IsAlive ? Color.FromArgb(210, 12, 16, 24) : Color.FromArgb(160, 16, 18, 22));
        using var border = new Pen(selected ? Color.FromArgb(236, 242, 210, 82) : Color.FromArgb(unit.IsAlive ? 126 : 82, teamColor), selected ? 1.5f : 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        string entityKey = ExtractEntityKey(unit.Id);
        string label = HudUnitLabelMap.TryGetValue(entityKey, out string? mappedLabel) ? mappedLabel : ResolveRoleLabel(unit);
        TextRenderer.DrawText(graphics, label, _tinyHudFont, new Rectangle(card.X + 4, card.Y + 1, card.Width - 8, 16), unit.IsAlive ? Color.WhiteSmoke : Color.FromArgb(150, 164, 176), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        int metaHeight = card.Height >= 54 ? 16 : 0;
        int hpHeight = 7;
        int hpY = card.Bottom - hpHeight - (metaHeight > 0 ? metaHeight + 3 : 5);
        Rectangle icon = new(
            card.X + 4,
            card.Y + 17,
            Math.Max(12, card.Width - 8),
            Math.Max(8, hpY - card.Y - 19));
        DrawOpenGkUcUnitSilhouetteV2(graphics, icon, unit, teamColor, mirrored, unit.IsAlive);

        Rectangle hp = new(card.X + 5, hpY, Math.Max(8, card.Width - 10), hpHeight);
        DrawOpenGkCompactBar(graphics, hp, ResolveHealthRatio(unit), unit.IsAlive ? Color.FromArgb(78, 255, 132) : Color.FromArgb(112, 118, 126), string.Empty);
        if (metaHeight > 0)
        {
            int ammo = ResolveDisplayedAmmo(unit);
            string meta = unit.IsAlive ? $"{(int)Math.Max(0.0, unit.Health)}  /  {ammo}\u53d1" : $"{unit.RespawnTimerSec:0}s\u590d\u6d3b";
            TextRenderer.DrawText(graphics, meta, _tinyHudFont, new Rectangle(card.X + 5, card.Bottom - metaHeight - 1, card.Width - 10, metaHeight), Color.FromArgb(unit.IsAlive ? 216 : 150, 222, 232, 242), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        _uiButtons.Add(new UiButton(card, $"match_select:{unit.Id}"));
    }
}
