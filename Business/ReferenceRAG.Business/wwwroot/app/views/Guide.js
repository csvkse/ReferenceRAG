import { defineComponent as _defineComponent } from 'vue';
import { useMessage } from 'naive-ui';
import { CopyOutline } from '@vicons/ionicons5';

const logCommands = `ReferenceRAG.WebHost.exe --logs          # 查看最近 50 行
ReferenceRAG.WebHost.exe --logs -n 200   # 查看最近 200 行
ReferenceRAG.WebHost.exe --logs -f Error # 过滤含 Error 的行
ReferenceRAG.WebHost.exe --logs --tail   # 实时跟踪（Ctrl+C 退出）`;

const logLevelJson = `{
  "Serilog": { "MinimumLevel": "Debug" }
}`;

const desktopApiJson = `{ "Desktop": { "EnableLocalApi": true } }`;

const dockerComposeExample = `services:
  referencerag:
    # 官方容器镜像标签可选:
    #   - ghcr.io/csvkse/referencerag:latest-cuda  (NVIDIA CUDA 12 满血加速)
    #   - ghcr.io/csvkse/referencerag:latest-rocm  (AMD ROCm 硬件加速)
    #   - ghcr.io/csvkse/referencerag:latest-intel (Intel GPU / Level Zero 加速)
    #   - ghcr.io/csvkse/referencerag:latest       (通用 / CPU 跨平台兼容)
    image: ghcr.io/csvkse/referencerag:latest-cuda
    container_name: referencerag
    restart: unless-stopped
    ports:
      - "7897:7897"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ReferenceRAG__dataPath=/app/data
      - ReferenceRAG__modelPath=/app/models
    volumes:
      - ./data:/app/data
      - ./models:/app/models
    # 若使用 NVIDIA 显卡加速:
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]`;

const dockerRunCpu = `docker run -d --name referencerag \
  -p 7897:7897 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  ghcr.io/csvkse/referencerag:latest`;

const dockerRunCuda = `# 需要宿主机具备 NVIDIA 显卡驱动与 nvidia-container-toolkit
docker run -d --name referencerag --gpus all \
  -p 7897:7897 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  ghcr.io/csvkse/referencerag:latest-cuda`;

const dockerRunAmd = `# 挂载 Linux KFD 算力设备与 DRI 渲染节点
docker run -d --name referencerag \
  --device=/dev/kfd --device=/dev/dri \
  --group-add video --group-add render \
  -p 7897:7897 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  ghcr.io/csvkse/referencerag:latest-rocm`;

const dockerRunIntel = `# 挂载 Linux DRI 渲染节点 (已内置 Level Zero / OpenCL 驱动)
docker run -d --name referencerag \
  --device=/dev/dri \
  --group-add video --group-add render \
  -p 7897:7897 \
  -v ./data:/app/data \
  -v ./models:/app/models \
  ghcr.io/csvkse/referencerag:latest-intel`;

const linuxCudaInstall = `wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.0-1_all.deb
sudo dpkg -i cuda-keyring_1.0-1_all.deb
sudo apt update
sudo apt install cuda-toolkit-12-3`;

const linuxCudaEnv = `echo 'export PATH=/usr/local/cuda/bin:$PATH' >> ~/.bashrc
echo 'export LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH' >> ~/.bashrc
source ~/.bashrc`;

const skillExample = `---
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
| \`rag:关键词\` | \`rag:Git 分支管理\` |
| \`/rag 关键词\` | \`/rag TypeScript 类型\` |

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
- \`笔记里有没有 xxx\`
- \`帮我搜一下笔记\`
- \`在知识库中查找\`
- \`看看笔记里的 xxx\`

### 4. 不触发场景
- 纯动作词无领域词：\`查询 Git\`、\`搜索配置\` ❌
- 实时信息：天气、新闻、股票 ❌
- 明确其他数据源：数据库查询、API 调用 ❌

## 执行流程

**查询词扩展策略**：将用户输入扩展为多个相关关键词（中英文、同义词、相关概念），提升召回率。

示例：\`Git 分支管理\` → \`Git 分支管理 branch 版本控制 分支策略 git flow\`

直接调用 \`POST http://localhost:7897/api/ai/query\` 执行 HybridRerank 搜索，格式化返回结果。

## 配置（可选）

如需自定义地址，修改 \`~/.agents/.env\`：

\`\`\`env
OBSIDIAN_RAG_API_URL=http://localhost:7897
OBSIDIAN_RAG_API_KEY=
\`\`\`

## API 调用示例

### Windows Git Bash 中文请求方式（重要）

⚠️ **Windows Git Bash 中直接使用 \`-d\` 发送中文会导致乱码**，因为 bash 字符串处理会破坏 UTF-8 编码。

**正确方式：使用 heredoc + --data-binary**
\`\`\`bash
curl -s -X POST "http://localhost:7897/api/ai/query" \\
  -H "Content-Type: application/json; charset=utf-8" \\
  --data-binary @- << 'EOF'
{"query": "搜索关键词", "mode": "HybridRerank", "topK": 10}
EOF
\`\`\`

**关键点：**
- \`--data-binary @-\` 从标准输入读取原始二进制数据
- 使用单引号 \`'EOF'\` 包裹内容，防止变量展开
- 确保中文字符以正确的 UTF-8 字节发送

### 其他方式
\`\`\`bash
# PowerShell（推荐） - 不需要特殊处理
curl -X POST "http://localhost:7897/api/ai/query" -H "Content-Type: application/json" -d '{"query": "搜索关键词", "mode": "HybridRerank"}'

# CMD - 需先设置编码
chcp 65001
curl -X POST "http://localhost:7897/api/ai/query" -H "Content-Type: application/json; charset=utf-8" -d "{\\"query\\": \\"搜索关键词\\"}"
\`\`\`

**查询模式**：Quick(3) | Standard(10) | Hybrid(15) | HybridRerank(10,推荐) | Deep(20)

## 服务地址

- Web UI: \`http://localhost:7897\`
- Swagger: \`http://localhost:7897/swagger\`

## 支持的模型

**Embedding**：bge-small-zh-v1.5、bge-base-zh-v1.5、bge-large-zh-v1.5、bge-m3

**Rerank**：bge-reranker-base、bge-reranker-large`;

