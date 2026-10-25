#ifndef PXBRIDGE_H
#define PXBRIDGE_H

// The boundary between the emulator core (C#, src/Ryujinx/Native) and the native UI (Swift).
//
// The core starts first, loads libProjectXUI.dylib and calls px_ui_run() on the main thread with a
// PXCoreCallbacks table. The UI then owns the main thread until the app quits. The UI also exports:
//
//   int32_t px_ui_run(const PXCoreCallbacks *callbacks);   // runs the app, returns when it quits
//   void    px_ui_post_event(const char *json);            // core -> UI notification, any thread
//   char   *px_ui_request(const char *json);               // core -> UI question that waits for the
//                                                          // player (a dialog); call off the main thread
//   void    px_ui_free(char *string);                      // frees strings from px_ui_request
//
// Every string is UTF-8 and NUL-terminated. Commands, events and requests are JSON objects; see
// src/Ryujinx/Native/README.md for their fields.

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define PX_BRIDGE_VERSION 1

enum {
    PX_MOUSE_MOVE = 0,
    PX_MOUSE_DOWN = 1,
    PX_MOUSE_UP = 2,
    PX_MOUSE_SCROLL = 3,
};

typedef struct PXCoreCallbacks {
    uint32_t version;

    /// Runs a JSON command ({"cmd": "...", ...}) and returns the JSON reply, which the caller
    /// releases with free_string.
    char *(*command)(const char *json);
    void (*free_string)(char *string);

    /// Keyboard input from the game view: the macOS virtual key code (kVK_*) and whether it is down.
    void (*key_event)(uint16_t key_code, bool pressed);

    /// Mouse input from the game view, in points from the view's top-left corner. For PX_MOUSE_SCROLL,
    /// x and y are the scroll deltas. button: 0 left, 1 right, 2 middle.
    void (*mouse_event)(int32_t kind, double x, double y, int32_t button);

    /// The game view's size changed. width and height are in pixels.
    void (*view_resized)(double width, double height, double scale);

    /// The game view gained or lost keyboard focus.
    void (*focus_changed)(bool focused);
} PXCoreCallbacks;

#ifdef __cplusplus
}
#endif

#endif
