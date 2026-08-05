using System.Security.Cryptography;
using System.Text;

namespace Catsino.Plugin.Backend;

public static class FinancialIdempotency
{
    public static Guid ForPayoutEvent(Guid operationId, long sequenceNumber) =>
        CreateDeterministic("payoutEvent", operationId, sequenceNumber);

    public static Guid ForPayoutAcknowledgment(Guid operationId, long sequenceNumber) =>
        CreateDeterministic("payoutAcknowledgment", operationId, sequenceNumber);

    private static Guid CreateDeterministic(string purpose, Guid operationId, long sequenceNumber)
    {
        if (operationId == Guid.Empty || sequenceNumber <= 0)
        {
            throw new ArgumentException("A deterministic financial key requires an operation ID and positive sequence number.");
        }

        var input = Encoding.UTF8.GetBytes($"Catsino.Plugin.v1:{purpose}:{operationId:D}:{sequenceNumber}");
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
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
