using Catsino.Plugin.Contracts;
using Catsino.Plugin.Payout;

namespace Catsino.Plugin.Tests;

public sealed class PayoutBatchCoordinatorTests
{
    private static readonly Guid ExecutorInstanceId = Guid.NewGuid();

    private static CashOutBatchPlan Plan(Guid cashOutId, params long[] nets) => new(
        cashOutId,
        Guid.NewGuid(),
        "Exact Player",
        "Ragnarok",
        nets.Select((net, index) => new CashOutBatchLegPlan(index + 1, net)).ToList());

    [Fact]
    public async Task Full_success_runs_every_leg_and_settles_once_with_all_completed()
    {
        var executor = new FakeExecutor();
        var store = new FakeStore();
        var settle = new FakeSettlement();
        using var coordinator = new PayoutBatchCoordinator(executor, store, settle, () => true, _ => { });
        var cashOutId = Guid.NewGuid();

        await coordinator.StartBatchAsync(Plan(cashOutId, 1_000_000, 1_000_000, 500_000));
        await RunLegAsync(executor, PayoutTradeEventType.TradeCompleted);
        await WaitAsync(() => executor.StartCount == 2);
        await RunLegAsync(executor, PayoutTradeEventType.TradeCompleted);
        await WaitAsync(() => executor.StartCount == 3);
        await RunLegAsync(executor, PayoutTradeEventType.TradeCompleted);
        await WaitAsync(() => settle.Requests.Count == 1);

        var request = Assert.Single(settle.Requests);
        Assert.Equal(cashOutId, request.CashOutId);
        Assert.Equal(new[] { 1, 2, 3 }, request.Legs.Select(x => x.Number));
        Assert.All(request.Legs, leg => Assert.Equal("completed", leg.Outcome));
        Assert.False(coordinator.HasActiveBatch);
        Assert.Empty(store.Saved); // durable batch cleared after settlement
    }

    [Fact]
    public async Task Failed_leg_stops_the_batch_and_settles_the_paid_legs_plus_the_failure()
    {
        var executor = new FakeExecutor();
        var settle = new FakeSettlement();
        using var coordinator = new PayoutBatchCoordinator(executor, new FakeStore(), settle, () => true, _ => { });

        await coordinator.StartBatchAsync(Plan(Guid.NewGuid(), 1_000_000, 1_000_000, 500_000));
        await RunLegAsync(executor, PayoutTradeEventType.TradeCompleted);
        await WaitAsync(() => executor.StartCount == 2);
        await RunLegAsync(executor, PayoutTradeEventType.TradeCancelled);
        await WaitAsync(() => settle.Requests.Count == 1);

        var request = Assert.Single(settle.Requests);
        Assert.Equal(2, request.Legs.Count); // leg 3 never attempted → omitted → backend releases it
        Assert.Equal("completed", request.Legs[0].Outcome);
        Assert.Equal("failed", request.Legs[1].Outcome);
        Assert.Equal(2, executor.StartCount);
    }

    [Fact]
    public async Task Ambiguous_leg_is_reported_ambiguous()
    {
        var executor = new FakeExecutor();
        var settle = new FakeSettlement();
        using var coordinator = new PayoutBatchCoordinator(executor, new FakeStore(), settle, () => true, _ => { });

        await coordinator.StartBatchAsync(Plan(Guid.NewGuid(), 1_000_000, 500_000));
        executor.Emit(PayoutTradeEventType.TradeOpened);
        await WaitAsync(() => executor.OpenPersisted.Count == 1);
        executor.Emit(PayoutTradeEventType.TradeFailed, ambiguous: true);
        await WaitAsync(() => settle.Requests.Count == 1);

        Assert.Equal("ambiguous", Assert.Single(settle.Requests).Legs.Single().Outcome);
    }

    [Fact]
    public async Task Gil_only_moves_after_the_trade_opened_marker_is_durable()
    {
        var executor = new FakeExecutor();
        var store = new FakeStore();
        using var coordinator = new PayoutBatchCoordinator(executor, store, new FakeSettlement(), () => true, _ => { });
        var cashOutId = Guid.NewGuid();

        await coordinator.StartBatchAsync(Plan(cashOutId, 1_000_000));
        Assert.Empty(executor.OpenPersisted);

        executor.Emit(PayoutTradeEventType.TradeOpened);
        await WaitAsync(() => executor.OpenPersisted.Count == 1);

        // The leg is durably marked Trading BEFORE the executor is released to confirm the trade.
        Assert.Equal(CashOutLegProgress.Trading, store.Saved[cashOutId].Legs.Single().Progress);
        Assert.Contains(executor.LastLeg!.OperationId, executor.OpenPersisted);
    }

    [Fact]
    public async Task Recovery_quarantines_a_leg_caught_mid_trade_and_never_re_runs_it()
    {
        var executor = new FakeExecutor();
        var store = new FakeStore();
        var settle = new FakeSettlement();
        var cashOutId = Guid.NewGuid();
        // Simulate a crash while leg 1's trade was open (gil may have moved) with leg 2 still pending.
        store.Saved[cashOutId] = new CashOutBatchState
        {
            CashOutId = cashOutId,
            SessionId = Guid.NewGuid(),
            CharacterName = "Exact Player",
            HomeWorld = "Ragnarok",
            Legs =
            [
                new CashOutBatchLegState { Number = 1, Net = 1_000_000, Progress = CashOutLegProgress.Trading },
                new CashOutBatchLegState { Number = 2, Net = 500_000, Progress = CashOutLegProgress.Pending },
            ],
        };
        using var coordinator = new PayoutBatchCoordinator(executor, store, settle, () => true, _ => { });

        await coordinator.ResumeAsync();

        Assert.Equal(0, executor.StartCount); // the opened leg is never re-traded
        var request = Assert.Single(settle.Requests);
        Assert.Equal("ambiguous", request.Legs.Single(x => x.Number == 1).Outcome);
    }

