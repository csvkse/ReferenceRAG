using Microsoft.ML.OnnxRuntime.Tensors;
using BERTTokenizers;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services.Tokenizers;

/// <summary>
/// 基于 BERTTokenizers 的高性能 BERT 分词器
/// 支持自定义词汇表，适用于 bge-small-zh-v1.5 等中文模型
/// </summary>
public class BertTokenizerWrapper : ITextTokenizer
{
    private readonly BertCasedCustomVocabulary _tokenizer;
    private readonly int _vocabSize;

    public string Name => "BERTTokenizers";
    public int VocabSize => _vocabSize;

    // BERT 特殊 token ID（标准值）
    public int ClsTokenId => 101;
    public int SepTokenId => 102;
    public int PadTokenId => 0;
    public int UnkTokenId => 100;

    /// <summary>
    /// 从 vocab.txt 文件创建分词器
    /// </summary>
    public BertTokenizerWrapper(string vocabPath)
    {
        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"Vocabulary file not found: {vocabPath}");
        }

        _tokenizer = new BertCasedCustomVocabulary(vocabPath);
        _vocabSize = CountVocabSize(vocabPath);
    }

    /// <summary>
    /// 从目录路径创建分词器（自动查找 vocab.txt）
    /// </summary>
    public static BertTokenizerWrapper? CreateFromDirectory(string modelDirectory)
    {
        var vocabPath = Path.Combine(modelDirectory, "vocab.txt");
        if (File.Exists(vocabPath))
        {
            return new BertTokenizerWrapper(vocabPath);
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

        if (trimToActualLength)
            return TokenizerUtils.TrimToActualLength(inputIds, attentionMask, tokenTypeIds, batchSize, maxLength);
        return (inputIds, attentionMask, tokenTypeIds);
    }

    /// <summary>
    /// 单个文本分词
    /// </summary>
    private void TokenizeSingle(string text, int maxLength, DenseTensor<long> inputIds, DenseTensor<long> attentionMask, DenseTensor<long> tokenTypeIds, int batchIndex)
    {
        // 使用 BERTTokenizers 编码
        var encoded = _tokenizer.Encode(maxLength, text);

        // 转换为张量
        for (int j = 0; j < maxLength; j++)
        {
            if (j < encoded.Count)
            {
                inputIds[batchIndex, j] = encoded[j].InputIds;
                attentionMask[batchIndex, j] = encoded[j].AttentionMask;
                tokenTypeIds[batchIndex, j] = encoded[j].TokenTypeIds;
            }
            else
            {
                // Padding
                inputIds[batchIndex, j] = PadTokenId;
                attentionMask[batchIndex, j] = 0;
                tokenTypeIds[batchIndex, j] = 0;
            }
        }
    }

    /// <summary>
    /// 编码单个文本
    /// </summary>
    public IReadOnlyList<int> Encode(string text, int maxLength = 512)
    {
        var encoded = _tokenizer.Encode(maxLength, text);
        return encoded.Select(e => (int)e.InputIds).ToList();
    }

    /// <summary>
    /// 计算 token 数量
    /// </summary>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var tokens = _tokenizer.Tokenize(text);
        // 加上 [CLS] 和 [SEP]
        return tokens.Count + 2;
    }
}
