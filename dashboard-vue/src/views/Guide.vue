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

          <!-- 一键安装 N 卡环境 -->
          <n-card title="一键安装 NVIDIA 环境">
            <n-space vertical>
              <n-alert type="info">
                此功能自动检测并安装 CUDA、cuDNN 等 GPU 加速所需依赖
              </n-alert>

              <n-form label-placement="left" label-width="100">
                <n-form-item label="安装目录">
                  <n-input-group>
                    <n-input v-model:value="cudaInstallDir" placeholder="默认: C:\CUDA" style="width: 300px" />
                    <n-button @click="selectInstallDir">选择目录</n-button>
                  </n-input-group>
                </n-form-item>
                <n-form-item label="CUDA 版本">
                  <n-select v-model:value="cudaVersion" :options="cudaVersionOptions" style="width: 200px" />
                </n-form-item>
                <n-form-item label="包含 cuDNN">
                  <n-switch v-model:value="includeCudnn" />
                </n-form-item>
              </n-form>

              <n-space>
                <n-button type="primary" :loading="installing" @click="installCuda">
                  <template #icon>
                    <n-icon><DownloadOutline /></n-icon>
                  </template>
                  开始安装
                </n-button>
                <n-button @click="checkCudaEnv">检测当前环境</n-button>
              </n-space>

              <n-card v-if="envCheckResult" title="环境检测结果" size="small">
                <n-descriptions label-placement="left" bordered :column="1">
                  <n-descriptions-item label="NVIDIA 驱动">
                    <n-tag :type="envCheckResult.driver ? 'success' : 'error'">
                      {{ envCheckResult.driver ? '已安装' : '未安装' }}
                    </n-tag>
                    <n-text v-if="envCheckResult.driverVersion" depth="3" style="margin-left: 8px">
                      版本: {{ envCheckResult.driverVersion }}
                    </n-text>
                  </n-descriptions-item>
                  <n-descriptions-item label="CUDA Toolkit">
                    <n-tag :type="envCheckResult.cuda ? 'success' : 'error'">
                      {{ envCheckResult.cuda ? '已安装' : '未安装' }}
                    </n-tag>
                    <n-text v-if="envCheckResult.cudaVersion" depth="3" style="margin-left: 8px">
                      版本: {{ envCheckResult.cudaVersion }}
                    </n-text>
                  </n-descriptions-item>
                  <n-descriptions-item label="cuDNN">
                    <n-tag :type="envCheckResult.cudnn ? 'success' : 'warning'">
                      {{ envCheckResult.cudnn ? '已安装' : '未安装' }}
                    </n-tag>
                  </n-descriptions-item>
                  <n-descriptions-item label="GPU 设备">
                    <n-text v-if="envCheckResult.gpuName">{{ envCheckResult.gpuName }}</n-text>
                    <n-text v-else depth="3">未检测到</n-text>
                  </n-descriptions-item>
                </n-descriptions>
              </n-card>

              <n-progress
                v-if="installProgress > 0"
                type="line"
                :percentage="installProgress"
                :status="installProgress === 100 ? 'success' : 'default'"
                :show-indicator="true"
              />
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
                    <td><n-text code>search</n-text></td>
                    <td>语义搜索知识库</td>
                    <td>query, topK, mode</td>
                  </tr>
                  <tr>
                    <td><n-text code>drilldown</n-text></td>
                    <td>深入展开上下文</td>
                    <td>refIds, expandContext</td>
                  </tr>
                  <tr>
                    <td><n-text code>get_sources</n-text></td>
                    <td>获取所有数据源</td>
                    <td>-</td>
                  </tr>
                  <tr>
                    <td><n-text code>get_models</n-text></td>
                    <td>获取可用模型列表</td>
                    <td>-</td>
                  </tr>
                  <tr>
                    <td><n-text code>switch_model</n-text></td>
                    <td>切换嵌入模型</td>
                    <td>modelName</td>
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
                <n-step title="创建 Skills 目录" description="在项目中创建 .cursor/skills 或 .claude/skills 目录" />
                <n-step title="复制 Skill 文件" description="将 .md 格式的 Skill 文件放入目录" />
                <n-step title="重启 AI 助手" description="重新加载以识别新的 Skills" />
              </n-steps>
            </n-space>
          </n-card>

          <n-card title="推荐 Skills 配置">
            <n-space vertical>
              <n-text>以下是与 ReferenceRAG 配合使用的推荐 Skills:</n-text>

              <n-table :bordered="false" :single-line="false">
                <thead>
                  <tr>
                    <th>Skill 名称</th>
                    <th>用途</th>
                    <th>触发词</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>ReferenceRAG Search</td>
                    <td>知识库语义搜索</td>
                    <td><n-text code>搜索知识库</n-text>, <n-text code>查找文档</n-text></td>
                  </tr>
                  <tr>
                    <td>ReferenceRAG Index</td>
                    <td>触发索引更新</td>
                    <td><n-text code>更新索引</n-text>, <n-text code>重建索引</n-text></td>
                  </tr>
                  <tr>
                    <td>ReferenceRAG Models</td>
                    <td>模型管理操作</td>
                    <td><n-text code>切换模型</n-text>, <n-text code>下载模型</n-text></td>
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
import { ref } from 'vue'
import { useMessage } from 'naive-ui'
import { DownloadOutline, CopyOutline } from '@vicons/ionicons5'

