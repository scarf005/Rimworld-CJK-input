# Fcitx CJK Input

Linux fcitx5 input support for RimWorld 1.6 IMGUI text fields.

## Requirements

- Native Linux RimWorld 1.6
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- fcitx5
- `libdbus-1.so.3`

Set this Steam launch option before starting RimWorld:

```text
XMODIFIERS=@im=fcitx SDL_IM_MODULE=fcitx %command%
```

## How it works

Unity 2022 creates an SDL2 fcitx5 input context but does not connect its Linux IME commits to IMGUI text fields. This mod:

1. Loads `1.6/Assemblies/libfcitxcjkinput.so` into the RimWorld process.
2. Observes fcitx5 D-Bus signals addressed to RimWorld's SDL2 input context.
3. Uses Harmony to suppress raw Latin key events while the Hangul engine is active.
4. Inserts committed text into the focused `TextEditor`.
5. Draws preedit text and its cursor without changing the saved field value.

The physical IME toggle key remains handled by KWin/fcitx5. The mod does not use `/dev/input`, poll fcitx5 state, create a temporary executable, or start a separate helper process.

Press **F11** to toggle the diagnostic overlay. Detailed diagnostics are written to `/tmp/fcitxcjkinput.log`; the current diagnostic log includes preedit and committed text.

## Build and install

```sh
just fmt
just build
just install
```

`just build` produces:

```text
1.6/Assemblies/FcitxCjkInput.dll
1.6/Assemblies/libfcitxcjkinput.so
```

The native library is dynamically linked to the system `libdbus-1.so.3`.

## License

AGPL-3.0. Distributions containing the DLL or native library must provide the corresponding source and build scripts for the same version.
