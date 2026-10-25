// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XEngine.Runtime.Rendering;

public sealed class ShaderGraphCompileException : Exception
{
    public ShaderGraphCompileException(string message) : base(message) { }
}

/// <summary>
/// Engine-agnostic Shader Graph → XEngine Shader DSL emitter (GLSLPROGRAM + SLANGPROGRAM).
/// Linked into xengine-cli so <c>shadergraph_compile</c> does not reference XEngine.Runtime.
/// Lit target uses LightBVH includes from <see cref="ShaderLibrary"/>.
/// </summary>
public static class ShaderGraphSourceCompiler
{
    private enum Dialect { Glsl, Slang }

    public static string CompileToSource(
        string shaderName,
        ShaderGraphTargetKind target,
        List<ShaderGraphProperty>? properties,
        List<ShaderGraphNode>? nodes,
        List<ShaderGraphEdge>? edges)
    {
        var state = new State(
            target,
            properties ?? new List<ShaderGraphProperty>(),
            nodes ?? new List<ShaderGraphNode>(),
            edges ?? new List<ShaderGraphEdge>());
        state.BuildLookups();
        ShaderGraphNode master = state.FindMaster();
        state.InVertex = true;
        string posGlsl = state.EmitPort(master, "Position", Dialect.Glsl, "vertexPosition");
        string posSlang = state.EmitPort(master, "Position", Dialect.Slang, "input.vertexPosition");
        state.InVertex = false;
        string albedoGlsl = target == ShaderGraphTargetKind.Fullscreen
            ? state.EmitPort(master, "Color", Dialect.Glsl, "vec4(1.0, 1.0, 1.0, 1.0)")
            : state.EmitPort(master, "Albedo", Dialect.Glsl, "vec4(1.0, 1.0, 1.0, 1.0)");
        string albedoSlang = target == ShaderGraphTargetKind.Fullscreen
            ? state.EmitPort(master, "Color", Dialect.Slang, "float4(1.0, 1.0, 1.0, 1.0)")
            : state.EmitPort(master, "Albedo", Dialect.Slang, "float4(1.0, 1.0, 1.0, 1.0)");
        string alphaGlsl = state.EmitPort(master, "Alpha", Dialect.Glsl, "albedo.a");
        string alphaSlang = state.EmitPort(master, "Alpha", Dialect.Slang, "albedo.a");
        string normalGlsl = state.EmitPort(master, "Normal", Dialect.Glsl, "vNormal");
        string normalSlang = state.EmitPort(master, "Normal", Dialect.Slang, "input.vNormal");
        string emissionGlsl = state.EmitPort(master, "Emission", Dialect.Glsl, "vec3(0.0, 0.0, 0.0)");
        string emissionSlang = state.EmitPort(master, "Emission", Dialect.Slang, "float3(0.0, 0.0, 0.0)");
        string metallicGlsl = state.EmitPort(master, "Metallic", Dialect.Glsl, "0.0");
        string metallicSlang = state.EmitPort(master, "Metallic", Dialect.Slang, "0.0");
        string smoothnessGlsl = state.EmitPort(master, "Smoothness", Dialect.Glsl, "0.5");
        string smoothnessSlang = state.EmitPort(master, "Smoothness", Dialect.Slang, "0.5");
        string aoGlsl = state.EmitPort(master, "Occlusion", Dialect.Glsl, "1.0");
        string aoSlang = state.EmitPort(master, "Occlusion", Dialect.Slang, "1.0");

        var sb = new StringBuilder(4096);
        sb.Append("Shader \"").Append(EscapeName(shaderName)).Append("\"\n\n");
        EmitProperties(sb, state);
        if (target == ShaderGraphTargetKind.Lit)
            EmitLitPass(sb, state, posGlsl, posSlang, albedoGlsl, albedoSlang, alphaGlsl, alphaSlang,
                normalGlsl, normalSlang, emissionGlsl, emissionSlang,
                metallicGlsl, metallicSlang, smoothnessGlsl, smoothnessSlang, aoGlsl, aoSlang);
        else if (target == ShaderGraphTargetKind.Decal)
            EmitTaggedUnlitPass(sb, state, "Decal", "Decal", posGlsl, posSlang, albedoGlsl, albedoSlang, alphaGlsl, alphaSlang);
        else if (target == ShaderGraphTargetKind.Fullscreen)
            EmitTaggedUnlitPass(sb, state, "Fullscreen", "Fullscreen", posGlsl, posSlang, albedoGlsl, albedoSlang, alphaGlsl, alphaSlang);
        else
            EmitUnlitPass(sb, state, posGlsl, posSlang, albedoGlsl, albedoSlang, alphaGlsl, alphaSlang);
        return sb.ToString();
    }

