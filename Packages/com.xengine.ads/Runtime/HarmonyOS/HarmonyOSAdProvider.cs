// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;

using XEngine.Async;

namespace XEngine.Ads.HarmonyOS;

/// <summary>
/// <see cref="IAdProvider"/> over the <c>libxengine_ads.so</c> + ArkTS-glue bridge for one
/// HarmonyOS vendor (Huawei Ads Kit or Pangle — the glue, not this class, is vendor-specific;
/// select via <paramref name="vendorId"/>). Requests are JSON posts to the ArkTS main thread;
/// events complete the matching <see cref="AutoResetXTaskCompletionSource{TResult}"/> inline
/// on the ArkTS thread (XTask contract: continuations run on the completing thread).
/// </summary>
public sealed class HarmonyOSAdProvider : IAdProvider
{
    private const int AdTypeCount = 2; // RewardVideo, Interstitial

    private readonly IHarmonyOSAdsNative _native;
    private readonly string _vendorId;
    private readonly object _gate = new();
    private readonly bool[] _loaded = new bool[AdTypeCount];
    private readonly Dictionary<int, IPendingRequest> _pending = new();

    private AdsSettings? _settings;
    private int _nextId;
    private bool _nativeStarted;

    public HarmonyOSAdProvider(string vendorId)
        : this(vendorId, native: null)
    {
    }

    /// <summary>Seam-injecting constructor for unit tests; uses the real ABI when <paramref name="native"/> is null.</summary>
    internal HarmonyOSAdProvider(string vendorId, IHarmonyOSAdsNative? native)
    {
        _vendorId = vendorId;
        _native = native ?? new HarmonyOSAdsNativeAbi();
    }

    public string Id => _vendorId;

    public bool IsInitialized { get; private set; }

    public bool IsLoaded(AdType type)
    {
        lock (_gate)
        {
            return _loaded[TypeIndex(type)];
        }
    }

    public XTask InitializeAsync(AdsSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled(cancellationToken);
        return InitializeCoreAsync(settings);
    }

    public XTask<AdLoadResult> LoadAsync(AdType type, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled<AdLoadResult>(cancellationToken);
        return LoadCoreAsync(type);
    }

    public XTask<AdShowResult> ShowAsync(AdType type, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return XTask.FromCanceled<AdShowResult>(cancellationToken);
        return ShowCoreAsync(type);
    }

    public void Close()
    {
        lock (_gate)
        {
            IsInitialized = false;
            Array.Clear(_loaded);
            _pending.Clear();
            _settings = null;
        }
        _native.Stop();
        _nativeStarted = false;
    }

    public event Action<AdType>? Loaded;
    public event Action<AdType>? ShowStart;
    public event Action<AdType, bool>? ShowEnd;
    public event Action<AdType, string>? Error;

    private async XTask InitializeCoreAsync(AdsSettings settings)
    {
        if (!_nativeStarted)
        {
            int started = _native.Start(HandleEvent);
            if (started != 0)
            {
                throw new InvalidOperationException($"xengine_ads bridge start failed ({started}).");
            }
            _nativeStarted = true;
        }

        _settings = settings;
        var completion = AutoResetXTaskCompletionSource.Create();
        int id = RegisterPending(new VoidPending(completion));
        int posted = _native.Request(HarmonyOSAdsProtocol.BuildInitialize(id));
        if (posted != 0)
        {
            UnregisterPending(id);
            throw new InvalidOperationException($"xengine_ads request post failed ({posted}); ArkTS glue not registered?");
        }
        await completion.Task;
        IsInitialized = true;
    }

    private async XTask<AdLoadResult> LoadCoreAsync(AdType type)
    {
        if (!IsInitialized)
            return AdLoadResult.Fail(type, $"{_vendorId} provider is not initialized");
        AdsSettings? settings = _settings;
        if (settings == null)
            return AdLoadResult.Fail(type, "no settings captured at initialize");

        AdsProviderConfig? config = settings.GetProvider(_vendorId);
        string slotId = type == AdType.RewardVideo
            ? config?.RewardVideoSlotId ?? string.Empty
            : config?.InterstitialSlotId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(slotId))
            return AdLoadResult.Fail(type, $"no {type} slot id configured for {_vendorId}");

