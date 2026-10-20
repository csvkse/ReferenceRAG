using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：测试数据存储高并发文件写入安全门禁
/// 防止再次出现“并发测试或请求时，由于 records.json 文件锁竞争抛出 IOException”的问题。
/// </summary>
public sealed class ConcurrentStoreQualityGateTests
{
    [Fact]
    public async Task TestRecordStore_ConcurrentSaves_DoNotThrowIOException()
    {
        var store = new TestRecordStore();
        const int concurrentCount = 20;

        var tasks = Enumerable.Range(0, concurrentCount).Select(async i =>
        {
            var record = new TestRecord
            {
                Id = Guid.NewGuid().ToString(),
                TestType = "concurrency-gate",
                ModelName = $"test-model-{i % 3}",
                Timestamp = DateTime.UtcNow,
                Query = $"query-{i}",
                Results = new Dictionary<string, object>
                {
                    ["index"] = i,
                    ["score"] = 0.95
                }
            };
            await store.SaveAsync(record);
        });

        // 必须全部成功完成，没有任何并发文件冲突异常
        await Task.WhenAll(tasks);

        var retrieved = await store.GetAsync(null, "concurrency-gate", 50);
        Assert.True(retrieved.Count >= concurrentCount);
    }
}
