#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ReferenceRAG CLI - 跨平台知识库检索与管理客户端门面
功能特性：
1. 双通道交互：支持直接调用 REST API (/api/knowledge-base-search) 与 MCP JSON-RPC (/api/mcp)
2. 跨平台编码安全：强制采用 UTF-8 字节流输入输出，消除 Windows 控制台 GBK 乱码
3. Token 预算优化：紧凑 Markdown 格式化输出，过滤无用长 UUID 与复杂跟踪日志，降低 Agent 上下文开销
4. 参数位置解耦：支持全局参数 (--base-url, --api-key, --json) 放置在子命令前或后任意位置
5. 零外部依赖：纯 Python 3 标准库（urllib + json + argparse + re）实现
"""

import sys
import os
import re
import json
import argparse
import urllib.request
import urllib.error

# 允许从环境变量读取，默认对齐生产环境
DEFAULT_BASE_URL = os.environ.get("OBSIDIAN_RAG_API_URL", "http://127.0.0.1:5123")
DEFAULT_API_KEY = os.environ.get("OBSIDIAN_RAG_API_KEY", "rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")

def request_http(endpoint: str, method: str = "GET", payload: dict = None,
                 base_url: str = DEFAULT_BASE_URL, api_key: str = DEFAULT_API_KEY, timeout: int = 40):
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

def is_code_or_exact_token(query: str) -> bool:
    """智能模式启发式规则：识别代码标识符、错误码、引号"""
    if not query:
        return False
    trimmed = query.strip()
    if (trimmed.startswith('"') and trimmed.endswith('"')) or (trimmed.startswith('“') and trimmed.endswith('”')):
        return True
    if re.match(r"^[A-Za-z0-9_]{2,40}$", trimmed) and ('_' in trimmed or any(c.isupper() for c in trimmed)):
        return True
    if re.search(r"(?i)\b(error|err|code|status|cs|e)\s*[:#\-]?\s*\d{3,5}\b", trimmed):
        return True
    return False

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
        # REST 模式：支持客户端 auto 意图路由
        if mode == "auto":
            is_code = is_code_or_exact_token(args.query)
            use_lexical = is_code
            tool_mode = "auto (词法代码通道)" if is_code else "auto (混合向量通道)"
        elif mode == "lexical":
            use_lexical = True
            tool_mode = "lexical"
        else:
            use_lexical = False
            tool_mode = mode

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
        chunk_id = it.get("ChunkId") or it.get("chunkId") or ""
        text = it.get("Text") or it.get("text") or ""

        print(f"#### {i}. `[[{source}#L{start_line}-L{end_line}]]`{heading}")
        print(f"- **得分**: `{score}` ({score_kind}) | **通道**: `{channels}` | **库ID**: `{kb_id}` | **切片ID**: `{chunk_id}`")
        print("```markdown")
        print(text.strip())
        print("```\n")

def cmd_chunk(args):
    """根据 chunkId 与 knowledgeBaseId 精确展开切片完整正文 (无须指定物理文件路径)"""
    kb_id = args.kb
    chunk_id = args.chunk_id.strip()

    # 若未指定 kb_id，自动从可用库中探测目标切片
    if not kb_id:
        kbs = request_http("api/knowledge-bases", method="GET", base_url=args.base_url, api_key=args.api_key)
        if isinstance(kbs, list) and len(kbs) > 0:
            for candidate in kbs:
                cid = candidate.get("Id")
                probe = request_http(f"api/knowledge-bases/{cid}/chunks/{chunk_id}",
                                     method="GET", base_url=args.base_url, api_key=args.api_key)
                if isinstance(probe, dict) and (probe.get("Id") == chunk_id or probe.get("id") == chunk_id):
                    kb_id = cid
                    break

    if not kb_id:
        # 回退至 MCP knowledge_base_drill_down 探测
        mcp_res = call_mcp_tool("knowledge_base_drill_down", {"chunkId": chunk_id},
                                base_url=args.base_url, api_key=args.api_key)
        if isinstance(mcp_res, dict) and mcp_res.get("chunk"):
            chunk_data = mcp_res["chunk"]
            kb_id = mcp_res.get("knowledgeBaseId", "未知库")
            if args.json:
                print(json.dumps(mcp_res, ensure_ascii=False, indent=2))
                return
            print_chunk_detail(chunk_id, kb_id, chunk_data)
            return
        print(f"❌ 未找到切片 `{chunk_id}`，请通过 `--kb <kbId>` 明确指定所属知识库。")
        return

    res = request_http(f"api/knowledge-bases/{kb_id}/chunks/{chunk_id}",
                       method="GET", base_url=args.base_url, api_key=args.api_key)
    if args.json:
        print(json.dumps(res, ensure_ascii=False, indent=2))
        return

    if res.get("Id") or res.get("id"):
        print_chunk_detail(chunk_id, kb_id, res)
    else:
        print(f"❌ 获取切片失败: {res.get('error', res)}")

def print_chunk_detail(chunk_id: str, kb_id: str, chunk_obj: dict):
    text = chunk_obj.get("Text") or chunk_obj.get("text") or ""
    source = chunk_obj.get("DocumentSourceKey") or chunk_obj.get("documentSourceKey") or "未知文档"
    start = chunk_obj.get("StartLine") or chunk_obj.get("startLine") or 0
    end = chunk_obj.get("EndLine") or chunk_obj.get("endLine") or 0
    heading = chunk_obj.get("HeadingPath") or chunk_obj.get("headingPath") or ""
    tokens = chunk_obj.get("TokenCount") or chunk_obj.get("tokenCount") or 0
    ordinal = chunk_obj.get("Ordinal") if chunk_obj.get("Ordinal") is not None else chunk_obj.get("ordinal", 0)

    print(f"### 切片正文详情: `{chunk_id}`")
    print(f"- **所属知识库**: `{kb_id}` | **来源文档**: `[[{source}#L{start}-L{end}]]`")
    if heading:
        print(f"- **章节大纲路径**: `{heading}`")
    print(f"- **分块序号**: {ordinal} | **Token 预估**: {tokens} | **行号范围**: L{start} ~ L{end}\n")
    print("```markdown")
    print(text.strip())
    print("```")

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

def reorder_argv(argv):
    """将放置在子命令之后的全局选项 (--base-url, --api-key, --json) 提升至前面，实现位置无关性"""
    global_options = {'--base-url', '--api-key'}
    global_flags = {'--json'}
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
    parser = argparse.ArgumentParser(description="ReferenceRAG CLI 跨平台客户端门面")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help=f"服务基础地址 (默认: {DEFAULT_BASE_URL})")
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

    # 7. chunk (下钻查看完整切片正文)
    p_chunk = subparsers.add_parser("chunk", aliases=["drill-down"], help="展开指定切片完整正文 (无须提供本地磁盘路径)")
    p_chunk.add_argument("chunk_id", help="切片 ID (32 位 Hex)")
    p_chunk.add_argument("--kb", default=None, help="指定切片所属知识库 ID (留空自动在授权库中探测)")
    p_chunk.set_defaults(func=cmd_chunk)

    # 8. outline
    p_outline = subparsers.add_parser("outline", help="获取指定 Markdown 文档大纲")
    p_outline.add_argument("file_path", help="文档相对路径或名称")
    p_outline.add_argument("--source", default=None, help="限定数据源名称")
    p_outline.set_defaults(func=cmd_outline)

    # 9. read
    p_read = subparsers.add_parser("read", help="按行号读取笔记正文")
    p_read.add_argument("file_path", help="文档相对路径或名称")
    p_read.add_argument("--start-line", type=int, default=1, help="起始行号 (从 1 开始)")
    p_read.add_argument("--end-line", type=int, default=100, help="结束行号")
    p_read.add_argument("--source", default=None, help="限定数据源名称")
    p_read.set_defaults(func=cmd_read)

    raw_args = sys.argv[1:]
    normalized_args = reorder_argv(raw_args)
    args = parser.parse_args(normalized_args)
    args.func(args)

if __name__ == "__main__":
    main()
