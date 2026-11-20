using System.Globalization;
using Lodur;

namespace Lodur.Core.Tests;

/// <summary>
/// The Actor against Bad Day Simulator's pain box at 9f59764, which that repo deletes in its next cut:
/// pain-box-golden.tsv holds, for 7 intensity/pull settings x seeds 1-60, the outcome and a hash of the
/// per-tick stimulus, pain, strain, resolve and goal stream. Regenerate with tools/pain-box-golden/generate.sh.
/// </summary>
public sealed class GoldenParityTests
{
    private sealed record Row(ulong Seed, double Intensity, double Pull, string Outcome, long BreakMs, long EndMs, int Flinches, long Ticks, ulong Hash);

    private static IEnumerable<Row> Rows()
    {
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "pain-box-golden.tsv")))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var f = line.Trim().Split(' ');
            yield return new Row(ulong.Parse(f[0]), double.Parse(f[1], CultureInfo.InvariantCulture), double.Parse(f[2], CultureInfo.InvariantCulture),
                f[3], long.Parse(f[4]), long.Parse(f[5]), int.Parse(f[6]), long.Parse(f[7]), Convert.ToUInt64(f[8], 16));
        }
    }

    [Fact]
    public void The_actor_reproduces_the_pain_box_golden_table_exactly()
    {
        var rows = Rows().ToList();
        Assert.Equal(420, rows.Count);
        Assert.Contains(rows, r => r.Outcome == "Endured");
        Assert.Contains(rows, r => r.Outcome == "WillBroke");

        var bad = new List<string>();
        foreach (var row in rows)
        {
            var replay = new PainBoxReplay(row.Seed, row.Intensity, row.Pull);
            var h = 14695981039346656037ul;
            void Byte(byte b) { h ^= b; h *= 1099511628211ul; }
            void Double(double d) { foreach (var b in BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(d))) Byte(b); }

            long ticks = 0, broke = -1, withdrew = -1;
            var flinches = 0;
            var max = (long)Math.Round(120.0 / PainBoxReplay.Dt);
            for (long i = 0; i <= max && withdrew < 0; i++)
            {
                var e = replay.Tick();
                ticks++;
                if ((e & TickEvents.Flinch) != 0) flinches++;
                if ((e & TickEvents.WillBroke) != 0) broke = i;
                if ((e & TickEvents.GoalChanged) != 0) withdrew = i;
                var d = replay.Actor.Drive;
                Double(replay.Actor.Stimulus(0)); Double(d.Pain); Double(d.Strain); Double(d.Resolve);
                Byte((byte)(replay.Actor.Drive.WillBroken ? 1 : 0));
            }

            var ms = (long)Math.Round(withdrew >= 0 ? withdrew * 10.0 : max * 10.0);
            var got = $"{(broke >= 0 ? "WillBroke" : "Endured")} {(broke >= 0 ? broke * 10 : -1)} {ms} {flinches} {ticks} {h:x16}";
            var want = $"{row.Outcome} {row.BreakMs} {row.EndMs} {row.Flinches} {row.Ticks} {row.Hash:x16}";
            if (got != want) bad.Add($"seed {row.Seed} i={row.Intensity} p={row.Pull}: got [{got}] want [{want}]");
        }

        Assert.True(bad.Count == 0, $"{bad.Count} of {rows.Count} runs differ; first: {bad.FirstOrDefault()}");
    }
}