    private static void EmitProperties(StringBuilder sb, State state)
    {
        sb.Append("Properties\n{\n");
        for (int i = 0; i < state.Properties.Count; i++)
        {
            ShaderGraphProperty p = state.Properties[i];
            string display = string.IsNullOrEmpty(p.DisplayName) ? p.Name : p.DisplayName;
            switch (p.Type)
            {
                case ShaderGraphValueType.Texture2D:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Texture2D) = \"")
                        .Append(string.IsNullOrEmpty(p.DefaultValue) ? "white" : p.DefaultValue).Append("\"\n");
                    break;
                case ShaderGraphValueType.Color:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Color) = ")
                        .Append(ColorDefault(p.DefaultValue)).Append('\n');
                    break;
                case ShaderGraphValueType.Float2:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Vector2) = (0.0, 0.0)\n");
                    break;
                case ShaderGraphValueType.Float3:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Vector3) = (0.0, 0.0, 0.0)\n");
                    break;
                case ShaderGraphValueType.Float4:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Vector4) = (0.0, 0.0, 0.0, 0.0)\n");
                    break;
                default:
                    sb.Append("    ").Append(p.Name).Append(" (\"").Append(display).Append("\", Float) = ")
                        .Append(string.IsNullOrEmpty(p.DefaultValue) ? "0.0" : p.DefaultValue).Append('\n');
                    break;
            }
        }
        sb.Append("}\n\n");
    }

    private static void EmitUnlitPass(StringBuilder sb, State state,
        string posGlsl, string posSlang, string albedoGlsl, string albedoSlang,
        string alphaGlsl, string alphaSlang)
        => EmitTaggedUnlitPass(sb, state, "Unlit", lightMode: null, posGlsl, posSlang, albedoGlsl, albedoSlang, alphaGlsl, alphaSlang);

    private static void EmitTaggedUnlitPass(StringBuilder sb, State state,
        string passName, string? lightMode,
        string posGlsl, string posSlang, string albedoGlsl, string albedoSlang,
        string alphaGlsl, string alphaSlang)
    {
        sb.Append("Pass \"").Append(passName).Append("\"\n{\n    Tags { \"RenderOrder\" = \"Opaque\"");
        if (!string.IsNullOrEmpty(lightMode))
            sb.Append(", \"LightMode\" = \"").Append(lightMode).Append('"');
        sb.Append(" }\n    Cull Back\n\n");
        sb.Append("\tGLSLPROGRAM\n\t\tVertex\n\t\t{\n            #include \"").Append(ShaderLibrary.Core).Append("\"\n            #include \"").Append(ShaderLibrary.VertexAttributes).Append("\"\n\n");
        sb.Append("\t\t\tout vec2 texCoord0;\n\t\t\tout vec3 worldPos;\n\t\t\tout vec4 vColor;\n\t\t\tout vec3 vNormal;\n\n");
        sb.Append("\t\t\tvoid main()\n\t\t\t{\n");
        sb.Append("\t\t\t\tvec3 vertPos = ").Append(posGlsl).Append(";\n");
        sb.Append("\t\t\t\tgl_Position = TransformClip(vertPos);\n");
        sb.Append("\t\t\t\ttexCoord0 = vertexTexCoord0;\n\t\t\t\tworldPos = TransformPosition(vertPos);\n");
        sb.Append("\t\t\t\tvColor = GetInstanceColor();\n\t\t\t\tvNormal = TransformDirection(GetMorphedNormal(vertexNormal));\n\t\t\t}\n\t\t}\n\n\t\tFragment\n\t\t{\n");
        sb.Append("            #include \"").Append(ShaderLibrary.Core).Append("\"\n            #include \"").Append(ShaderLibrary.Lighting).Append("\"\n\n");
        sb.Append("\t\t\tlayout (location = 0) out vec4 fragColor;\n\n");
        sb.Append("\t\t\tin vec2 texCoord0;\n\t\t\tin vec3 worldPos;\n\t\t\tin vec4 vColor;\n\t\t\tin vec3 vNormal;\n\n");
        EmitGlslUniforms(sb, state);
        sb.Append("\t\t\tvoid main()\n\t\t\t{\n");
        sb.Append("\t\t\t\tvec4 albedo = ").Append(albedoGlsl).Append(" * vColor;\n");
        sb.Append("\t\t\t\tvec3 baseColor = gammaToLinearSpace(albedo.rgb);\n");
        sb.Append("\t\t\t\tbaseColor = ApplyFog(baseColor, worldPos);\n");
        sb.Append("\t\t\t\tfloat alpha = ").Append(alphaGlsl).Append(";\n");
        sb.Append("\t\t\t\tfragColor = vec4(baseColor, alpha);\n\t\t\t}\n\t\t}\n\tENDGLSL\n\n");

        sb.Append("\tSLANGPROGRAM\n\t\tVertex\n\t\t{\n\t\t\t#include \"").Append(ShaderLibrary.Core).Append("\"\n\n");
        EmitSlangCbuffer(sb, state, "UnlitMaterial");
        EmitSlangVertexInput(sb);
        sb.Append("\t\t\tstruct VSOutput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; float3 worldPos : TEXCOORD1; float4 vColor : COLOR0; float3 vNormal : TEXCOORD2; };\n");
        sb.Append("\t\t\tVSOutput main(VSInput input) { VSOutput o; float3 vertPos = ").Append(posSlang).Append(";\n");
        EmitSlangVertexDeform(sb);
        sb.Append(" o.position = TransformClip(vertPos); o.texCoord0 = input.vertexTexCoord0; o.worldPos = TransformPosition(vertPos); o.vColor = GetInstanceColor();\n");
        EmitSlangNormalDeform(sb);
        sb.Append("\t\tFragment\n\t\t{\n\t\t\t#include \"").Append(ShaderLibrary.Core).Append("\"\n\n");
        EmitSlangCbuffer(sb, state, "UnlitMaterial");
        EmitSlangTextures(sb, state);
        sb.Append("\t\t\tstruct PSInput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; float3 worldPos : TEXCOORD1; float4 vColor : COLOR0; float3 vNormal : TEXCOORD2; };\n");
        sb.Append("\t\t\tfloat4 main(PSInput input) : SV_Target\n\t\t\t{\n");
        sb.Append("\t\t\t\tfloat4 albedo = ").Append(albedoSlang).Append(" * input.vColor;\n");
        sb.Append("\t\t\t\tfloat3 baseColor = gammaToLinearSpace(albedo.rgb);\n");
        sb.Append("\t\t\t\tbaseColor = ApplyFog(baseColor, input.worldPos);\n");
        sb.Append("\t\t\t\tfloat alpha = ").Append(alphaSlang).Append(";\n");
        sb.Append("\t\t\t\treturn float4(baseColor, alpha);\n\t\t\t}\n\t\t}\n\tENDSLANG\n}\n");
    }

    private static void EmitLitPass(StringBuilder sb, State state,
        string posGlsl, string posSlang,
        string albedoGlsl, string albedoSlang, string alphaGlsl, string alphaSlang,
        string normalGlsl, string normalSlang,
        string emissionGlsl, string emissionSlang,
        string metallicGlsl, string metallicSlang,
        string smoothnessGlsl, string smoothnessSlang,
        string aoGlsl, string aoSlang)
    {
        sb.Append("Pass \"Lit\"\n{\n    Tags { \"RenderOrder\" = \"Opaque\" }\n    Cull Back\n\n");
        sb.Append("\tGLSLPROGRAM\n\t\tVertex\n\t\t{\n            #include \"").Append(ShaderLibrary.Core).Append("\"\n            #include \"").Append(ShaderLibrary.VertexAttributes).Append("\"\n\n");
        sb.Append("\t\t\tout vec2 texCoord0;\n\t\t\tout vec3 worldPos;\n\t\t\tout vec4 vColor;\n\t\t\tout vec3 vNormal;\n\n");
        sb.Append("\t\t\tvoid main()\n\t\t\t{\n");
        sb.Append("\t\t\t\tvec3 vertPos = ").Append(posGlsl).Append(";\n");
        sb.Append("\t\t\t\tgl_Position = TransformClip(vertPos);\n");
        sb.Append("\t\t\t\ttexCoord0 = vertexTexCoord0;\n\t\t\t\tworldPos = TransformPosition(vertPos);\n");
        sb.Append("\t\t\t\tvColor = GetInstanceColor();\n\t\t\t\tvNormal = TransformDirection(GetMorphedNormal(vertexNormal));\n\t\t\t}\n\t\t}\n\n");
        sb.Append("\t\tFragment\n\t\t{\n            #include \"").Append(ShaderLibrary.Core).Append("\"\n            #include \"").Append(ShaderLibrary.Lighting).Append("\"\n            #include \"ShaderLibrary/GiProbes\"\n\n");
        sb.Append("\t\t\tuniform int _GIMode;\n");
        sb.Append("\t\t\tlayout (location = 0) out vec4 fragColor;\n\n");
        sb.Append("\t\t\tin vec2 texCoord0;\n\t\t\tin vec3 worldPos;\n\t\t\tin vec4 vColor;\n\t\t\tin vec3 vNormal;\n\n");
        EmitGlslUniforms(sb, state);
        sb.Append("\t\t\tvoid main()\n\t\t\t{\n");
        sb.Append("\t\t\t\tvec4 albedo = ").Append(albedoGlsl).Append(" * vColor;\n");
        sb.Append("\t\t\t\tvec3 baseColor = gammaToLinearSpace(albedo.rgb);\n");
        sb.Append("\t\t\t\tvec3 worldNormal = normalize(").Append(normalGlsl).Append(");\n");
        sb.Append("\t\t\t\tvec3 emission = ").Append(emissionGlsl).Append(";\n");
        sb.Append("\t\t\t\tfloat metallic = ").Append(metallicGlsl).Append(";\n");
        sb.Append("\t\t\t\tfloat roughness = (1.0 - (").Append(smoothnessGlsl).Append("));\n");
        sb.Append("\t\t\t\tfloat ao = ").Append(aoGlsl).Append(";\n");
        sb.Append("\t\t\t\tvec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);\n");
        sb.Append("\t\t\t\tvec3 lighting = CalculateForwardLighting(worldPos, worldNormal, viewDir, baseColor, metallic, roughness, ao);\n");
        sb.Append("\t\t\t\tvec3 ambientTerm = (_GIMode == 3)\n");
        sb.Append("\t\t\t\t\t? max(SampleGiProbes(worldPos, worldNormal), CalculateAmbient(worldNormal) * 0.25)\n");
        sb.Append("\t\t\t\t\t: CalculateAmbient(worldNormal);\n");
        sb.Append("\t\t\t\tvec3 ambient = ambientTerm * baseColor;\n");
        sb.Append("\t\t\t\tvec3 color = ApplyFog(ambient + lighting + emission, worldPos);\n");
        sb.Append("\t\t\t\tfloat alpha = ").Append(alphaGlsl).Append(";\n");
        sb.Append("\t\t\t\tfragColor = vec4(color, alpha);\n\t\t\t}\n\t\t}\n\tENDGLSL\n\n");

        sb.Append("\tSLANGPROGRAM\n\t\tVertex\n\t\t{\n\t\t\t#include \"").Append(ShaderLibrary.Core).Append("\"\n\n");
        EmitSlangCbuffer(sb, state, "LitMaterial");
        EmitSlangVertexInput(sb);
        sb.Append("\t\t\tstruct VSOutput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; float3 worldPos : TEXCOORD1; float4 vColor : COLOR0; float3 vNormal : TEXCOORD2; };\n");
        sb.Append("\t\t\tVSOutput main(VSInput input) { VSOutput o; float3 vertPos = ").Append(posSlang).Append(";\n");
        EmitSlangVertexDeform(sb);
        sb.Append(" o.position = TransformClip(vertPos); o.texCoord0 = input.vertexTexCoord0; o.worldPos = TransformPosition(vertPos); o.vColor = GetInstanceColor();\n");
        EmitSlangNormalDeform(sb);
        sb.Append("\t\tFragment\n\t\t{\n\t\t\t#include \"").Append(ShaderLibrary.Core).Append("\"\n\t\t\t#include \"ShaderLibrary/GiProbes\"\n\t\t\t#include \"").Append(ShaderLibrary.StandardLighting).Append("\"\n\n");
        EmitSlangCbuffer(sb, state, "LitMaterial");
        EmitSlangTextures(sb, state);
        sb.Append("\t\t\tstruct PSInput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; float3 worldPos : TEXCOORD1; float4 vColor : COLOR0; float3 vNormal : TEXCOORD2; };\n");
        sb.Append("\t\t\tfloat4 main(PSInput input) : SV_Target\n\t\t\t{\n");
        sb.Append("\t\t\t\tfloat4 albedo = ").Append(albedoSlang).Append(" * input.vColor;\n");
        sb.Append("\t\t\t\tfloat3 baseColor = gammaToLinearSpace(albedo.rgb);\n");
        sb.Append("\t\t\t\tfloat3 worldNormal = normalize(").Append(normalSlang).Append(");\n");
        sb.Append("\t\t\t\tfloat3 emission = ").Append(emissionSlang).Append(";\n");
        sb.Append("\t\t\t\tfloat metallic = ").Append(metallicSlang).Append(";\n");
        sb.Append("\t\t\t\tfloat roughness = (1.0 - (").Append(smoothnessSlang).Append("));\n");
        sb.Append("\t\t\t\tfloat ao = ").Append(aoSlang).Append(";\n");
        sb.Append("\t\t\t\tfloat3 viewDir = normalize(_WorldSpaceCameraPos.xyz - input.worldPos);\n");
        sb.Append("\t\t\t\tfloat3 lighting = StandardCalculateLighting(input.worldPos, worldNormal, viewDir, input.position.xy, baseColor, metallic, roughness, ao, 0.0, 0.0, 0.0, 0.0);\n");
        sb.Append("\t\t\t\tfloat3 ambientTerm = (_GIMode == 3)\n");
        sb.Append("\t\t\t\t\t? max(SampleGiProbes(input.worldPos, worldNormal), StandardCalculateAmbient(worldNormal) * 0.25)\n");
        sb.Append("\t\t\t\t\t: StandardCalculateAmbient(worldNormal);\n");
        sb.Append("\t\t\t\tfloat3 ambient = ambientTerm * baseColor;\n");
        sb.Append("\t\t\t\tfloat3 color = ApplyFog(ambient + lighting + emission, input.worldPos);\n");
        sb.Append("\t\t\t\tfloat alpha = ").Append(alphaSlang).Append(";\n");
        sb.Append("\t\t\t\treturn float4(color, alpha);\n\t\t\t}\n\t\t}\n\tENDSLANG\n}\n");
    }

    /// <summary>
    /// ★ N6 — Slang vertex input with skin/morph attributes (GLSL graphs get them for free via
    /// the VertexAttributes include). Bone streams are keyword-gated so meshes without skin data
    /// never declare unbound attributes (VUID-07904 / D3D12 unbound-input UB).
    /// </summary>
    private static void EmitSlangVertexInput(StringBuilder sb)
    {
        sb.Append("\t\t\tstruct VSInput { float3 vertexPosition : POSITION; uint vertexId : SV_VertexID; float3 vertexNormal : NORMAL; float2 vertexTexCoord0 : TEXCOORD0;\n");
        sb.Append("\t\t\t#ifdef HAS_BONEINDICES\n\t\t\tfloat4 vertexBoneIndices : BLENDINDICES;\n\t\t\t#endif\n");
        sb.Append("\t\t\t#ifdef HAS_BONEWEIGHTS\n\t\t\tfloat4 vertexBoneWeights : BLENDWEIGHT;\n\t\t\t#endif\n\t\t\t};\n");
    }

    /// <summary>Emits the morph → skin deform block applied to <c>vertPos</c> (morph first, mirroring the GLSL TransformClip order).</summary>
    private static void EmitSlangVertexDeform(StringBuilder sb)
    {
        sb.Append("\t\t\t\tvertPos = GetMorphedPosition(vertPos, input.vertexId);\n");
        sb.Append("\t\t\t\t#if defined(SKINNED) && defined(HAS_BONEINDICES) && defined(HAS_BONEWEIGHTS)\n");
        sb.Append("\t\t\t\tvertPos = XSkinPosition(vertPos, input.vertexBoneIndices, input.vertexBoneWeights).xyz;\n");
        sb.Append("\t\t\t\t#endif\n");
    }

    /// <summary>Emits the normal morph → skin block assigning <c>o.vNormal</c>.</summary>
    private static void EmitSlangNormalDeform(StringBuilder sb)
    {
        sb.Append("\t\t\t\tfloat3 nDeformed = GetMorphedNormal(input.vertexNormal, input.vertexId);\n");
        sb.Append("\t\t\t\t#if defined(SKINNED) && defined(HAS_BONEINDICES) && defined(HAS_BONEWEIGHTS)\n");
        sb.Append("\t\t\t\tnDeformed = XSkinNormal(nDeformed, input.vertexBoneIndices, input.vertexBoneWeights);\n");
        sb.Append("\t\t\t\t#endif\n");
        sb.Append("\t\t\t\to.vNormal = TransformDirection(nDeformed); return o; }\n\t\t}\n\n");
    }

    private static void EmitGlslUniforms(StringBuilder sb, State state)
    {
        for (int i = 0; i < state.Properties.Count; i++)
        {
            ShaderGraphProperty p = state.Properties[i];
            switch (p.Type)
            {
                case ShaderGraphValueType.Texture2D:
                    sb.Append("\t\t\tuniform sampler2D ").Append(p.Name).Append(";\n");
                    break;
                case ShaderGraphValueType.Color:
                case ShaderGraphValueType.Float4:
                    sb.Append("\t\t\tuniform vec4 ").Append(p.Name).Append(";\n");
                    break;
                case ShaderGraphValueType.Float3:
                    sb.Append("\t\t\tuniform vec3 ").Append(p.Name).Append(";\n");
                    break;
                case ShaderGraphValueType.Float2:
                    sb.Append("\t\t\tuniform vec2 ").Append(p.Name).Append(";\n");
                    break;
                default:
                    sb.Append("\t\t\tuniform float ").Append(p.Name).Append(";\n");
                    break;
            }
        }
        sb.Append('\n');
    }

    private static void EmitSlangCbuffer(StringBuilder sb, State state, string name)
    {
        bool any = false;
        for (int i = 0; i < state.Properties.Count; i++)
        {
            if (state.Properties[i].Type != ShaderGraphValueType.Texture2D)
                any = true;
        }
        if (!any)
            return;
        sb.Append("\t\t\tcbuffer ").Append(name).Append(" : register(b2)\n\t\t\t{\n");
        for (int i = 0; i < state.Properties.Count; i++)
        {
            ShaderGraphProperty p = state.Properties[i];
            if (p.Type == ShaderGraphValueType.Texture2D)
                continue;
            string ty = p.Type switch
            {
                ShaderGraphValueType.Color or ShaderGraphValueType.Float4 => "float4",
                ShaderGraphValueType.Float3 => "float3",
                ShaderGraphValueType.Float2 => "float2",
                _ => "float",
            };
            sb.Append("\t\t\t\t").Append(ty).Append(' ').Append(p.Name).Append(";\n");
        }
        sb.Append("\t\t\t};\n\n");
    }

    private static void EmitSlangTextures(StringBuilder sb, State state)
    {
        int slot = 0;
        for (int i = 0; i < state.Properties.Count; i++)
        {
            ShaderGraphProperty p = state.Properties[i];
            if (p.Type != ShaderGraphValueType.Texture2D)
                continue;
            sb.Append("\t\t\tTexture2D ").Append(p.Name).Append(" : register(t").Append(slot).Append(");\n");
            sb.Append("\t\t\tSamplerState ").Append(p.Name).Append("Sampler : register(s").Append(slot).Append(");\n");
            slot++;
        }
        if (slot > 0)
            sb.Append('\n');
    }

    private static string EscapeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Graph/Unnamed";
        return name.Replace("\"", "'", StringComparison.Ordinal);
    }

    private static string ColorDefault(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "(1.0, 1.0, 1.0, 1.0)";
        string[] parts = raw.Split(',');
        if (parts.Length >= 4)
            return "(" + parts[0].Trim() + ", " + parts[1].Trim() + ", " + parts[2].Trim() + ", " + parts[3].Trim() + ")";
        return "(1.0, 1.0, 1.0, 1.0)";
    }

    private sealed class State
    {
        public readonly ShaderGraphTargetKind Target;
        public readonly List<ShaderGraphProperty> Properties;
        public bool InVertex;
        private readonly List<ShaderGraphNode> _nodeList;
        private readonly Dictionary<string, ShaderGraphNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderGraphProperty> _props = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ShaderGraphProperty> _propsByName = new(StringComparer.Ordinal);
        private readonly List<ShaderGraphEdge> _edges;
        private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

        public State(
            ShaderGraphTargetKind target,
            List<ShaderGraphProperty> properties,
            List<ShaderGraphNode> nodes,
            List<ShaderGraphEdge> edges)
        {
            Target = target;
            Properties = properties;
            _nodeList = nodes;
            _edges = edges;
        }

        public void BuildLookups()
        {
            List<ShaderGraphNode> nodes = _nodeList;
            for (int i = 0; i < nodes.Count; i++)
            {
                ShaderGraphNode n = nodes[i];
                if (n == null || string.IsNullOrEmpty(n.Id))
                    throw new ShaderGraphCompileException("Node is missing Id.");
                if (!_nodes.TryAdd(n.Id, n))
                    throw new ShaderGraphCompileException("Duplicate node id: " + n.Id);
            }
            for (int i = 0; i < Properties.Count; i++)
            {
                ShaderGraphProperty p = Properties[i];
                if (p == null || string.IsNullOrEmpty(p.Id) || string.IsNullOrEmpty(p.Name))
                    throw new ShaderGraphCompileException("Property is missing Id or Name.");
                if (!_props.TryAdd(p.Id, p))
                    throw new ShaderGraphCompileException("Duplicate property id: " + p.Id);
                _propsByName[p.Name] = p;
            }
        }

        public ShaderGraphNode FindMaster()
        {
            string want = Target switch
            {
                ShaderGraphTargetKind.Lit => ShaderGraphNodeLibrary.MasterLit,
                ShaderGraphTargetKind.Decal => ShaderGraphNodeLibrary.MasterDecal,
                ShaderGraphTargetKind.Fullscreen => ShaderGraphNodeLibrary.MasterFullscreen,
                _ => ShaderGraphNodeLibrary.MasterUnlit,
            };
            foreach (KeyValuePair<string, ShaderGraphNode> kv in _nodes)
            {
                if (string.Equals(kv.Value.Type, want, StringComparison.Ordinal))
                    return kv.Value;
            }
            throw new ShaderGraphCompileException("Graph has no " + want + " node.");
        }

        public string EmitPort(ShaderGraphNode node, string port, Dialect dialect, string fallback)
        {
            if (!TryFindIncoming(node.Id, port, out ShaderGraphEdge edge))
                return fallback;
            return EmitNodeOutput(edge.FromNode, edge.FromPort, dialect);
        }

        private string EmitNodeOutput(string nodeId, string port, Dialect dialect)
        {
            string key = nodeId + ":" + port + ":" + (int)dialect + (InVertex ? ":v" : ":f");
            if (!_visiting.Add(key))
                throw new ShaderGraphCompileException("Cycle in shader graph at " + nodeId);
            try
            {
                if (!_nodes.TryGetValue(nodeId, out ShaderGraphNode? node))
                    throw new ShaderGraphCompileException("Unknown node " + nodeId);
                return EmitNode(node, port, dialect);
            }
            finally
            {
                _visiting.Remove(key);
            }
        }

        private string EmitNode(ShaderGraphNode node, string port, Dialect dialect)
        {
            string type = node.Type ?? "";
            if (string.Equals(type, ShaderGraphNodeLibrary.Color, StringComparison.Ordinal))
            {
                string r = Field(node, "R", "1");
                string g = Field(node, "G", "1");
                string b = Field(node, "B", "1");
                string a = Field(node, "A", "1");
                string ctor = dialect == Dialect.Glsl ? "vec4" : "float4";
                string expr = ctor + "(" + F(r) + ", " + F(g) + ", " + F(b) + ", " + F(a) + ")";
                return Swizzle(expr, port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Float, StringComparison.Ordinal))
                return F(Field(node, "Value", "0"));
            if (string.Equals(type, ShaderGraphNodeLibrary.Property, StringComparison.Ordinal))
            {
                ShaderGraphProperty prop = ResolveProperty(Field(node, "Property", ""));
                if (prop.Type == ShaderGraphValueType.Texture2D)
                    throw new ShaderGraphCompileException("Texture properties must be sampled with SampleTexture2D.");
                return Swizzle(prop.Name, port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.SampleTexture2D, StringComparison.Ordinal))
            {
                ShaderGraphProperty tex = ResolveProperty(Field(node, "Texture", ""));
                if (tex.Type != ShaderGraphValueType.Texture2D)
                    throw new ShaderGraphCompileException("SampleTexture2D Texture must be Texture2D.");
                string uv = Incoming(node.Id, "UV", dialect, UvExpr(dialect));
                string sample = dialect == Dialect.Glsl
                    ? "texture(" + tex.Name + ", " + uv + ")"
                    : tex.Name + ".Sample(" + tex.Name + "Sampler, " + uv + ")";
                return Swizzle(sample, port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Add, StringComparison.Ordinal))
                return Binary(node, dialect, "+", Vec(dialect, 0));
            if (string.Equals(type, ShaderGraphNodeLibrary.Subtract, StringComparison.Ordinal))
                return Binary(node, dialect, "-", Vec(dialect, 0));
            if (string.Equals(type, ShaderGraphNodeLibrary.Multiply, StringComparison.Ordinal))
                return Binary(node, dialect, "*", Vec(dialect, 1));
            if (string.Equals(type, ShaderGraphNodeLibrary.Divide, StringComparison.Ordinal))
                return Binary(node, dialect, "/", Vec(dialect, 1));
            if (string.Equals(type, ShaderGraphNodeLibrary.Lerp, StringComparison.Ordinal))
            {
                string a = Incoming(node.Id, "A", dialect, Vec(dialect, 0));
                string b = Incoming(node.Id, "B", dialect, Vec(dialect, 1));
                string t = Incoming(node.Id, "T", dialect, "0.5");
                string fn = dialect == Dialect.Glsl ? "mix" : "lerp";
                return Swizzle(fn + "(" + a + ", " + b + ", " + t + ")", port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Saturate, StringComparison.Ordinal))
            {
                string x = Incoming(node.Id, "In", dialect, "0.0");
                return dialect == Dialect.Glsl ? "clamp(" + x + ", 0.0, 1.0)" : "saturate(" + x + ")";
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Power, StringComparison.Ordinal))
            {
                string a = Incoming(node.Id, "A", dialect, Incoming(node.Id, "In", dialect, "0.0"));
                string p = Incoming(node.Id, "B", dialect, F(Field(node, "Power", "1")));
                return "pow(" + a + ", " + p + ")";
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.OneMinus, StringComparison.Ordinal))
                return "(1.0 - " + Incoming(node.Id, "In", dialect, "0.0") + ")";
            if (string.Equals(type, ShaderGraphNodeLibrary.Abs, StringComparison.Ordinal))
                return "abs(" + Incoming(node.Id, "In", dialect, "0.0") + ")";
            if (string.Equals(type, ShaderGraphNodeLibrary.Clamp, StringComparison.Ordinal))
            {
                string x = Incoming(node.Id, "In", dialect, "0.0");
                string min = Incoming(node.Id, "Min", dialect, F(Field(node, "Min", "0")));
                string max = Incoming(node.Id, "Max", dialect, F(Field(node, "Max", "1")));
                return "clamp(" + x + ", " + min + ", " + max + ")";
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Split, StringComparison.Ordinal))
            {
                string input = Incoming(node.Id, "In", dialect, Vec(dialect, 0));
                return Swizzle(input, port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Combine, StringComparison.Ordinal))
            {
                string r = Incoming(node.Id, "R", dialect, "0.0");
                string g = Incoming(node.Id, "G", dialect, "0.0");
                string b = Incoming(node.Id, "B", dialect, "0.0");
                string a = Incoming(node.Id, "A", dialect, "1.0");
                string ctor = dialect == Dialect.Glsl ? "vec4" : "float4";
                return Swizzle(ctor + "(" + r + ", " + g + ", " + b + ", " + a + ")", port);
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.UV, StringComparison.Ordinal))
                return Swizzle(UvExpr(dialect), port);
            if (string.Equals(type, ShaderGraphNodeLibrary.TilingAndOffset, StringComparison.Ordinal))
            {
                string uv = Incoming(node.Id, "UV", dialect, UvExpr(dialect));
                string tiling = Incoming(node.Id, "Tiling", dialect,
                    Vec2(dialect, F(Field(node, "TilingX", "1")), F(Field(node, "TilingY", "1"))));
                string offset = Incoming(node.Id, "Offset", dialect,
                    Vec2(dialect, F(Field(node, "OffsetX", "0")), F(Field(node, "OffsetY", "0"))));
                return "((" + uv + " * " + tiling + ") + " + offset + ")";
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Fresnel, StringComparison.Ordinal))
            {
                string n = Incoming(node.Id, "Normal", dialect, NormalExpr(dialect, world: true));
                string v = Incoming(node.Id, "ViewDir", dialect, ViewDirExpr(dialect));
                string power = Incoming(node.Id, "Power", dialect, F(Field(node, "Power", "5")));
                string sat = dialect == Dialect.Glsl
                    ? "clamp(dot(normalize(" + n + "), normalize(" + v + ")), 0.0, 1.0)"
                    : "saturate(dot(normalize(" + n + "), normalize(" + v + ")))";
                return "pow(1.0 - " + sat + ", " + power + ")";
            }
            if (string.Equals(type, ShaderGraphNodeLibrary.Position, StringComparison.Ordinal))
                return PositionExpr(dialect, WorldSpace(node));
            if (string.Equals(type, ShaderGraphNodeLibrary.Normal, StringComparison.Ordinal))
                return NormalExpr(dialect, WorldSpace(node));
            if (string.Equals(type, ShaderGraphNodeLibrary.ViewDirection, StringComparison.Ordinal))
                return ViewDirExpr(dialect);
            if (string.Equals(type, ShaderGraphNodeLibrary.VertexColor, StringComparison.Ordinal))
                return InVertex
                    ? (dialect == Dialect.Glsl ? "vertexColor" : "float4(1.0, 1.0, 1.0, 1.0)")
                    : (dialect == Dialect.Glsl ? "vColor" : "input.vColor");
            if (ShaderGraphNodeLibrary.IsKnown(type))
                throw new ShaderGraphCompileException("Node type '" + type + "' cannot be used as an output.");
            throw new ShaderGraphCompileException("Unsupported node type '" + type + "'.");
        }

        private string Binary(ShaderGraphNode node, Dialect dialect, string op, string fallback)
        {
            string a = Incoming(node.Id, "A", dialect, fallback);
            string b = Incoming(node.Id, "B", dialect, fallback);
            return "(" + a + " " + op + " " + b + ")";
        }

        private string Incoming(string nodeId, string port, Dialect dialect, string fallback)
        {
            return TryFindIncoming(nodeId, port, out ShaderGraphEdge edge)
                ? EmitNodeOutput(edge.FromNode, edge.FromPort, dialect)
                : fallback;
        }

        private string UvExpr(Dialect dialect)
        {
            if (InVertex)
                return dialect == Dialect.Glsl ? "vertexTexCoord0" : "input.vertexTexCoord0";
            return dialect == Dialect.Glsl ? "texCoord0" : "input.texCoord0";
        }

        private string PositionExpr(Dialect dialect, bool world)
        {
            if (InVertex)
            {
                string obj = dialect == Dialect.Glsl ? "vertexPosition" : "input.vertexPosition";
                return world ? "TransformPosition(" + obj + ")" : obj;
            }
            return dialect == Dialect.Glsl ? "worldPos" : "input.worldPos";
        }

        private string NormalExpr(Dialect dialect, bool world)
        {
            if (InVertex)
            {
                string obj = dialect == Dialect.Glsl ? "vertexNormal" : "input.vertexNormal";
                return world ? "TransformDirection(" + obj + ")" : obj;
            }
            return dialect == Dialect.Glsl ? "vNormal" : "input.vNormal";
        }

        private string ViewDirExpr(Dialect dialect)
        {
            string pos = PositionExpr(dialect, world: true);
            return "normalize(_WorldSpaceCameraPos.xyz - " + pos + ")";
        }

        private static bool WorldSpace(ShaderGraphNode node)
        {
            string space = Field(node, "Space", "World");
            return !string.Equals(space, "Object", StringComparison.OrdinalIgnoreCase);
        }

        private static string Vec(Dialect dialect, int component)
        {
            string n = component == 0 ? "0.0" : "1.0";
            return dialect == Dialect.Glsl
                ? "vec4(" + n + ")"
                : "float4(" + n + ", " + n + ", " + n + ", " + n + ")";
        }

        private static string Vec2(Dialect dialect, string x, string y)
        {
            return dialect == Dialect.Glsl
                ? "vec2(" + x + ", " + y + ")"
                : "float2(" + x + ", " + y + ")";
        }

        private static string Swizzle(string expr, string port)
        {
            if (string.Equals(port, "RGB", StringComparison.Ordinal))
                return expr + ".rgb";
            if (string.Equals(port, "R", StringComparison.Ordinal) || string.Equals(port, "X", StringComparison.Ordinal))
                return expr + ".r";
            if (string.Equals(port, "G", StringComparison.Ordinal) || string.Equals(port, "Y", StringComparison.Ordinal))
                return expr + ".g";
            if (string.Equals(port, "B", StringComparison.Ordinal) || string.Equals(port, "Z", StringComparison.Ordinal))
                return expr + ".b";
            if (string.Equals(port, "A", StringComparison.Ordinal)
                || string.Equals(port, "Alpha", StringComparison.Ordinal)
                || string.Equals(port, "W", StringComparison.Ordinal))
                return expr + ".a";
            return expr;
        }

        private ShaderGraphProperty ResolveProperty(string idOrName)
        {
            if (_props.TryGetValue(idOrName, out ShaderGraphProperty? byId))
                return byId;
            if (_propsByName.TryGetValue(idOrName, out ShaderGraphProperty? byName))
                return byName;
            throw new ShaderGraphCompileException("Unknown property '" + idOrName + "'.");
        }

        private bool TryFindIncoming(string toNode, string toPort, out ShaderGraphEdge edge)
        {
            for (int i = 0; i < _edges.Count; i++)
            {
                ShaderGraphEdge e = _edges[i];
                if (e != null
                    && string.Equals(e.ToNode, toNode, StringComparison.Ordinal)
                    && string.Equals(e.ToPort, toPort, StringComparison.Ordinal))
                {
                    edge = e;
                    return true;
                }
            }
            edge = null!;
            return false;
        }

        private static string Field(ShaderGraphNode node, string key, string fallback)
        {
            List<ShaderGraphNodeField> fields = node.Fields;
            if (fields == null)
                return fallback;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] != null && string.Equals(fields[i].Key, key, StringComparison.Ordinal))
                    return fields[i].Value ?? fallback;
            }
            return fallback;
        }

        private static string F(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "0.0";
            if (float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                return v.ToString("0.0###", CultureInfo.InvariantCulture);
            return "0.0";
        }
    }
}
