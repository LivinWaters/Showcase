// Mantissa struct — proof of concept tier, deliberately separate from the BigInt system.
//
//   value = significand × 10^exponent10
//   significand : long  — up to 18 significant decimal digits, trailing zeros stripped
//   exponent10  : int   — decimal exponent (range ceiling ±2,147,483,647)
//
// Storage is 12 bytes, value type, zero GC; arithmetic uses Int128 intermediates so
// 18-digit × 18-digit products and alignments never overflow.
//
// Relationship to the existing tiers (unchanged by this PoC):
//   BigInt system   — System.Numerics.BigInteger + BigIntChunked/BigIntUtils: exact, heap-allocated.
//   BigExp tier     — 16-byte double mantissa + double exponent (googolplex range, ~16 digits).
//   This struct     — fixed 18-digit precision, int exponent: exact for small chains,
//                     approximate past 18 digits, cannot represent tower exponents.

using System;
using System.Globalization;
using System.Numerics;

namespace MantissaSystem
{
    public readonly struct Mantissa : IEquatable<Mantissa>, IComparable<Mantissa>
    {
        public const int Precision = 18;
        private const long MaxSignificand = 999999999999999999L;
        private const long ExpStop = 4_000_000_000L;

        private static readonly Int128[] Pow10Table = BuildPow10Table();
        private static readonly double[] Pow10Double = BuildPow10DoubleTable();

        private static Int128[] BuildPow10Table()
        {
            var table = new Int128[37];
            Int128 v = 1;
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = v;
                v *= 10;
            }
            return table;
        }

        private static double[] BuildPow10DoubleTable()
        {
            var table = new double[309];
            for (int i = 0; i < table.Length; i++)
                table[i] = i == 0 ? 1.0 : table[i - 1] * 10.0;
            return table;
        }

        private readonly long _significand;
        private readonly int _exponent10;

        public static readonly Mantissa Zero = default;
        public static readonly Mantissa One = new Mantissa(1, 0);

        public bool IsZero => _significand == 0;
        public long Significand => _significand;
        public int Exponent10 => _exponent10;

        private Mantissa(long significand, int exponent10)
        {
            _significand = significand;
            _exponent10 = exponent10;
        }

        private static Int128 Pow10(long e) => Pow10Table[(int)e];

        private static Mantissa Create(long sig, long exp)
        {
            if (sig == 0) return Zero;
            if (sig > MaxSignificand || sig < -MaxSignificand)
            {
                while (sig > MaxSignificand || sig < -MaxSignificand)
                {
                    sig = RoundDiv10(sig);
                    exp++;
                }
                if (sig == 0) return Zero;
            }
            while (sig != 0 && sig % 10 == 0)
            {
                sig /= 10;
                exp++;
            }
            if (exp > int.MaxValue || exp < int.MinValue)
                throw new OverflowException($"Mantissa: decimal exponent {exp} exceeds the int exponent10 range.");
            return new Mantissa(sig, (int)exp);
        }

        private static long RoundDiv10(long v) => v >= 0 ? (v + 5) / 10 : (v - 5) / 10;

        private static Mantissa FromInt128(Int128 v, long exp)
        {
            if (v == 0) return Zero;
            bool neg = v < 0;
            Int128 abs = neg ? -v : v;
            while (abs > MaxSignificand)
            {
                abs = (abs + 5) / 10;
                exp++;
            }
            if (exp > int.MaxValue || exp < int.MinValue)
                throw new OverflowException($"Mantissa: decimal exponent {exp} exceeds the int exponent10 range.");
            long sig = (long)abs;
            while (sig != 0 && sig % 10 == 0)
            {
                sig /= 10;
                exp++;
            }
            return new Mantissa(neg ? -sig : sig, (int)exp);
        }

        private static int DigitCount(long v)
        {
            if (v < 0) v = -v;
            int n = 0;
            while (v != 0) { v /= 10; n++; }
            return n == 0 ? 1 : n;
        }

        public static bool TryParse(string text, out Mantissa result)
        {
            result = Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim().Replace(",", "").Replace("_", "");
            if (t.Length == 0) return false;

            int i = 0;
            bool negative = false;
            if (t[0] == '-' || t[0] == '+') { negative = t[0] == '-'; i = 1; }
            if (i >= t.Length) return false;

            long sig = 0;
            long kept = 0;
            long total = 0;
            long frac = 0;
            int firstDropped = -1;
            bool dotSeen = false;
            bool expNeg = false;
            long expVal = 0;
            bool expSeen = false;
            bool expDigitsSeen = false;
            bool expOverflow = false;

            for (; i < t.Length; i++)
            {
                char c = t[i];
                if (!expSeen && (c == 'e' || c == 'E')) { expSeen = true; continue; }
                if (expSeen)
                {
                    if (c == '+' || c == '-') { if (expDigitsSeen) return false; expNeg = c == '-'; continue; }
                    if (c < '0' || c > '9') return false;
                    expDigitsSeen = true;
                    if (expVal > ExpStop) { expOverflow = true; }
                    else expVal = expVal * 10 + (c - '0');
                    continue;
                }
                if (c == '.') { if (dotSeen) return false; dotSeen = true; continue; }
                if (c < '0' || c > '9') return false;
                total++;
                if (dotSeen) frac++;
                if (kept < Precision) { sig = sig * 10 + (c - '0'); kept++; }
                else if (firstDropped < 0) firstDropped = c - '0';
            }
            if (total == 0) return false;
            if (expSeen && !expDigitsSeen) return false;
            if (expOverflow) return false;

            long exp = (expNeg ? -expVal : expVal) - frac + (total - kept);
            if (firstDropped >= 5) sig++;

            try { result = Create(negative ? -sig : sig, exp); }
            catch (OverflowException) { return false; }
            return true;
        }

