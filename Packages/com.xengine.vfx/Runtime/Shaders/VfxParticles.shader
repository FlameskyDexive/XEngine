Shader "VFX/Particles"

Properties
{
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _MainTex ("Main Texture", Texture2D) = "white"
    _FlipbookColumns ("Flipbook Columns", Float) = 1.0
    _FlipbookRows ("Flipbook Rows", Float) = 1.0
    _FlipbookBlend ("Flipbook Blend", Float) = 0.0
    _MotionVectorMode ("Motion Vector Mode", Float) = 0.0
    _MotionVectorScale ("Motion Vector Scale", Float) = 1.0
    _OrientMode ("Orient Mode", Float) = 0.0
    _OutputMode ("Output Mode", Float) = 0.0
    _LitMode ("Lit Mode", Float) = 0.0
    _LightDirection ("Light Direction", Vector3) = (0.35, 0.85, 0.4)
    _LightColor ("Light Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _AmbientColor ("Ambient Color", Color) = (0.18, 0.18, 0.22, 32.0)
    _SoftParticleFade ("Soft Particle Fade", Float) = 0.0
    _DistortionIntensity ("Distortion Intensity", Float) = 0.0
    _CameraFadeNear ("Camera Fade Near", Float) = 0.0
    _CameraFadeFar ("Camera Fade Far", Float) = 0.0
    _CameraDepthTexture ("Camera Depth", Texture2D) = "white"
}

Pass "Init"
{
    SLANGPROGRAM
    Compute
    {
        struct VfxParticle
        {
            float3 position;
            float size;
            float3 velocity;
            float pad0;
            float age;
            float lifetime;
            uint alive;
            uint pad1;
            float4 color;
        };

        struct VfxSimParams
        {
            float deltaTime;
            uint capacity;
            uint spawnCount;
            uint indexCount;
            float3 emitterPosition;
            float startSpeed;
            float3 gravity;
            float lifetime;
            float4 startColor;
            float startSize;
            float collisionMode;
            float collisionBounce;
            float sdfMode;
            float4 collisionPlane;
            float4 sdfCenter;
            float4 sdfSize;
            float turbulenceIntensity;
            float turbulenceFrequency;
            float eventKind;
            float eventCount;
            float conformAttraction;
            float vectorFieldIntensity;
            float attributeMapMode;
            float attributeMapScale;
        };

        RWStructuredBuffer<VfxParticle> _Particles : register(u0);
        RWStructuredBuffer<uint> _Counters : register(u1);
        RWStructuredBuffer<VfxSimParams> _Params : register(u2);
        [[vk::binding(10, 0)]] Texture2D _AttributeMap : register(t0);
        [[vk::binding(11, 0)]] SamplerState _AttributeMapSampler : register(s0);

        float4 VfxSampleAttributeMap(uint slot)
        {
            uint tw = 1u;
            uint th = 1u;
            _AttributeMap.GetDimensions(tw, th);
            tw = max(tw, 1u);
            th = max(th, 1u);
            uint n = tw * th;
            uint i = slot % n;
            float2 uv = float2((i % tw + 0.5f) / (float)tw, (i / tw + 0.5f) / (float)th);
            return _AttributeMap.SampleLevel(_AttributeMapSampler, uv, 0.0f);
        }

        void VfxApplyAttributeMap(inout VfxParticle particle, VfxSimParams p, uint slot)
        {
            float mode = p.attributeMapMode;
            if (mode < 0.5f)
                return;
            float4 s = VfxSampleAttributeMap(slot);
            if (mode < 1.5f || mode >= 3.5f)
                particle.position = p.emitterPosition + (s.rgb * 2.0f - 1.0f) * p.attributeMapScale;
            if ((mode >= 1.5f && mode < 2.5f) || mode >= 3.5f)
                particle.color = s;
            if (mode >= 2.5f && mode < 3.5f)
                particle.size = p.startSize * max(s.r, 0.0f);
        }

        uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        float3 HashDir(uint n)
        {
            uint h = Hash(n);
            float z = ((h & 0xffffu) / 32767.5f) - 1.0f;
            float a = (((h >> 16) & 0xffffu) / 65535.0f) * 6.2831853f;
            float r = sqrt(max(0.0f, 1.0f - z * z));
            return float3(cos(a) * r, z, sin(a) * r);
        }

        void VfxPositionShape(uint slot, float mode, float sx, float sy, float sz, out float3 offset)
        {
            offset = float3(0.0, 0.0, 0.0);
            if (mode <= 0.0)
                return;
            if (mode < 1.5)
            {
                float3 d = HashDir(slot + 91u);
                float u = (Hash(slot + 7u) & 0xffffu) / 65535.0;
                offset = d * (sx * pow(max(u, 0.0), 0.3333333));
                return;
            }
            if (mode < 2.5)
            {
                uint h = Hash(slot + 13u);
                offset = float3(
                    ((h & 0xffu) / 127.5 - 1.0) * sx,
                    (((h >> 8) & 0xffu) / 127.5 - 1.0) * sy,
                    (((h >> 16) & 0xffu) / 127.5 - 1.0) * sz);
                return;
            }
            if (mode < 3.5)
            {
                float t = (Hash(slot + 19u) & 0xffffu) / 65535.0;
                float ang = (((Hash(slot + 23u) >> 16) & 0xffffu) / 65535.0) * 6.2831853;
                float rad = sx * t;
                offset = float3(cos(ang) * rad, t * sy, sin(ang) * rad);
                return;
            }
            float cang = (((Hash(slot + 29u) >> 16) & 0xffffu) / 65535.0) * 6.2831853;
            float cr = sx * ((Hash(slot + 31u) & 0xffffu) / 65535.0);
            offset = float3(cos(cang) * cr, 0.0, sin(cang) * cr);
        }

        void VfxSequential(uint slot, float mode, float sx, float sy, float sz, out float3 offset)
        {
            offset = float3(0.0, 0.0, 0.0);
            if (mode <= 0.0)
                return;
            float n = max(sz, 1.0);
            uint count = (uint)n;
            if (count < 1u)
                count = 1u;
            uint idx = slot % count;
            float t = count <= 1u ? 0.0 : idx / (float)(count - 1u);
            if (mode < 1.5)
            {
                float ang = idx * (6.2831853 / n);
                offset = float3(cos(ang) * sx, 0.0, sin(ang) * sx);
                return;
            }
            float along = t * 2.0 - 1.0;
            if (mode < 2.5)
                offset = float3(along * sx, 0.0, 0.0);
            else
                offset = float3(0.0, along * sy, 0.0);
        }

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            VfxSimParams p = _Params[0];
            if (id.x >= p.spawnCount)
                return;

            uint slot;
            InterlockedAdd(_Counters[1], 1, slot);
            slot = slot % max(p.capacity, 1u);

            VfxParticle particle;
            particle.position = p.emitterPosition;
            particle.size = p.startSize;
            particle.velocity = HashDir(slot + p.spawnCount) * p.startSpeed;
            particle.pad0 = 0.0f;
            particle.age = 0.0f;
            particle.lifetime = p.lifetime;
            particle.alive = 1u;
            particle.pad1 = 0u;
            particle.color = p.startColor;
            // VFXGRAPH_INIT_BLOCKS
            VfxApplyAttributeMap(particle, p, slot);
            _Particles[slot] = particle;
        }
    }
    ENDSLANG
}

