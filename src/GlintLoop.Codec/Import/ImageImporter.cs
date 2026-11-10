// 取り込みの手順(仕様書 FR-1・FR-3〜FR-5・FR-7・FR-14・FR-42・FR-57・§9.10 の 5・E-1〜E-14・E-20)。画面は持たない。
// ファイルは渡された順に 1 個ずつ、読む → 形式 → 上限 → 枚数 → 合計 → デコード → GPU の上限の縮小の順に調べ、読めたものを最後に 1 回で文書へ足す。
// 処理は同期で、UI スレッドの外から呼ぶ(INV-5)。同時に 1 つだけ(ImportGate)。ログにパスとファイル名を書かない(INV-15)。
using System.Globalization;
using GlintLoop.Core.Diagnostics;
using GlintLoop.Core.Notices;
using GlintLoop.Core.Platform;

namespace GlintLoop.Codec.Import;

public sealed class ImageImporter
{
    /// <summary>機能レベル 11_0 以上のテクスチャの最大寸法(§9.7 の規則 2)。</summary>
    public const int DefaultMaxTextureSide = 16384;

    private readonly IImportTarget _target;
    private readonly ILogSink _log;
    private readonly IFileSystem _fs;
    // 取り込んだファイルのパス(絶対パス。大文字と小文字を区別しない)→ その画像の SHA-256。保存先の検査(INV-2)に使う

    public ImageImporter(IImportTarget target, ILogSink log, ImportGate? gate = null, int maxTextureSide = DefaultMaxTextureSide, IFileSystem? fileSystem = null)
    {
        if (maxTextureSide < 1) throw new ArgumentOutOfRangeException(nameof(maxTextureSide));
        _target = target;
        _log = log;
        _fs = fileSystem ?? RealFileSystem.Instance;
        Gate = gate ?? new ImportGate();
        MaxTextureSide = maxTextureSide;
    }

    /// <summary>取り込みの門(確認ダイアログと共有する)。</summary>
    public ImportGate Gate { get; }

    /// <summary>import_ok の ms を測る時計(IClock.Monotonic を使う。テストは偽の時計)。</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>アダプターのテクスチャの最大寸法。これを超える辺の画像は 1/2 の箱形縮小で収める。</summary>
    public int MaxTextureSide { get; }

    /// <summary>画素のデコードの直前に呼ぶ口(テスト用。呼び出しを数える・途中で止める・例外を起こす)。製品では null。</summary>
    internal Action? BeforeDecode { get; set; }

    /// <summary>文書の合計のバイト数の上限(テストが差し替える。AC-55 の (16))。</summary>
    internal long TotalBytesLimit { get; set; } = ImportLimits.MaxTotalBytes;

    /// <summary>文書の合計の画素数の上限(テストが差し替える。AC-55 の (16))。</summary>
    internal long TotalPixelsLimit { get; set; } = ImportLimits.MaxTotalPixels;

    // ---------------- 入口 ----------------

    /// <summary>
    /// 貼り付け(Ctrl+V・Ctrl+Shift+V・「画像を貼り付け」。FR-1)。FileDrop(対応形式と判定できるファイルだけを数える)・PNG・DIBV5・DIB の順に試し、
    /// 足せた枚数が 0 のときだけ次へ進む。読む形式が 1 つも無ければ paste.empty(ログなし)。
    /// receivedAt は操作を受け取った時刻(Clock.Monotonic。import_ok の ms の測り始め。§9.11)。省くと、この呼び出しの時刻から測る。
    /// </summary>
    public ImportOutcome Paste(IImportData data, TimeSpan? receivedAt = null) => Run(ImportVia.Paste, receivedAt, () =>
    {
        try
        {
            var pending = ReadDataObject(data, prefilterFiles: true, LayerNames.Paste);
            return pending.Kind == PendingKind.Empty ? Pending.NoticeOnly(Notice.Of(NoticeKeys.PasteEmpty, NoticeLevel.Warn)) : pending;
        }
        catch (ImportDataUnavailableException e)
        {
            return ClipboardFailed(e);
        }
    });

