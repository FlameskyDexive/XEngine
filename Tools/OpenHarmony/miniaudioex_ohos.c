/*
 * OpenHarmony audio output for miniaudioex (plan N9 ohos leg).
 *
 * miniaudio has no OHOS device backend, so on OHOS builds the ma_device is left
 * unstarted (its null-backend callback is swapped for a no-op) and this module
 * drives the exact same data path — the app-registered deviceDataProc, which
 * reads the ma_engine node graph and runs the managed filter — from an
 * OH_AudioRenderer write callback. The renderer consumes f32 frames from the
 * engine and converts to s16 for the OS stream.
 *
 * Compiled only for OHOS (__OHOS__ is defined by the OHOS clang targets).
 */

#if defined(__OHOS__)

#include <string.h>
#include <stdlib.h>
#include <dlfcn.h>
#include <stdio.h>

#include <ohaudio/native_audiostream_base.h>
#include <ohaudio/native_audiostreambuilder.h>
#include <ohaudio/native_audiorenderer.h>

#include "miniaudioex.h"

/*
 * liboh_audio.so is a device system library (not in the SDK sysroot), so every
 * entry point resolves through dlopen/dlsym on first use. The enum and struct
 * definitions still come from the SDK headers.
 */
typedef int32_t (*oh_stream_builder_create_fn)(OH_AudioStreamBuilder** builder, OH_AudioStream_Type type);
typedef int32_t (*oh_stream_builder_set_rate_fn)(OH_AudioStreamBuilder* builder, int32_t rate);
typedef int32_t (*oh_stream_builder_set_channels_fn)(OH_AudioStreamBuilder* builder, int32_t channels);
typedef int32_t (*oh_stream_builder_set_format_fn)(OH_AudioStreamBuilder* builder, OH_AudioStream_SampleFormat format);
typedef int32_t (*oh_stream_builder_set_callbacks_fn)(OH_AudioStreamBuilder* builder, OH_AudioRenderer_Callbacks callbacks, void* userData);
typedef int32_t (*oh_stream_builder_generate_fn)(OH_AudioStreamBuilder* builder, OH_AudioRenderer** renderer);
typedef int32_t (*oh_stream_builder_destroy_fn)(OH_AudioStreamBuilder* builder);
typedef int32_t (*oh_renderer_start_fn)(OH_AudioRenderer* renderer);
typedef int32_t (*oh_renderer_stop_fn)(OH_AudioRenderer* renderer);
typedef int32_t (*oh_renderer_release_fn)(OH_AudioRenderer* renderer);

static void* g_ohLib = NULL;
static oh_stream_builder_create_fn p_Create = NULL;
static oh_stream_builder_set_rate_fn p_SetRate = NULL;
static oh_stream_builder_set_channels_fn p_SetChannels = NULL;
static oh_stream_builder_set_format_fn p_SetFormat = NULL;
static oh_stream_builder_set_callbacks_fn p_SetCallbacks = NULL;
static oh_stream_builder_generate_fn p_Generate = NULL;
static oh_stream_builder_destroy_fn p_DestroyBuilder = NULL;
static oh_renderer_start_fn p_RendererStart = NULL;
static oh_renderer_stop_fn p_RendererStop = NULL;
static oh_renderer_release_fn p_RendererRelease = NULL;

/*
 * Device-side diagnostics read back by the managed side when context init fails:
 * g_diag stages: 1=generic-init-before-renderer, 69=dlopen failed (detail in buffer),
 * 70=symbol resolve, 71=builder create, 72=generate, 73=renderer start, 100=success.
 * hilog/stderr are both unreliable from this module (namespace isolation), so the
 * detail travels out through P/Invoke instead.
 */
static ma_int32 g_diag = 0;
static char g_diagDetail[320] = {0};

static void oh_set_diag(ma_int32 stage)
{
    g_diag = stage;
}

static void oh_set_diag_detail(ma_int32 stage, const char* what, const char* why)
{
    g_diag = stage;
    if (what == NULL) what = "";
    if (why == NULL) why = "";
    snprintf(g_diagDetail, sizeof(g_diagDetail), "%s: %.200s", what, why);
}

MA_API ma_int32 ma_ex_ohos_get_diag(void) { return g_diag; }
MA_API const char* ma_ex_ohos_get_diag_detail(void) { return g_diagDetail; }

