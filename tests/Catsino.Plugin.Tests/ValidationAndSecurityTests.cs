using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Security;
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
        Assert.False(DealerInputValidator.TryParseGil("1.5", out _));
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
