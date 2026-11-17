#!/usr/bin/env python3
"""MCP 工具面离线审计（G-M8/G-M9）。

直接拉取运行中服务的 tools/list，按业界检查器同一套判定跑：
  1) 描述组件（用途 / 何时用 / 邻居取舍 / 上限 / 无参闭合 / required）
  2) 提示词预算（总字节、outputSchema 占比、单条长度）
  3) 25 条真实任务的工具选型（IDF 加权 + 名称加权 + 区分度阈值）

用法：
    # 正式环境需带 key（从 env/.envprod 动态读取，勿硬编码）
    set MCP_API_KEY=<from env/.envprod REFERENCERAG_API_KEY>
    python scripts/mcp-tool-audit.py http://127.0.0.1:5123/api/mcp
    python scripts/mcp-tool-audit.py http://127.0.0.1:5124/api/mcp --emit-json /tmp/listing.json
    python scripts/mcp-tool-audit.py listing.json --tasks scripts/mcp-tool-tasks.json

退出码非 0 表示存在 FAIL（可直接接 CI）。
"""
import argparse
import json
import math
import os
import re
import sys
import urllib.request

STOP_BG = {"一个", "这个", "那个", "可以", "需要", "用于", "通过", "以及", "并且",
           "我们", "你的", "如果", "就是", "还有", "什么", "哪些", "一下", "怎么",
           "是否", "多少", "目前", "当前", "所有"}

DEFAULT_TASKS = [
    {"task": "查一下 MCP 是什么", "expect": "semantic_search"},
    {"task": "精确查找错误码 E1001", "expect": "lexical_search"},
    {"task": "既要精确匹配又要语义理解地检索工具调用", "expect": "hybrid_search"},
    {"task": "在 Notes 库里搜索知识图谱相关内容", "expect": "knowledge_base_search"},
    {"task": "看看系统里有哪些知识库", "expect": "knowledge_base_list"},
    {"task": "列出这个知识库里有哪些文件", "expect": "list_files"},
    {"task": "把这个文件第 10 到 20 行读出来", "expect": "read_note_content"},
    {"task": "按 chunkId 展开这个切片", "expect": "knowledge_base_drill_down"},
    {"task": "这个切片属于哪个文件", "expect": "get_document_info"},
    {"task": "看看某个数据源里有哪些文件", "expect": "get_file_tree"},
    {"task": "这个文件的大纲标题有哪些", "expect": "get_note_outline"},
    {"task": "在源码里搜 TODO", "expect": "search_file_content"},
    {"task": "找一下所有 *.md 文件", "expect": "find_files_by_pattern"},
    {"task": "看看有哪些数据源", "expect": "list_sources"},
    {"task": "这个数据源的物理路径和大小", "expect": "get_source_info"},
    {"task": "索引建好了吗", "expect": "get_index_status"},
    {"task": "用的是哪个嵌入模型", "expect": "get_system_stats"},
    {"task": "沿着链接找相关笔记", "expect": "graph_search"},
    {"task": "按标题找知识图谱节点", "expect": "graph_search_by_title"},
    {"task": "把这段文本转成向量", "expect": "embed_text"},
    {"task": "批量把这几段文本转向量", "expect": "embed_batch"},
    {"task": "这两句话相似吗", "expect": "calculate_similarity"},
    {"task": "给这些候选文档重新排序", "expect": "rerank_results"},
    {"task": "服务版本是啥", "expect": "get_service_info"},
    {"task": "服务还活着吗", "expect": "ping"},
]


def tokenize(text):
    out = []
    for w in re.findall(r"[A-Za-z0-9]{2,}", text or ""):
        out.append(w.lower())
    for run in re.findall(r"[\u4e00-\u9fff]+", text or ""):
        if len(run) == 1:
            out.append(run)
            continue
        for i in range(len(run) - 1):
            bg = run[i:i + 2]
            if bg not in STOP_BG:
                out.append(bg)
    return out


def rpc(base, method, params=None):
    body = {"jsonrpc": "2.0", "id": 1, "method": method}
    if params is not None:
        body["params"] = params
    headers = {"Content-Type": "application/json",
               "Accept": "application/json, text/event-stream"}
    # 正式环境启用认证：从环境变量或 env/.envprod 取 key（不写进脚本/文档）。
    key = os.environ.get("MCP_API_KEY", "")
    if key:
        headers["X-API-Key"] = key
    req = urllib.request.Request(
        base, data=json.dumps(body, ensure_ascii=False).encode(), method="POST",
        headers=headers)
    with urllib.request.urlopen(req, timeout=60) as r:
        raw = r.read().decode("utf-8", "replace")
    data = [l[5:].strip() for l in raw.splitlines() if l.startswith("data:")]
    return json.loads(data[-1] if data else raw)


