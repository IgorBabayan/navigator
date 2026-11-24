using Avalonia.Controls;

namespace Navigator.App.Controls;

/// <summary>
/// The background surface of a file row or tile. Styled exactly like a <see cref="Border"/> (same style key, so every
/// <c>Border.item</c> selector applies), but it switches its transitions off while virtualization hands the container
/// a new item. Without this, a recycled row would fade from the previous item's hover/selection state to the new
/// item's state while scrolling, which shows up as rows blinking under the pointer.
/// </summary>
public sealed class RowSurface : Border
{
    protected override Type StyleKeyOverride => typeof(Border);

    protected override void OnDataContextBeginUpdate()
    {
        base.OnDataContextBeginUpdate();
        // A local null beats the style value and disposes any running fade, so the new state is applied instantly.
        Transitions = null;
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        // Back to the styled Motion.Hover transitions for genuine hover/selection changes.
        ClearValue(TransitionsProperty);
    }
}
