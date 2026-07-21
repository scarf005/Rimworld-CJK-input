# Postmortem

RimWorld 1.6 uses Unity 2022.3 IMGUI on Linux. The player receives fcitx5 SDL D-Bus signals but does not connect committed IME text to `TextEditor`.

Failed approaches:

- Forwarding IMGUI keys to a separate IBus context split focus and Hangul state from Unity's SDL context; asynchronous consume/commit ordering produced raw-key leaks and duplicate or reordered text.
- Polling `CurrentInputMethod`, kimpanel, and controller state did not reliably identify the focused context or physical 한/영 key transition.
- Patching `Event.current`, `Input.inputString`, or `GUI.DoTextField` alone could not supply ordered fcitx `CommitString` messages; one attempted `DoTextField` return-value patch targeted a `void` method.
- Unity cannot receive the compositor-consumed physical 한/영 key directly. The working path observes the fcitx signals already sent to Unity's SDL context, suppresses its raw Hangul keystrokes, and inserts only commits into the active IMGUI editor.

References:

- https://steamcommunity.com/sharedfiles/filedetails/?id=3746764792
- https://discussions.unity.com/t/ime-input-support-now-available-for-linux-preview/1711291
- https://github.com/libsdl-org/SDL/pull/4246
