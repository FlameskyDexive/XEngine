using System;
using System.IO;
using System.Linq;

using XEngine.Echo;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.Theming;
using XEngine.Editor.Projects;
using XEngine.Editor.Core.Tasks;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor;

public static class AssetCreateMenu
{
    [MenuItem("Assets/Create/Rendering/URP Asset (with Universal Renderer)", priority: 39, Icon = EditorIcons.Display)]
    static void CreateUrpAssetPairItem()
    {
        if (Project.Current == null) return;
        var created = UniversalProjectTemplate.CreateUrpAssetPair(
            Project.Current, GetCurrentFolder(), UniversalProjectTemplate.PipelineBaseName);
        if (created != null)
        {
            EditorAssetBackend.Instance?.InvalidateFolderIndex();
            Debug.Log($"Created URP asset pair: {created.Value.PipelinePath} + {created.Value.RendererPath}");
        }
    }

    [MenuItem("Assets/Create/Folder", priority: 0, Icon = EditorIcons.Folder)]
    static void CreateFolderItem()
    {
        var task = new CreateAssetTask();
        task.TaskType = CreateAssetTask.AssetType.Folder;
        task.BeginCreateTask(new AssetMenuEntry { Name = "New Folder", Extension = "", Icon = EditorRegistries.GetFileIconForExtension("") }, GetCurrentFolder());
    }

    [MenuItem("Assets/Create/Shader", priority: 1000, Icon = EditorIcons.WandMagicSparkles, Separator = true)]
    static void CreateShaderItem() => CreateShader(GetCurrentFolder());

    [MenuItem("Assets/Create/C# Script", priority: 1010, Icon = EditorIcons.FileCode, Separator = true)]
    static void CreateScriptItem() => NewScriptDialog.Open(GetCurrentFolder());

    [MenuItem("Assets/Create/Assembly Definition", priority: 1011, Icon = EditorIcons.FileLines)]
    static void CreateAsmDefItem() => CreateAssemblyDefinition(GetCurrentFolder());

    public static string? CreateAsset(AssetMenuEntry entry, string relativeFolder, string? filename = null)
    {
        string absFolder = GetAbsoluteFolder(relativeFolder);
        if (!Directory.Exists(absFolder)) return null;

        int lastSlash = entry.Name.LastIndexOf('/');
        string baseName = lastSlash >= 0 ? entry.Name.Substring(lastSlash + 1) : entry.Name;
        string name = filename ?? FindUniqueName(absFolder, $"New {baseName}", entry.Extension);
        string filePath = Path.Combine(absFolder, name);

        try
        {
            EngineObject instance = CreateTransientAsset(entry);
            try
            {
                var echo = XEngine.Echo.Serializer.Serialize(typeof(object), instance);
                File.WriteAllText(filePath, echo.WriteToString());
            }
            finally
            {
                instance.Dispose();
            }

            EditorAssetBackend.Instance?.InvalidateFolderIndex();
            Debug.Log($"Created {entry.Name}: {name}");
            return string.IsNullOrEmpty(relativeFolder) ? name : relativeFolder + "/" + name;
        }
        catch (Exception ex) { Debug.LogError($"Failed to create {entry.Name}: {ex.Message}"); return null; }
    }

    internal static EngineObject CreateTransientAsset(AssetMenuEntry entry)
    {
        if (entry.Factory != null)
            return entry.Factory();
        if (typeof(ScriptableObject).IsAssignableFrom(entry.Type))
            return ScriptableObject.CreateInstance(entry.Type);
        return (EngineObject)Activator.CreateInstance(entry.Type, nonPublic: true)!;
    }

    public static string GetCurrentFolder()
    {
        var selected = Selection.GetActiveAs<ContentItem>();
        if (selected != null && selected.IsFolder)
            return selected.RelativePath;
        return "";
    }

    public static string GetAbsoluteFolder(string relativeFolder)
    {
        if (Project.Current == null) return "";
        return string.IsNullOrEmpty(relativeFolder)
            ? Project.Current.AssetsPath
            : Path.Combine(Project.Current.AssetsPath, relativeFolder);
    }

