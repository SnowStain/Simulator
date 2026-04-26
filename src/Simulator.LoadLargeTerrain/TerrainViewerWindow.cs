using System.Runtime.InteropServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Simulator.Core.Map;
using Simulator.Editors;
using Keys = OpenTK.Windowing.GraphicsLibraryFramework.Keys;

namespace LoadLargeTerrain;

internal sealed class TerrainViewerWindow : GameWindow
{
    private enum AnchorPlacementSource
    {
        CoordinateOrigin,
        CurrentPivot,
        SelectedComponentCenter,
        CompositeCenter,
    }

    private enum ViewMode
    {
        Free,
        TopDown,
    }

    private enum FacilityDrawMode
    {
        Select,
        Rect,
        Line,
        Polygon,
    }

    private readonly TerrainSceneData _scene;
    private readonly string _modelName;
    private readonly List<GpuChunk> _gpuChunks = new();
    private readonly FreeCamera _camera = new();
    private readonly HashSet<int> _actorComponentIds = new();
    private readonly HashSet<int> _manualActorComponentIds = new();
    private readonly List<CompositeObject> _composites = new();
    private readonly Dictionary<int, ComponentData> _componentsById;
    private readonly Dictionary<int, int> _componentToCompositeId = new();
    private readonly Dictionary<int, List<ComponentRenderRef>> _componentRenderRefs = new();
    private readonly Dictionary<int, List<ComponentDrawBatch>> _compositeDrawBatches = new();
    private readonly Dictionary<int, System.Numerics.Vector4> _componentColorOverrides = new();
    private readonly Dictionary<GizmoAxis, System.Numerics.Vector2> _lastGizmoAxisTips = new();
    private readonly WorldScale _worldScale;
    private readonly MapPresetEditingSession? _mapEditingSession;
    private readonly bool _componentTestMode;
    private ComponentAnnotationImporter.ImportedAnnotationData? _importedAnnotations;

    private ImGuiController? _imgui;
    private ShaderProgram? _shader;
    private int _viewProjLocation;
    private int _modelLocation;
    private int _cameraLocation;
    private int _fogNearLocation;
    private int _fogFarLocation;
    private int _renderModeLocation;
    private int _componentOverrideEnabledLocation;
    private int _componentOverrideColorLocation;

    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();

    private bool _cursorCaptured = true;
    private bool _mouseCaptureEnabled = true;
    private bool _isBoxSelecting;
    private bool _boxSelectionAppend;
    private bool _isSweepSelecting;
    private bool _sweepAppendSelection;
    private bool _loadPopupRequested;
    private bool _savePopupRequested;
    private bool _awaitingAnchorPlacementPoint;
    private bool _topDownDraggingRect;
    private float _movementSpeed;
    private readonly float _minimumMovementSpeed;
    private readonly float _maximumMovementSpeed;
    private float _sweepSelectionRadiusPixels = 12.0f;
    private float _rotationNudgeDegrees = 1.0f;
    private bool _invertRotationDirection;
    private System.Numerics.Vector4 _componentColorDraft = new(0.12f, 0.42f, 1.0f, 1.0f);
    private float _farPlane;
    private ViewMode _viewMode;
    private readonly List<int> _visibleChunkIndices = new();
    private readonly HashSet<int> _visibleChunkLookup = new();
    private readonly List<ComponentDrawBatch> _standaloneActorDrawBatches = new();
    private readonly List<ComponentDrawBatch> _componentColorOverrideDrawBatches = new();
    private readonly List<ComponentDrawBatch> _scratchDrawBatches = new();
    private readonly List<System.Numerics.Vector2> _overlayScreenPoints = new();
    private System.Numerics.Vector2 _topDownCenter;
    private float _topDownHalfHeight;
    private FacilityDrawMode _facilityDrawMode = FacilityDrawMode.Select;
    private string _facilityDraftType = "supply";
    private string _facilityDraftTeam = "neutral";
    private string _facilityDraftBaseId = "facility";
    private float _facilityDraftThickness = 12.0f;
    private float _facilityDraftHeightM;
    private int _selectedFacilityIndex = -1;
    private System.Numerics.Vector2? _facilityRectStartWorld;
    private System.Numerics.Vector2? _facilityRectCurrentWorld;
    private readonly List<System.Numerics.Vector2> _facilityPolygonPoints = new();
    private bool _topDownDraggingSelection;
    private System.Numerics.Vector2? _selectedFacilityDragLastWorld;
    private double _fpsAccumulator;
    private int _fpsFrames;
    private int _lastVisibleChunkCount;
    private readonly HashSet<int> _selectedComponentIds = new();
    private int? _selectedComponentId;
    private int? _selectedCompositeId;
    private int? _focusedCompositeId;
    private int? _selectedInteractionUnitCompositeId;
    private int? _selectedInteractionUnitId;
    private int? _focusedInteractionUnitCompositeId;
    private int? _focusedInteractionUnitId;
    private int _nextCompositeId = 1;
    private int _boxSelectionAddCount;
    private int _sweepSelectionAddCount;
    private string _loadDialogPath;
    private string _currentExportPath;
    private string _saveDialogPath;
    private string _statusMessage = "中心指针已就绪。Shift+左键可框选，Caps+左键按住可连续扫选。";
    private AnchorPlacementSource _pendingAnchorPlacementSource = AnchorPlacementSource.CoordinateOrigin;
    private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
    private Matrix4 _lastViewProjection;
    private System.Numerics.Vector2 _boxSelectionStart;
    private System.Numerics.Vector2 _boxSelectionEnd;
    private System.Numerics.Vector2 _lastFreeMousePosition;
    private System.Numerics.Vector2 _lastGizmoOriginScreen;

    public TerrainViewerWindow(
        GameWindowSettings gameWindowSettings,
        NativeWindowSettings nativeWindowSettings,
        TerrainSceneData scene,
        string modelName,
        string exportPath,
        ComponentAnnotationImporter.ImportedAnnotationData? importedAnnotations,
        MapPresetEditingSession? mapEditingSession,
        bool startInTopDown,
        bool componentTestMode)
        : base(gameWindowSettings, nativeWindowSettings)
    {
        _scene = scene;
        _modelName = modelName;
        _importedAnnotations = importedAnnotations;
        _mapEditingSession = mapEditingSession;
        _componentTestMode = componentTestMode;
        _currentExportPath = exportPath;
        _loadDialogPath = importedAnnotations?.SourcePath ?? exportPath;
        _saveDialogPath = exportPath;
        _worldScale = new WorldScale(scene.Bounds);
        _componentsById = scene.Components.ToDictionary(component => component.Id);
        _movementSpeed = scene.RecommendedMoveSpeed;
        _minimumMovementSpeed = Math.Max(scene.RecommendedMoveSpeed * 0.06f, 0.06f);
        _maximumMovementSpeed = Math.Max(scene.RecommendedMoveSpeed * 20.0f, _minimumMovementSpeed + 1.0f);
        _farPlane = scene.RecommendedFarPlane;
        _viewMode = startInTopDown ? ViewMode.TopDown : ViewMode.Free;
        _topDownCenter = new System.Numerics.Vector2(scene.Bounds.Center.X, scene.Bounds.Center.Z);
        _topDownHalfHeight = Math.Clamp(MathF.Max(scene.Bounds.Size.X / 2.2f, scene.Bounds.Size.Z / 2.2f), 4.0f, 120.0f);
        NormalizeFacilitiesToTopDownWorld();

        var spawn = scene.RecommendedSpawn;
        _camera.Position = new Vector3(spawn.X, spawn.Y, spawn.Z);
        if (_componentTestMode)
        {
            ConfigureMatchLikeCamera();
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        VSync = VSyncMode.Off;
        SetCursorCaptured(true);

        GL.ClearColor(0.62f, 0.75f, 0.92f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        _shader = new ShaderProgram(VertexShaderSource, FragmentShaderSource);
        _viewProjLocation = _shader.GetUniformLocation("uViewProj");
        _modelLocation = _shader.GetUniformLocation("uModel");
        _cameraLocation = _shader.GetUniformLocation("uCameraPosition");
        _fogNearLocation = _shader.GetUniformLocation("uFogNear");
        _fogFarLocation = _shader.GetUniformLocation("uFogFar");
        _renderModeLocation = _shader.GetUniformLocation("uRenderMode");
        _componentOverrideEnabledLocation = _shader.GetUniformLocation("uUseComponentOverrideColor");
        _componentOverrideColorLocation = _shader.GetUniformLocation("uComponentOverrideColor");
        _imgui = new ImGuiController(ClientSize.X, ClientSize.Y, FramebufferSize.X, FramebufferSize.Y);

        Console.WriteLine($"正在上传 {_scene.Chunks.Count} 个合并分块 / {_scene.Components.Count:N0} 个可选组件到 GPU...");
        for (var i = 0; i < _scene.Chunks.Count; i++)
        {
            if (i % 50 == 0 || i == _scene.Chunks.Count - 1)
            {
                Console.WriteLine($"GPU 上传进度 {i + 1}/{_scene.Chunks.Count}...");
            }

            _gpuChunks.Add(GpuChunk.Create(_scene.Chunks[i], i));
        }

        foreach (var chunk in _gpuChunks)
        {
            foreach (var range in chunk.ComponentRanges)
            {
                if (!_componentRenderRefs.TryGetValue(range.ComponentId, out var refs))
                {
                    refs = new List<ComponentRenderRef>(1);
                    _componentRenderRefs.Add(range.ComponentId, refs);
                }

                refs.Add(new ComponentRenderRef(chunk, range));
            }
        }

        ApplyImportedAnnotations();
        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();

        Console.WriteLine("操作：WASD 平移，F 上升，C 下降，Alt 切换鼠标绑定，滚轮调节移动灵敏度。");
        Console.WriteLine("编辑器快捷键：左键选中中心指针目标，Ctrl+左键可多选，Shift+左键可框选，Caps+左键按住可连续扫选，N 新建组合体，Enter 加入当前组合体，Ctrl+Z 撤销，Ctrl+Y 重做，Ctrl+S 保存当前标注，Ctrl+Shift+S 标注另存为。");
        Console.WriteLine($"世界比例尺已确认：X = {WorldScale.RealLengthXMeters}m，Z = {WorldScale.RealLengthZMeters}m。");
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        foreach (var chunk in _gpuChunks)
        {
            chunk.Dispose();
        }

        _shader?.Dispose();
        _imgui?.Dispose();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _imgui?.WindowResized(ClientSize.X, ClientSize.Y, FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        _imgui?.PressChar((char)e.Unicode);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        _imgui?.Update(this, (float)args.Time);

        if (!IsFocused)
        {
            return;
        }

        var keyboard = KeyboardState;
        var mouse = MouseState;
        var mousePosition = ToNumerics(MousePosition);
        var ctrlDown = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);
        var shiftDown = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        var capsDown = keyboard.IsKeyDown(Keys.CapsLock);
        var shouldBoxSelect = shiftDown && mouse.IsButtonDown(MouseButton.Left);
        var sweepSelectingByCaps = capsDown && mouse.IsButtonDown(MouseButton.Left);
        var sightMultiSelectingByCaps = capsDown && mouse.IsButtonDown(MouseButton.Right);
        var shouldSweepSelect = sweepSelectingByCaps;
        var altPressed = keyboard.IsKeyPressed(Keys.LeftAlt) || keyboard.IsKeyPressed(Keys.RightAlt);
        var uiWantsKeyboard = _imgui?.WantCaptureKeyboard == true;
        var uiWantsMouse = _imgui?.WantCaptureMouse == true;
        var hasSavePopup = ImGui.IsPopupOpen("另存为");

        if (keyboard.IsKeyPressed(Keys.Escape))
        {
            CancelCurrentInteraction();
        }

        if (ctrlDown && keyboard.IsKeyPressed(Keys.Z) && !ImGui.IsAnyItemActive())
        {
            Undo();
        }

        if (ctrlDown && keyboard.IsKeyPressed(Keys.Y) && !ImGui.IsAnyItemActive())
        {
            Redo();
        }

        if (ctrlDown && keyboard.IsKeyPressed(Keys.S))
        {
            if (shiftDown)
            {
                OpenSavePopup();
            }
            else if (_componentTestMode)
            {
                SaveCurrentAnnotations();
            }
            else if (_viewMode == ViewMode.TopDown && _mapEditingSession is not null)
            {
                SaveMapDocument();
            }
            else
            {
                SaveCurrentAnnotations();
            }
        }

        if (altPressed && !hasSavePopup)
        {
            ToggleMouseCaptureMode();
        }

        if (!uiWantsKeyboard && !uiWantsMouse && shouldBoxSelect && !_isBoxSelecting && !_awaitingAnchorPlacementPoint)
        {
            StartBoxSelection(mousePosition, ctrlDown);
        }

        if (_isBoxSelecting)
        {
            if (!shouldBoxSelect)
            {
                FinishBoxSelection();
            }
            else
            {
                UpdateBoxSelection(mousePosition);
            }
        }

        if (!uiWantsKeyboard && !uiWantsMouse && shouldSweepSelect && !_isSweepSelecting && !_isBoxSelecting)
        {
            StartSweepSelection(ctrlDown);
        }

        if (_isSweepSelecting && !shouldSweepSelect)
        {
            FinishSweepSelection();
        }

        if (!uiWantsKeyboard && keyboard.IsKeyPressed(Keys.N))
        {
            CreateComposite(includeSelectedComponent: false, recordHistory: true);
        }

        if (!uiWantsKeyboard && keyboard.IsKeyPressed(Keys.Enter))
        {
            AddSelectedComponentToSelectedComposite(recordHistory: true);
        }

        if (!uiWantsKeyboard && keyboard.IsKeyPressed(Keys.F2))
        {
            SetSelectedComponentRole(isActor: true, recordHistory: true);
        }

        if (!uiWantsKeyboard && keyboard.IsKeyPressed(Keys.F1))
        {
            SetSelectedComponentRole(isActor: false, recordHistory: true);
        }

        if (!uiWantsKeyboard && keyboard.IsKeyPressed(Keys.F6))
        {
            ToggleViewMode();
        }

        if (_viewMode == ViewMode.TopDown)
        {
            SetCursorCaptured(false);
            if (!uiWantsKeyboard)
            {
                HandleTopDownHotkeys(keyboard);
                HandleTopDownNavigation(keyboard, (float)args.Time);
            }

            if (!uiWantsMouse)
            {
                HandleTopDownPointer(mousePosition, mouse);
            }

            if (Math.Abs(mouse.ScrollDelta.Y) > float.Epsilon && !uiWantsMouse)
            {
                AdjustTopDownZoom(mouse.ScrollDelta.Y);
            }

            return;
        }

        if (!uiWantsMouse && Math.Abs(mouse.ScrollDelta.Y) > float.Epsilon)
        {
            AdjustMovementSpeed(mouse.ScrollDelta.Y);
        }

        var wantsFreeCursor = shiftDown
            || _isBoxSelecting
            || _awaitingAnchorPlacementPoint
            || !_mouseCaptureEnabled
            || uiWantsMouse
            || uiWantsKeyboard
            || hasSavePopup
            || _activeGizmoAxis != GizmoAxis.None;
        SetCursorCaptured(!wantsFreeCursor);

        if (!_cursorCaptured && _activeGizmoAxis == GizmoAxis.None)
        {
            _lastFreeMousePosition = mousePosition;
        }

        if (_isBoxSelecting)
        {
            return;
        }

        if (_awaitingAnchorPlacementPoint && !_cursorCaptured && mouse.IsButtonPressed(MouseButton.Left) && !uiWantsMouse)
        {
            TryPlaceSelectedCompositeAtPointer(recordHistory: true);
            return;
        }

        if (_activeGizmoAxis != GizmoAxis.None)
        {
            UpdateGizmoDrag(mousePosition);
            return;
        }

        if (!_cursorCaptured && mouse.IsButtonPressed(MouseButton.Left) && !uiWantsMouse)
        {
            if (TryBeginGizmoDrag(mousePosition))
            {
                return;
            }
        }

        if (_cursorCaptured && mouse.IsButtonPressed(MouseButton.Left) && !uiWantsMouse && !capsDown)
        {
            SelectComponentAtPointer(ctrlDown);
        }

        if (_cursorCaptured && sightMultiSelectingByCaps && !uiWantsMouse)
        {
            AddFirstSightHitToSelection(reportDuplicate: false);
        }

        if (_cursorCaptured && mouse.IsButtonPressed(MouseButton.Right) && !uiWantsMouse && !capsDown)
        {
            SelectInteractionUnitAtPointer();
        }

        if (_cursorCaptured && !uiWantsMouse)
        {
            var mouseDelta = mouse.Delta;
            _camera.Rotate(mouseDelta.X, mouseDelta.Y, 0.12f);
        }

        if (uiWantsKeyboard || !_cursorCaptured)
        {
            return;
        }

        var moveDirection = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W))
        {
            moveDirection += _camera.Forward;
        }

        if (keyboard.IsKeyDown(Keys.S))
        {
            moveDirection -= _camera.Forward;
        }

        if (keyboard.IsKeyDown(Keys.D))
        {
            moveDirection += _camera.Right;
        }

        if (keyboard.IsKeyDown(Keys.A))
        {
            moveDirection -= _camera.Right;
        }

        if (keyboard.IsKeyDown(Keys.F))
        {
            moveDirection += Vector3.UnitY;
        }

        if (keyboard.IsKeyDown(Keys.C) && !ctrlDown)
        {
            moveDirection -= Vector3.UnitY;
        }

        if (moveDirection.LengthSquared > 0.0f)
        {
            moveDirection = moveDirection.Normalized();
        }

        var speedMultiplier = _isSweepSelecting ? 1.0f : (shiftDown ? 4.0f : 1.0f);
        _camera.Position += moveDirection * _movementSpeed * speedMultiplier * (float)args.Time;

        if (_isSweepSelecting && !uiWantsMouse)
        {
            UpdateSweepSelection();
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.Viewport(0, 0, Math.Max(FramebufferSize.X, 1), Math.Max(FramebufferSize.Y, 1));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_shader is null)
        {
            SwapBuffers();
            return;
        }

        var (_, _, viewProjection) = BuildViewProjection();
        _lastViewProjection = viewProjection;
        var frustum = Frustum.FromMatrix(viewProjection);

        _shader.Use();
        GL.UniformMatrix4(_viewProjLocation, false, ref viewProjection);
        var identity = Matrix4.Identity;
        GL.UniformMatrix4(_modelLocation, false, ref identity);
        GL.Uniform3(_cameraLocation, _camera.Position);
        GL.Uniform1(_fogNearLocation, _farPlane * 0.25f);
        GL.Uniform1(_fogFarLocation, _farPlane * 0.95f);
        GL.Uniform1(_renderModeLocation, 0);
        GL.Uniform1(_componentOverrideEnabledLocation, 0);

        var visibleCount = 0;
        _visibleChunkIndices.Clear();
        _visibleChunkLookup.Clear();
        for (var chunkIndex = 0; chunkIndex < _gpuChunks.Count; chunkIndex++)
        {
            var chunk = _gpuChunks[chunkIndex];
            if (!frustum.Intersects(chunk.Bounds))
            {
                continue;
            }

            visibleCount++;
            _visibleChunkIndices.Add(chunkIndex);
            _visibleChunkLookup.Add(chunkIndex);
            chunk.DrawStatic();
        }

        DrawComposites(frustum);
        DrawRoleOverlays(_visibleChunkLookup);

        _lastVisibleChunkCount = visibleCount;
        UpdateTitle(args.Time);

        BuildMapEditorUi();
        BuildEditorUi();
        DrawCenterPointer();
        DrawSweepSightOverlay();
        DrawBoxSelectionOverlay();
        DrawMapEditorOverlay();
        DrawCompositeGizmo();
        _imgui?.Render();

        SwapBuffers();
    }

    private void UpdateTitle(double frameTime)
    {
        _fpsAccumulator += frameTime;
        _fpsFrames++;

        if (_fpsAccumulator < 0.5)
        {
            return;
        }

        var fps = _fpsFrames / _fpsAccumulator;
        var selectedComponentText = _selectedComponentIds.Count > 0
            ? $"组件={_selectedComponentIds.Count} 个"
            : _selectedComponentId is int selectedComponentId
                ? $"组件={selectedComponentId}"
                : "组件=无";
        var selectedCompositeText = _selectedCompositeId is int selectedCompositeId
            ? $"组合体={selectedCompositeId}"
            : "组合体=无";
        string modeText = _componentTestMode ? "地图单位测试" : "地图编辑器";
        Title = $"{_modelName} | {modeText} | {fps:F0} FPS | 可见分块={_lastVisibleChunkCount}/{_gpuChunks.Count} | {selectedComponentText} | {selectedCompositeText} | {_statusMessage}";
        _fpsAccumulator = 0.0;
        _fpsFrames = 0;
    }

    private void DrawRoleOverlays(IReadOnlyCollection<int> visibleChunkIndices)
    {
        if (_shader is null)
        {
            return;
        }

        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-2.0f, -2.0f);
        GL.Disable(EnableCap.CullFace);

        var identity = Matrix4.Identity;
        GL.UniformMatrix4(_modelLocation, false, ref identity);

        if (_componentColorOverrideDrawBatches.Count > 0)
        {
            GL.Uniform1(_renderModeLocation, 0);
            DrawBatches(_componentColorOverrideDrawBatches, visibleChunkIndices);
        }

        if (_standaloneActorDrawBatches.Count > 0)
        {
            GL.Uniform1(_renderModeLocation, 2);
            DrawBatches(_standaloneActorDrawBatches, visibleChunkIndices);
        }

        if (_selectedComponentIds.Count > 0)
        {
            GL.Uniform1(_renderModeLocation, 1);
            _scratchDrawBatches.Clear();
            foreach (var selectedComponentId in _selectedComponentIds)
            {
                if (_componentToCompositeId.ContainsKey(selectedComponentId))
                {
                    continue;
                }

                if (_componentRenderRefs.TryGetValue(selectedComponentId, out var refs))
                {
                    foreach (var renderRef in refs)
                    {
                        _scratchDrawBatches.Add(ComponentDrawBatch.From(renderRef));
                    }
                }
            }

            DrawBatches(_scratchDrawBatches, visibleChunkIndices);
        }

