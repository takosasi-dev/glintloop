// 測定用の小さな GIF のバイト列を、ライブラリを使わずに組み立てる(probe-handoff と probe-skia で共有)。
// LZW は「2 画素ごとにクリアコード」を送る方式で、符号長を 3 ビットに固定する(圧縮はしないが、どのデコーダーでも読める)。
// 製品の GIF エンコーダーではない。パレットは 4 色まで。
namespace GlintLoop.Probes.Shared;

public static class MiniGif
{
    /// <summary>4 色パレットの GIF を作る。frames は各フレームの色番号(0〜3)の配列で、長さは w×h。</summary>
    public static byte[] Build(int w, int h, uint[] paletteRgb, IReadOnlyList<byte[]> frames,
        int delayCs, int? loopCount, int? transparentIndex = null, int disposal = 1)
    {
        if (paletteRgb.Length != 4) throw new ArgumentException("パレットは 4 色にする", nameof(paletteRgb));
        var o = new List<byte>();
        void U16(int v) { o.Add((byte)(v & 0xFF)); o.Add((byte)((v >> 8) & 0xFF)); }

        o.AddRange("GIF89a"u8.ToArray());
        U16(w); U16(h);
        o.Add(0x91); // 大域パレットあり、色の分解能 2 ビット、大きさ 2^(1+1)=4 色
        o.Add(0);    // 背景色の番号
        o.Add(0);    // 画素の縦横比
        foreach (var c in paletteRgb) { o.Add((byte)(c >> 16)); o.Add((byte)(c >> 8)); o.Add((byte)c); }

        if (loopCount is int loop)
        {
            o.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
            o.AddRange("NETSCAPE2.0"u8.ToArray());
            o.AddRange(new byte[] { 0x03, 0x01 });
            U16(loop);
            o.Add(0);
        }

        foreach (var px in frames)
        {
            if (px.Length != w * h) throw new ArgumentException("フレームの画素数が w×h と違う");
            // 画像制御拡張(GCE)
            o.AddRange(new byte[] { 0x21, 0xF9, 0x04 });
            byte packed = (byte)(((disposal & 7) << 2) | (transparentIndex.HasValue ? 1 : 0));
            o.Add(packed);
            U16(delayCs);
            o.Add((byte)(transparentIndex ?? 0));
            o.Add(0);
            // 画像記述子
            o.Add(0x2C); U16(0); U16(0); U16(w); U16(h); o.Add(0);
            // 画素(LZW 最小符号長 2)
            o.Add(2);
            var data = Lzw3(px);
            for (int i = 0; i < data.Length; i += 255)
            {
                int n = Math.Min(255, data.Length - i);
                o.Add((byte)n);
                for (int k = 0; k < n; k++) o.Add(data[i + k]);
            }
            o.Add(0);
        }
        o.Add(0x3B);
        return o.ToArray();
    }

    // 最小符号長 2(クリア=4、終わり=5)。クリアのあとに 2 個の符号を送ると表は 6 番まで増えるだけなので、符号長は 3 ビットのまま。
    static byte[] Lzw3(byte[] px)
    {
        var bits = new List<byte>();
        int acc = 0, nbits = 0;
        void Put(int code)
        {
            acc |= code << nbits; nbits += 3;
            while (nbits >= 8) { bits.Add((byte)(acc & 0xFF)); acc >>= 8; nbits -= 8; }
        }
        for (int i = 0; i < px.Length; i++)
        {
            if (i % 2 == 0) Put(4);
            Put(px[i] & 3);
        }
        Put(5);
        if (nbits > 0) bits.Add((byte)(acc & 0xFF));
        return bits.ToArray();
    }

    /// <summary>測定 4 の代わりに使う、色の違う 2 フレームの動く GIF(遅延 50、ループ回数 0)。</summary>
    public static byte[] TwoFrameAnimated(int size = 128)
    {
        uint[] pal = { 0xFFFFFF, 0xE03030, 0x3050E0, 0x101010 };
        var f0 = new byte[size * size];
        var f1 = new byte[size * size];
        int q = size / 4;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool sq0 = x >= q / 2 && x < q / 2 + q && y >= q && y < 2 * q;              // 左の黒い四角
                bool sq1 = x >= size - q / 2 - q && x < size - q / 2 && y >= 2 * q && y < 3 * q; // 右の黒い四角
                f0[y * size + x] = (byte)(sq0 ? 3 : 1);
                f1[y * size + x] = (byte)(sq1 ? 3 : 2);
            }
        return Build(size, size, pal, new[] { f0, f1 }, 50, 0);
    }
}
