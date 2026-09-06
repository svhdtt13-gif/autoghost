using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGhost.TaskEngine;

public sealed record TransportItemRequirement
{
    public TransportItemRequirement(string name, int requiredQuantity, int observedQuantity = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Transport item name is required.", nameof(name));
        }

        if (requiredQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredQuantity), "Required quantity cannot be negative.");
        }

        if (observedQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedQuantity), "Observed quantity cannot be negative.");
        }

        Name = name.Trim();
        RequiredQuantity = requiredQuantity;
        ObservedQuantity = observedQuantity;
    }

    public string Name { get; }

    public int RequiredQuantity { get; }

    public int ObservedQuantity { get; }
}

public enum SpecialItemComparisonStatus
{
    PendingUserList,
    Match,
    NoMatch
}

public sealed record SpecialItemComparisonResult(
    SpecialItemComparisonStatus Status,
    IReadOnlyList<string> ConfiguredItems,
    IReadOnlyList<string> MatchedItems)
{
    public bool AlertRequired => Status == SpecialItemComparisonStatus.Match;
}

/// <summary>
/// Compares captured item names with the operator-provided list. The matcher
/// intentionally has no built-in item names or game-specific allowlist.
/// </summary>
public static class SpecialItemMatcher
{
    public static SpecialItemComparisonResult Compare(
        IEnumerable<TransportItemRequirement> items,
        IEnumerable<string>? configuredSpecialItems)
    {
        ArgumentNullException.ThrowIfNull(items);

        var capturedItems = items.ToArray();
        var configuredItems = NormalizeConfiguredItems(configuredSpecialItems);
        if (configuredItems.Count == 0)
        {
            return new SpecialItemComparisonResult(
                SpecialItemComparisonStatus.PendingUserList,
                configuredItems,
                Array.Empty<string>());
        }

        var configuredNames = configuredItems
            .Select(NormalizeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedItems = capturedItems
            .Where(item => configuredNames.Contains(NormalizeName(item.Name)))
            .Select(item => item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SpecialItemComparisonResult(
            matchedItems.Length > 0
                ? SpecialItemComparisonStatus.Match
                : SpecialItemComparisonStatus.NoMatch,
            configuredItems,
            matchedItems);
    }

    public static string NormalizeName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length == 0)
        {
            return normalized;
        }

        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static IReadOnlyList<string> NormalizeConfiguredItems(IEnumerable<string>? configuredSpecialItems)
    {
        if (configuredSpecialItems is null)
        {
            return Array.Empty<string>();
        }

        return configuredSpecialItems
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record TransportNote
{
    public TransportNote(
        string roleId,
        DateOnly localDate,
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<TransportItemRequirement> items,
        SpecialItemComparisonStatus specialItemStatus,
        IReadOnlyList<string> configuredSpecialItems,
        IReadOnlyList<string> matchedSpecialItems)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            throw new ArgumentException("Role ID is required.", nameof(roleId));
        }

        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(configuredSpecialItems);
        ArgumentNullException.ThrowIfNull(matchedSpecialItems);

        RoleId = roleId.Trim();
        LocalDate = localDate;
        CapturedAtUtc = capturedAtUtc;
        Items = items.ToArray();
        SpecialItemStatus = specialItemStatus;
        ConfiguredSpecialItems = configuredSpecialItems.ToArray();
        MatchedSpecialItems = matchedSpecialItems.ToArray();
    }

    public string RoleId { get; }

    public DateOnly LocalDate { get; }

    public DateTimeOffset CapturedAtUtc { get; }

    public IReadOnlyList<TransportItemRequirement> Items { get; }

    public SpecialItemComparisonStatus SpecialItemStatus { get; }

    public IReadOnlyList<string> ConfiguredSpecialItems { get; }

    public IReadOnlyList<string> MatchedSpecialItems { get; }

    public string StorageKey => $"{RoleId}:{LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

public interface ITransportNoteHistoryStore
{
    IReadOnlyList<TransportNote> LoadAll();

    TransportNote? Load(string roleId, DateOnly localDate);

    void Save(TransportNote note);
}

/// <summary>
/// Stores one canonical transport note per Role ID and local calendar date.
/// Saves replace the exact composite key and use an atomic file replacement.
/// </summary>
public sealed class JsonTransportNoteHistoryStore : ITransportNoteHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _sync = new();

    public JsonTransportNoteHistoryStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A note history file path is required.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public IReadOnlyList<TransportNote> LoadAll()
    {
        lock (_sync)
        {
            if (!File.Exists(FilePath))
            {
                return Array.Empty<TransportNote>();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<TransportNote>>(json, JsonOptions)
                   ?? new List<TransportNote>();
        }
    }

    public TransportNote? Load(string roleId, DateOnly localDate)
    {
        if (string.IsNullOrWhiteSpace(roleId))
        {
            return null;
        }

        var normalizedRoleId = roleId.Trim();
        return LoadAll().FirstOrDefault(note =>
            string.Equals(note.RoleId, normalizedRoleId, StringComparison.Ordinal) &&
            note.LocalDate == localDate);
    }

    public void Save(TransportNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        TransportNoteValidator.ValidateOrThrow(note);

        lock (_sync)
        {
            var notes = LoadAll()
                .Where(existing => !HasSameKey(existing, note))
                .Append(note)
                .OrderBy(existing => existing.LocalDate)
                .ThenBy(existing => existing.RoleId, StringComparer.Ordinal)
                .ToList();

            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var json = JsonSerializer.Serialize(notes, JsonOptions);
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static bool HasSameKey(TransportNote left, TransportNote right) =>
        string.Equals(left.RoleId, right.RoleId, StringComparison.Ordinal) &&
        left.LocalDate == right.LocalDate;
}

public static class TransportNoteValidator
{
    public static void ValidateOrThrow(TransportNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        if (note.Items.Count != 8)
        {
            throw new InvalidDataException($"Canonical transport note must contain exactly 8 items; got {note.Items.Count}.");
        }

        if (note.Items.Any(item => string.IsNullOrWhiteSpace(item.Name)))
        {
            throw new InvalidDataException("Canonical transport note contains an item without a name.");
        }

        if (note.SpecialItemStatus == SpecialItemComparisonStatus.Match && note.MatchedSpecialItems.Count == 0)
        {
            throw new InvalidDataException("A special-item match must contain at least one matched item.");
        }

        if (note.SpecialItemStatus != SpecialItemComparisonStatus.Match && note.MatchedSpecialItems.Count > 0)
        {
            throw new InvalidDataException("Only a Match note may contain matched special items.");
        }
    }
}

public sealed class TransportNoteService
{
    private readonly ITransportNoteHistoryStore _historyStore;

    public TransportNoteService(ITransportNoteHistoryStore historyStore)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
    }

    public TransportNote CaptureAndPersist(
        string roleId,
        DateOnly localDate,
        DateTimeOffset capturedAtUtc,
        IEnumerable<TransportItemRequirement> items,
        IEnumerable<string>? configuredSpecialItems)
    {
        ArgumentNullException.ThrowIfNull(items);

        var capturedItems = items.ToArray();
        var comparison = SpecialItemMatcher.Compare(capturedItems, configuredSpecialItems);
        var note = new TransportNote(
            roleId,
            localDate,
            capturedAtUtc,
            capturedItems,
            comparison.Status,
            comparison.ConfiguredItems,
            comparison.MatchedItems);

        TransportNoteValidator.ValidateOrThrow(note);
        _historyStore.Save(note);
        return note;
    }
}
