namespace FeedPeek.PluginRuntime.Execution;

public sealed record PluginRuntimeOptions
{
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan InvocationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PromiseTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan HostTaskDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxStatements { get; init; } = 250_000;
    public long MemoryLimitBytes { get; init; } = 64L * 1024 * 1024;
    public int MaxRecursionDepth { get; init; } = 128;
    public int MaxScriptBytes { get; init; } = 1024 * 1024;
    public int MaxRequestJsonBytes { get; init; } = 1024 * 1024;
    public int MaxResponseJsonBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxConcurrentInvocations { get; init; } = 1;

    internal void Validate()
    {
        if (InitializationTimeout <= TimeSpan.Zero || InvocationTimeout <= TimeSpan.Zero
            || PromiseTimeout <= TimeSpan.Zero || HostTaskDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InvocationTimeout), "Runtime timeouts must be positive.");
        if (MaxStatements <= 0 || MemoryLimitBytes <= 0 || MaxRecursionDepth <= 0
            || MaxScriptBytes <= 0 || MaxRequestJsonBytes <= 0 || MaxResponseJsonBytes <= 0
            || MaxConcurrentInvocations != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStatements), "Runtime limits must be positive and a Jint realm must use one serialized invocation lane.");
        }
    }
}
