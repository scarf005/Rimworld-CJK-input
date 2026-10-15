# 림월드 Fcitx 한글 입력 모드

**Languages:** [中文](./README-ZH.md)

[2026-08-02_21-47-01.webm](https://github.com/user-attachments/assets/a1b7641f-6de8-4f93-a83b-e5b79897f1c6)

> [!CAUTION]
> 1. 바이브 코딩으로 만들어진 모드입니다. 사용에 유의하시기 바랍니다.
> 2. 불완전하게 지원하기 때문에 잔버그가 있습니다. 보통 한영키 변경 시 해결 가능합니다.

리눅스 [fcitx5](https://wiki.archlinux.org/title/Fcitx5) 입력기 환경에서 한글 입력 지원을 추가합니다. fedora 44 + KDE 6.6.4 (wayland) + fcitx5 환경에서 동작을 확인하였습니다.

## 사용 방법

### 1. libdbus 설치

```sh
sudo apt install libdbus-1-3    # Ubuntu
sudo apt install libdbus-1-3    # Debian
sudo dnf install dbus-libs      # Fedora
sudo pacman -S dbus             # Arch
sudo apk add dbus-libs          # Alpine
sudo zypper install libdbus-1-3 # openSUSE
sudo emerge sys-apps/dbus       # Gentoo
```

아래와 같이 나오면 성공입니다:
```sh
ldconfig -p | grep libdbus-1.so.3
        libdbus-1.so.3 (libc6,x86-64) => /lib64/libdbus-1.so.3
        libdbus-1.so.3 (libc6) => /lib/libdbus-1.so.3
```


RimWorld 1.7 베타는 `libxkbcommon`, `libwayland-client`, `fc-match`(fontconfig)도 사용합니다. 대부분의 데스크톱 배포판에는 기본 설치되어 있습니다.

### 2. (1.6만 해당) 스팀 Rimworld 우클릭 > `속성` 좌클릭

![](./assets/properties.webp)

### 3. (1.6만 해당) `실행 옵션`에 다음 값 붙여넣기

![](./assets/settings.webp)

```sh
XMODIFIERS=@im=fcitx SDL_IM_MODULE=fcitx %command%
```

### 4. 설정에서 `리눅스 Fcitx 한글 입력 / Fcitx CJK Input` 모드 활성화

<details>
    <summary>세부 사항</summary>


### 작동 방식 (1.7)

RimWorld 1.7은 GLFW를 쓰며 Wayland 텍스트 입력 프로토콜을 지원하지 않아 fcitx5가 키를 받지 못합니다. 이 모드는 다음 작업을 수행합니다.

1. `1.7/Assemblies/libfcitxcjkinput.so`로 fcitx5 D-Bus 입력 컨텍스트를 직접 만듭니다.
2. 텍스트 필드에 포커스가 있을 때 GLFW 키 입력을 fcitx5에 전달하고, fcitx5가 처리한 키는 게임에 넘기지 않습니다.
3. 커밋은 게임 텍스트 입력 큐에 넣고, 프리에디트는 필드 안에, 후보 목록은 커서 아래에 그립니다.
4. 게임이 찾는 고정 파일명의 CJK 폰트가 없으면 fontconfig가 고른 CJK 폰트를 UI 폰트에 병합합니다.

키맵은 시작할 때 한 번 읽으므로 키보드 레이아웃을 바꾸면 게임을 다시 시작해야 합니다. **Debug log**는 fcitx5 이벤트를 게임 로그에 기록합니다.

### 작동 방식 (1.6)

Unity 2022는 SDL2 fcitx5 입력 컨텍스트를 생성하지만 Linux IME 커밋을 IMGUI 텍스트 필드에 연결하지 않습니다. 이 모드는 다음 작업을 수행합니다.

1. RimWorld 프로세스에 `1.6/Assemblies/libfcitxcjkinput.so`를 로드합니다.
2. RimWorld의 SDL2 입력 컨텍스트에 대한 fcitx5 D-Bus 호출, 응답 및 시그널을 관찰합니다.
3. Unity 메인 스레드의 IMGUI 처리 과정에서 네이티브 이벤트 큐를 비웁니다.
4. 각 입력 컨텍스트, 이벤트 시퀀스, 대상 컨트롤 및 조합 앵커를 추적합니다.
5. 커서 이동을 유지하면서 해당 앵커에 텍스트를 커밋합니다.
6. 저장된 필드 값을 변경하지 않고 프리에디트 텍스트와 커서를 그립니다.

물리 IME 전환 키는 계속 KWin/fcitx5가 처리합니다. 이 모드는 `/dev/input`을 사용하거나, fcitx5 상태를 폴링하거나, 임시 실행 파일을 생성하거나, 별도의 헬퍼 프로세스를 시작하지 않습니다.

**옵션 → 모드 설정 → Fcitx CJK Input**에서 **Debug log**를 활성화하여 진단 오버레이를 표시하고 자세한 진단 로그를 `/tmp/fcitxcjkinput.log`에 기록할 수 있습니다.

### 빌드 및 설치

```sh
just fmt
just test
just build
just install
```

`just build`는 다음 파일을 생성합니다. 1.6 빌드는 `rimworld-1.6` 브랜치, `just prepare`로 받은 Harmony, `RIMWORLD_1_6_DIR`의 1.6 설치본을 사용합니다.

```text
1.6/Assemblies/FcitxCjkInput.dll
1.6/Assemblies/libfcitxcjkinput.so
1.7/Assemblies/FcitxCjkInput.dll
1.7/Assemblies/libfcitxcjkinput.so
```

## 라이선스

[AGPL-3.0](./LICENSE)

</details>

<details>
    <summary>english</summary>

# Fcitx CJK Input

> [!CAUTION]
> 1. vibe-coded mod. use at your own risk
> 2. partially fully supports CJK input; small bug here and there can be fixed by switching between korean <-> english

Linux fcitx5 input support for RimWorld 1.6 and 1.7 beta text fields.

## Requirements

- Native Linux RimWorld 1.6 or 1.7 beta
- [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
- fcitx5
- `libdbus-1.so.3`
- 1.7: `libxkbcommon`, `libwayland-client`, `fc-match` (fontconfig)

RimWorld 1.6 only: set this Steam launch option before starting RimWorld:
![](./assets/settings.webp)
```text
XMODIFIERS=@im=fcitx SDL_IM_MODULE=fcitx %command%
```

## How it works (1.7)

RimWorld 1.7 uses GLFW, which lacks the Wayland text-input protocol, so fcitx5 never receives keys. This mod:

1. creates its own fcitx5 D-Bus input context through `1.7/Assemblies/libfcitxcjkinput.so`
2. forwards GLFW key events to fcitx5 while a text field is focused and drops the keys fcitx5 consumes
3. queues commits as game text input, draws preedit inside the field and candidates below the caret
4. merges the CJK fonts fontconfig picks into the UI fonts when the game's fixed font paths have none

The keymap is read once at startup; restart the game after changing keyboard layouts. **Debug log** writes fcitx5 events to the game log.

## How it works (1.6)

Unity 2022 creates an SDL2 fcitx5 input context but does not connect its Linux IME commits to IMGUI text fields. This mod:

1. loads `1.6/Assemblies/libfcitxcjkinput.so` into the RimWorld process
2. observes fcitx5 D-Bus calls, replies, and signals for RimWorld's SDL2 input contexts
3. drains the native event queue from Unity's main-thread IMGUI pass
4. tracks each input context, event sequence, target control, and composition anchor
5. commits text at that anchor while preserving cursor navigation
6. draws preedit text and its cursor without changing the saved field value

The physical IME toggle key remains handled by KWin/fcitx5. The mod does not use `/dev/input`, poll fcitx5 state, create a temporary executable, or start a separate helper process.

enabling **Debug log** under **Options → Mod settings → Fcitx CJK Input** will enable diagnostic overlay and write verbose diagnostics to `/tmp/fcitxcjkinput.log`.

## Build and install

```sh
just fmt
just test
just build
just install
```

`just build` produces the files below. The 1.6 build uses the `rimworld-1.6` branch, Harmony from `just prepare`, and the 1.6 install in `RIMWORLD_1_6_DIR`.

```text
1.6/Assemblies/FcitxCjkInput.dll
1.6/Assemblies/libfcitxcjkinput.so
1.7/Assemblies/FcitxCjkInput.dll
1.7/Assemblies/libfcitxcjkinput.so
```

## License

[AGPL-3.0](./LICENSE)

</details>
