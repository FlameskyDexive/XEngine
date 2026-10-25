using System;
using XEngine.WebSurface;
using Xunit;

namespace XEngine.Runtime.Test.WebSurface;

/// <summary>
/// B2 — the SharedBuffer frame bridge host side: page control protocol, the single-in-flight
/// frame relay (produce tokens, stale-token drops, sync-wait/copy cost accounting) and the
/// frame scheduler the driver uses to pace requests at the asset's framerate cap. The WebView2
/// thread itself is exercised by the live editor acceptance; these tests pin the protocol logic
/// both sides share.
/// </summary>
public sealed class WebSurfaceHostBridgeTests
{
    private static WebSurfaceAsset Asset() => new()
    {
        ContentEntry = "Assets/Demo/index.html",
        Quality = new WebSurfaceQuality { Width = 32, Height = 24 },
    };

    // ------------------------------------------------------------------ protocol

    [Fact]
    public void Protocol_ProduceMessage_CarriesFrameNumber()
    {
        string json = WebSurfacePageProtocol.Produce(42);
        string? type = WebSurfacePageProtocol.TryParseEvent(json, out ulong no, out double ms, out _);
        Assert.Equal("produce", type);
        Assert.Equal(42UL, no);
        Assert.Equal(0, ms);
    }

    [Fact]
    public void Protocol_FrameMessage_CarriesNumberAndPageCost()
    {
        string json = """{"type":"websurface","event":"frame","no":7,"pageMs":1.25}""";
        string? type = WebSurfacePageProtocol.TryParseEvent(json, out ulong no, out double ms, out _);
        Assert.Equal("frame", type);
        Assert.Equal(7UL, no);
        Assert.Equal(1.25, ms, 3);
        Assert.True(WebSurfacePageProtocol.IsFrameEvent(type));
    }

    [Fact]
    public void Protocol_ReadyAndBufferAndError_AreClassified()
    {
        string? ready = WebSurfacePageProtocol.TryParseEvent(
            """{"type":"websurface","event":"ready"}""", out _, out _, out _);
        Assert.True(WebSurfacePageProtocol.IsReadyEvent(ready));

        string? buffer = WebSurfacePageProtocol.TryParseEvent(
            """{"type":"websurface","event":"buffer","width":64,"height":64}""", out _, out _, out _);
        Assert.Equal("buffer", buffer);

        string? error = WebSurfacePageProtocol.TryParseEvent(
            """{"type":"websurface","event":"error","message":"boom"}""", out _, out _, out string? message);
        Assert.True(WebSurfacePageProtocol.IsErrorEvent(error));
        Assert.Equal("boom", message);
    }

    [Fact]
    public void Protocol_ForeignAndMalformedMessages_AreIgnored()
    {
        Assert.Null(WebSurfacePageProtocol.TryParseEvent(
            """{"type":"other","event":"frame","no":1}""", out _, out _, out _));
        Assert.Null(WebSurfacePageProtocol.TryParseEvent("not json at all", out _, out _, out _));
        Assert.Null(WebSurfacePageProtocol.TryParseEvent("[]", out _, out _, out _));
        // A frame event without a number defaults to 0 and still reports its type.
        string? type = WebSurfacePageProtocol.TryParseEvent(
            """{"type":"websurface","event":"frame"}""", out ulong no, out _, out _);
        Assert.Equal("frame", type);
        Assert.Equal(0UL, no);
    }

    [Fact]
    public void Protocol_SharedBufferHandoff_DescribesPinnedLayout()
    {
        string json = WebSurfacePageProtocol.SharedBufferHandoff(320, 200);
        Assert.Contains("\"width\":320", json);
        Assert.Contains("\"height\":200", json);
        Assert.Contains("\"format\":\"rgba8\"", json);
        Assert.Contains("\"origin\":\"topLeft\"", json);
        Assert.Contains("\"alpha\":\"straight\"", json);
    }

    // ------------------------------------------------------------------ relay: single flight

