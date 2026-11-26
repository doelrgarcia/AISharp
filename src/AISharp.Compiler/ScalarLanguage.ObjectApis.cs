using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private const string ObjectEqualsTarget="__native.$object_equals";
    private const string ObjectInstanceEqualsTarget="__native.$object_instance_equals";
    private const string ObjectInstanceDisplayTarget="__native.$object_instance_display";
    private const string ObjectInstanceHashTarget="__native.$object_instance_hash";
    private static bool ObjectEqualsSignature(ScalarStructMethod method)=>method.Name=="Equals"&&method.Declaration.ReturnType=="bool"
        &&method.Declaration.Parameters.Count==1&&method.Declaration.Parameters[0].Type.Trim()=="object"&&ExtendedBinder.GenericOf(method.Declaration) is null;
    private static bool EmitObjectApis(List<ScalarFunction> members,ScalarFeatures features,IReadOnlyList<FunctionDeclaration> functions,IReadOnlyList<ScalarFunction> topLevel)
    {
        if((features&ScalarFeatures.Boxing)==0)return false;
        var calls=new HashSet<string>(StringComparer.Ordinal);
        void Value(ScalarExpression? value){if(value is null)return;if(value.Operation=="call"&&value.Value is {} target)calls.Add(target);Value(value.Left);Value(value.Right);if(value.Arguments is not null)foreach(var argument in value.Arguments)Value(argument);}
        void Statements(IReadOnlyList<ScalarStatement> statements){foreach(var s in statements){Value(s.Expression);Value(s.Increment);if(s.Initializer is not null)Statements([s.Initializer]);if(s.Body is not null)Statements(s.Body);if(s.Alternative is not null)Statements(s.Alternative);}}
        foreach(var f in topLevel.Concat(members).Concat(liftedFunctions!)){Statements(f.Body);foreach(var contract in f.Contracts)Value(contract.Expression);}
        var targets=new[]{ObjectEqualsTarget,ObjectInstanceEqualsTarget,ObjectInstanceDisplayTarget,ObjectInstanceHashTarget};
        if(!targets.Any(calls.Contains)||!requestedInstances!.Add("object-apis:"+activeStructs!.Count.ToString(CultureInfo.InvariantCulture)+":"+string.Join(",",targets.Where(calls.Contains))))return false;
        var at=new SourceLocation(1,1,0);var binder=new ExtendedBinder(new FunctionDeclaration("__object_api",[],"bool",[],[],new FunctionBody([]){ScalarSource=new("",at)},[],at),features,functions);
        foreach(var generated in binder.ObjectApiFunctions(calls))
        {var index=members.FindIndex(f=>f.Name==generated.Name);if(index<0)members.Add(generated);else members[index]=generated;}
        return true;
    }
    private sealed partial class ExtendedBinder
    {
        private ScalarExpression AsObject(ScalarExpression value)=>IsObject(value.Type)?value:Coerce(value,(ScalarType)ObjectTypeId);
        private ScalarExpression ObjectEquals(ScalarExpression left,ScalarExpression right,SourceLocation at,bool instance=false)
            =>new("call",ScalarType.Bool,at,instance?ObjectInstanceEqualsTarget:ObjectEqualsTarget){Arguments=[AsObject(left),AsObject(right)]};
        private bool ObjectStaticAt()=>Has(ScalarFeatures.Boxing)&&(ImportValueType(Current.Text,Current.Location)==(ScalarType)ObjectTypeId&&TokenAt(_position+1).Text=="." || Current.Text=="object"&&TokenAt(_position+1).Text=="." || QualifiedBuiltinAt(_position,expression:true) is {Alias:"object"} q&&TokenAt(q.End).Text==".");
        private ScalarExpression ObjectStaticCall()
        {
            TypeHeadToken();Expect(".");var member=Next();member=member with {Text=member.Name};
            if(member.Text is not ("Equals" or "ReferenceEquals"))Fail("AI3103","Object has no supported static member with this name.",member.Location);
            var arguments=ObjectArguments(member,2);
            return member.Text=="Equals"?ObjectEquals(arguments[0],arguments[1],member.Location):new("==",ScalarType.Bool,member.Location,Left:arguments[0],Right:arguments[1]);
        }
        private List<ScalarExpression> ObjectArguments(Lexeme member,int count,int alternate=-1,ScalarType? typedReceiver=null)
        {
            Expect("(");var arguments=new List<ScalarExpression>();if(!Take(")")){do{arguments.Add(CallArgument());}while(Take(","));Expect(")");}
            if(arguments.Count!=count&&arguments.Count!=alternate)Fail("AI3116",$"Object {member.Text} expects {count}"+(alternate<0?"":" or "+alternate)+" argument(s).",member.Location);
            if(typedReceiver is {} typed&&arguments.Count==1&&TryCoerce(arguments[0],typed) is not null)
            {arguments[0]=BindArgument(arguments[0],typed,ScalarParameterMode.Value);CompleteArguments(arguments);return arguments;}
            for(var i=0;i<arguments.Count;i++)arguments[i]=BindArgument(arguments[i],(ScalarType)ObjectTypeId,ScalarParameterMode.Value);
            CompleteArguments(arguments);return arguments;
        }
        private bool NativeObjectMemberAt(ScalarExpression receiver)=>Has(ScalarFeatures.Boxing)&&Current.Text=="."&&TokenAt(_position+1).Name is "Equals" or "ToString" or "GetHashCode"
            &&TokenAt(_position+2).Text=="("&&(IsReference(receiver.Type)||receiver.Type==ScalarType.String||Boxable(receiver.Type)||_checkOnly&&IsTypeParameterType(receiver.Type))
            &&(!IsStruct(receiver.Type)||!NameMemberExists(receiver.Type,TokenAt(_position+1).Name));
        private ScalarExpression NativeObjectMember(ScalarExpression receiver)
        {
            Next();var member=Next();member=member with {Text=member.Name};return BindNativeObjectMember(receiver,member);
        }
        private bool ImplicitNativeObjectAt()=>Has(ScalarFeatures.Boxing)&&_struct is not null&&Current.Text is "Equals" or "ReferenceEquals" or "ToString" or "GetHashCode"
            &&TokenAt(_position+1).Text=="("&&!IsLocalName(Current.Text)&&!NameMemberExists(_struct.Id,Current.Text)&&!_functions.Any(f=>f.Name==Current.Text)&&ScopedCompilationFunction(Current.Text,Current.Location) is null;
        private ScalarExpression ImplicitNativeObject()
        {
            var member=Next();member=member with {Text=member.Name};var receiver=_this is null?null:new ScalarExpression("local",_struct!.Id,member.Location,_this.EmittedName);
            return BindNativeObjectMember(receiver,member,true);
        }
        private ScalarExpression BindNativeObjectMember(ScalarExpression? receiver,Lexeme member,bool inherited=false)
        {
            member=member with {Text=member.Name};
            if(receiver is not null)ReadNavigationReceiver(receiver);
            var typed=member.Text=="Equals"&&receiver is not null&&receiver.Type!=ScalarType.String&&BuiltinEquatablePayload(receiver.Type)?receiver.Type:(ScalarType?)null;
            var arguments=ObjectArguments(member,member.Text=="ReferenceEquals"?2:member.Text=="Equals"?1:0,inherited&&member.Text=="Equals"?2:-1,typed);
            if(typed is {} type&&arguments.Count==1&&arguments[0].Type==type)return ObjectValueEqual(receiver!,arguments[0],member.Location);
            if(inherited&&arguments.Count==2)return member.Text=="ReferenceEquals"?new("==",ScalarType.Bool,member.Location,Left:arguments[0],Right:arguments[1]):ObjectEquals(arguments[0],arguments[1],member.Location);
            if(receiver is null){Fail("AI3103","An instance object member requires an instance receiver.",member.Location);return null!;}
            if(member.Text=="Equals")return ObjectEquals(receiver,arguments[0],member.Location,true);
            return new("call",member.Text=="ToString"?ScalarType.String:ScalarType.Int,member.Location,member.Text=="ToString"?ObjectInstanceDisplayTarget:ObjectInstanceHashTarget){Arguments=[AsObject(receiver)]};
        }
        private ScalarExpression BoxPayload(ScalarStruct box,ScalarExpression value,SourceLocation at)
            =>new("field",box.BoxedType!.Value,at,"0",Left:new("downcast",box.Id,at,Left:value));
        private ScalarStructMethod? ObjectOverride(ScalarStruct owner,Func<ScalarStructMethod,bool> signature)
        {
            for(var k=IndexOf(owner.Id);k>=0;k=_structs[k].BaseIndex)
                if(_structs[k].Methods.FirstOrDefault(m=>signature(m)&&m.IsPublic&&!m.IsInternal&&!m.IsStatic&&(m.IsOverride||_structs[k].IsRecord)) is {} method)return method;
            return null;
        }
        private ScalarExpression ReferenceValueEqual(ScalarExpression left,ScalarExpression right,SourceLocation at,bool invokeOnNullRight=false,ScalarExpression? comparison=null)
        {
            var leftNull=new ScalarExpression("==",ScalarType.Bool,at,Left:left,Right:new("null",left.Type,at));
            var rightNull=new ScalarExpression("==",ScalarType.Bool,at,Left:right,Right:new("null",right.Type,at));
            var nonNullLeft=new ScalarExpression("unary!",ScalarType.Bool,at,Left:leftNull);
            var canInvoke=invokeOnNullRight?nonNullLeft:And(nonNullLeft,new("unary!",ScalarType.Bool,at,Left:rightNull),at);
            return new("||",ScalarType.Bool,at,Left:And(leftNull,rightNull,at),Right:And(canInvoke,comparison??ObjectEquals(left,right,at,true),at));
        }
        private ScalarExpression ObjectValueEqual(ScalarExpression left,ScalarExpression right,SourceLocation at)
        {
            if(EquatableValueEqual(left,right,at) is {} typed)return typed;
            if(left.Type==ScalarType.Bool)return Fold(new("==",ScalarType.Bool,at,Left:left,Right:right));
            if(IsDelegate(left.Type))return DelegateBinary("==",left,right,at);
            if(IsObject(left.Type)||IsClass(left.Type))return ReferenceValueEqual(left,right,at);
            if(IsStruct(left.Type))return StructOf(left.Type).IsRecord?RecordEquality("==",left,right,at,comparer:true):ObjectEquals(left,right,at);
            if(IsFloat(left.Type))return new("||",ScalarType.Bool,at,Left:MakeBinary("==",left,right,at),Right:And(MakeBinary("!=",left,left,at),MakeBinary("!=",right,right,at),at));
            return MakeBinary("==",left,right,at);
        }
        public IReadOnlyList<ScalarFunction> ObjectApiFunctions(HashSet<string> calls)
        {
            var at=_function.Location;var type=(ScalarType)ObjectTypeId;var a=new ScalarExpression("local",type,at,"__left");var b=new ScalarExpression("local",type,at,"__right");
            ScalarExpression Null(ScalarExpression x)=>new("==",ScalarType.Bool,at,Left:x,Right:new("null",type,at));
            ScalarStatement Return(ScalarExpression x)=>new("return",at,x);
            ScalarStatement Guard()=>new("if",at,Null(a),Body:[new("throw",at,Name:"System.NullReferenceException")]);
            var generated=new List<ScalarFunction>();
            if(calls.Contains(ObjectEqualsTarget))generated.Add(new(ObjectEqualsTarget,[new("__left",type),new("__right",type)],ScalarType.Bool,
                [new("if",at,new("==",ScalarType.Bool,at,Left:a,Right:b),Body:[Return(new("literal",ScalarType.Bool,at,"true"))]),
                 new("if",at,new("||",ScalarType.Bool,at,Left:Null(a),Right:Null(b)),Body:[Return(new("literal",ScalarType.Bool,at,"false"))]),Return(ObjectEquals(a,b,at,true))],[TrueContract(at)],at));
            if(calls.Contains(ObjectInstanceEqualsTarget)||calls.Contains(ObjectEqualsTarget))
            {
                var body=new List<ScalarStatement>{Guard()};
                foreach(var owner in _structs.Where(s=>s.IsClass&&!s.IsInterface&&!s.IsAbstract&&!s.IsTypeParameter&&!s.IsCheckInstance).ToArray())
                {
                    ScalarExpression? equal=null;
                    if(owner.IsStringObject)equal=And(new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:b),MakeBinary("==",StringObjectPayload(owner,a,at),StringObjectPayload(owner,b,at),at),at);
                    else if(owner.BoxedType is {} payload)
                    {
                        var left=BoxPayload(owner,a,at);
                        var original=IsStruct(payload)?StructOf(payload):null;
                        var method=original is null?null:ObjectOverride(original,ObjectEqualsSignature);
                        if(method is not null)equal=InterfaceImplementationCall(method,left,[b],ScalarType.Bool,at);
                        else
                        {
                            var right=UnboxValue(b,payload,at);
                            var values=original is null?ObjectValueEqual(left,right,at):original.IsRecord?RecordEquality("==",left,right,at,comparer:true):FieldsEqual(original,left,right,at,valueTypeFields:true);
                            equal=And(new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:b),values,at);
                        }
                    }
                    else if(IsDelegate(owner.Id))equal=And(new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:b),DelegateBinary("==",new("downcast",owner.Id,at,Left:a),new("downcast",owner.Id,at,Left:b),at),at);
                    else if(ObjectOverride(owner,ObjectEqualsSignature) is {} method)
                        equal=InterfaceImplementationCall(method,new("downcast",_structs[method.Owner].Id,at,Left:a),[b],ScalarType.Bool,at);
                    if(equal is not null)body.Add(new("if",at,new("isexact",ScalarType.Bool,at,((int)owner.Id).ToString(CultureInfo.InvariantCulture),Left:a),Body:[Return(equal)]));
                }
                body.Add(Return(new("==",ScalarType.Bool,at,Left:a,Right:b)));
                generated.Add(new(ObjectInstanceEqualsTarget,[new("__left",type),new("__right",type)],ScalarType.Bool,body,[TrueContract(at)],at));
            }
            if(calls.Contains(ObjectInstanceDisplayTarget))generated.Add(new(ObjectInstanceDisplayTarget,[new("__left",type)],ScalarType.String,[Guard(),Return(DisplayObject(a,at))],[TrueContract(at)],at));
            if(calls.Contains(ObjectInstanceHashTarget))generated.Add(new(ObjectInstanceHashTarget,[new("__left",type)],ScalarType.Int,[Guard(),Return(HashObject(a,at))],[TrueContract(at)],at));
            return generated.Select(Erase).ToArray();
        }
    }
}
