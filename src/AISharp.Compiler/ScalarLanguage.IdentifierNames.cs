using System.Text.RegularExpressions;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    // Contextual names are identifiers in an explicitly selected type-parameter position.
    private static readonly HashSet<string> ReservedTypeParameterNames=new(StringComparer.Ordinal)
    {"abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while"};
    private static bool IsEscapedIdentifier(string text)
    {
        if ((ActiveFeatures & ScalarFeatures.UnicodeIdentifiers) != 0 && IdentifierSpelling.Valid(text))
            return text[0]=='@' ? (ActiveFeatures & ScalarFeatures.EscapedIdentifiers) != 0 : IdentifierSpelling.Name(text)!=text;
        return (ActiveFeatures & ScalarFeatures.EscapedIdentifiers) != 0 && text.Length>1 && text[0]=='@' && Regex.IsMatch(text,@"^@[A-Za-z_][A-Za-z0-9_]*$");
    }
    private static string IdentifierName(string text) =>
        (ActiveFeatures & ScalarFeatures.UnicodeIdentifiers) != 0 && IdentifierSpelling.Valid(text) && (text[0]!='@'||(ActiveFeatures & ScalarFeatures.EscapedIdentifiers)!=0)
        ? IdentifierSpelling.Name(text) : IsEscapedIdentifier(text) ? text[1..] : text;
    private static string CanonicalMemberName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? IdentifierName(name) : name[..(dot + 1)] + IdentifierName(name[(dot + 1)..]);
    }
    private static string TypeIdentifierSyntax(string name) =>
        ReservedTypeParameterNames.Contains(name) || BuiltInTypeNames.Contains(name) || name is "let" or "var"
        ? (ActiveFeatures & ScalarFeatures.EscapedIdentifiers)!=0 || (ActiveFeatures & ScalarFeatures.UnicodeIdentifiers)==0 ? "@"+name : "\\u"+((int)name[0]).ToString("x4",System.Globalization.CultureInfo.InvariantCulture)+name[1..]
        : name;
    private static bool ValidTypeParameterName(string name)=>IsEscapedIdentifier(name) || ((ActiveFeatures & ScalarFeatures.UnicodeIdentifiers)!=0 ? IdentifierSpelling.Valid(name,allowAt:false) : Regex.IsMatch(name,@"^[A-Za-z_][A-Za-z0-9_]*$"))
        && !ReservedTypeParameterNames.Contains(name)&&!BuiltInTypeNames.Contains(name);
    private static bool CanBindTypeParameter(string text) => IsEscapedIdentifier(text.Trim())
        || !ReservedTypeParameterNames.Contains(text.Trim()) && !BuiltInTypeNames.Contains(text.Trim());
    private static bool TypeParameterReference(string text,string name) => CanBindTypeParameter(text) && IdentifierName(text.Trim()) == name;

    private static (int Start,int NameOffset,string Name,int End)[] TypeConstraintMarkers(string header,out int contentEnd)
    {
        var tokens=new Lexer(header).Lex().Where(t=>t.Kind!=TokenKind.EndOfFile&&t.Kind!=TokenKind.NewLine).ToArray();
        contentEnd=tokens.Length==0?0:tokens[^1].Location.Offset+tokens[^1].Text.Length;
        var markers=new List<(int,int,string,int)>();
        for(var i=0;i+2<tokens.Length;i++)
            if(tokens[i].Kind==TokenKind.Identifier&&tokens[i].Text=="where"&&tokens[i+1].Kind!=TokenKind.String&&IdentifierSpelling.Valid(tokens[i+1].Text)&&tokens[i+2].Kind==TokenKind.Colon)
                markers.Add((tokens[i].Location.Offset,tokens[i+1].Location.Offset,tokens[i+1].Text,tokens[i+2].Location.Offset+1));
        return markers.ToArray();
    }

    private static bool ReadTypeIdentifier(string text,int start,out int end)
    {
        if((ActiveFeatures & ScalarFeatures.UnicodeIdentifiers)!=0)return IdentifierSpelling.TryRead(text,start,out end);
        end=start;var p=start<text.Length&&text[start]=='@'?start+1:start;
        if(p>=text.Length||!(char.IsAsciiLetter(text[p])||text[p]=='_'))return false;
        while(p<text.Length&&(char.IsAsciiLetterOrDigit(text[p])||text[p]=='_'))p++;
        end=p;return true;
    }
    // A qualified System path is a namespace/type name, including escaped components.
    // Scan whole source tokens so substitutions never split Unicode escapes or combining characters.
    private static string RewriteTypeIdentifiers(string text,Func<string,string?> rewrite)
    {
        var result=new System.Text.StringBuilder(text.Length);var i=0;
        while(i<text.Length)
        {
            if(!ReadTypeIdentifier(text,i,out var end)){result.Append(text[i++]);continue;}
            var raw=text[i..end];var name=IdentifierName(raw);var probe=end;
            while(probe<text.Length&&char.IsWhiteSpace(text[probe]))probe++;
            if(raw=="global"&&probe+1<text.Length&&text[probe..].StartsWith("::",StringComparison.Ordinal))
            {
                var system=probe+2;while(system<text.Length&&char.IsWhiteSpace(text[system]))system++;
                if(ReadTypeIdentifier(text,system,out var systemEnd)&&IdentifierName(text[system..systemEnd])=="System")
                {name="System";probe=systemEnd;end=systemEnd;}
            }
            if(name=="System")
            {
                var qualified=end;
                while(true)
                {
                    probe=qualified;while(probe<text.Length&&char.IsWhiteSpace(text[probe]))probe++;
                    if(probe>=text.Length||text[probe]!='.')break;
                    probe++;while(probe<text.Length&&char.IsWhiteSpace(text[probe]))probe++;
                    if(!ReadTypeIdentifier(text,probe,out var componentEnd))break;
                    qualified=componentEnd;
                }
                if(qualified>end){result.Append(text[i..qualified]);i=qualified;continue;}
            }
            result.Append(CanBindTypeParameter(raw)?rewrite(IdentifierName(raw))??raw:raw);i=end;
        }
        return result.ToString();
    }
}
