using VModLauncher.Core.Game;

namespace VModLauncher.Core.Features;

public sealed record KnownArg(string Arg, string Label, string Help, string[] Editions);

public sealed record LaunchOptionsInfo(string File, List<string> Args, List<KnownArg> Known);

/// <summary>
/// The game folder holds two launch-argument files:
///   args.txt        — read when the game is started through the Rockstar / Epic / Steam launcher.
///                     Script Hook V ships one with "-nobattleye -noBE" so story mode starts without
///                     BattlEye and mods can load.
///   commandline.txt — GTA V Legacy's own options file (windowed, safe mode, ...).
/// Enhanced only uses args.txt. BattlEye switches always go to args.txt; other options go to
/// commandline.txt on Legacy and args.txt on Enhanced. Options already in a file stay in that file.
/// </summary>
public static class LaunchOptions
{
    public const string ArgsFile = "args.txt";
    public const string CommandLineFile = "commandline.txt";

    static readonly string[] Both = { "Legacy", "Enhanced" };
    static readonly string[] LegacyOnly = { "Legacy" };

    public static readonly List<KnownArg> Known = new()
    {
        new("-nobattleye", "No BattlEye (story mode with mods)", "Starts story mode without the BattlEye anti-cheat so Script Hook V mods can load. GTA Online needs BattlEye: turn this and your mods off before going online.", Both),
        new("-windowed", "Windowed", "Starts the game in a window.", Both),
        new("-borderless", "Borderless window", "A window without borders that fills the screen (easy alt-tab).", Both),
        new("-fullscreen", "Fullscreen", "Starts the game in exclusive fullscreen.", Both),
        new("-ignoreDifferentVideoCard", "Ignore graphics card changes", "Keeps your graphics settings after a new graphics card or driver.", LegacyOnly),
        new("-safemode", "Safe mode", "Starts with the lowest graphics settings, to get into the menu after a bad setting.", LegacyOnly),
        new("-scOfflineOnly", "Social Club offline", "Keeps Social Club offline (story mode only).", LegacyOnly),
        new("-StraightIntoFreemode", "Straight into GTA Online", "Skips story mode and loads GTA Online. Turn mods off first.", Both),
    };

    static bool IsBattlEye(string a) => a.Equals("-nobattleye", StringComparison.OrdinalIgnoreCase) || a.Equals("-noBE", StringComparison.OrdinalIgnoreCase);

    static List<string> ReadArgs(string path)
    {
        if (!File.Exists(path)) return new();
        return File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static LaunchOptionsInfo Get(GameInstall game)
    {
        var args = ReadArgs(Path.Combine(game.Root, CommandLineFile));
        foreach (var a in ReadArgs(Path.Combine(game.Root, ArgsFile)))
            if (!args.Contains(a, StringComparer.OrdinalIgnoreCase)) args.Add(a);
        // -noBE always rides along with -nobattleye; show one switch
        args.RemoveAll(a => a.Equals("-noBE", StringComparison.OrdinalIgnoreCase) && args.Any(x => x.Equals("-nobattleye", StringComparison.OrdinalIgnoreCase)));
        var edition = game.Edition.ToString();
        return new LaunchOptionsInfo(
            game.Edition == GameEdition.Legacy ? CommandLineFile : ArgsFile,
            args,
            Known.Where(k => k.Editions.Contains(edition)).ToList());
    }

    /// <summary>Writes the options to the right files (backing up what was there) and returns the new state.</summary>
    public static LaunchOptionsInfo Set(GameInstall game, LauncherPaths paths, IEnumerable<string> wanted)
    {
        var list = wanted.Select(a => a.Trim()).Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Any(a => a.Equals("-nobattleye", StringComparison.OrdinalIgnoreCase)) && !list.Any(a => a.Equals("-noBE", StringComparison.OrdinalIgnoreCase)))
            list.Add("-noBE");
        if (!list.Any(a => a.Equals("-nobattleye", StringComparison.OrdinalIgnoreCase)))
            list.RemoveAll(a => a.Equals("-noBE", StringComparison.OrdinalIgnoreCase));

        var argsPath = Path.Combine(game.Root, ArgsFile);
        var cmdPath = Path.Combine(game.Root, CommandLineFile);
        var inArgs = ReadArgs(argsPath);
        var inCmd = ReadArgs(cmdPath);

        var toArgs = new List<string>();
        var toCmd = new List<string>();
        foreach (var a in list)
        {
            if (IsBattlEye(a) || game.Edition == GameEdition.Enhanced) toArgs.Add(a);
            else if (inArgs.Contains(a, StringComparer.OrdinalIgnoreCase)) toArgs.Add(a);
            else toCmd.Add(a);
        }

        WriteIfChanged(game, paths, argsPath, inArgs, toArgs);
        WriteIfChanged(game, paths, cmdPath, inCmd, toCmd);
        return Get(game);
    }

    static void WriteIfChanged(GameInstall game, LauncherPaths paths, string path, List<string> before, List<string> after)
    {
        if (before.SequenceEqual(after, StringComparer.Ordinal)) return;
        if (!File.Exists(path) && after.Count == 0) return;
        if (File.Exists(path))
        {
            var backup = Path.Combine(paths.State, $"{Path.GetFileNameWithoutExtension(path)}-{game.Edition.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.Copy(path, backup, overwrite: true);
        }
        File.WriteAllText(path, string.Join(" ", after));
    }
}
