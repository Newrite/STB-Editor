namespace StbEditor.Render;

/// <summary>HLSL sources, compiled at startup with d3dcompiler_47 (ships with Windows).</summary>
internal static class Shaders
{
    public const int MaxLights = 32;

    public const string Common = """
        #define MAX_LIGHTS 32
        cbuffer Frame : register(b0)
        {
            row_major float4x4 ViewProj;
            float4 CameraPos;
            float4 SunDir;
            float4 SunColor;
            float4 Ambient;
            float4 FogColor;   // w: fog enabled
            float4 FogRange;   // x near, y far
            float4 Mode;       // x: 0 lit, 1 unlit, 2 normals, 3 lit no textures; y: light count
            float4 CameraRight;
            float4 CameraUp;
            float4 CameraForward;
            float4 LightPos[MAX_LIGHTS];   // xyz, w radius
            float4 LightColor[MAX_LIGHTS]; // rgb * fade
        };

        float3 ApplyFog(float3 color, float3 world)
        {
            if (FogColor.w <= 0) return color;
            float d = distance(world, CameraPos.xyz);
            float f = saturate((d - FogRange.x) / max(FogRange.y - FogRange.x, 1));
            return lerp(color, FogColor.rgb, f * 0.85);
        }

        float3 Lighting(float3 N, float3 world, float3 albedo, float specMask, float gloss, float3 specColor)
        {
            float3 diffuse = Ambient.rgb + SunColor.rgb * saturate(dot(N, SunDir.xyz));
            float3 V = normalize(CameraPos.xyz - world);
            float3 H = normalize(SunDir.xyz + V);
            float3 spec = SunColor.rgb * pow(saturate(dot(N, H)), max(gloss, 1)) * specMask;

            int count = (int)Mode.y;
            [loop] for (int k = 0; k < count; k++)
            {
                float3 L = LightPos[k].xyz - world;
                float d = length(L);
                float r = LightPos[k].w;
                if (d >= r) continue;
                L /= max(d, 0.001);
                float att = saturate(1 - d / r);
                att *= att;
                diffuse += LightColor[k].rgb * att * saturate(dot(N, L));
                float3 h = normalize(L + V);
                spec += LightColor[k].rgb * att * pow(saturate(dot(N, h)), max(gloss, 1)) * specMask;
            }

            return albedo * diffuse + spec * specColor;
        }
        """;

