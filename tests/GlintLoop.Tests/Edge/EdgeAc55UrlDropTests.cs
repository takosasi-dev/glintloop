// AC-55 の (3): URL だけのドロップ(E-3)。import.web_url が出て、取得を試みない(INV-1)ことを、同じポートで待ち受けるループバックのリスナーで確かめる。
// このファイルだけがネットワークの型(TcpListener・IPAddress)を使う。StaticInvariantTests の INV-1 の検査の除外に入っている。
using System.Net;
using System.Net.Sockets;
using System.Windows;
using GlintLoop.Core.Notices;
using GlintLoop.Tests.Framework;
using GlintLoop.Tests.Ui;

namespace GlintLoop.Tests.Edge;

public sealed class EdgeAc55UrlDropTests
{
    [Test]
    public void AC55_3_URLだけのドロップはimport_web_urlで待ち受けへの接続が2秒待っても0回()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            // LocalEndpoint の文字列は「127.0.0.1:ポート」
            var url = "http://" + listener.LocalEndpoint + "/a.png";
            Check.True(url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), "URL の形: " + url);

            using var r = EdgeRig.Start();
            void Cases(string where)
            {
                foreach (var (name, data) in new[]
                         {
                             ("UnicodeText", new DataObject(DataFormats.UnicodeText, url)),
                             ("UniformResourceLocatorW", new DataObject("UniformResourceLocatorW", url)),
                         })
                {
                    r.Reject($"{where}: {name}", () =>
                    {
                        r.RaiseDrop(data);
                        r.WaitImports();
                    }, NoticeKeys.ImportWebUrl, NoticeLevel.Warn, []);
                }
            }
            Cases("ウェルカム画面");
            r.LoadTwoLayers();
            Cases("2 枚の文書");

            // 2 秒待っても接続が来ない(仕様の 2 秒はほかのプロセスとの間の実時間なので、ここだけは実時間で待つ)
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < until)
            {
                Check.True(!listener.Pending(), "ループバックの待ち受けに接続が来た(URL から取得しようとした)");
                Thread.Sleep(50);
            }
            Check.True(!listener.Pending(), "2 秒の終わりに接続が来ている");
        }
        finally
        {
            listener.Stop();
        }
    }
}
