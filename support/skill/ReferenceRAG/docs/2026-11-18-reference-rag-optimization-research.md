# ReferenceRAG 技能优化调研：编码、路径、接口与架构

> 调研日期：2026-11-18
> 范围：`C:\Users\hlr\.claude\skills\ReferenceRAG`（SKILL.md / references / scripts/rag_cli.py）
> 服务实测地址：`http://<server-ip>:5123`（生产）
> 方法：对生产服务实际发请求取证 + 本机环境复现 + 业界方案检索
> 性质：只调研，不改代码。所有"修复方案"均为建议。

---

## 0. 结论摘要

| # | 结论 | 证据强度 |
|:--|:--|:--|
| 1 | **UTF-8 崩溃可 100% 复现**，且 SKILL.md 给出的调用示例（裸 `python`）正好触发它 | 实测 |
| 2 | **失败退出码恒为 0**，脚本/Agent 无法凭退出码判断成败 | 实测 |
| 3 | **REST 通道认证失败被显示为"✅ 成功命中 0 条"**，属静默误报 | 实测 |
| 4 | **REST 通道 `--mode hybrid/semantic` 是假的**：服务端 `/api/knowledge-base-search` 不识别 `Mode` 字段，CLI 却打印用户指定的模式名。只有 MCP 通道真正支持模式 | 实测（同查询同分数） |
| 5 | **路径/数据源解析是最大可用性短板**：glob 只匹配文件名；`read`/`outline` 需要"源相对路径+正确源名"；`Notes - TNote` 源物理文件未挂载，相关命令全失败；`get_document_info` 返回的 path 无法回读 | 实测 |
| 6 | **MCP 错误信封被丢弃**：`call_mcp_tool` 只取 `content[0].text`，丢掉 `isError` / `structuredContent.errorCode` / `_meta`，而服务端明确要求按 errorCode 决策 | 实测 |
| 7 | **默认应走 MCP 通道而非 REST**：MCP 的 `knowledge_search` 支持 auto/hybrid/semantic/lexical 且实测通道行为不同；REST 只有一个 `UseLexicalTokenizer` 布尔 | 实测 |
| 8 | **自动路由启发式明显漏判**：`List<T>.Add`、`HttpClient.SendAsync`、`GC.SuppressFinalize`、`OSError: [Errno 2]` 全部被判为向量通道 | 实测 |
| 9 | **需要适配器 + 策略模式，但不需要状态机**。双传输 × 7 类操作 × 大小写双拼字段（脚本中 40+ 处 `X or x`）是真实复杂度；"未命中→改写→下钻"只是线性流程 + 有界重试 | 实测 + 业界对照 |
| 10 | **已有可复用基础设施被浪费**：`claude mcp add --transport http` 可直接把该服务注册为原生 MCP（26 工具），无需 CLI；服务端另有 `/v1/chat/completions`、`/api/jobs`、`graph_search`、`search_file_content` 未被 CLI 覆盖 | 实测 |
| 11 | **API Key 硬编码在已提交并推送的 Git 仓库中**（`.agents` 仓，两处文件）。需轮换并改为环境变量 | 实测 |
| 12 | **`C:\Users\hlr\.claude\skills` 本身是指向 `C:\Users\hlr\.agents\skills` 的符号链接**，所以技能只有一份真实目录（`.agents/skills/ReferenceRAG`）；编辑 `.claude/skills/...` 路径实际写的是同一文件。此前误判为"双副本"，已更正 | 实测（`LinkType=SymbolicLink` + `os.path.realpath` + `cp` 返回 same file） |

---

## 1. 实测环境

```
OS          Windows 11 Pro for Workstations 10.0.26300
Shell       PowerShell 7.7.0（Console.OutputEncoding = gb2312）
Python      3.14.6
代码页      chcp = 936 (GBK)
sys.stdout.encoding  = gbk（未设置任何 PYTHON* 变量时）
PYTHONUTF8 / PYTHONIOENCODING / PYTHONLEGACYWINDOWSSTDIO  均未设置（User/Machine 级也无）
已安装      requests 2.34.2、colorama
未安装      httpx / mcp / typer / rich / tenacity / pydantic / click
服务端      Kestrel，MCP tools/list 返回 26 个工具，7 个带 outputSchema
```

服务端能力实测：

| 能力 | 结果 |
|:--|:--|
| `initialize` (MCP 2025-06-18) | 200，返回 serverInfo `ReferenceRAG-MCP 1.0.0`，无 `Mcp-Session-Id` 响应头（无状态） |
| 无 `Accept` 头调用 | 返回 `application/json` |
| `Accept: application/json, text/event-stream` | 返回 `text/event-stream`（SSE），单条 `data:` 行 |
| JSON-RPC 批量数组 | **400**，服务端不支持 batch |
| notification（无 id） | 202 空响应 |
| 未知工具 | HTTP 200 + `isError:true` + `errorCode:"unknown_tool"` |
| 参数缺失 | `isError:true` + `errorCode:"invalid_argument"` + `missing:[...]` |
| 未知 REST 字段 / 非法 Mode | 200，静默忽略 |

---

## 2. 问题清单

### P0-1　UTF-8 输出崩溃（编码）

**现象**

```bash
python .claude/skills/ReferenceRAG/scripts/rag_cli.py ping
# Traceback ... UnicodeEncodeError: 'gbk' codec can't encode character '\u2705'
# exit=1
```

重定向到文件同样失败（管道/文件都会把 stdout 变成 gbk 文本流）。崩溃点在 `scripts/rag_cli.py:237`（`cmd_search` 的 ✅/⚠️ 三元表达式）与 `cmd_ping` 的 `✅/❌`。当前会话第一次 `search` 就复现了同一条 traceback。

**根因**

