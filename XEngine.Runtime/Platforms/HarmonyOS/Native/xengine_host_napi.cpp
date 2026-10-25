// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#include "xengine_player_abi.h"

#include <ace/xcomponent/native_interface_xcomponent.h>
#include <hilog/log.h>
#include <napi/native_api.h>
#include <native_window/external_window.h>
#include <vulkan/vulkan.h>

#include <array>
#include <cmath>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <mutex>
#include <atomic>
#include <string>
#include <thread>

namespace {

constexpr std::size_t InputQueueCapacity = 64;
constexpr std::size_t LifecycleQueueCapacity = 16;

enum class LifecycleEventType : uint8_t {
    SurfaceCreated,
    SurfaceChanged,
    SurfaceDestroyed,
    SetResumed,
    FocusChanged,
    DisplayOrientationChanged,
    Destroy,
};

struct QueuedTouchEvent {
    void* window = nullptr;
    int32_t pointer_id = 0;
    xengine_player_touch_action action = XENGINE_PLAYER_TOUCH_CANCEL;
    float x = 0.0f;
    float y = 0.0f;
    float pressure = 0.0f;
};

struct QueuedLifecycleEvent {
    LifecycleEventType type = LifecycleEventType::SurfaceDestroyed;
    void* window = nullptr;
    int32_t width = 0;
    int32_t height = 0;
    bool resumed = false;
    int32_t orientation = 0;
};

struct QueuedKeyEvent {
    void* window = nullptr;
    int32_t key_code = 0;
    bool pressed = false;
};

struct QueuedMouseEvent {
    void* window = nullptr;
    xengine_player_mouse_action action = XENGINE_PLAYER_MOUSE_CANCEL;
    int32_t button = 0;
    float x = 0.0f;
    float y = 0.0f;
};

struct QueuedMouseWheelEvent {
    void* window = nullptr;
    float delta = 0.0f;
};

struct HostState {
    std::mutex mutex;
    std::condition_variable worker_signal;
    std::thread worker;
    xengine_player* player = nullptr;
    void* window = nullptr;
    std::string content_root;
    std::array<QueuedTouchEvent, InputQueueCapacity> input_queue{};
    std::array<QueuedKeyEvent, InputQueueCapacity> key_queue{};
    std::array<QueuedMouseEvent, InputQueueCapacity> mouse_queue{};
    std::array<QueuedMouseWheelEvent, InputQueueCapacity> mouse_wheel_queue{};
    std::array<QueuedLifecycleEvent, LifecycleQueueCapacity> lifecycle_queue{};
    std::size_t input_count = 0;
    std::size_t key_count = 0;
    std::size_t mouse_count = 0;
    std::size_t mouse_wheel_count = 0;
    std::size_t lifecycle_count = 0;
    bool input_overflow_logged = false;
    bool key_overflow_logged = false;
    bool mouse_overflow_logged = false;
    bool mouse_wheel_overflow_logged = false;
    bool lifecycle_overflow_logged = false;
    bool surface_attached = false;
    bool requested_resumed = false;
    bool resumed = false;
    bool frame_thread_started = false;
    bool worker_stop_requested = false;
    bool frame_pending = false;
    uint64_t pending_frame_time_nanos = 0;
    bool destroy_requested = false;
    bool text_input_active = false;
    int32_t text_input_type = 0;
    // Screen orientation bridge: the engine stores packed requests here (drained by the ArkTS
    // poller); ArkTS pushes observed physical orientations through the lifecycle queue.
    xengine_player_set_preferred_orientation_callback set_preferred_orientation = nullptr;
    std::atomic<int32_t> pending_orientation_request{-1};
    std::atomic<int32_t> pending_keep_screen_on{-1};
};

HostState g_host;
OH_NativeXComponent_Callback g_surface_callbacks{};
OH_NativeXComponent_MouseEvent_Callback g_mouse_callbacks{};
constexpr unsigned int XEngineLogDomain = 0x3500;
constexpr const char* XEngineLogTag = "XEngine";

void SignalPlayerWorkerLocked();

int32_t ClampDimension(uint64_t value)
{
    if (value == 0) {
        return 1;
    }
    constexpr auto maximum = static_cast<uint64_t>(std::numeric_limits<int32_t>::max());
    return value > maximum ? std::numeric_limits<int32_t>::max() : static_cast<int32_t>(value);
}

void PlayerLog(int32_t, const char* utf8, void*)
{
    if (utf8 != nullptr) {
        OH_LOG_Print(LOG_APP, LOG_INFO, XEngineLogDomain, XEngineLogTag, "%{public}.4096s", utf8);
    }
}

uint64_t CreateVulkanSurface(uint64_t instance_handle, void* native_window, void*)
{
    if (instance_handle == 0 || native_window == nullptr) {
        return 0;
    }

    VkSurfaceCreateInfoOHOS create_info{};
    create_info.sType = VK_STRUCTURE_TYPE_SURFACE_CREATE_INFO_OHOS;
    create_info.window = static_cast<OHNativeWindow*>(native_window);
    VkSurfaceKHR surface = VK_NULL_HANDLE;
    VkResult result = vkCreateSurfaceOHOS(
        reinterpret_cast<VkInstance>(static_cast<std::uintptr_t>(instance_handle)),
        &create_info,
        nullptr,
        &surface);
    return result == VK_SUCCESS
        ? static_cast<uint64_t>(reinterpret_cast<std::uintptr_t>(surface))
        : 0;
}

napi_value BooleanResult(napi_env env, bool value)
{
    napi_value result = nullptr;
    if (napi_get_boolean(env, value, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

napi_value Int32Result(napi_env env, int32_t value)
{
    napi_value result = nullptr;
    if (napi_create_int32(env, value, &result) != napi_ok) {
        return nullptr;
    }
    return result;
}

napi_value Throw(napi_env env, const char* message)
{
    napi_throw_error(env, nullptr, message);
    return nullptr;
}

bool ReadUtf8Argument(napi_env env, napi_callback_info info, std::string& result)
{
    size_t argument_count = 1;
    napi_value arguments[1] = {nullptr};
    if (napi_get_cb_info(env, info, &argument_count, arguments, nullptr, nullptr) != napi_ok ||
        argument_count != 1) {
        return false;
    }

    size_t length = 0;
    if (napi_get_value_string_utf8(env, arguments[0], nullptr, 0, &length) != napi_ok || length == 0) {
        return false;
    }
    result.resize(length + 1);
    size_t written = 0;
    if (napi_get_value_string_utf8(env, arguments[0], result.data(), length + 1, &written) != napi_ok ||
        written != length) {
        result.clear();
        return false;
    }
    result.resize(written);
    return true;
}

void ClearInputQueuesLocked()
{
    g_host.input_count = 0;
    g_host.input_overflow_logged = false;
    g_host.key_count = 0;
    g_host.key_overflow_logged = false;
    g_host.mouse_count = 0;
    g_host.mouse_overflow_logged = false;
    g_host.mouse_wheel_count = 0;
    g_host.mouse_wheel_overflow_logged = false;
}

void DetachSurfaceLocked()
{
    if (g_host.player != nullptr && g_host.surface_attached) {
        xengine_player_detach_surface(g_host.player);
    }
    g_host.window = nullptr;
    g_host.surface_attached = false;
    ClearInputQueuesLocked();
}

void ReportInputOverflowLocked()
{
    if (g_host.input_overflow_logged) {
        return;
    }
    g_host.input_overflow_logged = true;
    OH_LOG_Print(
        LOG_APP,
        LOG_WARN,
        XEngineLogDomain,
        XEngineLogTag,
        "XEngine input queue overflow; non-coalescible event was not delivered.");
}

bool EnqueueTouchLocked(const QueuedTouchEvent& event)
{
    if (event.action == XENGINE_PLAYER_TOUCH_MOVE) {
        for (std::size_t index = g_host.input_count; index > 0; --index) {
            QueuedTouchEvent& existing = g_host.input_queue[index - 1];
            if (existing.action == XENGINE_PLAYER_TOUCH_MOVE &&
                existing.window == event.window &&
                existing.pointer_id == event.pointer_id) {
                existing = event;
                return true;
            }
        }
    }

    if (g_host.input_count < InputQueueCapacity) {
        g_host.input_queue[g_host.input_count++] = event;
        return true;
    }

    if (event.action == XENGINE_PLAYER_TOUCH_MOVE) {
        for (std::size_t index = g_host.input_count; index > 0; --index) {
            QueuedTouchEvent& existing = g_host.input_queue[index - 1];
            if (existing.action == XENGINE_PLAYER_TOUCH_MOVE) {
                existing = event;
                return true;
            }
        }
    }

    ReportInputOverflowLocked();
    return false;
}

void ReportKeyOverflowLocked()
{
    if (g_host.key_overflow_logged) {
        return;
    }
    g_host.key_overflow_logged = true;
    OH_LOG_Print(
        LOG_APP,
        LOG_WARN,
        XEngineLogDomain,
        XEngineLogTag,
        "XEngine key queue overflow; event was not delivered.");
}

bool EnqueueKeyLocked(const QueuedKeyEvent& event)
{
    if (g_host.key_count < InputQueueCapacity) {
        g_host.key_queue[g_host.key_count++] = event;
        return true;
    }
    ReportKeyOverflowLocked();
    return false;
}

void ReportMouseOverflowLocked()
{
    if (g_host.mouse_overflow_logged) {
        return;
    }
    g_host.mouse_overflow_logged = true;
    OH_LOG_Print(
        LOG_APP,
        LOG_WARN,
        XEngineLogDomain,
        XEngineLogTag,
        "XEngine mouse queue overflow; non-coalescible event was not delivered.");
}

bool EnqueueMouseLocked(const QueuedMouseEvent& event)
{
    if (event.action == XENGINE_PLAYER_MOUSE_MOVE) {
        for (std::size_t index = g_host.mouse_count; index > 0; --index) {
            QueuedMouseEvent& existing = g_host.mouse_queue[index - 1];
            if (existing.action == XENGINE_PLAYER_MOUSE_MOVE && existing.window == event.window) {
                existing = event;
                return true;
            }
        }
    }

    if (g_host.mouse_count < InputQueueCapacity) {
        g_host.mouse_queue[g_host.mouse_count++] = event;
        return true;
    }

    if (event.action == XENGINE_PLAYER_MOUSE_MOVE) {
        for (std::size_t index = g_host.mouse_count; index > 0; --index) {
            QueuedMouseEvent& existing = g_host.mouse_queue[index - 1];
            if (existing.action == XENGINE_PLAYER_MOUSE_MOVE) {
                existing = event;
                return true;
            }
        }
    }

    ReportMouseOverflowLocked();
    return false;
}

void ReportMouseWheelOverflowLocked()
{
    if (g_host.mouse_wheel_overflow_logged) {
        return;
    }
    g_host.mouse_wheel_overflow_logged = true;
    OH_LOG_Print(
        LOG_APP,
        LOG_WARN,
        XEngineLogDomain,
        XEngineLogTag,
        "XEngine mouse wheel queue overflow; event was not delivered.");
}

bool EnqueueMouseWheelLocked(const QueuedMouseWheelEvent& event)
{
    if (g_host.mouse_wheel_count < InputQueueCapacity) {
        g_host.mouse_wheel_queue[g_host.mouse_wheel_count++] = event;
        return true;
    }

    for (std::size_t index = g_host.mouse_wheel_count; index > 0; --index) {
        QueuedMouseWheelEvent& existing = g_host.mouse_wheel_queue[index - 1];
        if (existing.window != event.window) {
            continue;
        }
        float sum = existing.delta + event.delta;
        existing.delta = std::isfinite(sum)
            ? sum
            : (event.delta < 0.0f ? -std::numeric_limits<float>::max() : std::numeric_limits<float>::max());
        return true;
    }

    ReportMouseWheelOverflowLocked();
    return false;
}

void ReportLifecycleOverflowLocked()
{
    if (g_host.lifecycle_overflow_logged) {
        return;
    }
    g_host.lifecycle_overflow_logged = true;
    OH_LOG_Print(
        LOG_APP,
        LOG_ERROR,
        XEngineLogDomain,
        XEngineLogTag,
        "XEngine lifecycle queue overflow; event was not delivered.");
}

bool EnqueueLifecycleLocked(const QueuedLifecycleEvent& event)
{
    if (event.type == LifecycleEventType::SurfaceChanged) {
        for (std::size_t index = g_host.lifecycle_count; index > 0; --index) {
            QueuedLifecycleEvent& existing = g_host.lifecycle_queue[index - 1];
            if (existing.window != event.window) {
                continue;
            }
            if (existing.type == LifecycleEventType::SurfaceChanged) {
                existing = event;
                SignalPlayerWorkerLocked();
                return true;
            }
            if (existing.type == LifecycleEventType::SurfaceCreated) {
                existing.width = event.width;
                existing.height = event.height;
                SignalPlayerWorkerLocked();
                return true;
            }
            break;
        }
    }

    if (event.type == LifecycleEventType::SetResumed) {
        for (std::size_t index = g_host.lifecycle_count; index > 0; --index) {
            QueuedLifecycleEvent& existing = g_host.lifecycle_queue[index - 1];
            if (existing.type == LifecycleEventType::SetResumed) {
                existing = event;
                SignalPlayerWorkerLocked();
                return true;
            }
        }
    }

    if (event.type == LifecycleEventType::Destroy) {
        g_host.lifecycle_count = 0;
        g_host.input_count = 0;
        g_host.input_overflow_logged = false;
        g_host.key_count = 0;
        g_host.key_overflow_logged = false;
        g_host.mouse_count = 0;
        g_host.mouse_overflow_logged = false;
        g_host.mouse_wheel_count = 0;
        g_host.mouse_wheel_overflow_logged = false;
        g_host.lifecycle_queue[g_host.lifecycle_count++] = event;
        SignalPlayerWorkerLocked();
        return true;
    }

    if (g_host.lifecycle_count < LifecycleQueueCapacity) {
        g_host.lifecycle_queue[g_host.lifecycle_count++] = event;
        SignalPlayerWorkerLocked();
        return true;
    }

    ReportLifecycleOverflowLocked();
    return false;
}

bool IsCurrentOrPendingSurfaceLocked(void* window)
{
    bool known = g_host.surface_attached && g_host.window == window;
    for (std::size_t index = 0; index < g_host.lifecycle_count; ++index) {
        const QueuedLifecycleEvent& event = g_host.lifecycle_queue[index];
        if (event.window != window) {
            continue;
        }
        if (event.type == LifecycleEventType::SurfaceCreated) {
            known = true;
        } else if (event.type == LifecycleEventType::SurfaceDestroyed) {
            known = false;
        }
    }
    return known;
}

void DrainTouchEventsLocked()
{
    xengine_player* player = g_host.player;
    if (player == nullptr || !g_host.surface_attached) {
        g_host.input_count = 0;
        g_host.input_overflow_logged = false;
        return;
    }

    for (std::size_t index = 0; index < g_host.input_count; ++index) {
        const QueuedTouchEvent& event = g_host.input_queue[index];
        if (g_host.window == event.window) {
            xengine_player_touch(player, event.pointer_id, event.action, event.x, event.y, event.pressure);
        }
    }
    g_host.input_count = 0;
    g_host.input_overflow_logged = false;
}

void DrainKeyEventsLocked()
{
    xengine_player* player = g_host.player;
    if (player == nullptr || !g_host.surface_attached) {
        g_host.key_count = 0;
        g_host.key_overflow_logged = false;
        return;
    }

    for (std::size_t index = 0; index < g_host.key_count; ++index) {
        const QueuedKeyEvent& event = g_host.key_queue[index];
        if (g_host.window == event.window) {
            xengine_player_key(player, event.key_code, event.pressed ? 1 : 0);
        }
    }
    g_host.key_count = 0;
    g_host.key_overflow_logged = false;
}

void DrainMouseEventsLocked()
{
    xengine_player* player = g_host.player;
    if (player == nullptr || !g_host.surface_attached) {
        g_host.mouse_count = 0;
        g_host.mouse_overflow_logged = false;
        return;
    }

    for (std::size_t index = 0; index < g_host.mouse_count; ++index) {
        const QueuedMouseEvent& event = g_host.mouse_queue[index];
        if (g_host.window == event.window) {
            xengine_player_mouse(player, event.action, event.button, event.x, event.y);
        }
    }
    g_host.mouse_count = 0;
    g_host.mouse_overflow_logged = false;
}

void DrainMouseWheelEventsLocked()
{
    xengine_player* player = g_host.player;
    if (player == nullptr || !g_host.surface_attached) {
        g_host.mouse_wheel_count = 0;
        g_host.mouse_wheel_overflow_logged = false;
        return;
    }

    for (std::size_t index = 0; index < g_host.mouse_wheel_count; ++index) {
        const QueuedMouseWheelEvent& event = g_host.mouse_wheel_queue[index];
        if (g_host.window == event.window) {
            xengine_player_mouse_wheel(player, event.delta);
        }
    }
    g_host.mouse_wheel_count = 0;
    g_host.mouse_wheel_overflow_logged = false;
}

void DestroyPlayerLocked()
{
    DetachSurfaceLocked();
    if (g_host.player != nullptr) {
        xengine_player_set_resumed(g_host.player, 0);
        xengine_player_destroy(g_host.player);
        g_host.player = nullptr;
    }
    g_host.content_root.clear();
    g_host.lifecycle_count = 0;
    g_host.lifecycle_overflow_logged = false;
    g_host.requested_resumed = false;
    g_host.resumed = false;
    g_host.text_input_active = false;
    g_host.text_input_type = 0;
    g_host.pending_orientation_request = -1;
    g_host.pending_keep_screen_on = 0;
}

// Settings can be requested both during creation (host mutex held) and during unlocked ticks.
// Independent atomic mailboxes avoid recursively acquiring the host mutex from managed callbacks.
int32_t SetPreferredOrientation(int32_t packed_request, void*)
{
    g_host.pending_orientation_request.store(packed_request);
    return 0;
}

int32_t SetKeepScreenOn(int32_t enabled, void*)
{
    g_host.pending_keep_screen_on.store(enabled != 0 ? 1 : 0);
    return 0;
}

// Returns 1 when the surface was attached, 0 when attach failed (any previous surface was
// detached first). Runs WITHOUT g_host.mutex held: attach performs the engine's full
// first-frame initialization (device + scene load — seconds on device). The ArkTS main
// thread's per-vsync OnFrame callback needs that mutex, so holding it here stalls vSyncTask
// past the system THREAD_BLOCK_6S watchdog, which kills the process. The caller re-takes
// the lock and commits g_host.window/surface_attached from the returned outcome.
int32_t AttachSurfaceOffMutex(const QueuedLifecycleEvent& event, xengine_player* player, bool wasAttached)
{
    if (wasAttached) {
        xengine_player_detach_surface(player);
    }
    int32_t result = xengine_player_attach_surface(player, event.window, event.width, event.height);
    if (result != 0) {
        return 0;
    }
    OH_LOG_Print(
        LOG_APP,
        LOG_INFO,
        XEngineLogDomain,
        XEngineLogTag,
        "XENGINE_HARMONYOS_READY abi=%{public}u",
        xengine_player_abi_version());
    return 1;
}

void DestroyQueuedPlayerLocked()
{
    DestroyPlayerLocked();
    g_host.frame_pending = false;
    g_host.destroy_requested = false;
}

void DrainLifecycleEventsLocked(std::unique_lock<std::mutex>& guard)
{
    while (g_host.lifecycle_count > 0) {
        QueuedLifecycleEvent event = g_host.lifecycle_queue[0];
        for (std::size_t index = 1; index < g_host.lifecycle_count; ++index) {
            g_host.lifecycle_queue[index - 1] = g_host.lifecycle_queue[index];
        }
        --g_host.lifecycle_count;

        switch (event.type) {
            case LifecycleEventType::SurfaceCreated: {
                xengine_player* player = g_host.player;
                if (player == nullptr || event.window == nullptr) {
                    break;
                }
                bool wasAttached = g_host.surface_attached;
                // Engine surface (re)attach runs the multi-second first-frame init; keep the
                // host mutex free so the main thread's OnFrame/input callbacks stay live.
                guard.unlock();
                int32_t attached = AttachSurfaceOffMutex(event, player, wasAttached);
                guard.lock();
                if (attached != 0) {
                    g_host.window = event.window;
                    g_host.surface_attached = true;
                } else {
                    g_host.window = nullptr;
                    g_host.surface_attached = false;
                    ClearInputQueuesLocked();
                }
                break;
            }
            case LifecycleEventType::SurfaceChanged: {
                xengine_player* player = g_host.player;
                if (player == nullptr || !g_host.surface_attached || g_host.window != event.window) {
                    break;
                }
                guard.unlock();
                xengine_player_resize_surface(player, event.width, event.height);
                guard.lock();
                break;
            }
            case LifecycleEventType::SurfaceDestroyed: {
                xengine_player* player = g_host.player;
                if (!g_host.surface_attached || (g_host.window != event.window && event.window != nullptr)) {
                    break;
                }
                guard.unlock();
                if (player != nullptr) {
                    xengine_player_detach_surface(player);
                }
                guard.lock();
                g_host.window = nullptr;
                g_host.surface_attached = false;
                ClearInputQueuesLocked();
                break;
            }
            case LifecycleEventType::SetResumed:
                g_host.resumed = event.resumed;
                if (g_host.player != nullptr) {
                    xengine_player_set_resumed(g_host.player, event.resumed ? 1 : 0);
                }
                break;
            case LifecycleEventType::DisplayOrientationChanged:
                if (g_host.player != nullptr) {
                    xengine_player_set_display_orientation(g_host.player, event.orientation);
                }
                break;
            case LifecycleEventType::FocusChanged:
                if (g_host.player != nullptr) {
                    xengine_player_focus(g_host.player, event.resumed ? 1 : 0);
                }
                break;
            case LifecycleEventType::Destroy:
                DestroyQueuedPlayerLocked();
                return;
        }
    }
    g_host.lifecycle_overflow_logged = false;
}

void SignalPlayerWorkerLocked()
{
    if (g_host.frame_thread_started) {
        g_host.worker_signal.notify_one();
    }
}

void RunPlayerWorker()
{
    std::unique_lock<std::mutex> guard(g_host.mutex);
    while (true) {
        g_host.worker_signal.wait(guard, [] {
            return g_host.worker_stop_requested ||
                g_host.lifecycle_count > 0 ||
                g_host.frame_pending;
        });

        if (g_host.worker_stop_requested) {
            DestroyPlayerLocked();
            g_host.frame_pending = false;
            g_host.destroy_requested = false;
            g_host.frame_thread_started = false;
            return;
        }

        DrainLifecycleEventsLocked(guard);
        if (g_host.player == nullptr || !g_host.frame_pending) {
            continue;
        }

        int64_t frame_time_nanos = static_cast<int64_t>(g_host.pending_frame_time_nanos);
        g_host.frame_pending = false;
        if (g_host.surface_attached && g_host.resumed) {
            DrainKeyEventsLocked();
            DrainMouseEventsLocked();
            DrainMouseWheelEventsLocked();
            DrainTouchEventsLocked();

            // Release the host mutex while ticking. The first frame runs full player init
            // (scene load — tens of seconds on device); holding the lock across it stalls the
            // ArkTS main thread inside XComponent surface callbacks (they need this mutex) and
            // trips the system THREAD_BLOCK_6S watchdog, which kills the app. Main-thread
            // callbacks only append to queues/flags under the lock, safe while the worker
            // ticks; they are drained on the next iteration.
            guard.unlock();
            xengine_player_tick(g_host.player, frame_time_nanos);
            guard.lock();

            g_host.text_input_active = xengine_player_text_input_active(g_host.player) != 0;
            g_host.text_input_type = xengine_player_text_input_type(g_host.player);
        }
    }
}

void StartPlayerWorkerLocked()
{
    if (g_host.frame_thread_started) {
        return;
    }
    g_host.worker_stop_requested = false;
    g_host.frame_thread_started = true;
    g_host.worker = std::thread(RunPlayerWorker);
}

void ShutdownPlayerWorker(void*)
{
    std::unique_lock<std::mutex> guard(g_host.mutex);
    if (!g_host.frame_thread_started) {
        DestroyPlayerLocked();
        return;
    }

    g_host.worker_stop_requested = true;
    g_host.worker_signal.notify_one();
    guard.unlock();
    if (g_host.worker.joinable()) {
        g_host.worker.join();
    }
}

napi_value ConfigurePlayer(napi_env env, napi_callback_info info)
{
    std::string content_root;
    if (!ReadUtf8Argument(env, info, content_root)) {
        return Throw(env, "configurePlayer requires one non-empty UTF-8 content root.");
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.destroy_requested) {
        return Throw(env, "XEngine player shutdown is still pending.");
    }
    if (g_host.player != nullptr) {
        if (g_host.content_root != content_root) {
            return Throw(env, "The XEngine player is already configured with another content root.");
        }
        return BooleanResult(env, true);
    }

    xengine_player_create_args create_args{};
    create_args.struct_size = sizeof(create_args);
    create_args.abi_version = XENGINE_PLAYER_ABI_VERSION;
    create_args.content_root = content_root.c_str();
    create_args.content_root_length = static_cast<uint64_t>(content_root.size());
    create_args.log = PlayerLog;
    create_args.context = nullptr;
    create_args.create_vulkan_surface = CreateVulkanSurface;
    create_args.set_preferred_orientation = SetPreferredOrientation;
    create_args.set_keep_screen_on = SetKeepScreenOn;
    g_host.player = xengine_player_create(&create_args);
    if (g_host.player == nullptr) {
        return Throw(env, "xengine_player_create failed.");
    }
    g_host.content_root = std::move(content_root);
    if (g_host.requested_resumed) {
        EnqueueLifecycleLocked({LifecycleEventType::SetResumed, nullptr, 0, 0, true});
    }
    return BooleanResult(env, true);
}

napi_value SetResumed(napi_env env, napi_callback_info info)
{
    size_t argument_count = 1;
    napi_value arguments[1] = {nullptr};
    bool resumed = false;
    if (napi_get_cb_info(env, info, &argument_count, arguments, nullptr, nullptr) != napi_ok ||
        argument_count != 1 || napi_get_value_bool(env, arguments[0], &resumed) != napi_ok) {
        return Throw(env, "setResumed requires one boolean argument.");
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    g_host.requested_resumed = resumed;
    if (!resumed) {
        g_host.input_count = 0;
        g_host.input_overflow_logged = false;
        g_host.key_count = 0;
        g_host.key_overflow_logged = false;
        g_host.mouse_count = 0;
        g_host.mouse_overflow_logged = false;
        g_host.mouse_wheel_count = 0;
        g_host.mouse_wheel_overflow_logged = false;
    }
    if (g_host.player != nullptr) {
        EnqueueLifecycleLocked({LifecycleEventType::SetResumed, nullptr, 0, 0, resumed});
    }
    return BooleanResult(env, true);
}

napi_value DestroyPlayer(napi_env env, napi_callback_info)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player == nullptr) {
        return BooleanResult(env, true);
    }
    if (!g_host.frame_thread_started && !g_host.surface_attached) {
        DestroyPlayerLocked();
        return BooleanResult(env, true);
    }
    if (!g_host.destroy_requested) {
        g_host.destroy_requested = true;
        EnqueueLifecycleLocked({LifecycleEventType::Destroy, nullptr, 0, 0, false});
    }
    return BooleanResult(env, true);
}

void OnSurfaceCreated(OH_NativeXComponent* component, void* window)
{
    if (component == nullptr || window == nullptr) {
        return;
    }
    uint64_t width = 1;
    uint64_t height = 1;
    if (OH_NativeXComponent_GetXComponentSize(component, window, &width, &height) !=
        OH_NATIVEXCOMPONENT_RESULT_SUCCESS) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player == nullptr) {
        return;
    }
    EnqueueLifecycleLocked({
        LifecycleEventType::SurfaceCreated,
        window,
        ClampDimension(width),
        ClampDimension(height),
        false,
    });
}

void OnSurfaceChanged(OH_NativeXComponent* component, void* window)
{
    if (component == nullptr || window == nullptr) {
        return;
    }
    uint64_t width = 1;
    uint64_t height = 1;
    if (OH_NativeXComponent_GetXComponentSize(component, window, &width, &height) !=
        OH_NATIVEXCOMPONENT_RESULT_SUCCESS) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr) {
        EnqueueLifecycleLocked({
            LifecycleEventType::SurfaceChanged,
            window,
            ClampDimension(width),
            ClampDimension(height),
            false,
        });
    }
}

void OnSurfaceDestroyed(OH_NativeXComponent*, void* window)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr) {
        EnqueueLifecycleLocked({LifecycleEventType::SurfaceDestroyed, window, 0, 0, false});
    }
}

