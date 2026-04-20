<template>
  <n-space vertical :size="20">
    <!-- 顶部导航标签 -->
    <n-tabs type="line" animated>
      <n-tab-pane name="env" tab="环境安装">
        <n-space vertical :size="16">
          <!-- 模型下载依赖环境 -->
          <n-card title="模型下载依赖环境">
            <n-space vertical>
              <n-alert type="info" title="前置条件">
                模型下载需要 .NET 运行时和可选的 CUDA 支持（用于 GPU 加速）
              </n-alert>

              <n-descriptions label-placement="left" bordered :column="1">
                <n-descriptions-item label=".NET Runtime">
                  <n-space align="center">
                    <n-tag type="success">.NET 10.0</n-tag>
                    <n-button text type="primary" tag="a" href="https://dotnet.microsoft.com/download/dotnet/10.0" target="_blank">
                      下载地址
                    </n-button>
                  </n-space>
                </n-descriptions-item>
                <n-descriptions-item label="模型存储位置">
                  <n-text>默认: <n-text code>~/.cache/huggingface/hub</n-text></n-text>
                  <n-text depth="3">可在设置中自定义路径</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="支持的模型格式">
                  <n-space>
                    <n-tag>ONNX</n-tag>
                    <n-tag>GGUF</n-tag>
                    <n-tag>SafeTensors</n-tag>
                  </n-space>
                </n-descriptions-item>
              </n-descriptions>

              <n-divider />

              <n-text strong>模型下载步骤:</n-text>
              <n-steps vertical :current="0">
                <n-step title="进入模型管理" description="点击左侧菜单「模型管理」" />
                <n-step title="选择模型" description="在推荐模型列表中选择需要的模型" />
                <n-step title="点击下载" description="点击「下载」按钮开始下载模型文件" />
                <n-step title="切换模型" description="下载完成后点击「使用」切换到该模型" />
              </n-steps>
            </n-space>
          </n-card>

          <!-- CUDA 环境安装 -->
          <n-card title="CUDA 依赖环境安装">
            <n-space vertical>
              <n-alert type="warning" title="GPU 加速可选">
                CUDA 支持是可选的，CPU 模式下模型同样可以正常运行，但速度较慢
              </n-alert>

              <n-collapse>
                <n-collapse-item title="Windows 环境安装" name="windows">
                  <n-space vertical>
                    <n-text strong>1. 检查 NVIDIA 显卡</n-text>
                    <n-code :code="'nvidia-smi'" language="bash" />
                    <n-text depth="3">如果显示显卡信息，说明驱动已安装</n-text>

                    <n-divider />

                    <n-text strong>2. 安装 CUDA Toolkit</n-text>
                    <n-space vertical>
                      <n-text>推荐版本: CUDA 12.x</n-text>
                      <n-button text type="primary" tag="a" href="https://developer.nvidia.com/cuda-downloads" target="_blank">
                        CUDA Toolkit 下载地址
                      </n-button>
                    </n-space>

                    <n-divider />

                    <n-text strong>3. 安装 cuDNN</n-text>
                    <n-space vertical>
                      <n-text>需要与 CUDA 版本匹配的 cuDNN</n-text>
                      <n-button text type="primary" tag="a" href="https://developer.nvidia.com/cudnn" target="_blank">
                        cuDNN 下载地址
                      </n-button>
                    </n-space>

                    <n-divider />

                    <n-text strong>4. 验证安装</n-text>
                    <n-code :code="'nvcc --version'" language="bash" />
                  </n-space>
                </n-collapse-item>

                <n-collapse-item title="Linux 环境安装" name="linux">
                  <n-space vertical>
                    <n-text strong>1. 安装 NVIDIA 驱动</n-text>
                    <n-code :code="'sudo apt install nvidia-driver-535'" language="bash" />

                    <n-divider />

                    <n-text strong>2. 安装 CUDA Toolkit</n-text>
                    <n-code :code="`wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.0-1_all.deb
