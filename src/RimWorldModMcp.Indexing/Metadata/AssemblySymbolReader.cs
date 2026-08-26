using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Metadata;

/// <summary>
/// 直接從組件的 IL metadata 讀出符號表。
///
/// <para>
/// 這取代了 Python 版「用 ilspycmd 把整個組件反編譯成上萬個 .cs 檔，再用
/// tree-sitter 或 regex 掃文字」的流程。差別不只是快：metadata 給的是權威資料，
/// 有真實的型別簽章、完整的繼承鏈與介面實作，而文字解析只能做近似。
/// </para>
/// </summary>
public sealed class AssemblySymbolReader
{
    /// <summary>讀出組件中所有公開可見的型別與成員。</summary>
    public IReadOnlyList<SymbolRecord> Read(string assemblyPath)
    {
        var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
        var symbols = new List<SymbolRecord>();

        // PrefetchEntireImage 會把整個映像讀進記憶體，讀完就不再持有檔案控制代碼。
        // 預設的 memory-map 行為會鎖住 DLL，使用者更新遊戲時會出現卡住的 handle。
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream, PEStreamOptions.PrefetchEntireImage);

        if (!peReader.HasMetadata)
        {
            return symbols;
        }

        var reader = peReader.GetMetadataReader();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);

            // 編譯器產生的型別（lambda 閉包、迭代器狀態機等）對使用者沒有意義。
            if (name.StartsWith('<') || name == "<Module>")
            {
                continue;
            }

            ReadType(reader, assemblyName, handle, type, symbols);
        }

        return symbols;
    }

    private static void ReadType(
        MetadataReader reader,
        string assemblyName,
        TypeDefinitionHandle handle,
        TypeDefinition type,
        List<SymbolRecord> symbols)
    {
        var typeParameters = GenericParameterNames(reader, type.GetGenericParameters());
        var context = new GenericContext(typeParameters, []);

        var rawFqn = MetadataNames.FullName(reader, type);
        var fqn = MetadataNames.WithGenericParameters(rawFqn, typeParameters);
        var shortName = MetadataNames.WithGenericParameters(reader.GetString(type.Name), typeParameters);

        var kind = TypeKind(reader, type);
        var baseChain = BaseChain(reader, type, context);
        var interfaces = Interfaces(reader, type, context);

        var parentFqn = type.IsNested
            ? MetadataNames.FullName(reader, reader.GetTypeDefinition(type.GetDeclaringType()))
            : Namespace(reader, type);

        symbols.Add(new SymbolRecord
        {
            Assembly = assemblyName,
            Fqn = fqn,
            ShortName = shortName,
            Kind = kind,
            ParentFqn = parentFqn,
            MetadataToken = MetadataTokens.GetToken(handle),
            Signature = TypeSignature(kind, fqn, baseChain, interfaces, type),
            BaseChain = baseChain.Count > 0 ? string.Join('|', baseChain) : null,
            Interfaces = interfaces.Count > 0 ? string.Join('|', interfaces) : null,
            Accessibility = TypeAccessibility(type.Attributes),
            IsStatic = type.Attributes.HasFlag(TypeAttributes.Abstract) && type.Attributes.HasFlag(TypeAttributes.Sealed),
        });

        // enum 的成員是編譯器產生的欄位，逐一列出沒有價值。
        if (kind == SymbolKind.Enum)
        {
            return;
        }

        ReadMethods(reader, assemblyName, type, fqn, typeParameters, symbols);
        ReadProperties(reader, assemblyName, type, fqn, context, symbols);
        ReadFields(reader, assemblyName, type, fqn, context, symbols);
    }

    private static void ReadMethods(
        MetadataReader reader,
        string assemblyName,
        TypeDefinition type,
        string typeFqn,
        ImmutableArray<string> typeParameters,
        List<SymbolRecord> symbols)
    {
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);

            // 屬性與事件的存取子會另外以 property/event 的形式出現，不重複列。
            if (name.StartsWith('<') || IsAccessor(reader, type, handle))
            {
                continue;
            }

            var methodParameters = GenericParameterNames(reader, method.GetGenericParameters());
            var context = new GenericContext(typeParameters, methodParameters);

            MethodSignature<string> signature;
            try
            {
                signature = method.DecodeSignature(SignatureTypeProvider.Instance, context);
            }
            catch (BadImageFormatException)
            {
                // 個別成員的簽章壞掉不應該讓整個組件無法索引。
                continue;
            }

            var isConstructor = name is ".ctor" or ".cctor";
            var displayName = isConstructor ? SimpleName(typeFqn) : name;
            var parameters = ParameterList(reader, method, signature);

            symbols.Add(new SymbolRecord
            {
                Assembly = assemblyName,
                Fqn = $"{typeFqn}.{MetadataNames.WithGenericParameters(displayName, methodParameters)}",
                ShortName = displayName,
                Kind = isConstructor ? SymbolKind.Constructor : SymbolKind.Method,
                ParentFqn = typeFqn,
                MetadataToken = MetadataTokens.GetToken(handle),
                Signature = isConstructor
                    ? $"{displayName}({parameters})"
                    : $"{signature.ReturnType} {MetadataNames.WithGenericParameters(name, methodParameters)}({parameters})",
                Accessibility = MethodAccessibility(method.Attributes),
                IsStatic = method.Attributes.HasFlag(MethodAttributes.Static),
            });
        }
    }

    private static void ReadProperties(
        MetadataReader reader,
        string assemblyName,
        TypeDefinition type,
        string typeFqn,
        GenericContext context,
        List<SymbolRecord> symbols)
    {
        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            var name = reader.GetString(property.Name);

            if (name.StartsWith('<'))
            {
                continue;
            }

            string returnType;
            try
            {
                returnType = property.DecodeSignature(SignatureTypeProvider.Instance, context).ReturnType;
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            var accessors = property.GetAccessors();
            MethodDefinition? getter = accessors.Getter.IsNil ? null : reader.GetMethodDefinition(accessors.Getter);
            MethodDefinition? setter = accessors.Setter.IsNil ? null : reader.GetMethodDefinition(accessors.Setter);
            var representative = getter ?? setter;

            var parts = new List<string>();
            if (getter is not null)
            {
                parts.Add("get;");
            }

            if (setter is not null)
            {
                parts.Add("set;");
            }

            symbols.Add(new SymbolRecord
            {
                Assembly = assemblyName,
                Fqn = $"{typeFqn}.{name}",
                ShortName = name,
                Kind = SymbolKind.Property,
                ParentFqn = typeFqn,
                MetadataToken = MetadataTokens.GetToken(handle),
                Signature = $"{returnType} {name} {{ {string.Join(' ', parts)} }}",
                Accessibility = representative is null ? "private" : MethodAccessibility(representative.Value.Attributes),
                IsStatic = representative?.Attributes.HasFlag(MethodAttributes.Static) ?? false,
            });
        }
    }

    private static void ReadFields(
        MetadataReader reader,
        string assemblyName,
        TypeDefinition type,
        string typeFqn,
        GenericContext context,
        List<SymbolRecord> symbols)
    {
        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            var name = reader.GetString(field.Name);

            if (name.StartsWith('<'))
            {
                continue;
            }

            string fieldType;
            try
            {
                fieldType = field.DecodeSignature(SignatureTypeProvider.Instance, context);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            symbols.Add(new SymbolRecord
            {
                Assembly = assemblyName,
                Fqn = $"{typeFqn}.{name}",
                ShortName = name,
                Kind = SymbolKind.Field,
                ParentFqn = typeFqn,
                MetadataToken = MetadataTokens.GetToken(handle),
                Signature = $"{fieldType} {name}",
                Accessibility = FieldAccessibility(field.Attributes),
                IsStatic = field.Attributes.HasFlag(FieldAttributes.Static),
            });
        }
    }

    /// <summary>
    /// 由近到遠的基底型別鏈。同組件內的基底可以一路往上走，
    /// 遇到外部組件的型別參考時記下名稱就停——跨組件解析需要載入其他 DLL，
    /// 成本與收益不成比例。
    /// </summary>
    private static List<string> BaseChain(MetadataReader reader, TypeDefinition type, GenericContext context)
    {
        var chain = new List<string>();
        var current = type;
        var guard = 0;

        while (guard++ < 64)
        {
            var baseHandle = current.BaseType;
            var name = MetadataNames.Resolve(reader, baseHandle, context);

            if (name is null)
            {
                break;
            }

            chain.Add(name);

            if (baseHandle.Kind != HandleKind.TypeDefinition)
            {
                break;
            }

            current = reader.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
        }

        return chain;
    }

    private static List<string> Interfaces(MetadataReader reader, TypeDefinition type, GenericContext context)
    {
        var names = new List<string>();

        foreach (var handle in type.GetInterfaceImplementations())
        {
            var implementation = reader.GetInterfaceImplementation(handle);
            var name = MetadataNames.Resolve(reader, implementation.Interface, context);

            if (name is not null)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static SymbolKind TypeKind(MetadataReader reader, TypeDefinition type)
    {
        if (type.Attributes.HasFlag(TypeAttributes.Interface))
        {
            return SymbolKind.Interface;
        }

        var baseName = MetadataNames.Resolve(reader, type.BaseType, GenericContext.Empty);

        return baseName switch
        {
            "System.Enum" => SymbolKind.Enum,
            "System.ValueType" => SymbolKind.Struct,
            "System.MulticastDelegate" or "System.Delegate" => SymbolKind.Delegate,
            _ => SymbolKind.Class,
        };
    }

    private static string TypeSignature(
        SymbolKind kind,
        string fqn,
        List<string> baseChain,
        List<string> interfaces,
        TypeDefinition type)
    {
        var modifiers = new List<string>();

        if (type.Attributes.HasFlag(TypeAttributes.Abstract) && type.Attributes.HasFlag(TypeAttributes.Sealed))
        {
            modifiers.Add("static");
        }
        else
        {
            if (type.Attributes.HasFlag(TypeAttributes.Abstract))
            {
                modifiers.Add("abstract");
            }

            if (type.Attributes.HasFlag(TypeAttributes.Sealed) && kind is not (SymbolKind.Enum or SymbolKind.Struct))
            {
                modifiers.Add("sealed");
            }
        }

        modifiers.Add(kind.ToString().ToLowerInvariant());

        var signature = string.Join(' ', modifiers) + " " + fqn;

        // 只把直接基底與介面放進簽章；完整的鏈另外存在 base_chain 欄位。
        var inherits = new List<string>();
        if (baseChain.Count > 0 && baseChain[0] != "System.Object" && kind == SymbolKind.Class)
        {
            inherits.Add(baseChain[0]);
        }

        inherits.AddRange(interfaces);

        return inherits.Count > 0 ? $"{signature} : {string.Join(", ", inherits)}" : signature;
    }

    private static bool IsAccessor(MetadataReader reader, TypeDefinition type, MethodDefinitionHandle handle)
    {
        foreach (var propertyHandle in type.GetProperties())
        {
            var accessors = reader.GetPropertyDefinition(propertyHandle).GetAccessors();
            if (accessors.Getter == handle || accessors.Setter == handle)
            {
                return true;
            }
        }

        foreach (var eventHandle in type.GetEvents())
        {
            var accessors = reader.GetEventDefinition(eventHandle).GetAccessors();
            if (accessors.Adder == handle || accessors.Remover == handle || accessors.Raiser == handle)
            {
                return true;
            }
        }

        return false;
    }

    private static string ParameterList(MetadataReader reader, MethodDefinition method, MethodSignature<string> signature)
    {
        var names = new List<string>();

        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);

            // SequenceNumber 0 是回傳值，不是真正的參數。
            if (parameter.SequenceNumber > 0)
            {
                names.Add(reader.GetString(parameter.Name));
            }
        }

        return string.Join(", ", signature.ParameterTypes.Select((type, index) =>
            index < names.Count && !string.IsNullOrEmpty(names[index]) ? $"{type} {names[index]}" : type));
    }

    private static ImmutableArray<string> GenericParameterNames(MetadataReader reader, GenericParameterHandleCollection handles)
    {
        if (handles.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<string>(handles.Count);

        foreach (var handle in handles)
        {
            builder.Add(reader.GetString(reader.GetGenericParameter(handle).Name));
        }

        return builder.MoveToImmutable();
    }

    private static string Namespace(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? string.Empty : ns;
    }

    private static string SimpleName(string fqn)
    {
        var lastDot = fqn.LastIndexOf('.');
        var name = lastDot >= 0 ? fqn[(lastDot + 1)..] : fqn;

        var angle = name.IndexOf('<');
        return angle >= 0 ? name[..angle] : name;
    }

    private static string TypeAccessibility(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
        TypeAttributes.NestedFamily => "protected",
        TypeAttributes.NestedFamORAssem => "protected internal",
        TypeAttributes.NestedFamANDAssem => "private protected",
        TypeAttributes.NestedPrivate => "private",
        _ => "internal",
    };

    private static string MethodAccessibility(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Family => "protected",
        MethodAttributes.FamORAssem => "protected internal",
        MethodAttributes.FamANDAssem => "private protected",
        MethodAttributes.Assembly => "internal",
        _ => "private",
    };

    private static string FieldAccessibility(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => "public",
        FieldAttributes.Family => "protected",
        FieldAttributes.FamORAssem => "protected internal",
        FieldAttributes.FamANDAssem => "private protected",
        FieldAttributes.Assembly => "internal",
        _ => "private",
    };
}