void DispatchTouchEvent(OH_NativeXComponent* component, void* window)
{
    if (component == nullptr || window == nullptr) {
        return;
    }

    OH_NativeXComponent_TouchEvent touch_event{};
    if (OH_NativeXComponent_GetTouchEvent(component, window, &touch_event) !=
        OH_NATIVEXCOMPONENT_RESULT_SUCCESS ||
        touch_event.type < OH_NATIVEXCOMPONENT_DOWN ||
        touch_event.type > OH_NATIVEXCOMPONENT_CANCEL ||
        !std::isfinite(touch_event.x) ||
        !std::isfinite(touch_event.y) ||
        !std::isfinite(touch_event.force)) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr && IsCurrentOrPendingSurfaceLocked(window)) {
        EnqueueTouchLocked({
            window,
            touch_event.id,
            static_cast<xengine_player_touch_action>(touch_event.type),
            touch_event.x,
            touch_event.y,
            touch_event.force,
        });
        SignalPlayerWorkerLocked();
    }
}

void DispatchKeyEvent(OH_NativeXComponent* component, void* window)
{
    if (component == nullptr || window == nullptr) {
        return;
    }

    OH_NativeXComponent_KeyEvent* key_event = nullptr;
    OH_NativeXComponent_KeyAction action = OH_NATIVEXCOMPONENT_KEY_ACTION_UNKNOWN;
    OH_NativeXComponent_KeyCode key_code = KEY_UNKNOWN;
    if (OH_NativeXComponent_GetKeyEvent(component, &key_event) != OH_NATIVEXCOMPONENT_RESULT_SUCCESS ||
        key_event == nullptr ||
        OH_NativeXComponent_GetKeyEventAction(key_event, &action) != OH_NATIVEXCOMPONENT_RESULT_SUCCESS ||
        OH_NativeXComponent_GetKeyEventCode(key_event, &key_code) != OH_NATIVEXCOMPONENT_RESULT_SUCCESS ||
        (action != OH_NATIVEXCOMPONENT_KEY_ACTION_DOWN && action != OH_NATIVEXCOMPONENT_KEY_ACTION_UP)) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr && IsCurrentOrPendingSurfaceLocked(window)) {
        EnqueueKeyLocked({
            window,
            static_cast<int32_t>(key_code),
            action == OH_NATIVEXCOMPONENT_KEY_ACTION_DOWN,
        });
        SignalPlayerWorkerLocked();
    }
}

