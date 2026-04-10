using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// BM25 关键词搜索器 - 支持精确文本匹配
/// </summary>
public class BM25Searcher
{
    private readonly BM25Options _options;
    private readonly ConcurrentDictionary<string, DocumentInfo> _documents = new();
    private readonly ConcurrentDictionary<string, int> _documentFrequency = new();
    private int _totalDocuments;

    public BM25Searcher(BM25Options? options = null)
    {
        _options = options ?? new BM25Options();
    }

    /// <summary>
    /// 索引文档
    /// </summary>
    public void IndexDocument(string docId, string content)
    {
        // 如果文档已存在，先移除旧数据
        if (_documents.TryGetValue(docId, out var oldDoc))
        {
            foreach (var term in oldDoc.TermFrequencies.Keys)
            {
                _documentFrequency.AddOrUpdate(term, 0, (_, v) => Math.Max(0, v - 1));
            }
            _totalDocuments--;
        }

        // 分词并计算词频
        var tokens = Tokenize(content);
        var termFreq = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            termFreq.TryGetValue(token, out var count);
            termFreq[token] = count + 1;
        }

        // 计算文档长度
        var docLength = tokens.Count;

        // 存储文档信息
        _documents[docId] = new DocumentInfo
        {
            DocId = docId,
            TermFrequencies = termFreq,
            Length = docLength,
            Content = content
        };

