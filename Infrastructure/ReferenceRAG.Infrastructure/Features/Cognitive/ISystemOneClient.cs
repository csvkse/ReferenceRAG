using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Infrastructure.Features.Cognitive;

/// <summary>
/// TypeSafe AI System One 统一客户端接口
/// </summary>
public interface ISystemOneClient : IDisposable
{
    /// <summary>服务是否已配置有效密钥与端点</summary>
    bool IsConfigured { get; }

    /// <summary>当前生效的决策模型标识（如 jev-latest）</summary>
    string ModelName { get; }

    /// <summary>
    /// 对目标状态评估一组强类型问题
    /// </summary>
    /// <param name="state">评估内容（可为字符串、业务实体或匿名对象）</param>
    /// <param name="questions">问题字典（Key 为返回结果字段名）</param>
    /// <param name="ct">取消令牌</param>
    Task<SystemOneResponse> EvaluateAsync(
        object state,
        IDictionary<string, SystemOneQuestion> questions,
        CancellationToken ct = default);
}
