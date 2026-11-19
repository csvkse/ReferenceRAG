#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
ReferenceRAG Smoke Test Runner
基于 §8.2 验收规范，逐条执行 CLI 子命令并断言退出码与关键输出特征。
零外部依赖（纯 Python 标准库 subprocess + sys + os + time）。
支持 UTF-8 与 GBK 两种编码环境自动化回归。
"""

import sys
import os
import time
import subprocess
from typing import List, Tuple, Dict, Any, Optional

DEFAULT_BASE_URL = os.environ.get("OBSIDIAN_RAG_API_URL", "http://127.0.0.1:5123")
DEFAULT_API_KEY = os.environ.get("OBSIDIAN_RAG_API_KEY", "rag_live_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")

# 冒烟测试用例规范: (子命令参数列表, 期望退出码, 期望输出包含文本, 测试描述)
TEST_CASES: List[Tuple[List[str], int, str, str]] = [
    # 基础探活与统计
    (["ping"], 0, "服务连通正常", "探活指令 ping"),
    (["stats"], 0, "数据总量", "系统与索引统计 stats"),
    (["list-kbs"], 0, "已注册知识库列表", "知识库列表 list-kbs"),
    (["list-sources"], 0, "数据源清单", "数据源统计 list-sources"),
    (["list-files", "*.md", "--limit", "5"], 0, "匹配结果", "文件查找通配符 list-files"),

    # 检索通道与策略
    (["search", "PR", "--mode", "lexical", "--top-k", "2"], 0, "切片ID", "精确词法检索"),
    (["search", "PR", "--format", "brief", "--top-k", "2"], 0, "[[", "精简模式 brief 格式"),
    (["search", "PR", "--rest", "--top-k", "2"], 0, "PR", "REST 单轨检索通道 search --rest"),
    (["search", "人类每日状态曲线", "--policy", "concept", "--top-k", "2"], 0, "concept", "概念型策略 concept 检索"),
    (["search", "完全不存在的词zzzz_impossible_token_999"], 3, "未命中", "无命中断言 NoMatch (exit 3)"),

    # 鉴权与边界校验
    (["search", "PR", "--api-key", "invalid_bad_key_test"], 5, "", "鉴权失败检测 (exit 5)"),
    (["read", "non_existent_file_xyz_12345.md"], 4, "", "文件不存在定位 (exit 4)"),
    (["read", "知识库/PR含义.md", "--source", "Temp", "--start-line", "10", "--end-line", "5"], 2, "起始行号", "行号逆序边界校验 (exit 2)"),
    (["read", "--citations", "[{\"knowledgeBaseId\":\"test\",\"documentId\":\"doc1\",\"chunkId\":\"chunk1\",\"documentVersion\":1,\"digest\":\"abc\"}]"], 0, "证据回读结果", "证据批量回读 read --citations"),

    # 内容阅读与结构解析
    (["read", "知识库/PR含义.md", "--source", "Temp", "--start-line", "1", "--end-line", "10"], 0, "文件正文", "正文分段读取 read"),
    (["outline", "知识库/PR含义.md", "--source", "Temp"], 0, "文档大纲", "Markdown 大纲解析 outline"),
    (["tree", "知识库", "--source", "Temp"], 0, "目录树", "目录层级树 tree"),
    (["grep", "PR", "--source", "Temp", "--limit", "5"], 0, "正则检索", "磁盘正文搜索 grep"),

    # 图谱与系统任务状态
    (["graph", "#authors-note"], 0, "知识图谱", "Wiki-links 关系图谱遍历 graph"),
    (["status"], 0, "ReferenceRAG", "后台同步任务与健康度 status"),
    (["ask", "什么是PR？", "--top-k", "2"], 0, "问答", "知识库单轨有据问答 ask"),
]

def run_test_case(cli_path: str, args: List[str], expected_code: int, expected_keyword: str,
                  base_url: str, api_key: str, env_encoding: Optional[str] = None) -> Tuple[bool, int, str, float]:
    """执行单条冒烟测试并返回 (是否通过, 实际退出码, 输出摘要, 耗时秒数)"""
    cmd = [sys.executable]
    if env_encoding != "gbk":
        cmd.append("-X")
        cmd.append("utf8")
    cmd.append(cli_path)

    # 注入全局地址与鉴权（若 args 中未自行覆盖）
    full_args = list(args)
    if not any(a.startswith("--base-url") for a in full_args):
        full_args.extend(["--base-url", base_url])
    if not any(a.startswith("--api-key") for a in full_args):
        full_args.extend(["--api-key", api_key])

    cmd.extend(full_args)

    env = os.environ.copy()
    if env_encoding:
        env["PYTHONIOENCODING"] = env_encoding

    start_t = time.time()
    try:
        proc = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
            timeout=40,
            text=True,
            encoding="utf-8",
            errors="backslashreplace"
        )
        elapsed = time.time() - start_t
        out = (proc.stdout or "") + (proc.stderr or "")
        code = proc.returncode

        passed = (code == expected_code)
        if passed and expected_keyword:
            passed = (expected_keyword in out)

        return passed, code, out.strip(), elapsed
    except subprocess.TimeoutExpired:
        return False, -1, "执行超时 (>40s)", time.time() - start_t
    except Exception as e:
        return False, -1, f"运行异常: {e}", time.time() - start_t

def main():
    cli_dir = os.path.dirname(os.path.abspath(__file__))
    default_cli = os.path.join(cli_dir, "rag_cli.py")

    import argparse
    parser = argparse.ArgumentParser(description="ReferenceRAG 冒烟测试套件")
    parser.add_argument("--cli-path", default=default_cli, help="待测 rag_cli.py 绝对路径")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help=f"服务测试地址 (默认: {DEFAULT_BASE_URL})")
    parser.add_argument("--api-key", default=DEFAULT_API_KEY, help="服务测试鉴权密钥")
    parser.add_argument("--test-gbk", action="store_true", help="额外在 GBK 编码环境下跑一遍验证防崩溃能力")
    cli_args = parser.parse_args()

    print(f"==================================================")
    print(f"  ReferenceRAG 技能冒烟回归测试")
    print(f"  待测目标: {cli_args.cli_path}")
    print(f"  服务地址: {cli_args.base_url}")
    print(f"==================================================\n")

    total = 0
    passed_count = 0
    failed_cases = []

    # 第一轮：标准 UTF-8 环境
    print(">>> 正在执行第 1 轮测试：UTF-8 字符流环境...")
    for args, exp_code, exp_kw, desc in TEST_CASES:
        total += 1
        ok, actual_code, output, elapsed = run_test_case(
            cli_args.cli_path, args, exp_code, exp_kw, cli_args.base_url, cli_args.api_key, env_encoding=None
        )
        status_tag = "[PASS]" if ok else "[FAIL]"
        if ok:
            passed_count += 1
            print(f"  {status_tag} {desc} (耗时: {elapsed:.2f}s, exit: {actual_code})")
        else:
            print(f"  {status_tag} {desc} (期望 exit: {exp_code}, 实际: {actual_code}, 期望包含: '{exp_kw}')")
            first_line = output.splitlines()[0] if output else "无输出"
            print(f"         输出截断: {first_line[:120]}")
            failed_cases.append((desc, exp_code, actual_code, first_line))

    # 第二轮：GBK 环境防崩溃验证
    if cli_args.test_gbk:
        print("\n>>> 正在执行第 2 轮测试：PYTHONIOENCODING=gbk 降级防崩溃验证...")
        gbk_cases = [
            (["ping"], 0, "", "GBK 下探活 ping (无 Unicode 崩溃)"),
            (["stats"], 0, "", "GBK 下统计 stats (无 Unicode 崩溃)"),
            (["search", "PR", "--mode", "lexical"], 0, "", "GBK 下检索 search (符号自动降级)"),
        ]
        for args, exp_code, exp_kw, desc in gbk_cases:
            total += 1
            ok, actual_code, output, elapsed = run_test_case(
                cli_args.cli_path, args, exp_code, exp_kw, cli_args.base_url, cli_args.api_key, env_encoding="gbk"
            )
            status_tag = "[PASS]" if ok else "[FAIL]"
            if ok:
                passed_count += 1
                print(f"  {status_tag} {desc} (耗时: {elapsed:.2f}s, exit: {actual_code})")
            else:
                print(f"  {status_tag} {desc} (期望 exit: {exp_code}, 实际: {actual_code})")
                failed_cases.append((desc, exp_code, actual_code, output[:120]))

    print(f"\n==================================================")
    print(f"  冒烟测试结果: {passed_count}/{total} 通过 (成功率: {passed_count/total*100:.1f}%)")
    print(f"==================================================")

    if failed_cases:
        print("\n失败用例清单:")
        for desc, exp, act, out in failed_cases:
            print(f"- {desc}: 期望退出码 {exp}, 实际退出码 {act} | 详情: {out}")
        sys.exit(1)
    else:
        print("\n所有回归验收断言全部通过！系统与 CLI 客户端状态稳健。")
        sys.exit(0)

if __name__ == "__main__":
    main()