void DispatchMouseEvent(OH_NativeXComponent* component, void* window)
{
    if (component == nullptr || window == nullptr) {
        return;
    }

    OH_NativeXComponent_MouseEvent mouse_event{};
    if (OH_NativeXComponent_GetMouseEvent(component, window, &mouse_event) !=
            OH_NATIVEXCOMPONENT_RESULT_SUCCESS ||
        mouse_event.action < OH_NATIVEXCOMPONENT_MOUSE_PRESS ||
        mouse_event.action > OH_NATIVEXCOMPONENT_MOUSE_CANCEL ||
        !std::isfinite(mouse_event.x) ||
        !std::isfinite(mouse_event.y)) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr && IsCurrentOrPendingSurfaceLocked(window)) {
        EnqueueMouseLocked({
            window,
            static_cast<xengine_player_mouse_action>(mouse_event.action),
            static_cast<int32_t>(mouse_event.button),
            mouse_event.x,
            mouse_event.y,
        });
        SignalPlayerWorkerLocked();
    }
}

void DispatchAxisEvent(
    OH_NativeXComponent*,
    ArkUI_UIInputEvent* event,
    ArkUI_UIInputEvent_Type type)
{
    if (event == nullptr || type != ARKUI_UIINPUTEVENT_TYPE_AXIS) {
        return;
    }

    double vertical_axis = OH_ArkUI_AxisEvent_GetVerticalAxisValue(event);
    if (!std::isfinite(vertical_axis) ||
        vertical_axis < -std::numeric_limits<float>::max() ||
        vertical_axis > std::numeric_limits<float>::max()) {
        return;
    }
    float delta = static_cast<float>(vertical_axis);
    if (delta == 0.0f) {
        return;
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player != nullptr && g_host.surface_attached && g_host.window != nullptr) {
        EnqueueMouseWheelLocked({g_host.window, delta});
        SignalPlayerWorkerLocked();
    }
}

