namespace GameTranslatorOverlay.Core.Vision;

public sealed class FullScanSchedule(TimeSpan interval, TimeSpan settleDelay)
{
    public static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromMilliseconds(120);

    private TimeSpan _lastFullScanAt;
    private bool _rescanRequested;
    private long? _pipelineVersion;
    private bool _pipelineReady;
    private long? _satisfiedVersion;
    private TimeSpan _pipelineChangedAt;

    public FullScanSchedule(TimeSpan interval) : this(interval, DefaultSettleDelay)
    {
    }

    public bool RescanRequested => _rescanRequested;

    public bool IsDue(TimeSpan now) => interval > TimeSpan.Zero && now - _lastFullScanAt >= interval;

    public bool IsDue(TimeSpan now, long pipelineVersion, bool pipelineReady)
    {
        ObservePipeline(now, pipelineVersion);
        _pipelineReady = pipelineReady;
        return IsDue(now) || (_rescanRequested && pipelineReady && now - _pipelineChangedAt >= settleDelay);
    }

    public void ObservePipeline(TimeSpan now, long pipelineVersion)
    {
        if (_pipelineVersion == pipelineVersion) return;
        _pipelineVersion = pipelineVersion;
        _pipelineReady = false;
        _pipelineChangedAt = now;
    }

    public bool RequestAfterPipelineChange(TimeSpan now, long pipelineVersion)
    {
        ObservePipeline(now, pipelineVersion);
        if (_satisfiedVersion == pipelineVersion) return false;
        _rescanRequested = true;
        return true;
    }

    public void OnScanStarted(TimeSpan now, bool fullFrame)
    {
        if (!fullFrame) return;
        _lastFullScanAt = now;
        if (!_pipelineReady) return;
        _rescanRequested = false;
        _satisfiedVersion = _pipelineVersion;
    }
}
