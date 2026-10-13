/*
 * Native fcitx5 input-context client loaded in-process by FcitxCjkInput.dll.
 *
 * RimWorld 1.7 runs on GLFW without a Wayland text-input protocol, so fcitx5
 * never sees its keys. This bridge owns an fcitx5 D-Bus input context, turns
 * GLFW scancodes into keysyms with the compositor's XKB keymap, and forwards
 * key events while a text field is focused.
 *
 * Public API:
 *   int cjk_start(void)
 *   void cjk_stop(void)
 *   void cjk_focus(int focused)
 *   void cjk_reset(void)
 *   void cjk_reset_keys(void)
 *   int cjk_key(uint32_t scancode, int action, uint32_t time, int forward)
 *   int cjk_poll(char *buffer, int capacity)
 *
 * Queue messages, fields separated by U+001F:
 *   commit <text>
 *   preedit <UTF-8 byte cursor> <text>
 *   forward <UTF-32 codepoint> <keysym> <release 0|1>
 *   panel <candidate index> <header> <candidate>...
 *   focusout
 */

#include <dbus/dbus.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <unistd.h>
#include <wayland-client.h>
#include <xkbcommon/xkbcommon.h>

#define EXPORT __attribute__((visibility("default")))
#define FCITX_SERVICE "org.fcitx.Fcitx5"
#define INPUT_METHOD_PATH "/org/freedesktop/portal/inputmethod"
#define INPUT_METHOD_INTERFACE "org.fcitx.Fcitx.InputMethod1"
#define INPUT_CONTEXT_INTERFACE "org.fcitx.Fcitx.InputContext1"
#define SEPARATOR "\x1f"
#define CALL_TIMEOUT_MS 200
#define MAX_TEXT 4096
#define MAX_MESSAGE 16384
#define MAX_QUEUE_MESSAGES 256

/* fcitx-utils/capabilityflags.h */
#define CAPABILITY_PREEDIT (1ULL << 1)
#define CAPABILITY_FORMATTED_PREEDIT (1ULL << 4)
#define CAPABILITY_KEY_EVENT_ORDER_FIX (1ULL << 37)
#define CAPABILITY_CLIENT_SIDE_INPUT_PANEL (1ULL << 39)

/* dbusfrontend.cpp batched event kinds */
enum { BATCHED_COMMIT_STRING, BATCHED_PREEDIT, BATCHED_FORWARD_KEY };

static DBusConnection *bus;
static char context_path[256];
static struct xkb_context *xkb;
static struct xkb_keymap *keymap;
static struct xkb_state *key_state;
static uint32_t keycode_offset;
static char *queue[MAX_QUEUE_MESSAGES];
static int queue_head;
static int queue_count;

static void enqueue(const char *message) {
    if (queue_count == MAX_QUEUE_MESSAGES)
        return;
    queue[(queue_head + queue_count++) % MAX_QUEUE_MESSAGES] = strdup(message);
}

static void clear_queue(void) {
    while (queue_count > 0) {
        free(queue[queue_head]);
        queue_head = (queue_head + 1) % MAX_QUEUE_MESSAGES;
        queue_count--;
    }
}

static void keymap_handler(void *data, struct wl_keyboard *keyboard, uint32_t format, int32_t fd,
    uint32_t size) {
    (void)data;
    (void)keyboard;
    if (format == WL_KEYBOARD_KEYMAP_FORMAT_XKB_V1 && keymap == NULL) {
        char *text = mmap(NULL, size, PROT_READ, MAP_PRIVATE, fd, 0);
        if (text != MAP_FAILED) {
            keymap = xkb_keymap_new_from_string(xkb, text, XKB_KEYMAP_FORMAT_TEXT_V1,
                XKB_KEYMAP_COMPILE_NO_FLAGS);
            munmap(text, size);
        }
    }
    close(fd);
}

static void ignore_enter(void *data, struct wl_keyboard *keyboard, uint32_t serial,
    struct wl_surface *surface, struct wl_array *keys) {
    (void)data, (void)keyboard, (void)serial, (void)surface, (void)keys;
}

