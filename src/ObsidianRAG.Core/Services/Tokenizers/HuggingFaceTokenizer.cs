using Microsoft.ML.OnnxRuntime.Tensors;
using ObsidianRAG.Core.Interfaces;
using System.Text.Json;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

namespace ObsidianRAG.Core.Services.Tokenizers;

/// <summary>
/// HuggingFace 完整分词器适配器 - 使用 tokenizers-rust 原生库加载 tokenizer.json 完整管线
/// 优先级最高，支持 normalizer / pre_tokenizer / post_processor 全链路
/// </summary>
public class HuggingFaceTokenizer : ITextTokenizer
{
    private readonly HfTokenizer _tokenizer;
    private readonly int _vocabSize;

    public string Name => "HuggingFace Tokenizer (tokenizers-rust)";
    public int VocabSize => _vocabSize;
    public int ClsTokenId { get; }
    public int SepTokenId { get; }
    public int PadTokenId { get; }
    public int UnkTokenId { get; }

    public HuggingFaceTokenizer(string tokenizerJsonPath)
    {
        _tokenizer = HfTokenizer.FromFile(tokenizerJsonPath);

        // 从 tokenizer.json 中提取特殊 token ID 和词表大小
        var (clsId, sepId, padId, unkId, vocabSize) = ParseTokenizerConfig(tokenizerJsonPath);
        ClsTokenId = clsId;
        SepTokenId = sepId;
        PadTokenId = padId;
        UnkTokenId = unkId;
        _vocabSize = vocabSize;
    }

    public (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        Tokenize(List<string> texts, int maxLength)
    {
        var batchSize = texts.Count;
        var inputIds = new DenseTensor<long>(new[] { batchSize, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxLength });

        for (int i = 0; i < batchSize; i++)
        {
            TokenizeSingle(texts[i], maxLength, inputIds, attentionMask, tokenTypeIds, i);
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private void TokenizeSingle(string text, int maxLength,
        DenseTensor<long> inputIds, DenseTensor<long> attentionMask,
        DenseTensor<long> tokenTypeIds, int batchIdx)
    {
        var result = _tokenizer.Encode(
            text,
            add_special_tokens: true,
            include_type_ids: true,
            include_attention_mask: true
        );

        var encoding = result.Encodings[0];
        var ids = encoding.Ids;
        var typeIds = encoding.TypeIds;
        var attnMask = encoding.AttentionMask;

        // 截断到 maxLength（保留 [CLS]...[SEP] 结构，HuggingFace 已处理）
        var len = Math.Min(ids.Count, maxLength);

        for (int j = 0; j < len; j++)
        {
            inputIds[batchIdx, j] = ids[j];
            attentionMask[batchIdx, j] = attnMask[j];
            tokenTypeIds[batchIdx, j] = typeIds.Count > j ? typeIds[j] : 0;
        }

        // 其余位置保持 0（padding）
    }

    public IReadOnlyList<int> Encode(string text, int maxLength = 512)
    {
        var result = _tokenizer.Encode(
            text,
            add_special_tokens: true,
            include_type_ids: false,
            include_attention_mask: false
        );

        var ids = result.Encodings[0].Ids;
        var len = Math.Min(ids.Count, maxLength);
        var output = new List<int>(len);
        for (int i = 0; i < len; i++)
        {
            output.Add((int)ids[i]);
        }
        return output;
    }

    public int CountTokens(string text)
    {
        var result = _tokenizer.Encode(
            text,
            add_special_tokens: true,
            include_type_ids: false,
            include_attention_mask: false
        );
        return result.Encodings[0].Ids.Count;
    }

    /// <summary>
    /// 从 tokenizer.json 解析特殊 token ID 和词表大小
    /// HuggingFace 库不直接暴露这些属性，需要手动解析 JSON
    /// </summary>
    private static (int clsId, int sepId, int padId, int unkId, int vocabSize) ParseTokenizerConfig(string path)
    {
        int clsId = 101, sepId = 102, padId = 0, unkId = 100, vocabSize = 21128;

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 从 added_tokens 提取特殊 token ID
            if (root.TryGetProperty("added_tokens", out var addedTokens))
            {
                foreach (var token in addedTokens.EnumerateArray())
                {
                    var content = token.TryGetProperty("content", out var c) ? c.GetString() : null;
                    var id = token.TryGetProperty("id", out var i) ? i.GetInt32() : -1;
                    if (content == null || id < 0) continue;

                    switch (content)
                    {
                        case "[CLS]": clsId = id; break;
                        case "[SEP]": sepId = id; break;
                        case "[PAD]": padId = id; break;
                        case "[UNK]": unkId = id; break;
                    }
                }
            }

            // 从 model.vocab 获取词表大小
            if (root.TryGetProperty("model", out var model) &&
                model.TryGetProperty("vocab", out var vocab))
            {
                vocabSize = vocab.EnumerateObject().Count();
            }
        }
        catch
        {
            // 解析失败使用默认值
        }

        return (clsId, sepId, padId, unkId, vocabSize);
    }
}
