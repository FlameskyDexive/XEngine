# This file is part of the XEngine Game Engine
# Licensed under the MIT License. See the LICENSE file in the project root for details.

cmake_minimum_required(VERSION 3.16)
project(xengine_harmonyos_host LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT DEFINED XENGINE_PLAYER_LIBRARY OR XENGINE_PLAYER_LIBRARY STREQUAL "")
    set(XENGINE_PLAYER_LIBRARY "${CMAKE_CURRENT_SOURCE_DIR}/libxengine_player.so")
endif()
if(NOT EXISTS "${XENGINE_PLAYER_LIBRARY}")
    message(FATAL_ERROR "XENGINE_PLAYER_LIBRARY does not exist: ${XENGINE_PLAYER_LIBRARY}")
endif()

# Resolve the player library directory + name. Link by NAME (-lxengine_player) with an explicit
# -L search directory rather than via an IMPORTED target with an absolute IMPORTED_LOCATION: the
# latter causes the linker to record the absolute source-tree path as a DT_NEEDED entry on
# libxengine_host.so, which the HAP verifier (and the device loader) reject. Linking by name
# records only "libxengine_player.so", matching the .so that ships beside the host in libs/<abi>/.
get_filename_component(XENGINE_PLAYER_LIBRARY_DIR "${XENGINE_PLAYER_LIBRARY}" DIRECTORY)

# The NativeAOT player has UNDEF NetSecurityNative_* symbols but no NEEDED entry for
# libSystem.Net.Security.Native.so (Crosscompile.targets strips the .a at link time). Link
# the stub explicitly so the host's NEEDED pulls it in at load time and the symbols resolve.
if(EXISTS "${XENGINE_PLAYER_LIBRARY_DIR}/libSystem.Net.Security.Native.so")
    set(XENGINE_GSS_STUB "${XENGINE_PLAYER_LIBRARY_DIR}/libSystem.Net.Security.Native.so")
elseif(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/libSystem.Net.Security.Native.so")
    set(XENGINE_GSS_STUB "${CMAKE_CURRENT_SOURCE_DIR}/libSystem.Net.Security.Native.so")
endif()

add_library(xengine_host SHARED
    xengine_host_napi.cpp)

find_package(Threads REQUIRED)

target_include_directories(xengine_host PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}")

target_compile_options(xengine_host PRIVATE
    -fvisibility=hidden
    -fno-exceptions
    -fno-rtti)

target_compile_definitions(xengine_host PRIVATE
    VK_USE_PLATFORM_OHOS=1)

target_link_directories(xengine_host PRIVATE
    "${XENGINE_PLAYER_LIBRARY_DIR}")

target_link_libraries(xengine_host PRIVATE
    xengine_player
    Threads::Threads
    libace_napi.z.so
    libace_ndk.z.so
    libhilog_ndk.z.so
    libnative_window.so
    vulkan)

# Link the GSS stub after the player so the host's DT_NEEDED pulls it in at dlopen time.
if(DEFINED XENGINE_GSS_STUB)
    target_link_libraries(xengine_host PRIVATE ${XENGINE_GSS_STUB})
endif()
