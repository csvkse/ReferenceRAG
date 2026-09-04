import { defineComponent as _defineComponent } from 'vue';
import { useMessage } from 'naive-ui';
import { CopyOutline } from '@vicons/ionicons5';
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
        // Skill 示例（直接使用 skill/SKILL.md 内容）
        const copySkillTemplate = async () => {
            try {
                await navigator.clipboard.writeText(skillTemplate);
                message.success('Skill 模板已复制到剪贴板');
            }
            catch {
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
        const __returned__ = { message, mcpConfigExample, mcpSearchExample, mcpDrilldownExample, skillExample, skillTemplate, copySkillTemplate, downloadSkillTemplate, get CopyOutline() { return CopyOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- 顶部导航标签 -->\n    <n-tabs type=\"line\" animated>\n      <n-tab-pane name=\"env\" tab=\"环境安装\">\n        <n-space vertical :size=\"16\">\n          <!-- 模型下载依赖环境 -->\n          <n-card title=\"模型下载依赖环境\">\n            <n-space vertical>\n              <n-alert type=\"info\" title=\"前置条件\">\n                模型下载需要 .NET 运行时和可选的 CUDA 支持（用于 GPU 加速）\n              </n-alert>\n\n              <n-descriptions label-placement=\"left\" bordered :column=\"1\">\n                <n-descriptions-item label=\".NET Runtime\">\n                  <n-space align=\"center\">\n                    <n-tag type=\"success\">.NET 10.0</n-tag>\n                    <n-button text type=\"primary\" tag=\"a\" href=\"https://dotnet.microsoft.com/download/dotnet/10.0\" target=\"_blank\">\n                      下载地址\n                    </n-button>\n                  </n-space>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"模型存储位置\">\n                  <n-text>默认: <n-text code>~/.cache/huggingface/hub</n-text></n-text>\n                  <n-text depth=\"3\">可在设置中自定义路径</n-text>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"支持的模型格式\">\n                  <n-space>\n                    <n-tag>ONNX</n-tag>\n                    <n-tag>GGUF</n-tag>\n                    <n-tag>SafeTensors</n-tag>\n                  </n-space>\n                </n-descriptions-item>\n              </n-descriptions>\n\n              <n-divider />\n\n              <n-text strong>模型下载步骤:</n-text>\n              <n-steps vertical :current=\"0\">\n                <n-step title=\"进入模型管理\" description=\"点击左侧菜单「模型管理」\" />\n                <n-step title=\"选择模型\" description=\"在推荐模型列表中选择需要的模型\" />\n                <n-step title=\"点击下载\" description=\"点击「下载」按钮开始下载模型文件\" />\n                <n-step title=\"切换模型\" description=\"下载完成后点击「使用」切换到该模型\" />\n              </n-steps>\n            </n-space>\n          </n-card>\n\n          <!-- CUDA 环境安装 -->\n          <n-card title=\"CUDA 依赖环境安装\">\n            <n-space vertical>\n              <n-alert type=\"warning\" title=\"GPU 加速可选\">\n                CUDA 支持是可选的，CPU 模式下模型同样可以正常运行，但速度较慢\n              </n-alert>\n\n              <n-collapse>\n                <n-collapse-item title=\"Windows 环境安装\" name=\"windows\">\n                  <n-space vertical>\n                    <n-text strong>1. 检查 NVIDIA 显卡</n-text>\n                    <n-code :code=\"'nvidia-smi'\" language=\"bash\" />\n                    <n-text depth=\"3\">如果显示显卡信息，说明驱动已安装</n-text>\n\n                    <n-divider />\n\n                    <n-text strong>2. 安装 CUDA Toolkit</n-text>\n                    <n-space vertical>\n                      <n-text>推荐版本: CUDA 12.x</n-text>\n                      <n-button text type=\"primary\" tag=\"a\" href=\"https://developer.nvidia.com/cuda-downloads\" target=\"_blank\">\n                        CUDA Toolkit 下载地址\n                      </n-button>\n                    </n-space>\n\n                    <n-divider />\n\n                    <n-text strong>3. 安装 cuDNN</n-text>\n                    <n-space vertical>\n                      <n-text>需要与 CUDA 版本匹配的 cuDNN</n-text>\n                      <n-button text type=\"primary\" tag=\"a\" href=\"https://developer.nvidia.com/cudnn\" target=\"_blank\">\n                        cuDNN 下载地址\n                      </n-button>\n                    </n-space>\n\n                    <n-divider />\n\n                    <n-text strong>4. 验证安装</n-text>\n                    <n-code :code=\"'nvcc --version'\" language=\"bash\" />\n                  </n-space>\n                </n-collapse-item>\n\n                <n-collapse-item title=\"Linux 环境安装\" name=\"linux\">\n                  <n-space vertical>\n                    <n-text strong>1. 安装 NVIDIA 驱动</n-text>\n                    <n-code :code=\"'sudo apt install nvidia-driver-535'\" language=\"bash\" />\n\n                    <n-divider />\n\n                    <n-text strong>2. 安装 CUDA Toolkit</n-text>\n                    <n-code :code=\"`wget https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2204/x86_64/cuda-keyring_1.0-1_all.deb\nsudo dpkg -i cuda-keyring_1.0-1_all.deb\nsudo apt update\nsudo apt install cuda-toolkit-12-3`\" language=\"bash\" />\n\n                    <n-divider />\n\n                    <n-text strong>3. 配置环境变量</n-text>\n                    <n-code :code=\"`echo 'export PATH=/usr/local/cuda/bin:$PATH' >> ~/.bashrc\necho 'export LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH' >> ~/.bashrc\nsource ~/.bashrc`\" language=\"bash\" />\n                  </n-space>\n                </n-collapse-item>\n              </n-collapse>\n            </n-space>\n          </n-card>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"mcp\" tab=\"MCP 接口\">\n        <n-space vertical :size=\"16\">\n          <n-card title=\"MCP (Model Context Protocol) 接口使用\">\n            <n-space vertical>\n              <n-alert type=\"info\" title=\"什么是 MCP?\">\n                MCP 是一种标准化的模型上下文协议，允许 AI 助手与外部工具和服务进行交互\n              </n-alert>\n\n              <n-divider />\n\n              <n-text strong>MCP 配置示例</n-text>\n              <n-code :code=\"mcpConfigExample\" language=\"json\" word-wrap />\n\n              <n-divider />\n\n              <n-text strong>可用 MCP 工具</n-text>\n              <n-table :bordered=\"false\" :single-line=\"false\">\n                <thead>\n                  <tr>\n                    <th>工具名称</th>\n                    <th>功能描述</th>\n                    <th>参数</th>\n                  </tr>\n                </thead>\n                <tbody>\n                  <tr>\n                    <td><n-text code>sources-list</n-text></td>\n                    <td>列出所有数据源及索引统计</td>\n                    <td>-</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>sources-get-info</n-text></td>\n                    <td>获取指定数据源详细信息</td>\n                    <td>sourceName</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>rag-semantic-search</n-text></td>\n                    <td>向量语义搜索</td>\n                    <td>query, topK, sources</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>rag-hybrid-search</n-text></td>\n                    <td>混合搜索（向量+BM25）</td>\n                    <td>query, topK, k1, b, enableRerank</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>rag-rerank-results</n-text></td>\n                    <td>对候选文档重排</td>\n                    <td>query, documents, topK</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>embedding-encode-text</n-text></td>\n                    <td>文本向量化</td>\n                    <td>text</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>embedding-calculate-similarity</n-text></td>\n                    <td>计算文本相似度</td>\n                    <td>text1, text2</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>index-status</n-text></td>\n                    <td>获取索引状态</td>\n                    <td>-</td>\n                  </tr>\n                  <tr>\n                    <td><n-text code>ping</n-text></td>\n                    <td>测试 MCP 连通性</td>\n                    <td>-</td>\n                  </tr>\n                </tbody>\n              </n-table>\n\n              <n-divider />\n\n              <n-text strong>在 Claude Desktop 中使用</n-text>\n              <n-collapse>\n                <n-collapse-item title=\"配置步骤\" name=\"claude-steps\">\n                  <n-steps vertical :current=\"0\">\n                    <n-step title=\"打开配置文件\" description=\"Windows: %APPDATA%\\Claude\\claude_desktop_config.json\" />\n                    <n-step title=\"添加 MCP 服务器配置\" description=\"将上述配置示例添加到 mcpServers 中\" />\n                    <n-step title=\"重启 Claude Desktop\" description=\"完全关闭并重新打开 Claude Desktop\" />\n                    <n-step title=\"验证连接\" description=\"在对话中询问 Claude 是否可以使用 ReferenceRAG 工具\" />\n                  </n-steps>\n                </n-collapse-item>\n              </n-collapse>\n            </n-space>\n          </n-card>\n\n          <n-card title=\"MCP 使用示例\">\n            <n-tabs type=\"segment\">\n              <n-tab-pane name=\"search-example\" tab=\"搜索示例\">\n                <n-space vertical>\n                  <n-text>用户: \"搜索关于向量数据库的内容\"</n-text>\n                  <n-divider style=\"margin: 8px 0\" />\n                  <n-text depth=\"3\">Claude 调用 MCP 工具:</n-text>\n                  <n-code :code=\"mcpSearchExample\" language=\"json\" />\n                </n-space>\n              </n-tab-pane>\n              <n-tab-pane name=\"drilldown-example\" tab=\"深入查询示例\">\n                <n-space vertical>\n                  <n-text>用户: \"展开这个结果的完整上下文\"</n-text>\n                  <n-divider style=\"margin: 8px 0\" />\n                  <n-text depth=\"3\">Claude 调用 MCP 工具:</n-text>\n                  <n-code :code=\"mcpDrilldownExample\" language=\"json\" />\n                </n-space>\n              </n-tab-pane>\n            </n-tabs>\n          </n-card>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"skills\" tab=\"Agent Skills\">\n        <n-space vertical :size=\"16\">\n          <n-card title=\"Agent Skills 使用技巧\">\n            <n-space vertical>\n              <n-alert type=\"info\" title=\"什么是 Agent Skills?\">\n                Agent Skills 是一种将特定功能封装为可复用模块的方式，可以让 AI 助手获得特定领域的能力\n              </n-alert>\n\n              <n-divider />\n\n              <n-text strong>ReferenceRAG Skill 示例</n-text>\n              <n-code :code=\"skillExample\" language=\"markdown\" word-wrap />\n\n              <n-divider />\n\n              <n-text strong>Skill 安装方式</n-text>\n              <n-steps vertical :current=\"0\">\n                <n-step title=\"复制 Skill 文件\" description=\"将 skill 目录中的 SKILL.md 复制到 .cursor/skills 或 .claude/skills 目录\" />\n                <n-step title=\"配置服务地址（可选）\" description=\"在 ~/.agents/.env 中设置 OBSIDIAN_RAG_API_URL\" />\n                <n-step title=\"重启 AI 助手\" description=\"重新加载以识别新的 Skills\" />\n              </n-steps>\n            </n-space>\n          </n-card>\n\n          <n-card title=\"Skill 触发方式\">\n            <n-space vertical>\n              <n-text>ReferenceRAG Skill 支持以下触发方式:</n-text>\n\n              <n-table :bordered=\"false\" :single-line=\"false\">\n                <thead>\n                  <tr>\n                    <th>触发类型</th>\n                    <th>示例</th>\n                  </tr>\n                </thead>\n                <tbody>\n                  <tr>\n                    <td>强制触发</td>\n                    <td><n-text code>rag:关键词</n-text> 或 <n-text code>/rag 关键词</n-text></td>\n                  </tr>\n                  <tr>\n                    <td>组合触发</td>\n                    <td>领域词(知识库/笔记/vault/obsidian/文档) + 动作词(搜/查/找/检索/查询)</td>\n                  </tr>\n                  <tr>\n                    <td>意图触发</td>\n                    <td>笔记里有没有、帮我搜一下笔记、在知识库中查找</td>\n                  </tr>\n                </tbody>\n              </n-table>\n            </n-space>\n          </n-card>\n\n          <n-card title=\"自定义 Skill 模板\">\n            <n-space vertical>\n              <n-text>创建自定义 Skill 的模板:</n-text>\n              <n-code :code=\"skillTemplate\" language=\"markdown\" word-wrap />\n\n              <n-space style=\"margin-top: 16px\">\n                <n-button type=\"primary\" @click=\"copySkillTemplate\">\n                  <template #icon>\n                    <n-icon><CopyOutline /></n-icon>\n                  </template>\n                  复制模板\n                </n-button>\n                <n-button @click=\"downloadSkillTemplate\">下载模板文件</n-button>\n              </n-space>\n            </n-space>\n          </n-card>\n        </n-space>\n      </n-tab-pane>\n\n      <n-tab-pane name=\"tips\" tab=\"使用技巧\">\n        <n-space vertical :size=\"16\">\n          <n-card title=\"搜索技巧\">\n            <n-collapse>\n              <n-collapse-item title=\"查询模式选择\" name=\"query-mode\">\n                <n-space vertical :size=\"12\">\n                  <n-table :bordered=\"false\" :single-line=\"false\">\n                    <thead>\n                      <tr>\n                        <th>模式</th>\n                        <th>召回方式</th>\n                        <th>重排</th>\n                        <th>默认返回</th>\n                        <th>适用场景</th>\n                      </tr>\n                    </thead>\n                    <tbody>\n                      <tr>\n                        <td><n-tag type=\"default\">快速</n-tag></td>\n                        <td>纯向量搜索</td>\n                        <td><n-tag size=\"small\">无</n-tag></td>\n                        <td>3 条</td>\n                        <td>定向查询、快速试探</td>\n                      </tr>\n                      <tr>\n                        <td><n-tag type=\"success\">平衡</n-tag></td>\n                        <td>BM25 + 向量混合</td>\n                        <td><n-tag size=\"small\" type=\"success\">启用</n-tag></td>\n                        <td>10 条</td>\n                        <td>日常主力，准确率最高</td>\n                      </tr>\n                      <tr>\n                        <td><n-tag type=\"warning\">深度</n-tag></td>\n                        <td>纯向量搜索</td>\n                        <td><n-tag size=\"small\">无</n-tag></td>\n                        <td>20 条</td>\n                        <td>探索性查询、答案位置不确定</td>\n                      </tr>\n                    </tbody>\n                  </n-table>\n                  <n-alert type=\"info\" :show-icon=\"false\">\n                    <n-text strong>平衡</n-text> 模式会先用混合召回扩大候选池（TopK × 3），再由 Rerank 模型精排，关键词命中和语义相似度都考虑，综合效果最佳。<br />\n                    <n-text strong>深度</n-text> 模式返回更多结果并扩大上下文窗口，适合\"尽量多给\"的探索场景，但不经过重排。\n                  </n-alert>\n                </n-space>\n              </n-collapse-item>\n\n              <n-collapse-item title=\"提高搜索质量的技巧\" name=\"search-tips\">\n                <n-list>\n                  <n-list-item>\n                    <n-text strong>使用具体的关键词</n-text> - 避免过于笼统的查询词\n                  </n-list-item>\n                  <n-list-item>\n                    <n-text strong>组合多个相关词</n-text> - 如 \"向量数据库 索引 优化\"\n                  </n-list-item>\n                  <n-list-item>\n                    <n-text strong>使用专业术语</n-text> - BM25 模式对专业术语效果更好\n                  </n-list-item>\n                  <n-list-item>\n                    <n-text strong>调整 topK 值</n-text> - 根据需要调整返回结果数量\n                  </n-list-item>\n                </n-list>\n              </n-collapse-item>\n            </n-collapse>\n          </n-card>\n\n          <n-card title=\"模型选择建议\">\n            <n-collapse>\n              <n-collapse-item title=\"中文场景\" name=\"chinese\">\n                <n-table :bordered=\"false\" :single-line=\"false\">\n                  <thead>\n                    <tr>\n                      <th>模型</th>\n                      <th>维度</th>\n                      <th>推荐场景</th>\n                    </tr>\n                  </thead>\n                  <tbody>\n                    <tr>\n                      <td>BGE-small-zh</td>\n                      <td>512</td>\n                      <td>快速搜索、资源受限</td>\n                    </tr>\n                    <tr>\n                      <td>BGE-large-zh</td>\n                      <td>1024</td>\n                      <td>高质量搜索</td>\n                    </tr>\n                    <tr>\n                      <td>BGE-M3</td>\n                      <td>1024</td>\n                      <td>多语言、混合检索</td>\n                    </tr>\n                  </tbody>\n                </n-table>\n              </n-collapse-item>\n\n              <n-collapse-item title=\"英文场景\" name=\"english\">\n                <n-table :bordered=\"false\" :single-line=\"false\">\n                  <thead>\n                    <tr>\n                      <th>模型</th>\n                      <th>维度</th>\n                      <th>推荐场景</th>\n                    </tr>\n                  </thead>\n                  <tbody>\n                    <tr>\n                      <td>all-MiniLM</td>\n                      <td>384</td>\n                      <td>快速搜索</td>\n                    </tr>\n                    <tr>\n                      <td>all-mpnet-base</td>\n                      <td>768</td>\n                      <td>高质量搜索</td>\n                    </tr>\n                  </tbody>\n                </n-table>\n              </n-collapse-item>\n            </n-collapse>\n          </n-card>\n\n          <n-card title=\"性能优化建议\">\n            <n-list>\n              <n-list-item>\n                <n-text strong>使用 GPU 加速</n-text> - 安装 CUDA 可显著提升嵌入计算速度\n              </n-list-item>\n              <n-list-item>\n                <n-text strong>选择合适维度</n-text> - 维度越高质量越好但速度越慢\n              </n-list-item>\n              <n-list-item>\n                <n-text strong>定期重建索引</n-text> - 文档变更后建议重建索引\n              </n-list-item>\n              <n-list-item>\n                <n-text strong>使用重排模型</n-text> - Rerank 可提升搜索结果质量\n              </n-list-item>\n            </n-list>\n          </n-card>\n        </n-space>\n      </n-tab-pane>\n    </n-tabs>\n  </n-space>\n";
component.__scopeId = "data-v-318aced2";
export default component;
