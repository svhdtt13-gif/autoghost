using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGhost.TaskEngine;

public enum QuanNinhEligibilityStatus
{
    Eligible,
    ClientIdRequired,
    NotAllowedDay,
    OutsideRegistrationWindow,
    DailySuccessLimitReached
}

public sealed record QuanNinhRegistrationWindow
{
    public QuanNinhRegistrationWindow(TimeOnly start, TimeOnly end)
    {
        if (start >= end)
        {
            throw new ArgumentException("Registration window end must be after its start.");
        }

        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public bool Contains(TimeOnly localTime) => localTime >= Start && localTime < End;

    public string Label => $"{Start:HH\\:mm}-{End:HH\\:mm}";
}

public sealed record QuanNinhSchedulePolicy(
    string TimeZoneId,
    IReadOnlySet<DayOfWeek> AllowedDays,
    IReadOnlyList<QuanNinhRegistrationWindow> RegistrationWindows,
    int MaxSuccessfulRegistrationsPerClientPerDay)
{
    public static QuanNinhSchedulePolicy Default { get; } = new(
        TimeZoneId: "Asia/Ho_Chi_Minh",
        AllowedDays: new HashSet<DayOfWeek>
        {
            DayOfWeek.Monday,
            DayOfWeek.Wednesday,
            DayOfWeek.Friday,
            DayOfWeek.Sunday
        },
        RegistrationWindows: new[]
        {
            new QuanNinhRegistrationWindow(new TimeOnly(12, 0), new TimeOnly(13, 0)),
            new QuanNinhRegistrationWindow(new TimeOnly(20, 0), new TimeOnly(21, 0))
        },
        MaxSuccessfulRegistrationsPerClientPerDay: 3);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TimeZoneId))
        {
            throw new ArgumentException("A time zone ID is required.", nameof(TimeZoneId));
        }

        if (AllowedDays is null || AllowedDays.Count == 0)
        {
            throw new ArgumentException("At least one allowed day is required.", nameof(AllowedDays));
        }

        if (RegistrationWindows is null || RegistrationWindows.Count == 0)
        {
            throw new ArgumentException("At least one registration window is required.", nameof(RegistrationWindows));
        }

        if (RegistrationWindows.Any(window => window.Start >= window.End))
        {
            throw new ArgumentException("Registration windows must have a positive duration.", nameof(RegistrationWindows));
        }

        if (MaxSuccessfulRegistrationsPerClientPerDay <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxSuccessfulRegistrationsPerClientPerDay),
                "The daily successful-registration limit must be positive.");
        }
    }

    public TimeZoneInfo ResolveTimeZone()
    {
        Validate();

        IEnumerable<string?> candidateIds = new[]
        {
            TimeZoneId,
            TimeZoneId.Equals("Asia/Ho_Chi_Minh", StringComparison.OrdinalIgnoreCase)
                ? "SE Asia Standard Time"
                : null,
            TimeZoneId.Equals("SE Asia Standard Time", StringComparison.OrdinalIgnoreCase)
                ? "Asia/Ho_Chi_Minh"
                : null
        };

        foreach (var candidateId in candidateIds.Where(static id => !string.IsNullOrWhiteSpace(id)))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(candidateId!);
            }
            catch (TimeZoneNotFoundException)
            {
                // Try the platform-specific alias before failing closed.
            }
            catch (InvalidTimeZoneException)
            {
                // Try the platform-specific alias before failing closed.
            }
        }

        throw new InvalidOperationException($"Time zone '{TimeZoneId}' is not available on this host.");
    }
}

public sealed record QuanNinhRegistrationRecord
{
    public QuanNinhRegistrationRecord(
        string clientId,
        DateOnly localDate,
        int successfulRegistrations)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID is required.", nameof(clientId));
        }

        if (successfulRegistrations < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(successfulRegistrations),
                "Successful registration count cannot be negative.");
        }

        ClientId = clientId.Trim();
        LocalDate = localDate;
        SuccessfulRegistrations = successfulRegistrations;
    }

    public string ClientId { get; }

    public DateOnly LocalDate { get; }

    public int SuccessfulRegistrations { get; }

    public string StorageKey => $"{ClientId}:{LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

