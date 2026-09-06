using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGhost.P0Probe;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = ProbeOptions.Parse(args);
            var config = LoadConfig(options.ConfigPath, options.ProcessName, options.RoleIdRegex);
            var resolver = new RoleIdResolver(config.RoleIdRegex);
            var inventory = new Win32WindowInventory();
            var inventoryResult = inventory.Scan(config.ProcessName, resolver);
            var discovered = inventoryResult.TargetWindows;

            var duplicateRoleIds = discovered
                .Where(static observation => !string.IsNullOrWhiteSpace(observation.RoleId))
                .GroupBy(static observation => observation.RoleId!, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1)
                .Select(static group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            var classified = discovered
                .Select(observation => BindingEngine.Classify(
                    observation, config.RegisteredClients, duplicateRoleIds))
                .ToArray();
            var bindings = new BindingEngine(config.RegisteredClients).Evaluate(classified);
            var inputGuardDecisions = BuildInputGuardEvidence(config, classified, bindings);
            var evidenceDirectory = GetEvidenceDirectory(options.JsonOutputPath);
            var captures = options.Capture
                ? new CaptureProbe().Run(
                    bindings,
                    classified,
                    evidenceDirectory,
                    options.CaptureMinimized)
                : Array.Empty<CaptureEvidence>();
            var inputProbe = new InputProbe();
            var inputTests = new List<InputProbeEvidence>();
            if (options.InputTest && options.AllowInput)
            {
                inputTests.AddRange(inputProbe.Run(bindings, classified));
            }

            if (options.InputNotepadTest && options.AllowInput)
            {
                inputTests.AddRange(inputProbe.RunNotepadProbe());
            }
            var warnings = BuildWarnings(config, inventoryResult, classified);
            var report = new P0ScanReport(
                TimestampUtc: DateTimeOffset.UtcNow,
                ProcessName: config.ProcessName,
                RoleIdRegex: config.RoleIdRegex,
                Processes: inventoryResult.Processes,
                WindowCandidates: inventoryResult.WindowCandidates,
                Windows: classified,
                Bindings: bindings,
                InputGuardDecisions: inputGuardDecisions,
                Captures: captures,
                InputTests: inputTests,
                Warnings: warnings);

            WriteHumanReport(report, options);
            if (options.JsonOutputPath is not null)
            {
                var json = JsonSerializer.Serialize(report, JsonOptions);
                File.WriteAllText(options.JsonOutputPath, json);
                Console.WriteLine($"Evidence JSON: {Path.GetFullPath(options.JsonOutputPath)}");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P0 probe failed closed: {exception.GetType().Name}: {exception.Message}");
            return 2;
        }
    }

    private static P0Config LoadConfig(string? path, string? processNameOverride, string? roleIdRegexOverride)
    {
        P0Config config;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<P0Config>(json, JsonOptions) ?? new P0Config();
        }
        else
        {
            config = new P0Config();
        }

        return config with
        {
            ProcessName = string.IsNullOrWhiteSpace(processNameOverride)
                ? config.ProcessName
                : processNameOverride,
            RoleIdRegex = roleIdRegexOverride ?? config.RoleIdRegex
        };
    }

    private static IReadOnlyList<string> BuildWarnings(
        P0Config config,
        WindowInventoryResult inventory,
        IReadOnlyList<WindowObservation> observations)
    {
        var warnings = new List<string>();
        if (!inventory.Processes.Any(static process => process.ProcessTreeRelation == "Target"))
        {
            warnings.Add($"No running process named '{config.ProcessName}.exe' was discovered in the system process snapshot.");
        }

        if (inventory.Processes.Any(static process => process.ProcessTreeRelation == "Target") &&
            inventory.TargetWindows.Count == 0)
        {
            warnings.Add("Target process instances were found, but no top-level window mapped to their process ancestry.");
        }

        if (string.IsNullOrWhiteSpace(config.RoleIdRegex))
        {
            warnings.Add("RoleIdRegex is not configured. Title bars are recorded as raw evidence only; no Role ID bind can occur.");
        }

        if (observations.Any(static observation => observation.IsMinimized))
        {
            warnings.Add("At least one target window is minimized; capture/input behavior must be verified separately before automation.");
        }

        if (observations.Any(static observation => observation.WindowRect is null || observation.ClientRect is null))
        {
            warnings.Add("At least one target window geometry query failed; coordinate/capture claims must remain unverified.");
        }

        if (inventory.WindowCandidates.Any(static window =>
                window.RoleId is not null && !window.IsTargetProcessTree))
        {
            warnings.Add("At least one Role ID-like title belongs outside the qnyh process tree; inspect owner PID and ancestry before binding.");
        }

        return warnings;
    }

    private static IReadOnlyList<InputGuardEvidence> BuildInputGuardEvidence(
        P0Config config,
        IReadOnlyList<WindowObservation> observations,
        IReadOnlyList<RuntimeBinding> bindings)
    {
        var guard = new InputGuard();
        var evidence = new List<InputGuardEvidence>();
        var targetProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            config.ProcessName
        };

        foreach (var observation in observations.Where(observation =>
                     targetProcessNames.Contains(observation.ProcessName)))
        {
            var binding = bindings.FirstOrDefault(candidate =>
                candidate.ProcessId == observation.ProcessId &&
                candidate.WindowHandle == observation.WindowHandle);
            var decision = guard.Validate(
                binding,
                observation,
                killSwitchActive: false,
                inputEnabled: true);

            evidence.Add(new InputGuardEvidence(
                Scenario: "identity-guard",
                ProcessId: observation.ProcessId,
                WindowHandle: observation.WindowHandle,
                RoleId: observation.RoleId,
                IdentityState: observation.IdentityState,
                BindingReady: binding?.State == BindingState.Ready,
                KillSwitchActive: false,
                InputEnabledForDecision: true,
                Allowed: decision.Allowed,
                Reason: decision.Reason));

            if (binding is not null)
            {
                var killSwitchDecision = guard.Validate(
                    binding,
                    observation,
                    killSwitchActive: true,
                    inputEnabled: true);
                evidence.Add(new InputGuardEvidence(
                    Scenario: "kill-switch-on",
                    ProcessId: observation.ProcessId,
                    WindowHandle: observation.WindowHandle,
                    RoleId: observation.RoleId,
                    IdentityState: observation.IdentityState,
                    BindingReady: binding.State == BindingState.Ready,
                    KillSwitchActive: true,
                    InputEnabledForDecision: true,
                    Allowed: killSwitchDecision.Allowed,
                    Reason: killSwitchDecision.Reason));
            }
        }

        return evidence;
    }

    private static string GetEvidenceDirectory(string? jsonOutputPath)
    {
        var directory = string.IsNullOrWhiteSpace(jsonOutputPath)
            ? "artifacts"
            : Path.GetDirectoryName(jsonOutputPath);
        directory = string.IsNullOrWhiteSpace(directory) ? "artifacts" : directory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteHumanReport(P0ScanReport report, ProbeOptions options)
    {
        Console.WriteLine("AutoGhost P0 Probe");
        Console.WriteLine($"UTC: {report.TimestampUtc:O}");
        Console.WriteLine($"Process filter: {report.ProcessName}.exe");
        Console.WriteLine($"Role ID source hypothesis: title bar; regex configured: {!string.IsNullOrWhiteSpace(report.RoleIdRegex)}");
        Console.WriteLine($"Target process-tree entries: {report.Processes.Count}");
        Console.WriteLine($"Top-level windows enumerated system-wide: {report.WindowCandidates.Count}");
        Console.WriteLine($"Target process-tree windows: {report.Windows.Count}");

        foreach (var process in report.Processes)
        {
            Console.WriteLine(
                $"- PROCESS PID={process.ProcessId} Name='{process.ProcessName}' " +
                $"ParentPID={process.ParentProcessId?.ToString() ?? "<none>"} " +
                $"Relation={process.ProcessTreeRelation} Path='{process.ProcessPath ?? "<unavailable>"}' " +
                $"Ancestry=[{string.Join(",", process.ProcessAncestry)}]");
        }

        foreach (var window in report.Windows.Where(static window =>
                     window.IsVisible || !string.IsNullOrWhiteSpace(window.Title) || window.RoleId is not null))
        {
            Console.WriteLine(
                $"- PID={window.ProcessId} HWND=0x{window.WindowHandle.ToInt64():X} Thread={window.ThreadId} " +
                $"Visible={window.IsVisible} Enabled={window.IsEnabled} Minimized={window.IsMinimized} " +
                $"Cloaked={window.IsCloaked} Foreground={window.IsForeground} Relation={window.ProcessTreeRelation} " +
                $"Owner=0x{window.OwnerWindowHandle.ToInt64():X} Parent=0x{window.ParentWindowHandle.ToInt64():X} " +
                $"Class='{window.ClassName}' Title='{window.Title}' RoleId='{window.RoleId ?? "<unresolved>"}' " +
                $"Identity={window.IdentityState}");
            Console.WriteLine($"  Reason: {window.IdentityReason}");
        }

        foreach (var window in report.WindowCandidates.Where(static window =>
                     window.RoleId is not null && !window.IsTargetProcessTree))
        {
            Console.WriteLine(
                $"- OUTSIDE-TREE ROLE TITLE PID={window.ProcessId} HWND=0x{window.WindowHandle.ToInt64():X} " +
                $"Process='{window.ProcessName}' RoleId='{window.RoleId}' Title='{window.Title}'");
        }

        var safeInputProbe = (options.InputTest || options.InputNotepadTest) && options.AllowInput;
        Console.WriteLine(safeInputProbe
            ? "InputGuard decisions (safe input probe enabled; only the guarded F13 probe may emit OS input):"
            : "InputGuard decisions (dispatch remains suppressed by dry-run):");
        foreach (var decision in report.InputGuardDecisions)
        {
            Console.WriteLine(
                $"- Scenario={decision.Scenario} PID={decision.ProcessId} " +
                $"HWND=0x{decision.WindowHandle.ToInt64():X} RoleId='{decision.RoleId ?? "<unresolved>"}' " +
                $"BindingReady={decision.BindingReady} KillSwitch={decision.KillSwitchActive} " +
                $"Allowed={decision.Allowed} Reason='{decision.Reason}'");
        }

        foreach (var capture in report.Captures)
        {
            Console.WriteLine(
                $"- Capture Scenario={capture.Scenario} PID={capture.ProcessId} " +
                $"HWND=0x{capture.WindowHandle.ToInt64():X} Method={capture.Method} " +
                $"Success={capture.OperationSucceeded} Foreground={capture.ForegroundObserved} " +
                $"Minimized={capture.MinimizedObserved} Size={capture.Width}x{capture.Height} " +
                $"NonBlankRatio={capture.NonBlankRatio:F3} Evidence='{capture.EvidencePath}' " +
                $"Limitation='{capture.Limitation}'");
        }

        foreach (var input in report.InputTests)
        {
            Console.WriteLine(
                $"- Input Scenario={input.Scenario} PID={input.ProcessId} " +
                $"HWND=0x{input.WindowHandle.ToInt64():X} Key={input.Key} " +
                $"ForegroundRequest={input.ForegroundRequestSucceeded} " +
                $"ForegroundObserved={input.ForegroundObserved} EventsInjected={input.EventsInjected} " +
                $"DispatchSucceeded={input.DispatchSucceeded} Limitation='{input.Limitation}'");
        }

        Console.WriteLine($"READY bindings: {report.Bindings.Count}");
        foreach (var binding in report.Bindings)
        {
            Console.WriteLine(
                $"- ClientId='{binding.ClientId}' RoleId='{binding.RoleId}' PID={binding.ProcessId} " +
                $"HWND=0x{binding.WindowHandle.ToInt64():X} Session={binding.SessionId} State={binding.State}");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"WARNING: {warning}");
        }

        if (safeInputProbe)
        {
            Console.WriteLine("Input mode: SAFE INPUT TEST; only the guarded F13 probe may emit OS input.");
        }
        else
        {
            Console.WriteLine(options.DryRun
                ? "Input mode: DRY-RUN; InputGuard is not permitted to emit OS input."
                : "Input mode: probe only; no dispatcher is wired in P0.");
        }
        if ((options.InputTest || options.InputNotepadTest) && !options.AllowInput)
        {
            Console.WriteLine("Input test requested without --allow-input; no OS input probe was run.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(), new NintJsonConverter() }
    };
}

internal sealed class NintJsonConverter : JsonConverter<nint>
{
    public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
        {
            return new nint(number);
        }

        if (reader.TokenType == JsonTokenType.String &&
            long.TryParse(reader.GetString(), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hexadecimal))
        {
            return new nint(hexadecimal);
        }

        throw new JsonException("Expected an HWND-sized integer or hexadecimal string.");
    }

    public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"0x{value.ToInt64():X}");
    }
}

