#ifndef TOON_DITHER_FADE_INCLUDED
#define TOON_DITHER_FADE_INCLUDED

// 鏡頭太近時的「網點淡出」（screen-door / dither fade）。
// 由 CameraProximityFade 用 MaterialPropertyBlock 逐個 Renderer 設定 _DitherFade：
//   0 = 完全顯示（不做任何事），1 = 完全消失。
// 用 4x4 Bayer 網點逐像素 clip，物件仍然是不透明的：
// 不用改渲染順序、不會有半透明排序問題、自己的手臂也不會透出身體內部。
//
// 刻意不放進 Properties / UnityPerMaterial：放進去需要改套件裡的 CBUFFER，
// 放在外面當成一般 uniform，材質沒設定時就是 0，SRP Batcher 也不受影響。
float _DitherFade;

static const float kToonBayer4x4[16] =
{
     0.0,  8.0,  2.0, 10.0,
    12.0,  4.0, 14.0,  6.0,
     3.0, 11.0,  1.0,  9.0,
    15.0,  7.0, 13.0,  5.0
};

// pixel = 片段的螢幕像素座標（SV_POSITION.xy）
void ToonDitherFadeClip(float2 pixel)
{
    if (_DitherFade <= 0.0) return;

    uint2 q = uint2(pixel) & 3u;
    float threshold = (kToonBayer4x4[q.y * 4u + q.x] + 0.5) / 16.0;
    clip(threshold - _DitherFade);
}

#endif // TOON_DITHER_FADE_INCLUDED
