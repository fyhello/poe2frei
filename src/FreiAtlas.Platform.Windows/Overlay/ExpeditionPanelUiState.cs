using System.Drawing;

namespace FreiAtlas.Platform.Windows.Overlay;

public sealed class ExpeditionPanelUiState
{
    private Point _dragOffset;

    public ExpeditionPanelUiState(Point position)
    {
        Position = position;
    }

    public Point Position { get; private set; }

    public int ScrollOffset { get; private set; }

    public bool IsDragging { get; private set; }

    public bool BeginDrag(
        Point screenPosition,
        bool leftButton,
        bool ctrl,
        Rectangle titleBounds)
    {
        if (!leftButton || !ctrl || !titleBounds.Contains(screenPosition))
        {
            return false;
        }

        _dragOffset = new Point(
            screenPosition.X - Position.X,
            screenPosition.Y - Position.Y);
        IsDragging = true;
        return true;
    }

    public bool MoveDrag(
        Point screenPosition,
        bool leftButton,
        bool ctrl)
    {
        if (!IsDragging)
        {
            return false;
        }

        if (!leftButton || !ctrl)
        {
            IsDragging = false;
            return true;
        }

        Position = new Point(
            screenPosition.X - _dragOffset.X,
            screenPosition.Y - _dragOffset.Y);
        return true;
    }

    public void EndDrag()
        => IsDragging = false;

    public void SetPosition(Point position)
        => Position = position;

    public void ClampToWorkArea(Size workArea, Size panelSize)
        => ClampToWorkArea(new Rectangle(Point.Empty, workArea), panelSize);

    public void ClampToWorkArea(Rectangle workArea, Size panelSize)
    {
        var maximumX = Math.Max(workArea.Left, workArea.Right - panelSize.Width);
        var maximumY = Math.Max(workArea.Top, workArea.Bottom - panelSize.Height);
        Position = new Point(
            Math.Clamp(Position.X, workArea.Left, maximumX),
            Math.Clamp(Position.Y, workArea.Top, maximumY));
    }

    public int Scroll(int delta, int contentHeight, int viewportHeight)
    {
        var maximum = Math.Max(0, contentHeight - viewportHeight);
        ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, maximum);
        return ScrollOffset;
    }

    public void ResetScroll()
        => ScrollOffset = 0;
}
