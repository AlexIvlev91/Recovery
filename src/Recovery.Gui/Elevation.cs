using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Recovery.Gui;

internal static class Elevation
{
    public static bool IsAdministrator { get; } = Check();

    private static bool Check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Start a new elevated instance (UAC prompt) that opens on <paramref name="page"/>.
    /// Returns false when the user declined the prompt.
    /// </summary>
    public static bool TryRestartElevated(string page)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return false;
        try
        {
            // "dotnet RecoveryGui.dll" during development: relaunch the dll through the host.
            var psi = exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(exe, $"\"{Path.Combine(AppContext.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".dll")}\" --page {page}")
                : new ProcessStartInfo(exe, $"--page {page}");
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // UAC cancelled
        }
    }
}