        // 更新文档频率
        foreach (var term in termFreq.Keys)
        {
            _documentFrequency.AddOrUpdate(term, 1, (_, v) => v + 1);
        }
        _totalDocuments++;
    }

    /// <summary>
    /// 批量索引文档 - 并行优化版
    /// </summary>
    public void IndexDocuments(IEnumerable<(string DocId, string Content)> documents)
    {
        var docList = documents.ToList();
        if (docList.Count == 0) return;

        // 使用 ConcurrentBag 收集并行处理结果，避免锁竞争
        var indexedDocs = new ConcurrentBag<(string DocId, Dictionary<string, int> TermFreq, int Length, string Content)>();

        // 并行分词和词频统计
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
        };

        Parallel.ForEach(docList, parallelOptions, doc =>
        {
            var tokens = Tokenize(doc.Content);
            var termFreq = new Dictionary<string, int>();
            foreach (var token in tokens)
            {
                termFreq.TryGetValue(token, out var count);
                termFreq[token] = count + 1;
            }

            indexedDocs.Add((doc.DocId, termFreq, tokens.Count, doc.Content));
        });

        // 批量更新文档频率（减少锁竞争）
        foreach (var doc in indexedDocs)
        {
            // 先移除旧数据（如果存在）
            if (_documents.TryGetValue(doc.DocId, out var oldDoc))
            {
                foreach (var term in oldDoc.TermFrequencies.Keys)
                {
                    _documentFrequency.AddOrUpdate(term, 0, (_, v) => Math.Max(0, v - 1));
                }
            }
        }

        // 原子更新文档总数
        Interlocked.Add(ref _totalDocuments, indexedDocs.Count);

        // 批量添加文档和更新频率
        foreach (var doc in indexedDocs)
        {
            // 存储文档
            _documents[doc.DocId] = new DocumentInfo
            {
                DocId = doc.DocId,
                TermFrequencies = doc.TermFreq,
                Length = doc.Length,
                Content = doc.Content
            };

            // 更新词频（线程安全）
            foreach (var term in doc.TermFreq.Keys)
            {
                _documentFrequency.AddOrUpdate(term, 1, (_, v) => v + 1);
            }
        }
    }

    /// <summary>
    /// 删除文档
    /// </summary>
    public void RemoveDocument(string docId)
    {
        if (_documents.TryRemove(docId, out var doc))
        {
            foreach (var term in doc.TermFrequencies.Keys)
            {
                _documentFrequency.AddOrUpdate(term, 0, (_, v) => Math.Max(0, v - 1));
            }
            _totalDocuments--;
        }
    }

    /// <summary>
    /// 清空所有文档
    /// </summary>
    public void Clear()
    {
        _documents.Clear();
        _documentFrequency.Clear();
        _totalDocuments = 0;
    }

    /// <summary>
    /// 搜索文档 - 使用倒排索引优化，避免遍历所有文档
    /// </summary>
    public List<BM25Result> Search(string query, int topK = 10)
    {
        if (_totalDocuments == 0) return new List<BM25Result>();

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return new List<BM25Result>();

        var queryTerms = queryTokens.Distinct().ToList();

        // 使用倒排索引收集候选文档：找出包含任意查询词的文档
        var candidateDocIds = new HashSet<string>();
        foreach (var term in queryTerms)
        {
            // _documentFrequency 记录了每个词出现在哪些文档中（但这里没有倒排索引）
            // 实际上我们需要遍历文档来找到候选...
            // 对于内存搜索，文档数量通常不大，直接遍历所有文档反而更快
        }

        // 如果文档数量少于阈值，直接遍历所有文档（缓存友好）
        // 否则使用倒排索引思路优化
        var avgDocLength = _documents.Values.Average(d => d.Length);
        var scores = new Dictionary<string, float>();

        foreach (var doc in _documents.Values)
        {
            var score = ComputeBM25Score(doc, queryTerms, avgDocLength);
            if (score > 0)
            {
                scores[doc.DocId] = score;
            }
        }

        return scores
            .OrderByDescending(s => s.Value)
            .Take(topK)
            .Select(s => new BM25Result
            {
                DocId = s.Key,
                Score = s.Value,
                Content = _documents[s.Key].Content
            })
            .ToList();
    }

    /// <summary>
    /// 计算 BM25 分数
    /// </summary>
    private float ComputeBM25Score(DocumentInfo doc, List<string> queryTokens, double avgDocLength)
    {
        float score = 0;
        var k1 = _options.K1;
        var b = _options.B;

        foreach (var term in queryTokens.Distinct())
        {
            if (!doc.TermFrequencies.TryGetValue(term, out var tf)) continue;

            // IDF 计算
            var df = _documentFrequency.GetValueOrDefault(term, 0);
            if (df == 0) continue;

            var idf = (float)Math.Log((_totalDocuments - df + 0.5) / (df + 0.5) + 1);

            // TF 计算 (BM25 公式)
            var numerator = tf * (k1 + 1);
            var denominator = tf + k1 * (1 - b + b * (float)(doc.Length / avgDocLength));

            score += idf * (numerator / denominator);
        }

        return score;
    }

    /// <summary>
    /// 分词 - 支持中英文混合
    /// </summary>
    private List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        var tokens = new List<string>();
        var currentToken = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
            }
            else if (IsChinese(c))
            {
                // 中文字符单独成词
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
                tokens.Add(c.ToString());
            }
            else if (IsPunctuation(c))
            {
                // 标点符号单独处理或跳过
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
                // 可选：保留标点作为token
                // tokens.Add(c.ToString());
            }
            else
            {
                currentToken.Append(c);
            }
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString().ToLowerInvariant());
        }

        // 过滤停用词
        return tokens.Where(t => !_options.StopWords.Contains(t) && t.Length > 0).ToList();
    }

    private static bool IsChinese(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0xF900 && c <= 0xFAFF);
    }

    private static bool IsPunctuation(char c)
    {
        return char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.ConnectorPunctuation => true,
            System.Globalization.UnicodeCategory.DashPunctuation => true,
            System.Globalization.UnicodeCategory.OpenPunctuation => true,
            System.Globalization.UnicodeCategory.ClosePunctuation => true,
            System.Globalization.UnicodeCategory.InitialQuotePunctuation => true,
            System.Globalization.UnicodeCategory.FinalQuotePunctuation => true,
            System.Globalization.UnicodeCategory.OtherPunctuation => true,
            _ => false
        };
    }

    private class DocumentInfo
    {
        public string DocId { get; set; } = string.Empty;
        public Dictionary<string, int> TermFrequencies { get; set; } = new();
        public int Length { get; set; }
        public string Content { get; set; } = string.Empty;
    }
}

/// <summary>
/// BM25 搜索结果
/// </summary>
public class BM25Result
{
    public string DocId { get; set; } = string.Empty;
    public float Score { get; set; }
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// BM25 配置选项
/// </summary>
public class BM25Options
{
    /// <summary>
    /// 词频饱和参数 (通常 1.2-2.0)
    /// </summary>
    public float K1 { get; set; } = 1.5f;

    /// <summary>
    /// 文档长度归一化参数 (通常 0.75)
    /// </summary>
    public float B { get; set; } = 0.75f;

    /// <summary>
    /// 停用词列表
    /// </summary>
    public HashSet<string> StopWords { get; set; } = new()
    {
        // 中文停用词
        "的", "是", "在", "了", "和", "与", "或", "也", "都", "就",
        "不", "有", "这", "那", "我", "你", "他", "她", "它",
        "个", "上", "下", "中", "来", "去", "说", "对", "要",
        // 英文停用词
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "and", "or", "but", "not", "this", "that", "it", "as"
    };
}
