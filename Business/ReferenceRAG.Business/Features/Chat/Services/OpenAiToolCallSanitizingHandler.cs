using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// A stream wrapper that intercepts Server-Sent Events (SSE) and:
/// 1. Rewrites tool call chunks where "type": "" or "type": null is sent by third-party OpenAI-compatible providers,
///    normalizing them to "type": "function" so that openai-dotnet's ChatToolCallKind parser does not throw ArgumentOutOfRangeException.
/// 2. (Adapter Pattern) Adapts reasoning_content fields emitted by DeepSeek-R1 / Qwen models into standard content
///    wrapped in &lt;think&gt;...&lt;/think&gt; tags, ensuring complete CoT thinking visibility across both local and remote providers.
/// </summary>
public sealed class ToolCallSanitizingStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MemoryStream _outputBuffer = new();
    private readonly MemoryStream _pendingLine = new();
    private int _outputPosition;
    private bool _eof;
    private bool _inReasoning;

    private static readonly Regex TypeRegex = new(@"(?<!\\)""type""\s*:\s*(""""|null)", RegexOptions.Compiled);
    private static readonly Regex ReasoningContentRegex = new(@"""reasoning_content""\s*:\s*(""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);
    private static readonly Regex ContentPrefixRegex = new(@"(""content""\s*:\s*"")", RegexOptions.Compiled);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(new Memory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (_outputPosition >= _outputBuffer.Length && !_eof)
        {
            _outputBuffer.SetLength(0);
            _outputPosition = 0;

            byte[] temp = ArrayPool<byte>.Shared.Rent(4096);
            int bytesRead;
            try
            {
                bytesRead = await _inner.ReadAsync(temp.AsMemory(0, 4096), cancellationToken);
            }
            finally
            {
                // temp returned below
            }

            if (bytesRead == 0)
            {
                _eof = true;
                if (_pendingLine.Length > 0)
                {
                    ProcessLine(_pendingLine.ToArray());
                    _pendingLine.SetLength(0);
                }
                if (_inReasoning)
                {
                    _inReasoning = false;
                    string closeChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"\\n</think>\\n\\n\"}}]}\n\n";
                    byte[] closeBytes = Encoding.UTF8.GetBytes(closeChunk);
                    _outputBuffer.Write(closeBytes);
                }
                ArrayPool<byte>.Shared.Return(temp);
                break;
            }

            int lineStart = 0;
            for (int i = 0; i < bytesRead; i++)
            {
                if (temp[i] == (byte)'\n')
                {
                    int segmentLength = i - lineStart + 1;
                    if (_pendingLine.Length > 0)
                    {
                        _pendingLine.Write(temp, lineStart, segmentLength);
                        ProcessLine(_pendingLine.ToArray());
                        _pendingLine.SetLength(0);
                    }
                    else
                    {
                        ProcessLine(temp.AsSpan(lineStart, segmentLength));
                    }
                    lineStart = i + 1;
                }
            }

            if (lineStart < bytesRead)
            {
                _pendingLine.Write(temp, lineStart, bytesRead - lineStart);
            }

            ArrayPool<byte>.Shared.Return(temp);
        }

        int available = (int)(_outputBuffer.Length - _outputPosition);
        if (available <= 0) return 0;

        int toCopy = Math.Min(available, destination.Length);
        _outputBuffer.GetBuffer().AsSpan(_outputPosition, toCopy).CopyTo(destination.Span);
        _outputPosition += toCopy;
        return toCopy;
    }

    private void ProcessLine(ReadOnlySpan<byte> lineBytes)
    {
        bool hasToolCalls = lineBytes.IndexOf("\"tool_calls\""u8) >= 0;
        bool hasReasoning = lineBytes.IndexOf("\"reasoning_content\""u8) >= 0;
        bool hasContent = lineBytes.IndexOf("\"content\""u8) >= 0;
        bool hasDone = lineBytes.IndexOf("[DONE]"u8) >= 0;
        bool hasFinish = lineBytes.IndexOf("\"finish_reason\""u8) >= 0;

        if (hasToolCalls || hasReasoning || (_inReasoning && (hasContent || hasDone || hasFinish)))
        {
            string lineStr = Encoding.UTF8.GetString(lineBytes);

            // 1. Tool call type normalization
            if (hasToolCalls)
            {
                lineStr = TypeRegex.Replace(lineStr, "\"type\":\"function\"");
            }

            // 2. Reasoning content adaptation (DeepSeek-R1 / Qwen -> standard <think> stream)
            if (hasReasoning)
            {
                var match = ReasoningContentRegex.Match(lineStr);
                if (match.Success)
                {
                    string reasoningVal = match.Groups[1].Value;
                    if (!_inReasoning)
                    {
                        _inReasoning = true;
                        string newContent = "\"<think>\\n" + reasoningVal.Substring(1);
                        lineStr = ReasoningContentRegex.Replace(lineStr, "\"content\":" + newContent);
                    }
                    else
                    {
                        lineStr = ReasoningContentRegex.Replace(lineStr, "\"content\":" + reasoningVal);
                    }
                }
            }
            else if (_inReasoning && hasContent)
            {
                _inReasoning = false;
                lineStr = ContentPrefixRegex.Replace(lineStr, "$1\\n</think>\\n\\n");
            }
            else if (_inReasoning && (hasDone || (hasFinish && !lineStr.Contains("\"finish_reason\":null", StringComparison.OrdinalIgnoreCase))))
            {
                _inReasoning = false;
                string closeChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"\\n</think>\\n\\n\"}}]}\n\n";
                byte[] closeBytes = Encoding.UTF8.GetBytes(closeChunk);
                _outputBuffer.Write(closeBytes);
            }

            byte[] sanitizedBytes = Encoding.UTF8.GetBytes(lineStr);
            _outputBuffer.Write(sanitizedBytes);
        }
        else
        {
            _outputBuffer.Write(lineBytes);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _outputBuffer.Dispose();
            _pendingLine.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// DelegatingHandler that attaches <see cref="ToolCallSanitizingStream"/> to streaming responses and
/// normalizes tool call types in non-streaming responses, ensuring third-party LLMs and OpenAI-compatible
/// proxies that omit or send empty "type": "" do not crash openai-dotnet.
/// </summary>
public sealed class OpenAiToolCallSanitizingHandler : DelegatingHandler
{
    private static readonly Regex JsonTypeRegex = new(@"(?<!\\)""type""\s*:\s*(""""|null)", RegexOptions.Compiled);

    public OpenAiToolCallSanitizingHandler() : base(new SocketsHttpHandler()) { }
    public OpenAiToolCallSanitizingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Content != null)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var rawStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var sanitizedStream = new ToolCallSanitizingStream(rawStream);
                var newContent = new StreamContent(sanitizedStream);
                foreach (var header in response.Content.Headers)
                {
                    newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                response.Content = newContent;
            }
            else if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);
                bool modified = false;
                if (rawJson.Contains("\"tool_calls\""))
                {
                    rawJson = JsonTypeRegex.Replace(rawJson, "\"type\":\"function\"");
                    modified = true;
                }
                if (rawJson.Contains("\"reasoning_content\""))
                {
                    rawJson = AdaptNonStreamingReasoning(rawJson);
                    modified = true;
                }
                if (modified)
                {
                    var newContent = new StringContent(rawJson, Encoding.UTF8, "application/json");
                    foreach (var header in response.Content.Headers)
                    {
                        if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    response.Content = newContent;
                }
            }
        }
        return response;
    }

    private static readonly Regex NonStreamingReasoningRegex = new(@"""reasoning_content""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex NonStreamingContentRegex = new(@"""content""\s*:\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    /// <summary>
    /// 将非流式 JSON 响应中的 reasoning_content 提取并包裹进 &lt;think&gt; 插入 content 前部
    /// </summary>
    public static string AdaptNonStreamingReasoning(string json)
    {
        var rMatch = NonStreamingReasoningRegex.Match(json);
        if (!rMatch.Success) return json;

        string reasoning = rMatch.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(reasoning)) return json;

        return NonStreamingContentRegex.Replace(json, m =>
        {
            string content = m.Groups[1].Value;
            string combined = $"<think>\\n{reasoning}\\n</think>\\n\\n{content}";
            return $"\"content\":\"{combined}\"";
        }, 1);
    }
}
