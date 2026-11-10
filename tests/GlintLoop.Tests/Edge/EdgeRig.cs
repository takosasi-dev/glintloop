// AC-55 のテストの道具: 画面の外のアプリに、デコードの呼び出しの数え(ImageImporter.BeforeDecode)・偽のクリップボード・偽のダイアログ・偽のファイルシステムをつなぎ、取り込みの操作を 1 回ずつ動かす。
// Reject は「通知(キーと種別)・ログ(event と reason)を確かめ、文書の JSON・履歴の件数・選択・ウェルカム画面(GPU があればキャンバスの画素)が変わらない」を 1 つで調べる。
// 偽のストリームと、33 バイトの PNG(署名と IHDR だけ)もここに置く。
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Screens;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Export;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Session;
using GlintLoop.Render;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

/// <summary>操作の前後で変わってはいけないもの。</summary>
internal sealed record EditState(bool HasDocument, string? Json, int Undo, int Redo, Target? Selected, bool Welcome, byte[]? Pixels, object? Presented);

internal sealed class EdgeRig : IDisposable
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private int _decodes;

    private EdgeRig(TestApp app, bool gpu)
    {
        App = app;
        Gpu = gpu;
        Screens = EditingScreenKit.M<ScreensModule>(app);
        Dir = new TempDir();
        app.Ui(() => Imports.Importer.BeforeDecode = () =>
        {
            Interlocked.Increment(ref _decodes);
            OnDecode?.Invoke();
        });
    }

    public static EdgeRig Start(bool gpu = false) =>
        new(TestApp.Start(gpu: gpu ? GpuAdapterRequest.Warp : null, clock: FakeClock.ForPhase5()), gpu);

    public TestApp App { get; }

    public bool Gpu { get; }

    public ScreensModule Screens { get; }

    public ImportCoordinator Imports => Screens.Imports;

    public TempDir Dir { get; }

    public EditorSession Session => App.Shell.Session;

    /// <summary>これまでのデコードの呼び出しの回数(ヘッダーを読むだけでは増えない)。</summary>
    public int Decodes => Volatile.Read(ref _decodes);

    /// <summary>デコードの呼び出しの中で動かす処理(止める・例外を投げる)。取り込みのスレッドで呼ばれる。</summary>
    public Action? OnDecode { get; set; }

    public void Dispose()
    {
        OnDecode = null;
        App.Dispose();
        Dir.Dispose();
    }

    // ---------------- 取り込みの操作 ----------------

    private ImportOutcome? Run(Func<Task<ImportOutcome?>> op)
    {
        var t = App.Ui(op);
        Check.True(UiHost.WaitUntil(() => t.IsCompleted, Limit), "取り込みが終わらない");
        UiHost.Idle();
        return t.GetAwaiter().GetResult();
    }

    /// <summary>偽のクリップボードに data を入れて貼り付ける。</summary>
    public ImportOutcome? Paste(IImportData data)
    {
        App.Clipboard.Data = data;
        return Run(() => Imports.PasteAsync());
    }

    /// <summary>メモリ上の DataObject を、製品の変換(DataObjectImportData)を通して貼り付ける。</summary>
    public ImportOutcome? PasteObject(IDataObject data) => Paste(GlintLoop.App.Platform.DataObjectImportData.From(data));

    public ImportOutcome? PastePng(byte[] png) => Paste(new MemoryImportData().With(ImportFormatNames.Png, png));

    public ImportOutcome? PasteFiles(params string[] paths) => PasteObject(new DataObject(DataFormats.FileDrop, paths));

    /// <summary>ファイルのドロップ(WPF の DataObject を製品の変換に通す)。</summary>
    public ImportOutcome? DropFiles(params string[] paths) => Run(() => Imports.DropAsync(new DataObject(DataFormats.FileDrop, paths)));

    /// <summary>偽の開くダイアログが paths を返す「開く」。</summary>
    public ImportOutcome? Open(params string[] paths)
    {
        App.FileDialogs.OpenAnswers.Enqueue(paths);
        return Run(() => Imports.OpenAsync());
    }

    public ImportOutcome? Arg(string path) => Run(() => Imports.StartupArgsAsync([path]));

    /// <summary>ウィンドウへドロップのイベントを発生させる(DragEventArgs のコンストラクターは公開されていないのでリフレクションで作る)。取り込みが始まらないときもすぐ戻る。</summary>
    public void RaiseDrop(IDataObject data) => App.Ui(() =>
    {
        var target = App.Shell.Window.RootLayer;
        var ctor = typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(10, 10)]);
        args.RoutedEvent = DragDrop.DropEvent;
        target.RaiseEvent(args);
    });

    public void WaitImports() => Check.True(UiHost.WaitUntil(() => !Imports.IsImporting && !Imports.Gate.IsBusy, Limit), "取り込みが終わらない");

    public bool Key(Key key, ModifierKeys mods = ModifierKeys.None) => EditingScreenKit.Key(App, key, mods);

    // ---------------- 文書の用意 ----------------

    /// <summary>土台と最前面の 2 枚の文書を作り、土台を選んでおく(失敗で選択が最前面に動いたら分かる)。</summary>
    public void LoadTwoLayers()
    {
        var a = Dir.Write("base-layer.png", SkiaBytes.Png(64, 48, Img.Pattern(64, 48, alpha: false)));
        var b = Dir.Write("top-layer.png", SkiaBytes.Png(32, 24, Img.Pattern(32, 24, alpha: true)));
        Check.Equal(2, DropFiles(a, b)?.Added, "2 枚の文書を作れない");
        Check.True(UiHost.WaitUntil(() => App.Shell.Window.WelcomeHost.Visibility == Visibility.Collapsed, Limit), "ウェルカム画面が消えない");
        WaitPresented();
        App.Ui(() => Session.Select(Target.OfLayer(0)));
        UiHost.Idle();
    }

    /// <summary>GPU があるとき、キャンバスに今の文書の表示が載るまで待つ。</summary>
    public void WaitPresented()
    {
        if (!Gpu) return;
        var canvas = EditingScreenKit.M<CanvasModule>(App);
        Check.True(UiHost.WaitUntil(() => canvas.Picture.Bitmap is not null && !canvas.Processed.IsBusy && ReferenceEquals(canvas.Processed.PresentedDocument, Session.Document), Limit),
            "キャンバスに表示が載らない");
    }

    /// <summary>文書を閉じてウェルカム画面に戻す(次の場面の準備)。</summary>
    public void CloseDocument()
    {
        App.Ui(() => Session.CloseDocument());
        Check.True(UiHost.WaitUntil(() => App.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, Limit), "ウェルカム画面に戻らない");
    }

    public string[] LayerNames => App.Ui(() => Session.Document?.Layers.Select(l => l.Name).ToArray() ?? []);

    public int UndoCount => App.Ui(() => Session.UndoCount);

    // ---------------- 調べる ----------------

    public EditState State() => App.Ui(() =>
    {
        var s = Session;
        var canvas = Gpu && s.HasDocument ? App.Shell.Modules.OfType<CanvasModule>().Single() : null;
        Check.True(canvas is null || !canvas.Processed.IsBusy, "キャンバスの描き直しが動いている");
        return new EditState(s.HasDocument, s.HasDocument ? s.Json : null, s.UndoCount, s.RedoCount, s.HasDocument ? s.Selected : null,
            App.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, canvas?.Picture.CopyPixels(), canvas?.Processed.PresentedDocument);
    });

    public void ExpectSame(EditState before, string what)
    {
        var after = State();
        Check.Equal(before.HasDocument, after.HasDocument, what + ": 文書の有無");
        Check.Equal(before.Json, after.Json, what + ": 文書の JSON");
        Check.Equal(before.Undo, after.Undo, what + ": Undo の件数");
        Check.Equal(before.Redo, after.Redo, what + ": Redo の件数");
        Check.Equal(before.Selected, after.Selected, what + ": 選んでいるレイヤー");
        Check.Equal(before.Welcome, after.Welcome, what + ": ウェルカム画面");
        if (before.Pixels is not null)
        {
            Check.True(after.Pixels is not null && before.Pixels.AsSpan().SequenceEqual(after.Pixels), what + ": キャンバスの画素");
            Check.True(before.Pixels.Any(b => b != 0), what + ": キャンバスの画素が全部 0(空で通っている)");
            Check.True(ReferenceEquals(before.Presented, after.Presented), what + ": 表示の描き直しが起きた");
        }
    }

    public Notice? NoticeNow => App.Ui(() => App.Shell.NoticeBar.Current);

    public void ClearNotice() => App.Ui(() => App.Shell.NoticeBar.Close());

    public void ExpectNotice(string key, NoticeLevel level, string what, params (string Name, string Value)[] args)
    {
        var n = NoticeNow;
        Check.Equal(key, n?.Key, what + ": 通知");
        Check.Equal(level, n!.Level, what + ": 通知の種別");
        foreach (var (name, value) in args) Check.Equal(value, n.Args[name], $"{what}: {{{name}}}");
    }

    public void ExpectMulti(int n, int m, string what) => ExpectNotice(NoticeKeys.ImportMulti, NoticeLevel.Info, what, ("N", n.ToString()), ("M", m.ToString()));

    /// <summary>ログの全部の行(古い順)。</summary>
    public List<JsonElement> LogLines()
    {
        var lines = new List<JsonElement>();
        if (!Directory.Exists(App.Paths.LogsDir)) return lines; // まだ 1 行も書いていない
        foreach (var f in Directory.GetFiles(App.Paths.LogsDir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            while (r.ReadLine() is { } line)
            {
                if (line.Length > 0) lines.Add(JsonDocument.Parse(line).RootElement.Clone());
            }
        }
        return lines;
    }

    public int LogCount(string name) => LogLines().Count(l => l.GetProperty("event").GetString() == name);

    /// <summary>取り込みに関わるログの行の印(import_fail は reason も付ける)。</summary>
    public string[] ImportLog()
    {
        var tags = new List<string>();
        foreach (var l in LogLines())
        {
            var name = l.GetProperty("event").GetString();
            if (name == "import_fail") tags.Add("import_fail:" + l.GetProperty("reason").GetString());
            else if (name is "import_ok" or "clipboard_failed") tags.Add(name);
        }
        return tags.ToArray();
    }

    /// <summary>
    /// 足せない操作を 1 回行う。通知のキーと種別と置き換えの値、増えたログの行(順は問わない)、デコードの呼び出しが増えないこと(decodes で変えられる)、文書と履歴と選択とウェルカム画面が変わらないことを確かめる。
    /// log は import_fail:理由 か clipboard_failed の印の並び(空ならログは書かれない)。
    /// </summary>
    public void Reject(string what, Action op, string key, NoticeLevel level, string[] log, int decodes = 0, params (string Name, string Value)[] args)
    {
        ClearNotice();
        var before = State();
        var logBefore = ImportLog().Length;
        var decodesBefore = Decodes;
        op();
        UiHost.Idle();
        ExpectNotice(key, level, what, args);
        ExpectLog(logBefore, log, what);
        Check.Equal(decodes, Decodes - decodesBefore, what + ": デコードの呼び出し");
        ExpectSame(before, what);
    }

    /// <summary>logBefore より後ろに増えたログの行が expected と同じ組か(順は問わない)。</summary>
    public void ExpectLog(int logBefore, string[] expected, string what)
    {
        var added = ImportLog().Skip(logBefore).Order(StringComparer.Ordinal).ToArray();
        Check.SequenceEqual(expected.Order(StringComparer.Ordinal).ToArray(), added, what + ": ログの行");
    }

    // ---------------- 書き出し ----------------

    public ExportBandModule Band => EditingScreenKit.M<ExportBandModule>(App);

    /// <summary>本物の中身と環境が入るまで待つ(WARP のとき)。</summary>
    public ExportBandModule WaitBand()
    {
        var band = Band;
        Check.True(UiHost.WaitUntil(() => band.Pipeline is RenderExportPipeline && band.Auto.Environment is not null, Limit), "本物の中身が入らない");
        return band;
    }

    /// <summary>偽の時計を 600 ms 進めて、書き出しが完成するまで待つ。</summary>
    public ExportOutcome ExportDone(ExportBandModule band)
    {
        UiHost.Idle();
        App.Ui(() => App.Clock.Advance(AutoExport.Delay));
        Check.True(UiHost.WaitUntil(() => band.Auto.State is ExportState.Done or ExportState.Failed, Limit), "書き出しが終わらない: " + App.Ui(() => band.Auto.State));
        Check.Equal(ExportState.Done, App.Ui(() => band.Auto.State), "書き出しの状態(" + App.Ui(() => band.Auto.Error?.ToString()) + ")");
        return App.Ui(() => band.Auto.Outcome!);
    }

    public string[] TmpFiles() => Directory.Exists(App.Paths.CacheExportsDir) ? Directory.GetFiles(App.Paths.CacheExportsDir, "*.tmp") : [];
}

