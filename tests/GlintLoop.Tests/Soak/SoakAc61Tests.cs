// AC-61(NFR-12): 新規・貼り付け 2 枚・ルック・「全体」とモーション・コピー・偽のチャットの貼り付けの 1 周を、実時間で繰り返す。--soak を付けたときだけ走る。
// 周ごとにクラッシュの呼び出し・ログの禁止の行・ポップアップを調べて早めに落とす。作業セットは別のスレッドで 1 秒ごとに読み、60 分以上の実行だけ m55 − m5 ≤ 300 を判定する(m5 は 5〜10 分の最大)。
// タイルは InvokePattern を持たない(ListBoxItem)ので、「タイルを Invoke」はフォーカスと Enter の 1 回で行う(SuccessAc52Tests と同じ)。
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using GlintLoop.App.Export;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.App.Screens;
using GlintLoop.App.Viewing;
using GlintLoop.Codec.Import;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;
using GlintLoop.Tests.Codec;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Soak;

public sealed class SoakAc61Tests
{
    /// <summary>コピーを待つ上限(AC-61)。</summary>
    private static readonly TimeSpan CopyLimit = TimeSpan.FromSeconds(120);

    /// <summary>コピー以外の 1 つの段を待つ上限(仕様に無いので仮の決め)。</summary>
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(120);

    /// <summary>段が進まないまま、これだけ経ったらプロセスを終わらせる(ウィンドウのスレッドが止まると上の上限の判定に戻ってこない)。</summary>
    private static readonly TimeSpan HangLimit = TimeSpan.FromMinutes(5);

    private const int LookTiles = 24;
    private const int MotionTiles = 28;

    /// <summary>作業セットの記録と、段が進んでいるかの見張りを行うスレッド。</summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Stopwatch _clock;
        private readonly List<(double Seconds, long Mb)> _samples = new();
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private TimeSpan _lastBeat;
        private string _step = "開始";

        public Sampler(Stopwatch clock)
        {
            _clock = clock;
            _thread = new Thread(Loop) { IsBackground = true, Name = "GlintLoop.Tests Soak" };
            _thread.Start();
        }

        /// <summary>これから行う段を記録する(見張りの起点と、止まったときの説明)。</summary>
        public void Beat(string step)
        {
            lock (_samples)
            {
                _step = step;
                _lastBeat = _clock.Elapsed;
            }
        }

        public (double Seconds, long Mb)[] Samples
        {
            get
            {
                lock (_samples) return _samples.ToArray();
            }
        }

