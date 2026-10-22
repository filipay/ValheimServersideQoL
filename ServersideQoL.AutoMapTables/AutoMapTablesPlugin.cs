using BepInEx.Configuration;
using HarmonyLib;

namespace ServersideQoL.AutoMapTables;

partial class AutoMapTablesPlugin : ServersideQoLPluginBase<AutoMapTablesPlugin, Config>
{
  public static readonly int PluginGuidHash = PluginGuid.GetStableHashCode();
  internal static Harmony HarmonyInstance { get; } = new(PluginGuid);

  protected override Config CreateConfigSingleton(ConfigFile configFile, Logger logger) => new(configFile, logger);

  protected override void RegisterProcessors(IProcessorCollection processors) => processors
    .Add<AutoMapTablesProcessor>();
}
