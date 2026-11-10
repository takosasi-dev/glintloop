// 全画面を覆う三角形 1 枚を出す頂点シェーダー(頂点バッファを使わず SV_VertexID から作る)。
// すべての全画面パスが共有する。uv は左上が (0,0)、右下が (1,1)。
// Draw(3, 0) で呼ぶ。入力レイアウトは null。

struct VsOut
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOut main(uint id : SV_VertexID)
{
    VsOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}
