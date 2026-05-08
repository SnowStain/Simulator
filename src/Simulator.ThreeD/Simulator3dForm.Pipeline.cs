using System.Diagnostics;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private enum SimulatorRenderModeKind
    {
        MainMenu,
        Lobby,
        LanRoom,
        Match,
        Referee,
        Preview,
    }

    private readonly record struct SimulatorModeContext(
        SimulatorAppState AppState,
        SimulatorRenderModeKind RenderMode,
        bool IsInMatch,
        bool IsMainMenu,
        bool IsLobby,
        bool IsLanActive,
        bool IsLanHost,
        bool IsLanMatchActive,
        bool IsLanReferee,
        bool IsLanObserver,
        bool IsLanDuelRoom,
        bool IsLocalOnly,
        bool IsDuel,
        bool IsUnitTest,
        bool IsSingleUnitTest,
        bool IsPreview,
        bool IsSharedHost,
        bool IsPaused,
        bool IsMatchStartupActive,
        bool IsFirstPersonHudVisible,
        bool IsObserverMode,
        bool IsTacticalMode,
        bool IsFineTerrainEditor,
        bool ShowDebugSidebars,
        bool SuppressLanBackgroundMapWork,
        bool UseGpuRenderer,
        bool UseFastFlatRenderer);

    private readonly record struct SimulatorFramePacingPlan(
        double TargetFrameIntervalSec,
        bool UseLowLatencyPresent,
        bool AllowWarmGpuTerrain,
        bool AllowSimulationStep,
        bool SuppressActiveMatchWork,
        string Label)
    {
        public double TargetHz => 1.0 / Math.Max(1e-6, TargetFrameIntervalSec);
    }

    private readonly record struct SimulatorRenderPassPlan(
        SimulatorRenderModeKind Mode,
        bool RenderWorld,
        bool RenderTerrain,
        bool RenderFacilities,
        bool RenderEntities,
        bool RenderProjectiles,
        bool RenderSceneOverlay,
        bool RenderUiOverlay,
        bool RenderGpuHudPrimitives,
        bool RenderLanRoomFrozenBackdrop,
        double SceneOverlayUploadIntervalSec,
        double UiOverlayUploadIntervalSec,
        bool OverlayInteractive,
        string Label);

    private SimulatorModeContext ResolveSimulatorModeContext()
    {
        bool lanActive = IsLanMultiplayerActive;
        bool lanReferee = IsLanRefereeClient;
        bool lanObserver = IsLanObserverClient;
        bool lanRoom = _appState == SimulatorAppState.Lobby && lanActive;
        SimulatorRenderModeKind renderMode = _previewOnly
            ? SimulatorRenderModeKind.Preview
            : _appState switch
            {
                SimulatorAppState.MainMenu => SimulatorRenderModeKind.MainMenu,
                SimulatorAppState.Lobby when lanRoom => SimulatorRenderModeKind.LanRoom,
                SimulatorAppState.Lobby => SimulatorRenderModeKind.Lobby,
                SimulatorAppState.InMatch when lanReferee => SimulatorRenderModeKind.Referee,
                SimulatorAppState.InMatch => SimulatorRenderModeKind.Match,
                _ => SimulatorRenderModeKind.Match,
            };

        return new SimulatorModeContext(
            _appState,
            renderMode,
            _appState == SimulatorAppState.InMatch,
            _appState == SimulatorAppState.MainMenu,
            _appState == SimulatorAppState.Lobby,
            lanActive,
            _lanSession?.IsHost == true,
            IsLanMultiplayerMatchActive,
            lanReferee,
            lanObserver,
            IsLanDuelRoomMode,
            !lanActive,
            _host.IsDuelMode,
            _host.IsUnitTestMode,
            _host.IsSingleUnitTestMode,
            _previewOnly,
            _sharedHostSimulation,
            _paused,
            IsMatchStartupActive,
            IsFirstPersonHudVisible(),
            _observerMode,
            _tacticalMode,
            _fineTerrainInMatchEditMode,
            _showDebugSidebars,
            ShouldSuppressLanBackgroundMapWork(),
            UseGpuRenderer,
            UseFastFlatRenderer);
    }

    private SimulatorFramePacingPlan ResolveFramePacingPlan()
    {
        SimulatorModeContext mode = ResolveSimulatorModeContext();
        double interval = mode.IsMainMenu
            ? Math.Max(_targetFrameIntervalSec, MainMenuTargetFrameIntervalSec)
            : _targetFrameIntervalSec;
        if (mode.RenderMode == SimulatorRenderModeKind.LanRoom)
        {
            interval = Math.Max(interval, MainMenuTargetFrameIntervalSec);
        }

        bool activeMatch = mode.IsInMatch
            && !mode.IsPaused
            && !mode.IsPreview
            && !mode.IsSharedHost;
        return new SimulatorFramePacingPlan(
            interval,
            UseLowLatencyPresent: activeMatch && mode.UseGpuRenderer && !mode.UseFastFlatRenderer,
            AllowWarmGpuTerrain: (mode.IsMainMenu || mode.IsLobby || mode.IsMatchStartupActive) && !mode.SuppressLanBackgroundMapWork,
            AllowSimulationStep: mode.IsInMatch && !mode.IsPaused && !mode.IsSharedHost,
            SuppressActiveMatchWork: mode.IsInMatch && mode.IsPaused && !mode.IsMatchStartupActive && !mode.IsPreview && !mode.IsObserverMode && !mode.IsFineTerrainEditor && !mode.IsSharedHost,
            mode.RenderMode.ToString().ToLowerInvariant());
    }

    private SimulatorRenderPassPlan ResolveRenderPassPlan()
    {
        SimulatorModeContext mode = ResolveSimulatorModeContext();
        bool inMatchWorld = mode.RenderMode is SimulatorRenderModeKind.Match or SimulatorRenderModeKind.Referee or SimulatorRenderModeKind.Preview;
        bool menuWorld = mode.RenderMode is SimulatorRenderModeKind.MainMenu or SimulatorRenderModeKind.Lobby;
        bool renderWorld = inMatchWorld || (menuWorld && !mode.SuppressLanBackgroundMapWork);
        bool overlayInteractive = mode.IsTacticalMode
            || mode.ShowDebugSidebars
            || mode.IsMatchStartupActive
            || mode.IsPaused
            || mode.RenderMode == SimulatorRenderModeKind.Referee;

        double sceneInterval = mode.IsFineTerrainEditor
            ? GpuEditorOverlayUploadIntervalSec
            : (mode.IsFirstPersonHudVisible || mode.IsObserverMode || mode.IsPaused
                ? GpuOverlayUploadIntervalSec
                : GpuThirdPersonOverlayUploadIntervalSec);
        double uiInterval = mode.IsFineTerrainEditor
            ? GpuEditorOverlayUploadIntervalSec
            : (mode.IsFirstPersonHudVisible || mode.IsObserverMode || mode.IsPaused
                ? GpuOverlayUploadIntervalSec * 1.3
                : GpuThirdPersonOverlayUploadIntervalSec);
        if (mode.IsInMatch && UseOpenGkMatchHud() && !mode.IsPreview)
        {
            uiInterval = Math.Min(uiInterval, 1.0 / 18.0);
        }

        if (mode.RenderMode == SimulatorRenderModeKind.LanRoom)
        {
            uiInterval = Math.Max(uiInterval, 1.0 / 12.0);
        }

        return new SimulatorRenderPassPlan(
            mode.RenderMode,
            renderWorld,
            renderWorld,
            renderWorld,
            renderWorld,
            inMatchWorld && !mode.IsPreview,
            inMatchWorld,
            true,
            inMatchWorld && !mode.IsPreview,
            mode.RenderMode == SimulatorRenderModeKind.LanRoom && mode.SuppressLanBackgroundMapWork,
            sceneInterval,
            uiInterval,
            overlayInteractive,
            ResolveRenderPlanLabel(mode, renderWorld));
    }

    private static string ResolveRenderPlanLabel(SimulatorModeContext mode, bool renderWorld)
    {
        string network = mode.IsLanActive
            ? mode.IsLanReferee
                ? "lan_referee"
                : mode.IsLanObserver
                    ? "lan_observer"
                    : "lan_player"
            : "local";
        return $"{mode.RenderMode.ToString().ToLowerInvariant()}:{network}:world={(renderWorld ? 1 : 0)}";
    }
}
