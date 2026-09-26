using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SoundRelay.Models;

namespace SoundRelay.Windows;

/// <summary>
/// Lists the windows a user would see when pressing Alt+Tab, so the source
/// picker only ever offers real, visible applications, never hidden services
/// or background processes.
/// </summary>
public static class WindowEnumerator
{
    public static List<AudioTargetWindow> GetVisibleWindows()
    {
        var results = new List<AudioTargetWindow>();
        var seenProcesses = new HashSet<int>();
        int ownProcessId = Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownProcessId)
                return true;

            // One entry per process: several windows of the same app share its audio.
            if (!seenProcesses.Add((int)pid))
                return true;

            string title = GetWindowTitle(hwnd);
            string processName;
            try
            {
                using var process = Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch
            {
                processName = "process " + pid;
            }

            results.Add(new AudioTargetWindow
            {
                ProcessId = (int)pid,
                ProcessName = processName,
                Title = title,
                Handle = hwnd,
                Icon = TryGetWindowIcon(hwnd),
            });

            return true;
        }, IntPtr.Zero);

        return results
            .OrderBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
            return false;

        if (GetWindowTextLength(hwnd) == 0)
            return false;

        // Tool windows never appear in the Alt+Tab list; app windows always do.
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            return false;

        // Cloaked windows (for example minimised UWP apps on another desktop)
        // are technically visible but should not be offered.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        // A window owned by another top-level window is a dialog, not the root app.
        IntPtr owner = GetWindow(hwnd, GW_OWNER);
        if (owner != IntPtr.Zero)
            return false;

        return true;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0)
            return string.Empty;

        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static ImageSource? TryGetWindowIcon(IntPtr hwnd)
    {
        try
        {
            IntPtr hIcon = SendMessage(hwnd, WM_GETICON, ICON_SMALL2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = SendMessage(hwnd, WM_GETICON, ICON_BIG, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = GetClassLongPtr(hwnd, GCLP_HICON);
            if (hIcon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint WM_GETICON = 0x007F;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
