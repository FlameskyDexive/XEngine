// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;

using Silk.NET.Vulkan;

using XEngine.Runtime.Backends.Vulkan;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class VulkanDeviceFaultSummaryTests
{
    [Fact]
    public void DeviceFaultLayouts_MatchFinalExtensionSpec()
    {
        // https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultCountsEXT.html
        // https://docs.vulkan.org/refpages/latest/refpages/source/VkDeviceFaultInfoEXT.html
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.SizeOf<VulkanGraphicsDevice.DeviceFaultCounts>());
        Assert.Equal(296, System.Runtime.InteropServices.Marshal.SizeOf<VulkanGraphicsDevice.DeviceFaultInfo>());
        Assert.Equal(System.Runtime.InteropServices.Marshal.SizeOf<DeviceFaultCountsEXT>(),
            System.Runtime.InteropServices.Marshal.SizeOf<VulkanGraphicsDevice.DeviceFaultCounts>());
        Assert.Equal(System.Runtime.InteropServices.Marshal.SizeOf<DeviceFaultInfoEXT>(),
            System.Runtime.InteropServices.Marshal.SizeOf<VulkanGraphicsDevice.DeviceFaultInfo>());
        Assert.Equal(20, (int)System.Runtime.InteropServices.Marshal.OffsetOf<VulkanGraphicsDevice.DeviceFaultCounts>("VendorInfoCount"));
        Assert.Equal(24, (int)System.Runtime.InteropServices.Marshal.OffsetOf<VulkanGraphicsDevice.DeviceFaultCounts>("VendorBinarySize"));
        Assert.Equal(272, (int)System.Runtime.InteropServices.Marshal.OffsetOf<VulkanGraphicsDevice.DeviceFaultInfo>("PAddressInfos"));
        Assert.Equal(280, (int)System.Runtime.InteropServices.Marshal.OffsetOf<VulkanGraphicsDevice.DeviceFaultInfo>("PVendorInfos"));
        Assert.Equal(288, (int)System.Runtime.InteropServices.Marshal.OffsetOf<VulkanGraphicsDevice.DeviceFaultInfo>("PVendorBinaryData"));
    }

    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(1u, 12u, 1u)]
    [InlineData(uint.MaxValue, uint.MaxValue, 64u)]
    public void DeviceFaultCapacities_NeverExceedAllocatedBuffers(uint addresses, uint vendors, uint expectedAddresses)
    {
        var counts = new VulkanGraphicsDevice.DeviceFaultCounts
        {
            AddressInfoCount = addresses, VendorInfoCount = vendors, VendorBinarySize = ulong.MaxValue,
        };
        VulkanGraphicsDevice.ClampDeviceFaultCapacities(ref counts);
        Assert.Equal(expectedAddresses, counts.AddressInfoCount);
        Assert.Equal(Math.Min(vendors, 64u), counts.VendorInfoCount);
        Assert.Equal(0ul, counts.VendorBinarySize);
    }

    [Fact]
    public void DecodeAnsi_StopsAtFirstNul()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("Page fault while executing command buffer\0rest is padding");
        Assert.Equal("Page fault while executing command buffer", VulkanGraphicsDevice.DecodeAnsi(bytes));
    }

    [Fact]
    public void DecodeAnsi_NoNul_DecodesWholeBuffer()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("abc");
        Assert.Equal("abc", VulkanGraphicsDevice.DecodeAnsi(bytes));
    }

    [Fact]
    public void DecodeAnsi_EmptyBuffer_YieldsEmpty()
    {
        Assert.Equal(string.Empty, VulkanGraphicsDevice.DecodeAnsi(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Summarize_EmptyInputs_ReportsPlaceholders()
    {
        string[] lines = VulkanGraphicsDevice.SummarizeDeviceFault(string.Empty, null, null, null, ReadOnlySpan<byte>.Empty);

        Assert.Contains("device fault description: <none provided by driver>", lines);
        Assert.Contains("device fault addresses: <none reported>", lines);
        Assert.Contains("device fault vendor info: <none reported>", lines);
        Assert.Contains("device fault vendor binary: <none>", lines);
    }

    [Fact]
    public void Summarize_FullInputs_FormatsAddressVendorAndBlobHead()
    {
        var addresses = new[]
        {
            new DeviceFaultAddressInfoEXT
            {
                AddressType = DeviceFaultAddressTypeEXT.DeviceFaultAddressTypeInstructionPointerInvalidExt,
                ReportedAddress = 0x00007FFABCDEF000UL,
                AddressPrecision = 0x10UL,
            },
        };
        var vendors = new[]
        {
            new DeviceFaultVendorInfoEXT { VendorFaultCode = 0x1234UL, VendorFaultData = 0xABCDUL },
        };
        byte[] blob = new byte[100];
        for (int i = 0; i < blob.Length; i++)
            blob[i] = (byte)i;

        string[] lines = VulkanGraphicsDevice.SummarizeDeviceFault(
            "A GPU page fault occurred", addresses, vendors, new[] { "NVIDIA: opcode fault" }, blob);

        Assert.Contains("device fault description: A GPU page fault occurred", lines);
        Assert.Contains(
            "device fault address[0]: type=DeviceFaultAddressTypeInstructionPointerInvalidExt address=0x00007FFABCDEF000 precision=0x10",
            lines);
        Assert.Contains("device fault vendor[0]: code=0x0000000000001234 data=0x000000000000ABCD text=\"NVIDIA: opcode fault\"", lines);
        string blobLine = Assert.Single(lines, l => l.StartsWith("device fault vendor binary: ", StringComparison.Ordinal));
        Assert.Contains("100 byte(s)", blobLine);
        Assert.EndsWith(Convert.ToHexString(blob.AsSpan(0, 64)), blobLine); // head = first 64 bytes only
        Assert.DoesNotContain(Convert.ToHexString(blob.AsSpan(64, 4)), blobLine);
    }

    [Fact]
    public void Summarize_MissingVendorText_OmitsTextField()
    {
        var vendors = new[] { new DeviceFaultVendorInfoEXT { VendorFaultCode = 1UL } };

        string[] lines = VulkanGraphicsDevice.SummarizeDeviceFault("d", null, vendors, null, ReadOnlySpan<byte>.Empty);

        Assert.Contains("device fault vendor[0]: code=0x0000000000000001 data=0x0000000000000000", lines);
    }
}
