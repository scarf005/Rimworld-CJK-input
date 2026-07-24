namespace FcitxCjkInput

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text
open System.Threading

open HarmonyLib
open RimWorld
open UnityEngine
open Verse

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativeNotify = delegate of IntPtr -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativeSetNotify = delegate of NativeNotify * IntPtr -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativeSetDebug = delegate of int -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativeStart = delegate of uint32 -> int

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativePoll = delegate of byte[] * int -> int

module NativeLib =
    [<DllImport("libdl.so.2")>]
    extern IntPtr dlopen(string fileName, int flags)

    [<DllImport("libdl.so.2")>]
    extern IntPtr dlsym(IntPtr handle, string symbol)

    [<DllImport("libdl.so.2")>]
    extern IntPtr dlerror()


[<StaticConstructorOnStartup; AbstractClass; Sealed>]
type FcitxCjkInputMod =

    [<Literal>]
    static let NativeLibraryName = "libfcitxcjkinput.so"

    [<Literal>]
    static let LogPath = "/tmp/fcitxcjkinput.log"

    [<Literal>]
    static let RtldNow = 2

    [<Literal>]
    static let NativeBufferSize = 16384

    [<Literal>]
    static let NativeRestartDelayMs = 2000

    static let LogLock = obj ()
    static let NativeBuffer = Array.zeroCreate<byte> NativeBufferSize
    static let Engines = Dictionary<int, string>()
    static let ControlTokens = Dictionary<int, ControlToken>()
    static let ExpectedFieldTexts = Dictionary<ControlToken, string>()
    static let Composition = CompositionStateMachine(Stopwatch.Frequency * 2L)
    static let CommittedCharacters = CommittedCharacterTracker()
    static let DirectionalKeys = DirectionalKeyState()

    static let mutable LogWriter: StreamWriter = null
    static let mutable NativeHandle = IntPtr.Zero
    static let mutable NativeSetNotify: NativeSetNotify = null
    static let mutable NativeSetDebug: NativeSetDebug = null
    static let mutable NativeStart: NativeStart = null
    static let mutable NativePoll: NativePoll = null
    static let mutable MainContext: SynchronizationContext = null
    static let mutable RestartTimer: Timer = null
    static let mutable Engine = "unknown"
    static let mutable NativeReady = false
    static let mutable Overlay = false
    static let mutable OverlayFrame = -1
    static let mutable NativeLoaded = false
    static let mutable MainThreadId = -1
    static let mutable NativeDrainRequested = 0
    static let mutable FallbackRestartRequested = 0
    static let mutable FocusedControl = 0
    static let mutable FocusedTextFieldFrame = -10
    static let mutable FocusGeneration = 0L
    static let mutable LogInitialized = false
    static let mutable TextFieldActive = false
    static let mutable DebugLogEnabled = false

    static do
        if Application.platform <> RuntimePlatform.LinuxPlayer then
            ()
        else
            try
                MainThreadId <- Thread.CurrentThread.ManagedThreadId
                MainContext <- SynchronizationContext.Current
                FcitxCjkInputMod.EnsureLog()
                FcitxCjkInputMod.WriteRuntimeHeader "INIT"
                FcitxCjkInputMod.LoadNativeBridge()
                NativeSetNotify.Invoke(NativeNotify(FcitxCjkInputMod.OnNativeNotify), IntPtr.Zero)

                NativeSetDebug.Invoke(
                    if DebugLogEnabled then
                        1
                    else
                        0
                )

                FcitxCjkInputMod.DoPatch()
                FcitxCjkInputMod.StartNativeBridge()

                Log.Message(
                    "[CJK] fcitx5 SDL/IMGUI bridge initialized; "
                    + (if DebugLogEnabled then
                           "log=" + LogPath
                       else
                           "debug log disabled")
                )
            with ex ->
                FcitxCjkInputMod.WriteLog("FATAL " + ex.ToString())
                Log.Error("[CJK] initialization failed: " + ex.ToString())

    static member SetDebugLogEnabled(v: bool) = DebugLogEnabled <- v

    static member DebugLogging() = DebugLogEnabled

    static member Escape(text: string) =
        if isNull text then
            "null"
        else
            text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n")

    static member IsHangulLetter(keyCode: KeyCode) = keyCode >= KeyCode.A && keyCode <= KeyCode.Z

    static member ContainsNonAscii(text: string) = text |> Seq.exists (fun c -> c > char 127)

    static member DecodeHexBytes(hex: string) =
        if (hex.Length &&& 1) <> 0 then
            raise (FormatException("Odd hex length: " + hex.Length.ToString()))

        let bytes = Array.zeroCreate<byte> (hex.Length / 2)

        for i in 0 .. bytes.Length - 1 do
            bytes.[i] <- Convert.ToByte(hex.Substring(i * 2, 2), 16)

        bytes

    static member DecodeHex hex = Encoding.UTF8.GetString(FcitxCjkInputMod.DecodeHexBytes hex)

    static member EnsureLog() =
        if FcitxCjkInputMod.DebugLogging() && LogWriter = null then
            LogWriter <- new StreamWriter(LogPath, LogInitialized, UTF8Encoding(false), AutoFlush = true)
            LogInitialized <- true

    static member WriteLog(message: string) =
        if not (FcitxCjkInputMod.DebugLogging()) then
            ()
        else
            lock LogLock (fun () ->
                FcitxCjkInputMod.EnsureLog()

                if LogWriter <> null then
                    LogWriter.WriteLine(DateTimeOffset.Now.ToString("O") + " " + message))

    static member WriteRuntimeHeader reason =
        FcitxCjkInputMod.WriteLog(
            reason
            + " unity="
            + Application.unityVersion
            + " pid="
            + (Process.GetCurrentProcess().Id.ToString())
            + " XMODIFIERS="
            + Environment.GetEnvironmentVariable("XMODIFIERS")
            + " SDL_IM_MODULE="
            + Environment.GetEnvironmentVariable("SDL_IM_MODULE")
            + " syncContext="
            + (if MainContext <> null then
                   MainContext.GetType().FullName
               else
                   "null")
            + " engine="
            + Engine
            + " native="
            + NativeReady.ToString()
            + " ime="
            + Input.imeCompositionMode.ToString()
        )

    static member DescribeEvent() =
        let currentEvent = UnityEngine.Event.current

        if isNull currentEvent then
            "event=null frame=" + Time.frameCount.ToString()
        else
            "event="
            + currentEvent.``type``.ToString()
            + " raw="
            + currentEvent.rawType.ToString()
            + " key="
            + currentEvent.keyCode.ToString()
            + " char=U+"
            + (int currentEvent.character).ToString("X4")
            + " modifiers="
            + currentEvent.modifiers.ToString()
            + " command=["
            + FcitxCjkInputMod.Escape currentEvent.commandName
            + "] frame="
            + Time.frameCount.ToString()

    static member GetDlError() =
        let ptr = NativeLib.dlerror ()

        if ptr = IntPtr.Zero then
            "unknown dlerror"
        else
            Marshal.PtrToStringAnsi(ptr)

    static member LoadNativeFunction<'T when 'T :> Delegate> name =
        let ptr = NativeLib.dlsym (NativeHandle, name)

        if ptr = IntPtr.Zero then
            raise (MissingMethodException(name + ": " + FcitxCjkInputMod.GetDlError()))

        Marshal.GetDelegateForFunctionPointer(ptr, typeof<'T>) :?> 'T

    static member TryGetValue (dict: Dictionary<'K, 'V>) (key: 'K) : 'V option =
        let mutable value = Unchecked.defaultof<'V>

        if dict.TryGetValue(key, &value) then
            Some value
        else
            None

    static member LoadNativeBridge() =
        let content =
            LoadedModManager.RunningModsListForReading
            |> Seq.tryFind (fun (mod': ModContentPack) -> mod'.PackageId = "scarf.fcitxcjkinput")

        let assemblyDirectory =
            match content with
            | Some c -> Path.Combine(c.RootDir, "1.6", "Assemblies")
            | None ->
                System.Reflection.Assembly.GetExecutingAssembly().Location
                |> Path.GetDirectoryName

        let path = Path.Combine(assemblyDirectory, NativeLibraryName)
        NativeHandle <- NativeLib.dlopen (path, RtldNow)

        if NativeHandle = IntPtr.Zero then
            raise (DllNotFoundException(path + ": " + FcitxCjkInputMod.GetDlError()))

        NativeSetNotify <- FcitxCjkInputMod.LoadNativeFunction<NativeSetNotify> "fcitx_bridge_set_notify"
        NativeSetDebug <- FcitxCjkInputMod.LoadNativeFunction<NativeSetDebug> "fcitx_bridge_set_debug"
        NativeStart <- FcitxCjkInputMod.LoadNativeFunction<NativeStart> "fcitx_bridge_start"
        NativePoll <- FcitxCjkInputMod.LoadNativeFunction<NativePoll> "fcitx_bridge_poll"
        NativeLoaded <- true
        FcitxCjkInputMod.WriteLog("NATIVE loaded path=" + path)

    static member SetEngine(newEngine: string) =
        if FcitxCjkInputMod.DebugLogging() && Engine <> newEngine then
            FcitxCjkInputMod.WriteLog("STATE engine " + Engine + " -> " + newEngine)

        Engine <- newEngine

    static member SetImeCompositionMode textFieldActive' reason =
        let requested =
            if textFieldActive' then
                IMECompositionMode.On
            else
                IMECompositionMode.Off

        let previous = Input.imeCompositionMode

        if previous <> requested then
            Input.imeCompositionMode <- requested

            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "IME mode "
                    + previous.ToString()
                    + " -> "
                    + Input.imeCompositionMode.ToString()
                    + " reason="
                    + reason
                    + " keyboardControl="
                    + GUIUtility.keyboardControl.ToString()
                    + " focusedControl="
                    + FocusedControl.ToString()
                    + " seenFrame="
                    + FocusedTextFieldFrame.ToString()
                    + " frame="
                    + Time.frameCount.ToString()
                )

    static member HandleContextEvent contextId sequence kind payload =
        let now = Stopwatch.GetTimestamp()

        match kind with
        | "ENGINE" ->
            Engines.[contextId] <- payload

            if payload <> "hangul" then
                Composition.CancelComposition(contextId)
                DirectionalKeys.Clear()

            if Composition.ActiveContext = 0 || Composition.ActiveContext = contextId then
                FcitxCjkInputMod.SetEngine payload
        | "FOCUS" ->
            match payload with
            | "IN" ->
                Composition.FocusIn(contextId, sequence)
                let engine = defaultArg (FcitxCjkInputMod.TryGetValue Engines contextId) "unknown"
                FcitxCjkInputMod.SetEngine engine
            | "OUT" ->
                DirectionalKeys.Clear()

                if Composition.FocusOut(contextId, sequence) then
                    FcitxCjkInputMod.SetEngine "unknown"
            | _ -> ()
        | "KEY" ->
            let separator = payload.IndexOf(':')

            if separator < 0 then
                raise (FormatException("Missing key release separator: " + payload))

            let keyValue = Int32.Parse(payload.Substring(0, separator))
            let release = Int32.Parse(payload.Substring(separator + 1)) <> 0
            DirectionalKeys.Update(keyValue, release)
        | "PREEDIT" ->
            let separator = payload.IndexOf(':')

            if separator < 0 then
                raise (FormatException("Missing preedit cursor separator: " + payload))

            let cursorBytes = Int32.Parse(payload.Substring(0, separator))
            let bytes = FcitxCjkInputMod.DecodeHexBytes(payload.Substring(separator + 1))
            let clampedCursorBytes = max 0 (min cursorBytes bytes.Length)
            let text = Encoding.UTF8.GetString(bytes)
            let cursor = Encoding.UTF8.GetString(bytes, 0, clampedCursorBytes).Length

            if Composition.Preedit(contextId, sequence, text, cursor) then
                match FcitxCjkInputMod.TryGetValue Engines contextId with
                | Some eng -> FcitxCjkInputMod.SetEngine eng
                | None -> ()

                if FcitxCjkInputMod.DebugLogging() then
                    FcitxCjkInputMod.WriteLog(
                        "STATE preedit context="
                        + contextId.ToString()
                        + " control="
                        + FocusedControl.ToString()
                        + " cursorBytes="
                        + cursorBytes.ToString()
                        + " cursorChars="
                        + cursor.ToString()
                        + " text=["
                        + FcitxCjkInputMod.Escape text
                        + "]"
                    )
            elif FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "DROP preedit context="
                    + contextId.ToString()
                    + " sequence="
                    + sequence.ToString()
                    + " reason=inactive-or-unbound"
                )
        | "COMMIT" ->
            let text = FcitxCjkInputMod.DecodeHex payload

            match FcitxCjkInputMod.TryGetValue Engines contextId with
            | Some eng when
                eng = "hangul"
                && FcitxCjkInputMod.ContainsNonAscii text
                && Composition.Commit(contextId, sequence, text, now)
                ->
                if FcitxCjkInputMod.DebugLogging() then
                    FcitxCjkInputMod.WriteLog(
                        "QUEUE commit context="
                        + contextId.ToString()
                        + " sequence="
                        + sequence.ToString()
                        + " text=["
                        + FcitxCjkInputMod.Escape text
                        + "] count="
                        + Composition.PendingCount.ToString()
                    )
            | _ ->
                if FcitxCjkInputMod.DebugLogging() then
                    let engine = defaultArg (FcitxCjkInputMod.TryGetValue Engines contextId) "unknown"

                    FcitxCjkInputMod.WriteLog(
                        "DROP commit context="
                        + contextId.ToString()
                        + " sequence="
                        + sequence.ToString()
                        + " engine="
                        + engine
                        + " text=["
                        + FcitxCjkInputMod.Escape text
                        + "]"
                    )
        | _ ->
            FcitxCjkInputMod.WriteLog(
                "RX unknown event kind="
                + kind
                + " payload=["
                + FcitxCjkInputMod.Escape payload
                + "]"
            )

    static member HandleNativeMessage(line: string) =
        if line.StartsWith("LOG:", StringComparison.Ordinal) then
            FcitxCjkInputMod.WriteLog("NATIVE " + line.Substring(4))
        elif line.StartsWith("ERROR:", StringComparison.Ordinal) then
            FcitxCjkInputMod.WriteLog("NATIVE " + line)
        else
            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog("RX " + line)

            if line.StartsWith("READY:", StringComparison.Ordinal) then
                NativeReady <- true
            elif line = "STOPPED" then
                NativeReady <- false
                Engines.Clear()
                Composition.Reset()
                CommittedCharacters.Clear()
                DirectionalKeys.Clear()
                FcitxCjkInputMod.SetEngine "unknown"
                FcitxCjkInputMod.ScheduleNativeRestart()
            elif not (line.StartsWith("EVENT:", StringComparison.Ordinal)) then
                FcitxCjkInputMod.WriteLog("RX unknown line=[" + FcitxCjkInputMod.Escape line + "]")
            else
                let parts = line.Split([| ':' |], 5)

                if
                    parts.Length <> 5
                    || not (Int32.TryParse(parts.[1], ref Unchecked.defaultof<int>))
                    || not (Int64.TryParse(parts.[2], ref Unchecked.defaultof<int64>))
                then
                    raise (FormatException("Invalid native event: " + line))

                let contextId = Int32.Parse(parts.[1])
                let sequence = Int64.Parse(parts.[2])
                FcitxCjkInputMod.HandleContextEvent contextId sequence parts.[3] parts.[4]

    static member DrainNativeMessages() =
        if Thread.CurrentThread.ManagedThreadId <> MainThreadId then
            let mutable dr = NativeDrainRequested
            dr <- 1
            NativeDrainRequested <- dr
        else
            let mutable dr = NativeDrainRequested
            dr <- 0
            NativeDrainRequested <- dr

            if NativeLoaded then
                let mutable continueLoop = true

                while continueLoop do
                    let length = NativePoll.Invoke(NativeBuffer, NativeBuffer.Length)

                    if length <= 0 then
                        continueLoop <- false
                    else
                        let line = Encoding.UTF8.GetString(NativeBuffer, 0, length)

                        try
                            FcitxCjkInputMod.HandleNativeMessage line
                        with ex ->
                            FcitxCjkInputMod.WriteLog(
                                "RX error line=["
                                + FcitxCjkInputMod.Escape line
                                + "] exception="
                                + ex.ToString()
                            )

    static member OnNativeNotify(_: IntPtr) =
        let mutable dr = NativeDrainRequested
        dr <- 1
        NativeDrainRequested <- dr

        try
            if MainContext <> null then
                MainContext.Post(SendOrPostCallback(fun _ -> FcitxCjkInputMod.DrainNativeMessages()), null)
        with ex ->
            FcitxCjkInputMod.WriteLog("NATIVE notify fallback error=" + ex.ToString())

    static member RestartNativeBridgeOnMainThread() =
        if Thread.CurrentThread.ManagedThreadId = MainThreadId then
            FcitxCjkInputMod.StartNativeBridge()
        else
            let mutable fr = FallbackRestartRequested
            fr <- 1
            FallbackRestartRequested <- fr

    static member StartNativeBridge() =
        if RestartTimer <> null then
            RestartTimer.Dispose()
            RestartTimer <- null

        NativeReady <- false
        let result = NativeStart.Invoke(uint32 (Process.GetCurrentProcess().Id))
        FcitxCjkInputMod.WriteLog("NATIVE start result=" + result.ToString())

        if result <> 0 then
            FcitxCjkInputMod.ScheduleNativeRestart()

    static member ScheduleNativeRestart() =
        if RestartTimer <> null then
            RestartTimer.Dispose()

        let mutable fr = FallbackRestartRequested

        RestartTimer <-
            new Timer(
                TimerCallback(fun _ ->
                    if MainContext <> null then
                        MainContext.Post(
                            SendOrPostCallback(fun _ -> FcitxCjkInputMod.RestartNativeBridgeOnMainThread()),
                            null
                        )
                    else
                        fr <- 1
                        FallbackRestartRequested <- fr),
                null,
                NativeRestartDelayMs,
                Timeout.Infinite
            )

    static member DrawOverlay() =
        let style = GUIStyle(GUI.skin.label)
        style.fontSize <- 13

        style.normal.textColor <-
            if Engine = "hangul" then
                Color.green
            else
                Color.yellow

        let preedit =
            match FcitxCjkInputMod.TryGetValue ControlTokens FocusedControl with
            | Some target ->
                match Composition.TryGetView(target) with
                | Some view -> view.Text
                | None -> ""
            | None -> ""

        let text =
            "[CJK] engine="
            + Engine
            + " native="
            + (if NativeReady then
                   "ready"
               else
                   "waiting")
            + " debug="
            + (if FcitxCjkInputMod.DebugLogging() then
                   "on"
               else
                   "off")
            + " focus="
            + GUIUtility.keyboardControl.ToString()
            + " preedit=["
            + FcitxCjkInputMod.Escape preedit
            + "] context="
            + Composition.ActiveContext.ToString()
            + " commits="
            + Composition.PendingCount.ToString()
            + "\nF11: diagnostics"

        GUI.Label(Rect(10.0f, 10.0f, 900.0f, 48.0f), text, style)

    static member ObserveEditor id (editor: TextEditor) =
        if GUIUtility.keyboardControl = id then
            if
                FocusedControl <> id
                || Option.isNone (FcitxCjkInputMod.TryGetValue ControlTokens id)
            then
                FocusedControl <- id
                FocusGeneration <- FocusGeneration + 1L
                let focused = ControlToken(id, FocusGeneration)
                ControlTokens.[id] <- focused

                if FcitxCjkInputMod.DebugLogging() then
                    FcitxCjkInputMod.WriteLog(
                        "STATE focus control="
                        + id.ToString()
                        + " generation="
                        + focused.Generation.ToString()
                    )

            FocusedTextFieldFrame <- Time.frameCount
            TextFieldActive <- true
            FcitxCjkInputMod.SetImeCompositionMode true "text-field"
            Composition.Focus(ControlTokens.[id], editor.cursorIndex, editor.selectIndex)
            ControlTokens.[id]
        else
            defaultArg (FcitxCjkInputMod.TryGetValue ControlTokens id) (Unchecked.defaultof<ControlToken>)

    static member ApplyAction (editor: TextEditor) maxLength (action: CommitAction) (inserted: StringBuilder) =
        let selStart = max 0 (min action.SelectionStart editor.text.Length)
        let selEnd = max selStart (min action.SelectionEnd editor.text.Length)
        let previousCursor = editor.cursorIndex
        let previousSelect = editor.selectIndex

        let cursorMoved =
            min previousCursor previousSelect <> selStart
            || max previousCursor previousSelect <> selEnd

        editor.cursorIndex <- selEnd
        editor.selectIndex <- selStart
        let selectedLength = selEnd - selStart

        let allowedLength =
            if maxLength < 0 then
                action.Text.Length
            else
                max 0 (maxLength - (editor.text.Length - selectedLength))

        let insertedLength = min action.Text.Length allowedLength

        for i in 0 .. insertedLength - 1 do
            editor.Insert(action.Text.[i])

            if inserted <> null then
                inserted.Append(action.Text.[i]) |> ignore

        if cursorMoved then
            editor.cursorIndex <-
                max
                    0
                    (min editor.text.Length (TextEditMath.transformIndex previousCursor selStart selEnd insertedLength))

            editor.selectIndex <-
                max
                    0
                    (min editor.text.Length (TextEditMath.transformIndex previousSelect selStart selEnd insertedLength))

        insertedLength

    static member DrawPreedit (editor: TextEditor) (view: CompositionView) =
        let originalText = editor.text
        let originalCursor = editor.cursorIndex
        let originalSelect = editor.selectIndex
        let selStart = max 0 (min view.SelectionStart originalText.Length)
        let selEnd = max selStart (min view.SelectionEnd originalText.Length)
        let displayText = TextEditMath.replaceRange originalText selStart selEnd view.Text
        let displayCursor = selStart + min view.Cursor view.Text.Length

        if FcitxCjkInputMod.DebugLogging() then
            FcitxCjkInputMod.WriteLog(
                "DRAW preedit original=["
                + FcitxCjkInputMod.Escape originalText
                + "] display=["
                + FcitxCjkInputMod.Escape displayText
                + "] selection="
                + selStart.ToString()
                + ":"
                + selEnd.ToString()
                + " displayCursor="
                + displayCursor.ToString()
                + " "
                + FcitxCjkInputMod.DescribeEvent()
            )

        try
            editor.text <- displayText
            editor.cursorIndex <- displayCursor
            editor.selectIndex <- displayCursor
            editor.DrawCursor(displayText)
        finally
            editor.text <- originalText
            editor.cursorIndex <- originalCursor
            editor.selectIndex <- originalSelect

            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "DRAW restore editor=["
                    + FcitxCjkInputMod.Escape editor.text
                    + "] cursor="
                    + editor.cursorIndex.ToString()
                    + " select="
                    + editor.selectIndex.ToString()
                    + " "
                    + FcitxCjkInputMod.DescribeEvent()
                )

    static member VerifyCommittedText target id (content: GUIContent) (editor: TextEditor) =
        match FcitxCjkInputMod.TryGetValue ExpectedFieldTexts target with
        | Some expected ->
            ExpectedFieldTexts.Remove(target) |> ignore

            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "VERIFY commit control="
                    + id.ToString()
                    + " expected=["
                    + FcitxCjkInputMod.Escape expected
                    + "] content=["
                    + FcitxCjkInputMod.Escape content.text
                    + "] editor=["
                    + FcitxCjkInputMod.Escape editor.text
                    + "] event="
                    + UnityEngine.Event.current.``type``.ToString()
                )
        | None -> ()

    static member LogTextField stage (target: ControlToken) id (content: GUIContent) (editor: TextEditor) =
        if
            FcitxCjkInputMod.DebugLogging()
            && (GUIUtility.keyboardControl = id || target.Id <> 0)
        then
            FcitxCjkInputMod.WriteLog(
                stage
                + " control="
                + id.ToString()
                + " token="
                + target.Id.ToString()
                + ":"
                + target.Generation.ToString()
                + " keyboardControl="
                + GUIUtility.keyboardControl.ToString()
                + " hotControl="
                + GUIUtility.hotControl.ToString()
                + " contentObject="
                + RuntimeHelpers.GetHashCode(content).ToString()
                + " editorObject="
                + RuntimeHelpers.GetHashCode(editor).ToString()
                + " content=["
                + FcitxCjkInputMod.Escape content.text
                + "] editor=["
                + FcitxCjkInputMod.Escape editor.text
                + "] cursor="
                + editor.cursorIndex.ToString()
                + " select="
                + editor.selectIndex.ToString()
                + " guiChanged="
                + GUI.changed.ToString()
                + " "
                + FcitxCjkInputMod.DescribeEvent()
            )

    static member LogRootEvent (currentEvent: UnityEngine.Event) active =
        if
            FcitxCjkInputMod.DebugLogging()
            && (currentEvent.``type`` = EventType.KeyDown
                || currentEvent.``type`` = EventType.KeyUp)
        then
            FcitxCjkInputMod.WriteLog(
                "ROOT "
                + FcitxCjkInputMod.DescribeEvent()
                + " engine="
                + Engine
                + " ime="
                + Input.imeCompositionMode.ToString()
                + " textFieldActive="
                + active.ToString()
                + " keyboardControl="
                + GUIUtility.keyboardControl.ToString()
                + " focusedControl="
                + FocusedControl.ToString()
                + " compositionContext="
                + Composition.ActiveContext.ToString()
                + " preedit="
                + Composition.HasPreedit.ToString()
                + " pending="
                + Composition.PendingCount.ToString()
            )

    static member SuppressCommittedCharacter target id =
        let currentEvent = UnityEngine.Event.current

        if
            not (isNull currentEvent)
            && CommittedCharacters.ShouldSuppress(target, currentEvent.character, Time.frameCount)
        then
            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "KEY duplicate-commit control="
                    + id.ToString()
                    + " char=U+"
                    + (int currentEvent.character).ToString("X4")
                    + " "
                    + FcitxCjkInputMod.DescribeEvent()
                )

            currentEvent.character <- '\000'

    static member SuppressRawHangulKey id =
        let currentEvent = UnityEngine.Event.current
        let letter = FcitxCjkInputMod.IsHangulLetter currentEvent.keyCode
        let backspace = currentEvent.keyCode = KeyCode.Backspace

        if currentEvent.``type`` = EventType.KeyDown && (letter || backspace) then
            let shortcutModifiers = EventModifiers.Control ||| EventModifiers.Command ||| EventModifiers.Alt

            let suppress =
                InputSuppression.shouldSuppress
                    (GUIUtility.keyboardControl = id)
                    (Engine = "hangul")
                    letter
                    backspace
                    Composition.HasPreedit
                    ((currentEvent.modifiers &&& shortcutModifiers) <> EventModifiers.None)

            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.WriteLog(
                    "KEY textfield control="
                    + id.ToString()
                    + " key="
                    + currentEvent.keyCode.ToString()
                    + " char=U+"
                    + (int currentEvent.character).ToString("X4")
                    + " modifiers="
                    + currentEvent.modifiers.ToString()
                    + " engine="
                    + Engine
                    + " preedit="
                    + Composition.HasPreedit.ToString()
                    + " suppress="
                    + suppress.ToString()
                )

            if suppress then
                currentEvent.Use()

    static member IsCameraDolly(binding: KeyBindingDef) =
        binding = KeyBindingDefOf.MapDolly_Left
        || binding = KeyBindingDefOf.MapDolly_Right
        || binding = KeyBindingDefOf.MapDolly_Up
        || binding = KeyBindingDefOf.MapDolly_Down

    static member private DoPatch() =
        let harmony = Harmony("scarf.fcitxcjkinput")
        let rootOnGui = AccessTools.Method(typeof<Root>, "OnGUI")
        let desktopTextField = AccessTools.Method(typeof<GUI>, "HandleTextFieldEventForDesktop")
        let quickSearch = AccessTools.Method(typeof<QuickSearchWidget>, "OnGUI")
        let searchTextSetter = AccessTools.PropertySetter(typeof<QuickSearchFilter>, "Text")
        let keyBindingIsDown = AccessTools.PropertyGetter(typeof<KeyBindingDef>, "IsDown")

        if isNull rootOnGui then
            raise (MissingMethodException("Verse.Root.OnGUI"))

        if isNull desktopTextField then
            raise (MissingMethodException("UnityEngine.GUI.HandleTextFieldEventForDesktop"))

        if isNull quickSearch then
            raise (MissingMethodException("RimWorld.QuickSearchWidget.OnGUI"))

        if isNull searchTextSetter then
            raise (MissingMethodException("RimWorld.QuickSearchFilter.Text.set"))

        if isNull keyBindingIsDown then
            raise (MissingMethodException("Verse.KeyBindingDef.IsDown.get"))

        harmony.Patch(rootOnGui, prefix = HarmonyMethod(typeof<FcitxCjkInputMod>, "BeforeRootOnGui"))
        |> ignore

        harmony.Patch(
            desktopTextField,
            prefix = HarmonyMethod(typeof<FcitxCjkInputMod>, "BeforeDesktopTextField"),
            postfix = HarmonyMethod(typeof<FcitxCjkInputMod>, "AfterDesktopTextField")
        )
        |> ignore

        harmony.Patch(
            quickSearch,
            prefix = HarmonyMethod(typeof<FcitxCjkInputMod>, "BeforeQuickSearch"),
            postfix = HarmonyMethod(typeof<FcitxCjkInputMod>, "AfterQuickSearch")
        )
        |> ignore

        harmony.Patch(searchTextSetter, prefix = HarmonyMethod(typeof<FcitxCjkInputMod>, "BeforeSearchTextSet"))
        |> ignore

        harmony.Patch(keyBindingIsDown, postfix = HarmonyMethod(typeof<FcitxCjkInputMod>, "AfterKeyBindingIsDown"))
        |> ignore

        FcitxCjkInputMod.WriteLog(
            "PATCH Root.OnGUI="
            + rootOnGui.ToString()
            + " textField="
            + desktopTextField.ToString()
            + " quickSearch="
            + quickSearch.ToString()
            + " searchTextSetter="
            + searchTextSetter.ToString()
            + " keyBindingIsDown="
            + keyBindingIsDown.ToString()
        )

    // ---- public Harmony patch methods ----

    static member SetDebugLogging(enabled: bool) =
        FcitxCjkInputMod.SetDebugLogEnabled(enabled)

        if enabled then
            FcitxCjkInputMod.EnsureLog()

        if NativeSetDebug <> null then
            NativeSetDebug.Invoke(
                if enabled then
                    1
                else
                    0
            )

        if enabled then
            FcitxCjkInputMod.WriteRuntimeHeader "DEBUG enabled"
            Log.Message("[CJK] debug log enabled; log=" + LogPath)
        else
            ExpectedFieldTexts.Clear()

            lock LogLock (fun () ->
                if LogWriter <> null then
                    LogWriter.Dispose()
                    LogWriter <- null)

            Log.Message("[CJK] debug log disabled")

    static member BeforeRootOnGui() =
        let mutable dr = NativeDrainRequested
        dr <- 0
        NativeDrainRequested <- dr

        if dr <> 0 then
            FcitxCjkInputMod.DrainNativeMessages()

        let mutable fr = FallbackRestartRequested
        fr <- 0
        FallbackRestartRequested <- fr

        if fr <> 0 then
            FcitxCjkInputMod.StartNativeBridge()

        Composition.DiscardExpired(Stopwatch.GetTimestamp())

        if GUIUtility.keyboardControl = 0 && FocusedControl <> 0 then
            FocusedControl <- 0
            Composition.Blur()

        let active =
            ImeRouting.textFieldIsActive GUIUtility.keyboardControl FocusedControl FocusedTextFieldFrame Time.frameCount

        TextFieldActive <- active
        FcitxCjkInputMod.SetImeCompositionMode active "root"
        let currentEvent = UnityEngine.Event.current

        if not (isNull currentEvent) then
            FcitxCjkInputMod.LogRootEvent currentEvent active

            if currentEvent.``type`` = EventType.KeyDown && currentEvent.keyCode = KeyCode.F11 then
                Overlay <- not Overlay
                FcitxCjkInputMod.WriteLog("UI overlay=" + Overlay.ToString())
                currentEvent.Use()

    static member BeforeDesktopTextField
        (
            position: Rect,
            id: int,
            content: GUIContent,
            multiline: bool,
            maxLength: int,
            style: GUIStyle,
            editor: TextEditor
        ) =
        let target = FcitxCjkInputMod.ObserveEditor id editor
        FcitxCjkInputMod.LogTextField "FIELD before-original" target id content editor

        if target.Id <> 0 then
            FcitxCjkInputMod.SuppressRawHangulKey id

            if FcitxCjkInputMod.DebugLogging() then
                FcitxCjkInputMod.VerifyCommittedText target id content editor

            let actions = Composition.TakeActions(target, Stopwatch.GetTimestamp())

            if actions.Count > 0 then
                let inserted =
                    if FcitxCjkInputMod.DebugLogging() then
                        StringBuilder()
                    else
                        null

                for action in actions do
                    let insertedLength = FcitxCjkInputMod.ApplyAction editor maxLength action inserted
                    CommittedCharacters.Expect(target, action.Text, insertedLength, Time.frameCount)

                content.text <- editor.text

                if FcitxCjkInputMod.DebugLogging() then
                    ExpectedFieldTexts.[target] <- editor.text

                GUI.changed <- true

                if FcitxCjkInputMod.DebugLogging() then
                    FcitxCjkInputMod.WriteLog(
                        "INSERT event="
                        + UnityEngine.Event.current.``type``.ToString()
                        + " control="
                        + id.ToString()
                        + " text=["
                        + (if inserted <> null then
                               FcitxCjkInputMod.Escape(inserted.ToString())
                           else
                               "")
                        + "] result=["
                        + FcitxCjkInputMod.Escape editor.text
                        + "] cursor="
                        + editor.cursorIndex.ToString()
                        + " select="
                        + editor.selectIndex.ToString()
                        + " pending="
                        + Composition.PendingCount.ToString()
                    )

                if GUIUtility.keyboardControl = id then
                    Composition.Focus(target, editor.cursorIndex, editor.selectIndex)

            FcitxCjkInputMod.SuppressCommittedCharacter target id

    static member AfterDesktopTextField
        (
            position: Rect,
            id: int,
            content: GUIContent,
            multiline: bool,
            maxLength: int,
            style: GUIStyle,
            editor: TextEditor
        ) =
        let target = FcitxCjkInputMod.ObserveEditor id editor
        FcitxCjkInputMod.LogTextField "FIELD after-original" target id content editor
        let currentEvent = UnityEngine.Event.current

        if currentEvent.``type`` = EventType.Repaint && GUIUtility.keyboardControl = id then
            match Composition.TryGetView(target) with
            | Some view -> FcitxCjkInputMod.DrawPreedit editor view
            | None -> ()

        if
            currentEvent.``type`` = EventType.Repaint
            && Overlay
            && OverlayFrame <> Time.frameCount
        then
            OverlayFrame <- Time.frameCount
            FcitxCjkInputMod.DrawOverlay()

    static member BeforeQuickSearch(__instance: QuickSearchWidget, __state: byref<string>) =
        __state <- __instance.filter.Text

        if FcitxCjkInputMod.DebugLogging() then
            FcitxCjkInputMod.WriteLog(
                "SEARCH before filter="
                + RuntimeHelpers.GetHashCode(__instance.filter).ToString()
                + " text=["
                + FcitxCjkInputMod.Escape __state
                + "]"
                + " focused="
                + __instance.CurrentlyFocused().ToString()
                + " "
                + FcitxCjkInputMod.DescribeEvent()
            )

    static member AfterQuickSearch(__instance: QuickSearchWidget, __state: string) =
        if FcitxCjkInputMod.DebugLogging() then
            FcitxCjkInputMod.WriteLog(
                "SEARCH after filter="
                + RuntimeHelpers.GetHashCode(__instance.filter).ToString()
                + " before=["
                + FcitxCjkInputMod.Escape __state
                + "] after=["
                + FcitxCjkInputMod.Escape __instance.filter.Text
                + "] focused="
                + __instance.CurrentlyFocused().ToString()
                + " "
                + FcitxCjkInputMod.DescribeEvent()
            )

    static member BeforeSearchTextSet(__instance: QuickSearchFilter, value: string) =
        if FcitxCjkInputMod.DebugLogging() then
            FcitxCjkInputMod.WriteLog(
                "SEARCH set filter="
                + RuntimeHelpers.GetHashCode(__instance).ToString()
                + " old=["
                + FcitxCjkInputMod.Escape __instance.Text
                + "] new=["
                + FcitxCjkInputMod.Escape value
                + "] "
                + FcitxCjkInputMod.DescribeEvent()
            )

    static member AfterKeyBindingIsDown(__instance: KeyBindingDef, __result: byref<bool>) =
        if
            __result
            || TextFieldActive
            || not (FcitxCjkInputMod.IsCameraDolly __instance)
            || Find.WindowStack.AnySearchWidgetFocused
        then
            ()
        else
            let preferences = KeyPrefs.KeyPrefsData

            if not (isNull preferences) then
                let mutable binding = Unchecked.defaultof<KeyBindingData>

                if preferences.keyPrefs.TryGetValue(__instance, &binding) then
                    __result <-
                        GameplayKeyRecovery.shouldRecover
                            __result
                            TextFieldActive
                            true
                            (int binding.keyBindingA)
                            (int binding.keyBindingB)
                            DirectionalKeys
