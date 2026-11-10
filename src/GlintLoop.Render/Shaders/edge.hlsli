// 輪郭の調整(edge)の 2 つのパス(edge_rows.ps と edge.ps)が共有する定数バッファと、距離を探す半径の式。
// 探す半径 R の外の距離は α' を変えない(α' は sdα が shift±w÷2 の外で 0 か 1 に張り付く)ので、R の中だけを正確に求める。
// 定数バッファは両パスとも bind の順に {"shift": "shift", "feather": "feather", "shortSide": "$shortSide"}。

cbuffer Params : register(b0)
{
    float4 shift;
    float4 feather;
    float4 shortSide;
};

// 見つからなかったことを表す距離(R より十分大きく、二乗しても float で正確に表せる)。
static const float EdgeNone = 30000.0;

// 境界をぼかす幅 w=max(1, px(feather)×2)。
float EdgeWidth()
{
    return max(1.0, Px(feather.x, shortSide.x) * 2.0);
}

// 探す半径(画素)。|px(shift)|+w÷2 を超える距離は α' を 0 か 1 にするだけなので、それより 1 画素大きく取る。
int EdgeRadius()
{
    return int(ceil(abs(Px(shift.x, shortSide.x)) + EdgeWidth() * 0.5)) + 1;
}
