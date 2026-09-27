using FlowMonitor.Widgets;

namespace FlowMonitor.Host;

/// <summary>
/// Entry point for the widget's context menu. Forwards to ModernMenu so call
/// sites stay decoupled from the implementation.
/// </summary>
internal static class M3ContextMenu
{
    public static void Show(DesktopHost host, WidgetWindow widget, int x, int y)
        => ModernMenu.Show(host, widget, x, y);
}
