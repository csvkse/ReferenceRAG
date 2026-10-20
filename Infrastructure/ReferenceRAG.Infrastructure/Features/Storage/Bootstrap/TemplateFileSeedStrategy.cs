namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

using System;
using System.Text.Json.Nodes;

/// <summary>
/// 伴生模板文件种子生成策略（优先级 1）
/// 从发布的 appsettings.template.json 复制完整配置结构与注释
/// </summary>
public class TemplateFileSeedStrategy : IConfigSeedStrategy
{
    public string Name => "TemplateFileSeed";
    public int Priority => 1;

    public bool CanSeed(IConfigEnvironmentAdapter adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.TemplatePath)) return false;
        if (!adapter.FileExists(adapter.TemplatePath)) return false;
        return adapter.GetFileSize(adapter.TemplatePath) > 10;
    }

    public string GenerateConfigJson(IConfigEnvironmentAdapter adapter)
    {
        var templateContent = adapter.ReadAllText(adapter.TemplatePath);

        // 校验模板是否能被解析为合法 JSON 对象
        try
        {
            var node = JsonNode.Parse(templateContent)?.AsObject();
            if (node != null)
            {
                // 确保标注了最新的 schemaVersion 2
                if (node["ReferenceRAG"] is JsonObject ragObj)
                {
                    ragObj["schemaVersion"] = 2;
                }
                return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
        }
        catch
        {
            // 若模板损坏则由下一个策略接管
        }

        return templateContent;
    }
}
