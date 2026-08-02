using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    [StaticConstructorOnStartup]
    public static class FcitxCjkInputMod {
        private const string NativeLibraryName = "libfcitxcjkinput.so";
        private const string LogPath = "/tmp/fcitxcjkinput.log";
        private const int RtldNow = 2;
        private const int NativeBufferSize = 16384;
        private const int NativeDrainLimit = 32;
        private const int NativeRestartDelayMs = 2000;
        private const int LogBufferSize = 65536;
        private const long LogMaxBytes = 8L * 1024 * 1024;

        private static readonly long LogFlushInterval = Stopwatch.Frequency / 4L;

        private static readonly object LogLock = new object();
        private static readonly byte[] NativeBuffer = new byte[NativeBufferSize];
        private static readonly Dictionary<int, string> Engines = new Dictionary<int, string>();
        private static readonly Dictionary<int, ControlToken> ControlTokens =
            new Dictionary<int, ControlToken>();
        private static readonly Dictionary<ControlToken, string> ExpectedFieldTexts =
            new Dictionary<ControlToken, string>();
        private static readonly CompositionStateMachine Composition =
            new CompositionStateMachine(Stopwatch.Frequency * 2L);
        private static readonly CommittedCharacterTracker CommittedCharacters =
            new CommittedCharacterTracker();
        private static readonly GameplayKeyState GameplayKeys =
            new GameplayKeyState(Stopwatch.Frequency / 4L);
        private static readonly ShortcutCommitGuard ShortcutCommits =
            new ShortcutCommitGuard();

        private static StreamWriter _log;
        private static IntPtr _nativeHandle;
        private static NativeSetDebug _nativeSetDebug;
        private static NativeStart _nativeStart;
        private static NativePoll _nativePoll;
        private static NativeStop _nativeStop;
        private static string _engine = "unknown";
        private static bool _nativeReady;
        private static int _overlayFrame = -1;
        private static bool _nativeLoaded;
        private static int _mainThreadId;
        private static int _nativePollFrame = -1;
        private static long _restartAt;
        private static int _focusedControl;
        private static int _focusedTextFieldFrame = -10;
        private static int _lastZContext;
        private static int _unboundPreeditContext;
        private static string _unboundPreedit = "";
        private static long _focusGeneration;
        private static bool _logInitialized;
        private static bool _textFieldActive;
        private static int _shuttingDown;
        private static long _lastLogFlush;

        private static bool DebugLogging => FcitxCjkInputEntry.Settings?.DebugLog == true;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeSetDebug(int enabled);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeStart(uint pid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativePoll([Out] byte[] buffer, int capacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeStop();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        static FcitxCjkInputMod() {
            if (Application.platform != RuntimePlatform.LinuxPlayer)
                return;

            try {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                EnsureLog();
                WriteRuntimeHeader("INIT");
                LoadNativeBridge();
                Application.quitting += Shutdown;
                _nativeSetDebug(DebugLogging ? 1 : 0);
                Patch();
                StartNativeBridge();
                Log.Message("[CJK] fcitx5 SDL/IMGUI bridge initialized; " +
                    (DebugLogging ? "log=" + LogPath : "debug log disabled"));
            } catch (Exception exception) {
                WriteLog("FATAL " + exception);
                Shutdown();
                Log.Error("[CJK] initialization failed: " + exception);
            }
        }

        internal static void SetDebugLogging(bool enabled) {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            if (enabled)
                EnsureLog();
            if (_nativeSetDebug != null)
                _nativeSetDebug(enabled ? 1 : 0);
            if (enabled) {
                WriteRuntimeHeader("DEBUG enabled");
                Log.Message("[CJK] debug log enabled; log=" + LogPath);
            } else {
                ExpectedFieldTexts.Clear();
                lock (LogLock) {
                    _log?.Dispose();
                    _log = null;
                }
                Log.Message("[CJK] debug log disabled");
            }
        }

        private static void WriteRuntimeHeader(string reason) {
            WriteLog(reason + " unity=" + Application.unityVersion + " pid=" +
                Process.GetCurrentProcess().Id + " XMODIFIERS=" +
                Environment.GetEnvironmentVariable("XMODIFIERS") + " SDL_IM_MODULE=" +
                Environment.GetEnvironmentVariable("SDL_IM_MODULE") + " engine=" + _engine +
                " native=" + _nativeReady + " ime=" + Input.imeCompositionMode);
        }

        private static void Patch() {
            var harmony = new Harmony("scarf.cjkinput");
            var rootOnGui = AccessTools.Method(typeof(Root), "OnGUI");
            var desktopTextField = AccessTools.Method(typeof(GUI), "HandleTextFieldEventForDesktop");
            var quickSearch = AccessTools.Method(typeof(QuickSearchWidget), nameof(QuickSearchWidget.OnGUI));
            var searchTextSetter = AccessTools.PropertySetter(typeof(QuickSearchFilter),
                nameof(QuickSearchFilter.Text));
            var keyBindingIsDown = AccessTools.PropertyGetter(typeof(KeyBindingDef),
                nameof(KeyBindingDef.IsDown));
            var keyBindingKeyDownEvent = AccessTools.PropertyGetter(typeof(KeyBindingDef),
                nameof(KeyBindingDef.KeyDownEvent));
            var playSettingsControls = AccessTools.Method(typeof(PlaySettings),
                nameof(PlaySettings.DoPlaySettingsGlobalControls));
            if (rootOnGui == null)
                throw new MissingMethodException("Verse.Root.OnGUI");
            if (desktopTextField == null)
                throw new MissingMethodException("UnityEngine.GUI.HandleTextFieldEventForDesktop");
            if (quickSearch == null)
                throw new MissingMethodException("RimWorld.QuickSearchWidget.OnGUI");
            if (searchTextSetter == null)
                throw new MissingMethodException("RimWorld.QuickSearchFilter.Text.set");
            if (keyBindingIsDown == null)
                throw new MissingMethodException("Verse.KeyBindingDef.IsDown.get");
            if (keyBindingKeyDownEvent == null)
                throw new MissingMethodException("Verse.KeyBindingDef.KeyDownEvent.get");
            if (playSettingsControls == null)
                throw new MissingMethodException(
                    "RimWorld.PlaySettings.DoPlaySettingsGlobalControls");

            harmony.Patch(rootOnGui,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeRootOnGui)));
            harmony.Patch(desktopTextField,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeDesktopTextField)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterDesktopTextField)));
            harmony.Patch(quickSearch,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeQuickSearch)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterQuickSearch)));
            harmony.Patch(searchTextSetter,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeSearchTextSet)));
            harmony.Patch(keyBindingIsDown,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterKeyBindingIsDown)));
            harmony.Patch(keyBindingKeyDownEvent,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod),
                    nameof(AfterKeyBindingKeyDownEvent)));
            harmony.Patch(playSettingsControls,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod),
                    nameof(AfterPlaySettingsControls)));
            WriteLog("PATCH Root.OnGUI=" + rootOnGui + " textField=" + desktopTextField +
                " quickSearch=" + quickSearch + " searchTextSetter=" + searchTextSetter +
                " keyBindingIsDown=" + keyBindingIsDown + " keyBindingKeyDownEvent=" +
                keyBindingKeyDownEvent + " playSettingsControls=" + playSettingsControls);
        }

        private static void LoadNativeBridge() {
            var content = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(mod => mod.PackageId == "scarf.cjkinput");
            var assemblyDirectory = content != null
                ? Path.Combine(content.RootDir, "1.6", "Assemblies")
                : Path.GetDirectoryName(typeof(FcitxCjkInputMod).Assembly.Location);
            var path = Path.Combine(assemblyDirectory, NativeLibraryName);
            _nativeHandle = dlopen(path, RtldNow);
            if (_nativeHandle == IntPtr.Zero)
                throw new DllNotFoundException(path + ": " + GetDlError());

            _nativeSetDebug = LoadNativeFunction<NativeSetDebug>("fcitx_bridge_set_debug");
            _nativeStart = LoadNativeFunction<NativeStart>("fcitx_bridge_start");
            _nativePoll = LoadNativeFunction<NativePoll>("fcitx_bridge_poll");
            _nativeStop = LoadNativeFunction<NativeStop>("fcitx_bridge_stop");
            _nativeLoaded = true;
            WriteLog("NATIVE loaded path=" + path);
        }

        private static T LoadNativeFunction<T>(string name) where T : class {
            var pointer = dlsym(_nativeHandle, name);
            if (pointer == IntPtr.Zero)
                throw new MissingMethodException(name + ": " + GetDlError());
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        private static string GetDlError() {
            var pointer = dlerror();
            return pointer == IntPtr.Zero ? "unknown dlerror" : Marshal.PtrToStringAnsi(pointer);
        }

        private static void StartNativeBridge() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            _restartAt = 0;
            _nativeReady = false;
            var result = _nativeStart((uint)Process.GetCurrentProcess().Id);
            WriteLog("NATIVE start result=" + result);
            if (result != 0)
                ScheduleNativeRestart();
        }

        private static void ScheduleNativeRestart() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            _restartAt = Stopwatch.GetTimestamp() +
                Stopwatch.Frequency * NativeRestartDelayMs / 1000L;
        }

        private static void DrainNativeMessages() {
            if (Volatile.Read(ref _shuttingDown) != 0)
                return;
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
                return;
            if (!_nativeLoaded)
                return;

            for (var count = 0; count < NativeDrainLimit; count++) {
                var length = _nativePoll(NativeBuffer, NativeBuffer.Length);
                if (length <= 0)
                    break;
                var line = Encoding.UTF8.GetString(NativeBuffer, 0, length);
                try {
                    HandleNativeMessage(line);
                } catch (Exception exception) {
                    WriteLog("RX error line=[" + Escape(line) + "] exception=" + exception);
                }
            }
        }

        private static void HandleNativeMessage(string line) {
            if (line.StartsWith("LOG:", StringComparison.Ordinal)) {
                WriteLog("NATIVE " + line.Substring(4));
                return;
            }
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) {
                WriteLog("NATIVE " + line);
                return;
            }

            if (DebugLogging)
                WriteLog("RX " + line);
            if (line.StartsWith("READY:", StringComparison.Ordinal)) {
                _nativeReady = true;
                _restartAt = 0;
                return;
            }
            if (line == "STOPPED") {
                _nativeReady = false;
                Engines.Clear();
                Composition.Reset();
                CommittedCharacters.Clear();
                GameplayKeys.Clear();
                SetEngine("unknown");
                ScheduleNativeRestart();
                return;
            }
            if (!line.StartsWith("EVENT:", StringComparison.Ordinal)) {
                WriteLog("RX unknown line=[" + Escape(line) + "]");
                return;
            }

            var parts = line.Split(new[] { ':' }, 5);
            if (parts.Length != 5 || !int.TryParse(parts[1], out var contextId) ||
                !long.TryParse(parts[2], out var sequence))
                throw new FormatException("Invalid native event: " + line);
            HandleContextEvent(contextId, sequence, parts[3], parts[4]);
        }

        private static void HandleContextEvent(int contextId, long sequence, string kind, string payload) {
            var now = Stopwatch.GetTimestamp();
            if (kind == "RESET") {
                Engines.Clear();
                Composition.ResetAndDiscardActions();
                CommittedCharacters.Clear();
                GameplayKeys.Clear();
                ClearShortcutComposition(0);
                SetEngine("unknown");
                _nativeStop?.Invoke();
                StartNativeBridge();
                return;
            }
            if (kind == "HANGUL_PREEDIT" || kind == "HANGUL_COMMIT") {
                SetHangulEngine(contextId);
                kind = kind == "HANGUL_PREEDIT" ? "PREEDIT" : "COMMIT";
            }
            if (kind == "ENGINE") {
                Engines[contextId] = payload;
                if (payload != "hangul") {
                    Composition.CancelComposition(contextId);
                    GameplayKeys.Clear();
                    ClearShortcutComposition(contextId);
                }
                if (Composition.ActiveContext == 0 || Composition.ActiveContext == contextId)
                    SetEngine(payload);
                return;
            }
            if (kind == "FOCUS") {
                if (payload == "IN") {
                    Composition.FocusIn(contextId, sequence);
                    SetEngine(Engines.TryGetValue(contextId, out var engine) ? engine : "unknown");
                } else if (payload == "OUT") {
                    GameplayKeys.Clear();
                    ClearShortcutComposition(contextId);
                    if (Composition.FocusOut(contextId, sequence))
                        SetEngine("unknown");
                }
                return;
            }
            if (kind == "KEY") {
                var keyParts = payload.Split(':');
                if (keyParts.Length != 3)
                    throw new FormatException("Invalid key event: " + payload);
                var keyValue = int.Parse(keyParts[0]);
                var release = int.Parse(keyParts[1]) != 0;
                var observedAt = long.Parse(keyParts[2]) * Stopwatch.Frequency / 1000L;
                SetHangulEngine(contextId);
                if (!release && (keyValue == 'z' || keyValue == 'Z'))
                    _lastZContext = contextId;
                var gameplayBlocked = _textFieldActive ||
                    Find.WindowStack.AnySearchWidgetFocused;
                if (release || !gameplayBlocked)
                    GameplayKeys.Update(keyValue, release, observedAt, now);
                return;
            }
            if (kind == "KEYRESET") {
                GameplayKeys.Clear();
                ClearShortcutComposition(0);
                return;
            }
            if (kind == "PREEDIT") {
                var separator = payload.IndexOf(':');
                if (separator < 0)
                    throw new FormatException("Missing preedit cursor separator: " + payload);
                var cursorBytes = int.Parse(payload.Substring(0, separator));
                var bytes = DecodeHexBytes(payload.Substring(separator + 1));
                var clampedCursorBytes = Math.Max(0, Math.Min(cursorBytes, bytes.Length));
                var text = Encoding.UTF8.GetString(bytes);
                var cursor = Encoding.UTF8.GetString(bytes, 0, clampedCursorBytes).Length;
                if (text.Length == 0)
                    ClearShortcutComposition(contextId);
                if (Composition.Preedit(contextId, sequence, text, cursor)) {
                    _unboundPreeditContext = 0;
                    _unboundPreedit = "";
                    if (Engines.TryGetValue(contextId, out var engine))
                        SetEngine(engine);
                    if (DebugLogging)
                        WriteLog("STATE preedit context=" + contextId + " control=" +
                            _focusedControl + " cursorBytes=" + cursorBytes + " cursorChars=" +
                            cursor + " text=[" + Escape(text) + "]");
                } else {
                    _unboundPreeditContext = contextId;
                    _unboundPreedit = text;
                    if (DebugLogging)
                        WriteLog("DROP preedit context=" + contextId + " sequence=" + sequence +
                            " reason=inactive-or-unbound");
                }
                return;
            }
            if (kind == "COMMIT") {
                var text = DecodeHex(payload);
                if (ShortcutCommits.ShouldDiscard(contextId, text)) {
                    if (DebugLogging)
                        WriteLog("DROP shortcut commit context=" + contextId + " sequence=" +
                            sequence + " text=[" + Escape(text) + "]");
                    return;
                }
                Engines.TryGetValue(contextId, out var engine);
                if (engine == "hangul" && ContainsNonAscii(text) &&
                    Composition.Commit(contextId, sequence, text, now)) {
                    if (DebugLogging)
                        WriteLog("QUEUE commit context=" + contextId + " sequence=" + sequence +
                            " text=[" + Escape(text) + "] count=" + Composition.PendingCount);
                } else if (DebugLogging) {
                    WriteLog("DROP commit context=" + contextId + " sequence=" + sequence +
                        " engine=" + (engine ?? "unknown") + " text=[" + Escape(text) + "]");
                }
                return;
            }
            WriteLog("RX unknown event kind=" + kind + " payload=[" + Escape(payload) + "]");
        }

        private static void ClearShortcutComposition(int contextId) {
            ShortcutCommits.Cancel(contextId);
            if (contextId == 0 || _lastZContext == contextId)
                _lastZContext = 0;
            if (contextId == 0 || _unboundPreeditContext == contextId) {
                _unboundPreeditContext = 0;
                _unboundPreedit = "";
            }
        }

        private static void SetHangulEngine(int contextId) {
            Engines[contextId] = "hangul";
            if (Composition.ActiveContext == 0 || Composition.ActiveContext == contextId)
                SetEngine("hangul");
        }

        private static void SetEngine(string engine) {
            if (DebugLogging && _engine != engine)
                WriteLog("STATE engine " + _engine + " -> " + engine);
            _engine = engine;
        }

        private static void BeforeRootOnGui() {
            var now = Stopwatch.GetTimestamp();
            if (_restartAt != 0 && now >= _restartAt)
                StartNativeBridge();
            Composition.DiscardExpired(now);
            GameplayKeys.DiscardExpired(now);
            if (GUIUtility.keyboardControl == 0 && _focusedControl != 0) {
                _focusedControl = 0;
                Composition.Blur();
            }
            var textFieldActive = ImeRouting.TextFieldIsActive(GUIUtility.keyboardControl,
                _focusedControl, _focusedTextFieldFrame, Time.frameCount);
            _textFieldActive = textFieldActive;
            if (_nativePollFrame != Time.frameCount) {
                _nativePollFrame = Time.frameCount;
                DrainNativeMessages();
            }
            SetImeCompositionMode(textFieldActive, "root");

            var currentEvent = Event.current;
            if (currentEvent == null)
                return;
            LogRootEvent(currentEvent, textFieldActive);
        }

        private static void BeforeDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor) {
            var target = ObserveEditor(id, editor);
            LogTextField("FIELD before-original", target, id, content, editor);
            if (target.Id == 0)
                return;
            SuppressRawHangulKey(id);
            if (DebugLogging)
                VerifyCommittedText(target, id, content, editor);

            var actions = Composition.TakeActions(target, Stopwatch.GetTimestamp());
            if (actions.Count > 0) {
                var inserted = DebugLogging ? new StringBuilder() : null;
                foreach (var action in actions) {
                    var insertedLength = ApplyAction(editor, maxLength, action, inserted);
                    CommittedCharacters.Expect(target, action.Text, insertedLength, Time.frameCount);
                }
                content.text = editor.text;
                if (DebugLogging)
                    ExpectedFieldTexts[target] = editor.text;
                GUI.changed = true;
                if (DebugLogging)
                    WriteLog("INSERT event=" + Event.current.type + " control=" + id + " text=[" +
                        Escape(inserted.ToString()) + "] result=[" + Escape(editor.text) + "] cursor=" +
                        editor.cursorIndex + " select=" + editor.selectIndex + " pending=" +
                        Composition.PendingCount);
                if (GUIUtility.keyboardControl == id)
                    Composition.Focus(target, editor.cursorIndex, editor.selectIndex);
            }
            SuppressCommittedCharacter(target, id);
        }

        private static void AfterDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor) {
            var target = ObserveEditor(id, editor);
            LogTextField("FIELD after-original", target, id, content, editor);
            if (Event.current.type == EventType.Repaint && GUIUtility.keyboardControl == id &&
                Composition.TryGetView(target, out var view))
                DrawPreedit(editor, view);

            if (Event.current.type == EventType.Repaint && DebugLogging && _overlayFrame != Time.frameCount) {
                _overlayFrame = Time.frameCount;
                DrawOverlay();
            }
        }

        private static void BeforeQuickSearch(QuickSearchWidget __instance, out string __state) {
            __state = __instance.filter.Text;
        }

        private static void AfterQuickSearch(QuickSearchWidget __instance, string __state) {
            if (!DebugLogging)
                return;
            var focused = __instance.CurrentlyFocused();
            var currentEvent = Event.current;
            var keyEvent = currentEvent != null && (currentEvent.rawType == EventType.KeyDown ||
                currentEvent.rawType == EventType.KeyUp);
            if (__state == __instance.filter.Text && (!focused || !keyEvent))
                return;
            WriteLog("SEARCH filter=" + RuntimeHelpers.GetHashCode(__instance.filter) +
                " before=[" + Escape(__state) + "] after=[" + Escape(__instance.filter.Text) +
                "] focused=" + focused + " " + DescribeEvent());
        }

        private static void BeforeSearchTextSet(QuickSearchFilter __instance, string value) {
            if (!DebugLogging)
                return;
            WriteLog("SEARCH set filter=" + RuntimeHelpers.GetHashCode(__instance) + " old=[" +
                Escape(__instance.Text) + "] new=[" + Escape(value) + "] " + DescribeEvent());
        }

        private static void AfterKeyBindingIsDown(KeyBindingDef __instance, ref bool __result) {
            RecoverKeyBinding(__instance, ref __result, IsCameraDolly(__instance), pressed: false);
        }

        private static void AfterKeyBindingKeyDownEvent(KeyBindingDef __instance,
            ref bool __result) {
            var rotate = __instance == KeyBindingDefOf.Designator_RotateLeft ||
                __instance == KeyBindingDefOf.Designator_RotateRight;
            RecoverKeyBinding(__instance, ref __result, rotate, pressed: true);
        }

        private static void AfterPlaySettingsControls(bool worldView) {
            if (!TryRecoverKeyBinding(KeyBindingDefOf.OpenMapSearch, pressed: true))
                return;
            if (_lastZContext != 0 && _unboundPreeditContext == _lastZContext &&
                _unboundPreedit == "ㅋ") {
                ShortcutCommits.Arm(_lastZContext, _unboundPreedit);
                _unboundPreeditContext = 0;
                _unboundPreedit = "";
            }
            if (DebugLogging)
                WriteLog("RECOVER binding=" + KeyBindingDefOf.OpenMapSearch.defName + " " +
                    DescribeEvent());
            if (worldView)
                Find.WindowStack.Add(new Dialog_WorldSearch());
            else if (Find.CurrentMap != null)
                Find.WindowStack.Add(new Dialog_MapSearch(Find.CurrentMap));
        }

        private static void RecoverKeyBinding(KeyBindingDef bindingDef, ref bool result,
            bool gameplayShortcut, bool pressed) {
            if (result || !gameplayShortcut)
                return;
            result = TryRecoverKeyBinding(bindingDef, pressed);
            if (result && pressed && DebugLogging)
                WriteLog("RECOVER binding=" + bindingDef.defName + " " + DescribeEvent());
        }

        private static bool TryRecoverKeyBinding(KeyBindingDef bindingDef, bool pressed) {
            if (_textFieldActive || Find.WindowStack.AnySearchWidgetFocused)
                return false;
            var preferences = KeyPrefs.KeyPrefsData;
            if (preferences == null || !preferences.keyPrefs.TryGetValue(bindingDef, out var binding))
                return false;
            return pressed
                ? GameplayKeyRecovery.ShouldRecoverPress(false, _textFieldActive, true,
                    (int)binding.keyBindingA, (int)binding.keyBindingB, GameplayKeys,
                    Stopwatch.GetTimestamp())
                : GameplayKeyRecovery.ShouldRecover(false, _textFieldActive, true,
                    (int)binding.keyBindingA, (int)binding.keyBindingB, GameplayKeys);
        }

        private static bool IsCameraDolly(KeyBindingDef binding) {
            return binding == KeyBindingDefOf.MapDolly_Left ||
                binding == KeyBindingDefOf.MapDolly_Right ||
                binding == KeyBindingDefOf.MapDolly_Up ||
                binding == KeyBindingDefOf.MapDolly_Down;
        }

        private static void SuppressRawHangulKey(int id) {
            var currentEvent = Event.current;
            var letter = IsHangulLetter(currentEvent.keyCode);
            var backspace = currentEvent.keyCode == KeyCode.Backspace;
            if (currentEvent.type != EventType.KeyDown || (!letter && !backspace))
                return;
            var shortcutModifiers = EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt;
            var suppress = InputSuppression.ShouldSuppress(
                focusedTextField: GUIUtility.keyboardControl == id,
                hangul: _engine == "hangul",
                letter: letter,
                backspace: backspace,
                hasPreedit: Composition.HasPreedit,
                shortcut: (currentEvent.modifiers & shortcutModifiers) != 0);
            if (DebugLogging)
                WriteLog("KEY textfield control=" + id + " key=" + currentEvent.keyCode +
                    " char=U+" + ((int)currentEvent.character).ToString("X4") +
                    " modifiers=" + currentEvent.modifiers + " engine=" + _engine +
                    " preedit=" + Composition.HasPreedit + " suppress=" + suppress);
            if (suppress)
                currentEvent.Use();
        }

        private static void SuppressCommittedCharacter(ControlToken target, int id) {
            var currentEvent = Event.current;
            if (currentEvent == null ||
                !CommittedCharacters.ShouldSuppress(target, currentEvent.character, Time.frameCount))
                return;
            if (DebugLogging)
                WriteLog("KEY duplicate-commit control=" + id + " char=U+" +
                    ((int)currentEvent.character).ToString("X4") + " " + DescribeEvent());
            currentEvent.character = '\0';
        }

        private static void SetImeCompositionMode(bool textFieldActive, string reason) {
            var requested = textFieldActive ? IMECompositionMode.On : IMECompositionMode.Off;
            var previous = Input.imeCompositionMode;
            if (previous == requested)
                return;
            Input.imeCompositionMode = requested;
            if (DebugLogging)
                WriteLog("IME mode " + previous + " -> " + Input.imeCompositionMode + " reason=" +
                    reason + " keyboardControl=" + GUIUtility.keyboardControl +
                    " focusedControl=" + _focusedControl + " seenFrame=" +
                    _focusedTextFieldFrame + " frame=" + Time.frameCount);
        }

        private static void LogRootEvent(Event currentEvent, bool textFieldActive) {
            if (!DebugLogging || (currentEvent.type != EventType.KeyDown &&
                currentEvent.type != EventType.KeyUp))
                return;
            WriteLog("ROOT " + DescribeEvent() + " engine=" + _engine + " ime=" +
                Input.imeCompositionMode + " textFieldActive=" + textFieldActive +
                " keyboardControl=" + GUIUtility.keyboardControl + " focusedControl=" +
                _focusedControl + " compositionContext=" + Composition.ActiveContext +
                " preedit=" + Composition.HasPreedit + " pending=" + Composition.PendingCount);
        }

        private static void LogTextField(string stage, ControlToken target, int id, GUIContent content,
            TextEditor editor) {
            var currentEvent = Event.current;
            if (!DebugLogging || GUIUtility.keyboardControl != id || currentEvent == null ||
                (currentEvent.rawType != EventType.KeyDown &&
                    currentEvent.rawType != EventType.KeyUp))
                return;
            WriteLog(stage + " control=" + id + " token=" + target.Id + ":" + target.Generation +
                " keyboardControl=" + GUIUtility.keyboardControl + " hotControl=" +
                GUIUtility.hotControl + " contentObject=" + RuntimeHelpers.GetHashCode(content) +
                " editorObject=" + RuntimeHelpers.GetHashCode(editor) + " content=[" +
                Escape(content.text) + "] editor=[" + Escape(editor.text) + "] cursor=" +
                editor.cursorIndex + " select=" + editor.selectIndex + " graphicalCursor=" +
                editor.graphicalCursorPos + " multiline=" + editor.multiline + " guiChanged=" +
                GUI.changed + " " + DescribeEvent());
        }

        private static string DescribeEvent() {
            var currentEvent = Event.current;
            return currentEvent == null
                ? "event=null frame=" + Time.frameCount
                : "event=" + currentEvent.type + " raw=" + currentEvent.rawType + " key=" +
                    currentEvent.keyCode + " char=U+" + ((int)currentEvent.character).ToString("X4") +
                    " modifiers=" + currentEvent.modifiers + " command=[" +
                    Escape(currentEvent.commandName) + "] frame=" + Time.frameCount;
        }

        private static void VerifyCommittedText(ControlToken target, int id, GUIContent content,
            TextEditor editor) {
            if (!ExpectedFieldTexts.TryGetValue(target, out var expected))
                return;
            ExpectedFieldTexts.Remove(target);
            if (DebugLogging)
                WriteLog("VERIFY commit control=" + id + " expected=[" + Escape(expected) +
                    "] content=[" + Escape(content.text) + "] editor=[" + Escape(editor.text) +
                    "] event=" + Event.current.type);
        }

        private static void DrawPreedit(TextEditor editor, CompositionView view) {
            var originalText = editor.text;
            var originalCursor = editor.cursorIndex;
            var originalSelect = editor.selectIndex;
            var selectionStart = Math.Max(0, Math.Min(view.SelectionStart, originalText.Length));
            var selectionEnd = Math.Max(selectionStart, Math.Min(view.SelectionEnd, originalText.Length));
            var displayText = TextEditMath.ReplaceRange(originalText, selectionStart, selectionEnd,
                view.Text);
            var displayCursor = selectionStart + Math.Min(view.Cursor, view.Text.Length);

            if (DebugLogging)
                WriteLog("DRAW preedit original=[" + Escape(originalText) + "] display=[" +
                    Escape(displayText) + "] selection=" + selectionStart + ":" + selectionEnd +
                    " displayCursor=" + displayCursor + " " + DescribeEvent());
            try {
                editor.text = displayText;
                editor.cursorIndex = displayCursor;
                editor.selectIndex = displayCursor;
                editor.DrawCursor(displayText);
            } finally {
                editor.text = originalText;
                editor.cursorIndex = originalCursor;
                editor.selectIndex = originalSelect;
                if (DebugLogging)
                    WriteLog("DRAW restore editor=[" + Escape(editor.text) + "] cursor=" +
                        editor.cursorIndex + " select=" + editor.selectIndex + " " +
                        DescribeEvent());
            }
        }

        private static ControlToken ObserveEditor(int id, TextEditor editor) {
            if (GUIUtility.keyboardControl == id) {
                if (_focusedControl != id || !ControlTokens.TryGetValue(id, out var focused)) {
                    _focusedControl = id;
                    focused = new ControlToken(id, ++_focusGeneration);
                    ControlTokens[id] = focused;
                    if (DebugLogging)
                        WriteLog("STATE focus control=" + id + " generation=" + focused.Generation);
                }
                _focusedTextFieldFrame = Time.frameCount;
                _textFieldActive = true;
                SetImeCompositionMode(true, "text-field");
                Composition.Focus(focused, editor.cursorIndex, editor.selectIndex);
                return focused;
            }
            return ControlTokens.TryGetValue(id, out var target) ? target : default;
        }

        private static int ApplyAction(TextEditor editor, int maxLength, CommitAction action,
            StringBuilder inserted) {
            var selectionStart = Math.Max(0, Math.Min(action.SelectionStart, editor.text.Length));
            var selectionEnd = Math.Max(selectionStart, Math.Min(action.SelectionEnd, editor.text.Length));
            var previousCursor = editor.cursorIndex;
            var previousSelect = editor.selectIndex;
            var cursorMoved = Math.Min(previousCursor, previousSelect) != selectionStart ||
                Math.Max(previousCursor, previousSelect) != selectionEnd;
            editor.cursorIndex = selectionEnd;
            editor.selectIndex = selectionStart;
            var selectedLength = selectionEnd - selectionStart;
            var allowedLength = maxLength < 0
                ? action.Text.Length
                : Math.Max(0, maxLength - (editor.text.Length - selectedLength));
            var insertedLength = Math.Min(action.Text.Length, allowedLength);
            for (var i = 0; i < insertedLength; i++) {
                editor.Insert(action.Text[i]);
                inserted?.Append(action.Text[i]);
            }
            if (cursorMoved) {
                editor.cursorIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousCursor, selectionStart, selectionEnd,
                        insertedLength)));
                editor.selectIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousSelect, selectionStart, selectionEnd,
                        insertedLength)));
            }
            return insertedLength;
        }

        private static void DrawOverlay() {
            var style = new GUIStyle(GUI.skin.label) {
                fontSize = 13,
                normal = { textColor = _engine == "hangul" ? Color.green : Color.yellow }
            };
            var preedit = "";
            if (ControlTokens.TryGetValue(_focusedControl, out var target) &&
                Composition.TryGetView(target, out var view))
                preedit = view.Text;
            var text = "[CJK] engine=" + _engine + " native=" + (_nativeReady ? "ready" : "waiting") +
                " debug=" + (DebugLogging ? "on" : "off") + " focus=" +
                GUIUtility.keyboardControl + " preedit=[" + Escape(preedit) +
                "] context=" + Composition.ActiveContext + " commits=" + Composition.PendingCount;
            GUI.Label(new Rect(10f, 10f, 900f, 48f), text, style);
        }

        private static bool IsHangulLetter(KeyCode keyCode) {
            return keyCode >= KeyCode.A && keyCode <= KeyCode.Z;
        }

        private static bool ContainsNonAscii(string text) {
            foreach (var character in text) {
                if (character > 127)
                    return true;
            }
            return false;
        }

        private static string DecodeHex(string hex) {
            return Encoding.UTF8.GetString(DecodeHexBytes(hex));
        }

        private static byte[] DecodeHexBytes(string hex) {
            if ((hex.Length & 1) != 0)
                throw new FormatException("Odd hex length: " + hex.Length);
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string Escape(string text) {
            if (text == null)
                return "null";
            return text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void Shutdown() {
            if (Interlocked.Exchange(ref _shuttingDown, 1) != 0)
                return;
            Application.quitting -= Shutdown;
            _restartAt = 0;
            _nativeStop?.Invoke();
            _nativeLoaded = false;
            lock (LogLock) {
                _log?.Dispose();
                _log = null;
            }
        }

        private static void EnsureLog() {
            if (!DebugLogging || _log != null)
                return;
            var append = _logInitialized && (!File.Exists(LogPath) ||
                new FileInfo(LogPath).Length < LogMaxBytes);
            _log = new StreamWriter(LogPath, append, new UTF8Encoding(false), LogBufferSize);
            _lastLogFlush = Stopwatch.GetTimestamp();
            _logInitialized = true;
        }

        private static void WriteLog(string message) {
            if (!DebugLogging)
                return;
            lock (LogLock) {
                EnsureLog();
                if (_log == null)
                    return;
                _log.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message);
                var now = Stopwatch.GetTimestamp();
                if (now - _lastLogFlush < LogFlushInterval)
                    return;
                _log.Flush();
                _lastLogFlush = now;
                if (_log.BaseStream.Position < LogMaxBytes)
                    return;
                _log.Dispose();
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(false), LogBufferSize);
                _log.WriteLine(DateTimeOffset.Now.ToString("O") + " LOG rotated");
            }
        }
    }
}
