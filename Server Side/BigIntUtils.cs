using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using VortexClient.Core.Numbers;

namespace common
{
    /// <summary>
    /// BigInteger helpers, abbreviated display, and safe int coercion for legacy sim paths.
    /// </summary>
    public static partial class BigIntUtils
    {
        private static readonly BigInteger[] Pow10 = BuildPow10();
        private static readonly double[] Pow10Double = BuildPow10Double();

        private static BigInteger[] BuildPow10()
        {
            var arr = new BigInteger[351];
            var v = BigInteger.One;
            arr[0] = v;
            for (int i = 1; i < arr.Length; i++) { v *= 10; arr[i] = v; }
            return arr;
        }

        private static double[] BuildPow10Double()
        {
            var arr = new double[309];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = i == 0 ? 1.0 : arr[i - 1] * 10.0;
            return arr;
        }

        public static BigInteger ParseBig(string s, BigInteger defaultValue = default)
        {
            if (string.IsNullOrWhiteSpace(s))
                return defaultValue;
            var t = s.Trim();
            if (BigInteger.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            // Scientific / nested-exponent forms from the FNA client
            // ("1.5e308", "1e(1e100)"): accept when they fit inside BigInteger,
            // otherwise fall back to the default instead of silently zeroing.
            if (TryParseBigDouble(t, out var bd) && bd.FitsInBigInteger)
                return bd.ToBigInteger();
            return defaultValue;
        }

        /// <summary>Lossless parse into the exact BigDouble tier. Accepts plain
        /// decimal digits, scientific notation ("1.5e308") and BigExp power-tower
        /// notation ("1e(1e100)"). Returns BigDouble.Zero for invalid input.</summary>
        public static BigDouble ParseBigDouble(string s)
        {
            if (!TryParseBigDouble(s, out var v))
                return BigDouble.Zero;
            return v;
        }

        /// <summary>True when the string is a parseable number in any tier
        /// (plain digits, scientific, or power-tower).</summary>
        public static bool TryParseBigDouble(string s, out BigDouble result)
        {
            if (!string.IsNullOrWhiteSpace(s) && BigDouble.TryParse(s.Trim(), out result))
                return true;
            if (!string.IsNullOrWhiteSpace(s) && BigExp.TryParse(s.Trim(), out var exp))
            {
                result = exp.ToBigDouble();
                return true;
            }
            result = BigDouble.Zero;
            return false;
        }

        public static BigInteger ParseBig(byte[] raw, BigInteger defaultValue = default)
        {
            if (raw == null || raw.Length == 0)
                return defaultValue;
            return ParseBig(Encoding.UTF8.GetString(raw), defaultValue);
        }

        public static int CoerceToInt32(BigInteger v)
        {
            if (v > int.MaxValue) return int.MaxValue;
            if (v < int.MinValue) return int.MinValue;
            return (int)v;
        }

        /// <summary>For loot / balance formulas that need a double; may lose precision for astronomically large values.</summary>
        public static double ToDoubleLossy(BigInteger v)
        {
            if (v.IsZero)
                return 0d;

            // Fast path: small values convert directly with no allocation.
            if (v <= long.MaxValue && v >= long.MinValue)
                return (double)(long)v;
            if (v.Sign > 0 && v <= ulong.MaxValue)
                return (double)(ulong)v;

            var sign = v.Sign < 0 ? -1d : 1d;
            var abs = BigInteger.Abs(v);

            // Estimate decimal digit count without ToString allocation.
            // log10(2) ≈ 0.30103, so bits * 0.30103 ≈ digits.
            int bitLen = (int)abs.GetBitLength();
            int estDigits = (int)(bitLen * 0.3010299956639812d) + 1;

            if (estDigits > 308)
                return sign > 0 ? double.MaxValue : -double.MaxValue;

            // Extract leading 17 digits by dividing down.
            // 17 digits ensures we capture full double mantissa precision (53 bits ≈ 15.95 digits).
            const int sigDigits = 17;
            BigInteger divisor;
            if (estDigits <= sigDigits)
            {
                // Small enough to convert directly.
                divisor = BigInteger.One;
            }
            else
            {
                int shift = estDigits - sigDigits;
                divisor = Pow10[shift];
            }

            var mantissaBi = abs / divisor;
            // mantissaBi fits in ulong (max 17 digits < 2^64 ≈ 1.84e19)
            ulong mantissa = (ulong)mantissaBi;

            int exp = estDigits - (mantissa == 0 ? 1 : (int)Math.Log10(mantissa) + 1);
            if (exp >= Pow10Double.Length)
                exp = Pow10Double.Length - 1;

            double result = mantissa * Pow10Double[exp];
            if (double.IsNaN(result) || double.IsInfinity(result))
                return sign > 0 ? double.MaxValue : -double.MaxValue;
            return sign * result;
        }

        /// <summary>Bar-fill ratio in [0,1] for HP/MP/EXP gauges, computed from the raw
        /// decimal strings in log space so magnitudes past double range (10^308+)
        /// still yield sane ratios. Mirrors the FNA client's NumberDisplay.BarFillRatio.
        /// Returns -1 when either input is null or invalid.</summary>
        public static double RatioOf(string current, string max)
        {
            if (current == null || max == null)
                return -1;
            if (!TryParseBigDouble(current, out var cur))
                return -1;
            if (!TryParseBigDouble(max, out var mx))
                return -1;
            return RatioOf(cur, mx);
        }

        /// <summary>BigInteger convenience overload for RatioOf. Values that already
        /// lost magnitude through a BigInteger parse are treated as zero and yield 0.</summary>
        public static double RatioOf(BigInteger current, BigInteger max)
        {
            if (max.IsZero)
                return 0;
            if (current.IsZero || current.Sign < 0)
                return 0;
            if (current >= max)
                return 1;
            return RatioOf(BigDouble.FromBigInteger(current), BigDouble.FromBigInteger(max));
        }

        /// <summary>Log-space ratio (10^(log10cur - log10max)). Safe for magnitudes
        /// past double range; monotone, so current &lt; max maps into (0,1).</summary>
        public static double RatioOf(BigDouble cur, BigDouble max)
        {
            if (max.IsZero || cur.IsZero)
                return 0;
            if (cur.IsNegative)
                return 0;
            if (cur >= max)
                return 1;

            double lc, lm;
            if (!TryLog10(cur, out lc) || !TryLog10(max, out lm))
                return 0;

            double ratio = Math.Pow(10d, lc - lm);
            if (double.IsNaN(ratio) || ratio < 0)
                return 0;
            return Math.Min(1, Math.Max(0, ratio));
        }

        private static bool TryLog10(BigDouble v, out double log)
        {
            // log10 = exponent10 + log10(mantissa); exponent is a BigInteger and
            // may overflow a double for values past 10^10^308.
            double e;
            try { e = (double)v.ApproxExponent10; }
            catch (OverflowException) { e = v.ApproxExponent10.Sign < 0 ? double.MinValue : double.MaxValue; }
            double m = v.ApproxMantissa;
            if (m <= 0 || double.IsNaN(m) || double.IsInfinity(m))
            {
                log = 0;
                return false;
            }
            if (double.IsInfinity(e))
            {
                log = e > 0 ? double.MaxValue : double.MinValue;
                return true;
            }
            log = e + Math.Log10(m);
            return true;
        }

        /// <summary>Packet / ImportStats: accept int legacy or UTF decimal string.</summary>
        public static int ToInt32(object val, int def = 0)
        {
            if (val == null) return def;
            switch (val)
            {
                case int i: return i;
                case uint u: return u > int.MaxValue ? int.MaxValue : (int)u;
                case long l: return l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
                case BigInteger b: return CoerceToInt32(b);
                case string s: return CoerceToInt32(ParseBig(s, def));
                default:
                    try
                    {
                        return CoerceToInt32(BigInteger.Parse(Convert.ToString(val, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                    }
                    catch
                    {
                        return def;
                    }
            }
        }

        private sealed class ScaleEntry
        {
            public readonly BigInteger Exponent;      // sort key
            public readonly BigInteger MatchExponent; // digit-count threshold for FormatAbbreviated
            public readonly string Suffix;
            // Precomputed once — avoids ToString()/parse work on every Format call (was ~46KB/call).
            public readonly bool IsLiteral;           // literal power-of-ten scale ("1" followed by zeros)
            public readonly int LiteralGroupExp;      // digit-group exponent for literal scales
            public readonly int MatchExpInt;          // MatchExponent as int

            public ScaleEntry(string exponent, string suffix, string matchExponent = null)
            {
                Exponent = ParseBig(exponent, BigInteger.Zero);
                MatchExponent = matchExponent != null ? ParseBig(matchExponent, Exponent) : Exponent;
                Suffix = suffix;

                // Literal scales are "1" followed by zeros (7+ chars, not ending in "003").
                if (exponent.Length >= 7 && !exponent.EndsWith("003", StringComparison.Ordinal))
                {
                    IsLiteral = true;
                    LiteralGroupExp = Math.Max(0, ((exponent.Length - 1) / 3) * 3);
                    MatchExpInt = LiteralGroupExp;
                }
                else
                {
                    IsLiteral = false;
                    MatchExpInt = int.Parse(matchExponent ?? exponent, CultureInfo.InvariantCulture);
                }
            }
        }

        private static readonly ScaleEntry[] AbbrevScales =
        {
             new ScaleEntry("1" + new string('0', 462), "YZCePi", "462"),
             new ScaleEntry("1" + new string('0', 459), "XZCePi", "459"),
             new ScaleEntry("1" + new string('0', 456), "WZCePi", "456"),
             new ScaleEntry("1" + new string('0', 453), "VZCePi", "453"),
             new ScaleEntry("1" + new string('0', 450), "UZCePi", "450"),
             new ScaleEntry("1" + new string('0', 447), "TZCePi", "447"),
             new ScaleEntry("1" + new string('0', 444), "SZCePi", "444"),
             new ScaleEntry("1" + new string('0', 441), "RZCePi", "441"),
             new ScaleEntry("1" + new string('0', 438), "QZCePi", "438"),
             new ScaleEntry("1" + new string('0', 435), "PZCePi", "435"),
             new ScaleEntry("1" + new string('0', 432), "NZCePi", "432"),
             new ScaleEntry("1" + new string('0', 429), "MZCePi", "429"),
             new ScaleEntry("1" + new string('0', 426), "LZCePi", "426"),
             new ScaleEntry("1" + new string('0', 423), "KZCePi", "423"),
             new ScaleEntry("1" + new string('0', 420), "JZCePi", "420"),
             new ScaleEntry("1" + new string('0', 417), "IZCePi", "417"),
             new ScaleEntry("1" + new string('0', 414), "HZCePi", "414"),
             new ScaleEntry("1" + new string('0', 411), "GZCePi", "411"),
             new ScaleEntry("1" + new string('0', 408), "FZCePi", "408"),
             new ScaleEntry("1" + new string('0', 405), "EZCePi", "405"),
             new ScaleEntry("1" + new string('0', 402), "DZCePi", "402"),
             new ScaleEntry("1" + new string('0', 399), "CZCePi", "399"),
             new ScaleEntry("1" + new string('0', 396), "BZCePi", "396"),
             new ScaleEntry("1" + new string('0', 393), "AZCePi", "393"),
             new ScaleEntry("1" + new string('0', 390), "AAZCePi", "390"),
             new ScaleEntry("1" + new string('0', 387), "ZCePi", "387"),
             new ScaleEntry("1" + new string('0', 384), "YCePi", "384"),
             new ScaleEntry("1" + new string('0', 381), "WCePi", "381"),
             new ScaleEntry("1" + new string('0', 378), "VCePi", "378"),
             new ScaleEntry("1" + new string('0', 375), "QCePi", "375"),
             new ScaleEntry("1" + new string('0', 372), "TCePi", "372"),
             new ScaleEntry("1" + new string('0', 369), "UCePi", "369"),
             new ScaleEntry("1" + new string('0', 366), "DCePi", "366"),
             new ScaleEntry("1" + new string('0', 363), "XCePi", "363"),
             new ScaleEntry("1" + new string('0', 360), "HCePi", "360"),
             new ScaleEntry("1" + new string('0', 357), "CePi", "357"),
             new ScaleEntry("1" + new string('0', 354), "DePi", "354"),
             new ScaleEntry("1" + new string('0', 351), "Pi", "351"),
             new ScaleEntry("1" + new string('0', 348), "CeNa", "348"),
             new ScaleEntry("1" + new string('0', 345), "DeNa", "345"),
             new ScaleEntry("1" + new string('0', 342), "Na", "342"),
             new ScaleEntry("1" + new string('0', 339), "CeMc", "339"),
             new ScaleEntry("1" + new string('0', 336), "DeMc", "336"),
             new ScaleEntry("1" + new string('0', 333), "Mc", "333"),
             new ScaleEntry("1" + new string('0', 330), "NiMi", "350"),
             new ScaleEntry("1" + new string('0', 327), "OtMi", "350"),
             new ScaleEntry("1" + new string('0', 324), "SiMi", "350"),
             new ScaleEntry("1" + new string('0', 321), "SeMi", "350"),
             new ScaleEntry("1" + new string('0', 318), "QiMi", "350"),
             new ScaleEntry("1" + new string('0', 315), "QaMi", "350"),
             new ScaleEntry("1" + new string('0', 312), "TrMi", "350"),
             new ScaleEntry("1" + new string('0', 309), "DuMi", "350"),
             new ScaleEntry("1" + new string('0', 306), "CeMi", "350"),
             new ScaleEntry("1" + new string('0', 303), "NgMi", "350"),
             new ScaleEntry("1" + new string('0', 300), "OgMi", "350"),
             new ScaleEntry("1" + new string('0', 297), "SgMi", "350"),
             new ScaleEntry("1" + new string('0', 294), "sgMi", "350"),
             new ScaleEntry("1" + new string('0', 291), "QgMi", "350"),
             new ScaleEntry("1" + new string('0', 288), "qgMi", "350"),
             new ScaleEntry("1" + new string('0', 285), "TgMi", "350"),
             new ScaleEntry("1" + new string('0', 282), "TVtMi", "350"),
             new ScaleEntry("1" + new string('0', 279), "VtMi", "350"),
             new ScaleEntry("1" + new string('0', 276), "DeMi", "350"),
             new ScaleEntry("1" + new string('0', 273), "NoMi", "350"),
             new ScaleEntry("1" + new string('0', 270), "OcMi", "350"),
             new ScaleEntry("1" + new string('0', 267), "SpMi", "350"),
             new ScaleEntry("1" + new string('0', 264), "SxMi", "350"),
             new ScaleEntry("1" + new string('0', 261), "QnMi", "350"),
             new ScaleEntry("1" + new string('0', 258), "QdMi", "350"),
             new ScaleEntry("1" + new string('0', 255), "TMi", "350"),
             new ScaleEntry("1" + new string('0', 252), "DMi", "350"),
             new ScaleEntry("1" + new string('0', 249), "Mi", "350"),
             new ScaleEntry("1" + new string('0', 246), "Ni", "246"),
             new ScaleEntry("1" + new string('0', 243), "Ot", "243"),
             new ScaleEntry("1" + new string('0', 240), "Si", "240"),
             new ScaleEntry("1" + new string('0', 237), "Se", "237"),
             new ScaleEntry("1" + new string('0', 234), "Qi", "234"),
             new ScaleEntry("1" + new string('0', 231), "Qa", "231"),
             new ScaleEntry("1" + new string('0', 228), "Tr", "228"),
             new ScaleEntry("1" + new string('0', 225), "Du", "225"),
             new ScaleEntry("1" + new string('0', 222), "Ce", "222"),
             new ScaleEntry("1" + new string('0', 219), "Ng", "219"),
             new ScaleEntry("1" + new string('0', 216), "Og", "216"),
             new ScaleEntry("1" + new string('0', 213), "Sg", "213"),
             new ScaleEntry("1" + new string('0', 210), "Nosg", "210"),
             new ScaleEntry("1" + new string('0', 207), "Ocsg", "207"),
             new ScaleEntry("1" + new string('0', 204), "Spsg", "204"),
             new ScaleEntry("1" + new string('0', 201), "Sxsg", "201"),
             new ScaleEntry("1" + new string('0', 198), "Qnsg", "198"),
             new ScaleEntry("1" + new string('0', 195), "Qdsg", "195"),
             new ScaleEntry("1" + new string('0', 192), "Tsg", "192"),
             new ScaleEntry("1" + new string('0', 189), "Dsg", "189"),
             new ScaleEntry("1" + new string('0', 186), "Usg", "186"),
             new ScaleEntry("1" + new string('0', 183), "sg", "183"),
             new ScaleEntry("1" + new string('0', 180), "NoQg", "180"),
             new ScaleEntry("1" + new string('0', 177), "OcQg", "177"),
             new ScaleEntry("1" + new string('0', 174), "SpQg", "174"),
             new ScaleEntry("1" + new string('0', 171), "SxQg", "171"),
             new ScaleEntry("1" + new string('0', 168), "QnQg", "168"),
             new ScaleEntry("1" + new string('0', 165), "QdQg", "165"),
             new ScaleEntry("1" + new string('0', 162), "TQg", "162"),
             new ScaleEntry("1" + new string('0', 159), "DQg", "159"),
             new ScaleEntry("1" + new string('0', 156), "UQg", "156"),
             new ScaleEntry("1" + new string('0', 153), "Qg", "153"),
             new ScaleEntry("1" + new string('0', 150), "Noqg", "150"),
             new ScaleEntry("1" + new string('0', 147), "Ocqg", "147"),
             new ScaleEntry("1" + new string('0', 144), "Spqg", "144"),
             new ScaleEntry("1" + new string('0', 141), "Sxqg", "141"),
             new ScaleEntry("1" + new string('0', 138), "Qnqg", "138"),
             new ScaleEntry("1" + new string('0', 135), "Qdqg", "135"),
             new ScaleEntry("1" + new string('0', 132), "Tqg", "132"),
             new ScaleEntry("1" + new string('0', 129), "Dqg", "129"),
             new ScaleEntry("1" + new string('0', 126), "Uqg", "126"),
             new ScaleEntry("1" + new string('0', 123), "qg", "123"),
             new ScaleEntry("1" + new string('0', 120), "NoTg", "120"),
             new ScaleEntry("1" + new string('0', 117), "OcTg", "117"),
             new ScaleEntry("1" + new string('0', 114), "SpTg", "114"),
             new ScaleEntry("1" + new string('0', 111), "SxTg", "111"),
             new ScaleEntry("1" + new string('0', 108), "QnTg", "108"),
             new ScaleEntry("1" + new string('0', 105), "QdTg", "105"),
             new ScaleEntry("1" + new string('0', 102), "TTg", "102"),
             new ScaleEntry("1" + new string('0', 99), "DTg", "99"),
             new ScaleEntry("1" + new string('0', 96), "UTg", "96"),
             new ScaleEntry("1" + new string('0', 93), "Tg", "93"),
             new ScaleEntry("1" + new string('0', 90), "NoVt", "90"),
             new ScaleEntry("1" + new string('0', 87), "OcVt", "87"),
             new ScaleEntry("1" + new string('0', 84), "SpVt", "84"),
             new ScaleEntry("1" + new string('0', 81), "SxVt", "81"),
             new ScaleEntry("1" + new string('0', 78), "QnVt", "78"),
             new ScaleEntry("1" + new string('0', 75), "QdVt", "75"),
             new ScaleEntry("1" + new string('0', 72), "TVt", "72"),
             new ScaleEntry("1" + new string('0', 69), "DVt", "69"),
             new ScaleEntry("1" + new string('0', 66), "UVt", "66"),
             new ScaleEntry("1" + new string('0', 63), "Vt", "63"),
             new ScaleEntry("1" + new string('0', 60), "NoDe", "60"),
             new ScaleEntry("1" + new string('0', 57), "OcDe", "57"),
             new ScaleEntry("1" + new string('0', 54), "SpDe", "54"),
             new ScaleEntry("1" + new string('0', 51), "SxDe", "51"),
             new ScaleEntry("1" + new string('0', 48), "QnDe", "48"),
             new ScaleEntry("1" + new string('0', 45), "QdDe", "45"),
             new ScaleEntry("1" + new string('0', 42), "TDe", "42"),
             new ScaleEntry("1" + new string('0', 39), "DDe", "39"),
             new ScaleEntry("1" + new string('0', 36), "UDe", "36"),
             new ScaleEntry("1" + new string('0', 33), "De", "33"),
             new ScaleEntry("1" + new string('0', 30), "No", "30"),
             new ScaleEntry("1" + new string('0', 27), "Oc", "27"),
             new ScaleEntry("1" + new string('0', 24), "Sp", "24"),
             new ScaleEntry("1" + new string('0', 21), "Sx", "21"),
             new ScaleEntry("1" + new string('0', 18), "Qn", "18"),
             new ScaleEntry("1" + new string('0', 15), "Qd", "15"),
             new ScaleEntry("1" + new string('0', 12), "T", "12"),
             new ScaleEntry("1" + new string('0', 9), "B", "9"),
             new ScaleEntry("1" + new string('0', 6), "M", "6"),
             new ScaleEntry("1" + new string('0', 3), "k", "3")
        };

        // Static constructor — only use AbbrevScales so suffixes match what the client knows.
        // GenerateExtendedScales uses server-only names (GKappa, Alpha, etc.) the client can't render.
        static BigIntUtils()
        {
            var combined = new System.Collections.Generic.List<ScaleEntry>(AbbrevScales);
            combined.Sort((a, b) => b.Exponent.CompareTo(a.Exponent));
            _combinedScales = combined.ToArray();
            _bestIndexByDigitExp = BuildBestIndexMap();
        }
        
        private static ScaleEntry[] _combinedScales;
        private static int[] _bestIndexByDigitExp;
        
        // Accessor that returns combined scales
        private static ScaleEntry[] AllScales => _combinedScales ?? AbbrevScales;

        /// <summary>Precomputed digitExp → chosen scale index (old scan semantics, O(1) per call).</summary>
        private static int[] BuildBestIndexMap()
        {
            const int max = 499; // covers digitExp 0..498 (500+ digit numbers)
            var map = new int[max];
            for (int d = 0; d < max; d++)
            {
                int idx = _combinedScales.Length - 1; // fallback: smallest scale (k)
                for (int i = 0; i < _combinedScales.Length; i++)
                {
                    var s = _combinedScales[i];
                    int threshold = s.IsLiteral ? s.LiteralGroupExp : s.MatchExpInt;
                    if (d >= threshold) { idx = i; break; }
                }
                map[d] = idx;
            }
            return map;
        }

        public static string FormatAbbreviated(BigInteger value, int decimals = 2)
        {
            string ds = value.ToString(CultureInfo.InvariantCulture);
            var s = ds.AsSpan();
            bool neg = s.Length > 0 && s[0] == '-';
            if (neg) s = s.Slice(1);
            return FormatAbbreviatedCore(s, neg, decimals);
        }

        public static string FormatAbbreviated(string decimalString, int decimals = 2)
        {
            if (decimalString == null) return "0";
            var s = decimalString.AsSpan().Trim();
            if (s.Length == 0) return "0";

            bool neg = false;
            if (s[0] == '-') { neg = true; s = s.Slice(1); }
            else if (s[0] == '+') s = s.Slice(1);
            if (s.Length == 0) return "0";
            if (!IsDigitsRun(s)) return "0";

            int i = 0;
            while (i < s.Length && s[i] == '0') i++;
            if (i >= s.Length) return "0";
            return FormatAbbreviatedCore(s.Slice(i), neg, decimals);
        }

        private static string FormatAbbreviatedCore(ReadOnlySpan<char> digits, bool neg, int decimals)
        {
            if (digits.Length <= 3)
            {
                Span<char> small = stackalloc char[8];
                int w = 0;
                if (neg) small[w++] = '-';
                digits.CopyTo(small.Slice(w));
                w += digits.Length;
                return small.Slice(0, w).ToString();
            }

            // digitExp: the 10^(3n) bucket this number falls in, by digit count
            int digitExp = ((int)digits.Length - 1) / 3 * 3;
            var scales = AllScales;
            var top = scales[0];
            int greatestGroup = top.IsLiteral ? top.LiteralGroupExp : top.MatchExpInt;
            if (digitExp > greatestGroup)
            {
                // Past the largest table entry (~10^462): simple scientific notation
                // ("1.234e465"), never clamped to the top suffix — mirrors the FNA client.
                int exp = digits.Length - 1;
                int sciHead = digits.Length >= 3 ? 3 : digits.Length;
                var sciFrac = ReadOnlySpan<char>.Empty;
                if (decimals > 0 && sciHead < digits.Length)
                {
                    int take = Math.Min(decimals, digits.Length - sciHead);
                    var f = digits.Slice(sciHead, take);
                    while (f.Length > 0 && f[f.Length - 1] == '0') f = f.Slice(0, f.Length - 1);
                    sciFrac = f;
                }
                string suffix = "e" + exp.ToString(CultureInfo.InvariantCulture);
                int totalLen = (neg ? 1 : 0) + sciHead + (sciFrac.Length > 0 ? 1 + sciFrac.Length : 0) + suffix.Length;
                if (totalLen <= 256)
                {
                    Span<char> buf = stackalloc char[256];
                    int w = 0;
                    if (neg) buf[w++] = '-';
                    digits.Slice(0, sciHead).CopyTo(buf.Slice(w)); w += sciHead;
                    if (sciFrac.Length > 0) { buf[w++] = '.'; sciFrac.CopyTo(buf.Slice(w)); w += sciFrac.Length; }
                    suffix.AsSpan().CopyTo(buf.Slice(w)); w += suffix.Length;
                    return buf.Slice(0, w).ToString();
                }
                return (neg ? "-" : "") + digits.Slice(0, sciHead).ToString()
                     + (sciFrac.Length > 0 ? "." + sciFrac.ToString() : "") + suffix;
            }
            var chosen = scales[digitExp >= _bestIndexByDigitExp.Length ? 0 : _bestIndexByDigitExp[digitExp]];

            // Determine how many integer digits to show before the suffix
            int intDigits = digits.Length - (chosen.IsLiteral ? chosen.LiteralGroupExp : chosen.MatchExpInt);
            if (intDigits < 1) intDigits = 1;
            if (intDigits > digits.Length) intDigits = digits.Length;

            var frac = ReadOnlySpan<char>.Empty;
            if (decimals > 0 && intDigits < digits.Length)
            {
                int take = Math.Min(decimals, digits.Length - intDigits);
                var f = digits.Slice(intDigits, take);
                while (f.Length > 0 && f[f.Length - 1] == '0') f = f.Slice(0, f.Length - 1);
                frac = f;
            }

            int total = (neg ? 1 : 0) + intDigits + (frac.Length > 0 ? 1 + frac.Length : 0) + chosen.Suffix.Length;
            if (total <= 256)
            {
                Span<char> buf = stackalloc char[256];
                int w = 0;
                if (neg) buf[w++] = '-';
                digits.Slice(0, intDigits).CopyTo(buf.Slice(w)); w += intDigits;
                if (frac.Length > 0) { buf[w++] = '.'; frac.CopyTo(buf.Slice(w)); w += frac.Length; }
                chosen.Suffix.AsSpan().CopyTo(buf.Slice(w)); w += chosen.Suffix.Length;
                return buf.Slice(0, w).ToString();
            }

            // Rare fallback for oversized inputs
            return (neg ? "-" : "") + digits.Slice(0, intDigits).ToString() + (frac.Length > 0 ? "." + frac.ToString() : "") + chosen.Suffix;
        }

        private static bool IsDigitsRun(ReadOnlySpan<char> s)
        {
            for (int i = 0; i < s.Length; i++)
                if ((uint)(s[i] - '0') > 9) return false;
            return true;
        }

        /// <summary>
        /// Compares two abbreviated level strings (e.g. "1QaMi", "1Absolute") by their true exponent.
        /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
        /// Falls back to BigInteger comparison for small values that parse exactly.
        /// </summary>
        public static int CompareAbbreviated(string a, string b)
        {
            var expA = GetAbbreviatedExponent(a);
            var expB = GetAbbreviatedExponent(b);
            if (expA != expB)
                return expA.CompareTo(expB);
            // Same exponent tier: compare the mantissa numerically
            var bigA = ParseBigWithSuffix(a);
            var bigB = ParseBigWithSuffix(b);
            return bigA.CompareTo(bigB);
        }

        /// <summary>Returns the scale exponent for an abbreviated string, or -1 if not recognized.</summary>
        public static BigInteger GetSuffixExponent(string s) => GetAbbreviatedExponent(s);
        private static BigInteger GetAbbreviatedExponent(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return BigInteger.MinusOne;
            var raw = s.Trim();
            // Pure integer: exponent is digit count - 1
            if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
                return plain.IsZero ? BigInteger.Zero : new BigInteger((long)plain.ToString().Length - 1);
            // Find suffix portion
            var split = 0;
            while (split < raw.Length && (raw[split] >= '0' && raw[split] <= '9' || raw[split] == '.'))
                split++;
            if (split >= raw.Length) return BigInteger.MinusOne;
            var suffix = NormalizeSuffixToken(raw.Substring(split).Trim());
            var scales = AllScales;
            for (var i = 0; i < scales.Length; i++)
            {
                if (!string.Equals(scales[i].Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Literal power-of-ten suffixes use group exponent semantics;
                // sentinel/direct-shift use MatchExponent as the digit-count threshold.
                return new BigInteger(scales[i].IsLiteral ? scales[i].LiteralGroupExp : scales[i].MatchExpInt);
            }
            return BigInteger.MinusOne;
        }

        public static BigInteger ParseBigWithSuffix(string s, BigInteger defaultValue = default)
        {
            if (string.IsNullOrWhiteSpace(s))
                return defaultValue;

            var raw = s.Trim();
            if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
                return plain;

            var sign = 1;
            if (raw.StartsWith("-", StringComparison.Ordinal))
            {
                sign = -1;
                raw = raw.Substring(1).Trim();
            }
            else if (raw.StartsWith("+", StringComparison.Ordinal))
            {
                raw = raw.Substring(1).Trim();
            }

            var split = 0;
            while (split < raw.Length)
            {
                var c = raw[split];
                if ((c >= '0' && c <= '9') || c == '.')
                    split++;
                else
                    break;
            }
            if (split <= 0 || split >= raw.Length)
                return defaultValue;

            var numPart = raw.Substring(0, split);
            var suffix = raw.Substring(split).Trim();
            if (suffix.Length == 0)
                return defaultValue;
            suffix = NormalizeSuffixToken(suffix);
            if (suffix.Length == 0)
                return defaultValue;

            ScaleEntry matchedScale = null;
            var scales = AllScales;
            for (var i = 0; i < scales.Length; i++)
            {
                if (string.Equals(scales[i].Suffix, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    matchedScale = scales[i];
                    break;
                }
            }
            if (matchedScale == null)
                return defaultValue;

            if (!decimal.TryParse(numPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mantissa))
                return defaultValue;
            if (mantissa < 0) mantissa = -mantissa;

            var mantissaText = mantissa.ToString(CultureInfo.InvariantCulture);
            var dot = mantissaText.IndexOf('.');
            var fracDigits = 0;
            string digitsOnly;
            if (dot >= 0)
            {
                fracDigits = mantissaText.Length - dot - 1;
                digitsOnly = mantissaText.Remove(dot, 1);
            }
            else
            {
                digitsOnly = mantissaText;
            }

            if (!BigInteger.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sig))
                return defaultValue;

            // Determine the actual power-of-10 shift for this suffix.
            // AbbrevScales stores Exponent in three distinct ways:
            //   A) Small direct-shift: k=3, M=6, B=9 ... Ng=273, sg=183 (value IS the shift)
            //   B) Literal threshold: Mc=1000000, Na=1000000000, ZCePi=10^43 etc
            //      (value is the threshold; shift = string length - 1)
            //   C) Mi-family sentinel: Mi=3003, NiMi=2700003 (ends in "003", ultra-large)
            //      Cannot Pow safely; return sentinel 10^350.
            // Use MatchExponent for the actual shift calculation
// Sentinel sort keys (MatchExponent >= 3000): MatchExponent holds real digit-count
            if (matchedScale.MatchExpInt >= 3000)
                return sign < 0 ? -Pow10[350] : Pow10[350];

            const int MaxSafeShift = 350;
            BigInteger result;
            var fracDigitsBi = new BigInteger(fracDigits);
            // Literal power-of-ten scales map to grouped exponent; direct-shift/sentinel use MatchExponent
            var expShift = new BigInteger(matchedScale.IsLiteral ? matchedScale.LiteralGroupExp : matchedScale.MatchExpInt);
            if (expShift >= fracDigitsBi)
            {
                var diff = expShift - fracDigitsBi;
                var shift = diff > MaxSafeShift ? MaxSafeShift : (int)diff;
                result = sig * Pow10[shift];
            }
            else
            {
                var diff = fracDigitsBi - expShift;
                var shift = diff > MaxSafeShift ? MaxSafeShift : (int)diff;
                result = sig / Pow10[shift];
            }

            return sign < 0 ? -result : result;
        }

        /// <summary>
        /// Accept common player-entered shorthand forms:
        /// - trailing punctuation ("No.")
        /// - accidental spacing ("No .")
        /// Matching remains exact against known suffix tokens after normalization.
        /// </summary>
        private static string NormalizeSuffixToken(string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return string.Empty;

            var token = suffix.Trim();
            while (token.Length > 0)
            {
                var c = token[token.Length - 1];
                if (c == '.' || c == ',' || c == ';' || c == ':')
                {
                    token = token.Substring(0, token.Length - 1).TrimEnd();
                    continue;
                }
                break;
            }
            return token;
        }
    }
}
