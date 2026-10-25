// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG3: ShaderDescriptorLayoutPlan bind-group planning and wgpu BindGroupLayout creation.
/// Reflected <c>@group</c>/<c>@binding</c> must match the compiled WGSL declarations.
/// </summary>
public class WebGPUBindGroupLayoutTests
{
    private const string VertexSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
        "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return Color; }";
    private const string FragmentSource = "cbuffer Globals : register(b2) { float4 Color; }; " +
        "Texture2D MainTexture : register(t3); SamplerState MainSampler : register(s4); " +
        "float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return Color + MainTexture.Sample(MainSampler, uv); }";

    private static readonly Regex WgslResourceBinding = new(
        @"(?:@group\((?<g1>\d+)\)\s*@binding\((?<b1>\d+)\)|@binding\((?<b2>\d+)\)\s*@group\((?<g2>\d+)\))\s+var(?:<[^>\n]+>)?\s+(?<name>\w+)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    [Fact]
    public void ReflectionPhysicalBindings_MatchCompiledWgslDeclarations()
    {
        ShaderCompileResult result = CompileWebGpu();
        Assert.True(result.Success, result.ErrorMessage);
        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);

        string vertexWgsl = Encoding.UTF8.GetString(result.VertexBytecode);
        string fragmentWgsl = Encoding.UTF8.GetString(result.FragmentBytecode);
        Console.WriteLine("=== WG3 vertex WGSL ===");
        Console.WriteLine(vertexWgsl);
        Console.WriteLine("=== WG3 fragment WGSL ===");
        Console.WriteLine(fragmentWgsl);

        HashSet<(int Group, int Binding)> declared = CollectDeclaredBindings(vertexWgsl, fragmentWgsl);
        HashSet<(int Group, int Binding)> reflected = CollectReflectedBindings(layout);
        Assert.True(declared.Count > 0, "Compiled WGSL contained no @group/@binding resource declarations.");
        Assert.Equal(declared, reflected);

        AssertResourceDeclared(vertexWgsl + fragmentWgsl, "Globals");
        AssertResourceDeclared(fragmentWgsl, "MainTexture");
        AssertResourceDeclared(fragmentWgsl, "MainSampler");

        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(layout);
        Assert.NotEmpty(plan.BindGroups);
        foreach (ShaderBindGroupPlan group in plan.BindGroups)
        {
            foreach (ShaderDescriptorBinding binding in group.Bindings)
            {
                Assert.Equal(group.Group, binding.PhysicalGroup);
                Assert.Contains((binding.PhysicalGroup, binding.PhysicalBinding), declared);
            }
        }
    }

    [Fact]
    public void BindGroupLayoutEntries_MatchResourceKinds()
    {
        ShaderCompileResult result = CompileWebGpu();
        Assert.True(result.Success, result.ErrorMessage);
        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(
            Assert.IsType<ShaderBindingLayout>(result.BindingLayout));

        ShaderDescriptorBinding globals = FindBinding(plan, "Globals");
        Assert.Equal(WGPUBufferBindingType.Uniform, WebGPUBindGroupLayouts.ToLayoutEntry(globals).buffer.type);
        Assert.True((WebGPUBindGroupLayouts.ToLayoutEntry(globals).visibility & WGPUShaderStage.Vertex) != 0);

        ShaderDescriptorBinding texture = FindBinding(plan, "MainTexture");
        WGPUBindGroupLayoutEntry textureEntry = WebGPUBindGroupLayouts.ToLayoutEntry(texture);
        // Unfilterable-float accepts both filterable and non-filterable float views:
        // Load()-only data textures (morph deltas, bone matrices) are RGBA32F while material
        // textures are filterable, and one per-shader layout must bind both.
        Assert.Equal(WGPUTextureSampleType.UnfilterableFloat, textureEntry.texture.sampleType);
        Assert.Equal(WGPUTextureViewDimension.D2, textureEntry.texture.viewDimension);
        Assert.Equal(WGPUShaderStage.Fragment, textureEntry.visibility);

        ShaderDescriptorBinding sampler = FindBinding(plan, "MainSampler");
        WGPUBindGroupLayoutEntry samplerEntry = WebGPUBindGroupLayouts.ToLayoutEntry(sampler);
        Assert.Equal(WGPUSamplerBindingType.Filtering, samplerEntry.sampler.type);
        Assert.Equal(WGPUShaderStage.Fragment, samplerEntry.visibility);
    }

