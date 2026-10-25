/*
 * XEngine XTS native ABI implementation over QuickJS 2025-04-26.
 * QuickJS structs never appear in xengine_xts_abi.h; this file is the only
 * place that touches them. See the header for the contract.
 */
#include "xengine_xts_abi.h"

#include <stdlib.h>
#include <string.h>

#include "quickjs.h"
#include "cutils.h"

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <time.h>
#endif

/* ---------------------------------------------------------------- helpers */

static int64_t xts_now_ms(void)
{
#if defined(_WIN32)
    LARGE_INTEGER freq, counter;
    if (!QueryPerformanceFrequency(&freq) || !QueryPerformanceCounter(&counter))
        return 0;
    return (int64_t)(counter.QuadPart * 1000LL / freq.QuadPart);
#else
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0)
        return 0;
    return (int64_t)ts.tv_sec * 1000LL + ts.tv_nsec / 1000000LL;
#endif
}

static char *xts_strdup_range(const char *begin, const char *end)
{
    size_t len = (size_t)(end - begin);
    char *copy = (char *)malloc(len + 1);
    if (!copy)
        return NULL;
    memcpy(copy, begin, len);
    copy[len] = '\0';
    return copy;
}

static char *xts_strdup_cstr(const char *s)
{
    return xts_strdup_range(s, s + strlen(s));
}

/* --------------------------------------------------------------- env type */

struct module_entry {
    char *name;
    char *source;
};

struct callback_entry {
    char *name;
    xts_callback_fn fn;
    void *user_data;
};

struct xts_env {
    JSRuntime *rt;
    JSContext *ctx;

    struct module_entry *modules;
    uint32_t module_count;
    uint32_t module_capacity;

    struct callback_entry *callbacks;
    uint32_t callback_count;
    uint32_t callback_capacity;

    /* Deadline applies to one eval / job batch at a time; 0 = unlimited. */
    uint64_t deadline_ms;
    int64_t deadline_start_ms;
    uint64_t memory_limit_bytes;

    char *last_exception;
    /* Environment-owned buffer for STRING results crossing the ABI; replaced
     * by the next string-producing call on the same environment. */
    char *string_result;
    /* Set by the host promise rejection tracker when a module/promise
     * rejects with no handler — the only way module top-level failures
     * (throw/OOM/interrupt) become visible, because module bodies run as
     * async functions inside the job pump. */
    char *unhandled_rejection;
};

static void xts_set_exception_text(xts_env *env, char *owned_text)
{
    free(env->last_exception);
    env->last_exception = owned_text;
}

static JSModuleDef *xts_module_loader(JSContext *ctx, const char *module_name,
                                      void *opaque);

static void xts_rejection_tracker(JSContext *ctx, JSValueConst promise,
                                  JSValueConst reason, JS_BOOL is_handled,
                                  void *opaque);

static int32_t xts_convert_out_value(xts_env *env, JSValueConst value,
                                     xts_value *out_value);



/* ------------------------------------------------------------ env lifecycle */

static int xts_interrupt_handler(JSRuntime *rt, void *opaque)
{
    xts_env *env = (xts_env *)opaque;
    (void)rt;
    if (env->deadline_ms == 0)
        return 0;
    return (xts_now_ms() - env->deadline_start_ms) >= (int64_t)env->deadline_ms;
}

