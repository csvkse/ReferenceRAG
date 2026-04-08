using Microsoft.ML.OnnxRuntime.Tensors;

namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 文本分词器接口 - 用于将文本转换为模型输入张量
/// </summary>
public interface ITextTokenizer
{
    /// <summary>
    /// 分词器名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 词汇表大小
    /// </summary>
    int VocabSize { get; }

    /// <summary>
    /// [CLS] Token ID
    /// </summary>
    int ClsTokenId { get; }

    /// <summary>
    /// [SEP] Token ID
    /// </summary>
    int SepTokenId { get; }

    /// <summary>
    /// [PAD] Token ID
    /// </summary>
    int PadTokenId { get; }

    /// <summary>
    /// [UNK] Token ID
    /// </summary>
    int UnkTokenId { get; }

    /// <summary>
    /// 对文本列表进行分词，返回模型输入张量
    /// </summary>
    /// <param name="texts">待分词的文本列表</param>
    /// <param name="maxLength">最大序列长度</param>
    /// <returns>包含 input_ids, attention_mask, token_type_ids 的元组</returns>
    (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        Tokenize(List<string> texts, int maxLength);

    /// <summary>
    /// 对单个文本进行分词，返回 token ID 列表
    /// </summary>
    /// <param name="text">待分词的文本</param>
    /// <param name="maxLength">最大序列长度</param>
    /// <returns>token ID 列表</returns>
    IReadOnlyList<int> Encode(string text, int maxLength = 512);

    /// <summary>
    /// 计算 token 数量（用于估算）
    /// </summary>
    /// <param name="text">文本</param>
    /// <returns>token 数量</returns>
    int CountTokens(string text);
}