const skillTemplate = `# [Skill Name]

[Skill 描述]

## 触发条件
- 触发词 1
- 触发词 2

## 使用方式
[如何使用此 Skill]

## 参数
- param1: 参数描述
- param2: 参数描述

## 示例
\`\`\`
示例输入 -> 示例输出
\`\`\``;

const component = /*@__PURE__*/ _defineComponent({
    __name: 'Guide',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        
        const mcpConfigExample = JSON.stringify({
            "mcpServers": {
                "ReferenceRAG": {
                    "isActive": true,
                    "name": "ReferenceRAG",
                    "type": "streamableHttp",
                    "description": "",
                    "baseUrl": "http://127.0.0.1:7897/api/mcp",
                    "command": "",
                    "args": [],
                    "env": {},
                    "installSource": "unknown"
                }
            }
        }, null, 2);

        const mcpSearchExample = JSON.stringify({
            tool: "rag-semantic-search",
            arguments: {
                query: "向量数据库",
                topK: 10,
                sources: []
            }
        }, null, 2);

        const mcpDrilldownExample = JSON.stringify({
            tool: "rag-hybrid-search",
            arguments: {
                query: "向量数据库",
                topK: 10,
                k1: 1.5,
                b: 0.75,
                enableRerank: false
            }
        }, null, 2);

        const copySkillTemplate = async () => {
            try {
                await navigator.clipboard.writeText(skillTemplate);
                message.success('Skill 模板已复制到剪贴板');
            } catch {
                message.error('复制失败');
            }
        };

        const downloadSkillTemplate = () => {
            const blob = new Blob([skillTemplate], { type: 'text/markdown' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = 'skill-template.md';
            a.click();
            URL.revokeObjectURL(url);
            message.success('模板文件已下载');
        };

        const __returned__ = {
            message,
            logCommands,
            logLevelJson,
            desktopApiJson,
            dockerComposeExample,
            dockerRunCpu,
            dockerRunCuda,
            dockerRunAmd,
            dockerRunIntel,
            linuxCudaInstall,
            linuxCudaEnv,
            mcpConfigExample,
            mcpSearchExample,
            mcpDrilldownExample,
            skillExample,
            skillTemplate,
            copySkillTemplate,
            downloadSkillTemplate,
            get CopyOutline() { return CopyOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <n-tabs type="line" animated>
      <!-- 平台与硬件加速 -->
      <n-tab-pane name="platforms" tab="平台与硬件加速">
        <n-space vertical :size="16">
          <!-- 运行形态与支持平台 -->
          <n-card title="🖥️ 跨平台架构与运行形态">
            <n-grid :cols="3" :x-gap="12" :y-gap="12" responsive="screen">
              <n-grid-item>
                <n-card size="small" embedded title="Windows 桌面端 (DesktopHost)">
                  <n-space vertical :size="8">
                    <n-tag type="success" size="small">独立自包含 · 绿色免装 .NET</n-tag>
                    <n-text depth="2">内置原生轻量窗口容器，解压即用。系统仅需具备 WebView2 运行时（Win10/Win11 默认已随 Edge 自带）。</n-text>
                    <n-text depth="3">支持托盘后台驻留、全局热键唤醒与沉浸式交互。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
              <n-grid-item>
                <n-card size="small" embedded title="Web 服务端 (WebHost / ApiHost)">
                  <n-space vertical :size="8">
                    <n-tag type="info" size="small">控制台 / Windows 服务 / Systemd</n-tag>
                    <n-text depth="2">标准 ASP.NET Core 10 高并发后端，默认监听 <code>http://localhost:7897</code> 端口。</n-text>
                    <n-text depth="3">提供完整的现代化 Web UI 仪表盘、RESTful API 与交互式 Swagger 接口文档。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
              <n-grid-item>
                <n-card size="small" embedded title="Docker 容器化 (Linux Container)">
                  <n-space vertical :size="8">
                    <n-tag type="warning" size="small">4 大专属镜像 · GHCR 一键拉取</n-tag>
                    <n-text depth="2">基于 Ubuntu 24.04 (Noble) 打造，已适配 CPU、NVIDIA CUDA、AMD ROCm、Intel GPU 4 类镜像。</n-text>
                    <n-text depth="3">具备非 root 用户权限隔离、持久化数据挂载与自动化健康检查。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
            </n-grid>
          </n-card>

          <!-- 桌面端硬件加速选型矩阵 -->
          <n-card title="🚀 桌面端硬件加速选型指南">
            <n-space vertical :size="12">
              <n-alert type="info" :show-icon="false">
                ReferenceRAG 桌面端采用<strong>多目标硬件加速分发架构</strong>。不同分包预置了对应的显卡计算库，下载适合您显卡型号的安装包解压即可直接开启硬件加速：
              </n-alert>

              <n-table :bordered="true" :single-line="false" size="small">
                <thead>
                  <tr>
                    <th style="min-width: 180px">安装包名称</th>
                    <th style="min-width: 170px">适配硬件与显卡型号</th>
                    <th style="min-width: 120px">硬件加速引擎</th>
                    <th style="min-width: 170px">依赖安装需求</th>
                    <th>定位与推荐场景</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>
                      <n-text strong>ReferenceRAG-win-x64.zip</n-text><br/>
                      <n-text depth="3">(别名: ...-universal.zip)</n-text>
                    </td>
                    <td>
                      <strong>全显卡通用</strong><br/>
                      AMD / Intel / NVIDIA / 高通骁龙 / 各类核显
                    </td>
                    <td><n-tag type="success" size="small">DirectML + Vulkan</n-tag></td>
                    <td><strong>完全免装额外依赖！</strong> 有显卡驱动即可直接硬件加速</td>
                    <td><n-tag type="success" size="small">🌟 强烈推荐</n-tag> 开箱即用，零安装门槛，日常使用首选</td>
                  </tr>
                  <tr>
                    <td><n-text strong>ReferenceRAG-win-x64-cuda.zip</n-text></td>
                    <td>
                      <strong>NVIDIA 独立显卡</strong><br/>
                      GeForce RTX 20/30/40/50 系列、GTX 16 系列等
                    </td>
                    <td><n-tag type="warning" size="small">CUDA 12 + TensorRT</n-tag></td>
                    <td><strong>免装 3GB+ Toolkit！</strong> 已内嵌微型绿色核心库 (~700MB)</td>
                    <td><n-tag type="warning" size="small">🚀 极致算力（满血开箱）</n-tag> 纯净 Windows 解压即享极致 GPU 加速</td>
                  </tr>
                  <tr>
                    <td><n-text strong>ReferenceRAG-win-x64-cuda-slim.zip</n-text></td>
                    <td>
                      <strong>NVIDIA 独显极客环境</strong><br/>
                      本机已安装 CUDA 12 / PyTorch / Ollama
                    </td>
                    <td><n-tag type="warning" size="small">CUDA 12 + TensorRT</n-tag></td>
                    <td><strong>无内嵌依赖 (~70MB 极小体积)</strong>，自动借用本机已有 CUDA 12 环境</td>
                    <td><n-tag type="info" size="small">⚡ 极客轻量下载</n-tag> 适合开发者和已有深度学习环境用户秒速下载</td>
                  </tr>
                  <tr>
                    <td><n-text strong>ReferenceRAG-win-x64-amd.zip</n-text></td>
                    <td>
                      <strong>AMD Radeon 显卡</strong><br/>
                      RX 6000/7000/8000 独显及 Ryzen/APU 锐龙核显
                    </td>
                    <td><n-tag type="error" size="small">DirectML + Vulkan</n-tag></td>
                    <td><strong>完全免装 ROCm！</strong> 官方 Adrenalin 显卡驱动原生承接</td>
                    <td><n-tag type="error" size="small">🔴 AMD 优化</n-tag> 专为 A 卡优化的预置加速路径</td>
                  </tr>
                  <tr>
                    <td><n-text strong>ReferenceRAG-win-x64-intel.zip</n-text></td>
                    <td>
                      <strong>Intel 显卡与酷睿处理器</strong><br/>
                      Arc 独显、酷睿 Ultra 核显及 Iris Xe
                    </td>
                    <td><n-tag type="info" size="small">DirectML + Vulkan</n-tag></td>
                    <td><strong>完全免装 oneAPI！</strong> 官方 Intel 显卡驱动原生承接</td>
                    <td><n-tag type="info" size="small">🔵 Intel 优化</n-tag> 专为 Intel GPU 优化的预置加速路径</td>
                  </tr>
                  <tr>
                    <td><n-text strong>ReferenceRAG-win-x64-cpu.zip</n-text></td>
                    <td>
                      <strong>纯 CPU / 老旧电脑 / 虚拟机</strong><br/>
                      任何支持 x64 的 Windows 设备
                    </td>
                    <td><n-tag size="small">纯 CPU (AVX2/AVX-512)</n-tag></td>
                    <td><strong>零额外依赖！</strong> 纯软件多核高并发计算</td>
                    <td><n-tag size="small">🛡️ 纯净兼容</n-tag> 无独显机器、云虚拟机或低功耗运行</td>
                  </tr>
                </tbody>
              </n-table>
            </n-space>
          </n-card>

          <!-- 容器化部署指南 -->
          <n-card title="🐳 Docker 容器化部署与硬件直通">
            <n-space vertical :size="12">
              <n-text>官方镜像发布于 GitHub Container Registry (<code>ghcr.io/csvkse/referencerag</code>)，针对不同硬件环境提供了对应的容器镜像标签：</n-text>
              <n-tabs type="segment">
                <n-tab-pane name="docker-compose" tab="Docker Compose (推荐)">
                  <n-space vertical :size="8">
                    <n-text depth="3">一键编排示例 (含持久化数据卷挂载与 GPU 直通):</n-text>
                    <n-code :code="dockerComposeExample" language="yaml" word-wrap />
                  </n-space>
                </n-tab-pane>
                <n-tab-pane name="docker-cuda" tab="NVIDIA CUDA">
                  <n-space vertical :size="8">
                    <n-text depth="3">需宿主机安装 NVIDIA 显卡驱动与 nvidia-container-toolkit:</n-text>
                    <n-code :code="dockerRunCuda" language="bash" word-wrap />
                  </n-space>
                </n-tab-pane>
                <n-tab-pane name="docker-rocm" tab="AMD ROCm">
                  <n-space vertical :size="8">
                    <n-text depth="3">挂载 /dev/kfd 与 /dev/dri，并分配 video 与 render 用户组:</n-text>
                    <n-code :code="dockerRunAmd" language="bash" word-wrap />
                  </n-space>
                </n-tab-pane>
                <n-tab-pane name="docker-intel" tab="Intel GPU">
                  <n-space vertical :size="8">
                    <n-text depth="3">挂载 DRI 接口，容器内已预装 Intel Level Zero 与 OpenCL 运行时:</n-text>
                    <n-code :code="dockerRunIntel" language="bash" word-wrap />
                  </n-space>
                </n-tab-pane>
                <n-tab-pane name="docker-cpu" tab="纯 CPU / 通用">
                  <n-space vertical :size="8">
                    <n-text depth="3">无需任何 GPU 依赖，任意 Linux x64 机器拉取即可运行:</n-text>
                    <n-code :code="dockerRunCpu" language="bash" word-wrap />
                  </n-space>
                </n-tab-pane>
              </n-tabs>
            </n-space>
          </n-card>

          <!-- 核心引擎与底层依赖 -->
          <n-card title="⚙️ 核心计算引擎与底层依赖架构">
            <n-grid :cols="3" :x-gap="12" :y-gap="12" responsive="screen">
              <n-grid-item>
                <n-card size="small" embedded title="ONNX Runtime 引擎">
                  <n-space vertical :size="6">
                    <n-tag type="primary" size="small">密集嵌入 (Embedding) & 交叉重排 (Rerank)</n-tag>
                    <n-text depth="2">承载 BGE-M3、BGE-large-zh、BGE-Reranker-large 等密集模型。自动匹配 DirectML / CUDA 12 / CPU，支持动态 batch 与高速并发推理。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
              <n-grid-item>
                <n-card size="small" embedded title="LLamaSharp 引擎">
                  <n-space vertical :size="6">
                    <n-tag type="info" size="small">GGUF 端侧大模型 (LLM)</n-tag>
                    <n-text depth="2">基于 llama.cpp 原生 C++ 内核。Windows 原生支持 Vulkan 跨显卡加速与 CUDA 12；Linux 原生支持 CUDA、ROCm 与 CPU，支持 4-bit / 8-bit 量化运行。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
              <n-grid-item>
                <n-card size="small" embedded title="SQLite Vector Store">
                  <n-space vertical :size="6">
                    <n-tag type="success" size="small">内嵌向量数据库 & BM25</n-tag>
                    <n-text depth="2">零外部数据库依赖！单文件自包含持久化，集成余弦距离向量索引与 FTS5 分词全文索引，无需搭建 PostgreSQL 或 Milvus，即开即用。</n-text>
                  </n-space>
                </n-card>
              </n-grid-item>
            </n-grid>
          </n-card>
        </n-space>
      </n-tab-pane>

      <!-- 模型与环境配置 -->
      <n-tab-pane name="env" tab="模型与环境配置">
        <n-space vertical :size="16">
          <n-card title="模型格式与运行环境要求">
            <n-space vertical>
              <n-alert type="info" title="运行说明">
                桌面版属于独立分发包，<strong>无需安装 .NET 运行时</strong>；若从源码二次开发或运行 WebHost 控制台版，则需要 .NET 10.0 SDK/Runtime。
              </n-alert>

              <n-descriptions label-placement="left" bordered :column="1">
                <n-descriptions-item label=".NET 运行环境">
                  <n-space align="center">
                    <n-tag type="success">.NET 10.0</n-tag>
                    <n-button text type="primary" tag="a" href="https://dotnet.microsoft.com/download/dotnet/10.0" target="_blank">
                      官方下载地址
                    </n-button>
                    <n-text depth="3">(桌面端发布包已内嵌，无需单独安装)</n-text>
                  </n-space>
                </n-descriptions-item>
                <n-descriptions-item label="模型存储位置">
                  <n-text>默认: <n-text code>~/.cache/huggingface/hub</n-text></n-text>
                  <n-text depth="3">可在「系统设置」或 appsettings.json 中自定义路径</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="支持的模型格式">
                  <n-space>
                    <n-tag type="info">ONNX (Embedding & Rerank)</n-tag>
                    <n-tag type="success">GGUF (本地端侧大语言模型)</n-tag>
                    <n-tag>SafeTensors</n-tag>
                  </n-space>
                </n-descriptions-item>
              </n-descriptions>

              <n-divider />

              <n-text strong>模型下载与加载步骤:</n-text>
              <n-steps vertical :current="0">
                <n-step title="进入模型管理" description="点击左侧菜单「模型管理」" />
                <n-step title="选择模型" description="在推荐模型列表中选择需要的 Embedding、Rerank 或 LLM 模型" />
                <n-step title="点击下载" description="点击「下载」按钮开始下载模型权重文件与分词配置" />
                <n-step title="切换模型" description="下载完成后点击「使用」即可无缝切换为当前检索或对话模型" />
              </n-steps>
            </n-space>
          </n-card>

          <!-- 显卡驱动与环境安装 -->
          <n-card title="CUDA 显卡驱动环境 (可选配置)">
            <n-space vertical>
              <n-alert type="warning" title="GPU 加速可选">
                通用版 (DirectML/Vulkan) 与 CPU 模式均无需单独安装 CUDA Toolkit。若您使用 NVIDIA 显卡并希望配置系统级开发环境，可参考以下命令：
              </n-alert>

              <n-collapse>
                <n-collapse-item title="Windows 环境检查" name="windows">
                  <n-space vertical>
                    <n-text strong>1. 检查 NVIDIA 显卡状态</n-text>
                    <n-code :code="'nvidia-smi'" language="bash" />
                    <n-text depth="3">正常显示驱动版本与 GPU 型号即说明驱动已正确安装</n-text>

                    <n-divider />

                    <n-text strong>2. CUDA Toolkit (开发环境可选)</n-text>
                    <n-space vertical>
                      <n-text>推荐版本: CUDA 12.x</n-text>
                      <n-button text type="primary" tag="a" href="https://developer.nvidia.com/cuda-downloads" target="_blank">
                        CUDA Toolkit 官方下载地址
                      </n-button>
                    </n-space>

                    <n-divider />

                    <n-text strong>3. 验证编译器</n-text>
                    <n-code :code="'nvcc --version'" language="bash" />
                  </n-space>
                </n-collapse-item>

                <n-collapse-item title="Linux 环境配置" name="linux">
                  <n-space vertical>
                    <n-text strong>1. 安装 NVIDIA 驱动</n-text>
                    <n-code :code="'sudo apt install nvidia-driver-535'" language="bash" />

                    <n-divider />

                    <n-text strong>2. 安装 CUDA Toolkit 12</n-text>
                    <n-code :code="linuxCudaInstall" language="bash" />

                    <n-divider />

                    <n-text strong>3. 配置环境变量</n-text>
                    <n-code :code="linuxCudaEnv" language="bash" />
                  </n-space>
                </n-collapse-item>
              </n-collapse>
            </n-space>
          </n-card>
        </n-space>
      </n-tab-pane>

      <!-- MCP 接口 -->
      <n-tab-pane name="mcp" tab="MCP 接口">
        <n-space vertical :size="16">
          <n-card title="MCP (Model Context Protocol) 接口使用">
            <n-space vertical>
              <n-alert type="info" title="什么是 MCP?">
                MCP 是一种标准化的模型上下文协议，允许 AI 助手（如 Claude Desktop、Cursor 等）与外部工具和服务进行交互
              </n-alert>

              <n-divider />

              <n-text strong>MCP 配置示例</n-text>
              <n-code :code="mcpConfigExample" language="json" word-wrap />

              <n-divider />

              <n-text strong>可用 MCP 工具</n-text>
              <n-table :bordered="false" :single-line="false">
                <thead>
                  <tr>
                    <th>工具名称</th>
                    <th>功能描述</th>
                    <th>参数</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td><n-text code>sources-list</n-text></td>
                    <td>列出所有数据源及索引统计</td>
                    <td>-</td>
                  </tr>
                  <tr>
                    <td><n-text code>sources-get-info</n-text></td>
                    <td>获取指定数据源详细信息</td>
                    <td>sourceName</td>
                  </tr>
                  <tr>
                    <td><n-text code>rag-semantic-search</n-text></td>
                    <td>向量语义搜索</td>
                    <td>query, topK, sources</td>
                  </tr>
                  <tr>
                    <td><n-text code>rag-hybrid-search</n-text></td>
                    <td>混合搜索（向量+BM25）</td>
                    <td>query, topK, k1, b, enableRerank</td>
                  </tr>
                  <tr>
                    <td><n-text code>rag-rerank-results</n-text></td>
                    <td>对候选文档重排</td>
                    <td>query, documents, topK</td>
                  </tr>
                  <tr>
                    <td><n-text code>embedding-encode-text</n-text></td>
                    <td>文本向量化</td>
                    <td>text</td>
                  </tr>
                  <tr>
                    <td><n-text code>embedding-calculate-similarity</n-text></td>
                    <td>计算文本相似度</td>
                    <td>text1, text2</td>
                  </tr>
                  <tr>
                    <td><n-text code>index-status</n-text></td>
                    <td>获取索引状态</td>
                    <td>-</td>
                  </tr>
                  <tr>
                    <td><n-text code>ping</n-text></td>
                    <td>测试 MCP 连通性</td>
                    <td>-</td>
                  </tr>
                </tbody>
              </n-table>

              <n-divider />

              <n-text strong>在 Claude Desktop 中使用</n-text>
              <n-collapse>
                <n-collapse-item title="配置步骤" name="claude-steps">
                  <n-steps vertical :current="0">
                    <n-step title="打开配置文件" description="Windows: %APPDATA%\Claude\claude_desktop_config.json" />
                    <n-step title="添加 MCP 服务器配置" description="将上述配置示例添加到 mcpServers 中" />
                    <n-step title="重启 Claude Desktop" description="完全关闭并重新打开 Claude Desktop" />
                    <n-step title="验证连接" description="在对话中询问 Claude 是否可以使用 ReferenceRAG 工具" />
                  </n-steps>
                </n-collapse-item>
              </n-collapse>
            </n-space>
          </n-card>

          <n-card title="MCP 使用示例">
            <n-tabs type="segment">
              <n-tab-pane name="search-example" tab="搜索示例">
                <n-space vertical>
                  <n-text>用户: "搜索关于向量数据库的内容"</n-text>
                  <n-divider style="margin: 8px 0" />
                  <n-text depth="3">Claude 调用 MCP 工具:</n-text>
                  <n-code :code="mcpSearchExample" language="json" />
                </n-space>
              </n-tab-pane>
              <n-tab-pane name="drilldown-example" tab="深入查询示例">
                <n-space vertical>
                  <n-text>用户: "展开这个结果的完整上下文"</n-text>
                  <n-divider style="margin: 8px 0" />
                  <n-text depth="3">Claude 调用 MCP 工具:</n-text>
                  <n-code :code="mcpDrilldownExample" language="json" />
                </n-space>
              </n-tab-pane>
            </n-tabs>
          </n-card>
        </n-space>
      </n-tab-pane>

      <!-- Agent Skills -->
      <n-tab-pane name="skills" tab="Agent Skills">
        <n-space vertical :size="16">
          <n-card title="Agent Skills 使用技巧">
            <n-space vertical>
              <n-alert type="info" title="什么是 Agent Skills?">
                Agent Skills 是一种将特定功能封装为可复用模块的方式，可以让 AI 助手获得特定领域的能力
              </n-alert>

              <n-divider />

              <n-text strong>ReferenceRAG Skill 示例</n-text>
              <n-code :code="skillExample" language="markdown" word-wrap />

              <n-divider />

              <n-text strong>Skill 安装方式</n-text>
              <n-steps vertical :current="0">
                <n-step title="复制 Skill 文件" description="将 skill 目录中的 SKILL.md 复制到 .cursor/skills 或 .claude/skills 目录" />
                <n-step title="配置服务地址（可选）" description="在 ~/.agents/.env 中设置 OBSIDIAN_RAG_API_URL" />
                <n-step title="重启 AI 助手" description="重新加载以识别新的 Skills" />
              </n-steps>
            </n-space>
          </n-card>

          <n-card title="Skill 触发方式">
            <n-space vertical>
              <n-text>ReferenceRAG Skill 支持以下触发方式:</n-text>

              <n-table :bordered="false" :single-line="false">
                <thead>
                  <tr>
                    <th>触发类型</th>
                    <th>示例</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>强制触发</td>
                    <td><n-text code>rag:关键词</n-text> 或 <n-text code>/rag 关键词</n-text></td>
                  </tr>
                  <tr>
                    <td>组合触发</td>
                    <td>领域词(知识库/笔记/vault/obsidian/文档) + 动作词(搜/查/找/检索/查询)</td>
                  </tr>
                  <tr>
                    <td>意图触发</td>
                    <td>笔记里有没有、帮我搜一下笔记、在知识库中查找</td>
                  </tr>
                </tbody>
              </n-table>
            </n-space>
          </n-card>

          <n-card title="自定义 Skill 模板">
            <n-space vertical>
              <n-text>创建自定义 Skill 的模板:</n-text>
              <n-code :code="skillTemplate" language="markdown" word-wrap />

              <n-space style="margin-top: 16px">
                <n-button type="primary" @click="copySkillTemplate">
                  <template #icon>
                    <n-icon><CopyOutline /></n-icon>
                  </template>
                  复制模板
                </n-button>
                <n-button @click="downloadSkillTemplate">下载模板文件</n-button>
              </n-space>
            </n-space>
          </n-card>
        </n-space>
      </n-tab-pane>

      <!-- 使用技巧 -->
      <n-tab-pane name="tips" tab="使用技巧">
        <n-space vertical :size="16">
          <n-card title="搜索技巧">
            <n-collapse>
              <n-collapse-item title="查询模式选择" name="query-mode">
                <n-space vertical :size="12">
                  <n-table :bordered="false" :single-line="false">
                    <thead>
                      <tr>
                        <th>模式</th>
                        <th>召回方式</th>
                        <th>重排</th>
                        <th>默认返回</th>
                        <th>适用场景</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td><n-tag type="default">快速</n-tag></td>
                        <td>纯向量搜索</td>
                        <td><n-tag size="small">无</n-tag></td>
                        <td>3 条</td>
                        <td>定向查询、快速试探</td>
                      </tr>
                      <tr>
                        <td><n-tag type="success">平衡</n-tag></td>
                        <td>BM25 + 向量混合</td>
                        <td><n-tag size="small" type="success">启用</n-tag></td>
                        <td>10 条</td>
                        <td>日常主力，准确率最高</td>
                      </tr>
                      <tr>
                        <td><n-tag type="warning">深度</n-tag></td>
                        <td>纯向量搜索</td>
                        <td><n-tag size="small">无</n-tag></td>
                        <td>20 条</td>
                        <td>探索性查询、答案位置不确定</td>
                      </tr>
                    </tbody>
                  </n-table>
                  <n-alert type="info" :show-icon="false">
                    <n-text strong>平衡</n-text> 模式会先用混合召回扩大候选池（TopK × 3），再由 Rerank 模型精排，关键词命中和语义相似度都考虑，综合效果最佳。<br />
                    <n-text strong>深度</n-text> 模式返回更多结果并扩大上下文窗口，适合"尽量多给"的探索场景，但不经过重排。
                  </n-alert>
                </n-space>
              </n-collapse-item>

              <n-collapse-item title="提高搜索质量的技巧" name="search-tips">
                <n-list>
                  <n-list-item>
                    <n-text strong>使用具体的关键词</n-text> - 避免过于笼统的查询词
                  </n-list-item>
                  <n-list-item>
                    <n-text strong>组合多个相关词</n-text> - 如 "向量数据库 索引 优化"
                  </n-list-item>
                  <n-list-item>
                    <n-text strong>使用专业术语</n-text> - BM25 模式对专业术语效果更好
                  </n-list-item>
                  <n-list-item>
                    <n-text strong>调整 topK 值</n-text> - 根据需要调整返回结果数量
                  </n-list-item>
                </n-list>
              </n-collapse-item>
            </n-collapse>
          </n-card>

          <n-card title="模型选择建议">
            <n-collapse>
              <n-collapse-item title="中文场景" name="chinese">
                <n-table :bordered="false" :single-line="false">
                  <thead>
                    <tr>
                      <th>模型</th>
                      <th>维度</th>
                      <th>推荐场景</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>BGE-small-zh</td>
                      <td>512</td>
                      <td>快速搜索、资源受限</td>
                    </tr>
                    <tr>
                      <td>BGE-large-zh</td>
                      <td>1024</td>
                      <td>高质量搜索</td>
                    </tr>
                    <tr>
                      <td>BGE-M3</td>
                      <td>1024</td>
                      <td>多语言、混合检索</td>
                    </tr>
                  </tbody>
                </n-table>
              </n-collapse-item>

              <n-collapse-item title="英文场景" name="english">
                <n-table :bordered="false" :single-line="false">
                  <thead>
                    <tr>
                      <th>模型</th>
                      <th>维度</th>
                      <th>推荐场景</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td>all-MiniLM</td>
                      <td>384</td>
                      <td>快速搜索</td>
                    </tr>
                    <tr>
                      <td>all-mpnet-base</td>
                      <td>768</td>
                      <td>高质量搜索</td>
                    </tr>
                  </tbody>
                </n-table>
              </n-collapse-item>
            </n-collapse>
          </n-card>

          <n-card title="性能优化建议">
            <n-list>
              <n-list-item>
                <n-text strong>使用 GPU 加速</n-text> - 启用 DirectML / Vulkan / CUDA 加速可数倍提升嵌入与重排计算速度
              </n-list-item>
              <n-list-item>
                <n-text strong>选择合适维度</n-text> - 维度越高质量越好但速度越慢
              </n-list-item>
              <n-list-item>
                <n-text strong>定期重建索引</n-text> - 文档批量变更后建议重建索引
              </n-list-item>
              <n-list-item>
                <n-text strong>使用重排模型</n-text> - Rerank 可显著提升搜索结果的相关性与 Top-1 准确率
              </n-list-item>
            </n-list>
          </n-card>

          <n-card title="日志排查">
            <n-space vertical>
              <n-alert type="info" title="排查问题的第一步">
                遇到报错先看日志。日志包含请求地址、响应内容与异常堆栈，多数问题可直接定位，无需猜测。
              </n-alert>

              <n-divider />

              <n-text strong>日志位置</n-text>
              <n-table :bordered="false" :single-line="false">
                <thead>
                  <tr><th>场景</th><th>路径</th></tr>
                </thead>
                <tbody>
                  <tr>
                    <td>默认</td>
                    <td><n-text code>{程序目录}/Logs/app_年月日.log</n-text></td>
                  </tr>
                  <tr>
                    <td>自定义</td>
                    <td>appsettings.json 中 <n-text code>Work:Directory</n-text> 指定的目录下 <n-text code>Logs/</n-text></td>
                  </tr>
                </tbody>
              </n-table>
              <n-text depth="3">按天滚动，自动保留最近 7 天。桌面端与 Web 端共用同一套日志配置。</n-text>

              <n-divider />

              <n-text strong>常用排查命令</n-text>
              <n-code :code=\"logCommands\" language=\"bash\" />

              <n-divider />

              <n-text strong>提高日志详细程度</n-text>
              <n-space vertical :size="8">
                <n-text>默认级别为 <n-text code>Information</n-text>。改为 <n-text code>Debug</n-text> 可看到请求地址、请求体、响应体等详细诊断信息：</n-text>
                <n-code :code=\"logLevelJson\" language=\"json\" />
                <n-alert type=\"warning\" :show-icon=\"false\">
                  排查完成后建议改回 <n-text code>Information</n-text>，避免日志体积快速增长。
                </n-alert>
              </n-space>

              <n-divider />

              <n-text strong>常见报错与日志线索</n-text>
              <n-table :bordered="false" :single-line="false">
                <thead>
                  <tr><th>现象</th><th>日志线索</th><th>处理</th></tr>
                </thead>
                <tbody>
                  <tr>
                    <td>嵌入/重排模型调用失败</td>
                    <td>查 <n-text code>[OpenAIEmbedding]</n-text> / <n-text code>[OpenAIRerank]</n-text>，会打印实际请求 URL 与响应原文</td>
                    <td>确认 <n-text code>apiBaseUrl</n-text> 以 <n-text code>/v1</n-text> 结尾</td>
                  </tr>
                  <tr>
                    <td>响应解析失败（根元素类型不符）</td>
                    <td>日志会写明“根元素类型”与响应片段</td>
                    <td>多为请求打到了非 OpenAI 兼容端点，核对地址</td>
                  </tr>
                  <tr>
                    <td>提示“数据目录已被另一个宿主使用”</td>
                    <td>启动即失败，含 <n-text code>DataDirectoryLease</n-text></td>
                    <td>关闭另一个 ReferenceRAG 进程，或为两端配置不同 <n-text code>dataPath</n-text></td>
                  </tr>
                  <tr>
                    <td>GPU 未生效、速度偏慢</td>
                    <td>查 <n-text code>CUDA 不可用，回退到 CPU</n-text></td>
                    <td>按“平台与硬件加速”检查显卡驱动或安装包选型</td>
                  </tr>
                  <tr>
                    <td>搜索无结果</td>
                    <td>查 <n-text code>[ModelProbe]</n-text> 的维度与健康值</td>
                    <td>确认索引已建立、模型可用</td>
                  </tr>
                </tbody>
              </n-table>

              <n-divider />

              <n-text strong>桌面端排障提示</n-text>
              <n-space vertical :size="8">
                <n-text depth="3">桌面端默认不监听网络端口。如需查看 Swagger 调试或供局域网访问，可在 appsettings.json 中开启后重启：</n-text>
                <n-code :code=\"desktopApiJson\" language=\"json\" />
                <n-alert type=\"warning\" :show-icon=\"false\">
                  仅供本机调试使用，这会让局域网内的其他程序也能访问该端口，排查完请关闭。
                </n-alert>
              </n-space>
            </n-space>
          </n-card>
        </n-space>
      </n-tab-pane>
    </n-tabs>
  </n-space>
`;

component.__scopeId = "data-v-318aced2";
export default component;
