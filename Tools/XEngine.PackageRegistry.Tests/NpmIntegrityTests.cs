// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.PackageRegistry;

namespace XEngine.PackageRegistry.Tests;

public class NpmIntegrityTests
{
    private static readonly byte[] Payload =
        System.Text.Encoding.UTF8.GetBytes("xengine npm integrity payload");

    [Fact]
    public void ComputeThenVerify_Matches()
    {
        string sri = NpmIntegrity.Compute(Payload);
        Assert.StartsWith("sha512-", sri);
        Assert.True(NpmIntegrity.TryParse(sri, out NpmIntegrity integrity));
        Assert.True(integrity.Verify(Payload));
        using var stream = new MemoryStream(Payload);
        Assert.True(integrity.Verify(stream));
    }

    [Fact]
    public void Verify_DetectsTampering()
    {
        NpmIntegrity integrity = NpmIntegrity.Parse(NpmIntegrity.Compute(Payload));
        byte[] tampered = (byte[])Payload.Clone();
        tampered[0] ^= 0xFF;
        Assert.False(integrity.Verify(tampered));
    }

    [Fact]
    public void TryParse_RejectsMalformedStrings()
    {
        Assert.False(NpmIntegrity.TryParse(null, out _));
        Assert.False(NpmIntegrity.TryParse("", out _));
        Assert.False(NpmIntegrity.TryParse("sha512", out _));
        Assert.False(NpmIntegrity.TryParse("sha512-", out _));
        Assert.False(NpmIntegrity.TryParse("md5-AAAA", out _)); // unsupported algorithm
        Assert.False(NpmIntegrity.TryParse("sha512-not base64!!", out _));
        Assert.False(NpmIntegrity.TryParse("sha512-YQ==", out _)); // valid base64, wrong length
    }

    [Fact]
    public void Sha1_SupportedForLegacyPackages()
    {
        string sri = NpmIntegrity.Compute(Payload, "sha1");
        Assert.StartsWith("sha1-", sri);
        Assert.True(NpmIntegrity.Parse(sri).Verify(Payload));
    }

    [Fact]
    public void Shasum_IsLowercaseHexSha1()
    {
        string shasum = NpmIntegrity.ComputeShasum(Payload);
        Assert.Equal(40, shasum.Length);
        Assert.All(shasum, c => Assert.True(c is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
