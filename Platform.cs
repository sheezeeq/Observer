using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Observer;

internal sealed record GameWindow(nint Handle, string Title)
{
    public override string ToString() => Title;
}

internal static class Platform
{
    private delegate bool EnumWindowsProc(nint window, nint lparam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    public static List<GameWindow> Windows()
    {
        var result = new List<GameWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == Environment.ProcessId) return true;
            var name = new StringBuilder(256);
            if (GetWindowText(handle, name, name.Capacity) > 0 &&
                name.ToString().Contains("ArcheAge", StringComparison.OrdinalIgnoreCase))
                result.Add(new GameWindow(handle, name.ToString()));
            return true;
        }, 0);
        return result;
    }

    public static bool IsForeground(GameWindow window) => GetForegroundWindow() == window.Handle;
    public static void Activate(GameWindow window) => SetForegroundWindow(window.Handle);

    public static bool TryCapture(GameWindow window, out Bitmap? image, out string? reason)
    {
        image = null;
        reason = null;
        if (!IsWindow(window.Handle) || IsIconic(window.Handle) || !IsWindowVisible(window.Handle))
        {
            reason = "окно игры закрыто или свёрнуто";
            return false;
        }
        if (!GetClientRect(window.Handle, out var rect))
        {
            reason = "не удалось получить область окна игры";
            return false;
        }
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 200 || height < 150)
        {
            reason = "окно игры слишком мало";
            return false;
        }
        var point = new NativePoint();
        if (!ClientToScreen(window.Handle, ref point))
        {
            reason = "не удалось определить положение окна игры";
            return false;
        }
        try
        {
            image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(point.X, point.Y, 0, 0, new Size(width, height));
            return true;
        }
        catch (Exception ex)
        {
            image?.Dispose();
            image = null;
            reason = "снимок экрана не получен: " + ex.Message;
            return false;
        }
    }
}
