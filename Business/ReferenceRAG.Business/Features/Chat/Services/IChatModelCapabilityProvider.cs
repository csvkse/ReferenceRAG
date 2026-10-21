namespace ReferenceRAG.Business.Features.Chat.Services;

/// <summary>
/// 聊天模型能力元数据模型
/// 明确声明当前对话后端的视觉多模态、工具调用、上下文容量等核心能力。
/// </summary>
public record ChatModelCapabilities(
    bool SupportsVision,
    bool SupportsToolCalling,
    string ModelName,
    string BackendType,
    int MaxContextTokens,
    string? Description = null,
    string? VisionUnsupportedReason = null,
    string VisionStatus = "TextOnly",
    string? MatchedProjectorPath = null,
    string? GuidanceMessage = null);

/// <summary>
/// 聊天模型能力提供者接口（Capability Provider Pattern）
/// 为上层服务、API 控制器以及前端 UI 提供动态能力检测与行为适配。
/// </summary>
public interface IChatModelCapabilityProvider
{
    ChatModelCapabilities GetCapabilities();
}
