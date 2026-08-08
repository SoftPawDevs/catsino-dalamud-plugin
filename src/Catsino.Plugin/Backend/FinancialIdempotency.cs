using System.Security.Cryptography;
using System.Text;

namespace Catsino.Plugin.Backend;

public static class FinancialIdempotency
{
    public static Guid ForPayoutEvent(Guid operationId, long sequenceNumber) =>
        CreateDeterministic("payoutEvent", operationId, sequenceNumber);

    public static Guid ForPayoutAcknowledgment(Guid operationId, long sequenceNumber) =>
        CreateDeterministic("payoutAcknowledgment", operationId, sequenceNumber);

    // A single deterministic reconcile key per operation, so repeated recovery attempts for the same
    // stuck operation are idempotent on the backend rather than opening new reconciliations.
    public static Guid ForReconcile(Guid operationId) =>
        CreateDeterministic("payoutReconcile", operationId);

    // A single deterministic settlement key per cash-out, so a resent settlement (retry / restart) is
    // idempotent on the backend rather than double-booking.
    public static Guid ForCashOutSettlement(Guid cashOutId) =>
        CreateDeterministic("cashOutSettlement", cashOutId);

    private static Guid CreateDeterministic(string purpose, Guid operationId, long sequenceNumber)
    {
        if (operationId == Guid.Empty || sequenceNumber <= 0)
        {
            throw new ArgumentException("A deterministic financial key requires an operation ID and positive sequence number.");
        }

        return HashToGuid($"Catsino.Plugin.v1:{purpose}:{operationId:D}:{sequenceNumber}");
    }

    private static Guid CreateDeterministic(string purpose, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A deterministic financial key requires an operation ID.");
        }

        return HashToGuid($"Catsino.Plugin.v1:{purpose}:{operationId:D}");
    }

    private static Guid HashToGuid(string input)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        var uuid = hash[..16];
        uuid[6] = (byte)((uuid[6] & 0x0f) | 0x80);
        uuid[8] = (byte)((uuid[8] & 0x3f) | 0x80);
        return new Guid(uuid, bigEndian: true);
    }
}

public sealed class LogicalIdempotencyKeys
{
    private readonly object gate = new();
    private readonly Dictionary<string, Guid> keys = new(StringComparer.Ordinal);

    public Guid GetOrCreate(string logicalOperation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalOperation);
        lock (gate)
        {
            if (!keys.TryGetValue(logicalOperation, out var key))
            {
                key = Guid.NewGuid();
                keys.Add(logicalOperation, key);
            }

            return key;
        }
    }

    public void Complete(string logicalOperation, Guid key)
    {
        lock (gate)
        {
            if (keys.TryGetValue(logicalOperation, out var current) && current == key)
            {
                keys.Remove(logicalOperation);
            }
        }
    }
}
