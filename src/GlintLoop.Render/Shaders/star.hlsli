// sparkle と particles の stars が共有する星の形(§9.5 の sparkle の式): 腕 arm と核 core の max。
// common.hlsli の後に #include する。main は置かない。

// 星の形 max(arm, core)。dd=(dx, dy) は中心からの差(画素)、r は半径(0 より大きいこと)。smoothstep は §9.4 の SmoothStepSpec。
float StarShape(float2 dd, float r)
{
    float w = 0.07 * r;
    float2 ad = abs(dd);
    float armH = exp(-(dd.y / w) * (dd.y / w)) * (1.0 - SmoothStepSpec(0.0, r, ad.x));
    float armV = exp(-(dd.x / w) * (dd.x / w)) * (1.0 - SmoothStepSpec(0.0, r, ad.y));
    float q = length(dd) / (0.2 * r);
    float core = 0.6 * exp(-q * q);
    return max(max(armH, armV), core);
}
