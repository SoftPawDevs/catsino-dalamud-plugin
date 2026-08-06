using Catsino.Plugin.Contracts;
using Catsino.Plugin.Runtime;
using Catsino.Plugin.Workflow;

namespace Catsino.Plugin.Tests;

public sealed class DealerSessionStateTests
{
    [Fact]
    public void RosterStoreRejectsStaleAndCrossSessionResponses()
    {
        var store = new SessionRosterStore();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var oldA = store.BeginRefresh(sessionA);
        var revisionB = store.BeginRefresh(sessionB);
        var newA = store.BeginRefresh(sessionA);
        var now = DateTimeOffset.UtcNow;
        var rosterA = Roster(sessionA, "New Player", now);
        var rosterB = Roster(sessionB, "Other Player", now);

        Assert.True(store.TryApply(sessionA, newA, rosterA));
        Assert.True(store.TryApply(sessionB, revisionB, rosterB));
        Assert.False(store.TryApply(sessionA, oldA, Roster(sessionA, "Stale Player", now.AddMinutes(1))));
        Assert.False(store.TryApply(sessionA, newA, rosterB));
        var malformedRevision = store.BeginRefresh(sessionA);
        Assert.False(store.TryApply(sessionA, malformedRevision, rosterA with
        {
            Players = [rosterB.Players[0]],
        }));
        Assert.Equal("New Player", Assert.Single(store.Get(sessionA)!.Players).CharacterName);
        Assert.Equal("Other Player", Assert.Single(store.Get(sessionB)!.Players).CharacterName);
    }

    [Fact]
    public async Task RosterStoreCoalescesConcurrentRefreshesPerSession()
    {
        var store = new SessionRosterStore();
        var sessionId = Guid.NewGuid();
        var response = new TaskCompletionSource<SessionRosterDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<SessionRosterDto> Loader(Guid _, CancellationToken __)
        {
            Interlocked.Increment(ref calls);
            return response.Task;
        }

        var first = store.RefreshAsync(sessionId, Loader);
        var second = store.RefreshAsync(sessionId, Loader);
        response.SetResult(Roster(sessionId, "Exact Player", DateTimeOffset.UtcNow));
        await Task.WhenAll(first, second);

        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(sessionId, store.Get(sessionId)!.SessionId);
    }

    [Fact]
    public async Task RosterStoreReloadsAfterInvalidationDuringAnInflightRequest()
    {
        var store = new SessionRosterStore();
        var sessionId = Guid.NewGuid();
        var firstResponse = new TaskCompletionSource<SessionRosterDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondResponse = new TaskCompletionSource<SessionRosterDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<SessionRosterDto> Loader(Guid _, CancellationToken __)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return firstResponse.Task;
            }

