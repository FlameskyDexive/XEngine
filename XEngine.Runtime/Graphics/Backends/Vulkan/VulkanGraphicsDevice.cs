// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

using XEngine.Runtime.Platforms.HarmonyOS;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using VkCommandBuffer = Silk.NET.Vulkan.CommandBuffer;

namespace XEngine.Runtime.Backends.Vulkan;

/// <summary>
/// Vulkan <see cref="IGraphicsDevice"/> with instance/device/swapchain ownership and
/// per-submit command translation via <see cref="VulkanCommandTranslator"/>.
/// </summary>
public sealed unsafe class VulkanGraphicsDevice : IGraphicsDevice, IPresentationSurfaceLifecycle
{
    public const int MaxFramesInFlight = 2;
    // Never wait forever on a driver fence. A lost or wedged device can otherwise block the
    // editor's main loop (and, on some drivers, the desktop compositor) indefinitely.
    private const ulong GpuWaitTimeoutNanoseconds = 5_000_000_000UL;

    private readonly GraphicsDeviceOptions _options;
    private readonly Vk _vk;
    private readonly object _gate = new();

    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsQueueFamily;
    private uint _presentQueueFamily;
    private bool _queuesShared;

    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private PipelineCache _pipelineCache;
    private string? _pipelineCachePath;
    private int _pipelineCacheLoadedBytes;
    private int _pipelineCachePersistedBytes;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private bool[] _swapchainImageInitialized = Array.Empty<bool>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Framebuffer[] _swapchainFramebuffers = Array.Empty<Framebuffer>();
    private RenderPass _swapchainRenderPass;
    private RenderPass _swapchainLoadRenderPass;
    private Image _headlessImage;
    private DeviceMemory _headlessMemory;
    private ImageView _headlessView;
    private Framebuffer _headlessFramebuffer;
    private RenderPass _headlessRenderPass;

    private CommandPool _commandPool;
    // One-shot uploads / off-frame Execute() used to vkAllocate+vkFree a command buffer per
    // Texture2D.FromFile. Intel ICDs return VK_ERROR_OUT_OF_HOST_MEMORY from
    // vkBeginCommandBuffer after a few hundred of those in one editor tick (Icey G3).
    private CommandPool _transientCommandPool;
    private readonly Stack<VkCommandBuffer> _transientCommandFree = new();
    private int _transientCommandLive;
    private const int MaxCachedTransientCommandBuffers = 16;
    private const int MaxPendingRetireBeforeDrain = 8;
    private const uint DescriptorPoolCapacity = 4096;
    // A pathological UI frame can contain tens of thousands of state changes. Keep growth bounded
    // while leaving enough headroom for the editor's largest detached-surface frame.
    private const int MaxDescriptorPoolsPerFrame = 64;
    // Per-frame descriptor pools: one primary pool per frame-in-flight slot, with lazily-created
    // overflow pools for frames whose draw count exceeds the normal capacity. BeginFrame resets
    // every pool in the slot after its fence completes, so descriptor sets remain frame-owned.
    private readonly DescriptorPool[] _frameDescriptorPools = new DescriptorPool[MaxFramesInFlight];
    private readonly List<DescriptorPool>[] _frameDescriptorOverflowPools = CreateDescriptorPoolLists();
    private readonly int[] _currentDescriptorPoolIndices = new int[MaxFramesInFlight];
    private readonly VkCommandBuffer[] _frameCommandBuffers = new VkCommandBuffer[MaxFramesInFlight];
    private readonly VkCommandBuffer[] _timestampEndCommandBuffers = new VkCommandBuffer[MaxFramesInFlight];
    private readonly Fence[] _inFlightFences = new Fence[MaxFramesInFlight];
    private readonly Semaphore[] _imageAvailable = new Semaphore[MaxFramesInFlight];
    private Semaphore[] _renderFinished = Array.Empty<Semaphore>();

    private IWindowSurface? _windowSurface;
    private GraphicsDeviceCapabilities _capabilities = new() { BackendName = "Vulkan" };
    private readonly GraphicsDeviceLossState _deviceLoss = new();
    // VK_EXT_device_fault (Silk 2.23 ships the structs but not the command): resolved manually
    // so driver-side fault details can be captured the moment the device is lost.
    private delegate* unmanaged[Cdecl]<Device, DeviceFaultCounts*, DeviceFaultInfo*, Result> _getDeviceFaultInfo;
    private bool _deviceFaultCaptured;
    private VulkanCommandTranslator? _translator;
    private bool _initialized;
    private bool _shutdown;
    private int _currentFrame;
    private uint _currentImageIndex;
    private bool _frameBegun;
    private bool _frameAcquireConsumed;
    private bool _swapchainResizePending;
    private int _requestedResizeWidth = -1;
    private int _requestedResizeHeight = -1;
    private ulong _fenceValue;
    private int _suboptimalCount;
    private uint _nextHandle = 1;

    // Frame timestamps use two fixed query slots per in-flight frame. Query results are read only
    // after that slot's present fence retired on a later BeginFrame, never by stalling the frame.
    private QueryPool _timestampQueryPool;
    private float _timestampPeriodNanoseconds;
    private readonly bool[] _timestampFrameSubmitted = new bool[MaxFramesInFlight];
    private readonly ulong[] _timestampFrameIds = new ulong[MaxFramesInFlight];
    private ulong _nextTimestampFrameId;
    private GpuTimestampFrame _lastGpuTimestampFrame;
    private bool _hasGpuTimestampFrame;
    private const int MaxGpuCommandTimingsPerFrame = 16;
    private const int TimestampQueriesPerFrame = 2 + MaxGpuCommandTimingsPerFrame * 2;
    private readonly int[] _gpuCommandTimingCounts = new int[MaxFramesInFlight];
    private readonly string?[,] _gpuCommandTimingNames = new string?[MaxFramesInFlight, MaxGpuCommandTimingsPerFrame];
    private readonly PresentationSurfaceId[] _gpuCommandTimingSurfaces = new PresentationSurfaceId[MaxFramesInFlight];
    private readonly GpuCommandTimingBuffer _gpuCommandTimings = new();

    // B7b — occlusion (ANY_SAMPLES_PASSED-style) queries. A fixed slot ring per in-flight frame,
    // reset in the frame-start submit, recorded around probe draws at command-execute time and
    // published into a bounded CPU map once that frame slot's present fence retires.
    private const int OcclusionSlotsPerFrame = 16;
    private QueryPool _occlusionQueryPool;
    private readonly List<(ulong QueryId, uint AbsoluteSlot)>[] _occlusionPending =
        new List<(ulong, uint)>[MaxFramesInFlight];
    private readonly Dictionary<ulong, bool> _completedOcclusionQueries = new();
    private readonly Queue<ulong> _completedOcclusionOrder = new();
    private readonly object _occlusionGate = new();
    private int _occlusionSlotCursor;

    private readonly Dictionary<uint, VkBufferResource> _buffers = new();
    private readonly Dictionary<uint, VkImageResource> _images = new();
    private readonly Dictionary<uint, VkFramebufferResource> _framebuffers = new();
    private readonly Dictionary<uint, VkVertexArrayResource> _vertexArrays = new();
    private readonly Dictionary<uint, VkShaderResource> _shaders = new();
    private readonly Dictionary<int, VkShaderLayoutResource> _shaderLayouts = new();
    private readonly Dictionary<int, VkShaderModuleResource> _shaderModules = new();
    private readonly Dictionary<VkGraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
    private readonly Dictionary<int, Pipeline> _computePipelines = new();
    private int _pipelineCreateCount;
    private int _pipelineCacheHitCount;
    private readonly Dictionary<VkRenderTargetFormats, RenderPass> _pipelineRenderPasses = new();
    private readonly PresentationSurfaceRegistry _presentationSurfaceRegistry = new();
    private readonly Dictionary<PresentationSurfaceId, VulkanPresentationSurfaceState> _presentationSurfaces = new();
    private PresentationSurfaceId _activePresentationSurface;
    private PresentationSurfaceId _primaryPresentationSurface;

    public VulkanGraphicsDevice(GraphicsDeviceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        try
        {
            // Discover non-default loader locations (Homebrew) before the first Silk.NET call;
            // GetApi's internal name search cannot see them, so bind an explicit context.
            _vk = VulkanLoader.TryCreateExplicitContext() is { } loaderContext
                ? new Vk(loaderContext)
                : Vk.GetApi();
            // GetApi loads the Vulkan loader itself; on macOS/Linux without a system loader
            // (e.g. brew's /opt/homebrew/lib is not on dyld's default search path) this throws
            // before CreateInstance, so keep it inside the unavailable-machinery wrap.
            CreateInstance();
        }
        catch (Exception ex)
        {
            _vk?.Dispose();
            throw new InvalidOperationException(
                "Failed to create Vulkan instance. Vulkan is unavailable on this machine (missing ICD/driver or loader).",
                ex);
        }
    }

    public GraphicsBackend Backend => GraphicsBackend.Vulkan;
    public GraphicsDeviceCapabilities Capabilities => _capabilities;

    // Intel Arc drivers rasterize negative-height viewports with the opposite orientation to
    // NVIDIA/AMD; Android Mali/Maleoon surfaces are composited flipped, so they render upright
    // with the positive-height convention plus the matching winding and blit _FlipY compensation.
    // HarmonyOS is excluded from the mobile-driver match: its vkCreateSurfaceOHOS compositor does
    // NOT pre-flip like Android's, so a Mali-G610 there renders upright with the negative-height
    // convention (validated on device — positive convention rendered vertically mirrored).
    // Player builds only: the editor's chrome, GameView blit, and Paper/UI ortho projections
    // still assume the legacy negative-height convention on desktop (they are not
    // convention-aware yet), so the editor keeps the legacy orientation until those consumers
    // follow this flag.
    internal bool UsesPositiveViewportHeight =>
        Application.IsPlayerBuild &&
        UsesPositiveViewportHeightConvention(_capabilities.BackendName);

    internal static bool UsesPositiveViewportHeightConvention(string backendName)
    {
#if !XENGINE_HARMONYOS
        if (backendName.Contains("Mali", StringComparison.OrdinalIgnoreCase) ||
            backendName.Contains("Maleoon", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
#endif
        return backendName.Contains("Intel", StringComparison.OrdinalIgnoreCase);
    }
    public bool IsInitialized => _initialized;
    public bool IsDeviceLost => _deviceLoss.IsLost;
    public int DeviceLossCount => _deviceLoss.LossCount;
    public string LastDeviceLossOperation => _deviceLoss.LastOperation;

    /// <summary>Vendor fault blob from the last VK_EXT_device_fault capture; null when none was
    /// produced. Read by the editor crash handler so NVIDIA's parser can decode the dump offline.</summary>
    public static byte[]? LastDeviceFaultVendorBlob { get; internal set; }
    /// <summary>Driver-side description line from the last VK_EXT_device_fault capture.</summary>
    public static string LastDeviceFaultSummary { get; internal set; } = string.Empty;
    public int PipelineCreateCount => _pipelineCreateCount;
    public int PipelineCacheHitCount => _pipelineCacheHitCount;
    public int PipelineCacheLoadedBytes => _pipelineCacheLoadedBytes;
    public int PipelineCachePersistedBytes => _pipelineCachePersistedBytes;

    internal Vk Vk => _vk;
    internal Device Device => _device;
    internal PhysicalDevice PhysicalDevice => _physicalDevice;
    internal Queue GraphicsQueue => _graphicsQueue;
    internal uint GraphicsQueueFamily => _graphicsQueueFamily;
    internal CommandPool CommandPool => _commandPool;
    internal Format SwapchainFormat => _swapchainFormat;
    internal Extent2D SwapchainExtent => _swapchainExtent;
    internal RenderPass SwapchainRenderPass => _swapchainRenderPass;
    internal Framebuffer CurrentSwapchainFramebuffer =>
        _swapchainFramebuffers.Length > 0 ? _swapchainFramebuffers[_currentImageIndex] : default;
    internal Image CurrentSwapchainImage =>
        _swapchainImages.Length > 0 ? _swapchainImages[_currentImageIndex] : default;
    internal Image CurrentColorImage => HasSwapchain ? CurrentSwapchainImage : _headlessImage;
    internal bool HasSwapchain => _swapchain.Handle != 0;
    internal Format CurrentColorFormat => HasSwapchain ? _swapchainFormat : Format.R8G8B8A8Unorm;
    internal RenderPass CurrentRenderPass => HasSwapchain ? _swapchainLoadRenderPass : _headlessRenderPass;
    internal Framebuffer CurrentFramebuffer => HasSwapchain ? CurrentSwapchainFramebuffer : _headlessFramebuffer;
    internal Extent2D CurrentRenderExtent => HasSwapchain ? _swapchainExtent : new Extent2D(1, 1);

    internal Dictionary<uint, VkBufferResource> Buffers => _buffers;
    internal ulong UniformBufferOffsetAlignment { get; private set; } = 1;
    // Anisotropy is opt-in at logical-device creation time. Keep this at one until the
    // physical-device feature has been both reported and enabled.
    internal float MaxSamplerAnisotropy { get; private set; } = 1f;
    // Whether the logical device was created with fillModeNonSolid (wireframe debug view).
    private bool _fillModeNonSolid;
    internal Dictionary<uint, VkImageResource> Images => _images;
    internal int PendingSubmissionCount
    {
        get
        {
            lock (_gate)
                return _pendingRetire.Count;
        }
    }
    internal int PendingSamplerRetirementCount
    {
        get
        {
            lock (_gate)
            {
                int count = _unfencedSamplerRetire.Count;
                for (int i = 0; i < _pendingRetire.Count; i++)
                    count += _pendingRetire[i].Samplers.Count;
                return count;
            }
        }
    }
    internal Dictionary<uint, VkFramebufferResource> Framebuffers => _framebuffers;
    internal Dictionary<uint, VkVertexArrayResource> VertexArrays => _vertexArrays;
    internal Dictionary<uint, VkShaderResource> Shaders => _shaders;

    internal uint AllocateHandle() => System.Threading.Interlocked.Increment(ref _nextHandle) - 1;

    public void Initialize(IWindowSurface? surface)
    {
        if (_shutdown)
            throw new ObjectDisposedException(nameof(VulkanGraphicsDevice));
        if (_initialized)
            return;

        _windowSurface = surface;
        if (surface != null)
            CreateWindowSurface(surface);

        PickPhysicalDevice();

        CreateLogicalDevice();
        CreateCommandPoolAndSync();
        TryCreateTimestampQueryPool();
        TryCreateOcclusionQueryPool();
        CreateDescriptorPool();

        if (surface != null)
        {
            RecreateSwapchain();
        }
        else
        {
            CreateHeadlessRenderTarget();
        }

        QueryCapabilities();
        CreatePipelineCache();
        _translator = new VulkanCommandTranslator(this);
        _initialized = true;
        // Missing-binding fallbacks (white texture) must be created before any draw; runs after
        // _initialized so the translator can execute creation through this device.
        _translator.InitializeMissingBindingFallbacks();

        if (surface != null)
        {
            _primaryPresentationSurface = _presentationSurfaceRegistry.Create(surface);
            _activePresentationSurface = _primaryPresentationSurface;
            _presentationSurfaces.Add(_activePresentationSurface, CreatePresentationSurfaceState());
        }
    }

    /// <summary>Creates a new swapchain for a replacement platform surface.</summary>
    internal void AttachSurface(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_gate)
        {
            EnsureInitialized();
            if (_surface.Handle != 0)
                throw new InvalidOperationException("A Vulkan presentation surface is already attached.");

            _windowSurface = surface;
            CreateWindowSurface(surface);
            Bool32 supported = false;
            _khrSurface!.GetPhysicalDeviceSurfaceSupport(
                _physicalDevice,
                _presentQueueFamily,
                _surface,
                &supported);
            if (!supported)
            {
                DestroyWindowSurface();
                _windowSurface = null;
                throw new InvalidOperationException(
                    "The existing Vulkan present queue does not support the replacement Android surface.");
            }

            _swapchainResizePending = false;
            RecreateSwapchain();
            _primaryPresentationSurface = _presentationSurfaceRegistry.Create(surface);
            _activePresentationSurface = _primaryPresentationSurface;
            _presentationSurfaces.Add(_activePresentationSurface, CreatePresentationSurfaceState());
        }
    }

    /// <summary>Releases swapchain/WSI objects while preserving device-owned game resources.</summary>
    internal void DetachSurface()
    {
        lock (_gate)
        {
            if (!_initialized || _surface.Handle == 0)
                return;
            if (_frameBegun)
                throw new InvalidOperationException("Cannot detach a Vulkan surface during a frame.");

            WaitForGpuIdle("detach surface");
            DestroySwapchain();
            DestroyWindowSurface();
            _windowSurface = null;
            _swapchainResizePending = false;
            _currentFrame = 0;
            _currentImageIndex = 0;
            _frameAcquireConsumed = false;
            if (_activePresentationSurface.IsValid)
            {
                _presentationSurfaces.Remove(_activePresentationSurface);
                _presentationSurfaceRegistry.Destroy(_activePresentationSurface);
            }
            _activePresentationSurface = default;
            _primaryPresentationSurface = default;
        }
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;

        // vkDeviceWaitIdle has no timeout. Once the device is lost, calling it can hang forever;
        // tear down host-side handles directly and let vkDestroyDevice release the lost device.
        if (_device.Handle != 0 && !_deviceLoss.IsLost)
        {
            try { WaitForGpuIdle("shutdown"); }
            catch (InvalidOperationException) when (_deviceLoss.IsLost) { }
        }

        DestroyAllPresentationSurfaces();
        DestroyHeadlessRenderTarget();
        DestroySyncAndPool();
        DestroyDescriptorPool();
        if (_timestampQueryPool.Handle != 0)
        {
            _vk.DestroyQueryPool(_device, _timestampQueryPool, null);
            _timestampQueryPool = default;
        }
        if (_occlusionQueryPool.Handle != 0)
        {
            _vk.DestroyQueryPool(_device, _occlusionQueryPool, null);
            _occlusionQueryPool = default;
        }

        foreach (var kv in _buffers) DestroyBuffer(kv.Value);
        _buffers.Clear();
        foreach (var kv in _images) DestroyImage(kv.Value);
        _images.Clear();
        foreach (var kv in _framebuffers) DestroyFramebuffer(kv.Value);
        _framebuffers.Clear();
        _vertexArrays.Clear();
        foreach (var kv in _shaders) DestroyShader(kv.Value);
        _shaders.Clear();
        foreach (var kv in _graphicsPipelines)
            _vk.DestroyPipeline(_device, kv.Value, null);
        _graphicsPipelines.Clear();
        foreach (var kv in _computePipelines)
            _vk.DestroyPipeline(_device, kv.Value, null);
        _computePipelines.Clear();
        foreach (var kv in _shaderLayouts) DestroyShaderLayout(kv.Value);
        _shaderLayouts.Clear();
        foreach (var kv in _shaderModules) DestroyShaderModules(kv.Value);
        _shaderModules.Clear();
        foreach (var kv in _pipelineRenderPasses)
            _vk.DestroyRenderPass(_device, kv.Value, null);
        _pipelineRenderPasses.Clear();

        if (_device.Handle != 0)
        {
            if (_pipelineCache.Handle != 0)
            {
                PersistPipelineCache();
                _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                _pipelineCache = default;
            }
            _vk.DestroyDevice(_device, null);
            _device = default;
        }

        if (_debugMessenger.Handle != 0 && _debugUtils != null)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            _debugMessenger = default;
        }

        DestroyWindowSurface();
        _khrSwapchain?.Dispose();
        _khrSwapchain = null;
        _khrSurface?.Dispose();
        _khrSurface = null;
        _debugUtils?.Dispose();
        _debugUtils = null;

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
        }

