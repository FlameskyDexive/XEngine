using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.VFX;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test.VFX;

public sealed class VfxGraphAssetTests
{
    [Fact]
    public void Catalog_ListsContextsBlocksAndOperators()
    {
        Assert.True(VfxNodeLibrary.IsContext(VfxNodeLibrary.Spawn));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Gravity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.CollideWithPlane));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.CollideWithSDF));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Turbulence));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.TriggerEvent));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.GPUEventTarget));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.VectorField));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.SpawnOverDistance));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.PeriodicBurst));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.FlipbookMotion));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.InheritVelocity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.TriggerEventRate));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.TriggerEventOverDistance));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.TriggerEventOnce));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.CollisionFriction));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.CollisionElasticity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.CollisionLifetimeLoss));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.LimitVelocity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.KillSphere));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.KillAABox));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Attract));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.ConformToSphere));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Vortex));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.ConformToCylinder));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.OrientLookAt));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.FlipbookRate));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.KillPlane));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.FlipbookStart));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.SizeOverSpeed));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.MultiplySize));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.MultiplyColor));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddSize));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddColor));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddVelocity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.MultiplyVelocity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.OverwriteSize));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.OverwriteColor));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.OverwriteVelocity));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.OverwriteLifetime));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddLifetime));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.MultiplyLifetime));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddAge));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.MultiplyAge));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.SetAge));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AddPosition));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.SetPosition));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.ColorOverSpeed));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.ScreenSpaceSize));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.AttributeMap));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Distort));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.ParticleLight));
        Assert.True(VfxNodeLibrary.IsOperator(VfxNodeLibrary.Multiply));
        Assert.False(VfxNodeLibrary.IsKnown("NotANode"));

        var inputs = new List<string>();
        var outputs = new List<string>();
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Initialize, inputs, outputs);
        Assert.Equal(new[] { VfxNodeLibrary.FlowPort }, inputs);
        Assert.Equal(new[] { VfxNodeLibrary.FlowPort }, outputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ColorOverLife, inputs, outputs);
        Assert.Equal(new[] { "From", "To" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.SizeOverLife, inputs, outputs);
        Assert.Equal(new[] { "From", "To" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CollideWithPlane, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Bounce", "Nx", "Ny", "Nz", "Distance" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CollideWithSDF, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Shape", "Cx", "Cy", "Cz", "Sx", "Sy", "Sz" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Turbulence, inputs, outputs);
        Assert.Equal(new[] { "Intensity", "Frequency" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.TriggerEvent, inputs, outputs);
        Assert.Equal(new[] { "Kind", "Count" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.GPUEventTarget, inputs, outputs);
        Assert.Equal(new[] { "Name" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.VectorField, inputs, outputs);
        Assert.Equal(new[] { "Intensity" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.SpawnOverDistance, inputs, outputs);
        Assert.Equal(new[] { "Rate" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.PeriodicBurst, inputs, outputs);
        Assert.Equal(new[] { "Count", "Period" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AttributeMap, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Scale" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Distort, inputs, outputs);
        Assert.Equal(new[] { "Intensity" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ParticleLight, inputs, outputs);
        Assert.Equal(new[] { "Intensity", "Range", "Count" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Sort, inputs, outputs);
        Assert.Equal(new[] { "Mode" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.FlipbookMotion, inputs, outputs);
        Assert.Equal(new[] { "Scale" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MotionVector, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Scale" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OrientAlongVelocity, inputs, outputs);
        Assert.Equal(new[] { "Mode" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OrientFixedAxis, inputs, outputs);
        Assert.Equal(new[] { "Mode", "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OrientLookAt, inputs, outputs);
        Assert.Equal(new[] { "Mode", "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.FlipbookRate, inputs, outputs);
        Assert.Equal(new[] { "Rate" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.KillPlane, inputs, outputs);
        Assert.Equal(new[] { "Nx", "Ny", "Nz", "Distance" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.FlipbookStart, inputs, outputs);
        Assert.Equal(new[] { "Frame" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.PositionShape, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Sx", "Sy", "Sz" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Sequential, inputs, outputs);
        Assert.Equal(new[] { "Mode", "Sx", "Sy", "Sz" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.InheritVelocity, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.TriggerEventRate, inputs, outputs);
        Assert.Equal(new[] { "Rate" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.TriggerEventOverDistance, inputs, outputs);
        Assert.Equal(new[] { "Rate" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.TriggerEventOnce, inputs, outputs);
        Assert.Equal(new[] { "Count" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.SizeOverSpeed, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MultiplySize, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MultiplyColor, inputs, outputs);
        Assert.Equal(new[] { "R", "G", "B", "A" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddSize, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddColor, inputs, outputs);
        Assert.Equal(new[] { "R", "G", "B", "A" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddVelocity, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MultiplyVelocity, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OverwriteSize, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OverwriteColor, inputs, outputs);
        Assert.Equal(new[] { "R", "G", "B", "A" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OverwriteVelocity, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OverwriteLifetime, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddLifetime, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MultiplyLifetime, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddAge, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.MultiplyAge, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.SetAge, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AddPosition, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.SetPosition, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ColorOverSpeed, inputs, outputs);
        Assert.Equal(new[] { "From", "To", "Scale" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ScreenSpaceSize, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CollisionFriction, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CollisionElasticity, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CollisionLifetimeLoss, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.LimitVelocity, inputs, outputs);
        Assert.Equal(new[] { "Speed" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.KillSphere, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Radius" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.KillAABox, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Sx", "Sy", "Sz" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Attract, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Strength" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ConformToSphere, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Radius", "Strength" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Vortex, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Ax", "Ay", "Az", "Rotation", "Attraction" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.ConformToCylinder, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z", "Ax", "Ay", "Az", "Radius", "Strength" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.LinearDrag, inputs, outputs);
        Assert.Equal(new[] { "Amount" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Force, inputs, outputs);
        Assert.Equal(new[] { "X", "Y", "Z" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.AngularVelocity, inputs, outputs);
        Assert.Equal(new[] { "Rate" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.CustomHLSL, inputs, outputs);
        Assert.Equal(new[] { "Code" }, inputs);
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Add, inputs, outputs);
        Assert.Equal(new[] { "A", "B" }, inputs);
        Assert.Equal(new[] { "Out" }, outputs);
        Assert.True(VfxNodeLibrary.IsOutputContext(VfxNodeLibrary.OutputParticleMesh));
        Assert.True(VfxNodeLibrary.IsOutputContext(VfxNodeLibrary.OutputParticleStrip));
        Assert.True(VfxNodeLibrary.IsOutputContext(VfxNodeLibrary.OutputParticleDecal));
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.OutputParticleMesh, inputs, outputs);
        Assert.Equal(new[] { VfxNodeLibrary.FlowPort }, inputs);
        Assert.True(VfxNodeLibrary.IsOperator(VfxNodeLibrary.Subgraph));
        Assert.True(VfxNodeLibrary.IsBlock(VfxNodeLibrary.Subgraph));
        VfxNodeLibrary.GetPorts(VfxNodeLibrary.Subgraph, inputs, outputs);
        Assert.Equal(new[] { VfxExposedNames.SubgraphPath }, inputs);
    }

    [Fact]
    public void Templates_HaveRequiredContextsAndExpectedBlocks()
    {
        VfxGraphAsset empty = VfxGraphTemplates.CreateEmpty();
        Assert.Equal(4, empty.Contexts.Count);
        Assert.Empty(empty.Blocks);
        Assert.Contains(empty.Contexts, c => c.Type == VfxNodeLibrary.Spawn);
        Assert.Contains(empty.Contexts, c => c.Type == VfxNodeLibrary.Initialize);
        Assert.Contains(empty.Contexts, c => c.Type == VfxNodeLibrary.Update);
        Assert.Contains(empty.Contexts, c => c.Type == VfxNodeLibrary.OutputParticleQuad);

        VfxGraphAsset loop = VfxGraphTemplates.CreateLoop();
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ConstantSpawnRate);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Gravity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollideWithPlane);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollideWithSDF);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Turbulence);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.TriggerEvent);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.GPUEventTarget);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollideWithDepth);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ConformToSDF);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.VectorField);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.SpawnOverDistance);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.PeriodicBurst);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AttributeMap);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Distort);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ParticleLight);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Sort);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CameraFade);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.FlipbookBlend);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.FlipbookMotion);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MotionVector);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OrientAlongVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OrientFixedAxis);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OrientLookAt);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.FlipbookRate);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.FlipbookStart);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.PositionShape);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Sequential);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.InheritVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventRate);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOverDistance);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOnce);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollisionFriction);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollisionElasticity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CollisionLifetimeLoss);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.LimitVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.KillSphere);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.KillAABox);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Attract);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ConformToSphere);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Vortex);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ConformToCylinder);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.KillPlane);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.SizeOverSpeed);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MultiplySize);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MultiplyColor);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddSize);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddColor);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MultiplyVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OverwriteSize);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OverwriteColor);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OverwriteVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.OverwriteLifetime);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddLifetime);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MultiplyLifetime);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddAge);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.MultiplyAge);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.SetAge);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AddPosition);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.SetPosition);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ColorOverSpeed);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ScreenSpaceSize);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.ColorOverLife);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.SizeOverLife);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.LinearDrag);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.Force);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.AngularVelocity);
        Assert.Contains(loop.Blocks, b => b.Type == VfxNodeLibrary.CustomHLSL);
        Assert.NotNull(loop.FindProperty(VfxExposedNames.CollisionMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SdfCollisionMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.TurbulenceIntensity));
        Assert.Contains(loop.Operators, o => o.Type == VfxNodeLibrary.Color);
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SpawnRate));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.MainTexture));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.FlipbookColumns));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.LitMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SoftParticleFade));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SdfVolume));
        Assert.Equal(VfxValueType.Texture3D, loop.FindProperty(VfxExposedNames.SdfVolume)!.Type);
        Assert.NotNull(loop.FindProperty(VfxExposedNames.EventKind));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.EventCount));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.DepthCollisionMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.ConformAttraction));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.VectorFieldIntensity));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.VectorField));
        Assert.Equal(VfxValueType.Texture3D, loop.FindProperty(VfxExposedNames.VectorField)!.Type);
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SpawnOverDistance));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.AttributeMapMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.AttributeMapScale));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.AttributeMap));
        Assert.Equal(VfxValueType.Texture, loop.FindProperty(VfxExposedNames.AttributeMap)!.Type);
        Assert.NotNull(loop.FindProperty(VfxExposedNames.DistortionIntensity));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.ParticleLightIntensity));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.ParticleLightRange));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.ParticleLightCount));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.SortMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.CameraFadeNear));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.CameraFadeFar));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.FlipbookBlend));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.MotionVectorMode));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.MotionVectorScale));
        Assert.NotNull(loop.FindProperty(VfxExposedNames.OrientMode));

        VfxGraphAsset burst = VfxGraphTemplates.CreateBurst();
        Assert.Contains(burst.Blocks, b => b.Type == VfxNodeLibrary.Burst);
        Assert.DoesNotContain(burst.Blocks, b => b.Type == VfxNodeLibrary.ConstantSpawnRate);

        VfxGraphAsset trail = VfxGraphTemplates.CreateTrail();
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.ColorOverLife);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.SizeOverLife);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.LinearDrag);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.Force);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AngularVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.CustomHLSL);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.Sequential);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.InheritVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventRate);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOverDistance);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOnce);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.CollisionFriction);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.CollisionElasticity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.CollisionLifetimeLoss);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.LimitVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.KillSphere);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.KillAABox);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.Attract);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.ConformToSphere);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.Vortex);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.ConformToCylinder);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.KillPlane);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.SizeOverSpeed);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.MultiplySize);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.MultiplyColor);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddSize);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddColor);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.MultiplyVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OverwriteSize);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OverwriteColor);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OverwriteVelocity);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OverwriteLifetime);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddLifetime);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.MultiplyLifetime);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddAge);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.MultiplyAge);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.SetAge);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.AddPosition);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.SetPosition);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.ColorOverSpeed);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.ScreenSpaceSize);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OrientFixedAxis);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.OrientLookAt);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.FlipbookRate);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.FlipbookStart);
        Assert.Contains(trail.Blocks, b => b.Type == VfxNodeLibrary.FlipbookMotion);
        Assert.Contains(trail.Contexts, c => c.Type == VfxNodeLibrary.OutputParticleStrip);
        Assert.Equal(8192, trail.Capacity);

        VfxGraphAsset mesh = VfxGraphTemplates.CreateMesh();
        Assert.Contains(mesh.Contexts, c => c.Type == VfxNodeLibrary.OutputParticleMesh);
        Assert.DoesNotContain(mesh.Blocks, b => b.Type == VfxNodeLibrary.OrientToView);
        Assert.Contains(mesh.Blocks, b => b.Type == VfxNodeLibrary.OrientFixedAxis);
        Assert.Contains(mesh.Blocks, b => b.Type == VfxNodeLibrary.OrientLookAt);
        Assert.Contains(mesh.Blocks, b => b.Type == VfxNodeLibrary.FlipbookMotion);
        Assert.NotNull(mesh.FindProperty(VfxExposedNames.OutputMesh));

        VfxGraphAsset decal = VfxGraphTemplates.CreateDecal();
        Assert.Contains(decal.Contexts, c => c.Type == VfxNodeLibrary.OutputParticleDecal);
        Assert.DoesNotContain(decal.Blocks, b => b.Type == VfxNodeLibrary.OrientToView);
        Assert.DoesNotContain(decal.Blocks, b => b.Type == VfxNodeLibrary.OrientFixedAxis);
        Assert.DoesNotContain(decal.Blocks, b => b.Type == VfxNodeLibrary.OrientLookAt);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.PeriodicBurst);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventRate);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOverDistance);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.TriggerEventOnce);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.CollisionFriction);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.CollisionElasticity);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.CollisionLifetimeLoss);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.LimitVelocity);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.KillSphere);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.KillAABox);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.Attract);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.ConformToSphere);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.Vortex);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.ConformToCylinder);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.KillPlane);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.SizeOverSpeed);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.MultiplySize);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.MultiplyColor);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddSize);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddColor);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddVelocity);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.MultiplyVelocity);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.OverwriteSize);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.OverwriteColor);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.OverwriteVelocity);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.OverwriteLifetime);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddLifetime);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.MultiplyLifetime);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddAge);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.MultiplyAge);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.SetAge);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.AddPosition);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.SetPosition);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.ColorOverSpeed);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.ScreenSpaceSize);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.FlipbookRate);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.FlipbookStart);
        Assert.Contains(decal.Blocks, b => b.Type == VfxNodeLibrary.FlipbookMotion);
        Assert.Equal(44, decal.Properties.Count);

        VfxGraphAsset subgraph = VfxGraphTemplates.CreateSubgraph();
        Assert.Contains(subgraph.Operators, o => o.Type == VfxNodeLibrary.Subgraph);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.ColorOverLife);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.SizeOverLife);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.LinearDrag);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.Force);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.AngularVelocity);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.CustomHLSL);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.Sequential);
        Assert.Contains(subgraph.Blocks, b => b.Type == VfxNodeLibrary.OrientFixedAxis);
        Assert.Equal(VfxTemplateKind.Subgraph, subgraph.Template);
    }

    [Fact]
    public void EchoRoundTrip_PreservesLoopGraph()
    {
        VfxGraphAsset original = VfxGraphTemplates.CreateLoop();
        original.Blocks[0].Order = 7;

        var echo = Serializer.Serialize(typeof(object), original);
        var deserialized = Serializer.Deserialize<VfxGraphAsset>(echo);

        Assert.NotNull(deserialized);
        Assert.Equal("VFX/Loop", deserialized!.GraphName);
        Assert.Equal(VfxTemplateKind.Loop, deserialized.Template);
        Assert.Equal(4096, deserialized.Capacity);
        Assert.Equal(4, deserialized.Contexts.Count);
        Assert.Equal(73, deserialized.Blocks.Count);
        Assert.Equal(7, deserialized.Blocks[0].Order);
        Assert.Equal(4, deserialized.Edges.Count);
        Assert.Equal("n_color", deserialized.Operators[0].Id);
        Assert.Equal(44, deserialized.Properties.Count);
        Assert.Equal(VfxExposedNames.SpawnRate, deserialized.Properties[0].Name);
    }

    [Theory]
    [InlineData("Empty.vfx", "VFX/Empty", VfxTemplateKind.Empty)]
    [InlineData("Loop.vfx", "VFX/Loop", VfxTemplateKind.Loop)]
    [InlineData("Burst.vfx", "VFX/Burst", VfxTemplateKind.Burst)]
    [InlineData("Trail.vfx", "VFX/Trail", VfxTemplateKind.Trail)]
    [InlineData("Mesh.vfx", "VFX/Mesh", VfxTemplateKind.Mesh)]
    [InlineData("Subgraph.vfx", "VFX/Subgraph", VfxTemplateKind.Subgraph)]
    [InlineData("Decal.vfx", "VFX/Decal", VfxTemplateKind.Decal)]
    public void PackagedTemplates_Deserialize(string fileName, string graphName, VfxTemplateKind kind)
    {
        string path = FindTemplate(fileName);
        var echo = EchoObject.ReadFromString(File.ReadAllText(path));
        var graph = Serializer.Deserialize<VfxGraphAsset>(echo);
        Assert.NotNull(graph);
        Assert.Equal(graphName, graph!.GraphName);
        Assert.Equal(kind, graph.Template);
        Assert.Equal(4, graph.Contexts.Count);
    }

    private static string FindTemplate(string fileName)
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "Packages", "com.xengine.vfx", "Templates", fileName);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class VfxGraphCompilerTests
{
    private readonly ITestOutputHelper _output;

    public VfxGraphCompilerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CompileSettings_LoopAndBurst_MatchTemplateKnobs()
    {
        VfxCompiledSettings loop = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(4096, loop.Capacity);
        Assert.Equal(64f, loop.SpawnRate);
        Assert.Equal(0f, loop.BurstCount);
        Assert.Equal(2f, loop.Lifetime);
        Assert.Equal(2f, loop.StartSpeed);
        Assert.Equal(0.15f, loop.StartSize);
        Assert.Equal(-1.5f, loop.Gravity.Y);
        Assert.Equal(1f, loop.StartColor.R);
        Assert.Equal(1f, loop.FlipbookColumns);
        Assert.Equal(1f, loop.FlipbookRows);
        Assert.Equal(2f, loop.CollisionMode);
        Assert.Equal(0.4f, loop.CollisionBounce, 3);
        Assert.Equal(1f, loop.CollisionPlane.Y);
        Assert.Equal(2f, loop.SdfCollisionMode);
        Assert.Equal(1f, loop.SdfShape);
        Assert.Equal(0.55f, loop.SdfCenter.Y, 3);
        Assert.Equal(0.4f, loop.SdfSize.X, 3);
        Assert.Equal(1.2f, loop.TurbulenceIntensity, 3);
        Assert.Equal(1.25f, loop.TurbulenceFrequency, 3);
        Assert.Equal(1f, loop.SoftParticleFade);
        Assert.Equal(1f, loop.EventKind);
        Assert.Equal(2f, loop.EventCount);
        Assert.Equal(0f, loop.EventTarget);
        Assert.Equal("", loop.EventTargetName);
        Assert.Equal(0f, loop.DepthCollisionMode);
        Assert.Equal(0f, loop.ConformAttraction);
        Assert.Equal(0f, loop.VectorFieldIntensity);
        Assert.Equal(0f, loop.SpawnOverDistance);
        Assert.Equal(0f, loop.PeriodicBurstCount);
        Assert.Equal(1f, loop.PeriodicBurstPeriod);
        Assert.Equal(0f, loop.AttributeMapMode);
        Assert.Equal(1f, loop.AttributeMapScale);
        Assert.Equal(0f, loop.DistortionIntensity);
        Assert.Equal(0f, loop.ParticleLightIntensity);
        Assert.Equal(1.5f, loop.ParticleLightRange);
        Assert.Equal(0f, loop.ParticleLightCount);
        Assert.Equal(0f, loop.CameraFadeNear);
        Assert.Equal(0f, loop.CameraFadeFar);
        Assert.Equal(0f, loop.FlipbookBlend);
        Assert.Equal(0f, loop.FlipbookMotionScale);
        Assert.Equal(0f, loop.MotionVectorMode);
        Assert.Equal(1f, loop.MotionVectorScale);
        Assert.Equal(0f, loop.OrientMode);
        Assert.Equal(0f, loop.FixedAxisMode);
        Assert.Equal(1f, loop.FixedAxisY);
        Assert.Equal(0f, loop.PositionShapeMode);
        Assert.Equal(0.25f, loop.PositionShapeSx, 3);
        Assert.Equal(0f, loop.SequentialMode);
        Assert.Equal(0f, loop.InheritVelocityAmount);
        Assert.Equal(0f, loop.TriggerEventRate);
        Assert.Equal(0f, loop.TriggerEventOverDistance);
        Assert.Equal(0f, loop.TriggerEventOnceCount);
        Assert.Equal(0f, loop.CollisionFrictionAmount);
        Assert.Equal(0f, loop.CollisionElasticityAmount);
        Assert.Equal(0f, loop.CollisionLifetimeLossAmount);
        Assert.Equal(0f, loop.LimitVelocitySpeed);
        Assert.Equal(0f, loop.KillSphereRadius);
        Assert.Equal(0f, loop.KillAABoxSx);
        Assert.Equal(0f, loop.AttractStrength);
        Assert.Equal(1f, loop.ConformSphereRadius);
        Assert.Equal(0f, loop.ConformSphereStrength);
        Assert.Equal(1f, loop.VortexAy);
        Assert.Equal(0f, loop.VortexRotation);
        Assert.Equal(1f, loop.ConformCylinderAy);
        Assert.Equal(1f, loop.ConformCylinderRadius);
        Assert.Equal(0f, loop.ConformCylinderStrength);
        Assert.Equal(0f, loop.KillPlaneDistance);
        Assert.Equal(0f, loop.LookAtMode);
        Assert.Equal(0f, loop.FlipbookRate);
        Assert.Equal(0f, loop.FlipbookStart);
        Assert.Equal(0f, loop.SizeOverSpeedAmount);
        Assert.Equal(1f, loop.MultiplySizeAmount);
        Assert.Equal(1f, loop.MultiplyColorR);
        Assert.Equal(1f, loop.MultiplyColorG);
        Assert.Equal(1f, loop.MultiplyColorB);
        Assert.Equal(1f, loop.MultiplyColorA);
        Assert.Equal(0f, loop.AddSizeAmount);
        Assert.Equal(0f, loop.AddColorR);
        Assert.Equal(0f, loop.AddColorG);
        Assert.Equal(0f, loop.AddColorB);
        Assert.Equal(0f, loop.AddColorA);
        Assert.Equal(0f, loop.AddVelocityX);
        Assert.Equal(0f, loop.AddVelocityY);
        Assert.Equal(0f, loop.AddVelocityZ);
        Assert.Equal(1f, loop.MultiplyVelocityAmount);
        Assert.Equal(0f, loop.OverwriteSizeAmount);
        Assert.Equal(0f, loop.OverwriteColorR);
        Assert.Equal(0f, loop.OverwriteColorG);
        Assert.Equal(0f, loop.OverwriteColorB);
        Assert.Equal(0f, loop.OverwriteColorA);
        Assert.Equal(0f, loop.OverwriteVelocityX);
        Assert.Equal(0f, loop.OverwriteVelocityY);
        Assert.Equal(0f, loop.OverwriteVelocityZ);
        Assert.Equal(0f, loop.OverwriteLifetimeAmount);
        Assert.Equal(0f, loop.AddLifetimeAmount);
        Assert.Equal(1f, loop.MultiplyLifetimeAmount);
        Assert.Equal(0f, loop.AddAgeAmount);
        Assert.Equal(1f, loop.MultiplyAgeAmount);
        Assert.Equal(0f, loop.SetAgeAmount);
        Assert.Equal(0f, loop.AddPositionX);
        Assert.Equal(0f, loop.AddPositionY);
        Assert.Equal(0f, loop.AddPositionZ);
        Assert.Equal(0f, loop.SetPositionX);
        Assert.Equal(0f, loop.SetPositionY);
        Assert.Equal(0f, loop.SetPositionZ);
        Assert.Equal(0f, loop.ColorOverSpeedScale);
        Assert.Equal(0f, loop.ScreenSpaceSizeAmount);
        Assert.Equal(0.5f, loop.SequentialSx, 3);
        Assert.Equal(8f, loop.SequentialSz);
        Assert.Equal(1f, loop.ColorOverLife);
        Assert.Equal(1f, loop.SizeOverLifeFrom);
        Assert.Equal(0f, loop.SizeOverLifeTo);
        Assert.Equal(0f, loop.DragAmount);
        Assert.Equal(0f, loop.ForceX);
        Assert.Equal(0f, loop.ForceY);
        Assert.Equal(0f, loop.ForceZ);
        Assert.Equal(0f, loop.AngularVelocityRate);
        Assert.Equal(0f, loop.CustomHlsl);
        Assert.False(loop.OutputMesh);
        Assert.Equal(0f, loop.LitMode);

        VfxCompiledSettings burst = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateBurst());
        Assert.Equal(0f, burst.SpawnRate);
        Assert.Equal(256f, burst.BurstCount);
        Assert.Equal(-2f, burst.Gravity.Y);
        Assert.Equal(0.85f, burst.StartColor.G, 3);
        Assert.Equal(1f, burst.CollisionMode);
        Assert.Equal(1f, burst.SdfCollisionMode);
        Assert.Equal(1f, burst.SdfShape);
    }

    [Fact]
    public void CompileSettings_BlackboardSdf()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.SdfCollisionMode)!.DefaultValue = "1";
        graph.FindProperty(VfxExposedNames.SdfShape)!.DefaultValue = "4";
        graph.FindProperty(VfxExposedNames.SdfCenter)!.DefaultValue = "0,1,0";
        graph.FindProperty(VfxExposedNames.SdfSize)!.DefaultValue = "0.8,0.6,0.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.SdfCollisionMode);
        Assert.Equal(4f, settings.SdfShape);
        Assert.Equal(1f, settings.SdfCenter.Y);
        Assert.Equal(0.8f, settings.SdfSize.X);
        Assert.Equal(0.6f, settings.SdfSize.Y, 3);
    }

    [Fact]
    public void CompileSettings_BlackboardTurbulence()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.TurbulenceIntensity)!.DefaultValue = "2.5";
        graph.FindProperty(VfxExposedNames.TurbulenceFrequency)!.DefaultValue = "3";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2.5f, settings.TurbulenceIntensity);
        Assert.Equal(3f, settings.TurbulenceFrequency);
    }

    [Fact]
    public void CompileSettings_BlackboardSoftParticleFade()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.SoftParticleFade)!.DefaultValue = "2.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2.5f, settings.SoftParticleFade);
    }

    [Fact]
    public void CompileSettings_BlackboardGpuEvent()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.EventKind)!.DefaultValue = "2";
        graph.FindProperty(VfxExposedNames.EventCount)!.DefaultValue = "4";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.EventKind);
        Assert.Equal(4f, settings.EventCount);
    }

    [Fact]
    public void CompileSettings_BlackboardDepthCollision()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.DepthCollisionMode)!.DefaultValue = "2";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.DepthCollisionMode);
    }

    [Fact]
    public void CompileSettings_BlackboardConformAttraction()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.ConformAttraction)!.DefaultValue = "3.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(3.5f, settings.ConformAttraction);
    }

    [Fact]
    public void CompileSettings_BlackboardVectorField()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.VectorFieldIntensity)!.DefaultValue = "2.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2.5f, settings.VectorFieldIntensity);
    }

    [Fact]
    public void CompileSettings_BlackboardSpawnOverDistance()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.SpawnOverDistance)!.DefaultValue = "8";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(8f, settings.SpawnOverDistance);
    }

    [Fact]
    public void CompileSettings_BlackboardAttributeMap()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.AttributeMapMode)!.DefaultValue = "4";
        graph.FindProperty(VfxExposedNames.AttributeMapScale)!.DefaultValue = "2.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(4f, settings.AttributeMapMode);
        Assert.Equal(2.5f, settings.AttributeMapScale);
    }

    [Fact]
    public void CompileSettings_BlackboardDistortion()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.DistortionIntensity)!.DefaultValue = "0.35";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.35f, settings.DistortionIntensity);
    }

    [Fact]
    public void CompileSettings_BlackboardParticleLight()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.ParticleLightIntensity)!.DefaultValue = "2.5";
        graph.FindProperty(VfxExposedNames.ParticleLightRange)!.DefaultValue = "3";
        graph.FindProperty(VfxExposedNames.ParticleLightCount)!.DefaultValue = "8";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2.5f, settings.ParticleLightIntensity);
        Assert.Equal(3f, settings.ParticleLightRange);
        Assert.Equal(8f, settings.ParticleLightCount);
    }

    [Fact]
    public void CompileSettings_BlackboardOverridesBlockRate()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxProperty rate = graph.FindProperty(VfxExposedNames.SpawnRate)!;
        rate.DefaultValue = "120";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(120f, settings.SpawnRate);
        Assert.Equal(-1.5f, settings.Gravity.Y);
    }

    [Fact]
    public void CompileSettings_BlackboardFlipbookSize()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.FlipbookColumns)!.DefaultValue = "8";
        graph.FindProperty(VfxExposedNames.FlipbookRows)!.DefaultValue = "2";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(8f, settings.FlipbookColumns);
        Assert.Equal(2f, settings.FlipbookRows);
    }

    [Fact]
    public void CompileSettings_BlackboardCollisionPlane()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.CollisionMode)!.DefaultValue = "1";
        graph.FindProperty(VfxExposedNames.CollisionBounce)!.DefaultValue = "0.25";
        graph.FindProperty(VfxExposedNames.CollisionPlane)!.DefaultValue = "0,1,0,0.5";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.CollisionMode);
        Assert.Equal(0.25f, settings.CollisionBounce);
        Assert.Equal(0.5f, settings.CollisionPlane.W);
    }

    [Fact]
    public void Compile_Loop_IsDeterministicAndParses()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        string a = VfxGraphCompiler.CompileToSource(graph);
        string b = VfxGraphCompiler.CompileToSource(graph);
        Assert.Equal(a, b);
        Assert.Contains("Shader \"VFX/Loop\"", a, StringComparison.Ordinal);
        Assert.Contains("Template=Loop", a, StringComparison.Ordinal);
        Assert.Contains("SpawnRate=64.0", a, StringComparison.Ordinal);
        Assert.Contains("particle.color = float4(1.0, 1.0, 1.0, 1.0);", a, StringComparison.Ordinal);
        Assert.Contains("_MainTex.Sample(_MainTexSampler", a, StringComparison.Ordinal);
        Assert.Contains("flipbookUv", a, StringComparison.Ordinal);
        Assert.Contains("_FlipbookColumns", a, StringComparison.Ordinal);
        Assert.Contains("CollisionMode=2.0", a, StringComparison.Ordinal);
        Assert.Contains("SdfMode=2.0", a, StringComparison.Ordinal);
        Assert.Contains("SdfShape=1.0", a, StringComparison.Ordinal);
        Assert.Contains("Turbulence=1.2", a, StringComparison.Ordinal);
        Assert.Contains("SoftFade=1.0", a, StringComparison.Ordinal);
        Assert.Contains("EventKind=1.0", a, StringComparison.Ordinal);
        Assert.Contains("EventCount=2.0", a, StringComparison.Ordinal);
        Assert.Contains("EventTarget=0.0", a, StringComparison.Ordinal);
        Assert.Contains("DepthMode=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Conform=0.0", a, StringComparison.Ordinal);
        Assert.Contains("VectorField=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Distance=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Periodic=0.0", a, StringComparison.Ordinal);
        Assert.Contains("AttributeMap=0.0", a, StringComparison.Ordinal);
        Assert.Contains("VfxApplyAttributeMap", a, StringComparison.Ordinal);
        Assert.Contains("_AttributeMap", a, StringComparison.Ordinal);
        Assert.Contains("attributeMapMode", a, StringComparison.Ordinal);
        Assert.Contains("Distort=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Lights=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Sort=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FadeNear=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FadeFar=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FlipbookBlend=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FlipbookMotion=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Motion=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Orient=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Shape=0.0", a, StringComparison.Ordinal);
        Assert.Contains("ColorOverLife=1.0", a, StringComparison.Ordinal);
        Assert.Contains("SizeOverLife=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Drag=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Force=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Spin=0.0", a, StringComparison.Ordinal);
        Assert.Contains("CustomHLSL=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Seq=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Inherit=0.0", a, StringComparison.Ordinal);
        Assert.Contains("TriggerRate=0.0", a, StringComparison.Ordinal);
        Assert.Contains("TriggerDist=0.0", a, StringComparison.Ordinal);
        Assert.Contains("TriggerOnce=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Friction=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Elastic=0.0", a, StringComparison.Ordinal);
        Assert.Contains("LifeLoss=0.0", a, StringComparison.Ordinal);
        Assert.Contains("LimitVel=0.0", a, StringComparison.Ordinal);
        Assert.Contains("KillSphere=0.0", a, StringComparison.Ordinal);
        Assert.Contains("KillBox=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Attract=0.0", a, StringComparison.Ordinal);
        Assert.Contains("ConformSphere=0.0", a, StringComparison.Ordinal);
        Assert.Contains("Vortex=0.0", a, StringComparison.Ordinal);
        Assert.Contains("ConformCyl=0.0", a, StringComparison.Ordinal);
        Assert.Contains("LookAt=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FlipRate=0.0", a, StringComparison.Ordinal);
        Assert.Contains("KillPlane=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FlipStart=0.0", a, StringComparison.Ordinal);
        Assert.Contains("SizeSpeed=0.0", a, StringComparison.Ordinal);
        Assert.Contains("MulSize=1.0", a, StringComparison.Ordinal);
        Assert.Contains("MulColor=1.0", a, StringComparison.Ordinal);
        Assert.Contains("AddSize=0.0", a, StringComparison.Ordinal);
        Assert.Contains("AddColor=0.0", a, StringComparison.Ordinal);
        Assert.Contains("AddVel=0.0", a, StringComparison.Ordinal);
        Assert.Contains("MulVel=1.0", a, StringComparison.Ordinal);
        Assert.Contains("OwSize=0.0", a, StringComparison.Ordinal);
        Assert.Contains("OwColor=0.0", a, StringComparison.Ordinal);
        Assert.Contains("OwVel=0.0", a, StringComparison.Ordinal);
        Assert.Contains("OwLife=0.0", a, StringComparison.Ordinal);
        Assert.Contains("AddLife=0.0", a, StringComparison.Ordinal);
        Assert.Contains("MulLife=1.0", a, StringComparison.Ordinal);
        Assert.Contains("AddAge=0.0", a, StringComparison.Ordinal);
        Assert.Contains("MulAge=1.0", a, StringComparison.Ordinal);
        Assert.Contains("SetAge=0.0", a, StringComparison.Ordinal);
        Assert.Contains("AddPos=0.0", a, StringComparison.Ordinal);
        Assert.Contains("SetPos=0.0", a, StringComparison.Ordinal);
        Assert.Contains("ColorSpeed=0.0", a, StringComparison.Ordinal);
        Assert.Contains("ScreenSize=0.0", a, StringComparison.Ordinal);
        Assert.Contains("FixedAxis=0.0", a, StringComparison.Ordinal);
        Assert.Contains("vfxLife01", a, StringComparison.Ordinal);
        Assert.Contains(
            "float4(1.0, 1.0, 1.0, 1.0) + (float4(1.0, 1.0, 1.0, 0.0) - float4(1.0, 1.0, 1.0, 1.0)) * vfxLife01",
            a,
            StringComparison.Ordinal);
        Assert.Contains("particle.size * (1.0 + (0.0 - 1.0) * vfxLife01)", a, StringComparison.Ordinal);
        Assert.Contains("VfxCameraFade", a, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookBlend", a, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookWarp", a, StringComparison.Ordinal);
        Assert.Contains("_FlipbookMotion", a, StringComparison.Ordinal);
        Assert.Contains(VfxGraphSourceCompiler.OutputPsMarker, a, StringComparison.Ordinal);
        Assert.Contains("VfxMotionNdcDelta", a, StringComparison.Ordinal);
        Assert.Contains("VfxMotionEncode", a, StringComparison.Ordinal);
        Assert.Contains("VfxOrientAlongVelocity", a, StringComparison.Ordinal);
        Assert.Contains("vfxOrientAlong", a, StringComparison.Ordinal);
        Assert.Contains(VfxGraphSourceCompiler.OutputMarker, a, StringComparison.Ordinal);
        Assert.Contains("VfxPositionShape", a, StringComparison.Ordinal);
        Assert.Contains("VfxSequential", a, StringComparison.Ordinal);
        Assert.Contains("VfxDrag", a, StringComparison.Ordinal);
        Assert.Contains("VfxForce", a, StringComparison.Ordinal);
        Assert.Contains("VfxAngularVelocity", a, StringComparison.Ordinal);
        Assert.Contains("VfxSpinLocal", a, StringComparison.Ordinal);
        Assert.Contains("_MotionVectorMode", a, StringComparison.Ordinal);
        Assert.Contains("_OrientMode", a, StringComparison.Ordinal);
        Assert.Contains("_CameraFadeNear", a, StringComparison.Ordinal);
        Assert.Contains("VfxSortDist2", a, StringComparison.Ordinal);
        Assert.Contains("VfxDistortUv", a, StringComparison.Ordinal);
        Assert.Contains("_VfxSceneColor", a, StringComparison.Ordinal);
        Assert.Contains("_DistortionIntensity", a, StringComparison.Ordinal);
        Assert.Contains("VfxDepthProject", a, StringComparison.Ordinal);
        Assert.Contains("conformAttraction", a, StringComparison.Ordinal);
        Assert.Contains("vectorFieldIntensity", a, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVectorField", a, StringComparison.Ordinal);
        Assert.Contains("_VectorField", a, StringComparison.Ordinal);
        Assert.Contains("_VfxCameraDepth", a, StringComparison.Ordinal);
        Assert.Contains("turbulenceIntensity", a, StringComparison.Ordinal);
        Assert.Contains("eventKind", a, StringComparison.Ordinal);
        Assert.Contains("VfxEventEmitCount", a, StringComparison.Ordinal);
        Assert.Contains("_SoftParticleFade", a, StringComparison.Ordinal);
        Assert.Contains("VfxSoftFade", a, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVolumeSdf", a, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCylinder", a, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCone", a, StringComparison.Ordinal);
        Assert.Contains("_SdfVolume", a, StringComparison.Ordinal);
        Assert.Contains("collisionPlane", a, StringComparison.Ordinal);
        Assert.Contains("sdfCenter", a, StringComparison.Ordinal);
        Assert.Contains(VfxGraphCompiler.InitMarker, a, StringComparison.Ordinal);
        Assert.Contains("OutputMesh=0", a, StringComparison.Ordinal);
        Assert.Contains("OutputStrip=0", a, StringComparison.Ordinal);
        Assert.Contains("OutputDecal=0", a, StringComparison.Ordinal);
        Assert.Contains("VfxDecalProject", a, StringComparison.Ordinal);
        Assert.Contains("_DecalPlane", a, StringComparison.Ordinal);
        Assert.Contains("Subgraphs=0", a, StringComparison.Ordinal);
        Assert.Contains("LitMode=0.0", a, StringComparison.Ordinal);
        Assert.Contains("_LitMode", a, StringComparison.Ordinal);
        Assert.DoesNotContain("lerp(", a, StringComparison.Ordinal);

        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(VfxShaderLibrary.InitPass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.UpdatePass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.WriteArgsPass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.SortPass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.OutputPass).HasHlslGraphics);
    }

    [Fact]
    public void Compile_CustomHlsl_EmitsUserCode()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.CustomHLSL)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Code")
                    graph.Blocks[i].Fields[f].Value = "particle.velocity.y += 0.5;";
            }
        }

        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("CustomHLSL=1.0", source, StringComparison.Ordinal);
        Assert.Contains("particle.velocity.y += 0.5;", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_InheritVelocity_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.InheritVelocity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.InheritVelocityAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Inherit=0.5", source, StringComparison.Ordinal);
        Assert.Contains("particle.velocity += p.gravity * 0.5", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_TriggerEventRate_EmitsHeader()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.TriggerEventRate)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Rate")
                    graph.Blocks[i].Fields[f].Value = "8";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(8f, settings.TriggerEventRate, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("TriggerRate=8.0", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_TriggerEventOverDistance_EmitsHeader()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.TriggerEventOverDistance)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Rate")
                    graph.Blocks[i].Fields[f].Value = "8";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(8f, settings.TriggerEventOverDistance, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("TriggerDist=8.0", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_TriggerEventOnce_EmitsHeader()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.TriggerEventOnce)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Count")
                    graph.Blocks[i].Fields[f].Value = "3";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(3f, settings.TriggerEventOnceCount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("TriggerOnce=3.0", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_CollisionFriction_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.CollisionFriction)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.4";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.4f, settings.CollisionFrictionAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Friction=0.4", source, StringComparison.Ordinal);
        Assert.Contains("VfxCollisionFriction(particle.velocity, vfxHitN, 0.4)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_CollisionElasticity_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.CollisionElasticity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.4";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.4f, settings.CollisionElasticityAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Elastic=0.4", source, StringComparison.Ordinal);
        Assert.Contains("VfxCollisionElasticity(particle.velocity, vfxHitN, 0.4)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_CollisionLifetimeLoss_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.CollisionLifetimeLoss)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.4";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.4f, settings.CollisionLifetimeLossAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("LifeLoss=0.4", source, StringComparison.Ordinal);
        Assert.Contains("VfxCollisionLifetimeLoss(particle.lifetime, particle.age, 0.4)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_LimitVelocity_EmitsClamp()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.LimitVelocity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Speed")
                    graph.Blocks[i].Fields[f].Value = "4";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(4f, settings.LimitVelocitySpeed, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("LimitVel=4.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxLimitVelocity(particle.velocity, 4.0)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_KillSphere_EmitsInside()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.KillSphere)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Radius")
                    graph.Blocks[i].Fields[f].Value = "1.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1.5f, settings.KillSphereRadius, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("KillSphere=1.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxKillSphere(particle.alive, particle.position, float3(0.0, 0.0, 0.0), 1.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_KillAABox_EmitsInside()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.KillAABox)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key is "Sx" or "Sy" or "Sz")
                    graph.Blocks[i].Fields[f].Value = "1";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.KillAABoxSx, 3);
        Assert.Equal(1f, settings.KillAABoxSy, 3);
        Assert.Equal(1f, settings.KillAABoxSz, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("KillBox=1.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxKillAABox(particle.alive, particle.position, float3(0.0, 0.0, 0.0), float3(1.0, 1.0, 1.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_Attract_EmitsPull()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.Attract)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Strength")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.AttractStrength, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Attract=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxAttract(particle.velocity, particle.position, float3(0.0, 0.0, 0.0), 2.0, p.deltaTime)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_ConformToSphere_EmitsPull()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.ConformToSphere)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Strength")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.ConformSphereRadius, 3);
        Assert.Equal(2f, settings.ConformSphereStrength, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("ConformSphere=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxConformToSphere(particle.velocity, particle.position, float3(0.0, 0.0, 0.0), 1.0, 2.0, p.deltaTime)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_Vortex_EmitsSpin()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.Vortex)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Rotation")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.VortexAy, 3);
        Assert.Equal(2f, settings.VortexRotation, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Vortex=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxVortex(particle.velocity, particle.position, float3(0.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), 2.0, 0.0, p.deltaTime)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_ConformToCylinder_EmitsPull()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.ConformToCylinder)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Strength")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.ConformCylinderAy, 3);
        Assert.Equal(1f, settings.ConformCylinderRadius, 3);
        Assert.Equal(2f, settings.ConformCylinderStrength, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("ConformCyl=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxConformToCylinder(particle.velocity, particle.position, float3(0.0, 0.0, 0.0), float3(0.0, 1.0, 0.0), 1.0, 2.0, p.deltaTime)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_MultiplySize_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.MultiplySize)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.MultiplySizeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("MulSize=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplySize(particle.size, 2.0)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_MultiplyColor_EmitsMul()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.MultiplyColor)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "R")
                    graph.Blocks[i].Fields[f].Value = "0.5";
                else if (graph.Blocks[i].Fields[f].Key == "G")
                    graph.Blocks[i].Fields[f].Value = "0.25";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.MultiplyColorR, 3);
        Assert.Equal(0.25f, settings.MultiplyColorG, 3);
        Assert.Equal(1f, settings.MultiplyColorB, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("MulColor=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyColor(particle.color, float4(0.5, 0.25, 1.0, 1.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddSize_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddSize)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.1";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.1f, settings.AddSizeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddSize=0.1", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddSize(particle.size, 0.1)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddColor_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddColor)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "R")
                    graph.Blocks[i].Fields[f].Value = "0.25";
                else if (graph.Blocks[i].Fields[f].Key == "G")
                    graph.Blocks[i].Fields[f].Value = "0.1";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.25f, settings.AddColorR, 3);
        Assert.Equal(0.1f, settings.AddColorG, 3);
        Assert.Equal(0f, settings.AddColorB, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddColor=0.25", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddColor(particle.color, float4(0.25, 0.1, 0.0, 0.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddVelocity_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddVelocity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "X")
                    graph.Blocks[i].Fields[f].Value = "1.5";
                else if (graph.Blocks[i].Fields[f].Key == "Y")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1.5f, settings.AddVelocityX, 3);
        Assert.Equal(0.5f, settings.AddVelocityY, 3);
        Assert.Equal(0f, settings.AddVelocityZ, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddVel=1.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddVelocity(particle.velocity, float3(1.5, 0.5, 0.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_MultiplyVelocity_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.MultiplyVelocity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.MultiplyVelocityAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("MulVel=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyVelocity(particle.velocity, 2.0)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_OverwriteSize_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.OverwriteSize)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.2f, settings.OverwriteSizeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("OwSize=0.2", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetSize(particle.size, 0.2)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_OverwriteColor_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.OverwriteColor)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "R")
                    graph.Blocks[i].Fields[f].Value = "0.25";
                else if (graph.Blocks[i].Fields[f].Key == "G")
                    graph.Blocks[i].Fields[f].Value = "0.1";
                else if (graph.Blocks[i].Fields[f].Key == "A")
                    graph.Blocks[i].Fields[f].Value = "1";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.25f, settings.OverwriteColorR, 3);
        Assert.Equal(0.1f, settings.OverwriteColorG, 3);
        Assert.Equal(0f, settings.OverwriteColorB, 3);
        Assert.Equal(1f, settings.OverwriteColorA, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("OwColor=0.25", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetColor(particle.color, float4(0.25, 0.1, 0.0, 1.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_OverwriteVelocity_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.OverwriteVelocity)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "X")
                    graph.Blocks[i].Fields[f].Value = "1.5";
                else if (graph.Blocks[i].Fields[f].Key == "Y")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1.5f, settings.OverwriteVelocityX, 3);
        Assert.Equal(0.5f, settings.OverwriteVelocityY, 3);
        Assert.Equal(0f, settings.OverwriteVelocityZ, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("OwVel=1.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetVelocity(particle.velocity, float3(1.5, 0.5, 0.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_OverwriteLifetime_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.OverwriteLifetime)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "1.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1.5f, settings.OverwriteLifetimeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("OwLife=1.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetLifetime(particle.lifetime, 1.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddLifetime_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddLifetime)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.AddLifetimeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddLife=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddLifetime(particle.lifetime, 0.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_MultiplyLifetime_EmitsMul()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.MultiplyLifetime)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.MultiplyLifetimeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("MulLife=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyLifetime(particle.lifetime, 2.0)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddAge_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddAge)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.AddAgeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddAge=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddAge(particle.age, 0.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_MultiplyAge_EmitsMul()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.MultiplyAge)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.MultiplyAgeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("MulAge=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxMultiplyAge(particle.age, 2.0)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_SetAge_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.SetAge)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.SetAgeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("SetAge=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetAge(particle.age, 0.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_AddPosition_EmitsAdd()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.AddPosition)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "X")
                    graph.Blocks[i].Fields[f].Value = "0.5";
                else if (graph.Blocks[i].Fields[f].Key == "Y")
                    graph.Blocks[i].Fields[f].Value = "0.25";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.AddPositionX, 3);
        Assert.Equal(0.25f, settings.AddPositionY, 3);
        Assert.Equal(0f, settings.AddPositionZ, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("AddPos=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxAddPosition(particle.position, float3(0.5, 0.25, 0.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_SetPosition_EmitsAssign()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.SetPosition)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "X")
                    graph.Blocks[i].Fields[f].Value = "0.5";
                else if (graph.Blocks[i].Fields[f].Key == "Y")
                    graph.Blocks[i].Fields[f].Value = "0.25";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.SetPositionX, 3);
        Assert.Equal(0.25f, settings.SetPositionY, 3);
        Assert.Equal(0f, settings.SetPositionZ, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("SetPos=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxSetPosition(particle.position, float3(0.5, 0.25, 0.0))", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_SizeOverSpeed_EmitsMul()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.SizeOverSpeed)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "0.5";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0.5f, settings.SizeOverSpeedAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("SizeSpeed=0.5", source, StringComparison.Ordinal);
        Assert.Contains(
            "particle.size = particle.size * (1.0 + length(particle.velocity) * 0.5)",
            source,
            StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_ColorOverSpeed_EmitsMix()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.ColorOverSpeed)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Scale")
                    graph.Blocks[i].Fields[f].Value = "4";
                else if (graph.Blocks[i].Fields[f].Key == "ToR")
                    graph.Blocks[i].Fields[f].Value = "0.2";
                else if (graph.Blocks[i].Fields[f].Key == "ToG")
                    graph.Blocks[i].Fields[f].Value = "0.85";
                else if (graph.Blocks[i].Fields[f].Key == "ToB")
                    graph.Blocks[i].Fields[f].Value = "1";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(4f, settings.ColorOverSpeedScale, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("ColorSpeed=4.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxSpeed01", source, StringComparison.Ordinal);
        Assert.Contains(
            "float4(1.0, 1.0, 1.0, 1.0) + (float4(0.2, 0.85, 1.0, 1.0) - float4(1.0, 1.0, 1.0, 1.0)) * vfxSpeed01",
            source,
            StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_ScreenSpaceSize_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.ScreenSpaceSize)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Amount")
                    graph.Blocks[i].Fields[f].Value = "2";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.ScreenSpaceSizeAmount, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("ScreenSize=2.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxScreenRef", source, StringComparison.Ordinal);
        Assert.Contains(
            "particle.size = particle.size * (length(particle.position - _WorldSpaceCameraPos) / max(vfxScreenRef, 1e-5))",
            source,
            StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_FlipbookMotion_EmitsScale()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.FlipbookMotion)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Scale")
                    graph.Blocks[i].Fields[f].Value = "1.25";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1.25f, settings.FlipbookMotionScale, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("FlipbookMotion=1.25", source, StringComparison.Ordinal);
        Assert.Contains("vfxFlipbookMotionScale = 1.25", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_GpuEventTarget_SetsName()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.GPUEventTarget)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Name")
                    graph.Blocks[i].Fields[f].Value = "ChildFx";
            }
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.EventTarget);
        Assert.Equal("ChildFx", settings.EventTargetName);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("EventTarget=1.0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_FixedAxis_EmitsAxis()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type != VfxNodeLibrary.OrientFixedAxis)
                continue;
            for (int f = 0; f < graph.Blocks[i].Fields.Count; f++)
            {
                if (graph.Blocks[i].Fields[f].Key == "Mode")
                    graph.Blocks[i].Fields[f].Value = "2";
                else if (graph.Blocks[i].Fields[f].Key == "X")
                    graph.Blocks[i].Fields[f].Value = "1";
                else if (graph.Blocks[i].Fields[f].Key == "Y")
                    graph.Blocks[i].Fields[f].Value = "0";
            }
        }

        graph.FindProperty(VfxExposedNames.OrientMode)!.DefaultValue = "2";
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("FixedAxis=2.0", source, StringComparison.Ordinal);
        Assert.Contains("Orient=2.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxOrientAlong = float3(1.0, 0.0, 0.0);", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_OrientLookAt_EmitsDirection()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxBlock? lookAt = null;
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type == VfxNodeLibrary.OrientLookAt)
                lookAt = graph.Blocks[i];
        }
        Assert.NotNull(lookAt);
        for (int f = 0; f < lookAt!.Fields.Count; f++)
        {
            if (lookAt.Fields[f].Key == "Mode")
                lookAt.Fields[f].Value = "2";
        }
        graph.FindProperty(VfxExposedNames.OrientMode)!.DefaultValue = "2";

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.LookAtMode, 3);
        Assert.Equal(2f, settings.OrientMode, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("LookAt=2.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxOrientLookAt(particle.position, float3(0.0, 0.0, 0.0), vfxOrientAlong)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_FlipbookRate_EmitsRate()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxBlock? rate = null;
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type == VfxNodeLibrary.FlipbookRate)
                rate = graph.Blocks[i];
        }
        Assert.NotNull(rate);
        for (int f = 0; f < rate!.Fields.Count; f++)
        {
            if (rate.Fields[f].Key == "Rate")
                rate.Fields[f].Value = "8";
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(8f, settings.FlipbookRate, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("FlipRate=8.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxFlipbookRate = 8.0;", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_FlipbookStart_EmitsOffset()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxBlock? start = null;
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type == VfxNodeLibrary.FlipbookStart)
                start = graph.Blocks[i];
        }
        Assert.NotNull(start);
        for (int f = 0; f < start!.Fields.Count; f++)
        {
            if (start.Fields[f].Key == "Frame")
                start.Fields[f].Value = "2";
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.FlipbookStart, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("FlipStart=2.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxFlipbookStart = 2.0;", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_KillPlane_EmitsHalfSpace()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxBlock? plane = null;
        for (int i = 0; i < graph.Blocks.Count; i++)
        {
            if (graph.Blocks[i].Type == VfxNodeLibrary.KillPlane)
                plane = graph.Blocks[i];
        }
        Assert.NotNull(plane);
        for (int f = 0; f < plane!.Fields.Count; f++)
        {
            if (plane.Fields[f].Key == "Ny")
                plane.Fields[f].Value = "1";
            else if (plane.Fields[f].Key == "Distance")
                plane.Fields[f].Value = "0.5";
        }

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.KillPlaneNy, 3);
        Assert.Equal(0.5f, settings.KillPlaneDistance, 3);
        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("KillPlane=0.5", source, StringComparison.Ordinal);
        Assert.Contains("VfxKillPlane(particle.alive, particle.position, float3(0.0, 1.0, 0.0), 0.5)", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
    }

    [Fact]
    public void Compile_Mesh_EmitsOutputParticleMesh()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateMesh();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.True(settings.OutputMesh);
        Assert.Equal(32f, settings.SpawnRate);
        Assert.Equal(1f, settings.LitMode);

        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Shader \"VFX/Mesh\"", source, StringComparison.Ordinal);
        Assert.Contains("OutputMesh=1", source, StringComparison.Ordinal);
        Assert.Contains("LitMode=1.0", source, StringComparison.Ordinal);
        Assert.Contains("_OutputMode", source, StringComparison.Ordinal);
        Assert.Contains("indexCount", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(VfxShaderLibrary.WriteArgsPass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.OutputPass).HasHlslGraphics);
    }

    [Fact]
    public void Compile_Decal_EmitsOutputMode()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateDecal();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.True(settings.OutputDecal);
        Assert.False(settings.OutputMesh);
        Assert.False(settings.OutputStrip);
        Assert.Equal(64f, settings.SpawnRate);

        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Shader \"VFX/Decal\"", source, StringComparison.Ordinal);
        Assert.Contains("OutputDecal=1", source, StringComparison.Ordinal);
        Assert.Contains("VfxDecalProject", source, StringComparison.Ordinal);
        Assert.Contains("_DecalPlane", source, StringComparison.Ordinal);
        Assert.Contains("_OutputMode", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lerp(", source, StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(VfxShaderLibrary.WriteArgsPass).HasCompute);
        Assert.True(shader.GetPass(VfxShaderLibrary.OutputPass).HasHlslGraphics);
    }

    [Fact]
    public void Compile_Trail_EmitsColorOverLifeLerp()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateTrail());
        Assert.True(settings.OutputStrip);
        Assert.False(settings.OutputMesh);

        string source = VfxGraphCompiler.CompileToSource(VfxGraphTemplates.CreateTrail());
        Assert.Contains("Shader \"VFX/Trail\"", source, StringComparison.Ordinal);
        Assert.Contains("OutputStrip=1", source, StringComparison.Ordinal);
        Assert.Contains("VfxStripExtrude", source, StringComparison.Ordinal);
        Assert.Contains(
            "float4(1.0, 1.0, 1.0, 1.0) + (float4(1.0, 1.0, 1.0, 0.0) - float4(1.0, 1.0, 1.0, 1.0)) * vfxLife01",
            source,
            StringComparison.Ordinal);
        Assert.Contains("saturate(particle.age", source, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("VFX/Trail.shader", source, out Shader? shader));
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(VfxShaderLibrary.UpdatePass).HasCompute);
    }

    [Fact]
    public void Compile_Empty_ParsesWithoutInitOverrides()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateEmpty();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(0f, settings.SpawnRate);
        Assert.Equal(0f, settings.Gravity.Y);

        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Shader \"VFX/Empty\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("particle.color = float4", source, StringComparison.Ordinal);
        Assert.True(ShaderParser.ParseShader("VFX/Empty.shader", source, out _));
    }

    [Fact]
    public void CompileSettings_BlackboardLitMode()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.LitMode)!.DefaultValue = "2";
        graph.FindProperty(VfxExposedNames.Shininess)!.DefaultValue = "16";
        graph.FindProperty(VfxExposedNames.LightDirection)!.DefaultValue = "0,1,0";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(2f, settings.LitMode);
        Assert.Equal(16f, settings.Shininess);
        Assert.Equal(1f, settings.LightDirection.Y);
    }

    [Fact]
    public void Expand_LoopWithTrail_InlinesColorOverLife()
    {
        VfxGraphAsset host = VfxGraphTemplates.CreateLoop();
        host.Operators.Add(SubgraphOp("n_sub", "Trail"));
        VfxGraphLists expanded = VfxSubgraph.Expand(ListsOf(host), ResolveTrailInMemory);
        Assert.Contains(expanded.Blocks, b => b.Type == VfxNodeLibrary.ColorOverLife);
        Assert.DoesNotContain(expanded.Operators, o => o.Type == VfxNodeLibrary.Subgraph);
        Assert.Equal(1, expanded.SubgraphCount);
        Assert.Equal("64", VfxProperties.Find(expanded.Properties, VfxExposedNames.SpawnRate)!.DefaultValue);
    }

    [Fact]
    public void Compile_LoopWithTrailSubgraph_EmitsColorOverLifeLerp()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateSubgraph();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(64f, settings.SpawnRate);
        Assert.Equal(1, settings.SubgraphCount);
        Assert.False(settings.OutputMesh);

        string source = VfxGraphCompiler.CompileToSource(graph);
        Assert.Contains("Shader \"VFX/Subgraph\"", source, StringComparison.Ordinal);
        Assert.Contains("Subgraphs=1", source, StringComparison.Ordinal);
        Assert.Contains("SpawnRate=64.0", source, StringComparison.Ordinal);
        Assert.Contains(
            "float4(1.0, 1.0, 1.0, 1.0) + (float4(1.0, 1.0, 1.0, 0.0) - float4(1.0, 1.0, 1.0, 1.0)) * vfxLife01",
            source,
            StringComparison.Ordinal);
        Assert.True(VfxGraphCompiler.TryCompileToShader(graph, out Shader? shader, out string error), error);
        Assert.NotNull(shader);
        Assert.True(shader!.GetPass(VfxShaderLibrary.UpdatePass).HasCompute);
    }

    [Fact]
    public void Expand_Cycle_Throws()
    {
        var a = new VfxGraphLists { GraphName = "A" };
        a.Operators.Add(SubgraphOp("s", "B"));
        var b = new VfxGraphLists { GraphName = "B" };
        b.Operators.Add(SubgraphOp("s", "A"));
        var ex = Assert.Throws<VfxGraphCompileException>(() =>
            VfxSubgraph.Expand(a, path =>
            {
                if (VfxSubgraph.PathEquals(path, "A"))
                    return a.Clone();
                if (VfxSubgraph.PathEquals(path, "B"))
                    return b.Clone();
                return null;
            }));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expand_UnresolvedPath_Throws()
    {
        var lists = new VfxGraphLists { GraphName = "VFX/Missing" };
        lists.Operators.Add(SubgraphOp("n_sub", "DoesNotExist"));
        var ex = Assert.Throws<VfxGraphCompileException>(() =>
            VfxSubgraph.Expand(lists, _ => null));
        Assert.Contains("DoesNotExist", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UnknownBlock_Throws()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateEmpty();
        graph.Blocks.Add(new VfxBlock { Id = "b_bad", Type = "NotABlock", ContextId = "ctx_init" });
        Assert.Throws<VfxGraphCompileException>(() => VfxGraphCompiler.CompileToSource(graph));
    }

    [Fact]
    public void Compile_MissingContext_Throws()
    {
        var graph = new VfxGraphAsset { GraphName = "VFX/Broken" };
        graph.Contexts.Add(new VfxContext { Id = "ctx_spawn", Type = VfxNodeLibrary.Spawn });
        Assert.Throws<VfxGraphCompileException>(() => VfxGraphCompiler.CompileToSource(graph));
    }

    [Fact]
    public void Compiles_Loop_InitUpdate_Vulkan()
    {
        Assert.True(VfxGraphCompiler.TryCompileToShader(VfxGraphTemplates.CreateLoop(), out Shader? shader, out string error), error);
        CompileCompute(shader!.GetPass(VfxShaderLibrary.InitPass), GraphicsBackend.Vulkan);
        CompileCompute(shader.GetPass(VfxShaderLibrary.UpdatePass), GraphicsBackend.Vulkan);
        CompileCompute(shader.GetPass(VfxShaderLibrary.WriteArgsPass), GraphicsBackend.Vulkan);
        CompileCompute(shader.GetPass(VfxShaderLibrary.SortPass), GraphicsBackend.Vulkan);
    }

    private void CompileCompute(ShaderPass pass, GraphicsBackend backend)
    {
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            pass.CreateHlslCompileRequest(backend, null, ""));
        Assert.True(result.Success, $"{pass.Name}: {result.ErrorMessage}");
        Assert.NotEmpty(result.ComputeBytecode ?? []);
        _output.WriteLine($"{pass.Name} {backend}: {result.ComputeBytecode!.Length} B");
    }

    private static VfxGraphLists ListsOf(VfxGraphAsset graph)
        => VfxGraphLists.From(
            graph.GraphName, graph.Template, graph.Capacity,
            graph.Properties, graph.Contexts, graph.Blocks, graph.Operators, graph.Edges);

    private static VfxGraphLists? ResolveTrailInMemory(string path)
        => VfxSubgraph.PathEquals(path, "Trail") ? ListsOf(VfxGraphTemplates.CreateTrail()) : null;

    private static VfxOperator SubgraphOp(string id, string path)
    {
        var op = new VfxOperator { Id = id, Type = VfxNodeLibrary.Subgraph };
        op.Fields.Add(new VfxNodeField { Key = VfxExposedNames.SubgraphPath, Value = path });
        return op;
    }
}
