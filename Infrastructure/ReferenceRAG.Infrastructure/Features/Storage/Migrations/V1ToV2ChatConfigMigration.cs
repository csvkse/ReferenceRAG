namespace ReferenceRAG.Infrastructure.Features.Storage.Migrations;

using System;
using System.Text.Json.Nodes;

/// <summary>
/// SchemaVersion 1 -> 2:
/// 1. 将顶层遗留 "Chat" 节点（Endpoint, ApiKey, Model, SystemPrompt）收敛合并至 ReferenceRAG.chat.openAiApi。
/// 2. 彻底移除顶层遗留 "Chat" 节点。
/// 3. 清理 ReferenceRAG.chat 下历史代理属性序列化残留的多余顶层同名字段（endpoint, apiKey, model）。
/// 4. 统一打上 schemaVersion: 2 标记。
/// </summary>
public class V1ToV2ChatConfigMigration : IConfigMigration
{
    public int TargetVersion => 2;

    public bool Migrate(JsonObject root)
    {
        bool changed = false;

        // 确保 ReferenceRAG 节点存在
        if (root["ReferenceRAG"] is not JsonObject ragObj)
        {
            ragObj = new JsonObject();
            root["ReferenceRAG"] = ragObj;
            changed = true;
        }

        // 1. 迁移顶层遗留 "Chat" 节点
        if (root["Chat"] is JsonObject legacyChat)
        {
            var chatObj = ragObj["chat"]?.AsObject() ?? new JsonObject();
            ragObj["chat"] = chatObj;

            var openAiObj = chatObj["openAiApi"]?.AsObject() ?? new JsonObject();
            chatObj["openAiApi"] = openAiObj;

            var legacyEndpoint = legacyChat["Endpoint"]?.GetValue<string>()?.Trim();
            var legacyApiKey = legacyChat["ApiKey"]?.GetValue<string>()?.Trim();
            var legacyModel = legacyChat["Model"]?.GetValue<string>()?.Trim();
            var legacyPrompt = legacyChat["SystemPrompt"]?.GetValue<string>()?.Trim();

            // API Key 迁移（若现有为空且旧节点有值）
            var currentKey = openAiObj["apiKey"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(currentKey) && !string.IsNullOrWhiteSpace(legacyApiKey))
            {
                openAiObj["apiKey"] = legacyApiKey;
                changed = true;
            }

            // Endpoint 迁移
            var currentEp = openAiObj["endpoint"]?.GetValue<string>()?.Trim();
            if ((string.IsNullOrWhiteSpace(currentEp) || currentEp == "https://api.openai.com/v1") && !string.IsNullOrWhiteSpace(legacyEndpoint))
            {
                openAiObj["endpoint"] = legacyEndpoint;
                changed = true;
            }

            // Model 迁移（避免保留本地 GGUF 或默认值）
            var currentModel = openAiObj["model"]?.GetValue<string>()?.Trim();
            if ((string.IsNullOrWhiteSpace(currentModel) || currentModel == "gpt-4o-mini" || currentModel.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(legacyModel))
            {
                openAiObj["model"] = legacyModel;
                changed = true;
            }

            // SystemPrompt 迁移
            var currentPrompt = chatObj["systemPrompt"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(currentPrompt) && !string.IsNullOrWhiteSpace(legacyPrompt))
            {
                chatObj["systemPrompt"] = legacyPrompt;
                changed = true;
            }

            // 彻底删除顶层遗留 "Chat" 节点
            root.Remove("Chat");
            changed = true;
        }

        // 2. 清理 ReferenceRAG.chat 下历史代理属性序列化残留的多余字段
        if (ragObj["chat"] is JsonObject cObj)
        {
            if (cObj.ContainsKey("endpoint")) { cObj.Remove("endpoint"); changed = true; }
            if (cObj.ContainsKey("apiKey")) { cObj.Remove("apiKey"); changed = true; }
            if (cObj.ContainsKey("model")) { cObj.Remove("model"); changed = true; }
        }

        // 3. 升级版本号
        ragObj["schemaVersion"] = 2;
        return changed;
    }
}
