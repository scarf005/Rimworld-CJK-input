#include <assert.h>
#include <string.h>

#include "fcitxcjkinput.c"

#define CLIENT ":1.42"

static DBusMessage *key_message(uint32_t keyval, dbus_bool_t release,
    uint32_t serial) {
    DBusMessage *message = dbus_message_new_method_call(
        "org.fcitx.Fcitx5", "/inputcontext_1", INPUT_CONTEXT_INTERFACE,
        "ProcessKeyEvent");
    assert(message);
    assert(dbus_message_set_sender(message, CLIENT));
    dbus_message_set_serial(message, serial);
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
    uint32_t serial, unsigned long long sequence) {
    DBusMessage *message = key_message(keyval, release, serial);
    handle_process_key(message, 1, sequence);
    dbus_message_unref(message);
}

static void handle_reply(uint32_t serial, dbus_bool_t accepted,
    unsigned long long sequence) {
    DBusMessage *message = dbus_message_new(DBUS_MESSAGE_TYPE_METHOD_RETURN);
    assert(message);
    assert(dbus_message_set_destination(message, CLIENT));
    dbus_message_set_reply_serial(message, serial);
    assert(dbus_message_append_args(message,
        DBUS_TYPE_BOOLEAN, &accepted,
        DBUS_TYPE_INVALID));
    handle_process_key_reply(message, sequence);
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
    snprintf(contexts[0].destination, sizeof(contexts[0].destination), "%s", CLIENT);
    rimworld_destination_count = 1;
    snprintf(rimworld_destinations[0], sizeof(rimworld_destinations[0]), "%s", CLIENT);

    handle_key('w', FALSE, 7, 7);
    expect_no_message();
    handle_reply(7, TRUE, 8);
    expect_message("EVENT:1:8:KEY:119:0");
    handle_key('w', TRUE, 9, 9);
    expect_message("EVENT:1:9:KEY:119:1");

    handle_key('q', FALSE, 10, 10);
    expect_no_message();
    handle_reply(10, TRUE, 11);
    expect_message("EVENT:1:11:KEY:113:0");

    handle_key('e', FALSE, 12, 12);
    handle_reply(12, FALSE, 13);
    expect_no_message();

    handle_key('z', FALSE, 14, 14);
    handle_key('z', TRUE, 15, 15);
    expect_message("EVENT:1:15:KEY:122:1");
    handle_reply(14, TRUE, 16);
    expect_no_message();

    handle_key('r', FALSE, 17, 17);
    expect_no_message();

    contexts[0].hangul = 0;
    handle_key('a', FALSE, 18, 18);
    expect_no_message();
    return 0;
}
