// アセンブリのメタデータに出る名前を集める道具(INV-3 の検査: 参照の一覧と名前の grep)。System.Reflection.Metadata で読む。
// 集めるのは AssemblyRef・TypeRef(名前空間つき)・MemberRef・ModuleRef(P/Invoke の DLL)と、P/Invoke の宣言(メソッド名と入口の名前)。
// 埋め込みリソースの中身・文字列の定数(#US)・自分の型やメソッドの名前は対象にしない(呼び出しの参照ではないため)。
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace GlintLoop.Tests.Static;

internal static class AssemblyMetadataScan
{
    /// <summary>名前の種類と名前の組。</summary>
    public readonly record struct Name(string Kind, string Value)
    {
        public override string ToString() => $"{Kind} {Value}";
    }

    /// <summary>参照しているアセンブリの名前。</summary>
    public static IReadOnlyList<string> AssemblyReferences(byte[] pe)
    {
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
    }

    /// <summary>参照と P/Invoke の名前をすべて集める。</summary>
    public static IReadOnlyList<Name> Names(byte[] pe)
    {
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var names = new List<Name>();
        foreach (var h in md.AssemblyReferences) names.Add(new("AssemblyRef", md.GetString(md.GetAssemblyReference(h).Name)));
        foreach (var h in md.TypeReferences)
        {
            var t = md.GetTypeReference(h);
            var ns = md.GetString(t.Namespace);
            names.Add(new("TypeRef", ns.Length == 0 ? md.GetString(t.Name) : ns + "." + md.GetString(t.Name)));
        }
        foreach (var h in md.MemberReferences) names.Add(new("MemberRef", md.GetString(md.GetMemberReference(h).Name)));
        for (var row = 1; row <= md.GetTableRowCount(TableIndex.ModuleRef); row++)
            names.Add(new("ModuleRef", md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name)));
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
            var import = m.GetImport();
            names.Add(new("PInvoke", md.GetString(m.Name)));
            if (!import.Name.IsNil) names.Add(new("PInvokeEntry", md.GetString(import.Name)));
            if (!import.Module.IsNil) names.Add(new("PInvokeModule", md.GetString(md.GetModuleReference(import.Module).Name)));
        }
        return names;
    }

    /// <summary>names のうち、terms のどれかを含む(大文字と小文字を区別しない)もの。</summary>
    public static IReadOnlyList<Name> Hits(IEnumerable<Name> names, IEnumerable<string> terms) =>
        names.Where(n => terms.Any(t => n.Value.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
}