static ma_bool32 oh_resolve_symbols(void)
{
    if (g_ohLib != NULL)
        return MA_TRUE;

    /* The namespace our module was loaded into may not resolve system sonames; try
     * the plain soname first, then the canonical absolute locations. */
    const char* candidates[] = { "libohaudio.so", "/system/lib64/libohaudio.so", "/system/lib/libohaudio.so" };
    for (size_t i = 0; i < sizeof(candidates) / sizeof(candidates[0]); i++) {
        g_ohLib = dlopen(candidates[i], RTLD_NOW | RTLD_LOCAL);
        if (g_ohLib != NULL)
            break;
        const char* err = dlerror();
        if (err != NULL)
            oh_set_diag_detail(69, candidates[i], err);
    }
    if (g_ohLib == NULL)
        return MA_FALSE;

#define OH_SYM(field, name) \
    do { \
        field = (typeof(field))dlsym(g_ohLib, name); \
        if (field == NULL) { dlclose(g_ohLib); g_ohLib = NULL; return MA_FALSE; } \
    } while (0)

    OH_SYM(p_Create, "OH_AudioStreamBuilder_Create");
    OH_SYM(p_SetRate, "OH_AudioStreamBuilder_SetSamplingRate");
    OH_SYM(p_SetChannels, "OH_AudioStreamBuilder_SetChannelCount");
    OH_SYM(p_SetFormat, "OH_AudioStreamBuilder_SetSampleFormat");
    OH_SYM(p_SetCallbacks, "OH_AudioStreamBuilder_SetRendererCallback");
    OH_SYM(p_Generate, "OH_AudioStreamBuilder_GenerateRenderer");
    OH_SYM(p_DestroyBuilder, "OH_AudioStreamBuilder_Destroy");
    OH_SYM(p_RendererStart, "OH_AudioRenderer_Start");
    OH_SYM(p_RendererStop, "OH_AudioRenderer_Stop");
    OH_SYM(p_RendererRelease, "OH_AudioRenderer_Release");
#undef OH_SYM
    return MA_TRUE;
}

static OH_AudioStreamBuilder* g_builder = NULL;
static OH_AudioRenderer* g_renderer = NULL;
static ma_ex_context* g_context = NULL;
static ma_device_data_proc g_dataProc = NULL;
static volatile ma_int32 g_active = 1;
static ma_uint32 g_channels = 2;
static float* g_staging = NULL;
static ma_uint32 g_stagingFrames = 0;

/*
 * Step logging straight into hilog so device-side init failures are visible:
 * dlopen libhilog_ndk.z.so lazily and fall back to stderr when unavailable.
 */
#include <stdarg.h>

typedef void (*oh_log_print_fn)(int type, unsigned int level, unsigned int domain,
    const char* tag, const char* fmt, ...);

static oh_log_print_fn g_ohLogPrint = NULL;
static ma_bool32 g_ohLogResolved = MA_FALSE;

static void oh_log(const char* message)
{
    if (!g_ohLogResolved) {
        g_ohLogResolved = MA_TRUE;
        void* hilog = dlopen("libhilog_ndk.z.so", RTLD_NOW | RTLD_LOCAL);
        if (hilog != NULL)
            g_ohLogPrint = (oh_log_print_fn)dlsym(hilog, "OH_LogPrint");
    }
    if (g_ohLogPrint != NULL)
        g_ohLogPrint(0 /* LOG_APP */, 3 /* LOG_INFO */, 0xD002100u, "miniaudioex", "%s", message);
    else
        fprintf(stderr, "[miniaudioex] %s\n", message);
}

/* Called from ma_ex_context_init (OHOS): captures the app data callback and stream shape. */
void ma_ex_ohos_attach(ma_ex_context* context, ma_device_data_proc dataProc, ma_uint32 channels)
{
    oh_set_diag(1); /* generic init, renderer not yet reached */
    g_context = context;
    g_dataProc = dataProc;
    g_channels = channels ? channels : 2;
}

/* The null-device callback installed on the ma_device: never started, never runs. */
void ma_ex_ohos_null_data_cb(ma_device* pDevice, void* pOutput, const void* pInput, ma_uint32 frameCount)
{
    (void)pDevice; (void)pOutput; (void)pInput; (void)frameCount;
}

static void oh_fill_silence(void* buffer, int32_t length)
{
    memset(buffer, 0, (size_t)length);
}

static int32_t oh_on_write_data(OH_AudioRenderer* renderer, void* userData, void* buffer, int32_t length)
{
    (void)renderer; (void)userData;

    if (g_context == NULL || g_dataProc == NULL || g_staging == NULL || length <= 0) {
        oh_fill_silence(buffer, length);
        return 0;
    }

    const ma_uint32 bytesPerFrame = g_channels * sizeof(int16_t);
    ma_uint32 totalFrames = (ma_uint32)length / bytesPerFrame;
    int16_t* out = (int16_t*)buffer;

    ma_uint32 done = 0;
    while (done < totalFrames) {
        ma_uint32 chunk = totalFrames - done;
        if (chunk > g_stagingFrames)
            chunk = g_stagingFrames;

        if (g_active) {
            /* Same path as every other backend: device callback reads the engine
             * node graph (and runs the managed filter) into our staging buffer. */
            g_dataProc(&g_context->device, g_staging, NULL, chunk);
        } else {
            memset(g_staging, 0, (size_t)chunk * g_channels * sizeof(float));
        }

        for (ma_uint32 f = 0; f < chunk; f++) {
            for (ma_uint32 c = 0; c < g_channels; c++) {
                float s = g_staging[f * g_channels + c];
                if (s > 1.0f) s = 1.0f;
                if (s < -1.0f) s = -1.0f;
                out[(done + f) * g_channels + c] = (int16_t)(s * 32767.0f);
            }
        }
        done += chunk;
    }
    return 0;
}