    public static string FindUniqueName(string folder, string baseName, string ext)
        => Utils.UniqueNames.ForFile(folder, baseName, ext);

    public static string? CreateFolder(string relativeFolder)
    {
        string absFolder = GetAbsoluteFolder(relativeFolder);
        if (!Directory.Exists(absFolder)) return null;

        string name = FindUniqueName(absFolder, "New Folder", "");
        string newPath = Path.Combine(absFolder, name);
        Directory.CreateDirectory(newPath);
        MetaFile.EnsureMeta(newPath, "DefaultImporter");
        EditorAssetBackend.Instance?.InvalidateFolderIndex();
        Debug.Log($"Created folder: {name}");
        string relPath = string.IsNullOrEmpty(relativeFolder) ? name : relativeFolder + "/" + name;
        return relPath;
    }

    public static string? CreateAssemblyDefinition(string relativeFolder)
    {
        string absFolder = GetAbsoluteFolder(relativeFolder);
        if (!Directory.Exists(absFolder)) return null;

        string name = FindUniqueName(absFolder, "NewAssembly", Projects.Scripting.AssemblyDefinitionDatabase.Extension);
        string filePath = Path.Combine(absFolder, name);

        var def = new Projects.Scripting.AssemblyDefinition { Name = Path.GetFileNameWithoutExtension(name) };
        def.WriteToFile(filePath);

        Debug.Log($"Created assembly definition: {name}");
        return string.IsNullOrEmpty(relativeFolder) ? name : relativeFolder + "/" + name;
    }