        _vk.Dispose();
        _initialized = false;
    }

    private VulkanPresentationSurfaceState CreatePresentationSurfaceState()
    {
        var state = new VulkanPresentationSurfaceState();
        CapturePresentationSurfaceState(state);
        return state;
    }

    private void CapturePresentationSurfaceState(VulkanPresentationSurfaceState state)
    {
        state.WindowSurface = _windowSurface;
        state.Surface = _surface;
        state.Swapchain = _swapchain;
        state.SwapchainFormat = _swapchainFormat;
        state.SwapchainExtent = _swapchainExtent;
        state.SwapchainImages = _swapchainImages;
        state.SwapchainImageInitialized = _swapchainImageInitialized;
        state.RenderFinished = _renderFinished;
        state.SwapchainImageViews = _swapchainImageViews;
        state.SwapchainFramebuffers = _swapchainFramebuffers;
        state.SwapchainRenderPass = _swapchainRenderPass;
        state.SwapchainLoadRenderPass = _swapchainLoadRenderPass;
        state.CurrentImageIndex = _currentImageIndex;
        state.SwapchainResizePending = _swapchainResizePending;
        state.RequestedResizeWidth = _requestedResizeWidth;
        state.RequestedResizeHeight = _requestedResizeHeight;
        state.SuboptimalCount = _suboptimalCount;
    }

    private void StoreActivePresentationSurface()
    {
        if (_activePresentationSurface.IsValid
            && _presentationSurfaces.TryGetValue(_activePresentationSurface, out VulkanPresentationSurfaceState? state))
            CapturePresentationSurfaceState(state);
    }

    private void ActivatePresentationSurface(PresentationSurfaceId id)
    {
        if (_activePresentationSurface == id)
            return;
        if (!_presentationSurfaces.TryGetValue(id, out VulkanPresentationSurfaceState? state))
            throw new KeyNotFoundException($"Unknown Vulkan presentation surface {id.Value}.");

        StoreActivePresentationSurface();
        ClearActivePresentationSurface();
        LoadPresentationSurfaceState(state);
        _activePresentationSurface = id;
    }

    private void ClearActivePresentationSurface()
    {
        _windowSurface = null;
        _surface = default;
        _swapchain = default;
        _swapchainFormat = default;
        _swapchainExtent = default;
        _swapchainImages = Array.Empty<Image>();
        _swapchainImageInitialized = Array.Empty<bool>();
        _renderFinished = Array.Empty<Semaphore>();
        _swapchainImageViews = Array.Empty<ImageView>();
        _swapchainFramebuffers = Array.Empty<Framebuffer>();
        _swapchainRenderPass = default;
        _swapchainLoadRenderPass = default;
        _currentImageIndex = 0;
        _swapchainResizePending = false;
        _requestedResizeWidth = -1;
        _requestedResizeHeight = -1;
        _suboptimalCount = 0;
    }

    private void LoadPresentationSurfaceState(VulkanPresentationSurfaceState state)
    {
        _windowSurface = state.WindowSurface;
        _surface = state.Surface;
        _swapchain = state.Swapchain;
        _swapchainFormat = state.SwapchainFormat;
        _swapchainExtent = state.SwapchainExtent;
        _swapchainImages = state.SwapchainImages;
        _swapchainImageInitialized = state.SwapchainImageInitialized;
        _renderFinished = state.RenderFinished;
        _swapchainImageViews = state.SwapchainImageViews;
        _swapchainFramebuffers = state.SwapchainFramebuffers;
        _swapchainRenderPass = state.SwapchainRenderPass;
        _swapchainLoadRenderPass = state.SwapchainLoadRenderPass;
        _currentImageIndex = state.CurrentImageIndex;
        _swapchainResizePending = state.SwapchainResizePending;
        _requestedResizeWidth = state.RequestedResizeWidth;
        _requestedResizeHeight = state.RequestedResizeHeight;
        _suboptimalCount = state.SuboptimalCount;
    }

    private void DestroyAllPresentationSurfaces()
    {
        if (_presentationSurfaces.Count == 0)
        {
            DestroySwapchain();
            return;
        }

        List<PresentationSurfaceId> ids = new(_presentationSurfaces.Keys);
        for (int i = 0; i < ids.Count; i++)
        {
            PresentationSurfaceId id = ids[i];
            if (_activePresentationSurface != id)
            {
                ClearActivePresentationSurface();
                LoadPresentationSurfaceState(_presentationSurfaces[id]);
                _activePresentationSurface = id;
            }
            DestroySwapchain();
            DestroyWindowSurface();
        }

        _presentationSurfaces.Clear();
        _presentationSurfaceRegistry.Clear();
        _activePresentationSurface = default;
        _primaryPresentationSurface = default;
        ClearActivePresentationSurface();
    }

    private sealed class VulkanPresentationSurfaceState
    {
        public IWindowSurface? WindowSurface;
        public SurfaceKHR Surface;
        public SwapchainKHR Swapchain;
        public Format SwapchainFormat;
        public Extent2D SwapchainExtent;
        public Image[] SwapchainImages = Array.Empty<Image>();
        public bool[] SwapchainImageInitialized = Array.Empty<bool>();
        public Semaphore[] RenderFinished = Array.Empty<Semaphore>();
        public ImageView[] SwapchainImageViews = Array.Empty<ImageView>();
        public Framebuffer[] SwapchainFramebuffers = Array.Empty<Framebuffer>();
        public RenderPass SwapchainRenderPass;
        public RenderPass SwapchainLoadRenderPass;
        public uint CurrentImageIndex;
        public bool SwapchainResizePending;
        public int RequestedResizeWidth = -1;
        public int RequestedResizeHeight = -1;
        public int SuboptimalCount;
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            // Generic frames always belong to the application's primary window. Detached editor
            // surfaces use BeginFrame(surface) and leave their swapchain active after presenting.
            // Switch back here so the next editor frame cannot render into the last detached host.
            if (_primaryPresentationSurface.IsValid
                && _presentationSurfaces.ContainsKey(_primaryPresentationSurface))
                ActivatePresentationSurface(_primaryPresentationSurface);
            BeginFrameLocked();
        }
    }

    public bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
    {
        lock (_gate)
        {
            surface = _primaryPresentationSurface;
            return surface.IsValid && _presentationSurfaces.ContainsKey(surface);
        }
    }

    public PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        lock (_gate)
        {
            EnsureInitialized();
            if (_frameBegun)
                throw new InvalidOperationException("Cannot create a Vulkan presentation surface during a frame.");
            if (_khrSurface == null || _khrSwapchain == null)
                throw new NotSupportedException("Vulkan presentation surface extensions are unavailable.");

            PresentationSurfaceId previous = _activePresentationSurface;
            StoreActivePresentationSurface();
            _activePresentationSurface = default;
            ClearActivePresentationSurface();
            PresentationSurfaceId id = _presentationSurfaceRegistry.Create(surface);
            try
            {
                _windowSurface = surface;
                CreateWindowSurface(surface);
                Bool32 supported = false;
                _khrSurface.GetPhysicalDeviceSurfaceSupport(
                    _physicalDevice, _presentQueueFamily, _surface, &supported);
                if (!supported)
                    throw new NotSupportedException("The Vulkan present queue does not support this surface.");

                RecreateSwapchain();

                _presentationSurfaces.Add(id, CreatePresentationSurfaceState());
                _activePresentationSurface = id;
                if (previous.IsValid)
                {
                    _activePresentationSurface = default;
                    ClearActivePresentationSurface();
                    ActivatePresentationSurface(previous);
                }
                return id;
            }
            catch
            {
                DestroySwapchain();
                DestroyWindowSurface();
                _presentationSurfaceRegistry.Destroy(id);
                _activePresentationSurface = default;
                ClearActivePresentationSurface();
                if (previous.IsValid)
                    ActivatePresentationSurface(previous);
                throw;
            }
        }
    }

    public bool DestroyPresentationSurface(PresentationSurfaceId surface)
    {
        lock (_gate)
        {
            if (!_presentationSurfaceRegistry.TryGet(surface, out _)
                || !_presentationSurfaces.ContainsKey(surface)
                || (_frameBegun && _activePresentationSurface == surface))
                return false;

            PresentationSurfaceId previous = _activePresentationSurface;
            StoreActivePresentationSurface();
            _activePresentationSurface = default;
            ClearActivePresentationSurface();
            ActivatePresentationSurface(surface);
            WaitForGpuIdle("destroy presentation surface");
            DestroySwapchain();
            DestroyWindowSurface();
            _presentationSurfaces.Remove(surface);
            _presentationSurfaceRegistry.Destroy(surface);
            if (_primaryPresentationSurface == surface)
                _primaryPresentationSurface = default;

            if (previous.IsValid && previous != surface)
            {
                _activePresentationSurface = default;
                ClearActivePresentationSurface();
                ActivatePresentationSurface(previous);
            }
            else if (_presentationSurfaces.Count > 0)
            {
                PresentationSurfaceId fallback = default;
                foreach (PresentationSurfaceId candidate in _presentationSurfaces.Keys)
                {
                    fallback = candidate;
                    break;
                }
                _activePresentationSurface = default;
                ClearActivePresentationSurface();
                ActivatePresentationSurface(fallback);
            }
            return true;
        }
    }

    public bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
        lock (_gate)
        {
            if (!_presentationSurfaceRegistry.TryGet(surface, out _)
                || !_presentationSurfaces.ContainsKey(surface)
                || (_frameBegun && _activePresentationSurface != surface))
                return false;

            PresentationSurfaceId previous = _activePresentationSurface;
            if (previous != surface)
            {
                ActivatePresentationSurface(surface);
            }
            try
            {
                Resize(width, height);
                _presentationSurfaceRegistry.Resize(surface, width, height);
                StoreActivePresentationSurface();
                return true;
            }
            finally
            {
                if (previous.IsValid && previous != surface)
                {
                    _activePresentationSurface = default;
                    ClearActivePresentationSurface();
                    ActivatePresentationSurface(previous);
                }
            }
        }
    }

    public bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info)
    {
        lock (_gate)
            return _presentationSurfaceRegistry.TryGet(surface, out info);
    }

    public bool BeginFrame(PresentationSurfaceId surface)
    {
        lock (_gate)
        {
            if (!_presentationSurfaceRegistry.Begin(surface))
                return false;
            try
            {
                if (_frameBegun)
                    throw new InvalidOperationException("A Vulkan frame is already active.");
                ActivatePresentationSurface(surface);
                BeginFrameLocked();
                return true;
            }
            catch
            {
                _presentationSurfaceRegistry.End(surface);
                throw;
            }
        }
    }

    public bool EndFrame(PresentationSurfaceId surface)
    {
        lock (_gate)
        {
            if (_activePresentationSurface != surface || !_frameBegun)
                return false;
            EndFrameLocked();
            return _presentationSurfaceRegistry.End(surface);
        }
    }

    internal double LastFrameFenceWaitMs { get; private set; }
    internal double LastSwapchainAcquireMs { get; private set; }

    private void BeginFrameLocked()
    {
        EnsureInitialized();
        ApplyPendingResize();
        RetireCompletedSubmissions();
        if (!HasSwapchain)
        {
            _frameBegun = true;
            return;
        }

        Fence fence = _inFlightFences[_currentFrame];
        long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        WaitForFence(fence, "vkWaitForFences(frame)");
        LastFrameFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        PublishTimestampFrame(_currentFrame);
        PublishOcclusionResults(_currentFrame);

        waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Result acquire = _khrSwapchain!.AcquireNextImage(
            _device, _swapchain, GpuWaitTimeoutNanoseconds, _imageAvailable[_currentFrame], default, ref _currentImageIndex);
        LastSwapchainAcquireMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        ThrowIfDeviceLost(acquire, "vkAcquireNextImageKHR");

        if (acquire == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            acquire = _khrSwapchain.AcquireNextImage(
                _device, _swapchain, GpuWaitTimeoutNanoseconds, _imageAvailable[_currentFrame], default, ref _currentImageIndex);
            ThrowIfDeviceLost(acquire, "vkAcquireNextImageKHR(retry)");
        }

        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
        {
            if (acquire == Result.Timeout)
            {
                _deviceLoss.RecordLoss("vkAcquireNextImageKHR", acquire.ToString());
                throw new InvalidOperationException(
                    "vkAcquireNextImageKHR timed out; Vulkan is no longer responding.");
            }
            throw new InvalidOperationException($"vkAcquireNextImageKHR failed: {acquire}");
        }

        // A suboptimal image is usable for this frame, but the surface no longer exactly matches
        // the swapchain. Recreate before the following frame instead of repeatedly presenting a
        // stale image, which manifests as an intermittent flash on resize/DPI transitions.
        if (acquire == Result.SuboptimalKhr)
            _swapchainResizePending = true;

        _vk.ResetFences(_device, 1, &fence);

        // Recycle descriptor sets from the previous use of this frame slot. With 2 frames in
        // flight and the frame-end fence waited above, the previous use of this slot's GPU work
        // is guaranteed complete (its Execute submissions are ordered before the SubmitEmpty that
        // signaled this fence, so FIFO queue ordering ensures they finished first).
        ResetDescriptorPools(_currentFrame);
        RetireFrameRecordingSlot(_currentFrame);

        _frameAcquireConsumed = false;
        _frameBegun = true;
        SubmitTimestampFrameStart(_currentFrame);
        StartFrameRecording();
        if (!_swapchainImageInitialized[_currentImageIndex])
        {
            // Startup/hidden views may submit no color pass. Even those acquired images
            // must have a legal PRESENT layout before QueuePresent, never UNDEFINED.
            var barrier = CreateImageBarrier(_swapchainImages[_currentImageIndex],
                ImageLayout.Undefined, ImageLayout.PresentSrcKhr, 0, 0, 0, 0);
            _vk.CmdPipelineBarrier(_frameCmd, PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.ColorAttachmentOutputBit, 0, 0, null, 0, null, 1, &barrier);
            _swapchainImageInitialized[_currentImageIndex] = true;
        }
    }

    public void EndFrame()
    {
        lock (_gate)
            EndFrameLocked();
    }

    private void EndFrameLocked()
    {
        EnsureInitialized();
        if (!_frameBegun)
            return;
        _frameBegun = false;

        if (!HasSwapchain)
        {
            _fenceValue++;
            return;
        }

        // Submit the frame's batched recording, then the terminal frame-end submit.
        if (_frameCmdActive)
            SubmitFrameRecording();

        // A frame fence covers rendering, not the presentation engine's semaphore wait.
        // Re-acquiring this IMAGE proves its previous present wait finished.
        var waitSem = _renderFinished[_currentImageIndex];
        // The terminal submit writes the end timestamp after every render submission and signals
        // the same presentation fence. If timestamps are unavailable it remains an empty submit.
        SubmitFrameEnd(
            _frameAcquireConsumed ? default : _imageAvailable[_currentFrame],
            waitSem,
            _inFlightFences[_currentFrame],
            _currentFrame);

        var swapchain = _swapchain;
        uint imageIndex = _currentImageIndex;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSem,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        Result present = _khrSwapchain!.QueuePresent(_presentQueue, &presentInfo);
        ThrowIfDeviceLost(present, "vkQueuePresentKHR");
        if (present == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
        }
        else if (present == Result.SuboptimalKhr)
        {
            _suboptimalCount++;
            _swapchainResizePending = true;
            if (_suboptimalCount % 60 == 1)
                Debug.LogWarning($"Vulkan present returned SuboptimalKhr ({_suboptimalCount} times); scheduling swapchain recreation.");
        }
        else if (present != Result.Success)
            Debug.LogWarning($"vkQueuePresentKHR failed: {present}");

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
        _fenceValue++;
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            if (width < 0 || height < 0 || _surface.Handle == 0)
                return;

            _requestedResizeWidth = width;
            _requestedResizeHeight = height;
            _swapchainResizePending = true;
            if (_initialized && !_frameBegun)
                ApplyPendingResize();
        }
    }

    public void Execute(global::XEngine.Runtime.CommandBuffer commandBuffer, bool wait)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        if (commandBuffer._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");
        EnsureInitialized();

        commandBuffer._submitted = true;
        commandBuffer._ownerReleased = true;

        lock (_gate)
        {
            if (commandBuffer.PresentationSurface.IsValid
                && (_activePresentationSurface != commandBuffer.PresentationSurface || !_frameBegun))
                throw new InvalidOperationException(
                    "A command buffer presentation target must be the surface active in BeginFrame(surface).");
            if (_frameCmdActive)
            {
                int timingSlot = BeginGpuCommandTiming(commandBuffer, _frameCmd);
                _translator!.Translate(commandBuffer, _frameCmd, _frameCmdSamplers, _frameCmdBuffers, _frameCmdImages, _frameCmdFramebuffers, preserveUniformArena: true);
                if (timingSlot >= 0)
                    _vk.CmdWriteTimestamp(_frameCmd, PipelineStageFlags.BottomOfPipeBit, _timestampQueryPool,
                        (uint)(_currentFrame * TimestampQueriesPerFrame + 2 + timingSlot * 2 + 1));
                if (wait)
                    FlushFrameRecordingAndWait();
                goto frameRecorded;
            }

            if (commandBuffer.CaptureGpuTiming && _timestampQueryPool.Handle != 0)
                _gpuCommandTimings.RecordDropped(1); // Only fenced, frame-batched intervals are supported.
            VkCommandBuffer vkCmd = AllocateTransientCommandBuffer();
            List<Sampler> retiredSamplers = RentSamplerList();
            List<VkBufferResource> transientBuffers = RentTransientBufferList();
            List<VkImageResource> transientImages = RentTransientImageList();
            List<VkFramebufferResource> transientFramebuffers = RentTransientFramebufferList();
            Fence fence = default;
            bool submitted = false;
            try
            {
                BeginOneTimeCommandBuffer(vkCmd, "vkBeginCommandBuffer");
                try
                {
                    _translator!.Translate(commandBuffer, vkCmd, retiredSamplers, transientBuffers, transientImages, transientFramebuffers);
                }
                finally
                {
                    Check(_vk.EndCommandBuffer(vkCmd), "vkEndCommandBuffer");
                }

                fence = CreateFence(signaled: false);
                bool waitForAcquire = _frameBegun && HasSwapchain && !_frameAcquireConsumed;
                Semaphore acquireSemaphore = waitForAcquire ? _imageAvailable[_currentFrame] : default;
                PipelineStageFlags acquireStage = PipelineStageFlags.ColorAttachmentOutputBit;
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    WaitSemaphoreCount = waitForAcquire ? 1u : 0u,
                    PWaitSemaphores = waitForAcquire ? &acquireSemaphore : null,
                    PWaitDstStageMask = waitForAcquire ? &acquireStage : null,
                    CommandBufferCount = 1,
                    PCommandBuffers = &vkCmd,
                };
                CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit");
                submitted = true;
                if (waitForAcquire)
                    _frameAcquireConsumed = true;

                if (wait)
                {
                    WaitForFence(fence, "vkWaitForFences");
                    _vk.DestroyFence(_device, fence, null);
                    FreeTransientCommandBuffer(vkCmd);
                    // Descriptor sets are NOT freed here — the per-frame pool is reset atomically
                    // in BeginFrame via vkResetDescriptorPool.
                    DestroySamplers(retiredSamplers);
                    ReturnSamplerList(retiredSamplers);
                    DestroyTransientBuffers(transientBuffers);
                    ReturnTransientBufferList(transientBuffers);
                    DestroyTransientImages(transientImages);
                    ReturnTransientImageList(transientImages);
                    DestroyTransientFramebuffers(transientFramebuffers);
                    ReturnTransientFramebufferList(transientFramebuffers);
                    _fenceValue++;
                }
                else
                {
                    // Retire asynchronously on next WaitIdle / BeginFrame fence wait.
                    _pendingRetire.Add((vkCmd, fence, retiredSamplers, transientBuffers, transientImages, transientFramebuffers));
                }
            }
            catch
            {
                if (!submitted)
                {
                    if (fence.Handle != 0)
                        _vk.DestroyFence(_device, fence, null);
                    FreeTransientCommandBuffer(vkCmd);
                    // No descriptor-set cleanup needed (pool reset handles it).
                    _unfencedSamplerRetire.AddRange(retiredSamplers);
                    ReturnSamplerList(retiredSamplers);
                    DestroyTransientBuffers(transientBuffers);
                    ReturnTransientBufferList(transientBuffers);
                    DestroyTransientImages(transientImages);
                    ReturnTransientImageList(transientImages);
                    DestroyTransientFramebuffers(transientFramebuffers);
                    ReturnTransientFramebufferList(transientFramebuffers);
                }
                throw;
            }

        frameRecorded: ;
        }

        CommandBufferPool.Return(commandBuffer);
    }

    private readonly List<(VkCommandBuffer Cmd, Fence Fence, List<Sampler> Samplers, List<VkBufferResource> Buffers, List<VkImageResource> Images, List<VkFramebufferResource> Framebuffers)> _pendingRetire = new();

    // Frame-batched recording (P2.3): while a swapchain frame is begun, Execute translates every
    // engine CommandBuffer into one Vulkan command buffer and the frame ends with a single
    // vkQueueSubmit (previously each engine CB cost its own submit + fence + transient CB --
    // ~25-50 vkQueueSubmit per frame). Per-slot retirements are disposed at the next BeginFrame
    // of the slot, after its in-flight fence (signaled by the frame-end submit; FIFO queue order
    // guarantees the recording completed first) has been waited.
    private VkCommandBuffer _frameCmd;
    private bool _frameCmdActive;
    private List<Sampler> _frameCmdSamplers = null!;
    private List<VkBufferResource> _frameCmdBuffers = null!;
    private List<VkImageResource> _frameCmdImages = null!;
    private List<VkFramebufferResource> _frameCmdFramebuffers = null!;
    private readonly (VkCommandBuffer Cmd, List<Sampler> Samplers, List<VkBufferResource> Buffers, List<VkImageResource> Images, List<VkFramebufferResource> Framebuffers)?[] _frameRecordingRetire
        = new (VkCommandBuffer, List<Sampler>, List<VkBufferResource>, List<VkImageResource>, List<VkFramebufferResource>)?[MaxFramesInFlight];

    private void StartFrameRecording()
    {
        _frameCmd = AllocateTransientCommandBuffer();
        _frameCmdSamplers = RentSamplerList();
        _frameCmdBuffers = RentTransientBufferList();
        _frameCmdImages = RentTransientImageList();
        _frameCmdFramebuffers = RentTransientFramebufferList();
        BeginOneTimeCommandBuffer(_frameCmd, "vkBeginCommandBuffer(frame)");
        // Fresh recording session: the previous session's uniform arena retires with its frame
        // lists, so the translator must not keep writing into it.
        _translator?.ResetTransientUniformArena();
        _frameCmdActive = true;
    }

    /// <summary>Submit the recorded frame work without a fence; the caller's frame-end submit
    /// signals the in-flight fence and FIFO ordering covers this recording.</summary>
    private void SubmitFrameRecording()
    {
        _frameCmdActive = false;
        Check(_vk.EndCommandBuffer(_frameCmd), "vkEndCommandBuffer(frame)");

        bool waitForAcquire = HasSwapchain && !_frameAcquireConsumed;
        Semaphore acquireSemaphore = waitForAcquire ? _imageAvailable[_currentFrame] : default;
        PipelineStageFlags acquireStage = PipelineStageFlags.ColorAttachmentOutputBit;
        VkCommandBuffer cmd = _frameCmd;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = waitForAcquire ? 1u : 0u,
            PWaitSemaphores = waitForAcquire ? &acquireSemaphore : null,
            PWaitDstStageMask = waitForAcquire ? &acquireStage : null,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, default), "vkQueueSubmit(frame)");
        if (waitForAcquire)
            _frameAcquireConsumed = true;

        _frameRecordingRetire[_currentFrame] = (_frameCmd, _frameCmdSamplers, _frameCmdBuffers, _frameCmdImages, _frameCmdFramebuffers);
    }

    /// <summary>Flush-and-wait inside a frame (Execute with wait: readbacks/screenshots), then
    /// reopen a fresh recording for the rest of the frame.</summary>
    private void FlushFrameRecordingAndWait()
    {
        _frameCmdActive = false;
        Check(_vk.EndCommandBuffer(_frameCmd), "vkEndCommandBuffer(frame flush)");

        Fence fence = CreateFence(signaled: false);
        bool waitForAcquire = HasSwapchain && !_frameAcquireConsumed;
        Semaphore acquireSemaphore = waitForAcquire ? _imageAvailable[_currentFrame] : default;
        PipelineStageFlags acquireStage = PipelineStageFlags.ColorAttachmentOutputBit;
        VkCommandBuffer cmd = _frameCmd;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = waitForAcquire ? 1u : 0u,
            PWaitSemaphores = waitForAcquire ? &acquireSemaphore : null,
            PWaitDstStageMask = waitForAcquire ? &acquireStage : null,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit(frame flush)");
        if (waitForAcquire)
            _frameAcquireConsumed = true;

        WaitForFence(fence, "vkWaitForFences(frame flush)");
        _vk.DestroyFence(_device, fence, null);
        FreeTransientCommandBuffer(_frameCmd);
        DestroySamplers(_frameCmdSamplers);
        ReturnSamplerList(_frameCmdSamplers);
        DestroyTransientBuffers(_frameCmdBuffers);
        ReturnTransientBufferList(_frameCmdBuffers);
        DestroyTransientImages(_frameCmdImages);
        ReturnTransientImageList(_frameCmdImages);
        DestroyTransientFramebuffers(_frameCmdFramebuffers);
        ReturnTransientFramebufferList(_frameCmdFramebuffers);
        _fenceValue++;

        StartFrameRecording();
    }

    private void RetireFrameRecordingSlot(int slot)
    {
        if (_frameRecordingRetire[slot] is not { } recording)
            return;
        _frameRecordingRetire[slot] = null;
        FreeTransientCommandBuffer(recording.Cmd);
        DestroySamplers(recording.Samplers);
        ReturnSamplerList(recording.Samplers);
        DestroyTransientBuffers(recording.Buffers);
        ReturnTransientBufferList(recording.Buffers);
        DestroyTransientImages(recording.Images);
        ReturnTransientImageList(recording.Images);
        DestroyTransientFramebuffers(recording.Framebuffers);
        ReturnTransientFramebufferList(recording.Framebuffers);
    }
    private readonly List<Sampler> _unfencedSamplerRetire = new();
    private readonly Stack<List<Sampler>> _samplerListPool = new();
    private readonly Stack<List<VkBufferResource>> _transientBufferListPool = new();
    private readonly Stack<List<VkImageResource>> _transientImageListPool = new();
    private readonly Stack<List<VkFramebufferResource>> _transientFramebufferListPool = new();

    public void WaitIdle()
    {
        lock (_gate)
        {
            if (!_initialized || _device.Handle == 0)
                return;
            if (_deviceLoss.IsLost)
                return;
            WaitForGpuIdle("wait idle");
            RetirePending();
        }
    }

    public ulong GetFenceValue() => _fenceValue;

    public bool TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame)
    {
        frame = _lastGpuTimestampFrame;
        return _hasGpuTimestampFrame;
    }

    public bool SupportsGpuCommandTimings => _timestampQueryPool.Handle != 0;
    public long DroppedGpuCommandTimings => _gpuCommandTimings.Dropped;
    public bool TryDequeueGpuCommandTiming(out GpuCommandTiming timing) => _gpuCommandTimings.TryRead(out timing);

    private int BeginGpuCommandTiming(global::XEngine.Runtime.CommandBuffer buffer, VkCommandBuffer command)
    {
        if (!buffer.CaptureGpuTiming || _timestampQueryPool.Handle == 0 || !_timestampFrameSubmitted[_currentFrame]) return -1;
        int slot = _gpuCommandTimingCounts[_currentFrame];
        if (slot == MaxGpuCommandTimingsPerFrame) { _gpuCommandTimings.RecordDropped(1); return -1; }
        _gpuCommandTimingCounts[_currentFrame] = slot + 1;
        _gpuCommandTimingNames[_currentFrame, slot] = buffer.Name ?? string.Empty;
        _vk.CmdWriteTimestamp(command, PipelineStageFlags.TopOfPipeBit, _timestampQueryPool,
            (uint)(_currentFrame * TimestampQueriesPerFrame + 2 + slot * 2));
        return slot;
    }

    public void WaitFence(ulong fenceValue)
    {
        if (fenceValue > _fenceValue)
            WaitIdle();
    }

    public void Dispose() => Shutdown();

    // ─────────────────────── Resource helpers used by translator ───────────────────────

    internal uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }
        throw new InvalidOperationException("Failed to find suitable Vulkan memory type.");
    }

    internal void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags memProps,
        out Buffer buffer, out DeviceMemory memory)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Check(_vk.CreateBuffer(_device, &info, null, out buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements reqs);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = reqs.Size,
            MemoryTypeIndex = FindMemoryType(reqs.MemoryTypeBits, memProps),
        };
        AllocateDeviceMemory(in alloc, out memory, "vkAllocateMemory");
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
    }

    internal void UploadTexture2D(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint width,
        uint height,
        int bytesPerPixel) =>
        UploadTexture(resource, data, width, height, 1, bytesPerPixel);

    internal void UploadCompressedTexture2D(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint width,
        uint height,
        int bytesPerBlock,
        uint mipLevel = 0,
        ImageLayout oldLayout = ImageLayout.Undefined,
        bool setResourceLayout = true) =>
        UploadTexture(
            resource,
            data,
            width,
            height,
            1,
            bytesPerBlock,
            mipLevel: mipLevel,
            oldLayout: oldLayout,
            setResourceLayout: setResourceLayout,
            blockWidth: 4,
            blockHeight: 4);

    internal void UploadTexture3D(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint width,
        uint height,
        uint depth,
        int bytesPerPixel) =>
        UploadTexture(resource, data, width, height, depth, bytesPerPixel);

    internal void UploadTexture2DArray(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint width,
        uint height,
        uint layers,
        int bytesPerPixel)
    {
        int sliceSize = checked((int)(width * height * (uint)bytesPerPixel));
        int expectedSize = checked(sliceSize * (int)layers);
        if (data.Length != expectedSize)
            throw new ArgumentException($"Vulkan texture-array upload expected {expectedSize} bytes, got {data.Length}.", nameof(data));

        for (uint layer = 0; layer < layers; layer++)
        {
            ReadOnlySpan<byte> slice = data.Slice(checked((int)layer * sliceSize), sliceSize);
            UploadTexture(
                resource,
                slice,
                width,
                height,
                1,
                bytesPerPixel,
                baseArrayLayer: layer,
                setResourceLayout: layer + 1 == layers);
        }
    }

    internal void UploadTextureCubeFace(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint size,
        uint face,
        uint mipLevel,
        ImageLayout oldLayout,
        int bytesPerPixel) =>
        UploadTexture(resource, data, size, size, 1, bytesPerPixel, face, mipLevel, oldLayout, setResourceLayout: false);

    private void UploadTexture(
        VkImageResource resource,
        ReadOnlySpan<byte> data,
        uint width,
        uint height,
        uint depth,
        int bytesPerPixel,
        uint baseArrayLayer = 0,
        uint mipLevel = 0,
        ImageLayout oldLayout = ImageLayout.Undefined,
        bool setResourceLayout = true,
        uint blockWidth = 1,
        uint blockHeight = 1)
    {
        uint blocksWide = (width + blockWidth - 1) / blockWidth;
        uint blocksHigh = (height + blockHeight - 1) / blockHeight;
        int expectedSize = checked((int)(blocksWide * blocksHigh * depth * (uint)bytesPerPixel));
        if (data.Length != expectedSize)
            throw new ArgumentException($"Vulkan texture upload expected {expectedSize} bytes, got {data.Length}.", nameof(data));

        CreateBuffer(
            (ulong)data.Length,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer stagingBuffer,
            out DeviceMemory stagingMemory);
        try
        {
            void* mapped;
            Check(_vk.MapMemory(_device, stagingMemory, 0, (ulong)data.Length, 0, &mapped), "vkMapMemory");
            try
            {
                fixed (byte* source = data)
                    System.Buffer.MemoryCopy(source, mapped, data.Length, data.Length);
            }
            finally
            {
                _vk.UnmapMemory(_device, stagingMemory);
            }

            VkCommandBuffer commandBuffer = AllocateTransientCommandBuffer();
            Fence fence = default;
            try
            {
                BeginOneTimeCommandBuffer(commandBuffer, "vkBeginCommandBuffer");

                ImageMemoryBarrier toTransfer = CreateImageBarrier(
                    resource.Image,
                    oldLayout,
                    ImageLayout.TransferDstOptimal,
                    oldLayout == ImageLayout.ShaderReadOnlyOptimal ? AccessFlags.ShaderReadBit : 0,
                    AccessFlags.TransferWriteBit,
                    baseArrayLayer,
                    mipLevel);
                PipelineStageFlags sourceStage = oldLayout == ImageLayout.ShaderReadOnlyOptimal
                    ? PipelineStageFlags.FragmentShaderBit
                    : PipelineStageFlags.TopOfPipeBit;
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    sourceStage,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var copy = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mipLevel,
                        BaseArrayLayer = baseArrayLayer,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(width, height, depth),
                };
                _vk.CmdCopyBufferToImage(
                    commandBuffer,
                    stagingBuffer,
                    resource.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copy);

                ImageMemoryBarrier toShader = CreateImageBarrier(
                    resource.Image,
                    ImageLayout.TransferDstOptimal,
                    ImageLayout.ShaderReadOnlyOptimal,
                    AccessFlags.TransferWriteBit,
                    AccessFlags.ShaderReadBit,
                    baseArrayLayer,
                    mipLevel);
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShader);
                Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

                fence = CreateFence(signaled: false);
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit");
                WaitForFence(fence, "vkWaitForFences(texture upload)");
                if (setResourceLayout)
                    resource.Layout = ImageLayout.ShaderReadOnlyOptimal;
            }
            finally
            {
                if (fence.Handle != 0)
                    _vk.DestroyFence(_device, fence, null);
                FreeTransientCommandBuffer(commandBuffer);
            }
        }
        finally
        {
            if (stagingBuffer.Handle != 0)
                _vk.DestroyBuffer(_device, stagingBuffer, null);
            if (stagingMemory.Handle != 0)
                _vk.FreeMemory(_device, stagingMemory, null);
        }
    }

    internal void InitializeImageForSampling(Image image, uint mipLevels, uint arrayLayers)
    {
        VkCommandBuffer commandBuffer = AllocateTransientCommandBuffer();
        Fence fence = default;
        try
        {
            BeginOneTimeCommandBuffer(commandBuffer, "vkBeginCommandBuffer");
            ImageMemoryBarrier barrier = CreateImageBarrier(
                image,
                ImageLayout.Undefined,
                ImageLayout.ShaderReadOnlyOptimal,
                0,
                AccessFlags.ShaderReadBit,
                0,
                0,
                mipLevels,
                arrayLayers);
            _vk.CmdPipelineBarrier(
                commandBuffer,
                PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.FragmentShaderBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &barrier);
            Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");
            fence = CreateFence(signaled: false);
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
            };
            CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit");
            WaitForFence(fence, "vkWaitForFences(image init)");
        }
        finally
        {
            if (fence.Handle != 0)
                _vk.DestroyFence(_device, fence, null);
            FreeTransientCommandBuffer(commandBuffer);
        }
    }

    internal byte[] ReadTexture2D(VkImageResource resource, int bytesPerPixel) =>
        ReadTexture2D(resource, 0, bytesPerPixel);

    internal byte[] ReadTexture2D(VkImageResource resource, uint mipLevel, int bytesPerPixel)
    {
        if (mipLevel >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        uint width = Math.Max(1u, resource.Width >> checked((int)mipLevel));
        uint height = Math.Max(1u, resource.Height >> checked((int)mipLevel));
        return ReadTextureSubresource(resource, width, height, 0, mipLevel, bytesPerPixel);
    }

    internal byte[] ReadTextureCubeFace(VkImageResource resource, uint face, uint mipLevel, int bytesPerPixel)
    {
        if (face >= 6)
            throw new ArgumentOutOfRangeException(nameof(face));
        if (mipLevel >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mipLevel));
        uint width = Math.Max(1u, resource.Width >> checked((int)mipLevel));
        uint height = Math.Max(1u, resource.Height >> checked((int)mipLevel));
        return ReadTextureSubresource(resource, width, height, face, mipLevel, bytesPerPixel);
    }

    private byte[] ReadTextureSubresource(
        VkImageResource resource,
        uint width,
        uint height,
        uint arrayLayer,
        uint mipLevel,
        int bytesPerPixel)
    {
        int byteCount = checked((int)(width * height * (uint)bytesPerPixel));
        CreateBuffer(
            (ulong)byteCount,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer readbackBuffer,
            out DeviceMemory readbackMemory);
        try
        {
            VkCommandBuffer commandBuffer = AllocateTransientCommandBuffer();
            Fence fence = default;
            try
            {
                BeginOneTimeCommandBuffer(commandBuffer, "vkBeginCommandBuffer");
                ImageMemoryBarrier toTransfer = CreateImageBarrier(
                    resource.Image,
                    resource.Layout,
                    ImageLayout.TransferSrcOptimal,
                    AccessFlags.ShaderReadBit,
                    AccessFlags.TransferReadBit,
                    arrayLayer,
                    mipLevel);
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.FragmentShaderBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var copy = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        MipLevel = mipLevel,
                        BaseArrayLayer = arrayLayer,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(width, height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    commandBuffer,
                    resource.Image,
                    ImageLayout.TransferSrcOptimal,
                    readbackBuffer,
                    1,
                    &copy);

                ImageMemoryBarrier toShader = CreateImageBarrier(
                    resource.Image,
                    ImageLayout.TransferSrcOptimal,
                    resource.Layout,
                    AccessFlags.TransferReadBit,
                    AccessFlags.ShaderReadBit,
                    arrayLayer,
                    mipLevel);
                _vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShader);
                Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

                fence = CreateFence(signaled: false);
                var submit = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit");
                WaitForFence(fence, "vkWaitForFences(readback)");
            }
            finally
            {
                if (fence.Handle != 0)
                    _vk.DestroyFence(_device, fence, null);
                FreeTransientCommandBuffer(commandBuffer);
            }

            var result = new byte[byteCount];
            void* mapped;
            Check(_vk.MapMemory(_device, readbackMemory, 0, (ulong)byteCount, 0, &mapped), "vkMapMemory");
            try
            {
                fixed (byte* destination = result)
                    System.Buffer.MemoryCopy(mapped, destination, byteCount, byteCount);
            }
            finally
            {
                _vk.UnmapMemory(_device, readbackMemory);
            }
            return result;
        }
        finally
        {
            if (readbackBuffer.Handle != 0)
                _vk.DestroyBuffer(_device, readbackBuffer, null);
            if (readbackMemory.Handle != 0)
                _vk.FreeMemory(_device, readbackMemory, null);
        }
    }

    private static ImageMemoryBarrier CreateImageBarrier(
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        AccessFlags sourceAccess,
        AccessFlags destinationAccess,
        uint baseArrayLayer = 0,
        uint baseMipLevel = 0,
        uint levelCount = 1,
        uint layerCount = 1) => new()
    {
        SType = StructureType.ImageMemoryBarrier,
        OldLayout = oldLayout,
        NewLayout = newLayout,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = image,
        SrcAccessMask = sourceAccess,
        DstAccessMask = destinationAccess,
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = baseMipLevel,
            BaseArrayLayer = baseArrayLayer,
            LevelCount = levelCount,
            LayerCount = layerCount,
        },
    };

    internal void DestroyBuffer(VkBufferResource res)
    {
        if (res.Buffer.Handle != 0) _vk.DestroyBuffer(_device, res.Buffer, null);
        if (res.Memory.Handle != 0) _vk.FreeMemory(_device, res.Memory, null);
    }

    internal void DestroyImage(VkImageResource res)
    {
        if (res.View.Handle != 0) _vk.DestroyImageView(_device, res.View, null);
        if (res.Image.Handle != 0) _vk.DestroyImage(_device, res.Image, null);
        if (res.Memory.Handle != 0) _vk.FreeMemory(_device, res.Memory, null);
        if (res.Sampler.Handle != 0) _vk.DestroySampler(_device, res.Sampler, null);
    }

    internal void DestroyFramebuffer(VkFramebufferResource res)
    {
        if (res.Framebuffer.Handle != 0) _vk.DestroyFramebuffer(_device, res.Framebuffer, null);
        for (int i = 0; i < res.AttachmentViews.Length; i++)
        {
            if (res.AttachmentViews[i].Handle != 0)
                _vk.DestroyImageView(_device, res.AttachmentViews[i], null);
        }
        if (res.RenderPass.Handle != 0) _vk.DestroyRenderPass(_device, res.RenderPass, null);
    }

    internal void DestroyShader(VkShaderResource res)
    {
        if (res.Pipeline.Handle != 0) _vk.DestroyPipeline(_device, res.Pipeline, null);
        if (res.PipelineLayout.Handle != 0) _vk.DestroyPipelineLayout(_device, res.PipelineLayout, null);
        if (res.VertModule.Handle != 0) _vk.DestroyShaderModule(_device, res.VertModule, null);
        if (res.FragModule.Handle != 0) _vk.DestroyShaderModule(_device, res.FragModule, null);
    }

    internal VkShaderLayoutResource GetOrCreateShaderLayout(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (_shaderLayouts.TryGetValue(variant.Id, out VkShaderLayoutResource? cached))
            return cached;

        ShaderBindingLayout bindingLayout = variant.Bytecode?.BindingLayout ?? new ShaderBindingLayout();
        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(bindingLayout);
        plan.EnsureSingleBindGroup(0);
        DescriptorSetLayoutBinding[] managedBindings = new DescriptorSetLayoutBinding[plan.Bindings.Length];
        for (int i = 0; i < plan.Bindings.Length; i++)
        {
            ShaderDescriptorBinding binding = plan.Bindings[i];
            managedBindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)binding.PhysicalBinding,
                DescriptorType = ToDescriptorType(binding.Slot.Kind),
                DescriptorCount = 1,
                StageFlags = ToVkShaderStageFlags(binding.Slot.Stages),
            };
        }

        DescriptorSetLayout descriptorSetLayout;
        fixed (DescriptorSetLayoutBinding* bindings = managedBindings)
        {
            var descriptorInfo = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)managedBindings.Length,
                PBindings = bindings,
            };
            Check(
                _vk.CreateDescriptorSetLayout(_device, &descriptorInfo, null, out descriptorSetLayout),
                "vkCreateDescriptorSetLayout");
        }

        PipelineLayout pipelineLayout;
        try
        {
            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = &descriptorSetLayout,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &pipelineInfo, null, out pipelineLayout),
                "vkCreatePipelineLayout");
        }
        catch
        {
            _vk.DestroyDescriptorSetLayout(_device, descriptorSetLayout, null);
            throw;
        }

        var resource = new VkShaderLayoutResource
        {
            DescriptorSetLayout = descriptorSetLayout,
            PipelineLayout = pipelineLayout,
            Plan = plan,
        };
        _shaderLayouts.Add(variant.Id, resource);
        return resource;
    }

    internal DescriptorSet AllocateDescriptorSet(VkShaderLayoutResource layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        DescriptorSetLayout setLayout = layout.DescriptorSetLayout;
        int frameIndex = _currentFrame;
        List<DescriptorPool> overflowPools = _frameDescriptorOverflowPools[frameIndex];
        int poolIndex = _currentDescriptorPoolIndices[frameIndex];

        while (true)
        {
            DescriptorPool pool;
            bool created = false;
            if (poolIndex == 0)
            {
                pool = _frameDescriptorPools[frameIndex];
            }
            else if (poolIndex - 1 < overflowPools.Count)
            {
                pool = overflowPools[poolIndex - 1];
            }
            else
            {
                if (overflowPools.Count >= MaxDescriptorPoolsPerFrame - 1)
                    throw new InvalidOperationException(
                        $"Vulkan descriptor pool exhausted after {MaxDescriptorPoolsPerFrame * DescriptorPoolCapacity} sets in frame {frameIndex}.");
                pool = CreateDescriptorPool($"overflow {overflowPools.Count} frame {frameIndex}");
                overflowPools.Add(pool);
                created = true;
            }

            var allocation = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            Result result = _vk.AllocateDescriptorSets(_device, &allocation, out DescriptorSet descriptorSet);
            if (result == Result.Success)
            {
                _currentDescriptorPoolIndices[frameIndex] = poolIndex;
                return descriptorSet;
            }

            if (result != Result.ErrorOutOfPoolMemory && result != Result.ErrorFragmentedPool)
                Check(result, "vkAllocateDescriptorSets");
            // A newly-created pool failing means one descriptor set needs more resources than
            // DescriptorPoolCapacity; do not spin creating an unbounded number of identical pools.
            if (created)
                Check(result, "vkAllocateDescriptorSets (new overflow pool)");

            poolIndex++;
        }
    }

    private List<Sampler> RentSamplerList() =>
        _samplerListPool.Count > 0 ? _samplerListPool.Pop() : new List<Sampler>(4);

    private void ReturnSamplerList(List<Sampler> samplers)
    {
        samplers.Clear();
        _samplerListPool.Push(samplers);
    }

    private void DestroySamplers(List<Sampler> samplers)
    {
        for (int i = 0; i < samplers.Count; i++)
        {
            Sampler sampler = samplers[i];
            if (sampler.Handle != 0)
                _vk.DestroySampler(_device, sampler, null);
        }
    }

    internal VkShaderModuleResource GetOrCreateShaderModules(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (_shaderModules.TryGetValue(variant.Id, out VkShaderModuleResource? cached))
            return cached;

        CompiledShaderBytecode bytecode = variant.Bytecode
            ?? throw new InvalidOperationException("Vulkan shader modules require compiled bytecode.");
        if (bytecode.Format != ShaderBytecodeFormat.SpirV)
            throw new InvalidOperationException($"Vulkan shader modules require SPIR-V, got {bytecode.Format}.");

        ShaderModule vertexModule = default;
        ShaderModule fragmentModule = default;
        ShaderModule computeModule = default;
        try
        {
            if (bytecode.VertexBytecode.Length > 0)
                vertexModule = CreateShaderModule(bytecode.VertexBytecode, "vertex");
            if (bytecode.FragmentBytecode.Length > 0)
                fragmentModule = CreateShaderModule(bytecode.FragmentBytecode, "fragment");
            if (bytecode.ComputeBytecode.Length > 0)
                computeModule = CreateShaderModule(bytecode.ComputeBytecode, "compute");
            if (vertexModule.Handle == 0 && fragmentModule.Handle == 0 && computeModule.Handle == 0)
                throw new InvalidOperationException("Vulkan shader modules require vertex/fragment or compute bytecode.");
        }
        catch
        {
            if (vertexModule.Handle != 0)
                _vk.DestroyShaderModule(_device, vertexModule, null);
            if (fragmentModule.Handle != 0)
                _vk.DestroyShaderModule(_device, fragmentModule, null);
            if (computeModule.Handle != 0)
                _vk.DestroyShaderModule(_device, computeModule, null);
            throw;
        }

        var resource = new VkShaderModuleResource
        {
            VertexModule = vertexModule,
            FragmentModule = fragmentModule,
            ComputeModule = computeModule,
        };
        _shaderModules.Add(variant.Id, resource);
        return resource;
    }

    internal Pipeline GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        Format colorFormat)
    {
        return GetOrCreateGraphicsPipeline(key, variant, new VkColorAttachmentFormats(colorFormat));
    }

    internal Pipeline GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        VkColorAttachmentFormats colorFormats)
        => GetOrCreateGraphicsPipeline(key, variant, new VkRenderTargetFormats(colorFormats, Format.Undefined));

    internal Pipeline GetOrCreateGraphicsPipeline(
        GraphicsPipelineKey key,
        ShaderVariant variant,
        VkRenderTargetFormats targetFormats)
    {
        var cacheKey = new VkGraphicsPipelineKey(key, targetFormats);
        if (_graphicsPipelines.TryGetValue(cacheKey, out Pipeline cached))
        {
            _pipelineCacheHitCount++;
            if (_pipelineCacheHitCount == 1)
                Debug.Log("[Vulkan] pipeline-cache first in-process hit");
            return cached;
        }

        RasterizerState raster = key.RasterState;
        // Silently disable depth/stencil testing when the current framebuffer has no depth
        // attachment (e.g. the default swapchain target or a color-only custom framebuffer used by
        // the UI renderer). The OpenGL backend ignores depth state on depth-less targets naturally;
        // Vulkan requires explicit PSO state, so we override here instead of crashing.
        if ((raster.DepthTest || raster.DepthWrite) && targetFormats.DepthFormat == Format.Undefined)
        {
            raster.DepthTest = false;
            raster.DepthWrite = false;
        }
        if (raster.StencilEnabled && !VulkanFormats.HasStencil(targetFormats.DepthFormat))
        {
            raster.StencilEnabled = false;
        }

        VkShaderLayoutResource layout = GetOrCreateShaderLayout(variant);
        VkShaderModuleResource modules = GetOrCreateShaderModules(variant);
        RenderPass renderPass = GetOrCreatePipelineRenderPass(targetFormats);
        CreateVertexInputDescriptions(
            key.VertexArrayHandle,
            variant.Bytecode?.VertexInputLayout,
            out VertexInputBindingDescription[] bindings,
            out VertexInputAttributeDescription[] attributes);
        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            fixed (VertexInputBindingDescription* bindingPointer = bindings)
            fixed (VertexInputAttributeDescription* attributePointer = attributes)
            {
                PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = modules.VertexModule,
                    PName = entryPoint,
                };
                stages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = modules.FragmentModule,
                    PName = entryPoint,
                };
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)bindings.Length,
                    PVertexBindingDescriptions = bindingPointer,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = attributePointer,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = VulkanFormats.ToTopology(key.Topology),
                    PrimitiveRestartEnable = false,
                };
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = raster.FillMode == RasterizerState.FillModeKind.Wireframe && _fillModeNonSolid
                        ? PolygonMode.Line : PolygonMode.Fill,
                    CullMode = VulkanFormats.ToCullMode(raster.CullFace),
                    FrontFace = VulkanFormats.ToFrontFace(raster.Winding, UsesPositiveViewportHeight),
                    DepthBiasEnable = false,
                    LineWidth = 1f,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    // Must equal the render pass's attachment sample count. SampleShading stays
                    // off: per-sample shading would restore the very per-pixel cost MSAA avoids.
                    RasterizationSamples = VulkanFormats.ToSampleCount(targetFormats.SampleCount),
                    SampleShadingEnable = false,
                };
                PipelineColorBlendAttachmentState* colorAttachments = stackalloc PipelineColorBlendAttachmentState[targetFormats.ColorFormats.Count];
                for (int i = 0; i < targetFormats.ColorFormats.Count; i++)
                {
                    colorAttachments[i] = new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = raster.DoBlend,
                        SrcColorBlendFactor = VulkanFormats.ToBlendFactor(raster.BlendSrc),
                        DstColorBlendFactor = VulkanFormats.ToBlendFactor(raster.BlendDst),
                        ColorBlendOp = VulkanFormats.ToBlendOp(raster.Blend),
                        SrcAlphaBlendFactor = VulkanFormats.ToBlendFactor(raster.BlendSrc),
                        DstAlphaBlendFactor = VulkanFormats.ToBlendFactor(raster.BlendDst),
                        AlphaBlendOp = VulkanFormats.ToBlendOp(raster.Blend),
                        ColorWriteMask = (ColorComponentFlags)raster.ColorMask,
                    };
                }
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = checked((uint)targetFormats.ColorFormats.Count),
                    PAttachments = targetFormats.ColorFormats.Count == 0 ? null : colorAttachments,
                };
                var stencil = new StencilOpState
                {
                    FailOp = VulkanFormats.ToStencilOp(raster.StencilFailOp),
                    PassOp = VulkanFormats.ToStencilOp(raster.StencilPassOp),
                    DepthFailOp = VulkanFormats.ToStencilOp(raster.StencilZFailOp),
                    CompareOp = VulkanFormats.ToCompareOp(raster.StencilFunc),
                    CompareMask = checked((uint)raster.StencilReadMask),
                    WriteMask = checked((uint)raster.StencilWriteMask),
                    Reference = checked((uint)raster.StencilRef),
                };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = raster.DepthTest,
                    DepthWriteEnable = raster.DepthWrite,
                    DepthCompareOp = VulkanFormats.ToCompareOp(raster.Depth),
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = raster.StencilEnabled,
                    Front = stencil,
                    Back = stencil,
                    MinDepthBounds = 0f,
                    MaxDepthBounds = 1f,
                };
                DynamicState* dynamicStates = stackalloc DynamicState[2]
                {
                    DynamicState.Viewport,
                    DynamicState.Scissor,
                };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates,
                };
                var createInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &colorBlend,
                    PDynamicState = &dynamicState,
                    Layout = layout.PipelineLayout,
                    RenderPass = renderPass,
                    Subpass = 0,
                    BasePipelineIndex = -1,
                };
                Check(
                    _vk.CreateGraphicsPipelines(_device, _pipelineCache, 1, &createInfo, null, out Pipeline pipeline),
                    "vkCreateGraphicsPipelines");
                _graphicsPipelines.Add(cacheKey, pipeline);
                _pipelineCreateCount++;
                if (_pipelineCreateCount == 1)
                    Debug.Log("[Vulkan] pipeline-cache first pipeline created");
                return pipeline;
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    internal Pipeline GetOrCreateComputePipeline(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (_computePipelines.TryGetValue(variant.Id, out Pipeline cached))
            return cached;

        CompiledShaderBytecode bytecode = variant.Bytecode
            ?? throw new InvalidOperationException("Vulkan compute pipelines require compiled bytecode.");
        if (bytecode.Format != ShaderBytecodeFormat.SpirV)
            throw new InvalidOperationException($"Vulkan compute pipelines require SPIR-V, got {bytecode.Format}.");
        if (!bytecode.HasCompute)
            throw new InvalidOperationException("Vulkan compute pipelines require compute bytecode.");

        VkShaderModuleResource modules = GetOrCreateShaderModules(variant);
        if (modules.ComputeModule.Handle == 0)
            throw new InvalidOperationException("Vulkan compute pipeline is missing a compute shader module.");

        VkShaderLayoutResource layout = GetOrCreateShaderLayout(variant);
        byte* entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = modules.ComputeModule,
                PName = entryPoint,
            };
            var createInfo = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout.PipelineLayout,
            };
            Check(
                _vk.CreateComputePipelines(_device, _pipelineCache, 1, &createInfo, null, out Pipeline pipeline),
                "vkCreateComputePipelines");
            _computePipelines.Add(variant.Id, pipeline);
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint);
        }
    }

    private void CreateVertexInputDescriptions(
        uint vertexArrayHandle,
        ShaderVertexInputLayout? shaderInputLayout,
        out VertexInputBindingDescription[] bindings,
        out VertexInputAttributeDescription[] attributes)
    {
        if (vertexArrayHandle == 0)
        {
            bindings = [];
            attributes = [];
            return;
        }
        if (!_vertexArrays.TryGetValue(vertexArrayHandle, out VkVertexArrayResource? vertexArray))
            throw new InvalidOperationException($"Vulkan PSO references unknown vertex array {vertexArrayHandle}.");
        VertexFormat? instanceFormat = vertexArray.InstanceFormat;
        bindings = instanceFormat == null
            ?
            [
                new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = (uint)vertexArray.Format.Size,
                    InputRate = VertexInputRate.Vertex,
                },
            ]
            :
            [
                new VertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = (uint)vertexArray.Format.Size,
                    InputRate = VertexInputRate.Vertex,
                },
                new VertexInputBindingDescription
                {
                    Binding = 1,
                    Stride = (uint)instanceFormat.Size,
                    InputRate = VertexInputRate.Instance,
                },
            ];
        VertexFormat.Element[] vertexElements = vertexArray.Format.Elements;
        VertexFormat.Element[] instanceElements = instanceFormat?.Elements ?? [];
        attributes = new VertexInputAttributeDescription[vertexElements.Length + instanceElements.Length];
        int count = AddVertexInputAttributes(attributes, 0, vertexElements, 0, perInstance: false, shaderInputLayout);
        count = AddVertexInputAttributes(attributes, count, instanceElements, 1, perInstance: true, shaderInputLayout);
        Array.Resize(ref attributes, count);
    }

    internal static int AddVertexInputAttributes(
        VertexInputAttributeDescription[] attributes,
        int destinationOffset,
        VertexFormat.Element[] elements,
        uint binding,
        bool perInstance,
        ShaderVertexInputLayout? shaderInputLayout)
    {
        for (int i = 0; i < elements.Length; i++)
        {
            VertexFormat.Element element = elements[i];
            uint location = element.Semantic;
            // Reflection compacts consumed semantics. An UNUSED mesh attribute must not
            // fall back to its engine semantic number and collide with a consumed input.
            if (shaderInputLayout != null && !shaderInputLayout.TryGetLocation(element.Semantic, out location))
                continue;
            if (perInstance ? element.Divisor != 1 : element.Divisor != 0)
            {
                throw new NotSupportedException(perInstance
                    ? "Core Vulkan instanced input currently supports divisor 1 only."
                    : "Vulkan vertex-stream element cannot have a non-zero divisor.");
            }
            attributes[destinationOffset++] = new VertexInputAttributeDescription
            {
                Location = location,
                Binding = binding,
                Format = VulkanFormats.ToVertexFormat(element.Type, element.Count, element.Normalized),
                Offset = (uint)element.Offset,
            };
        }
        return destinationOffset;
    }

    private RenderPass GetOrCreatePipelineRenderPass(VkRenderTargetFormats targetFormats)
    {
        if (_pipelineRenderPasses.TryGetValue(targetFormats, out RenderPass cached))
            return cached;

        int attachmentCount = targetFormats.ColorFormats.Count + (targetFormats.DepthFormat == Format.Undefined ? 0 : 1);
        AttachmentDescription* attachments = stackalloc AttachmentDescription[attachmentCount];
        AttachmentReference* colorReferences = stackalloc AttachmentReference[targetFormats.ColorFormats.Count];
        for (int i = 0; i < targetFormats.ColorFormats.Count; i++)
        {
            attachments[i] = new AttachmentDescription
            {
                Format = targetFormats.ColorFormats[i],
                Samples = VulkanFormats.ToSampleCount(targetFormats.SampleCount),
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.ColorAttachmentOptimal,
            };
            colorReferences[i] = new AttachmentReference
            {
                Attachment = checked((uint)i),
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
        }
        AttachmentReference depthReference = default;
        if (targetFormats.DepthFormat != Format.Undefined)
        {
            int depthIndex = targetFormats.ColorFormats.Count;
            attachments[depthIndex] = new AttachmentDescription
            {
                Format = targetFormats.DepthFormat,
                Samples = VulkanFormats.ToSampleCount(targetFormats.SampleCount),
                LoadOp = AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.Load,
                StencilStoreOp = AttachmentStoreOp.Store,
                InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
            depthReference = new AttachmentReference
            {
                Attachment = checked((uint)depthIndex),
                Layout = ImageLayout.DepthStencilAttachmentOptimal,
            };
        }
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = checked((uint)targetFormats.ColorFormats.Count),
            PColorAttachments = targetFormats.ColorFormats.Count == 0 ? null : colorReferences,
            PDepthStencilAttachment = targetFormats.DepthFormat == Format.Undefined ? null : &depthReference,
        };
        // MUST mirror the translator's custom per-framebuffer render pass EXACTLY in dependency
        // count AND content: RenderPass compatibility (VUID-vkCmdDrawIndexed-renderPass-02684)
        // covers dependencies — count plus each dependency's stage/access/flags. The
        // dependency-less pass created here made every pipeline incompatible with the render
        // pass it was actually drawn in — NVIDIA dropped those draws wholesale (skinned
        // characters collapsed/disappeared). The stage/access masks below are bit-identical to
        // the translator's framebuffer pass (external->subpass, subpass->external).
        SubpassDependency* dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                           PipelineStageFlags.EarlyFragmentTestsBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit |
                            AccessFlags.ColorAttachmentWriteBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        dependencies[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        var createInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = checked((uint)attachmentCount),
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        Check(_vk.CreateRenderPass(_device, &createInfo, null, out RenderPass renderPass), "vkCreateRenderPass(pipeline)");
        _pipelineRenderPasses.Add(targetFormats, renderPass);
        return renderPass;
    }

    private void CreateHeadlessRenderTarget()
    {
        Format format = Format.R8G8B8A8Unorm;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(1, 1, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Check(_vk.CreateImage(_device, &imageInfo, null, out _headlessImage), "vkCreateImage(headless)");
        _vk.GetImageMemoryRequirements(_device, _headlessImage, out MemoryRequirements requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out _headlessMemory), "vkAllocateMemory(headless)");
        Check(_vk.BindImageMemory(_device, _headlessImage, _headlessMemory, 0), "vkBindImageMemory(headless)");
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _headlessImage,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out _headlessView), "vkCreateImageView(headless)");
        _headlessRenderPass = GetOrCreatePipelineRenderPass(new VkRenderTargetFormats(new VkColorAttachmentFormats(format), Format.Undefined));
        ImageView attachment = _headlessView;
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _headlessRenderPass,
            AttachmentCount = 1,
            PAttachments = &attachment,
            Width = 1,
            Height = 1,
            Layers = 1,
        };
        Check(_vk.CreateFramebuffer(_device, &framebufferInfo, null, out _headlessFramebuffer), "vkCreateFramebuffer(headless)");
    }

    private void DestroyHeadlessRenderTarget()
    {
        if (_headlessFramebuffer.Handle != 0) _vk.DestroyFramebuffer(_device, _headlessFramebuffer, null);
        if (_headlessView.Handle != 0) _vk.DestroyImageView(_device, _headlessView, null);
        if (_headlessImage.Handle != 0) _vk.DestroyImage(_device, _headlessImage, null);
        if (_headlessMemory.Handle != 0) _vk.FreeMemory(_device, _headlessMemory, null);
        _headlessFramebuffer = default;
        _headlessView = default;
        _headlessImage = default;
        _headlessMemory = default;
        _headlessRenderPass = default;
    }

    private ShaderModule CreateShaderModule(byte[] bytecode, string stage)
    {
        if (bytecode.Length == 0 || (bytecode.Length & 3) != 0)
            throw new InvalidOperationException($"Vulkan {stage} SPIR-V bytecode must be non-empty and 4-byte aligned.");

        fixed (byte* bytes = bytecode)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)bytecode.Length,
                PCode = (uint*)bytes,
            };
            Check(
                _vk.CreateShaderModule(_device, &createInfo, null, out ShaderModule module),
                $"vkCreateShaderModule({stage})");
            return module;
        }
    }

    private void DestroyShaderModules(VkShaderModuleResource resource)
    {
        if (resource.VertexModule.Handle != 0)
            _vk.DestroyShaderModule(_device, resource.VertexModule, null);
        if (resource.FragmentModule.Handle != 0)
            _vk.DestroyShaderModule(_device, resource.FragmentModule, null);
        if (resource.ComputeModule.Handle != 0)
            _vk.DestroyShaderModule(_device, resource.ComputeModule, null);
    }

    private void DestroyShaderLayout(VkShaderLayoutResource resource)
    {
        if (resource.PipelineLayout.Handle != 0)
            _vk.DestroyPipelineLayout(_device, resource.PipelineLayout, null);
        if (resource.DescriptorSetLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, resource.DescriptorSetLayout, null);
    }

    private static DescriptorType ToDescriptorType(ShaderBindingKind kind) => kind switch
    {
        ShaderBindingKind.Buffer => DescriptorType.UniformBuffer,
        ShaderBindingKind.StructuredBuffer or ShaderBindingKind.StorageBuffer => DescriptorType.StorageBuffer,
        ShaderBindingKind.Texture => DescriptorType.SampledImage,
        ShaderBindingKind.StorageTexture => DescriptorType.StorageImage,
        ShaderBindingKind.Sampler => DescriptorType.Sampler,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ShaderStageFlags ToVkShaderStageFlags(ShaderStageMask mask)
    {
        ShaderStageFlags flags = 0;
        if ((mask & ShaderStageMask.Vertex) != 0)
            flags |= ShaderStageFlags.VertexBit;
        if ((mask & ShaderStageMask.Fragment) != 0)
            flags |= ShaderStageFlags.FragmentBit;
        if ((mask & ShaderStageMask.Geometry) != 0)
            flags |= ShaderStageFlags.GeometryBit;
        if ((mask & ShaderStageMask.Compute) != 0)
            flags |= ShaderStageFlags.ComputeBit;
        if ((mask & ShaderStageMask.Hull) != 0)
            flags |= ShaderStageFlags.TessellationControlBit;
        if ((mask & ShaderStageMask.Domain) != 0)
            flags |= ShaderStageFlags.TessellationEvaluationBit;
        return flags == 0
            ? ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit
            : flags;
    }

    // ─────────────────────── Init helpers ───────────────────────

    private void CreateInstance()
    {
        byte* appName = (byte*)SilkMarshal.StringToPtr("XEngine");
        byte* engName = (byte*)SilkMarshal.StringToPtr("XEngine");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = engName,
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version12, // Slang emits SPIR-V 1.5; Vulkan 1.1 only admits 1.3.
        };

        List<string> extensions = GetRequiredInstanceExtensions(
            OperatingSystem.IsAndroid(),
            RuntimeUtils.IsHarmonyOS(),
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX));

        bool wantDebug = _options.Debug || _options.EnableValidation;
        if (wantDebug)
            extensions.Add(ExtDebugUtils.ExtensionName);

        string[]? layers = wantDebug ? new[] { "VK_LAYER_KHRONOS_validation" } : null;
        if (layers != null && !SupportsLayer(layers[0]))
            layers = null;

        // MoltenVK (and other conformant-but-portable drivers) is only enumerable when the
        // instance opts into VK_KHR_portability_enumeration; without the flag the loader reports
        // ErrorIncompatibleDriver with "Found no drivers" on macOS. VK_EXT_metal_surface is what
        // GLFW's surface creation needs. Both are only requested when the loader actually
        // advertises them so older MoltenVK builds keep working.
        const string PortabilityEnumeration = "VK_KHR_portability_enumeration";
        const string MetalSurface = "VK_EXT_metal_surface";
        bool macOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (macOS && SupportsInstanceExtension(PortabilityEnumeration))
            extensions.Add(PortabilityEnumeration);
        if (macOS && SupportsInstanceExtension(MetalSurface))
            extensions.Add(MetalSurface);

        byte** extPtr = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        byte** layerPtr = layers != null ? (byte**)SilkMarshal.StringArrayToPtr(layers) : null;

        var ci = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = extPtr,
            EnabledLayerCount = layers != null ? (uint)layers.Length : 0,
            PpEnabledLayerNames = layerPtr,
        };
        if (extensions.Contains(PortabilityEnumeration))
            ci.Flags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;

        Result r = _vk.CreateInstance(&ci, null, out _instance);
        SilkMarshal.Free((nint)extPtr);
        if (layerPtr != null) SilkMarshal.Free((nint)layerPtr);
        SilkMarshal.Free((nint)appName);
        SilkMarshal.Free((nint)engName);

        if (r != Result.Success)
            throw new InvalidOperationException($"vkCreateInstance failed: {r}");

        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new InvalidOperationException("VK_KHR_surface is not available.");

        if (wantDebug && _vk.TryGetInstanceExtension(_instance, out _debugUtils))
            CreateDebugMessenger();
    }

    internal static List<string> GetRequiredInstanceExtensions(
        bool isAndroid,
        bool isHarmonyOS,
        bool isWindows,
        bool isMacOS)
    {
        var extensions = new List<string> { KhrSurface.ExtensionName };
        if (isAndroid)
            extensions.Add(KhrAndroidSurface.ExtensionName);
        else if (isHarmonyOS)
            extensions.Add("VK_OHOS_surface"); // matches HarmonyOSWindowSurface.VulkanInstanceExtension
        else if (isWindows)
            extensions.Add("VK_KHR_win32_surface");
        else if (isMacOS)
        {
            extensions.Add("VK_MVK_macos_surface");
            // GLFW's glfwCreateWindowSurface drives VK_EXT_metal_surface (modern MoltenVK
            // deprecates the MVK variant); requested when the loader advertises it so the
            // CreateInstance validation never sees an unsupported extension.
        }
        else
        {
            extensions.Add("VK_KHR_xcb_surface");
            extensions.Add("VK_KHR_wayland_surface");
        }
        return extensions;
    }

    private void CreateWindowSurface(IWindowSurface surface)
    {
        nint surfaceHandle = surface.CreateVulkanSurface((nint)_instance.Handle);
        if (surfaceHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                "Vulkan surface creation failed for the platform window.");
        }
        _surface = new SurfaceKHR((ulong)surfaceHandle);
    }

    private void DestroyWindowSurface()
    {
        if (_surface.Handle == 0)
            return;
        _khrSurface?.DestroySurface(_instance, _surface, null);
        _surface = default;
    }

    private bool SupportsInstanceExtension(string name)
    {
        uint count = 0;
        if (_vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, null) != Result.Success)
            return false;
        if (count == 0) return false;
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
        {
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, p) != Result.Success)
                return false;
        }
        for (int i = 0; i < props.Length; i++)
        {
            fixed (ExtensionProperties* prop = &props[i])
            {
                string extension = Marshal.PtrToStringAnsi((nint)prop->ExtensionName) ?? "";
                if (extension == name) return true;
            }
        }
        return false;
    }

    private bool SupportsLayer(string name)
    {
        uint count = 0;
        _vk.EnumerateInstanceLayerProperties(ref count, null);
        if (count == 0) return false;
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
            _vk.EnumerateInstanceLayerProperties(ref count, p);
        for (int i = 0; i < props.Length; i++)
        {
            fixed (LayerProperties* prop = &props[i])
            {
                string layer = Marshal.PtrToStringAnsi((nint)prop->LayerName) ?? "";
                if (layer == name) return true;
            }
        }
        return false;
    }

    private void CreateDebugMessenger()
    {
        var ci = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                              DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                          DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                          DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback,
        };
        _debugUtils!.CreateDebugUtilsMessenger(_instance, &ci, null, out _debugMessenger);
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string msg = Marshal.PtrToStringAnsi((nint)data->PMessage) ?? "";
        if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            Debug.LogError($"Vulkan: {msg}");
        else
            Debug.LogWarning($"Vulkan: {msg}");
        return Vk.False;
    }

    private void PickPhysicalDevice()
    {
        uint count = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref count, null);
        if (count == 0)
            throw new InvalidOperationException("No Vulkan physical devices found.");

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            _vk.EnumeratePhysicalDevices(_instance, ref count, p);

        int preferred = _options.PreferredAdapterIndex;
        if (preferred >= 0 && preferred < devices.Length)
        {
            _physicalDevice = devices[preferred];
            return;
        }

        // Prefer discrete GPU.
        PhysicalDevice chosen = devices[0];
        foreach (PhysicalDevice pd in devices)
        {
            _vk.GetPhysicalDeviceProperties(pd, out PhysicalDeviceProperties props);
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                chosen = pd;
                break;
            }
        }
        _physicalDevice = chosen;
    }

    private void CreateLogicalDevice()
    {
        FindQueueFamilies(out _graphicsQueueFamily, out _presentQueueFamily);
        _queuesShared = _graphicsQueueFamily == _presentQueueFamily;

        var unique = new HashSet<uint> { _graphicsQueueFamily, _presentQueueFamily };
        float priority = 1f;
        var queueInfos = new DeviceQueueCreateInfo[unique.Count];
        int qi = 0;
        foreach (uint family in unique)
        {
            queueInfos[qi++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = family,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        }

        // VK_KHR_maintenance1 enables negative viewport height (Y-flip) which the
        // VulkanCommandTranslator uses to convert the engine's bottom-left convention
        // to Vulkan's top-left. Without it, Mali drivers (HarmonyOS) render upside-down.
        // In Vulkan 1.1+ this functionality is core but the extension name is still
        // accepted by drivers, so we always request it.
        string[] deviceExts = [KhrSwapchain.ExtensionName, "VK_KHR_maintenance1"];
        // VK_EXT_device_fault is diagnostics-only: when the device is lost we ask the driver
        // what faulted instead of logging a bare ErrorDeviceLost.
        bool deviceFaultSupported = false;
        foreach (string available in EnumerateDeviceExtensionNames())
        {
            if (available == "VK_EXT_device_fault")
            {
                deviceExts = [.. deviceExts, "VK_EXT_device_fault"];
                deviceFaultSupported = true;
                break;
            }
        }
        byte** extPtr = (byte**)SilkMarshal.StringArrayToPtr(deviceExts);

        // Extension presence alone does not enable its optional features.
        var faultFeatures = new PhysicalDeviceFaultFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
        };
        if (deviceFaultSupported)
        {
            var supported = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &faultFeatures,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &supported);
            deviceFaultSupported = faultFeatures.DeviceFault;
            // Vendor binaries are optional; keep diagnostics bounded to addresses/text.
            faultFeatures.DeviceFaultVendorBinary = false;
        }

        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out PhysicalDeviceFeatures supportedFeatures);
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
            // Wireframe (editor debug view) needs non-solid fill; request it only when the
            // hardware offers it and remember the answer for pipeline creation.
            FillModeNonSolid = supportedFeatures.FillModeNonSolid,
        };
        _fillModeNonSolid = supportedFeatures.FillModeNonSolid;
        if (supportedFeatures.SamplerAnisotropy)
        {
            // Eight samples is sufficient for the built-in ground plane at grazing angles,
            // while avoiding an unbounded per-sample cost on mobile hardware.
            MaxSamplerAnisotropy = Math.Clamp(properties.Limits.MaxSamplerAnisotropy, 1f, 8f);
        }
        fixed (DeviceQueueCreateInfo* qPtr = queueInfos)
        {
            var ci = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueInfos.Length,
                PQueueCreateInfos = qPtr,
                EnabledExtensionCount = (uint)deviceExts.Length,
                PpEnabledExtensionNames = extPtr,
                PEnabledFeatures = &features,
                PNext = deviceFaultSupported ? &faultFeatures : null,
            };
            Check(_vk.CreateDevice(_physicalDevice, &ci, null, out _device), "vkCreateDevice");
        }
        SilkMarshal.Free((nint)extPtr);

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentQueueFamily, 0, out _presentQueue);

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain is not available.");

        if (deviceFaultSupported)
        {
            // Silk 2.23 has no ExtDeviceFault binding; load the command through the device proc
            // address so loss captures work without waiting on a package update.
            PfnVoidFunction pfn = _vk.GetDeviceProcAddr(_device, "vkGetDeviceFaultInfoEXT");
            if (pfn.Handle != default)
                _getDeviceFaultInfo = (delegate* unmanaged[Cdecl]<Device, DeviceFaultCounts*, DeviceFaultInfo*, Result>)(nint)pfn.Handle;
            // No init-time log here on purpose: logical-device creation runs before the editor
            // file-log sink attaches, so the line would never land. Loss-time captures below are
            // LogError and do reach both the log store and crash reports.
        }

    }

    private void FindQueueFamilies(out uint graphics, out uint present)
    {
        graphics = present = uint.MaxValue;
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, null);
        var props = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = props)
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref count, p);

        for (uint i = 0; i < count; i++)
        {
            if ((props[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                graphics = i;

            Bool32 supported = false;
            if (_surface.Handle != 0 && _khrSurface != null)
                _khrSurface.GetPhysicalDeviceSurfaceSupport(_physicalDevice, i, _surface, &supported);
            else
                supported = true; // no surface yet — accept graphics family for present too

            if (supported)
                present = i;

            if (graphics != uint.MaxValue && present != uint.MaxValue)
                return;
        }

        if (graphics == uint.MaxValue)
            throw new InvalidOperationException("No graphics queue family on Vulkan device.");
        if (present == uint.MaxValue)
        {
            if (_surface.Handle != 0)
                throw new InvalidOperationException("No Vulkan present queue family supports the window surface.");
            present = graphics;
        }
    }

    private void CreateCommandPoolAndSync()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamily,
        };
        Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

        var transientPoolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamily,
        };
        Check(_vk.CreateCommandPool(_device, &transientPoolInfo, null, out _transientCommandPool),
            "vkCreateCommandPool(transient)");

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            _inFlightFences[i] = CreateFence(signaled: true);
            _imageAvailable[i] = CreateSemaphore();

            var alloc = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            fixed (VkCommandBuffer* cb = &_frameCommandBuffers[i])
                Check(_vk.AllocateCommandBuffers(_device, &alloc, cb), "vkAllocateCommandBuffers");
            fixed (VkCommandBuffer* cb = &_timestampEndCommandBuffers[i])
                Check(_vk.AllocateCommandBuffers(_device, &alloc, cb), "vkAllocateCommandBuffers(timestamp end)");
        }
    }

    private void TryCreateTimestampQueryPool()
    {
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        // Command-buffer query reset is core Vulkan 1.0 (only host reset needs 1.2).
        if (properties.Limits.TimestampPeriod <= 0)
            return;

        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, null);
        if (queueFamilyCount == 0)
            return;
        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* p = queueFamilies)
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, p);
        if (_graphicsQueueFamily >= queueFamilies.Length || queueFamilies[_graphicsQueueFamily].TimestampValidBits == 0)
            return;

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = MaxFramesInFlight * TimestampQueriesPerFrame,
        };
        Result result = _vk.CreateQueryPool(_device, &info, null, out _timestampQueryPool);
        if (result != Result.Success)
        {
            _timestampQueryPool = default;
            return;
        }

        _timestampPeriodNanoseconds = properties.Limits.TimestampPeriod;
    }

    /// <summary>B7b — occlusion query pool. Requires Vulkan 1.2+ (pool reset via vkCmdResetQueryPool
    /// in the frame-start submit). Slot count covers every probe any single frame can record.</summary>
    private void TryCreateOcclusionQueryPool()
    {
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties properties);
        if (properties.ApiVersion < Vk.Version12)
        {
            Runtime.Debug.LogWarning(
                $"[Vulkan] occlusion queries unavailable: API v{properties.ApiVersion >> 22}.{(properties.ApiVersion >> 12) & 0x3FF}.{properties.ApiVersion & 0xFFF} < 1.2 (query-pool reset).");
            return;
        }

        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Occlusion,
            QueryCount = MaxFramesInFlight * OcclusionSlotsPerFrame,
        };
        Result result = _vk.CreateQueryPool(_device, &info, null, out _occlusionQueryPool);
        if (result != Result.Success)
        {
            _occlusionQueryPool = default;
            Runtime.Debug.LogWarning($"[Vulkan] occlusion query pool creation failed: {result}.");
            return;
        }

        for (int i = 0; i < _occlusionPending.Length; i++)
            _occlusionPending[i] = new List<(ulong, uint)>();
        Runtime.Debug.Log("[Vulkan] occlusion query pool created "
            + $"({MaxFramesInFlight * OcclusionSlotsPerFrame} slots).");
    }

    public bool SupportsOcclusionQueries => _occlusionQueryPool.Handle != 0;

    internal int CurrentFrameSlot => _currentFrame;

    /// <summary>
    /// Called by the command translator at execute time. Claims the next occlusion slot of the
    /// current frame for <paramref name="queryId"/> and records vkCmdBeginQuery; when the ring is
    /// exhausted the id is registered as slot-less so its End becomes a no-op and it simply never
    /// completes (callers treat a missing verdict as "no answer this attempt").
    /// </summary>
    internal void BeginOcclusionQueryRecord(VkCommandBuffer commandBuffer, int frameSlot, ulong queryId)
    {
        if (_occlusionQueryPool.Handle == 0)
            return;
        var pending = _occlusionPending[frameSlot];
        if (pending.Count >= OcclusionSlotsPerFrame)
            return;
        uint absolute = (uint)(frameSlot * OcclusionSlotsPerFrame + pending.Count);
        pending.Add((queryId, absolute));
        _vk.CmdBeginQuery(commandBuffer, _occlusionQueryPool, absolute, 0);
        Runtime.Debug.Log($"[OcclusionQuery] begin id={queryId} absSlot={absolute}");
    }

    internal void EndOcclusionQueryRecord(VkCommandBuffer commandBuffer, int frameSlot, ulong queryId)
    {
        if (_occlusionQueryPool.Handle == 0)
            return;
        var pending = _occlusionPending[frameSlot];
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            if (pending[i].QueryId != queryId)
                continue;
            _vk.CmdEndQuery(commandBuffer, _occlusionQueryPool, pending[i].AbsoluteSlot);
            Runtime.Debug.Log($"[OcclusionQuery] end id={queryId} absSlot={pending[i].AbsoluteSlot}");
            return; // keep the entry: results publish after the fence retires
        }
    }

    /// <summary>
    /// Reads the retired frame slot's occlusion results (all its GPU work completed when the
    /// present fence was waited at the top of BeginFrame) and publishes them into the bounded
    /// completed map polled by engine systems.
    /// </summary>
    private void PublishOcclusionResults(int frameIndex)
    {
        var pending = _occlusionPending[frameIndex];
        if (_occlusionQueryPool.Handle == 0 || pending.Count == 0)
            return;

        lock (_occlusionGate)
        {
            // Results are read only after the frame slot's present fence retired, so they are
            // expected to be available; a not-ready read stays pending for the next cycle
            // instead of publishing a false "not passed" verdict.
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (queryId, absoluteSlot) = pending[i];
                uint passed = 0;
                Result result = _vk.GetQueryPoolResults(_device, _occlusionQueryPool, absoluteSlot, 1,
                    sizeof(uint), &passed, sizeof(uint), QueryResultFlags.None);
                if (result == Result.Success)
                {
                    SetOcclusionResultLocked(queryId, completed: true, passed: passed != 0);
                    pending.RemoveAt(i);
                }
            }
        }
    }

    private void SetOcclusionResultLocked(ulong queryId, bool completed, bool passed)
    {
        if (_completedOcclusionQueries.ContainsKey(queryId))
            _completedOcclusionQueries[queryId] = completed && passed;
        else
        {
            _completedOcclusionQueries[queryId] = completed && passed;
            _completedOcclusionOrder.Enqueue(queryId);
        }
        // Bounded retention: probes poll their verdict within a few frames; drop ancient ids.
        while (_completedOcclusionOrder.Count > 256)
        {
            ulong oldest = _completedOcclusionOrder.Dequeue();
            _completedOcclusionQueries.Remove(oldest);
        }
    }

    /// <inheritdoc />
    public bool TryGetOcclusionQueryResult(ulong queryId, out bool passed)
    {
        lock (_occlusionGate)
        {
            // Only queries that actually claimed a slot publish here; an exhausted-ring id stays
            // absent, which callers read as "no verdict this attempt".
            return _completedOcclusionQueries.TryGetValue(queryId, out passed);
        }
    }

    private void CreateDescriptorPool()
    {
        for (int i = 0; i < MaxFramesInFlight; i++)
            _frameDescriptorPools[i] = CreateDescriptorPool($"frame {i}");
    }

    private DescriptorPool CreateDescriptorPool(string label)
    {
        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[3]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = DescriptorPoolCapacity },
            new() { Type = DescriptorType.SampledImage, DescriptorCount = DescriptorPoolCapacity },
            new() { Type = DescriptorType.Sampler, DescriptorCount = DescriptorPoolCapacity },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            // No FreeDescriptorSetBit — sets are recycled via vkResetDescriptorPool in BeginFrame.
            Flags = DescriptorPoolCreateFlags.None,
            MaxSets = DescriptorPoolCapacity,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
        };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out DescriptorPool pool), $"vkCreateDescriptorPool({label})");
        return pool;
    }

    private static List<DescriptorPool>[] CreateDescriptorPoolLists()
    {
        var pools = new List<DescriptorPool>[MaxFramesInFlight];
        for (int i = 0; i < pools.Length; i++)
            pools[i] = new List<DescriptorPool>();
        return pools;
    }

    private void ResetDescriptorPools(int frameIndex)
    {
        DescriptorPool primary = _frameDescriptorPools[frameIndex];
        if (primary.Handle != 0)
            Check(_vk.ResetDescriptorPool(_device, primary, 0), "vkResetDescriptorPool");

        List<DescriptorPool> overflowPools = _frameDescriptorOverflowPools[frameIndex];
        for (int i = 0; i < overflowPools.Count; i++)
        {
            if (overflowPools[i].Handle != 0)
                Check(_vk.ResetDescriptorPool(_device, overflowPools[i], 0), "vkResetDescriptorPool(overflow)");
        }
        _currentDescriptorPoolIndices[frameIndex] = 0;
    }

    private void DestroyDescriptorPool()
    {
        if (_device.Handle == 0)
            return;
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_frameDescriptorPools[i].Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, _frameDescriptorPools[i], null);
                _frameDescriptorPools[i] = default;
            }

            List<DescriptorPool> overflowPools = _frameDescriptorOverflowPools[i];
            for (int j = 0; j < overflowPools.Count; j++)
            {
                if (overflowPools[j].Handle != 0)
                    _vk.DestroyDescriptorPool(_device, overflowPools[j], null);
            }
            overflowPools.Clear();
            _currentDescriptorPoolIndices[i] = 0;
        }
    }

    private void DestroySyncAndPool()
    {
        if (_device.Handle == 0) return;
        if (_deviceLoss.IsLost)
            DiscardPendingRetirements();
        else
            RetirePending();
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_inFlightFences[i].Handle != 0) _vk.DestroyFence(_device, _inFlightFences[i], null);
            if (_imageAvailable[i].Handle != 0) _vk.DestroySemaphore(_device, _imageAvailable[i], null);
            _inFlightFences[i] = default;
            _imageAvailable[i] = default;
        }
        _transientCommandFree.Clear();
        _transientCommandLive = 0;
        if (_transientCommandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _transientCommandPool, null);
            _transientCommandPool = default;
        }
        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }
    }

    internal void RecreateSwapchain()
    {
        if (_surface.Handle == 0 || _khrSurface == null || _khrSwapchain == null)
            return;

        if (_deviceLoss.IsLost)
            return;
        WaitForGpuIdle("recreate swapchain");

        (int fbW, int fbH) = _windowSurface?.FramebufferSize ?? (0, 0);
        if (_requestedResizeWidth >= 0)
            fbW = _requestedResizeWidth;
        if (_requestedResizeHeight >= 0)
            fbH = _requestedResizeHeight;
        _requestedResizeWidth = -1;
        _requestedResizeHeight = -1;
        if (fbW <= 0 || fbH <= 0)
        {
            DestroySwapchain();
            Debug.Log($"Vulkan swapchain: surface extent resolved to {fbW}x{fbH}; suspending until a non-zero size arrives.");
            return;
        }
        SurfaceCapabilitiesKHR caps = WaitForCurrentSurfaceExtent(fbW, fbH, out int extentWaitMilliseconds);

        // Keep the previous chain alive until the replacement exists. Destroying it before
        // vkCreateSwapchainKHR leaves the compositor with no presentable image during a live
        // resize, which is visible as a black flash on Windows.
        SwapchainKHR oldSwapchain = _swapchain;
        DestroySwapchainResources();

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, null);
        var formats = new SurfaceFormatKHR[Math.Max(formatCount, 1)];
        fixed (SurfaceFormatKHR* f = formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref formatCount, f);

        SurfaceFormatKHR chosenFormat = SelectSwapchainSurfaceFormat(formats.AsSpan(0, (int)formatCount));
        _swapchainFormat = chosenFormat.Format;

        uint width = fbW > 0 ? (uint)fbW : caps.CurrentExtent.Width;
        uint height = fbH > 0 ? (uint)fbH : caps.CurrentExtent.Height;
        if (caps.CurrentExtent.Width != uint.MaxValue)
        {
            width = caps.CurrentExtent.Width;
            height = caps.CurrentExtent.Height;
        }
        width = Math.Clamp(width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width);
        height = Math.Clamp(height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height);

        // Plan §4.2: a zero-size surface (minimized window, mid-resize on Android/HarmonyOS, or a
        // surface that reported 0x0 before detach) must suspend the swapchain rather than create a
        // degenerate 1x1 chain. The old handle is still retained until a replacement has been
        // created, so destroy it here before returning and leave HasSwapchain false.
        if (width == 0 || height == 0)
        {
            DestroySwapchain();
            Debug.Log($"Vulkan swapchain: surface extent resolved to {width}x{height}; suspending until a non-zero size arrives.");
            return;
        }

        _swapchainExtent = new Extent2D(width, height);
        SurfaceTransformFlagsKHR preTransform = SelectSwapchainPreTransform(caps);
        Debug.Log($"Vulkan swapchain: surface={fbW}x{fbH}, extent={width}x{height}, currentTransform={caps.CurrentTransform}, supportedTransforms={caps.SupportedTransforms}, preTransform={preTransform}, extentWaitMs={extentWaitMilliseconds}.");

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref presentModeCount, null);
        var presentModes = new PresentModeKHR[presentModeCount];
        fixed (PresentModeKHR* modes = presentModes)
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref presentModeCount, modes);

        var sci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _swapchainFormat,
            ImageColorSpace = chosenFormat.ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            // Keep app-space pixels in the SurfaceView's current orientation. On Android the
            // surface's natural buffer orientation can differ after a display rotation; opting
            // into CurrentTransform in that case rotates an already-oriented frame a second time.
            PreTransform = preTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = SelectPresentMode(_windowSurface?.VSync ?? _options.VSync, presentModes),
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        if (!_queuesShared)
        {
            uint* families = stackalloc uint[] { _graphicsQueueFamily, _presentQueueFamily };
            sci.ImageSharingMode = SharingMode.Concurrent;
            sci.QueueFamilyIndexCount = 2;
            sci.PQueueFamilyIndices = families;
        }
        else
        {
            sci.ImageSharingMode = SharingMode.Exclusive;
        }

        Check(_khrSwapchain.CreateSwapchain(_device, &sci, null, out SwapchainKHR replacementSwapchain), "vkCreateSwapchainKHR");
        _swapchain = replacementSwapchain;
        if (oldSwapchain.Handle != 0)
            _khrSwapchain.DestroySwapchain(_device, oldSwapchain, null);

        uint actualCount = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref actualCount, null);
        _swapchainImages = new Image[actualCount];
        fixed (Image* imgs = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref actualCount, imgs);

        _swapchainImageViews = new ImageView[actualCount];
        for (int i = 0; i < actualCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1,
                },
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[i]), "vkCreateImageView");
        }

        CreateSwapchainRenderPass();
        _swapchainFramebuffers = new Framebuffer[actualCount];
        _swapchainImageInitialized = new bool[actualCount];
        _renderFinished = new Semaphore[actualCount];
        for (int i = 0; i < actualCount; i++)
        {
            _renderFinished[i] = CreateSemaphore();
            ImageView attachment = _swapchainImageViews[i];
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _swapchainRenderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1,
            };
            Check(_vk.CreateFramebuffer(_device, &fbInfo, null, out _swapchainFramebuffers[i]), "vkCreateFramebuffer");
        }
    }

    internal static PresentModeKHR SelectPresentMode(bool vsync, ReadOnlySpan<PresentModeKHR> available)
    {
        if (vsync) return PresentModeKHR.FifoKhr;
        bool immediate = false;
        for (int i = 0; i < available.Length; i++)
        {
            if (available[i] == PresentModeKHR.MailboxKhr) return PresentModeKHR.MailboxKhr;
            immediate |= available[i] == PresentModeKHR.ImmediateKhr;
        }
        if (immediate) return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr; // Vulkan guarantees FIFO, but not Mailbox/Immediate.
    }

    private void ApplyPendingResize()
    {
        if (!_swapchainResizePending || _surface.Handle == 0)
            return;

        _swapchainResizePending = false;
        RecreateSwapchain();
    }

    private void CreateSwapchainRenderPass()
    {
        _swapchainRenderPass = CreateSwapchainRenderPass(AttachmentLoadOp.Clear, ImageLayout.Undefined);
        _swapchainLoadRenderPass = CreateSwapchainRenderPass(AttachmentLoadOp.Load, ImageLayout.PresentSrcKhr);
    }

    // The final blit writes linear scene colors. Choosing an sRGB image lets Vulkan encode that
    // output for the display; UNORM would present the linear values directly and darken all
    // midtones on Android.
    internal static SurfaceFormatKHR SelectSwapchainSurfaceFormat(ReadOnlySpan<SurfaceFormatKHR> formats)
    {
        if (formats.IsEmpty)
            throw new InvalidOperationException("Vulkan surface did not expose a usable swapchain format.");

        if (formats.Length == 1 && formats[0].Format == Format.Undefined)
        {
            return new SurfaceFormatKHR
            {
                Format = Format.B8G8R8A8Srgb,
                ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            };
        }

        for (int i = 0; i < formats.Length; i++)
        {
            SurfaceFormatKHR format = formats[i];
            if (format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr &&
                (format.Format == Format.B8G8R8A8Srgb || format.Format == Format.R8G8B8A8Srgb))
            {
                return format;
            }
        }

        for (int i = 0; i < formats.Length; i++)
        {
            if (formats[i].Format == Format.B8G8R8A8Unorm || formats[i].Format == Format.R8G8B8A8Unorm)
                return formats[i];
        }

        return formats[0];
    }

    /// <summary>
    /// Prefer identity so the presentation engine applies any display rotation exactly once.
    /// Some surfaces only expose a fixed transform, in which case Vulkan requires that value.
    /// </summary>
    internal static SurfaceTransformFlagsKHR SelectSwapchainPreTransform(in SurfaceCapabilitiesKHR capabilities) =>
        (capabilities.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0
            ? SurfaceTransformFlagsKHR.IdentityBitKhr
            : capabilities.CurrentTransform;

    internal static bool IsSurfaceExtentReady(
        in SurfaceCapabilitiesKHR capabilities,
        int framebufferWidth,
        int framebufferHeight)
    {
        if (framebufferWidth <= 0 || framebufferHeight <= 0 ||
            capabilities.CurrentExtent.Width == uint.MaxValue ||
            SelectSwapchainPreTransform(capabilities) != SurfaceTransformFlagsKHR.IdentityBitKhr)
        {
            return true;
        }

        return capabilities.CurrentExtent.Width == (uint)framebufferWidth &&
            capabilities.CurrentExtent.Height == (uint)framebufferHeight;
    }

    private SurfaceCapabilitiesKHR WaitForCurrentSurfaceExtent(
        int framebufferWidth,
        int framebufferHeight,
        out int waitedMilliseconds)
    {
        const int retryDelayMilliseconds = 10;
        const int maxAttempts = 76;
        SurfaceCapabilitiesKHR capabilities = default;
        waitedMilliseconds = 0;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out capabilities);
            if (IsSurfaceExtentReady(capabilities, framebufferWidth, framebufferHeight))
                return capabilities;

            if (attempt + 1 < maxAttempts)
            {
                System.Threading.Thread.Sleep(retryDelayMilliseconds);
                waitedMilliseconds += retryDelayMilliseconds;
            }
        }

        Debug.LogWarning(
            $"Vulkan surface extent did not converge to {framebufferWidth}x{framebufferHeight} after {waitedMilliseconds} ms; " +
            $"using current extent {capabilities.CurrentExtent.Width}x{capabilities.CurrentExtent.Height}.");
        return capabilities;
    }

    private RenderPass CreateSwapchainRenderPass(AttachmentLoadOp loadOp, ImageLayout initialLayout)
    {
        var color = new AttachmentDescription
        {
            Format = _swapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = loadOp,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = initialLayout,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                           PipelineStageFlags.EarlyFragmentTestsBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            // The load render pass is used for the overlay command buffer after the final blit.
            // Alpha-blended UI reads the existing swapchain color while writing the glyph pixels;
            // declaring only the write access leaves that load/read undefined on tile-based Vulkan
            // implementations and makes text intermittently disappear between presents.
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit |
                            AccessFlags.ColorAttachmentWriteBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        // Second dependency (subpass -> external) keeps this pass COMPATIBLE with the 2-dependency
        // pipeline render passes (RenderPass compatibility includes dependencyCount AND each
        // dependency's stage/access content). The overlay UI / FinalBlit draws run in THIS pass
        // with pipelines created for the 2-dependency pass; any 1-vs-2 or content mismatch made
        // every such draw a validation error and driver-undefined. Bit-identical to the pipeline
        // pass and the translator's framebuffer pass.
        var dependencyOut = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit |
                           PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask = PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit |
                            AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        var dependencies = stackalloc SubpassDependency[2];
        dependencies[0] = dependency;
        dependencies[1] = dependencyOut;
        var rp = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = dependencies,
        };
        Check(_vk.CreateRenderPass(_device, &rp, null, out RenderPass renderPass), "vkCreateRenderPass");
        return renderPass;
    }

    private void DestroySwapchain()
    {
        if (_device.Handle == 0) return;
        DestroySwapchainResources();

        if (_swapchain.Handle != 0 && _khrSwapchain != null)
        {
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }
    }

    private void DestroySwapchainResources()
    {
        for (int i = 0; i < _renderFinished.Length; i++)
            if (_renderFinished[i].Handle != 0)
                _vk.DestroySemaphore(_device, _renderFinished[i], null);
        _renderFinished = Array.Empty<Semaphore>();
        for (int i = 0; i < _swapchainFramebuffers.Length; i++)
        {
            if (_swapchainFramebuffers[i].Handle != 0)
                _vk.DestroyFramebuffer(_device, _swapchainFramebuffers[i], null);
        }
        _swapchainFramebuffers = Array.Empty<Framebuffer>();

        if (_swapchainRenderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _swapchainRenderPass, null);
            _swapchainRenderPass = default;
        }
        if (_swapchainLoadRenderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _swapchainLoadRenderPass, null);
            _swapchainLoadRenderPass = default;
        }

        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            if (_swapchainImageViews[i].Handle != 0)
                _vk.DestroyImageView(_device, _swapchainImageViews[i], null);
        }
        _swapchainImageViews = Array.Empty<ImageView>();
        _swapchainImages = Array.Empty<Image>();
    }

    private void QueryCapabilities()
    {
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out PhysicalDeviceProperties props);
        _vk.GetPhysicalDeviceFeatures(_physicalDevice, out PhysicalDeviceFeatures features);
        UniformBufferOffsetAlignment = Math.Max(1ul, props.Limits.MinUniformBufferOffsetAlignment);
        string name = Marshal.PtrToStringAnsi((nint)props.DeviceName) ?? "Vulkan";
        string[] extensions = EnumerateDeviceExtensionNames();
        bool anisotropy = features.SamplerAnisotropy;
        bool timestampQueries = false;
        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, null);
        if (queueFamilyCount > 0)
        {
            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* p = queueFamilies)
                _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, p);
            if (_graphicsQueueFamily < queueFamilies.Length)
                timestampQueries = queueFamilies[_graphicsQueueFamily].TimestampValidBits > 0;
        }
        GraphicsCapabilityBaseline baseline = new()
        {
            Backend = "Vulkan",
            DeviceName = name,
            DeviceType = props.DeviceType.ToString(),
            VendorId = props.VendorID,
            DeviceId = props.DeviceID,
            ApiVersion = props.ApiVersion,
            DriverVersion = props.DriverVersion,
            ApiVersionName = GraphicsCapabilityBaseline.FormatVulkanVersion(props.ApiVersion),
            DriverVersionName = GraphicsCapabilityBaseline.FormatVulkanVersion(props.DriverVersion),
            MaxTextureSize = (int)props.Limits.MaxImageDimension2D,
            SupportsSamplerAnisotropy = anisotropy,
            SupportsPipelineCache = true,
            CapabilityTier = GraphicsCapabilityBaseline.ClassifyVulkanTier(
                props.ApiVersion,
                (int)props.Limits.MaxImageDimension2D,
                anisotropy),
            DeviceExtensions = extensions,
        };
        _capabilities = new GraphicsDeviceCapabilities
        {
            MaxTextureSize = (int)props.Limits.MaxImageDimension2D,
            MaxCubeMapTextureSize = (int)props.Limits.MaxImageDimensionCube,
            MaxArrayTextureLayers = (int)props.Limits.MaxImageArrayLayers,
            MaxFramebufferColorAttachments = (int)props.Limits.MaxColorAttachments,
            MaxFramesInFlight = MaxFramesInFlight,
            // VkSampleCountFlags uses the same encoding the RHI capability mask does — the bit whose
            // value equals the sample count. Forward the driver limits unchanged; the RHI intersects
            // color and depth itself because a render pass needs one shared count.
            SupportedColorSampleCounts = (int)props.Limits.FramebufferColorSampleCounts,
            SupportedDepthSampleCounts = (int)props.Limits.FramebufferDepthSampleCounts,
            SupportsCompute = true,
            SupportsGeometryShader = props.Limits.MaxGeometryOutputVertices > 0,
            SupportsTimestampQueries = _timestampQueryPool.Handle != 0,
            HardwareSupportsTimestampQueries = timestampQueries,
            BackendName = $"Vulkan ({name})",
            Baseline = baseline,
        };