static int32_t oh_on_stream_event(OH_AudioRenderer* renderer, void* userData, OH_AudioStream_Event event)
{
    (void)renderer; (void)userData; (void)event;
    return 0;
}

static int32_t oh_on_interrupt_event(OH_AudioRenderer* renderer, void* userData,
    OH_AudioInterrupt_ForceType type, OH_AudioInterrupt_Hint hint)
{
    (void)renderer; (void)userData; (void)type; (void)hint;
    return 0;
}

static int32_t oh_on_error(OH_AudioRenderer* renderer, void* userData, OH_AudioStream_Result error)
{
    (void)renderer; (void)userData; (void)error;
    return 0;
}

ma_result ma_ex_ohos_renderer_start(ma_ex_context* context)
{
    if (context == NULL)
        return MA_INVALID_ARGS;
    oh_log("renderer_start: begin");
    if (!oh_resolve_symbols()) {
        oh_set_diag(70);
        return MA_INVALID_ARGS;
    }
    oh_log("renderer_start: symbols resolved");

    g_context = context;
    g_channels = context->channels ? context->channels : g_channels;
    g_stagingFrames = context->sampleRate / 100u; /* ~10 ms of frames */
    if (g_stagingFrames == 0)
        g_stagingFrames = 512;

    g_staging = (float*)malloc((size_t)g_stagingFrames * g_channels * sizeof(float));
    if (g_staging == NULL)
        return MA_OUT_OF_MEMORY;

    OH_AudioStream_Result r = p_Create(&g_builder, AUDIOSTREAM_TYPE_RENDERER);
    if (r != AUDIOSTREAM_SUCCESS) {
        oh_log("renderer_start: builder create failed");
        oh_set_diag(71);
        return MA_INVALID_ARGS;
    }
    p_SetRate(g_builder, (int32_t)context->sampleRate);
    p_SetChannels(g_builder, (int32_t)g_channels);
    p_SetFormat(g_builder, AUDIOSTREAM_SAMPLE_S16LE);
    oh_log("renderer_start: stream configured");

    OH_AudioRenderer_Callbacks callbacks;
    memset(&callbacks, 0, sizeof(callbacks));
    callbacks.OH_AudioRenderer_OnWriteData = oh_on_write_data;
    callbacks.OH_AudioRenderer_OnStreamEvent = oh_on_stream_event;
    callbacks.OH_AudioRenderer_OnInterruptEvent = oh_on_interrupt_event;
    callbacks.OH_AudioRenderer_OnError = oh_on_error;
    p_SetCallbacks(g_builder, callbacks, NULL);

    r = p_Generate(g_builder, &g_renderer);
    if (r != AUDIOSTREAM_SUCCESS) {
        oh_log("renderer_start: generate renderer failed");
        oh_set_diag(72);
        p_DestroyBuilder(g_builder);
        g_builder = NULL;
        return MA_INVALID_ARGS;
    }
    oh_log("renderer_start: renderer generated");

    if (p_RendererStart(g_renderer) != AUDIOSTREAM_SUCCESS) {
        oh_log("renderer_start: renderer start failed");
        oh_set_diag(73);
        return MA_INVALID_ARGS;
    }
    oh_set_diag(100);
    return MA_SUCCESS;
}

void ma_ex_ohos_renderer_stop(void)
{
    if (g_renderer != NULL) {
        if (p_RendererStop != NULL) p_RendererStop(g_renderer);
        if (p_RendererRelease != NULL) p_RendererRelease(g_renderer);
        g_renderer = NULL;
    }
    if (g_builder != NULL) {
        if (p_DestroyBuilder != NULL) p_DestroyBuilder(g_builder);
        g_builder = NULL;
    }
    if (g_staging != NULL) {
        free(g_staging);
        g_staging = NULL;
    }
    g_context = NULL;
    g_dataProc = NULL;
}

void ma_ex_ohos_set_playback_active(ma_bool32 active)
{
    g_active = active ? 1 : 0;
}

#endif /* __OHOS__ */
