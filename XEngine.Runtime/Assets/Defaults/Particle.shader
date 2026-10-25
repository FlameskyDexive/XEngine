Shader "Default/Particle"

Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}

Properties
{
    _MainTex ("Particle Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _SoftParticlesFactor ("Soft Particles Factor", Float) = 1.0
    _VelocityScale ("Stretched Billboard Velocity Scale", Float) = 1.0
    _LengthScale ("Stretched Billboard Length Scale", Float) = 1.0
}

Pass "Particle"
{
    Tags { "RenderOrder" = "Transparent" }

    // Particle settings
    Cull Off
    ZWrite Off
    Blend Alpha

	GLSLPROGRAM

		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec4 vColor;
            out vec3 worldPos;
            out float vLifetime;
            out float vEyeDepth;

            // Stretched billboard mode (M9): instanceModelRow2 carries the particle velocity
            // (xyz) with W encoding 1 + total length (0 = view-plane).
            uniform float _VelocityScale = 1.0;
            uniform float _LengthScale = 1.0;

#ifdef GPU_INSTANCING
            // Instance attributes declared in VertexAttributes.glsl
#endif

			void main()
			{
#ifdef GPU_INSTANCING
                // Extract position and scale from instance matrix
                vec3 particlePosition = instanceModelRow3.xyz;

                // Calculate scale from the instance matrix column lengths
                float scaleX = length(instanceModelRow0.xyz);
                float scaleY = length(instanceModelRow1.xyz);

                // rpgvfx mesh render mode: the instance matrix is a full localâ†’world
                // transform (columns); no billboarding. world = M * v.
#ifdef PARTICLE_MESH
                vec3 worldPosition = instanceModelRow0.xyz * vertexPosition.x
                    + instanceModelRow1.xyz * vertexPosition.y
                    + instanceModelRow2.xyz * vertexPosition.z
                    + instanceModelRow3.xyz;
#else
                // Extract rotation from the matrix (Z-axis rotation for billboards)
                // Get normalized right vector from first column
                vec3 matrixRight = instanceModelRow0.xyz / scaleX;
                vec3 matrixUp = instanceModelRow1.xyz / scaleY;

                // Calculate rotation angle from the XY components
                // This extracts the Z-axis rotation that was applied
                float rotationAngle = atan(matrixRight.y, matrixRight.x);

                // Create billboard matrix using camera's inverse view matrix
                // Get camera right and up vectors from view matrix (inverse)
                vec3 cameraRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
                vec3 cameraUp = vec3(XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[2][1]);

                // Apply rotation to the quad vertices
                float cosRot = cos(rotationAngle);
                float sinRot = sin(rotationAngle);
                vec2 rotatedVertex = vec2(
                    vertexPosition.x * cosRot - vertexPosition.y * sinRot,
                    vertexPosition.x * sinRot + vertexPosition.y * cosRot
                );

                // Build billboard quad in world space with rotation applied
                vec3 worldPosition = particlePosition
                    + cameraRight * rotatedVertex.x * scaleX
                    + cameraUp * rotatedVertex.y * scaleY;

                // Velocity-stretched billboard (Unity RenderMode.Stretch): the quad's long
                // axis aligns with the velocity; head at the particle, tail trailing.
                if (instanceModelRow2.w > 0.5)
                {
                    vec3 velocity = instanceModelRow2.xyz;
                    float speed = length(velocity);
                    vec3 velocityDir = speed > 0.0001 ? velocity / speed : vec3(0.0);
                    vec3 projectedSide = cross(_WorldSpaceCameraPos.xyz - particlePosition, velocity);
                    vec2 sideInView = vec2(dot(projectedSide, cameraRight), dot(projectedSide, cameraUp));
                    float sideLength = length(sideInView);
                    vec3 side = sideLength > 0.0001
                        ? (cameraRight * sideInView.x + cameraUp * sideInView.y) / sideLength : cameraRight;
                    float stretchLength = max(instanceModelRow2.w - 1.0, 0.0);
                    worldPosition = particlePosition + side * vertexPosition.y * scaleY
                        - velocityDir * (vertexPosition.x + 0.5) * stretchLength;
                }
#endif // PARTICLE_MESH

                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);

                // Apply UV animation
                // instanceCustomData: x=lifetime, y=UV offsetX, z=UV offsetY, w=UV scale
                vec2 uvOffset = instanceCustomData.yz;
                vec2 uvScale = vec2(instanceCustomData.w, instanceCustomData.w * instanceModelRow3.w);
                texCoord0 = vertexTexCoord0 * uvScale + uvOffset;

                worldPos = worldPosition;
                vEyeDepth = abs((XENGINE_MATRIX_V * vec4(worldPosition, 1.0)).z);

                // Particle quads do not carry a COLOR0 stream. Reading that missing input is
                // undefined on explicit APIs (Vulkan returned black/green data), so the
                // instanced path uses the authored per-particle color directly.
                vColor = instanceColor;

                // Pass normalized lifetime from custom data
                vLifetime = instanceCustomData.x;
#else
                // Non-instanced fallback
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                texCoord0 = vertexTexCoord0;
                worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                vColor = vertexColor;
                vLifetime = 0.0;
                vEyeDepth = 0.0;
#endif
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec4 vColor;
            in vec3 worldPos;
            in float vLifetime;
            in float vEyeDepth;

            // Soft particles (M9): fade where the particle sits close behind scene geometry;
            // the global depth texture is bound before the transparent pass (CopyDepth).
            uniform sampler2D _CameraDepthTexture;

			uniform sampler2D _MainTex;
			uniform vec4 _MainColor;
            uniform float _SoftParticlesFactor;

			float LinearEyeDepth(float rawDepth)
            {
                // Reconstruct eye depth from the hardware depth buffer (VfxParticles math).
                float near = _ProjectionParams.y;
                float far = _ProjectionParams.z;
                float ndc = rawDepth * 2.0 - 1.0;
                return 2.0 * near * far / (far + near - ndc * (far - near));
            }

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
                float alpha = albedo.a;

                // Soft-particle depth fade over the factor distance.
                if (_SoftParticlesFactor > 0.001 && vEyeDepth > 0.0)
                {
                    vec2 screenUV = gl_FragCoord.xy / _ScreenParams.xy;
                    float sceneEye = LinearEyeDepth(texture(_CameraDepthTexture, screenUV).r);
                    alpha *= clamp((sceneEye - vEyeDepth) / _SoftParticlesFactor, 0.0, 1.0);
                }

                if (alpha < 0.01)
                    discard;

                vec3 baseColor = gammaToLinearSpace(albedo.rgb);
				fragColor = vec4(baseColor, alpha);
			}
		}
	ENDGLSL


	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer ParticleMaterial : register(b2)
			{
				float4 _MainColor;
				float _SoftParticlesFactor;
				float _VelocityScale;
				float _LengthScale;
				float _ParticlePadding;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
#ifndef GPU_INSTANCING
				float4 vertexColor : COLOR;
#endif
#ifdef GPU_INSTANCING
				float4 instanceModelRow0 : TEXCOORD8;
				float4 instanceModelRow1 : TEXCOORD9;
				float4 instanceModelRow2 : TEXCOORD10;
				float4 instanceModelRow3 : TEXCOORD11;
				float4 instanceColor : TEXCOORD12;
				float4 instanceCustomData : TEXCOORD13;
#endif
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float vLifetime : TEXCOORD2;
				float vEyeDepth : TEXCOORD3;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
	#ifdef GPU_INSTANCING
				float3 particlePosition = input.instanceModelRow3.xyz;
				float scaleX = length(input.instanceModelRow0.xyz);
				float scaleY = length(input.instanceModelRow1.xyz);
				float3 worldPosition;
			#ifdef PARTICLE_MESH
				worldPosition = input.instanceModelRow0.xyz * input.vertexPosition.x
                    + input.instanceModelRow1.xyz * input.vertexPosition.y
                    + input.instanceModelRow2.xyz * input.vertexPosition.z
                    + input.instanceModelRow3.xyz;
			#else
				float3 matrixRight = input.instanceModelRow0.xyz / max(scaleX, 0.0001);
				float rotationAngle = atan2(matrixRight.y, matrixRight.x);
				float3 cameraRight = float3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[0][2]);
				float3 cameraUp = float3(XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[1][2]);
				float cosine = cos(rotationAngle);
				float sine = sin(rotationAngle);
				float2 rotatedVertex = float2(
					input.vertexPosition.x * cosine - input.vertexPosition.y * sine,
					input.vertexPosition.x * sine + input.vertexPosition.y * cosine);
				worldPosition = particlePosition
					+ cameraRight * rotatedVertex.x * scaleX
					+ cameraUp * rotatedVertex.y * scaleY;
				if (input.instanceModelRow2.w > 0.5)
				{
					float3 velocity = input.instanceModelRow2.xyz;
					float speed = length(velocity);
                    float3 velocityDir = speed > 0.0001 ? velocity / speed : float3(0.0, 0.0, 0.0);
                    float3 projectedSide = cross(_WorldSpaceCameraPos.xyz - particlePosition, velocity);
                    float2 sideInView = float2(dot(projectedSide, cameraRight), dot(projectedSide, cameraUp));
                    float sideLength = length(sideInView);
                    float3 side = sideLength > 0.0001
                        ? (cameraRight * sideInView.x + cameraUp * sideInView.y) / sideLength : cameraRight;
                    float stretchLength = max(input.instanceModelRow2.w - 1.0, 0.0);
                    worldPosition = particlePosition + side * input.vertexPosition.y * scaleY
                        - velocityDir * (input.vertexPosition.x + 0.5) * stretchLength;
				}
			#endif
				o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
				o.texCoord0 = input.vertexTexCoord0 * float2(input.instanceCustomData.w, input.instanceCustomData.w * input.instanceModelRow3.w) + input.instanceCustomData.yz;
				o.worldPos = worldPosition;
				o.vEyeDepth = abs(mul(XENGINE_MATRIX_V, float4(worldPosition, 1.0)).z);
				o.vColor = input.instanceColor;
				o.vLifetime = input.instanceCustomData.x;
