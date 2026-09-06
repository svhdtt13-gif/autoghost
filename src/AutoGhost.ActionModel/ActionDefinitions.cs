using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGhost.ActionModel;

public enum RecordedActionKind
{
    MouseMove,
    MouseButton,
    MouseWheel,
    Key,
    SemanticHook
}

public enum RecordedMouseButton
{
    Left,
    Right,
    Middle,
    X1,
    X2
}

public enum RecordedKeyEvent
{
    Down,
    Up
}

public enum SemanticHookKind
{
    Text,
    Template,
    State,
    Coordinate
}

public sealed record NormalizedPoint(double X, double Y)
{
    public static NormalizedPoint FromClientPixel(int pixelX, int pixelY, int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), "Client dimensions must be positive.");
        }

        return new NormalizedPoint(
            Math.Clamp(pixelX / (double)Math.Max(1, clientWidth - 1), 0d, 1d),
            Math.Clamp(pixelY / (double)Math.Max(1, clientHeight - 1), 0d, 1d));
    }

    public (int X, int Y) ToClientPixel(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clientWidth), "Client dimensions must be positive.");
        }

        return (
            (int)Math.Round(Math.Clamp(X, 0d, 1d) * Math.Max(1, clientWidth - 1)),
            (int)Math.Round(Math.Clamp(Y, 0d, 1d) * Math.Max(1, clientHeight - 1)));
    }
}

public sealed record SemanticHookDefinition(
    string Name,
    SemanticHookKind Kind,
    string Query,
    int TimeoutMs = 5000,
    bool Required = true,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    public IReadOnlyDictionary<string, string> SafeParameters => Parameters ??
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record ActionStep(
    int Sequence,
    RecordedActionKind Kind,
    int DelayBeforeMs,
    NormalizedPoint? Point = null,
    RecordedMouseButton? MouseButton = null,
    bool? IsDown = null,
    int? VirtualKey = null,
    RecordedKeyEvent? KeyEvent = null,
    int? WheelDelta = null,
    SemanticHookDefinition? Semantic = null,
    string? Note = null);

public sealed record ActionDefinition(
    int SchemaVersion,
    string Name,
    string TargetClientId,
    DateTimeOffset RecordedAtUtc,
    int RecordedClientWidth,
    int RecordedClientHeight,
    IReadOnlyList<ActionStep> Steps)
{
    public static ActionDefinition Create(
        string name,
        string targetClientId,
        int clientWidth,
        int clientHeight,
        IReadOnlyList<ActionStep> steps) => new(
            SchemaVersion: 1,
            Name: name.Trim(),
            TargetClientId: targetClientId.Trim(),
            RecordedAtUtc: DateTimeOffset.UtcNow,
            RecordedClientWidth: clientWidth,
            RecordedClientHeight: clientHeight,
            Steps: steps.ToArray());
}

public sealed record ActionValidationResult(bool IsValid, string Reason)
{
    public static ActionValidationResult Pass() => new(true, "Action definition is valid.");
    public static ActionValidationResult Fail(string reason) => new(false, reason);
}

public static class ActionDefinitionValidator
{
    public static ActionValidationResult Validate(ActionDefinition? definition)
    {
        if (definition is null)
        {
            return ActionValidationResult.Fail("Action definition is missing.");
        }

        if (definition.SchemaVersion != 1)
        {
            return ActionValidationResult.Fail($"Unsupported action schema version {definition.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(definition.Name) || string.IsNullOrWhiteSpace(definition.TargetClientId))
        {
            return ActionValidationResult.Fail("Action name and TargetClientId are required.");
        }

        if (definition.RecordedClientWidth <= 0 || definition.RecordedClientHeight <= 0)
        {
            return ActionValidationResult.Fail("Recorded client dimensions must be positive.");
        }

        var expectedSequence = 0;
        foreach (var step in definition.Steps)
        {
            if (step.Sequence != expectedSequence++)
            {
                return ActionValidationResult.Fail("Action sequences must be contiguous and zero-based.");
            }

            if (step.DelayBeforeMs < 0)
            {
                return ActionValidationResult.Fail("Action delays cannot be negative.");
            }

            if (step.Point is { } point &&
                (point.X is < 0 or > 1 || point.Y is < 0 or > 1 ||
                 double.IsNaN(point.X) || double.IsNaN(point.Y)))
            {
                return ActionValidationResult.Fail("Action points must be finite normalized coordinates in [0,1].");
            }

            if (step.Kind is RecordedActionKind.MouseMove or RecordedActionKind.MouseButton && step.Point is null)
            {
                return ActionValidationResult.Fail($"Step {step.Sequence} requires a normalized point.");
            }

            if (step.Kind == RecordedActionKind.MouseButton && (step.MouseButton is null || step.IsDown is null))
            {
                return ActionValidationResult.Fail($"Mouse button step {step.Sequence} requires button and down/up state.");
            }

            if (step.Kind == RecordedActionKind.MouseWheel && step.WheelDelta is null)
            {
                return ActionValidationResult.Fail($"Mouse wheel step {step.Sequence} requires WheelDelta.");
            }

            if (step.Kind == RecordedActionKind.Key && (step.VirtualKey is null || step.KeyEvent is null))
            {
                return ActionValidationResult.Fail($"Key step {step.Sequence} requires virtual key and down/up state.");
            }

            if (step.Kind == RecordedActionKind.SemanticHook && step.Semantic is null)
            {
                return ActionValidationResult.Fail($"Semantic step {step.Sequence} requires a hook definition.");
            }

            if (step.Semantic is { TimeoutMs: <= 0 })
            {
                return ActionValidationResult.Fail($"Semantic hook on step {step.Sequence} requires a positive timeout.");
            }
        }

        return ActionValidationResult.Pass();
    }
}

public sealed class ActionDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Save(string path, ActionDefinition definition)
    {
        var validation = ActionDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Reason);
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Action path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(definition, JsonOptions));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }

    public ActionDefinition Load(string path)
    {
        var definition = JsonSerializer.Deserialize<ActionDefinition>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Action file is empty.");
        var validation = ActionDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Reason);
        }

        return definition;
    }
}
