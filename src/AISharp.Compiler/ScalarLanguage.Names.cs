namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private bool NameofAt() => Has(ScalarFeatures.Names) && Has(ScalarFeatures.Strings)
            && Current.Text == "nameof" && TokenAt(_position + 1).Text == "(" && !IsLocalName("nameof")
            && !_functions.Any(f => f.Name == "nameof") && ScopedCompilationFunction("nameof",Current.Location) is null
            && !(_struct is not null && (_struct.Fields.Any(f => f.Name == "nameof" && !f.Hidden)
                || FindStaticField(IndexOf(_struct.Id), "nameof") is not null || FindProperty(IndexOf(_struct.Id), "nameof") is not null || FindMethod(IndexOf(_struct.Id), "nameof") is not null));

        // Resolve only the restricted named-entity grammar. No expression is bound or read, and no
        // getter, method, conversion, constructor, closure capture or definite-assignment update is emitted.
        private ScalarExpression NameofExpression()
        {
            var keyword = Next(); Expect("(");
            var first = Next(); var name = first.Text; ScalarType? type = null; var method = false;
            string? unbound = null;
            var needsMember = false;
            var sourceStart=_position-1;
            var importedStatics=StaticImportCandidates(first);
            var compiledQualified=CompilationQualification(sourceStart,true);
            if(importedStatics.Count>0)
            {
                if(importedStatics.Count>1&&importedStatics.Any(x=>!x.Method))Fail("AI3106","Ambiguous imported static member '"+first.Name+"'.",first.Location);
                name=first.Name;method=importedStatics[0].Method;
                if(!method){var imported=importedStatics[0];type=imported.Enumeration?imported.Type:ScalarConstants.IsNative(imported.Type)?first.Name=="Size"?ScalarType.Int:imported.Type:FindProperty(IndexOf(imported.Type),first.Name)?.Type??FindStaticField(IndexOf(imported.Type),first.Name)!.Type;}
            }
            else if(compiledQualified is {} sourceType)
            {
                _position=sourceType.End;var genericDepth=0;
                for(var cursor=sourceStart;cursor<sourceType.End;cursor++)
                {
                    var part=TokenAt(cursor);
                    if(part.Text=="<")genericDepth++;
                    else if(part.Text is ">" or ">>" or ">>>")genericDepth-=part.Text.Length;
                    else if(genericDepth==0&&IsSourceIdentifier(part.Text))name=part.Name;
                }
                var unboundOpen=sourceType.Name.IndexOf('<');
                var normalizedUnbound=unboundOpen>=0&&sourceType.Name.EndsWith('>')&&sourceType.Name[(unboundOpen+1)..^1].All(c=>c==',');
                var definition=_structs.FirstOrDefault(s=>s.Name==(normalizedUnbound?sourceType.Name[..unboundOpen]:sourceType.Name)&&s.TypeParameters.Count>0);
                var enumDefinition=EnumDefinition(normalizedUnbound?sourceType.Name[..unboundOpen]:sourceType.Name);
                if(normalizedUnbound&&enumDefinition is not null)
                {
                    if(sourceType.Name.Length-unboundOpen-1!=enumDefinition.TypeParameters.Count)Fail("AI3116","Incorrect unbound type arity.",first.Location);
                    type=enumDefinition.Id;CompilationTypeAccess(type.Value,first.Location);
                }
                else if(normalizedUnbound&&definition is not null)
                {
                    if(sourceType.Name.Length-unboundOpen-1!=definition.TypeParameters.Count)Fail("AI3116","Incorrect unbound type arity.",first.Location);
                    type=definition.Id;CompilationTypeAccess(type.Value,first.Location);
                }
                else if(definition is not null)
                {
                    if(Current.Text!="<")Fail("AI3106","The generic type needs type arguments or an unbound arity.",first.Location);
                    if(TokenAt(_position+1).Text is ">" or ",")
                    {Next();var arity=1;while(Take(","))arity++;Expect(">");if(arity!=definition.TypeParameters.Count)Fail("AI3116","Incorrect unbound type arity.",first.Location);type=definition.Id;}
                    else {_position=sourceStart;var written=TypeToken();type=Type(written.Text,written.Location,allowStatic:true);}
                }
                else if(Current.Text=="<")
                {
                    if(TokenAt(_position+1).Text is ">" or ","&&sourceType.Alias is "IEnumerable" or "IEnumerator")
                    {Next();var arity=1;while(Take(","))arity++;Expect(">");if(arity!=1)Fail("AI3116","Incorrect unbound iterator arity.",first.Location);unbound=sourceType.Alias;}
                    else {_position=sourceStart;var written=TypeToken();type=Type(written.Text,written.Location,allowStatic:true);}
                }
                else type=Type(sourceType.Name,first.Location,allowStatic:true);
            }
            else if(activeCompilation is not null&&CanBindTypeParameter(first.Text)&&!CompilationValueShadows(sourceStart)&&Current.Text==")"&&activeCompilation.AliasResolver?.Invoke(first.Text,first.Location.Offset) is {Namespace:true})
            {name=first.Name;method=true;}
            else if(activeCompilation is not null&&CanBindTypeParameter(first.Text)&&!CompilationValueShadows(sourceStart)&&CompilationPaths(sourceStart).LastOrDefault() is var namespacePath&&TokenAt(namespacePath.End).Text==")"&&activeCompilation.NamespaceResolver?.Invoke(namespacePath.Path,first.Location.Offset) is not null)
            { _position=namespacePath.End;name=TokenAt(_position-1).Name;method=true; }
            else if(activeCompilation is not null&&CanBindTypeParameter(first.Text)&&!IsLocalName(first.Text)&&!(_struct is not null&&NameMemberExists(_struct.Id,first.Name))&&
                (CompilationFunctionAt(sourceStart) is {} || ScopedCompilationFunction(first.Text,first.Location) is not null))
            {if(CompilationFunctionAt(sourceStart) is {} function){_position=function.End;name=TokenAt(_position-1).Name;}else name=first.Name;method=true;}
            else
            {
            var importedName=ImportAlias(first.Text);
            var importedQualified=LocalNamed(first.Text) is null&&!ScopedTypeArgument(first.Text,out _)?ImportedQualification(_position-1,false):null;
            if(importedQualified is {} qualifiedImport)
            {
                _position=qualifiedImport.End;name=TokenAt(_position-1).Name;type=Type(qualifiedImport.Name,first.Location,allowStatic:true);
            }
            else if(importedName is not null&&LocalNamed(first.Text) is null&&!ScopedTypeArgument(first.Text,out _))
            {
                name=first.Name;if(ImportAliasType(first.Text,first.Location) is {} importedType)type=importedType;else method=true;
            }
            else if (IsEscapedIdentifier(first.Text))
            {
                name = IdentifierName(first.Text);
                if (LocalNamed(name) is { } escapedLocal) type = escapedLocal.Type;
                else if (_functions.Any(f => f.Name == name)) method = true;
                else if (ScopedTypeArgument(first.Text,out var escapedArgument)) type = escapedArgument;
                else if (StructNamed(first.Text) is { } escapedStructure) type = escapedStructure.Id;
                else if (EnumNamed(first.Text) is { } escapedEnum) type = escapedEnum.Id;
                else Fail("AI3103", $"Unresolved name '{name}'.", first.Location);
            }
            else if (first.Text is "this" or "base")
            {
                if (_this is null || _struct is null) Fail("AI3103", $"'{first.Text}' is unavailable here.", first.Location);
                if (_inBaseArgs) Fail("AI3103", $"'{first.Text}' is unavailable in base constructor arguments.", first.Location);
                if (first.Text == "base" && _struct!.BaseIndex < 0) Fail("AI3103", "No base class is declared.", first.Location);
                type = first.Text == "this" ? _this!.Type : _structs[_struct!.BaseIndex].Id; needsMember = true;
            }
            else if (!IsIdentifier(first.Text) || first.Text is "true" or "false" or "null" or "new")
                Fail("AI3106", "nameof requires a bound variable, type or member name.", first.Location);
            else if (LocalNamed(first.Text) is { } local) type = local.Type;
            else if (first.Text == "field" && _backing is not null)
                Fail("AI3106", "A synthesized property backing field has no source name; name the property or qualify an ordinary field.", first.Location);
            else if (_struct is not null && NameMemberExists(_struct.Id, first.Text))
                (type, method) = NameMember(_struct.Id, first);
            else if (_typeArgs is not null && _typeArgs.TryGetValue(first.Text, out var argument)) type = argument;
            else if (_memberArgs is not null && _memberArgs.TryGetValue(first.Text, out var memberArgument)) type = memberArgument;
            else if (EnumNamed(first.Text) is { } enumeration) type = enumeration.Id;
            else if (StructNamedAny(first.Text) is { } structure)
            {
                if (structure.TypeParameters.Count > 0)
                {
                    if (Current.Text != "<") Fail("AI3106", $"Generic type '{name}' needs type arguments or an unbound arity.", first.Location);
                    if (TokenAt(_position + 1).Text is ">" or ",")
                    {
                        Next(); var arity = 1; while (Take(",")) arity++; Expect(">");
                        if (arity != structure.TypeParameters.Count) Fail("AI3116", $"'{name}' requires {structure.TypeParameters.Count} type argument slots.", first.Location);
                        type = structure.Id;
                    }
                    else
                    {
                        _position--; var written = TypeToken(); type = Type(written.Text, written.Location, allowStatic: true);
                    }
                }
                else type = structure.Id;
            }
            else if (NameBuiltin(first.Text))
            {
                if (Current.Text == "<" && TokenAt(_position + 1).Text is ">" or ",")
                {
                    Next(); var arity = 1; while (Take(",")) arity++; Expect(">");
                    if (arity != 1) Fail("AI3116", $"'{name}' requires one type argument slot.", first.Location);
                    unbound = name;
                }
                else
                {
                    if (Current.Text != "<" && name != "Task") Fail("AI3106", $"Generic type '{name}' needs one type argument or an unbound arity.", first.Location);
                    _position--; var written = TypeToken(); type = Type(written.Text, written.Location, allowStatic: true);
                }
            }
            else if (Has(ScalarFeatures.Delegates) && activeDelegates?.ContainsKey(first.Text) == true) type = Type(first.Text, first.Location);
            else if (_functions.Any(f => f.Name == first.Text)) method = true;
            else if (IsTypeKeyword(first.Text) || first.Text is "string" or "char" or "decimal" or "float" or "double" or "f32" or "f64" or "object" or "void" or "unit" or "nint" or "nuint")
            {
                type = Type(first.Text, first.Location, true); needsMember = true;
            }
            else Fail("AI3103", $"Unresolved or out-of-scope name '{name}'.", first.Location);
            }

            if (needsMember && Current.Text != ".") Fail("AI3106", "This nameof operand has no identifier name.", first.Location);
            while (Take("."))
            {
                var member = Next();
                if (!IsSourceIdentifier(member.Text) || IsReserved(member.Text)) Fail("AI3106", "Expected a member name in nameof.", member.Location);
                if (method || type is null && unbound is null) Fail("AI3106", "A method group cannot be the target of member lookup.", member.Location);
                if (unbound is not null)
                {
                    var target = unbound; unbound = null;
                    if (target == "IEnumerable" && member.Text == "GetEnumerator"
                        || target == "IEnumerator" && member.Text is "MoveNext" or "Dispose"
                        || target == "Task" && member.Text == "GetAwaiter"
                        || target == "TaskCompletionSource" && member.Text is "SetResult" or "TrySetResult") method = true;
                    else if (target == "IEnumerator" && member.Text == "Current") unbound = "<type parameter>";
                    else if (target == "Task" && member.Text is "IsCompleted" or "IsCompletedSuccessfully" or "IsFaulted") type = ScalarType.Bool;
                    else if (target == "TaskCompletionSource" && member.Text == "Task") unbound = "Task";
                    else Fail("AI3103", $"'{target}' has no supported member '{member.Text}'.", member.Location);
                }
                else if (IsEnum(type!.Value))
                {
                    if (!EnumOf(type.Value).Members.Any(m => m.Key == member.Name)) Fail("AI3103", $"No enum member '{member.Text}'.", member.Location);
                    method = true; // An enum constant cannot qualify another named entity.
                }
                else if (ScalarConstants.IsNative(type!.Value) && member.Text is "MinValue" or "MaxValue" or "Size") { type = member.Text == "Size" ? ScalarType.Int : type; method = false; }
                else if (type == ScalarType.String && member.Text == "Length") { type = ScalarType.Int; method = false; }
                else if (IsStruct(type.Value)) (type, method) = NameMember(type.Value, member);
                else Fail("AI3103", $"'{Show(type.Value)}' has no supported member '{member.Text}'.", member.Location);
                name = member.Name;
            }
            if (Current.Text != ")") Fail("AI3106", "nameof accepts one name; calls, indexing, operators and method type arguments are not allowed.");
            Expect(")");
            return Fold(new("literal", ScalarType.String, keyword.Location, CompilationDisplayName(name)));
        }

        private bool NameBuiltin(string name) => Has(ScalarFeatures.Iterators) && name is "IEnumerable" or "IEnumerator"
            || Has(ScalarFeatures.Async) && activeAsync && name is "Task" or "TaskCompletionSource";

        private bool NameMemberExists(ScalarType type, string name) => StructOf(type).Fields.Any(f => f.Name == name && !f.Hidden) || FindStaticField(IndexOf(type), name) is not null
            || FindProperty(IndexOf(type), name) is not null || FindMethod(IndexOf(type), name) is { IsAccessor: false, IsConstructor: false };

        private (ScalarType Type, bool Method) NameMember(ScalarType type, Lexeme member)
        {
            member = member with { Text = member.Name };
            if(IsStruct(type))type=RecordLookupOwner(StructOf(type),member.Text).Id;
            var info = StructOf(type);
            if (IsDelegate(type))
            {
                if (member.Text == "Invoke") return (ScalarType.Unit, true);
                Fail("AI3103", $"The delegate has no supported member '{member.Text}'.", member.Location);
            }
            if (IsTaskType(type))
            {
                if (member.Text is "IsCompleted" or "IsCompletedSuccessfully" or "IsFaulted") return (ScalarType.Bool, false);
                if (member.Text == "GetAwaiter") return (ScalarType.Unit, true);
                if (StructOf(type).Name == "__Task" && member.Text == "FromResult") return (ScalarType.Unit, true);
                if (StructOf(type).Name == "__Task" && member.Text == "CompletedTask") return (type, false);
                Fail("AI3103", $"The task has no supported member '{member.Text}'.", member.Location);
            }
            if (IteratorShape(type) is { } iterator && IsIteratorInterface(info.Name))
            {
                if (!iterator.Sequence && member.Text == "Current") return (iterator.Element, false);
                if (member.Text == "get_Current") Fail("AI3103", "Generated iterator accessor names are not source members.", member.Location);
            }
            if (info.Name.StartsWith("__Grp_", StringComparison.Ordinal) && !(_struct?.Name.StartsWith("__Grp_", StringComparison.Ordinal) ?? false)
                && member.Text is not ("Key" or "GetEnumerator")) Fail("AI3103", "The generated grouping storage is not accessible.", member.Location);
            if (FindProperty(IndexOf(type), member.Text) is { } property)
            {
                NameAccess(_structs[property.Owner], property.IsPublic, member);
                return (property.Type, false);
            }
            if (FindStaticField(IndexOf(type), member.Text) is { } sf)
            { CheckAccess(_structs[sf.Owner], sf.IsPublic, member.Text, member.Location); return (sf.Type, false); }
            if (info.Fields.FirstOrDefault(f => f.Name == member.Text && !f.Hidden) is { } field)
            {
                NameAccess(field.Owner >= 0 ? _structs[field.Owner] : info, field.IsPublic, member);
                return (field.Type, false);
            }
            if (FindMethod(IndexOf(type), member.Text) is { IsAccessor: false, IsConstructor: false } found)
            {
                NameAccess(found.Owner >= 0 ? _structs[found.Owner] : info, found.IsPublic, member);
                return (ScalarType.Unit, true);
            }
            if (info.IsTypeParameter)
            {
                var through = info.Interfaces.Where(i => NameMemberExists(_structs[i].Id, member.Text)).ToList();
                if (through.Count > 1) Fail("AI3106", $"The member '{member.Text}' is ambiguous between the constraints.", member.Location);
                if (through.Count == 1) return NameMember(_structs[through[0]].Id, member);
            }
            Fail("AI3103", $"'{info.Name}' has no accessible member '{member.Text}'.", member.Location);
            return default;
        }

        private void NameAccess(ScalarStruct owner, bool isPublic, Lexeme member)
        {
            // Closed instances and the unbound declaration share private-member accessibility.
            if (!isPublic && _struct is { Template: >= 0 } self && self.Template == (owner.Template >= 0 ? owner.Template : IndexOf(owner.Id))) return;
            CheckAccess(owner, isPublic, member.Text, member.Location);
        }
    }
}