    /// <summary>
    /// ドロップ(FR-3・FR-4・FR-5)。FileDrop があれば、そのすべてのファイルを渡された順に数える(先頭バイトが ZIP のものは読み飛ばして非対応。1 個だけなら開く操作)。
    /// FileDrop が無ければ PNG・DIBV5・DIB の順に試す。URL か文字列しか無いときは import.web_url。
    /// </summary>
    public ImportOutcome Drop(IImportData data, TimeSpan? receivedAt = null) => Run(ImportVia.Drop, receivedAt, () =>
    {
        try
        {
            if (data.Formats.Contains(ImportFormatNames.FileDrop) && data.GetFileList() is { Count: > 0 } files)
            {
                return FilesOrProject(files);
            }
            var pending = ReadDataObject(data, prefilterFiles: false, LayerNames.Fallback);
            if (pending.Kind == PendingKind.Empty)
                return data.HasUrlOrText ? Pending.NoticeOnly(Notice.Of(NoticeKeys.ImportWebUrl, NoticeLevel.Warn)) : Pending.Nothing();
            return pending;
        }
        catch (ImportDataUnavailableException e)
        {
            return ClipboardFailed(e);
        }
    });

    /// <summary>「開く」ダイアログで選んだファイル(FR-6。複数の扱いは FR-4)。先頭バイトが ZIP のファイルが 1 個だけなら開く操作。</summary>
    public ImportOutcome Open(IReadOnlyList<string> paths, TimeSpan? receivedAt = null) => Run(ImportVia.Open, receivedAt, () =>
    {
        if (paths.Count == 0) return Pending.Nothing();
        return FilesOrProject(paths);
    });

    /// <summary>起動引数(FR-7)。最初の 1 個だけをファイルのパスとして使い、2 個目以降は使わない。</summary>
    public ImportOutcome StartupArgs(IReadOnlyList<string> args, TimeSpan? receivedAt = null) => Run(ImportVia.Arg, receivedAt, () =>
    {
        if (args.Count == 0 || string.IsNullOrEmpty(args[0])) return Pending.Nothing();
        return FilesOrProject([args[0]]);
    });

    /// <summary>「サンプルで試す」(FR-14)。コードで生成した 1024×768 の画像を PNG にして、「サンプル」の名前で取り込む。</summary>
    public ImportOutcome Sample() => Run(ImportVia.Sample, null, () =>
    {
        var batch = NewBatch();
        AddBytes(batch, SampleImage.CreatePng(), ImageFormat.Png, LayerNames.Sample);
        return Pending.FromBatch(batch);
    });

    /// <summary>
    /// バッチの 1 個(FR-87): ファイルを取り込みと同じ順に調べてデコードする。文書には足さず、文書の合計(FR-57)を数えず、門にも入らない。
    /// 読めたら画像を、読めなければ null と failure を返す(import_fail を書く)。UI スレッドの外から呼ぶ。
    /// </summary>
    public ImportedImage? LoadSingle(string path, out ImportFailure? failure)
    {
        var b = new Batch();
        // .glint(ZIP の署名)も形式を判定できないので非対応になる
        if (ReadFile(path, out var bytes, out var format, out _) is { } read)
        {
            Fail(b, read);
        }
        else
        {
            AddBytes(b, bytes!, format, LayerNames.FromPath(path));
        }
        foreach (var e in b.FailLogs) _log.Write(e);
        failure = b.Failures.Count > 0 ? b.Failures[^1] : null;
        return b.Added.Count > 0 ? b.Added[0].Image : null;
    }

    // ---------------- 共通の流れ ----------------

    private ImportOutcome Run(ImportVia via, TimeSpan? receivedAt, Func<Pending> body)
    {
        var ticket = Gate.TryEnter();
        if (ticket is null) return ImportOutcome.Simple(ImportStatus.Ignored, via, null, _log);
        var clock = new ImportClock(Clock, receivedAt ?? Clock.Monotonic);
        try
        {
            var p = body();
            switch (p.Kind)
            {
                case PendingKind.Empty:
                    ticket.Dispose();
                    return ImportOutcome.Simple(ImportStatus.NoInput, via, p.Notice, _log);
                case PendingKind.Project:
                    // 開く処理の間(読み込み・確認・最初の表示)も門の中にいる(E-20)。呼び出し側が CompleteDisplay で出る
                    return new ImportOutcome(ImportStatus.ProjectRequested, via, null, 0, [], [], 0, p.ProjectPath, _log, clock, ticket);
                default:
                    return Finish(via, p, clock, ticket);
            }
        }
        catch
        {
            ticket.Dispose();
            throw;
        }
    }

