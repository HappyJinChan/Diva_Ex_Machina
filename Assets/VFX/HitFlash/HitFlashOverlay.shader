// 受擊閃光的疊加層（URP）。由 EntityHitFlash 在閃光期間暫時掛到每個 Renderer 的材質陣列最後面。
//
// 不讀貼圖、不受光：在原本的模型上再畫一次，顏色往 _FlashColor 混合，混合比例 = _FlashAmount × _FlashColor.a。
// 所以底下是 URP Lit、PotaToon 或其他 Shader 都一樣有效，也不需要材質開啟 Emission。
// _FlashColor 是 HDR：強度大於 1 會吃到 Bloom，看起來像在發光。
//
// 只有 UniversalForward 一個 Pass：不投影、不寫深度，不會多出一層影子。
Shader "Diva/HitFlashOverlay"
{
    Properties
    {
        [HDR] _FlashColor ("Flash Color", Color) = (1, 1, 1, 1)
        _FlashAmount ("Flash Amount", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "HitFlash"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1   // 跟原本的模型同一個位置，往鏡頭推一點避免 Z-fighting

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FlashColor;
                half _FlashAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return half4(_FlashColor.rgb, saturate(_FlashAmount * _FlashColor.a));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
