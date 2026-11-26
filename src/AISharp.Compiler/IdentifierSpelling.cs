using System.Buffers;
using System.Globalization;
using System.Text;

namespace AISharp.Compiler;

// Reads source spelling without replacing source text or changing UTF-16 locations.
internal static class IdentifierSpelling
{
    internal const string StartPattern = @"(?:[_\p{L}\p{Nl}]|[\uD800-\uDBFF][\uDC00-\uDFFF]|\\u[0-9a-fA-F]{4}|\\U[0-9a-fA-F]{8})";
    internal const string PartPattern = @"(?:[_\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}\p{Cf}]|[\uD800-\uDBFF][\uDC00-\uDFFF]|\\u[0-9a-fA-F]{4}|\\U[0-9a-fA-F]{8})";
    internal const string Pattern = "@?" + StartPattern + PartPattern + "*";

    private static bool Unit(string source,int index,out Rune value,out int width)
    {
        value=default;width=0;if(index>=source.Length)return false;
        if(source[index]!='\\')return Rune.DecodeFromUtf16(source.AsSpan(index),out value,out width)==OperationStatus.Done;
        if(index+1>=source.Length||source[index+1] is not ('u' or 'U'))return false;
        var digits=source[index+1]=='u'?4:8;if(source.Length-index<digits+2)return false;
        uint code=0;
        for(var i=index+2;i<index+digits+2;i++)
        {
            var c=source[i];var h=c is >= '0' and <= '9'?c-'0':c is >= 'a' and <= 'f'?c-'a'+10:c is >= 'A' and <= 'F'?c-'A'+10:-1;
            if(h<0)return false;code=(code<<4)|(uint)h;
        }
        if(code>0x10ffff||!Rune.TryCreate((int)code,out value))return false;
        width=digits+2;return true;
    }
    private static bool Start(Rune value)=>value.Value=='_'||Rune.GetUnicodeCategory(value) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    private static bool Part(Rune value)=>Start(value)||Rune.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;
    internal static bool IsPartAt(string source,int index)=>Unit(source,index,out var value,out _)&&Part(value);
    internal static bool TryRead(string source,int start,out int end,bool allowLegacyHash=false)
    {
        end=start;var at=start<source.Length&&source[start]=='@';var p=at?start+1:start;
        if(!Unit(source,p,out var value,out var width)||!Start(value))return false;
        p+=width;
        while(p<source.Length)
        {
            if(allowLegacyHash&&!at&&source[p]=='#'){p++;continue;}
            if(!Unit(source,p,out value,out width)||!Part(value))break;
            p+=width;
        }
        end=p;return true;
    }
    internal static bool Valid(string text,bool allowAt=true)=>text.Length>0&&(allowAt||text[0]!='@')&&TryRead(text,0,out var end)&&end==text.Length;
    internal static string Name(string text)
    {
        var result=new StringBuilder(text.Length);var i=text.StartsWith('@')?1:0;
        while(i<text.Length)
        {
            if(!Unit(text,i,out var value,out var width))return text;
            if(Rune.GetUnicodeCategory(value)!=UnicodeCategory.Format)result.Append(value.ToString());
            i+=width;
        }
        return result.ToString();
    }
}
