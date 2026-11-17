# ReferenceRAG 故障排查与易错点手册 (v2)

> **基础地址**：`http://127.0.0.1:5123`
> **鉴权凭据**：`X-API-Key: rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx`

调用报错或结果异常时，请按本手册排查定位。

---

## 1. 快速自检顺序

```bash
# 1. 连通性探活 (验证服务是否存活)
python scripts/rag_cli.py ping

# 2. 系统与模型状态 (检查 GPU、模型加载、重排健康度)
python scripts/rag_cli.py stats

# 3. 知识库清单 (验证当前 API Key 能否看见知识库)
python scripts/rag_cli.py list-kbs

# 4. 数据源清单 (验证各物理挂载源的文件数与切片数)
python scripts/rag_cli.py list-sources
```

---

## 2. 常见故障排查表

| 现象 / 错误码 | 根本原因 | 处置方案 |
|:---|:---|:---|
| **401 Unauthorized** | 请求未附带 API Key，或 Key 已过期/被禁用 | 在请求头中补充 `X-API-Key: rag_live_xxx` 或 `Authorization: Bearer rag_live_xxx` |
| **检索恒返回空，且 `knowledgeBases: []`** | **API Key 作用域失配陷阱**：Key 的 `AllowedSources` 配成了知识库名（如 `["Notes"]`），而系统底层数据源名为连接器动态生成的 `"Notes - Note"` | 登录系统管理后台或调用 `PUT /api/ApiKeys/{id}`，将 `sourceScopeMode` 改为 `"all"`，或授权实际源名称通配符 `"*"` |
| **检索返回切片但标记 `NoMatch: true`** | **质检拦截**：服务端 `QueryMatchSignals` 判定查询词核心标识或二元组未在任何切片正文中重叠，返回的是宽松兜底凑数切片 | **不可采信这批切片生成答案**。提取核心名词去停用词后重试，或加上 `--lexical` 精确模式；若多次未命中，如实向用户反馈未收录 |
| **400 `must be valid UTF-8 encoded text`** | Windows 控制台将中文按本地代码页（GBK）发出，破坏了请求体字节 | **强烈推荐直接使用 `scripts/rag_cli.py`**（原生 UTF-8 字节流）。若必须用 curl，Git Bash 必须使用 `heredoc + --data-binary`，PowerShell 必须传 `[System.Text.Encoding]::UTF8.GetBytes()` |
| **MCP 报错: 参数 'x' 类型无效，期望 schema 未声明的参数** | 服务端开启了严格的入参 Schema 门禁，传入了未声明的额外参数（如拼错的参数名 `kbIds` 代替 `knowledgeBaseIds`） | 检查对应工具的 `inputSchema`（可查阅 `references/mcp-tools.md`），仅传入允许的字段 |
| **获取大纲/读取正文报错: 路径不在任何已注册数据源内** | 传入的路径没有以数据源根为基准，或跨数据源访问 | 1. 先用 `python scripts/rag_cli.py list-sources` 获取合法源名；<br>2. 传 `--source "Notes - Note"` 明确指定源；<br>3. 相对路径需包含子目录（如 `Note/1 工作/...`） |
| **重排模型返回 `health: degraded`** | 生产环境 GGUF 格式重排模型缺少分类头，系统自动降级以保护服务不崩溃 | 正常行为。当前环境检索排序已自动回退至高可靠的 RRF 融合排序，不要依据重排绝对分值做硬性截断 |
| **长文档检索截断** | 切片正文默认按 500~1000 字进行了摘要保护 | 先调用 `outline` 查看章节大纲，再调用 `read` 传入目标 `startLine` 与 `endLine` 精确获取全文 |

---

## 3. 未命中 (NoMatch) 语义改写规则

当服务返回 `NoMatch: true` 时，说明当前自然语言查询与词法/向量索引存在词表落差（Vocabulary Gap）。

| 用户原始输入 | 失败原因分析 | 推荐改写策略 |
|:---|:---|:---|
| `帮我查下那个关于状态机是怎么写的` | 口语化助词（`帮我查下`、`那个`、`关于`、`是怎么写的`）稀释了核心词权重 | 提取核心术语：`状态机 MoveNext 异步实现` |
| `IndexOutOfRangeException` | 代码异常类名若走向量可能被泛化为一般性"数组越界异常" | 强制走词法精确匹配：`python scripts/rag_cli.py search "IndexOutOfRangeException" --lexical` |
| `docker 网络配置` | 笔记中可能用英文写为 `network bridge overlay` | 补充同义词中英混合：`docker 网络 network bridge 端口映射 port mapping` |