        var completion = AutoResetXTaskCompletionSource<AdLoadResult>.Create();
        int id = RegisterPending(new TypedPending<AdLoadResult>(completion, @event =>
            @event.Ok ? AdLoadResult.Ok(type) : AdLoadResult.Fail(type, FailMessage(@event))));
        int posted = _native.Request(HarmonyOSAdsProtocol.BuildLoad(id, type, slotId, settings.UseTestAds));
        if (posted != 0)
        {
            UnregisterPending(id);
            return AdLoadResult.Fail(type, $"xengine_ads request post failed ({posted})");
        }
        return await completion.Task;
    }

    private async XTask<AdShowResult> ShowCoreAsync(AdType type)
    {
        if (!IsLoaded(type))
        {
            string error = $"show with nothing loaded for {type}";
            Error?.Invoke(type, error);
            return AdShowResult.Fail(type, error);
        }

        var completion = AutoResetXTaskCompletionSource<AdShowResult>.Create();
        int id = RegisterPending(new TypedPending<AdShowResult>(completion, @event =>
            @event.Ok ? AdShowResult.Ok(type, @event.Reward) : AdShowResult.Fail(type, FailMessage(@event))));
        int posted = _native.Request(HarmonyOSAdsProtocol.BuildShow(id, type));
        if (posted != 0)
        {
            UnregisterPending(id);
            return AdShowResult.Fail(type, $"xengine_ads request post failed ({posted})");
        }
        return await completion.Task;
    }

    /// <summary>Bridge event sink — runs on the ArkTS main thread.</summary>
    private void HandleEvent(string json)
    {
        if (!HarmonyOSAdsProtocol.TryParseEvent(json, out HarmonyOSAdsEvent @event))
            return;

        // Only *-result events carry the request id that completes a pending operation;
        // state events never consume one, even if the glue echoes an id on them.
        IPendingRequest? pending = @event.Id >= 0 && @event.Type.EndsWith("-result", StringComparison.Ordinal)
            ? UnregisterPending(@event.Id)
            : null;
        switch (@event.Type)
        {
            case "load-result":
                if (pending is TypedPending<AdLoadResult> load)
                {
                    if (@event.Ok)
                    {
                        SetLoaded(ResolveAdType(@event), true);
                        Loaded?.Invoke(ResolveAdType(@event));
                    }
                    load.Complete(@event);
                }
                else
                    RaiseStateError(@event);
                break;
            case "show-result":
                if (pending is TypedPending<AdShowResult> show)
                    show.Complete(@event);
                else
                    RaiseStateError(@event);
                break;
            case "init-result" or "close-result" or "error":
                if (pending is VoidPending voidPending)
                    voidPending.Complete(@event);
                else if (pending is TypedPending<AdLoadResult> failedLoad && !@event.Ok)
                    failedLoad.Complete(@event);
                else if (pending is TypedPending<AdShowResult> failedShow && !@event.Ok)
                    failedShow.Complete(@event);
                else
                    RaiseStateError(@event);
                break;
            case "state" when @event.State == "show-start":
                // The ad is consumed the moment presentation opens, matching vendor behavior.
                if (@event.IsAdType(AdType.RewardVideo)) ClearLoaded(AdType.RewardVideo);
                if (@event.IsAdType(AdType.Interstitial)) ClearLoaded(AdType.Interstitial);
                ShowStart?.Invoke(ResolveAdType(@event));
                break;
            case "state" when @event.State == "show-end":
                ShowEnd?.Invoke(ResolveAdType(@event), @event.Reward);
                break;
        }
    }

    private void RaiseStateError(HarmonyOSAdsEvent @event)
    {
        if (!@event.Ok && @event.Id >= 0)
            Error?.Invoke(AdType.RewardVideo, @event.Error);
    }

    private static AdType ResolveAdType(HarmonyOSAdsEvent @event) =>
        @event.IsAdType(AdType.Interstitial) ? AdType.Interstitial : AdType.RewardVideo;

    private void SetLoaded(AdType type, bool value)
    {
        lock (_gate)
        {
            _loaded[TypeIndex(type)] = value;
        }
    }

    private void ClearLoaded(AdType type) => SetLoaded(type, false);

    private int RegisterPending(IPendingRequest pending)
    {
        int id = Interlocked.Increment(ref _nextId);
        lock (_gate)
        {
            _pending.Add(id, pending);
        }
        return id;
    }

    private IPendingRequest? UnregisterPending(int id)
    {
        lock (_gate)
        {
            return _pending.Remove(id, out IPendingRequest? pending) ? pending : null;
        }
    }

    private static string FailMessage(HarmonyOSAdsEvent @event) =>
        string.IsNullOrEmpty(@event.Error) ? "request failed" : @event.Error;

    private static int TypeIndex(AdType type) => type switch
    {
        AdType.RewardVideo => 0,
        AdType.Interstitial => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ad type."),
    };

    private interface IPendingRequest
    {
    }

    private sealed class VoidPending : IPendingRequest
    {
        private readonly AutoResetXTaskCompletionSource _completion;

        public VoidPending(AutoResetXTaskCompletionSource completion) => _completion = completion;

        public void Complete(HarmonyOSAdsEvent @event)
        {
            if (@event.Ok)
                _completion.TrySetResult();
            else
                _completion.TrySetException(new InvalidOperationException(
                    string.IsNullOrEmpty(@event.Error) ? "request failed" : @event.Error));
        }
    }

    private sealed class TypedPending<T> : IPendingRequest
    {
        private readonly AutoResetXTaskCompletionSource<T> _completion;
        private readonly Func<HarmonyOSAdsEvent, T> _buildResult;

        public TypedPending(AutoResetXTaskCompletionSource<T> completion, Func<HarmonyOSAdsEvent, T> buildResult)
        {
            _completion = completion;
            _buildResult = buildResult;
        }

        public void Complete(HarmonyOSAdsEvent @event) => _completion.TrySetResult(_buildResult(@event));
    }
}
