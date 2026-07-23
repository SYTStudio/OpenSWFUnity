using System;
using System.Globalization;
using System.Text;

namespace OpenSWFUnity.Runtime.AVM2.Values
{
    // Type conversion and comparison, following the ECMAScript rules AS3 inherits.
    //
    // Deliberately free of any dependency on the interpreter: everything here works
    // on values alone. Conversions that would require running user code (valueOf and
    // toString overrides) are handled by the interpreter, which calls in here for the
    // primitive cases.
    public static class Avm2Convert
    {
        public static bool IsUndefined(object value)
        {
            return ReferenceEquals(value, Avm2Undefined.Value);
        }

        public static bool IsNullOrUndefined(object value)
        {
            return value == null || ReferenceEquals(value, Avm2Undefined.Value);
        }

        public static bool IsNumeric(object value)
        {
            return value is int || value is uint || value is double;
        }

        public static bool ToBoolean(object value)
        {
            if (value == null || IsUndefined(value))
                return false;

            if (value is bool boolean)
                return boolean;

            if (value is int i)
                return i != 0;

            if (value is uint u)
                return u != 0u;

            if (value is double d)
                return !double.IsNaN(d) && d != 0d;

            if (value is string s)
                return s.Length > 0;

            return true;
        }

        // null converts to 0 in AS3, unlike undefined which gives NaN.
        public static double ToNumber(object value)
        {
            if (value == null)
                return 0d;

            if (IsUndefined(value))
                return double.NaN;

            if (value is double d)
                return d;

            if (value is int i)
                return i;

            if (value is uint u)
                return u;

            if (value is bool boolean)
                return boolean ? 1d : 0d;

            if (value is string s)
                return StringToNumber(s);

            if (value is Avm2Array array)
            {
                // An array converts through its string form, so [] is 0 and [5] is 5.
                return array.Length == 0 ? 0d : StringToNumber(ToString(array));
            }

            return double.NaN;
        }

        public static double StringToNumber(string text)
        {
            if (text == null)
                return 0d;

            string trimmed = text.Trim();

            if (trimmed.Length == 0)
                return 0d;

            if (trimmed == "Infinity" || trimmed == "+Infinity")
                return double.PositiveInfinity;

            if (trimmed == "-Infinity")
                return double.NegativeInfinity;

            bool negative = false;
            string body = trimmed;

            if (body.StartsWith("-", StringComparison.Ordinal))
            {
                negative = true;
                body = body.Substring(1);
            }
            else if (body.StartsWith("+", StringComparison.Ordinal))
            {
                body = body.Substring(1);
            }

            if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(
                        body.Substring(2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out long hex))
                {
                    return negative ? -hex : hex;
                }

                return double.NaN;
            }

            if (double.TryParse(
                    trimmed,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed))
            {
                return parsed;
            }

            return double.NaN;
        }

        // ECMA ToInt32: NaN and the infinities become 0, everything else wraps modulo
        // 2^32. Casting a large double straight to int is undefined in C#, so the
        // wrap is done explicitly.
        public static int ToInt32(object value)
        {
            return DoubleToInt32(ToNumber(value));
        }

        public static int DoubleToInt32(double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
                return 0;

            double truncated = Math.Truncate(number);
            double wrapped = truncated % 4294967296d;

            if (wrapped < 0)
                wrapped += 4294967296d;

            if (wrapped >= 2147483648d)
                wrapped -= 4294967296d;

            return (int)wrapped;
        }

        public static uint ToUint32(object value)
        {
            return DoubleToUint32(ToNumber(value));
        }

        public static uint DoubleToUint32(double number)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
                return 0u;

            double truncated = Math.Truncate(number);
            double wrapped = truncated % 4294967296d;

            if (wrapped < 0)
                wrapped += 4294967296d;

