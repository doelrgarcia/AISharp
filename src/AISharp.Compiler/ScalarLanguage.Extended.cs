using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace AISharp.Compiler;

/// <summary>Opt-in front-end extensions beyond the original strict scalar slice. The default (None) is bit-for-bit the original binder.</summary>
[Flags]
public enum ScalarFeatures : long
{
    None = 0,
    /// <summary>const locals (compile-time constants, constant folding) and do/while.</summary>
    Control = 1,
    /// <summary>checked/unchecked expression and block forms; arithmetic stamped with its lexical overflow context.</summary>
    Checked = 2,
    /// <summary>long, uint, ulong, short, ushort, byte, sbyte; literal suffixes, implicit conversions and explicit (T)x casts.</summary>
    Types = 4,
    /// <summary>Functions with typed parameters and results, calls and recursion.</summary>
    Functions = 8,
    /// <summary>Bitwise operators, shifts and their compound forms, *= /= %=, hexadecimal/binary literals and digit separators.</summary>
    Bits = 16,
    /// <summary>f32/f64 (float and double are aliases of f64 per the legacy mapping), float literals and suffixes.</summary>
    Floats = 32,
    /// <summary>char: one UTF-16 code unit, character literals and escapes, explicit conversions (no implicit int to char).</summary>
    Chars = 64,
    /// <summary>string: immutable UTF-16 text, literals with escapes, ordinal ==/!=, concatenation, Length and bounds-checked indexing.</summary>
    Strings = 128,
    /// <summary>decimal: System.Decimal semantics, m/M literals, + - * / %, comparisons, integer/float conversions.</summary>
    Decimals = 256,
    /// <summary>enum declarations with integral underlying types, member constants, casts to/from the underlying type, comparisons and the C# enum operators.</summary>
    Enums = 512,
    /// <summary>struct declarations: value types with fields, an optional constructor, readonly instance methods, member visibility and readonly fields.</summary>
    Structs = 1024,
    /// <summary>switch statements and expressions, the is operator, and patterns: constant, relational, not/and/or, parenthesized, discard (switch arms) and type patterns (is only).</summary>
    Patterns = 2048,
    /// <summary>class declarations: reference types allocated in the per-run arena, null, reference equality and NullReferenceException on null access.</summary>
    Classes = 4096,
    /// <summary>Generic functions: fn f&lt;T, U&gt;(...) with where-clause constraints (class, a base class, interfaces), bound once against
    /// placeholder types for checking and monomorphized per closed type-argument list.</summary>
    Generics = 8192,
    /// <summary>N17: try/catch/finally over the built-in exception types, throw new E() and rethrow (throw;).</summary>
    Exceptions = 16384,
    /// <summary>N18: foreach over strings.</summary>
    Foreach = 32768,
    /// <summary>N18: using statements and declarations over IDisposable class resources (needs Classes and Exceptions).</summary>
    Using = 65536,
    /// <summary>N19: delegate declarations, lambdas, anonymous methods, method groups, closures and multicast delegates (needs Classes and Functions).</summary>
    Delegates = 131072,
    /// <summary>N20: field-like events and events with add/remove accessors in classes (needs Delegates).</summary>
    Events = 262144,
    /// <summary>N21: iterators (yield return / yield break) returning IEnumerable&lt;T&gt; or IEnumerator&lt;T&gt;, and foreach over IEnumerable&lt;T&gt; (needs Classes).</summary>
    Iterators = 524288,
    /// <summary>N22: top-level async functions returning Task or Task&lt;T&gt;, await, TaskCompletionSource&lt;T&gt; and completed tasks (needs Classes and Exceptions).</summary>
    Async = 1048576,
    /// <summary>N23: query expressions over IEnumerable&lt;T&gt; (from, where, let, select, into; needs Iterators and Delegates).</summary>
    Queries = 2097152,
    /// <summary>N24: orderby (ascending/descending) and join ... on ... equals in query expressions (needs Queries).</summary>
    QueryJoins = 4194304,
    /// <summary>N25: group ... by, group continuations and group joins (needs Queries; group joins also need QueryJoins).</summary>
    QueryGroups = 8388608,
    /// <summary>N26: null strings (null and default(string) convert to string) and string fields in classes, structs and records.</summary>
    NullStrings = 16777216,
    /// <summary>N27: compile-time symbol names, including unbound generic types, and string constants.</summary>
    Names = 33554432,
    /// <summary>N28: required instance fields/properties and ordered property object initializers.</summary>
    RequiredMembers = 67108864,
    /// <summary>N29: distinct native integer types bound for an explicit 32-bit or 64-bit target.</summary>
    NativeIntegers = 134217728,
    /// <summary>N30: string arrays and explicit process-argument entry parameters.</summary>
    ProcessArguments = 268435456,
    /// <summary>N31: receiver-free methods and zero-initialized per-type fields.</summary>
    StaticMembers = 536870912,
    /// <summary>N32: ordered static field initialization and explicit type constructors.</summary>
    StaticInitialization = 1073741824,
    /// <summary>Reference, output, input and readonly-reference parameter passing with nonescaping call storage.</summary>
    ByReferenceParameters = 1L << 31,
    ReferenceVariables = 1L << 32,
    ReferenceReturns = 1L << 33,
    ReferenceMembers = 1L << 34,
    Indexers = 1L << 35,
    MutableStructAccessors = 1L << 36,
    ExtendedAccessors = 1L << 37,
    InstanceInitializers = 1L << 38,
    RecordInitializers = 1L << 39,
    RecordMembers = 1L << 40,
    RecordInheritance = 1L << 41,
    RecordConstructors = 1L << 42,
    RecordDisplay = 1L << 43,
    RecordHashing = 1L << 44,
    TypeIdentity = 1L << 45,
    TypeShapes = 1L << 46,
    ReadonlyStructs = 1L << 47,
    MutableStructMethods = 1L << 48,
    Boxing = 1L << 49,
    TypedArrays = 1L << 50,
    /// <summary>Escaped source identifiers with canonical symbol identity and original source locations.</summary>
    EscapedIdentifiers = 1L << 51,
    /// <summary>Unicode scalar identifier characters, escapes and canonical format-character identity.</summary>
    UnicodeIdentifiers = 1L << 52,
    /// <summary>Compile-time namespace imports and type/namespace aliases.</summary>
    Imports = 1L << 53,
    CompilationUnits = 1L << 54,
    StaticImports = 1L << 55,
    NestedTypes = 1L << 56,
    PartialTypes = 1L << 57,
    PartialMembers = 1L << 58,
    FileLocalTypes = 1L << 59,
    NamedArguments = 1L << 60,
}

/// <summary>A struct field; <see cref="StorageType"/> is <see cref="Type"/> with enums erased and native integers mapped to the explicit target width.</summary>
public sealed record ScalarStructField(string Name, ScalarType Type, ScalarType StorageType, bool IsPublic, bool IsReadonly, SourceLocation Location)
{
    public bool IsInternal { get; init; }
    /// <summary>The index of the declaring struct or class (an inherited field keeps the index of its base class).</summary>
    public int Owner { get; init; } = -1;
    /// <summary>An auto-property stored as this field: 'init' (assignable only during initialization) and 'private set'.</summary>
    public bool IsInit { get; init; }
    public bool WritePrivate { get; init; }
    public bool IsProperty { get; init; }
    /// <summary>The synthesized backing storage of a property with accessor bodies (reached only through the contextual 'field').</summary>
    public bool Hidden { get; init; }
    /// <summary>N20: a field-like event; outside its declaring class it can only be the left operand of += or -=.</summary>
    public bool IsEvent { get; init; }
    /// <summary>The source creation contract requires this member in each object initializer.</summary>
    public bool IsRequired { get; init; }
}
/// <summary>A property with at least one accessor body: Getter/Setter are accessor function names (null for an auto accessor using Backing, the hidden backing field).</summary>
public sealed record ScalarProperty(string Name, ScalarType Type, string TypeText, bool IsPublic, bool HasGet, string? Getter, bool HasSet, string? Setter, bool SetPrivate, string? Backing, SourceLocation Location)
{
    public bool IsProtectedMember { get; init; }
    public bool IsSynthesizedRecordTypeContract { get; init; }
    public bool IsInternal { get; init; }
    public ScalarParameterMode ReferenceMode { get; init; }
    public bool IsIndexer { get; init; }
    public bool IsOverride { get; init; }
    public bool GetPrivate { get; init; }
    public bool IsAutomatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsRecordParameterOverride { get; init; }
    public string? InterfaceQualifier { get; init; }
    public IReadOnlyList<ParameterDeclaration> IndexParameters { get; init; } = [];
    public IReadOnlyList<string> SourceIndexParameterNames { get; init; } = [];
    public int Owner { get; init; } = -1;
    public bool IsStatic { get; init; }
    /// <summary>N20: an event with add and remove accessors (Setter is K.add_E, Remover is K.remove_E); it is used only as E += d and E -= d.</summary>
    public bool IsEvent { get; init; }
    public string? Remover { get; init; }
    public bool IsRequired { get; init; }
    public bool IsInit { get; init; }
}
/// <summary>A member function: instance methods receive this; static methods receive only source parameters; instance constructors return the constructed value.</summary>
public sealed record ScalarStructMethod(string Name, string QualifiedName, bool IsPublic, bool IsConstructor, FunctionDeclaration Declaration)
{
    public IReadOnlyList<string> SourceParameterNames { get; init; } = [];
    public IReadOnlyList<string> SourceTypeParameterNames { get; init; } = [];
    public bool IsInternal { get; init; }
    /// <summary>Internal constructor access is callable within the source assembly but does not prove new().</summary>
    public bool IsInternalConstructor { get; init; }
    public bool IsProtectedConstructor { get; init; }
    public string? RecordConstructorKind { get; init; }
    public bool IsSynthesizedRecordCopy { get; init; }
    public IReadOnlyList<ScalarParameter> RecordConstructorParameters { get; init; } = [];
    public string? InterfaceQualifier { get; init; }
    public string? InterfaceSlot { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadonly { get; init; }
    public ScalarParameterMode ReceiverMode { get; init; }
    public bool IsStaticConstructor { get; init; }
    public bool IsImplicitStaticConstructor { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    /// <summary>For a virtual, abstract, override or interface member: the qualified name of the member that introduced the dispatch slot.</summary>
    public string? Slot { get; init; }
    /// <summary>The index of the declaring struct, class or interface.</summary>
    public int Owner { get; init; } = -1;
    /// <summary>A property accessor function (never found by member lookup); Backing names the hidden field that 'field' denotes in it.</summary>
    public bool IsAccessor { get; init; }
    public bool IsRecordEqualityComparer { get; init; }
    public bool IsRecordCustomEquality { get; init; }
    public string? RecordEqualityApi { get; init; }
    public string? RecordDisplayApi { get; init; }
    public bool IsRecordHashApi { get; init; }
    public bool IsProtectedMember { get; init; }
    public bool IsInitAccessor { get; init; }
    public string? AccessorMember { get; init; }
    public string? Backing { get; init; }
    /// <summary>N19: A synthesized lambda/method-group member or closed generic method whose body is bound separately.</summary>
    public bool IsLifted { get; init; }
    public string? BoxedMethodTarget { get; init; }
}
/// <summary>A bound struct declaration. Its type is a ScalarType value at or above <see cref="ScalarLanguage.StructTypeBase"/>.</summary>
public sealed record ScalarStruct(string Name, ScalarType Id, IReadOnlyList<ScalarStructField> Fields, IReadOnlyList<ScalarStructMethod> Methods, SourceLocation Location, bool IsClass = false)
{
    public string? SourceNamespace { get; init; }
    public string? SourceName { get; init; }
    public string? SourceFullName { get; init; }
    public string? SourceRuntimeFullName { get; init; }
    public int? SourceOwnArity { get; init; }
    public IReadOnlyList<string> SourceTypeParameterNames { get; init; } = [];
    public bool IsNativeType { get; init; }
    public bool IsNativeRuntimeType { get; init; }
    public ScalarRuntimeGenericDefinition? RuntimeTypeDefinition { get; init; }
    public bool RuntimeTypeArgumentAllowed { get; init; } = true;
    public bool IsStatic { get; init; }
    /// <summary>A readonly value type with checked storage and receiver modes.</summary>
    public bool IsReadonly { get; init; }
    /// <summary>Logical payload type of a compiler-owned native value box.</summary>
    public ScalarType? BoxedType { get; init; }
    /// <summary>Compiler-owned storage whose public runtime identity is System.Object.</summary>
    public bool IsObjectRoot { get; init; }
    /// <summary>Compiler-owned identity bridge for the native UTF-16 string ABI.</summary>
    public bool IsStringObject { get; init; }
    /// <summary>Per-type storage, separate from the instance layout; each closed generic type owns its own list.</summary>
    public IReadOnlyList<ScalarStructField> StaticFields { get; init; } = [];
    public IReadOnlyList<ScalarStaticInitializer> StaticInitializers { get; init; } = [];
    public IReadOnlyList<ScalarInstanceInitializer> InstanceInitializers { get; init; } = [];
    /// <summary>The index of the base class, or -1. Interfaces have no fields and only abstract members.</summary>
    public int BaseIndex { get; init; } = -1;
    public IReadOnlyList<int> Interfaces { get; init; } = [];
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    public bool IsInterface { get; init; }
    public IReadOnlyList<ScalarProperty> Properties { get; init; } = [];
    /// <summary>A placeholder standing for a type parameter while a generic function is checked (never emitted). Unconstrained parameters
    /// are field-less structs; constrained ones are abstract classes deriving from the constraint types. ReferenceKnown: 'class' or a base class.</summary>
    public bool IsTypeParameter { get; init; }
    public bool ReferenceKnown { get; init; }
    /// <summary>Proofs carried by a generic type-parameter placeholder; concrete types are classified structurally.</summary>
    public bool HasValueConstraint { get; init; }
    public bool HasUnmanagedConstraint { get; init; }
    public bool HasConstructorConstraint { get; init; }
    public ScalarParameterConstraintBounds? ParameterBounds { get; init; }
    /// <summary>A generic class or struct declaration (TypeParameters) and its closed instances: Template is the index of the declaration
    /// (the declaration itself is its own instance over placeholder arguments, used only for checking), TypeArgumentList the closed arguments.</summary>
    public IReadOnlyList<string> TypeParameters { get; init; } = [];
    public IReadOnlyList<int> TypeVariances { get; init; } = [];
    public bool IsDelegate { get; init; }
    public bool IsDelegateFunction { get; init; }
    public int Template { get; init; } = -1;
    public IReadOnlyDictionary<string, ScalarType>? TypeArguments { get; init; }
    public IReadOnlyList<ScalarType> TypeArgumentList { get; init; } = [];
    /// <summary>An instance over placeholder types (the generic declaration, or a type named inside a generic body while it is checked); never emitted.</summary>
    public bool IsCheckInstance { get; init; }
    /// <summary>Compiler-owned UTF-16 string-array reference storage.</summary>
    public bool IsStringArray { get; init; }
    /// <summary>Compiler-owned single-dimensional array element identity.</summary>
    public ScalarType? ArrayElementType { get; init; }
    /// <summary>Element identity for a compiler-owned IEnumerable descriptor used by array conversions.</summary>
    public ScalarType? NativeEnumerableElement { get; init; }
    public string? NativeCollectionKind { get; init; }
    public ScalarType? NativeCollectionElement { get; init; }
    /// <summary>A record class or struct. Positional counts constructor parameters; custom members need not have positional storage.</summary>
    public bool IsRecord { get; init; }
    public int Positional { get; init; }
    public bool HasRecordPrimaryConstructor { get; init; }
    public IReadOnlyList<ParameterDeclaration> RecordParameters { get; init; } = [];
    public IReadOnlyList<ScalarType> RecordParameterTypes { get; init; } = [];
    public IReadOnlyList<ScalarType> RecordParameterStorageTypes { get; init; } = [];
    public IReadOnlyList<SourceLocation> RecordParameterLocations { get; init; } = [];
    public IReadOnlySet<string> CustomRecordMembers { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> InheritedRecordParameters { get; init; } = new HashSet<string>();
    public ScalarSource? RecordBaseArguments { get; init; }
    public SourceLocation? RecordBaseLocation { get; init; }
}

/// <summary>A static field initializer retained at its original source span until member signatures are available.</summary>
public sealed record ScalarStaticInitializer(string Field, ScalarSource Expression);
public sealed record ScalarInstanceInitializer(string Field, string TypeText, ScalarSource Expression);

/// <summary>A bound enum declaration. Its type is a ScalarType value at or above <see cref="ScalarLanguage.EnumTypeBase"/>, indexing the per-parse declaration table.</summary>
public sealed record ScalarEnum(string Name, ScalarType Id, ScalarType Underlying, IReadOnlyList<KeyValuePair<string, System.Numerics.BigInteger>> Members, SourceLocation Location)
{
    public string? SourceNamespace { get; init; }
    public string? SourceName { get; init; }
    public string? SourceFullName { get; init; }
    public string? SourceRuntimeFullName { get; init; }
    public int? SourceOwnArity { get; init; }
    public IReadOnlyList<string> SourceTypeParameterNames { get; init; } = [];
    public int Template { get; init; } = -1;
    public ScalarType? GenericOwner { get; init; }
    public IReadOnlyList<string> TypeParameters { get; init; } = [];
    public IReadOnlyList<ScalarRuntimeTypeShape> TypeArguments { get; init; } = [];
    public bool IsCheckInstance { get; init; }
    public ScalarRuntimeGenericDefinition? RuntimeTypeDefinition { get; init; }
}

public static partial class ScalarLanguage
{
    [ThreadStatic] private static ScalarFeatures activeFeatures;
    private static ScalarFeatures ActiveFeatures => activeFeatures;

    /// <summary>User-declared enum types are ScalarType values EnumTypeBase + index into the declaration table of the current parse.
    /// They exist only inside the binder: every bound function is erased to the underlying types before lowering.</summary>
    public const int EnumTypeBase = 1000;
    [ThreadStatic] private static List<ScalarEnum>? activeEnums;

    /// <summary>User-declared struct types are ScalarType values StructTypeBase + index into the struct table of the current parse; they are not erased.</summary>
    public const int StructTypeBase = 2000;
    /// <summary>The static type of the 'null' literal before it converts to a class type; it never reaches a bound function.</summary>
    public const int NullTypeId = 3500;
    /// <summary>System.Object: the static type 'object', which every class reference (and null) converts to implicitly. Value types do not convert yet (no boxing).</summary>
    public const int ObjectTypeId = 3501;
    /// <summary>Maximum UTF-16 code units in a folded string constant; prevents exponential constant expansion.</summary>
    public const int MaxConstantStringUnits = 1_048_576;
    [ThreadStatic] private static List<ScalarStruct>? activeStructs;
    private sealed class TypeParameterInfo(string name, SourceLocation location)
    {
        public string Name { get; } = name;
        public SourceLocation Location { get; } = location;
        public List<(string Text, SourceLocation Location)> Constraints { get; } = [];
        public bool HasWhere { get; set; }
        public int Placeholder { get; set; } = -1;
    }
    private sealed record GenericInfo(string Name, int NameOffset, List<TypeParameterInfo> Parameters);
    // Generic function declarations by the offset of their name; the closed instances still to bind (deduplicated by name).
    [ThreadStatic] private static Dictionary<int, GenericInfo>? activeGenerics;
    [ThreadStatic] private static List<(FunctionDeclaration Declaration, Dictionary<string, ScalarType> Arguments, string Name)>? pendingInstances;
    [ThreadStatic] private static HashSet<string>? requestedInstances;
    [ThreadStatic] private static List<(ScalarStruct Owner, ScalarStructMethod Method, Dictionary<string, ScalarType> Arguments, string Name)>? pendingMethodInstances;
    // Generic class/struct declarations: their source text (re-bound per closed instance); member signatures are copied once known.
    [ThreadStatic] private static Dictionary<int, (string Text, SourceLocation Start)>? templateDeclarations;
    [ThreadStatic] private static bool templateMembersReady;
    // The struct table handed to the back end: placeholders and check-only instances become empty unused value types (indices are kept).
    // Trailing placeholders are dropped (nothing emitted refers to them), so a program without generic types keeps its N13 table.
    private static ScalarStruct[] EmittedStructs()
    {
        var count = activeStructs!.Count;
        while (count > 0 && activeStructs[count - 1] is { IsTypeParameter: true } or { IsCheckInstance: true }) count--;
        count=Math.Max(count,RuntimeQueryRetentionCount());
        return activeStructs.Take(count).Select(CompilationMemberMetadata).Select(SaveRuntimeTypeDefinition).Select(s => s.IsTypeParameter || s.IsCheckInstance
            ? s with { Fields = [], StaticFields = [], StaticInitializers = [], InstanceInitializers = [], Methods = [], Properties = [], IsStatic = false, IsClass = false, IsInterface = false, IsAbstract = false, BaseIndex = -1, Interfaces = [], ParameterBounds = null, ArrayElementType = null, NativeEnumerableElement = null, NativeCollectionElement = null, NativeCollectionKind = null } : s with{Methods=s.Methods.Where(m=>!OmittedPartial(m.Declaration)).ToArray()}).ToArray();
    }
    private static void FillInstanceMethods(int index)
    {
        var instance = activeStructs![index];
        if (instance.Template < 0 || instance.Template == index) return;
        activeStructs[index] = instance with
        {
            Methods = activeStructs[instance.Template].Methods.Select(m =>
            {
                var map = instance.TypeArguments!.Where(p => !(ExtendedBinder.GenericOf(m.Declaration)?.Parameters.Any(t => t.Name == p.Key) ?? false)).ToDictionary();
                var name = SubstituteMemberText(m.Name, map);
                var prefix=activeStructs[instance.Template].Name+".";
                var suffix=m.QualifiedName.StartsWith(prefix,StringComparison.Ordinal)?m.QualifiedName[prefix.Length..]:m.Name;
                return m with { Name = name, QualifiedName = instance.Name + "." + SubstituteMemberText(suffix,map), Owner = index, Slot = null, InterfaceSlot = null,
                    InterfaceQualifier = m.InterfaceQualifier is null ? null : SubstituteTypeText(m.InterfaceQualifier, map),
                    Declaration = m.Declaration with { ReturnType = SubstituteSignatureTypeText(m.Declaration.ReturnType, map), Parameters = m.Declaration.Parameters.Select(p => p with { Type = SubstituteSignatureTypeText(p.Type, map) }).ToList() } };
            }).ToList()
        };
        RefreshRecordTypeContractProperty(index);
    }
    // Commas inside <...> of a function signature line become ';' (same length) so that the document parser keeps Pair<A, B> as one parameter type.
    private static void ProtectSignatureCommas(char[] text)
    {
        foreach (Match line in Regex.Matches(new string(text), @"(?m)^[ \t]*(?:(?:public|private|internal|static|async|virtual|override|abstract|sealed|readonly)[ \t]+)*fn\b[^\n]*"))
        {
            var depth = 0;
            for (var i = line.Index; i < line.Index + line.Length; i++)
            {
                if (text[i] == '-' && i + 1 < text.Length && text[i + 1] == '>') { i++; continue; }
                if (text[i] == '<') depth++;
                else if (text[i] == '>') depth = Math.Max(0, depth - 1);
                else if (text[i] == ',' && depth > 0) text[i] = ';';
            }
        }
    }

    /// <summary>Parses with the strict profile and the requested front-end extensions enabled for this call only.</summary>
    public static ParseResult ParseExtended(string source, ScalarFeatures features) => ParseExtended(source, features, out _, out _);

    /// <summary>As <see cref="ParseExtended(string, ScalarFeatures)"/>, also returning the bound enum declarations.</summary>
    public static ParseResult ParseExtended(string source, ScalarFeatures features, out IReadOnlyList<ScalarEnum> enums) => ParseExtended(source, features, out enums, out _);

    /// <summary>As <see cref="ParseExtended(string, ScalarFeatures)"/>, also returning the bound enum and struct declarations.</summary>
    public static ParseResult ParseExtended(string source, ScalarFeatures features, out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs)
        => ParseExtended(source, features, 64, out enums, out structs, out _);
    private static ParseResult ParseExtendedCore(string source, ScalarFeatures features, out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs)
    {
        ArgumentNullException.ThrowIfNull(source);
        // N25: a group clause needs a hidden IGrouping<K, E> class for its key and element types, which are known only after binding. A pass
        // that meets a new pair records it; the program is then parsed again with the classes for every recorded pair.
        var outer = groupingPairs is null;
        if (outer) { groupingPairs = []; queryElementTypes = new(StringComparer.Ordinal); taskShapeRequests = new(StringComparer.Ordinal); arrayEnumeratorRequests = new(StringComparer.Ordinal); arrayCollectionRequests = new(StringComparer.Ordinal); iteratorShapeRequests = new(StringComparer.Ordinal); }
        try
        {
            while (true)
            {
                var before = groupingPairs!.Count + queryElementTypes!.Count + taskShapeRequests!.Count + arrayEnumeratorRequests!.Count + arrayCollectionRequests!.Count + iteratorShapeRequests!.Count;
                var result = ParseExtendedPass(source, features, out enums, out structs);
                var after = groupingPairs.Count + queryElementTypes.Count + taskShapeRequests!.Count + arrayEnumeratorRequests!.Count + arrayCollectionRequests!.Count + iteratorShapeRequests!.Count;
                if (after == before || groupingPairs.Count + queryElementTypes.Count > 64) return result;
            }
        }
        finally { if (outer) { groupingPairs = null; queryElementTypes = null; taskShapeRequests = null; arrayEnumeratorRequests = null; arrayCollectionRequests = null; iteratorShapeRequests = null; } }
    }
    [ThreadStatic] private static List<(string Key, string Element)>? groupingPairs;
    [ThreadStatic] private static HashSet<string>? queryElementTypes;
    // Length prefixes keep pairs such as (A_B, C) and (A, B_C) distinct.
    private static string GroupingName(string key, string element) => $"__Grp_{key.Length}_{key}_{element.Length}_{element}";
    private static ParseResult ParseExtendedPass(string source, ScalarFeatures features, out IReadOnlyList<ScalarEnum> enums, out IReadOnlyList<ScalarStruct> structs)
    {
        var previous = activeFeatures; var previousEnums = activeEnums; var previousStructs = activeStructs;
        var previousDisplayModule=activeDisplayModule;activeDisplayModule=null;
        var previousImports=sourceImports;sourceImports=null;
        var previousTypeInitialization = activeTypeInitialization; activeTypeInitialization = UsesTypeInitialization(source, features);
        var previousGenerics = activeGenerics; var previousPending = pendingInstances; var previousRequested = requestedInstances;
        var previousMethods = pendingMethodInstances; pendingMethodInstances = [];
        var previousGenericMethods = (genericConstraintSources, genericMethodDispatches, methodGroupDispatches); genericConstraintSources = []; genericMethodDispatches = []; methodGroupDispatches = [];
        var previousGenericHierarchy = (genericBases, hierarchyBusy, slotsBusy, genericBindingDepth, validatingTypeUses, validatedTypeUseCount, pendingConstraintValidation);
        genericBases = []; hierarchyBusy = slotsBusy = pendingConstraintValidation = false; genericBindingDepth = validatedTypeUseCount = 0; validatingTypeUses = [];
        var previousStaticCalls = staticInterfaceCalls; staticInterfaceCalls = [];
        var previousStaticProperties = staticInterfaceProperties; staticInterfaceProperties = [];
        var previousInstanceProperties = constrainedInstanceProperties; constrainedInstanceProperties = [];
        var previousIndexers = constrainedIndexers; constrainedIndexers = [];
        var previousDeconstructSelections=checkedDeconstructSelections;checkedDeconstructSelections=[];
        var previousOpenPatternInputs=checkedOpenPatternInputs;checkedOpenPatternInputs=[];
        var previousRecordConstructorEdges=recordConstructorEdges;recordConstructorEdges=[];
        var previousRecordMemberReceivers=checkedRecordMemberReceivers;checkedRecordMemberReceivers=[];
        var previousRecordEqualityTargets=checkedRecordEqualityTargets;checkedRecordEqualityTargets=[];
        var previousConstrainedCalls = constrainedInterfaceCalls; var previousDefaultInterfaces = activeDefaultInterfaces; constrainedInterfaceCalls = []; activeDefaultInterfaces = false;
        var previousTypeConstraints = (typeGenerics, typeUses, typeUseKeys, typeConstraintsReady);
        typeGenerics = []; typeUses = []; typeUseKeys = new(StringComparer.Ordinal); typeConstraintsReady = false;
        var previousConstructorConstraints = activeConstructorConstraints; activeConstructorConstraints = UsesConstructorConstraints(source);
        var previousArrayFaults = activeTypedArrayFaults; activeTypedArrayFaults = TypedArrayFaultsRequested(source, (features & ScalarFeatures.TypedArrays) != 0);
        activeFeatures = features; activeEnums = []; activeStructs = []; activeGenerics = []; pendingInstances = []; requestedInstances = new(StringComparer.Ordinal);
        var previousTemplates = templateDeclarations; var previousReady = templateMembersReady; templateDeclarations = []; templateMembersReady = false;
        var previousDelegates = activeDelegates; var previousLifted = liftedFunctions; var previousSynthetic = syntheticClasses;
        var previousDelegateMetadata = (delegateHelperOffsets, delegateSourceMaps, delegateOperations); delegateHelperOffsets = []; delegateSourceMaps = []; delegateOperations = [];
        activeDelegates = new(StringComparer.Ordinal); liftedFunctions = []; syntheticClasses = new(StringComparer.Ordinal);
        var previousIterators = activeIterators; activeIterators = new(StringComparer.Ordinal);
        var previousAsync = activeAsync; activeAsync = false;
        var previousCheckTasks = checkTaskNames; checkTaskNames = new(StringComparer.Ordinal);
        var previousPropertyFields = staticPropertyFieldUses; staticPropertyFieldUses = [];
        var previousPropertyWrites = staticPropertyConstructorWrites; staticPropertyConstructorWrites = [];
        var previousInstancePropertyFields = instancePropertyFieldUses; instancePropertyFieldUses = [];
        var previousAccessorPrefixes = accessorPrefixes; accessorPrefixes = [];
        var previousRuntimeQueryProofs=runtimeQueryProofs;runtimeQueryProofs=[];var previousRuntimeQueryTypes=runtimeQueryTypes;runtimeQueryTypes=[];
        try
        {
            sourceImports=ReadSourceImports(source,features,out var importError);
            source=AddTypeIdentitySource(source,features,out var typeIdentityError);
            var boxingError=ValidateBoxingSource(source,features);
            if(new Lexer(source).Lex().FirstOrDefault(t=>t.Kind==TokenKind.Identifier&&IdentifierName(t.Text).StartsWith(NativeComparerPrefix,StringComparison.Ordinal))is {} reservedComparer)
                boxingError=new("AI3106",DiagnosticSeverity.Error,"The native comparer storage prefix is reserved.",reservedComparer.Location);
            if ((features & ScalarFeatures.Generics) != 0 && new Lexer(source).Lex().FirstOrDefault(t => t.Kind == TokenKind.Identifier && IdentifierName(t.Text).StartsWith(NativeEquatableName, StringComparison.Ordinal)) is { } reservedEquatable)
                boxingError = new("AI3106", DiagnosticSeverity.Error, "The native equality interface name is reserved.", reservedEquatable.Location);
            if ((features & ScalarFeatures.TypedArrays) != 0 && new Lexer(source).Lex().FirstOrDefault(t => t.Kind == TokenKind.Identifier && IdentifierName(t.Text).StartsWith(NativeCollectionPrefix, StringComparison.Ordinal)) is { } reservedCollection)
                boxingError = new("AI3106", DiagnosticSeverity.Error, "The array collection storage prefix is reserved.", reservedCollection.Location);
            if ((features & (ScalarFeatures.TypedArrays|ScalarFeatures.Iterators)) != 0 && new Lexer(source).Lex().FirstOrDefault(t => t.Kind == TokenKind.Identifier && (IdentifierName(t.Text).StartsWith(NativeSequenceShapePrefix, StringComparison.Ordinal)||IdentifierName(t.Text).StartsWith("__IEnumerable_"+NativeSequenceShapePrefix,StringComparison.Ordinal)||IdentifierName(t.Text).StartsWith("__IEnumerator_"+NativeSequenceShapePrefix,StringComparison.Ordinal))) is {} reservedSequence)
            boxingError=new("AI3106",DiagnosticSeverity.Error,"The sequence shape prefix is reserved.",reservedSequence.Location);
            if ((features & ScalarFeatures.TypedArrays) != 0 && new Lexer(source).Lex().FirstOrDefault(t => t.Kind == TokenKind.Identifier && IdentifierName(t.Text).StartsWith(NativeArrayEnumeratorPrefix, StringComparison.Ordinal)) is { } reservedArrayEnumerator)
                boxingError = new("AI3106", DiagnosticSeverity.Error, "The array enumerator storage prefix is reserved.", reservedArrayEnumerator.Location);
            if((features&ScalarFeatures.RecordDisplay)!=0&&RecordDisplayRequested(source))source+=DisplayBuilderSource;
            Diagnostic? declarationError = null; Diagnostic? delegateError = null; Diagnostic? arrayError = null;
            if ((features & ScalarFeatures.ProcessArguments) != 0) source = ExtractStringArrays(source, out arrayError);
            var reserved = (features & ScalarFeatures.QueryGroups) != 0 ? new Lexer(source).Lex().FirstOrDefault(t =>
                t.Kind == TokenKind.Identifier && (IdentifierName(t.Text).StartsWith("__Grp_", StringComparison.Ordinal)
                    || IdentifierName(t.Text).StartsWith("__GrpN_", StringComparison.Ordinal) || IdentifierName(t.Text).StartsWith("__grp_", StringComparison.Ordinal)
                    || IdentifierName(t.Text).StartsWith("__query_pair_", StringComparison.Ordinal) || IdentifierName(t.Text).StartsWith("__query_create_", StringComparison.Ordinal))) : null;
            // N19: each top-level delegate declaration is replaced by its runtime classes and helpers, appended after the user's text.
            source = AddEqualityComparerSource(source, features);
            source = AddEquatableSource(source, features);
            if ((features & ScalarFeatures.Delegates) != 0) source = ExtractDelegates(source, out delegateError);
            // N21: each element type named in IEnumerable<T> or IEnumerator<T> gets hidden abstract classes, appended after the user's text.
            if ((features & ScalarFeatures.TypedArrays) != 0) source += ArrayCollectionSource();
            if ((features & ScalarFeatures.Iterators) != 0) source = ExtractIterators(source, (features & ScalarFeatures.Queries) != 0);
            // N22: the hidden task classes and helpers, appended after the user's text when the program names Task or async.
            if ((features & ScalarFeatures.Async) != 0) source = ExtractAsync(source);
            // Top-level enum and struct declarations are bound first and blanked out (offsets and lines are kept) before the document parser runs.
            if ((features & ScalarFeatures.Using) != 0 && System.Text.RegularExpressions.Regex.IsMatch(source, @"\bIDisposable\b")) source += BuiltInDisposable;
            source=MaskSourceImports(source);
            if ((features & (ScalarFeatures.Enums | ScalarFeatures.Structs)) != 0) source = ExtractDeclarations(source, features, out declarationError);
            if(importError is null && declarationError is null)try{ValidateSourceImports();ValidateCompilationNestedMembers();}catch(ScalarError failure){importError=failure.Diagnostic;}
            declarationError = importError ?? typeIdentityError ?? boxingError ?? arrayError ?? delegateError ?? declarationError;
            if (declarationError is not null) declarationError = MapDelegateDiagnostic(declarationError, source);
            if (declarationError is null)
                try { ValidateDelegateSignatures(features, source); } catch (ScalarError failure) { declarationError = failure.Diagnostic; }
            for (var k = 0; k < activeStructs.Count; k++)
                if ((features & ScalarFeatures.ProcessArguments) != 0 && activeStructs[k].Name == StringArrayName) activeStructs[k] = activeStructs[k] with { IsStringArray = true };
            if (reserved is not null) declarationError = new("AI3106", DiagnosticSeverity.Error,
                $"Identifier '{reserved.Text}' is reserved for generated query storage and helpers.", reserved.Location);
            var priorDeclarationFailure=invalidExtendedDeclaration;invalidExtendedDeclaration=declarationError is not null;
            ParseResult parsed;
            try { parsed=AISharpParser.Parse(source,StrictProfile); activeDisplayModule=parsed.Document.Module; }
            finally { invalidExtendedDeclaration=priorDeclarationFailure; }
            if(declarationError is null)try{ValidateCompilationFileExposure(parsed.Document.Functions);}catch(ScalarError failure){declarationError=failure.Diagnostic;}
            enums = activeEnums.Select(SaveEnumRuntimeDefinition).ToArray(); structs = EmittedStructs();
            if (declarationError is not null)
                // An invalid declaration is reported alone; function diagnostics that would only cascade from it are dropped.
                return new(parsed.Document with { ScalarFunctions = [], ScalarValidationFailed = true },
                    [.. parsed.Diagnostics.Where(d => d.Code.StartsWith("AI1", StringComparison.Ordinal)), declarationError]);
            if (activeStructs.Count == 0 && (features&ScalarFeatures.Boxing)==0) return parsed;
            // Generic class instances are bound below, as they are requested; the back end sees the final table.
            // Struct members are bound after the document, so they can call its functions; they become functions named Struct.member.
            var members = new List<ScalarFunction>(); var diagnostics = parsed.Diagnostics.ToList();
            var bound = 0; var next = 0;
            while (true)
            {
            for (var k = next; k < activeStructs.Count; k++, next = k)
            {
                var declared = activeStructs[k];
                // Placeholders have no members; check-only instances other than a generic declaration itself are not bound.
                if (declared.IsTypeParameter || declared.IsCheckInstance && declared.Template != k) continue;
                var output = declared.IsCheckInstance ? new List<ScalarFunction>() : members;
                var methods = declared.Methods.ToList();
                // A class without a declared constructor whose base constructor must run gets an implicit public parameterless one.
                if (NeedsInit(k) && !methods.Any(m => m.IsConstructor))
                    methods.Add(new("new", declared.Name + ".new", true, true,
                        new FunctionDeclaration("new", [], TypeIdentifierSyntax(declared.Name), [], [], new FunctionBody([]) { ScalarSource = new("", declared.Location) }, [], declared.Location)) { Owner = k });
                foreach (var method in methods)
                {
                    if(OmittedPartial(method.Declaration))continue;
                    if (method.IsAbstract || method.IsLifted || method.IsRecordEqualityComparer || method.RecordEqualityApi is not null || method.RecordDisplayApi is not null || method.IsRecordHashApi) continue;
                    try
                    {
                        var init = method.IsConstructor && (NeedsInit(k)||RecordConstructorsEnabled(declared));
                        var binder = new ExtendedBinder(method.Declaration, features, parsed.Document.Functions, declared, method, init);
                        var function = binder.Bind();
                        if (ExtendedBinder.GenericOf(method.Declaration) is null) output.Add(function);
                        if (init && !declared.IsAbstract) output.Add(binder.ConstructorWrapper());
                    }
                    catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
                }
                // One dispatch function per slot, named Slot.virtual: an exact class-tag test per concrete class, calling that class's implementation.
                // The dispatch of a delegate's hidden function class is made after every lambda class exists.
                foreach (var root in declared.Methods.Where(m => !LazyRecordApi(m) && !declared.IsCheckInstance && m.Slot == m.QualifiedName && !m.IsStatic && ExtendedBinder.GenericOf(m.Declaration) is null && !IsDelegateFunctionClass(declared.Name)))
                    try { members.Add(new ExtendedBinder(root.Declaration, features, parsed.Document.Functions, declared, root, false).Dispatch(root)); }
                    catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            }
            // Closed generic instances, bound with the type arguments substituted; an instance may request further instances.
            // The generic declarations themselves were bound against placeholders only to check them; they are not emitted.
            if (pendingInstances.Count > 0 && !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var (declaration, arguments, name) = pendingInstances[0]; pendingInstances.RemoveAt(0);
                if (++bound > 256)
                {
                    diagnostics.Add(new("AI3100", DiagnosticSeverity.Error, "More than 256 generic instantiations are not supported.", declaration.Location));
                    break;
                }
                try { members.Add(new ExtendedBinder(declaration, features, parsed.Document.Functions, arguments, name).Bind()); }
                catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            }
            if (pendingMethodInstances.Count > 0 && !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                var (owner, method, arguments, name) = pendingMethodInstances[0]; pendingMethodInstances.RemoveAt(0);
                if (++bound > 256)
                {
                    diagnostics.Add(new("AI3100", DiagnosticSeverity.Error, "More than 256 generic instantiations are not supported.", method.Declaration.Location));
                    break;
                }
                try { members.Add(new ExtendedBinder(method.Declaration, features, parsed.Document.Functions, owner, method, arguments, name).Bind()); }
                catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            }
            if (next >= activeStructs.Count && pendingInstances.Count == 0 && pendingMethodInstances.Count == 0 && !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            {
                try { if (EmitBuiltinEquatableAdapters(members,features,parsed.Document.Functions) || EmitObjectApis(members,features,parsed.Document.Functions,parsed.Document.ScalarFunctions) || EmitRecordApis(members,features,parsed.Document.Functions,parsed.Document.ScalarFunctions) || EmitObjectDisplayApi(members,features,parsed.Document.Functions,parsed.Document.ScalarFunctions) || EmitObjectHashApi(members,features,parsed.Document.Functions,parsed.Document.ScalarFunctions) || EmitRecordEqualityComparers(members,features,parsed.Document.Functions,parsed.Document.ScalarFunctions) || EmitGenericMethodDispatches(members, features, parsed.Document.Functions) || EmitMethodGroupDispatches(members, features, parsed.Document.Functions) || EmitDelegateOperations(members, features, parsed.Document.Functions)) continue; }
                catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
                var dispatchTypeCount = activeStructs.Count;
                for (var owner = 0; owner < dispatchTypeCount; owner++)
                {
                    var declared = activeStructs[owner];
                    foreach (var root in declared.Methods.Where(m => !LazyRecordApi(m) && !declared.IsCheckInstance && !m.IsLifted && m.Slot == m.QualifiedName && !m.IsStatic && ExtendedBinder.GenericOf(m.Declaration) is null && !IsDelegateFunctionClass(declared.Name)).ToArray())
                        try
                        {
                            var name = root.Slot + ".virtual"; var index = members.FindIndex(f => f.Name == name);
                            var dispatch = new ExtendedBinder(root.Declaration, features, parsed.Document.Functions, declared, root, false).Dispatch(root);
                            if (index < 0) members.Add(dispatch); else members[index] = dispatch;
                        }
                        catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
                }
                if (dispatchTypeCount != activeStructs.Count || pendingInstances.Count > 0 || pendingMethodInstances.Count > 0) continue;
            }
            if (next >= activeStructs.Count && (pendingInstances.Count == 0 && pendingMethodInstances.Count == 0 || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))) break;
            }
            for (var k = 0; k < activeStructs.Count; k++)
            {
                var declared = activeStructs[k];
                if (!IsDelegateFunctionClass(declared.Name) || declared.IsCheckInstance) continue;
                foreach (var root in declared.Methods.Where(m => !LazyRecordApi(m) && m.Slot == m.QualifiedName && !m.IsStatic))
                    try { members.Add(new ExtendedBinder(root.Declaration, features, parsed.Document.Functions, declared, root, false).Dispatch(root)); }
                    catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            }
            if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                try { ValidateStaticPropertyBacking(); ValidateInstancePropertyInitializers(); ValidateRecordEqualityFamilies([..parsed.Document.ScalarFunctions,..members,..liftedFunctions!]); ValidateRecordConstructorCycles(); } catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            try { CheckLayoutCycles(); } catch (ScalarError failure) { diagnostics.Add(failure.Diagnostic); }
            enums = activeEnums.Select(SaveEnumRuntimeDefinition).ToArray();
            structs = EmittedStructs();
            var genericNames = parsed.Document.Functions.Where(f => ExtendedBinder.GenericOf(f) is not null).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            return new(parsed.Document with
            {
                ScalarFunctions = [.. parsed.Document.ScalarFunctions.Where(f => !genericNames.Contains(f.Name)), .. members, .. liftedFunctions],
                ScalarValidationFailed = parsed.Document.ScalarValidationFailed || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
            }, diagnostics);
        }
        finally
        {
            accessorPrefixes = previousAccessorPrefixes;
            runtimeQueryProofs=previousRuntimeQueryProofs;
            runtimeQueryTypes=previousRuntimeQueryTypes;
            sourceImports=previousImports;activeFeatures = previous; activeEnums = previousEnums; activeStructs = previousStructs; activeDisplayModule=previousDisplayModule; activeTypeInitialization = previousTypeInitialization;
            activeGenerics = previousGenerics; pendingInstances = previousPending; requestedInstances = previousRequested; pendingMethodInstances = previousMethods; activeConstructorConstraints = previousConstructorConstraints;
            activeTypedArrayFaults = previousArrayFaults;
            (typeGenerics, typeUses, typeUseKeys, typeConstraintsReady) = previousTypeConstraints;
            constrainedInterfaceCalls = previousConstrainedCalls; activeDefaultInterfaces = previousDefaultInterfaces;
            checkedDeconstructSelections=previousDeconstructSelections;
            checkedOpenPatternInputs=previousOpenPatternInputs;
            recordConstructorEdges=previousRecordConstructorEdges;
            checkedRecordMemberReceivers=previousRecordMemberReceivers;
            checkedRecordEqualityTargets=previousRecordEqualityTargets;
            staticInterfaceCalls = previousStaticCalls; staticInterfaceProperties = previousStaticProperties;
            constrainedInstanceProperties = previousInstanceProperties;
            constrainedIndexers = previousIndexers;
            (genericConstraintSources, genericMethodDispatches, methodGroupDispatches) = previousGenericMethods;
            (genericBases, hierarchyBusy, slotsBusy, genericBindingDepth, validatingTypeUses, validatedTypeUseCount, pendingConstraintValidation) = previousGenericHierarchy;
            templateDeclarations = previousTemplates; templateMembersReady = previousReady;
            activeDelegates = previousDelegates; liftedFunctions = previousLifted; syntheticClasses = previousSynthetic;
            (delegateHelperOffsets, delegateSourceMaps, delegateOperations) = previousDelegateMetadata;
            activeIterators = previousIterators; activeAsync = previousAsync; checkTaskNames = previousCheckTasks;
            staticPropertyFieldUses = previousPropertyFields; staticPropertyConstructorWrites = previousPropertyWrites;
            instancePropertyFieldUses = previousInstancePropertyFields;
        }
    }

    // 'abstract class' or 'sealed class' (modifiers in any order, at most two words before 'class').
    private static bool IsModifiedType(string source, int index)
    {
        for (var words = 0; words < 8; words++)
        {
            while (index < source.Length && char.IsAsciiLetter(source[index])) index++;
            while (index < source.Length && source[index] is ' ' or '\t') index++;
            if (IsWordAt(source, index, "class") || IsWordAt(source,index,"record") || IsWordAt(source,index,"struct") || IsWordAt(source,index,"interface") || IsWordAt(source,index,"enum")) return true;
            if (!(IsWordAt(source, index, "abstract") || IsWordAt(source, index, "sealed") || IsWordAt(source, index, "static") || IsWordAt(source,index,"readonly"))) return false;
        }
        return false;
    }
    private static ScalarError DeclarationError(string code, string message, SourceLocation location) => new(new(code, DiagnosticSeverity.Error, message, location));
    private static int IndexOf(ScalarType type) => (int)type - StructTypeBase;
    private static int LineEnd(string source, int index) { var end = source.IndexOf('\n', index); return end < 0 ? source.Length : end; }
    // A record has a body when a '{' follows its header on the same line or starts the next non-blank line.
    private static bool RecordHasBody(string source, int index)
    {
        var end = LineEnd(source, index);
        for (var i = index; i < end; i++)
        {
            if (source[i] == '/' && i + 1 < end && source[i + 1] == '/') break;
            if (source[i] == '{') return true;
        }
        var k = end;
        while (k < source.Length && source[k] is ' ' or '\t' or '\r' or '\n') k++;
        return k < source.Length && source[k] == '{';
    }
    // Record class bases require the inheritance feature; ordinary class/record boundaries remain invalid.
    private static void CheckRecordHierarchy()
    {
        for (var k = 0; k < activeStructs!.Count; k++)
        {
            var declared = activeStructs[k];
            if(declared.RecordBaseArguments is not null&&(!declared.IsClass||declared.BaseIndex<0))
                throw DeclarationError("AI3106","Record base constructor arguments require a direct record class base.",declared.RecordBaseLocation??declared.Location);
            if (declared.IsTypeParameter||declared.BaseIndex < 0) continue;
            if (declared.IsRecord)
            {
                if((activeFeatures&ScalarFeatures.RecordInheritance)==0)throw DeclarationError("AI3100", $"Record '{declared.Name}' cannot derive from a class yet (a record may implement interfaces).", declared.Location);
                if(!activeStructs[declared.BaseIndex].IsRecord)throw DeclarationError("AI3106","A record class can inherit only from another record class.",declared.Location);
            }
            else if (activeStructs[declared.BaseIndex].IsRecord)
                throw DeclarationError("AI3106", $"Only records may inherit from records ('{declared.Name}' derives from '{activeStructs[declared.BaseIndex].Name}').", declared.Location);
        }
    }
    // a derives from b: the same type, a base class (transitively), or an interface implemented by a or one of its base classes.
    public static bool Derives(IReadOnlyList<ScalarStruct> all, int a, int b)
        => NominalDerives(all, a, b) || VariantInterfaceDerives(all, a, b) || VariantDelegateDerives(all, a, b) || ArrayReferenceDerives(all, a, b) || ArrayEnumerableDerives(all, a, b) || ArrayCollectionDerives(all,a,b);
    private static bool NominalDerives(IReadOnlyList<ScalarStruct> all, int a, int b)
    {
        if (a >= 0 && all[a].IsTypeParameter && ParameterDependsOn(all, a, b)) return true;
        var visited = new HashSet<int>();
        for (var k = a; k >= 0 && visited.Add(k); k = all[k].BaseIndex)
            if (k == b || InterfaceClosure(all, all[k].Interfaces).Contains(b)) return true;
        return false;
    }
    // The classes that can exist at run time (not abstract, not interfaces) whose objects are b.
    public static IEnumerable<int> ConcreteOf(IReadOnlyList<ScalarStruct> all, int b) =>
        Enumerable.Range(0, all.Count).Where(k => all[k].IsClass && !all[k].IsInterface && !all[k].IsAbstract && !all[k].IsCheckInstance && Derives(all, k, b));
    // The member function named name visible on k: its own, else inherited (members are never hidden, so the most derived declaration is the implementation).
    private static ScalarStructMethod? FindMethod(int k, string name)
    {
        if (activeStructs![k].IsInterface) return InterfaceMethods([k], name).FirstOrDefault(m => !m.IsAccessor);
        for (; k >= 0; k = activeStructs![k].BaseIndex)
        {
            var found = activeStructs![k].Methods.FirstOrDefault(m => !m.IsConstructor && !m.IsAccessor && m.Name == name);
            if (found is not null) return found;
        }
        return null;
    }
    private static ScalarProperty? FindProperty(int k, string name)
    {
        var original = k;
        for (; k >= 0; k = activeStructs![k].BaseIndex)
        {
            var found = activeStructs![k].Properties.FirstOrDefault(p => !p.IsIndexer && p.InterfaceQualifier is null && p.Name == name);
            if (found is not null) return found;
            if(RecordMemberHiding(activeStructs[k])&&OwnRecordMember(activeStructs[k],name))return null;
        }
        if (activeStructs![original].IsInterface || activeStructs[original].IsTypeParameter)
        {
            var through = InterfaceClosure(activeStructs, activeStructs[original].Interfaces).SelectMany(i => activeStructs[i].Properties).Where(p => !p.IsIndexer && p.InterfaceQualifier is null && p.Name == name).DistinctBy(p => (p.Owner, p.Name)).ToArray();
            if (through.Length > 1) throw DeclarationError("AI3106", "Ambiguous inherited property.", activeStructs[original].Location);
            return through.FirstOrDefault();
        }
        return null;
    }
    // C# explicit reference conversions involving an interface: a class that is not sealed (or that implements it) to an interface and back; interface to interface.
    private static bool CastPossible(int from, int to)
    {
        var all = activeStructs!; var f = all[from]; var t = all[to];
        if (f.IsInterface && t.IsInterface) return true;
        if (t.IsInterface) return !f.IsSealed || Derives(all, from, to);
        if (f.IsInterface) return !t.IsSealed || Derives(all, to, from);
        return false;
    }
    // A class constructor is bound as K.init(this, params) (plus K.new = allocate + init) when a base or derived constructor must run on the same object.
    private static bool NeedsInit(int k)
    {
        var all = activeStructs!; var s = all[k];
        if (!s.IsClass || s.IsInterface || s.IsStatic) return false;
        if (NeedsRecordConstruction(s)) return false;
        if (s.InstanceInitializers.Count > 0) return true;
        if (s.Methods.Any(m => m.IsStaticConstructor && !m.IsImplicitStaticConstructor)) return true;
        if (s.Methods.Any(m => m.IsConstructor)) return s.BaseIndex >= 0 || all.Any(o => o.BaseIndex == k);
        return s.BaseIndex >= 0 && NeedsInit(s.BaseIndex);
    }
    private static bool SameSignature(FunctionDeclaration a, FunctionDeclaration b) =>
        (ExtendedBinder.GenericOf(a)?.Parameters.Count ?? 0) == (ExtendedBinder.GenericOf(b)?.Parameters.Count ?? 0)
        && MethodSignatureType(a, a.ReturnType) == MethodSignatureType(b, b.ReturnType) && a.Parameters.Count == b.Parameters.Count && a.Parameters.Zip(b.Parameters).All(p => MethodSignatureType(a, p.First.Type) == MethodSignatureType(b, p.Second.Type));

    // Base lists: one optional base class first, then interfaces. Inherited fields are copied in front of the class's own fields (same order as the base layout).
    private static void ResolveHierarchyCore()
    {
        var all = activeStructs!;
        for (var k = 0; k < all.Count; k++)
        {
            if (all[k].ParameterBounds is { } bounds) all[k] = all[k] with { BaseIndex = bounds.DeclaredBaseIndex, Interfaces = bounds.DeclaredInterfaces };
            var list = genericBases!.GetValueOrDefault(k, []);
            if (list.Count == 0) continue;
            var s = all[k];
            if (s.IsStatic) throw DeclarationError("AI3106", "A static class cannot have a base list.", list[0].Location);
            var baseIndex = -1; var interfaces = new List<int>();
            for (var j = 0; j < list.Count; j++)
            {
                var (name, location) = list[j];
                var t = all.FindIndex(x => x.Name == IdentifierName(name) && !x.IsTypeParameter);
                if(t<0&&sourceImports is not null&&!name.Contains('<'))
                {
                    var binder=new ExtendedBinder(activeFeatures,"",location);
                    if(s.TypeArguments is not null)binder.UseTypeArguments(new Dictionary<string,ScalarType>(s.TypeArguments),s.IsCheckInstance||s.IsTypeParameter);
                    var imported=binder.ImportAliasType(name,location);
                    if(imported is null&&(name.Contains('.')||name.Contains(':')))imported=binder.HierarchyType(name,location);
                    if(imported is {} selected){t=(int)selected-StructTypeBase;if(t<0||t>=all.Count)throw DeclarationError("AI3106","A base alias must denote a class or interface.",location);}
                }
                if (name.Contains('<'))
                {
                    var binder = new ExtendedBinder(activeFeatures, "", location);
                    if (s.TypeArguments is not null) binder.UseTypeArguments(new Dictionary<string, ScalarType>(s.TypeArguments), s.IsCheckInstance || s.IsTypeParameter);
                    t = IndexOf(binder.HierarchyType(name, location));
                }
                if (t < 0) throw DeclarationError("AI3106", $"Unknown base type '{name}'.", location);
                var b = all[t];
                if (b.TypeParameters.Count > 0 && (b.Template < 0 || b.Template == t) && !name.Contains('<')) throw DeclarationError("AI3106", $"The generic base type '{name}' requires type arguments.", location);
                if (!b.IsClass) throw DeclarationError("AI3106", $"'{name}' is a struct; a class derives only from a class or implements interfaces.", location);
                if (b.IsInterface)
                {
                    if (interfaces.Contains(t)) throw DeclarationError("AI3106", $"'{name}' is already listed in the base list of '{s.Name}'.", location);
                    interfaces.Add(t); continue;
                }
                if (s.IsInterface) throw DeclarationError("AI3106", $"Interface '{s.Name}' can inherit only from interfaces.", location);
                if (!s.IsClass && !s.IsTypeParameter) throw DeclarationError("AI3106", $"Struct '{s.Name}' can implement only interfaces.", location);
                if (j != 0) throw DeclarationError("AI3106", $"Base class '{name}' must come first in the base list, and a class has at most one base class.", location);
                if (b.IsStatic || b.IsSealed) throw DeclarationError("AI3106", $"'{s.Name}' cannot derive from sealed class '{name}'.", location);
                baseIndex = t;
            }
            all[k] = s with { BaseIndex = baseIndex, Interfaces = interfaces };
        }
        for (var k = 0; k < all.Count; k++)
        {
            var steps = 0;
            for (var b = all[k].BaseIndex; b >= 0; b = all[b].BaseIndex)
                if (b == k || ++steps > all.Count) throw DeclarationError("AI3106", $"Circular base class dependency involving '{all[k].Name}'.", all[k].Location);
        }
        ResolveParameterConstraintBounds();
        ValidateVarianceBases();
        ValidateInterfaceHierarchy();
        var done = new bool[all.Count];
        void Flatten(int k)
        {
            if (done[k]) return;
            done[k] = true;
            var s = all[k]; var own = s.Fields.Where(f => f.Owner < 0 || f.Owner == k).Select(f => f.Owner >= 0 ? f : f with { Owner = k }).ToList();
            s = s with { StaticFields = s.StaticFields.Select(f => f with { Owner = k }).ToList(), Properties = s.Properties.Select(p => p with { Owner = k }).ToList() };
            if (s.BaseIndex < 0) { all[k] = s with { Fields = own }; return; }
            Flatten(s.BaseIndex);
            var inherited = all[s.BaseIndex].Fields;
            if(s.IsRecord&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0){own=BindInheritedRecordStorage(k,s,own,inherited);s=s with{InheritedRecordParameters=all[k].InheritedRecordParameters,Properties=all[k].Properties};}
            foreach (var field in own)
                if ((inherited.Any(f => f.Name == field.Name) || !field.Hidden && FindProperty(s.BaseIndex, field.Name) is not null) && !RecordMemberHiding(s))
                    throw DeclarationError("AI3106", $"'{s.Name}.{field.Name}' hides an inherited field; hiding is not supported.", field.Location);
            foreach (var property in s.Properties)
                if (!property.IsIndexer && !property.IsOverride && !RecordMemberHiding(s) && (inherited.Any(f => f.Name == property.Name) || FindProperty(s.BaseIndex, property.Name) is not null))
                    throw DeclarationError("AI3106", $"'{s.Name}.{property.Name}' hides an inherited member; hiding is not supported.", property.Location);
            all[k] = s with { Fields = [.. inherited, .. own] };
        }
        for (var k = 0; k < all.Count; k++) Flatten(k);
    }

    // Virtual, abstract, override and sealed members; interface members; and the C# checks that every concrete class implements every abstract member it inherits.
    private static void ResolveSlotsCore()
    {
        MaterializeAutoInterfaceAccessors();
        var all = activeStructs!;
        int Depth(int k) { var d = 0; for (var b = all[k].BaseIndex; b >= 0; b = all[b].BaseIndex) d++; return d; }
        foreach (var k in Enumerable.Range(0, all.Count).OrderBy(Depth))
        {
            var s = all[k]; var methods = s.Methods.ToList();
            if((activeFeatures&ScalarFeatures.Boxing)!=0&&!s.IsInterface&&!s.IsRecord)
            {
                var signatures=new HashSet<string>(StringComparer.Ordinal);
                foreach(var method in methods.Where(m=>m.Name is "Equals" or "GetHashCode"))
                    if(!signatures.Add(method.Name+"|"+DeconstructSignature(s,method)))throw DeclarationError("AI3106","Duplicate "+method.Name+" parameter signature.",method.Declaration.Location);
            }
            for (var i = 0; i < methods.Count; i++)
            {
                var m = methods[i]; var at = m.Declaration.Location;
                if (m.IsLifted) continue;
                var modified = m.IsVirtual || m.IsOverride || m.IsAbstract || m.IsSealed;
                if (m.IsStatic && modified && !s.IsInterface) throw DeclarationError("AI3106", "A static method cannot be virtual, override, abstract or sealed.", at);
                if (m.IsStaticConstructor) continue;
                if (m.IsConstructor) { if (modified) throw DeclarationError("AI3106", "A constructor cannot be virtual, override, abstract or sealed.", at); continue; }
                if (m.InterfaceQualifier is not null) continue;
                if (s.IsInterface) { methods[i] = m with { Slot = m.IsPublic && !m.IsSealed && (!m.IsStatic || m.IsAbstract || m.IsVirtual) ? m.QualifiedName : null }; continue; }
                if(HashingEnabled&&RecordHashSignature(m)&&m.IsSealed&&!s.IsClass)throw DeclarationError("AI3106","A struct GetHashCode override cannot be sealed.",at);
                if(NativeObjectOverrideSignature(m)&&m.IsOverride&&(!m.IsPublic||m.IsInternal))throw DeclarationError("AI3106",$"An object {m.Name} override must preserve public access.",at);
                if (!s.IsClass && modified && !(NativeObjectOverrideSignature(m)&&(s.IsRecord||m.IsOverride))) throw DeclarationError("AI3106", $"Struct member '{m.QualifiedName}' cannot be virtual, override, abstract or sealed.", at);
                var inherited = s.BaseIndex >= 0 ? m.IsAccessor ? FindAccessor(s.BaseIndex, m) : (m.Name=="Deconstruct"&&(activeFeatures&ScalarFeatures.RecordMembers)!=0||s.IsRecord&&DisplayEnabled&&m.Name is "ToString" or "PrintMembers"||s.IsRecord&&HashingEnabled&&m.Name=="GetHashCode")?FindBaseDeconstruct(s.BaseIndex,s,m):m.Name=="GetHashCode"&&(activeFeatures&ScalarFeatures.Boxing)!=0?FindBaseDeconstruct(s.BaseIndex,s,m):m.Name=="Equals"&&((activeFeatures&ScalarFeatures.Boxing)!=0||s.IsRecord&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0)?FindBaseRecordEquality(s.BaseIndex,s,m):FindMethod(s.BaseIndex, m.Name) : null;
                if(NativeObjectOverrideSignature(m)&&inherited is null&&(s.IsRecord||m.IsOverride))
                { if(!s.IsRecord&&(!m.IsPublic||m.IsInternal||m.IsVirtual))throw DeclarationError("AI3106",$"An object {m.Name} override must preserve public access and cannot also be virtual.",at); methods[i]=m with{Slot=s.IsClass&&!s.IsSealed?m.QualifiedName:null};continue; }
                if (m.IsOverride)
                {
                    var property = s.Properties.FirstOrDefault(p => p.Getter == m.QualifiedName || !p.HasGet && p.Setter == m.QualifiedName);
                    if (property is not null && s.BaseIndex >= 0 && FindProperty(s.BaseIndex,property.Name) is { } baseProperty)
                    {
                        if (property.IsAutomatic && !property.HasSet && baseProperty.HasSet && !baseProperty.SetPrivate) throw DeclarationError("AI3106", "An automatic override must implement all inherited accessible accessors.",at);
                        if (baseProperty.IsRequired && !property.IsRequired) throw DeclarationError("AI3106", "An override must preserve required membership.",property.Location);
                    }
                    if (m.IsVirtual) throw DeclarationError("AI3106", $"'{m.QualifiedName}': an override member cannot be marked virtual.", at);
                    if (inherited?.Slot is null) throw DeclarationError("AI3106", $"'{m.QualifiedName}': no suitable virtual or abstract method found to override.", at);
                    if (inherited.IsSealed) throw DeclarationError("AI3106", $"'{m.QualifiedName}': cannot override '{inherited.QualifiedName}' because it is sealed.", at);
                    if (!SameSignature(m.Declaration, inherited.Declaration)) throw DeclarationError("AI3106", $"'{m.QualifiedName}': the override must have the parameter and return types of '{inherited.QualifiedName}'.", at);
                    if (m.IsPublic != inherited.IsPublic) throw DeclarationError("AI3106", $"'{m.QualifiedName}': cannot change access modifiers when overriding '{inherited.QualifiedName}'.", at);
                    CheckReferenceScopeContract(m, inherited);
                    methods[i] = m with { Slot = inherited.Slot };
                }
                else
                {
                    if (m.IsSealed) throw DeclarationError("AI3106", $"'{m.QualifiedName}' cannot be sealed because it is not an override.", at);
                    if (inherited is not null&&!RecordMemberHiding(s)&&!NativeObjectOverrideSignature(m)) throw DeclarationError("AI3106", $"'{m.QualifiedName}' hides inherited member '{inherited.QualifiedName}'; use override (member hiding is not supported).", at);
                    if (m.IsVirtual && m.IsAbstract) throw DeclarationError("AI3106", $"The abstract method '{m.QualifiedName}' cannot be marked virtual.", at);
                    if ((m.IsVirtual || m.IsAbstract) && !m.IsPublic && !m.IsProtectedMember) throw DeclarationError("AI3106", $"'{m.QualifiedName}': virtual or abstract members cannot be private.", at);
                    if (m.IsVirtual || m.IsAbstract) methods[i] = m with { Slot = m.QualifiedName };
                }
                if (m.IsAbstract && !s.IsAbstract) throw DeclarationError("AI3106", $"'{m.QualifiedName}' is abstract but it is contained in non-abstract class '{s.Name}'.", at);
            }
            all[k] = s with { Methods = methods };
        }
        ResolveExplicitInterfaceMembers();
        activeDefaultInterfaces = all.Any(type => type.IsInterface && type.Methods.Any(method => !method.IsAbstract) || type.Methods.Any(method => method.InterfaceQualifier is not null));
        for (var k = 0; k < all.Count; k++)
        {
            var s = all[k];
            if (s.IsInterface || s.IsTypeParameter) continue;
            if (!s.IsAbstract)
                for (var b = k; b >= 0; b = all[b].BaseIndex)
                    foreach (var m in all[b].Methods.Where(m => m.IsAbstract && !m.IsLifted))
                        if ((all[m.Owner].IsRecord&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0?FindVirtualImplementation(k,m):m.IsAccessor?FindAccessor(k,m):m.Name=="Deconstruct"&&(activeFeatures&ScalarFeatures.RecordMembers)!=0?FindDeconstructImplementation(k,m):FindMethod(k,m.Name)) is not { IsAbstract: false, IsStatic: false })
                            throw DeclarationError("AI3106", $"'{s.Name}' does not implement inherited abstract member '{m.QualifiedName}'.", s.Location);
            for (var b = k; b >= 0; b = all[b].BaseIndex)
                foreach (var i in InterfaceClosure(all, all[b].Interfaces))
                    foreach (var m in all[i].Methods.Where(m => !m.IsLifted && m.IsPublic && m.Slot == m.QualifiedName))
                    {
                        var resolution = ResolveInterfaceImplementation(k, m); var impl = resolution.Method;
                        if (resolution.Ambiguous) throw DeclarationError("AI3106", $"'{s.Name}' has no most specific implementation for interface member '{m.QualifiedName}'.", s.Location);
                        if (impl is null || impl.IsAbstract && !s.IsAbstract)
                            throw DeclarationError("AI3106", $"'{s.Name}' does not implement interface member '{m.QualifiedName}' (a public method with the same parameter and return types).", s.Location);
                    }
        }
    }

    private static bool IsWordAt(string source, int index, string word) =>
        string.CompareOrdinal(source, index, word, 0, word.Length) == 0
        && (index + word.Length == source.Length || !((ActiveFeatures & ScalarFeatures.UnicodeIdentifiers)!=0 ? IdentifierSpelling.IsPartAt(source,index+word.Length) : char.IsAsciiLetterOrDigit(source[index + word.Length]) || source[index + word.Length] == '_'));

    private static SourceLocation LocationAt(string source, int offset)
    {
        var line = 1; var lineStart = 0;
        for (var i = 0; i < offset; i++) if (source[i] == '\n') { line++; lineStart = i + 1; }
        return new(line, offset - lineStart + 1, offset);
    }

    // The offset just past the brace that closes the first '{' after start (string, character and comment aware), or the end of the text.
    private static int ClosingBrace(string source, int start)
    {
        var depth = 0;
        for (var i = start; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i + 1 < source.Length && source[i + 1] != '\n') i++; continue; }
            if (c is '"' or '\'') { var quote = c; i++; while (i < source.Length && source[i] != quote && source[i] != '\n') { if (source[i] == '\\') i++; i++; } continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i + 1;
        }
        return source.Length;
    }

    // Finds enum and struct declarations at brace depth 0 that start a line, binds them and replaces their text by spaces.
    // N18: System.IDisposable. It is appended after the user's text, so every user location is unchanged.
    private const string BuiltInDisposable = "\ninterface IDisposable {\n    fn Dispose() -> void\n}\n";
    // ---- N19: delegate declarations ----
    // 'delegate fn D(p: T, ...) -> R' declares the sealed class D (an immutable invocation list: h is one callable, t the rest), the abstract
    // class __D_fn of callables (Run invokes one target, Same is C# Delegate.Equals for one target: the same method and the same target) and
    // the helpers __D_invoke, __D_combine, __D_remove and __D_equals (System.Delegate.Combine, Remove and op_Equality, written in the language).
    private sealed record DelegateInfo(string Name, IReadOnlyList<(string Name, string Type)> Parameters, string Return, SourceLocation Location)
    {
        public bool EscapedName { get; init; }
        public IReadOnlyList<(string Type, SourceLocation Location)> SignatureLocations { get; init; } = [];
        public IReadOnlyList<DelegateTypeParameter> TypeParameters { get; init; } = [];
        public IReadOnlyList<ScalarType> TypeArguments { get; init; } = [];
        public string DefinitionName { get; init; } = Name;
        public string WhereClause { get; init; } = "";
        public SourceLocation WhereLocation { get; init; }
        public SourceLocation ReturnLocation { get; init; }
    }
    [ThreadStatic] private static Dictionary<string, DelegateInfo>? activeDelegates;
    [ThreadStatic] private static List<ScalarFunction>? liftedFunctions;
    [ThreadStatic] private static Dictionary<string, int>? syntheticClasses;
    private static bool IsDelegateFunctionClass(string name)
    {
        var open = name.IndexOf('<'); var head = open < 0 ? name : name[..open];
        return activeDelegates is { Count: > 0 } all && head.StartsWith("__", StringComparison.Ordinal) && head.EndsWith("_fn", StringComparison.Ordinal) && all.ContainsKey(head[2..^3])
            || IsIteratorInterface(name) || activeAsync && name == "__Cont";
    }
    // N21: the hidden abstract classes standing for IEnumerable<T> and IEnumerator<T>; their dispatch is made after every iterator class exists.
    [ThreadStatic] private static HashSet<string>? activeIterators;
    private static bool IsIteratorInterface(string name) => activeIterators is { Count: > 0 } all && all.Contains(name);
    private static string DisplayName(string name) =>
        name.StartsWith(NativeEqualityComparerName,StringComparison.Ordinal) ? "EqualityComparer"+name[NativeEqualityComparerName.Length..] :
        name.StartsWith(NativeEqualityComparerInterface,StringComparison.Ordinal) ? "IEqualityComparer"+name[NativeEqualityComparerInterface.Length..] :
        name.StartsWith(NativeEquatableName, StringComparison.Ordinal) ? "IEquatable" + name[NativeEquatableName.Length..] :
        activeAsync && name == "__Task" ? "Task" : activeAsync && name.StartsWith("__Task_", StringComparison.Ordinal) ? TaskShapeDisplay(name, 7)
        : activeAsync && name.StartsWith("__TCS_", StringComparison.Ordinal) ? TaskShapeDisplay(name, 6) :
        IsIteratorInterface(name) ? (name.StartsWith("__IEnumerable_", StringComparison.Ordinal) ? "IEnumerable<" : "IEnumerator<") + DisplayName(name[14..]) + ">"
        : name.StartsWith("__Grp_", StringComparison.Ordinal) && groupingPairs?.FirstOrDefault(p => GroupingName(p.Key, p.Element) == name) is { Key: not null } pair
            ? "IGrouping<" + pair.Key + ", " + pair.Element + ">" : CompilationDisplayName(name);
    // N22: the exception types an async method's task can carry, by capture code (1-based); SwitchExpressionException is rethrown by a hidden spelling.
    [ThreadStatic] private static bool activeConstructorConstraints;
    private static string[] AsyncFaultNames
    {
        get
        {
            var names = activeTypeInitialization ? AllAsyncFaultNames : AllAsyncFaultNames[..^1];
            if (activeConstructorConstraints) names = [.. names, "TargetInvocationException"];
            return activeTypedArrayFaults ? [.. names, "ArrayTypeMismatchException"] : names;
        }
    }
    private static readonly string[] AllAsyncFaultNames =
    [
        "Exception", "SystemException", "ArithmeticException", "DivideByZeroException", "OverflowException", "NullReferenceException", "InvalidCastException",
        "IndexOutOfRangeException", "InvalidOperationException", "ObjectDisposedException", "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "NotSupportedException", "NotImplementedException", "FormatException", "OutOfMemoryException", "InsufficientExecutionStackException", "InvalidProgramException",
        "SwitchExpressionException", "TypeInitializationException",
    ];
    private static string AsyncRuntimeName(string name) => name == "SwitchExpressionException" ? "System.Runtime.CompilerServices.SwitchExpressionException" : name == "TargetInvocationException" ? "System.Reflection.TargetInvocationException" : "System." + name;
    [ThreadStatic] private static bool activeAsync;
    private static string ExtractAsync(string source)
    {
        if (!Regex.IsMatch(source, @"\b(Task|async|await|TaskCompletionSource)\b")) return source;
        var known = new HashSet<string>(StringComparer.Ordinal) { "int", "bool", "string", "char", "decimal", "f32", "f64", "double", "float", "object", "Task" };
        foreach (Match m in Regex.Matches(source, @"(?m)^[ \t]*(?:(?:public|abstract|sealed|readonly)[ \t]+)*(?:class|struct|interface|enum|record(?:[ \t]+(?:class|struct))?)[ \t]+(" + IdentifierSpelling.Pattern + @")[ \t]*(<)?"))
            if (!m.Groups[2].Success) known.Add(m.Groups[1].Value);
        if (activeDelegates is { } delegates) known.UnionWith(delegates.Keys);
        var elements = new List<string>();
        foreach (Match m in Regex.Matches(source, @"\b(?:Task|TaskCompletionSource)[ \t]*<[ \t]*(" + IdentifierSpelling.Pattern + @")[ \t]*>"))
        {
            var t = m.Groups[1].Value;
            if ((known.Contains(t) || ScalarConstants.FromSpelling(t) is not null) && !t.StartsWith(TaskShapePrefix, StringComparison.Ordinal) && !elements.Contains(t)) elements.Add(t);
        }
        activeAsync = true;
        static string F(string signature, string body) => $"fn {signature}\n    ensures true\n{{\n{body}\n}}\n";
        static string M(string signature, string body) => $"    public fn {signature}\n        ensures true\n    {{\n{body}\n    }}\n";
        var text = new StringBuilder(source);
        text.Append("\nclass __Task {\n    public status: int\n    public code: int\n    public head: __ContNode\n    public tail: __ContNode\n}\n");
        text.Append("abstract class __Cont {\n    public abstract fn Run() -> void\n}\n");
        text.Append("sealed class __ContNode {\n    public c: __Cont\n    public next: __ContNode\n}\n");
        text.Append(F("__task_new() -> __Task", "    return new __Task()"));
        text.Append(F("__task_status(t: __Task) -> int", "    return t.status"));
        text.Append(F("__task_pending(t: __Task) -> bool", "    return t.status == 0"));
        text.Append(F("__task_on(t: __Task, c: __Cont) -> void", "    let n = new __ContNode()\n    n.c = c\n    if (t.tail == null) {\n        t.head = n\n    } else {\n        t.tail.next = n\n    }\n    t.tail = n"));
        text.Append(F("__task_run(t: __Task) -> void", "    var n = t.head\n    t.head = null\n    t.tail = null\n    while (n != null) {\n        n.c.Run()\n        n = n.next\n    }"));
        text.Append(F("__task_done(t: __Task) -> void", "    t.status = 1\n    __task_run(t)"));
        text.Append(F("__task_fail(t: __Task, code: int) -> void", "    t.status = 2\n    t.code = code\n    __task_run(t)"));
        text.Append(F("__task_completed() -> __Task", "    let t = new __Task()\n    t.status = 1\n    return t"));
        var throws = new StringBuilder();
        for (var i = 0; i < AsyncFaultNames.Length; i++)
            throws.Append($"    if (code == {i + 1}) {{\n        throw new {(AsyncFaultNames[i] == "SwitchExpressionException" ? "__SwitchExpressionException" : AsyncFaultNames[i])}()\n    }}\n");
        throws.Append("    throw new Exception()");
        text.Append(F("__task_throw(code: int) -> void", throws.ToString()));
        // A pending task here is a synchronous wait the native program cannot block on: it faults with InvalidOperationException.
        text.Append(F("__task_check(t: __Task) -> void", "    if (t.status == 0) {\n        throw new InvalidOperationException()\n    }\n    if (t.status == 2) {\n        __task_throw(t.code)\n    }"));
        foreach (var key in taskShapeRequests!.Keys) if (!elements.Contains(key)) elements.Add(key);
        foreach (var t in elements)
        {
            var valueType = taskShapeRequests.TryGetValue(t, out var requested) ? requested.Syntax : t;
            text.Append($"class __Task_{t} : __Task {{\n    public result: {valueType}\n}}\n");
            text.Append($"class __TCS_{t} {{\n    public Task: __Task_{t}\n");
            text.Append(M($"new() -> __TCS_{t}", $"        this.Task = new __Task_{t}()"));
            text.Append(M($"SetResult(v: {valueType}) -> void", $"        if (!__task_try_{t}(Task, v)) {{\n            throw new InvalidOperationException()\n        }}"));
            text.Append(M($"TrySetResult(v: {valueType}) -> bool", $"        return __task_try_{t}(Task, v)"));
            text.Append("}\n");
            text.Append(F($"__task_new_{t}() -> __Task_{t}", $"    return new __Task_{t}()"));
            text.Append(F($"__task_try_{t}(t: __Task_{t}, v: {valueType}) -> bool", "    if (t.status != 0) {\n        return false\n    }\n    t.result = v\n    __task_done(t)\n    return true"));
            text.Append(F($"__task_finish_{t}(t: __Task_{t}, v: {valueType}) -> void", "    t.result = v\n    __task_done(t)"));
            text.Append(F($"__task_get_{t}(t: __Task_{t}) -> {valueType}", "    __task_check(t)\n    return t.result"));
            text.Append(F($"__task_from_{t}(v: {valueType}) -> __Task_{t}", $"    let t = new __Task_{t}()\n    t.result = v\n    t.status = 1\n    return t"));
        }
        return text.ToString();
    }
    private static string ExtractIterators(string source, bool queries = false)
    {
        var known = new HashSet<string>(StringComparer.Ordinal) { "int", "bool", "string", "char", "decimal", "f32", "f64", "double", "float", "object" };
        foreach (Match m in Regex.Matches(source, @"(?m)^[ \t]*(?:(?:public|abstract|sealed|readonly)[ \t]+)*(?:class|struct|interface|enum|record(?:[ \t]+(?:class|struct))?)[ \t]+(" + IdentifierSpelling.Pattern + @")[ \t]*(<)?"))
            if (!m.Groups[2].Success) known.Add(m.Groups[1].Value);
        if (activeDelegates is { } delegates) known.UnionWith(delegates.Keys);
        var elements = new List<string>();
        var identifierOffsets=new Lexer(source).Lex().Where(t=>t.Kind==TokenKind.Identifier).Select(t=>t.Location.Offset).ToHashSet();
        foreach (Match m in Regex.Matches(source, @"\bIEnumera(?:ble|tor)[ \t]*<[ \t]*(" + IdentifierSpelling.Pattern + @")[ \t]*>"))
        {
            if(!identifierOffsets.Contains(m.Index))continue;
            var t = m.Groups[1].Value;
            if ((known.Contains(t) || ScalarConstants.FromSpelling(t) is not null) && !elements.Contains(t)) elements.Add(t);
        }
        // N23: a query's result element type is inferred, so a program with a query expression gets IEnumerable<T> for int, bool, string and
        // every declared non-generic type.
        if (queries && Regex.IsMatch(source, @"\bfrom[ \t]+" + IdentifierSpelling.Pattern + @"[ \t]+in\b"))
        {
            foreach (var t in new[] { "int", "bool", "string" }) if (!elements.Contains(t)) elements.Add(t);
            foreach (Match m in Regex.Matches(source, @"(?m)^[ \t]*(?:(?:public|abstract|sealed|readonly)[ \t]+)*(?:class|struct|interface|enum|record(?:[ \t]+(?:class|struct))?)[ \t]+(" + IdentifierSpelling.Pattern + @")[ \t]*(<)?"))
                if (!m.Groups[2].Success && !elements.Contains(m.Groups[1].Value)) elements.Add(m.Groups[1].Value);
        }
        // N25: the grouping classes recorded by an earlier pass, and IEnumerable<T> for their elements and for themselves.
        var groupings = queries && groupingPairs is { Count: > 0 } pairs ? pairs.ToList() : [];
        foreach (var (k, e) in groupings)
        {
            if (!elements.Contains(e)) elements.Add(e);
            if (!elements.Contains(GroupingName(k, e))) elements.Add(GroupingName(k, e));
        }
        foreach (var t in queryElementTypes ?? []) if (!elements.Contains(t)) elements.Add(t);
        foreach (var key in (iteratorShapeRequests ?? []).Keys) if (!elements.Contains(key)) elements.Add(key);
        foreach (var key in (arrayCollectionRequests ?? []).Keys) if (!elements.Contains(key)) elements.Add(key);
        if ((ActiveFeatures & ScalarFeatures.ProcessArguments) != 0 && source.Contains("sealed class " + StringArrayName, StringComparison.Ordinal) && !elements.Contains("string")) elements.Add("string");
        if (elements.Count == 0) return source;
        var text = new StringBuilder(source);
        foreach (var t in elements)
        {
            var element=iteratorShapeRequests?.GetValueOrDefault(t)??arrayCollectionRequests?.GetValueOrDefault(t)??t;
            activeIterators!.Add("__IEnumerator_" + t); activeIterators.Add("__IEnumerable_" + t);
            text.Append($"\nabstract class __IEnumerator_{t} {{\n    public abstract fn MoveNext() -> bool\n    public abstract fn get_Current() -> {element}\n    public abstract fn Dispose() -> void\n}}\n");
            text.Append($"abstract class __IEnumerable_{t} {{\n    public abstract fn GetEnumerator() -> __IEnumerator_{t}\n}}\n");
        }
        if ((ActiveFeatures & ScalarFeatures.TypedArrays) != 0) text.Append(ArrayEnumeratorSource());
        static string F(string signature, string body) => $"fn {signature}\n    ensures true\n{{\n{body}\n}}\n";
        static string M(string signature, string body) => $"    public {signature}\n        ensures true\n    {{\n{body}\n    }}\n";
        foreach (var e in groupings.Select(p => p.Element).Distinct())
        {
            text.Append($"sealed class __GrpN_{e} {{\n    public v: {e}\n    public next: __GrpN_{e}\n}}\n");
            text.Append(F($"__grp_items_{e}(first: __GrpN_{e}) -> IEnumerable<{e}>", "    var n = first\n    while (n != null) {\n        yield return n.v\n        n = n.next\n    }"));
        }
        foreach (var (k, e) in groupings)
        {
            var g = GroupingName(k, e); var x = g[6..];
            var equal = k == "bool" ? "g.Key && key || !g.Key && !key" : "g.Key == key";
            text.Append($"sealed class {g} : __IEnumerable_{e} {{\n    public Key: {k} {{ get; private set; }}\n    public __head: __GrpN_{e}\n    public __tail: __GrpN_{e}\n    public __next: {g}\n");
            text.Append(M($"fn new(key: {k}) -> {g}", "        this.Key = key"));
            text.Append(M($"override fn GetEnumerator() -> __IEnumerator_{e}", $"        return __grp_items_{e}(__head).GetEnumerator()"));
            text.Append("}\n");
            text.Append(F($"__grp_new_{x}(key: {k}) -> {g}", $"    return new {g}(key)"));
            text.Append(F($"__grp_find_{x}(first: {g}, key: {k}) -> {g}", $"    var g = first\n    while (g != null) {{\n        if ({equal}) {{\n            return g\n        }}\n        g = g.__next\n    }}\n    return null"));
            text.Append(F($"__grp_link_{x}(tail: {g}, g: {g}) -> void", "    tail.__next = g"));
            text.Append(F($"__grp_next_{x}(g: {g}) -> {g}", "    return g.__next"));
            text.Append(F($"__grp_add_{x}(g: {g}, v: {e}) -> void", $"    let n = new __GrpN_{e}()\n    n.v = v\n    if (g.__tail == null) {{\n        g.__head = n\n    }} else {{\n        g.__tail.next = n\n    }}\n    g.__tail = n"));
        }
        return text.ToString();
    }
    private static readonly HashSet<string> DelegateSignatureTypes = new(StringComparer.Ordinal)
        { "int", "bool", "long", "uint", "ulong", "short", "ushort", "byte", "sbyte", "f32", "f64", "float", "double", "char", "string", "decimal", "object" };
    private static string ExtractDelegates(string source, out Diagnostic? error)
    {
        error = null;
        var text = source.ToCharArray(); var found = new List<(int Start, int Stop)>();
        var depth = 0; var lineStart = true;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\n') { lineStart = true; continue; }
            if (c is ' ' or '\t' or '\r') continue;
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i + 1 < source.Length && source[i + 1] != '\n') i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2; while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++; lineStart = false; continue;
            }
            if (c is '"' or '\'')
            {
                var quote = c; i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n') { if (source[i] == '\\') i++; i++; }
                lineStart = false; continue;
            }
            if (depth == 0 && lineStart && IsWordAt(source, i, "delegate"))
            {
                var stop = LineEnd(source, i);
                while (true)
                {
                    var next = stop; while (next < source.Length && char.IsWhiteSpace(source[next])) next++;
                    if (!IsWordAt(source, next, "where")) break;
                    stop = LineEnd(source, next);
                }
                found.Add((i, stop));
                for (var k = i; k < stop; k++) if (text[k] is not '\n' and not '\r') text[k] = ' ';
                i = stop - 1; lineStart = false; continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            lineStart = false;
        }
        if (found.Count == 0) return source;
        var generated = new StringBuilder();
        try
        {
            var declaredTypes = Regex.Matches(source, DelegateTypeDeclarationPattern)
                .Select(m => IdentifierName(m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
            var infos = new List<(DelegateInfo Info, List<(string Type, int Offset)> Types, int ReturnOffset)>();
            foreach (var (start, stop) in found)
            {
                var info = new ExtendedBinder(activeFeatures, source[start..stop], LocationAt(source, start)).ParseDelegateDeclaration(); var name = info.Name;
                if (declaredTypes.Contains(name) || infos.Any(d => d.Info.Name == name) || DelegateSignatureTypes.Contains(name) && !(info.EscapedName && (ReservedTypeParameterNames.Contains(name) || BuiltInTypeNames.Contains(name))) || name is "IDisposable" || name.StartsWith("__", StringComparison.Ordinal)&&CompilationBinding(name) is null)
                    throw DeclarationError("AI3106", $"The type '{name}' is already declared.", info.Location);
                infos.Add((info, info.SignatureLocations.Take(info.Parameters.Count).Select(t => (t.Type, t.Location.Offset)).ToList(), info.ReturnLocation.Offset));
            }
            var arities = Regex.Matches(source, DelegateTypeDeclarationPattern)
                .GroupBy(m => IdentifierName(m.Groups[1].Value)).ToDictionary(g => g.Key, g => g.First().Groups[2].Success ? g.First().Groups[2].Value.Split(',').Length : 0);
            foreach (var item in infos) arities[item.Info.Name] = item.Info.TypeParameters.Count;
            var delegateNames = infos.Select(d => d.Info.Name).ToHashSet();
            var runtimeSymbols = infos.SelectMany(d => new[] { "__" + d.Info.Name + "_fn" }.Concat(DelegateOperations.Select(op => "__" + d.Info.Name + "_" + op))).ToHashSet();
            var reserved = new Lexer(source).Lex().FirstOrDefault(t => t.Kind == TokenKind.Identifier && runtimeSymbols.Contains(IdentifierName(t.Text)));
            if (reserved is not null) throw DeclarationError("AI3106", $"Identifier '{reserved.Text}' is reserved for delegate runtime storage and helpers.", reserved.Location);
            foreach (var (info, types, returnOffset) in infos)
            {
                var parameters = info.TypeParameters.Select(p => p.Name).ToHashSet();
                bool Known(string type) => KnownDelegateSignature(ParameterPassing(type).Element, arities, delegateNames, 0, parameters);
                foreach (var (type, offset) in types)
                    if (!Known(type)) throw DeclarationError(type is "void" or "unit" ? "AI3110" : "AI3106", $"Unsupported scalar type '{type}'.", LocationAt(source, offset));
                if (!Known(info.Return) && info.Return is not ("void" or "unit")) throw DeclarationError("AI3106", $"Unsupported scalar type '{info.Return}'.", LocationAt(source, returnOffset));
                activeDelegates![info.Name] = info;
                generated.Append(info.TypeParameters.Count == 0 ? DelegateText(info) : GenericDelegateText(info, source.Length + generated.Length));
            }
            foreach (System.Text.RegularExpressions.Match helper in Regex.Matches(generated.ToString(), @"(?m)^fn (" + IdentifierSpelling.Pattern + @")"))
                delegateHelperOffsets!.Add(source.Length + helper.Groups[1].Index);
        }
        catch (ScalarError failure) { error = failure.Diagnostic; activeDelegates!.Clear(); return new string(text); }
        return new string(text) + generated.ToString();
    }
    private static string DelegateText(DelegateInfo d)
    {
        var n = d.Name; var node = TypeIdentifierSyntax(n); var fn = "__" + n + "_fn";
        var typed = string.Join(", ", d.Parameters.Select((p, i) => $"p{i}: {p.Type}"));
        var args = string.Join(", ", d.Parameters.Select((p, i) => ForwardSyntax(p.Type) + $"p{i}"));
        var more = typed.Length == 0 ? "" : ", " + typed;
        var resultMode = ParameterPassing(d.Return).Mode; var resultElement = ParameterPassing(d.Return).Element;
        var unit = d.Return is "void" or "unit";
        static string F(string signature, params string[] body) => $"fn {signature}\n    ensures true\n{{\n" + string.Concat(body.Select(l => "    " + l + "\n")) + "}\n";
        var t = new StringBuilder();
        t.Append($"\nabstract class {fn} {{\n    public abstract fn Run({typed}) -> {d.Return}\n    public abstract fn Same(other: {fn}) -> bool\n}}\n");
        t.Append($"sealed class {node} {{\n    public h: {fn}\n    public t: {node}\n}}\n");
        t.Append(unit
            ? F($"__{n}_invoke(d: {node}{more}) -> void", $"d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    n.h.Run({args})", "    n = n.t", "}")
            : resultMode != ScalarParameterMode.Value ? F($"__{n}_invoke(d: {node}{more}) -> {d.Return}", $"{ReferenceSyntax(resultMode)}{resultElement} r = ref d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    r = ref n.h.Run({args})", "    n = n.t", "}", "return ref r")
            : F($"__{n}_invoke(d: {node}{more}) -> {d.Return}", $"var r = d.h.Run({args})", "var n = d.t", "while (n != null) {", $"    r = n.h.Run({args})", "    n = n.t", "}", "return r"));
        t.Append(F($"__{n}_append(a: {node}, b: {node}) -> {node}", "if (a.t == null) {", $"    return new {node} {{ h = a.h, t = b }}", "}", $"return new {node} {{ h = a.h, t = __{n}_append(a.t, b) }}"));
        t.Append(F($"__{n}_combine(a: {node}, b: {node}) -> {node}", "if (a == null) {", "    return b", "}", "if (b == null) {", "    return a", "}", $"return __{n}_append(a, b)"));
        t.Append(F($"__{n}_count(d: {node}) -> int", "var k = 0", "var n = d", "while (n != null) {", "    k = k + 1", "    n = n.t", "}", "return k"));
        t.Append(F($"__{n}_at(d: {node}, i: int) -> {node}", "var n = d", "var k = i", "while (k > 0) {", "    n = n.t", "    k = k - 1", "}", "return n"));
        t.Append(F($"__{n}_prefix(s: {node}, v: {node}) -> bool", "var p = s", "var q = v", "while (q != null) {", "    if (!p.h.Same(q.h)) {", "        return false", "    }", "    p = p.t", "    q = q.t", "}", "return true"));
        t.Append(F($"__{n}_splice(s: {node}, i: int, m: int) -> {node}", "if (i == 0) {", $"    return __{n}_at(s, m)", "}", $"return new {node} {{ h = s.h, t = __{n}_splice(s.t, i - 1, m) }}"));
        t.Append(F($"__{n}_remove(s: {node}, v: {node}) -> {node}", "if (s == null) {", "    return null", "}", "if (v == null) {", "    return s", "}",
            $"let c = __{n}_count(s)", $"let m = __{n}_count(v)", "var i = c - m", "while (i >= 0) {", $"    if (__{n}_prefix(__{n}_at(s, i), v)) {{", $"        return __{n}_splice(s, i, m)", "    }", "    i = i - 1", "}", "return s"));
        t.Append(F($"__{n}_equals(a: {node}, b: {node}) -> bool", "if (a == null) {", "    return b == null", "}", "if (b == null) {", "    return false", "}", "var p = a", "var q = b",
            "while (p != null && q != null) {", "    if (!p.h.Same(q.h)) {", "        return false", "    }", "    p = p.t", "    q = q.t", "}", "return p == null && q == null"));
        return t.ToString();
    }
    private static string ExtractDeclarations(string source, ScalarFeatures features, out Diagnostic? error)
    {
        error = null;
        var text = source.ToCharArray();
        var found = new List<(bool IsStruct, int Start, int Stop)>();
        var depth = 0; var lineStart = true;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\n') { lineStart = true; continue; }
            if (c is ' ' or '\t' or '\r') continue;
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { while (i + 1 < source.Length && source[i + 1] != '\n') i++; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2; while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++; lineStart = false; continue;
            }
            if (c is '"' or '\'')
            {
                var quote = c; i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n') { if (source[i] == '\\') i++; i++; }
                lineStart = false; continue;
            }
            var isEnum = (features & ScalarFeatures.Enums) != 0 && IsWordAt(source, i, "enum");
            var isStruct = (features & ScalarFeatures.Classes) != 0 && IsWordAt(source, i, "record") || (features & ScalarFeatures.Structs) != 0 && IsWordAt(source, i, "struct") || (features & ScalarFeatures.Classes) != 0 && (IsWordAt(source, i, "class") || IsWordAt(source, i, "interface")
                || (IsWordAt(source, i, "abstract") || IsWordAt(source, i, "sealed") || IsWordAt(source,i,"readonly") || (features & ScalarFeatures.StaticMembers) != 0 && IsWordAt(source, i, "static")) && IsModifiedType(source, i));
            if (depth == 0 && lineStart && (isEnum || isStruct))
            {
                // A record without a body (record P(x: int)) ends at the end of its line.
                var close = isEnum ? source.IndexOf('}', i) : IsRecordDeclaration(source,i) && !RecordHasBody(source, i) ? LineEnd(source, i) - 1 : ClosingBrace(source, i) - 1;
                var stop = close < 0 ? source.Length : close + 1;
                found.Add((isStruct, i, stop));
                for (var k = i; k < stop; k++) if (text[k] is not '\n' and not '\r') text[k] = ' ';
                i = stop - 1; lineStart = false; continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
            lineStart = false;
        }
        try
        {
            if ((features & ScalarFeatures.Using) != 0)
                foreach (var (_, start, stop) in found)
                {
                    var declared = System.Text.RegularExpressions.Regex.Match(source[start..stop], @"^interface\s+(IDisposable)\b");
                    if (declared.Success && !(source.EndsWith(BuiltInDisposable, StringComparison.Ordinal) && start == source.Length - BuiltInDisposable.Length + 1))
                        throw DeclarationError("AI3106", "IDisposable is the built-in System.IDisposable; it cannot be declared.", LocationAt(source, start + declared.Groups[1].Index));
                }
            if ((features & ScalarFeatures.Generics) != 0) { ExtractGenerics(source, text); ProtectSignatureCommas(text); }
            // 1. struct names (structs may refer to each other in any order), 2. enums in source order, 3. struct members, 4. layout cycles, 5. member signatures.
            var bodies = new List<(ExtendedBinder Binder, int Start, int Stop)>();
            foreach (var (isStruct, start, stop) in found.Where(d => d.IsStruct))
            {
                var binder = new ExtendedBinder(features, source[start..stop], LocationAt(source, start));
                var bound=binder.BindStructName(activeStructs!.Count);
                activeStructs.Add(bound with{IsNativeType=(features&ScalarFeatures.TypeIdentity)!=0&&bound.Name==NativeTypeClassName,IsNativeRuntimeType=(features&ScalarFeatures.TypeIdentity)!=0&&bound.Name==NativeRuntimeTypeClassName});
                bodies.Add((binder, start, stop));
            }
            // Generic type declarations use the same constrained proofs as functions and methods.
            var typeBases = new List<IReadOnlyList<(string Name, SourceLocation Location)>>();
            for (var k = 0; k < bodies.Count; k++)
            {
                var template = activeStructs![k];
                if (template.TypeParameters.Count == 0) continue;
                var map = new Dictionary<string, ScalarType>(StringComparer.Ordinal); var list = new List<ScalarType>();
                foreach (var parameter in typeGenerics![k].Parameters)
                {
                    var (placeholder, constraints) = MakeParameterPlaceholder(typeGenerics[k], parameter, bodies.Count);
                    if (parameter.Constraints.Count == 0) placeholder = placeholder with { Location = template.Location };
                    activeStructs.Add(placeholder); typeBases.Add(constraints);
                    map[parameter.Name] = placeholder.Id; list.Add(placeholder.Id);
                }
                foreach (var parameter in typeGenerics[k].Parameters) activeStructs[parameter.Placeholder] = activeStructs[parameter.Placeholder] with { TypeArguments = map };
                activeStructs[k] = template with { Template = k, TypeArguments = map, TypeArgumentList = list, IsCheckInstance = true };
                templateDeclarations![k] = (source[bodies[k].Start..bodies[k].Stop], LocationAt(source, bodies[k].Start));
                bodies[k].Binder.UseTypeArguments(map, true);
            }
            if (typeGenerics!.Values.Any(g => g.Parameters.Any(p => p.Constraints.Count > 0)))
            {
                var earlyBases = bodies.Select(b => b.Binder.HeaderBases()).ToList();
                earlyBases.AddRange(typeBases); ResolveHierarchy(earlyBases);
            }
            foreach (var (isStruct, start, stop) in found.Where(d => !d.IsStruct))
                activeEnums!.Add(new ExtendedBinder(features, source[start..stop], LocationAt(source, start)).BindEnum(activeEnums.Count));
            var spans = new List<List<MemberSpan>>(); var bases = new List<IReadOnlyList<(string Name, SourceLocation Location)>>();
            for (var k = 0; k < bodies.Count; k++)
            {
                var (fields, methods, baseList, properties) = bodies[k].Binder.BindStructBody(activeStructs![k]);
                activeStructs[k] = activeStructs[k] with { Fields = fields, Properties = properties, Positional = bodies[k].Binder.LastPositional };
                spans.Add(methods); bases.Add(baseList);
            }
            // Register method type parameters before creating their constrained placeholders.
            if ((features & ScalarFeatures.Generics) != 0)
            {
                var signatures = text.ToArray();
                foreach (var list in spans) foreach (var span in list)
                    for (var i = span.Start; i < span.Stop; i++) signatures[i] = source[i];
                foreach (var span in spans.SelectMany(list => list).Where(span => span.InterfaceQualifier is not null))
                    for (var i = span.QualifierStart; i < span.NameOffset; i++) if (signatures[i] is not ('\r' or '\n')) signatures[i] = ' ';
                ExtractGenerics(source, signatures);
            }
            // Placeholders and instances created while member types were bound have no members and no base list.
            while (spans.Count < activeStructs!.Count) { spans.Add([]); bases.Add([]); }
            var genericSlots = spans.Select((list, owner) => list.Any(span => activeGenerics!.ContainsKey(span.NameOffset)
                && (activeStructs[owner].IsInterface || span.InterfaceQualifier is not null || span.Modifiers.Any(m => m is "virtual" or "override" or "abstract" or "sealed")))).Any(found => found);
            if (genericSlots)
            {
                BindMemberSignatures(source, new string(text), spans);
                ResolveHierarchy(bases);
                for (var k = 0; k < activeStructs.Count; k++) FillInstanceMethods(k);
                DiscoverGenericConstraintSources();
                while (spans.Count < activeStructs.Count) { spans.Add([]); bases.Add([]); }
                AddTypeParameterPlaceholders(spans, bases);
                ResolveHierarchy(bases);
                InheritGenericMethodConstraints();
                ResolveHierarchy(bases);
                CheckRecordHierarchy();
                CheckLayoutCycles();
            }
            else
            {
                AddTypeParameterPlaceholders(spans, bases);
                ResolveHierarchy(bases);
                CheckRecordHierarchy();
                CheckLayoutCycles();
                BindMemberSignatures(source, new string(text), spans);
            }
            EnsureRecordPositionalOverrides();
            ValidateInheritedRecordParameters();
            EnsureRecordEqualityApis();
            EnsureRecordDisplayApis();
            EnsureRecordHashApis();
            EnsureReadonlyStructMembers();
            EnsureRecordTypeContracts();
            ValidateNativeTypeInheritance();
            templateMembersReady = true;
            for (var k = 0; k < activeStructs.Count; k++) FillInstanceMethods(k);
            ValidateStaticHiding();
            ResolveSlots();
            for (var k = 0; k < activeStructs.Count; k++)
                if ((features & ScalarFeatures.ProcessArguments) != 0 && activeStructs[k].Name == StringArrayName)
                    activeStructs[k] = activeStructs[k] with { IsStringArray = true };
            typeConstraintsReady = true;
            ValidatePendingTypeUses();
        }
        catch (ScalarError failure) { error = failure.Diagnostic; }
        return new string(text);
    }

    private static readonly string[] BuiltInTypeNames = ["int", "i32", "bool", "char", "string", "decimal", "object", "f32", "f64", "float", "double", "unit", "void", "long", "short", "i16", "byte", "u8", "sbyte", "i8", "ushort", "uint", "ulong"];
    // fn name<T, U>(...) -> R where T: class, Base, IFace where U: ...  (one line from 'fn' to the return type): the type parameter list
    // and the where clause are recorded and blanked (offsets and lines are kept) so that the document parser sees an ordinary function.
    private static void ExtractGenerics(string source, char[] text)
    {
        foreach (Match match in Regex.Matches(new string(text), @"(?m)^[ \t]*(?:async[ \t]+)?fn[ \t]+(" + IdentifierSpelling.Pattern + @")[ \t]*<"))
        {
            var name = match.Groups[1]; var open = match.Index + match.Length - 1;
            var eol = source.IndexOf('\n', open); if (eol < 0) eol = source.Length;
            var close = source.IndexOf('>', open);
            if (close < 0 || close > eol) throw DeclarationError("AI3106", "Unterminated type parameter list.", LocationAt(source, open));
            var info = new GenericInfo(IdentifierName(name.Value), name.Index, []);
            var start = open + 1;
            foreach (var part in source[(open + 1)..close].Split(','))
            {
                var at = start + (part.Length - part.TrimStart().Length); var parameter = part.Trim(); start += part.Length + 1;
                if (parameter.Length == 0) throw DeclarationError("AI3106", "Expected a type parameter name.", LocationAt(source, at));
                if (!ValidTypeParameterName(parameter))
                    throw DeclarationError("AI3106", $"Invalid type parameter name '{parameter}'.", LocationAt(source, at));
                parameter = IdentifierName(parameter);
                if (info.Parameters.Any(p => p.Name == parameter)) throw DeclarationError("AI3106", $"Duplicate type parameter '{parameter}'.", LocationAt(source, at));
                info.Parameters.Add(new(parameter, LocationAt(source, at)));
            }
            for (var i = open; i <= close; i++) text[i] = ' ';
            var arrow = source.IndexOf("->", close, StringComparison.Ordinal);
            if (arrow >= 0 && arrow < eol)
            {
                var clauses = TypeConstraintMarkers(source[arrow..eol],out var contentEnd);
                if (clauses.Length>0)
                {
                    var clauseStart = arrow + clauses[0].Start; var clauseEnd = arrow+contentEnd;
                    for(var clauseIndex=0;clauseIndex<clauses.Length;clauseIndex++)
                    {
                        var clause=clauses[clauseIndex];
                        var parameterName = IdentifierName(clause.Name); var at = arrow + clause.NameOffset;
                        var parameter = info.Parameters.FirstOrDefault(p => p.Name == parameterName)
                            ?? throw DeclarationError("AI3106", $"'{parameterName}' is not a type parameter of '{info.Name}'.", LocationAt(source, at));
                        if (parameter.HasWhere) throw DeclarationError("AI3106", $"A constraint clause has already been specified for type parameter '{parameterName}'.", LocationAt(source, at));
                        parameter.HasWhere = true;
                        var offset=arrow+clause.End;
                        var stop=clauseIndex+1<clauses.Length?arrow+clauses[clauseIndex+1].Start:clauseEnd;
                        foreach (var item in SplitTypeList(source[offset..stop]))
                        {
                            var itemAt = offset + (item.Length - item.TrimStart().Length); var constraint = item.Trim(); offset += item.Length + 1;
                            if (constraint.Length == 0) throw DeclarationError("AI3106", "Expected a type constraint.", LocationAt(source, itemAt));
                            if (Regex.IsMatch(constraint, @"^new\s*\(\s*\)$")) constraint = "new()";
                            if (constraint is "notnull" or "default")
                                throw DeclarationError("AI3100", $"The '{constraint}' constraint is not supported yet.", LocationAt(source, itemAt));
                            if (constraint != "new()" && QualifiedBuiltinAlias(constraint) is null && !Regex.IsMatch(constraint, @"^" + IdentifierSpelling.Pattern + @"(?:<.*>)?$")) throw DeclarationError("AI3106", $"Invalid type constraint '{constraint}'.", LocationAt(source, itemAt));
                            if (constraint is "class" or "struct" or "unmanaged" && parameter.Constraints.Count > 0)
                                throw DeclarationError("AI3106", $"The '{constraint}' constraint must come before any other constraints.", LocationAt(source, itemAt));
                            if (parameter.Constraints.Any(c => c.Text == "new()"))
                                throw DeclarationError("AI3106", "The 'new()' constraint must be last.", LocationAt(source, itemAt));
                            if (constraint == "new()" && parameter.Constraints.Any(c => c.Text is "struct" or "unmanaged"))
                                throw DeclarationError("AI3106", "The 'new()' constraint cannot be combined with 'struct' or 'unmanaged'.", LocationAt(source, itemAt));
                            if (parameter.Constraints.Any(c => c.Text == constraint)) throw DeclarationError("AI3106", $"Duplicate constraint '{constraint}' for type parameter '{parameterName}'.", LocationAt(source, itemAt));
                            parameter.Constraints.Add((constraint, LocationAt(source, itemAt)));
                        }
                    }
                    for (var i = clauseStart; i < clauseEnd; i++) text[i] = ' ';
                }
            }
            if (!activeGenerics!.ContainsKey(info.NameOffset)) activeGenerics[info.NameOffset] = info;
        }
    }
    // Each type parameter gets a placeholder declaration after the user's types (so user type indices are unchanged); its constraints are its base list.
    private static void AddTypeParameterPlaceholders(List<List<MemberSpan>> spans, List<IReadOnlyList<(string Name, SourceLocation Location)>> bases)
    {
        var users = activeStructs!.Count;
        foreach (var generic in activeGenerics!.Values.OrderBy(g => g.NameOffset))
        {
            var enclosing = spans.FindIndex(list => list.Any(span => span.NameOffset == generic.NameOffset));
            var map = enclosing >= 0 && activeStructs[enclosing].TypeArguments is { } enclosingArguments
                ? new Dictionary<string, ScalarType>(enclosingArguments) : new Dictionary<string, ScalarType>();
            foreach (var parameter in generic.Parameters)
            {
                var (placeholder, constraints) = MakeParameterPlaceholder(generic, parameter, users, map);
                activeStructs.Add(placeholder); spans.Add([]); bases.Add(constraints);
            }
            foreach (var parameter in generic.Parameters) map[parameter.Name] = activeStructs[parameter.Placeholder].Id;
            foreach (var parameter in generic.Parameters) activeStructs[parameter.Placeholder] = activeStructs[parameter.Placeholder] with { TypeArguments = map };
        }
    }

    private static (ScalarStruct Placeholder, List<(string Name, SourceLocation Location)> Bases) MakeParameterPlaceholder(GenericInfo generic, TypeParameterInfo parameter, int users, IReadOnlyDictionary<string, ScalarType>? ownerArguments = null)
    {
        var all = activeStructs!;
        if (all.Count >= 1000) throw DeclarationError("AI3100", "Too many types and type parameters.", parameter.Location);
        var isClass = false; var refKnown = false; var valueKnown = false; var unmanagedKnown = false; var constructorKnown = false; var list = new List<(string Name, SourceLocation Location)>();
        foreach (var (writtenConstraint, at) in parameter.Constraints)
        {
            var constraint=ImportSignatureSyntax(writtenConstraint,generic.Parameters.Select(p=>p.Name).Concat(ownerArguments?.Keys??Enumerable.Empty<string>()).ToHashSet(StringComparer.Ordinal));
            if (constraint == "class") { isClass = refKnown = true; continue; }
            if (constraint is "struct" or "unmanaged") { valueKnown = true; unmanagedKnown = constraint == "unmanaged"; continue; }
            if (constraint == "new()") { constructorKnown = true; continue; }
            if (constraint == "object") throw DeclarationError("AI3106", "Constraint cannot be special class 'object'.", at);
            if (generic.Parameters.Any(p => p.Name == constraint) || ownerArguments?.ContainsKey(constraint) == true) continue;
            var head = constraint.Split('<')[0].Trim();
            if(QualifiedBuiltinAlias(head) is {} qualified && (head.StartsWith("global::",StringComparison.Ordinal)||!all.Take(users).Any(x=>x.Name=="System"&&x.TypeParameters.Count==0)))head=qualified;
            if (head == "IEquatable" && !all.Take(users).Any(x => x.Name == head)) head = NativeEquatableName;
            if (!all.Take(users).Any(x=>x.Name==head)) head=head switch {"EqualityComparer"=>NativeEqualityComparerName,"IEqualityComparer"=>NativeEqualityComparerInterface,_=>head};
            if(ArrayCollectionKinds.Contains(head)&&!all.Take(users).Any(x=>x.Name==head)&&(activeFeatures&ScalarFeatures.TypedArrays)!=0)
            {isClass=!valueKnown;list.Add((constraint,at));continue;}
            var t = all.FindIndex(0, users, x => x.Name == IdentifierName(head) && !x.IsTypeParameter && (x.Template < 0 || x.Template == all.IndexOf(x)));
            if (t < 0) throw DeclarationError("AI3106", $"Unknown constraint type '{constraint}'.", at);
            var b = all[t];
            if (!b.IsClass) throw DeclarationError("AI3106", $"'{constraint}' is not a valid constraint: a struct cannot be used as a constraint.", at);
            if (!b.IsInterface)
            {
                if (valueKnown) throw DeclarationError("AI3106", "A value-type constraint cannot be combined with a base class.", at);
                if (parameter.Constraints.TakeWhile(c => c.Text != constraint).Any(c => generic.Parameters.Any(p => p.Name == c.Text) || ownerArguments?.ContainsKey(c.Text) == true))
                    throw DeclarationError("AI3106", "A base class constraint must precede type parameter constraints.", at);
                if (b.IsSealed) throw DeclarationError("AI3106", $"'{constraint}' is not a valid constraint: a sealed class cannot be used as a constraint.", at);
                if (refKnown) throw DeclarationError("AI3106", $"The class type constraint '{constraint}' must come before any other constraints, and cannot be combined with 'class'.", at);
                refKnown = true;
            }
            isClass = !valueKnown; list.Add((constraint, at));
        }
        parameter.Placeholder = all.Count;
        return (new(parameter.Name, (ScalarType)(StructTypeBase + all.Count), [], [], parameter.Location, isClass) { IsAbstract = true, IsTypeParameter = true, ReferenceKnown = refKnown, HasValueConstraint = valueKnown, HasUnmanagedConstraint = unmanagedKnown, HasConstructorConstraint = constructorKnown }, list);
    }

    // C# CS0523: a struct cannot contain itself by value, directly or through other structs.
    private static void CheckLayoutCycles()
    {
        var all = activeStructs!; var state = new int[all.Count];
        void Visit(int index)
        {
            state[index] = 1;
            foreach (var field in all[index].Fields)
            {
                var inner = (int)field.Type - StructTypeBase;
                if (inner < 0 || inner >= all.Count || all[inner].IsClass) continue; // a class field is a reference
                if (state[inner] == 1)
                    throw new ScalarError(new("AI3106", DiagnosticSeverity.Error, $"Struct member '{all[index].Name}.{field.Name}' of type '{all[inner].Name}' causes a cycle in the struct layout.", field.Location));
                if (state[inner] == 0) Visit(inner);
            }
            state[index] = 2;
        }
        for (var i = 0; i < state.Length; i++) if (state[i] == 0) Visit(i);
    }

    // Member functions reuse the document parser's function syntax: a copy of the source keeps only the member texts (offsets unchanged).
    // A member function in a declaration body: its text span, modifiers, and (for an abstract or interface member) the signature parsed without a body.
    private sealed record MemberSpan(string Name, int NameOffset, int Start, int Stop, bool IsPublic, IReadOnlySet<string> Modifiers, FunctionDeclaration? Signature)
    { public bool IsInternal { get; init; } public bool IsInternalConstructor { get; init; } public bool IsProtectedConstructor { get; init; } public string? InterfaceQualifier { get; init; } public int QualifierStart { get; init; } }
    private static void BindMemberSignatures(string source, string blanked, List<List<MemberSpan>> spans)
    {
        if (spans.All(list => list.Count == 0) && !activeStructs!.Any(t => t.StaticInitializers.Count > 0 || t.InstanceInitializers.Count > 0 || NeedsRecordConstruction(t) || t.IsRecord && t.Positional>0 && (activeFeatures&ScalarFeatures.RecordMembers)!=0)) return;
        // The module header and functions stay; inside declarations only the member function texts are restored.
        var only = blanked.ToCharArray();
        foreach (var list in spans) foreach (var span in list) for (var i = span.Start; i < span.Stop; i++) only[i] = source[i];
        foreach (var span in spans.SelectMany(list => list).Where(span => span.InterfaceQualifier is not null))
            for (var i = span.QualifierStart; i < span.NameOffset; i++) if (only[i] is not ('\r' or '\n')) only[i] = ' ';
        if ((activeFeatures & ScalarFeatures.Generics) != 0) { ExtractGenerics(source, only); ProtectSignatureCommas(only); }
        var features = activeFeatures;
        ParseResult parsed;
        activeFeatures = ScalarFeatures.None;
        try { parsed = AISharpParser.Parse(new string(only), StrictProfile); }
        finally { activeFeatures = features; }
        // Syntax errors inside member texts are reported here; everything else is reported by the document parse or by member binding.
        var syntax = parsed.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error && !d.Code.StartsWith("AI2", StringComparison.Ordinal) && !d.Code.StartsWith("AI3", StringComparison.Ordinal)
            && spans.Any(list => list.Any(span => d.Location.Offset >= span.Start && d.Location.Offset < span.Stop
                && !(d.Code == "AI1006" && span.Name == "new" && span.Modifiers.Contains("static")))));
        if (syntax is not null) throw new ScalarError(syntax);
        for (var k = 0; k < spans.Count; k++)
        {
            var declared = activeStructs![k]; var methods = declared.IsCheckInstance && declared.Template < 0 ? declared.Methods.ToList() : new List<ScalarStructMethod>();
            foreach (var span in spans[k])
            {
                var function = span.Signature ?? parsed.Document.Functions.FirstOrDefault(f => f.Location.Offset == span.NameOffset)
                    ?? throw new ScalarError(new("AI3106", DiagnosticSeverity.Error, $"Member '{span.Name}' is not a valid function declaration.", LocationAt(source, span.NameOffset)));
                function = function with { Name = IdentifierName(function.Name) };
                if (span.Modifiers.Contains("async")) function = function with { IsAsync = true };
                var isStaticConstructor = span.Name == "new" && span.Modifiers.Contains("static");
                if (isStaticConstructor && function.IsAsync) throw DeclarationError("AI3106", "A static constructor cannot be async.", function.Location);
                var isConstructor = span.Name == "new" && !isStaticConstructor;
                if (isConstructor && function.IsAsync) throw DeclarationError("AI3106", "A constructor cannot be async.", function.Location);
                var memberName = isStaticConstructor ? "<cctor>" : CanonicalMemberName(span.Name);
                if (isStaticConstructor && (function.Parameters.Count != 0 || function.ReturnType is not ("void" or "unit")))
                    throw DeclarationError("AI3106", "A static constructor has no parameters and returns void.", function.Location);
                if (isConstructor && declared.IsInterface)
                    throw new ScalarError(new("AI3106", DiagnosticSeverity.Error, $"Interface '{declared.Name}' cannot declare a constructor.", function.Location));
                if (isConstructor && (RecordConstructorsEnabled(declared)?function.ReturnType!=RecordConstructorDeclaration(declared).Name&&DeconstructType(declared,function.ReturnType)!=declared.Id:IdentifierName(function.ReturnType) != declared.Name))
                    throw new ScalarError(new("AI3106", DiagnosticSeverity.Error, $"A constructor of '{declared.Name}' is written 'fn new(...) -> {declared.Name}'.", function.Location));
                if (isConstructor && function.Parameters.Count == 0 && !declared.IsClass
                    && ((activeFeatures & ScalarFeatures.InstanceInitializers) == 0 || !span.IsPublic))
                    throw DeclarationError("AI3106", "A parameterless struct constructor requires the instance-initializer profile and public access.",function.Location);
                if (methods.Any(m => m.Name == memberName && m.IsConstructor == isConstructor && m.IsStaticConstructor == isStaticConstructor) && !(memberName is "Equals" or "GetHashCode"&&!declared.IsInterface&&(activeFeatures&ScalarFeatures.Boxing)!=0) && !(isConstructor&&RecordConstructorsEnabled(declared)) && !(declared.IsRecord&&DisplayEnabled&&memberName is "ToString" or "PrintMembers") && !(declared.IsRecord&&HashingEnabled&&memberName=="GetHashCode") && !((memberName=="Deconstruct"||memberName=="Equals"&&declared.IsRecord&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0)&&!declared.IsInterface&&(activeFeatures&ScalarFeatures.RecordMembers)!=0) || !isConstructor && (declared.StaticFields.Any(f => f.Name == memberName) || declared.Fields.Any(f => f.Name == memberName&&(!RecordMemberHiding(declared)||f.Owner==k)) || declared.Properties.Any(p => p.Name == memberName)))
                    throw new ScalarError(new("AI3106", DiagnosticSeverity.Error, $"Duplicate member '{span.Name}' in {(declared.IsClass ? "class" : "struct")} '{declared.Name}'.", function.Location));
                var qualifiedName=isConstructor&&RecordConstructorsEnabled(declared)?declared.Name+".$record_ctor_"+function.Location.Offset:declared.Name+"."+((memberName=="Deconstruct"||memberName is "Equals" or "GetHashCode"&&(declared.IsRecord||(activeFeatures&ScalarFeatures.Boxing)!=0)||declared.IsRecord&&DisplayEnabled&&memberName is "ToString" or "PrintMembers"||declared.IsRecord&&HashingEnabled&&memberName=="GetHashCode")&&methods.Any(m=>m.Name==memberName)?(memberName=="Deconstruct"?"$deconstruct_source_":memberName=="Equals"?"$equals_source_":"$display_source_")+function.Location.Offset:memberName == "new" && !isConstructor && !isStaticConstructor ? "@new" : memberName);
                methods.Add(new(memberName, qualifiedName, span.IsPublic, isConstructor, function)
                {
                    IsStatic = span.Modifiers.Contains("static"), IsReadonly = span.Modifiers.Contains("readonly") || declared.IsReadonly&&!isConstructor&&!span.Modifiers.Contains("static")&&!span.Modifiers.Contains("init-accessor"), IsStaticConstructor = isStaticConstructor, IsVirtual = span.Modifiers.Contains("virtual"), IsOverride = span.Modifiers.Contains("override"), IsAbstract = span.Modifiers.Contains("abstract") || declared.IsInterface && span.Signature is not null && !span.Modifiers.Contains("concrete-accessor"),
                    IsSealed = span.Modifiers.Contains("sealed"), Owner = k, IsInternal=span.IsInternal, IsInternalConstructor = span.IsInternalConstructor, IsProtectedConstructor=isConstructor&&span.IsProtectedConstructor, IsProtectedMember=!isConstructor&&span.IsProtectedConstructor, RecordConstructorKind=isConstructor&&RecordConstructorsEnabled(declared)?"source":null, InterfaceQualifier = span.InterfaceQualifier,
                    AccessorMember = span.Modifiers.FirstOrDefault(m => m.StartsWith("accessor-member:", StringComparison.Ordinal))?["accessor-member:".Length..],
                    ReceiverMode = (activeFeatures&ScalarFeatures.MutableStructMethods)!=0&&!declared.IsClass&&!span.Modifiers.Contains("static")&&!isConstructor ? StructMethodReceiverMode(function,span.Modifiers.Contains("readonly")||declared.IsReadonly&&!span.Modifiers.Contains("init-accessor")) : declared.IsReadonly&&!isConstructor&&!span.Modifiers.Contains("static")&&!span.Modifiers.Contains("init-accessor") ? ReadonlyStructReceiverMode(function) : !declared.IsClass && !span.Modifiers.Contains("static") && (span.Modifiers.Contains("accessor") || span.Name=="Deconstruct" && (activeFeatures&ScalarFeatures.RecordMembers)!=0 || span.Name=="Equals" && declared.IsRecord && (activeFeatures&ScalarFeatures.RecordInheritance)!=0 || declared.IsRecord && span.Name is "ToString" or "PrintMembers" && DisplayEnabled || HashingEnabled&&span.Name=="GetHashCode") && (activeFeatures & ScalarFeatures.MutableStructAccessors) != 0 ? span.Modifiers.Contains("readonly") && !span.Modifiers.Contains("init-accessor") ? ScalarParameterMode.In : ScalarParameterMode.Ref : ScalarParameterMode.Value,
                    IsAccessor = span.Modifiers.Contains("accessor"), IsInitAccessor = span.Modifiers.Contains("init-accessor"), Backing = span.Modifiers.FirstOrDefault(m => m.StartsWith("backing:", StringComparison.Ordinal))?["backing:".Length..]
                });
            }
            if (declared.InstanceInitializers.Count > 0 && !declared.IsClass && !methods.Any(m => m.IsConstructor) && !(declared.IsRecord && declared.HasRecordPrimaryConstructor))
                throw DeclarationError("AI3106", "A struct with instance initializers must declare a constructor.",declared.Location);
            if(declared.IsRecord&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0)
                for(var ordinal=0;ordinal<methods.Count;ordinal++)
                {
                    var candidate=methods[ordinal];
                    if(candidate.Name!="Equals"||candidate.Declaration.ReturnType!="bool"||candidate.Declaration.Parameters.Count!=1||ExtendedBinder.GenericOf(candidate.Declaration)is not null)continue;
                    var parameter=candidate.Declaration.Parameters[0];if(ParameterPassing(parameter.Type).Mode!=ScalarParameterMode.Value||DeconstructType(declared,parameter.Type)!=declared.Id)continue;
                    if(candidate.IsStatic||!candidate.IsPublic||declared.IsClass&&!declared.IsSealed&&!candidate.IsVirtual&&!candidate.IsOverride&&!candidate.IsAbstract)throw DeclarationError("AI3106","A typed record Equals member must be public, instance and virtual unless its class is sealed.",candidate.Declaration.Location);
                    methods[ordinal]=candidate with{IsRecordCustomEquality=true};
                }
            if((activeFeatures&ScalarFeatures.RecordMembers)!=0&&(declared.IsRecord&&declared.Positional>0||methods.Any(m=>m.Name=="Deconstruct")))AddRecordDeconstructionMethods(declared,k,methods);
            if(declared.IsRecord&&declared.IsClass&&(activeFeatures&ScalarFeatures.RecordInheritance)!=0)methods.Add(RecordEqualityMethod(declared,k));
            if(RecordConstructorsEnabled(declared))
            {
                for(var ordinal=0;ordinal<methods.Count;ordinal++)if(methods[ordinal].IsConstructor&&RecordCopySignature(declared,methods[ordinal]))methods[ordinal]=methods[ordinal] with{RecordConstructorKind="copy"};
                AddRecordConstructors(declared,k,methods);
            }
            if (NeedsRecordConstruction(declared)&&(!RecordConstructorsEnabled(declared)||methods.Any(m=>m.RecordConstructorKind is "primary" or "default"))) methods.Add(RecordInitializerMethod(declared,k));
            else for (var i = 0; i < declared.InstanceInitializers.Count; i++) methods.Add(InstanceInitializerMethod(declared,k,i));
            if (declared.StaticInitializers.Count > 0 && !methods.Any(m => m.IsStaticConstructor))
                methods.Add(ImplicitStaticConstructor(declared, k));
            activeStructs[k] = declared with { Methods = methods };
        }
    }

    private sealed partial class ExtendedBinder
    {
        // A switch statement is a break target (IsSwitch) but not a continue target; BreakStates are the definite-assignment states at its breaks.
        // N17: the built-in exception types (C# System names) and their base types.
        private static readonly Dictionary<string, string?> ExceptionBases = new(StringComparer.Ordinal)
        {
            ["Exception"] = null, ["SystemException"] = "Exception", ["ArithmeticException"] = "SystemException",
            ["DivideByZeroException"] = "ArithmeticException", ["OverflowException"] = "ArithmeticException",
            ["NullReferenceException"] = "SystemException", ["InvalidCastException"] = "SystemException", ["IndexOutOfRangeException"] = "SystemException",
            ["ArrayTypeMismatchException"] = "SystemException",
            ["InvalidOperationException"] = "SystemException", ["ObjectDisposedException"] = "InvalidOperationException",
            ["ArgumentException"] = "SystemException", ["ArgumentNullException"] = "ArgumentException", ["ArgumentOutOfRangeException"] = "ArgumentException",
            ["NotSupportedException"] = "SystemException", ["NotImplementedException"] = "SystemException", ["FormatException"] = "SystemException",
            ["TargetInvocationException"] = "Exception", ["OutOfMemoryException"] = "SystemException", ["InsufficientExecutionStackException"] = "SystemException", ["InvalidProgramException"] = "SystemException", ["TypeInitializationException"] = "SystemException",
        };
        private static bool ExceptionDerives(string type, string ancestor)
        {
            for (string? t = type; t is not null; t = ExceptionBases[t]) if (t == ancestor) return true;
            return false;
        }
        // The run-time names a catch clause for 'type' matches (SwitchExpressionException derives from InvalidOperationException).
        private static List<string> CaughtNames(string type)
        {
            var names = ExceptionBases.Keys.Where(k => (k != "TypeInitializationException" || activeTypeInitialization) && (k != "ArrayTypeMismatchException" || activeTypedArrayFaults) && ExceptionDerives(k, type)).Select(AsyncRuntimeName).ToList();
            if (ExceptionDerives("InvalidOperationException", type)) names.Add("System.Runtime.CompilerServices.SwitchExpressionException");
            return names;
        }
        private readonly List<string> _handlerContexts = [];
        private readonly Stack<int> _finallyLoopDepth = new();
        private (ScalarStatement Statement, bool Returns) TryStatement()
        {
            var keyword = Next(); var before = new HashSet<string>(_assigned);
            _handlerContexts.Add("try");
            (IReadOnlyList<ScalarStatement> Body, bool Returns) body;
            try { body = Block(); } finally { _handlerContexts.RemoveAt(_handlerContexts.Count - 1); }
            var states = new List<HashSet<string>>(); if (!body.Returns) states.Add(new(_assigned));
            var clauses = new List<ScalarStatement>(); var caught = new List<string>(); var general = false; var allReturn = body.Returns;
            NewLines();
            while (Current.Text == "catch")
            {
                var clause = Next(); string? type = null;
                if (general) Fail("AI3106", "A catch clause cannot follow the general catch clause of a try statement.", clause.Location);
                if (Take("("))
                {
                    var typeToken = Next();
                    if (typeToken.Text == "TargetInvocationException" && StructNamedAny(typeToken.Text) is not null)
                        Fail("AI3106", "A source type named TargetInvocationException is not the built-in reflection exception.", typeToken.Location);
                    if (!ExceptionBases.ContainsKey(typeToken.Text)) Fail("AI3103", $"Unknown exception type '{typeToken.Text}'; only the built-in System exception types are supported.", typeToken.Location);
                    if (IsIdentifier(Current.Text)) Fail("AI3100", "Catch variables are not supported yet; use 'catch (E)'.", Current.Location);
                    Expect(")");
                    if (caught.FirstOrDefault(previous => ExceptionDerives(typeToken.Text, previous)) is { } earlier)
                        Fail("AI3106", $"A previous catch clause already catches all exceptions of this or a super type ('{earlier}').", typeToken.Location);
                    type = typeToken.Text; caught.Add(type);
                }
                else general = true;
                if (Current.Text == "when") Fail("AI3100", "Exception filters ('when') are not supported yet.", Current.Location);
                _assigned = new(before);
                _handlerContexts.Add("catch");
                (IReadOnlyList<ScalarStatement> Body, bool Returns) handler;
                try { handler = Block(); } finally { _handlerContexts.RemoveAt(_handlerContexts.Count - 1); }
                if (!handler.Returns) states.Add(new(_assigned));
                allReturn &= handler.Returns;
                clauses.Add(new ScalarStatement("catch", clause.Location, Name: type is null || type == "Exception" ? "*" : string.Join(",", CaughtNames(type)), Body: handler.Body));
                NewLines();
            }
            HashSet<string>? finallyAssigned = null;
            if (Current.Text == "finally")
            {
                var clause = Next(); _assigned = new(before);
                _handlerContexts.Add("finally"); _finallyLoopDepth.Push(_loops.Count);
                (IReadOnlyList<ScalarStatement> Body, bool Returns) cleanup;
                try { cleanup = Block(); } finally { _handlerContexts.RemoveAt(_handlerContexts.Count - 1); _finallyLoopDepth.Pop(); }
                finallyAssigned = new(_assigned);
                clauses.Add(new ScalarStatement("finally", clause.Location, Body: cleanup.Body));
            }
            if (clauses.Count == 0) Fail("AI3106", "A try statement needs a catch or finally clause.", keyword.Location);
            var merged = states.Count == 0 ? new HashSet<string>(before) : new HashSet<string>(states[0]);
            foreach (var state in states.Skip(1)) merged.IntersectWith(state);
            if (finallyAssigned is not null) merged.UnionWith(finallyAssigned);
            _assigned = merged;
            return (new ScalarStatement("try", keyword.Location, Body: body.Body, Alternative: clauses), allReturn);
        }
        private (ScalarStatement Statement, bool Returns) ThrowStatement()
        {
            var keyword = Next();
            if (Current.Text is "\n" or ";" or "}" or "")
            {
                // throw; rethrows the exception of the nearest enclosing catch clause (not from a finally clause nested inside it).
                var nearest = _handlerContexts.LastOrDefault(c => c is "catch" or "finally");
                if (nearest != "catch") Fail("AI3106", "A throw statement with no arguments is allowed only in a catch clause.", keyword.Location);
                EndStatement(); return (new ScalarStatement("throw", keyword.Location), true);
            }
            if (Current.Text != "new") Fail("AI3100", "Only 'throw new E()' of a built-in exception type is supported.", Current.Location);
            Next(); var typeToken = Next();
            if (typeToken.Text == "__SwitchExpressionException" && _function.Name == "__task_throw" && activeAsync)
            {
                Expect("("); Expect(")"); EndStatement();
                return (new ScalarStatement("throw", keyword.Location, Name: AsyncRuntimeName("SwitchExpressionException")), true);
            }
            if (!ExceptionBases.ContainsKey(typeToken.Text)) Fail("AI3103", $"Unknown exception type '{typeToken.Text}'; only the built-in System exception types are supported.", typeToken.Location);
            if (typeToken.Text == "TargetInvocationException" && !(_function.Name == "__task_throw" && activeAsync))
                Fail("AI3100", "Exception objects with constructor arguments are not supported yet.", typeToken.Location);
            Expect("(");
            if (Current.Text != ")") Fail("AI3100", "Exception constructor arguments (messages) are not supported yet.", Current.Location);
            Expect(")");
            EndStatement(); return (new ScalarStatement("throw", keyword.Location, Name: AsyncRuntimeName(typeToken.Text)), true);
        }
        // ---- N19: delegates, lambdas, anonymous methods, method groups and closures ----
        // A lambda (or anonymous method, or method group) has no type of its own: it is a placeholder until it converts to a delegate type,
        // where it is bound in place (outer locals are visible) and lifted into a sealed class deriving from __D_fn. Captured locals and parameters
        // become cells (one shared object per variable instance, as C# display classes share variables); 'this' of a class is captured by reference.
        private const int LambdaTypeId = 3502, MethodGroupTypeId = 3503;
        private static bool IsFunctionValue(ScalarType type) => (int)type is LambdaTypeId or MethodGroupTypeId;
        private bool IsDelegate(ScalarType type) => IsClass(type) && StructOf(type).IsDelegate && !StructOf(type).IsTypeParameter;
        private DelegateInfo DelegateOf(ScalarType type) => ClosedDelegateInfo(type);
        private static bool Delegating0 => activeDelegates is { Count: > 0 };
        private bool InDelegateHelper => _function is not null && delegateHelperOffsets?.Contains(_function.Location.Offset) == true;
        private bool Delegating => Has(ScalarFeatures.Delegates) && Delegating0;
        private int _noLambda;
        private readonly HashSet<string> _cells = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScalarExpression> _constInits = new(StringComparer.Ordinal);
        private bool _bindingConst;
        private readonly HashSet<string> _patternLocals = new(StringComparer.Ordinal);
        private readonly Dictionary<(int, ScalarType), ScalarExpression> _lambdaMemo = new();
        private Lexeme TokenAt(int p) => _tokens[Math.Min(p, _tokens.Count - 1)];
        private static int? LocalId(string name) =>
            name.StartsWith("__scalar_local_", StringComparison.Ordinal) && int.TryParse(name["__scalar_local_".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
        private int FunctionClassOf(DelegateInfo info) => info.TypeParameters.Count == 0 ? _structs.FindIndex(x => x.Name == "__" + info.Name + "_fn")
            : IndexOf(Type("__" + info.DefinitionName + "_fn<" + string.Join(", ", info.TypeArguments.Select(SignatureTypeName)) + ">", info.Location));
        // After a declaration error the delegate support classes may have no members yet; conversions are then checked but not lifted,
        // so the declaration error is the one reported.
        private bool DelegateReady(DelegateInfo info) => FunctionClassOf(info) is var k && k >= 0 && _structs[k].Methods.Any(m => m.Name == "Run") && _structs[k].Methods.Any(m => m.Name == "Same");
        private void Lift(ScalarFunction function)
        {
            if (ValidateReferenceExits(function, _structs) is { } diagnostic) throw new ScalarError(diagnostic);
            if (!_checkOnly) liftedFunctions!.Add((Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers)) ? Erase(function) : function);
        }
        private static ScalarExpression Literal(bool value, SourceLocation at) => new("literal", ScalarType.Bool, at, value ? "true" : "false");
        private string SourceName(string emitted)
        {
            if (_this?.EmittedName == emitted) return "this";
            foreach (var scope in _scopes) foreach (var (name, local) in scope) if (local.EmittedName == emitted) return name;
            return _this is not null && emitted == _this.EmittedName ? "this" : emitted;
        }
        private bool IsLambdaAt(int p)
        {
            if (!Delegating || _noLambda > 0) return false;
            var t = TokenAt(p).Text;
            if (t == "static") return TokenAt(p + 1).Text != "static" && IsLambdaAt(p + 1);
            if (t == "delegate") return TokenAt(p + 1).Text == "(";
            if (IsSourceIdentifier(t) && !IsReserved(t) && TokenAt(p + 1).Text == "=>") return true;
            if (t != "(") return false;
            var depth = 0;
            for (var q = p; q < _tokens.Count; q++)
            {
                var x = _tokens[q].Text;
                if (x == "(") depth++;
                else if (x == ")") { if (--depth == 0) return TokenAt(q + 1).Text == "=>"; }
                else if (x is "" or "\n" or "{" or "}" or ";" or "=>") return false;
            }
            return false;
        }
        private void SkipBalanced(string open, string close)
        {
            var depth = 0;
            do
            {
                if (Current.Text == "") Fail("AI3106", $"Expected '{close}'.", Current.Location);
                if (Current.Text == open) depth++; else if (Current.Text == close) depth--;
                Next();
            } while (depth > 0);
        }
        // The placeholder records the token span of the lambda; its body is bound when the lambda converts to a delegate type.
        private ScalarExpression LambdaPlaceholder()
        {
            var start = _position; var at = Current.Location;
            Take("static");
            var anonymous = Take("delegate");
            if (Current.Text == "(") SkipBalanced("(", ")"); else Next();
            if (!anonymous) Expect("=>");
            if (Current.Text == "{") SkipBalanced("{", "}");
            else if (anonymous) Fail("AI3106", "An anonymous method needs a block body.", Current.Location);
            else
            {
                var depth = 0;
                while (Current.Text != "" && !(depth == 0 && Current.Text is "," or ")" or "]" or "}" or "\n" or ";"))
                {
                    if (Current.Text is "(" or "[" or "{") depth++; else if (Current.Text is ")" or "]" or "}") depth--;
                    Next();
                }
            }
            return new("lambda", (ScalarType)LambdaTypeId, at, start.ToString(CultureInfo.InvariantCulture) + ":" + _position.ToString(CultureInfo.InvariantCulture));
        }
        private ScalarExpression? DelegateMember(ScalarExpression receiver, Lexeme member)
        {
            member = member with { Text = member.Name };
            if (!IsStruct(receiver.Type)) return null;
            receiver=RecordMemberReceiver(receiver,member);
            var info = StructOf(receiver.Type);
            if (CheckedInstanceProperty(receiver,member) is { } contract) return IsDelegate(contract.Type) ? PropertyRead(receiver,contract,member) : null;
            if (FindProperty(IndexOf(info.Id), member.Text) is { } property) return IsDelegate(property.Type) ? FieldRead(receiver, member) : null;
            var fieldIndex=VisibleInstanceFieldIndex(info,member.Text);var field=fieldIndex>=0?info.Fields[fieldIndex]:null;
            return field is not null && IsDelegate(field.Type) ? FieldRead(receiver, member) : null;
        }
        // receiver.member after the dot: d.Invoke(args) on a delegate, o.f(args) for a delegate-typed field or property, o.m without arguments as a method group.
        private ScalarExpression MemberAccess(ScalarExpression receiver, Lexeme member)
        {
            member = member with { Text = member.Name };
            receiver=RecordMemberReceiver(receiver,member);
            if (IsDelegate(receiver.Type) && !InDelegateHelper)
            {
                if (member.Text == "Invoke" && Current.Text == "(") return Invoke(receiver, member.Location);
                if (member.Text is "Method" or "Target" or "GetInvocationList" or "DynamicInvoke" or "Clone" or "HasSingleTarget")
                    Fail("AI3100", $"The delegate member '{member.Text}' is not supported yet.", member.Location);
                Fail("AI3103", $"'{StructOf(receiver.Type).Name}' has no member '{member.Text}'.", member.Location);
            }
            var index = IndexOf(receiver.Type);
            if (CheckedInstanceProperty(receiver,member) is { } checkedProperty)
            {
                var value = PropertyRead(receiver,checkedProperty,member);
                return Current.Text == "(" && IsDelegate(value.Type) ? Invoke(value,member.Location) : value;
            }
            if (Current.Text == "(" && (FindMethod(index, member.Text) is null||RecordDelegateHidesMethod(receiver.Type,member.Text)) && DelegateMember(receiver, member) is { } target) return Invoke(target, member.Location);
            if (MethodGroupAhead() && CallableMethod(receiver.Type, member)
                && VisibleInstanceFieldIndex(StructOf(receiver.Type),member.Text)<0 && FindProperty(index, member.Text) is null)
                return MethodGroupExpression(member, receiver);
            return Current.Text == "(" || Current.Text == "<" && CallableMethod(receiver.Type, member) ? MethodCall(receiver, member) : FieldRead(receiver, member);
        }
        // d(args) is __D_invoke(d, args): every callable of the invocation list runs in order and the last result is returned; a null d faults
        // with NullReferenceException after the arguments are evaluated, as in C#.
        private ScalarExpression Invoke(ScalarExpression receiver, SourceLocation at)
        {
            var info = DelegateOf(receiver.Type);
            Read(RootOf(receiver));
            Expect("(");
            var arguments = new List<ScalarExpression>();
            if (!Take(")"))
            {
                do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                Expect(")");
            }
            if (arguments.Count != info.Parameters.Count)
                Fail("AI3116", $"Delegate '{info.Name}' takes {info.Parameters.Count} argument(s) but {arguments.Count} were supplied.", at);
            for (var i = 0; i < arguments.Count; i++) arguments[i] = BindArgument(arguments[i], Type(info.Parameters[i].Type, at), ParameterPassing(info.Parameters[i].Type).Mode);
            CompleteArguments(arguments);
            return DelegateHelperCall(info, "invoke", [receiver, .. arguments], at);
        }
        // C# delegate operators: + is Delegate.Combine, - is Delegate.Remove (the last occurrence of the right invocation list), == and != compare invocation lists.
        private ScalarExpression DelegateBinary(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            var type = IsDelegate(left.Type) ? left.Type : right.Type;
            if (IsDelegate(left.Type) && IsDelegate(right.Type) && left.Type != right.Type)
            {
                if (ReferenceConvertible(_structs, left.Type, right.Type, [])) type = right.Type;
                else if (!ReferenceConvertible(_structs, right.Type, left.Type, [])) Fail("AI3107", $"Operator '{symbol}' is not defined for {Show(left.Type)} and {Show(right.Type)}.", at);
            }
            if (symbol is not ("+" or "-" or "==" or "!=")) Fail("AI3107", $"Operator '{symbol}' is not defined for delegates.", at);
            var l = Coerce(left, type); var r = Coerce(right, type);
            ScalarExpression call = DelegateOperationCall(type, symbol switch { "+" => "combine", "-" => "remove", _ => "equals" }, l, r, at);
            return symbol == "!=" ? new("unary!", ScalarType.Bool, at, Left: call) { Wraps = _wraps } : call;
        }
        private ScalarType CellType(ScalarType type, SourceLocation at)
        {
            var key = "cell:" + ((int)type).ToString(CultureInfo.InvariantCulture);
            if (syntheticClasses!.TryGetValue(key, out var known)) return _structs[known].Id;
            var k = _structs.Count; var id = (ScalarType)(StructTypeBase + k);
            _structs.Add(new ScalarStruct($"__cell_{k}", id, [new ScalarStructField("v", type, EraseType(type), true, false, at) { Owner = k }], [], at, IsClass: true) { IsSealed = true });
            syntheticClasses[key] = k;
            return id;
        }
        private static ScalarExpression CellNew(ScalarType cell, ScalarExpression value) =>
            new("init", cell, value.Location, "0", Left: new("new", cell, value.Location)) { Arguments = [value] };
        // Rewrites a body whose captured variables (in _cells) are declared in it: their declarations allocate cells, their reads and writes go
        // through the cell, and 'cellref' (a lambda creation capturing the variable) is the cell itself. 'outer' maps the free variables of a lambda body.
        private (List<ScalarParameter> Parameters, List<ScalarStatement> Body) Cellify(IReadOnlyList<ScalarParameter> parameters, IReadOnlyList<ScalarStatement> body, Func<ScalarExpression, ScalarExpression?>? outer)
        {
            var result = new List<ScalarParameter>(); var prologue = new List<ScalarStatement>();
            var at0 = _function?.Location ?? new SourceLocation(1, 1, 0);
            foreach (var parameter in parameters)
            {
                if (!_cells.Contains(parameter.Name)) { result.Add(parameter); continue; }
                var fresh = Fresh(); result.Add(new(fresh, parameter.Type));
                var cell = CellType(parameter.Type, at0);
                prologue.Add(new ScalarStatement("local", at0, CellNew(cell, new("local", parameter.Type, at0, fresh)), parameter.Name, cell) { Mutable = false });
            }
            ScalarExpression E(ScalarExpression e)
            {
                if (e.Operation is "local" or "cellref" && e.Value is { } name)
                {
                    if (outer?.Invoke(e) is { } mapped) return mapped;
                    if (!_cells.Contains(name)) return e;
                    var holder = new ScalarExpression("local", CellType(e.Type, e.Location), e.Location, name);
                    return e.Operation == "cellref" ? holder : new("field", e.Type, e.Location, "0", Left: holder);
                }
                return e with { Left = e.Left is null ? null : E(e.Left), Right = e.Right is null ? null : E(e.Right), Arguments = e.Arguments?.Select(E).ToList() };
            }
            ScalarStatement S(ScalarStatement s)
            {
                var r = s with
                {
                    Expression = s.Expression is null ? null : E(s.Expression), Increment = s.Increment is null ? null : E(s.Increment),
                    Initializer = s.Initializer is null ? null : S(s.Initializer), Body = s.Body?.Select(S).ToList(), Alternative = s.Alternative?.Select(S).ToList()
                };
                if (s.Kind == "local" && s.Name is { } name && _cells.Contains(name) && !s.Const)
                {
                    var cell = CellType(s.Type, s.Location);
                    var initial = r.Expression ?? (s.Type == ScalarType.String ? new("literal", ScalarType.String, s.Location, "") : DefaultValue(s.Type, s.Location));
                    return new ScalarStatement("local", s.Location, CellNew(cell, initial), name, cell) { Mutable = false };
                }
                return r;
            }
            return (result, [.. prologue, .. body.Select(S)]);
        }
        private static void Visit(ScalarExpression? e, Action<ScalarExpression> action)
        {
            if (e is null) return;
            action(e); Visit(e.Left, action); Visit(e.Right, action);
            if (e.Arguments is { } arguments) foreach (var argument in arguments) Visit(argument, action);
        }
        private static void Visit(ScalarStatement s, Action<ScalarExpression> action)
        {
            Visit(s.Expression, action); Visit(s.Increment, action);
            if (s.Initializer is { } initializer) Visit(initializer, action);
            foreach (var x in s.Body ?? []) Visit(x, action);
            foreach (var x in s.Alternative ?? []) Visit(x, action);
        }
        // Same(other) for a lifted class: other is exactly this class and every captured cell (and 'this') is the same object.
        private ScalarFunction SameFunction(string name, ScalarType self, ScalarType fnType, IReadOnlyList<ScalarStructField> fields, SourceLocation at)
        {
            var me = new ScalarParameter(Fresh(), self); var other = new ScalarParameter(Fresh(), fnType);
            var meRead = new ScalarExpression("local", self, at, me.Name); var otherRead = new ScalarExpression("local", fnType, at, other.Name);
            ScalarExpression test = new("isexact", ScalarType.Bool, at, ((int)self).ToString(CultureInfo.InvariantCulture), Left: otherRead);
            List<ScalarStatement> body;
            if (fields.Count == 0) body = [new("return", at, test)];
            else
            {
                var cast = new ScalarExpression("downcast", self, at, Left: otherRead);
                ScalarExpression? all = null;
                for (var i = 0; i < fields.Count; i++)
                {
                    var index = i.ToString(CultureInfo.InvariantCulture);
                    ScalarExpression equal = new("==", ScalarType.Bool, at, Left: new("field", fields[i].Type, at, index, Left: meRead), Right: new("field", fields[i].Type, at, index, Left: cast)) { Wraps = _wraps };
                    all = all is null ? equal : new("&&", ScalarType.Bool, at, Left: all, Right: equal) { Wraps = _wraps };
                }
                body = [new("if", at, test, Body: [new("return", at, all)], Alternative: []), new("return", at, Literal(false, at))];
            }
            return new ScalarFunction(name, [me, other], ScalarType.Bool, body, [TrueContract(at)], at);
        }
        private (int Index, ScalarType Id, string Name) NewLiftedClass(string prefix, DelegateInfo info, List<ScalarStructField> fields, SourceLocation at)
        {
            var k = _structs.Count; if (k >= 1000) Fail("AI3100", "More than 1000 native type identities are not supported.", at);
            var id = (ScalarType)(StructTypeBase + k); var name = $"{prefix}_{k}";
            var fnIndex = FunctionClassOf(info); var fnClass = _structs[fnIndex];
            var run = fnClass.Methods.First(m => m.Name == "Run"); var same = fnClass.Methods.First(m => m.Name == "Same");
            var methods = new List<ScalarStructMethod>
            {
                new("Run", name + ".Run", true, false, run.Declaration) { IsOverride = true, Slot = run.Slot, Owner = k, IsLifted = true },
                new("Same", name + ".Same", true, false, same.Declaration) { IsOverride = true, Slot = same.Slot, Owner = k, IsLifted = true },
            };
            _structs.Add(new ScalarStruct(name, id, fields.Select(f => f with { Owner = k }).ToList(), methods, at, IsClass: true) { BaseIndex = fnIndex, IsSealed = true });
            return (k, id, name);
        }
        private static ScalarExpression WrapDelegate(ScalarType target, ScalarType fnType, ScalarExpression callable, SourceLocation at) =>
            new("init", target, at, "0", Left: new("new", target, at)) { Arguments = [new ScalarExpression("upcast", fnType, at, Left: callable)] };
        private ScalarExpression ConvertLambda(ScalarExpression site, ScalarType target)
        {
            if (_lambdaMemo.TryGetValue((site.Location.Offset, target), out var known)) return known;
            var info = DelegateOf(target); var at = site.Location;
            if (_contract) Fail("AI3100", "Lambdas are not supported in contracts.", at);
            if (_iterElem is not null) Fail("AI3100", "Lambdas and anonymous methods inside iterators are not supported yet.", at);
            if (_asyncTask is not null) Fail("AI3100", "Lambdas and anonymous methods inside async functions are not supported yet.", at);
            var span = site.Value!.Split(':');
            int start = int.Parse(span[0], CultureInfo.InvariantCulture), end = int.Parse(span[1], CultureInfo.InvariantCulture);
            var resume = _position; _position = start;
            var types = info.Parameters.Select(p => Type(p.Type, at)).ToList();
            var returnType = Type(info.Return, at, true);
            var parameters = new List<ScalarParameter>(); List<ScalarStatement> body; bool isStatic;
            var startId = _nextId;
            var saved = (_returnType, _ctor, _initAccessor, _directSection, _baseCallAllowed, _inBaseArgs);
            var savedReturnMode = _returnMode;
            var savedAssigned = new HashSet<string>(_assigned);
            var savedReferenceParameters = new Dictionary<string, ScalarParameterMode>(_referenceParameters);
            var savedLoops = _loops.ToArray(); var savedFinally = _finallyLoopDepth.ToArray();
            var savedHandlers = _handlerContexts.ToList(); var savedPending = _pendingDeclarations.ToList();
            var scopes = _scopes.Count;
            try
            {
                _lambdaNest++;
                isStatic = Take("static");
                var anonymous = Take("delegate");
                var names = new List<(Lexeme Name, Lexeme? Type, ScalarParameterMode Mode, bool Scoped)>();
                if (Take("("))
                {
                    if (!Take(")"))
                    {
                        do
                        {
                            var scoped = Current.Text == "scoped" && TokenAt(_position + 1).Text is "ref" or "in" or "out" && Take("scoped");
                            if (scoped && !Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Scoped lambda parameters are disabled in this profile.", Current.Location);
                            ScalarParameterMode? prefixMode = null;
                            if (Current.Text is "ref" or "out" or "in" && TokenAt(_position + 1).Text is not (":" or ")" or ","))
                            {
                                var modifier = Next(); prefixMode = modifier.Text switch { "out" => ScalarParameterMode.Out, "in" => ScalarParameterMode.In, _ => ScalarParameterMode.Ref };
                                if (modifier.Text == "ref" && Take("readonly")) prefixMode = ScalarParameterMode.RefReadonly;
                            }
                            var name = Next();
                            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text)) Fail("AI3106", $"Invalid lambda parameter name '{name.Text}'.", name.Location);
                            Lexeme? typeToken = Take(":") ? ParameterTypeToken() : null;
                            var annotatedMode = typeToken is null ? ScalarParameterMode.Value : ParameterPassing(typeToken.Text).Mode;
                            if (prefixMode is not null && annotatedMode != ScalarParameterMode.Value) Fail("AI3106", "A lambda parameter has only one reference modifier.", name.Location);
                            names.Add((name, typeToken, prefixMode ?? annotatedMode, scoped || typeToken is not null && ScopedReference(typeToken.Text)));
                        } while (Take(","));
                        Expect(")");
                    }
                }
                else names.Add((Next(), null, ScalarParameterMode.Value, false));
                if (!anonymous) Expect("=>");
                if (anonymous && names.Any(p => p.Type is null)) Fail("AI3106", "The parameters of an anonymous method need explicit types.", at);
                if (names.Any(p => p.Type is null) && names.Any(p => p.Type is not null)) Fail("AI3106", "Lambda parameters must be all explicitly typed or all implicitly typed.", at);
                if (names.Count != info.Parameters.Count) Fail("AI3116", $"Delegate '{info.Name}' does not take {names.Count} argument(s).", at);
                for (var i = 0; i < names.Count; i++)
                {
                    if (names[i].Type is { } given && Type(given.Text, given.Location) != types[i])
                        Fail("AI3107", $"Parameter {i + 1} is declared as type '{given.Text}' but delegate '{info.Name}' takes '{info.Parameters[i].Type}'.", given.Location);
                    if (ParameterPassing(info.Return).Mode != ScalarParameterMode.Value && ScopedReference(info.Parameters[i].Type) && !names[i].Scoped) Fail("AI3107", "The lambda parameter must retain the delegate scoped contract.", names[i].Name.Location);
                    if (!CompatibleReferenceMethod(names[i].Mode, ParameterPassing(info.Parameters[i].Type).Mode))
                        Fail("AI3107", $"Parameter {i + 1} does not match delegate '{info.Name}' passing mode.", names[i].Type?.Location ?? names[i].Name.Location);
                }
                _loops.Clear(); _finallyLoopDepth.Clear(); _handlerContexts.Clear(); _pendingDeclarations.Clear();
                _returnType = returnType; _returnMode = ParameterPassing(info.Return).Mode; _ctor = false; _initAccessor = false; _directSection = -1; _baseCallAllowed = false; _inBaseArgs = false;
                _scopes.Add(new(StringComparer.Ordinal));
                for (var i = 0; i < names.Count; i++)
                {
                    var mode = names[i].Mode;
                    var local = Declare(names[i].Name.Text, types[i], mode is not (ScalarParameterMode.In or ScalarParameterMode.RefReadonly), names[i].Name.Location);
                    if (mode != ScalarParameterMode.Out) _assigned.Add(local.EmittedName);
                    if (mode != ScalarParameterMode.Value) _referenceParameters[local.EmittedName] = mode;
                    parameters.Add(new(local.EmittedName, types[i]) { Mode = mode, Scoped = names[i].Scoped });
                    _referenceContexts[local.EmittedName] = mode is ScalarParameterMode.Ref or ScalarParameterMode.In or ScalarParameterMode.RefReadonly && !names[i].Scoped ? 1 : _scopes.Count + 1;
                }
                if (Current.Text == "{")
                {
                    var block = Block();
                    if (returnType != ScalarType.Unit && !block.Returns) Fail("AI3108", $"Not all code paths of the lambda return a value of type '{info.Return}'.", at);
                    body = block.Body.ToList();
                }
                else
                {
                    var bodyAt = Current.Location; var byRef = Take("ref");
                    if (byRef != (_returnMode != ScalarParameterMode.Value)) Fail("AI3107", "Lambda return reference mode does not match the delegate.", bodyAt);
                    var value = byRef ? ReferenceTarget() : Expression(); Read(value);
                    if (byRef && (value.Type != returnType || ReferenceContext(value) > 1)) Fail("AI3106", "A lambda reference return must preserve type and referent lifetime.", value.Location);
                    if (byRef && _returnMode == ScalarParameterMode.Ref) Write(value);
                    ScalarStatement made;
                    if (returnType == ScalarType.Unit)
                    {
                        if (value.Operation is not ("=" or "array-store" or "static-event-update" or "pre++" or "pre--" or "post++" or "post--" or "call"))
                            Fail("AI3106", "Only assignment, call, increment and decrement expressions can be the body of a lambda whose delegate returns void.", bodyAt);
                        made = new("expression", value.Location, Lower(value));
                    }
                    else made = Returning(Coerce(value, returnType), _returnMode, value.Location);
                    body = [.. _pendingDeclarations, made];
                }
                if (_position != end) Fail("AI3106", $"Unexpected '{Current.Text}' after the lambda body.", Current.Location);
            }
            finally
            {
                while (_scopes.Count > scopes) _scopes.RemoveAt(_scopes.Count - 1);
                (_returnType, _ctor, _initAccessor, _directSection, _baseCallAllowed, _inBaseArgs) = saved;
                _returnMode = savedReturnMode;
                _assigned = savedAssigned;
                _referenceParameters.Clear(); foreach (var parameter in savedReferenceParameters) _referenceParameters.Add(parameter.Key, parameter.Value);
                _loops.Clear(); foreach (var loop in savedLoops.Reverse()) _loops.Push(loop);
                _finallyLoopDepth.Clear(); foreach (var depth in savedFinally.Reverse()) _finallyLoopDepth.Push(depth);
                _handlerContexts.Clear(); _handlerContexts.AddRange(savedHandlers);
                _pendingDeclarations.Clear(); _pendingDeclarations.AddRange(savedPending);
                _position = resume;
                _lambdaNest = Math.Max(0, _lambdaNest - 1);
            }
            // Free variables: locals declared before the lambda (smaller ids) that its body reads, writes or captures again.
            var free = new List<ScalarExpression>();
            foreach (var statement in body)
                Visit(statement, e => { if (e.Operation is "local" or "cellref" && e.Value is { } n && LocalId(n) is { } id && id < startId && !free.Any(f => f.Value == n)) free.Add(e); });
            var fields = new List<ScalarStructField>(); var map = new Dictionary<string, (int Index, bool Cell, ScalarType Type)>(StringComparer.Ordinal);
            var consts = new Dictionary<string, ScalarExpression>(StringComparer.Ordinal); var captures = new List<ScalarExpression>();
            foreach (var e in free.OrderBy(f => LocalId(f.Value!)))
            {
                var n = e.Value!;
                if (_referenceParameters.ContainsKey(n)) Fail("AI3106", "An anonymous function cannot capture a by-reference parameter.", e.Location);
                if (_constLocals.ContainsKey(n)) { consts[n] = _constInits[n]; continue; }
                if (isStatic) Fail("AI3106", $"A static anonymous function cannot contain a reference to '{SourceName(n)}'.", e.Location);
                if (_this is not null && n == _this.EmittedName)
                {
                    if (!_struct!.IsClass) Fail("AI3106", "Anonymous methods and lambdas inside structs cannot access instance members of 'this'.", e.Location);
                    map[n] = (fields.Count, false, e.Type);
                    fields.Add(new($"c{fields.Count}", e.Type, e.Type, true, false, at));
                    captures.Add(new("local", e.Type, at, n));
                    continue;
                }
                if (_patternLocals.Contains(n)) Fail("AI3100", $"Capturing the pattern variable '{SourceName(n)}' in a lambda is not supported yet.", e.Location);
                if (!_checkOnly && DelegateReady(info)) _cells.Add(n);
                map[n] = (fields.Count, true, e.Type);
                var cell = _checkOnly || !DelegateReady(info) ? e.Type : CellType(e.Type, at);
                fields.Add(new($"c{fields.Count}", cell, cell, true, false, at));
                captures.Add(new("cellref", e.Type, at, n));
            }
            if (_checkOnly || !DelegateReady(info)) { var placeholder = new ScalarExpression("new", target, at); _lambdaMemo[(at.Offset, target)] = placeholder; return placeholder; }
            var fnType = _structs[FunctionClassOf(info)].Id;
            var (k, id, className) = NewLiftedClass("__lambda", info, fields, at);
            var self = new ScalarParameter(Fresh(), id); var selfRead = new ScalarExpression("local", id, at, self.Name);
            ScalarExpression? Captured(ScalarExpression e)
            {
                if (consts.TryGetValue(e.Value!, out var constant)) return e.Operation == "local" ? constant : null;
                if (!map.TryGetValue(e.Value!, out var m)) return null;
                var holder = new ScalarExpression("field", fields[m.Index].Type, e.Location, m.Index.ToString(CultureInfo.InvariantCulture), Left: selfRead);
                return !m.Cell || e.Operation == "cellref" ? holder : new("field", m.Type, e.Location, "0", Left: holder);
            }
            var (runParameters, runBody) = Cellify(parameters, body, Captured);
            Lift(new ScalarFunction(className + ".Run", [self, .. runParameters], returnType, runBody, [TrueContract(at)], at) { ReturnMode = ParameterPassing(info.Return).Mode });
            Lift(SameFunction(className + ".Same", id, DelegateSameType(info), _structs[k].Fields, at));
            ScalarExpression callable = fields.Count == 0 ? new("new", id, at)
                : new("init", id, at, string.Join(",", Enumerable.Range(0, fields.Count)), Left: new("new", id, at)) { Arguments = captures };
            var made2 = WrapDelegate(target, fnType, callable, at);
            _lambdaMemo[(at.Offset, target)] = made2;
            return made2;
        }
        // A method group converts when its parameters take the delegate's parameter types and its result converts to the delegate's result
        // (identity or a reference conversion, C# method group variance). One lifted class per method and delegate type: equal delegates have the
        // same class and, for an instance method, the same target object. Creating from a null target faults as C# does: NullReferenceException
        // for a virtual method, ArgumentException otherwise.
        private ScalarExpression ConvertMethodGroup(ScalarExpression site, ScalarType target) =>
            site.Operation=="import-methodgroup"?ConvertMethodGroup(SelectImportedMethodGroup(site,target),target):
            Has(ScalarFeatures.Boxing)&&site.Left is {Operation:not "static-type"}&&IsStruct(site.Left.Type)&&!IsClass(site.Left.Type) || ExtendedMethodGroup(site) || site.Left is not null&&(site.Value=="Deconstruct"&&Has(ScalarFeatures.RecordMembers)||site.Value=="Equals"&&Has(ScalarFeatures.RecordInheritance)&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordDisplay)&&site.Value is "ToString" or "PrintMembers"&&RecordEqualityReceiver(StructOf(site.Left.Type))||Has(ScalarFeatures.RecordHashing)&&site.Value=="GetHashCode"&&RecordEqualityReceiver(StructOf(site.Left.Type))) ? ConvertBoundMethodGroup(site, target) : ConvertLegacyMethodGroup(site, target);

        private ScalarExpression ConvertLegacyMethodGroup(ScalarExpression site, ScalarType target)
        {
            var info = DelegateOf(target); var at = site.Location;
            var types = info.Parameters.Select(p => Type(p.Type, at)).ToList(); var returnType = Type(info.Return, at, true);
            List<ScalarType> parameterTypes; ScalarType resultType; ScalarStructMethod? method = null; ScalarStruct? holderClass = null; string key, label;
            if (site.Left is null)
            {
                var declaration = _functions.First(f => f.Name == site.Value);
                if (GenericOf(declaration) is not null) Fail("AI3100", $"Converting the generic function '{site.Value}' to a delegate is not supported yet.", at);
                parameterTypes = declaration.Parameters.Select(p => Type(p.Type, at)).ToList(); resultType = Type(declaration.ReturnType, at, true);
                key = "fn:" + site.Value + ":" + info.Name; label = site.Value!;
            }
            else if (site.Left.Operation == "static-type")
            {
                var constrainedStatic = site.Left.Value is { } qualifier && _typeArgs?.ContainsKey(qualifier) == true;
                method = constrainedStatic ? StaticInterfaceMember(site.Left, new Lexeme(site.Value!, at)).Implementation : FindMethod(IndexOf(site.Left.Type), site.Value!)!;
                var declaring = _structs[method.Owner];
                if (!constrainedStatic && declaring.IsInterface && method.Slot is not null) Fail("AI3106", "A static abstract or virtual interface member can be accessed only on a constrained type parameter.", at);
                if (!constrainedStatic) CheckAccess(declaring, method.IsPublic, site.Value!, at);
                parameterTypes = method.Declaration.Parameters.Select(p => MemberType(declaring.TypeArguments, p.Type, at)).ToList();
                resultType = MemberType(declaring.TypeArguments, method.Declaration.ReturnType, at, true);
                key = "static:" + method.QualifiedName + ":" + info.Name; label = method.QualifiedName;
            }
            else
            {
                var receiverInfo = StructOf(site.Left.Type);
                method = FindMethod(IndexOf(receiverInfo.Id), site.Value!)!;
                var declaring = method.Owner >= 0 ? _structs[method.Owner] : receiverInfo;
                if (!receiverInfo.IsClass) Fail("AI3100", $"Converting a method of the struct '{receiverInfo.Name}' to a delegate (boxing) is not supported yet.", at);
                if (receiverInfo.IsInterface || declaring.IsInterface || receiverInfo.IsTypeParameter) Fail("AI3100", "Converting an interface method to a delegate is not supported yet.", at);
                CheckAccess(declaring, method.IsPublic, site.Value!, at);
                parameterTypes = method.Declaration.Parameters.Select(p => MemberType(declaring.TypeArguments, p.Type, at)).ToList();
                resultType = MemberType(declaring.TypeArguments, method.Declaration.ReturnType, at, true);
                holderClass = method.Slot is { } slot ? _structs.First(x => x.Name == slot[..slot.LastIndexOf('.')]) : declaring;
                key = "m:" + (method.Slot ?? method.QualifiedName) + ":" + info.Name; label = receiverInfo.Name + "." + site.Value;
            }
            var signature = method?.Declaration ?? _functions.First(f => f.Name == site.Value);
            var resultMode = ParameterPassing(info.Return).Mode;
            if (resultMode != ScalarParameterMode.Value && signature.Parameters.Zip(info.Parameters).Any(p=>ScopedReference(p.Second.Type) && !ScopedReference(p.First.Type))) Fail("AI3107", "Method and delegate scoped contracts do not match.", at);
            if (ParameterPassing(signature.ReturnType).Mode != resultMode || resultMode != ScalarParameterMode.Value && resultType != returnType) Fail("AI3107", "Method and delegate reference returns must have identical modes and element types.", at);
            if (signature.Parameters.Count != info.Parameters.Count || !signature.Parameters.Zip(info.Parameters).All(p => CompatibleReferenceMethod(ParameterPassing(p.First.Type).Mode, ParameterPassing(p.Second.Type).Mode))) Fail("AI3107", "Method and delegate parameter passing modes do not match.", at);
            bool Converts(ScalarType from, ScalarType to) => Has(ScalarFeatures.Boxing)?ReferenceConvertible(_structs,from,to,[]):from == to || IsClass(from) && IsClass(to) && Derives(_structs, IndexOf(from), IndexOf(to)) || IsClass(from) && IsObject(to);
            if (parameterTypes.Count != types.Count || !types.Select((t, i) => ParameterPassing(info.Parameters[i].Type).Mode == ScalarParameterMode.Value ? Converts(t, parameterTypes[i]) : t == parameterTypes[i]).All(ok => ok)
                || !(resultType == returnType || returnType != ScalarType.Unit && resultType != ScalarType.Unit && Converts(resultType, returnType)))
                Fail("AI3107", $"No overload for '{label}' matches delegate '{info.Name}'.", at);
            if (_checkOnly || !DelegateReady(info)) return new("new", target, at);
            var fnType = _structs[FunctionClassOf(info)].Id;
            if (!syntheticClasses!.TryGetValue(key, out var k))
            {
                var fields = holderClass is null ? new List<ScalarStructField>() : [new ScalarStructField("target", holderClass.Id, holderClass.Id, true, false, at)];
                var (index, id, className) = NewLiftedClass("__mg", info, fields, at);
                k = index; syntheticClasses[key] = k;
                var self = new ScalarParameter(Fresh(), id); var selfRead = new ScalarExpression("local", id, at, self.Name);
                var ps = types.Select((t, i) => new ScalarParameter(Fresh(), t) { Mode = ParameterPassing(info.Parameters[i].Type).Mode, Scoped = ScopedReference(info.Parameters[i].Type) }).ToList();
                ScalarExpression call;
                if (holderClass is null)
                {
                    var arguments = ps.Select((p, i) => Coerce(ForwardParameter(p, at), parameterTypes[i])).ToList();
                    call = new("call", resultType, at, method?.QualifiedName ?? site.Value) { Arguments = arguments };
                }
                else
                    call = MethodCall(new ScalarExpression("field", holderClass.Id, at, "0", Left: selfRead), new Lexeme(site.Value!, at), false,
                        ps.Select(p => ForwardParameter(p, at)).ToList());
                List<ScalarStatement> runBody = returnType == ScalarType.Unit ? [new("expression", at, call)] : [Returning(Coerce(call, returnType), ParameterPassing(info.Return).Mode, at)];
                Lift(new ScalarFunction(className + ".Run", [self, .. ps], returnType, runBody, [TrueContract(at)], at) { ReturnMode = ParameterPassing(info.Return).Mode });
                Lift(SameFunction(className + ".Same", id, DelegateSameType(info), _structs[k].Fields, at));
                if (holderClass is not null)
                {
                    var r = new ScalarParameter(Fresh(), holderClass.Id); var rRead = new ScalarExpression("local", holderClass.Id, at, r.Name);
                    var fault = method!.Slot is not null ? "System.NullReferenceException" : "System.ArgumentException";
                    Lift(new ScalarFunction(className + "_check", [r], holderClass.Id,
                        [new ScalarStatement("if", at, new ScalarExpression("==", ScalarType.Bool, at, Left: rRead, Right: new("null", holderClass.Id, at)) { Wraps = _wraps },
                            Body: [new ScalarStatement("throw", at, Name: fault)], Alternative: []), new ScalarStatement("return", at, rRead)], [TrueContract(at)], at));
                }
            }
            var lifted = _structs[k];
            ScalarExpression callable = holderClass is null ? new("new", lifted.Id, at)
                : new("init", lifted.Id, at, "0", Left: new("new", lifted.Id, at))
                { Arguments = [new ScalarExpression("call", holderClass.Id, at, lifted.Name + "_check") { Arguments = [Upcast(site.Left!, holderClass.Id)] }] };
            return WrapDelegate(target, fnType, callable, at);
        }
        // ---- N18: foreach over a string, and using ----
        // foreach (var c in s) { body } is { let t = s; for (var i = 0; i < t.Length; i++) { let c = t[i]; body } }: the string is evaluated once and
        // the iteration variable is read-only, as in C#.
        private (ScalarStatement Statement, bool Returns) ForeachStatement()
        {
            var keyword = Next(); var at = keyword.Location; Expect("(");
            if (Current.Text is not "var" and not "let") Fail("AI3106", "foreach requires 'var name' before 'in'.", Current.Location);
            Next(); var name = Next(); Lexeme? typeToken = null;
            if (Take(":")) typeToken = TypeToken();
            if (Current.Text != "in") Fail("AI3106", "Expected 'in' in a foreach statement.", Current.Location);
            Next();
            var source = Expression(); Read(source); Expect(")");
            if (IsTypedArray(source.Type)) return ForeachTypedArray(at, name, typeToken, source);
            if (IteratorShape(source.Type) is { } enumerated)
            {
                if (!enumerated.Sequence) Fail("AI3106", $"foreach statement cannot operate on variables of type '{Show(source.Type)}' because it does not contain a public definition for 'GetEnumerator'.", source.Location);
                return ForeachSequence(at, name, typeToken, source, enumerated.Element, enumerated.Enumerator);
            }
            if (source.Type != ScalarType.String) Fail("AI3100", $"foreach over {Show(source.Type)} is not supported; only strings can be enumerated.", source.Location);
            if (typeToken is not null && Type(typeToken.Text, typeToken.Location) != ScalarType.Char)
                Fail("AI3100", "The iteration variable of a string is a char; foreach conversions are not supported.", typeToken.Location);
            var before = new HashSet<string>(_assigned);
            var text = HiddenLocal(ScalarType.String);
            var index = new Local($"__scalar_local_{_nextId++}", ScalarType.Int, true); _hiddenLocals[index.EmittedName] = index; _assigned.Add(index.EmittedName);
            var readText = new ScalarExpression("local", ScalarType.String, at, text.EmittedName);
            var readIndex = new ScalarExpression("local", ScalarType.Int, at, index.EmittedName);
            var start = new ScalarStatement("local", at, Fold(new("literal", ScalarType.Int, at, "0")), index.EmittedName, ScalarType.Int) { Mutable = true };
            var condition = Fold(new("<", ScalarType.Bool, at, Left: readIndex, Right: new("length", ScalarType.Int, at, Left: readText)) { Wraps = _wraps });
            var step = Lower(new("post++", ScalarType.Int, at, Left: readIndex) { Wraps = _wraps });
            _scopes.Add(new(StringComparer.Ordinal));
            var element = Declare(name.Text, ScalarType.Char, false, name.Location); _assigned.Add(element.EmittedName);
            var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop();
            foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
            _scopes.RemoveAt(_scopes.Count - 1);
            _assigned = new(before); _assigned.Add(text.EmittedName); _assigned.Add(index.EmittedName);
            var item = new ScalarStatement("local", at, new("index", ScalarType.Char, at, Left: readText, Right: readIndex), element.EmittedName, ScalarType.Char) { Mutable = false };
            var loop = new ScalarStatement("for", at, condition, Body: [item, .. body.Body], Initializer: start, Increment: step);
            return (new ScalarStatement("block", at, Body: [new ScalarStatement("local", at, source, text.EmittedName, ScalarType.String) { Mutable = false }, loop]), false);
        }
        // ---- N21: iterators ----
        // A function or method whose body contains 'yield return' or 'yield break' and whose return type is IEnumerable<T> or IEnumerator<T> is an
        // iterator. Its body is bound normally, then rewritten into a sealed class __iter_k deriving from the hidden abstract class for
        // IEnumerator<T>: MoveNext runs the body as a state machine over the hoisted variables, get_Current returns the last yielded value and
        // Dispose runs the finally blocks enclosing the suspended yield. For IEnumerable<T> a second class holds the arguments and makes a fresh
        // enumerator in GetEnumerator. Calling the iterator runs none of the body (C# deferred execution).
        private ScalarType? _iterElem; private int _lambdaNest;
        private ScalarType? IteratorTypeText(string text, SourceLocation at,bool forceBuiltin=false)
        {
            var m = Regex.Match(text.Trim(), @"^IEnumera(ble|tor)\s*<(.*)>$");
            if (!m.Success) return null;
            if(!forceBuiltin&&StructNamedAny("IEnumera"+m.Groups[1].Value)is not null)return null;
            var arg=m.Groups[2].Value.Trim();var element=Type(arg,at);
            if(element==ScalarType.Unit||IsStruct(element)&&StructOf(element).IsStatic)Fail("AI3106","An iterator element requires a storage type.",at);
            if(IsTypeParameterType(element)||IsStruct(element)&&StructOf(element).IsCheckInstance)return CheckIteratorType(element,m.Groups[1].Value=="ble",at);
            var syntax=SequenceSyntax(element);var key=SequenceShapeKey(syntax);
            if (StructNamed("__IEnumera" + m.Groups[1].Value + "_" + key) is { } found)
            {
                if (Has(ScalarFeatures.TypedArrays) && m.Groups[1].Value == "ble") _structs[IndexOf(found.Id)] = found with { NativeEnumerableElement = element };
                return found.Id;
            }
            if(iteratorShapeRequests!.Count>=128&&!iteratorShapeRequests.ContainsKey(key))Fail("AI3100","More than 128 iterator shapes are unsupported.",at);
            iteratorShapeRequests.TryAdd(key,syntax);Fail("AI3100","The iterator shape requires another binding pass.",at);
            return null;
        }
        private (ScalarType Element, bool Sequence, int Enumerator)? IteratorShape(ScalarType type)
        {
            if (!Has(ScalarFeatures.Iterators) || !IsClass(type)) return null;
            var name = StructOf(type).Name;
            if (StructOf(type).NativeCollectionElement is {} collectionElement && StructOf(type).NativeCollectionKind is {} collectionKind)
            {
                var key=StructOf(type).IsCheckInstance?CheckSequenceKey(collectionElement):name[(NativeCollectionPrefix+collectionKind+"_").Length..];var enumeratorIndex=_structs.FindIndex(t=>t.Name=="__IEnumerator_"+key);
                if(enumeratorIndex>=0)return(collectionElement,true,enumeratorIndex);
            }
            // N25: a hidden grouping class is enumerated as its IEnumerable<E> base.
            if (!IsIteratorInterface(name))
                return (name.StartsWith("__Grp_", StringComparison.Ordinal) || IsStringArray(type)) && StructOf(type).BaseIndex >= 0 ? IteratorShape(_structs[StructOf(type).BaseIndex].Id) : null;
            var sequence = name.StartsWith("__IEnumerable_", StringComparison.Ordinal);
            var enumerator = _structs.FindIndex(x => x.Name == "__IEnumerator_" + name[14..]);
            if (enumerator < 0 || _structs[enumerator].Methods.FirstOrDefault(x => x.Name == "get_Current") is not { } current) return null;
            return (Type(current.Declaration.ReturnType, current.Declaration.Location), sequence, enumerator);
        }
        public static bool DeclarationHasYield(FunctionDeclaration declaration)
            => declaration.Body.ScalarSource is { } source && TokensHaveYield(Lex(source.Text,source.Location,true,true,true));
        private bool BodyHasYield() => TokensHaveYield(_tokens);
        private static bool TokensHaveYield(IReadOnlyList<Lexeme> tokens)
        {
            for (var i = 0; i + 1 < tokens.Count; i++)
                if (tokens[i].Text == "yield" && tokens[i + 1].Text is "return" or "break") return true;
            return false;
        }
        private (ScalarStatement Statement, bool Returns) YieldStatement()
        {
            var keyword = Next(); var at = keyword.Location;
            if (_lambdaNest > 0) Fail("AI3106", "The yield statement cannot be used inside an anonymous method or lambda expression.", at);
            if (_iterElem is not { } element)
            {
                Fail("AI3106", $"The body of '{_function.Name}' cannot be an iterator block because '{_function.ReturnType.Trim()}' is not an iterator interface type.", at);
                return default;
            }
            if (_finallyLoopDepth.Count > 0) Fail("AI3106", "Cannot yield in the body of a finally clause.", at);
            if (Take("break")) { EndStatement(); return (new ScalarStatement("yieldbreak", at), true); }
            Expect("return");
            var value = Expression(); Read(value);
            var converted = Coerce(value, element);
            EndStatement();
            return (new ScalarStatement("yield", at, converted), false);
        }
        private static bool HasYield(ScalarStatement s) =>
            s.Kind == "yield" || s.Initializer is { } i && HasYield(i) || (s.Body ?? []).Any(HasYield) || (s.Alternative ?? []).Any(HasYield);
        private static ScalarStatement FirstYield(ScalarStatement s) =>
            s.Kind == "yield" ? s : new[] { s.Initializer }.Concat(s.Body ?? []).Concat(s.Alternative ?? []).Where(x => x is not null && HasYield(x)).Select(x => FirstYield(x!)).First();
        private sealed class IterRegion
        {
            public int Lo, Hi = int.MaxValue; public IterRegion? Parent; public List<IterRegion> Children { get; } = [];
            public List<ScalarStatement> Finally { get; set; } = [];
            public bool Contains(int state) => state >= Lo && state <= Hi;
        }
        private List<ScalarStatement> LowerIterator(List<ScalarParameter> parameters, IReadOnlyList<ScalarStatement> body)
        {
            body = PrepareReferenceSlots(body, out var referenceSlots);
            var at = _function.Location;
            var shape = IteratorShape(_returnType)!.Value;
            var element = shape.Element; var enumIndex = shape.Enumerator; var enumBase = _structs[enumIndex];
            var seqIndex = shape.Sequence ? IndexOf(_returnType) : -1;
            if (_patternLocals.Count > 0) Fail("AI3100", "Pattern variables inside iterators are not supported yet.", at);
            string[] enumMembers = ["MoveNext", "get_Current", "Dispose"];
            if (enumMembers.Any(n => !enumBase.Methods.Any(x => x.Name == n)) || seqIndex >= 0 && !_structs[seqIndex].Methods.Any(x => x.Name == "GetEnumerator") || _checkOnly)
                return [new ScalarStatement("return", at, new ScalarExpression("null", _returnType, at))];
            // The enumerator class is reserved first so that field reads of it can be typed while the body is rewritten.
            var kE = _structs.Count; var idE = (ScalarType)(StructTypeBase + kE); var nameE = $"__iter_{kE}";
            _structs.Add(new ScalarStruct(nameE, idE, [], [], at, IsClass: true) { BaseIndex = enumIndex, IsSealed = true });
            var fields = new List<ScalarStructField>();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int Hoist(string name, ScalarType type)
            {
                if (map.TryGetValue(name, out var known)) return known;
                map[name] = fields.Count; fields.Add(new ScalarStructField($"v{fields.Count}", type, EraseType(type), true, false, at) { Owner = kE });
                return fields.Count - 1;
            }
            foreach (var p in parameters) Hoist(p.Name, p.Type);
            var self = new ScalarParameter(Fresh(), idE); var selfRead = new ScalarExpression("local", idE, at, self.Name);
            ScalarExpression Field(int index, ScalarType type, SourceLocation where) => new("field", type, where, index.ToString(CultureInfo.InvariantCulture), Left: selfRead);
            ScalarExpression Assign(ScalarExpression target, ScalarExpression value) => new("=", target.Type, value.Location, Left: target, Right: value) { Wraps = _wraps };
            ScalarExpression Int(int value) => new("literal", ScalarType.Int, at, value.ToString(CultureInfo.InvariantCulture));
            // Reserved fields after the hoisted variables are added once generation has finished; their indices are fixed up front here.
            int stateField = -1, currentField = -1, suspField = -1;
            ScalarExpression StateField() => new("field", ScalarType.Int, at, "@state", Left: selfRead);
            ScalarExpression CurrentField() => new("field", element, at, "@current", Left: selfRead);
            ScalarExpression SuspField() => new("field", ScalarType.Bool, at, "@susp", Left: selfRead);
            ScalarExpression E(ScalarExpression e)
            {
                if (e.Operation == "cellref") Fail("AI3100", "Lambdas inside iterators are not supported yet.", e.Location);
                if (e.Operation == "local" && e.Value is { } n && map.TryGetValue(n, out var index)) return Field(index, e.Type, e.Location);
                return e with { Left = e.Left is null ? null : E(e.Left), Right = e.Right is null ? null : E(e.Right), Arguments = e.Arguments?.Select(E).ToList() };
            }
            var dispatch = Fresh(); ScalarExpression S() => new("local", ScalarType.Int, at, dispatch);
            var blocks = new Dictionary<int, List<ScalarStatement>>(); var nextState = 0;
            var root = new IterRegion { Lo = 0 }; var region = root;
            var loopTargets = new Stack<(int Break, int Continue)>();
            List<ScalarStatement> cur = [];
            int New() => nextState++;
            void Open(int state) { cur = []; blocks[state] = cur; }
            ScalarStatement JumpTo(int target, SourceLocation where, bool inSwitch) => new("__jump", where, Name: target.ToString(CultureInfo.InvariantCulture)) { Mutable = inSwitch };
            static bool Ends(IReadOnlyList<ScalarStatement>? list) => list is { Count: > 0 } && (list[^1].Kind is "return" or "__jump" or "continue" or "break" or "throw"
                || list[^1].Kind == "if" && Ends(list[^1].Body) && Ends(list[^1].Alternative));
            void Jump(int target) { if (!Ends(cur)) cur.Add(JumpTo(target, at, false)); }
            // A constant condition (while (true)) jumps directly: the native verifier rejects constant if conditions.
            void Branch(ScalarExpression condition, int yes, int no, SourceLocation where)
            {
                if (condition.Operation == "literal" && condition.Type == ScalarType.Bool) cur.Add(JumpTo(condition.Value == "true" ? yes : no, where, false));
                else cur.Add(new ScalarStatement("if", where, condition, Body: [JumpTo(yes, where, false)], Alternative: [JumpTo(no, where, false)]));
            }
            // An intact statement contains no 'yield return': its variables stay local unless hoisted, and a break or continue that leaves it
            // for a rewritten loop becomes a jump of the state machine.
            ScalarStatement M(ScalarStatement s, bool inLoop, bool inSwitch)
            {
                switch (s.Kind)
                {
                    case "break" when !inLoop && !inSwitch: return JumpTo(loopTargets.Peek().Break, s.Location, false);
                    case "continue" when !inLoop: return JumpTo(loopTargets.Peek().Continue, s.Location, inSwitch);
                    case "yieldbreak": return new ScalarStatement("return", s.Location, Literal(false, s.Location));
                }
                var loop = s.Kind is "while" or "dowhile" or "for"; var sw = s.Kind == "switch";
                return s with
                {
                    Expression = s.Expression is null ? null : E(s.Expression), Increment = s.Increment is null ? null : E(s.Increment),
                    Initializer = s.Initializer is null ? null : M(s.Initializer, inLoop, inSwitch),
                    Body = s.Body?.Select(x => M(x, inLoop || loop, inSwitch && !loop || sw)).ToList(),
                    Alternative = s.Alternative?.Select(x => M(x, inLoop || loop, inSwitch && !loop || sw)).ToList()
                };
            }
            void Local(ScalarStatement s)
            {
                var index = Hoist(s.Name!, s.Type);
                if (s.Expression is { } value) cur.Add(new ScalarStatement("expression", s.Location, Assign(Field(index, s.Type, s.Location), E(value))));
            }
            void Gen(IEnumerable<ScalarStatement> list) { foreach (var s in list) One(s); }
            void One(ScalarStatement s)
            {
                if (s.Kind == "local" && s.Name is not null) { Local(s); return; }
                if (!HasYield(s)) { cur.Add(M(s, false, false)); return; }
                switch (s.Kind)
                {
                    case "yield":
                    {
                        var resume = New();
                        cur.Add(new ScalarStatement("expression", s.Location, Assign(CurrentField(), E(s.Expression!))));
                        cur.Add(new ScalarStatement("expression", s.Location, Assign(StateField(), Int(resume))));
                        cur.Add(new ScalarStatement("expression", s.Location, Assign(SuspField(), Literal(true, s.Location))));
                        cur.Add(new ScalarStatement("return", s.Location, Literal(true, s.Location)));
                        Open(resume); return;
                    }
                    case "block": Gen(s.Body ?? []); return;
                    case "if":
                    {
                        int yes = New(), no = New(), join = New();
                        Branch(E(s.Expression!), yes, no, s.Location);
                        Open(yes); Gen(s.Body ?? []); Jump(join);
                        Open(no); Gen(s.Alternative ?? []); Jump(join);
                        Open(join); return;
                    }
                    case "while":
                    {
                        int head = New(), loopBody = New(), exit = New();
                        Jump(head); Open(head);
                        Branch(E(s.Expression!), loopBody, exit, s.Location);
                        Open(loopBody); loopTargets.Push((exit, head)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(head);
                        Open(exit); return;
                    }
                    case "dowhile":
                    {
                        int loopBody = New(), test = New(), exit = New();
                        Jump(loopBody); Open(loopBody); loopTargets.Push((exit, test)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(test);
                        Open(test);
                        Branch(E(s.Expression!), loopBody, exit, s.Location);
                        Open(exit); return;
                    }
                    case "for":
                    {
                        if (s.Initializer is { } init) One(init);
                        int head = New(), loopBody = New(), step = New(), exit = New();
                        Jump(head); Open(head);
                        if (s.Expression is { } condition)
                            Branch(E(condition), loopBody, exit, s.Location);
                        else Jump(loopBody);
                        Open(loopBody); loopTargets.Push((exit, step)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(step);
                        Open(step);
                        if (s.Increment is { } increment) cur.Add(new ScalarStatement("expression", s.Location, E(increment)));
                        Jump(head);
                        Open(exit); return;
                    }
                    case "try":
                    {
                        var clauses = s.Alternative ?? [];
                        if (clauses.Any(c => c.Kind == "catch"))
                        {
                            if ((s.Body ?? []).Any(HasYield)) Fail("AI3106", "Cannot yield a value in the body of a try block with a catch clause.", FirstYield(s.Body!.First(HasYield)).Location);
                            Fail("AI3106", "Cannot yield a value in the body of a catch clause.", FirstYield(clauses.First(HasYield)).Location);
                        }
                        var cleanup = clauses.FirstOrDefault(c => c.Kind == "finally");
                        var after = New(); var entry = New();
                        var inner = new IterRegion { Lo = entry, Parent = region, Finally = (cleanup?.Body ?? []).Select(x => M(x, false, false)).ToList() };
                        region.Children.Add(inner); region = inner;
                        Jump(entry); Open(entry); Gen(s.Body ?? []); Jump(after);
                        inner.Hi = nextState - 1; region = inner.Parent!;
                        Open(after); return;
                    }
                    case "switch": Fail("AI3100", "yield return inside a switch statement is not supported yet.", FirstYield(s).Location); return;
                    default: Fail("AI3100", "This statement cannot contain yield return yet.", FirstYield(s).Location); return;
                }
            }
            Open(New());
            Gen(body);
            if (!Ends(cur)) cur.Add(new ScalarStatement("return", at, Literal(false, at)));
            // Fields: hoisted variables, then the state, the current value and the 'suspended at a yield' flag (finally blocks do not run then).
            stateField = fields.Count; fields.Add(new ScalarStructField("state", ScalarType.Int, ScalarType.Int, true, false, at) { Owner = kE });
            currentField = fields.Count; fields.Add(new ScalarStructField("current", element, EraseType(element), true, false, at) { Owner = kE });
            suspField = fields.Count; fields.Add(new ScalarStructField("susp", ScalarType.Bool, ScalarType.Bool, true, false, at) { Owner = kE });
            ScalarExpression Fix(ScalarExpression e)
            {
                if (e.Operation == "field" && e.Value is "@state" or "@current" or "@susp")
                    return e with { Value = (e.Value switch { "@state" => stateField, "@current" => currentField, _ => suspField }).ToString(CultureInfo.InvariantCulture) };
                return e with { Left = e.Left is null ? null : Fix(e.Left), Right = e.Right is null ? null : Fix(e.Right), Arguments = e.Arguments?.Select(Fix).ToList() };
            }
            IterRegion RegionOf(int state)
            {
                var r = root;
                while (r.Children.FirstOrDefault(c => c.Contains(state)) is { } child) r = child;
                return r;
            }
            // A jump inside region R continues R's dispatch loop when the target is in R, and otherwise leaves it (running R's finally block).
            List<ScalarStatement> Finish(IEnumerable<ScalarStatement> list, IterRegion r) => list.SelectMany(x => FinishOne(x, r)).ToList();
            IEnumerable<ScalarStatement> FinishOne(ScalarStatement s, IterRegion r)
            {
                if (s.Kind == "__jump")
                {
                    var target = int.Parse(s.Name!, CultureInfo.InvariantCulture); var stays = r == root || r.Contains(target);
                    if (!stays && s.Mutable) Fail("AI3100", "continue inside a switch that leaves a try block with yield return is not supported yet.", s.Location);
                    return [new ScalarStatement("expression", s.Location, Assign(S(), Int(target))), new ScalarStatement(stays ? "continue" : "break", s.Location)];
                }
                return [s with
                {
                    Expression = s.Expression is null ? null : Fix(s.Expression), Increment = s.Increment is null ? null : Fix(s.Increment),
                    Initializer = s.Initializer is null ? null : FinishOne(s.Initializer, r).Single(),
                    Body = s.Body is null ? null : Finish(s.Body, r), Alternative = s.Alternative is null ? null : Finish(s.Alternative, r)
                }];
            }
            ScalarExpression InRange(IterRegion r) =>
                new("&&", ScalarType.Bool, at, Left: new(">=", ScalarType.Bool, at, Left: S(), Right: Int(r.Lo)) { Wraps = _wraps }, Right: new("<=", ScalarType.Bool, at, Left: S(), Right: Int(r.Hi)) { Wraps = _wraps }) { Wraps = _wraps };
            ScalarExpression Is(int state) => new("==", ScalarType.Bool, at, Left: S(), Right: Int(state)) { Wraps = _wraps };
            List<ScalarStatement> Dispatch(IterRegion r)
            {
                var output = new List<ScalarStatement>();
                foreach (var (state, block) in blocks.OrderBy(b => b.Key))
                    if (RegionOf(state) == r) output.Add(new ScalarStatement("if", at, Is(state), Body: Finish(block, r), Alternative: []));
                foreach (var child in r.Children)
                {
                    var guarded = new ScalarStatement("try", at, Body: [new ScalarStatement("while", at, Literal(true, at), Body: Dispatch(child))],
                        Alternative: [new ScalarStatement("finally", at, Body: [new ScalarStatement("if", at, new ScalarExpression("unary!", ScalarType.Bool, at, Left: Fix(SuspField())) { Wraps = _wraps },
                            Body: Finish(child.Finally, root), Alternative: [])])]);
                    List<ScalarStatement> next = r == root ? [new ScalarStatement("continue", at)]
                        : [new ScalarStatement("if", at, InRange(r), Body: [new ScalarStatement("continue", at)], Alternative: [new ScalarStatement("break", at)])];
                    output.Add(new ScalarStatement("if", at, InRange(child), Body: [guarded, .. next], Alternative: []));
                }
                output.Add(r == root ? new ScalarStatement("return", at, Literal(false, at)) : new ScalarStatement("break", at));
                return output;
            }
            List<ScalarStatement> moveNext =
            [
                new ScalarStatement("local", at, Fix(StateField()), dispatch, ScalarType.Int) { Mutable = true },
                new ScalarStatement("expression", at, Assign(Fix(StateField()), Int(-1))),
                new ScalarStatement("expression", at, Assign(Fix(SuspField()), Literal(false, at))),
                new ScalarStatement("while", at, Literal(true, at), Body: Dispatch(root)),
            ];
            List<ScalarStatement> DisposeChain(IterRegion r) => r.Children.Select(c => new ScalarStatement("if", at, InRange(c),
                Body: [new ScalarStatement("try", at, Body: DisposeChain(c), Alternative: [new ScalarStatement("finally", at, Body: Finish(c.Finally, root))])], Alternative: [])).ToList();
            List<ScalarStatement> dispose = root.Children.Count == 0 ? [new ScalarStatement("expression", at, Assign(Fix(StateField()), Int(-1)))] :
            [
                new ScalarStatement("local", at, Fix(StateField()), dispatch, ScalarType.Int) { Mutable = false },
                new ScalarStatement("expression", at, Assign(Fix(StateField()), Int(-1))),
                .. DisposeChain(root),
            ];
            ScalarStructMethod Override(ScalarStruct baseClass, string name, string className, int owner)
            {
                var m = baseClass.Methods.First(x => x.Name == name);
                return new(name, className + "." + name, true, false, m.Declaration) { IsOverride = true, Slot = m.Slot, Owner = owner, IsLifted = true };
            }
            _structs[kE] = new ScalarStruct(nameE, idE, fields, enumMembers.Select(n => Override(enumBase, n, nameE, kE)).ToList(), at, IsClass: true) { BaseIndex = enumIndex, IsSealed = true };
            Lift(new ScalarFunction(nameE + ".MoveNext", [self], ScalarType.Bool, [.. referenceSlots, .. moveNext], [TrueContract(at)], at));
            Lift(new ScalarFunction(nameE + ".get_Current", [self], element, [new ScalarStatement("return", at, Fix(CurrentField()))], [TrueContract(at)], at));
            Lift(new ScalarFunction(nameE + ".Dispose", [self], ScalarType.Unit, dispose, [TrueContract(at)], at));
            var arguments = parameters.Select(p => ForwardParameter(p, at)).ToList();
            ScalarExpression Make(ScalarType id, List<ScalarExpression> values) => values.Count == 0 ? new("new", id, at)
                : new("init", id, at, string.Join(",", Enumerable.Range(0, values.Count)), Left: new("new", id, at)) { Arguments = values };
            if (seqIndex < 0) return [new ScalarStatement("return", at, Upcast(Make(idE, arguments), _returnType))];
            var kS = _structs.Count; var idS = (ScalarType)(StructTypeBase + kS); var nameS = $"__iterseq_{kS}";
            var seqFields = parameters.Select((p, i) => new ScalarStructField($"a{i}", p.Type, EraseType(p.Type), true, false, at) { Owner = kS }).ToList();
            _structs.Add(new ScalarStruct(nameS, idS, seqFields, [Override(_structs[seqIndex], "GetEnumerator", nameS, kS)], at, IsClass: true) { BaseIndex = seqIndex, IsSealed = true });
            var me = new ScalarParameter(Fresh(), idS); var meRead = new ScalarExpression("local", idS, at, me.Name);
            var copies = parameters.Select((p, i) => new ScalarExpression("field", p.Type, at, i.ToString(CultureInfo.InvariantCulture), Left: meRead)).ToList();
            Lift(new ScalarFunction(nameS + ".GetEnumerator", [me], enumBase.Id, [new ScalarStatement("return", at, Upcast(Make(idE, copies), enumBase.Id))], [TrueContract(at)], at));
            return [new ScalarStatement("return", at, Upcast(Make(idS, arguments), _returnType))];
        }
        // foreach (var x in seq) { body } over IEnumerable<T> is, as in C#,
        // { let s = seq; let e = s.GetEnumerator(); try { while (e.MoveNext()) { let x = e.Current; body } } finally { if (e != null) e.Dispose() } }.
        private (ScalarStatement Statement, bool Returns) ForeachSequence(SourceLocation at, Lexeme name, Lexeme? typeToken, ScalarExpression source, ScalarType element, int enumIndex)
        {
            if (typeToken is not null && Type(typeToken.Text, typeToken.Location) != element)
                Fail("AI3100", $"The iteration variable has the element type '{Show(element)}'; foreach conversions are not supported.", typeToken.Location);
            var before = new HashSet<string>(_assigned);
            var enumType = _structs[enumIndex].Id;
            var sequence = HiddenLocal(source.Type); var readSequence = new ScalarExpression("local", source.Type, at, sequence.EmittedName);
            var enumerator = HiddenLocal(enumType); var readEnumerator = new ScalarExpression("local", enumType, at, enumerator.EmittedName);
            var getEnumerator = MethodCall(readSequence, new Lexeme("GetEnumerator", at), false, []);
            var moveNext = MethodCall(readEnumerator, new Lexeme("MoveNext", at), false, []);
            var current = MethodCall(readEnumerator, new Lexeme("get_Current", at), false, []);
            var dispose = MethodCall(readEnumerator, new Lexeme("Dispose", at), false, []);
            _scopes.Add(new(StringComparer.Ordinal));
            var item = Declare(name.Text, element, false, name.Location); _assigned.Add(item.EmittedName);
            var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop();
            foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
            _scopes.RemoveAt(_scopes.Count - 1);
            _assigned = new(before); _assigned.Add(sequence.EmittedName); _assigned.Add(enumerator.EmittedName);
            var itemStatement = new ScalarStatement("local", at, current, item.EmittedName, element) { Mutable = false };
            var loop = new ScalarStatement("while", at, moveNext, Body: [itemStatement, .. body.Body]);
            var isNull = new ScalarExpression("==", ScalarType.Bool, at, Left: readEnumerator, Right: new("null", enumType, at)) { Wraps = _wraps };
            var cleanup = new ScalarStatement("if", at, isNull, Body: [], Alternative: [new ScalarStatement("expression", at, dispose)]);
            var guarded = new ScalarStatement("try", at, Body: [loop], Alternative: [new ScalarStatement("finally", at, Body: [cleanup])]);
            return (new ScalarStatement("block", at, Body:
            [
                new ScalarStatement("local", at, source, sequence.EmittedName, source.Type) { Mutable = false },
                new ScalarStatement("local", at, getEnumerator, enumerator.EmittedName, enumType) { Mutable = false },
                guarded
            ]), false);
        }
        // ---- N22: async/await ----
        // A top-level 'async fn' returning Task or Task<T> is bound with T (or unit) as its return type, then rewritten into a sealed class
        // __async_k deriving from the hidden abstract class __Cont. Its Step function runs the body as a state machine over the hoisted variables
        // (as an iterator's MoveNext does); an 'await' of a pending task records the resume state, registers the machine as a continuation of
        // that task and returns. Run (the continuation entry) calls Step, captures an escaping exception as a code and completes the method's
        // task. Continuations run inline when a task completes (C# runs await continuations synchronously when no context is captured), so
        // the native program needs no scheduler. The hidden task classes and helpers are appended to the source by ExtractAsync.
        private ScalarType? _asyncTask; private ScalarType? _asyncElem;
        // The hidden task helpers and TaskCompletionSource classes read the task fields directly.
        private bool InTaskHelper => _function is not null && _function.Name.StartsWith("__task_", StringComparison.Ordinal) || _struct is not null && _struct.Name.StartsWith("__TCS_", StringComparison.Ordinal);
        private bool IsTaskType(ScalarType type) => activeAsync && Has(ScalarFeatures.Async) && IsClass(type) && StructOf(type).Name is var n && (n == "__Task" || n.StartsWith("__Task_", StringComparison.Ordinal)
            && StructNamed("__Task") is { } root && Derives(_structs, IndexOf(type), IndexOf(root.Id)));
        private ScalarType TaskElement(ScalarType type) => StructOf(type).Name == "__Task" ? ScalarType.Unit : StructOf(type).Fields.FirstOrDefault(f => f.Name == "result")?.Type ?? ScalarType.Unit;
        private ScalarType? AsyncTypeText(string text, SourceLocation at)
        {
            var t = text.Trim();
            if (t == "Task") return StructNamed("__Task")?.Id;
            var m = Regex.Match(t, @"^(Task|TaskCompletionSource)\s*<(.*)>$");
            if (!m.Success) return null;
            var argument = m.Groups[2].Value.Trim(); var depth = 0;
            if (argument.Length == 0) Fail("AI3116", $"'{m.Groups[1].Value}' expects one type argument.", at);
            foreach (var c in argument)
            {
                if (c == '<') depth++; else if (c == '>') depth--;
                else if (depth == 0 && c is ',' or ';') Fail("AI3116", $"'{m.Groups[1].Value}' expects one type argument.", at);
            }
            if (++_asyncTypeDepth > 64) Fail("AI3100", "Task type nesting exceeds 64 levels.", at);
            try { return ResolvedTaskType(Type(argument, at), m.Groups[1].Value == "TaskCompletionSource", at); }
            finally { _asyncTypeDepth--; }
        }
        private bool AwaitAt()
        {
            if (_asyncTask is not null) return true;
            var next = TokenAt(_position + 1).Text;
            return IsIdentifier(next) && !IsReserved(next) || next == "new";
        }
        private ScalarExpression AwaitExpression()
        {
            var keyword = Next(); var at = keyword.Location;
            if (_asyncTask is null) Fail("AI3106", "The 'await' operator can only be used within an async method.", at);
            if (_lambdaNest > 0) Fail("AI3100", "await inside a lambda or anonymous method is not supported yet.", at);
            if (_contract) Fail("AI3106", "The 'await' operator cannot be used in a contract.", at);
            var operand = Unary(); Read(operand);
            if (!IsTaskType(operand.Type)) Fail("AI3106", $"Cannot await '{Show(operand.Type)}'.", operand.Location);
            return new ScalarExpression("await", TaskElement(operand.Type), at, Left: operand);
        }
        // Task.FromResult(v) and Task.CompletedTask make completed tasks.
        private ScalarExpression TaskStatic()
        {
            var at = Next().Location; Expect("."); var member = Next();
            if (member.Text == "CompletedTask")
                return new ScalarExpression("call", StructNamed("__Task")!.Id, at, "__task_completed") { Arguments = [] };
            if (member.Text == "FromResult" && Current.Text == "(")
            {
                Next(); var value = Expression(); Read(value); Expect(")");
                var task = ResolvedTaskType(value.Type, false, value.Location);
                return new ScalarExpression("call", task, at, "__task_from_" + StructOf(task).Name[7..]) { Arguments = [Coerce(value, TaskElement(task))] };
            }
            Fail("AI3103", $"'Task' has no supported member '{member.Text}'.", member.Location);
            return null!;
        }
        // Members of Task and Task<T>: IsCompleted, IsCompletedSuccessfully, IsFaulted and GetAwaiter().GetResult().
        private ScalarExpression TaskMember(ScalarExpression task, Lexeme member)
        {
            var at = member.Location; var baseTask = StructNamed("__Task")!.Id; var up = Upcast(task, baseTask);
            ScalarExpression Status() => new("call", ScalarType.Int, at, "__task_status") { Arguments = [up] };
            ScalarExpression Compare(string op, int value) => new(op, ScalarType.Bool, at, Left: Status(), Right: new("literal", ScalarType.Int, at, value.ToString(CultureInfo.InvariantCulture))) { Wraps = _wraps };
            switch (member.Text)
            {
                case "IsCompleted": return Compare("!=", 0);
                case "IsCompletedSuccessfully": return Compare("==", 1);
                case "IsFaulted": return Compare("==", 2);
                case "GetAwaiter":
                {
                    Expect("("); Expect(")"); Expect("."); var get = Next();
                    if (get.Text != "GetResult") Fail("AI3103", $"The awaiter has no supported member '{get.Text}'.", get.Location);
                    Expect("("); Expect(")");
                    var element = TaskElement(task.Type);
                    return element == ScalarType.Unit ? new ScalarExpression("call", ScalarType.Unit, at, "__task_check") { Arguments = [up] }
                        : new ScalarExpression("call", element, at, "__task_get_" + StructOf(task.Type).Name[7..]) { Arguments = [task] };
                }
                case "Result" or "Wait": Fail("AI3100", $"Task.{member.Text} is not supported yet; use await or GetAwaiter().GetResult().", at); return null!;
                default: Fail("AI3103", $"'{Show(task.Type)}' has no supported member '{member.Text}'.", at); return null!;
            }
        }
        private static bool HasAwaitExpr(ScalarExpression? e) =>
            e is not null && (e.Operation == "await" || HasAwaitExpr(e.Left) || HasAwaitExpr(e.Right) || (e.Arguments ?? []).Any(HasAwaitExpr));
        private static bool HasAwait(ScalarStatement s) =>
            HasAwaitExpr(s.Expression) || HasAwaitExpr(s.Increment) || s.Initializer is { } i && HasAwait(i) || (s.Body ?? []).Any(HasAwait) || (s.Alternative ?? []).Any(HasAwait);
        private static ScalarExpression FirstAwait(ScalarExpression e) =>
            e.Operation == "await" ? e : new[] { e.Left, e.Right }.Concat(e.Arguments ?? []).Where(HasAwaitExpr).Select(x => FirstAwait(x!)).First();
        private static ScalarExpression FirstAwait(ScalarStatement s) =>
            new[] { s.Expression, s.Increment }.Where(HasAwaitExpr).Select(x => FirstAwait(x!)).Concat(
            new[] { s.Initializer }.Concat(s.Body ?? []).Concat(s.Alternative ?? []).Where(x => x is not null && HasAwait(x)).Select(x => FirstAwait(x!))).First();
        // The await of a statement value sits on its unary spine (return await t, x = await t, -await t, a conversion of await t).
        private static ScalarExpression? SpineAwait(ScalarExpression e)
        {
            for (var x = e; x is not null; x = x.Arguments is null && x.Right is null ? x.Left : null)
                if (x.Operation == "await") return HasAwaitExpr(x.Left) ? null : x;
            return null;
        }
        private static ScalarExpression ReplaceAwait(ScalarExpression e, ScalarExpression value) =>
            e.Operation == "await" ? value : e with { Left = ReplaceAwait(e.Left!, value) };
        private sealed class AsyncRegion
        {
            public int Lo, Hi = int.MaxValue, After; public AsyncRegion? Parent; public List<AsyncRegion> Children { get; } = [];
            public List<ScalarStatement> Finally { get; set; } = [];
            public List<ScalarStatement>? Catches { get; set; }
            public int Cleanup = -1, PendingTarget = -1, PendingCode = -1;
            public bool Contains(int state) => state >= Lo && state <= Hi;
        }
        private List<ScalarStatement> LowerAsync(List<ScalarParameter> parameters, IReadOnlyList<ScalarStatement> body)
        {
            body = PrepareReferenceSlots(body, out var referenceSlots);
            var at = _function.Location;
            var taskType = _asyncTask!.Value; var element = _asyncElem!.Value;
            bool AsyncFinally(ScalarStatement s) => s.Kind == "finally" && HasAwait(s) || s.Body?.Any(AsyncFinally) == true || s.Alternative?.Any(AsyncFinally) == true;
            var asyncFinally = body.Any(AsyncFinally);
            var contIndex = _structs.FindIndex(x => x.Name == "__Cont");
            if (contIndex < 0 || _checkOnly || !_structs[contIndex].Methods.Any(x => x.Name == "Run") || StructNamed("__Task") is null)
                return [new ScalarStatement("return", at, new ScalarExpression("null", taskType, at))];
            var baseTask = StructNamed("__Task")!.Id; var contType = _structs[contIndex].Id;
            var kA = _structs.Count; var idA = (ScalarType)(StructTypeBase + kA); var nameA = $"__async_{kA}";
            _structs.Add(new ScalarStruct(nameA, idA, [], [], at, IsClass: true) { BaseIndex = contIndex, IsSealed = true });
            var fields = new List<ScalarStructField>();
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            int Hoist(string name, ScalarType type)
            {
                if (map.TryGetValue(name, out var known)) return known;
                map[name] = fields.Count; fields.Add(new ScalarStructField($"v{fields.Count}", type, EraseType(type), true, false, at) { Owner = kA });
                return fields.Count - 1;
            }
            foreach (var p in parameters) Hoist(p.Name, p.Type);
            var self = new ScalarParameter(Fresh(), idA); var selfRead = new ScalarExpression("local", idA, at, self.Name);
            ScalarExpression Field(int index, ScalarType type, SourceLocation where) => new("field", type, where, index.ToString(CultureInfo.InvariantCulture), Left: selfRead);
            ScalarExpression Assign(ScalarExpression target, ScalarExpression value) => new("=", target.Type, value.Location, Left: target, Right: value) { Wraps = _wraps };
            ScalarExpression Int(int value) => new("literal", ScalarType.Int, at, value.ToString(CultureInfo.InvariantCulture));
            ScalarExpression Call(string name, ScalarType type, params ScalarExpression[] arguments) => new("call", type, at, name) { Arguments = arguments };
            ScalarStatement Do(ScalarExpression e) => new("expression", e.Location, e);
            // Move suspending catch bodies outside the native catch, preserving the caught
            // exception's code for a later rethrow. A surrounding finally still encloses both.
            List<ScalarStatement> Normalize(IEnumerable<ScalarStatement> statements, ScalarExpression? rethrow) => statements.SelectMany(s => NormalizeOne(s, rethrow)).ToList();
            IEnumerable<ScalarStatement> NormalizeOne(ScalarStatement s, ScalarExpression? rethrow)
            {
                if (s.Kind == "throw" && s.Name is null && rethrow is not null)
                    return [Do(Call("__task_throw", ScalarType.Unit, rethrow)), new ScalarStatement("throw", s.Location, Name: AsyncRuntimeName("Exception"))];
                if (s.Kind == "try")
                {
                    var catches = (s.Alternative ?? []).Where(c => c.Kind == "catch").ToList();
                    var cleanup = s.Alternative?.FirstOrDefault(c => c.Kind == "finally");
                    if (catches.Count > 0 && cleanup is not null)
                        return NormalizeOne(s with { Body = [s with { Alternative = catches }], Alternative = [cleanup] }, rethrow);
                    if (catches.Any(HasAwait))
                    {
                        var tagName = Fresh(); var codeName = Fresh();
                        var tag = new ScalarExpression("local", ScalarType.Int, s.Location, tagName);
                        var code = new ScalarExpression("local", ScalarType.Int, s.Location, codeName);
                        var captures = new List<ScalarStatement>(); var handled = new HashSet<string>(StringComparer.Ordinal);
                        var handlers = new List<ScalarStatement>();
                        for (var h = 0; h < catches.Count; h++)
                        {
                            var clause = catches[h];
                            for (var i = 0; i < AsyncFaultNames.Length; i++)
                            {
                                var name = AsyncRuntimeName(AsyncFaultNames[i]);
                                if ((clause.Name == "*" || clause.Name!.Split(',').Contains(name)) && handled.Add(name))
                                    captures.Add(new ScalarStatement("catch", clause.Location, Name: name, Body: [Do(Assign(tag, Int(h + 1))), Do(Assign(code, Int(i + 1)))]));
                            }
                            if (clause.Name == "*") captures.Add(new ScalarStatement("catch", clause.Location, Name: "*", Body: [Do(Assign(tag, Int(h + 1))), Do(Assign(code, Int(1)))]));
                            handlers.Add(new ScalarStatement("if", clause.Location, new ScalarExpression("==", ScalarType.Bool, clause.Location, Left: tag, Right: Int(h + 1)) { Wraps = _wraps }, Body: Normalize(clause.Body ?? [], code), Alternative: []));
                        }
                        return [new ScalarStatement("local", s.Location, Int(0), tagName, ScalarType.Int) { Mutable = true },
                            new ScalarStatement("local", s.Location, Int(0), codeName, ScalarType.Int) { Mutable = true },
                            s with { Body = Normalize(s.Body ?? [], rethrow), Alternative = captures }, .. handlers];
                    }
                    return [s with { Body = Normalize(s.Body ?? [], rethrow), Alternative = s.Alternative?.Select(c => c with { Body = Normalize(c.Body ?? [], null) }).ToList() }];
                }
                return [s with { Body = s.Body is null ? null : Normalize(s.Body, rethrow), Alternative = s.Alternative is null ? null : Normalize(s.Alternative, rethrow) }];
            }
            int stateField = -1, suspField = -1, taskField = -1, resultField = -1;
            ScalarExpression StateField() => new("field", ScalarType.Int, at, "@state", Left: selfRead);
            ScalarExpression SuspField() => new("field", ScalarType.Bool, at, "@susp", Left: selfRead);
            ScalarExpression ResultField() => new("field", element, at, "@result", Left: selfRead);
            ScalarExpression E(ScalarExpression e)
            {
                if (e.Operation == "cellref") Fail("AI3100", "Lambdas inside async methods are not supported yet.", e.Location);
                if (e.Operation == "await") Fail("AI3100", "await is supported only as a statement, a local initializer, the right side of an assignment to a local, or a return value.", e.Location);
                if (e.Operation == "local" && e.Value is { } n && map.TryGetValue(n, out var index)) return Field(index, e.Type, e.Location);
                return e with { Left = e.Left is null ? null : E(e.Left), Right = e.Right is null ? null : E(e.Right), Arguments = e.Arguments?.Select(E).ToList() };
            }
            var dispatch = Fresh(); ScalarExpression S() => new("local", ScalarType.Int, at, dispatch);
            var blocks = new Dictionary<int, List<ScalarStatement>>(); var nextState = 0; var completionState = -1;
            var root = new AsyncRegion { Lo = 0 }; var region = root;
            var loopTargets = new Stack<(int Break, int Continue)>();
            List<ScalarStatement> cur = [];
            int New() => nextState++;
            void Open(int state) { cur = []; blocks[state] = cur; }
            ScalarStatement JumpTo(int target, SourceLocation where, bool inSwitch) => new("__jump", where, Name: target.ToString(CultureInfo.InvariantCulture)) { Mutable = inSwitch };
            static bool Ends(IReadOnlyList<ScalarStatement>? list) => list is { Count: > 0 } && (list[^1].Kind is "return" or "__jump" or "__complete" or "__resume" or "continue" or "break" or "throw"
                || list[^1].Kind == "if" && Ends(list[^1].Body) && Ends(list[^1].Alternative) || list[^1].Kind == "block" && Ends(list[^1].Body)
                || list[^1].Kind == "try" && (list[^1].Alternative?.Any(c => c.Kind == "finally" && Ends(c.Body)) == true
                    || Ends(list[^1].Body) && (list[^1].Alternative ?? []).Where(c => c.Kind == "catch").All(c => Ends(c.Body))));
            void Jump(int target) { if (!Ends(cur)) cur.Add(JumpTo(target, at, false)); }
            void Branch(ScalarExpression condition, int yes, int no, SourceLocation where)
            {
                if (HasAwaitExpr(condition)) { InitValue(condition, value => Branch(value!, yes, no, where)); return; }
                if (condition.Operation == "literal" && condition.Type == ScalarType.Bool) cur.Add(JumpTo(condition.Value == "true" ? yes : no, where, false));
                else cur.Add(new ScalarStatement("if", where, E(condition), Body: [JumpTo(yes, where, false)], Alternative: [JumpTo(no, where, false)]));
            }
            ScalarStatement Finish0(ScalarExpression? value, SourceLocation where) => asyncFinally ? new ScalarStatement("__complete", where, value) : value is null
                ? new ScalarStatement("return", where, Literal(true, where))
                : new ScalarStatement("block", where, Body: [Do(Assign(ResultField(), value)), new ScalarStatement("return", where, Literal(true, where))]);
            ScalarStatement M(ScalarStatement s, bool inLoop, bool inSwitch)
            {
                switch (s.Kind)
                {
                    case "break" when !inLoop && !inSwitch: return JumpTo(loopTargets.Peek().Break, s.Location, false);
                    case "continue" when !inLoop: return JumpTo(loopTargets.Peek().Continue, s.Location, inSwitch);
                    case "return": return Finish0(s.Expression is null ? null : E(s.Expression), s.Location);
                }
                var loop = s.Kind is "while" or "dowhile" or "for"; var sw = s.Kind == "switch";
                return s with
                {
                    Expression = s.Expression is null ? null : E(s.Expression), Increment = s.Increment is null ? null : E(s.Increment),
                    Initializer = s.Initializer is null ? null : M(s.Initializer, inLoop, inSwitch),
                    Body = s.Body?.Select(x => M(x, inLoop || loop, inSwitch && !loop || sw)).ToList(),
                    Alternative = s.Alternative?.Select(x => M(x, inLoop || loop, inSwitch && !loop || sw)).ToList()
                };
            }
            // await t: store t; if it is pending, suspend (resume at k once t completes); at k rethrow t's exception or use its result.
            void AwaitSite(ScalarExpression site, Action<ScalarExpression?> use)
            {
                var operand = E(site.Left!); var slot = Hoist(Fresh(), operand.Type);
                var stored = Field(slot, operand.Type, site.Location); var asTask = Upcast(stored, baseTask);
                cur.Add(Do(Assign(stored, operand)));
                var resume = New();
                cur.Add(new ScalarStatement("if", site.Location, Call("__task_pending", ScalarType.Bool, asTask), Body:
                [
                    Do(Assign(StateField(), Int(resume))), Do(Assign(SuspField(), Literal(true, at))),
                    Do(Call("__task_on", ScalarType.Unit, asTask, Upcast(selfRead, contType))),
                    new ScalarStatement("return", at, Literal(false, at)),
                ], Alternative: []));
                Jump(resume); Open(resume);
                cur.Add(Do(Call("__task_check", ScalarType.Unit, asTask)));
                use(site.Type == ScalarType.Unit ? null : Call("__task_get_" + StructOf(operand.Type).Name[7..], site.Type, stored));
            }
            bool InitializerAwait(ScalarExpression e) => (e.Operation is "accessor-mutation" or "init-record" or "init-members" or "array-new" or "array-store" or "array-index" or "static-event-update" || e.Operation == "call" && e.Arguments?.Any(a => ReferenceArgument(a) || a.Operation == "argument-temporary") == true) && HasAwaitExpr(e)
                || e.Left is not null && InitializerAwait(e.Left) || e.Right is not null && InitializerAwait(e.Right)
                || e.Arguments?.Any(InitializerAwait) == true;
            bool ComplexAwait(ScalarExpression e) => InitializerAwait(e) || HasAwaitExpr(e) && SpineAwait(e) is null;
            ScalarExpression Snapshot(ScalarExpression value)
            {
                var stored = Field(Hoist(Fresh(), value.Type), value.Type, value.Location);
                cur.Add(Do(Assign(stored, value))); return stored;
            }
            void Consume(ScalarExpression? value)
            {
                // Assignment/switch lowering already materializes its value in this machine.
                if (value is null || value.Operation == "field" && ReferenceEquals(value.Left, selfRead)) return;
                cur.Add(Do(value));
            }
            // Capture an object receiver, not the value of the field being assigned. Struct paths
            // remain locations in their local/static storage across a suspension.
            void Assignment(ScalarExpression e, Action<ScalarExpression?> use)
            {
                var target = e.Left!;
                ScalarExpression? receiver = null;
                for (var node = target; node.Operation == "field"; node = node.Left!)
                    if (IsClass(node.Left!.Type)) { receiver = node.Left; break; }
                void Store(ScalarExpression destination, ScalarExpression right)
                {
                    InitValue(right, value =>
                    {
                        var stored = Snapshot(value!);
                        cur.Add(Do(e with { Left = destination, Right = stored, Value = null }));
                        use(stored);
                    });
                }
                if (receiver is not null)
                {
                    InitValue(receiver, value =>
                    {
                        var saved = Snapshot(value!);
                        ScalarExpression Replace(ScalarExpression node) => ReferenceEquals(node, receiver) ? saved : node with
                        { Left = node.Left is null ? null : Replace(node.Left), Right = node.Right is null ? null : Replace(node.Right), Arguments = node.Arguments?.Select(Replace).ToList() };
                        Store(E(Replace(target)), Replace(e.Right!));
                    });
                    return;
                }
                Store(E(target), e.Right!);
            }
            // Keep a recipe for a variable location across suspension, never a by-reference pointer in the state object.
            void ReferenceAcrossAwait(ScalarExpression argument, bool spillsAcrossAwait, Action<ScalarExpression> use)
            {
                var target = argument.Left!;
                if (argument.Operation is "argument-temporary" or "argument-receiver-copy")
                {
                    InitValue(target, value => use(argument with { Left = Snapshot(value!) })); return;
                }
                void Finish(ScalarExpression storage)
                {
                    // Roslyn spills field receivers, delaying the final field address until invocation.
                    // Array locations still perform their null/bounds checks before later awaits.
                    if (!spillsAcrossAwait || target.Operation == "array-index")
                        cur.Add(Do(new ScalarExpression("reference-probe", ScalarType.Unit, argument.Location, argument.Operation, Left: storage)));
                    use(argument with { Left = storage });
                }
                if (target.Operation == "array-index")
                {
                    InitValue(target.Left!, array =>
                    {
                        var savedArray = Snapshot(array!);
                        InitValue(target.Right!, index => Finish(target with { Left = savedArray, Right = Snapshot(index!) }));
                    }); return;
                }
                ScalarExpression? receiver = null;
                for (var node = target; node.Operation == "field"; node = node.Left!)
                    if (IsClass(node.Left!.Type)) { receiver = node.Left; break; }
                if (receiver is null)
                {
                    var root = RootOf(target);
                    if (spillsAcrossAwait && (root.Operation is "ref-select" or "ref-reassign" || root.Operation == "call" && root.ReferenceMode != ScalarParameterMode.Value))
                        Fail("AI3106", "This reference expression cannot be preserved across a later await.", argument.Location);
                    Finish(E(target)); return;
                }
                InitValue(receiver, value =>
                {
                    var saved = ReferenceEquals(value, selfRead) ? value! : Snapshot(value!);
                    ScalarExpression Replace(ScalarExpression node) => ReferenceEquals(node, receiver) ? saved : node with
                    { Left = node.Left is null ? null : Replace(node.Left), Right = node.Right is null ? null : Replace(node.Right), Arguments = node.Arguments?.Select(Replace).ToList() };
                    Finish(E(Replace(target)));
                });
            }
            // Values and receivers evaluated before an await are hoisted before control can suspend.
            void InitValue(ScalarExpression e, Action<ScalarExpression?> use)
            {
                if (!HasAwaitExpr(e)) { use(E(e)); return; }
                if (e.Operation == "await")
                {
                    InitValue(e.Left!, operand => AwaitSite(e with { Left = operand }, use)); return;
                }
                if (e.Operation == "accessor-mutation")
                {
                    var call = e.Left!; var arguments = new List<ScalarExpression>();
                    for (var i = 0; i < call.Arguments!.Count; i++)
                    {
                        var argument = call.Arguments[i];
                        if (argument.Operation == "accessor-box")
                        {
                            ReferenceAcrossAwait(argument.Left!, call.Arguments.Skip(i + 1).Any(HasAwaitExpr) || HasAwaitExpr(e.Right!),
                                value => arguments.Add(argument with { Left = value }));
                            continue;
                        }
                        if (ReferenceArgument(argument) || argument.Operation is "argument-temporary" or "argument-receiver-copy")
                            ReferenceAcrossAwait(argument, call.Arguments.Skip(i + 1).Any(HasAwaitExpr) || HasAwaitExpr(e.Right!), value => arguments.Add(value));
                        else InitValue(argument, value => arguments.Add(Snapshot(value!)));
                    }
                    ScalarExpression? old = null;
                    var getterArguments = arguments.ToArray();
                    if (e.Value != "store") old = Snapshot(E(call with { Arguments = getterArguments }));
                    ScalarExpression ReplaceOld(ScalarExpression node)
                    {
                        if (node.Operation == "reference-read") return old!;
                        if (node.Operation is "accessor-mutation" or "reference-update") return node;
                        return node with { Left = node.Left is null ? null : ReplaceOld(node.Left), Right = node.Right is null ? null : ReplaceOld(node.Right), Arguments = node.Arguments?.Select(ReplaceOld).ToList() };
                    }
                    InitValue(ReplaceOld(e.Right!), value =>
                    {
                        var stored = Snapshot(value!);
                        // A constrained setter reads its saved location at invocation, after awaited operands.
                        arguments = arguments.Select(argument => argument.Operation == "accessor-box"
                            ? BoxValue(argument.Left!.Left!, argument.Type, receiver: true) : argument).ToList();
                        cur.Add(Do(new("call", ScalarType.Unit, e.Location, e.Value == "store" ? call.Value : e.AccessorSetter) { Arguments = [.. arguments, stored] }));
                        use(e.Value == "post" ? old : stored);
                    }); return;
                }
                if (e.Operation == "static-event-update")
                {
                    // The event identity is storage, not an operand to read before suspension.
                    InitValue(e.Right!, handler => use(e with { Right = handler })); return;
                }
                if (e.Operation == "init-record")
                {
                    var arguments = new List<ScalarExpression>();
                    foreach (var argument in e.Arguments!)
                        InitValue(argument, value =>
                        {
                            var slot = Hoist(Fresh(), value!.Type); var stored = Field(slot, value.Type, value.Location);
                            cur.Add(Do(Assign(stored, value))); arguments.Add(stored);
                        });
                    use(e with { Arguments = arguments }); return;
                }
                if (e.Operation == "array-new")
                {
                    var slot = Hoist(Fresh(), e.Type); var receiver = Field(slot, e.Type, e.Location);
                    InitValue(e.Left!, count =>
                    {
                        cur.Add(Do(Assign(receiver, e with { Left = count, Arguments = [] })));
                        for (var i = 0; i < e.Arguments!.Count; i++)
                        {
                            var index = Int(i);
                            InitValue(e.Arguments[i], value => cur.Add(Do(new("array-store", ArrayElement(e.Type), e.Location, "=", Left: receiver, Right: index) { Arguments = [value!] })));
                        }
                        use(receiver);
                    }); return;
                }
                if (e.Operation == "array-store")
                {
                    InitValue(e.Left!, origin =>
                    {
                        var receiver = Field(Hoist(Fresh(), origin!.Type), origin.Type, e.Location); cur.Add(Do(Assign(receiver, origin)));
                        InitValue(e.Right!, indexValue =>
                        {
                            var index = Field(Hoist(Fresh(), ScalarType.Int), ScalarType.Int, e.Location); cur.Add(Do(Assign(index, indexValue!)));
                            ScalarExpression? previous = null;
                            if (e.Value == "+=")
                            {
                                previous = Field(Hoist(Fresh(), ScalarType.String), ScalarType.String, e.Location);
                                cur.Add(Do(Assign(previous, new("array-index", ScalarType.String, e.Location, Left: receiver, Right: index))));
                            }
                            InitValue(e.Arguments![0], value => use(new("array-store", ArrayElement(e.Left!.Type), e.Location, "=", Left: receiver, Right: index)
                            { Arguments = [previous is null ? value! : new("+", ScalarType.String, e.Location, Left: previous, Right: value)] }));
                        });
                    }); return;
                }
                if (e.Operation == "init-members")
                {
                    var slot = Hoist(e.Value!, e.Type); var receiver = Field(slot, e.Type, e.Location);
                    InitValue(e.Left!, origin =>
                    {
                        cur.Add(Do(Assign(receiver, origin!)));
                        foreach (var action in e.Arguments!)
                            InitValue(action, value => { if (value is not null) { if (action.Operation == "init") cur.Add(Do(Assign(receiver, value))); else Consume(value); } });
                        use(receiver);
                    }); return;
                }
                if (e.Operation is "&&" or "||")
                {
                    var slot = Hoist(Fresh(), ScalarType.Bool); var result = Field(slot, ScalarType.Bool, e.Location);
                    InitValue(e.Left!, left =>
                    {
                        var next = New(); var shorted = New(); var done = New();
                        Branch(left!, e.Operation == "&&" ? next : shorted, e.Operation == "&&" ? shorted : next, e.Location);
                        Open(shorted); cur.Add(Do(Assign(result, Literal(e.Operation == "||", e.Location)))); Jump(done);
                        Open(next); InitValue(e.Right!, right => { cur.Add(Do(Assign(result, right!))); Jump(done); });
                        Open(done); use(result);
                    }); return;
                }
                if (e.Operation == "=") { Assignment(e, use); return; }
                if (e.Operation == "switch")
                {
                    InitValue(e.Left!, subject =>
                    {
                        var input = Field(Hoist(e.Value!, e.Left!.Type), e.Left.Type, e.Location);
                        cur.Add(Do(Assign(input, subject!)));
                        var result = Field(Hoist(Fresh(), e.Type), e.Type, e.Location); var done = New();
                        for (var i = 0; i < e.Arguments!.Count; i += 2)
                        {
                            var arm = e.Arguments[i + 1]; var hit = New(); var next = New();
                            InitValue(e.Arguments[i], test => Branch(test!, hit, next, e.Location));
                            Open(hit); InitValue(arm, value => cur.Add(Do(Assign(result, value!)))); Jump(done);
                            Open(next);
                        }
                        cur.Add(new ScalarStatement("throw", e.Location, Name: AsyncRuntimeName("SwitchExpressionException")));
                        Open(done); use(result);
                    }); return;
                }
                if (e.Operation == "bindpat")
                {
                    // The left operand is binding storage, never an evaluated value to snapshot.
                    InitValue(e.Right!, value => use(E(e with { Right = value }))); return;
                }
                var children = new List<ScalarExpression>();
                if (e.Left is not null) children.Add(e.Left);
                if (e.Right is not null) children.Add(e.Right);
                if (e.Arguments is not null) children.AddRange(e.Arguments);
                var evaluated = new List<ScalarExpression>();
                for (var childIndex = 0; childIndex < children.Count; childIndex++)
                {
                    var child = children[childIndex];
                    // Only implicit constrained receivers retain a location. Explicit box conversions and
                    // ordinary value arguments keep their before-await snapshots.
                    var boxed = child;
                    while ((boxed.Operation is "upcast" or "toobject") && boxed.Left is not null) boxed = boxed.Left;
                    if (e.Operation == "call" && e.Value?.EndsWith(".virtual", StringComparison.Ordinal) == true
                        && childIndex == 0 && boxed.Operation == "receiver-box")
                    {
                        var operand = boxed.Left!;
                        var capture = new ScalarExpression(IsReferenceStorage(operand) && WritableAccessorReceiver(operand) ? "argument-in" : "argument-temporary",
                            operand.Type, operand.Location, Left: operand);
                        ReferenceAcrossAwait(capture, children.Skip(childIndex + 1).Any(HasAwaitExpr), value =>
                        {
                            ScalarExpression ReplaceReceiver(ScalarExpression node) => ReferenceEquals(node, boxed)
                                ? node with { Left = value.Left }
                                : node with { Left = ReplaceReceiver(node.Left!) };
                            evaluated.Add(ReplaceReceiver(child));
                        });
                        continue;
                    }
                    if (ReferenceArgument(child) || child.Operation == "argument-temporary")
                    { ReferenceAcrossAwait(child, children.Skip(childIndex + 1).Any(HasAwaitExpr), value => evaluated.Add(value)); continue; }
                    InitValue(child, value =>
                    {
                        if (value is null || value.Type == ScalarType.Unit) Fail("AI3100", "An initializer operand cannot be void.", child.Location);
                        var slot = Hoist(Fresh(), value!.Type); var stored = Field(slot, value.Type, value.Location);
                        cur.Add(Do(Assign(stored, value))); evaluated.Add(stored);
                    });
                }
                var position = 0;
                use(e with { Left = e.Left is null ? null : evaluated[position++], Right = e.Right is null ? null : evaluated[position++], Arguments = e.Arguments is null ? null : evaluated.Skip(position).ToList() });
            }
            void Gen(IEnumerable<ScalarStatement> list) { foreach (var s in list) One(s); }
            bool ContainsSwitch(ScalarStatement s) => s.Kind == "switch" || s.Body?.Any(ContainsSwitch) == true || s.Alternative?.Any(ContainsSwitch) == true;
            void One(ScalarStatement s)
            {
                if (s.Kind == "local" && s.Name is not null)
                {
                    var index = Hoist(s.Name, s.Type);
                    if (s.Expression is not { } value) return;
                    if (ComplexAwait(value)) InitValue(value, v => cur.Add(Do(Assign(Field(index, s.Type, s.Location), v!))));
                    else if (SpineAwait(value) is { } aw) AwaitSite(aw, v => cur.Add(Do(Assign(Field(index, s.Type, s.Location), E(ReplaceAwait(value, v!))))));
                    else cur.Add(Do(Assign(Field(index, s.Type, s.Location), E(value))));
                    return;
                }
                if (s.Kind == "switch")
                {
                    var exit = New(); var next = loopTargets.Count == 0 ? -1 : loopTargets.Peek().Continue;
                    loopTargets.Push((exit, next)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(exit); Open(exit); return;
                }
                if (!HasAwait(s) && !ContainsSwitch(s)) { cur.Add(M(s, false, false)); return; }
                switch (s.Kind)
                {
                    case "expression" when s.Expression is { Operation: "=", Left: { Operation: "local" } target, Right: { } right } && ComplexAwait(right):
                        InitValue(right, v => cur.Add(Do(Assign(E(target), v!)))); return;
                    case "return" when s.Expression is { } x && ComplexAwait(x):
                        InitValue(x, v => cur.Add(Finish0(v, s.Location))); return;
                    case "expression" when s.Expression is { } x && SpineAwait(x) is { } aw:
                        AwaitSite(aw, v => { if (v is not null) cur.Add(Do(E(ReplaceAwait(x, v)))); });
                        return;
                    case "expression" when s.Expression is { Operation: "=", Left: { Operation: "local" } target, Right: { } right } && SpineAwait(right) is { } aw:
                        AwaitSite(aw, v => cur.Add(Do(Assign(E(target), E(ReplaceAwait(right, v!))))));
                        return;
                    case "expression" when s.Expression is { } x:
                        InitValue(x, Consume); return;
                    case "return" when s.Expression is { } x && SpineAwait(x) is { } aw:
                        AwaitSite(aw, v => cur.Add(Finish0(v is null ? null : E(ReplaceAwait(x, v)), s.Location)));
                        return;
                    case "block": Gen(s.Body ?? []); return;
                    case "if":
                    {
                        int yes = New(), no = New(), join = New();
                        Branch(s.Expression!, yes, no, s.Location);
                        Open(yes); Gen(s.Body ?? []); Jump(join);
                        Open(no); Gen(s.Alternative ?? []); Jump(join);
                        Open(join); return;
                    }
                    case "while":
                    {
                        int head = New(), loopBody = New(), exit = New();
                        Jump(head); Open(head);
                        Branch(s.Expression!, loopBody, exit, s.Location);
                        Open(loopBody); loopTargets.Push((exit, head)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(head);
                        Open(exit); return;
                    }
                    case "dowhile":
                    {
                        int loopBody = New(), test = New(), exit = New();
                        Jump(loopBody); Open(loopBody); loopTargets.Push((exit, test)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(test);
                        Open(test);
                        Branch(s.Expression!, loopBody, exit, s.Location);
                        Open(exit); return;
                    }
                    case "for":
                    {
                        if (s.Initializer is { } init) One(init);
                        int head = New(), loopBody = New(), step = New(), exit = New();
                        Jump(head); Open(head);
                        if (s.Expression is { } condition) Branch(condition, loopBody, exit, s.Location);
                        else Jump(loopBody);
                        Open(loopBody); loopTargets.Push((exit, step)); Gen(s.Body ?? []); loopTargets.Pop(); Jump(step);
                        Open(step);
                        if (s.Increment is { } increment)
                        {
                            if (HasAwaitExpr(increment)) InitValue(increment, Consume);
                            else cur.Add(Do(E(increment)));
                        }
                        Jump(head);
                        Open(exit); return;
                    }
                    case "try":
                    {
                        var clauses = s.Alternative ?? [];
                        var catches = clauses.Where(c => c.Kind == "catch").ToList(); var cleanup = clauses.FirstOrDefault(c => c.Kind == "finally");
                        // try { B } catch { H } finally { F } is try { try { B } catch { H } } finally { F }.
                        if (catches.Count > 0 && cleanup is not null) { One(s with { Body = [s with { Alternative = catches }], Alternative = [cleanup] }); return; }
                        if (cleanup is not null && HasAwait(cleanup))
                        {
                            if (completionState < 0) completionState = New();
                            var afterCleanup = New(); var protectedEntry = New();
                            var protectedRegion = new AsyncRegion { Lo = protectedEntry, After = afterCleanup, Parent = region,
                                PendingTarget = Hoist(Fresh(), ScalarType.Int), PendingCode = Hoist(Fresh(), ScalarType.Int) };
                            region.Children.Add(protectedRegion); region = protectedRegion;
                            Jump(protectedEntry); Open(protectedEntry); Gen(s.Body ?? []); Jump(afterCleanup);
                            protectedRegion.Hi = nextState - 1; region = protectedRegion.Parent!;
                            protectedRegion.Cleanup = New(); Open(protectedRegion.Cleanup); Gen(cleanup.Body ?? []);
                            if (!Ends(cur))
                            {
                                var code = Field(protectedRegion.PendingCode, ScalarType.Int, s.Location);
                                cur.Add(new ScalarStatement("if", s.Location, new ScalarExpression("!=", ScalarType.Bool, s.Location, Left: code, Right: Int(0)) { Wraps = _wraps },
                                    Body: [Do(Call("__task_throw", ScalarType.Unit, code)), new ScalarStatement("throw", s.Location, Name: AsyncRuntimeName("Exception"))], Alternative: []));
                                cur.Add(new ScalarStatement("__resume", s.Location, Field(protectedRegion.PendingTarget, ScalarType.Int, s.Location)));
                            }
                            Open(afterCleanup); return;
                        }
                        var after = New(); var entry = New();
                        var inner = new AsyncRegion { Lo = entry, After = after, Parent = region,
                            Finally = (cleanup?.Body ?? []).Select(x => M(x, false, false)).ToList(),
                            Catches = catches.Count == 0 ? null : catches.Select(c => c with { Body = (c.Body ?? []).Select(x => M(x, false, false)).ToList() }).ToList() };
                        region.Children.Add(inner); region = inner;
                        Jump(entry); Open(entry); Gen(s.Body ?? []); Jump(after);
                        inner.Hi = nextState - 1; region = inner.Parent!;
                        Open(after); return;
                    }
                    default: E(FirstAwait(s)); return;
                }
            }
            bool AwaitHandler(ScalarStatement s) => s.Kind is "catch" or "finally" && HasAwait(s) || s.Body?.Any(AwaitHandler) == true || s.Alternative?.Any(AwaitHandler) == true;
            if (body.Any(AwaitHandler)) body = Normalize(body, null);
            Open(New());
            Gen(body);
            if (!Ends(cur)) cur.Add(new ScalarStatement("return", at, Literal(true, at)));
            if (completionState >= 0) { Open(completionState); cur.Add(new ScalarStatement("return", at, Literal(true, at))); }
            stateField = fields.Count; fields.Add(new ScalarStructField("state", ScalarType.Int, ScalarType.Int, true, false, at) { Owner = kA });
            suspField = fields.Count; fields.Add(new ScalarStructField("susp", ScalarType.Bool, ScalarType.Bool, true, false, at) { Owner = kA });
            taskField = fields.Count; fields.Add(new ScalarStructField("task", taskType, EraseType(taskType), true, false, at) { Owner = kA });
            if (element != ScalarType.Unit) { resultField = fields.Count; fields.Add(new ScalarStructField("result", element, EraseType(element), true, false, at) { Owner = kA }); }
            ScalarExpression Fix(ScalarExpression e)
            {
                if (e.Operation == "field" && e.Value is "@state" or "@susp" or "@result")
                    return e with { Value = (e.Value switch { "@state" => stateField, "@susp" => suspField, _ => resultField }).ToString(CultureInfo.InvariantCulture) };
                return e with { Left = e.Left is null ? null : Fix(e.Left), Right = e.Right is null ? null : Fix(e.Right), Arguments = e.Arguments?.Select(Fix).ToList() };
            }
            AsyncRegion RegionOf(int state)
            {
                var r = root;
                while (r.Children.FirstOrDefault(c => c.Contains(state)) is { } child) r = child;
                return r;
            }
            List<ScalarStatement> Finish(IEnumerable<ScalarStatement> list, AsyncRegion r) => list.SelectMany(x => FinishOne(x, r)).ToList();
            static bool HasCleanup(AsyncRegion? region)
            {
                for (var frame = region; frame is not null; frame = frame.Parent) if (frame.Cleanup >= 0) return true;
                return false;
            }
            List<ScalarStatement> Transfer(ScalarExpression target, int? constant, AsyncRegion r, SourceLocation where, bool inSwitch = false)
            {
                List<ScalarStatement> Direct(ScalarExpression destination, int? known)
                {
                    var output = new List<ScalarStatement> { Do(Assign(S(), destination)) };
                    if (known is { } state) output.Add(new ScalarStatement(r == root || r.Contains(state) ? "continue" : "break", where));
                    else if (r == root) output.Add(new ScalarStatement("continue", where));
                    else output.Add(new ScalarStatement("if", where, InRange(r), Body: [new ScalarStatement("continue", where)], Alternative: [new ScalarStatement("break", where)]));
                    return output;
                }
                List<ScalarStatement> Route(AsyncRegion? frame)
                {
                    while (frame is not null && (frame.Cleanup < 0 || constant is { } value && frame.Contains(value))) frame = frame.Parent;
                    if (frame is null)
                    {
                        if (constant is { } value && r != root && !r.Contains(value) && inSwitch) Fail("AI3100", "continue inside a switch that leaves a try block with await is not supported yet.", where);
                        return Direct(target, constant);
                    }
                    List<ScalarStatement> cleanup = [Do(Assign(Field(frame.PendingTarget, ScalarType.Int, where), target)), Do(Assign(Field(frame.PendingCode, ScalarType.Int, where), Int(0))), .. Direct(Int(frame.Cleanup), frame.Cleanup)];
                    if (constant is not null) return cleanup;
                    var inside = new ScalarExpression("&&", ScalarType.Bool, where,
                        Left: new ScalarExpression(">=", ScalarType.Bool, where, Left: target, Right: Int(frame.Lo)) { Wraps = _wraps },
                        Right: new ScalarExpression("<=", ScalarType.Bool, where, Left: target, Right: Int(frame.Hi)) { Wraps = _wraps }) { Wraps = _wraps };
                    return [new ScalarStatement("if", where, inside, Body: Route(frame.Parent), Alternative: cleanup)];
                }
                return Route(r);
            }
            IEnumerable<ScalarStatement> FinishOne(ScalarStatement s, AsyncRegion r)
            {
                if (s.Kind == "__jump")
                {
                    var target = int.Parse(s.Name!, CultureInfo.InvariantCulture);
                    return Transfer(Int(target), target, r, s.Location, s.Mutable);
                }
                if (s.Kind == "__resume") return Transfer(Fix(s.Expression!), null, r, s.Location);
                if (s.Kind == "__complete")
                {
                    var result = new List<ScalarStatement>();
                    if (s.Expression is not null) result.Add(Do(Assign(Fix(ResultField()), Fix(s.Expression))));
                    if (HasCleanup(r))
                        result.AddRange(Transfer(Int(completionState), completionState, r, s.Location));
                    else result.Add(new ScalarStatement("return", s.Location, Literal(true, at)));
                    return result;
                }
                return [s with
                {
                    Expression = s.Expression is null ? null : Fix(s.Expression), Increment = s.Increment is null ? null : Fix(s.Increment),
                    Initializer = s.Initializer is null ? null : FinishOne(s.Initializer, r).Single(),
                    Body = s.Body is null ? null : Finish(s.Body, r), Alternative = s.Alternative is null ? null : Finish(s.Alternative, r)
                }];
            }
            ScalarExpression InRange(AsyncRegion r) =>
                new("&&", ScalarType.Bool, at, Left: new(">=", ScalarType.Bool, at, Left: S(), Right: Int(r.Lo)) { Wraps = _wraps }, Right: new("<=", ScalarType.Bool, at, Left: S(), Right: Int(r.Hi)) { Wraps = _wraps }) { Wraps = _wraps };
            ScalarExpression Is(int state) => new("==", ScalarType.Bool, at, Left: S(), Right: Int(state)) { Wraps = _wraps };
            List<ScalarStatement> Dispatch(AsyncRegion r)
            {
                var output = new List<ScalarStatement>();
                foreach (var (state, block) in blocks.OrderBy(b => b.Key))
                    if (RegionOf(state) == r) output.Add(new ScalarStatement("if", at, Is(state), Body: Finish(block, r), Alternative: []));
                foreach (var child in r.Children)
                {
                    List<ScalarStatement> handlers = child.Cleanup >= 0
                        ? AsyncFaultNames.Select((name, i) => new ScalarStatement("catch", at, Name: AsyncRuntimeName(name), Body:
                            [Do(Assign(Field(child.PendingCode, ScalarType.Int, at), Int(i + 1))), Do(Assign(Field(child.PendingTarget, ScalarType.Int, at), Int(child.After))), Do(Assign(S(), Int(child.Cleanup)))])).Append(
                            new ScalarStatement("catch", at, Name: "*", Body: [Do(Assign(Field(child.PendingCode, ScalarType.Int, at), Int(1))), Do(Assign(Field(child.PendingTarget, ScalarType.Int, at), Int(child.After))), Do(Assign(S(), Int(child.Cleanup)))])).ToList()
                        : child.Catches is { } catches
                        ? catches.Select(c => c with { Body = Ends(c.Body) ? Finish(c.Body!, r) : [.. Finish(c.Body ?? [], r), Do(Assign(S(), Int(child.After)))] }).ToList()
                        : [new ScalarStatement("finally", at, Body: [new ScalarStatement("if", at, new ScalarExpression("unary!", ScalarType.Bool, at, Left: Fix(SuspField())) { Wraps = _wraps },
                            Body: Finish(child.Finally, root), Alternative: [])])];
                    var guarded = new ScalarStatement("try", at, Body: [new ScalarStatement("while", at, Literal(true, at), Body: Dispatch(child))], Alternative: handlers);
                    List<ScalarStatement> next = r == root ? [new ScalarStatement("continue", at)]
                        : [new ScalarStatement("if", at, InRange(r), Body: [new ScalarStatement("continue", at)], Alternative: [new ScalarStatement("break", at)])];
                    output.Add(new ScalarStatement("if", at, InRange(child), Body: [guarded, .. next], Alternative: []));
                }
                output.Add(r == root ? new ScalarStatement("return", at, Literal(true, at)) : new ScalarStatement("break", at));
                return output;
            }
            List<ScalarStatement> step =
            [
                new ScalarStatement("local", at, Fix(StateField()), dispatch, ScalarType.Int) { Mutable = true },
                Do(Assign(Fix(StateField()), Int(-1))),
                Do(Assign(Fix(SuspField()), Literal(false, at))),
                new ScalarStatement("while", at, Literal(true, at), Body: Dispatch(root)),
            ];
            // Run: Step, with an escaping exception captured as its code (the native runtime knows exceptions by name), then completion.
            var code = Fresh(); var finished = Fresh();
            ScalarExpression CodeRead() => new("local", ScalarType.Int, at, code);
            ScalarExpression FinishedRead() => new("local", ScalarType.Bool, at, finished);
            var task = new ScalarExpression("field", taskType, at, taskField.ToString(CultureInfo.InvariantCulture), Left: selfRead);
            var captures = AsyncFaultNames.Select((n, i) => new ScalarStatement("catch", at, Name: AsyncRuntimeName(n), Body: [Do(Assign(CodeRead(), Int(i + 1)))])).ToList();
            captures.Add(new ScalarStatement("catch", at, Name: "*", Body: [Do(Assign(CodeRead(), Int(1 + Array.IndexOf(AsyncFaultNames, "Exception"))))]));
            List<ScalarStatement> complete = element == ScalarType.Unit ? [Do(Call("__task_done", ScalarType.Unit, Upcast(task, baseTask)))]
                : [Do(Call("__task_finish_" + StructOf(taskType).Name[7..], ScalarType.Unit, task, Fix(ResultField())))];
            List<ScalarStatement> run =
            [
                new ScalarStatement("local", at, Int(0), code, ScalarType.Int) { Mutable = true },
                new ScalarStatement("local", at, Literal(false, at), finished, ScalarType.Bool) { Mutable = true },
                new ScalarStatement("try", at, Body: [Do(Assign(FinishedRead(), Call(nameA + "_step", ScalarType.Bool, selfRead)))], Alternative: captures),
                new ScalarStatement("if", at, new ScalarExpression("!=", ScalarType.Bool, at, Left: CodeRead(), Right: Int(0)) { Wraps = _wraps },
                    Body: [Do(Call("__task_fail", ScalarType.Unit, Upcast(task, baseTask), CodeRead()))],
                    Alternative: [new ScalarStatement("if", at, FinishedRead(), Body: complete, Alternative: [])]),
            ];
            var runMethod = _structs[contIndex].Methods.First(x => x.Name == "Run");
            _structs[kA] = new ScalarStruct(nameA, idA, fields, [new ScalarStructMethod("Run", nameA + ".Run", true, false, runMethod.Declaration) { IsOverride = true, Slot = runMethod.Slot, Owner = kA, IsLifted = true }], at, IsClass: true) { BaseIndex = contIndex, IsSealed = true };
            Lift(new ScalarFunction(nameA + "_step", [self], ScalarType.Bool, [.. referenceSlots, .. step], [TrueContract(at)], at));
            Lift(new ScalarFunction(nameA + ".Run", [self], ScalarType.Unit, run, [TrueContract(at)], at));
            // The method: make the machine with the arguments and a pending task, run it to its first suspension, return the task.
            var machine = Fresh(); var machineRead = new ScalarExpression("local", idA, at, machine);
            var values = parameters.Select(p => ForwardParameter(p, at)).ToList();
            values.Add(Call(taskType == baseTask ? "__task_new" : "__task_new_" + StructOf(taskType).Name[7..], taskType));
            var indices = Enumerable.Range(0, parameters.Count).Append(taskField);
            var made = new ScalarExpression("init", idA, at, string.Join(",", indices), Left: new("new", idA, at)) { Arguments = values };
            return
            [
                new ScalarStatement("local", at, made, machine, idA) { Mutable = false },
                Do(Call(nameA + ".Run", ScalarType.Unit, machineRead)),
                new ScalarStatement("return", at, new ScalarExpression("field", taskType, at, taskField.ToString(CultureInfo.InvariantCulture), Left: machineRead)),
            ];
        }
        // ---- N23/N24/N25: query expressions (from, where, let, select, into; N24: orderby ascending/descending, join on/equals; N25: group by) ----
        // 'from x in s clauses select e' is, as C#'s Where/Select/SelectMany translation over LINQ to Objects, a deferred sequence: s is evaluated
        // (and checked for null) when the query is created; the clauses run lazily, element by element, when the result is enumerated. The
        // query body is bound in place (outer locals are visible) as the iterator 'foreach (x in s) { where: if (p) / let: local y = v /
        // from: foreach (z in t) ... yield return e }' and lowered by the N21 iterator rewriting into a sealed IEnumerable<U> class. Outer
        // variables are captured by reference through the N19 cells, as the C# lambdas of the translation capture them.
        private int _queryNest;
        private sealed class QStep
        {
            public string Kind = ""; public Local? Variable; public ScalarExpression? Value; public List<ScalarStatement> Pending = [];
            public (ScalarType Element, bool Sequence, int Enumerator) Shape; public List<Local> Vars = [];
            public List<(ScalarExpression Key, bool Descending)> Keys = []; public string? Param; public ScalarExpression? InnerKey;
            public SourceLocation At; public Local? GroupVariable; public ScalarType GroupType; public List<ScalarStatement> KeyPending = [];
        }
        private bool OrderableKey(ScalarType t) => Numeric(t) || t == ScalarType.Char || t == ScalarType.Bool || t == ScalarType.Decimal || IsEnum(t);
        private bool JoinableKey(ScalarType t) => OrderableKey(t) || t == ScalarType.String;
        // N26 adds nullable string keys to grouping; generated storage uses the same UTF-16 representation as string fields.
        private bool GroupKey(ScalarType t) => OrderableKey(t) || t == ScalarType.String && Has(ScalarFeatures.NullStrings);
        // The hidden IGrouping<K, E> class for a key and element type. A pair seen for the first time is recorded and the program is parsed
        // again with the class added (see ParseExtended).
        private ScalarType GroupingType(ScalarType key, ScalarType element, SourceLocation at)
        {
            var k = QueryShow(key); var e = QueryShow(element);
            // Earlier feature profiles retain their original string-field boundary.
            if (element == ScalarType.String && !Has(ScalarFeatures.NullStrings)) Fail("AI3100", "Grouping string elements is not supported yet.", at);
            var found = _structs.FindIndex(x => x.Name == GroupingName(k, e));
            if (found >= 0) return _structs[found].Id;
            if (_structs.FindIndex(x => x.Name == "__IEnumerable_" + e && IsIteratorInterface(x.Name)) < 0 || e.Contains('<') || k.Contains('<'))
                Fail("AI3100", $"Grouping elements of type '{e}' is not supported yet.", at);
            if (groupingPairs is { } pairs && !pairs.Contains((k, e))) pairs.Add((k, e));
            Fail("AI3100", $"Grouping '{e}' elements by '{k}' keys needs a further pass.", at);
            return ScalarType.Unit;
        }
        private readonly HashSet<string> _rangeLocals = new(StringComparer.Ordinal);
        private bool QueryAt() => Has(ScalarFeatures.Queries) && Current.Text == "from" && !IsLocalName("from") && IsSourceIdentifier(TokenAt(_position + 1).Text)
            && (TokenAt(_position + 2).Text == "in" || IsSourceIdentifier(TokenAt(_position + 2).Text) && TokenAt(_position + 3).Text == "in");
        private int ClauseAhead()
        {
            var p = _position;
            while (TokenAt(p).Text == "\n") p++;
            return p;
        }
        private string QueryShow(ScalarType type) => IsStruct(type) || IsEnum(type) || IsNull(type) || IsObject(type) || IsFunctionValue(type) ? Show(type) : Show(type).ToLowerInvariant();
        private static bool IsClauseWord(string text) => text is "where" or "let" or "from" or "select" or "orderby" or "join" or "group" or "into";
        private ScalarExpression QueryExpression()
        {
            var keyword = Next(); var at = keyword.Location;
            if (_contract) Fail("AI3100", "Query expressions are not supported in contracts.", at);
            if (_queryNest > 0) Fail("AI3100", "Query expressions inside a query clause are not supported yet.", at);
            if (_lambdaNest > 0) Fail("AI3100", "Query expressions inside lambdas and anonymous methods are not supported yet.", at);
            if (_iterElem is not null || _asyncTask is not null) Fail("AI3100", "Query expressions inside iterators and async functions are not supported yet.", at);
            if (TokenAt(_position + 1).Text != "in") Fail("AI3100", "Explicitly typed range variables (Cast) are not supported yet.", Current.Location);
            var name = Next(); Expect("in");
            var source = Expression(); Read(source);
            var result = QueryBody(source, name, at);
            while (TokenAt(ClauseAhead()).Text == "into" && IsSourceIdentifier(TokenAt(ClauseAhead() + 1).Text))
            {
                _position = ClauseAhead(); Next();
                var next = Next();
                result = QueryBody(result, next, at);
            }
            return result;
        }
        private ScalarExpression QueryBody(ScalarExpression source, Lexeme name, SourceLocation at)
        {
            if (IteratorShape(source.Type) is not { Sequence: true } shape)
            {
                Fail("AI3106", $"Could not find an implementation of the query pattern for source type '{QueryShow(source.Type)}'.", source.Location);
                return source;
            }
            var startId = _nextId;
            var steps = new List<QStep>(); var vars = new List<Local>();
            var savedPattern = _patternLocals.ToList(); _patternLocals.Clear();
            var savedPending = _pendingDeclarations.ToList(); _pendingDeclarations.Clear();
            var savedAssigned = new HashSet<string>(_assigned);
            var scopes = _scopes.Count;
            var rangeNames = new HashSet<string>(StringComparer.Ordinal);
            ScalarExpression selected; Local first;
            void NoPending(string what, SourceLocation where)
            {
                if (_pendingDeclarations.Count > 0 || _patternLocals.Count > 0) Fail("AI3100", $"Pattern variables and switch expressions in {what} are not supported yet.", where);
            }
            void OnlyRange(ScalarExpression e, Local? allowed, string message)
            {
                Visit(e, x => { if (x.Operation == "local" && x.Value is { } v && vars.Any(r => r.EmittedName == v) && v != allowed?.EmittedName) Fail("AI3106", string.Format(CultureInfo.InvariantCulture, message, SourceName(v)), x.Location); });
            }
            _scopes.Add(new(StringComparer.Ordinal)); _queryNest++;
            Local Range(Lexeme n, ScalarType type)
            {
                if (rangeNames.Contains(n.Name)) Fail("AI3106", $"The range variable '{n.Text}' has already been declared.", n.Location);
                else if (IsLocalName(n.Text)) Fail("AI3106", $"The range variable '{n.Text}' conflicts with a previous declaration of '{n.Text}'.", n.Location);
                rangeNames.Add(n.Name);
                var local = Declare(n.Text, type, false, n.Location); _assigned.Add(local.EmittedName); _rangeLocals.Add(local.EmittedName);
                vars.Add(local);
                return local;
            }
            List<ScalarStatement> TakePending()
            {
                if (_patternLocals.Count > 0) Fail("AI3100", "Pattern variables inside query expressions are not supported yet.", _pendingDeclarations.Count > 0 ? _pendingDeclarations[0].Location : at);
                var taken = _pendingDeclarations.ToList(); _pendingDeclarations.Clear(); return taken;
            }
            void RangeValue(ScalarExpression value, string clause)
            {
                if (IsNull(value.Type) || IsFunctionValue(value.Type) || value.Type == ScalarType.Unit)
                    Fail("AI3106", clause == "select" ? "The type of the expression in the select clause is incorrect." : $"Cannot assign {QueryShow(value.Type)} to a range variable.", value.Location);
            }
            try
            {
                first = Range(name, shape.Element);
                while (true)
                {
                    var p = ClauseAhead();
                    if (IsClauseWord(TokenAt(p).Text)) _position = p;
                    var clause = Current;
                    if (clause.Text == "where")
                    {
                        Next(); var condition = Expression(); Read(condition); Require(condition, ScalarType.Bool);
                        steps.Add(new QStep { Kind = "where", Value = condition, Pending = TakePending() }); continue;
                    }
                    if (clause.Text == "let")
                    {
                        Next(); var n = Next(); Expect("=");
                        var value = Expression(); Read(value); RangeValue(value, "let");
                        var pending = TakePending();
                        steps.Add(new QStep { Kind = "let", Variable = Range(n, value.Type), Value = value, Pending = pending }); continue;
                    }
                    if (clause.Text == "from")
                    {
                        Next();
                        if (TokenAt(_position + 1).Text != "in") Fail("AI3100", "Explicitly typed range variables (Cast) are not supported yet.", Current.Location);
                        var n = Next(); Expect("in");
                        var inner = Expression(); Read(inner);
                        if (IteratorShape(inner.Type) is not { Sequence: true } innerShape)
                        {
                            Fail("AI3106", $"Could not find an implementation of the query pattern for source type '{QueryShow(inner.Type)}'.", inner.Location);
                            return source;
                        }
                        var pending = TakePending();
                        steps.Add(new QStep { Kind = "from", Variable = Range(n, innerShape.Element), Value = inner, Pending = pending, Shape = innerShape }); continue;
                    }
                    if (clause.Text == "select")
                    {
                        Next(); selected = Expression(); Read(selected); RangeValue(selected, "select");
                        steps.Add(new QStep { Kind = "select", Value = selected, Pending = TakePending() });
                        break;
                    }
                    if (clause.Text == "orderby" && Has(ScalarFeatures.QueryJoins))
                    {
                        Next(); var keys = new List<(ScalarExpression Key, bool Descending)>();
                        do
                        {
                            var key = Expression(); Read(key); NoPending("orderby keys", key.Location);
                            if (key.Type == ScalarType.String) Fail("AI3100", "Ordering by string keys is not supported yet (C# compares strings with culture-sensitive rules).", key.Location);
                            else if (!OrderableKey(key.Type)) Fail("AI3100", $"Ordering by keys of type '{QueryShow(key.Type)}' is not supported yet.", key.Location);
                            var descending = false;
                            if (Current.Text == "descending") { Next(); descending = true; } else if (Current.Text == "ascending") Next();
                            keys.Add((key, descending));
                        } while (Take(","));
                        steps.Add(new QStep { Kind = "orderby", Keys = keys, Vars = vars.ToList(), At = clause.Location }); continue;
                    }
                    if (clause.Text == "join" && Has(ScalarFeatures.QueryJoins))
                    {
                        Next();
                        if (TokenAt(_position + 1).Text != "in") Fail("AI3100", "Explicitly typed range variables (Cast) are not supported yet.", Current.Location);
                        var n = Next(); Expect("in");
                        var inner = Expression(); Read(inner); NoPending("join sources", inner.Location);
                        OnlyRange(inner, null, "The name '{0}' does not exist in the current context.");
                        if (IteratorShape(inner.Type) is not { Sequence: true } innerShape)
                        {
                            Fail("AI3106", $"Could not find an implementation of the query pattern for source type '{QueryShow(inner.Type)}'.", inner.Location);
                            return source;
                        }
                        if (Current.Text != "on") Fail("AI3106", "Expected contextual keyword 'on'.", Current.Location);
                        Next();
                        var outerKey = Expression(); Read(outerKey); NoPending("join keys", outerKey.Location);
                        if (Current.Text != "equals") Fail("AI3106", "Expected contextual keyword 'equals'.", Current.Location);
                        Next();
                        var variable = Range(n, innerShape.Element);
                        var innerKey = Expression(); Read(innerKey); NoPending("join keys", innerKey.Location);
                        OnlyRange(innerKey, variable, "The name '{0}' is not in scope on the right side of 'equals'.  Consider swapping the expressions on either side of 'equals'.");
                        var groupJoin = TokenAt(ClauseAhead()).Text == "into";
                        if (groupJoin && !Has(ScalarFeatures.QueryGroups)) Fail("AI3100", "Group joins (join ... into) require the QueryGroups capability.", TokenAt(ClauseAhead()).Location);
                        if (outerKey.Type != innerKey.Type) Fail("AI3106", "The type of one of the expressions in the join clause is incorrect.  Type inference failed in the call to 'Join'.", clause.Location);
                        else if (!JoinableKey(outerKey.Type)) Fail("AI3100", $"Joining on keys of type '{QueryShow(outerKey.Type)}' is not supported yet.", outerKey.Location);
                        Local? groupVariable = null; var groupType = ScalarType.Unit;
                        if (groupJoin)
                        {
                            if (!GroupKey(outerKey.Type)) Fail("AI3100", $"Group joining on keys of type '{QueryShow(outerKey.Type)}' is not supported yet.", outerKey.Location);
                            groupType = GroupingType(outerKey.Type, innerShape.Element, n.Location);
                            _position = ClauseAhead(); Next(); var groupName = Next();
                            if (!IsSourceIdentifier(groupName.Text)) Fail("AI3106", "Expected a group range variable after 'into'.", groupName.Location);
                            // The joined item is visible only in the right key; 'into' replaces it with the sequence variable.
                            _scopes[^1].Remove(n.Name); rangeNames.Remove(n.Name); vars.Remove(variable); _rangeLocals.Remove(variable.EmittedName);
                            var sequenceType = _structs[StructOf(groupType).BaseIndex].Id;
                            groupVariable = Range(groupName, sequenceType);
                        }
                        steps.Add(new QStep { Kind = groupJoin ? "groupjoin" : "join", Variable = variable, GroupVariable = groupVariable, GroupType = groupType,
                            Value = inner, Shape = innerShape, Keys = [(outerKey, false)], InnerKey = innerKey, Param = Fresh(), At = clause.Location });
                        continue;
                    }
                    if (clause.Text == "group" && Has(ScalarFeatures.QueryGroups))
                    {
                        Next();
                        var value = Expression(); Read(value); var valuePending = TakePending();
                        if (IsNull(value.Type) || IsFunctionValue(value.Type) || value.Type == ScalarType.Unit)
                            Fail("AI3106", "The type of the expression in the group clause is incorrect.", value.Location);
                        if (Current.Text != "by") Fail("AI3106", "Expected contextual keyword 'by'.", Current.Location);
                        Next();
                        var key = Expression(); Read(key); var keyPending = TakePending();
                        if (!GroupKey(key.Type)) Fail("AI3100", $"Grouping by keys of type '{QueryShow(key.Type)}' is not supported yet.", key.Location);
                        var grouping = GroupingType(key.Type, value.Type, value.Location);
                        selected = new ScalarExpression("null", grouping, value.Location);
                        steps.Add(new QStep { Kind = "group", Value = value, Keys = [(key, false)], Pending = valuePending, KeyPending = keyPending, Vars = vars.ToList(), At = clause.Location });
                        break;
                    }
                    if (clause.Text is "orderby" or "join" or "group") Fail("AI3100", $"The '{clause.Text}' query clause is not supported yet.", clause.Location);
                    Fail("AI3106", "A query body must end with a select clause or a group clause.", TokenAt(p).Location);
                    return source;
                }
            }
            finally
            {
                _queryNest--;
                while (_scopes.Count > scopes) _scopes.RemoveAt(_scopes.Count - 1);
                _assigned = savedAssigned;
                _patternLocals.Clear(); foreach (var n in savedPattern) _patternLocals.Add(n);
                _pendingDeclarations.Clear(); _pendingDeclarations.AddRange(savedPending);
            }
            var element = selected.Type;
            var resultIndex = _structs.FindIndex(x => x.Name.StartsWith("__IEnumerable_", StringComparison.Ordinal) && IsIteratorInterface(x.Name) && IteratorShape(x.Id) is { } s && s.Element == element);
            if (resultIndex < 0)
            {
                if (IteratorShape(element) is { Sequence: true }) queryElementTypes!.Add(StructOf(element).Name);
                Fail("AI3100", $"Query results of type '{QueryShow(element)}' are not supported yet.", selected.Location); return source;
            }
            var resultType = _structs[resultIndex].Id;
            if (_checkOnly) return new ScalarExpression("null", resultType, at);
            // The iterator body.
            ScalarStatement ForeachOver(ScalarExpression sequenceValue, (ScalarType Element, bool Sequence, int Enumerator) s, Local item, List<ScalarStatement> body)
            {
                var enumType = _structs[s.Enumerator].Id;
                var sequence = Fresh(); var readSequence = new ScalarExpression("local", sequenceValue.Type, at, sequence);
                var enumerator = Fresh(); var readEnumerator = new ScalarExpression("local", enumType, at, enumerator);
                var getEnumerator = MethodCall(readSequence, new Lexeme("GetEnumerator", at), false, []);
                var moveNext = MethodCall(readEnumerator, new Lexeme("MoveNext", at), false, []);
                var current = MethodCall(readEnumerator, new Lexeme("get_Current", at), false, []);
                var dispose = MethodCall(readEnumerator, new Lexeme("Dispose", at), false, []);
                var itemStatement = new ScalarStatement("local", at, current, item.EmittedName, s.Element) { Mutable = false };
                var loop = new ScalarStatement("while", at, moveNext, Body: [itemStatement, .. body]);
                var isNull = new ScalarExpression("==", ScalarType.Bool, at, Left: readEnumerator, Right: new("null", enumType, at)) { Wraps = _wraps };
                var cleanup = new ScalarStatement("if", at, isNull, Body: [], Alternative: [new ScalarStatement("expression", at, dispose)]);
                var guarded = new ScalarStatement("try", at, Body: [loop], Alternative: [new ScalarStatement("finally", at, Body: [cleanup])]);
                return new ScalarStatement("block", at, Body:
                [
                    new ScalarStatement("local", at, sequenceValue, sequence, sequenceValue.Type) { Mutable = false },
                    new ScalarStatement("local", at, getEnumerator, enumerator, enumType) { Mutable = false },
                    guarded
                ]);
            }
            ScalarExpression Fld(ScalarExpression obj, int i, ScalarType t) => new("field", t, at, i.ToString(CultureInfo.InvariantCulture), Left: obj);
            ScalarStatement Set(ScalarExpression target, ScalarExpression v) => new("expression", at, new ScalarExpression("=", target.Type, at, Left: target, Right: v) { Wraps = _wraps });
            static ScalarStatement Decl(string n, ScalarType t, ScalarExpression init, bool mutable, SourceLocation where) => new("local", where, init, n, t) { Mutable = mutable };
            ScalarExpression LocalRead(string n, ScalarType t) => new("local", t, at, n);
            ScalarExpression Null(ScalarType t) => new("null", t, at);
            ScalarExpression NotNull(ScalarExpression e) => new("!=", ScalarType.Bool, at, Left: e, Right: Null(e.Type)) { Wraps = _wraps };
            ScalarExpression IsNullE(ScalarExpression e) => new("==", ScalarType.Bool, at, Left: e, Right: Null(e.Type)) { Wraps = _wraps };
            ScalarExpression Not(ScalarExpression e) => new("unary!", ScalarType.Bool, at, Left: e) { Wraps = _wraps };
            ScalarExpression AndE(ScalarExpression a, ScalarExpression b) => new("&&", ScalarType.Bool, at, Left: a, Right: b) { Wraps = _wraps };
            ScalarExpression OrE(ScalarExpression a, ScalarExpression b) => new("||", ScalarType.Bool, at, Left: a, Right: b) { Wraps = _wraps };
            // bool keys compare as (a && b) || (!a && !b); MakeBinary has no bool equality.
            ScalarExpression EqualKeys(ScalarExpression a, ScalarExpression b) =>
                a.Type == ScalarType.Bool ? OrE(AndE(a, b), AndE(Not(a), Not(b))) : MakeBinary("==", a, b, at);
            static ScalarExpression Ren(ScalarExpression e, IReadOnlyDictionary<string, ScalarExpression> map) =>
                e.Operation == "local" && e.Value is { } v && map.TryGetValue(v, out var m) ? m with { Location = e.Location }
                : e with { Left = e.Left is null ? null : Ren(e.Left, map), Right = e.Right is null ? null : Ren(e.Right, map), Arguments = e.Arguments?.Select(x => Ren(x, map)).ToList() };
            static ScalarStatement RenS(ScalarStatement st, IReadOnlyDictionary<string, ScalarExpression> map) => st with
            {
                Expression = st.Expression is null ? null : Ren(st.Expression, map), Increment = st.Increment is null ? null : Ren(st.Increment, map),
                Initializer = st.Initializer is null ? null : RenS(st.Initializer, map), Body = st.Body?.Select(x => RenS(x, map)).ToList(), Alternative = st.Alternative?.Select(x => RenS(x, map)).ToList()
            };
            ScalarType NodeClass(List<ScalarType> types)
            {
                var k = _structs.Count; var id = (ScalarType)(StructTypeBase + k);
                var fields = types.Select((t, i) => new ScalarStructField($"f{i}", t, EraseType(t), true, false, at) { Owner = k }).ToList();
                fields.Add(new ScalarStructField("next", id, EraseType(id), true, false, at) { Owner = k });
                _structs.Add(new ScalarStruct($"__qnode_{k}", id, fields, [], at, IsClass: true) { IsSealed = true });
                return id;
            }
            ScalarExpression NewNode(ScalarType id, List<ScalarExpression> values) =>
                new("init", id, at, string.Join(",", Enumerable.Range(0, values.Count)), Left: new("new", id, at)) { Arguments = values };
            // Appends a node at the end of the list (head, tail).
            List<ScalarStatement> Append(ScalarType id, string head, string tail, List<ScalarExpression> values)
            {
                var made = Fresh();
                return [Decl(made, id, NewNode(id, [.. values, Null(id)]), false, at),
                    new ScalarStatement("if", at, IsNullE(LocalRead(head, id)), Body: [Set(LocalRead(head, id), LocalRead(made, id))],
                        Alternative: [Set(Fld(LocalRead(tail, id), values.Count, id), LocalRead(made, id))]),
                    Set(LocalRead(tail, id), LocalRead(made, id))];
            }
            var topDecls = new List<ScalarStatement>();
            var stopAt = -1; (ScalarType Id, string Head, string Tail) stopNode = default;
            // N25 (Enumerable.GroupBy): when first enumerated, the elements before 'group' are read completely; for each, the key and then
            // the element are computed and the element is added to the group of an equal key (groups in order of first key, elements in
            // source order). Then the groups are produced.
            string GroupSuffix() => StructOf(element).Name[6..];
            List<ScalarStatement> GroupAdd(ScalarType gt, string head, string tail, ScalarExpression key, ScalarExpression value,
                List<ScalarStatement> keyPending, List<ScalarStatement> valuePending)
            {
                var suffix = StructOf(gt).Name[6..];
                string k = Fresh(), e = Fresh(), g = Fresh();
                ScalarExpression gR = LocalRead(g, gt), headR = LocalRead(head, gt), tailR = LocalRead(tail, gt);
                ScalarExpression Call(string name, ScalarType type, params ScalarExpression[] args) => new("call", type, at, name) { Arguments = args };
                return [.. keyPending, Decl(k, key.Type, key, false, at), .. valuePending, Decl(e, value.Type, value, false, at),
                    Decl(g, gt, Call("__grp_find_" + suffix, gt, headR, LocalRead(k, key.Type)), true, at),
                    new ScalarStatement("if", at, IsNullE(gR), Body:
                    [
                        Set(gR, Call("__grp_new_" + suffix, gt, LocalRead(k, key.Type))),
                        new ScalarStatement("if", at, IsNullE(headR), Body: [Set(headR, gR)], Alternative: [new ScalarStatement("expression", at, Call("__grp_link_" + suffix, ScalarType.Unit, tailR, gR))]),
                        Set(tailR, gR)
                    ], Alternative: []),
                    new ScalarStatement("expression", at, Call("__grp_add_" + suffix, ScalarType.Unit, gR, LocalRead(e, value.Type)))];
            }
            List<ScalarStatement> Build(int i)
            {
                var step = steps[i];
                if (i == stopAt && step.Kind == "group") return GroupAdd(element, stopNode.Head!, stopNode.Tail!, step.Keys[0].Key, step.Value!, step.KeyPending, step.Pending);
                if (i == stopAt)
                    return Append(stopNode.Id, stopNode.Head!, stopNode.Tail!,
                        [.. step.Vars.Select(v => LocalRead(v.EmittedName, v.Type)), .. step.Keys.Select(k => DefaultValue(k.Key.Type, at))]);
                switch (step.Kind)
                {
                    case "select": return [.. step.Pending, new ScalarStatement("yield", step.Value!.Location, Coerce(step.Value, element))];
                    case "where":
                        if (step.Value!.Operation == "literal") return step.Value.Value == "true" ? [.. step.Pending, .. Build(i + 1)] : [.. step.Pending];
                        return [.. step.Pending, new ScalarStatement("if", step.Value.Location, step.Value, Body: Build(i + 1), Alternative: [])];
                    case "let":
                        return [.. step.Pending, new ScalarStatement("local", step.Value!.Location, step.Value, step.Variable!.EmittedName, step.Value.Type) { Mutable = false }, .. Build(i + 1)];
                    case "groupjoin":
                    {
                        // GroupJoin buffers the inner source once, at the first outer item. Unlike Join, an empty
                        // lookup still produces one (empty) sequence per outer item, and evaluates every outer key.
                        var gt = step.GroupType; var z = step.Variable!; var group = step.GroupVariable!;
                        var suffix = StructOf(gt).Name[6..];
                        ScalarExpression Call(string name, ScalarType type, params ScalarExpression[] args) => new("call", type, at, name) { Arguments = args };
                        string built = Fresh(), head = Fresh(), tail = Fresh(), empty = Fresh();
                        topDecls.Add(Decl(built, ScalarType.Bool, Literal(false, at), true, at));
                        topDecls.Add(Decl(head, gt, Null(gt), true, at)); topDecls.Add(Decl(tail, gt, Null(gt), true, at));
                        topDecls.Add(Decl(empty, gt, Call("__grp_new_" + suffix, gt, DefaultValue(step.InnerKey!.Type, at)), false, at));
                        var zb = new Local(Fresh(), z.Type, false);
                        var innerKey = Ren(step.InnerKey, new Dictionary<string, ScalarExpression> { [z.EmittedName] = LocalRead(zb.EmittedName, z.Type) });
                        var buildBody = GroupAdd(gt, head, tail, innerKey, LocalRead(zb.EmittedName, z.Type), [], []);
                        if (innerKey.Type == ScalarType.String && Has(ScalarFeatures.NullStrings))
                        {
                            // Lookup.CreateForJoin skips null inner keys, including for GroupJoin. Evaluate the selector once.
                            var key = Fresh(); var readKey = LocalRead(key, ScalarType.String);
                            buildBody = [Decl(key, ScalarType.String, innerKey, false, at),
                                new ScalarStatement("if", at, MakeBinary("!=", readKey, Null(ScalarType.String), at),
                                    Body: GroupAdd(gt, head, tail, readKey, LocalRead(zb.EmittedName, z.Type), [], []), Alternative: [])];
                        }
                        var build = new ScalarStatement("if", at, Not(LocalRead(built, ScalarType.Bool)), Body:
                        [
                            Set(LocalRead(built, ScalarType.Bool), Literal(true, at)),
                            ForeachOver(LocalRead(step.Param!, step.Value!.Type), step.Shape, zb, buildBody)
                        ], Alternative: []);
                        var found = Fresh(); var f = LocalRead(found, gt);
                        return [build, Decl(found, gt, Call("__grp_find_" + suffix, gt, LocalRead(head, gt), step.Keys[0].Key), true, at),
                            new ScalarStatement("if", at, IsNullE(f), Body: [Set(f, LocalRead(empty, gt))], Alternative: []),
                            Decl(group.EmittedName, group.Type, Coerce(f, group.Type), false, at), .. Build(i + 1)];
                    }
                    case "join":
                    {
                        // Enumerable.Join: at the first outer element the inner sequence is read into a lookup (inner keys in inner order, null keys
                        // dropped); an empty lookup ends the join. Each outer element then computes its key once and meets the matching inner
                        // elements in inner order.
                        var keyType = step.InnerKey!.Type; var z = step.Variable!;
                        var id = NodeClass([z.Type, keyType]);
                        string built = Fresh(), head = Fresh(), tail = Fresh();
                        topDecls.Add(Decl(built, ScalarType.Bool, Literal(false, at), true, at));
                        topDecls.Add(Decl(head, id, Null(id), true, at)); topDecls.Add(Decl(tail, id, Null(id), true, at));
                        var zb = new Local(Fresh(), z.Type, false); var kb = Fresh();
                        var innerKey = Ren(step.InnerKey, new Dictionary<string, ScalarExpression> { [z.EmittedName] = LocalRead(zb.EmittedName, z.Type) });
                        var append = Append(id, head, tail, [LocalRead(zb.EmittedName, z.Type), LocalRead(kb, keyType)]);
                        // Enumerable.Join drops elements whose key is null; with NullStrings a string key can be null, so null inner keys are not stored (a null outer key then matches nothing).
                        var nullable = keyType == ScalarType.String && Has(ScalarFeatures.NullStrings);
                        List<ScalarStatement> buildBody = nullable
                            ? [Decl(kb, keyType, innerKey, false, at), new ScalarStatement("if", at, MakeBinary("!=", LocalRead(kb, keyType), Null(ScalarType.String), at), Body: append, Alternative: [])]
                            : [Decl(kb, keyType, innerKey, false, at), .. append];
                        var param = LocalRead(step.Param!, step.Value!.Type);
                        var build = new ScalarStatement("if", at, Not(LocalRead(built, ScalarType.Bool)), Body:
                        [
                            Set(LocalRead(built, ScalarType.Bool), Literal(true, at)),
                            ForeachOver(param, step.Shape, zb, buildBody),
                            new ScalarStatement("if", at, IsNullE(LocalRead(head, id)), Body: [new ScalarStatement("yieldbreak", at)], Alternative: [])
                        ], Alternative: []);
                        string ko = Fresh(), m = Fresh();
                        var mRead = LocalRead(m, id);
                        var equal = EqualKeys(Fld(mRead, 1, keyType), LocalRead(ko, keyType));
                        if (nullable) equal = AndE(MakeBinary("!=", LocalRead(ko, keyType), Null(ScalarType.String), at), equal);
                        var loop = new ScalarStatement("while", at, NotNull(mRead), Body:
                        [
                            new ScalarStatement("if", at, equal, Body: [Decl(z.EmittedName, z.Type, Fld(mRead, 0, z.Type), false, at), .. Build(i + 1)], Alternative: []),
                            Set(mRead, Fld(mRead, 2, id))
                        ]);
                        return [.. step.Pending, build, Decl(ko, keyType, step.Keys[0].Key, false, at), Decl(m, id, LocalRead(head, id), true, at), loop];
                    }
                    default:
                        return [.. step.Pending, ForeachOver(step.Value!, step.Shape, step.Variable!, Build(i + 1))];
                }
            }
            // A stage enumerates elements up to the next orderby (or the select). Enumerable.OrderBy reads the whole sequence first, then computes
            // every key of the first level for all elements, then of the second level, and so on; it sorts stably (equal keys keep their order,
            // descending reverses the comparison) and runs the rest of the query lazily over the sorted elements.
            List<ScalarStatement> Stage(int start, Func<List<ScalarStatement>, List<ScalarStatement>> wrap)
            {
                var j = steps.FindIndex(start, x => x.Kind is "orderby" or "group");
                if (j < 0) { stopAt = -1; return wrap(Build(start)); }
                if (steps[j].Kind == "group")
                {
                    var gt = element; string gHead = Fresh(), gTail = Fresh(), c0 = Fresh();
                    topDecls.Add(Decl(gHead, gt, Null(gt), true, at)); topDecls.Add(Decl(gTail, gt, Null(gt), true, at));
                    stopAt = j; stopNode = (gt, gHead, gTail);
                    var grouped = wrap(Build(start));
                    stopAt = -1;
                    var cR = LocalRead(c0, gt);
                    grouped.Add(Decl(c0, gt, LocalRead(gHead, gt), true, at));
                    grouped.Add(new ScalarStatement("while", at, NotNull(cR), Body:
                    [
                        new ScalarStatement("yield", at, cR),
                        Set(cR, new ScalarExpression("call", gt, at, "__grp_next_" + GroupSuffix()) { Arguments = [cR] })
                    ]));
                    return grouped;
                }
                var o = steps[j]; var nv = o.Vars.Count;
                var id = NodeClass([.. o.Vars.Select(v => v.Type), .. o.Keys.Select(k => k.Key.Type)]);
                string head = Fresh(), tail = Fresh(), sorted = Fresh();
                topDecls.Add(Decl(head, id, Null(id), true, at)); topDecls.Add(Decl(tail, id, Null(id), true, at)); topDecls.Add(Decl(sorted, id, Null(id), true, at));
                stopAt = j; stopNode = (id, head, tail);
                var result = wrap(Build(start));
                stopAt = -1;
                ScalarExpression Next0(ScalarExpression n) => Fld(n, nv + o.Keys.Count, id);
                for (var level = 0; level < o.Keys.Count; level++)
                {
                    var n = Fresh(); var nRead = LocalRead(n, id);
                    var map = o.Vars.Select((v, vi) => (v.EmittedName, Fld(nRead, vi, v.Type))).ToDictionary(x => x.EmittedName, x => x.Item2, StringComparer.Ordinal);
                    var key = o.Keys[level].Key;
                    result.Add(Decl(n, id, LocalRead(head, id), true, at));
                    result.Add(new ScalarStatement("while", at, NotNull(nRead), Body: [Set(Fld(nRead, nv + level, key.Type), Ren(key, map)), Set(nRead, Next0(nRead))]));
                }
                ScalarExpression Compare(ScalarExpression x, ScalarExpression y, bool greater) =>
                    x.Type == ScalarType.Bool ? (greater ? AndE(x, Not(y)) : AndE(Not(x), y)) : MakeBinary(greater ? ">" : "<", x, y, at);
                ScalarExpression After(ScalarExpression a, ScalarExpression b)
                {
                    ScalarExpression? g = null;
                    for (var level = o.Keys.Count - 1; level >= 0; level--)
                    {
                        var t = o.Keys[level].Key.Type; var fa = Fld(a, nv + level, t); var fb = Fld(b, nv + level, t);
                        var strict = Compare(fa, fb, !o.Keys[level].Descending);
                        g = g is null ? strict : OrE(strict, AndE(EqualKeys(fa, fb), g));
                    }
                    return g!;
                }
                // Stable insertion: each element goes after every sorted element that does not come after it.
                {
                    string n = Fresh(), nx = Fresh(), prev = Fresh(), cur = Fresh();
                    ScalarExpression nR = LocalRead(n, id), nxR = LocalRead(nx, id), prevR = LocalRead(prev, id), curR = LocalRead(cur, id), sR = LocalRead(sorted, id);
                    result.Add(Decl(n, id, LocalRead(head, id), true, at));
                    result.Add(new ScalarStatement("while", at, NotNull(nR), Body:
                    [
                        Decl(nx, id, Next0(nR), false, at), Decl(prev, id, Null(id), true, at), Decl(cur, id, sR, true, at),
                        new ScalarStatement("while", at, AndE(NotNull(curR), Not(After(curR, nR))), Body: [Set(prevR, curR), Set(curR, Next0(curR))]),
                        Set(Next0(nR), curR),
                        new ScalarStatement("if", at, IsNullE(prevR), Body: [Set(sR, nR)], Alternative: [Set(Next0(prevR), nR)]),
                        Set(nR, nxR)
                    ]));
                }
                var c = Fresh(); var cRead = LocalRead(c, id);
                var fresh = o.Vars.Select(v => Fresh()).ToList();
                var rename = o.Vars.Select((v, vi) => (v.EmittedName, LocalRead(fresh[vi], v.Type))).ToDictionary(x => x.EmittedName, x => x.Item2, StringComparer.Ordinal);
                result.Add(Decl(c, id, LocalRead(sorted, id), true, at));
                result.AddRange(Stage(j + 1, inner => [new ScalarStatement("while", at, NotNull(cRead), Body:
                [
                    .. o.Vars.Select((v, vi) => Decl(fresh[vi], v.Type, Fld(cRead, vi, v.Type), false, at)),
                    .. inner.Select(x => RenS(x, rename)),
                    Set(cRead, Next0(cRead))
                ])]));
                return result;
            }
            var srcName = Fresh();
            var staged = Stage(0, inner => [ForeachOver(new ScalarExpression("local", source.Type, at, srcName), shape, first, inner)]);
            List<ScalarStatement> iteratorBody = [.. topDecls, .. staged];
            // Free variables: locals declared before the query body that its clauses read or write.
            var free = new List<ScalarExpression>();
            foreach (var statement in iteratorBody)
                Visit(statement, e => { if (e.Operation is "local" or "cellref" && e.Value is { } n && LocalId(n) is { } id && id < startId && !free.Any(f => f.Value == n)) free.Add(e); });
            var parameters = new List<ScalarParameter> { new(srcName, source.Type) };
            var arguments = new List<ScalarExpression> { QuerySourceCheck(source, at) };
            foreach (var join in steps.Where(x => x.Kind is "join" or "groupjoin")) { parameters.Add(new(join.Param!, join.Value!.Type)); arguments.Add(QuerySourceCheck(join.Value, at)); }
            var rename = new Dictionary<string, ScalarExpression>(StringComparer.Ordinal);
            foreach (var e in free.OrderBy(f => LocalId(f.Value!)))
            {
                var n = e.Value!;
                if (_constLocals.ContainsKey(n)) { rename[n] = _constInits[n]; continue; }
                var p = Fresh();
                if (_this is not null && n == _this.EmittedName)
                {
                    if (!_struct!.IsClass) Fail("AI3106", "Query expressions inside structs cannot access instance members of 'this'.", e.Location);
                    parameters.Add(new(p, e.Type)); arguments.Add(new("local", e.Type, at, n)); rename[n] = new("local", e.Type, at, p);
                    continue;
                }
                if (_patternLocals.Contains(n)) Fail("AI3100", $"Capturing the pattern variable '{SourceName(n)}' in a query expression is not supported yet.", e.Location);
                if (_referenceParameters.ContainsKey(n)) Fail("AI3106", "A deferred query cannot capture a by-reference parameter.", e.Location);
                _cells.Add(n);
                var cell = CellType(e.Type, at);
                parameters.Add(new(p, cell)); arguments.Add(new("cellref", e.Type, at, n));
                rename[n] = new("field", e.Type, e.Location, "0", Left: new("local", cell, e.Location, p));
            }
            ScalarExpression R(ScalarExpression e) => e.Operation == "local" && e.Value is { } v && rename.TryGetValue(v, out var mapped) ? mapped with { Location = e.Location }
                : e with { Left = e.Left is null ? null : R(e.Left), Right = e.Right is null ? null : R(e.Right), Arguments = e.Arguments?.Select(R).ToList() };
            ScalarStatement RS(ScalarStatement s) => s with
            {
                Expression = s.Expression is null ? null : R(s.Expression), Increment = s.Increment is null ? null : R(s.Increment),
                Initializer = s.Initializer is null ? null : RS(s.Initializer), Body = s.Body?.Select(RS).ToList(), Alternative = s.Alternative?.Select(RS).ToList()
            };
            iteratorBody = iteratorBody.Select(RS).ToList();
            var saved = (_returnType, _iterElem);
            List<ScalarStatement> made;
            try { _returnType = resultType; _iterElem = element; made = LowerIterator(parameters, iteratorBody); }
            finally { (_returnType, _iterElem) = saved; }
            var byName = parameters.Select((p, i) => (p.Name, arguments[i])).ToDictionary(x => x.Name, x => x.Item2, StringComparer.Ordinal);
            ScalarExpression A(ScalarExpression e) => e.Operation == "local" && e.Value is { } v && byName.TryGetValue(v, out var value) ? value
                : e with { Left = e.Left is null ? null : A(e.Left), Right = e.Right is null ? null : A(e.Right), Arguments = e.Arguments?.Select(A).ToList() };
            if (steps[0].Kind is "join" or "groupjoin")
            {
                // C# evaluates both source arguments before Join/GroupJoin checks either for null. A
                // checked bundle preserves this order, including failure before a later join's source.
                var join = steps[0]; var bundleType = NodeClass([source.Type, join.Value!.Type]);
                var bundleMaker = "__query_pair_" + Fresh();
                var sourceParam = Fresh(); var innerParam = Fresh();
                Lift(new ScalarFunction(bundleMaker, [new(sourceParam, source.Type), new(innerParam, join.Value.Type)], bundleType,
                    [new ScalarStatement("return", at, NewNode(bundleType,
                        [QuerySourceCheck(LocalRead(sourceParam, source.Type), at), QuerySourceCheck(LocalRead(innerParam, join.Value.Type), at), Null(bundleType)]))],
                    [TrueContract(at)], at));
                var bundleParam = Fresh(); var bundleRead = LocalRead(bundleParam, bundleType);
                var createName = "__query_create_" + Fresh();
                var map = new Dictionary<string, ScalarExpression>
                {
                    [parameters[0].Name] = Fld(bundleRead, 0, source.Type),
                    [parameters[1].Name] = Fld(bundleRead, 1, join.Value.Type)
                };
                Lift(new ScalarFunction(createName, [new(bundleParam, bundleType), .. parameters.Skip(2)], resultType,
                    [new ScalarStatement("return", at, Ren(made[0].Expression!, map))], [TrueContract(at)], at));
                return new ScalarExpression("call", resultType, at, createName) { Arguments =
                    [new ScalarExpression("call", bundleType, at, bundleMaker) { Arguments = [source, join.Value] }, .. arguments.Skip(2)] };
            }
            return A(made[0].Expression!) with { Location = at };
        }
        // Enumerable.Where/Select/SelectMany throw ArgumentNullException for a null source when the query is created.
        private ScalarExpression QuerySourceCheck(ScalarExpression source, SourceLocation at)
        {
            var name = "__query_source_" + IndexOf(source.Type).ToString(CultureInfo.InvariantCulture);
            if (!liftedFunctions!.Any(f => f.Name == name))
            {
                var p = Fresh(); var read = new ScalarExpression("local", source.Type, at, p);
                var isNull = new ScalarExpression("==", ScalarType.Bool, at, Left: read, Right: new("null", source.Type, at)) { Wraps = _wraps };
                Lift(new ScalarFunction(name, [new(p, source.Type)], source.Type,
                    [new ScalarStatement("if", at, isNull, Body: [new ScalarStatement("throw", at, Name: "System.ArgumentNullException")], Alternative: []), new ScalarStatement("return", at, read)],
                    [TrueContract(at)], at));
            }
            return new ScalarExpression("call", source.Type, at, name) { Arguments = [source] };
        }
        private int _directSection = -1;
        // using (let r = e) { body }, using (e) { body } and the declaration using let r = e (to the end of the enclosing block) are
        // { let r = e; try { body } finally { if (r != null) ((IDisposable)r).Dispose() } }, as in C#.
        private (ScalarStatement Statement, bool Returns) UsingStatement()
        {
            var keyword = Next(); var at = keyword.Location;
            var declaration = Current.Text is "let" or "var";
            if (declaration && _scopes.Count == _directSection) Fail("AI3106", "A using declaration cannot be used directly in a switch section; put it in a block.", at);
            var disposable = _structs.ToList().FindIndex(x => x.Name == "IDisposable" && x.IsInterface);
            if (!declaration) Expect("(");
            if (!declaration) _scopes.Add(new(StringComparer.Ordinal));
            Local resource; ScalarExpression value;
            if (Current.Text is "let" or "var")
            {
                Next(); var name = Next(); ScalarType? annotation = null;
                if (Take(":")) { var type = TypeToken(); annotation = Type(type.Text, type.Location); }
                if (!Take("=")) Fail("AI3106", "A using variable requires an initializer.", name.Location);
                value = Expression(); Read(value);
                var resolved = annotation ?? value.Type;
                if (IsNull(resolved)) Fail("AI3107", "Cannot assign null to an implicitly typed local.", value.Location);
                CheckDisposable(resolved, value.Location, disposable);
                value = Coerce(value, resolved);
                resource = Declare(name.Text, resolved, false, name.Location); _assigned.Add(resource.EmittedName);
            }
            else
            {
                value = Expression(); Read(value);
                CheckDisposable(value.Type, value.Location, disposable);
                resource = HiddenLocal(value.Type);
            }
            (IReadOnlyList<ScalarStatement> Body, bool Returns) body;
            if (declaration) { EndStatement(); body = Block(false); }
            else
            {
                Expect(")"); body = Block();
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
            }
            var read = new ScalarExpression("local", resource.Type, at, resource.EmittedName);
            var call = MethodCall(Upcast(read, _structs[disposable].Id), new Lexeme("Dispose", at), false, []);
            var dispose = new ScalarStatement("if", at, new("!=", ScalarType.Bool, at, Left: read, Right: new("null", resource.Type, at)) { Wraps = _wraps },
                Body: [new ScalarStatement("expression", at, call)], Alternative: []);
            var guarded = new ScalarStatement("try", at, Body: body.Body, Alternative: [new ScalarStatement("finally", at, Body: [dispose])]);
            return (new ScalarStatement("block", at, Body: [new ScalarStatement("local", at, value, resource.EmittedName, resource.Type) { Mutable = false }, guarded]), body.Returns);
        }
        private void CheckDisposable(ScalarType type, SourceLocation at, int disposable)
        {
            if (disposable >= 0 && IsClass(type) && (IndexOf(type) == disposable || Derives(_structs, IndexOf(type), disposable))) return;
            if (disposable >= 0 && IsStruct(type) && !IsClass(type) && Derives(_structs, IndexOf(type), disposable))
                Fail("AI3100", "using over a struct resource is not supported yet.", at);
            Fail("AI3107", $"'{Show(type)}': the type used in a using statement must implement IDisposable.", at);
        }
        private sealed class LoopFlags { public bool Breaks; public bool Continues; public bool IsSwitch; public List<HashSet<string>> BreakStates { get; } = []; }
        private readonly FunctionDeclaration _function;
        private readonly ScalarFeatures _features;
        private readonly IReadOnlyList<FunctionDeclaration> _functions;
        private List<Lexeme> _tokens;
        private int _position;
        private int _nextId;
        private bool _contract;
        private readonly Stack<LoopFlags> _loops = new();
        private readonly List<Dictionary<string, Local>> _scopes = [new(StringComparer.Ordinal)];
        private HashSet<string> _assigned = [];
        private ScalarType _returnType;
        private bool _wraps;
        // Compile-time constants: folded values are BigInteger (integers) or bool; keyed by node identity / emitted local name.
        private readonly Dictionary<ScalarExpression, object> _folded = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, object> _constLocals = new(StringComparer.Ordinal);
        private Lexeme Current => _tokens[_position];
        private readonly List<ScalarEnum> _enums = activeEnums ?? [];
        private readonly List<ScalarStruct> _structs = activeStructs ?? [];
        // Struct member context: the declaring struct, whether this is its constructor, and the 'this' local.
        private readonly ScalarStruct? _struct;
        private bool _ctor;
        private readonly string? _boxedMethodTarget;
        private readonly bool _static;
        private readonly bool _readonly;
        private readonly ScalarParameterMode _receiverMode;
        private readonly bool _cctor;
        private readonly string? _qualifiedName;
        private Local? _this;
        // Constructor binding as K.init(this, params): the base constructor runs first (explicit 'base.new(args)' as the first statement, or implicit).
        private readonly bool _init;
        private bool _baseCallAllowed, _baseCalled, _inBaseArgs;

        public ExtendedBinder(FunctionDeclaration function, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> all, ScalarStruct owner, ScalarStructMethod method, bool init = false)
            : this(function, features, all)
        { _boxedMethodTarget=method.BoxedMethodTarget; _recordConstructor=method.IsConstructor&&RecordConstructorsEnabled(owner)?method:null; _struct = owner; _static = method.IsStatic; _recordInitialization = owner.IsRecord && method.Name == RecordInitializerName(owner); _recordFieldsEquality=method.IsRecordEqualityComparer; _recordEqualityApi=method.RecordEqualityApi; _recordHashApi=method.IsRecordHashApi; _readonly = method.IsReadonly; _receiverMode = method.ReceiverMode; _cctor = method.IsStaticConstructor; _ctor = method.IsConstructor; _qualifiedName = init ? _recordConstructor is not null?RecordConstructorInitName(method):owner.Name + ".init" : method.QualifiedName; _init = init; _accessor = method.IsAccessor; _initAccessor = method.IsInitAccessor; _backing = method.Backing;
          if (owner.TypeArguments is { Count: > 0 } arguments)
          {
              var combined = new Dictionary<string, ScalarType>(arguments);
              if (_typeArgs is not null) foreach (var pair in _typeArgs) combined[pair.Key] = pair.Value;
              _typeArgs = combined; _checkOnly |= owner.IsCheckInstance;
          } }
        public ExtendedBinder(FunctionDeclaration function, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> all, ScalarStruct owner, ScalarStructMethod method, Dictionary<string, ScalarType> arguments, string name)
            : this(function, features, all, owner, method)
        { _typeArgs = arguments; _checkOnly = false; _qualifiedName = name; }
        // Property accessors: the contextual 'field' is the hidden backing field; reads of a property (getter calls or backing-field reads) are
        // remembered with their receiver so that an assignment, compound assignment or ++/-- on them becomes a setter call or a backing store.
        private readonly bool _accessor;
        private bool _initAccessor;
        private readonly string? _backing;
        private readonly Dictionary<ScalarExpression, (ScalarExpression Receiver, ScalarProperty Property, IReadOnlyList<ScalarExpression> Indices)> _propertyReads = new(ReferenceEqualityComparer.Instance);
        private ScalarExpression PropertyRead(ScalarExpression receiver, ScalarProperty property, Lexeme member, IReadOnlyList<ScalarExpression>? indices = null)
        {
            var owner = _structs[property.Owner];
            CheckPropertyAccess(receiver,owner,property,member);
            if (property.GetPrivate && property.ReferenceMode != ScalarParameterMode.Value && !SameTypeScope(owner)) Fail("AI3119", "The get accessor is inaccessible.", member.Location);
            ScalarExpression read;
            if (property.IsStatic != (receiver.Operation == "static-type")) Fail("AI3106", property.IsStatic ? "A static property must be accessed through its type." : "An instance property requires an object receiver.", member.Location);
            indices ??= [];
            if (property.Getter is { } getter) read = AccessorCall(receiver, property, getter, indices, member.Location);
            else if (!property.HasGet) read = new("property-target", property.Type, member.Location);
            else
            {
                read = PropertyBackingRead(receiver, property, member.Location);
            }
            _propertyReads[read] = (receiver, property, indices);
            return read;
        }
        private static bool IsPure(ScalarExpression e) => e.Operation is "local" or "static-type" || e.Operation is "field" or "upcast" && IsPure(e.Left!);
        // P = v, P op= v and P++/P-- on a property with an accessor body: the setter call K.set_P(receiver, value), or a store into the backing field.
        private ScalarExpression PropertyAssign(ScalarExpression read, string op, ScalarExpression right, SourceLocation at, bool postfix = false, bool captureUpdate = false, bool initializing = false)
        {
            var (receiver, property, indices) = _propertyReads[read];
            var owner = _structs[property.Owner];
            if (property.ReferenceMode != ScalarParameterMode.Value)
            {
                Write(read);
                return op == "=" ? ReferenceWrite(read, right, at) : ReferenceUpdate(read, op[..^1], right, postfix, at);
            }
            var constructorBacking = _lambdaNest == 0 && SameTypeScope(owner) && property.Backing is not null
                && (property.IsStatic && _cctor || !property.IsStatic && property.IsAutomatic && _ctor && receiver.Operation == "local" && receiver.Value == _this?.EmittedName);
            if (!property.HasSet && !constructorBacking) Fail("AI3105", $"Property '{owner.Name}.{property.Name}' cannot be assigned to -- it is read only.", read.Location);
            if (!property.HasSet && constructorBacking && property.IsStatic)
                staticPropertyConstructorWrites!.Add((StaticPropertyDeclaration(owner), property.Backing!, read.Location));
            if (property.SetPrivate && !SameTypeScope(owner))
                Fail("AI3119", $"The property '{owner.Name}.{property.Name}' cannot be used in this context because the set accessor is inaccessible.", read.Location);
            if (property.IsInit && !initializing && !((_ctor || _initAccessor) && receiver.Operation == "local" && _this is not null && receiver.Value == _this.EmittedName))
                Fail("AI3105", $"Init-only property '{owner.Name}.{property.Name}' can be assigned only during initialization.", read.Location);
            if (property.Setter is { } targetSetter && (property.IsIndexer || !IsPure(receiver) || captureUpdate || op != "=" && !IsClass(receiver.Type) && Has(ScalarFeatures.MutableStructAccessors))) return AccessorMutation(read, receiver, property, indices, targetSetter, op, right, postfix, at);
            var value = op == "=" ? Coerce(right, property.Type) : CompoundValue(read, op[..^1], right, at);
            if (property.Setter is { } setter) return AccessorCall(receiver, property, setter, [.. indices, value], at);
            var target = PropertyBackingRead(receiver, property, read.Location);
            Read(RootOf(target)); Write(target);
            return new("=", property.Type, at, op == "=" ? null : "compound", Left: target, Right: value) { Wraps = _wraps };
        }
        private static ScalarContract TrueContract(SourceLocation at) => new(ContractKind.Ensures, new ScalarExpression("literal", ScalarType.Bool, at, "true"), "true", at);
        private string Fresh() => $"__scalar_local_{_nextId++}";
        // K.new(params): allocate a zeroed K, run K.init on it, return it.
        public ScalarFunction ConstructorWrapper()
        {
            var at = _function.Location; var k = _struct!;
            var parameters = _function.Parameters.Select(p => SignatureParameter(p, at)).ToList();
            var t = Fresh(); var self = new ScalarExpression("local", k.Id, at, t);
            var call = new ScalarExpression("call", _recordConstructor is not null?k.Id:ScalarType.Unit, at, _recordConstructor is not null?RecordConstructorInitName(_recordConstructor):k.Name + ".init") { Arguments = [self, .. parameters.Select(p => ForwardParameter(p, at))] };
            if(_recordConstructor is not null)
            {
                var wrapper=new ScalarFunction(_recordConstructor.QualifiedName,parameters,k.Id,[new("local",at,new("new",k.Id,at),t,k.Id){Mutable=!k.IsClass},new("return",at,call)],[TrueContract(at)],at);
                return (Has(ScalarFeatures.Enums)||Has(ScalarFeatures.NativeIntegers))?Erase(wrapper):wrapper;
            }
            var f = new ScalarFunction(k.Name + ".new", parameters, k.Id,
                [new ScalarStatement("local", at, new ScalarExpression("new", k.Id, at), t, k.Id) { Mutable = false }, new ScalarStatement("expression", at, call), new ScalarStatement("return", at, self)],
                [TrueContract(at)], at);
            return (Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers)) ? Erase(f) : f;
        }
        // Slot.virtual(this, params): for each concrete class X that is a root owner, 'if this is exactly X: return X's implementation'. The caller has checked this for null.
        public ScalarFunction Dispatch(ScalarStructMethod root)
        {
            var at = root.Declaration.Location; var owner = _struct!;
            var returnType = Type(root.Declaration.ReturnType, at, true); var resultMode = ParameterPassing(root.Declaration.ReturnType).Mode;
            var self = new ScalarParameter(Fresh(), owner.Id);
            var parameters = root.Declaration.Parameters.Select(p => SignatureParameter(p, at)).ToList();
            var selfRef = new ScalarExpression("local", owner.Id, at, self.Name);
            ScalarStatement Invoke(int x)
            {
                if (owner.NativeCollectionKind is not null && ArrayElementIdentity(_structs,x) is not null)
                    return ArrayCollectionDispatch(owner,root,selfRef,parameters,returnType,at);
                if (owner.NativeEnumerableElement is { } element && ArrayElementIdentity(_structs, x) is not null && !NominalDerives(_structs, x, IndexOf(owner.Id)))
                    return ArrayEnumerableDispatch(selfRef, element, returnType, at);
                var impl = owner.IsInterface ? ResolveInterfaceImplementation(x, root).Method! : (owner.IsRecord&&Has(ScalarFeatures.RecordInheritance)||Has(ScalarFeatures.Boxing)&&root.Name is "Equals" or "GetHashCode")?FindVirtualImplementation(x,root)!:root.IsAccessor?FindAccessor(x,root)!:root.Name=="Deconstruct"&&Has(ScalarFeatures.RecordMembers)?FindDeconstructImplementation(x,root)!:FindMethod(x,root.Name)!; var implOwner = _structs[impl.Owner];
                var receiver = implOwner.Id == owner.Id ? selfRef : new ScalarExpression("downcast", implOwner.Id, at, Left: selfRef);
                var call = InterfaceImplementationCall(impl, receiver, parameters.Select(p => ForwardParameter(p, at)).ToList(), returnType, at);
                return returnType == ScalarType.Unit ? new ScalarStatement("expression", at, call) : Returning(call, resultMode, at);
            }
            var concrete = ConcreteOf(_structs, IndexOf(owner.Id)).ToList();
            List<ScalarStatement> body;
            if (concrete.Count == 0)
                // No object can reach this slot: only a null receiver could, and the caller faults on it first.
                body = [new ScalarStatement("while", at, new ScalarExpression("literal", ScalarType.Bool, at, "true"), Body: [])];
            else
            {
                body = [Invoke(concrete[^1])];
                for (var i = concrete.Count - 2; i >= 0; i--)
                    body = [new ScalarStatement("if", at, new ScalarExpression("isexact", ScalarType.Bool, at, ((int)_structs[concrete[i]].Id).ToString(CultureInfo.InvariantCulture), Left: selfRef),
                        Body: [Invoke(concrete[i])], Alternative: body)];
            }
            var f = new ScalarFunction(root.Slot + ".virtual", [self, .. parameters], returnType, body, [TrueContract(at)], at) { ReturnMode = resultMode };
            return (Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers)) ? Erase(f) : f;
        }
        private static ScalarExpression Upcast(ScalarExpression expression, ScalarType to) =>
            expression.Type == to ? expression : new("upcast", to, expression.Location, Left: expression);
        // base.new(args) or the implicit base constructor call: Base.init(this, args); a base without its own init only accepts no arguments.
        private ScalarStatement BaseConstructorCall(List<ScalarExpression>? arguments, SourceLocation at)
        {
            var b = _structs[_struct!.BaseIndex]; var bi = IndexOf(b.Id);
            var constructor = b.Methods.FirstOrDefault(m => m.IsConstructor);
            var count = arguments?.Count ?? 0;
            var expected = constructor?.Declaration.Parameters.Count ?? 0;
            if (count != expected) Fail("AI3116", $"'{b.Name}' has no constructor taking {count} argument(s){(arguments is null ? "; call 'base.new(...)' as the first statement" : "")}.", at);
            if (constructor is not null) CheckAccess(b, constructor.IsPublic, "new", at);
            if (!NeedsInit(bi)) return new ScalarStatement("block", at, Body: []);
            var values = new List<ScalarExpression>();
            for (var i = 0; i < count; i++) values.Add(BindArgument(arguments![i], Type(constructor!.Declaration.Parameters[i].Type, at), ParameterPassing(constructor.Declaration.Parameters[i].Type).Mode));
            CompleteArguments(values);
            var self = new ScalarExpression("local", _this!.Type, at, _this.EmittedName);
            return new ScalarStatement("expression", at, new ScalarExpression("call", ScalarType.Unit, at, b.Name + ".init") { Arguments = [Upcast(self, b.Id), .. values] });
        }

        // Binds one top-level enum declaration (the text from 'enum' to its closing brace).
        private readonly string? _declaration;
        private readonly SourceLocation _declarationStart;
        // Type parameters in scope: placeholders while a generic declaration is checked (_checkOnly), closed types in an instance.
        private Dictionary<string, ScalarType>? _typeArgs;
        private bool _checkOnly;
        // Type parameters of the member being called or the generic function being instantiated, resolved in its signature texts.
        private IReadOnlyDictionary<string, ScalarType>? _memberArgs;
        private ScalarType MemberType(IReadOnlyDictionary<string, ScalarType>? arguments, string text, SourceLocation location, bool allowUnit = false)
        {
            var saved = _memberArgs; _memberArgs = arguments ?? saved;
            try { return Type(text, location, allowUnit); } finally { _memberArgs = saved; }
        }
        public ExtendedBinder(FunctionDeclaration function, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> all, Dictionary<string, ScalarType> typeArguments, string instanceName)
            : this(function, features, all)
        { _typeArgs = typeArguments; _checkOnly = false; _qualifiedName = instanceName; }
        public static GenericInfo? GenericOf(FunctionDeclaration declaration) =>
            activeGenerics is not null && activeGenerics.TryGetValue(declaration.Location.Offset, out var generic) && generic.Name == declaration.Name ? generic : null;
        private bool IsOpaqueReference(ScalarType type) => IsStruct(type) && StructOf(type) is { IsTypeParameter: true, IsClass: true, ReferenceKnown: false };
        private bool IsTypeParameterType(ScalarType type) => IsStruct(type) && StructOf(type).IsTypeParameter;
        // Class tests and casts involving a type parameter are checked at run time only; an instance repeats the checked declaration.
        // C# casts from a type parameter: to its effective base classes (an upcast) or any interface; in an instance every cast is the checked declaration's.
        private bool LenientCast(ScalarType from, ScalarType to) => _typeArgs is not null && (!_checkOnly || IsTypeParameterType(from) && StructOf(to).IsInterface);
        private bool Lenient(ScalarType a, ScalarType b) => _typeArgs is not null && (!_checkOnly || IsTypeParameterType(a) || IsTypeParameterType(b));
        // C# constraint satisfaction for a closed (or placeholder) type argument.
        private bool Satisfies(ScalarType argument, ScalarStruct parameter, bool? declarationChecked = null)
        {
            if (IsNull(argument) || argument == ScalarType.Unit) return false;
            if (!ValidStaticInterfaceArgument(argument)) return false;
            if (parameter.HasValueConstraint && !IsValueType(argument)) return false;
            if (parameter.HasUnmanagedConstraint && !IsUnmanagedType(argument, [])) return false;
            if (parameter.HasConstructorConstraint && !HasDefaultConstructor(argument, declarationChecked ?? (_typeArgs is not null && !_checkOnly))) return false;
            if (parameter.ParameterBounds is { } bounds && bounds.Dependencies.Any(d => !ParameterConstraintConversion(argument, d.Type))) return false;
            if (!parameter.IsClass && parameter.Interfaces.Count == 0) return true;
            if (parameter.IsClass && !IsReference(argument)&&!(Has(ScalarFeatures.Boxing)&&argument==ScalarType.String)
                && !(IsValueType(argument) && !parameter.ReferenceKnown && parameter.BaseIndex < 0)) return false;
            if (parameter.ReferenceKnown && parameter.BaseIndex < 0 && IsOpaqueReference(argument)) return false;
            if (parameter.BaseIndex >= 0 && !(IsClass(argument) && Derives(_structs, IndexOf(argument), parameter.BaseIndex) || IsTypeParameterType(argument) && ParameterConstraintConversion(argument, _structs[parameter.BaseIndex].Id))) return false;
            foreach (var i in parameter.Interfaces) if (!(BuiltinEquatableConversion(argument,_structs[i].Id) || IsStruct(argument) && (Derives(_structs, IndexOf(argument), i) || IsTypeParameterType(argument) && ParameterConstraintConversion(argument, _structs[i].Id)))) return false;
            return true;
        }
        public ExtendedBinder(ScalarFeatures features, string declaration, SourceLocation start)
        {
            _function = null!; _features = features; _functions = []; _declaration = declaration; _declarationStart = start;
            _tokens = Lex(declaration, start, Has(ScalarFeatures.Floats) || Has(ScalarFeatures.Decimals), Has(ScalarFeatures.Chars), Has(ScalarFeatures.Strings));
        }

        public ExtendedBinder(FunctionDeclaration function, ScalarFeatures features, IReadOnlyList<FunctionDeclaration> all)
        {
            _function = function;
            _features = features;
            _functions = all;
            var body = function.Body.ScalarSource!;
            _tokens = Lex(body.Text, body.Location, Has(ScalarFeatures.Floats) || Has(ScalarFeatures.Decimals), Has(ScalarFeatures.Chars), Has(ScalarFeatures.Strings));
            if (accessorPrefixes?.TryGetValue(body, out var accessorPrefix) == true && _tokens.Count > 0) _tokens[0] = _tokens[0] with { Location = accessorPrefix };
            if (GenericOf(function) is { } generic)
            {
                // A declaration error stopped before the placeholders were created; that error is reported alone.
                if (generic.Parameters.Any(p => p.Placeholder < 0)) throw Error("AI3106", "Generic declaration not bound.", function.Location);
                _typeArgs = generic.Parameters.ToDictionary(p => p.Name, p => (ScalarType)(StructTypeBase + p.Placeholder), StringComparer.Ordinal);
                _checkOnly = true;
            }
        }
        private bool Has(ScalarFeatures feature) => (_features & feature) != 0;
        private static ScalarError Error(string code, string message, SourceLocation location) =>
            new(new(code, DiagnosticSeverity.Error, message, location));
        private void Fail(string code, string message, SourceLocation? location = null) =>
            throw Error(code, message, location ?? Current.Location);
        private bool Take(string text)
        {
            if (Current.Text != text) return false;
            if (_position < _tokens.Count - 1) _position++;
            return true;
        }
        private Lexeme Next() { var token = Current; if (_position < _tokens.Count - 1) _position++; return token; }
        private Lexeme MemberToken()
        {
            var token=Next();
            if(!IsSourceIdentifier(token.Text) || (ActiveFeatures & ScalarFeatures.EscapedIdentifiers) != 0 && ReservedTypeParameterNames.Contains(token.Text))
                Fail("AI3106","Expected a member identifier.",token.Location);
            return token;
        }
        private void Expect(string text)
        {
            if (!Take(text)) Fail("AI3106", $"Expected '{text}'.");
        }
        private void NewLines() { while (Take("\n")) { } }
        private void EndStatement()
        {
            if (Take(";")) { NewLines(); return; }
            if (Current.Text is "\n" or "}" or "") { NewLines(); return; }
            Fail("AI3106", "Expected a statement boundary; one declarator is supported.");
        }
        // float is the legacy spelling of f64 (specification: legacy AI# float stays f64); f32 is binary32.
        private static ScalarType? FloatType(string text) => text switch { "f32" => ScalarType.F32, "f64" or "double" or "float" => ScalarType.F64, _ => null };
        private ScalarType? CastType(string text) =>
            ScopedTypeArgument(text, out var typeArgument) && !IsLocalName(text) ? (IsReference(typeArgument)||Has(ScalarFeatures.Boxing) ? typeArgument : null) :
            Has(ScalarFeatures.Types) && ScalarConstants.FromSpelling(text) is { } integer && (!ScalarConstants.IsNative(integer) || Has(ScalarFeatures.NativeIntegers)) ? (ScalarConstants.IsNative(integer) ? Type(text, Current.Location) : integer) : Has(ScalarFeatures.Floats) && FloatType(text) is { } floating ? floating : Has(ScalarFeatures.Chars) && text == "char" ? ScalarType.Char : Has(ScalarFeatures.Strings) && text == "string" ? ScalarType.String : Has(ScalarFeatures.Decimals) && text == "decimal" ? ScalarType.Decimal : EnumNamed(text) is { } declared && !IsLocalName(text) ? declared.Id
            : Has(ScalarFeatures.Classes) && !IsLocalName(text) && (text == "object" || StructNamed(text) is { IsClass: true } || Has(ScalarFeatures.Boxing)&&StructNamed(text) is not null || Has(ScalarFeatures.TypeIdentity)&&text=="Type") ? Type(text, Current.Location) : null;
        private bool ScopedTypeArgument(string text,out ScalarType type)
        {
            type=default; if(!CanBindTypeParameter(text))return false;
            var name=IdentifierName(text.Trim());
            return _memberArgs is not null && _memberArgs.TryGetValue(name,out type) || _typeArgs is not null && _typeArgs.TryGetValue(name,out type);
        }
        private ScalarType Type(string text, SourceLocation location, bool allowUnit = false, bool allowStatic = false)
        {
            var type = TypeCore(text, location, allowUnit);
            CompilationTypeAccess(type,location);
            if (!allowStatic && IsStruct(type) && StructOf(type).IsStatic)
                Fail("AI3106", $"Static class '{Show(type)}' cannot be used as a value type.", location);
            return type;
        }
        private ScalarType TypeCore(string text, SourceLocation location, bool allowUnit)
        {
            var passing = ParameterPassing(text);
            if (passing.Mode != ScalarParameterMode.Value)
            {
                if (!Has(ScalarFeatures.ByReferenceParameters)) Fail("AI3100", "By-reference parameters are disabled in this profile.", location);
                if (ScopedReference(text) && !Has(ScalarFeatures.ReferenceVariables)) Fail("AI3100", "Scoped references are disabled in this profile.", location);
                if (allowUnit && (!Has(ScalarFeatures.ReferenceReturns) || ScopedReference(text))) Fail("AI3100", "This reference-return signature is unavailable in the profile.", location);
                if (allowUnit && passing.Mode is ScalarParameterMode.In or ScalarParameterMode.Out) Fail("AI3106", "A return uses ref or ref readonly.", location);
                if (allowUnit && passing.Element is "void" or "unit") Fail("AI3110", "A reference return needs a storage element type.", location);
                if (ParameterPassing(passing.Element).Mode != ScalarParameterMode.Value) Fail("AI3106", "A parameter has only one reference passing mode.", location);
                return Type(passing.Element, location, allowUnit);
            }
            if (text.StartsWith('$') && int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var identity)
                && identity >= StructTypeBase && identity < StructTypeBase + _structs.Count && _structs[identity - StructTypeBase].IsTypeParameter) return (ScalarType)identity;
            if (ScopedTypeArgument(text, out var scopedArgument)) return scopedArgument;
            if(ImportAliasType(text,location) is {} aliasType)return aliasType;
            if(CompilationTypeText(text,location) is {} compilationType)return TypeCore(compilationType,location,allowUnit);
            text=ImportQualifiedText(text,location);
            var arrayText = Regex.Replace(text, @"\s+", "");
            if (Has(ScalarFeatures.TypedArrays) && arrayText.EndsWith("[]", StringComparison.Ordinal) && arrayText != "string[]")
            {
                var arrayDepth = 0;
                for (var end = arrayText.Length; end >= 2 && arrayText.AsSpan(end - 2, 2).SequenceEqual("[]"); end -= 2) arrayDepth++;
                if (arrayDepth > 32) Fail("AI3100", "Native array type nesting exceeds 32 levels.", location);
                return TypedArray(Type(arrayText[..^2], location), location);
            }
            if (QualifiedBuiltinTypeText(text,location,allowUnit) is {} qualifiedBuiltin) return qualifiedBuiltin;
            if (Has(ScalarFeatures.Async) && activeAsync && (text.Trim() == "Task" || text.Contains('<')) && StructNamed(text.Trim()) is null && AsyncTypeText(text, location) is { } asyncType) return asyncType;
            if (Has(ScalarFeatures.Iterators) && text.Contains('<') && IteratorTypeText(text, location) is { } iteratorType) return iteratorType;
            if (Has(ScalarFeatures.TypedArrays) && text.Contains('<') && ArrayCollectionTypeText(text,location) is {} collectionType) return collectionType;
            if (Has(ScalarFeatures.Generics) && text.Contains('<') && EquatableTypeText(text,location) is {} equatableType) return equatableType;
            if (Has(ScalarFeatures.Generics) && (text.Contains('<')||text.Trim()=="IEqualityComparer") && EqualityComparerTypeText(text,location) is {} comparerType) return comparerType;
            if (Has(ScalarFeatures.Generics) && text.Contains('<')) return GenericEnumType(text.Trim(),location) ?? GenericType(text.Trim(), location);
            if (Has(ScalarFeatures.Generics) && CanBindTypeParameter(text) && StructNamedAny(text) is { TypeParameters.Count: > 0 } open)
                Fail("AI3106", $"The generic type '{text}' requires {open.TypeParameters.Count} type argument(s).", location);
            if (Regex.Replace(text, @"\s+", "") == "string[]") return StringArrayType(location);
            if (text is "int" or "i32") return ScalarType.Int;
            if (text == "bool") return ScalarType.Bool;
            if (Has(ScalarFeatures.Types) && ScalarConstants.FromSpelling(text) is { } integer)
            {
                if (ScalarConstants.IsNative(integer))
                {
                    if (StructNamed(text) is { } shadow) return shadow.Id;
                    if (!Has(ScalarFeatures.NativeIntegers)) Fail("AI3106", $"Native integer type '{text}' is unavailable in this profile.", location);
                    _nativeTarget.Used = true;
                }
                return integer;
            }
            if (Has(ScalarFeatures.Floats) && FloatType(text) is { } floating) return floating;
            if (Has(ScalarFeatures.Chars) && text == "char") return ScalarType.Char;
            if (Has(ScalarFeatures.Strings) && text == "string") return ScalarType.String;
            if (Has(ScalarFeatures.Decimals) && text == "decimal") return ScalarType.Decimal;
            if (Has(ScalarFeatures.Classes) && text == "object") return (ScalarType)ObjectTypeId;
            if (text is "unit" or "void")
            {
                if (!allowUnit) Fail("AI3110", $"Unsupported scalar type '{text}'.", location);
                return ScalarType.Unit;
            }
            if (EnumNamed(text) is { } declared) return declared.Id;
            if (StructNamed(text) is { } structure) return structure.Id;
            if(Has(ScalarFeatures.TypeIdentity)&&text == "Type")return NativeType(location);
            if (Has(ScalarFeatures.Classes) && text == "object") return (ScalarType)ObjectTypeId;
            if (allowUnit && text is "unit" or "void") return ScalarType.Unit;
            if(ImportedSimpleType(text,location,allowUnit) is {} imported)return imported;
            Fail(text is "unit" or "void" ? "AI3110" : "AI3106", $"Unsupported scalar type '{text}'.", location);
            return ScalarType.Unit;
        }
        public ScalarFunction Bind()
        {
            if(_boxedMethodTarget is not null)return BindBoxAdapter();
            if(DefaultComparerMethod(_struct,_function.Name))return BindDefaultComparerMethod();
            if (!IsIdentifier(_function.Name))
                Fail("AI3106", "Unsupported scalar function identifier.", _function.Location);
            if (_function.IsAsync && !Has(ScalarFeatures.Async) || _function.Effects.Count != 0)
                Fail("AI3106", "Async and effectful functions are outside the scalar capability.", _function.Location);
            _returnType = _recordInitialization || _ctor && _struct is { Template: >= 0 } ? _struct!.Id : Type(_function.ReturnType, _function.Location, true);
            _returnMode = ParameterPassing(_function.ReturnType).Mode;
            if (_returnMode != ScalarParameterMode.Value && (!Has(ScalarFeatures.ReferenceReturns) || _function.IsAsync || ScopedReference(_function.ReturnType)))
                Fail("AI3100", "Reference return is unavailable in this declaration/profile.", _function.Location);
            if (_returnMode is ScalarParameterMode.Out or ScalarParameterMode.In) Fail("AI3106", "A return uses ref or ref readonly.", _function.Location);
            if (Has(ScalarFeatures.Iterators) && !_ctor && BodyHasYield() && IteratorShape(_returnType) is { } iteratorShape)
            {
                if (_accessor) Fail("AI3100", "Iterator property accessors are not supported yet.", _function.Location);
                if (_typeArgs is not null || _struct is { Template: >= 0 }) Fail("AI3100", "Generic iterators are not supported yet.", _function.Location);
                _iterElem = iteratorShape.Element;
            }
            if (_function.IsAsync)
            {
                if (_struct is null && _function.Name == "main") Fail("AI3100", "An async entry point is not supported yet.", _function.Location);
                if (_returnType == ScalarType.Unit) Fail("AI3100", "async void functions are not supported yet; return Task.", _function.Location);
                if (!IsTaskType(_returnType)) Fail("AI3106", "The return type of an async method must be void, Task or Task<T>.", _function.Location);
                _asyncTask = _returnType; _asyncElem = TaskElement(_returnType);
            }
            var parameters = new List<ScalarParameter>();
            if (_struct is not null && !_static)
            {
                if (_ctor && _returnType != _struct.Id) Fail("AI3106", "A constructor returns its struct.", _function.Location);
                // Struct receiver modes select writable storage or readonly defensive copies. An async struct method
                // hoists its receiver by value and can mutate that copy unless explicitly readonly.
                // In a class, 'this' is an immutable reference (fields are assigned through it); a class constructor starts from a newly allocated zeroed object.
                _this = new Local($"__scalar_local_{_nextId++}", _struct.Id, !_struct.IsClass && (_ctor || (_function.IsAsync || Has(ScalarFeatures.MutableStructMethods)&&_iterElem is not null) && !_readonly || _receiverMode == ScalarParameterMode.Ref));
                _scopes[0]["$this"] = _this;
                if (!_ctor || _init)
                {
                    parameters.Add(new(_this.EmittedName, _this.Type) { Mode = _receiverMode, Scoped = _receiverMode != ScalarParameterMode.Value }); _assigned.Add(_this.EmittedName);
                    if (_receiverMode != ScalarParameterMode.Value) { _referenceParameters[_this.EmittedName] = _receiverMode; _referenceContexts[_this.EmittedName] = 2; }
                }
                if (_init&&_recordConstructor is null) _returnType = ScalarType.Unit;
            }
            foreach (var parameter in _function.Parameters)
            {
                if (_recordInitialization && parameter.Name == "$record_self")
                {
                    _recordSelf = new Local($"__scalar_local_{_nextId++}",_struct!.Id,!_struct.IsClass);
                    parameters.Add(new(_recordSelf.EmittedName,_recordSelf.Type));_assigned.Add(_recordSelf.EmittedName);continue;
                }
                if (parameter.Ownership != ParameterOwnership.Unspecified)
                    Fail("AI3106", "Ownership parameters are outside the scalar capability.", _function.Location);
                var mode = ParameterPassing(parameter.Type).Mode;
                if (mode != ScalarParameterMode.Value && (_function.IsAsync || _iterElem is not null))
                    Fail("AI3106", "Async functions and iterators cannot have by-reference parameters.", _function.Location);
                var local = Declare(parameter.Name, Type(parameter.Type, _function.Location), mode is not (ScalarParameterMode.In or ScalarParameterMode.RefReadonly), parameter.NameLocation ?? _function.Location);
                parameters.Add(new(local.EmittedName, local.Type) { Mode = mode, Scoped = ScopedReference(parameter.Type) });
                _referenceContexts[local.EmittedName] = mode is ScalarParameterMode.Ref or ScalarParameterMode.In or ScalarParameterMode.RefReadonly && !ScopedReference(parameter.Type) ? 1 : 2;
                if (mode != ScalarParameterMode.Value) _referenceParameters.Add(local.EmittedName, mode);
                if (mode != ScalarParameterMode.Out) _assigned.Add(local.EmittedName);
            }
            if (_struct is null && _function.Name == "main" && parameters.Any(p => IsTypedArray(p.Type)))
                Fail("AI3106", "The native entry argument must be string[].", _function.Location);
            if(_recordConstructor is not null)
            {
                var index=IndexOf(_struct!.Id);var current=_structs[index];var abi=parameters.Skip(1).Select(p=>p with{Type=EraseType(p.Type)}).ToArray();
                _structs[index]=current with{Methods=current.Methods.Select(m=>m.QualifiedName==_recordConstructor.QualifiedName?m with{RecordConstructorParameters=abi}:m).ToArray()};
            }
            var contractTokens = _tokens;
            var contracts = new List<ScalarContract>();
            foreach (var contract in _function.Contracts)
            {
                _tokens = Lex(contract.Expression, contract.ExpressionLocation ?? contract.Location, Has(ScalarFeatures.Floats) || Has(ScalarFeatures.Decimals), Has(ScalarFeatures.Chars), Has(ScalarFeatures.Strings));
                _position = 0;
                _contract = true;
                _scopes.Add(new(StringComparer.Ordinal));
                if (contract.Kind != ContractKind.Requires && _returnType != ScalarType.Unit)
                {
                    _scopes[^1]["result"] = new("__scalar_result", _returnType, false);
                    _assigned.Add("__scalar_result");
                }
                var expression = Expression();
                NewLines();
                if (Current.Text != "") Fail("AI3100", "Unsupported contract expression.");
                Require(expression, ScalarType.Bool, "AI3040");
                contracts.Add(new(contract.Kind, expression, contract.Expression, contract.Location));
                _scopes.RemoveAt(_scopes.Count - 1);
                _assigned.Remove("__scalar_result");
            }
            _tokens = contractTokens; _position = 0; _contract = false;
            // The implicit constructor of a derived class (no source text) carries the proven 'ensures true'.
            if (_init && _function.Contracts.Count == 0 && _function.Body.ScalarSource?.Text == "" || _accessor || _cctor && _function.Contracts.Count == 0) contracts.Add(TrueContract(_function.Location));
            if (_ctor) _assigned.Add(_this!.EmittedName);
            _baseCallAllowed = _recordConstructor is null&&_init && _struct!.BaseIndex >= 0;
            if (_asyncElem is { } awaitedResult) _returnType = awaitedResult;
            var staticInitializers = _cctor ? BindStaticInitializers() : [];
            var recordConstructorPrefix=_recordConstructor is not null?RecordConstructorPrefix():[];
            if(_recordConstructor is not null&&_pendingDeclarations.Count>0)
            {
                recordConstructorPrefix=[.._pendingDeclarations,..recordConstructorPrefix];_pendingDeclarations.Clear();
            }
            var (body, returns) = _recordInitialization ? (BindRecordInitializers(),true) : _recordFieldsEquality ? (BindRecordFieldsEquality(),true) : _recordEqualityApi is not null ? (BindRecordEqualityApi(),true) : _recordHashApi ? (BindRecordHashApi(),true) : _struct is {IsNativeType:true}&&_function.Name=="GetHashCode" ? (BindNativeTypeHash(),true) : _struct is {IsNativeType:true}&&_function.Name=="ToString" ? (BindNativeTypeName(),true) : Block(false);
            if (_cctor) body = [.. staticInitializers, .. body];
            if (_iterElem is not null) { body = LowerIterator(parameters, body); returns = true; }
            if (_asyncTask is { } asyncTask)
            {
                if (_returnType != ScalarType.Unit && !returns) Fail("AI3108", "A reachable path has no return value.", _function.Location);
                _returnType = asyncTask; body = LowerAsync(parameters, body); returns = true;
            }
            if (Current.Text != "") Fail("AI3106", "Unexpected closing brace.");
            if(_recordConstructor is not null)
            {
                body=[..recordConstructorPrefix,..body,..returns?Array.Empty<ScalarStatement>():[new("return",_function.Location,new ScalarExpression("local",_struct!.Id,_function.Location,_this!.EmittedName))]];returns=true;
            }
            else if (_init)
            {
                if (_struct!.BaseIndex >= 0 && !_baseCalled) body = [BaseConstructorCall(null, _function.Location), .. body];
                body = [.. BindInstanceInitializers(), .. body];
                returns = false;
            }
            else if (_ctor)
            {
                var at = _function.Location;
                var self = new ScalarExpression("local", _struct!.Id, at, _this!.EmittedName);
                body = [new ScalarStatement("local", at, new ScalarExpression("new", _struct.Id, at), _this.EmittedName, _struct.Id) { Mutable = !_struct.IsClass },
                    .. BindInstanceInitializers(), .. body, .. returns ? Array.Empty<ScalarStatement>() : [new ScalarStatement("return", at, self)]];
                returns = true;
            }
            if (_returnType != ScalarType.Unit && !returns)
                Fail("AI3108", "A reachable path has no return value.", _function.Location);
            if (_pendingDeclarations.Count > 0) Fail("AI3100", "Pattern variables are not supported in contracts.", _pendingDeclarations[0].Location);
            // N19: locals and parameters captured by a lambda live in cells (C# closure variables are shared, not copied).
            if (_cells.Count > 0 && !_checkOnly) { var (cellParameters, cellBody) = Cellify(parameters, body, null); parameters = cellParameters; body = cellBody; }
            if (_recordInitialization) LiftRecordFactory(parameters);
            var exitDiagnostic = ValidateReferenceExits(new ScalarFunction(_function.Name, parameters, _returnType, body, contracts, _function.Location), _structs);
            if (exitDiagnostic is not null) throw new ScalarError(exitDiagnostic);
            var bound = new ScalarFunction(_qualifiedName ?? _function.Name, parameters, _returnType, body, contracts, _function.Location) { ReturnMode = _returnMode };
            return (Has(ScalarFeatures.Enums) || Has(ScalarFeatures.NativeIntegers)) ? Erase(bound) : bound;
        }

        // ---- enums: declarations, helpers and erasure ----
        private static bool IsEnum(ScalarType type) => (int)type >= EnumTypeBase && (int)type < EnumTypeBase + 1000;
        private ScalarEnum EnumOf(ScalarType type) => _enums[(int)type - EnumTypeBase];
        private ScalarEnum? EnumNamed(string name) => Has(ScalarFeatures.Enums) ? _enums.FirstOrDefault(e => e.Name == IdentifierName(name)) ?? ImportedEnumeration(name) ?? CompilationEnumeration(name) : null;
        private bool IsLocalName(string name) => _scopes.Any(scope => scope.ContainsKey(IdentifierName(name)));
        private string Show(ScalarType type) => (int)type == LambdaTypeId ? "lambda expression" : (int)type == MethodGroupTypeId ? "method group" : IsEnum(type) ? EnumOf(type).Name : IsStruct(type) ? (StructOf(type).ArrayElementType is { } element ? Show(element) + "[]" : StructOf(type).IsStringArray ? "string[]" : DisplayName(StructOf(type).Name)) : IsNull(type) ? "<null>" : IsObject(type) ? "object" : type.ToString();
        // A retype changes only the static type (enum to underlying or back); erasure removes it.
        private ScalarExpression Retype(ScalarExpression expression, ScalarType to) => Fold(new("retype", to, expression.Location, Left: expression) { Wraps = _wraps });
        private static bool IsStruct(ScalarType type) => (int)type >= StructTypeBase && (int)type < StructTypeBase + 1000;
        private bool IsClass(ScalarType type) => IsStruct(type) && StructOf(type).IsClass;
        private static bool IsNull(ScalarType type) => (int)type == NullTypeId;
        private static bool IsObject(ScalarType type) => (int)type == ObjectTypeId;
        private bool IsReference(ScalarType type) => IsClass(type) || IsObject(type);
        private ScalarStruct StructOf(ScalarType type) => _structs[(int)type - StructTypeBase];
        private ScalarStruct? StructNamed(string name) => StructNamedAny(name) is { TypeParameters.Count: 0 } found ? found : null;
        private ScalarStruct? StructNamedAny(string name) => Has(ScalarFeatures.Structs) ? _structs.FirstOrDefault(s => s.Name == IdentifierName(name) && !s.IsTypeParameter && (s.Template < 0 || s.Template == IndexOf(s.Id))) ?? ImportedStructure(name) ?? CompilationStructure(name) : null;
        private Local? LocalNamed(string name)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--) if (_scopes[i].TryGetValue(IdentifierName(name), out var local)) return local;
            return null;
        }
        private void CheckAccess(ScalarStruct owner, bool isPublic, string member, SourceLocation location)
        {
            if (!isPublic && !SameTypeScope(owner))
                Fail("AI3119", $"'{owner.Name}.{member}' is inaccessible due to its protection level.", location);
        }
        private static bool IsIdentifier(string text) => (ActiveFeatures & ScalarFeatures.UnicodeIdentifiers) != 0 ? IdentifierSpelling.Valid(text,allowAt:false) : Regex.IsMatch(text, @"^[A-Za-z_][A-Za-z0-9_]*$");
        private static bool IsSourceIdentifier(string text) => IsIdentifier(text) || IsEscapedIdentifier(text);
        public ScalarStruct BindStructName(int index)
        {
            var isAbstract = false; var isSealed = false; var isStatic = false;var isReadonly=false;SourceLocation? readonlyAt=null;
            while (Current.Text is "abstract" or "sealed" or "readonly" || Current.Text == "static" && Has(ScalarFeatures.StaticMembers))
            {
                var modifier = Next();
                if(modifier.Text=="readonly")
                {
                    if(!Has(ScalarFeatures.ReadonlyStructs))Fail("AI3100","Readonly struct declarations are disabled in this profile.",modifier.Location);
                    if(isReadonly)Fail("AI3106","Duplicate modifier 'readonly'.",modifier.Location);
                    isReadonly=true;readonlyAt=modifier.Location;continue;
                }
                if (modifier.Text == "static") { if (isStatic) Fail("AI3106", "Duplicate modifier 'static'.", modifier.Location); isStatic = true; continue; }
                if (modifier.Text == "abstract" ? isAbstract : isSealed) Fail("AI3106", $"Duplicate modifier '{modifier.Text}'.", modifier.Location);
                if (modifier.Text == "abstract") isAbstract = true; else isSealed = true;
                if (isAbstract && isSealed) Fail("AI3106", "A class cannot be both abstract and sealed.", modifier.Location);
            }
            var keyword = Next(); // struct, class, interface or record
            if (isStatic && (keyword.Text != "class" || isAbstract || isSealed)) Fail("AI3106", "A static class cannot have abstract or sealed modifiers.", keyword.Location);
            var isRecord = keyword.Text == "record"; var recordStruct = false;
            if (isRecord)
            {
                if ((isAbstract || isSealed)&&!Has(ScalarFeatures.RecordInheritance)) Fail("AI3100", "Abstract and sealed records are not supported yet (records cannot be inherited).", keyword.Location);
                if (Current.Text is "struct" or "class") recordStruct = Next().Text == "struct";
                if(recordStruct&&(isAbstract||isSealed))Fail("AI3106","A record struct cannot be abstract or sealed.",keyword.Location);
            }
            if(isReadonly&&keyword.Text!="struct"&&!recordStruct)Fail("AI3106","Readonly type declarations require a struct or record struct.",readonlyAt!);
            if(keyword.Text=="struct"&&(isAbstract||isSealed))Fail("AI3106","A struct cannot be abstract or sealed.",keyword.Location);
            if(keyword.Text is "interface" or "enum"&&(isAbstract||isSealed||isStatic))Fail("AI3106","This type declaration cannot use class modifiers.",keyword.Location);
            var name = Next();
            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || IsTypeKeyword(name.Text) || name.Text is "this" or "new" or "null" or "object" or "class" or "struct" or "interface" or "base"
                or "abstract" or "sealed" or "virtual" or "override")
                Fail("AI3106", $"Invalid {keyword.Text} name '{name.Text}'.", name.Location);
            if (_structs.Any(e => e.Name == IdentifierName(name.Text))) Fail("AI3106", $"Duplicate type '{name.Text}'.", name.Location);
            var typeParameters = new List<string>(); var parameterDefinitions = new List<TypeParameterInfo>(); var typeVariances = new List<int>();
            if (Current.Text == "<" && Has(ScalarFeatures.Generics))
            {
                var open = Next();
                do
                {
                    var parameter = Next();
                    var variance = 0;
                    if (parameter.Text is "in" or "out")
                    {
                        if (keyword.Text != "interface") Fail("AI3106", "Variance annotations are allowed only on interface type parameters.", parameter.Location);
                        variance = parameter.Text == "out" ? 1 : -1; parameter = Next();
                    }
                    if (!ValidTypeParameterName(parameter.Text))
                        Fail("AI3106", $"Invalid type parameter name '{parameter.Text}'.", parameter.Location);
                    if (typeParameters.Contains(parameter.Name) || parameter.Name == name.Name) Fail("AI3106", $"Duplicate type parameter '{parameter.Text}'.", parameter.Location);
                    typeParameters.Add(parameter.Name); parameterDefinitions.Add(new(parameter.Name, parameter.Location)); typeVariances.Add(variance);
                } while (Take(","));
                Expect(">");
                var definition = new GenericInfo(name.Name, name.Location.Offset, parameterDefinitions);
                var primaryStart = _position;
                if (isRecord && Current.Text == "(") SkipBalanced("(", ")");
                BindTypeConstraints(definition); typeGenerics![index] = definition;
                var afterConstraints = _position;
                var declaredBases = new List<(string Name, SourceLocation Location)>();
                if (Take(":"))
                {
                    do { var baseType = HeaderTypeToken(); declaredBases.Add((baseType.Text, baseType.Location)); if(isRecord&&Current.Text=="(")SkipBalanced("(",")"); } while (Take(","));
                    BindTypeConstraints(definition);
                }
                genericBases![index] = declaredBases; _position = isRecord ? primaryStart : afterConstraints;
            }
            var delegateDeclaration = DelegateDeclaration(name.Name, out var delegateFunction);
            return new(IdentifierName(name.Text), (ScalarType)(StructTypeBase + index), [], [], name.Location, keyword.Text is "class" or "interface" || isRecord && !recordStruct)
            { SourceNamespace=CompilationBinding(name.Name)?.Namespace, SourceName=CompilationBinding(name.Name)?.Name, SourceFullName=CompilationBinding(name.Name)?.FullName, SourceRuntimeFullName=CompilationBinding(name.Name)?.RuntimeFullName, SourceOwnArity=CompilationBinding(name.Name)?.Arity, SourceTypeParameterNames=CompilationBinding(name.Name)?.TypeParameters??[], HasRecordPrimaryConstructor = isRecord && Current.Text == "(", IsStatic = isStatic, IsReadonly=isReadonly, IsRecord = isRecord, IsAbstract = isAbstract, IsSealed = isSealed, IsInterface = keyword.Text == "interface", TypeParameters = typeParameters, TypeVariances = delegateDeclaration?.TypeParameters.Select(p => p.Variance).ToArray() ?? (IReadOnlyList<int>)typeVariances, IsDelegate = delegateDeclaration is not null && !delegateFunction, IsDelegateFunction = delegateFunction };
        }
        // An instance binder starts after the header of the generic declaration it re-binds.
        public void SkipStructHeader()
        {
            while (Current.Text is "abstract" or "sealed" or "static" or "readonly") Next();
            var keyword = Next();
            if (keyword.Text == "record" && Current.Text is "class" or "struct") Next();
            Next();
            if (Take("<")) { while (Current.Text is not ">" and not "") Next(); Expect(">"); }
            if (keyword.Text == "record") return;
            while (Current.Text is not "{" and not "") Next();
        }
        public int LastPositional;
        public void UseTypeArguments(Dictionary<string, ScalarType> map, bool checkOnly) { _typeArgs = map; _checkOnly = checkOnly; }
        // Name<A, B>: the closed instance of a generic class or struct, created (and its fields bound) on first use.
        private ScalarType InstanceOf(int template, IReadOnlyList<ScalarType> arguments, SourceLocation at)
        {
            CheckTypeArguments(template, arguments, at);
            var existing = _structs.FindIndex(x => x.Template == template && x.Template >= 0 && x.TypeArgumentList.SequenceEqual(arguments));
            if (existing >= 0) return _structs[existing].Id;
            if (_structs.Count(x => x.Template >= 0) > 256) Fail("AI3100", "More than 256 generic type instantiations are not supported.", at);
            var declaration = _structs[template]; var index = _structs.Count;
            if (index >= 1000) Fail("AI3100", "Too many types.", at);
            var name = declaration.Name + "<" + string.Join(", ", arguments.Select(Show)) + ">";
            var map = new Dictionary<string, ScalarType>(StringComparer.Ordinal);
            for (var i = 0; i < arguments.Count; i++) map[declaration.TypeParameters[i]] = arguments[i];
            var check = arguments.Any(a => IsStruct(a) && StructOf(a) is { IsTypeParameter: true } or { IsCheckInstance: true });
            _structs.Add(new(name, (ScalarType)(StructTypeBase + index), [], [], declaration.Location, declaration.IsClass)
            { SourceNamespace=declaration.SourceNamespace, SourceName=declaration.SourceName, SourceFullName=declaration.SourceFullName, SourceRuntimeFullName=declaration.SourceRuntimeFullName, SourceOwnArity=declaration.SourceOwnArity, SourceTypeParameterNames=declaration.SourceTypeParameterNames, Template = template, IsRecord = declaration.IsRecord, HasRecordPrimaryConstructor = declaration.HasRecordPrimaryConstructor, TypeParameters = declaration.TypeParameters, TypeVariances = declaration.TypeVariances, IsDelegate = declaration.IsDelegate, IsDelegateFunction = declaration.IsDelegateFunction, TypeArguments = map, TypeArgumentList = arguments.ToList(), IsCheckInstance = check, IsSealed = declaration.IsSealed, IsStatic = declaration.IsStatic, IsInterface = declaration.IsInterface, IsAbstract = declaration.IsAbstract });
            genericBases![index] = genericBases.GetValueOrDefault(template, []);
            genericBindingDepth++;
            try
            {
            // After a declaration error the generic declarations may not be registered; that error is reported alone.
            if (!templateDeclarations!.TryGetValue(template, out var registered)) Fail("AI3106", "Generic declaration not bound.", at);
            var (text, start) = registered;
            var binder = new ExtendedBinder(_features, text, start);
            binder.UseTypeArguments(map, check); binder.SkipStructHeader();
            List<ScalarStructField> fields; List<ScalarProperty> properties;
            // A member that is invalid for these type arguments is reported where the instance is named.
            try { (fields, _, _, properties) = binder.BindStructBody(_structs[index]); }
            catch (ScalarError failure) { throw Error(failure.Diagnostic.Code, $"In '{name}': {failure.Diagnostic.Message}", at); }
            _structs[index] = _structs[index] with { Fields = fields.Select(f => f with { Owner = index }).ToList(), Properties = properties.Select(p => p with { Owner = index }).ToList(), Positional = binder.LastPositional };
            if (templateMembersReady) FillInstanceMethods(index);
            }
            finally { genericBindingDepth--; }
            RefreshGenericHierarchy();
            return _structs[index].Id;
        }
        // A type written as tokens: Name or Name<T, ...> (a '>>' token closes two lists).
        private Lexeme TypeToken()
        {
            var first = TypeHeadToken();
            Lexeme ArraySuffix(Lexeme token)
            {
                var result = token.Text;
                while (Has(ScalarFeatures.TypedArrays) && Current.Text == "[" && TokenAt(_position + 1).Text == "]")
                { Next(); Next(); result += "[]"; }
                return new(result, token.Location);
            }
            if (Has(ScalarFeatures.ProcessArguments) && first.Text == "string" && Current.Text == "[" && TokenAt(_position + 1).Text == "]")
            { Next(); Next(); return ArraySuffix(new("string[]", first.Location)); }
            var iteratorInterface = QualifiedBuiltinAlias(first.Text) is NativeEquatableName or NativeEqualityComparerName or NativeEqualityComparerInterface || QualifiedBuiltinAlias(first.Text) is {} qualifiedSequence && BuiltinSequenceTypeName(qualifiedSequence) || Has(ScalarFeatures.Iterators) && first.Text is "IEnumerable" or "IEnumerator" || Has(ScalarFeatures.TypedArrays) && ArrayCollectionKinds.Contains(first.Text) || Has(ScalarFeatures.Generics) && (first.Text == "IEquatable" || BuiltinComparerTypeName(first.Text)) || Has(ScalarFeatures.Async) && activeAsync && first.Text is "Task" or "TaskCompletionSource";
            // Callers have already selected type syntax. A value with the same name
            // does not hide a generic type in annotations, creations, casts or patterns.
            if (Current.Text != "<" || !iteratorInterface && (!Has(ScalarFeatures.Generics) || StructNamedAny(first.Text) is not { TypeParameters.Count: > 0 } && EnumDefinition(first.Text) is null)) return ArraySuffix(first);
            var text = new StringBuilder(first.Text); var depth = 0;
            do
            {
                var token = Next();
                if (token.Text == "") Fail("AI3106", "Unterminated type argument list.", first.Location);
                var closing=token.Text.TakeWhile(c=>c=='>').Count();
                if(closing>0&&(closing==token.Text.Length||token.Text[closing..]=="="))
                {
                    // In a type context, consume closing brackets before a following assignment/operator.
                    var consumed=Math.Min(closing,depth);var rest=token.Text[consumed..];
                    if(rest.Length>0)_tokens.Insert(_position,new(rest,new(token.Location.Line,token.Location.Column+consumed,token.Location.Offset+consumed)));
                    text.Append(new string('>',consumed));depth-=consumed;
                    continue;
                }
                if (token.Text.All(c => c == '>')) depth -= token.Text.Length;
                else if (token.Text == "<") depth++;
                else if (token.Text is not ("," or "." or ":" or "::") && !((Has(ScalarFeatures.ProcessArguments) || Has(ScalarFeatures.TypedArrays)) && token.Text is "[" or "]") && !IsSourceIdentifier(token.Text)) Fail("AI3106", $"Unexpected '{token.Text}' in a type argument list.", token.Location);
                text.Append(token.Text == "," ? ", " : token.Text);
            } while (depth > 0);
            return ArraySuffix(new(text.ToString(), first.Location));
        }
        // Name<A, B> written as text (from a signature): ',' or ';' separate top-level arguments.
        private ScalarType GenericType(string text, SourceLocation location)
        {
            var open = text.IndexOf('<');
            var name = text[..open].Trim();
            if (!text.EndsWith('>')) Fail("AI3106", $"Unsupported type '{text}'.", location);
            var declaration = StructNamedAny(name);
            if (declaration is not { TypeParameters.Count: > 0 }) Fail("AI3106", $"'{name}' is not a generic type.", location);
            var arguments = new List<ScalarType>(); var depth = 0; var start = open + 1;
            for (var i = open + 1; i < text.Length - 1; i++)
            {
                if (text[i] == '<') depth++;
                else if (text[i] == '>') depth--;
                else if (text[i] is ',' or ';' && depth == 0) { arguments.Add(Type(text[start..i].Trim(), location)); start = i + 1; }
            }
            arguments.Add(Type(text[start..^1].Trim(), location));
            if (arguments.Count != declaration!.TypeParameters.Count)
                Fail("AI3116", $"'{name}' expects {declaration.TypeParameters.Count} type argument(s) but {arguments.Count} were supplied.", location);
            return InstanceOf(IndexOf(declaration.Id), arguments, location);
        }
        public (List<ScalarStructField> Fields, List<MemberSpan> Methods, IReadOnlyList<(string Name, SourceLocation Location)> Bases, List<ScalarProperty> Properties) BindStructBody(ScalarStruct header)
        {
            if (_enums.Any(e => e.Name == header.Name)) Fail("AI3106", $"Duplicate type '{header.Name}'.", header.Location);
            // BindStructName has consumed the modifiers, 'struct', 'class' or 'interface', and the name; a base list may follow.
            // A record may first list positional members (name: T, ...): public properties, init-only in a record class and get/set in a record struct.
            var positional = new List<ScalarStructField>();var recordParameters=new List<ParameterDeclaration>();
            if (header.IsRecord && Take("("))
            {
                if (!Take(")"))
                {
                    do
                    {
                        var member = Next();
                        if (!IsSourceIdentifier(member.Text) || IsReserved(member.Text) || IsTypeKeyword(member.Text) || member.Text is "this" or "new" or "fn")
                            Fail("AI3106", $"Invalid record parameter name '{member.Text}'.", member.Location);
                        if (positional.Any(f => f.Name == member.Name)) Fail("AI3106", $"Duplicate record parameter '{member.Text}'.", member.Location);
                        Expect(":");
                        var memberTypeToken = ParameterTypeToken();
                        var passing = ParameterPassing(memberTypeToken.Text).Mode;
                        if (passing is ScalarParameterMode.Ref or ScalarParameterMode.Out) Fail("AI3106", "Record primary parameters cannot use ref or out.",memberTypeToken.Location);
                        if (passing != ScalarParameterMode.Value && !Has(ScalarFeatures.RecordInitializers)) Fail("AI3100", "Readonly record primary parameters are disabled in this profile.",memberTypeToken.Location);
                        var memberType = Type(memberTypeToken.Text, memberTypeToken.Location);
                        if (memberType == ScalarType.String && !Has(ScalarFeatures.NullStrings)) Fail("AI3106", "A record member cannot be a string: string fields need nullable strings, which are not supported yet.", memberTypeToken.Location);
                        positional.Add(new(member.Name, memberType, EraseType(memberType), true, header.IsReadonly, member.Location) { IsInit = header.IsClass||header.IsReadonly, IsProperty = true });
                        recordParameters.Add(new(member.Text,memberTypeToken.Text));
                    } while (Take(","));
                    Expect(")");
                }
            }
            LastPositional = positional.Count;
            if(header.IsRecord&&header.IsClass&&recordParameters.Count==1&&ParameterPassing(recordParameters[0].Type).Mode==ScalarParameterMode.Value&&positional[0].Type==header.Id)
                Fail("AI3106","A record primary constructor cannot have the signature of its synthesized copy constructor.",positional[0].Location);
            _structs[IndexOf(header.Id)]=_structs[IndexOf(header.Id)] with {RecordParameters=recordParameters,RecordParameterTypes=positional.Select(f=>f.Type).ToArray(),RecordParameterStorageTypes=positional.Select(f=>f.StorageType).ToArray(),RecordParameterLocations=positional.Select(f=>f.Location).ToArray()};
            var bases = new List<(string Name, SourceLocation Location)>();
            if (Take(":"))
                do
                {
                    var baseName = HeaderTypeToken();
                    bases.Add((baseName.Text, baseName.Location));
                    if(Current.Text=="(")
                    {
                        if(!header.IsRecord||!Has(ScalarFeatures.RecordInheritance))Fail("AI3100","Record base constructor arguments are disabled in this profile.",Current.Location);
                        if(!header.HasRecordPrimaryConstructor)Fail("AI3106","Record base constructor arguments require a primary parameter list on the derived record.",baseName.Location);
                        if(bases.Count!=1)Fail("AI3106","Only the direct base class can have constructor arguments.",baseName.Location);
                        var arguments=ReadRecordBaseArguments();_structs[IndexOf(header.Id)]=_structs[IndexOf(header.Id)] with {RecordBaseArguments=arguments,RecordBaseLocation=baseName.Location};
                    }
                } while (Take(","));
            NewLines();
            if (header.TypeParameters.Count > 0 && Current.Text == "where") while (Current.Text is not "{" and not "") Next();
            var fields = new List<ScalarStructField>(positional);var customRecordMembers=new HashSet<string>(StringComparer.Ordinal);
            var staticFields = new List<ScalarStructField>();
            var staticInitializers = new List<ScalarStaticInitializer>();
            var instanceInitializers = new List<ScalarInstanceInitializer>();
            var methods = new List<MemberSpan>();
            var properties = new List<ScalarProperty>();
            if (header.IsRecord && Current.Text == "") return (fields, methods, bases, properties);
            Expect("{"); NewLines();
            while (Current.Text is not "}" and not "")
            {
                bool? isPublic = null; var isReadonly = false; var isInternal = false; var modifiers = new HashSet<string>(StringComparer.Ordinal); SourceLocation? asyncLocation = null;
                Lexeme? required = null;Lexeme? newModifier=null;
                while (Current.Text is "public" or "private" or "internal" or "protected" or "readonly" or "virtual" or "override" or "abstract" or "sealed" or "new"
                    || Current.Text == "static" && Has(ScalarFeatures.StaticMembers)
                    || Current.Text == "required" && TokenAt(_position + 1).Text != ":")
                {
                    var modifier = Next();
                    if(modifier.Text=="new")
                    {
                        if(!header.IsRecord||!Has(ScalarFeatures.RecordInheritance))Fail("AI3100","Explicit member hiding is unavailable in this profile.",modifier.Location);
                        if(newModifier is not null)Fail("AI3106","Duplicate modifier 'new'.",modifier.Location);
                        newModifier=modifier;continue;
                    }
                    if (modifier.Text == "required")
                    {
                        if (!Has(ScalarFeatures.RequiredMembers)) Fail("AI3100", "Required members are unavailable in this native profile.", modifier.Location);
                        if (required is not null) Fail("AI3106", "Duplicate modifier 'required'.", modifier.Location);
                        if (header.IsInterface) Fail("AI3106", "An interface member cannot be required.", modifier.Location);
                        required = modifier; continue;
                    }
                    if (modifier.Text == "static") { if (!modifiers.Add("static")) Fail("AI3106", "Duplicate modifier 'static'.", modifier.Location); continue; }
                    if (header.IsRecord && !Has(ScalarFeatures.RecordInheritance)&&modifier.Text is "virtual" or "override" or "abstract" or "sealed")
                        Fail("AI3100", "Virtual, override, abstract and sealed members of records are not supported yet.", modifier.Location);
                    if (header.IsInterface && modifier.Text is "internal" or "readonly" or "override" or "required") Fail("AI3100", "This interface member modifier is not supported yet.", modifier.Location);
                    if (modifier.Text is "virtual" or "override" or "abstract" or "sealed")
                    { if (!modifiers.Add(modifier.Text)) Fail("AI3106", $"Duplicate modifier '{modifier.Text}'.", modifier.Location); }
                    else if (modifier.Text == "readonly") { if (isReadonly) Fail("AI3106", "Duplicate modifier 'readonly'.", modifier.Location); isReadonly = true; }
                    else { if (isPublic is not null) Fail("AI3106", "A member has at most one visibility modifier.", modifier.Location); isPublic = modifier.Text is not ("private" or "protected"); isInternal = modifier.Text == "internal"; if(modifier.Text=="protected")modifiers.Add("protected-constructor"); }
                }
                if(modifiers.Contains("protected-constructor")&&(!header.IsRecord||!(Current.Text=="fn"&&(Has(ScalarFeatures.RecordConstructors)&&TokenAt(_position+1).Text=="new"||Has(ScalarFeatures.RecordDisplay)&&TokenAt(_position+1).Text=="PrintMembers")||Has(ScalarFeatures.TypeIdentity)&&Current.Text=="EqualityContract")))Fail("AI3106","Protected access is available on record constructors, PrintMembers and EqualityContract in this profile.",Current.Location);
                var isStatic = modifiers.Contains("static");
                if(newModifier is not null&&(modifiers.Contains("override")||Current.Text=="fn"&&TokenAt(_position+1).Text=="new"))Fail("AI3106","The new modifier cannot be combined with override or used on a constructor.",newModifier.Location);
                if (header.IsStatic && !isStatic) Fail("AI3106", "A static class can contain only static members.");
                if (isStatic && isReadonly && Current.Text == "fn") Fail("AI3106", "A static method cannot be readonly.");
                if (isStatic && modifiers.Count > 1 && !header.IsInterface) Fail("AI3106", "A static member cannot be virtual, override, abstract or sealed.");
                if (isStatic && Current.Text == "fn" && TokenAt(_position + 1).Text == "new")
                {
                    if (!Has(ScalarFeatures.StaticInitialization)) Fail("AI3100", "Static constructors are not supported yet.");
                    if (isPublic is not null) Fail("AI3106", "A static constructor cannot have a visibility modifier.");
                }
                if (required is not null)
                {
                    if (isStatic) Fail("AI3106", "Only instance fields and properties can be required.", required.Location);
                    if (isReadonly) Fail("AI3106", "A required member cannot be readonly.", required.Location);
                    if (isPublic != true) Fail("AI3106", "A required member must be accessible to callers of this type.", required.Location);
                    if (Current.Text is "fn" or "event") Fail("AI3106", "Only instance fields and properties can be required.", required.Location);
                }
                if (Current.Text == "event" && Has(ScalarFeatures.Events) && !header.IsInterface)
                {
                    BindEvent(header, isPublic ?? false, isReadonly, modifiers, isStatic ? staticFields : fields, isStatic ? fields : staticFields, methods, properties, staticInitializers, instanceInitializers, isStatic);
                    NewLines();
                    continue;
                }
                if (Current.Text == "async" && Has(ScalarFeatures.Async))
                {
                    asyncLocation = Current.Location;
                    if (modifiers.Contains("abstract")) Fail("AI3106", "An async member needs an implementation body and cannot be abstract or an interface signature.", Current.Location);
                    if (isReadonly && !isStatic && header.IsClass) Fail("AI3106", "Only a struct instance method can be readonly.", Current.Location);
                    if (isReadonly) modifiers.Add("readonly");
                    Next(); modifiers.Add("async");
                    if (Current.Text != "fn") Fail("AI3106", "Expected fn after async.");
                    if (isStatic && isReadonly) Fail("AI3106", "A static method cannot be readonly.");
                }

                if(Current.Text=="fn"&&isReadonly&&(activeFeatures&ScalarFeatures.MutableStructMethods)!=0)
                {
                    if(header.IsClass)Fail("AI3106","Only a struct instance method can be readonly.",Current.Location);
                    modifiers.Add("readonly");
                }
                if (Current.Text == "fn" && header.IsInterface) { BindInterfaceMember(header, modifiers, isPublic, methods, asyncLocation); NewLines(); continue; }
                if (Current.Text == "fn" && modifiers.Contains("abstract"))
                {
                    // fn name(p: T, ...) -> R with no body.
                    var fn = Next(); var name = Next();
                    var generic = BindMemberTypeParameters(name);
                    if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || name.Text is "this" or "new") Fail("AI3106", $"Invalid member name '{name.Text}'.", name.Location);
                    var parameters = new List<ParameterDeclaration>();
                    Expect("(");
                    if (!Take(")"))
                    {
                        do
                        {
                            var parameter = Next();
                            if (!IsIdentifier(parameter.Text) || IsReserved(parameter.Text)) Fail("AI3106", $"Invalid parameter name '{parameter.Text}'.", parameter.Location);
                            Expect(":"); var parameterType = ParameterTypeToken();
                            parameters.Add(new(parameter.Text, parameterType.Text));
                        } while (Take(","));
                        Expect(")");
                    }
                    Expect("-"); Expect(">"); var returnType = ParameterTypeToken();
                    if (generic is not null) BindTypeConstraints(generic);
                    var semicolon = Take(";");
                    if (!semicolon && Current.Text is not "\n" and not "}" && (_position == 0 || _tokens[_position - 1].Text != "\n")) Fail("AI3106", header.IsInterface ? "An interface member is a signature only: it has no contract or body." : "An abstract member has no contract or body.");
                    var signature = new FunctionDeclaration(name.Name, parameters, returnType.Text, [], [], new FunctionBody([]) { ScalarSource = new("", name.Location) }, [], name.Location);
                    methods.Add(new(name.Text, name.Location.Offset, fn.Location.Offset, fn.Location.Offset, isPublic ?? header.IsInterface, modifiers, signature){IsInternal=isInternal,IsProtectedConstructor=modifiers.Contains("protected-constructor")});
                }
                else if (Current.Text == "fn")
                {
                    // Explicit readonly metadata is retained for checked value-method receivers.
                    var fn = Next(); var name = Current; string? qualifier = null; var qualifierStart = name.Location.Offset;
                    if(name.Text=="new"&&isReadonly)Fail("AI3106","A constructor cannot have a readonly modifier.",name.Location);
                    if (QualifiedInterfaceMemberAt())
                    {
                        qualifier = HeaderTypeToken().Text; Expect("."); name = Current;
                        if (isPublic is not null || modifiers.Any(m => m != "async" && m != "static" && !(m=="readonly"&&Has(ScalarFeatures.MutableStructMethods)))) Fail("AI3106", "An explicit interface implementation takes no access or virtual modifier.", name.Location);
                    }
                    if (header.IsRecord && name.Text == "new" && !isStatic&&!Has(ScalarFeatures.RecordConstructors)) Fail("AI3100", "Constructors in records are not supported yet; the positional members define the constructor.", name.Location);
                    if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) && name.Text != "new" || name.Text == "this") Fail("AI3106", $"Invalid member name '{name.Text}'.", name.Location);
                    if (_tokens[_position + 1].Text == "<" && Has(ScalarFeatures.Generics)
                        && name.Text == "new")
                        Fail("AI3100", "Generic constructors are not supported.", _tokens[_position + 1].Location);
                    var depth = 0; Lexeme? close = null;
                    while (Current.Text != "")
                    {
                        var t = Next();
                        if (t.Text == "{") depth++;
                        else if (t.Text == "}" && --depth == 0) { close = t; break; }
                    }
                    if (close is null) Fail("AI3106", "Unterminated member function.", fn.Location);
                    methods.Add(new(qualifier is null ? name.Text : qualifier + "." + name.Text, name.Location.Offset, fn.Location.Offset, close!.Location.Offset + 1, isPublic ?? false, modifiers, null)
                        { IsInternal=isInternal, IsInternalConstructor = isInternal && name.Text == "new" && !modifiers.Contains("static"), IsProtectedConstructor=modifiers.Contains("protected-constructor"), InterfaceQualifier = qualifier, QualifierStart = qualifierStart });
                }
                else
                {
                    string? propertyQualifier = null;
                    if (QualifiedInterfaceMemberAt())
                    {
                        if (!Has(ScalarFeatures.ExtendedAccessors)) Fail("AI3100", "Explicit interface accessors are disabled in this profile.",Current.Location);
                        propertyQualifier = HeaderTypeToken().Text; Expect(".");
                        if (isPublic is not null || required is not null || modifiers.Any(m => m != "static")) Fail("AI3106", "An explicit interface accessor takes no access, required or virtual modifier.",Current.Location);
                    }
                    var name = Next();
                    var isIndexer = name.Text == "this" && Current.Text == "[";
                    if (!isIndexer && (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || IsTypeKeyword(name.Text) || name.Text is "this" or "new" or "fn"))
                        Fail("AI3106", $"Invalid field name '{name.Name}'.", name.Location);
                    var indexParameters = isIndexer ? BindIndexParameters(name, isStatic, required is not null) : new List<ParameterDeclaration>();
                    var positionalMember = propertyQualifier is null ? positional.FirstOrDefault(f=>f.Name==name.Name) : null;
                    var customPositional = positionalMember is not null && !customRecordMembers.Contains(name.Name);
                    if(customPositional)
                    {
                        if(!Has(ScalarFeatures.RecordMembers))Fail("AI3100","Custom positional record members are disabled in this profile.",name.Location);
                        if(isStatic)Fail("AI3106","A positional record member must be an instance field or readable property.",name.Location);
                        fields.Remove(positionalMember!);customRecordMembers.Add(name.Name);
                    }
                    if (propertyQualifier is null && (staticFields.Any(f => f.Name == name.Name) || fields.Any(f => f.Name == name.Name) || methods.Any(m => m.Name != "new" && CanonicalMemberName(m.Name) == name.Name)) || properties.Any(p => p.Name == (propertyQualifier is null ? name.Name : propertyQualifier + "." + name.Name) && (!isIndexer || SameIndexSignature(p.IndexParameters, indexParameters)))) Fail("AI3106", $"Duplicate member '{name.Name}' in {(header.IsClass ? "class" : "struct")} '{header.Name}'.", name.Location);
                    Expect(":");
                    var typeToken = ParameterTypeToken(); var type = Type(typeToken.Text, typeToken.Location);
                    var referenceMode = ParameterPassing(typeToken.Text).Mode;
                    if(customPositional && type!=positionalMember!.Type)Fail("AI3106","A positional record member must have the same type as its primary parameter.",name.Location);
                    if (referenceMode != ScalarParameterMode.Value && !Has(ScalarFeatures.ReferenceMembers)) Fail("AI3100", "Reference properties are disabled in this profile.", typeToken.Location);
                    if (referenceMode is ScalarParameterMode.In or ScalarParameterMode.Out || ScopedReference(typeToken.Text)) Fail("AI3106", "A property uses ref or ref readonly.", typeToken.Location);
                    if (referenceMode != ScalarParameterMode.Value && Current.Text is not ("{" or "=>")) Fail("AI3106", "A reference member needs a getter body.", name.Location);
                    if (type == ScalarType.String && !Has(ScalarFeatures.NullStrings)) Fail("AI3106", $"A {(header.IsClass ? "class" : "struct")} field cannot be a string: string fields need nullable strings, which are not supported yet.", typeToken.Location);
                    if (isStatic && Current.Text == "=" && !Has(ScalarFeatures.StaticInitialization)) Fail("AI3100", "Static field initializers are not supported yet.", Current.Location);
                    if (Current.Text is "{" or "=>" && Has(ScalarFeatures.Classes))
                    {
                        if (isReadonly && (header.IsClass || isStatic)) Fail("AI3106", "A readonly accessor requires a struct instance member.", name.Location);
                        if (isReadonly) modifiers.Add("readonly");
                        BindProperty(header, name with { Text = name.Name }, typeToken, type, isPublic ?? header.IsInterface, isStatic ? staticFields : fields, methods, properties, required is not null, isStatic, indexParameters, modifiers, propertyQualifier,isInternal);
                        if(customPositional && properties.FirstOrDefault(p=>p.Name==name.Name) is {HasGet:false})Fail("AI3106","A positional record property requires a getter.",name.Location);
                        if (!isStatic && Take("="))
                        {
                            var backing = fields.FirstOrDefault(f => f.IsProperty && f.Name == name.Name)?.Name
                                ?? properties.First(p => p.Name == (propertyQualifier is null ? name.Name : propertyQualifier + "." + name.Name)).Backing;
                            if (backing is null || referenceMode != ScalarParameterMode.Value) Fail("AI3106", "Only a field-backed value property can have an initializer.",name.Location);
                            AddInstanceInitializer(header,instanceInitializers,backing!,typeToken.Text,name.Location);
                            Take(";");
                        }
                        if (isStatic)
                        {
                            if (Take("="))
                            {
                                if (!Has(ScalarFeatures.StaticInitialization)) Fail("AI3100", "Static property initializers are unavailable in this profile.", name.Location);
                                var backing = staticFields.FirstOrDefault(f => f.IsProperty && f.Name == name.Name)?.Name
                                    ?? properties.First(p => p.Name == (propertyQualifier is null ? name.Name : propertyQualifier + "." + name.Name)).Backing;
                                if (backing is null) Fail("AI3106", "Only a field-backed property can have an initializer.", name.Location);
                                staticInitializers.Add(new(backing!, ReadStaticInitializer()));
                            }
                            if (!Take(";") && Current.Text is not "\n" and not "}") Fail("AI3106", "Expected the end of the static property declaration.");
                        }
                    }
                    else
                    {
                        if (propertyQualifier is not null) Fail("AI3106", "An explicit interface member requires accessors.",name.Location);
                        if (header.IsInterface && !isStatic) Fail("AI3100", "Interfaces declare only method signatures (no fields).", name.Location);
                        if (isIndexer || modifiers.Any(m => m != "static")) Fail("AI3106", "This member requires property accessors.", name.Location);
                        if(header.IsReadonly&&!isStatic&&!isReadonly)Fail("AI3106","Instance fields of a readonly struct must be readonly.",name.Location);
                        (isStatic ? staticFields : fields).Add(new(name.Name, type, EraseType(type), isPublic ?? header.IsInterface, isReadonly, name.Location) { Owner = IndexOf(header.Id), IsRequired = required is not null,IsInternal=isInternal });
                        if (isStatic && Take("=")) staticInitializers.Add(new(name.Name, ReadStaticInitializer()));
                        else if (!isStatic && Take("=")) AddInstanceInitializer(header,instanceInitializers,name.Name,typeToken.Text,name.Location);
                        if (!Take(";") && Current.Text is not "\n" and not "}") Fail("AI3106", "Expected the end of the field declaration.");
                    }
                }
                NewLines();
            }
            Expect("}");
            _structs[IndexOf(header.Id)] = _structs[IndexOf(header.Id)] with { StaticFields = staticFields, StaticInitializers = staticInitializers, InstanceInitializers = instanceInitializers, CustomRecordMembers = customRecordMembers };
            return (fields, methods, bases, properties);
        }
        // N20: 'event name: D' is a field-like event: a delegate field that code outside the declaring class can only combine into (+=) and
        // remove from (-=). 'event name: D { add { ... } remove { ... } }' declares the accessors K.add_name(value: D) and K.remove_name(value: D);
        // such an event has no storage and every use of it is E += d or E -= d.
        private void BindEvent(ScalarStruct header, bool isPublic, bool isReadonly, HashSet<string> modifiers, List<ScalarStructField> fields, List<ScalarStructField> otherFields, List<MemberSpan> methods, List<ScalarProperty> properties, List<ScalarStaticInitializer> staticInitializers, List<ScalarInstanceInitializer> instanceInitializers, bool isStatic)
        {
            var keyword = Next();
            if (header.IsRecord && !isStatic && !Has(ScalarFeatures.RecordInitializers)) Fail("AI3100", "Instance events in records are disabled in this profile.", keyword.Location);
            if (modifiers.Any(m => m != "static")) Fail("AI3100", "Virtual, override, abstract and sealed events are not supported yet.", keyword.Location);
            if (isReadonly&&(header.IsClass||isStatic||!Has(ScalarFeatures.ReadonlyStructs))) Fail("AI3106", "A readonly event requires a supported struct instance accessor body.", keyword.Location);
            var name = Next();
            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || IsTypeKeyword(name.Text) || name.Text is "this" or "new" or "fn")
                Fail("AI3106", $"Invalid event name '{name.Name}'.", name.Location);
            if (fields.Any(f => f.Name == name.Name) || otherFields.Any(f => f.Name == name.Name) || methods.Any(m => m.Name == name.Name) || properties.Any(p => p.Name == name.Name)) Fail("AI3106", $"Duplicate member '{name.Name}' in class '{header.Name}'.", name.Location);
            Expect(":");
            var typeToken = TypeToken(); var type = Type(typeToken.Text, typeToken.Location);
            if (!IsDelegate(Type(typeToken.Text, typeToken.Location))) Fail("AI3106", $"The event '{header.Name}.{name.Name}' must be of a delegate type; '{typeToken.Text}' is not one.", typeToken.Location);
            if(!header.IsClass&&!isStatic&&Current.Text!="{"&&(header.IsReadonly||isReadonly))Fail("AI3106","A readonly struct or readonly event member cannot declare a field-like event.",keyword.Location);
            if(!header.IsClass&&!isStatic&&!Has(ScalarFeatures.MutableStructMethods)&&(!Has(ScalarFeatures.ReadonlyStructs)||!header.IsReadonly&&!isReadonly))Fail("AI3100","Mutable instance events in structs are not supported yet.",keyword.Location);
            if (Current.Text != "{")
            {
                fields.Add(new(name.Name, type, type, isPublic, false, name.Location) { IsEvent = true, Owner = IndexOf(header.Id) });
                if (isStatic && Take("="))
                {
                    if (!Has(ScalarFeatures.StaticInitialization)) Fail("AI3100", "Static initializers are not supported in this profile.", name.Location);
                    staticInitializers.Add(new(name.Name, ReadStaticInitializer()));
                }
                else if (!isStatic && Take("=")) AddInstanceInitializer(header,instanceInitializers,name.Name,typeToken.Text,name.Location);
                if (!Take(";") && Current.Text is not "\n" and not "}") Fail("AI3106", "Expected the end of the event declaration.");
                return;
            }
            var open = Next(); NewLines();
            (int Start, int Stop, SourceLocation At)? addBody = null, removeBody = null;
            while (!Take("}"))
            {
                if (Current.Text == "") Fail("AI3106", "Unterminated event.", open.Location);
                var accessor = Next();
                if (accessor.Text is not ("add" or "remove")) Fail("AI3106", $"Expected 'add' or 'remove' in event '{name.Name}'.", accessor.Location);
                if ((accessor.Text == "add" ? addBody : removeBody) is not null) Fail("AI3106", $"Event '{name.Name}' has more than one {accessor.Text} accessor.", accessor.Location);
                if (Current.Text != "{") Fail("AI3106", $"An event accessor needs a body: '{accessor.Text} {{ ... }}'.", Current.Location);
                var brace = Next(); var depth = 1; Lexeme? close = null;
                while (Current.Text != "")
                {
                    var t = Next();
                    if (t.Text == "{") depth++;
                    else if (t.Text == "}" && --depth == 0) { close = t; break; }
                }
                if (close is null) Fail("AI3106", "Unterminated accessor body.", brace.Location);
                var body = (brace.Location.Offset + 1 - _declarationStart.Offset, close!.Location.Offset - _declarationStart.Offset,
                    new SourceLocation(brace.Location.Line, brace.Location.Column + 1, brace.Location.Offset + 1));
                if (accessor.Text == "add") addBody = body; else removeBody = body;
                NewLines();
            }
            if (addBody is null || removeBody is null) Fail("AI3106", $"The event '{name.Name}' must have both add and remove accessors.", name.Location);
            var accessorModifiers = new HashSet<string>(StringComparer.Ordinal) { "accessor" };
            if(!isStatic&&(header.IsReadonly||isReadonly))accessorModifiers.Add("readonly");
            if (isStatic) accessorModifiers.Add("static");
            FunctionDeclaration Accessor(string accessorName, (int Start, int Stop, SourceLocation At) b) =>
                new(accessorName, [new("value", typeToken.Text)], "unit", [], [], new FunctionBody([]) { ScalarSource = new(_declaration![b.Start..b.Stop], b.At) }, [], name.Location);
            methods.Add(new("add_" + name.Name, name.Location.Offset, name.Location.Offset, name.Location.Offset, true, accessorModifiers, Accessor("add_" + name.Name, addBody!.Value)));
            methods.Add(new("remove_" + name.Name, name.Location.Offset, name.Location.Offset, name.Location.Offset, true, accessorModifiers, Accessor("remove_" + name.Name, removeBody!.Value)));
            properties.Add(new(name.Name, type, typeToken.Text, isPublic, false, null, false, header.Name + ".add_" + name.Name, false, null, name.Location) { IsStatic = isStatic, IsEvent = true, Remover = header.Name + ".remove_" + name.Name });
        }
        // name: T { get; set; } / { get; init; } / { get; private set; } / { get; } is an auto-property stored as a field with access flags.
        // Accessors with bodies become functions K.get_name(this) -> T and K.set_name(this, value: T); an auto accessor next to a body,
        // and the contextual 'field' inside a body, use a hidden backing field.
        // new S(), new S(args) (the constructor), optionally followed by an object initializer { field = value, ... } evaluated in order.
        private ScalarExpression NewExpression(Lexeme keyword)
        {
            if (Has(ScalarFeatures.ProcessArguments) && !Has(ScalarFeatures.TypedArrays) && Current.Text == "string" && TokenAt(_position + 1).Text == "[") return NewStringArray(keyword);
            var typeToken = TypeToken();
            if (Has(ScalarFeatures.TypedArrays) && (Current.Text == "[" || typeToken.Text.EndsWith("[]", StringComparison.Ordinal))) return NewTypedArray(keyword, typeToken);
            var importedType=ImportAliasType(typeToken.Text,typeToken.Location);
            ScalarType? substituted = ScopedTypeArgument(typeToken.Text,out var substitutedArgument) ? substitutedArgument : null;
            if(substituted is null&&importedType is {} importedScalar&&!IsStruct(importedScalar))substituted=importedScalar;
            if (substituted is null && QualifiedBuiltinAlias(typeToken.Text) is not null)
            {var qualified=Type(typeToken.Text,typeToken.Location);if(!IsStruct(qualified))substituted=qualified;}
            if (substituted is null && typeToken.Text == "object" && Has(ScalarFeatures.Classes)) substituted = (ScalarType)ObjectTypeId;
            if (substituted is { } scalar && (!IsStruct(scalar) || !StructOf(scalar).IsClass && !StructOf(scalar).IsTypeParameter))
                return NewValueTypeParameter(scalar, typeToken, keyword.Location);
            var info = substituted is { } resolved ? StructOf(resolved) : importedType is {} importedStructure&&IsStruct(importedStructure)?StructOf(importedStructure):typeToken.Text.Contains('<') || QualifiedBuiltinAlias(typeToken.Text) is not null ? StructOf(Type(typeToken.Text, typeToken.Location)) : StructNamed(typeToken.Text);
            if (info is null && StructNamedAny(typeToken.Text) is { TypeParameters.Count: > 0 } open)
                Fail("AI3106", $"The generic type '{typeToken.Text}' requires {open.TypeParameters.Count} type argument(s).", typeToken.Location);
            if (info is null) { Fail("AI3106", $"'new' needs a struct or class type; '{typeToken.Text}' is not one.", typeToken.Location); return null!; }
            CompilationTypeAccess(info.Id,typeToken.Location);
            if(IsDisplayBuilder(info))return NewDisplayBuilder(info,keyword.Location);
            if(substituted is null&&info.IsRecord&&Has(ScalarFeatures.RecordConstructors))return NewRecordWithConstructors(info,typeToken,keyword.Location);
            if (substituted is null && NeedsRecordConstruction(info)) return NewInitializedRecord(info,typeToken,keyword.Location);
            if (substituted is not null && info.IsClass && !info.IsTypeParameter)
                return NewReferenceTypeParameter(info, typeToken, keyword.Location);
            if (Delegating && IsDelegate(info.Id) && !InDelegateHelper)
            {
                // C# delegate creation: new D(lambda or method group) is the conversion of its argument.
                if (Current.Text != "(") Fail("AI3106", $"A delegate creation expression is written 'new {info.Name}(method or lambda)'.", Current.Location);
                Next();
                if (Current.Text == ")") Fail("AI3106", "A delegate creation expression requires a method group or lambda.", typeToken.Location);
                var argument = Expression(); Expect(")");
                if (!IsFunctionValue(argument.Type)) Fail("AI3100", $"Creating a '{info.Name}' from another delegate value is not supported yet; use the value itself.", argument.Location);
                return Coerce(argument, info.Id);
            }
            if (info.IsTypeParameter && !info.HasValueConstraint && !info.HasConstructorConstraint)
                Fail("AI3106", $"Cannot create an instance of type parameter '{info.Name}' without a 'new()' or value-type constraint.", typeToken.Location);
            if (info.IsInterface || info.IsAbstract && !info.IsTypeParameter) Fail("AI3116", $"Cannot create an instance of the abstract type '{info.Name}'.", typeToken.Location);
            ScalarExpression value = new("new", info.Id, keyword.Location);
            var positionalDone = false;
            if (info.IsRecord && (info.Positional > 0 || info.HasRecordPrimaryConstructor && info.Methods.Any(m => m.IsStaticConstructor && !m.IsImplicitStaticConstructor)) && Current.Text == "(")
            {
                // The positional constructor assigns the positional members in order (arguments are evaluated left to right).
                Next(); var arguments = new List<ScalarExpression>();
                if (!Take(")"))
                {
                    do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                    Expect(")");
                }
                if (arguments.Count != info.Positional) Fail("AI3116", $"'{info.Name}' has no constructor taking {arguments.Count} argument(s).", typeToken.Location);
                for (var i = 0; i < arguments.Count; i++) arguments[i] = Coerce(arguments[i], info.Fields[i].Type);
                value = new(info.Methods.Any(m => m.IsStaticConstructor && !m.IsImplicitStaticConstructor) && (info.IsClass || info.HasRecordPrimaryConstructor) ? "init-record" : "init", info.Id, keyword.Location, string.Join(",", Enumerable.Range(0, arguments.Count)), Left: value) { Arguments = arguments };
                positionalDone = true;
            }
            else if (info.IsRecord && info.IsClass && info.Positional > 0)
                Fail("AI3116", $"'{info.Name}' has no constructor taking 0 argument(s).", typeToken.Location);
            if (positionalDone) { }
            else if (Take("("))
            {
                var arguments = new List<ScalarExpression>();
                if (!Take(")"))
                {
                    do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                    Expect(")");
                }
                var declaredConstructor = info.Methods.FirstOrDefault(m => m.IsConstructor);
                if (arguments.Count == 0 && info.IsClass && declaredConstructor is not null && declaredConstructor.Declaration.Parameters.Count > 0)
                    Fail("AI3116", $"'{info.Name}' has no constructor taking 0 argument(s).", typeToken.Location);
                if (arguments.Count > 0 || declaredConstructor is not null && (info.IsClass || declaredConstructor.Declaration.Parameters.Count == 0))
                {
                    var constructor = declaredConstructor;
                    if (constructor is null || constructor.Declaration.Parameters.Count != arguments.Count)
                        Fail("AI3116", $"'{info.Name}' has no constructor taking {arguments.Count} argument(s).", typeToken.Location);
                    CheckAccess(info, constructor!.IsPublic, "new", typeToken.Location);
                    var namedOrder=BindNamedArguments(arguments,constructor.Declaration,typeToken.Location);
                    for (var i = 0; i < arguments.Count; i++)
                        arguments[i] = BindArgument(arguments[i], MemberType(info.TypeArguments, constructor.Declaration.Parameters[i].Type, typeToken.Location), ParameterPassing(constructor.Declaration.Parameters[i].Type).Mode);
                    CompleteArguments(arguments);
                    value = NamedCall(new("call", info.Id, keyword.Location, constructor.QualifiedName) { Arguments = arguments },namedOrder);
                }
            }
            else if (Current.Text != "{") Fail("AI3106", "Expected '(' or '{' after the struct type in 'new'.");
            if (value.Operation == "new" && (NeedsInit(IndexOf(info.Id)) || !info.IsClass && info.Methods.Any(m => m.IsConstructor && m.Declaration.Parameters.Count == 0)))
            {
                // The constructor chain must run: K.new() (an implicit constructor or a declared parameterless one).
                var declared = info.Methods.FirstOrDefault(m => m.IsConstructor);
                if (declared is not null && declared.Declaration.Parameters.Count > 0) Fail("AI3116", $"'{info.Name}' has no constructor taking 0 argument(s).", typeToken.Location);
                if (declared is not null) CheckAccess(info, declared.IsPublic, "new", typeToken.Location);
                value = new("call", info.Id, keyword.Location, info.Name + ".new") { Arguments = [] };
            }
            if (Has(ScalarFeatures.RequiredMembers)) return RequiredObjectInitializer(info, value, typeToken.Location, enforceRequired: !info.IsTypeParameter);
            if (Take("{"))
            {
                NewLines();
                var indices = new List<int>(); var values = new List<ScalarExpression>();
                while (Current.Text != "}")
                {
                    var fieldToken = Next();
                    var index = info.Fields.ToList().FindIndex(f => f.Name == fieldToken.Text && !f.Hidden);
                    if (index < 0 && FindProperty(IndexOf(info.Id), fieldToken.Text) is not null)
                        Fail("AI3100", $"A property with accessor bodies ('{info.Name}.{fieldToken.Text}') cannot be set in an object initializer yet.", fieldToken.Location);
                    if (index < 0) Fail("AI3103", $"'{info.Name}' has no field '{fieldToken.Text}'.", fieldToken.Location);
                    var field = info.Fields[index];
                    var declaringType = field.Owner >= 0 ? _structs[field.Owner] : info;
                    CheckAccess(declaringType, field.IsPublic, field.Name, fieldToken.Location);
                    if (field.IsReadonly && field.IsProperty&&!field.IsInit) Fail("AI3105", $"Property '{declaringType.Name}.{field.Name}' cannot be assigned to -- it is read only.", fieldToken.Location);
                    if (field.IsReadonly&&!field.IsInit) Fail("AI3105", $"Readonly field '{info.Name}.{field.Name}' cannot be set in an object initializer.", fieldToken.Location);
                    if (field.IsEvent && _struct?.Id != declaringType.Id) Fail("AI3106", $"The event '{declaringType.Name}.{field.Name}' can only appear on the left hand side of += or -= (except when used from within the type '{declaringType.Name}').", fieldToken.Location);
                    if (field.WritePrivate && _struct?.Id != declaringType.Id)
                        Fail("AI3119", $"The property '{declaringType.Name}.{field.Name}' cannot be used in this context because the set accessor is inaccessible.", fieldToken.Location);
                    if (indices.Contains(index)) Fail("AI3106", $"Field '{field.Name}' is initialized twice.", fieldToken.Location);
                    Expect("=");
                    var item = Expression(); Read(item);
                    values.Add(Coerce(item, field.Type)); indices.Add(index);
                    NewLines();
                    if (!Take(",")) break;
                    NewLines();
                }
                NewLines(); Expect("}");
                // An empty initializer adds nothing to the constructed value.
                if (indices.Count > 0) value = new("init", info.Id, keyword.Location, string.Join(",", indices), Left: value) { Arguments = values };
            }
            return value;
        }
        private ScalarExpression FieldRead(ScalarExpression receiver, Lexeme member)
        {
            member = member with { Text = member.Name };
            receiver=RecordMemberReceiver(receiver,member);
            var info = StructOf(receiver.Type);
            if (receiver.Operation == "static-type") return StaticFieldRead(info, member);
            if (CheckedInstanceProperty(receiver,member) is { } contractProperty) return PropertyRead(receiver,contractProperty,member);
            if (FindStaticField(IndexOf(info.Id), member.Text) is not null || FindProperty(IndexOf(info.Id), member.Text) is { IsStatic: true }) Fail("AI3106", "A static field must be accessed through its type.", member.Location);
            if (FindProperty(IndexOf(info.Id), member.Text) is { IsEvent: true } customEvent) return EventAccessorCall(receiver, customEvent, member);
            if (FindProperty(IndexOf(info.Id), member.Text) is { } property) return PropertyRead(receiver, property, member);
            var index = VisibleInstanceFieldIndex(info,member.Text);
            if (index < 0) Fail("AI3103", $"'{info.Name}' has no field '{member.Text}'.", member.Location);
            CheckAccess(info.Fields[index].Owner >= 0 ? _structs[info.Fields[index].Owner] : info, info.Fields[index].IsPublic, member.Text, member.Location);
            if (info.Fields[index].IsEvent)
            {
                var declaring = info.Fields[index].Owner >= 0 ? _structs[info.Fields[index].Owner] : info;
                if (_struct?.Id != declaring.Id && Current.Text is not ("+=" or "-="))
                    Fail("AI3106", $"The event '{declaring.Name}.{member.Text}' can only appear on the left hand side of += or -= (except when used from within the type '{declaring.Name}').", member.Location);
            }
            return new("field", info.Fields[index].Type, member.Location, index.ToString(CultureInfo.InvariantCulture), Left: receiver);
        }
        // N20: E += d and E -= d on an event with accessors call K.add_E(receiver, d) and K.remove_E(receiver, d); any other use is an error, inside the class too.
        private ScalarExpression EventAccessorCall(ScalarExpression receiver, ScalarProperty customEvent, Lexeme member)
        {
            member = member with { Text = member.Name };
            var owner = _structs[customEvent.Owner];
            if (customEvent.IsStatic != (receiver.Operation == "static-type")) Fail("AI3106", customEvent.IsStatic ? "A static event must be accessed through its type." : "An instance event requires an object receiver.", member.Location);
            CheckAccess(owner, customEvent.IsPublic, member.Text, member.Location);
            if (Current.Text is not ("+=" or "-="))
                Fail("AI3106", $"The event '{owner.Name}.{member.Text}' can only appear on the left hand side of += or -=.", member.Location);
            var add = Next().Text == "+=";
            var value = Expression(); Read(value);
            var handler = Coerce(value, customEvent.Type);
            var function=add?customEvent.Setter:customEvent.Remover;var method=owner.Methods.First(m=>m.QualifiedName==function);
            return new("call", ScalarType.Unit, member.Location, function) { Arguments = customEvent.IsStatic ? [handler] : [AccessorReceiverArgument(method,receiver,member.Location), handler] };
        }
        // receiver.m(args) evaluates the receiver first, then passes a checked reference or value snapshot according to its member ABI.
        // A virtual, abstract, override or interface member is called through its slot's dispatch function; base.m(...) (direct) calls the implementation itself.
        private ScalarExpression MethodCall(ScalarExpression receiver, Lexeme member, bool direct = false, List<ScalarExpression>? given = null)
        {
            member = member with { Text = member.Name };
            receiver=RecordMemberReceiver(receiver,member);
            if(!IsStruct(receiver.Type))Fail("AI3100","This primitive instance call is unavailable in the current profile.",member.Location);
            var info = StructOf(receiver.Type); var constrained = info.IsTypeParameter;
            if (receiver.Operation == "static-type" && receiver.Value is { } written && _typeArgs?.ContainsKey(written) == true)
                return StaticInterfaceCall(receiver, member, given);
            if (receiver.Operation == "static-type" && info.IsInterface && FindMethod(IndexOf(info.Id), member.Text) is { IsStatic: true, Slot: not null })
                Fail("AI3106", "A static abstract or virtual interface member can be accessed only on a constrained type parameter.", member.Location);
            ScalarStructMethod? method; ScalarStructMethod? selectedImplementation = null;
            if (_typeArgs is not null && (!_checkOnly || !info.IsTypeParameter) && constrainedInterfaceCalls!.TryGetValue(member.Location.Offset, out var bound))
            {
                bound = ReboundInterfaceRoot(bound);
                if (!Derives(_structs, IndexOf(info.Id), bound.Owner)) Fail("AI3107", "The closed receiver does not satisfy its declared interface proof.", member.Location);
                if (!info.IsClass)
                {
                    var resolved = ResolveInterfaceImplementation(IndexOf(info.Id), bound).Method;
                    if (resolved is null || _structs[resolved.Owner].IsInterface&&!Has(ScalarFeatures.Boxing)) Fail("AI3100", "A default interface instance body on a value type requires boxing, which is not supported yet.", member.Location);
                    if(_structs[resolved!.Owner].IsInterface)
                    {
                        info=_structs[bound.Owner];receiver=BoxValue(receiver,info.Id,receiver:true);method=bound;
                    }
                    else { method = bound; selectedImplementation = resolved; }
                }
                else { info = _structs[bound.Owner]; receiver = Upcast(receiver, info.Id); method = bound; }
            }
            else method = FindMethod(IndexOf(info.Id), member.Text);
            if(Has(ScalarFeatures.TypeIdentity)&&member.Text=="GetType"&&(given is {Count:0}||given is null&&Current.Text=="("&&TokenAt(_position+1).Text==")"))
                method=SourceParameterlessGetType(info.Id)??method;
            if (info.IsInterface && InterfaceMethods([IndexOf(info.Id)], member.Text).Count > 1)
                Fail("AI3106", $"The call to '{member.Text}' is ambiguous between inherited interface members of '{info.Name}'.", member.Location);
            if (method is null && info.IsTypeParameter)
            {
                // Distinct paths to one inherited declaration share a slot; unrelated declarations remain ambiguous.
                var through = InterfaceMethods(info.Interfaces, member.Text);
                if (through.Count > 1) Fail("AI3106", $"The call to '{member.Text}' is ambiguous between the constraints of '{info.Name}'.", member.Location);
                if (through.Count == 1) { method = through[0]; info = _structs[method.Owner]; receiver = Upcast(receiver, info.Id); }
            }
            if (method is null) { Fail("AI3103", $"'{info.Name}' has no method '{member.Text}'.", member.Location); return null!; }
            var declaring = method.Owner >= 0 ? _structs[method.Owner] : info;
            var deconstructOverload=(member.Text=="Deconstruct"&&Has(ScalarFeatures.RecordMembers)||member.Text=="Equals"&&(Has(ScalarFeatures.Boxing)||Has(ScalarFeatures.RecordInheritance)&&RecordEqualityReceiver(info))||Has(ScalarFeatures.RecordDisplay)&&member.Text is "ToString" or "PrintMembers"&&RecordEqualityReceiver(info)||Has(ScalarFeatures.RecordHashing)&&member.Text=="GetHashCode"&&(Has(ScalarFeatures.Boxing)||RecordEqualityReceiver(info)))&&!info.IsInterface&&(!info.IsTypeParameter||member.Text=="Equals");
            if (activeDefaultInterfaces && _checkOnly && constrained && declaring.IsInterface) constrainedInterfaceCalls![member.Location.Offset] = method;
            if (!deconstructOverload&&method.IsStatic != (receiver.Operation == "static-type")) Fail("AI3106", method.IsStatic ? "A static method must be accessed through its type." : "An instance method requires an object receiver.", member.Location);
            if(!deconstructOverload)CheckDisplayMethodAccess(receiver,declaring,method,member,direct);
            if (direct && method.IsAbstract) Fail("AI3106", $"Cannot call the abstract member '{method.QualifiedName}' through base.", member.Location);
            List<(ScalarType Type, SourceLocation Location)>? explicitArguments = null;
            if (given is null && Has(ScalarFeatures.Generics) && Take("<"))
            {
                explicitArguments = [];
                do { var type = TypeToken(); explicitArguments.Add((Type(type.Text, type.Location), type.Location)); } while (Take(","));
                Expect(">");
            }
            var arguments = given ?? new List<ScalarExpression>();
            if (given is null)
            {
                Expect("(");
                if (!Take(")"))
                {
                    do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                    Expect(")");
                }
            }
            if(explicitArguments is null&&ComparerObjectMember(receiver,member,arguments) is {} objectMember)return objectMember;
            if(deconstructOverload)
            {
                method=SelectDeconstructCall(receiver,arguments,explicitArguments,member.Location,member.Text);declaring=_structs[method.Owner];
                CheckDisplayMethodAccess(receiver,declaring,method,member,direct);
                if(method.IsStatic!=(receiver.Operation=="static-type"))Fail("AI3106",method.IsStatic?"A static method must be accessed through its type.":"An instance method requires an object receiver.",member.Location);
            }
            if (arguments.Count != method.Declaration.Parameters.Count)
                Fail("AI3116", $"'{info.Name}.{member.Text}' expects {method.Declaration.Parameters.Count} argument(s) but {arguments.Count} were supplied.", member.Location);
            var namedOrder=BindNamedArguments(arguments,method.Declaration,member.Location);
            if (GenericOf(method.Declaration) is { } generic)
                return GenericCall(member, method.Declaration, generic, explicitArguments, arguments, declaring, method, receiver, direct, selectedImplementation,namedOrder);
            if (explicitArguments is not null) Fail("AI3116", $"The non-generic method '{method.QualifiedName}' cannot be used with type arguments.", member.Location);
            for (var i = 0; i < arguments.Count; i++) arguments[i] = BindArgument(arguments[i], MemberType(declaring.TypeArguments, method.Declaration.Parameters[i].Type, member.Location), ParameterPassing(method.Declaration.Parameters[i].Type).Mode);
            CompleteArguments(arguments);
            if(OmittedPartial(method.Declaration))return new("omitted-partial-call",ScalarType.Unit,member.Location);
            if (selectedImplementation is not null)
                return NamedCall(InterfaceImplementationCall(selectedImplementation, receiver, arguments, MemberType(declaring.TypeArguments, method.Declaration.ReturnType, member.Location, true), member.Location),namedOrder);
            if (method.Slot is { } slot && !direct)
            {
                var rootOwner = _structs.First(x => x.Name == slot[..slot.LastIndexOf('.')]);
                return NamedCall(ReferenceResult(new("call", Type(method.Declaration.ReturnType, member.Location, true), member.Location, slot + ".virtual") { Arguments = [Upcast(receiver, rootOwner.Id), .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, 1),namedOrder);
            }
            return NamedCall(AccessorReferenceResult(ReferenceResult(new("call", MemberType(declaring.TypeArguments, method.Declaration.ReturnType, member.Location, true), member.Location, method.QualifiedName) { Arguments = method.IsStatic ? arguments : [method.ReceiverMode==ScalarParameterMode.Value?Upcast(receiver,declaring.Id):AccessorReceiverArgument(method,receiver,member.Location), .. arguments] }, method.Declaration.ReturnType, method.Declaration.Parameters, method.IsStatic ? 0 : 1), method),namedOrder);
        }
        private static ScalarExpression RootOf(ScalarExpression expression)
        {
            while (expression.Operation == "field") expression = expression.Left!;
            return expression;
        }
        // ---- patterns (C# 9 subset) ----
        private static int SwitchPrecedence => 9; // constant patterns are shift expressions
        private bool PatternSubject(ScalarType type) => ScalarConstants.IsInteger(type) || type is ScalarType.Char or ScalarType.Bool or ScalarType.String || IsEnum(type) || Has(ScalarFeatures.Boxing)&&(IsFloat(type)||IsDecimal(type));
        private Local HiddenLocal(ScalarType type)
        {
            var local = new Local($"__scalar_local_{_nextId++}", type, false);
            _hiddenLocals[local.EmittedName] = local; _assigned.Add(local.EmittedName);
            return local;
        }
        private readonly Dictionary<string, Local> _hiddenLocals = new(StringComparer.Ordinal);
        // N15 pattern variables: each is a real mutable local declared (with a default value) before the statement that contains its pattern,
        // and stored by 'bindpat' when the pattern matches. Definite assignment follows C#: a variable is assigned where its pattern is known to be true
        // (or false, for 'is not T x'), propagated through !, && and || to if/while bodies, the right operand of &&/||, and switch arm guards and values.
        private readonly Dictionary<ScalarExpression, (List<string> T, List<string> F)> _patternFlow = new(ReferenceEqualityComparer.Instance);
        private readonly List<ScalarStatement> _pendingDeclarations = [];
        private List<string>? _patternDeclared;
        private int _patternNoDeclare;
        private bool _topNotAllowed, _negatedDeclarations;
        private (List<string> T, List<string> F) Flow(ScalarExpression expression) => _patternFlow.TryGetValue(expression, out var flow) ? flow : ([], []);
        private ScalarExpression WithFlow(ScalarExpression expression, IEnumerable<string> whenTrue, IEnumerable<string> whenFalse)
        {
            var t = whenTrue.Distinct().ToList(); var f = whenFalse.Distinct().ToList();
            if (t.Count > 0 || f.Count > 0) _patternFlow[expression] = (t, f);
            return expression;
        }
        private ScalarExpression DeclarePatternVariable(Lexeme name, ScalarType type, ScalarExpression value)
        {
            if (_patternDeclared is null) Fail("AI3100", "Pattern variables are not supported here.", name.Location);
            if (_patternNoDeclare > 0) Fail("AI3106", "A variable may not be declared within a 'not' or 'or' pattern.", name.Location);
            if (IsTypeKeyword(name.Text) || name.Text is "and" or "or" or "not" or "when" or "var" or "_") Fail("AI3106", $"Invalid pattern variable name '{name.Text}'.", name.Location);
            if (LocalNamed(name.Text) is not null) Fail("AI3106", $"A local named '{name.Text}' is already defined in this or an enclosing scope.", name.Location);
            var local = Declare(name.Text, type, true, name.Location);
            ScalarExpression initial = type == ScalarType.String ? new("literal", ScalarType.String, name.Location, "") : DefaultValue(type, name.Location);
            _pendingDeclarations.Add(new ScalarStatement("local", name.Location, initial, local.EmittedName, type) { Mutable = true });
            _patternDeclared!.Add(local.EmittedName); _patternLocals.Add(local.EmittedName);
            return new("bindpat", ScalarType.Bool, name.Location, Left: new("local", type, name.Location, local.EmittedName), Right: Coerce(value, type));
        }
        private ScalarExpression And(ScalarExpression left, ScalarExpression right, SourceLocation at) => Fold(new("&&", ScalarType.Bool, at, Left: left, Right: right) { Wraps = _wraps });
        // Binds a whole pattern with variable declarations allowed; returns the test and the declared variables (and whether they hold when the test is false).
        private (PatternResult Pattern, List<string> Declared, bool Negated) DeclaringPattern(ScalarExpression subject, bool allowDiscard, bool allowType, bool topNot)
        {
            var saved = (_patternDeclared, _negatedDeclarations, _topNotAllowed, _patternNoDeclare);
            _patternDeclared = []; _negatedDeclarations = false; _topNotAllowed = topNot; _patternNoDeclare = 0;
            try { var pattern = Pattern(subject, allowDiscard, allowType); return (pattern, _patternDeclared, _negatedDeclarations); }
            finally { (_patternDeclared, _negatedDeclarations, _topNotAllowed, _patternNoDeclare) = saved; }
        }
        private sealed record PatternResult(ScalarExpression Test, IReadOnlyList<object>? Constants, bool IsDiscard, SourceLocation Location)
        {
            // A type (or declaration) pattern without a nested test matches every non-null value of this type: later arms of a subtype are subsumed.
            public ScalarType? Covers { get; init; }
            public ScalarExpression? Narrowed { get; init; }
        }
        // Binds a pattern against the subject (a read of the hidden subject local) and returns the bool test.
        private PatternResult PatternCore(ScalarExpression subject, bool allowDiscard, bool allowType)
        {
            var start = Current.Location;
            var declaredBefore = _patternDeclared?.Count ?? 0;
            var first = PatternAnd(subject, allowDiscard, allowType);
            if (Current.Text != "or") return first;
            if ((_patternDeclared?.Count ?? 0) > declaredBefore || _negatedDeclarations) Fail("AI3106", "A variable may not be declared within a 'not' or 'or' pattern.", Current.Location);
            var test = first.Test;
            // A disjunction of constants keeps its constant set, so a later case or arm it fully covers is reported as subsumed.
            var constants = first.Constants is null ? null : new List<object>(first.Constants);
            var narrowed=first.Narrowed??subject;
            while (Current.Text == "or")
            {
                var op = Next(); _patternNoDeclare++; PatternResult right;
                try { right = PatternAnd(subject, allowDiscard, allowType); } finally { _patternNoDeclare--; }
                test = Fold(new("||", ScalarType.Bool, op.Location, Left: test, Right: right.Test) { Wraps = _wraps });
                narrowed=CommonPatternInput(narrowed,right.Narrowed??subject,subject);
                if (constants is not null && right.Constants is not null) constants.AddRange(right.Constants); else constants = null;
            }
            return new(test, constants, false, start){Narrowed=narrowed};
        }
        private PatternResult PatternAnd(ScalarExpression subject, bool allowDiscard, bool allowType)
        {
            var start = Current.Location;
            var first = PatternNot(subject, allowDiscard, allowType);
            if (Current.Text != "and") return first;
            if (_negatedDeclarations) Fail("AI3106", "A variable may not be declared within a 'not' or 'or' pattern.", Current.Location);
            var test = first.Test;
            var narrowed=first.Narrowed??subject;
            while (Current.Text == "and")
            {
                var op = Next(); var right = PatternNot(narrowed, allowDiscard, allowType);
                test = Fold(new("&&", ScalarType.Bool, op.Location, Left: test, Right: right.Test) { Wraps = _wraps });
                narrowed=right.Narrowed??narrowed;
            }
            return new(test, null, false, start){Narrowed=narrowed};
        }
        private PatternResult PatternNot(ScalarExpression subject, bool allowDiscard, bool allowType)
        {
            if (Current.Text != "not") { _topNotAllowed = false; return PrimaryPattern(subject, allowDiscard, allowType); }
            // 'e is not T x' declares x where the test is false; any other 'not' may not declare variables.
            var op = Next(); var top = _topNotAllowed; _topNotAllowed = false;
            var declaredBefore = _patternDeclared?.Count ?? 0; PatternResult inner;
            if (!top) _patternNoDeclare++;
            try { inner = PatternNot(subject, false, allowType); } finally { if (!top) _patternNoDeclare--; }
            if (top && (_patternDeclared?.Count ?? 0) > declaredBefore) _negatedDeclarations = true;
            return new(Fold(new("unary!", ScalarType.Bool, op.Location, Left: inner.Test) { Wraps = _wraps }), null, false, op.Location);
        }
        private string PeekText(int offset) => _position + offset < _tokens.Count ? _tokens[_position + offset].Text : "";
        private bool PatternTypeName(string text) =>
            BuiltinSequenceTypeName(text) || Has(ScalarFeatures.Generics)&&(text=="IEquatable"||BuiltinComparerTypeName(text)||_typeArgs?.ContainsKey(text)==true||_memberArgs?.ContainsKey(text)==true) || (IsTypeKeyword(text) || text is "f32" or "f64" or "decimal" or "string" or "char" or "bool" or "nint" or "nuint" || EnumNamed(text) is not null || StructNamed(text) is not null || Has(ScalarFeatures.TypeIdentity)&&text=="Type") && !IsLocalName(text);
        private PatternResult PrimaryPattern(ScalarExpression subject, bool allowDiscard, bool allowType)
        {
            var token = Current;
            // '(T)e' is a cast in a constant pattern; '(T)' followed by a pattern delimiter is a parenthesized type pattern.
            var isCast = token.Text == "(" && PatternTypeName(PeekText(1)) && PeekText(2) == ")"
                && PeekText(3) is not ("or" or "and" or "=>" or ":" or "when" or ")" or "}" or "," or "&&" or "||" or "\n" or "");
            if (!isCast && Take("("))
            {
                var inner = Pattern(subject, false, allowType); Expect(")");
                return inner with { Location = token.Location };
            }
            if (token.Text == "_" && !IsLocalName("_"))
            {
                if (!allowDiscard) Fail("AI3100", "The discard pattern '_' is supported only as a switch expression arm.", token.Location);
                Next(); return new(Fold(new("literal", ScalarType.Bool, token.Location, "true")), null, true, token.Location);
            }
            if (token.Text == "var" && !IsLocalName("var") && IsSourceIdentifier(PeekText(1)) && Has(ScalarFeatures.Classes))
            {
                // var x: always matches (including null) and declares x of the subject's type.
                Next(); var name = Next();
                return new(DeclarePatternVariable(name, subject.Type, subject), null, false, token.Location) { Covers = subject.Type };
            }
            if(OpenPatternInput(subject,token.Location)&&token.Text!="{")return ObjectPattern(AsObject(subject),token);
            if(Has(ScalarFeatures.Boxing)&&subject.Type==ScalarType.String&&token.Text=="{")return PropertyPattern(subject,subject,token,null);
            if (!PatternSubject(subject.Type) && Has(ScalarFeatures.Classes)) return ObjectPattern(subject, token);
            var isTypeName = PatternTypeName(token.Text) && _tokens[_position + 1].Text != ".";
            if (isTypeName && IsSourceIdentifier(PeekText(1)) && PeekText(1) is not ("and" or "or" or "when") && Has(ScalarFeatures.Classes))
            {
                // T x over a value of value type T: always matches and declares x (other value types never match, as for type patterns).
                Next(); var declaredType = Type(token.Text, token.Location); var name = Next();
                if(TryBoxPattern(subject,declaredType,token.Location) is {} boxedPattern)
                    return new(And(boxedPattern.Test,DeclarePatternVariable(name,declaredType,boxedPattern.Value),token.Location),null,false,token.Location){Covers=declaredType,Narrowed=boxedPattern.Value};
                if (declaredType != subject.Type) Fail("AI3107", $"An expression of type '{Show(subject.Type)}' cannot be handled by a pattern of type '{token.Text}'.", token.Location);
                var bind = DeclarePatternVariable(name, subject.Type, subject);
                if (subject.Type == ScalarType.String && Has(ScalarFeatures.NullStrings))
                    return new(And(MakeBinary("!=", subject, new("null", ScalarType.String, token.Location), token.Location), bind, token.Location), null, false, token.Location);
                return new(bind, null, false, token.Location) { Covers = subject.Type };
            }
            if (isTypeName)
            {
                // A type pattern over a value of static value type T: true exactly when the pattern type is T (C# CS0183/CS0184 cases).
                if (!allowType) Fail("AI3100", "Type patterns are supported only with 'is'.", token.Location);
                Next(); var type = Type(token.Text, token.Location);
                if(TryBoxPattern(subject,type,token.Location) is {} boxedPattern)return new(boxedPattern.Test,null,false,token.Location){Covers=type,Narrowed=boxedPattern.Value};
                if (subject.Type == ScalarType.String && Has(ScalarFeatures.NullStrings) && (type == ScalarType.String || IsObject(type)))
                    return new(MakeBinary("!=", subject, new("null", ScalarType.String, token.Location), token.Location), null, false, token.Location);
                return new(Fold(new("literal", ScalarType.Bool, token.Location, type == subject.Type ? "true" : "false")), null, false, token.Location);
            }
            if (!PatternSubject(subject.Type))
                Fail("AI3100", $"Constant and relational patterns over {Show(subject.Type)} are not supported.", token.Location);
            if (token.Text is "<" or "<=" or ">" or ">=")
            {
                Next();
                if (subject.Type is ScalarType.String or ScalarType.Bool) Fail("AI3107", $"Relational patterns are not defined for {Show(subject.Type)}.", token.Location);
                var constantAt=Current.Location;var bound = PatternConstant(subject);
                if(Has(ScalarFeatures.Boxing)&&ConstantOf(bound) is double number&&double.IsNaN(number))
                    Fail("AI3113","A relational pattern cannot use NaN.",constantAt);
                return new(MakeBinary(token.Text, subject, bound, token.Location), null, false, token.Location);
            }
            var constant = PatternConstant(subject);
            var test = Has(ScalarFeatures.Boxing)&&IsFloat(subject.Type)?ObjectValueEqual(subject,constant,constant.Location):subject.Type == ScalarType.Bool
                ? Fold(new("==", ScalarType.Bool, constant.Location, Left: subject, Right: constant) { Wraps = _wraps })
                : MakeBinary("==", subject, constant, constant.Location);
            return new(test, [PatternValue(constant)!], false, constant.Location);
        }
        // Patterns over class, interface, object and struct subjects: null, type and declaration patterns (T, T x), property patterns { m: P, ... }
        // (optionally after a type and before a designation), constants are not supported.
        private PatternResult ObjectPattern(ScalarExpression subject, Lexeme token)
        {
            var reference = IsReference(subject.Type);
            if (token.Text == "null")
            {
                if (!reference) Fail("AI3107", $"A null pattern cannot match a value of type '{Show(subject.Type)}'.", token.Location);
                Next(); return new(new("==", ScalarType.Bool, token.Location, Left: subject, Right: new("null", subject.Type, token.Location)) { Wraps = _wraps }, null, false, token.Location);
            }
            if (token.Text == "{")
            {
                var property = PropertyPattern(subject, subject, token, null);
                if (!IsSourceIdentifier(Current.Text) || Current.Text is "and" or "or" or "when" or "not" || IsReserved(Current.Text)) return property;
                var designation = Next();
                return property with { Test = And(property.Test, DeclarePatternVariable(designation, subject.Type, subject), designation.Location) };
            }
            var arrayType = ArrayTypeAhead();
            var isType = arrayType || QualifiedBuiltinAt(_position) is not null || !IsLocalName(token.Text) && PeekText(1)!="." && (token.Text == "object" || Has(ScalarFeatures.Boxing)&&PatternTypeName(token.Text) || StructNamedAny(token.Text) is not null || Has(ScalarFeatures.TypeIdentity)&&token.Text=="Type");
            if(!isType&&Has(ScalarFeatures.Boxing))return BoxedConstantPattern(subject,token);
            if (!isType) Fail("AI3100", $"Constant and relational patterns over {Show(subject.Type)} are not supported.", token.Location);
            var typeToken = TypeToken(); var tested = Type(typeToken.Text, typeToken.Location);
            ScalarExpression test, narrowed;
            if(TryBoxPattern(subject,tested,typeToken.Location) is {} boxedPattern){test=boxedPattern.Test;narrowed=boxedPattern.Value;}
            else if (!reference)
            {
                if (tested != subject.Type) Fail("AI3107", $"An expression of type '{Show(subject.Type)}' cannot be handled by a pattern of type '{typeToken.Text}'.", typeToken.Location);
                test = Fold(new("literal", ScalarType.Bool, token.Location, "true")); narrowed = subject;
            }
            else
            {
                if (!IsReference(tested)) Fail("AI3107", $"An expression of type '{Show(subject.Type)}' cannot be handled by a pattern of type '{typeToken.Text}'.", typeToken.Location);
                var always = tested == subject.Type || IsObject(tested) || IsClass(subject.Type) && IsClass(tested) && Derives(_structs, IndexOf(subject.Type), IndexOf(tested));
                if (always) { test = new("!=", ScalarType.Bool, token.Location, Left: subject, Right: new("null", subject.Type, token.Location)) { Wraps = _wraps }; narrowed = ReferenceCast(tested, subject, token.Location); }
                else
                {
                    var possible = IsObject(subject.Type) || Lenient(subject.Type, tested) || Derives(_structs, IndexOf(tested), IndexOf(subject.Type)) || CastPossible(IndexOf(subject.Type), IndexOf(tested));
                    if (!possible) Fail("AI3107", $"An expression of type '{Show(subject.Type)}' cannot be handled by a pattern of type '{typeToken.Text}'.", typeToken.Location);
                    test = new("istype", ScalarType.Bool, token.Location, ((int)tested).ToString(CultureInfo.InvariantCulture), Left: subject);
                    narrowed = new("downcast", tested, token.Location, Left: subject);
                }
            }
            var covers = true;
            if (Current.Text == "(")
            {
                // T(P1, ..., Pn) on a positional record: the positional members are matched in order (the synthesized Deconstruct).
                var open = Next(); var record = IsStruct(tested) && !IsObject(tested) ? StructOf(tested) : null;
                if(Has(ScalarFeatures.RecordMembers)&&record is not null)return RecordPositionalPattern(subject,narrowed,test,record,open) with{Narrowed=narrowed};
                if (record is not { IsRecord: true, Positional: > 0 }) { Fail("AI3100", $"A positional pattern needs a positional record; '{typeToken.Text}' has no positional members.", open.Location); return null!; }
                var count = 0; var discards = true;
                if (Current.Text != ")")
                    do
                    {
                        if (count >= record.Positional) Fail("AI3106", $"'{record.Name}' has {record.Positional} positional member(s).", Current.Location);
                        var read = new ScalarExpression("field", record.Fields[count].Type, Current.Location, count.ToString(CultureInfo.InvariantCulture), Left: narrowed);
                        var inner = Pattern(read, true, true);
                        discards &= inner.IsDiscard;
                        test = And(test, inner.Test, open.Location); count++;
                    } while (Take(","));
                Expect(")");
                if (count != record.Positional) Fail("AI3106", $"'{record.Name}' has {record.Positional} positional member(s); the pattern has {count}.", open.Location);
                covers = discards;
            }
            if (Current.Text == "{") { var open = Current; test = PropertyPattern(narrowed, subject, open, test).Test; covers = false; }
            if (IsSourceIdentifier(Current.Text) && Current.Text is not ("and" or "or" or "when" or "not") && !IsReserved(Current.Text))
            {
                var name = Next();
                test = And(test, DeclarePatternVariable(name, tested, narrowed), name.Location);
            }
            return new(test, null, false, token.Location) { Covers = covers ? tested : null,Narrowed=narrowed };
        }
        private PatternResult PropertyPattern(ScalarExpression value, ScalarExpression subject, Lexeme open, ScalarExpression? guard)
        {
            Expect("{");
            var reference = IsReference(subject.Type)||Has(ScalarFeatures.Boxing)&&subject.Type==ScalarType.String;
            ScalarExpression test = guard ?? (reference
                ? new("!=", ScalarType.Bool, open.Location, Left: subject, Right: new("null", subject.Type, open.Location)) { Wraps = _wraps }
                : Fold(new("literal", ScalarType.Bool, open.Location, "true")));
            if (Current.Text != "}" && (IsObject(value.Type) || !IsStruct(value.Type))&&!(Has(ScalarFeatures.Boxing)&&value.Type==ScalarType.String))
                Fail("AI3100", $"Property patterns over {Show(value.Type)} are not supported.", Current.Location);
            while (Current.Text != "}")
            {
                var member = Next();
                if (!IsSourceIdentifier(member.Text)) Fail("AI3106", "Expected a member name in a property pattern.", member.Location);
                var read = Has(ScalarFeatures.Boxing)?PatternPropertyRead(value,member):FieldRead(value, member);
                // Extended property patterns a.b: P mean a: { b: P } (with the null test for a reference a).
                var path = new List<ScalarExpression>();
                while (Take("."))
                {
                    if(Has(ScalarFeatures.Boxing)&&(IsReference(read.Type)||read.Type==ScalarType.String)){var cached=CachedPatternValue(read);path.Add(cached.Ensure);read=cached.Read;}
                    if (IsReference(read.Type)||Has(ScalarFeatures.Boxing)&&read.Type==ScalarType.String) path.Add(new("!=", ScalarType.Bool, member.Location, Left: read, Right: new("null", read.Type, member.Location)) { Wraps = _wraps });
                    if ((!IsStruct(read.Type) || IsObject(read.Type))&&!(Has(ScalarFeatures.Boxing)&&read.Type==ScalarType.String)) Fail("AI3100", $"Property patterns over {Show(read.Type)} are not supported.", Current.Location);
                    member = Next(); read = Has(ScalarFeatures.Boxing)?PatternPropertyRead(read,member):FieldRead(read, member);
                }
                Expect(":");
                foreach (var check in path) test = And(test, check, member.Location);
                var inner = Pattern(read, Has(ScalarFeatures.RecordMembers), true);
                test = And(test, inner.Test, member.Location);
                if (!Take(",")) break;
            }
            Expect("}");
            return new(test, null, false, open.Location);
        }
        private ScalarExpression PatternConstant(ScalarExpression subject)
        {
            _noLambda++; ScalarExpression value;
            try { value = Expression(SwitchPrecedence); } finally { _noLambda--; }
            Read(value);
            value = Coerce(value, subject.Type);
            if (PatternValue(value) is null) Fail("AI3113", "A pattern requires a compile-time constant.", value.Location);
            return value;
        }
        // The compile-time value of a pattern constant; string literals are constants of their decoded text.
        private object? PatternValue(ScalarExpression value) =>
            ConstantOf(value) ?? (value is { Operation: "literal", Type: ScalarType.String, Value: { } text } ? text
                : value is { Operation: "null", Type: ScalarType.String } && Has(ScalarFeatures.NullStrings) ? NullStringPattern : null);
        // N26: the constant of a 'null' pattern over a string subject.
        private static readonly object NullStringPattern = new();
        private static string ShowConstant(object value) => value switch
        {
            _ when ReferenceEquals(value, NullStringPattern) => "null",
            string text => "\"" + text + "\"", bool truth => truth ? "true" : "false", _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?"
        };
        // e is P: evaluates e once into a hidden local and tests the pattern (lowered as e switch { P => true, _ => false }).
        private ScalarExpression IsExpression(ScalarExpression subject, Lexeme keyword)
        {
            if (subject.Type == ScalarType.Unit) Fail("AI3107", "A unit value cannot be matched.", subject.Location);
            var simple = SimpleTypeTest();
            if (simple && IsClass(subject.Type) && (!IsLocalName(Current.Text) || ArrayTypeAhead()) && (Current.Text == "not" && (StructNamedAny(_tokens[_position + 1].Text) is { IsClass: true } || ArrayTypeAhead(1)) || StructNamedAny(Current.Text) is { IsClass: true } || ArrayTypeAhead()))
            {
                // r is D / r is not D on a class or interface reference: D must be a proper subtype or a possible interface conversion (a test that is always true for non-null is rejected).
                var negated = Take("not"); var typeToken = TypeToken(); var tested = Type(typeToken.Text, typeToken.Location);
                int s0 = IndexOf(subject.Type), t0 = IndexOf(tested);
                // C# allows every class test on a value of a type parameter type (and so in an instance of a generic function).
                var lenient = Lenient(subject.Type, tested);
                if (!lenient && Derives(_structs, s0, t0)) Fail("AI3100", $"'{Show(subject.Type)}' is always '{typeToken.Text}' when not null; compare with null instead.", subject.Location);
                if (!lenient && !Derives(_structs, t0, s0) && !CastPossible(s0, t0)) Fail("AI3100", $"A '{Show(subject.Type)}' value is never a '{typeToken.Text}'.", typeToken.Location);
                ScalarExpression classTest = new("istype", ScalarType.Bool, keyword.Location, ((int)tested).ToString(CultureInfo.InvariantCulture), Left: subject);
                return negated ? new("unary!", ScalarType.Bool, keyword.Location, Left: classTest) : classTest;
            }
            if (IsObject(subject.Type) && simple)
            {
                // o is C / o is not C / o is object: a run-time class test; false for null.
                var negated = Take("not");
                // o is Box<int>: a closed generic class test reads the whole type token.
                if (!(QualifiedBuiltinAt(_position) is not null || Current.Text == "object" || Has(ScalarFeatures.Boxing)&&PatternTypeName(Current.Text) || StructNamedAny(Current.Text) is { IsClass: true } || ArrayTypeAhead()) || IsLocalName(Current.Text) && !ArrayTypeAhead() && QualifiedBuiltinAt(_position) is null)
                    Fail("AI3100", "Only class type tests are supported on object values.", Current.Location);
                var typeToken = TypeToken();
                var tested = Type(typeToken.Text, typeToken.Location);
                if(TryBoxPattern(subject,tested,typeToken.Location) is {} boxedPattern)return negated?new("unary!",ScalarType.Bool,keyword.Location,Left:boxedPattern.Test):boxedPattern.Test;
                if (!IsObject(tested) && !IsClass(tested)) Fail("AI3100", "Only class type tests are supported on object values.", typeToken.Location);
                ScalarExpression test = IsObject(tested)
                    ? new("!=", ScalarType.Bool, keyword.Location, Left: subject, Right: new("null", subject.Type, keyword.Location))
                    : new("istype", ScalarType.Bool, keyword.Location, ((int)tested).ToString(CultureInfo.InvariantCulture), Left: subject);
                return negated ? new("unary!", ScalarType.Bool, keyword.Location, Left: test) : test;
            }
            if (!PatternSubject(subject.Type) && !(Has(ScalarFeatures.Classes) && (IsStruct(subject.Type) || IsObject(subject.Type) || IsNull(subject.Type))))
                Fail("AI3100", $"Patterns over {Show(subject.Type)} are not supported.", subject.Location);
            if (IsNull(subject.Type)) Fail("AI3100", "Patterns over the null literal are not supported.", subject.Location);
            var (hidden,patternOperation) = CapturePatternSubject(subject);
            var read = new ScalarExpression("local", subject.Type, keyword.Location, hidden.EmittedName);
            var (pattern, declared, negatedDeclared) = DeclaringPattern(read, false, true, true);
            var no = Fold(new("literal", ScalarType.Bool, keyword.Location, "false"));
            var yes = Fold(new("literal", ScalarType.Bool, keyword.Location, "true"));
            ScalarExpression result = new(patternOperation, ScalarType.Bool, keyword.Location, hidden.EmittedName, Left: subject)
            {
                Arguments = [pattern.Test, yes, Fold(new("literal", ScalarType.Bool, keyword.Location, "true")), no], Wraps = _wraps
            };
            return WithFlow(result, negatedDeclared ? [] : declared, negatedDeclared ? declared : []);
        }
        // [not] T followed by a token that ends the pattern: the N10/N11 class test (always-true and impossible tests are rejected there).
        private bool SimpleTypeTest()
        {
            var saved = _position; var originalTokens = _tokens; _tokens = [.. _tokens];
            try
            {
                Take("not");
                if (QualifiedBuiltinAt(_position) is null && (IsLocalName(Current.Text) && !ArrayTypeAhead() || !(Current.Text == "object" || StructNamedAny(Current.Text) is not null || ArrayTypeAhead()))) return false;
                TypeToken();
                return !(IsSourceIdentifier(Current.Text) && Current.Text is not ("and" or "or" or "when") || Current.Text is "{" or "(" or "and" or "or");
            }
            finally { _position = saved; _tokens = originalTokens; }
        }
        // e as T: T for a class, interface or object type; null when e is null or not a T (e is evaluated once).
        private ScalarExpression AsExpression(ScalarExpression subject, Lexeme keyword)
        {
            var typeToken = TypeToken(); var target = Type(typeToken.Text, typeToken.Location);
            if(Has(ScalarFeatures.Boxing))
            {
                if(_checkOnly&&IsTypeParameterType(subject.Type)&&IsReference(target))return new("null",target,keyword.Location);
                if(target==ScalarType.String)
                {
                    if(IsNull(subject.Type))return new("null",ScalarType.String,keyword.Location);
                    if(subject.Type==ScalarType.String)return subject;
                    if(IsObject(subject.Type))return ObjectToString(subject,keyword.Location,true);
                    if(BuiltinEquatableConversion(ScalarType.String,subject.Type))return ObjectToString(AsObject(subject),keyword.Location,true);
                    Fail("AI3118","This reference type cannot be converted to string.",typeToken.Location);
                }
                if(subject.Type==ScalarType.String&&IsObject(target))return StringToObject(subject);
                if(subject.Type==ScalarType.String&&BuiltinEquatableConversion(ScalarType.String,target))return StringToEquatable(subject,target);
                if(Boxable(subject.Type)&&(IsObject(target)||ValueInterfaceConversion(subject.Type,target)))return BoxValue(subject,target);
                if(_typeArgs is {Count:>0}&&IsClass(target)&&StructOf(target).IsInterface)
                {
                    if(Boxable(subject.Type))subject=BoxValue(subject,(ScalarType)ObjectTypeId);
                    else if(subject.Type==ScalarType.String)subject=StringToObject(subject);
                }
                if(!IsReference(subject.Type)&&IsClass(target)&&StructOf(target).IsInterface)Fail("AI3118","This value has no boxing conversion to the requested interface.",typeToken.Location);
            }
            if (!IsReference(target)) Fail("AI3100", $"The 'as' operator needs a reference type; '{typeToken.Text}' is a value type (nullable value types are not supported).", typeToken.Location);
            if (IsNull(subject.Type)) return subject with { Type = target };
            if (!IsReference(subject.Type)) Fail("AI3100", $"Boxing ({Show(subject.Type)} as {typeToken.Text}) is not supported.", subject.Location);
            if (subject.Type == target || IsObject(target) || IsClass(subject.Type) && IsClass(target) && Derives(_structs, IndexOf(subject.Type), IndexOf(target)))
                return ReferenceCast(target, subject, keyword.Location);
            var hidden = HiddenLocal(subject.Type);
            var read = new ScalarExpression("local", subject.Type, keyword.Location, hidden.EmittedName);
            var narrowed = ReferenceCast(target, read, keyword.Location);
            var test = new ScalarExpression("istype", ScalarType.Bool, keyword.Location, ((int)target).ToString(CultureInfo.InvariantCulture), Left: read);
            return new("switch", target, keyword.Location, hidden.EmittedName, Left: subject)
            {
                Arguments = [test, narrowed, Fold(new("literal", ScalarType.Bool, keyword.Location, "true")), new("null", target, keyword.Location)], Wraps = _wraps
            };
        }
        // e switch { P [when g] => v, ... }: arms are tried in order; no matching arm throws SwitchExpressionException.
        private ScalarExpression SwitchExpression(ScalarExpression subject)
        {
            var keyword = Next(); Read(subject);
            if (subject.Type == ScalarType.Unit) Fail("AI3107", "A unit value cannot be matched.", subject.Location);
            var objects = !PatternSubject(subject.Type) && Has(ScalarFeatures.Classes) && (IsStruct(subject.Type) || IsObject(subject.Type));
            if (!PatternSubject(subject.Type) && !objects) Fail("AI3100", $"Patterns over {Show(subject.Type)} are not supported.", subject.Location);
            var covered = new List<ScalarType>();
            var (hidden,patternOperation) = CapturePatternSubject(subject);
            var read = new ScalarExpression("local", subject.Type, keyword.Location, hidden.EmittedName);
            NewLines(); Expect("{"); NewLines();
            var arms = new List<(ScalarExpression Test, ScalarExpression Value)>();
            var seen = new List<object>(); var closed = false;
            while (Current.Text != "}")
            {
                var armStart = Current.Location;
                if (closed) Fail("AI3106", "The pattern has already been handled by a previous arm of the switch expression.", armStart);
                _scopes.Add(new(StringComparer.Ordinal)); var armAssigned = new HashSet<string>(_assigned);
                var (pattern, declared, _) = DeclaringPattern(read, true, objects, false);
                if (pattern.Covers is { } coveredType && covered.Any(c => c == coveredType || IsClass(c) && IsClass(coveredType) && Derives(_structs, IndexOf(coveredType), IndexOf(c)) || IsObject(c)))
                    Fail("AI3106", "The pattern has already been handled by a previous arm of the switch expression.", pattern.Location);
                _assigned.UnionWith(declared);
                ScalarExpression test = pattern.Test;
                var guarded = false;
                if (Current.Text == "when")
                {
                    Next(); _noLambda++; ScalarExpression guard;
                    try { guard = Expression(); } finally { _noLambda--; }
                    Read(guard); Require(guard, ScalarType.Bool, "AI3101"); guarded = true;
                    _assigned.UnionWith(Flow(guard).T);
                    test = Fold(new("&&", ScalarType.Bool, guard.Location, Left: test, Right: guard) { Wraps = _wraps });
                }
                if (!guarded && pattern.Covers is { } covers) { covered.Add(covers); if (covers == subject.Type && !IsReference(covers)) closed = true; }
                if (pattern.Constants is { } ks)
                {
                    if (ks.All(k => seen.Any(x => x.Equals(k)))) Fail("AI3106", $"The pattern {string.Join(" or ", ks.Select(ShowConstant))} has already been handled by a previous arm of the switch expression.", pattern.Location);
                    if (!guarded) seen.AddRange(ks);
                }
                if (pattern.IsDiscard && !guarded) closed = true;
                if (Current.Text != "=>") Fail("AI3106", "Expected '=>' in a switch expression arm.");
                Next();
                var value = Expression(); Read(value);
                if (value.Type == ScalarType.Unit) Fail("AI3110", "A switch expression arm must have a value.", value.Location);
                arms.Add((test, value));
                _assigned = armAssigned; _scopes.RemoveAt(_scopes.Count - 1);
                NewLines();
                if (!Take(",")) break;
                NewLines();
            }
            NewLines(); Expect("}");
            if (arms.Count == 0) Fail("AI3106", "A switch expression needs at least one arm.", keyword.Location);
            // The result type is the best common type: the first arm type that every arm converts to.
            ScalarType? best = null;
            foreach (var candidate in arms.Select(a => a.Value.Type).Distinct())
                if (arms.All(a => TryCoerce(a.Value, candidate) is not null)) { best = candidate; break; }
            if (best is null || IsNull(best.Value)) Fail("AI3107", "No best type was found for the switch expression arms.", keyword.Location);
            var arguments = new List<ScalarExpression>();
            foreach (var (test, value) in arms) { arguments.Add(test); arguments.Add(Coerce(value, best!.Value)); }
            return new(patternOperation, best!.Value, keyword.Location, hidden.EmittedName, Left: subject) { Arguments = arguments, Wraps = _wraps };
        }
        // switch (e) { case P [when g]: ... default: ... }: no fall-through; break leaves the switch; sections are tried in order, default last.
        private (ScalarStatement Statement, bool Returns) SwitchStatement()
        {
            var keyword = Next();
            Expect("("); var subject = Expression(); Read(subject); Expect(")");
            if (subject.Type == ScalarType.Unit) Fail("AI3107", "A unit value cannot be matched.", subject.Location);
            var objects = !PatternSubject(subject.Type) && Has(ScalarFeatures.Classes) && (IsStruct(subject.Type) || IsObject(subject.Type));
            if (!PatternSubject(subject.Type) && !objects) Fail("AI3100", $"switch over {Show(subject.Type)} is not supported.", subject.Location);
            var switchPending = new List<ScalarStatement>(); var covered = new List<ScalarType>();
            var (hidden,patternOperation) = CapturePatternSubject(subject);
            var read = new ScalarExpression("local", subject.Type, keyword.Location, hidden.EmittedName);
            NewLines(); Expect("{"); NewLines();
            var before = new HashSet<string>(_assigned);
            var flags = new LoopFlags { IsSwitch = true }; _loops.Push(flags);
            var sections = new List<(ScalarExpression? Test, IReadOnlyList<ScalarStatement> Body)>();
            IReadOnlyList<ScalarStatement>? fallback = null;
            var seen = new List<object>();
            while (Current.Text != "}")
            {
                ScalarExpression? test = null; var isDefault = false; var firstLabel = Current;
                _scopes.Add(new(StringComparer.Ordinal)); var sectionDeclared = new List<string>(); var labels = 0;
                while (Current.Text is "case" or "default")
                {
                    var label = Next();
                    if (label.Text == "default")
                    {
                        if (fallback is not null || isDefault) Fail("AI3106", "The switch statement contains multiple default labels.", label.Location);
                        isDefault = true; Expect(":"); NewLines(); continue;
                    }
                    _assigned = new(before); labels++;
                    var (pattern, declared, _) = DeclaringPattern(read, false, objects, false);
                    if (pattern.Covers is { } coveredType && covered.Any(c => c == coveredType || IsClass(c) && IsClass(coveredType) && Derives(_structs, IndexOf(coveredType), IndexOf(c)) || IsObject(c)))
                        Fail("AI3106", "The switch case is unreachable: it is subsumed by a previous case.", pattern.Location);
                    sectionDeclared.AddRange(declared); _assigned.UnionWith(declared);
                    var labelTest = pattern.Test; var guarded = false;
                    if (Current.Text == "when")
                    {
                        Next(); var guard = Expression(); Read(guard); Require(guard, ScalarType.Bool, "AI3101"); guarded = true;
                        labelTest = Fold(new("&&", ScalarType.Bool, guard.Location, Left: labelTest, Right: guard) { Wraps = _wraps });
                    }
                    if (!guarded && pattern.Covers is { } covers) covered.Add(covers);
                    if (pattern.Constants is { } ks)
                    {
                        if (ks.All(k => seen.Any(x => x.Equals(k))))
                            Fail("AI3106", guarded || ks.Count > 1 ? "The switch case is unreachable: it is subsumed by a previous case." : $"The switch statement contains multiple cases with the label value {ShowConstant(ks[0])}.", pattern.Location);
                        if (!guarded) seen.AddRange(ks);
                    }
                    test = test is null ? labelTest : Fold(new("||", ScalarType.Bool, label.Location, Left: test, Right: labelTest) { Wraps = _wraps });
                    Expect(":"); NewLines();
                }
                if (test is null && !isDefault) Fail("AI3106", "Expected 'case' or 'default' in a switch statement.");
                _assigned = new(before);
                // Variables of a section with one case label (and no default) are assigned in its body.
                if (labels == 1 && !isDefault) _assigned.UnionWith(sectionDeclared);
                switchPending.AddRange(_pendingDeclarations); _pendingDeclarations.Clear();
                _scopes.Add(new(StringComparer.Ordinal));
                var statements = new List<ScalarStatement>(); var terminates = false;
                while (Current.Text is not "case" and not "default" and not "}" and not "")
                {
                    _directSection = _scopes.Count; var (statement, ends) = Statement(); _directSection = -1;
                    if (_pendingDeclarations.Count > 0) { statements.AddRange(_pendingDeclarations); _pendingDeclarations.Clear(); }
                    statements.Add(statement); terminates |= ends; NewLines();
                }
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
                if (!terminates)
                    Fail("AI3106", "Control cannot fall through from one case label to another.", firstLabel.Location);
                // A section that is both a case and the default runs whenever no earlier case matches.
                if (isDefault) fallback = statements;
                else sections.Add((test, statements));
            }
            Expect("}");
            _loops.Pop();
            // Reaching the end: through a break, or because no case matched and there is no default.
            var reaching = flags.BreakStates.ToList();
            if (fallback is null) reaching.Add(before);
            if (reaching.Count > 0) { _assigned = new(reaching[0]); foreach (var state in reaching.Skip(1)) _assigned.IntersectWith(state); }
            else _assigned = new(before);
            IReadOnlyList<ScalarStatement> chain = fallback ?? [];
            for (var i = sections.Count - 1; i >= 0; i--)
                chain = [new ScalarStatement("if", keyword.Location, sections[i].Test, Body: sections[i].Body, Alternative: chain)];
            var subjectLocal = patternOperation is "switch" or "switch-copy"?new ScalarStatement("local",keyword.Location,subject,hidden.EmittedName,subject.Type){Mutable=patternOperation=="switch-copy"}
                :new ScalarStatement("ref-local",keyword.Location,subject,hidden.EmittedName,subject.Type){ReferenceMode=patternOperation=="switch-ref"?ScalarParameterMode.Ref:ScalarParameterMode.RefReadonly,Scoped=true};
            var lowered = new ScalarStatement("block", keyword.Location, Body: [subjectLocal, new ScalarStatement("switch", keyword.Location, Body: chain)]);
            _pendingDeclarations.AddRange(switchPending);
            return (lowered, fallback is not null && !flags.Breaks);
        }
        private static bool IsTypeKeyword(string name) => name is "int" or "i32" or "bool" or "long" or "uint" or "ulong" or "short" or "i16" or "ushort" or "byte" or "u8" or "sbyte" or "i8"
            or "char" or "string" or "decimal" or "f32" or "f64" or "float" or "double" or "void" or "unit" or "enum" or "struct" or "object";
        public ScalarEnum BindEnum(int index)
        {
            Next(); // enum
            var name = Next();
            if (!IsSourceIdentifier(name.Text) || IsReserved(name.Text) || IsTypeKeyword(name.Text))
                Fail("AI3106", $"Invalid enum name '{name.Text}'.", name.Location);
            if (_enums.Any(e => e.Name == IdentifierName(name.Text))) Fail("AI3106", $"Duplicate type '{name.Text}'.", name.Location);
            if(index>=1000)Fail("AI3100","More than 1000 enum identities are unsupported.",name.Location);
            var parameters=new List<string>();ScalarStruct? genericOwner=null;
            if(Take("<"))
            {
                var binding=CompilationBinding(name.Name);
                if(!Has(ScalarFeatures.Generics)||binding is not {Arity:0,CapturedArity:>0,DeclaringType:not null})Fail("AI3106","An enum cannot declare its own type parameters.",name.Location);
                do {var parameter=Next();if(!IsSourceIdentifier(parameter.Text)||parameters.Contains(parameter.Name))Fail("AI3106","Invalid captured enum parameter.",parameter.Location);parameters.Add(parameter.Name);}while(Take(","));Expect(">");
                genericOwner=_structs.FirstOrDefault(s=>s.Name==binding!.DeclaringType);
                if(genericOwner is null||parameters.Count!=binding!.CapturedArity||genericOwner.TypeParameters.Count!=parameters.Count)Fail("AI3106","The captured enum parameters do not match their containing declaration.",name.Location);
                _typeArgs=parameters.Select((p,i)=>(p,Type:genericOwner!.TypeArgumentList[i])).ToDictionary(p=>p.p,p=>p.Type,StringComparer.Ordinal);
            }
            var underlying = ScalarType.Int;
            if (Take(":"))
            {
                var type = TypeHeadToken();
                var importedIntegral=ImportAliasType(type.Text,type.Location) ?? (QualifiedBuiltinAlias(type.Text) is null?null:(ScalarType?)Type(type.Text,type.Location));
                if ((importedIntegral??ScalarConstants.FromSpelling(type.Text)) is not { } integral || !ScalarConstants.IsInteger(integral) || integral == ScalarType.Bool || ScalarConstants.IsNative(integral))
                { Fail("AI3106", "The underlying type of an enum must be sbyte, byte, short, ushort, int, uint, long or ulong.", type.Location); return null!; }
                underlying = integral;
            }
            if(parameters.Count>0)BindTypeConstraints(new GenericInfo(name.Name,name.Location.Offset,parameters.Select(p=>new TypeParameterInfo(p,name.Location)).ToList()));
            NewLines(); Expect("{"); NewLines();
            var members = new List<KeyValuePair<string, BigInteger>>();
            BigInteger? previous = null;
            while (Current.Text != "}")
            {
                var member = Next();
                if (!IsSourceIdentifier(member.Text) || IsReserved(member.Text))
                    Fail("AI3106", $"Invalid enum member name '{member.Text}'.", member.Location);
                if (members.Any(m => m.Key == member.Name)) Fail("AI3106", $"Duplicate enum member '{member.Text}'.", member.Location);
                BigInteger value;
                if (Take("="))
                {
                    // C#: inside the declaration, other members are constants of the underlying type; the value converts implicitly to it.
                    ScalarExpression initializer;var savedConst=_bindingConst;_bindingConst=true;
                    try{initializer=Expression();}finally{_bindingConst=savedConst;}Read(initializer);
                    if (ConstantOf(initializer) is not BigInteger)
                        Fail("AI3113", "An enum member value must be a compile-time integer constant.", initializer.Location);
                    value = (BigInteger)ConstantOf(Coerce(initializer, underlying))!;
                }
                else
                {
                    value = previous is null ? BigInteger.Zero : previous.Value + 1;
                    if (!ScalarConstants.Fits(value, underlying))
                        Fail("AI3111", $"The value of enum member '{member.Text}' is outside the range of {ScalarConstants.Spelling(underlying)}.", member.Location);
                }
                members.Add(new(member.Name, value)); previous = value;
                var local = Declare(member.Text, underlying, false, member.Location);
                _constLocals[local.EmittedName] = value; _assigned.Add(local.EmittedName);
                NewLines();
                if (!Take(",")) { if (Current.Text != "}") Fail("AI3106", "Expected ',' or '}' in the enum body."); break; }
                NewLines();
            }
            Expect("}");
            return SaveEnumRuntimeDefinition(new(IdentifierName(name.Text), (ScalarType)(EnumTypeBase + index), underlying, members, name.Location) { SourceNamespace=CompilationBinding(name.Name)?.Namespace, SourceName=CompilationBinding(name.Name)?.Name, SourceFullName=CompilationBinding(name.Name)?.FullName, SourceRuntimeFullName=CompilationBinding(name.Name)?.RuntimeFullName,SourceOwnArity=CompilationBinding(name.Name)?.Arity,SourceTypeParameterNames=CompilationBinding(name.Name)?.TypeParameters??[],Template=parameters.Count==0?-1:index,GenericOwner=genericOwner?.Id,TypeParameters=Array.AsReadOnly(parameters.ToArray()),IsCheckInstance=parameters.Count>0 });
        }
        private ScalarType EraseType(ScalarType type) => IsEnum(type) ? EnumOf(type).Underlying : NativeStorage(type);
        private ScalarExpression EraseExpression(ScalarExpression e) => e.Operation == "retype" ? EraseExpression(e.Left!)
            : EraseNative(e);
        private ScalarStatement EraseStatement(ScalarStatement s) => s with
        {
            Type = EraseType(s.Type), Expression = s.Expression is null ? null : EraseExpression(s.Expression),
            Body = s.Body?.Select(EraseStatement).ToList(), Alternative = s.Alternative?.Select(EraseStatement).ToList(),
            Initializer = s.Initializer is null ? null : EraseStatement(s.Initializer), Increment = s.Increment is null ? null : EraseExpression(s.Increment)
        };
        private ScalarFunction Erase(ScalarFunction f) => f with
        {
            Parameters = f.Parameters.Select(p => p with { Type = EraseType(p.Type) }).ToList(), ReturnType = EraseType(f.ReturnType),
            Body = f.Body.Select(EraseStatement).ToList(), Contracts = f.Contracts.Select(c => c with { Expression = EraseExpression(c.Expression) }).ToList()
        };
        // C# enum operators (ECMA-334 12.12.6, 12.10.5, 12.10.6, 12.13.3): ==, !=, <, <=, >, >= and & | ^ on one enum type E (a constant zero converts to E);
        // E + U, U + E and E - U give E; E - E gives U; every operation runs on the underlying values and the result is narrowed back to U.
        private ScalarExpression EnumBinary(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            var enumType = IsEnum(left.Type) ? left.Type : right.Type;
            var u = EnumOf(enumType).Underlying;
            ScalarExpression ToEnum(ScalarExpression e) => TryCoerce(e, enumType)
                ?? throw Error("AI3107", $"Operator '{symbol}' needs two {Show(enumType)} operands; found {Show(e.Type)}.", e.Location);
            ScalarExpression ToUnderlying(ScalarExpression e) => TryCoerce(e, u)
                ?? throw Error("AI3107", $"Operator '{symbol}' combines {Show(enumType)} with {ScalarConstants.Spelling(u)} only; found {Show(e.Type)}.", e.Location);
            ScalarExpression Narrow(ScalarExpression e, bool wraps) => e.Type == u ? e : Fold(new("cast", u, at, Left: e) { Wraps = wraps });
            switch (symbol)
            {
                case "==" or "!=" or "<" or "<=" or ">" or ">=":
                    return MakeBinary(symbol, Retype(ToEnum(left), u), Retype(ToEnum(right), u), at);
                case "&" or "|" or "^":
                    return Retype(Narrow(MakeBinary(symbol, Retype(ToEnum(left), u), Retype(ToEnum(right), u), at), true), enumType);
                case "+":
                    if (IsEnum(left.Type) && IsEnum(right.Type)) Fail("AI3107", "Operator '+' is not defined for two enum operands.", at);
                    return Retype(Narrow(IsEnum(left.Type) ? MakeBinary("+", Retype(left, u), ToUnderlying(right), at)
                        : MakeBinary("+", ToUnderlying(left), Retype(right, u), at), _wraps), enumType);
                case "-":
                    if (!IsEnum(left.Type)) Fail("AI3107", $"Operator '-' is not defined for {Show(left.Type)} and {Show(right.Type)}.", at);
                    // E - E (a constant zero also converts to E, as C# overload resolution picks) yields the underlying type.
                    if (IsEnum(right.Type) || TryCoerce(right, enumType) is not null)
                        return Narrow(MakeBinary("-", Retype(left, u), Retype(ToEnum(right), u), at), _wraps);
                    return Retype(Narrow(MakeBinary("-", Retype(left, u), ToUnderlying(right), at), _wraps), enumType);
                default:
                    Fail("AI3107", $"Operator '{symbol}' is not defined for enum operands.", at); return null!;
            }
        }
        private ScalarExpression EnumCast(ScalarType castType, ScalarExpression operand, SourceLocation at)
        {
            var source = IsEnum(operand.Type) ? Retype(operand, EnumOf(operand.Type).Underlying) : operand;
            if (!IsNum(source.Type)) Fail("AI3118", $"Cannot convert {Show(operand.Type)} to {Show(castType)}.", operand.Location);
            var target = IsEnum(castType) ? EnumOf(castType).Underlying : castType;
            var converted = source.Type == target ? source : Fold(new("cast", target, at, Left: source) { Wraps = _wraps });
            return IsEnum(castType) ? Retype(converted, castType) : converted;
        }
        private Local Declare(string name, ScalarType type, bool mutable, SourceLocation location)
        {
            if (!IsEscapedIdentifier(name) && (!IsIdentifier(name) || IsReserved(name) || Has(ScalarFeatures.Classes) && name is "null" or "class" or "object" || name == "this" && _this is not null))
                Fail("AI3106", $"Invalid local name '{name}'.", location);
            name = IdentifierName(name);
            if (_scopes[^1].ContainsKey(name)) Fail("AI3106", $"Duplicate local '{name}'.", location);
            var local = new Local($"__scalar_local_{_nextId++}", type, mutable);
            _referenceContexts[local.EmittedName] = _scopes.Count + 1;
            _scopes[^1].Add(name, local);
            return local;
        }
        private static bool IsReserved(string name) => (ActiveFeatures & ScalarFeatures.EscapedIdentifiers) != 0 && ReservedTypeParameterNames.Contains(name) || name is "var" or "let" or "if" or "else" or "while" or "do" or "const"
            or "checked" or "unchecked" or "for" or "break" or "continue" or "return" or "true" or "false" or "int" or "bool"
            or "long" or "uint" or "ulong" or "short" or "i16" or "ushort" or "byte" or "u8" or "sbyte" or "i8";
        private Local Lookup(Lexeme token)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(IdentifierName(token.Text), out var local)) return local;
            Fail("AI3103", $"Unresolved or out-of-scope local '{token.Text}'.", token.Location);
            return null!;
        }
        private (IReadOnlyList<ScalarStatement> Body, bool Returns) Block(bool braces = true)
        {
            if (braces) { NewLines(); Expect("{"); _scopes.Add(new(StringComparer.Ordinal)); }
            NewLines();
            var statements = new List<ScalarStatement>();
            var returns = false;
            var outerPending = new List<ScalarStatement>(_pendingDeclarations); _pendingDeclarations.Clear();
            while (Current.Text is not "" and not "}")
            {
                var (statement, terminates) = Statement();
                if (_pendingDeclarations.Count > 0) { statements.AddRange(_pendingDeclarations); _pendingDeclarations.Clear(); }
                statements.Add(statement);
                returns |= terminates;
                NewLines();
            }
            if (braces)
            {
                Expect("}");
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
            }
            _pendingDeclarations.AddRange(outerPending);
            return (statements, returns);
        }
        private (ScalarStatement Statement, bool Returns) Statement()
        {
            var token = Current;
            var allowBase = _baseCallAllowed; _baseCallAllowed = false;
            if (token.Text == "base" && Has(ScalarFeatures.Classes) && _position + 2 < _tokens.Count && _tokens[_position + 1].Text == "." && _tokens[_position + 2].Text == "new")
            {
                if (!allowBase) Fail("AI3106", "'base.new(...)' is allowed only as the first statement of a constructor of a derived class.", token.Location);
                Next(); Next(); Next(); Expect("(");
                var arguments = new List<ScalarExpression>();
                _inBaseArgs = true;
                try
                {
                    if (!Take(")"))
                    {
                        do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                        Expect(")");
                    }
                }
                finally { _inBaseArgs = false; }
                _baseCalled = true;
                var call = BaseConstructorCall(arguments, token.Location);
                EndStatement(); return (call, false);
            }
            if (token.Text == "switch" && Has(ScalarFeatures.Patterns)) return SwitchStatement();
            if (token.Text == "try" && Has(ScalarFeatures.Exceptions)) return TryStatement();
            if (token.Text == "throw" && Has(ScalarFeatures.Exceptions)) return ThrowStatement();
            if (token.Text == "foreach" && Has(ScalarFeatures.Foreach)) return ForeachStatement();
            if (token.Text == "using" && Has(ScalarFeatures.Using) && Has(ScalarFeatures.Exceptions) && Has(ScalarFeatures.Classes)) return UsingStatement();
            if (token.Text is "foreach" or "goto" or "try" or "throw" or "using" or "match" or "switch"
                or "async" or "taskgroup" or "spawn" or "lock" || token.Text == "await" && !Has(ScalarFeatures.Async)
                || token.Text == "do" && !Has(ScalarFeatures.Control) || token.Text == "const" && !Has(ScalarFeatures.Control)
                || token.Text is "checked" or "unchecked" && !Has(ScalarFeatures.Checked))
                Fail("AI3106", $"Statement '{token.Text}' is outside the scalar capability.", token.Location);
            if (token.Text is "checked" or "unchecked" && _tokens[_position + 1].Text != "(")
            {
                Next(); var saved = _wraps; _wraps = token.Text == "unchecked";
                try { var scoped = Block(); return (new("block", token.Location, Body: scoped.Body), scoped.Returns); }
                finally { _wraps = saved; }
            }
            if (token.Text == "ref" || token.Text == "scoped" && TokenAt(_position + 1).Text == "ref")
            { var declaration = PrefixReferenceDeclaration(); EndStatement(); return (declaration, false); }
            if (token.Text is "let" or "var")
            {
                var declaration = Declaration(); EndStatement(); return (declaration, false);
            }
            if (token.Text == "const")
            {
                var declaration = ConstDeclaration(); EndStatement(); return (declaration, false);
            }
            if (token.Text == "yield" && Has(ScalarFeatures.Iterators) && TokenAt(_position + 1).Text is "return" or "break") return YieldStatement();
            if (Current.Text == "return" && _iterElem is not null && _lambdaNest == 0)
                Fail("AI3106", "Cannot return a value from an iterator. Use the yield return statement to return a value, or yield break to end the iteration.", token.Location);
            if (Current.Text == "return" && _finallyLoopDepth.Count > 0) Fail("AI3106", "Control cannot leave the body of a finally clause.", token.Location);
            if (Take("return"))
            {
                if (Take("ref")) return (ReferenceReturn(token.Location), true);
                if (_returnMode != ScalarParameterMode.Value) Fail("AI3107", "A reference-returning function requires return ref.", token.Location);
                ScalarExpression? expression = Current.Text is "\n" or ";" or "}" or "" ? null : Expression();
                if (_ctor)
                {
                    // 'return' in a constructor ends it and yields 'this'.
                    if (expression is not null) Fail("AI3020", "A constructor cannot return a value.", token.Location);
                    EndStatement(); return (new("return", token.Location, new ScalarExpression("local", _struct!.Id, token.Location, _this!.EmittedName)), true);
                }
                if (expression is not null && expression.Type != _returnType) expression = TryCoerce(expression, _returnType) ?? expression;
                if (expression is null ? _returnType != ScalarType.Unit : expression.Type != _returnType || _returnType == ScalarType.Unit)
                    Fail("AI3020", "Return value does not match the declared type.", token.Location);
                EndStatement(); return (new("return", token.Location, expression), true);
            }
            if (Take("if"))
            {
                var condition = Condition(); var before = new HashSet<string>(_assigned);
                var (whenTrue, whenFalse) = Flow(condition);
                _assigned.UnionWith(whenTrue);
                var yes = Block(); var yesAssigned = new HashSet<string>(_assigned);
                _assigned = new(before); _assigned.UnionWith(whenFalse); NewLines();
                (IReadOnlyList<ScalarStatement> Body, bool Returns) no = ([], false);
                if (Take("else"))
                {
                    NewLines();
                    if (Current.Text == "if") { var nested = Statement(); no = ([nested.Statement], nested.Returns); }
                    else no = Block();
                }
                var constant = BooleanConstant(condition);
                if (constant == true) _assigned = yesAssigned;
                else if (constant == false) { /* only the else path is reachable */ }
                else if (yes.Returns && !no.Returns) { /* only the else path reaches the next statement */ }
                else if (no.Returns && !yes.Returns) _assigned = yesAssigned;
                else _assigned.IntersectWith(yesAssigned);
                var terminates = constant switch { true => yes.Returns, false => no.Returns, null => yes.Returns && no.Returns };
                return (new("if", token.Location, condition, Body: yes.Body, Alternative: no.Body), terminates);
            }
            if (Take("while"))
            {
                var condition = Condition(); var before = new HashSet<string>(_assigned);
                _assigned.UnionWith(Flow(condition).T);
                var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop(); _assigned = before;
                return (new("while", token.Location, condition, Body: body.Body), BooleanConstant(condition) == true && !flags.Breaks);
            }
            if (Take("do"))
            {
                var before = new HashSet<string>(_assigned);
                var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop();
                var afterBody = new HashSet<string>(_assigned);
                NewLines();
                if (Current.Text != "while") Fail("AI3106", "do requires a trailing while (condition).");
                Next();
                // continue skips the rest of the body, so the condition may only rely on what held before the body.
                _assigned = flags.Continues ? new(before) : new(afterBody);
                var condition = Condition();
                EndStatement();
                // A break leaves before the condition; the body can also be left early by continue.
                _assigned = flags.Breaks || flags.Continues ? before : afterBody;
                var constant = BooleanConstant(condition);
                var reachesCondition = !body.Returns || flags.Continues;
                return (new("dowhile", token.Location, condition, Body: body.Body),
                    !flags.Breaks && (constant == true || !reachesCondition));
            }
            if (Take("for"))
            {
                Expect("("); _scopes.Add(new(StringComparer.Ordinal));
                if (Current.Text is not "var" and not "let") Fail("AI3106", "For requires a local initializer.");
                var initializer = Declaration();
                if (initializer.Expression is null) Fail("AI3106", "For initializer requires a value.");
                Expect(";"); var condition = Expression(); Require(condition, ScalarType.Bool, "AI3101"); Expect(";");
                var before = new HashSet<string>(_assigned);
                _assigned.UnionWith(Flow(condition).T);
                var increment = Expression();
                if (increment.Operation is not "accessor-mutation" and not "reference-store" and not "reference-update" and not "ref-reassign" and not "array-store" and not "static-event-update" and not "=" and not "+=" and not "-=" and not "pre++" and not "pre--" and not "post++" and not "post--")
                    Fail("AI3106", "For increment requires an update expression.", increment.Location);
                increment = Lower(increment);
                Expect(")"); _assigned = new(before); _assigned.UnionWith(Flow(condition).T);
                var flags = new LoopFlags(); _loops.Push(flags); var body = Block(); _loops.Pop(); _assigned = before;
                foreach (var local in _scopes[^1].Values) _assigned.Remove(local.EmittedName);
                _scopes.RemoveAt(_scopes.Count - 1);
                return (new("for", token.Location, condition, Body: body.Body, Initializer: initializer, Increment: increment),
                    BooleanConstant(condition) == true && !flags.Breaks);
            }
            if (token.Text is "break" or "continue")
            {
                Next();
                if (token.Text == "break")
                {
                    if (_loops.Count == 0) Fail("AI3104", "break requires a loop or switch.", token.Location);
                    if (_finallyLoopDepth.Count > 0 && _loops.Count <= _finallyLoopDepth.Peek()) Fail("AI3106", "Control cannot leave the body of a finally clause.", token.Location);
                    _loops.Peek().Breaks = true;
                    if (_loops.Peek().IsSwitch) _loops.Peek().BreakStates.Add(new(_assigned));
                }
                else
                {
                    // continue skips enclosing switch statements and targets the innermost loop.
                    var loop = _loops.FirstOrDefault(l => !l.IsSwitch);
                    if (loop is null) Fail("AI3104", "continue requires a loop.", token.Location);
                    if (_finallyLoopDepth.Count > 0 && _loops.Count - _loops.ToList().IndexOf(loop!) <= _finallyLoopDepth.Peek()) Fail("AI3106", "Control cannot leave the body of a finally clause.", token.Location);
                    loop!.Continues = true;
                }
                EndStatement(); return (new(token.Text, token.Location), true);
            }
            if (Current.Text == "{") { var block = Block(); return (new("block", token.Location, Body: block.Body), block.Returns); }
            var value = Expression(valueRequired:false);
            if (value.Operation is not "accessor-mutation" and not "reference-store" and not "reference-update" and not "ref-reassign" and not "array-store" and not "static-event-update" and not "=" and not "+=" and not "-=" and not "pre++" and not "pre--" and not "post++" and not "post--" and not "await"
                && !(value.Operation is "call" or "omitted-partial-call" && Has(ScalarFeatures.Functions))
                && !(value.Operation == "runtime-type" && Has(ScalarFeatures.TypeIdentity))
                && !(token.Text == "new" && value.Operation is "new" or "init" or "init-record" or "init-members"))
                Fail("AI3106", "Only assignment/update expressions may be statements.", value.Location);
            value = Lower(value);
            EndStatement(); return value.Operation=="omitted-partial-call"?(new("block",token.Location,Body:[]),false):(new("expression", token.Location, value), false);
        }
        private ScalarStatement Declaration()
        {
            var keyword = Next(); var name = Next(); ScalarType? annotation = null;
            if (Take(":"))
            {
                var type = ParameterTypeToken(); annotation = Type(type.Text, type.Location); var mode = ParameterPassing(type.Text).Mode;
                if (mode != ScalarParameterMode.Value) return ReferenceDeclaration(keyword, name, annotation, mode, ScopedReference(type.Text));
            }
            var initializer = Take("=") ? Expression() : null;
            if (initializer is null && (annotation is null || keyword.Text == "let"))
                Fail("AI3106", "Inferred var and let require an initializer.", keyword.Location);
            var resolved = annotation ?? initializer!.Type;
            if (IsNull(resolved)) Fail("AI3107", "Cannot assign null to an implicitly typed local.", initializer!.Location);
            if (IsFunctionValue(resolved)) Fail("AI3100", $"A {Show(resolved)} has no type of its own here; declare the variable with a delegate type.", initializer!.Location);
            if (resolved == ScalarType.Unit) Fail("AI3110", "A unit value cannot be stored in a local.", initializer?.Location ?? keyword.Location);
            if (initializer is not null) initializer = Coerce(initializer, resolved);
            var local = Declare(name.Text, resolved, keyword.Text == "var", name.Location);
            if (initializer is not null) _assigned.Add(local.EmittedName);
            return new ScalarStatement("local", keyword.Location, initializer, local.EmittedName, resolved) { Mutable = local.Mutable };
        }
        private ScalarStatement ConstDeclaration()
        {
            var keyword = Next(); var name = Next(); ScalarType? annotation = null;
            if (Take(":")) { var type = TypeToken(); annotation = Type(type.Text, type.Location); }
            if (!Take("=")) Fail("AI3106", "const requires an initializer.", keyword.Location);
            ScalarExpression initializer;
            var savedConst = _bindingConst; _bindingConst = true;
            try { initializer = Expression(); } finally { _bindingConst = savedConst; }
            var value = ConstantOf(initializer);
            if (value is null) Fail("AI3113", "A const initializer must be a compile-time constant.", initializer.Location);
            if (value is string text && text.Length > MaxConstantStringUnits) Fail("AI3113", "A string constant exceeds the supported UTF-16 length limit.", initializer.Location);
            var resolved = annotation ?? initializer.Type;
            if (IsNull(resolved)) Fail("AI3107", "Cannot infer a type for a null constant.", initializer.Location);
            initializer = Coerce(initializer, resolved);
            value = ConstantOf(initializer)!;
            var local = Declare(name.Text, resolved, false, name.Location);
            _constLocals[local.EmittedName] = value!; _constInits[local.EmittedName] = initializer;
            _assigned.Add(local.EmittedName);
            return new ScalarStatement("local", keyword.Location, initializer, local.EmittedName, resolved) { Mutable = false, Const = true };
        }

        // ---- constant folding (checked unless an unchecked context applies) ----
        private object? ConstantOf(ScalarExpression expression) => _folded.TryGetValue(expression, out var value) ? value
            : Has(ScalarFeatures.Names) && expression.Operation == "null" ? NullStringPattern : null;
        private bool? BooleanConstant(ScalarExpression expression) => ConstantOf(expression) as bool?;
        // Constants of f32 are held as the exactly equal double; every f32 result is rounded to binary32 after each operation.
        private bool FoldFloat(ScalarExpression node)
        {
            var involved = IsFloat(node.Type) || node.Left is not null && IsFloat(node.Left.Type) || node.Right is not null && IsFloat(node.Right.Type);
            if (!involved) return false;
            double? value = null; bool? truth = null;
            switch (node.Operation)
            {
                case "literal" when IsFloat(node.Type):
                    value = node.Type == ScalarType.F32 ? float.Parse(node.Value!, NumberStyles.Float, CultureInfo.InvariantCulture) : double.Parse(node.Value!, NumberStyles.Float, CultureInfo.InvariantCulture);
                    break;
                case "local" when _constLocals.TryGetValue(node.Value!, out var constant) && constant is double known: value = known; break;
                case "convert" when node.Left is not null && ConstantOf(node.Left) is { } widened:
                    value = widened is BigInteger integer ? ScalarConstants.IntegerToFloat(integer, node.Type) : (double)widened; break;
                case "cast" when node.Left is not null && ConstantOf(node.Left) is { } source:
                    if (IsFloat(node.Type))
                        value = source is BigInteger fromInteger ? ScalarConstants.IntegerToFloat(fromInteger, node.Type) : ScalarConstants.RoundTo(node.Type, (double)source);
                    else
                    {
                        if (ScalarConstants.FloatConstantToInteger(node.Type, (double)source, node.Wraps, out var converted) != ScalarFault.None)
                            Fail("AI3111", "Constant conversion overflows its type; use an unchecked context.", node.Location);
                        _folded[node] = converted; return true;
                    }
                    break;
                case "unary+" or "unary-" when node.Left is not null && ConstantOf(node.Left) is double operand:
                    value = node.Operation == "unary-" ? -operand : operand; break;
                case "+" or "-" or "*" or "/" or "%" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is double fl && ConstantOf(node.Right) is double fr:
                    value = ScalarConstants.FloatArithmetic(node.Operation, node.Type, fl, fr); break;
                case "<" or "<=" or ">" or ">=" or "==" or "!=" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is double cl && ConstantOf(node.Right) is double cr:
                    truth = ScalarConstants.FloatCompare(node.Operation, cl, cr); break;
            }
            if (value is not null) _folded[node] = value.Value;
            else if (truth is not null) _folded[node] = truth.Value;
            return true;
        }
        // Decimal constants are System.Decimal values; folding uses System.Decimal itself, so compile-time results equal run-time results.
        private bool FoldDecimal(ScalarExpression node)
        {
            var involved = IsDecimal(node.Type) || node.Left is not null && IsDecimal(node.Left.Type) || node.Right is not null && IsDecimal(node.Right.Type);
            if (!involved) return false;
            object? value = null;
            try
            {
                switch (node.Operation)
                {
                    case "literal" when IsDecimal(node.Type):
                        value = decimal.Parse(node.Value!, NumberStyles.Float, CultureInfo.InvariantCulture); break;
                    case "local" when _constLocals.TryGetValue(node.Value!, out var constant) && constant is decimal known: value = known; break;
                    case "convert" when node.Left is not null && ConstantOf(node.Left) is BigInteger widened && IsDecimal(node.Type):
                        value = (decimal)widened; break;
                    case "cast" when node.Left is not null && ConstantOf(node.Left) is { } source:
                        if (IsDecimal(node.Type))
                            value = source is BigInteger fromInteger ? (decimal)fromInteger : node.Left.Type == ScalarType.F32 ? (decimal)(float)(double)source : (decimal)(double)source;
                        else if (ScalarConstants.IsFloat(node.Type))
                            value = node.Type == ScalarType.F32 ? (double)(float)(decimal)source : (double)(decimal)source;
                        else
                        {
                            var truncated = new BigInteger(decimal.Truncate((decimal)source));
                            if (!ScalarConstants.Fits(truncated, node.Type)) Fail("AI3111", "Constant conversion overflows its type; decimal conversions always check.", node.Location);
                            value = truncated;
                        }
                        break;
                    case "unary+" or "unary-" when node.Left is not null && ConstantOf(node.Left) is decimal operand:
                        value = node.Operation == "unary-" ? -operand : operand; break;
                    case "+" or "-" or "*" or "/" or "%" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is decimal dl && ConstantOf(node.Right) is decimal dr:
                        value = node.Operation switch { "+" => dl + dr, "-" => dl - dr, "*" => dl * dr, "/" => dl / dr, _ => dl % dr }; break;
                    case "<" or "<=" or ">" or ">=" or "==" or "!=" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is decimal cl && ConstantOf(node.Right) is decimal cr:
                    {
                        var c = decimal.Compare(cl, cr);
                        value = node.Operation switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, ">=" => c >= 0, "==" => c == 0, _ => c != 0 }; break;
                    }
                }
            }
            catch (OverflowException) { Fail("AI3111", "Constant expression overflows decimal; decimal arithmetic always checks.", node.Location); }
            catch (DivideByZeroException) { Fail("AI3112", "Constant division or remainder by zero.", node.Location); }
            if (value is not null) _folded[node] = value;
            return true;
        }
        private ScalarExpression Fold(ScalarExpression node)
        {
            if (NativeInvolved(node)) return FoldNative(node);
            if (FoldDecimal(node)) return node;
            if (FoldFloat(node))
            {
                // Preserve the compile-time conversion decision in backend-neutral IR.
                // Re-emitting a floating cast here would apply the different runtime rule.
                if (node.Operation == "cast" && node.Left is not null && IsFloat(node.Left.Type)
                    && ScalarConstants.IsInteger(node.Type) && ConstantOf(node) is BigInteger integer)
                    return Fold(new("literal", node.Type, node.Location, integer.ToString(CultureInfo.InvariantCulture)));
                return node;
            }
            object? value = null;
            switch (node.Operation)
            {
                case "literal":
                    value = node.Type == ScalarType.String ? node.Value : node.Type == ScalarType.Bool ? node.Value == "true" : BigInteger.Parse(node.Value!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    break;
                case "local":
                    if (_constLocals.TryGetValue(node.Value!, out var constant)) value = constant;
                    break;
                case "&" or "|" or "^" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is { } bl && ConstantOf(node.Right) is { } br:
                    if (bl is bool ba && br is bool bb) value = node.Operation == "&" ? ba & bb : node.Operation == "|" ? ba | bb : ba ^ bb;
                    else value = ScalarConstants.Bitwise(node.Operation, (BigInteger)bl, (BigInteger)br);
                    break;
                case "<<" or ">>" or ">>>" when node.Left is not null && node.Right is not null && ConstantOf(node.Left) is BigInteger sl && ConstantOf(node.Right) is BigInteger sr:
                    value = ScalarConstants.Shift(node.Operation, node.Type, sl, sr); break;
                case "unary~" when node.Left is not null && ConstantOf(node.Left) is BigInteger notOperand:
                    value = ScalarConstants.BitNot(node.Type, notOperand); break;
                case "convert" when node.Left is not null && ConstantOf(node.Left) is BigInteger widened:
                    value = widened; break;
                case "retype" when node.Left is not null && ConstantOf(node.Left) is BigInteger same:
                    value = same; break;
                case "cast" when node.Left is not null && ConstantOf(node.Left) is BigInteger source:
                {
                    var fault = ScalarConstants.Convert(node.Type, source, node.Wraps, out var converted);
                    if (fault != ScalarFault.None) Fail("AI3111", "Constant conversion overflows its type; use an unchecked context.", node.Location);
                    value = converted; break;
                }
                case "unary+" or "unary-" or "unary!" when node.Left is not null && ConstantOf(node.Left) is { } operand:
                    if (node.Operation == "unary!") value = !(bool)operand;
                    else if (node.Operation == "unary+") value = operand;
                    else
                    {
                        var fault = ScalarConstants.Negate(node.Type, (BigInteger)operand, node.Wraps, out var negated);
                        if (fault != ScalarFault.None) Fail("AI3111", "Constant expression overflows its type; use an unchecked context.", node.Location);
                        value = negated;
                    }
                    break;
                case "+" or "-" or "*" or "/" or "%" when node.Left is not null && node.Right is not null
                    && ConstantOf(node.Left) is { } l && ConstantOf(node.Right) is { } r:
                {
                    var fault = ScalarConstants.Arithmetic(node.Operation, node.Type, (BigInteger)l, (BigInteger)r, node.Wraps, out var result);
                    if (fault == ScalarFault.DivideByZero) Fail("AI3112", "Constant division or remainder by zero.", node.Location);
                    if (fault != ScalarFault.None) Fail("AI3111", "Constant expression overflows its type; use an unchecked context.", node.Location);
                    value = result; break;
                }
                case "<" or "<=" or ">" or ">=" when node.Left is not null && node.Right is not null
                    && ConstantOf(node.Left) is { } cl && ConstantOf(node.Right) is { } cr:
                {
                    var c = ((BigInteger)cl).CompareTo((BigInteger)cr);
                    value = node.Operation switch { "<" => c < 0, "<=" => c <= 0, ">" => c > 0, _ => c >= 0 }; break;
                }
                case "==" or "!=" when node.Left is not null && node.Right is not null
                    && ConstantOf(node.Left) is { } el && ConstantOf(node.Right) is { } er:
                    value = el.Equals(er) == (node.Operation == "=="); break;
                case "&&" or "||" when node.Left is not null && node.Right is not null
                    && ConstantOf(node.Left) is bool a && ConstantOf(node.Right) is bool b:
                    value = node.Operation == "&&" ? a && b : a || b; break;
            }
            if (value is not null) _folded[node] = value;
            return node;
        }

        private ScalarExpression ConstantConditional(ScalarExpression condition)
        {
            var at=Next().Location;Require(condition,ScalarType.Bool);
            var yes=Expression();Expect(":");var no=Expression();Read(yes);Read(no);
            if(yes.Type!=no.Type)Fail("AI3100","A constant conditional currently requires identical branch types.",at);
            if(ConstantOf(condition) is not bool||ConstantOf(yes) is null||ConstantOf(no) is null)
                Fail("AI3113","A constant conditional requires constant condition and branches.",at);
            return (bool)ConstantOf(condition)!?yes:no;
        }
        private ScalarExpression Condition()
        {
            Expect("("); var expression = Expression(); Expect(")"); Require(expression, ScalarType.Bool, "AI3101"); return expression;
        }
        private void Require(ScalarExpression expression, ScalarType type, string code = "AI3107")
        {
            if (expression.Type != type) Fail(code, $"Expected {Show(type)}, found {Show(expression.Type)}.", expression.Location);
        }
        private int Precedence(string op) => op switch
        {
            "=" or "+=" or "-=" => 1,
            "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=" or ">>>=" => Has(ScalarFeatures.Bits) ? 1 : 0,
            "||" => 2, "&&" => 3,
            "|" => Has(ScalarFeatures.Bits) ? 4 : 0, "^" => Has(ScalarFeatures.Bits) ? 5 : 0, "&" => Has(ScalarFeatures.Bits) ? 6 : 0,
            "==" or "!=" => 7, "<" or "<=" or ">" or ">=" => 8, "is" => Has(ScalarFeatures.Patterns) ? 8 : 0, "as" => Has(ScalarFeatures.Patterns) && Has(ScalarFeatures.Classes) ? 8 : 0,
            "<<" or ">>" or ">>>" => Has(ScalarFeatures.Bits) ? 9 : 0,
            "+" or "-" => 10, "*" or "/" or "%" => 11, _ => 0
        };
        // ---- typed conversions (implicit numeric conversions per C#, constant conversions, unification) ----
        private static bool Numeric(ScalarType type) => ScalarConstants.IsInteger(type);
        private static bool IsFloat(ScalarType type) => ScalarConstants.IsFloat(type);
        private static bool IsDecimal(ScalarType type) => type == ScalarType.Decimal;
        // Floats and decimal are numeric but not integral: bit operations and shifts are rejected for them.
        private static bool NonIntegral(ScalarType type) => ScalarConstants.IsFloat(type) || type == ScalarType.Decimal;
        private static bool IsNum(ScalarType type) => ScalarConstants.IsInteger(type) || ScalarConstants.IsFloat(type) || type == ScalarType.Decimal;
        private static bool ConstantConverts(ScalarType from, BigInteger value, ScalarType to) =>
            Numeric(from) && Numeric(to) && (from == ScalarType.Int || from == ScalarType.Long && to == ScalarType.ULong) && to != ScalarType.Char && ScalarConstants.Fits(value, ScalarConstants.NativeStorage(to, 32));
        private ScalarExpression? TryCoerce(ScalarExpression expression, ScalarType target)
        {
            if (IsFunctionValue(expression.Type))
                return !IsDelegate(target) ? null : expression.Operation == "lambda" ? ConvertLambda(expression, target) : ConvertMethodGroup(expression, target);
            if (expression.Type == target) return expression;
            if(Has(ScalarFeatures.Boxing)&&_checkOnly&&IsObject(target)&&IsTypeParameterType(expression.Type))return new("null",target,expression.Location);
            if (IsTypeParameterType(expression.Type) && StructOf(expression.Type).ParameterBounds is not null && ParameterValueConversion(expression.Type, target))
                return new("upcast", target, expression.Location, Left: expression);
            // null converts to every class type and to object, and to nothing else; a class reference converts to object (an identity-preserving upcast).
            // N26: null also converts to string.
            if (IsNull(expression.Type)) return IsReference(target) && !IsOpaqueReference(target) || target == ScalarType.String && Has(ScalarFeatures.NullStrings) ? expression with { Type = target } : null;
            if (IsObject(target)) return IsClass(expression.Type) ? new("toobject", target, expression.Location, Left: expression) : Has(ScalarFeatures.Boxing)&&expression.Type==ScalarType.String?StringToObject(expression):Has(ScalarFeatures.Boxing)&&Boxable(expression.Type) ? BoxValue(expression,target) : null;
            if(Has(ScalarFeatures.Boxing)&&Boxable(expression.Type)&&ValueInterfaceConversion(expression.Type,target))return BoxValue(expression,target);
            if(Has(ScalarFeatures.Boxing)&&expression.Type==ScalarType.String&&BuiltinEquatableConversion(ScalarType.String,target))return StringToEquatable(expression,target);
            // A derived class converts implicitly to its base classes and to the interfaces it implements (the same reference).
            if (IsClass(expression.Type) && IsClass(target)) return Derives(_structs, IndexOf(expression.Type), IndexOf(target)) ? new("upcast", target, expression.Location, Left: expression) : null;
            // C#: the only implicit conversion involving an enum is a constant zero of an integer type to any enum type.
            if (IsEnum(target) || IsEnum(expression.Type))
                return IsEnum(target) && Numeric(expression.Type) && expression.Type != ScalarType.Char && ConstantOf(expression) is BigInteger zero && zero.IsZero
                    ? Fold(new("literal", target, expression.Location, "0")) : null;
            if (!IsNum(expression.Type) || !IsNum(target)) return null;
            if (ConstantOf(expression) is BigInteger value && ConstantConverts(expression.Type, value, target))
                return Fold(new("literal", target, expression.Location, value.ToString(CultureInfo.InvariantCulture)));
            if (ScalarConstants.ImplicitNumeric(expression.Type, target))
                return Fold(new("convert", target, expression.Location, Left: expression));
            return null;
        }
        private ScalarExpression Coerce(ScalarExpression expression, ScalarType target, string code = "AI3107")
        {
            var converted = TryCoerce(expression, target);
            if (converted is null && IsObject(target) && !IsNull(expression.Type) && !IsReference(expression.Type))
                Fail("AI3100", $"Boxing {Show(expression.Type)} to object is not supported.", expression.Location);
            if (converted is null) Fail(code, $"Expected {Show(target)}, found {Show(expression.Type)}.", expression.Location);
            return converted!;
        }
        // Binary operator resolution over the predefined int, uint, long, ulong operators (smaller types promote to int).
        private ScalarType? Unify(ScalarExpression left, ScalarExpression right)
        {
            bool Can(ScalarExpression e, ScalarType candidate) => e.Type == candidate || TryCoerceProbe(e, candidate);
            // C#: decimal combines only with itself and the integer types; a float or double operand has no common type with it.
            if (IsDecimal(left.Type) || IsDecimal(right.Type)) return Can(left, ScalarType.Decimal) && Can(right, ScalarType.Decimal) ? ScalarType.Decimal : null;
            // C# binary numeric promotion: a float operand selects f64 if either is f64, otherwise f32; integers alone use the integer table.
            if (IsFloat(left.Type) || IsFloat(right.Type))
                foreach (var candidate in new[] { ScalarType.F32, ScalarType.F64 })
                    if (Can(left, candidate) && Can(right, candidate)) return candidate;
            if (IsFloat(left.Type) || IsFloat(right.Type)) return null;
            foreach (var candidate in new[] { ScalarType.Int, ScalarType.UInt, ScalarType.NInt, ScalarType.NUInt, ScalarType.Long, ScalarType.ULong })
                if (Can(left, candidate) && Can(right, candidate)) return candidate;
            return null;
        }
        private bool TryCoerceProbe(ScalarExpression e, ScalarType to) =>
            IsNum(e.Type) && IsNum(to) && (ScalarConstants.ImplicitNumeric(e.Type, to) || ConstantOf(e) is BigInteger v && ConstantConverts(e.Type, v, to));
        private ScalarExpression Arithmetic(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            if (!IsNum(left.Type)) Fail("AI3107", $"Expected a numeric operand, found {Show(left.Type)}.", left.Location);
            if (!IsNum(right.Type)) Fail("AI3107", $"Expected a numeric operand, found {Show(right.Type)}.", right.Location);
            var type = Unify(left, right);
            if (type is null) Fail("AI3107", $"No common numeric type for {Show(left.Type)} and {Show(right.Type)}.", at);
            return Fold(new(symbol, symbol is "<" or "<=" or ">" or ">=" or "==" or "!=" ? ScalarType.Bool : type!.Value, at,
                Left: Coerce(left, type!.Value), Right: Coerce(right, type.Value)) { Wraps = _wraps });
        }
        // string supports + (concatenation of two strings) and ordinal == / != between two strings; nothing converts implicitly.
        private ScalarExpression StringBinary(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at, bool wraps)
        {
            if (left.Type != ScalarType.String) Fail("AI3107", $"Operator '{symbol}' is not defined for {Show(left.Type)} and string.", left.Location);
            if (right.Type != ScalarType.String) Fail("AI3107", $"Operator '{symbol}' is not defined for string and {Show(right.Type)}.", right.Location);
            if (symbol is not ("+" or "==" or "!=")) Fail("AI3107", $"Operator '{symbol}' is not defined for string.", at);
            var result = new ScalarExpression(symbol, symbol == "+" ? ScalarType.String : ScalarType.Bool, at, Left: left, Right: right) { Wraps = wraps };
            if (Has(ScalarFeatures.Names) && (symbol == "+" || _bindingConst) && ConstantOf(left) is { } a && ConstantOf(right) is { } b)
            {
                var x = ReferenceEquals(a, NullStringPattern) ? null : (string)a;
                var y = ReferenceEquals(b, NullStringPattern) ? null : (string)b;
                if (symbol == "+" && (long)(x?.Length ?? 0) + (y?.Length ?? 0) > MaxConstantStringUnits)
                    Fail("AI3113", "A string constant exceeds the supported UTF-16 length limit.", at);
                _folded[result] = symbol == "+" ? x + y : symbol == "==" ? x == y : x != y;
            }
            return result;
        }
        private ScalarExpression MakeBinary(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            if(symbol is "==" or "!="&&Has(ScalarFeatures.RecordInheritance)&&DeclaredReferenceEquality(symbol,left,right,at) is { } declaredReference)return declaredReference;
            if (Delegating && !InDelegateHelper && (IsDelegate(left.Type) || IsDelegate(right.Type)) && !IsObject(left.Type) && !IsObject(right.Type)) return DelegateBinary(symbol, left, right, at);
            if(symbol is "==" or "!="&&Has(ScalarFeatures.RecordInheritance)&&TryRecordClassBinary(symbol,left,right,at) is { } recordBinary)return recordBinary;
            if (symbol is "==" or "!=" && IsStruct(left.Type) && left.Type == right.Type && StructOf(left.Type).IsRecord) return RecordEquality(symbol, left, right, at);
            // N26: a string compared with (or concatenated with) null uses the string operators, null as a null string.
            if (Has(ScalarFeatures.NullStrings) && (left.Type == ScalarType.String && IsNull(right.Type) || IsNull(left.Type) && right.Type == ScalarType.String))
                return StringBinary(symbol, left with { Type = ScalarType.String }, right with { Type = ScalarType.String }, at, _wraps);
            if (IsReference(left.Type) || IsReference(right.Type) || IsNull(left.Type) || IsNull(right.Type)) return ReferenceBinary(symbol, left, right, at);
            if (IsEnum(left.Type) || IsEnum(right.Type)) return EnumBinary(symbol, left, right, at);
            if (left.Type == ScalarType.String || right.Type == ScalarType.String) return StringBinary(symbol, left, right, at, _wraps);
            if (symbol is "<<" or ">>" or ">>>") return Shift(symbol, left, right, at);
            if (symbol is "&" or "|" or "^" && left.Type == ScalarType.Bool && right.Type == ScalarType.Bool)
                return Fold(new(symbol, ScalarType.Bool, at, Left: left, Right: right) { Wraps = _wraps });
            if (symbol is "&" or "|" or "^" && (NonIntegral(left.Type) || NonIntegral(right.Type)))
                Fail("AI3107", "Bitwise operators require integer operands.", NonIntegral(left.Type) ? left.Location : right.Location);
            return Arithmetic(symbol, left, right, at);
        }
        // N16 record equality (C# synthesized Equals): a record class compares equal when the references are equal, or both are non-null and every
        // field compares equal; a record struct compares every field. Fields compare as EqualityComparer<T>.Default: == for integers, bool, char,
        // enums and decimal, == or both NaN for floats, record equality for records, identity for other classes, field by field for plain structs.
        private readonly HashSet<ScalarType> _equalityStack = [];
        private ScalarExpression RecordEquality(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at, bool comparer = false)
        {
            var info = StructOf(left.Type);
            if(!info.IsClass&&Has(ScalarFeatures.RecordInheritance)&&CustomRecordEquality(info)is { } customEquality)
            {
                var receiver=customEquality.ReceiverMode==ScalarParameterMode.Value?left:new ScalarExpression("argument-receiver-copy",left.Type,at,"temporary",Left:left);
                ScalarExpression comparison=new("call",ScalarType.Bool,at,customEquality.QualifiedName){Arguments=[receiver,right]};
                return symbol=="=="?comparison:new("unary!",ScalarType.Bool,at,Left:comparison);
            }
            if(info.IsClass&&Has(ScalarFeatures.RecordInheritance))
            {
                ValidateRecordEqualityShape(activeStructs!,info.Id,[],at);
                ScalarExpression result=new("record-equal",ScalarType.Bool,at,Left:left,Right:right);
                if(!comparer&&info.Methods.Any(m=>m.IsStaticConstructor&&!m.IsImplicitStaticConstructor))result=new("type-init-value",ScalarType.Bool,at,IndexOf(info.Id).ToString(CultureInfo.InvariantCulture),Left:result);
                return symbol=="=="?result:new("unary!",ScalarType.Bool,at,Left:result);
            }
            if (!_equalityStack.Add(info.Id)) { Fail("AI3100", $"Equality of the recursive record '{info.Name}' is not supported yet.", at); return null!; }
            try
            {
                var h1 = HiddenLocal(left.Type); var h2 = HiddenLocal(left.Type);
                ScalarExpression a = new("local", left.Type, at, h1.EmittedName), b = new("local", left.Type, at, h2.EmittedName);
                var body = FieldsEqual(info, a, b, at);
                if (info.IsClass)
                {
                    ScalarExpression NotNull(ScalarExpression e) => new("!=", ScalarType.Bool, at, Left: e, Right: new("null", e.Type, at)) { Wraps = _wraps };
                    body = Fold(new("||", ScalarType.Bool, at, Left: new("==", ScalarType.Bool, at, Left: a, Right: b) { Wraps = _wraps },
                        Right: And(And(NotNull(a), NotNull(b), at), body, at)) { Wraps = _wraps });
                }
                if ((!comparer || !info.IsClass) && info.Methods.Any(m => m.IsStaticConstructor && !m.IsImplicitStaticConstructor))
                    body = new("type-init-value", ScalarType.Bool, at, IndexOf(info.Id).ToString(CultureInfo.InvariantCulture), Left: body);
                var yes = Fold(new("literal", ScalarType.Bool, at, "true"));
                ScalarExpression inner = new("switch", ScalarType.Bool, at, h2.EmittedName, Left: right) { Arguments = [yes, body], Wraps = _wraps };
                ScalarExpression outer = new("switch", ScalarType.Bool, at, h1.EmittedName, Left: left) { Arguments = [Fold(new("literal", ScalarType.Bool, at, "true")), inner], Wraps = _wraps };
                return symbol == "==" ? outer : new("unary!", ScalarType.Bool, at, Left: outer);
            }
            finally { _equalityStack.Remove(info.Id); }
        }
        private ScalarExpression FieldsEqual(ScalarStruct info, ScalarExpression a, ScalarExpression b, SourceLocation at,bool ownOnly=false,bool valueTypeFields=false)
        {
            ScalarExpression result = Fold(new("literal", ScalarType.Bool, at, "true"));
            for (var i = 0; i < info.Fields.Count; i++)
            {
                var field = info.Fields[i]; var index = i.ToString(CultureInfo.InvariantCulture);
                if(ownOnly&&field.Owner!=IndexOf(info.Id))continue;
                ScalarExpression fa = new("field", field.Type, at, index, Left: a), fb = new("field", field.Type, at, index, Left: b);
                ScalarExpression equal;
                if (Has(ScalarFeatures.Boxing))equal=valueTypeFields
                    ? (IsObject(field.Type)||IsClass(field.Type)) ? ReferenceValueEqual(fa,fb,at,invokeOnNullRight:true)
                        : IsStruct(field.Type) ? ObjectEquals(fa,fb,at) : ObjectValueEqual(fa,fb,at)
                    : ObjectValueEqual(fa,fb,at);
                else if (IsFloat(field.Type))
                    equal = Fold(new("||", ScalarType.Bool, at, Left: MakeBinary("==", fa, fb, at),
                        Right: And(MakeBinary("!=", fa, fa, at), MakeBinary("!=", fb, fb, at), at)) { Wraps = _wraps });
                else if (IsStruct(field.Type) && StructOf(field.Type).IsRecord) equal = RecordEquality("==", fa, fb, at, comparer: true);
                else if (IsObject(field.Type) || IsClass(field.Type) && StructOf(field.Type).IsInterface)
                { Fail("AI3100", $"Record equality over the {Show(field.Type)} member '{info.Name}.{field.Name}' is not supported yet.", at); return null!; }
                else if (IsStruct(field.Type) && !IsClass(field.Type) && !StructOf(field.Type).IsRecord)
                {
                    var plain = StructOf(field.Type);
                    if (!PlainComparable(plain)) { Fail("AI3100", $"Record equality over the struct member '{info.Name}.{field.Name}' is supported only for structs of integer, bool, char, enum and class members.", at); return null!; }
                    equal = FieldsEqual(plain, fa, fb, at,valueTypeFields:valueTypeFields);
                }
                else equal = MakeBinary("==", fa, fb, at);
                result = And(result, equal, at);
            }
            return result;
        }
        private bool PlainComparable(ScalarStruct plain) => plain.Fields.All(f =>
            ScalarConstants.IsInteger(f.Type) || f.Type is ScalarType.Bool or ScalarType.Char || IsEnum(f.Type)
            || IsClass(f.Type) && !StructOf(f.Type).IsInterface
            || IsStruct(f.Type) && !IsClass(f.Type) && (StructOf(f.Type).IsRecord || PlainComparable(StructOf(f.Type))));
        // e with { m = v, ... }: a record struct is copied and the members are set; a record class is cloned (every field copied, which reads
        // through e and throws NullReferenceException for null) and the members are set in order.
        private ScalarExpression WithExpression(ScalarExpression subject)
        {
            var keyword = Next(); Read(subject);
            var info = StructOf(subject.Type);
            if (!info.IsRecord&&(!Has(ScalarFeatures.ReadonlyStructs)||info.IsClass||info.IsTypeParameter)) Fail("AI3100", $"A 'with' expression needs a record or a supported value struct.", keyword.Location);
            if(Has(ScalarFeatures.RecordMembers))
            {
                NewLines();
                if(Current.Text!="{")Fail("AI3106","Expected '{' in a with initializer.",Current.Location);
                return RequiredObjectInitializer(info,info.IsRecord?new("record-copy",info.Id,keyword.Location,Left:subject):subject,keyword.Location,enforceRequired:false);
            }
            NewLines(); Expect("{"); NewLines();
            var indices = new List<int>(); var values = new List<ScalarExpression>();
            while (Current.Text != "}")
            {
                var fieldToken = Next();
                var index = info.Fields.ToList().FindIndex(f => f.Name == fieldToken.Text && !f.Hidden);
                if (index < 0 && FindProperty(IndexOf(info.Id), fieldToken.Text) is not null)
                    Fail("AI3100", $"A property with accessor bodies ('{info.Name}.{fieldToken.Text}') cannot be set in a 'with' expression yet.", fieldToken.Location);
                if (index < 0) Fail("AI3103", $"'{info.Name}' has no field '{fieldToken.Text}'.", fieldToken.Location);
                var field = info.Fields[index];
                CheckAccess(info, field.IsPublic, field.Name, fieldToken.Location);
                if (field.IsReadonly && field.IsProperty&&!field.IsInit) Fail("AI3105", $"Property '{info.Name}.{field.Name}' cannot be assigned to -- it is read only.", fieldToken.Location);
                if (field.IsReadonly&&!field.IsInit) Fail("AI3105", $"Readonly field '{info.Name}.{field.Name}' cannot be set in a 'with' expression.", fieldToken.Location);
                if (field.WritePrivate && _struct?.Id != info.Id)
                    Fail("AI3119", $"The property '{info.Name}.{field.Name}' cannot be used in this context because the set accessor is inaccessible.", fieldToken.Location);
                if (indices.Contains(index)) Fail("AI3106", $"Field '{field.Name}' is initialized twice.", fieldToken.Location);
                Expect("=");
                var item = Expression(); Read(item);
                values.Add(Coerce(item, field.Type)); indices.Add(index);
                NewLines();
                if (!Take(",")) break;
                NewLines();
            }
            NewLines(); Expect("}");
            if (!info.IsClass)
                return indices.Count == 0 ? subject : new("init", info.Id, keyword.Location, string.Join(",", indices), Left: subject) { Arguments = values };
            if (info.Fields.Count == 0) Fail("AI3100", "A 'with' expression on a record class without members is not supported yet.", keyword.Location);
            var hidden = HiddenLocal(subject.Type);
            ScalarExpression read = new("local", subject.Type, keyword.Location, hidden.EmittedName);
            ScalarExpression Copy(int i) => new("field", info.Fields[i].Type, keyword.Location, i.ToString(CultureInfo.InvariantCulture), Left: read);
            var copied = Enumerable.Range(0, info.Fields.Count).Where(i => !indices.Contains(i)).ToList();
            ScalarExpression clone = new("init", info.Id, keyword.Location, string.Join(",", copied.Concat(indices)), Left: new("new", info.Id, keyword.Location))
            { Arguments = copied.Select(Copy).Concat(values).ToList() };
            var arms = new List<ScalarExpression>();
            if (copied.Count == 0)
            {
                // Every member is set: the null check reads the first member before any value is evaluated.
                arms.Add(new("==", ScalarType.Bool, keyword.Location, Left: read, Right: new("null", subject.Type, keyword.Location)) { Wraps = _wraps });
                arms.Add(new("init", info.Id, keyword.Location, "0", Left: new("new", info.Id, keyword.Location)) { Arguments = [Copy(0)] });
            }
            arms.Add(Fold(new("literal", ScalarType.Bool, keyword.Location, "true"))); arms.Add(clone);
            return new("switch", subject.Type, keyword.Location, hidden.EmittedName, Left: subject) { Arguments = arms, Wraps = _wraps };
        }
        // Class references support only == and != (reference identity, C# predefined reference equality) against the same class or null.
        private ScalarExpression ReferenceBinary(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            if (symbol is not ("==" or "!=")) Fail("AI3107", $"Operator '{symbol}' is not defined for {Show(left.Type)} and {Show(right.Type)}.", at);
            if (IsNull(left.Type) && IsNull(right.Type)) Fail("AI3107", "Comparing null with null is not supported.", at);
            if ((IsOpaqueReference(left.Type) || IsOpaqueReference(right.Type)) && !IsNull(left.Type) && !IsNull(right.Type))
                Fail("AI3107", $"Operator '{symbol}' cannot be applied to operands of type {Show(left.Type)} and {Show(right.Type)}: a type parameter is not known to be a reference type (add a 'class' or base-class constraint).", at);
            var type = IsNull(left.Type) ? right.Type : IsNull(right.Type) ? left.Type : IsObject(left.Type) || IsObject(right.Type) ? (ScalarType)ObjectTypeId : left.Type;
            if (IsClass(left.Type) && IsClass(right.Type) && left.Type != right.Type)
            {
                // Related classes compare as the base type; a possible interface conversion compares as object (identity either way).
                int l0 = IndexOf(left.Type), r0 = IndexOf(right.Type);
                type = Derives(_structs, l0, r0) ? right.Type : Derives(_structs, r0, l0) ? left.Type : CastPossible(l0, r0) ? (ScalarType)ObjectTypeId : left.Type;
            }
            if (!IsReference(type) || TryCoerce(left, type) is not { } l || TryCoerce(right, type) is not { } r)
            { Fail("AI3107", $"Operator '{symbol}' is not defined for {Show(left.Type)} and {Show(right.Type)}.", at); return null!; }
            return new(symbol, ScalarType.Bool, at, Left: l, Right: r) { Wraps = _wraps };
        }
        // (C)o checks the run-time class (InvalidCastException; null stays null), (object)c is an upcast; identity casts are allowed. Boxing and unboxing are not supported.
        private ScalarExpression ReferenceCast(ScalarType target, ScalarExpression operand, SourceLocation at)
        {
            if (target == ScalarType.String && Has(ScalarFeatures.NullStrings) && (IsNull(operand.Type) || operand.Type == ScalarType.String))
                return operand with { Type = ScalarType.String };
            if (operand.Type == target) return operand;
            if(Has(ScalarFeatures.Boxing))
            {
                if(_checkOnly&&IsTypeParameterType(operand.Type)&&IsObject(target))return new("null",target,at);
                if(IsObject(target)&&operand.Type==ScalarType.String)return StringToObject(operand);
                if(operand.Type==ScalarType.String&&BuiltinEquatableConversion(ScalarType.String,target))return StringToEquatable(operand,target);
                if(target==ScalarType.String&&BuiltinEquatableConversion(ScalarType.String,operand.Type))return ObjectToString(AsObject(operand),at);
                if(target==ScalarType.String&&IsObject(operand.Type))return ObjectToString(operand,at);
                if(_checkOnly&&IsTypeParameterType(target)&&(IsObject(operand.Type)||IsClass(operand.Type)&&StructOf(operand.Type).IsInterface))return DefaultValue(target,at);
                if(Boxable(operand.Type)&&(IsObject(target)||ValueInterfaceConversion(operand.Type,target)))return BoxValue(operand,target);
                if(Boxable(target)&&(IsObject(operand.Type)||IsClass(operand.Type)&&StructOf(operand.Type).IsInterface))return UnboxValue(operand,target,at);
            }
            if (IsNull(operand.Type) && IsReference(target)) return operand with { Type = target };
            if (IsObject(target) && IsClass(operand.Type)) return new("toobject", target, at, Left: operand);
            if (IsClass(target) && IsObject(operand.Type)) return new("downcast", target, at, Left: operand);
            if (IsClass(target) && IsClass(operand.Type))
            {
                if (Derives(_structs, IndexOf(operand.Type), IndexOf(target))) return new("upcast", target, at, Left: operand);
                if (Derives(_structs, IndexOf(target), IndexOf(operand.Type)) || CastPossible(IndexOf(operand.Type), IndexOf(target)) || LenientCast(operand.Type, target)) return new("downcast", target, at, Left: operand);
            }
            if (IsObject(target) || IsObject(operand.Type))
                Fail("AI3100", $"Boxing and unboxing ({Show(operand.Type)} to {Show(target)}) are not supported.", at);
            Fail("AI3118", $"Cannot convert {Show(operand.Type)} to {Show(target)}.", at);
            return null!;
        }
        // The shift count is an int (or converts to one); the result has the promoted type of the left operand.
        private ScalarExpression Shift(string symbol, ScalarExpression left, ScalarExpression right, SourceLocation at)
        {
            if (!Numeric(left.Type)) Fail("AI3107", $"Expected an integer operand, found {Show(left.Type)}.", left.Location);
            var count = Coerce(right, ScalarType.Int);
            var promoted = ScalarConstants.PromoteUnary(left.Type, false)!.Value;
            return Fold(new(symbol, promoted, at, Left: Coerce(left, promoted), Right: count) { Wraps = _wraps });
        }
        // x op= y is x = (T)(x op y); the update statement forms are lowered to one typed assignment.
        private ScalarExpression CompoundValue(ScalarExpression target, string symbol, ScalarExpression operand, SourceLocation at)
        {
            if (Delegating && IsDelegate(target.Type))
            {
                if (symbol is not ("+" or "-")) Fail("AI3107", $"Operator '{symbol}=' is not defined for {Show(target.Type)}.", at);
                return DelegateBinary(symbol, target, operand, at);
            }
            var shift = symbol is "<<" or ">>" or ">>>";
            if (IsEnum(target.Type))
            {
                // x op= y is x = (E)(x op y); E - E yields the underlying type, which converts back explicitly.
                if (symbol is not ("+" or "-" or "&" or "|" or "^")) Fail("AI3107", $"Operator '{symbol}=' is not defined for {Show(target.Type)}.", at);
                var combined = EnumBinary(symbol, target, operand, at);
                return combined.Type == target.Type ? combined : Retype(combined, target.Type);
            }
            if (target.Type == ScalarType.String)
            {
                if (symbol != "+") Fail("AI3107", $"Operator '{symbol}=' is not defined for string.", at);
                if (IsNull(operand.Type) && Has(ScalarFeatures.NullStrings)) operand = operand with { Type = ScalarType.String };
                return StringBinary("+", target, operand, at, _wraps);
            }
            // C# allows 'c += 1': an int constant that fits is accepted for a char target in a compound assignment.
            if (target.Type == ScalarType.Char && !shift && operand.Type == ScalarType.Int && ConstantOf(operand) is BigInteger small && ScalarConstants.Fits(small, ScalarType.Char))
                operand = Fold(new("literal", ScalarType.Char, operand.Location, small.ToString(CultureInfo.InvariantCulture)));
            var boolean = target.Type == ScalarType.Bool && symbol is "&" or "|" or "^";
            if (!IsNum(target.Type) && !boolean) Fail("AI3107", $"Expected a numeric target, found {Show(target.Type)}.", target.Location);
            if (NonIntegral(target.Type) && (shift || symbol is "&" or "|" or "^")) Fail("AI3107", "Bitwise operators require integer operands.", target.Location);
            if (!shift && !boolean && TryCoerce(operand, target.Type) is null) Fail("AI3107", $"Expected {Show(target.Type)}, found {Show(operand.Type)}.", operand.Location);
            var value = MakeBinary(symbol, target, operand, at);
            return value.Type == target.Type ? value : Fold(new("cast", target.Type, at, Left: value) { Wraps = _wraps });
        }
        private ScalarExpression IncrementValue(ScalarExpression operand,string operation,SourceLocation at)
        {
            var old=new ScalarExpression("reference-read",operand.Type,at);
            var value=CompoundValue(old,operation.EndsWith("++",StringComparison.Ordinal)?"+":"-",Fold(new("literal",ScalarType.Int,at,"1")),at);
            return new(operation,operand.Type,at,Left:operand,Right:value){Wraps=_wraps};
        }
        private ScalarExpression Lower(ScalarExpression expression)
        {
            if (expression.Operation is not ("pre++" or "post++" or "pre--" or "post--") || expression.Left is null) return expression;
            var one = Fold(new("literal", ScalarType.Int, expression.Location, "1"));
            if (_propertyReads.ContainsKey(expression.Left))
                return PropertyAssign(expression.Left, expression.Operation.EndsWith("++", StringComparison.Ordinal) ? "+=" : "-=", one, expression.Location, expression.Operation.StartsWith("post", StringComparison.Ordinal), true);
            var value = CompoundValue(expression.Left, expression.Operation.EndsWith("++", StringComparison.Ordinal) ? "+" : "-", one, expression.Location);
            return new("=", expression.Left.Type, expression.Location, "compound", Left: expression.Left, Right: value) { Wraps = _wraps };
        }
        private ScalarExpression Expression(int minimum = 1,bool valueRequired=true)
        {
            var beforeOmittedCall=activeCompilation?.HasOmittedPartialMembers==true?new HashSet<string>(_assigned):null;
            var left = Unary();
            // A switch expression binds tighter than every binary operator (C# switch_expression).
            while (Current.Text == "switch" && Has(ScalarFeatures.Patterns) || Current.Text == "with" && IsStruct(left.Type) && !IsLocalName("with"))
                left = Current.Text == "with" ? WithExpression(left) : SwitchExpression(left);
            while (Precedence(Current.Text) >= minimum)
            {
                var op = Next(); var precedence = Precedence(op.Text);
                if (op.Text == "is") { Read(left); left = IsExpression(left, op); continue; }
                if (op.Text == "as") { Read(left); left = AsExpression(left, op); continue; }
                var assignment = precedence == 1;
                if (assignment && op.Text == "=" && Take("ref")) { left = ReferenceReassignment(left, ReferenceTarget(), op.Location); continue; }
                var beforeRight = new HashSet<string>(_assigned);
                if (!assignment || op.Text != "=") Read(left);
                var leftFlow = Flow(left);
                if (op.Text == "&&") _assigned.UnionWith(leftFlow.T); else if (op.Text == "||") _assigned.UnionWith(leftFlow.F);
                var right = Expression(assignment ? precedence : precedence + 1);
                Read(right);
                if (assignment && left.Operation == "array-index") { left = ArrayAssignment(left, op.Text, right, op.Location); continue; }
                if (assignment && _propertyReads.ContainsKey(left)) { left = PropertyAssign(left, op.Text, right, op.Location,captureUpdate:valueRequired&&Has(ScalarFeatures.MutableStructAccessors)); continue; }
                if (assignment && StaticEventUpdate(left, op.Text, right, op.Location) is { } eventUpdate) { left = eventUpdate; continue; }
                if (assignment)
                {
                    if (left.Operation == "field" && !OutputStructStorage(left)) Read(RootOf(left));
                    Write(left);
                    if (ReferenceVariable(left) && op.Text != "=") { left = ReferenceUpdate(left, op.Text[..^1], right, false, op.Location); continue; }
                    if (ReferenceVariable(left) && (ReferenceAlias(left) || ReassignsReference(right))) { left = ReferenceWrite(left, right, op.Location); continue; }
                    right = op.Text == "=" ? Coerce(right, left.Type) : CompoundValue(left, op.Text[..^1], right, op.Location);
                    if (left.Operation == "local") _assigned.Add(left.Value!);
                    if (left.Operation == "field" && OutputStructStorage(left)) AssignReferenceStorage(left);
                    // Compound assignments retain their receiver-capture identity through native verification.
                    left = new("=", left.Type, op.Location, op.Text == "=" ? null : "compound", Left: left, Right: right) { Wraps = _wraps };
                }
                else if (op.Text is "&&" or "||")
                {
                    Require(left, ScalarType.Bool); Require(right, ScalarType.Bool);
                    var alwaysRight = BooleanConstant(left) == (op.Text == "&&");
                    if (!alwaysRight) _assigned.IntersectWith(beforeRight);
                    var rightFlow = Flow(right);
                    left = Fold(new(op.Text, ScalarType.Bool, op.Location, Left: left, Right: right) { Wraps = _wraps });
                    left = op.Text == "&&" ? WithFlow(left, leftFlow.T.Concat(rightFlow.T), []) : WithFlow(left, [], leftFlow.F.Concat(rightFlow.F));
                }
                else if (op.Text is "==" or "!=" && (left.Type == ScalarType.Bool || right.Type == ScalarType.Bool))
                {
                    Require(right, left.Type);
                    left = Fold(new(op.Text, ScalarType.Bool, op.Location, Left: left, Right: right) { Wraps = _wraps });
                }
                else left = MakeBinary(op.Text, left, right, op.Location);
            }
            if (minimum == 1 && Current.Text == "?" && TokenAt(_position + 1).Text == "ref") left = ReferenceConditional(left);
            else if(minimum==1&&Current.Text=="?"&&_bindingConst)left=ConstantConditional(left);
            // The left of plain '=' may be unassigned; all other expression results must be readable.
            if(left.Operation=="omitted-partial-call"&&beforeOmittedCall is not null){_assigned.Clear();_assigned.UnionWith(beforeOmittedCall);}
            if (minimum == 1 && Current.Text != "=" && !_outputStorage) Read(left);
            return left;
        }
        private bool IsStringLocal(string name)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(IdentifierName(name), out var local)) return local.Type == ScalarType.String;
            return false;
        }
        // C# regular string literal escapes; the result is the exact UTF-16 text (lone surrogates are preserved).
        private static string? DecodeStringLiteral(string content)
        {
            var text = new StringBuilder();
            for (var i = 0; i < content.Length; i++)
            {
                var ch = content[i];
                if (ch != '\\') { text.Append(ch); continue; }
                if (++i >= content.Length) return null;
                switch (content[i])
                {
                    case 'n': text.Append('\n'); break;
                    case 't': text.Append('\t'); break;
                    case 'r': text.Append('\r'); break;
                    case '0': text.Append('\0'); break;
                    case 'a': text.Append('\a'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'v': text.Append('\v'); break;
                    case '\\': text.Append('\\'); break;
                    case '\'': text.Append('\''); break;
                    case '"': text.Append('"'); break;
                    case 'x':
                    {
                        var begin = i + 1; var end = begin;
                        while (end < content.Length && end - begin < 4 && char.IsAsciiHexDigit(content[end])) end++;
                        if (end == begin) return null;
                        text.Append((char)int.Parse(content[begin..end], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)); i = end - 1; break;
                    }
                    case 'u' or 'U':
                    {
                        var width = content[i] == 'u' ? 4 : 8; var begin = i + 1;
                        if (begin + width > content.Length || !content.Substring(begin, width).All(char.IsAsciiHexDigit)) return null;
                        var scalar = long.Parse(content.Substring(begin, width), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                        if (width == 4) text.Append((char)scalar);
                        else if (scalar > 0x10FFFF || scalar is >= 0xD800 and <= 0xDFFF) return null;
                        else text.Append(char.ConvertFromUtf32((int)scalar));
                        i = begin + width - 1; break;
                    }
                    default: return null;
                }
            }
            return text.ToString();
        }
        private static int? DecodeCharLiteral(string content)
        {
            if (content.Length == 0) return null;
            if (content[0] != '\\') return content.Length == 1 && content[0] != '\'' ? content[0] : null;
            if (content.Length < 2) return null;
            if (content.Length == 2)
                return content[1] switch
                {
                    'n' => 10, 't' => 9, 'r' => 13, '0' => 0, 'a' => 7, 'b' => 8, 'f' => 12, 'v' => 11, '\\' => 92, '\'' => 39, '"' => 34, _ => null
                };
            var hex = content[1] switch { 'u' when content.Length == 6 => content[2..], 'x' when content.Length is >= 3 and <= 6 => content[2..], _ => null };
            return hex is not null && hex.All(char.IsAsciiHexDigit) ? int.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : null;
        }
        private static bool IsDigits(string text) { foreach (var ch in text) if (!char.IsAsciiDigit(ch)) return false; return text.Length > 0; }
        // Splits a literal token into magnitude, u/l suffix flags and radix form; false when malformed.
        private bool ParseLiteral(string text, out BigInteger magnitude, out bool u, out bool l, out bool isDecimal)
        {
            magnitude = BigInteger.Zero; u = false; l = false; isDecimal = true;
            if (Has(ScalarFeatures.Types))
                while (text.Length > 1 && text[^1] is 'u' or 'U' or 'l' or 'L')
                {
                    if (text[^1] is 'u' or 'U') { if (u) break; u = true; } else { if (l) break; l = true; }
                    text = text[..^1];
                }
            var radix = 10; var digits = text;
            if (Has(ScalarFeatures.Bits) && text.Length > 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'b' or 'B')
            { radix = text[1] is 'x' or 'X' ? 16 : 2; digits = text[2..]; isDecimal = false; }
            if (Has(ScalarFeatures.Bits)) { if (digits.EndsWith('_')) return false; digits = digits.Replace("_", "", StringComparison.Ordinal); }
            if (digits.Length == 0) return false;
            foreach (var ch in digits)
            {
                var digit = ch >= '0' && ch <= '9' ? ch - '0' : ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 : ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : 99;
                if (digit >= radix) return false;
                magnitude = magnitude * radix + digit;
            }
            return true;
        }
        // Integer literal; type is the first of the C# candidate list that holds the value.
        private static readonly Regex FloatLiteralPattern = new(@"^[0-9]+(_[0-9]+)*(\.[0-9]+(_[0-9]+)*)?([eE][+-]?[0-9]+(_[0-9]+)*)?[fFdD]?$", RegexOptions.CultureInvariant);
        // Decimal floating literal: digits [. digits] [exponent] [f|F|d|D]; f is binary32, everything else binary64.
        private bool TryFloatLiteral(Lexeme token, out ScalarExpression? literal)
        {
            literal = null;
            var text = token.Text;
            if (!FloatLiteralPattern.IsMatch(text) || text.IndexOfAny(['.', 'e', 'E', 'f', 'F', 'd', 'D']) < 0) return false;
            var single = text[^1] is 'f' or 'F';
            var digits = (text[^1] is 'f' or 'F' or 'd' or 'D' ? text[..^1] : text).Replace("_", "", StringComparison.Ordinal);
            double value;
            if (single)
            {
                var narrow = float.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (float.IsInfinity(narrow)) { Fail("AI3107", "Floating-point literal is outside the range of f32.", token.Location); return true; }
                value = narrow;
            }
            else
            {
                value = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsInfinity(value)) { Fail("AI3107", "Floating-point literal is outside the range of f64.", token.Location); return true; }
            }
            var type = single ? ScalarType.F32 : ScalarType.F64;
            literal = Fold(new("literal", type, token.Location, single ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture)));
            return true;
        }
        // Decimal literal: digits [. digits] [exponent] followed by m or M. A plain decimal-point literal stays f64 (as in C#); there is no implicit double-to-decimal conversion.
        private static readonly Regex DecimalLiteralPattern = new(@"^[0-9]+(_[0-9]+)*(\.[0-9]+(_[0-9]+)*)?([eE][+-]?[0-9]+(_[0-9]+)*)?[mM]$", RegexOptions.CultureInvariant);
        private bool TryDecimalLiteral(Lexeme token, out ScalarExpression? literal)
        {
            literal = null;
            var text = token.Text;
            if (!DecimalLiteralPattern.IsMatch(text)) return false;
            decimal value;
            try { value = decimal.Parse(text[..^1].Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture); }
            catch (OverflowException) { Fail("AI3107", "Decimal literal is outside the range of decimal.", token.Location); return true; }
            // The canonical text keeps the scale (1.10m stays 1.10), so the verifier re-parses exactly the same value.
            literal = Fold(new("literal", ScalarType.Decimal, token.Location, value.ToString(CultureInfo.InvariantCulture)));
            return true;
        }
        private bool TryLiteral(Lexeme token, bool negated, out ScalarExpression? literal)
        {
            literal = null;
            if (Has(ScalarFeatures.Decimals) && TryDecimalLiteral(token, out literal)) return true;
            if (Has(ScalarFeatures.Floats) && TryFloatLiteral(token, out literal)) return true;
            if (!ParseLiteral(token.Text, out var magnitude, out var u, out var l, out var isDecimal))
            { Fail("AI3107", "Integer literal is outside Int32 or is not a valid literal.", token.Location); return false; }
            if (!Has(ScalarFeatures.Types))
            {
                if (negated && isDecimal && magnitude == 2147483648L) { literal = Fold(new("literal", ScalarType.Int, token.Location, "-2147483648")); return true; }
                if (magnitude > int.MaxValue) { Fail("AI3107", "Integer literal is outside Int32 or is not decimal.", token.Location); return false; }
                literal = Fold(new("literal", ScalarType.Int, token.Location, magnitude.ToString(CultureInfo.InvariantCulture))); return true;
            }
            ScalarType[] candidates = (u, l) switch
            {
                (false, false) => [ScalarType.Int, ScalarType.UInt, ScalarType.Long, ScalarType.ULong],
                (true, false) => [ScalarType.UInt, ScalarType.ULong],
                (false, true) => [ScalarType.Long, ScalarType.ULong],
                _ => [ScalarType.ULong]
            };
            // A decimal literal 2147483648 / 9223372036854775808 directly after unary minus denotes the signed minimum.
            if (negated && isDecimal && !u && !l && magnitude == 2147483648L) { literal = Fold(new("literal", ScalarType.Int, token.Location, "-2147483648")); return true; }
            if (negated && isDecimal && !u && magnitude == new BigInteger(9223372036854775808UL)) { literal = Fold(new("literal", ScalarType.Long, token.Location, "-9223372036854775808")); return true; }
            foreach (var candidate in candidates)
                if (ScalarConstants.Fits(magnitude, candidate)) { literal = Fold(new("literal", candidate, token.Location, magnitude.ToString(CultureInfo.InvariantCulture))); return true; }
            Fail("AI3107", "Integer literal is outside the range of every integer type.", token.Location); return false;
        }
        private ScalarExpression Unary()
        {
            var token = Current;
            if (token.Text is "+" or "-" or "!" or "++" or "--" || token.Text == "~" && Has(ScalarFeatures.Bits))
            {
                Next();
                if (token.Text == "-" && Current.Text.Length > 0 && char.IsAsciiDigit(Current.Text[0]))
                {
                    var literalToken = Current;
                    if (ParseLiteral(literalToken.Text, out var magnitude, out var u, out var l, out var isDecimal) && isDecimal && !u
                        && (magnitude == 2147483648L && !l || Has(ScalarFeatures.Types) && magnitude == new BigInteger(9223372036854775808UL)))
                    {
                        Next(); TryLiteral(literalToken, true, out var minimum); return minimum!;
                    }
                }
                var operand = Unary(); Read(operand);
                ScalarExpression made;
                if (token.Text == "!") { Require(operand, ScalarType.Bool); made = new("unary!", ScalarType.Bool, token.Location, Left: operand) { Wraps = _wraps }; }
                else if (token.Text is "++" or "--")
                {
                    if (!IsNum(operand.Type) && !IsEnum(operand.Type) && operand.Type != ScalarType.Char) Fail("AI3107", $"Expected a numeric operand, found {Show(operand.Type)}.", operand.Location);
                    Write(operand);
                    if (ReferenceVariable(operand)) return ReferenceUpdate(operand, token.Text == "++" ? "+" : "-", new("literal", ScalarType.Int, token.Location, "1"), false, token.Location);
                    return _propertyReads.ContainsKey(operand) ? Lower(new("pre" + token.Text, operand.Type, token.Location, Left: operand) { Wraps = _wraps }) : IncrementValue(operand,"pre"+token.Text,token.Location);
                }
                else if (token.Text == "~" && IsEnum(operand.Type))
                {
                    // ~E is (E)~(U)x; the complement of an in-range value is narrowed back without an overflow check (as C# does).
                    var u = EnumOf(operand.Type).Underlying;
                    var promoted = ScalarConstants.PromoteUnary(u, false)!.Value;
                    var inverted = Fold(new("unary~", promoted, token.Location, Left: Coerce(Retype(operand, u), promoted)) { Wraps = _wraps });
                    return Retype(inverted.Type == u ? inverted : Fold(new("cast", u, token.Location, Left: inverted) { Wraps = true }), operand.Type);
                }
                else
                {
                    if (!Numeric(operand.Type) && !(NonIntegral(operand.Type) && token.Text is "+" or "-"))
                        Fail("AI3107", $"Expected an integer operand, found {Show(operand.Type)}.", operand.Location);
                    var promoted = NonIntegral(operand.Type) ? operand.Type : ScalarConstants.PromoteUnary(operand.Type, token.Text == "-");
                    if (promoted is null) Fail("AI3107", $"Unary '-' is not defined for {Show(operand.Type)}.", token.Location);
                    made = new("unary" + token.Text, promoted!.Value, token.Location, Left: Coerce(operand, promoted.Value)) { Wraps = _wraps };
                }
                var folded = Fold(made);
                if (token.Text == "!") { var (t, f) = Flow(operand); WithFlow(folded, f, t); }
                return folded;
            }
            ScalarExpression expression;
            if (Has(ScalarFeatures.Async) && token.Text == "async" && IsLambdaAt(_position + 1)) Fail("AI3100", "Async lambdas are not supported yet.", token.Location);
            if (_queryNest > 0 && IsLambdaAt(_position)) Fail("AI3100", "Lambdas and anonymous methods inside query clauses are not supported yet.", token.Location);
            if (QueryAt()) return QueryExpression();
            if (IsLambdaAt(_position)) return LambdaPlaceholder();
            if (Has(ScalarFeatures.Async) && token.Text == "await" && !IsLocalName("await") && AwaitAt()) return AwaitExpression();
            if (StaticImportCandidates(token) is {Count:>0} importedStatics) expression=StaticImportedExpression(token,importedStatics);
            else if (IsEscapedIdentifier(token.Text) && QualifiedBuiltinAt(_position,expression:true) is null)
            {
                if (TokenAt(_position + 1).Text == "." && !IsLocalName(token.Text) && (StructNamed(token.Text) is not null || ScopedTypeArgument(token.Text,out var escapedTypeArgument) && IsStruct(escapedTypeArgument)))
                    expression = StaticQualified();
                else if (TokenAt(_position + 1).Text == "." && EnumNamed(token.Text) is { } escapedEnum && !IsLocalName(token.Text))
                {
                    Next(); Next(); var member = MemberToken();
                    var found = escapedEnum.Members.FirstOrDefault(m => m.Key == IdentifierName(member.Text));
                    if (found.Key is null) Fail("AI3103", $"Enum '{escapedEnum.Name}' has no member '{member.Text}'.", member.Location);
                    expression = Fold(new("literal", escapedEnum.Id, token.Location, found.Value.ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                Next(); expression = NamedExpression(token with { Text = token.Name },true);
                }
            }
            else if (Has(ScalarFeatures.Async) && activeAsync && token.Text == "Task" && TokenAt(_position + 1).Text == "." && !IsLocalName("Task") && StructNamed("Task") is null) expression = TaskStatic();
            else if (Has(ScalarFeatures.TypeIdentity)&&token.Text=="typeof") expression=TypeOfExpression();
            else if (ObjectStaticAt()) expression=ObjectStaticCall();
            else if (ImplicitNativeObjectAt()) expression=ImplicitNativeObject();
            else if (ImplicitNativeGetTypeAt()) expression=ImplicitNativeGetType();
            else if (NameofAt()) expression = NameofExpression();
            else if (TypedArrayCastAt()) expression = TypedArrayCast();
            else if (StringArrayCastAt()) expression = StringArrayCast();
            else if (NativeStaticAt()) expression = NativeStatic();
            else if(CompilationFunctionAt(_position) is {} compiledFunction)
            {
                _position=compiledFunction.End;var name=new Lexeme(compiledFunction.Name,token.Location);
                expression=Current.Text=="("||Current.Text=="<"&&!MethodGroupAhead()?Call(name):Delegating?MethodGroupExpression(name):throw DeclarationError("AI3100","Function values require delegate support.",token.Location);
            }
            else if(CompilationEnumAt(_position) is {} compiledEnum)
            {
                _position=compiledEnum.End;Expect(".");var member=MemberToken();var found=compiledEnum.Enum.Members.FirstOrDefault(m=>m.Key==member.Name);
                if(found.Key is null)Fail("AI3103","The enum has no member '"+member.Text+"'.",member.Location);
                expression=Fold(new("literal",compiledEnum.Enum.Id,token.Location,found.Value.ToString(CultureInfo.InvariantCulture)));
            }
            else if (StaticQualifiedAt()) expression = StaticQualified();
            else if (token.Text is "checked" or "unchecked" && Has(ScalarFeatures.Checked) && _tokens[_position + 1].Text == "(")
            {
                Next(); Next(); var saved = _wraps; _wraps = token.Text == "unchecked";
                try { expression = Expression(); } finally { _wraps = saved; }
                Expect(")");
            }
            else if (token.Text == "(" && QualifiedTypeCast() is {} qualifiedCast)
            {
                _position=qualifiedCast.End;var operand=Unary();Read(operand);
                expression=QualifiedExplicitCast(qualifiedCast.Type,operand,qualifiedCast.Token.Location);
            }
            else if (GenericDelegateCast() is { } genericCast)
            {
                _position = genericCast.End;
                var operand = Unary(); Read(operand);
                expression = ReferenceCast(genericCast.Type, operand, genericCast.Token.Location);
            }
            else if (token.Text == "(" && TokenAt(_position + 2).Text == ")" && !NativeAliasParenthesizedValue()
                && CastType(_tokens[_position + 1].Text) is { } castType)
            {
                var typeToken = _tokens[_position + 1];
                Next(); Next(); Next();
                var operand = Unary(); Read(operand);
                if(castType==ScalarType.Bool&&!(Has(ScalarFeatures.Boxing)&&(operand.Type==ScalarType.Bool||IsReference(operand.Type)))||castType==ScalarType.String&&!Has(ScalarFeatures.NullStrings))Fail("AI3118","Only numeric conversions are supported.",typeToken.Location);
                if(castType==ScalarType.Bool&&operand.Type==ScalarType.Bool)expression=operand;
                else if (castType == ScalarType.String && Has(ScalarFeatures.NullStrings))
                {
                    if(Has(ScalarFeatures.Boxing)&&(IsObject(operand.Type)||BuiltinEquatableConversion(ScalarType.String,operand.Type)))expression=ReferenceCast(castType,operand,typeToken.Location);
                    else
                    {
                        if (operand.Type != ScalarType.String && !IsNull(operand.Type)) Fail("AI3118", "Only identity and null conversions to string are supported.", typeToken.Location);
                        expression = operand with { Type = ScalarType.String };
                    }
                }
                else if (IsReference(castType) || IsReference(operand.Type) || IsNull(operand.Type)) expression = ReferenceCast(castType, operand, typeToken.Location);
                else if (IsEnum(castType) || IsEnum(operand.Type)) expression = EnumCast(castType, operand, token.Location);
                else
                {
                    if (!IsNum(operand.Type)) Fail("AI3118", $"Cannot convert {Show(operand.Type)} to {Show(castType)}.", operand.Location);
                    expression = Fold(new("cast", castType, token.Location, Left: operand) { Wraps = _wraps });
                }
            }
            else if (Take("(")) { expression = Expression(); Expect(")"); }
            else
            {
                Next();
                if (token.Text is "true" or "false") expression = Fold(new("literal", ScalarType.Bool, token.Location, token.Text));
                else if (Has(ScalarFeatures.Chars) && token.Text.Length >= 3 && token.Text[0] == '\'' && token.Text[^1] == '\'')
                {
                    var unit = DecodeCharLiteral(token.Text[1..^1]);
                    if (unit is null) Fail("AI3107", "A character literal must be exactly one UTF-16 code unit (or a valid escape).", token.Location);
                    expression = Fold(new("literal", ScalarType.Char, token.Location, unit!.Value.ToString(CultureInfo.InvariantCulture)));
                }
                else if (Has(ScalarFeatures.Chars) && token.Text == "''") { Fail("AI3107", "An empty character literal is not allowed.", token.Location); return null!; }
                else if (Has(ScalarFeatures.Strings) && token.Text.Length >= 2 && token.Text[0] == '"' && token.Text[^1] == '"')
                {
                    var text = DecodeStringLiteral(token.Text[1..^1]);
                    if (text is null) Fail("AI3107", "A string literal contains an invalid escape sequence.", token.Location);
                    expression = new("literal", ScalarType.String, token.Location, text);
                    if (Has(ScalarFeatures.Names)) expression = Fold(expression);
                }
                else if (token.Text.Length > 0 && char.IsAsciiDigit(token.Text[0]))
                {
                    TryLiteral(token, false, out var literal); expression = literal!;
                }
                else if (token.Text == "base" && Has(ScalarFeatures.Classes))
                {
                    if (_struct is null || _struct.BaseIndex < 0 || _this is null) Fail("AI3103", "'base' is available only inside member functions of a derived class.", token.Location);
                    if (_inBaseArgs) Fail("AI3103", "'base' cannot be used in a base constructor call.", token.Location);
                    var b = _structs[_struct!.BaseIndex];
                    var baseReceiver = Upcast(new("local", _this!.Type, token.Location, _this.EmittedName), b.Id);
                    _baseAccessorReceivers.Add(baseReceiver);
                    if (Current.Text == "[" && Has(ScalarFeatures.Indexers)) expression = IndexerRead(baseReceiver);
                    else
                    {
                        Expect("."); var member = MemberToken();
                        if (member.Text == "new") Fail("AI3106", "'base.new(...)' is allowed only as the first statement of a constructor of a derived class.", member.Location);
                        baseReceiver=RecordMemberReceiver(baseReceiver,member);_baseAccessorReceivers.Add(baseReceiver);
                        if (Current.Text is not ("(" or "<")) expression = FieldRead(baseReceiver, member);
                        else expression = MethodCall(baseReceiver, member, direct: true);
                    }
                }
                else if (token.Text == "this" && Has(ScalarFeatures.Structs))
                {
                    if (_this is null) Fail("AI3103", "'this' is available only inside struct member functions.", token.Location);
                    if (_inBaseArgs) Fail("AI3103", "'this' cannot be used in a base constructor call.", token.Location);
                    expression = new("local", _this!.Type, token.Location, _this.EmittedName);
                }
                else if (token.Text == "new" && Has(ScalarFeatures.Structs)) expression = NewExpression(token);
                else if (token.Text == "null" && Has(ScalarFeatures.Classes)) expression = new("null", (ScalarType)NullTypeId, token.Location);
                else if (token.Text == "default" && Current.Text == "(" && Has(ScalarFeatures.Generics))
                {
                    Next(); var typeToken = TypeToken(); var type = Type(typeToken.Text, typeToken.Location); Expect(")");
                    expression = DefaultValue(type, token.Location);
                }
                else expression = NamedExpression(token,false);
            }
            while (true)
            {
            if(NativeGetTypeAt(expression)){expression=NativeGetTypeCall(expression);continue;}
            if(NativeObjectMemberAt(expression)){expression=NativeObjectMember(expression);continue;}
            while (IsStruct(expression.Type) && (Current.Text == "." || Has(ScalarFeatures.Indexers) && Current.Text == "[") || IsStringArray(expression.Type) && Current.Text == "[" || Has(ScalarFeatures.Strings) && expression.Type == ScalarType.String && Current.Text is "." or "[")
            {
                if(NativeGetTypeAt(expression)){expression=NativeGetTypeCall(expression);continue;}
            if(NativeObjectMemberAt(expression)){expression=NativeObjectMember(expression);continue;}
                ReadNavigationReceiver(expression);
                if (IsStruct(expression.Type) && !IsArray(expression.Type) && Current.Text == "[") { expression = IndexerRead(expression); continue; }
                if (IsArray(expression.Type) && Take("["))
                {
                    var index = Expression(); Read(index); Expect("]");
                    expression = new("array-index", ArrayElement(expression.Type), expression.Location, Left: expression, Right: Coerce(index, ScalarType.Int));
                    continue;
                }
                if (IsStruct(expression.Type))
                {
                    Next(); var member = MemberToken(); member=member with {Text=member.Name};
                    if (StructOf(expression.Type).Name.StartsWith("__Grp_", StringComparison.Ordinal)
                        && !_function.Name.StartsWith("__grp_", StringComparison.Ordinal)
                        && !(_struct?.Name.StartsWith("__Grp_", StringComparison.Ordinal) ?? false)
                        && member.Text is not ("Key" or "GetEnumerator"))
                        Fail("AI3103", $"'{Show(expression.Type)}' has no accessible member '{member.Text}'.", member.Location);
                    if(IsDisplayBuilder(StructOf(expression.Type))){expression=DisplayBuilderMember(expression,member);continue;}
                    if (IsTaskType(expression.Type) && !InTaskHelper) { expression = TaskMember(expression, member); continue; }
                    if (member.Text is "Current" or "get_Current" && IteratorShape(expression.Type) is { } iterated)
                    {
                        if (member.Text == "get_Current" || iterated.Sequence || Current.Text == "(") Fail("AI3103", $"'{Show(expression.Type)}' has no member '{member.Text}' that can be used here.", member.Location);
                        expression = MethodCall(expression, new Lexeme("get_Current", member.Location), false, []);
                        continue;
                    }
                    expression = Delegating ? MemberAccess(expression, member) : Current.Text == "(" || Current.Text == "<" && CallableMethod(expression.Type, member) ? MethodCall(expression, member) : FieldRead(expression, member);
                    continue;
                }
                if (Take("["))
                {
                    var index = Expression(); Read(index); Expect("]");
                    expression = new("index", ScalarType.Char, expression.Location, Left: expression, Right: Coerce(index, ScalarType.Int));
                }
                else
                {
                    Next();
                    if (Current.Name != "Length") Fail("AI3100", $"Unsupported string member '{Current.Text}'; only Length is supported.");
                    Next();
                    expression = new("length", ScalarType.Int, expression.Location, Left: expression);
                }
            }
            // N19: a delegate-typed value followed by an argument list is invoked.
            if(NativeGetTypeAt(expression)){expression=NativeGetTypeCall(expression);continue;}
            if(NativeObjectMemberAt(expression)){expression=NativeObjectMember(expression);continue;}
            if (Delegating && IsDelegate(expression.Type) && Current.Text == "(") { expression = Invoke(expression, Current.Location); continue; }
            break;
            }
            if (Current.Text is "++" or "--")
            {
                var op = Next(); Read(expression);
                if (!IsNum(expression.Type) && !IsEnum(expression.Type) && expression.Type != ScalarType.Char) Fail("AI3107", $"Expected a numeric operand, found {Show(expression.Type)}.", expression.Location);
                Write(expression);
                expression = ReferenceVariable(expression) ? ReferenceUpdate(expression, op.Text == "++" ? "+" : "-", new("literal", ScalarType.Int, op.Location, "1"), true, op.Location)
                    : _propertyReads.ContainsKey(expression) ? Lower(new("post" + op.Text, expression.Type, op.Location, Left: expression) { Wraps = _wraps }) : IncrementValue(expression,"post"+op.Text,op.Location);
            }
            if (Current.Text is "." or "[" or "(" || Current.Text == "?" && !_bindingConst && !(Has(ScalarFeatures.ReferenceVariables) && TokenAt(_position + 1).Text == "ref") || !Has(ScalarFeatures.Bits) && Current.Text is "&" or "|" or "^")
                Fail("AI3100", "Unsupported scalar expression.");
            return expression;
        }
        private ScalarExpression NamedExpression(Lexeme token,bool escaped)
        {
            ScalarExpression expression;
            if (escaped && LocalNamed(token.Text) is { } escapedLocal)
                expression = Fold(new("local", escapedLocal.Type, token.Location, escapedLocal.EmittedName));
            else if (escaped && token.Text=="GetType" && Has(ScalarFeatures.TypeIdentity) && Current.Text=="(" && _this is not null && _struct is not null && !SourceHidesNativeGetType(_struct.Id,TokenAt(_position+1).Text==")"))
                expression=BindNativeGetType(new("local",_struct.Id,token.Location,_this.EmittedName),token);
            else if (escaped && Has(ScalarFeatures.Boxing) && _struct is not null && token.Text is "Equals" or "ReferenceEquals" or "ToString" or "GetHashCode" && Current.Text=="(" && !NameMemberExists(_struct.Id,token.Text) && !_functions.Any(f=>f.Name==token.Text))
                expression=BindNativeObjectMember(_this is null?null:new("local",_struct.Id,token.Location,_this.EmittedName),token,true);
            else if (Delegating && Current.Text == "(" && LocalNamed(token.Text) is { } called && IsDelegate(called.Type))
            {
                var local = Lookup(token);
                expression = Invoke(Fold(new("local", local.Type, token.Location, local.EmittedName)), token.Location);
            }
            else if (!escaped && token.Text == "field" && _backing is not null && !IsLocalName(token.Text))
            {
                var owner = _struct!;
                if (_static)
                {
                    expression = StaticFieldRead(owner, new Lexeme(_backing, token.Location), allowHidden: true);
                    var backingField = FindStaticField(IndexOf(owner.Id), _backing, allowHidden: true)!;
                    staticPropertyFieldUses!.Add((StaticPropertyDeclaration(_structs[backingField.Owner]), _backing));
                }
                else
                {
                    instancePropertyFieldUses!.Add(StaticPropertyKey(owner,_backing));
                    var index = owner.Fields.ToList().FindIndex(f => f.Name == _backing&&(f.Owner<0||f.Owner==IndexOf(owner.Id)));
                    expression = new("field", owner.Fields[index].Type, token.Location, index.ToString(CultureInfo.InvariantCulture), Left: new("local", _this!.Type, token.Location, _this.EmittedName));
                }
            }
            else if (_struct is not null && !IsLocalName(token.Text) && FindProperty(IndexOf(_struct.Id), token.Text) is { IsStatic: true })
                expression = StaticFieldRead(_struct, token);
            else if (Delegating && _struct is not null && _this is not null && !IsLocalName(token.Text) && Current.Text == "(" && (FindMethod(IndexOf(_struct.Id), token.Text) is null||RecordDelegateHidesMethod(_struct.Id,token.Text))
                && DelegateMember(new("local", _this.Type, token.Location, _this.EmittedName), token) is { } member0)
            {
                if (_inBaseArgs) Fail("AI3103", "'this' cannot be used in a base constructor call.", token.Location);
                expression = Invoke(member0, token.Location);
            }
            else if (Delegating && _struct is not null && _this is not null && !IsLocalName(token.Text) && MethodGroupAhead() && FindMethod(IndexOf(_struct.Id), token.Text) is { IsConstructor: false, IsStatic: false }
                && VisibleInstanceFieldIndex(_struct,token.Text)<0 && FindProperty(IndexOf(_struct.Id), token.Text) is null)
            {
                if (_inBaseArgs) Fail("AI3103", "'this' cannot be used in a base constructor call.", token.Location);
                expression = MethodGroupExpression(token, new("local", _this.Type, token.Location, _this.EmittedName));
            }
            else if (_struct is not null && !IsLocalName(token.Text) && (Current.Text == "(" || Current.Text == "<" && !MethodGroupAhead()) && FindMethod(IndexOf(_struct.Id), token.Text) is not null)
            {
                if (_inBaseArgs) Fail("AI3103", "'this' cannot be used in a base constructor call.", token.Location);
                expression = MethodCall(ImplicitMemberReceiver(token), token);
            }
            else if (_struct is not null && !IsLocalName(token.Text) && FindStaticField(IndexOf(_struct.Id), token.Text) is not null)
                expression = StaticFieldRead(_struct, token);
            else if (Delegating && _struct is not null && !IsLocalName(token.Text) && FindMethod(IndexOf(_struct.Id), token.Text) is { IsStatic: true } && Current.Text != "(")
                expression = MethodGroupExpression(token, StaticType(_struct, token.Location));
            else if (_struct is not null && !IsLocalName(token.Text) && Current.Text != "(" && (_struct.Fields.Any(f => f.Name == token.Text && !f.Hidden) || FindProperty(IndexOf(_struct.Id), token.Text) is not null))
            {
                if (_inBaseArgs) Fail("AI3103", "'this' cannot be used in a base constructor call.", token.Location);
                expression = FieldRead(ImplicitMemberReceiver(token), token);
            }
            else if (IsIdentifier(token.Text) && (escaped || !IsReserved(token.Text) || token.Text is "var" or "let" && IsLocalName(token.Text)))
            {
                if(!IsLocalName(token.Text)&&ScopedCompilationFunction(token.Text,token.Location) is {} scopedFunction)token=token with{Text=scopedFunction};
                if (Current.Text == "." && EnumNamed(token.Text) is { } named && !IsLocalName(token.Text))
                {
                    Next(); var member = MemberToken();
                    var found = named.Members.FirstOrDefault(m => m.Key == member.Name);
                    if (found.Key is null) Fail("AI3103", $"Enum '{named.Name}' has no member '{member.Text}'.", member.Location);
                    expression = Fold(new("literal", named.Id, token.Location, found.Value.ToString(CultureInfo.InvariantCulture)));
                }
                else if (Current.Text == "(" && Has(ScalarFeatures.Functions)) expression = Call(token);
                else if (Current.Text == "<" && Has(ScalarFeatures.Generics) && !IsLocalName(token.Text) && _functions.FirstOrDefault(f => f.Name == token.Text) is { } generic && GenericOf(generic) is not null) expression = Delegating && MethodGroupAhead() ? MethodGroupExpression(token) : Call(token);
                else if (Delegating && !IsLocalName(token.Text) && Current.Text is not ("(" or "." or "[") && _functions.Any(f => f.Name == token.Text))
                    expression = MethodGroupExpression(token);
                else
                {
                    if (Current.Text is "(" or "." or "[" && !(Has(ScalarFeatures.Strings) && Current.Text is "." or "[" && IsStringLocal(token.Text))
                        && !(Has(ScalarFeatures.Boxing)&&Current.Text=="."&&TokenAt(_position+1).Name is "Equals" or "ToString" or "GetHashCode")
                        && !(Has(ScalarFeatures.TypeIdentity)&&Current.Text=="."&&TokenAt(_position+1).Name=="GetType")
                        && !(Current.Text == "." && LocalNamed(token.Text) is { } structLocal && IsStruct(structLocal.Type))
                        && !(Current.Text == "[" && LocalNamed(token.Text) is { } arrayLocal && (IsStringArray(arrayLocal.Type) || Has(ScalarFeatures.Indexers) && IsStruct(arrayLocal.Type))))
                        Fail("AI3100", "Calls and member/index access are outside the scalar capability.", token.Location);
                    var local = Lookup(token); expression = Fold(new("local", local.Type, token.Location, local.EmittedName));
                }
            }
            else { Fail("AI3100", $"Unsupported scalar expression '{token.Text}'.", token.Location); return null!; }
            return expression;
        }

        private ScalarExpression Call(Lexeme name)
        {
            List<(ScalarType Type, SourceLocation Location)>? explicitArguments = null;
            if (Take("<"))
            {
                explicitArguments = [];
                do { var typeToken = TypeToken(); explicitArguments.Add((Type(typeToken.Text, typeToken.Location), typeToken.Location)); } while (Take(","));
                Expect(">");
                if (Current.Text != "(") Fail("AI3106", "Expected '(' after the type argument list.");
            }
            Next(); // '('
            var declaration = _functions.FirstOrDefault(f => f.Name == name.Text);
            if (declaration is null) Fail("AI3115", $"Unresolved function '{name.Text}'.", name.Location);
            if (GenericOf(declaration!) is { } generic) return GenericCall(name, declaration!, generic, explicitArguments);
            var arguments = new List<ScalarExpression>();
            if (!Take(")"))
            {
                do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                Expect(")");
            }
            if (arguments.Count != declaration!.Parameters.Count)
                Fail("AI3116", $"'{name.Text}' expects {declaration.Parameters.Count} argument(s) but {arguments.Count} were supplied.", name.Location);
            var namedOrder=BindNamedArguments(arguments,declaration,name.Location);
            for (var i = 0; i < arguments.Count; i++)
                arguments[i] = BindArgument(arguments[i], Type(declaration.Parameters[i].Type, name.Location), ParameterPassing(declaration.Parameters[i].Type).Mode);
            CompleteArguments(arguments);
            var returnType = Type(declaration.ReturnType, name.Location, true);
            return NamedCall(ReferenceResult(new("call", returnType, name.Location, name.Text) { Arguments = arguments }, declaration.ReturnType, declaration.Parameters),namedOrder);
        }
        // f(args) or f<A, B>(args) of a generic function: explicit or inferred type arguments (C# inference over the parameters typed by a bare
        // type parameter: the unique candidate type every such argument converts to; null contributes nothing), constraint checks, then a call of
        // the closed instance f<A, B>, which is bound once per program (not while a generic declaration is only being checked).
        private ScalarExpression GenericCall(Lexeme name, FunctionDeclaration declaration, GenericInfo generic, List<(ScalarType Type, SourceLocation Location)>? explicitArguments, List<ScalarExpression>? given = null, ScalarStruct? owner = null, ScalarStructMethod? method = null, ScalarExpression? receiver = null, bool direct = false, ScalarStructMethod? selectedImplementation = null,IReadOnlyList<int>? namedOrder=null)
        {
            var arguments = given ?? new List<ScalarExpression>();
            if (given is null && !Take(")"))
            {
                do { var argument = CallArgument(); arguments.Add(argument); } while (Take(","));
                Expect(")");
            }
            namedOrder??=BindNamedArguments(arguments,declaration,name.Location);
            var map = BindGenericArguments(name, declaration, generic, explicitArguments, arguments, owner);
            ScalarType Closed(string text, bool allowUnit) => MemberType(map, text, name.Location, allowUnit);
            for (var j = 0; j < arguments.Count; j++) arguments[j] = BindArgument(arguments[j], Closed(declaration.Parameters[j].Type, false), ParameterPassing(declaration.Parameters[j].Type).Mode);
            CompleteArguments(arguments);
            if(OmittedPartial(declaration))return new("omitted-partial-call",ScalarType.Unit,name.Location);
            if (method is not null && (selectedImplementation is not null || method.Slot is not null && !direct))
                return NamedCall(ClosedGenericMethodCall(method, selectedImplementation, generic, map, receiver, arguments, Closed(declaration.ReturnType, true), name.Location),namedOrder);
            var instance = (method?.QualifiedName ?? generic.Name) + "<" + string.Join(", ", generic.Parameters.Select(p => Show(map[p.Name]))) + ">";
            if (!_checkOnly && !map.Values.Any(IsTypeParameterType) && requestedInstances!.Add(instance))
            {
                if (method is null) pendingInstances!.Add((declaration, map, instance));
                else
                {
                    // The backend consumes exact member metadata for receiver checks and type initialization.
                    // Keep the open declaration for lookup; the closed body is bound by the queue below.
                    var index = IndexOf(owner!.Id); var current = _structs[index];
                    _structs[index] = current with { Methods = [.. current.Methods, method with { Name = instance, QualifiedName = instance, IsLifted = true }] };
                    pendingMethodInstances!.Add((owner, method, map, instance));
                }
            }
            var call = ReferenceResult(new("call", Closed(declaration.ReturnType, true), name.Location, instance)
            { Arguments = method is { IsStatic: false } ? [method.ReceiverMode==ScalarParameterMode.Value?Upcast(receiver!, owner!.Id):AccessorReceiverArgument(method,receiver!,name.Location), .. arguments] : arguments }, declaration.ReturnType, declaration.Parameters, method is { IsStatic: false } ? 1 : 0);
            return NamedCall(method is null ? call : AccessorReferenceResult(call,method),namedOrder);
        }
        private Dictionary<string, ScalarType> BindGenericArguments(Lexeme name, FunctionDeclaration declaration, GenericInfo generic, List<(ScalarType Type, SourceLocation Location)>? explicitArguments, List<ScalarExpression> arguments, ScalarStruct? owner)
        {
            if (arguments.Count != declaration.Parameters.Count)
                Fail("AI3116", $"'{name.Text}' expects {declaration.Parameters.Count} argument(s) but {arguments.Count} were supplied.", name.Location);
            if (explicitArguments is not null && explicitArguments.Count != generic.Parameters.Count)
                Fail("AI3116", $"'{name.Text}' expects {generic.Parameters.Count} type argument(s) but {explicitArguments.Count} were supplied.", name.Location);
            var map = owner?.TypeArguments is { } ownerArguments ? new Dictionary<string, ScalarType>(ownerArguments) : new Dictionary<string, ScalarType>(StringComparer.Ordinal);
            var dependent = genericConstraintSources!.ContainsKey(generic.NameOffset) || generic.Parameters.Any(p => p.Constraints.Any(c => c.Text.Contains('<')) || p.Placeholder >= 0 && _structs[p.Placeholder].ParameterBounds is not null);
            for (var i = 0; i < generic.Parameters.Count; i++)
            {
                var parameter = generic.Parameters[i]; ScalarType chosen;
                if (explicitArguments is not null) chosen = explicitArguments[i].Type;
                else if (UsesVariantInference(declaration))
                {
                    var inferred = InferVariantArgument(declaration, parameter.Name, arguments);
                    if (inferred is null) Fail("AI3116", $"The type arguments for '{name.Text}' cannot be inferred from the usage; specify them explicitly.", name.Location);
                    chosen = inferred!.Value;
                }
                else
                {
                    var candidates = arguments.Where((a, j) => TypeParameterReference(ParameterPassing(declaration.Parameters[j].Type).Element, parameter.Name) && !IsNull(a.Type) && a.Type != ScalarType.Unit).ToList();
                    // Inside Name<..T..> a type argument is inferred exactly from the argument's closed generic type.
                    var exact = new List<ScalarType>();
                    for (var j = 0; j < arguments.Count; j++) if (arguments[j].Type != ScalarType.Unit) Unify(ParameterPassing(declaration.Parameters[j].Type).Element.Trim(), arguments[j].Type, parameter.Name, exact, ParameterPassing(declaration.Parameters[j].Type).Mode != ScalarParameterMode.Value);
                    var choices = candidates.Select(a => a.Type).Concat(exact).Distinct().Where(t => t != ScalarType.Unit && !IsFunctionValue(t) && candidates.All(a => TryCoerce(a, t) is not null) && exact.All(e => e == t)).ToList();
                    if (choices.Count != 1) Fail("AI3116", $"The type arguments for '{name.Text}' cannot be inferred from the usage; specify them explicitly.", name.Location);
                    chosen = choices[0];
                }
                // After a declaration error the placeholders may be missing; that error is reported alone.
                if (parameter.Placeholder < 0) Fail("AI3106", "Generic declaration not bound.", name.Location);
                if (!dependent && !Satisfies(chosen, _structs[parameter.Placeholder]))
                    Fail("AI3107", $"The type '{Show(chosen)}' cannot be used as type parameter '{parameter.Name}' of '{name.Text}': it does not satisfy the constraints.", explicitArguments is not null ? explicitArguments[i].Location : name.Location);
                map[parameter.Name] = chosen;
            }
            if (dependent) for (var i = 0; i < generic.Parameters.Count; i++)
            {
                var parameter = generic.Parameters[i]; var chosen = map[parameter.Name];
                if (!Satisfies(chosen, ReboundConstraint(_structs[parameter.Placeholder], map, name.Location, ScopeTypeArguments(map, owner, generic))))
                    Fail("AI3107", $"The type '{Show(chosen)}' cannot be used as type parameter '{parameter.Name}' of '{name.Text}': it does not satisfy the constraints.", explicitArguments is not null ? explicitArguments[i].Location : name.Location);
            }
            return map;
        }
        private void Unify(string text, ScalarType actual, string parameter, List<ScalarType> exact, bool nested)
        {
            if (TypeParameterReference(text, parameter)) { if (nested) exact.Add(actual); return; }
            if (Has(ScalarFeatures.TypedArrays) && text.EndsWith("[]", StringComparison.Ordinal) && IsArray(actual))
            { Unify(text[..^2].Trim(), ArrayElement(actual), parameter, exact, true); return; }
            var open = text.IndexOf('<');
            if (open >= 0 && text.EndsWith('>') && Has(ScalarFeatures.Async) && IsStruct(actual))
            {
                var head = text[..open].Trim();
                if (head == "Task" && IsTaskType(actual) && TaskElement(actual) != ScalarType.Unit)
                { Unify(text[(open + 1)..^1].Trim(), TaskElement(actual), parameter, exact, true); return; }
                if (head == "TaskCompletionSource" && StructOf(actual).Name.StartsWith("__TCS_", StringComparison.Ordinal) && StructOf(actual).Fields.FirstOrDefault(f => f.Name == "Task") is { } task)
                { Unify(text[(open + 1)..^1].Trim(), TaskElement(task.Type), parameter, exact, true); return; }
            }
            if (open < 0 || !text.EndsWith('>') || !IsStruct(actual)) return;
            var formalHead=text[..open].Trim();
            if(InferredSequenceElement(formalHead,actual) is {} sequenceElement&&SplitTypeList(text[(open+1)..^1]).Count()==1)
            {Unify(text[(open+1)..^1].Trim(),sequenceElement,parameter,exact,true);return;}
            var resolvedHead=GenericLibraryTypeHead(formalHead);
            if (StructOf(actual) is not { Template: >= 0 } instance || _structs[instance.Template].Name != resolvedHead)
            {
                var info = StructOf(actual);
                if (info.BaseIndex >= 0) Unify(text, _structs[info.BaseIndex].Id, parameter, exact, nested);
                foreach (var parent in info.Interfaces) Unify(text, _structs[parent].Id, parameter, exact, nested);
                return;
            }
            var parts = new List<string>(); var depth = 0; var start = open + 1;
            for (var i = open + 1; i < text.Length - 1; i++)
            {
                if (text[i] == '<') depth++; else if (text[i] == '>') depth--;
                else if (text[i] is ',' or ';' && depth == 0) { parts.Add(text[start..i].Trim()); start = i + 1; }
            }
            parts.Add(text[start..^1].Trim());
            if (parts.Count != instance.TypeArgumentList.Count) return;
            for (var i = 0; i < parts.Count; i++) Unify(parts[i], instance.TypeArgumentList[i], parameter, exact, true);
        }
        // default(T): the zero value of T (0, false, '\0', 0m, the zero struct, null); a null string is not supported.
        private ScalarExpression DefaultValue(ScalarType type, SourceLocation at)
        {
            if (type == ScalarType.String && Has(ScalarFeatures.NullStrings)) return new("null", ScalarType.String, at);
            if (type == ScalarType.String) Fail("AI3100", "default(string) is null, and null strings are not supported.", at);
            if (type == ScalarType.Unit) Fail("AI3107", "default(unit) is not a value.", at);
            if (IsReference(type)) return new("null", type, at);
            if (IsStruct(type)) return new("new", type, at);
            if (type == ScalarType.Bool) return new("literal", ScalarType.Bool, at, "false");
            if (IsEnum(type)) return Fold(new("literal", type, at, "0"));
            var zero = Fold(new("literal", ScalarType.Int, at, "0"));
            return type == ScalarType.Int ? zero : Fold(new("cast", type, at, Left: zero) { Wraps = _wraps });
        }
        private void Read(ScalarExpression expression)
        {
            if (_propertyReads.TryGetValue(expression,out var accessed) && accessed.Property.GetPrivate && !SameTypeScope(_structs[accessed.Property.Owner])) Fail("AI3119", "The get accessor is inaccessible.", expression.Location);
            if (expression.Operation == "property-target") Fail("AI3105", "This property has no get accessor.", expression.Location);
            if ((expression.Operation == "local" || expression.Operation == "field" && OutputStructStorage(expression)) && !AssignedStorage(expression))
                Fail("AI3102", "Local is read before assignment on every reaching path.", expression.Location);
        }
        private void Write(ScalarExpression expression)
        {
            if (_contract) Fail("AI3100", "Contracts cannot mutate scalar locals.", expression.Location);
            if (expression.Operation == "ref-select") { foreach (var arm in expression.Arguments!) Write(arm); return; }
            if (expression.Operation == "ref-reassign") { Write(expression.Left!); return; }
            if (expression.Operation == "call" && expression.ReferenceMode != ScalarParameterMode.Value)
            { if (expression.ReferenceMode != ScalarParameterMode.Ref) Fail("AI3105", "A readonly reference result cannot be written.", expression.Location); return; }
            // ++/-- on a property read is checked when it is lowered to the setter call or backing store.
            if (_propertyReads.ContainsKey(expression)) return;
            if (expression.Operation == "array-index") { Read(expression.Left!); return; }
            if (expression.Operation == "static-field") { WriteStatic(expression); return; }
            if (expression.Operation is "index" or "length") Fail("AI3105", "Strings are immutable; an element or Length cannot be assigned.", expression.Location);
            if (expression.Operation == "field")
            {
                // A struct field is assigned through the local that holds the struct; a field of a class object through any path of field reads
                // from a local that reaches the object reference. Readonly fields only in a constructor of their type, through 'this'.
                var root = RootOf(expression);
                var isThis = _this is not null && root.Value == _this.EmittedName;
                // The object written is the nearest class-typed node below the target; the segment above it is stored in place.
                ScalarExpression? target = null;
                for (var node = expression; node.Operation == "field"; node = node.Left!) if (IsClass(node.Left!.Type)) { target = node.Left; break; }
                if (target is null && root.Operation is not ("local" or "static-field") && !IsReferenceStorage(root)) Fail("AI3107", "A struct field needs writable storage.", expression.Location);
                var targetIsThis = target is not null ? target.Operation == "local" && _this is not null && target.Value == _this.EmittedName : isThis;
                for (var node = expression; node.Operation == "field" && !ReferenceEquals(node, target); node = node.Left!)
                {
                    var owner = StructOf(node.Left!.Type); var field = owner.Fields[int.Parse(node.Value!, CultureInfo.InvariantCulture)];
                    var declaringIndex = field.Owner >= 0 ? field.Owner : IndexOf(owner.Id); var declaring = _structs[declaringIndex];
                    if (!ReferenceEquals(node, expression) && field.IsProperty && IsStruct(node.Type) && !IsClass(node.Type))
                        Fail("AI3105", "A struct value returned by a property is not writable storage.", node.Location);
                    if (field.WritePrivate && (_struct is null || IndexOf(_struct.Id) != declaringIndex))
                        Fail("AI3119", $"The property '{declaring.Name}.{field.Name}' cannot be used in this context because the set accessor is inaccessible.", node.Location);
                    // An init accessor runs only during initialization: an object initializer, or a constructor of the type or a derived type through 'this'.
                    if (field.IsInit && !((_ctor || _initAccessor) && targetIsThis))
                        Fail("AI3105", $"Init-only property '{declaring.Name}.{field.Name}' can be assigned only in an object initializer, or on 'this' in a constructor.", node.Location);
                    if (field.IsReadonly && !((_ctor || _initAccessor) && targetIsThis && declaringIndex == IndexOf(_struct!.Id)))
                        Fail("AI3105", field.IsProperty ? $"Property '{declaring.Name}.{field.Name}' cannot be assigned to -- it is read only." : $"Readonly field '{owner.Name}.{field.Name}' can be assigned only in a constructor of '{owner.Name}'.", node.Location);
                }
                if (target is not null) return;
                if (root.Operation == "static-field")
                {
                    var parts = root.Value!.Split(':'); var field = _structs[int.Parse(parts[0], CultureInfo.InvariantCulture)].StaticFields[int.Parse(parts[1], CultureInfo.InvariantCulture)];
                    if (field.IsProperty && IsStruct(root.Type) && !IsClass(root.Type))
                        Fail("AI3105", "A struct value returned by a property is not writable storage.", root.Location);
                    WriteStatic(root); return;
                }
                if (root.Operation != "local") { Write(root); return; }
                if (isThis && !_ctor && _receiverMode != ScalarParameterMode.Ref && !((_function.IsAsync || Has(ScalarFeatures.MutableStructMethods)&&_iterElem is not null) && !_readonly)) Fail("AI3105", "Struct member functions are readonly: they cannot modify 'this'.", expression.Location);
                var holder = _scopes.SelectMany(s => s.Values).First(l => l.EmittedName == root.Value);
                if (!holder.Mutable) Fail("AI3105", _rangeLocals.Contains(holder.EmittedName) ? $"Range variable '{SourceName(holder.EmittedName)}' cannot be assigned to -- it is read only." : "Cannot write an immutable let.", root.Location);
                return;
            }
            if (expression.Operation != "local") Fail("AI3107", "Assignment requires local storage.", expression.Location);
            var local = _scopes.SelectMany(s => s.Values).First(l => l.EmittedName == expression.Value);
            if (!local.Mutable) Fail("AI3105", _rangeLocals.Contains(local.EmittedName) ? $"Range variable '{SourceName(local.EmittedName)}' cannot be assigned to -- it is read only." : "Cannot write an immutable let.", expression.Location);
        }
        private static List<Lexeme> Lex(string source, SourceLocation start, bool floats, bool chars, bool strings)
        {
            var tokens = new List<Lexeme>();
            int i = 0, line = start.Line, column = start.Column;
            while (i < source.Length)
            {
                var c = source[i];
                if (c is ' ' or '\t' or '\r') { i++; column++; continue; }
                if (c == '/' && i+1 < source.Length && source[i+1] == '/')
                { while (i < source.Length && source[i] != '\n') { i++; column++; } continue; }
                var location = new SourceLocation(line, column, start.Offset+i);
                if (c == '\n') { tokens.Add(new("\n",location)); i++; line++; column=1; continue; }
                var begin = i;
                if (strings && c == '"')
                {
                    // A string literal is one token, including its quotes; escapes are validated and decoded by the binder.
                    var j = i + 1;
                    while (j < source.Length && source[j] != '\n' && source[j] != '"') j += source[j] == '\\' ? 2 : 1;
                    if (j < source.Length && source[j] == '"')
                    {
                        i = j + 1; tokens.Add(new(source[begin..i], location)); column += i - begin; continue;
                    }
                }
                if (chars && c == '\'')
                {
                    // A character literal is one token, including its quotes; its content is validated by the binder.
                    var j = i + 1;
                    while (j < source.Length && source[j] != '\n' && source[j] != '\'') j += source[j] == '\\' ? 2 : 1;
                    if (j < source.Length && source[j] == '\'' && j > i)
                    {
                        i = j + 1; tokens.Add(new(source[begin..i], location)); column += i - begin; continue;
                    }
                }
                if ((ActiveFeatures & ScalarFeatures.UnicodeIdentifiers) != 0 && IdentifierSpelling.TryRead(source,i,out var identifierEnd))
                {
                    i=identifierEnd;tokens.Add(new(source[begin..i],location));column+=i-begin;continue;
                }
                if (c == '@' && i + 1 < source.Length && (char.IsAsciiLetter(source[i + 1]) || source[i + 1] == '_'))
                {
                    i += 2;
                    while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    tokens.Add(new(source[begin..i], location)); column += i - begin; continue;
                }
                if (char.IsAsciiLetterOrDigit(c) || c == '_')
                {
                    while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                    // Decimal floating literals: digits '.' digits, and an exponent sign directly after e/E.
                    if (floats && char.IsAsciiDigit(c) && !(c == '0' && i - begin > 1 && source[begin + 1] is 'x' or 'X' or 'b' or 'B'))
                    {
                        if (i + 1 < source.Length && source[i] == '.' && char.IsAsciiDigit(source[i + 1]) && source[begin..i].All(ch => char.IsAsciiDigit(ch) || ch == '_'))
                        {
                            i++;
                            while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                        }
                        var run = source[begin..i];
                        if (i + 1 < source.Length && source[i] is '+' or '-' && char.IsAsciiDigit(source[i + 1]) && run[^1] is 'e' or 'E'
                            && run[..^1].All(ch => char.IsAsciiDigit(ch) || ch is '_' or '.'))
                        {
                            i++;
                            while (i < source.Length && (char.IsAsciiLetterOrDigit(source[i]) || source[i] == '_')) i++;
                        }
                    }
                }
                else if (i+3 < source.Length && source.Substring(i,4) == ">>>=") i+=4;
                else if (i+2 < source.Length && source.Substring(i,3) is "<<=" or ">>=" or ">>>") i+=3;
                else if (i+1 < source.Length && source.Substring(i,2) is "++" or "--" or "+=" or "-=" or "==" or "!=" or "<=" or ">=" or "&&" or "||"
                    or "<<" or ">>" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "=>") i+=2;
                else i++;
                tokens.Add(new(source[begin..i], location)); column += i-begin;
            }
            tokens.Add(new("",new(line,column,start.Offset+i))); return tokens;
        }
    }
}
