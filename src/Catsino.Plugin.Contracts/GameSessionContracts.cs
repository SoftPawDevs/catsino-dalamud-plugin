namespace Catsino.Plugin.Contracts;

public enum GameSessionState
{
    Created,
    Open,
    Closing,
    Closed,
}

public enum SessionPlayerState
{
    Invited,
    Open,
    Closed,
}

public sealed record GameSessionDto(
    Guid SessionId,
    string GameType,
    decimal FeePercent,
    GameSessionState State,
    int PlayerCount,
    long TotalDepositedGil,
    string PayoutState,
    string ReconciliationState,
    DateTimeOffset CreatedAt,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? ClosedAt);

public sealed record CreateGameSessionRequest(string GameType, decimal FeePercent);

public sealed record UpdateSessionFeeRequest(decimal FeePercent);

public sealed record SessionPlayerDto(
    Guid PlayerId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    SessionPlayerState State,
    long DepositedGil,
    long? PayoutGil,
    string PayoutState,
    string ReconciliationState,
    DateTimeOffset JoinedAt);

public sealed record CreateInviteRequest(string CharacterName, string HomeWorld);

public sealed record InviteDto(
    Guid InviteId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    Uri InviteUrl,
    DateTimeOffset ExpiresAt);

public sealed record SessionRosterPlayerDto(
    Guid MembershipId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long BalanceGil,
    long NetGil,
    long Tokens,
    long ReservedTokens,
    bool BettingLocked,
    string PayoutState,
    string ReconciliationState,
    DateTimeOffset JoinedAt);

public sealed record PendingInviteDto(
    Guid InviteId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record SessionRosterDto(
    Guid SessionId,
    IReadOnlyList<SessionRosterPlayerDto> Players,
    IReadOnlyList<PendingInviteDto> PendingInvites,
    DateTimeOffset ObservedAt);

public sealed record AdjustPlayerBalanceRequest(long AmountGil);

public sealed record DealerCashOutRequest(
    bool ConfirmAllAvailable,
    bool ConfirmNetZero,
    long ExpectedGross,
    long ExpectedFee,
    long ExpectedNet);

public sealed record SessionRemovalDto(Guid SessionId, string Mode);

public sealed record CreateManualDepositRequest(Guid PlayerId, long AmountGil);

public sealed record DepositDto(
    Guid DepositId,
    Guid SessionId,
    Guid PlayerId,
    long AmountGil,
    Guid IdempotencyKey,
    DateTimeOffset RecordedAt);
