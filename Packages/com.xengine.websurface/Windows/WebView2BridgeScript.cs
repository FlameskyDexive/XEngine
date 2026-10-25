namespace XEngine.WebSurface;

/// <summary>
/// The bridge script injected into every document (AddScriptToExecuteOnDocumentCreatedAsync).
/// Responsibilities: receive the host's shared buffer (RGBA8, top-left origin, straight alpha)
/// and expose it as a Uint8Array view; capture the page canvas into that view when a produce
/// token is outstanding — after the page's own requestAnimationFrame callbacks have rendered —
/// and signal the host. Canvas readback supports both 2D contexts (getImageData) and
/// WebGL/WebGL2 contexts (readPixels with an in-memory row flip, since GL is bottom-left).
/// The capture runs inside the same animation frame as the page render, so WebGL drawing
/// buffers are still valid (no preserveDrawingBuffer requirement).
/// </summary>
public static class WebView2BridgeScript
{
    public static string Script => """
        (function () {
            "use strict";
            if (window.__xengineWebSurfaceBridge) { return; }

            var state = { view: null, width: 0, height: 0, produceNo: null, pageMs: 0 };

            function post(message) {
                try { window.chrome.webview.postMessage(message); } catch (e) { /* host is gone */ }
            }

            window.chrome.webview.addEventListener("sharedbufferreceived", function (e) {
                try {
                    // additionalData arrives as an already-parsed object (or undefined); be
                    // tolerant of string shapes too, and surface any failure to the host.
                    var data = e.additionalData;
                    if (typeof data === "string") {
                        try { data = JSON.parse(data); } catch (err) { data = { }; }
                    }
                    data = data || { };
                    state.width = data.width | 0;
                    state.height = data.height | 0;
                    var buffer = typeof e.getBuffer === "function" ? e.getBuffer() : e.getArrayBuffer();
                    state.view = new Uint8Array(buffer);
                    var canvas = document.querySelector("canvas");
                    if (canvas) {
                        if (canvas.width !== state.width) { canvas.width = state.width; }
                        if (canvas.height !== state.height) { canvas.height = state.height; }
                    }
                    post({ type: "websurface", event: "buffer", width: state.width, height: state.height });
                } catch (err) {
                    post({ type: "websurface", event: "error", message: "sharedbufferreceived: " + (err && err.message) });
                }
            });

            window.chrome.webview.addEventListener("message", function (e) {
                var message = e.data;
                if (!message || message.type !== "websurface") { return; }
                if (message.event === "produce") { state.produceNo = message.no; return; }
                if (message.event === "input") { dispatchInput(message); }
            });

            // Engine-synthesized input (B6): forward to the page as DOM events. The page's own
            // event handlers run exactly as for real user input (hit testing, focus, IME stay
            // native to the browser).
            function dispatchInput(m) {
                try {
                    var canvas = document.querySelector("canvas");
                    var target = document.activeElement || document.body;
                    if (m.kind === "key") {
                        target.dispatchEvent(new KeyboardEvent(m.keyKind === "up" ? "keyup" : "keydown", {
                            key: m.key || "", bubbles: true, cancelable: true
                        }));
                        if (m.keyKind === "down" && m.char && m.char.length === 1) {
                            target.dispatchEvent(new InputEvent("beforeinput", {
                                data: m.char, inputType: "insertText", bubbles: true, cancelable: true
                            }));
                            target.dispatchEvent(new InputEvent("input", {
                                data: m.char, inputType: "insertText", bubbles: true
                            }));
                        }
                        return;
                    }
                    if (!canvas) { return; }
                    var rect = canvas.getBoundingClientRect();
                    var scaleX = rect.width ? canvas.width / rect.width : 1;
                    var scaleY = rect.height ? canvas.height / rect.height : 1;
                    var clientX = rect.left + (m.x | 0) / scaleX;
                    var clientY = rect.top + (m.y | 0) / scaleY;
                    var init = {
                        bubbles: true, cancelable: true,
                        clientX: clientX, clientY: clientY,
                        button: 0, buttons: m.buttons | 0,
                        pointerId: m.pointerId | 0, pointerType: m.pointerType || "mouse",
                        isPrimary: (m.pointerId | 0) === 0
                    };
                    var type = { down: "pointerdown", up: "pointerup", cancel: "pointercancel",
                                 move: "pointermove", scroll: "wheel" }[m.kind] || m.kind;
                    if (type === "wheel") {
                        canvas.dispatchEvent(new WheelEvent("wheel", Object.assign(init, { deltaX: 0, deltaY: -(m.scrollDelta || 0) })));
                        return;
                    }
                    canvas.dispatchEvent(new PointerEvent(type, init));
                    var mouseType = { down: "mousedown", up: "mouseup", cancel: "mouseleave",
                                      move: "mousemove" }[m.kind];
                    if (mouseType) {
                        canvas.dispatchEvent(new MouseEvent(mouseType, Object.assign(init, {
                            relatedTarget: null
                        })));
                    }
                } catch (err) {
                    post({ type: "websurface", event: "error", message: "input: " + (err && err.message) });
                }
            }

            function captureCanvas() {
                var canvas = document.querySelector("canvas");
                if (!canvas || !state.view) { return false; }
                // The page may have resized its canvas after the buffer was attached; the
                // capture always negotiates back to the shared-buffer geometry.
                if (canvas.width !== state.width) { canvas.width = state.width; }
                if (canvas.height !== state.height) { canvas.height = state.height; }
                var t0 = performance.now();
                var ctx2d = canvas.getContext("2d");
                if (ctx2d) {
                    var image = ctx2d.getImageData(0, 0, state.width, state.height);
                    state.view.set(image.data);
                } else {
                    var gl = canvas.getContext("webgl2") || canvas.getContext("webgl");
                    if (!gl) { return false; }
                    var stride = state.width * 4;
                    var rows = new Uint8Array(stride * state.height);
                    gl.readPixels(0, 0, state.width, state.height, gl.RGBA, gl.UNSIGNED_BYTE, rows);
                    for (var y = 0; y < state.height; y++) {
                        var from = (state.height - 1 - y) * stride;
                        state.view.set(rows.subarray(from, from + stride), y * stride);
                    }
                }
                state.pageMs = Math.round((performance.now() - t0) * 100) / 100;
                return true;
            }

            var originalRaf = window.requestAnimationFrame.bind(window);
            window.requestAnimationFrame = function (callback) {
                return originalRaf(function (timestamp) {
                    callback(timestamp);
                    if (state.produceNo !== null && state.view) {
                        var no = state.produceNo;
                        state.produceNo = null;
                        if (captureCanvas()) {
                            post({ type: "websurface", event: "frame", no: no, pageMs: state.pageMs });
                        }
                    }
                });
            };

            window.__xengineWebSurfaceBridge = true;
            post({ type: "websurface", event: "ready" });
        })();
        """;
}
