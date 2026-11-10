# THIRD-PARTY-NOTICES

GlintLoop が使う依存ライブラリの名前・バージョン・ライセンス(仕様書 §0・INV-4)。
許可リストは MIT・BSD-2-Clause・BSD-3-Clause・Apache-2.0・zlib(フォントは SIL OFL 1.1 も)。
2026-10-27 に、SkiaSharp のネイティブ部品の一部のライセンスを、作者の判断で例外として許可した(下の「SkiaSharp のネイティブ部品」)。

## NuGet パッケージ

`dotnet list package --include-transitive` に出るものをすべて載せる。表の形は INV-4 の検査(AC-9)が読むので崩さない。

| パッケージ | バージョン | ライセンス | 使う場所 |
|---|---|---|---|
| Vortice.Direct3D11 | 3.8.3 | MIT | src/GlintLoop.Render、probes/probe-gpu、tests |
| Vortice.DXGI | 3.8.3 | MIT | src/GlintLoop.Render(Vortice.Direct3D11 の依存) |
| Vortice.DirectX | 3.8.3 | MIT | Vortice.DXGI の依存 |
| Vortice.Mathematics | 2.1.0 | MIT | Vortice.DirectX の依存 |
| SharpGen.Runtime | 2.4.2-beta | MIT | Vortice の依存 |
| SharpGen.Runtime.COM | 2.4.2-beta | MIT | Vortice の依存 |
| Vortice.D3DCompiler | 3.8.3 | MIT | tools/ShaderCompiler だけ(ビルド時に HLSL をコンパイルする。製品からは参照しない。INV-3) |
| SkiaSharp | 4.153.1 | MIT | src/GlintLoop.Codec(画像のデコードとサンプルの描画)、probes/probe-skia |
| SkiaSharp.NativeAssets.Win32 | 4.153.1 | MIT | SkiaSharp の依存(ネイティブ部品は下の表) |
| SkiaSharp.NativeAssets.macOS | 4.153.1 | MIT | SkiaSharp の依存(Windows では使わない) |

## SkiaSharp のネイティブ部品

`SkiaSharp.NativeAssets.Win32` 4.153.1 が同梱する通知ファイルの全文は `licenses/SkiaSharp-4.153.1-THIRD-PARTY-NOTICES.txt`。通知ファイルには部品の版が書かれていないので、版の欄は空にしてある。パッケージは `libSkiaSharp.pdb` も同梱していて、2026-10-28 にその記号の名前で GIF を読む部品を確かめた。`SkWuffsCodec` と `wuffs_gif__decoder` があり、Mozilla 由来の GIF デコーダー(`SkGifCodec`・`SkGIFImageReader`)は無い。GIF を読むのは Wuffs で、Wuffs は同梱の通知ファイルに載っていないので、表に足してライセンスの全文を `licenses/Wuffs-LICENSE.txt` に置いた(google/wuffs の main の `LICENSE` を 2026-10-28 に取得。DLL に入っている Wuffs の版は確かめていない)。

| 部品 | ライセンス | 扱い |
|---|---|---|
| ANGLE | BSD-3-Clause | 許可リスト |
| HarfBuzz | Old MIT | 例外として許可(2026-10-27) |
| skia | BSD-3-Clause | 許可リスト |
| etc1 | Apache-2.0 | 許可リスト |
| gif(Mozilla 由来の GIF デコーダー) | MPL 1.1 / GPL 2.0 / LGPL 2.1 | 通知に載っているが使わない部品として記録(2026-10-27)。2026-10-28 に `libSkiaSharp.pdb` で入っていないことを確かめた。GIF の読み込みは Wuffs、書き出しは自作のエンコーダー |
| Wuffs(GIF のデコーダー。MIT と Apache-2.0 の二重ライセンスで、MIT を選ぶ。同梱の通知ファイルには無く、2026-10-28 に足した) | MIT | 許可リスト |
| libpng | libpng License | 例外として許可(2026-10-27) |
| DNG SDK | Adobe DNG SDK License | 例外として許可(2026-10-27) |
| expat | MIT | 許可リスト |
| freetype | FreeType License(FTL)を選ぶ(FTL と GPLv2 の二重ライセンス) | 例外として許可(2026-10-27) |
| ICU | Unicode License | 例外として許可(2026-10-27) |
| imgui | MIT | 許可リスト |
| jsoncpp | MIT | 許可リスト |
| libjpeg-turbo | IJG License・BSD-3-Clause・zlib | 例外として許可(2026-10-27。IJG の部分) |
| libwebp | BSD-3-Clause | 許可リスト |
| libmicrohttpd | LGPL 2.1(eCos License との選択) | 通知に載っているが使わない部品として記録(2026-10-27)。GlintLoop はネットワークを使わない(INV-1) |
| piex | Apache-2.0 | 許可リスト |
| sdl | zlib | 許可リスト |
| sfntly | Apache-2.0 | 許可リスト |
| SPIR-V Headers | MIT | 許可リスト |
| SPIR-V Tools | Apache-2.0 | 許可リスト |
| zlib | zlib | 許可リスト |
