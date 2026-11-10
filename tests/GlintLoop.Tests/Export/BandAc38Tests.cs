// AC-38 のうち担当 band の分: (7) 容量上限のバイト数の直接入力と「上限なし」、(8) 書き出しプリセット(FR-77)の保存・読み込み・名前の変更・削除とファイルの形。
// あわせて、書き出し帯の出力設定の操作(EditorSession.Output。Undo の 1 件・SessionChange.Output・組み込みの容量上限のプリセットの一度だけの写し)を調べる。
// プリセットのファイルは一時フォルダの AppPaths(INV-18)。画面の部分は画面の外に立てたアプリで、UIA と部品の直接の操作で行う。
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using GlintLoop.App.Export;
using GlintLoop.App.Looks;
using GlintLoop.Core.Export;
using GlintLoop.Core.Looks;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;
using static GlintLoop.Tests.Session.SessionTestKit;

namespace GlintLoop.Tests.Exports;

public sealed class BandAc38Tests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // ---------------------------------------------------------------- 出力設定の操作

    [Test]
    public void AC38_7_容量上限はバイト数の直接入力と上限なしを1件のUndoで持つ()
    {
        var s = WithBase();
        var changes = new List<SessionChange>();
        s.Changed += c => changes.Add(c);
        var undo = s.UndoCount;
        Check.True(s.SetOutputLimit(new OutputLimit("bytes", null, 2000)).Changed, "バイト数を入れる");
        Check.Equal(new OutputLimit("bytes", null, 2000), s.Document!.Output.Limit, "直接入力の上限");
        Check.Equal(undo + 1, s.UndoCount, "Undo の 1 件");
        Check.True(changes.Single().HasFlag(SessionChange.Output), "SessionChange.Output が上がる");
        s.SetOutputLimit(new OutputLimit("bytes", null, 0));
        Check.Equal(OutputSettingsJson.MinLimitBytes, s.Document!.Output.Limit.Bytes, "範囲外は端に直す");
        s.SetOutputLimit(new OutputLimit("none", null, null));
        Check.Equal(new OutputLimit("none", null, null), s.Document!.Output.Limit, "上限なし");
        s.Undo();
        Check.Equal("bytes", s.Document!.Output.Limit.Kind, "Undo で戻る");
    }

    [Test]
    public void AC38_出力設定の操作は範囲を直し組み込みのプリセットは形式と長辺とループを一度だけ写す()
    {
        var s = WithBase();
        s.SetOutputFormat(ExportFormat.Jpeg);
        Check.Equal(ExportFormat.Jpeg, s.Document!.Output.Format, "形式");
        Check.Throws<ArgumentException>(() => s.SetOutputFormat("bmp"), "知らない形式");
        s.SetOutputSize("long", 9000);
        Check.Equal(new OutputSize("long", OutputSettingsJson.MaxSizePx), s.Document!.Output.Size, "長辺は 4096 まで");
        s.SetOutputSize("auto", 12);
        Check.Equal(new OutputSize("auto", null), s.Document!.Output.Size, "auto は値を持たない");
        s.SetGifOptions(s.Document!.Output.Gif with { Colors = 1, Dither = "x", Matte = "#00ff00" });
        Check.Equal(new GifOptions(2, "blue", "#00FF00", true), s.Document!.Output.Gif, "GIF のオプションの直し");
        s.SetJpegOptions(new JpegOptions(101, "bad"));
        Check.Equal(new JpegOptions(100, OutputSettings.Default.Jpeg.Background), s.Document!.Output.Jpeg, "JPEG のオプションの直し");

        s.SetApngLoops(3);
        Check.Equal((LoopMode.Once, 3L), (s.Document!.Time.Loop, s.Document.Output.Apng.Loops), "APNG の 3 回");
        s.SetApngLoops(0);
        Check.Equal(LoopMode.Infinite, s.Document!.Time.Loop, "APNG の無限");

        var line = BandLimitPresets.Builtin.Single(p => p.Key == "line_stamp");
        var undo = s.UndoCount;
        s.ApplyLimitPreset(line.Key, line.Format, line.LongSide, line.Loop);
        var o = s.Document!.Output;
        Check.Equal((ExportFormat.Apng, "long", 320.0, "preset", "line_stamp", LoopMode.Once, 1L),
            (o.Format, o.Size.Mode, o.Size.Value!.Value, o.Limit.Kind, o.Limit.Key!, s.Document.Time.Loop, o.Apng.Loops), "line_stamp を選んだあと");
        Check.Equal(undo + 1, s.UndoCount, "プリセットの適用は Undo の 1 件");
        s.ApplyLimitPreset("none", null, null, null);
        Check.Equal((ExportFormat.Apng, "none", 320.0), (s.Document!.Output.Format, s.Document.Output.Limit.Kind, s.Document.Output.Size.Value!.Value),
            "上限なしは形式と長辺を変えない");
        Check.SequenceEqual(["none", "discord_emoji", "discord_sticker", "slack_emoji", "line_stamp", "chat_small", "chat_medium"],
            BandLimitPresets.Builtin.Select(p => p.Key).ToArray(), "組み込みのプリセットの並び(§9.9 の表の順)");
    }

    // ---------------------------------------------------------------- 書き出しプリセットのファイル(8)

    [Test]
    public void AC38_8_書き出しプリセットはexport8桁のjsonに保存し読み込み名前の変更と削除ができる()
    {
        using var dir = new TempDir();
        var presetsDir = System.IO.Path.Combine(dir.Path, "presets");
        var store = new UserExportPresetStore(presetsDir);
        var output = OutputSettings.Default with
        {
            Format = ExportFormat.Webp,
            Size = new OutputSize("width", 300),
            Limit = new OutputLimit("bytes", null, 123456),
            Webp = new WebpOptions(70, true),
        };
        var saved = store.Save("  小さい WebP ", output);
        Check.Equal("小さい WebP", saved.Name, "名前は前後の空白を除いて持つ");
        var file = System.IO.Path.Combine(presetsDir, "export-" + LookNames.Hash8("小さい WebP") + ".json");
        Check.Equal(file, saved.Path, "ファイルの名前は export-<8桁>.json");
        Check.True(File.Exists(file), "ファイルが置かれる");

        var bytes = File.ReadAllBytes(file);
        Check.True(bytes.Length < 3 || !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), "BOM なし");
        using (var json = JsonDocument.Parse(bytes))
        {
            var r = json.RootElement;
            Check.SequenceEqual(["schema", "name", "output"], r.EnumerateObject().Select(p => p.Name).ToArray(), "{\"schema\":1,\"name\":…,\"output\":{…}} の形");
            Check.Equal(1, r.GetProperty("schema").GetInt32(), "schema");
            Check.Equal("小さい WebP", r.GetProperty("name").GetString(), "name");
            var o = r.GetProperty("output");
            Check.SequenceEqual(["format", "size", "limit", "options"], o.EnumerateObject().Select(p => p.Name).ToArray(), "output の項目(文書の output と同じ形)");
            Check.Equal("webp", o.GetProperty("format").GetString(), "format");
            Check.Equal(300, o.GetProperty("size").GetProperty("value").GetInt32(), "size.value");
            Check.Equal(123456, o.GetProperty("limit").GetProperty("bytes").GetInt64(), "limit.bytes");
            Check.True(o.GetProperty("options").GetProperty("webp").GetProperty("lossless").GetBoolean(), "options.webp.lossless");
        }

        // 読み込み(起動し直し)
        var again = new UserExportPresetStore(presetsDir);
        again.Load();
        Check.Equal(1, again.Presets.Count, "読み込んだ数");
        Check.Equal(OutputSettingsJson.Normalize(output), again.Presets[0].Output, "読み込んだ出力設定");

        // 名前の変更: 新しい名前の 8 桁のファイルに書き直し、古いファイルを消す
        var renamed = again.Rename(again.Presets[0], "WebP 小");
        Check.Equal(System.IO.Path.Combine(presetsDir, "export-" + LookNames.Hash8("WebP 小") + ".json"), renamed.Path, "名前の変更のあとのファイル");
        Check.True(!File.Exists(file), "古いファイルが残っている");
        var third = new UserExportPresetStore(presetsDir);
        third.Load();
        Check.Equal("WebP 小", third.Presets.Single().Name, "変更した名前を読み込む");

        // 削除(確認なし)
        third.Delete(third.Presets[0]);
        Check.Equal(0, third.Presets.Count, "削除のあと");
        Check.True(Directory.GetFiles(presetsDir).Length == 0, "削除のあとにファイルが残っている");
    }

    [Test]
    public void AC38_8_同じ8桁は番号を付け壊れたファイルは読み飛ばし並びは名前の順()
    {
        using var dir = new TempDir();
        var store = new UserExportPresetStore(dir.Path, _ => "deadbeef");
        var b = store.Save("b", OutputSettings.Default);
        var a = store.Save("a", OutputSettings.Default with { Format = ExportFormat.Png });
        Check.Equal("export-deadbeef.json", System.IO.Path.GetFileName(b.Path), "1 つ目");
        Check.Equal("export-deadbeef-2.json", System.IO.Path.GetFileName(a.Path), "同じ 8 桁の 2 つ目");
        Check.SequenceEqual(["a", "b"], store.Presets.Select(p => p.Name).ToArray(), "名前の順");
        store.Save("a", OutputSettings.Default with { Format = ExportFormat.Jpeg });
        Check.Equal(ExportFormat.Jpeg, store.Find("a")!.Output.Format, "同じ名前は中身を置き換える");
        Check.Equal(2, Directory.GetFiles(dir.Path).Length, "置き換えでファイルは増えない");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "export-broken1.json"), "{ not json");
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "export-broken2.json"), "{\"schema\":2,\"name\":\"x\",\"output\":{}}");
        var load = new UserExportPresetStore(dir.Path);
        load.Load();
        Check.SequenceEqual(["a", "b"], load.Presets.Select(p => p.Name).ToArray(), "壊れたものは読み飛ばす");
        Check.Throws<ArgumentException>(() => store.Save("   ", OutputSettings.Default), "空の名前");
    }

    // ---------------------------------------------------------------- 画面(7)(8)

    [Test]
    public void AC38_7_帯のバイト数の直接入力と上限なしの選択が文書の上限になり書き出しに渡る()
    {
        var pipe = new FakePipeline();
        using var b = BandUiAc39Tests.Start(pipe);
        var view = b.App.Ui(() => b.Module.View);
        b.App.Ui(() =>
        {
            view.LimitBytes.Text = "2000";
            view.LimitBytes.Commit();
        });
        UiHost.Idle();
        b.App.Ui(() =>
        {
            Check.Equal(new OutputLimit("bytes", null, 2000), b.App.Shell.Session.Document!.Output.Limit, "直接入力のあとの上限");
            Check.Equal(-1, view.Limit.SelectedIndex, "直接入力のときは一覧の項目を選ばない");
            Check.Equal("2000", view.LimitBytes.Text, "直接入力の欄");
            // 入力していないまま確定しても(フォーカスが外れたときなど)上限を変えない
            b.App.Shell.Session.SetOutputLimit(new OutputLimit("none", null, null));
        });
        UiHost.Idle();
        b.App.Ui(() =>
        {
            view.LimitBytes.Commit();
            Check.Equal("none", b.App.Shell.Session.Document!.Output.Limit.Kind, "打っていない確定で上限が変わった");
            Check.Equal(0, view.Limit.SelectedIndex, "上限なしの項目を選ぶ");
        });
        var root = Uia.Root(b.App.Hwnd);
        Check.Equal("容量上限", Uia.Find(root, "容量上限").Current.Name, "一覧の UIA の名前");
        Check.Equal("バイト数", Uia.Find(root, "バイト数").Current.Name, "直接入力の UIA の名前");

        b.App.Ui(() =>
        {
            view.LimitBytes.Text = "5000";
            view.LimitBytes.Commit();
        });
        UiHost.Idle();
        Check.Equal(5000L, b.App.Ui(() => b.App.Shell.Session.Document!.Output.Limit.Bytes), "2 回目の直接入力");
        b.App.Ui(() => view.Limit.SelectedIndex = 0);
        UiHost.Idle();
        Check.Equal("none", b.App.Ui(() => b.App.Shell.Session.Document!.Output.Limit.Kind), "上限なしを選んだあと");
        b.Advance(600);
        b.WaitDone("上限なし");
        Check.Equal("none", pipe.Jobs.Last().Document.Output.Limit.Kind, "上限なしの文書が書き出しに渡る(調整しないのは担当 export の探索)");

        // 組み込みのプリセットを選ぶと一度だけ写す
        b.App.Ui(() => view.Limit.SelectedIndex = 1);
        UiHost.Idle();
        var o = b.App.Ui(() => b.App.Shell.Session.Document!.Output);
        Check.Equal((ExportFormat.Gif, "discord_emoji", 128.0), (o.Format, o.Limit.Key!, o.Size.Value!.Value), "discord_emoji を選んだあと");
        Check.Equal(1, b.App.Ui(() => view.Limit.SelectedIndex), "一覧は文書の上限に合わせる");
        Check.True(b.App.Ui(() => view.Limit.Items.OfType<ComboBoxItem>().Take(BandLimitPresets.Builtin.Count).All(i => i.ContextMenu is null)),
            "組み込みのプリセットには名前の変更と削除のメニューが無い");
    }

    [Test]
    public void AC38_8_帯から現在の設定を保存しユーザーのプリセットを選ぶと出力設定を一度だけ写し名前の変更と削除ができる()
    {
        using var b = BandUiAc39Tests.Start(new FakePipeline());
        var view = b.App.Ui(() => b.Module.View);
        b.App.Ui(() =>
        {
            b.App.Shell.Session.SetOutputFormat(ExportFormat.Jpeg);
            b.App.Shell.Session.SetJpegOptions(new JpegOptions(60, OutputSettings.Default.Jpeg.Background));
        });
        UiHost.Idle();
        var saveIndex = b.App.Ui(() => view.Limit.Items.Count - 1);
        b.App.Ui(() => view.Limit.SelectedIndex = saveIndex);
        UiHost.Idle();
        var dialog = b.App.Ui(() => b.App.Shell.Dialogs.Top as NameInputDialog);
        Check.True(dialog is not null, "名前の入力のダイアログが出ない");
        b.App.Ui(() =>
        {
            dialog!.Input.Text = "JPEG 60";
            dialog.Confirm.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => b.Module.Presets.Presets.Count == 1, Wait), "保存されない");
        var path = b.App.Ui(() => b.Module.Presets.Presets[0].Path);
        Check.True(path.StartsWith(b.App.Paths.PresetsDir, StringComparison.OrdinalIgnoreCase), "一時フォルダの presets に置く(INV-18)");
        Check.True(File.Exists(path), "ファイルがある");
        Check.True(b.App.Ui(() => view.UserPresetItem("JPEG 60") is { ContextMenu: not null }), "ユーザーのプリセットは一覧にあり、メニューを持つ");
        Check.True(b.App.Ui(() => view.Limit.SelectedItem is ComboBoxItem { Tag: LimitPresetItem { Key: "none" } }), "保存のあとの一覧は文書の上限に戻る");

        // 出力設定を変えてからプリセットを選ぶと、一度だけ写す
        b.App.Ui(() => b.App.Shell.Session.SetOutputFormat(ExportFormat.Gif));
        UiHost.Idle();
        b.App.Ui(() => view.Limit.SelectedItem = view.UserPresetItem("JPEG 60"));
        UiHost.Idle();
        var o = b.App.Ui(() => b.App.Shell.Session.Document!.Output);
        Check.Equal((ExportFormat.Jpeg, 60L), (o.Format, o.Jpeg.Quality), "プリセットの出力設定を写す");
        Check.True(b.App.Ui(() => view.Limit.SelectedItem is ComboBoxItem { Tag: LimitPresetItem { Key: "none" } }), "一覧はプリセットを選んだままにしない");

        // 名前の変更と削除(コンテキストメニュー)
        var menu = b.App.Ui(() => view.UserPresetItem("JPEG 60")!.ContextMenu!);
        b.App.Ui(() => ((MenuItem)menu.Items[0]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent)));
        UiHost.Idle();
        var rename = b.App.Ui(() => b.App.Shell.Dialogs.Top as NameInputDialog);
        Check.True(rename is { Mode: NameDialogMode.Rename }, "名前の変更のダイアログ");
        b.App.Ui(() =>
        {
            rename!.Input.Text = "JPEG 低";
            rename.Confirm.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        });
        Check.True(UiHost.WaitUntil(() => b.Module.Presets.Presets.Single().Name == "JPEG 低", Wait), "名前が変わらない");
        Check.True(!File.Exists(path), "古いファイルが残っている");
        Check.True(b.App.Ui(() => view.UserPresetItem("JPEG 低") is not null), "一覧の名前が変わる");
        var menu2 = b.App.Ui(() => view.UserPresetItem("JPEG 低")!.ContextMenu!);
        b.App.Ui(() => ((MenuItem)menu2.Items[1]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent)));
        Check.Equal(0, b.App.Ui(() => b.Module.Presets.Presets.Count), "削除(確認なし)");
        Check.True(b.App.Ui(() => view.UserPresetItem("JPEG 低") is null), "一覧から消える");
        Check.True(b.App.Ui(() => b.App.Shell.Dialogs.Top) is null, "削除に確認を出した");
    }

    [Test]
    public void AC38_帯の部品は93の並びと幅で置かれコピー保存ドラッグ欄は無効()
    {
        using var b = BandUiAc39Tests.Start(new FakePipeline());
        var root = Uia.Root(b.App.Hwnd);
        b.App.Ui(() =>
        {
            var v = b.Module.View;
            Check.True(ReferenceEquals(v, b.App.Shell.Window.ExportBandHost.Content), "帯は ExportBandHost に入る");
            var r = GlintLoop.App.Theme.AppResources.LayoutTokens;
            Check.Equal((double)r["ExportFormatWidth"], v.Format.Width, "形式の幅");
            Check.Equal((double)r["ExportOptionsSize"], v.Options.Width, "出力オプションの幅");
            Check.Equal((double)r["ExportSizeWidth"], ((System.Windows.FrameworkElement)v.SizeValue.Parent).Width, "サイズの幅");
            Check.Equal((double)r["ExportLimitWidth"], ((System.Windows.FrameworkElement)v.Limit.Parent).Width, "容量上限の幅");
            Check.Equal((double)r["ExportStatusMinWidth"], v.Status.MinWidth, "状態の最小の幅");
            Check.Equal((double)r["ExportCopyMinWidth"], v.Copy.MinWidth, "コピーの最小の幅");
            Check.Equal((double)r["ExportSaveMinWidth"], v.Save.MinWidth, "保存の最小の幅");
            Check.Equal(((double)r["DragAreaWidth"], (double)r["DragAreaHeight"]), (v.Drag.Width, v.Drag.Height), "ドラッグ欄の大きさ");
            Check.True(v.Copy.IsEnabled && v.Save.IsEnabled && !v.Drag.IsEnabled, "文書があればコピーと保存は押せて(待機になる)、完成前のドラッグ欄は無効(INV-9)");
            var order = new System.Windows.UIElement[] { v.Format, v.Options, (System.Windows.UIElement)v.SizeValue.Parent, (System.Windows.UIElement)v.Limit.Parent, v.Status, v.Copy, v.Save, v.Drag };
            for (var i = 0; i < order.Length; i++) Check.Equal(i, Grid.GetColumn(order[i]), $"{i + 1} 番目の部品の列");
            Check.True(!v.Options.IsEnabled || b.App.Shell.Session.Document!.Output.Format != ExportFormat.Png, "PNG のとき出力オプションは無効");
            b.App.Shell.Session.SetOutputFormat(ExportFormat.Png);
        });
        UiHost.Idle();
        Check.True(!b.App.Ui(() => b.Module.View.Options.IsEnabled), "PNG のとき出力オプションは無効");
        foreach (var name in new[] { "形式", "出力オプション", "出力サイズ", "容量上限", "コピー", "保存" })
            Check.True(Uia.TryFind(root, name) is not null, $"UIA で「{name}」が見つからない");
    }

    [Test]
    public void AC38_出力オプションのフライアウトは形式の項目だけを並べ値が文書に入る()
    {
        using var b = BandUiAc39Tests.Start(new FakePipeline());
        var v = b.App.Ui(() => b.Module.View);
        // フライアウトは開かない(ポップアップは実際の画面に出る。INV-18)。中身だけを作って調べる
        b.App.Ui(() => v.Flyout.Rebuild());
        string[] Names() => b.App.Ui(() => v.Flyout.Body.Children.OfType<System.Windows.FrameworkElement>()
            .Where(e => e is not TextBlock).Select(e => AutomationProperties.GetName(e)).ToArray());
        Check.SequenceEqual(["色数", "ディザ", "", "差分の最適化"], Names(), "GIF の項目");
        b.App.Ui(() =>
        {
            var colors = (GlintLoop.App.Controls.NumberBox)v.Flyout.Body.Children.OfType<System.Windows.FrameworkElement>().First(e => AutomationProperties.GetName(e) == "色数");
            colors.Text = "64";
            colors.Commit();
        });
        UiHost.Idle();
        Check.Equal(64L, b.App.Ui(() => b.App.Shell.Session.Document!.Output.Gif.Colors), "色数が文書に入る");
        b.App.Ui(() => b.App.Shell.Session.SetOutputFormat(ExportFormat.Webp));
        UiHost.Idle();
        b.App.Ui(() => v.Flyout.Rebuild());
        Check.SequenceEqual(["", "ロスレス"], Names(), "WebP の項目(品質の行とロスレス)");
        b.App.Ui(() => b.App.Shell.Session.SetOutputFormat(ExportFormat.Apng));
        UiHost.Idle();
        b.App.Ui(() => v.Flyout.Rebuild());
        Check.SequenceEqual(["色", "ループ"], Names(), "APNG の項目");
        b.App.Ui(() =>
        {
            var loop = (ComboBox)v.Flyout.Body.Children.OfType<System.Windows.FrameworkElement>().First(e => AutomationProperties.GetName(e) == "ループ");
            loop.SelectedIndex = 2;
        });
        UiHost.Idle();
        Check.Equal((LoopMode.Once, 2L), b.App.Ui(() => (b.App.Shell.Session.Document!.Time.Loop, b.App.Shell.Session.Document.Output.Apng.Loops)), "APNG の 2 回");
    }
}
