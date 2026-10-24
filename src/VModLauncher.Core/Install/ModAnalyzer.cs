using System.Text.RegularExpressions;
using System.Xml;
using CodeWalker.GameFiles;
using VModLauncher.Core.Game;

namespace VModLauncher.Core.Install;

/// <summary>Looks up where a vanilla game file lives, for replace mods (see GameIndex).</summary>
public interface IGameIndex
{
    /// <summary>Game-relative "archive|inner/path" locations of a file name, best (highest priority) first.</summary>
    IReadOnlyList<string> Find(string fileName);
    /// <summary>The game's own copy at a location from <see cref="Find"/>, or null.</summary>
    byte[]? Read(string location);
}

/// <summary>
/// Works out where every file of an unpacked mod belongs, using what the files are
/// (.NET references, XML roots, dlc.rpf names, folders that mirror the game folder)
/// rather than trusting the archive's folder names alone.
/// </summary>
public sealed class ModAnalyzer
{
    readonly GameInstall? game;
    readonly IGameIndex? index;

    public ModAnalyzer(GameInstall? game, IGameIndex? index = null)
    {
        this.game = game;
        this.index = index;
    }

    // ---------------------------------------------------------------- vocabulary

    static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase) { ".ds_store", "thumbs.db", "desktop.ini" };

    /// <summary>Folders that only exist in the game root; an archive folder holding one of these mirrors the game folder.</summary>
    static readonly HashSet<string> RootAnchors = new(StringComparer.OrdinalIgnoreCase)
        { "scripts", "plugins", "lspdfr", "els", "menyoostuff", "mods", "update", "reshade-shaders", "enbseries", "onigiri", "lml" };

    /// <summary>Files that only make sense in the game root.</summary>
    static readonly Regex RootMarkerFile = new(
        @"^(scripthookv\.dll|dinput8\.dll|xinput1_4\.dll|ragepluginhook\.exe|ragepluginhook\.ini|.*\.asi|d3d11\.dll|dxgi\.dll|enbseries\.ini|enblocal\.ini|reshade\.ini|scripthookvdotnet\d?\.dll|lspd first response\.dll)$",
        RegexOptions.IgnoreCase);

    /// <summary>Wrapper folder names authors use for "this is your game folder".</summary>
    /// <remarks>
    /// Only names that clearly mean "the game's own folder". Generic wrappers ("Main Files", "Installation",
    /// "Game") are left out: authors use them for anything.
    /// </remarks>
    static readonly Regex RootFolderName = new(
        @"^(grand[ _-]?theft[ _-]?auto[ _-]?v|gta[ _-]?v|gta[ _-]?5|gtav|gta5)([ _-]?(enhanced|legacy|directory|folder|root|main( folder| directory)?|install(ation)?( folder)?))?$|^(game|gta|main|root)[ _-]?(directory|folder|dir)$|^root$",
        RegexOptions.IgnoreCase);

    static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".pdf", ".rtf", ".url", ".html", ".htm", ".doc", ".docx", ".odt", ".nfo", ".lnk" };
    static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".mp4", ".webm", ".mkv", ".avi", ".psd", ".tga" };
    static readonly HashSet<string> ConfigExt = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".json", ".cfg", ".toml", ".yaml", ".yml", ".config", ".xml", ".dat", ".log", ".csv" };
    static readonly HashSet<string> AssetExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".yft", ".ytd", ".ydr", ".ydd", ".ycd", ".ymt", ".ytyp", ".ymap", ".ybn", ".ynv", ".ynd", ".ypt", ".yld", ".yed",
        ".ywr", ".yvr", ".awc", ".rel", ".gxt2", ".ymf", ".gfx", ".meta", ".ysc", ".nametable", ".dat151", ".dat54", ".dat4", ".dat10", ".dat16", ".dat22",
    };
    static readonly Regex SaveGameName = new(@"^SGTA5\d{4}(\.bak)?$", RegexOptions.IgnoreCase);
    /// <summary>Launch-argument files the game reads from its root; merged, never replaced.</summary>
    public static readonly HashSet<string> ArgFiles = new(StringComparer.OrdinalIgnoreCase) { "args.txt", "commandline.txt" };
    static readonly Regex EditionWord = new(@"\b(legacy|enhanced|gen ?9|e&e|e ?& ?e|next[ -]?gen|old[ -]?gen|expanded)\b", RegexOptions.IgnoreCase);
    static readonly Regex OptionalFolder = new(@"^(optional|extras?|bonus|alternatives?|alt)\b|\boptional\b", RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- model

    sealed class ModFile
    {
        public required string Full;
        public required string Rel;          // forward slashes, relative to staging root
        public required string[] Parts;
        public string Name => Parts[^1];
        public string Ext => Path.GetExtension(Name);
        public string Dir => Parts.Length > 1 ? string.Join('/', Parts, 0, Parts.Length - 1) : "";
        public InstallOp? Op;
    }

    // ---------------------------------------------------------------- entry point

    public InstallPlan Analyze(string stagingDir, string modName, string? archivePath = null)
    {
        stagingDir = Path.GetFullPath(stagingDir);
        var plan = new InstallPlan { ModName = modName, StagingDir = stagingDir, ArchivePath = archivePath ?? "" };

        var files = Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(stagingDir, f).Replace('\\', '/'))
            .Where(r => !r.Split('/').Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)))
            .Where(r => !r.StartsWith(Tools.AddonPackBuilder.BuildFolder + "/", StringComparison.OrdinalIgnoreCase)) // packs built by an earlier analysis
            .Where(r => !JunkNames.Contains(Path.GetFileName(r)))
            .Select(r => new ModFile { Full = Path.Combine(stagingDir, r.Replace('/', '\\')), Rel = r, Parts = r.Split('/') })
            .OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            plan.Warnings.Add(new(WarningLevel.Block, "The download is empty."));
            return plan;
        }

        var notForSp = NotASinglePlayerMod(files);
        if (notForSp != null)
        {
            plan.Ops.AddRange(files.Select(f => Skip(f, notForSp)));
            plan.Summary = "Not a GTA V single-player mod";
            plan.Warnings.Add(new(WarningLevel.Block, notForSp));
            return plan;
        }

        // 1. OpenIV packages decide everything themselves.
        var oivOps = OivPackages(files);
        if (oivOps.Count > 0)
        {
            foreach (var (op, f) in oivOps) { f.Op = op; }
            foreach (var f in files.Where(f => f.Op == null)) f.Op = Skip(f, "Not needed: the .oiv package installs everything");
            plan.Ops.AddRange(files.Select(f => f.Op!));
            var infos = oivOps.ToDictionary(o => o.Op, o => Oiv.OivPackage.Peek(o.Op.Source));
            foreach (var (op, info) in infos)
                if (info != null) op.Reason = $"OpenIV package: {info.Name} {info.VersionText}{(info.Author.Length > 0 ? " by " + info.Author : "")}"
                                              + (info.TargetEdition is { } te ? $" (for {te})" : "");
            // several packages are alternatives (Legacy / Enhanced, Lite / Full) or parts to install together (base + add-on)
            if (oivOps.Count > 1 && OivsAreAlternatives(oivOps.Select(o => o.Op).ToList(), infos))
                VariantsFromOivs(plan, oivOps.Select(o => o.Op).ToList(), infos);
            var chosen = plan.ActiveOps.FirstOrDefault(o => o.Kind == OpKind.Oiv);
            if (chosen != null && infos[chosen] is { } meta)
            {
                plan.PackageName = meta.Name;
                plan.PackageVersion = meta.VersionText;
                plan.PackageAuthor = meta.Author.Length > 0 ? meta.Author : null;
                // only when no package in the download fits this game
                if (game != null && infos.Values.All(i => i?.TargetEdition is { } t && t != game.Edition))
                    plan.Warnings.Add(new(WarningLevel.Warn, $"This package is made for GTA V {meta.TargetEdition}, and your game is {game.Edition}. Look for the {game.Edition} version of the mod."));
            }
            Finish(plan, files);
            return plan;
        }

        // 2. Folders that mirror the game folder map 1:1.
        MapGameRootFolders(files);

        // 3. Everything else by what it is.
        foreach (var f in files.Where(f => f.Op == null)) ClassifyLoose(f, files);
        LspdfrAudio(files);
        foreach (var f in files.Where(f => f.Op == null)) AttachToSibling(f, files);
        foreach (var f in files.Where(f => f.Op == null)) f.Op = Skip(f, "Not recognised: choose where it goes if the readme says it's needed");

        // 3b. Add-on cars and peds shipped as loose files get a proper dlc.rpf built for them.
        var built = BuildLooseAddonPacks(plan, files);

        plan.Ops.AddRange(files.Select(f => f.Op!));
        plan.Ops.AddRange(built.Select(b => b.Op));

        // 4. Choices: game-version folders, then folders that would write the same target.
        EditionVariants(plan, files);
        CollisionVariants(plan, files);
        OptionalFolders(plan, files);
        ExtraAsiLoaders(plan);
        // a built pack belongs to whatever choice its loose files sit in
        foreach (var b in built) b.Op.Variant = b.Packed.Select(f => f.Op!.Variant).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        Finish(plan, files);
        if (built.Count > 0 && plan.Summary == "Add-on DLC pack")
            plan.Summary = built.All(b => b.Kind == "ped") ? "Add-on ped" : "Add-on vehicle";
        return plan;
    }

    /// <summary>Downloads that are for something else entirely get a plain explanation instead of a plan.</summary>
    static string? NotASinglePlayerMod(List<ModFile> files)
    {
        bool Any(Func<ModFile, bool> p) => files.Any(p);
        bool hasGtaContent = Any(f => f.Ext is ".asi" or ".oiv" or ".rpf" || AssetExt.Contains(f.Ext) || f.Name.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase)
                                      || (f.Ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && PeInspector.Inspect(f.Full) is DllKind.Shvdn or DllKind.RphPlugin or DllKind.LspdfrPlugin));
        if (Any(f => f.Name.Equals("fxmanifest.lua", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("__resource.lua", StringComparison.OrdinalIgnoreCase)))
            return "This is a FiveM server resource (it has fxmanifest.lua), not a single-player mod. It goes on a FiveM server's resources folder.";
        if (hasGtaContent) return null;
        if (Any(f => f.Ext.Equals(".jar", StringComparison.OrdinalIgnoreCase)))
            return "This looks like a Minecraft mod (.jar files), not a GTA V mod.";
        if (Any(f => f.Name.Equals("blender_manifest.toml", StringComparison.OrdinalIgnoreCase)))
            return "This is a Blender add-on, not a GTA V mod.";
        return null;
    }

    // ---------------------------------------------------------------- OIV

    List<(InstallOp Op, ModFile File)> OivPackages(List<ModFile> files)
    {
        var result = new List<(InstallOp, ModFile)>();
        // "Uninstall.oiv" next to the real package is skipped; a lone "Remove Wanted Stars.oiv" is the mod itself
        var oivs = files.Where(f => f.Ext.Equals(".oiv", StringComparison.OrdinalIgnoreCase)).ToList();
        bool IsUninstaller(ModFile f) => Regex.IsMatch(Path.GetFileNameWithoutExtension(f.Name), @"(^|[^a-z])un-?install(er)?([^a-z]|$)", RegexOptions.IgnoreCase);
        bool hasInstaller = oivs.Any(o => !IsUninstaller(o));
        foreach (var f in files)
        {
            if (f.Ext.Equals(".oiv", StringComparison.OrdinalIgnoreCase))
            {
                if (hasInstaller && IsUninstaller(f))
                {
                    f.Op = Skip(f, "Uninstaller package (the launcher can uninstall by itself)");
                    continue;
                }
                result.Add((new InstallOp { Kind = OpKind.Oiv, Source = f.Full, SourceRel = f.Rel, Target = Path.GetFileNameWithoutExtension(f.Name), Reason = "OpenIV package" }, f));
            }
            else if (f.Name.Equals("assembly.xml", StringComparison.OrdinalIgnoreCase)
                     && files.Any(o => o.Rel.StartsWith(f.Dir.Length == 0 ? "content/" : f.Dir + "/content/", StringComparison.OrdinalIgnoreCase)))
            {
                // an .oiv that was unpacked: folder with assembly.xml + content/
                var dir = Path.GetDirectoryName(f.Full)!;
                var op = new InstallOp { Kind = OpKind.Oiv, Source = dir, SourceRel = f.Dir.Length == 0 ? "(package)" : f.Dir, Target = f.Dir.Length == 0 ? "package" : f.Parts[^2], Reason = "Unpacked OpenIV package" };
                result.Add((op, f));
                var prefix = f.Dir.Length == 0 ? "" : f.Dir + "/";
                foreach (var o in files.Where(o => o != f && o.Rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    o.Op = Skip(o, "Part of the OpenIV package");
            }
        }
        return result;
    }

    static readonly Regex AlternativeWord = new(@"\b(legacy|enhanced|gen ?9|e ?& ?e|lite|light|full|hd|sd|low|medium|high|ultra|\d+k|replace|add-?on|option|version|variant|v\d+)\b", RegexOptions.IgnoreCase);

    static bool OivsAreAlternatives(List<InstallOp> oivs, Dictionary<InstallOp, Oiv.OivPackage.Info?> infos)
    {
        var editions = oivs.Select(o => infos[o]?.TargetEdition).Where(e => e != null).Distinct().Count();
        if (editions > 1) return true;
        // alternatives tell themselves apart with words like these; base + extras packs don't
        var words = oivs.Select(o => AlternativeWord.Matches(o.Target).Select(m => m.Value.ToLowerInvariant()).OrderBy(x => x).ToList()).ToList();
        return words.Count(w => w.Count > 0) >= 2 && words.Select(w => string.Join(",", w)).Distinct().Count() > 1;
    }

    void VariantsFromOivs(InstallPlan plan, List<InstallOp> oivs, Dictionary<InstallOp, Oiv.OivPackage.Info?> infos)
    {
        var group = new VariantGroup { Id = "oiv", Title = "Package" };
        foreach (var op in oivs)
        {
            var id = op.SourceRel;
            var ed = infos[op]?.TargetEdition;
            group.Options.Add(new VariantOption { Id = id, Label = ed != null ? $"{op.Target} ({ed})" : op.Target });
            op.Variant = "oiv:" + id;
        }
        PickRecommended(group, oivs.Select(o => o.SourceRel).ToList());
        // the package's own target beats guessing from its file name
        var match = oivs.FirstOrDefault(o => game != null && infos[o]?.TargetEdition == game.Edition);
        if (match != null)
        {
            group.Selected = match.SourceRel;
            foreach (var o in group.Options) o.Recommended = o.Id == match.SourceRel;
        }
        plan.Variants.Add(group);
    }

    // ---------------------------------------------------------------- game-root mirrors

    void MapGameRootFolders(List<ModFile> files)
    {
        // A folder is the game root if it holds a root-only folder (scripts, plugins, mods, ...),
        // a root-only file (.asi, ScriptHookV.dll, RAGEPluginHook.exe), or is named like the game folder.
        var dirs = files.Select(f => f.Dir).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var allDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        foreach (var d in dirs)
        {
            var parts = d.Length == 0 ? Array.Empty<string>() : d.Split('/');
            for (int i = 1; i <= parts.Length; i++) allDirs.Add(string.Join('/', parts, 0, i));
        }

        var strongRoots = new List<string>();
        var weakRoots = new List<string>();
        foreach (var d in allDirs.OrderBy(d => d.Length))
        {
            if (strongRoots.Concat(weakRoots).Any(r => IsUnder(d, r) && d != r)) continue; // inside an earlier root
            var name = d.Length == 0 ? "" : d.Split('/')[^1];
            var childDirs = allDirs.Where(c => Parent(c) == d && c != d).Select(c => c.Split('/')[^1]).ToList();
            var childFiles = files.Where(f => f.Dir.Equals(d, StringComparison.OrdinalIgnoreCase)).ToList();

            bool hasAnchorDir = childDirs.Any(c => RootAnchors.Contains(c) && LooksLikeAnchorContent(files, Join(d, c)));
            bool namedRoot = name.Length > 0 && RootFolderName.IsMatch(name);
            bool hasMarker = childFiles.Any(f => RootMarkerFile.IsMatch(f.Name));

            if (hasAnchorDir || namedRoot) strongRoots.Add(d);
            else if (hasMarker) weakRoots.Add(d);
        }

        foreach (var f in files)
        {
            var root = strongRoots.Concat(weakRoots).Where(r => IsUnder(f.Dir, r)).OrderByDescending(r => r.Length).FirstOrDefault();
            if (root == null) continue;
            bool strong = strongRoots.Contains(root);
            var relParts = f.Parts.Skip(root.Length == 0 ? 0 : root.Split('/').Length).ToArray();
            MapRootRelative(f, relParts, strong, files, root);
        }
    }

    static bool LooksLikeAnchorContent(List<ModFile> files, string anchorDir)
    {
        // "update" or "mods" folders must really hold game data, not e.g. a changelog folder called "update"
        var name = anchorDir.Split('/')[^1].ToLowerInvariant();
        var inside = files.Where(f => IsUnder(f.Dir, anchorDir)).ToList();
        if (inside.Count == 0) return false;
        return name switch
        {
            "update" => inside.Any(f => f.Rel.Contains("/x64/", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)),
            "mods" => inside.Any(f => f.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase) || f.Rel.Contains("/update/", StringComparison.OrdinalIgnoreCase)),
            "scripts" => inside.Any(f => !DocExt.Contains(f.Ext) && !MediaExt.Contains(f.Ext)),
            _ => true,
        };
    }

    void MapRootRelative(ModFile f, string[] rel, bool strongRoot, List<ModFile> files, string root)
    {
        // folders named like archives ("x64e.rpf/levels/...", "mods/update/update.rpf/common/...") give the path inside the game
        var rpfSeg = Array.FindIndex(rel, 0, Math.Max(0, rel.Length - 1), p => p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
        if (rpfSeg >= 0)
        {
            var archive = ResolveArchive(rel.Take(rpfSeg + 1).ToArray());
            if (archive != null)
            {
                f.Op = RpfOp(f, archive, string.Join('/', rel.Skip(rpfSeg + 1)), "File inside a game archive (path given by the mod)");
                return;
            }
        }

        var first = rel.Length > 1 ? rel[0] : null;
        bool inAnchor = first != null && RootAnchors.Contains(first);

        // dlc packs anywhere under the root still need a dlclist entry
        if (f.Name.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase))
        {
            f.Op = DlcPackOp(f, "Add-on DLC pack");
            return;
        }

        if (!inAnchor)
        {
            // Models, textures and data files never belong loose in the game folder, and .NET scripts or
            // plugins have their own folders: those get the normal type-based placement.
            if (AssetExt.Contains(f.Ext) || (f.Ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) && IsGameMetaXml(f)))
                return;
            if (f.Ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                && PeInspector.Inspect(f.Full) is DllKind.Shvdn or DllKind.RphPlugin or DllKind.LspdfrPlugin)
                return;

            if (rel.Length == 1)
            {
                // top level of the game folder: install real files, show docs and pictures
                if (ArgFiles.Contains(f.Name)) { f.Op = Copy(f, f.Name, "Launch options (merged into yours)"); return; }
                if (IsDoc(f) || MediaExt.Contains(f.Ext)) { f.Op = Skip(f, "Readme / picture"); return; }
                if (f.Ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !f.Name.Equals("RAGEPluginHook.exe", StringComparison.OrdinalIgnoreCase))
                {
                    f.Op = Skip(f, "Program inside the mod (not copied; run it yourself if the readme says so)");
                    return;
                }
                f.Op = Copy(f, string.Join('\\', rel), "Game folder file");
                return;
            }
            // a sub-folder that isn't a known game folder: only trust it when the archive clearly mirrors the game folder
            if (!strongRoot && !SubfolderBelongsToRootFile(first!, files, root))
                return; // leave for loose classification
            if (IsDoc(f) && IsReadmeName(f.Name)) { f.Op = Skip(f, "Readme"); return; }
            f.Op = Copy(f, string.Join('\\', rel), "Game folder (archive mirrors the game folder)");
            return;
        }

        var anchor = first!.ToLowerInvariant();
        if (anchor is "update" or "mods")
        {
            // game data: always goes to mods\, never the game's own update folder
            var path = anchor == "mods" ? rel : new[] { "mods" }.Concat(rel).ToArray();
            var rpfIndex = Array.FindIndex(path, p => p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
            if (rpfIndex >= 0 && rpfIndex < path.Length - 1)
            {
                // folder named like an archive: "mods/update/update.rpf/common/data/x.meta" means "put x.meta inside update.rpf"
                var archive = string.Join('\\', path.Skip(1).Take(rpfIndex));
                var inner = string.Join('/', path.Skip(rpfIndex + 1));
                f.Op = RpfOp(f, archive, inner, "File inside a game archive (path given by the mod)");
                return;
            }
            f.Op = Copy(f, string.Join('\\', path), "mods folder file");
            return;
        }

        if (IsDoc(f) && IsReadmeName(f.Name) && rel.Length == 2) { f.Op = Skip(f, "Readme"); return; }
        if (anchor == "els" && rel.Length == 2 && f.Ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) && XmlRoot(f.Full) == "vcfroot")
        {
            // ELS only reads ELS\pack_default\, even when the archive's ELS folder skips that level
            f.Op = Copy(f, @"ELS\pack_default\" + f.Name, "ELS vehicle config");
            return;
        }
        f.Op = Copy(f, string.Join('\\', rel), $"{rel[0]} folder");
    }

    static bool SubfolderBelongsToRootFile(string folder, List<ModFile> files, string root)
    {
        // weak root (just an .asi next to it): keep folders the plugin obviously owns
        var rootFiles = files.Where(f => f.Dir.Equals(root, StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetFileNameWithoutExtension(f.Name)).ToList();
        if (rootFiles.Any(n => folder.StartsWith(n, StringComparison.OrdinalIgnoreCase) || n.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
            return true;
        var inside = files.Where(f => IsUnder(f.Dir, Join(root, folder))).ToList();
        return inside.Count > 0 && inside.All(f => ConfigExt.Contains(f.Ext) || f.Ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || AssetExt.Contains(f.Ext));
    }

    // ---------------------------------------------------------------- loose files

    void ClassifyLoose(ModFile f, List<ModFile> files)
    {
        var name = f.Name;
        var ext = f.Ext.ToLowerInvariant();

        // folders named after archives: "x64e.rpf/levels/gta5/vehicles.rpf/adder.yft"
        var rpfSeg = Array.FindIndex(f.Parts, 0, f.Parts.Length - 1, p => p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
        if (rpfSeg >= 0)
        {
            var archive = ResolveArchive(f.Parts.Take(rpfSeg + 1).ToArray());
            if (archive != null)
            {
                f.Op = RpfOp(f, archive, string.Join('/', f.Parts.Skip(rpfSeg + 1)), "File inside a game archive (path given by the mod)");
                return;
            }
        }

        if (name.Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase)) { f.Op = DlcPackOp(f, "Add-on DLC pack"); return; }
        if (SaveGameName.IsMatch(name)) { f.Op = new InstallOp { Kind = OpKind.SaveGame, Source = f.Full, SourceRel = f.Rel, Target = name, Reason = "Save game" }; return; }

        switch (ext)
        {
            case ".asi":
                f.Op = Copy(f, name, "ASI plugin (game folder)");
                return;
            case ".dll":
                ClassifyDll(f, files);
                return;
            case ".cs":
            case ".vb":
                if (IsSourceCode(f, files)) { f.Op = Skip(f, "Source code"); return; }
                f.Op = Copy(f, "scripts\\" + name, ".NET script source (compiled by Script Hook V .NET)");
                return;
            case ".xml":
                if (ClassifyXml(f)) return;
                break;
            case ".exe":
                f.Op = Skip(f, Regex.IsMatch(name, "setup|install", RegexOptions.IgnoreCase)
                    ? "Installer program inside the mod: not run automatically"
                    : "Program (tool), not copied into the game");
                return;
        }

        if (IsDoc(f) || MediaExt.Contains(ext)) { f.Op = Skip(f, IsReadmeName(name) ? "Readme" : "Readme / picture"); return; }

        if (AssetExt.Contains(ext) || (ext == ".xml" && IsGameMetaXml(f)))
        {
            ClassifyAsset(f);
        }
    }

    void ClassifyDll(ModFile f, List<ModFile> files)
    {
        var kind = PeInspector.Inspect(f.Full);
        switch (kind)
        {
            case DllKind.Shvdn:
                f.Op = Copy(f, "scripts\\" + f.Name, ".NET script (uses Script Hook V .NET)");
                return;
            case DllKind.LspdfrPlugin:
                f.Op = Copy(f, "plugins\\LSPDFR\\" + f.Name, "LSPDFR plugin");
                return;
            case DllKind.RphPlugin:
                f.Op = Copy(f, "plugins\\" + f.Name, "RAGE Plugin Hook plugin");
                return;
            case DllKind.RphLibrary:
                f.Op = Copy(f, f.Name, "Library for RAGE Plugin Hook plugins (game folder)");
                return;
            default:
                // plain .NET or native library: follow what it ships with (handled in AttachToSibling)
                return;
        }
    }

    bool ClassifyXml(ModFile f)
    {
        var root = XmlRoot(f.Full);
        switch (root)
        {
            case "vcfroot":
                f.Op = Copy(f, "ELS\\pack_default\\" + f.Name, "ELS vehicle config");
                return true;
            case "SpoonerPlacements":
                f.Op = Copy(f, "menyooStuff\\Spooner\\" + f.Name, "Menyoo map / placement");
                return true;
            case "OutfitPedData":
                f.Op = Copy(f, "menyooStuff\\Outfits\\" + f.Name, "Menyoo outfit");
                return true;
            case "Map" when XmlHasChild(f.Full, "Objects"):
                f.Op = Copy(f, "scripts\\AutoloadMaps\\" + f.Name, "Map Editor map (loads automatically)");
                return true;
            case "SMandatoryPacksData":
                f.Op = Skip(f, "Full dlclist.xml from the mod: not copied (it would drop your other packs); its packs are registered one by one instead");
                return true;
        }
        return false;
    }

    static readonly HashSet<string> MetaRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "CVehicleModelInfo__InitDataList", "CHandlingDataMgr", "CVehicleModelInfoVarGlobal", "CVehicleModelInfoVariation",
        "CVehicleMetadataMgr", "CPedModelInfo__InitDataList", "CWeaponInfoBlob", "CWeaponComponentInfoBlob",
        "CWeaponAnimationsSets", "CPedVariationInfo", "CDataFileMgr__ContentsOfDataFileXml", "SSetupData",
        "CScenarioPointManifest", "CMapTypes", "CMapData", "CTimeCycleModifiersManager", "CExtraContentManager",
    };

    bool IsGameMetaXml(ModFile f) => MetaRoots.Contains(XmlRoot(f.Full) ?? "");

    void ClassifyAsset(ModFile f)
    {
        if (index != null)
        {
            var hits = index.Find(f.Name).ToList();
            if (hits.Count > 0)
            {
                string reason;
                if (hits.Count > 1 && TextData.Contains(f.Ext))
                {
                    // data files (handling.meta, carcols.meta, ...) exist once per DLC: pick the copy whose
                    // entries the mod's file actually edits, not just the newest one
                    var (best, score) = BestByContent(f.Full, hits);
                    if (best != null && score >= 0.2)
                    {
                        hits.Remove(best);
                        hits.Insert(0, best);
                        reason = $"Replaces the game's copy with the same entries ({score:P0} match, {hits.Count} copies)";
                    }
                    else
                    {
                        var update = hits.FirstOrDefault(h => h.StartsWith(@"update\update.rpf|", StringComparison.OrdinalIgnoreCase));
                        if (update != null) { hits.Remove(update); hits.Insert(0, update); }
                        reason = $"Replaces the game's file ({hits.Count} copies and none clearly matches: check the readme)";
                    }
                }
                else
                {
                    reason = hits.Count == 1 ? "Replaces the game's own file" : $"Replaces the copy the game loads ({hits.Count} copies in the game)";
                }
                var (archive, inner) = SplitRpfTarget(hits[0]);
                f.Op = RpfOp(f, archive, inner, reason);
                f.Op.Alternatives.AddRange(hits.Skip(1));
                return;
            }
            f.Op = Skip(f, "Game file that doesn't exist in the base game: probably an add-on without a dlc.rpf");
            return;
        }
        f.Op = Skip(f, "Game file meant to replace one inside the game's archives (scan the game to place it)");
    }

    /// <summary>
    /// Callout packs often ship their dispatch lines in a loose "Audio" folder; LSPDFR's police scanner
    /// reads them from lspdfr\audio\scanner\&lt;pack&gt;\.
    /// </summary>
    static void LspdfrAudio(List<ModFile> files)
    {
        if (!files.Any(f => f.Op?.Target.StartsWith(@"plugins\LSPDFR\", StringComparison.OrdinalIgnoreCase) == true)) return;
        foreach (var f in files.Where(f => f.Op == null && f.Ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)))
        {
            var i = Array.FindIndex(f.Parts, p => p.Equals("audio", StringComparison.OrdinalIgnoreCase) || p.Equals("scanner", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i >= f.Parts.Length - 1) continue;
            var rest = f.Parts.Skip(i + 1).ToList();
            if (rest.Count > 0 && rest[0].Equals("scanner", StringComparison.OrdinalIgnoreCase)) rest.RemoveAt(0);
            f.Op = Copy(f, @"lspdfr\audio\scanner\" + string.Join('\\', rest), "LSPDFR scanner audio");
        }
    }

    // ---------------------------------------------------------------- loose add-on packs

    sealed record BuiltPack(InstallOp Op, string Kind, List<ModFile> Packed);

    /// <summary>Game files nothing could place, or that only matched the game's own copies by name.</summary>
    static bool IsLooseGameFile(ModFile f) => f.Op != null &&
        ((f.Op.Kind == OpKind.Skip && f.Op.Reason.StartsWith("Game file"))
         || (f.Op.Kind == OpKind.RpfFile && f.Op.Reason.StartsWith("Replaces")));

    /// <summary>
    /// Add-on cars and peds that come as loose files (models + vehicles.meta / peds.meta) get a dlc.rpf built
    /// in the staging folder, installed like any other add-on pack. Models the game already has stay
    /// replace files.
    /// </summary>
    List<BuiltPack> BuildLooseAddonPacks(InstallPlan plan, List<ModFile> files)
    {
        var result = new List<BuiltPack>();
        var loose = files.Where(IsLooseGameFile).ToList();
        if (!loose.Any(f => f.Ext.Equals(".meta", StringComparison.OrdinalIgnoreCase) || f.Ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)))
            return result;

        // the game scan knows every vanilla model; without it, the built-in list of the game's cars
        bool IsVanilla(string modelFile) => index != null
            ? index.Find(modelFile).Count > 0
            : modelFile.EndsWith(".yft", StringComparison.OrdinalIgnoreCase) && Tools.AddonPackVanilla.Vehicles.Contains(Path.GetFileNameWithoutExtension(modelFile));
        var sets = Tools.AddonPackBuilder.Detect(loose.Select(f => (f.Rel, f.Full)).ToList(), IsVanilla);
        if (sets.Count == 0) return result;

        var taken = TakenPackNames();
        var existing = ExistingPackModels(sets.SelectMany(s => s.Models).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var outDir = Path.Combine(plan.StagingDir, Tools.AddonPackBuilder.BuildFolder);
        foreach (var set in sets)
        {
            var name = Tools.AddonPackBuilder.PackName(set, taken);
            taken.Add(name);
            string dlc;
            try { dlc = Tools.AddonPackBuilder.Build(set, outDir, name); }
            catch (Exception ex)
            {
                plan.Warnings.Add(new(WarningLevel.Warn, $"Couldn't build an add-on pack from the loose files for {set.Models[0]}: {ex.Message}"));
                continue;
            }

            var rels = set.AllRels.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var packed = files.Where(f => rels.Contains(f.Rel)).ToList();
            foreach (var f in packed) f.Op = Skip(f, $"Packed into {name}");
            var what = set.Kind == "ped" ? (set.Models.Count == 1 ? "add-on ped" : "add-on peds") : (set.Models.Count == 1 ? "add-on car" : "add-on cars");
            var op = new InstallOp
            {
                Kind = OpKind.DlcPack,
                Source = dlc,
                SourceRel = $"{Tools.AddonPackBuilder.BuildFolder}/{name}/dlc.rpf",
                Target = name,
                Reason = $"Built from the mod's loose files ({what}: {string.Join(", ", set.Models.Take(6))}{(set.Models.Count > 6 ? ", …" : "")})",
            };
            result.Add(new BuiltPack(op, set.Kind, packed));

            foreach (var w in set.Warnings) plan.Warnings.Add(new(WarningLevel.Warn, w));
            foreach (var m in set.Models.Where(existing.ContainsKey).Take(3))
                plan.Warnings.Add(new(WarningLevel.Warn,
                    $"A {(set.Kind == "ped" ? "ped" : "car")} called \"{m}\" is already in the add-on pack \"{existing[m]}\". Two packs with the same model fight over it: keep one of them."));
        }
        if (result.Count > 0 && index == null)
            plan.Warnings.Add(new(WarningLevel.Info,
                "The loose model files were packed into an add-on pack. If the readme says this mod replaces something the game already has, cancel and use Tools > Add-on packs, or place the files by hand."));
        return result;
    }

    /// <summary>Pack names already in use: registered in the active dlclist or present as a pack folder.</summary>
    HashSet<string> TakenPackNames()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game == null) return taken;
        foreach (var dir in new[] { game.ModsDlcPacksDir, game.OnigiriDlcPacksDir, Path.Combine(game.Root, "update", "x64", "dlcpacks") })
        {
            try
            {
                if (Directory.Exists(dir))
                    foreach (var d in Directory.EnumerateDirectories(dir)) taken.Add(Path.GetFileName(d));
            }
            catch { }
        }
        try
        {
            var target = DlcListTarget.For(game);
            string? xml = null;
            if (target.Kind == DlcListTarget.Onigiri)
            {
                xml = File.ReadAllText(target.Path);
            }
            else
            {
                var list = File.Exists(game.ModsUpdateRpf) ? game.ModsUpdateRpf : game.GameUpdateRpf;
                var bytes = File.Exists(list) ? new RpfService(game).TryRead(list, DlcList.InnerPath) : null;
                if (bytes != null) xml = DlcList.Decode(bytes, out _);
            }
            if (xml != null)
                foreach (var item in DlcList.Items(xml)) taken.Add(item.TrimEnd('/').Split('/', ':').Last());
        }
        catch { }
        return taken;
    }

    /// <summary>Which of these model names an add-on pack already in the game has (model -> pack name).</summary>
    Dictionary<string, string> ExistingPackModels(HashSet<string> models)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (game == null || models.Count == 0) return found;
        var rpf = new RpfService(game);
        foreach (var dir in new[] { game.ModsDlcPacksDir, game.OnigiriDlcPacksDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var pack in Directory.EnumerateDirectories(dir).Take(500))
            {
                var dlc = Path.Combine(pack, "dlc.rpf");
                if (!File.Exists(dlc)) continue;
                try
                {
                    foreach (var archive in WalkArchives(rpf.Open(dlc)))
                        foreach (var e in archive.AllEntries?.OfType<RpfFileEntry>() ?? Enumerable.Empty<RpfFileEntry>())
                        {
                            var stem = Path.GetFileNameWithoutExtension(e.Name);
                            if ((e.NameLower.EndsWith(".yft") || e.NameLower.EndsWith(".ydd")) && models.Contains(stem))
                                found.TryAdd(stem, Path.GetFileName(pack));
                        }
                }
                catch { }
                finally { rpf.Forget(dlc); }
            }
        }
        return found;
    }

    static IEnumerable<RpfFile> WalkArchives(RpfFile f)
    {
        yield return f;
        if (f.Children == null) yield break;
        foreach (var c in f.Children)
            foreach (var x in WalkArchives(c)) yield return x;
    }

    /// <summary>Configs, data folders and plain libraries go wherever the main file of their folder went.</summary>
    void AttachToSibling(ModFile f, List<ModFile> files)
    {
        if (IsDoc(f) && IsReadmeName(f.Name)) return;

        // walk up the folders until one holds an installed "primary" file
        var dirParts = f.Parts.Take(f.Parts.Length - 1).ToArray();
        for (int depth = dirParts.Length; depth >= 0; depth--)
        {
            var dir = string.Join('/', dirParts.Take(depth));
            var primary = files.Where(o => o.Op is { Kind: OpKind.CopyFile } && o.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase) && IsPrimary(o))
                .OrderBy(o => PrimaryRank(o)).FirstOrDefault();
            if (primary == null) continue;

            var targetDir = Path.GetDirectoryName(primary.Op!.Target) ?? "";
            var relFromDir = string.Join('\\', f.Parts.Skip(depth));
            var kind = Path.GetExtension(f.Name).ToLowerInvariant();
            if (MediaExt.Contains(kind) && depth == dirParts.Length && files.Count(o => o.Dir == f.Dir && MediaExt.Contains(o.Ext)) > 2)
                return; // screenshots next to the main file, not data
            if (IsDoc(f) && depth == dirParts.Length) return;
            f.Op = Copy(f, Path.Combine(targetDir, relFromDir), $"Goes with {primary.Name}");
            return;
        }
    }

    static bool IsPrimary(ModFile f) =>
        f.Ext.Equals(".asi", StringComparison.OrdinalIgnoreCase) || f.Ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
        || f.Ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) || f.Ext.Equals(".vb", StringComparison.OrdinalIgnoreCase);

    static int PrimaryRank(ModFile f) => f.Ext.ToLowerInvariant() switch { ".asi" => 1, ".dll" => 0, _ => 2 };

    // ---------------------------------------------------------------- variants

    void EditionVariants(InstallPlan plan, List<ModFile> files)
    {
        // sibling folders named for game versions ("Legacy", "Enhanced", "Gen9 / E&E")
        var dirs = files.Where(f => f.Op!.Kind != OpKind.Skip).SelectMany(f => Ancestors(f.Dir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byParent = dirs.Where(d => d.Length > 0).GroupBy(Parent, StringComparer.OrdinalIgnoreCase);
        foreach (var g in byParent)
        {
            var kids = g.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (kids.Count < 2 || !kids.Any(k => EditionWord.IsMatch(k.Split('/')[^1]))) continue;
            AddFolderVariant(plan, files, g.Key, kids, "Game version");
        }
    }

    void CollisionVariants(InstallPlan plan, List<ModFile> files)
    {
        for (int pass = 0; pass < 12; pass++)
        {
            var active = files.Where(f => f.Op!.Kind != OpKind.Skip).ToList();
            // a pair writing the same target that could still be selected together
            (ModFile A, ModFile B)? clash = null;
            foreach (var g in active.GroupBy(f => TargetKey(f.Op!), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var list = g.ToList();
                for (int i = 0; i < list.Count && clash == null; i++)
                    for (int j = i + 1; j < list.Count && clash == null; j++)
                        if (!Separated(list[i], list[j])) clash = (list[i], list[j]);
                if (clash != null) break;
            }
            if (clash == null) return;

            // the folder where their paths split is where the choice is made
            var a = clash.Value.A.Parts; var b = clash.Value.B.Parts;
            int common = 0;
            while (common < a.Length - 1 && common < b.Length - 1 && a[common].Equals(b[common], StringComparison.OrdinalIgnoreCase)) common++;
            var parent = string.Join('/', a.Take(common));
            var kids = active.Where(f => f.Parts.Length > common + 1 && IsUnder(f.Dir, parent))
                .Select(f => Join(parent, f.Parts[common])).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (kids.Count < 2 || plan.Variants.Any(v => v.Id == "dir~" + parent))
            {
                // same folder, same target (e.g. two copies of one file): keep the first
                clash.Value.B.Op = Skip(clash.Value.B, "Duplicate of " + clash.Value.A.Rel);
                continue;
            }
            AddFolderVariant(plan, files, parent, kids, "Choose a version");
        }
    }

    static string TargetKey(InstallOp op) => op.Kind + "|" + op.Target;

    /// <summary>True when the two ops sit in different options of some choice, so they never install together.</summary>
    static bool Separated(ModFile x, ModFile y)
    {
        static Dictionary<string, string> Tags(ModFile f) =>
            (f.Op!.Variant ?? "").Split('|').Select(t => t.Split(':', 2)).Where(t => t.Length == 2)
                .GroupBy(t => t[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var tx = Tags(x); var ty = Tags(y);
        return tx.Any(kv => ty.TryGetValue(kv.Key, out var o) && o != kv.Value);
    }

    void AddFolderVariant(InstallPlan plan, List<ModFile> files, string parent, List<string> kids, string title)
    {
        var id = "dir~" + parent;
        if (plan.Variants.Any(v => v.Id == id)) return;
        var group = new VariantGroup { Id = id, Title = parent.Length == 0 ? title : $"{title} ({parent.Split('/')[^1]})" };
        foreach (var k in kids.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var label = k.Split('/')[^1];
            group.Options.Add(new VariantOption { Id = label, Label = label });
            foreach (var f in files.Where(f => IsUnder(f.Dir, k) && f.Op != null))
                f.Op!.Variant = string.IsNullOrEmpty(f.Op.Variant) ? $"{id}:{label}" : f.Op.Variant + $"|{id}:{label}";
        }
        PickRecommended(group, group.Options.Select(o => o.Id).ToList());
        plan.Variants.Add(group);
    }

    void PickRecommended(VariantGroup group, List<string> ids)
    {
        int Score(string name)
        {
            var n = name.ToLowerInvariant();
            int s = 0;
            bool saysEnhanced = Regex.IsMatch(n, @"enhanced|gen ?9|e ?& ?e|next[ -]?gen|expanded");
            bool saysLegacy = Regex.IsMatch(n, @"legacy|old[ -]?gen");
            if (game != null)
            {
                if (game.Edition == GameEdition.Enhanced) s += saysEnhanced ? 20 : saysLegacy ? -20 : 0;
                else s += saysLegacy ? 20 : saysEnhanced ? -20 : 0;
            }
            if (Regex.IsMatch(n, @"add[ -]?on")) s += 5;
            if (Regex.IsMatch(n, @"replace")) s -= 1;
            if (Regex.IsMatch(n, @"optional|extra|alt")) s -= 3;
            if (Regex.IsMatch(n, @"default|standard|normal|original|main|recommended|lore")) s += 2;
            return s;
        }
        var best = ids.Select((id, i) => (id, i, s: Score(id))).OrderByDescending(t => t.s).ThenBy(t => t.i).First().id;
        group.Selected = best;
        foreach (var o in group.Options) o.Recommended = o.Id == best;
    }

    void OptionalFolders(InstallPlan plan, List<ModFile> files)
    {
        var installable = files.Where(f => f.Op!.Kind != OpKind.Skip).ToList();
        foreach (var f in installable)
        {
            if (f.Parts.Take(f.Parts.Length - 1).Any(p => OptionalFolder.IsMatch(p)) && installable.Any(o => !o.Parts.Take(o.Parts.Length - 1).Any(p => OptionalFolder.IsMatch(p))))
            {
                f.Op!.Enabled = false;
                f.Op.Reason += " - optional extra, off by default";
            }
        }
    }

    /// <summary>
    /// Some mods (OpenRPF, ASI packs) bundle their own ASI loader as dsound.dll / version.dll / winmm.dll.
    /// Next to Script Hook V's loader that loads every .asi twice, so it's left out unless asked for.
    /// </summary>
    void ExtraAsiLoaders(InstallPlan plan)
    {
        if (game == null || !File.Exists(Path.Combine(game.Root, game.AsiLoaderName))) return;
        foreach (var op in plan.Ops.Where(o => o.Kind == OpKind.CopyFile && o.Enabled))
        {
            if (op.Target.Equals("dsound.dll", StringComparison.OrdinalIgnoreCase)
                || op.Target.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                || op.Target.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase))
            {
                op.Enabled = false;
                op.Reason = $"Extra ASI loader: you already have Script Hook V's {game.AsiLoaderName}, and two loaders load every plugin twice";
            }
        }
    }

    // ---------------------------------------------------------------- finishing

    void Finish(InstallPlan plan, List<ModFile> files)
    {
        plan.Readmes = files.Where(f => IsDoc(f) && (f.Ext is ".txt" or ".md" or ".TXT" or ".MD" || IsReadmeName(f.Name)))
            .Select(f => f.Rel).ToList();

        var active = plan.Ops.Where(o => o.Kind != OpKind.Skip).ToList();
        var req = new HashSet<string>();
        foreach (var op in active)
        {
            var t = op.Target.ToLowerInvariant();
            switch (op.Kind)
            {
                case OpKind.DlcPack:
                case OpKind.RpfFile:
                    req.Add(Prerequisites.ModsFolder); break;
                case OpKind.Oiv:
                    req.Add(Prerequisites.ModsFolder); break;
                case OpKind.CopyFile:
                    if (t.StartsWith("scripts\\") && (t.EndsWith(".dll") || t.EndsWith(".cs") || t.EndsWith(".vb"))) { req.Add(Prerequisites.Shvdn); req.Add(Prerequisites.ScriptHookV); }
                    if (t.EndsWith(".asi") && !t.Contains("openiv") && !t.Contains("openrpf")) { req.Add(Prerequisites.ScriptHookV); req.Add(Prerequisites.AsiLoader); }
                    if (t.StartsWith("plugins\\lspdfr\\") && t.EndsWith(".dll")) { req.Add(Prerequisites.Lspdfr); req.Add(Prerequisites.RagePluginHook); }
                    else if (t.StartsWith("plugins\\") && t.EndsWith(".dll")) req.Add(Prerequisites.RagePluginHook);
                    if (t.StartsWith("els\\")) req.Add(Prerequisites.Els);
                    if (t.StartsWith("menyoostuff\\")) req.Add(Prerequisites.Menyoo);
                    if (t.StartsWith("mods\\")) req.Add(Prerequisites.ModsFolder);
                    break;
            }
        }
        // frameworks the mod ships itself aren't requirements
        if (active.Any(o => o.Target.Equals("ScriptHookV.dll", StringComparison.OrdinalIgnoreCase))) { req.Remove(Prerequisites.ScriptHookV); req.Remove(Prerequisites.AsiLoader); }
        if (active.Any(o => o.Target.Equals("ScriptHookVDotNet.asi", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.Shvdn);
        if (active.Any(o => o.Target.Equals("RAGEPluginHook.exe", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.RagePluginHook);
        if (active.Any(o => o.Target.Equals(@"plugins\LSPD First Response.dll", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.Lspdfr);
        if (active.Any(o => o.Target.Equals("ELS.asi", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.Els);
        if (active.Any(o => o.Target.Equals("Menyoo.asi", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.Menyoo);
        if (active.Any(o => o.Target.EndsWith("OpenIV.asi", StringComparison.OrdinalIgnoreCase) || o.Target.EndsWith("OpenRPF.asi", StringComparison.OrdinalIgnoreCase))) req.Remove(Prerequisites.ModsFolder);
        plan.Requires = req.ToList();

        plan.Summary = Summarize(active);

        if (active.Count == 0)
            plan.Warnings.Add(new(WarningLevel.Block, "Nothing in this download could be placed automatically. Check its readme."));
        if (active.Any(o => o.Kind == OpKind.SaveGame))
            plan.Warnings.Add(new(WarningLevel.Warn, "This is a save game: it replaces the save slot with the same number (your old one is backed up)."));
        if (plan.Ops.Any(o => o.Kind == OpKind.Skip && o.Reason.StartsWith("Game file")))
            plan.Warnings.Add(new(WarningLevel.Warn, "Some game files couldn't be placed: " +
                string.Join(", ", plan.Ops.Where(o => o.Kind == OpKind.Skip && o.Reason.StartsWith("Game file")).Take(5).Select(o => Path.GetFileName(o.SourceRel)))));
        if (plan.Ops.Any(o => o.Kind == OpKind.Skip && o.Reason.StartsWith("Installer program")))
            plan.Warnings.Add(new(WarningLevel.Info, "The mod ships its own installer program. Everything the launcher could place is listed; check the readme for anything else."));
        if (game != null && active.Any(o => o.Kind is OpKind.DlcPack) && !game.HasOnigiri && !File.Exists(game.ModsUpdateRpf))
            plan.Warnings.Add(new(WarningLevel.Info, $"First add-on pack: the launcher copies update.rpf into the mods folder once ({SizeOf(game.GameUpdateRpf)}), so the game's own files stay untouched."));
    }

    static string SizeOf(string file)
    {
        try { return $"{new FileInfo(file).Length / (1024.0 * 1024 * 1024):0.0} GB"; } catch { return "a few GB"; }
    }

    static string Summarize(List<InstallOp> ops)
    {
        if (ops.Count == 0) return "Nothing to install";
        var t = ops.Select(o => (o.Kind, o.Target.ToLowerInvariant())).ToList();
        if (t.Any(x => x.Kind == OpKind.Oiv)) return "OpenIV package";
        if (t.Any(x => x.Item2 == @"plugins\lspd first response.dll")) return "LSPDFR";
        if (t.Any(x => x.Item2 == "scripthookv.dll")) return "Script Hook V";
        if (t.Any(x => x.Item2 == "scripthookvdotnet.asi")) return "Script Hook V .NET";
        if (t.Any(x => x.Item2 == "ragepluginhook.exe")) return "RAGE Plugin Hook";
        if (t.Any(x => x.Item2.StartsWith(@"plugins\lspdfr\") && x.Item2.EndsWith(".dll"))) return "LSPDFR plugin";
        if (t.Any(x => x.Item2.StartsWith(@"plugins\") && x.Item2.EndsWith(".dll"))) return "RAGE plugin";
        if (t.Any(x => x.Kind == OpKind.DlcPack)) return t.Any(x => x.Item2.StartsWith("els\\")) ? "Add-on vehicle (ELS)" : "Add-on DLC pack";
        if (t.Any(x => x.Kind == OpKind.RpfFile)) return "Replace mod";
        if (t.Any(x => x.Kind == OpKind.SaveGame)) return "Save game";
        if (t.Any(x => x.Item2.StartsWith("scripts\\") && (x.Item2.EndsWith(".dll") || x.Item2.EndsWith(".cs")))) return "Script (.NET)";
        if (t.Any(x => x.Item2.EndsWith(".asi"))) return "ASI plugin";
        if (t.Any(x => x.Item2.StartsWith("menyoostuff\\"))) return "Menyoo content";
        if (t.Any(x => x.Item2.StartsWith("els\\"))) return "ELS config";
        return "Game files";
    }

    // ---------------------------------------------------------------- op builders

    static InstallOp Copy(ModFile f, string target, string reason) =>
        new() { Kind = OpKind.CopyFile, Source = f.Full, SourceRel = f.Rel, Target = target.Replace('/', '\\').TrimStart('\\'), Reason = reason };

    static InstallOp Skip(ModFile f, string reason) =>
        new() { Kind = OpKind.Skip, Source = f.Full, SourceRel = f.Rel, Target = "", Reason = reason, Enabled = false };

    static InstallOp RpfOp(ModFile f, string archive, string inner, string reason) =>
        new() { Kind = OpKind.RpfFile, Source = f.Full, SourceRel = f.Rel, Target = archive.Replace('/', '\\') + "|" + inner.Replace('\\', '/'), Reason = reason };

    public static (string Archive, string Inner) SplitRpfTarget(string target)
    {
        var i = target.IndexOf('|');
        return (target.Substring(0, i), target.Substring(i + 1));
    }

    InstallOp DlcPackOp(ModFile f, string reason)
    {
        var name = PackName(f);
        return new InstallOp { Kind = OpKind.DlcPack, Source = f.Full, SourceRel = f.Rel, Target = name, Reason = $"{reason} \"{name}\"" };
    }

    /// <summary>Pack folder name: setup2.xml's nameHash when the archive is readable, else the folder it came in.</summary>
    static string PackName(ModFile f)
    {
        string? fromSetup = null;
        try
        {
            var rpf = new RpfFile(f.Full, f.Name);
            rpf.ScanStructure(null, null);
            var setup = rpf.Root?.Files.FirstOrDefault(e => e.NameLower == "setup2.xml");
            if (setup != null)
            {
                var data = rpf.ExtractFile(setup);
                if (data != null)
                {
                    var doc = new XmlDocument();
                    doc.LoadXml(System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿'));
                    fromSetup = doc.SelectSingleNode("//nameHash")?.InnerText?.Trim();
                    if (string.IsNullOrEmpty(fromSetup))
                    {
                        var dev = doc.SelectSingleNode("//deviceName")?.InnerText?.Trim();
                        if (dev != null && dev.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)) fromSetup = dev.Substring(4);
                    }
                }
            }
        }
        catch { }
        var raw = !string.IsNullOrWhiteSpace(fromSetup) ? fromSetup! : (f.Parts.Length > 1 ? f.Parts[^2] : "addonpack");
        var clean = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9_]", "");
        return clean.Length == 0 ? "addonpack" : clean;
    }

    string? ResolveArchive(string[] partsToRpf)
    {
        // drop wrapper folders before the first real game path piece
        var start = Array.FindIndex(partsToRpf, p => RootAnchors.Contains(p) || p.Equals("x64", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase));
        var parts = partsToRpf.Skip(Math.Max(0, start)).ToList();
        if (parts.Count > 0 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        var rel = string.Join('\\', parts);
        var rpfName = parts[^1].ToLowerInvariant();

        if (parts.Count == 1)
        {
            if (rpfName == "update.rpf" || rpfName == "update2.rpf") rel = "update\\" + rpfName;
            else if (rpfName == "dlc.rpf") return null;
        }
        if (game == null) return rel;
        if (File.Exists(game.Resolve(rel)) || File.Exists(Path.Combine(game.ModsDir, rel))) return rel;
        // try the usual homes
        foreach (var guess in new[] { rpfName, "update\\" + rpfName, "x64\\" + rpfName })
            if (File.Exists(game.Resolve(guess))) return guess;
        return rel;
    }

    static readonly HashSet<string> TextData = new(StringComparer.OrdinalIgnoreCase) { ".meta", ".xml", ".dat", ".txt", ".ymt" };

    (string? Best, double Score) BestByContent(string modFile, List<string> candidates)
    {
        var mine = Tokens(File.ReadAllBytes(modFile));
        if (mine.Count == 0) return (null, 0);
        string? best = null;
        double bestScore = 0;
        foreach (var c in candidates.Take(60))
        {
            byte[]? data;
            try { data = index!.Read(c); } catch { continue; }
            if (data == null) continue;
            var theirs = Tokens(data);
            if (theirs.Count == 0) continue;
            // share of the mod's identifiers found in this copy
            double score = (double)mine.Count(theirs.Contains) / mine.Count;
            // on a tie the base game's update.rpf copy wins: that's where whole-game data edits belong
            bool tieToUpdate = score == bestScore && score > 0 && c.StartsWith(@"update\update.rpf|", StringComparison.OrdinalIgnoreCase);
            if (score > bestScore || tieToUpdate) { bestScore = score; best = c; }
        }
        return (best, bestScore);
    }

    /// <summary>Identifier-like values from an XML/meta file (model names, handling names, hashes).</summary>
    static HashSet<string> Tokens(byte[] data)
    {
        var text = System.Text.Encoding.UTF8.GetString(data);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, @">\s*([A-Za-z_][A-Za-z0-9_]{2,40})\s*<|value=""([A-Za-z_][A-Za-z0-9_]{2,40})"""))
        {
            var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!Regex.IsMatch(v, "^(true|false|null|item|none)$", RegexOptions.IgnoreCase)) set.Add(v);
        }
        return set;
    }

    // ---------------------------------------------------------------- small helpers

    static bool IsDoc(ModFile f) => DocExt.Contains(f.Ext);
    static bool IsReadmeName(string n) => Regex.IsMatch(n, @"read ?me|change ?log|licen[sc]e|install|credits|instructions|how ?to|notes|faq|license", RegexOptions.IgnoreCase) || DocExt.Contains(Path.GetExtension(n));

    static bool IsSourceCode(ModFile f, List<ModFile> files) =>
        f.Parts.Any(p => Regex.IsMatch(p, @"^(src|source|source ?code|sourcecode)$", RegexOptions.IgnoreCase))
        || files.Any(o => o.Dir.Equals(f.Dir, StringComparison.OrdinalIgnoreCase) && (o.Ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || o.Ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)))
        || f.Name.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);

    static string? XmlRoot(string path)
    {
        try
        {
            using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true });
            while (r.Read()) if (r.NodeType == XmlNodeType.Element) return r.LocalName;
        }
        catch { }
        return null;
    }

    static bool XmlHasChild(string path, string child)
    {
        try
        {
            using var r = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            int depth = 0;
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.Depth == 1 && r.LocalName == child) return true;
                if (++depth > 50) break;
            }
        }
        catch { }
        return false;
    }

    static string Parent(string dir) => dir.Contains('/') ? dir.Substring(0, dir.LastIndexOf('/')) : "";
    static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;
    static bool IsUnder(string dir, string root) =>
        root.Length == 0 || dir.Equals(root, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Ancestors(string dir)
    {
        var parts = dir.Length == 0 ? Array.Empty<string>() : dir.Split('/');
        for (int i = 1; i <= parts.Length; i++) yield return string.Join('/', parts, 0, i);
    }
}