- PEP 528/529 只解决**控制台句柄**的宽字符写入；stdout 被管道/文件重定向时仍是 `TextIOWrapper(encoding=locale.getpreferredencoding())` = gbk。
- 脚本只在文档里要求"强制 UTF-8"，代码里没有任何 `reconfigure`，靠调用方传 `-X utf8` 或 `PYTHONIOENCODING`。
- SKILL.md 的全部示例都是裸 `python <script>`，即默认走崩溃路径。

**编码机制要点（官方）**

- PEP 528：fd 是真控制台时 stdout 走 `_WindowsConsoleIO`（UTF-8 ↔ `WriteConsoleW`），`print("✅")` 不会崩，与 chcp 无关。
- **只有管道/文件重定向**才回退 `TextIOWrapper`，编码取 ANSI 代码页（中文 = cp936/gbk）。Claude Code、IDE、pwsh 捕获输出正是此路径。
- PEP 529 只固定文件系统编码（UTF-8/surrogatepass），不管 stdout。
- 优先级：`PYTHONIOENCODING` > `-X utf8`/`PYTHONUTF8` > 默认；代码内 `reconfigure()` 再覆盖。
- `chcp 65001` 只改 Console CP，**不改** ANSI CP，管道场景仍 gbk。
- PEP 686：3.15 起 UTF-8 模式默认；本机 3.14.6 尚未。

**建议修复：按 `sys.stdout.encoding` 决策，而不是无条件 reconfigure**

社区（Click FAQ、pip #11798、uv #10073）的结论是：**强行 reconfigure 成 UTF-8 会让父进程按 gbk 解码时花屏**。安全做法是双轨：

```python
def _configure_streams(force_utf8: bool = False) -> bool:
    """返回 True 表示可安全输出 Unicode。"""
    enc = (getattr(sys.stdout, "encoding", "") or "").lower()
    # 关键：必须在 reconfigure 之前判断，否则永远为 True（见下方实测）
    unicode_ok = enc.startswith("utf") or force_utf8
    if unicode_ok:
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8", errors="backslashreplace")
            except Exception:
                return False
    return unicode_ok

UNICODE_OK = _configure_streams()
OK, WARN, FAIL = ("✅", "⚠️", "❌") if UNICODE_OK else ("[OK]", "[WARN]", "[FAIL]")
```

> ⚠️ **实测陷阱**：Windows 上 `TextIOWrapper.reconfigure()` 几乎总是成功（即使 `PYTHONIOENCODING=gbk`），所以"先 reconfigure 再判断"永远得到 True，ASCII 降级永不触发。必须**先读原 `encoding`** 再决定。已实测：`PYTHONIOENCODING=gbk python x.py` 且 `x.py` 内先 reconfigure → 管道输出变成 UTF-8 字节，父进程按 gbk 解码会花屏。

已实测验证（本机 Python 3.14.6，四种组合）：

| 场景 | `stdout.encoding` | `unicode_ok` | 输出符号 |
|:--|:--|:--|:--|
| 管道，无环境变量 | gbk | False | `[OK][WARN][FAIL]` |
| `PYTHONIOENCODING=gbk` | gbk | False | `[OK][WARN][FAIL]` |
| `PYTHONIOENCODING=gbk` + `--utf8` | utf-8 | True | `✅⚠️❌` |
| `PYTHONUTF8=1` | utf-8 | True | `✅⚠️❌` |

- Claude Code / 多数 CI 消费端是 UTF-8 → `--utf8` 或 `OBSIDIAN_RAG_FORCE_UTF8=1` 可直接强制。
- 未知消费端 → ASCII 降级，永不崩溃、不花屏。
- `errors="backslashreplace"` 只兜底 lone surrogate；不要用 `replace`（中文会变 `?`，排障信息丢失）。
- `line_buffering` / `isatty` 不受 `reconfigure` 影响。

**配套**

- SKILL.md 所有示例命令加 `-X utf8`（文档层面减少踩坑），脚本内部仍自兜底。
- stderr 的 `errors` 恒为 backslashreplace（官方规定），无需单独设。

---

### P0-2　退出码恒为 0（可编程性）

**现象**

| 命令 | 实际 exit |
|:--|:--|
| `read no/such/file.md` → ❌ 读取失败 | 0 |
| `chunk 0000…` → ❌ 未找到切片 | 0 |
| `list-files` 参数非法 → ❌ | 0 |
| `search` 服务不可达 | 0 |
| `search --api-key bad`（REST） | 0 |
| `--mode bogus`（argparse 拒绝） | 2 |

**影响**：Agent/CI 只能靠解析中文文案判断失败；`&&` 链、重试循环、告警全部失效。

**建议**：定义错误模型与退出码表。

```python
class RagError(Exception):
    def __init__(self, code: str, message: str, exit_code: int, retryable: bool = False):
        ...
EXIT = {
  "ok": 0, "usage": 2, "no_match": 3,
  "not_found": 4, "path_denied": 4,
  "auth": 5, "forbidden": 5,
  "network": 6, "timeout": 6, "server": 7,
}
```

- `NoMatch=true` 单独给 `3`（不是失败，但 Agent 需要分支：改写查询而非重试）。
- 所有 `print("❌ …")` 路径改为 `raise RagError(...)`，`main()` 统一捕获 + 打印 + `sys.exit`。

---

### P0-3　REST 通道静默误报成功

**现象**

```bash
rag_cli.py search "Where" --api-key bad_key
# ### …  - **状态**: ✅ 成功命中
# - **召回条数**: 0 条 | 耗时 0ms   exit=0
```

`request_http` 对 `HTTPError` 返回 `{"success": False, ...}`，而 `cmd_search` 的 REST 分支从不检查该字段，直接读 `Items`（空）继续渲染。

