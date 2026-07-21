#!/bin/bash
# fcitx5 IME bridge - communicates with fcitx5's IBus D-Bus interface
# Protocol:
#   Input (stdin lines):
#     IC:CREATE              - create input context
#     IC:DESTROY             - destroy input context
#     KEY:KEYVAL:KEYCODE:STATE     - process key event (state includes release bit 1<<30)
#     FOCUS:IN               - focus in
#     FOCUS:OUT              - focus out
#     CURSOR:X:Y:W:H         - set cursor location
#     RESET                  - reset IME state
#     PING                   - check if helper is alive
#   Output (stdout lines):
#     OK:MSG                 - generic success
#     ERROR:MSG              - error
#     COMMIT:TEXT            - committed text (may contain colons, so read full line)
#     PREEDIT:TEXT:CURSOR    - preedit text with cursor position
#     HIDE                   - hide preedit
#     CONSUMED:BOOL          - whether key was consumed by IME
#     PONG                   - response to PING

set -euo pipefail

BUS_NAME="org.fcitx.Fcitx5"
IBUS_PATH="/org/freedesktop/IBus"
CONTROLLER_PATH="/controller"
CONTROLLER_IFACE="org.fcitx.Fcitx.Controller1"

log() {
    echo "LOG:$*" >&2
}

log "fcitx5-ime-bridge starting (PID=$$)"

# Create input context
IC_PATH=""

create_ic() {
    log "Creating input context..."
    local result
    result=$(busctl --user call "$BUS_NAME" "$IBUS_PATH" org.freedesktop.IBus CreateInputContext s "fcitx-rw-$$" 2>&1) || {
        log "Failed to create input context: $result"
        echo "ERROR:CreateInputContext failed: $result"
        return 1
    }
    # Extract object path from result like: o "/org/freedesktop/IBus/InputContext_0"
    IC_PATH=$(echo "$result" | grep -oP '"/[^"]*"' | tr -d '"')
    if [ -z "$IC_PATH" ]; then
        log "Failed to parse IC path from: $result"
        echo "ERROR:Failed to parse IC path"
        return 1
    fi
    log "Created IC at: $IC_PATH"
    echo "OK:IC created at $IC_PATH"

    # Focus in
    busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext FocusIn 2>&1 >/dev/null || true
    log "FocusIn sent"
}

destroy_ic() {
    if [ -n "$IC_PATH" ]; then
        log "Destroying IC: $IC_PATH"
        busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext Destroy 2>&1 >/dev/null || true
        IC_PATH=""
        echo "OK:IC destroyed"
    fi
}

process_key() {
    local keyval="$1"
    local keycode="$2"
    local state="$3"

    if [ -z "$IC_PATH" ]; then
        echo "CONSUMED:false"
        return
    fi

    log "ProcessKey: keyval=$keyval keycode=$keycode state=$state"
    local result
    result=$(busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext ProcessKeyEvent uuu "${keyval}" "${keycode}" "${state}" 2>&1) || {
        log "ProcessKeyEvent failed: $result"
        echo "ERROR:ProcessKeyEvent failed"
        echo "CONSUMED:false"
        return
    }
    log "ProcessKeyEvent result: $result"

    # Check if key was consumed (boolean result)
    if echo "$result" | grep -q "true"; then
        echo "CONSUMED:true"
    else
        echo "CONSUMED:false"
    fi
}

set_cursor() {
    local x="$1" y="$2" w="$3" h="$4"
    if [ -n "$IC_PATH" ]; then
        busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext SetCursorLocation iiii "$x" "$y" "$w" "$h" 2>&1 >/dev/null || true
    fi
    echo "OK:cursor set"
}

focus_in() {
    if [ -n "$IC_PATH" ]; then
        busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext FocusIn 2>&1 >/dev/null || true
    fi
    echo "OK:focus in"
}

focus_out() {
    if [ -n "$IC_PATH" ]; then
        busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext FocusOut 2>&1 >/dev/null || true
    fi
    echo "OK:focus out"
}

reset_ime() {
    if [ -n "$IC_PATH" ]; then
        busctl --user call "$BUS_NAME" "$IC_PATH" org.freedesktop.IBus.InputContext Reset 2>&1 >/dev/null || true
    fi
    echo "OK:reset"
}

# Also listen for fcitx5 signals (CommitText, UpdatePreeditText) in background
listen_signals() {
    log "Starting signal listener..."
    # We need to poll for signals since bash can't easily listen to D-Bus signals
    # Alternative: use dbus-monitor
    dbus-monitor --session "type='signal',sender='$BUS_NAME',path='$IC_PATH'" 2>/dev/null | while read -r line; do
        log "SIGNAL RAW: $line"
        # Try to parse commit text and preedit from signal output
        if echo "$line" | grep -q "string"; then
            # Extract quoted strings
            local quoted
            quoted=$(echo "$line" | grep -oP '"(?:[^"\\]|\\.)*"' || true)
            log "SIGNAL PARSED: $quoted"
        fi
    done &
    SIGNAL_PID=$!
    log "Signal listener PID: $SIGNAL_PID"
}

# Main loop
log "Entering main loop"
create_ic

# Start signal listener in background
listen_signals

while IFS= read -r line; do
    log "RECV: $line"

    cmd="${line%%:*}"
    rest="${line#*:}"

    case "$cmd" in
        IC:CREATE)
            create_ic
            ;;
        IC:DESTROY)
            destroy_ic
            ;;
        KEY)
            # KEY:KEYVAL:KEYCODE:STATE
            keyval="${rest%%:*}"
            tmp="${rest#*:}"
            keycode="${tmp%%:*}"
            state="${tmp#*:}"
            process_key "$keyval" "$keycode" "$state"
            ;;
        FOCUS)
            case "$rest" in
                IN)  focus_in ;;
                OUT) focus_out ;;
                *)   echo "ERROR:Unknown focus command: $rest" ;;
            esac
            ;;
        CURSOR)
            # CURSOR:X:Y:W:H
            cx="${rest%%:*}"
            tmp="${rest#*:}"
            cy="${tmp%%:*}"
            tmp="${tmp#*:}"
            cw="${tmp%%:*}"
            ch="${tmp#*:}"
            set_cursor "$cx" "$cy" "$cw" "$ch"
            ;;
        RESET)
            reset_ime
            ;;
        PING)
            echo "PONG"
            ;;
        EXIT)
            log "Exit requested"
            break
            ;;
        "")
            # empty line, ignore
            ;;
        *)
            echo "ERROR:Unknown command: $cmd"
            ;;
    esac
done

log "Shutting down..."
destroy_ic
[ -n "${SIGNAL_PID:-}" ] && kill "$SIGNAL_PID" 2>/dev/null || true
log "Bridge exited"
