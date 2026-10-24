using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EQLogParser
{
  internal sealed record WebhookMessage(string BatchId, int Part, int Total, bool Aggregated, string Digest, string Content)
  {
    private const int MaxContentLength = 2000;

    internal string Json => JsonSerializer.Serialize(new
    {
      content = Content,
      embeds = new[] { new { footer = new { text = $"EQLP1|{BatchId}|{Part}|{Total}|{(Aggregated ? "A" : "N")}|{Digest}" } } }
    });

    internal static List<WebhookMessage> Create(string content, bool aggregated, string batchId = null)
    {
      ArgumentException.ThrowIfNullOrEmpty(content);
      batchId ??= Guid.NewGuid().ToString("N");
      var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
      var chunks = new List<string>();
      for (var offset = 0; offset < content.Length;)
      {
        var length = Math.Min(MaxContentLength, content.Length - offset);
        if (offset + length < content.Length && char.IsHighSurrogate(content[offset + length - 1])
          && char.IsLowSurrogate(content[offset + length]))
        {
          length--;
        }

        chunks.Add(content.Substring(offset, length));
        offset += length;
      }

      var result = new List<WebhookMessage>();
      for (var index = 0; index < chunks.Count; index++)
      {
        result.Add(new(batchId, index + 1, chunks.Count, aggregated, digest, chunks[index]));
      }

      return result;
    }
  }
}