    public const string Mesh = Common + """
        cbuffer Material : register(b1)
        {
            float4 UvTransform; // offset xy, scale zw
            float4 Params;      // x alpha test threshold (<0 off), y alpha, z glossiness, w specular strength
            float4 SpecColor;
            float4 Emissive;    // rgb * multiple
            float4 TintColor;
            float4 Billboard;   // xyz origin in model space, w: 0 none, 1 face camera, 2 rotate about up
            float4 BillboardUp; // model-space axis for mode 2
            uint4  Flags;
        };

        #define F_VERTEXCOLOR 1
        #define F_VERTEXALPHA 2
        #define F_MSN         4
        #define F_EFFECT      8
        #define F_SPECULAR    16
        #define F_GLOWMAP     32
        #define F_SPECMAP     64
        #define F_OWNEMIT     128
        #define F_TINT        256
        #define F_FLAT        512
        #define F_PALCOLOR    1024
        #define F_PALALPHA    2048
        #define F_FACEGEN     4096
        #define F_FADE        8192

        Texture2D DiffuseMap : register(t0);
        Texture2D NormalMap  : register(t1);
        Texture2D GlowMap    : register(t2);
        Texture2D SpecMap    : register(t3);
        Texture2D FaceTintMap   : register(t4);
        Texture2D FaceDetailMap : register(t5);
        SamplerState Sampler : register(s0);
        SamplerState ClampSampler : register(s1);

        struct VSIn
        {
            float3 Position  : POSITION;
            float3 Normal    : NORMAL;
            float3 Tangent   : TANGENT;
            float3 Bitangent : BINORMAL;
            float2 UV        : TEXCOORD0;
            float4 Color     : COLOR0;
            float4 W0 : WORLD0;
            float4 W1 : WORLD1;
            float4 W2 : WORLD2;
            float4 W3 : WORLD3;
            float4 Highlight : HIGHLIGHT;
        };

        struct PSIn
        {
            float4 Position  : SV_Position;
            float3 World     : WORLDPOS;
            float3 Normal    : NORMAL;
            float3 Tangent   : TANGENT;
            float3 Bitangent : BINORMAL;
            float2 UV        : TEXCOORD0;
            float4 Color     : COLOR0;
            float4 Highlight : HIGHLIGHT;
            float3 R0 : ROT0;
            float3 R1 : ROT1;
            float3 R2 : ROT2;
        };

        PSIn VSMain(VSIn i)
        {
            float4x4 W = float4x4(i.W0, i.W1, i.W2, i.W3);
            float3x3 R = (float3x3)W;
            PSIn o;

            if (Billboard.w > 0)
            {
                // NiBillboardNode: the card's local X/Y become screen right/up, +Z points at the camera.
                float3 origin = mul(float4(Billboard.xyz, 1), W).xyz;
                float scale = length(i.W0.xyz);
                float3 right, up, toward;
                if (Billboard.w < 1.5)
                {
                    right = CameraRight.xyz;
                    up = CameraUp.xyz;
                    toward = -CameraForward.xyz;
                }
                else
                {
                    up = normalize(mul(BillboardUp.xyz, R));
                    float3 d = CameraPos.xyz - origin;
                    d -= up * dot(d, up);
                    toward = dot(d, d) > 1e-4 ? normalize(d) : -CameraForward.xyz;
                    right = cross(up, toward);
                }
                float3x3 B = float3x3(right, up, toward);
                float3 world = origin + mul(i.Position, B) * scale;
                o.Position = mul(float4(world, 1), ViewProj);
                o.World = world;
                o.Normal = mul(i.Normal, B);
                o.Tangent = mul(i.Tangent, B);
                o.Bitangent = mul(i.Bitangent, B);
                o.R0 = right;
                o.R1 = up;
                o.R2 = toward;
            }
            else
            {
                float4 world = mul(float4(i.Position, 1), W);
                o.Position = mul(world, ViewProj);
                o.World = world.xyz;
                o.Normal = mul(i.Normal, R);
                o.Tangent = mul(i.Tangent, R);
                o.Bitangent = mul(i.Bitangent, R);
                o.R0 = normalize(i.W0.xyz);
                o.R1 = normalize(i.W1.xyz);
                o.R2 = normalize(i.W2.xyz);
            }

            o.UV = i.UV * UvTransform.zw + UvTransform.xy;
            o.Color = i.Color;
            o.Highlight = i.Highlight;
            return o;
        }

        struct VSParticleIn
        {
            float3 Center    : POSITION;
            float2 Corner    : TEXCOORD1;
            float2 SizeAngle : TEXCOORD2;
            float2 UV        : TEXCOORD0;
            float4 Color     : COLOR0;
            float4 VelAspect : TEXCOORD3;
            float4 W0 : WORLD0;
            float4 W1 : WORLD1;
            float4 W2 : WORLD2;
            float4 W3 : WORLD3;
            float4 Highlight : HIGHLIGHT;
        };

        // Particles: model-space centre, expanded to a camera-facing quad here.
        PSIn VSParticle(VSParticleIn i)
        {
            float4x4 W = float4x4(i.W0, i.W1, i.W2, i.W3);
            float3 center = mul(float4(i.Center, 1), W).xyz;
            float scale = length(i.W0.xyz);
            float angle = i.SizeAngle.y;
            float3 velocity = mul(i.VelAspect.xyz, (float3x3)W);
            float2 screenVelocity = float2(dot(velocity, CameraRight.xyz), dot(velocity, CameraUp.xyz));
            // Velocity-oriented quads point their +Y along the on-screen motion.
            if (dot(screenVelocity, screenVelocity) > 1e-6) angle = atan2(-screenVelocity.x, screenVelocity.y);
            float s, c;
            sincos(angle, s, c);
            float2 corner = float2(i.Corner.x * i.VelAspect.w, i.Corner.y);
            float2 k = float2(corner.x * c - corner.y * s, corner.x * s + corner.y * c) * i.SizeAngle.x * scale;
            float3 world = center + CameraRight.xyz * k.x + CameraUp.xyz * k.y;

            PSIn o;
            o.Position = mul(float4(world, 1), ViewProj);
            o.World = world;
            o.Normal = -CameraForward.xyz;
            o.Tangent = CameraRight.xyz;
            o.Bitangent = CameraUp.xyz;
            o.UV = i.UV * UvTransform.zw + UvTransform.xy;
            o.Color = i.Color;
            o.Highlight = i.Highlight;
            o.R0 = CameraRight.xyz;
            o.R1 = CameraUp.xyz;
            o.R2 = -CameraForward.xyz;
            return o;
        }

        float4 PSMain(PSIn i, bool front : SV_IsFrontFace) : SV_Target
        {
            uint flags = Flags.x;

            if (flags & F_FLAT)
            {
                float3 n = normalize(i.Normal);
                float shade = 0.55 + 0.45 * saturate(dot(n, normalize(float3(0.3, -0.4, 0.85))));
                return float4(i.Highlight.rgb * shade, 1);
            }

            float4 texel = Mode.x == 3 ? float4(0.7, 0.7, 0.7, 1) : DiffuseMap.Sample(Sampler, i.UV);
            float4 albedo = texel;
            float3 color;

            if (flags & F_EFFECT)
            {
                // BSEffectShader as the game does it (cf. Community Shaders Effect.hlsl): base colour
                // (emissive colour) times vertex colour; greyscale-to-alpha looks the opacity up in
                // the 2D palette at (texture alpha, alpha), greyscale-to-colour the colour at
                // (texture green, base red), scaled by the emissive multiple.
                float4 vc = float4((flags & F_VERTEXCOLOR) ? i.Color.rgb : 1, (flags & (F_VERTEXCOLOR | F_VERTEXALPHA)) ? i.Color.a : 1);
                float4 mul = Emissive * vc;
                float alpha = ((flags & F_PALALPHA) ? 1 : texel.a) * mul.a * Params.y;
                if (flags & F_PALALPHA) alpha = SpecMap.Sample(ClampSampler, float2(saturate(texel.a), saturate(alpha))).a;
                if (flags & F_PALCOLOR) color = SpecColor.w * SpecMap.Sample(ClampSampler, float2(saturate(texel.g), saturate(mul.r))).rgb;
                else color = mul.rgb * SpecColor.w * texel.rgb;
                albedo.a = alpha;
                if (Params.x >= 0 && albedo.a < Params.x) discard;
            }
            else
            {
                // Hair (as the game): vertex green blends towards the hair colour; no plain vertex colour.
                if (flags & F_TINT) albedo.rgb *= lerp(float3(1, 1, 1), TintColor.rgb, i.Color.g);
                else if (flags & F_VERTEXCOLOR) albedo.rgb *= i.Color.rgb;
                if (flags & F_VERTEXALPHA) albedo.a *= i.Color.a;
                if ((flags & F_FACEGEN) && Mode.x != 3)
                {
                    // FaceGen head: per-NPC tint overlay and age/detail map (Lighting.hlsl GetFacegenBaseColor).
                    float3 detail = 3.984375 * (float3(0.00392156886, 0, 0.00392156886) + FaceDetailMap.Sample(Sampler, i.UV).rgb);
                    float3 tint = FaceTintMap.Sample(Sampler, i.UV).rgb * albedo.rgb * 2;
                    tint -= tint * albedo.rgb;
                    albedo.rgb = (albedo.rgb * albedo.rgb + tint) * detail;
                }
                albedo.a *= Params.y;
                if (Params.x >= 0 && albedo.a < Params.x) discard;
                if (flags & F_FADE) albedo.a = Params.y;

                float4 nm = NormalMap.Sample(Sampler, i.UV);
                float3 N = normalize(i.Normal);
                float specMask = nm.a;

                if (flags & F_MSN)
                {
                    float3 m = nm.xyz * 2 - 1;
                    N = normalize(m.x * i.R0 + m.y * i.R1 + m.z * i.R2);
                    specMask = (flags & F_SPECMAP) ? SpecMap.Sample(Sampler, i.UV).r : 0;
                }
                else
                {
                    float3 T = normalize(i.Tangent);
                    float3 B = normalize(i.Bitangent);
                    float3 m = nm.xyz * 2 - 1;
                    N = normalize(m.x * T + m.y * B + m.z * N);
                }

                if (!front) N = -N;
                if (!(flags & F_SPECULAR)) specMask = 0;

                if (Mode.x == 2) return float4(N * 0.5 + 0.5, 1);

                if (Mode.x == 1)
                    color = albedo.rgb;
                else
                    color = Lighting(N, i.World, albedo.rgb, specMask * Params.w, Params.z, SpecColor.rgb);

                if (flags & F_GLOWMAP) color += GlowMap.Sample(Sampler, i.UV).rgb * Emissive.rgb;
                else if (flags & F_OWNEMIT) color += albedo.rgb * Emissive.rgb * 0.5;
            }

            color = ApplyFog(color, i.World);
            color = lerp(color, i.Highlight.rgb, i.Highlight.w);
            return float4(color, albedo.a);
        }
        """;

