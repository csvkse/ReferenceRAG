using Microsoft.ML.OnnxRuntime.Tensors;
using System.Collections.Concurrent;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services.Tokenizers;

/// <summary>
/// 自定义 BERT Tokenizer（基于 HuggingFace tokenizer.json）
/// 实现了 ITextTokenizer 接口，作为 Microsoft.ML.Tokenizers 的备用方案
/// </summary>
public class BertTokenizer : ITextTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly string _unkToken;
    private readonly string _clsToken;
    private readonly string _sepToken;
    private readonly string _padToken;
    private readonly bool _doLowerCase;

    public string Name => $"Custom BertTokenizer ({(_doLowerCase ? "cased" : "uncased")})";

    public int VocabSize => _vocab.Count;

    // 预缓存的特殊 token ID
    public int ClsTokenId { get; }
    public int SepTokenId { get; }
    public int PadTokenId { get; }
    public int UnkTokenId { get; }

    // 常用 token 缓存（单字符、双字符）- 使用 ConcurrentDictionary 保证线程安全
    private readonly ConcurrentDictionary<int, int> _singleCharCache = new();
    private readonly ConcurrentDictionary<long, int> _doubleCharCache = new();

    public BertTokenizer(string tokenizerPath)
    {
        var json = File.ReadAllText(tokenizerPath);
        var config = JsonSerializer.Deserialize<TokenizerConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        _vocab = config?.Model?.Vocab ?? new Dictionary<string, int>();
        _unkToken = config?.UnkToken ?? "[UNK]";
        _clsToken = config?.ClsToken ?? "[CLS]";
        _sepToken = config?.SepToken ?? "[SEP]";
        _padToken = config?.PadToken ?? "[PAD]";

        // 从 tokenizer_config.json 读取 do_lower_case 配置
        var configDir = Path.GetDirectoryName(tokenizerPath) ?? "";
        var configPath = Path.Combine(configDir, "tokenizer_config.json");
        _doLowerCase = ReadDoLowerCase(configPath);
        Console.WriteLine($"[BertTokenizer] do_lower_case={_doLowerCase}");

        // 预缓存特殊 token ID
        ClsTokenId = GetTokenId(_clsToken);
        SepTokenId = GetTokenId(_sepToken);
        UnkTokenId = GetTokenId(_unkToken);
        PadTokenId = GetTokenId(_padToken);

        // 预缓存常用 token（ASCII 字符和常见双字符组合）
        PreCacheCommonTokens();
    }

    /// <summary>
    /// 从 tokenizer_config.json 读取 do_lower_case
    /// </summary>
    private static bool ReadDoLowerCase(string configPath)
    {
        if (!File.Exists(configPath)) return true; // 默认 lowercased（自定义分词器预分词逻辑与 HuggingFace 不同，需要 lowercasing 来匹配 vocab）
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("do_lower_case", out var el))
            {
                // HuggingFace do_lower_case=false 的完整行为依赖 BasicTokenizer 的 Unicode 处理，
                // 自定义分词器没有实现该逻辑，强制使用 lowercasing 以确保 vocab 匹配
                return true;
            }
        }
        catch { /* 忽略解析错误，使用默认值 */ }
        return true;
    }

    /// <summary>
    /// 预缓存常用 token 以加速查找
    /// </summary>
    private void PreCacheCommonTokens()
    {
        // 缓存 ASCII 字符
        for (int i = 32; i < 127; i++)
        {
            var key = (char)i;
            var lookupKey = _doLowerCase ? char.ToLower(key) : key;
            if (_vocab.TryGetValue(lookupKey.ToString(), out var id))
            {
                _singleCharCache.TryAdd(i, id);
            }
        }
    }

    /// <summary>
    /// 批量分词
    /// </summary>
    public (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        Tokenize(List<string> texts, int maxLength, bool trimToActualLength = true)
    {
        var batchSize = texts.Count;
        if (batchSize == 0)
        {
            return (new DenseTensor<long>(new[] { 0, maxLength }),
                    new DenseTensor<long>(new[] { 0, maxLength }),
                    new DenseTensor<long>(new[] { 0, maxLength }));
        }

        var inputIds = new DenseTensor<long>(new[] { batchSize, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxLength });

        // 小批量顺序处理
        if (batchSize < 8)
        {
            for (int i = 0; i < batchSize; i++)
            {
                TokenizeSingle(texts[i], maxLength, inputIds, attentionMask, i);
            }
        }
        else
        {
            // 大批量并行处理
            Parallel.For(0, batchSize, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
            }, i =>
            {
                TokenizeSingle(texts[i], maxLength, inputIds, attentionMask, i);
            });
        }

        if (trimToActualLength)
            return TokenizerUtils.TrimToActualLength(inputIds, attentionMask, tokenTypeIds, batchSize, maxLength);
        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// 单个文本 tokenize（线程安全，低分配）
    /// </summary>
    private void TokenizeSingle(string text, int maxLength, DenseTensor<long> inputIds, DenseTensor<long> attentionMask, int batchIndex)
    {
        ReadOnlySpan<char> textSpan = text.AsSpan();
        var tokenCount = 0;
        var maxTokens = maxLength - 2; // 预留 [CLS] 和 [SEP]

        // [CLS]
        inputIds[batchIndex, 0] = ClsTokenId;
        attentionMask[batchIndex, 0] = 1;

        // 使用栈上缓冲区处理当前 token
        Span<char> tokenBuffer = stackalloc char[256];
        Span<char> singleCharBuffer = stackalloc char[1];
        var bufferPos = 0;

        var prevWasChinese = false;

        foreach (var c in textSpan)
        {
            if (tokenCount >= maxTokens) break;

            var isChinese = IsChinese(c);

            if (char.IsWhiteSpace(c))
            {
                if (bufferPos > 0)
                {
                    tokenCount += AddToken(tokenBuffer[..bufferPos], inputIds, attentionMask, batchIndex, tokenCount);
                    bufferPos = 0;
                }
                prevWasChinese = false;
            }
            else if (isChinese)
            {
                // CJK 字符前 flush 非 CJK buffer（对应 HuggingFace tokenize_chinese_chars 插入空格）
                if (bufferPos > 0)
                {
                    tokenCount += AddToken(tokenBuffer[..bufferPos], inputIds, attentionMask, batchIndex, tokenCount);
                    bufferPos = 0;
                }
                // CJK 字符单独成 token
                singleCharBuffer[0] = c;
                tokenCount += AddToken(singleCharBuffer, inputIds, attentionMask, batchIndex, tokenCount);
                prevWasChinese = true;
            }
            else if (IsPunctuation(c))
            {
                // 标点字符单独成 token（与 HuggingFace BasicTokenizer 对齐）
                // 例如 C++ → [c, +, +], 3.5 → [3, ., 5]
                if (bufferPos > 0)
                {
                    tokenCount += AddToken(tokenBuffer[..bufferPos], inputIds, attentionMask, batchIndex, tokenCount);
                    bufferPos = 0;
                }
                singleCharBuffer[0] = _doLowerCase ? char.ToLower(c) : c;
                tokenCount += AddToken(singleCharBuffer, inputIds, attentionMask, batchIndex, tokenCount);
                prevWasChinese = false;
            }
            else
            {
                // 非 CJK 字符紧跟 CJK 字符时，先 flush（对应 HuggingFace 在 CJK 后插入空格）
                if (prevWasChinese)
                {
                    if (bufferPos > 0)
                    {
                        tokenCount += AddToken(tokenBuffer[..bufferPos], inputIds, attentionMask, batchIndex, tokenCount);
                        bufferPos = 0;
                    }
                }
                if (bufferPos < tokenBuffer.Length)
                {
                    tokenBuffer[bufferPos++] = _doLowerCase ? char.ToLower(c) : c;
                }
                prevWasChinese = false;
            }
        }

        // 处理剩余的 token
        if (bufferPos > 0 && tokenCount < maxTokens)
        {
            tokenCount += AddToken(tokenBuffer[..bufferPos], inputIds, attentionMask, batchIndex, tokenCount);
        }

        // [SEP]
        inputIds[batchIndex, tokenCount + 1] = SepTokenId;
        attentionMask[batchIndex, tokenCount + 1] = 1;
    }

    /// <summary>
    /// 添加 token 到 tensor（支持 WordPiece 子词分词）
    /// </summary>
    private int AddToken(ReadOnlySpan<char> token, DenseTensor<long> inputIds, DenseTensor<long> attentionMask, int batchIndex, int position)
    {
        if (position >= inputIds.Dimensions[1] - 2) return 0;

        // 先尝试完整匹配
        var tokenId = GetTokenIdFastSpan(token);
        if (tokenId != UnkTokenId)
        {
            inputIds[batchIndex, position + 1] = tokenId;
            attentionMask[batchIndex, position + 1] = 1;
            return 1;
        }

        // 未找到，使用 WordPiece 子词分解
        return AddTokenWithWordPiece(token, inputIds, attentionMask, batchIndex, position);
    }

    /// <summary>
    /// WordPiece 子词分词（贪婪最长匹配，带安全边界检查）
    /// </summary>
    private int AddTokenWithWordPiece(ReadOnlySpan<char> token, DenseTensor<long> inputIds, DenseTensor<long> attentionMask, int batchIndex, int position)
    {
        var maxPosition = inputIds.Dimensions[1] - 2;
        var addedCount = 0;
        var start = 0;

        var maxSubwords = Math.Min(10, maxPosition - position);
        Span<char> subwordBuffer = stackalloc char[128];

        while (start < token.Length && addedCount < maxSubwords)
        {
            var found = false;
            for (var end = token.Length; end > start; end--)
            {
                var length = end - start;
                if (length == 0 || length > 64) continue;

                if (addedCount == 0)
                {
                    for (int i = 0; i < length; i++)
                        subwordBuffer[i] = _doLowerCase ? char.ToLower(token[start + i]) : token[start + i];

                    var subId = GetTokenIdFastSpan(subwordBuffer[..length]);
                    if (subId != UnkTokenId)
                    {
                        var pos = position + addedCount + 1;
                        if (pos >= maxPosition) break;

                        inputIds[batchIndex, pos] = subId;
                        attentionMask[batchIndex, pos] = 1;
                        addedCount++;
                        start = end;
                        found = true;
                        break;
                    }
                }
                else
                {
                    subwordBuffer[0] = '#';
                    subwordBuffer[1] = '#';
                    for (int i = 0; i < length; i++)
                        subwordBuffer[i + 2] = _doLowerCase ? char.ToLower(token[start + i]) : token[start + i];

                    var subId = GetTokenIdFastSpan(subwordBuffer[..(length + 2)]);
                    if (subId != UnkTokenId)
                    {
                        var pos = position + addedCount + 1;
                        if (pos >= maxPosition) break;

                        inputIds[batchIndex, pos] = subId;
                        attentionMask[batchIndex, pos] = 1;
                        addedCount++;
                        start = end;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                start++;
            }
        }

        if (addedCount == 0 && position + 1 < maxPosition)
        {
            inputIds[batchIndex, position + 1] = UnkTokenId;
            attentionMask[batchIndex, position + 1] = 1;
            return 1;
        }

        return addedCount;
    }

    /// <summary>
    /// 快速获取 token ID（使用缓存优化）
    /// </summary>
    private int GetTokenIdFastSpan(ReadOnlySpan<char> token)
    {
        if (token.Length == 1)
        {
            var c = token[0];
            var lookupChar = _doLowerCase ? char.ToLower(c) : c;
            var key = (int)lookupChar;

            if (_singleCharCache.TryGetValue(key, out var cachedId))
            {
                return cachedId;
            }

            var tokenStr = lookupChar.ToString();
            if (_vocab.TryGetValue(tokenStr, out var id))
            {
                _singleCharCache.TryAdd(key, id);
                return id;
            }

            return UnkTokenId;
        }

        if (token.Length == 2)
        {
            var c0 = _doLowerCase ? char.ToLower(token[0]) : token[0];
            var c1 = _doLowerCase ? char.ToLower(token[1]) : token[1];
            var key = ((long)c0 << 16) | c1;

            if (_doubleCharCache.TryGetValue(key, out var cachedId))
            {
                return cachedId;
            }

            var tokenStr = new string(new[] { c0, c1 });

            if (_vocab.TryGetValue(tokenStr, out var id))
            {
                _doubleCharCache.TryAdd(key, id);
                return id;
            }

            return UnkTokenId;
        }

        string str;
        if (_doLowerCase)
        {
            str = token.Length <= 256
                ? string.Create(token.Length, token, (span, src) =>
                {
                    for (int i = 0; i < src.Length; i++)
                        span[i] = char.ToLower(src[i]);
                })
                : token.ToString().ToLower();
        }
        else
        {
            str = token.ToString();
        }

        if (_vocab.TryGetValue(str, out var longId))
        {
            return longId;
        }

        return UnkTokenId;
    }

    /// <summary>
    /// 编码单个文本
    /// </summary>
    public IReadOnlyList<int> Encode(string text, int maxLength = 512)
    {
        var result = new List<int>(maxLength) { ClsTokenId };

        ReadOnlySpan<char> textSpan = text.AsSpan();
        Span<char> tokenBuffer = stackalloc char[256];
        var bufferPos = 0;
        var prevWasChinese = false;

        foreach (var c in textSpan)
        {
            if (result.Count >= maxLength - 1) break;

            var isChinese = IsChinese(c);

            if (char.IsWhiteSpace(c))
            {
                if (bufferPos > 0)
                {
                    AddTokensToList(tokenBuffer[..bufferPos], result, maxLength);
                    bufferPos = 0;
                }
                prevWasChinese = false;
            }
            else if (isChinese)
            {
                if (bufferPos > 0)
                {
                    AddTokensToList(tokenBuffer[..bufferPos], result, maxLength);
                    bufferPos = 0;
                }
                var id = GetTokenIdFastSpan(stackalloc[] { c });
                if (result.Count < maxLength - 1)
                {
                    result.Add(id);
                }
                prevWasChinese = true;
            }
            else if (IsPunctuation(c))
            {
                if (bufferPos > 0)
                {
                    AddTokensToList(tokenBuffer[..bufferPos], result, maxLength);
                    bufferPos = 0;
                }
                var id = GetTokenIdFastSpan(stackalloc[] { _doLowerCase ? char.ToLower(c) : c });
                if (result.Count < maxLength - 1)
                {
                    result.Add(id);
                }
                prevWasChinese = false;
            }
            else
            {
                if (prevWasChinese && bufferPos > 0)
                {
                    AddTokensToList(tokenBuffer[..bufferPos], result, maxLength);
                    bufferPos = 0;
                }
                if (bufferPos < tokenBuffer.Length)
                {
                    tokenBuffer[bufferPos++] = _doLowerCase ? char.ToLower(c) : c;
                }
                prevWasChinese = false;
            }
        }

        if (bufferPos > 0 && result.Count < maxLength - 1)
        {
            AddTokensToList(tokenBuffer[..bufferPos], result, maxLength);
        }

        result.Add(SepTokenId);
        return result;
    }

    private void AddTokensToList(ReadOnlySpan<char> token, List<int> result, int maxLength)
    {
        var id = GetTokenIdFastSpan(token);
        if (id != UnkTokenId)
        {
            if (result.Count < maxLength - 1)
            {
                result.Add(id);
            }
        }
        else
        {
            // WordPiece 分解
            var start = 0;
            var isFirst = true;
            // 预分配最大缓冲区（64 字符 + 2 个 ## 前缀）
            Span<char> subwordBuffer = stackalloc char[66];
            while (start < token.Length && result.Count < maxLength - 1)
            {
                var found = false;
                for (var end = token.Length; end > start; end--)
                {
                    var length = end - start;
                    if (length == 0 || length > 64) continue;

                    var subword = isFirst ? subwordBuffer[..length] : subwordBuffer[..(length + 2)];
                    if (isFirst)
                    {
                        for (int i = 0; i < length; i++)
                            subword[i] = _doLowerCase ? char.ToLower(token[start + i]) : token[start + i];
                    }
                    else
                    {
                        subword[0] = '#';
                        subword[1] = '#';
                        for (int i = 0; i < length; i++)
                            subword[i + 2] = _doLowerCase ? char.ToLower(token[start + i]) : token[start + i];
                    }

                    var subId = GetTokenIdFastSpan(subword);
                    if (subId != UnkTokenId)
                    {
                        result.Add(subId);
                        start = end;
                        isFirst = false;
                        found = true;
                        break;
                    }
                }
                if (!found) start++;
            }
        }
    }

    /// <summary>
    /// 计算 token 数量
    /// </summary>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Encode(text).Count;
    }

    /// <summary>
    /// 判断字符是否为 CJK 字符（与 HuggingFace BasicTokenizer._is_chinese_char 对齐）
    /// 注意：仅包含汉字，不含标点。CJK 标点由 IsPunctuation 处理。
    /// </summary>
    private static bool IsChinese(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK Unified Ideographs
               (c >= 0x3400 && c <= 0x4DBF) ||   // CJK Unified Ideographs Extension A
               (c >= 0xF900 && c <= 0xFAFF);      // CJK Compatibility Ideographs
    }

    /// <summary>
    /// 判断字符是否为标点（与 HuggingFace BasicTokenizer._is_punctuation 对齐）
    /// 标点字符会在预分词阶段被单独切分
    /// </summary>
    private static bool IsPunctuation(char c)
    {
        // ASCII 标点范围
        if ((c >= 33 && c <= 47) ||    // !"#$%&'()*+,-./
            (c >= 58 && c <= 64) ||    // :;<=>?@
            (c >= 91 && c <= 96) ||    // [\]^_`
            (c >= 123 && c <= 126))    // {|}~
        {
            return true;
        }

        // Unicode 标点类别
        var cat = char.GetUnicodeCategory(c);
        return cat == System.Globalization.UnicodeCategory.ConnectorPunctuation ||
               cat == System.Globalization.UnicodeCategory.DashPunctuation ||
               cat == System.Globalization.UnicodeCategory.OpenPunctuation ||
               cat == System.Globalization.UnicodeCategory.ClosePunctuation ||
               cat == System.Globalization.UnicodeCategory.InitialQuotePunctuation ||
               cat == System.Globalization.UnicodeCategory.FinalQuotePunctuation ||
               cat == System.Globalization.UnicodeCategory.OtherPunctuation;
    }

    private int GetTokenId(string token)
    {
        if (_vocab.TryGetValue(token, out var id))
        {
            return id;
        }
        return UnkTokenId;
    }
}

/// <summary>
/// Tokenizer 配置
/// </summary>
internal class TokenizerConfig
{
    public TokenizerModel? Model { get; set; }
    public string? UnkToken { get; set; }
    public string? ClsToken { get; set; }
    public string? SepToken { get; set; }
    public string? PadToken { get; set; }
}

internal class TokenizerModel
{
    public Dictionary<string, int>? Vocab { get; set; }
}

/// <summary>
/// 共享工具：裁剪 token tensor 到批次实际最大序列长度。
/// 长序列模型（如 BGE M3 maxLen=8192）对短文本补零到 8192 会使 ONNX 推理慢 16x。
/// </summary>
internal static class TokenizerUtils
{
    internal static (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        TrimToActualLength(DenseTensor<long> inputIds, DenseTensor<long> attentionMask, DenseTensor<long> tokenTypeIds,
                           int batchSize, int maxLength)
    {
        int actualMaxLen = 0;
        for (int i = 0; i < batchSize; i++)
        {
            for (int j = maxLength - 1; j >= 0; j--)
            {
                if (attentionMask[i, j] != 0)
                {
                    if (j + 1 > actualMaxLen) actualMaxLen = j + 1;
                    break;
                }
            }
        }
        if (actualMaxLen == 0) actualMaxLen = 1;
        if (actualMaxLen >= maxLength) return (inputIds, attentionMask, tokenTypeIds);

        var tIds   = new DenseTensor<long>(new[] { batchSize, actualMaxLen });
        var tMask  = new DenseTensor<long>(new[] { batchSize, actualMaxLen });
        var tTypes = new DenseTensor<long>(new[] { batchSize, actualMaxLen });

        for (int i = 0; i < batchSize; i++)
            for (int j = 0; j < actualMaxLen; j++)
            {
                tIds[i, j]   = inputIds[i, j];
                tMask[i, j]  = attentionMask[i, j];
                tTypes[i, j] = tokenTypeIds[i, j];
            }

        return (tIds, tMask, tTypes);
    }
}
