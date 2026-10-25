/* Emscripten WebGL2 context + getProcAddress for Silk.NET.OpenGL on browser-wasm.
   Linked only when IsBrowserTarget compiles NativeFileReference. */

#if defined(__EMSCRIPTEN__)

#include <emscripten.h>
#include <emscripten/html5.h>
#include <emscripten/html5_webgl.h>
#include <string.h>

EMSCRIPTEN_KEEPALIVE EMSCRIPTEN_WEBGL_CONTEXT_HANDLE xengine_webgl2_create(const char* selector)
{
    EmscriptenWebGLContextAttributes attr;
    emscripten_webgl_init_context_attributes(&attr);
    attr.majorVersion = 2;
    attr.minorVersion = 0;
    attr.alpha = EM_TRUE;
    attr.depth = EM_TRUE;
    attr.stencil = EM_TRUE;
    attr.antialias = EM_FALSE;
    attr.enableExtensionsByDefault = EM_TRUE;
    attr.explicitSwapControl = EM_FALSE;
    const char* target = (selector != NULL && selector[0] != '\0') ? selector : "#canvas";
    EMSCRIPTEN_WEBGL_CONTEXT_HANDLE ctx = emscripten_webgl_create_context(target, &attr);
    if (ctx > 0)
        emscripten_webgl_make_context_current(ctx);
    return ctx;
}

EMSCRIPTEN_KEEPALIVE int xengine_webgl2_make_current(EMSCRIPTEN_WEBGL_CONTEXT_HANDLE ctx)
{
    return (int)emscripten_webgl_make_context_current(ctx);
}

EMSCRIPTEN_KEEPALIVE void* xengine_webgl2_get_proc_address(const char* name)
{
    if (name == NULL || name[0] == '\0')
        return NULL;
    return emscripten_webgl_get_proc_address(name);
}

/* Mini-game hosts have no DOM `#canvas`; Module.canvas / __xengineCanvas is the wx/tt canvas. */
EMSCRIPTEN_KEEPALIVE int xengine_webgl2_canvas_width(void)
{
    return EM_ASM_INT({
        var c = (typeof Module !== 'undefined' && Module.canvas)
            ? Module.canvas
            : (globalThis.__xengineCanvas || null);
        return (c && c.width) ? (c.width | 0) : 1;
    });
}

EMSCRIPTEN_KEEPALIVE int xengine_webgl2_canvas_height(void)
{
    return EM_ASM_INT({
        var c = (typeof Module !== 'undefined' && Module.canvas)
            ? Module.canvas
            : (globalThis.__xengineCanvas || null);
        return (c && c.height) ? (c.height | 0) : 1;
    });
}

/* Direct GLctx clear. Int channels avoid Mono-WASM float P/Invoke cookies (VFFFF
   is generated but runtime lookup still aborts aot-runtime-wasm.c:188). */
EMSCRIPTEN_KEEPALIVE void xengine_webgl2_clear_rgba8(int r, int g, int b, int a)
{
    EM_ASM({
        const ctx = (typeof GL !== 'undefined' && GL.currentContext) ? GL.currentContext : null;
        if (!ctx || !ctx.GLctx)
            return;
        const gl = ctx.GLctx;
        gl.clearColor($0 / 255.0, $1 / 255.0, $2 / 255.0, $3 / 255.0);
        gl.clear(gl.COLOR_BUFFER_BIT);
    }, r, g, b, a);
}

/* C#-started present pump. WeChat has no JSExport rAF → OnAnimationFrame, so the
   loop lives in native and uses the host canvas rAF (not JSExport). */
static int s_frame_count;
static int s_loop_started;

EMSCRIPTEN_KEEPALIVE void xengine_webgl2_on_native_frame(void)
{
    s_frame_count++;
    /* W5: do not magenta-clear. C# already presented the lit cube; wiping here
       would hide DrawElements. JS may replace this export after Module.create. */
    EM_ASM({
        if (typeof globalThis.__xengineManagedTick === 'function')
            globalThis.__xengineManagedTick();
        if ($0 == 1 || ($0 % 60) == 0)
            console.warn('[XEngine] WeChat native frame', $0);
    }, s_frame_count);
}

EMSCRIPTEN_KEEPALIVE int xengine_webgl2_frame_count(void)
{
    return s_frame_count;
}

EMSCRIPTEN_KEEPALIVE void xengine_webgl2_start_main_loop(void)
{
    if (s_loop_started)
        return;
    s_loop_started = 1;
    EM_ASM({
        if (globalThis.__xengineNativePump)
            return;
        globalThis.__xengineNativePump = true;
        function tick() {
            if (typeof Module !== 'undefined' && typeof Module._xengine_webgl2_on_native_frame === 'function')
                Module._xengine_webgl2_on_native_frame();
            var c = (typeof Module !== 'undefined' && Module.canvas)
                ? Module.canvas
                : (globalThis.__xengineCanvas || null);
            if (c && typeof c.requestAnimationFrame === 'function')
                c.requestAnimationFrame(tick);
            else if (typeof requestAnimationFrame === 'function')
                requestAnimationFrame(tick);
            else if (typeof wx !== 'undefined' && typeof wx.requestAnimationFrame === 'function')
                wx.requestAnimationFrame(tick);
            else
                setTimeout(tick, 16);
        }
        tick();
    });
}

/* Unique symbols so ManagedToNativeGenerator emits Silk.NET interp trampolines. */
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vffff(float a, float b, float c, float d)
{
    (void)a; (void)b; (void)c; (void)d;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vf(float a) { (void)a; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vd(double a) { (void)a; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vi(int a) { (void)a; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vii(int a, int b) { (void)a; (void)b; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viiii(int a, int b, int c, int d)
{
    (void)a; (void)b; (void)c; (void)d;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vfi(float a, int b) { (void)a; (void)b; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vif(int a, float b) { (void)a; (void)b; }
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viff(int a, float b, float c)
{
    (void)a; (void)b; (void)c;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_vifff(int a, float b, float c, float d)
{
    (void)a; (void)b; (void)c; (void)d;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viffff(int a, float b, float c, float d, float e)
{
    (void)a; (void)b; (void)c; (void)d; (void)e;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viii(int a, int b, int c)
{
    (void)a; (void)b; (void)c;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viiiii(int a, int b, int c, int d, int e)
{
    (void)a; (void)b; (void)c; (void)d; (void)e;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viiiiii(int a, int b, int c, int d, int e, int f)
{
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f;
}
EMSCRIPTEN_KEEPALIVE void xengine_wasm_seed_viibp(int a, int b, int c, void* d)
{
    (void)a; (void)b; (void)c; (void)d;
}

#else

#include <stddef.h>

long xengine_webgl2_create(const char* selector)
{
    (void)selector;
    return 0;
}

int xengine_webgl2_make_current(long ctx)
{
    (void)ctx;
    return -1;
}

void* xengine_webgl2_get_proc_address(const char* name)
{
    (void)name;
    return NULL;
}

void xengine_webgl2_clear_rgba8(int r, int g, int b, int a)
{
    (void)r; (void)g; (void)b; (void)a;
}

int xengine_webgl2_canvas_width(void)
{
    return 1;
}

int xengine_webgl2_canvas_height(void)
{
    return 1;
}

void xengine_webgl2_on_native_frame(void)
{
}

int xengine_webgl2_frame_count(void)
{
    return 0;
}

void xengine_webgl2_start_main_loop(void)
{
}

#endif
