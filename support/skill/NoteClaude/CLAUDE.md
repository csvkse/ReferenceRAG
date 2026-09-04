---
created: 2026-04-14T19:01
updated: 2026-04-18
---
# CLAUDE.md

本目录为 Obsidian Vault，路径由**工作目录自动获取**，无需硬编码。

## 知识库优先检索规则 ⚠️

**一切可能需要查询资料的问题，都优先调用 `/ReferenceRAG` 获取一次。**

- 只有当 RAG 返回结果为空或明显不相关时，才进行其他搜索和查询。
- RAG 是你的**第一手知识来源**，优先使用。

## ReferenceRAG 配置

### 触发方式

| 格式 | 示例 |
|------|------|
| `rag:关键词` | `rag:Git 分支管理` |
| `/rag 关键词` | `/rag TypeScript 类型` |
| 领域词 + 动作词 | 在知识库搜 Git、笔记里查配置 |
| 意图短语 | 笔记里有没有 xxx、帮我搜一下笔记 |

### 执行方式（推荐 Python）

#### 方式一：Python 脚本 ✅ 推荐

```python
# -*- coding: utf-8 -*-
import json
import urllib.request

body = {
    'Query': '搜索关键词',
    'Mode': 'HybridRerank',
    'TopK': 30,
    'ContextWindow': 1,
    'EnableRerank': True,
    'RerankTopN': 10,
    'Filters': {
        'Folders': ['D:/D/Notes/Note']
    }
}

data = json.dumps(body).encode('utf-8')
req = urllib.request.Request(
    'http://localhost:7897/api/ai/query',
    data=data,
    headers={'Content-Type': 'application/json'}
)
with urllib.request.urlopen(req) as resp:
    result = json.load(resp)
    with open('.claude/rag_result.json', 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
```

> ⚠️ **必须使用 Write 工具创建脚本**（强制 UTF-8 编码），避免 Windows 控制台编码问题。

#### 方式二：PowerShell 脚本

```powershell
# .claude/rag_query.ps1
$vault = (Get-Location).Path

$body = @{
    Query = "关键词"
    Mode = "HybridRerank"
    TopK = 30
    EnableRerank = $true
    RerankTopN = 10
    Filters = @{ Folders = @($vault) }
} | ConvertTo-Json -Compress

Invoke-WebRequest -UseBasicParsing -Uri 'http://localhost:7897/api/ai/query' `
    -Method POST -ContentType 'application/json' `
    -Body ([System.Text.Encoding]::UTF8.GetBytes($body))
```

> ⚠️ 必须使用 Write 工具创建脚本，避免编码问题。

### API 参数说明

| 参数 | 类型 | 说明 |
|------|------|------|
| `Query` | string | 搜索关键词 |
| `Mode` | string | 检索模式：Standard / HybridRerank |
| `TopK` | int | 返回结果数量 |
| `EnableRerank` | bool | 是否启用重排序 |
| `RerankTopN` | int | 重排序后返回数量 |
| `Filters.Folders` | array | Vault 路径数组 |

### 检索模式

| 模式 | 返回数量 | 适用场景 |
|------|---------|---------|
| Quick | 3 条 | 快速确认 |
| Standard | 10 条 | 日常检索 |
| **HybridRerank** | 10 条 | **推荐** |
| Deep | 20 条 | 深度探索 |

> 推荐使用 `HybridRerank` 模式，结合 bge-reranker-base 重新排序，精度最高。

### 查询词自动改写 ⚡

**原则**：用户查询词模糊时，自动扩展意图，同时覆盖多个相关方向。

#### 改写策略

| 原查询 | 自动改写为 | 覆盖方向 |
|--------|-----------|----------|
| `rust使用` | `rust 入门教程 学习路径 能做什么` | 学习/能做/实战 |
| `git用法` | `git 教程 常用命令 分支管理 冲突解决` | 入门/进阶/实战 |
| `python数据分析` | `python 数据分析 入门 pandas 可视化` | 入门/工具/项目 |

#### 执行流程

1. **分析查询意图**：判断用户是想「学习」「能做」「如何使用」
2. **扩展关键词**：将模糊词扩展为多个方向
3. **执行检索**：使用扩展后的查询词
4. **过滤系统文件**：排除 `QUICK_REFERENCE`、`SKILL`、`.*` 等系统文件
5. **分类输出**：按学习/实战/进阶等方向分组展示

## 结果输出规范

### 文件路径 - 禁止截断

RAG 返回的文件名称和路径在生成结果时**必须完整显示**，不得截断任何字符。

- ✅ 正确：`D:\D\Notes\Note\AI开发\3 xxxxx.md`
- ❌ 错误：`D:\D\Notes\Note\AI开发\3 xxxxxxx.md`

这是为了确保 Obsidian 中点击 Wiki-link 时能够正确跳转。

### Wiki-link 格式

在结果中引用笔记时，使用完整的 Wiki-link 格式：

```
[[笔记完整文件名#L行号]]
```

示例：
```
📄 [[查询分组中最新的一条数据#L1-L2]]
```

## Windows 兼容说明

| 问题 | 解决方案 |
|------|---------|
| 中文编码 | 使用 Write 工具创建脚本（UTF-8），结果写入文件 |
| 控制台输出乱码 | Python 输出改为写入 `.claude/rag_result.json` |
| RAG 查询路径 | 必须使用 `D:/D/Notes/Note`（正斜杠） |

> ⚠️ **RAG 路径格式**：`D:/D/Notes/Note`（正斜杠），不是 `D:\\D\\Notes\\Note`

## 服务地址

- **Web UI**：`http://localhost:7897`
- **API**：`http://localhost:7897/api/ai/query`
- **Swagger**：`http://localhost:7897/swagger`
