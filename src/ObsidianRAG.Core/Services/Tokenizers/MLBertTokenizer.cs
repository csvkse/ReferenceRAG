using Microsoft.ML.OnnxRuntime.Tensors;
using ObsidianRAG.Core.Interfaces;

namespace ObsidianRAG.Core.Services.Tokenizers;

/// <summary>
/// 基于 Microsoft.ML.Tokenizers.BertTokenizer 的高性能 BERT 分词器
/// 这是微软官方实现，性能优异，支持中文 BERT 模型
/// </summary>
public class MLBertTokenizer : ITextTokenizer
{
    private readonly Microsoft.ML.Tokenizers.BertTokenizer _tokenizer;

    public string Name => "Microsoft.ML.Tokenizers";

    public int VocabSize { get; }

    public int ClsTokenId { get; }
    public int SepTokenId { get; }
    public int PadTokenId { get; }
    public int UnkTokenId { get; }

    /// <summary>
    /// 从 vocab.txt 文件路径创建分词器
    /// </summary>
    public MLBertTokenizer(string vocabPath, Microsoft.ML.Tokenizers.BertOptions? options = null)
    {
        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");
        }

        // 设置默认选项（适用于 bge-small-zh-v1.5 等中文模型）
        options ??= new Microsoft.ML.Tokenizers.BertOptions
        {
            LowerCaseBeforeTokenization = false, // bge 模型通常不转小写
            IndividuallyTokenizeCjk = true       // 中文字符单独分词
        };

        _tokenizer = Microsoft.ML.Tokenizers.BertTokenizer.Create(vocabPath, options);
        VocabSize = CountVocabSize(vocabPath);

        // 获取特殊 token ID
        ClsTokenId = _tokenizer.ClassificationTokenId;
        SepTokenId = _tokenizer.SeparatorTokenId;
        PadTokenId = _tokenizer.PaddingTokenId;
        UnkTokenId = _tokenizer.UnknownTokenId;
    }

    /// <summary>
    /// 从目录路径创建分词器（自动查找 vocab.txt）
    /// </summary>
    public static MLBertTokenizer? CreateFromDirectory(string modelDirectory)
    {
        var vocabPath = Path.Combine(modelDirectory, "vocab.txt");
        if (File.Exists(vocabPath))
        {
            return new MLBertTokenizer(vocabPath);
        }

        return null;
    }

    /// <summary>
    /// 计算词汇表大小
    /// </summary>
    private static int CountVocabSize(string vocabPath)
    {
        var count = 0;
        foreach (var _ in File.ReadLines(vocabPath))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// 批量分词（高性能实现）
    /// </summary>
    public (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        Tokenize(List<string> texts, int maxLength)
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
                TokenizeSingle(texts[i], maxLength, inputIds, attentionMask, tokenTypeIds, i);
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
                TokenizeSingle(texts[i], maxLength, inputIds, attentionMask, tokenTypeIds, i);
            });
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// 单个文本分词
    /// </summary>
    private void TokenizeSingle(string text, int maxLength, DenseTensor<long> inputIds, DenseTensor<long> attentionMask, DenseTensor<long> tokenTypeIds, int batchIndex)
    {
        // 使用 Microsoft.ML.Tokenizers 编码
        // API: EncodeToIds(string text, int maxTokenCount, bool addSpecialTokens, out string? normalizedText, out int charsConsumed, ...)
        var encoded = _tokenizer.EncodeToIds(text, maxLength, true, out _, out _);

        // 填充张量
        for (int j = 0; j < maxLength; j++)
        {
            if (j < encoded.Count)
            {
                inputIds[batchIndex, j] = encoded[j];
                attentionMask[batchIndex, j] = 1;
            }
            else
            {
                // Padding
                inputIds[batchIndex, j] = PadTokenId;
                attentionMask[batchIndex, j] = 0;
            }
            // token_type_ids 默认为 0（单句任务）
            tokenTypeIds[batchIndex, j] = 0;
        }
    }

    /// <summary>
    /// 编码单个文本
    /// </summary>
    public IReadOnlyList<int> Encode(string text, int maxLength = 512)
    {
        // API: EncodeToIds(string text, int maxTokenCount, bool addSpecialTokens, out string? normalizedText, out int charsConsumed, ...)
        return _tokenizer.EncodeToIds(text, maxLength, true, out _, out _);
    }

    /// <summary>
    /// 计算 token 数量
    /// </summary>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // API: CountTokens(string text, bool considerPreTokenization = true, bool considerNormalization = true)
        // 注意：CountTokens 不自动添加特殊 token，需要手动 +2
        return _tokenizer.CountTokens(text) + 2; // 加上 [CLS] 和 [SEP]
    }
}
