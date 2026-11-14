using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Services;

/// <summary>
/// 查询向量高性能有界 LRU 缓存代理 (Query Embedding Cache Proxy)
/// 针对高频重复/变体问句，实现 0ms 向量生成，避免重复 200~500ms 模型推理耗时。
/// </summary>
public sealed class QueryEmbeddingCacheProxy : IEmbeddingService, IModelRuntimeTelemetryProvider
{
    private readonly IEmbeddingService _inner;
    private readonly int _maxCapacity;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private long _accessCounter;

    public ModelLifecycleSnapshot GetLifecycleSnapshot()
    {
        if (_inner is IModelRuntimeTelemetryProvider provider)
            return provider.GetLifecycleSnapshot();

        return new ModelLifecycleSnapshot
        {
            State = ModelState.Ready,
            IsReady = true,
            StateDescription = "就绪",
            ActiveLeases = 0
        };
    }

    public IEmbeddingBackendAdapter? CurrentAdapter =>
        (_inner as IModelRuntimeTelemetryProvider)?.CurrentAdapter;

    public EmbeddingOptions CurrentOptions =>
        (_inner as IModelRuntimeTelemetryProvider)?.CurrentOptions
        ?? new EmbeddingOptions
        {
            ModelName = _inner.ModelName,
            MaxSequenceLength = 512
        };

    private sealed class CacheEntry
    {
        public required float[] Vector { get; init; }
        public long LastAccess { get; set; }
    }

    public QueryEmbeddingCacheProxy(IEmbeddingService inner, int maxCapacity = 2000)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _maxCapacity = Math.Max(maxCapacity, 200);
    }

    public string ModelName => _inner.ModelName;
    public int Dimension => _inner.Dimension;
    public bool IsSimulationMode => _inner.IsSimulationMode;
    public bool SupportsAsymmetricEncoding => _inner.SupportsAsymmetricEncoding;

    public async Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null)
    {
        ClearCache();
        return await _inner.ReloadModelAsync(modelPath, modelName, maxSequenceLength);
    }

    public void UnloadModel()
    {
        ClearCache();
        _inner.UnloadModel();
    }

    public void ClearCache()
    {
        _cache.Clear();
    }

    public int CachedCount => _cache.Count;

    public async Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default)
    {
        return await EncodeAsync(text, EmbeddingMode.Symmetric, cancellationToken);
    }

    public async Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return await _inner.EncodeAsync(text, mode, cancellationToken);
        }

        // 仅缓存查询向量：Document/Symmetric 直通，避免入库文本污染 LRU 并在模型切换后返回陈旧向量
        if (mode != EmbeddingMode.Query)
        {
            return await _inner.EncodeAsync(text, mode, cancellationToken);
        }

        // 超大文本 (>4KB) 避免长驻 LRU 缓存，直接调用底层
        if (text.Length > 4096)
        {
            return await _inner.EncodeAsync(text, mode, cancellationToken);
        }

        var key = BuildCacheKey(text, mode);
        var access = Interlocked.Increment(ref _accessCounter);

        if (_cache.TryGetValue(key, out var entry))
        {
            entry.LastAccess = access;
            return entry.Vector;
        }

        var vector = await _inner.EncodeAsync(text, mode, cancellationToken);
        EnsureCapacity();
        _cache[key] = new CacheEntry { Vector = vector, LastAccess = access };
        return vector;
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        return await EncodeBatchAsync(texts, EmbeddingMode.Symmetric, cancellationToken);
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        var textList = texts as IList<string> ?? texts.ToList();
        if (textList.Count == 0) return Array.Empty<float[]>();

        // 仅缓存查询向量：Document/Symmetric 批量直通底层
        if (mode != EmbeddingMode.Query)
        {
            return await _inner.EncodeBatchAsync(textList, mode, cancellationToken);
        }

        var results = new float[textList.Count][];
        var missIndices = new List<int>();
        var missTexts = new List<string>();

        var access = Interlocked.Increment(ref _accessCounter);

        for (int i = 0; i < textList.Count; i++)
        {
            var t = textList[i];
            if (string.IsNullOrWhiteSpace(t) || t.Length > 4096)
            {
                missIndices.Add(i);
                missTexts.Add(t);
                continue;
            }

            var key = BuildCacheKey(t, mode);
            if (_cache.TryGetValue(key, out var entry))
            {
                entry.LastAccess = access;
                results[i] = entry.Vector;
            }
            else
            {
                missIndices.Add(i);
                missTexts.Add(t);
            }
        }

        if (missTexts.Count > 0)
        {
            var encodedMisses = await _inner.EncodeBatchAsync(missTexts, mode, cancellationToken);
            EnsureCapacity();

            for (int j = 0; j < missIndices.Count; j++)
            {
                var originalIdx = missIndices[j];
                var rawText = missTexts[j];
                var vec = encodedMisses[j];
                results[originalIdx] = vec;

                if (!string.IsNullOrWhiteSpace(rawText) && rawText.Length <= 4096)
                {
                    var key = BuildCacheKey(rawText, mode);
                    _cache[key] = new CacheEntry { Vector = vec, LastAccess = access };
                }
            }
        }

        return results;
    }

    public float[] Normalize(float[] vector) => _inner.Normalize(vector);

    public float Similarity(float[] a, float[] b) => _inner.Similarity(a, b);

    private string BuildCacheKey(string text, EmbeddingMode mode)
    {
        var normalized = NormalizeQuery(text);
        return $"{_inner.ModelName}:{mode}:{normalized}";
    }

    public static string NormalizeQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var trimmed = text.Trim().ToLowerInvariant();
        // 去除首尾中英文疑问词与常见问答标点
        trimmed = trimmed.TrimEnd('?', '？', '!', '！', '.', '。', ';', '；');
        // 将连续空白字符规范为单一空格
        return Regex.Replace(trimmed, @"\s+", " ");
    }

    private void EnsureCapacity()
    {
        if (_cache.Count < _maxCapacity) return;

        var entriesToRemove = _cache.Count - (int)(_maxCapacity * 0.85);
        if (entriesToRemove <= 0) return;

        var oldest = _cache
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(entriesToRemove)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldest)
        {
            _cache.TryRemove(key, out _);
        }
    }
}