void OnFocusChanged(OH_NativeXComponent*, void* window, bool focused)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    bool current_surface = g_host.player != nullptr && IsCurrentOrPendingSurfaceLocked(window);
    if (!focused && current_surface) {
        g_host.input_count = 0;
        g_host.input_overflow_logged = false;
        g_host.key_count = 0;
        g_host.key_overflow_logged = false;
        g_host.mouse_count = 0;
        g_host.mouse_overflow_logged = false;
        g_host.mouse_wheel_count = 0;
        g_host.mouse_wheel_overflow_logged = false;
    }
    if (current_surface) {
        EnqueueLifecycleLocked({LifecycleEventType::FocusChanged, window, 0, 0, focused});
    }
}

void OnFocus(OH_NativeXComponent* component, void* window)
{
    OnFocusChanged(component, window, true);
}

void OnBlur(OH_NativeXComponent* component, void* window)
{
    OnFocusChanged(component, window, false);
}

void OnFrame(OH_NativeXComponent*, uint64_t timestamp, uint64_t)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player == nullptr) {
        return;
    }
    StartPlayerWorkerLocked();
    g_host.pending_frame_time_nanos = timestamp;
    g_host.frame_pending = true;
    SignalPlayerWorkerLocked();
}

napi_value TextInputActive(napi_env env, napi_callback_info)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    return BooleanResult(env, g_host.text_input_active);
}

