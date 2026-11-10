// AC-26 の (4) のうちファイルの保存(FR-36・§9.11 の 1 と 5・E-29): look-<8桁>.json の名前と中身の形、同名の上書き、名前の変更と削除、
// 8 桁が衝突したときの -2、名前の規則(1〜40 コードポイント・改行の除去)、読み直し。置き場は一時フォルダ(INV-18)。
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Platform;
using GlintLoop.Render.Effects;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;

namespace GlintLoop.Tests.Looks;

public sealed class UserLookStoreAc26Tests
{
    private static readonly Lazy<EffectCatalog> Catalog = new(() => BuiltInEffects.Load(new MemoryLogSink()));

    private static IReadOnlyList<LookEffect> Vivid => BuiltInLooks.Build(Catalog.Value).Single(l => l.Key == "vivid").Effects;

    private static IReadOnlyList<LookEffect> Mono => BuiltInLooks.Build(Catalog.Value).Single(l => l.Key == "mono").Effects;

    private static string Hash8(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];

    [Test]
    public void AC26_4_保存先はlooksのlook_8桁_jsonで中身はUTF8_BOMなし_インデント2()
    {
        using var dir = new TempDir();
        var paths = AppPaths.UnderOneRoot(dir.Path);
        var store = new UserLookStore(paths.LooksDir, Catalog.Value);
        var look = store.Save("  夕方の色  ", Vivid);
        Check.Equal("夕方の色", look.Name, "前後の空白を除いた名前");
        Check.Equal(Path.Combine(paths.LooksDir, "look-" + Hash8("夕方の色") + ".json"), look.Path, "ファイルの場所");
        var bytes = File.ReadAllBytes(look.Path);
        Check.True(!(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "BOM がある");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Check.True(!text.Contains('\r'), "改行は LF");
        Check.True(text.StartsWith("{\n  \"schema\": 1,\n  \"name\": \"夕方の色\",\n  \"effects\": [\n    {\n      \"id\": \"saturation\",\n      \"amount\": 100,\n      \"params\": {\n        \"saturation\": 25,\n", StringComparison.Ordinal),
            "§9.11 の 5 の形(インデント 2): " + text[..Math.Min(200, text.Length)]);
        Check.True(!Directory.GetFiles(paths.LooksDir, "*.tmp").Any(), ".tmp が残っている");

        // 読み直すと同じ中身
        var again = new UserLookStore(paths.LooksDir, Catalog.Value);
        again.Load();
        Check.Equal(1, again.Looks.Count, "読み直した数");
        Check.Equal("夕方の色", again.Looks[0].Name, "名前");
        Check.SequenceEqual(Vivid.Select(e => e.Id).ToList(), again.Looks[0].Effects.Select(e => e.Id).ToList(), "id");
        for (var i = 0; i < Vivid.Count; i++)
        {
            Check.Equal(Vivid[i].Amount, again.Looks[0].Effects[i].Amount, "amount");
            Check.SequenceEqual(Vivid[i].Params, again.Looks[0].Effects[i].Params, "params");
        }
    }

    [Test]
    public void AC26_4_E29_同名の保存は同じファイルの中身を置き換える()
    {
        using var dir = new TempDir();
        var store = new UserLookStore(dir.Path, Catalog.Value);
        var a = store.Save("ルックA", Vivid);
        var b = store.Save(" ルックA ", Mono);
        Check.Equal(a.Path, b.Path, "同じファイル");
        Check.Equal(1, store.Looks.Count, "数");
        Check.Equal(1, Directory.GetFiles(dir.Path).Length, "ファイルの数");
        var again = new UserLookStore(dir.Path, Catalog.Value);
        again.Load();
        Check.SequenceEqual(Mono.Select(e => e.Id).ToList(), again.Looks[0].Effects.Select(e => e.Id).ToList(), "置き換わった中身");
        Check.True(store.Find("ルックA") is not null && store.Find("ルックa") is null, "同名の判定は前後の空白を除いたバイト列(大文字と小文字は区別する)");
    }

    [Test]
    public void AC26_4_8桁が衝突する2つの名前では後から保存した方の末尾に_2が付く()
    {
        using var dir = new TempDir();
        var store = new UserLookStore(dir.Path, Catalog.Value, hash8: _ => "deadbeef");
        var a = store.Save("一つ目", Vivid);
        var b = store.Save("二つ目", Mono);
        var c = store.Save("三つ目", Mono);
        Check.Equal("look-deadbeef.json", Path.GetFileName(a.Path), "先に保存した方");
        Check.Equal("look-deadbeef-2.json", Path.GetFileName(b.Path), "後から保存した方");
        Check.Equal("look-deadbeef-3.json", Path.GetFileName(c.Path), "3 つ目");
        var again = new UserLookStore(dir.Path, Catalog.Value);
        again.Load();
        Check.SequenceEqual(["一つ目", "三つ目", "二つ目"], again.Looks.Select(l => l.Name).ToList(), "3 つとも読める(名前の順)");
    }

    [Test]
    public void AC26_4_名前の変更と削除ができ別のものと同じ名前に変えるとその別のものを消す()
    {
        using var dir = new TempDir();
        var store = new UserLookStore(dir.Path, Catalog.Value);
        var a = store.Save("あ", Vivid);
        var b = store.Save("い", Mono);
        var renamed = store.Rename(a, "う");
        Check.Equal("う", renamed.Name, "変更後の名前");
        Check.Equal("look-" + Hash8("う") + ".json", Path.GetFileName(renamed.Path), "新しい名前のファイル");
        Check.True(!File.Exists(a.Path), "古いファイルが残っている");
        Check.SequenceEqual(Vivid.Select(e => e.Id).ToList(), renamed.Effects.Select(e => e.Id).ToList(), "中身はそのまま");
        // い → う(別のものと同じ名前): う(元の あ)を消して、い を う として残す
        var merged = store.Rename(store.Find("い")!, "う");
        Check.Equal(1, store.Looks.Count, "数");
        Check.SequenceEqual(Mono.Select(e => e.Id).ToList(), merged.Effects.Select(e => e.Id).ToList(), "残るのは変更した方");
        Check.Equal(1, Directory.GetFiles(dir.Path).Length, "ファイルの数");
        store.Delete(merged);
        Check.Equal(0, store.Looks.Count, "削除");
        Check.Equal(0, Directory.GetFiles(dir.Path).Length, "ファイルが消えた");
        _ = b;
    }

    [Test]
    public void AC26_4_E29_名前は1から40コードポイントで改行を取り除き貼り付けは40で切る()
    {
        Check.True(!LookNames.IsValid(""), "空");
        Check.True(!LookNames.IsValid("   "), "空白だけ");
        Check.True(LookNames.IsValid(new string('あ', 40)), "40 文字");
        Check.True(!LookNames.IsValid(new string('あ', 41)), "41 文字");
        var emoji = string.Concat(Enumerable.Repeat("😀", 41));
        Check.Equal(40, LookNames.LimitInput(emoji).EnumerateRunes().Count(), "サロゲートの組もコードポイントで数える");
        Check.True(LookNames.IsValid(LookNames.LimitInput(emoji)), "40 コードポイントのサロゲートの組は有効");
        Check.Equal("ab", LookNames.LimitInput("a\r\nb"), "改行を取り除く");
        Check.Equal(new string('x', 40), LookNames.LimitInput(new string('x', 55)), "先頭の 40 文字");
    }

    [Test]
    public void AC26_4_読み込みは壊れたファイルを飛ばし範囲外の値を直してログに書く()
    {
        using var dir = new TempDir();
        dir.Write("look-00000001.json", Encoding.UTF8.GetBytes("{ 壊れている"));
        dir.Write("look-00000002.json", Encoding.UTF8.GetBytes("{\"schema\":2,\"name\":\"新しい版\",\"effects\":[]}"));
        dir.Write("look-00000003.json", Encoding.UTF8.GetBytes(
            "{\"schema\":1,\"name\":\"直す\",\"effects\":[{\"id\":\"contrast\",\"amount\":150,\"params\":{\"c\":500}},{\"id\":\"rotate\",\"params\":{}},{\"id\":\"no_such\"}]}"));
        var log = new MemoryLogSink();
        var store = new UserLookStore(dir.Path, Catalog.Value, log);
        store.Load();
        Check.Equal(1, store.Looks.Count, "読めたのは 1 つ");
        var e = store.Looks[0].Effects;
        Check.SequenceEqual(["contrast"], e.Select(x => x.Id).ToList(), "形ステージと無い id は外す");
        Check.Equal(100.0, e[0].Amount, "amount は 100 に直す");
        Check.Equal(new NumberValue(100) as ParamValue, e[0].Get("c"), "c は範囲の端");
        Check.True(log.Events.Count(x => x.Name == "param_clamped") == 2, "param_clamped を 2 行");
    }
}