XTS_API xts_env *xts_env_create(const xts_options *options)
{
    xts_options defaults;
    xts_env *env;

    if (options != NULL) {
        if (options->struct_size != (uint32_t)sizeof(xts_options))
            return NULL;
        if (options->abi_version != XTS_ABI_VERSION)
            return NULL;
    } else {
        memset(&defaults, 0, sizeof(defaults));
        defaults.struct_size = (uint32_t)sizeof(xts_options);
        defaults.abi_version = XTS_ABI_VERSION;
        options = &defaults;
    }

    env = (xts_env *)calloc(1, sizeof(xts_env));
    if (!env)
        return NULL;

    env->rt = JS_NewRuntime();
    if (!env->rt) {
        free(env);
        return NULL;
    }
    env->ctx = JS_NewContext(env->rt);
    if (!env->ctx) {
        JS_FreeRuntime(env->rt);
        free(env);
        return NULL;
    }

    if (options->memory_limit_bytes != 0) {
        JS_SetMemoryLimit(env->rt, (size_t)options->memory_limit_bytes);
        env->memory_limit_bytes = options->memory_limit_bytes;
    }
    if (options->max_stack_bytes != 0)
        JS_SetMaxStackSize(env->rt, (size_t)options->max_stack_bytes);

    JS_SetInterruptHandler(env->rt, xts_interrupt_handler, env);
    /* The module loader is installed once here; it resolves imports from the
     * per-env source registry (xts_module_loader below). */
    JS_SetModuleLoaderFunc(env->rt, NULL, xts_module_loader, env);
    JS_SetHostPromiseRejectionTracker(env->rt, xts_rejection_tracker, env);
    JS_SetContextOpaque(env->ctx, env);
    return env;
}

XTS_API void xts_env_destroy(xts_env *env)
{
    uint32_t i;
    if (!env)
        return;
    for (i = 0; i < env->module_count; i++) {
        free(env->modules[i].name);
        free(env->modules[i].source);
    }
    free(env->modules);
    for (i = 0; i < env->callback_count; i++)
        free(env->callbacks[i].name);
    free(env->callbacks);
    free(env->last_exception);
    free(env->unhandled_rejection);
    free(env->string_result);
    if (env->ctx)
        JS_FreeContext(env->ctx);
    if (env->rt)
        JS_FreeRuntime(env->rt);
    free(env);
}

XTS_API int32_t xts_abi_version(void)
{
    return XTS_ABI_VERSION;
}

/* ----------------------------------------------------------- module registry */

static int xts_env_grow_modules(xts_env *env)
{
    uint32_t capacity = env->module_capacity ? env->module_capacity * 2 : 8;
    struct module_entry *grown =
        (struct module_entry *)realloc(env->modules, capacity * sizeof(*grown));
    if (!grown)
        return -1;
    env->modules = grown;
    env->module_capacity = capacity;
    return 0;
}

static struct module_entry *xts_env_find_module(xts_env *env, const char *name)
{
    uint32_t i;
    for (i = 0; i < env->module_count; i++) {
        if (strcmp(env->modules[i].name, name) == 0)
            return &env->modules[i];
    }
    return NULL;
}

XTS_API int32_t xts_env_add_module_source(xts_env *env, const char *name,
                                          const char *source_utf8)
{
    struct module_entry *entry;
    char *name_copy, *source_copy;

    if (!env || !env->rt || !name || !source_utf8)
        return XTS_ERR_BAD_ARG;

    name_copy = xts_strdup_cstr(name);
    source_copy = xts_strdup_cstr(source_utf8);
    if (!name_copy || !source_copy) {
        free(name_copy);
        free(source_copy);
        return XTS_ERR_BAD_ARG;
    }

    entry = xts_env_find_module(env, name);
    if (entry) {
        free(entry->name);
        free(entry->source);
        entry->name = name_copy;
        entry->source = source_copy;
        return XTS_OK;
    }
    if (env->module_count == env->module_capacity &&
        xts_env_grow_modules(env) != 0) {
        free(name_copy);
        free(source_copy);
        return XTS_ERR_BAD_ARG;
    }
    env->modules[env->module_count].name = name_copy;
    env->modules[env->module_count].source = source_copy;
    env->module_count++;
    return XTS_OK;
}

static JSModuleDef *xts_module_loader(JSContext *ctx, const char *module_name,
                                      void *opaque)
{
    xts_env *env = (xts_env *)opaque;
    struct module_entry *entry;
    JSValue func_val;
    JSModuleDef *module_def;

    if (!env) {
        JS_ThrowReferenceError(ctx, "xts: environment is gone");
        return NULL;
    }
    entry = xts_env_find_module(env, module_name);
    if (!entry) {
        JS_ThrowReferenceError(ctx, "could not load module '%s'", module_name);
        return NULL;
    }
    /* COMPILE_ONLY: the loader must return the JSModuleDef, not evaluate the
     * module — evaluation happens through the entry module's own eval. */
    func_val = JS_Eval(ctx, entry->source, strlen(entry->source), entry->name,
                       JS_EVAL_TYPE_MODULE | JS_EVAL_FLAG_COMPILE_ONLY);
    if (JS_IsException(func_val))
        return NULL;
    /* the module def keeps its own reference; drop the one we hold */
    module_def = (JSModuleDef *)JS_VALUE_GET_PTR(func_val);
    JS_FreeValue(ctx, func_val);
    return module_def;
}