#if XENGINE_HARMONYOS
        // Plan §4.2: every HarmonyOS device must clear the Vulkan capability envelope on its first
        // capability query, and the first-frame log must record vendor/device/driver/API/extensions
        // plus any unmet requirement. We cannot assume support — the device must confirm it. The
        // pure validator + log live in HarmonyOSVulkanBaseline (unit-tested); this hook captures the
        // observed facts from the same vkGetPhysicalDevice* data queried above.
        LogHarmonyOSVulkanBaseline(props, features, name);
#endif
    }

#if XENGINE_HARMONYOS
    private void LogHarmonyOSVulkanBaseline(
        PhysicalDeviceProperties props,
        PhysicalDeviceFeatures features,
        string deviceName)
    {
        var supportedFormats = new Dictionary<string, FormatSupport>(HarmonyOSVulkanBaseline.RequiredFormats.Count);
        // We confirm each required format against the device's reported format features rather than
        // assuming it. vkGetPhysicalDeviceFormatProperties is the canonical query; absence of an entry
        // here means the format was not probed and the baseline must treat it as unsupported.
        foreach (string format in HarmonyOSVulkanBaseline.RequiredFormats)
        {
            Format fmt = TryParseVulkanFormat(format);
            if (fmt == Format.Undefined)
                continue;
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, fmt, out FormatProperties fp);
            // VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT = 1 (the lowest bit). We treat any non-zero
            // optimal/linear feature set as "the format is usable by the pipeline", which is the
            // baseline's bar. Avoiding the named Silk FormatFeatures enum keeps this bit-version-stable.
            const ulong sampledImageBit = 1;
            FormatSupport support = FormatSupport.None;
            if (((ulong)fp.OptimalTilingFeatures & sampledImageBit) != 0)
                support |= FormatSupport.OptimalTiling | FormatSupport.SampledImage;
            if (((ulong)fp.LinearTilingFeatures & sampledImageBit) != 0)
                support |= FormatSupport.LinearTiling | FormatSupport.SampledImage;
            supportedFormats[format] = support;
        }

        // Swapchain present modes and surface formats are only knowable once a surface is attached.
        // Until then we cannot confirm FIFO support, so the baseline stays conservative.
        bool hasGraphicsQueue = _graphicsQueueFamily != uint.MaxValue;
        bool hasSurface = _surface.Handle != 0;
        bool supportsFifo = false;
        if (hasSurface && _khrSurface != null)
        {
            uint modeCount = 0;
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, null);
            var modes = new PresentModeKHR[modeCount];
            fixed (PresentModeKHR* p = modes)
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref modeCount, p);
            for (int i = 0; i < modes.Length; i++)
            {
                if (modes[i] == PresentModeKHR.FifoKhr || modes[i] == PresentModeKHR.FifoRelaxedKhr)
                {
                    supportsFifo = true;
                    break;
                }
            }
        }

        // Timestamp query support is per-queue-family (QueueFamilyProperties.TimestampValidBits),
        // not a physical-device property. Probe the selected graphics family.
        bool supportsTimestamps = false;
        if (hasGraphicsQueue)
        {
            uint qfCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref qfCount, null);
            var qfProps = new QueueFamilyProperties[qfCount];
            fixed (QueueFamilyProperties* p = qfProps)
                _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref qfCount, p);
            if (_graphicsQueueFamily < qfProps.Length)
                supportsTimestamps = qfProps[_graphicsQueueFamily].TimestampValidBits > 0;
        }

        var snapshot = new HarmonyOSVulkanCapabilitySnapshot
        {
            ApiVersion = props.ApiVersion,
            DeviceName = deviceName,
            MaxImageDimension2D = (int)props.Limits.MaxImageDimension2D,
            MaxPerStageDescriptorUniformBuffers = (int)props.Limits.MaxPerStageDescriptorUniformBuffers,
            MaxPerStageDescriptorStorageBuffers = (int)props.Limits.MaxPerStageDescriptorStorageBuffers,
            MaxBoundDescriptorSets = (int)props.Limits.MaxBoundDescriptorSets,
            MaxColorAttachments = (int)props.Limits.MaxColorAttachments,
            FramebufferColorSampleCounts = (int)props.Limits.FramebufferColorSampleCounts,
            SupportsSamplerAnisotropy = features.SamplerAnisotropy,
            // VkPipelineCache is core Vulkan 1.0; the device always creates one when available.
            SupportsPipelineCache = true,
            HasGraphicsQueue = hasGraphicsQueue,
            SupportsFifoPresentMode = supportsFifo,
            SupportsTimestampQueries = supportsTimestamps,
            // Subgroup size/basic support requires a Vulkan 1.1 pNext query
            // (PhysicalDeviceSubgroupProperties via GetPhysicalDeviceProperties2). Not queried here
            // yet, so report 0 — the baseline treats this as "unverified" rather than assuming support.
            SubgroupSize = 0,
            SupportsSubgroupBasic = false,
            SupportedFormats = supportedFormats,
        };

        Debug.Log(HarmonyOSVulkanBaseline.FormatCapabilityLog(snapshot));
        if (!hasSurface)
            Debug.Log("[HarmonyOS-Vulkan] surface not yet attached; FIFO/format confirmation deferred.");
        Debug.Log("[HarmonyOS-Vulkan] subgroup support not yet probed (requires Vulkan 1.1 properties2 query); reported as unverified.");
    }

    private static Format TryParseVulkanFormat(string name)
    {
        // Map the baseline format names to Silk.NET Vulkan Format enum values. Keep this explicit
        // rather than reflecting, so an unknown name is a clear no-op instead of an exception.
        return name switch
        {
            "R8G8B8A8Srgb" => Format.R8G8B8A8Srgb,
            "B8G8R8A8Srgb" => Format.B8G8R8A8Srgb,
            "D32Sfloat" => Format.D32Sfloat,
            "D24UnormS8Uint" => Format.D24UnormS8Uint,
            "ETC2R8G8B8UnormBlock" => Format.Etc2R8G8B8UnormBlock,
            "Astc4x4UnormBlock" => Format.Astc4x4UnormBlock,
            _ => Format.Undefined,
        };
    }
