using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private const string DisplayBuilderName="StringBuilder";
    private const string DisplayBuilderField="__record_display_text";
    private const string DisplayBuilderSource="\nsealed class StringBuilder { private __record_display_text: string\npublic fn ToString() -> string\n ensures true\n{ return __record_display_text } }\n";
    private static bool DisplayEnabled=>(activeFeatures&ScalarFeatures.RecordDisplay)!=0;
    private static bool RecordDisplayRequested(string source)=>new Lexer(source).Lex().Any(token=>token.Kind==TokenKind.Identifier&&IdentifierName(token.Text) is "ToString" or "PrintMembers" or DisplayBuilderName);
    private static bool RecordTextSignature(ScalarStructMethod method)=>method.Name=="ToString"&&method.Declaration.Parameters.Count==0&&ExtendedBinder.GenericOf(method.Declaration) is null;
    private static bool RecordPrintSignature(ScalarStructMethod method)=>method.Name=="PrintMembers"&&method.Declaration.Parameters.Count==1&&method.Declaration.Parameters[0].Type==DisplayBuilderName&&ExtendedBinder.GenericOf(method.Declaration) is null;
    private static ScalarStructMethod? BaseRecordText(int index)
    {
        for(var k=index;k>=0;k=activeStructs![k].BaseIndex)if(activeStructs![k].Methods.FirstOrDefault(RecordTextSignature) is {} method)return method;
        return null;
    }
    private static IReadOnlyList<(string Name,SourceLocation At)> PrintableRecordMembers(ScalarStruct owner)
    {
        var index=IndexOf(owner.Id);var properties=owner.Properties.Where(p=>p.Owner==index&&!p.IsInternal&&!p.IsStatic&&!p.IsIndexer&&!p.IsEvent&&!p.IsOverride&&p.IsPublic&&p.HasGet).ToArray();
        return owner.Fields.Where(f=>f.Owner==index&&!f.IsInternal&&!f.Hidden&&!f.IsEvent&&f.IsPublic&&!owner.Properties.Any(p=>p.Owner==index&&p.Name==f.Name)).Select(f=>(f.Name,At:f.Location))
            .Concat(properties.Select(p=>(p.Name,At:p.Location))).OrderBy(p=>p.At.Offset).ToArray();
    }
    private static void EnsureRecordDisplayApis()
    {
        if(!DisplayEnabled||!activeStructs!.Any(s=>s.Name==DisplayBuilderName))return;
        int Depth(ScalarStruct owner){var n=0;for(var k=owner.BaseIndex;k>=0;k=activeStructs![k].BaseIndex)n++;return n;}
        foreach(var original in activeStructs!.Where(s=>s.IsRecord&&!s.IsTypeParameter&&(s.Template<0||s.Template==IndexOf(s.Id))).OrderBy(Depth).ToArray())
        {
            var index=IndexOf(original.Id);var owner=activeStructs![index];var methods=owner.Methods.ToList();var at=owner.Location;
            var printed=PrintableRecordMembers(owner);var print=methods.FirstOrDefault(RecordPrintSignature);
            var text=methods.FirstOrDefault(RecordTextSignature);
            var signatures=new HashSet<string>(StringComparer.Ordinal);
            foreach(var method in methods.Where(m=>m.Name is "ToString" or "PrintMembers"))
                if(!signatures.Add(method.Name+"|"+DeconstructSignature(owner,method)))throw DeclarationError("AI3106","Duplicate record display parameter signature.",method.Declaration.Location);
            foreach(var name in new[]{"ToString","PrintMembers"})
                if(owner.Fields.Any(f=>f.Owner==index&&!f.Hidden&&f.Name==name)||owner.Properties.Any(p=>p.Owner==index&&p.Name==name))throw DeclarationError("AI3106","A record display method cannot be replaced by a field or property.",owner.Fields.FirstOrDefault(f=>f.Owner==index&&f.Name==name)?.Location??owner.Properties.First(p=>p.Owner==index&&p.Name==name).Location);
            if(print is not null)
            {
                var signature=print.Declaration;
                var valid=signature.ReturnType=="bool"&&signature.Parameters.Count==1&&signature.Parameters[0].Type==DisplayBuilderName&&ExtendedBinder.GenericOf(signature) is null&&!print.IsStatic;
                valid&=owner.IsClass&&!owner.IsSealed?print.IsProtectedMember&&(owner.BaseIndex>=0?print.IsOverride:print.IsVirtual||print.IsAbstract):owner.IsClass&&owner.BaseIndex>=0?print.IsProtectedMember&&print.IsOverride:!print.IsPublic&&!print.IsProtectedMember&&!print.IsVirtual&&!print.IsOverride&&!print.IsSealed;
                if(owner.IsClass&&!owner.IsSealed)valid&=!print.IsSealed;
                if(!valid)throw DeclarationError("AI3106","Record PrintMembers requires the compiler-defined bool(StringBuilder) signature, access, and virtual modifiers.",signature.Location);
            }
            else
            {
                var body=new System.Text.StringBuilder("var __printed=false\n");
                if(owner.IsClass&&owner.BaseIndex>=0)body.Append("__printed=base.PrintMembers(__builder)\n");
                foreach(var member in printed)
                {
                    body.Append("if (__printed) { __builder.Append(\", \") }\n__builder.Append(").Append(System.Text.Json.JsonSerializer.Serialize(member.Name+" = ")).Append(")\n__builder.Append(this.").Append(member.Name).Append(")\n__printed=true\n");
                }
                body.Append("return __printed");
                var declaration=new FunctionDeclaration("PrintMembers",[new("__builder",DisplayBuilderName)],"bool",[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new(body.ToString(),at)},[],at);
                var isReadonly=!owner.IsClass&&printed.All(p=>owner.Properties.FirstOrDefault(property=>property.Owner==index&&property.Name==p.Name) is not {Getter:{ } getter}||methods.First(m=>m.QualifiedName==getter).IsReadonly);
                print=new("PrintMembers",owner.Name+".$record_display_members",false,false,declaration){Owner=index,RecordDisplayApi="members",IsProtectedMember=owner.IsClass&&(!owner.IsSealed||owner.BaseIndex>=0),IsVirtual=owner.IsClass&&!owner.IsSealed&&owner.BaseIndex<0,IsOverride=owner.IsClass&&owner.BaseIndex>=0,IsReadonly=isReadonly,ReceiverMode=owner.IsClass?ScalarParameterMode.Value:isReadonly?ScalarParameterMode.In:ScalarParameterMode.Ref};methods.Add(print);
            }
            if(text is not null)
            {
                if(text.IsStatic||!text.IsPublic||text.IsInternal||text.Declaration.ReturnType!="string"||text.Declaration.Parameters.Count!=0||ExtendedBinder.GenericOf(text.Declaration) is not null||!text.IsOverride||text.IsVirtual||text.IsAbstract&&!owner.IsAbstract)
                    throw DeclarationError("AI3106","Record ToString requires a public instance override returning string with no parameters.",text.Declaration.Location);
            }
            else if(owner.BaseIndex<0||BaseRecordText(owner.BaseIndex) is not {IsSealed:true})
            {
                var name=owner.Name.Split('<')[0];var body="let __builder=new StringBuilder()\n__builder.Append("+System.Text.Json.JsonSerializer.Serialize(name+" { ")+")\nif (this.PrintMembers(__builder)) { __builder.Append(\" \") }\n__builder.Append(\"}\")\nreturn __builder.ToString()";
                var declaration=new FunctionDeclaration("ToString",[],"string",[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new(body,at)},[],at);
                methods.Add(new("ToString",owner.Name+".$record_display_text",true,false,declaration){Owner=index,RecordDisplayApi="text",IsVirtual=owner.IsClass&&owner.BaseIndex<0&&!owner.IsSealed,IsOverride=owner.IsClass&&owner.BaseIndex>=0,IsReadonly=print.IsReadonly,ReceiverMode=owner.IsClass?ScalarParameterMode.Value:print.IsReadonly?ScalarParameterMode.In:ScalarParameterMode.Ref});
            }
            activeStructs[index]=owner with{Methods=methods};
        }
    }
    private sealed partial class ExtendedBinder
    {
        private void CheckDisplayMethodAccess(ScalarExpression receiver,ScalarStruct declaring,ScalarStructMethod method,Lexeme member,bool direct)
        {
            if(method.IsProtectedMember&&_struct is not null&&Derives(_structs,IndexOf(_struct.Id),IndexOf(declaring.Id)))
            {
                if(!direct&&!Derives(_structs,IndexOf(receiver.Type),IndexOf(_struct.Id)))Fail("AI3119","A protected member requires this, base, or a receiver of the derived declaring type.",member.Location);
                return;
            }
            CheckAccess(declaring,method.IsPublic,member.Text,member.Location);
        }
        private bool IsDisplayBuilder(ScalarStruct info)=>Has(ScalarFeatures.RecordDisplay)&&info.Name==DisplayBuilderName&&info.Fields.Any(f=>f.Name==DisplayBuilderField);
        private ScalarExpression DisplayValue(ScalarExpression value,SourceLocation at)
        {
            if(IsNull(value.Type))return new("literal",ScalarType.String,at,"");
            if(value.Type==ScalarType.String)return value;
            if(value.Type is ScalarType.Bool or ScalarType.Char or ScalarType.Decimal||ScalarConstants.IsInteger(value.Type)||ScalarConstants.IsFloat(value.Type))return new("display-value",ScalarType.String,at,Left:value);
            if(IsEnum(value.Type))return DisplayEnum(value,at);
            if(IsObject(value.Type)||IsClass(value.Type))return DisplayObject(value,at);
            if(IsStruct(value.Type))
            {
                if(StructOf(value.Type).IsTypeParameter&&_checkOnly)return new("literal",ScalarType.String,at,"");
                if(!StructOf(value.Type).IsTypeParameter)return FindMethod(IndexOf(value.Type),"ToString") is null?new("literal",ScalarType.String,at,RuntimeDisplayType(value.Type)):MethodCall(value,new("ToString",at),given:[]);
            }
            Fail("AI3100",$"Display of '{Show(value.Type)}' requires its native formatting implementation.",at);return null!;
        }
        private ScalarExpression NewDisplayBuilder(ScalarStruct info,SourceLocation at)
        {
            var arguments=new List<ScalarExpression>();Expect("(");if(!Take(")")){arguments.Add(Expression());Expect(")");}
            if(arguments.Count>1||arguments.Count==1&&arguments[0].Type!=ScalarType.String&&!IsNull(arguments[0].Type))Fail("AI3116","A native StringBuilder takes zero arguments or one string.",at);
            var text=arguments.Count==0?new ScalarExpression("literal",ScalarType.String,at,""):Coerce(arguments[0],ScalarType.String);
            var empty=new ScalarExpression("literal",ScalarType.String,at,"");
            var initial=new ScalarExpression("+",ScalarType.String,at,Left:empty,Right:text);
            var index=info.Fields.ToList().FindIndex(f=>f.Name==DisplayBuilderField);
            return new("init",info.Id,at,index.ToString(CultureInfo.InvariantCulture),Left:new("new",info.Id,at)){Arguments=[initial]};
        }
        private ScalarExpression AppendDisplayBuilder(ScalarExpression receiver,ScalarExpression argument,SourceLocation at)
        {
            if(IsNull(argument.Type))argument=Coerce(argument,ScalarType.String);
            var owner=StructOf(receiver.Type);var name=owner.Name+".$append_"+((int)argument.Type).ToString(CultureInfo.InvariantCulture);
            if(requestedInstances!.Add(name))
            {
                var index=IndexOf(owner.Id);var declaration=new FunctionDeclaration("Append",[new("value",Show(argument.Type))],DisplayBuilderName,[],[new(ContractKind.Ensures,"true",at)],new FunctionBody([]){ScalarSource=new("",at)},[],at);
                var metadata=new ScalarStructMethod("Append",name,true,false,declaration){Owner=index,IsLifted=true};var current=_structs[index];_structs[index]=current with{Methods=[..current.Methods,metadata]};
                var self=new ScalarExpression("local",owner.Id,at,"__builder");var item=new ScalarExpression("local",argument.Type,at,"__item");
                var field=owner.Fields.ToList().FindIndex(f=>f.Name==DisplayBuilderField);var target=new ScalarExpression("field",ScalarType.String,at,field.ToString(CultureInfo.InvariantCulture),Left:self);
                ScalarExpression formatted;
                // Append receives a value copy. Bind its parameter as writable storage so
                // a mutable struct ToString updates that copy, preserving the caller's value.
                _scopes.Add(new(StringComparer.Ordinal){["__item"]=new("__item",argument.Type,true)});_assigned.Add("__item");
                try{formatted=DisplayValue(item,at);}finally{_scopes.RemoveAt(_scopes.Count-1);_assigned.Remove("__item");}
                var value=new ScalarExpression("+",ScalarType.String,at,Left:target,Right:formatted);
                ScalarStatement append=new("expression",at,new("=",ScalarType.String,at,Left:target,Right:value));
                if(IsClass(argument.Type))append=new("if",at,new("!=",ScalarType.Bool,at,Left:item,Right:new("null",argument.Type,at)),Body:[append]);
                Lift(new ScalarFunction(name,[new("__builder",owner.Id),new("__item",argument.Type)],owner.Id,[append,new("return",at,self)],[TrueContract(at)],at));
            }
            return new("call",owner.Id,at,name){Arguments=[receiver,argument]};
        }
        private ScalarExpression DisplayBuilderMember(ScalarExpression receiver,Lexeme member)
        {
            if(member.Text!="Append")return Current.Text=="("?MethodCall(receiver,member):FieldRead(receiver,member);
            Expect("(");var argument=Expression();Read(argument);Expect(")");return AppendDisplayBuilder(receiver,argument,member.Location);
        }
        private ScalarExpression DisplayEnum(ScalarExpression value,SourceLocation at)
        {
            var info=EnumOf(value.Type);var name=DisplayBuilderName+".$enum_"+((int)value.Type).ToString(CultureInfo.InvariantCulture);
            if(requestedInstances!.Add(name))
            {
                var argument=new ScalarExpression("local",info.Id,at,"__enum");var statements=new List<ScalarStatement>();
                foreach(var member in info.Members)
                    statements.Add(new("if",at,new("==",ScalarType.Bool,at,Left:argument,Right:new("literal",info.Id,at,member.Value.ToString(CultureInfo.InvariantCulture))),Body:[new("return",at,new("literal",ScalarType.String,at,member.Key))]));
                statements.Add(new("return",at,DisplayValue(new("cast",info.Underlying,at,Left:argument),at)));
                Lift(new ScalarFunction(name,[new("__enum",info.Id)],ScalarType.String,statements,[TrueContract(at)],at));
            }
            return new("call",ScalarType.String,at,name){Arguments=[value]};
        }
    }
}
