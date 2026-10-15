using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.Pointers;
using HarmonyLib;
using Verse;

namespace FcitxCjkInput;

/// <summary>
/// RimWorld 1.7 picks CJK fallback fonts by fixed file names (RimWorldWaylith.Imgui.FontFallbacks),
/// so CJK text renders as boxes when only other files exist, e.g. Fedora's default NotoSansCJK-VF.ttc.
/// Adds the fonts fontconfig picks for CJK languages to the engine's fallback list.
/// </summary>
internal static class CjkFontFallback {
    /// <summary>The engine's Han fallbacks; its Hangul-only NanumGothic leaves Han text as boxes.</summary>
    private static readonly string[] KnownCjkFonts = ["CJK", "DroidSansFallback", "wqy"];
    private static readonly Type Fallbacks = AccessTools.TypeByName("RimWorldWaylith.Imgui.FontFallbacks");
    private static readonly Type Source = AccessTools.Inner(Fallbacks, "Source");
    private static readonly uint[] HanRanges = (uint[])AccessTools.Field(Fallbacks, "HanRanges").GetValue(null)!;
    private static readonly MethodInfo FallbackFileData = AccessTools.Method("RimWorldWaylith.Imgui.ImGuiHost:FallbackFileData");

    private static bool applied;

    internal static void Patch(Harmony harmony) {
        harmony.Patch(AccessTools.Method(Fallbacks, "Resolve"), postfix: new HarmonyMethod(AfterResolve));
        harmony.Patch(AccessTools.Method("RimWorldWaylith.Imgui.ImGuiHost:BeginFrame"), prefix: new HarmonyMethod(BeforeFrame));
    }

    /// <summary>Postfix of FontFallbacks.Resolve: inserts fontconfig's CJK fonts after the script-excluding symbol fonts.</summary>
    private static void AfterResolve(string? locale, object __result) {
        var sources = (IList)__result;
        if (HasCjk(sources)) {
            return;
        }

        var at = sources.Cast<object>().TakeWhile(source => Field(source, "ExcludeRanges") != null).Count();
        foreach (var (path, fontNo, excludeRanges) in Enumerable.Reverse(Fonts(locale))) {
            sources.Insert(at, Activator.CreateInstance(Source, path, fontNo, excludeRanges));
        }
    }

    /// <summary>Prefix of ImGuiHost.BeginFrame: fixes fonts built before this mod loaded, once between frames.</summary>
    private static void BeforeFrame(object __instance) {
        if (applied) {
            return;
        }

        applied = true;
        try {
            Rebuild(__instance);
        } catch (Exception exception) {
            Log.Error($"[CJK] CJK font fallback failed: {exception}");
        }
    }

    private static void Rebuild(object host) {
        var byLocale = (IDictionary)Traverse.Create(host).Field("fallbacksByLocale").GetValue();
        if (byLocale.Values.Cast<IList>().Any(HasCjk)) {
            return;
        }

        var fonts = Fonts(null);
        if (fonts.Count == 0) {
            Log.Warning("[CJK] fontconfig found no CJK font; CJK text renders as boxes");
            return;
        }

        // Locale and styled fonts are rebuilt lazily from the patched fallbacks; base fonts are merged in place.
        byLocale.Clear();
        Traverse.Create(host).Field<IDictionary>("localeFonts").Value.Clear();
        Traverse.Create(host).Field<IDictionary>("styledFonts").Value.Clear();
        var sizes = Traverse.Create(host).Field<List<float>>("importSizes").Value;
        foreach (var (font, size) in Traverse.Create(host).Field<List<ImFontPtr>>("fonts").Value.Zip(sizes)) {
            // Existing bakes hold loader data per source; discard them while the source count still matches.
            // Discarding also zeroes the engine's fallback and ellipsis characters, which a merge only sets up on the
            // atlas's last font, so missing glyphs would vanish instead of drawing as boxes.
            var (fallbackChar, ellipsisChar) = (font.FallbackChar, font.EllipsisChar);
            font.ClearOutputData();
            foreach (var (path, fontNo, excludeRanges) in fonts) {
                Merge(font, path, fontNo, excludeRanges, size);
            }

            (font.FallbackChar, font.EllipsisChar) = (fallbackChar, ellipsisChar);
        }

        Traverse.Create(host).Field<int>("fontGeneration").Value++;
        Log.Message($"[CJK] merged CJK fallback fonts: {string.Join(", ", fonts.Select(font => $"{font.Path}#{font.FontNo}"))}");
    }

    private static bool HasCjk(IList sources) =>
        sources.Cast<object>().Any(source => KnownCjkFonts.Any(name => ((string)Field(source, "Path")!).Contains(name, StringComparison.OrdinalIgnoreCase)));

    private static object? Field(object source, string name) => Traverse.Create(source).Property(name).GetValue();

    /// <summary>Mirrors FontFallbacks.Resolve: a Hangul font with Han excluded, then the locale's Han font.</summary>
    private static List<(string Path, uint FontNo, uint[]? ExcludeRanges)> Fonts(string? locale) {
        var hangul = FontFor("ko");
        var han = FontFor(locale switch { "KR" => "ko", "SC" => "zh-cn", "TC" => "zh-tw", _ => "ja" });
        return [
            .. hangul is var (hangulPath, hangulNo) ? [(hangulPath, hangulNo, HanRanges)] : Array.Empty<(string, uint, uint[]?)>(),
            .. han is var (hanPath, hanNo) ? [(hanPath, hanNo, null)] : Array.Empty<(string, uint, uint[]?)>(),
        ];
    }

    /// <summary>fc-match falls back to its closest font, so a match must also cover the language.</summary>
    private static (string Path, uint FontNo)? FontFor(string language) {
        try {
            using var process = Process.Start(new ProcessStartInfo("fc-match", ["--format=%{file}\t%{index}\t%{lang}", $"sans-serif:lang={language}"]) {
                RedirectStandardOutput = true,
            });
            var output = process?.StandardOutput.ReadToEnd().Trim().Split('\t');
            process?.WaitForExit();
            return output is [{ Length: > 0 } path, var index, var languages] && languages.Split('|').Contains(language)
                && uint.TryParse(index, out var fontNo) ? (path, fontNo) : null;
        } catch (Exception exception) {
            Log.Warning($"[CJK] fc-match failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Mirrors ImGuiHost.AddFallbacks, targeting an already loaded font with the engine's shared file data.</summary>
    private static unsafe void Merge(ImFontPtr font, string path, uint fontNo, uint[]? excludeRanges, float size) {
        var (data, length) = ((nint, int))FallbackFileData.Invoke(null, [path])!;
        if (data == 0) {
            return;
        }

        var config = new ImFontConfig {
            FontDataOwnedByAtlas = false,
            GlyphMaxAdvanceX = float.MaxValue,
            RasterizerMultiply = 1f,
            RasterizerDensity = 1f,
            FontLoaderFlags = 0x200,
            PixelSnapH = true,
            MergeMode = true,
            FontNo = fontNo,
            DstFont = font,
        };
        if (excludeRanges != null) {
            config.GlyphExcludeRanges = new Ptr<uint>((uint*)Marshal.UnsafeAddrOfPinnedArrayElement(excludeRanges, 0));
        }

        ImGui.GetIO().Fonts.AddFontFromMemoryTTF((void*)data, length, size, new ImFontConfigPtr(&config));
    }
}