        public static Mantissa Parse(string text)
        {
            if (!TryParse(text, out var result))
                throw new FormatException("Mantissa: invalid number '" + text + "'");
            return result;
        }

        public static Mantissa FromBigInteger(BigInteger value)
        {
            if (value.IsZero) return Zero;
            return Parse(value.ToString(CultureInfo.InvariantCulture));
        }

        public BigInteger ToBigInteger()
        {
            if (IsZero) return BigInteger.Zero;
            if (_exponent10 >= 0)
            {
                if (_exponent10 > (1 << 20))
                    throw new OverflowException("Mantissa: ToBigInteger would expand beyond 2^20 digits");
                return new BigInteger(_significand) * BigInteger.Pow(10, _exponent10);
            }
            int d = -_exponent10;
            if (d > (1 << 20))
                throw new OverflowException("Mantissa: ToBigInteger would expand beyond 2^20 digits");
            BigInteger pow = BigInteger.Pow(10, d);
            BigInteger q = BigInteger.DivRem(new BigInteger(_significand), pow, out BigInteger r);
            if (BigInteger.Abs(r) * 2 >= pow)
                q += _significand < 0 ? -1 : 1;
            return q;
        }

        public static Mantissa FromDouble(double value)
        {
            if (double.IsNaN(value)) return Zero;
            if (double.IsPositiveInfinity(value)) throw new OverflowException("Mantissa: +Infinity has no decimal representation");
            if (double.IsNegativeInfinity(value)) throw new OverflowException("Mantissa: -Infinity has no decimal representation");
            return Parse(value.ToString("R", CultureInfo.InvariantCulture));
        }

        public double ToDouble()
        {
            if (IsZero) return 0;
            if (_exponent10 > 308) return _significand < 0 ? -double.MaxValue : double.MaxValue;
            if (_exponent10 < -324) return 0;
            int absExp = _exponent10 >= 0 ? _exponent10 : -_exponent10;
            double result = _significand * Pow10Double[absExp];
            if (_exponent10 < 0) result = _significand / Pow10Double[-_exponent10];
            if (double.IsInfinity(result))
                return _significand < 0 ? -double.MaxValue : double.MaxValue;
            return result;
        }

        public static Mantissa Add(Mantissa a, Mantissa b)
        {
            if (a.IsZero) return b;
            if (b.IsZero) return a;
            if (a._exponent10 < b._exponent10) { var t = a; a = b; b = t; }
            long shift = (long)a._exponent10 - b._exponent10;
            if (shift > Precision)
            {
                long la = a._exponent10 + DigitCount(a._significand);
                long lb = b._exponent10 + DigitCount(b._significand);
                if (la - lb > Precision) return a;
            }
            Int128 combined = (Int128)a._significand * Pow10(shift) + b._significand;
            return FromInt128(combined, b._exponent10);
        }

        public static Mantissa Subtract(Mantissa a, Mantissa b)
        {
            if (b.IsZero) return a;
            if (a.IsZero) return new Mantissa(-b._significand, b._exponent10);
            return Add(a, new Mantissa(-b._significand, b._exponent10));
        }

        public static Mantissa Multiply(Mantissa a, Mantissa b)
        {
            if (a.IsZero || b.IsZero) return Zero;
            Int128 p = (Int128)a._significand * b._significand;
            return FromInt128(p, (long)a._exponent10 + b._exponent10);
        }

        public static Mantissa DivideByInt(Mantissa a, int n)
        {
            if (n == 0) throw new DivideByZeroException();
            if (a.IsZero) return Zero;
            if (n == 1) return a;
            Int128 sig = a._significand;
            Int128 den = n;
            bool neg = (sig < 0) ^ (den < 0);
            sig = sig < 0 ? -sig : sig;
            den = den < 0 ? -den : den;
            Int128 num = sig * Pow10(Precision);
            Int128 q = (num + den / 2) / den;
            return FromInt128(neg ? -q : q, (long)a._exponent10 - Precision);
        }