static void ignore_leave(void *data, struct wl_keyboard *keyboard, uint32_t serial,
    struct wl_surface *surface) {
    (void)data, (void)keyboard, (void)serial, (void)surface;
}

static void ignore_key(void *data, struct wl_keyboard *keyboard, uint32_t serial, uint32_t time,
    uint32_t key, uint32_t state) {
    (void)data, (void)keyboard, (void)serial, (void)time, (void)key, (void)state;
}

static void ignore_modifiers(void *data, struct wl_keyboard *keyboard, uint32_t serial,
    uint32_t depressed, uint32_t latched, uint32_t locked, uint32_t group) {
    (void)data, (void)keyboard, (void)serial, (void)depressed, (void)latched, (void)locked,
        (void)group;
}

static void ignore_repeat_info(void *data, struct wl_keyboard *keyboard, int32_t rate,
    int32_t delay) {
    (void)data, (void)keyboard, (void)rate, (void)delay;
}

static const struct wl_keyboard_listener keyboard_listener = {
    keymap_handler, ignore_enter, ignore_leave, ignore_key, ignore_modifiers, ignore_repeat_info,
};

static void seat_capabilities(void *data, struct wl_seat *seat, uint32_t capabilities) {
    struct wl_keyboard **keyboard = data;
    if ((capabilities & WL_SEAT_CAPABILITY_KEYBOARD) && *keyboard == NULL) {
        *keyboard = wl_seat_get_keyboard(seat);
        wl_keyboard_add_listener(*keyboard, &keyboard_listener, NULL);
    }
}

static void seat_name(void *data, struct wl_seat *seat, const char *name) {
    (void)data, (void)seat, (void)name;
}

static const struct wl_seat_listener seat_listener = { seat_capabilities, seat_name };

static void registry_global(void *data, struct wl_registry *registry, uint32_t name,
    const char *interface, uint32_t version) {
    struct wl_seat **seat = data;
    (void)version;
    if (*seat == NULL && strcmp(interface, wl_seat_interface.name) == 0)
        *seat = wl_registry_bind(registry, name, &wl_seat_interface, 1);
}

static void registry_global_remove(void *data, struct wl_registry *registry, uint32_t name) {
    (void)data, (void)registry, (void)name;
}

static const struct wl_registry_listener registry_listener = {
    registry_global, registry_global_remove,
};

/* GLFW reports evdev scancodes on Wayland and X11 keycodes on X11. */
static void load_keymap(void) {
    struct wl_display *display = getenv("WAYLAND_DISPLAY") ? wl_display_connect(NULL) : NULL;
    if (display != NULL) {
        struct wl_seat *seat = NULL;
        struct wl_keyboard *keyboard = NULL;
        struct wl_registry *registry = wl_display_get_registry(display);
        wl_registry_add_listener(registry, &registry_listener, &seat);
        wl_display_roundtrip(display);
        if (seat != NULL) {
            wl_seat_add_listener(seat, &seat_listener, &keyboard);
            wl_display_roundtrip(display);
            wl_display_roundtrip(display);
        }
        if (keyboard != NULL)
            wl_keyboard_destroy(keyboard);
        if (seat != NULL)
            wl_seat_destroy(seat);
        wl_registry_destroy(registry);
        wl_display_disconnect(display);
        keycode_offset = 8;
    }
    if (keymap == NULL)
        keymap = xkb_keymap_new_from_names(xkb, NULL, XKB_KEYMAP_COMPILE_NO_FLAGS);
}

static uint32_t x11_modifiers(void) {
    static const struct {
        const char *name;
        uint32_t mask;
    } modifiers[] = {
        { XKB_MOD_NAME_SHIFT, 1 << 0 },
        { XKB_MOD_NAME_CAPS, 1 << 1 },
        { XKB_MOD_NAME_CTRL, 1 << 2 },
        { XKB_MOD_NAME_ALT, 1 << 3 },
        { XKB_MOD_NAME_NUM, 1 << 4 },
        { XKB_MOD_NAME_LOGO, 1 << 6 },
    };
    uint32_t mask = 0;
    for (size_t i = 0; i < sizeof modifiers / sizeof modifiers[0]; i++) {
        if (xkb_state_mod_name_is_active(key_state, modifiers[i].name, XKB_STATE_MODS_EFFECTIVE) > 0)
            mask |= modifiers[i].mask;
    }
    return mask;
}