    private ImportOutcome Finish(ImportVia via, Pending p, ImportClock clock, IDisposable ticket)
    {
        if (p.Batch is null)
        {
            ticket.Dispose();
            return ImportOutcome.Simple(ImportStatus.Completed, via, p.Notice, _log);
        }
        var b = p.Batch;
        foreach (var e in b.FailLogs) _log.Write(e);
        var hadDocument = _target.HasDocument;
        var layersAfter = 0;
        if (b.Added.Count > 0) layersAfter = _target.AddImages(b.Added.ToArray(), via);
        var notice = p.Notice ?? DecideNotice(b, hadDocument);
        var outcome = new ImportOutcome(ImportStatus.Completed, via, notice, b.Requested, b.Added.ToArray(), b.Failures.ToArray(),
            layersAfter, null, _log, clock, b.Added.Count > 0 ? ticket : null);
        if (b.Added.Count == 0) ticket.Dispose();
        return outcome;
    }

    /// <summary>
    /// FR-4 の通知の決め方。M=0 は最後に足せなかったものの理由、0&lt;M&lt;N は import.multi、M=N は import.animated・import.gpu_shrunk の最後に当たったもの、
    /// 当たらなければ layer.added(文書を新しく作る取り込みで 1 枚だけのときは出さない)。
    /// </summary>
    internal static Notice? DecideNotice(Batch b, bool hadDocument)
    {
        var m = b.Added.Count;
        if (m == 0) return b.Failures.Count > 0 ? b.Failures[^1].Notice : null;
        if (m < b.Requested)
            return Notice.Of(NoticeKeys.ImportMulti, NoticeLevel.Info,
                ("N", b.Requested.ToString(CultureInfo.InvariantCulture)), ("M", m.ToString(CultureInfo.InvariantCulture)));
        Notice? flagged = null;
        foreach (var layer in b.Added)
        {
            if (layer.Image.Animated) flagged = Notice.Of(NoticeKeys.ImportAnimated, NoticeLevel.Info);
            if (layer.Image.GpuShrunk) flagged = Notice.Of(NoticeKeys.ImportGpuShrunk, NoticeLevel.Info);
        }
        if (flagged is not null) return flagged;
        if (!hadDocument && m == 1) return null;
        return Notice.Of(NoticeKeys.LayerAdded, NoticeLevel.Info);
    }

    private Pending ClipboardFailed(ImportDataUnavailableException e)
    {
        _log.Write(LogEvent.Of("clipboard_failed", ("hr", e.HResult32)));
        return Pending.NoticeOnly(Notice.Of(NoticeKeys.ClipFailed, NoticeLevel.Error));
    }

    // 貼り付けとドロップのデータを、FileDrop(貼り付けのときだけ)・PNG・DIBV5・DIB の順に試す。
    // 足せたものがあればその試行を、どれも足せなければ最後の試行(その通知とログ)を採る。読む形式が 1 つも無ければ Empty。
    private Pending ReadDataObject(IImportData data, bool prefilterFiles, string dataName)
    {
        var formats = data.Formats;
        Batch? last = null;
        if (prefilterFiles && formats.Contains(ImportFormatNames.FileDrop) && data.GetFileList() is { Count: > 0 } files)
        {
            var readable = files.Where(f => SniffFile(f) is not null).ToArray();
            if (readable.Length > 0)
            {
                last = RunFiles(readable);
                if (last.Added.Count > 0) return Pending.FromBatch(last);
            }
        }
        foreach (var (name, isDib) in new[] { (ImportFormatNames.Png, false), (ImportFormatNames.DibV5, true), (ImportFormatNames.Dib, true) })
        {
            if (!formats.Contains(name)) continue;
            last = NewBatch();
            var bytes = data.GetBytes(name);
            if (bytes is null) Fail(last, ImportLimits.Simple(ImportFailReason.DecodeError));
            else AddBytes(last, bytes, isDib ? ImageFormat.Dib : null, dataName);
            if (last.Added.Count > 0) return Pending.FromBatch(last);
        }
        return last is null ? Pending.Nothing() : Pending.FromBatch(last);
    }

    // ---------------- 1 回の取り込みの中の状態 ----------------

    internal sealed class Batch
    {
        public int Layers;
        public long Bytes;
        public long Pixels;
        public int Requested;
        public ImportFailure? Stop;
        public string? ProjectPath;
        public readonly Dictionary<string, ImportedImage> BySha = new(StringComparer.Ordinal);
        public readonly List<ImportedLayer> Added = new();
        public readonly List<ImportFailure> Failures = new();
        public readonly List<LogEvent> FailLogs = new();
    }

