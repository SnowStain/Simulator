using System.Drawing;
using Simulator.Runtime.Input;

namespace Simulator.ThreeD;

internal interface ISimulatorOpenTkRuntime : IDisposable
{
    bool ExternalRuntimeClosed { get; }

    void ExternalResize(Size clientSize);

    void AttachExternalBorrowedGpuContext();

    void ExternalPrepareInitialPresentation();

    void ExternalAdvanceFrame();

    bool ShouldCaptureMouseExternally();

    bool ExternalRenderToCurrentOpenGlContext();

    void ExternalRender(Graphics graphics);

    void ExternalApplyInput(GameInputSnapshot snapshot);
}

internal static class SimulatorOpenTkRuntimeFactory
{
    public static ISimulatorOpenTkRuntime CreateCompatibilityRuntime(Simulator3dOptions options)
        => Simulator3dForm.CreateExternalCompatibilityRuntime(options);
}

internal sealed partial class Simulator3dForm : ISimulatorOpenTkRuntime
{
    bool ISimulatorOpenTkRuntime.ExternalRuntimeClosed => ExternalRuntimeClosed;

    void ISimulatorOpenTkRuntime.ExternalResize(Size clientSize) => ExternalResize(clientSize);

    void ISimulatorOpenTkRuntime.AttachExternalBorrowedGpuContext() => AttachExternalBorrowedGpuContext();

    void ISimulatorOpenTkRuntime.ExternalPrepareInitialPresentation() => ExternalPrepareInitialPresentation();

    void ISimulatorOpenTkRuntime.ExternalAdvanceFrame() => ExternalAdvanceFrame();

    bool ISimulatorOpenTkRuntime.ShouldCaptureMouseExternally() => ShouldCaptureMouseExternally();

    bool ISimulatorOpenTkRuntime.ExternalRenderToCurrentOpenGlContext() => ExternalRenderToCurrentOpenGlContext();

    void ISimulatorOpenTkRuntime.ExternalRender(Graphics graphics) => ExternalRender(graphics);

    void ISimulatorOpenTkRuntime.ExternalApplyInput(GameInputSnapshot snapshot) => ExternalApplyInput(snapshot);
}
