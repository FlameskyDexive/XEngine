Shader "Default/DeferredLighting"

Variants
{
}

Properties
{
}

Pass "DeferredLighting"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        // Clustered light lists (P3.2): cluster lookup replaces the per-fragment BVH walk.
        #define XENGINE_CLUSTERED_LIGHTS
        #include "XEngineCG"
        #include "Lighting"
        #include "ShaderLibrary/ImageBasedLighting"
        #include "ShaderLibrary/GiProbes"

        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform sampler2D _CameraMotionVectorsTexture;
        uniform sampler2D _GBufferAlbedo;
        uniform sampler2D _GBufferEmission;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            float rawDepth = texture(_CameraDepthTexture, TexCoords).r;
            if (rawDepth >= 1.0)
                discard;

            vec3 viewPos = getViewPos(TexCoords, rawDepth);
            vec3 worldPos = (XENGINE_MATRIX_I_V * vec4(viewPos, 1.0)).xyz;
            vec3 worldNormal = DecodeViewNormal(texture(_CameraNormalsTexture, TexCoords));
            vec4 albedoAO = texture(_GBufferAlbedo, TexCoords);
            vec3 albedo = albedoAO.rgb;
            float ao = albedoAO.a;
            vec4 motionRM = texture(_CameraMotionVectorsTexture, TexCoords);
            float roughness = max(motionRM.b, 0.045);
            float metallic = saturate(motionRM.a);
            vec3 emission = texture(_GBufferEmission, TexCoords).rgb;

            vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
            vec3 lighting = CalculateForwardLighting(worldPos, worldNormal, viewDir,
                albedo, metallic, roughness, ao);

            // Realtime GI probe volume when active (deferred has no per-object _GIMode):
            // sample the volume with a small ambient floor, plain ambient otherwise.
            vec3 ambientLight = GiProbesActive()
                ? max(SampleGiProbes(worldPos, worldNormal), CalculateAmbient(worldNormal) * _AmbientStrength * 0.25)
                : CalculateAmbient(worldNormal) * _AmbientStrength;
            ambientLight *= ao;
            vec3 diffuseColor = albedo * (1.0 - metallic);
            vec3 ambientDiffuse = ambientLight * diffuseColor;
            vec3 F0 = mix(vec3(0.04), albedo, metallic);
            float NdotV = max(dot(worldNormal, viewDir), 0.0);
            vec3 F = FresnelSchlickRoughness(NdotV, F0, roughness);
            float specOcclusion = 1.0 - roughness * roughness;
            vec3 ambientSpecular = MixIndirectSpecular(
                ambientLight * F * mix(specOcclusion, 1.0, 0.25),
                worldPos, worldNormal, viewDir, F0, roughness);

            vec3 color = ApplyFog(ambientDiffuse + ambientSpecular + lighting + emission, worldPos);
            fragColor = vec4(color, 1.0);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        // Clustered light lists (P3.2): cluster lookup replaces the per-fragment BVH walk.
        #define XENGINE_CLUSTERED_LIGHTS
        #include "XEngineCG"
        #include "ShaderLibrary/LightCookie"
        #include "ShaderLibrary/GiProbes"
        #include "StandardLighting"
        #include "ShaderLibrary/ImageBasedLighting"

        Texture2D _CameraDepthTexture : register(t0);
        SamplerState _CameraDepthSampler : register(s0);
        Texture2D _CameraNormalsTexture : register(t1);
        SamplerState _CameraNormalsSampler : register(s1);
        Texture2D _GBufferAlbedo : register(t2);
        SamplerState _GBufferAlbedoSampler : register(s2);
        Texture2D _GBufferEmission : register(t3);
        SamplerState _GBufferEmissionSampler : register(s3);
        Texture2D _CameraMotionVectorsTexture : register(t9);
        SamplerState _CameraMotionSampler : register(s9);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        float3 ViewPosition(float2 uv, float depth)
        {
            float4 clip = float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
            float4 view = mul(xengine_MatIP, clip);
            return view.xyz / view.w;
        }

        float4 main(PSInput input) : SV_Target
        {
            float rawDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            if (rawDepth >= 1.0)
                discard;

            float3 viewPos = ViewPosition(input.TexCoords, rawDepth);
            float3 worldPos = mul(XENGINE_MATRIX_I_V, float4(viewPos, 1.0)).xyz;
            float3 worldNormal = DecodeViewNormal(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords));
            float4 albedoAO = _GBufferAlbedo.Sample(_GBufferAlbedoSampler, input.TexCoords);
            float3 albedo = albedoAO.rgb;
            float ao = albedoAO.a;
            float4 motionRM = _CameraMotionVectorsTexture.Sample(_CameraMotionSampler, input.TexCoords);
            float roughness = max(motionRM.b, 0.045);
            float metallic = saturate(motionRM.a);
            float3 emission = _GBufferEmission.Sample(_GBufferEmissionSampler, input.TexCoords).rgb;

            float3 viewDirection = normalize(_WorldSpaceCameraPos - worldPos);
            float3 ambientLight = GiProbesActive()
                ? max(SampleGiProbes(worldPos, worldNormal), StandardCalculateAmbient(worldNormal) * 0.25)
                : StandardCalculateAmbient(worldNormal);
            ambientLight *= ao;
            float3 diffuseColor = albedo * (1.0 - metallic);
            float3 ambientDiffuse = ambientLight * diffuseColor;
            float3 F0 = lerp(0.04.xxx, albedo, metallic);
            float NdotV = max(dot(worldNormal, viewDirection), 0.0);
            float3 F = StandardAmbientFresnel(NdotV, F0, roughness);
            float specOcclusion = 1.0 - roughness * roughness;
            float3 ambientSpecular = MixIndirectSpecular(
                ambientLight * F * lerp(specOcclusion, 1.0, 0.25),
                worldPos, worldNormal, viewDirection, F0, roughness);
            float3 color = ambientDiffuse + ambientSpecular;
            color += StandardCalculateLighting(worldPos, worldNormal, viewDirection, input.position.xy,
                albedo, metallic, roughness, ao, 0.0, 0.0, 0.0, 0.0);
            color += emission;
            color = StandardApplyFog(color, worldPos);
            return float4(color, 1.0);
        }
    }

    ENDSLANG
}