    private Batch NewBatch() => new()
    {
        Layers = _target.HasDocument ? _target.LayerCount : 0,
        Bytes = _target.SourceBytesTotal,
        Pixels = _target.SourcePixelsTotal,
    };

    private static void Fail(Batch b, ImportFailure f)
    {
        b.Failures.Add(f);
        b.FailLogs.Add(LogEvent.Of("import_fail", ("reason", f.LogReason)));
    }

    // 渡されたファイルが 1 個で先頭バイトが ZIP なら開く操作(§9.11 の 4 の (1))、それ以外は取り込み(複数の中の ZIP は非対応。FR-4)
    private Pending FilesOrProject(IReadOnlyList<string> paths)
    {
        var b = RunFiles(paths, detectProject: paths.Count == 1);
        return b.ProjectPath is { } project ? Pending.Project(project) : Pending.FromBatch(b);
    }

    private Batch RunFiles(IReadOnlyList<string> paths, bool detectProject = false)
    {
        var b = NewBatch();
        foreach (var path in paths)
        {
            StopIfFull(b);
            if (b.Stop is not null && !detectProject)
            {
                // 上限を超えたファイルより後ろは、判定もデコードもせず同じ理由で足さない(FR-4・E-12・E-13)
                b.Requested++;
                Fail(b, b.Stop);
                continue;
            }
            // 文書が満杯でも、1 個だけなら .glint かどうかは先頭バイトで見る(開く操作は枚数の上限と関係ない)
            var read = ReadFile(path, out var bytes, out var format, out var zip, headOnly: b.Stop is not null);
            if (zip && detectProject)
            {
                b.ProjectPath = path;
                return b;
            }
            if (b.Stop is not null)
            {
                b.Requested++;
                Fail(b, b.Stop);
                continue;
            }
            if (read is not null)
            {
                b.Requested++;
                Fail(b, read);
                continue;
            }
            AddBytes(b, bytes!, format, LayerNames.FromPath(path), path);
        }
        return b;
    }

    // 文書がすでに 32 枚なら、中身を判定する前に「枚数の上限」で止める。取り込みの途中で 32 枚に達したときと同じ扱い(FR-4・E-12)。
    private static void StopIfFull(Batch b)
    {
        if (b.Stop is null && b.Layers >= ImportLimits.MaxLayers) b.Stop = ImportLimits.Simple(ImportFailReason.TooManyLayers);
    }

    // ファイルを読み取り専用(FileShare.ReadWrite)で開き、先頭バイトで形式を判定して、上限以下なら全体を読む(INV-2)。headOnly なら判定だけで戻る。
    private ImportFailure? ReadFile(string path, out byte[]? bytes, out ImageFormat? format, out bool zip, bool headOnly = false)
    {
        bytes = null;
        format = null;
        zip = false;
        try
        {
            using var fs = _fs.OpenRead(path);
            var head = new byte[ImageFormats.SniffLength];
            var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            zip = ImageFormats.IsZipSignature(head.AsSpan(0, n));
            format = ImageFormats.Sniff(head.AsSpan(0, n));
            if (format is null) return ImportLimits.Simple(ImportFailReason.Unsupported);
            if (headOnly) return null;
            var length = fs.Length;
            if (length > ImportLimits.MaxFileBytes) return ImportLimits.FileTooLarge(length);
            fs.Position = 0;
            var all = new byte[length];
            fs.ReadExactly(all);
            bytes = all;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or System.Security.SecurityException or OutOfMemoryException)
        {
            return ImportLimits.Simple(ImportFailReason.ReadError);
        }
    }

    /// <summary>ファイルの先頭バイトで形式を判定する(貼り付けの FileDrop の選り分け)。開けなければ null(数えない)。</summary>
    private ImageFormat? SniffFile(string path)
    {
        try
        {
            using var fs = _fs.OpenRead(path);
            var head = new byte[ImageFormats.SniffLength];
            var n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return ImageFormats.Sniff(head.AsSpan(0, n));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return null;
        }
    }

