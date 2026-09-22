using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace RimWorldModMcp.Indexing.Metadata;

/// <summary>解碼簽章時可見的泛型參數名稱。</summary>
public sealed record GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters)
{
    public static readonly GenericContext Empty = new([], []);
}

/// <summary>
/// 把 IL 簽章 blob 解碼成可讀的 C# 型別文字。
///
/// <para>
/// 這是「真實簽章」的來源。Python 版只能把宣告那一行的原始文字當作 signature，
/// 因為它沒有辦法讀 metadata；多行宣告只會抓到第一行，而且完全沒有型別資訊。
/// </para>
/// </summary>
internal sealed class SignatureTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    internal static readonly SignatureTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.Void => "void",
        _ => "object",
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        => MetadataNames.FullName(reader, reader.GetTypeDefinition(handle));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        => MetadataNames.FullName(reader, reader.GetTypeReference(handle));

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => elementType + "[" + new string(',', Math.Max(shape.Rank - 1, 0)) + "]";

    public string GetByReferenceType(string elementType) => "ref " + elementType;

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    // custom modifier（modreq/modopt）對閱讀簽章沒有幫助，直接略過。
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
    {
        // 逐層巢狀處理 arity 與型別引數（F10）：genericType 如 `Ns.Outer`1+Nested`，
        // 直接對整個字串切 backtick 會把 `+Nested` 一併丟掉。
        // 每層只消耗屬於該層的引數（外層先），無 arity 的巢狀層原樣保留。
        var parts = MetadataNames.SplitNestedTopLevel(genericType);
        var rendered = new string[parts.Length];
        var argIndex = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var (name, arity) = MetadataNames.SplitArity(parts[i]);
            var take = Math.Min(arity, typeArguments.Length - argIndex);

            if (take > 0)
            {
                name += $"<{string.Join(", ", typeArguments.Skip(argIndex).Take(take))}>";
            }

            rendered[i] = name;
            argIndex += arity;
        }

        return string.Join("+", rendered);
    }

    public string GetGenericTypeParameter(GenericContext genericContext, int index)
        => index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "T" + index;

    public string GetGenericMethodParameter(GenericContext genericContext, int index)
        => index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "TMethod" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature)
        => $"delegate*<{string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType))}>";
}