    [Fact]
    public async Task Recovery_resumes_a_pending_leg_after_a_completed_one()
    {
        var executor = new FakeExecutor();
        var store = new FakeStore();
        var cashOutId = Guid.NewGuid();
        store.Saved[cashOutId] = new CashOutBatchState
        {
            CashOutId = cashOutId,
            SessionId = Guid.NewGuid(),
            CharacterName = "Exact Player",
            HomeWorld = "Ragnarok",
            Legs =
            [
                new CashOutBatchLegState { Number = 1, Net = 1_000_000, Progress = CashOutLegProgress.Completed },
                new CashOutBatchLegState { Number = 2, Net = 500_000, Progress = CashOutLegProgress.Pending },
            ],
        };
        using var coordinator = new PayoutBatchCoordinator(executor, store, new FakeSettlement(), () => true, _ => { });

        await coordinator.ResumeAsync();

        Assert.Equal(1, executor.StartCount); // resumes leg 2 only
        Assert.Equal(500_000, executor.LastLeg!.AmountGil);
    }

    [Fact]
    public async Task Settlement_is_retried_when_the_backend_call_fails()
    {
        var executor = new FakeExecutor();
        var store = new FakeStore();
        var settle = new FakeSettlement { Fail = true };
        using var coordinator = new PayoutBatchCoordinator(executor, store, settle, () => true, _ => { });
        var cashOutId = Guid.NewGuid();

        await coordinator.StartBatchAsync(Plan(cashOutId, 1_000_000));
        await RunLegAsync(executor, PayoutTradeEventType.TradeCompleted);
        await WaitAsync(() => store.Saved.ContainsKey(cashOutId) && store.Saved[cashOutId].Legs.Single().Progress == CashOutLegProgress.Completed);

        Assert.Empty(settle.Requests);      // settle failed
        Assert.True(coordinator.HasActiveBatch); // batch retained for retry

        settle.Fail = false;
        await coordinator.ResumeAsync();

        Assert.Single(settle.Requests);
        Assert.False(coordinator.HasActiveBatch);
        Assert.Empty(store.Saved);
    }

    // Drives one leg of the currently active operation to a terminal outcome (opened, then the given event).
    private static async Task RunLegAsync(FakeExecutor executor, PayoutTradeEventType terminal)
    {
        var before = executor.OpenPersisted.Count;
        executor.Emit(PayoutTradeEventType.TradeOpened);
        await WaitAsync(() => executor.OpenPersisted.Count == before + 1);
        executor.Emit(terminal);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition() && DateTimeOffset.UtcNow < timeout)
            await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeExecutor : IPayoutTradeExecutor
    {
        public event Action<PayoutTradeEvent>? TradeEventReceived;

        public int StartCount { get; private set; }
        public PayoutLegDto? LastLeg { get; private set; }
        public List<Guid> OpenPersisted { get; } = [];

        public PayoutExecutorReadiness Probe() => new(true, ExecutorInstanceId, null, "ready");

        public bool StartOperation(PayoutLegDto leg)
        {
            StartCount++;
            LastLeg = leg;
            return true;
        }

        public bool CancelOperation(Guid operationId) => LastLeg?.OperationId == operationId;

        public PayoutTradeOperation? GetOperation(Guid operationId) => null;

        public void MarkOpenEventPersisted(Guid operationId) => OpenPersisted.Add(operationId);

        public void Emit(PayoutTradeEventType type, bool ambiguous = false)
        {
            var leg = LastLeg!;
            TradeEventReceived?.Invoke(new PayoutTradeEvent(
                leg.OperationId, leg.SessionId, leg.CharacterName, leg.HomeWorld, leg.AmountGil, type,
                ExecutorInstanceId, 1, DateTimeOffset.UtcNow, ambiguous ? "reconciliationRequired" : null, null, ambiguous));
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeStore : ICashOutBatchStore
    {
        public Dictionary<Guid, CashOutBatchState> Saved { get; } = new();

        public Task SaveAsync(CashOutBatchState state, CancellationToken cancellationToken = default)
        {
            Saved[state.CashOutId] = Clone(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CashOutBatchState>> LoadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CashOutBatchState>>(Saved.Values.Select(Clone).ToList());

        public Task DeleteAsync(Guid cashOutId, CancellationToken cancellationToken = default)
        {
            Saved.Remove(cashOutId);
            return Task.CompletedTask;
        }

        private static CashOutBatchState Clone(CashOutBatchState state) => new()
        {
            CashOutId = state.CashOutId,
            SessionId = state.SessionId,
            CharacterName = state.CharacterName,
            HomeWorld = state.HomeWorld,
            Legs = state.Legs.Select(x => new CashOutBatchLegState { Number = x.Number, Net = x.Net, Progress = x.Progress, ErrorCode = x.ErrorCode }).ToList(),
        };
    }

    private sealed class FakeSettlement : IPayoutSettlementTransport
    {
        public List<CashOutSettlementRequest> Requests { get; } = [];
        public bool Fail { get; set; }

        public Task SettleAsync(Guid cashOutId, CashOutSettlementRequest request, CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new InvalidOperationException("settle failed");
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
