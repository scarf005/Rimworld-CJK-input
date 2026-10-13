using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Brutal.ImGuiApi;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FcitxCjkInput;

/// <summary>
/// RimWorld 1.7 merges CJK fallback fonts only from a fixed list of distribution paths
/// (RimWorldWaylith.Imgui.FontFallbacks), so CJK text renders as boxes elsewhere, e.g. on Fedora.
/// Merges the fonts fontconfig picks for CJK languages into every loaded UI font.
/// </summary>
internal static class CjkFontFallback {
    private static readonly string[] Languages = ["ko", "zh-cn", "ja"];
    private static readonly string[] KnownCjkFonts = ["CJK", "Nanum", "DroidSansFallback", "wqy"];

    private static bool applied;

    /// <summary>Prefix of ImGuiHost.BeginFrame: merges once between frames, before ImGui.NewFrame.</summary>
    internal static void BeforeFrame(object __instance) {
        if (applied) {
            return;
        }

        applied = true;
        Apply(__instance);
    }

    private static void Apply(object host) {
        var hostType = host.GetType();
        var fallbackField = AccessTools.Field(hostType, "fallbackFonts");
        var fallbacks = (IReadOnlyList<string>)fallbackField.GetValue(host)!;
        if (fallbacks.Any(path => KnownCjkFonts.Any(name => path.Contains(name, StringComparison.OrdinalIgnoreCase)))) {
            return;
        }

        var added = Languages.Select(FontFor).OfType<string>().Distinct().Except(fallbacks).ToList();
        if (added.Count == 0) {
            Log.Warning("[CJK] fontconfig found no CJK font; CJK text renders as boxes");
            return;
        }

        // Fonts loaded later (registered and bold/italic variants) read this list.
        fallbackField.SetValue(host, fallbacks.Concat(added).ToList());
        ImGui.SetCurrentContext((ImGuiContextPtr)AccessTools.Field(hostType, "context").GetValue(host)!);
        var fonts = (List<ImFontPtr>)AccessTools.Field(hostType, "fonts").GetValue(host)!;
        var sizes = (List<float>)AccessTools.Field(hostType, "importSizes").GetValue(host)!;
        var styled = (Dictionary<(int, FontStyle), ImFontPtr>)AccessTools.Field(hostType, "styledFonts").GetValue(host)!;
        var targets = fonts.Select((font, i) => (font, sizes[i], 0u))
            .Concat(styled.Select(pair => (pair.Value, sizes[pair.Key.Item1], StyleFlags(pair.Key.Item2))));
        foreach (var (font, size, styleFlags) in targets) {
            // Existing bakes hold loader data per source; discard them while the source count still matches.
            font.ClearOutputData();
            foreach (var path in added) {
                Merge(font, path, size, styleFlags);
            }
        }

        Log.Message($"[CJK] merged CJK fallback fonts: {string.Join(", ", added)}");
    }

    private static uint StyleFlags(FontStyle style) =>
        ((style & FontStyle.Bold) != 0 ? 0x20u : 0u) | ((style & FontStyle.Italic) != 0 ? 0x40u : 0u);

    private static string? FontFor(string language) {
        try {
            using var process = Process.Start(new ProcessStartInfo("fc-match", ["--format=%{file}", $"sans-serif:lang={language}"]) {
                RedirectStandardOutput = true,
            });
            var path = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit();
            return string.IsNullOrEmpty(path) ? null : path;
        } catch (Exception exception) {
            Log.Warning($"[CJK] fc-match failed: {exception.Message}");
            return null;
        }
    }

    /// <summary>Mirrors ImGuiHost.AddFallbacks, targeting an already loaded font.</summary>
    private static unsafe void Merge(ImFontPtr font, string path, float size, uint styleFlags) {
        var config = new ImFontConfig {
            FontDataOwnedByAtlas = true,
            GlyphMaxAdvanceX = float.MaxValue,
            RasterizerMultiply = 1f,
            RasterizerDensity = 1f,
            FontLoaderFlags = 0x200 | styleFlags,
            PixelSnapH = true,
            MergeMode = true,
            DstFont = font,
        };
        ImGui.GetIO().Fonts.AddFontFromFileTTF(path, size, new ImFontConfigPtr(&config));
    }
}