MCP 分支行为正确：同样的坏 Key 打印 `❌ MCP 知识库检索失败: HTTP 403`。

**建议**：REST 与 MCP 共用同一套"先判错再渲染"前置检查；`success is False` 时抛 `RagError`。

---

### P0-4　REST 的 `--mode` 名不副实

**证据**（同查询 `如何判断集合里有没有元素`，TopK=5）

| 请求 | 返回 ChunkId+Score 序列 |
|:--|:--|
| `{Query, TopK=5, UseLexicalTokenizer=false}` | 基线 |
| 基线 + `"Mode":"semantic"` | **与基线完全一致** |
| 基线 + `"Mode":"hybrid"` | **与基线完全一致** |
| 基线 + `"UseLexicalTokenizer":true` | 全 `lexical` 通道 |

CLI 的 REST 分支只映射 `lexical/auto` → `UseLexicalTokenizer` 布尔；`--mode semantic` 与 `--mode hybrid` 发的是同一个请求，却打印 `模式: semantic` / `模式: hybrid`。

MCP 通道则真实生效：

| 查询 | `mode=hybrid` | `mode=semantic` | `mode=lexical` |
|:--|:--|:--|:--|
| `Where` | lexical | lexical | lexical |
| `IndexOutOfRangeException` | **vector** | **vector** | lexical |
| `string.Intern` | lexical+vector 混合 | lexical+vector 混合 | lexical |

**建议**

1. `search` 默认传输改为 **MCP**（这是唯一真实支持模式语义的通道）；REST 仅作"降级通道"，且降级时必须打印 `⚠ REST 通道不支持模式区分，已按 mixed 执行`。
2. 或 REST 分支拒绝 `--mode semantic/hybrid`，改为自动切 MCP。
3. 文档同步：`references/api-reference.md` 应标注"`Mode` 字段服务端暂未实现（静默忽略）"。

---

### P0-5　MCP 错误信封被丢弃

**证据**

```python
call_mcp_tool("nope_tool", {})      # -> {"raw_text": "工具 'nope_tool' 不存在…"}
call_mcp_tool("knowledge_base_drill_down", {"chunkId":"abc"})
                                    # -> {"raw_text": "缺少必需参数: knowledgeBaseId"}
```

实际响应含 `isError:true`、`structuredContent.errorCode`、`_meta.errorCode`（`unknown_tool` / `invalid_argument` / `not_found` / `path_denied` / `too_large` / `forbidden`）。服务端 `initialize.instructions` 第 6 条明确要求"按 errorCode 决定重试还是放弃"，客户端把它丢掉了。

**建议**

```python
result = res["result"]
sc = result.get("structuredContent") or {}
code = sc.get("errorCode") or (result.get("_meta") or {}).get("errorCode")
if result.get("isError") or code:
    raise RagError(code or "server", sc.get("error") or text, EXIT.get(code, 7))
return sc or json.loads(text)
```

同时：**优先读 `structuredContent`**，`content[0].text` 只兜底。实测一次 TopK=5 的检索：原始响应 18.8 KB，其中 `content.text` 8.2 KB、`structuredContent` 6.0 KB，两者内容重复。

---

### P1-1　路径与数据源解析

**实测矩阵**

| 操作 | 输入 | 结果 |
|:--|:--|:--|
| `list-files` | `Where.md` | ✅ 1 条 |
| `list-files` | `*Where*` | ✅ 8 条 |
| `list-files` | `TNote/*` / `*/概念/*` / `知识库/概念/*` | ❌ 0 条 |
| `list-files` | `*.md` | 500 条（`find_files_by_pattern` 的 `maxResults` 上限 500；CLI 不显示 effective/truncated） |
| MCP `list_files(pathKeyword="C#.NET开发")` | — | ✅ 21 条，带 connectorId/documentId/chunkCount |
| `read` | `知识库/概念/C#.NET开发/Where.md`（无 `--source`） | ❌ `path_denied` |
| `read` | 同上 + `--source "Notes - Note"` | ❌ `not_found` |
| `read` | `TNote/知识库/概念/C#.NET开发/Where.md` + `--source "Notes - Note"` | ✅ |
| `read` | 同上 + `--source "Notes - TNote"` | ❌ `not_found`（源根已含 `TNote`，再拼一次成 `TNote/TNote/…`） |
| `outline` | 同 read | 同 read |
| `get_document_info` 返回 `file.path` | `知识库/概念/C#.NET开发/Where.md` | **三个源全部 `not_found`/`path_denied`，无法回读** |

**根因**

1. `find_files_by_pattern` 只对**文件名**做 fnmatch，不支持路径段；CLI 把它当成通用文件浏览命令暴露，文档也写成"按 Glob 模式检索文件清单"。
2. `read_note_content`/`get_note_outline` 的路径解析是"源根 + 相对路径"的朴素拼接，没有"尝试剥离前缀"或"跨源探测"。
3. `Notes - TNote` 是 `KnowledgeBaseOnly` 源：`physicalFiles=0, totalSizeBytes=0`，仅索引存在、物理目录未挂载，因此所有文件级命令必然失败——但它仍在 `list_sources` 的合法清单里。
4. `TNote/…` 文件在索引中的 `sourceKey` 不带 `TNote/` 前缀，而物理路径必须带；两套坐标系没有统一。

**建议**

- 新增统一解析函数（按序尝试，命中即缓存）：

```python
candidates = []
for src in sources:                       # 先试调用方给的 source
    for p in (path, strip_known_prefix(path), f"{src.prefix}/{path}"):
        candidates.append((src.name, p))
```

