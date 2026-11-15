using Source2.Compiler.Kv3;
using Source2.Compiler.KeyValues;

namespace Source2.Compiler.Cli;

/// <summary>
/// <c>kv3-types &lt;in.*_c&gt; [BLOCK]</c>: prints a KV3 v5 block's tree with each node's wire type,
/// the detail a KV tree folds away (INT32 vs UINT32 vs the zero/one codes, typed array kinds).
/// </summary>
internal static class Kv3Types
{
    public static int Run(string[] args)
    {
        var bytes = File.ReadAllBytes(args[0]);
        var type = args.Length > 1 ? args[1].ToUpperInvariant() : "DATA";
        var block = ResourceFile.Read(bytes)[type] ?? throw new InvalidDataException($"{args[0]} has no {type} block.");
        var t = Kv3Tree.Read(block.Data);
        void Walk(Kv3Node n, string key, int depth)
        {
            var value = n.Type == Kv3Type.String ? $" \"{(n.StringId >= 0 ? t.Strings[n.StringId] : "")}\""
                : n.Items is null ? $" {n.Bits}" : n.IsTypedArray ? $" of {n.ElementType}" : "";
            Console.WriteLine($"{new string(' ', depth * 2)}{key}: {n.Type}{(n.Flag != 0 ? $"/{n.Flag}" : "")}{value}");
            for (var i = 0; n.Items is not null && i < n.Items.Count; i++)
                Walk(n.Items[i], n.KeyIds is not null ? t.Strings[n.KeyIds[i]] : $"[{i}]", depth + 1);
        }
        Walk(t.Root, "(root)", 0);
        return 0;
    }
}
