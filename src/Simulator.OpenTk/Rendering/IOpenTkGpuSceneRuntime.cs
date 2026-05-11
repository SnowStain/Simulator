using System.Drawing;
using Simulator.Runtime.Input;

namespace Simulator.OpenTk.Rendering;

public interface IOpenTkGpuSceneRuntime : IDisposable
{
    bool ExternalRuntimeClosed { get; }

    void ExternalResize(Size clientSize);

    void AttachExternalBorrowedGpuContext();

    void ExternalPrepareInitialPresentation();

    void ExternalAdvanceFrame();

    bool ShouldCaptureMouseExternally();

    bool ExternalRenderToCurrentOpenGlContext();

    void ExternalApplyInput(GameInputSnapshot snapshot);
}