        GL.Uniform1(_renderModeLocation, 0);
        GL.Uniform1(_componentOverrideEnabledLocation, 0);
        GL.Enable(EnableCap.CullFace);
        GL.Disable(EnableCap.PolygonOffsetFill);
    }

    private void DrawComposites(Frustum frustum)
    {
        if (_shader is null || _composites.Count == 0)
        {
            return;
        }

        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-1.0f, -1.0f);
        GL.Disable(EnableCap.CullFace);

        foreach (var composite in _composites)
        {
            if (composite.ComponentIds.Count == 0)
            {
                continue;
            }

            var bounds = composite.ComputeBounds(_componentsById);
            if (bounds.IsValid() && !frustum.Intersects(bounds))
            {
                continue;
            }

            var model = ToOpenTk(composite.ModelMatrix);
            var hasSelectedComponentsInComposite = HasSelectedComponents(composite);
            GL.UniformMatrix4(_modelLocation, false, ref model);
            GL.Uniform1(
                _renderModeLocation,
                composite.Id == _selectedCompositeId && !hasSelectedComponentsInComposite
                    ? 3
                    : composite.IsActor ? 2 : 0);

            if (_compositeDrawBatches.TryGetValue(composite.Id, out var drawBatches))
            {
                DrawBatches(drawBatches);
            }

            if (_selectedComponentIds.Count > 0)
            {
                var selectedInteractionUnit = _selectedInteractionUnitCompositeId == composite.Id &&
                                              _selectedInteractionUnitId is int selectedInteractionUnitId
                    ? composite.InteractionUnits.FirstOrDefault(unit => unit.Id == selectedInteractionUnitId)
                    : null;

                GL.PolygonOffset(-3.0f, -3.0f);
                GL.Uniform1(_renderModeLocation, selectedInteractionUnit is null ? 4 : 5);
                foreach (var componentId in composite.ComponentIds)
                {
                    var selectedForThisPass = selectedInteractionUnit is null
                        ? _selectedComponentIds.Contains(componentId)
                        : selectedInteractionUnit.ComponentIds.Contains(componentId);
                    if (!selectedForThisPass ||
                        !_componentRenderRefs.TryGetValue(componentId, out var selectedRefs))
                    {
                        continue;
                    }

                    foreach (var renderRef in selectedRefs)
                    {
                        renderRef.Chunk.DrawRange(renderRef.Range);
                    }
                }

                GL.PolygonOffset(-1.0f, -1.0f);
            }
        }

        var identity = Matrix4.Identity;
        GL.UniformMatrix4(_modelLocation, false, ref identity);
        GL.Uniform1(_renderModeLocation, 0);
        GL.Enable(EnableCap.CullFace);
        GL.Disable(EnableCap.PolygonOffsetFill);
    }

    private void SelectComponentAtPointer(bool multiSelect)
    {
        var bestHit = TryPickComponentAtPointer();

        if (bestHit is null)
        {
            if (!multiSelect)
            {
                ClearSelectedComponents();
                _selectedCompositeId = null;
                _selectedInteractionUnitCompositeId = null;
                _selectedInteractionUnitId = null;
                _statusMessage = "中心指针下没有组件";
            }

            return;
        }

        var componentId = bestHit.Value.ComponentId;
        _selectedCompositeId = bestHit.Value.CompositeId;

        if (ShouldPromotePrimaryPickToInteractionUnit() && !multiSelect &&
            bestHit.Value.CompositeId is int compositeId &&
            _composites.FirstOrDefault(item => item.Id == compositeId) is { } composite &&
            TryGetInteractionUnitByComponent(composite, componentId, out var interactionUnit))
        {
            SelectInteractionUnit(composite, interactionUnit, focusComposite: true, $"已在 3D 视图中选中互动单元 {interactionUnit.Name}");
            Console.WriteLine(_statusMessage);
            return;
        }

        if (multiSelect)
        {
            _selectedInteractionUnitCompositeId = null;
            _selectedInteractionUnitId = null;

            if (_selectedComponentIds.Contains(componentId))
            {
                _selectedComponentIds.Remove(componentId);
                if (_selectedComponentId == componentId)
                {
                    _selectedComponentId = _selectedComponentIds.Count > 0
                        ? _selectedComponentIds.OrderBy(id => id).Last()
                        : null;
                }

                _statusMessage = $"已取消选择组件 {componentId}";
            }
            else
            {
                _selectedComponentIds.Add(componentId);
                _selectedComponentId = componentId;
                _statusMessage = BuildSelectionMessage(bestHit.Value);
            }
        }
        else
        {
            _selectedComponentIds.Clear();
            _selectedComponentIds.Add(componentId);
            _selectedComponentId = componentId;
            _selectedInteractionUnitCompositeId = null;
            _selectedInteractionUnitId = null;
            _statusMessage = BuildSelectionMessage(bestHit.Value);
        }

        Console.WriteLine(_statusMessage);
    }

    private static bool ShouldPromotePrimaryPickToInteractionUnit()
    {
        return false;
    }

    private void SelectInteractionUnitAtPointer()
    {
        var bestHit = TryPickComponentAtPointer();
        if (bestHit is null || bestHit.Value.CompositeId is not int compositeId)
        {
            _statusMessage = "当前准星下没有可选中的互动单元。";
            return;
        }

        var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
        if (composite is null || !TryGetInteractionUnitByComponent(composite, bestHit.Value.ComponentId, out var interactionUnit))
        {
            _statusMessage = "当前组件不属于任何互动单元。";
            return;
        }

        SelectInteractionUnit(composite, interactionUnit, focusComposite: true, $"已在 3D 视图中选中互动单元 {interactionUnit.Name}");
        Console.WriteLine(_statusMessage);
    }

    private void AddFirstSightHitToSelection(bool reportDuplicate)
    {
        var hit = TryPickComponentAtPointer();
        if (hit is null)
        {
            if (reportDuplicate)
            {
                _statusMessage = "Caps+右键：视线第一次交汇处没有组件。";
            }

            return;
        }

        _selectedInteractionUnitCompositeId = null;
        _selectedInteractionUnitId = null;
        _selectedComponentId = hit.Value.ComponentId;
        _selectedCompositeId = hit.Value.CompositeId;

        if (_selectedComponentIds.Add(hit.Value.ComponentId))
        {
            _statusMessage = $"Caps+右键多选：已加入视线第一次交汇组件 {hit.Value.ComponentId}。当前共 {_selectedComponentIds.Count} 个。";
            Console.WriteLine(_statusMessage);
        }
        else
        {
            if (reportDuplicate)
            {
                _statusMessage = $"Caps+右键多选：组件 {hit.Value.ComponentId} 已在选择中。当前共 {_selectedComponentIds.Count} 个。";
                Console.WriteLine(_statusMessage);
            }
        }
    }

    private PickHit? TryPickComponentAtPointer()
    {
        var ray = BuildPointerRay();
        var bestHit = PickComposite(ray);

        foreach (var chunk in _scene.Chunks)
        {
            if (!TryIntersectRayBounds(ray, chunk.Bounds, out _))
            {
                continue;
            }

            foreach (var range in chunk.ComponentRanges)
            {
                if (_componentToCompositeId.ContainsKey(range.ComponentId))
                {
                    continue;
                }

                if (!TryIntersectRayBounds(ray, range.Bounds, out _) ||
                    !TryIntersectComponentTriangles(ray, chunk, range, out var distance))
                {
                    continue;
                }

                if (bestHit is null || distance < bestHit.Value.Distance)
                {
                    bestHit = new PickHit(range.ComponentId, distance, null);
                }
            }
        }

        return bestHit;
    }

    private PickHit? PickComposite(PickRay worldRay)
    {
        PickHit? bestHit = null;

        foreach (var composite in _composites)
        {
            if (composite.ComponentIds.Count == 0)
            {
                continue;
            }

            if (!System.Numerics.Matrix4x4.Invert(composite.ModelMatrix, out var inverse))
            {
                continue;
            }

            var localOrigin = System.Numerics.Vector3.Transform(worldRay.Origin, inverse);
            var localDirection = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(worldRay.Direction, inverse));
            var localRay = new PickRay(localOrigin, localDirection);

            foreach (var componentId in composite.ComponentIds)
            {
                if (!_componentRenderRefs.TryGetValue(componentId, out var refs))
                {
                    continue;
                }

                foreach (var renderRef in refs)
                {
                    if (!TryIntersectRayBounds(localRay, renderRef.Range.Bounds, out _) ||
                        !TryIntersectComponentTriangles(localRay, renderRef.Chunk.SourceChunk, renderRef.Range, out var distance))
                    {
                        continue;
                    }

                    if (bestHit is null || distance < bestHit.Value.Distance)
                    {
                        bestHit = new PickHit(componentId, distance, composite.Id);
                    }
                }
            }
        }

        return bestHit;
    }

    private void SetSelectedComponentRole(bool isActor, bool recordHistory)
    {
        var selectedComponentIds = GetEditableSelectedComponentIds();
        if (selectedComponentIds.Count == 0)
        {
            _statusMessage = "请先选择至少一个组件，再设置动态或静态";
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        var touchedCompositeIds = new HashSet<int>();
        var standaloneCount = 0;
        foreach (var selectedComponentId in selectedComponentIds)
        {
            if (_componentToCompositeId.TryGetValue(selectedComponentId, out var compositeId))
            {
                var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
                if (composite is null)
                {
                    continue;
                }

                composite.IsActor = isActor;
                touchedCompositeIds.Add(compositeId);
                continue;
            }

            if (isActor)
            {
                _manualActorComponentIds.Add(selectedComponentId);
                _actorComponentIds.Add(selectedComponentId);
            }
            else
            {
                _manualActorComponentIds.Remove(selectedComponentId);
                _actorComponentIds.Remove(selectedComponentId);
            }

            standaloneCount++;
        }

        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        _statusMessage = $"已将 {selectedComponentIds.Count} 个选中组件设置为{(isActor ? "动态体" : "静态")}。涉及组合体 {touchedCompositeIds.Count} 个，独立组件 {standaloneCount} 个。";
    }

    private void OpenSavePopup()
    {
        _saveDialogPath = _currentExportPath;
        _savePopupRequested = true;
        _statusMessage = "请输入新的导出文件名";
    }

    private void OpenLoadPopup()
    {
        _loadDialogPath = _importedAnnotations?.SourcePath ?? _currentExportPath;
        _loadPopupRequested = true;
        _statusMessage = "请选择要读取的 JSON 文件";
    }

    private void SaveCurrentAnnotations()
    {
        if (string.IsNullOrWhiteSpace(_currentExportPath))
        {
            OpenSavePopup();
            return;
        }

        ExportComponentRoles(_currentExportPath);
    }

    private void ExportComponentRoles(string? targetPath = null)
    {
        try
        {
            var resolvedPath = ResolveExportPath(targetPath ?? _currentExportPath);
            ComponentAnnotationExporter.Export(resolvedPath, _modelName, _scene.Components, _actorComponentIds, _composites, _worldScale, _componentColorOverrides);
            _currentExportPath = resolvedPath;
            _saveDialogPath = resolvedPath;
            _statusMessage = $"已保存到 {Path.GetFileName(resolvedPath)}";
            Console.WriteLine($"已导出组件角色文件：{resolvedPath}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"保存失败：{ex.Message}";
            Console.WriteLine(_statusMessage);
        }
    }

    private void LoadAnnotationsFromPath(string? targetPath, bool recordHistory)
    {
        try
        {
            var resolvedPath = ResolveImportPath(targetPath ?? _loadDialogPath);
            var importedAnnotations = ComponentAnnotationImporter.TryLoad(resolvedPath, _worldScale);
            if (importedAnnotations is null)
            {
                _statusMessage = $"无法读取 JSON：{Path.GetFileName(resolvedPath)}";
                return;
            }

            if (recordHistory)
            {
                PushUndoSnapshot();
            }

            _importedAnnotations = importedAnnotations;
            _loadDialogPath = importedAnnotations.SourcePath;
            _currentExportPath = importedAnnotations.SourcePath;
            _saveDialogPath = importedAnnotations.SourcePath;
            ApplyImportedAnnotations();
            RebuildStaticIndexBuffers();
            Console.WriteLine($"已读取 JSON：{importedAnnotations.SourcePath}");
        }
        catch (Exception ex)
        {
            _statusMessage = $"读取 JSON 失败：{ex.Message}";
            Console.WriteLine(_statusMessage);
        }
    }

    private string ResolveExportPath(string candidatePath)
    {
        var path = string.IsNullOrWhiteSpace(candidatePath) ? _currentExportPath : candidatePath.Trim();
        if (!Path.IsPathRooted(path))
        {
            var baseDirectory = Path.GetDirectoryName(_currentExportPath) ?? Directory.GetCurrentDirectory();
            path = Path.Combine(baseDirectory, path);
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
        {
            path = Path.ChangeExtension(path, ".json");
        }

        return Path.GetFullPath(path);
    }

    private string ResolveImportPath(string candidatePath)
    {
        var path = string.IsNullOrWhiteSpace(candidatePath) ? _currentExportPath : candidatePath.Trim();
        if (!Path.IsPathRooted(path))
        {
            var baseDirectory = Path.GetDirectoryName(_currentExportPath) ?? Directory.GetCurrentDirectory();
            path = Path.Combine(baseDirectory, path);
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(path)))
        {
            path = Path.ChangeExtension(path, ".json");
        }

        return Path.GetFullPath(path);
    }

    private void CreateComposite(bool includeSelectedComponent, bool recordHistory)
    {
        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        var compositeId = _nextCompositeId++;
        var composite = new CompositeObject
        {
            Id = compositeId,
            Name = $"组合体 {compositeId}",
            PivotModel = _scene.Bounds.Center,
            PositionModel = _scene.Bounds.Center,
        };

        _composites.Add(composite);
        _selectedCompositeId = composite.Id;
        _focusedCompositeId = composite.Id;
        _selectedInteractionUnitCompositeId = null;
        _selectedInteractionUnitId = null;
        _statusMessage = $"已创建 {composite.Name}";

        if (includeSelectedComponent && GetEditableSelectedComponentIds().Count > 0)
        {
            AddSelectedComponentToSelectedComposite(recordHistory: false);
        }
    }

    private void AddSelectedComponentToSelectedComposite(bool recordHistory)
    {
        var selectedComponentIds = GetEditableSelectedComponentIds();
        if (selectedComponentIds.Count == 0)
        {
            _statusMessage = "请先选中至少一个组件，再按 Enter 加入组合体";
            return;
        }

        var composite = GetActiveComposite();
        if (composite is null)
        {
            CreateComposite(includeSelectedComponent: false, recordHistory: recordHistory);
            composite = GetActiveComposite();
            recordHistory = false;
        }

        if (composite is null)
        {
            return;
        }

        var movableComponents = selectedComponentIds
            .Where(componentId => _componentsById.ContainsKey(componentId))
            .ToList();
        if (movableComponents.Count == 0)
        {
            return;
        }

        var changedCount = 0;
        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        foreach (var componentId in movableComponents)
        {
            if (_componentToCompositeId.TryGetValue(componentId, out var oldCompositeId))
            {
                if (oldCompositeId == composite.Id)
                {
                    continue;
                }

                var oldComposite = _composites.FirstOrDefault(item => item.Id == oldCompositeId);
                if (oldComposite is not null)
                {
                    oldComposite.ComponentIds.Remove(componentId);
                    RemoveComponentFromInteractionUnits(oldComposite, componentId);
                }
            }

            var component = _componentsById[componentId];
            if (composite.ComponentIds.Count == 0)
            {
                composite.PivotModel = component.Bounds.Center;
                composite.PositionModel = component.Bounds.Center;
            }

            composite.ComponentIds.Add(componentId);
            _componentToCompositeId[componentId] = composite.Id;
            changedCount++;
        }

        if (changedCount == 0)
        {
            _statusMessage = "选中的组件已经全部属于当前组合体";
            return;
        }

        composite.IsActor = true;
        _selectedCompositeId = composite.Id;
        _focusedCompositeId = composite.Id;
        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
        _statusMessage = $"已将 {changedCount} 个组件加入 {composite.Name}";
    }

    private void RemoveSelectedComponentFromComposite(bool recordHistory)
    {
        var selectedComponentIds = GetEditableSelectedComponentIds();
        if (selectedComponentIds.Count == 0)
        {
            _statusMessage = "请先选中至少一个组件";
            return;
        }

        var removableComponentIds = selectedComponentIds
            .Where(componentId => _componentToCompositeId.ContainsKey(componentId))
            .ToList();
        if (removableComponentIds.Count == 0)
        {
            _statusMessage = "当前选中组件不属于任何组合体";
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        var touchedCompositeIds = new HashSet<int>();
        foreach (var componentId in removableComponentIds)
        {
            var compositeId = _componentToCompositeId[componentId];
            var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
            if (composite is not null)
            {
                composite.ComponentIds.Remove(componentId);
                RemoveComponentFromInteractionUnits(composite, componentId);
            }

            _componentToCompositeId.Remove(componentId);
            touchedCompositeIds.Add(compositeId);
        }

        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
        _statusMessage = $"已将 {removableComponentIds.Count} 个组件从 {touchedCompositeIds.Count} 个组合体中移除";
    }

    private void DeleteSelectedComposite(bool recordHistory)
    {
        var composite = GetSelectedComposite();
        if (composite is null)
        {
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        foreach (var componentId in composite.ComponentIds)
        {
            _componentToCompositeId.Remove(componentId);
        }

        _composites.Remove(composite);
        if (_focusedCompositeId == composite.Id)
        {
            _focusedCompositeId = null;
            _focusedInteractionUnitCompositeId = null;
            _focusedInteractionUnitId = null;
        }

        if (_selectedInteractionUnitCompositeId == composite.Id)
        {
            _selectedInteractionUnitCompositeId = null;
            _selectedInteractionUnitId = null;
        }

        if (_focusedInteractionUnitCompositeId == composite.Id)
        {
            _focusedInteractionUnitCompositeId = null;
            _focusedInteractionUnitId = null;
        }

        _selectedCompositeId = _composites.LastOrDefault()?.Id;
        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
        _statusMessage = $"已删除 {composite.Name}";
    }

    private CompositeObject? GetSelectedComposite()
    {
        return _selectedCompositeId is int selectedCompositeId
            ? _composites.FirstOrDefault(composite => composite.Id == selectedCompositeId)
            : null;
    }

    private CompositeObject? GetGizmoComposite()
    {
        var selectedComposite = GetSelectedComposite();
        if (selectedComposite is not null)
        {
            return selectedComposite;
        }

        if (_selectedInteractionUnitCompositeId is int interactionCompositeId)
        {
            var interactionComposite = _composites.FirstOrDefault(composite => composite.Id == interactionCompositeId);
            if (interactionComposite is not null)
            {
                return interactionComposite;
            }
        }

        if (_selectedComponentId is int selectedComponentId &&
            _componentToCompositeId.TryGetValue(selectedComponentId, out var selectedComponentCompositeId))
        {
            return _composites.FirstOrDefault(composite => composite.Id == selectedComponentCompositeId);
        }

        var touchedCompositeIds = _selectedComponentIds
            .Select(componentId => _componentToCompositeId.TryGetValue(componentId, out var compositeId) ? compositeId : (int?)null)
            .Where(compositeId => compositeId.HasValue)
            .Select(compositeId => compositeId!.Value)
            .Distinct()
            .ToList();
        if (touchedCompositeIds.Count == 1)
        {
            return _composites.FirstOrDefault(composite => composite.Id == touchedCompositeIds[0]);
        }

        if (_focusedCompositeId is int focusedCompositeId)
        {
            return _composites.FirstOrDefault(composite => composite.Id == focusedCompositeId);
        }

        return null;
    }

    private CompositeObject? GetActiveComposite()
    {
        if (_focusedCompositeId is int focusedCompositeId)
        {
            return _composites.FirstOrDefault(composite => composite.Id == focusedCompositeId);
        }

        return GetSelectedComposite();
    }

    private InteractionUnitObject? GetSelectedInteractionUnit()
    {
        if (_selectedInteractionUnitCompositeId is not int compositeId || _selectedInteractionUnitId is not int interactionUnitId)
        {
            return null;
        }

        var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
        return composite?.InteractionUnits.FirstOrDefault(unit => unit.Id == interactionUnitId);
    }

    private static bool TryGetInteractionUnitByComponent(
        CompositeObject composite,
        int componentId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InteractionUnitObject? interactionUnit)
    {
        interactionUnit = composite.InteractionUnits.FirstOrDefault(unit => unit.ComponentIds.Contains(componentId));
        return interactionUnit is not null;
    }

    private void SelectInteractionUnit(
        CompositeObject composite,
        InteractionUnitObject interactionUnit,
        bool focusComposite,
        string? statusMessage = null)
    {
        if (focusComposite)
        {
            _focusedCompositeId = composite.Id;
        }

        _selectedInteractionUnitCompositeId = composite.Id;
        _selectedInteractionUnitId = interactionUnit.Id;
        _selectedCompositeId = composite.Id;
        _selectedComponentIds.Clear();
        foreach (var componentId in interactionUnit.ComponentIds.OrderBy(componentId => componentId))
        {
            _selectedComponentIds.Add(componentId);
        }

        _selectedComponentId = interactionUnit.ComponentIds.Count > 0
            ? interactionUnit.ComponentIds.OrderBy(componentId => componentId).Last()
            : null;

        _statusMessage = statusMessage ?? $"已选中互动单元 {interactionUnit.Name}";
    }

    private void CreateInteractionUnitFromSelection(bool recordHistory)
    {
        var composite = GetActiveComposite();
        if (composite is null)
        {
            _statusMessage = "请先选中一个组合体，再创建互动单元。";
            return;
        }

        var componentIds = GetEditableSelectedComponentIds()
            .Where(componentId => _componentToCompositeId.TryGetValue(componentId, out var compositeId) && compositeId == composite.Id)
            .Distinct()
            .Order()
            .ToList();

        if (componentIds.Count == 0)
        {
            _statusMessage = "请先在当前组合体中选中一个或多个组件。";
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        foreach (var interactionUnit in composite.InteractionUnits)
        {
            interactionUnit.ComponentIds.RemoveWhere(componentIds.Contains);
        }

        composite.InteractionUnits.RemoveAll(unit => unit.ComponentIds.Count == 0);

        var interactionUnitId = composite.NextInteractionUnitId++;
        var newInteractionUnit = new InteractionUnitObject
        {
            Id = interactionUnitId,
            Name = $"互动单元 {interactionUnitId}",
        };

        foreach (var componentId in componentIds)
        {
            newInteractionUnit.ComponentIds.Add(componentId);
        }

        composite.InteractionUnits.Add(newInteractionUnit);
        _selectedInteractionUnitCompositeId = composite.Id;
        _selectedInteractionUnitId = newInteractionUnit.Id;
        _focusedInteractionUnitCompositeId = composite.Id;
        _focusedInteractionUnitId = newInteractionUnit.Id;
        _selectedCompositeId = composite.Id;
        _statusMessage = $"已在 {composite.Name} 下创建 {newInteractionUnit.Name}。";
    }

    private void DeleteSelectedInteractionUnit(bool recordHistory)
    {
        if (_selectedInteractionUnitCompositeId is not int compositeId || _selectedInteractionUnitId is not int interactionUnitId)
        {
            _statusMessage = "请先选中一个互动单元。";
            return;
        }

        var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
        var interactionUnit = composite?.InteractionUnits.FirstOrDefault(unit => unit.Id == interactionUnitId);
        if (composite is null || interactionUnit is null)
        {
            _statusMessage = "当前互动单元不存在。";
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        composite.InteractionUnits.Remove(interactionUnit);
        _selectedInteractionUnitCompositeId = null;
        _selectedInteractionUnitId = null;
        if (_focusedInteractionUnitCompositeId == composite.Id && _focusedInteractionUnitId == interactionUnit.Id)
        {
            _focusedInteractionUnitCompositeId = null;
            _focusedInteractionUnitId = null;
        }

        _statusMessage = $"已删除互动单元 {interactionUnit.Name}。";
    }

    private void RemoveComponentFromInteractionUnits(CompositeObject composite, int componentId)
    {
        foreach (var interactionUnit in composite.InteractionUnits)
        {
            interactionUnit.ComponentIds.Remove(componentId);
        }

        composite.InteractionUnits.RemoveAll(unit => unit.ComponentIds.Count == 0);

        if (_selectedInteractionUnitCompositeId == composite.Id &&
            _selectedInteractionUnitId is int selectedInteractionUnitId &&
            composite.InteractionUnits.All(unit => unit.Id != selectedInteractionUnitId))
        {
            _selectedInteractionUnitCompositeId = null;
            _selectedInteractionUnitId = null;
        }

        if (_focusedInteractionUnitCompositeId == composite.Id &&
            _focusedInteractionUnitId is int focusedInteractionUnitId &&
            composite.InteractionUnits.All(unit => unit.Id != focusedInteractionUnitId))
        {
            _focusedInteractionUnitCompositeId = null;
            _focusedInteractionUnitId = null;
        }
    }

    private List<int> GetEditableSelectedComponentIds()
    {
        if (_selectedComponentIds.Count > 0)
        {
            return _selectedComponentIds.OrderBy(componentId => componentId).ToList();
        }

        return _selectedComponentId is int selectedComponentId
            ? new List<int> { selectedComponentId }
            : new List<int>();
    }

    private void ClearSelectedComponents()
    {
        _selectedComponentIds.Clear();
        _selectedComponentId = null;
    }

    private string BuildSelectionMessage(PickHit hit)
    {
        if (hit.CompositeId is int compositeId)
        {
            var composite = _composites.FirstOrDefault(item => item.Id == compositeId);
            return $"已选择组合体 {compositeId} 中的组件 {hit.ComponentId}（组合体共 {composite?.ComponentIds.Count ?? 0} 个组件）";
        }

        var role = _actorComponentIds.Contains(hit.ComponentId) ? "动态体" : "静态";
        return $"已选择组件 {hit.ComponentId}（{role}）";
    }

    private void ToggleMouseCaptureMode()
    {
        _mouseCaptureEnabled = !_mouseCaptureEnabled;
        _statusMessage = _mouseCaptureEnabled
            ? "鼠标已绑定到视角控制。"
            : "鼠标已释放，可进行框选、扫选和面板操作。";
    }

    private void CancelCurrentInteraction()
    {
        if (_awaitingAnchorPlacementPoint)
        {
            _awaitingAnchorPlacementPoint = false;
            _statusMessage = "已取消空间点吸附。";
            return;
        }

        if (_topDownDraggingRect)
        {
            _topDownDraggingRect = false;
            _facilityRectStartWorld = null;
            _facilityRectCurrentWorld = null;
            _statusMessage = _facilityDrawMode == FacilityDrawMode.Line
                ? "已取消线段设施绘制。"
                : "已取消矩形设施绘制。";
            return;
        }

        if (_facilityPolygonPoints.Count > 0)
        {
            _facilityPolygonPoints.Clear();
            _statusMessage = "已取消多边形设施绘制。";
            return;
        }

        if (_isBoxSelecting)
        {
            _isBoxSelecting = false;
            _statusMessage = "已取消框选。";
            return;
        }

        if (_isSweepSelecting)
        {
            _isSweepSelecting = false;
            _statusMessage = "已取消连续扫选。";
            return;
        }

        if (_activeGizmoAxis != GizmoAxis.None)
        {
            _activeGizmoAxis = GizmoAxis.None;
            _statusMessage = "已取消三轴拖动。";
            return;
        }

        _statusMessage = "Esc 不再退出程序。";
    }

    private void StartBoxSelection(System.Numerics.Vector2 startPosition, bool appendSelection)
    {
        _isBoxSelecting = true;
        _boxSelectionAppend = appendSelection;
        _boxSelectionAddCount = 0;
        _boxSelectionStart = startPosition;
        _boxSelectionEnd = startPosition;

        if (!appendSelection)
        {
            ClearSelectedComponents();
            _selectedCompositeId = null;
        }

        _statusMessage = appendSelection ? "开始追加框选。" : "开始框选。";
    }

    private void UpdateBoxSelection(System.Numerics.Vector2 currentMousePosition)
    {
        _boxSelectionEnd = currentMousePosition;
    }

    private void FinishBoxSelection()
    {
        _isBoxSelecting = false;
        _boxSelectionAddCount = SelectComponentsInRectangle(_boxSelectionStart, _boxSelectionEnd, _boxSelectionAppend);
        _statusMessage = _boxSelectionAddCount > 0
            ? $"框选完成，本次新增 {_boxSelectionAddCount} 个组件，当前共选中 {_selectedComponentIds.Count} 个。"
            : "框选结束，未命中新组件。";
    }

    private void AdjustMovementSpeed(float wheelDelta)
    {
        var scaledSpeed = _movementSpeed * MathF.Pow(1.2f, wheelDelta);
        _movementSpeed = Math.Clamp(scaledSpeed, _minimumMovementSpeed, _maximumMovementSpeed);
        _statusMessage = $"移动灵敏度已调整为 {_movementSpeed:F2}";
    }

    private void StartSweepSelection(bool appendSelection)
    {
        _isSweepSelecting = true;
        _sweepAppendSelection = appendSelection;
        _sweepSelectionAddCount = 0;

        if (!appendSelection)
        {
            ClearSelectedComponents();
            _selectedCompositeId = null;
        }

        _statusMessage = appendSelection ? "开始追加连续扫选。" : "开始连续扫选。";
    }

    private void UpdateSweepSelection()
    {
        var addedCount = SelectComponentsInViewRadius(_sweepSelectionRadiusPixels);
        if (addedCount <= 0)
        {
            return;
        }

        _sweepSelectionAddCount += addedCount;
        _statusMessage = $"连续扫选中：本帧新增 {addedCount} 个组件，当前共 {_selectedComponentIds.Count} 个，视线半径 {_sweepSelectionRadiusPixels:F0}px。";
    }

    private int SelectComponentsInRectangle(System.Numerics.Vector2 start, System.Numerics.Vector2 end, bool appendSelection)
    {
        var selectionMin = System.Numerics.Vector2.Min(start, end);
        var selectionMax = System.Numerics.Vector2.Max(start, end);
        var compositeLookup = _composites.ToDictionary(composite => composite.Id);
        var addedCount = 0;
        var selectionFrustum = BuildSelectionFrustum(selectionMin, selectionMax);

        if (!appendSelection)
        {
            ClearSelectedComponents();
        }

        if (selectionFrustum is null)
        {
            _selectedCompositeId = null;
            return 0;
        }

        foreach (var component in _scene.Components)
        {
            var worldBounds = component.Bounds;
            if (_componentToCompositeId.TryGetValue(component.Id, out var compositeId) &&
                compositeLookup.TryGetValue(compositeId, out var composite))
            {
                worldBounds = BoundingBox.Transform(worldBounds, composite.ModelMatrix);
            }

            if (!selectionFrustum.Value.Intersects(worldBounds))
            {
                continue;
            }

            if (_selectedComponentIds.Add(component.Id))
            {
                addedCount++;
            }
        }

        _selectedComponentId = _selectedComponentIds.Count > 0
            ? _selectedComponentIds.OrderBy(componentId => componentId).Last()
            : null;

        var touchedCompositeIds = _selectedComponentIds
            .Select(componentId => _componentToCompositeId.TryGetValue(componentId, out var compositeId) ? compositeId : (int?)null)
            .Where(compositeId => compositeId.HasValue)
            .Select(compositeId => compositeId!.Value)
            .Distinct()
            .ToList();

        _selectedCompositeId = touchedCompositeIds.Count == 1 ? touchedCompositeIds[0] : null;
        return addedCount;
    }

    private int SelectComponentsInViewRadius(float radiusPixels)
    {
        var center = new System.Numerics.Vector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f);
        var radius = Math.Clamp(radiusPixels, 1.0f, 120.0f);
        var selectionMin = center - new System.Numerics.Vector2(radius, radius);
        var selectionMax = center + new System.Numerics.Vector2(radius, radius);
        var selectionFrustum = BuildSelectionFrustum(selectionMin, selectionMax);
        if (selectionFrustum is null)
        {
            return 0;
        }

        var viewProjection = _lastViewProjection;
        var radiusSquared = radius * radius;
        var compositeLookup = _composites.ToDictionary(composite => composite.Id);
        var addedCount = 0;

        foreach (var component in _scene.Components)
        {
            System.Numerics.Matrix4x4? modelMatrix = null;
            var worldBounds = component.Bounds;
            if (_componentToCompositeId.TryGetValue(component.Id, out var compositeId) &&
                compositeLookup.TryGetValue(compositeId, out var composite))
            {
                modelMatrix = composite.ModelMatrix;
                worldBounds = BoundingBox.Transform(worldBounds, composite.ModelMatrix);
            }

            if (!selectionFrustum.Value.Intersects(worldBounds) ||
                !TryComponentGeometryOverlapsScreenCircle(component.Id, modelMatrix, viewProjection, center, radiusSquared))
            {
                continue;
            }

            if (_selectedComponentIds.Add(component.Id))
            {
                addedCount++;
                _selectedComponentId = component.Id;
            }
        }

        if (addedCount <= 0)
        {
            return 0;
        }

        var touchedCompositeIds = _selectedComponentIds
            .Select(componentId => _componentToCompositeId.TryGetValue(componentId, out var compositeId) ? compositeId : (int?)null)
            .Where(compositeId => compositeId.HasValue)
            .Select(compositeId => compositeId!.Value)
            .Distinct()
            .ToList();

        _selectedCompositeId = touchedCompositeIds.Count == 1 ? touchedCompositeIds[0] : null;
        return addedCount;
    }

    private Frustum? BuildSelectionFrustum(System.Numerics.Vector2 selectionMin, System.Numerics.Vector2 selectionMax)
    {
        var topLeft = BuildRayFromScreenPosition(new Vector2(selectionMin.X, selectionMin.Y));
        var topRight = BuildRayFromScreenPosition(new Vector2(selectionMax.X, selectionMin.Y));
        var bottomLeft = BuildRayFromScreenPosition(new Vector2(selectionMin.X, selectionMax.Y));
        var bottomRight = BuildRayFromScreenPosition(new Vector2(selectionMax.X, selectionMax.Y));

        return Frustum.FromSelectionBox(
            topLeft.Origin,
            ToNumerics(_camera.Forward),
            topLeft.Direction,
            topRight.Direction,
            bottomLeft.Direction,
            bottomRight.Direction,
            _farPlane);
    }

    private bool TryComponentGeometryOverlapsScreenCircle(
        int componentId,
        System.Numerics.Matrix4x4? modelMatrix,
        Matrix4 viewProjection,
        System.Numerics.Vector2 circleCenter,
        float radiusSquared)
    {
        if (!_componentRenderRefs.TryGetValue(componentId, out var refs))
        {
            return false;
        }

        foreach (var renderRef in refs)
        {
            var chunk = renderRef.Chunk.SourceChunk;
            var end = renderRef.Range.StartIndex + renderRef.Range.IndexCount;
            for (var i = renderRef.Range.StartIndex; i + 2 < end; i += 3)
            {
                var v0 = chunk.Vertices[(int)chunk.Indices[i]].Position;
                var v1 = chunk.Vertices[(int)chunk.Indices[i + 1]].Position;
                var v2 = chunk.Vertices[(int)chunk.Indices[i + 2]].Position;

                if (modelMatrix is System.Numerics.Matrix4x4 transform)
                {
                    v0 = System.Numerics.Vector3.Transform(v0, transform);
                    v1 = System.Numerics.Vector3.Transform(v1, transform);
                    v2 = System.Numerics.Vector3.Transform(v2, transform);
                }

                if (!TryProjectToScreen(v0, viewProjection, out var s0) ||
                    !TryProjectToScreen(v1, viewProjection, out var s1) ||
                    !TryProjectToScreen(v2, viewProjection, out var s2))
                {
                    continue;
                }

                if (DoesTriangleOverlapCircle(s0, s1, s2, circleCenter, radiusSquared))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void FinishSweepSelection()
    {
        _isSweepSelecting = false;
        _statusMessage = _sweepSelectionAddCount > 0
            ? $"连续扫选完成，本次新增 {_sweepSelectionAddCount} 个组件，当前共选中 {_selectedComponentIds.Count} 个。"
            : "连续扫选结束，未命中新组件。";
    }

    private bool TryProjectPointToScreen(
        System.Numerics.Vector3 worldPoint,
        Matrix4 viewProjection,
        System.Numerics.Matrix4x4? modelMatrix,
        out System.Numerics.Vector2 screenPoint)
    {
        if (modelMatrix is System.Numerics.Matrix4x4 transform)
        {
            worldPoint = System.Numerics.Vector3.Transform(worldPoint, transform);
        }

        return TryProjectToScreen(worldPoint, viewProjection, out screenPoint);
    }

    private bool TryProjectBoundsToScreen(
        BoundingBox bounds,
        Matrix4 viewProjection,
        System.Numerics.Matrix4x4? modelMatrix,
        out System.Numerics.Vector2 screenMin,
        out System.Numerics.Vector2 screenMax)
    {
        var min = new System.Numerics.Vector2(float.MaxValue, float.MaxValue);
        var max = new System.Numerics.Vector2(float.MinValue, float.MinValue);
        var hasProjection = false;

        IncludeProjectedCorner(bounds.Min.X, bounds.Min.Y, bounds.Min.Z);
        IncludeProjectedCorner(bounds.Max.X, bounds.Min.Y, bounds.Min.Z);
        IncludeProjectedCorner(bounds.Min.X, bounds.Max.Y, bounds.Min.Z);
        IncludeProjectedCorner(bounds.Max.X, bounds.Max.Y, bounds.Min.Z);
        IncludeProjectedCorner(bounds.Min.X, bounds.Min.Y, bounds.Max.Z);
        IncludeProjectedCorner(bounds.Max.X, bounds.Min.Y, bounds.Max.Z);
        IncludeProjectedCorner(bounds.Min.X, bounds.Max.Y, bounds.Max.Z);
        IncludeProjectedCorner(bounds.Max.X, bounds.Max.Y, bounds.Max.Z);

        screenMin = min;
        screenMax = max;
        return hasProjection;

        void IncludeProjectedCorner(float x, float y, float z)
        {
            var worldPoint = new System.Numerics.Vector3(x, y, z);
            if (modelMatrix is System.Numerics.Matrix4x4 transform)
            {
                worldPoint = System.Numerics.Vector3.Transform(worldPoint, transform);
            }

            if (!TryProjectToScreen(worldPoint, viewProjection, out var screenPoint))
            {
                return;
            }

            hasProjection = true;
            min = System.Numerics.Vector2.Min(min, screenPoint);
            max = System.Numerics.Vector2.Max(max, screenPoint);
        }
    }

    private static bool IsRectFullyInside(
        System.Numerics.Vector2 outerMin,
        System.Numerics.Vector2 outerMax,
        System.Numerics.Vector2 innerMin,
        System.Numerics.Vector2 innerMax)
    {
        return innerMin.X >= outerMin.X &&
               innerMax.X <= outerMax.X &&
               innerMin.Y >= outerMin.Y &&
               innerMax.Y <= outerMax.Y;
    }

    private static bool IsPointInsideRect(
        System.Numerics.Vector2 rectMin,
        System.Numerics.Vector2 rectMax,
        System.Numerics.Vector2 point)
    {
        return point.X >= rectMin.X &&
               point.X <= rectMax.X &&
               point.Y >= rectMin.Y &&
               point.Y <= rectMax.Y;
    }

    private static bool DoRectsOverlap(
        System.Numerics.Vector2 aMin,
        System.Numerics.Vector2 aMax,
        System.Numerics.Vector2 bMin,
        System.Numerics.Vector2 bMax)
    {
        return aMin.X <= bMax.X &&
               aMax.X >= bMin.X &&
               aMin.Y <= bMax.Y &&
               aMax.Y >= bMin.Y;
    }

    private static bool DoesTriangleOverlapCircle(
        System.Numerics.Vector2 a,
        System.Numerics.Vector2 b,
        System.Numerics.Vector2 c,
        System.Numerics.Vector2 circleCenter,
        float radiusSquared)
    {
        return (a - circleCenter).LengthSquared() <= radiusSquared ||
               (b - circleCenter).LengthSquared() <= radiusSquared ||
               (c - circleCenter).LengthSquared() <= radiusSquared ||
               DistancePointToSegmentSquared(circleCenter, a, b) <= radiusSquared ||
               DistancePointToSegmentSquared(circleCenter, b, c) <= radiusSquared ||
               DistancePointToSegmentSquared(circleCenter, c, a) <= radiusSquared ||
               IsPointInsideTriangle(circleCenter, a, b, c);
    }

    private static float DistancePointToSegmentSquared(
        System.Numerics.Vector2 point,
        System.Numerics.Vector2 start,
        System.Numerics.Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
        {
            return (point - start).LengthSquared();
        }

        var t = Math.Clamp(System.Numerics.Vector2.Dot(point - start, segment) / lengthSquared, 0.0f, 1.0f);
        var projection = start + (segment * t);
        return (point - projection).LengthSquared();
    }

    private static bool IsPointInsideTriangle(
        System.Numerics.Vector2 point,
        System.Numerics.Vector2 a,
        System.Numerics.Vector2 b,
        System.Numerics.Vector2 c)
    {
        var d1 = SignedTriangleArea(point, a, b);
        var d2 = SignedTriangleArea(point, b, c);
        var d3 = SignedTriangleArea(point, c, a);
        var hasNegative = d1 < 0.0f || d2 < 0.0f || d3 < 0.0f;
        var hasPositive = d1 > 0.0f || d2 > 0.0f || d3 > 0.0f;
        return !(hasNegative && hasPositive);
    }

    private static float SignedTriangleArea(
        System.Numerics.Vector2 p1,
        System.Numerics.Vector2 p2,
        System.Numerics.Vector2 p3)
    {
        return ((p1.X - p3.X) * (p2.Y - p3.Y)) - ((p2.X - p3.X) * (p1.Y - p3.Y));
    }

    private void ApplyImportedAnnotations()
    {
        if (_importedAnnotations is null)
        {
            return;
        }

        _manualActorComponentIds.Clear();
        foreach (var componentId in _importedAnnotations.ActorComponentIds)
        {
            if (_componentsById.ContainsKey(componentId))
            {
                _manualActorComponentIds.Add(componentId);
            }
        }

        _componentColorOverrides.Clear();
        foreach (var (componentId, color) in _importedAnnotations.ComponentColorOverrides)
        {
            if (_componentsById.ContainsKey(componentId))
            {
                _componentColorOverrides[componentId] = color;
                _componentColorDraft = color;
            }
        }

        _composites.Clear();
        _componentToCompositeId.Clear();
        var maxCompositeId = 0;

        foreach (var importedComposite in _importedAnnotations.Composites.OrderBy(composite => composite.Id))
        {
            var composite = new CompositeObject
            {
                Id = importedComposite.Id,
                Name = importedComposite.Name,
                IsActor = importedComposite.IsActor,
                NextInteractionUnitId = 1,
                PivotModel = importedComposite.PivotModel,
                PositionModel = importedComposite.PositionModel,
                RotationYprDegrees = importedComposite.RotationYprDegrees,
                CoordinateYprDegrees = importedComposite.CoordinateYprDegrees,
                CoordinateSystemMode = importedComposite.CoordinateSystemMode,
            };

            foreach (var componentId in importedComposite.ComponentIds.Distinct().Order())
            {
                if (!_componentsById.ContainsKey(componentId))
                {
                    continue;
                }

                if (_componentToCompositeId.TryGetValue(componentId, out var oldCompositeId))
                {
                    var oldComposite = _composites.FirstOrDefault(item => item.Id == oldCompositeId);
                    oldComposite?.ComponentIds.Remove(componentId);
                }

                composite.ComponentIds.Add(componentId);
                _componentToCompositeId[componentId] = composite.Id;
            }

            foreach (var importedUnit in importedComposite.InteractionUnits.OrderBy(unit => unit.Id))
            {
                var interactionUnit = new InteractionUnitObject
                {
                    Id = importedUnit.Id,
                    Name = importedUnit.Name,
                };

                foreach (var componentId in importedUnit.ComponentIds.Distinct().Order())
                {
                    if (composite.ComponentIds.Contains(componentId))
                    {
                        interactionUnit.ComponentIds.Add(componentId);
                    }
                }

                if (interactionUnit.ComponentIds.Count > 0)
                {
                    composite.InteractionUnits.Add(interactionUnit);
                    composite.NextInteractionUnitId = Math.Max(composite.NextInteractionUnitId, interactionUnit.Id + 1);
                }
            }

            _composites.Add(composite);
            maxCompositeId = Math.Max(maxCompositeId, composite.Id);
        }

        _nextCompositeId = Math.Max(_nextCompositeId, maxCompositeId + 1);
        _selectedCompositeId = _composites.LastOrDefault()?.Id;
        _focusedCompositeId = null;
        _selectedInteractionUnitCompositeId = null;
        _selectedInteractionUnitId = null;
        _focusedInteractionUnitCompositeId = null;
        _focusedInteractionUnitId = null;
        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        _statusMessage = $"已读取 JSON：动态组件 {_manualActorComponentIds.Count} 个，组合体 {_composites.Count} 个，颜色覆写 {_componentColorOverrides.Count} 个。";
    }

    private void UpdateActorIdsFromComposites()
    {
        _actorComponentIds.Clear();

        foreach (var componentId in _manualActorComponentIds)
        {
            if (!_componentToCompositeId.ContainsKey(componentId))
            {
                _actorComponentIds.Add(componentId);
            }
        }

        foreach (var composite in _composites)
        {
            foreach (var componentId in composite.ComponentIds)
            {
                if (composite.IsActor)
                {
                    _actorComponentIds.Add(componentId);
                }
                else
                {
                    _actorComponentIds.Remove(componentId);
                }
            }
        }
    }

    private void RebuildStaticIndexBuffers()
    {
        var excluded = _composites
            .SelectMany(composite => composite.ComponentIds)
            .ToHashSet();
        foreach (var componentId in _manualActorComponentIds)
        {
            excluded.Add(componentId);
        }

        foreach (var componentId in _componentColorOverrides.Keys)
        {
            excluded.Add(componentId);
        }

        foreach (var chunk in _gpuChunks)
        {
            chunk.RebuildStaticIndexBuffer(excluded);
        }
    }

    private void RebuildCompositeDrawCaches()
    {
        _compositeDrawBatches.Clear();
        foreach (var composite in _composites)
        {
            composite.InvalidateBoundsCache();
            _compositeDrawBatches[composite.Id] = BuildMergedDrawBatches(composite.ComponentIds);
        }

        _standaloneActorDrawBatches.Clear();
        _standaloneActorDrawBatches.AddRange(BuildMergedDrawBatches(
            _manualActorComponentIds.Where(componentId => !_componentToCompositeId.ContainsKey(componentId))));

        _componentColorOverrideDrawBatches.Clear();
        _componentColorOverrideDrawBatches.AddRange(BuildMergedDrawBatches(
            _componentColorOverrides.Keys.Where(componentId => !_componentToCompositeId.ContainsKey(componentId))));
    }

    private List<ComponentDrawBatch> BuildMergedDrawBatches(IEnumerable<int> componentIds)
    {
        var renderRefs = new List<ComponentRenderRef>();
        foreach (var componentId in componentIds)
        {
            if (_componentRenderRefs.TryGetValue(componentId, out var refs))
            {
                renderRefs.AddRange(refs);
            }
        }

        renderRefs.Sort(static (left, right) =>
        {
            var chunkCompare = left.Chunk.ChunkIndex.CompareTo(right.Chunk.ChunkIndex);
            return chunkCompare != 0
                ? chunkCompare
                : left.Range.StartIndex.CompareTo(right.Range.StartIndex);
        });

        var batches = new List<ComponentDrawBatch>(renderRefs.Count);
        for (var index = 0; index < renderRefs.Count; index++)
        {
            var renderRef = renderRefs[index];
            var startIndex = renderRef.Range.StartIndex;
            var indexCount = renderRef.Range.IndexCount;
            while (index + 1 < renderRefs.Count)
            {
                var next = renderRefs[index + 1];
                if (!ReferenceEquals(renderRef.Chunk, next.Chunk) ||
                    startIndex + indexCount != next.Range.StartIndex ||
                    renderRef.Range.ComponentId != next.Range.ComponentId)
                {
                    break;
                }

                indexCount += next.Range.IndexCount;
                index++;
            }

            batches.Add(new ComponentDrawBatch(renderRef.Chunk, startIndex, indexCount, renderRef.Range.ComponentId));
        }

        return batches;
    }

    private void DrawBatches(IReadOnlyList<ComponentDrawBatch> batches, IReadOnlyCollection<int>? visibleChunkIndices = null)
    {
        if (batches.Count == 0)
        {
            return;
        }

        GpuChunk? boundChunk = null;
        foreach (var batch in batches)
        {
            if (visibleChunkIndices is not null && !visibleChunkIndices.Contains(batch.Chunk.ChunkIndex))
            {
                continue;
            }

            if (!ReferenceEquals(boundChunk, batch.Chunk))
            {
                batch.Chunk.BindFull();
                boundChunk = batch.Chunk;
            }

            if (_componentColorOverrides.TryGetValue(batch.ComponentId, out System.Numerics.Vector4 color))
            {
                GL.Uniform1(_componentOverrideEnabledLocation, 1);
                GL.Uniform3(_componentOverrideColorLocation, color.X, color.Y, color.Z);
            }
            else
            {
                GL.Uniform1(_componentOverrideEnabledLocation, 0);
            }

            batch.Chunk.DrawRangeBound(batch.StartIndex, batch.IndexCount);
        }

        GL.Uniform1(_componentOverrideEnabledLocation, 0);
    }

    private bool HasSelectedComponents(CompositeObject composite)
    {
        if (_selectedComponentIds.Count == 0)
        {
            return false;
        }

        foreach (var componentId in composite.ComponentIds)
        {
            if (_selectedComponentIds.Contains(componentId))
            {
                return true;
            }
        }

        return false;
    }

    private void BuildMapEditorUi()
    {
        if (_imgui is null || _mapEditingSession is null)
        {
            return;
        }

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(12.0f, 12.0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(390.0f, 0.0f), ImGuiCond.Always);
        ImGui.Begin("地图增益编辑", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);

        ImGui.Text($"地图: {_mapEditingSession.PresetName}");
        ImGui.Text($"设施数量: {_mapEditingSession.Document.Facilities.Count}");

        string[] facilityModeLabels = ["选择", "矩形", "线段", "多边形"];
        int facilityModeIndex = (int)_facilityDrawMode;
        if (ImGui.Combo("绘制模式", ref facilityModeIndex, facilityModeLabels, facilityModeLabels.Length))
        {
            _facilityDrawMode = (FacilityDrawMode)facilityModeIndex;
            if (_facilityDrawMode is not FacilityDrawMode.Polygon)
            {
                _facilityPolygonPoints.Clear();
            }

            if (_facilityDrawMode is not (FacilityDrawMode.Rect or FacilityDrawMode.Line))
            {
                _topDownDraggingRect = false;
                _facilityRectStartWorld = null;
                _facilityRectCurrentWorld = null;
            }
        }

        string[] facilityTypes =
        [
            "supply",
            "buff_supply",
            "mineral_exchange",
            "mining_area",
            "dog_hole",
            "fly_slope",
            "outpost",
            "base",
            "energy_mechanism",
            "wall",
        ];
        int facilityTypeIndex = Array.FindIndex(facilityTypes, value => string.Equals(value, _facilityDraftType, StringComparison.OrdinalIgnoreCase));
        facilityTypeIndex = Math.Max(0, facilityTypeIndex);
        if (ImGui.Combo("设施类型", ref facilityTypeIndex, facilityTypes, facilityTypes.Length))
        {
            _facilityDraftType = facilityTypes[facilityTypeIndex];
        }

        string[] teamValues = ["neutral", "red", "blue"];
        int teamIndex = Array.FindIndex(teamValues, value => string.Equals(value, _facilityDraftTeam, StringComparison.OrdinalIgnoreCase));
        teamIndex = Math.Max(0, teamIndex);
        if (ImGui.Combo("所属阵营", ref teamIndex, teamValues, teamValues.Length))
        {
            _facilityDraftTeam = teamValues[teamIndex];
        }

        ImGui.InputText("Id 前缀", ref _facilityDraftBaseId, 128);
        ImGui.InputFloat("厚度", ref _facilityDraftThickness, 1.0f, 10.0f, "%.2f");
        ImGui.InputFloat("高度(m)", ref _facilityDraftHeightM, 0.05f, 0.2f, "%.3f");
        _facilityDraftThickness = Math.Max(0.01f, _facilityDraftThickness);

        if (ImGui.Button(_viewMode == ViewMode.TopDown ? "切到自由视角 (F6)" : "切到正顶视角 (F6)", new System.Numerics.Vector2(-1, 0)))
        {
            ToggleViewMode();
        }

        if (ImGui.Button("恢复局内固定机位", new System.Numerics.Vector2(-1, 0)))
        {
            ConfigureMatchLikeCamera();
            _statusMessage = "已恢复到地图单位测试固定机位。";
        }

        if (ImGui.Button("保存 map.json (Ctrl+S)", new System.Numerics.Vector2(-1, 0)))
        {
            SaveMapDocument();
        }

        if (ImGui.Button("取消当前绘制 (Esc)", new System.Numerics.Vector2(-1, 0)))
        {
            CancelTopDownDraft();
        }

        FacilityRegionEditorModel? selectedFacility = GetSelectedFacility();
        if (selectedFacility is not null)
        {
            ImGui.Separator();
            ImGui.Text($"当前选中: {selectedFacility.Id}");
            string facilityId = selectedFacility.Id;
            if (ImGui.InputText("设施 Id", ref facilityId, 128) && !string.IsNullOrWhiteSpace(facilityId))
            {
                selectedFacility.Id = facilityId.Trim();
            }

            string facilityType = selectedFacility.Type;
            if (ImGui.InputText("设施类型##selected", ref facilityType, 128))
            {
                selectedFacility.Type = string.IsNullOrWhiteSpace(facilityType) ? selectedFacility.Type : facilityType.Trim();
            }

            string facilityTeam = selectedFacility.Team;
            if (ImGui.InputText("阵营##selected", ref facilityTeam, 64))
            {
                selectedFacility.Team = string.IsNullOrWhiteSpace(facilityTeam) ? selectedFacility.Team : facilityTeam.Trim();
            }

            float selectedThickness = (float)selectedFacility.Thickness;
            if (ImGui.InputFloat("厚度##selected", ref selectedThickness, 1.0f, 10.0f, "%.2f"))
            {
                selectedFacility.Thickness = Math.Max(0.01, selectedThickness);
            }

            float selectedHeight = (float)selectedFacility.HeightM;
            if (ImGui.InputFloat("高度(m)##selected", ref selectedHeight, 0.05f, 0.2f, "%.3f"))
            {
                selectedFacility.HeightM = selectedHeight;
            }

            if (ImGui.Button("删除当前设施 (Delete)", new System.Numerics.Vector2(-1, 0)))
            {
                DeleteSelectedFacility();
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped("操作说明: Tab 切换绘制模式。矩形/线段模式按住左键拖拽后松开提交；多边形模式左键逐点绘制，Enter 或右键提交，Backspace 删除最后一点，Esc 取消当前草稿，Delete 删除当前设施。选择模式下拖拽已选设施可直接平移。");
        ImGui.End();
    }

    private void DrawMapEditorOverlay()
    {
        if (_mapEditingSession is null || _imgui is null)
        {
            return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var (_, _, viewProjection) = BuildViewProjection();
        for (int index = 0; index < _mapEditingSession.Document.Facilities.Count; index++)
        {
            FacilityRegionEditorModel facility = _mapEditingSession.Document.Facilities[index];
            DrawFacilityOverlay(drawList, viewProjection, facility, index == _selectedFacilityIndex);
        }

        if (_topDownDraggingRect && _facilityRectStartWorld is System.Numerics.Vector2 rectStart && _facilityRectCurrentWorld is System.Numerics.Vector2 rectEnd)
        {
            uint draftColor = ImGui.GetColorU32(new System.Numerics.Vector4(1.0f, 0.85f, 0.25f, 0.95f));
            if (_facilityDrawMode == FacilityDrawMode.Line)
            {
                DrawLineOverlay(drawList, viewProjection, rectStart, rectEnd, draftColor, 3.0f);
            }
            else
            {
                DrawRectOverlay(drawList, viewProjection, rectStart, rectEnd, draftColor);
            }
        }

        if (_facilityPolygonPoints.Count >= 1)
        {
            uint color = ImGui.GetColorU32(new System.Numerics.Vector4(0.2f, 0.95f, 1.0f, 0.95f));
            for (int index = 0; index < _facilityPolygonPoints.Count; index++)
            {
                if (TryProjectTopDownPoint(_facilityPolygonPoints[index], viewProjection, out System.Numerics.Vector2 point))
                {
                    drawList.AddCircleFilled(point, 4.0f, color);
                    if (index > 0 && TryProjectTopDownPoint(_facilityPolygonPoints[index - 1], viewProjection, out System.Numerics.Vector2 previous))
                    {
                        drawList.AddLine(previous, point, color, 2.0f);
                    }
                }
            }
        }
    }

    private void DrawFacilityOverlay(ImDrawListPtr drawList, Matrix4 viewProjection, FacilityRegionEditorModel facility, bool selected)
    {
        uint color = ImGui.GetColorU32(selected
            ? new System.Numerics.Vector4(1.0f, 0.82f, 0.18f, 0.95f)
            : new System.Numerics.Vector4(0.18f, 0.92f, 0.55f, 0.85f));
        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            System.Numerics.Vector2 startWorld = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1));
            System.Numerics.Vector2 endWorld = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2));
            DrawLineOverlay(
                drawList,
                viewProjection,
                startWorld,
                endWorld,
                color,
                selected ? 4.0f : 2.5f);
            DrawFacilityLabel(drawList, viewProjection, facility);
            return;
        }

        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<Point2D> points = facility.ParsePoints();
            if (points.Count >= 3)
            {
                _overlayScreenPoints.Clear();
                for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    System.Numerics.Vector2 worldPoint = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)points[pointIndex].X, (float)points[pointIndex].Y));
                    if (TryProjectTopDownPoint(worldPoint, viewProjection, out System.Numerics.Vector2 screenPoint))
                    {
                        _overlayScreenPoints.Add(screenPoint);
                    }
                }

                if (_overlayScreenPoints.Count >= 3)
                {
                    var screenPoints = CollectionsMarshal.AsSpan(_overlayScreenPoints);
                    drawList.AddPolyline(ref screenPoints[0], screenPoints.Length, color, ImDrawFlags.Closed, selected ? 3.0f : 2.0f);
                }
            }

            DrawFacilityLabel(drawList, viewProjection, facility);
            return;
        }

        DrawRectOverlay(
            drawList,
            viewProjection,
            MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1)),
            MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2)),
            color,
            selected ? 3.0f : 2.0f);
        DrawFacilityLabel(drawList, viewProjection, facility);
    }

    private void DrawFacilityLabel(ImDrawListPtr drawList, Matrix4 viewProjection, FacilityRegionEditorModel facility)
    {
        if (string.IsNullOrWhiteSpace(facility.Id))
        {
            return;
        }

        if (TryProjectTopDownPoint(GetFacilityAnchorPointWorld(facility), viewProjection, out System.Numerics.Vector2 labelPoint))
        {
            drawList.AddText(labelPoint + new System.Numerics.Vector2(6.0f, -14.0f), ImGui.GetColorU32(new System.Numerics.Vector4(0.96f, 0.98f, 1.0f, 0.92f)), facility.Id);
        }
    }

    private void DrawRectOverlay(
        ImDrawListPtr drawList,
        Matrix4 viewProjection,
        System.Numerics.Vector2 start,
        System.Numerics.Vector2 end,
        uint color,
        float thickness = 2.0f)
    {
        Span<System.Numerics.Vector2> corners =
        [
            new(MathF.Min(start.X, end.X), MathF.Min(start.Y, end.Y)),
            new(MathF.Max(start.X, end.X), MathF.Min(start.Y, end.Y)),
            new(MathF.Max(start.X, end.X), MathF.Max(start.Y, end.Y)),
            new(MathF.Min(start.X, end.X), MathF.Max(start.Y, end.Y)),
        ];
        Span<System.Numerics.Vector2> screenPoints = stackalloc System.Numerics.Vector2[4];
        for (int index = 0; index < corners.Length; index++)
        {
            if (!TryProjectTopDownPoint(corners[index], viewProjection, out screenPoints[index]))
            {
                return;
            }
        }

        drawList.AddPolyline(ref screenPoints[0], screenPoints.Length, color, ImDrawFlags.Closed, thickness);
    }

    private void DrawLineOverlay(
        ImDrawListPtr drawList,
        Matrix4 viewProjection,
        System.Numerics.Vector2 start,
        System.Numerics.Vector2 end,
        uint color,
        float thickness)
    {
        if (!TryProjectTopDownPoint(start, viewProjection, out System.Numerics.Vector2 startPoint)
            || !TryProjectTopDownPoint(end, viewProjection, out System.Numerics.Vector2 endPoint))
        {
            return;
        }

        drawList.AddLine(startPoint, endPoint, color, thickness);
    }

    private bool TryProjectTopDownPoint(System.Numerics.Vector2 point, Matrix4 viewProjection, out System.Numerics.Vector2 screenPoint)
    {
        return TryProjectToScreen(new System.Numerics.Vector3(point.X, 0.02f, point.Y), viewProjection, out screenPoint);
    }

    private void DrawComponentColorEditorUi()
    {
        ImGui.Separator();
        ImGui.Text("组件颜色覆写");
        ImGui.ColorEdit4("调色板", ref _componentColorDraft);
        bool hasSelection = _selectedComponentIds.Count > 0 || _selectedComponentId.HasValue;
        if (!hasSelection)
        {
            ImGui.TextDisabled("先在场景中选择一个或多个组件，再应用颜色。");
            return;
        }

        if (ImGui.Button("应用到选中组件", new System.Numerics.Vector2(-1, 0)))
        {
            ApplyComponentColorToSelection();
        }

        if (ImGui.Button("吸取当前主组件颜色", new System.Numerics.Vector2(-1, 0)))
        {
            PickSelectedComponentColor();
        }

        if (ImGui.Button("清除选中组件颜色覆写", new System.Numerics.Vector2(-1, 0)))
        {
            ClearComponentColorSelection();
        }

        ImGui.Text($"颜色覆写数量：{_componentColorOverrides.Count}");
    }

    private IReadOnlyCollection<int> ResolveColorEditSelection()
    {
        if (_selectedComponentIds.Count > 0)
        {
            return _selectedComponentIds;
        }

        return _selectedComponentId is int selectedComponentId ? [selectedComponentId] : Array.Empty<int>();
    }

    private void ApplyComponentColorToSelection()
    {
        var selected = ResolveColorEditSelection();
        if (selected.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (int componentId in selected)
        {
            if (_componentsById.ContainsKey(componentId))
            {
                _componentColorOverrides[componentId] = new System.Numerics.Vector4(
                    Math.Clamp(_componentColorDraft.X, 0.0f, 1.0f),
                    Math.Clamp(_componentColorDraft.Y, 0.0f, 1.0f),
                    Math.Clamp(_componentColorDraft.Z, 0.0f, 1.0f),
                    Math.Clamp(_componentColorDraft.W <= 0.0f ? 1.0f : _componentColorDraft.W, 0.0f, 1.0f));
            }
        }

        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
        _statusMessage = $"已给 {selected.Count} 个组件设置颜色。";
    }

    private void PickSelectedComponentColor()
    {
        int? selected = _selectedComponentId ?? _selectedComponentIds.OrderBy(componentId => componentId).FirstOrDefault();
        if (selected is not int componentId || !_componentsById.ContainsKey(componentId))
        {
            return;
        }

        if (_componentColorOverrides.TryGetValue(componentId, out System.Numerics.Vector4 color)
            || TrySampleComponentColor(componentId, out color))
        {
            _componentColorDraft = color;
            _statusMessage = $"已吸取组件 {componentId} 的颜色。";
        }
    }

    private void ClearComponentColorSelection()
    {
        var selected = ResolveColorEditSelection();
        if (selected.Count == 0)
        {
            return;
        }

        PushUndoSnapshot();
        foreach (int componentId in selected)
        {
            _componentColorOverrides.Remove(componentId);
        }

        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
        _statusMessage = $"已清除 {selected.Count} 个组件的颜色覆写。";
    }

    private bool TrySampleComponentColor(int componentId, out System.Numerics.Vector4 color)
    {
        color = new System.Numerics.Vector4(1f, 1f, 1f, 1f);
        if (!_componentRenderRefs.TryGetValue(componentId, out var refs) || refs.Count == 0)
        {
            return false;
        }

        foreach (var renderRef in refs)
        {
            int start = renderRef.Range.StartIndex;
            int end = start + renderRef.Range.IndexCount;
            if (start < 0 || end > renderRef.Chunk.SourceChunk.Indices.Length)
            {
                continue;
            }

            int vertexIndex = (int)renderRef.Chunk.SourceChunk.Indices[start];
            if (vertexIndex < 0 || vertexIndex >= renderRef.Chunk.SourceChunk.Vertices.Length)
            {
                continue;
            }

            uint packed = renderRef.Chunk.SourceChunk.Vertices[vertexIndex].Color;
            color = new System.Numerics.Vector4(
                (packed & 0xFF) / 255.0f,
                ((packed >> 8) & 0xFF) / 255.0f,
                ((packed >> 16) & 0xFF) / 255.0f,
                ((packed >> 24) & 0xFF) / 255.0f);
            if (color.W <= 0.0f)
            {
                color.W = 1.0f;
            }

            return true;
        }

        return false;
    }

    private void BuildEditorUi()
    {
        if (_imgui is null)
        {
            return;
        }

        const float panelWidth = 420.0f;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(MathF.Max(0.0f, ClientSize.X - panelWidth), 0.0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(panelWidth, ClientSize.Y), ImGuiCond.Always);
        ImGui.Begin("组合体编辑器", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);

        ImGui.Text("世界坐标系");
        ImGui.Text("地图尺寸：X 方向 28 米，Z 方向 15 米");
        ImGui.Text($"X 比例尺：{_worldScale.XMetersPerUnit:F4} 米/模型单位");
        ImGui.Text($"Y 比例尺：{_worldScale.YMetersPerUnit:F4} 米/模型单位");
        ImGui.Text($"Z 比例尺：{_worldScale.ZMetersPerUnit:F4} 米/模型单位");
        ImGui.Text($"当前移动灵敏度：{_movementSpeed:F2}");
        ImGui.Text($"Caps 视线半径：{_sweepSelectionRadiusPixels:F0}px");
        if (ImGui.SliderFloat("Caps+左键视线半径", ref _sweepSelectionRadiusPixels, 1.0f, 80.0f, "%.0f px"))
        {
            _sweepSelectionRadiusPixels = Math.Clamp(_sweepSelectionRadiusPixels, 1.0f, 80.0f);
            _statusMessage = $"Caps+左键视线半径已调整为 {_sweepSelectionRadiusPixels:F0}px";
        }

        ImGui.Separator();

        var selectedComponentPreview = _selectedComponentIds.Count == 0
            ? "无"
            : string.Join("、", _selectedComponentIds.OrderBy(componentId => componentId).Take(6)) + (_selectedComponentIds.Count > 6 ? "..." : string.Empty);
        ImGui.Text($"已选组件数：{_selectedComponentIds.Count}");
        ImGui.Text($"组件预览：{selectedComponentPreview}");
        ImGui.Text($"当前主组件：{(_selectedComponentId?.ToString() ?? "无")}");
        ImGui.Text($"当前组合体：{(_selectedCompositeId?.ToString() ?? "无")}");
        ImGui.Text($"当前编辑组合体：{(_focusedCompositeId?.ToString() ?? "无")}");
        ImGui.TextWrapped(_statusMessage);
        DrawComponentColorEditorUi();
        ImGui.Separator();

        if (ImGui.Button("新建组合体 (N)", new System.Numerics.Vector2(-1, 0)))
        {
            CreateComposite(includeSelectedComponent: false, recordHistory: true);
        }

        if (ImGui.Button("将选中组件加入当前组合体 (Enter)", new System.Numerics.Vector2(-1, 0)))
        {
            AddSelectedComponentToSelectedComposite(recordHistory: true);
        }

        if (ImGui.Button("从组合体移除选中组件", new System.Numerics.Vector2(-1, 0)))
        {
            RemoveSelectedComponentFromComposite(recordHistory: true);
        }

        if (ImGui.Button("撤销 (Ctrl+Z)", new System.Numerics.Vector2(-1, 0)))
        {
            Undo();
        }

        if (ImGui.Button("重做 (Ctrl+Y)", new System.Numerics.Vector2(-1, 0)))
        {
            Redo();
        }

        if (ImGui.Button("保存当前标注 (Ctrl+S)", new System.Numerics.Vector2(-1, 0)))
        {
            SaveCurrentAnnotations();
        }

        if (ImGui.Button("标注另存为 (Ctrl+Shift+S)", new System.Numerics.Vector2(-1, 0)))
        {
            OpenSavePopup();
        }

        if (ImGui.Button("读取 JSON", new System.Numerics.Vector2(-1, 0)))
        {
            OpenLoadPopup();
        }

        ImGui.Separator();
        ImGui.Text($"组合体数量：{_composites.Count}");
        if (_focusedCompositeId is int focusedCompositeId)
        {
            var focusedComposite = _composites.FirstOrDefault(composite => composite.Id == focusedCompositeId);
            if (focusedComposite is null)
            {
                _focusedCompositeId = null;
                _focusedInteractionUnitCompositeId = null;
                _focusedInteractionUnitId = null;
            }
            else
            {
                if (ImGui.Button("返回组合体列表", new System.Numerics.Vector2(-1, 0)))
                {
                    _focusedCompositeId = null;
                    _focusedInteractionUnitCompositeId = null;
                    _focusedInteractionUnitId = null;
                }

                ImGui.Text($"正在编辑：{focusedComposite.Name}");
                ImGui.TextWrapped("双击左侧组合体可进入组件列表。此视图下，按 Enter 会把当前选中的组件加入这个组合体；互动单元也从下面的组件列表选中项创建。");
                ImGui.BeginChild("CompositeComponents", new System.Numerics.Vector2(0, 180), ImGuiChildFlags.Border);
                foreach (var componentId in focusedComposite.ComponentIds.OrderBy(componentId => componentId))
                {
                    var selected = _selectedComponentIds.Contains(componentId);
                    if (ImGui.Selectable($"组件 {componentId}", selected))
                    {
                        var ctrlDown = ImGui.GetIO().KeyCtrl;
                        if (!ctrlDown)
                        {
                            _selectedComponentIds.Clear();
                        }

                        _selectedComponentIds.Add(componentId);
                        _selectedComponentId = componentId;
                        _selectedCompositeId = focusedComposite.Id;
                        _selectedInteractionUnitCompositeId = null;
                        _selectedInteractionUnitId = null;
                    }
                }

                ImGui.EndChild();

                if (ImGui.Button("将组件列表选中项创建为互动单元", new System.Numerics.Vector2(-1, 0)))
                {
                    CreateInteractionUnitFromSelection(recordHistory: true);
                }

                ImGui.Separator();
                ImGui.Text($"互动单元数量：{focusedComposite.InteractionUnits.Count}");
                ImGui.BeginChild("InteractionUnitList", new System.Numerics.Vector2(0, 140), ImGuiChildFlags.Border);
                foreach (var interactionUnit in focusedComposite.InteractionUnits.OrderBy(unit => unit.Id))
                {
                    var selectedUnit = _selectedInteractionUnitCompositeId == focusedComposite.Id &&
                                       _selectedInteractionUnitId == interactionUnit.Id;
                    if (ImGui.Selectable($"{interactionUnit.Id}: {interactionUnit.Name}（{interactionUnit.ComponentIds.Count} 个组件）", selectedUnit, ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        SelectInteractionUnit(focusedComposite, interactionUnit, focusComposite: false);
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            _focusedInteractionUnitCompositeId = focusedComposite.Id;
                            _focusedInteractionUnitId = interactionUnit.Id;
                            _statusMessage = $"已打开互动单元 {interactionUnit.Name} 的组件列表";
                        }
                    }
                }

                ImGui.EndChild();

                var focusedInteractionUnit = _focusedInteractionUnitCompositeId == focusedComposite.Id &&
                                             _focusedInteractionUnitId is int focusedInteractionUnitId
                    ? focusedComposite.InteractionUnits.FirstOrDefault(unit => unit.Id == focusedInteractionUnitId)
                    : null;
                if (focusedInteractionUnit is not null)
                {
                    ImGui.Separator();
                    ImGui.Text($"正在查看互动单元：{focusedInteractionUnit.Name}");
                    ImGui.Text($"相关组件数量：{focusedInteractionUnit.ComponentIds.Count}");
                    if (ImGui.Button("关闭互动单元组件列表", new System.Numerics.Vector2(-1, 0)))
                    {
                        _focusedInteractionUnitCompositeId = null;
                        _focusedInteractionUnitId = null;
                    }

                    ImGui.BeginChild("FocusedInteractionUnitComponents", new System.Numerics.Vector2(0, 120), ImGuiChildFlags.Border);
                    foreach (var componentId in focusedInteractionUnit.ComponentIds.OrderBy(componentId => componentId))
                    {
                        var selected = _selectedComponentIds.Contains(componentId);
                        if (ImGui.Selectable($"组件 {componentId}", selected))
                        {
                            _selectedComponentId = componentId;
                            _selectedCompositeId = focusedComposite.Id;
                            _statusMessage = $"正在查看互动单元 {focusedInteractionUnit.Name} 中的组件 {componentId}";
                        }
                    }

                    ImGui.EndChild();
                }

                var selectedInteractionUnit = GetSelectedInteractionUnit();
                if (selectedInteractionUnit is not null && _selectedInteractionUnitCompositeId == focusedComposite.Id)
                {
                    var interactionUnitName = selectedInteractionUnit.Name;
                    if (ImGui.InputText("互动单元名称", ref interactionUnitName, 128) && interactionUnitName != selectedInteractionUnit.Name)
                    {
                        PushUndoSnapshot();
                        selectedInteractionUnit.Name = string.IsNullOrWhiteSpace(interactionUnitName)
                            ? $"互动单元 {selectedInteractionUnit.Id}"
                            : interactionUnitName;
                    }

                    if (ImGui.Button("删除当前互动单元", new System.Numerics.Vector2(-1, 0)))
                    {
                        DeleteSelectedInteractionUnit(recordHistory: true);
                    }
                }
            }
        }
        else
        {
            ImGui.BeginChild("CompositeList", new System.Numerics.Vector2(0, 180), ImGuiChildFlags.Border);
            foreach (var composite in _composites)
            {
                var selected = composite.Id == _selectedCompositeId;
                if (ImGui.Selectable($"{composite.Id}: {composite.Name}（{composite.ComponentIds.Count} 个组件）", selected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selectedCompositeId = composite.Id;
                    if (_selectedInteractionUnitCompositeId != composite.Id)
                    {
                        _selectedInteractionUnitCompositeId = null;
                        _selectedInteractionUnitId = null;
                    }

                    if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _focusedCompositeId = composite.Id;
                        _statusMessage = $"已进入 {composite.Name} 的组件列表";
                    }
                }
            }

            ImGui.EndChild();
            ImGui.TextWrapped("提示：双击组合体可进入其组件列表；场景内按 Ctrl+左键可以多选组件。");
        }

        var selectedComposite = GetSelectedComposite();
        if (selectedComposite is not null)
        {
            var isActor = selectedComposite.IsActor;
            if (ImGui.Checkbox("动态组合体", ref isActor))
            {
                PushUndoSnapshot();
                selectedComposite.IsActor = isActor;
                UpdateActorIdsFromComposites();
                RebuildCompositeDrawCaches();
                _statusMessage = $"{selectedComposite.Name} 已切换为{(isActor ? "动态体" : "静态")}";
            }

            var name = selectedComposite.Name;
            if (ImGui.InputText("名称", ref name, 128) && name != selectedComposite.Name)
            {
                PushUndoSnapshot();
                selectedComposite.Name = string.IsNullOrWhiteSpace(name) ? $"组合体 {selectedComposite.Id}" : name;
            }

            var spaceIndex = selectedComposite.CoordinateSystemMode == CoordinateSystemMode.Custom ? 1 : 0;
            var spaces = new[] { "世界系", "自定义系" };
            if (ImGui.Combo("移动/旋转坐标系", ref spaceIndex, spaces, spaces.Length))
            {
                PushUndoSnapshot();
                selectedComposite.CoordinateSystemMode = spaceIndex == 1 ? CoordinateSystemMode.Custom : CoordinateSystemMode.World;
                _statusMessage = $"当前组合体坐标系切换为 {spaces[spaceIndex]}";
            }
            ImGui.TextDisabled("自定义系使用组合体自己的 X/Y/Z 轴；世界系直接使用世界坐标轴。");

            var coordinateYpr = selectedComposite.CoordinateYprDegrees;
            var coordinateEdited = ImGui.InputFloat3("坐标系轴旋转 YPR（度）", ref coordinateYpr, "%.2f");
            coordinateEdited |= DrawVector3ArrowAdjusters("coordinate-ypr-nudge", ref coordinateYpr, _rotationNudgeDegrees, "坐标轴");
            if (coordinateEdited)
            {
                PushUndoSnapshot();
                selectedComposite.CoordinateYprDegrees = coordinateYpr;
                selectedComposite.CoordinateSystemMode = CoordinateSystemMode.Custom;
                _statusMessage = $"{selectedComposite.Name} 的组合体系轴方向已更新";
            }

            if (ImGui.Button("坐标系轴=当前模型旋转", new System.Numerics.Vector2(-1, 0)))
            {
                PushUndoSnapshot();
                selectedComposite.CoordinateYprDegrees = selectedComposite.RotationYprDegrees;
                selectedComposite.CoordinateSystemMode = CoordinateSystemMode.Custom;
                _statusMessage = $"{selectedComposite.Name} 的组合体系轴已对齐当前模型旋转";
            }

            if (ImGui.Button("重置坐标系轴", new System.Numerics.Vector2(-1, 0)))
            {
                PushUndoSnapshot();
                selectedComposite.CoordinateYprDegrees = System.Numerics.Vector3.Zero;
                selectedComposite.CoordinateSystemMode = CoordinateSystemMode.World;
                _statusMessage = $"{selectedComposite.Name} 的组合体系轴已重置";
            }

            var coordinateOriginMeters = _worldScale.ModelToMeters(selectedComposite.PositionModel);
            var coordinateOriginEdited = ImGui.InputFloat3("坐标系零点（米）", ref coordinateOriginMeters, "%.3f");
            coordinateOriginEdited |= DrawVector3ArrowAdjusters("coordinate-origin-nudge", ref coordinateOriginMeters, 0.01f, "坐标系零点");
            if (coordinateOriginEdited)
            {
                PushUndoSnapshot();
                selectedComposite.PositionModel = _worldScale.MetersToModel(coordinateOriginMeters);
                _statusMessage = $"{selectedComposite.Name} 的坐标系零点已更新";
            }

            var pivotMeters = _worldScale.ModelToMeters(selectedComposite.PivotModel);
            var pivotEdited = ImGui.InputFloat3("旋转锚点（米）", ref pivotMeters, "%.3f");
            pivotEdited |= DrawVector3ArrowAdjusters("pivot-nudge", ref pivotMeters, 0.01f, "锚点");
            if (pivotEdited)
            {
                PushUndoSnapshot();
                SetCompositePivotPreserveCurrentPose(selectedComposite, _worldScale.MetersToModel(pivotMeters));
                _statusMessage = $"{selectedComposite.Name} 的旋转锚点已更新";
            }

            if (ImGui.Button("锚点=当前主组件中心", new System.Numerics.Vector2(-1, 0)))
            {
                if (_selectedComponentId is int selectedComponentId && _componentsById.TryGetValue(selectedComponentId, out var selectedComponent))
                {
                    PushUndoSnapshot();
                    SetCompositePivotPreserveCurrentPose(selectedComposite, selectedComponent.Bounds.Center);
                    _statusMessage = $"{selectedComposite.Name} 的旋转锚点已设为组件 {selectedComponentId} 中心";
                }
                else
                {
                    _statusMessage = "请先选中一个组件，再用它设置旋转锚点。";
                }
            }

            if (ImGui.Button("锚点=组合体组件中心", new System.Numerics.Vector2(-1, 0)))
            {
                if (TryComputeCompositeLocalBounds(selectedComposite, out var localBounds))
                {
                    PushUndoSnapshot();
                    SetCompositePivotPreserveCurrentPose(selectedComposite, localBounds.Center);
                    _statusMessage = $"{selectedComposite.Name} 的旋转锚点已设为组合体中心";
                }
            }

            if (ImGui.Button("用当前旋转锚点重建组合体系", new System.Numerics.Vector2(-1, 0)))
            {
                PushUndoSnapshot();
                RebuildCompositeCoordinateSystemAtPivot(selectedComposite);
                selectedComposite.CoordinateSystemMode = CoordinateSystemMode.Custom;
                _statusMessage = $"{selectedComposite.Name} 已以当前旋转锚点重建组合体系";
            }

            if (ImGui.Button("用当前主组件中心重建组合体系", new System.Numerics.Vector2(-1, 0)))
            {
                if (_selectedComponentId is int selectedComponentId && _componentsById.TryGetValue(selectedComponentId, out var selectedComponent))
                {
                    PushUndoSnapshot();
                    SetCompositePivotPreserveCurrentPose(selectedComposite, selectedComponent.Bounds.Center);
                    RebuildCompositeCoordinateSystemAtPivot(selectedComposite);
                    selectedComposite.CoordinateSystemMode = CoordinateSystemMode.Custom;
                    _statusMessage = $"{selectedComposite.Name} 已以组件 {selectedComponentId} 中心重建组合体系";
                }
                else
                {
                    _statusMessage = "请先选中一个组件，再用它重建组合体系。";
                }
            }

            ImGui.Separator();
            ImGui.TextWrapped("空间点吸附：可选择坐标系零点、当前旋转锚点、当前组件中心或组合体中心作为锚点，然后在 3D 空间点击一个世界点，把组合体定位到该点。按 Esc 可以取消。");
            if (ImGui.Button("点击空间点（坐标系零点）", new System.Numerics.Vector2(-1, 0)))
            {
                StartAnchorPlacementSelection(AnchorPlacementSource.CoordinateOrigin);
            }

            if (ImGui.Button("点击空间点（当前锚点）", new System.Numerics.Vector2(-1, 0)))
            {
                StartAnchorPlacementSelection(AnchorPlacementSource.CurrentPivot);
            }

            if (ImGui.Button("点击空间点（当前组件中心）", new System.Numerics.Vector2(-1, 0)))
            {
                StartAnchorPlacementSelection(AnchorPlacementSource.SelectedComponentCenter);
            }

            if (ImGui.Button("点击空间点（组合体中心）", new System.Numerics.Vector2(-1, 0)))
            {
                StartAnchorPlacementSelection(AnchorPlacementSource.CompositeCenter);
            }

            if (_awaitingAnchorPlacementPoint)
            {
                ImGui.TextColored(new System.Numerics.Vector4(0.25f, 0.9f, 1.0f, 1.0f), "正在等待空间点选择：Alt 释放鼠标后，左键点击 3D 场景定位。");
            }

            var positionMeters = _worldScale.ModelToMeters(selectedComposite.PositionModel);
            var positionEdited = ImGui.InputFloat3("XYZ 位置（米）", ref positionMeters, "%.3f");
            positionEdited |= DrawVector3ArrowAdjusters("position-nudge", ref positionMeters, 0.01f, "位置");
            if (positionEdited)
            {
                PushUndoSnapshot();
                selectedComposite.PositionModel = _worldScale.MetersToModel(positionMeters);
                _statusMessage = $"{selectedComposite.Name} 的位置已更新";
            }

            ImGui.TextWrapped($"当前标注文件：{_currentExportPath}");
            if (ImGui.Button("保存当前位置修改", new System.Numerics.Vector2(-1, 0)))
            {
                SaveCurrentAnnotations();
            }

            var ypr = selectedComposite.RotationYprDegrees;
            var rotationEdited = ImGui.InputFloat3("YPR 旋转（度）", ref ypr, "%.2f");
            if (rotationEdited)
            {
                PushUndoSnapshot();
                selectedComposite.RotationYprDegrees = ypr;
                _statusMessage = $"{selectedComposite.Name} 的旋转已更新";
            }
            DrawRotationAxisAdjusters("rotation-axis-nudge", selectedComposite);

            ImGui.SetNextItemWidth(120.0f);
            ImGui.InputFloat("旋转步长（度）", ref _rotationNudgeDegrees, 0.1f, 1.0f, "%.2f");
            _rotationNudgeDegrees = Math.Clamp(_rotationNudgeDegrees, 0.01f, 45.0f);
            ImGui.Checkbox("反向旋转", ref _invertRotationDirection);
            if (ImGui.Button("绕 X 轴左转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.X, -_rotationNudgeDegrees);
            }

            ImGui.SameLine();
            if (ImGui.Button("绕 X 轴右转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.X, _rotationNudgeDegrees);
            }

            if (ImGui.Button("绕 Y 轴左转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.Y, -_rotationNudgeDegrees);
            }

            ImGui.SameLine();
            if (ImGui.Button("绕 Y 轴右转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.Y, _rotationNudgeDegrees);
            }

            if (ImGui.Button("绕 Z 轴左转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.Z, -_rotationNudgeDegrees);
            }

            ImGui.SameLine();
            if (ImGui.Button("绕 Z 轴右转", new System.Numerics.Vector2(96, 0)))
            {
                RotateSelectedCompositeAroundAxis(selectedComposite, GizmoAxis.Z, _rotationNudgeDegrees);
            }

            if (ImGui.Button("重置 YPR", new System.Numerics.Vector2(-1, 0)))
            {
                PushUndoSnapshot();
                selectedComposite.RotationYprDegrees = System.Numerics.Vector3.Zero;
                _statusMessage = $"{selectedComposite.Name} 的旋转已重置";
            }

            if (ImGui.Button("删除当前组合体", new System.Numerics.Vector2(-1, 0)))
            {
                DeleteSelectedComposite(recordHistory: true);
            }
        }

        ImGui.Separator();
        ImGui.TextWrapped("快捷说明：Alt 切换鼠标绑定；按住 Shift 并按住鼠标左键可进行屏幕框选；按住 Caps 会显示准星视线，Caps+左键会连续选中视线半径扫到的组件，Caps+右键会多选视线第一次交汇的组件；滚轮调整 WASDFC 灵敏度；中心准星左键可单选，Ctrl+左键可多选；Shift 在未框选时仍可作为加速移动；Esc 只取消当前操作，不退出程序；选中组合体后会显示红 X / 绿 Y / 蓝 Z 三轴箭头。");

        HandleSavePopupUi();
        HandleLoadPopupUi();

        ImGui.End();
    }

    private void HandleSavePopupUi()
    {
        if (_savePopupRequested)
        {
            ImGui.OpenPopup("另存为");
            _savePopupRequested = false;
        }

        var keepOpen = true;
        if (ImGui.BeginPopupModal("另存为", ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("请输入新的导出文件路径：");
            ImGui.InputText("保存路径", ref _saveDialogPath, 512);

            if (ImGui.Button("保存", new System.Numerics.Vector2(120, 0)))
            {
                ExportComponentRoles(_saveDialogPath);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new System.Numerics.Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void HandleLoadPopupUi()
    {
        if (_loadPopupRequested)
        {
            ImGui.OpenPopup("读取 JSON");
            _loadPopupRequested = false;
        }

        var keepOpen = true;
        if (ImGui.BeginPopupModal("读取 JSON", ref keepOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("请选择要读取的 JSON 文件：");
            ImGui.InputText("JSON 路径", ref _loadDialogPath, 512);

            if (ImGui.Button("浏览...", new System.Numerics.Vector2(120, 0)))
            {
                var selectedPath = FileDialogService.PickJsonFile(_loadDialogPath);
                if (!string.IsNullOrWhiteSpace(selectedPath))
                {
                    _loadDialogPath = selectedPath;
                }
            }

            if (ImGui.Button("读取", new System.Numerics.Vector2(120, 0)))
            {
                LoadAnnotationsFromPath(_loadDialogPath, recordHistory: true);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new System.Numerics.Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private bool DrawVector3ArrowAdjusters(string id, ref System.Numerics.Vector3 value, float step, string label)
    {
        var changed = false;
        ImGui.PushID(id);
        DrawAxisNudge("X", 0, ref value, step, label, ref changed);
        DrawAxisNudge("Y", 1, ref value, step, label, ref changed);
        DrawAxisNudge("Z", 2, ref value, step, label, ref changed);
        ImGui.PopID();
        return changed;
    }

    private void DrawRotationAxisAdjusters(string id, CompositeObject composite)
    {
        ImGui.PushID(id);
        DrawRotationAxisNudge("X", GizmoAxis.X, composite);
        DrawRotationAxisNudge("Y", GizmoAxis.Y, composite);
        DrawRotationAxisNudge("Z", GizmoAxis.Z, composite);
        ImGui.PopID();
    }

    private void DrawRotationAxisNudge(string axisLabel, GizmoAxis axis, CompositeObject composite)
    {
        ImGui.Text($"旋转 {axisLabel}");
        ImGui.SameLine(72.0f);
        if (ImGui.ArrowButton($"{axisLabel}-", ImGuiDir.Left))
        {
            RotateSelectedCompositeAroundAxis(composite, axis, -_rotationNudgeDegrees);
        }

        ImGui.SameLine();
        if (ImGui.ArrowButton($"{axisLabel}+", ImGuiDir.Right))
        {
            RotateSelectedCompositeAroundAxis(composite, axis, _rotationNudgeDegrees);
        }
    }

    private static void DrawAxisNudge(
        string axisLabel,
        int axisIndex,
        ref System.Numerics.Vector3 value,
        float step,
        string label,
        ref bool changed)
    {
        ImGui.Text($"{label} {axisLabel}");
        ImGui.SameLine(72.0f);
        if (ImGui.ArrowButton($"{axisLabel}-", ImGuiDir.Left))
        {
            SetVectorComponent(ref value, axisIndex, GetVectorComponent(value, axisIndex) - step);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.ArrowButton($"{axisLabel}+", ImGuiDir.Right))
        {
            SetVectorComponent(ref value, axisIndex, GetVectorComponent(value, axisIndex) + step);
            changed = true;
        }
    }

    private static float GetVectorComponent(System.Numerics.Vector3 value, int axisIndex)
    {
        return axisIndex switch
        {
            0 => value.X,
            1 => value.Y,
            2 => value.Z,
            _ => 0.0f,
        };
    }

    private static void SetVectorComponent(ref System.Numerics.Vector3 value, int axisIndex, float componentValue)
    {
        switch (axisIndex)
        {
            case 0:
                value.X = componentValue;
                break;
            case 1:
                value.Y = componentValue;
                break;
            case 2:
                value.Z = componentValue;
                break;
        }
    }

    private void SetCompositePivotPreserveCurrentPose(CompositeObject composite, System.Numerics.Vector3 newPivotModel)
    {
        var currentMatrix = composite.ModelMatrix;
        composite.PivotModel = newPivotModel;
        composite.PositionModel = System.Numerics.Vector3.Transform(newPivotModel, currentMatrix);
    }

    private static void RebuildCompositeCoordinateSystemAtPivot(CompositeObject composite)
    {
        composite.PositionModel = System.Numerics.Vector3.Transform(composite.PivotModel, composite.ModelMatrix);
        composite.CoordinateYprDegrees = composite.RotationYprDegrees;
    }

    private bool TryComputeCompositeLocalBounds(CompositeObject composite, out BoundingBox bounds)
    {
        bounds = BoundingBox.CreateEmpty();
        foreach (var componentId in composite.ComponentIds)
        {
            if (_componentsById.TryGetValue(componentId, out var component))
            {
                bounds.Include(component.Bounds);
            }
        }

        return bounds.IsValid();
    }

    private void StartAnchorPlacementSelection(AnchorPlacementSource source)
    {
        var composite = GetSelectedComposite();
        if (composite is null)
        {
            _statusMessage = "请先选择一个组合体，再进行空间点吸附。";
            return;
        }

        if (!TryResolveAnchorModelPoint(composite, source, out _, out string message))
        {
            _statusMessage = message;
            return;
        }

        _pendingAnchorPlacementSource = source;
        _awaitingAnchorPlacementPoint = true;
        _statusMessage = $"{composite.Name} 已进入空间点吸附模式；Alt 释放鼠标后，左键点击 3D 场景定位。";
    }

    private bool TryResolveAnchorModelPoint(
        CompositeObject composite,
        AnchorPlacementSource source,
        out System.Numerics.Vector3 anchorModel,
        out string message)
    {
        switch (source)
        {
            case AnchorPlacementSource.CoordinateOrigin:
                anchorModel = composite.PivotModel;
                message = string.Empty;
                return true;
            case AnchorPlacementSource.CurrentPivot:
                anchorModel = composite.PivotModel;
                message = string.Empty;
                return true;
            case AnchorPlacementSource.SelectedComponentCenter:
                if (_selectedComponentId is int selectedComponentId && _componentsById.TryGetValue(selectedComponentId, out var selectedComponent))
                {
                    anchorModel = selectedComponent.Bounds.Center;
                    message = string.Empty;
                    return true;
                }

                anchorModel = default;
                message = "请先选中一个组件，再用其中心作为空间点吸附锚点。";
                return false;
            case AnchorPlacementSource.CompositeCenter:
                if (TryComputeCompositeLocalBounds(composite, out var localBounds))
                {
                    anchorModel = localBounds.Center;
                    message = string.Empty;
                    return true;
                }

                anchorModel = composite.PivotModel;
                message = "当前组合体没有可用的本地边界，无法以中心作为锚点。";
                return false;
            default:
                anchorModel = composite.PivotModel;
                message = string.Empty;
                return true;
        }
    }

    private void TryPlaceSelectedCompositeAtPointer(bool recordHistory)
    {
        var composite = GetSelectedComposite();
        if (composite is null)
        {
            _awaitingAnchorPlacementPoint = false;
            _statusMessage = "当前没有选中的组合体，已取消空间点吸附。";
            return;
        }

        if (!TryResolveAnchorModelPoint(composite, _pendingAnchorPlacementSource, out var anchorModel, out string anchorMessage))
        {
            _awaitingAnchorPlacementPoint = false;
            _statusMessage = anchorMessage;
            return;
        }

        if (!TryPickWorldPointAtPointer(out var worldPoint))
        {
            _statusMessage = "当前鼠标位置没有可用的场景交点，请尝试点击地形或组件表面。";
            return;
        }

        if (recordHistory)
        {
            PushUndoSnapshot();
        }

        System.Numerics.Matrix4x4 rotationMatrix = GetCompositeRotationMatrix(composite);
        System.Numerics.Vector3 rotatedAnchorOffset = System.Numerics.Vector3.TransformNormal(anchorModel - composite.PivotModel, rotationMatrix);
        composite.PivotModel = anchorModel;
        composite.PositionModel = worldPoint - rotatedAnchorOffset;
        _awaitingAnchorPlacementPoint = false;
        _statusMessage = $"{composite.Name} 已将锚点吸附到新的空间点。";
    }

    private bool TryPickWorldPointAtPointer(out System.Numerics.Vector3 worldPoint)
    {
        return TryPickWorldPoint(BuildPointerRay(), out worldPoint);
    }

    private bool TryPickWorldPoint(PickRay ray, out System.Numerics.Vector3 worldPoint)
    {
        worldPoint = default;
        float bestDistance = float.PositiveInfinity;
        bool found = false;

        foreach (var composite in _composites)
        {
            if (composite.ComponentIds.Count == 0 || !System.Numerics.Matrix4x4.Invert(composite.ModelMatrix, out var inverse))
            {
                continue;
            }

            var localOrigin = System.Numerics.Vector3.Transform(ray.Origin, inverse);
            var localDirection = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.TransformNormal(ray.Direction, inverse));
            var localRay = new PickRay(localOrigin, localDirection);
            foreach (var componentId in composite.ComponentIds)
            {
                if (!_componentRenderRefs.TryGetValue(componentId, out var refs))
                {
                    continue;
                }

                foreach (var renderRef in refs)
                {
                    if (!TryIntersectRayBounds(localRay, renderRef.Range.Bounds, out _)
                        || !TryIntersectComponentTriangles(localRay, renderRef.Chunk.SourceChunk, renderRef.Range, out float distance))
                    {
                        continue;
                    }

                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    bestDistance = distance;
                    worldPoint = System.Numerics.Vector3.Transform(localRay.Origin + localRay.Direction * distance, composite.ModelMatrix);
                    found = true;
                }
            }
        }

        foreach (var chunk in _scene.Chunks)
        {
            if (!TryIntersectRayBounds(ray, chunk.Bounds, out _))
            {
                continue;
            }

            foreach (var range in chunk.ComponentRanges)
            {
                if (!TryIntersectRayBounds(ray, range.Bounds, out _)
                    || !TryIntersectComponentTriangles(ray, chunk, range, out float distance)
                    || distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                worldPoint = ray.Origin + ray.Direction * distance;
                found = true;
            }
        }

        return found;
    }

    private void RotateSelectedCompositeAroundAxis(CompositeObject composite, GizmoAxis axis, float degrees)
    {
        PushUndoSnapshot();
        var signedDegrees = _invertRotationDirection ? -degrees : degrees;
        var axisVector = GetCompositeAxisVector(composite, axis);
        if (axisVector.LengthSquared() <= 1e-8f)
        {
            _statusMessage = $"无法绕 {AxisToChinese(axis)} 轴旋转：当前坐标系轴无效。";
            return;
        }

        var currentRotation = GetCompositeRotationMatrix(composite);
        var deltaRotation = System.Numerics.Matrix4x4.CreateFromAxisAngle(
            axisVector,
            MathF.PI / 180.0f * signedDegrees);
        var nextRotation = currentRotation * deltaRotation;
        composite.RotationYprDegrees = MatrixToYprDegrees(nextRotation);
        string spaceLabel = composite.CoordinateSystemMode == CoordinateSystemMode.Custom ? "自定义" : "世界";
        _statusMessage = $"{composite.Name} 已按当前{spaceLabel}坐标系绕 {AxisToChinese(axis)} 轴旋转 {signedDegrees:F2} 度";
    }

    private void DrawCenterPointer()
    {
        if (_viewMode == ViewMode.TopDown)
        {
            return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var center = new System.Numerics.Vector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f);
        var white = ImGui.GetColorU32(new System.Numerics.Vector4(1.0f, 1.0f, 1.0f, 0.95f));
        var black = ImGui.GetColorU32(new System.Numerics.Vector4(0.0f, 0.0f, 0.0f, 0.65f));

        drawList.AddCircle(center, 4.0f, black, 24, 2.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(-15, 0), center + new System.Numerics.Vector2(-5, 0), black, 3.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(5, 0), center + new System.Numerics.Vector2(15, 0), black, 3.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(0, -15), center + new System.Numerics.Vector2(0, -5), black, 3.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(0, 5), center + new System.Numerics.Vector2(0, 15), black, 3.0f);

        drawList.AddCircle(center, 4.0f, white, 24, 1.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(-14, 0), center + new System.Numerics.Vector2(-6, 0), white, 1.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(6, 0), center + new System.Numerics.Vector2(14, 0), white, 1.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(0, -14), center + new System.Numerics.Vector2(0, -6), white, 1.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(0, 6), center + new System.Numerics.Vector2(0, 14), white, 1.0f);
    }

    private void DrawSweepSightOverlay()
    {
        if (!IsFocused || !KeyboardState.IsKeyDown(Keys.CapsLock))
        {
            return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var center = new System.Numerics.Vector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f);
        var radius = Math.Clamp(_sweepSelectionRadiusPixels, 1.0f, 80.0f);
        var active = MouseState.IsButtonDown(MouseButton.Left) || MouseState.IsButtonDown(MouseButton.Right);
        var lineColor = ImGui.GetColorU32(active
            ? new System.Numerics.Vector4(0.1f, 1.0f, 0.42f, 0.95f)
            : new System.Numerics.Vector4(0.1f, 0.85f, 1.0f, 0.75f));
        var fillColor = ImGui.GetColorU32(active
            ? new System.Numerics.Vector4(0.1f, 1.0f, 0.42f, 0.10f)
            : new System.Numerics.Vector4(0.1f, 0.85f, 1.0f, 0.08f));
        drawList.AddCircleFilled(center, radius, fillColor, 48);
        drawList.AddCircle(center, radius, lineColor, 48, 2.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(-radius, 0.0f), center + new System.Numerics.Vector2(radius, 0.0f), lineColor, 1.0f);
        drawList.AddLine(center + new System.Numerics.Vector2(0.0f, -radius), center + new System.Numerics.Vector2(0.0f, radius), lineColor, 1.0f);
        drawList.AddCircleFilled(center, 3.0f, lineColor, 16);
        drawList.AddText(center + new System.Numerics.Vector2(radius + 8.0f, -radius - 8.0f), lineColor, $"Caps 镜头视线 {radius:F0}px");
    }

    private void DrawBoxSelectionOverlay()
    {
        if (!_isBoxSelecting)
        {
            return;
        }

        var drawList = ImGui.GetForegroundDrawList();
        var min = System.Numerics.Vector2.Min(_boxSelectionStart, _boxSelectionEnd);
        var max = System.Numerics.Vector2.Max(_boxSelectionStart, _boxSelectionEnd);
        var fillColor = ImGui.GetColorU32(new System.Numerics.Vector4(0.15f, 0.55f, 0.95f, 0.12f));
        var borderColor = ImGui.GetColorU32(new System.Numerics.Vector4(0.15f, 0.75f, 1.0f, 0.95f));

        drawList.AddRectFilled(min, max, fillColor);
        drawList.AddRect(min, max, borderColor, 0.0f, ImDrawFlags.None, 2.0f);
        drawList.AddText(min + new System.Numerics.Vector2(8.0f, -24.0f), borderColor, "框选中");
    }

    private void DrawCompositeGizmo()
    {
        if (!TryBuildGizmoData(out var gizmo))
        {
            return;
        }

        _lastGizmoOriginScreen = gizmo.OriginScreen;
        _lastGizmoAxisTips.Clear();
        _lastGizmoAxisTips.Add(GizmoAxis.X, gizmo.XTip);
        _lastGizmoAxisTips.Add(GizmoAxis.Y, gizmo.YTip);
        _lastGizmoAxisTips.Add(GizmoAxis.Z, gizmo.ZTip);

        var drawList = ImGui.GetForegroundDrawList();
        drawList.AddCircleFilled(gizmo.OriginScreen, 4.0f, ImGui.GetColorU32(new System.Numerics.Vector4(1, 1, 1, 0.9f)));

        DrawGizmoAxis(drawList, gizmo.OriginScreen, gizmo.XTip, GizmoAxis.X, new System.Numerics.Vector4(1.0f, 0.22f, 0.22f, 1.0f), "X");
        DrawGizmoAxis(drawList, gizmo.OriginScreen, gizmo.YTip, GizmoAxis.Y, new System.Numerics.Vector4(0.18f, 0.95f, 0.28f, 1.0f), "Y");
        DrawGizmoAxis(drawList, gizmo.OriginScreen, gizmo.ZTip, GizmoAxis.Z, new System.Numerics.Vector4(0.22f, 0.52f, 1.0f, 1.0f), "Z");
    }

    private void DrawGizmoAxis(ImDrawListPtr drawList, System.Numerics.Vector2 origin, System.Numerics.Vector2 tip, GizmoAxis axis, System.Numerics.Vector4 color, string label)
    {
        var thickness = _activeGizmoAxis == axis ? 5.0f : 3.0f;
        var colorU32 = ImGui.GetColorU32(_activeGizmoAxis == axis
            ? new System.Numerics.Vector4(1.0f, 1.0f, 0.2f, 1.0f)
            : color);

        drawList.AddLine(origin, tip, colorU32, thickness);

        var direction = tip - origin;
        if (direction.LengthSquared() > 0.01f)
        {
            direction = System.Numerics.Vector2.Normalize(direction);
            var left = new System.Numerics.Vector2(-direction.Y, direction.X);
            var arrowA = tip - (direction * 12.0f) + (left * 5.0f);
            var arrowB = tip - (direction * 12.0f) - (left * 5.0f);
            drawList.AddTriangleFilled(tip, arrowA, arrowB, colorU32);
        }

        drawList.AddText(tip + new System.Numerics.Vector2(8.0f, -8.0f), colorU32, label);
    }

    private bool TryBeginGizmoDrag(System.Numerics.Vector2 mousePosition)
    {
        if (!TryBuildGizmoData(out var gizmo))
        {
            return false;
        }

        const float pickThreshold = 12.0f;
        GizmoAxis bestAxis = GizmoAxis.None;
        var bestDistance = float.MaxValue;

        foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
        {
            var tip = gizmo.GetTip(axis);
            var distance = DistancePointToSegment(mousePosition, gizmo.OriginScreen, tip);
            if (distance < pickThreshold && distance < bestDistance)
            {
                bestDistance = distance;
                bestAxis = axis;
            }
        }

        if (bestAxis == GizmoAxis.None)
        {
            return false;
        }

        PushUndoSnapshot();
        _activeGizmoAxis = bestAxis;
        _lastFreeMousePosition = mousePosition;
        _statusMessage = $"开始拖动 {AxisToChinese(bestAxis)} 轴";
        return true;
    }

    private void UpdateGizmoDrag(System.Numerics.Vector2 mousePosition)
    {
        if (!MouseState.IsButtonDown(MouseButton.Left))
        {
            _statusMessage = "三轴拖动结束";
            _activeGizmoAxis = GizmoAxis.None;
            return;
        }

        var composite = GetGizmoComposite();
        if (composite is null || !TryBuildGizmoData(out var gizmo))
        {
            _activeGizmoAxis = GizmoAxis.None;
            return;
        }

        var axisTip = gizmo.GetTip(_activeGizmoAxis);
        var axisVector = axisTip - gizmo.OriginScreen;
        if (axisVector.LengthSquared() < 1.0f)
        {
            return;
        }

        var axisDirectionScreen = System.Numerics.Vector2.Normalize(axisVector);
        var mouseDelta = mousePosition - _lastFreeMousePosition;
        var pixelsAlongAxis = System.Numerics.Vector2.Dot(mouseDelta, axisDirectionScreen);
        var pixelsPerModelUnit = axisVector.Length() / gizmo.AxisLengthModel;
        if (pixelsPerModelUnit <= 0.0001f)
        {
            return;
        }

        var deltaModel = pixelsAlongAxis / pixelsPerModelUnit;
        if (MathF.Abs(deltaModel) <= 0.00001f)
        {
            return;
        }

        composite.PositionModel += GetCompositeAxisVector(composite, _activeGizmoAxis) * deltaModel;
        _lastFreeMousePosition = mousePosition;
        _statusMessage = $"{composite.Name} 沿 {AxisToChinese(_activeGizmoAxis)} 轴移动中";
    }

    private bool TryBuildGizmoData(out GizmoScreenData gizmo)
    {
        gizmo = default;
        var composite = GetGizmoComposite();
        if (composite is null)
        {
            return false;
        }

        var axisLength = GetGizmoAxisLengthModel();
        System.Numerics.Vector3 originWorld = composite.PositionModel;

        var (_, _, viewProjection) = BuildViewProjection();
        if (!TryProjectToScreen(originWorld, viewProjection, out var originScreen))
        {
            return false;
        }

        if (!TryProjectToScreen(originWorld + (GetCompositeAxisVector(composite, GizmoAxis.X) * axisLength), viewProjection, out var xTip) ||
            !TryProjectToScreen(originWorld + (GetCompositeAxisVector(composite, GizmoAxis.Y) * axisLength), viewProjection, out var yTip) ||
            !TryProjectToScreen(originWorld + (GetCompositeAxisVector(composite, GizmoAxis.Z) * axisLength), viewProjection, out var zTip))
        {
            return false;
        }

        gizmo = new GizmoScreenData(originScreen, xTip, yTip, zTip, axisLength);
        return true;
    }

    private float GetGizmoAxisLengthModel()
    {
        var size = _scene.Bounds.Size;
        return Math.Clamp(MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.08f, 0.8f, 3.0f);
    }

    private void ToggleViewMode()
    {
        CancelTopDownDraft();
        _viewMode = _viewMode == ViewMode.Free ? ViewMode.TopDown : ViewMode.Free;
        _statusMessage = _viewMode == ViewMode.TopDown
            ? "已切换到正顶俯视编辑视角。"
            : "已切换到自由观察视角。";
    }

    private void AdjustTopDownZoom(float wheelDelta)
    {
        float scale = MathF.Pow(0.92f, wheelDelta);
        _topDownHalfHeight = Math.Clamp(_topDownHalfHeight * scale, 1.2f, 180.0f);
        _statusMessage = $"俯视缩放已调整为 {_topDownHalfHeight:F2}";
    }

    private void HandleTopDownNavigation(KeyboardState keyboard, float deltaTime)
    {
        System.Numerics.Vector2 pan = System.Numerics.Vector2.Zero;
        if (keyboard.IsKeyDown(Keys.W))
        {
            pan.Y -= 1.0f;
        }

        if (keyboard.IsKeyDown(Keys.S))
        {
            pan.Y += 1.0f;
        }

        if (keyboard.IsKeyDown(Keys.A))
        {
            pan.X -= 1.0f;
        }

        if (keyboard.IsKeyDown(Keys.D))
        {
            pan.X += 1.0f;
        }

        if (pan.LengthSquared() <= 1e-6f)
        {
            return;
        }

        pan = System.Numerics.Vector2.Normalize(pan);
        float panSpeed = Math.Max(1.0f, _topDownHalfHeight * 1.8f) * deltaTime;
        _topDownCenter += pan * panSpeed;
    }

    private void HandleTopDownHotkeys(KeyboardState keyboard)
    {
        if (keyboard.IsKeyPressed(Keys.Enter) && _facilityDrawMode == FacilityDrawMode.Polygon)
        {
            CommitFacilityPolygon();
        }

        if (keyboard.IsKeyPressed(Keys.Tab))
        {
            CycleFacilityDrawMode();
        }

        if (keyboard.IsKeyPressed(Keys.Backspace) && _facilityDrawMode == FacilityDrawMode.Polygon && _facilityPolygonPoints.Count > 0)
        {
            _facilityPolygonPoints.RemoveAt(_facilityPolygonPoints.Count - 1);
            _statusMessage = $"已移除最后一个多边形点，剩余 {_facilityPolygonPoints.Count} 个点。";
        }

        if (keyboard.IsKeyPressed(Keys.Escape))
        {
            CancelTopDownDraft();
        }

        if (keyboard.IsKeyPressed(Keys.Delete))
        {
            DeleteSelectedFacility();
        }
    }

    private void HandleTopDownPointer(System.Numerics.Vector2 mousePosition, MouseState mouse)
    {
        if (!TryGetTopDownGroundPoint(mousePosition, out System.Numerics.Vector2 worldPoint))
        {
            return;
        }

        if (_facilityDrawMode is FacilityDrawMode.Rect or FacilityDrawMode.Line)
        {
            if (mouse.IsButtonPressed(MouseButton.Left))
            {
                _facilityRectStartWorld = worldPoint;
                _facilityRectCurrentWorld = worldPoint;
                _topDownDraggingRect = true;
                return;
            }

            if (_topDownDraggingRect && mouse.IsButtonDown(MouseButton.Left))
            {
                _facilityRectCurrentWorld = worldPoint;
                return;
            }

            if (_topDownDraggingRect && mouse.IsButtonReleased(MouseButton.Left))
            {
                _facilityRectCurrentWorld = worldPoint;
                if (_facilityDrawMode == FacilityDrawMode.Line)
                {
                    CommitFacilityLine();
                }
                else
                {
                    CommitFacilityRect();
                }
            }

            return;
        }

        if (_facilityDrawMode == FacilityDrawMode.Polygon)
        {
            if (mouse.IsButtonPressed(MouseButton.Left))
            {
                if (_facilityPolygonPoints.Count == 0 || System.Numerics.Vector2.DistanceSquared(_facilityPolygonPoints[^1], worldPoint) > 1e-4f)
                {
                    _facilityPolygonPoints.Add(worldPoint);
                    _statusMessage = $"多边形设施绘制中，已记录 {_facilityPolygonPoints.Count} 个点。";
                }
            }

            if (mouse.IsButtonPressed(MouseButton.Right))
            {
                CommitFacilityPolygon();
            }

            return;
        }

        if (_topDownDraggingSelection && _selectedFacilityDragLastWorld is System.Numerics.Vector2 previousPoint)
        {
            if (mouse.IsButtonDown(MouseButton.Left))
            {
                MoveSelectedFacility(worldPoint - previousPoint);
                _selectedFacilityDragLastWorld = worldPoint;
                return;
            }

            if (mouse.IsButtonReleased(MouseButton.Left))
            {
                _topDownDraggingSelection = false;
                _selectedFacilityDragLastWorld = null;
                return;
            }
        }

        if (mouse.IsButtonPressed(MouseButton.Left))
        {
            _selectedFacilityIndex = HitTestFacility(worldPoint);
            if (_selectedFacilityIndex >= 0)
            {
                _topDownDraggingSelection = true;
                _selectedFacilityDragLastWorld = worldPoint;
                _statusMessage = $"已选中设施 {GetSelectedFacility()?.Id}";
            }
            else
            {
                _topDownDraggingSelection = false;
                _selectedFacilityDragLastWorld = null;
                _statusMessage = "当前俯视位置未命中设施。";
            }
        }
    }

    private bool TryGetTopDownGroundPoint(System.Numerics.Vector2 screenPoint, out System.Numerics.Vector2 worldPoint)
    {
        PickRay ray = BuildRayFromScreenPosition(new Vector2(screenPoint.X, screenPoint.Y));
        if (Math.Abs(ray.Direction.Y) <= 1e-6f)
        {
            worldPoint = default;
            return false;
        }

        float t = -ray.Origin.Y / ray.Direction.Y;
        if (t < 0.0f)
        {
            worldPoint = default;
            return false;
        }

        System.Numerics.Vector3 hit = ray.Origin + ray.Direction * t;
        worldPoint = new System.Numerics.Vector2(hit.X, hit.Z);
        return true;
    }

    private FacilityRegionEditorModel? GetSelectedFacility()
    {
        if (_mapEditingSession is null)
        {
            return null;
        }

        return _selectedFacilityIndex >= 0 && _selectedFacilityIndex < _mapEditingSession.Document.Facilities.Count
            ? _mapEditingSession.Document.Facilities[_selectedFacilityIndex]
            : null;
    }

    private void CommitFacilityRect()
    {
        try
        {
            if (_mapEditingSession is null || _facilityRectStartWorld is not System.Numerics.Vector2 start || _facilityRectCurrentWorld is not System.Numerics.Vector2 end)
            {
                return;
            }

            if (Math.Abs(end.X - start.X) < 1e-3f || Math.Abs(end.Y - start.Y) < 1e-3f)
            {
                _statusMessage = "矩形设施尺寸过小，已忽略。";
                return;
            }

            System.Numerics.Vector2 startMap = MapTopDownWorldToFacilityPoint(start);
            System.Numerics.Vector2 endMap = MapTopDownWorldToFacilityPoint(end);
            var facility = new FacilityRegionEditorModel
            {
                Id = BuildNextFacilityId(),
                Type = _facilityDraftType,
                Team = _facilityDraftTeam,
                Shape = "rect",
                X1 = Math.Min(startMap.X, endMap.X),
                Y1 = Math.Min(startMap.Y, endMap.Y),
                X2 = Math.Max(startMap.X, endMap.X),
                Y2 = Math.Max(startMap.Y, endMap.Y),
                Thickness = _facilityDraftThickness,
                HeightM = _facilityDraftHeightM,
            };
            _mapEditingSession.Document.Facilities.Add(facility);
            _selectedFacilityIndex = _mapEditingSession.Document.Facilities.Count - 1;
            _statusMessage = $"已新增矩形设施 {facility.Id}";
        }
        finally
        {
            _topDownDraggingRect = false;
            _facilityRectStartWorld = null;
            _facilityRectCurrentWorld = null;
        }
    }

    private void CommitFacilityLine()
    {
        try
        {
            if (_mapEditingSession is null || _facilityRectStartWorld is not System.Numerics.Vector2 start || _facilityRectCurrentWorld is not System.Numerics.Vector2 end)
            {
                return;
            }

            if (System.Numerics.Vector2.DistanceSquared(start, end) < 1e-5f)
            {
                _statusMessage = "线段设施过短，已忽略。";
                return;
            }

            System.Numerics.Vector2 startMap = MapTopDownWorldToFacilityPoint(start);
            System.Numerics.Vector2 endMap = MapTopDownWorldToFacilityPoint(end);
            var facility = new FacilityRegionEditorModel
            {
                Id = BuildNextFacilityId(),
                Type = _facilityDraftType,
                Team = _facilityDraftTeam,
                Shape = "line",
                X1 = startMap.X,
                Y1 = startMap.Y,
                X2 = endMap.X,
                Y2 = endMap.Y,
                Thickness = _facilityDraftThickness,
                HeightM = _facilityDraftHeightM,
            };
            _mapEditingSession.Document.Facilities.Add(facility);
            _selectedFacilityIndex = _mapEditingSession.Document.Facilities.Count - 1;
            _statusMessage = $"已新增线段设施 {facility.Id}";
        }
        finally
        {
            _topDownDraggingRect = false;
            _facilityRectStartWorld = null;
            _facilityRectCurrentWorld = null;
        }
    }

    private void CommitFacilityPolygon()
    {
        if (_mapEditingSession is null)
        {
            _statusMessage = "当前没有可保存的地图文档。";
            return;
        }

        if (_facilityPolygonPoints.Count < 3)
        {
            _statusMessage = "多边形设施至少需要 3 个点。";
            return;
        }

        IReadOnlyList<System.Numerics.Vector2> polygonMapPoints = _facilityPolygonPoints
            .Select(MapTopDownWorldToFacilityPoint)
            .ToArray();
        string pointsText = string.Join("; ", polygonMapPoints.Select(point =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}")));
        var facility = new FacilityRegionEditorModel
        {
            Id = BuildNextFacilityId(),
            Type = _facilityDraftType,
            Team = _facilityDraftTeam,
            Shape = "polygon",
            X1 = polygonMapPoints.Min(point => point.X),
            Y1 = polygonMapPoints.Min(point => point.Y),
            X2 = polygonMapPoints.Max(point => point.X),
            Y2 = polygonMapPoints.Max(point => point.Y),
            Thickness = _facilityDraftThickness,
            HeightM = _facilityDraftHeightM,
            PointsText = pointsText,
        };
        _mapEditingSession.Document.Facilities.Add(facility);
        _selectedFacilityIndex = _mapEditingSession.Document.Facilities.Count - 1;
        _facilityPolygonPoints.Clear();
        _statusMessage = $"已新增多边形设施 {facility.Id}";
    }

    private void DeleteSelectedFacility()
    {
        if (_mapEditingSession is null || _selectedFacilityIndex < 0 || _selectedFacilityIndex >= _mapEditingSession.Document.Facilities.Count)
        {
            return;
        }

        string facilityId = _mapEditingSession.Document.Facilities[_selectedFacilityIndex].Id;
        _mapEditingSession.Document.Facilities.RemoveAt(_selectedFacilityIndex);
        _selectedFacilityIndex = Math.Clamp(_selectedFacilityIndex, -1, _mapEditingSession.Document.Facilities.Count - 1);
        _statusMessage = $"已删除设施 {facilityId}";
    }

    private int HitTestFacility(System.Numerics.Vector2 worldPoint)
    {
        if (_mapEditingSession is null)
        {
            return -1;
        }

        for (int index = _mapEditingSession.Document.Facilities.Count - 1; index >= 0; index--)
        {
            FacilityRegionEditorModel facility = _mapEditingSession.Document.Facilities[index];
            if (ContainsFacility(facility, worldPoint))
            {
                return index;
            }
        }

        return -1;
    }

    private bool ContainsFacility(FacilityRegionEditorModel facility, System.Numerics.Vector2 worldPoint)
    {
        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            System.Numerics.Vector2 start = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1));
            System.Numerics.Vector2 end = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2));
            float hitRadius = MathF.Max(0.05f, ResolveFacilityThicknessWorld((float)facility.Thickness) * 0.5f);
            return DistancePointToSegment(worldPoint, start, end) <= hitRadius;
        }

        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<Point2D> points = facility.ParsePoints();
            if (points.Count >= 3)
            {
                bool inside = false;
                System.Numerics.Vector2 previous = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)points[^1].X, (float)points[^1].Y));
                foreach (Point2D current in points)
                {
                    System.Numerics.Vector2 currentWorld = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)current.X, (float)current.Y));
                    bool intersects =
                        ((currentWorld.Y > worldPoint.Y) != (previous.Y > worldPoint.Y))
                        && (worldPoint.X < (previous.X - currentWorld.X) * (worldPoint.Y - currentWorld.Y) / Math.Max(previous.Y - currentWorld.Y, 1e-9f) + currentWorld.X);
                    if (intersects)
                    {
                        inside = !inside;
                    }

                    previous = currentWorld;
                }

                return inside;
            }
        }

        System.Numerics.Vector2 startWorld = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1));
        System.Numerics.Vector2 endWorld = MapFacilityPointToTopDownWorld(new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2));
        double minX = Math.Min(startWorld.X, endWorld.X);
        double maxX = Math.Max(startWorld.X, endWorld.X);
        double minY = Math.Min(startWorld.Y, endWorld.Y);
        double maxY = Math.Max(startWorld.Y, endWorld.Y);
        return worldPoint.X >= minX && worldPoint.X <= maxX && worldPoint.Y >= minY && worldPoint.Y <= maxY;
    }

    private void MoveSelectedFacility(System.Numerics.Vector2 delta)
    {
        FacilityRegionEditorModel? facility = GetSelectedFacility();
        if (facility is null)
        {
            return;
        }

        System.Numerics.Vector2 anchorWorld = GetFacilityAnchorPointWorld(facility);
        System.Numerics.Vector2 anchorMap = GetFacilityAnchorPoint(facility);
        System.Numerics.Vector2 movedAnchorMap = MapTopDownWorldToFacilityPoint(anchorWorld + delta);
        System.Numerics.Vector2 deltaMap = movedAnchorMap - anchorMap;

        facility.X1 += deltaMap.X;
        facility.Y1 += deltaMap.Y;
        facility.X2 += deltaMap.X;
        facility.Y2 += deltaMap.Y;

        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<Point2D> movedPoints = facility.ParsePoints()
                .Select(point => new Point2D(point.X + deltaMap.X, point.Y + deltaMap.Y))
                .ToArray();
            facility.PointsText = string.Join("; ", movedPoints.Select(point =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}")));
        }

        _statusMessage = $"正在平移设施 {facility.Id}";
    }

    private void SaveMapDocument()
    {
        if (_mapEditingSession is null)
        {
            return;
        }

        bool renormalize = _mapEditingSession.Document.FacilitiesNormalizedToTopDownWorld;
        try
        {
            if (renormalize)
            {
                DenormalizeFacilitiesFromTopDownWorld();
            }

            _mapEditingSession.SaveMapDocument();
        }
        finally
        {
            if (renormalize)
            {
                NormalizeFacilitiesToTopDownWorld();
            }
        }
        _statusMessage = $"已保存 {_mapEditingSession.PresetName}\\map.json";
    }

    private void CancelTopDownDraft()
    {
        _topDownDraggingRect = false;
        _topDownDraggingSelection = false;
        _facilityRectStartWorld = null;
        _facilityRectCurrentWorld = null;
        _selectedFacilityDragLastWorld = null;
        _facilityPolygonPoints.Clear();
        _statusMessage = "已取消当前俯视编辑操作。";
    }

    private void CycleFacilityDrawMode()
    {
        int nextMode = ((int)_facilityDrawMode + 1) % Enum.GetValues<FacilityDrawMode>().Length;
        _facilityDrawMode = (FacilityDrawMode)nextMode;
        if (_facilityDrawMode is not FacilityDrawMode.Polygon)
        {
            _facilityPolygonPoints.Clear();
        }

        if (_facilityDrawMode is not (FacilityDrawMode.Rect or FacilityDrawMode.Line))
        {
            _topDownDraggingRect = false;
            _facilityRectStartWorld = null;
            _facilityRectCurrentWorld = null;
        }

        _statusMessage = $"俯视绘制模式已切换为 {GetFacilityDrawModeLabel(_facilityDrawMode)}";
    }

    private static string GetFacilityDrawModeLabel(FacilityDrawMode mode)
    {
        return mode switch
        {
            FacilityDrawMode.Select => "选择",
            FacilityDrawMode.Rect => "矩形",
            FacilityDrawMode.Line => "线段",
            FacilityDrawMode.Polygon => "多边形",
            _ => "未知",
        };
    }

    private static System.Numerics.Vector2 GetFacilityAnchorPoint(FacilityRegionEditorModel facility)
    {
        if (string.Equals(facility.Shape, "line", StringComparison.OrdinalIgnoreCase))
        {
            return new System.Numerics.Vector2(
                (float)((facility.X1 + facility.X2) * 0.5),
                (float)((facility.Y1 + facility.Y2) * 0.5));
        }

        if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<Point2D> points = facility.ParsePoints();
            if (points.Count > 0)
            {
                return new System.Numerics.Vector2(
                    (float)points.Average(point => point.X),
                    (float)points.Average(point => point.Y));
            }
        }

        return new System.Numerics.Vector2(
            (float)((facility.X1 + facility.X2) * 0.5),
            (float)((facility.Y1 + facility.Y2) * 0.5));
    }

    private System.Numerics.Vector2 GetFacilityAnchorPointWorld(FacilityRegionEditorModel facility)
    {
        return MapFacilityPointToTopDownWorld(GetFacilityAnchorPoint(facility));
    }

    private bool UseFacilityMapCoordinates()
    {
        if (_mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document)
        {
            return false;
        }

        if (document.FacilitiesNormalizedToTopDownWorld)
        {
            return false;
        }

        return HasFacilityPixelCoordinateSource(document);
    }

    private static bool HasFacilityPixelCoordinateSource(MapPresetEditorSettings document)
    {
        string? unit = document.RawMap?["coordinate_system"]?["unit"]?.ToString()
            ?? document.RawMap?["unit"]?.ToString();
        return string.Equals(unit, "px", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unit, "pixel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unit, "pixels", StringComparison.OrdinalIgnoreCase);
    }

    private System.Numerics.Vector2 MapFacilityPointToTopDownWorld(System.Numerics.Vector2 facilityPoint)
    {
        if (!UseFacilityMapCoordinates() || _mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document)
        {
            return facilityPoint;
        }

        return MapFacilityPointToTopDownWorldRaw(document, facilityPoint);
    }

    private System.Numerics.Vector2 MapFacilityPointToTopDownWorldRaw(MapPresetEditorSettings document, System.Numerics.Vector2 facilityPoint)
    {
        float metersPerMapX = (float)(document.FieldLengthM / Math.Max(1, document.Width));
        float metersPerMapY = (float)(document.FieldWidthM / Math.Max(1, document.Height));
        System.Numerics.Vector3 model = _worldScale.MetersToModel(new System.Numerics.Vector3(
            (document.Width * 0.5f - facilityPoint.X) * metersPerMapX,
            0f,
            (document.Height * 0.5f - facilityPoint.Y) * metersPerMapY));
        return new System.Numerics.Vector2(model.X, model.Z);
    }

    private System.Numerics.Vector2 MapTopDownWorldToFacilityPoint(System.Numerics.Vector2 worldPoint)
    {
        if (!UseFacilityMapCoordinates() || _mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document)
        {
            return worldPoint;
        }

        return MapTopDownWorldToFacilityPointRaw(document, worldPoint);
    }

    private System.Numerics.Vector2 MapTopDownWorldToFacilityPointRaw(MapPresetEditorSettings document, System.Numerics.Vector2 worldPoint)
    {
        System.Numerics.Vector3 meters = _worldScale.ModelToMeters(new System.Numerics.Vector3(worldPoint.X, _scene.Bounds.Center.Y, worldPoint.Y));
        float mapUnitsPerMeterX = document.Width / (float)Math.Max(document.FieldLengthM, 1e-6);
        float mapUnitsPerMeterY = document.Height / (float)Math.Max(document.FieldWidthM, 1e-6);
        return new System.Numerics.Vector2(
            document.Width * 0.5f - meters.X * mapUnitsPerMeterX,
            document.Height * 0.5f - meters.Z * mapUnitsPerMeterY);
    }

    private float ResolveFacilityThicknessWorld(float thickness)
    {
        if (!UseFacilityMapCoordinates() || _mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document)
        {
            return thickness;
        }

        return ResolveFacilityThicknessWorldRaw(document, thickness);
    }

    private float ResolveFacilityThicknessWorldRaw(MapPresetEditorSettings document, float thickness)
    {
        float metersPerMapX = (float)(document.FieldLengthM / Math.Max(1, document.Width));
        float metersPerMapY = (float)(document.FieldWidthM / Math.Max(1, document.Height));
        float meters = thickness * ((metersPerMapX + metersPerMapY) * 0.5f);
        float metersPerModelUnit = Math.Max((_worldScale.XMetersPerUnit + _worldScale.ZMetersPerUnit) * 0.5f, 1e-6f);
        return meters / metersPerModelUnit;
    }

    private float ResolveFacilityThicknessMapRaw(MapPresetEditorSettings document, float worldThickness)
    {
        float metersPerMapX = (float)(document.FieldLengthM / Math.Max(1, document.Width));
        float metersPerMapY = (float)(document.FieldWidthM / Math.Max(1, document.Height));
        float metersPerModelUnit = Math.Max((_worldScale.XMetersPerUnit + _worldScale.ZMetersPerUnit) * 0.5f, 1e-6f);
        float mapMeters = Math.Max((metersPerMapX + metersPerMapY) * 0.5f, 1e-6f);
        return worldThickness * metersPerModelUnit / mapMeters;
    }

    private void NormalizeFacilitiesToTopDownWorld()
    {
        if (_mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document
            || document.FacilitiesNormalizedToTopDownWorld
            || !HasFacilityPixelCoordinateSource(document))
        {
            return;
        }

        foreach (FacilityRegionEditorModel facility in document.Facilities)
        {
            System.Numerics.Vector2 startWorld = MapFacilityPointToTopDownWorldRaw(document, new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1));
            System.Numerics.Vector2 endWorld = MapFacilityPointToTopDownWorldRaw(document, new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2));
            facility.X1 = startWorld.X;
            facility.Y1 = startWorld.Y;
            facility.X2 = endWorld.X;
            facility.Y2 = endWorld.Y;
            facility.Thickness = ResolveFacilityThicknessWorldRaw(document, (float)facility.Thickness);
            if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<Point2D> normalizedPoints = facility.ParsePoints()
                    .Select(point => MapFacilityPointToTopDownWorldRaw(document, new System.Numerics.Vector2((float)point.X, (float)point.Y)))
                    .Select(point => new Point2D(point.X, point.Y))
                    .ToArray();
                facility.PointsText = string.Join("; ", normalizedPoints.Select(point =>
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}")));
            }
        }

        document.FacilitiesNormalizedToTopDownWorld = true;
    }

    private void DenormalizeFacilitiesFromTopDownWorld()
    {
        if (_mapEditingSession?.Document is not { Width: > 0, Height: > 0 } document
            || !document.FacilitiesNormalizedToTopDownWorld
            || !HasFacilityPixelCoordinateSource(document))
        {
            return;
        }

        foreach (FacilityRegionEditorModel facility in document.Facilities)
        {
            System.Numerics.Vector2 startMap = MapTopDownWorldToFacilityPointRaw(document, new System.Numerics.Vector2((float)facility.X1, (float)facility.Y1));
            System.Numerics.Vector2 endMap = MapTopDownWorldToFacilityPointRaw(document, new System.Numerics.Vector2((float)facility.X2, (float)facility.Y2));
            facility.X1 = startMap.X;
            facility.Y1 = startMap.Y;
            facility.X2 = endMap.X;
            facility.Y2 = endMap.Y;
            facility.Thickness = ResolveFacilityThicknessMapRaw(document, (float)facility.Thickness);
            if (string.Equals(facility.Shape, "polygon", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<Point2D> mapPoints = facility.ParsePoints()
                    .Select(point => MapTopDownWorldToFacilityPointRaw(document, new System.Numerics.Vector2((float)point.X, (float)point.Y)))
                    .Select(point => new Point2D(point.X, point.Y))
                    .ToArray();
                facility.PointsText = string.Join("; ", mapPoints.Select(point =>
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{point.X:0.###},{point.Y:0.###}")));
            }
        }

        document.FacilitiesNormalizedToTopDownWorld = false;
    }

    private string BuildNextFacilityId()
    {
        string baseId = string.IsNullOrWhiteSpace(_facilityDraftBaseId) ? _facilityDraftType : _facilityDraftBaseId.Trim();
        if (_mapEditingSession is null)
        {
            return baseId;
        }

        string candidate = baseId;
        int suffix = 1;
        HashSet<string> existing = _mapEditingSession.Document.Facilities
            .Select(facility => facility.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (existing.Contains(candidate))
        {
            suffix++;
            candidate = $"{baseId}_{suffix}";
        }

        return candidate;
    }

    private void ConfigureMatchLikeCamera()
    {
        _viewMode = ViewMode.Free;

        System.Numerics.Vector3 focusPoint = _selectedCompositeId is int selectedCompositeId
            ? _composites.FirstOrDefault(item => item.Id == selectedCompositeId)?.PivotModel ?? _scene.Bounds.Center
            : _scene.Bounds.Center;

        float extent = MathF.Max(4.5f, MathF.Max(_scene.Bounds.Size.X, _scene.Bounds.Size.Z) * 0.18f);
        _camera.YawDegrees = -136.0f;
        _camera.PitchDegrees = -18.0f;

        Vector3 focus = new(
            focusPoint.X,
            MathF.Max(_scene.Bounds.Center.Y + 1.2f, focusPoint.Y + 0.9f),
            focusPoint.Z);
        Vector3 forward = _camera.Forward;
        if (forward.LengthSquared <= 1e-6f)
        {
            forward = new Vector3(-0.62f, -0.31f, -0.72f);
        }

        _camera.Position = focus - forward.Normalized() * (extent + 6.5f);
    }

    private (Matrix4 View, Matrix4 Projection, Matrix4 ViewProjection) BuildViewProjection()
    {
        var aspect = ClientSize.Y == 0 ? 1.0f : ClientSize.X / (float)ClientSize.Y;
        if (_viewMode == ViewMode.TopDown)
        {
            float halfHeight = Math.Max(0.5f, _topDownHalfHeight);
            float halfWidth = halfHeight * aspect;
            Vector3 eye = new(_topDownCenter.X, _scene.Bounds.Max.Y + Math.Max(20.0f, _scene.Bounds.Size.Y + 10.0f), _topDownCenter.Y);
            Vector3 target = new(_topDownCenter.X, 0.0f, _topDownCenter.Y);
            var viewTop = Matrix4.LookAt(eye, target, -Vector3.UnitZ);
            var projectionTop = Matrix4.CreateOrthographicOffCenter(-halfWidth, halfWidth, -halfHeight, halfHeight, 0.1f, _farPlane);
            return (viewTop, projectionTop, viewTop * projectionTop);
        }

        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(65.0f), aspect, 0.1f, _farPlane);
        var view = _camera.GetViewMatrix();
        return (view, projection, view * projection);
    }

    private PickRay BuildPointerRay()
    {
        return BuildRayFromScreenPosition(new Vector2(ClientSize.X * 0.5f, ClientSize.Y * 0.5f));
    }

    private PickRay BuildRayFromScreenPosition(Vector2 pointer)
    {
        if (_viewMode == ViewMode.TopDown)
        {
            float aspectTop = ClientSize.Y == 0 ? 1.0f : ClientSize.X / (float)ClientSize.Y;
            float ndcXTop = ((2.0f * pointer.X) / Math.Max(ClientSize.X, 1)) - 1.0f;
            float ndcYTop = 1.0f - ((2.0f * pointer.Y) / Math.Max(ClientSize.Y, 1));
            float halfHeight = Math.Max(0.5f, _topDownHalfHeight);
            float halfWidth = halfHeight * aspectTop;
            float worldX = _topDownCenter.X + ndcXTop * halfWidth;
            float worldZ = _topDownCenter.Y + ndcYTop * halfHeight;
            float startY = _scene.Bounds.Max.Y + Math.Max(20.0f, _scene.Bounds.Size.Y + 10.0f);
            return new PickRay(
                new System.Numerics.Vector3(worldX, startY, worldZ),
                new System.Numerics.Vector3(0.0f, -1.0f, 0.0f));
        }

        var ndcX = ((2.0f * pointer.X) / Math.Max(ClientSize.X, 1)) - 1.0f;
        var ndcY = 1.0f - ((2.0f * pointer.Y) / Math.Max(ClientSize.Y, 1));
        var aspect = ClientSize.Y == 0 ? 1.0f : ClientSize.X / (float)ClientSize.Y;
        var tanHalfFov = MathF.Tan(MathHelper.DegreesToRadians(65.0f) * 0.5f);

        var up = Vector3.Normalize(Vector3.Cross(_camera.Right, _camera.Forward));
        var direction = _camera.Forward +
                        (_camera.Right * ndcX * tanHalfFov * aspect) +
                        (up * ndcY * tanHalfFov);

        return new PickRay(ToNumerics(_camera.Position), ToNumerics(direction.Normalized()));
    }

    private bool TryProjectToScreen(System.Numerics.Vector3 worldPosition, Matrix4 viewProjection, out System.Numerics.Vector2 screen)
    {
        var clip = Vector4.TransformRow(new Vector4(worldPosition.X, worldPosition.Y, worldPosition.Z, 1.0f), viewProjection);
        if (clip.W <= 0.0001f)
        {
            screen = default;
            return false;
        }

        var ndc = clip.Xyz / clip.W;
        screen = new System.Numerics.Vector2(
            (ndc.X * 0.5f + 0.5f) * ClientSize.X,
            (1.0f - (ndc.Y * 0.5f + 0.5f)) * ClientSize.Y);
        return true;
    }

    private static float DistancePointToSegment(System.Numerics.Vector2 point, System.Numerics.Vector2 start, System.Numerics.Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= 0.0001f)
        {
            return (point - start).Length();
        }

        var t = Math.Clamp(System.Numerics.Vector2.Dot(point - start, segment) / lengthSquared, 0.0f, 1.0f);
        var projection = start + segment * t;
        return (point - projection).Length();
    }

    private static System.Numerics.Vector3 GetAxisVector(GizmoAxis axis)
    {
        return axis switch
        {
            GizmoAxis.X => System.Numerics.Vector3.UnitX,
            GizmoAxis.Y => System.Numerics.Vector3.UnitY,
            GizmoAxis.Z => System.Numerics.Vector3.UnitZ,
            _ => System.Numerics.Vector3.Zero,
        };
    }

    private System.Numerics.Vector3 GetCompositeAxisVector(CompositeObject composite, GizmoAxis axis)
    {
        if (composite.CoordinateSystemMode != CoordinateSystemMode.Custom)
        {
            return GetAxisVector(axis);
        }

        var (xAxis, yAxis, zAxis) = GetOrthonormalCoordinateAxes(composite);
        return axis switch
        {
            GizmoAxis.X => xAxis,
            GizmoAxis.Y => yAxis,
            GizmoAxis.Z => zAxis,
            _ => System.Numerics.Vector3.Zero,
        };
    }

    private static System.Numerics.Matrix4x4 GetCompositeRotationMatrix(CompositeObject composite)
    {
        var yaw = MathF.PI / 180.0f * composite.RotationYprDegrees.X;
        var pitch = MathF.PI / 180.0f * composite.RotationYprDegrees.Y;
        var roll = MathF.PI / 180.0f * composite.RotationYprDegrees.Z;
        return System.Numerics.Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
    }

    private static System.Numerics.Matrix4x4 GetCoordinateSystemRotationMatrix(CompositeObject composite)
    {
        var yaw = MathF.PI / 180.0f * composite.CoordinateYprDegrees.X;
        var pitch = MathF.PI / 180.0f * composite.CoordinateYprDegrees.Y;
        var roll = MathF.PI / 180.0f * composite.CoordinateYprDegrees.Z;
        return System.Numerics.Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
    }

    private static System.Numerics.Vector3 MatrixToYprDegrees(System.Numerics.Matrix4x4 rotationMatrix)
    {
        var rotation = System.Numerics.Quaternion.CreateFromRotationMatrix(rotationMatrix);
        rotation = System.Numerics.Quaternion.Normalize(rotation);

        var sinPitch = 2.0f * ((rotation.W * rotation.X) - (rotation.Y * rotation.Z));
        sinPitch = Math.Clamp(sinPitch, -1.0f, 1.0f);
        var pitch = MathF.Asin(sinPitch);
        var yaw = MathF.Atan2(
            2.0f * ((rotation.W * rotation.Y) + (rotation.X * rotation.Z)),
            1.0f - (2.0f * ((rotation.X * rotation.X) + (rotation.Y * rotation.Y))));
        var roll = MathF.Atan2(
            2.0f * ((rotation.W * rotation.Z) + (rotation.X * rotation.Y)),
            1.0f - (2.0f * ((rotation.X * rotation.X) + (rotation.Z * rotation.Z))));

        const float radiansToDegrees = 180.0f / MathF.PI;
        return new System.Numerics.Vector3(
            yaw * radiansToDegrees,
            pitch * radiansToDegrees,
            roll * radiansToDegrees);
    }

    private static (
        System.Numerics.Vector3 XAxis,
        System.Numerics.Vector3 YAxis,
        System.Numerics.Vector3 ZAxis) GetOrthonormalCoordinateAxes(CompositeObject composite)
    {
        var rotation = GetCoordinateSystemRotationMatrix(composite);
        var xAxis = SafeNormalize(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX, rotation), System.Numerics.Vector3.UnitX);
        var yCandidate = SafeNormalize(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitY, rotation), System.Numerics.Vector3.UnitY);
        var zAxis = SafeNormalize(System.Numerics.Vector3.Cross(xAxis, yCandidate), System.Numerics.Vector3.UnitZ);
        var yAxis = SafeNormalize(System.Numerics.Vector3.Cross(zAxis, xAxis), System.Numerics.Vector3.UnitY);
        return (xAxis, yAxis, zAxis);
    }

    private static System.Numerics.Vector3 SafeNormalize(System.Numerics.Vector3 value, System.Numerics.Vector3 fallback)
    {
        return value.LengthSquared() <= 1e-8f
            ? fallback
            : System.Numerics.Vector3.Normalize(value);
    }

    private static string AxisToChinese(GizmoAxis axis)
    {
        return axis switch
        {
            GizmoAxis.X => "X",
            GizmoAxis.Y => "Y",
            GizmoAxis.Z => "Z",
            _ => "无",
        };
    }

    private void SetCursorCaptured(bool captured)
    {
        if (_cursorCaptured == captured)
        {
            return;
        }

        _cursorCaptured = captured;
        CursorState = captured ? CursorState.Grabbed : CursorState.Normal;
    }

    private void PushUndoSnapshot()
    {
        _undoStack.Push(CaptureSnapshot());
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            _statusMessage = "没有可以撤销的操作";
            return;
        }

        _redoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_undoStack.Pop());
        _statusMessage = "已撤销";
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
        {
            _statusMessage = "没有可以重做的操作";
            return;
        }

        _undoStack.Push(CaptureSnapshot());
        RestoreSnapshot(_redoStack.Pop());
        _statusMessage = "已重做";
    }

    private EditorSnapshot CaptureSnapshot()
    {
        return new EditorSnapshot
        {
            NextCompositeId = _nextCompositeId,
            SelectedComponentId = _selectedComponentId,
            SelectedComponentIds = _selectedComponentIds.ToHashSet(),
            SelectedCompositeId = _selectedCompositeId,
            FocusedCompositeId = _focusedCompositeId,
            SelectedInteractionUnitCompositeId = _selectedInteractionUnitCompositeId,
            SelectedInteractionUnitId = _selectedInteractionUnitId,
            FocusedInteractionUnitCompositeId = _focusedInteractionUnitCompositeId,
            FocusedInteractionUnitId = _focusedInteractionUnitId,
            CurrentExportPath = _currentExportPath,
            ManualActorComponentIds = _manualActorComponentIds.ToHashSet(),
            ComponentColorOverrides = _componentColorOverrides.ToDictionary(pair => pair.Key, pair => pair.Value),
            Composites = _composites.Select(composite => new CompositeState
            {
                Id = composite.Id,
                Name = composite.Name,
                IsActor = composite.IsActor,
                NextInteractionUnitId = composite.NextInteractionUnitId,
                PivotModel = composite.PivotModel,
                PositionModel = composite.PositionModel,
                RotationYprDegrees = composite.RotationYprDegrees,
                CoordinateYprDegrees = composite.CoordinateYprDegrees,
                CoordinateSystemMode = composite.CoordinateSystemMode,
                ComponentIds = composite.ComponentIds.ToArray(),
                InteractionUnits = composite.InteractionUnits.Select(unit => new InteractionUnitState
                {
                    Id = unit.Id,
                    Name = unit.Name,
                    ComponentIds = unit.ComponentIds.ToArray(),
                }).ToList(),
            }).ToList(),
        };
    }

    private void RestoreSnapshot(EditorSnapshot snapshot)
    {
        _nextCompositeId = snapshot.NextCompositeId;
        _selectedComponentId = snapshot.SelectedComponentId;
        _selectedComponentIds.Clear();
        foreach (var componentId in snapshot.SelectedComponentIds)
        {
            _selectedComponentIds.Add(componentId);
        }

        _selectedCompositeId = snapshot.SelectedCompositeId;
        _focusedCompositeId = snapshot.FocusedCompositeId;
        _selectedInteractionUnitCompositeId = snapshot.SelectedInteractionUnitCompositeId;
        _selectedInteractionUnitId = snapshot.SelectedInteractionUnitId;
        _focusedInteractionUnitCompositeId = snapshot.FocusedInteractionUnitCompositeId;
        _focusedInteractionUnitId = snapshot.FocusedInteractionUnitId;
        _currentExportPath = snapshot.CurrentExportPath;
        _saveDialogPath = snapshot.CurrentExportPath;

        _manualActorComponentIds.Clear();
        foreach (var componentId in snapshot.ManualActorComponentIds)
        {
            _manualActorComponentIds.Add(componentId);
        }

        _componentColorOverrides.Clear();
        foreach (var (componentId, color) in snapshot.ComponentColorOverrides)
        {
            _componentColorOverrides[componentId] = color;
        }

        _composites.Clear();
        foreach (var state in snapshot.Composites)
        {
            var composite = new CompositeObject
            {
                Id = state.Id,
                Name = state.Name,
                IsActor = state.IsActor,
                NextInteractionUnitId = state.NextInteractionUnitId,
                PivotModel = state.PivotModel,
                PositionModel = state.PositionModel,
                RotationYprDegrees = state.RotationYprDegrees,
                CoordinateYprDegrees = state.CoordinateYprDegrees,
                CoordinateSystemMode = state.CoordinateSystemMode,
            };

            foreach (var componentId in state.ComponentIds)
            {
                composite.ComponentIds.Add(componentId);
            }

            foreach (var interactionUnitState in state.InteractionUnits)
            {
                var interactionUnit = new InteractionUnitObject
                {
                    Id = interactionUnitState.Id,
                    Name = interactionUnitState.Name,
                };

                foreach (var componentId in interactionUnitState.ComponentIds)
                {
                    if (composite.ComponentIds.Contains(componentId))
                    {
                        interactionUnit.ComponentIds.Add(componentId);
                    }
                }

                if (interactionUnit.ComponentIds.Count > 0)
                {
                    composite.InteractionUnits.Add(interactionUnit);
                }
            }

            _composites.Add(composite);
        }

        _componentToCompositeId.Clear();
        foreach (var composite in _composites)
        {
            foreach (var componentId in composite.ComponentIds)
            {
                _componentToCompositeId[componentId] = composite.Id;
            }
        }

        UpdateActorIdsFromComposites();
        RebuildCompositeDrawCaches();
        RebuildStaticIndexBuffers();
    }

    private static bool TryIntersectComponentTriangles(PickRay ray, TerrainChunkData chunk, ComponentRangeData range, out float bestDistance)
    {
        bestDistance = float.PositiveInfinity;
        var hit = false;
        var end = range.StartIndex + range.IndexCount;

        for (var i = range.StartIndex; i + 2 < end; i += 3)
        {
            var v0 = chunk.Vertices[(int)chunk.Indices[i]].Position;
            var v1 = chunk.Vertices[(int)chunk.Indices[i + 1]].Position;
            var v2 = chunk.Vertices[(int)chunk.Indices[i + 2]].Position;

            if (!TryIntersectTriangle(ray, v0, v1, v2, out var distance))
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                hit = true;
            }
        }

        return hit;
    }

    private static bool TryIntersectTriangle(
        PickRay ray,
        System.Numerics.Vector3 v0,
        System.Numerics.Vector3 v1,
        System.Numerics.Vector3 v2,
        out float distance)
    {
        const float epsilon = 1e-7f;
        distance = 0.0f;

        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = System.Numerics.Vector3.Cross(ray.Direction, edge2);
        var a = System.Numerics.Vector3.Dot(edge1, h);
        if (a > -epsilon && a < epsilon)
        {
            return false;
        }

        var f = 1.0f / a;
        var s = ray.Origin - v0;
        var u = f * System.Numerics.Vector3.Dot(s, h);
        if (u < 0.0f || u > 1.0f)
        {
            return false;
        }

        var q = System.Numerics.Vector3.Cross(s, edge1);
        var v = f * System.Numerics.Vector3.Dot(ray.Direction, q);
        if (v < 0.0f || u + v > 1.0f)
        {
            return false;
        }

        var t = f * System.Numerics.Vector3.Dot(edge2, q);
        if (t <= epsilon)
        {
            return false;
        }

        distance = t;
        return true;
    }

    private static bool TryIntersectRayBounds(PickRay ray, BoundingBox bounds, out float distance)
    {
        var min = bounds.Min;
        var max = bounds.Max;
        var tMin = 0.0f;
        var tMax = float.PositiveInfinity;

        if (!UpdateSlab(ray.Origin.X, ray.Direction.X, min.X, max.X, ref tMin, ref tMax) ||
            !UpdateSlab(ray.Origin.Y, ray.Direction.Y, min.Y, max.Y, ref tMin, ref tMax) ||
            !UpdateSlab(ray.Origin.Z, ray.Direction.Z, min.Z, max.Z, ref tMin, ref tMax))
        {
            distance = 0.0f;
            return false;
        }

        distance = tMin;
        return true;
    }

    private static bool UpdateSlab(float origin, float direction, float min, float max, ref float tMin, ref float tMax)
    {
        if (MathF.Abs(direction) < 1e-8f)
        {
            return origin >= min && origin <= max;
        }

        var inv = 1.0f / direction;
        var near = (min - origin) * inv;
        var far = (max - origin) * inv;

        if (near > far)
        {
            (near, far) = (far, near);
        }

        tMin = MathF.Max(tMin, near);
        tMax = MathF.Min(tMax, far);
        return tMin <= tMax;
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value)
    {
        return new System.Numerics.Vector3(value.X, value.Y, value.Z);
    }

    private static System.Numerics.Vector2 ToNumerics(Vector2 value)
    {
        return new System.Numerics.Vector2(value.X, value.Y);
    }

    private static Matrix4 ToOpenTk(System.Numerics.Matrix4x4 value)
    {
        return new Matrix4(
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44);
    }

    private const string VertexShaderSource = """
        #version 330 core

        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec4 aColor;

        uniform mat4 uViewProj;
        uniform mat4 uModel;

        out vec3 vWorldPosition;
        out vec3 vNormal;
        out vec3 vColor;

        void main()
        {
            vec4 worldPosition = uModel * vec4(aPosition, 1.0);
            gl_Position = uViewProj * worldPosition;
            vWorldPosition = worldPosition.xyz;
            vNormal = normalize(mat3(uModel) * aNormal);
            vColor = aColor.rgb;
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core

        in vec3 vWorldPosition;
        in vec3 vNormal;
        in vec3 vColor;

        uniform vec3 uCameraPosition;
        uniform float uFogNear;
        uniform float uFogFar;
        uniform int uRenderMode;
        uniform int uUseComponentOverrideColor;
        uniform vec3 uComponentOverrideColor;

        out vec4 FragColor;

        void main()
        {
            vec3 normal = normalize(vNormal);
            vec3 lightDirection = normalize(vec3(0.35, 0.95, 0.25));
            float diffuse = max(dot(normal, lightDirection), 0.0);
            float ambient = 0.32;
            float lighting = ambient + diffuse * 0.68;
            vec3 baseColor = uUseComponentOverrideColor == 1 ? uComponentOverrideColor : vColor;
            vec3 litColor = baseColor * lighting;

            float distanceToCamera = distance(vWorldPosition, uCameraPosition);
            float fogFactor = smoothstep(uFogNear, uFogFar, distanceToCamera);
            vec3 fogColor = vec3(0.72, 0.82, 0.93);

            vec3 finalColor = mix(litColor, fogColor, fogFactor);
            if (uRenderMode == 1)
            {
                finalColor = mix(finalColor, vec3(1.0, 0.84, 0.12), 0.72);
            }
            else if (uRenderMode == 2)
            {
                finalColor = mix(finalColor, vec3(1.0, 0.32, 0.08), 0.48);
            }
            else if (uRenderMode == 3)
            {
                finalColor = mix(finalColor, vec3(0.1, 0.92, 1.0), 0.62);
            }
            else if (uRenderMode == 4)
            {
                finalColor = mix(vec3(1.0, 0.08, 0.68), fogColor, fogFactor * 0.28);
            }
            else if (uRenderMode == 5)
            {
                finalColor = mix(vec3(0.0, 1.0, 0.28), fogColor, fogFactor * 0.22);
            }

            FragColor = vec4(finalColor, 1.0);
        }
        """;

    private sealed class GpuChunk : IDisposable
    {
        private readonly int _chunkIndex;
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _fullEbo;
        private readonly int _staticEbo;
        private readonly uint[] _sourceIndices;

        private GpuChunk(int chunkIndex, int vao, int vbo, int fullEbo, int staticEbo, uint[] sourceIndices, int indexCount, BoundingBox bounds, TerrainChunkData sourceChunk)
        {
            _chunkIndex = chunkIndex;
            _vao = vao;
            _vbo = vbo;
            _fullEbo = fullEbo;
            _staticEbo = staticEbo;
            _sourceIndices = sourceIndices;
            StaticIndexCount = indexCount;
            Bounds = bounds;
            SourceChunk = sourceChunk;
            ComponentRanges = [];
        }

        public int StaticIndexCount { get; private set; }

        public BoundingBox Bounds { get; }

        public int ChunkIndex => _chunkIndex;

        public TerrainChunkData SourceChunk { get; }

        public ComponentRangeData[] ComponentRanges { get; private init; }

        public static GpuChunk Create(TerrainChunkData data, int chunkIndex)
        {
            var vao = GL.GenVertexArray();
            var vbo = GL.GenBuffer();
            var fullEbo = GL.GenBuffer();
            var staticEbo = GL.GenBuffer();
            var stride = Marshal.SizeOf<VertexData>();

            GL.BindVertexArray(vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Vertices.Length * stride, data.Vertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, fullEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, data.Indices.Length * sizeof(uint), data.Indices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, staticEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, data.Indices.Length * sizeof(uint), data.Indices, BufferUsageHint.StaticDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);

            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 12);

            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 24);

            GL.BindVertexArray(0);

            return new GpuChunk(chunkIndex, vao, vbo, fullEbo, staticEbo, data.Indices, data.Indices.Length, data.Bounds, data)
            {
                ComponentRanges = data.ComponentRanges,
            };
        }

        public void DrawStatic()
        {
            BindStatic();
            GL.DrawElements(PrimitiveType.Triangles, StaticIndexCount, DrawElementsType.UnsignedInt, 0);
        }

        public void DrawRange(ComponentRangeData range)
        {
            BindFull();
            DrawRangeBound(range.StartIndex, range.IndexCount);
        }

        public void BindStatic()
        {
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _staticEbo);
        }

        public void BindFull()
        {
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _fullEbo);
        }

        public void DrawRangeBound(int startIndex, int indexCount)
        {
            GL.DrawElements(PrimitiveType.Triangles, indexCount, DrawElementsType.UnsignedInt, startIndex * sizeof(uint));
        }

        public void RebuildStaticIndexBuffer(IReadOnlySet<int> excludedComponentIds)
        {
            if (excludedComponentIds.Count == 0)
            {
                StaticIndexCount = _sourceIndices.Length;
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _staticEbo);
                GL.BufferData(BufferTarget.ElementArrayBuffer, _sourceIndices.Length * sizeof(uint), _sourceIndices, BufferUsageHint.DynamicDraw);
                return;
            }

            var staticIndices = new List<uint>(_sourceIndices.Length);
            foreach (var range in ComponentRanges)
            {
                if (excludedComponentIds.Contains(range.ComponentId))
                {
                    continue;
                }

                for (var i = range.StartIndex; i < range.StartIndex + range.IndexCount; i++)
                {
                    staticIndices.Add(_sourceIndices[i]);
                }
            }

            StaticIndexCount = staticIndices.Count;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _staticEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, staticIndices.Count * sizeof(uint), staticIndices.ToArray(), BufferUsageHint.DynamicDraw);
        }

        public void Dispose()
        {
            GL.DeleteBuffer(_vbo);
            GL.DeleteBuffer(_fullEbo);
            GL.DeleteBuffer(_staticEbo);
            GL.DeleteVertexArray(_vao);
        }
    }

    private readonly record struct PickRay(System.Numerics.Vector3 Origin, System.Numerics.Vector3 Direction);

    private readonly record struct PickHit(int ComponentId, float Distance, int? CompositeId);

    private readonly record struct ComponentRenderRef(GpuChunk Chunk, ComponentRangeData Range);

    private readonly record struct ComponentDrawBatch(GpuChunk Chunk, int StartIndex, int IndexCount, int ComponentId)
    {
        public static ComponentDrawBatch From(ComponentRenderRef renderRef)
            => new(renderRef.Chunk, renderRef.Range.StartIndex, renderRef.Range.IndexCount, renderRef.Range.ComponentId);
    }

    private readonly struct GizmoScreenData
    {
        public GizmoScreenData(System.Numerics.Vector2 originScreen, System.Numerics.Vector2 xTip, System.Numerics.Vector2 yTip, System.Numerics.Vector2 zTip, float axisLengthModel)
        {
            OriginScreen = originScreen;
            XTip = xTip;
            YTip = yTip;
            ZTip = zTip;
            AxisLengthModel = axisLengthModel;
        }

        public System.Numerics.Vector2 OriginScreen { get; }

        public System.Numerics.Vector2 XTip { get; }

        public System.Numerics.Vector2 YTip { get; }

        public System.Numerics.Vector2 ZTip { get; }

        public float AxisLengthModel { get; }

        public System.Numerics.Vector2 GetTip(GizmoAxis axis)
        {
            return axis switch
            {
                GizmoAxis.X => XTip,
                GizmoAxis.Y => YTip,
                GizmoAxis.Z => ZTip,
                _ => OriginScreen,
            };
        }
    }

    private enum GizmoAxis
    {
        None,
        X,
        Y,
        Z,
    }

    private sealed class EditorSnapshot
    {
        public required int NextCompositeId { get; init; }

        public required int? SelectedComponentId { get; init; }

        public required HashSet<int> SelectedComponentIds { get; init; }

        public required int? SelectedCompositeId { get; init; }

        public required int? FocusedCompositeId { get; init; }

        public required int? SelectedInteractionUnitCompositeId { get; init; }

        public required int? SelectedInteractionUnitId { get; init; }

        public required int? FocusedInteractionUnitCompositeId { get; init; }

        public required int? FocusedInteractionUnitId { get; init; }

        public required string CurrentExportPath { get; init; }

        public required HashSet<int> ManualActorComponentIds { get; init; }

        public required Dictionary<int, System.Numerics.Vector4> ComponentColorOverrides { get; init; }

        public required List<CompositeState> Composites { get; init; }
    }

    private sealed class CompositeState
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required bool IsActor { get; init; }

        public required int NextInteractionUnitId { get; init; }

        public required System.Numerics.Vector3 PivotModel { get; init; }

        public required System.Numerics.Vector3 PositionModel { get; init; }

        public required System.Numerics.Vector3 RotationYprDegrees { get; init; }

        public required System.Numerics.Vector3 CoordinateYprDegrees { get; init; }

        public required CoordinateSystemMode CoordinateSystemMode { get; init; }

        public required int[] ComponentIds { get; init; }

        public required List<InteractionUnitState> InteractionUnits { get; init; }
    }

    private sealed class InteractionUnitState
    {
        public required int Id { get; init; }

        public required string Name { get; init; }

        public required int[] ComponentIds { get; init; }
    }

    private readonly struct Frustum
    {
        private readonly Vector4[] _planes;

        private Frustum(Vector4[] planes)
        {
            _planes = planes;
        }

        public static Frustum FromMatrix(Matrix4 matrix)
        {
            var planes = new[]
            {
                Normalize(new Vector4(matrix.M14 + matrix.M11, matrix.M24 + matrix.M21, matrix.M34 + matrix.M31, matrix.M44 + matrix.M41)),
                Normalize(new Vector4(matrix.M14 - matrix.M11, matrix.M24 - matrix.M21, matrix.M34 - matrix.M31, matrix.M44 - matrix.M41)),
                Normalize(new Vector4(matrix.M14 + matrix.M12, matrix.M24 + matrix.M22, matrix.M34 + matrix.M32, matrix.M44 + matrix.M42)),
                Normalize(new Vector4(matrix.M14 - matrix.M12, matrix.M24 - matrix.M22, matrix.M34 - matrix.M32, matrix.M44 - matrix.M42)),
                Normalize(new Vector4(matrix.M14 + matrix.M13, matrix.M24 + matrix.M23, matrix.M34 + matrix.M33, matrix.M44 + matrix.M43)),
                Normalize(new Vector4(matrix.M14 - matrix.M13, matrix.M24 - matrix.M23, matrix.M34 - matrix.M33, matrix.M44 - matrix.M43)),
            };

            return new Frustum(planes);
        }

        public static Frustum? FromSelectionBox(
            System.Numerics.Vector3 origin,
            System.Numerics.Vector3 forward,
            System.Numerics.Vector3 topLeftDirection,
            System.Numerics.Vector3 topRightDirection,
            System.Numerics.Vector3 bottomLeftDirection,
            System.Numerics.Vector3 bottomRightDirection,
            float farDistance)
        {
            var centerDirection = topLeftDirection + topRightDirection + bottomLeftDirection + bottomRightDirection;
            if (centerDirection.LengthSquared() <= 1e-8f)
            {
                return null;
            }

            centerDirection = System.Numerics.Vector3.Normalize(centerDirection);
            var normalizedForward = forward.LengthSquared() <= 1e-8f
                ? centerDirection
                : System.Numerics.Vector3.Normalize(forward);
            var clampedFarDistance = MathF.Max(farDistance, 1.0f);

            var planes = new[]
            {
                CreatePlaneFromRays(origin, topLeftDirection, bottomLeftDirection, centerDirection),
                CreatePlaneFromRays(origin, bottomRightDirection, topRightDirection, centerDirection),
                CreatePlaneFromRays(origin, topRightDirection, topLeftDirection, centerDirection),
                CreatePlaneFromRays(origin, bottomLeftDirection, bottomRightDirection, centerDirection),
                CreatePlaneFromPointNormal(origin, normalizedForward),
                CreatePlaneFromPointNormal(origin + (normalizedForward * clampedFarDistance), -normalizedForward),
            };

            if (planes.Any(static plane => plane is null))
            {
                return null;
            }

            return new Frustum(planes.Select(static plane => plane!.Value).ToArray());
        }

        public bool Intersects(BoundingBox bounds)
        {
            foreach (var plane in _planes)
            {
                var positive = new System.Numerics.Vector3(
                    plane.X >= 0.0f ? bounds.Max.X : bounds.Min.X,
                    plane.Y >= 0.0f ? bounds.Max.Y : bounds.Min.Y,
                    plane.Z >= 0.0f ? bounds.Max.Z : bounds.Min.Z);

                if ((plane.X * positive.X) + (plane.Y * positive.Y) + (plane.Z * positive.Z) + plane.W < 0.0f)
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector4 Normalize(Vector4 plane)
        {
            var invLength = 1.0f / MathF.Sqrt((plane.X * plane.X) + (plane.Y * plane.Y) + (plane.Z * plane.Z));
            return plane * invLength;
        }

        private static Vector4? CreatePlaneFromRays(
            System.Numerics.Vector3 origin,
            System.Numerics.Vector3 firstDirection,
            System.Numerics.Vector3 secondDirection,
            System.Numerics.Vector3 insideDirection)
        {
            var normal = System.Numerics.Vector3.Cross(firstDirection, secondDirection);
            if (normal.LengthSquared() <= 1e-8f)
            {
                return null;
            }

            normal = System.Numerics.Vector3.Normalize(normal);
            if (System.Numerics.Vector3.Dot(normal, insideDirection) < 0.0f)
            {
                normal = -normal;
            }

            return CreatePlaneFromPointNormal(origin, normal);
        }

        private static Vector4 CreatePlaneFromPointNormal(System.Numerics.Vector3 point, System.Numerics.Vector3 normal)
        {
            var normalized = System.Numerics.Vector3.Normalize(normal);
            return Normalize(new Vector4(
                normalized.X,
                normalized.Y,
                normalized.Z,
                -System.Numerics.Vector3.Dot(normalized, point)));
        }
    }
}