Pass "Update"
{
    SLANGPROGRAM
    Compute
    {
        struct VfxParticle
        {
            float3 position;
            float size;
            float3 velocity;
            float pad0;
            float age;
            float lifetime;
            uint alive;
            uint pad1;
            float4 color;
        };

        struct VfxSimParams
        {
            float deltaTime;
            uint capacity;
            uint spawnCount;
            uint indexCount;
            float3 emitterPosition;
            float startSpeed;
            float3 gravity;
            float lifetime;
            float4 startColor;
            float startSize;
            float collisionMode;
            float collisionBounce;
            float sdfMode;
            float4 collisionPlane;
            float4 sdfCenter;
            float4 sdfSize;
            float turbulenceIntensity;
            float turbulenceFrequency;
            float eventKind;
            float eventCount;
            float conformAttraction;
            float vectorFieldIntensity;
            float attributeMapMode;
            float attributeMapScale;
        };

        RWStructuredBuffer<VfxParticle> _Particles : register(u0);
        RWStructuredBuffer<uint> _Counters : register(u1);
        RWStructuredBuffer<VfxSimParams> _Params : register(u2);
        RWStructuredBuffer<uint> _LiveList : register(u3);
        [[vk::binding(4, 0)]] Texture3D _SdfVolume : register(t0);
        [[vk::binding(5, 0)]] SamplerState _SdfVolumeSampler : register(s0);
        [[vk::binding(6, 0)]] Texture2D _VfxCameraDepth : register(t1);
        [[vk::binding(7, 0)]] SamplerState _VfxCameraDepthSampler : register(s1);
        [[vk::binding(8, 0)]] Texture3D _VectorField : register(t2);
        [[vk::binding(9, 0)]] SamplerState _VectorFieldSampler : register(s2);

        float3 VfxSampleVectorField(float3 pos, float3 center, float3 size)
        {
            float3 ext = max(size, float3(1e-5f, 1e-5f, 1e-5f));
            float3 uv = (pos - (center - ext)) / (2.0f * ext);
            if (any(uv < 0.0f) || any(uv > 1.0f))
                return float3(0.0f, 0.0f, 0.0f);
            return _VectorField.SampleLevel(_VectorFieldSampler, uv, 0.0f).rgb * 2.0f - 1.0f;
        }

        bool VfxDepthProject(float3 pos, out float2 uv, out float particleEye)
        {
            float3 cam = float3(0.0f, 1.2f, 3.2f);
            float3 target = float3(0.0f, 0.4f, 0.0f);
            float3 f = target - cam;
            float flen2 = dot(f, f);
            f *= rsqrt(max(flen2, 1e-12f));
            float3 s = cross(f, float3(0.0f, 1.0f, 0.0f));
            float slen2 = dot(s, s);
            if (slen2 <= 1e-12f)
                s = float3(1.0f, 0.0f, 0.0f);
            else
                s *= rsqrt(slen2);
            float3 up = cross(s, f);
            float3 rel = pos - cam;
            float z = dot(rel, f);
            uv = float2(0.0f, 0.0f);
            particleEye = 0.0f;
            if (z <= 0.1f)
                return false;
            float x = dot(rel, s);
            float y = dot(rel, up);
            uv.x = 0.5f + 0.5f * (x / (z * 0.57735027f * 1.7777778f));
            uv.y = 0.5f + 0.5f * (y / (z * 0.57735027f));
            particleEye = z;
            return uv.x >= 0.0f && uv.x <= 1.0f && uv.y >= 0.0f && uv.y <= 1.0f;
        }

        float VfxDepthLinearEye(float raw)
        {
            float clipZ = raw * 2.0f - 1.0f;
            return (2.0f * 0.1f * 100.0f) / (100.0f + 0.1f - clipZ * (100.0f - 0.1f));
        }

        float VfxSampleVolumeSdf(float3 pos, float3 center, float3 size)
        {
            float3 ext = max(size, float3(1e-5f, 1e-5f, 1e-5f));
            float3 uv = (pos - (center - ext)) / (2.0f * ext);
            if (any(uv < 0.0f) || any(uv > 1.0f))
                return 1e6f;
            return _SdfVolume.SampleLevel(_SdfVolumeSampler, uv, 0.0f).r;
        }

        float VfxSampleCylinder(float3 p, float r, float h)
        {
            float2 d = abs(float2(length(p.xz), p.y)) - float2(max(r, 0.0f), max(h, 1e-5f));
            return min(max(d.x, d.y), 0.0f) + length(max(d, 0.0f));
        }

        float VfxSampleCone(float3 p, float h, float r1, float r2)
        {
            h = max(h, 1e-5f);
            r1 = max(r1, 0.0f);
            r2 = max(r2, 0.0f);
            float2 q = float2(length(p.xz), p.y);
            float2 k1 = float2(r2, h);
            float2 k2 = float2(r2 - r1, 2.0f * h);
            float2 ca = float2(q.x - min(q.x, (q.y < 0.0f) ? r1 : r2), abs(q.y) - h);
            float k2len2 = dot(k2, k2);
            float t = k2len2 > 1e-12f ? saturate(dot(k1 - q, k2) / k2len2) : 0.0f;
            float2 cb = q - k1 + k2 * t;
            float s = (cb.x < 0.0f && ca.y < 0.0f) ? -1.0f : 1.0f;
            return s * sqrt(min(dot(ca, ca), dot(cb, cb)));
        }

        float VfxSampleSdf(float3 pos, float3 center, float3 size, float shape)
        {
            float3 d = pos - center;
            if (shape < 1.5f)
                return length(d) - max(size.x, 0.0f);
            if (shape < 2.5f)
            {
                float3 q = abs(d) - size;
                float3 m = max(q, 0.0f);
                return length(m) + min(max(q.x, max(q.y, q.z)), 0.0f);
            }
            if (shape < 3.5f)
                return VfxSampleVolumeSdf(pos, center, size);
            if (shape < 4.5f)
                return VfxSampleCylinder(d, size.x, size.y);
            return VfxSampleCone(d, size.y, size.x, size.z);
        }

        float VfxHash(float3 p)
        {
            p = frac(p * 0.1031f);
            float d = p.x * (p.y + 33.33f) + p.y * (p.z + 33.33f) + p.z * (p.x + 33.33f);
            p += d;
            return frac((p.x + p.y) * p.z);
        }

        float VfxNoise(float3 p)
        {
            float3 i = floor(p);
            float3 f = p - i;
            float3 u = f * f * (3.0f - 2.0f * f);
            float n000 = VfxHash(i);
            float n100 = VfxHash(i + float3(1.0f, 0.0f, 0.0f));
            float n010 = VfxHash(i + float3(0.0f, 1.0f, 0.0f));
            float n110 = VfxHash(i + float3(1.0f, 1.0f, 0.0f));
            float n001 = VfxHash(i + float3(0.0f, 0.0f, 1.0f));
            float n101 = VfxHash(i + float3(1.0f, 0.0f, 1.0f));
            float n011 = VfxHash(i + float3(0.0f, 1.0f, 1.0f));
            float n111 = VfxHash(i + float3(1.0f, 1.0f, 1.0f));
            float n00 = n000 + (n100 - n000) * u.x;
            float n10 = n010 + (n110 - n010) * u.x;
            float n01 = n001 + (n101 - n001) * u.x;
            float n11 = n011 + (n111 - n011) * u.x;
            float n0 = n00 + (n10 - n00) * u.y;
            float n1 = n01 + (n11 - n01) * u.y;
            return n0 + (n1 - n0) * u.z;
        }

        float3 VfxPotential(float3 p)
        {
            return float3(
                VfxNoise(p),
                VfxNoise(p + float3(17.1f, 9.2f, 3.4f)),
                VfxNoise(p + float3(31.7f, 5.8f, 13.3f)));
        }

        float3 VfxCurl(float3 p)
        {
            float e = 0.15f;
            float3 yp = VfxPotential(p + float3(0.0f, e, 0.0f));
            float3 ym = VfxPotential(p - float3(0.0f, e, 0.0f));
            float3 zp = VfxPotential(p + float3(0.0f, 0.0f, e));
            float3 zm = VfxPotential(p - float3(0.0f, 0.0f, e));
            float3 xp = VfxPotential(p + float3(e, 0.0f, 0.0f));
            float3 xm = VfxPotential(p - float3(e, 0.0f, 0.0f));
            return float3(yp.z - ym.z - (zp.y - zm.y), zp.x - zm.x - (xp.z - xm.z), xp.y - xm.y - (yp.x - ym.x)) * (1.0f / (2.0f * e));
        }

        void VfxDrag(inout float3 velocity, float amount, float dt)
        {
            if (amount <= 0.0f)
                return;
            velocity *= exp(-amount * dt);
        }

        void VfxForce(inout float3 velocity, float3 force, float dt)
        {
            if (dot(force, force) <= 0.0f)
                return;
            velocity += force * dt;
        }

        void VfxCollisionFriction(inout float3 velocity, float3 n, float amount)
        {
            if (amount <= 0.0f)
                return;
            float n2 = dot(n, n);
            if (n2 <= 1e-10f)
                return;
            n *= rsqrt(n2);
            float vn = dot(velocity, n);
            float3 vt = velocity - vn * n;
            velocity = vn * n + vt * saturate(1.0f - amount);
        }

        void VfxCollisionElasticity(inout float3 velocity, float3 n, float amount)
        {
            if (amount <= 0.0f)
                return;
            float n2 = dot(n, n);
            if (n2 <= 1e-10f)
                return;
            n *= rsqrt(n2);
            float vn = dot(velocity, n);
            float3 vt = velocity - vn * n;
            velocity = vt + vn * n * saturate(1.0f - amount);
        }

        void VfxCollisionLifetimeLoss(inout float lifetime, float age, float amount)
        {
            if (amount <= 0.0f)
                return;
            lifetime = age + (lifetime - age) * saturate(1.0f - amount);
        }

        void VfxLimitVelocity(inout float3 velocity, float maxSpeed)
        {
            if (maxSpeed <= 0.0f)
                return;
            float spd2 = dot(velocity, velocity);
            float max2 = maxSpeed * maxSpeed;
            if (spd2 <= max2)
                return;
            velocity *= maxSpeed * rsqrt(spd2);
        }

        void VfxKillSphere(inout uint alive, float3 position, float3 center, float radius)
        {
            if (radius <= 0.0f)
                return;
            float3 d = position - center;
            if (dot(d, d) <= radius * radius)
                alive = 0u;
        }

        void VfxKillAABox(inout uint alive, float3 position, float3 center, float3 size)
        {
            if (size.x <= 0.0f || size.y <= 0.0f || size.z <= 0.0f)
                return;
            float3 d = abs(position - center);
            if (d.x <= size.x && d.y <= size.y && d.z <= size.z)
                alive = 0u;
        }

        void VfxKillPlane(inout uint alive, float3 position, float3 n, float d)
        {
            float n2 = dot(n, n);
            if (n2 <= 1e-10)
                return;
            n *= rsqrt(n2);
            if (dot(position, n) - d < 0.0)
                alive = 0u;
        }

        void VfxAttract(inout float3 velocity, float3 position, float3 target, float strength, float dt)
        {
            if (strength <= 0.0f)
                return;
            float3 d = target - position;
            float d2 = dot(d, d);
            if (d2 <= 1e-10f)
                return;
            velocity += d * rsqrt(d2) * strength * dt;
        }

        void VfxConformToSphere(inout float3 velocity, float3 position, float3 center, float radius, float strength, float dt)
        {
            if (strength <= 0.0)
                return;
            float3 d = position - center;
            float d2 = dot(d, d);
            if (d2 <= 1e-10)
                return;
            float inv = rsqrt(d2);
            float dist = d2 * inv;
            velocity += d * inv * (radius - dist) * strength * dt;
        }

        void VfxVortex(inout float3 velocity, float3 position, float3 center, float3 axis, float rotation, float attraction, float dt)
        {
            if (rotation == 0.0 && attraction == 0.0)
                return;
            float a2 = dot(axis, axis);
            if (a2 <= 1e-10)
                return;
            axis *= rsqrt(a2);
            float3 r = position - center;
            float3 radial = r - axis * dot(r, axis);
            float r2 = dot(radial, radial);
            if (r2 <= 1e-10)
                return;
            float3 n = radial * rsqrt(r2);
            float3 tangent = cross(axis, n);
            velocity += tangent * rotation * dt - n * attraction * dt;
        }

        void VfxMultiplySize(inout float size, float amount)
        {
            if (amount == 1.0)
                return;
            size *= amount;
        }

        void VfxMultiplyColor(inout float4 color, float4 amount)
        {
            if (amount.x == 1.0 && amount.y == 1.0 && amount.z == 1.0 && amount.w == 1.0)
                return;
            color *= amount;
        }

        void VfxAddSize(inout float size, float amount)
        {
            if (amount == 0.0)
                return;
            size += amount;
        }

        void VfxAddColor(inout float4 color, float4 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0 && amount.w == 0.0)
                return;
            color += amount;
        }

        void VfxAddVelocity(inout float3 velocity, float3 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0)
                return;
            velocity += amount;
        }

        void VfxAddPosition(inout float3 position, float3 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0)
                return;
            position += amount;
        }

        void VfxSetPosition(inout float3 position, float3 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0)
                return;
            position = amount;
        }

        void VfxMultiplyVelocity(inout float3 velocity, float amount)
        {
            if (amount == 1.0)
                return;
            velocity *= amount;
        }

        void VfxSetSize(inout float size, float amount)
        {
            if (amount == 0.0)
                return;
            size = amount;
        }

        void VfxSetColor(inout float4 color, float4 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0 && amount.w == 0.0)
                return;
            color = amount;
        }

        void VfxSetVelocity(inout float3 velocity, float3 amount)
        {
            if (amount.x == 0.0 && amount.y == 0.0 && amount.z == 0.0)
                return;
            velocity = amount;
        }

        void VfxSetLifetime(inout float lifetime, float amount)
        {
            if (amount == 0.0)
                return;
            lifetime = amount;
        }

        void VfxAddLifetime(inout float lifetime, float amount)
        {
            if (amount == 0.0)
                return;
            lifetime += amount;
        }

        void VfxMultiplyLifetime(inout float lifetime, float amount)
        {
            if (amount == 1.0)
                return;
            lifetime *= amount;
        }

        void VfxAddAge(inout float age, float amount)
        {
            if (amount == 0.0)
                return;
            age += amount;
        }

        void VfxMultiplyAge(inout float age, float amount)
        {
            if (amount == 1.0)
                return;
            age *= amount;
        }

        void VfxSetAge(inout float age, float amount)
        {
            if (amount == 0.0)
                return;
            age = amount;
        }

        void VfxConformToCylinder(inout float3 velocity, float3 position, float3 center, float3 axis, float radius, float strength, float dt)
        {
            if (strength <= 0.0)
                return;
            float a2 = dot(axis, axis);
            if (a2 <= 1e-10)
                return;
            axis *= rsqrt(a2);
            float3 r = position - center;
            float3 radial = r - axis * dot(r, axis);
            float r2 = dot(radial, radial);
            if (r2 <= 1e-10)
                return;
            float inv = rsqrt(r2);
            float dist = r2 * inv;
            velocity += radial * inv * (radius - dist) * strength * dt;
        }

        void VfxAngularVelocity(inout float angle, float rate, float dt)
        {
            angle += rate * dt;
        }

        uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        float3 HashDir(uint n)
        {
            uint h = Hash(n);
            float z = ((h & 0xffffu) / 32767.5f) - 1.0f;
            float a = (((h >> 16) & 0xffffu) / 65535.0f) * 6.2831853f;
            float r = sqrt(max(0.0f, 1.0f - z * z));
            return float3(cos(a) * r, z, sin(a) * r);
        }

        uint VfxEventEmitCount(float kind, float count, bool died, bool collided)
        {
            if (count < 0.5f)
                return 0u;
            if (kind >= 0.5f && kind < 1.5f && died)
                return (uint)count;
            if (kind >= 1.5f && kind < 2.5f && collided)
                return (uint)count;
            return 0u;
        }

        void VfxEmitGpuEvent(VfxParticle particle, VfxSimParams p, bool died, bool collided)
        {
            uint n = VfxEventEmitCount(p.eventKind, p.eventCount, died, collided);
            if (n == 0u)
                return;
            uint previous;
            InterlockedAdd(_Counters[0], n, previous);
            if (!died)
                return;
            uint spawnN = n > 8u ? 8u : n;
            for (uint k = 0u; k < spawnN; k++)
            {
                uint slot;
                InterlockedAdd(_Counters[1], 1, slot);
                slot = slot % max(p.capacity, 1u);
                VfxParticle child;
                child.position = particle.position;
                child.size = p.startSize * 0.5f;
                child.velocity = HashDir(slot + k + 17u) * p.startSpeed;
                child.pad0 = 0.0f;
                child.age = 0.0f;
                child.lifetime = max(p.lifetime * 0.35f, 0.05f);
                child.alive = 1u;
                child.pad1 = 1u;
                child.color = p.startColor;
                _Particles[slot] = child;
            }
        }

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            VfxSimParams p = _Params[0];
            uint i = id.x;
            if (i >= p.capacity)
                return;

            VfxParticle particle = _Particles[i];
            if (particle.alive == 0u)
                return;

            bool died = false;
            bool collided = false;
            float3 vfxHitN = float3(0.0, 1.0, 0.0);

            particle.age += p.deltaTime;
            if (particle.age >= particle.lifetime)
            {
                particle.alive = 0u;
                died = true;
                _Particles[i] = particle;
                VfxEmitGpuEvent(particle, p, died, collided);
                return;
            }

            particle.velocity += p.gravity * p.deltaTime;
            if (p.turbulenceIntensity > 0.0f)
            {
                float freq = p.turbulenceFrequency > 1e-5f ? p.turbulenceFrequency : 1e-5f;
                particle.velocity += VfxCurl(particle.position * freq) * p.turbulenceIntensity * p.deltaTime;
            }
            if (p.vectorFieldIntensity > 0.0f)
            {
                float3 field = VfxSampleVectorField(particle.position, p.sdfCenter.xyz, p.sdfSize.xyz);
                particle.velocity += field * p.vectorFieldIntensity * p.deltaTime;
            }
            particle.position += particle.velocity * p.deltaTime;
            {
                float mode = p.collisionMode;
                if (mode >= 0.5f)
                {
                    float3 n = p.collisionPlane.xyz;
                    float nlen2 = dot(n, n);
                    if (nlen2 > 1e-8f)
                    {
                        n *= rsqrt(nlen2);
                        float dist = dot(particle.position, n) + p.collisionPlane.w;
                        if (dist < 0.0f)
                        {
                            collided = true;
                            if (mode < 1.5f)
                            {
                                particle.alive = 0u;
                                died = true;
                                _Particles[i] = particle;
                                VfxEmitGpuEvent(particle, p, died, collided);
                                return;
                            }
                            particle.position -= n * dist;
                            vfxHitN = n;
                            float vn = dot(particle.velocity, n);
                            if (vn < 0.0f)
                                particle.velocity -= (1.0f + p.collisionBounce) * vn * n;
                        }
                    }
                }
            }
            {
                float sdfMode = p.sdfMode;
                float sdfShape = p.sdfSize.w;
                if (sdfMode >= 0.5f && sdfShape >= 0.5f)
                {
                    float3 c = p.sdfCenter.xyz;
                    float3 b = p.sdfSize.xyz;
                    float dist = VfxSampleSdf(particle.position, c, b, sdfShape) - particle.size * 0.5f;
                    if (dist < 0.0f)
                    {
                        collided = true;
                        if (sdfMode < 1.5f)
                        {
                            particle.alive = 0u;
                            died = true;
                            _Particles[i] = particle;
                            VfxEmitGpuEvent(particle, p, died, collided);
                            return;
                        }
                        float e = 1e-3f;
                        float3 n = float3(
                            VfxSampleSdf(particle.position + float3(e, 0.0f, 0.0f), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(e, 0.0f, 0.0f), c, b, sdfShape),
                            VfxSampleSdf(particle.position + float3(0.0f, e, 0.0f), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(0.0f, e, 0.0f), c, b, sdfShape),
                            VfxSampleSdf(particle.position + float3(0.0f, 0.0f, e), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(0.0f, 0.0f, e), c, b, sdfShape));
                        float nlen2 = dot(n, n);
                        if (nlen2 > 1e-10f)
                            n *= rsqrt(nlen2);
                        else
                            n = float3(0.0f, 1.0f, 0.0f);
                        particle.position -= n * dist;
                        vfxHitN = n;
                        float vn = dot(particle.velocity, n);
                        if (vn < 0.0f)
                            particle.velocity -= (1.0f + p.collisionBounce) * vn * n;
                    }
                }
            }
            {
                float depthMode = p.sdfCenter.w;
                if (depthMode >= 0.5f)
                {
                    float2 uv;
                    float particleEye;
                    if (VfxDepthProject(particle.position, uv, particleEye))
                    {
                        float raw = _VfxCameraDepth.SampleLevel(_VfxCameraDepthSampler, uv, 0.0f).r;
                        float sceneEye = VfxDepthLinearEye(raw);
                        float dist = (sceneEye - 0.05f) - particleEye;
                        if (dist < 0.0f)
                        {
                            collided = true;
                            if (depthMode < 1.5f)
                            {
                                particle.alive = 0u;
                                died = true;
                                _Particles[i] = particle;
                                VfxEmitGpuEvent(particle, p, died, collided);
                                return;
                            }
                            float3 n = float3(0.0f, 1.2f, 3.2f) - particle.position;
                            float nlen2 = dot(n, n);
                            if (nlen2 > 1e-10f)
                                n *= rsqrt(nlen2);
                            else
                                n = float3(0.0f, 1.0f, 0.0f);
                            particle.position -= n * dist;
                            vfxHitN = n;
                            float vn = dot(particle.velocity, n);
                            if (vn < 0.0f)
                                particle.velocity -= (1.0f + p.collisionBounce) * vn * n;
                        }
                    }
                }
            }
            {
                float attraction = p.conformAttraction;
                float sdfShape = p.sdfSize.w;
                if (attraction > 0.0f && sdfShape >= 0.5f)
                {
                    float3 c = p.sdfCenter.xyz;
                    float3 b = p.sdfSize.xyz;
                    float dist = VfxSampleSdf(particle.position, c, b, sdfShape);
                    if (dist < 1e5f)
                    {
                        float e = 1e-3f;
                        float3 n = float3(
                            VfxSampleSdf(particle.position + float3(e, 0.0f, 0.0f), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(e, 0.0f, 0.0f), c, b, sdfShape),
                            VfxSampleSdf(particle.position + float3(0.0f, e, 0.0f), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(0.0f, e, 0.0f), c, b, sdfShape),
                            VfxSampleSdf(particle.position + float3(0.0f, 0.0f, e), c, b, sdfShape)
                                - VfxSampleSdf(particle.position - float3(0.0f, 0.0f, e), c, b, sdfShape));
                        float nlen2 = dot(n, n);
                        if (nlen2 > 1e-10f)
                            n *= rsqrt(nlen2);
                        else
                            n = float3(0.0f, 1.0f, 0.0f);
                        float k = attraction * p.deltaTime;
                        if (k > 1.0f)
                            k = 1.0f;
                        particle.position -= n * dist * k;
                    }
                }
            }
            // VFXGRAPH_UPDATE_BLOCKS
            _Particles[i] = particle;
            if (collided)
                VfxEmitGpuEvent(particle, p, false, true);

            uint live;
            InterlockedAdd(_Counters[2], 1, live);
            _LiveList[live] = i;
        }
    }
    ENDSLANG
}

