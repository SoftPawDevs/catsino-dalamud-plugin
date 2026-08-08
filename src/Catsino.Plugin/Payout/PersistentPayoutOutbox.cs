using System.Text.Json;
using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Payout;

public interface IPayoutOutbox
{
    Task EnqueueAsync(PayoutEventDto payoutEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PayoutEventDto>> ReadPendingAsync(CancellationToken cancellationToken = default);

    Task<bool> AcknowledgeAsync(Guid operationId, long sequenceNumber, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    Task<bool> HasPendingForOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
}

public sealed class PersistentPayoutOutbox(string directoryPath) : IPayoutOutbox
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task EnqueueAsync(PayoutEventDto payoutEvent, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(payoutEvent.OperationId, payoutEvent.SequenceNumber);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directoryPath);
            var path = EventPath(payoutEvent.OperationId, payoutEvent.SequenceNumber);
            if (File.Exists(path))
            {
                var existing = await ReadEventAsync(path, cancellationToken).ConfigureAwait(false);
                if (existing != payoutEvent)
                {
                    throw new InvalidDataException("An outbox identity is already used by a different event.");
                }

                return;
            }

            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, payoutEvent, ContractJson.Options, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, path, false);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PayoutEventDto>> ReadPendingAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(directoryPath))
            {
                return [];
            }

            var events = new List<PayoutEventDto>();
            foreach (var path in Directory.EnumerateFiles(directoryPath, "*.json").Order(StringComparer.Ordinal))
            {
                events.Add(await ReadEventAsync(path, cancellationToken).ConfigureAwait(false));
            }

            return events.OrderBy(item => item.OccurredAt).ThenBy(item => item.SequenceNumber).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> AcknowledgeAsync(Guid operationId, long sequenceNumber, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(operationId, sequenceNumber);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = EventPath(operationId, sequenceNumber);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Directory.Exists(directoryPath)
                ? Directory.EnumerateFiles(directoryPath, "*.json").Count()
                : 0;
        }
        finally
        {
            gate.Release();
        }
    }

    // Durable, restart-surviving check for any not-yet-acknowledged event belonging to an operation.
    // The recovery/start path consults this so a leg whose progress (e.g. a completed trade) is still
    // sitting in the durable outbox is never physically re-traded before that progress is delivered.
    public async Task<bool> HasPendingForOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Directory.Exists(directoryPath) &&
                   Directory.EnumerateFiles(directoryPath, $"*-{operationId:N}.json").Any();
        }
        finally
        {
            gate.Release();
        }
    }

    private string EventPath(Guid operationId, long sequenceNumber) =>
        Path.Combine(directoryPath, $"{sequenceNumber:D20}-{operationId:N}.json");

    private static async Task<PayoutEventDto> ReadEventAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PayoutEventDto>(stream, ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Outbox event is empty.");
    }

    private static void ValidateIdentity(Guid operationId, long sequenceNumber)
    {
        if (operationId == Guid.Empty || sequenceNumber <= 0)
        {
            throw new ArgumentException("Outbox events require an operation ID and positive sequence number.");
        }
    }
}
