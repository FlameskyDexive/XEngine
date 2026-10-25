Shader "Default/ProceduralSkybox"

Variants
{
}

Properties
{
}

Pass "Skybox"
{
    Tags { "RenderOrder" = "Opaque" }

    // Rasterizer culling mode
    Blend Off
    Cull None
    //ZTest Lequal
    ZTest Off
    ZWrite Off

	GLSLPROGRAM

	    Vertex
	    {
            #include "XEngineCG"

	    	in vec3 vertexPosition;

            out vec3 vSkyColor;
            out vec3 vDirection;
            out vec3 vSunDir;

	    	uniform vec2 Resolution;
	    	uniform vec3 _SunDir;
	    	uniform float fogDensity;
	    	
	    	#define PI 3.141592
            #define iSteps 16
            #define jSteps 8
            
            vec2 rsi(vec3 r0, vec3 rd, float sr) {
                // ray-sphere intersection that assumes
                // the sphere is centered at the origin.
                // No intersection when result.x > result.y
                float a = dot(rd, rd);
                float b = 2.0 * dot(rd, r0);
                float c = dot(r0, r0) - (sr * sr);
                float d = (b*b) - 4.0*a*c;
                if (d < 0.0) return vec2(1e5,-1e5);
                return vec2(
                    (-b - sqrt(d))/(2.0*a),
                    (-b + sqrt(d))/(2.0*a)
                );
            }
            
            vec3 atmosphere(vec3 r, vec3 r0, vec3 pSun, float iSun, float rPlanet, float rAtmos, vec3 kRlh, float kMie, float shRlh, float shMie, float g) {
                // Normalize the sun and view directions.
                pSun = normalize(pSun);
                r = normalize(r);
            
                // Calculate the step size of the primary ray.
                vec2 p = rsi(r0, r, rAtmos);
                if (p.x > p.y) return vec3(0,0,0);
                p.y = min(p.y, rsi(r0, r, rPlanet).x);
                float iStepSize = (p.y - p.x) / float(iSteps);
            
                // Initialize the primary ray time.
                float iTime = 0.0;
            
                // Initialize accumulators for Rayleigh and Mie scattering.
                vec3 totalRlh = vec3(0,0,0);
                vec3 totalMie = vec3(0,0,0);
            
                // Initialize optical depth accumulators for the primary ray.
                float iOdRlh = 0.0;
                float iOdMie = 0.0;
            
                // Calculate the Rayleigh and Mie phases.
                float mu = dot(r, pSun);
                float mumu = mu * mu;
                float gg = g * g;
                float pRlh = 3.0 / (16.0 * PI) * (1.0 + mumu);
                float pMie = 3.0 / (8.0 * PI) * ((1.0 - gg) * (mumu + 1.0)) / (pow(1.0 + gg - 2.0 * mu * g, 1.5) * (2.0 + gg));
            
                // Sample the primary ray.
                for (int i = 0; i < iSteps; i++) {
            
                    // Calculate the primary ray sample position.
                    vec3 iPos = r0 + r * (iTime + iStepSize * 0.5);
            
                    // Calculate the height of the sample.
                    float iHeight = length(iPos) - rPlanet;
            
                    // Calculate the optical depth of the Rayleigh and Mie scattering for this step.
                    float odStepRlh = exp(-iHeight / shRlh) * iStepSize;
                    float odStepMie = exp(-iHeight / shMie) * iStepSize;
            
                    // Accumulate optical depth.
                    iOdRlh += odStepRlh;
                    iOdMie += odStepMie;
            
                    // Calculate the step size of the secondary ray.
                    float jStepSize = rsi(iPos, pSun, rAtmos).y / float(jSteps);
            
                    // Initialize the secondary ray time.
                    float jTime = 0.0;
            
                    // Initialize optical depth accumulators for the secondary ray.
                    float jOdRlh = 0.0;
                    float jOdMie = 0.0;
            
                    // Sample the secondary ray.
                    for (int j = 0; j < jSteps; j++) {
            
                        // Calculate the secondary ray sample position.
                        vec3 jPos = iPos + pSun * (jTime + jStepSize * 0.5);
            
                        // Calculate the height of the sample.
                        float jHeight = length(jPos) - rPlanet;
            
                        // Accumulate the optical depth.
                        jOdRlh += exp(-jHeight / shRlh) * jStepSize;
                        jOdMie += exp(-jHeight / shMie) * jStepSize;
            
                        // Increment the secondary ray time.
                        jTime += jStepSize;
                    }
            
                    // Calculate attenuation.
                    vec3 attn = exp(-(kMie * (iOdMie + jOdMie) + kRlh * (iOdRlh + jOdRlh)));
            
                    // Accumulate scattering.
                    totalRlh += odStepRlh * attn;
                    totalMie += odStepMie * attn;
            
                    // Increment the primary ray time.
                    iTime += iStepSize;
            
                }
            
                // Calculate and return the final color.
                return iSun * (pRlh * kRlh * totalRlh + pMie * kMie * totalMie);
            }
            
            vec3 atmosphereNoMie(vec3 r, vec3 r0, vec3 pSun, float iSun, float rPlanet, float rAtmos, vec3 kRlh, float shRlh) {
                // Normalize the sun and view directions.
                pSun = normalize(pSun);
                r = normalize(r);
            
                // Calculate the step size of the primary ray.
                vec2 p = rsi(r0, r, rAtmos);
                if (p.x > p.y) return vec3(0,0,0);
                p.y = min(p.y, rsi(r0, r, rPlanet).x);
                float iStepSize = (p.y - p.x) / float(iSteps);
            
                // Initialize the primary ray time.
                float iTime = 0.0;
            
                // Initialize accumulators for Rayleigh.
                vec3 totalRlh = vec3(0,0,0);
            
                // Initialize optical depth accumulators for the primary ray.
                float iOdRlh = 0.0;
            
                // Calculate the Rayleigh phase.
                float mu = dot(r, pSun);
                float mumu = mu * mu;
                float pRlh = 3.0 / (16.0 * PI) * (1.0 + mumu);
            
                // Sample the primary ray.
                for (int i = 0; i < iSteps; i++) {
            
                    // Calculate the primary ray sample position.
                    vec3 iPos = r0 + r * (iTime + iStepSize * 0.5);
            
                    // Calculate the height of the sample.
                    float iHeight = length(iPos) - rPlanet;
            
                    // Calculate the optical depth of the Rayleigh scattering for this step.
                    float odStepRlh = exp(-iHeight / shRlh) * iStepSize;
            
                    // Accumulate optical depth.
                    iOdRlh += odStepRlh;
            
                    // Calculate the step size of the secondary ray.
                    float jStepSize = rsi(iPos, pSun, rAtmos).y / float(jSteps);
            
                    // Initialize the secondary ray time.
                    float jTime = 0.0;
            
                    // Initialize optical depth accumulators for the secondary ray.
                    float jOdRlh = 0.0;
            
                    // Sample the secondary ray.
                    for (int j = 0; j < jSteps; j++) {
            
                        // Calculate the secondary ray sample position.
                        vec3 jPos = iPos + pSun * (jTime + jStepSize * 0.5);
            
                        // Calculate the height of the sample.
                        float jHeight = length(jPos) - rPlanet;
            
                        // Accumulate the optical depth.
                        jOdRlh += exp(-jHeight / shRlh) * jStepSize;
            
                        // Increment the secondary ray time.
                        jTime += jStepSize;
                    }
            
                    // Calculate attenuation.
                    vec3 attn = exp(-(kRlh * (iOdRlh + jOdRlh)));
            
                    // Accumulate scattering.
                    totalRlh += odStepRlh * attn;
            
                    // Increment the primary ray time.
                    iTime += iStepSize;
            
                }
            
                // Calculate and return the final color.
                return iSun * (pRlh * kRlh * totalRlh);
            }
            
	    	void main()
	    	{
			    gl_Position = TransformSkyboxClip(vertexPosition);

                // Sky
                vDirection = vertexPosition;
                vSunDir = normalize(_SunDir);
                vSkyColor = atmosphere(
                    normalize(vDirection),                     // normalized ray direction
                    vec3(0,6372e3,0),               // ray origin
                    vSunDir,                        // position of the sun
                    22.0,                           // intensity of the sun
                    6371e3,                         // radius of the planet in meters
                    6471e3,                         // radius of the atmosphere in meters
                    vec3(5.5e-6, 13.0e-6, 22.4e-6), // Rayleigh scattering coefficient
                    21e-6,                          // Mie scattering coefficient
                    8e3,                            // Rayleigh scale height
                    1.2e3,                          // Mie scale height
                    0.758                           // Mie preferred scattering direction
                );
	    	}
	    }

	    Fragment
	    {
	    	layout(location = 0) out vec4 fragColor;

            in vec3 vSkyColor;
            in vec3 vDirection;
            in vec3 vSunDir;

	    	void main()
	    	{
                vec3 color = vSkyColor;
	            color.rgb += smoothstep(0.996, 0.9965, dot(normalize(vDirection), vSunDir)); // Sun
                color = 1.0 - exp(-1.0 * color); // Exposure
	    		fragColor = vec4(color, 1.0);
	    	}

	    }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            cbuffer SkyVS : register(b2)
            {
                float2 Resolution;
                float fogDensity;
                float3 _SunDir;
            };

            struct VSInput
            {
                float3 vertexPosition : POSITION;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float3 vSkyColor : TEXCOORD0;
                float3 vDirection : TEXCOORD1;
                float3 vSunDir : TEXCOORD2;
            };

            static const float PI = 3.141592;
            static const int iSteps = 16;
            static const int jSteps = 8;

            float2 rsi(float3 r0, float3 rd, float sr)
            {
                float a = dot(rd, rd);
                float b = 2.0 * dot(rd, r0);
                float c = dot(r0, r0) - (sr * sr);
                float d = (b * b) - 4.0 * a * c;
                if (d < 0.0) return float2(1e5, -1e5);
                return float2((-b - sqrt(d)) / (2.0 * a), (-b + sqrt(d)) / (2.0 * a));
            }

            float3 atmosphere(float3 r, float3 r0, float3 pSun, float iSun, float rPlanet, float rAtmos,
                float3 kRlh, float kMie, float shRlh, float shMie, float g)
            {
                pSun = normalize(pSun);
                r = normalize(r);
                float2 p = rsi(r0, r, rAtmos);
                if (p.x > p.y) return 0.0.xxx;
                p.y = min(p.y, rsi(r0, r, rPlanet).x);
                float iStepSize = (p.y - p.x) / float(iSteps);
                float iTime = 0.0;
                float3 totalRlh = 0.0.xxx;
                float3 totalMie = 0.0.xxx;
                float iOdRlh = 0.0;
                float iOdMie = 0.0;
                float mu = dot(r, pSun);
                float mumu = mu * mu;
                float gg = g * g;
                float pRlh = 3.0 / (16.0 * PI) * (1.0 + mumu);
                float pMie = 3.0 / (8.0 * PI) * ((1.0 - gg) * (mumu + 1.0)) / (pow(1.0 + gg - 2.0 * mu * g, 1.5) * (2.0 + gg));

                [loop] for (int i = 0; i < iSteps; i++)
                {
                    float3 iPos = r0 + r * (iTime + iStepSize * 0.5);
                    float iHeight = length(iPos) - rPlanet;
                    float odStepRlh = exp(-iHeight / shRlh) * iStepSize;
                    float odStepMie = exp(-iHeight / shMie) * iStepSize;
                    iOdRlh += odStepRlh;
                    iOdMie += odStepMie;
                    float jStepSize = rsi(iPos, pSun, rAtmos).y / float(jSteps);
                    float jTime = 0.0;
                    float jOdRlh = 0.0;
                    float jOdMie = 0.0;
                    [loop] for (int j = 0; j < jSteps; j++)
                    {
                        float3 jPos = iPos + pSun * (jTime + jStepSize * 0.5);
                        float jHeight = length(jPos) - rPlanet;
                        jOdRlh += exp(-jHeight / shRlh) * jStepSize;
                        jOdMie += exp(-jHeight / shMie) * jStepSize;
                        jTime += jStepSize;
                    }
                    float3 attn = exp(-(kMie * (iOdMie + jOdMie) + kRlh * (iOdRlh + jOdRlh)));
                    totalRlh += odStepRlh * attn;
                    totalMie += odStepMie * attn;
                    iTime += iStepSize;
                }
                return iSun * (pRlh * kRlh * totalRlh + pMie * kMie * totalMie);
            }

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformSkyboxClip(input.vertexPosition);
                o.vDirection = input.vertexPosition;
                o.vSunDir = normalize(_SunDir);
                o.vSkyColor = atmosphere(
                    normalize(o.vDirection),
                    float3(0, 6372e3, 0),
                    o.vSunDir,
                    22.0,
                    6371e3,
                    6471e3,
                    float3(5.5e-6, 13.0e-6, 22.4e-6),
                    21e-6,
                    8e3,
                    1.2e3,
                    0.758);
                return o;
            }
        }

        Fragment
        {
            struct PSInput
            {
                float4 position : SV_Position;
                float3 vSkyColor : TEXCOORD0;
                float3 vDirection : TEXCOORD1;
                float3 vSunDir : TEXCOORD2;
            };

            float4 main(PSInput input) : SV_Target
            {
                float3 color = input.vSkyColor;
                color.rgb += smoothstep(0.996, 0.9965, dot(normalize(input.vDirection), input.vSunDir));
                color = 1.0 - exp(-1.0 * color);
                return float4(color, 1.0);
            }
        }
    ENDSLANG
}