- 优先使用 `list_files(pathKeyword=…)` 找文件（返回权威 `sourceKey` + `locator`），再用 `locator` 推回源名。
- `list-files` 增加 `--path` 模式（走 `list_files`，上限 200/truncated），保留 `--name` 模式（走 `find_files_by_pattern`，上限 500），文档写清二者差异与上限。
- 对 `physicalFiles == 0` 的源提前给出可诊断错误：`该源仅索引、物理目录未挂载，文件读取不可用；改用 chunk/drill-down 或 search_file_content`。
- `chunk` 无 `--kb` 时当前实现：1 次 `GET /api/knowledge-bases` + 每库 1 次 `GET chunks/{id}`，且 404 后还会经 MCP 再探一次（实测 1 个 chunk 共 3 个请求，其中 2 个重复）。改为：`knowledge_base_list` 一次 + 命中 kb 直接单次 GET；或直接要求 `--kb`（搜索输出里已带 `库ID`，复制即可）。

---

### P1-2　自动路由启发式漏判

16 条样例的判定结果：

| 判为 LEXICAL（正确） | 判为 VECTOR（**错误**，明显是代码/错误码） |
|:--|:--|
| `IndexOutOfRangeException`、`NullReferenceException`、`CS8618`、`SubscriptionManager`、`Where`、`"异步状态机"` | `List<T>.Add`、`HttpClient.SendAsync`、`GC.SuppressFinalize`、`OSError: [Errno 2]`、`错误码 500` |

规则只看 `^[A-Za-z0-9_]{2,40}$` 且含大写/下划线，遇 `.`、`<`、`(`、`:`、空格即失效；中文"错误码"不在 `error|code|status` 词表内。

**建议**：正则改为"任一命中即词法"：

```python
CODE_HINTS = re.compile(r"""
  [A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+   # 点号成员/命名空间
| [A-Za-z_][A-Za-z0-9_]*\s*\(                        # 函数调用
| \b[A-Z][A-Za-z0-9]{2,}(Exception|Error)\b          # 异常类
| \b(0x[0-9A-Fa-f]+|E[A-Z]{3,}|CS\d{4})\b            # 错误码
| (错误码|异常|状态码|报错)\s*[:：#]?\s*\d{3,5}
""", re.X)
```

注意：服务端 `mode=auto` 自己也在做路由，实测三条查询全部落到 lexical（含中文概念查询）。**客户端启发式与服务端 auto 是重复建设**，建议二选一：默认 MCP `mode=hybrid`（保召回），显式 `--mode lexical` 走精确；或默认 `auto` 且信任服务端，删掉客户端启发式。

---

### P1-3　NoMatch 门禁偏弱

| 查询 | NoMatch | 实际相关性 |
|:--|:--|:--|
| `zxqvbnmklp` | ✅ true（`没有召回任何切片`） | 正确 |
| `完全无关乱码词汇组合` | ❌ false | 命中含"词汇"的无关笔记，coverage 0.25/0.5 |
| `苹果香蕉西瓜橘子` | ❌ false | 命中含"属性值/数组"的笔记 |
| `量子纠缠退相干实验数据` | ❌ false | 命中量子计算段落，部分相关 |

服务器对 `knowledge_search` 的结果项还提供 `coverage`（0–1）与 `isFloor`，CLI 完全未使用。

**建议**：客户端增加后置过滤链（责任链的合理用法，3 个纯函数）：

```python
filters = [drop_floor, drop_low_coverage(min=0.5), dedupe_by_document]
```

当全部结果被过滤掉 → 输出"未命中"并 `exit 3`。阈值需用自有标注集校准，不要照搬业界 0.75。

---

### P1-4　输出与 Token 成本

| 形态 | 字节数（TopK=5） |
|:--|:--|
| CLI 文本输出 | 4,848 |
| CLI `--json` | 13,899 |
| MCP 原始响应 | 18,837（text 8,226 + structuredContent 6,085 + 信封） |
| 其中切片正文合计 | 1,627 字符 |

**问题**

- 每条结果都重复打印 `库ID`、`切片ID`、`得分`、`通道`，对 Agent 是噪声；`HEAD` 提示在结果里出现两次（`headingPath` 与正文内标题）。
- 无 `--limit-chars` / 正文截断。10 条结果实测 30–42 KB。
- `--json` 直接把服务端 PascalCase 原样倒出，Agent 仍需自己兼容双写法。

**建议**

- 默认 `--format md`（现状），新增 `--format brief`（仅 `[[path#Lx-Ly]] + 得分 + 通道`，正文省略，需要时再 `chunk`/`read`）。
- 新增 `--max-chars N`（默认 800/条），超出加 `… (truncated, chunkId=…)`。
- 统一 normalized 输出模型：`{sourceKey, startLine, endLine, headingPath, score, channels, chunkId, kbId, text}`，字段名稳定后再渲染。

---

### P1-5　可靠性与性能

| 场景 | 实测 |
|:--|:--|
| 端口拒绝 | 2.3 s 失败，无重试 |
| 主机不可达 | 21.3 s 失败（OS 连接超时），无重试 |
| 5 次 hybrid（进程内复用连接） | 均 337 ms，其中一次 703 ms |
| 5 次请求 keep-alive vs 每请求新连接 | 348 ms vs 382 ms（约 9%） |
| 8 次 lexical 串行 vs 并行 8 线程 | 361 ms vs 180 ms（**2.0x**，非 8x → 服务端存在串行点） |
| CLI 单次启动开销 | 155–165 ms（Python 解释器 + argparse） |
| 单次检索延迟 | lexical 41 ms / semantic 77 ms / auto 92 ms / hybrid 235 ms |

**建议**

