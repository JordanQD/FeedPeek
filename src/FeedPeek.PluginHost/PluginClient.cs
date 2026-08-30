using FeedPeek.PluginContracts.Protocol;
using System.Text.Json;

namespace FeedPeek.PluginHost;

public sealed class PluginClient(IPluginRequestInvoker invoker)
{
    private readonly IPluginRequestInvoker _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));

    public async Task<TResult> InvokeAsync<TPayload, TResult>(
        string pluginId,
        string method,
        TPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!PluginMethods.All.Contains(method))
            throw new ArgumentOutOfRangeException(nameof(method), method, "The plugin method is not part of protocol v1.");

        var requestId = Guid.NewGuid().ToString("N");
        var request = new PluginRequestEnvelope<TPayload>(
            requestId,
            PluginProtocolVersion.Current,
            pluginId,
            method,
            payload);
        var requestJson = JsonSerializer.Serialize(request, PluginJson.SerializerOptions);
        var responseJson = await _invoker.InvokeAsync(pluginId, requestJson, cancellationToken)
            .ConfigureAwait(false);

        PluginResponseEnvelope<TResult>? response;
        try
        {
            response = JsonSerializer.Deserialize<PluginResponseEnvelope<TResult>>(
                responseJson,
                PluginJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new PluginInvocationException(
                "PLUGIN_RESPONSE_INVALID",
                "The plugin returned invalid JSON.",
                innerException: exception);
        }

        if (response is null || !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            throw new PluginInvocationException("PLUGIN_RESPONSE_MISMATCH", "The plugin response does not match the request.");
        if (!response.Success)
        {
            var error = response.Error
                ?? new PluginError("PLUGIN_ERROR_INVALID", "The plugin returned an incomplete error.", false, null);
            throw new PluginInvocationException(error.Code, error.Message, error.Retryable, error.Context);
        }
        if (response.Error is not null || response.Result is null)
            throw new PluginInvocationException("PLUGIN_RESPONSE_INVALID", "The plugin returned an incomplete success response.");
        return response.Result;
    }
}
