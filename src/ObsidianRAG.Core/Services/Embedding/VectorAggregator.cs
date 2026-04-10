using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 向量聚合服务 - 文档级和章节级向量聚合
/// </summary>
public class VectorAggregator
{
    private const int MinTokenWeightThreshold = 100;
    private const int MaxTokenWeightThreshold = 500;

    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;

    public VectorAggregator(IEmbeddingService embeddingService, IVectorStore vectorStore)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// 聚合文档向量
    /// </summary>
    public async Task<VectorRecord> AggregateDocumentVectorAsync(
        string fileId,
        IEnumerable<ChunkRecord> chunks,
        CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0)
        {
            throw new ArgumentException("No chunks to aggregate", nameof(chunks));
        }

        // 获取所有分段的向量
        var vectors = new List<float[]>();
        foreach (var chunk in chunkList)
        {
            var vector = await _vectorStore.GetVectorByChunkIdAsync(chunk.Id, cancellationToken);
            if (vector != null)
            {
                vectors.Add(vector.Vector);
            }
        }

        if (vectors.Count == 0)
        {
            throw new InvalidOperationException("No vectors found for chunks");
        }

        // 加权平均池化
        var aggregatedVector = WeightedAveragePooling(vectors, chunkList);

        // 创建文档级向量记录
        var docVector = new VectorRecord
        {
            Id = Guid.NewGuid().ToString(),
            ChunkId = $"doc-{fileId}",
            Vector = _embeddingService.Normalize(aggregatedVector),
            ModelName = _embeddingService.ModelName
        };

