import { defineComponent as _defineComponent } from 'vue';
import { ref, onMounted, onUnmounted, h } from 'vue';
import { useRouter } from 'vue-router';
import { NTag, NButton, useMessage, useDialog } from 'naive-ui';
import { BookOutline, CubeOutline, FolderOutline, GridOutline, LayersOutline, PlayOutline, TrashOutline, RefreshOutline, AddOutline, SearchOutline, SettingsOutline } from '@vicons/ionicons5';
import { knowledgeBasesApi, modelsApi } from '/app/api/index.js';

const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'KnowledgeBases' },
    __name: 'KnowledgeBases',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const message = useMessage();
        const dialog = useDialog();

        // 状态列表
        const loading = ref(false);
        const knowledgeBases = ref([]);
        const connectorsMap = ref({});
        const embeddingSpecs = ref([]);

        // 新建向导模态框状态
        const showCreateModal = ref(false);
        const creating = ref(false);
        const fetchingModels = ref(false);
        const probingDimension = ref(false);
        const remoteModelOptions = ref([]);

        const createForm = ref({
            name: '',
            description: '',
            bindingMode: 'preset', // 'preset' | 'online'
            presetSpecId: 'bge-m3-default',
            onlineProvider: 'siliconflow',
            onlineBaseUrl: 'https://api.siliconflow.cn/v1',
            onlineApiKey: '',
            onlineModelName: 'BAAI/bge-m3',
            detectedDimension: 1024,
            initialFolder: '',
            initialPattern: '*.md,*.txt,*.pdf',
            enableDualChannel: false,
            ingestionBackend: 'OpenAiApi',
            searchBackend: 'LocalOnnx'
        });

        // 连接器抽屉状态
        const showConnectorDrawer = ref(false);
        const currentKb = ref(null);
        const currentConnectors = ref([]);
        const loadingConnectors = ref(false);
        const newConnectorPath = ref('');
        const newConnectorPattern = ref('*.md,*.txt,*.pdf');

        // 加载所有知识库及连接器
        const loadKnowledgeBases = async () => {
            loading.value = true;
            try {
                const res = await knowledgeBasesApi.getAll();
                knowledgeBases.value = res.data || [];
                
                // 并发获取各库连接器信息
                const map = {};
                await Promise.all(knowledgeBases.value.map(async (kb) => {
                    try {
                        const connRes = await knowledgeBasesApi.getConnectors(kb.id);
                        map[kb.id] = connRes.data || [];
                    } catch (e) {
                        map[kb.id] = [];
                    }
                }));
                connectorsMap.value = map;
            } catch (err) {
                console.error('加载知识库失败:', err);
                message.error('加载知识库失败: ' + (err.response?.data?.message || err.message));
            } finally {
                loading.value = false;
            }
        };

        // 加载预设模型规范
        const loadEmbeddingSpecs = async () => {
            try {
                const res = await knowledgeBasesApi.getEmbeddingSpecs();
                embeddingSpecs.value = res.data || [];
            } catch (err) {
                console.warn('获取预设 EmbeddingSpecs 失败:', err);
            }
        };

        // 切换线上服务商快捷地址
        const handleProviderChange = (provider) => {
            createForm.value.onlineProvider = provider;
            if (provider === 'siliconflow') {
                createForm.value.onlineBaseUrl = 'https://api.siliconflow.cn/v1';
                createForm.value.onlineModelName = 'BAAI/bge-m3';
                createForm.value.detectedDimension = 1024;
            } else if (provider === 'ollama') {
                createForm.value.onlineBaseUrl = 'http://localhost:11434/v1';
                createForm.value.onlineModelName = 'bge-m3';
                createForm.value.detectedDimension = 1024;
            } else if (provider === 'openai') {
                createForm.value.onlineBaseUrl = 'https://api.openai.com/v1';
                createForm.value.onlineModelName = 'text-embedding-3-small';
                createForm.value.detectedDimension = 1536;
            } else if (provider === 'deepseek') {
                createForm.value.onlineBaseUrl = 'https://api.deepseek.com/v1';
                createForm.value.onlineModelName = 'deepseek-chat';
                createForm.value.detectedDimension = 1024;
            }
        };

        // 动态拉取远程模型列表
        const handleDiscoverRemoteModels = async () => {
            if (!createForm.value.onlineBaseUrl) {
                message.warning('请先填写 API 基础地址');
                return;
            }
            fetchingModels.value = true;
            try {
                const res = await modelsApi.discoverRemoteModels({
                    baseUrl: createForm.value.onlineBaseUrl,
                    apiKey: createForm.value.onlineApiKey
                });
                const list = res.data?.models || [];
                remoteModelOptions.value = list.map(m => ({ label: m, value: m }));
                message.success(`成功拉取到 ${list.length} 个远端模型`);
                if (list.length > 0 && !createForm.value.onlineModelName) {
                    createForm.value.onlineModelName = list[0];
                }
            } catch (err) {
                console.error('拉取远端模型列表失败:', err);
                message.error('拉取模型失败: ' + (err.response?.data?.error || err.message));
            } finally {
                fetchingModels.value = false;
            }
        };

        // 自动探测向量维度
        const handleProbeDimension = async () => {
            if (!createForm.value.onlineBaseUrl || !createForm.value.onlineModelName) {
                message.warning('请先指定 API 地址与模型名称');
                return;
            }
            probingDimension.value = true;
            try {
                const res = await modelsApi.probeDimension({
                    baseUrl: createForm.value.onlineBaseUrl,
                    modelName: createForm.value.onlineModelName,
                    apiKey: createForm.value.onlineApiKey
                });
                if (res.data?.success && res.data.dimension > 0) {
                    createForm.value.detectedDimension = res.data.dimension;
                    message.success(`探针测试成功！已精准测得向量维度为: ${res.data.dimension} 维`);
                } else {
                    message.warning('探针未返回有效维度');
                }
            } catch (err) {
                console.error('探测维度失败:', err);
                message.error('探测失败: ' + (err.response?.data?.error || err.message));
            } finally {
                probingDimension.value = false;
            }
        };

        // 提交新建知识库
        const handleCreateSubmit = async () => {
            if (!createForm.value.name.trim()) {
                message.warning('知识库名称不能为空');
                return;
            }

            creating.value = true;
            try {
                let payload = {
                    name: createForm.value.name.trim(),
                    description: createForm.value.description.trim()
                };

                if (createForm.value.bindingMode === 'preset') {
                    payload.embeddingSpecId = createForm.value.presetSpecId;
                } else {
                    // 自定义/线上 API 模型
                    payload.embeddingSpec = {
                        id: `spec_${Date.now()}`,
                        modelName: createForm.value.onlineModelName.trim(),
                        dimension: createForm.value.detectedDimension || 1024,
                        metric: 'cosine',
                        enableDualChannel: createForm.value.enableDualChannel,
                        ingestionBackend: createForm.value.enableDualChannel ? 'OpenAiApi' : null,
                        ingestionBaseUrl: createForm.value.onlineBaseUrl,
                        ingestionApiKey: createForm.value.onlineApiKey,
                        searchBackend: createForm.value.enableDualChannel ? 'LocalOnnx' : null
                    };
                }

                const res = await knowledgeBasesApi.create(payload);
                const newKb = res.data;
                message.success(`知识库「${newKb.name}」创建成功`);

                // 若指定了初始本地文件夹，自动绑定连接器
                if (createForm.value.initialFolder.trim()) {
                    try {
                        await knowledgeBasesApi.addConnector(newKb.id, {
                            kind: 'Folder',
                            targetPath: createForm.value.initialFolder.trim(),
                            filePattern: createForm.value.initialPattern.trim() || null
                        });
                        message.info('初始数据目录已成功挂载为连接器');
                    } catch (connErr) {
                        console.warn('挂载初始连接器失败:', connErr);
                    }
                }

                showCreateModal.value = false;
                // 重置表单
                createForm.value.name = '';
                createForm.value.description = '';
                createForm.value.initialFolder = '';
                await loadKnowledgeBases();
            } catch (err) {
                console.error('创建知识库失败:', err);
                message.error('创建失败: ' + (err.response?.data?.message || err.message));
            } finally {
                creating.value = false;
            }
        };

        // 打开连接器抽屉
        const openConnectorsDrawer = async (kb) => {
            currentKb.value = kb;
            showConnectorDrawer.value = true;
            newConnectorPath.value = '';
            await loadCurrentConnectors();
        };

        const loadCurrentConnectors = async () => {
            if (!currentKb.value) return;
            loadingConnectors.value = true;
            try {
                const res = await knowledgeBasesApi.getConnectors(currentKb.value.id);
                currentConnectors.value = res.data || [];
            } catch (err) {
                message.error('加载连接器列表失败');
            } finally {
                loadingConnectors.value = false;
            }
        };

        // 添加连接器
        const handleAddConnector = async () => {
            if (!newConnectorPath.value.trim()) {
                message.warning('请输入目标文件夹路径');
                return;
            }
            try {
                await knowledgeBasesApi.addConnector(currentKb.value.id, {
                    kind: 'Folder',
                    targetPath: newConnectorPath.value.trim(),
                    filePattern: newConnectorPattern.value.trim() || null
                });
                message.success('已成功挂载连接器');
                newConnectorPath.value = '';
                await loadCurrentConnectors();
                await loadKnowledgeBases();
            } catch (err) {
                message.error('添加连接器失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // 删除连接器
        const handleDeleteConnector = async (connectorId) => {
            try {
                await knowledgeBasesApi.deleteConnector(currentKb.value.id, connectorId);
                message.success('连接器已解除挂载');
                await loadCurrentConnectors();
                await loadKnowledgeBases();
            } catch (err) {
                message.error('删除连接器失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // 触发同步作业
        const handleTriggerSync = async (kbId, connectorId) => {
            try {
                const res = await knowledgeBasesApi.triggerSync(kbId, connectorId);
                message.success('已触发异步同步任务: ' + res.data.id);
            } catch (err) {
                message.error('触发同步失败: ' + (err.response?.data?.message || err.message));
            }
        };

        // 删除知识库
        const handleDeleteKnowledgeBase = (kb) => {
            dialog.warning({
                title: '确认删除知识库',
                content: `确定要删除知识库「${kb.name}」吗？这将彻底清理其物理存储数据库(kb.db)与全部索引。`,
                positiveText: '确认删除',
                negativeText: '取消',
                onPositiveClick: async () => {
                    try {
                        await knowledgeBasesApi.delete(kb.id);
                        message.success(`知识库「${kb.name}」已安全删除`);
                        await loadKnowledgeBases();
                    } catch (err) {
                        message.error('删除知识库失败: ' + (err.response?.data?.message || err.message));
                    }
                }
            });
        };

        onMounted(async () => {
            await Promise.all([loadKnowledgeBases(), loadEmbeddingSpecs()]);
        });

        const __returned__ = {
            router, message, dialog, loading, knowledgeBases, connectorsMap, embeddingSpecs,
            showCreateModal, creating, fetchingModels, probingDimension, remoteModelOptions, createForm,
            showConnectorDrawer, currentKb, currentConnectors, loadingConnectors, newConnectorPath, newConnectorPattern,
            loadKnowledgeBases, loadEmbeddingSpecs, handleProviderChange, handleDiscoverRemoteModels, handleProbeDimension,
            handleCreateSubmit, openConnectorsDrawer, loadCurrentConnectors, handleAddConnector, handleDeleteConnector,
            handleTriggerSync, handleDeleteKnowledgeBase,
            get NTag() { return NTag; },
            get NButton() { return NButton; },
            get BookOutline() { return BookOutline; },
            get CubeOutline() { return CubeOutline; },
            get FolderOutline() { return FolderOutline; },
            get GridOutline() { return GridOutline; },
            get LayersOutline() { return LayersOutline; },
            get PlayOutline() { return PlayOutline; },
            get TrashOutline() { return TrashOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get AddOutline() { return AddOutline; },
            get SearchOutline() { return SearchOutline; },
            get SettingsOutline() { return SettingsOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <!-- 顶部概览与操作栏 -->
    <n-card>
      <n-page-header title="知识库管理 (Physical Knowledge Bases)" subtitle="物理单库隔离 (kb.db) 架构，支持按库独立绑定本地/线上模型，彻底告别单库维度混杂">
        <template #extra>
          <n-space>
            <n-button @click="loadKnowledgeBases" :loading="loading">
              <template #icon><n-icon :component="RefreshOutline" /></template>
              刷新
            </n-button>
            <n-button type="primary" @click="showCreateModal = true">
              <template #icon><n-icon :component="AddOutline" /></template>
              新建知识库
            </n-button>
          </n-space>
        </template>
      </n-page-header>
    </n-card>

    <!-- 知识库卡片网格 -->
    <n-spin :show="loading">
      <n-grid :cols="3" :x-gap="16" :y-gap="16">
        <n-gi v-for="kb in knowledgeBases" :key="kb.id">
          <n-card :title="kb.name" hoverable>
            <template #header-extra>
              <n-tag :type="kb.enabled ? 'success' : 'default'" size="small">
                {{ kb.enabled ? '已就绪' : '停用' }}
              </n-tag>
            </template>

            <n-space vertical :size="12">
              <n-text depth="3">{{ kb.description || '暂无描述' }}</n-text>

              <n-descriptions :column="1" size="small" label-placement="left">
                <n-descriptions-item label="物理存储">
                  <n-text code>data/kbs/{{ kb.id }}/kb.db</n-text>
                </n-descriptions-item>
                <n-descriptions-item label="绑定模型">
                  <n-tag type="info" size="small">
                    <template #icon><n-icon :component="CubeOutline" /></template>
                    {{ kb.embeddingSpecId }}
                  </n-tag>
                </n-descriptions-item>
                <n-descriptions-item label="连接器通道">
                  <n-text depth="2">{{ (connectorsMap[kb.id] || []).length }} 个数据源挂载</n-text>
                </n-descriptions-item>
              </n-descriptions>
            </n-space>

            <template #action>
              <n-space justify="space-between">
                <n-button size="small" secondary @click="openConnectorsDrawer(kb)">
                  <template #icon><n-icon :component="FolderOutline" /></template>
                  数据连接器
                </n-button>
                <n-space>
                  <n-button size="small" quaternary type="error" @click="handleDeleteKnowledgeBase(kb)">
                    <template #icon><n-icon :component="TrashOutline" /></template>
                    删除
                  </n-button>
                </n-space>
              </n-space>
            </template>
          </n-card>
        </n-gi>
      </n-grid>

      <n-empty v-if="knowledgeBases.length === 0 && !loading" description="暂无知识库，请点击上方「新建知识库」创建首个物理隔离知识库" style="margin: 40px 0;">
        <template #extra>
          <n-button type="primary" @click="showCreateModal = true">
            <template #icon><n-icon :component="AddOutline" /></template>
            立即创建
          </n-button>
        </template>
      </n-empty>
    </n-spin>

    <!-- 新建知识库向导模态框 -->
    <n-modal v-model:show="showCreateModal" preset="card" title="新建知识库 (New Knowledge Base)" style="width: 680px;" :mask-closable="false">
      <n-form label-placement="top">
        <n-form-item label="知识库名称" required>
          <n-input v-model:value="createForm.name" placeholder="例如：企业核心技术文档库 / 法律合同审查库" />
        </n-form-item>

        <n-form-item label="知识库描述">
          <n-input v-model:value="createForm.description" type="textarea" placeholder="描述此知识库的数据范畴与主要用途" :rows="2" />
        </n-form-item>

        <n-divider title-placement="left">嵌入模型绑定 (不可变规格契约)</n-divider>

        <n-form-item label="模型来源模式">
          <n-radio-group v-model:value="createForm.bindingMode" name="bindingMode">
            <n-radio-button value="preset">本地/系统已有预设规范</n-radio-button>
            <n-radio-button value="online">线上 API 模型提供商 (SiliconFlow/Ollama/OpenAI等)</n-radio-button>
          </n-radio-group>
        </n-form-item>

        <!-- 模式一：选择预设模型规范 -->
        <div v-if="createForm.bindingMode === 'preset'">
          <n-form-item label="选择模型规格 (EmbeddingSpec)">
            <n-select
              v-model:value="createForm.presetSpecId"
              :options="embeddingSpecs.map(s => ({ label: s.name ? (s.name + ' (' + s.dimension + 'd)') : (s.modelName + ' (' + s.dimension + 'd) [' + s.id + ']'), value: s.id }))"
              placeholder="选择绑定的模型规范"
            />
          </n-form-item>
        </div>

        <!-- 模式二：配置线上 API 提供商与模型列表 -->
        <div v-else>
          <n-form-item label="选择 API 提供商快捷预设">
            <n-space>
              <n-button size="small" :type="createForm.onlineProvider === 'siliconflow' ? 'primary' : 'default'" @click="handleProviderChange('siliconflow')">
                SiliconFlow 硅基流动
              </n-button>
              <n-button size="small" :type="createForm.onlineProvider === 'ollama' ? 'primary' : 'default'" @click="handleProviderChange('ollama')">
                本地 Ollama
              </n-button>
              <n-button size="small" :type="createForm.onlineProvider === 'openai' ? 'primary' : 'default'" @click="handleProviderChange('openai')">
                OpenAI 官方
              </n-button>
              <n-button size="small" :type="createForm.onlineProvider === 'deepseek' ? 'primary' : 'default'" @click="handleProviderChange('deepseek')">
                DeepSeek
              </n-button>
              <n-button size="small" :type="createForm.onlineProvider === 'custom' ? 'primary' : 'default'" @click="handleProviderChange('custom')">
                自定义端点
              </n-button>
            </n-space>
          </n-form-item>

          <n-grid :cols="2" :x-gap="12">
            <n-gi>
              <n-form-item label="API 基础地址 (Base URL)">
                <n-input v-model:value="createForm.onlineBaseUrl" placeholder="https://api.siliconflow.cn/v1" />
              </n-form-item>
            </n-gi>
            <n-gi>
              <n-form-item label="API Key">
                <n-input v-model:value="createForm.onlineApiKey" type="password" show-password-on="click" placeholder="sk-..." />
              </n-form-item>
            </n-gi>
          </n-grid>

          <n-grid :cols="2" :x-gap="12">
            <n-gi>
              <n-form-item label="选择或输入模型名称">
                <n-space vertical style="width: 100%;">
                  <n-input-group>
                    <n-input v-model:value="createForm.onlineModelName" placeholder="例如: BAAI/bge-m3" />
                    <n-button type="info" :loading="fetchingModels" @click="handleDiscoverRemoteModels">
                      拉取列表
                    </n-button>
                  </n-input-group>
                  <n-select
                    v-if="remoteModelOptions.length > 0"
                    v-model:value="createForm.onlineModelName"
                    :options="remoteModelOptions"
                    filterable
                    placeholder="从远端拉取到的模型列表中选取"
                  />
                </n-space>
              </n-form-item>
            </n-gi>

            <n-gi>
              <n-form-item label="向量维度 (Embedding Dimension)">
                <n-input-group>
                  <n-input-number v-model:value="createForm.detectedDimension" :min="64" :max="8192" style="width: 100%;" />
                  <n-button type="success" :loading="probingDimension" @click="handleProbeDimension">
                    探测维度
                  </n-button>
                </n-input-group>
              </n-form-item>
            </n-gi>
          </n-grid>
        </div>

        <n-form-item label="双通道解耦加速 (Dual-Channel Optimization)">
          <n-space align="center">
            <n-switch v-model:value="createForm.enableDualChannel" />
            <n-text depth="3">开启后：文档入库走线上云端高速 API，搜索查询走本地轻量引擎（兼顾秒级入库与本地离线零成本）</n-text>
          </n-space>
        </n-form-item>

        <n-divider title-placement="left">初始数据连接器 (可选)</n-divider>

        <n-grid :cols="2" :x-gap="12">
          <n-gi>
            <n-form-item label="本地目标文件夹路径">
              <n-input v-model:value="createForm.initialFolder" placeholder="例如: D:\\Documents\\TechNotes" />
            </n-form-item>
          </n-gi>
          <n-gi>
            <n-form-item label="文件匹配模式 (Glob)">
              <n-input v-model:value="createForm.initialPattern" placeholder="*.md,*.txt,*.pdf" />
            </n-form-item>
          </n-gi>
        </n-grid>
      </n-form>

      <template #footer>
        <n-space justify="flex-end">
          <n-button @click="showCreateModal = false">取消</n-button>
          <n-button type="primary" :loading="creating" @click="handleCreateSubmit">
            立即创建
          </n-button>
        </n-space>
      </template>
    </n-modal>

    <!-- 连接器管理抽屉 -->
    <n-drawer v-model:show="showConnectorDrawer" :width="560" placement="right">
      <n-drawer-content :title="'连接器管理 - ' + (currentKb?.name || '')">
        <n-space vertical :size="16">
          <n-alert type="info">
            连接器是知识库的数据输入管道。一个知识库可挂载多个本地目录，各目录变更将增量同步到此库专属的底层数据库中。
          </n-alert>

          <!-- 添加新连接器表单 -->
          <n-card title="挂载本地新目录" size="small">
            <n-space vertical :size="10">
              <n-input v-model:value="newConnectorPath" placeholder="本地完整路径 (如 E:\\Notes)" />
              <n-input v-model:value="newConnectorPattern" placeholder="文件规则 (默认 *.md,*.txt,*.pdf)" />
              <n-button type="primary" block @click="handleAddConnector">
                <template #icon><n-icon :component="AddOutline" /></template>
                添加连接器
              </n-button>
            </n-space>
          </n-card>

          <!-- 已挂载连接器列表 -->
          <n-card title="已挂载连接器列表" size="small">
            <n-spin :show="loadingConnectors">
              <n-list v-if="currentConnectors.length > 0">
                <n-list-item v-for="conn in currentConnectors" :key="conn.id">
                  <n-thing :title="conn.targetPath" :description="'规则: ' + (conn.filePattern || '全部文件')">
                    <template #extra>
                      <n-space>
                        <n-button size="small" type="primary" secondary @click="handleTriggerSync(currentKb.id, conn.id)">
                          <template #icon><n-icon :component="PlayOutline" /></template>
                          立即同步
                        </n-button>
                        <n-button size="small" quaternary type="error" @click="handleDeleteConnector(conn.id)">
                          <template #icon><n-icon :component="TrashOutline" /></template>
                        </n-button>
                      </n-space>
                    </template>
                  </n-thing>
                </n-list-item>
              </n-list>
              <n-empty v-else description="暂无挂载连接器" />
            </n-spin>
          </n-card>
        </n-space>
      </n-drawer-content>
    </n-drawer>
  </n-space>
`;

export default component;
