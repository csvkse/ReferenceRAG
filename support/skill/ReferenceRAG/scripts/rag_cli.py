#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ReferenceRAG CLI - 跨平台知识库检索与管理客户端门面
功能特性：
1. 双通道交互：支持直接调用 REST API (/api/knowledge-base-search) 与 MCP JSON-RPC (/api/mcp)
2. 跨平台编码安全：强制采用 UTF-8 字节流输入输出，消除 Windows 控制台 GBK 乱码
3. Token 预算优化：紧凑 Markdown 格式化输出，过滤无用长 UUID 与复杂跟踪日志，降低 Agent 上下文开销
4. 零外部依赖：纯 Python 3 标准库（urllib + json + argparse）实现
"""

import sys
import os
import json
import argparse
import urllib.request
import urllib.error

# 允许从环境变量读取，默认对齐生产环境
DEFAULT_BASE_URL = os.environ.get("OBSIDIAN_RAG_API_URL", "http://127.0.0.1:5123")
DEFAULT_API_KEY = os.environ.get("OBSIDIAN_RAG_API_KEY", "rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")

def request_http(endpoint: str, method: str = "GET", payload: dict = None,
                 base_url: str = DEFAULT_BASE_URL, api_key: str = DEFAULT_API_KEY, timeout: int = 30):
    url = f"{base_url.rstrip('/')}/{endpoint.lstrip('/')}"
    headers = {
        "User-Agent": "ReferenceRAG-Skill-CLI/2.0",
        "Connection": "close"
    }
    if api_key:
        headers["X-API-Key"] = api_key
        headers["Authorization"] = f"Bearer {api_key}"

    data_bytes = None
    if payload is not None:
        headers["Content-Type"] = "application/json; charset=utf-8"
        data_bytes = json.dumps(payload, ensure_ascii=False).encode("utf-8")

    req = urllib.request.Request(url, data=data_bytes, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            content = resp.read().decode("utf-8")
            try:
                return json.loads(content)
            except json.JSONDecodeError:
                return {"_raw": content}
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        try:
            err_json = json.loads(err_body)
            return {"success": False, "error": f"HTTP {e.code}", "details": err_json}
        except Exception:
            return {"success": False, "error": f"HTTP {e.code}: {err_body}"}
    except urllib.error.URLError as e:
        return {"success": False, "error": f"连接失败: {e.reason}"}
    except Exception as e:
        return {"success": False, "error": str(e)}

def call_mcp_tool(tool_name: str, arguments: dict, base_url: str = DEFAULT_BASE_URL, api_key: str = DEFAULT_API_KEY):
    payload = {
        "jsonrpc": "2.0",
        "id": 1,
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": arguments
        }
    }
    res = request_http("api/mcp", method="POST", payload=payload, base_url=base_url, api_key=api_key)
    if "result" in res and "content" in res["result"]:
        content_list = res["result"]["content"]
        if isinstance(content_list, list) and len(content_list) > 0:
            text = content_list[0].get("text", "")
            try:
                return json.loads(text)
            except Exception:
                return {"raw_text": text}
    if "error" in res:
        return {"success": False, "error": res["error"]}
    return res

# ----------------- 子命令实现 -----------------

def cmd_ping(args):
    """测试连通性"""
    res = call_mcp_tool("ping", {}, base_url=args.base_url, api_key=args.api_key)
    if res.get("success") or res.get("message") == "pong" or "pong" in str(res):
        print(f"✅ 服务连通正常: {args.base_url}")
        print(f"响应: {json.dumps(res, ensure_ascii=False)}")
    else:
        print(f"❌ 连通测试异常: {res}")

def cmd_stats(args):
    """获取系统统计与模型运行健康度"""
    res = call_mcp_tool("get_system_stats", {}, base_url=args.base_url, api_key=args.api_key)
    if not res.get("success"):
        print(f"❌ 获取统计失败: {res.get('error', res)}")
        return

    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    sys_info = res.get("system", {})
    embed_info = res.get("embedding", {})
    rerank_info = res.get("rerank", {})
    idx_info = res.get("index", {})

    print("### ReferenceRAG 系统与索引状态")
    print(f"- **数据总量**: {idx_info.get('totalDocuments', 0)} 篇文档 / {idx_info.get('totalChunks', 0)} 分块 / {idx_info.get('totalVectors', 0)} 向量")
    print(f"- **嵌入模型**: `{embed_info.get('modelName')}` (CUDA: {embed_info.get('useCuda')}, SeqLen: {embed_info.get('maxSequenceLength')})")
    print(f"- **重排模型**: `{rerank_info.get('modelName')}` (健康度: `{rerank_info.get('health')}`)")
    if rerank_info.get("degradationReason"):
        print(f"  > ⚠️ 重排降级说明: {rerank_info.get('degradationReason')}")

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
        print(f"❌ 列出知识库失败: {res.get('error', res)}")

def cmd_list_sources(args):
    """列出数据源与分块计数"""
    res = call_mcp_tool("list_sources", {}, base_url=args.base_url, api_key=args.api_key)
    if not res.get("success"):
        print(f"❌ 列出数据源失败: {res.get('error', res)}")
        return

    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    sources = res.get("sources", [])
    print(f"### 数据源清单（共 {len(sources)} 个源，覆盖 {res.get('knowledgeBaseCount', 0)} 个知识库）\n")
    for s in sources:
        print(f"- **{s.get('name')}** (`{s.get('path')}`)")
        print(f"  文件数: {s.get('fileCount')} | 切片数: {s.get('chunkCount')} | 来源: {s.get('origin')}")

def cmd_list_files(args):
    """按模式查找或列举文件"""
    mcp_args = {
        "globPattern": args.pattern or "*.*",
        "maxResults": args.limit
    }
    if args.source:
        mcp_args["sourceName"] = args.source
    res = call_mcp_tool("find_files_by_pattern", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if not res.get("success"):
        print(f"❌ 查找文件失败: {res.get('error', res)}")
        return

    files = res.get("files", [])
    print(f"### 文件匹配结果（模式: `{args.pattern}`，共 {res.get('count', len(files))} 条）\n")
    for f in files:
        print(f"- `{f}`")

def cmd_search(args):
    """知识库检索主力命令（统一入口，支持 auto/hybrid/semantic/lexical）"""
    mode = "lexical" if args.lexical else args.mode

    if args.mcp:
        mcp_args = {
            "query": args.query,
            "mode": mode,
            "topK": args.top_k
        }
        if args.kb:
            mcp_args["knowledgeBaseIds"] = json.dumps([args.kb])
        res = call_mcp_tool("knowledge_search", mcp_args, base_url=args.base_url, api_key=args.api_key)
        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
            return
        if not res.get("success", False):
            print(f"❌ MCP 知识库检索失败: {res.get('error', res)}")
            return
        items = res.get("items", [])
        no_match = res.get("noMatch", False)
        elapsed = f"{res.get('elapsedMs', 0)}ms"
        score_kind = res.get("scoreKind", "RRF")
        tool_mode = res.get("mode", mode)
    else:
        use_lexical = mode == "lexical"
        payload = {
            "Query": args.query,
            "KnowledgeBaseIds": [args.kb] if args.kb else [],
            "TopK": args.top_k,
            "UseLexicalTokenizer": use_lexical
        }
        res = request_http("api/knowledge-base-search", method="POST", payload=payload,
                           base_url=args.base_url, api_key=args.api_key)

        if args.json:
            print(json.dumps(res, ensure_ascii=False, indent=2))
            return

        items = res.get("Items") or res.get("items") or []
        no_match = res.get("NoMatch") or res.get("noMatch") or False
        elapsed = res.get("Elapsed") or f"{res.get('elapsedMs', 0)}ms"
        score_kind = res.get("ScoreKind") or res.get("scoreKind") or "RRF"
        tool_mode = mode

    print(f"### ReferenceRAG 知识库检索: `{args.query}` (模式: `{tool_mode}`)")
    print(f"- **状态**: {'⚠️ 未命中 (NoMatch)' if no_match else '✅ 成功命中'}")
    print(f"- **召回条数**: {len(items)} 条 | **耗时**: {elapsed} | **评分**: {score_kind}")

    if no_match:
        reason = res.get("NoMatchReason") or res.get("noMatchReason") or "检索切片中未包含查询词核心二元组/标识"
        print(f"> ⚠️ **未命中判定**: {reason}")
        print("> 建议：去除无实义助词、补充专业技术名词、或切换 `--mode lexical` 精确模式重试。\n")
        return

    print()
    for i, it in enumerate(items, 1):
        channels_raw = it.get("Channels") or it.get("channels") or []
        channels = ",".join(channels_raw) if isinstance(channels_raw, list) else str(channels_raw)
        heading_path = it.get("HeadingPath") or it.get("headingPath") or ""
        heading = f" > {heading_path}" if heading_path else ""
        source = it.get("SourceKey") or it.get("sourceKey") or "未知来源"
        start_line = it.get("StartLine") or it.get("startLine") or 0
        end_line = it.get("EndLine") or it.get("endLine") or 0
        score = round(float(it.get("Score") or it.get("score") or 0), 4)
        kb_id = it.get("KnowledgeBaseId") or it.get("knowledgeBaseId") or ""
        text = it.get("Text") or it.get("text") or ""

        print(f"#### {i}. `[[{source}#L{start_line}-L{end_line}]]`{heading}")
        print(f"- **得分**: `{score}` ({score_kind}) | **通道**: `{channels}` | **库ID**: `{kb_id}`")
        print("```markdown")
        print(text.strip())
        print("```\n")

def cmd_outline(args):
    """获取指定文档大纲（H1-H6 标题树与分块状态）"""
    mcp_args = {
        "filePath": args.file_path
    }
    if args.source:
        mcp_args["sourceName"] = args.source
    res = call_mcp_tool("get_note_outline", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if not res.get("success"):
        print(f"❌ 获取文档大纲失败: {res.get('error', res)}")
        return

    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    chunk_count = res.get("ragChunkCount") or res.get("chunkCount") or "未索引"
    print(f"### 文档大纲: `{args.file_path}`")
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
    """按行读取文件正文"""
    mcp_args = {
        "filePath": args.file_path,
        "startLine": args.start_line,
        "endLine": args.end_line
    }
    if args.source:
        mcp_args["sourceName"] = args.source
    res = call_mcp_tool("read_note_content", mcp_args, base_url=args.base_url, api_key=args.api_key)
    if not res.get("success"):
        print(f"❌ 读取文件失败: {res.get('error', res)}")
        return

    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    returned_lines = res.get("returnedLines") or res.get("lineCount") or 0
    total_lines = res.get("totalLines", -1)
    content = res.get("text") or res.get("content") or ""

    print(f"### 文件正文: `[[{args.file_path}#L{res.get('startLine')}-L{res.get('endLine')}]]`")
    print(f"- **读取行数**: {returned_lines} 行 / 总 {total_lines} 行")
    print("```markdown")
    print(content.strip())
    print("```")

# ----------------- CLI 入口解析 -----------------

def main():
    parser = argparse.ArgumentParser(description="ReferenceRAG CLI 跨平台客户端门面")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="服务基础地址 (默认: 127.0.0.1:5123)")
    parser.add_argument("--api-key", default=DEFAULT_API_KEY, help="鉴权密钥 (X-API-Key)")
    parser.add_argument("--json", action="store_true", help="以原始格式化 JSON 打印输出")

    subparsers = parser.add_subparsers(dest="subcommand", required=True)

    # 1. ping
    p_ping = subparsers.add_parser("ping", help="服务连通性与探活")
    p_ping.set_defaults(func=cmd_ping)

    # 2. stats
    p_stats = subparsers.add_parser("stats", help="获取系统与索引统计、模型状态")
    p_stats.set_defaults(func=cmd_stats)

    # 3. list-kbs
    p_kbs = subparsers.add_parser("list-kbs", help="列出已注册知识库清单")
    p_kbs.set_defaults(func=cmd_list_kbs)

    # 4. list-sources
    p_sources = subparsers.add_parser("list-sources", help="列出物理数据源及切片统计")
    p_sources.set_defaults(func=cmd_list_sources)

    # 5. list-files
    p_files = subparsers.add_parser("list-files", help="按 Glob 模式检索文件清单")
    p_files.add_argument("pattern", nargs="?", default="*.*", help="文件通配符模式 (如 *.md)")
    p_files.add_argument("--limit", type=int, default=50, help="最大返回数量 (默认 50)")
    p_files.add_argument("--source", default=None, help="限定数据源名称")
    p_files.set_defaults(func=cmd_list_files)

    # 6. search
    p_search = subparsers.add_parser("search", help="知识库检索（统一入口，支持 auto/hybrid/semantic/lexical）")
    p_search.add_argument("query", help="查询关键词或自然语言问题")
    p_search.add_argument("--mode", choices=["auto", "hybrid", "semantic", "lexical"], default="auto",
                          help="检索模式：auto (默认，自动决策)、hybrid (混合检索)、semantic (语义检索)、lexical (精确词法)")
    p_search.add_argument("--top-k", type=int, default=5, help="返回结果数量 (默认 5)")
    p_search.add_argument("--kb", default=None, help="限定目标知识库 ID (留空表示全库)")
    p_search.add_argument("--lexical", action="store_true", help="强制纯词法检索 (相当于 --mode lexical)")
    p_search.add_argument("--mcp", action="store_true", help="走 MCP JSON-RPC 协议调用 knowledge_search")
    p_search.set_defaults(func=cmd_search)

    # 7. outline
    p_outline = subparsers.add_parser("outline", help="获取指定 Markdown 文档大纲")
    p_outline.add_argument("file_path", help="文档相对路径或名称")
    p_outline.add_argument("--source", default=None, help="限定数据源名称")
    p_outline.set_defaults(func=cmd_outline)

    # 8. read
    p_read = subparsers.add_parser("read", help="按行号读取笔记正文")
    p_read.add_argument("file_path", help="文档相对路径或名称")
    p_read.add_argument("--start-line", type=int, default=1, help="起始行号 (从 1 开始)")
    p_read.add_argument("--end-line", type=int, default=100, help="结束行号")
    p_read.add_argument("--source", default=None, help="限定数据源名称")
    p_read.set_defaults(func=cmd_read)

    args = parser.parse_args()
    args.func(args)

if __name__ == "__main__":
    main()


