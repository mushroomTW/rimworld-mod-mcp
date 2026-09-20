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
}
