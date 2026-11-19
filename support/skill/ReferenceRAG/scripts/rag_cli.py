#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ReferenceRAG CLI - 跨平台企业级多知识库检索与管理客户端门面
架构设计：
1. 适配器模式 (Adapter Pattern)：统一抽象 MCP JSON-RPC 与 REST 传输通道，实现单一出口数据归一化
2. 策略模式 (Strategy Pattern)：解耦 exact/concept/balanced 检索策略与后置质量过滤链
3. 容错与重试机制：指数退避有界重试、网络故障分类与精准退出码语义规范
4. 跨平台编码安全：动态适配控制台与重定向管道，安全降级避免 GBK 乱码与 Unicode 崩溃
5. Token 预算工程：支持 brief/md/json 格式化输出与正文截断，削减 Agent 上下文负载
6. 零外部依赖：纯 Python 3 标准库（urllib + json + argparse + re + dataclasses）实现
"""

import sys
import os
import re
import json
import time
import argparse
import urllib.request
import urllib.error
from dataclasses import dataclass, asdict
from typing import Optional, List, Dict, Any, Tuple

# ----------------- 退出码与错误模型 -----------------

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_NO_MATCH = 3
EXIT_NOT_FOUND = 4
EXIT_AUTH = 5
EXIT_NETWORK = 6
EXIT_SERVER = 7

class RagError(Exception):
    def __init__(self, code: str, message: str, exit_code: int = EXIT_SERVER, retryable: bool = False, details: Optional[Dict[str, Any]] = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.exit_code = exit_code
        self.retryable = retryable
        self.details = details or {}

# ----------------- 跨平台流与符号配置 -----------------

def _configure_streams(force_utf8: bool = False) -> bool:
    """按原始 stdout.encoding 与 force_utf8 决定是否使用 Unicode 符号。"""
    enc = (getattr(sys.stdout, "encoding", "") or "").lower()
    unicode_ok = enc.startswith("utf") or force_utf8 or os.environ.get("OBSIDIAN_RAG_FORCE_UTF8") == "1"
    if unicode_ok:
        for stream in (sys.stdout, sys.stderr):
            try:
                stream.reconfigure(encoding="utf-8", errors="backslashreplace")
            except Exception:
                pass
    return unicode_ok

# 预计算默认编码状态
UNICODE_OK = _configure_streams()
SYM_OK = "✅" if UNICODE_OK else "[OK]"
SYM_WARN = "⚠️" if UNICODE_OK else "[WARN]"
SYM_FAIL = "❌" if UNICODE_OK else "[FAIL]"

def update_symbols(force_utf8: bool):
    global UNICODE_OK, SYM_OK, SYM_WARN, SYM_FAIL
    UNICODE_OK = _configure_streams(force_utf8)
    SYM_OK = "✅" if UNICODE_OK else "[OK]"
    SYM_WARN = "⚠️" if UNICODE_OK else "[WARN]"
    SYM_FAIL = "❌" if UNICODE_OK else "[FAIL]"

# 允许从环境变量读取，默认对齐生产环境
DEFAULT_BASE_URL = os.environ.get("OBSIDIAN_RAG_API_URL", "http://127.0.0.1:5123")
DEFAULT_API_KEY = os.environ.get("OBSIDIAN_RAG_API_KEY", "rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")

# ----------------- 数据模型归一化 (Normalize) -----------------

@dataclass
class NormalizedChunk:
    chunk_id: str
    document_id: str
    knowledge_base_id: str
    source_key: str
    locator: str
    start_line: int
    end_line: int
    heading_path: str
    text: str
    score: float
    score_kind: str
    channels: List[str]
    coverage: float
    is_floor: bool

def normalize_chunk(item: Dict[str, Any]) -> NormalizedChunk:
    """单一归一化出口，彻底消除 40+ 处 PascalCase/camelCase 双拼逻辑"""
    def get_val(*keys, default=""):
        for k in keys:
            if k in item and item[k] is not None:
                return item[k]
        return default

    channels_raw = get_val("channels", "Channels", default=[])
    if isinstance(channels_raw, list):
        channels = [str(c) for c in channels_raw]
    elif isinstance(channels_raw, str):
        channels = [c.strip() for c in channels_raw.split(",") if c.strip()]
    else:
        channels = []

    try:
        score_val = float(get_val("score", "Score", default=0.0))
    except (ValueError, TypeError):
        score_val = 0.0

    try:
        start_line = int(get_val("startLine", "StartLine", default=0))
    except (ValueError, TypeError):
        start_line = 0

    try:
        end_line = int(get_val("endLine", "EndLine", default=0))
    except (ValueError, TypeError):
        end_line = 0

    try:
        cov_val = float(get_val("coverage", "Coverage", default=0.0))
    except (ValueError, TypeError):
        cov_val = 0.0

    return NormalizedChunk(
        chunk_id=str(get_val("chunkId", "ChunkId")),
        document_id=str(get_val("documentId", "DocumentId")),
        knowledge_base_id=str(get_val("knowledgeBaseId", "KnowledgeBaseId")),
        source_key=str(get_val("sourceKey", "SourceKey", "documentSourceKey", "DocumentSourceKey", default="未知来源")),
        locator=str(get_val("locator", "Locator")),
        start_line=start_line,
        end_line=end_line,
        heading_path=str(get_val("headingPath", "HeadingPath")),
        text=str(get_val("text", "Text")),
        score=score_val,
        score_kind=str(get_val("scoreKind", "ScoreKind", default="RRF")),
        channels=channels,
        coverage=cov_val,
        is_floor=bool(get_val("isFloor", "IsFloor", default=False))
    )

# ----------------- HTTP 客户端适配层 (含连接重用与有界重试) -----------------

def request_http(endpoint: str, method: str = "GET", payload: Optional[Dict[str, Any]] = None,
                 base_url: str = DEFAULT_BASE_URL, api_key: str = DEFAULT_API_KEY,
                 timeout: int = 15, max_retries: int = 2) -> Dict[str, Any]:
    url = f"{base_url.rstrip('/')}/{endpoint.lstrip('/')}"
    headers = {
        "User-Agent": "ReferenceRAG-Skill-CLI/2.5"
    }
    if api_key:
        headers["X-API-Key"] = api_key
        headers["Authorization"] = f"Bearer {api_key}"

    data_bytes = None
    if payload is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        data_bytes = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    last_error: Optional[Exception] = None
    for attempt in range(max_retries + 1):
        if attempt > 0:
            backoff = 0.3 * (3 ** (attempt - 1))
            time.sleep(backoff)

        req = urllib.request.Request(url, data=data_bytes, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                content = resp.read().decode("utf-8", errors="replace")
                try:
                    return json.loads(content)
                except json.JSONDecodeError:
                    return {"_raw": content}
        except urllib.error.HTTPError as e:
            err_body = e.read().decode("utf-8", errors="replace")
            try:
                err_json = json.loads(err_body)
            except Exception:
                err_json = {"error": err_body}

            # 4xx 业务/鉴权错误绝不重试，立即根据语义退出
            if e.code in (401, 403):
                raise RagError("auth_failed", f"HTTP {e.code} 鉴权拒绝: {err_json.get('error') or err_json.get('message') or err_body}", exit_code=EXIT_AUTH, details=err_json)
            if e.code == 404:
                raise RagError("not_found", f"HTTP 404 资源不存在: {err_json.get('error') or err_json.get('message') or err_body}", exit_code=EXIT_NOT_FOUND, details=err_json)
            if e.code == 400:
                raise RagError("bad_request", f"HTTP 400 请求参数无效: {err_json.get('error') or err_json.get('message') or err_body}", exit_code=EXIT_USAGE, details=err_json)

            # 5xx 服务端错误按重试策略处理
            last_error = RagError("server_error", f"HTTP {e.code} 服务端异常: {err_body}", exit_code=EXIT_SERVER, retryable=True, details=err_json)
        except (urllib.error.URLError, TimeoutError) as e:
            reason = getattr(e, "reason", str(e))
            last_error = RagError("network_error", f"网络连接异常: {reason}", exit_code=EXIT_NETWORK, retryable=True)
        except Exception as e:
            last_error = RagError("request_error", f"请求执行异常: {str(e)}", exit_code=EXIT_SERVER)

    if last_error:
        raise last_error
    raise RagError("unknown_error", "未知的网络请求错误", exit_code=EXIT_NETWORK)

def call_mcp_tool(tool_name: str, arguments: Dict[str, Any], base_url: str = DEFAULT_BASE_URL,
                  api_key: str = DEFAULT_API_KEY, timeout: int = 20) -> Dict[str, Any]:
    """统一 MCP JSON-RPC 调用入口，严格解析错误信封 (isError, errorCode) 并抛出语义异常"""
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments
        }
    }
    raw = request_http("api/mcp", method="POST", payload=payload, base_url=base_url, api_key=api_key, timeout=timeout)
    if "error" in raw:
        err = raw["error"]
        msg = err.get("message") if isinstance(err, dict) else str(err)
        raise RagError("mcp_rpc_error", f"MCP JSON-RPC 协议错误: {msg}", exit_code=EXIT_SERVER)

    res = raw.get("result", {})
    sc = res.get("structuredContent") or {}
    meta = res.get("_meta") or {}
    error_code = sc.get("errorCode") or meta.get("errorCode") or ("error" if res.get("isError") else None)

    content_list = res.get("content") or []
    text_content = content_list[0].get("text", "") if (isinstance(content_list, list) and content_list) else ""

    if res.get("isError") or error_code:
        err_msg = sc.get("error") or sc.get("message") or text_content or f"MCP 工具 {tool_name} 执行失败"
        exit_code = EXIT_SERVER
        if error_code in ("invalid_argument", "missing_argument"):
            exit_code = EXIT_USAGE
        elif error_code in ("not_found", "file_not_found"):
            exit_code = EXIT_NOT_FOUND
        elif error_code in ("path_denied", "forbidden"):
            exit_code = EXIT_AUTH
        elif error_code == "no_knowledge_base":
            exit_code = EXIT_NOT_FOUND
        raise RagError(str(error_code or "mcp_error"), str(err_msg), exit_code=exit_code, details=sc)

    if sc:
        return sc
    if text_content:
        try:
            return json.loads(text_content)
        except Exception:
            return {"text": text_content}
    return res

# ----------------- 路由启发式与策略模式 -----------------

CODE_HINTS = re.compile(r"""
  [A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+   # 点号成员/命名空间 (如 List.Add, HttpClient.SendAsync)
| [A-Za-z_][A-Za-z0-9_]*\s*\(                        # 函数调用 (如 MoveNext())
| \b[A-Z][A-Za-z0-9]{2,}(Exception|Error)\b          # 异常类 (如 NullReferenceException)
| \b(0x[0-9A-Fa-f]+|E[A-Z]{3,}|CS\d{4})\b            # 十六进制/编译错误码 (如 CS8618)
| (错误码|异常|状态码|报错)\s*[:：#]?\s*\d{3,5}        # 中文错误码模式
| ^[A-Za-z0-9_]{2,40}$                               # 经典标识符 (含大写或下划线)
""", re.X)

def is_code_or_exact_token(query: str) -> bool:
    """扩展启发式规则，精准捕获点号成员、命名空间与异常类名"""
    if not query:
        return False
    trimmed = query.strip()
    if (trimmed.startswith('"') and trimmed.endswith('"')) or (trimmed.startswith('“') and trimmed.endswith('”')):
        return True
    if CODE_HINTS.search(trimmed):
        if trimmed.isalnum() and trimmed.islower():
            return False  # 纯小写单个单词默认允许走混合融合
        return True
    return False

@dataclass(frozen=True)
class SearchPolicy:
    name: str
    mode: str
    drop_floor: bool
    min_coverage: float
    max_per_doc: int

POLICIES: Dict[str, SearchPolicy] = {
    "exact":    SearchPolicy("exact", "lexical", True, 0.0, 5),
    "concept":  SearchPolicy("concept", "hybrid", True, 0.4, 2),
    "balanced": SearchPolicy("balanced", "auto", True, 0.2, 3),
}

def filter_chunks(chunks: List[NormalizedChunk], policy: SearchPolicy) -> List[NormalizedChunk]:
    """后置质量过滤链：剔除凑底切片、低覆盖切片与同文档刷屏"""
    filtered = []
    doc_counts: Dict[str, int] = {}
    for c in chunks:
        if policy.drop_floor and c.is_floor:
            continue
        if policy.min_coverage > 0 and c.coverage > 0 and c.coverage < policy.min_coverage:
            continue
        cnt = doc_counts.get(c.document_id, 0)
        if cnt >= policy.max_per_doc and c.document_id:
            continue
        doc_counts[c.document_id] = cnt + 1
        filtered.append(c)
    return filtered

# ----------------- 数据源与路径智能解析 -----------------

def resolve_file_source_and_path(file_path: str, user_source: Optional[str],
                                 base_url: str, api_key: str) -> Tuple[str, str]:
    """解析权威数据源与物理路径；遇到未挂载物理文件的源给出诊断"""
    sources_res = call_mcp_tool("list_sources", {}, base_url=base_url, api_key=api_key)
    sources = sources_res.get("sources", [])
    if not sources:
        return user_source or "", file_path

    # 若用户指定了数据源，先检查物理文件状态
    if user_source:
        matched = next((s for s in sources if s.get("name") == user_source), None)
        if matched and matched.get("fileCount", 0) == 0:
            raise RagError("source_unmounted",
                           f"数据源 '{user_source}' 物理目录未挂载 (fileCount=0)，文件读取命令不可用。请改用 'chunk' 切片下钻或 'grep' 内容检索。",
                           exit_code=EXIT_NOT_FOUND)
        return user_source, file_path

    # 智能探测：若未指定源，优先尝试有效挂载的数据源
    active_sources = [s for s in sources if s.get("fileCount", 0) > 0]
    if active_sources:
        # 如果路径里包含某个活跃源的名字特征，直接匹配
        for s in active_sources:
            s_name = s.get("name", "")
            if s_name.lower() in file_path.lower():
                return s_name, file_path
        # 默认取第一个活跃知识库源
        return active_sources[0].get("name", ""), file_path

    return "", file_path

# ----------------- 子命令实现 -----------------

def cmd_ping(args):
    """服务连通性探活"""
    res = call_mcp_tool("ping", {}, base_url=args.base_url, api_key=args.api_key)
    if res.get("status") == "ok" or res.get("message") == "pong" or "pong" in str(res):
        print(f"{SYM_OK} 服务连通正常: {args.base_url}")
        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
        else:
            print(f"响应: {json.dumps(res, ensure_ascii=False)}")
    else:
        raise RagError("ping_failed", f"连通测试异常: {res}", exit_code=EXIT_NETWORK)

def cmd_stats(args):
    """获取系统统计与模型运行健康度"""
    res = call_mcp_tool("get_system_stats", {}, base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    embed_info = res.get("embedding", {})
    rerank_info = res.get("rerank", {})
    idx_info = res.get("index", {})

    print("### ReferenceRAG 系统与索引状态")
    print(f"- **数据总量**: {idx_info.get('totalDocuments', 0)} 篇文档 / {idx_info.get('totalChunks', 0)} 分块 / {idx_info.get('totalVectors', 0)} 向量")
    print(f"- **嵌入模型**: `{embed_info.get('modelName')}` (CUDA: {embed_info.get('useCuda')}, SeqLen: {embed_info.get('maxSequenceLength')})")
    print(f"- **重排模型**: `{rerank_info.get('modelName')}` (健康度: `{rerank_info.get('health')}`)")
    if rerank_info.get("degradationReason"):
        print(f"  > {SYM_WARN} 重排降级说明: {rerank_info.get('degradationReason')}")

def cmd_list_kbs(args):
    """列出可用知识库"""
    res = request_http("api/knowledge-bases", method="GET", base_url=args.base_url, api_key=args.api_key)
    if isinstance(res, list):
        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
            return
        print("### 系统已注册知识库列表\n")
        for kb in res:
            spec = kb.get("EmbeddingSpecId", "")
            mode = "纯词法 (BM25)" if "lexical" in spec.lower() else f"混合向量 ({spec})"
            print(f"- **{kb.get('Name')}** (ID: `{kb.get('Id')}`)")
            print(f"  模式: {mode} | 启用: {kb.get('Enabled')} | 代次: rev{kb.get('EffectiveConfigRevision')}")
    else:
        raise RagError("list_kbs_failed", f"列出知识库失败: {res.get('error', res)}", exit_code=EXIT_SERVER)

def cmd_list_sources(args):
    """列出物理数据源及切片统计"""
    res = call_mcp_tool("list_sources", {}, base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    sources = res.get("sources", [])
    print(f"### 数据源清单（共 {len(sources)} 个源，覆盖 {res.get('knowledgeBaseCount', 0)} 个知识库）\n")
    for s in sources:
        state_warn = f" ({SYM_WARN} 未挂载物理文件)" if s.get("fileCount", 0) == 0 else ""
        print(f"- **{s.get('name')}** (`{s.get('path')}`){state_warn}")
        print(f"  文件数: {s.get('fileCount')} | 切片数: {s.get('chunkCount')} | 来源: {s.get('origin')}")

def cmd_list_files(args):
    """按模式或路径前缀查找文件"""
    limit = max(1, min(args.limit, 500))

    if args.path_keyword:
        # 使用精确路径关键字检索（支持路径段匹配）
        mcp_args = {"pathKeyword": args.path_keyword, "limit": limit}
        res = call_mcp_tool("list_files", mcp_args, base_url=args.base_url, api_key=args.api_key)
        files = res.get("files", [])
        total = res.get("total", len(files))
        truncated = res.get("truncated", False)
        print(f"### 文件路径匹配结果（关键字: `{args.path_keyword}`，呈现 {len(files)} / 共 {total} 条{' [已截断]' if truncated else ''}）\n")
        for f in files:
            source = f.get("sourceName", "")
            src_tag = f"[{source}] " if source else ""
            print(f"- `{src_tag}{f.get('path')}` (分块数: {f.get('chunkCount')})")
    else:
        # 使用 Glob 文件名模式匹配
        pattern = args.pattern or "*.*"
        mcp_args = {"globPattern": pattern, "maxResults": limit}
        if args.source:
            mcp_args["sourceName"] = args.source
        res = call_mcp_tool("find_files_by_pattern", mcp_args, base_url=args.base_url, api_key=args.api_key)
        files = res.get("files", [])
        total = res.get("count", len(files))
        print(f"### 文件名匹配结果（模式: `{pattern}`，共 {total} 条{' [已截断上限 500]' if len(files) >= 500 else ''}）\n")
        for f in files:
            print(f"- `{f}`")

def cmd_search(args):
    """统一知识库检索入口（默认 MCP 通道，保证模式语义真实生效）"""
    top_k = max(1, min(args.top_k, 50))
    policy = POLICIES.get(args.policy, POLICIES["balanced"])
    mode = args.mode or policy.mode

    # 1. 模式与意图解析
    if args.lexical:
        mode = "lexical"

    # 若为 auto，进行启发式智能检测
    auto_decision_desc = ""
    if mode == "auto":
        is_code = is_code_or_exact_token(args.query)
        if is_code:
            mode = "lexical"
            auto_decision_desc = "auto (词法代码通道)"
        else:
            mode = "hybrid"
            auto_decision_desc = "auto (混合向量通道)"

    chunks: List[NormalizedChunk] = []
    raw_response: Dict[str, Any] = {}
    elapsed = "0ms"
    score_kind = "RRF"
    no_match = False
    no_match_reason = ""

    # 2. 通道调用 (REST 与 MCP 共享单轨检索契约)
    if args.rest:
        payload = {
            "query": args.query,
            "mode": mode,
            "topK": top_k,
            "allowDegrade": True
        }
        if args.kb:
            payload["knowledgeBaseIds"] = [args.kb]
        if getattr(args, "connector", None):
            payload["connectorId"] = args.connector
        if getattr(args, "path_prefix", None):
            payload["pathPrefix"] = args.path_prefix

        res = request_http("api/knowledge-base-search", method="POST", payload=payload,
                           base_url=args.base_url, api_key=args.api_key)
        raw_response = res
        hits = res.get("hits") or []
        items = res.get("items") or res.get("Items") or []
        chunks = [normalize_chunk(h) for h in hits] if hits else [normalize_chunk(it) for it in items]
        outcome = res.get("outcome") or ("matched" if chunks else "no_match")
        no_match = outcome != "matched" or bool(res.get("noMatch") or res.get("NoMatch") or False)
        no_match_reason = res.get("noMatchReason") or res.get("NoMatchReason") or ""
        elapsed = res.get("elapsed") or f"{res.get('elapsedMs', 0)}ms"
        score_kind = res.get("scoreKind") or res.get("ScoreKind") or "RRF"
    else:
        # 权威 MCP 通道
        mcp_args = {
            "query": args.query,
            "mode": mode,
            "topK": top_k
        }
        if args.kb:
            mcp_args["knowledgeBaseIds"] = json.dumps([args.kb])
        res = call_mcp_tool("knowledge_search", mcp_args, base_url=args.base_url, api_key=args.api_key)
        raw_response = res
        hits = res.get("hits") or []
        items = res.get("items") or []
        chunks = [normalize_chunk(h) for h in hits] if hits else [normalize_chunk(it) for it in items]
        outcome = res.get("outcome") or ("matched" if chunks else "no_match")
        no_match = outcome != "matched" or bool(res.get("noMatch", False))
        no_match_reason = res.get("noMatchReason", "")
        elapsed = f"{res.get('elapsedMs', 0)}ms"
        score_kind = res.get("scoreKind", "RRF")

    # 3. 执行后置过滤链
    filtered_chunks = filter_chunks(chunks, policy)
    if not filtered_chunks and chunks and not no_match:
        no_match = True
        no_match_reason = "召回切片未能通过质量门禁（低相关度或属于兜底切片）"

    # 4. 格式化输出
    tool_label = auto_decision_desc or mode
    if args.format == "json":
        out_payload = {
            "query": args.query,
            "mode": mode,
            "policy": policy.name,
            "noMatch": no_match,
            "noMatchReason": no_match_reason,
            "elapsed": elapsed,
            "scoreKind": score_kind,
            "totalRecalled": len(chunks),
            "returnedCount": len(filtered_chunks),
            "items": [asdict(c) for c in filtered_chunks]
        }
        print(json.dumps(out_payload, ensure_ascii=False, indent=2))
        if no_match:
            sys.exit(EXIT_NO_MATCH)
        return

    print(f"### ReferenceRAG 知识库检索: `{args.query}` (模式: `{tool_label}`, 策略: `{policy.name}`)")
    print(f"- **状态**: {f'{SYM_WARN} 未命中 (NoMatch)' if no_match else f'{SYM_OK} 成功命中'}")
    print(f"- **召回条数**: 质检后 {len(filtered_chunks)} 条 / 初筛 {len(chunks)} 条 | **耗时**: {elapsed} | **评分**: {score_kind}")

    if no_match:
        print(f"> {SYM_WARN} **未命中判定**: {no_match_reason or '检索切片中未包含查询词核心二元组/标识'}")
        print("> 建议：去除口语助词、补充专业技术名词、或切换 `--mode lexical` 精确词法模式重试。\n")
        sys.exit(EXIT_NO_MATCH)

    print()
    max_chars = args.max_chars
    for i, it in enumerate(filtered_chunks, 1):
        channels_str = ",".join(it.channels)
        heading = f" > {it.heading_path}" if it.heading_path else ""

        if args.format == "brief":
            # 精简模式：极大节约 Agent 检索上下文预算
            print(f"{i}. `[[{it.source_key}#L{it.start_line}-L{it.end_line}]]`{heading} (得分: `{it.score:.4f}`, 库: `{it.knowledge_base_id}`, 切片ID: `{it.chunk_id}`)")
        else:
            # 标准 Markdown 格式
            body_text = it.text.strip()
            truncated_hint = ""
            if len(body_text) > max_chars:
                body_text = body_text[:max_chars].rstrip()
                truncated_hint = f"\n\n... [正文已截断，使用 `rag_cli.py chunk {it.chunk_id}` 展开全文]"

            print(f"#### {i}. `[[{it.source_key}#L{it.start_line}-L{it.end_line}]]`{heading}")
            print(f"- **得分**: `{it.score:.4f}` ({score_kind}) | **通道**: `{channels_str}` | **库ID**: `{it.knowledge_base_id}` | **切片ID**: `{it.chunk_id}`")
            print("```markdown")
            print(f"{body_text}{truncated_hint}")
            print("```\n")

def cmd_chunk(args):
    """根据 chunkId 与 knowledgeBaseId 精确展开切片完整正文 (无须指定物理文件路径)"""
    kb_id = args.kb
    chunk_id = args.chunk_id.strip()

    # 1. 若未显式指定 kb_id，自动从可用库中单次探测
    if not kb_id:
        kbs = request_http("api/knowledge-bases", method="GET", base_url=args.base_url, api_key=args.api_key)
        if isinstance(kbs, list):
            for candidate in kbs:
                cid = candidate.get("Id")
                try:
                    probe = request_http(f"api/knowledge-bases/{cid}/chunks/{chunk_id}",
                                         method="GET", base_url=args.base_url, api_key=args.api_key)
                    if isinstance(probe, dict) and (probe.get("Id") == chunk_id or probe.get("id") == chunk_id):
                        kb_id = cid
                        break
                except RagError:
                    continue

    # 2. 若依然未命中，回退到 MCP 下钻探测
    if not kb_id:
        try:
            mcp_res = call_mcp_tool("knowledge_base_drill_down", {"chunkId": chunk_id},
                                    base_url=args.base_url, api_key=args.api_key)
            if isinstance(mcp_res, dict) and mcp_res.get("chunk"):
                chunk_data = mcp_res["chunk"]
                kb_id = mcp_res.get("knowledgeBaseId", "未知库")
                _render_single_chunk(chunk_id, kb_id, chunk_data, args.json)
                return
        except RagError:
            pass
        raise RagError("chunk_not_found", f"未能在任何知识库中定位到切片 `{chunk_id}`。请确认 ID 是否正确或通过 `--kb` 指定目标库。", exit_code=EXIT_NOT_FOUND)

    res = request_http(f"api/knowledge-bases/{kb_id}/chunks/{chunk_id}",
                       method="GET", base_url=args.base_url, api_key=args.api_key)
    _render_single_chunk(chunk_id, kb_id, res, args.json)

def _render_single_chunk(chunk_id: str, kb_id: str, chunk_obj: Dict[str, Any], is_json: bool):
    if is_json:
        print(json.dumps(chunk_obj, ensure_ascii=False, indent=2))
        return

    c = normalize_chunk(chunk_obj)
    tokens = chunk_obj.get("TokenCount") or chunk_obj.get("tokenCount") or 0
    ordinal = chunk_obj.get("Ordinal") if chunk_obj.get("Ordinal") is not None else chunk_obj.get("ordinal", 0)

    print(f"### 切片正文详情: `{chunk_id}`")
    print(f"- **所属知识库**: `{kb_id}` | **来源文档**: `[[{c.source_key}#L{c.start_line}-L{c.end_line}]]`")
    if c.heading_path:
        print(f"- **章节大纲路径**: `{c.heading_path}`")
    print(f"- **分块序号**: {ordinal} | **Token 预估**: {tokens} | **行号范围**: L{c.start_line} ~ L{c.end_line}\n")
    print("```markdown")
    print(c.text.strip())
    print("```")

def cmd_outline(args):
    """获取指定文档大纲（H1-H6 标题树与分块状态）"""
    source, path = resolve_file_source_and_path(args.file_path, args.source, args.base_url, args.api_key)
    mcp_args = {"filePath": path}
    if source:
        mcp_args["sourceName"] = source

    res = call_mcp_tool("get_note_outline", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    chunk_count = res.get("ragChunkCount") or res.get("chunkCount") or "未索引"
    print(f"### 文档大纲: `{path}` (数据源: `{source or '自动推断'}`)")
    print(f"- **总行数**: {res.get('totalLines')} | **切片数**: {chunk_count}")
    headings = res.get("headings", [])
    if headings:
        print("\n**标题层级结构：**")
        for h in headings:
            indent = "  " * (h.get("level", 1) - 1)
            line_no = h.get("lineNumber") or h.get("line") or 1
            print(f"{indent}- H{h.get('level')} `{h.get('title')}` (L{line_no})")
    else:
        print("\n> 该文档无 Markdown 标题层级。")

def cmd_read(args):
    """读取文件行正文或批量回读已验证证据"""
    citation_input = getattr(args, "citations", None) or getattr(args, "citation", None)
    if citation_input:
        try:
            raw_s = citation_input.strip()
            if raw_s.startswith("{"):
                cit_list = [json.loads(raw_s)]
            else:
                cit_list = json.loads(raw_s)
        except Exception as e:
            raise RagError("invalid_citation", f"citation JSON 解析失败: {e}", exit_code=EXIT_USAGE)

        payload = {"citations": cit_list}
        res = request_http("api/knowledge-evidence/read", method="POST", payload=payload,
                           base_url=args.base_url, api_key=args.api_key)
        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
            return

        items = res.get("items") or []
        print(f"### 批量证据回读结果 ({len(items)} 项)：\n")
        for idx, item in enumerate(items, start=1):
            cit = item.get("citation", {})
            status = item.get("status", "unknown")
            icon = SYM_OK if status == "ok" else SYM_FAIL
            print(f"- {icon} [{idx}] **切片 `{cit.get('chunkId')}`** (知识库: `{cit.get('knowledgeBaseId')}`, 版本: v{cit.get('documentVersion')}, 状态: `{status}`)")
            if status == "ok":
                print(f"  源文件: `[[{item.get('sourceKey')}#L{item.get('startLine')}-L{item.get('endLine')}]]`")
                if item.get("headingPath"):
                    print(f"  大纲层级: `{item.get('headingPath')}`")
                print("  ```markdown")
                print((item.get("text") or "").strip())
                print("  ```\n")
        return

    if not args.file_path:
        raise RagError("missing_argument", "请提供 file_path 或指定 --citation/--citations", exit_code=EXIT_USAGE)

    if args.start_line > args.end_line:
        raise RagError("invalid_range", f"起始行号 ({args.start_line}) 不得大于结束行号 ({args.end_line})", exit_code=EXIT_USAGE)

    source, path = resolve_file_source_and_path(args.file_path, args.source, args.base_url, args.api_key)
    mcp_args = {
        "filePath": path,
        "startLine": args.start_line,
        "endLine": args.end_line
    }
    if source:
        mcp_args["sourceName"] = source

    res = call_mcp_tool("read_note_content", mcp_args, base_url=args.base_url, api_key=args.api_key, timeout=30)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    returned_lines = res.get("returnedLines") or res.get("lineCount") or 0
    total_lines = res.get("totalLines", -1)
    content = res.get("text") or res.get("content") or ""

    print(f"### 文件正文: `[[{path}#L{res.get('startLine')}-L{res.get('endLine')}]]` (源: `{source or '自动'}`)")
    print(f"- **读取行数**: {returned_lines} 行 / 总 {total_lines} 行")
    print("```markdown")
    print(content.strip())
    print("```")

def cmd_tree(args):
    """浏览目录树"""
    mcp_args = {"subPath": args.path or ""}
    if args.source:
        mcp_args["sourceName"] = args.source
    res = call_mcp_tool("get_file_tree", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    print(f"### 目录树: `{args.path or '/'}` (源: `{args.source or '默认'}`)\n")
    def print_node(node, depth=0):
        name = node.get("name") or node.get("Name") or ""
        is_dir = node.get("isDirectory") or node.get("IsDirectory") or False
        indent = "  " * depth
        icon = "📁" if is_dir else "📄"
        print(f"{indent}{icon} {name}")
        for child in (node.get("children") or []):
            print_node(child, depth + 1)

    nodes = res.get("entries") or res.get("tree") or res.get("children") or []
    if not nodes and res.get("name"):
        nodes = [res]
    for n in nodes:
        print_node(n, 0)

def cmd_grep(args):
    """ripgrep 风格的磁盘文件正则/正文检索"""
    mcp_args = {
        "pattern": args.pattern,
        "maxMatches": max(1, min(args.limit, 100)),
        "contextLines": max(0, min(args.context, 5))
    }
    if args.source:
        mcp_args["sourceName"] = args.source
    if args.sub_path:
        mcp_args["subPath"] = args.sub_path

    res = call_mcp_tool("search_file_content", mcp_args, base_url=args.base_url, api_key=args.api_key, timeout=30)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    files = res.get("files", [])
    total_matches = res.get("totalMatches", 0)
    print(f"### 正则检索: `{args.pattern}` (共匹配 {total_matches} 处，涉及 {len(files)} 个文件)\n")
    for f in files:
        rel_path = f.get("relativePath") or f.get("fullPath") or ""
        src_name = f.get("sourceName", "")
        src_prefix = f"[{src_name}] " if src_name else ""
        print(f"#### `{src_prefix}{rel_path}` (匹配 {f.get('matchCount', 0)} 处)")
        for hunk in f.get("hunks", []):
            for line in hunk.get("lines", []):
                if line.get("isMatch"):
                    print(f"- L{line.get('lineNumber')}: `{line.get('text', '').strip()}`")
        print()

def cmd_doc_info(args):
    """根据 documentId 或 chunkId 查询文档切片总数、元数据与连接器信息"""
    mcp_args = {}
    if args.document_id:
        mcp_args["documentId"] = args.document_id
    if args.chunk_id:
        mcp_args["chunkId"] = args.chunk_id
    if not mcp_args:
        raise RagError("missing_id", "必须提供 --doc <documentId> 或 --chunk <chunkId>", exit_code=EXIT_USAGE)

    res = call_mcp_tool("get_document_info", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    doc = res.get("document", {})
    file_info = res.get("file", {})
    print(f"### 文档信息: `{doc.get('documentId')}`")
    print(f"- **来源路径**: `{file_info.get('path')}`")
    print(f"- **切片总数**: {doc.get('totalChunks')} | **所属知识库**: `{doc.get('knowledgeBaseId')}`")
    print(f"- **文件大小**: {file_info.get('size')} 字节 | **最后修改**: {file_info.get('lastModified')}")

def cmd_graph(args):
    """关系图谱检索与遍历 (Wiki-links / Tags)"""
    target = args.target.strip()
    limit = max(1, min(args.limit, 100))
    depth = max(1, min(args.depth, 3))

    if args.by_title:
        mcp_args = {"query": target, "limit": limit}
        if args.kb:
            mcp_args["knowledgeBaseId"] = args.kb
        res = call_mcp_tool("graph_search_by_title", mcp_args, base_url=args.base_url, api_key=args.api_key)
        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
            return
        nodes = res.get("nodes", [])
        print(f"### 图谱节点模糊检索: `{target}` (找到 {len(nodes)} 个节点)\n")
        if not nodes:
            print(f"> {SYM_WARN} 未在知识图谱中检索到包含标题 `{target}` 的节点。")
            sys.exit(EXIT_NO_MATCH)
        for i, n in enumerate(nodes, 1):
            icon = "🏷️" if n.get("type") == "tag" else "📄"
            print(f"{i}. {icon} `{n.get('id')}` (类型: `{n.get('type')}`, 库: `{n.get('knowledgeBaseId')}`)")
        return

    mcp_args = {"nodeId": target, "depth": depth}
    if args.kb:
        mcp_args["knowledgeBaseId"] = args.kb
    try:
        res = call_mcp_tool("graph_search", mcp_args, base_url=args.base_url, api_key=args.api_key)
    except RagError as e:
        if not target.startswith("#") and "/" not in target and "\\" not in target:
            mcp_args_fallback = {"query": target, "limit": limit}
            if args.kb:
                mcp_args_fallback["knowledgeBaseId"] = args.kb
            res_title = call_mcp_tool("graph_search_by_title", mcp_args_fallback, base_url=args.base_url, api_key=args.api_key)
            nodes = res_title.get("nodes", [])
            if nodes:
                print(f"{SYM_WARN} 提示: 未找到 ID 为 `{target}` 的精确节点，已自动执行标题检索：\n")
                for i, n in enumerate(nodes, 1):
                    icon = "🏷️" if n.get("type") == "tag" else "📄"
                    print(f"{i}. {icon} `{n.get('id')}` (类型: `{n.get('type')}`, 库: `{n.get('knowledgeBaseId')}`)")
                return
        raise e

    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    root_id = res.get("rootId", target)
    nodes = res.get("nodes", [])
    edges = res.get("edges", [])
    print(f"### 知识图谱关联网络: `{root_id}` (深度: {depth})")
    print(f"- **总节点数**: {res.get('totalNodes', len(nodes))} | **总边数**: {res.get('totalEdges', len(edges))}\n")

    if args.format == "brief":
        for e in edges:
            print(f"- `{e.get('from')}` --[{e.get('type')}]--> `{e.get('to')}`")
    else:
        print("#### 关联节点清单：")
        for n in nodes:
            icon = "🏷️" if n.get("type") == "tag" else "📄"
            print(f"- {icon} **{n.get('title')}** (`{n.get('id')}`) [类型: `{n.get('type')}`]")

        if edges:
            print("\n#### 关系边 (Edges)：")
            for e in edges:
                print(f"- `{e.get('from')}` --[{e.get('type')}]--> `{e.get('to')}`")

def cmd_status(args):
    """查看知识库单轨检索状态与后台作业"""
    status_res = None
    try:
        status_res = request_http("api/knowledge-base-search/status", method="GET",
                                  base_url=args.base_url, api_key=args.api_key)
    except Exception:
        status_res = None

    if status_res and "accessibleKnowledgeBases" in status_res:
        if args.json:
            print(json.dumps(status_res, ensure_ascii=False, indent=2))
            return

        kbs = status_res.get("accessibleKnowledgeBases") or []
        jobs = status_res.get("recentJobs") or []
        print("### ReferenceRAG 知识库检索与作业状态\n")
        print("#### 可访问受权知识库状态：")
        if kbs:
            for kb in kbs:
                lexical_tag = "词法索引就绪" if kb.get("lexicalReady") else f"{SYM_WARN} 词法未就绪"
                pure_tag = " [纯词法]" if kb.get("isPureLexical") else ""
                status_icon = SYM_OK if kb.get("enabled") else "⏸️"
                print(f"- {status_icon} **{kb.get('name')}** (`{kb.get('id')}`){pure_tag}")
                print(f"  文档数: {kb.get('documentCount')} | 切片数: {kb.get('chunkCount')} | 规格: `{kb.get('embeddingSpecId')}` | {lexical_tag}")
        else:
            print("> 当前无有权访问的知识库。")

        print("\n#### 最近同步与索引作业：")
        if jobs:
            limit = max(1, min(args.limit, 50))
            for j in jobs[:limit]:
                status_icon = SYM_OK if j.get("Status") == "Succeeded" else (SYM_FAIL if j.get("Status") == "Failed" else "⏳")
                err = f" (错误: {j.get('ErrorMessage')})" if j.get("ErrorMessage") else ""
                print(f"- {status_icon} **任务 `{j.get('Id')}`** [{j.get('Operation')}]: 状态 `{j.get('Status')}`{err}")
                print(f"  知识库: `{j.get('KnowledgeBaseId')}` | 进度: {j.get('ProcessedUnits')}/{j.get('TotalUnits')} 单元 | 完成时间: {j.get('FinishedAt')}")
        else:
            print("> 暂无作业记录。")
        return

    # 降级走旧版 API
    jobs = request_http("api/jobs", method="GET", base_url=args.base_url, api_key=args.api_key)
    sources = request_http("api/sources", method="GET", base_url=args.base_url, api_key=args.api_key)

    if args.json:
        print(json.dumps({"jobs": jobs, "sources": sources}, ensure_ascii=False, indent=2))
        return

    print("### ReferenceRAG 运行与任务状态\n")
    print("#### 物理数据源健康状态：")
    if isinstance(sources, list):
        for s in sources:
            warn = f" ({SYM_WARN} 未挂载物理文件)" if s.get("FileCount", 0) == 0 else ""
            kbs = ", ".join(kb.get("Name", "") for kb in s.get("LinkedKnowledgeBases", []))
            print(f"- **{s.get('Name')}** (`{s.get('Path')}`){warn}")
            print(f"  文件数: {s.get('FileCount')} | 切片数: {s.get('ChunkCount')} | 绑定库: [{kbs}] | 最后索引: {s.get('LastIndexed')}")
    else:
        print(f"> 获取数据源状态失败: {sources}")

    print("\n#### 后台同步索引任务 (最近记录)：")
    if isinstance(jobs, list):
        limit = max(1, min(args.limit, 50))
        for j in jobs[:limit]:
            status_icon = SYM_OK if j.get("Status") == "Succeeded" else (SYM_FAIL if j.get("Status") == "Failed" else "⏳")
            err = f" (错误: {j.get('ErrorMessage')})" if j.get("ErrorMessage") else ""
            print(f"- {status_icon} **任务 `{j.get('Id')}`** [{j.get('Operation')}]: 状态 `{j.get('Status')}`{err}")
            print(f"  知识库: `{j.get('KnowledgeBaseId')}` | 进度: {j.get('ProcessedUnits')}/{j.get('TotalUnits')} 单元 | 完成时间: {j.get('FinishedAt')}")
    else:
        print(f"> 获取任务记录失败: {jobs}")

def cmd_ask(args):
    """基于知识库自主智能有据问答"""
    # 优先使用单轨有据问答接口 POST /api/knowledge-answers
    top_k = max(1, min(args.top_k, 50))
    payload = {
        "question": args.question,
        "mode": getattr(args, "mode", "auto") or "auto",
        "topK": top_k
    }
    if getattr(args, "kb", None):
        payload["knowledgeBaseIds"] = [args.kb]

    use_grounded = True
    grounded_res = None
    try:
        grounded_res = request_http("api/knowledge-answers", method="POST", payload=payload,
                                    base_url=args.base_url, api_key=args.api_key, timeout=120)
    except Exception:
        use_grounded = False

    if use_grounded and grounded_res and "status" in grounded_res:
        if args.json:
            print(json.dumps(grounded_res, ensure_ascii=False, indent=2))
            return

        status = grounded_res.get("status")
        answer = grounded_res.get("answer") or ""
        citations = grounded_res.get("citations") or []

        print(f"### ReferenceRAG 智能有据问答: `{args.question}`\n")
        if status == "no_match":
            print(f"{SYM_FAIL} **未匹配**: 知识库中未检索到相关事实证据，已短路退出以避免虚假生成。")
            return
        elif status == "insufficient_evidence":
            print(f"{SYM_WARN} **证据不足**: 知识库中候选切片相关度不足，无法支撑确定性回答。")
            return

        print(answer)
        if citations:
            print("\n#### 证据引用链：")
            for c in citations:
                cref = c.get("citationRef", {})
                print(f"- `{c.get('shortId')}` 源文件: `[[{c.get('sourceKey')}#L{c.get('startLine')}-L{c.get('endLine')}]]` (库: `{cref.get('knowledgeBaseId')}`, 切片: `{cref.get('chunkId')}`)")
        return

    # 降级走 /v1/chat/completions 兼容接口
    payload = {
        "model": args.model or "reference-rag",
        "messages": [
            {"role": "user", "content": args.question}
        ],
        "temperature": args.temp
    }
    raw = request_http("v1/chat/completions", method="POST", payload=payload,
                       base_url=args.base_url, api_key=args.api_key, timeout=120)

    if args.json:
        print(json.dumps(raw, ensure_ascii=False, indent=2))
        return

    choices = raw.get("choices", [])
    if not choices:
        raise RagError("ask_failed", f"模型未返回可用回答: {raw}", exit_code=EXIT_SERVER)

    message = choices[0].get("message", {})
    content = message.get("content", "")

    if not args.show_thinking:
        content = re.sub(r"<think>.*?</think>", "", content, flags=re.DOTALL).strip()

    usage = raw.get("usage", {})
    print(f"### ReferenceRAG 智能问答: `{args.question}`\n")
    print(content)
    if usage:
        print(f"\n---\n*Token 消耗: 提示 {usage.get('prompt_tokens', 0)} / 生成 {usage.get('completion_tokens', 0)} / 总计 {usage.get('total_tokens', 0)}*")

# ----------------- CLI 参数重排与入口 -----------------

def reorder_argv(argv: List[str]) -> List[str]:
    """将顶级全局选项提升到子命令之前，实现任意位置自由传参"""
    global_options = {'--base-url', '--api-key', '--format', '--max-chars'}
    global_flags = {'--json', '--utf8', '-h', '--help'}
    pre = []
    post = []
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg in global_options and i + 1 < len(argv):
            pre.extend([arg, argv[i+1]])
            i += 2
        elif any(arg.startswith(opt + '=') for opt in global_options):
            pre.append(arg)
            i += 1
        elif arg in global_flags:
            pre.append(arg)
            i += 1
        else:
            post.append(arg)
            i += 1
    return pre + post

def main():
    parser = argparse.ArgumentParser(description="ReferenceRAG CLI 跨平台知识库检索客户端")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help=f"服务基础地址 (默认: {DEFAULT_BASE_URL})")
    parser.add_argument("--api-key", default=DEFAULT_API_KEY, help="鉴权密钥 (X-API-Key)")
    parser.add_argument("--json", action="store_true", help="强制输出原始标准 JSON 格式")
    parser.add_argument("--utf8", action="store_true", help="强制启用 UTF-8 宽字符/Emoji 符号输出")
    parser.add_argument("--format", choices=["md", "brief", "json"], default="md", help="输出形态：md (完整Markdown), brief (精简行链接), json (结构化)")
    parser.add_argument("--max-chars", type=int, default=1000, help="单条切片正文最大展示字符数 (默认 1000)")

    subparsers = parser.add_subparsers(dest="subcommand", required=True)

    # ping
    p_ping = subparsers.add_parser("ping", help="服务连通性探活")
    p_ping.set_defaults(func=cmd_ping)

    # stats
    p_stats = subparsers.add_parser("stats", help="获取系统与索引统计、模型状态")
    p_stats.set_defaults(func=cmd_stats)

    # list-kbs
    p_kbs = subparsers.add_parser("list-kbs", help="列出已注册知识库清单")
    p_kbs.set_defaults(func=cmd_list_kbs)

    # list-sources
    p_sources = subparsers.add_parser("list-sources", help="列出物理数据源及切片统计")
    p_sources.set_defaults(func=cmd_list_sources)

    # list-files
    p_files = subparsers.add_parser("list-files", help="查找文件清单")
    p_files.add_argument("pattern", nargs="?", default="*.*", help="文件名通配符 (如 *.md)")
    p_files.add_argument("--path-keyword", default=None, help="按完整路径段关键字检索 (走 list_files)")
    p_files.add_argument("--limit", type=int, default=50, help="最大返回数量 (上限 500)")
    p_files.add_argument("--source", default=None, help="限定数据源名称")
    p_files.set_defaults(func=cmd_list_files)

    # search
    p_search = subparsers.add_parser("search", help="知识库检索（统一入口，支持 auto/hybrid/semantic/lexical）")
    p_search.add_argument("query", help="查询关键词或自然语言问题")
    p_search.add_argument("--mode", choices=["auto", "hybrid", "semantic", "lexical"], default="auto",
                          help="检索模式：auto (默认，自动决策)、hybrid (混合检索)、semantic (语义检索)、lexical (精确词法)")
    p_search.add_argument("--policy", choices=["exact", "concept", "balanced"], default="balanced",
                          help="检索质量策略：exact (纯词法无凑底), concept (语义保召回), balanced (默认平衡)")
    p_search.add_argument("--top-k", type=int, default=5, help="返回结果数量 (1-50, 默认 5)")
    p_search.add_argument("--kb", default=None, help="限定目标知识库 ID (留空表示全库)")
    p_search.add_argument("--connector", default=None, help="限定连接器 ID")
    p_search.add_argument("--path-prefix", default=None, help="限定库内相对路径前缀 (如 'notes/')")
    p_search.add_argument("--lexical", action="store_true", help="强制纯词法检索 (相当于 --mode lexical)")
    p_search.add_argument("--rest", action="store_true", help="强制走 REST API 通道 (默认走权威 MCP)")
    p_search.set_defaults(func=cmd_search)

    # chunk (下钻查看完整切片正文)
    p_chunk = subparsers.add_parser("chunk", aliases=["drill-down"], help="展开指定切片完整正文 (无须提供本地磁盘路径)")
    p_chunk.add_argument("chunk_id", help="切片 ID (32 位 Hex)")
    p_chunk.add_argument("--kb", default=None, help="指定切片所属知识库 ID (留空自动在授权库中探测)")
    p_chunk.set_defaults(func=cmd_chunk)

    # outline
    p_outline = subparsers.add_parser("outline", help="获取指定 Markdown 文档大纲")
    p_outline.add_argument("file_path", help="文档相对路径或名称")
    p_outline.add_argument("--source", default=None, help="限定数据源名称")
    p_outline.set_defaults(func=cmd_outline)

    # read
    p_read = subparsers.add_parser("read", help="按行号读取笔记正文或批量回读已验证证据")
    p_read.add_argument("file_path", nargs="?", default=None, help="文档相对路径或名称（如果提供 --citation 则可省略）")
    p_read.add_argument("--start-line", type=int, default=1, help="起始行号 (从 1 开始)")
    p_read.add_argument("--end-line", type=int, default=100, help="结束行号")
    p_read.add_argument("--source", default=None, help="限定数据源名称")
    p_read.add_argument("--citation", dest="citation", default=None, help="单条 CitationRef JSON 字符串")
    p_read.add_argument("--citations", dest="citations", default=None, help="批量 CitationRef JSON 数组字符串")
    p_read.set_defaults(func=cmd_read)

    # tree
    p_tree = subparsers.add_parser("tree", help="逐层浏览目录树结构")
    p_tree.add_argument("path", nargs="?", default="", help="子目录相对路径")
    p_tree.add_argument("--source", default=None, help="限定数据源名称")
    p_tree.set_defaults(func=cmd_tree)

    # grep
    p_grep = subparsers.add_parser("grep", help="磁盘文件文本正则搜索 (ripgrep风格)")
    p_grep.add_argument("pattern", help="搜索正则表达式或文本")
    p_grep.add_argument("--sub-path", default=None, help="限定子目录路径")
    p_grep.add_argument("--source", default=None, help="限定数据源名称")
    p_grep.add_argument("--limit", type=int, default=20, help="最大匹配数量")
    p_grep.add_argument("--context", type=int, default=1, help="上下文展示行数")
    p_grep.set_defaults(func=cmd_grep)

    # doc-info
    p_doc = subparsers.add_parser("doc-info", help="查询文档元数据与连接器信息")
    p_doc.add_argument("--doc", dest="document_id", default=None, help="文档 ID (DocumentId)")
    p_doc.add_argument("--chunk", dest="chunk_id", default=None, help="切片 ID (ChunkId)")
    p_doc.set_defaults(func=cmd_doc_info)

    # graph
    p_graph = subparsers.add_parser("graph", help="知识图谱节点检索与关系遍历")
    p_graph.add_argument("target", help="起始节点 ID (如文件路径、标签 #tag) 或 笔记标题")
    p_graph.add_argument("--depth", type=int, default=1, help="关联图谱遍历深度 (1-3, 默认 1)")
    p_graph.add_argument("--limit", type=int, default=20, help="模糊检索最大返回数")
    p_graph.add_argument("--kb", default=None, help="限定知识库 ID")
    p_graph.add_argument("--by-title", action="store_true", help="强制按笔记标题模糊检索图谱节点")
    p_graph.set_defaults(func=cmd_graph)

    # status
    p_status = subparsers.add_parser("status", help="查看后台同步任务进度与数据源健康状态")
    p_status.add_argument("--limit", type=int, default=10, help="最多展示历史任务数")
    p_status.set_defaults(func=cmd_status)

    # ask
    p_ask = subparsers.add_parser("ask", help="基于知识库单轨有据问答 (优先 /api/knowledge-answers)")
    p_ask.add_argument("question", help="自然语言问题")
    p_ask.add_argument("--kb", default=None, help="限定目标知识库 ID")
    p_ask.add_argument("--mode", choices=["auto", "hybrid", "semantic", "lexical"], default="auto", help="检索模式")
    p_ask.add_argument("--top-k", type=int, default=5, help="检索候选证据数量 (1-50, 默认 5)")
    p_ask.add_argument("--model", default="reference-rag", help="模型名称 (默认: reference-rag)")
    p_ask.add_argument("--temp", type=float, default=0.2, help="采样温度 (0.0 - 1.0)")
    p_ask.add_argument("--show-thinking", action="store_true", help="输出模型内部推理思考链 (<think>...)")
    p_ask.set_defaults(func=cmd_ask)

    # 参数归一化重排与解析
    raw_args = sys.argv[1:]
    normalized_args = reorder_argv(raw_args)
    try:
        args = parser.parse_args(normalized_args)
    except SystemExit as e:
        sys.exit(e.code)

    # 动态应用符号与编码偏好
    if getattr(args, "utf8", False):
        update_symbols(force_utf8=True)
    if getattr(args, "json", False):
        args.format = "json"

    try:
        args.func(args)
    except RagError as e:
        print(f"{SYM_FAIL} {e.message}", file=sys.stderr)
        sys.exit(e.exit_code)
    except Exception as e:
        print(f"{SYM_FAIL} 未知执行异常: {e}", file=sys.stderr)
        sys.exit(EXIT_SERVER)

if __name__ == "__main__":
    main()
