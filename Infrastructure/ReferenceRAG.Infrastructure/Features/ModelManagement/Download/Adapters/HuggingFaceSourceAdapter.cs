using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;

/// <summary>
/// HuggingFace 及国内镜像源 (hf-mirror.com) 适配器
/// 支持 308 重定向自愈、Token 鉴权、轻量级 HEAD 文件大小自动补齐与断点临时文件下载
/// </summary>
public class HuggingFaceSourceAdapter : IModelSourceAdapter
{
    private readonly HttpClient _httpClient;
    private readonly string _hfToken;
    private readonly List<string> _endpoints = new();
    private string _activeEndpoint;
    private bool _disposed;

    public string SourceName => "HuggingFace";
    public string ActiveEndpoint => _activeEndpoint;

    public HuggingFaceSourceAdapter(string? hfToken = null, string? customEndpoint = null, HttpClient? httpClient = null)
    {
        _hfToken = !string.IsNullOrWhiteSpace(hfToken) ? hfToken : (Environment.GetEnvironmentVariable("HF_TOKEN") ?? string.Empty);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        _httpClient = httpClient ?? new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36 ReferenceRAG/2.0");

        if (!string.IsNullOrWhiteSpace(_hfToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _hfToken);
        }

        var ep = !string.IsNullOrWhiteSpace(customEndpoint) ? customEndpoint : Environment.GetEnvironmentVariable("HF_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(ep))
        {
            var cleanEp = ep.Trim().TrimEnd('/');
            if (!_endpoints.Contains(cleanEp, StringComparer.OrdinalIgnoreCase))
            {
                _endpoints.Add(cleanEp);
            }
        }

        if (!_endpoints.Contains("https://hf-mirror.com", StringComparer.OrdinalIgnoreCase))
        {
            _endpoints.Add("https://hf-mirror.com");
        }

        if (!_endpoints.Contains("https://huggingface.co", StringComparer.OrdinalIgnoreCase))
        {
            _endpoints.Add("https://huggingface.co");
        }

        _activeEndpoint = _endpoints[0];
    }

    public async Task<ModelManifest> GetManifestAsync(string modelId, CancellationToken cancellationToken = default)
    {
        Exception? lastEx = null;

        foreach (var endpoint in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var apiUrl = $"{endpoint}/api/models/{modelId}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30)); // 30s 元数据查询超时保护

                using var response = await _httpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException(
                        $"模型 '{modelId}' 属于 HuggingFace 协议限制/授权私有模型 (HTTP {(int)response.StatusCode})。请在系统配置中提供 HF_TOKEN。");
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    lastEx = new KeyNotFoundException($"模型 '{modelId}' 在端点 {endpoint} 不存在 (HTTP 404)");
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    lastEx = new HttpRequestException($"端点 {endpoint} 返回异常状态码: {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var manifest = new ModelManifest
                {
                    ModelId = modelId,
                    SourceEndpoint = endpoint
                };

                if (root.TryGetProperty("siblings", out var siblings) && siblings.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sibling in siblings.EnumerateArray())
                    {
                        if (sibling.TryGetProperty("rfilename", out var rfilename))
                        {
                            var path = rfilename.GetString();
                            if (string.IsNullOrWhiteSpace(path)) continue;

                            long size = 0;
                            if (sibling.TryGetProperty("size", out var sizeElem) && sizeElem.ValueKind == JsonValueKind.Number)
                            {
                                size = sizeElem.GetInt64();
                            }

                            manifest.Files.Add(new ModelFileItem
                            {
                                Path = path,
                                Size = size
                            });
                        }
                    }
                }

                _activeEndpoint = endpoint;

                // 核心修复：对缺失 size 的关键模型文件（如 model.safetensors, model.onnx），通过 HEAD 请求快速探测真实 Content-Length
                await EnrichMissingFileSizesAsync(manifest, modelId, cancellationToken);

                return manifest;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"[HuggingFaceAdapter] 端点 {endpoint} 探测失败 ({ex.Message})，尝试备用端点...");
            }
        }