#endif

    private string[] EnumerateDeviceExtensionNames()
    {
        uint count = 0;
        Check(_vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, ref count, null),
            "vkEnumerateDeviceExtensionProperties(count)");
        if (count == 0)
            return Array.Empty<string>();

        int propertyCount = checked((int)count);
        var properties = new ExtensionProperties[propertyCount];
        fixed (ExtensionProperties* p = properties)
        {
            Check(_vk.EnumerateDeviceExtensionProperties(
                _physicalDevice,
                (byte*)null,
                ref count,
                p), "vkEnumerateDeviceExtensionProperties");
        }

        var names = new string[propertyCount];
        for (int i = 0; i < propertyCount; i++)
        {
            fixed (ExtensionProperties* property = &properties[i])
                names[i] = Marshal.PtrToStringAnsi((nint)property->ExtensionName) ?? string.Empty;
        }
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    private void CreatePipelineCache()
    {
        string identity = _capabilities.Baseline == null
            ? "unknown"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                _capabilities.Baseline.ToStableLogString())))[..16].ToLowerInvariant();
        string directory = Path.Combine(Application.NoBackupDataPath, "PipelineCache");
        _pipelineCachePath = Path.Combine(directory, $"vulkan-{identity}.bin");

        byte[] initialData = Array.Empty<byte>();
        try
        {
            if (File.Exists(_pipelineCachePath))
                initialData = File.ReadAllBytes(_pipelineCachePath);
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Vulkan] pipeline cache read failed: {exception.Message}");
        }

        fixed (byte* initialPointer = initialData)
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)initialData.Length,
                PInitialData = initialData.Length == 0 ? null : initialPointer,
            };
            Check(_vk.CreatePipelineCache(_device, &cacheInfo, null, out _pipelineCache), "vkCreatePipelineCache");
        }
        _pipelineCacheLoadedBytes = initialData.Length;
        Debug.Log($"[Vulkan] pipeline-cache initialized bytes={_pipelineCacheLoadedBytes} persistent={_pipelineCachePath}");
    }

    private void PersistPipelineCache()
    {
        if (_deviceLoss.IsLost || _pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            return;
        try
        {
            nuint size = 0;
            Check(_vk.GetPipelineCacheData(_device, _pipelineCache, ref size, null),
                "vkGetPipelineCacheData(size)");
            if (size == 0 || size > int.MaxValue)
                return;

            byte[] data = new byte[(int)size];
            fixed (byte* dataPointer = data)
                Check(_vk.GetPipelineCacheData(_device, _pipelineCache, ref size, dataPointer),
                    "vkGetPipelineCacheData");
            string directory = Path.GetDirectoryName(_pipelineCachePath)!;
            Directory.CreateDirectory(directory);
            string temporaryPath = _pipelineCachePath + ".tmp";
            File.WriteAllBytes(temporaryPath, data);
            File.Move(temporaryPath, _pipelineCachePath, overwrite: true);
            _pipelineCachePersistedBytes = data.Length;
            Debug.Log($"[Vulkan] pipeline-cache persisted bytes={_pipelineCachePersistedBytes}");
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[Vulkan] pipeline cache persist failed: {exception.Message}");
        }
    }

    internal static bool IsRecoverableMemoryPressure(Result result) =>
        result is Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory;

    internal void AllocateDeviceMemory(in MemoryAllocateInfo alloc, out DeviceMemory memory, string what)
    {
        MemoryAllocateInfo copy = alloc;
        Result result = _vk.AllocateMemory(_device, &copy, null, out memory);
        if (IsRecoverableMemoryPressure(result))
        {
            RecoverHostMemory(resetTransientPool: !_frameCmdActive && _transientCommandLive == 0);
            result = _vk.AllocateMemory(_device, &copy, null, out memory);
        }
        Check(result, what);
    }

    private void BeginOneTimeCommandBuffer(VkCommandBuffer commandBuffer, string what)
    {
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Result result = _vk.BeginCommandBuffer(commandBuffer, &begin);
        if (IsRecoverableMemoryPressure(result))
        {
            RecoverHostMemory(resetTransientPool: !_frameCmdActive);
            result = _vk.BeginCommandBuffer(commandBuffer, &begin);
        }
        Check(result, what);
    }

    private void DrainPendingRetirementsIfNeeded()
    {
        if (_pendingRetire.Count < MaxPendingRetireBeforeDrain)
            return;
        RetireCompletedSubmissions();
        if (_pendingRetire.Count < MaxPendingRetireBeforeDrain)
            return;
        WaitForFence(_pendingRetire[0].Fence, "vkWaitForFences(drain transient)");
        RetireCompletedSubmissions();
    }

    private void RecoverHostMemory(bool resetTransientPool)
    {
        if (_device.Handle == 0 || _deviceLoss.IsLost)
            return;
        Debug.LogWarning("[Vulkan] host/device memory pressure; waiting idle and retiring one-shot submissions.");
        WaitForGpuIdle("recover host memory");
        RetirePending();
        if (!resetTransientPool || _frameCmdActive || _transientCommandPool.Handle == 0)
            return;
        Check(_vk.ResetCommandPool(_device, _transientCommandPool, 0), "vkResetCommandPool(transient)");
    }

    private VkCommandBuffer AllocateTransientCommandBuffer()
    {
        DrainPendingRetirementsIfNeeded();
        if (_transientCommandFree.Count > 0)
        {
            _transientCommandLive++;
            return _transientCommandFree.Pop();
        }

        Result result = TryAllocateTransientCommandBufferFromPool(out VkCommandBuffer cb);
        if (IsRecoverableMemoryPressure(result))
        {
            RecoverHostMemory(resetTransientPool: !_frameCmdActive && _transientCommandLive == 0);
            result = TryAllocateTransientCommandBufferFromPool(out cb);
        }
        Check(result, "vkAllocateCommandBuffers");
        _transientCommandLive++;
        return cb;
    }

    private Result TryAllocateTransientCommandBufferFromPool(out VkCommandBuffer cb)
    {
        CommandPool pool = _transientCommandPool.Handle != 0 ? _transientCommandPool : _commandPool;
        var alloc = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        return _vk.AllocateCommandBuffers(_device, &alloc, out cb);
    }

    private void FreeTransientCommandBuffer(VkCommandBuffer cb)
    {
        if (cb.Handle == 0)
            return;
        if (_transientCommandLive > 0)
            _transientCommandLive--;
        if (_transientCommandPool.Handle != 0 && _transientCommandFree.Count < MaxCachedTransientCommandBuffers)
        {
            _transientCommandFree.Push(cb);
            return;
        }

        CommandPool pool = _transientCommandPool.Handle != 0 ? _transientCommandPool : _commandPool;
        _vk.FreeCommandBuffers(_device, pool, 1, &cb);
    }

    private void RetirePending()
    {
        for (int i = _pendingRetire.Count - 1; i >= 0; i--)
        {
            RetireSubmission(i);
        }
        for (int slot = 0; slot < MaxFramesInFlight; slot++)
            RetireFrameRecordingSlot(slot);

        DestroySamplers(_unfencedSamplerRetire);
        _unfencedSamplerRetire.Clear();
    }

    private void DiscardPendingRetirements()
    {
        // The device is terminally lost. Vulkan guarantees that destroying the device releases
        // all remaining child objects, so avoid issuing further GPU destruction calls here.
        for (int i = _pendingRetire.Count - 1; i >= 0; i--)
        {
            var (_, _, samplers, buffers, images, framebuffers) = _pendingRetire[i];
            _pendingRetire.RemoveAt(i);
            ReturnSamplerList(samplers);
            ReturnTransientBufferList(buffers);
            ReturnTransientImageList(images);
            ReturnTransientFramebufferList(framebuffers);
        }
        for (int i = 0; i < _unfencedSamplerRetire.Count; i++)
            _unfencedSamplerRetire[i] = default;
        _unfencedSamplerRetire.Clear();
        for (int slot = 0; slot < MaxFramesInFlight; slot++)
        {
            if (_frameRecordingRetire[slot] is not { } recording)
                continue;
            _frameRecordingRetire[slot] = null;
            ReturnSamplerList(recording.Samplers);
            ReturnTransientBufferList(recording.Buffers);
            ReturnTransientImageList(recording.Images);
            ReturnTransientFramebufferList(recording.Framebuffers);
        }
        _frameCmdActive = false;
    }

    private void RetireCompletedSubmissions()
    {
        for (int i = _pendingRetire.Count - 1; i >= 0; i--)
        {
            Result status = _vk.GetFenceStatus(_device, _pendingRetire[i].Fence);
            if (status == Result.NotReady)
                continue;
            if (status == Result.ErrorDeviceLost)
            {
                RecordDeviceLoss("vkGetFenceStatus", status.ToString());
                throw new InvalidOperationException(
                    "vkGetFenceStatus failed: ErrorDeviceLost; refusing further GPU work.");
            }
            Check(status, "vkGetFenceStatus");
            RetireSubmission(i);
        }
    }

    private void RetireSubmission(int index)
    {
        var (cmd, fence, samplers, buffers, images, framebuffers) = _pendingRetire[index];
        // Only retire submissions whose fence has signaled: destroying a staging buffer that the
        // GPU is still reading (vkCmdCopyBufferToImage in flight) is undefined behavior and was
        // silently zeroing large texture uploads on the async render-thread path — the
        // "characters render as pure-black silhouettes" root cause.
        Result status = _vk.GetFenceStatus(_device, fence);
        if (status != Result.Success)
        {
            if (status != Result.NotReady)
                CheckDeviceResult(status, "vkGetFenceStatus(retire)");
            return; // fence not signaled yet — leave in _pendingRetire for a later pass.
        }

        _pendingRetire.RemoveAt(index);
        _vk.DestroyFence(_device, fence, null);
        FreeTransientCommandBuffer(cmd);
        DestroySamplers(samplers);
        ReturnSamplerList(samplers);
        DestroyTransientBuffers(buffers);
        ReturnTransientBufferList(buffers);
        DestroyTransientFramebuffers(framebuffers);
        ReturnTransientFramebufferList(framebuffers);
        DestroyTransientImages(images);
        ReturnTransientImageList(images);
    }

    private void WaitForFence(Fence fence, string operation)
    {
        // One 5s timeout is NOT proof the device is gone — a very slow frame (first heavy
        // compile/upload burst, or a stalled presenter) can exceed it while the GPU is still
        // healthy. Retry once with 10s before declaring a real loss.
        Result result = _vk.WaitForFences(
            _device, 1, &fence, true, GpuWaitTimeoutNanoseconds);
        if (result == Result.Timeout)
        {
            Debug.LogWarning($"[Vulkan] {operation} timed out after 5s; retrying once with 10s before declaring device loss.");
            result = _vk.WaitForFences(
                _device, 1, &fence, true, GpuWaitTimeoutNanoseconds * 2);
        }
        if (result == Result.Timeout)
        {
            _deviceLoss.RecordLoss(operation, result.ToString());
            Debug.LogError($"[Vulkan] {operation} timed out after retry; refusing further GPU work.");
            throw new InvalidOperationException(
                $"{operation} failed: {result}; Vulkan device is not responding.");
        }
        CheckDeviceResult(result, operation);
    }

    private void WaitForGpuIdle(string operation)
    {
        if (_device.Handle == 0 || _deviceLoss.IsLost)
            return;

        // Do not use vkDeviceWaitIdle: it has an unbounded wait and can wedge the host when a
        // driver stops making progress without returning VK_ERROR_DEVICE_LOST. Poll submitted
        // fences instead, keeping shutdown and swapchain resize interruptible.
        long deadline = Environment.TickCount64 + 5_000;
        while (true)
        {
            RetireCompletedSubmissions();
            bool frameWorkPending = false;
            for (int i = 0; i < MaxFramesInFlight; i++)
            {
                Fence fence = _inFlightFences[i];
                if (fence.Handle == 0)
                    continue;
                Result status = _vk.GetFenceStatus(_device, fence);
                if (status == Result.ErrorDeviceLost)
                {
                    RecordDeviceLoss(operation, status.ToString());
                    throw new InvalidOperationException($"{operation} failed: {status}; Vulkan device is lost.");
                }
                CheckDeviceResult(status == Result.NotReady ? Result.Success : status,
                    $"vkGetFenceStatus({operation})");
                frameWorkPending |= status == Result.NotReady;
            }
            if (_pendingRetire.Count == 0 && !frameWorkPending)
                break;
            if (Environment.TickCount64 >= deadline)
            {
                // A timeout is terminal for our host, but is not VK_ERROR_DEVICE_LOST.
                // vkGetDeviceFaultInfoEXT is only valid after an actual device loss.
                _deviceLoss.RecordLoss(operation, Result.Timeout.ToString());
                throw new InvalidOperationException(
                    $"Vulkan GPU did not become idle during {operation}; refusing to block the host.");
            }
            System.Threading.Thread.Sleep(1);
        }
    }

    private void DestroyTransientBuffers(List<VkBufferResource> buffers)
    {
        for (int i = 0; i < buffers.Count; i++)
            DestroyBuffer(buffers[i]);
        buffers.Clear();
    }

    private List<VkBufferResource> RentTransientBufferList() =>
        _transientBufferListPool.Count > 0 ? _transientBufferListPool.Pop() : new List<VkBufferResource>();

    private void ReturnTransientBufferList(List<VkBufferResource> buffers)
    {
        buffers.Clear();
        _transientBufferListPool.Push(buffers);
    }

    private void DestroyTransientImages(List<VkImageResource> images)
    {
        for (int i = 0; i < images.Count; i++)
            DestroyImage(images[i]);
        images.Clear();
    }

    private List<VkImageResource> RentTransientImageList() =>
        _transientImageListPool.Count > 0 ? _transientImageListPool.Pop() : new List<VkImageResource>();

    private void ReturnTransientImageList(List<VkImageResource> images)
    {
        images.Clear();
        _transientImageListPool.Push(images);
    }

    private List<VkFramebufferResource> RentTransientFramebufferList() =>
        _transientFramebufferListPool.Count > 0 ? _transientFramebufferListPool.Pop() : new List<VkFramebufferResource>();

    private void ReturnTransientFramebufferList(List<VkFramebufferResource> framebuffers)
    {
        framebuffers.Clear();
        _transientFramebufferListPool.Push(framebuffers);
    }

    private void DestroyTransientFramebuffers(List<VkFramebufferResource> framebuffers)
    {
        for (int i = 0; i < framebuffers.Count; i++)
            DestroyFramebuffer(framebuffers[i]);
        framebuffers.Clear();
    }

    private void SubmitTimestampFrameStart(int frameIndex)
    {
        if ((_timestampQueryPool.Handle == 0 && _occlusionQueryPool.Handle == 0) || !HasSwapchain)
            return;

        VkCommandBuffer commandBuffer = _frameCommandBuffers[frameIndex];
        Check(_vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(timestamp start)");
        BeginOneTimeCommandBuffer(commandBuffer, "vkBeginCommandBuffer(timestamp start)");
        if (_timestampQueryPool.Handle != 0)
        {
            uint firstQuery = (uint)(frameIndex * TimestampQueriesPerFrame);
            _gpuCommandTimingCounts[frameIndex] = 0;
            _gpuCommandTimingSurfaces[frameIndex] = _activePresentationSurface;
            _vk.CmdResetQueryPool(commandBuffer, _timestampQueryPool, firstQuery, TimestampQueriesPerFrame);
            _vk.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.TopOfPipeBit, _timestampQueryPool, firstQuery);
        }
        if (_occlusionQueryPool.Handle != 0)
        {
            _vk.CmdResetQueryPool(commandBuffer, _occlusionQueryPool,
                (uint)(frameIndex * OcclusionSlotsPerFrame), OcclusionSlotsPerFrame);
        }
        Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(timestamp start)");

        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, default), "vkQueueSubmit(timestamp start)");
        _timestampFrameSubmitted[frameIndex] = true;
        _timestampFrameIds[frameIndex] = ++_nextTimestampFrameId;
    }

    private void SubmitFrameEnd(Semaphore wait, Semaphore signal, Fence fence, int frameIndex)
    {
        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        bool hasWait = wait.Handle != 0;
        VkCommandBuffer commandBuffer = default;
        bool writeTimestamp = _timestampQueryPool.Handle != 0 && _timestampFrameSubmitted[frameIndex];
        if (writeTimestamp)
        {
            commandBuffer = _timestampEndCommandBuffers[frameIndex];
            Check(_vk.ResetCommandBuffer(commandBuffer, 0), "vkResetCommandBuffer(timestamp end)");
            BeginOneTimeCommandBuffer(commandBuffer, "vkBeginCommandBuffer(timestamp end)");
            _vk.CmdWriteTimestamp(commandBuffer, PipelineStageFlags.BottomOfPipeBit, _timestampQueryPool,
                (uint)(frameIndex * TimestampQueriesPerFrame + 1));
            Check(_vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer(timestamp end)");
        }
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = hasWait ? 1u : 0u,
            PWaitSemaphores = hasWait ? &wait : null,
            PWaitDstStageMask = hasWait ? &waitStage : null,
            CommandBufferCount = writeTimestamp ? 1u : 0u,
            PCommandBuffers = writeTimestamp ? &commandBuffer : null,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signal,
        };
        CheckDeviceResult(_vk.QueueSubmit(_graphicsQueue, 1, &submit, fence), "vkQueueSubmit(frame-end)");
    }

    private void PublishTimestampFrame(int frameIndex)
    {
        if (_timestampQueryPool.Handle == 0 || !_timestampFrameSubmitted[frameIndex])
            return;

        Span<ulong> timestamps = stackalloc ulong[2];
        Result result = _vk.GetQueryPoolResults(_device, _timestampQueryPool, (uint)(frameIndex * TimestampQueriesPerFrame), 2,
            (nuint)(timestamps.Length * sizeof(ulong)), timestamps, sizeof(ulong), QueryResultFlags.Result64Bit);
        _timestampFrameSubmitted[frameIndex] = false;
        PublishGpuCommandTimings(frameIndex);
        if (result != Result.Success || timestamps[1] < timestamps[0])
            return;

        float durationMs = (timestamps[1] - timestamps[0]) * _timestampPeriodNanoseconds / 1_000_000f;
        _lastGpuTimestampFrame = new GpuTimestampFrame(_timestampFrameIds[frameIndex], durationMs);
        _hasGpuTimestampFrame = true;
    }

    private void PublishGpuCommandTimings(int frameIndex)
    {
        int count = _gpuCommandTimingCounts[frameIndex];
        if (count == 0) return;
        Span<ulong> values = stackalloc ulong[MaxGpuCommandTimingsPerFrame * 2];
        Result result = _vk.GetQueryPoolResults(_device, _timestampQueryPool,
            (uint)(frameIndex * TimestampQueriesPerFrame + 2), (uint)(count * 2),
            (nuint)(count * 2 * sizeof(ulong)), values, sizeof(ulong), QueryResultFlags.Result64Bit);
        for (int i = 0; i < count; i++)
        {
            string name = _gpuCommandTimingNames[frameIndex, i] ?? string.Empty;
            _gpuCommandTimingNames[frameIndex, i] = null;
            if (result != Result.Success || values[i * 2 + 1] < values[i * 2])
            { _gpuCommandTimings.RecordDropped(1); continue; }
            var sample = new GpuCommandTiming(_timestampFrameIds[frameIndex], _gpuCommandTimingSurfaces[frameIndex],
                name, (values[i * 2 + 1] - values[i * 2]) * _timestampPeriodNanoseconds / 1_000_000f);
            _gpuCommandTimings.Publish(in sample);
        }
        _gpuCommandTimingCounts[frameIndex] = 0;
    }

    private Fence CreateFence(bool signaled)
    {
        var ci = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = signaled ? FenceCreateFlags.SignaledBit : 0,
        };
        Check(_vk.CreateFence(_device, &ci, null, out Fence fence), "vkCreateFence");
        return fence;
    }

    private Semaphore CreateSemaphore()
    {
        var ci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        Check(_vk.CreateSemaphore(_device, &ci, null, out Semaphore sem), "vkCreateSemaphore");
        return sem;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("VulkanGraphicsDevice has not been initialized.");
        _deviceLoss.ThrowIfLost();
    }

    /// <summary>Records a terminal device loss and captures VK_EXT_device_fault details once.</summary>
    private void RecordDeviceLoss(string operation, string result)
    {
        _deviceLoss.RecordLoss(operation, result);
        CaptureDeviceFaultInfo(operation);
    }

    /// <summary>
    /// Best-effort capture of driver-side fault details via VK_EXT_device_fault. Runs once on the
    /// first terminal loss; every line is Debug.LogError'd so it lands in the crash report's
    /// recent-logs section, and the vendor binary is kept for the editor crash handler to dump.
    /// </summary>
    /// <summary>
    /// VK_EXT_device_fault ABI: two uint32 counts and one VkDeviceSize; the info
    /// struct contains three pointers, with no inline counts. Keep offsets tested
    /// against the Khronos headers and Silk bindings: a mismatch corrupts native memory.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceFaultCounts
    {
        public StructureType SType;
        public nint PNext;
        public uint AddressInfoCount;
        public uint VendorInfoCount;
        public ulong VendorBinarySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DeviceFaultInfo
    {
        public StructureType SType;
        public nint PNext;
        public fixed byte Description[256];
        public nint PAddressInfos;
        public nint PVendorInfos;
        public nint PVendorBinaryData;
    }

    private void CaptureDeviceFaultInfo(string operation)
    {
        if (_deviceFaultCaptured)
            return;
        _deviceFaultCaptured = true;

        if (_getDeviceFaultInfo == null)
        {
            Debug.LogError($"[Vulkan] device fault capture unavailable (VK_EXT_device_fault not exposed by driver); operation={operation}");
            return;
        }

        try
        {
            var counts = new DeviceFaultCounts { SType = StructureType.DeviceFaultCountsExt };
            Result countsResult = _getDeviceFaultInfo(_device, &counts, null);
            if (countsResult != Result.Success)
            {
                Debug.LogError($"[Vulkan] device fault counts query failed: {countsResult}");
                return;
            }

            ClampDeviceFaultCapacities(ref counts);
            int addressCount = (int)counts.AddressInfoCount;
            int vendorCount = (int)counts.VendorInfoCount;
            int blobLength = (int)counts.VendorBinarySize;
            DeviceFaultAddressInfoEXT[]? addressInfos = addressCount > 0 ? new DeviceFaultAddressInfoEXT[addressCount] : null;
            DeviceFaultVendorInfoEXT[]? vendorInfos = vendorCount > 0 ? new DeviceFaultVendorInfoEXT[vendorCount] : null;
            string[]? vendorTexts = vendorCount > 0 ? new string[vendorCount] : null;
            byte[] blob = blobLength > 0 ? new byte[blobLength] : Array.Empty<byte>();

            string description;
            fixed (DeviceFaultAddressInfoEXT* a = addressInfos)
            fixed (DeviceFaultVendorInfoEXT* v = vendorInfos)
            fixed (byte* b = blob)
            {
                var info = new DeviceFaultInfo
                {
                    SType = StructureType.DeviceFaultInfoExt,
                    PAddressInfos = (nint)a,
                    PVendorInfos = (nint)v,
                    PVendorBinaryData = (nint)b,
                };
                Result infoResult = _getDeviceFaultInfo(_device, &counts, &info);
                if (infoResult != Result.Success && infoResult != Result.Incomplete)
                {
                    Debug.LogError($"[Vulkan] device fault info query failed: {infoResult}");
                    return;
                }
                description = DecodeAnsi(new ReadOnlySpan<byte>(info.Description, VkMaxDescriptionSize));
                addressCount = (int)Math.Min(counts.AddressInfoCount, (uint)addressCount);
                vendorCount = (int)Math.Min(counts.VendorInfoCount, (uint)vendorCount);
                blobLength = (int)Math.Min(counts.VendorBinarySize, (ulong)blobLength);
                if (v != null)
                {
                    for (int i = 0; i < vendorCount; i++)
                        vendorTexts![i] = DecodeAnsi(new ReadOnlySpan<byte>(v[i].Description, VkMaxDescriptionSize));
                }
            }

            if (addressInfos != null) Array.Resize(ref addressInfos, addressCount);
            if (vendorInfos != null) Array.Resize(ref vendorInfos, vendorCount);
            if (vendorTexts != null) Array.Resize(ref vendorTexts, vendorCount);
            Array.Resize(ref blob, blobLength);

            foreach (string line in SummarizeDeviceFault(description, addressInfos, vendorInfos, vendorTexts, blob))
                Debug.LogError($"[Vulkan] {line}");

            LastDeviceFaultVendorBlob = blob;
            LastDeviceFaultSummary = description;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Vulkan] device fault capture threw: {ex.Message}");
        }
    }

    private const int VkMaxDescriptionSize = 256;

    internal static void ClampDeviceFaultCapacities(ref DeviceFaultCounts counts)
    {
        // The second native call reads these as capacities, not the original demand.
        counts.AddressInfoCount = Math.Min(counts.AddressInfoCount, 64u);
        counts.VendorInfoCount = Math.Min(counts.VendorInfoCount, 64u);
        counts.VendorBinarySize = 0; // deviceFaultVendorBinary is deliberately disabled.
    }

    internal static string DecodeAnsi(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
            length = bytes.Length;
        return Encoding.ASCII.GetString(bytes[..length]);
    }

    /// <summary>Formats driver fault data into log lines (unit-tested; loss path only, allocations acceptable).</summary>
    internal static string[] SummarizeDeviceFault(
        string description,
        DeviceFaultAddressInfoEXT[]? addressInfos,
        DeviceFaultVendorInfoEXT[]? vendorInfos,
        string[]? vendorTexts,
        ReadOnlySpan<byte> vendorBlob)
    {
        var lines = new List<string>(4 + (addressInfos?.Length ?? 0) + (vendorInfos?.Length ?? 0));
        lines.Add($"device fault description: {(string.IsNullOrEmpty(description) ? "<none provided by driver>" : description)}");
        if (addressInfos == null || addressInfos.Length == 0)
        {
            lines.Add("device fault addresses: <none reported>");
        }
        else
        {
            for (int i = 0; i < addressInfos.Length; i++)
            {
                ref DeviceFaultAddressInfoEXT a = ref addressInfos[i];
                lines.Add($"device fault address[{i}]: type={a.AddressType} address=0x{a.ReportedAddress:X16} precision=0x{a.AddressPrecision:X}");
            }
        }
        if (vendorInfos == null || vendorInfos.Length == 0)
        {
            lines.Add("device fault vendor info: <none reported>");
        }
        else
        {
            for (int i = 0; i < vendorInfos.Length; i++)
            {
                ref DeviceFaultVendorInfoEXT v = ref vendorInfos[i];
                string text = i < (vendorTexts?.Length ?? 0) ? vendorTexts![i] ?? string.Empty : string.Empty;
                lines.Add(text.Length == 0
                    ? $"device fault vendor[{i}]: code=0x{v.VendorFaultCode:X16} data=0x{v.VendorFaultData:X16}"
                    : $"device fault vendor[{i}]: code=0x{v.VendorFaultCode:X16} data=0x{v.VendorFaultData:X16} text=\"{text}\"");
            }
        }
        lines.Add(vendorBlob.IsEmpty
            ? "device fault vendor binary: <none>"
            : $"device fault vendor binary: {vendorBlob.Length} byte(s); head={Convert.ToHexString(vendorBlob[..Math.Min(vendorBlob.Length, 64)])}");
        return lines.ToArray();
    }

    private void ThrowIfDeviceLost(Result result, string operation)
    {
        if (result == Result.ErrorDeviceLost)
        {
            RecordDeviceLoss(operation, result.ToString());
            Debug.LogError($"[Vulkan] device lost operation={operation}; result={result}; refusing further GPU work.");
            throw new InvalidOperationException(
                $"{operation} failed: {result}; Vulkan device loss is terminal for this host instance.");
        }
    }

    private void CheckDeviceResult(Result result, string what)
    {
        ThrowIfDeviceLost(result, what);
        if (result != Result.Success)
            throw new InvalidOperationException($"{what} failed: {result}");
    }

    internal static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{what} failed: {result}");
    }
}

