using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using AutoGhost.ActionModel;
using AutoGhost.App;
using AutoGhost.App.Services;
using AutoGhost.P0Probe;

namespace AutoGhost.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ClientRegistryService _registryService;
    private readonly RuntimeProbeService _runtimeProbeService;
    private readonly ActionRecordingService _actionRecordingService;
    private readonly ActionDefinitionStore _actionStore = new();
    private string _processName;
    private string _roleIdRegex;
    private string _actionName = "recorded-action";
    private ClientRowViewModel? _selectedClient;
    private ClientRowViewModel? _selectedRecordingClient;
    private string _statusMessage;
    private string _lastRefresh = "Not scanned yet.";
    private string _p2Status = "P2 recorder idle; replay is dry-run only.";
    private string _lastReplayEvent = "No replay event yet.";
    private bool _killSwitchActive = true;
    private bool _isBusy;
    private ActionDefinition? _lastRecordedAction;
    private DryRunReplayEngine? _dryRunEngine;

    public MainWindowViewModel(
        ClientRegistryService? registryService = null,
        RuntimeProbeService? runtimeProbeService = null)
    {
        _registryService = registryService ?? new ClientRegistryService();
        _runtimeProbeService = runtimeProbeService ?? new RuntimeProbeService();
        _actionRecordingService = new ActionRecordingService(IsVerifiedRecordingTarget);
        _actionRecordingService.StepCountChanged += (_, _) => OnPropertyChanged(nameof(RecordedStepCount));

        var loaded = _registryService.Load();
        _processName = loaded.Config.ProcessName;
        _roleIdRegex = loaded.Config.RoleIdRegex ?? ClientRegistryService.DefaultRoleIdRegex;
        _statusMessage = loaded.Warning ?? "Add enabled Client IDs, then refresh to verify live bindings.";

        foreach (var client in loaded.Config.RegisteredClients)
        {
            if (!string.IsNullOrWhiteSpace(client.ClientId))
            {
                Clients.Add(new ClientRowViewModel(client.ClientId.Trim(), client.Enabled, client.SpecialItems));
            }
        }

        var restoredClientIds = string.Join(",", Clients.Select(static client => client.ClientId));
        WpfStartupDiagnostics.LogStage(
            "Registry.Restore",
            $"path='{_registryService.FilePath}' configuredClients=[{restoredClientIds}] " +
            $"enabledCount={Clients.Count(static client => client.IsEnabled)} killSwitchActive={_killSwitchActive} automationArmed={AutomationArmed}");

        RegistryPath = _registryService.FilePath;
        AddClientCommand = new RelayCommand(parameter => AddClient(parameter as Window));
        RemoveClientCommand = new RelayCommand(
            _ => RemoveSelectedClient(),
            _ => SelectedClient is not null);
        ToggleClientCommand = new RelayCommand(
            _ => ToggleSelectedClient(),
            _ => SelectedClient is not null);
        SaveCommand = new RelayCommand(_ => SaveRegistry());
        RefreshCommand = new AsyncRelayCommand(RefreshRuntimeAsync);
        KillSwitchCommand = new RelayCommand(_ => ToggleKillSwitch());
        StartRecordingCommand = new RelayCommand(_ => StartRecording());
        StopRecordingCommand = new RelayCommand(_ => StopRecording());
        SaveActionCommand = new RelayCommand(_ => SaveAction());
        DryRunCommand = new AsyncRelayCommand(RunDryRunAsync);
        PauseResumeDryRunCommand = new RelayCommand(_ => ToggleDryRunPause());
        StopDryRunCommand = new RelayCommand(_ => StopDryRun());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ClientRowViewModel> Clients { get; } = new();

    public string RegistryPath { get; }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (string.Equals(_processName, value, StringComparison.Ordinal))
            {
                return;
            }

            _processName = value;
            OnPropertyChanged();
        }
    }

    public string RoleIdRegex
    {
        get => _roleIdRegex;
        set
        {
            if (string.Equals(_roleIdRegex, value, StringComparison.Ordinal))
            {
                return;
            }

            _roleIdRegex = value;
            OnPropertyChanged();
        }
    }

    public string ActionName
    {
        get => _actionName;
        set
        {
            var normalized = value.Trim();
            if (string.Equals(_actionName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _actionName = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionFilePath));
        }
    }

    public ClientRowViewModel? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (ReferenceEquals(_selectedClient, value))
            {
                return;
            }

            _selectedClient = value;
            OnPropertyChanged();
            RemoveClientCommand.RaiseCanExecuteChanged();
            ToggleClientCommand.RaiseCanExecuteChanged();
        }
    }

    public ClientRowViewModel? SelectedRecordingClient
    {
        get => _selectedRecordingClient;
        set
        {
            if (ReferenceEquals(_selectedRecordingClient, value))
            {
                return;
            }

            _selectedRecordingClient = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActionFilePath));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(_statusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public string LastRefresh
    {
        get => _lastRefresh;
        private set
        {
            if (string.Equals(_lastRefresh, value, StringComparison.Ordinal))
            {
                return;
            }

            _lastRefresh = value;
            OnPropertyChanged();
        }
    }

    public string P2Status
    {
        get => _p2Status;
        private set
        {
            if (string.Equals(_p2Status, value, StringComparison.Ordinal))
            {
                return;
            }

            _p2Status = value;
            OnPropertyChanged();
        }
    }

    public string LastReplayEvent
    {
        get => _lastReplayEvent;
        private set
        {
            if (string.Equals(_lastReplayEvent, value, StringComparison.Ordinal))
            {
                return;
            }

            _lastReplayEvent = value;
            OnPropertyChanged();
        }
    }

    public int RecordedStepCount => _actionRecordingService.StepCount;
    public bool IsRecording => _actionRecordingService.IsRecording;
    public string ActionFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoGhost",
        "actions",
        $"{SanitizeFileName(ActionName)}.action.json");

    public bool KillSwitchActive
    {
        get => _killSwitchActive;
        private set
        {
            if (_killSwitchActive == value)
            {
                return;
            }

            _killSwitchActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KillSwitchText));
        }
    }

    public string KillSwitchText => KillSwitchActive ? "KILL SWITCH: ON" : "KILL SWITCH: OFF";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// P1 has no automation dispatcher. Keep this explicit so a future phase
    /// cannot accidentally restore an armed automation state from persistence.
    /// </summary>
    public bool AutomationArmed => false;

    public string AutomationStateText => AutomationArmed ? "ARMED" : "DISARMED";

    public RelayCommand AddClientCommand { get; }
    public RelayCommand RemoveClientCommand { get; }
    public RelayCommand ToggleClientCommand { get; }
    public RelayCommand SaveCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand KillSwitchCommand { get; }
    public RelayCommand StartRecordingCommand { get; }
    public RelayCommand StopRecordingCommand { get; }
    public RelayCommand SaveActionCommand { get; }
    public AsyncRelayCommand DryRunCommand { get; }
    public RelayCommand PauseResumeDryRunCommand { get; }
    public RelayCommand StopDryRunCommand { get; }

    public void AddClient(Window? owner)
    {
        var dialog = new ClientDialog();
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var clientId = dialog.ClientId;
        if (Clients.Any(client => string.Equals(client.ClientId, clientId, StringComparison.Ordinal)))
        {
            StatusMessage = $"Client ID '{clientId}' already exists; no duplicate was added.";
            return;
        }

        Clients.Add(new ClientRowViewModel(
            clientId,
            isEnabled: true,
            specialItems: ClientRowViewModel.ParseSpecialItems(dialog.SpecialItems)));
        SelectedClient = Clients[^1];
        StatusMessage = $"Added Client ID '{clientId}'. Save the registry when ready.";
    }

    public void RemoveSelectedClient()
    {
        if (SelectedClient is null)
        {
            return;
        }

        var selected = SelectedClient;
        var result = MessageBox.Show(
            $"Remove Client ID '{selected.ClientId}' from the registry?",
            "Confirm registry change",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Clients.Remove(selected);
        SelectedClient = Clients.LastOrDefault();
        StatusMessage = $"Removed Client ID '{selected.ClientId}'. Save the registry when ready.";
    }

    public void ToggleSelectedClient()
    {
        if (SelectedClient is null)
        {
            return;
        }

        SelectedClient.IsEnabled = !SelectedClient.IsEnabled;
        StatusMessage = SelectedClient.IsEnabled
            ? $"Enabled Client ID '{SelectedClient.ClientId}'."
            : $"Disabled Client ID '{SelectedClient.ClientId}'; it cannot bind or receive input.";
    }

    public void SaveRegistry()
    {
        if (!TryBuildConfig(out var config, out var error))
        {
            StatusMessage = error;
            return;
        }

        try
        {
            _registryService.Save(config);
            StatusMessage = $"Registry saved: {RegistryPath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Registry save failed closed: {exception.Message}";
        }
    }

    public async Task RefreshRuntimeAsync()
    {
        if (_actionRecordingService.IsRecording)
        {
            StatusMessage = "Stop the foreground recorder before refreshing runtime bindings.";
            return;
        }

        if (!TryBuildConfig(out var config, out var error))
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => _runtimeProbeService.Scan(config));
            ApplyRuntimeResult(result);
            LastRefresh = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} local time";

            var readyCount = result.Bindings.Count;
            var enabledCount = Clients.Count(client => client.IsEnabled);
            StatusMessage =
                $"P0 PASS · read-only scan complete: {readyCount}/{enabledCount} enabled client(s) ready. " +
                $"Processes={result.Processes.Count}, windows={result.Windows.Count}.";
        }
        catch (Exception exception)
        {
            foreach (var client in Clients)
            {
                client.ClearRuntime("Runtime scan failed closed; no binding is trusted.");
            }

            StatusMessage = $"Runtime scan failed closed: {exception.GetType().Name}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyRuntimeResult(RuntimeProbeResult result)
    {
        foreach (var client in Clients)
        {
            var matches = result.Windows
                .Where(observation => string.Equals(
                    observation.RoleId,
                    client.ClientId,
                    StringComparison.Ordinal))
                .ToArray();
            var match = matches.FirstOrDefault();
            if (match is null)
            {
                client.ClearRuntime();
                continue;
            }

            var binding = result.Bindings.FirstOrDefault(candidate =>
                string.Equals(candidate.ClientId, client.ClientId, StringComparison.Ordinal) &&
                candidate.ProcessId == match.ProcessId &&
                candidate.WindowHandle == match.WindowHandle);
            client.ApplyRuntime(match, binding);
        }

        if (SelectedRecordingClient is null || !Clients.Contains(SelectedRecordingClient))
        {
            SelectedRecordingClient = Clients.FirstOrDefault(static client => client.IsRuntimeReady);
        }

        var rebindDetails = string.Join(
            ";",
            Clients.Select(static client =>
                $"client={client.ClientId},state={client.RuntimeState},role={client.ObservedRoleId}," +
                $"pid={client.ProcessId},hwnd={client.WindowHandle},session={client.SessionId}"));
        WpfStartupDiagnostics.LogStage(
            "Runtime.Rebind",
            $"bindings={result.Bindings.Count} uniqueClientIds={Clients.Select(static client => client.ClientId).Distinct(StringComparer.Ordinal).Count()} details={rebindDetails}");
    }

    private void ToggleKillSwitch()
    {
        KillSwitchActive = !KillSwitchActive;
        if (KillSwitchActive)
        {
            _dryRunEngine?.Stop();
        }

        StatusMessage = KillSwitchActive
            ? "Kill Switch ON: replay is stopped and all future dispatch remains suppressed."
            : "Kill Switch OFF: P2 still exposes dry-run only; no OS input is emitted by this build.";
    }

    private void StartRecording()
    {
        if (SelectedRecordingClient is null)
        {
            P2Status = "Recorder blocked: select a READY client first.";
            return;
        }

        var target = SelectedRecordingClient.CreateRecordingTarget();
        if (target is null)
        {
            P2Status = "Recorder blocked: selected client is not READY with a valid client area.";
            return;
        }

        try
        {
            _actionRecordingService.Start(target);
            P2Status = $"Recording '{target.ClientId}' foreground input only. No input will be emitted.";
            OnPropertyChanged(nameof(IsRecording));
        }
        catch (Exception exception)
        {
            P2Status = $"Recorder failed closed: {exception.Message}";
        }
    }

    private void StopRecording()
    {
        if (!_actionRecordingService.IsRecording)
        {
            P2Status = "Recorder is not active.";
            return;
        }

        try
        {
            _lastRecordedAction = _actionRecordingService.Stop(ActionName);
            P2Status = $"Recording stopped: {_lastRecordedAction.Steps.Count} step(s) captured; save before dry-run.";
            OnPropertyChanged(nameof(IsRecording));
        }
        catch (Exception exception)
        {
            P2Status = $"Recorder stop failed closed: {exception.Message}";
        }
    }

    private void SaveAction()
    {
        if (_lastRecordedAction is null)
        {
            P2Status = "No completed recording is available to save.";
            return;
        }

        try
        {
            _actionStore.Save(ActionFilePath, _lastRecordedAction);
            P2Status = $"Action definition saved: {ActionFilePath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            P2Status = $"Action save failed closed: {exception.Message}";
        }
    }

    private async Task RunDryRunAsync()
    {
        if (_lastRecordedAction is null)
        {
            P2Status = "Dry-run blocked: stop a recording first.";
            return;
        }

        var client = SelectedRecordingClient;
        if (client is null || !client.IsRuntimeReady)
        {
            P2Status = "Dry-run blocked: selected client is not READY.";
            return;
        }

        _dryRunEngine = new DryRunReplayEngine();
        var context = new ReplayContext(
            TargetClientId: client.ClientId,
            ObservedRoleId: client.ObservedRoleId,
            BindingReady: client.IsRuntimeReady,
            KillSwitchActive: KillSwitchActive);
        var progress = new Progress<DryRunReplayEvent>(replayEvent =>
        {
            LastReplayEvent = $"#{replayEvent.Sequence} {replayEvent.Kind}: {replayEvent.Outcome}";
        });

        P2Status = "Dry-run replay started; every dispatch is suppressed.";
        var report = await _dryRunEngine.RunAsync(_lastRecordedAction, context, progress);
        P2Status = $"Dry-run {report.State}: {report.Reason} Events={report.Events.Count}.";
        _dryRunEngine = null;
    }

    private void ToggleDryRunPause()
    {
        if (_dryRunEngine is null || !_dryRunEngine.IsRunning)
        {
            P2Status = "No dry-run replay is active.";
            return;
        }

        if (_dryRunEngine.IsPaused)
        {
            _dryRunEngine.Resume();
            P2Status = "Dry-run resumed.";
        }
        else
        {
            _dryRunEngine.Pause();
            P2Status = "Dry-run paused.";
        }
    }

    private void StopDryRun()
    {
        _dryRunEngine?.Stop();
        P2Status = "Dry-run stop requested.";
    }

    private bool IsVerifiedRecordingTarget(RecordingTarget target)
    {
        var client = Clients.FirstOrDefault(candidate =>
            string.Equals(candidate.ClientId, target.ClientId, StringComparison.Ordinal));
        var currentTarget = client?.CreateRecordingTarget();
        return client is not null &&
            client.IsRuntimeReady &&
            currentTarget is not null &&
            currentTarget.WindowHandle == target.WindowHandle &&
            string.Equals(client.ObservedRoleId, target.ClientId, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _dryRunEngine?.Stop();
        _actionRecordingService.Dispose();
    }

    private static string SanitizeFileName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "recorded-action" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length > 80 ? name[..80] : name;
    }

    private bool TryBuildConfig(out P0Config config, out string error)
    {
        config = new P0Config();
        error = string.Empty;

        var processName = ProcessName.Trim();
        if (processName.Length == 0)
        {
            error = "Process name is required.";
            return false;
        }

        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<ClientDefinition>();
        foreach (var client in Clients)
        {
            var clientId = client.ClientId.Trim();
            if (clientId.Length == 0)
            {
                error = "Every registry row must have a non-empty Client ID.";
                return false;
            }

            if (!clientIds.Add(clientId))
            {
                error = $"Duplicate Client ID '{clientId}' is not allowed.";
                return false;
            }

            definitions.Add(client.ToDefinition());
        }

        var roleIdRegex = RoleIdRegex.Trim();
        if (roleIdRegex.Length > 0)
        {
            try
            {
                _ = new Regex(roleIdRegex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                error = $"Role ID regex is invalid: {exception.Message}";
                return false;
            }
        }

        config = new P0Config(
            ProcessName: processName,
            RoleIdRegex: roleIdRegex.Length == 0 ? null : roleIdRegex,
            Clients: definitions);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
