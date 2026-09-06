using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutoGhost.ActionModel;
using AutoGhost.P0Probe;

namespace AutoGhost.App.ViewModels;

public sealed class ClientRowViewModel : INotifyPropertyChanged
{
    private string _clientId;
    private bool _isEnabled;
    private string _runtimeState = "OFFLINE";
    private string _observedRoleId = "—";
    private string _processId = "—";
    private string _windowHandle = "—";
    private string _sessionId = "—";
    private string _reason = "Not scanned yet.";
    private string _specialItemsText;
    private nint _runtimeWindowHandle;
    private int _runtimeClientWidth;
    private int _runtimeClientHeight;

    public ClientRowViewModel(
        string clientId,
        bool isEnabled,
        IReadOnlyList<string>? specialItems = null)
    {
        _clientId = clientId;
        _isEnabled = isEnabled;
        _specialItemsText = string.Join(Environment.NewLine, specialItems ?? Array.Empty<string>());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ClientId
    {
        get => _clientId;
        set
        {
            var normalized = value.Trim();
            if (normalized.Length == 0 || string.Equals(_clientId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _clientId = normalized;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Operator-provided Special Items, one item per line. An empty value means
    /// the comparison remains pending and cannot raise an alert.
    /// </summary>
    public string SpecialItemsText
    {
        get => _specialItemsText;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_specialItemsText, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _specialItemsText = normalized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConfiguredSpecialItems));
            OnPropertyChanged(nameof(SpecialItemsSummary));
        }
    }

    public IReadOnlyList<string> ConfiguredSpecialItems => ParseSpecialItems(SpecialItemsText);

    public string SpecialItemsSummary => ConfiguredSpecialItems.Count == 0
        ? "Not configured"
        : string.Join(", ", ConfiguredSpecialItems);

    public string RuntimeState
    {
        get => _runtimeState;
        private set
        {
            if (string.Equals(_runtimeState, value, StringComparison.Ordinal))
            {
                return;
            }

            _runtimeState = value;
            OnPropertyChanged();
        }
    }

    public string ObservedRoleId
    {
        get => _observedRoleId;
        private set
        {
            if (string.Equals(_observedRoleId, value, StringComparison.Ordinal))
            {
                return;
            }

            _observedRoleId = value;
            OnPropertyChanged();
        }
    }

    public string ProcessId
    {
        get => _processId;
        private set
        {
            if (string.Equals(_processId, value, StringComparison.Ordinal))
            {
                return;
            }

            _processId = value;
            OnPropertyChanged();
        }
    }

    public string WindowHandle
    {
        get => _windowHandle;
        private set
        {
            if (string.Equals(_windowHandle, value, StringComparison.Ordinal))
            {
                return;
            }

            _windowHandle = value;
            OnPropertyChanged();
        }
    }

    public string SessionId
    {
        get => _sessionId;
        private set
        {
            if (string.Equals(_sessionId, value, StringComparison.Ordinal))
            {
                return;
            }

            _sessionId = value;
            OnPropertyChanged();
        }
    }

    public string Reason
    {
        get => _reason;
        private set
        {
            if (string.Equals(_reason, value, StringComparison.Ordinal))
            {
                return;
            }

            _reason = value;
            OnPropertyChanged();
        }
    }

    public bool IsRuntimeReady => RuntimeState == "READY" &&
        _runtimeWindowHandle != nint.Zero &&
        string.Equals(ObservedRoleId, ClientId, StringComparison.Ordinal);

    public RecordingTarget? CreateRecordingTarget()
    {
        if (!IsRuntimeReady || _runtimeClientWidth <= 0 || _runtimeClientHeight <= 0)
        {
            return null;
        }

        return new RecordingTarget(
            ClientId,
            _runtimeWindowHandle,
            ScreenLeft: 0,
            ScreenTop: 0,
            _runtimeClientWidth,
            _runtimeClientHeight);
    }

    public ClientDefinition ToDefinition() => new(ClientId.Trim(), IsEnabled, ConfiguredSpecialItems);

    public static IReadOnlyList<string> ParseSpecialItems(string value) => value
        .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static item => item.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void ClearRuntime(string reason = "No verified runtime window matched this Client ID.")
    {
        RuntimeState = "OFFLINE";
        ObservedRoleId = "—";
        ProcessId = "—";
        WindowHandle = "—";
        SessionId = "—";
        Reason = reason;
        _runtimeWindowHandle = nint.Zero;
        _runtimeClientWidth = 0;
        _runtimeClientHeight = 0;
        OnPropertyChanged(nameof(IsRuntimeReady));
    }

    public void ApplyRuntime(WindowObservation observation, RuntimeBinding? binding)
    {
        RuntimeState = binding?.State == BindingState.Ready
            ? "READY"
            : observation.IdentityState switch
            {
                IdentityState.RoleIdUnavailable => "IDENTIFYING",
                IdentityState.Mismatch or IdentityState.DuplicateIdentity or IdentityState.Error => "ERROR",
                _ => "OFFLINE"
            };
        ObservedRoleId = observation.RoleId ?? "—";
        ProcessId = observation.ProcessId.ToString();
        WindowHandle = $"0x{observation.WindowHandle.ToInt64():X}";
        SessionId = binding?.SessionId.ToString() ?? "—";
        Reason = binding?.Reason ?? observation.IdentityReason;
        _runtimeWindowHandle = observation.WindowHandle;
        _runtimeClientWidth = observation.ClientRect is { } clientRect
            ? Math.Max(0, clientRect.Right - clientRect.Left)
            : 0;
        _runtimeClientHeight = observation.ClientRect is { } clientRectHeight
            ? Math.Max(0, clientRectHeight.Bottom - clientRectHeight.Top)
            : 0;
        OnPropertyChanged(nameof(IsRuntimeReady));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
