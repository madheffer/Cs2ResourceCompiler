using System.Globalization;
using System.Numerics;

namespace Source2.Compiler.Maps.Decompile;

/// <summary>
/// A point_template member's placement back from its copy (EntityLumpSet.Collect places the
/// copy through <see cref="TemplateTransform.Relative"/>). The copy's translation is the
/// template's inverse applied to the member's origin alone, and its angles read back from the
/// rotations alone, so the two are solved apart; each is a value the forward takes to the
/// copy's bit for bit. The matrix inverse is only the first guess: near a pitch of 90 the read
/// back is ill-conditioned (atixref's lights at 90 come back 89.99998 and land 89.999992), so
/// the guess's decimal roundings and its neighbouring floats are tried too, fewest decimals
/// first. Several values may land; the copy keeps nothing that tells them apart, and the one
/// with the fewest decimals, as a mapper types it, is taken.
/// </summary>
internal static class TemplatePlacement
{
    /// <summary>The member's origin and angles, or null when no candidate lands.</summary>
    /// <param name="originText">The copy's origin as the lump printed it, when the lump writes
    /// placement as "%f" strings (an older compile, ze_sisyphus): its floats are past six decimals
    /// gone, so a candidate lands when the forward's float prints to that text (SettleWorld.Fixed6),
    /// not on the parsed value's bits. Null for today's float arrays.</param>
    public static (Vector3 Origin, Vector3 Angles)? Solve(Vector3 templateOrigin, Vector3 templateAngles,
                                                          Vector3 copyOrigin, Vector3 copyAngles,
                                                          string[]? originText = null, string[]? anglesText = null)
    {
        using var profiled = DecompileProfile.Time("TemplatePlacement.Solve");
        var template = TemplateTransform.AngleMatrix(templateOrigin, templateAngles);
        var inverse = TemplateTransform.Invert(template);
        var guess = TemplateTransform.Concat(template, TemplateTransform.AngleMatrix(copyOrigin, copyAngles));

        var origin = Search(new Vector3(guess[3], guess[7], guess[11]), copyOrigin, originText, o =>
        {
            var m = TemplateTransform.Concat(inverse, TemplateTransform.AngleMatrix(o, Vector3.Zero));
            return new Vector3(m[3], m[7], m[11]);
        });
        var angles = Search(TemplateTransform.MatrixAngles(guess), copyAngles, anglesText,
                            a => TemplateTransform.MatrixAngles(TemplateTransform.Concat(inverse, TemplateTransform.AngleMatrix(Vector3.Zero, a))),
                            turns: true);
        return origin is { } o && angles is { } a ? (o, a) : null;
    }

    /// <summary>The cheapest candidate <paramref name="forward"/> takes to <paramref name="want"/>.</summary>
    private static Vector3? Search(Vector3 guess, Vector3 want, string[]? printed, Func<Vector3, Vector3> forward, bool turns = false)
    {
        var xs = Candidates(guess.X, turns);
        var ys = Candidates(guess.Y, turns);
        var zs = Candidates(guess.Z, turns);
        var trials = from x in xs from y in ys from z in zs
                     orderby x.Cost + y.Cost + z.Cost
                     select new Vector3(x.Value, y.Value, z.Value);
        foreach (var v in trials)
            if (printed is { Length: 3 } ? Prints(forward(v), printed) : Same(forward(v), want))
                return v;
        return null;
    }

    /// <summary>One component's candidates and their cost: the guess rounded to 0 to 6
    /// decimals (cost the decimals), the guess (7) and the floats up to 16 steps from it
    /// (7 plus the steps).</summary>
    private static List<(float Value, int Cost)> Candidates(float guess, bool turns)
    {
        var list = new List<(float Value, int Cost)>();
        var seen = new HashSet<int>();
        void Add(float v, int cost)
        {
            if (float.IsFinite(v) && seen.Add(BitConverter.SingleToInt32Bits(v)))
                list.Add((v, cost));
        }
        for (var d = 0; d <= 6; d++)
            Add(float.Parse(Math.Round((double)guess, d, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture),
                            CultureInfo.InvariantCulture), d);
        // The same angle a turn away, as a mapper may type it (180 or -180): in floats they are not
        // the same rotation, sin(pi) carrying its rounding with the sign, so only one may land
        // (ze_sisyphus' point_teleport 857 under a template at yaw 180).
        if (turns)
            for (var d = 0; d <= 6; d++)
            {
                var r = Math.Round((double)guess, d, MidpointRounding.AwayFromZero);
                foreach (var t in (double[])[r + 360, r - 360])
                    if (Math.Abs(t) <= 360)
                        Add((float)t, d + 1);
            }
        Add(guess, 7);
        float up = guess, down = guess;
        for (var k = 1; k <= 16; k++)
        {
            Add(up = MathF.BitIncrement(up), 7 + k);
            Add(down = MathF.BitDecrement(down), 7 + k);
        }
        return list;
    }

    private static bool Prints(Vector3 v, string[] text)
        => SettleWorld.Fixed6(v.X) == text[0] && SettleWorld.Fixed6(v.Y) == text[1] && SettleWorld.Fixed6(v.Z) == text[2];

    private static bool Same(Vector3 a, Vector3 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
           && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y)
           && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
