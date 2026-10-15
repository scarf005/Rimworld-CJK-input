using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityStubs.Internal;
using Verse;

namespace FcitxCjkInput;

/// <summary>
/// Routes RimWorld 1.7 text-field key presses through an fcitx5 input context. GLFW on Wayland
/// has no text-input protocol, so fcitx5 otherwise never sees the keys.
/// </summary>
internal static class FcitxCjkInputMod {
    private const string LibraryName = "fcitxcjkinput";
    private const string LibraryFile = "libfcitxcjkinput.so";
    private const int PanelWindowId = 0x434a4b;

    private static readonly byte[] Buffer = new byte[16384];
    private static readonly List<Action> heldButtons = [];
    private static readonly HashSet<int> consumedPresses = [];

    private static int focusedControl;
    private static int fieldFrame = -10;
    private static bool swallowChars;
    private static bool discardCommits;
    private static Panel? panel;

    /// <summary>The input context has focus exactly while a text field is tracked.</summary>
    private static bool ContextFocused => focusedControl != 0;

    private static bool DebugLogging => FcitxCjkInputEntry.Settings?.DebugLog == true;

    private static bool FieldActive => TextFocus.CapturesText && Time.frameCount - fieldFrame <= 2;

    [DllImport(LibraryName)]
    private static extern int cjk_start(int evdevScancodes);

    [DllImport(LibraryName)]
    private static extern void cjk_stop();

    [DllImport(LibraryName)]
    private static extern void cjk_focus(int focused);

    [DllImport(LibraryName)]
    private static extern void cjk_reset();

    [DllImport(LibraryName)]
    private static extern void cjk_reset_keys();

    [DllImport(LibraryName)]
    private static extern int cjk_key(uint scancode, int action, uint time, int forward);

    [DllImport(LibraryName)]
    private static extern int cjk_poll(byte[] buffer, int capacity);

    internal static void Start(ModContentPack content) {
        if (!OperatingSystem.IsLinux()) {
            return;
        }

        var library = content.foldersToLoadDescendingOrder
            .Select(folder => Path.Combine(folder, "Assemblies", LibraryFile))
            .FirstOrDefault(File.Exists);
        if (library is null) {
            Log.Error($"[CJK] {LibraryFile} was not found in {content.RootDir}");
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(FcitxCjkInputMod).Assembly,
            (name, _, _) => name == LibraryName ? NativeLibrary.Load(library) : IntPtr.Zero);
        if (cjk_start(Glfw.GetPlatform() == GlfwPlatform.Wayland ? 1 : 0) is var result and not 0) {
            Log.Warning($"[CJK] fcitx5 input context is unavailable (code {result}); CJK input is disabled");
            return;
        }

        Patch();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cjk_stop();
        Log.Message("[CJK] fcitx5 input context ready");
    }

    private static void Patch() {
        var harmony = new Harmony("scarf.cjkinput");
        var bridge = AccessTools.TypeByName("RimWorldWaylith.GlfwInputBridge")
            ?? throw new MissingMemberException("RimWorldWaylith.GlfwInputBridge");
        harmony.Patch(AccessTools.Method(bridge, "OnKey"), prefix: new HarmonyMethod(BeforeKey));
        harmony.Patch(AccessTools.Method(bridge, "OnChar"), prefix: new HarmonyMethod(BeforeChar));
        harmony.Patch(AccessTools.Method(bridge, "OnMouseButton"), prefix: new HarmonyMethod(BeforeMouseButton));
        harmony.Patch(AccessTools.Method(bridge, "OnFocus"), prefix: new HarmonyMethod(BeforeWindowFocus));
        Waylith.EngineApplication.Instance.PrePollEvents += _ => ReleaseButtons();
        harmony.Patch(AccessTools.Method(typeof(GUI), "DrawTextFieldChrome"),
            prefix: new HarmonyMethod(BeforeDrawField), postfix: new HarmonyMethod(AfterDrawField));
        try {
            CjkFontFallback.Patch(harmony);
        } catch (Exception exception) {
            Log.Error($"[CJK] CJK font fallback unavailable: {exception}");
        }
    }

    /// <summary>
    /// Forwards keys to fcitx5 while a text field is focused and skips the ones it consumes. The game drops
    /// repeats of a press it never saw, so a key held past the composition, such as Backspace, becomes a press.
    /// </summary>
    private static bool BeforeKey(int scancode, ref GlfwKeyAction action) {
        SyncFocus();
        var handled = cjk_key((uint)scancode, (int)action, (uint)Environment.TickCount, ContextFocused ? 1 : 0) == 1;
        Drain();
        swallowChars = handled && action != GlfwKeyAction.Release;
        if (swallowChars) {
            if (action == GlfwKeyAction.Press) {
                consumedPresses.Add(scancode);
            }

            return false;
        }

        if (consumedPresses.Remove(scancode) && action == GlfwKeyAction.Repeat) {
            action = GlfwKeyAction.Press;
        }

        return true;
    }

    /// <summary>GLFW reports a key's character right after the key; drop it when fcitx5 consumed the key.</summary>
    private static bool BeforeChar() => !swallowChars;

    /// <summary>
    /// Commits the visible preedit before a click can move the caret or focus. The game dispatches a
    /// frame's clicks before its typed text, so the click and later button events wait for the next frame.
    /// </summary>
    private static bool BeforeMouseButton(MethodBase __originalMethod, object __instance, object[] __args, GlfwButtonAction action) {
        if (action == GlfwButtonAction.Press && ImeComposition.Active) {
            QueueText(ImeComposition.Text);
            DiscardComposition();
        } else if (heldButtons.Count == 0) {
            return true;
        }

        heldButtons.Add(() => __originalMethod.Invoke(__instance, __args));
        return false;
    }

