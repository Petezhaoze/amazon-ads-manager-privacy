namespace AmazonAdsManager.Api.Services;

public readonly record struct AmcDateSegment(DateOnly Start, DateOnly End);

public static class AmcCoveragePlanner
{
    public static IReadOnlyList<AmcDateSegment> ComputeMissingSegments(
        DateOnly requestedStart,
        DateOnly requestedEnd,
        IReadOnlySet<DateOnly> skipDates)
    {
        if (requestedStart > requestedEnd)
            return Array.Empty<AmcDateSegment>();

        var segments = new List<AmcDateSegment>();
        DateOnly? segStart = null;
        for (var d = requestedStart; d <= requestedEnd; d = d.AddDays(1))
        {
            if (skipDates.Contains(d))
            {
                if (segStart.HasValue)
                {
                    segments.Add(new AmcDateSegment(segStart.Value, d.AddDays(-1)));
                    segStart = null;
                }
            }
            else
            {
                segStart ??= d;
            }
        }
        if (segStart.HasValue)
            segments.Add(new AmcDateSegment(segStart.Value, requestedEnd));
        return segments;
    }

    public static IEnumerable<DateOnly> EnumerateDates(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
            yield return d;
    }
}