sudo dpkg -i cuda-keyring_1.0-1_all.deb
sudo apt update
sudo apt install cuda-toolkit-12-3`" language="bash" />

                    <n-divider />

                    <n-text strong>3. 配置环境变量</n-text>
                    <n-code :code="`echo 'export PATH=/usr/local/cuda/bin:$PATH' >> ~/.bashrc
echo 'export LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH' >> ~/.bashrc
source ~/.bashrc`" language="bash" />
                  </n-space>
                </n-collapse-item>
              </n-collapse>
            </n-space>
          </n-card>
        </n-space>
      </n-tab-pane>

      <n-tab-pane name="mcp" tab="MCP 接口">
        <n-space vertical :size="16">
          <n-card title="MCP (Model Context Protocol) 接口使用">
            <n-space vertical>
              <n-alert type="info" title="什么是 MCP?">
                MCP 是一种标准化的模型上下文协议，允许 AI 助手与外部工具和服务进行交互
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

      <n-tab-pane name="tips" tab="使用技巧">
        <n-space vertical :size="16">
          <n-card title="搜索技巧">
            <n-collapse>
              <n-collapse-item title="查询模式选择" name="query-mode">
                <n-table :bordered="false" :single-line="false">
                  <thead>
                    <tr>
                      <th>模式</th>
                      <th>适用场景</th>
                      <th>特点</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr>
                      <td><n-tag type="info">Vector</n-tag></td>
                      <td>语义相似度搜索</td>
                      <td>理解语义，适合概念性查询</td>
                    </tr>
                    <tr>
                      <td><n-tag type="success">BM25</n-tag></td>
                      <td>关键词精确匹配</td>
                      <td>精确匹配，适合专业术语</td>
                    </tr>
                    <tr>
                      <td><n-tag type="warning">Hybrid</n-tag></td>
                      <td>混合搜索（推荐）</td>
                      <td>结合两者优势，综合效果最佳</td>
                    </tr>
                  </tbody>
                </n-table>
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
                <n-text strong>使用 GPU 加速</n-text> - 安装 CUDA 可显著提升嵌入计算速度
              </n-list-item>
              <n-list-item>
                <n-text strong>选择合适维度</n-text> - 维度越高质量越好但速度越慢
              </n-list-item>
              <n-list-item>
                <n-text strong>定期重建索引</n-text> - 文档变更后建议重建索引
              </n-list-item>
              <n-list-item>
                <n-text strong>使用重排模型</n-text> - Rerank 可提升搜索结果质量
              </n-list-item>
            </n-list>
          </n-card>
        </n-space>
      </n-tab-pane>
    </n-tabs>
  </n-space>
</template>

<script setup lang="ts">
import { useMessage } from 'naive-ui'
import { CopyOutline } from '@vicons/ionicons5'

const message = useMessage()

// Skill 模板

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
}, null, 2)

const mcpSearchExample = JSON.stringify({
  tool: "rag-semantic-search",
  arguments: {
    query: "向量数据库",
    topK: 10,
    sources: []
  }
}, null, 2)

const mcpDrilldownExample = JSON.stringify({
  tool: "rag-hybrid-search",
  arguments: {
    query: "向量数据库",
    topK: 10,
    k1: 1.5,
    b: 0.75,
    enableRerank: false
  }
}, null, 2)

// Skill 示例（直接使用 skill/SKILL.md 内容）
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

**Rerank**：bge-reranker-base、bge-reranker-large`

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
\`\`\``

const copySkillTemplate = async () => {
  try {
    await navigator.clipboard.writeText(skillTemplate)
    message.success('Skill 模板已复制到剪贴板')
  } catch {
    message.error('复制失败')
  }
}

const downloadSkillTemplate = () => {
  const blob = new Blob([skillTemplate], { type: 'text/markdown' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = 'skill-template.md'
  a.click()
  URL.revokeObjectURL(url)
  message.success('模板文件已下载')
}
</script>

<style scoped>
:deep(.n-code) {
  max-height: 300px;
  overflow: auto;
}
</style>