    /// <summary>Replays held button events after the next frame resets its input state.</summary>
    private static void ReleaseButtons() {
        Action[] held = [.. heldButtons];
        heldButtons.Clear();
        foreach (var replay in held) {
            replay();
        }
    }

    private static void BeforeWindowFocus(GlfwFocusEvent focusEvent) {
        if (focusEvent == GlfwFocusEvent.LostFocus) {
            cjk_reset_keys();
        }
    }

    /// <summary>
    /// Follows text-field focus. Runs from the key and focused-field hooks instead of a per-frame hook:
    /// on RimWorld 1.7 a Root.OnGUI prefix applied during loading can stop firing once loading ends.
    /// </summary>
    private static void SyncFocus() {
        var control = FieldActive ? GUIUtility.keyboardControl : 0;
        if (control != focusedControl) {
            if (ContextFocused) {
                DiscardComposition();
            }

            if (ContextFocused != (control != 0)) {
                cjk_focus(control != 0 ? 1 : 0);
            }

            focusedControl = control;
            if (DebugLogging) {
                Log.Message($"[CJK] text field control {control}, input context focused: {ContextFocused}");
            }
        }
    }

    private static void BeforeDrawField(bool focused) {
        if (!focused) {
            return;
        }

        fieldFrame = Time.frameCount;
        SyncFocus();
        Drain();
    }

    /// <summary>Requests the panel on repaint passes; ImmediateWindow ignores calls from the other passes.</summary>
    private static void AfterDrawField(Rect rect, bool focused, bool multiline) {
        if (focused && ContextFocused && Event.current.type == EventType.Repaint && panel is { IsEmpty: false } visible) {
            DrawPanel(visible, rect, multiline);
        }
    }

    /// <summary>Opens the candidates under the caret, or above its line when they would leave the screen.</summary>
    private static void DrawPanel(Panel visible, Rect field, bool multiline) {
        string[] lines = visible.Header.Length > 0 ? [visible.Header, .. visible.Candidates] : [.. visible.Candidates];
        var offset = visible.Header.Length > 0 ? 1 : 0;
        // DrawTextFieldChrome draws the caret one line high in a text area and 3 px short of a single-line field's edges.
        var caretHeight = multiline ? Text.LineHeight : field.height - 6f;
        var font = Text.Font;
        Text.Font = GameFont.Small;
        var lineHeight = Text.LineHeight;
        var width = lines.Max(line => Text.CalcSize(line).x) + 16f;
        Text.Font = font;
        var height = lineHeight * lines.Length + 8f;
        // DrawTextFieldChrome just set CandidatePos to the caret's bottom through the backend's GUI-to-screen
        // mapping; its inverse gives the GUI point back.
        var caret = UI.GUIToScreenPoint(GUIUtility.ScreenToGUIPoint(ImeComposition.CandidatePos));
        var below = caret.y + 5f;
        var rect = new Rect(Mathf.Min(caret.x, UI.screenWidth - width),
            below + height <= UI.screenHeight ? below : caret.y - caretHeight - 5f - height, width, height);
        Find.WindowStack.ImmediateWindow(PanelWindowId, rect, WindowLayer.Super, () => {
            Text.Font = GameFont.Small;
            for (var i = 0; i < lines.Length; i++) {
                var row = new Rect(8f, 4f + i * lineHeight, width - 16f, lineHeight);
                if (i - offset == visible.Index) {
                    Widgets.DrawHighlight(row);
                }

                Widgets.Label(row, lines[i]);
            }
        }, absorbInputAroundWindow: false, shadowAlpha: 0f);
    }

    /// <summary>Resets fcitx5 and drops what it commits; the caller already inserted or abandoned the preedit.</summary>
    private static void DiscardComposition() {
        ImeComposition.Set("", 0);
        panel = null;
        discardCommits = true;
        cjk_reset();
        Drain();
        discardCommits = false;
    }

    private static void Drain() {
        int length;
        while ((length = cjk_poll(Buffer, Buffer.Length)) > 0) {
            var line = Encoding.UTF8.GetString(Buffer, 0, length);
            if (DebugLogging) {
                Log.Message($"[CJK] {line.Replace(ImeEvent.Separator, '|')}");
            }

            switch (ImeEvent.Parse(line)) {
                case Commit commit when ContextFocused && !discardCommits:
                    QueueText(commit.Text);
                    break;
                case Preedit next:
                    ImeComposition.Set(next.Text, next.Cursor);
                    break;
                case Forward { Release: false } key:
                    ForwardKey(key);
                    break;
                case Panel next:
                    panel = next;
                    break;
                case FocusOut:
                    ImeComposition.Set("", 0);
                    panel = null;
                    break;
            }
        }
    }

    private static void QueueText(string text) {
        foreach (var character in text) {
            InputSystem.QueueTextInput(character);
        }
    }

    private static void ForwardKey(Forward key) {
        if (key.Codepoint >= 0x20 && key.Codepoint != 0x7f) {
            QueueText(char.ConvertFromUtf32(key.Codepoint));
            return;
        }

        Key? unityKey = key.Keysym switch {
            0xff08 => Key.Backspace,
            0xff09 => Key.Tab,
            0xff0d => Key.Enter,
            0xff1b => Key.Escape,
            0xff50 => Key.Home,
            0xff51 => Key.LeftArrow,
            0xff52 => Key.UpArrow,
            0xff53 => Key.RightArrow,
            0xff54 => Key.DownArrow,
            0xff57 => Key.End,
            0xffff => Key.Delete,
            _ => null,
        };
        if (unityKey is { } pressed) {
            InputSystem.QueueKeyChange(pressed, true);
            InputSystem.QueueKeyChange(pressed, false);
        }
    }
}
