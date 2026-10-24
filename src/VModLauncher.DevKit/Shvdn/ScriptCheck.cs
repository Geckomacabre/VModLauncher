using System.Diagnostics;
using System.Text;

namespace VModLauncher.DevKit.Shvdn;

/// <summary>
/// Runs ScriptCheck.exe (tools\ScriptCheck, .NET Framework like the game's Script Hook V .NET): every method of a
/// script JIT-compiled against a given ScriptHookVDotNet3.dll, so a member that build doesn't have shows up before
/// players see "Unhandled exception in script".
/// </summary>
public static class ScriptCheck
{
    public sealed record Result(int ExitCode, string Output)
    {
        public bool Passed => ExitCode == 0;
    }

    /// <summary>ScriptCheck.exe next to this tool, or where VML_SCRIPTCHECK points.</summary>
    public static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("VML_SCRIPTCHECK");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var here = Path.Combine(AppContext.BaseDirectory, "ScriptCheck.exe");
        return File.Exists(here) ? here : null;
    }

    /// <param name="shvdnFolder">A folder with ScriptHookVDotNet3.dll: a game folder works.</param>
    /// <param name="referenceFolders">Where the script's other references are (e.g. scripts\ for LemonUI).</param>
    public static Result Run(string scriptDll, string shvdnFolder, IEnumerable<string>? referenceFolders = null)
    {
        var exe = Locate() ?? throw new FileNotFoundException("ScriptCheck.exe not found: build tools\\ScriptCheck (it's copied next to vml) or set VML_SCRIPTCHECK");
        if (!File.Exists(scriptDll)) throw new FileNotFoundException("No script at " + scriptDll);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(Path.GetFullPath(scriptDll));
        psi.ArgumentList.Add(Path.GetFullPath(shvdnFolder));
        foreach (var r in referenceFolders ?? Array.Empty<string>()) psi.ArgumentList.Add(Path.GetFullPath(r));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ScriptCheck didn't start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("ScriptCheck took over 2 minutes"); }
        return new Result(p.ExitCode, (stdout.Result + stderr.Result).TrimEnd());
    }
}
