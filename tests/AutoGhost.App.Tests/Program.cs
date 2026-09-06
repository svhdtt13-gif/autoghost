using System.IO;
using AutoGhost.App.Services;
using AutoGhost.App.ViewModels;
using AutoGhost.P0Probe;

namespace AutoGhost.App.Tests;

public static class Program
{
    public static int Main()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "AutoGhost-P1-tests", Guid.NewGuid().ToString("N"));
        var testPath = Path.Combine(testDirectory, "client-registry.json");

        try
        {
            var expected = new P0Config(
                ProcessName: "qnyh",
                RoleIdRegex: ClientRegistryService.DefaultRoleIdRegex,
                Clients: new[]
                {
                    new ClientDefinition("client-alpha", Enabled: true, SpecialItems: new[] { "Special Alpha" }),
                    new ClientDefinition("client-disabled", Enabled: false)
                });
            var service = new ClientRegistryService(testPath);

            service.Save(expected);
            var loaded = service.Load();
            var savedJson = File.ReadAllText(testPath);
            var viewModel = new MainWindowViewModel(service, new RuntimeProbeService());

            Assert(File.Exists(testPath), "registry file was not created");
            Assert(loaded.Warning is null, "registry load returned an unexpected warning");
            Assert(loaded.Config.ProcessName == expected.ProcessName, "process name did not round-trip");
            Assert(loaded.Config.RoleIdRegex == expected.RoleIdRegex, "Role ID regex did not round-trip");
            Assert(loaded.Config.RegisteredClients.Count == 2, "client count did not round-trip");
            Assert(loaded.Config.RegisteredClients[0].ClientId == expected.RegisteredClients[0].ClientId &&
                   loaded.Config.RegisteredClients[0].Enabled == expected.RegisteredClients[0].Enabled,
                "enabled client did not round-trip");
            Assert(loaded.Config.RegisteredClients[1].ClientId == expected.RegisteredClients[1].ClientId &&
                   loaded.Config.RegisteredClients[1].Enabled == expected.RegisteredClients[1].Enabled,
                "disabled client did not round-trip");
            Assert(loaded.Config.RegisteredClients[0].SpecialItems?.SequenceEqual(new[] { "Special Alpha" }) == true,
                "client-specific Special Items did not round-trip");
            Assert(viewModel.Clients[0].ConfiguredSpecialItems.SequenceEqual(new[] { "Special Alpha" }),
                "the app view model did not restore client-specific Special Items");
            Assert(savedJson.Contains("specialItems", StringComparison.OrdinalIgnoreCase),
                "client-specific Special Items were not written to the registry");
            viewModel.Clients[0].SpecialItemsText = "Edited One\nEdited Two";
            viewModel.SaveRegistry();
            var edited = service.Load();
            Assert(edited.Config.RegisteredClients[0].SpecialItems?.SequenceEqual(new[] { "Edited One", "Edited Two" }) == true,
                "editing Special Items for an existing client did not persist");
            Assert(!savedJson.Contains("processId", StringComparison.OrdinalIgnoreCase), "PID was persisted as registry state");
            Assert(!savedJson.Contains("windowHandle", StringComparison.OrdinalIgnoreCase), "HWND was persisted as registry state");
            Assert(!savedJson.Contains("sessionId", StringComparison.OrdinalIgnoreCase), "runtime session was persisted as registry state");
            Assert(viewModel.KillSwitchActive, "Kill Switch did not start active");
            Assert(!viewModel.AutomationArmed, "automation restored armed after restart");

            Console.WriteLine("P1 registry persistence + disarmed startup: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"P1 registry persistence: FAIL — {exception.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