napi_value TextInputType(napi_env env, napi_callback_info)
{
    std::lock_guard<std::mutex> guard(g_host.mutex);
    return Int32Result(env, g_host.text_input_type);
}

// ArkTS drains the pending engine orientation request (packed per the ABI header), or -1.
napi_value TakePreferredOrientation(napi_env env, napi_callback_info)
{
    int32_t request = g_host.pending_orientation_request.exchange(-1);
    return Int32Result(env, request);
}

napi_value TakeKeepScreenOn(napi_env env, napi_callback_info)
{
    int32_t request = g_host.pending_keep_screen_on.exchange(-1);
    return Int32Result(env, request);
}

// ArkTS reports the physical orientation the device rotated to (XENGINE_PLAYER_ORIENTATION_*).
napi_value SetDisplayOrientation(napi_env env, napi_callback_info info)
{
    size_t argument_count = 1;
    napi_value arguments[1] = {nullptr};
    int32_t orientation = 0;
    if (napi_get_cb_info(env, info, &argument_count, arguments, nullptr, nullptr) != napi_ok ||
        argument_count != 1 || napi_get_value_int32(env, arguments[0], &orientation) != napi_ok) {
        return Throw(env, "setDisplayOrientation requires one integer argument.");
    }
    if (orientation < 0 || orientation > 5) {
        return Throw(env, "setDisplayOrientation received an unknown orientation code.");
    }

    std::lock_guard<std::mutex> guard(g_host.mutex);
    if (g_host.player == nullptr) {
        return BooleanResult(env, false);
    }
    EnqueueLifecycleLocked({LifecycleEventType::DisplayOrientationChanged, nullptr, 0, 0, false, orientation});
    SignalPlayerWorkerLocked();
    return BooleanResult(env, true);
}

