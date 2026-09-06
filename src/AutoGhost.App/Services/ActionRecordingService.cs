using AutoGhost.ActionModel;

namespace AutoGhost.App.Services;

public sealed class ActionRecordingService : IDisposable
{
    private readonly WindowsInputRecorder _recorder;
    private bool _disposed;

    public ActionRecordingService(Func<RecordingTarget, bool> identityValidator)
    {
        _recorder = new WindowsInputRecorder(identityValidator);
        _recorder.StepRecorded += (_, _) => StepCountChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? StepCountChanged;

    public bool IsRecording => _recorder.IsRecording;
    public int StepCount => _recorder.StepCount;

    public void Start(RecordingTarget target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder.Start(target);
    }

    public ActionDefinition Stop(string actionName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _recorder.Stop(actionName);
    }

    public void Cancel() => _recorder.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _recorder.Dispose();
        _disposed = true;
    }
}
