# Research request: correct fcitx5 gameplay shortcut recovery in RimWorld

## Objective

Find a general, maintainable architecture for recovering RimWorld gameplay shortcuts while the fcitx5 Hangul engine is active without leaving shortcut keystrokes in the IME composition

The solution must follow the active RimWorld key bindings and keyboard layout rather than special-casing physical `Z`, Hangul `ㅋ`, or one UI action

Please research the relevant SDL2, fcitx5, D-Bus, Unity Linux, and RimWorld behavior from authoritative source code and documentation, then recommend a concrete implementation

## Project and environment

- repository: https://github.com/scarf005/Rimworld-Fcitx-Mod
- local source snapshot: `144d4207474bcb5c16185e0e7eaf3426a7f6eb16`
- the latest local commits are not necessarily present on the remote repository
- RimWorld 1.6 for native Linux
- Unity `2022.3.35f1`
- Unity IMGUI text fields
- fcitx5 with the Hangul engine
- launch environment: `XMODIFIERS=@im=fcitx SDL_IM_MODULE=fcitx`
- native bridge: C with libdbus and pthreads
- managed mod: C# with Harmony patches
- Unity embeds SDL2 in `UnityPlayer.so`
- `UnityPlayer.so` has no dynamic exports named `SDL_Fcitx_Reset`, `SDL_DBus_GetContext`, `SDL_StartTextInput`, or `SDL_StopTextInput` according to `nm -D`

Relevant files:

- `native/fcitxcjkinput.c`
- `Source/FcitxCjkInput/FcitxCjkInput.cs`
- `Source/FcitxCjkInput/InputRecovery.cs`
- `Source/FcitxCjkInput/CompositionStateMachine.cs`
- `Source/FcitxCjkInput.Tests/Program.cs`

## Why the bridge exists

Unity creates and uses an SDL2 fcitx5 input context, but this RimWorld version does not deliver Linux IME commit text correctly to IMGUI `TextEditor` instances

The mod observes the existing SDL/fcitx D-Bus traffic and inserts `CommitString` text into the focused IMGUI editor while rendering `UpdateFormattedPreedit` separately

It intentionally avoids a second helper process, `/dev/input`, and native-to-managed callbacks

## Current native implementation

The native bridge opens its own private session-bus connection, discovers D-Bus unique names belonging to the RimWorld PID, and calls `org.freedesktop.DBus.Monitoring.BecomeMonitor`

It observes the following traffic for SDL-created `org.fcitx.Fcitx.InputContext1` objects:

- `FocusIn` method calls
- `ProcessKeyEvent` method calls
- replies to `ProcessKeyEvent`
- `CurrentIM` signals
- `UpdateFormattedPreedit` signals
- `CommitString` signals
- `NotifyFocusOut` signals

Contexts are identified by the SDL client unique bus name plus input-context object path

For key recovery, the bridge currently:

1. observes a `ProcessKeyEvent` call
2. stores the sender, D-Bus serial, context, `keyval`, `keycode`, modifiers, release flag, and native monotonic timestamp
3. correlates the method return by destination and reply serial
4. emits a managed `KEY` press only when fcitx replies `accepted=true`
5. treats release as immediate invalidation and cancels a pending press with the same physical `keycode`
6. clears pending input on focus or engine changes

The current native key filter is hardcoded:

```c
static int is_recoverable_key(uint32_t keyval) {
    return keyval == 'a' || keyval == 'A' || keyval == 'd' || keyval == 'D' ||
        keyval == 's' || keyval == 'S' || keyval == 'w' || keyval == 'W' ||
        keyval == 'q' || keyval == 'Q' || keyval == 'e' || keyval == 'E' ||
        keyval == 'z' || keyval == 'Z';
}
```

The native-to-managed queue is bounded to 512 messages and emits a destructive global `RESET` if a non-debug message is lost

## Current managed implementation

Managed messages are polled from the Unity main thread during `Root.OnGUI`, with at most 32 messages drained per frame

The managed code tracks:

- active fcitx engine per context
- focused IMGUI control and generation
- preedit and commit actions per context
- short-lived recovered gameplay key state
- expected Unity character events after an injected commit

Current gameplay recovery patches:

- `KeyBindingDef.IsDown` for camera movement
- `KeyBindingDef.KeyDownEvent` for designator rotation
- `PlaySettings.DoPlaySettingsGlobalControls` for map/world search because vanilla search additionally requires an IMGUI `KeyDown` event that fcitx consumed

