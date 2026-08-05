// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;

// 全体の不透明度（0=完全透明, 1=完全不透明）。
// GaussianSplatController から Shader.SetGlobalFloat で設定する。
// NOTE: Properties ブロックには含めない。
//       Properties のデフォルト値はマテリアルに保存され SetGlobalFloat より優先されるため、
//       グローバル値での制御が効かなくなる。
float _GaussianSplatGlobalOpacity;

UNITY_DECLARE_DEPTH_TEXTURE(_GS_DepthTex);
float4 _GS_DepthTex_TexelSize;
float  _GS_HasDepthTex;

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    if (col.a <= 0) discard;

    if (_GS_HasDepthTex > 0.5)
    {
        if (_GS_DepthTex_TexelSize.z > 1.5)
        {
            float2 uv = i.vertex.xy * _GS_DepthTex_TexelSize.xy;
            float sceneRaw = SAMPLE_DEPTH_TEXTURE(_GS_DepthTex, uv);
            #if defined(UNITY_REVERSED_Z)
            if (sceneRaw > 0.0001) discard;
            #else
            if (sceneRaw < 0.9999) discard;
            #endif
        }
    }

    return float4(GammaToLinearSpace(col.rgb / col.a), _GaussianSplatGlobalOpacity);
}
ENDCG
        }
    }
}