napi_value Init(napi_env env, napi_value exports)
{
    napi_property_descriptor descriptors[] = {
        {"configurePlayer", nullptr, ConfigurePlayer, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setResumed", nullptr, SetResumed, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"destroyPlayer", nullptr, DestroyPlayer, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"textInputActive", nullptr, TextInputActive, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"textInputType", nullptr, TextInputType, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"takePreferredOrientation", nullptr, TakePreferredOrientation, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"takeKeepScreenOn", nullptr, TakeKeepScreenOn, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setDisplayOrientation", nullptr, SetDisplayOrientation, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    if (napi_define_properties(
            env,
            exports,
            sizeof(descriptors) / sizeof(descriptors[0]),
            descriptors) != napi_ok) {
        return Throw(env, "Failed to define XEngine native host properties.");
    }
    if (napi_add_env_cleanup_hook(env, ShutdownPlayerWorker, nullptr) != napi_ok) {
        return Throw(env, "Failed to register XEngine native host cleanup.");
    }

    napi_value xcomponent_value = nullptr;
    if (napi_get_named_property(env, exports, OH_NATIVE_XCOMPONENT_OBJ, &xcomponent_value) == napi_ok) {
        void* unwrapped = nullptr;
        if (napi_unwrap(env, xcomponent_value, &unwrapped) == napi_ok && unwrapped != nullptr) {
            auto* component = static_cast<OH_NativeXComponent*>(unwrapped);
            g_surface_callbacks.OnSurfaceCreated = OnSurfaceCreated;
            g_surface_callbacks.OnSurfaceChanged = OnSurfaceChanged;
            g_surface_callbacks.OnSurfaceDestroyed = OnSurfaceDestroyed;
            g_surface_callbacks.DispatchTouchEvent = DispatchTouchEvent;
            OH_NativeXComponent_RegisterCallback(component, &g_surface_callbacks);
            OH_NativeXComponent_RegisterKeyEventCallback(component, DispatchKeyEvent);
            g_mouse_callbacks.DispatchMouseEvent = DispatchMouseEvent;
            OH_NativeXComponent_RegisterMouseEventCallback(component, &g_mouse_callbacks);
            OH_NativeXComponent_RegisterUIInputEventCallback(
                component,
                DispatchAxisEvent,
                ARKUI_UIINPUTEVENT_TYPE_AXIS);
            OH_NativeXComponent_RegisterFocusEventCallback(component, OnFocus);
            OH_NativeXComponent_RegisterBlurEventCallback(component, OnBlur);
            OH_NativeXComponent_RegisterOnFrameCallback(component, OnFrame);
        }
    }
    return exports;
}

napi_module g_module = {
    1,
    0,
    nullptr,
    Init,
    "xengine_host",
    nullptr,
    {nullptr},
};

} // namespace

extern "C" __attribute__((constructor)) void RegisterXEngineHostModule()
{
    napi_module_register(&g_module);
}