Pass "Sort"
{
    SLANGPROGRAM
    Compute
    {
        struct VfxParticle
        {
            float3 position;
            float size;
            float3 velocity;
            float pad0;
            float age;
            float lifetime;
            uint alive;
            uint pad1;
            float4 color;
        };

        struct VfxSimParams
        {
            float deltaTime;
            uint capacity;
            uint spawnCount;
            uint indexCount;
            float3 emitterPosition;
            float startSpeed;
            float3 gravity;
            float lifetime;
            float4 startColor;
            float startSize;
            float collisionMode;
            float collisionBounce;
            float sdfMode;
            float4 collisionPlane;
            float4 sdfCenter;
            float4 sdfSize;
            float turbulenceIntensity;
            float turbulenceFrequency;
            float eventKind;
            float eventCount;
            float conformAttraction;
            float vectorFieldIntensity;
            float attributeMapMode;
            float attributeMapScale;
        };

        RWStructuredBuffer<VfxParticle> _Particles : register(u0);
        RWStructuredBuffer<uint> _Counters : register(u1);
        RWStructuredBuffer<VfxSimParams> _Params : register(u2);
        RWStructuredBuffer<uint> _LiveList : register(u3);

        float VfxSortDist2(float3 pos, float3 cam)
        {
            float3 d = pos - cam;
            return dot(d, d);
        }

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            VfxSimParams p = _Params[0];
            if ((p.indexCount >> 24) == 0u)
                return;
            uint n = _Counters[2];
            uint i = id.x;
            uint k = (uint)p.startSpeed;
            uint j = (uint)p.startSize;
            uint ixj = i ^ j;
            if (ixj <= i || i >= n || ixj >= n)
                return;
            uint a = _LiveList[i];
            uint b = _LiveList[ixj];
            float da = VfxSortDist2(_Particles[a].position, p.emitterPosition);
            float db = VfxSortDist2(_Particles[b].position, p.emitterPosition);
            bool ascending = (i & k) == 0u;
            bool swap = ascending ? (da < db) : (da > db);
            if (swap)
            {
                _LiveList[i] = b;
                _LiveList[ixj] = a;
            }
        }
    }
    ENDSLANG
}

