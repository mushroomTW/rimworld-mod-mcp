using System.Reflection.Metadata;

namespace RimWorldModMcp.Indexing.Metadata;

/// <summary>從 metadata 控制代碼組出完整型別名稱。</summary>
internal static class MetadataNames
{
    /// <summary>型別定義的完整名稱，巢狀型別以 + 連接外層。</summary>
    internal static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);

        if (type.IsNested)
        {
            var declaring = reader.GetTypeDefinition(type.GetDeclaringType());
            return FullName(reader, declaring) + "+" + name;
        }

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>型別參考的完整名稱。</summary>
    internal static string FullName(MetadataReader reader, TypeReference type)
    {
        var name = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var declaring = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            return FullName(reader, declaring) + "+" + name;
        }

        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>解析 base type 或介面實作的控制代碼，回傳完整名稱。</summary>
    internal static string? Resolve(MetadataReader reader, EntityHandle handle, GenericContext context)
    {
        if (handle.IsNil)
        {
            return null;
        }

        return handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => FullName(reader, reader.GetTypeReference((TypeReferenceHandle)handle)),
            HandleKind.TypeSpecification => reader
                .GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(SignatureTypeProvider.Instance, context),
            _ => null,
        };
    }

    /// <summary>把 metadata 的 arity 標記（List`1）換成可讀的泛型參數列表。</summary>
    internal static string WithGenericParameters(string fullName, IReadOnlyList<string> parameters)
    {
        var backtick = fullName.LastIndexOf('`');
        var name = backtick >= 0 ? fullName[..backtick] : fullName;

        return parameters.Count == 0 ? name : $"{name}<{string.Join(", ", parameters)}>";
    }

    /// <summary>按巢狀分隔 `+` 切分，但角括號（泛型引數）內的 `+` 不切。</summary>
    internal static string[] SplitNestedTopLevel(string name)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (c == '+' && depth == 0)
            {
                parts.Add(name[start..i]);
                start = i + 1;
            }
        }

        parts.Add(name[start..]);
        return [.. parts];
    }

    /// <summary>
    /// 解析單一巢狀層的名稱與 arity（`N）。回傳去除 arity 標記的名稱與 arity；
    /// 沒有標記時 arity 為 0。
    /// </summary>
    internal static (string Name, int Arity) SplitArity(string segment)
    {
        var backtick = segment.LastIndexOf('`');

        if (backtick < 0)
        {
            return (segment, 0);
        }

        var digits = 0;

        while (backtick + 1 + digits < segment.Length && char.IsDigit(segment[backtick + 1 + digits]))
        {
            digits++;
        }

        if (digits == 0 || !int.TryParse(segment.AsSpan(backtick + 1, digits), out var arity))
        {
            return (segment, 0);
        }

        return (segment[..backtick], arity);
    }
}
