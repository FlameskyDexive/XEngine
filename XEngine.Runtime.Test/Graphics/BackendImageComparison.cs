// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Test.Rhi;

internal readonly record struct RgbaImage(int Width, int Height, byte[] Pixels)
{
    public void Validate(string parameterName)
    {
        if (Width <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Image width must be positive.");
        if (Height <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "Image height must be positive.");
        if (Pixels == null)
            throw new ArgumentNullException(parameterName, "Image pixels cannot be null.");

        int expectedLength = checked(Width * Height * 4);
        if (Pixels.Length != expectedLength)
            throw new ArgumentException($"Expected {expectedLength} RGBA8 bytes, received {Pixels.Length}.", parameterName);
    }
}

internal readonly record struct ImageComparisonOptions(byte MaxChannelDelta, double MaxMismatchRatio)
{
    public static ImageComparisonOptions Exact => new(0, 0d);

    public void Validate()
    {
        if (double.IsNaN(MaxMismatchRatio) || MaxMismatchRatio < 0d || MaxMismatchRatio > 1d)
            throw new ArgumentOutOfRangeException(nameof(MaxMismatchRatio), "Mismatch ratio must be between zero and one.");
    }
}

internal sealed class ImageComparisonResult
{
    public required bool Passed { get; init; }
    public required int PixelCount { get; init; }
    public required int MismatchedPixels { get; init; }
    public required byte MaxChannelDelta { get; init; }
    public required double MeanAbsoluteChannelDelta { get; init; }
    public required byte[] DifferenceRgba { get; init; }

    public double MismatchRatio => PixelCount == 0 ? 0d : (double)MismatchedPixels / PixelCount;

    public override string ToString() =>
        $"pixels={PixelCount}, mismatched={MismatchedPixels} ({MismatchRatio:P4}), " +
        $"max-channel-delta={MaxChannelDelta}, mean-absolute-channel-delta={MeanAbsoluteChannelDelta:F4}";
}

internal static class BackendImageComparison
{
    public static ImageComparisonResult Compare(
        in RgbaImage reference,
        in RgbaImage candidate,
        in ImageComparisonOptions options)
    {
        reference.Validate(nameof(reference));
        candidate.Validate(nameof(candidate));
        options.Validate();

        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            throw new ArgumentException(
                $"Image dimensions differ: reference is {reference.Width}x{reference.Height}, " +
                $"candidate is {candidate.Width}x{candidate.Height}.",
                nameof(candidate));
        }

        int pixelCount = reference.Width * reference.Height;
        byte[] difference = new byte[reference.Pixels.Length];
        int mismatchedPixels = 0;
        byte maxChannelDelta = 0;
        long absoluteDeltaSum = 0;

        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            byte pixelMaxDelta = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int delta = Math.Abs(reference.Pixels[offset + channel] - candidate.Pixels[offset + channel]);
                absoluteDeltaSum += delta;
                if (delta > pixelMaxDelta)
                    pixelMaxDelta = (byte)delta;
                if (delta > maxChannelDelta)
                    maxChannelDelta = (byte)delta;
            }

            if (pixelMaxDelta > options.MaxChannelDelta)
                mismatchedPixels++;

            byte heat = pixelMaxDelta == 0 ? (byte)0 : (byte)Math.Min(255, pixelMaxDelta * 8);
            difference[offset] = heat;
            difference[offset + 1] = pixelMaxDelta <= options.MaxChannelDelta ? heat : (byte)0;
            difference[offset + 2] = 0;
            difference[offset + 3] = 255;
        }

        double mismatchRatio = pixelCount == 0 ? 0d : (double)mismatchedPixels / pixelCount;
        return new ImageComparisonResult
        {
            Passed = mismatchRatio <= options.MaxMismatchRatio,
            PixelCount = pixelCount,
            MismatchedPixels = mismatchedPixels,
            MaxChannelDelta = maxChannelDelta,
            MeanAbsoluteChannelDelta = (double)absoluteDeltaSum / (pixelCount * 4),
            DifferenceRgba = difference,
        };
    }
}