/* ------------------------------------------------------------- host callbacks */

static struct callback_entry *xts_env_find_callback(xts_env *env, uint32_t magic)
{
    if (magic >= env->callback_count)
        return NULL;
    return &env->callbacks[magic];
}

static JSValue xts_callback_trampoline(JSContext *ctx, JSValueConst this_val,
                                       int argc, JSValueConst *argv, int magic)
{
    xts_env *env = (xts_env *)JS_GetContextOpaque(ctx);
    struct callback_entry *entry;
    xts_value args[XTS_MAX_CALLBACK_ARGS];
    xts_value result;
    xts_callback_fn fn;
    void *user_data;
    const char *strings[XTS_MAX_CALLBACK_ARGS] = { 0 };
    int32_t i, count;

    (void)this_val;
    if (!env) {
        return JS_ThrowInternalError(ctx, "xts: environment is gone");
    }
    entry = xts_env_find_callback(env, (uint32_t)magic);
    if (!entry)
        return JS_ThrowInternalError(ctx, "xts: unknown callback slot %d", magic);

    count = argc > XTS_MAX_CALLBACK_ARGS ? XTS_MAX_CALLBACK_ARGS : argc;
    for (i = 0; i < count; i++) {
        JSValueConst v = argv[i];
        args[i].kind = XTS_VALUE_UNDEF;
        if (JS_IsBool(v)) {
            args[i].kind = XTS_VALUE_BOOL;
            args[i].u.bool_value = JS_VALUE_GET_BOOL(v) ? 1 : 0;
        } else if (JS_VALUE_GET_TAG(v) == JS_TAG_INT) {
            args[i].kind = XTS_VALUE_INT32;
            args[i].u.int32_value = JS_VALUE_GET_INT(v);
        } else if (JS_IsNumber(v)) {
            double f = 0;
            JS_ToFloat64(ctx, &f, v);
            args[i].kind = XTS_VALUE_FLOAT64;
            args[i].u.float64_value = f;
        } else if (JS_VALUE_GET_TAG(v) == JS_TAG_BIG_INT) {
            int64_t big = 0;
            if (!JS_ToBigInt64(ctx, &big, v)) {
                args[i].kind = XTS_VALUE_BIGINT;
                args[i].u.bigint_value = big;
            }
        } else if (JS_IsString(v)) {
            size_t len = 0;
            const char *s = JS_ToCStringLen(ctx, &len, v);
            if (!s) {
                int32_t j;
                for (j = 0; j < i; j++) {
                    if (strings[j])
                        JS_FreeCString(ctx, strings[j]);
                }
                return JS_EXCEPTION;
            }
            strings[i] = s;
            args[i].kind = XTS_VALUE_STRING;
            args[i].u.string_value.data = s;
            args[i].u.string_value.length = (uint32_t)len;
        }
        /* objects / symbols / functions surface as UNDEF until N2. */
    }

    memset(&result, 0, sizeof(result));
    result.kind = XTS_VALUE_UNDEF;

    fn = entry->fn;
    user_data = entry->user_data;

    fn(user_data, count, args, &result);

    for (i = 0; i < count; i++) {
        if (strings[i])
            JS_FreeCString(ctx, strings[i]);
    }

    switch (result.kind) {
    case XTS_VALUE_BOOL:
        return JS_NewBool(ctx, result.u.bool_value != 0);
    case XTS_VALUE_INT32:
        return JS_NewInt32(ctx, result.u.int32_value);
    case XTS_VALUE_FLOAT64:
        return JS_NewFloat64(ctx, result.u.float64_value);
    case XTS_VALUE_BIGINT:
        return JS_NewBigInt64(ctx, result.u.bigint_value);
    case XTS_VALUE_STRING:
        if (result.u.string_value.data)
            return JS_NewStringLen(ctx, result.u.string_value.data,
                                   result.u.string_value.length);
        return JS_ThrowInternalError(ctx, "xts: callback returned null string");
    default:
        return JS_UNDEFINED;
    }
}