static int append(char *buffer, size_t capacity, const char *text) {
    size_t length = strlen(buffer);
    size_t add = strlen(text);
    if (length + add >= capacity)
        return 0;
    memcpy(buffer + length, text, add + 1);
    return 1;
}

/* Concatenates the text of an a(si) formatted-text array. */
static void read_formatted(DBusMessageIter *array, char *buffer, size_t capacity) {
    DBusMessageIter item;
    buffer[0] = '\0';
    if (dbus_message_iter_get_arg_type(array) != DBUS_TYPE_ARRAY)
        return;
    dbus_message_iter_recurse(array, &item);
    while (dbus_message_iter_get_arg_type(&item) == DBUS_TYPE_STRUCT) {
        DBusMessageIter field;
        const char *text = "";
        dbus_message_iter_recurse(&item, &field);
        dbus_message_iter_get_basic(&field, &text);
        append(buffer, capacity, text);
        dbus_message_iter_next(&item);
    }
}

static void enqueue_commit(const char *text) {
    char message[MAX_MESSAGE];
    snprintf(message, sizeof message, "commit" SEPARATOR "%s", text);
    enqueue(message);
}

/* Reads the a(si)i preedit payload at iter. */
static void enqueue_preedit(DBusMessageIter *iter) {
    char text[MAX_TEXT];
    char message[MAX_MESSAGE];
    int32_t cursor = 0;
    read_formatted(iter, text, sizeof text);
    dbus_message_iter_next(iter);
    if (dbus_message_iter_get_arg_type(iter) == DBUS_TYPE_INT32)
        dbus_message_iter_get_basic(iter, &cursor);
    snprintf(message, sizeof message, "preedit" SEPARATOR "%d" SEPARATOR "%s", cursor, text);
    enqueue(message);
}

static void enqueue_forward(uint32_t keysym, dbus_bool_t release) {
    char message[MAX_MESSAGE];
    snprintf(message, sizeof message, "forward" SEPARATOR "%u" SEPARATOR "%u" SEPARATOR "%d",
        xkb_keysym_to_utf32(keysym), keysym, release ? 1 : 0);
    enqueue(message);
}

/* Reads a(si)ia(si)a(si)a(ss)iibb from UpdateClientSideUI. */
static void enqueue_panel(DBusMessage *signal) {
    DBusMessageIter iter;
    DBusMessageIter candidates;
    char preedit[MAX_TEXT];
    char aux_up[MAX_TEXT];
    char aux_down[MAX_TEXT];
    char header[MAX_TEXT * 3];
    char message[MAX_MESSAGE];
    int32_t index = -1;
    if (!dbus_message_iter_init(signal, &iter))
        return;
    read_formatted(&iter, preedit, sizeof preedit);
    dbus_message_iter_next(&iter);
    dbus_message_iter_next(&iter);
    read_formatted(&iter, aux_up, sizeof aux_up);
    dbus_message_iter_next(&iter);
    read_formatted(&iter, aux_down, sizeof aux_down);
    dbus_message_iter_next(&iter);
    candidates = iter;
    dbus_message_iter_next(&iter);
    if (dbus_message_iter_get_arg_type(&iter) == DBUS_TYPE_INT32)
        dbus_message_iter_get_basic(&iter, &index);
    snprintf(header, sizeof header, "%s%s%s", aux_up, preedit, aux_down);
    snprintf(message, sizeof message, "panel" SEPARATOR "%d" SEPARATOR "%s", index, header);
    if (dbus_message_iter_get_arg_type(&candidates) == DBUS_TYPE_ARRAY) {
        DBusMessageIter item;
        dbus_message_iter_recurse(&candidates, &item);
        while (dbus_message_iter_get_arg_type(&item) == DBUS_TYPE_STRUCT) {
            DBusMessageIter field;
            const char *label = "";
            const char *text = "";
            dbus_message_iter_recurse(&item, &field);
            dbus_message_iter_get_basic(&field, &label);
            dbus_message_iter_next(&field);
            dbus_message_iter_get_basic(&field, &text);
            if (!append(message, sizeof message, SEPARATOR) ||
                !append(message, sizeof message, label) || !append(message, sizeof message, text))
                break;
            dbus_message_iter_next(&item);
        }
    }
    enqueue(message);
}

