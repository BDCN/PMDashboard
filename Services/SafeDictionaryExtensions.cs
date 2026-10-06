namespace PMDashboard.Services;

public static class SafeDictionaryExtensions
{
    /// <summary>
    /// Case-insensitive-by-key dictionary that tolerates duplicate keys in the source (which
    /// Enumerable.ToDictionary throws ArgumentException on — "An item with the same key has
    /// already been added") by keeping the most-recently-modified row per key instead of
    /// crashing. Import caches (Location/Site/Romp lookups keyed by Name) use this because the
    /// app has no DB-level uniqueness on those names — two machines independently auto-creating
    /// the same code before syncing (or a stray manual duplicate) produces two rows with the
    /// same name, and that shouldn't be able to take down every future import from that point on.
    /// </summary>
    public static Dictionary<string, T> ToSafeNameDictionary<T>(
        this IEnumerable<T> source, Func<T, string> keySelector, Func<T, DateTime> lastModifiedSelector)
        => source
            .GroupBy(keySelector, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(lastModifiedSelector).First(),
                StringComparer.OrdinalIgnoreCase);
}