XTS_API int32_t xts_env_register_callback(xts_env *env, const char *name,
                                          xts_callback_fn fn, void *user_data)
{
    uint32_t capacity = env->callback_capacity ? env->callback_capacity * 2 : 8;
    struct callback_entry *grown;
    char *name_copy;
    JSValue global, func;

    if (!env || !env->ctx || !name || !fn)
        return XTS_ERR_BAD_ARG;

    name_copy = xts_strdup_cstr(name);
    if (!name_copy)
        return XTS_ERR_BAD_ARG;

    grown = (struct callback_entry *)realloc(env->callbacks,
                                             capacity * sizeof(*grown));
    if (!grown) {
        free(name_copy);
        return XTS_ERR_BAD_ARG;
    }
    env->callbacks = grown;
    env->callback_capacity = capacity;

    global = JS_GetGlobalObject(env->ctx);
    func = JS_NewCFunctionMagic(env->ctx, xts_callback_trampoline, name, 0,
                                JS_CFUNC_generic_magic,
                                (int)env->callback_count);
    if (JS_IsException(func) ||
        JS_SetPropertyStr(env->ctx, global, name, func) < 0) {
        if (!JS_IsException(func))
            JS_FreeValue(env->ctx, func);
        free(name_copy);
        JS_FreeValue(env->ctx, global);
        return XTS_ERR_BAD_ARG;
    }
    JS_FreeValue(env->ctx, global);

    env->callbacks[env->callback_count].name = name_copy;
    env->callbacks[env->callback_count].fn = fn;
    env->callbacks[env->callback_count].user_data = user_data;
    env->callback_count++;
    return XTS_OK;
}

/* ------------------------------------------------------------------- eval */

/* Convert a JS value into the primitive xts_value crossing the ABI. STRING
 * payloads are copied into environment-owned memory (replaced by the next
 * use), so the host never frees them. Objects surface as UNDEF for now. */
static int32_t xts_convert_out_value(xts_env *env, JSValueConst value,
                                     xts_value *out_value)
{
    size_t len = 0;

    if (!out_value)
        return XTS_ERR_BAD_ARG;
    memset(out_value, 0, sizeof(*out_value));

    switch (JS_VALUE_GET_NORM_TAG(value)) {
    case JS_TAG_BOOL:
        out_value->kind = XTS_VALUE_BOOL;
        out_value->u.bool_value = JS_VALUE_GET_BOOL(value) ? 1 : 0;
        return XTS_OK;
    case JS_TAG_INT:
        out_value->kind = XTS_VALUE_INT32;
        out_value->u.int32_value = JS_VALUE_GET_INT(value);
        return XTS_OK;
    case JS_TAG_FLOAT64:
        out_value->kind = XTS_VALUE_FLOAT64;
        out_value->u.float64_value = JS_VALUE_GET_FLOAT64(value);
        return XTS_OK;
    case JS_TAG_NULL:
        out_value->kind = XTS_VALUE_NULL;
        return XTS_OK;
    case JS_TAG_BIG_INT: {
        int64_t big = 0;
        if (JS_ToBigInt64(env->ctx, &big, value))
            return XTS_ERR_EXCEPTION;
        out_value->kind = XTS_VALUE_BIGINT;
        out_value->u.bigint_value = big;
        return XTS_OK;
    }
    case JS_TAG_STRING:
    case JS_TAG_STRING_ROPE: {
        const char *s = JS_ToCStringLen(env->ctx, &len, value);
        if (!s)
            return XTS_ERR_EXCEPTION;
        free(env->string_result);
        env->string_result = xts_strdup_range(s, s + len);
        JS_FreeCString(env->ctx, s);
        if (!env->string_result)
            return XTS_ERR_BAD_ARG;
        out_value->kind = XTS_VALUE_STRING;
        out_value->u.string_value.data = env->string_result;
        out_value->u.string_value.length = (uint32_t)len;
        return XTS_OK;
    }
    default:
        /* undefined, objects, functions, symbols, bigints (until the object
         * binding layer maps them) */
        out_value->kind = XTS_VALUE_UNDEF;
        return XTS_OK;
    }
}

