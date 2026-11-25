using System.Text.Json;

namespace TaskSandbox.Execution;

public sealed record RuntimeEvent(
    DateTime TimestampUtc,
    string Type,
    string Title,
    string Description,
    string? TaskId,
    string Severity);

/// <summary>Small append-only runtime journal shared by the worker and local dashboard.</summary>
public sealed class RuntimeEventJournal
{
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxDescriptionLength = 1200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RuntimeEventJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "activity.jsonl");
    }

    public async Task WriteAsync(
        string type,
        string title,
        string description = "",
        string? taskId = null,
        string severity = "info",
        CancellationToken cancellationToken = default)
    {
        var entry = new RuntimeEvent(DateTime.UtcNow, type, title,
            description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength],
            taskId, severity);
        string line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(_filePath);
            if (info.Exists && info.Length + System.Text.Encoding.UTF8.GetByteCount(line) > MaxFileBytes)
            {
                string backup = _filePath + ".1";
                File.Move(_filePath, backup, overwrite: true);
            }
            await File.AppendAllTextAsync(_filePath, line, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<RuntimeEvent> ReadRecent(int limit = 100)
    {
        if (!File.Exists(_filePath)) return [];
        limit = Math.Clamp(limit, 1, 500);
        var recent = new Queue<RuntimeEvent>(limit);
        foreach (string line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                RuntimeEvent? item = JsonSerializer.Deserialize<RuntimeEvent>(line, JsonOptions);
                if (item is null) continue;
                if (recent.Count == limit) recent.Dequeue();
                recent.Enqueue(item);
            }
            catch (JsonException) { }
        }
        return recent.Reverse().ToArray();
    }

    public (int Events, int Errors, int Fixes) GetTodaySummary()
    {
        if (!File.Exists(_filePath)) return (0, 0, 0);
        DateTime today = DateTime.UtcNow.Date;
        int events = 0, errors = 0, fixes = 0;
        foreach (string line in File.ReadLines(_filePath))
        {
            try
            {
                RuntimeEvent? item = JsonSerializer.Deserialize<RuntimeEvent>(line, JsonOptions);
                if (item is null || item.TimestampUtc.Date != today) continue;
                events++;
                if (item.Severity == "error") errors++;
                if (item.Type == "correction_succeeded") fixes++;
            }
            catch (JsonException) { }
        }
        return (events, errors, fixes);
    }
}