    public static string? CreateShader(string relativeFolder)
    {
        string absFolder = GetAbsoluteFolder(relativeFolder);
        if (!Directory.Exists(absFolder)) return null;

        string name = FindUniqueName(absFolder, "New Shader", ".shader");
        string filePath = Path.Combine(absFolder, name);

        File.WriteAllText(filePath, @"// Custom PBR Shader
// GPU instancing, skeletal animation, shadows, and fog are handled
// automatically by the VertexAttributes and Lighting includes.
//
// Vertex utilities (from VertexAttributes.glsl):
//   TransformClip(pos)       - position to clip space (handles instancing + skinning)
//   TransformPosition(pos)   - position to world space
//   TransformDirection(dir)  - normal/tangent to world space
//   GetInstanceColor()       - vertex color with instance tint
//   GetInstanceCustomData()  - per-instance custom vec4
//   GetModelMatrix()         - model matrix (instanced or per-object)
//   GetMVPMatrix()           - MVP matrix
//
// Lighting utilities (from Lighting.glsl):
//   CalculateForwardLighting(worldPos, normal, viewDir, albedo, metallic, roughness, ao)
//   CalculateAmbient(worldNormal)
//   ApplyFog(color, worldPos)

Shader ""Custom/NewShader""

Properties
{
    _MainTex (""Albedo"", Texture2D) = ""white""
    _MainColor (""Tint"", Color) = (1.0, 1.0, 1.0, 1.0)
    _NormalTex (""Normal"", Texture2D) = ""normal""
    _SurfaceTex (""Surface (AO, Roughness, Metallic)"", Texture2D) = ""surface""
    _EmissionTex (""Emission"", Texture2D) = ""emission""
    _EmissionIntensity (""Emission Intensity"", Float) = 1.0
}

// === Main Forward Lit Pass ===
Pass ""Default""
{
    Tags { ""RenderOrder"" = ""Opaque"" }
    Cull Back

    GLSLPROGRAM

        Vertex
        {
            #include ""XEngineCG""
            #include ""VertexAttributes""

            out vec2 texCoord0;
            out vec3 worldPos;
            out vec4 vColor;
            out vec3 vNormal;
            out vec3 vTangent;
            out vec3 vBitangent;

            void main()
            {
                gl_Position = TransformClip(vertexPosition);
                texCoord0   = vertexTexCoord0;
                worldPos    = TransformPosition(vertexPosition);
                vColor      = GetInstanceColor();
                vNormal     = TransformDirection(vertexNormal);
#ifdef HAS_TANGENTS
                vTangent    = TransformDirection(vertexTangent.xyz);
                vBitangent  = cross(vNormal, vTangent);
#endif
            }
        }

        Fragment
        {
            #include ""XEngineCG""
            #include ""Lighting""

            layout (location = 0) out vec4 fragColor;

            in vec2 texCoord0;
            in vec3 worldPos;
            in vec4 vColor;
            in vec3 vNormal;
            in vec3 vTangent;
            in vec3 vBitangent;

            uniform sampler2D _MainTex;
            uniform sampler2D _NormalTex;
            uniform sampler2D _SurfaceTex;
            uniform sampler2D _EmissionTex;
            uniform float _EmissionIntensity;
            uniform vec4 _MainColor;

            void main()
            {
                // Albedo
                vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
                vec3 baseColor = gammaToLinearSpace(albedo.rgb);

                // Normal mapping
                vec3 worldNormal = ApplyNormalMap(_NormalTex, texCoord0, vNormal, vTangent, vBitangent);

                // Surface: R = AO, G = Roughness, B = Metallic
                vec4 surface = texture(_SurfaceTex, texCoord0);
                float ao = 1.0 - surface.r;
                float roughness = surface.g;
                float metallic = surface.b;

                // Emission
                vec3 emission = texture(_EmissionTex, texCoord0).rgb * _EmissionIntensity;

                // PBR lighting + ambient + fog
                vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
                vec3 lighting = CalculateForwardLighting(worldPos, worldNormal, viewDir,
                                                         baseColor, metallic, roughness, ao);
                vec3 ambient = CalculateAmbient(worldNormal) * baseColor * ao * _AmbientStrength;
                vec3 color = ApplyFog(ambient + lighting + emission, worldPos);

                fragColor = vec4(color, albedo.a);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include ""XEngineCG""

            cbuffer StandardMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _EmissionIntensity;
            };

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
                float3 vertexNormal : NORMAL;
                float4 vertexTangent : TANGENT;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 vColor : COLOR0;
                float3 vNormal : TEXCOORD2;
                float3 vTangent : TEXCOORD3;
                float3 vBitangent : TEXCOORD4;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformClip(input.vertexPosition);
                o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
                o.worldPos = TransformPosition(input.vertexPosition);
                o.vColor = GetInstanceColor();
                o.vNormal = TransformDirection(input.vertexNormal);
                o.vTangent = TransformDirection(input.vertexTangent.xyz);
                o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
                return o;
            }
        }

        Fragment
        {
            #include ""XEngineCG""
            #include ""StandardLighting""

            cbuffer StandardMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _EmissionIntensity;
            };

            Texture2D _MainTex : register(t0);
            SamplerState _MainTexSampler : register(s0);
            Texture2D _NormalTex : register(t1);
            SamplerState _NormalTexSampler : register(s1);
            Texture2D _SurfaceTex : register(t2);
            SamplerState _SurfaceTexSampler : register(s2);
            Texture2D _EmissionTex : register(t3);
            SamplerState _EmissionTexSampler : register(s3);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 vColor : COLOR0;
                float3 vNormal : TEXCOORD2;
                float3 vTangent : TEXCOORD3;
                float3 vBitangent : TEXCOORD4;
            };

            float4 main(PSInput input) : SV_Target
            {
                float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
                float3 baseColor = gammaToLinearSpace(albedo.rgb);
                float3 normal = normalize(input.vNormal);
                float3 tangent = normalize(input.vTangent);
                float3 bitangent = normalize(input.vBitangent);
                float3 tangentNormal = _NormalTex.Sample(_NormalTexSampler, input.texCoord0).xyz * 2.0 - 1.0;
                normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * normal);
                float4 surface = _SurfaceTex.Sample(_SurfaceTexSampler, input.texCoord0);
                float ao = saturate(1.0 - surface.r);
                float roughness = max(surface.g, 0.045);
                float metallic = saturate(surface.b);
                float3 emission = _EmissionTex.Sample(_EmissionTexSampler, input.texCoord0).rgb * _EmissionIntensity;
                float3 viewDirection = normalize(_WorldSpaceCameraPos - input.worldPos);
                float3 color = StandardCalculateLighting(input.worldPos, normal, viewDirection, input.position.xy,
                    baseColor, metallic, roughness, ao, 0.0, 0.0, 0.0, 0.0);
                color += StandardCalculateAmbient(normal) * baseColor * ao;
                color += emission;
                color = StandardApplyFog(color, input.worldPos);
                return float4(color, albedo.a);
            }
        }
    ENDSLANG
}

