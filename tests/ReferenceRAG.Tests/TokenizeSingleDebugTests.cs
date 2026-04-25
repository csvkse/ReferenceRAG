using Microsoft.ML.OnnxRuntime.Tensors;
using ReferenceRAG.Core.Services.Tokenizers;
using Xunit;
using static Xunit.Skip;

namespace ReferenceRAG.Tests;

/// <summary>
/// 诊断 TokenizeSingle 的位置偏移问题
/// </summary>
public class TokenizeSingleDebugTests
{
    private static readonly string? TokenizerPath = ResolveTokenizerPath();

    private static string? ResolveTokenizerPath()
    {
        var paths = new[]
        {
            "E:/LinuxWork/Obsidian/resource/data/models/bge-small-zh-v1.5/tokenizer.json",
            Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..",
                "resource", "data", "models", "bge-small-zh-v1.5", "tokenizer.json")
        };

        return paths.FirstOrDefault(File.Exists);
    }

    [SkippableFact]
    public void Debug_EnglishWordPiece_Positions()
    {
        Skip.If(TokenizerPath == null, "Tokenizer 文件不存在，跳过测试");

        var tokenizer = new BertTokenizer(TokenizerPath!);

        var text = "how to improve programming skills";
        var result = tokenizer.Tokenize(new List<string> { text }, 512);

        var actualLen = result.InputIds.Dimensions[1]; // TrimToActualLength 可能已裁剪
        var ids = new List<long>();
        var masks = new List<long>();
        for (int j = 0; j < actualLen; j++)
        {
            ids.Add(result.InputIds[0, j]);
            masks.Add(result.AttentionMask[0, j]);
        }

        // 找到 attention_mask 的最后一个 1（裁剪后所有位置均为有效 token）
        int lastTokenPos = actualLen - 1;
        for (int j = 0; j < actualLen; j++)
        {
            if (masks[j] == 0) { lastTokenPos = j - 1; break; }
        }

        var activeIds = ids.Take(lastTokenPos + 1).ToList();
        var output = string.Join(", ", activeIds);
        
        // HuggingFace expected: [101, 9510, 8228, 10481, 10538, 8519, 11738, 10693, 9820, 9868, 8118, 102]
        var expected = new List<long> { 101, 9510, 8228, 10481, 10538, 8519, 11738, 10693, 9820, 9868, 8118, 102 };
        
        Assert.True(activeIds.SequenceEqual(expected), 
            $"Mismatch for \"{text}\":\n  expected: [{string.Join(", ", expected)}]\n  actual:   [{output}]\n  len: expected={expected.Count}, actual={activeIds.Count}");
    }

    [SkippableFact]
    public void Debug_SingleWord_Skills()
    {
        Skip.If(TokenizerPath == null, "Tokenizer 文件不存在，跳过测试");

        var tokenizer = new BertTokenizer(TokenizerPath!);

        var text = "skills";
        var result = tokenizer.Tokenize(new List<string> { text }, 512);

        var actualLen = result.InputIds.Dimensions[1]; // TrimToActualLength 可能已裁剪
        var ids = new List<long>();
        for (int j = 0; j < Math.Min(20, actualLen); j++)
        {
            if (result.AttentionMask[0, j] == 0) break;
            ids.Add(result.InputIds[0, j]);
        }

        // HuggingFace: [101, 9820, 9868, 8118, 102] → CLS sk ##ill ##s SEP
        var expected = new List<long> { 101, 9820, 9868, 8118, 102 };
        
        Assert.True(ids.SequenceEqual(expected),
            $"Mismatch for \"{text}\":\n  expected: [{string.Join(", ", expected)}]\n  actual:   [{string.Join(", ", ids)}]");
    }
}
