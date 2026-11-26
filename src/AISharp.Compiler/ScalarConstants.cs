using System.Numerics;

namespace AISharp.Compiler;

/// <summary>Outcome of a checked scalar operation.</summary>
public enum ScalarFault { None, Overflow, DivideByZero }

/// <summary>
/// Reference semantics for AI# scalar integer arithmetic, conversions and constant folding.
/// Values are exact mathematical integers; every result is range checked (throws) or wrapped (unchecked).
/// Shared by the extended front end (constant folding) and the native verifier; native runtime helpers are tested against it.
/// </summary>
public static class ScalarConstants
{
    public static bool IsInteger(ScalarType type) =>
        type is ScalarType.Int or ScalarType.Long or ScalarType.UInt or ScalarType.ULong
            or ScalarType.Short or ScalarType.UShort or ScalarType.Byte or ScalarType.SByte or ScalarType.Char or ScalarType.NInt or ScalarType.NUInt;
    public static bool IsNative(ScalarType type) => type is ScalarType.NInt or ScalarType.NUInt;
    /// <summary>Choose storage from an explicit native target, never from the compiler process's pointer size.</summary>
    public static ScalarType NativeStorage(ScalarType type, int pointerBits) => pointerBits is not (32 or 64)
        ? throw new ArgumentOutOfRangeException(nameof(pointerBits)) : type switch
        {
            ScalarType.NInt => pointerBits == 32 ? ScalarType.Int : ScalarType.Long,
            ScalarType.NUInt => pointerBits == 32 ? ScalarType.UInt : ScalarType.ULong,
            _ => type
        };
    public static bool IsFloat(ScalarType type) => type is ScalarType.F32 or ScalarType.F64;
    public static bool IsDecimal(ScalarType type) => type == ScalarType.Decimal;
    public static bool IsSigned(ScalarType type) => type is ScalarType.SByte or ScalarType.Short or ScalarType.Int or ScalarType.Long or ScalarType.NInt;
    public static int Bits(ScalarType type) => type switch
    {
        ScalarType.Byte or ScalarType.SByte => 8,
        ScalarType.Short or ScalarType.UShort or ScalarType.Char => 16,
        ScalarType.Int or ScalarType.UInt => 32,
        ScalarType.Long or ScalarType.ULong => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    public static BigInteger Min(ScalarType type) => IsSigned(type) ? -(BigInteger.One << (Bits(type) - 1)) : BigInteger.Zero;
    public static BigInteger Max(ScalarType type) => IsSigned(type) ? (BigInteger.One << (Bits(type) - 1)) - 1 : (BigInteger.One << Bits(type)) - 1;
    public static bool Fits(BigInteger value, ScalarType type) => value >= Min(type) && value <= Max(type);
    /// <summary>Two's-complement truncation to the width and signedness of <paramref name="type"/>.</summary>
    public static BigInteger Wrap(BigInteger value, ScalarType type)
    {
        var modulus = BigInteger.One << Bits(type);
        var result = ((value % modulus) + modulus) % modulus;
        return IsSigned(type) && result > Max(type) ? result - modulus : result;
    }
    public static string Spelling(ScalarType type) => type switch
    {
        ScalarType.Int => "int", ScalarType.Long => "long", ScalarType.UInt => "uint", ScalarType.ULong => "ulong",
        ScalarType.Short => "short", ScalarType.UShort => "ushort", ScalarType.Byte => "byte", ScalarType.SByte => "sbyte",
        ScalarType.NInt => "nint", ScalarType.NUInt => "nuint", ScalarType.Bool => "bool", ScalarType.F32 => "f32", ScalarType.F64 => "f64", ScalarType.Char => "char", ScalarType.String => "string", ScalarType.Decimal => "decimal", _ => "void"
    };
    public static ScalarType? FromSpelling(string text) => text switch
    {
        "int" or "i32" => ScalarType.Int, "long" => ScalarType.Long, "uint" => ScalarType.UInt, "ulong" => ScalarType.ULong,
        "short" or "i16" => ScalarType.Short, "ushort" => ScalarType.UShort, "byte" or "u8" => ScalarType.Byte, "sbyte" or "i8" => ScalarType.SByte,
        "nint" => ScalarType.NInt, "nuint" => ScalarType.NUInt, "bool" => ScalarType.Bool, _ => null
    };

    /// <summary>Checked (throws) or unchecked (wraps) <c>+ - * / %</c> on operands already of type <paramref name="type"/>.</summary>
    public static ScalarFault Arithmetic(string op, ScalarType type, BigInteger left, BigInteger right, bool wraps, out BigInteger result)
    {
        result = BigInteger.Zero;
        if (op is "/" or "%" && right.IsZero) return ScalarFault.DivideByZero;
        // MIN / -1 and MIN % -1 overflow in every context (the hardware divide cannot represent them), as on .NET.
        if (op is "/" or "%" && IsSigned(type) && left == Min(type) && right == BigInteger.MinusOne) { result = BigInteger.Zero; return ScalarFault.Overflow; }
        var exact = op switch
        {
            "+" => left + right, "-" => left - right, "*" => left * right,
            "/" => BigInteger.Divide(left, right), "%" => BigInteger.Remainder(left, right),
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        return Narrow(exact, type, wraps, out result);
    }
    public static ScalarFault Negate(ScalarType type, BigInteger value, bool wraps, out BigInteger result) =>
        Narrow(-value, type, wraps, out result);
    /// <summary>Explicit numeric conversion: checked conversions throw when the value does not fit, unchecked ones truncate.</summary>
    public static ScalarFault Convert(ScalarType to, BigInteger value, bool wraps, out BigInteger result) =>
        Narrow(value, to, wraps, out result);
    private static ScalarFault Narrow(BigInteger exact, ScalarType type, bool wraps, out BigInteger result)
    {
        if (Fits(exact, type)) { result = exact; return ScalarFault.None; }
        if (wraps) { result = Wrap(exact, type); return ScalarFault.None; }
        result = BigInteger.Zero; return ScalarFault.Overflow;
    }

    /// <summary>Bitwise <c>&amp; | ^</c> on operands of one type (two's complement semantics).</summary>
    public static BigInteger Bitwise(string op, BigInteger left, BigInteger right) => op switch
    {
        "&" => left & right, "|" => left | right, "^" => left ^ right, _ => throw new ArgumentOutOfRangeException(nameof(op))
    };
    /// <summary>Bitwise complement within the width of <paramref name="type"/>.</summary>
    public static BigInteger BitNot(ScalarType type, BigInteger value) => IsSigned(type) ? -value - 1 : Max(type) - value;
    /// <summary>Shift with the count masked to the operand width; shifts never fault. <c>&gt;&gt;&gt;</c> is a logical shift.</summary>
    public static BigInteger Shift(string op, ScalarType type, BigInteger value, BigInteger count)
    {
        var shift = (int)(count & (Bits(type) - 1));
        return op switch
        {
            "<<" => Wrap(value << shift, type),
            ">>" => value >> shift,
            ">>>" => Wrap((value & ((BigInteger.One << Bits(type)) - 1)) >> shift, type),
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
    }

    // ---- IEEE floating point (f32 = binary32, f64 = binary64); f32 values are carried as the exactly equal double ----
    /// <summary>Correctly rounded (nearest, ties to even) conversion of an integer to binary32 or binary64, independent of the host conversion operators.</summary>
    public static double IntegerToFloat(BigInteger value, ScalarType target)
    {
        var precision = target == ScalarType.F32 ? 24 : 53;
        var magnitude = BigInteger.Abs(value); var sign = value.Sign < 0 ? -1.0 : 1.0;
        if (magnitude.IsZero) return 0.0;
        var length = (int)magnitude.GetBitLength();
        if (length <= precision) return sign * (double)(long)magnitude;
        var drop = length - precision;
        var mantissa = magnitude >> drop; var remainder = magnitude - (mantissa << drop); var half = BigInteger.One << (drop - 1);
        if (remainder > half || remainder == half && !mantissa.IsEven) mantissa += 1;
        // mantissa <= 2^precision, so the scaling is exact in binary64; binary32 results are exactly representable by construction.
        var result = sign * Math.ScaleB((double)(long)mantissa, drop);
        return target == ScalarType.F32 ? (double)(float)result : result;
    }
    /// <summary>Float-to-integer conversion: checked truncation faults outside the target range.
    /// Unchecked conversions saturate at 32/64 bits (NaN becomes zero); smaller targets
    /// narrow the saturated Int32 result, matching the product runtime's conv.i1/u1/i2/u2.</summary>
    public static ScalarFault FloatToInteger(ScalarType to, double value, bool wraps, out BigInteger result)
    {
        if (wraps && Bits(to) < 32)
        {
            var fault = FloatToInteger(ScalarType.Int, value, true, out var intermediate);
            result = Wrap(intermediate, to);
            return fault;
        }
        result = BigInteger.Zero;
        if (double.IsNaN(value)) return wraps ? ScalarFault.None : ScalarFault.Overflow;
        var truncated = double.IsInfinity(value) ? value : Math.Truncate(value);
        if (double.IsInfinity(truncated) || new BigInteger(truncated) is var exact && (exact < Min(to) || exact > Max(to)))
        {
            if (!wraps) return ScalarFault.Overflow;
            result = truncated < 0 ? Min(to) : Max(to); return ScalarFault.None;
        }
        result = new BigInteger(truncated); return ScalarFault.None;
    }
    /// <summary>C#13/14 constant casts use zero for an unchecked floating value
    /// outside the destination range. Keep this distinct from runtime conv instructions.</summary>
    public static ScalarFault FloatConstantToInteger(ScalarType to, double value, bool wraps, out BigInteger result)
    {
        var fault = FloatToInteger(to, value, false, out result);
        return wraps && fault == ScalarFault.Overflow ? ScalarFault.None : fault;
    }
    /// <summary>IEEE arithmetic; f32 operations round to binary32 after each operation. Never faults.</summary>
    public static double FloatArithmetic(string op, ScalarType type, double left, double right)
    {
        if (type == ScalarType.F32)
        {
            float a = (float)left, b = (float)right;
            return op switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b, _ => throw new ArgumentOutOfRangeException(nameof(op)) };
        }
        return op switch { "+" => left + right, "-" => left - right, "*" => left * right, "/" => left / right, "%" => left % right, _ => throw new ArgumentOutOfRangeException(nameof(op)) };
    }
    /// <summary>Rounds a double to the target float type (f32 narrowing is round-to-nearest-even; overflow becomes infinity).</summary>
    public static double RoundTo(ScalarType type, double value) => type == ScalarType.F32 ? (double)(float)value : value;
    public static bool FloatCompare(string op, double left, double right) => op switch
    {
        "<" => left < right, "<=" => left <= right, ">" => left > right, ">=" => left >= right, "==" => left == right, "!=" => left != right,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    /// <summary>C# implicit numeric conversions, including integer and float to floating point.</summary>
    public static bool ImplicitNumeric(ScalarType from, ScalarType to)
    {
        if (from == to) return true;
        // C#: every integer type (including char) converts implicitly to decimal; float, double and decimal never convert implicitly to each other.
        if (to == ScalarType.Decimal) return IsInteger(from);
        if (IsFloat(to)) return IsInteger(from) || from == ScalarType.F32 && to == ScalarType.F64;
        if (!IsInteger(from) || !IsInteger(to)) return false;
        return from switch
        {
            ScalarType.SByte => to is ScalarType.Short or ScalarType.Int or ScalarType.Long or ScalarType.NInt,
            ScalarType.Byte => to is ScalarType.Short or ScalarType.UShort or ScalarType.Int or ScalarType.UInt or ScalarType.Long or ScalarType.ULong or ScalarType.NInt or ScalarType.NUInt,
            ScalarType.Short => to is ScalarType.Int or ScalarType.Long or ScalarType.NInt,
            ScalarType.UShort => to is ScalarType.Int or ScalarType.UInt or ScalarType.Long or ScalarType.ULong or ScalarType.NInt or ScalarType.NUInt,
            ScalarType.Char => to is ScalarType.UShort or ScalarType.Int or ScalarType.UInt or ScalarType.Long or ScalarType.ULong or ScalarType.NInt or ScalarType.NUInt,
            ScalarType.Int => to is ScalarType.Long or ScalarType.NInt,
            ScalarType.UInt => to is ScalarType.Long or ScalarType.ULong or ScalarType.NUInt,
            ScalarType.NInt => to is ScalarType.Long,
            ScalarType.NUInt => to is ScalarType.ULong,
            _ => false
        };
    }
    /// <summary>Binary numeric promotion; null when no common type exists (for example long with ulong).</summary>
    public static ScalarType? Promote(ScalarType left, ScalarType right)
    {
        if (!IsInteger(left) || !IsInteger(right)) return null;
        if (left == ScalarType.ULong || right == ScalarType.ULong)
        {
            var other = left == ScalarType.ULong ? right : left;
            return IsSigned(other) ? null : ScalarType.ULong;
        }
        if (left == ScalarType.NUInt || right == ScalarType.NUInt)
        {
            var other = left == ScalarType.NUInt ? right : left;
            return IsSigned(other) ? null : ScalarType.NUInt;
        }
        if (left == ScalarType.Long || right == ScalarType.Long) return ScalarType.Long;
        if (left == ScalarType.UInt || right == ScalarType.UInt)
        {
            var other = left == ScalarType.UInt ? right : left;
            return other is ScalarType.SByte or ScalarType.Short or ScalarType.Int or ScalarType.NInt ? ScalarType.Long : ScalarType.UInt;
        }
        if (left == ScalarType.NInt || right == ScalarType.NInt) return ScalarType.NInt;
        return ScalarType.Int;
    }
    /// <summary>Unary promotion: small types become int, uint becomes long under negation; null when illegal.</summary>
    public static ScalarType? PromoteUnary(ScalarType type, bool negation)
    {
        if (!IsInteger(type)) return null;
        if (type is ScalarType.SByte or ScalarType.Byte or ScalarType.Short or ScalarType.UShort or ScalarType.Char or ScalarType.Int) return ScalarType.Int;
        if (type == ScalarType.UInt) return negation ? ScalarType.Long : ScalarType.UInt;
        if (type is ScalarType.ULong or ScalarType.NUInt) return negation ? null : type;
        return type;
    }
}