        throw new InvalidOperationException($"无法获取模型 '{modelId}' 的文件清单。尝试过的端点: [{string.Join(", ", _endpoints)}]. 错误: {lastEx?.Message}", lastEx);
    }

    public async Task<long> ResolveFileSizeAsync(string modelId, string relativePath, CancellationToken cancellationToken = default)
    {
        var candidateEndpoints = new List<string> { _activeEndpoint };
        foreach (var ep in _endpoints)
        {
            if (!candidateEndpoints.Contains(ep, StringComparer.OrdinalIgnoreCase))
            {
                candidateEndpoints.Add(ep);
            }
        }

        foreach (var ep in candidateEndpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{ep}/{modelId}/resolve/main/{relativePath}";

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15)); // 单个文件元数据探测上限 15s

                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
                {
                    return response.Content.Headers.ContentLength.Value;
                }

                // 若 HEAD 不支持，使用 GET 读取 headers
                if (!response.IsSuccessStatusCode)
                {
                    using var getReq = new HttpRequestMessage(HttpMethod.Get, url);
                    using var getResp = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (getResp.IsSuccessStatusCode && getResp.Content.Headers.ContentLength.HasValue)
                    {
                        return getResp.Content.Headers.ContentLength.Value;
                    }
                }
            }
            catch
            {
                // 忽略单个端点 HEAD 失败，尝试下一个
            }
        }

        return 0;
    }

    public async Task DownloadFileAsync(
        string modelId,
        string relativePath,
        string targetFilePath,
        Action<long, long>? onChunkReceived = null,
        CancellationToken cancellationToken = default)
    {
        var targetDir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var tempFilePath = targetFilePath + ".tmp_" + Guid.NewGuid().ToString("N")[..8];
        Exception? lastEx = null;

        var candidateEndpoints = new List<string> { _activeEndpoint };
        foreach (var ep in _endpoints)
        {
            if (!candidateEndpoints.Contains(ep, StringComparer.OrdinalIgnoreCase))
            {
                candidateEndpoints.Add(ep);
            }
        }

        foreach (var endpoint in candidateEndpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{endpoint}/{modelId}/resolve/main/{relativePath}";

            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalRead = 0;
                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                {
                    var buffer = new byte[65536];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                        totalRead += bytesRead;
                        onChunkReceived?.Invoke(bytesRead, totalRead);
                    }
                }

                // 下载完整，原子覆盖目标文件
                if (File.Exists(targetFilePath))
                {
                    File.Delete(targetFilePath);
                }
                File.Move(tempFilePath, targetFilePath);

                _activeEndpoint = endpoint;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteFile(tempFilePath);
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                TryDeleteFile(tempFilePath);
                Console.WriteLine($"[HuggingFaceAdapter] 端点 {endpoint} 下载文件 {relativePath} 异常: {ex.Message}，尝试备用源...");
            }
        }

        TryDeleteFile(tempFilePath);
        throw new InvalidOperationException($"无法下载文件 '{relativePath}' (模型: {modelId})。已尝试所有镜像端点。最后错误: {lastEx?.Message}", lastEx);
    }

    private async Task EnrichMissingFileSizesAsync(ModelManifest manifest, string modelId, CancellationToken cancellationToken)
    {
        // 关键大权重文件过滤：当未提供 size 时通过 HEAD 探测
        var targetFiles = manifest.Files.Where(f =>
            f.Size <= 0 &&
            (f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase))).ToList();

        if (targetFiles.Count == 0) return;

        foreach (var file in targetFiles)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var resolved = await ResolveFileSizeAsync(modelId, file.Path, cancellationToken);
            if (resolved > 0)
            {
                file.Size = resolved;
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public async Task<List<ModelVariantSearchResult>> SearchVariantsAsync(
        string query,
        string? format = null,
        int limit = 15,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ModelVariantSearchResult>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return results;
        }

        var normalizedFormat = format?.Trim().ToLowerInvariant();
        var cleanQuery = query.Trim();

        foreach (var endpoint in _endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encodedQuery = Uri.EscapeDataString(cleanQuery);
            
            // 构建带排序与过滤的搜索 URL
            var searchUrl = $"{endpoint}/api/models?search={encodedQuery}&sort=downloads&direction=-1&limit={Math.Max(limit * 2, 20)}&full=false";
            if (normalizedFormat == "gguf")
            {
                searchUrl += "&filter=gguf";
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(12)); // 12秒快速超时保护

                using var response = await _httpClient.GetAsync(searchUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idElem)) continue;
                    var id = idElem.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var parts = id.Split('/');
                    var author = parts.Length > 1 ? parts[0] : "";
                    var modelName = parts.Length > 1 ? parts[1] : id;

                    long downloads = 0;
                    if (item.TryGetProperty("downloads", out var dlElem) && dlElem.ValueKind == JsonValueKind.Number)
                    {
                        downloads = dlElem.GetInt64();
                    }

                    int likes = 0;
                    if (item.TryGetProperty("likes", out var likesElem) && likesElem.ValueKind == JsonValueKind.Number)
                    {
                        likes = likesElem.GetInt32();
                    }

                    string pipelineTag = "";
                    if (item.TryGetProperty("pipeline_tag", out var ptElem))
                    {
                        pipelineTag = ptElem.GetString() ?? "";
                    }

                    bool isPrivate = false;
                    if (item.TryGetProperty("private", out var privElem))
                    {
                        isPrivate = privElem.GetBoolean();
                    }

                    DateTime? lastModified = null;
                    if (item.TryGetProperty("lastModified", out var lmElem) && lmElem.TryGetDateTime(out var dt))
                    {
                        lastModified = dt;
                    }

                    var tags = new List<string>();
                    if (item.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tagsElem.EnumerateArray())
                        {
                            var s = t.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) tags.Add(s);
                        }
                    }

                    var lowerId = id.ToLowerInvariant();
                    bool hasGguf = tags.Any(t => t.Equals("gguf", StringComparison.OrdinalIgnoreCase))
                                || lowerId.Contains("-gguf") || lowerId.Contains(".gguf") || lowerId.Contains("/gguf");
                    bool hasOnnx = tags.Any(t => t.Equals("onnx", StringComparison.OrdinalIgnoreCase))
                                || lowerId.Contains("onnx");

                    // 依据请求格式做智能过滤
                    if (normalizedFormat == "gguf" && !hasGguf) continue;
                    if (normalizedFormat == "onnx" && !hasOnnx) continue;

                    results.Add(new ModelVariantSearchResult
                    {
                        Id = id,
                        Author = author,
                        ModelName = modelName,
                        Downloads = downloads,
                        Likes = likes,
                        PipelineTag = pipelineTag,
                        Tags = tags,
                        HasGguf = hasGguf,
                        HasOnnx = hasOnnx,
                        IsPrivate = isPrivate,
                        LastModified = lastModified,
                        Description = string.IsNullOrWhiteSpace(pipelineTag) ? "开源社区变种" : pipelineTag
                    });
                }

                if (results.Count > 0)
                {
                    _activeEndpoint = endpoint;
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[HuggingFaceAdapter] 端点 {endpoint} 搜索变种失败: {ex.Message}，尝试备选端点...");
            }
        }

        return results
            .DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r => r.Downloads)
            .Take(limit)
            .ToList();
    }

    public async Task<ModelVariantDetail?> GetVariantDetailAsync(
        string repositoryId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId)) return null;

        var manifest = await GetManifestAsync(repositoryId, cancellationToken);
        if (manifest == null || manifest.Files.Count == 0) return null;

        var parts = repositoryId.Split('/');
        var author = parts.Length > 1 ? parts[0] : "";

        var detail = new ModelVariantDetail
        {
            Id = repositoryId,
            Author = author,
            SourceEndpoint = manifest.SourceEndpoint,
            HasGguf = manifest.HasGguf,
            HasOnnx = manifest.HasOnnx,
            TotalFilesCount = manifest.Files.Count,
            WebUrl = $"{manifest.SourceEndpoint}/{repositoryId}"
        };

        // 提取并结构化 GGUF 文件
        var ggufFiles = manifest.GetGgufFiles();
        foreach (var file in ggufFiles)
        {
            var match = System.Text.RegularExpressions.Regex.Match(file.FileName.ToLowerInvariant(), @"(q[0-9]_[a-z0-9_]+|f16|f32|bf16)");
            var quant = match.Success ? match.Groups[1].Value.ToUpperInvariant() : "GGUF";
            bool isRecommended = quant.Contains("Q4_K") || quant.Contains("Q4_0");

            detail.GgufFiles.Add(new ModelVariantFileOption
            {
                Path = file.Path,
                FileName = file.FileName,
                FileSize = file.Size,
                Format = "gguf",
                Quantization = quant,
                IsRecommended = isRecommended,
                Description = $"GGUF 量化版本 ({quant})"
            });
        }

        // 提取并结构化 ONNX 文件
        var onnxFiles = manifest.GetOnnxFiles();
        foreach (var file in onnxFiles)
        {
            detail.OnnxFiles.Add(new ModelVariantFileOption
            {
                Path = file.Path,
                FileName = file.FileName,
                FileSize = file.Size,
                Format = "onnx",
                Description = file.FileName.Equals("model.onnx", StringComparison.OrdinalIgnoreCase)
                    ? "标准 ONNX 格式"
                    : $"ONNX 变体 ({file.FileName})"
            });
        }

        return detail;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