    [Fact]
    public void Device_CreatesNativeBindGroupLayouts_Or_Skip()
    {
        ShaderCompileResult result = CompileWebGpu();
        Assert.True(result.Success, result.ErrorMessage);
        ShaderDescriptorLayoutPlan plan = ShaderDescriptorLayoutPlan.Create(
            Assert.IsType<ShaderBindingLayout>(result.BindingLayout));

        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            nint[] layouts = WebGPUBindGroupLayouts.CreateNativeLayouts(device.NativeDevice, plan);
            try
            {
                Assert.Equal(plan.BindGroups.Length, layouts.Length);
                Assert.All(layouts, layout => Assert.NotEqual(nint.Zero, layout));
                Console.WriteLine($"WG3 native bind-group layouts={layouts.Length}");
            }
            finally
            {
                WebGPUBindGroupLayouts.Release(layouts);
            }
        }
    }

    private static ShaderCompileResult CompileWebGpu() =>
        new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = VertexSource,
            FragmentSource = FragmentSource,
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });

    private static HashSet<(int Group, int Binding)> CollectDeclaredBindings(params string[] wgslSources)
    {
        var declared = new HashSet<(int Group, int Binding)>();
        for (int i = 0; i < wgslSources.Length; i++)
        {
            foreach (Match match in WgslResourceBinding.Matches(wgslSources[i]))
                declared.Add(ReadGroupBinding(match));
        }

        return declared;
    }

    private static HashSet<(int Group, int Binding)> CollectReflectedBindings(ShaderBindingLayout layout)
    {
        var reflected = new HashSet<(int Group, int Binding)>();
        AddPhysical(reflected, layout.Buffers);
        AddPhysical(reflected, layout.Textures);
        AddPhysical(reflected, layout.Samplers);
        return reflected;
    }

    private static void AddPhysical(HashSet<(int Group, int Binding)> destination, ShaderBindingSlot[] slots)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderPhysicalBinding[] physicalBindings = slots[i].PhysicalBindings;
            Assert.True(physicalBindings.Length > 0, $"'{slots[i].Name}' has no physical @group/@binding.");
            for (int physicalIndex = 0; physicalIndex < physicalBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = physicalBindings[physicalIndex];
                destination.Add((physical.Space, physical.Binding));
            }
        }
    }

    private static (int Group, int Binding) ReadGroupBinding(Match match)
    {
        if (match.Groups["g1"].Success)
            return (int.Parse(match.Groups["g1"].Value), int.Parse(match.Groups["b1"].Value));
        return (int.Parse(match.Groups["g2"].Value), int.Parse(match.Groups["b2"].Value));
    }

    private static void AssertResourceDeclared(string wgsl, string resourceName)
    {
        Assert.Contains(resourceName, wgsl, StringComparison.Ordinal);
        bool found = false;
        foreach (Match match in WgslResourceBinding.Matches(wgsl))
        {
            if (match.Groups["name"].Value.Contains(resourceName, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        Assert.True(found, $"WGSL has no @group/@binding declaration whose name contains '{resourceName}'.");
    }

    private static ShaderDescriptorBinding FindBinding(ShaderDescriptorLayoutPlan plan, string name)
    {
        for (int i = 0; i < plan.Bindings.Length; i++)
        {
            if (plan.Bindings[i].Slot.Name == name)
                return plan.Bindings[i];
        }

        throw new InvalidOperationException($"Plan has no binding named '{name}'.");
    }

    private static bool TryCreateDevice(out WebGPUGraphicsDevice device)
    {
        try
        {
            device = new WebGPUGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGPU });
            return true;
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGPU create failure: {ex.GetType().FullName}: {ex.Message}");
            device = null!;
            return false;
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException && IsExplicitGpuAvailabilityMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsExplicitGpuAvailabilityMessage(string message) =>
        message.StartsWith("Failed to create WebGPU device. wgpu-native is unavailable", StringComparison.Ordinal)
        || message.StartsWith("wgpuCreateInstance returned null", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU adapter:", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal)
        || message.Equals("wgpuDeviceGetQueue returned null.", StringComparison.Ordinal);
}
