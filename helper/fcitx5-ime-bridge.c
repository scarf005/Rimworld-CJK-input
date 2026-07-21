/*
 * Observe the fcitx5 context created by Unity's embedded SDL2 backend.
 *
 * Unity 2022 receives fcitx5 preedit/commit signals but does not connect them
 * to IMGUI text fields. This helper eavesdrops only signals addressed to its
 * parent RimWorld process and forwards them as a line protocol:
 *
 *   READY:<rimworld-pid>
 *   ENGINE:<unique-name>
 *   PREEDIT_HEX:<UTF-8 byte cursor>:<UTF-8 bytes as hex>
 *   COMMIT_HEX:<UTF-8 bytes as hex>
 *   FOCUS:OUT
 */

#include <dbus/dbus.h>
#include <errno.h>
#include <locale.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define INPUT_CONTEXT_INTERFACE "org.fcitx.Fcitx.InputContext1"
#define MAX_TEXT 4096

static DBusConnection *connection;
static pid_t rimworld_pid;
static char rimworld_destinations[16][128];
static size_t rimworld_destination_count;

static void log_message(const char *format, ...) {
    va_list args;
    va_start(args, format);
    fputs("BRIDGE ", stderr);
    vfprintf(stderr, format, args);
    fputc('\n', stderr);
    fflush(stderr);
    va_end(args);
}

static void output_message(const char *format, ...) {
    char buffer[16384];
    va_list args;
    va_start(args, format);
    const int length = vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    if (length <= 0 || (size_t)length >= sizeof(buffer)) {
        log_message("output overflow length=%d", length);
        return;
    }
    write(STDOUT_FILENO, buffer, (size_t)length);
    write(STDOUT_FILENO, "\n", 1);
}

static void output_hex(const char *prefix, const char *text) {
    static const char digits[] = "0123456789ABCDEF";
    const size_t length = strlen(text);
    char *hex = malloc(length * 2 + 1);
    if (!hex) {
        log_message("hex allocation failed bytes=%zu", length);
        return;
    }
    for (size_t i = 0; i < length; i++) {
        const unsigned char byte = (unsigned char)text[i];
        hex[i * 2] = digits[byte >> 4];
        hex[i * 2 + 1] = digits[byte & 0x0f];
    }
    hex[length * 2] = '\0';
    output_message("%s:%s", prefix, hex);
    free(hex);
}

static uint32_t connection_pid(const char *name) {
    DBusMessage *request = dbus_message_new_method_call(
        DBUS_SERVICE_DBUS, DBUS_PATH_DBUS, DBUS_INTERFACE_DBUS,
        "GetConnectionUnixProcessID");
    if (!request) return 0;
    dbus_message_append_args(request, DBUS_TYPE_STRING, &name, DBUS_TYPE_INVALID);

    DBusError error;
    dbus_error_init(&error);
    DBusMessage *reply = dbus_connection_send_with_reply_and_block(
        connection, request, 1000, &error);
    dbus_message_unref(request);
    if (!reply) {
        log_message("pid lookup failed destination=%s error=%s", name,
            dbus_error_is_set(&error) ? error.message : "no reply");
        dbus_error_free(&error);
        return 0;
    }

    uint32_t pid = 0;
    if (!dbus_message_get_args(reply, &error, DBUS_TYPE_UINT32, &pid,
            DBUS_TYPE_INVALID)) {
        log_message("pid reply invalid destination=%s error=%s", name,
            dbus_error_is_set(&error) ? error.message : "unknown");
        dbus_error_free(&error);
    }
    dbus_message_unref(reply);
    return pid;
}

