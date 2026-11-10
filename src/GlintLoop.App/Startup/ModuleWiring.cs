// 統合: 担当どうしの部品をつなぐ(IShellModule、Order 1000。すべての担当がつながったあとに呼ばれる)。レイヤーの設定とスタックの行の選択(FR-24・FR-48・FR-50)、
// すべてリセット(FR-32)、左ペインのエフェクトとモーションのタブ(FR-26)と Ctrl+F、ウェルカム画面の空のキャンバス(FR-44)、取り込みの最初の表示の待ち合わせ(E-20)。
// 各担当のモジュールは互いを知らないので、ここで受け口どうしを結ぶ。ショートカットの登録は各担当が 1 つずつ持ち、ここでは足さない。
using GlintLoop.App.Export;
using GlintLoop.App.Layers;
using GlintLoop.App.Looks;
using GlintLoop.App.MotionTab;
using GlintLoop.App.Screens;
using GlintLoop.App.Stack;
using GlintLoop.App.Theme;
using GlintLoop.App.Viewing;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Startup;

public sealed class ModuleWiring : IShellModule
{
    public int Order => 1000;

    public void Attach(AppShell shell)
    {
        var stack = Find<StackModule>(shell);
        var layers = Find<LayersModule>(shell);
        var looks = Find<LooksModule>(shell);
        var screens = Find<ScreensModule>(shell);
        var canvas = Find<CanvasModule>(shell);

        if (stack is not null && layers is not null)
        {
            // 行を選んでいないレイヤーのときに、パラメータ欄(stack の ParamsView)へレイヤーの設定(layers の部品 1 つ)を出す
            stack.LayerSettingsFactory = _ => layers.Settings;
            // 枠を出すか(FR-50・AC-29 の (3))と設定を出すかは、スタックの行の選択で決まる
            layers.SelectedStackRow = () => stack.SelectedRow?.Row;
            stack.SelectedRowChanged += layers.StackSelectionChanged;
            // 一覧の同じ行をもう一度クリックすると、行の選択を外して設定へ戻る(AC-28 の (5))
            layers.SettingsRequested += stack.ClearRowSelection;
            // 右ペインの見出しの「すべてリセット」は 1 つだけ(layers の見出しのボタン。動作は stack)
            layers.ResetAllRequested += stack.ResetAll;
            stack.ParamsView.Refresh(SessionChange.Document | SessionChange.AllState);
            layers.StackSelectionChanged();
        }

        if (stack is not null && looks is not null)
        {
            looks.LeftPane.EffectsContent = stack.EffectsTab;
            stack.SelectEffectsTab = () => looks.SelectTab(LeftPaneTab.Effects);
        }

        if (looks is not null && Find<MotionTabModule>(shell) is { } motion)
            looks.LeftPane.MotionContent = motion.View;

        if (screens is not null && layers is not null)
            screens.EmptyCanvasRequested = () => _ = layers.ShowEmptyCanvasDialog(MotionCause.Pointer);

        // 設定の「キャッシュの削除」(FR-90・FR-105)
        if (screens is not null && Find<ExportBandModule>(shell) is { } band)
            screens.ClearCache = band.ClearCacheFromSettings;

        // 描画があるときだけ、キャンバスに載せ終えるまで取り込みの門を出ない(無いときは screens の既定の待ち方)
        if (screens is not null && canvas is not null && shell.RendererReady is not null)
            screens.Imports.WaitForDisplay = _ => canvas.WaitForDisplay();
    }

    private static T? Find<T>(AppShell shell) where T : class, IShellModule => shell.Modules.OfType<T>().FirstOrDefault();
}
