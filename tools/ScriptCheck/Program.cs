using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// ScriptCheck <script.dll> <folder with ScriptHookVDotNet3.dll> [more folders to find references in]
// JIT-compiles every method of a Script Hook V .NET script against that Script Hook V .NET, the way the game would
// when the method first runs, so a call to something that build doesn't have (a newer nightly's API, a missing
// library) shows up now instead of as "Unhandled exception in script" in game. Exit code 0 = every method compiled.
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: ScriptCheck <script.dll> <folder with ScriptHookVDotNet3.dll> [reference folders...]");
            return 2;
        }
        string dll = Path.GetFullPath(args[0]), hook = Path.GetFullPath(args[1]);
        var folders = new[] { hook, Path.GetDirectoryName(dll) }.Concat(args.Skip(2).Select(Path.GetFullPath)).ToArray();
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string n = new AssemblyName(e.Name).Name + ".dll";
            foreach (string d in folders)
                if (File.Exists(Path.Combine(d, n))) return Assembly.LoadFrom(Path.Combine(d, n));
            return null;
        };
        string shvdnPath = Path.Combine(hook, "ScriptHookVDotNet3.dll");
        if (!File.Exists(shvdnPath))
        {
            Console.WriteLine("no ScriptHookVDotNet3.dll in " + hook);
            return 2;
        }
        Assembly shv = Assembly.LoadFrom(shvdnPath);
        Console.WriteLine("hook: ScriptHookVDotNet3 " + shv.GetName().Version + "  " + hook);
        Assembly asm = Assembly.LoadFrom(dll);
        Console.WriteLine("script: " + asm.GetName().Name + " " + asm.GetName().Version);
        int ok = 0, bad = 0;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray();
            foreach (var le in ex.LoaderExceptions) { Console.WriteLine("TYPE LOAD: " + le.Message); bad++; }
        }
        foreach (Type t in types)
        {
            if (t.ContainsGenericParameters) continue;
            var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                .Concat(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
            foreach (MethodBase m in methods)
            {
                if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); ok++; }
                catch (Exception ex) { bad++; Console.WriteLine("FAIL " + t.FullName + "." + m.Name + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        Console.WriteLine("prepared " + ok + ", failed " + bad);
        return bad == 0 ? 0 : 1;
    }
}
