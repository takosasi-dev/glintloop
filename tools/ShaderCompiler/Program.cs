// ビルド時に HLSL を DXBC(vs_5_0 / ps_5_0)へコンパイルして .cso を書くツール(仕様書 D-7・INV-3)。
// Render の csproj のビルド前ターゲットから呼ばれる。System32 の d3dcompiler_47.dll を Vortice.D3DCompiler 経由で使う。
// 使い方: ShaderCompiler <出力フォルダ> <入力.hlsl>...(name.vs.hlsl は vs_5_0、name.ps.hlsl は ps_5_0、入口は main。#include "x.hlsli" は入力と同じフォルダから読む)
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace GlintLoop.Tools.ShaderCompiler;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("使い方: ShaderCompiler <出力フォルダ> <入力.hlsl>...");
            return 2;
        }

        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var failed = 0;
        foreach (var input in args.Skip(1))
        {
            var fileName = Path.GetFileName(input);
            var stem = Path.GetFileNameWithoutExtension(fileName); // 例: fill.ps
            var profile = Path.GetExtension(stem) switch
            {
                ".vs" => "vs_5_0",
                ".ps" => "ps_5_0",
                _ => null,
            };
            if (profile is null)
            {
                Console.Error.WriteLine($"{input}: error SC0001: 名前は name.vs.hlsl か name.ps.hlsl にすること");
                failed++;
                continue;
            }

            var source = File.ReadAllBytes(input);
            // 決定論のため、フラグは固定。デバッグ情報は入れない。
            const ShaderFlags flags = ShaderFlags.OptimizationLevel3 | ShaderFlags.EnableStrictness | ShaderFlags.WarningsAreErrors;
            using var include = new FolderInclude(Path.GetDirectoryName(Path.GetFullPath(input))!);
            var hr = Compiler.Compile(source, null!, include, "main", fileName, profile, flags, out var blob, out var errorBlob);
            try
            {
                if (hr.Failure || blob is null)
                {
                    // d3dcompiler のメッセージは path(line,col): error ... の形なので、MSBuild がエラーとして拾える。
                    var message = errorBlob?.AsString() ?? $"HRESULT 0x{hr.Code:X8}";
                    Console.Error.WriteLine(message.Trim());
                    Console.Error.WriteLine($"{input}: error SC0002: コンパイルに失敗した({profile})");
                    failed++;
                    continue;
                }

                var output = Path.Combine(outDir, stem + ".cso");
                File.WriteAllBytes(output, blob.AsBytes());
                Console.WriteLine($"ShaderCompiler: {fileName} -> {Path.GetFileName(output)} ({profile}, {blob.BufferSize} バイト)");
            }
            finally
            {
                blob?.Dispose();
                errorBlob?.Dispose();
            }
        }

        return failed == 0 ? 0 : 1;
    }
}

/// <summary>
/// #include "名前" を、入力の .hlsl と同じフォルダのファイルから読む(ビルド時だけ。実行時には使わない。INV-3)。
/// フォルダの外(.. や絶対パス)と、&lt;名前&gt; の形のシステムのインクルードは拒む。
/// </summary>
internal sealed class FolderInclude(string folder) : CallbackBase, Include
{
    public Stream Open(IncludeType type, string fileName, Stream? parentStream)
    {
        if (type != IncludeType.Local)
            throw new InvalidOperationException($"#include <{fileName}> は使えない(\"名前\" の形にすること)");
        var full = Path.GetFullPath(Path.Combine(folder, fileName));
        if (!string.Equals(Path.GetDirectoryName(full), folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"#include \"{fileName}\" は Shaders フォルダの直下のファイルだけにすること");
        return new MemoryStream(File.ReadAllBytes(full), writable: false);
    }

    public void Close(Stream stream) => stream.Dispose();
}
