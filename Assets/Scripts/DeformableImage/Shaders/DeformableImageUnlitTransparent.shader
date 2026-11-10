Shader "Deformable Image/Unlit Transparent"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _CutMask ("Cut Mask", 2D) = "white" {}
        _CutVisualMask ("Cut Visual Mask", 2D) = "black" {}
        _CutVisualColor ("Cut Visual Color", Color) = (1,0.35,0.35,0.72)
        _Color ("Tint", Color) = (1,1,1,1)
        _DepthAlphaCutoff ("Depth Alpha Cutoff", Range(0,0.25)) = 0.002
        _AmbientStrength ("Ambient Strength", Range(0,2)) = 0.65
        _DiffuseStrength ("Diffuse Strength", Range(0,2)) = 0.55
        _SpecularStrength ("Specular Strength", Range(0,1)) = 0.08
        _Smoothness ("Smoothness", Range(0.02,1)) = 0.28
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Back
        // The front and back are separate, coincident submeshes. Without depth writes,
        // a visible back-facing triangle drawn later can blend over a nearer front-facing
        // triangle when the relief mesh is viewed at a grazing angle.
        ZWrite On
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct AppData
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float2 cutUv : TEXCOORD1;
            };

            struct VertexToFragment
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 cutUv : TEXCOORD1;
                float3 worldPosition : TEXCOORD2;
                float3 worldNormal : TEXCOORD3;
                SHADOW_COORDS(4)
            };

            sampler2D _MainTex;
            sampler2D _CutMask;
            sampler2D _CutVisualMask;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _CutVisualColor;
            float _DepthAlphaCutoff;
            float _AmbientStrength;
            float _DiffuseStrength;
            float _SpecularStrength;
            float _Smoothness;

            VertexToFragment vert(AppData input)
            {
                VertexToFragment output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.cutUv = input.cutUv;
                output.worldPosition = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                TRANSFER_SHADOW(output)
                return output;
            }

            fixed4 frag(VertexToFragment input) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, input.uv) * _Color;
                fixed cutMask = tex2D(_CutMask, input.cutUv).r;
                fixed cutVisual = tex2D(_CutVisualMask, input.cutUv).r;
                color.a *= cutMask;
                fixed overlayAlpha = cutVisual * _CutVisualColor.a;
                color.rgb = lerp(color.rgb, _CutVisualColor.rgb, overlayAlpha);
                color.a = max(color.a, overlayAlpha);
                // Do not let the transparent sprite background or an opened cut write
                // depth. Visible skin does write depth so its front surface occludes the
                // rear pattern correctly at oblique camera angles.
                clip(color.a - _DepthAlphaCutoff);

                float3 normalDirection = normalize(input.worldNormal);
                float3 lightDirection = normalize(UnityWorldSpaceLightDir(input.worldPosition));
                float3 viewDirection = normalize(UnityWorldSpaceViewDir(input.worldPosition));
                float3 halfDirection = normalize(lightDirection + viewDirection);
                UNITY_LIGHT_ATTENUATION(lightAttenuation, input, input.worldPosition);

                float diffuse = saturate(dot(normalDirection, lightDirection));
                float specularPower = lerp(8.0, 128.0, saturate(_Smoothness));
                float specular = pow(saturate(dot(normalDirection, halfDirection)), specularPower)
                    * _SpecularStrength;
                float3 indirectLight = max(0.0, ShadeSH9(float4(normalDirection, 1.0)));
                float3 lighting = _AmbientStrength
                    + indirectLight
                    + _LightColor0.rgb * (diffuse * _DiffuseStrength * lightAttenuation);
                color.rgb = color.rgb * lighting
                    + _LightColor0.rgb * (specular * lightAttenuation);
                return color;
            }
            ENDCG
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            struct ShadowAppData
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                float2 cutUv : TEXCOORD1;
            };

            struct ShadowVertexToFragment
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
                float2 cutUv : TEXCOORD2;
            };

            sampler2D _MainTex;
            sampler2D _CutMask;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _DepthAlphaCutoff;

            ShadowVertexToFragment shadowVert(ShadowAppData v)
            {
                ShadowVertexToFragment output;
                output.uv = TRANSFORM_TEX(v.uv, _MainTex);
                output.cutUv = v.cutUv;
                TRANSFER_SHADOW_CASTER_NORMALOFFSET(output)
                return output;
            }

            float4 shadowFrag(ShadowVertexToFragment input) : SV_Target
            {
                fixed sourceAlpha = tex2D(_MainTex, input.uv).a * _Color.a;
                fixed cutAlpha = tex2D(_CutMask, input.cutUv).r;
                // Only physical skin casts a shadow. Transparent sprite pixels and the
                // removed area of a cut must stay absent from the shadow map.
                clip(sourceAlpha * cutAlpha - _DepthAlphaCutoff);
                SHADOW_CASTER_FRAGMENT(input)
            }
            ENDCG
        }
    }

    FallBack Off
}
