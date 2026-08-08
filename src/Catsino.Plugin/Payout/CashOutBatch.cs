using System.Text.Json;
using Catsino.Plugin.Backend;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

// Per-leg progress in a client-driven cash-out batch. This is the durable, restart-surviving truth about
// which legs have already physically traded, so a completed leg is never re-run and a leg that opened a
// trade before a crash is treated as ambiguous rather than re-traded.
public enum CashOutLegProgress
{
    Pending,   // not started
    Trading,   // a trade was opened for this leg (gil may have moved) — never auto-restart from here
    Completed, // the exact gil transfer was proven
    Failed,    // clean failure, no gil moved — safe to release
    Ambiguous, // outcome unknown — quarantine on the backend, do not refund
}

// The plan the plugin receives from the cash-out response: the backend-authored net per leg.
public sealed record CashOutBatchPlan(
    Guid CashOutId,
    Guid SessionId,
    string CharacterName,
    string HomeWorld,
    IReadOnlyList<CashOutBatchLegPlan> Legs);

public sealed record CashOutBatchLegPlan(int Number, long Net);

// Durable, mutable batch state persisted to disk (one file per cash-out).
public sealed class CashOutBatchState
{
    public Guid CashOutId { get; set; }
    public Guid SessionId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public string HomeWorld { get; set; } = string.Empty;
    public List<CashOutBatchLegState> Legs { get; set; } = [];

    public static CashOutBatchState FromPlan(CashOutBatchPlan plan) => new()
    {
        CashOutId = plan.CashOutId,
        SessionId = plan.SessionId,
        CharacterName = plan.CharacterName,
        HomeWorld = plan.HomeWorld,
        Legs = plan.Legs.OrderBy(x => x.Number)
            .Select(x => new CashOutBatchLegState { Number = x.Number, Net = x.Net, Progress = CashOutLegProgress.Pending })
            .ToList(),
    };
}

public sealed class CashOutBatchLegState
{
    public int Number { get; set; }
    public long Net { get; set; }
    public CashOutLegProgress Progress { get; set; }
    public string? ErrorCode { get; set; }
}

public interface ICashOutBatchStore
{
    Task SaveAsync(CashOutBatchState state, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CashOutBatchState>> LoadAllAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid cashOutId, CancellationToken cancellationToken = default);
}

// Durable, atomic per-batch persistence, mirroring PersistentPayoutOutbox's crash-safe write pattern
// (temp file + WriteThrough + Flush(true) + atomic File.Move). Survives a plugin restart.
public sealed class PersistentCashOutBatchStore(string directoryPath) : ICashOutBatchStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task SaveAsync(CashOutBatchState state, CancellationToken cancellationToken = default)
    {
        if (state.CashOutId == Guid.Empty)
            throw new ArgumentException("A batch requires a cash-out id.", nameof(state));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directoryPath);
            var path = BatchPath(state.CashOutId);
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, state, ContractJson.Options, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CashOutBatchState>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directoryPath))
                return [];
            var result = new List<CashOutBatchState>();
            foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json").Order(StringComparer.Ordinal))
            {
                await using var stream = File.OpenRead(path);
                var state = await JsonSerializer.DeserializeAsync<CashOutBatchState>(stream, ContractJson.Options, cancellationToken).ConfigureAwait(false);
                if (state is not null && state.CashOutId != Guid.Empty)
                    result.Add(state);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(Guid cashOutId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = BatchPath(cashOutId);
            if (File.Exists(path))
                File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    private string BatchPath(Guid cashOutId) => Path.Combine(directoryPath, $"{cashOutId:N}.json");
}

public interface IPayoutSettlementTransport
{
    Task SettleAsync(Guid cashOutId, CashOutSettlementRequest request, CancellationToken cancellationToken = default);
}

public sealed class BackendPayoutSettlementTransport(CatsinoApiClient api) : IPayoutSettlementTransport
{
    public Task SettleAsync(Guid cashOutId, CashOutSettlementRequest request, CancellationToken cancellationToken = default) =>
        api.SettleCashOutAsync(cashOutId, request, FinancialIdempotency.ForCashOutSettlement(cashOutId), cancellationToken);
}
