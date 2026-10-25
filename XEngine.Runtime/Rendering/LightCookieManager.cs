// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.RHI;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Binds the directional cookie as a dedicated 2D texture and packs local cookies into
/// <see cref="LightCookieAtlas"/>. No cookies → dummy white textures, enabled=0, lighting unchanged.
/// </summary>
public sealed class LightCookieManager
{
    public static LightCookieManager Instance { get; } = new();

    private readonly List<(Texture2D texture, int x, int y, int w, int h)> _blitTiles = [];
    private Texture2D? _empty;
    private IGraphicsDevice? _emptyDevice;
    private Light? _directional;
    private bool _packedLocals;
    private bool _needsAtlasClear;

    public int PackedLocalCount { get; private set; }
    public bool DirectionalEnabled { get; private set; }

    public void BeginFrame(bool hasLocalCookies)
    {
        _blitTiles.Clear();
        PackedLocalCount = 0;
        _packedLocals = hasLocalCookies;
        _needsAtlasClear = false;
        if (hasLocalCookies)
        {
            LightCookieAtlas.TryInitialize();
            LightCookieAtlas.Clear();
            _needsAtlasClear = true;
        }
    }

    public void SetDirectional(Light? light)
    {
        _directional = light != null && light.ResolvedCookie != null ? light : null;
        DirectionalEnabled = _directional != null;
    }

    public void PackLocal(Light light, LightBVH bvh)
    {
        Texture2D? cookie = light.ResolvedCookie;
        if (cookie == null)
        {
            bvh.SetCookie(light, false, Float4.Zero, 0f);
            return;
        }

        int tile = TileSize(cookie);
        Int2? slot = LightCookieAtlas.ReserveTiles(tile, tile);
        if (slot == null)
        {
            bvh.SetCookie(light, false, Float4.Zero, 0f);
            return;
        }

        int atlas = LightCookieAtlas.GetSize();
        float inv = atlas > 0 ? 1f / atlas : 0f;
        var uv = new Float4(slot.Value.X * inv, slot.Value.Y * inv, tile * inv, tile * inv);
        bvh.SetCookie(light, true, uv, Maths.Clamp(light.CookieIntensity, 0f, 1f));
        _blitTiles.Add((cookie, slot.Value.X, slot.Value.Y, tile, tile));
        PackedLocalCount++;
    }

    public void BlitIfNeeded()
    {
        if (!_packedLocals || _blitTiles.Count == 0)
            return;

        RenderTexture? atlas = LightCookieAtlas.GetAtlas();
        if (atlas == null)
            return;

        Material blit = RenderPipeline.GetBlitMaterial();
        using var cmd = Graphics.GetCommandBuffer("LightCookieAtlas");
        cmd.SetRenderTarget(atlas.frameBuffer);
        if (_needsAtlasClear)
        {
            cmd.ClearRenderTarget(ClearFlags.Color, Color.White);
            RenderStats.RecordCookieAtlasClear(LightCookieAtlas.GetSize());
            _needsAtlasClear = false;
        }

        for (int i = 0; i < _blitTiles.Count; i++)
        {
            (Texture2D texture, int x, int y, int w, int h) = _blitTiles[i];
            cmd.SetViewport(x, y, (uint)w, (uint)h);
            blit.SetTexture("_MainTex", texture);
            blit.SetFloat("_FlipY", 0f);
            cmd.Blit(blit);
        }

        Graphics.Submit(cmd);
    }

    public void BindGlobals(CommandBuffer cmd)
    {
        EnsureEmpty();
        Texture2D dummy = _empty!;

        if (_directional != null && _directional.ResolvedCookie != null)
        {
            Float3 up = _directional.Transform.Up;
            Float4x4 worldToCookie = LightCookieMath.DirectionalWorldToCookie(
                _directional.Transform.Position,
                _directional.GetLightDirection(),
                up,
                _directional.CookieSize);
            cmd.SetGlobalTexture("_LightCookieMain", _directional.ResolvedCookie);
            cmd.SetGlobalInt("_LightCookieMainEnabled", 1);
            cmd.SetGlobalFloat("_LightCookieMainIntensity", Maths.Clamp(_directional.CookieIntensity, 0f, 1f));
            cmd.SetGlobalMatrix("_LightCookieMainWorldToLight", worldToCookie);
        }
        else
        {
            cmd.SetGlobalTexture("_LightCookieMain", dummy);
            cmd.SetGlobalInt("_LightCookieMainEnabled", 0);
            cmd.SetGlobalFloat("_LightCookieMainIntensity", 0f);
            cmd.SetGlobalMatrix("_LightCookieMainWorldToLight", Float4x4.Identity);
        }

        Texture2D? atlas = LightCookieAtlas.GetTexture();
        cmd.SetGlobalTexture("_LightCookieAtlas", atlas ?? dummy);
        cmd.SetGlobalVector("_LightCookieAtlasSize", atlas != null
            ? new Float2(LightCookieAtlas.GetSize(), LightCookieAtlas.GetSize())
            : Float2.Zero);
    }

    public void UploadDefaults()
    {
        using var cmd = Graphics.GetCommandBuffer("LightCookieDefaults");
        BindGlobals(cmd);
        Graphics.Submit(cmd);
    }

    public void ResetForTests()
    {
        _blitTiles.Clear();
        _directional = null;
        PackedLocalCount = 0;
        DirectionalEnabled = false;
        _packedLocals = false;
        _empty?.Dispose();
        _empty = null;
        _emptyDevice = null;
        LightCookieAtlas.Dispose();
    }

    private void EnsureEmpty()
    {
        if (_empty != null && !_empty.IsDisposed && ReferenceEquals(_emptyDevice, Graphics.Device))
            return;

        _empty?.Dispose();
        _empty = new Texture2D(4, 4, false, TextureImageFormat.Color4b);
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = 255;
        _empty.SetData<byte>(pixels);
        _emptyDevice = Graphics.Device;
    }

    private static int TileSize(Texture2D cookie)
    {
        int edge = (int)Maths.Max(cookie.Width, cookie.Height);
        return Maths.Clamp(edge, 16, 256);
    }
}
