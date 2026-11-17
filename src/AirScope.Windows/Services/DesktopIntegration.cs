using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirScope.Windows.Services;

public static class DesktopIntegration
{
    public static TimeSpan IdleTime()
    {
        var value = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref value)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - value.Tick));
    }
    public static void SetStartAtSignIn(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (enabled)
        {
            string exe = Environment.ProcessPath ?? throw new IOException("The application path is unavailable.");
            if (!Path.GetFileName(exe).Equals("AirScope.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Launch AirScope.exe directly to configure sign-in.");
            key.SetValue("AirScope", "\"" + exe + "\" --background");
        }
        else key.DeleteValue("AirScope", throwOnMissingValue: false);
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInput input);
}
