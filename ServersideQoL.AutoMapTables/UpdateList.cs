namespace ServersideQoL.AutoMapTables;

internal static class UpdateList
{
  public static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    where TKey : notnull
    => dictionary.TryAdd(key, value);
}
