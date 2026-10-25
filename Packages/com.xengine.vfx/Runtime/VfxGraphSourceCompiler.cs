using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace XEngine.VFX;

/// <summary>
/// Engine-agnostic Visual Effect Graph lowering: Echo POCOs + <c>VfxParticles.shader</c> template
/// in, Shader DSL out. The Host links this file (no ScriptableObject / ShaderParser / Vector).
/// </summary>
public static class VfxGraphSourceCompiler
{
    public const string InitMarker = "// VFXGRAPH_INIT_BLOCKS";
    public const string UpdateMarker = "// VFXGRAPH_UPDATE_BLOCKS";
    public const string OutputMarker = "// VFXGRAPH_OUTPUT_VS";
    public const string OutputPsMarker = "// VFXGRAPH_OUTPUT_PS";
    public const string ShaderAssetName = "VfxParticles.shader";

    public static VfxGraphLoweredSettings CompileSettings(
        string graphName,
        VfxTemplateKind template,
        int capacity,
        IReadOnlyList<VfxProperty>? properties,
        IReadOnlyList<VfxContext>? contexts,
        IReadOnlyList<VfxBlock>? blocks,
        IReadOnlyList<VfxOperator>? operators,
        IReadOnlyList<VfxEdge>? edges,
        VfxSubgraphResolver? resolveSubgraph = null)
    {
        VfxGraphLists expanded = ExpandLists(
            graphName, template, capacity, properties, contexts, blocks, operators, edges, resolveSubgraph);
        var state = new State(
            expanded.GraphName, expanded.Template, expanded.Capacity,
            expanded.Properties, expanded.Contexts, expanded.Blocks, expanded.Operators, expanded.Edges);
        VfxGraphLoweredSettings settings = state.LowerSettings();
        settings.SubgraphCount = expanded.SubgraphCount;
        return settings;
    }

