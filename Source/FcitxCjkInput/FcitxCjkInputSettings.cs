using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    public sealed class FcitxCjkInputSettings : ModSettings {
        public bool DebugLog;

        public override void ExposeData() {
            Scribe_Values.Look(ref DebugLog, "debugLog", false);
        }
    }

    public sealed class FcitxCjkInputEntry : Mod {
        internal static FcitxCjkInputSettings Settings { get; private set; }

        public FcitxCjkInputEntry(ModContentPack content) : base(content) {
            Settings = GetSettings<FcitxCjkInputSettings>();
        }

        public override string SettingsCategory() {
            return "Fcitx CJK Input";
        }

        public override void DoSettingsWindowContents(Rect inRect) {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            var enabled = Settings.DebugLog;
            listing.CheckboxLabeled("Debug log", ref enabled,
                "Write verbose IME diagnostics to /tmp/fcitxcjkinput.log.");
            listing.End();
            if (enabled == Settings.DebugLog)
                return;

            Settings.DebugLog = enabled;
            WriteSettings();
            FcitxCjkInputMod.SetDebugLogging(enabled);
        }
    }
}
