using System.IO;
using System.Reflection;
using System.Windows;
using VModLauncher.Core.Game;
using System.Windows.Threading;
using VModLauncher.Core;
using VModLauncher.Core.Install;
using VModLauncher.Core.Install.Oiv;

namespace VModLauncher;

public partial class App : Application
{
    /// <summary>%LOCALAPPDATA%\VModLauncher, or VML_DATA for a separate test profile.</summary>
    public static LauncherPaths Paths { get; } = new(Environment.GetEnvironmentVariable("VML_DATA") is { Length: > 0 } d ? d : null);

    /// <summary>Set in the Legacy-only and Enhanced-only builds (-p:Edition=...); null in the build that handles both.</summary>
    public static GameEdition? LockedEdition { get; } =
        typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LockedEdition")?.Value is { } v && Enum.TryParse<GameEdition>(v, true, out var e) ? e : null;

    /// <summary>Real GTA V artwork pulled from the player's own game (see ArtService).</summary>
    public static Host.ArtService Art { get; } = new(Paths.Cache);

    public static string ProductName => LockedEdition is { } e ? $"V Mod Launcher {e}" : "V Mod Launcher";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("fatal", a.ExceptionObject?.ToString());

        // one launcher per data folder: the Legacy and Enhanced builds share the library, and two
        // writers would each overwrite the other's install records
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Paths.Root.ToLowerInvariant())))[..16];
        instanceLock = new System.Threading.Mutex(true, @"Local\VModLauncher-" + key, out var first);
        if (!first)
        {
            MessageBox.Show("V Mod Launcher is already open (the Legacy and Enhanced launchers share one library, so only one can run at a time). Switch to the open window, or close it first.",
                ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // .oiv packages run through the journal like every other install
        ModInstaller.OivRunner = (journal, path, log) =>
        {
            using var pkg = OivPackage.Open(path);
            var oiv = new OivInstaller(journal);
            oiv.Install(pkg, log);
            foreach (var w in oiv.Warnings) Log("oiv", $"{pkg.Name}: {w}");
        };

        new MainWindow().Show();
    }

    static System.Threading.Mutex? instanceLock;

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui", e.Exception.ToString());
        MessageBox.Show(e.Exception.Message, ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    public static void Log(string area, string? text)
    {
        try
        {
            File.AppendAllText(Path.Combine(Paths.Logs, $"launcher-{DateTime.Now:yyyyMMdd}.log"),
                $"{DateTime.Now:HH:mm:ss} [{area}] {text}{Environment.NewLine}");
        }
        catch { }
    }
}
