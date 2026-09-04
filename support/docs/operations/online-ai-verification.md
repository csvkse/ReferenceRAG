# 在线 AI 接口验证

日期：2026-09-04。测试使用隔离数据目录和进程级环境变量；密钥不写入源码、配置、日志或发布目录。

## 环境变量

| 用途 | 变量 |
|---|---|
| Embedding 模式 | `REFERENCERAG_EMBEDDING_MODE=openai` |
| Embedding 地址 | `REFERENCERAG_EMBEDDING_API_BASE_URL` |
| Embedding 密钥 | `REFERENCERAG_EMBEDDING_API_KEY` |
| Embedding 模型 | `REFERENCERAG_EMBEDDING_MODEL_NAME` |
| Embedding 维度 | `REFERENCERAG_EMBEDDING_API_DIMENSION` |
| Reranker 模式 | `REFERENCERAG_RERANK_MODE=openai` |
| Reranker 地址 | `REFERENCERAG_RERANK_API_BASE_URL` |
| Reranker 密钥 | `REFERENCERAG_RERANK_API_KEY` |
| Reranker 模型 | `REFERENCERAG_RERANK_MODEL_NAME` |
| Reranker 开关 | `REFERENCERAG_RERANK_ENABLED=true` |
| Chat 地址、密钥、模型 | `Chat__Endpoint`、`Chat__ApiKey`、`Chat__Model` |

这些变量只影响当前启动进程及其子进程。不要使用 `setx`，也不要将密钥写入启动脚本。Chat 配置查询只返回 `ApiKeyConfigured`，不回传密钥。

## 实际结果

- SiliconFlow BAAI/bge-m3：批量两条输入均返回 1024 维有限数值向量。
- SiliconFlow BAAI/bge-reranker-v2-m3：相关 GPU 优化文本排名第一，分数约 0.923；无关烹饪文本排名最后。
- 程序索引：在线 Embedding 成功写入 1 个文件、1 个分块、1 个 1024 维向量。
- 程序 HybridRerank：正确命中测试文档，`RerankApplied=true`，重排分数约 0.991；实测 Embedding 约 286ms、Reranker 约 525ms。
- Chat 原始接口：请求 stable-code-latest 得到正文 `ONLINE_CHAT_OK`；供应商响应中的实际模型标识为 glm-5.3-flash。
- MafChat：SSE 返回 122 个文本增量和 done，无 error；模型调用知识库搜索后正确回答延迟加载策略，并给出测试文档来源。

在线结果会受供应商路由、限流和网络影响。stable-code-latest 是供应商别名，程序只能按请求发送，不能保证上游实际模型标识与别名一致。

## 验证与安全

- 6 项 Host 测试通过，Release 解决方案构建通过。
- 测试服务已经停止。
- 对源码、配置和普通文本文档进行精确密钥残留检查，命中数为 0；构建产物和隔离测试目录不保存环境变量。
- 本次未修改真实知识库和正式索引。
