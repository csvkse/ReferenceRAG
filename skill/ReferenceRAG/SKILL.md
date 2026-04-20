---
name: ReferenceRAG
description: 本地知识库语义检索服务。从 Obsidian 笔记库检索技术教程、配置说明、最佳实践等内容。触发规则：(1)强制触发：rag:关键词、/rag 关键词 (2)组合触发：领域词(知识库/笔记/vault/obsidian/文档) + 动作词(搜/查/找/检索/查询) (3)意图触发：笔记里有没有、帮我搜一下笔记、在知识库中查找。NOT for: 天气、新闻、股票等实时信息。
allowed-tools: Read, Bash
---

# ObsidianRAG 知识库检索

本地知识库检索服务，支持 Obsidian 笔记和 Markdown 文档的语义搜索。

## 触发规则（优先级从高到低）

### 1. 强制触发（无需领域词）
| 输入格式 | 示例 |
|---------|------|
| `rag:关键词` | `rag:Git 分支管理` |
| `/rag 关键词` | `/rag TypeScript 类型` |

### 2. 组合触发（领域词 + 动作词）
**领域词**：知识库、笔记、vault、obsidian、文档、文库
**动作词**：搜、查、找、检索、查询、翻一下、看看

| 示例输入 | 触发原因 |
|---------|---------|
| 在知识库搜 Git | 领域词 + 动作词 ✅ |
| 笔记里查一下配置 | 领域词 + 动作词 ✅ |
| 帮我在 obsidian 找教程 | 领域词 + 动作词 ✅ |
| vault 里有没有 | 领域词 + 动作词 ✅ |

### 3. 意图触发（完整短语）
- `笔记里有没有 xxx`
- `帮我搜一下笔记`
- `在知识库中查找`
- `看看笔记里的 xxx`

### 4. 不触发场景
- 纯动作词无领域词：`查询 Git`、`搜索配置` ❌
- 实时信息：天气、新闻、股票 ❌
- 明确其他数据源：数据库查询、API 调用 ❌

## 执行流程

**查询词扩展策略**：将用户输入扩展为多个相关关键词（中英文、同义词、相关概念），提升召回率。

示例：`Git 分支管理` → `Git 分支管理 branch 版本控制 分支策略 git flow`

直接调用 `POST http://localhost:5000/api/ai/query` 执行 HybridRerank 搜索，格式化返回结果。

## 配置（可选）

如需自定义地址，修改 `~/.agents/.env`：

```env
OBSIDIAN_RAG_API_URL=http://localhost:5000
OBSIDIAN_RAG_API_KEY=
```

## API 调用示例

### Windows Git Bash 中文请求方式（重要）

⚠️ **Windows Git Bash 中直接使用 `-d` 发送中文会导致乱码**，因为 bash 字符串处理会破坏 UTF-8 编码。

**正确方式：使用 heredoc + --data-binary**
```bash
curl -s -X POST "http://localhost:5000/api/ai/query" \
  -H "Content-Type: application/json; charset=utf-8" \
  --data-binary @- << 'EOF'
{"query": "搜索关键词", "mode": "HybridRerank", "topK": 10}
EOF
```

**关键点：**
- `--data-binary @-` 从标准输入读取原始二进制数据
- 使用单引号 `'EOF'` 包裹内容，防止变量展开
- 确保中文字符以正确的 UTF-8 字节发送

### 其他方式
```bash
# PowerShell（推荐） - 不需要特殊处理
curl -X POST "http://localhost:5000/api/ai/query" -H "Content-Type: application/json" -d '{"query": "搜索关键词", "mode": "HybridRerank"}'

# CMD - 需先设置编码
chcp 65001
curl -X POST "http://localhost:5000/api/ai/query" -H "Content-Type: application/json; charset=utf-8" -d "{\"query\": \"搜索关键词\"}"
```

**查询模式**：Quick(3) | Standard(10) | Hybrid(15) | HybridRerank(10,推荐) | Deep(20)

## 服务地址

- Web UI: `http://localhost:5000`
- Swagger: `http://localhost:5000/swagger`

## 支持的模型

**Embedding**：bge-small-zh-v1.5、bge-base-zh-v1.5、bge-large-zh-v1.5、bge-m3

**Rerank**：bge-reranker-base、bge-reranker-large

## GitHub

https://github.com/hlrlive/ObsidianRAG
