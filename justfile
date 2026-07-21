rimworld := "/media/scarf/@steam/SteamLibrary/steamapps/common/RimWorld"
config := "/home/scarf/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"
steam_os := "RimWorldLinux_Data"
mod_name := "FcitxCjkInput"
package_id := "scarf.fcitxcjkinput"
mod_dest := rimworld + "/Mods/" + mod_name
bridge_src := "helper/fcitx5-ime-bridge.c"
bridge_bin := "helper/fcitx5-ime-bridge"

# Download Harmony
prepare:
    curl -LO https://github.com/pardeike/HarmonyRimWorld/releases/latest/download/HarmonyMod.zip
    7z x -y HarmonyMod.zip -opackages

# Build the C bridge
build-bridge:
    gcc -o {{bridge_bin}} {{bridge_src}} $(pkg-config --cflags --libs dbus-1) -Wall -Wextra -O2
    @echo "Bridge built: {{bridge_bin}}"

# Format C# source
fmt:
    dotnet format ./Source/{{mod_name}}/{{mod_name}}.sln

# Build the C# mod
build-dll:
    STEAM_APPS="{{rimworld}}/../.." \
    STEAM_OS="{{steam_os}}" \
    mise exec dotnet@8.0.422 -- dotnet build ./Source/{{mod_name}}/{{mod_name}}.csproj -c Release

# Build everything
build: build-bridge build-dll

# Install built mod to RimWorld Mods directory
install: build
    mkdir -p "{{mod_dest}}/1.6/Assemblies"
    mkdir -p "{{mod_dest}}/About"
    mkdir -p "{{mod_dest}}/Languages"
    mkdir -p "{{mod_dest}}/helper"
    cp -r 1.6/Assemblies/* "{{mod_dest}}/1.6/Assemblies/"
    cp About/About.xml "{{mod_dest}}/About/"
    cp -r Languages/* "{{mod_dest}}/Languages/"
    cp {{bridge_bin}} "{{mod_dest}}/helper/"
    rm -f /tmp/fcitx5-ime-bridge 2>/dev/null || true
    cp {{bridge_bin}} /tmp/fcitx5-ime-bridge
    chmod +x "{{mod_dest}}/helper/fcitx5-ime-bridge"
    chmod +x /tmp/fcitx5-ime-bridge
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