        public static Mantissa MultiplyByInt(Mantissa a, int n)
        {
            if (n == 0) return Zero;
            if (a.IsZero) return Zero;
            if (n == 1) return a;
            return FromInt128((Int128)a._significand * n, a._exponent10);
        }

        public static Mantissa Negate(Mantissa a)
            => a.IsZero ? Zero : new Mantissa(-a._significand, a._exponent10);

        public static Mantissa operator +(Mantissa a, Mantissa b) => Add(a, b);
        public static Mantissa operator -(Mantissa a, Mantissa b) => Subtract(a, b);
        public static Mantissa operator *(Mantissa a, Mantissa b) => Multiply(a, b);
        public static Mantissa operator /(Mantissa a, int n) => DivideByInt(a, n);
        public static Mantissa operator *(Mantissa a, int n) => MultiplyByInt(a, n);
        public static Mantissa operator -(Mantissa a) => Negate(a);

        public int CompareTo(Mantissa other)
        {
            if (IsZero && other.IsZero) return 0;
            if (IsZero) return other._significand > 0 ? -1 : 1;
            if (other.IsZero) return _significand > 0 ? 1 : -1;
            if ((_significand > 0) != (other._significand > 0))
                return _significand > 0 ? 1 : -1;

            int mag = CompareMagnitude(other);
            return _significand > 0 ? mag : -mag;
        }

        private int CompareMagnitude(Mantissa o)
        {
            long la = _exponent10 + DigitCount(_significand);
            long lb = o._exponent10 + DigitCount(o._significand);
            if (la != lb) return la > lb ? 1 : -1;
            long shift = (long)_exponent10 - o._exponent10;
            Int128 mine = _significand, theirs = o._significand;
            if (shift >= 0) mine *= Pow10(shift);
            else theirs *= Pow10(-shift);
            return mine.CompareTo(theirs);
        }

        public bool Equals(Mantissa other)
        {
            if (IsZero && other.IsZero) return true;
            return _significand == other._significand && _exponent10 == other._exponent10;
        }

        public override bool Equals(object obj)
            => obj is Mantissa m && Equals(m);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = _significand.GetHashCode();
                h = (h * 397) ^ _exponent10.GetHashCode();
                return h;
            }
        }

        public static bool operator ==(Mantissa a, Mantissa b) => a.Equals(b);
        public static bool operator !=(Mantissa a, Mantissa b) => !a.Equals(b);
        public static bool operator <(Mantissa a, Mantissa b) => a.CompareTo(b) < 0;
        public static bool operator >(Mantissa a, Mantissa b) => a.CompareTo(b) > 0;
        public static bool operator <=(Mantissa a, Mantissa b) => a.CompareTo(b) <= 0;
        public static bool operator >=(Mantissa a, Mantissa b) => a.CompareTo(b) >= 0;

        public string ToScientific(int decimals = 6)
        {
            if (IsZero) return "0";
            string digits = Math.Abs(_significand).ToString(CultureInfo.InvariantCulture);
            long exponent = (long)_exponent10 + digits.Length - 1;
            string mantissaText;
            if (decimals <= 0)
                mantissaText = digits.Substring(0, 1);
            else
            {
                string padded = digits.Length >= decimals + 1 ? digits.Substring(0, decimals + 1)
                    : digits.PadRight(decimals + 1, '0');
                mantissaText = padded.Substring(0, 1) + "." + padded.Substring(1).TrimEnd('0');
                if (mantissaText.EndsWith(".", StringComparison.Ordinal))
                    mantissaText = mantissaText.Substring(0, mantissaText.Length - 1);
            }
            return (_significand < 0 ? "-" : "") + mantissaText + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        }

        public override string ToString()
        {
            if (IsZero) return "0";
            string digits = Math.Abs(_significand).ToString(CultureInfo.InvariantCulture);
            string sign = _significand < 0 ? "-" : "";
            if (_exponent10 >= 0)
            {
                if (_exponent10 > Precision) return ToScientific(6);
                return sign + digits + new string('0', _exponent10);
            }
            int point = digits.Length + _exponent10;
            if (point > 0) return sign + digits.Substring(0, point) + "." + digits.Substring(point);
            if (point < -Precision) return ToScientific(6);
            return sign + "0." + new string('0', -point) + digits;
        }

        public double RelativeError(Mantissa reference)
        {
            if (reference.IsZero) return IsZero ? 0.0 : 1.0;
            long shift = (long)_exponent10 - reference._exponent10;
            if (shift > Precision || shift < -Precision) return 1.0;
            Int128 mine = _significand;
            Int128 theirs = reference._significand;
            if (shift >= 0) mine *= Pow10(shift);
            else theirs *= Pow10(-shift);
            Int128 diff = mine - theirs;
            if (diff < 0) diff = -diff;
            Int128 magnitude = theirs < 0 ? -theirs : theirs;
            if (magnitude == 0) return 1.0;
            return (double)diff / (double)magnitude;
        }
    }
}