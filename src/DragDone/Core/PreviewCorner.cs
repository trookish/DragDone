namespace DragDone.Core;

/// <summary>
/// Where the settings window's floating live preview sits. It floats once the inline preview
/// scrolls out of view, and dragging it to another part of the window picks a new corner.
/// </summary>
public enum PreviewCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}
