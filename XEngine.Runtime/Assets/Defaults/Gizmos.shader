Shader "Default/Gizmos"

Variants
{
}

Properties
{
}

Pass "Gizmos"
{
    Tags { "RenderOrder" = "Opaque" }

    Cull None
    Blend Alpha
    ZWrite Off
    ZTest Always

	GLSLPROGRAM
	Vertex
	{
        #include "XEngineCG"
        #include "VertexAttributes"
		out vec4 vColor;
		out vec4 screenPos;

		uniform mat4 mvp;
		void main()
		{
			gl_Position = XENGINE_MATRIX_VP * vec4(vertexPosition, 1.0);
		    vColor = vertexColor;
		    screenPos = gl_Position;
		}
	}
	Fragment
	{
        #include "XEngineCG"
		in vec4 vColor;
		in vec4 screenPos;
		layout (location = 0) out vec4 finalColor;
        uniform sampler2D _CameraDepthTexture;

		void main()
		{
		    // Get screen UV from gl_FragCoord (already in pixel coordinates)
		    vec2 screenUV = gl_FragCoord.xy / _ScreenParams.xy;

		    // Sample the depth buffer at this fragment's screen position
		    float sceneDepth = texture(_CameraDepthTexture, screenUV).r;

		    // Use gl_FragCoord.z which is in the same depth space as the depth buffer
		    float fragmentDepth = gl_FragCoord.z;

		    // Check if this fragment is behind scene geometry
		    // If fragmentDepth > sceneDepth, it's occluded
		    float occluded = step(sceneDepth, fragmentDepth - 0.00001); // Small epsilon to avoid z-fighting

		    // When occluded: darken significantly and make transparent
		    // When visible: use original color
		    vec4 color = vColor;
		    if (occluded > 0.5)
		    {
		        color.rgb *= 0.5; // Darken to 50% of original brightness
		        color.a *= 0.3;   // Make 70% transparent
		    }

			finalColor = color;
		}
	}
	ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float4 vertexColor : COLOR;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float4 color : COLOR0;
            };

            VSOutput main(VSInput input)
            {
                VSOutput output;
                output.position = mul(XENGINE_MATRIX_VP, float4(input.vertexPosition, 1.0));
                output.color = input.vertexColor;
                return output;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            Texture2D _CameraDepthTexture : register(t0);
            SamplerState _CameraDepthSampler : register(s0);

            struct PSInput
            {
                float4 position : SV_Position;
                float4 color : COLOR0;
            };

            float4 main(PSInput input) : SV_Target
            {
                float2 screenUV = input.position.xy / _ScreenParams.xy;
                float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r;
                float occluded = step(sceneDepth, input.position.z - 0.00001);
                float4 color = input.color;
                if (occluded > 0.5)
                {
                    color.rgb *= 0.5;
                    color.a *= 0.3;
                }
                return color;
            }
        }
    ENDSLANG
}
