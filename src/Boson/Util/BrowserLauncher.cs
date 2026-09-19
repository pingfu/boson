using System.Diagnostics;

namespace Boson.Util;

public static class BrowserLauncher
{
    /// <summary>
    /// Best effort: the setup URL is public and served through Caddy, so the
    /// browser can be anywhere. A headless host simply prints it.
    /// </summary>
    public static bool TryOpen(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
                return true;
            }

            if (OperatingSystem.IsLinux())
            {
                var p = Process.Start(new ProcessStartInfo("xdg-open", url)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                return p is not null;
            }
        }
        catch
        {
        }
        
        return false;
    }
}