#else
				o.position = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
				o.texCoord0 = input.vertexTexCoord0;
				o.worldPos = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)).xyz;
				o.vColor = input.vertexColor;
				o.vLifetime = 0.0;
				o.vEyeDepth = 0.0;
#endif
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer ParticleMaterial : register(b2)
			{
				float4 _MainColor;
				float _SoftParticlesFactor;
				// zonezero M9: the old float3 padding slots now carry the stretched-billboard
				// factors. Component instances now carry total length in row2.w directly.
				float _VelocityScale;
				float _LengthScale;
				float _ParticlePadding;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float vLifetime : TEXCOORD2;
				float vEyeDepth : TEXCOORD3;
			};

			Texture2D _CameraDepthTexture : register(t3);
			SamplerState _CameraDepthSampler : register(s3);

			float LinearEyeDepth(float rawDepth)
			{
				float nearPlane = _ProjectionParams.y;
				float farPlane = _ProjectionParams.z;
				float ndc = rawDepth * 2.0 - 1.0;
				return 2.0 * nearPlane * farPlane / (farPlane + nearPlane - ndc * (farPlane - nearPlane));
			}

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				float alpha = albedo.a;
				if (_SoftParticlesFactor > 0.001 && input.vEyeDepth > 0.0)
				{
					float2 screenUV = input.position.xy / max(_ScreenParams.xy, 1.0.xx);
					float sceneEye = LinearEyeDepth(_CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r);
					alpha *= saturate((sceneEye - input.vEyeDepth) / _SoftParticlesFactor);
				}
				if (alpha < 0.01)
					discard;
				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				return float4(baseColor, alpha);
			}
		}
	ENDSLANG
}