- 加入有界重试：仅对 `URLError`、`TimeoutError`、HTTP 5xx 重试 2 次，指数退避 0.3 s / 0.9 s；4xx 不重试（幂等性由 `annotations.idempotentHint=true` 保证，26 个工具均是只读）。
- 去掉 `Connection: close`（当前每请求强制短连接）。
- 服务端并行收益有限，客户端不必上线程池；批量查询用一次进程内循环即可，重点是**减少进程启动次数**（Agent 侧合并调用）。
- 默认超时从 40 s 降至 15 s（检索实测 <1 s），`read` 大文件单独放宽。

---

### P2-1　文档与实现不一致（会误导 Agent）

| 文档 | 声称 | 实际 |
|:--|:--|:--|
| SKILL.md | 服务地址 `<server-ip>:5123` | 一致 |
| references/api-reference.md、mcp-tools.md、troubleshooting.md、knowledge-bases.md | `127.0.0.1:5123` | 生产是 `<server-ip>`；本机端口未验证 |
| openai-compat.md | embeddings/rerank 用 `localhost:7897` | 与 5123 服务不是同一端口，未说明二者关系 |
| SKILL.md 第 106 行 / api-reference.md 第 101 行 | "25 个 MCP 工具" | `tools/list` 返回 **26** 个 |
| references/mcp-tools.md | 标题"完整参考手册 (25 工具)" | 仅详细写了 2 个工具；其余只在分类表里一行 |
| references/knowledge-base.md | — | 223 字节占位符，内容只有"请参阅 knowledge-bases.md" |
| SKILL.md | "零外部依赖，强制 UTF-8" | 零依赖属实；"强制 UTF-8"不属实（见 P0-1） |
| SKILL.md 工具清单 | 只列 5 个 MCP 工具 | 实际 26 个，`graph_search`、`search_file_content`、`get_file_tree`、`get_document_info` 等完全未提及 |

**建议**：文档与代码同批更新；工具清单改为从 `tools/list` 生成（加一个 `--list-tools` 子命令或维护 `docs/mcp-tools.generated.md`）。

---

### P2-2　架构与工程

| 项 | 现状 |
|:--|:--|
| 单文件 478 行 | 可接受，但职责混杂：传输、字段兼容、渲染、参数解析、路由启发式 |
| 双写法兼容 | `it.get("ChunkId") or it.get("chunkId")` 一类出现 **40+ 次**，分布在 9 个渲染函数 |
| 传输选择 | `search` 有 `--mcp`，其余命令固定走 MCP 或 REST，无统一策略 |
| 配置 | `argparse.Namespace` 贯穿所有函数，无 Settings 对象 |
| 无测试 | 无任何冒烟/回归脚本 |
| `__pycache__` | 技能目录内存在 `.pyc`（已被 `.gitignore` 忽略，未提交） |
| 目录结构 | `.claude/skills` 指向 `.agents/skills`（符号链接），只有一份真实目录，无需双写 |

---

## 3. 设计模式评估（可行性与"该不该做"）

### 3.1 适配器（Adapter）—— **该做，但收窄**

**触发条件已满足**：2 种传输（REST / MCP）× 约 7 类操作（search / read / outline / drill / list / tree / stats）× 字段大小写双拼（PascalCase vs camelCase）在 40+ 处重复。

**建议形态**（不引第三方库）：

```python
class Transport(Protocol):
    def call(self, op: str, **kw) -> dict: ...   # 统一返回 normalized dict
    def close(self) -> None: ...

class RestTransport:  def call(...)  # PascalCase→normalized
class McpTransport:   def call(...)  # JSON-RPC，structuredContent，errorCode 抛出
```

**不该做的部分**：不要为每个命令各写一遍适配器，也不要把 26 个工具全部建模成类。只适配 CLI 实际暴露的 7 类操作，其余工具走 `mcp.call("graph_search", …)` 透传。

判据：**字段规范化只写一次**，而不是集中化所有业务逻辑。

### 3.2 策略（Strategy）—— **该做，用 dict 不用继承**

```python
@dataclass(frozen=True)
class SearchPolicy:
    mode: str            # auto|hybrid|semantic|lexical
    dedupe_doc: bool
    min_coverage: float

POLICIES = {
    "exact":    SearchPolicy("lexical",  False, 0.0),
    "concept":  SearchPolicy("hybrid",   True,  0.5),
    "balanced": SearchPolicy("auto",     True,  0.3),
}
```

模式本身是纯函数选择，无需类层次。**只有在策略需要持有连接/生命周期时才用类**（此处不需要）。

### 3.3 状态机 —— **不该做**

"检索 → NoMatch → 改写 → 重试 → 下钻 → 按行读取" 是线性流程 + 至多一次重试，状态数 < 5，无恢复/审计需求。显式 FSM 会引入转移表、状态持久化等无收益复杂度。

**替代**：编排函数 + 预算计数器。

```python
def retrieve(q, budget=Budget(rewrite=1, drill=1)):
    r = search(q)
    if r.no_match and budget.take("rewrite"):
        r = search(rewrite(q))
    if r.truncated and budget.take("drill"):
        r = drill(r.top1.chunk_id)
    return r
```

**什么时候才值得上状态机**：需要跨进程恢复（长任务队列）、状态 > 8、需要审计日志/可视化、或多人协作编排。当前均不满足。

### 3.4 责任链 —— **仅用于后置过滤器**

`drop_floor → min_coverage → dedupe_by_document` 三个纯函数串起来即可；不要为改写器/下钻器套 chain。

### 3.5 其他

- **Facade**：CLI 本身就是 Facade，保持。
- **Null Object**：`NoMatch` 结果用空结果对象替代异常，避免调用方到处判空——可选，收益中等。
- **Cache**：CLI 单次进程短命，无收益；若做常驻模式再考虑。
- **不引入**：pydantic（破坏零依赖）、langchain/llamaindex（29+ 依赖）、transitions（无状态机需求）。

