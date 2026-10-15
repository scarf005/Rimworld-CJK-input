/*
 * Integration test against the running fcitx5 session. Requires fcitx5-hangul
 * as an input method and a keymap where Right Alt or the Hangul key emits the
 * Hangul keysym (for example `kr` with `korean:ralt_hangul`).
 */

#include <assert.h>
#include <stdio.h>
#include <string.h>

#include "fcitxcjkinput.c"

#define KEY_G 34
#define KEY_K 37
#define KEY_S 31
#define KEY_R 19
#define KEY_M 50
#define KEY_F 33
#define KEY_SPACE 57
#define KEY_RIGHTALT 100
#define KEY_HANGEUL 122

static char commits[MAX_TEXT];
static char preedit[MAX_TEXT];
static uint32_t scancode_base;

static void drain(void) {
    char buffer[MAX_MESSAGE + 1];
    int length;
    while ((length = cjk_poll(buffer, MAX_MESSAGE)) > 0) {
        buffer[length] = '\0';
        if (strncmp(buffer, "commit" SEPARATOR, 7) == 0)
            append(commits, sizeof commits, buffer + 7);
        else if (strncmp(buffer, "preedit" SEPARATOR, 8) == 0)
            snprintf(preedit, sizeof preedit, "%s", strchr(buffer + 8, '\x1f') + 1);
    }
}

/*
 * Taps an evdev key as GLFW reports it on the platform under test. The game
 * drains right after each key, so the press's events must already be queued.
 */
static int tap(uint32_t key) {
    int handled = cjk_key(key + scancode_base, 1, 0, 1);
    drain();
    cjk_key(key + scancode_base, 0, 0, 1);
    return handled;
}

static uint32_t hangul_toggle(void) {
    uint32_t candidates[] = { KEY_HANGEUL, KEY_RIGHTALT };
    for (size_t i = 0; i < sizeof candidates / sizeof candidates[0]; i++) {
        if (xkb_state_key_get_one_sym(key_state, candidates[i] + 8) == XKB_KEY_Hangul)
            return candidates[i];
    }
    return 0;
}

/* GLFW reports evdev scancodes on Wayland and X11 keycodes (evdev + 8) on X11. */
static void compose(int evdev_scancodes) {
    uint32_t toggle;
    int toggled = 0;
    scancode_base = evdev_scancodes ? 0 : 8;
    commits[0] = '\0';
    preedit[0] = '\0';
    assert(cjk_start(evdev_scancodes) == 0);
    toggle = hangul_toggle();
    assert(toggle != 0 && "no key emits the Hangul keysym");
    cjk_focus(1);

    /* A fresh context may start in either state; switch until g composes. */
    tap(KEY_G);
    if (preedit[0] == '\0') {
        cjk_reset();
        drain();
        tap(toggle);
        toggled = 1;
        commits[0] = '\0';
        tap(KEY_G);
    }
    assert(strcmp(preedit, "ㅎ") == 0);

    assert(tap(KEY_K) == 1);
    assert(tap(KEY_S) == 1);
    assert(tap(KEY_R) == 1);
    assert(strcmp(commits, "한") == 0);
    assert(strcmp(preedit, "ㄱ") == 0);
    assert(tap(KEY_M) == 1);
    assert(tap(KEY_F) == 1);
    assert(strcmp(preedit, "글") == 0);

    /* Space commits the syllable first and stays with the game. */
    assert(tap(KEY_SPACE) == 0);
    assert(strcmp(commits, "한글") == 0);
    assert(preedit[0] == '\0');

    if (toggled)
        tap(toggle);
    cjk_focus(0);
    cjk_stop();
}

int main(void) {
    compose(1);
    puts("PASS hangul composition with Wayland evdev scancodes");
    compose(0);
    puts("PASS hangul composition with X11 keycodes");
    return 0;
}
