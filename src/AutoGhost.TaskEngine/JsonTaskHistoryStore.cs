using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGhost.TaskEngine;

public sealed class JsonTaskHistoryStore : ITaskHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public JsonTaskHistoryStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A history file path is required.", nameof(filePath));
        }

        FilePath = filePath;
    }

    public string FilePath { get; }

    public IReadOnlyList<TaskRunHistory> Load()
    {
        if (!File.Exists(FilePath))
        {
            return Array.Empty<TaskRunHistory>();
        }

        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<TaskRunHistory>>(json, SerializerOptions)
               ?? new List<TaskRunHistory>();
    }

    public void Append(TaskRunHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var directory = System.IO.Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var all = Load().ToList();
        all.Add(history);

        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(all, SerializerOptions));
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
