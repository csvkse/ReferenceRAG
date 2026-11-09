using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Business.Features.Search.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class DppKernelDiversitySelectorTests
{
    private record TestChunk(string Id, string Content, double Score);

    [Fact]
    public void Diversify_HandlesEmptyOrSingleItemGracefully()
    {
        var empty = DppKernelDiversitySelector.Diversify(
            new List<TestChunk>(), c => c.Content, c => c.Score, targetK: 5);
        Assert.Empty(empty);

        var single = new List<TestChunk> { new("1", "单个分块", 0.9) };
        var singleRes = DppKernelDiversitySelector.Diversify(
            single, c => c.Content, c => c.Score, targetK: 5);
        Assert.Single(singleRes);
    }

    [Fact]
    public void Diversify_PenalizesDuplicateNearIdenticalChunks()
    {
        // 模拟 3 个几乎完全一样的切片和 2 个完全不同主题的切片
        var candidates = new List<TestChunk>
        {
            new("dup1", "武当派掌门人张三丰在武当山闭关修炼太极拳神功太极剑法", 0.98),
            new("dup2", "武当派掌门人张三丰在武当山闭关修炼太极拳神功太极剑法精妙绝伦", 0.97),
            new("dup3", "武当派张三丰在武当山闭关修炼太极拳神功太极剑法", 0.96),
            new("diff1", "华山派令狐冲在思过崖从风清扬学得独孤九剑破剑式", 0.85),
            new("diff2", "全真派王重阳在终南山创立全真教与九阴真经", 0.80)
        };

        var selected = DppKernelDiversitySelector.Diversify(
            candidates, c => c.Content, c => c.Score, targetK: 3);

        Assert.Equal(3, selected.Count);

        // 第一个应该是最高分 dup1
        Assert.Equal("dup1", selected[0].Id);

        // 另外两个名额中，应该优先选入语义差异大的 diff1 或 diff2，而不是把 3 个全占满同一重复内容
        var selectedIds = selected.Select(s => s.Id).ToList();
        Assert.True(selectedIds.Contains("diff1") || selectedIds.Contains("diff2"),
            "DPP 应当抑制与第一名高度重复的 dup2/dup3，提拔具有不同语义信息的候选项");
    }
}
