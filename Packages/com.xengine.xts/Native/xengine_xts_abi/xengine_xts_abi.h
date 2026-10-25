/*
 * XEngine XTS native ABI — stable C boundary between the engine (C# via
 * P/Invoke) and the embedded QuickJS engine.
 *
 * N0 scope: environment create/destroy, module source registry + ESM eval,
 * host callbacks, promise job pumping, memory/execution limits, exception
 * reporting, memory stats. Object-level value conversion arrives with the N2
 * managed runtime on top of this ABI; the ABI itself is versioned so that
 * boundary can evolve without breaking hosts.
 *
 * Threading: every xts_env must be created, used, and destroyed on one and
 * the same thread (the engine main thread). The ABI performs no locking.
 *
 * All strings crossing this ABI are UTF-8. String payloads handed to an
 * xts_callback_fn stay valid only for the duration of that callback.
 */
#ifndef XENGINE_XTS_ABI_H
#define XENGINE_XTS_ABI_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define XTS_ABI_VERSION 1

typedef struct xts_env xts_env;

enum xts_status {
    XTS_OK = 0,
    /* JS parse / link failure (syntax error, unresolved import). */
    XTS_ERR_COMPILE = 1,
    /* A JS exception escaped to the boundary (includes host callback throws). */
    XTS_ERR_EXCEPTION = 2,
    /* Resource limit hit: execution deadline, memory limit, or interrupt. */
    XTS_ERR_LIMIT = 3,
    XTS_ERR_BAD_HANDLE = 4,
    XTS_ERR_BAD_ARG = 5,
};

typedef struct xts_options {
    /* sizeof(xts_options); lets the ABI grow without breaking old hosts. */
    uint32_t struct_size;
    uint32_t abi_version;
    /* 0 = QuickJS default. Limit covers JS heap only, not host allocations. */
    uint64_t memory_limit_bytes;
    /* 0 = QuickJS default. */
    uint64_t max_stack_bytes;
} xts_options;

typedef enum xts_value_kind {
    XTS_VALUE_UNDEF = 0,
    XTS_VALUE_BOOL = 1,
    XTS_VALUE_INT32 = 2,
    XTS_VALUE_FLOAT64 = 3,
    XTS_VALUE_STRING = 4,
    XTS_VALUE_NULL = 5,
    /* JS BigInt crossing as int64; conversion is lossy above 64 bits. */
    XTS_VALUE_BIGINT = 6,
} xts_value_kind;

typedef struct xts_value {
    xts_value_kind kind;
    union {
        int32_t bool_value;
        int32_t int32_value;
        double float64_value;
        struct {
            const char *data; /* UTF-8, NUL-terminated for convenience */
            uint32_t length;  /* bytes excluding the NUL */
        } string_value;
        int64_t bigint_value;
    } u;
} xts_value;

#define XTS_MAX_CALLBACK_ARGS 8

/* Host function invoked from JS. `result` arrives pre-set to XTS_VALUE_UNDEF;
 * the host may overwrite it (strings are copied back by the ABI before the
 * host buffer would go stale, so the buffer only needs to outlive the call). */
typedef void (*xts_callback_fn)(void *user_data, int32_t arg_count,
                                const xts_value *args, xts_value *result);

#if defined(_WIN32)
  #if defined(XTS_BUILDING)
    #define XTS_API __declspec(dllexport)
  #else
    #define XTS_API __declspec(dllimport)
  #endif
#else
  #define XTS_API __attribute__((visibility("default")))
#endif

XTS_API int32_t xts_abi_version(void);

/* options may be NULL (all defaults). Returns NULL on bad options/handle. */
XTS_API xts_env *xts_env_create(const xts_options *options);
XTS_API void xts_env_destroy(xts_env *env);

/* Register a module's ESM source under `name` (exact key, e.g. "main.js",
 * "lib.js"). Import specifiers are normalized by QuickJS before lookup, so
 * register the resolved name (e.g. "./lib.js" from "main.js" -> "lib.js").
 * Sources are copied. Re-registering replaces the previous source. */
XTS_API int32_t xts_env_add_module_source(xts_env *env, const char *name,
                                          const char *source_utf8);

/* Compile + link + evaluate the named module (and its imports). */
XTS_API int32_t xts_env_eval_module(xts_env *env, const char *name);

/* Evaluate `code_utf8` as a global (non-module) script and write the
 * completion value to `out_value`. Primitive results are fully converted;
 * objects / arrays / functions surface as XTS_VALUE_UNDEF until the object
 * binding layer. STRING and NULL results point into environment-owned
 * memory that stays valid until the next call on the same environment. */
XTS_API int32_t xts_env_eval_expression(xts_env *env, const char *code_utf8,
                                        xts_value *out_value);

/* Expose a host function as a global `name(...)` callable from JS. */
XTS_API int32_t xts_env_register_callback(xts_env *env, const char *name,
                                          xts_callback_fn fn, void *user_data);

/* Run pending promise/timer jobs. Writes the number of jobs executed.
 * Execution stops on exception (XTS_ERR_EXCEPTION) or deadline. */
XTS_API int32_t xts_env_execute_pending_jobs(xts_env *env, uint32_t max_jobs,
                                             uint32_t *executed);

/* 0 = unlimited. Applies to QuickJS heap allocations. */
XTS_API int32_t xts_env_set_memory_limit(xts_env *env, uint64_t bytes);
/* 0 = unlimited. Deadline is measured from the start of the next eval or job
 * batch; an overrun raises an interrupt inside the running script. */
XTS_API int32_t xts_env_set_execution_deadline_ms(xts_env *env, uint64_t ms);

/* Bytes currently allocated on the JS heap. */
XTS_API int32_t xts_env_memory_used_bytes(xts_env *env, uint64_t *used);

/* Copy the last exception/error text (UTF-8) into `buffer`; always
 * NUL-terminated. Returns the number of bytes written excluding the NUL. */
XTS_API int32_t xts_env_get_last_exception(xts_env *env, char *buffer,
                                           uint32_t buffer_length);

#ifdef __cplusplus
}
#endif

#endif /* XENGINE_XTS_ABI_H */