static char *xts_describe_error_value(xts_env *env, JSValueConst value)
{
    JSValue name_val, message_val;
    const char *name = NULL, *message = NULL;
    char *text = NULL;

    if (JS_IsError(env->ctx, value)) {
        name_val = JS_GetPropertyStr(env->ctx, value, "name");
        message_val = JS_GetPropertyStr(env->ctx, value, "message");
        if (JS_IsString(name_val))
            name = JS_ToCString(env->ctx, name_val);
        if (JS_IsString(message_val))
            message = JS_ToCString(env->ctx, message_val);
        if (name && message) {
            size_t name_len = strlen(name), message_len = strlen(message);
            text = (char *)malloc(name_len + 2 + message_len + 1);
            if (text) {
                memcpy(text, name, name_len);
                text[name_len] = ':';
                text[name_len + 1] = ' ';
                memcpy(text + name_len + 2, message, message_len);
                text[name_len + 2 + message_len] = '\0';
            }
        } else {
            text = xts_strdup_cstr(name ? name : "non-string exception");
        }
        if (name)
            JS_FreeCString(env->ctx, name);
        if (message)
            JS_FreeCString(env->ctx, message);
        JS_FreeValue(env->ctx, name_val);
        JS_FreeValue(env->ctx, message_val);
        return text;
    }

    {
        size_t len = 0;
        const char *s = JS_ToCStringLen(env->ctx, &len, value);
        text = s ? xts_strdup_range(s, s + len)
                 : xts_strdup_cstr("non-string exception");
        if (s)
            JS_FreeCString(env->ctx, s);
        return text;
    }
}

static int xts_heap_at_limit(const xts_env *env)
{
    JSMemoryUsage stats;
    if (env->memory_limit_bytes == 0)
        return 0;
    JS_ComputeMemoryUsage(env->rt, &stats);
    return (uint64_t)stats.malloc_size + 65536 >= env->memory_limit_bytes;
}

static int32_t xts_status_from_text(const char *text)
{
    /* Substring match: under OOM the Error object may partially fail to
     * construct, so the "Name: message" shape is not guaranteed. */
    if (text && (strstr(text, "interrupted") != NULL ||
                 strstr(text, "out of memory") != NULL))
        return XTS_ERR_LIMIT;
    return XTS_ERR_EXCEPTION;
}

/* Host promise rejection tracker: module bodies run as async functions in
 * the job pump, so a top-level throw / OOM / interrupt surfaces here as an
 * unhandled rejection of the module promise instead of a pending exception. */
static void xts_rejection_tracker(JSContext *ctx, JSValueConst promise,
                                  JSValueConst reason, JS_BOOL is_handled,
                                  void *opaque)
{
    xts_env *env = (xts_env *)opaque;
    (void)ctx;
    (void)promise;
    if (is_handled || !env)
        return;
    free(env->unhandled_rejection);
    env->unhandled_rejection = xts_describe_error_value(env, reason);
}

