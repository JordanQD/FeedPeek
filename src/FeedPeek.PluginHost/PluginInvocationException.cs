namespace FeedPeek.PluginHost;

public sealed class PluginInvocationException : Exception
{
    public PluginInvocationException(
        string code,
        string message,
        bool retryable = false,
        IReadOnlyDictionary<string, string>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
        Context = context;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public IReadOnlyDictionary<string, string>? Context { get; }
}
