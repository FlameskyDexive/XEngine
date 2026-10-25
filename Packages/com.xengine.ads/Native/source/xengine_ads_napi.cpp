// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// NAPI module "xengine_ads" — the HarmonyOS bridge of the com.xengine.ads package.
//
// ArkTS side (glue, imported as `import xengineAds from 'libxengine_ads.so'`):
//   xengineAds.ads_abi_version(): number
//   xengineAds.ads_register_bridge(handler: (json: string) => void): void
//   xengineAds.ads_emit_event(json: string): void   — vendor SDK events → the C# player
//
// C side (P/Invoked by the NativeAOT player via [DllImport("xengine_ads")]):
//   int  xengine_ads_abi_version(void);
//   int  xengine_ads_start(xengine_ads_event_fn on_event);   // 0 ok
//   void xengine_ads_stop(void);
//   int  xengine_ads_request(const char* json_utf8);          // 0 posted
//
// Threading: xengine_ads_request may be called from the player thread; the request string is
// marshaled to the ArkTS main thread through a thread-safe function. ads_emit_event runs on
// the ArkTS main thread and invokes the C# event callback inline there — ad completions are
// allowed to resume XTask continuations on the completing thread (XEngine.Async contract);
// callers that need the game thread marshal explicitly.

#include <napi/native_api.h>

#include <atomic>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

namespace {

constexpr char kModuleLogTag[] = "xengine_ads";

std::mutex g_gate;
napi_threadsafe_function g_request_tsfn = nullptr;
void (*g_event_fn)(const char* json_utf8) = nullptr;

napi_value ThrowError(napi_env env, const char* message)
{
    napi_throw_error(env, kModuleLogTag, message);
    return nullptr;
}

// Runs on the ArkTS main thread: converts the posted UTF-8 request into a JS string and
// hands it to the registered glue handler; owns and frees the heap copy.
void CallRequestHandler(napi_env env, napi_value handler, void* context, void* data)
{
    auto* utf8 = static_cast<char*>(data);
    if (utf8 == nullptr)
        return;
    if (env != nullptr && handler != nullptr)
    {
        napi_value json = nullptr;
        if (napi_create_string_utf8(env, utf8, NAPI_AUTO_LENGTH, &json) == napi_ok)
        {
            napi_value undefined = nullptr;
            napi_get_undefined(env, &undefined);
            napi_call_function(env, undefined, handler, 1, &json, nullptr);
        }
    }
    std::free(utf8);
}

napi_value RegisterBridge(napi_env env, napi_callback_info info)
{
    std::size_t argc = 1;
    napi_value argv[1] = {nullptr};
    if (napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr) != napi_ok || argc < 1)
        return ThrowError(env, "ads_register_bridge expects one handler function.");

    napi_valuetype value_type = napi_undefined;
    if (napi_typeof(env, argv[0], &value_type) != napi_ok || value_type != napi_function)
        return ThrowError(env, "ads_register_bridge handler must be a function.");

    napi_value resource_name = nullptr;
    napi_create_string_utf8(env, "xengine_ads_request", NAPI_AUTO_LENGTH, &resource_name);

    napi_threadsafe_function tsfn = nullptr;
    if (napi_create_threadsafe_function(
            env,
            argv[0],
            nullptr,
            resource_name,
            64,
            1,
            nullptr,
            nullptr,
            nullptr,
            CallRequestHandler,
            &tsfn) != napi_ok || tsfn == nullptr)
    {
        return ThrowError(env, "Failed to create the xengine_ads request thread-safe function.");
    }

    {
        std::lock_guard<std::mutex> lock(g_gate);
        if (g_request_tsfn != nullptr)
            napi_release_threadsafe_function(g_request_tsfn, napi_tsfn_release);
        g_request_tsfn = tsfn;
    }
    return nullptr;
}

napi_value EmitEvent(napi_env env, napi_callback_info info)
{
    std::size_t argc = 1;
    napi_value argv[1] = {nullptr};
    if (napi_get_cb_info(env, info, &argc, argv, nullptr, nullptr) != napi_ok || argc < 1)
        return ThrowError(env, "ads_emit_event expects one JSON string.");

    std::size_t length = 0;
    if (napi_get_value_string_utf8(env, argv[0], nullptr, 0, &length) != napi_ok)
        return ThrowError(env, "ads_emit_event payload must be a string.");

    std::string json(length, '\0');
    if (length > 0 &&
        napi_get_value_string_utf8(env, argv[0], json.data(), length + 1, &length) != napi_ok)
    {
        return ThrowError(env, "Failed to read the ads event payload.");
    }

    // Copy the function pointer under the lock, invoke outside: the C# side never reenters
    // the module from the callback, but a concurrent xengine_ads_stop must not race the read.
    void (*event_fn)(const char*) = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_gate);
        event_fn = g_event_fn;
    }
    if (event_fn != nullptr)
        event_fn(json.c_str());
    return nullptr;
}

napi_value AbiVersion(napi_env env, napi_callback_info info)
{
    napi_value version = nullptr;
    napi_create_int32(env, 1, &version);
    return version;
}

napi_value Init(napi_env env, napi_value exports)
{
    napi_property_descriptor descriptors[] = {
        {"ads_register_bridge", nullptr, RegisterBridge, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"ads_emit_event", nullptr, EmitEvent, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"ads_abi_version", nullptr, AbiVersion, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    if (napi_define_properties(env, exports, sizeof(descriptors) / sizeof(descriptors[0]), descriptors) != napi_ok)
        return ThrowError(env, "Failed to define xengine_ads properties.");

    napi_add_env_cleanup_hook(
        env,
        [](void*) {
            std::lock_guard<std::mutex> lock(g_gate);
            if (g_request_tsfn != nullptr)
            {
                napi_release_threadsafe_function(g_request_tsfn, napi_tsfn_release);
                g_request_tsfn = nullptr;
            }
            g_event_fn = nullptr;
        },
        nullptr);
    return exports;
}

napi_module g_module = {
    1,
    0,
    nullptr,
    Init,
    "xengine_ads",
    nullptr,
    {nullptr},
};

} // namespace

extern "C" {

// ---- C ABI consumed by the NativeAOT player ([DllImport("xengine_ads")]) ----

__attribute__((visibility("default"))) int xengine_ads_abi_version(void)
{
    return 1;
}

__attribute__((visibility("default"))) int xengine_ads_start(void (*on_event)(const char* json_utf8))
{
    if (on_event == nullptr)
        return -1;
    std::lock_guard<std::mutex> lock(g_gate);
    g_event_fn = on_event;
    return 0;
}

__attribute__((visibility("default"))) void xengine_ads_stop(void)
{
    std::lock_guard<std::mutex> lock(g_gate);
    g_event_fn = nullptr;
}

__attribute__((visibility("default"))) int xengine_ads_request(const char* json_utf8)
{
    if (json_utf8 == nullptr)
        return -1;
    napi_threadsafe_function tsfn = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_gate);
        tsfn = g_request_tsfn;
    }
    if (tsfn == nullptr)
        return -2; // ArkTS glue has not registered a bridge yet.

    char* copy = strdup(json_utf8);
    if (copy == nullptr)
        return -3;
    if (napi_call_threadsafe_function(tsfn, copy, napi_tsfn_nonblocking) != napi_ok)
    {
        std::free(copy);
        return -4; // queue full or shutting down
    }
    return 0;
}

} // extern "C"

extern "C" __attribute__((constructor)) void RegisterXEngineAdsModule()
{
    napi_module_register(&g_module);
}
