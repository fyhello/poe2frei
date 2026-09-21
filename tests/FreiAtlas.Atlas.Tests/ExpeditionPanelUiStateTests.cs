using System.Drawing;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Overlay.Native;

namespace FreiAtlas.Atlas.Tests;

public sealed class ExpeditionPanelUiStateTests
{
    [Fact]
    public void BeginDrag_RequiresCtrlLeftButtonInsideTitle()
    {
        var state = new ExpeditionPanelUiState(new Point(100, 200));
        var title = new Rectangle(100, 200, 300, 40);

        Assert.False(state.BeginDrag(new Point(120, 220), leftButton: true, ctrl: false, title));
        Assert.False(state.IsDragging);
        Assert.False(state.BeginDrag(new Point(120, 220), leftButton: false, ctrl: true, title));
        Assert.False(state.BeginDrag(new Point(20, 20), leftButton: true, ctrl: true, title));
        Assert.False(state.IsDragging);
    }

    [Fact]
    public void Drag_UsesScreenDeltaAndStopsWhenCtrlOrButtonIsReleased()
    {
        var state = new ExpeditionPanelUiState(new Point(100, 200));
        var title = new Rectangle(100, 200, 300, 40);

        Assert.True(state.BeginDrag(new Point(120, 220), leftButton: true, ctrl: true, title));
        Assert.True(state.MoveDrag(new Point(360, 480), leftButton: true, ctrl: true));
        Assert.Equal(new Point(340, 460), state.Position);
        Assert.True(state.MoveDrag(new Point(400, 520), leftButton: true, ctrl: false));
        Assert.False(state.IsDragging);
        Assert.Equal(new Point(340, 460), state.Position);
    }

    [Fact]
    public void Drag_ClampsPositionToWorkArea()
    {
        var state = new ExpeditionPanelUiState(new Point(100, 200));
        var title = new Rectangle(100, 200, 300, 40);
        Assert.True(state.BeginDrag(new Point(120, 220), leftButton: true, ctrl: true, title));

        state.MoveDrag(new Point(-100, -100), leftButton: true, ctrl: true);
        state.ClampToWorkArea(new Size(800, 600), new Size(300, 240));
        Assert.Equal(new Point(0, 0), state.Position);

        state.MoveDrag(new Point(1000, 1000), leftButton: true, ctrl: true);
        state.ClampToWorkArea(new Size(800, 600), new Size(300, 240));
        Assert.Equal(new Point(500, 360), state.Position);
    }

    [Fact]
    public void Scroll_ClampsToContentAndViewport()
    {
        var state = new ExpeditionPanelUiState(new Point(0, 0));

        Assert.Equal(0, state.Scroll(-30, contentHeight: 400, viewportHeight: 200));
        Assert.Equal(120, state.Scroll(120, contentHeight: 400, viewportHeight: 200));
        Assert.Equal(200, state.Scroll(1000, contentHeight: 400, viewportHeight: 200));
        Assert.Equal(0, state.Scroll(-1000, contentHeight: 100, viewportHeight: 200));
    }

    [Fact]
    public void WindowStyle_PreservesClickThroughForMapsButEnablesSelectivePanelHitTesting()
    {
        var passthrough = LayeredOverlayWindow.BuildExtendedStyle(
            OverlayWindowInteraction.Passthrough);
        var selective = LayeredOverlayWindow.BuildExtendedStyle(
            OverlayWindowInteraction.Selective);

        Assert.NotEqual(0u, passthrough & OverlayNative.WS_EX_TRANSPARENT);
        Assert.Equal(0u, selective & OverlayNative.WS_EX_TRANSPARENT);
        Assert.NotEqual(0u, selective & OverlayNative.WS_EX_NOACTIVATE);
        Assert.Equal(
            OverlayNative.HTCLIENT,
            LayeredOverlayWindow.HitTest(
                OverlayWindowInteraction.Selective,
                screenX: 150,
                screenY: 260,
                originX: 100,
                originY: 200,
                width: 300,
                height: 200));
        Assert.Equal(
            OverlayNative.HTTRANSPARENT,
            LayeredOverlayWindow.HitTest(
                OverlayWindowInteraction.Passthrough,
                screenX: 150,
                screenY: 260,
                originX: 100,
                originY: 200,
                width: 300,
                height: 200));
    }

    [Fact]
    public void MouseActivate_AlwaysPreservesGameKeyboardFocus()
    {
        Assert.Equal(
            OverlayNative.MA_NOACTIVATE,
            LayeredOverlayWindow.MouseActivateResult(OverlayNative.WM_MOUSEACTIVATE));
        Assert.Equal(0, LayeredOverlayWindow.MouseActivateResult(OverlayNative.WM_LBUTTONDOWN));
    }
}