`TryRecoverKeyBinding` reads the actual primary and secondary keys from `KeyPrefs.KeyPrefsData`, but `GameplayKeyState` only represents hardcoded `W/A/S/D/Q/E/Z` values and loses the native context and event identity when a press is consumed

Presses for `Q`, `E`, and `Z` expire after 250 ms and are consumed once; directional states are cleared on release and reset paths

## Reproduced composition leak

With Hangul active and no text field focused:

1. the player presses the default map-search shortcut `Z`
2. SDL sends `ProcessKeyEvent(keyval=122, keycode=52)` to fcitx
3. fcitx accepts the key and changes its composition to `ㅋ`
4. the mod drops that preedit because no text field is active
5. the accepted key is recovered as the RimWorld search shortcut
6. RimWorld opens and focuses the search field
7. the player presses the key that produces `ㄹ`
8. fcitx first commits the stale `ㅋ`, then starts `ㄹ` as the next preedit
9. without suppression, the search field receives `ㅋㄹ` instead of `ㄹ`

Observed log excerpt:

```text
ProcessKeyEvent serial=41 context=1 keyval=122 keycode=52 release=0 hangul=1 recover=1
Preedit context=1 cursor=3 bytes=3 text=ㅋ
DROP preedit context=1 sequence=1320 reason=inactive-or-unbound
ProcessKeyReply serial=41 context=1 keyval=122 accepted=1 recover=1 canceled=0
RECOVER binding=OpenMapSearch
STATE focus control=331
ProcessKeyEvent serial=45 context=1 keyval=102 keycode=41 release=0 hangul=1 recover=0
CommitString context=1 bytes=3 text=ㅋ
Preedit context=1 cursor=3 bytes=3 text=ㄹ
```

The important invariant is that dropping preedit from the game UI does not clear the composition inside fcitx

## Incorrect local workaround that must be replaced

The current local code records the last accepted physical `Z`, remembers a dropped preedit exactly equal to `ㅋ`, and arms a one-shot commit guard only after recovering `OpenMapSearch`

Equivalent logic:

```csharp
if (!release && (keyValue == 'z' || keyValue == 'Z'))
    _lastZContext = contextId;

if (_lastZContext != 0 && _unboundPreeditContext == _lastZContext &&
    _unboundPreedit == "ㅋ")
    ShortcutCommits.Arm(_lastZContext, _unboundPreedit);
```

A later commit is discarded only if both context and text match

This happened to suppress the reproduced `ㅋ` commit, but it is not an acceptable design

### Why it is wrong

- it hardcodes the default physical `Z`
- it hardcodes the Dubeolsik result `ㅋ`
- it hardcodes `OpenMapSearch`
- changing the search binding can immediately bypass the guard
- another keyboard layout or Hangul layout can produce different text
- fcitx may merge the stale shortcut composition with later intended input rather than committing the stale text separately
- `Q`, `E`, camera keys, and future recovered shortcuts can leave their own composition behind
- another gameplay shortcut that opens or closes UI can reproduce the same state leak
- the native filter and managed state disagree with RimWorld's configurable key-binding model
- a string-equality commit filter cannot prove which portion of a composed Hangul commit came from the gameplay shortcut
- the unit test only verifies the one-shot string guard, not the required end-to-end invariant

Do not recommend expanding this workaround with more key-to-jamo tables, more UI-specific patches, or more expected-string cases

## Required behavior contract

A correct solution must satisfy all of the following:

1. use the player's current primary and secondary RimWorld bindings rather than fixed defaults
2. support arbitrary keyboard and Hangul layouts without mapping physical keys to expected Hangul strings
3. recover accepted gameplay presses only when the matching fcitx `ProcessKeyEvent` reply says `accepted=true`
4. invalidate unresolved presses on release, focus loss, engine change, queue loss, and shutdown
5. ensure that any fcitx composition caused by a key consumed as gameplay cannot later enter a text field
6. preserve the player's next intentional IME input in full, including cases where fcitx would normally combine it with an existing preedit
7. handle movement, rotation, search, and any other gameplay or UI binding through the same general mechanism
8. preserve normal text entry when a text field is already active
9. avoid delayed shortcut activation and stale or stuck held-key state
10. remain safe during Unity/Mono shutdown, with no worker-to-managed callback
11. keep queues and per-frame work bounded
12. recover cleanly after bridge restart or message loss
13. avoid process-wide focus changes, synthetic global keyboard input, or behavior that affects other applications

The design should explicitly define what happens when a shortcut press, preedit update, commit, focus transition, and key release are observed in different orders

## Central technical obstacle

The input context belongs to SDL's private D-Bus connection