def load_tools(source):
    if source.startswith("http"):
        rpc(source, "initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                  "clientInfo": {"name": "rr-mcp-audit", "version": "1"}})
        return rpc(source, "tools/list", {})["result"]["tools"]
    raw = json.load(open(source, encoding="utf-8"))
    return (raw.get("result") or raw).get("tools", [])


def lint(tools):
    names = [t["name"] for t in tools]
    compact = len(json.dumps(tools, ensure_ascii=False, separators=(",", ":")).encode("utf-8"))
    desc_bytes = sum(len(t["description"].encode("utf-8")) for t in tools)
    out_bytes = sum(len(json.dumps(t.get("outputSchema") or {}, ensure_ascii=False,
                                   separators=(",", ":")).encode("utf-8")) for t in tools)

    fails, warns = [], []
    if compact > 30_000:
        fails.append(f"定义总字节 {compact} > 30 KB")
    # outputSchema 声明后必须同时建模成功/失败形态（anyOf），体积约为单形态的 2 倍。
    # 7 个结构化工具实测占 25.9%，上限按 35% 留出再补 2-3 个工具的空间。
    if out_bytes / max(compact, 1) > 0.35:
        fails.append(f"outputSchema 占比 {out_bytes / compact:.1%} > 35%")
    for t in tools:
        if len(t["description"]) > 600:
            fails.append(f"{t['name']} 描述 {len(t['description'])} 字符 > 600")
        if not t.get("title"):
            warns.append(f"{t['name']} 缺 title")
        d = t["description"]
        if not any(k in d for k in ("适合", "用于", "在需要", "时使用", "推荐", "建议", "仅当")):
            warns.append(f"{t['name']} 缺「何时该用」")
        if not any(o != t["name"] and o in d for o in names):
            warns.append(f"{t['name']} 未点名相邻工具")
        for k, v in (t["inputSchema"].get("properties") or {}).items():
            if (v.get("type") in ("integer", "number")
                    and not any(w in (v.get("description") or "")
                                for w in ("上限", "最大", "最多", "范围", "建议", "默认"))):
                warns.append(f"{t['name']}.{k} 未写下界/上限")
        if t["inputSchema"].get("properties") and "required" not in t["inputSchema"]:
            warns.append(f"{t['name']} 缺 required 键")
        if not t["inputSchema"].get("properties") and t["inputSchema"].get("additionalProperties") is not False:
            warns.append(f"{t['name']} 无参却缺 additionalProperties:false")
    return fails, warns, (compact, desc_bytes, out_bytes)


def select(tools, tasks):
    docs = []
    for t in tools:
        body = set(tokenize(t.get("title"))) | set(tokenize(t.get("description")))
        for k, v in (t.get("inputSchema", {}).get("properties") or {}).items():
            body |= set(tokenize(k)) | set(tokenize((v or {}).get("description")))
        docs.append((set(tokenize(t["name"])), body))

    df = {}
    for nt, bt in docs:
        for w in nt | bt:
            df[w] = df.get(w, 0) + 1

    results = []
    for item in tasks:
        want = item["expect"]
        task = set(tokenize(item["task"]))
        scores = []
        for t, (nt, bt) in zip(tools, docs):
            s = 0.0
            for w in task:
                if w in df:
                    wt = math.log(1 + len(tools) / df[w])
                    s += 3 * wt if w in nt else wt if w in bt else 0
            scores.append((s, t["name"]))
        scores.sort(key=lambda p: (-p[0], p[1]))
        top, top_name = scores[0]
        mine = next((s for s, n in scores if n == want), None)
        if mine is None:
            results.append((item["task"], want, top_name, top, mine, "错误"))
        elif top == 0:
            results.append((item["task"], want, top_name, top, mine, "错误"))
        elif mine < top:
            results.append((item["task"], want, top_name, top, mine, "错误"))
        else:
            runner = next((p for p in scores if p[1] != want), (0.0, "-"))
            verdict = "歧义" if runner[0] >= 0.8 * top else "明确"
            results.append((item["task"], want, top_name, top, mine, verdict))
    return results


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("source", help="MCP endpoint (http://...) 或已保存的 listing JSON")
    ap.add_argument("--emit-json", default="")
    ap.add_argument("--tasks", default="")
    args = ap.parse_args()

    tools = load_tools(args.source)
    if args.emit_json:
        open(args.emit_json, "w", encoding="utf-8").write(
            json.dumps({"result": {"tools": tools}}, ensure_ascii=False, indent=1))

    tasks = json.load(open(args.tasks, encoding="utf-8")) if args.tasks else DEFAULT_TASKS

    fails, warns, (compact, desc_bytes, out_bytes) = lint(tools)
    results = select(tools, tasks)
    clear = sum(1 for r in results if r[5] == "明确")
    amb = sum(1 for r in results if r[5] == "歧义")
    wrong = sum(1 for r in results if r[5] == "错误")

    # 基线（P1 提示词结构化之后）：明确 17/25、错误 1/25。低于基线即回归。
    if clear < 17:
        fails.append(f"选型明确 {clear}/{len(tasks)} < 17（P1 基线）")
    if wrong > 1:
        fails.append(f"选型错误 {wrong}/{len(tasks)} > 1")

    print(f"tools={len(tools)} compact={compact}B desc={desc_bytes}B "
          f"outputSchema={out_bytes}B ({out_bytes / max(compact, 1):.1%})")
    print(f"selection: 明确 {clear} 歧义 {amb} 错误 {wrong}（共 {len(tasks)}）")
    for r in results:
        if r[5] != "明确":
            print(f"  {r[5]:2} {r[0]} → {r[2]}({r[3]:.1f}) vs expect {r[1]}({r[4]:.1f})")
    print(f"warns={len(warns)}")
    for w in warns[:40]:
        print("  WARN", w)
    for f in fails:
        print("  FAIL", f)
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