    // 1 個のバイト列(ファイルの中身かデータ)を数え、上限を調べてデコードし、読めたら batch に足す。
    // format が Dib なら DibReader で、そうでなければ先頭バイトで判定した形式として SkiaSharp で読む。
    private void AddBytes(Batch b, byte[] bytes, ImageFormat? format, string name, string? sourcePath = null)
    {
        b.Requested++;
        StopIfFull(b);
        if (b.Stop is not null)
        {
            Fail(b, b.Stop);
            return;
        }
        if (bytes.LongLength > ImportLimits.MaxFileBytes)
        {
            Fail(b, ImportLimits.FileTooLarge(bytes.LongLength));
            return;
        }

        int width, height;
        ImageFormat fmt;
        if (format == ImageFormat.Dib)
        {
            if (DibReader.ReadHeader(bytes) is not { } dh)
            {
                Fail(b, ImportLimits.Simple(ImportFailReason.DecodeError));
                return;
            }
            (fmt, width, height) = (ImageFormat.Dib, dh.Width, dh.Height);
        }
        else
        {
            if (ImageDecoder.ReadHeader(bytes, out var why) is not { } hh)
            {
                // ファイルは先頭バイトで判定済み。データ(PNG の形式名)は、中身が 6 形式のどれでもなければ壊れているとみなす
                Fail(b, ImportLimits.Simple(format is null && why == ImportFailReason.Unsupported ? ImportFailReason.DecodeError : why));
                return;
            }
            (fmt, width, height) = (hh.Format, hh.Width, hh.Height);
        }

        if (ImportLimits.CheckSingle(bytes.LongLength, width, height) is { } single)
        {
            Fail(b, single);
            return;
        }

        var sha = ImportedImage.ComputeSha256(bytes);
        var pixels = (long)width * height;
        var counted = b.BySha.ContainsKey(sha) || _target.ContainsSource(sha);
        if (ImportLimits.CheckDocument(b.Layers, b.Bytes, b.Pixels, counted, bytes.LongLength, pixels, TotalBytesLimit, TotalPixelsLimit) is { } doc)
        {
            b.Stop = doc;
            Fail(b, doc);
            return;
        }

        if (!b.BySha.TryGetValue(sha, out var image))
        {
            try
            {
                image = Decode(bytes, fmt, sha, pixels);
            }
            catch (OutOfMemoryException)
            {
                Fail(b, ImportLimits.Simple(ImportFailReason.ReadError));
                return;
            }
            catch (Exception)
            {
                // 形式は分かるが、壊れている・途中で切れている・デコード中に例外が起きた(E-5)
                Fail(b, ImportLimits.Simple(ImportFailReason.DecodeError));
                return;
            }
            b.BySha[sha] = image;
        }

        b.Layers++;
        if (!counted)
        {
            b.Bytes += bytes.LongLength;
            b.Pixels += pixels;
        }
        b.Added.Add(new ImportedLayer(name, image, sourcePath));
    }

    private ImportedImage Decode(byte[] bytes, ImageFormat fmt, string sha, long sourcePixels)
    {
        BeforeDecode?.Invoke();
        return DecodeSource(bytes, fmt, sha, sourcePixels, MaxTextureSide);
    }

    /// <summary>
    /// ソースのバイト列を画素にする(取り込みと .glint の読み込みが共有する)。fmt が Dib なら DibReader で、それ以外は SkiaSharp で読み、GPU の上限に収める。
    /// 読めないときは ImageDecodeException。
    /// </summary>
    internal static ImportedImage DecodeSource(byte[] bytes, ImageFormat fmt, string sha, long sourcePixels, int maxTextureSide)
    {
        int w, h;
        byte[] px;
        var animated = false;
        if (fmt == ImageFormat.Dib)
        {
            px = DibReader.Read(bytes, out w, out h) ?? throw new ImageDecodeException("DIB を読めない");
        }
        else
        {
            var d = ImageDecoder.Decode(bytes);
            (w, h, px, animated) = (d.Width, d.Height, d.Rgba, d.Animated);
        }
        px = GpuFit.Fit(px, w, h, maxTextureSide, out w, out h, out var shrunk);
        return new ImportedImage(w, h, px, fmt, bytes, sha, sourcePixels, animated, shrunk);
    }

    // ---------------- 途中の結果 ----------------

    private enum PendingKind { Empty, Project, Done }

    private sealed record Pending(PendingKind Kind, Batch? Batch, Notice? Notice, string? ProjectPath)
    {
        public static Pending Nothing() => new(PendingKind.Empty, null, null, null);

        public static Pending Project(string path) => new(PendingKind.Project, null, null, path);

        public static Pending FromBatch(Batch b) => new(PendingKind.Done, b, null, null);

        /// <summary>取り込みをせず、通知だけを出す(clip.failed・import.web_url)。</summary>
        public static Pending NoticeOnly(Notice n) => new(PendingKind.Empty, null, n, null);
    }
}
