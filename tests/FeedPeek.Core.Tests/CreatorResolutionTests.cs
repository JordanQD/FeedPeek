using FeedPeek.Core.Models;
using FeedPeek.PluginContracts.Dtos;
using FeedPeek.PluginContracts.Protocol;
using FeedPeek.PluginHost;
using System.Text.Json;

namespace FeedPeek.Core.Tests;

public sealed class CreatorResolutionTests
{
    [Theory]
    [InlineData("followed", CreatorFollowState.Followed)]
    [InlineData("notFollowed", CreatorFollowState.NotFollowed)]
    [InlineData("unknown", CreatorFollowState.Unknown)]
    public async Task ResolveMapsFollowStateAndKeepsSubscriptionIdentity(
        string followState,
        CreatorFollowState expectedState)
    {
        var invoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorResolveResult(Creator("creator-01"), followState)));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));
        var subscription = CreateSubscription();

        var result = await service.ResolveAsync(subscription, "  https://example.test/creators/01  ");

        Assert.Equal(expectedState, result.FollowState);
        Assert.NotNull(result.Creator);
        Assert.Equal("demo.plugin", result.Creator.ProviderId);
        Assert.Equal("source-01", result.Creator.SourceId);
        Assert.Equal("creator-01", result.Creator.CreatorKey);
        Assert.Equal(["video", "live"], result.Creator.SupportedNotificationKinds);
        Assert.Equal(PluginMethods.CreatorResolve, Assert.Single(invoker.Methods));
        Assert.Equal("https://example.test/creators/01", invoker.ResolvePayload!.Input);
    }

    [Fact]
    public async Task ResolveCarriesTheFullSubscriptionContext()
    {
        var invoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorResolveResult(Creator("creator-01"), "unknown")));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));
        var subscription = CreateSubscription();

        await service.ResolveAsync(subscription, "creator-01");

        var context = invoker.ResolvePayload!.Context;
        Assert.Equal(subscription.SubscriptionId, context.SubscriptionId);
        Assert.Equal(subscription.ProviderId, context.ProviderId);
        Assert.Equal(subscription.SourceId, context.SourceId);
        Assert.Equal(subscription.FeedId, context.FeedId);
        Assert.Equal(subscription.CredentialProfileId, context.CredentialProfileId);
        Assert.Equal(subscription.Options, context.Options);
    }

    [Fact]
    public async Task ResolveRejectsAPluginSubscriptionFromAnotherProvider()
    {
        var invoker = new RecordingInvoker(_ => throw new InvalidOperationException("must not invoke"));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveAsync(CreateSubscription() with { ProviderId = "other.plugin" }, "creator-01"));

        Assert.Contains("provider", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(invoker.Methods);
    }

    [Theory]
    [MemberData(nameof(InvalidCreatorResults))]
    public async Task ResolveRejectsInvalidCreatorData(PluginCreatorResolveResult result)
    {
        var invoker = new RecordingInvoker(request => Success(request.RequestId, result));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        var error = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            service.ResolveAsync(CreateSubscription(), "creator-01"));

        Assert.Equal("PLUGIN_RESULT_INVALID", error.Code);
    }

    [Fact]
    public async Task ResolveRejectsInvalidInputBeforeInvokingPlugin()
    {
        var invoker = new RecordingInvoker(_ => throw new InvalidOperationException("must not invoke"));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveAsync(CreateSubscription(), " "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ResolveAsync(CreateSubscription(), new string('x', 2049)));
        Assert.Empty(invoker.Methods);
    }

    [Fact]
    public async Task ResolveRejectsAnInvalidFollowStateAndAnEmptyResult()
    {
        var invalidStateInvoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorResolveResult(Creator("creator-01"), "maybe")));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invalidStateInvoker));
        var invalidState = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            service.ResolveAsync(CreateSubscription(), "creator-01"));
        Assert.Equal("PLUGIN_RESULT_INVALID", invalidState.Code);

        var emptyInvoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorResolveResult(null!, "unknown")));
        service = new PluginCreatorService("demo.plugin", new PluginClient(emptyInvoker));
        var empty = await Assert.ThrowsAsync<PluginInvocationException>(() =>
            service.ResolveAsync(CreateSubscription(), "creator-01"));
        Assert.Equal("PLUGIN_RESULT_INVALID", empty.Code);
    }

    [Fact]
    public async Task FollowingPageTreatsOmittedCompletenessAsUnknown()
    {
        var invoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorListResult([Creator("creator-01")])));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        var page = await service.GetFollowingPageAsync(CreateSubscription());

        Assert.False(page.IsComplete);
        Assert.Equal("demo.plugin", Assert.Single(page.Creators).ProviderId);
    }

    [Fact]
    public async Task FollowingPagePreservesExplicitCompleteness()
    {
        var invoker = new RecordingInvoker(request => Success(
            request.RequestId,
            new PluginCreatorListResult([Creator("creator-01")], IsComplete: true)));
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        var page = await service.GetFollowingPageAsync(CreateSubscription());

        Assert.True(page.IsComplete);
    }

    [Theory]
    [InlineData("https://example.test/creator/01", true)]
    [InlineData("example.test/creator/01", true)]
    [InlineData("https://sub.example.test/creator/01", false)]
    [InlineData("https://evil.example.test/creator/01", false)]
    [InlineData("https://user:password@example.test/creator/01", false)]
    [InlineData("https://example.test:443/creator/01", false)]
    [InlineData("https://example.test:8443/creator/01", false)]
    public void WebInputRoutingRequiresAnExactHostWithoutUserInfoOrPort(string input, bool expected)
    {
        var matches = CreatorInputRouting.TryGetWebUri(input, out var uri)
            && CreatorInputRouting.MatchesHost(uri!, ["example.test"]);

        Assert.Equal(expected, matches);
    }

    [Fact]
    public void FollowingPageMarksAnExactProfileAsFollowed()
    {
        var creator = CoreCreator("creator-01", "https://example.test/creator/01");
        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([creator], IsComplete: false),
            "https://example.test/creator/01");

        Assert.Equal(CreatorFollowState.Followed, result.FollowState);
        Assert.Equal(creator, result.Creator);
    }

    [Fact]
    public void OpaqueCreatorIdIsMatchedExactlyEvenWhenItContainsADot()
    {
        var creator = CoreCreator("creator.example", "https://example.test/creator/01");
        var result = CreatorInputRouting.FromFollowingPage(new FollowingCreatorsPage([creator], false), "creator.example");
        Assert.Equal(CreatorFollowState.Followed, result.FollowState);
        Assert.Equal(creator, result.Creator);
    }

    [Fact]
    public void IncompleteFollowingPageLeavesAnUnmatchedInputUnknown()
    {
        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([CoreCreator("creator-01", "https://example.test/creator/01")], IsComplete: false),
            "creator-02");

        Assert.Null(result.Creator);
        Assert.Equal(CreatorFollowState.Unknown, result.FollowState);
    }

    [Fact]
    public void CompleteFollowingPageCanDisproveAnUnmatchedCreatorId()
    {
        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([CoreCreator("creator-01", "https://example.test/creator/01")], IsComplete: true),
            "creator-02");

        Assert.Null(result.Creator);
        Assert.Equal(CreatorFollowState.NotFollowed, result.FollowState);
    }

    [Fact]
    public void UnknownUrlPathRemainsUnknownInsteadOfGuessingAnId()
    {
        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([CoreCreator("creator-01", "https://example.test/creator/01")], IsComplete: true),
            "https://example.test/creator/02");

        Assert.Null(result.Creator);
        Assert.Equal(CreatorFollowState.Unknown, result.FollowState);
    }

    [Theory]
    [InlineData("https://user:password@example.test/creator/01")]
    [InlineData("https://example.test:443/creator/01")]
    [InlineData("ftp://example.test/creator/01")]
    [InlineData("")]
    public void InvalidWebInputsRemainUnknownWithACompleteFollowingList(string input)
    {
        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([CoreCreator("creator-01", "https://example.test/creator/01")], IsComplete: true),
            input);

        Assert.Null(result.Creator);
        Assert.Equal(CreatorFollowState.Unknown, result.FollowState);
    }

    [Fact]
    public void FollowingMatchPreservesProviderAndSourceIdentity()
    {
        var creator = CoreCreator("creator-01", "https://example.test/creator/01") with
        {
            ProviderId = "other.plugin",
            SourceId = "source-02"
        };

        var result = CreatorInputRouting.FromFollowingPage(
            new FollowingCreatorsPage([creator], IsComplete: true),
            "creator-01");

        Assert.Equal(CreatorFollowState.Followed, result.FollowState);
        Assert.Equal("other.plugin", result.Creator!.ProviderId);
        Assert.Equal("source-02", result.Creator.SourceId);
    }

    [Fact]
    public async Task ResolvePropagatesCancellationToInvoker()
    {
        using var cancellation = new CancellationTokenSource();
        var invoker = new RecordingInvoker(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return string.Empty;
        });
        var service = new PluginCreatorService("demo.plugin", new PluginClient(invoker));

        var pending = service.ResolveAsync(CreateSubscription(), "creator-01", cancellation.Token);
        await invoker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(invoker.ObservedCancellation);
    }

    public static IEnumerable<object[]> InvalidCreatorResults()
    {
        yield return [new PluginCreatorResolveResult(Creator(""), "unknown")];
        yield return [new PluginCreatorResolveResult(Creator("creator-01") with { DisplayName = "" }, "unknown")];
        yield return [new PluginCreatorResolveResult(Creator("creator-01") with { ProfileUrl = "file:///private" }, "unknown")];
        yield return [new PluginCreatorResolveResult(Creator("creator-01") with { AvatarUrl = "javascript:alert(1)" }, "unknown")];
        yield return [new PluginCreatorResolveResult(Creator("creator-01") with { SupportedNotificationKinds = ["comment"] }, "unknown")];
        yield return [new PluginCreatorResolveResult(Creator("creator-01") with { SupportedNotificationKinds = ["video", "video"] }, "unknown")];
    }

    private static PluginCreatorDescriptor Creator(string key)
        => new(key, "Creator", "https://example.test/profile/01", "https://example.test/avatar/01", ["video", "live"], null);

    private static CreatorDescriptor CoreCreator(string key, string profileUrl)
        => new("demo.plugin", "source-01", key, "Creator", profileUrl, "https://example.test/avatar/01", ["video", "live"]);

    private static FeedSubscription CreateSubscription()
        => new(
            "subscription-01",
            "demo.plugin",
            "source-01",
            "updates",
            "Demo source",
            null,
            "credential-01",
            true,
            TimeSpan.FromMinutes(15),
            new Dictionary<string, string> { ["mode"] = "all" });

    private static string Success<T>(string requestId, T result)
        => JsonSerializer.Serialize(
            PluginResponseEnvelope<T>.Succeeded(requestId, result),
            PluginJson.SerializerOptions);

    private sealed class RecordingInvoker : IPluginRequestInvoker
    {
        private readonly Func<PluginRequestEnvelope<JsonElement>, CancellationToken, Task<string>> _responseFactory;

        public RecordingInvoker(Func<PluginRequestEnvelope<JsonElement>, string> responseFactory)
            : this((request, _) => Task.FromResult(responseFactory(request))) { }

        public RecordingInvoker(Func<PluginRequestEnvelope<JsonElement>, CancellationToken, Task<string>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<string> Methods { get; } = [];
        public PluginCreatorResolvePayload? ResolvePayload { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        public async Task<string> InvokeAsync(
            string pluginId,
            string requestJson,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = JsonSerializer.Deserialize<PluginRequestEnvelope<JsonElement>>(
                requestJson,
                PluginJson.SerializerOptions)!;
            Assert.Equal(pluginId, request.PluginId);
            Methods.Add(request.Method);
            if (request.Method == PluginMethods.CreatorResolve)
            {
                ResolvePayload = request.Payload.Deserialize<PluginCreatorResolvePayload>(PluginJson.SerializerOptions);
            }

            Started.TrySetResult(true);
            try
            {
                return await _responseFactory(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservedCancellation = true;
                throw;
            }
        }
    }
}
