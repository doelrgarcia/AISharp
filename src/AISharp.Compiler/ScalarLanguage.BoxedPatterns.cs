using System.Globalization;

namespace AISharp.Compiler;

public static partial class ScalarLanguage
{
    private sealed record TypedPatternConstant(ScalarType Type,object Value);
    [ThreadStatic] private static HashSet<(int Function,string Scope,int Pattern)>? checkedOpenPatternInputs;
    private sealed partial class ExtendedBinder
    {
        private bool OpenPatternInput(ScalarExpression subject,SourceLocation at)
        {
            if(!Has(ScalarFeatures.Boxing))return false;
            var site=DeconstructBindingSite(at);
            if(_checkOnly&&IsTypeParameterType(subject.Type))checkedOpenPatternInputs!.Add(site);
            return checkedOpenPatternInputs!.Contains(site);
        }
        private ScalarExpression CommonPatternInput(ScalarExpression left,ScalarExpression right,ScalarExpression original)
        {
            if(left.Type==right.Type)return left;
            if(ReferenceConvertible(_structs,left.Type,right.Type,[])||Boxable(left.Type)&&IsObject(right.Type))return right;
            if(ReferenceConvertible(_structs,right.Type,left.Type,[])||Boxable(right.Type)&&IsObject(left.Type))return left;
            return original;
        }
        private PatternResult BoxedConstantPattern(ScalarExpression subject,Lexeme token)
        {
            var relational=token.Text is "<" or "<=" or ">" or ">=";if(relational)Next();var start=Current.Location;
            _noLambda++;ScalarExpression constant;try{constant=Expression(SwitchPrecedence);}finally{_noLambda--;}
            Read(constant);var value=PatternValue(constant);if(value is null)Fail("AI3113","A pattern requires a compile-time constant.",start);
            if(!(Boxable(constant.Type)||constant.Type==ScalarType.String))Fail("AI3107","This constant cannot be used in a boxed value pattern.",start);
            if(relational&&(constant.Type is ScalarType.Bool or ScalarType.String))Fail("AI3107","A relational pattern requires an ordered numeric, character or enum constant.",start);
            if(relational&&value is double number&&double.IsNaN(number))Fail("AI3113","A relational pattern cannot use NaN.",start);
            if(_checkOnly&&IsTypeParameterType(subject.Type))return new(new("literal",ScalarType.Bool,token.Location,"true"),relational?null:[new TypedPatternConstant(constant.Type,value!)],false,token.Location);
            var narrowed=TryBoxPattern(subject,constant.Type,start);
            if(narrowed is null){Fail("AI3107","The constant's type cannot match this pattern input.",start);return null!;}
            var compare=relational?MakeBinary(token.Text,narrowed.Value.Value,constant,token.Location):ObjectValueEqual(narrowed.Value.Value,constant,token.Location);
            return new(And(narrowed.Value.Test,compare,token.Location),relational?null:[new TypedPatternConstant(constant.Type,value!)],false,token.Location){Narrowed=narrowed.Value.Value};
        }
        private ScalarExpression PatternPropertyRead(ScalarExpression value,Lexeme member)
        {
            if(value.Type!=ScalarType.String)return FieldRead(value,member);
            if(member.Text!="Length")Fail("AI3103","String has no supported property with this name.",member.Location);
            return new("length",ScalarType.Int,member.Location,Left:value);
        }
        private PatternValueCache CachedPatternValue(ScalarExpression value)
        {
            var key=PatternKey(value);if(_patternValues.TryGetValue(key,out var known))return known;
            var read=PatternTemporary(value.Type,value.Location);var flag=PatternTemporary(ScalarType.Bool,value.Location);
            var cache=new PatternValueCache(read,PatternOnce(flag,new("bindpat",ScalarType.Bool,value.Location,Left:read,Right:value),value.Location));_patternValues[key]=cache;return cache;
        }
    }
}