---

## 4. 业界方案对比

| 方案 | 路由抽象 | 融合 | 对本项目的可借鉴点 | 是否引入 |
|:--|:--|:--|:--|:--|
| **LlamaIndex** RouterRetriever / RouterQueryEngine | Selector 选索引（默认 LLM、支持多选），多选结果按 node_id 去重 | TreeSummarize 合并 | "候选 + 选择器"分离；选择器返回**多选**而非单选 | 否（29 个直接依赖） |
| **LangChain** EnsembleRetriever | 不做路由，只做融合 | 加权 RRF `w/(rank+60)`，按 `id`/正文去重，源码 <200 行 | RRF 实现可直接照抄思路；去重键选择 | 否 |
| **LangChain** SelfQueryRetriever | LLM 产出 StructuredQuery → translator | — | 查询理解层，和"通道选择"正交 | 否 |
| **Haystack** | `ConditionalRouter` / `MetadataRouter` 组件 | `DocumentJoiner(join_mode=rrf)` | 路由与融合拆成两个可组合步骤 | 否（组件图过重） |
| **Semantic Kernel** | VectorStore → Collection → `IVectorSearchable` | — | "集合管理"与"只读检索"接口分离 | 否 |
| **FastMCP Client / Proxy** | `infer` 自动选 transport；proxy 做 HTTP↔stdio 桥 | — | 传输适配 + header 转发 + 命名空间隔离 | 只借鉴结构 |
| **langchain-mcp-adapters** | 多 server 配置 → 每 server 一个 session | — | 配置驱动、会话隔离 | 否 |
| **MCP 官方 Python SDK** | Streamable HTTP / stdio | — | 协议正确性 | 否（需 httpx/anyio/pydantic，破坏零依赖） |

**RRF 参数**：`k=60` 是 Elasticsearch / Weaviate / LangChain 的共同默认（Cormack 2009）。服务端已实现 `k=60`，客户端**不需要**再做融合，只需正确透传 `scoreKind`。

**质量门禁业界做法**：绝对阈值 + top1-top2 margin + 跨通道一致性；CRAG / Self-RAG 需要额外模型，单文件场景退化为启发式即可（见 P1-3）。

**查询改写**：Rewrite-Retrieve-Read / HyDE / MultiQuery 均需额外 LLM 调用；建议仅在 `NoMatch` 后调用 1 次本地规则改写（同义词表 + 去停用词），代码类查询跳过。

**库的取舍**

| 库 | 结论 |
|:--|:--|
| `click` | 可选。零依赖，能替代 `reorder_argv` 这种手工 hack；但现有 argparse 已够用，迁移收益低 |
| `jieba` | 可选。中文查询改写/分词有用，但 19 MB sdist |
| `rank-bm25` | 不选。依赖 numpy；服务端已有 FTS5 |
| `tenacity` | 不选。15 行手写重试足够 |
| `httpx` | 不选。官方 MCP SDK 依赖它，但本项目手写 JSON-RPC 只需 urllib |
| `rich` / `typer` / `pydantic` | 不选。破坏零依赖 |
| `urllib3` | 不选。`urllib3.Retry` 在 urllib.request 里用不上 |

---

## 5. 现有基础设施与可复用能力

### 5.1 已被系统验证可用、但技能未利用

| 能力 | 位置 | 价值 |
|:--|:--|:--|
| **原生 MCP 注册** | `claude mcp add --transport http reference-rag http://<server-ip>:5123/api/mcp -H "X-API-Key: rag_live_…"` | Agent 直接得到 26 个工具，无需调用 CLI，彻底绕开编码/退出码/路径三类问题。`claude mcp add --help` 已确认支持 `--transport http` 与 `-H` |
| MCP 无状态 + 无 Session 头 | 实测 `initialize` 无 `Mcp-Session-Id` | 注册为 HTTP MCP 后无需会话管理；每次 POST 自带鉴权头即可 |
| `search_file_content` | MCP 工具（ripgrep 风格，支持 regex/contextLines/subPath） | 精确字面量检索，绕过索引；CLI 未暴露 |
| `get_file_tree` | MCP 工具 | 目录树浏览；可替代"glob 猜路径" |
| `get_document_info` | MCP 工具 | chunk → 文档元数据；注意其 `file.path` 目前不可回读（见 P1-1） |
| `graph_search` / `graph_search_by_title` | MCP 工具 | Obsidian 双链图谱邻域遍历；可做"相关笔记"发现 |
| `list_files(pathKeyword=…)` | MCP 工具，返回权威 sourceKey/locator/connectorId | **路径解析的正确入口** |
| `/api/jobs` | REST | 索引任务进度（8 条历史任务，含 TotalUnits/ProcessedUnits） |
| `/api/sources` | REST | 数据源清单含 FileCount/ChunkCount/LastIndexed/KnowledgeBaseId |
| `/v1/chat/completions` | OpenAI 兼容端点，`model=reference-rag` 触发 RAG 模式 | 可直接产出带引用的答案，适合做 `ask` 子命令 |
| `rerank_results` | MCP | 服务端明确返回 `degraded:true`（GGUF 缺分类头），**不要使用其分数** |

### 5.2 环境依赖现状

- 已装：`requests`（未被使用）、`colorama`。
- 未装：`httpx`、`mcp`、`typer`、`rich`、`tenacity`、`pydantic`、`click`。
- 结论：**维持零依赖是当前唯一不需要装机的路线**；若改用官方 MCP SDK，需新增 httpx + anyio + pydantic-core 等 6+ 包，跨机器复制即失效。

### 5.3 安全与仓库卫生