internal sealed class VkBufferResource
{
    public Buffer Buffer;
    public DeviceMemory Memory;
    public ulong Size;
    public BufferType Type;
    public bool Dynamic;
    /// <summary>Persistent host mapping (transient uniform arenas). vkFreeMemory implicitly
    /// unmaps, so no explicit cleanup is required at destroy time.</summary>
    public IntPtr Mapped;
}

internal sealed class VkImageResource
{
    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Sampler Sampler;
    public Format Format;
    public TextureImageFormat EngineFormat;
    public TextureType Type;
    public uint Width;
    public uint Height;
    public uint Depth = 1;
    public uint MipLevels = 1;

    /// <summary>Multisample count of the image (1 = single-sample).</summary>
    public uint SampleCount = 1;
    public bool IsMultisampled => SampleCount > 1;
    public byte CubeInitializedFaces;
    public byte[] CubeInitializedFacesByMip = Array.Empty<byte>();
    public uint AvailableMipLevels;
    public ImageLayout Layout = ImageLayout.Undefined;
    public TextureWrap WrapS = TextureWrap.Repeat;
    public TextureWrap WrapT = TextureWrap.Repeat;
    public TextureWrap WrapR = TextureWrap.Repeat;
    public TextureMin MinFilter = TextureMin.Linear;
    public TextureMag MagFilter = TextureMag.Linear;

