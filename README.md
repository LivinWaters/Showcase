# Shifted BigInts - Code Showcase

Extracted arrow-notation scaling arithmetic and the supporting short-suffix number
types from **Absentina**. The gameplay
amounts in that game pass through this code whenever an item with a big "value",
"feed power", or "fame" number is rendered to the client, so these classes sit in
the hot path of every economy-based packet.

Every source file below is a verbatim copy of the production implementation,
apart from the documented branch-level tweaks. The public API is exactly
what the game uses.

## Production Path Mapping

| Showcase file | Production source | Shared | Match |
|---|---|---|---|
| `FNA Client Side\BigDouble.cs` | `Vortex FNA Engine\src\Core\BigDouble.cs` | -- | Byte-identical |
| `FNA Client Side\BigExp.cs` | `Vortex FNA Engine\src\Core\BigExp.cs` | -- | Byte-identical |
| `FNA Client Side\BigIntChunked.cs` | `Vortex FNA Engine\src\Core\BigIntChunked.cs` | -- | Byte-identical |
| `FNA Client Side\NumberDisplay.cs` | `Vortex FNA Engine\src\Core\NumberDisplay.cs` | -- | Byte-identical |
| `FNA Client Side\NumberDisplayScales.cs` | `Vortex FNA Engine\src\Core\NumberDisplayScales.cs` | -- | Byte-identical |
| `Server Side\BigDouble.cs` | `Vortex Server\common\BigDouble.cs` | shared, client & server | Byte-identical |
| `Server Side\BigExp.cs` | `Vortex Server\common\BigExp.cs` | shared, client & server | Byte-identical |
| `Server Side\BigIntChunked.cs` | `Vortex Server\common\BigIntChunked.cs` | shared, client & server | Byte-identical |
| `Server Side\BigIntUtils.cs` | `Vortex Server\common\BigIntUtils.cs` | server-only | 1 added line, see below |
| `Server Side\NReader.cs` | `Vortex Server\common\NReader.cs` | server-only | Trailing line-ending drift |
| `Server Side\NWriter.cs` | `Vortex Server\common\NWriter.cs` | server-only | Trailing line-ending drift |
| `Server Side\StringInt.cs` | `Vortex Server\common\StringInt.cs` | server-only | Byte-identical |

### Deltas from production

- **`BigIntUtils.cs`** - identical to production except a single additional
  `using VortexClient.Core.Numbers;` line at the top. This copy exists for the
  **benchmark** harness (it links the client and server halves into one .NET 10
  console app without the original solution/namespace structure); the **server**
  build gets the file as-is with no `using`. The full `AbbrevScales` table
  (K up to `YZCePi`, exponents 0-462) and every helper method are unchanged.
- **`NReader.cs` / `NWriter.cs`** - same content line-for-line; production ends
  with one extra trailing blank line (CRLF drift from the repo's `.gitattributes`).
  No functional difference.
- Everything else is byte-identical, CRLF-normalized.

## Running the benchmark

dotnet run -c Release


Requires the .NET 10 SDK. The console app links the client and server halves
(that's why `BigIntUtils.cs` carries the extra `using VortexClient.Core.Numbers;`,
see the delta note above) and runs:

- **Sanity checks** — 14 round-trips verify abbreviated strings parse back to the
  same `BigExp`, so the contract holds before timing starts.
- **Per-benchmark timing** — iterations, total ms, ops/sec, allocated MB,
  bytes/op, CPU %, and RAM delta for each of the five exercised files:
  `BigIntChunked`, `BigIntUtils`, `NumberDisplay`, `NumberDisplayScales`,
  `StringInt` (e.g. `FormatAbbreviated` on a 500-digit `BigInteger`, the
  `FormattedNumberCache` hit path at ~199M ops/sec, big-string add/sub/mul/div,
  and surgical hot paths like damage rolls running at over 23M ops/sec with zero allocations).
- **Process resource summary** — CPU time, working set / private memory, managed
  heap, plus the raw and abbreviated highest/lowest numbers and a
  tower envelope round-trip check (`1e1e100` tower input mapped to `1gp` through a chained ladder down to negative octo-vigintillion tiers), which is what makes the suffix table lockstep testable.
- Diagnostics: pass `--diag` / `--diagnostic` / `--trace`, or set
  `BIGINTBENCH_DIAGNOSTIC=1`.

A prior run is saved in `benchmark-output.txt` (UTF-16; 14/14 sanity checks
passed, total run time ~1.5 s on 12 logical processors).

## What's interesting

- **Short suffixes go farther than the base game.** The production
  `AbbrevScales` table goes out to $10^{10^{100}}$ (`gp`), versus the vanilla
  ~2.14x10^9 ("2.14 Billion or the max int32 integer") the unmodified client hard-codes.
- **Exponent delta is the key.** A number >= 10^exponent gets the suffix shown;
  the table is the contract trusted by both `client` and `server`, and it has to
  stay in lockstep with what the game writes to the ledger.
- **One implementation, two halves.** The `FNA Client Side` (client render) and
  `Server Side` (server persistence) versions are literally the same real
  one -- checked byte-for-byte against the production repo.

**Demetri Silver** | [dvsilver1198@hotmail.com](mailto:dvsilver1198@hotmail.com) | [View Resume](Resume.pdf)
