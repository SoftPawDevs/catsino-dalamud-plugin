using Dalamud.Configuration;

namespace Catsino.Plugin.Configuration;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Guid DeviceId { get; set; } = Guid.NewGuid();

    public string ApiBaseUrl { get; set; } = "https://152-53-121-56.sslip.io/";

    public decimal DefaultDealerFeePercent { get; set; }

    public long DefaultMinBet { get; set; } = Contracts.PlinkoBetDefaults.MinBet;

    public long DefaultMaxBet { get; set; } = Contracts.PlinkoBetDefaults.MaxBet;

    // Optional per-plugin default player cap that pre-fills newly created sessions. null = unlimited.
    public int? DefaultMaxPlayers { get; set; }
}
