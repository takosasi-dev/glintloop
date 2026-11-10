// 描画の失敗の分類(FR-93・E-32・E-41): デバイスの消失(DXGI_ERROR_DEVICE_REMOVED・DXGI_ERROR_DEVICE_RESET)、メモリ不足、そのほかの GPU のエラーを HRESULT で分ける。
// App が SharpGen の型を知らなくてよいように、ここで判定する。ログの reason への写しは呼び手が行う。
using SharpGen.Runtime;
using Vortice.DXGI;

namespace GlintLoop.Render;

/// <summary>デバイスが失われている間に受けた依頼の失敗(書き出しの依頼)と、作り直しをあきらめたあとの依頼の失敗。</summary>
public sealed class GpuDeviceLostException() : Exception("GPU のデバイスが失われた");

public static class GpuErrors
{
    private const int EOutOfMemory = unchecked((int)0x8007000E);

    /// <summary>Task の Wait などが包んだ AggregateException をほどいて、元の例外を返す。</summary>
    public static Exception Unwrap(Exception e)
    {
        while (e is AggregateException { InnerExceptions.Count: > 0 } a) e = a.InnerExceptions[0];
        return e;
    }

    /// <summary>デバイスの消失か(FR-93)。</summary>
    public static bool IsDeviceLost(Exception e) => Unwrap(e) switch
    {
        GpuDeviceLostException => true,
        SharpGenException s => s.ResultCode.Code == ResultCode.DeviceRemoved.Code || s.ResultCode.Code == ResultCode.DeviceReset.Code,
        _ => false,
    };

    /// <summary>GPU の呼び出しが返したエラーか(export_fail の gpu_error)。</summary>
    public static bool IsGpuError(Exception e) => Unwrap(e) is SharpGenException;

    /// <summary>メモリ不足か(render_fail の oom)。GPU の E_OUTOFMEMORY と .NET の OutOfMemoryException の両方。</summary>
    public static bool IsOutOfMemory(Exception e) => Unwrap(e) switch
    {
        OutOfMemoryException => true,
        SharpGenException s => s.ResultCode.Code == EOutOfMemory,
        _ => false,
    };

    /// <summary>E-41 の描画の失敗に数えるか。消失(作り直しが描き直す)・取り消し・閉じたあとは数えない。</summary>
    public static bool IsRenderFailure(Exception e) =>
        !IsDeviceLost(e) && Unwrap(e) is not (OperationCanceledException or ObjectDisposedException);
}
