// パラメータ欄(FR-27。担当 stack が作る)で、型ごとの入力部品を差し替える口。text と font の部品は担当 text が自分のモジュールで登録する。
// 欄は、登録のある型ではその部品を使い、無い型では自分の既定の部品を使う。部品は ctx の行を読み、変えるときは Session.SetEffectParam を呼ぶ。
// 登録は AppShell の組み立て中(IShellModule.Attach)に UI スレッドで行い、欄を作るのはそのあと。
using System.Windows;
using GlintLoop.App.Startup;
using GlintLoop.Core.Effects;
using GlintLoop.Core.Session;

namespace GlintLoop.App.Params;

/// <summary>1 つのパラメータの入力部品を作るときに渡す情報。Row は対象のスタックの行の番号。</summary>
public sealed record ParamEditorContext(AppShell Shell, Target Target, int Row, EffectDef Effect, ParamDef Param);

public sealed class ParamEditorRegistry
{
    private readonly Dictionary<ParamType, Func<ParamEditorContext, FrameworkElement>> _factories = [];

    /// <summary>型の部品を登録する(同じ型を 2 回登録すると後のものが勝つ)。</summary>
    public void Register(ParamType type, Func<ParamEditorContext, FrameworkElement> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[type] = factory;
    }

    /// <summary>登録のある型なら部品を作って返す。無ければ null(欄の既定の部品を使う)。</summary>
    public FrameworkElement? TryCreate(ParamEditorContext ctx) =>
        _factories.TryGetValue(ctx.Param.Type, out var f) ? f(ctx) : null;
}