        return docVector;
    }

    /// <summary>
    /// 聚合章节向量
    /// </summary>
    public async Task<VectorRecord> AggregateSectionVectorAsync(
        string sectionId,
        IEnumerable<ChunkRecord> chunks,
        CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0)
        {
            throw new ArgumentException("No chunks to aggregate", nameof(chunks));
        }

        // 获取所有分段的向量
        var vectors = new List<float[]>();
        foreach (var chunk in chunkList)
        {
            var vector = await _vectorStore.GetVectorByChunkIdAsync(chunk.Id, cancellationToken);
            if (vector != null)
            {
                vectors.Add(vector.Vector);
            }
        }

        if (vectors.Count == 0)
        {
            throw new InvalidOperationException("No vectors found for chunks");
        }

        // 加权平均池化
        var aggregatedVector = WeightedAveragePooling(vectors, chunkList);

        // 创建章节级向量记录
        var sectionVector = new VectorRecord
        {
            Id = Guid.NewGuid().ToString(),
            ChunkId = $"section-{sectionId}",
            Vector = _embeddingService.Normalize(aggregatedVector),
            ModelName = _embeddingService.ModelName
        };

        return sectionVector;
    }

    /// <summary>
    /// 批量聚合文档向量
    /// </summary>
    public async Task<Dictionary<string, VectorRecord>> AggregateAllDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, VectorRecord>();
        var files = await _vectorStore.GetAllFilesAsync(cancellationToken);

        foreach (var file in files)
        {
            try
            {
                var chunks = await _vectorStore.GetChunksByFileAsync(file.Id, cancellationToken);
                var chunkList = chunks.ToList();

                if (chunkList.Count > 0)
                {
                    var docVector = await AggregateDocumentVectorAsync(file.Id, chunkList, cancellationToken);
                    result[file.Id] = docVector;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to aggregate document {file.Id}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// 加权平均池化
    /// </summary>
    private float[] WeightedAveragePooling(List<float[]> vectors, List<ChunkRecord> chunks)
    {
        var dimension = vectors[0].Length;
        var result = new float[dimension];
        var totalWeight = 0f;

        for (int i = 0; i < vectors.Count; i++)
        {
            var weight = GetChunkWeight(chunks[i]);
            totalWeight += weight;

            for (int j = 0; j < dimension; j++)
            {
                result[j] += vectors[i][j] * weight;
            }
        }

        // 归一化
        if (totalWeight > 0)
        {
            for (int j = 0; j < dimension; j++)
            {
                result[j] /= totalWeight;
            }
        }

        return result;
    }

    /// <summary>
    /// 获取分段权重
    /// </summary>
    private float GetChunkWeight(ChunkRecord chunk)
    {
        float weight = 1.0f;

        // 标题级别权重
        weight *= chunk.Level switch
        {
            1 => 1.5f,  // H1
            2 => 1.3f,  // H2
            3 => 1.1f,  // H3
            _ => 1.0f
        };

        // 内容长度权重（短内容信息密度高）
        if (chunk.TokenCount < MinTokenWeightThreshold) weight *= 1.2f;
        if (chunk.TokenCount > MaxTokenWeightThreshold) weight *= 0.8f;

        // 预设权重
        weight *= chunk.Weight;

        return weight;
    }

    /// <summary>
    /// 计算向量相似度矩阵
    /// </summary>
    public float[,] ComputeSimilarityMatrix(List<float[]> vectors)
    {
        var n = vectors.Count;
        var matrix = new float[n, n];

        for (int i = 0; i < n; i++)
        {
            for (int j = i; j < n; j++)
            {
                var similarity = _embeddingService.Similarity(vectors[i], vectors[j]);
                matrix[i, j] = similarity;
                matrix[j, i] = similarity;
            }
        }

        return matrix;
    }

    /// <summary>
    /// 检测相似章节（用于去重）
    /// </summary>
    public async Task<List<SimilarSectionPair>> FindSimilarSectionsAsync(
        float threshold = 0.95f,
        CancellationToken cancellationToken = default)
    {
        var result = new List<SimilarSectionPair>();
        var files = await _vectorStore.GetAllFilesAsync(cancellationToken);

        var allSectionVectors = new List<(string FileId, string SectionPath, float[] Vector)>();

        foreach (var file in files)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id, cancellationToken);
            
            // 按章节分组
            var sections = chunks
                .Where(c => !string.IsNullOrEmpty(c.HeadingPath))
                .GroupBy(c => c.HeadingPath);

            foreach (var section in sections)
            {
                var sectionChunks = section.ToList();
                if (sectionChunks.Count == 0) continue;

                // 聚合章节向量
                var vectors = new List<float[]>();
                foreach (var chunk in sectionChunks)
                {
                    var vector = await _vectorStore.GetVectorByChunkIdAsync(chunk.Id, cancellationToken);
                    if (vector != null)
                    {
                        vectors.Add(vector.Vector);
                    }
                }

                if (vectors.Count > 0)
                {
                    var aggregated = WeightedAveragePooling(vectors, sectionChunks);
                    allSectionVectors.Add((file.Id, section.Key!, aggregated));
                }
            }
        }

        // 比较所有章节对
        for (int i = 0; i < allSectionVectors.Count; i++)
        {
            for (int j = i + 1; j < allSectionVectors.Count; j++)
            {
                var similarity = _embeddingService.Similarity(
                    allSectionVectors[i].Vector,
                    allSectionVectors[j].Vector);

                if (similarity >= threshold)
                {
                    result.Add(new SimilarSectionPair
                    {
                        FileId1 = allSectionVectors[i].FileId,
                        SectionPath1 = allSectionVectors[i].SectionPath,
                        FileId2 = allSectionVectors[j].FileId,
                        SectionPath2 = allSectionVectors[j].SectionPath,
                        Similarity = similarity
                    });
                }
            }
        }

        return result;
    }
}

/// <summary>
/// 相似章节对
/// </summary>
public class SimilarSectionPair
{
    public string FileId1 { get; set; } = string.Empty;
    public string SectionPath1 { get; set; } = string.Empty;
    public string FileId2 { get; set; } = string.Empty;
    public string SectionPath2 { get; set; } = string.Empty;
    public float Similarity { get; set; }
}
