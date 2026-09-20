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
    private const string AccessPublic = "public";
    private const string AccessProtectedInternal = "protected internal";
    private const string AccessInternal = "internal";
    private const string AccessProtected = "protected";
    private const string AccessPrivateProtected = "private protected";
    private const string AccessPrivate = "private";

    /// <summary>讀出組件中所有公開可見的型別與成員。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Preserved as instance method for API compatibility and DI")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "Preserved as instance method for API compatibility and DI")]
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

        var fqn = DisplayName(reader, type);
        var shortName = MetadataNames.WithGenericParameters(reader.GetString(type.Name), OwnGenericParameters(reader, type, typeParameters));

        var kind = TypeKind(reader, type);
        var baseChain = BaseChain(reader, type, context);
        var interfaces = Interfaces(reader, type, context);

        // 巢狀型別的 parent 要跟外層型別自己的 fqn 一模一樣（含泛型參數），
        // list_symbols(parent=外層) 才列得出它。
        var parentFqn = type.IsNested
            ? DisplayName(reader, reader.GetTypeDefinition(type.GetDeclaringType()))
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
        ReadEvents(reader, assemblyName, type, fqn, context, symbols);
    }

    private static void ReadMethods(
        MetadataReader reader,
        string assemblyName,
        TypeDefinition type,
        string typeFqn,
        ImmutableArray<string> typeParameters,
        List<SymbolRecord> symbols)
    {
        // 一次建好存取子集合。逐一線性掃描的話，每個方法都要重掃整個型別的
        // 屬性與事件，Pawn、Map 這種巨型類別會是 O(方法數 × 成員數)。
        var accessors = AccessorMethods(reader, type);

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var name = reader.GetString(method.Name);

            // 屬性與事件的存取子會另外以 property/event 的形式出現，不重複列。
            if (name.StartsWith('<') || accessors.Contains(handle))
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

            var (parts, accessibility, isStatic) = AnalyzePropertyAccessors(reader, property);

            symbols.Add(new SymbolRecord
            {
                Assembly = assemblyName,
                Fqn = $"{typeFqn}.{name}",
                ShortName = name,
                Kind = SymbolKind.Property,
                ParentFqn = typeFqn,
                MetadataToken = MetadataTokens.GetToken(handle),
                Signature = $"{returnType} {name} {{ {string.Join(' ', parts)} }}",
                Accessibility = accessibility,
                IsStatic = isStatic,
            });
        }
    }

    private static (List<string> Parts, string Accessibility, bool IsStatic) AnalyzePropertyAccessors(
        MetadataReader reader,
        PropertyDefinition property)
    {
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

        // 屬性的可見度取兩個存取子中較開放的一個——只看 getter 的話，
        // private get / public set 這種組合會被誤報成 private。
        var accessibility = MostVisible(
            getter is null ? null : MethodAccessibility(getter.Value.Attributes),
            setter is null ? null : MethodAccessibility(setter.Value.Attributes)) ?? AccessPrivate;

        var isStatic = representative?.Attributes.HasFlag(MethodAttributes.Static) ?? false;
        return (parts, accessibility, isStatic);
    }

    /// <summary>
    /// 事件。存取子（add_／remove_）在 ReadMethods 被略過，事件本身若不另外列，
    /// 索引裡就完全沒有它——list_symbols 卻宣稱支援 kind=Event。
    /// </summary>
    private static void ReadEvents(
        MetadataReader reader,
        string assemblyName,
        TypeDefinition type,
        string typeFqn,
        GenericContext context,
        List<SymbolRecord> symbols)
    {
        foreach (var handle in type.GetEvents())
        {
            var eventDefinition = reader.GetEventDefinition(handle);
            var name = reader.GetString(eventDefinition.Name);

            if (name.StartsWith('<'))
            {
                continue;
            }

            var eventType = MetadataNames.Resolve(reader, eventDefinition.Type, context) ?? "?";
            var accessors = eventDefinition.GetAccessors();
            MethodDefinition? adder = accessors.Adder.IsNil ? null : reader.GetMethodDefinition(accessors.Adder);
            MethodDefinition? remover = accessors.Remover.IsNil ? null : reader.GetMethodDefinition(accessors.Remover);

            var accessibility = MostVisible(
                adder is null ? null : MethodAccessibility(adder.Value.Attributes),
                remover is null ? null : MethodAccessibility(remover.Value.Attributes)) ?? AccessPrivate;

            symbols.Add(new SymbolRecord
            {
                Assembly = assemblyName,
                Fqn = $"{typeFqn}.{name}",
                ShortName = name,
                Kind = SymbolKind.Event,
                ParentFqn = typeFqn,
                MetadataToken = MetadataTokens.GetToken(handle),
                Signature = $"event {eventType} {name}",
                Accessibility = accessibility,
                IsStatic = (adder ?? remover)?.Attributes.HasFlag(MethodAttributes.Static) ?? false,
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
    ///
    /// <para>
    /// 泛型實例化的基底（class Foo : Comp＜Bar＞）的 handle 是
    /// TypeSpecification 而不是 TypeDefinition——不解開它的話，鏈在第一層就斷，
    /// RimWorld 大量以泛型為基底的家族（Dialog_*、CompProperties_* 等）
    /// 在 find_descendants 全部查不到。鏈上存的是去除引數的定義名稱，
    /// 讓 Descendants 的精確比對不受實例化引數影響。
    /// </para>
    /// </summary>
    private static List<string> BaseChain(MetadataReader reader, TypeDefinition type, GenericContext context)
    {
        var chain = new List<string>();
        var current = type;
        var guard = 0;

        while (guard++ < 64)
        {
            var baseHandle = current.BaseType;

            if (baseHandle.IsNil)
            {
                break;
            }

            var nextHandle = baseHandle;

            if (baseHandle.Kind == HandleKind.TypeSpecification)
            {
                var definition = GenericDefinitionHandle(reader, (TypeSpecificationHandle)baseHandle);

                if (!definition.IsNil)
                {
                    nextHandle = definition;
                }
            }

            var name = MetadataNames.Resolve(reader, nextHandle, context);

            if (name is null)
            {
                break;
            }

            chain.Add(StripGenericSuffix(name));

            if (nextHandle.Kind != HandleKind.TypeDefinition)
            {
                break;
            }

            current = reader.GetTypeDefinition((TypeDefinitionHandle)nextHandle);
        }

        return chain;
    }

    /// <summary>
    /// 從 GENERICINST 簽章取出未實例化的型別定義／參考 handle。
    /// 不是泛型實例（或格式不符預期）時回傳 nil。
    /// </summary>
    private static EntityHandle GenericDefinitionHandle(MetadataReader reader, TypeSpecificationHandle handle)
    {
        try
        {
            var blob = reader.GetBlobReader(reader.GetTypeSpecification(handle).Signature);

            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            {
                return default;
            }

            // GENERICINST 之後是 CLASS/VALUETYPE 標記（讀出來是 TypeHandle 偽碼），
            // 接著才是 TypeDefOrRef coded token。
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
            {
                return default;
            }

            return blob.ReadTypeHandle();
        }
        catch (BadImageFormatException)
        {
            return default;
        }
    }

    /// <summary>去掉 arity 標記（`1）與泛型引數列表（＜...＞）。</summary>
    private static string StripGenericSuffix(string name)
    {
        var backtick = name.IndexOf('`');
        var angle = name.IndexOf('<');

        var cut = (backtick, angle) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => angle,
            (_, < 0) => backtick,
            _ => Math.Min(backtick, angle),
        };

        return cut < 0 ? name : name[..cut];
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

    private static HashSet<MethodDefinitionHandle> AccessorMethods(MetadataReader reader, TypeDefinition type)
    {
        var set = new HashSet<MethodDefinitionHandle>();

        foreach (var propertyHandle in type.GetProperties())
        {
            var accessors = reader.GetPropertyDefinition(propertyHandle).GetAccessors();

            if (!accessors.Getter.IsNil)
            {
                set.Add(accessors.Getter);
            }

            if (!accessors.Setter.IsNil)
            {
                set.Add(accessors.Setter);
            }
        }

        foreach (var eventHandle in type.GetEvents())
        {
            var accessors = reader.GetEventDefinition(eventHandle).GetAccessors();

            if (!accessors.Adder.IsNil)
            {
                set.Add(accessors.Adder);
            }

            if (!accessors.Remover.IsNil)
            {
                set.Add(accessors.Remover);
            }

            if (!accessors.Raiser.IsNil)
            {
                set.Add(accessors.Raiser);
            }
        }

        return set;
    }

    private static string ParameterList(MetadataReader reader, MethodDefinition method, MethodSignature<string> signature)
    {
        var count = signature.ParameterTypes.Length;
        var names = new string?[count];
        var refKinds = new string?[count];

        foreach (var handle in method.GetParameters())
        {
            var parameter = reader.GetParameter(handle);

            // Param 表的列是可選且可稀疏的。必須用 SequenceNumber 定位——
            // 順序 Add 的話，任何一個參數缺列就會讓後面所有名字整體錯位。
            // SequenceNumber 0 是回傳值，不是真正的參數。
            var index = parameter.SequenceNumber - 1;

            if (index < 0 || index >= count)
            {
                continue;
            }

            names[index] = reader.GetString(parameter.Name);

            // byref 參數的 out/in 資訊在參數屬性上，簽章本身只知道 byref。
            // 對 Harmony patch 作者來說 out 與 ref 的差異是關鍵——prefix 要照著寫。
            var attributes = parameter.Attributes;

            if (attributes.HasFlag(ParameterAttributes.Out) && !attributes.HasFlag(ParameterAttributes.In))
            {
                refKinds[index] = "out";
            }
            else if (attributes.HasFlag(ParameterAttributes.In) && !attributes.HasFlag(ParameterAttributes.Out))
            {
                refKinds[index] = "in";
            }
        }

        return string.Join(", ", signature.ParameterTypes.Select((type, index) =>
        {
            var display = type;

            if (display.StartsWith("ref ", StringComparison.Ordinal) && refKinds[index] is { } kind)
            {
                display = kind + display[3..];
            }

            var name = names[index];
            return string.IsNullOrEmpty(name) ? display : $"{display} {name}";
        }));
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

    /// <summary>
    /// 型別的顯示用完整名稱：Ns.Outer＜T＞+Inner＜U＞。
    ///
    /// <para>
    /// IL 裡巢狀型別會把外層的泛型參數重新宣告一次，直接拿全部參數去換掉最後一個
    /// arity 標記，Ns.Outer`1+Inner 會變成 Ns.Outer＜T＞——巢狀名稱整個消失，
    /// 還跟外層型別撞同一個 fqn。這裡逐層組：每一層只放自己新增的參數。
    /// </para>
    /// </summary>
    private static string DisplayName(MetadataReader reader, TypeDefinition type)
    {
        var parameters = GenericParameterNames(reader, type.GetGenericParameters());

        if (!type.IsNested)
        {
            return MetadataNames.WithGenericParameters(MetadataNames.FullName(reader, type), parameters);
        }

        var declaring = reader.GetTypeDefinition(type.GetDeclaringType());

        return DisplayName(reader, declaring) + "+"
            + MetadataNames.WithGenericParameters(reader.GetString(type.Name), OwnGenericParameters(reader, type, parameters));
    }

    /// <summary>去掉從外層繼承來的泛型參數，只留這一層自己宣告的。</summary>
    private static ImmutableArray<string> OwnGenericParameters(MetadataReader reader, TypeDefinition type, ImmutableArray<string> all)
    {
        if (!type.IsNested)
        {
            return all;
        }

        var inherited = reader.GetTypeDefinition(type.GetDeclaringType()).GetGenericParameters().Count;
        return inherited < all.Length ? all[inherited..] : [];
    }

    private static string Namespace(MetadataReader reader, TypeDefinition type)
    {
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? string.Empty : ns;
    }

    private static string SimpleName(string fqn)
    {
        // 巢狀型別是 Ns.Outer<T>+Inner<U>：建構子叫 Inner，不是 Outer。
        var lastSeparator = Math.Max(fqn.LastIndexOf('.'), fqn.LastIndexOf('+'));
        var name = lastSeparator >= 0 ? fqn[(lastSeparator + 1)..] : fqn;

        var angle = name.IndexOf('<');
        return angle >= 0 ? name[..angle] : name;
    }

    /// <summary>回傳兩個可見度字串中較開放的一個。</summary>
    private static string? MostVisible(string? left, string? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return VisibilityRank(left) >= VisibilityRank(right) ? left : right;
    }

    private static int VisibilityRank(string accessibility) => accessibility switch
    {
        AccessPublic => 5,
        AccessProtectedInternal => 4,
        AccessInternal => 3,
        AccessProtected => 2,
        AccessPrivateProtected => 1,
        _ => 0,
    };

    private static string TypeAccessibility(TypeAttributes attributes) => (attributes & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public or TypeAttributes.NestedPublic => AccessPublic,
        TypeAttributes.NestedFamily => AccessProtected,
        TypeAttributes.NestedFamORAssem => AccessProtectedInternal,
        TypeAttributes.NestedFamANDAssem => AccessPrivateProtected,
        TypeAttributes.NestedPrivate => AccessPrivate,
        _ => AccessInternal,
    };

    private static string MethodAccessibility(MethodAttributes attributes) => (attributes & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => AccessPublic,
        MethodAttributes.Family => AccessProtected,
        MethodAttributes.FamORAssem => AccessProtectedInternal,
        MethodAttributes.FamANDAssem => AccessPrivateProtected,
        MethodAttributes.Assembly => AccessInternal,
        _ => AccessPrivate,
    };

    private static string FieldAccessibility(FieldAttributes attributes) => (attributes & FieldAttributes.FieldAccessMask) switch
    {
        FieldAttributes.Public => AccessPublic,
        FieldAttributes.Family => AccessProtected,
        FieldAttributes.FamORAssem => AccessProtectedInternal,
        FieldAttributes.FamANDAssem => AccessPrivateProtected,
        FieldAttributes.Assembly => AccessInternal,
        _ => AccessPrivate,
    };
}