public interface IQuanNinhRegistrationHistoryStore
{
    IReadOnlyList<QuanNinhRegistrationRecord> LoadAll();

    QuanNinhRegistrationRecord? Load(string clientId, DateOnly localDate);

    void Save(QuanNinhRegistrationRecord record);

    bool TryRecordVerifiedSuccess(
        string clientId,
        DateOnly localDate,
        int maxSuccessfulRegistrationsPerClientPerDay,
        out QuanNinhRegistrationRecord? updatedRecord);
}

/// <summary>
/// Stores verified successful registrations by exact Client ID and local date.
/// Attempts, failures, and unknown UI states must never be recorded here.
/// </summary>
public sealed class JsonQuanNinhRegistrationHistoryStore : IQuanNinhRegistrationHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonQuanNinhRegistrationHistoryStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A registration history file path is required.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
        _sync = FileLocks.GetOrAdd(FilePath, static _ => new object());
    }

    public string FilePath { get; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly object _sync;

    public IReadOnlyList<QuanNinhRegistrationRecord> LoadAll()
    {
        lock (_sync)
        {
            return LoadAllUnsafe();
        }
    }

    public QuanNinhRegistrationRecord? Load(string clientId, DateOnly localDate)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var normalizedClientId = clientId.Trim();
        return LoadAll().FirstOrDefault(record =>
            string.Equals(record.ClientId, normalizedClientId, StringComparison.Ordinal) &&
            record.LocalDate == localDate);
    }

    public void Save(QuanNinhRegistrationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync)
        {
            var records = LoadAllUnsafe()
                .Where(existing => !HasSameKey(existing, record))
                .Append(record)
                .OrderBy(existing => existing.LocalDate)
                .ThenBy(existing => existing.ClientId, StringComparer.Ordinal)
                .ToList();

            WriteAllUnsafe(records);
        }
    }

    /// <summary>
    /// Atomically performs the read-check-increment-write sequence under the
    /// file lock shared by all store instances for the same path.
    /// </summary>
    public bool TryRecordVerifiedSuccess(
        string clientId,
        DateOnly localDate,
        int maxSuccessfulRegistrationsPerClientPerDay,
        out QuanNinhRegistrationRecord? updatedRecord)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Client ID is required.", nameof(clientId));
        }

        if (maxSuccessfulRegistrationsPerClientPerDay <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSuccessfulRegistrationsPerClientPerDay),
                "The daily successful-registration limit must be positive.");
        }

        var normalizedClientId = clientId.Trim();
        lock (_sync)
        {
            var records = LoadAllUnsafe();
            var existing = records.FirstOrDefault(record =>
                string.Equals(record.ClientId, normalizedClientId, StringComparison.Ordinal) &&
                record.LocalDate == localDate);
            var currentCount = existing?.SuccessfulRegistrations ?? 0;
            if (currentCount >= maxSuccessfulRegistrationsPerClientPerDay)
            {
                updatedRecord = null;
                return false;
            }

            var newRecord = new QuanNinhRegistrationRecord(
                normalizedClientId,
                localDate,
                currentCount + 1);
            updatedRecord = newRecord;
            var updatedRecords = records
                .Where(record => !HasSameKey(record, newRecord))
                .Append(newRecord)
                .OrderBy(record => record.LocalDate)
                .ThenBy(record => record.ClientId, StringComparer.Ordinal)
                .ToList();
            WriteAllUnsafe(updatedRecords);
            return true;
        }
    }

    private List<QuanNinhRegistrationRecord> LoadAllUnsafe()
    {
        if (!File.Exists(FilePath))
        {
            return new List<QuanNinhRegistrationRecord>();
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<QuanNinhRegistrationRecord>>(json, JsonOptions)
               ?? new List<QuanNinhRegistrationRecord>();
    }

    private void WriteAllUnsafe(IReadOnlyList<QuanNinhRegistrationRecord> records)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(records, JsonOptions);
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

    private static bool HasSameKey(
        QuanNinhRegistrationRecord left,
        QuanNinhRegistrationRecord right) =>
        string.Equals(left.ClientId, right.ClientId, StringComparison.Ordinal) &&
        left.LocalDate == right.LocalDate;
}

