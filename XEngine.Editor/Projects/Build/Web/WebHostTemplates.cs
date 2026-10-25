// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Editor.Build.Web;

/// <summary>
/// JS / HTML adapters. Browser may use stock <c>dotnet.js</c> (fetch + instantiateStreaming).
/// WeChat and Douyin must instantiate by package path (<c>WXWebAssembly</c> / <c>TTWebAssembly</c>)
/// and must not fetch runtime wasm from a CDN.
/// </summary>
internal static class WebHostTemplates
{
    internal static string CreateIndexHtml(string applicationLabel) =>
        $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no" />
          <title>{{Xml(applicationLabel)}}</title>
          <style>
            html, body { margin: 0; padding: 0; background: #000; overflow: hidden; height: 100%; }
            canvas { display: block; width: 100vw; height: 100vh; }
          </style>
        </head>
        <body>
          <canvas id="canvas"></canvas>
          <script type="module" src="./main.js"></script>
        </body>
        </html>
        """;

    internal static string CreateBrowserMainJs() =>
        """
        import { dotnet } from './_framework/dotnet.js';

        const canvas = document.getElementById('canvas');
        function fail(err) {
          console.error('[XEngine]', err);
          window.__xengineBootError = err;
          let text = '';
          try {
            if (err && typeof err === 'object') {
              text = [err.name, err.message, err.stack, err.toString && err.toString()]
                .filter(Boolean)
                .join('\n');
              if (!text)
                text = JSON.stringify(err, Object.getOwnPropertyNames(err));
            }
          } catch (_) { /* ignore */ }
          if (!text)
            text = String(err);
          const pre = document.createElement('pre');
          pre.id = 'xengine-boot-error';
          pre.textContent = text;
          pre.style.cssText = 'position:fixed;inset:0;margin:0;padding:12px;color:#f88;background:#200;white-space:pre-wrap;z-index:9';
          document.body.appendChild(pre);
        }
        function syncCanvasSize() {
          const dpr = window.devicePixelRatio || 1;
          canvas.width = Math.max(1, Math.floor(canvas.clientWidth * dpr));
          canvas.height = Math.max(1, Math.floor(canvas.clientHeight * dpr));
        }
        syncCanvasSize();
        globalThis.Module = globalThis.Module || {};
        Module.canvas = canvas;

        try {
          const { getAssemblyExports, getConfig, runMain } = await dotnet
            .withDiagnosticTracing(false)
            .withConfig({ environmentVariables: { DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1' } })
            .create();

          await runMain();
          const exports = await getAssemblyExports(getConfig().mainAssemblyName);
          const player = exports.XEngine.Generated.Web.BrowserPlayer;
          player.SetAssetBase(new URL('.', import.meta.url).href);
          player.ConfigurePlatform(navigator.language || 'en', navigator.onLine !== false);
          player.InitializeAudio('webaudio');
          player.AttachCanvas(canvas.width, canvas.height, navigator.gpu ? 1 : 0);

          let audioCtx = null;
          function ensureWebAudio() {
            if (audioCtx)
              return audioCtx;
            const Ctx = window.AudioContext || window.webkitAudioContext;
            if (!Ctx)
              return null;
            audioCtx = new Ctx({ sampleRate: 44100 });
            const processor = audioCtx.createScriptProcessor(2048, 0, 2);
            const gain = audioCtx.createGain();
            gain.gain.value = player.GetMasterVolume();
            processor.onaudioprocess = (e) => {
              player.OnAudioProcess(e.outputBuffer.length);
              const out = e.outputBuffer;
              for (let c = 0; c < out.numberOfChannels; c++)
                out.getChannelData(c).fill(0);
            };
            processor.connect(gain);
            gain.connect(audioCtx.destination);
            audioCtx.__xengineGain = gain;
            return audioCtx;
          }

          function cssPos(ev) {
            const r = canvas.getBoundingClientRect();
            return [ev.clientX - r.left, ev.clientY - r.top, canvas.clientWidth || 1, canvas.clientHeight || 1];
          }
          canvas.addEventListener('pointerdown', ev => {
            canvas.setPointerCapture(ev.pointerId);
            const ctx = ensureWebAudio();
            if (ctx && ctx.resume)
              ctx.resume();
            const p = cssPos(ev);
            player.OnPointer(0, ev.pointerId, p[0], p[1], p[2], p[3], ev.buttons, ev.pressure || 1, ev.pointerType === 'touch' ? 1 : 0);
          });
          canvas.addEventListener('pointermove', ev => {
            const p = cssPos(ev);
            player.OnPointer(1, ev.pointerId, p[0], p[1], p[2], p[3], ev.buttons, ev.pressure || 1, ev.pointerType === 'touch' ? 1 : 0);
          });
          function pointerEnd(kind, ev) {
            const p = cssPos(ev);
            player.OnPointer(kind, ev.pointerId, p[0], p[1], p[2], p[3], ev.buttons, ev.pressure || 1, ev.pointerType === 'touch' ? 1 : 0);
          }
          canvas.addEventListener('pointerup', ev => pointerEnd(2, ev));
          canvas.addEventListener('pointercancel', ev => pointerEnd(3, ev));
          canvas.addEventListener('wheel', ev => {
            const p = cssPos(ev);
            player.OnWheel(p[0], p[1], p[2], p[3], ev.deltaY || 0);
          }, { passive: true });
          window.addEventListener('keydown', ev => player.OnKey(ev.code || '', 1, ev.key || ''));
          window.addEventListener('keyup', ev => player.OnKey(ev.code || '', 0, ev.key || ''));

          window.addEventListener('resize', () => {
            syncCanvasSize();
            player.OnResize(canvas.width, canvas.height);
          });
          document.addEventListener('visibilitychange', () => {
            const visible = document.visibilityState !== 'hidden';
            player.OnVisibility(visible);
            if (!audioCtx)
              return;
            if (visible)
              audioCtx.resume();
            else
              audioCtx.suspend();
          });

          function pollGamepads() {
            if (!navigator.getGamepads) return;
            const pads = navigator.getGamepads();
            for (let i = 0; i < pads.length && i < 4; i++) {
              const pad = pads[i];
              if (!pad) continue;
              let mask = 0;
              for (let b = 0; b < pad.buttons.length && b < 16; b++)
                if (pad.buttons[b].pressed) mask |= (1 << b);
              const lt = pad.buttons[6] ? pad.buttons[6].value : 0;
              const rt = pad.buttons[7] ? pad.buttons[7].value : 0;
              player.OnGamepad(i, mask, pad.axes[0] || 0, pad.axes[1] || 0, pad.axes[2] || 0, pad.axes[3] || 0, lt, rt);
            }
          }

          function loop(timeMs) {
            pollGamepads();
            player.OnAnimationFrame(timeMs);
            if (audioCtx && audioCtx.__xengineGain)
              audioCtx.__xengineGain.gain.value = player.GetMasterVolume();
            const url = player.TakePendingOpenUrl();
            if (url) window.open(url, '_blank');
            const clip = player.TakePendingClipboardWrite();
            if (clip && navigator.clipboard && navigator.clipboard.writeText)
              navigator.clipboard.writeText(clip).catch(() => {});
            const orientationLock = player.TakePendingOrientationLock();
            if (orientationLock) {
              if (screen.orientation && screen.orientation.lock)
                screen.orientation.lock(orientationLock).catch(() => {
                  // Fullscreen-only on most browsers; the engine keeps the request as state.
                });
              else
                player.NoteScreenOrientationUnsupported();
            }
            requestAnimationFrame(loop);
          }
          requestAnimationFrame(loop);
          if (screen.orientation && screen.orientation.addEventListener) {
            const reportOrientation = () => player.ReportScreenOrientation(screen.orientation.type || '');
            screen.orientation.addEventListener('change', reportOrientation);
            reportOrientation();
          }
        } catch (err) {
          fail(err);
        }
        """;

    internal static string CreateWeChatGameJson() =>
        """
        {
          "deviceOrientation": "landscape",
          "showStatusBar": false,
          "subpackages": [
            {
              "name": "dotnet",
              "root": "dotnet/"
            },
            {
              "name": "cdn",
              "root": "cdn/"
            }
          ],
          "networkTimeout": {
            "request": 10000,
            "connectSocket": 10000,
            "uploadFile": 10000,
            "downloadFile": 10000
          }
        }
        """;

    /// <summary>
    /// DevTools <c>isGameApp</c> is keyed by AppID, not <c>compileType</c>.
    /// Mini-program tourist is <c>touristappid</c> (<c>gameApp: false</c>);
    /// mini-game tourist is <c>wx6ac3f5090a6b99c5</c>.
    /// </summary>
    internal const string WeChatGameTouristAppId = "wx6ac3f5090a6b99c5";

    internal static string CreateWeChatProjectConfig(string applicationLabel) =>
        $$"""
        {
          "description": "XEngine WeChat mini-game",
          "packOptions": { "ignore": [] },
          "setting": {
            "urlCheck": false,
            "es6": false,
            "enhance": false,
            "postcss": false,
            "minified": false
          },
          "compileType": "game",
          "libVersion": "widelyUsed",
          "appid": "{{WeChatGameTouristAppId}}",
          "projectname": {{System.Text.Json.JsonSerializer.Serialize(applicationLabel)}},
          "isGameTourist": true,
          "condition": {}
        }
        """;

    /// <summary>
    /// WeChat requires <c>game.js</c> in every subpackage root. Boot stays in the main package.
    /// </summary>
    internal static string CreateWeChatSubpackageGameJs() =>
        """
        // WeChat requires game.js in every subpackage root. Boot stays in the main package.
        module.exports = {};
        """;

    internal static string CreateWeChatGameJs() =>
        """
        // WeChat mini-game adapter. Runtime wasm MUST be instantiated from a code-package
        // path (WXWebAssembly.instantiate). Do not copy the browser fetch/ESM boot.
        // Module.canvas is the wx canvas used by emscripten.
        // GPU shim: call getGPU only after a typeof function check. No 内测 → no GPU.
        console.warn('[XEngine] WeChat game.js start');
        const canvas = wx.createCanvas();
        function installGpuShim() {
          globalThis.__xengineHasWebGpu = false;
          if (typeof wx.getGPU === 'function') {
            try {
              const gpu = wx.getGPU();
              globalThis.navigator = globalThis.navigator || {};
              if (gpu) {
                globalThis.navigator.gpu = gpu;
                globalThis.__xengineHasWebGpu = true;
              }
            } catch (e) {
              console.warn('[XEngine] wx.getGPU 内测 not granted:', e);
            }
          }
        }
        installGpuShim();
        function sizeCanvasToWindow() {
          const info = wx.getSystemInfoSync() || {};
          const dpr = info.pixelRatio || 1;
          const cssW = info.windowWidth || info.screenWidth || 1;
          const cssH = info.windowHeight || info.screenHeight || 1;
          canvas.width = Math.max(1, Math.floor(cssW * dpr));
          canvas.height = Math.max(1, Math.floor(cssH * dpr));
          console.warn('[XEngine] WeChat canvas', canvas.width, 'x', canvas.height, 'css', cssW, 'x', cssH, 'dpr', dpr);
        }
        sizeCanvasToWindow();
        globalThis.Module = globalThis.Module || {};
        Module.canvas = canvas;
        Module.onAbort = function (what) {
          console.error('[XEngine] WeChat wasm abort', what);
        };
        Module.printErr = function (text) {
          console.error('[XEngine] WeChat printErr', text);
        };
        globalThis.__xengineCanvas = canvas;
        // Leave the canvas unused so emscripten can own the WebGL context.

        const innerAudio = wx.createInnerAudioContext();
        globalThis.__xengineInnerAudio = innerAudio;
        function applyInnerAudio(cmd) {
          if (!cmd) return;
          if (cmd.indexOf('play|') === 0) {
            innerAudio.src = cmd.slice(5);
            innerAudio.play();
          } else if (cmd === 'pause') {
            innerAudio.pause();
          } else if (cmd === 'stop') {
            innerAudio.stop();
          }
        }
        globalThis.__xengineApplyInnerAudio = applyInnerAudio;

        const fs = wx.getFileSystemManager();
        function installHostShims() {
          installWebAssemblyShim();
          if (typeof globalThis.window === 'undefined')
            globalThis.window = globalThis;
          if (typeof globalThis.addEventListener !== 'function') {
            globalThis.addEventListener = function () {};
            globalThis.removeEventListener = function () {};
          }
          const canvasOf = function () {
            return (globalThis.Module && globalThis.Module.canvas) || globalThis.__xengineCanvas || null;
          };
          if (typeof globalThis.requestAnimationFrame !== 'function') {
            globalThis.requestAnimationFrame = function (cb) {
              var c = canvasOf();
              if (c && typeof c.requestAnimationFrame === 'function')
                return c.requestAnimationFrame(cb);
              return setTimeout(function () { cb(Date.now()); }, 16);
            };
          }
          if (typeof globalThis.cancelAnimationFrame !== 'function') {
            globalThis.cancelAnimationFrame = function (id) {
              var c = canvasOf();
              if (c && typeof c.cancelAnimationFrame === 'function')
                return c.cancelAnimationFrame(id);
              clearTimeout(id);
            };
          }
          globalThis.document = {
            baseURI: 'https://xengine.local/dotnet/',
            body: { appendChild: function () {} },
            createElement: function () { return { style: {}, innerHTML: '' }; },
            querySelector: function (sel) {
              if (sel === '#canvas' || sel === 'canvas')
                return canvasOf();
              return null;
            },
            getElementById: function (id) {
              if (id === 'canvas')
                return canvasOf();
              return null;
            }
          };
          if (typeof globalThis.URL !== 'function') {
            globalThis.URL = function (path, base) {
              var p = String(path || '');
              var b = String(base || 'https://xengine.local/dotnet/');
              if (/^[a-zA-Z][a-zA-Z\d+\-.]*:/.test(p))
                this.href = p;
              else {
                var dir = b.replace(/[?#].*$/, '');
                if (dir.charAt(dir.length - 1) !== '/') {
                  var slash = dir.lastIndexOf('/');
                  dir = slash >= 0 ? dir.slice(0, slash + 1) : dir + '/';
                }
                this.href = dir + p.replace(/^\/+/, '');
              }
              this.toString = function () { return this.href; };
            };
          }
          const info = wx.getSystemInfoSync() || {};
          globalThis.navigator = globalThis.navigator || {};
          if (!globalThis.navigator.language)
            globalThis.navigator.language = info.language || 'zh';
          if (typeof globalThis.navigator.onLine === 'undefined')
            globalThis.navigator.onLine = true;
          if (!globalThis.navigator.userAgent)
            globalThis.navigator.userAgent = 'XEngine-WeChat';
          globalThis.fetch = function packageRead(url) {
            const rel = resolveFetchedPackagePath(url);
            if (typeof rel !== 'string' || !rel)
              throw new Error('package-relative read only');
            if (/^https?:\/\//i.test(rel) || rel.indexOf('://') >= 0)
              throw new Error('package-relative read only; do not download runtime wasm');
            const data = fs.readFileSync(rel);
            return Promise.resolve({
              ok: true,
              status: 200,
              statusText: 'OK',
              url: rel,
              headers: { length: 0, get: function () { return null; } },
              arrayBuffer: function () { return Promise.resolve(data); },
              json: function () {
                const text = typeof data === 'string' ? data : new TextDecoder('utf-8').decode(data);
                return Promise.resolve(JSON.parse(text));
              },
              text: function () {
                const text = typeof data === 'string' ? data : new TextDecoder('utf-8').decode(data);
                return Promise.resolve(text);
              }
            });
          };
        }
        const SETTINGS_PREFIX = 'xengine.settings.';
        function setSettings(key, value) {
          if (typeof key !== 'string' || key.indexOf(SETTINGS_PREFIX) !== 0)
            throw new Error('v1 storage is settings keys only');
          if (typeof value === 'string' && value.length > 1024)
            throw new Error('settings value too large');
          wx.setStorageSync(key, value);
        }
        function getSettings(key) {
          try { return wx.getStorageSync(key); } catch (_) { return ''; }
        }
        function readPackageFile(rel) {
          return fs.readFileSync(rel);
        }
        function packageFileExists(rel) {
          try { fs.accessSync(rel); return true; } catch (_) { return false; }
        }
        function downloadCdn(url, destRel) {
          return new Promise(function (resolve, reject) {
            wx.downloadFile({
              url: url,
              success: function (res) {
                if (res.statusCode !== 200) { reject(res); return; }
                resolve(res.tempFilePath);
              },
              fail: reject
            });
          }).then(function (tempPath) {
            if (!destRel)
              return tempPath;
            try { fs.copyFileSync(tempPath, destRel); } catch (_) { /* temp path is enough */ }
            return destRel || tempPath;
          });
        }
        function loadCdnSubpackage() {
          if (typeof wx.loadSubpackage !== 'function')
            return;
          try {
            wx.loadSubpackage({
              name: 'cdn',
              success: function () { console.log('[XEngine] WeChat subpackage cdn ready'); },
              fail: function (err) { console.warn('[XEngine] WeChat subpackage cdn skipped:', err); }
            });
          } catch (e) {
            console.warn('[XEngine] WeChat subpackage cdn skipped:', e);
          }
        }

        function instantiateRuntime(path, imports) {
          if (typeof path !== 'string' || !path)
            throw new Error('WXWebAssembly path must be a non-empty package-relative string');
          if (/^https?:\/\//i.test(path) || path.indexOf('://') >= 0)
            throw new Error('WXWebAssembly path must be package-relative; do not fetch runtime wasm');
          const rel = path.replace(/^\/+/, '');
          const result = WXWebAssembly.instantiate(rel, imports || {});
          if (result && typeof result.then === 'function')
            return result;
          return Promise.resolve(result);
        }
        globalThis.__xengineInstantiateWasm = instantiateRuntime;

        function resolveNativeWasmPath() {
          const candidates = ['wasm/dotnet.native.wasm.br', 'wasm/dotnet.native.wasm'];
          for (let i = 0; i < candidates.length; i++) {
            if (packageFileExists(candidates[i]))
              return candidates[i];
          }
          return null;
        }

        function wasmSourceToPackagePath(source) {
          if (typeof source === 'string') {
            const rel = source.replace(/^\/+/, '');
            if (/^https?:\/\//i.test(rel) || rel.indexOf('://') >= 0)
              throw new Error('WXWebAssembly path must be package-relative; do not fetch runtime wasm');
            return rel;
          }
          if (source && typeof source === 'object' && typeof source.__xengineWasmPath === 'string')
            return source.__xengineWasmPath;
          const nativePath = resolveNativeWasmPath();
          if (!nativePath)
            throw new Error('missing wasm/dotnet.native.wasm for WXWebAssembly.instantiate');
          return nativePath;
        }

        function wasmBytesHaveMagic(source) {
          try {
            const u8 = source instanceof ArrayBuffer
              ? new Uint8Array(source)
              : (source && source.buffer ? new Uint8Array(source.buffer, source.byteOffset || 0, source.byteLength) : null);
            return !!(u8 && u8.length >= 4 && u8[0] === 0 && u8[1] === 97 && u8[2] === 115 && u8[3] === 109);
          } catch (_) {
            return false;
          }
        }

        function installWebAssemblyShim() {
          if (typeof WXWebAssembly !== 'object' || !WXWebAssembly)
            return;
          if (globalThis.WebAssembly && globalThis.WebAssembly.__xengineWeChatShim)
            return;
          function compileWasm(source) {
            return Promise.resolve(new WasmModule(source));
          }
          function instantiateWasmModule(source, imports) {
            return Promise.resolve(WXWebAssembly.instantiate(wasmSourceToPackagePath(source), imports || {}));
          }
          function validateWasm(source) {
            if (typeof source === 'string' && typeof WXWebAssembly.validate === 'function')
              return WXWebAssembly.validate(source);
            if (typeof WXWebAssembly.validate === 'function') {
              try { return !!WXWebAssembly.validate(source); } catch (_) { /* path-only */ }
            }
            if (!wasmBytesHaveMagic(source))
              return false;
            const u8 = source instanceof ArrayBuffer
              ? new Uint8Array(source)
              : new Uint8Array(source.buffer, source.byteOffset || 0, source.byteLength);
            for (let i = 0; i < u8.length; i++) {
              if (u8[i] === 0xfd)
                return true;
            }
            return false;
          }
          function WasmModule(source) {
            this.__xengineWasmPath = wasmSourceToPackagePath(source);
          }
          globalThis.WebAssembly = {
            __xengineWeChatShim: true,
            compile: compileWasm,
            instantiate: instantiateWasmModule,
            validate: validateWasm,
            Module: WasmModule,
            Memory: WXWebAssembly.Memory,
            Table: WXWebAssembly.Table,
            Global: WXWebAssembly.Global,
            CompileError: WXWebAssembly.CompileError || Error,
            LinkError: WXWebAssembly.LinkError || Error,
            RuntimeError: WXWebAssembly.RuntimeError || Error
          };
        }

        function readUtf8PackageFile(rel) {
          const raw = fs.readFileSync(rel, 'utf8');
          return typeof raw === 'string' ? raw : new TextDecoder('utf-8').decode(raw);
        }

        function listDotnetSubpackages() {
          const names = [];
          const roots = [];
          try {
            const cfg = JSON.parse(readUtf8PackageFile('game.json'));
            const list = (cfg && cfg.subpackages) || [];
            for (let i = 0; i < list.length; i++) {
              const name = list[i] && list[i].name;
              if (typeof name === 'string' && name.indexOf('dotnet') === 0) {
                names.push(name);
                roots.push((list[i].root || (name + '/')).replace(/\\/g, '/'));
              }
            }
          } catch (_) { /* generate-time empty pack */ }
          if (names.length === 0) {
            names.push('dotnet');
            roots.push('dotnet/');
          }
          return { names: names, roots: roots };
        }

        let assemblyMapCache = null;
        function loadAssemblyMap() {
          if (assemblyMapCache)
            return assemblyMapCache;
          assemblyMapCache = {};
          try {
            const parsed = JSON.parse(readUtf8PackageFile('assembly-map.json'));
            const files = (parsed && parsed.files) || {};
            for (const key in files)
              assemblyMapCache[key] = files[key];
          } catch (_) { /* generate-time empty pack */ }
          return assemblyMapCache;
        }

        function stripAssemblyFingerprint(base) {
          const m = /^(.+)\.([A-Za-z0-9]{8,12})\.(wasm)$/i.exec(base);
          return m ? (m[1] + '.' + m[3]) : '';
        }

        function resolveFetchedPackagePath(url) {
          let s = String(url || '');
          s = s.replace(/^https:\/\/xengine\.local\//, '');
          s = s.replace(/[?#].*$/, '');
          s = s.replace(/^\/+/, '');
          if (/dotnet\.native(\.[^/]+)?\.wasm(\.br)?$/.test(s))
            return resolveNativeWasmPath() || s;
          if (/dotnet\.native(\.[^/]+)?\.js$/.test(s))
            s = 'dotnet/dotnet.native.js';
          else if (/dotnet\.runtime(\.[^/]+)?\.js$/.test(s))
            s = 'dotnet/dotnet.runtime.js';
          else if (/dotnet\.[^/]+\.js$/.test(s) && s.indexOf('dotnet.boot') < 0 && s.indexOf('dotnet.diagnostics') < 0)
            s = 'dotnet/dotnet.js';
          const base = s.split('/').pop();
          const map = loadAssemblyMap();
          if (map[base])
            return map[base];
          const unhashed = stripAssemblyFingerprint(base);
          if (unhashed && map[unhashed])
            return map[unhashed];
          if (packageFileExists(s))
            return s;
          const packs = listDotnetSubpackages().roots;
          const names = [base];
          if (unhashed && unhashed !== base)
            names.push(unhashed);
          for (let n = 0; n < names.length; n++) {
            for (let i = 0; i < packs.length; i++) {
              const candidate = packs[i] + names[n];
              if (packageFileExists(candidate))
                return candidate;
            }
            if (packageFileExists('wasm/' + names[n]))
              return 'wasm/' + names[n];
          }
          return s;
        }

        function loadOneSubpackage(name) {
          return new Promise(function (resolve) {
            let done = false;
            function finish(kind, err) {
              if (done)
                return;
              done = true;
              if (err)
                console.warn('[XEngine] WeChat subpackage ' + name + ' ' + kind + ':', err);
              else
                console.warn('[XEngine] WeChat subpackage ' + name + ' ' + kind);
              resolve();
            }
            if (typeof wx.loadSubpackage !== 'function') {
              finish('skipped-no-api');
              return;
            }
            const timer = setTimeout(function () { finish('timeout'); }, 8000);
            try {
              wx.loadSubpackage({
                name: name,
                success: function () { clearTimeout(timer); finish('ready'); },
                fail: function (err) { clearTimeout(timer); finish('skipped', err); }
              });
            } catch (e) {
              clearTimeout(timer);
              finish('skipped', e);
            }
          });
        }

        function loadCodeSubpackages() {
          const names = listDotnetSubpackages().names;
          console.warn('[XEngine] WeChat loading code subpackages', names.join(','));
          const tasks = [];
          for (let i = 0; i < names.length; i++)
            tasks.push(loadOneSubpackage(names[i]));
          return tasks.length ? Promise.all(tasks) : Promise.resolve();
        }

        function exportKeySummary(value, depth) {
          if (!value || typeof value !== 'object' || depth < 0)
            return typeof value;
          const keys = Object.keys(value);
          if (depth === 0)
            return keys;
          const out = {};
          for (let i = 0; i < keys.length && i < 24; i++)
            out[keys[i]] = exportKeySummary(value[keys[i]], depth - 1);
          return out;
        }

        function findBrowserPlayer(exports) {
          if (!exports || typeof exports !== 'object')
            return null;
          const nested = exports.XEngine && exports.XEngine.Generated && exports.XEngine.Generated.Web
            ? exports.XEngine.Generated.Web.BrowserPlayer
            : null;
          if (nested && typeof nested.SetAssetBase === 'function')
            return nested;
          if (typeof exports.SetAssetBase === 'function' && typeof exports.AttachCanvas === 'function')
            return exports;
          const keys = Object.keys(exports);
          for (let i = 0; i < keys.length; i++) {
            const child = exports[keys[i]];
            if (child && typeof child === 'object') {
              const found = findBrowserPlayer(child);
              if (found)
                return found;
            }
          }
          return null;
        }

        function resolveMainAssemblyName(api) {
          const cfg = api && api.getConfig ? api.getConfig() : null;
          const fromConfig = cfg && cfg.mainAssemblyName;
          return fromConfig || 'My_Game.Web';
        }

        function loadPlayerExports(api) {
          const main = resolveMainAssemblyName(api);
          const names = [main];
          if (main.indexOf('.dll') < 0)
            names.push(main + '.dll');
          if (main !== 'My_Game.Web') {
            names.push('My_Game.Web');
            names.push('My_Game.Web.dll');
          }
          console.warn('[XEngine] WeChat getAssemblyExports', names.join(','));
          let chain = Promise.resolve(null);
          for (let i = 0; i < names.length; i++) {
            (function (name) {
              chain = chain.then(function (found) {
                if (found)
                  return found;
                return api.getAssemblyExports(name).then(function (exports) {
                  console.warn('[XEngine] WeChat assembly exports', name, JSON.stringify(exportKeySummary(exports, 2)));
                  return findBrowserPlayer(exports);
                }).catch(function (err) {
                  console.warn('[XEngine] WeChat getAssemblyExports failed', name, err);
                  return null;
                });
              });
            })(names[i]);
          }
          return chain.then(function (player) {
            if (!player)
              console.warn('[XEngine] WeChat JSExport missing; self-host attach');
            return player;
          });
        }

        function bootMonoRuntime() {
          installHostShims();
          console.warn('[XEngine] WeChat requiring ./dotnet/dotnet.js');
          let loader = null;
          try {
            loader = require('./dotnet/dotnet.js');
          } catch (e) {
            console.warn('[XEngine] WeChat CommonJS Mono loader missing; empty-import instantiate fallback:', e);
            return bootEmptyImportWasm();
          }
          const host = loader.dotnet || (loader.default && loader.default.dotnet) || loader;
          if (!host || typeof host.create !== 'function') {
            console.warn('[XEngine] WeChat loader has no dotnet.create; empty-import instantiate fallback.');
            return bootEmptyImportWasm();
          }
          const nativePath = resolveNativeWasmPath();
          console.warn('[XEngine] WeChat dotnet.create', nativePath);
          return host
            .withModuleConfig({
              canvas: canvas,
              instantiateWasm: function (imports, successCallback) {
                if (!nativePath)
                  throw new Error('missing wasm/dotnet.native.wasm for WXWebAssembly.instantiate');
                console.warn('[XEngine] WeChat instantiateWasm', nativePath);
                instantiateRuntime(nativePath, imports).then(function (result) {
                  const instance = result && result.instance ? result.instance : result;
                  const wasmModule = result && result.module;
                  console.warn('[XEngine] WeChat instantiateWasm resolved');
                  successCallback(instance, wasmModule);
                }).catch(function (err) {
                  console.warn('[XEngine] WeChat instantiateWasm failed:', err);
                  globalThis.__xengineWasmError = String(err);
                });
                return {};
              }
            })
            .withConfig({
              environmentVariables: {
                DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
                XENGINE_CANVAS_WIDTH: String(canvas.width),
                XENGINE_CANVAS_HEIGHT: String(canvas.height)
              },
              disableIntegrityCheck: true,
              globalizationMode: 'invariant'
            })
            .create()
            .then(function (api) {
              globalThis.__xengineDotnet = api;
              if (typeof api.setModuleImports === 'function') {
                api.setModuleImports('xengine', {
                  log: function (msg) { console.warn('[XEngine] WeChat C#', msg); }
                });
              }
              const main = resolveMainAssemblyName(api);
              const cfg = api.getConfig ? api.getConfig() : null;
              silenceNativeMagentaPump();
              console.warn('[XEngine] WeChat runMain', main, 'configKeys', cfg ? Object.keys(cfg).join(',') : 'null');
              const run = api.runMain ? api.runMain(main, []) : Promise.resolve();
              return run.then(function (code) {
                console.warn('[XEngine] WeChat runMain done', code);
                return loadPlayerExports(api);
              }).then(function (player) {
                if (player) {
                  player.SetAssetBase('package:');
                  bindPlayerHooks(player);
                }
                globalThis.__xengineMonoReady = true;
                silenceNativeMagentaPump();
                console.warn('[XEngine] WeChat Mono path boot ok');
                if (!player && typeof Module !== 'undefined' && typeof Module._xengine_webgl2_clear_rgba8 === 'function')
                  console.warn('[XEngine] WeChat present fallback');
                return api;
              });
            })
            .catch(function (err) {
              console.warn('[XEngine] WeChat Mono path boot failed:', err);
              globalThis.__xengineWasmError = String(err);
              return null;
            });
        }

        function bootEmptyImportWasm() {
          const runtimePath = resolveNativeWasmPath();
          if (!runtimePath) {
            console.warn('[XEngine] WeChat package has no wasm/dotnet.native.wasm; instantiate deferred until publish copies it.');
            return Promise.resolve(null);
          }
          return instantiateRuntime(runtimePath, {}).then(function (result) {
            globalThis.__xengineWasmModule = result;
            console.log('[XEngine] WXWebAssembly.instantiate ok', runtimePath);
            return result;
          }).catch(function (err) {
            console.warn('[XEngine] WXWebAssembly.instantiate failed (Mono import object / JSExport boot still incomplete):', err);
            globalThis.__xengineWasmError = String(err);
            return null;
          });
        }

        function bootNativeWasm() {
          console.warn('[XEngine] WeChat boot start');
          return loadCodeSubpackages().then(function () {
            return bootMonoRuntime();
          }).catch(function (err) {
            console.warn('[XEngine] WeChat bootNativeWasm failed:', err);
            globalThis.__xengineWasmError = String(err);
            return null;
          });
        }

        function bindPlayerHooks(player) {
          if (!player)
            return;
          globalThis.__xengineOnVisibility = function (visible) { player.OnVisibility(visible); };
          globalThis.__xengineOnAnimationFrame = function (t) { player.OnAnimationFrame(t); };
          globalThis.__xengineOnTouch = function (kind, id, x, y, w, h, buttons, pressure) {
            player.OnPointer(kind, id, x, y, w, h, buttons, pressure, 1);
          };
          globalThis.__xengineTakeInnerAudio = function () { return player.TakePendingInnerAudio(); };
          globalThis.__xengineTakeStorage = function () { return player.TakePendingStorageWrite(); };
          try {
            const info = wx.getSystemInfoSync() || {};
            player.ConfigurePlatform(info.language || 'zh', true);
            player.InitializeAudio('inner');
            player.AttachCanvas(canvas.width, canvas.height, globalThis.__xengineHasWebGpu ? 1 : 0);
          } catch (e) {
            console.warn('[XEngine] WeChat player attach skipped:', e);
          }
        }

        let loopRunning = false;
        let rafId = 0;
        function drainHost() {
          if (globalThis.__xengineTakeInnerAudio)
            applyInnerAudio(globalThis.__xengineTakeInnerAudio());
          const packed = globalThis.__xengineTakeStorage && globalThis.__xengineTakeStorage();
          if (typeof packed === 'string') {
            const i = packed.indexOf('\n');
            if (i > 0)
              setSettings(packed.slice(0, i), packed.slice(i + 1));
          }
        }
        function scheduleFrame(cb) {
          if (canvas && typeof canvas.requestAnimationFrame === 'function')
            return canvas.requestAnimationFrame(cb);
          if (typeof requestAnimationFrame === 'function')
            return requestAnimationFrame(cb);
          return setTimeout(function () { cb(Date.now()); }, 16);
        }
        function silenceNativeMagentaPump() {
          var m = typeof Module !== 'undefined' ? Module : null;
          if (!m || typeof m._xengine_webgl2_on_native_frame !== 'function' || m.__xengineNoMagenta)
            return;
          m.__xengineNoMagenta = true;
          m._xengine_webgl2_on_native_frame = function () {
            if (typeof globalThis.__xengineManagedTick === 'function')
              globalThis.__xengineManagedTick();
          };
          console.warn('[XEngine] WeChat native magenta pump silenced');
        }
        function presentFallback() {
          if (globalThis.__xengineMonoReady)
            return;
          var m = typeof Module !== 'undefined' ? Module : null;
          if (m && typeof m._xengine_webgl2_clear_rgba8 === 'function')
            m._xengine_webgl2_clear_rgba8(255, 0, 255, 255);
        }
        function loop(timeMs) {
          if (!loopRunning)
            return;
          silenceNativeMagentaPump();
          if (globalThis.__xengineOnAnimationFrame)
            globalThis.__xengineOnAnimationFrame(timeMs);
          else
            presentFallback();
          drainHost();
          rafId = scheduleFrame(loop);
        }
        function startLoop() {
          if (loopRunning)
            return;
          loopRunning = true;
          rafId = scheduleFrame(loop);
        }
        function stopLoop() {
          loopRunning = false;
          if (typeof cancelAnimationFrame === 'function' && rafId)
            cancelAnimationFrame(rafId);
          rafId = 0;
        }

        wx.onShow(() => {
          if (globalThis.__xengineOnVisibility) globalThis.__xengineOnVisibility(true);
          startLoop();
        });
        wx.onHide(() => {
          stopLoop();
          if (globalThis.__xengineOnVisibility) globalThis.__xengineOnVisibility(false);
          innerAudio.pause();
        });

        function forwardTouch(kind, e) {
          const fn = globalThis.__xengineOnTouch;
          if (!fn) return;
          const c = globalThis.__xengineCanvas;
          const w = (c && c.width) || 1;
          const h = (c && c.height) || 1;
          const list = e.changedTouches || e.touches || [];
          for (let i = 0; i < list.length; i++) {
            const t = list[i];
            fn(kind, t.identifier | 0, t.clientX || 0, t.clientY || 0, w, h, 1, t.force || 1);
          }
        }
        wx.onTouchStart(e => forwardTouch(0, e));
        wx.onTouchMove(e => forwardTouch(1, e));
        wx.onTouchEnd(e => forwardTouch(2, e));
        wx.onTouchCancel(e => forwardTouch(3, e));

        loadCdnSubpackage();
        bootNativeWasm();
        startLoop();

        module.exports = {
          instantiateRuntime,
          bootNativeWasm,
          canvas,
          innerAudio,
          setSettings,
          getSettings,
          readPackageFile,
          downloadCdn,
          bindPlayerHooks,
          startLoop,
          stopLoop
        };
        """;

    internal static string CreateDouyinGameJson() =>
        """
        {
          "deviceOrientation": "landscape",
          "showStatusBar": false,
          "subpackages": [
            {
              "name": "cdn",
              "root": "cdn/"
            }
          ]
        }
        """;

    internal static string CreateDouyinProjectConfig(string applicationLabel) =>
        $$"""
        {
          "description": "XEngine Douyin mini-game",
          "setting": {
            "urlCheck": false,
            "es6": false,
            "minified": false
          },
          "compileType": "game",
          "appid": "testAppId",
          "projectname": {{System.Text.Json.JsonSerializer.Serialize(applicationLabel)}},
          "condition": {}
        }
        """;

    internal static string CreateDouyinGameJs() =>
        """
        // Douyin mini-game adapter. Runtime wasm MUST be instantiated from a code-package
        // path (TTWebAssembly.instantiate), preferring wasm/dotnet.native.wasm.br.
        // This publish is SIMD-off. Do not request a GPU device. Clear is optional.
        const canvas = tt.createCanvas();
        globalThis.__xengineHasWebGpu = false;
        const sys = tt.getSystemInfoSync() || {};
        const dpr = sys.pixelRatio || 1;
        const cssW = sys.windowWidth || sys.screenWidth || 1;
        const cssH = sys.windowHeight || sys.screenHeight || 1;
        canvas.width = Math.max(1, Math.floor(cssW * dpr));
        canvas.height = Math.max(1, Math.floor(cssH * dpr));
        globalThis.Module = globalThis.Module || {};
        Module.canvas = canvas;
        globalThis.__xengineCanvas = canvas;
        // Leave the canvas unused so emscripten_webgl_create_context can own WebGL2.

        const innerAudio = tt.createInnerAudioContext();
        globalThis.__xengineInnerAudio = innerAudio;
        function applyInnerAudio(cmd) {
          if (!cmd) return;
          if (cmd.indexOf('play|') === 0) {
            innerAudio.src = cmd.slice(5);
            innerAudio.play();
          } else if (cmd === 'pause') {
            innerAudio.pause();
          } else if (cmd === 'stop') {
            innerAudio.stop();
          }
        }
        globalThis.__xengineApplyInnerAudio = applyInnerAudio;

        const fs = tt.getFileSystemManager();
        const SETTINGS_PREFIX = 'xengine.settings.';
        function setSettings(key, value) {
          if (typeof key !== 'string' || key.indexOf(SETTINGS_PREFIX) !== 0)
            throw new Error('v1 storage is settings keys only');
          if (typeof value === 'string' && value.length > 1024)
            throw new Error('settings value too large');
          tt.setStorageSync(key, value);
        }
        function getSettings(key) {
          try { return tt.getStorageSync(key); } catch (_) { return ''; }
        }
        function readPackageFile(rel) {
          return fs.readFileSync(rel);
        }
        function packageFileExists(rel) {
          try { fs.accessSync(rel); return true; } catch (_) { return false; }
        }
        function downloadCdn(url, destRel) {
          return new Promise(function (resolve, reject) {
            tt.downloadFile({
              url: url,
              success: function (res) {
                if (res.statusCode !== 200) { reject(res); return; }
                resolve(res.tempFilePath);
              },
              fail: reject
            });
          }).then(function (tempPath) {
            if (!destRel)
              return tempPath;
            try { fs.copyFileSync(tempPath, destRel); } catch (_) { /* temp path is enough */ }
            return destRel || tempPath;
          });
        }
        function loadCdnSubpackage() {
          if (typeof tt.loadSubpackage !== 'function')
            return;
          try {
            tt.loadSubpackage({
              name: 'cdn',
              success: function () { console.log('[XEngine] Douyin subpackage cdn ready'); },
              fail: function (err) { console.warn('[XEngine] Douyin subpackage cdn skipped:', err); }
            });
          } catch (e) {
            console.warn('[XEngine] Douyin subpackage cdn skipped:', e);
          }
        }

        function instantiateRuntime(path, imports) {
          if (typeof path !== 'string' || !path)
            throw new Error('TTWebAssembly path must be a non-empty package-relative string');
          if (/^https?:\/\//i.test(path) || path.indexOf('://') >= 0)
            throw new Error('TTWebAssembly path must be package-relative; do not fetch runtime wasm');
          const rel = path.replace(/^\/+/, '');
          return TTWebAssembly.instantiate(rel, imports || {});
        }
        globalThis.__xengineInstantiateWasm = instantiateRuntime;

        function bootNativeWasm() {
          const candidates = ['wasm/dotnet.native.wasm.br', 'wasm/dotnet.native.wasm'];
          let runtimePath = null;
          for (let i = 0; i < candidates.length; i++) {
            if (packageFileExists(candidates[i])) {
              runtimePath = candidates[i];
              break;
            }
          }
          if (!runtimePath) {
            console.warn('[XEngine] Douyin package has no wasm/dotnet.native.wasm.br; instantiate deferred until publish copies it.');
            return Promise.resolve(null);
          }
          return instantiateRuntime(runtimePath, {}).then(function (result) {
            globalThis.__xengineWasmModule = result;
            console.log('[XEngine] TTWebAssembly.instantiate ok', runtimePath);
            return result;
          }).catch(function (err) {
            console.warn('[XEngine] TTWebAssembly.instantiate failed (Mono import object / JSExport boot still incomplete):', err);
            globalThis.__xengineWasmError = String(err);
            return null;
          });
        }

        function bindPlayerHooks(player) {
          if (!player)
            return;
          globalThis.__xengineOnVisibility = function (visible) { player.OnVisibility(visible); };
          globalThis.__xengineOnAnimationFrame = function (t) { player.OnAnimationFrame(t); };
          globalThis.__xengineOnTouch = function (kind, id, x, y, w, h, buttons, pressure) {
            player.OnPointer(kind, id, x, y, w, h, buttons, pressure, 1);
          };
          globalThis.__xengineTakeInnerAudio = function () { return player.TakePendingInnerAudio(); };
          globalThis.__xengineTakeStorage = function () { return player.TakePendingStorageWrite(); };
          try {
            const info = tt.getSystemInfoSync() || {};
            player.ConfigurePlatform(info.language || 'zh', true);
            player.InitializeAudio('inner');
            player.AttachCanvas(canvas.width, canvas.height, 0);
          } catch (e) {
            console.warn('[XEngine] Douyin player attach skipped:', e);
          }
        }

        let loopRunning = false;
        let rafId = 0;
        function drainHost() {
          if (globalThis.__xengineTakeInnerAudio)
            applyInnerAudio(globalThis.__xengineTakeInnerAudio());
          const packed = globalThis.__xengineTakeStorage && globalThis.__xengineTakeStorage();
          if (typeof packed === 'string') {
            const i = packed.indexOf('\n');
            if (i > 0)
              setSettings(packed.slice(0, i), packed.slice(i + 1));
          }
        }
        function loop(timeMs) {
          if (!loopRunning)
            return;
          if (globalThis.__xengineOnAnimationFrame)
            globalThis.__xengineOnAnimationFrame(timeMs);
          drainHost();
          rafId = requestAnimationFrame(loop);
        }
        function startLoop() {
          if (loopRunning)
            return;
          loopRunning = true;
          rafId = requestAnimationFrame(loop);
        }
        function stopLoop() {
          loopRunning = false;
          if (typeof cancelAnimationFrame === 'function' && rafId)
            cancelAnimationFrame(rafId);
          rafId = 0;
        }

        tt.onShow(() => {
          if (globalThis.__xengineOnVisibility) globalThis.__xengineOnVisibility(true);
          startLoop();
        });
        tt.onHide(() => {
          stopLoop();
          if (globalThis.__xengineOnVisibility) globalThis.__xengineOnVisibility(false);
          innerAudio.pause();
        });

        function forwardTouch(kind, e) {
          const fn = globalThis.__xengineOnTouch;
          if (!fn) return;
          const c = globalThis.__xengineCanvas;
          const w = (c && c.width) || 1;
          const h = (c && c.height) || 1;
          const list = e.changedTouches || e.touches || [];
          for (let i = 0; i < list.length; i++) {
            const t = list[i];
            fn(kind, t.identifier | 0, t.clientX || 0, t.clientY || 0, w, h, 1, t.force || 1);
          }
        }
        tt.onTouchStart(e => forwardTouch(0, e));
        tt.onTouchMove(e => forwardTouch(1, e));
        tt.onTouchEnd(e => forwardTouch(2, e));
        tt.onTouchCancel(e => forwardTouch(3, e));

        loadCdnSubpackage();
        bootNativeWasm();
        startLoop();

        module.exports = {
          instantiateRuntime,
          bootNativeWasm,
          canvas,
          innerAudio,
          setSettings,
          getSettings,
          readPackageFile,
          downloadCdn,
          bindPlayerHooks,
          startLoop,
          stopLoop
        };
        """;

    private static string Xml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}
