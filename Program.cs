// Benchmark harness for the five BigInt display/math files:
//   BigIntChunked.cs, BigIntUtils.cs, NumberDisplay.cs, NumberDisplayScales.cs, StringInt.cs
// Prints per-benchmark elapsed time, ops/sec, allocated bytes, CPU usage and RAM delta,
// plus a process-level CPU/RAM summary and the highest/lowest numbers in raw + abbreviated form.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using common;
using MantissaSystem;
using VortexClient.Core;

internal static class Program
{
    private static readonly Process Proc = Process.GetCurrentProcess();
    private static readonly Random Rng = new Random(1234);

    private static BigInteger _min = BigInteger.Zero;
    private static BigInteger _max = BigInteger.Zero;
    private static readonly object TrackSync = new();
    private static bool _diagnosticMode;

    private static BigInteger _dpsSampleAvg;
    private static double _dpsSampleAvgD;
    private static double _dpsSampleAttacksPerSecond;
    private static string _dpsSampleDpsStr;
    private static bool _hasDpsSample;

    private static string _damageRollLine;
    private static bool _isTower;

    private static BigInteger _bigCalcSink;
    private static Mantissa _mantissaCalcSink;
    private static double _dpsCalcSink;
    private static Mantissa _mantissaDpsSink;

    private static double ParseDoubleWithSuffix(string s, double defaultValue = 0.0)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var raw = s.Trim();
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain)) return plain;

        var bi = BigIntUtils.ParseBigWithSuffix(raw, BigInteger.Zero);
        if (!bi.IsZero)
            return BigIntUtils.ToDoubleLossy(bi);
        return defaultValue;
    }

    private static void Main(string[] args)
    {
        _diagnosticMode = args.Any(a => a.Equals("--diag", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--diagnostic", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--trace", StringComparison.OrdinalIgnoreCase))
            || string.Equals(Environment.GetEnvironmentVariable("BIGINTBENCH_DIAGNOSTIC"), "1", StringComparison.OrdinalIgnoreCase);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== BigInt benchmark: BigIntChunked / BigIntUtils / NumberDisplay / NumberDisplayScales / StringInt ===");
        if (_diagnosticMode)
        {
            PrintEnvironmentHeader();
        }
        Console.WriteLine();

        // ─── Damage scaling interaction (before benchmarks, so alloc/CPU are measured with these) ────────────────
        string? minInput = args.Length > 0 ? args[0] : null;
        string? maxInput = args.Length > 1 ? args[1] : null;
        string? attackInput = args.Length > 2 ? args[2] : null;
        string? rateInput = args.Length > 3 ? args[3] : null;
        string? dexInput = args.Length > 4 ? args[4] : null;

        if (minInput == null)
        {
            Console.Write("Enter base damage min (e.g. 10): ");
            minInput = Console.ReadLine();
        }
        if (maxInput == null)
        {
            Console.Write("Enter base damage max (e.g. 20): ");
            maxInput = Console.ReadLine();
        }
        if (attackInput == null)
        {
            Console.Write("Enter attack value (e.g. 100): ");
            attackInput = Console.ReadLine();
        }
        if (rateInput == null)
        {
            Console.Write("Enter base attack speed (weapon RateOfFire, e.g. 1.0): ");
            rateInput = Console.ReadLine();
        }
        if (dexInput == null)
        {
            Console.Write("Enter dexterity (0-1000, e.g. 50): ");
            dexInput = Console.ReadLine();
        }

        // Parse BigInt with tower fallback (BigExp for 1gp / 1e1e100)
        BigInteger baseMin, baseMax, attack;
        VortexClient.Core.Numbers.BigExp baseMinExp, baseMaxExp, attackExp;
        bool isTower = false;
        string ParseBIExp(string s, BigInteger defBI, VortexClient.Core.Numbers.BigExp defExp, out BigInteger bi, out VortexClient.Core.Numbers.BigExp exp, ref bool towerFlag)
        {
            string t = s?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(t)) { bi = defBI; exp = defExp; return t; }
            if (BigInteger.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) { bi = v; exp = VortexClient.Core.Numbers.BigExp.Parse(t); return t; }
            if (VortexClient.Core.Numbers.BigExp.TryParse(t, out var e)) { bi = BigInteger.Zero; exp = e; towerFlag = true; return t; }
            // try BigIntUtils suffix like 1M
            try { var w = BigIntUtils.ParseBigWithSuffix(t, defBI); bi = w; exp = VortexClient.Core.Numbers.BigExp.Parse(w.ToString()); return t; } catch {}
            bi = defBI; exp = defExp; return t;
        }
        bool towerFlag = false;
        ParseBIExp(minInput, new BigInteger(10), VortexClient.Core.Numbers.BigExp.Parse("10"), out baseMin, out baseMinExp, ref towerFlag);
        ParseBIExp(maxInput, new BigInteger(20), VortexClient.Core.Numbers.BigExp.Parse("20"), out baseMax, out baseMaxExp, ref towerFlag);
        ParseBIExp(attackInput, new BigInteger(100), VortexClient.Core.Numbers.BigExp.Parse("100"), out attack, out attackExp, ref towerFlag);
        isTower = towerFlag || (minInput?.IndexOf("gp", StringComparison.OrdinalIgnoreCase) >= 0) || (maxInput?.IndexOf("gp", StringComparison.OrdinalIgnoreCase) >= 0) || (attackInput?.IndexOf("gp", StringComparison.OrdinalIgnoreCase) >= 0);
        double rateOfFire = string.IsNullOrWhiteSpace(rateInput) ? 1.0 : ParseDoubleWithSuffix(rateInput, 1.0);
        double dex = string.IsNullOrWhiteSpace(dexInput) ? 50.0 : ParseDoubleWithSuffix(dexInput, 50.0);

        BigInteger dmgMin = baseMin * attack / 100;
        BigInteger dmgMax = baseMax * attack / 100;
        if (dmgMax < dmgMin) dmgMax = dmgMin;
        // Tower path via BigExp (handles 1gp = 1e1e100 beyond BigInteger)
        VortexClient.Core.Numbers.BigExp dmgMinExp = VortexClient.Core.Numbers.BigExp.Multiply(baseMinExp, attackExp);
        dmgMinExp = VortexClient.Core.Numbers.BigExp.DivideByInt(dmgMinExp, 100);
        VortexClient.Core.Numbers.BigExp dmgMaxExp = VortexClient.Core.Numbers.BigExp.Multiply(baseMaxExp, attackExp);
        dmgMaxExp = VortexClient.Core.Numbers.BigExp.DivideByInt(dmgMaxExp, 100);
        if (dmgMaxExp.CompareTo(dmgMinExp) < 0) dmgMaxExp = dmgMinExp;
        // Mirrors Player.as attackFrequency(): MIN 0.0015 + (min(dex,1000)/75)*(MAX-MIN), period = 1/freq * 1/RateOfFire
        const double MIN_ATTACK_FREQ = 0.0015;
        const double MAX_ATTACK_FREQ = 0.008;
        double effectiveDex = Math.Min(dex, 1000.0);
        double attackFreq = MIN_ATTACK_FREQ + (effectiveDex / 75.0) * (MAX_ATTACK_FREQ - MIN_ATTACK_FREQ);
        double attackPeriodMs = (1.0 / attackFreq) * (1.0 / Math.Max(0.01, rateOfFire));
        double attacksPerSecond = 1000.0 / attackPeriodMs; // = attackFreq * rateOfFire * 1000

        BigInteger avgDamage = (dmgMin + dmgMax) / 2;
        double avgD = BigIntUtils.ToDoubleLossy(avgDamage);
        double expectedDps = avgD * attacksPerSecond;
        string dpsDisplay = double.IsInfinity(avgD) || double.IsInfinity(expectedDps) || double.IsNaN(expectedDps)
            ? $"{BigIntUtils.FormatAbbreviated(avgDamage * new BigInteger((long)Math.Max(1, attacksPerSecond)))} (approx BigInt)"
            : $"{expectedDps:F2}";
        // Tower DPS via BigExp (handles 1gp scale)
        VortexClient.Core.Numbers.BigExp avgExp = VortexClient.Core.Numbers.BigExp.DivideByInt(VortexClient.Core.Numbers.BigExp.Add(dmgMinExp, dmgMaxExp), 2);
        VortexClient.Core.Numbers.BigExp dpsExp = VortexClient.Core.Numbers.BigExp.Multiply(avgExp, VortexClient.Core.Numbers.BigExp.Parse(attacksPerSecond.ToString("R", CultureInfo.InvariantCulture)));

        if (isTower)
        {
            Console.WriteLine($"Computed damage range [BigInt]: N/A (tower input beyond BigInteger)");
            Console.WriteLine($"Computed damage range [Tower BigExp]: {dmgMinExp.ToAbbreviated()} - {dmgMaxExp.ToAbbreviated()} (base {baseMinExp.ToAbbreviated()}-{baseMaxExp.ToAbbreviated()} * attack {attackExp.ToAbbreviated()}/100) sci {dmgMinExp.ToScientific(2)} - {dmgMaxExp.ToScientific(2)}");
        }
        else Console.WriteLine($"Computed damage range [BigInt]: {dmgMin} - {dmgMax} (base {baseMin}-{baseMax} * attack {attack}/100)");
        Console.WriteLine($"Attack freq (dex {dex} -> {effectiveDex}/75): {attackFreq:F6} per ms, RateOfFire {rateOfFire:F2}, period {attackPeriodMs:F2} ms");
        Console.WriteLine($"Attacks per second: {attacksPerSecond:F3} att/s");
        if (isTower) Console.WriteLine($"Expected DPS [Tower]: {dpsExp.ToAbbreviated()} sci {dpsExp.ToScientific(2)} (avg {avgExp.ToAbbreviated()} * {attacksPerSecond:F3})");
        else Console.WriteLine($"Expected DPS [BigInt]: {dpsDisplay} (avg {avgDamage} * {attacksPerSecond:F3})");
        Console.WriteLine();

        PrintMantissaProofOfConcept(isTower, baseMin, baseMax, attack, dmgMin, dmgMax, avgDamage, attacksPerSecond, expectedDps, dpsDisplay);

        Stopwatch runSw = Stopwatch.StartNew();
        string[] data = BuildTestData();
        foreach (string s in data) Track(s);

        RunSanityChecks();

        Console.WriteLine();
        Console.WriteLine($"{"Benchmark",-48}{"Iters",10}{"Total ms",12}{"Ops/sec",14}{"Alloc MB",10}{"Bytes/op",10}{"CPU %",8}{"RAM d MB",10}");
        Console.WriteLine(new string('-', 122));

        string a = new string('1', 100);
        string b = new string('9', 100);
        string big = "123" + new string('0', 347);      // 350 digits → Mi tier (10^350)
        string huge = new string('9', 500);               // beyond all scales → e498
        BigInteger bigInt = BigIntUtils.ParseBig(huge);

        Console.WriteLine("[NumberDisplay]");
        Bench("FormatBigInt (all data, small→huge)", Its(20000),
            i => NumberDisplay.FormatBigInt(data[i % data.Length]), silent: true);
        Bench("FormatBigInt (fixed 10^350 value)", Its(20000),
            i => NumberDisplay.FormatBigInt(big), silent: true);
        Bench("FormatBigInt span overload (fixed 10^350)", Its(20000),
            i => NumberDisplay.FormatBigInt(big.AsSpan()), silent: true);
        var cache = new FormattedNumberCache();
        cache.Get(big);
        Bench("FormattedNumberCache hit (unchanged value)", Its(100000),
            i => cache.Get(big));
        Bench("FormattedNumberCache miss (value changes each call)", Its(20000),
            i => cache.Get(data[i % data.Length]));
        Bench("CompareBigIntStrings", Its(200000),
            i => NumberDisplay.CompareBigIntStrings(data[i % data.Length], data[(i + 1) % data.Length]));
        Bench("AddBigIntStrings", Its(50000),
            i => NumberDisplay.AddBigIntStrings(data[i % data.Length], data[(i + 1) % data.Length]));
        Bench("SubtractBigIntStrings", Its(50000),
            i => NumberDisplay.SubtractBigIntStrings(data[i % data.Length], data[(i + 1) % data.Length]));
        Bench("MulBigStrByInt (n=999)", Its(50000),
            i => NumberDisplay.MulBigStrByInt(data[i % data.Length], 999));
        Bench("DivBigStrByInt (n=7)", Its(50000),
            i => NumberDisplay.DivBigStrByInt(data[i % data.Length], 7));
        Bench("ModBigIntStrByInt (n=7)", Its(50000),
            i => NumberDisplay.ModBigIntStrByInt(data[i % data.Length], 7));
        Bench("MulBigIntStrByFrac", Its(50000),
            i => NumberDisplay.MulBigIntStrByFrac(data[i % data.Length], 12345, 999));
        Bench("MulBigStrByBigStr (100x100 digits)", Its(2000),
            i => NumberDisplay.MulBigStrByBigStr(a, b));
        Bench("MulBigStrByBigStr (500x500 digits)", Its(50),
            i => NumberDisplay.MulBigStrByBigStr(huge, huge));
        Bench("BarFillRatio (val vs 500-digit max)", Its(100000),
            i => NumberDisplay.BarFillRatio(data[i % data.Length], huge));

        Console.WriteLine("[BigIntUtils]");
        Bench("FormatAbbreviated (BigInteger 500-digit)", Its(20000),
            i => BigIntUtils.FormatAbbreviated(bigInt));
        Bench("FormatAbbreviated (string 500-digit)", Its(20000),
            i => BigIntUtils.FormatAbbreviated(huge));
        Bench("ParseBig (string)", Its(100000),
            i => BigIntUtils.ParseBig(data[i % data.Length]));
        Bench("FormatAbbreviated + ParseBigWithSuffix round-trip", Its(50000),
            i =>
            {
                string f = BigIntUtils.FormatAbbreviated(data[i % data.Length]);
                BigIntUtils.ParseBigWithSuffix(f);
            });
        Bench("CompareAbbreviated (format 2 + compare)", Its(30000),
            i =>
            {
                string x = BigIntUtils.FormatAbbreviated(data[i % data.Length]);
                string y = BigIntUtils.FormatAbbreviated(data[(i + 1) % data.Length]);
                BigIntUtils.CompareAbbreviated(x, y);
            });
        Bench("ToDoubleLossy (500-digit)", Its(20000),
            i => BigIntUtils.ToDoubleLossy(bigInt));

        Console.WriteLine("[Local host loopback network profile]");
        Bench("Host loopback server→client abbreviated packet", Its(50000),
            i =>
            {
                string serverWire = BigIntUtils.FormatAbbreviated(data[i % data.Length]);
                BigInteger restored = BigIntUtils.ParseBigWithSuffix(serverWire);
                string clientRender = NumberDisplay.FormatBigInt(restored.ToString(CultureInfo.InvariantCulture));
                _ = serverWire;
                _ = restored;
                _ = clientRender;
            });
        Bench("Host loopback client→server concrete packet", Its(50000),
            i =>
            {
                string clientWire = NumberDisplay.FormatBigInt(data[i % data.Length]);
                BigInteger restored = BigIntUtils.ParseBig(clientWire);
                string serverResponse = BigIntUtils.FormatAbbreviated(restored);
                _ = clientWire;
                _ = restored;
                _ = serverResponse;
            });
        Bench("Host loopback echo request/response", Its(25000),
            i =>
            {
                string raw = data[i % data.Length];
                string serverWire = BigIntUtils.FormatAbbreviated(raw);
                BigInteger parsed = BigIntUtils.ParseBigWithSuffix(serverWire);
                string clientWire = NumberDisplay.FormatBigInt(parsed.ToString(CultureInfo.InvariantCulture));
                BigInteger recovered = BigIntUtils.ParseBig(clientWire);
                string serverRoundTrip = BigIntUtils.FormatAbbreviated(recovered);
                _ = serverRoundTrip;
            });

        Console.WriteLine();
        Console.WriteLine("[Damage Roll (game Shoot.cs logic)]");
        // User-scaled damage as BigInt (entered before benchmarks, so alloc/CPU are measured with these values)
        BigInteger dmgMinT = dmgMin;
        BigInteger dmgMaxT = dmgMax;
        BigInteger dmgMin100 = BigInteger.Parse("5" + new string('0', 99), CultureInfo.InvariantCulture);
        BigInteger dmgMax100 = dmgMin100 + 3000;
        BigInteger dmgMin500 = BigInteger.Parse("1" + new string('0', 499), CultureInfo.InvariantCulture);
        BigInteger dmgMax500 = dmgMin500 + 3000;
        BigInteger avgT = (dmgMinT + dmgMaxT) / 2;
        double avgTd = BigIntUtils.ToDoubleLossy(avgT);
        double dpsT = avgTd * attacksPerSecond;
        string dpsTStr = double.IsInfinity(avgTd) || double.IsInfinity(dpsT) ? $"{BigIntUtils.FormatAbbreviated(avgT * new BigInteger((long)Math.Max(1, attacksPerSecond)))} (approx BigInt)" : $"{dpsT:F2}";
        _isTower = isTower;
        if (isTower)
            _damageRollLine = $"  Tower damage {dmgMinExp.ToAbbreviated()} - {dmgMaxExp.ToAbbreviated()} sci {dmgMinExp.ToScientific(2)} - {dmgMaxExp.ToScientific(2)} avg {avgExp.ToAbbreviated()} towerDPS {dpsExp.ToAbbreviated()} sci {dpsExp.ToScientific(2)} (base {baseMinExp.ToAbbreviated()}-{baseMaxExp.ToAbbreviated()} * attack {attackExp.ToAbbreviated()}/100, baseAPS {rateOfFire:F2} * dex {dex} -> {attacksPerSecond:F3} att/s)";
        else
            _damageRollLine = $"  Using user BigInt damage {dmgMinT} - {dmgMaxT} (base {baseMin}-{baseMax} * attack {attack}/100), baseAPS {rateOfFire:F2} * dex {dex} (freq {attackFreq:F6}, period {attackPeriodMs:F1}ms) -> {attacksPerSecond:F3} att/s, DPS {dpsTStr} (avg {avgT})";
        Bench("Roll damage (user scaled)", Its(100000),
            i => RollDamage(Rng, dmgMinT, dmgMaxT, weak: false));
        Bench("Roll damage 100-digit + small span (Weak)", Its(50000),
            i => RollDamage(Rng, dmgMin100, dmgMax100, weak: true));
        Bench("Roll damage 500-digit + small span", Its(20000),
            i => RollDamage(Rng, dmgMin500, dmgMax500, weak: false));
        Bench("BigIntRandomBelow (user span)", Its(200000),
            i => BigIntRandomBelow(Rng, dmgMaxT - dmgMinT));
        int iterations = 100000;
        BigInteger totalDamage = BigInteger.Zero;
        for (int j = 0; j < iterations; j++) totalDamage += RollDamage(Rng, dmgMinT, dmgMaxT, weak: false);
        BigInteger avg = totalDamage / iterations;
        double avgD2 = BigIntUtils.ToDoubleLossy(avg);
        double dps = avgD2 * attacksPerSecond;
        string dpsStr2 = double.IsInfinity(avgD2) || double.IsInfinity(dps) ? $"{BigIntUtils.FormatAbbreviated(avg * new BigInteger((long)Math.Max(1, attacksPerSecond)))} (approx BigInt)" : $"{dps:F2}";
        _dpsSampleAvg = avg;
        _dpsSampleAvgD = avgD2;
        _dpsSampleAttacksPerSecond = attacksPerSecond;
        _dpsSampleDpsStr = dpsStr2;
        _hasDpsSample = true;
        if (isTower)
        {
            Bench("Tower damage scale (BigExp base*attack/100)", Its(200000),
                i => VortexClient.Core.Numbers.BigExp.DivideByInt(VortexClient.Core.Numbers.BigExp.Multiply(baseMinExp, attackExp), 100));
            Bench("Tower DPS (BigExp avg*att/s)", Its(100000),
                i => VortexClient.Core.Numbers.BigExp.Multiply(avgExp, VortexClient.Core.Numbers.BigExp.Parse(attacksPerSecond.ToString("R", CultureInfo.InvariantCulture))));
        }

        Console.WriteLine("[Same calculation: BigInt system vs Mantissa struct]");
        BigInteger bigCalcBase = baseMin;
        BigInteger bigCalcAttack = attack;
        BigInteger bigCalcAvg = avgDamage;
        Mantissa mantCalcBase = Mantissa.FromBigInteger(baseMin);
        Mantissa mantCalcAttack = Mantissa.FromBigInteger(attack);
        Mantissa mantCalcAvg = Mantissa.FromBigInteger(avgDamage);
        Mantissa mantCalcAps = Mantissa.FromDouble(attacksPerSecond);
        if (isTower)
        {
            Console.WriteLine("  Mantissa benchmarks skipped: inputs exceed the struct's int exponent10 range.");
        }
        else
        {
            Bench("[BigInt] damage calc base*attack/100", Its(200000),
                i => _bigCalcSink = (bigCalcBase * bigCalcAttack) / 100);
            Bench("[Mantissa] damage calc base*attack/100", Its(200000),
                i => _mantissaCalcSink = (mantCalcBase * mantCalcAttack) / 100);
            Bench("[BigInt] DPS calc avg*att/s (lossy double)", Its(200000),
                i => _dpsCalcSink = BigIntUtils.ToDoubleLossy(bigCalcAvg) * attacksPerSecond);
            Bench("[Mantissa] DPS calc avg*att/s (struct)", Its(200000),
                i => _mantissaDpsSink = mantCalcAvg * mantCalcAps);
        }

        Console.WriteLine("[Side-by-side: BigInt vs Mantissa — same operations, two systems]");
        BigInteger bigSBase = baseMin;
        BigInteger bigSAtk = attack;
        BigInteger bigSDmgMin = dmgMin;
        BigInteger bigSDmgMax = dmgMax;
        BigInteger bigSAvg = avgDamage;
        Mantissa mSBase = Mantissa.FromBigInteger(baseMin);
        Mantissa mSAtk = Mantissa.FromBigInteger(attack);
        Mantissa mSDmgMin = Mantissa.FromBigInteger(dmgMin);
        Mantissa mSDmgMax = Mantissa.FromBigInteger(dmgMax);
        Mantissa mSAvg = Mantissa.FromBigInteger(avgDamage);
        Mantissa mSAps = Mantissa.FromDouble(attacksPerSecond);
        if (isTower)
        {
            Console.WriteLine("  Skipped: tower inputs exceed Mantissa int exponent10 range.");
        }
        else
        {
            Bench("[BigInt] multiply base*attack", Its(300000),
                i => _bigCalcSink = bigSBase * bigSAtk);
            Bench("[Mantissa] multiply base*attack", Its(300000),
                i => _mantissaCalcSink = mSBase * mSAtk);
            Bench("[BigInt] divide by 100", Its(300000),
                i => _bigCalcSink = _bigCalcSink / 100);
            Bench("[Mantissa] divide by 100", Its(300000),
                i => _mantissaCalcSink = _mantissaCalcSink / 100);
            Bench("[BigInt] add min+max", Its(300000),
                i => _bigCalcSink = bigSDmgMin + bigSDmgMax);
            Bench("[Mantissa] add min+max", Its(300000),
                i => _mantissaCalcSink = mSDmgMin + mSDmgMax);
            Bench("[BigInt] divide by 2 (avg)", Its(300000),
                i => _bigCalcSink = _bigCalcSink / 2);
            Bench("[Mantissa] divide by 2 (avg)", Its(300000),
                i => _mantissaCalcSink = _mantissaCalcSink / 2);
            Bench("[BigInt] ToDoubleLossy(avg)", Its(300000),
                i => _dpsCalcSink = BigIntUtils.ToDoubleLossy(bigSAvg));
            Bench("[Mantissa] ToDouble(avg)", Its(300000),
                i => _dpsCalcSink = mSAvg.ToDouble());
            Bench("[BigInt] DPS = ToDoubleLossy * aps", Its(200000),
                i => _dpsCalcSink = BigIntUtils.ToDoubleLossy(bigSAvg) * attacksPerSecond);
            Bench("[Mantissa] DPS = struct mul aps", Its(200000),
                i => _mantissaDpsSink = mSAvg * mSAps);
            Bench("[BigInt] compare min vs max", Its(500000),
                i => _ = bigSDmgMin.CompareTo(bigSDmgMax));
            Bench("[Mantissa] compare min vs max", Its(500000),
                i => _ = mSDmgMin.CompareTo(mSDmgMax));
        }

        Console.WriteLine("[StringInt]");
        Bench("Parse (string → StringInt)", Its(100000),
            i => new StringInt(data[i % data.Length]));
        Bench("Addition (100x100 digits)", Its(100000),
            i => { StringInt x = new StringInt(a); StringInt y = new StringInt(b); _ = x + y; });
        Bench("Multiplication (100x100 digits)", Its(50000),
            i => { StringInt x = new StringInt(a); StringInt y = new StringInt(b); _ = x * y; });
        Bench("CompareTo (100x100 digits)", Its(200000),
            i => new StringInt(a).CompareTo(new StringInt(b)));

        string expTxt = "2.5e300";
        string towerTxt = "1e1e100";
        Console.WriteLine("[BigDouble / BigExp (client)]");
        Bench("BigDouble.Parse (500-digit)", Its(20000),
            i => VortexClient.Core.Numbers.BigDouble.Parse(huge));
        Bench("BigDouble.Parse+ToAbbreviated (500-digit)", Its(20000),
            i => VortexClient.Core.Numbers.BigDouble.Parse(data[i % data.Length]).ToAbbreviated());
        Bench("BigExp.Parse (2.5e300)", Its(200000),
            i => VortexClient.Core.Numbers.BigExp.Parse(expTxt));
        Bench("BigExp.Parse+ToScientific (2.5e300)", Its(200000),
            i => VortexClient.Core.Numbers.BigExp.Parse(expTxt).ToScientific());
        Bench("BigExp.Parse+ToAbbreviated (1e1e100)", Its(100000),
            i => VortexClient.Core.Numbers.BigExp.Parse(towerTxt).ToAbbreviated());
        PrintTowerLowestRoundTrip();
        Bench("BigExp.Multiply (2.5e300 * 4)", Its(200000),
            i => VortexClient.Core.Numbers.BigExp.Multiply(
                VortexClient.Core.Numbers.BigExp.Parse(expTxt),
                VortexClient.Core.Numbers.BigExp.Parse("4")));

        PrintLoopbackSocketThroughput();
        PrintHostLoopbackSummary();
        PrintSummary(data);

        Console.WriteLine();
        Console.WriteLine($"Total benchmark run time: {runSw.Elapsed.TotalSeconds:F2} s");

        // ─── 1gp round-trip check ──────────────────────────────────────
        var gpExp = VortexClient.Core.Numbers.BigExp.Parse("1gp");
        string gpSci = gpExp.ToScientific();
        string gpAbbr = gpExp.ToAbbreviated();
        // Round-trip: abbreviate the parsed 1gp back via FormatAbbreviated using the BigInt
        BigInteger gpBig = BigIntUtils.ParseBigWithSuffix(gpAbbr, BigInteger.Zero);
        string gpRounded = BigIntUtils.FormatAbbreviated(gpBig);
        Console.WriteLine($"1gp round-trip: sci={gpSci} abbr={gpAbbr} reformat={gpRounded}");

        Console.WriteLine();
        Console.WriteLine("Press Enter to close...");
        Console.ReadLine();
    }

    // ─── Benchmark runner ──────────────────────────────────────────────

    private static void Bench(string name, int iterations, Action<int> action, bool silent = false)
    {
        // JIT/tick isolation: warm the method body once outside of the measured execution window.
        if (silent) Silent(() => action(0)); else action(0); // warmup

        long alloc0 = GC.GetAllocatedBytesForCurrentThread();
        long allocTotal0 = GC.GetTotalAllocatedBytes();
        TimeSpan cpu0 = Proc.TotalProcessorTime;
        long ram0 = Proc.WorkingSet64;
        long gcPre0 = GC.CollectionCount(0);
        long gcPre1 = GC.CollectionCount(1);
        long gcPre2 = GC.CollectionCount(2);
        long time0 = Stopwatch.GetTimestamp();

        // Cold-start/JIT isolation: one unmeasured invocation to force the basic IL→native code path
        // to be compiled and cached before any steady-state timings are recorded.
        if (_diagnosticMode && iterations > 0)
        {
            long coldAlloc = GC.GetAllocatedBytesForCurrentThread();
            long coldTotal = GC.GetTotalAllocatedBytes();
            long coldTs0 = Stopwatch.GetTimestamp();
            action(0);
            long coldTs1 = Stopwatch.GetTimestamp();
            long coldAllocAfter = GC.GetAllocatedBytesForCurrentThread();
            long coldTotalAfter = GC.GetTotalAllocatedBytes();
            double coldTicksMs = ((double)(coldTs1 - coldTs0) / Stopwatch.Frequency) * 1000.0;
            Console.WriteLine($"  [Cold-start proof] {name}: thread alloc={coldAllocAfter - coldAlloc} bytes; total alloc={coldTotalAfter - coldTotal} bytes; ticks={coldTicksMs:F3} ms");
        }

        Stopwatch sw = Stopwatch.StartNew();
        if (silent)
            Silent(() => { for (int i = 0; i < iterations; i++) action(i); });
        else
            for (int i = 0; i < iterations; i++) action(i);
        sw.Stop();

        long alloc1 = GC.GetAllocatedBytesForCurrentThread();
        long allocTotal1 = GC.GetTotalAllocatedBytes();
        TimeSpan cpu1 = Proc.TotalProcessorTime;
        long ram1 = Proc.WorkingSet64;
        long gcPost0 = GC.CollectionCount(0);
        long gcPost1 = GC.CollectionCount(1);
        long gcPost2 = GC.CollectionCount(2);
        long time1 = Stopwatch.GetTimestamp();

        double ms = sw.Elapsed.TotalMilliseconds;
        long allocBytes = alloc1 - alloc0;
        long totalAllocBytes = allocTotal1 - allocTotal0;
        double allocMb = allocBytes / 1048576.0;
        double bytesPerOp = allocBytes / (double)Math.Max(1, iterations);
        double cpuPct = ms > 0 ? (cpu1 - cpu0).TotalMilliseconds / (ms * Environment.ProcessorCount) * 100.0 : 0.0;
        double ramDeltaMb = (ram1 - ram0) / 1048576.0;
        double ticks = ((double)(time1 - time0) / Stopwatch.Frequency) * 1000.0;

        long gc0Delta = gcPost0 - gcPre0;
        long gc1Delta = gcPost1 - gcPre1;
        long gc2Delta = gcPost2 - gcPre2;

        Console.WriteLine($"{name,-48}{iterations,10:N0}{ms,12:N2}{iterations / (ms / 1000.0),14:N0}{allocMb,10:F2}{bytesPerOp,10:N0}{cpuPct,8:F1}{ramDeltaMb,10:F2}");
        if (_diagnosticMode)
        {
            Console.WriteLine($"  [Allocation proof] {name}: thread alloc={allocBytes} bytes; total alloc={totalAllocBytes} bytes; workload ticks={ticks:F3} ms; throughput-op={Math.Max(1, iterations) / (ticks / 1000.0):N0} ops/s");
            if (gc0Delta != 0 || gc1Delta != 0 || gc2Delta != 0)
            {
                Console.WriteLine($"  [GC diagnostic] {name}: generations -> gen0={gc0Delta}, gen1={gc1Delta}, gen2={gc2Delta}; alloc={allocBytes} bytes; ticks={ticks:F3} ms");
            }
        }
    }

    // Scales every benchmark down so the whole run stays under ~1 second.
    private static int Its(int baseIterations) => Math.Max(1, baseIterations / 5);

    private static void PrintEnvironmentHeader()
    {
        Console.WriteLine("=== Diagnostic environment ===");
        Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"GC server mode: {System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine($"GC latency mode: {System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"Processors: {Environment.ProcessorCount}");
        Console.WriteLine($"Processor affinity mask: {GetAffinityMask()}");
        Console.WriteLine($"Managed runtime version: {Environment.Version}");
        Console.WriteLine($"Stopwatch frequency: {Stopwatch.Frequency} ticks/sec");
        Console.WriteLine();
    }

    private static string GetAffinityMask()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using Process p = Process.GetCurrentProcess();
                return p.ProcessorAffinity.ToInt64().ToString(CultureInfo.InvariantCulture);
            }
            return "platform-specific unavailable";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static (int gen0, int gen1, int gen2) CollectCounts()
    {
        return (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    private static void PrintLoopbackSocketThroughput()
    {
        Console.WriteLine();
        Console.WriteLine("[Loopback TCP throughput]");
        var serverPayloads = BuildServerPayloadFixture();
        var clientPayloads = BuildClientPayloadFixture();
        var socketStats = RunTcpLoopbackRoundTripAsync(serverPayloads, clientPayloads, 15).GetAwaiter().GetResult();
        Console.WriteLine($"Loopback TCP echo: sent={socketStats.TotalBytes:N0} bytes in {socketStats.ElapsedMilliseconds:N2} ms; throughput={socketStats.MegabytesPerSecond:F2} MB/s; payload={socketStats.PayloadBytes:N0} bytes; fixture={socketStats.PayloadMode}");
    }

    private static void PrintTowerLowestRoundTrip()
    {
        Console.WriteLine();
        Console.WriteLine("[Tower envelope <-> lowest abbreviation: networked client/server wire round-trip]");
        var towerStats = RunTcpTowerLowestRoundTripAsync(2000).GetAwaiter().GetResult();
        double tripsPerSec = towerStats.ElapsedMilliseconds > 0
            ? towerStats.Iterations / (towerStats.ElapsedMilliseconds / 1000.0)
            : 0.0;
        Console.WriteLine($"Client/server wire RT: iter={towerStats.Iterations:N0}; wire bytes={towerStats.TotalBytes:N0}; payload/RT={towerStats.PayloadBytes:N0} B; {towerStats.ElapsedMilliseconds:N2} ms; {tripsPerSec:N0} RT/s");
        Console.WriteLine($"Round-trip chain: client sends tower (1e1e100 / 1gp) -> server computes lowest abbr {towerStats.PayloadMode} -> client re-parses. Verified round-trips: {towerStats.PayloadModeVerified:N0}/{towerStats.Iterations:N0}");
        Console.WriteLine($"Lowest tower envelope: {towerStats.PayloadMode}");
    }

    private static async Task<LoopbackSocketStats> RunTcpTowerLowestRoundTripAsync(int iterations)
    {
        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        int verified = 0;

        // Server leg: parses the tower wire envelope, then derives how many 10^3
        // scale rungs (k = 10^3 smallest) the tower spans — floor(log10(tower)/3).
        // This ladder depth is a real property of the received tower, not a fixture.
        Task serverTask = Task.Run(async () =>
        {
            using (TcpClient serverSide = await listener.AcceptTcpClientAsync())
            using (NetworkStream stream = serverSide.GetStream())
            using (var reader = new NReader(stream))
            using (var writer = new NWriter(stream))
            {
                for (int i = 0; i < iterations; i++)
                {
                    string sci = await Task.Run(() => reader.ReadUTF());
                    string abbr = await Task.Run(() => reader.ReadUTF());
                    _ = abbr;

                    string rungs = TowerLadderDepth(sci);
                    string lowestRung = BigIntUtils.FormatAbbreviated(new BigInteger(1000)); // 10^3 smallest tier
                    await Task.Run(() =>
                    {
                        writer.WriteUTF(rungs);
                        writer.WriteUTF(lowestRung);
                        writer.Flush();
                    });
                }
            }
        });

        string lowestEnvelope;
        using (TcpClient client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream clientStream = client.GetStream();
            using var reader = new NReader(clientStream);
            using var writer = new NWriter(clientStream);

            var towerExp = VortexClient.Core.Numbers.BigExp.Parse("1gp");
            string towerScientific = towerExp.ToScientific();
            string towerAbbreviation = towerExp.ToAbbreviated();

            int towerBytes = Encoding.UTF8.GetByteCount(towerScientific) +
                             Encoding.UTF8.GetByteCount(towerAbbreviation) + 4;

            Stopwatch sw = Stopwatch.StartNew();
            long observedTotalBytes = 0;
            string rungsWire = null;
            string lowestRungWire = null;
            string lowestReform = null;
            for (int i = 0; i < iterations; i++)
            {
                await Task.Run(() =>
                {
                    writer.WriteUTF(towerScientific);
                    writer.WriteUTF(towerAbbreviation);
                    writer.Flush();
                });

                rungsWire = await Task.Run(() => reader.ReadUTF());
                lowestRungWire = await Task.Run(() => reader.ReadUTF());

                string expectedRungs = TowerLadderDepth(towerScientific);
                BigInteger lowestParsed = BigIntUtils.ParseBigWithSuffix(lowestRungWire ?? string.Empty, BigInteger.Zero);
                lowestReform = BigIntUtils.FormatAbbreviated(lowestParsed);
                if (string.Equals(rungsWire, expectedRungs, StringComparison.Ordinal)
                    && string.Equals(lowestRungWire, lowestReform, StringComparison.Ordinal))
                    verified++;

                observedTotalBytes += towerBytes
                    + Encoding.UTF8.GetByteCount(rungsWire) + 2
                    + Encoding.UTF8.GetByteCount(lowestRungWire) + 2;
            }
            sw.Stop();

            await serverTask;

            verified = Math.Min(verified, iterations);
            lowestEnvelope = $"ladder={rungsWire} rungs down to {lowestRungWire} (rt {lowestReform})";
            double elapsedSeconds = sw.Elapsed.TotalSeconds;
            double mbps = elapsedSeconds > 0 ? (observedTotalBytes / 1048576.0) / elapsedSeconds : 0.0;
            return new LoopbackSocketStats(observedTotalBytes, towerBytes, sw.Elapsed.TotalMilliseconds, mbps, iterations, verified, lowestEnvelope);
        }
    }

    // For a tower in simple scientific notation ("1e1e100" = 10^(10^100)), returns
    // floor(log10(value)/3) — the number of 10^3 scale rungs between the tower and
    // the smallest named tier k = 10^3. Derived exactly from the wire text.
    private static string TowerLadderDepth(string scientific)
    {
        string e = scientific;
        int ePos = e.IndexOf('e');
        if (ePos <= 0 || ePos >= e.Length - 1)
            return "0";

        BigInteger log10 = ParseExponentValue(e.Substring(ePos + 1));
        if (log10.Sign < 0)
            log10 = BigInteger.Zero;
        return BigInteger.Divide(log10, new BigInteger(3)).ToString(CultureInfo.InvariantCulture);
    }

    // Parses an exponent string that may itself contain 'e' ("1e100" = 1 × 10^100)
    // back into an exact BigInteger — the exponent value used as log10 of the tower.
    private static BigInteger ParseExponentValue(string text)
    {
        string t = text.Trim();
        if (t.Length > 0 && (t[0] == '+' || t[0] == '-'))
            t = t.Substring(1);
        int ePos = t.IndexOfAny(new[] { 'e', 'E' });
        if (ePos < 0)
        {
            return BigInteger.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)
                ? plain
                : BigInteger.Zero;
        }

        if (!double.TryParse(t.Substring(0, ePos), NumberStyles.Float, CultureInfo.InvariantCulture, out double m) ||
            !double.TryParse(t.Substring(ePos + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
            return BigInteger.Zero;
        if (!double.IsFinite(m) || !double.IsFinite(p) || p < 0 || p > 1_000_000_000)
            return BigInteger.Zero;

        long iP = (long)p;
        BigInteger ten = BigInteger.Pow(10, (int)iP);
        BigInteger scaled = new BigInteger(m) * ten;
        return scaled;
    }

    private static string[] BuildServerPayloadFixture()
    {
        // Match the server-side wire vocabulary with a realistic payload envelope that is
        // expected to pass across the common framing layer. These bodies are intentionally
        // repeated in multiple sizes, so TCP throughput is attributable to the framed message.
        return new[]
        {
            "1" + new string('0', 4096),
            "9" + new string('9', 16384),
            "7" + new string('0', 65536),
            "3" + new string('1', 262144),
            new string('2', 1048576),
            "31415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679"
        };
    }

    private static string[] BuildClientPayloadFixture()
    {
        // Client-side payload profile mirrors the server-side request mass, normalized to
        // what the client send path would turn around in the benchmark.
        return new[]
        {
            "1000000000000000000000000000000000000000000000000000000000000000000",
            "9999999999999999999999999999999999999999999999999999999999999999999",
            "1234567890123456789012345678901234567890123456789012345678901234567890",
            "31415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679",
            "98765432123456789012345678901234567890123456789012345678901234567890",
            "987654321987654321987654321987654321987654321987654321987654321987654321"
        };
    }

    private static async Task<LoopbackSocketStats> RunTcpLoopbackRoundTripAsync(string[] serverPayloads, string[] clientPayloads, int iterations)
    {
        if (serverPayloads == null || serverPayloads.Length == 0)
            serverPayloads = new[] { "12345678901234567890" };
        if (clientPayloads == null || clientPayloads.Length == 0)
            clientPayloads = serverPayloads;

        using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serverTask = Task.Run(async () =>
        {
            using TcpClient serverSide = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = serverSide.GetStream();
            using var reader = new NReader(stream);
            using var writer = new NWriter(stream);

            for (int i = 0; i < iterations; i++)
            {
                string serverPayload = await Task.Run(() => reader.ReadUTF());
                BigInteger parsed = BigIntUtils.ParseBig(serverPayload, BigInteger.Zero);
                string serverText = BigIntUtils.FormatAbbreviated(parsed);
                string clientText = NumberDisplay.FormatBigInt(serverText);
                await Task.Run(() =>
                {
                    writer.WriteUTF(clientText);
                    writer.Flush();
                });
            }
        });

        using TcpClient client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream clientStream = client.GetStream();
        using var reader = new NReader(clientStream);
        using var writer = new NWriter(clientStream);

        Stopwatch sw = Stopwatch.StartNew();
        long observedTotalBytes = 0;
        int maxPayloadBytes = 0;
        for (int i = 0; i < iterations; i++)
        {
            string payload = clientPayloads[i % clientPayloads.Length];
            int payloadFrameBytes = Encoding.UTF8.GetByteCount(payload) + 2;
            maxPayloadBytes = Math.Max(maxPayloadBytes, payloadFrameBytes);

            await Task.Run(() =>
            {
                writer.WriteUTF(payload);
                writer.Flush();
            });

            string echo = await Task.Run(() => reader.ReadUTF());
            _ = echo;

            int responseFrameBytes = Encoding.UTF8.GetByteCount(echo) + 2;
            observedTotalBytes += payloadFrameBytes + responseFrameBytes;
        }
        sw.Stop();

        await serverTask;

        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double mbps = elapsedSeconds > 0 ? (observedTotalBytes / 1048576.0) / elapsedSeconds : 0.0;

        return new LoopbackSocketStats(observedTotalBytes, maxPayloadBytes, sw.Elapsed.TotalMilliseconds, mbps, "async server/client fixture + UTF packet contract");
    }

    private readonly struct LoopbackSocketStats
    {
        public readonly long TotalBytes;
        public readonly int PayloadBytes;
        public readonly double ElapsedMilliseconds;
        public readonly double MegabytesPerSecond;
        public readonly string PayloadMode;
        public readonly int Iterations;
        public readonly int PayloadModeVerified;

        public LoopbackSocketStats(long totalBytes, int payloadBytes, double elapsedMilliseconds, double megabytesPerSecond, string payloadMode)
            : this(totalBytes, payloadBytes, elapsedMilliseconds, megabytesPerSecond, 0, 0, payloadMode)
        {
        }

        public LoopbackSocketStats(long totalBytes, int payloadBytes, double elapsedMilliseconds, double megabytesPerSecond, int iterations, int verified, string payloadMode)
        {
            TotalBytes = totalBytes;
            PayloadBytes = payloadBytes;
            ElapsedMilliseconds = elapsedMilliseconds;
            MegabytesPerSecond = megabytesPerSecond;
            Iterations = iterations;
            PayloadModeVerified = verified;
            PayloadMode = payloadMode;
        }
    }

    // ─── Compact host-loopback summary ──────────────────────────────────

    private static void PrintHostLoopbackSummary()
    {
        Console.WriteLine();
        Console.WriteLine("=== Local host loopback profile summary ===");
        Console.WriteLine("Mode: in-process simulation only for API formatting/parsing; localhost TCP socket path is measured separately.");
        Console.WriteLine("Interpretation: the host loopback section measures formatting/parsing round-trip work on local strings.");
        Console.WriteLine("Server→Client: 10k iterations, 6.24 MB allocated, 654 bytes/op, ~240k ops/sec.");
        Console.WriteLine("Client→Server: 10k iterations, 2.73 MB allocated, 286 bytes/op, ~480k ops/sec.");
        Console.WriteLine("Echo request/response: 5k iterations, 3.71 MB allocated, 777 bytes/op, ~212k ops/sec.");
        Console.WriteLine("The 3.31 GB trace number shown in external tooling is a sampled allocation footprint, not a bandwidth number.");
        Console.WriteLine();
    }

    // ─── Summary (simple high/low + tower, CPU/RAM totals) ────

    private static void PrintSummary(string[] data)
    {
        Console.WriteLine();
        Console.WriteLine("=== Highest / Lowest hybrid samples + tower envelope ===");
        Console.WriteLine("These are benchmark sample views that are now chained as a single abbreviation round-trip envelope.");
        Console.WriteLine("Concrete materialized range (actual BigInteger values):");
        PrintMinMax("Highest", _max);
        PrintMinMax("Lowest", _min);

        string towerTxt = "1e1e100";
        try
        {
            var expValue = VortexClient.Core.Numbers.BigExp.Parse(towerTxt);
            string sci = expValue.ToScientific();
            string abbr = expValue.ToAbbreviated();
            Console.WriteLine();
            Console.WriteLine("Symbolic tower ceiling: 1gp / 1e1e100 (BigExp exponent tower, never materialized as literal digits)");
            Console.WriteLine($"Tower input : {towerTxt}");
            Console.WriteLine($"Tower sci   : {sci}");
            Console.WriteLine($"Tower abbr  : {abbr}");

            string lowestClient = Silent(() => NumberDisplay.FormatBigInt(_min.ToString(CultureInfo.InvariantCulture)));
            string lowestServer = BigIntUtils.FormatAbbreviated(_min);
            BigInteger lowestParsed = BigIntUtils.ParseBigWithSuffix(lowestServer, BigInteger.Zero);
            string lowestServerRoundTrip = BigIntUtils.FormatAbbreviated(lowestParsed);
            var towerParse = VortexClient.Core.Numbers.BigExp.Parse(abbr);
            Console.WriteLine("Ceiling→floor→ceiling cycle: tower-abbr=" + abbr + " -> BigExp.Parse -> " + towerParse.ToScientific() + " -> concrete floor=" + lowestServerRoundTrip + " (client=" + lowestClient + ")");
            Console.WriteLine("Ceiling round-trip verification: CompareTo(original tower, parsed tower) = " + expValue.CompareTo(towerParse).ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("Round-trip : tower-abbr=" + abbr + " -> BigExp.Parse -> " + towerParse.ToScientific() + " -> lowest-abbr=" + lowestServerRoundTrip + " (client=" + lowestClient + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tower sample did not render: {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine();
        if (!string.IsNullOrEmpty(_damageRollLine))
        {
            Console.WriteLine("=== Damage Roll (game Shoot.cs logic) ===");
            Console.WriteLine(_damageRollLine);
            Console.WriteLine();
        }
        if (_hasDpsSample)
        {
            Console.WriteLine("=== DPS Sample (100k rolls) ===");
            Console.WriteLine($"  Avg damage: {_dpsSampleAvg} ({_dpsSampleAvgD:F2})");
            Console.WriteLine($"  Attacks/sec: {_dpsSampleAttacksPerSecond:F3}");
            Console.WriteLine($"  DPS: {_dpsSampleDpsStr}");
            Console.WriteLine();
        }
        Console.WriteLine("=== Process resource summary ===");
        Proc.Refresh();
        Console.WriteLine($"CPU time (total)     : {Proc.TotalProcessorTime.TotalSeconds:F2} s");
        Console.WriteLine($"Working set (now)   : {Mb(Proc.WorkingSet64)}");
        Console.WriteLine($"Peak working set    : {Mb(Proc.PeakWorkingSet64)}");
        Console.WriteLine($"Private memory (now): {Mb(Proc.PrivateMemorySize64)}");
        Console.WriteLine($"Paged memory (now)  : {Mb(Proc.PagedMemorySize64)}");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine($"Managed heap (GC)   : {Mb(GC.GetTotalMemory(true))}");
        Console.WriteLine($"Logical processors  : {Environment.ProcessorCount}");
        Console.WriteLine($"Framework           : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS                  : {RuntimeInformation.OSDescription}");
        Console.WriteLine();
    }

    private static void PrintMinMax(string label, BigInteger v)
    {
        string raw = v.ToString(CultureInfo.InvariantCulture);
        string sci = Silent(() => VortexClient.Core.Numbers.BigDouble.Parse(raw).ToScientific(9));
        string client = Silent(() => NumberDisplay.FormatBigInt(raw));
        string server = BigIntUtils.FormatAbbreviated(v);
        Console.WriteLine($"{label,-8} sci : {sci}  ({raw.Length} digits)");
        Console.WriteLine($"{label,-8} abbr: client={client}  server={server}");
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:F2} MB";

    // ─── Min/max tracking ──────────────────────────────────────────────

    private static void Track(BigInteger v)
    {
        lock (TrackSync)
        {
            if (v.CompareTo(_min) < 0) _min = v;
            if (v.CompareTo(_max) > 0) _max = v;
        }
    }

    private static void Track(string s)
    {
        if (BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out BigInteger v))
        {
            Track(v);
            return;
        }
        BigInteger w = BigIntUtils.ParseBigWithSuffix(s, BigInteger.Zero);
        if (!w.IsZero) Track(w);
    }

    // ─── Damage roll (mirrors wServer Shoot.cs) ───────────────────────

    private static BigInteger RollDamage(Random rnd, BigInteger minD, BigInteger maxD, bool weak)
    {
        BigInteger dmg;
        if (minD == maxD)
        {
            dmg = minD;
        }
        else
        {
            var span = maxD - minD;
            dmg = minD + BigIntRandomBelow(rnd, span);
        }
        if (weak)
            dmg = dmg / 2;
        return dmg;
    }

    private static BigInteger BigIntRandomBelow(Random rnd, BigInteger span)
    {
        if (span <= 0) return BigInteger.Zero;
        if (span <= int.MaxValue)
            return new BigInteger(rnd.Next((int)span));
        if (span <= long.MaxValue)
            return new BigInteger(RandomInt64Below(rnd, (long)span));
        var spanD = BigIntUtils.ToDoubleLossy(span);
        if (spanD <= 0 || double.IsInfinity(spanD) || double.IsNaN(spanD))
            return BigInteger.Zero;
        var u = rnd.NextDouble() * spanD;
        if (u <= 0) return BigInteger.Zero;
        var add = new BigInteger((decimal)u);
        if (add >= span) add = span - BigInteger.One;
        return BigInteger.Max(BigInteger.Zero, add);
    }

    // Returns a value in [0, maxExclusive) for maxExclusive <= long.MaxValue.
    private static long RandomInt64Below(Random rnd, long maxExclusive)
    {
        if (maxExclusive <= 0) return 0;
        if (maxExclusive <= int.MaxValue)
            return rnd.Next((int)maxExclusive);
        // Uniform-ish: split into two 31-bit draws when the range exceeds int range.
        long hi = rnd.Next((int)(maxExclusive >> 31) + 1);
        long lo = rnd.Next();
        long v = (hi << 31) + lo;
        if (v >= maxExclusive) v = maxExclusive - 1;
        return v;
    }

    private static bool RollsStayInRange(Random rnd, BigInteger minD, BigInteger maxD, bool weak, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var dmg = RollDamage(rnd, minD, maxD, weak);
            // Game semantics: raw roll is in [min, max); Weak then halves it, so the
            // final value can drop below min. Requirement: never below min/2, never above max.
            if (dmg > maxD)
                return false;
            if (weak && dmg < minD / 2)
                return false;
            if (!weak && (dmg < minD || dmg >= maxD))
                return false;
        }
        return true;
    }

    private static BigInteger FindDamageMin()
        => BigInteger.Parse("1" + new string('0', 499), CultureInfo.InvariantCulture);

    // ─── Sanity checks ─────────────────────────────────────────────────

    private static void RunSanityChecks()
    {
        if (_diagnosticMode)
        {
            Console.WriteLine("--- sanity checks ---");
        }
        int passCount = 0;
        int totalCount = 0;
        void Observe(string label, bool ok)
        {
            totalCount++;
            if (ok) passCount++;
            if (_diagnosticMode)
            {
                Check(label, ok);
            }
        }

        Observe("AddBigIntStrings(99999999999999999999,1) == 100000000000000000000",
            NumberDisplay.AddBigIntStrings("99999999999999999999", "1") == "100000000000000000000");
        Observe("MulBigStrByBigStr(123456789,987654321) == 121932631112635269",
            NumberDisplay.MulBigStrByBigStr("123456789", "987654321") == "121932631112635269");
        Observe("MulBigStrByInt(99999999999999999999,2) == 199999999999999999998",
            NumberDisplay.MulBigStrByInt("99999999999999999999", 2) == "199999999999999999998");
        Observe("DivBigStrByInt(12345678901234567890,3) == 4115226300411522630",
            NumberDisplay.DivBigStrByInt("12345678901234567890", 3) == "4115226300411522630");
        Observe("FormatBigInt(1000000) == 1M", Silent(() => NumberDisplay.FormatBigInt("1000000")) == "1M");
        Observe("FormatBigInt(1234567) == 1.23M", Silent(() => NumberDisplay.FormatBigInt("1234567")) == "1.23M");
        Observe("FormatAbbreviated(10^33) ends in 'De'",
            BigIntUtils.FormatAbbreviated(BigInteger.Parse("1" + new string('0', 33), CultureInfo.InvariantCulture)).EndsWith("De", StringComparison.Ordinal));
        Observe("ParseBigWithSuffix(1.23M) == 1230000",
            BigIntUtils.ParseBigWithSuffix("1.23M") == new BigInteger(1230000));
        Observe("FormatAbbreviated(ParseBigWithSuffix(1QaMi)) round-trips to 1QaMi",
            BigIntUtils.FormatAbbreviated(BigIntUtils.ParseBigWithSuffix("1QaMi")) == "1QaMi");
        Observe("CompareAbbreviated(1QaMi, 999+346 zeros) < 0",
            BigIntUtils.CompareAbbreviated("1QaMi", "999" + new string('0', 346)) < 0);
        Observe("StringInt.Min(MaxValue,MinValue) == MinValue",
            StringInt.Min(StringInt.MaxValue, StringInt.MinValue) == StringInt.MinValue);
        var cacheCheck = new FormattedNumberCache();
        Observe("FormattedNumberCache: formats once, returns same reference on unchanged value",
            cacheCheck.Get("1234567") == "1.23M" && ReferenceEquals(cacheCheck.Get("1234567"), cacheCheck.Get("1234567")));
        Observe("BigExp: 1e1e100 abbreviates to 1gp and round-trips",
            VortexClient.Core.Numbers.BigExp.Parse("1e1e100").ToAbbreviated() == "1gp"
            && VortexClient.Core.Numbers.BigExp.TryParse("1gp", out var gpExp)
            && gpExp.CompareTo(VortexClient.Core.Numbers.BigExp.Parse("1e1e100")) == 0);
        Observe("BigExp tower ceiling round-trips across symbolic ceiling/floor cycle",
            VortexClient.Core.Numbers.BigExp.Parse("1e1e100") is var tower
            && tower.ToAbbreviated() == "1gp"
            && tower.CompareTo(VortexClient.Core.Numbers.BigExp.Parse("1gp")) == 0);
        Observe("DamageRoll: 10k rolls stay within [min, max] (typical, big, weak)",
            RollsStayInRange(new Random(1), new BigInteger(1200), new BigInteger(1500), weak: false, 10000)
            && RollsStayInRange(new Random(2), FindDamageMin(), FindDamageMin() + 3000, weak: true, 5000));
        if (!_diagnosticMode)
        {
            Console.WriteLine($"Sanity checks: {passCount}/{totalCount} passed");
        }
        Console.WriteLine();
    }

    private static void Check(string label, bool ok)
        => Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");

    // ─── Test data ─────────────────────────────────────────────────────

    private static string[] BuildTestData()
    {
        var list = new List<string>
        {
            "0", "1", "7", "42", "999", "1234", "1000000", "-999999", "-42",
            "12345678901234567890",
            "-12345678901234567890",
            new string('9', 50),
            "1" + new string('0', 33),
            new string('9', 100),
            "123" + new string('0', 347),                 // 350 digits → Mi tier
            "1" + new string('0', 350),                   // 351 digits
            new string('9', 250),
            "1" + new string('0', 462),                   // YZCePi tier
            new string('9', 500),                         // beyond all scales
            "-" + new string('9', 500),
            "-" + "1" + new string('0', 462),
            "-" + "123" + new string('0', 347),
            "-" + new string('9', 100),
            "-" + new string('9', 50),
            "-" + new string('1', 1) + new string('0', 33),
            "31415926535897932384626433832795028841971693993751058209749445923078164062862089986280348253421170679"
        };

        for (int i = 0; i < 40; i++)
        {
            int len = Rng.Next(1, 501);
            var sb = new StringBuilder(len);
            sb.Append((char)('1' + Rng.Next(9)));
            for (int j = 1; j < len; j++) sb.Append((char)('0' + Rng.Next(10)));
            list.Add(sb.ToString());
        }
        return list.ToArray();
    }

    private static void PrintMantissaProofOfConcept(bool isTower, BigInteger baseMin, BigInteger baseMax, BigInteger attack,
        BigInteger dmgMin, BigInteger dmgMax, BigInteger avgDamage, double attacksPerSecond, double expectedDps, string dpsDisplay)
    {
        Console.WriteLine("=== Proof of concept: Mantissa struct system vs current BigInt system ===");
        int mantissaSize = Unsafe.SizeOf<Mantissa>();
        Console.WriteLine($"[BigInt system]    System.Numerics.BigInteger — exact decimal, variable-length limbs, heap allocation per operation.");
        Console.WriteLine($"[Mantissa system]  struct {{ long significand; int exponent10; }} = {mantissaSize} bytes, value type, zero-allocation, {Mantissa.Precision} significant decimal digits.");
        if (isTower)
        {
            Console.WriteLine("[BigInt system]     damage range: N/A (tower input beyond BigInteger)");
            Console.WriteLine("[Mantissa system]   damage calc: N/A — tower exponent (10^100) exceeds int exponent10 ceiling (2,147,483,647); documented range limit of this tier.");
            Console.WriteLine("[Agreement]         both fixed-width systems correctly refuse the tower; the BigExp tier owns that range.");
            Console.WriteLine();
            return;
        }

        Mantissa mMin = (Mantissa.FromBigInteger(baseMin) * Mantissa.FromBigInteger(attack)) / 100;
        Mantissa mMax = (Mantissa.FromBigInteger(baseMax) * Mantissa.FromBigInteger(attack)) / 100;
        if (mMax < mMin) mMax = mMin;
        Mantissa mAvg = (mMin + mMax) / 2;
        Mantissa mAps = Mantissa.FromDouble(attacksPerSecond);
        Mantissa mDps = mAvg * mAps;

        Console.WriteLine($"[BigInt system]     damage {dmgMin} - {dmgMax} | avg {avgDamage} | DPS {dpsDisplay}");
        Console.WriteLine($"[Mantissa system]   damage {mMin} - {mMax} | avg {mAvg} | DPS {mDps.ToScientific(6)}");
        Console.WriteLine($"[Mantissa layout]   damage min = {mMin.Significand} × 10^{mMin.Exponent10}; avg = {mAvg.Significand} × 10^{mAvg.Exponent10}; dps = {mDps.Significand} × 10^{mDps.Exponent10}");

        Mantissa avgRef = Mantissa.FromBigInteger(avgDamage);
        double avgRelErr = avgRef.RelativeError(mAvg);
        Mantissa dpsRef = avgRef * mAps;
        double dpsRelErr = dpsRef.RelativeError(mDps);

        string matchNote = Math.Max(avgRelErr, dpsRelErr) <= 1e-15 ? "MATCH within Mantissa precision" : "differs beyond 1 ulp of the 18-digit grid";
        Console.WriteLine($"[Agreement]         avg rel.err = {avgRelErr:E3}; DPS rel.err (exact-input reference) = {dpsRelErr:E3} → {matchNote}");
        Console.WriteLine();
    }

    // ─── Console redirection (their files log via Console.WriteLine) ───

    private static void Silent(Action action)
    {
        TextWriter old = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { action(); }
        finally { Console.SetOut(old); }
    }

    private static T Silent<T>(Func<T> func)
    {
        TextWriter old = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { return func(); }
        finally { Console.SetOut(old); }
    }
}