public sealed record QuanNinhEligibilityDecision(
    string ClientId,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    DayOfWeek LocalDay,
    int SuccessfulRegistrationsToday,
    int RemainingSuccessfulRegistrations,
    QuanNinhEligibilityStatus Status,
    string Reason)
{
    public bool IsEligible => Status == QuanNinhEligibilityStatus.Eligible;
}

public sealed class QuanNinhSchedulerService
{
    private readonly IQuanNinhRegistrationHistoryStore _historyStore;
    private readonly QuanNinhSchedulePolicy _policy;
    private readonly TimeZoneInfo _timeZone;

    public QuanNinhSchedulerService(
        IQuanNinhRegistrationHistoryStore historyStore,
        QuanNinhSchedulePolicy? policy = null)
    {
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _policy = policy ?? QuanNinhSchedulePolicy.Default;
        _policy.Validate();
        _timeZone = _policy.ResolveTimeZone();
    }

    public QuanNinhSchedulePolicy Policy => _policy;

    public QuanNinhEligibilityDecision Evaluate(
        string clientId,
        DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc.ToUniversalTime(), _timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var localTime = TimeOnly.FromDateTime(localNow.DateTime);
        var successful = string.IsNullOrWhiteSpace(clientId)
            ? 0
            : _historyStore.Load(clientId.Trim(), localDate)?.SuccessfulRegistrations ?? 0;
        var remaining = Math.Max(0, _policy.MaxSuccessfulRegistrationsPerClientPerDay - successful);

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new QuanNinhEligibilityDecision(
                string.Empty,
                localDate,
                localTime,
                localNow.DayOfWeek,
                successful,
                remaining,
                QuanNinhEligibilityStatus.ClientIdRequired,
                "A registered Client ID is required before Quan Ninh can be scheduled.");
        }

        var normalizedClientId = clientId.Trim();
        if (!_policy.AllowedDays.Contains(localNow.DayOfWeek))
        {
            return Decision(
                normalizedClientId,
                localDate,
                localTime,
                localNow.DayOfWeek,
                successful,
                remaining,
                QuanNinhEligibilityStatus.NotAllowedDay,
                "Today is outside the configured Quan Ninh days.");
        }

        if (!_policy.RegistrationWindows.Any(window => window.Contains(localTime)))
        {
            return Decision(
                normalizedClientId,
                localDate,
                localTime,
                localNow.DayOfWeek,
                successful,
                remaining,
                QuanNinhEligibilityStatus.OutsideRegistrationWindow,
                "Current local time is outside the configured Quan Ninh registration windows.");
        }

        if (successful >= _policy.MaxSuccessfulRegistrationsPerClientPerDay)
        {
            return Decision(
                normalizedClientId,
                localDate,
                localTime,
                localNow.DayOfWeek,
                successful,
                0,
                QuanNinhEligibilityStatus.DailySuccessLimitReached,
                "The daily successful-registration limit has been reached for this client.");
        }

        return Decision(
            normalizedClientId,
            localDate,
            localTime,
            localNow.DayOfWeek,
            successful,
            remaining,
            QuanNinhEligibilityStatus.Eligible,
            "Client is inside a valid Quan Ninh window and has remaining successful registrations.");
    }

    /// <summary>
    /// Records one registration only after the qnyh UI has visibly confirmed
    /// success. Attempts, failures, and UNKNOWN states must not call this.
    /// </summary>
    public QuanNinhRegistrationRecord RecordVerifiedSuccess(
        string clientId,
        DateTimeOffset verifiedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("A registered Client ID is required.", nameof(clientId));
        }

        var normalizedClientId = clientId.Trim();
        var localNow = TimeZoneInfo.ConvertTime(verifiedAtUtc.ToUniversalTime(), _timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        if (!_historyStore.TryRecordVerifiedSuccess(
                normalizedClientId,
                localDate,
                _policy.MaxSuccessfulRegistrationsPerClientPerDay,
                out var updated))
        {
            throw new InvalidOperationException(
                "The daily successful-registration limit has already been reached.");
        }

        return updated!;
    }

    private static QuanNinhEligibilityDecision Decision(
        string clientId,
        DateOnly localDate,
        TimeOnly localTime,
        DayOfWeek localDay,
        int successful,
        int remaining,
        QuanNinhEligibilityStatus status,
        string reason) => new(
        clientId,
        localDate,
        localTime,
        localDay,
        successful,
        remaining,
        status,
        reason);
}