/* Reads the a(uv) events returned by ProcessKeyEventBatch. */
static void enqueue_batch(DBusMessageIter *array) {
    DBusMessageIter item;
    dbus_message_iter_recurse(array, &item);
    while (dbus_message_iter_get_arg_type(&item) == DBUS_TYPE_STRUCT) {
        DBusMessageIter field;
        DBusMessageIter variant;
        uint32_t kind = 0;
        dbus_message_iter_recurse(&item, &field);
        dbus_message_iter_get_basic(&field, &kind);
        dbus_message_iter_next(&field);
        dbus_message_iter_recurse(&field, &variant);
        if (kind == BATCHED_COMMIT_STRING &&
            dbus_message_iter_get_arg_type(&variant) == DBUS_TYPE_STRING) {
            const char *text = "";
            dbus_message_iter_get_basic(&variant, &text);
            enqueue_commit(text);
        } else if (kind == BATCHED_PREEDIT &&
            dbus_message_iter_get_arg_type(&variant) == DBUS_TYPE_STRUCT) {
            DBusMessageIter preedit;
            dbus_message_iter_recurse(&variant, &preedit);
            enqueue_preedit(&preedit);
        } else if (kind == BATCHED_FORWARD_KEY &&
            dbus_message_iter_get_arg_type(&variant) == DBUS_TYPE_STRUCT) {
            DBusMessageIter key;
            uint32_t keysym = 0;
            uint32_t state = 0;
            dbus_bool_t release = FALSE;
            dbus_message_iter_recurse(&variant, &key);
            dbus_message_iter_get_basic(&key, &keysym);
            dbus_message_iter_next(&key);
            dbus_message_iter_get_basic(&key, &state);
            dbus_message_iter_next(&key);
            dbus_message_iter_get_basic(&key, &release);
            enqueue_forward(keysym, release);
        }
        dbus_message_iter_next(&item);
    }
}

static void handle_signal(DBusMessage *signal) {
    DBusMessageIter iter;
    if (strcmp(dbus_message_get_path(signal) ?: "", context_path) != 0)
        return;
    if (dbus_message_is_signal(signal, INPUT_CONTEXT_INTERFACE, "CommitString")) {
        const char *text = "";
        if (dbus_message_get_args(signal, NULL, DBUS_TYPE_STRING, &text, DBUS_TYPE_INVALID))
            enqueue_commit(text);
    } else if (dbus_message_is_signal(signal, INPUT_CONTEXT_INTERFACE, "UpdateFormattedPreedit")) {
        if (dbus_message_iter_init(signal, &iter))
            enqueue_preedit(&iter);
    } else if (dbus_message_is_signal(signal, INPUT_CONTEXT_INTERFACE, "UpdateClientSideUI")) {
        enqueue_panel(signal);
    } else if (dbus_message_is_signal(signal, INPUT_CONTEXT_INTERFACE, "ForwardKey")) {
        uint32_t keysym = 0;
        uint32_t state = 0;
        dbus_bool_t release = FALSE;
        if (dbus_message_get_args(signal, NULL, DBUS_TYPE_UINT32, &keysym, DBUS_TYPE_UINT32, &state,
                DBUS_TYPE_BOOLEAN, &release, DBUS_TYPE_INVALID))
            enqueue_forward(keysym, release);
    } else if (dbus_message_is_signal(signal, INPUT_CONTEXT_INTERFACE, "NotifyFocusOut")) {
        enqueue("focusout");
    }
}

