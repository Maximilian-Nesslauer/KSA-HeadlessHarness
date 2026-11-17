using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using HeadlessHarness.Core;
using KSA;

namespace HeadlessHarness.Harness;

// Runs the StarMap lifecycle hooks of a deployed mod inside a harness run. StarMap applies the
// patches that call them (the ModLibrary.LoadAll postfix for [StarMapAllModsLoaded], the
// Mod.PrepareSystems prefix for [StarMapImmediateLoad]) only after every [StarMapBeforeMain] method
// has run, and the harness brings the game up and exits inside its own, so a mod under test never
// gets its hooks called and its Harmony patches are never applied. InvokeAllModsLoaded does what
// StarMap does: it finds the class marked [StarMapMod] in the mod's entry assembly, creates one
// instance, and calls its [StarMapImmediateLoad] methods with the mod's KSA.Mod and then its
// [StarMapAllModsLoaded] methods. Unload calls the [StarMapUnload] methods on that instance.
//
// The calls are reference counted per mod id: only the first InvokeAllModsLoaded calls the load
// hooks, and only the Unload that balances the last one calls the unload hooks, so tests that each
// load and unload the mod in try/finally nest correctly. An Unload without a load does nothing. The
// runner unloads every mod still loaded at the end of the run, as StarMap does at game exit.
//
// The entry assembly is the copy the harness load context resolves, which is the copy a consumer
// test binds to, so the hooks patch the same types the test reads.
public static class HarnessMods
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LoadedMod> Loaded = new(StringComparer.Ordinal);

    private sealed class LoadedMod
    {
        public required object Instance { get; init; }
        public required List<MethodInfo> UnloadHooks { get; init; }
        public int References { get; set; }
    }

    public static bool IsLoaded(string modId)
    {
        lock (Gate)
            return Loaded.ContainsKey(modId);
    }

    public static void InvokeAllModsLoaded(string modId)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(modId, out LoadedMod? existing))
            {
                existing.References++;
                HarnessLog.Line($"[harness] mod '{modId}' already loaded ({existing.References} reference(s)).");
                return;
            }

            string tomlPath = FindModToml(modId);
            string entryName = ReadEntryAssembly(tomlPath) ?? modId;
            Assembly assembly = ResolveEntryAssembly(entryName, Path.Combine(Path.GetDirectoryName(tomlPath)!, entryName + ".dll"));
            Type modType = FindModType(assembly, modId);
            object instance = Activator.CreateInstance(modType)
                ?? throw new InvalidOperationException($"[HeadlessHarness] could not create the mod class {modType.FullName}.");

            List<MethodInfo> immediate = Hooks(modType, "StarMapImmediateLoadAttribute",
                m => m.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(KSA.Mod));
            List<MethodInfo> allLoaded = Hooks(modType, "StarMapAllModsLoadedAttribute", m => m.GetParameters().Length == 0);
            List<MethodInfo> unload = Hooks(modType, "StarMapUnloadAttribute", m => m.GetParameters().Length == 0);

            LoadedMod loaded = new() { Instance = instance, UnloadHooks = unload, References = 1 };
            try
            {
                if (immediate.Count > 0)
                {
                    KSA.Mod definingMod = ModLibrary.Find(modId) ?? KSA.Mod.MakeUsing(modId, tomlPath);
                    foreach (MethodInfo hook in immediate)
                        Invoke(modId, hook, instance, new object[] { definingMod });
                }
                foreach (MethodInfo hook in allLoaded)
                    Invoke(modId, hook, instance, Array.Empty<object>());
            }
            catch
            {
                // A hook that threw may have applied part of its patches; the unload hooks are the
                // mod's own way to take them off again.
                RunUnloadHooks(modId, loaded, rethrow: false);
                throw;
            }
            Loaded[modId] = loaded;
            HarnessLog.Line($"[harness] mod '{modId}' loaded: {modType.FullName} from '{assembly.Location}', " +
                            $"{immediate.Count} immediate-load and {allLoaded.Count} all-mods-loaded hook(s) called.");
        }
    }

    public static void Unload(string modId)
    {
        lock (Gate)
        {
            if (!Loaded.TryGetValue(modId, out LoadedMod? loaded))
            {
                HarnessLog.Line($"[harness] mod '{modId}' is not loaded; nothing to unload.");
                return;
            }
            if (--loaded.References > 0)
                return;
            Loaded.Remove(modId);
            RunUnloadHooks(modId, loaded, rethrow: true);
            HarnessLog.Line($"[harness] mod '{modId}' unloaded ({loaded.UnloadHooks.Count} unload hook(s) called).");
        }
    }

    // The mod ids still loaded, with their reference counts.
    internal static IReadOnlyList<string> Describe()
    {
        lock (Gate)
            return Loaded.Select(kv => $"{kv.Key} ({kv.Value.References} reference(s))").ToList();
    }

    // Returns the number of mods whose unload hooks threw.
    internal static int UnloadAll()
    {
        int failures = 0;
        lock (Gate)
        {
            foreach (string modId in Loaded.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
            {
                LoadedMod loaded = Loaded[modId];
                Loaded.Remove(modId);
                try
                {
                    RunUnloadHooks(modId, loaded, rethrow: true);
                    HarnessLog.Line($"[harness] mod '{modId}' unloaded at run end.");
                }
                catch (Exception e)
                {
                    failures++;
                    HarnessLog.Line($"[harness] FAIL: unload hook of mod '{modId}' threw at run end:\n{e}");
                }
            }
        }
        return failures;
    }

    private static void RunUnloadHooks(string modId, LoadedMod loaded, bool rethrow)
    {
        Exception? first = null;
        foreach (MethodInfo hook in loaded.UnloadHooks)
        {
            try
            {
                Invoke(modId, hook, loaded.Instance, Array.Empty<object>());
            }
            catch (Exception e)
            {
                first ??= e;
                if (!rethrow)
                    HarnessLog.Line($"[harness] unload hook of mod '{modId}' threw while cleaning up a failed load: {e.Message}");
            }
        }
        if (rethrow && first != null)
            throw first;
    }

    private static void Invoke(string modId, MethodInfo hook, object instance, object[] args)
    {
        try
        {
            hook.Invoke(hook.IsStatic ? null : instance, args);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw new InvalidOperationException(
                $"[HeadlessHarness] hook {hook.DeclaringType?.Name}.{hook.Name} of mod '{modId}' threw: {e.InnerException.Message}",
                e.InnerException);
        }
    }

    // StarMap matches its attributes by type name, not by type identity (RuntimeMod.InitializeMod), so
    // a mod built against another StarMap.API copy still counts. The signature checks are those of
    // the attributes' IsValidSignature, which also accepts static methods.
    private static List<MethodInfo> Hooks(Type modType, string attributeName, Func<MethodInfo, bool> signature)
    {
        List<MethodInfo> hooks = new();
        foreach (MethodInfo method in modType.GetMethods())
        {
            if (method.ReturnType != typeof(void))
                continue;
            if (!method.GetCustomAttributes().Any(a => a.GetType().Name == attributeName))
                continue;
            if (signature(method))
                hooks.Add(method);
            else
                HarnessLog.Line($"[harness] mod hook {modType.Name}.{method.Name} has the wrong signature for {attributeName}; StarMap skips it too.");
        }
        return hooks;
    }

    // StarMap looks in the game's Content folder first and then in the profile's mods folder
    // (RuntimeMod.TryCreateMod); the game runs with its install folder as the working directory.
    private static string FindModToml(string modId)
    {
        string[] candidates =
        {
            Path.Combine("Content", modId, "mod.toml"),
            Path.Combine(ModLibrary.LocalModsFolderPath, modId, "mod.toml"),
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException(
            $"[HeadlessHarness] mod '{modId}' has no mod.toml in the game's Content folder or in {ModLibrary.LocalModsFolderPath}.");
    }

    private static readonly Regex SectionLine = new(@"^\s*\[([^\[\]]+)\]\s*(#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex EntryAssemblyLine = new(@"^\s*EntryAssembly\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant);

    // The [StarMap] table's EntryAssembly, or null when the file does not name one (StarMap then uses
    // the mod id).
    private static string? ReadEntryAssembly(string tomlPath)
    {
        string? section = null;
        foreach (string line in File.ReadLines(tomlPath))
        {
            Match header = SectionLine.Match(line);
            if (header.Success)
            {
                section = header.Groups[1].Value.Trim();
                continue;
            }
            if (section == "StarMap" && EntryAssemblyLine.Match(line) is { Success: true } entry)
                return entry.Groups[1].Value;
        }
        return null;
    }

    private static Assembly ResolveEntryAssembly(string name, string path)
    {
        AssemblyLoadContext alc = AssemblyLoadContext.GetLoadContext(typeof(HarnessMods).Assembly) ?? AssemblyLoadContext.Default;
        Assembly? existing = alc.Assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;
        try
        {
            return alc.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (FileNotFoundException)
        {
            if (!File.Exists(path))
                throw new InvalidOperationException($"[HeadlessHarness] the entry assembly '{name}' was not found at {path}.");
            return alc.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
    }

    private static Type FindModType(Assembly assembly, string modId)
    {
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types;
        }
        foreach (Type? type in types)
        {
            if (type == null)
                continue;
            try
            {
                if (type.GetCustomAttributes().Any(a => a.GetType().Name == "StarMapModAttribute"))
                    return type;
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or CustomAttributeFormatException)
            {
            }
        }
        throw new InvalidOperationException(
            $"[HeadlessHarness] mod '{modId}': no loadable class marked [StarMapMod] in {assembly.GetName().Name}.");
    }
}
