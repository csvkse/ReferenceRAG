using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：AI 对话视图单气泡流式响应完整性门禁
/// 防止再次出现“回复时产生两个卡片（一个空文本光标卡片 + 一个独立外部思考中卡片）的双 Agent 回复框”问题。
/// </summary>
public sealed class ChatViewBubbleIntegrityGateTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到代码仓根目录 (ReferenceRAG.slnx)");
    }

    private static string GetChatJsPath()
    {
        var root = FindRepoRoot();
        var chatPath = Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "app", "views", "Chat.js");
        Assert.True(File.Exists(chatPath), $"Chat.js 未找到: {chatPath}");
        return chatPath;
    }

    [Fact]
    public void ChatView_TemplateMustNotContain_SeparateExternalThinkingCard()
    {
        var chatPath = GetChatJsPath();
        var content = File.ReadAllText(chatPath);

        // 门禁规则 1：严禁在消息列表循环外部单独挂载独立的 Thinking indicator 卡片，
        // 任何外部卡片都会在生成未开始时与 assistantMsg 占位框同时渲染，造成“双框”现象！
        Assert.DoesNotContain("<!-- Thinking indicator -->", content);
    }

    [Fact]
    public void ChatView_AssistantCard_EmbedsThinkingStateInsideSingleBubble()
    {
        var chatPath = GetChatJsPath();
        var content = File.ReadAllText(chatPath);

        // 门禁规则 2：思考中动画必须内聚在 assistant 卡片内部，
        // 且仅当处于当前轮次流式中且尚未吐出任何文字时 (streaming && i === messages.length - 1 && !msg.content) 展示
        Assert.Contains("streaming && i === messages.length - 1 && !msg.content", content);
        Assert.Contains("思考中...", content);
    }

    [Fact]
    public void ChatView_RenderMsg_MustNotAppendCursorWhenContentIsEmpty()
    {
        var chatPath = GetChatJsPath();
        var content = File.ReadAllText(chatPath);

        // 门禁规则 3：在内容为空时不应追加闪烁光标 ▌，避免在思考阶段出现孤立光标空卡片
        Assert.Contains("isCurrentStreaming && msg.content ? ' ▌' : ''", content);
    }

    [Fact]
    public void ChatView_TemplateTags_MustBeStrictlyBalanced()
    {
        var chatPath = GetChatJsPath();
        var content = File.ReadAllText(chatPath);

        const string prefix = "component.template = \"";
        var start = content.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;

        const string suffix = "\";\ncomponent.__scopeId";
        var end = content.LastIndexOf(suffix, StringComparison.Ordinal);
        Assert.True(end >= 0);

        var rawTmplJson = "\"" + content.Substring(start, end - start) + "\"";
        var tmpl = JsonSerializer.Deserialize<string>(rawTmplJson);
        Assert.NotNull(tmpl);

        var divOpen = Regex.Matches(tmpl, @"<div[\s>]").Count;
        var divClose = Regex.Matches(tmpl, @"</div>").Count;
        Assert.Equal(divOpen, divClose);

        var ncardOpen = Regex.Matches(tmpl, @"<n-card[\s>]").Count;
        var ncardClose = Regex.Matches(tmpl, @"</n-card>").Count;
        Assert.Equal(ncardOpen, ncardClose);

        var nspaceOpen = Regex.Matches(tmpl, @"<n-space[\s>]").Count;
        var nspaceClose = Regex.Matches(tmpl, @"</n-space>").Count;
        Assert.Equal(nspaceOpen, nspaceClose);
    }
}
