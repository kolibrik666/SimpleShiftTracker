using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Tracker.App;

internal sealed class SingleInstanceActivation : IDisposable
{
    private const string EventPrefix = @"Local\SimpleShiftTracker-Restore-";
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle registration;

    internal SingleInstanceActivation(string identity, Action restore)
    {
        signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventPrefix + identity);
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, timedOut) =>
        {
            if (!timedOut) restore();
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    internal static string IdentityFor(string dataDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }

    internal static bool TryRequestRestore(string identity)
    {
        if (!EventWaitHandle.TryOpenExisting(EventPrefix + identity, out var existing)) return false;
        using (existing) return existing.Set();
    }

    public void Dispose()
    {
        registration.Unregister(null);
        signal.Dispose();
    }
}
