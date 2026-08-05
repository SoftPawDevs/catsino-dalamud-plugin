using Dalamud.Configuration;

namespace Catsino.Plugin.Configuration;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Guid DeviceId { get; set; } = Guid.NewGuid();

    public string ApiBaseUrl { get; set; } = "https://api.catsino.invalid/";
}