    [Fact]
    public void Relay_SecondProduceIsRejected()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        ulong? first = relay.BeginProduce();
        ulong? second = relay.BeginProduce();

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, session.Diagnostics.Get(WebSurfaceCounters.ProduceRejects));
        Assert.True(relay.IsProducing);
    }

    // ------------------------------------------------------------------ relay: delivery

    [Fact]
    public void Relay_DeliveredFrame_IsCopiedCommittedAndMeasured()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        ulong? token = relay.BeginProduce();
        int copies = 0;
        bool committed = relay.OnFrameReady(token!.Value, pageMs: 0.5, frame =>
        {
            copies++;
            Array.Fill(frame.Pixels, (byte)9);
        });

        Assert.True(committed);
        Assert.Equal(1, copies);
        Assert.False(relay.IsProducing);
        Assert.Equal(token.Value + 1, relay.NextFrameNumber);

        WebSurfaceDiagnostics d = session.Diagnostics;
        Assert.Equal(1, d.Get(WebSurfaceCounters.FramesFromProducer));
        Assert.Equal(1, d.Get(WebSurfaceCounters.SharedBufferCopies));
        Assert.Equal(1, d.Get(WebSurfaceCounters.SyncWaits));
        Assert.Equal(32L * 24 * 4, d.Get(WebSurfaceCounters.BytesCopied));
        Assert.True(d.Get(WebSurfaceCounters.CopyMicrosTotal) >= 0);
        Assert.Equal(500, d.Get(WebSurfaceCounters.PageReadbackMicrosTotal));
        Assert.True(d.Get(WebSurfaceCounters.SyncWaitMicrosTotal) >= 0);

        // The frame really reached the pool for presentation.
        using WebSurfaceFrame? presented = session.TryAcquireLatest();
        Assert.NotNull(presented);
        Assert.Equal(token.Value, presented!.FrameNumber);
        Assert.Equal(9, presented.Pixels[0]);
    }

    [Fact]
    public void Relay_StaleFrameNumber_IsDroppedWithoutCommit()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        ulong? token = relay.BeginProduce();

        bool delivered = relay.OnFrameReady(token!.Value + 100, pageMs: 0, frame => { });
        Assert.False(delivered);
        Assert.Equal(0, session.Diagnostics.Get(WebSurfaceCounters.FramesFromProducer));
        Assert.True(relay.IsProducing); // the in-flight token is still outstanding

        // The correct frame is still accepted afterwards.
        Assert.True(relay.OnFrameReady(token.Value, pageMs: 0, frame => { }));
    }

    [Fact]
    public void Relay_FrameWithoutProduce_IsDropped()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        bool delivered = relay.OnFrameReady(0, pageMs: 0, frame => { });
        Assert.False(delivered);
        Assert.Equal(0, session.Diagnostics.Get(WebSurfaceCounters.FramesFromProducer));
    }

    // ------------------------------------------------------------------ relay: cancellation

    [Fact]
    public void Relay_CancelProduce_AdvancesTokenSoStaleFramesCannotSatisfy()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        ulong? first = relay.BeginProduce();
        relay.CancelProduce();

        Assert.False(relay.IsProducing);
        Assert.Equal(first!.Value + 1, relay.NextFrameNumber);

        // The cancelled token's frame arrives late (old page callback) — dropped.
        ulong? next = relay.BeginProduce();
        Assert.Equal(first.Value + 1, next);
        Assert.False(relay.OnFrameReady(first.Value, pageMs: 0, frame => { }));
        Assert.True(relay.OnFrameReady(next!.Value, pageMs: 0, frame => { }));
    }

    [Fact]
    public void Relay_CopyThrowing_CancelsTheFrameAndPropagates()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        ulong? token = relay.BeginProduce();

        Assert.Throws<InvalidOperationException>(() =>
            relay.OnFrameReady(token!.Value, pageMs: 0, _ => throw new InvalidOperationException("copy failed")));

        // The pool is writable again (the in-flight frame was cancelled) and the token retired.
        Assert.Equal(1, session.Diagnostics.Get(WebSurfaceCounters.WritesCancelled));
        Assert.False(relay.IsProducing);
    }

    [Fact]
    public void Relay_Disposed_RejectsProduceAndDelivery()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var relay = new WebSurfaceFrameRelay(session, session.Diagnostics, 32, 24, WebSurfaceAlphaMode.Opaque);
        relay.Dispose();

        Assert.Null(relay.BeginProduce());
        Assert.False(relay.OnFrameReady(0, pageMs: 0, frame => { }));
    }

    // ------------------------------------------------------------------ scheduler + driver

    [Fact]
    public void Scheduler_RequestsAtMostOncePerCapInterval()
    {
        var scheduler = new WebSurfaceFrameScheduler(new WebSurfaceQuality { FramerateCapHz = 30 });
        Assert.Equal(1f / 30f, scheduler.MinRequestIntervalSeconds, 5);

        // Sub-interval ticks never request; the accumulated overdue time produces exactly one.
        Assert.False(scheduler.ShouldRequest(0.01f));
        Assert.False(scheduler.ShouldRequest(0.01f));
        Assert.False(scheduler.ShouldRequest(0.01f));
        Assert.True(scheduler.ShouldRequest(0.01f));  // total 40ms ≥ 33.3ms — one catch-up request
        Assert.False(scheduler.ShouldRequest(0.001f)); // accumulator was reset by the request
    }

    [Fact]
    public void Scheduler_ResetPreventsCatchupBurstAfterPause()
    {
        var scheduler = new WebSurfaceFrameScheduler(new WebSurfaceQuality { FramerateCapHz = 15 });
        Assert.False(scheduler.ShouldRequest(0.05f)); // 50ms < 66.7ms — only accumulates
        scheduler.Reset();
        Assert.False(scheduler.ShouldRequest(0.02f)); // 20ms alone must not fire off the old debt
    }

    [Fact]
    public void Driver_TicksRequestAndPresentThroughSession()
    {
        using var session = new WebSurfaceSession();
        var backend = new FakeWebSurfaceBackend();
        session.Load(Asset(), backend);
        session.Run();

        var driver = new WebSurfaceFrameDriver(session, graphic: null);
        WebSurfaceDriverTick tick = driver.Tick(0.5f);

        // The fake backend produces synchronously inside RequestFrame, so the tick both
        // requested and left a presentable frame in the pool.
        Assert.True(tick.Requested);
        Assert.Equal(1, session.Diagnostics.Get(WebSurfaceCounters.FramesCommitted));
        Assert.Null(tick.PresentedFrameNumber); // no graphic bound — nothing presented

        // Session paused: no further requests go out even when due.
        session.Pause();
        WebSurfaceDriverTick paused = driver.Tick(0.5f);
        Assert.False(paused.Requested);
    }

    // ------------------------------------------------------------------ pool byte accounting

    [Fact]
    public void Pool_ConsumingSlotDoubleReturn_KeepsAccountingExact()
    {
        var diagnostics = new WebSurfaceDiagnostics();
        var pool = new WebSurfaceFramePool(diagnostics);

        using WebSurfaceFrame? first = pool.BeginFrame(0, 16, 16, 16 * 4, WebSurfaceAlphaMode.Opaque);
        Assert.NotNull(first);
        Assert.True(pool.Commit(first!));
        Assert.Equal(16L * 16 * 4, diagnostics.LiveBufferBytes);

        // Present: TryAcquireLatest moves the slot to consuming; the consumer disposes the lease
        // (normal present path). The NEXT acquire must not return the same slot again — the
        // double return would duplicate the buffer in the spare list and corrupt the accounting.
        using (WebSurfaceFrame? presented = pool.TryAcquireLatest())
        {
            Assert.NotNull(presented);
            Assert.Equal(16L * 16 * 4, diagnostics.LiveBufferBytes);
        }
        Assert.Equal(16L * 16 * 4, diagnostics.LiveBufferBytes);

        // The slot is back in the spare list exactly once; a second consumer cycle rents it.
        using WebSurfaceFrame? second = pool.BeginFrame(1, 16, 16, 16 * 4, WebSurfaceAlphaMode.Opaque);
        Assert.NotNull(second);
        Assert.True(pool.Commit(second!));
        Assert.Equal(16L * 16 * 4, diagnostics.LiveBufferBytes); // rented, not re-allocated

        // Many acquire/dispose cycles never push the accounting negative.
        for (int i = 0; i < 32; i++)
        {
            using WebSurfaceFrame? produce = pool.BeginFrame((ulong)(i + 2), 16, 16, 16 * 4, WebSurfaceAlphaMode.Opaque);
            Assert.NotNull(produce);
            Assert.True(pool.Commit(produce!));
            using WebSurfaceFrame? frame = pool.TryAcquireLatest();
            Assert.NotNull(frame);
        }
        Assert.True(diagnostics.LiveBufferBytes >= 0, "LiveBufferBytes=" + diagnostics.LiveBufferBytes);

        pool.Dispose();
        Assert.Equal(0, diagnostics.LiveBufferBytes);
    }

    [Fact]
    public void Pool_DoubleDisposeOfConsumingFrame_IsIdempotentOnAccounting()
    {
        var diagnostics = new WebSurfaceDiagnostics();
        var pool = new WebSurfaceFramePool(diagnostics);

        using WebSurfaceFrame? frame = pool.BeginFrame(0, 16, 16, 16 * 4, WebSurfaceAlphaMode.Opaque);
        Assert.True(pool.Commit(frame!));
        WebSurfaceFrame? acquired = pool.TryAcquireLatest();
        Assert.NotNull(acquired);

        // Consumer disposes twice (e.g. an explicit Dispose plus a using scope): the buffer is
        // returned once and the accounting stays exact.
        acquired!.Dispose();
        acquired.Dispose();
        Assert.Equal(16L * 16 * 4, diagnostics.LiveBufferBytes);

        pool.Dispose();
        Assert.Equal(0, diagnostics.LiveBufferBytes);
    }

    // ------------------------------------------------------------------ navigation policy

    [Fact]
    public void NavigationPolicy_ResolvesLocalContentUnderContentRoot()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xengine-websurface-tests");
        System.IO.Directory.CreateDirectory(root);
        string entry = System.IO.Path.Combine(root, "web", "index.html");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(entry)!);
        System.IO.File.WriteAllText(entry, "<html></html>");

        string target = WebSurfaceNavigationPolicy.ResolveTarget(
            new WebSurfaceAsset { ContentEntry = "web/index.html" }, root);

        Assert.Equal("https://websurface.local/web/index.html", target);
    }

    [Fact]
    public void NavigationPolicy_RejectsContentEntryEscapingContentRoot()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xengine-websurface-tests");
        System.IO.Directory.CreateDirectory(root);

        Assert.Throws<InvalidOperationException>(() => WebSurfaceNavigationPolicy.ResolveTarget(
            new WebSurfaceAsset { ContentEntry = "../../Windows/system32/config" }, root));
    }

    [Fact]
    public void NavigationPolicy_PassesHttpsThroughForAllowListCheck()
    {
        const string url = "https://cdn.example.com/app/index.html";
        Assert.Equal(url, WebSurfaceNavigationPolicy.ResolveTarget(
            new WebSurfaceAsset { ContentEntry = url }, "whatever-root"));
    }

    [Fact]
    public void NavigationPolicy_AllowsVirtualHostAndAboutFrames()
    {
        var asset = new WebSurfaceAsset { ContentEntry = "web/index.html" };
        Assert.True(WebSurfaceNavigationPolicy.DecideNavigation(asset, "https://websurface.local/web/index.html").Allow);
        Assert.True(WebSurfaceNavigationPolicy.DecideNavigation(asset, "about:blank").Allow);
    }

    [Fact]
    public void NavigationPolicy_BlocksInsecureSchemesAndNonAllowListedHosts()
    {
        var asset = new WebSurfaceAsset { ContentEntry = "web/index.html" };
        WebSurfaceNavigationDecision http = WebSurfaceNavigationPolicy.DecideNavigation(asset, "http://cdn.example.com/x.html");
        Assert.False(http.Allow);
        Assert.Contains("insecure scheme", http.BlockedReason);

        WebSurfaceNavigationDecision foreign = WebSurfaceNavigationPolicy.DecideNavigation(
            asset, "https://evil.example.com/payload.html");
        Assert.False(foreign.Allow);
        Assert.Contains("evil.example.com", foreign.BlockedReason);
        Assert.Contains("allow-list", foreign.BlockedReason);
    }

    [Fact]
    public void NavigationPolicy_AllowsListedHostsCaseInsensitively()
    {
        var asset = new WebSurfaceAsset
        {
            ContentEntry = "https://CDN.Example.com/app/index.html",
            AllowedHosts = ["cdn.example.com"],
        };
        Assert.True(WebSurfaceNavigationPolicy.DecideNavigation(
            asset, "https://CDN.Example.com/app/index.html").Allow);
    }
}