    public VkImageResource CloneForRetirement() => new()
    {
        Image = Image,
        Memory = Memory,
        View = View,
        Sampler = Sampler,
        Format = Format,
        EngineFormat = EngineFormat,
        Type = Type,
        Width = Width,
        Height = Height,
        Depth = Depth,
        MipLevels = MipLevels,
        SampleCount = SampleCount,
        CubeInitializedFaces = CubeInitializedFaces,
        CubeInitializedFacesByMip = CubeInitializedFacesByMip,
        AvailableMipLevels = AvailableMipLevels,
        Layout = Layout,
        WrapS = WrapS,
        WrapT = WrapT,
        WrapR = WrapR,
        MinFilter = MinFilter,
        MagFilter = MagFilter,
    };
}

internal sealed class VkFramebufferResource
{
    public GraphicsFrameBuffer? Owner;
    public Framebuffer Framebuffer;
    public RenderPass RenderPass;
    public uint Width;
    public uint Height;
    public Format ColorFormat;
    public VkColorAttachmentFormats ColorFormats;
    public Format DepthFormat;
    public uint[] ColorHandles = Array.Empty<uint>();
    public uint[] ColorMipLevels = Array.Empty<uint>();
    public uint[] ColorArrayLayers = Array.Empty<uint>();
    public ImageView[] AttachmentViews = Array.Empty<ImageView>();
    public ulong[] AttachmentImages = Array.Empty<ulong>();
    public uint DepthHandle;

