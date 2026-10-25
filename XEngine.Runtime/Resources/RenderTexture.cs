// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime.Resources;

public sealed class RenderTexture : EngineObject, ISerializable
{
    private GraphicsFrameBuffer _frameBuffer;
    public GraphicsFrameBuffer frameBuffer { get { EnsureNotDisposed(); return _frameBuffer; } private set => _frameBuffer = value; }
    public Texture2D MainTexture { get { EnsureNotDisposed(); return InternalTextures[0]; } }
    private Texture2D[] _internalTextures;
    public Texture2D[] InternalTextures { get { EnsureNotDisposed(); return _internalTextures; } private set => _internalTextures = value; }
    private Texture2D _internalDepth;
    public Texture2D InternalDepth { get { EnsureNotDisposed(); return _internalDepth; } private set => _internalDepth = value; }

    private int _width;
    private int _height;
    public int Width { get { EnsureNotDisposed(); return _width; } private set => _width = value; }
    public int Height { get { EnsureNotDisposed(); return _height; } private set => _height = value; }
    private int numTextures;
    private bool hasDepthAttachment;
    private TextureImageFormat[] textureFormats;
    private int sampleCount = 1;
    internal bool HasDepthAttachment => hasDepthAttachment;
    internal TextureImageFormat[] ColorFormats => textureFormats;

    /// <summary>
    /// Multisample count of every attachment (1 = single-sample). A multisample target can only be
    /// rendered into and then resolved (<c>CommandBuffer.ResolveTexture</c>) — its textures cannot
    /// be sampled, blitted from, or read back.
    /// </summary>
    public int SampleCount => sampleCount;

    /// <summary>True when this target must be resolved before anything can sample it.</summary>
    public bool IsMultisampled => sampleCount > 1;

    /// <summary>True while this texture belongs to the transient pool — its contents may be
    /// recycled within a frame, which the Frame Debugger preview flags to the user.</summary>
    internal bool IsTransient { get; private set; }

    /// <summary>Names this target for the Frame Debugger / GPU debug tooling.</summary>
    public void SetDebugName(string name)
    {
        Name = name;
        if (_frameBuffer != null)
            _frameBuffer.DebugName = name;
    }

    public RenderTexture() : base("RenderTexture")
    {
        Width = 0;
        Height = 0;
        numTextures = 0;
        hasDepthAttachment = false;
        textureFormats = [];
    }

    public RenderTexture(int Width, int Height, bool hasDepthAttachment, TextureImageFormat[] formats)
        : this(Width, Height, hasDepthAttachment, formats, 1)
    {
    }

    /// <param name="sampleCount">
    /// Multisample count (1 = single-sample). Above 1 every color and depth attachment is created
    /// multisampled — a render pass requires all its attachments to share one sample count.
    /// </param>
    public RenderTexture(int Width, int Height, bool hasDepthAttachment, TextureImageFormat[] formats, int sampleCount)
        : this(Width, Height, hasDepthAttachment, formats, sampleCount, TextureImageFormat.Depth24f) { }

