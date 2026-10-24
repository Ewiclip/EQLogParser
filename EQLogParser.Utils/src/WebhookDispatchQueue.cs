using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EQLogParser
{
  /* Reference identity prevents cached wrappers from sending after a trigger is disabled and re-enabled. */
  internal sealed class WebhookSource(string owner, string characterId, string triggerId, string name)
  {
    internal string Owner { get; } = owner;
    internal string CharacterId { get; } = characterId;
    internal string TriggerId { get; } = triggerId;
    internal string Name { get; set; } = name;
  }
  internal sealed record WebhookError(WebhookSource Source, string Url, string Message);
  internal sealed record WebhookQueueStatus(int Pending, int Aggregating, int Retrying, WebhookError[] Errors);

  internal sealed class WebhookDispatchQueue
  {
    private readonly object _sync = new();
    private readonly DiscordWebhookSender _sender;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Dictionary<(string Owner, string Trigger), WebhookSource> _sources = new();
    private readonly HashSet<(string Character, string Trigger)> _disabled = new();
    private readonly Dictionary<(WebhookSource Source, string Url, int Seconds), Batch> _collecting = new();
    private readonly HashSet<Batch> _pending = new();
    private readonly Dictionary<string, WebhookError> _errors = new(StringComparer.Ordinal);

    internal event Action EventsStatusChanged;
    internal event Action<Exception> EventsSendFailed;

    internal WebhookDispatchQueue(DiscordWebhookSender sender, Func<DateTimeOffset> utcNow = null,
      Func<TimeSpan, CancellationToken, Task> delay = null)
    {
      _sender = sender;
      _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
      _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    internal WebhookQueueStatus Status
    {
      get
      {
        lock (_sync)
        {
          return new(_pending.Count, _collecting.Count, _pending.Count(batch => batch.Retrying), [.. _errors.Values]);
        }
      }
    }

    internal string ErrorFor(string url, string characterId = null, string triggerId = null)
    {
      lock (_sync)
      {
        if (url != null && _errors.TryGetValue(url.Trim(), out var error)) return error.Message;
        return triggerId == null ? null : _errors.Values.FirstOrDefault(item =>
          item.Source.CharacterId == characterId && item.Source.TriggerId == triggerId)?.Message;
      }
    }

    internal Dictionary<string, WebhookSource> UpdateSources(string owner, string characterId,
      IEnumerable<(string Id, string Name)> triggers)
    {
      var result = new Dictionary<string, WebhookSource>();
      bool canceled;
      lock (_sync)
      {
        var current = triggers.Where(trigger => !_disabled.Contains((characterId, trigger.Id))).ToArray();
        var ids = current.Select(trigger => trigger.Id).ToHashSet();
        var removed = _sources.Values.Where(source => source.Owner == owner && !ids.Contains(source.TriggerId)).ToHashSet();
        canceled = RemoveSources(removed);
        foreach (var (id, name) in current)
        {
          var key = (owner, id);
          if (!_sources.TryGetValue(key, out var source))
          {
            source = new(owner, characterId, id, name);
            _sources.Add(key, source);
          }

          source.Name = name;
          result.Add(id, source);
        }
      }

      if (canceled) EventsStatusChanged?.Invoke();
      return result;
    }

    internal void SetEnabled(string characterId, string triggerId, bool enabled)
    {
      var canceled = false;
      lock (_sync)
      {
        if (enabled) _disabled.Remove((characterId, triggerId));
        else _disabled.Add((characterId, triggerId));

        if (!enabled)
        {
          canceled = RemoveSources(_sources.Values
            .Where(source => source.CharacterId == characterId && source.TriggerId == triggerId).ToHashSet());
        }
      }

      if (canceled) EventsStatusChanged?.Invoke();
    }

    internal void ClearOwner(string owner, bool removeSources = false)
    {
      bool canceled;
      lock (_sync)
      {
        var sources = _sources.Values.Where(source => source.Owner == owner).ToHashSet();
        canceled = removeSources ? RemoveSources(sources) : RemoveBatches(sources);
      }

      if (canceled) EventsStatusChanged?.Invoke();
    }

    internal Task EnqueueAsync(WebhookSource source, string url, string content, int seconds)
    {
      if (source == null || string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(content)) return Task.CompletedTask;
      seconds = seconds is >= 5 and <= 30 && seconds % 5 == 0 ? seconds : 0;
      url = url?.Trim() ?? "";
      Batch batch;
      lock (_sync)
      {
        if (!_sources.TryGetValue((source.Owner, source.TriggerId), out var active) || !ReferenceEquals(source, active))
          return Task.CompletedTask;

        var key = (source, url, seconds);
        if (seconds > 0 && _collecting.TryGetValue(key, out batch) && _utcNow() < batch.Deadline)
        {
          batch.Lines.Add(content);
          return batch.Completion.Task;
        }

        batch = new(source, url, seconds, _utcNow().AddSeconds(seconds));
        batch.Lines.Add(content);
        _pending.Add(batch);
        if (seconds > 0) _collecting[key] = batch;
      }

      EventsStatusChanged?.Invoke();
      _ = ProcessAsync(batch);
      return batch.Completion.Task;
    }

    private async Task ProcessAsync(Batch batch)
    {
      var token = batch.Cancellation.Token;
      try
      {
        while (batch.Deadline - _utcNow() is var wait && wait > TimeSpan.Zero)
          await _delay(wait, token).ConfigureAwait(false);

        string content;
        lock (_sync)
        {
          token.ThrowIfCancellationRequested();
          StopCollecting(batch);
          content = string.Join("\n", batch.Lines);
        }

        EventsStatusChanged?.Invoke();
        foreach (var part in WebhookMessage.Create(content, batch.Seconds > 0))
        {
          await _sender.SendAsync(part, batch.Url, token, () => MarkRetrying(batch)).ConfigureAwait(false);
          lock (_sync)
          {
            batch.Retrying = false;
            foreach (var key in _errors.Where(pair => pair.Key == batch.Url
              || (pair.Value.Source.TriggerId == batch.Source.TriggerId
                && pair.Value.Source.CharacterId == batch.Source.CharacterId)).Select(pair => pair.Key).ToArray())
              _errors.Remove(key);
          }

          EventsStatusChanged?.Invoke();
        }
      }
      catch (OperationCanceledException) when (token.IsCancellationRequested)
      {
        /* Disabling a trigger cancels its pending work without reporting a failure. */
      }
      catch (Exception ex)
      {
        lock (_sync)
        {
          if (!token.IsCancellationRequested)
            _errors[batch.Url] = new(batch.Source, batch.Url, ex is System.Net.Http.HttpRequestException { StatusCode: { } status }
              ? $"Discord rejected this webhook (HTTP {(int)status}). Check the URL and permissions."
              : "Webhook failed. Check the URL and Error Log.");
        }

        if (!token.IsCancellationRequested) EventsSendFailed?.Invoke(ex);
      }
      finally
      {
        lock (_sync)
        {
          StopCollecting(batch);
          _pending.Remove(batch);
          /* Serialize disposal with cancellation in RemoveBatches. */
          batch.Cancellation.Dispose();
        }

        batch.Completion.TrySetResult();
        EventsStatusChanged?.Invoke();
      }
    }

    private void MarkRetrying(Batch batch)
    {
      var changed = false;
      lock (_sync)
      {
        if (_pending.Contains(batch) && !batch.Retrying)
        {
          batch.Retrying = true;
          changed = true;
        }
      }

      if (changed) EventsStatusChanged?.Invoke();
    }

    private bool RemoveSources(HashSet<WebhookSource> sources)
    {
      foreach (var source in sources) _sources.Remove((source.Owner, source.TriggerId));
      return RemoveBatches(sources);
    }

    private bool RemoveBatches(HashSet<WebhookSource> sources)
    {
      var batches = _pending.Where(batch => sources.Contains(batch.Source)).ToArray();
      foreach (var batch in batches)
      {
        _pending.Remove(batch);
        StopCollecting(batch);
        batch.Cancellation.Cancel();
      }

      return batches.Length > 0;
    }

    private void StopCollecting(Batch batch)
    {
      var key = (batch.Source, batch.Url, batch.Seconds);
      if (_collecting.TryGetValue(key, out var current) && ReferenceEquals(current, batch)) _collecting.Remove(key);
    }

    private sealed class Batch(WebhookSource source, string url, int seconds, DateTimeOffset deadline)
    {
      internal readonly WebhookSource Source = source;
      internal readonly string Url = url;
      internal readonly int Seconds = seconds;
      internal readonly DateTimeOffset Deadline = deadline;
      internal readonly List<string> Lines = [];
      internal readonly CancellationTokenSource Cancellation = new();
      internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
      internal bool Retrying;
    }
  }
}