            return (uint)wrapped;
        }

        public static string ToString(object value)
        {
            if (value == null)
                return "null";

            if (IsUndefined(value))
                return "undefined";

            if (value is string s)
                return s;

            if (value is bool boolean)
                return boolean ? "true" : "false";

            if (value is int i)
                return i.ToString(CultureInfo.InvariantCulture);

            if (value is uint u)
                return u.ToString(CultureInfo.InvariantCulture);

            if (value is double d)
                return NumberToString(d);

            if (value is Avm2Array array)
                return ArrayToString(array);

            // Error subclasses keep their useful text in __message. Avm2Object's
            // fallback string is only "[object ReferenceError]", which hid the
            // missing definition and made real AS3 startup failures impossible to
            // diagnose from Unity's Console.
            if (value is Avm2Object error &&
                error.TryGetDynamic(Avm2QName.Public("__message"), out object message) &&
                !IsNullOrUndefined(message))
            {
                string errorName = error.Class != null &&
                                   !string.IsNullOrEmpty(error.Class.Name.Local)
                    ? error.Class.Name.Local
                    : "Error";
                return errorName + ": " + ToString(message);
            }

            return value.ToString();
        }

        // ECMA number formatting: integral values print without a decimal point, and
        // the infinities have spelled-out names rather than the symbol .NET uses.
        public static string NumberToString(double number)
        {
            if (double.IsNaN(number))
                return "NaN";

            if (double.IsPositiveInfinity(number))
                return "Infinity";

            if (double.IsNegativeInfinity(number))
                return "-Infinity";

            if (number == 0d)
                return "0";

            if (number == Math.Floor(number) && Math.Abs(number) < 1e21)
                return ((long)number).ToString(CultureInfo.InvariantCulture);

            return number.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string ArrayToString(Avm2Array array)
        {
            StringBuilder builder = new StringBuilder();

            for (int i = 0; i < array.Items.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');

                object item = array.Items[i];

                // null and undefined contribute nothing between the separators.
                if (!IsNullOrUndefined(item))
                    builder.Append(ToString(item));
            }

            return builder.ToString();
        }

        public static string TypeOf(object value)
        {
            if (IsUndefined(value))
                return "undefined";

            if (value == null)
                return "object";

            if (value is bool)
                return "boolean";

            if (IsNumeric(value))
                return "number";

            if (value is string)
                return "string";

            if (value is Avm2Function || value is Avm2Class)
                return "function";

            if (value is Avm2Namespace)
                return "object";

            return "object";
        }

        public static bool StrictEquals(object left, object right)
        {
            if (IsUndefined(left) || IsUndefined(right))
                return IsUndefined(left) && IsUndefined(right);

            if (left == null || right == null)
                return left == null && right == null;

            if (IsNumeric(left) && IsNumeric(right))
                return ToNumber(left) == ToNumber(right);

            if (left is string leftText && right is string rightText)
                return string.Equals(leftText, rightText, StringComparison.Ordinal);

            if (left is bool leftBool && right is bool rightBool)
                return leftBool == rightBool;

            // Mixed primitive types are never strictly equal; objects compare by
            // identity.
            return ReferenceEquals(left, right);
        }

        public static bool LooseEquals(object left, object right)
        {
            bool leftNullish = IsNullOrUndefined(left);
            bool rightNullish = IsNullOrUndefined(right);

            // null == undefined, but neither equals anything else.
            if (leftNullish || rightNullish)
                return leftNullish && rightNullish;

            if (IsNumeric(left) && IsNumeric(right))
                return ToNumber(left) == ToNumber(right);

            if (left is string leftText && right is string rightText)
                return string.Equals(leftText, rightText, StringComparison.Ordinal);

            if (left is bool || right is bool)
                return ToNumber(left) == ToNumber(right);

            if (IsNumeric(left) && right is string)
                return ToNumber(left) == ToNumber(right);

            if (left is string && IsNumeric(right))
                return ToNumber(left) == ToNumber(right);

            return ReferenceEquals(left, right);
        }

        // Returns -1, 0, 1, or int.MinValue when the comparison is undefined because
        // an operand is NaN. Callers map that to false for every relational operator,
        // which is what makes both `x < y` and `x >= y` false against NaN.
        public const int Unordered = int.MinValue;

        public static int Compare(object left, object right)
        {
            if (left is string leftText && right is string rightText)
                return string.CompareOrdinal(leftText, rightText);

            double leftNumber = ToNumber(left);
            double rightNumber = ToNumber(right);

            if (double.IsNaN(leftNumber) || double.IsNaN(rightNumber))
                return Unordered;

            return leftNumber < rightNumber ? -1 : leftNumber > rightNumber ? 1 : 0;
        }

        // The `+` operator: string when either side is a string, numeric otherwise.
        public static object Add(object left, object right)
        {
            if (left is string || right is string)
                return ToString(left) + ToString(right);

            if (left is Avm2Array || right is Avm2Array)
                return ToString(left) + ToString(right);

            if (left is int leftInt && right is int rightInt)
            {
                long sum = (long)leftInt + rightInt;

                // Stay in int only while the result is representable; AS3 promotes to
                // Number rather than wrapping.
                if (sum >= int.MinValue && sum <= int.MaxValue)
                    return (int)sum;

                return (double)sum;
            }

            return ToNumber(left) + ToNumber(right);
        }
    }
}
