namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed partial class ExtendedBinder
    {
        private string? CompilationTypeText(string text,SourceLocation at)
        {
            if(activeCompilation is null||text.StartsWith("__unit_",StringComparison.Ordinal)||ReservedTypeParameterNames.Contains(text))return null;
            if(CompilationInheritedTypeText(text,at) is {} inherited)return inherited;
            var normalized=activeCompilation.TypeNormalizer?.Invoke(text,at.Offset);return string.IsNullOrEmpty(normalized)||normalized==text?null:normalized;
        }
        private ScalarStruct? CompilationStructure(string text)
        {
            if(activeCompilation is null||ScopedTypeArgument(text,out _))return null;var normalized=CompilationTypeText(text,Current.Location);if(normalized is null)return null;
            var type=Type(normalized,Current.Location,allowStatic:true);return IsStruct(type)?StructOf(type):null;
        }
        private ScalarEnum? CompilationEnumeration(string text)
        {
            if(activeCompilation is null||ScopedTypeArgument(text,out _))return null;var normalized=CompilationTypeText(text,Current.Location);if(normalized is null)return null;
            var type=Type(normalized,Current.Location);return IsEnum(type)?EnumOf(type):null;
        }
        private bool CompilationValueShadows(int position)
        {
            var first=TokenAt(position);var forced=first.Text=="global"&&TokenAt(position+1).Text is ":" or "::"||TokenAt(position+1).Text is ":" or "::";
            if(forced)return false;
            if(ScopedTypeArgument(first.Text,out _)||ScopedCompilationFunction(first.Text,first.Location) is not null)return true;
            if(_struct is not null&&NameMemberExists(_struct.Id,first.Name)&&!CompilationAccessibleMember(first.Name))return false;
            var saved=_position;_position=position;
            try{return ValueShadowsStaticReceiver();}finally{_position=saved;}
        }
        private bool CompilationAccessibleMember(string name)
        {
            if(_struct is null)return false;var index=IndexOf(_struct.Id);
            bool Access(int owner,bool visible,bool assembly,bool inherited=false)
            {var info=owner>=0?_structs[owner]:_struct;return visible||assembly||SameTypeScope(info)||inherited&&Derives(_structs,index,IndexOf(info.Id));}
            if(FindProperty(index,name) is {} property)return Access(property.Owner,property.IsPublic,property.IsInternal,property.IsProtectedMember);
            if(FindStaticField(index,name) is {} field)return Access(field.Owner,field.IsPublic,field.IsInternal);
            if(_struct.Fields.FirstOrDefault(f=>f.Name==name&&!f.Hidden) is {} value)return Access(value.Owner,value.IsPublic,value.IsInternal);
            if(FindMethod(index,name) is {} method)return Access(method.Owner,method.IsPublic,method.IsInternal,method.IsProtectedMember);
            return false;
        }
        private IEnumerable<(string Path,int End)> CompilationPaths(int position)
        {
            var first=TokenAt(position);if(!IsSourceIdentifier(first.Text))yield break;var text=first.Text;var cursor=position+1;yield return(text,cursor);
            for(var count=0;count<32;count++)
            {
                var colon=TokenAt(cursor).Text=="::"||TokenAt(cursor).Text==":"&&TokenAt(cursor+1).Text==":";
                var next=cursor+(TokenAt(cursor).Text=="::"?1:colon?2:1);
                if(!colon&&TokenAt(cursor).Text!="."||!IsSourceIdentifier(TokenAt(next).Text))yield break;
                text+=(colon?"::":".")+TokenAt(next).Text;cursor=next+1;yield return(text,cursor);
            }
        }
        private int CompilationArity(int position)
        {
            if(TokenAt(position).Text!="<")return 0;var depth=0;var arity=1;
            for(var p=position;p<position+1024;p++)
            {
                var token=TokenAt(p).Text;if(token=="<")depth++;else if(token is ">" or ">>" or ">>>"){depth-=token.Length;if(depth<=0)return arity;}
                else if(token==","&&depth==1)arity++;else if(token is "" or "\n" or "{")break;
            }
            return 0;
        }
        private (string Name,string Alias,int End)? CompilationQualification(int position,bool expression)
        {
            if(activeCompilation?.TypeResolver is null||expression&&CompilationValueShadows(position))return null;
            var first=TokenAt(position);if(!expression&&ScopedTypeArgument(first.Text,out _))return null;
            if(ReservedTypeParameterNames.Contains(first.Text))return null;
            (string Name,string Alias,int End)? found=null;
            foreach(var (path,end) in CompilationPaths(position))
            {
                var arity=CompilationArity(end);var target=activeCompilation.TypeResolver(path,arity,first.Location.Offset);if(target is null)continue;
                var genericEnd=target.LastIndexOf('>');if(genericEnd>=0&&genericEnd+1<target.Length&&target[genericEnd+1]=='.')continue;
                if(arity==0)target=activeCompilation.TypeNormalizer?.Invoke(path,first.Location.Offset)??target;
                var builtin=QualifiedBuiltinAlias(target);var head=target.Split('<','[')[0];
                if(builtin is not null)found=(target,builtin,end);
                else if(_structs.Any(s=>s.Name==head&&!s.IsTypeParameter)||_enums.Any(e=>e.Name==head))found=(target,target,end);
            }
            foreach(var (path,end) in CompilationGenericPaths(position))
            {
                var target=activeCompilation.TypeNormalizer?.Invoke(path,first.Location.Offset);if(string.IsNullOrEmpty(target))continue;
                var head=target.Split('<','[')[0];
                if(_structs.Any(s=>s.Name==head&&!s.IsTypeParameter)||_enums.Any(e=>e.Name==head))found=(target,target,end);
            }
            return found;
        }
        private IEnumerable<(string Path,int End)> CompilationGenericPaths(int position)
        {
            var text="";var cursor=position;var generic=false;
            for(var segment=0;segment<32;segment++)
            {
                if(!IsSourceIdentifier(TokenAt(cursor).Text))yield break;
                text+=TokenAt(cursor++).Text;
                if(TokenAt(cursor).Text=="<")
                {
                    generic=true;var depth=0;var stop=cursor+1024;
                    do
                    {
                        var token=TokenAt(cursor++).Text;
                        if(token=="<")depth++;
                        else if(token is ">" or ">>" or ">>>")depth-=token.Length;
                        else if(token is "" or "\n" or "{")yield break;
                        text+=token+" ";
                        if(cursor>=stop||depth<0)yield break;
                    }while(depth>0);
                }
                if(generic)yield return(text,cursor);
                if(TokenAt(cursor).Text=="."){text+=".";cursor++;}
                else if(TokenAt(cursor).Text=="::"){text+="::";cursor++;}
                else if(TokenAt(cursor).Text==":"&&TokenAt(cursor+1).Text==":"){text+="::";cursor+=2;}
                else yield break;
            }
        }
        private (string Name,int End)? CompilationFunctionAt(int position)
        {
            if(activeCompilation?.FunctionResolver is null||CompilationValueShadows(position))return null;
            (string Name,int End)? found=null;
            foreach(var (path,end) in CompilationPaths(position))
            {if(!path.Contains('.')&&!path.Contains("::",StringComparison.Ordinal))continue;var function=activeCompilation.FunctionResolver(path,TokenAt(position).Location.Offset);if(function is not null)found=(function,end);}
            return found;
        }
        private (ScalarEnum Enum,int End)? CompilationEnumAt(int position)
        {
            var qualified=CompilationQualification(position,true);if(qualified is null||TokenAt(qualified.Value.End).Text!=".")return null;
            var type=Type(qualified.Value.Name,TokenAt(position).Location);return IsEnum(type)?(EnumOf(type),qualified.Value.End):null;
        }
    }
}
