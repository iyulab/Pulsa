namespace PulsaRedact;

/// <summary>One detector look at a video: the time of the frame and the regions found in it that are to be hidden.</summary>
internal readonly record struct SampledLook(double Time, IReadOnlyList<DetectedRegion> Regions);

/// <summary>
/// Turns what a detector found in frames taken at several times into rectangles that each hold for a stretch of time.
/// A region is held from its frame until the next one; when the next frame holds the same kind of thing close by, the
/// rectangle between the two frames is the union of both positions, so what moved a little in between is still covered.
/// What is nowhere in the next frame ends there. Nothing is assumed about the time between two frames beyond that, so the
/// closer the frames, the tighter the cover.
/// </summary>
internal static class RegionTrack
{
    /// <summary>The stretches of time to hide, for looks sorted by time, up to <paramref name="end"/> seconds.</summary>
    public static List<TimedRegion> Build(IReadOnlyList<SampledLook> looks, double end)
    {
        var stretches = new List<TimedRegion>();
        for (var i = 0; i < looks.Count; i++)
        {
            var from = looks[i].Time;
            var to = i + 1 < looks.Count ? looks[i + 1].Time : end;
            if (to <= from) continue;

            var next = i + 1 < looks.Count ? looks[i + 1].Regions : [];
            var taken = new bool[next.Count];
            foreach (var found in looks[i].Regions)
            {
                var partner = BestPartner(found, next, taken);
                var rectangle = partner is null ? found.Region : Union(found.Region, next[partner.Value].Region);
                if (partner is not null) taken[partner.Value] = true;
                Add(stretches, new TimedRegion(rectangle, from, to));
            }
        }
        return stretches;
    }

    /// <summary>The index of the unclaimed region of the same label that overlaps <paramref name="found"/> most, if any overlaps.</summary>
    private static int? BestPartner(DetectedRegion found, IReadOnlyList<DetectedRegion> next, bool[] taken)
    {
        int? best = null;
        long bestOverlap = 0;
        for (var j = 0; j < next.Count; j++)
        {
            if (taken[j] || !string.Equals(next[j].Label, found.Label, StringComparison.OrdinalIgnoreCase)) continue;
            var overlap = OverlapArea(found.Region, next[j].Region);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = j;
            }
        }
        return best;
    }

    private static long OverlapArea(RedactRegion a, RedactRegion b)
    {
        var width = Math.Min((long)a.X + a.Width, (long)b.X + b.Width) - Math.Max(a.X, b.X);
        var height = Math.Min((long)a.Y + a.Height, (long)b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static RedactRegion Union(RedactRegion a, RedactRegion b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max((long)a.X + a.Width, (long)b.X + b.Width);
        var bottom = Math.Max((long)a.Y + a.Height, (long)b.Y + b.Height);
        return new RedactRegion(left, top, (int)(right - left), (int)(bottom - top));
    }

    /// <summary>A rectangle that stays put across frames is one stretch, not one per frame.</summary>
    private static void Add(List<TimedRegion> stretches, TimedRegion stretch)
    {
        for (var i = stretches.Count - 1; i >= 0; i--)
        {
            if (stretches[i].End != stretch.Start) continue;
            if (stretches[i].Region != stretch.Region) continue;
            stretches[i] = stretches[i] with { End = stretch.End };
            return;
        }
        stretches.Add(stretch);
    }
}