| 项 | 现状 | 建议 |
|:--|:--|:--|
| API Key | 明文写在 `SKILL.md` 与 `scripts/rag_cli.py`，且已被提交并推送到 `origin`（`.agents` 仓，push remote = `https://github.com/hlrlive/agents.git`） | 轮换 Key；改为 `OBSIDIAN_RAG_API_KEY` 环境变量必填（缺失时报错而非回退默认值）；从 Git 历史清除（需用户明确授权重写历史） |
| 目录结构 | `.claude/skills` 是指向 `.agents/skills` 的符号链接；只有一份真实目录 | 无需处理；改 `.claude/skills/...` 等同于改 `.agents/skills/...` |
| `__pycache__` | 技能目录内存在 | 已在 `.gitignore`，可清理 |

> ⚠️ 安全提示：Key 出现在推送到远程的提交中，即使仓库为私有，也应视为已泄露。轮换优先于其他一切优化。Git 历史重写属破坏性操作，需单独授权后执行。

---

## 6. 目标架构

```
                 ┌──────────────────────────────────────┐
                 │              CLI (facade)            │
                 │  argparse → Settings → Policy        │
                 └───────────────┬──────────────────────┘
                                 │
                 ┌───────────────▼──────────────────────┐
                 │        RagClient (orchestrator)      │
                 │  retrieve()  Budget(rewrite=1)       │
                 │  post_filters = [floor, coverage,    │
                 │                  dedupe_doc]         │
                 └───────────────┬──────────────────────┘
                                 │ Transport (Protocol)
              ┌──────────────────┴───────────────────┐
              ▼                                      ▼
     ┌─────────────────┐                   ┌─────────────────┐
     │  McpTransport   │                   │  RestTransport  │
     │ JSON-RPC/SSE    │                   │ PascalCase REST │
     │ structuredContent│                  │ 降级通道         │
     │ errorCode→异常  │                   │ 无模式语义        │
     └────────┬────────┘                   └────────┬────────┘
              └──────────────┬─────────────────────┘
                             ▼
                    normalize() 单点
              ┌──────────────────────────────┐
              │ {sourceKey,startLine,endLine,│
              │  headingPath,score,channels, │
              │  chunkId,kbId,text,coverage} │
              └──────────────────────────────┘
                             │
                    render(format=md|brief|json)
```

关键决策：

1. **默认 MCP，REST 降级**（因为模式语义只在 MCP 存在）。
2. **错误统一为 `RagError(code, exit_code, retryable)`**，退出码表见 P0-2。
3. **normalize 只写一次**，渲染层不再出现 `X or x`。
4. **状态机与责任链框架不引入**；后置过滤用 3 个纯函数组成的 list。
5. **保留单文件**（目标 ≤ 900 行），确保"复制即用、零依赖"不退化。

---

## 7. 分阶段实施

### P0（半天，收益最高，不改变架构）

1. stdout/stderr 编码策略：按 `sys.stdout.encoding` 决策（UTF-8 则 reconfigure，否则 ASCII 标记），加 `--utf8` 强制开关。
2. 退出码模型 + `RagError`；所有 ❌ 路径改为非零退出；NoMatch → 3。
3. MCP 错误信封解析（`isError` / `structuredContent.errorCode` / `_meta`）。
4. REST 分支先判 `success`，禁止静默误报。
5. `search` 默认传输切 MCP；REST 降级时打印明确警告。
6. `chunk` 去掉重复探测请求；`list-files` 增加 truncation 提示。
7. 数值参数校验（top_k 1–50；start_line ≤ end_line）。
8. 有界重试（仅网络/5xx，2 次，指数退避）。
9. SKILL.md：全部命令加 `-X utf8`，修正服务地址、工具数量（26）、移除"25 工具"表述。

### P1（1–2 天，结构化重构）

1. `Transport` Protocol + `McpTransport` / `RestTransport`。
2. `normalize()` 单点，删除 40+ 处双拼字段读取。
3. `SearchPolicy` dict + `--policy exact|concept|balanced`。
4. 路径解析 helper（候选路径 × 源探测 + 缓存）；`physicalFiles==0` 的源给出可诊断错误。
5. 后置过滤链：`drop_floor`、`min_coverage`、`dedupe_by_document`。
6. `--format brief|md|json`、`--max-chars`、`--quiet`。
7. 新增子命令：`tree`、`grep`（`search_file_content`）、`doc-info`、`graph`、`status`（`/api/jobs` + `/api/sources`）。
8. 冒烟测试脚本（见 §8）。

### P2（按需）

1. 在 Claude Code 注册原生 MCP（与 CLI 并存，Agent 优先用 MCP）。
2. `ask` 子命令（`/v1/chat/completions`，`model=reference-rag`，输出带引用）。
3. NoMatch 后的本地规则改写（同义词表 + 去停用词，预算 1 次）。
4. 服务质量评估集（20–30 条查询 + 期望文件），用于校准 coverage 阈值与路由策略。

---

## 8. 验收标准

### 8.1 功能矩阵

| 用例 | 期望 |
|:--|:--|
| `python rag_cli.py ping`（无 `-X utf8`，管道/文件/控制台） | exit 0，无 traceback，中文与 emoji 正确或降级为 ASCII |
| `PYTHONIOENCODING=gbk python rag_cli.py ping` | 不崩溃（backslashreplace） |
| `rag_cli.py read no.md` | exit 4，stderr 单行错误 |
| `rag_cli.py search x --api-key bad` | exit 5，输出 ❌，绝不出现"成功命中" |
| `rag_cli.py chunk <id>`（无 `--kb`） | 请求数 ≤ 2，命中即返回 |
| `rag_cli.py search q --mode hybrid` | 实际走 MCP，通道含 vector |
| `rag_cli.py search q --mode semantic --mcp` | `channels` 出现 vector（概念类查询） |
| `rag_cli.py list-files "*.md" --limit 1000` | 显示 `已截断（500/上限 500）` |
| `rag_cli.py read TNote/… --source "Notes - Note"` | 成功（保持现有可用路径） |
| `rag_cli.py read TNote/… --source "Notes - TNote"` | 给出"该源未挂载物理文件"的明确提示，或自动切到可用源 |
| 服务端 503 / 超时 | 重试 2 次后 exit 6 |
| `--top-k 0` / `--top-k 999` | 参数校验失败 exit 2 |
| `read --start-line 10 --end-line 5` | exit 2 |

