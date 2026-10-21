using System.Globalization;

namespace DragDone.Core;

/// <summary>
/// Which language the interface uses: whatever Windows is showing, or a fixed choice.
/// </summary>
public enum LanguageMode
{
    /// <summary>Follow the operating system's display language.</summary>
    System,

    /// <summary>Always Arabic, right to left.</summary>
    Arabic,

    /// <summary>Always English, left to right.</summary>
    English,
}