const message = useMessage()

// CUDA 安装配置
const cudaInstallDir = ref('')
const cudaVersion = ref('12.3')
const includeCudnn = ref(true)
const installing = ref(false)
const installProgress = ref(0)

const cudaVersionOptions = [
  { label: 'CUDA 12.3', value: '12.3' },
  { label: 'CUDA 12.2', value: '12.2' },
  { label: 'CUDA 12.1', value: '12.1' },
  { label: 'CUDA 11.8', value: '11.8' }
]

interface EnvCheckResult {
  driver: boolean
  driverVersion?: string
  cuda: boolean
  cudaVersion?: string
  cudnn: boolean
  gpuName?: string
}

const envCheckResult = ref<EnvCheckResult | null>(null)

const selectInstallDir = () => {
  // 在实际实现中打开目录选择对话框
  message.info('目录选择功能需要后端支持')
}

const installCuda = async () => {
  installing.value = true
  installProgress.value = 0

  // 模拟安装过程
  for (let i = 0; i <= 100; i += 10) {
    await new Promise(resolve => setTimeout(resolve, 500))
    installProgress.value = i
  }

  installing.value = false
  message.success('CUDA 环境安装完成')
}

const checkCudaEnv = async () => {
  // 模拟检测结果
  envCheckResult.value = {
    driver: true,
    driverVersion: '535.154.05',
    cuda: true,
    cudaVersion: '12.3',
    cudnn: true,
    gpuName: 'NVIDIA GeForce RTX 3080'
  }
  message.success('环境检测完成')
}

// MCP 配置示例
const mcpConfigExample = JSON.stringify({
  mcpServers: {
    referencerag: {
      command: "node",
      args: ["path/to/referencerag-mcp/dist/index.js"],
      env: {
        REFERENCERAG_API_URL: "http://localhost:5000"
      }
    }
  }
}, null, 2)

const mcpSearchExample = JSON.stringify({
  tool: "search",
  arguments: {
    query: "向量数据库",
    topK: 10,
    mode: "Hybrid"
  }
}, null, 2)

const mcpDrilldownExample = JSON.stringify({
  tool: "drilldown",
  arguments: {
    refIds: ["chunk-001", "chunk-002"],
    expandContext: 2
  }
}, null, 2)

// Skill 示例
const skillExample = `# ReferenceRAG Search Skill

搜索 Obsidian 知识库中的内容。

## 触发条件
- 用户提到 "搜索知识库"
- 用户提到 "查找文档"
- 用户询问具体的技术问题

## 使用方式
调用 MCP 工具 \`search\` 进行语义搜索。

## 参数
- query: 搜索查询词
- topK: 返回结果数量 (默认: 10)
- mode: 搜索模式 (Vector/BM25/Hybrid)`

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