            secondCall.TrySetResult();
            return secondResponse.Task;
        }

        var refresh = store.RefreshAsync(sessionId, Loader);
        store.Invalidate(sessionId);
        var joinedRefresh = store.RefreshAsync(sessionId, Loader);
        firstResponse.SetResult(Roster(sessionId, "Stale Player", DateTimeOffset.UtcNow));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(1));
        secondResponse.SetResult(Roster(sessionId, "Current Player", DateTimeOffset.UtcNow.AddSeconds(1)));
        await Task.WhenAll(refresh, joinedRefresh);

        Assert.Same(refresh, joinedRefresh);
        Assert.Equal(2, calls);
        Assert.Equal("Current Player", Assert.Single(store.Get(sessionId)!.Players).CharacterName);
    }

    [Fact]
    public void RosterStoreDoesNotReuseRevisionsAfterClear()
    {
        var store = new SessionRosterStore();
        var sessionId = Guid.NewGuid();
        var oldRevision = store.BeginRefresh(sessionId);
        store.Clear();
        var currentRevision = store.BeginRefresh(sessionId);

        Assert.False(store.TryApply(sessionId, oldRevision, Roster(sessionId, "Stale Player", DateTimeOffset.UtcNow)));
        Assert.True(store.TryApply(sessionId, currentRevision, Roster(sessionId, "Current Player", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void InviteCountdownHidesExpiredInvitesAndUsesServerExpiry()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        var invite = new PendingInviteDto(Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", now, now.AddSeconds(120));

        Assert.True(InviteCountdown.IsVisible(invite, now));
        Assert.Equal("2:00", InviteCountdown.Format(invite.ExpiresAt, now));
        Assert.False(InviteCountdown.IsVisible(invite, invite.ExpiresAt));
        Assert.Equal("Expired", InviteCountdown.Format(invite.ExpiresAt, invite.ExpiresAt.AddSeconds(1)));
    }

    [Fact]
    public void PendingInviteCanBeShownImmediatelyAndRemovedOnCancel()
    {
        var store = new SessionRosterStore();
        var now = DateTimeOffset.UtcNow;
        var invite = new PendingInviteDto(
            Guid.NewGuid(), Guid.NewGuid(), "Exact Player", "Ragnarok", now, now.AddMinutes(2));

        store.UpsertPendingInvite(invite);

        Assert.Equal(invite, Assert.Single(store.Get(invite.SessionId)!.PendingInvites));
        Assert.True(store.HasUnexpiredPendingInvites(now));

        store.RemovePendingInvite(invite.SessionId, invite.InviteId);

        Assert.Empty(store.Get(invite.SessionId)!.PendingInvites);
        Assert.False(store.HasUnexpiredPendingInvites(now));
    }

    [Fact]
    public void ActionDraftsAreKeyedBySessionAndMembership()
    {
        var store = new SessionActionDraftStore();
        var membershipId = Guid.NewGuid();
        var playerA = new SessionPlayerKey(Guid.NewGuid(), membershipId);
        var playerB = new SessionPlayerKey(Guid.NewGuid(), membershipId);

        store.SetBalanceAdjustment(playerA, "+100");
        store.SetBalanceAdjustment(playerB, "-50");
        store.SetNetZeroConfirmation(playerA, true);

        Assert.Equal("+100", store.GetBalanceAdjustment(playerA));
        Assert.Equal("-50", store.GetBalanceAdjustment(playerB));
        Assert.True(store.GetNetZeroConfirmation(playerA));
        Assert.False(store.GetNetZeroConfirmation(playerB));
    }

    [Fact]
    public void NegativeAdjustmentRetryRetainsItsIdempotencyKey()
    {
        var submission = new BalanceAdjustmentSubmission(new SessionPlayerKey(Guid.NewGuid(), Guid.NewGuid()), -100);
        var key = submission.IdempotencyKey;

        submission.MarkSending();
        submission.MarkFailed("temporary failure");
        submission.MarkSending();

        Assert.Equal(-100, submission.AmountGil);
        Assert.Equal(key, submission.IdempotencyKey);
    }

    [Fact]
    public void AmbiguousFailureCannotBeDiscardedButDefinitiveRejectionCan()
    {
        var player = new SessionPlayerKey(Guid.NewGuid(), Guid.NewGuid());
        var adjustment = new BalanceAdjustmentSubmission(player, -100);
        adjustment.MarkSending();
        adjustment.MarkFailed("connection lost");
        Assert.False(adjustment.CanDiscardFailure);

        adjustment.MarkSending();
        adjustment.MarkFailed("request rejected", canDiscard: true);
        Assert.True(adjustment.CanDiscardFailure);

        var cashOut = new CashOutSubmission(player, new CashOutPreviewResponse(100, 5m, 5, 95, false, []));
        cashOut.MarkSending();
        cashOut.MarkFailed("connection lost");
        Assert.False(cashOut.CanDiscardFailure);
    }

    private static SessionRosterDto Roster(Guid sessionId, string playerName, DateTimeOffset observedAt) => new(
        sessionId,
        [new SessionRosterPlayerDto(
            Guid.NewGuid(), sessionId, playerName, "Ragnarok", 100, 10, 110, 0,
            false, "none", "clear", observedAt)],
        [],
        observedAt);
}
