using System.Collections.Concurrent;

namespace SqlOS.AuthServer.Services;

public sealed class SqlOSDynamicClientRegistrationRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _registrations = new(StringComparer.Ordinal);

    public bool TryConsume(string key, TimeSpan window, int maxRegistrations)
    {
        var now = DateTime.UtcNow;
        var queue = _registrations.GetOrAdd(key, static _ => new Queue<DateTime>());

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= maxRegistrations)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