/// <summary>AC-55 の画像のバイト列。</summary>
internal static class EdgeBytes
{
    public static readonly byte[] ZipHead = [0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0, 0, 0];

    /// <summary>署名と IHDR だけの 33 バイトの PNG(8 ビット RGBA)。</summary>
    public static byte[] PngHeader(int w, int h)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        PngBytes.WriteChunk(o, "IHDR", ihdr);
        var bytes = o.ToArray();
        Check.Equal(33, bytes.Length, "署名と IHDR の長さ");
        return bytes;
    }

    /// <summary>途中で切れた PNG(IHDR は読める。読める 64×64 の半分の長さ)。</summary>
    public static byte[] CutPng(byte seed = 1)
    {
        var px = Img.Pattern(64, 64, alpha: true);
        for (var i = 0; i < px.Length; i += 4) px[i + 2] = (byte)(px[i + 2] ^ seed);
        var png = SkiaBytes.Png(64, 64, px);
        return png[..(png.Length / 2)];
    }

    /// <summary>色が違う小さい PNG。</summary>
    public static byte[] Solid(byte r, byte g, byte b, int w = 8, int h = 6) => SkiaBytes.Png(w, h, Img.Solid(w, h, r, g, b));
}

/// <summary>Length だけが大きく、先頭の head より後ろを読むと例外を投げる偽のストリーム。</summary>
internal sealed class HeadOnlyStream(byte[] head, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position + count > head.Length) throw new InvalidOperationException($"先頭の {head.Length} バイトより後ろを読んだ(位置 {_position}、{count} バイト)");
        Array.Copy(head, _position, buffer, offset, count);
        _position += count;
        return count;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