static void pump(void) {
    DBusMessage *message;
    if (bus == NULL)
        return;
    dbus_connection_read_write(bus, 0);
    while ((message = dbus_connection_pop_message(bus)) != NULL) {
        if (dbus_message_get_type(message) == DBUS_MESSAGE_TYPE_SIGNAL)
            handle_signal(message);
        dbus_message_unref(message);
    }
}

/* Sends a context method and waits, so signals it emits are queued before its reply. */
static DBusMessage *call(const char *method, int first_type, ...) {
    DBusMessage *message;
    DBusMessage *reply;
    va_list args;
    if (bus == NULL || context_path[0] == '\0')
        return NULL;
    message = dbus_message_new_method_call(FCITX_SERVICE, context_path, INPUT_CONTEXT_INTERFACE,
        method);
    va_start(args, first_type);
    dbus_message_append_args_valist(message, first_type, args);
    va_end(args);
    reply = dbus_connection_send_with_reply_and_block(bus, message, CALL_TIMEOUT_MS, NULL);
    dbus_message_unref(message);
    return reply;
}

static void call_void(const char *method, int first_type, ...) {
    DBusMessage *message;
    va_list args;
    if (bus == NULL || context_path[0] == '\0')
        return;
    message = dbus_message_new_method_call(FCITX_SERVICE, context_path, INPUT_CONTEXT_INTERFACE,
        method);
    va_start(args, first_type);
    dbus_message_append_args_valist(message, first_type, args);
    va_end(args);
    dbus_connection_send(bus, message, NULL);
    dbus_connection_flush(bus);
    dbus_message_unref(message);
}

static int create_context(void) {
    DBusMessage *message = dbus_message_new_method_call(FCITX_SERVICE, INPUT_METHOD_PATH,
        INPUT_METHOD_INTERFACE, "CreateInputContext");
    DBusMessageIter iter;
    DBusMessageIter array;
    DBusMessage *reply;
    const char *arguments[][2] = { { "program", "RimWorld" }, { "display", "wayland:" } };
    const char *path = NULL;
    dbus_uint64_t capability = CAPABILITY_PREEDIT | CAPABILITY_FORMATTED_PREEDIT |
        CAPABILITY_KEY_EVENT_ORDER_FIX | CAPABILITY_CLIENT_SIDE_INPUT_PANEL;
    char rule[512];

    dbus_message_iter_init_append(message, &iter);
    dbus_message_iter_open_container(&iter, DBUS_TYPE_ARRAY, "(ss)", &array);
    for (size_t i = 0; i < sizeof arguments / sizeof arguments[0]; i++) {
        DBusMessageIter pair;
        dbus_message_iter_open_container(&array, DBUS_TYPE_STRUCT, NULL, &pair);
        dbus_message_iter_append_basic(&pair, DBUS_TYPE_STRING, &arguments[i][0]);
        dbus_message_iter_append_basic(&pair, DBUS_TYPE_STRING, &arguments[i][1]);
        dbus_message_iter_close_container(&array, &pair);
    }
    dbus_message_iter_close_container(&iter, &array);
    reply = dbus_connection_send_with_reply_and_block(bus, message, CALL_TIMEOUT_MS * 5, NULL);
    dbus_message_unref(message);
    if (reply == NULL)
        return -1;
    if (dbus_message_iter_init(reply, &iter) &&
        dbus_message_iter_get_arg_type(&iter) == DBUS_TYPE_OBJECT_PATH)
        dbus_message_iter_get_basic(&iter, &path);
    if (path != NULL)
        snprintf(context_path, sizeof context_path, "%s", path);
    dbus_message_unref(reply);
    if (context_path[0] == '\0')
        return -1;

    snprintf(rule, sizeof rule, "type='signal',interface='%s',path='%s'", INPUT_CONTEXT_INTERFACE,
        context_path);
    dbus_bus_add_match(bus, rule, NULL);
    call_void("SetCapability", DBUS_TYPE_UINT64, &capability, DBUS_TYPE_INVALID);
    return 0;
}

