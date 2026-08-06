using Catsino.Plugin.Contracts;

namespace Catsino.Plugin.Runtime;

public sealed class SessionRosterStore
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private long nextRevision;

    public SessionRosterDto? Get(Guid sessionId)
    {
        lock (sync)
        {
            return entries.TryGetValue(sessionId, out var entry) ? entry.Snapshot : null;
        }
    }

    public IReadOnlyList<Guid> SessionIds
    {
        get
        {
            lock (sync)
            {
                return entries.Keys.ToArray();
            }
        }
    }

    public Task RefreshAsync(
        Guid sessionId,
        Func<Guid, CancellationToken, Task<SessionRosterDto>> loader,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? completion;
        lock (sync)
        {
            var entry = GetOrCreate(sessionId);
            if (entry.RefreshTask is not null)
            {
                return entry.RefreshTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.RefreshTask = completion.Task;
        }

        _ = RefreshCoreAsync(sessionId, loader, completion, cancellationToken);
        return completion.Task;
    }

    public void Invalidate(Guid sessionId)
    {
        lock (sync)
        {
            GetOrCreate(sessionId).Revision = NextRevision();
        }
    }

    public long BeginRefresh(Guid sessionId)
    {
        lock (sync)
        {
            var entry = GetOrCreate(sessionId);
            entry.Revision = NextRevision();
            return entry.Revision;
        }
    }

    public bool TryApply(Guid sessionId, long revision, SessionRosterDto roster)
    {
        if (!IsForSession(roster, sessionId))
        {
            return false;
        }

        lock (sync)
        {
            if (!entries.TryGetValue(sessionId, out var entry))
            {
                return false;
            }

            if (revision != entry.Revision)
            {
                return false;
            }

            entry.Snapshot = roster;
            return true;
        }
    }

    public void UpsertPendingInvite(PendingInviteDto invite)
    {
        lock (sync)
        {
            var entry = GetOrCreate(invite.SessionId);
            entry.Revision = NextRevision();
            var current = entry.Snapshot;
            var pending = current?.PendingInvites
                .Where(item => item.InviteId != invite.InviteId)
                .Append(invite)
                .ToArray() ?? [invite];
            entry.Snapshot = new SessionRosterDto(
                invite.SessionId,
                current?.Players ?? [],
                pending,
                DateTimeOffset.UtcNow);
        }
    }

    public void RemovePendingInvite(Guid sessionId, Guid inviteId)
    {
        lock (sync)
        {
            var entry = GetOrCreate(sessionId);
            entry.Revision = NextRevision();
            if (entry.Snapshot is { } current)
            {
                entry.Snapshot = current with
                {
                    PendingInvites = current.PendingInvites.Where(item => item.InviteId != inviteId).ToArray(),
                    ObservedAt = DateTimeOffset.UtcNow,
                };
            }
        }
    }

    public bool HasUnexpiredPendingInvites(DateTimeOffset now)
    {
        lock (sync)
        {
            return entries.Values.Any(entry =>
                entry.Snapshot?.PendingInvites.Any(invite => InviteCountdown.IsVisible(invite, now)) == true);
        }
    }

    public void Remove(Guid sessionId)
    {
        lock (sync)
        {
            entries.Remove(sessionId);
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
        }
    }

    private async Task RefreshCoreAsync(
        Guid sessionId,
        Func<Guid, CancellationToken, Task<SessionRosterDto>> loader,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                long revision;
                lock (sync)
                {
                    if (!entries.TryGetValue(sessionId, out var entry) || !ReferenceEquals(entry.RefreshTask, completion.Task))
                    {
                        completion.TrySetResult();
                        return;
                    }

                    revision = entry.Revision;
                }

                var roster = await loader(sessionId, cancellationToken).ConfigureAwait(false);
                if (!IsForSession(roster, sessionId))
                {
                    throw new InvalidDataException("The backend returned roster data for a different session.");
                }

                lock (sync)
                {
                    if (!entries.TryGetValue(sessionId, out var entry) || !ReferenceEquals(entry.RefreshTask, completion.Task))
                    {
                        completion.TrySetResult();
                        return;
                    }

                    if (revision != entry.Revision)
                    {
                        continue;
                    }

                    entry.Snapshot = roster;
                    entry.RefreshTask = null;
                    completion.TrySetResult();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            lock (sync)
            {
                if (entries.TryGetValue(sessionId, out var entry) && ReferenceEquals(entry.RefreshTask, completion.Task))
                {
                    entry.RefreshTask = null;
                }
            }

            completion.TrySetException(exception);
        }
    }

    private Entry GetOrCreate(Guid sessionId)
    {
        if (!entries.TryGetValue(sessionId, out var entry))
        {
            entry = new Entry { Revision = NextRevision() };
            entries.Add(sessionId, entry);
        }

        return entry;
    }

    private long NextRevision() => ++nextRevision;

    private static bool IsForSession(SessionRosterDto roster, Guid sessionId) =>
        roster.SessionId == sessionId &&
        roster.Players.All(player => player.SessionId == sessionId) &&
        roster.PendingInvites.All(invite => invite.SessionId == sessionId);

    private sealed class Entry
    {
        internal long Revision { get; set; }

        internal SessionRosterDto? Snapshot { get; set; }

        internal Task? RefreshTask { get; set; }
    }
}

public static class InviteCountdown
{
    public static bool IsVisible(PendingInviteDto invite, DateTimeOffset now) => invite.ExpiresAt > now;

    public static string Format(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var remainingSeconds = Math.Max(0, (int)Math.Ceiling((expiresAt - now).TotalSeconds));
        return remainingSeconds == 0
            ? "Expired"
            : $"{remainingSeconds / 60}:{remainingSeconds % 60:D2}";
    }
}
