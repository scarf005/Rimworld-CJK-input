using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    [StaticConstructorOnStartup]
    public static class FcitxCjkInputMod {
        private const string NativeLibraryName = "libfcitxcjkinput.so";
        private const string LogPath = "/tmp/fcitxcjkinput.log";
        private const int RtldNow = 2;
        private const int NativeBufferSize = 16384;
        private const int NativeRestartDelayMs = 2000;
        private const int KeyLogLimit = 256;

        private static readonly object LogLock = new object();
        private static readonly byte[] NativeBuffer = new byte[NativeBufferSize];
        private static readonly Dictionary<int, string> Engines = new Dictionary<int, string>();
        private static readonly Dictionary<int, ControlToken> ControlTokens =
            new Dictionary<int, ControlToken>();
        private static readonly Dictionary<ControlToken, string> ExpectedFieldTexts =
            new Dictionary<ControlToken, string>();
        private static readonly CompositionStateMachine Composition =
            new CompositionStateMachine(Stopwatch.Frequency * 2L);
        private static readonly NativeNotify NotifyCallback = OnNativeNotify;
        private static readonly SendOrPostCallback DrainCallback = _ => DrainNativeMessages();
        private static readonly SendOrPostCallback RestartCallback = _ => RestartNativeBridgeOnMainThread();

        private static StreamWriter _log;
        private static IntPtr _nativeHandle;
        private static NativeSetNotify _nativeSetNotify;
        private static NativeStart _nativeStart;
        private static NativePoll _nativePoll;
        private static SynchronizationContext _mainContext;
        private static System.Threading.Timer _restartTimer;
        private static string _engine = "unknown";
        private static bool _nativeReady;
        private static bool _overlay;
        private static int _overlayFrame = -1;
        private static bool _nativeLoaded;
        private static int _mainThreadId;
        private static int _keyLogCount;
        private static int _nativeDrainRequested;
        private static int _fallbackRestartRequested;
        private static int _focusedControl;
        private static int _focusedTextFieldFrame = -10;
        private static long _focusGeneration;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeNotify(IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeSetNotify(NativeNotify callback, IntPtr userData);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeStart(uint pid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativePoll([Out] byte[] buffer, int capacity);

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
                _mainContext = SynchronizationContext.Current;
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                WriteLog("INIT unity=" + Application.unityVersion + " pid=" + Process.GetCurrentProcess().Id +
                    " XMODIFIERS=" + Environment.GetEnvironmentVariable("XMODIFIERS") +
                    " SDL_IM_MODULE=" + Environment.GetEnvironmentVariable("SDL_IM_MODULE") +
                    " syncContext=" + (_mainContext?.GetType().FullName ?? "null"));
                LoadNativeBridge();
                _nativeSetNotify(NotifyCallback, IntPtr.Zero);
                Patch();
                StartNativeBridge();
                Log.Message("[CJK] fcitx5 SDL/IMGUI bridge initialized; log=" + LogPath);
            } catch (Exception exception) {
                WriteLog("FATAL " + exception);
                Log.Error("[CJK] initialization failed: " + exception);
            }
        }

        private static void Patch() {
            var harmony = new Harmony("scarf.fcitxcjkinput");
            var rootOnGui = AccessTools.Method(typeof(Root), "OnGUI");
            var desktopTextField = AccessTools.Method(typeof(GUI), "HandleTextFieldEventForDesktop");
            if (rootOnGui == null)
                throw new MissingMethodException("Verse.Root.OnGUI");
            if (desktopTextField == null)
                throw new MissingMethodException("UnityEngine.GUI.HandleTextFieldEventForDesktop");

            harmony.Patch(rootOnGui,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeRootOnGui)));
            harmony.Patch(desktopTextField,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeDesktopTextField)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterDesktopTextField)));
            WriteLog("PATCH Root.OnGUI=" + rootOnGui + " textField=" + desktopTextField);
        }

        private static void LoadNativeBridge() {
            var content = LoadedModManager.RunningModsListForReading
                .FirstOrDefault(mod => mod.PackageId == "scarf.fcitxcjkinput");
            var assemblyDirectory = content != null
                ? Path.Combine(content.RootDir, "1.6", "Assemblies")
                : Path.GetDirectoryName(typeof(FcitxCjkInputMod).Assembly.Location);
            var path = Path.Combine(assemblyDirectory, NativeLibraryName);
            _nativeHandle = dlopen(path, RtldNow);
            if (_nativeHandle == IntPtr.Zero)
                throw new DllNotFoundException(path + ": " + GetDlError());

            _nativeSetNotify = LoadNativeFunction<NativeSetNotify>("fcitx_bridge_set_notify");
            _nativeStart = LoadNativeFunction<NativeStart>("fcitx_bridge_start");
            _nativePoll = LoadNativeFunction<NativePoll>("fcitx_bridge_poll");
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
            _restartTimer?.Dispose();
            _restartTimer = null;
            _nativeReady = false;
            var result = _nativeStart((uint)Process.GetCurrentProcess().Id);
            WriteLog("NATIVE start result=" + result);
            if (result != 0)
                ScheduleNativeRestart();
        }

        private static void RestartNativeBridgeOnMainThread() {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                StartNativeBridge();
            else
                Interlocked.Exchange(ref _fallbackRestartRequested, 1);
        }

        private static void ScheduleNativeRestart() {
            _restartTimer?.Dispose();
            _restartTimer = new System.Threading.Timer(_ => {
                if (_mainContext != null)
                    _mainContext.Post(RestartCallback, null);
                else
                    Interlocked.Exchange(ref _fallbackRestartRequested, 1);
            }, null, NativeRestartDelayMs, Timeout.Infinite);
        }

        private static void OnNativeNotify(IntPtr userData) {
            Interlocked.Exchange(ref _nativeDrainRequested, 1);
            try {
                if (_mainContext != null) {
                    _mainContext.Post(DrainCallback, null);
                    return;
                }
            } catch (Exception exception) {
                WriteLog("NATIVE notify fallback error=" + exception);
            }
        }

        private static void DrainNativeMessages() {
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId) {
                Interlocked.Exchange(ref _nativeDrainRequested, 1);
                return;
            }
            Interlocked.Exchange(ref _nativeDrainRequested, 0);
            if (!_nativeLoaded)
                return;

            while (true) {
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

            WriteLog("RX " + line);
            if (line.StartsWith("READY:", StringComparison.Ordinal)) {
                _nativeReady = true;
                return;
            }
            if (line == "STOPPED") {
                _nativeReady = false;
                Engines.Clear();
                Composition.Reset();
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
            if (kind == "ENGINE") {
                Engines[contextId] = payload;
                if (payload != "hangul")
                    Composition.CancelComposition(contextId);
                if (Composition.ActiveContext == 0 || Composition.ActiveContext == contextId)
                    SetEngine(payload);
                return;
            }
            if (kind == "FOCUS") {
                if (payload == "IN") {
                    Composition.FocusIn(contextId, sequence);
                    SetEngine(Engines.TryGetValue(contextId, out var engine) ? engine : "unknown");
                } else if (payload == "OUT" && Composition.FocusOut(contextId, sequence)) {
                    SetEngine("unknown");
                }
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
                if (Composition.Preedit(contextId, sequence, text, cursor)) {
                    if (Engines.TryGetValue(contextId, out var engine))
                        SetEngine(engine);
                    WriteLog("STATE preedit context=" + contextId + " control=" + _focusedControl +
                        " cursorBytes=" + cursorBytes + " cursorChars=" + cursor + " text=[" +
                        Escape(text) + "]");
                } else {
                    WriteLog("DROP preedit context=" + contextId + " sequence=" + sequence +
                        " reason=inactive-or-unbound");
                }
                return;
            }
            if (kind == "COMMIT") {
                var text = DecodeHex(payload);
                Engines.TryGetValue(contextId, out var engine);
                if (engine == "hangul" && ContainsNonAscii(text) &&
                    Composition.Commit(contextId, sequence, text, now)) {
                    WriteLog("QUEUE commit context=" + contextId + " sequence=" + sequence +
                        " text=[" + Escape(text) + "] count=" + Composition.PendingCount);
                } else {
                    WriteLog("DROP commit context=" + contextId + " sequence=" + sequence +
                        " engine=" + (engine ?? "unknown") + " text=[" + Escape(text) + "]");
                }
                return;
            }
            WriteLog("RX unknown event kind=" + kind + " payload=[" + Escape(payload) + "]");
        }

        private static void SetEngine(string engine) {
            if (_engine != engine)
                WriteLog("STATE engine " + _engine + " -> " + engine);
            _engine = engine;
        }

        private static void BeforeRootOnGui() {
            if (Interlocked.Exchange(ref _nativeDrainRequested, 0) != 0)
                DrainNativeMessages();
            if (Interlocked.Exchange(ref _fallbackRestartRequested, 0) != 0)
                StartNativeBridge();
            Composition.DiscardExpired(Stopwatch.GetTimestamp());
            if (GUIUtility.keyboardControl == 0 && _focusedControl != 0) {
                _focusedControl = 0;
                Composition.Blur();
            }
            var textFieldActive = ImeRouting.TextFieldIsActive(GUIUtility.keyboardControl,
                _focusedControl, _focusedTextFieldFrame, Time.frameCount);
            SetImeCompositionMode(textFieldActive, "root");

            var currentEvent = Event.current;
            if (currentEvent == null)
                return;
            LogRootKey(currentEvent, textFieldActive);

            if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.F11) {
                _overlay = !_overlay;
                WriteLog("UI overlay=" + _overlay);
                currentEvent.Use();
                return;
            }
        }

        private static void BeforeDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor) {
            var target = ObserveEditor(id, editor);
            if (target.Id == 0)
                return;
            SuppressRawHangulKey(id);
            VerifyCommittedText(target, id, content, editor);

            var actions = Composition.TakeActions(target, Stopwatch.GetTimestamp());
            if (actions.Count == 0)
                return;

            var inserted = new StringBuilder();
            foreach (var action in actions)
                ApplyAction(editor, maxLength, action, inserted);
            content.text = editor.text;
            ExpectedFieldTexts[target] = editor.text;
            GUI.changed = true;
            WriteLog("INSERT event=" + Event.current.type + " control=" + id + " text=[" +
                Escape(inserted.ToString()) + "] result=[" + Escape(editor.text) + "] cursor=" +
                editor.cursorIndex + " select=" + editor.selectIndex + " pending=" +
                Composition.PendingCount);
            if (GUIUtility.keyboardControl == id)
                Composition.Focus(target, editor.cursorIndex, editor.selectIndex);
        }

        private static void AfterDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor) {
            var target = ObserveEditor(id, editor);
            if (Event.current.type == EventType.Repaint && GUIUtility.keyboardControl == id &&
                Composition.TryGetView(target, out var view))
                DrawPreedit(editor, view);

            if (Event.current.type == EventType.Repaint && _overlay && _overlayFrame != Time.frameCount) {
                _overlayFrame = Time.frameCount;
                DrawOverlay();
            }
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
            if (TryReserveKeyLog())
                WriteLog("KEY textfield control=" + id + " key=" + currentEvent.keyCode +
                    " char=U+" + ((int)currentEvent.character).ToString("X4") +
                    " modifiers=" + currentEvent.modifiers + " engine=" + _engine +
                    " preedit=" + Composition.HasPreedit + " suppress=" + suppress);
            if (suppress)
                currentEvent.Use();
        }

        private static void SetImeCompositionMode(bool textFieldActive, string reason) {
            var requested = textFieldActive ? IMECompositionMode.On : IMECompositionMode.Off;
            var previous = Input.imeCompositionMode;
            if (previous == requested)
                return;
            Input.imeCompositionMode = requested;
            WriteLog("IME mode " + previous + " -> " + Input.imeCompositionMode + " reason=" + reason +
                " keyboardControl=" + GUIUtility.keyboardControl + " focusedControl=" +
                _focusedControl + " seenFrame=" + _focusedTextFieldFrame + " frame=" +
                Time.frameCount);
        }

        private static void LogRootKey(Event currentEvent, bool textFieldActive) {
            if (currentEvent.type != EventType.KeyDown ||
                (!IsHangulLetter(currentEvent.keyCode) && currentEvent.keyCode != KeyCode.Backspace))
                return;
            if (TryReserveKeyLog())
                WriteLog("KEY root key=" + currentEvent.keyCode + " char=U+" +
                    ((int)currentEvent.character).ToString("X4") + " modifiers=" +
                    currentEvent.modifiers + " engine=" + _engine + " ime=" +
                    Input.imeCompositionMode + " textFieldActive=" + textFieldActive +
                    " keyboardControl=" + GUIUtility.keyboardControl + " focusedControl=" +
                    _focusedControl);
        }

        private static bool TryReserveKeyLog() {
            if (_keyLogCount >= KeyLogLimit)
                return false;
            _keyLogCount++;
            return true;
        }

        private static void VerifyCommittedText(ControlToken target, int id, GUIContent content,
            TextEditor editor) {
            if (!ExpectedFieldTexts.TryGetValue(target, out var expected))
                return;
            ExpectedFieldTexts.Remove(target);
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

            try {
                editor.text = displayText;
                editor.cursorIndex = displayCursor;
                editor.selectIndex = displayCursor;
                editor.DrawCursor(displayText);
            } finally {
                editor.text = originalText;
                editor.cursorIndex = originalCursor;
                editor.selectIndex = originalSelect;
            }
        }

        private static ControlToken ObserveEditor(int id, TextEditor editor) {
            if (GUIUtility.keyboardControl == id) {
                if (_focusedControl != id || !ControlTokens.TryGetValue(id, out var focused)) {
                    _focusedControl = id;
                    focused = new ControlToken(id, ++_focusGeneration);
                    ControlTokens[id] = focused;
                    WriteLog("STATE focus control=" + id + " generation=" + focused.Generation);
                }
                _focusedTextFieldFrame = Time.frameCount;
                SetImeCompositionMode(true, "text-field");
                Composition.Focus(focused, editor.cursorIndex, editor.selectIndex);
                return focused;
            }
            return ControlTokens.TryGetValue(id, out var target) ? target : default;
        }

        private static void ApplyAction(TextEditor editor, int maxLength, CommitAction action,
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
                inserted.Append(action.Text[i]);
            }
            if (cursorMoved) {
                editor.cursorIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousCursor, selectionStart, selectionEnd,
                        insertedLength)));
                editor.selectIndex = Math.Max(0, Math.Min(editor.text.Length,
                    TextEditMath.TransformIndex(previousSelect, selectionStart, selectionEnd,
                        insertedLength)));
            }
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
                " focus=" + GUIUtility.keyboardControl + " preedit=[" + Escape(preedit) +
                "] context=" + Composition.ActiveContext + " commits=" + Composition.PendingCount +
                "\nF11: diagnostics";
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

        private static void WriteLog(string message) {
            lock (LogLock) {
                _log?.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message);
            }
        }
    }
}
