# emdawnwebgpu (vendored local port)

Dawn release **v20260814.182433** (`56f332d7`). Upstream:
https://github.com/google/dawn/releases/tag/v20260814.182433

.NET 10 `wasm-tools` (10.0.11) bundles Emscripten **3.1.56**, which does not
register a named `--use-port=emdawnwebgpu`. The generated Web player copies this
directory to `Player/native/emdawnwebgpu_pkg/` and passes:

```
--use-port=native/emdawnwebgpu_pkg/emdawnwebgpu.port.py:cpp_bindings=false
```

plus `-sDEFAULT_TO_CXX=1` at link (the port requires C++). Douyin publishes must
not copy or link this package.

Emscripten 3.1.56 does not provide the later `$stackSave` / `$stackRestore`
JS-library helpers. The vendored `library_webgpu.js` therefore:
- lists wasm exports `stackSave` / `stackRestore` in `__deps` (not `$`-prefixed names)
- calls `_stackSave()` / `_stackRestore()` in method bodies so the JS compiler does
  not request a colliding `$stackSave` library symbol
Do not alias `$stackSave` onto the runtime export — Emscripten 3.1.56 asserts
`runtimeElements contains library symbol: $stackSave`.

Do not use Emscripten's legacy `-sUSE_WEBGPU=1` (pre-string-view ABI; WG0).
