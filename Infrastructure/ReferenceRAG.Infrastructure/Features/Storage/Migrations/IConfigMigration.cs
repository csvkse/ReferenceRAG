namespace ReferenceRAG.Infrastructure.Features.Storage.Migrations;

using System.Text.Json.Nodes;

/// <summary>
/// 配置文件版本迁移接口
/// </summary>
public interface IConfigMigration
{
    /// <summary>
    /// 目标结构版本号
    /// </summary>
    int TargetVersion { get; }

    /// <summary>
    /// 执行从上一版本向目标版本的迁移转换
    /// </summary>
    /// <param name="root">appsettings.json 根 JSON 对象</param>
    /// <returns>是否有数据变更需要回写</returns>
    bool Migrate(JsonObject root);
}
