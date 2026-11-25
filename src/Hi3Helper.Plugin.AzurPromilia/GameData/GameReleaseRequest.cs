namespace Hi3Helper.Plugin.AzurPromilia.GameData;

internal sealed class GameReleaseRequest(Func<CancellationToken, Task<GameInstallPlan>> load) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<GameInstallPlan>? _pending;
    private bool _disposed;

    // Collapse retries initialization with short time limits; keep one publisher transfer across those waits.
    internal async Task<GameInstallPlan> GetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<GameInstallPlan> pending;
        using (_gate.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            pending = _pending ??= load(_lifetime.Token);
        }

        try
        {
            GameInstallPlan plan = await pending.WaitAsync(token).ConfigureAwait(false);
            using (_gate.EnterScope())
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
            return plan;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            using (_gate.EnterScope())
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            throw;
        }
    }

    public void Dispose()
    {
        using (_gate.EnterScope())
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = null;
        }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
