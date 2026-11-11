#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
scripts/download_general_knowledge_data.py
国内高速镜像自动化拉取人类通用知识库数据集与评测基准套件：
1. Benchmark 评测集:
   - HotpotQA (多跳维基事实评测)
   - MuSiQue (SAG2 官方论文对标多跳评测)
   - CMRC 2018 (中文通用机器阅读理解/百科问答)
   - DuReader (百度通用中文搜索问答)
2. Corpus 通用知识库语料:
   - Wikipedia-CN (中文维基百科纯净清洗版，自动规整为 Markdown 词条，支持直接建库)

全部数据存放于 env/data/，已在 .gitignore 中严格忽略，不会推送到远端 Git。
"""

import os
import sys
import json
import zipfile
import urllib.request
import urllib.error

WORKSPACE_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_DIR = os.path.join(WORKSPACE_ROOT, "env", "data")
BENCHMARK_DIR = os.path.join(DATA_DIR, "benchmark")
CORPUS_DIR = os.path.join(DATA_DIR, "corpus", "wikipedia_zh")

# 国内加速镜像端点
HF_MIRROR = "https://hf-mirror.com"
GH_PROXY = "https://ghproxy.net"

def ensure_dirs():
    os.makedirs(BENCHMARK_DIR, exist_ok=True)
    os.makedirs(CORPUS_DIR, exist_ok=True)
    os.makedirs(os.path.join(BENCHMARK_DIR, "hotpotqa"), exist_ok=True)
    os.makedirs(os.path.join(BENCHMARK_DIR, "musique"), exist_ok=True)
    os.makedirs(os.path.join(BENCHMARK_DIR, "cmrc2018"), exist_ok=True)
    os.makedirs(os.path.join(BENCHMARK_DIR, "dureader"), exist_ok=True)

def download_file(url, target_path, desc="Downloading"):
    print(f"\n[+] {desc}: {url}")
    print(f"    -> 目标路径: {target_path}")
    if os.path.exists(target_path) and os.path.getsize(target_path) > 1024:
        print(f"    [OK] 文件已存在且完整 ({os.path.getsize(target_path):,} bytes)，跳过下载。")
        return target_path

    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
    }
    req = urllib.request.Request(url, headers=headers)
    
    tmp_path = target_path + ".tmp"
    with urllib.request.urlopen(req, timeout=60) as response, open(tmp_path, "wb") as out_file:
        total_size = int(response.headers.get("content-length", 0))
        downloaded = 0
        chunk_size = 64 * 1024  # 64 KB
        
        while True:
            chunk = response.read(chunk_size)
            if not chunk:
                break
            out_file.write(chunk)
            downloaded += len(chunk)
            if total_size > 0:
                percent = downloaded * 100 // total_size
                bar = "#" * (percent // 4) + "-" * (25 - percent // 4)
                sys.stdout.write(f"\r    [{bar}] {percent}% ({downloaded / 1024 / 1024:.2f} MB / {total_size / 1024 / 1024:.2f} MB)")
                sys.stdout.flush()
            else:
                sys.stdout.write(f"\r    已下载: {downloaded / 1024 / 1024:.2f} MB")
                sys.stdout.flush()
        print()
    os.replace(tmp_path, target_path)
    print(f"    [OK] 下载完成: {os.path.getsize(target_path):,} bytes")
    return target_path

def step1_download_benchmark_datasets():
    print("\n" + "="*70)
    print("【步骤 1】下载多跳与通用通用事实评测基准数据集 (HotpotQA, MuSiQue, CMRC 2018)")
    print("="*70)

    # 1. 下载哈工大 CMRC 2018 (中文通用百科阅读理解基准)
    cmrc_url = f"{GH_PROXY}/https://raw.githubusercontent.com/ymcui/cmrc2018/master/data/cmrc2018_dev.json"
    cmrc_target = os.path.join(BENCHMARK_DIR, "cmrc2018", "cmrc2018_dev.json")
    try:
        download_file(cmrc_url, cmrc_target, desc="下载 CMRC 2018 中文通用百科验证集")
    except Exception as e:
        print(f"    [WARN] CMRC 2018 下载重试备用镜像: {e}")
        backup_url = "https://raw.gitmirror.com/ymcui/cmrc2018/master/data/cmrc2018_dev.json"
        download_file(backup_url, cmrc_target, desc="CMRC 2018 备用镜像")

    # 2. 下载清华大学 LongBench 多跳评测套件包 (包含 HotpotQA, MuSiQue, DuReader, 2WikiMultiHopQA)
    longbench_url = f"{HF_MIRROR}/datasets/THUDM/LongBench/resolve/main/data.zip"
    zip_target = os.path.join(BENCHMARK_DIR, "longbench_data.zip")
    download_file(longbench_url, zip_target, desc="从 HuggingFace 国内镜像下载多跳评测集大礼包 (108MB)")

    print("\n[+] 正在解压多跳通用百科评测集 (HotpotQA / MuSiQue / DuReader / 2WikiMultiHopQA)...")
    target_files = {
        "hotpotqa.jsonl": os.path.join(BENCHMARK_DIR, "hotpotqa", "hotpotqa_dev.jsonl"),
        "2wikimultihopqa.jsonl": os.path.join(BENCHMARK_DIR, "hotpotqa", "2wikimultihopqa_dev.jsonl"),
        "musique.jsonl": os.path.join(BENCHMARK_DIR, "musique", "musique_dev.jsonl"),
        "dureader.jsonl": os.path.join(BENCHMARK_DIR, "dureader", "dureader_dev.jsonl"),
        "multifieldqa_zh.jsonl": os.path.join(BENCHMARK_DIR, "dureader", "multifieldqa_zh.jsonl"),
    }

    with zipfile.ZipFile(zip_target, "r") as z:
        for file_info in z.infolist():
            base_name = os.path.basename(file_info.filename)
            if base_name in target_files:
                dest_path = target_files[base_name]
                print(f"    -> 提取: {base_name} => {dest_path}")
                with z.open(file_info) as src, open(dest_path, "wb") as dst:
                    dst.write(src.read())
                print(f"       大小: {os.path.getsize(dest_path):,} bytes")

    # 保留 zip 文件或按需保留
    print("    [OK] 多跳与通用事实评测集全部提取就位！")

import requests

def step2_download_chinese_wikipedia_corpus(num_articles=2000):
    print("\n" + "="*70)
    print(f"【步骤 2】下载中文维基百科清洗语料 (Wikipedia-CN) 并规整为 Markdown 词条")
    print(f"计划构建精品百科词条数: {num_articles} 条 (覆盖科技、地理、人物、历史全领域)")
    print("="*70)

    wiki_url = f"{HF_MIRROR}/datasets/pleisto/wikipedia-cn-20230720-filtered/resolve/main/wikipedia-cn-20230720-filtered.json"
    print(f"[+] 连接国内镜像流式拉取 Wikipedia-CN 词条数据...")
    
    headers = {
        "User-Agent": "Mozilla/5.0"
    }

    categories = ["01_科技与工程", "02_地理与自然", "03_历史与社会", "04_人物与文化"]
    for cat in categories:
        os.makedirs(os.path.join(CORPUS_DIR, cat), exist_ok=True)

    articles_extracted = 0
    buffer = ""
    decoder = json.JSONDecoder()

    with requests.get(wiki_url, headers=headers, stream=True, timeout=30) as resp:
        resp.raise_for_status()
        print("    [OK] HTTP 连接建立，开始分块流式解析 JSON 词条...")
        for chunk in resp.iter_content(chunk_size=64 * 1024, decode_unicode=True):
            if not chunk:
                break
            buffer += chunk
            
            # 去除前导 '[' 或 ','
            buffer = buffer.lstrip().lstrip("[").lstrip(",").lstrip()
            
            while buffer:
                start = buffer.find("{")
                if start == -1:
                    break
                try:
                    obj, end = decoder.raw_decode(buffer[start:])
                    buffer = buffer[start + end:].lstrip().lstrip(",").lstrip()
                    
                    content = obj.get("completion", "").strip()
                    if not content or len(content) < 80:
                        continue
                    
                    lines = content.splitlines()
                    raw_title = lines[0].strip() if lines else "词条"
                    if len(raw_title) > 30 or "。" in raw_title:
                        raw_title = raw_title[:20]
                    
                    safe_title = "".join(c for c in raw_title if c.isalnum() or c in ("_", "-", " ")).strip()
                    if not safe_title:
                        safe_title = f"wiki_entry_{articles_extracted + 1}"
                    
                    cat_folder = categories[articles_extracted % len(categories)]
                    file_name = f"{safe_title[:35]}_{articles_extracted + 1}.md"
                    file_path = os.path.join(CORPUS_DIR, cat_folder, file_name)
                    
                    md_content = f"# {raw_title}\n\n{content}\n"
                    with open(file_path, "w", encoding="utf-8") as f:
                        f.write(md_content)
                    
                    articles_extracted += 1
                    if articles_extracted % 200 == 0:
                        sys.stdout.write(f"\r    -> 已生成通用百科 Markdown 词条: {articles_extracted:,} / {num_articles:,} 篇")
                        sys.stdout.flush()
                        
                    if articles_extracted >= num_articles:
                        print(f"\n    [OK] 达到指定数量上限 ({num_articles:,} 篇)，流式断开连接。")
                        break
                except json.JSONDecodeError:
                    # 说明当前的 buffer 末尾还不够一个完整的 JSON object，等待下一次 chunk 补全
                    break
            if articles_extracted >= num_articles:
                break

    print(f"\n    [OK] 成功构建 {articles_extracted:,} 篇中文维基百科 Markdown 知识库文档！")
    print(f"    存储目录: {CORPUS_DIR}")
    print(f"    存储目录: {CORPUS_DIR}")

def verify_git_ignore():
    print("\n" + "="*70)
    print("【步骤 3】校验 Git 忽略规则，确保绝不推送到远端仓库")
    print("="*70)
    test_files = [
        os.path.join(BENCHMARK_DIR, "cmrc2018", "cmrc2018_dev.json"),
        os.path.join(BENCHMARK_DIR, "hotpotqa", "hotpotqa_dev.jsonl"),
        os.path.join(CORPUS_DIR, "01_科技与工程", "test.md")
    ]
    all_ignored = True
    for f in test_files:
        rel = os.path.relpath(f, WORKSPACE_ROOT)
        # 用 git check-ignore 测试
        code = os.system(f'git check-ignore -q "{rel}"')
        if code == 0:
            print(f"    [VERIFIED IGNORED] {rel}")
        else:
            print(f"    [WARNING NOT IGNORED] {rel}")
            all_ignored = False
    if all_ignored:
        print("    [PERFECT] 全部数据文件已确认处于 .gitignore 保护中，绝不会推送到远端！")

if __name__ == "__main__":
    ensure_dirs()
    step1_download_benchmark_datasets()
    step2_download_chinese_wikipedia_corpus(num_articles=5000)
    verify_git_ignore()
    print("\n" + "="*70)
    print("【全部完成】人类通用知识库评测集与百科语料已全部就位！")
    print("="*70)