public sealed record ProbeOptions(
    string? ConfigPath,
    string? ProcessName,
    string? RoleIdRegex,
    string? JsonOutputPath,
    bool DryRun,
    bool Capture,
    bool CaptureMinimized,
    bool InputTest,
    bool InputNotepadTest,
    bool AllowInput)
{
    public static ProbeOptions Parse(string[] args)
    {
        string? config = null;
        string? processName = null;
        string? roleRegex = null;
        string? jsonOutput = null;
        var dryRun = true;
        var capture = false;
        var captureMinimized = false;
        var inputTest = false;
        var inputNotepadTest = false;
        var allowInput = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    config = RequireValue(args, ref index, "--config");
                    break;
                case "--process-name":
                    processName = RequireValue(args, ref index, "--process-name");
                    break;
                case "--role-regex":
                    roleRegex = RequireValue(args, ref index, "--role-regex");
                    break;
                case "--json":
                    jsonOutput = RequireValue(args, ref index, "--json");
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--capture":
                    capture = true;
                    break;
                case "--capture-minimized":
                    capture = true;
                    captureMinimized = true;
                    break;
                case "--input-test":
                    inputTest = true;
                    break;
                case "--input-notepad-test":
                    inputNotepadTest = true;
                    break;
                case "--allow-input":
                    allowInput = true;
                    dryRun = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        return new ProbeOptions(
            config,
            processName,
            roleRegex,
            jsonOutput,
            dryRun,
            capture,
            captureMinimized,
            inputTest,
            inputNotepadTest,
            allowInput);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }
}
