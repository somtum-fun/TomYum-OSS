namespace TomYum.Performance;

/// <summary>
/// Placeholder implementation of <see cref="IPerformanceCalculator"/> that always returns 0 pp.
/// Replace with the real calculator once the osu! ruleset packages are integrated (ADR-0005).
/// </summary>
public sealed class StubPerformanceCalculator : IPerformanceCalculator
{
    /// <inheritdoc />
    public Task<double> CalculateAsync(PerformanceRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(0.0);
}