EXPORT void cjk_stop(void) {
    if (bus != NULL) {
        call_void("DestroyIC", DBUS_TYPE_INVALID);
        dbus_connection_close(bus);
        dbus_connection_unref(bus);
        bus = NULL;
    }
    context_path[0] = '\0';
    clear_queue();
    if (key_state != NULL)
        xkb_state_unref(key_state);
    if (keymap != NULL)
        xkb_keymap_unref(keymap);
    if (xkb != NULL)
        xkb_context_unref(xkb);
    key_state = NULL;
    keymap = NULL;
    xkb = NULL;
}

/* Returns 0 on success, -1 without a keymap, -2 without fcitx5. */
EXPORT int cjk_start(void) {
    cjk_stop();
    xkb = xkb_context_new(XKB_CONTEXT_NO_FLAGS);
    if (xkb == NULL)
        return -1;
    load_keymap();
    if (keymap == NULL)
        return -1;
    key_state = xkb_state_new(keymap);
    bus = dbus_bus_get_private(DBUS_BUS_SESSION, NULL);
    if (bus == NULL)
        return -2;
    dbus_connection_set_exit_on_disconnect(bus, FALSE);
    return create_context() == 0 ? 0 : -2;
}

EXPORT void cjk_focus(int focused) {
    call_void(focused ? "FocusIn" : "FocusOut", DBUS_TYPE_INVALID);
}

/* Discards the composition; any commit it causes is queued before this returns. */
EXPORT void cjk_reset(void) {
    DBusMessage *reply = call("Reset", DBUS_TYPE_INVALID);
    if (reply != NULL)
        dbus_message_unref(reply);
    pump();
}

EXPORT void cjk_reset_keys(void) {
    if (key_state == NULL)
        return;
    xkb_state_unref(key_state);
    key_state = xkb_state_new(keymap);
}

/*
 * action: 0 release, 1 press, 2 repeat. Every key updates the modifier state;
 * only forwarded keys reach fcitx5. Returns 1 when fcitx5 consumed the key.
 */
EXPORT int cjk_key(uint32_t scancode, int action, uint32_t time, int forward) {
    DBusMessage *reply;
    DBusMessageIter iter;
    dbus_bool_t handled = FALSE;
    dbus_bool_t release = action == 0;
    uint32_t keycode = scancode + keycode_offset;
    uint32_t keysym;
    uint32_t state;
    if (key_state == NULL)
        return 0;
    keysym = xkb_state_key_get_one_sym(key_state, keycode);
    state = x11_modifiers();
    if (action != 2)
        xkb_state_update_key(key_state, keycode, release ? XKB_KEY_UP : XKB_KEY_DOWN);
    if (!forward || keysym == XKB_KEY_NoSymbol)
        return 0;

    reply = call("ProcessKeyEventBatch", DBUS_TYPE_UINT32, &keysym, DBUS_TYPE_UINT32, &keycode,
        DBUS_TYPE_UINT32, &state, DBUS_TYPE_BOOLEAN, &release, DBUS_TYPE_UINT32, &time,
        DBUS_TYPE_INVALID);
    pump();
    if (reply == NULL)
        return 0;
    if (dbus_message_iter_init(reply, &iter) &&
        dbus_message_iter_get_arg_type(&iter) == DBUS_TYPE_ARRAY) {
        enqueue_batch(&iter);
        dbus_message_iter_next(&iter);
        if (dbus_message_iter_get_arg_type(&iter) == DBUS_TYPE_BOOLEAN)
            dbus_message_iter_get_basic(&iter, &handled);
    }
    dbus_message_unref(reply);
    return handled ? 1 : 0;
}

/* Copies the next queued message without a terminator; returns its length or 0. */
EXPORT int cjk_poll(char *buffer, int capacity) {
    char *message;
    int length;
    if (queue_count == 0)
        pump();
    if (queue_count == 0 || capacity <= 0)
        return 0;
    message = queue[queue_head];
    length = (int)strlen(message);
    if (length > capacity)
        length = capacity;
    memcpy(buffer, message, (size_t)length);
    free(message);
    queue_head = (queue_head + 1) % MAX_QUEUE_MESSAGES;
    queue_count--;
    return length;
}
