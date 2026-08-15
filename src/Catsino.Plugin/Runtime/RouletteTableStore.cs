using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Runtime;

// Latest-known roulette table per session. Hub pushes and polls can race, so a snapshot is only replaced
// by a strictly newer one (by ObservedAt) — an out-of-order poll can never clobber a fresher push.
// Mirrors BlackjackTableStore and HoldemTableStore.
public sealed class RouletteTableStore
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, RouletteTableDto> tables = [];

    public RouletteTableDto? Get(Guid sessionId)
    {
        lock (sync)
        {
            return tables.TryGetValue(sessionId, out var table) ? table : null;
        }
    }

    public void Set(RouletteTableDto table)
    {
        lock (sync)
        {
            if (tables.TryGetValue(table.SessionId, out var existing) && existing.ObservedAt > table.ObservedAt)
            {
                return;
            }

            tables[table.SessionId] = table;
        }
    }

    public void Remove(Guid sessionId)
    {
        lock (sync)
        {
            tables.Remove(sessionId);
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            tables.Clear();
        }
    }
}
