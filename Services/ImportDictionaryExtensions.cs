namespace PMDashboard.Services;

internal static class ImportDictionaryExtensions
{
    /// <summary>
    /// Builds a lookup like ToDictionary, but keeps the last item on a duplicate key
    /// instead of throwing.
    ///
    /// None of Orders, Sites or Locations has a unique constraint on the columns the
    /// import keys off — Orders on (CustomerOrder, OrdLine), Sites and Locations on
    /// Name — so a table that already contains duplicates made every subsequent import
    /// abort with "An item with the same key has already been added". Rows are returned
    /// in Id order, so "last wins" resolves to the most recently inserted duplicate,
    /// which is the row an upsert should be updating.
    /// </summary>
    public static Dictionary<TKey, TSource> ToDictionaryLastWins<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        var dict = new Dictionary<TKey, TSource>(comparer);
        foreach (var item in source)
            dict[keySelector(item)] = item;
        return dict;
    }

    /// <summary>
    /// Same as <see cref="ToDictionaryLastWins{TSource,TKey}"/> but also reports how many
    /// items collided, so an import can tell the user its source data contained duplicates.
    /// </summary>
    public static Dictionary<TKey, TSource> ToDictionaryLastWins<TSource, TKey>(
        this IEnumerable<TSource> source,
        Func<TSource, TKey> keySelector,
        out int duplicateCount,
        IEqualityComparer<TKey>? comparer = null) where TKey : notnull
    {
        var dict = new Dictionary<TKey, TSource>(comparer);
        duplicateCount = 0;
        foreach (var item in source)
        {
            var key = keySelector(item);
            if (dict.ContainsKey(key)) duplicateCount++;
            dict[key] = item;
        }
        return dict;
    }
}
