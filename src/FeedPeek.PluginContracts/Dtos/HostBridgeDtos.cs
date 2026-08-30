namespace FeedPeek.PluginContracts.Dtos;

public sealed record HostHttpRequest(
    string Url,
    string Method,
    IReadOnlyDictionary<string, string>? Headers,
    string? Body,
    string? BodyEncoding,
    int? TimeoutMs,
    int? MaxResponseBytes);

public sealed record HostHttpResponse(
    int Status,
    string FinalUrl,
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    string BodyEncoding);

public sealed record CredentialRequest(
    string CredentialProfileId,
    string? Name,
    string? Value);

public sealed record CredentialStatus(bool Exists);
