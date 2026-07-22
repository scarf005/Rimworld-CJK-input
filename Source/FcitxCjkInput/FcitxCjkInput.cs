using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    [StaticConstructorOnStartup]
    public static class FcitxCjkInputMod {
        private const string NativeLibraryName = "libfcitxcjkinput.so";
        private const string LogPath = "/tmp/fcitxcjkinput.log";
        private const double DuplicateCommitSeconds = 0.15;
        private const int RtldNow = 2;
        private const int NativeBufferSize = 16384;

        private static readonly object LogLock = new object();
        private static readonly Queue<PendingCommit> Commits = new Queue<PendingCommit>();
        private static readonly byte[] NativeBuffer = new byte[NativeBufferSize];

        private static StreamWriter _log;
        private static IntPtr _nativeHandle;
        private static NativeStart _nativeStart;
        private static NativePoll _nativePoll;
        private static NativeIsRunning _nativeIsRunning;
        private static string _engine = "unknown";
        private static string _preedit = "";
        private static int _preeditCursor;
        private static int _preeditControl;
        private static bool _nativeReady;
        private static bool _overlay;
        private static int _overlayFrame = -1;
        private static long _nextNativeRestart;
        private static bool _nativeLoaded;
        private static string _lastQueuedCommit = "";
        private static int _lastQueuedControl;
        private static long _lastQueuedTimestamp;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeStart(uint pid);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativePoll([Out] byte[] buffer, int capacity);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeIsRunning();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string fileName, int flags);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        private struct PendingCommit {
            public readonly int ControlId;
            public readonly string Text;

            public PendingCommit(int controlId, string text) {
                ControlId = controlId;
                Text = text;
            }
        }

        private struct PreeditDrawState {
            public bool Active;
            public string ContentText;
            public string EditorText;
            public int CursorIndex;
            public int SelectIndex;
        }

        static FcitxCjkInputMod() {
            if (Application.platform != RuntimePlatform.LinuxPlayer)
                return;

            try {
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                WriteLog("INIT unity=" + Application.unityVersion + " pid=" + Process.GetCurrentProcess().Id +
                    " XMODIFIERS=" + Environment.GetEnvironmentVariable("XMODIFIERS") +
                    " SDL_IM_MODULE=" + Environment.GetEnvironmentVariable("SDL_IM_MODULE"));
                LoadNativeBridge();
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

            _nativeStart = LoadNativeFunction<NativeStart>("fcitx_bridge_start");
            _nativePoll = LoadNativeFunction<NativePoll>("fcitx_bridge_poll");
            _nativeIsRunning = LoadNativeFunction<NativeIsRunning>("fcitx_bridge_is_running");
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
            _nextNativeRestart = DateTime.UtcNow.AddSeconds(2).Ticks;
            _nativeReady = false;
            var result = _nativeStart((uint)Process.GetCurrentProcess().Id);
            WriteLog("NATIVE start result=" + result);
        }

        private static void EnsureNativeBridge() {
            if (!_nativeLoaded || _nativeIsRunning() != 0)
                return;
            if (DateTime.UtcNow.Ticks < _nextNativeRestart)
                return;
            WriteLog("NATIVE restart requested");
            StartNativeBridge();
        }

        private static void Pump() {
            EnsureNativeBridge();
            if (!_nativeLoaded)
                return;

            while (true) {
                var length = _nativePoll(NativeBuffer, NativeBuffer.Length);
                if (length <= 0)
                    break;
                var line = Encoding.UTF8.GetString(NativeBuffer, 0, length);
                if (line.StartsWith("LOG:", StringComparison.Ordinal)) {
                    WriteLog("NATIVE " + line.Substring(4));
                    continue;
                }
                if (line.StartsWith("ERROR:", StringComparison.Ordinal)) {
                    WriteLog("NATIVE " + line);
                    continue;
                }
                WriteLog("RX " + line);
                if (line.StartsWith("READY:", StringComparison.Ordinal)) {
                    _nativeReady = true;
                } else if (line.StartsWith("ENGINE:", StringComparison.Ordinal)) {
                    var nextEngine = line.Substring(7);
                    if (_engine != nextEngine)
                        WriteLog("STATE engine " + _engine + " -> " + nextEngine);
                    _engine = nextEngine;
                    if (_engine != "hangul")
                        ClearPreedit("engine=" + _engine);
                } else if (line.StartsWith("PREEDIT_HEX:", StringComparison.Ordinal)) {
                    var payload = line.Substring(12);
                    var separator = payload.IndexOf(':');
                    if (separator < 0)
                        throw new FormatException("Missing preedit cursor separator: " + line);
                    var cursorBytes = int.Parse(payload.Substring(0, separator));
                    var hex = payload.Substring(separator + 1);
                    var bytes = DecodeHexBytes(hex);
                    var clampedCursorBytes = Math.Max(0, Math.Min(cursorBytes, bytes.Length));
                    _preedit = Encoding.UTF8.GetString(bytes);
                    _preeditCursor = Encoding.UTF8.GetString(bytes, 0, clampedCursorBytes).Length;
                    _preeditControl = GUIUtility.keyboardControl;
                    WriteLog("STATE preedit control=" + _preeditControl + " cursorBytes=" + cursorBytes +
                        " cursorChars=" + _preeditCursor + " text=[" + Escape(_preedit) + "]");
                } else if (line.StartsWith("COMMIT_HEX:", StringComparison.Ordinal)) {
                    var text = DecodeHex(line.Substring(11));
                    var controlId = GUIUtility.keyboardControl;
                    if (_engine == "hangul" && ContainsNonAscii(text)) {
                        var timestamp = Stopwatch.GetTimestamp();
                        var duplicateTicks = DuplicateCommitSeconds * Stopwatch.Frequency;
                        var isDuplicate = text == _lastQueuedCommit && controlId == _lastQueuedControl &&
                            timestamp - _lastQueuedTimestamp <= duplicateTicks;
                        if (isDuplicate) {
                            WriteLog("DROP duplicate commit control=" + controlId + " text=[" +
                                Escape(text) + "] ageMs=" +
                                ((timestamp - _lastQueuedTimestamp) * 1000d / Stopwatch.Frequency).ToString("F1"));
                        } else {
                            Commits.Enqueue(new PendingCommit(controlId, text));
                            _lastQueuedCommit = text;
                            _lastQueuedControl = controlId;
                            _lastQueuedTimestamp = timestamp;
                            WriteLog("QUEUE commit control=" + controlId + " text=[" + Escape(text) +
                                "] count=" + Commits.Count);
                        }
                    } else {
                        WriteLog("DROP commit engine=" + _engine + " control=" + controlId +
                            " text=[" + Escape(text) + "] reason=ascii-or-non-hangul");
                    }
                } else if (line == "FOCUS:OUT") {
                    _engine = "unknown";
                    ClearPreedit("focus-out");
                    WriteLog("STATE focus out");
                } else {
                    WriteLog("RX unknown line=[" + Escape(line) + "]");
                }
            }
        }

        private static void BeforeRootOnGui() {
            Pump();
            var currentEvent = Event.current;
            if (currentEvent == null)
                return;

            if (currentEvent.type == EventType.KeyDown && currentEvent.keyCode == KeyCode.F11) {
                _overlay = !_overlay;
                WriteLog("UI overlay=" + _overlay);
                currentEvent.Use();
                return;
            }

            if (currentEvent.type != EventType.KeyDown || GUIUtility.keyboardControl == 0 ||
                _engine != "hangul")
                return;

            var shortcutModifiers = EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt;
            var suppress = (IsHangulLetter(currentEvent.keyCode) &&
                (currentEvent.modifiers & shortcutModifiers) == 0) ||
                (currentEvent.keyCode == KeyCode.Backspace && _preedit.Length > 0);
            if (!suppress)
                return;

            WriteLog("KEY suppress control=" + GUIUtility.keyboardControl + " key=" + currentEvent.keyCode +
                " char=U+" + ((int)currentEvent.character).ToString("X4") +
                " modifiers=" + currentEvent.modifiers + " preedit=[" + Escape(_preedit) + "]");
            currentEvent.Use();
        }

        private static void BeforeDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor,
            ref PreeditDrawState __state) {
            __state = default;
            if (GUIUtility.keyboardControl != id)
                return;

            if (Commits.Count > 0) {
                var remaining = Commits.Count;
                var inserted = new StringBuilder();
                while (remaining-- > 0) {
                    var commit = Commits.Dequeue();
                    if (commit.ControlId != 0 && commit.ControlId != id) {
                        Commits.Enqueue(commit);
                        continue;
                    }
                    foreach (var character in commit.Text) {
                        if (maxLength >= 0 && editor.text.Length >= maxLength)
                            break;
                        editor.Insert(character);
                        inserted.Append(character);
                    }
                }
                if (inserted.Length > 0) {
                    content.text = editor.text;
                    GUI.changed = true;
                    WriteLog("INSERT event=" + Event.current.type + " control=" + id + " text=[" +
                        Escape(inserted.ToString()) + "] result=[" + Escape(editor.text) + "] cursor=" +
                        editor.cursorIndex + " select=" + editor.selectIndex + " preedit=[" +
                        Escape(_preedit) + "]");
                }
            }

            if (Event.current.type != EventType.Repaint || _preeditControl != id ||
                _preedit.Length == 0)
                return;

            __state.Active = true;
            __state.ContentText = content.text;
            __state.EditorText = editor.text;
            __state.CursorIndex = editor.cursorIndex;
            __state.SelectIndex = editor.selectIndex;

            var selectionStart = Math.Min(editor.cursorIndex, editor.selectIndex);
            var selectionEnd = Math.Max(editor.cursorIndex, editor.selectIndex);
            var displayText = editor.text.Remove(selectionStart, selectionEnd - selectionStart)
                .Insert(selectionStart, _preedit);
            var displayCursor = selectionStart + Math.Min(_preeditCursor, _preedit.Length);
            editor.text = displayText;
            content.text = displayText;
            editor.cursorIndex = displayCursor;
            editor.selectIndex = displayCursor;
        }

        private static void AfterDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor,
            PreeditDrawState __state) {
            if (__state.Active) {
                content.text = __state.ContentText;
                editor.text = __state.EditorText;
                editor.cursorIndex = __state.CursorIndex;
                editor.selectIndex = __state.SelectIndex;
            }

            if (Event.current.type == EventType.Repaint && _overlay && _overlayFrame != Time.frameCount) {
                _overlayFrame = Time.frameCount;
                DrawOverlay();
            }
        }

        private static void DrawOverlay() {
            var style = new GUIStyle(GUI.skin.label) {
                fontSize = 13,
                normal = { textColor = _engine == "hangul" ? Color.green : Color.yellow }
            };
            var text = "[CJK] engine=" + _engine + " native=" + (_nativeReady ? "ready" : "waiting") +
                " focus=" + GUIUtility.keyboardControl + " preedit=[" + Escape(_preedit) +
                "] cursor=" + _preeditCursor + " commits=" + Commits.Count + "\nF11: diagnostics";
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

        private static void ClearPreedit(string reason) {
            if (_preedit.Length > 0)
                WriteLog("STATE preedit clear reason=" + reason + " old=[" + Escape(_preedit) + "]");
            _preedit = "";
            _preeditCursor = 0;
            _preeditControl = 0;
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