Pass "WriteArgs"
{
    SLANGPROGRAM
    Compute
    {
        struct VfxSimParams
        {
            float deltaTime;
            uint capacity;
            uint spawnCount;
            uint indexCount;
            float3 emitterPosition;
            float startSpeed;
            float3 gravity;
            float lifetime;
            float4 startColor;
            float startSize;
            float collisionMode;
            float collisionBounce;
            float sdfMode;
            float4 collisionPlane;
            float4 sdfCenter;
            float4 sdfSize;
            float turbulenceIntensity;
            float turbulenceFrequency;
            float eventKind;
            float eventCount;
            float conformAttraction;
            float vectorFieldIntensity;
            float attributeMapMode;
            float attributeMapScale;
        };

        RWStructuredBuffer<uint> _Counters : register(u1);
        RWStructuredBuffer<VfxSimParams> _Params : register(u2);
        RWStructuredBuffer<uint> _DrawArgs : register(u4);

        [numthreads(1, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            VfxSimParams p = _Params[0];
            uint packed = p.indexCount;
            uint indices = packed & 0xffffu;
            uint mode = packed >> 16;
            if (indices < 3u)
                indices = 6u;
            uint instances = _Counters[2];
            if (mode >= 2u && instances > 0u)
                instances -= 1u;
            _DrawArgs[0] = indices;
            _DrawArgs[1] = instances;
            _DrawArgs[2] = 0u;
            _DrawArgs[3] = 0u;
            _DrawArgs[4] = 0u;
        }
    }
    ENDSLANG
}

Pass "Output"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    ZWrite Off
    Blend Alpha

    SLANGPROGRAM
    Vertex
    {
        cbuffer GlobalUniforms : register(b0)
        {
            float4x4 xengine_MatV;
            float4x4 xengine_MatIV;
            float4x4 xengine_MatP;
            float4x4 xengine_MatVP;
            float4x4 xengine_PrevViewProj;
            float4x4 xengine_MatIP;
            float4x4 xengine_MatIVP;
            float4x4 xengine_MatVP_NonJittered;
            float3 _WorldSpaceCameraPos;
            float _padding0;
            float4 _ProjectionParams;
            float4 _ScreenParams;
            float2 _CameraJitter;
            float2 _CameraPreviousJitter;
            float4 _Time;
            float4 _SinTime;
            float4 _CosTime;
            float4 xengine_DeltaTime;
        };

        struct VfxParticle
        {
            float3 position;
            float size;
            float3 velocity;
            float pad0;
            float age;
            float lifetime;
            uint alive;
            uint pad1;
            float4 color;
        };

        StructuredBuffer<VfxParticle> _Particles : register(t0);
        StructuredBuffer<uint> _LiveList : register(t1);

        cbuffer VfxOutputMaterial : register(b2)
        {
            float4 _MainColor;
            float _FlipbookColumns;
            float _FlipbookRows;
            float _OutputMode;
            float _LitMode;
            float4 _LightDirection;
            float4 _LightColor;
            float4 _AmbientColor;
            float _SoftParticleFade;
            float _DistortionIntensity;
            float _CameraFadeNear;
            float _CameraFadeFar;
            float4 _DecalPlane;
            float _FlipbookBlend;
            float _MotionVectorMode;
            float _MotionVectorScale;
            float _OrientMode;
        };

        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord0 : TEXCOORD0;
            uint instanceId : SV_InstanceID;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 texCoord0 : TEXCOORD0;
            float2 flipbookUv : TEXCOORD1;
            float4 color : COLOR0;
            float outputMode : TEXCOORD2;
            float3 worldNormal : TEXCOORD3;
            float3 viewDir : TEXCOORD4;
            float particleEyeZ : TEXCOORD5;
            float2 flipbookUvNext : TEXCOORD6;
            float flipbookBlend : TEXCOORD7;
            float2 motion : TEXCOORD8;
        };

        float2 VfxMotionNdcDelta(float4 curr, float4 prev)
        {
            float2 c = curr.xy / max(curr.w, 1e-5);
            float2 p = prev.xy / max(prev.w, 1e-5);
            return (c - p) * 0.5;
        }

        void VfxOrientLookAt(float3 position, float3 target, out float3 along)
        {
            along = target - position;
        }

        void VfxOrientAlongVelocity(float3 vel, float3 toCam, float3 local, float size, float3 center, out float3 world)
        {
            float3 along = vel;
            float a2 = dot(along, along);
            if (a2 <= 1e-10)
                along = float3(0.0, 1.0, 0.0);
            else
                along *= rsqrt(a2);
            float3 side = cross(along, toCam);
            float s2 = dot(side, side);
            if (s2 <= 1e-10)
            {
                side = cross(along, float3(0.0, 0.0, 1.0));
                s2 = dot(side, side);
            }
            if (s2 <= 1e-10)
                side = float3(1.0, 0.0, 0.0);
            else
                side *= rsqrt(s2);
            float3 up = cross(side, along);
            world = center + (side * local.x + along * local.y + up * local.z) * size;
        }

        void VfxSpinLocal(float2 local, float angle, out float2 spun)
        {
            if (angle == 0.0)
            {
                spun = local;
                return;
            }
            float c = cos(angle);
            float s = sin(angle);
            spun = float2(local.x * c - local.y * s, local.x * s + local.y * c);
        }

        void VfxFlipbookFrame(float age, float life, float frames, float rate, float start, out float frameF)
        {
            if (rate > 0.0)
            {
                float f = frames < 1.0 ? 1.0 : frames;
                frameF = age * rate;
                frameF = frameF - f * floor(frameF / f);
                if (frameF < 0.0)
                    frameF = 0.0;
            }
            else
                frameF = saturate(age / max(life, 1e-5)) * frames;
            if (start != 0.0)
            {
                float f = frames < 1.0 ? 1.0 : frames;
                frameF += start;
                frameF = frameF - f * floor(frameF / f);
                if (frameF < 0.0)
                    frameF = 0.0;
            }
        }

        void VfxFlipbookCell(float frame, float cols, float rows, float2 uv, out float2 atlasUv)
        {
            float col = fmod(frame, cols);
            float row = floor(frame / cols);
            float2 cell = float2(1.0 / cols, 1.0 / rows);
            float2 atlas = float2(col, (rows - 1.0) - row);
            atlasUv = (uv + atlas) * cell;
        }

        void VfxStripExtrude(float3 p0, float3 p1, float localX, float along01, float width, float3 camera, out float3 world)
        {
            float3 mid = p0 + (p1 - p0) * along01;
            float3 along = p1 - p0;
            float3 toCam = camera - mid;
            float3 side = cross(along, toCam);
            float slen2 = dot(side, side);
            if (slen2 <= 1e-10f)
                side = float3(1.0f, 0.0f, 0.0f);
            else
                side *= rsqrt(slen2);
            world = mid + side * (localX * width);
        }

        void VfxDecalProject(float3 pos, float4 plane, out float3 onPlane, out float3 n)
        {
            n = plane.xyz;
            float n2 = dot(n, n);
            if (n2 <= 1e-10)
            {
                n = float3(0.0, 1.0, 0.0);
                onPlane = float3(pos.x, -plane.w, pos.z);
                return;
            }
            float dist = (dot(pos, n) + plane.w) / n2;
            onPlane = pos - n * dist;
            n *= rsqrt(n2);
        }

        void VfxDecalExtrude(float3 onPlane, float3 n, float localX, float localY, float size, out float3 world)
        {
            float3 tangent = cross(n, float3(0.0, 1.0, 0.0));
            float t2 = dot(tangent, tangent);
            if (t2 <= 1e-10)
                tangent = cross(n, float3(1.0, 0.0, 0.0));
            tangent *= rsqrt(dot(tangent, tangent));
            float3 bitangent = cross(n, tangent);
            world = onPlane + tangent * (localX * size) + bitangent * (localY * size) + n * 0.01;
        }

        VSOutput main(VSInput input)
        {
            uint particleIndex = _LiveList[input.instanceId];
            VfxParticle particle = _Particles[particleIndex];
            VfxParticle next = particle;
            bool strip = _OutputMode >= 1.5 && _OutputMode < 2.5;
            if (strip)
                next = _Particles[_LiveList[input.instanceId + 1u]];

            float along01 = input.vertexTexCoord0.y;
            float2 spun;
            VfxSpinLocal(float2(input.vertexPosition.x, input.vertexPosition.y), particle.pad0, spun);
            float3 local = float3(spun.x, spun.y, input.vertexPosition.z);
            float3 vfxOrientAlong = particle.velocity;
            if (_OrientMode >= 1.5)
                vfxOrientAlong = float3(0.0, 1.0, 0.0);
            float vfxFlipbookRate = 0.0;
            float vfxFlipbookStart = 0.0;
            // VFXGRAPH_OUTPUT_VS
            float3 worldPosition;
            float3 decalNormal = float3(0.0, 1.0, 0.0);
            if (_OutputMode >= 2.5)
            {
                float3 onPlane;
                VfxDecalProject(particle.position, _DecalPlane, onPlane, decalNormal);
                VfxDecalExtrude(onPlane, decalNormal, input.vertexPosition.x, input.vertexPosition.y, particle.size, worldPosition);
            }
            else if (strip)
            {
                float width = particle.size + (next.size - particle.size) * along01;
                VfxStripExtrude(particle.position, next.position, input.vertexPosition.x, along01, width, _WorldSpaceCameraPos, worldPosition);
            }
            else if (_OutputMode >= 0.5)
            {
                if (_OrientMode > 0.0)
                    VfxOrientAlongVelocity(vfxOrientAlong, _WorldSpaceCameraPos - particle.position, local, particle.size, particle.position, worldPosition);
                else
                    worldPosition = particle.position + local * particle.size;
            }
            else if (_OrientMode > 0.0)
            {
                VfxOrientAlongVelocity(vfxOrientAlong, _WorldSpaceCameraPos - particle.position, float3(local.x, local.y, 0.0), particle.size, particle.position, worldPosition);
            }
            else
            {
                float3 cameraRight = float3(xengine_MatV[0][0], xengine_MatV[0][1], xengine_MatV[0][2]);
                float3 cameraUp = float3(xengine_MatV[1][0], xengine_MatV[1][1], xengine_MatV[1][2]);
                worldPosition = particle.position
                    + cameraRight * local.x * particle.size
                    + cameraUp * local.y * particle.size;
            }

            float3 vel = strip
                ? particle.velocity + (next.velocity - particle.velocity) * along01
                : particle.velocity;
            float3 prevWorld = worldPosition - vel * xengine_DeltaTime.x;
            float4 currNJ = mul(xengine_MatVP_NonJittered, float4(worldPosition, 1.0));
            float4 prevClip = mul(xengine_PrevViewProj, float4(prevWorld, 1.0));

            float cols = max(_FlipbookColumns, 1.0);
            float rows = max(_FlipbookRows, 1.0);
            float frames = cols * rows;
            float age = strip ? particle.age + (next.age - particle.age) * along01 : particle.age;
            float life = max(strip ? particle.lifetime + (next.lifetime - particle.lifetime) * along01 : particle.lifetime, 1e-5);
            float frameF;
            VfxFlipbookFrame(age, life, frames, vfxFlipbookRate, vfxFlipbookStart, frameF);
            float frame = min(floor(frameF), frames - 1.0);
            float nextFrame = min(frame + 1.0, frames - 1.0);

            VSOutput o;
            o.position = mul(xengine_MatVP, float4(worldPosition, 1.0));
            o.texCoord0 = input.vertexTexCoord0;
            VfxFlipbookCell(frame, cols, rows, input.vertexTexCoord0, o.flipbookUv);
            VfxFlipbookCell(nextFrame, cols, rows, input.vertexTexCoord0, o.flipbookUvNext);
            o.flipbookBlend = _FlipbookBlend > 0.0 ? saturate(_FlipbookBlend) * (frameF - frame) : 0.0;
            o.color = (strip ? particle.color + (next.color - particle.color) * along01 : particle.color) * _MainColor;
            o.outputMode = _OutputMode;
            if (_OutputMode >= 2.5)
                o.worldNormal = decalNormal;
            else if (strip)
            {
                float3 along = next.position - particle.position;
                float3 toCam = _WorldSpaceCameraPos - worldPosition;
                o.worldNormal = cross(along, toCam);
            }
            else if (_OutputMode >= 0.5)
                o.worldNormal = input.vertexPosition;
            else
                o.worldNormal = _WorldSpaceCameraPos - particle.position;
            o.viewDir = _WorldSpaceCameraPos - worldPosition;
            float4 viewPos = mul(xengine_MatV, float4(worldPosition, 1.0));
            o.particleEyeZ = abs(viewPos.z);
            o.motion = VfxMotionNdcDelta(currNJ, prevClip);
            return o;
        }
    }

    Fragment
    {
        cbuffer GlobalUniforms : register(b0)
        {
            float4x4 xengine_MatV;
            float4x4 xengine_MatIV;
            float4x4 xengine_MatP;
            float4x4 xengine_MatVP;
            float4x4 xengine_PrevViewProj;
            float4x4 xengine_MatIP;
            float4x4 xengine_MatIVP;
            float4x4 xengine_MatVP_NonJittered;
            float3 _WorldSpaceCameraPos;
            float _padding0;
            float4 _ProjectionParams;
            float4 _ScreenParams;
            float2 _CameraJitter;
            float2 _CameraPreviousJitter;
            float4 _Time;
            float4 _SinTime;
            float4 _CosTime;
            float4 xengine_DeltaTime;
        };

        struct PSInput
        {
            float4 position : SV_Position;
            float2 texCoord0 : TEXCOORD0;
            float2 flipbookUv : TEXCOORD1;
            float4 color : COLOR0;
            float outputMode : TEXCOORD2;
            float3 worldNormal : TEXCOORD3;
            float3 viewDir : TEXCOORD4;
            float particleEyeZ : TEXCOORD5;
            float2 flipbookUvNext : TEXCOORD6;
            float flipbookBlend : TEXCOORD7;
            float2 motion : TEXCOORD8;
        };

        cbuffer VfxOutputMaterial : register(b2)
        {
            float4 _MainColor;
            float _FlipbookColumns;
            float _FlipbookRows;
            float _OutputMode;
            float _LitMode;
            float4 _LightDirection;
            float4 _LightColor;
            float4 _AmbientColor;
            float _SoftParticleFade;
            float _DistortionIntensity;
            float _CameraFadeNear;
            float _CameraFadeFar;
            float4 _DecalPlane;
            float _FlipbookBlend;
            float _MotionVectorMode;
            float _MotionVectorScale;
            float _OrientMode;
        };

        // Particle SoA SRVs occupy t0/t1 on the vertex stage; keep the sprite on t2.
        Texture2D _MainTex : register(t2);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t3);
        SamplerState _CameraDepthSampler : register(s1);
        Texture2D _VfxSceneColor : register(t4);
        SamplerState _VfxSceneColorSampler : register(s2);
        Texture2D _FlipbookMotion : register(t5);
        SamplerState _FlipbookMotionSampler : register(s3);

        float VfxLinearEyeDepth(float rawDepth)
        {
            float z = rawDepth * 2.0 - 1.0;
            float nearZ = _ProjectionParams.y;
            float farZ = _ProjectionParams.z;
            return (2.0 * nearZ * farZ) / (farZ + nearZ - z * (farZ - nearZ));
        }

        float VfxSoftFade(float particleEye, float sceneEye, float fade)
        {
            if (fade <= 0.0)
                return 1.0;
            return saturate((sceneEye - particleEye) / fade);
        }

        float2 VfxDistortUv(float2 uv, float2 source, float intensity)
        {
            if (intensity <= 0.0)
                return uv;
            return saturate(uv + (source * 2.0 - 1.0) * intensity);
        }

        float VfxCameraFade(float d, float fadeNear, float fadeFar)
        {
            float a = 1.0;
            if (fadeNear > 0.0)
                a = saturate(d / fadeNear);
            if (fadeFar > 0.0)
                a *= saturate((fadeFar - d) / fadeFar);
            return a;
        }

        float4 VfxFlipbookBlend(float4 a, float4 b, float w)
        {
            if (w <= 0.0)
                return a;
            return a + (b - a) * saturate(w);
        }

        float2 VfxFlipbookWarp(float2 uv, float2 mv, float weight, float scale)
        {
            return uv + mv * weight * scale;
        }

        float3 VfxMotionEncode(float2 mv, float scale)
        {
            float2 e = saturate(mv * scale * 0.5 + 0.5);
            return float3(e.x, e.y, 0.5);
        }

        float4 main(PSInput input) : SV_Target
        {
            if (input.outputMode < 0.5)
            {
                float2 centered = input.texCoord0 * 2.0f - 1.0f;
                if (dot(centered, centered) > 1.0f)
                    discard;
            }
            float vfxFlipbookMotionScale = 0.0;
            // VFXGRAPH_OUTPUT_PS
            float2 uv0 = input.flipbookUv;
            float2 uv1 = input.flipbookUvNext;
            if (vfxFlipbookMotionScale > 0.0 && input.flipbookBlend > 0.0)
            {
                float2 mv0 = _FlipbookMotion.Sample(_FlipbookMotionSampler, input.flipbookUv).rg * 2.0 - 1.0;
                float2 mv1 = _FlipbookMotion.Sample(_FlipbookMotionSampler, input.flipbookUvNext).rg * 2.0 - 1.0;
                uv0 = VfxFlipbookWarp(input.flipbookUv, mv0, input.flipbookBlend, vfxFlipbookMotionScale);
                uv1 = VfxFlipbookWarp(input.flipbookUvNext, mv1, input.flipbookBlend - 1.0, vfxFlipbookMotionScale);
            }
            float4 tex = _MainTex.Sample(_MainTexSampler, uv0);
            if (input.flipbookBlend > 0.0)
            {
                float4 tex1 = _MainTex.Sample(_MainTexSampler, uv1);
                tex = VfxFlipbookBlend(tex, tex1, input.flipbookBlend);
            }
            float4 baseColor = input.color * tex;
            if (_DistortionIntensity > 0.0)
            {
                float2 screenUV = input.position.xy / max(_ScreenParams.xy, float2(1.0, 1.0));
                float2 duv = VfxDistortUv(screenUV, input.texCoord0, _DistortionIntensity);
                float3 scene = _VfxSceneColor.Sample(_VfxSceneColorSampler, duv).rgb;
                float w = saturate(_DistortionIntensity) * saturate(baseColor.a);
                baseColor.rgb = baseColor.rgb + (scene - baseColor.rgb) * w;
            }
            if (_SoftParticleFade > 0.0)
            {
                float2 screenUV = input.position.xy / max(_ScreenParams.xy, float2(1.0, 1.0));
                float sceneRaw = _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r;
                float sceneEye = VfxLinearEyeDepth(sceneRaw);
                baseColor.a *= VfxSoftFade(input.particleEyeZ, sceneEye, _SoftParticleFade);
            }
            if (_CameraFadeNear > 0.0 || _CameraFadeFar > 0.0)
                baseColor.a *= VfxCameraFade(input.particleEyeZ, _CameraFadeNear, _CameraFadeFar);
            if (_MotionVectorMode > 0.0)
            {
                float3 enc = VfxMotionEncode(input.motion, _MotionVectorScale);
                float w = saturate(_MotionVectorMode) * saturate(baseColor.a);
                baseColor.rgb = baseColor.rgb + (enc - baseColor.rgb) * w;
            }
            if (_LitMode < 0.5)
                return baseColor;

            float3 N = normalize(input.worldNormal);
            float3 L = normalize(_LightDirection.xyz);
            float nDotL = max(dot(N, L), 0.0);
            float intensity = _LightColor.a;
            float3 lit = baseColor.rgb * (_AmbientColor.rgb + _LightColor.rgb * intensity * nDotL);
            if (_LitMode >= 1.5)
            {
                float3 H = normalize(L + normalize(input.viewDir));
                float spec = pow(max(dot(N, H), 0.0), max(_AmbientColor.w, 1.0));
                lit += _LightColor.rgb * intensity * spec;
            }
            return float4(lit, baseColor.a);
        }
    }
    ENDSLANG
}
