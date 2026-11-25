using Hi3Helper.Plugin.AzurPromilia.GameData;

internal static class GameReleaseRequestTests
{
    internal static async Task RunAsync()
    {
        var plan = new GameInstallPlan("Fixture", new Version(1, 2, 3), new string('0', 64), new Uri("https://example.invalid/"), [], []);
        var result = new TaskCompletionSource<GameInstallPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        CancellationToken transferToken = default;
        using var request = new GameReleaseRequest(token =>
        {
            attempts++;
            transferToken = token;
            return result.Task;
        });
        using var first = new CancellationTokenSource();
        Task<GameInstallPlan> expired = request.GetAsync(first.Token);
        first.Cancel();
        try { await expired; throw new InvalidOperationException("The initialization wait was not canceled."); }
        catch (OperationCanceledException) when (first.IsCancellationRequested) { }

        Task<GameInstallPlan> retried = request.GetAsync(CancellationToken.None);
        Require(attempts == 1 && !transferToken.IsCancellationRequested,
            "A host initialization timeout must not discard or restart the publisher configuration transfer.");
        result.SetResult(plan);
        Require(ReferenceEquals(await retried.WaitAsync(TimeSpan.FromSeconds(2)), plan),
            "A later initialization attempt must receive the original transfer result.");
        await request.GetAsync(CancellationToken.None);
        Require(attempts == 2, "A completed initialization must allow a subsequent publisher refresh.");

        var lifetime = new GameReleaseRequest(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return plan;
        });
        Task<GameInstallPlan> pending = lifetime.GetAsync(CancellationToken.None);
        lifetime.Dispose();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new InvalidOperationException("A disposed request kept transferring."); }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
