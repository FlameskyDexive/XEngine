// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define XENGINE_PLAYER_ABI_VERSION 8u

typedef struct xengine_player xengine_player;

typedef int32_t xengine_player_touch_action;
typedef int32_t xengine_player_mouse_action;

enum {
    XENGINE_PLAYER_TOUCH_DOWN = 0,
    XENGINE_PLAYER_TOUCH_UP = 1,
    XENGINE_PLAYER_TOUCH_MOVE = 2,
    XENGINE_PLAYER_TOUCH_CANCEL = 3,
};

enum {
    XENGINE_PLAYER_MOUSE_PRESS = 1,
    XENGINE_PLAYER_MOUSE_RELEASE = 2,
    XENGINE_PLAYER_MOUSE_MOVE = 3,
    XENGINE_PLAYER_MOUSE_CANCEL = 4,
};

// Screen orientation requests from the engine to the host. The packed value carries the
// XENGINE_PLAYER_ORIENTATION_* request in the low byte and the allowed-orientation bitmask in
// the next nibble (bit8 portrait, bit9 portrait upside-down, bit10 landscape left, bit11
// landscape right). The host stores the request; the ArkTS layer polls and applies it through
// window.setPreferredOrientation. Returns 0 when the request was stored.
enum {
    XENGINE_PLAYER_ORIENTATION_UNKNOWN = 0,
    XENGINE_PLAYER_ORIENTATION_PORTRAIT = 1,
    XENGINE_PLAYER_ORIENTATION_PORTRAIT_UPSIDE_DOWN = 2,
    XENGINE_PLAYER_ORIENTATION_LANDSCAPE_LEFT = 3,
    XENGINE_PLAYER_ORIENTATION_LANDSCAPE_RIGHT = 4,
    XENGINE_PLAYER_ORIENTATION_AUTO_ROTATION = 5,
};

typedef void (*xengine_player_log_callback)(int32_t severity, const char* utf8, void* context);
typedef uint64_t (*xengine_player_create_vulkan_surface_callback)(
    uint64_t instance,
    void* native_window,
    void* context);
typedef int32_t (*xengine_player_set_preferred_orientation_callback)(int32_t packed_request, void* context);

typedef struct xengine_player_create_args {
    uint32_t struct_size;
    uint32_t abi_version;
    const char* content_root;
    uint64_t content_root_length;
    xengine_player_log_callback log;
    void* context;
    xengine_player_create_vulkan_surface_callback create_vulkan_surface;
    xengine_player_set_preferred_orientation_callback set_preferred_orientation;
    int32_t (*set_keep_screen_on)(int32_t enabled, void* context);
} xengine_player_create_args;

uint32_t xengine_player_abi_version(void);
xengine_player* xengine_player_create(const xengine_player_create_args* args);
int32_t xengine_player_attach_surface(
    xengine_player* player,
    void* native_window,
    int32_t width,
    int32_t height);
void xengine_player_resize_surface(xengine_player* player, int32_t width, int32_t height);
void xengine_player_detach_surface(xengine_player* player);
void xengine_player_set_resumed(xengine_player* player, int32_t resumed);
void xengine_player_touch(
    xengine_player* player,
    int32_t pointer_id,
    xengine_player_touch_action action,
    float x,
    float y,
    float pressure);
void xengine_player_key(xengine_player* player, int32_t harmonyos_key_code, int32_t pressed);
void xengine_player_mouse(
    xengine_player* player,
    xengine_player_mouse_action action,
    int32_t button,
    float x,
    float y);
void xengine_player_mouse_wheel(xengine_player* player, float delta);
void xengine_player_focus(xengine_player* player, int32_t focused);
void xengine_player_tick(xengine_player* player, int64_t frame_time_nanos);
int32_t xengine_player_text_input_active(xengine_player* player);
int32_t xengine_player_text_input_type(xengine_player* player);
// Reports the physical orientation (XENGINE_PLAYER_ORIENTATION_*) the device rotated to so the
// engine can resolve Screen.CurrentOrientation. Unknown values are ignored by the engine.
void xengine_player_set_display_orientation(xengine_player* player, int32_t orientation);
void xengine_player_destroy(xengine_player* player);

#ifdef __cplusplus
}
#endif
