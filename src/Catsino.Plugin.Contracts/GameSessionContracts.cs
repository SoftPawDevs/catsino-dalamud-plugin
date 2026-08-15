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
    int? MaxPlayers = null,
    // Short per-dealer label to show in the session list ("#1", "#2", …). Unique among that dealer's
    // live sessions; a deleted session's number is reused by the next one created.
    int DealerSessionNumber = 0);

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

// === Texas Hold'em (dealer side) ===
// Player-versus-player: the dealer does not play a hand, they only start each one — the backend runs the
// streets, the betting rules and the settlement. This view therefore NEVER contains a hole card, not even
// after a showdown, so the dealer cannot leak one.
public sealed record HoldemSeatDto(
    Guid MembershipId,
    string Name,
    string HomeWorld,
    int SeatIndex,
    long Tokens,
    long Committed,
    long TotalCommitted,
    IReadOnlyList<BlackjackCardDto> Cards,
    bool HasHiddenCards,
    // waiting (seated, not in the current hand) | playing | folded | allIn | won | lost
    string Status,
    bool IsActive,
    bool IsButton,
    bool IsSmallBlind,
    bool IsBigBlind,
    bool SittingOut,
    string? HandDescription,
    long? Payout,
    long? Net);

public sealed record HoldemPotDto(long Amount, IReadOnlyList<Guid> EligibleMembershipIds);

// Status is idle|waitingForPlayers|preflop|flop|turn|river|showdown|settled.
public sealed record HoldemTableDto(
    Guid SessionId,
    Guid? RoundId,
    string Status,
    IReadOnlyList<HoldemSeatDto> Seats,
    IReadOnlyList<BlackjackCardDto> Board,
    IReadOnlyList<HoldemPotDto> Pots,
    long TotalPot,
    long CurrentBet,
    long MinRaise,
    long SmallBlind,
    long BigBlind,
    Guid? ActiveMembershipId,
    Guid? ViewerMembershipId,
    int SeatCapacity,
    DateTimeOffset? DeadlineAt,
    IReadOnlyList<string> AvailableActions,
    long CallAmount,
    long MinRaiseTo,
    long MaxRaiseTo,
    DateTimeOffset ObservedAt);

public sealed record HoldemDealRequest(Guid SessionId);

// === Roulette (dealer side) ===
// European wheel, 37 pockets. Nothing is secret at this table, so the dealer sees the same view the
// players do: every stake, who placed it, and the winning number once the dealer spins.
public sealed record RouletteBetDto(
    Guid MembershipId,
    string Name,
    // straight|split|street|corner|sixLine|column|dozen|red|black|odd|even|low|high
    string Type,
    IReadOnlyList<int> Selection,
    long Amount,
    long? Payout,
    long? Net);

// Status is idle|betting|spinning|settled. While spinning, DeadlineAt is when the ball lands — the plugin
// animates the wheel against it and the payouts book at that moment, not before.
public sealed record RouletteTableDto(
    Guid SessionId,
    Guid? RoundId,
    string Status,
    IReadOnlyList<RouletteBetDto> Bets,
    long TotalStaked,
    int? WinningNumber,
    string? WinningColor,
    IReadOnlyList<int> RecentNumbers,
    long MinBet,
    long MaxBet,
    IReadOnlyList<long> ChipDenominations,
    Guid? ViewerMembershipId,
    DateTimeOffset? DeadlineAt,
    DateTimeOffset ObservedAt);

public sealed record RouletteSpinRequest(Guid SessionId);

public static class RouletteBetDefaults
{
    // Exactly the length of roulette_spin.ogg, the clip both surfaces play while the wheel turns.
    public const int SpinMilliseconds = 6600;
    public const int ResultsVisibleSeconds = 10;
    public const int PocketCount = 37;
}

// Dealer records a payout made OUTSIDE the game (a marketboard sale when the amount is too large to hand
// over in 1M trades) and clears the player from the table. No trade runs and no payout leg is created, so
// the confirmation echoes the exact quote the dealer was shown.
public sealed record ManualSettlementRequest(
    bool ConfirmAllAvailable,
    long ExpectedGross,
    long ExpectedFee,
    long ExpectedNet);

public static class HoldemBetDefaults
{
    public const int TurnSeconds = 45;
    // Players only — the dealer never takes a seat. A session's MaxPlayers may narrow a table below this,
    // never above it; the backend clamps anything larger when the session is created.
    public const int MaxSeats = 10;
}
