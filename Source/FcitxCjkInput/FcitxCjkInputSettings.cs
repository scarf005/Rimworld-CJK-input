using UnityEngine;
using Verse;

namespace FcitxCjkInput;

public sealed class FcitxCjkInputSettings : ModSettings {
    public bool DebugLog;

    public override void ExposeData() => Scribe_Values.Look(ref DebugLog, "debugLog", false);
}

public sealed class FcitxCjkInputEntry : Mod {
    internal static FcitxCjkInputSettings? Settings { get; private set; }

    public FcitxCjkInputEntry(ModContentPack content) : base(content) {
        Settings = GetSettings<FcitxCjkInputSettings>();
        FcitxCjkInputMod.Start(content);
    }

    public override string SettingsCategory() => "Fcitx CJK Input";

    public override void DoSettingsWindowContents(Rect inRect) {
        var listing = new Listing_Standard();
        listing.Begin(inRect);
        listing.CheckboxLabeled("Debug log", ref Settings!.DebugLog, "Log fcitx5 events to the RimWorld log.");
        listing.End();
    }
}
