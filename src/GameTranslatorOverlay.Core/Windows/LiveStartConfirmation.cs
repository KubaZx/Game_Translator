namespace GameTranslatorOverlay.Core.Windows;

public enum LiveStartDecision
{
    Start,
    AskForConfirmation,
}

public sealed class LiveStartConfirmation
{
    public static readonly TimeSpan ConfirmationWindow = TimeSpan.FromSeconds(6);

    public static string Message =>
        $"Nowe okno — wciśnij skrót jeszcze raz w ciągu {(int)ConfirmationWindow.TotalSeconds} s, żeby zacząć tłumaczyć.";

    private nint _pendingHandle;
    private DateTimeOffset _pendingSince;

    public LiveStartDecision Evaluate(nint windowHandle, LiveTargetReason reason, bool isKnownProcess, DateTimeOffset now)
    {
        if (reason is LiveTargetReason.RememberedProcess or LiveTargetReason.ProfileProcess || isKnownProcess)
        {
            Reset();
            return LiveStartDecision.Start;
        }

        if (_pendingHandle != 0 && _pendingHandle == windowHandle
            && now >= _pendingSince && now - _pendingSince <= ConfirmationWindow)
        {
            Reset();
            return LiveStartDecision.Start;
        }

        _pendingHandle = windowHandle;
        _pendingSince = now;
        return LiveStartDecision.AskForConfirmation;
    }

    public void Reset()
    {
        _pendingHandle = 0;
        _pendingSince = default;
    }
}