        private void Loop()
        {
            do
            {
                // mem の定義(1,000,000 バイト単位で切り捨て)
                var mb = Environment.WorkingSet / 1_000_000;
                lock (_samples)
                {
                    _samples.Add((_clock.Elapsed.TotalSeconds, mb));
                    if (_clock.Elapsed - _lastBeat <= HangLimit) continue;
                    Console.WriteLine($"FAIL SOAK AC61: 「{_step}」のまま {HangLimit.TotalMinutes} 分進まない(経過 {_clock.Elapsed.TotalSeconds:F0} 秒、作業セット {mb} MB)");
                    Environment.Exit(1);
                }
            }
            while (!_stop.Wait(TimeSpan.FromSeconds(1)));
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            _stop.Dispose();
        }
    }

    private sealed class Cycles(SoakRig rig, string[] images, Sampler sampler)
    {
        private readonly AutomationElement _root = Uia.Root(rig.Hwnd);
        private readonly LooksModule _looks = rig.M<LooksModule>();
        private readonly MotionTabModule _motion = rig.M<MotionTabModule>();
        private readonly ScreensModule _screens = rig.M<ScreensModule>();
        private readonly CanvasModule _canvas = rig.M<CanvasModule>();
        private readonly ExportBandModule _band = rig.M<ExportBandModule>();
        private int _placed;

        private EditorSession Session => rig.Shell.Session;

        /// <summary>置き換えの確認が出た回数(コピーのあとは未渡しでないので、ふつうは 0)。</summary>
        public int ReplaceDialogs { get; private set; }

        /// <summary>30 秒の待機(FR-83)が切れて「コピー」を押し直した回数。</summary>
        public int CopyRepresses { get; private set; }

        public TimeSpan LongestCopy { get; private set; }

        private void Wait(Func<bool> condition, TimeSpan limit, string what) =>
            Check.True(UiHost.WaitUntil(condition, limit), what + $"(上限 {limit.TotalSeconds:F0} 秒)");

        private AutomationElement Named(string name, ControlType type) =>
            _root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.ControlTypeProperty, type)))
            ?? throw new InvalidOperationException($"UIA で「{name}」が見つからない");

        private void PressTile(System.Windows.Controls.ListBoxItem tile, string what) => rig.Ui(() =>
        {
            Keyboard.Focus(tile);
            Check.True(ReferenceEquals(Keyboard.FocusedElement, tile), what + ": フォーカスが当たらない");
            Check.True(UiInput.Key(tile, Key.Enter), what + ": Enter を処理しない");
        });

        /// <summary>(1) Ctrl+N。確認が出たら「保存せず置き換え」を Invoke する。最初の周はウェルカム画面なので何も起きない。</summary>
        private void NewDocument(int cycle)
        {
            sampler.Beat("Ctrl+N");
            rig.Key(Key.N, ModifierKeys.Control);
            UiHost.Idle();
            if (cycle == 0)
            {
                Check.True(!rig.Ui(() => Session.HasDocument) && !rig.Ui(() => rig.Shell.Dialogs.IsOpen), "最初の周の Ctrl+N で何かが起きた");
                return;
            }
            Wait(() => !Session.HasDocument || rig.Shell.Dialogs.Top is ReplaceConfirmView, StepLimit, "Ctrl+N で文書が閉じず、確認も出ない");
            if (rig.Ui(() => Session.HasDocument))
            {
                ReplaceDialogs++;
                Uia.Invoke(Uia.Find(_root, "保存せず置き換え"));
                Wait(() => !Session.HasDocument, StepLimit, "「保存せず置き換え」で文書が閉じない");
            }
            Wait(() => rig.Shell.Window.WelcomeHost.Visibility == Visibility.Visible, StepLimit, "ウェルカム画面に戻らない");
        }

        /// <summary>(2) FileDrop で 1 枚置いて Ctrl+V。取り込みが終わるまで待つ。</summary>
        private void Paste(string path, int layers, string what)
        {
            sampler.Beat(what);
            rig.Clipboard.Data = new MemoryImportData().WithFiles(path);
            Check.True(rig.Key(Key.V, ModifierKeys.Control), what + ": Ctrl+V が処理されない");
            Wait(() => !_screens.Imports.IsImporting && !_screens.Imports.Gate.IsBusy, StepLimit, what + ": 取り込みが終わらない");
            UiHost.Idle();
            Check.Equal(layers, rig.Ui(() => Session.Document?.Layers.Count ?? 0), what + $": レイヤーの枚数(通知 {rig.Ui(() => rig.Shell.NoticeBar.Current?.Key)})");
        }

        /// <summary>(3) ルックのタイルを 1 つ選ぶ(選ばれている 2 枚目のレイヤーに当たる)。</summary>
        private string PressLook(int cycle)
        {
            sampler.Beat("ルック");
            var tiles = rig.Ui(() => _looks.Gallery.Tiles.Where(t => t.Recipe is not null && t.User is null).ToList());
            Check.Equal(LookTiles, tiles.Count, "ルックのタイルの数");
            var tile = tiles[cycle % LookTiles];
            var name = rig.Ui(() => tile.DisplayName);
            Check.Equal(LeftPaneTab.Looks, rig.Ui(() => _looks.LeftPane.SelectedTab), "2 枚目のあとのタブ");
            PressTile(tile, "ルック " + name);
            UiHost.Idle();
            var expected = rig.Ui(() => tile.Effects.Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
            var actual = rig.Ui(() => Session.Document!.Layers[1].Stack.Where(e => e.Origin == EffectOrigin.Look).Select(e => e.Id).Order(StringComparer.Ordinal).ToArray());
            Check.SequenceEqual(expected, actual, $"2 枚目のレイヤーのルック「{name}」");
            return name;
        }

        /// <summary>(4) 「全体」の行とタブ「モーション」を Select し、モーションのタイルを 1 つ選ぶ。</summary>
        private string PressMotion(int cycle)
        {
            sampler.Beat("全体とモーション");
            Uia.Select(Named("全体", ControlType.ListItem));
            Wait(() => Session.Selected == Target.All, StepLimit, "「全体」の行が選ばれない");
            Uia.Select(Named("モーション", ControlType.TabItem));
            Wait(() => _looks.LeftPane.SelectedTab == LeftPaneTab.Motion, StepLimit, "モーションのタブにならない");
            UiHost.Idle();
            var tiles = rig.Ui(() => _motion.View.Tiles.Where(t => t.Preset is not null).ToList());
            Check.Equal(MotionTiles, tiles.Count, "モーションのタイルの数");
            var tile = tiles[cycle % MotionTiles];
            var name = rig.Ui(() => tile.DisplayName);
            PressTile(tile, "モーション " + name);
            UiHost.Idle();
            var expected = rig.Ui(() => tile.Effects.Select(e => e.Id).ToArray());
            var actual = rig.Ui(() => Session.Document!.All.Stack.Select(e => e.Id).ToArray());
            Check.True(expected.Length > 0 && expected.All(actual.Contains), $"全体のスタックにモーション「{name}」が無い: {string.Join(",", actual)}");
            return name;
        }

        private void PressCopy(int expected)
        {
            Uia.Invoke(Named("コピー", ControlType.Button));
            // ボタンのクリックは WPF が後から起こすので、押下が届くまで待つ
            Wait(() => _band.Handoff.IsCopyWaiting || _band.Handoff.Busy is not null || rig.Clipboard.PlaceCalls >= expected, StepLimit, "コピーが押されない");
        }

        /// <summary>(5) 「コピー」を Invoke して、コピーが行われるまで最大 120 秒待つ。製品の待機は 30 秒で切れる(FR-83)ので、切れたら押し直す。</summary>
        private TimeSpan Copy()
        {
            sampler.Beat("コピー");
            var expected = ++_placed;
            var watch = Stopwatch.StartNew();
            PressCopy(expected);
            while (true)
            {
                var left = CopyLimit - watch.Elapsed;
                if (left > TimeSpan.Zero) UiHost.WaitUntil(() => _band.Handoff.Busy is null && (rig.Clipboard.PlaceCalls >= expected || !_band.Handoff.IsCopyWaiting), left);
                if (rig.Ui(() => rig.Clipboard.PlaceCalls >= expected && _band.Handoff.Busy is null)) break;
                Check.True(watch.Elapsed < CopyLimit,
                    $"コピーが {CopyLimit.TotalSeconds:F0} 秒で行われない: 状態 {rig.Ui(() => _band.Auto.State)}、{rig.Ui(() => _band.View.StatusText)}、{rig.Ui(() => _band.Auto.Error?.ToString())}");
                if (!rig.Ui(() => _band.Handoff.IsCopyWaiting && _band.Handoff.Busy is null))
                {
                    CopyRepresses++;
                    PressCopy(expected);
                }
            }
            Check.Equal(expected, rig.Ui(() => rig.Clipboard.PlaceCalls), "コピーの回数");
            if (watch.Elapsed > LongestCopy) LongestCopy = watch.Elapsed;
            return watch.Elapsed;
        }

        /// <summary>(6) 偽のチャットが貼り付ける(FileDrop の次に PNG を読む。載せ方は既定の both)。</summary>
        private void ChatPaste() => rig.Ui(() =>
        {
            sampler.Beat("偽のチャット");
            var data = rig.Clipboard.Last as DataObject ?? throw new InvalidOperationException("置かれたものが DataObject でない");
            Check.True(data.ContainsFileDropList(), "FileDrop が無い");
            var file = data.GetFileDropList()[0]!;
            Check.True(File.Exists(file) && new FileInfo(file).Length > 0, "FileDrop のファイルが読めない");
            Check.True(data.GetData("PNG") is MemoryStream { Length: > 0 }, "PNG の形式が読めない");
        });

        public string Run(int cycle)
        {
            var first = images[cycle % images.Length];
            var second = images[(cycle + 1) % images.Length];
            NewDocument(cycle);
            Paste(first, 1, "1 枚目");
            Paste(second, 2, "2 枚目");
            Check.Equal(Target.OfLayer(1), rig.Ui(() => Session.Selected), "2 枚目が選ばれる");
            sampler.Beat("2 枚目の最初の表示");
            Wait(() => !_canvas.Processed.IsBusy && ReferenceEquals(_canvas.Processed.PresentedDocument, Session.Document), StepLimit, "2 枚目の最初の表示が出ない");
            var look = PressLook(cycle);
            var motion = PressMotion(cycle);
            var copy = Copy();
            ChatPaste();
            return $"{Path.GetFileName(first)}+{Path.GetFileName(second)}、ルック「{look}」、モーション「{motion}」、コピー {copy.TotalSeconds:F1} 秒";
        }
    }

    private static void CheckClean(SoakRig rig)
    {
        Check.Equal(0, rig.CrashCalls, "クラッシュの処理が呼ばれた回数");
        Check.Equal(0, rig.ProcessExit.Codes.Count, "終了の依頼の回数");
        var bad = rig.ForbiddenLines();
        Check.True(bad.Count == 0, "ログの禁止の行: " + string.Join("\n", bad.Take(5).Select(b => b.Line)));
        PopupGuard.ThrowIfOpened();
    }

    [Test(Soak = true)]
    public void AC61_連続動作で新規から貼り付けまでを繰り返してクラッシュと禁止のログが0でメモリの増加が300MB以下()
    {
        var minutes = SoakOptions.Minutes;
        using var dir = new TempDir();
        var images = SoakImages.Write(dir);
        using var rig = new SoakRig();
        var clock = Stopwatch.StartNew();
        using var sampler = new Sampler(clock);
        var cycles = new Cycles(rig, images, sampler);
        var done = 0;
        try
        {
            while (clock.Elapsed.TotalMinutes < minutes)
            {
                var what = cycles.Run(done);
                done++;
                sampler.Beat("周の検査");
                CheckClean(rig);
                Console.WriteLine($"SOAK 周 {done}({clock.Elapsed.TotalSeconds:F0} 秒): {what}、作業セット {Environment.WorkingSet / 1_000_000} MB");
            }
        }
        finally
        {
            sampler.Beat("結果の出力");
            var samples = sampler.Samples;
            Console.WriteLine($"SOAK AC61: 長さの指定 {minutes} 分、経過 {clock.Elapsed.TotalSeconds:F0} 秒、周の数 {done}、最長のコピー {cycles.LongestCopy.TotalSeconds:F1} 秒、押し直し {cycles.CopyRepresses} 回、確認の表示 {cycles.ReplaceDialogs} 回");
            Console.WriteLine($"SOAK AC61: 作業セット 記録 {samples.Length} 点、最初 {samples[0].Mb} MB、最後 {samples[^1].Mb} MB、最大 {samples.Max(s => s.Mb)} MB");
            var lines = rig.ForbiddenLines();
            Console.WriteLine($"SOAK AC61: クラッシュの呼び出し {rig.CrashCalls} 回、禁止の行 {lines.Count} 行(" + string.Join("、", SoakRig.ForbiddenEvents.Select(e => $"{e} {lines.Count(l => l.Event == e)}")) + ")");
        }

        CheckClean(rig);
        if (minutes < SoakOptions.FullMinutes)
        {
            Console.WriteLine($"SOAK AC61: {SoakOptions.FullMinutes} 分未満の実行なので m5・m55 を判定しない(合格として扱わない)");
            throw new NotJudgedException($"{minutes} 分の短い実行。メモリの判定をしていない");
        }

        var all = sampler.Samples;
        // 写真の周だけ山が高いので、m5 も m55 と同じく 5 分の窓の最大で取る(2026-11-06 の作者の判断)
        var early = all.Where(s => s.Seconds is >= 300 and <= 600).ToArray();
        Check.True(early.Length > 0, "5 分から 10 分の間の記録が無い");
        var m5 = early.Max(s => s.Mb);
        var window = all.Where(s => s.Seconds is >= 3300 and <= 3600).ToArray();
        Check.True(window.Length > 0, "55 分から 60 分の間の記録が無い");
        var m55 = window.Max(s => s.Mb);
        Console.WriteLine($"SOAK AC61: m5 {m5} MB、m55 {m55} MB、m55 − m5 = {m55 - m5} MB(上限 300)");
        Check.True(m55 - m5 <= 300, $"メモリの増加: m5 {m5} MB、m55 {m55} MB、差 {m55 - m5} MB");
    }
}
