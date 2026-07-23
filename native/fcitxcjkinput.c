/*
 * Native fcitx5 signal bridge loaded in-process by FcitxCjkInput.dll.
 *
 * Public API:
 *   void fcitx_bridge_set_notify(void (*callback)(void *), void *user_data)
 *   int fcitx_bridge_start(uint32_t rimworld_pid)
 *   int fcitx_bridge_poll(char *buffer, int capacity)
 *   void fcitx_bridge_stop(void)
 *
 * Queue messages:
 *   READY:<rimworld-pid>
 *   EVENT:<context-id>:<sequence>:ENGINE:<unique-name>
 *   EVENT:<context-id>:<sequence>:PREEDIT:<UTF-8 byte cursor>:<UTF-8 hex>
 *   EVENT:<context-id>:<sequence>:COMMIT:<UTF-8 hex>
 *   EVENT:<context-id>:<sequence>:FOCUS:IN|OUT
 *   STOPPED
 *   LOG:<diagnostic>
 *   ERROR:<diagnostic>
 */

#include <dbus/dbus.h>
#include <errno.h>
#include <pthread.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define EXPORT __attribute__((visibility("default")))
#define INPUT_CONTEXT_INTERFACE "org.fcitx.Fcitx.InputContext1"
#define MAX_TEXT 4096
#define MAX_MESSAGE 16384
#define MAX_DESTINATIONS 16
#define MAX_CONTEXTS 64

struct context_entry {
    char destination[128];
    char path[256];
};

typedef void (*notify_callback)(void *user_data);

struct message_node {
    char *text;
    struct message_node *next;
};

static pthread_mutex_t queue_mutex = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t state_mutex = PTHREAD_MUTEX_INITIALIZER;
static struct message_node *queue_head;
static struct message_node *queue_tail;
static pthread_t worker_thread;
static int worker_created;
static int dbus_threads_ready;
static volatile int running;
static uint32_t rimworld_pid;
static notify_callback queue_notify;
static void *queue_notify_data;

static DBusConnection *connection;
static char rimworld_destinations[MAX_DESTINATIONS][128];
static size_t rimworld_destination_count;
static struct context_entry contexts[MAX_CONTEXTS];
static size_t context_count;
static uint64_t next_sequence;

static void enqueue_message(const char *format, ...) {
    char buffer[MAX_MESSAGE];
    va_list args;
    va_start(args, format);
    const int length = vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    if (length <= 0 || (size_t)length >= sizeof(buffer)) return;

    struct message_node *node = malloc(sizeof(*node));
    if (!node) return;
    node->text = strdup(buffer);
    if (!node->text) {
        free(node);
        return;
    }
    node->next = NULL;

    pthread_mutex_lock(&queue_mutex);
    const int notify = queue_head == NULL;
    if (queue_tail) queue_tail->next = node;
    else queue_head = node;
    queue_tail = node;
    pthread_mutex_unlock(&queue_mutex);

    if (notify && queue_notify) queue_notify(queue_notify_data);
}

