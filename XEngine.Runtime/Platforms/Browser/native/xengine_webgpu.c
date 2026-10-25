/* emdawnwebgpu canvas surface helper. Linked only when the player passes
   --use-port=native/emdawnwebgpu_pkg/emdawnwebgpu.port.py (Browser / WeChat).
   Douyin must not compile this file. */

#if defined(__EMSCRIPTEN__)

#include <string.h>
#include <webgpu/webgpu.h>
#include <emscripten.h>

EMSCRIPTEN_KEEPALIVE WGPUSurface xengine_webgpu_create_canvas_surface(WGPUInstance instance, const char* selector)
{
    const char* target = (selector != NULL && selector[0] != '\0') ? selector : "#canvas";
    WGPUEmscriptenSurfaceSourceCanvasHTMLSelector source;
    memset(&source, 0, sizeof(source));
    source.chain.sType = WGPUSType_EmscriptenSurfaceSourceCanvasHTMLSelector;
    source.selector.data = target;
    source.selector.length = WGPU_STRLEN;
    WGPUSurfaceDescriptor desc;
    memset(&desc, 0, sizeof(desc));
    desc.nextInChain = (WGPUChainedStruct*)&source;
    return wgpuInstanceCreateSurface(instance, &desc);
}

#else

void* xengine_webgpu_create_canvas_surface(void* instance, const char* selector)
{
    (void)instance;
    (void)selector;
    return 0;
}

#endif
