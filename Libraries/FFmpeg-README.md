# FFmpeg shared libraries (per-RID native payload)

Pinned combination for the XEngine video playback module (`XEngine.Runtime/Video/`):

- **Bindings**: FFmpeg.AutoGen **8.1.0** (NuGet, P/Invoke only — ships no native code).
- **Native libraries**: FFmpeg **8.1.x** (ABI majors: avutil 60, swresample 6, swscale 9,
  avcodec 62, avformat 62, avfilter 11).
- **Windows/Linux build**: BtbN/FFmpeg-Builds `ffmpeg-n8.1-latest-{rid}-lgpl-shared-8.1`
  (LGPL 2.1, dynamic linking, GPL and nonfree components disabled).
- **Windows/Linux source**: https://github.com/BtbN/FFmpeg-Builds (release tag `latest`)
  Upstream: https://ffmpeg.org — build instructions:
  https://trac.ffmpeg.org/wiki/CompilationGuide
- **License**: LGPL 2.1 — see `FFmpeg-LICENSE.txt` in each RID's native directory. FFmpeg is a
  trademark of Fabrice Bellard (http://ffmpeg.org).

## Layout

```
Libraries/
  win-x64/native/     avcodec-62.dll avformat-62.dll avutil-60.dll
                      swresample-6.dll swscale-9.dll avfilter-11.dll
  linux-x64/native/   libavcodec.so.62 libavformat.so.62 libavutil.so.60
                      libswresample.so.6 libswscale.so.9 libavfilter.so.11
  openharmony-arm64/native/  libavcodec.so.62 libavformat.so.62 libavutil.so.60
                            libswresample.so.6 libswscale.so.9 libavfilter.so.11
  osx-arm64/native/   libavcodec.62.dylib libavformat.62.dylib libavutil.60.dylib
                      libswresample.6.dylib libswscale.9.dylib libavfilter.11.dylib
  osx-x64/native/     same six ABI-major filenames (Intel cross-build)
```

`XEngine.Runtime.csproj` copies `Libraries/**` into build output as
`runtimes/{rid}/native/`; `FFmpegNativeLoader` preloads each library with
`System.Runtime.InteropServices.NativeLibrary` (dependency order: avutil →
swresample → swscale → avcodec → avformat → avfilter) so FFmpeg.AutoGen's
imports resolve against the pinned ABI. Linux soname files carry the ABI major
only (`libavcodec.so.62`), matching the loader's constructed names.

## macOS payload

The Darwin libraries are built from official FFmpeg **8.1.3** source, rather than
copied from an installed Homebrew package:

- Source: `https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz`, SHA-256
  `7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3`.
  Its detached signature is verified with FFmpeg release-key fingerprint
  `FCF986EA15E6E293A5644F10B4322F04D67658D8`.
- Recipe: `Tools/ffmpeg/build-macos-ffmpeg.sh`, separate arm64/x64 build trees,
  Apple clang and SDK, minimum macOS **13.0**. The recipe selects the real Apple
  `nm` along with the compiler: developer-tool shims pointed at a different Xcode
  can otherwise misdetect Darwin symbol prefixes and break ARM assembly linking.
- Shared-only LGPL 2.1, GPL/nonfree and external-library autodetection disabled;
  built-in codecs/formats/filters and Apple's SecureTransport TLS backend enabled.
  The license text is included in each native directory.
- Install names/dependencies use `@rpath` with `@loader_path` in the libraries,
  without build-machine absolute dependency paths. Each staged dylib is ad-hoc
  signed. Editor/Player bundle signing remains a separate packaging step.
- Intel uses `--disable-x86asm` so cross-building does not require NASM/Homebrew.
  Producing its Mach-O payload does not establish Intel hardware/performance
  acceptance.

FFmpeg's `AVERROR(EAGAIN)` preserves the host errno: **-35 on Darwin**, -11 in the
Windows/Linux/OpenHarmony builds. The decoder's send/receive loops use the cached
host value instead of treating a normal Darwin drain result as a decode failure.

## OpenHarmony payload

`openharmony-arm64/native/` is **not** a BtbN download — it is built from source
with the official OpenHarmony NDK so the provenance chain is fully owned:

- **Source**: FFmpeg **8.1.2** release tarball
  (`https://www.ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz`,
  sha256 `464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c`).
  Same n8.1 ABI majors as the desktop payload, so FFmpeg.AutoGen 8.1.0 binds
  unchanged.
- **Toolchain**: OpenHarmony SDK API 13 (5.0.1.111) NDK
  `aarch64-unknown-linux-ohos` clang 15.0.4 — the same SDK the HarmonyOS player
  cross-compile locks via the Bundle catalog. GNU make 4.4.1 (ezwinports) drives
  the build under Git Bash; the configure host-compiler bootstrap uses the NDK
  clang against the local VS/WinSDK headers through space-free junctions. The
  full recipe is the provenance script: `Tools/ffmpeg/build-openharmony-ffmpeg.sh`.
- **Configuration**: LGPL 2.1 shared-only, `--disable-everything` plus
  h264/hevc/vp8/vp9/aac/opus/vorbis/flac/mp3 decode, mov/matroska/ogg demux,
  file/http/tcp protocols, the abuffer→atempo→aformat→abuffersink filter chain,
  pthreads, `-Wl,--build-id=sha1`. No TLS backend (OpenHarmony exposes none to
  apps) — **https URLs are not supported on HarmonyOS yet**.
- **Packaging**: `HarmonyOSBuildPipeline` stages these sonames into the Stage's
  `entry/libs/<abi>/` and the package verifier proves their exact SHA-256 inside
  the built HAP; `FFmpegNativeLoader` (`XENGINE_HARMONYOS`) resolves them from
  the app library directory the OS loader populates.
- **License**: LGPL 2.1 — `FFmpeg-LICENSE.txt` next to the sonames (COPYING.LGPLv2.1
  from the 8.1.2 source tree).

## ABI check (强制)

FFmpeg.AutoGen 8.1.0 is generated against the n8.1 headers. The generated
`AVIOContext` layout is WRONG on Windows (its `checksum` field is generated as
`CULong`, 8 bytes, while Windows C `unsigned long` is 4 bytes — every field after
it is shifted by 8). Do not read `AVIOContext` fields directly; use the
corresponding functions (e.g. `avio_seek(pb, 0, SEEK_CUR)` to probe seekability,
`avio_size` for length). Verified by byte-dump probe against the real n8.1 DLLs
(N0 evidence, `docs/superpowers/evidence/video/2026-09-12-n0-decode-baseline.txt`).

When bumping either side, re-verify: load, open the N0 test media, check
metadata + first frame + cancel/timeout behavior
(`XEngine.Runtime.Test/Video/FFmpegVideoDecoderTests.cs`).