static int discover_rimworld_destinations(void) {
    DBusMessage *request = dbus_message_new_method_call(
        DBUS_SERVICE_DBUS, DBUS_PATH_DBUS, DBUS_INTERFACE_DBUS, "ListNames");
    if (!request) return 0;

    DBusError error;
    dbus_error_init(&error);
    DBusMessage *reply = dbus_connection_send_with_reply_and_block(
        connection, request, 1000, &error);
    dbus_message_unref(request);
    if (!reply) {
        log_message("ListNames failed error=%s",
            dbus_error_is_set(&error) ? error.message : "no reply");
        dbus_error_free(&error);
        return 0;
    }

    DBusMessageIter root;
    if (!dbus_message_iter_init(reply, &root) ||
        dbus_message_iter_get_arg_type(&root) != DBUS_TYPE_ARRAY) {
        dbus_message_unref(reply);
        return 0;
    }

    DBusMessageIter names;
    dbus_message_iter_recurse(&root, &names);
    while (dbus_message_iter_get_arg_type(&names) == DBUS_TYPE_STRING &&
        rimworld_destination_count < 16) {
        const char *name = "";
        dbus_message_iter_get_basic(&names, &name);
        if (name[0] == ':' && connection_pid(name) == (uint32_t)rimworld_pid) {
            snprintf(rimworld_destinations[rimworld_destination_count],
                sizeof(rimworld_destinations[0]), "%s", name);
            log_message("target destination=%s pid=%d", name, (int)rimworld_pid);
            rimworld_destination_count++;
        }
        dbus_message_iter_next(&names);
    }
    dbus_message_unref(reply);
    return rimworld_destination_count > 0;
}

static int become_monitor(void) {
    DBusMessage *request = dbus_message_new_method_call(
        DBUS_SERVICE_DBUS, DBUS_PATH_DBUS, "org.freedesktop.DBus.Monitoring",
        "BecomeMonitor");
    if (!request) return 0;

    DBusMessageIter root;
    DBusMessageIter rules;
    dbus_message_iter_init_append(request, &root);
    dbus_message_iter_open_container(&root, DBUS_TYPE_ARRAY, "s", &rules);
    char rule_buffer[256];
    for (size_t i = 0; i < rimworld_destination_count; i++) {
        snprintf(rule_buffer, sizeof(rule_buffer),
            "type='signal',interface='org.fcitx.Fcitx.InputContext1',destination='%.127s'",
            rimworld_destinations[i]);
        const char *rule = rule_buffer;
        dbus_message_iter_append_basic(&rules, DBUS_TYPE_STRING, &rule);
    }
    dbus_message_iter_close_container(&root, &rules);
    uint32_t flags = 0;
    dbus_message_iter_append_basic(&root, DBUS_TYPE_UINT32, &flags);

    DBusError error;
    dbus_error_init(&error);
    DBusMessage *reply = dbus_connection_send_with_reply_and_block(
        connection, request, 1000, &error);
    dbus_message_unref(request);
    if (!reply) {
        log_message("BecomeMonitor failed error=%s",
            dbus_error_is_set(&error) ? error.message : "no reply");
        dbus_error_free(&error);
        return 0;
    }
    dbus_message_unref(reply);
    return 1;
}

static int is_rimworld_signal(DBusMessage *message) {
    const char *destination = dbus_message_get_destination(message);
    if (!destination || destination[0] != ':') return 0;
    for (size_t i = 0; i < rimworld_destination_count; i++) {
        if (strcmp(destination, rimworld_destinations[i]) == 0)
            return 1;
    }
    return 0;
}

static int read_string(DBusMessage *message, char *buffer, size_t size) {
    DBusError error;
    dbus_error_init(&error);
    const char *text = "";
    if (!dbus_message_get_args(message, &error, DBUS_TYPE_STRING, &text,
            DBUS_TYPE_INVALID)) {
        log_message("string signal parse failed member=%s error=%s",
            dbus_message_get_member(message),
            dbus_error_is_set(&error) ? error.message : "unknown");
        dbus_error_free(&error);
        return 0;
    }
    snprintf(buffer, size, "%s", text);
    return 1;
}

static int read_preedit(DBusMessage *message, char *buffer, size_t size,
    int32_t *cursor) {
    DBusMessageIter root;
    if (!dbus_message_iter_init(message, &root) ||
        dbus_message_iter_get_arg_type(&root) != DBUS_TYPE_ARRAY)
        return 0;

    buffer[0] = '\0';
    size_t used = 0;
    DBusMessageIter array;
    dbus_message_iter_recurse(&root, &array);
    while (dbus_message_iter_get_arg_type(&array) == DBUS_TYPE_STRUCT) {
        DBusMessageIter entry;
        dbus_message_iter_recurse(&array, &entry);
        if (dbus_message_iter_get_arg_type(&entry) == DBUS_TYPE_STRING) {
            const char *part = "";
            dbus_message_iter_get_basic(&entry, &part);
            const size_t part_length = strlen(part);
            if (part_length >= size - used) {
                log_message("preedit truncated bytes=%zu capacity=%zu", used + part_length,
                    size);
                return 0;
            }
            memcpy(buffer + used, part, part_length);
            used += part_length;
            buffer[used] = '\0';
        }
        dbus_message_iter_next(&array);
    }

    if (!dbus_message_iter_next(&root) ||
        dbus_message_iter_get_arg_type(&root) != DBUS_TYPE_INT32)
        return 0;
    dbus_message_iter_get_basic(&root, cursor);
    return 1;
}

