namespace Catsino.Plugin.Contracts;

public sealed record CharacterIdentityDto(
    string CharacterName,
    string HomeWorld,
    string CurrentWorld,
    bool IsLoggedIn);

public sealed record DropboxCapabilitiesDto(
    bool IsAvailable,
    string? IpcVersion,
    string? BuildVersion,
    IReadOnlyList<string> Capabilities,
    bool SupportsLanguageIndependentTradeState,
    Guid? PluginInstanceId);

public sealed record AuthorizeDealerRequest(
    string ActivationJwt,
    CharacterIdentityDto Character,
    Guid DeviceId,
    string PluginVersion,
    string ContractVersion,
    DropboxCapabilitiesDto Dropbox);

public sealed record RefreshDealerRequest(
    string RefreshCredential,
    CharacterIdentityDto Character,
    Guid DeviceId,
    string PluginVersion,
    string ContractVersion,
    DropboxCapabilitiesDto Dropbox);

public sealed record DealerAuthorizationDto(
    Guid PairingId,
    Guid DealerId,
    string AccessToken,
    string RefreshCredential,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset AuthorizedAt);

public sealed record PluginPairingRequest(
    Guid DeviceId,
    CharacterIdentityDto Character,
    string PluginVersion,
    string ContractVersion,
    DropboxCapabilitiesDto Dropbox);

public sealed record PluginPairingDto(
    Guid PairingId,
    DateTimeOffset PairedAt,
    DateTimeOffset LastHeartbeatAt);

public sealed record PluginHeartbeatRequest(
    Guid PairingId,
    Guid DeviceId,
    CharacterIdentityDto Character,
    string PluginVersion,
    string ContractVersion,
    DropboxCapabilitiesDto Dropbox,
    int PendingOutboxEvents,
    DateTimeOffset SentAt);

public sealed record DropboxStatusDto(
    bool IsAvailable,
    bool IsCompatible,
    bool PayoutModeEnabled,
    Guid? ActiveOperationId,
    string? Status,
    DateTimeOffset ObservedAt);

public sealed record ApiErrorDto(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? ValidationErrors = null,
    Guid? TraceId = null);
