#include <assert.h>
#include <string.h>

#include "fcitxcjkinput.c"

static DBusMessage *key_message(uint32_t keyval, dbus_bool_t release) {
    DBusMessage *message = dbus_message_new_method_call(
        "org.fcitx.Fcitx5", "/inputcontext_1", INPUT_CONTEXT_INTERFACE,
        "ProcessKeyEvent");
    assert(message);
    uint32_t keycode = 25;
    uint32_t state = 0;
    uint32_t time = 0;
    assert(dbus_message_append_args(message,
        DBUS_TYPE_UINT32, &keyval,
        DBUS_TYPE_UINT32, &keycode,
        DBUS_TYPE_UINT32, &state,
        DBUS_TYPE_BOOLEAN, &release,
        DBUS_TYPE_UINT32, &time,
        DBUS_TYPE_INVALID));
    return message;
}

static void handle_key(uint32_t keyval, dbus_bool_t release,
    unsigned long long sequence) {
    DBusMessage *message = key_message(keyval, release);
    handle_process_key(message, 1, sequence);
    dbus_message_unref(message);
}

static void expect_message(const char *expected) {
    char buffer[256];
    const int length = fcitx_bridge_poll(buffer, sizeof(buffer));
    assert(length > 0);
    assert(strcmp(buffer, expected) == 0);
}

static void expect_no_message(void) {
    char buffer[256];
    assert(fcitx_bridge_poll(buffer, sizeof(buffer)) == 0);
}

int main(void) {
    atomic_store_explicit(&debug_logging, 0, memory_order_relaxed);
    contexts[0].hangul = 1;

    handle_key('w', FALSE, 7);
    expect_message("EVENT:1:7:KEY:119:0");
    handle_key('w', TRUE, 8);
    expect_message("EVENT:1:8:KEY:119:1");

    handle_key('q', FALSE, 9);
    expect_no_message();

    contexts[0].hangul = 0;
    handle_key('w', FALSE, 10);
    expect_no_message();
    return 0;
}
