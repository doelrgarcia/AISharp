using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    // Record lookup supports hiding while preserving required members. Other profiles
    // retain their existing rejection of ambiguous static and instance declarations.
    private static void ValidateStaticHiding()
    {
        foreach (var type in activeStructs!)
        {
            if (type.BaseIndex < 0) continue;
            var own = type.StaticFields.Select(f => (f.Name, f.Location, Static: true))
                .Concat(type.Fields.Where(f => f.Owner == IndexOf(type.Id)).Select(f => (f.Name, f.Location, Static: false)))
                .Concat(type.Properties.Select(p => (p.Name, p.Location, Static: p.IsStatic)))
                .Concat(type.Methods.Where(m => !m.IsConstructor && !m.IsStaticConstructor && !m.IsAccessor).Select(m => (m.Name, m.Declaration.Location, Static: m.IsStatic)));
            foreach (var member in own)
                for (var b = type.BaseIndex; b >= 0; b = activeStructs[b].BaseIndex)
                {
                    var parent = activeStructs[b];
                    var conflict = parent.Properties.Any(p => p.IsStatic && p.Name == member.Name) || parent.StaticFields.Any(f => f.Name == member.Name)
                        || parent.Methods.Any(m => m.Name == member.Name && m.IsStatic)
                        || member.Static && (parent.Fields.Any(f => f.Name == member.Name)
                            || parent.Properties.Any(p => p.Name == member.Name) || parent.Methods.Any(m => m.Name == member.Name));
                    if(RecordMemberHiding(type))
                    {
                        if((parent.Fields.Any(f=>f.Name==member.Name&&f.IsRequired)||parent.Properties.Any(p=>p.Name==member.Name&&p.IsRequired))&&!type.Properties.Any(p=>p.Name==member.Name&&p.IsOverride))
                            throw DeclarationError("AI3106","An inherited required member cannot be hidden.",member.Location);
                    }
                    else if (conflict) throw DeclarationError("AI3100", $"'{type.Name}.{member.Name}' hides an inherited member; static member hiding is not supported yet.", member.Location);
                }
        }
    }

    private static ScalarStructField? FindStaticField(int index, string name, bool allowHidden = false)
    {
        for (var k = index; k >= 0; k = activeStructs![k].BaseIndex)
        {
            if (activeStructs![k].StaticFields.FirstOrDefault(f => f.Name == name && (allowHidden || !f.Hidden)) is { } field) return field;
            if(RecordMemberHiding(activeStructs[k])&&OwnRecordMember(activeStructs[k],name))return null;
        }
        return null;
    }

    private sealed partial class ExtendedBinder
    {
        private bool SameTypeScope(ScalarStruct owner) => _struct is not null
            && (_struct.Id == owner.Id || _struct.Template >= 0 && _struct.Template == owner.Template || CompilationNestedWithin(_struct.Name,owner.Name));

        private static ScalarExpression StaticType(ScalarStruct owner, SourceLocation at) => new("static-type", owner.Id, at);

        // A type qualifier is recognized only in the type namespace, after checking local shadowing.
        private bool StaticQualifiedAt()
        {
            if (Current.Text is "this" or "base") return false;
            if(Has(ScalarFeatures.StaticMembers)&&activeCompilation is not null&&CompilationQualification(_position,true) is {} sourceQualifier)
            {
                var saved=_position;var tokens=_tokens;_tokens=[..tokens];
                try{var written=TypeToken();if(Current.Text=="."&&IsStruct(Type(written.Text,written.Location,allowStatic:true)))return true;}
                finally{_position=saved;_tokens=tokens;}
            }

            if(ImportAlias(Current.Text) is not null&&IsLocalName(Current.Text))return false;
            if(Has(ScalarFeatures.StaticMembers)&&QualifiedBuiltinAt(_position,expression:true) is {Alias:NativeEqualityComparerName or "Type"} qualified)
            {
                var saved=_position;var tokens=_tokens;_tokens=[..tokens];
                try {TypeToken();return Current.Text==".";}finally{_position=saved;_tokens=tokens;}
            }
            if (!Has(ScalarFeatures.StaticMembers) || StructNamedAny(Current.Text) is null && !BuiltinComparerTypeName(Current.Text) && _typeArgs?.ContainsKey(Current.Text) != true) return false;
            var position = _position + 1;
            if(TokenAt(position).Text=="."&&ValueShadowsStaticReceiver())return false;
            if (TokenAt(position).Text == "<")
            {
                var depth = 0;
                do
                {
                    var token = TokenAt(position++).Text;
                    if (token == "<") depth++;
                    else if (token is ">" or ">>" or ">>>") depth -= token.Length;
                    else if (token is "" or "\n" or "{") return false;
                } while (depth > 0);
                if (depth != 0) return false;
            }
            return TokenAt(position).Text == ".";
        }

        private bool ValueShadowsStaticReceiver()
        {
            ScalarType? value=null;
            if(LocalNamed(Current.Text) is {} local)value=local.Type;
            else if(_struct is not null&&NameMemberExists(_struct.Id,Current.Text))
            {
                var member=NameMember(_struct.Id,Current);if(member.Method)return true;value=member.Type;
            }
            if(value is null)return IsLocalName(Current.Text);
            var named=StructNamedAny(Current.Text);if(named is null||value.Value!=named.Id)return true;
            var name=TokenAt(_position+2).Text;var index=IndexOf(named.Id);
            return FindStaticField(index,name) is null&&FindProperty(index,name) is not {IsStatic:true}&&FindMethod(index,name) is not {IsStatic:true};
        }

        private ScalarExpression StaticQualified()
        {
            var written = TypeToken(); var type = Type(written.Text, written.Location, allowStatic: true);
            Expect("."); var member = MemberToken(); var receiver = StaticType(StructOf(type), written.Location) with { Value = ScopedTypeArgument(written.Text,out _) ? IdentifierName(written.Text) : null };
            if(receiver.Value is null){receiver=RecordMemberReceiver(receiver,member);type=receiver.Type;}
            if (receiver.Value is not null && ConstrainedStaticProperty(receiver, member) is { } contractProperty)
            {
                var read = PropertyRead(receiver, contractProperty, member);
                return Delegating && IsDelegate(read.Type) && Current.Text == "(" ? Invoke(read,member.Location) : read;
            }
            if (Current.Text == "(" || Current.Text == "<" && !MethodGroupAhead() && (receiver.Value is not null || FindMethod(IndexOf(type), member.Text) is not null))
            {
                if (receiver.Value is null && Delegating && (FindMethod(IndexOf(type), member.Text) is null||RecordDelegateHidesMethod(type,member.Text))
                    && (FindStaticField(IndexOf(type), member.Text) is { } field && IsDelegate(field.Type)
                        || FindProperty(IndexOf(type), member.Text) is { IsStatic: true } property && IsDelegate(property.Type)))
                    return Invoke(StaticFieldRead(StructOf(type), member), member.Location);
                return MethodCall(receiver, member);
            }
            if (Delegating && (receiver.Value is not null || FindMethod(IndexOf(type), member.Text) is { IsStatic: true }))
                return MethodGroupExpression(member, receiver);
            if (receiver.Value is not null)
                Fail("AI3106", "A static type-parameter access requires a static abstract or virtual interface method contract.", member.Location);
            return StaticFieldRead(StructOf(type), member);
        }

        private ScalarExpression ImplicitMemberReceiver(Lexeme member)
        {
            if (FindMethod(IndexOf(_struct!.Id), member.Text) is { IsStatic: true } || FindProperty(IndexOf(_struct.Id), member.Text) is { IsStatic: true }) return StaticType(_struct, member.Location);
            if (_this is null) Fail("AI3103", $"Instance member '{member.Text}' is unavailable in a static method.", member.Location);
            return new("local", _this!.Type, member.Location, _this.EmittedName);
        }

        private ScalarExpression StaticFieldRead(ScalarStruct type, Lexeme member, bool allowHidden = false)
        {
            member = member with { Text = member.Name };
            if(!allowHidden)type=RecordLookupOwner(type,member.Text);
            if (!allowHidden && FindProperty(IndexOf(type.Id), member.Text) is { } property)
            {
                if (!property.IsStatic) Fail("AI3106", "An instance property requires an object receiver.", member.Location);
                if (property.IsEvent) return EventAccessorCall(StaticType(type, member.Location), property, member);
                return PropertyRead(StaticType(type, member.Location), property, member);
            }
            var field = FindStaticField(IndexOf(type.Id), member.Text, allowHidden);
            if (field is null) { Fail("AI3103", $"'{type.Name}' has no static field '{member.Text}'.", member.Location); return null!; }
            if (!allowHidden) CheckAccess(_structs[field.Owner], field.IsPublic, member.Text, member.Location);
            if (field.IsEvent && !SameTypeScope(_structs[field.Owner]) && Current.Text is not ("+=" or "-="))
                Fail("AI3106", $"The event '{_structs[field.Owner].Name}.{field.Name}' can only appear on the left hand side of += or -= outside its declaring type.", member.Location);
            var index = _structs[field.Owner].StaticFields.ToList().FindIndex(f => f.Name == field.Name);
            return new("static-field", field.Type, member.Location, field.Owner.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture));
        }

        private void WriteStatic(ScalarExpression expression)
        {
            var parts = expression.Value!.Split(':');
            var owner = _structs[int.Parse(parts[0], CultureInfo.InvariantCulture)];
            var field = owner.StaticFields[int.Parse(parts[1], CultureInfo.InvariantCulture)];
            if (field.WritePrivate && !SameTypeScope(owner)) Fail("AI3119", $"The set accessor of '{owner.Name}.{field.Name}' is inaccessible.", expression.Location);
            // Readonly static storage is writable only in its declaring type constructor/initializer, outside lambdas.
            if (field.IsReadonly && !(_cctor && _lambdaNest == 0 && SameTypeScope(owner))) Fail("AI3105", $"Readonly static field '{owner.Name}.{field.Name}' cannot be assigned outside its static constructor.", expression.Location);
        }
    }
}
