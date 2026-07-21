using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    [StaticConstructorOnStartup]
    public static class FcitxCjkInputMod {
        private const string BridgePath = "/tmp/fcitx5-ime-bridge";
        private const string LogPath = "/tmp/fcitxcjkinput.log";
        private const double DuplicateCommitSeconds = 0.15;

        private static readonly object LogLock = new object();
        private static readonly ConcurrentQueue<string> Responses = new ConcurrentQueue<string>();
        private static readonly Queue<PendingCommit> Commits = new Queue<PendingCommit>();

        private static StreamWriter _log;
        private static Process _bridge;
        private static Thread _reader;
        private static string _engine = "unknown";
        private static string _preedit = "";
        private static int _preeditCursor;
        private static int _preeditControl;
        private static bool _bridgeReady;
        private static bool _overlay = true;
        private static int _overlayFrame = -1;
        private static long _nextBridgeRestart;
        private static string _lastQueuedCommit = "";
        private static int _lastQueuedControl;
        private static long _lastQueuedTimestamp;

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
            try {
                _log = new StreamWriter(LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                WriteLog("INIT unity=" + Application.unityVersion + " pid=" + Process.GetCurrentProcess().Id +
                    " XMODIFIERS=" + Environment.GetEnvironmentVariable("XMODIFIERS") +
                    " SDL_IM_MODULE=" + Environment.GetEnvironmentVariable("SDL_IM_MODULE"));
                Patch();
                StartBridge();
                Log.Message("[CJK] fcitx5 SDL/IMGUI bridge initialized; log=" + LogPath);
            } catch (Exception exception) {
                WriteLog("FATAL " + exception);
                Log.Error("[CJK] initialization failed: " + exception);
            }
        }

        private static void Patch() {
            var harmony = new Harmony("scarf.fcitxcjkinput");
            var currentGetter = AccessTools.PropertyGetter(typeof(Event), "current");
            var desktopTextField = AccessTools.Method(typeof(GUI), "HandleTextFieldEventForDesktop");
            if (currentGetter == null)
                throw new MissingMethodException("UnityEngine.Event.current getter");
            if (desktopTextField == null)
                throw new MissingMethodException("UnityEngine.GUI.HandleTextFieldEventForDesktop");

            harmony.Patch(currentGetter,
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterCurrentEvent)));
            harmony.Patch(desktopTextField,
                prefix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(BeforeDesktopTextField)),
                postfix: new HarmonyMethod(typeof(FcitxCjkInputMod), nameof(AfterDesktopTextField)));
            WriteLog("PATCH Event.current=" + currentGetter + " textField=" + desktopTextField);
        }

        private static void StartBridge() {
            _nextBridgeRestart = DateTime.UtcNow.AddSeconds(2).Ticks;
            if (!File.Exists(BridgePath)) {
                WriteLog("BRIDGE missing path=" + BridgePath);
                return;
            }
            if (_bridge != null && !_bridge.HasExited)
                return;

            _bridgeReady = false;
            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = BridgePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.ErrorDataReceived += (_, args) => {
                if (!string.IsNullOrEmpty(args.Data))
                    WriteLog(args.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
            _bridge = process;
            _reader = new Thread(() => ReadBridge(process)) {
                IsBackground = true,
                Name = "FcitxCjkInput bridge reader"
            };
            _reader.Start();
            WriteLog("BRIDGE start pid=" + process.Id + " path=" + BridgePath);
        }

        private static void ReadBridge(Process process) {
            try {
                using (var reader = new StreamReader(process.StandardOutput.BaseStream, Encoding.UTF8)) {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        Responses.Enqueue(line);
                }
                WriteLog("BRIDGE stdout closed pid=" + process.Id);
            } catch (Exception exception) {
                WriteLog("BRIDGE reader failed pid=" + process.Id + " error=" + exception);
            }
        }

        private static void EnsureBridge() {
            if (_bridge != null && !_bridge.HasExited)
                return;
            if (DateTime.UtcNow.Ticks < _nextBridgeRestart)
                return;
            WriteLog("BRIDGE restart requested");
            StartBridge();
        }

        private static void Pump() {
            EnsureBridge();
            while (Responses.TryDequeue(out var line)) {
                WriteLog("RX " + line);
                if (line.StartsWith("READY:", StringComparison.Ordinal)) {
                    _bridgeReady = true;
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

        private static void AfterCurrentEvent(ref Event __result) {
            Pump();
            if (__result == null)
                return;

            if (__result.type == EventType.KeyDown && __result.keyCode == KeyCode.F11) {
                _overlay = !_overlay;
                WriteLog("UI overlay=" + _overlay);
                __result.Use();
                return;
            }

            if (__result.type != EventType.KeyDown || GUIUtility.keyboardControl == 0 ||
                _engine != "hangul")
                return;

            var shortcutModifiers = EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt;
            var suppress = (IsHangulLetter(__result.keyCode) &&
                (__result.modifiers & shortcutModifiers) == 0) ||
                (__result.keyCode == KeyCode.Backspace && _preedit.Length > 0);
            if (!suppress)
                return;

            WriteLog("KEY suppress control=" + GUIUtility.keyboardControl + " key=" + __result.keyCode +
                " char=U+" + ((int)__result.character).ToString("X4") +
                " modifiers=" + __result.modifiers + " preedit=[" + Escape(_preedit) + "]");
            __result.Use();
        }

        private static void BeforeDesktopTextField(Rect position, int id, GUIContent content,
            bool multiline, int maxLength, GUIStyle style, TextEditor editor,
            ref PreeditDrawState __state) {
            __state = default;
            Pump();
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
            var text = "[CJK] engine=" + _engine + " bridge=" + (_bridgeReady ? "ready" : "waiting") +
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
