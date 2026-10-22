namespace ServersideQoL.AutoMapTables;

static class WardAccess
{
  internal readonly record struct Snapshot(bool Enabled, float DistanceXZ, float Radius, long Creator, long[] Permitted);

  internal sealed class TableAccess(Snapshot[] covering)
  {
    public bool CanWrite(long characterID) => WardAccess.CanWrite(covering, static ward => ward.Enabled,
      ward => ward.Creator == characterID || Array.IndexOf(ward.Permitted, characterID) >= 0);
  }

  internal static float ReadRadius(string marker, string componentType, float prefabRadius,
    Func<string, bool> getBool, Func<string, float, float> getFloat)
    => getBool(marker) && getBool(marker + componentType)
      ? getFloat(componentType + ".m_radius", prefabRadius) : prefabRadius;

  public static bool Covers(float distanceXZ, float radius) => distanceXZ < radius;

  public static bool CanWrite<TWard>(IEnumerable<TWard>? wards, Func<TWard, bool> isEnabled, Func<TWard, bool> isPermitted,
    Func<TWard, bool>? covers = null)
  {
    var restricted = false;
    if (wards is not null)
    {
      foreach (var ward in wards)
      {
        if (!isEnabled(ward) || (covers is not null && !covers(ward)))
          continue;
        if (isPermitted(ward))
          return true;
        restricted = true;
      }
    }
    return !restricted;
  }

  public static bool HasActiveWard<TWard>(IEnumerable<TWard>? wards, Func<TWard, bool> isEnabled)
  {
    if (wards is null)
      return false;

    foreach (var ward in wards)
    {
      if (isEnabled(ward))
        return true;
    }

    return false;
  }
}
