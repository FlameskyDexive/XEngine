using System;

namespace XEngine.GpuAnimation;

/// <summary>
/// Shared CPU-side math for vertex animation textures. Mirrors the shader-side sampling
/// (frame row → V coordinate, per-instance phase/speed, in-clip loop wrap) so tests can
/// assert exact parity with what the GPU will reconstruct.
/// </summary>
public static class GpuAnimMath
{
    /// <summary>Smallest power of two >= <paramref name="value"/> (>= 1).</summary>
    public static int NextPowerOfTwo(int value)
    {
        int p = 1;
        while (p < value) p <<= 1;
        return p;
    }

    public static float Fract(float v) => v - MathF.Floor(v);

    /// <summary>
    /// U coordinate (texture column) of a vertex texel — texel center, so columns never
    /// bleed into each other or into the power-of-two padding columns.
    /// </summary>
    public static float VertexU(int vertexIndex, int textureWidth) => (vertexIndex + 0.5f) / textureWidth;

    /// <summary>V coordinate (texture row) of a frame texel center.</summary>
    public static float RowV(int rowIndex, int textureHeight) => (rowIndex + 0.5f) / textureHeight;

    /// <summary>
    /// Continuous (fractional) texture row for playing a clip: wraps inside the clip's own
    /// atlas segment, so the loop cross-fades its own last and first frames.
    /// </summary>
    /// <param name="clip">Clip atlas layout.</param>
    /// <param name="time">Global playback time in seconds.</param>
    /// <param name="phase">Per-instance phase offset in [0,1) of the clip.</param>
    /// <param name="speed">Per-instance playback speed multiplier.</param>
    public static float ContinuousRow(GpuAnimationClipInfo clip, float time, float phase = 0f, float speed = 1f)
    {
        float t = clip.AnimLength > 0f ? time / clip.AnimLength : 0f;
        float f = Fract(t * speed + phase);
        return clip.FrameStart + f * clip.FrameCount;
    }

    /// <summary>
    /// Splits a continuous row into the two rows the shader must sample plus the blend
    /// factor between them (shader-side manual lerp — deterministic, format-safe).
    /// </summary>
    public static void SampleRows(float continuousRow, GpuAnimationClipInfo clip, out int row0, out int row1, out float blend)
    {
        float local = continuousRow - clip.FrameStart;
        if (local < 0f) local = 0f;
        if (local > clip.FrameCount) local = clip.FrameCount;
        int i0 = (int)local;
        if (i0 >= clip.FrameCount) i0 = clip.FrameCount - 1;
        blend = local - i0;
        row0 = clip.FrameStart + i0;
        row1 = clip.FrameStart + (i0 + 1) % clip.FrameCount;
    }
}
