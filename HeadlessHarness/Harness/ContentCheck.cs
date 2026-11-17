using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using KSA;

namespace HeadlessHarness.Harness;

// Reads the content errors the game collected while it loaded the enabled mods: the read errors of
// every mod file, the errors of the full load, and the errors the streamed body textures added
// (CelestialTextureStreamer.LoadStreamed through ModLibrary.AddStreamErrors). The game prints the
// same list to its log (ModLibrary.PrintContentErrors) and goes on, so a broken content mod loads
// with parts or systems missing and nothing fails. A test reads the list here and gates on it.
public static class ContentCheck
{
    // The id of the stock content mod. Its errors belong to the game, not to the mod under test.
    public const string CoreModId = "Core";

    private static PropertyInfo? _fullLoadErrors;
    private static PropertyInfo? _preloadErrors;

    // The errors of the content load, in the order the game recorded them. ModLibrary.FullLoadErrors
    // and ModLibrary.PreloadErrors are internal, so they are read by name. The game prints the full
    // load list and falls back to the preload list only when no full load ran
    // (ModLibrary.ChooseErrorsToPrint); this follows the same rule. Empty before BringUp.
    public static IReadOnlyList<ContentError> LoadErrors()
    {
        _fullLoadErrors ??= AccessTools.Property(typeof(ModLibrary), "FullLoadErrors")
            ?? throw Drift("ModLibrary.FullLoadErrors");
        _preloadErrors ??= AccessTools.Property(typeof(ModLibrary), "PreloadErrors")
            ?? throw Drift("ModLibrary.PreloadErrors");
        ContentErrors? errors = ReadErrors(_fullLoadErrors, "ModLibrary.FullLoadErrors")
            ?? ReadErrors(_preloadErrors, "ModLibrary.PreloadErrors");

        List<ContentError> list = new List<ContentError>(errors?.Count ?? 0);
        if (errors != null)
        {
            for (int i = 0; i < errors.Count; i++)
                list.Add(errors[i]);
        }
        return list;
    }

    // The errors that belong to the named mods, or to every mod except Core when modIds is null.
    // An error the game could not attach to a mod has an empty Mod and counts as not Core.
    public static IReadOnlyList<ContentError> ModErrors(IEnumerable<string>? modIds = null)
    {
        HashSet<string>? wanted = modIds != null ? new HashSet<string>(modIds, StringComparer.Ordinal) : null;
        List<ContentError> list = new List<ContentError>();
        foreach (ContentError error in LoadErrors())
        {
            bool match = wanted != null ? wanted.Contains(error.Mod) : !IsCore(error);
            if (match)
                list.Add(error);
        }
        return list;
    }

    public static bool IsCore(ContentError error) => string.Equals(error.Mod, CoreModId, StringComparison.Ordinal);

    // The errors of the list that only a run without a renderer has. ShaderReference.Load throws
    // "Renderer is null" when Program.GetRenderer answers null, so every shader fails to load
    // headless, and every content that refers to a failed item then fails in turn as FailedRef
    // (ContentResolver names the failed item as <Type> "<Id>" failed to load). The chain is followed
    // until no further error joins it.
    public static HashSet<ContentError> HeadlessOnly(IReadOnlyList<ContentError> errors)
    {
        HashSet<ContentError> headless = new HashSet<ContentError>();
        HashSet<string> failedTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentError error in errors)
        {
            if (error.Kind == ContentErrorKind.LoadFailed && error.Tag == "Shader"
                && error.Detail.Contains("Renderer is null compiling shader '", StringComparison.Ordinal))
            {
                headless.Add(error);
                AddFailedTarget(error, failedTargets);
            }
        }

        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (ContentError error in errors)
            {
                if (error.Kind != ContentErrorKind.FailedRef || headless.Contains(error))
                    continue;
                if (!failedTargets.Contains(error.Detail))
                    continue;
                headless.Add(error);
                if (AddFailedTarget(error, failedTargets))
                    grew = true;
            }
        }
        return headless;
    }

    // ContentResolver.PropagateFailures names the CLR type as well as the id. Different registries
    // can reuse an id, so matching the id alone can hide an unrelated broken reference.
    private static bool AddFailedTarget(ContentError error, HashSet<string> targets) =>
        !string.IsNullOrEmpty(error.Id) && ModLibrary.TagTable.TryGet(error.Tag, out ContentTagBinding binding)
            && targets.Add($"{binding.Type.Name} \"{error.Id}\" failed to load");

    // One line with the kind, mod, file, tag and id, in the format the game logs
    // (ContentErrorFormatter.Format).
    public static string Describe(ContentError error) => ContentErrorFormatter.Format(error);

    private static ContentErrors? ReadErrors(PropertyInfo property, string name)
    {
        object? value = property.GetValue(null);
        if (value != null && value is not ContentErrors)
            throw new InvalidOperationException(
                $"[HeadlessHarness] {name} is {value.GetType().Name}, expected ContentErrors - game version may have changed.");
        return (ContentErrors?)value;
    }

    private static Exception Drift(string what) =>
        new InvalidOperationException($"[HeadlessHarness] {what} not found - game version may have changed.");
}