    public const string Terrain = Common + """
        Texture2D LayerMap : register(t0);
        SamplerState Sampler : register(s0);

        struct VSIn
        {
            float3 Position : POSITION;
            float3 Normal   : NORMAL;
            float4 Color    : COLOR0;
            float2 UV       : TEXCOORD0;
            float  Alpha    : TEXCOORD1;
        };

        struct PSIn
        {
            float4 Position : SV_Position;
            float3 World    : WORLDPOS;
            float3 Normal   : NORMAL;
            float4 Color    : COLOR0;
            float2 UV       : TEXCOORD0;
            float  Alpha    : TEXCOORD1;
        };

        PSIn VSMain(VSIn i)
        {
            PSIn o;
            o.Position = mul(float4(i.Position, 1), ViewProj);
            o.World = i.Position;
            o.Normal = i.Normal;
            o.Color = i.Color;
            o.UV = i.UV;
            o.Alpha = i.Alpha;
            return o;
        }

        float4 PSMain(PSIn i) : SV_Target
        {
            float3 albedo = Mode.x == 3 ? float3(0.6, 0.6, 0.6) : LayerMap.Sample(Sampler, i.UV).rgb;
            albedo *= i.Color.rgb;
            float3 N = normalize(i.Normal);
            float3 color;
            if (Mode.x == 2) color = N * 0.5 + 0.5;
            else if (Mode.x == 1) color = albedo;
            else color = Lighting(N, i.World, albedo, 0, 1, 0);
            return float4(ApplyFog(color, i.World), i.Alpha);
        }
        """;

    public const string Lines = Common + """
        struct VSIn { float3 Position : POSITION; float4 Color : COLOR0; };
        struct PSIn { float4 Position : SV_Position; float4 Color : COLOR0; };

        PSIn VSMain(VSIn i)
        {
            PSIn o;
            o.Position = mul(float4(i.Position, 1), ViewProj);
            o.Color = i.Color;
            return o;
        }

        float4 PSMain(PSIn i) : SV_Target { return i.Color; }

        // Navmesh: each vertex pulled toward the camera along its view ray — same place on screen, nearer in depth, so the
        // navmesh shows over floor clutter and bumps up to that far above it but stays behind walls.
        PSIn Pulled(VSIn i, float pull)
        {
            float3 toEye = CameraPos.xyz - i.Position;
            float distance = length(toEye);
            PSIn o;
            o.Position = mul(float4(i.Position + toEye / max(distance, 1e-3) * min(pull, distance * 0.5), 1), ViewProj);
            o.Color = i.Color;
            return o;
        }

        PSIn VSNavNear(VSIn i) { return Pulled(i, 8); }
        PSIn VSNavFar(VSIn i) { return Pulled(i, 48); }
        """;
}
