# XEngine XTS native layer — builds the QuickJS engine plus the XTS C ABI
# wrapper into a single shared library:
#   Windows     -> xengine_xts.dll
#   HarmonyOS   -> libxengine_xts.so (ohos ndk cmake toolchain)
# QuickJS structs stay confined to Native/; the public contract is
# xengine_xts_abi/xengine_xts_abi.h.
cmake_minimum_required(VERSION 3.20)
project(xengine_xts C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(xengine_xts SHARED
    xengine_xts_abi/xengine_xts_abi.c
    quickjs/quickjs.c
    quickjs/cutils.c
    quickjs/libregexp.c
    quickjs/libunicode.c
    quickjs/dtoa.c)

target_include_directories(xengine_xts
    PRIVATE quickjs xengine_xts_abi)

if(MSVC)
    # sys/time.h + gettimeofday shim for quickjs.c (see source-manifest.json).
    target_include_directories(xengine_xts PRIVATE quickjs/msvc-compat)
    target_compile_definitions(xengine_xts PRIVATE _CRT_SECURE_NO_WARNINGS XTS_BUILDING)
    target_compile_options(xengine_xts PRIVATE /W2 /O2 /utf-8)
else()
    target_compile_definitions(xengine_xts PRIVATE XTS_BUILDING)
    target_compile_options(xengine_xts PRIVATE -O2 -fvisibility=hidden)
    target_link_libraries(xengine_xts PRIVATE m)
endif()

# Uniform JS surface on every platform: no Atomics/SharedArrayBuffer (the
# script domain is single-threaded; see source-manifest.json).
target_compile_definitions(xengine_xts PRIVATE XTS_NO_ATOMICS)
target_compile_definitions(xengine_xts PRIVATE CONFIG_VERSION="2025-04-26")

# On HarmonyOS the ohos cmake toolchain supplies the ABI/arch; the player
# builder records the produced .so SHA256 into the native dependency manifest.
set_target_properties(xengine_xts PROPERTIES
    OUTPUT_NAME "xengine_xts"
    POSITION_INDEPENDENT_CODE ON)
