using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Simulator.ThreeD;

internal sealed partial class Simulator3dForm
{
    private bool _mainMenuStartExpanded = true;
    private bool _mainMenuEditorExpanded;
    private double _mainMenuStartExpandedVisual = 1.0;
    private double _mainMenuEditorExpandedVisual;
    private double _mainMenuPulseTimeSec;
    private long _mainMenuChromeLastTicks;
    private readonly Dictionary<string, float> _uiHoverMix = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeMainMenuChrome()
    {
        _mainMenuStartExpanded = true;
        _mainMenuEditorExpanded = false;
        _mainMenuStartExpandedVisual = 1.0;
        _mainMenuEditorExpandedVisual = 0.0;
        _mainMenuPulseTimeSec = 0.0;
        _mainMenuChromeLastTicks = _frameClock.ElapsedTicks;
        _uiHoverMix.Clear();
        InitializeBackgroundVideo();
    }

    private void ToggleMainMenuStartSection()
    {
        _mainMenuStartExpanded = !_mainMenuStartExpanded;
        if (_mainMenuStartExpanded)
        {
            _mainMenuEditorExpanded = false;
        }

        Invalidate();
    }

    private void ToggleMainMenuEditorSection()
    {
        _mainMenuEditorExpanded = !_mainMenuEditorExpanded;
        if (_mainMenuEditorExpanded)
        {
            _mainMenuStartExpanded = false;
        }

        Invalidate();
    }

    private void EnterLobbyFromMainMenu(string matchMode)
    {
        DiscardPendingLobbyWorldRebuild();
        if (string.Equals(matchMode, "full", StringComparison.OrdinalIgnoreCase))
        {
            _host.SetMatchModeDeferred(matchMode);
        }
        else
        {
            _host.SetMatchMode(matchMode);
        }

        _mainMenuStartExpanded = false;
        _mainMenuEditorExpanded = false;
        EnterLobby();
    }

    private void UpdateMainMenuChrome()
    {
        long nowTicks = _frameClock.ElapsedTicks;
        if (_mainMenuChromeLastTicks <= 0)
        {
            _mainMenuChromeLastTicks = nowTicks;
        }

        double dt = Math.Clamp((nowTicks - _mainMenuChromeLastTicks) / (double)Stopwatch.Frequency, 0.0, 0.08);
        _mainMenuChromeLastTicks = nowTicks;
        _mainMenuPulseTimeSec += dt;
        _mainMenuStartExpandedVisual = ApproachUiAnimation(_mainMenuStartExpandedVisual, _mainMenuStartExpanded ? 1.0 : 0.0, dt, 10.0);
        _mainMenuEditorExpandedVisual = ApproachUiAnimation(_mainMenuEditorExpandedVisual, _mainMenuEditorExpanded ? 1.0 : 0.0, dt, 10.0);

        string? hoveredAction = !_mouseCaptureActive && ClientRectangle.Contains(_lastMouse)
            ? ResolveUiAction(_lastMouse)
            : null;
        HashSet<string> liveActions = new(StringComparer.OrdinalIgnoreCase);
        foreach (UiButton button in _uiButtons)
        {
            if (string.IsNullOrWhiteSpace(button.Action))
            {
                continue;
            }

            liveActions.Add(button.Action);
            float current = _uiHoverMix.TryGetValue(button.Action, out float value) ? value : 0f;
            float target = string.Equals(button.Action, hoveredAction, StringComparison.OrdinalIgnoreCase) ? 1f : 0f;
            _uiHoverMix[button.Action] = (float)ApproachUiAnimation(current, target, dt, 16.0);
        }

        string[] staleActions = _uiHoverMix.Keys
            .Where(action => !liveActions.Contains(action))
            .ToArray();
        foreach (string action in staleActions)
        {
            float next = (float)ApproachUiAnimation(_uiHoverMix[action], 0.0, dt, 16.0);
            if (next <= 0.01f)
            {
                _uiHoverMix.Remove(action);
            }
            else
            {
                _uiHoverMix[action] = next;
            }
        }
    }

    private bool UseModernMainMenuChrome()
        => true;

    private static double ApproachUiAnimation(double current, double target, double dt, double sharpness)
    {
        if (dt <= 1e-6)
        {
            return current;
        }

        double response = 1.0 - Math.Exp(-Math.Max(0.1, sharpness) * dt);
        return current + (target - current) * response;
    }

    private float ResolveUiHoverMix(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return 0f;
        }

        return _uiHoverMix.TryGetValue(action, out float value) ? value : 0f;
    }
}
