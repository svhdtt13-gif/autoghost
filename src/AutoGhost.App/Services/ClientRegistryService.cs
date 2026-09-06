using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoGhost.P0Probe;

namespace AutoGhost.App.Services;

public sealed record ClientRegistryLoadResult(P0Config Config, string? Warning);

/// <summary>
/// Persists only operator-owned registry/configuration data. Runtime identity is
/// always discovered from the live window title and is never persisted as truth.
/// </summary>
public sealed class ClientRegistryService
{
    public const string DefaultRoleIdRegex = @"Role\s*\[(?<roleId>\d+)\]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ClientRegistryService(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultFilePath()
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public ClientRegistryLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            return new ClientRegistryLoadResult(
                new P0Config(
                    RoleIdRegex: DefaultRoleIdRegex,
                    Clients: Array.Empty<ClientDefinition>()),
                null);
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var config = JsonSerializer.Deserialize<P0Config>(json, JsonOptions)
                         ?? new P0Config(Clients: Array.Empty<ClientDefinition>());
            return new ClientRegistryLoadResult(config, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClientRegistryLoadResult(
                new P0Config(
                    RoleIdRegex: DefaultRoleIdRegex,
                    Clients: Array.Empty<ClientDefinition>()),
                $"Registry could not be loaded; using an empty registry: {exception.Message}");
        }
    }

    public void Save(P0Config config)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    private static string GetDefaultFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "AutoGhost", "client-registry.json");
    }
}
