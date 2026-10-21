using System;

namespace ReferenceRAG.DesktopHost;

internal sealed class StartupWindowCoordinator(bool startMinimized, Action<nint> hide, Action<nint> reveal, Action<nint>? prepare = null)
{
    private readonly object _sync = new();
    private nint _handle;
    private bool _ready;
    private bool _showRequested;

    public void SetWindowHandle(nint handle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(handle);
        lock (_sync)
        {
            _handle = handle;
            if (startMinimized && !_showRequested) prepare?.Invoke(handle);
            if (_ready) ApplyReadyState();
            else if (_showRequested) reveal(handle);
        }
    }

    public void SignalReady()
    {
        lock (_sync)
        {
            if (_ready) return;
            _ready = true;
            if (_handle != 0) ApplyReadyState();
        }
    }

    public void RequestShow()
    {
        lock (_sync)
        {
            _showRequested = true;
            if (_handle != 0) reveal(_handle);
        }
    }

    public bool SignalTimeout()
    {
        lock (_sync)
        {
            if (_ready) return false;
            _ready = true;
            // Missing readiness must not leave an invisible window indefinitely.
            _showRequested = true;
            if (_handle != 0) reveal(_handle);
            return true;
        }
    }

    private void ApplyReadyState()
    {
        if (startMinimized && !_showRequested) hide(_handle);
        else reveal(_handle);
    }
}
