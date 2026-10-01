using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Stedjcast.Services;

/// <summary>A window showing a plugin's own GUI, hosted in a native child window.</summary>
public sealed class Vst3EditorWindow : Window
{
    private readonly Vst3PluginInstance _plugin;
    private readonly ChildHost _host = new();

    private Vst3EditorWindow(Vst3PluginInstance plugin, string title)
    {
        _plugin = plugin;
        Title = title;
        ResizeMode = ResizeMode.CanMinimize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = _host;
        Closed += (_, _) => _plugin.CloseEditor();
    }

    /// <summary>Opens the editor window, or returns null when the plugin has no GUI for Windows.</summary>
    public static Vst3EditorWindow? Open(Vst3PluginInstance plugin, string title, Window? owner)
    {
        var window = new Vst3EditorWindow(plugin, title) { Owner = owner, ShowActivated = true };
        window.Show();
        var size = plugin.OpenEditor(window._host.Handle, window.ResizeToPixels);
        if (size is null)
        {
            window.Close();
            return null;
        }
        window.ResizeToPixels(size.Value.Width, size.Value.Height);
        return window;
    }

    // Plugins work in physical pixels; WPF in device independent units.
    private bool ResizeToPixels(int width, int height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _host.Width = width / dpi.DpiScaleX;
        _host.Height = height / dpi.DpiScaleY;
        UpdateLayout();
        return true;
    }

    private sealed class ChildHost : HwndHost
    {
        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000;
            var hwnd = CreateWindowEx(0, "Static", "", WsChild | WsVisible | WsClipChildren,
                0, 0, 200, 200, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return new HandleRef(this, hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);
    }
}
