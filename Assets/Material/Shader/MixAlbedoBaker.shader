Shader "Hidden/MixAlbedoBaker"
{
    Properties
    {
        _Base ("Base", 2D) = "white" {}
        _Layer1 ("Layer1", 2D) = "black" {}
        _Layer2 ("Layer2", 2D) = "black" {}
        _Layer3 ("Layer3", 2D) = "black" {}
        _Layer4 ("Layer4", 2D) = "black" {}

        _BaseColor ("BaseColor", Color) = (1,1,1,1)
        _Layer1Color ("Layer1Color", Color) = (1,1,1,1)
        _Layer2Color ("Layer2Color", Color) = (1,1,1,1)
        _Layer3Color ("Layer3Color", Color) = (1,1,1,1)
        _Layer4Color ("Layer4Color", Color) = (1,1,1,1)

        _LayerCount ("Layer Count", Float) = 0

        // 上色介面的高亮（ColorPicker → LayeredToonMaterialController.SetHighlight）
        // _HighlightLayer：-1 = 不高亮，0 = Base，1～4 = Layer1～4。只作用在那一層「看得到」的範圍。
        // 底下比較亮的地方往 _HighlightDark 拉、比較暗的地方往 _HighlightLight 拉，任何顏色都看得出來。
        _HighlightLayer ("Highlight Layer", Float) = -1
        _HighlightAmount ("Highlight Amount", Range(0, 1)) = 0
        _HighlightLight ("Highlight Light", Color) = (1,1,1,1)
        _HighlightDark ("Highlight Dark", Color) = (0.05,0.05,0.05,1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }

        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            sampler2D _Base;
            sampler2D _Layer1;
            sampler2D _Layer2;
            sampler2D _Layer3;
            sampler2D _Layer4;

            float4 _Base_ST;
            float4 _Layer1_ST;
            float4 _Layer2_ST;
            float4 _Layer3_ST;
            float4 _Layer4_ST;

            fixed4 _BaseColor;
            fixed4 _Layer1Color;
            fixed4 _Layer2Color;
            fixed4 _Layer3Color;
            fixed4 _Layer4Color;

            float _LayerCount;
            float _HighlightLayer;
            float _HighlightAmount;
            fixed4 _HighlightLight;
            fixed4 _HighlightDark;

            float2 ApplyST(float2 uv, float4 st)
            {
                return uv * st.xy + st.zw;
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 baseTex = tex2D(_Base, ApplyST(i.uv, _Base_ST));
                fixed3 result = baseTex.rgb * _BaseColor.rgb;

                // hw = 高亮那一層在這個像素「看得到」的比例（被後面的層蓋掉的部分會扣掉）
                float hw = (_HighlightLayer == 0.0) ? 1.0 : 0.0;

                if (_LayerCount >= 1.0)
                {
                    fixed4 layer = tex2D(_Layer1, ApplyST(i.uv, _Layer1_ST));
                    fixed3 layerRGB = layer.rgb * _Layer1Color.rgb;
                    result = lerp(result, layerRGB, layer.a);
                    hw = (_HighlightLayer == 1.0) ? layer.a : hw * (1.0 - layer.a);
                }

                if (_LayerCount >= 2.0)
                {
                    fixed4 layer = tex2D(_Layer2, ApplyST(i.uv, _Layer2_ST));
                    fixed3 layerRGB = layer.rgb * _Layer2Color.rgb;
                    result = lerp(result, layerRGB, layer.a);
                    hw = (_HighlightLayer == 2.0) ? layer.a : hw * (1.0 - layer.a);
                }

                if (_LayerCount >= 3.0)
                {
                    fixed4 layer = tex2D(_Layer3, ApplyST(i.uv, _Layer3_ST));
                    fixed3 layerRGB = layer.rgb * _Layer3Color.rgb;
                    result = lerp(result, layerRGB, layer.a);
                    hw = (_HighlightLayer == 3.0) ? layer.a : hw * (1.0 - layer.a);
                }

                if (_LayerCount >= 4.0)
                {
                    fixed4 layer = tex2D(_Layer4, ApplyST(i.uv, _Layer4_ST));
                    fixed3 layerRGB = layer.rgb * _Layer4Color.rgb;
                    result = lerp(result, layerRGB, layer.a);
                    hw = (_HighlightLayer == 4.0) ? layer.a : hw * (1.0 - layer.a);
                }

                if (_HighlightAmount > 0.0 && hw > 0.0)
                {
                    float lum = dot(result, float3(0.299, 0.587, 0.114));
                    fixed3 target = lum > 0.5 ? _HighlightDark.rgb : _HighlightLight.rgb;
                    result = lerp(result, target, saturate(hw * _HighlightAmount));
                }

                // Your Mix materials are used as opaque equipment albedo,
                // so the baked Base Map is written opaque as well.
                return fixed4(result, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