static void enqueue_hex(const char *prefix, const char *text) {
    static const char digits[] = "0123456789ABCDEF";
    const size_t prefix_length = strlen(prefix);
    const size_t text_length = strlen(text);
    const size_t length = prefix_length + 1 + text_length * 2;
    char *message = malloc(length + 1);
    if (!message) return;

    memcpy(message, prefix, prefix_length);
    message[prefix_length] = ':';
    for (size_t i = 0; i < text_length; i++) {
        const unsigned char byte = (unsigned char)text[i];
        message[prefix_length + 1 + i * 2] = digits[byte >> 4];
        message[prefix_length + 2 + i * 2] = digits[byte & 0x0f];
    }
    message[length] = '\0';
    enqueue_message("%s", message);
    free(message);
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
        enqueue_message("ERROR:pid lookup destination=%s error=%s", name,
            dbus_error_is_set(&error) ? error.message : "no reply");
        dbus_error_free(&error);
        return 0;
    }

    uint32_t pid = 0;
    if (!dbus_message_get_args(reply, &error, DBUS_TYPE_UINT32, &pid,
            DBUS_TYPE_INVALID)) {
        enqueue_message("ERROR:pid reply destination=%s error=%s", name,
            dbus_error_is_set(&error) ? error.message : "invalid reply");
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
        enqueue_message("ERROR:ListNames error=%s",
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

    rimworld_destination_count = 0;
    DBusMessageIter names;
    dbus_message_iter_recurse(&root, &names);
    while (dbus_message_iter_get_arg_type(&names) == DBUS_TYPE_STRING &&
        rimworld_destination_count < MAX_DESTINATIONS) {
        const char *name = "";
        dbus_message_iter_get_basic(&names, &name);
        if (name[0] == ':' && connection_pid(name) == rimworld_pid) {
            snprintf(rimworld_destinations[rimworld_destination_count],
                sizeof(rimworld_destinations[0]), "%s", name);
            enqueue_message("LOG:target destination=%s pid=%u", name, rimworld_pid);
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
        snprintf(rule_buffer, sizeof(rule_buffer),
            "type='method_call',interface='org.fcitx.Fcitx.InputContext1',member='FocusIn',sender='%.127s'",
            rimworld_destinations[i]);
        rule = rule_buffer;
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
        enqueue_message("ERROR:BecomeMonitor error=%s",
            dbus_error_is_set(&error) ? error.message : "no reply");
        dbus_error_free(&error);
        return 0;
    }
    dbus_message_unref(reply);
    return 1;
}

static const char *client_name(DBusMessage *message) {
    return dbus_message_get_type(message) == DBUS_MESSAGE_TYPE_SIGNAL
        ? dbus_message_get_destination(message)
        : dbus_message_get_sender(message);
}

static int is_rimworld_message(DBusMessage *message) {
    const char *client = client_name(message);
    if (!client || client[0] != ':') return 0;
    for (size_t i = 0; i < rimworld_destination_count; i++) {
        if (strcmp(client, rimworld_destinations[i]) == 0) return 1;
    }
    return 0;
}

static uint32_t context_id(DBusMessage *message) {
    const char *client = client_name(message);
    const char *path = dbus_message_get_path(message);
    if (!client || !path) return 0;

    for (size_t i = 0; i < context_count; i++) {
        if (strcmp(contexts[i].destination, client) == 0 &&
            strcmp(contexts[i].path, path) == 0)
            return (uint32_t)(i + 1);
    }
    if (context_count >= MAX_CONTEXTS) return 0;

    struct context_entry *entry = &contexts[context_count++];
    snprintf(entry->destination, sizeof(entry->destination), "%s", client);
    snprintf(entry->path, sizeof(entry->path), "%s", path);
    enqueue_message("LOG:context id=%zu destination=%s path=%s",
        context_count, client, path);
    return (uint32_t)context_count;
}

static int read_string(DBusMessage *message, char *buffer, size_t size) {
    DBusError error;
    dbus_error_init(&error);
    const char *text = "";
    if (!dbus_message_get_args(message, &error, DBUS_TYPE_STRING, &text,
            DBUS_TYPE_INVALID)) {
        enqueue_message("ERROR:string parse member=%s error=%s",
            dbus_message_get_member(message),
            dbus_error_is_set(&error) ? error.message : "invalid signal");
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
            if (part_length >= size - used) return 0;
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

static void handle_message(DBusMessage *message) {
    if (!dbus_message_has_interface(message, INPUT_CONTEXT_INTERFACE) ||
        !is_rimworld_message(message))
        return;

    const char *member = dbus_message_get_member(message);
    const uint32_t context = context_id(message);
    if (!member || context == 0) return;
    const unsigned long long sequence = ++next_sequence;
    if (dbus_message_get_type(message) == DBUS_MESSAGE_TYPE_METHOD_CALL) {
        if (strcmp(member, "FocusIn") == 0) {
            enqueue_message("LOG:FocusIn context=%u", context);
            enqueue_message("EVENT:%u:%llu:FOCUS:IN", context, sequence);
        }
        return;
    }
    if (dbus_message_get_type(message) != DBUS_MESSAGE_TYPE_SIGNAL) return;

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
            enqueue_message("LOG:CurrentIM context=%u name=%s unique=%s lang=%s",
                context, name, unique_name, language);
            enqueue_message("EVENT:%u:%llu:ENGINE:%s", context, sequence, unique_name);
        } else {
            enqueue_message("ERROR:CurrentIM context=%u error=%s", context,
                dbus_error_is_set(&error) ? error.message : "invalid signal");
            dbus_error_free(&error);
        }
    } else if (strcmp(member, "CommitString") == 0) {
        char text[MAX_TEXT];
        if (read_string(message, text, sizeof(text))) {
            enqueue_message("LOG:CommitString context=%u bytes=%zu text=%s",
                context, strlen(text), text);
            char prefix[96];
            snprintf(prefix, sizeof(prefix), "EVENT:%u:%llu:COMMIT",
                context, sequence);
            enqueue_hex(prefix, text);
        }
    } else if (strcmp(member, "UpdateFormattedPreedit") == 0) {
        char text[MAX_TEXT];
        int32_t cursor = 0;
        if (read_preedit(message, text, sizeof(text), &cursor)) {
            enqueue_message("LOG:Preedit context=%u cursor=%d bytes=%zu text=%s",
                context, cursor, strlen(text), text);
            char prefix[96];
            snprintf(prefix, sizeof(prefix), "EVENT:%u:%llu:PREEDIT:%d",
                context, sequence, cursor);
            enqueue_hex(prefix, text);
        } else {
            enqueue_message("ERROR:Preedit context=%u parse failed", context);
        }
    } else if (strcmp(member, "NotifyFocusOut") == 0) {
        enqueue_message("LOG:NotifyFocusOut context=%u", context);
        enqueue_message("EVENT:%u:%llu:FOCUS:OUT", context, sequence);
    }
}

static void close_connection(void) {
    if (!connection) return;
    dbus_connection_close(connection);
    dbus_connection_unref(connection);
    connection = NULL;
}

static void *monitor_worker(void *unused) {
    (void)unused;

    DBusError error;
    dbus_error_init(&error);
    connection = dbus_bus_get_private(DBUS_BUS_SESSION, &error);
    if (!connection) {
        enqueue_message("ERROR:session bus error=%s",
            dbus_error_is_set(&error) ? error.message : "unknown");
        dbus_error_free(&error);
        goto stopped;
    }
    dbus_connection_set_exit_on_disconnect(connection, FALSE);

    if (!discover_rimworld_destinations()) {
        enqueue_message("ERROR:no D-Bus destination for pid=%u", rimworld_pid);
        goto stopped;
    }
    if (!become_monitor()) goto stopped;

    enqueue_message("READY:%u", rimworld_pid);
    while (running && kill((pid_t)rimworld_pid, 0) == 0) {
        if (!dbus_connection_read_write(connection, 250)) break;
        DBusMessage *message;
        while ((message = dbus_connection_pop_message(connection)) != NULL) {
            handle_message(message);
            dbus_message_unref(message);
        }
    }

stopped:
    close_connection();
    running = 0;
    enqueue_message("STOPPED");
    return NULL;
}

EXPORT void fcitx_bridge_set_notify(notify_callback callback, void *user_data) {
    queue_notify = callback;
    queue_notify_data = user_data;
}

EXPORT int fcitx_bridge_start(uint32_t pid) {
    pthread_mutex_lock(&state_mutex);
    if (!dbus_threads_ready) {
        if (!dbus_threads_init_default()) {
            pthread_mutex_unlock(&state_mutex);
            return ENOMEM;
        }
        dbus_threads_ready = 1;
    }
    if (running) {
        pthread_mutex_unlock(&state_mutex);
        return 0;
    }
    if (worker_created) {
        pthread_join(worker_thread, NULL);
        worker_created = 0;
    }

    rimworld_pid = pid;
    context_count = 0;
    next_sequence = 0;
    running = 1;
    const int result = pthread_create(&worker_thread, NULL, monitor_worker, NULL);
    if (result == 0) worker_created = 1;
    else {
        running = 0;
        enqueue_message("ERROR:pthread_create result=%d", result);
    }
    pthread_mutex_unlock(&state_mutex);
    return result;
}

EXPORT int fcitx_bridge_poll(char *buffer, int capacity) {
    if (!buffer || capacity <= 1) return -1;

    pthread_mutex_lock(&queue_mutex);
    struct message_node *node = queue_head;
    if (!node) {
        pthread_mutex_unlock(&queue_mutex);
        return 0;
    }
    queue_head = node->next;
    if (!queue_head) queue_tail = NULL;
    pthread_mutex_unlock(&queue_mutex);

    const size_t length = strlen(node->text);
    const size_t copy_length = length < (size_t)(capacity - 1)
        ? length : (size_t)(capacity - 1);
    memcpy(buffer, node->text, copy_length);
    buffer[copy_length] = '\0';
    free(node->text);
    free(node);
    return (int)copy_length;
}

EXPORT int fcitx_bridge_is_running(void) {
    return running;
}

EXPORT void fcitx_bridge_stop(void) {
    pthread_mutex_lock(&state_mutex);
    running = 0;
    if (worker_created) {
        pthread_join(worker_thread, NULL);
        worker_created = 0;
    }
    pthread_mutex_unlock(&state_mutex);
}