// === Depth + Normals Pre-Pass (for GTAO, SSR) ===
Pass ""DepthNormals""
{
    Tags { ""LightMode"" = ""DepthNormals"" }
    Cull Back

    GLSLPROGRAM

        Vertex
        {
            #include ""XEngineCG""
            #include ""VertexAttributes""

            out vec3 vNormal;
            out vec3 vTangent;
            out vec3 vBitangent;
            out vec2 texCoord0;

            void main()
            {
                gl_Position = TransformClip(vertexPosition);
                vNormal     = TransformDirection(vertexNormal);
#ifdef HAS_TANGENTS
                vTangent    = TransformDirection(vertexTangent.xyz);
                vBitangent  = cross(vNormal, vTangent);
#endif
                texCoord0   = vertexTexCoord0;
            }
        }

        Fragment
        {
            #include ""XEngineCG""

            layout (location = 0) out vec4 normalOut;
            in vec3 vNormal;
            in vec3 vTangent;
            in vec3 vBitangent;
            in vec2 texCoord0;

            uniform sampler2D _NormalTex;

            void main()
            {
                vec3 worldNormal = ApplyNormalMap(_NormalTex, texCoord0, vNormal, vTangent, vBitangent);
                normalOut = EncodeViewNormal(worldNormal);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include ""XEngineCG""

            cbuffer StandardMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _EmissionIntensity;
            };

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
                float3 vertexNormal : NORMAL;
                float4 vertexTangent : TANGENT;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float3 vNormal : TEXCOORD0;
                float3 vTangent : TEXCOORD1;
                float3 vBitangent : TEXCOORD2;
                float2 texCoord0 : TEXCOORD3;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformClip(input.vertexPosition);
                o.vNormal = TransformDirection(input.vertexNormal);
                o.vTangent = TransformDirection(input.vertexTangent.xyz);
                o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
                o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
                return o;
            }
        }

        Fragment
        {
            #include ""XEngineCG""

            cbuffer StandardMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _EmissionIntensity;
            };

            Texture2D _NormalTex : register(t1);
            SamplerState _NormalTexSampler : register(s1);

            struct PSInput
            {
                float4 position : SV_Position;
                float3 vNormal : TEXCOORD0;
                float3 vTangent : TEXCOORD1;
                float3 vBitangent : TEXCOORD2;
                float2 texCoord0 : TEXCOORD3;
            };

            float4 main(PSInput input) : SV_Target
            {
                float3 normal = normalize(input.vNormal);
                float3 tangent = normalize(input.vTangent);
                float3 bitangent = normalize(input.vBitangent);
                float3 tangentNormal = _NormalTex.Sample(_NormalTexSampler, input.texCoord0).xyz * 2.0 - 1.0;
                normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * normal);
                return EncodeViewNormal(normal);
            }
        }
    ENDSLANG
}

// === Shadow Caster Pass ===
Pass ""ShadowCaster""
{
    Tags { ""LightMode"" = ""ShadowCaster"" }
    Cull Back

    GLSLPROGRAM

        Vertex
        {
            #include ""XEngineCG""
            #include ""VertexAttributes""

            void main()
            {
                gl_Position = TransformClip(vertexPosition);
            }
        }

        Fragment
        {
            #include ""XEngineCG""

            void main()
            {
                gl_FragDepth = gl_FragCoord.z;
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include ""XEngineCG""

            struct VSInput
            {
                float3 vertexPosition : POSITION;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformClip(input.vertexPosition);
                return o;
            }
        }

        Fragment
        {
            #include ""XEngineCG""

            float4 main(float4 position : SV_Position) : SV_Target
            {
                return float4(0.0, 0.0, 0.0, 1.0);
            }
        }
    ENDSLANG
}
");

        Debug.Log($"Created shader: {name}");
        return string.IsNullOrEmpty(relativeFolder) ? name : relativeFolder + "/" + name;
    }

}