The bridge's monitor connection has a different D-Bus unique sender and only observes copied messages. Prior investigation indicates that calling `org.fcitx.Fcitx.InputContext1.Reset` from a different sender is rejected because fcitx checks ownership of the input context

SDL itself has an internal reset path such as `SDL_Fcitx_Reset`, which calls the input context's `Reset`, but Unity's embedded SDL symbols are not dynamically exported

Please verify these claims against the exact relevant fcitx5 and SDL2 source and explain their version sensitivity

A solution that merely observes and deletes a later `CommitString` may be impossible to make lossless because Hangul composition can combine the shortcut-derived state with subsequent intended input

## Research questions

### 1. Reset through the owning SDL connection

Determine whether an in-process Unity native plugin can safely reach or invoke the embedded SDL fcitx reset path despite hidden symbols

Investigate at least:

- whether Unity exposes any supported native or managed IME reset API that reaches SDL's input context
- whether toggling `Input.imeCompositionMode`, `SDL_StopTextInput`, or `SDL_StartTextInput` resets fcitx composition in the relevant SDL2 implementation
- whether `SDL_DBus_GetContext` or the SDL fcitx state can be obtained safely without relying on unstable private structure offsets
- whether symbol lookup, ELF symbol tables, build IDs, relocation inspection, or another supported integration point is viable when symbols are not in `.dynsym`
- thread-affinity requirements for the SDL/fcitx reset call
- shutdown and unload safety

Do not recommend hardcoded function addresses or binary-pattern scanning unless there is a defensible compatibility and validation strategy

### 2. Own or intercept the input context

Evaluate whether the bridge should own the fcitx input context or intercept input before SDL mutates its context

For each viable design, explain:

- how physical key events reach the bridge
- how the current engine, focus, capabilities, surrounding text, cursor rectangle, and client lifecycle are synchronized
- how duplicate processing by SDL and the new context is prevented
- how commits and preedit are delivered exactly once
- how gameplay shortcut consumption resets or bypasses composition
- whether this remains compatible with KWin/fcitx handling of the physical Hangul toggle key
- whether it requires a preload library, launcher change, helper process, or unsupported Unity hook

### 3. Generic binding-aware recovery

Design a replacement for hardcoded `W/A/S/D/Q/E/Z` state

The answer should cover:

- how native `keyval` and `keycode` should be represented and matched to Unity `KeyCode` and RimWorld `KeyBindingData`
- keyboard-layout and modifier handling
- primary and secondary bindings
- held actions versus one-shot actions
- repeated key presses
- bindings changed while the game is running
- how consuming a recovered press retains its context, key identity, sequence, and composition generation
- whether all `KeyBindingDef` queries can be supported centrally without recovering unrelated text input

### 4. If direct reset is impossible

Determine whether any state-machine or replay algorithm based only on observed `ProcessKeyEvent`, preedit, and commit traffic can guarantee both of these properties:

- all output caused by the consumed gameplay key is removed
- all output caused by subsequent intentional text input is preserved

Provide either a concrete algorithm with proof across Hangul composition cases or a counterexample showing why observation-only filtering cannot be lossless

Do not treat exact commit-string matching as sufficient

### 5. Validation plan

Provide a test matrix that includes:

- search bound to its default key and several remapped letter/non-letter keys
- primary and secondary bindings
- Dubeolsik and at least one different layout assumption
- movement and rotation keys followed by opening a text field
- shortcuts that open, close, or switch UI
- repeated presses and held keys
- press/release before the asynchronous fcitx reply
- negative and late replies
- focus and engine transitions
- queue overflow and bridge restart
- normal Hangul composition with no gameplay shortcut
- process shutdown and repeated start/stop/unload

Specify which tests can be deterministic native or managed unit tests and which require a real Unity/fcitx integration test

## Known references

- Unity Linux IME preview discussion: https://discussions.unity.com/t/ime-input-support-now-available-for-linux-preview/1711291
- SDL fcitx5 implementation history: https://github.com/libsdl-org/SDL/pull/4246
- project repository: https://github.com/scarf005/Rimworld-Fcitx-Mod

## Requested deliverable

Return:

1. a source-backed explanation of the SDL/fcitx ownership and reset constraints with direct permalinks
2. at least two technically viable architectures, including limitations and lifecycle risks
3. one recommended architecture and why it satisfies the complete behavior contract
4. a concrete event protocol and state machine for native and managed components
5. the minimum repository changes by file and responsibility
6. a deterministic validation strategy
7. any critical unknowns that require a small runtime probe before implementation

Prioritize correctness and maintainability over patch size or implementation speed
