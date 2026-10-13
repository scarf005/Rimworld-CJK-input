rimworld := "/media/scarf/@steam/SteamLibrary/steamapps/common/RimWorld"
rimworld_16 := env("RIMWORLD_1_6_DIR", "/media/scarf/@steam/RimWorld-1.6.4871-backup")
config := "/home/scarf/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"
mod_name := "FcitxCjkInput"
package_id := "scarf.cjkinput"
mod_dest := rimworld + "/Mods/" + mod_name
dotnet := "mise exec dotnet@10.0.301 -- dotnet"
native_libs := "$(pkg-config --cflags --libs dbus-1 xkbcommon wayland-client)"
legacy_ref := env("RIMWORLD_1_6_REF", "rimworld-1.6")
legacy_dir := "Source/obj/rimworld-1.6"

# Download Harmony for the RimWorld 1.6 build
prepare:
    curl -LO https://github.com/pardeike/HarmonyRimWorld/releases/latest/download/HarmonyMod.zip
    7z x -y HarmonyMod.zip -opackages

# Build the RimWorld 1.7 fcitx5 client
build-native:
    mkdir -p 1.7/Assemblies
    gcc -shared -fPIC -fvisibility=hidden -Wl,-soname,libfcitxcjkinput.so \
        -o 1.7/Assemblies/libfcitxcjkinput.so native/fcitxcjkinput.c {{native_libs}} -Wall -Wextra -Werror -O2

# Format C# source
fmt:
    {{dotnet}} format ./Source/{{mod_name}}/{{mod_name}}.csproj
    {{dotnet}} format ./Source/{{mod_name}}.Tests/{{mod_name}}.Tests.csproj

# Test Hangul composition against the running fcitx5 session
test-native:
    mkdir -p Source/obj
    gcc -o Source/obj/fcitxcjkinput_test native/fcitxcjkinput_test.c {{native_libs}} -Wall -Wextra -Werror
    Source/obj/fcitxcjkinput_test

# Build the RimWorld 1.7 assembly
build-dll:
    RIMWORLD_DIR="{{rimworld}}" {{dotnet}} build ./Source/{{mod_name}}/{{mod_name}}.csproj -c Release

# Build the RimWorld 1.6 assembly and bridge from the frozen 1.6 branch (needs `just prepare`)
build-16:
    mkdir -p {{legacy_dir}}/steamapps/common 1.6/Assemblies
    git archive {{legacy_ref}} Source native | tar -x -C {{legacy_dir}}
    ln -sfn "{{rimworld_16}}" {{legacy_dir}}/steamapps/common/RimWorld
    ln -sfn "$PWD/packages" {{legacy_dir}}/packages
    STEAM_APPS="$PWD/{{legacy_dir}}/steamapps" STEAM_OS=RimWorldLinux_Data \
        {{dotnet}} build {{legacy_dir}}/Source/{{mod_name}}/{{mod_name}}.csproj -c Release -p:OutputPath="$PWD/1.6/Assemblies/"
    gcc -shared -fPIC -fvisibility=hidden -pthread -Wl,-soname,libfcitxcjkinput.so \
        -o 1.6/Assemblies/libfcitxcjkinput.so {{legacy_dir}}/native/fcitxcjkinput.c \
        $(pkg-config --cflags --libs dbus-1) -Wall -Wextra -O2

# Run event parsing tests
test:
    {{dotnet}} run --project Source/{{mod_name}}.Tests/{{mod_name}}.Tests.csproj -c Release

# Build everything
build: test test-native build-native build-dll build-16

# Install built mod to RimWorld Mods directory
install: build
    @pgrep -x RimWorld >/dev/null 2>&1 && { echo "RimWorld is running — 설치 전에 종료해주세요"; exit 1; } || true
    mkdir -p "{{mod_dest}}/1.6/Assemblies" "{{mod_dest}}/1.7/Assemblies" "{{mod_dest}}/About"
    cp -r 1.6/Assemblies/* "{{mod_dest}}/1.6/Assemblies/"
    cp -r 1.7/Assemblies/* "{{mod_dest}}/1.7/Assemblies/"
    cp -r About/* "{{mod_dest}}/About/"
    gio trash -f "{{mod_dest}}/helper" "{{mod_dest}}/Languages"
    @echo "Installed to {{mod_dest}}"

# Install and enable in ModsConfig.xml
enable: install
    @if [ -f "{{config}}" ]; then \
        if ! rg -q "{{package_id}}" "{{config}}"; then \
            sed -i 's|</activeMods>|  <li>{{package_id}}</li>\n</activeMods>|' "{{config}}"; \
            echo "Added {{package_id}} to activeMods"; \
        else \
            echo "{{package_id}} already in activeMods"; \
        fi; \
    else \
        echo "ModsConfig.xml not found at {{config}}"; \
    fi
