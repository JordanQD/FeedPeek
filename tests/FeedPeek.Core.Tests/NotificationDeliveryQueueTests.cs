using FeedPeek.Core.Models;
using FeedPeek.Core.Notifications;

namespace FeedPeek.Core.Tests;

public sealed class NotificationDeliveryQueueTests
{
    [Fact]
    public void CancellationOnlyAcknowledgesItemsAlreadySubmitted()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        queue.Enqueue(new("first"), [watch]);
        queue.Enqueue(new("second"), [watch]);

        var firstAttempt = queue.GetPending([watch]);
        Assert.Equal(["first", "second"], firstAttempt.Select(item => item.Id));

        // The first item was submitted before cancellation; the second must
        // remain available for the next refresh.
        queue.Acknowledge(firstAttempt[0]);

        Assert.Equal(["second"], queue.GetPending([watch]).Select(item => item.Id));
    }

    [Fact]
    public void SameWatchGenerationKeepsPendingItemsAcrossUnrelatedRevision()
    {
        using var queue = NewQueue();
        var watch = Watch("same-generation");
        queue.Enqueue(new("pending"), [watch]);

        Assert.Equal(["pending"], queue.GetPending([watch]).Select(item => item.Id));
    }

    [Theory]
    [InlineData("scope-generation")]
    [InlineData("account-generation")]
    public void ChangedScopeOrAccountGenerationDropsOldPendingItems(string generation)
    {
        using var queue = NewQueue();
        queue.Enqueue(new("old"), [Watch("old-generation")]);

        Assert.Empty(queue.GetPending([Watch(generation)]));
    }

    [Fact]
    public void RemovingAllWatchesClearsPendingItems()
    {
        using var queue = NewQueue();
        queue.Enqueue(new("pending"), [Watch("generation")]);

        Assert.Empty(queue.GetPending([]));
        Assert.Empty(queue.GetPending([Watch("generation")]));
    }

    [Fact]
    public void OneMatchingWatchKeepsItemWhenAnotherWatchIsGone()
    {
        using var queue = NewQueue();
        var sourceWatch = Watch("source-generation");
        var creatorWatch = Watch("creator-generation", "creator");
        queue.Enqueue(new("shared"), [sourceWatch, creatorWatch]);

        Assert.Equal(["shared"], queue.GetPending([creatorWatch]).Select(item => item.Id));
    }

    [Fact]
    public void CanObserveFailureLeavesItemForALaterAttempt()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        queue.Enqueue(new("temporarily-unavailable"), [watch]);

        Assert.Empty(queue.GetPending([watch], _ => false));
        Assert.Equal(["temporarily-unavailable"], queue.GetPending([watch], _ => true)
            .Select(item => item.Id));
    }

    [Fact]
    public void DuplicateItemIsReturnedOnlyOnce()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        queue.Enqueue(new("same"), [watch]);
        queue.Enqueue(new("same"), [watch]);

        var pending = queue.GetPending([watch]);

        Assert.Single(pending);
        Assert.Equal("same", pending[0].Id);
    }

    [Fact]
    public void LargeBacklogIsDeliveredInBoundedBatchesWithoutLosingOrRepeatingEvents()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        const int count = 2501;
        using (queue.BeginBatch())
            for (var i = 0; i < count; i++) queue.Enqueue(new(i.ToString()), [watch]);
        Assert.Equal(count, queue.Count);
        var submitted = new HashSet<string>();
        while (queue.Count > 0)
        {
            var pending = queue.GetPending([watch]);
            Assert.InRange(pending.Count, 1, NotificationDeliveryQueue<string, DeliveryItem>.DeliveryBatchSize);
            using (queue.BeginBatch())
                foreach (var item in pending)
                {
                    Assert.True(submitted.Add(item.Id));
                    queue.Acknowledge(item);
                }
        }
        Assert.Equal(count, submitted.Count);
        Assert.Empty(queue.GetPending([watch]));
    }

    [Fact]
    public void UnacknowledgedBatchDoesNotStarveTheNextBatchAndIsRetriedLater()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        using (queue.BeginBatch())
            for (var i = 0; i < 300; i++) queue.Enqueue(new(i.ToString()), [watch]);
        var first = queue.GetPending([watch]);
        var next = queue.GetPending([watch]);
        Assert.Equal("256", next[0].Id);
        Assert.Contains(next, item => item.Id == "299");
        Assert.Equal(300, queue.Count);
        using (queue.BeginBatch())
            foreach (var item in first.Concat(next).DistinctBy(item => item.Id)) queue.Acknowledge(item);
        Assert.Empty(queue.GetPending([watch]));
    }

    [Fact]
    public void NewGenerationCanReplaceAnOldPendingEventWithTheSameIdentity()
    {
        using var queue = NewQueue();
        queue.Enqueue(new("same"), [Watch("old")]);
        queue.Enqueue(new("same"), [Watch("new")]);
        Assert.Single(queue.GetPending([Watch("new")]));
    }

    [Fact]
    public void RemoveWhereScansEveryDiskBatchAndKeepsUnmatchedEvents()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        using (queue.BeginBatch())
            for (var i = 0; i < 600; i++) queue.Enqueue(new(i.ToString()), [watch]);
        queue.RemoveWhere(item => int.Parse(item.Id) % 2 == 0);
        Assert.Equal(300, queue.Count);
        var pending = queue.GetPending([watch]);
        Assert.All(pending, item => Assert.Equal(1, int.Parse(item.Id) % 2));
    }

    [Fact]
    public void RepeatedPlanningMergesWatchesAndRetainsEventWhenTheOriginalWatchIsRemoved()
    {
        using var queue = NewQueue();
        var first = Watch("source");
        var second = Watch("creator", "creator");
        queue.Enqueue(new("shared"), [first]);
        queue.Enqueue(new("shared"), [first, second]);
        Assert.Single(queue.GetPending([second]));
    }

    [Fact]
    public void ContinuousNewEventsCannotStarveAnOlderRecoveredEvent()
    {
        using var queue = NewQueue();
        var watch = Watch("generation");
        using (queue.BeginBatch())
        {
            queue.Enqueue(new("recovered"), [watch]);
            for (var i = 0; i < 512; i++) queue.Enqueue(new($"first-{i}"), [watch]);
        }
        var first = queue.GetPending([watch], item => item.Id != "recovered");
        using (queue.BeginBatch())
            foreach (var item in first) queue.Acknowledge(item);
        var deliveredRecovered = false;
        for (var round = 0; round < 5; round++)
        {
            using (queue.BeginBatch())
                for (var i = 0; i < 300; i++) queue.Enqueue(new($"new-{round}-{i}"), [watch]);
            var pending = queue.GetPending([watch]);
            deliveredRecovered |= pending.Any(item => item.Id == "recovered");
            using (queue.BeginBatch())
                foreach (var item in pending) queue.Acknowledge(item);
        }
        Assert.True(deliveredRecovered);
    }

    private static NotificationDeliveryQueue<string, DeliveryItem> NewQueue()
        => new(item => item.Id);

    private static NotificationWatch Watch(string generation, string? creatorKey = null)
        => new(
            new NotificationWatchKey(
                new NotificationFeedKey("provider", "source", "updates", NotificationKind.Updates),
                creatorKey),
            generation);

    private sealed record DeliveryItem(string Id);
}