Pass "ParticleAdditive"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    ZWrite Off
    Blend Additive

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec4 vColor;
            out vec3 worldPos;
            out float vLifetime;
            out float vEyeDepth;

            uniform float _VelocityScale = 1.0;
            uniform float _LengthScale = 1.0;

			void main()
			{
#ifdef GPU_INSTANCING
                vec3 particlePosition = instanceModelRow3.xyz;
                float scaleX = length(instanceModelRow0.xyz);
                float scaleY = length(instanceModelRow1.xyz);
#ifdef PARTICLE_MESH
                vec3 worldPosition = instanceModelRow0.xyz * vertexPosition.x
                    + instanceModelRow1.xyz * vertexPosition.y
                    + instanceModelRow2.xyz * vertexPosition.z
                    + instanceModelRow3.xyz;
#else
                vec3 matrixRight = instanceModelRow0.xyz / scaleX;
                float rotationAngle = atan(matrixRight.y, matrixRight.x);
                vec3 cameraRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
                vec3 cameraUp = vec3(XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[2][1]);
                float cosRot = cos(rotationAngle);
                float sinRot = sin(rotationAngle);
                vec2 rotatedVertex = vec2(
                    vertexPosition.x * cosRot - vertexPosition.y * sinRot,
                    vertexPosition.x * sinRot + vertexPosition.y * cosRot
                );
                vec3 worldPosition = particlePosition
                    + cameraRight * rotatedVertex.x * scaleX
                    + cameraUp * rotatedVertex.y * scaleY;
                if (instanceModelRow2.w > 0.5)
                {
                    vec3 velocity = instanceModelRow2.xyz;
                    float speed = length(velocity);
                    if (speed > 0.0001)
                    {
                        vec3 dirToCamera = _WorldSpaceCameraPos.xyz - particlePosition;
                        vec3 velocityDir = velocity / speed;
                        vec3 stretch = velocityDir * speed * _VelocityScale * _LengthScale * scaleY;
                        vec3 sideDir = normalize(cross(velocityDir, normalize(dirToCamera)));
                        worldPosition = particlePosition + sideDir * rotatedVertex.x * scaleX - stretch * (rotatedVertex.y * 0.5 + 0.5);
                    }
                }
#endif // PARTICLE_MESH
                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);
                vec2 uvOffset = instanceCustomData.yz;
                vec2 uvScale = vec2(instanceCustomData.w, instanceCustomData.w * instanceModelRow3.w);
                texCoord0 = vertexTexCoord0 * uvScale + uvOffset;
                worldPos = worldPosition;
                vEyeDepth = abs((XENGINE_MATRIX_V * vec4(worldPosition, 1.0)).z);
                vColor = instanceColor;
                vLifetime = instanceCustomData.x;
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                texCoord0 = vertexTexCoord0;
                worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                vColor = vertexColor;
                vLifetime = 0.0;
                vEyeDepth = 0.0;
