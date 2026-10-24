using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace VModLauncher.Host;

/// <summary>
/// Games installed under Program Files are read-only for the player. One elevated icacls grant
/// (the player sees Windows' admin prompt) gives their account modify rights on the game folder,
/// so the launcher itself never needs to run as administrator.
/// </summary>
public static class FolderAccess
{
    public static async Task<bool> GrantAsync(string folder)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("No user SID");
        // (OI)(CI)M covers folders and what's created later; the plain M covers files that have their
        // own rights; /T walks everything, /C keeps going past locked files, /Q stays quiet.
        var psi = new ProcessStartInfo("icacls.exe", $"\"{folder}\" /grant *{sid}:(OI)(CI)M *{sid}:M /T /C /Q")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return true;
        }
        catch (Win32Exception) // the player said No to the prompt
        {
            return false;
        }
    }
}
