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
    DateTimeOffset? ClosedAt,
    int? MaxPlayers = null);

public static class PlinkoBetDefaults
{
    public const long MinBet = 50_000;
    public const long MaxBet = 1_000_000;
}

public sealed record CreateGameSessionRequest(string GameType, decimal FeePercent, long? MinBet = null, long? MaxBet = null, int? MaxPlayers = null);

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

public sealed record CreateInviteRequest(string CharacterName, string HomeWorld, long InitialBalanceGil);

public sealed record InviteDto(
    Guid InviteId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long InitialBalanceGil,
    Uri InviteUrl,
    DateTimeOffset ExpiresAt);

public sealed record SessionRosterPlayerDto(
    Guid MembershipId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long Tokens,
    long Deposit,
    bool BettingLocked,
    string PayoutState,
    string ReconciliationState,
    DateTimeOffset JoinedAt,
    DateTimeOffset? CashOutRequestedAt = null);

public sealed record PendingInviteDto(
    Guid InviteId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    long InitialBalanceGil,
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

// === Blackjack (dealer side) ===
// Rank 1 = Ace, 11 = J, 12 = Q, 13 = K. Suit 0 = Clubs, 1 = Diamonds, 2 = Hearts, 3 = Spades.
public sealed record BlackjackCardDto(int Rank, int Suit);

public sealed record BlackjackSeatDto(
    Guid MembershipId,
    string Name,
    string HomeWorld,
    long Tokens,
    long Bet,
    IReadOnlyList<BlackjackCardDto> Cards,
    int Value,
    bool IsBust,
    bool IsBlackjack,
    string Status,
    bool IsActive,
    long? Payout,
    long? Net);

// Shared table view. While DealerHasHiddenCard is true the hole card is withheld: DealerCards holds only the
// up-card(s) and DealerValue is their value. Status is idle|betting|playerTurns|dealerTurn|settled.
public sealed record BlackjackTableDto(
    Guid SessionId,
    Guid? RoundId,
    string Status,
    IReadOnlyList<BlackjackSeatDto> Seats,
    IReadOnlyList<BlackjackCardDto> DealerCards,
    bool DealerHasHiddenCard,
    int DealerValue,
    Guid? ActiveMembershipId,
    bool DealerTurn,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset ObservedAt);

public sealed record BlackjackDealRequest(Guid SessionId);
public sealed record BlackjackDealerActionRequest(Guid SessionId);