    /// <summary>Sample count shared by every attachment; feeds the pipeline key.</summary>
    public uint SampleCount = 1;
}

internal sealed class VkVertexArrayResource
{
    public uint VertexBuffer;
    public uint IndexBuffer;
    public uint InstanceBuffer;
    public VertexFormat Format = null!;
    public VertexFormat? InstanceFormat;
}

internal sealed class VkShaderResource
{
    public ShaderModule VertModule;
    public ShaderModule FragModule;
    public Pipeline Pipeline;
    public PipelineLayout PipelineLayout;
    public bool Valid;
}

internal sealed class VkShaderLayoutResource
{
    public DescriptorSetLayout DescriptorSetLayout;
    public PipelineLayout PipelineLayout;
    public ShaderDescriptorLayoutPlan Plan = null!;
}

internal sealed class VkShaderModuleResource
{
    public ShaderModule VertexModule;
    public ShaderModule FragmentModule;
    public ShaderModule ComputeModule;
}

internal readonly struct VkGraphicsPipelineKey : IEquatable<VkGraphicsPipelineKey>
{
    private readonly GraphicsPipelineKey _pipeline;
    private readonly VkRenderTargetFormats _targetFormats;

    public VkGraphicsPipelineKey(GraphicsPipelineKey pipeline, VkRenderTargetFormats targetFormats)
    {
        _pipeline = pipeline;
        _targetFormats = targetFormats;
    }

    public bool Equals(VkGraphicsPipelineKey other) =>
        _pipeline.Equals(other._pipeline) && _targetFormats.Equals(other._targetFormats);

    public override bool Equals(object? obj) => obj is VkGraphicsPipelineKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_pipeline, _targetFormats);
}