    internal RenderTexture(int Width, int Height, bool hasDepthAttachment, TextureImageFormat[] formats,
        int sampleCount, TextureImageFormat depthFormat) : base("RenderTexture")
    {
        this.Width = Width;
        this.Height = Height;
        numTextures = formats?.Length ?? throw new ArgumentNullException(nameof(formats), "Texture formats cannot be null.");
        this.hasDepthAttachment = hasDepthAttachment;
        if (sampleCount < 1 || (sampleCount & (sampleCount - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Sample count must be a power of two.");
        this.sampleCount = sampleCount;

        if (numTextures < 0 || numTextures > Graphics.MaxFramebufferColorAttachments)
            throw new Exception("Invalid number of textures! [0-" + Graphics.MaxFramebufferColorAttachments + "]");

        textureFormats = formats;
        uint samples = (uint)sampleCount;

        GraphicsFrameBuffer.Attachment[] attachments = new GraphicsFrameBuffer.Attachment[numTextures + (hasDepthAttachment ? 1 : 0)];
        InternalTextures = new Texture2D[numTextures];
        for (int i = 0; i < numTextures; i++)
        {
            InternalTextures[i] = new Texture2D((uint)Width, (uint)Height, false, textureFormats[i], samples);
            // Filter/wrap state is meaningless on a multisample attachment (it is resolved, never
            // sampled) and some backends reject sampler work against it, so only set it at 1x.
            if (samples == 1)
            {
                InternalTextures[i].SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
                InternalTextures[i].SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            }
            attachments[i] = new GraphicsFrameBuffer.Attachment { Texture = InternalTextures[i].Handle, IsDepth = false };
        }

        if (hasDepthAttachment)
        {
            InternalDepth = new Texture2D((uint)Width, (uint)Height, false, depthFormat, samples);
            attachments[numTextures] = new GraphicsFrameBuffer.Attachment { Texture = InternalDepth.Handle, IsDepth = true };
        }

        frameBuffer = Graphics.CreateFramebuffer(attachments, (uint)Width, (uint)Height);
        RenderTargetRegistry.Register(frameBuffer, this);
    }

    public override void OnDispose()
    {
        if (_frameBuffer == null) return;

        _frameBuffer.Dispose();

        foreach (Texture2D texture in _internalTextures)
            texture.Dispose();

        // Dispose depth texture if present
        if (hasDepthAttachment && _internalDepth != null)
            _internalDepth.Dispose();
    }

    ~RenderTexture() => Dispose();

    public void Serialize(ref EchoObject compoundTag, SerializationContext ctx)
    {
        compoundTag.Add("Width", new(Width));
        compoundTag.Add("Height", new(Height));
        compoundTag.Add("NumTextures", new(numTextures));
        compoundTag.Add("HasDepthAttachment", new((byte)(hasDepthAttachment ? 1 : 0)));
        EchoObject textureFormatsTag = EchoObject.NewList();
        foreach (TextureImageFormat format in textureFormats)
            textureFormatsTag.ListAdd(new((byte)format));
        compoundTag.Add("TextureFormats", textureFormatsTag);
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Width = value["Width"].IntValue;
        Height = value["Height"].IntValue;
        numTextures = value["NumTextures"].IntValue;
        hasDepthAttachment = value["HasDepthAttachment"].ByteValue == 1;
        textureFormats = new TextureImageFormat[numTextures];
        EchoObject? textureFormatsTag = value.Get("TextureFormats");
        for (int i = 0; i < numTextures; i++)
            textureFormats[i] = (TextureImageFormat)textureFormatsTag[i].ByteValue;


        Type[] param = new[] { typeof(int), typeof(int), typeof(bool), typeof(TextureImageFormat[]) };
        object[] values = new object[] { Width, Height, hasDepthAttachment, textureFormats };
        typeof(RenderTexture).GetConstructor(param).Invoke(this, values);
    }

    #region Pool

    private readonly struct RenderTextureKey(int width, int height, bool hasDepth, TextureImageFormat[] format, int samples = 1)
        : IEquatable<RenderTextureKey>
    {
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly bool HasDepth = hasDepth;
        public readonly TextureImageFormat[] Format = format;

        /// <summary>Part of pool identity: a 1x request must never be served a 4x target.</summary>
        public readonly int Samples = samples;

        public bool Equals(RenderTextureKey key)
        {
            if (Width == key.Width && Height == key.Height && HasDepth == key.HasDepth &&
                Samples == key.Samples && Format.Length == key.Format.Length)
            {
                for (int i = 0; i < Format.Length; i++)
                    if (Format[i] != key.Format[i])
                        return false;
                return true;
            }
            return false;
        }
        public override bool Equals(object? obj) => obj is RenderTextureKey key && Equals(key);
        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 23 + Width.GetHashCode();
            hash = hash * 23 + Height.GetHashCode();
            hash = hash * 23 + HasDepth.GetHashCode();
            hash = hash * 23 + Samples.GetHashCode();
            foreach (TextureImageFormat format in Format)
                hash = hash * 23 + ((int)format).GetHashCode();
            return hash;
        }
        public static bool operator ==(RenderTextureKey left, RenderTextureKey right) => left.Equals(right);
        public static bool operator !=(RenderTextureKey left, RenderTextureKey right) => !(left == right);
    }

    private static Dictionary<RenderTextureKey, List<(RenderTexture, long frameCreated)>> pool = [];
    private static Dictionary<RenderTextureKey, List<(RenderTexture, long frameAcquired)>> active = [];
    private const int MaxUnusedFrames = 10;
    private const int MaxActiveFrames = 3; // Warn if held longer than 3 frames

    public static RenderTexture GetTemporaryRT(int width, int height, bool hasDepth, TextureImageFormat[] format)
    {
        var key = new RenderTextureKey(width, height, hasDepth, format);
        RenderStats.RecordTransientRenderTarget(width, height, hasDepth, format);

        RenderTexture renderTexture;
        if (pool.TryGetValue(key, out List<(RenderTexture, long frameCreated)>? list) && list.Count > 0)
        {
            int i = list.Count - 1;
            renderTexture = list[i].Item1;
            list.RemoveAt(i);
        }
        else
        {
            renderTexture = new RenderTexture(width, height, hasDepth, format);
        }

        // Track in active pool
        if (!active.TryGetValue(key, out List<(RenderTexture, long frameAcquired)>? activeList))
        {
            activeList = [];
            active[key] = activeList;
        }
        activeList.Add((renderTexture, Time.FrameCount));

        renderTexture.IsTransient = true;
        if (string.IsNullOrEmpty(renderTexture.Name) || renderTexture.Name == "RenderTexture")
            renderTexture.SetDebugName($"TempRT {width}x{height}");

        return renderTexture;
    }

    /// <summary>
    /// Remove a temporary RT from leak tracking without returning it to the pool.
    /// RTHandle uses this when it adopts a post-process replacement across frames.
    /// </summary>
    internal static void ClaimTemporaryRT(RenderTexture renderTexture)
    {
        if (renderTexture == null || renderTexture.IsDisposed)
            return;

        var key = new RenderTextureKey(
            renderTexture.Width,
            renderTexture.Height,
            renderTexture.hasDepthAttachment,
            renderTexture.textureFormats,
            renderTexture.sampleCount);

        renderTexture.IsTransient = false;

        if (!active.TryGetValue(key, out List<(RenderTexture, long frameAcquired)>? activeList))
            return;

        for (int i = activeList.Count - 1; i >= 0; i--)
        {
            if (activeList[i].Item1 == renderTexture)
            {
                activeList.RemoveAt(i);
                break;
            }
        }
    }

    public static void ReleaseTemporaryRT(RenderTexture renderTexture)
    {
        if (renderTexture.IsDisposed)
            return;

        var key = new RenderTextureKey(
            renderTexture.Width,
            renderTexture.Height,
            renderTexture.hasDepthAttachment,
            renderTexture.textureFormats,
            renderTexture.sampleCount);

        // Remove from active pool
        if (active.TryGetValue(key, out List<(RenderTexture, long frameAcquired)>? activeList))
        {
            for (int i = activeList.Count - 1; i >= 0; i--)
            {
                if (activeList[i].Item1 == renderTexture)
                {
                    activeList.RemoveAt(i);
                    break;
                }
            }
        }

        // Add to pool for reuse
        if (!pool.TryGetValue(key, out List<(RenderTexture, long frameCreated)>? list))
        {
            list = [];
            pool[key] = list;
        }

        list.Add((renderTexture, Time.FrameCount));
    }

    internal static void DisposeTemporaryPool()
    {
        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameCreated)>> pair in pool)
            for (int i = 0; i < pair.Value.Count; i++)
                pair.Value[i].Item1.Dispose();

        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameAcquired)>> pair in active)
            for (int i = 0; i < pair.Value.Count; i++)
                pair.Value[i].Item1.Dispose();

        pool.Clear();
        active.Clear();
    }

    // Reused across frames; UpdatePool runs once per rendered frame on the main thread.
    private static readonly List<RenderTexture> s_disposableScratch = [];

    public static void UpdatePool()
    {
        List<RenderTexture> disposableTextures = s_disposableScratch;
        disposableTextures.Clear();

        // Check for leaked active render textures (held longer than MaxActiveFrames)
        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameAcquired)>> pair in active)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
            {
                (RenderTexture renderTexture, long frameAcquired) = pair.Value[i];
                long framesActive = Time.FrameCount - frameAcquired;

                if (framesActive > MaxActiveFrames)
                {
                    Debug.LogWarning($"RenderTexture leak detected! Texture ({renderTexture.Width}x{renderTexture.Height}) has been active for {framesActive} frames (max: {MaxActiveFrames}). Auto-disposing to prevent memory leak.");
                    disposableTextures.Add(renderTexture);
                    pair.Value.RemoveAt(i);
                }
            }
        }

        // Clean up unused textures in pool
        foreach (KeyValuePair<RenderTextureKey, List<(RenderTexture, long frameCreated)>> pair in pool)
        {
            for (int i = pair.Value.Count - 1; i >= 0; i--)
            {
                (RenderTexture renderTexture, long frameCreated) = pair.Value[i];
                if (Time.FrameCount - frameCreated > MaxUnusedFrames)
                {
                    disposableTextures.Add(renderTexture);
                    pair.Value.RemoveAt(i);
                }
            }
        }

        foreach (RenderTexture renderTexture in disposableTextures)
            renderTexture.Dispose();

        // Clean up empty dictionary entries to prevent unbounded key accumulation
        List<RenderTextureKey>? emptyKeys = null;
        foreach (var pair in pool)
            if (pair.Value.Count == 0)
                (emptyKeys ??= []).Add(pair.Key);
        if (emptyKeys != null) foreach (var k in emptyKeys) pool.Remove(k);

        emptyKeys = null;
        foreach (var pair in active)
            if (pair.Value.Count == 0)
                (emptyKeys ??= []).Add(pair.Key);
        if (emptyKeys != null) foreach (var k in emptyKeys) active.Remove(k);
    }

    #endregion

}