static int32_t xts_classify_exception(xts_env *env)
{
    JSValue exc, name_val, message_val;
    const char *name = NULL, *message = NULL;
    int32_t status = XTS_ERR_EXCEPTION;

    exc = JS_GetException(env->ctx);
    if (JS_IsNull(exc) || JS_IsUndefined(exc)) {
        /* An out-of-memory abort can lose the exception object itself (the
         * error allocation fails too); the heap sitting at the host-set limit
         * is the deterministic signal. */
        if (xts_heap_at_limit(env)) {
            xts_set_exception_text(env, xts_strdup_cstr(
                "RangeError: out of memory"));
            return XTS_ERR_LIMIT;
        }
        xts_set_exception_text(env, xts_strdup_cstr("unknown JS failure"));
        return XTS_ERR_EXCEPTION;
    }

    if (JS_IsError(env->ctx, exc)) {
        name_val = JS_GetPropertyStr(env->ctx, exc, "name");
        message_val = JS_GetPropertyStr(env->ctx, exc, "message");
        if (JS_IsString(name_val))
            name = JS_ToCString(env->ctx, name_val);
        if (JS_IsString(message_val))
            message = JS_ToCString(env->ctx, message_val);

        if (name && strcmp(name, "SyntaxError") == 0)
            status = XTS_ERR_COMPILE;
        else if (name && strcmp(name, "ReferenceError") == 0 && message &&
                 strncmp(message, "could not load module", 21) == 0)
            status = XTS_ERR_COMPILE;
        else if (message && (strcmp(message, "interrupted") == 0 ||
                             strcmp(message, "out of memory") == 0))
            status = XTS_ERR_LIMIT;

        if (name && message) {
            size_t name_len = strlen(name), message_len = strlen(message);
            char *text = (char *)malloc(name_len + 2 + message_len + 1);
            if (text) {
                memcpy(text, name, name_len);
                text[name_len] = ':';
                text[name_len + 1] = ' ';
                memcpy(text + name_len + 2, message, message_len);
                text[name_len + 2 + message_len] = '\0';
            }
            xts_set_exception_text(env, text);
        } else {
            xts_set_exception_text(env, xts_strdup_cstr(
                name ? name : "non-string exception"));
        }

        if (name)
            JS_FreeCString(env->ctx, name);
        if (message)
            JS_FreeCString(env->ctx, message);
        JS_FreeValue(env->ctx, name_val);
        JS_FreeValue(env->ctx, message_val);
    } else {
        /* thrown non-Error value: stringify best-effort */
        size_t len = 0;
        const char *s = JS_ToCStringLen(env->ctx, &len, exc);
        xts_set_exception_text(env, s ? xts_strdup_range(s, s + len) :
                                        xts_strdup_cstr("non-string exception"));
        if (s)
            JS_FreeCString(env->ctx, s);
    }

    JS_FreeValue(env->ctx, exc);
    return status;
}

XTS_API int32_t xts_env_eval_module(xts_env *env, const char *name)
{
    struct module_entry *entry;
    JSValue result;
    size_t len;
    int32_t status;

    if (!env || !env->ctx || !name)
        return XTS_ERR_BAD_ARG;
    entry = xts_env_find_module(env, name);
    if (!entry) {
        xts_set_exception_text(env, xts_strdup_cstr("module not registered"));
        return XTS_ERR_COMPILE;
    }

    free(env->last_exception);
    env->last_exception = NULL;
    free(env->unhandled_rejection);
    env->unhandled_rejection = NULL;
    env->deadline_start_ms = xts_now_ms();

    len = strlen(entry->source);
    result = JS_Eval(env->ctx, entry->source, len, entry->name,
                     JS_EVAL_TYPE_MODULE);
    if (JS_IsException(result)) {
        JS_FreeValue(env->ctx, result);
        status = xts_classify_exception(env);
    } else {
        JS_FreeValue(env->ctx, result);
        status = XTS_OK;
    }
    if (env->unhandled_rejection) {
        /* the module body (async function) failed; the rejection carries the
         * real cause even when the pending exception was already consumed */
        if (status != XTS_ERR_COMPILE) {
            if (xts_heap_at_limit(env)) {
                /* the rejection text itself may be garbage when the OOM hit
                 * while building the Error object — use the canonical form */
                xts_set_exception_text(env, xts_strdup_cstr(
                    "RangeError: out of memory"));
                status = XTS_ERR_LIMIT;
            } else {
                int limit = xts_status_from_text(env->unhandled_rejection) == XTS_ERR_LIMIT;
                xts_set_exception_text(env, env->unhandled_rejection);
                env->unhandled_rejection = NULL;
                if (limit)
                    status = XTS_ERR_LIMIT;
                else if (status == XTS_OK)
                    status = XTS_ERR_EXCEPTION;
            }
        }
    }
    return status;
}