internal readonly struct VkRenderTargetFormats : IEquatable<VkRenderTargetFormats>
{
    public VkRenderTargetFormats(VkColorAttachmentFormats colorFormats, Format depthFormat, uint sampleCount = 1)
    {
        ColorFormats = colorFormats;
        DepthFormat = depthFormat;
        SampleCount = sampleCount < 1 ? 1 : sampleCount;
    }

    public VkColorAttachmentFormats ColorFormats { get; }
    public Format DepthFormat { get; }

    /// <summary>
    /// Multisample count of the bound attachments. Part of both the pipeline cache key and the
    /// pipeline-compatibility render-pass cache key: a render pass declaring 1× attachments is not
    /// compatible with a 4× framebuffer, and a pipeline's rasterizationSamples must match the pass.
    /// </summary>
    public uint SampleCount { get; }

    public bool Equals(VkRenderTargetFormats other) =>
        ColorFormats.Equals(other.ColorFormats) && DepthFormat == other.DepthFormat && SampleCount == other.SampleCount;

    public override bool Equals(object? obj) => obj is VkRenderTargetFormats other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(ColorFormats, DepthFormat, SampleCount);
}

internal readonly struct VkColorAttachmentFormats : IEquatable<VkColorAttachmentFormats>
{
    private readonly Format _format0;
    private readonly Format _format1;
    private readonly Format _format2;
    private readonly Format _format3;
    private readonly Format _format4;
    private readonly Format _format5;
    private readonly Format _format6;
    private readonly Format _format7;

    public VkColorAttachmentFormats(Format format)
    {
        Count = 1;
        _format0 = format;
        _format1 = default;
        _format2 = default;
        _format3 = default;
        _format4 = default;
        _format5 = default;
        _format6 = default;
        _format7 = default;
    }

    public VkColorAttachmentFormats(ReadOnlySpan<Format> formats)
    {
        if (formats.Length is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(formats), "Vulkan supports one through eight color attachments in this RHI slice.");

        Count = formats.Length;
        _format0 = formats[0];
        _format1 = formats.Length > 1 ? formats[1] : default;
        _format2 = formats.Length > 2 ? formats[2] : default;
        _format3 = formats.Length > 3 ? formats[3] : default;
        _format4 = formats.Length > 4 ? formats[4] : default;
        _format5 = formats.Length > 5 ? formats[5] : default;
        _format6 = formats.Length > 6 ? formats[6] : default;
        _format7 = formats.Length > 7 ? formats[7] : default;
    }

    public int Count { get; }

    public Format this[int index] => index switch
    {
        0 when Count > 0 => _format0,
        1 when Count > 1 => _format1,
        2 when Count > 2 => _format2,
        3 when Count > 3 => _format3,
        4 when Count > 4 => _format4,
        5 when Count > 5 => _format5,
        6 when Count > 6 => _format6,
        7 when Count > 7 => _format7,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public bool Equals(VkColorAttachmentFormats other) =>
        Count == other.Count &&
        _format0 == other._format0 &&
        _format1 == other._format1 &&
        _format2 == other._format2 &&
        _format3 == other._format3 &&
        _format4 == other._format4 &&
        _format5 == other._format5 &&
        _format6 == other._format6 &&
        _format7 == other._format7;

    public override bool Equals(object? obj) => obj is VkColorAttachmentFormats other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Count);
        hash.Add(_format0);
        hash.Add(_format1);
        hash.Add(_format2);
        hash.Add(_format3);
        hash.Add(_format4);
        hash.Add(_format5);
        hash.Add(_format6);
        hash.Add(_format7);
        return hash.ToHashCode();
    }
}
