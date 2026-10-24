using EQLogParser;
using System.Collections.Concurrent;
using System.Net;
using static EQLogParserTest.WebhookTestHandler;

namespace EQLogParserTest
{
  [TestClass]
  public class WebhookDispatchQueueTest
  {
    private const string Url = "https://discord.com/api/webhooks/123/test";

    [TestMethod]
    [DataRow(5)]
    [DataRow(10)]
    [DataRow(15)]
    [DataRow(20)]
    [DataRow(25)]
    [DataRow(30)]
    public async Task CollectionDeadlineStartsAtFirstMatchAndDoesNotSlide(int seconds)
    {
      var clock = new WebhookTestClock();
      var delivered = new ConcurrentQueue<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        delivered.Enqueue(await ContentAsync(request, token));
        return Response(HttpStatusCode.OK);
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client), clock.Now, clock.DelayAsync);
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      var first = queue.EnqueueAsync(source, Url, "first", seconds);
      Assert.AreEqual(1, queue.Status.Pending);
      Assert.AreEqual(1, queue.Status.Aggregating);
      await clock.AdvanceAsync(seconds - 1);
      var second = queue.EnqueueAsync(source, Url, "second", seconds);
      Assert.IsEmpty(delivered);
      await clock.AdvanceAsync(1);
      await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
      CollectionAssert.AreEqual(new[] { "first\nsecond" }, delivered.ToArray());
      Assert.AreEqual(0, queue.Status.Pending);
      var next = queue.EnqueueAsync(source, Url, "next window", seconds);
      await clock.AdvanceAsync(seconds);
      await next.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.HasCount(2, delivered);
    }

    [TestMethod]
    public async Task NoAggregationSendsEveryIdenticalLineWithDistinctIdentity()
    {
      var payloads = new List<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        Assert.IsNotNull(request.Content);
        payloads.Add(await request.Content.ReadAsStringAsync(token));
        return Response(HttpStatusCode.OK);
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      await queue.EnqueueAsync(source, Url, "Item | Item", 0);
      await queue.EnqueueAsync(source, Url, "Item | Item", 0);
      Assert.HasCount(2, payloads);
      Assert.AreNotEqual(payloads[0], payloads[1]);
      Assert.AreEqual(0, queue.Status.Pending);
    }

    [TestMethod]
    public async Task DisableClearsOnlyItsCharacterAndPreventsStaleOrReenabledLeasesFromSending()
    {
      var clock = new WebhookTestClock();
      var delivered = new ConcurrentQueue<string>();
      using var handler = new WebhookTestHandler(async (request, token) =>
      {
        delivered.Enqueue(await ContentAsync(request, token));
        return Response(HttpStatusCode.OK);
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client), clock.Now, clock.DelayAsync);
      var first = queue.UpdateSources("owner1", "char1", [("loot", "Loot")])["loot"];
      var other = queue.UpdateSources("owner2", "char2", [("loot", "Loot")])["loot"];
      var canceled = queue.EnqueueAsync(first, Url, "discard", 10);
      var retained = queue.EnqueueAsync(other, Url, "keep", 10);
      queue.SetEnabled("char1", "loot", false);
      Assert.AreEqual(1, queue.Status.Pending);
      await canceled;
      await queue.EnqueueAsync(first, Url, "stale", 0);
      Assert.IsEmpty(queue.UpdateSources("owner1", "char1", [("loot", "Loot")]));
      queue.SetEnabled("char1", "loot", true);
      var fresh = queue.UpdateSources("owner1", "char1", [("loot", "Loot")])["loot"];
      await queue.EnqueueAsync(first, Url, "still stale", 0);
      await queue.EnqueueAsync(fresh, Url, "fresh", 0);
      await clock.AdvanceAsync(10);
      await retained.WaitAsync(TimeSpan.FromSeconds(5));
      CollectionAssert.AreEqual(new[] { "fresh", "keep" }, delivered.ToArray());
    }

    [TestMethod]
    public async Task DisableCancelsRetryWithoutReportingAnErrorOrResending()
    {
      var clock = new WebhookTestClock();
      var attempts = 0;
      using var handler = new WebhookTestHandler((_, _) =>
      {
        attempts++;
        return Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{\"retry_after\":60}"));
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client, clock.Now, clock.DelayAsync), clock.Now, clock.DelayAsync);
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      var sending = queue.EnqueueAsync(source, Url, "retry me", 0);
      await WebhookTestClock.UntilAsync(() => queue.Status.Retrying == 1);
      queue.SetEnabled("char", "loot", false);
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      await clock.AdvanceAsync(60);
      Assert.AreEqual(1, attempts);
      Assert.AreEqual(0, queue.Status.Pending);
      Assert.IsEmpty(queue.Status.Errors);
    }

    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(404)]
    public async Task PermanentFailureIsSkippedFlaggedAndClearedByLaterSuccess(int status)
    {
      var calls = 0;
      using var handler = new WebhookTestHandler((_, _) =>
        Task.FromResult(Response(++calls == 1 ? (HttpStatusCode)status : HttpStatusCode.OK)));
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot trigger")])["loot"];
      await queue.EnqueueAsync(source, Url, "failed", 0);
      Assert.AreEqual(0, queue.Status.Pending);
      Assert.HasCount(1, queue.Status.Errors);
      Assert.AreEqual(source, queue.Status.Errors[0].Source);
      Assert.IsNotNull(queue.ErrorFor(Url));
      Assert.AreEqual(1, calls);
      await queue.EnqueueAsync(source, Url + "-fixed", "works", 0);
      Assert.IsEmpty(queue.Status.Errors);
    }

    [TestMethod]
    public async Task MalformedUrlIsFlaggedWithoutAnHttpAttempt()
    {
      using var handler = new WebhookTestHandler((_, _) => throw new AssertFailedException("Must not send."));
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      await queue.EnqueueAsync(source, "bad url", "loot", 0);
      Assert.IsNotNull(queue.ErrorFor("bad url"));
    }

    [TestMethod]
    public async Task CancelingAnHttpRequestDoesNotLogTheAbortedConnectionAsAWebhookFailure()
    {
      var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var failures = new ConcurrentQueue<Exception>();
      using var handler = new WebhookTestHandler(async (_, token) =>
      {
        started.TrySetResult();
        try
        {
          await Task.Delay(Timeout.InfiniteTimeSpan, token);
          return Response(HttpStatusCode.OK);
        }
        catch (OperationCanceledException)
        {
          throw new HttpRequestException("Connection aborted");
        }
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      queue.EventsSendFailed += failures.Enqueue;
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      var sending = queue.EnqueueAsync(source, Url, "cancel", 0);
      await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
      queue.SetEnabled("char", "loot", false);
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.IsEmpty(failures);
      Assert.IsEmpty(queue.Status.Errors);
    }

    [TestMethod]
    public async Task ClearOwnerCancelsUnsentPartsAfterAnInFlightPart()
    {
      var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var calls = 0;
      using var handler = new WebhookTestHandler(async (_, token) =>
      {
        calls++;
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK);
      });
      using var client = new HttpClient(handler);
      var queue = new WebhookDispatchQueue(new DiscordWebhookSender(client));
      var source = queue.UpdateSources("owner", "char", [("loot", "Loot")])["loot"];
      var sending = queue.EnqueueAsync(source, Url, new string('x', 5000), 0);
      await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
      queue.ClearOwner("owner", true);
      await sending.WaitAsync(TimeSpan.FromSeconds(5));
      Assert.AreEqual(0, queue.Status.Pending);
      Assert.AreEqual(1, calls);
      Assert.IsEmpty(queue.Status.Errors);
    }
  }
}