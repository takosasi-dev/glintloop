// デバイスの作り方の差し込み口(§11 Phase 5 の前書き。AC-49 の偽のデバイス工場)。製品は DefaultDeviceFactory、テストは WARP の実体を返す偽物を渡す。
// 要求のアダプターと WARP のどちらのデバイスもここで作り、作成の失敗と起動時の自己検査の失敗(FR-94)を差し替えられるようにする。
// 消失と作り直し(FR-93)の手順は DeviceRecovery が持つ。どのメンバーも GpuDevice の持ち主になるスレッド(描画のスレッド)の上で呼ばれる。
namespace GlintLoop.Render;

public interface IDeviceFactory
{
    /// <summary>要求のアダプターでデバイスを作る。作れないときは例外を投げる。</summary>
    GpuDevice Create(GpuAdapterRequest request);

    /// <summary>作ったデバイスの起動時の自己検査(FR-94)。既定は本物の検査。</summary>
    GpuCheckResult StartupCheck(PassRenderer renderer) => GpuSelfTest.StartupCheck(renderer);

    /// <summary>描画の依頼を実行する直前に呼ばれる(製品は何もしない)。偽の工場はここで HRESULT の例外を投げ、指定した回の依頼を失敗させる(AC-49)。</summary>
    void BeforeRender(RenderPriority priority)
    {
    }
}

/// <summary>本物のデバイスを作る(製品の既定)。</summary>
public sealed class DefaultDeviceFactory : IDeviceFactory
{
    public static DefaultDeviceFactory Instance { get; } = new();

    public GpuDevice Create(GpuAdapterRequest request) => GpuDevice.Create(request);
}
