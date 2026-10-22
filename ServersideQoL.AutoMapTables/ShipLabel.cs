namespace ServersideQoL.AutoMapTables;

internal static class ShipLabel
{
  internal static string Format(string ship, string? builder) => string.IsNullOrWhiteSpace(builder) ? ship : $"{ship} ({builder})";
}
