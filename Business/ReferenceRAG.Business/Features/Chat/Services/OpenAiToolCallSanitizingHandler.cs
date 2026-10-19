using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// A stream wrapper that intercepts Server-Sent Events (SSE) and rewrites tool call chunks where
/// "type": "" or "type": null is sent by third-party OpenAI-compatible providers, normalizing them
/// to "type": "function" so that openai-dotnet's ChatToolCallKind parser does not throw ArgumentOutOfRangeException.
/// </summary>
public sealed class ToolCallSanitizingStream(Stream inner) : Stream
{
    private readonly Stream _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly MemoryStream _outputBuffer = new();
    private readonly MemoryStream _pendingLine = new();
    private int _outputPosition;
    private bool _eof;

    private static readonly Regex TypeRegex = new(@"(?<!\\)""type""\s*:\s*(""""|null)", RegexOptions.Compiled);

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
        if (lineBytes.IndexOf("\"tool_calls\""u8) >= 0)
        {
            string lineStr = Encoding.UTF8.GetString(lineBytes);
            string sanitized = TypeRegex.Replace(lineStr, "\"type\":\"function\"");
            byte[] sanitizedBytes = Encoding.UTF8.GetBytes(sanitized);
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
                if (rawJson.Contains("\"tool_calls\""))
                {
                    var sanitized = JsonTypeRegex.Replace(rawJson, "\"type\":\"function\"");
                    var newContent = new StringContent(sanitized, Encoding.UTF8, "application/json");
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
}