#endif
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec4 vColor;
            in vec3 worldPos;
            in float vLifetime;
            in float vEyeDepth;

			uniform sampler2D _MainTex;
			uniform vec4 _MainColor;
            uniform float _SoftParticlesFactor;
            uniform sampler2D _CameraDepthTexture;

            float LinearEyeDepth(float rawDepth)
            {
                float near = _ProjectionParams.y;
                float far = _ProjectionParams.z;
                float ndc = rawDepth * 2.0 - 1.0;
                return 2.0 * near * far / (far + near - ndc * (far - near));
            }

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
                float alpha = albedo.a;
                if (_SoftParticlesFactor > 0.001 && vEyeDepth > 0.0)
                {
                    vec2 screenUV = gl_FragCoord.xy / _ScreenParams.xy;
                    float sceneEye = LinearEyeDepth(texture(_CameraDepthTexture, screenUV).r);
                    alpha *= clamp((sceneEye - vEyeDepth) / _SoftParticlesFactor, 0.0, 1.0);
                }
                if (alpha < 0.01)
                    discard;
                // Additive: color scales by alpha; dst alpha keeps its own value.
				fragColor = vec4(gammaToLinearSpace(albedo.rgb) * alpha, 0.0);
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
				float2 vertexTexCoord0 : TEXCOORD0;
#ifndef GPU_INSTANCING
				float4 vertexColor : COLOR;
#endif
#ifdef GPU_INSTANCING
				float4 instanceModelRow0 : TEXCOORD8;
				float4 instanceModelRow1 : TEXCOORD9;
				float4 instanceModelRow2 : TEXCOORD10;
				float4 instanceModelRow3 : TEXCOORD11;
				float4 instanceColor : TEXCOORD12;
				float4 instanceCustomData : TEXCOORD13;
