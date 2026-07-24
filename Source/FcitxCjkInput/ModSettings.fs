namespace FcitxCjkInput

open UnityEngine
open Verse

type FcitxCjkInputSettings() =
    inherit ModSettings()

    let mutable debugLog = false

    member _.DebugLog
        with get () = debugLog
        and set v = debugLog <- v

    override _.ExposeData() = Scribe_Values.Look(&debugLog, "debugLog", false)

type FcitxCjkInputEntry(content: ModContentPack) =
    inherit Mod(content)

    static let mutable settings = Unchecked.defaultof<FcitxCjkInputSettings>

    do settings <- base.GetSettings<FcitxCjkInputSettings>()

    static member Settings = settings

    override _.SettingsCategory() = "Fcitx CJK Input"

    override this.DoSettingsWindowContents(inRect: Rect) =
        let listing = new Listing_Standard()
        listing.Begin(inRect) |> ignore

        let mutable enabled = settings.DebugLog

        listing.CheckboxLabeled("Debug log", &enabled, "Write verbose IME diagnostics to /tmp/fcitxcjkinput.log.")
        |> ignore

        listing.End() |> ignore

        if enabled <> settings.DebugLog then
            settings.DebugLog <- enabled
            this.WriteSettings()
            FcitxCjkInputMod.SetDebugLogging(enabled)
