// UI shader for the QueuedSong row's album-art background. Fades the alpha
// toward the rect edges and modulates that fade with animated value-noise so
// the edge breaks up into wispy / smokey tendrils instead of a hard rectangle.
//
// Usage:
//   1. Create a Material that references this shader.
//   2. Assign the Material to the QueuedSong prefab's _songBackground Image.
//   3. Tweak the material exposed values (edge thickness, noise scale, etc).
//
// Compatible with Unity UI: respects the standard UI stencil mask state and
// the Image component's color tint + canvas alpha multiplier.

Shader "UI/QueuedSongSmokeyEdges"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // Fraction of each axis (0..0.5) consumed by the edge fade. 0.15 means
        // the outer 15% of width/height fades from full alpha to zero.
        _EdgeFade ("Edge Fade Width (all sides)", Range(0, 0.5)) = 0.18

        // Per-edge multipliers. 1 = full fade as set by _EdgeFade, 0 = no fade
        // on that edge (useful when an edge sits against a hard layout boundary
        // and the smoke would otherwise look clipped by a parent mask).
        _FadeTop ("Fade Top", Range(0, 1)) = 1
        _FadeBottom ("Fade Bottom", Range(0, 1)) = 1
        _FadeLeft ("Fade Left", Range(0, 1)) = 1
        _FadeRight ("Fade Right", Range(0, 1)) = 1

        // Strength of the noise distortion on the edge mask. 0 = clean
        // rectangular vignette, 1 = fully driven by noise (very wispy).
        _SmokeStrength ("Smoke Strength", Range(0, 1)) = 0.6

        // Spatial frequency of the smoke noise. Higher = finer-grained wisps.
        _SmokeScale ("Smoke Scale", Range(0.5, 20)) = 6.0

        // Drift speed of the noise field. 0 = static, 1 = animated.
        _SmokeSpeed ("Smoke Speed", Range(0, 2)) = 0.25

        // Below this alpha the fragment is fully cut. Higher values eat further
        // into the image but make edges crisper.
        _AlphaCut ("Alpha Cut", Range(0, 0.5)) = 0.05

        // -- Standard UI properties (kept so the shader drops into the
        //    same slot as Unity's UI/Default shader) --
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;

            float _EdgeFade;
            float _FadeTop;
            float _FadeBottom;
            float _FadeLeft;
            float _FadeRight;
            float _SmokeStrength;
            float _SmokeScale;
            float _SmokeSpeed;
            float _AlphaCut;

            v2f vert(appdata_t IN)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.worldPosition = IN.vertex;
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(IN.texcoord, _MainTex);
                OUT.color = IN.color * _Color;
                return OUT;
            }

            // Hash-based 2D value noise + fbm. Cheap and good enough for UI smoke.
            float hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                float a = hash(i);
                float b = hash(i + float2(1, 0));
                float c = hash(i + float2(0, 1));
                float d = hash(i + float2(1, 1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            float fbm(float2 p)
            {
                float v = 0.0;
                float amp = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    v += amp * valueNoise(p);
                    p *= 2.0;
                    amp *= 0.5;
                }
                return v;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                half4 tex = (tex2D(_MainTex, IN.texcoord) + _TextureSampleAdd);

                // Per-edge fade: distance from each edge / (edge fade width *
                // per-edge multiplier). A multiplier of 0 collapses the divisor
                // to a huge number, making that edge fully opaque (no fade).
                float fadeBand = max(_EdgeFade, 0.0001);
                float fadeLeft   = saturate(IN.texcoord.x         / (fadeBand * max(_FadeLeft,   0.0001)));
                float fadeRight  = saturate((1.0 - IN.texcoord.x) / (fadeBand * max(_FadeRight,  0.0001)));
                float fadeBottom = saturate(IN.texcoord.y         / (fadeBand * max(_FadeBottom, 0.0001)));
                float fadeTop    = saturate((1.0 - IN.texcoord.y) / (fadeBand * max(_FadeTop,    0.0001)));
                // If a per-edge multiplier is 0, force that side's fade to 1
                // (fully opaque) so it doesn't fade at all.
                if (_FadeLeft   <= 0) fadeLeft   = 1;
                if (_FadeRight  <= 0) fadeRight  = 1;
                if (_FadeBottom <= 0) fadeBottom = 1;
                if (_FadeTop    <= 0) fadeTop    = 1;
                float edgeMask = min(min(fadeLeft, fadeRight), min(fadeBottom, fadeTop));

                // Smokey distortion: sample animated fbm and bias the edge mask
                // by (noise - 0.5) so noisy fragments are pushed toward / away
                // from being cut. Result is wispy tendrils on the boundary.
                float2 nUV = IN.texcoord * _SmokeScale + _Time.y * _SmokeSpeed;
                float n = fbm(nUV);
                float smokeyMask = edgeMask + (n - 0.5) * _SmokeStrength;
                smokeyMask = saturate(smokeyMask);

                // Hard cut below threshold so straggler ~0 alpha pixels don't leave a haze.
                smokeyMask = smokeyMask < _AlphaCut ? 0.0 : smokeyMask;

                half4 color = tex * IN.color;
                color.a *= smokeyMask;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a - 0.001);
                #endif

                return color;
            }
            ENDCG
        }
    }
}