static void handle_signal(DBusMessage *message) {
    if (dbus_message_get_type(message) != DBUS_MESSAGE_TYPE_SIGNAL ||
        !dbus_message_has_interface(message, INPUT_CONTEXT_INTERFACE) ||
        !is_rimworld_signal(message))
        return;

    const char *member = dbus_message_get_member(message);
    const char *path = dbus_message_get_path(message);
    if (!member) return;

    if (strcmp(member, "CurrentIM") == 0) {
        DBusError error;
        dbus_error_init(&error);
        const char *name = "";
        const char *unique_name = "";
        const char *language = "";
        if (dbus_message_get_args(message, &error,
                DBUS_TYPE_STRING, &name,
                DBUS_TYPE_STRING, &unique_name,
                DBUS_TYPE_STRING, &language,
                DBUS_TYPE_INVALID)) {
            log_message("signal path=%s member=CurrentIM name=%s unique=%s lang=%s",
                path, name, unique_name, language);
            output_message("ENGINE:%s", unique_name);
        } else {
            log_message("CurrentIM parse failed error=%s", error.message);
            dbus_error_free(&error);
        }
    } else if (strcmp(member, "CommitString") == 0) {
        char text[MAX_TEXT];
        if (read_string(message, text, sizeof(text))) {
            log_message("signal path=%s member=CommitString bytes=%zu text=%s", path,
                strlen(text), text);
            output_hex("COMMIT_HEX", text);
        }
    } else if (strcmp(member, "UpdateFormattedPreedit") == 0) {
        char text[MAX_TEXT];
        int32_t cursor = 0;
        if (read_preedit(message, text, sizeof(text), &cursor)) {
            log_message("signal path=%s member=UpdateFormattedPreedit cursor=%d bytes=%zu text=%s",
                path, cursor, strlen(text), text);
            char prefix[64];
            snprintf(prefix, sizeof(prefix), "PREEDIT_HEX:%d", cursor);
            output_hex(prefix, text);
        } else {
            log_message("UpdateFormattedPreedit parse failed path=%s", path);
        }
    } else if (strcmp(member, "NotifyFocusOut") == 0) {
        log_message("signal path=%s member=NotifyFocusOut", path);
        output_message("FOCUS:OUT");
    }
}

int main(void) {
    setlocale(LC_ALL, "C.UTF-8");
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    rimworld_pid = getppid();

    DBusError error;
    dbus_error_init(&error);
    connection = dbus_bus_get_private(DBUS_BUS_SESSION, &error);
    if (!connection) {
        log_message("session bus failed error=%s",
            dbus_error_is_set(&error) ? error.message : "unknown");
        dbus_error_free(&error);
        return 1;
    }
    dbus_connection_set_exit_on_disconnect(connection, FALSE);

    if (!discover_rimworld_destinations()) {
        log_message("no D-Bus destination found for parent=%d", (int)rimworld_pid);
        dbus_connection_close(connection);
        dbus_connection_unref(connection);
        return 2;
    }
    if (!become_monitor()) {
        dbus_connection_close(connection);
        dbus_connection_unref(connection);
        return 1;
    }
    log_message("ready parent=%d destinations=%zu", (int)rimworld_pid,
        rimworld_destination_count);
    output_message("READY:%d", (int)rimworld_pid);

    while (getppid() == rimworld_pid && kill(rimworld_pid, 0) == 0) {
        if (!dbus_connection_read_write(connection, 250)) break;
        DBusMessage *message;
        while ((message = dbus_connection_pop_message(connection)) != NULL) {
            handle_signal(message);
            dbus_message_unref(message);
        }
    }

    log_message("exit parent=%d errno=%d", (int)rimworld_pid, errno);
    dbus_connection_close(connection);
    dbus_connection_unref(connection);
    return 0;
}
