using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
using Catsino.Plugin.Ui;
using Catsino.Plugin.Workflow;

namespace Catsino.Plugin.Tests;

public sealed class ValidationAndSecurityTests
{
    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(1.001)]
    public void FeeMustRemainWithinRange(double value)
    {
        Assert.NotNull(DealerInputValidator.ValidateFee((decimal)value, GameSessionState.Created));
    }

    // The wire carries bare identifiers ("holdem"); nothing dealer-facing should print them raw.
    [Fact]
    public void GameTypesAreLabelledForTheDealer()
    {
        Assert.Equal("Plinko", GameTypeLabels.Label("plinko"));
        Assert.Equal("Blackjack", GameTypeLabels.Label("blackjack"));
        Assert.Equal("Hold'em", GameTypeLabels.Label("holdem"));
        Assert.Equal("Hold'em", GameTypeLabels.Label("HOLDEM"));
        Assert.Equal("Roulette", GameTypeLabels.Label("roulette"));
        // A game this plugin does not know about is still worth showing rather than hiding.
        Assert.Equal("baccarat", GameTypeLabels.Label("baccarat"));
        Assert.Equal("Unknown", GameTypeLabels.Label(null));
    }

    [Fact]
    public void SessionSummaryLeadsWithTheDealersOwnNumber()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new GameSessionDto(
            Guid.NewGuid(), "holdem", 0m, GameSessionState.Open, 2, 0, "none", "clear", now, now, null, 10, 1);
        Assert.Equal("#1 Hold'em | Open", GameTypeLabels.Summary(session));
        // A backend that does not send a number yet (or an archived row) simply drops the "#" prefix.
        Assert.Equal("Hold'em | Open", GameTypeLabels.Summary(session with { DealerSessionNumber = 0 }));
    }

    [Fact]
    public void MaxPlayersMustBePositiveAndFitTheGame()
    {
        Assert.NotNull(DealerInputValidator.ValidateMaxPlayers(0));
        Assert.Null(DealerInputValidator.ValidateMaxPlayers(50));                 // Plinko/Blackjack: no ceiling
        Assert.Null(DealerInputValidator.ValidateMaxPlayers(null, "holdem"));     // unset is a full table
        Assert.Null(DealerInputValidator.ValidateMaxPlayers(HoldemBetDefaults.MaxSeats, "holdem"));
        // A Hold'em table has a fixed number of seats, so the dealer is told before the request goes out.
        var tooMany = DealerInputValidator.ValidateMaxPlayers(HoldemBetDefaults.MaxSeats + 1, "holdem");
        Assert.NotNull(tooMany);
        Assert.Contains(HoldemBetDefaults.MaxSeats.ToString(), tooMany);
    }

    [Fact]
    public void MaxPlayersResolvesToAFullHoldemTableWhenUnset()
    {
        // Hold'em has no "unlimited": an empty field must reach the backend as the full seat count so the
        // stored cap matches the number of seats the dealer sees.
        Assert.Equal(HoldemBetDefaults.MaxSeats, DealerInputValidator.ResolveMaxPlayers(null, "holdem"));
        Assert.Equal(HoldemBetDefaults.MaxSeats, DealerInputValidator.ResolveMaxPlayers(99, "holdem"));
        Assert.Equal(6, DealerInputValidator.ResolveMaxPlayers(6, "holdem"));
        // Other games keep the existing "unset = unlimited" behaviour.
        Assert.Null(DealerInputValidator.ResolveMaxPlayers(null, "plinko"));
        Assert.Equal(99, DealerInputValidator.ResolveMaxPlayers(99, "blackjack"));
    }

    [Fact]
    public void FeeLocksAfterCreated()
    {
        Assert.Null(DealerInputValidator.ValidateFee(0m, GameSessionState.Created));
        Assert.NotNull(DealerInputValidator.ValidateFee(0m, GameSessionState.Open));
        Assert.NotNull(DealerInputValidator.ValidateFee(0m, GameSessionState.Closing));
        Assert.NotNull(DealerInputValidator.ValidateFee(0m, GameSessionState.Closed));
    }

    [Fact]
    public void DepositRequiresOpenMemberAndPositiveWholeLong()
    {
        var open = TestData.Player(SessionPlayerState.Open);
        var invited = TestData.Player(SessionPlayerState.Invited);

        Assert.Null(DealerInputValidator.ValidateDeposit(open, long.MaxValue));
        Assert.NotNull(DealerInputValidator.ValidateDeposit(open, 0));
        Assert.NotNull(DealerInputValidator.ValidateDeposit(invited, 1));
        // A shorthand that does not resolve to whole gil is refused (1.2345k would be 1234.5).
        Assert.False(DealerInputValidator.TryParseGilAmount("1.2345k", out _));
    }

    [Theory]
    [InlineData("500", 500L)]
    [InlineData("+500", 500L)]
    [InlineData("-500", -500L)]
    public void SignedBalanceAdjustmentAllowsPositiveAndNegativeButNotZero(string text, long expected)
    {
        Assert.True(DealerInputValidator.TryParseBalanceAdjustment(text, out var amount));
        Assert.Equal(expected, amount);
        Assert.Null(DealerInputValidator.ValidateBalanceAdjustment(amount));
        Assert.False(DealerInputValidator.TryParseBalanceAdjustment("0", out _));
        Assert.NotNull(DealerInputValidator.ValidateBalanceAdjustment(0));
        Assert.False(DealerInputValidator.TryParseBalanceAdjustment(long.MinValue.ToString(), out _));
        Assert.NotNull(DealerInputValidator.ValidateBalanceAdjustment(long.MinValue));
    }

    [Fact]
    public void FailedDepositRetryRetainsIdempotencyKeyAndRedactsSecrets()
    {
        var submission = new DepositSubmission(Guid.NewGuid(), Guid.NewGuid(), 100);
        var key = submission.IdempotencyKey;
        submission.MarkSending();
        submission.MarkFailed("Bearer secret eyJabc.def.ghi refreshCredential=hidden");

        Assert.Equal(key, submission.IdempotencyKey);
        Assert.DoesNotContain("secret", submission.ResultMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJabc", submission.ResultMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", submission.ResultMessage, StringComparison.Ordinal);

        submission.MarkSending();
        Assert.Equal(key, submission.IdempotencyKey);
    }

    [Theory]
    [InlineData("Single", "Ragnarok")]
    [InlineData("First Last", "Bad World")]
    [InlineData("First Last", "World;command")]
    public void ExactCharacterIdentityRejectsUnsafeInput(string name, string world)
    {
        Assert.NotNull(DealerInputValidator.ValidateCharacter(name, world));
    }

    [Fact]
    public void TellCommandUsesNativeCrossWorldSyntax()
    {
        var inviteUrl = new Uri("https://152-53-121-56.sslip.io/invite?token=abc123");

        var command = GameChat.BuildTellCommand("Rhe'kash Tia", "Phoenix", inviteUrl);

        Assert.Equal("/tell Rhe'kash Tia@Phoenix https://152-53-121-56.sslip.io/invite?token=abc123", command);
    }

    [Fact]
    public void TellCommandRejectsMessagesOverTheGameChatLimit()
    {
        var inviteUrl = new Uri($"https://example.com/invite?token={new string('a', 500)}");

        Assert.Throws<ArgumentException>(() => GameChat.BuildTellCommand("First Last", "Phoenix", inviteUrl));
    }
}