#endif
			};

			cbuffer ParticleMaterial : register(b2)
			{
				float4 _MainColor;
				float _SoftParticlesFactor;
				float _VelocityScale;
				float _LengthScale;
				float _ParticlePadding;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float vLifetime : TEXCOORD2;
				float vEyeDepth : TEXCOORD3;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
#ifdef GPU_INSTANCING
				float3 particlePosition = input.instanceModelRow3.xyz;
				float scaleX = length(input.instanceModelRow0.xyz);
				float scaleY = length(input.instanceModelRow1.xyz);
				float3 worldPosition;
			#ifdef PARTICLE_MESH
				worldPosition = input.instanceModelRow0.xyz * input.vertexPosition.x
                    + input.instanceModelRow1.xyz * input.vertexPosition.y
                    + input.instanceModelRow2.xyz * input.vertexPosition.z
                    + input.instanceModelRow3.xyz;
			#else
				float3 matrixRight = input.instanceModelRow0.xyz / max(scaleX, 0.0001);
				float rotationAngle = atan2(matrixRight.y, matrixRight.x);
				float3 cameraRight = float3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[0][2]);
				float3 cameraUp = float3(XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[1][2]);
				float cosine = cos(rotationAngle);
				float sine = sin(rotationAngle);
				float2 rotatedVertex = float2(
					input.vertexPosition.x * cosine - input.vertexPosition.y * sine,
					input.vertexPosition.x * sine + input.vertexPosition.y * cosine);
				worldPosition = particlePosition
					+ cameraRight * rotatedVertex.x * scaleX
					+ cameraUp * rotatedVertex.y * scaleY;
				if (input.instanceModelRow2.w > 0.5)
				{
					float3 velocity = input.instanceModelRow2.xyz;
					float speed = length(velocity);
					if (speed > 0.0001)
					{
						float3 dirToCamera = _WorldSpaceCameraPos.xyz - particlePosition;
						float3 velocityDir = velocity / speed;
						float3 stretch = velocityDir * speed;
						float3 sideDir = normalize(cross(velocityDir, normalize(dirToCamera)));
						worldPosition = particlePosition + sideDir * rotatedVertex.x * scaleX - stretch * (rotatedVertex.y * 0.5 + 0.5);
					}
				}
			#endif
				o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
				o.texCoord0 = input.vertexTexCoord0 * float2(input.instanceCustomData.w, input.instanceCustomData.w * input.instanceModelRow3.w) + input.instanceCustomData.yz;
				o.worldPos = worldPosition;
				o.vEyeDepth = abs(mul(XENGINE_MATRIX_V, float4(worldPosition, 1.0)).z);
				o.vColor = input.instanceColor;
				o.vLifetime = input.instanceCustomData.x;
#else
				o.position = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
				o.texCoord0 = input.vertexTexCoord0;
				o.worldPos = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)).xyz;
				o.vColor = input.vertexColor;
				o.vLifetime = 0.0;
				o.vEyeDepth = 0.0;
#endif
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer ParticleMaterial : register(b2)
			{
				float4 _MainColor;
				float _SoftParticlesFactor;
				float _VelocityScale;
				float _LengthScale;
				float _ParticlePadding;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			Texture2D _CameraDepthTexture : register(t3);
			SamplerState _CameraDepthSampler : register(s3);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float vLifetime : TEXCOORD2;
				float vEyeDepth : TEXCOORD3;
			};

			float LinearEyeDepth(float rawDepth)
			{
				float nearPlane = _ProjectionParams.y;
				float farPlane = _ProjectionParams.z;
				float ndc = rawDepth * 2.0 - 1.0;
				return 2.0 * nearPlane * farPlane / (farPlane + nearPlane - ndc * (farPlane - nearPlane));
			}

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				float alpha = albedo.a;
				if (_SoftParticlesFactor > 0.001 && input.vEyeDepth > 0.0)
				{
					float2 screenUV = input.position.xy / max(_ScreenParams.xy, 1.0.xx);
					float sceneEye = LinearEyeDepth(_CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r);
					alpha *= saturate((sceneEye - input.vEyeDepth) / _SoftParticlesFactor);
				}
				if (alpha < 0.01)
					discard;
				// Additive: color scales by alpha; dst alpha keeps its own value.
				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				return float4(baseColor * alpha, 0.0);
			}
		}
	ENDSLANG

}