public enum QuanNinhLiveGateState
{
    PendingLiveObservation,
    Verified,
    SafeStop
}

/// <summary>
/// The qnyh-specific UI semantics remain an explicit live-discovery gate.
/// No scheduler call can claim Q-QN-001/Q-QN-002 are verified by policy alone.
/// </summary>
public sealed record QuanNinhLiveGateEvidence(
    string GateId,
    string ClientId,
    string RoleId,
    nint WindowHandle,
    QuanNinhLiveGateState State,
    string? EvidencePath,
    string Reason)
{
    public bool IsVerified => State == QuanNinhLiveGateState.Verified;

    public bool IsBoundTo(QuanNinhLiveTarget target) =>
        string.Equals(ClientId, target.ClientId, StringComparison.Ordinal) &&
        string.Equals(RoleId, target.RoleId, StringComparison.Ordinal) &&
        WindowHandle == target.WindowHandle;
}

public static class QuanNinhLiveGateGuard
{
    public const string SlotRecognitionGate = "Q-QN-001";
    public const string RegistrationConfirmationGate = "Q-QN-002";

    public static void RequireVerified(
        QuanNinhLiveTarget target,
        QuanNinhLiveGateEvidence slotRecognition,
        QuanNinhLiveGateEvidence registrationConfirmation)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(slotRecognition);
        ArgumentNullException.ThrowIfNull(registrationConfirmation);

        if (string.IsNullOrWhiteSpace(target.ClientId) ||
            string.IsNullOrWhiteSpace(target.RoleId) ||
            !string.Equals(target.ClientId, target.RoleId, StringComparison.Ordinal) ||
            target.WindowHandle == nint.Zero)
        {
            throw new InvalidOperationException(
                "Quan Ninh live evidence requires an exact Client ID/Role ID and non-zero HWND target.");
        }

        if (!slotRecognition.IsBoundTo(target) || !registrationConfirmation.IsBoundTo(target))
        {
            throw new InvalidOperationException(
                "Quan Ninh live evidence is bound to a different Client ID, Role ID, or HWND.");
        }

        if (!string.Equals(slotRecognition.GateId, SlotRecognitionGate, StringComparison.Ordinal) ||
            !string.Equals(registrationConfirmation.GateId, RegistrationConfirmationGate, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Quan Ninh live gate IDs are invalid.");
        }

        if (!slotRecognition.IsVerified || !registrationConfirmation.IsVerified)
        {
            throw new InvalidOperationException(
                "Q-QN-001 and Q-QN-002 require verified qnyh live evidence before registration input.");
        }
    }
}

public sealed record QuanNinhLiveTarget(
    string ClientId,
    string RoleId,
    nint WindowHandle,
    bool IsForeground,
    bool KillSwitchActive,
    bool AutomationArmed);

/// <summary>
/// Input remains fail-closed until identity, HWND, foreground, Kill Switch,
/// and both qnyh-specific live gates are verified for the same target.
/// </summary>
public static class QuanNinhLiveInteractionGuard
{
    public static void RequireReady(
        QuanNinhLiveTarget target,
        QuanNinhLiveGateEvidence slotRecognition,
        QuanNinhLiveGateEvidence registrationConfirmation)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(target.ClientId) ||
            string.IsNullOrWhiteSpace(target.RoleId) ||
            !string.Equals(target.ClientId, target.RoleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Quan Ninh input requires an exact registered Client ID and Role ID match.");
        }

        if (target.WindowHandle == nint.Zero)
        {
            throw new InvalidOperationException("Quan Ninh input requires a non-zero bound HWND.");
        }

        if (!target.IsForeground)
        {
            throw new InvalidOperationException("Quan Ninh input requires the exact bound HWND to be foreground.");
        }

        if (target.KillSwitchActive)
        {
            throw new InvalidOperationException("Kill Switch is active; Quan Ninh input is blocked.");
        }

        if (!target.AutomationArmed)
        {
            throw new InvalidOperationException("Automation is not armed; Quan Ninh input is blocked.");
        }

        QuanNinhLiveGateGuard.RequireVerified(target, slotRecognition, registrationConfirmation);
    }
}
