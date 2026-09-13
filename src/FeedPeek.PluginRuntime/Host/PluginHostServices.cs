namespace FeedPeek.PluginRuntime.Host;

public sealed record PluginHostServices(
    IPluginHttpBridge Http,
    IPluginCredentialStore Credentials,
    IPluginLogSink Log,
    IPluginInvocationAuthorizer InvocationAuthorizer,
    IPluginCookieProvider? Cookies = null)
{
    /// <summary>
    /// Cookie source for the synchronous <c>Host.session.getCookieHeader</c> dialect API. Hosts that
    /// never expose raw cookies can leave it unset.
    /// </summary>
    public IPluginCookieProvider CookieProvider => Cookies ?? NullPluginCookieProvider.Instance;
}
