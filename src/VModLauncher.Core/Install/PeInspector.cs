using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace VModLauncher.Core.Install;

public enum DllKind
{
    Native,
    DotNetOther,
    /// <summary>References ScriptHookVDotNet: goes in scripts\.</summary>
    Shvdn,
    /// <summary>Has [assembly: Rage.Attributes.Plugin]: goes in plugins\.</summary>
    RphPlugin,
    /// <summary>Has a class deriving LSPD_First_Response.Mod.API.Plugin: goes in plugins\LSPDFR\.</summary>
    LspdfrPlugin,
    /// <summary>References Rage / LSPDFR but isn't a plugin itself (RAGENativeUI, APIs): goes in the game root.</summary>
    RphLibrary,
}

/// <summary>Reads .NET metadata to tell where a .dll belongs, instead of guessing from folder names.</summary>
public static class PeInspector
{
    public static DllKind Inspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return DllKind.Native;
            var md = pe.GetMetadataReader();

            var refs = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var self = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "";

            bool refsLspdfr = refs.Contains("LSPD First Response");
            bool refsRage = refs.Contains("RagePluginHook") || refs.Contains("RagePluginHookSDK") || refs.Contains("Rage");
            bool refsShvdn = refs.Any(r => r.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase));

            if (refsLspdfr && DerivesFrom(md, "LSPD_First_Response.Mod.API", "Plugin")) return DllKind.LspdfrPlugin;
            if (refsRage && HasAssemblyAttribute(md, "Rage.Attributes", "PluginAttribute")) return DllKind.RphPlugin;
            if (refsLspdfr || refsRage) return DllKind.RphLibrary;
            if (refsShvdn) return DllKind.Shvdn;
            if (self.StartsWith("ScriptHookVDotNet", StringComparison.OrdinalIgnoreCase)) return DllKind.DotNetOther;
            return DllKind.DotNetOther;
        }
        catch
        {
            return DllKind.Native;
        }
    }

    static bool DerivesFrom(MetadataReader md, string ns, string name)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            if (td.BaseType.IsNil || td.BaseType.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)td.BaseType);
            if (md.GetString(tr.Name) == name && md.GetString(tr.Namespace) == ns) return true;
        }
        return false;
    }

    static bool HasAssemblyAttribute(MetadataReader md, string ns, string name)
    {
        if (!md.IsAssembly) return false;
        foreach (var ah in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(ah);
            EntityHandle typeHandle = attr.Constructor.Kind switch
            {
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent,
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType(),
                _ => default,
            };
            if (typeHandle.Kind != HandleKind.TypeReference) continue;
            var tr = md.GetTypeReference((TypeReferenceHandle)typeHandle);
            if (md.GetString(tr.Name) == name && md.GetString(tr.Namespace) == ns) return true;
        }
        return false;
    }
}
