using GameTranslatorOverlay.Core.Ocr;

namespace GameTranslatorOverlay.Core.Windows;

public readonly record struct WindowPlacement(int ProcessId, RectPx Bounds);

public enum GameVisibilityState
{
    Visible,
    Covered,
}

public static class GameVisibility
{
    public const int CoveredPercent = 15;

    public static GameVisibilityState Evaluate(WindowPlacement game, WindowPlacement? foreground)
    {
        if (foreground is not { } front) return GameVisibilityState.Visible;
        if (front.ProcessId == game.ProcessId) return GameVisibilityState.Visible;
        if (game.Bounds.IsEmpty || front.Bounds.IsEmpty) return GameVisibilityState.Visible;

        var overlap = game.Bounds.Intersect(front.Bounds);
        if (overlap.IsEmpty) return GameVisibilityState.Visible;

        var overlapArea = (long)overlap.Width * overlap.Height;
        var gameArea = (long)game.Bounds.Width * game.Bounds.Height;
        return overlapArea * 100 >= gameArea * CoveredPercent ? GameVisibilityState.Covered : GameVisibilityState.Visible;
    }
}

public enum GamePauseTransition
{
    None,
    Paused,
    Resumed,
}

public sealed class GamePauseGate
{
    public const int CoveredCyclesToPause = 2;

    private int _coveredCycles;

    public bool IsPaused { get; private set; }

    public GamePauseTransition Observe(GameVisibilityState state)
    {
        if (state == GameVisibilityState.Visible)
        {
            _coveredCycles = 0;
            if (!IsPaused) return GamePauseTransition.None;
            IsPaused = false;
            return GamePauseTransition.Resumed;
        }

        _coveredCycles++;
        if (IsPaused || _coveredCycles < CoveredCyclesToPause) return GamePauseTransition.None;
        IsPaused = true;
        return GamePauseTransition.Paused;
    }

    public void Reset()
    {
        _coveredCycles = 0;
        IsPaused = false;
    }
}