    public static string CompileToSource(
        string graphName,
        VfxTemplateKind template,
        int capacity,
        IReadOnlyList<VfxProperty>? properties,
        IReadOnlyList<VfxContext>? contexts,
        IReadOnlyList<VfxBlock>? blocks,
        IReadOnlyList<VfxOperator>? operators,
        IReadOnlyList<VfxEdge>? edges,
        string templateShaderSource,
        VfxSubgraphResolver? resolveSubgraph = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateShaderSource);
        VfxGraphLists expanded = ExpandLists(
            graphName, template, capacity, properties, contexts, blocks, operators, edges, resolveSubgraph);
        var state = new State(
            expanded.GraphName, expanded.Template, expanded.Capacity,
            expanded.Properties, expanded.Contexts, expanded.Blocks, expanded.Operators, expanded.Edges);
        state.Validate();
        VfxGraphLoweredSettings settings = state.LowerSettings();
        settings.SubgraphCount = expanded.SubgraphCount;
        string named = ReplaceShaderName(templateShaderSource, state.GraphName);
        string withInit = InsertAfterMarker(named, InitMarker, state.EmitInitHlsl());
        string withUpdate = InsertAfterMarker(withInit, UpdateMarker, state.EmitUpdateHlsl());
        string withOutput = InsertAfterMarker(withUpdate, OutputMarker, state.EmitOutputHlsl());
        string withOutputPs = InsertAfterMarker(withOutput, OutputPsMarker, state.EmitOutputPsHlsl());
        var header = new StringBuilder(256);
        header.Append("// Compiled from VFX Graph \"").Append(EscapeName(state.GraphName)).Append("\"\n");
        header.Append("// Template=").Append(template)
            .Append(" Capacity=").Append(settings.Capacity)
            .Append(" SpawnRate=").Append(Format(settings.SpawnRate))
            .Append(" BurstCount=").Append(Format(settings.BurstCount))
            .Append(" Distance=").Append(Format(settings.SpawnOverDistance))
            .Append(" Periodic=").Append(Format(settings.PeriodicBurstCount))
            .Append(" CollisionMode=").Append(Format(settings.CollisionMode))
            .Append(" SdfMode=").Append(Format(settings.SdfCollisionMode))
            .Append(" SdfShape=").Append(Format(settings.SdfShape))
            .Append(" OutputMesh=").Append(settings.OutputMesh ? "1" : "0")
            .Append(" OutputStrip=").Append(settings.OutputStrip ? "1" : "0")
            .Append(" OutputDecal=").Append(settings.OutputDecal ? "1" : "0")
            .Append(" Subgraphs=").Append(settings.SubgraphCount)
            .Append(" LitMode=").Append(Format(settings.LitMode))
            .Append(" Turbulence=").Append(Format(settings.TurbulenceIntensity))
            .Append(" SoftFade=").Append(Format(settings.SoftParticleFade))
            .Append(" EventKind=").Append(Format(settings.EventKind))
            .Append(" EventCount=").Append(Format(settings.EventCount))
            .Append(" EventTarget=").Append(Format(settings.EventTarget))
            .Append(" DepthMode=").Append(Format(settings.DepthCollisionMode))
            .Append(" Conform=").Append(Format(settings.ConformAttraction))
            .Append(" VectorField=").Append(Format(settings.VectorFieldIntensity))
            .Append(" AttributeMap=").Append(Format(settings.AttributeMapMode))
            .Append(" Distort=").Append(Format(settings.DistortionIntensity))
            .Append(" Lights=").Append(Format(settings.ParticleLightIntensity))
            .Append(" Sort=").Append(Format(settings.SortMode))
            .Append(" FadeNear=").Append(Format(settings.CameraFadeNear))
            .Append(" FadeFar=").Append(Format(settings.CameraFadeFar))
            .Append(" FlipbookBlend=").Append(Format(settings.FlipbookBlend))
            .Append(" FlipbookMotion=").Append(Format(settings.FlipbookMotionScale))
            .Append(" Motion=").Append(Format(settings.MotionVectorMode))
            .Append(" Orient=").Append(Format(settings.OrientMode))
            .Append(" Shape=").Append(Format(settings.PositionShapeMode))
            .Append(" Seq=").Append(Format(settings.SequentialMode))
            .Append(" Inherit=").Append(Format(settings.InheritVelocityAmount))
            .Append(" TriggerRate=").Append(Format(settings.TriggerEventRate))
            .Append(" TriggerDist=").Append(Format(settings.TriggerEventOverDistance))
            .Append(" TriggerOnce=").Append(Format(settings.TriggerEventOnceCount))
            .Append(" ColorOverLife=").Append(Format(settings.ColorOverLife))
            .Append(" SizeOverLife=").Append(Format(settings.SizeOverLifeTo))
            .Append(" SizeSpeed=").Append(Format(settings.SizeOverSpeedAmount))
            .Append(" MulSize=").Append(Format(settings.MultiplySizeAmount))
            .Append(" MulColor=").Append(Format(settings.MultiplyColorR))
            .Append(" AddSize=").Append(Format(settings.AddSizeAmount))
            .Append(" AddColor=").Append(Format(settings.AddColorR))
            .Append(" AddVel=").Append(Format(settings.AddVelocityX))
            .Append(" MulVel=").Append(Format(settings.MultiplyVelocityAmount))
            .Append(" OwSize=").Append(Format(settings.OverwriteSizeAmount))
            .Append(" OwColor=").Append(Format(settings.OverwriteColorR))
            .Append(" OwVel=").Append(Format(settings.OverwriteVelocityX))
            .Append(" OwLife=").Append(Format(settings.OverwriteLifetimeAmount))
            .Append(" AddLife=").Append(Format(settings.AddLifetimeAmount))
            .Append(" MulLife=").Append(Format(settings.MultiplyLifetimeAmount))
            .Append(" AddAge=").Append(Format(settings.AddAgeAmount))
            .Append(" MulAge=").Append(Format(settings.MultiplyAgeAmount))
            .Append(" SetAge=").Append(Format(settings.SetAgeAmount))
            .Append(" AddPos=").Append(Format(settings.AddPositionX))
            .Append(" SetPos=").Append(Format(settings.SetPositionX))
            .Append(" ColorSpeed=").Append(Format(settings.ColorOverSpeedScale))
            .Append(" ScreenSize=").Append(Format(settings.ScreenSpaceSizeAmount))
            .Append(" Friction=").Append(Format(settings.CollisionFrictionAmount))
            .Append(" Elastic=").Append(Format(settings.CollisionElasticityAmount))
            .Append(" LifeLoss=").Append(Format(settings.CollisionLifetimeLossAmount))
            .Append(" LimitVel=").Append(Format(settings.LimitVelocitySpeed))
            .Append(" KillSphere=").Append(Format(settings.KillSphereRadius))
            .Append(" KillBox=").Append(Format(settings.KillAABoxSx))
            .Append(" Attract=").Append(Format(settings.AttractStrength))
            .Append(" KillPlane=").Append(Format(settings.KillPlaneDistance))
            .Append(" ConformSphere=").Append(Format(settings.ConformSphereStrength))
            .Append(" Vortex=").Append(Format(settings.VortexRotation))
            .Append(" ConformCyl=").Append(Format(settings.ConformCylinderStrength))
            .Append(" Drag=").Append(Format(settings.DragAmount))
            .Append(" Force=").Append(Format(settings.ForceY))
            .Append(" Spin=").Append(Format(settings.AngularVelocityRate))
            .Append(" CustomHLSL=").Append(Format(settings.CustomHlsl))
            .Append(" FixedAxis=").Append(Format(settings.FixedAxisMode))
            .Append(" LookAt=").Append(Format(settings.LookAtMode))
            .Append(" FlipRate=").Append(Format(settings.FlipbookRate))
            .Append(" FlipStart=").Append(Format(settings.FlipbookStart))
            .Append('\n');
        header.Append(withOutputPs);
        return header.ToString();
    }

    private static VfxGraphLists ExpandLists(
        string graphName,
        VfxTemplateKind template,
        int capacity,
        IReadOnlyList<VfxProperty>? properties,
        IReadOnlyList<VfxContext>? contexts,
        IReadOnlyList<VfxBlock>? blocks,
        IReadOnlyList<VfxOperator>? operators,
        IReadOnlyList<VfxEdge>? edges,
        VfxSubgraphResolver? resolveSubgraph)
    {
        return VfxSubgraph.Expand(
            VfxGraphLists.From(graphName, template, capacity, properties, contexts, blocks, operators, edges),
            resolveSubgraph);
    }

    public static string ReadTemplateShader()
    {
        foreach (string candidate in EnumerateShaderPaths())
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            "VfxParticles.shader was not found next to the VFX package or the repository Packages folder.");
    }

    public static IEnumerable<string> EnumerateShaderPaths()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Packages", "com.xengine.vfx", "Runtime", "Shaders", ShaderAssetName);

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield return Path.Combine(directory.FullName, "Packages", "com.xengine.vfx", "Runtime", "Shaders", ShaderAssetName);
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
            {
                yield return Path.Combine(directory.FullName, "Packages", "com.xengine.vfx", "Runtime", "Shaders", ShaderAssetName);
                yield break;
            }
            directory = directory.Parent;
        }
    }

    public static IEnumerable<string> EnumeratePackageRoots()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Packages", "com.xengine.vfx");

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield return Path.Combine(directory.FullName, "Packages", "com.xengine.vfx");
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string repo = Path.Combine(directory.FullName, "Packages", "com.xengine.vfx");
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || Directory.Exists(repo))
                yield return repo;
            directory = directory.Parent;
        }
    }

    public static string FindTemplateFile(VfxTemplateKind kind)
    {
        string fileName = kind switch
        {
            VfxTemplateKind.Loop => "Loop.vfx",
            VfxTemplateKind.Burst => "Burst.vfx",
            VfxTemplateKind.Trail => "Trail.vfx",
            VfxTemplateKind.Mesh => "Mesh.vfx",
            VfxTemplateKind.Subgraph => "Subgraph.vfx",
            VfxTemplateKind.Decal => "Decal.vfx",
            _ => "Empty.vfx",
        };
        foreach (string root in EnumeratePackageRoots())
        {
            string path = Path.Combine(root, "Templates", fileName);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("VFX template not found: " + fileName);
    }

    internal static string Format(float value)
        => value.ToString("0.0###", CultureInfo.InvariantCulture);

    private static string ReplaceShaderName(string source, string graphName)
    {
        const string original = "Shader \"VFX/Particles\"";
        int at = source.IndexOf(original, StringComparison.Ordinal);
        if (at < 0)
            throw new VfxGraphCompileException("VfxParticles.shader is missing the VFX/Particles shader name.");
        return source.Substring(0, at)
            + "Shader \"" + EscapeName(graphName) + "\""
            + source.Substring(at + original.Length);
    }

    private static string InsertAfterMarker(string source, string marker, string hlsl)
    {
        int at = source.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            throw new VfxGraphCompileException("VfxParticles.shader is missing marker " + marker + ".");
        if (string.IsNullOrEmpty(hlsl))
            return source;
        int lineEnd = source.IndexOf('\n', at);
        if (lineEnd < 0)
            lineEnd = source.Length;
        return source.Substring(0, lineEnd + 1) + hlsl + source.Substring(lineEnd + 1);
    }

    private static string EscapeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "VFX/Unnamed";
        return name.Replace("\"", "'", StringComparison.Ordinal);
    }

    private readonly struct Rgba
    {
        public readonly float R, G, B, A;
        public Rgba(float r, float g, float b, float a)
        {
            R = r; G = g; B = b; A = a;
        }
    }

    private sealed class State
    {
        private readonly int _capacity;
        private readonly IReadOnlyList<VfxProperty> _properties;
        private readonly Dictionary<string, VfxContext> _contexts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VfxBlock> _blocks = new(StringComparer.Ordinal);
        private readonly Dictionary<string, VfxOperator> _operators = new(StringComparer.Ordinal);
        private readonly List<VfxEdge> _edges;

        public string GraphName { get; }

        public State(
            string graphName,
            VfxTemplateKind template,
            int capacity,
            IReadOnlyList<VfxProperty>? properties,
            IReadOnlyList<VfxContext>? contexts,
            IReadOnlyList<VfxBlock>? blocks,
            IReadOnlyList<VfxOperator>? operators,
            IReadOnlyList<VfxEdge>? edges)
        {
            GraphName = string.IsNullOrWhiteSpace(graphName) ? "VFX/Unnamed" : graphName;
            _ = template;
            _properties = properties ?? Array.Empty<VfxProperty>();
            _capacity = capacity < 1 ? 1 : capacity;
            _edges = edges == null ? new List<VfxEdge>() : new List<VfxEdge>(edges);
            if (contexts != null)
            {
                for (int i = 0; i < contexts.Count; i++)
                {
                    VfxContext ctx = contexts[i];
                    if (!string.IsNullOrEmpty(ctx.Id))
                        _contexts[ctx.Id] = ctx;
                }
            }
            if (blocks != null)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    VfxBlock block = blocks[i];
                    if (!string.IsNullOrEmpty(block.Id))
                        _blocks[block.Id] = block;
                }
            }
            if (operators != null)
            {
                for (int i = 0; i < operators.Count; i++)
                {
                    VfxOperator op = operators[i];
                    if (!string.IsNullOrEmpty(op.Id))
                        _operators[op.Id] = op;
                }
            }
        }

        public void Validate()
        {
            RequireContext(VfxNodeLibrary.Spawn);
            RequireContext(VfxNodeLibrary.Initialize);
            RequireContext(VfxNodeLibrary.Update);
            RequireOutputContext();
            foreach (VfxContext ctx in _contexts.Values)
            {
                if (!VfxNodeLibrary.IsContext(ctx.Type))
                    throw new VfxGraphCompileException("Unknown VFX context type '" + ctx.Type + "'.");
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (!VfxNodeLibrary.IsBlock(block.Type))
                    throw new VfxGraphCompileException("Unknown VFX block type '" + block.Type + "'.");
            }
            foreach (VfxOperator op in _operators.Values)
            {
                if (!VfxNodeLibrary.IsOperator(op.Type))
                    throw new VfxGraphCompileException("Unknown VFX operator type '" + op.Type + "'.");
            }
        }

        public VfxGraphLoweredSettings LowerSettings()
        {
            var settings = new VfxGraphLoweredSettings
            {
                Capacity = _capacity,
            };
            foreach (VfxContext ctx in _contexts.Values)
            {
                if (ctx.Type == VfxNodeLibrary.OutputParticleMesh)
                    settings.OutputMesh = true;
                else if (ctx.Type == VfxNodeLibrary.OutputParticleStrip)
                    settings.OutputStrip = true;
                else if (ctx.Type == VfxNodeLibrary.OutputParticleDecal)
                    settings.OutputDecal = true;
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type == VfxNodeLibrary.ConstantSpawnRate)
                    settings.SpawnRate = ResolveFloat(block, "Rate", settings.SpawnRate);
                else if (block.Type == VfxNodeLibrary.SpawnOverDistance)
                    settings.SpawnOverDistance = ResolveFloat(block, "Rate", settings.SpawnOverDistance);
                else if (block.Type == VfxNodeLibrary.Burst)
                    settings.BurstCount = ResolveFloat(block, "Count", settings.BurstCount);
                else if (block.Type == VfxNodeLibrary.PeriodicBurst)
                {
                    settings.PeriodicBurstCount = ResolveFloat(block, "Count", settings.PeriodicBurstCount);
                    settings.PeriodicBurstPeriod = ResolveFloat(block, "Period", settings.PeriodicBurstPeriod);
                }
                else if (block.Type == VfxNodeLibrary.SetVelocity)
                    settings.StartSpeed = ResolveFloat(block, "Speed", settings.StartSpeed);
                else if (block.Type == VfxNodeLibrary.SetSize)
                    settings.StartSize = ResolveFloat(block, "Size", settings.StartSize);
                else if (block.Type == VfxNodeLibrary.SetLifetime)
                    settings.Lifetime = ResolveFloat(block, "Lifetime", settings.Lifetime);
                else if (block.Type == VfxNodeLibrary.SetColor)
                {
                    Rgba c = ResolveColor(block, "Color", new Rgba(settings.ColorR, settings.ColorG, settings.ColorB, settings.ColorA));
                    settings.ColorR = c.R;
                    settings.ColorG = c.G;
                    settings.ColorB = c.B;
                    settings.ColorA = c.A;
                }
                else if (block.Type == VfxNodeLibrary.Gravity)
                {
                    settings.GravityX = ResolveAxis(block, "Force", "X", settings.GravityX);
                    settings.GravityY = ResolveAxis(block, "Force", "Y", settings.GravityY);
                    settings.GravityZ = ResolveAxis(block, "Force", "Z", settings.GravityZ);
                }
                else if (block.Type == VfxNodeLibrary.CollideWithPlane)
                {
                    settings.CollisionMode = ResolveFloat(block, "Mode", settings.CollisionMode);
                    settings.CollisionBounce = ResolveFloat(block, "Bounce", settings.CollisionBounce);
                    settings.CollisionNx = FieldFloat(block.Fields, "Nx", settings.CollisionNx);
                    settings.CollisionNy = FieldFloat(block.Fields, "Ny", settings.CollisionNy);
                    settings.CollisionNz = FieldFloat(block.Fields, "Nz", settings.CollisionNz);
                    settings.CollisionD = FieldFloat(block.Fields, "Distance", settings.CollisionD);
                }
                else if (block.Type == VfxNodeLibrary.CollideWithSDF)
                {
                    settings.SdfCollisionMode = ResolveFloat(block, "Mode", settings.SdfCollisionMode);
                    settings.SdfShape = ResolveFloat(block, "Shape", settings.SdfShape);
                    settings.SdfCx = FieldFloat(block.Fields, "Cx", settings.SdfCx);
                    settings.SdfCy = FieldFloat(block.Fields, "Cy", settings.SdfCy);
                    settings.SdfCz = FieldFloat(block.Fields, "Cz", settings.SdfCz);
                    settings.SdfSx = FieldFloat(block.Fields, "Sx", settings.SdfSx);
                    settings.SdfSy = FieldFloat(block.Fields, "Sy", settings.SdfSy);
                    settings.SdfSz = FieldFloat(block.Fields, "Sz", settings.SdfSz);
                }
                else if (block.Type == VfxNodeLibrary.Turbulence)
                {
                    settings.TurbulenceIntensity = ResolveFloat(block, "Intensity", settings.TurbulenceIntensity);
                    settings.TurbulenceFrequency = ResolveFloat(block, "Frequency", settings.TurbulenceFrequency);
                }
                else if (block.Type == VfxNodeLibrary.TriggerEvent)
                {
                    settings.EventKind = ResolveFloat(block, "Kind", settings.EventKind);
                    settings.EventCount = ResolveFloat(block, "Count", settings.EventCount);
                }
                else if (block.Type == VfxNodeLibrary.TriggerEventRate)
                    settings.TriggerEventRate = ResolveFloat(block, "Rate", settings.TriggerEventRate);
                else if (block.Type == VfxNodeLibrary.TriggerEventOnce)
                    settings.TriggerEventOnceCount = ResolveFloat(block, "Count", settings.TriggerEventOnceCount);
                else if (block.Type == VfxNodeLibrary.TriggerEventOverDistance)
                    settings.TriggerEventOverDistance = ResolveFloat(block, "Rate", settings.TriggerEventOverDistance);
                else if (block.Type == VfxNodeLibrary.GPUEventTarget)
                {
                    settings.EventTargetName = FieldString(block.Fields, "Name", settings.EventTargetName);
                    settings.EventTarget = VfxGpuEvent.HasTarget(settings.EventTargetName) ? 1f : 0f;
                }
                else if (block.Type == VfxNodeLibrary.CollideWithDepth)
                {
                    settings.DepthCollisionMode = ResolveFloat(block, "Mode", settings.DepthCollisionMode);
                }
                else if (block.Type == VfxNodeLibrary.ConformToSDF)
                {
                    settings.ConformAttraction = ResolveFloat(block, "Attraction", settings.ConformAttraction);
                }
                else if (block.Type == VfxNodeLibrary.VectorField)
                {
                    settings.VectorFieldIntensity = ResolveFloat(block, "Intensity", settings.VectorFieldIntensity);
                }
                else if (block.Type == VfxNodeLibrary.AttributeMap)
                {
                    settings.AttributeMapMode = ResolveFloat(block, "Mode", settings.AttributeMapMode);
                    settings.AttributeMapScale = ResolveFloat(block, "Scale", settings.AttributeMapScale);
                }
                else if (block.Type == VfxNodeLibrary.PositionShape)
                {
                    settings.PositionShapeMode = ResolveFloat(block, "Mode", settings.PositionShapeMode);
                    settings.PositionShapeSx = ResolveFloat(block, "Sx", settings.PositionShapeSx);
                    settings.PositionShapeSy = ResolveFloat(block, "Sy", settings.PositionShapeSy);
                    settings.PositionShapeSz = ResolveFloat(block, "Sz", settings.PositionShapeSz);
                }
                else if (block.Type == VfxNodeLibrary.Sequential)
                {
                    settings.SequentialMode = ResolveFloat(block, "Mode", settings.SequentialMode);
                    settings.SequentialSx = ResolveFloat(block, "Sx", settings.SequentialSx);
                    settings.SequentialSy = ResolveFloat(block, "Sy", settings.SequentialSy);
                    settings.SequentialSz = ResolveFloat(block, "Sz", settings.SequentialSz);
                }
                else if (block.Type == VfxNodeLibrary.InheritVelocity)
                    settings.InheritVelocityAmount = ResolveFloat(block, "Amount", settings.InheritVelocityAmount);
                else if (block.Type == VfxNodeLibrary.Distort)
                    settings.DistortionIntensity = ResolveFloat(block, "Intensity", settings.DistortionIntensity);
                else if (block.Type == VfxNodeLibrary.ParticleLight)
                {
                    settings.ParticleLightIntensity = ResolveFloat(block, "Intensity", settings.ParticleLightIntensity);
                    settings.ParticleLightRange = ResolveFloat(block, "Range", settings.ParticleLightRange);
                    settings.ParticleLightCount = ResolveFloat(block, "Count", settings.ParticleLightCount);
                }
                else if (block.Type == VfxNodeLibrary.Sort)
                    settings.SortMode = ResolveFloat(block, "Mode", settings.SortMode);
                else if (block.Type == VfxNodeLibrary.CameraFade)
                {
                    settings.CameraFadeNear = ResolveFloat(block, "Near", settings.CameraFadeNear);
                    settings.CameraFadeFar = ResolveFloat(block, "Far", settings.CameraFadeFar);
                }
                else if (block.Type == VfxNodeLibrary.FlipbookBlend)
                    settings.FlipbookBlend = ResolveFloat(block, "Blend", settings.FlipbookBlend);
                else if (block.Type == VfxNodeLibrary.FlipbookMotion)
                    settings.FlipbookMotionScale = ResolveFloat(block, "Scale", settings.FlipbookMotionScale);
                else if (block.Type == VfxNodeLibrary.FlipbookRate)
                    settings.FlipbookRate = ResolveFloat(block, "Rate", settings.FlipbookRate);
                else if (block.Type == VfxNodeLibrary.FlipbookStart)
                    settings.FlipbookStart = ResolveFloat(block, "Frame", settings.FlipbookStart);
                else if (block.Type == VfxNodeLibrary.MotionVector)
                {
                    settings.MotionVectorMode = ResolveFloat(block, "Mode", settings.MotionVectorMode);
                    settings.MotionVectorScale = ResolveFloat(block, "Scale", settings.MotionVectorScale);
                }
                else if (block.Type == VfxNodeLibrary.OrientAlongVelocity)
                    settings.OrientMode = ResolveFloat(block, "Mode", settings.OrientMode);
                else if (block.Type == VfxNodeLibrary.OrientFixedAxis)
                {
                    settings.FixedAxisMode = ResolveFloat(block, "Mode", settings.FixedAxisMode);
                    settings.FixedAxisX = ResolveFloat(block, "X", settings.FixedAxisX);
                    settings.FixedAxisY = ResolveFloat(block, "Y", settings.FixedAxisY);
                    settings.FixedAxisZ = ResolveFloat(block, "Z", settings.FixedAxisZ);
                    if (settings.FixedAxisMode >= 1.5f)
                        settings.OrientMode = settings.FixedAxisMode;
                }
                else if (block.Type == VfxNodeLibrary.OrientLookAt)
                {
                    settings.LookAtMode = ResolveFloat(block, "Mode", settings.LookAtMode);
                    settings.LookAtX = ResolveFloat(block, "X", settings.LookAtX);
                    settings.LookAtY = ResolveFloat(block, "Y", settings.LookAtY);
                    settings.LookAtZ = ResolveFloat(block, "Z", settings.LookAtZ);
                    if (settings.LookAtMode >= 1.5f)
                        settings.OrientMode = settings.LookAtMode;
                }
                else if (block.Type == VfxNodeLibrary.ColorOverLife)
                    settings.ColorOverLife = 1f;
                else if (block.Type == VfxNodeLibrary.SizeOverLife)
                {
                    settings.SizeOverLifeFrom = ResolveFloat(block, "From", settings.SizeOverLifeFrom);
                    settings.SizeOverLifeTo = ResolveFloat(block, "To", 0f);
                }
                else if (block.Type == VfxNodeLibrary.SizeOverSpeed)
                    settings.SizeOverSpeedAmount = ResolveFloat(block, "Amount", settings.SizeOverSpeedAmount);
                else if (block.Type == VfxNodeLibrary.MultiplySize)
                    settings.MultiplySizeAmount = ResolveFloat(block, "Amount", settings.MultiplySizeAmount);
                else if (block.Type == VfxNodeLibrary.MultiplyColor)
                {
                    settings.MultiplyColorR = ResolveFloat(block, "R", settings.MultiplyColorR);
                    settings.MultiplyColorG = ResolveFloat(block, "G", settings.MultiplyColorG);
                    settings.MultiplyColorB = ResolveFloat(block, "B", settings.MultiplyColorB);
                    settings.MultiplyColorA = ResolveFloat(block, "A", settings.MultiplyColorA);
                }
                else if (block.Type == VfxNodeLibrary.AddSize)
                    settings.AddSizeAmount = ResolveFloat(block, "Amount", settings.AddSizeAmount);
                else if (block.Type == VfxNodeLibrary.AddColor)
                {
                    settings.AddColorR = ResolveFloat(block, "R", settings.AddColorR);
                    settings.AddColorG = ResolveFloat(block, "G", settings.AddColorG);
                    settings.AddColorB = ResolveFloat(block, "B", settings.AddColorB);
                    settings.AddColorA = ResolveFloat(block, "A", settings.AddColorA);
                }
                else if (block.Type == VfxNodeLibrary.AddVelocity)
                {
                    settings.AddVelocityX = ResolveFloat(block, "X", settings.AddVelocityX);
                    settings.AddVelocityY = ResolveFloat(block, "Y", settings.AddVelocityY);
                    settings.AddVelocityZ = ResolveFloat(block, "Z", settings.AddVelocityZ);
                }
                else if (block.Type == VfxNodeLibrary.MultiplyVelocity)
                    settings.MultiplyVelocityAmount = ResolveFloat(block, "Amount", settings.MultiplyVelocityAmount);
                else if (block.Type == VfxNodeLibrary.OverwriteSize)
                    settings.OverwriteSizeAmount = ResolveFloat(block, "Amount", settings.OverwriteSizeAmount);
                else if (block.Type == VfxNodeLibrary.OverwriteColor)
                {
                    settings.OverwriteColorR = ResolveFloat(block, "R", settings.OverwriteColorR);
                    settings.OverwriteColorG = ResolveFloat(block, "G", settings.OverwriteColorG);
                    settings.OverwriteColorB = ResolveFloat(block, "B", settings.OverwriteColorB);
                    settings.OverwriteColorA = ResolveFloat(block, "A", settings.OverwriteColorA);
                }
                else if (block.Type == VfxNodeLibrary.OverwriteVelocity)
                {
                    settings.OverwriteVelocityX = ResolveFloat(block, "X", settings.OverwriteVelocityX);
                    settings.OverwriteVelocityY = ResolveFloat(block, "Y", settings.OverwriteVelocityY);
                    settings.OverwriteVelocityZ = ResolveFloat(block, "Z", settings.OverwriteVelocityZ);
                }
                else if (block.Type == VfxNodeLibrary.OverwriteLifetime)
                    settings.OverwriteLifetimeAmount = ResolveFloat(block, "Amount", settings.OverwriteLifetimeAmount);
                else if (block.Type == VfxNodeLibrary.AddLifetime)
                    settings.AddLifetimeAmount = ResolveFloat(block, "Amount", settings.AddLifetimeAmount);
                else if (block.Type == VfxNodeLibrary.MultiplyLifetime)
                    settings.MultiplyLifetimeAmount = ResolveFloat(block, "Amount", settings.MultiplyLifetimeAmount);
                else if (block.Type == VfxNodeLibrary.AddAge)
                    settings.AddAgeAmount = ResolveFloat(block, "Amount", settings.AddAgeAmount);
                else if (block.Type == VfxNodeLibrary.MultiplyAge)
                    settings.MultiplyAgeAmount = ResolveFloat(block, "Amount", settings.MultiplyAgeAmount);
                else if (block.Type == VfxNodeLibrary.SetAge)
                    settings.SetAgeAmount = ResolveFloat(block, "Amount", settings.SetAgeAmount);
                else if (block.Type == VfxNodeLibrary.AddPosition)
                {
                    settings.AddPositionX = ResolveFloat(block, "X", settings.AddPositionX);
                    settings.AddPositionY = ResolveFloat(block, "Y", settings.AddPositionY);
                    settings.AddPositionZ = ResolveFloat(block, "Z", settings.AddPositionZ);
                }
                else if (block.Type == VfxNodeLibrary.SetPosition)
                {
                    settings.SetPositionX = ResolveFloat(block, "X", settings.SetPositionX);
                    settings.SetPositionY = ResolveFloat(block, "Y", settings.SetPositionY);
                    settings.SetPositionZ = ResolveFloat(block, "Z", settings.SetPositionZ);
                }
                else if (block.Type == VfxNodeLibrary.ColorOverSpeed)
                    settings.ColorOverSpeedScale = ResolveFloat(block, "Scale", settings.ColorOverSpeedScale);
                else if (block.Type == VfxNodeLibrary.ScreenSpaceSize)
                    settings.ScreenSpaceSizeAmount = ResolveFloat(block, "Amount", settings.ScreenSpaceSizeAmount);
                else if (block.Type == VfxNodeLibrary.CollisionFriction)
                    settings.CollisionFrictionAmount = ResolveFloat(block, "Amount", settings.CollisionFrictionAmount);
                else if (block.Type == VfxNodeLibrary.CollisionElasticity)
                    settings.CollisionElasticityAmount = ResolveFloat(block, "Amount", settings.CollisionElasticityAmount);
                else if (block.Type == VfxNodeLibrary.CollisionLifetimeLoss)
                    settings.CollisionLifetimeLossAmount = ResolveFloat(block, "Amount", settings.CollisionLifetimeLossAmount);
                else if (block.Type == VfxNodeLibrary.LimitVelocity)
                    settings.LimitVelocitySpeed = ResolveFloat(block, "Speed", settings.LimitVelocitySpeed);
                else if (block.Type == VfxNodeLibrary.KillSphere)
                {
                    settings.KillSphereX = ResolveFloat(block, "X", settings.KillSphereX);
                    settings.KillSphereY = ResolveFloat(block, "Y", settings.KillSphereY);
                    settings.KillSphereZ = ResolveFloat(block, "Z", settings.KillSphereZ);
                    settings.KillSphereRadius = ResolveFloat(block, "Radius", settings.KillSphereRadius);
                }
                else if (block.Type == VfxNodeLibrary.KillAABox)
                {
                    settings.KillAABoxX = ResolveFloat(block, "X", settings.KillAABoxX);
                    settings.KillAABoxY = ResolveFloat(block, "Y", settings.KillAABoxY);
                    settings.KillAABoxZ = ResolveFloat(block, "Z", settings.KillAABoxZ);
                    settings.KillAABoxSx = ResolveFloat(block, "Sx", settings.KillAABoxSx);
                    settings.KillAABoxSy = ResolveFloat(block, "Sy", settings.KillAABoxSy);
                    settings.KillAABoxSz = ResolveFloat(block, "Sz", settings.KillAABoxSz);
                }
                else if (block.Type == VfxNodeLibrary.Attract)
                {
                    settings.AttractX = ResolveFloat(block, "X", settings.AttractX);
                    settings.AttractY = ResolveFloat(block, "Y", settings.AttractY);
                    settings.AttractZ = ResolveFloat(block, "Z", settings.AttractZ);
                    settings.AttractStrength = ResolveFloat(block, "Strength", settings.AttractStrength);
                }
                else if (block.Type == VfxNodeLibrary.ConformToSphere)
                {
                    settings.ConformSphereX = ResolveFloat(block, "X", settings.ConformSphereX);
                    settings.ConformSphereY = ResolveFloat(block, "Y", settings.ConformSphereY);
                    settings.ConformSphereZ = ResolveFloat(block, "Z", settings.ConformSphereZ);
                    settings.ConformSphereRadius = ResolveFloat(block, "Radius", settings.ConformSphereRadius);
                    settings.ConformSphereStrength = ResolveFloat(block, "Strength", settings.ConformSphereStrength);
                }
                else if (block.Type == VfxNodeLibrary.Vortex)
                {
                    settings.VortexX = ResolveFloat(block, "X", settings.VortexX);
                    settings.VortexY = ResolveFloat(block, "Y", settings.VortexY);
                    settings.VortexZ = ResolveFloat(block, "Z", settings.VortexZ);
                    settings.VortexAx = ResolveFloat(block, "Ax", settings.VortexAx);
                    settings.VortexAy = ResolveFloat(block, "Ay", settings.VortexAy);
                    settings.VortexAz = ResolveFloat(block, "Az", settings.VortexAz);
                    settings.VortexRotation = ResolveFloat(block, "Rotation", settings.VortexRotation);
                    settings.VortexAttraction = ResolveFloat(block, "Attraction", settings.VortexAttraction);
                }
                else if (block.Type == VfxNodeLibrary.ConformToCylinder)
                {
                    settings.ConformCylinderX = ResolveFloat(block, "X", settings.ConformCylinderX);
                    settings.ConformCylinderY = ResolveFloat(block, "Y", settings.ConformCylinderY);
                    settings.ConformCylinderZ = ResolveFloat(block, "Z", settings.ConformCylinderZ);
                    settings.ConformCylinderAx = ResolveFloat(block, "Ax", settings.ConformCylinderAx);
                    settings.ConformCylinderAy = ResolveFloat(block, "Ay", settings.ConformCylinderAy);
                    settings.ConformCylinderAz = ResolveFloat(block, "Az", settings.ConformCylinderAz);
                    settings.ConformCylinderRadius = ResolveFloat(block, "Radius", settings.ConformCylinderRadius);
                    settings.ConformCylinderStrength = ResolveFloat(block, "Strength", settings.ConformCylinderStrength);
                }
                else if (block.Type == VfxNodeLibrary.KillPlane)
                {
                    settings.KillPlaneNx = ResolveFloat(block, "Nx", settings.KillPlaneNx);
                    settings.KillPlaneNy = ResolveFloat(block, "Ny", settings.KillPlaneNy);
                    settings.KillPlaneNz = ResolveFloat(block, "Nz", settings.KillPlaneNz);
                    settings.KillPlaneDistance = ResolveFloat(block, "Distance", settings.KillPlaneDistance);
                }
                else if (block.Type == VfxNodeLibrary.LinearDrag)
                    settings.DragAmount = ResolveFloat(block, "Amount", settings.DragAmount);
                else if (block.Type == VfxNodeLibrary.Force)
                {
                    settings.ForceX = ResolveAxis(block, "Force", "X", settings.ForceX);
                    settings.ForceY = ResolveAxis(block, "Force", "Y", settings.ForceY);
                    settings.ForceZ = ResolveAxis(block, "Force", "Z", settings.ForceZ);
                }
                else if (block.Type == VfxNodeLibrary.AngularVelocity)
                    settings.AngularVelocityRate = ResolveFloat(block, "Rate", settings.AngularVelocityRate);
                else if (block.Type == VfxNodeLibrary.CustomHLSL
                    && VfxCustomHlsl.HasCode(FieldString(block.Fields, "Code", "")))
                    settings.CustomHlsl = 1f;
            }
            ApplyExposedProperties(settings);
            return settings;
        }

        private void ApplyExposedProperties(VfxGraphLoweredSettings settings)
        {
            Span<float> parsed = stackalloc float[4];
            for (int i = 0; i < _properties.Count; i++)
            {
                VfxProperty property = _properties[i];
                string name = string.IsNullOrEmpty(property.Name) ? property.DisplayName : property.Name;
                if (string.IsNullOrEmpty(name))
                    continue;
                if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnRate)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float spawn))
                    settings.SpawnRate = spawn;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.BurstCount)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float burst))
                    settings.BurstCount = burst;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnOverDistance)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float distanceRate))
                    settings.SpawnOverDistance = distanceRate;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Lifetime)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float life))
                    settings.Lifetime = life;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSpeed)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float speed))
                    settings.StartSpeed = speed;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSize)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float size))
                    settings.StartSize = size;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Gravity))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed[..3]);
                    if (n > 0) settings.GravityX = parsed[0];
                    if (n > 1) settings.GravityY = parsed[1];
                    if (n > 2) settings.GravityZ = parsed[2];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartColor))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed);
                    if (n > 0) settings.ColorR = parsed[0];
                    if (n > 1) settings.ColorG = parsed[1];
                    if (n > 2) settings.ColorB = parsed[2];
                    if (n > 3) settings.ColorA = parsed[3];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookColumns)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float cols))
                    settings.FlipbookColumns = cols;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookRows)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float rows))
                    settings.FlipbookRows = rows;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookSize))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed[..2]);
                    if (n > 0) settings.FlipbookColumns = parsed[0];
                    if (n > 1) settings.FlipbookRows = parsed[1];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float collisionMode))
                    settings.CollisionMode = collisionMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionBounce)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float bounce))
                    settings.CollisionBounce = bounce;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionPlane))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed);
                    if (n > 0) settings.CollisionNx = parsed[0];
                    if (n > 1) settings.CollisionNy = parsed[1];
                    if (n > 2) settings.CollisionNz = parsed[2];
                    if (n > 3) settings.CollisionD = parsed[3];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCollisionMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float sdfMode))
                    settings.SdfCollisionMode = sdfMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfShape)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float sdfShape))
                    settings.SdfShape = sdfShape;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCenter))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed[..3]);
                    if (n > 0) settings.SdfCx = parsed[0];
                    if (n > 1) settings.SdfCy = parsed[1];
                    if (n > 2) settings.SdfCz = parsed[2];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfSize))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed[..3]);
                    if (n > 0) settings.SdfSx = parsed[0];
                    if (n > 1) settings.SdfSy = parsed[1];
                    if (n > 2) settings.SdfSz = parsed[2];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LitMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float litMode))
                    settings.LitMode = litMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightDirection))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed[..3]);
                    if (n > 0) settings.LightDx = parsed[0];
                    if (n > 1) settings.LightDy = parsed[1];
                    if (n > 2) settings.LightDz = parsed[2];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightColor))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed);
                    if (n > 0) settings.LightR = parsed[0];
                    if (n > 1) settings.LightG = parsed[1];
                    if (n > 2) settings.LightB = parsed[2];
                    if (n > 3) settings.LightIntensity = parsed[3];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AmbientColor))
                {
                    int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed);
                    if (n > 0) settings.AmbientR = parsed[0];
                    if (n > 1) settings.AmbientG = parsed[1];
                    if (n > 2) settings.AmbientB = parsed[2];
                }
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Shininess)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float shininess))
                    settings.Shininess = shininess;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceIntensity)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float turbI))
                    settings.TurbulenceIntensity = turbI;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceFrequency)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float turbF))
                    settings.TurbulenceFrequency = turbF;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SoftParticleFade)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float softFade))
                    settings.SoftParticleFade = softFade;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventKind)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float eventKind))
                    settings.EventKind = eventKind;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventCount)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float eventCount))
                    settings.EventCount = eventCount;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.DepthCollisionMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float depthMode))
                    settings.DepthCollisionMode = depthMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformAttraction)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float conform))
                    settings.ConformAttraction = conform;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.VectorFieldIntensity)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float fieldI))
                    settings.VectorFieldIntensity = fieldI;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float mapMode))
                    settings.AttributeMapMode = mapMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapScale)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float mapScale))
                    settings.AttributeMapScale = mapScale;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.DistortionIntensity)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float distort))
                    settings.DistortionIntensity = distort;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightIntensity)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float plightI))
                    settings.ParticleLightIntensity = plightI;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightRange)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float plightR))
                    settings.ParticleLightRange = plightR;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightCount)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float plightC))
                    settings.ParticleLightCount = plightC;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SortMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float sortMode))
                    settings.SortMode = sortMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeNear)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float fadeNear))
                    settings.CameraFadeNear = fadeNear;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeFar)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float fadeFar))
                    settings.CameraFadeFar = fadeFar;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookBlend)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float flipbookBlend))
                    settings.FlipbookBlend = flipbookBlend;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float motionMode))
                    settings.MotionVectorMode = motionMode;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorScale)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float motionScale))
                    settings.MotionVectorScale = motionScale;
                else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OrientMode)
                    && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float orientMode))
                    settings.OrientMode = orientMode;
            }
        }

        public string EmitInitHlsl()
        {
            var sb = new StringBuilder();
            foreach (VfxBlock block in OrderedBlocks(VfxNodeLibrary.Initialize))
            {
                if (block.Type == VfxNodeLibrary.SetColor)
                {
                    Rgba c = ResolveColor(block, "Color", new Rgba(1f, 1f, 1f, 1f));
                    sb.Append("            particle.color = ").Append(HlslFloat4(c)).Append(";\n");
                }
                else if (block.Type == VfxNodeLibrary.SetSize)
                {
                    float size = ResolveFloat(block, "Size", 0.15f);
                    sb.Append("            particle.size = ").Append(Format(size)).Append(";\n");
                }
                else if (block.Type == VfxNodeLibrary.SetLifetime)
                {
                    float life = ResolveFloat(block, "Lifetime", 2f);
                    sb.Append("            particle.lifetime = ").Append(Format(life)).Append(";\n");
                }
                else if (block.Type == VfxNodeLibrary.SetVelocity)
                {
                    float speed = ResolveFloat(block, "Speed", 2f);
                    sb.Append("            particle.velocity = HashDir(slot + p.spawnCount) * ")
                        .Append(Format(speed)).Append(";\n");
                }
                else if (block.Type == VfxNodeLibrary.PositionShape)
                {
                    float mode = ResolveFloat(block, "Mode", 0f);
                    float sx = ResolveFloat(block, "Sx", 0.25f);
                    float sy = ResolveFloat(block, "Sy", 0.25f);
                    float sz = ResolveFloat(block, "Sz", 0.25f);
                    sb.Append("            {\n");
                    sb.Append("                float3 vfxShape;\n");
                    sb.Append("                VfxPositionShape(slot, ")
                        .Append(Format(mode)).Append(", ")
                        .Append(Format(sx)).Append(", ")
                        .Append(Format(sy)).Append(", ")
                        .Append(Format(sz)).Append(", vfxShape);\n");
                    sb.Append("                particle.position += vfxShape;\n");
                    sb.Append("            }\n");
                }
                else if (block.Type == VfxNodeLibrary.Sequential)
                {
                    float mode = ResolveFloat(block, "Mode", 0f);
                    float sx = ResolveFloat(block, "Sx", 0.5f);
                    float sy = ResolveFloat(block, "Sy", 0.5f);
                    float sz = ResolveFloat(block, "Sz", 8f);
                    sb.Append("            {\n");
                    sb.Append("                float3 vfxSeq;\n");
                    sb.Append("                VfxSequential(slot, ")
                        .Append(Format(mode)).Append(", ")
                        .Append(Format(sx)).Append(", ")
                        .Append(Format(sy)).Append(", ")
                        .Append(Format(sz)).Append(", vfxSeq);\n");
                    sb.Append("                particle.position += vfxSeq;\n");
                    sb.Append("            }\n");
                }
                else if (block.Type == VfxNodeLibrary.InheritVelocity)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount > 0f)
                    {
                        sb.Append("            particle.velocity += p.gravity * ")
                            .Append(Format(amount))
                            .Append(";\n");
                    }
                }
            }
            return sb.ToString();
        }

        public string EmitUpdateHlsl()
        {
            var sb = new StringBuilder();
            foreach (VfxBlock block in OrderedBlocks(VfxNodeLibrary.Update))
            {
                if (block.Type == VfxNodeLibrary.ColorOverLife)
                {
                    Rgba from = ResolveColorFields(block, "From", new Rgba(1f, 1f, 1f, 1f));
                    Rgba to = ResolveColorFields(block, "To", new Rgba(1f, 1f, 1f, 0f));
                    sb.Append("            {\n");
                    sb.Append("                float vfxLife01 = saturate(particle.age / max(particle.lifetime, 1e-5));\n");
                    sb.Append("                particle.color = ")
                        .Append(HlslFloat4(from))
                        .Append(" + (")
                        .Append(HlslFloat4(to))
                        .Append(" - ")
                        .Append(HlslFloat4(from))
                        .Append(") * vfxLife01;\n");
                    sb.Append("            }\n");
                }
                else if (block.Type == VfxNodeLibrary.SizeOverLife)
                {
                    float from = ResolveFloat(block, "From", 1f);
                    float to = ResolveFloat(block, "To", 0f);
                    sb.Append("            {\n");
                    sb.Append("                float vfxLife01 = saturate(particle.age / max(particle.lifetime, 1e-5));\n");
                    sb.Append("                particle.size = particle.size * (")
                        .Append(Format(from))
                        .Append(" + (")
                        .Append(Format(to))
                        .Append(" - ")
                        .Append(Format(from))
                        .Append(") * vfxLife01);\n");
                    sb.Append("            }\n");
                }
                else if (block.Type == VfxNodeLibrary.SizeOverSpeed)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount > 0f)
                    {
                        sb.Append("            particle.size = particle.size * (1.0 + length(particle.velocity) * ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.ColorOverSpeed)
                {
                    float scale = ResolveFloat(block, "Scale", 0f);
                    if (scale > 0f)
                    {
                        Rgba from = ResolveColorFields(block, "From", new Rgba(1f, 1f, 1f, 1f));
                        Rgba to = ResolveColorFields(block, "To", new Rgba(1f, 1f, 1f, 1f));
                        sb.Append("            {\n");
                        sb.Append("                float vfxSpeed01 = saturate(length(particle.velocity) / max(")
                            .Append(Format(scale))
                            .Append(", 1e-5));\n");
                        sb.Append("                particle.color = ")
                            .Append(HlslFloat4(from))
                            .Append(" + (")
                            .Append(HlslFloat4(to))
                            .Append(" - ")
                            .Append(HlslFloat4(from))
                            .Append(") * vfxSpeed01;\n");
                        sb.Append("            }\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.CollisionFriction)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount > 0f)
                    {
                        sb.Append("            if (collided)\n");
                        sb.Append("                VfxCollisionFriction(particle.velocity, vfxHitN, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.CollisionElasticity)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount > 0f)
                    {
                        sb.Append("            if (collided)\n");
                        sb.Append("                VfxCollisionElasticity(particle.velocity, vfxHitN, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.CollisionLifetimeLoss)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount > 0f)
                    {
                        sb.Append("            if (collided)\n");
                        sb.Append("                VfxCollisionLifetimeLoss(particle.lifetime, particle.age, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.LimitVelocity)
                {
                    float speed = ResolveFloat(block, "Speed", 0f);
                    if (speed > 0f)
                    {
                        sb.Append("            VfxLimitVelocity(particle.velocity, ")
                            .Append(Format(speed))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.KillSphere)
                {
                    float radius = ResolveFloat(block, "Radius", 0f);
                    if (radius > 0f)
                    {
                        float cx = ResolveFloat(block, "X", 0f);
                        float cy = ResolveFloat(block, "Y", 0f);
                        float cz = ResolveFloat(block, "Z", 0f);
                        sb.Append("            VfxKillSphere(particle.alive, particle.position, float3(")
                            .Append(Format(cx))
                            .Append(", ")
                            .Append(Format(cy))
                            .Append(", ")
                            .Append(Format(cz))
                            .Append("), ")
                            .Append(Format(radius))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.KillAABox)
                {
                    float sx = ResolveFloat(block, "Sx", 0f);
                    float sy = ResolveFloat(block, "Sy", 0f);
                    float sz = ResolveFloat(block, "Sz", 0f);
                    if (sx > 0f && sy > 0f && sz > 0f)
                    {
                        float cx = ResolveFloat(block, "X", 0f);
                        float cy = ResolveFloat(block, "Y", 0f);
                        float cz = ResolveFloat(block, "Z", 0f);
                        sb.Append("            VfxKillAABox(particle.alive, particle.position, float3(")
                            .Append(Format(cx))
                            .Append(", ")
                            .Append(Format(cy))
                            .Append(", ")
                            .Append(Format(cz))
                            .Append("), float3(")
                            .Append(Format(sx))
                            .Append(", ")
                            .Append(Format(sy))
                            .Append(", ")
                            .Append(Format(sz))
                            .Append("));\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.KillPlane)
                {
                    float nx = ResolveFloat(block, "Nx", 0f);
                    float ny = ResolveFloat(block, "Ny", 0f);
                    float nz = ResolveFloat(block, "Nz", 0f);
                    if (nx * nx + ny * ny + nz * nz > 1e-10f)
                    {
                        float d = ResolveFloat(block, "Distance", 0f);
                        sb.Append("            VfxKillPlane(particle.alive, particle.position, float3(")
                            .Append(Format(nx))
                            .Append(", ")
                            .Append(Format(ny))
                            .Append(", ")
                            .Append(Format(nz))
                            .Append("), ")
                            .Append(Format(d))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.Attract)
                {
                    float strength = ResolveFloat(block, "Strength", 0f);
                    if (strength > 0f)
                    {
                        float tx = ResolveFloat(block, "X", 0f);
                        float ty = ResolveFloat(block, "Y", 0f);
                        float tz = ResolveFloat(block, "Z", 0f);
                        sb.Append("            VfxAttract(particle.velocity, particle.position, float3(")
                            .Append(Format(tx))
                            .Append(", ")
                            .Append(Format(ty))
                            .Append(", ")
                            .Append(Format(tz))
                            .Append("), ")
                            .Append(Format(strength))
                            .Append(", p.deltaTime);\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.ConformToSphere)
                {
                    float strength = ResolveFloat(block, "Strength", 0f);
                    if (strength > 0f)
                    {
                        float cx = ResolveFloat(block, "X", 0f);
                        float cy = ResolveFloat(block, "Y", 0f);
                        float cz = ResolveFloat(block, "Z", 0f);
                        float radius = ResolveFloat(block, "Radius", 0f);
                        sb.Append("            VfxConformToSphere(particle.velocity, particle.position, float3(")
                            .Append(Format(cx))
                            .Append(", ")
                            .Append(Format(cy))
                            .Append(", ")
                            .Append(Format(cz))
                            .Append("), ")
                            .Append(Format(radius))
                            .Append(", ")
                            .Append(Format(strength))
                            .Append(", p.deltaTime);\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.Vortex)
                {
                    float rotation = ResolveFloat(block, "Rotation", 0f);
                    float attraction = ResolveFloat(block, "Attraction", 0f);
                    float ax = ResolveFloat(block, "Ax", 0f);
                    float ay = ResolveFloat(block, "Ay", 0f);
                    float az = ResolveFloat(block, "Az", 0f);
                    if ((rotation != 0f || attraction != 0f) && ax * ax + ay * ay + az * az > 1e-10f)
                    {
                        float cx = ResolveFloat(block, "X", 0f);
                        float cy = ResolveFloat(block, "Y", 0f);
                        float cz = ResolveFloat(block, "Z", 0f);
                        sb.Append("            VfxVortex(particle.velocity, particle.position, float3(")
                            .Append(Format(cx))
                            .Append(", ")
                            .Append(Format(cy))
                            .Append(", ")
                            .Append(Format(cz))
                            .Append("), float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("), ")
                            .Append(Format(rotation))
                            .Append(", ")
                            .Append(Format(attraction))
                            .Append(", p.deltaTime);\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.ConformToCylinder)
                {
                    float strength = ResolveFloat(block, "Strength", 0f);
                    float ax = ResolveFloat(block, "Ax", 0f);
                    float ay = ResolveFloat(block, "Ay", 0f);
                    float az = ResolveFloat(block, "Az", 0f);
                    if (strength > 0f && ax * ax + ay * ay + az * az > 1e-10f)
                    {
                        float cx = ResolveFloat(block, "X", 0f);
                        float cy = ResolveFloat(block, "Y", 0f);
                        float cz = ResolveFloat(block, "Z", 0f);
                        float radius = ResolveFloat(block, "Radius", 0f);
                        sb.Append("            VfxConformToCylinder(particle.velocity, particle.position, float3(")
                            .Append(Format(cx))
                            .Append(", ")
                            .Append(Format(cy))
                            .Append(", ")
                            .Append(Format(cz))
                            .Append("), float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("), ")
                            .Append(Format(radius))
                            .Append(", ")
                            .Append(Format(strength))
                            .Append(", p.deltaTime);\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.MultiplySize)
                {
                    float amount = ResolveFloat(block, "Amount", 1f);
                    if (amount != 1f)
                    {
                        sb.Append("            VfxMultiplySize(particle.size, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.MultiplyColor)
                {
                    Rgba amount = ResolveColorFields(block, "", new Rgba(1f, 1f, 1f, 1f));
                    if (amount.R != 1f || amount.G != 1f || amount.B != 1f || amount.A != 1f)
                    {
                        sb.Append("            VfxMultiplyColor(particle.color, ")
                            .Append(HlslFloat4(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddSize)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxAddSize(particle.size, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddColor)
                {
                    Rgba amount = ResolveColorFields(block, "", new Rgba(0f, 0f, 0f, 0f));
                    if (amount.R != 0f || amount.G != 0f || amount.B != 0f || amount.A != 0f)
                    {
                        sb.Append("            VfxAddColor(particle.color, ")
                            .Append(HlslFloat4(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddVelocity)
                {
                    float ax = ResolveFloat(block, "X", 0f);
                    float ay = ResolveFloat(block, "Y", 0f);
                    float az = ResolveFloat(block, "Z", 0f);
                    if (ax != 0f || ay != 0f || az != 0f)
                    {
                        sb.Append("            VfxAddVelocity(particle.velocity, float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("));\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.MultiplyVelocity)
                {
                    float amount = ResolveFloat(block, "Amount", 1f);
                    if (amount != 1f)
                    {
                        sb.Append("            VfxMultiplyVelocity(particle.velocity, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.OverwriteSize)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxSetSize(particle.size, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.OverwriteColor)
                {
                    Rgba amount = ResolveColorFields(block, "", new Rgba(0f, 0f, 0f, 0f));
                    if (amount.R != 0f || amount.G != 0f || amount.B != 0f || amount.A != 0f)
                    {
                        sb.Append("            VfxSetColor(particle.color, ")
                            .Append(HlslFloat4(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.OverwriteVelocity)
                {
                    float ax = ResolveFloat(block, "X", 0f);
                    float ay = ResolveFloat(block, "Y", 0f);
                    float az = ResolveFloat(block, "Z", 0f);
                    if (ax != 0f || ay != 0f || az != 0f)
                    {
                        sb.Append("            VfxSetVelocity(particle.velocity, float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("));\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.OverwriteLifetime)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxSetLifetime(particle.lifetime, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddLifetime)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxAddLifetime(particle.lifetime, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.MultiplyLifetime)
                {
                    float amount = ResolveFloat(block, "Amount", 1f);
                    if (amount != 1f)
                    {
                        sb.Append("            VfxMultiplyLifetime(particle.lifetime, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddAge)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxAddAge(particle.age, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.MultiplyAge)
                {
                    float amount = ResolveFloat(block, "Amount", 1f);
                    if (amount != 1f)
                    {
                        sb.Append("            VfxMultiplyAge(particle.age, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.SetAge)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    if (amount != 0f)
                    {
                        sb.Append("            VfxSetAge(particle.age, ")
                            .Append(Format(amount))
                            .Append(");\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.AddPosition)
                {
                    float ax = ResolveFloat(block, "X", 0f);
                    float ay = ResolveFloat(block, "Y", 0f);
                    float az = ResolveFloat(block, "Z", 0f);
                    if (ax != 0f || ay != 0f || az != 0f)
                    {
                        sb.Append("            VfxAddPosition(particle.position, float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("));\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.SetPosition)
                {
                    float ax = ResolveFloat(block, "X", 0f);
                    float ay = ResolveFloat(block, "Y", 0f);
                    float az = ResolveFloat(block, "Z", 0f);
                    if (ax != 0f || ay != 0f || az != 0f)
                    {
                        sb.Append("            VfxSetPosition(particle.position, float3(")
                            .Append(Format(ax))
                            .Append(", ")
                            .Append(Format(ay))
                            .Append(", ")
                            .Append(Format(az))
                            .Append("));\n");
                    }
                }
                else if (block.Type == VfxNodeLibrary.LinearDrag)
                {
                    float amount = ResolveFloat(block, "Amount", 0f);
                    sb.Append("            VfxDrag(particle.velocity, ")
                        .Append(Format(amount))
                        .Append(", p.deltaTime);\n");
                }
                else if (block.Type == VfxNodeLibrary.Force)
                {
                    float fx = ResolveAxis(block, "Force", "X", 0f);
                    float fy = ResolveAxis(block, "Force", "Y", 0f);
                    float fz = ResolveAxis(block, "Force", "Z", 0f);
                    sb.Append("            VfxForce(particle.velocity, float3(")
                        .Append(Format(fx))
                        .Append(", ")
                        .Append(Format(fy))
                        .Append(", ")
                        .Append(Format(fz))
                        .Append("), p.deltaTime);\n");
                }
                else if (block.Type == VfxNodeLibrary.AngularVelocity)
                {
                    float rate = ResolveFloat(block, "Rate", 0f);
                    sb.Append("            VfxAngularVelocity(particle.pad0, ")
                        .Append(Format(rate))
                        .Append(", p.deltaTime);\n");
                }
                else if (block.Type == VfxNodeLibrary.CustomHLSL)
                {
                    string code = FieldString(block.Fields, "Code", "").Trim();
                    if (code.Length > 0)
                    {
                        sb.Append("            {\n                ");
                        sb.Append(code);
                        sb.Append("\n            }\n");
                    }
                }
            }
            return sb.ToString();
        }

        public string EmitOutputHlsl()
        {
            var sb = new StringBuilder();
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.ScreenSpaceSize)
                    continue;
                float amount = ResolveFloat(block, "Amount", 0f);
                if (amount <= 0f)
                    continue;
                sb.Append("            {\n");
                sb.Append("                float vfxScreenRef = ").Append(Format(amount)).Append(";\n");
                sb.Append("                particle.size = particle.size * (length(particle.position - _WorldSpaceCameraPos) / max(vfxScreenRef, 1e-5));\n");
                sb.Append("                next.size = next.size * (length(next.position - _WorldSpaceCameraPos) / max(vfxScreenRef, 1e-5));\n");
                sb.Append("            }\n");
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.OrientFixedAxis)
                    continue;
                float mode = ResolveFloat(block, "Mode", 0f);
                if (mode < 1.5f)
                    continue;
                float x = ResolveFloat(block, "X", 0f);
                float y = ResolveFloat(block, "Y", 1f);
                float z = ResolveFloat(block, "Z", 0f);
                sb.Append("            vfxOrientAlong = float3(")
                    .Append(Format(x))
                    .Append(", ")
                    .Append(Format(y))
                    .Append(", ")
                    .Append(Format(z))
                    .Append(");\n");
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.OrientLookAt)
                    continue;
                float mode = ResolveFloat(block, "Mode", 0f);
                if (mode < 1.5f)
                    continue;
                float x = ResolveFloat(block, "X", 0f);
                float y = ResolveFloat(block, "Y", 0f);
                float z = ResolveFloat(block, "Z", 0f);
                sb.Append("            VfxOrientLookAt(particle.position, float3(")
                    .Append(Format(x))
                    .Append(", ")
                    .Append(Format(y))
                    .Append(", ")
                    .Append(Format(z))
                    .Append("), vfxOrientAlong);\n");
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.FlipbookRate)
                    continue;
                float rate = ResolveFloat(block, "Rate", 0f);
                if (rate <= 0f)
                    continue;
                sb.Append("            vfxFlipbookRate = ")
                    .Append(Format(rate))
                    .Append(";\n");
            }
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.FlipbookStart)
                    continue;
                float start = ResolveFloat(block, "Frame", 0f);
                if (start == 0f)
                    continue;
                sb.Append("            vfxFlipbookStart = ")
                    .Append(Format(start))
                    .Append(";\n");
            }
            return sb.ToString();
        }

        public string EmitOutputPsHlsl()
        {
            var sb = new StringBuilder();
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.Type != VfxNodeLibrary.FlipbookMotion)
                    continue;
                float scale = ResolveFloat(block, "Scale", 0f);
                if (scale <= 0f)
                    continue;
                sb.Append("            vfxFlipbookMotionScale = ")
                    .Append(Format(scale))
                    .Append(";\n");
            }
            return sb.ToString();
        }

        private List<VfxBlock> OrderedBlocks(string contextType)
        {
            string contextId = "";
            foreach (VfxContext ctx in _contexts.Values)
            {
                if (ctx.Type == contextType)
                {
                    contextId = ctx.Id;
                    break;
                }
            }
            var list = new List<VfxBlock>();
            foreach (VfxBlock block in _blocks.Values)
            {
                if (block.ContextId == contextId)
                    list.Add(block);
            }
            list.Sort(static (a, b) => a.Order.CompareTo(b.Order));
            return list;
        }

        private void RequireContext(string type)
        {
            foreach (VfxContext ctx in _contexts.Values)
            {
                if (ctx.Type == type)
                    return;
            }
            throw new VfxGraphCompileException("VFX graph is missing a " + type + " context.");
        }

        private void RequireOutputContext()
        {
            foreach (VfxContext ctx in _contexts.Values)
            {
                if (VfxNodeLibrary.IsOutputContext(ctx.Type))
                    return;
            }
            throw new VfxGraphCompileException(
                "VFX graph is missing a " + VfxNodeLibrary.OutputParticleQuad
                + ", " + VfxNodeLibrary.OutputParticleMesh
                + ", " + VfxNodeLibrary.OutputParticleStrip
                + ", or " + VfxNodeLibrary.OutputParticleDecal + " context.");
        }

        private float ResolveFloat(VfxBlock block, string port, float fallback)
        {
            if (TryIncoming(block.Id, port, out string fromNode, out string fromPort)
                && TryEvalFloat(fromNode, fromPort, 0, out float wired))
                return wired;
            return FieldFloat(block.Fields, port, fallback);
        }

        private float ResolveAxis(VfxBlock block, string vectorPort, string axis, float fallback)
        {
            if (TryIncoming(block.Id, vectorPort, out string fromNode, out string fromPort)
                && TryEvalFloat(fromNode, fromPort == "Out" ? axis : fromPort, 0, out float wired))
                return wired;
            if (TryIncoming(block.Id, axis, out fromNode, out fromPort)
                && TryEvalFloat(fromNode, fromPort, 0, out wired))
                return wired;
            return FieldFloat(block.Fields, axis, fallback);
        }

        private Rgba ResolveColor(VfxBlock block, string port, Rgba fallback)
        {
            if (TryIncoming(block.Id, port, out string fromNode, out _)
                && _operators.TryGetValue(fromNode, out VfxOperator? op)
                && op.Type == VfxNodeLibrary.Color)
                return ColorFromFields(op.Fields, "", fallback);
            return ResolveColorFields(block, "", fallback);
        }

        private Rgba ResolveColorFields(VfxBlock block, string prefix, Rgba fallback)
            => ColorFromFields(block.Fields, prefix, fallback);

        private bool TryEvalFloat(string nodeId, string port, int depth, out float value)
        {
            value = 0f;
            if (depth > 16)
                return false;
            if (!_operators.TryGetValue(nodeId, out VfxOperator? op))
                return false;
            if (op.Type == VfxNodeLibrary.Float)
            {
                value = FieldFloat(op.Fields, "Value", 0f);
                return true;
            }
            if (op.Type == VfxNodeLibrary.Color)
            {
                Rgba c = ColorFromFields(op.Fields, "", new Rgba(1f, 1f, 1f, 1f));
                value = Channel(c, port);
                return true;
            }
            if (op.Type == VfxNodeLibrary.Add || op.Type == VfxNodeLibrary.Multiply)
            {
                float a = IncomingOrField(op, "A", depth + 1);
                float b = IncomingOrField(op, "B", depth + 1);
                value = op.Type == VfxNodeLibrary.Add ? a + b : a * b;
                return true;
            }
            if (op.Type == VfxNodeLibrary.Time || op.Type == VfxNodeLibrary.Random)
            {
                value = 0f;
                return true;
            }
            return false;
        }

        private float IncomingOrField(VfxOperator op, string port, int depth)
        {
            if (TryIncoming(op.Id, port, out string fromNode, out string fromPort)
                && TryEvalFloat(fromNode, fromPort, depth, out float wired))
                return wired;
            return FieldFloat(op.Fields, port, 0f);
        }

        private bool TryIncoming(string toNode, string toPort, out string fromNode, out string fromPort)
        {
            fromNode = "";
            fromPort = "";
            for (int i = 0; i < _edges.Count; i++)
            {
                VfxEdge edge = _edges[i];
                if (edge.ToNode == toNode && edge.ToPort == toPort)
                {
                    fromNode = edge.FromNode;
                    fromPort = edge.FromPort;
                    return true;
                }
            }
            return false;
        }

        private static Rgba ColorFromFields(List<VfxNodeField>? fields, string prefix, Rgba fallback)
        {
            return new Rgba(
                FieldFloat(fields, prefix + "R", fallback.R),
                FieldFloat(fields, prefix + "G", fallback.G),
                FieldFloat(fields, prefix + "B", fallback.B),
                FieldFloat(fields, prefix + "A", fallback.A));
        }

        private static float FieldFloat(List<VfxNodeField>? fields, string key, float fallback)
        {
            if (fields == null)
                return fallback;
            for (int i = 0; i < fields.Count; i++)
            {
                if (!string.Equals(fields[i].Key, key, StringComparison.Ordinal))
                    continue;
                if (float.TryParse(fields[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                    return parsed;
            }
            return fallback;
        }

        private static string FieldString(List<VfxNodeField>? fields, string key, string fallback)
        {
            if (fields == null)
                return fallback;
            for (int i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i].Key, key, StringComparison.Ordinal))
                    return fields[i].Value ?? fallback;
            }
            return fallback;
        }

        private static float Channel(Rgba c, string port)
        {
            if (port == "G") return c.G;
            if (port == "B") return c.B;
            if (port == "A") return c.A;
            return c.R;
        }

        private static string HlslFloat4(Rgba c)
            => "float4(" + Format(c.R) + ", " + Format(c.G) + ", " + Format(c.B) + ", " + Format(c.A) + ")";
    }
}

/// <summary>CPU-side playback knobs lowered from graph lists (no engine Vector types).</summary>
public sealed class VfxGraphLoweredSettings
{
    public int Capacity = 4096;
    public float SpawnRate;
    public float BurstCount;
    public float SpawnOverDistance;
    public float PeriodicBurstCount;
    public float PeriodicBurstPeriod = 1f;
    public float Lifetime = 2f;
    public float StartSpeed = 2f;
    public float StartSize = 0.15f;
    public float GravityX;
    public float GravityY;
    public float GravityZ;
    public float ColorR = 1f;
    public float ColorG = 1f;
    public float ColorB = 1f;
    public float ColorA = 1f;
    public float FlipbookColumns = 1f;
    public float FlipbookRows = 1f;
    public float CollisionMode;
    public float CollisionBounce = 0.4f;
    public float CollisionNx;
    public float CollisionNy = 1f;
    public float CollisionNz;
    public float CollisionD;
    public float SdfCollisionMode;
    public float SdfShape;
    public float SdfCx;
    public float SdfCy = 0.55f;
    public float SdfCz;
    public float SdfSx = 0.4f;
    public float SdfSy = 0.4f;
    public float SdfSz = 0.4f;
    public float TurbulenceIntensity;
    public float TurbulenceFrequency = 1f;
    public bool OutputMesh;
    public bool OutputStrip;
    public bool OutputDecal;
    public int SubgraphCount;
    public float LitMode;
    public float LightDx = 0.35f;
    public float LightDy = 0.85f;
    public float LightDz = 0.4f;
    public float LightR = 1f;
    public float LightG = 1f;
    public float LightB = 1f;
    public float LightIntensity = 1f;
    public float AmbientR = 0.18f;
    public float AmbientG = 0.18f;
    public float AmbientB = 0.22f;
    public float Shininess = 32f;
    public float SoftParticleFade;
    public float EventKind;
    public float EventCount;
    public float EventTarget;
    public string EventTargetName = "";
    public float DepthCollisionMode;
    public float ConformAttraction;
    public float VectorFieldIntensity;
    public float AttributeMapMode;
    public float AttributeMapScale = 1f;
    public float DistortionIntensity;
    public float ParticleLightIntensity;
    public float ParticleLightRange = 1.5f;
    public float ParticleLightCount;
    public float SortMode;
    public float CameraFadeNear;
    public float CameraFadeFar;
    public float FlipbookBlend;
    public float FlipbookMotionScale;
    public float MotionVectorMode;
    public float MotionVectorScale = 1f;
    public float OrientMode;
    public float PositionShapeMode;
    public float PositionShapeSx = 0.25f;
    public float PositionShapeSy = 0.25f;
    public float PositionShapeSz = 0.25f;
    public float SequentialMode;
    public float SequentialSx = 0.5f;
    public float SequentialSy = 0.5f;
    public float SequentialSz = 8f;
    public float InheritVelocityAmount;
    public float TriggerEventRate;
    public float TriggerEventOverDistance;
    public float TriggerEventOnceCount;
    public float ColorOverLife;
    public float SizeOverLifeFrom = 1f;
    public float SizeOverLifeTo = 1f;
    public float SizeOverSpeedAmount;
    public float MultiplySizeAmount = 1f;
    public float MultiplyColorR = 1f;
    public float MultiplyColorG = 1f;
    public float MultiplyColorB = 1f;
    public float MultiplyColorA = 1f;
    public float AddSizeAmount;
    public float AddColorR;
    public float AddColorG;
    public float AddColorB;
    public float AddColorA;
    public float AddVelocityX;
    public float AddVelocityY;
    public float AddVelocityZ;
    public float MultiplyVelocityAmount = 1f;
    public float OverwriteSizeAmount;
    public float OverwriteColorR;
    public float OverwriteColorG;
    public float OverwriteColorB;
    public float OverwriteColorA;
    public float OverwriteVelocityX;
    public float OverwriteVelocityY;
    public float OverwriteVelocityZ;
    public float OverwriteLifetimeAmount;
    public float AddLifetimeAmount;
    public float MultiplyLifetimeAmount = 1f;
    public float AddAgeAmount;
    public float MultiplyAgeAmount = 1f;
    public float SetAgeAmount;
    public float AddPositionX;
    public float AddPositionY;
    public float AddPositionZ;
    public float SetPositionX;
    public float SetPositionY;
    public float SetPositionZ;
    public float ColorOverSpeedScale;
    public float ScreenSpaceSizeAmount;
    public float CollisionFrictionAmount;
    public float CollisionElasticityAmount;
    public float CollisionLifetimeLossAmount;
    public float LimitVelocitySpeed;
    public float KillSphereX;
    public float KillSphereY;
    public float KillSphereZ;
    public float KillSphereRadius;
    public float KillAABoxX;
    public float KillAABoxY;
    public float KillAABoxZ;
    public float KillAABoxSx;
    public float KillAABoxSy;
    public float KillAABoxSz;
    public float AttractX;
    public float AttractY;
    public float AttractZ;
    public float AttractStrength;
    public float ConformSphereX;
    public float ConformSphereY;
    public float ConformSphereZ;
    public float ConformSphereRadius;
    public float ConformSphereStrength;
    public float VortexX;
    public float VortexY;
    public float VortexZ;
    public float VortexAx;
    public float VortexAy = 1f;
    public float VortexAz;
    public float VortexRotation;
    public float VortexAttraction;
    public float ConformCylinderX;
    public float ConformCylinderY;
    public float ConformCylinderZ;
    public float ConformCylinderAx;
    public float ConformCylinderAy = 1f;
    public float ConformCylinderAz;
    public float ConformCylinderRadius = 1f;
    public float ConformCylinderStrength;
    public float KillPlaneNx;
    public float KillPlaneNy;
    public float KillPlaneNz;
    public float KillPlaneDistance;
    public float DragAmount;
    public float ForceX;
    public float ForceY;
    public float ForceZ;
    public float AngularVelocityRate;
    public float CustomHlsl;
    public float FixedAxisMode;
    public float FixedAxisX;
    public float FixedAxisY = 1f;
    public float FixedAxisZ;
    public float LookAtMode;
    public float LookAtX;
    public float LookAtY;
    public float LookAtZ;
    public float FlipbookRate;
    public float FlipbookStart;
}
