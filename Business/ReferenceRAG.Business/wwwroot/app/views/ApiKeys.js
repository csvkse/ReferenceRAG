import { defineComponent as _defineComponent, ref, reactive, computed, onMounted, h } from 'vue';
import {
    NCard, NSpace, NButton, NTag, NDataTable, NModal, NForm, NFormItem,
    NInput, NCheckbox, NCheckboxGroup, NRadio, NRadioGroup, NAlert, NText,
    NPopconfirm, NDescriptions, NDescriptionsItem, useMessage
} from 'naive-ui';
import { apiKeysApi } from '/app/api/index.js';

const component = /*@__PURE__*/ _defineComponent({
    name: 'ApiKeys',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();

        const keys = ref([]);
        const loading = ref(false);
        const metaCatalog = ref({ modules: [], mcpTools: [], sources: [] });

        // 新建/编辑弹窗
        const showModal = ref(false);
        const isEdit = ref(false);
        const currentId = ref(null);
        const submitting = ref(false);

        const form = reactive({
            name: '',
            allowApi: true,
            allowMcp: true,
            allScopes: true,
            selectedScopes: [],
            allTools: true,
            selectedTools: [],
            sourceScopeMode: 'all',
            selectedSources: []
        });

        // 明文展示弹窗（仅新建一次）
        const showSecretModal = ref(false);
        const createdSecret = reactive({
            name: '',
            rawKey: '',
            keyPrefix: ''
        });

        // 客户端接入指南弹窗
        const showGuideModal = ref(false);
        const guideKey = ref('');

        const loadCatalog = async () => {
            try {
                const res = await apiKeysApi.getMetaCatalog();
                metaCatalog.value = res.data || { modules: [], mcpTools: [], sources: [] };
            } catch (err) {
                console.warn('获取权限元数据失败:', err);
            }
        };

        const loadKeys = async () => {
            loading.value = true;
            try {
                const res = await apiKeysApi.getAll();
                keys.value = res.data || [];
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '加载 API Key 列表失败');
            } finally {
                loading.value = false;
            }
        };

        const parseJsonArray = (str) => {
            if (!str) return [];
            try {
                const parsed = JSON.parse(str);
                return Array.isArray(parsed) ? parsed : [];
            } catch {
                return str.split(',').map(s => s.trim()).filter(Boolean);
            }
        };

        const openCreateModal = () => {
            isEdit.value = false;
            currentId.value = null;
            form.name = '';
            form.allowApi = true;
            form.allowMcp = true;
            form.allScopes = true;
            form.selectedScopes = metaCatalog.value.modules.map(m => m.key);
            form.allTools = true;
            form.selectedTools = metaCatalog.value.mcpTools.map(t => t.name);
            form.sourceScopeMode = 'all';
            form.selectedSources = metaCatalog.value.sources.map(s => s.name);
            showModal.value = true;
        };

        const openEditModal = (row) => {
            isEdit.value = true;
            currentId.value = row.id;
            form.name = row.name;
            form.allowApi = row.allowApi;
            form.allowMcp = row.allowMcp;

            const scopes = parseJsonArray(row.allowedScopes);
            if (scopes.includes('*') || scopes.length === 0) {
                form.allScopes = true;
                form.selectedScopes = metaCatalog.value.modules.map(m => m.key);
            } else {
                form.allScopes = false;
                form.selectedScopes = scopes;
            }

            const tools = parseJsonArray(row.allowedMcpTools);
            if (tools.includes('*') || tools.length === 0) {
                form.allTools = true;
                form.selectedTools = metaCatalog.value.mcpTools.map(t => t.name);
            } else {
                form.allTools = false;
                form.selectedTools = tools;
            }

            form.sourceScopeMode = row.sourceScopeMode || 'all';
            const sources = parseJsonArray(row.allowedSources);
            form.selectedSources = sources.length > 0 ? sources : metaCatalog.value.sources.map(s => s.name);

            showModal.value = true;
        };

        const handleToggleAllScopes = (checked) => {
            form.allScopes = checked;
            if (checked) {
                form.selectedScopes = metaCatalog.value.modules.map(m => m.key);
            }
        };

        const handleToggleAllTools = (checked) => {
            form.allTools = checked;
            if (checked) {
                form.selectedTools = metaCatalog.value.mcpTools.map(t => t.name);
            }
        };

        const handleSave = async () => {
            if (!form.name.trim()) {
                message.warning('请输入 API Key 名称');
                return;
            }
            if (!form.allowApi && !form.allowMcp) {
                message.warning('请至少开启一种通道（REST API 或 MCP 协议）');
                return;
            }

            submitting.value = true;
            try {
                const payload = {
                    name: form.name.trim(),
                    allowApi: form.allowApi,
                    allowMcp: form.allowMcp,
                    allowedScopes: form.allScopes ? '["*"]' : JSON.stringify(form.selectedScopes),
                    allowedMcpTools: form.allTools ? '["*"]' : JSON.stringify(form.selectedTools),
                    sourceScopeMode: form.sourceScopeMode,
                    allowedSources: form.sourceScopeMode === 'all' ? '[]' : JSON.stringify(form.selectedSources)
                };

                if (isEdit.value) {
                    await apiKeysApi.update(currentId.value, payload);
                    message.success('API Key 权限已更新');
                    showModal.value = false;
                    await loadKeys();
                } else {
                    const res = await apiKeysApi.create(payload);
                    showModal.value = false;
                    await loadKeys();

                    // 展示一次性明文弹窗
                    createdSecret.name = res.data.entity?.name || form.name;
                    createdSecret.rawKey = res.data.rawKey;
                    createdSecret.keyPrefix = res.data.entity?.keyPrefix || '';
                    showSecretModal.value = true;
                }
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '保存 API Key 失败');
            } finally {
                submitting.value = false;
            }
        };

        const handleToggle = async (row) => {
            try {
                const nextState = !row.isEnabled;
                await apiKeysApi.toggle(row.id, nextState);
                message.success(nextState ? `已启用「${row.name}」` : `已停用「${row.name}」`);
                await loadKeys();
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '切换状态失败');
            }
        };

        const handleDelete = async (row) => {
            try {
                await apiKeysApi.delete(row.id);
                message.success(`已删除 API Key「${row.name}」`);
                await loadKeys();
            } catch (err) {
                message.error(err.response?.data?.error || err.message || '删除失败');
            }
        };

        const copyToClipboard = async (text) => {
            try {
                await navigator.clipboard.writeText(text);
                message.success('已复制到剪贴板！');
            } catch {
                message.error('复制失败，请手动长按或选中文本复制');
            }
        };

        const openGuide = (keyText) => {
            guideKey.value = keyText || createdSecret.rawKey || 'rag_live_xxxxxxxx';
            showGuideModal.value = true;
        };

        const mcpConfigJson = computed(() => {
            const host = window.location.origin;
            return JSON.stringify({
                mcpServers: {
                    "ReferenceRAG": {
                        url: `${host}/api/mcp`,
                        headers: {
                            Authorization: `Bearer ${guideKey.value}`
                        }
                    }
                }
            }, null, 2);
        });

        const columns = [
            {
                title: '名称',
                key: 'name',
                width: 170,
                render: (row) => h('div', { style: { fontWeight: '600' } }, row.name)
            },
            {
                title: '密钥前缀',
                key: 'keyPrefix',
                width: 150,
                render: (row) => h(NTag, { type: 'default', size: 'small', style: { fontFamily: 'monospace' } }, {
                    default: () => `${row.keyPrefix}...`
                })
            },
            {
                title: '通道权限',
                key: 'channels',
                width: 140,
                render: (row) => h(NSpace, { size: 'tiny' }, {
                    default: () => [
                        h(NTag, { type: row.allowApi ? 'success' : 'default', size: 'small' }, { default: () => 'API' }),
                        h(NTag, { type: row.allowMcp ? 'info' : 'default', size: 'small' }, { default: () => 'MCP' })
                    ]
                })
            },
            {
                title: '功能模块范围',
                key: 'allowedScopes',
                ellipsis: { tooltip: true },
                render: (row) => {
                    const scopes = parseJsonArray(row.allowedScopes);
                    if (scopes.includes('*') || scopes.length === 0) {
                        return h(NTag, { type: 'primary', size: 'small' }, { default: () => '全模块放行 (*)' });
                    }
                    return h(NSpace, { size: 'tiny' }, {
                        default: () => scopes.map(s => h(NTag, { size: 'small', secondary: true }, { default: () => s }))
                    });
                }
            },
            {
                title: '数据源范围',
                key: 'sourceScopeMode',
                width: 150,
                render: (row) => {
                    if (row.sourceScopeMode === 'all') {
                        return h(NTag, { type: 'success', size: 'small', secondary: true }, { default: () => '全部知识源' });
                    }
                    const sources = parseJsonArray(row.allowedSources);
                    return h(NTag, { type: 'warning', size: 'small' }, { default: () => `限制: ${sources.length} 个源` });
                }
            },
            {
                title: '状态',
                key: 'isEnabled',
                width: 80,
                render: (row) => h(NTag, { type: row.isEnabled ? 'success' : 'error', size: 'small' }, {
                    default: () => row.isEnabled ? '启用' : '禁用'
                })
            },
            {
                title: '最后使用',
                key: 'lastUsedAt',
                width: 160,
                render: (row) => row.lastUsedAt ? new Date(row.lastUsedAt).toLocaleString('zh-CN') : '从未'
            },
            {
                title: '操作',
                key: 'actions',
                width: 220,
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NButton, { size: 'small', onClick: () => openEditModal(row) }, { default: () => '编辑' }),
                        h(NButton, {
                            size: 'small',
                            type: row.isEnabled ? 'warning' : 'success',
                            onClick: () => handleToggle(row)
                        }, { default: () => row.isEnabled ? '停用' : '启用' }),
                        h(NPopconfirm, {
                            onPositiveClick: () => handleDelete(row)
                        }, {
                            trigger: () => h(NButton, { size: 'small', type: 'error', secondary: true }, { default: () => '删除' }),
                            default: () => `确定要彻底删除 API Key「${row.name}」吗？删除后不可恢复。`
                        })
                    ]
                })
            }
        ];

        onMounted(async () => {
            await loadCatalog();
            await loadKeys();
        });

        const __returned__ = {
            keys, loading, metaCatalog, showModal, isEdit, submitting, form,
            showSecretModal, createdSecret, showGuideModal, guideKey, mcpConfigJson,
            columns, loadKeys, openCreateModal, openEditModal, handleToggleAllScopes,
            handleToggleAllTools, handleSave, handleToggle, handleDelete, copyToClipboard, openGuide
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="20">
    <n-card title="🔑 API Key 授权管理">
      <template #header-extra>
        <n-space>
          <n-button type="primary" @click="openCreateModal">
            新建 API Key
          </n-button>
          <n-button size="small" :loading="loading" @click="loadKeys">
            刷新
          </n-button>
        </n-space>
      </template>

      <n-text depth="3" style="display: block; margin-bottom: 16px;">
        为第三方应用、AI Agent（如 Cursor、Claude Desktop、Dify）及内部系统分配独立的细粒度访问凭证，支持 REST API 与 MCP 协议树状权限隔离与数据源分流。
      </n-text>

      <n-data-table
        :columns="columns"
        :data="keys"
        :loading="loading"
        :bordered="false"
        :row-key="(row) => row.id"
      />
    </n-card>

    <!-- 新建 / 编辑弹窗 -->
    <n-modal
      v-model:show="showModal"
      preset="card"
      :title="isEdit ? '编辑 API Key 权限' : '新建 API Key'"
      style="width: 820px; max-width: 90vw;"
    >
      <n-space vertical :size="16">
        <n-form label-placement="left" label-width="120">
          <n-form-item label="密钥名称" required>
            <n-input v-model:value="form.name" placeholder="例如: Cursor 团队协作、客服问答 Agent" />
          </n-form-item>

          <n-form-item label="协议通道控制" required>
            <n-space>
              <n-checkbox v-model:checked="form.allowApi">
                REST API (允许 HTTP 业务接口与流式对话)
              </n-checkbox>
              <n-checkbox v-model:checked="form.allowMcp">
                MCP 协议 (允许 AI Agent 工具调用与动态投影)
              </n-checkbox>
            </n-space>
          </n-form-item>

          <!-- 功能模块授权 -->
          <n-form-item label="业务模块授权">
            <n-space vertical :size="8" style="width: 100%;">
              <n-checkbox :checked="form.allScopes" @update:checked="handleToggleAllScopes">
                全选所有业务模块 (*)
              </n-checkbox>
              <div v-if="!form.allScopes" style="background: rgba(128,128,128,0.06); padding: 12px; border-radius: 6px;">
                <n-checkbox-group v-model:value="form.selectedScopes">
                  <n-space vertical :size="8">
                    <n-checkbox v-for="m in metaCatalog.modules" :key="m.key" :value="m.key">
                      <span>{{ m.label }}</span>
                      <n-text depth="3" style="margin-left: 8px; font-size: 12px;">({{ m.description }})</n-text>
                    </n-checkbox>
                  </n-space>
                </n-checkbox-group>
              </div>
            </n-space>
          </n-form-item>

          <!-- MCP 工具授权 -->
          <n-form-item v-if="form.allowMcp" label="MCP 工具白名单">
            <n-space vertical :size="8" style="width: 100%;">
              <n-checkbox :checked="form.allTools" @update:checked="handleToggleAllTools">
                全选所有 MCP 工具 (*)
              </n-checkbox>
              <div v-if="!form.allTools" style="background: rgba(128,128,128,0.06); padding: 12px; border-radius: 6px;">
                <n-checkbox-group v-model:value="form.selectedTools">
                  <n-space vertical :size="8">
                    <n-checkbox v-for="t in metaCatalog.mcpTools" :key="t.name" :value="t.name">
                      <span style="font-family: monospace; font-weight: 500;">{{ t.name }}</span>
                      <n-text depth="3" style="margin-left: 8px; font-size: 12px;">- {{ t.description }}</n-text>
                    </n-checkbox>
                  </n-space>
                </n-checkbox-group>
              </div>
            </n-space>
          </n-form-item>

          <!-- 数据源级隔离 -->
          <n-form-item label="数据源访问隔离" required>
            <n-space vertical :size="8" style="width: 100%;">
              <n-radio-group v-model:value="form.sourceScopeMode">
                <n-space>
                  <n-radio value="all">
                    全部知识源 (允许全局跨库检索与问答)
                  </n-radio>
                  <n-radio value="custom">
                    限制特定知识源 (最小权限原则，仅允许检索勾选源)
                  </n-radio>
                </n-space>
              </n-radio-group>

              <div v-if="form.sourceScopeMode === 'custom'" style="background: rgba(128,128,128,0.06); padding: 12px; border-radius: 6px; margin-top: 8px;">
                <n-checkbox-group v-model:value="form.selectedSources">
                  <n-space vertical :size="8">
                    <n-checkbox v-for="s in metaCatalog.sources" :key="s.name" :value="s.name">
                      <span style="font-weight: 500;">📁 {{ s.name }}</span>
                      <n-text depth="3" style="margin-left: 8px; font-size: 12px;">({{ s.path }})</n-text>
                    </n-checkbox>
                  </n-space>
                </n-checkbox-group>
              </div>
            </n-space>
          </n-form-item>
        </n-form>

        <n-space justify="end">
          <n-button @click="showModal = false">取消</n-button>
          <n-button type="primary" :loading="submitting" @click="handleSave">
            {{ isEdit ? '保存更改' : '立即创建' }}
          </n-button>
        </n-space>
      </n-space>
    </n-modal>

    <!-- 新建成功一次性明文展示弹窗 -->
    <n-modal
      v-model:show="showSecretModal"
      preset="card"
      title="🎉 API Key 创建成功"
      style="width: 650px; max-width: 90vw;"
      :mask-closable="false"
    >
      <n-space vertical :size="16">
        <n-alert type="warning" title="重要密码学安全提示">
          出于最高安全标准，服务端仅保存加盐哈希，<strong>此明文密钥仅在本次创建时展示一次</strong>。关闭窗口后将无法再次找回，请立即复制并妥善保管！
        </n-alert>

        <n-descriptions label-placement="left" :column="1" bordered size="small">
          <n-descriptions-item label="密钥名称">
            <strong>{{ createdSecret.name }}</strong>
          </n-descriptions-item>
          <n-descriptions-item label="明文密钥">
            <n-space align="center">
              <span style="font-family: monospace; font-weight: 600; color: #18a058; word-break: break-all;">
                {{ createdSecret.rawKey }}
              </span>
              <n-button size="small" type="primary" @click="copyToClipboard(createdSecret.rawKey)">
                一键复制
              </n-button>
            </n-space>
          </n-descriptions-item>
        </n-descriptions>

        <n-space justify="space-between" align="center">
          <n-button type="info" secondary @click="openGuide(createdSecret.rawKey)">
            查看 Claude Desktop / Cursor 接入配置
          </n-button>
          <n-button type="primary" @click="showSecretModal = false">
            我已妥善保存，关闭窗口
          </n-button>
        </n-space>
      </n-space>
    </n-modal>

    <!-- Agent 客户端配置生成器弹窗 -->
    <n-modal
      v-model:show="showGuideModal"
      preset="card"
      title="🤖 AI Agent / 客户端接入指南"
      style="width: 700px; max-width: 90vw;"
    >
      <n-space vertical :size="16">
        <n-text>
          将下方 JSON 配置复制并粘贴到 <code>claude_desktop_config.json</code> 或 Cursor 的 MCP 配置文件中即可完成接入：
        </n-text>

        <n-input
          type="textarea"
          :value="mcpConfigJson"
          readonly
          :autosize="{ minRows: 8, maxRows: 12 }"
          style="font-family: monospace; font-size: 13px;"
        />

        <n-space justify="end">
          <n-button type="primary" @click="copyToClipboard(mcpConfigJson)">
            复制配置代码
          </n-button>
        </n-space>
      </n-space>
    </n-modal>
  </n-space>
`;

export default component;
