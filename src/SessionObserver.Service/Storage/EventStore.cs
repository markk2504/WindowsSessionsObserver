using System.Globalization;
using System.Text.Json;
using log4net;
using Microsoft.Extensions.Options;
using SessionObserver.Protocol;
using SessionObserver.Protocol.Models;

namespace SessionObserver.Service.Storage;

/// <summary>
/// Assigns sequence numbers to events, keeps the most recent ones in memory, appends every event to a
/// daily JSON-lines file (events-yyyyMMdd.jsonl, UTC date) and notifies subscribers.
/// History is reloaded from the files on startup, so sequences keep increasing across restarts.
/// </summary>
public sealed class EventStore
{
    private const string FilePrefix = "events-";
    private const string FileExtension = ".jsonl";
    private const string DateFormat = "yyyyMMdd";

    private static readonly ILog Log = LogManager.GetLogger(typeof(EventStore));

    private readonly object _sync = new();
    private readonly List<SessionEvent> _events = [];
    private readonly string _directory;
    private readonly int _ringSize;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private Action<SessionEvent>[] _subscribers = [];
    private long _latestSequence;
    private DateOnly _lastCleanup;

    public EventStore(IOptions<EventStoreOptions> options)
        : this(options.Value, TimeProvider.System)
    {
    }

    internal EventStore(EventStoreOptions options, TimeProvider time)
    {
        _directory = options.ResolveDirectory();
        _ringSize = Math.Max(1, options.RingSize);
        _retentionDays = Math.Max(1, options.RetentionDays);
        _time = time;

        Directory.CreateDirectory(_directory);
        CleanupOldFiles();
        Load();
    }

    public string DirectoryPath => _directory;

    public long LatestSequence
    {
        get { lock (_sync) { return _latestSequence; } }
    }

    public int Count
    {
        get { lock (_sync) { return _events.Count; } }
    }

    /// <summary>Stores the event under the next sequence number and notifies subscribers.</summary>
    public SessionEvent Append(SessionEvent sessionEvent)
    {
        SessionEvent stored;
        Action<SessionEvent>[] subscribers;
        lock (_sync)
        {
            stored = sessionEvent with { Sequence = ++_latestSequence };
            _events.Add(stored);
            if (_events.Count > _ringSize)
            {
                _events.RemoveRange(0, _events.Count - _ringSize);
            }
            Persist(stored);
            subscribers = _subscribers;
        }

        foreach (Action<SessionEvent> subscriber in subscribers)
        {
            try
            {
                subscriber(stored);
            }
            catch (Exception ex)
            {
                Log.Error("An event subscriber threw.", ex);
            }
        }
        return stored;
    }

    /// <summary>
    /// Events in ascending order: with <paramref name="sinceSequence"/>, the oldest <paramref name="max"/> after it;
    /// otherwise the newest <paramref name="max"/>.
    /// </summary>
    public (IReadOnlyList<SessionEvent> Events, long LatestSequence) GetEvents(long? sinceSequence, int max)
    {
        lock (_sync)
        {
            if (max <= 0 || _events.Count == 0)
            {
                return ([], _latestSequence);
            }

            int start;
            if (sinceSequence is null)
            {
                start = Math.Max(0, _events.Count - max);
            }
            else
            {
                start = FirstIndexAfter(sinceSequence.Value);
            }

            int count = Math.Min(max, _events.Count - start);
            return (_events.GetRange(start, count), _latestSequence);
        }
    }

    /// <summary>Registers a callback invoked synchronously for every appended event. Must not block.</summary>
    public IDisposable Subscribe(Action<SessionEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_sync)
        {
            _subscribers = [.. _subscribers, handler];
        }
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<SessionEvent> handler)
    {
        lock (_sync)
        {
            _subscribers = _subscribers.Where(s => s != handler).ToArray();
        }
    }

    private int FirstIndexAfter(long sequence)
    {
        int low = 0;
        int high = _events.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (_events[mid].Sequence <= sequence)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    private void Persist(SessionEvent stored)
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (today != _lastCleanup)
        {
            CleanupOldFiles();
        }

        string path = GetFilePath(DateOnly.FromDateTime(stored.Timestamp.UtcDateTime));
        try
        {
            string line = JsonSerializer.Serialize(stored, ProtocolJsonContext.Default.SessionEvent);
            File.AppendAllText(path, line + "\n");
        }
        catch (Exception ex)
        {
            // The event stays available in memory; losing the file copy must not stop monitoring.
            Log.Error($"Cannot append event {stored.Sequence} to '{path}'.", ex);
        }
    }

    private void Load()
    {
        int loaded = 0;
        foreach (string path in GetEventFiles().OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            int lineNumber = 0;
            int corrupt = 0;
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    SessionEvent? e;
                    try
                    {
                        e = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.SessionEvent);
                    }
                    catch (JsonException)
                    {
                        e = null;
                    }
                    if (e is null)
                    {
                        corrupt++;
                        continue;
                    }

                    _latestSequence = Math.Max(_latestSequence, e.Sequence);
                    _events.Add(e);
                    loaded++;
                    if (_events.Count > _ringSize * 2)
                    {
                        _events.RemoveRange(0, _events.Count - _ringSize);
                    }
                }
            }
            catch (IOException ex)
            {
                Log.Warn($"Cannot read event file '{path}'.", ex);
            }

            if (corrupt > 0)
            {
                Log.Warn($"Skipped {corrupt} unreadable line(s) of {lineNumber} in '{path}'.");
            }
        }

        // Files are in date order, but sort defensively so binary search by sequence stays valid.
        _events.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        if (_events.Count > _ringSize)
        {
            _events.RemoveRange(0, _events.Count - _ringSize);
        }

        Log.Info($"Event store '{_directory}': loaded {loaded} event(s), keeping {_events.Count} in memory; " +
                 $"latest sequence {_latestSequence}.");
    }

    private void CleanupOldFiles()
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        _lastCleanup = today;
        DateOnly oldestKept = today.AddDays(-(_retentionDays - 1));

        foreach (string path in GetEventFiles())
        {
            if (TryGetFileDate(path, out DateOnly date) && date < oldestKept)
            {
                try
                {
                    File.Delete(path);
                    Log.Info($"Deleted expired event file '{path}'.");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Cannot delete expired event file '{path}'.", ex);
                }
            }
        }
    }

    private IEnumerable<string> GetEventFiles() =>
        Directory.EnumerateFiles(_directory, FilePrefix + "*" + FileExtension)
            .Where(path => TryGetFileDate(path, out _));

    private string GetFilePath(DateOnly date) =>
        Path.Combine(_directory, FilePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);

    private static bool TryGetFileDate(string path, out DateOnly date)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        date = default;
        return name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) &&
               DateOnly.TryParseExact(name[FilePrefix.Length..], DateFormat, CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out date);
    }

    private sealed class Subscription(EventStore store, Action<SessionEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                store.Unsubscribe(handler);
            }
        }
    }
}