XTS_API int32_t xts_env_eval_expression(xts_env *env, const char *code_utf8,
                                        xts_value *out_value)
{
    JSValue result;
    size_t len;
    int32_t status;

    if (!env || !env->ctx || !code_utf8)
        return XTS_ERR_BAD_ARG;

    free(env->last_exception);
    env->last_exception = NULL;
    env->deadline_start_ms = xts_now_ms();

    len = strlen(code_utf8);
    result = JS_Eval(env->ctx, code_utf8, len, "<xts-expression>", JS_EVAL_TYPE_GLOBAL);
    if (JS_IsException(result)) {
        status = xts_classify_exception(env);
    } else {
        status = xts_convert_out_value(env, result, out_value);
        JS_FreeValue(env->ctx, result);
        if (status != XTS_OK)
            status = xts_classify_exception(env);
    }

    if (env->unhandled_rejection) {
        /* async work scheduled by the expression may have rejected already
         * (tracker fired); surface it like the module path does */
        if (status != XTS_ERR_COMPILE) {
            int limit = xts_heap_at_limit(env) ||
                        xts_status_from_text(env->unhandled_rejection) == XTS_ERR_LIMIT;
            xts_set_exception_text(env, env->unhandled_rejection);
            env->unhandled_rejection = NULL;
            if (limit)
                status = XTS_ERR_LIMIT;
            else if (status == XTS_OK)
                status = XTS_ERR_EXCEPTION;
        }
    }
    return status;
}

XTS_API int32_t xts_env_execute_pending_jobs(xts_env *env, uint32_t max_jobs,
                                             uint32_t *executed)
{
    JSContext *job_ctx;
    uint32_t ran = 0;
    int32_t status = XTS_OK;

    if (!env || !env->rt)
        return XTS_ERR_BAD_HANDLE;

    free(env->last_exception);
    env->last_exception = NULL;
    free(env->unhandled_rejection);
    env->unhandled_rejection = NULL;
    env->deadline_start_ms = xts_now_ms();

    while (max_jobs == 0 || ran < max_jobs) {
        int ret = JS_ExecutePendingJob(env->rt, &job_ctx);
        if (ret == 0)
            break;
        if (ret < 0) {
            status = xts_classify_exception(env);
            break;
        }
        ran++;
        if (env->unhandled_rejection) {
            /* a module body or rejected promise failed inside this job */
            xts_set_exception_text(env, env->unhandled_rejection);
            env->unhandled_rejection = NULL;
            status = xts_status_from_text(env->last_exception);
            break;
        }
    }
    if (executed)
        *executed = ran;
    return status;
}

/* ------------------------------------------------------------------ limits */

XTS_API int32_t xts_env_set_memory_limit(xts_env *env, uint64_t bytes)
{
    if (!env || !env->rt)
        return XTS_ERR_BAD_HANDLE;
    JS_SetMemoryLimit(env->rt, (size_t)bytes);
    env->memory_limit_bytes = bytes;
    return XTS_OK;
}

XTS_API int32_t xts_env_set_execution_deadline_ms(xts_env *env, uint64_t ms)
{
    if (!env || !env->rt)
        return XTS_ERR_BAD_HANDLE;
    env->deadline_ms = ms;
    return XTS_OK;
}

XTS_API int32_t xts_env_memory_used_bytes(xts_env *env, uint64_t *used)
{
    JSMemoryUsage stats;
    if (!env || !env->rt)
        return XTS_ERR_BAD_HANDLE;
    JS_ComputeMemoryUsage(env->rt, &stats);
    if (used)
        *used = (uint64_t)stats.memory_used_size;
    return XTS_OK;
}

XTS_API int32_t xts_env_get_last_exception(xts_env *env, char *buffer,
                                           uint32_t buffer_length)
{
    const char *text;
    size_t len;
    if (!env || !buffer || buffer_length == 0)
        return XTS_ERR_BAD_ARG;
    text = env->last_exception ? env->last_exception : "";
    len = strlen(text);
    if (len >= buffer_length)
        len = buffer_length - 1;
    memcpy(buffer, text, len);
    buffer[len] = '\0';
    return (int32_t)len;
}