### 8.2 冒烟脚本（建议）

```python
# scripts/smoke_test.py —— 不依赖 pytest，逐条跑子命令并断言退出码/关键字
CASES = [
    (["ping"], 0, "pong"),
    (["stats"], 0, "数据总量"),
    (["search", "Where", "--mode", "lexical", "--top-k", "1"], 0, "切片ID"),
    (["search", "完全不存在的词zzzz"], 3, "未命中"),
    (["read", "no/such.md"], 4, "不存在"),
    (["list-files", "Where.md"], 0, "共"),
]
```

在 `PYTHONIOENCODING=gbk` 与 `PYTHONUTF8=1` 两种环境下各跑一遍。

---

## 9. 未决问题与风险

| 项 | 说明 | 需要谁决定 |
|:--|:--|:--|
| API Key 轮换与 Git 历史处理 | 已推送泄露；重写历史不可逆 | 用户 |
| REST 是否保留 | `Mode` 未实现；若服务端短期不修，REST 只剩"词法/混合"二态 | 用户 / 服务端 |
| `Notes - TNote` 源 | 物理文件未挂载，文件级命令永久失败；是修挂载还是从 `list_sources` 隐藏 | 服务端 |
| `get_document_info.file.path` 不可回读 | 服务端坐标系不一致 | 服务端 |
| NoMatch 阈值 | 需标注集；当前只能用启发式（coverage ≥ 0.5） | 用户 / 后续评估 |
| 文档写入位置 | 已写入 `skills/ReferenceRAG/docs/`（技能内 docs，符合仓库既有约定：codegraph-analysis、vsmcp-debug 均如此） | 无需决定 |
| 是否注册原生 MCP | 注册后 Agent 行为改变（26 工具常驻），需评估上下文开销 | 用户 |

---

## 10. 附录：关键实测数据

### 10.1 延迟

| 模式 | 客户端观测 | 服务端自报 |
|:--|:--|:--|
| lexical | 38–41 ms | 15.6 ms |
| semantic | 77 ms | 68 ms |
| auto | 92 ms | 67.8 ms |
| hybrid | 235 ms | 211.8 ms |

冷启动/异常：端口拒绝 2.3 s；主机不可达 21.3 s；CLI 进程启动 155–165 ms。

### 10.2 并发与连接

| 场景 | 总耗时 |
|:--|:--|
| 8 × lexical 串行 | 361 ms |
| 8 × lexical 并行（8 线程） | 180 ms（2.0x） |
| 5 × hybrid 每请求新连接 | 382 ms |
| 5 × hybrid keep-alive | 348 ms（−9%） |

### 10.3 载荷

| 项 | 字节 |
|:--|:--|
| MCP 原始响应（TopK=5） | 18,837 |
| 其中 `content[0].text` | 8,226 |
| 其中 `structuredContent` | 6,085 |
| CLI 文本输出 | 4,848 |
| CLI `--json` | 13,899 |
| 切片正文合计 | 1,627 字符 |

### 10.4 服务端工具清单（26）

`knowledge_search` `hybrid_search` `lexical_search` `semantic_search` `knowledge_base_search`
`knowledge_base_drill_down` `get_document_info` `get_note_outline` `read_note_content`
`list_files` `find_files_by_pattern` `get_file_tree` `search_file_content`
`graph_search` `graph_search_by_title`
`knowledge_base_list` `list_sources` `get_source_info` `get_system_stats` `get_index_status`
`get_service_info` `ping`
`embed_text` `embed_batch` `calculate_similarity` `rerank_results`

带 `outputSchema` 的 7 个：`knowledge_base_search`、`knowledge_base_list`、`list_files`、`get_document_info`、`get_system_stats`、`get_index_status`、`list_sources`。

### 10.5 复现命令

```bash
# 编码崩溃
python .claude/skills/ReferenceRAG/scripts/rag_cli.py ping

# 退出码
python -X utf8 .claude/skills/ReferenceRAG/scripts/rag_cli.py read no.md; echo $?

# REST 静默误报
python -X utf8 .claude/skills/ReferenceRAG/scripts/rag_cli.py search Where --api-key bad_key

# REST Mode 被忽略（对比 ChunkId 与 Score）
curl -s -X POST http://<server-ip>:5123/api/knowledge-base-search \
  -H 'Content-Type: application/json' -H 'X-API-Key: <key>' \
  -d '{"Query":"如何判断集合里有没有元素","TopK":5,"UseLexicalTokenizer":false,"Mode":"semantic"}'

# glob 只匹配文件名
python -X utf8 .claude/skills/ReferenceRAG/scripts/rag_cli.py list-files 'TNote/*'

# 源根重复拼接
python -X utf8 .claude/skills/ReferenceRAG/scripts/rag_cli.py read 'TNote/知识库/概念/C#.NET开发/Where.md' --source 'Notes - TNote'

# MCP 错误信封丢失
python -X utf8 -c "import importlib.util as u; s=u.spec_from_file_location('r',r'.claude/skills/ReferenceRAG/scripts/rag_cli.py'); m=u.module_from_spec(s); s.loader.exec_module(m); print(m.call_mcp_tool('nope_tool',{}))"
```
