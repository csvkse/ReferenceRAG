using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Infrastructure.Features.Cognitive;

/// <summary>
/// TypeSafe AI System One (Jev) 客户端实现
/// 内置 IPv4 优先连接处理（消除 Windows Cloudflare IPv6 握手挂起）与指数退避重试
/// </summary>
public sealed class JevSystemOneClient : ISystemOneClient
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("JevSystemOneClient");
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly bool _ownsClient;
    private bool _disposed;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsConfigured { get; }
    public string ModelName => _model;

    public JevSystemOneClient(
        string? apiKey,
        string? baseUrl = "https://api.typesafe.ai/v1/systemone",
        string? model = "jev-latest",
        HttpMessageHandler? customHandler = null,
        int timeoutSeconds = 15)
    {
        _baseUrl = (baseUrl ?? "https://api.typesafe.ai/v1/systemone").TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "jev-latest" : model.Trim();

        IsConfigured = !string.IsNullOrWhiteSpace(apiKey) && !apiKey.StartsWith("<", StringComparison.Ordinal);

        if (customHandler != null)
        {
            _httpClient = new HttpClient(customHandler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))
            };
            _ownsClient = true;
        }
        else
        {
            var handler = CreateResilientSocketsHandler();
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))
            };
            _ownsClient = true;
        }

        if (IsConfigured)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey!.Trim());
        }
    }

    private static SocketsHttpHandler CreateResilientSocketsHandler()
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            // Windows 环境下优先解析与连接 IPv4，解决 Cloudflare 国际节点 IPv6 路由空洞引发的 15s 握手超时
            ConnectCallback = async (context, cancellationToken) =>
            {
                IPAddress[] targetAddresses;
                try
                {
                    // 优先获取 IPv4
                    var ipv4Entry = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
                    targetAddresses = ipv4Entry.Length > 0
                        ? ipv4Entry
                        : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                }
                catch
                {
                    targetAddresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(targetAddresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    public async Task<SystemOneResponse> EvaluateAsync(
        object state,
        IDictionary<string, SystemOneQuestion> questions,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConfigured)
        {
            throw new InvalidOperationException("TypeSafe AI System One 未配置有效 API Key。");
        }

        if (questions == null || questions.Count == 0)
        {
            throw new ArgumentException("问题字典不能为空", nameof(questions));
        }

        var requestPayload = new SystemOneRequest
        {
            Model = _model,
            State = state,
            Questions = new Dictionary<string, SystemOneQuestion>(questions)
        };

        var json = JsonSerializer.Serialize(requestPayload, _jsonOptions);
        var sw = Stopwatch.StartNew();

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                var resp = await _httpClient.SendAsync(request, ct);
                var payload = await resp.Content.ReadAsByteArrayAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    sw.Stop();
                    var parsed = JsonSerializer.Deserialize<SystemOneResponse>(payload, _jsonOptions);
                    if (parsed == null)
                    {
                        throw new JsonException("System One 反序列化返回空对象");
                    }

                    _logger.LogDebug("[JevSystemOne] 决策成功 | Model: {Model} | 耗时: {Elapsed}ms | Tokens: in={In}, out={Out}",
                        parsed.Model, sw.ElapsedMilliseconds, parsed.Usage?.InputTokens, parsed.Usage?.OutputTokens);

                    return parsed;
                }

                // 429 或 5xx 服务端错误尝试退避重试
                var statusCode = (int)resp.StatusCode;
                var rawBody = Encoding.UTF8.GetString(payload);

                if ((statusCode == 429 || statusCode >= 500) && attempt < maxRetries)
                {
                    var delayMs = (int)(Math.Pow(2, attempt) * 200);
                    _logger.LogWarning("[JevSystemOne] 遭遇 HTTP {Status}，将在 {Delay}ms 后执行第 {Next} 次重试... 响应: {Body}",
                        statusCode, delayMs, attempt + 1, rawBody);
                    await Task.Delay(delayMs, ct);
                    continue;
                }

                _logger.LogError("[JevSystemOne] HTTP {Status} | POST {Url} | 响应: {Body}",
                    statusCode, _baseUrl, rawBody);
                throw new HttpRequestException($"System One 服务返回 HTTP {statusCode} ({resp.ReasonPhrase}): {rawBody}");
            }
            catch (Exception ex) when (attempt < maxRetries && ex is not OperationCanceledException)
            {
                var delayMs = (int)(Math.Pow(2, attempt) * 200);
                _logger.LogWarning(ex, "[JevSystemOne] 调用异常，将在 {Delay}ms 后重试: {Message}", delayMs, ex.Message);
                await Task.Delay(delayMs, ct);
            }
        }

        throw new HttpRequestException($"System One 调用在 {maxRetries} 次重试后均失败。");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
