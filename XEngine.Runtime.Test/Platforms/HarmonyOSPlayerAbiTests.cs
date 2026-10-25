// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class HarmonyOSPlayerAbiTests
{
    [Fact]
    public void AbiHeader_ExportsVersionedLifecycleContract()
    {
        string header = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_player_abi.h"));

        Assert.Contains("#pragma once", header, StringComparison.Ordinal);
        Assert.Contains("#define XENGINE_PLAYER_ABI_VERSION 8u", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct xengine_player xengine_player;", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct xengine_player_create_args", header, StringComparison.Ordinal);
        Assert.Contains("uint32_t struct_size;", header, StringComparison.Ordinal);
        Assert.Contains("uint32_t abi_version;", header, StringComparison.Ordinal);
        Assert.Contains("const char* content_root;", header, StringComparison.Ordinal);
        Assert.Contains("uint64_t content_root_length;", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_create_vulkan_surface_callback create_vulkan_surface;", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_abi_version", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_create", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_attach_surface", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_resize_surface", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_detach_surface", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_set_resumed", header, StringComparison.Ordinal);
        Assert.Contains("typedef int32_t xengine_player_touch_action;", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_touch", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_key", header, StringComparison.Ordinal);
        Assert.Contains("typedef int32_t xengine_player_mouse_action;", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_mouse", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_mouse_wheel", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_focus", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_tick", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_text_input_active", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_text_input_type", header, StringComparison.Ordinal);
        Assert.Contains("xengine_player_destroy", header, StringComparison.Ordinal);
        Assert.Contains("extern \"C\"", header, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_RegistersXComponentLifecycleAndFrameCallbacks()
    {
        string nativeRoot = Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native");
        string source = File.ReadAllText(Path.Combine(nativeRoot, "xengine_host_napi.cpp"));
        string cmake = File.ReadAllText(Path.Combine(nativeRoot, "CMakeLists.txt"));

        Assert.Contains("OH_NATIVE_XCOMPONENT_OBJ", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_RegisterCallback", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_RegisterOnFrameCallback", source, StringComparison.Ordinal);
        Assert.Contains("OnSurfaceCreated", source, StringComparison.Ordinal);
        Assert.Contains("OnSurfaceChanged", source, StringComparison.Ordinal);
        Assert.Contains("OnSurfaceDestroyed", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_create", source, StringComparison.Ordinal);
        Assert.Contains("vkCreateSurfaceOHOS", source, StringComparison.Ordinal);
        Assert.Contains("create_args.struct_size", source, StringComparison.Ordinal);
        Assert.Contains("create_args.content_root_length", source, StringComparison.Ordinal);
        Assert.Contains("create_args.create_vulkan_surface", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_attach_surface", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_resize_surface", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_detach_surface", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_set_resumed", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_RegisterKeyEventCallback", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_GetKeyEvent", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_key", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_RegisterMouseEventCallback", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_GetMouseEvent", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_mouse", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_RegisterUIInputEventCallback", source, StringComparison.Ordinal);
        Assert.Contains("OH_ArkUI_AxisEvent_GetVerticalAxisValue", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_mouse_wheel", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_focus", source, StringComparison.Ordinal);
        Assert.Contains("OH_NativeXComponent_GetTouchEvent", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_touch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("input integration wave", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_tick", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_text_input_active(g_host.player)", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_text_input_type(g_host.player)", source, StringComparison.Ordinal);
        Assert.Contains("textInputActive", source, StringComparison.Ordinal);
        Assert.Contains("textInputType", source, StringComparison.Ordinal);
        Assert.Contains("xengine_player_destroy", source, StringComparison.Ordinal);
        Assert.Contains("libace_ndk.z.so", cmake, StringComparison.Ordinal);
        Assert.Contains("libnative_window.so", cmake, StringComparison.Ordinal);
        Assert.Contains("vulkan", cmake, StringComparison.Ordinal);
        Assert.Contains("xengine_player", cmake, StringComparison.Ordinal);
        Assert.Contains("CMAKE_CURRENT_SOURCE_DIR}/libxengine_player.so", cmake, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\Engine\\OHOS", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("D:\\Engine\\OHOS", cmake, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeHost_BuffersTouchEventsUntilTheFrameCallback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("InputQueueCapacity", source, StringComparison.Ordinal);
        Assert.Contains("std::array", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueTouchLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainTouchEventsLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainTouchEventsLocked();", source, StringComparison.Ordinal);
        string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("if (!resumed) {\n        g_host.input_count = 0;", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("xengine_player_touch(\n            g_host.player", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_BuffersMouseEventsUntilTheFrameCallback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("QueuedMouseEvent", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueMouseLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainMouseEventsLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainMouseEventsLocked();", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueMouseWheelLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainMouseWheelEventsLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainMouseWheelEventsLocked();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("xengine_player_mouse(\n            g_host.player", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_RejectsNonFinitePointerSamplesBeforeQueueing()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("!std::isfinite(touch_event.x)", source, StringComparison.Ordinal);
        Assert.Contains("!std::isfinite(touch_event.y)", source, StringComparison.Ordinal);
        Assert.Contains("!std::isfinite(touch_event.force)", source, StringComparison.Ordinal);
        Assert.Contains("!std::isfinite(mouse_event.x)", source, StringComparison.Ordinal);
        Assert.Contains("!std::isfinite(mouse_event.y)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_ProcessesQueuedEventsOnTheDedicatedPlayerWorker()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("#include <condition_variable>", source, StringComparison.Ordinal);
        Assert.Contains("#include <thread>", source, StringComparison.Ordinal);
        Assert.Contains("std::condition_variable worker_signal", source, StringComparison.Ordinal);
        Assert.Contains("std::thread worker", source, StringComparison.Ordinal);
        Assert.Contains("void RunPlayerWorker()", source, StringComparison.Ordinal);
        Assert.Contains("void StartPlayerWorkerLocked()", source, StringComparison.Ordinal);
        Assert.Contains("void SignalPlayerWorkerLocked()", source, StringComparison.Ordinal);
        Assert.Contains("g_host.pending_frame_time_nanos = timestamp", source, StringComparison.Ordinal);
        Assert.Contains("g_host.worker_signal.notify_one()", source, StringComparison.Ordinal);
        Assert.Contains("napi_add_env_cleanup_hook", source, StringComparison.Ordinal);
        Assert.DoesNotContain("xengine_player_tick(g_host.player, static_cast<int64_t>(timestamp));", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_FocusLossDropsPendingInputBeforeTheNextFrame()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("void OnFocusChanged(OH_NativeXComponent*, void* window, bool focused)", source, StringComparison.Ordinal);
        int handlerStart = source.IndexOf("void OnFocusChanged", StringComparison.Ordinal);
        int handlerEnd = source.IndexOf("void OnFocus(", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerStart >= 0 && handlerEnd > handlerStart);
        string handler = source[handlerStart..handlerEnd];
        Assert.Contains("bool current_surface = g_host.player != nullptr && IsCurrentOrPendingSurfaceLocked(window);", handler, StringComparison.Ordinal);
        Assert.Contains("if (!focused && current_surface)", handler, StringComparison.Ordinal);
        Assert.Contains("g_host.input_count = 0;", handler, StringComparison.Ordinal);
        Assert.Contains("g_host.key_count = 0;", handler, StringComparison.Ordinal);
        Assert.Contains("g_host.mouse_count = 0;", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeHost_BuffersLifecycleEventsUntilTheFrameCallback()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "XEngine.Runtime",
            "Platforms",
            "HarmonyOS",
            "Native",
            "xengine_host_napi.cpp"));

        Assert.Contains("LifecycleQueueCapacity", source, StringComparison.Ordinal);
        Assert.Contains("QueuedLifecycleEvent", source, StringComparison.Ordinal);
        Assert.Contains("EnqueueLifecycleLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainLifecycleEventsLocked", source, StringComparison.Ordinal);
        Assert.Contains("DrainLifecycleEventsLocked(guard);", source, StringComparison.Ordinal);
        Assert.Contains("DestroyQueuedPlayerLocked", source, StringComparison.Ordinal);
        Assert.Contains("XEngine player shutdown is still pending.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("wait_for(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("!g_host.frame_thread_started && !g_host.surface_attached && g_host.lifecycle_count == 0", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
