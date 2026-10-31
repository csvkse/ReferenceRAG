import { defineComponent as _defineComponent, ref, reactive, computed, onMounted, h } from 'vue';
import {
    NCard, NSpace, NButton, NTag, NDataTable, NModal, NForm, NFormItem,
    NInput, NCheckbox, NCheckboxGroup, NRadio, NRadioGroup, NRadioButton, NAlert, NText,
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
        const metaCatalog = ref({ categories: [], modules: [], mcpTools: [], sources: [] });

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

        // 统一权限树搜索与交互状态
        const scopeSearchKeyword = ref('');
        const scopeProtocolFilter = ref('all'); // 'all', 'api', 'mcp'
        const collapsedCategories = ref(new Set());

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
                metaCatalog.value = res.data || { categories: [], modules: [], mcpTools: [], sources: [] };
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

        // 过滤后的业务分类列表
        const filteredCategories = computed(() => {
            const list = metaCatalog.value.categories || [];
            const kw = scopeSearchKeyword.value.trim().toLowerCase();
            const proto = scopeProtocolFilter.value;

            return list.filter(cat => {
                if (proto === 'api' && !cat.supportsApi) return false;
                if (proto === 'mcp' && !cat.supportsMcp) return false;

                if (!kw) return true;
                const matchCat = (cat.name || '').toLowerCase().includes(kw) ||
                                 (cat.key || '').toLowerCase().includes(kw) ||
                                 (cat.moduleCode || '').toLowerCase().includes(kw) ||
                                 (cat.description || '').toLowerCase().includes(kw);
                if (matchCat) return true;
                const matchTool = (cat.tools || []).some(t =>
                    (t.name || '').toLowerCase().includes(kw) || (t.description || '').toLowerCase().includes(kw)
                );
                return matchTool;
            });
        });

        // 折叠状态控制
        const isCategoryCollapsed = (key) => collapsedCategories.value.has(key);
        const toggleCategoryCollapse = (key) => {
            if (collapsedCategories.value.has(key)) {
                collapsedCategories.value.delete(key);
            } else {
                collapsedCategories.value.add(key);
            }
        };
        const toggleExpandAll = () => {
            const categories = metaCatalog.value.categories || [];
            if (collapsedCategories.value.size > 0) {
                collapsedCategories.value.clear();
            } else {
                categories.forEach(c => collapsedCategories.value.add(c.key));
            }
        };

        // 统计模块下已勾选工具数
        const getCategorySelectedToolCount = (cat) => {
            if (!cat.tools || cat.tools.length === 0) return 0;
            return cat.tools.filter(t => form.selectedTools.includes(t.name)).length;
        };

        // 分类勾选判定（三态联动）
        const isCategoryFullyChecked = (cat) => {
            if (!form.selectedScopes.includes(cat.moduleCode)) return false;
            if (!cat.tools || cat.tools.length === 0) return true;
            return getCategorySelectedToolCount(cat) === cat.tools.length;
        };

        const isCategoryIndeterminate = (cat) => {
            const modChecked = form.selectedScopes.includes(cat.moduleCode);
            if (!cat.tools || cat.tools.length === 0) return false;
            const count = getCategorySelectedToolCount(cat);
            if (modChecked) {
                return count > 0 && count < cat.tools.length;
            } else {
                return count > 0;
            }
        };

        const syncGlobalFlags = () => {
            const categories = metaCatalog.value.categories || [];
            const allModules = categories.map(c => c.moduleCode);
            const allTools = categories.flatMap(c => (c.tools || []).map(t => t.name));

            form.allScopes = allModules.length > 0 && allModules.every(m => form.selectedScopes.includes(m));
            form.allTools = allTools.length > 0 && allTools.every(t => form.selectedTools.includes(t));
        };

        // 切换业务分类主节点勾选
        const toggleCategoryNode = (cat) => {
            const isChecked = isCategoryFullyChecked(cat);
            if (isChecked) {
                // 取消该模块授权，并同步移除其下所有 MCP 工具
                form.selectedScopes = form.selectedScopes.filter(m => m !== cat.moduleCode);
                if (cat.tools && cat.tools.length > 0) {
                    const toolNames = new Set(cat.tools.map(t => t.name));
                    form.selectedTools = form.selectedTools.filter(t => !toolNames.has(t));
                }
            } else {
                // 开启该模块授权，并同步勾选其下所有 MCP 工具
                if (!form.selectedScopes.includes(cat.moduleCode)) {
                    form.selectedScopes.push(cat.moduleCode);
                }
                if (cat.tools && cat.tools.length > 0) {
                    const toolSet = new Set(form.selectedTools);
                    cat.tools.forEach(t => toolSet.add(t.name));
                    form.selectedTools = Array.from(toolSet);
                }
            }
            syncGlobalFlags();
        };

        // 工具叶子节点勾选
        const isToolChecked = (toolName) => {
            return form.selectedTools.includes(toolName);
        };

        const toggleToolNode = (cat, toolName) => {
            const idx = form.selectedTools.indexOf(toolName);
            if (idx >= 0) {
                form.selectedTools.splice(idx, 1);
            } else {
                form.selectedTools.push(toolName);
                // 自动同步激活父级业务功能模块！
                if (!form.selectedScopes.includes(cat.moduleCode)) {
                    form.selectedScopes.push(cat.moduleCode);
                }
            }
            syncGlobalFlags();
        };

        // 快捷安全策略预设
        const selectAll = () => {
            const categories = metaCatalog.value.categories || [];
            form.selectedScopes = categories.map(c => c.moduleCode);
            form.selectedTools = categories.flatMap(c => (c.tools || []).map(t => t.name));
            form.allScopes = true;
            form.allTools = true;
            message.success('已应用「全量放行」策略');
        };

        const selectStandardOps = () => {
            const standardModules = ['chat', 'search', 'files', 'sources'];
            form.selectedScopes = [...standardModules];
            const categories = metaCatalog.value.categories || [];
            form.selectedTools = categories
                .filter(c => standardModules.includes(c.moduleCode))
                .flatMap(c => (c.tools || []).map(t => t.name));
            syncGlobalFlags();
            message.success('已应用「知识问答与检索」预设策略');
        };

        const selectReadOnly = () => {
            const categories = metaCatalog.value.categories || [];
            form.selectedScopes = categories.map(c => c.moduleCode);
            form.selectedTools = categories
                .flatMap(c => (c.tools || []).filter(t => t.riskLevel === 'Read').map(t => t.name));
            syncGlobalFlags();
            message.success('已应用「只读探测」安全策略');
        };

        const clearAll = () => {
            form.selectedScopes = [];
            form.selectedTools = [];
            form.allScopes = false;
            form.allTools = false;
            message.info('已清空全部功能模块与工具');
        };

        const openCreateModal = () => {
            isEdit.value = false;
            currentId.value = null;
            form.name = '';
            form.allowApi = true;
            form.allowMcp = true;
            form.allScopes = true;
            const categories = metaCatalog.value.categories || [];
            form.selectedScopes = categories.map(m => m.moduleCode);
            form.allTools = true;
            form.selectedTools = categories.flatMap(c => (c.tools || []).map(t => t.name));
            form.sourceScopeMode = 'all';
            form.selectedSources = (metaCatalog.value.sources || []).map(s => s.name);

            collapsedCategories.value.clear();
            scopeSearchKeyword.value = '';
            scopeProtocolFilter.value = 'all';
            showModal.value = true;
        };

        const openEditModal = (row) => {
            isEdit.value = true;
            currentId.value = row.id;
            form.name = row.name;
            form.allowApi = row.allowApi;
            form.allowMcp = row.allowMcp;

            const categories = metaCatalog.value.categories || [];
            const allCatModules = categories.map(m => m.moduleCode);
            const allCatTools = categories.flatMap(c => (c.tools || []).map(t => t.name));

            const scopes = parseJsonArray(row.allowedScopes);
            if (scopes.includes('*') || scopes.length === 0) {
                form.allScopes = true;
                form.selectedScopes = [...allCatModules];
            } else {
                form.allScopes = false;
                form.selectedScopes = scopes;
            }

            const tools = parseJsonArray(row.allowedMcpTools);
            if (tools.includes('*') || tools.length === 0) {
                form.allTools = true;
                form.selectedTools = [...allCatTools];
            } else {
                form.allTools = false;
                form.selectedTools = tools;
            }

            form.sourceScopeMode = row.sourceScopeMode || 'all';
            const sources = parseJsonArray(row.allowedSources);
            form.selectedSources = sources.length > 0 ? sources : (metaCatalog.value.sources || []).map(s => s.name);

            collapsedCategories.value.clear();
            scopeSearchKeyword.value = '';
            scopeProtocolFilter.value = 'all';
            showModal.value = true;
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
            if (form.selectedScopes.length === 0) {
                message.warning('请至少勾选一个业务功能模块');
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
                render: (row) => h(NSpace, { size: 'small' }, {
                    default: () => [
                        h(NTag, { type: row.allowApi ? 'success' : 'default', size: 'small' }, { default: () => 'API' }),
                        h(NTag, { type: row.allowMcp ? 'info' : 'default', size: 'small' }, { default: () => 'MCP' })
                    ]
                })
            },
            {
                title: '业务功能模块',
                key: 'allowedScopes',
                ellipsis: { tooltip: true },
                render: (row) => {
                    const scopes = parseJsonArray(row.allowedScopes);
                    if (scopes.includes('*') || scopes.length === 0) {
                        return h(NTag, { type: 'primary', size: 'small' }, { default: () => '全模块放行 (*)' });
                    }
                    return h(NSpace, { size: 'small' }, {
                        default: () => scopes.map(s => h(NTag, { size: 'small', secondary: true }, { default: () => s }))
                    });
                }
            },
            {
                title: 'MCP 工具范围',
                key: 'allowedMcpTools',
                width: 140,
                render: (row) => {
                    if (!row.allowMcp) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '未开启 MCP' });
                    }
                    const tools = parseJsonArray(row.allowedMcpTools);
                    if (tools.includes('*') || tools.length === 0) {
                        return h(NTag, { type: 'info', size: 'small' }, { default: () => '全部工具 (*)' });
                    }
                    return h(NTag, { type: 'info', size: 'small', secondary: true }, { default: () => `已选 ${tools.length} 个工具` });
                }
            },
            {
                title: '数据源范围',
                key: 'sourceScopeMode',
                width: 140,
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
            columns, loadKeys, openCreateModal, openEditModal, handleSave, handleToggle,
            handleDelete, copyToClipboard, openGuide,
            // 统一树状矩阵状态与操作方法
            filteredCategories, isCategoryCollapsed, toggleCategoryCollapse, toggleExpandAll,
            isCategoryFullyChecked, isCategoryIndeterminate, toggleCategoryNode,
            getCategorySelectedToolCount, isToolChecked, toggleToolNode,
            selectAll, selectStandardOps, selectReadOnly, clearAll,
            scopeSearchKeyword, scopeProtocolFilter
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
        为第三方应用、AI Agent（如 Cursor、Claude Desktop、Dify）及内部系统分配独立的细粒度访问凭证，支持 REST API 与 MCP 协议功能模块化统一授权与数据源分流。
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
      style="width: 860px; max-width: 92vw;"
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

          <!-- 统一业务模块与 MCP 工具树状授权矩阵 (对齐 WebToolForLinux 规范) -->
          <n-form-item label="业务与MCP权限" required>
            <div style="width: 100%; border: 1px solid rgba(128,128,128,0.2); border-radius: 8px; padding: 12px; background: rgba(128,128,128,0.03);">
              <!-- 头部工具栏与策略预设 -->
              <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; flex-wrap: wrap; gap: 8px;">
                <div>
                  <span style="font-weight: 600; font-size: 13px;">功能模块与 MCP 二级工具树</span>
                  <n-text depth="3" style="font-size: 11px; margin-left: 8px;">
                    (勾选模块同时授权 API 与旗下 MCP 工具，支持展开反选微调)
                  </n-text>
                </div>
                <n-space size="small">
                  <n-button size="tiny" quaternary type="primary" @click="selectAll">全选全部</n-button>
                  <n-button size="tiny" quaternary type="info" @click="selectStandardOps">标准问答检索</n-button>
                  <n-button size="tiny" quaternary type="warning" @click="selectReadOnly">只读探测</n-button>
                  <n-button size="tiny" quaternary type="error" @click="clearAll">清空</n-button>
                  <n-button size="tiny" quaternary @click="toggleExpandAll">展开/折叠</n-button>
                </n-space>
              </div>

              <!-- 搜索与协议过滤器 -->
              <div style="display: flex; gap: 8px; margin-bottom: 12px; align-items: center;">
                <n-input
                  v-model:value="scopeSearchKeyword"
                  placeholder="搜索模块名称、#code、MCP 工具名或功能描述..."
                  size="small"
                  clearable
                  style="flex: 1;"
                />
                <n-radio-group v-model:value="scopeProtocolFilter" size="small">
                  <n-radio-button value="all">全部</n-radio-button>
                  <n-radio-button value="api">仅 API</n-radio-button>
                  <n-radio-button value="mcp">仅 MCP</n-radio-button>
                </n-radio-group>
              </div>

              <!-- 树状卡片滚动容器 -->
              <div style="max-height: 380px; overflow-y: auto; display: flex; flex-direction: column; gap: 8px; padding-right: 4px;">
                <div v-if="filteredCategories.length === 0" style="text-align: center; padding: 24px; color: #888; font-size: 12px;">
                  未匹配到符合条件的业务模块或 MCP 工具
                </div>

                <!-- 模块级卡片 (父节点) -->
                <div
                  v-for="cat in filteredCategories"
                  :key="cat.key"
                  style="border: 1px solid rgba(128,128,128,0.18); border-radius: 6px; overflow: hidden; background: rgba(128,128,128,0.04);"
                >
                  <!-- 模块头部 -->
                  <div
                    style="display: flex; justify-content: space-between; align-items: center; padding: 8px 12px; background: rgba(128,128,128,0.06); cursor: pointer; user-select: none;"
                    @click="toggleCategoryCollapse(cat.key)"
                  >
                    <div style="display: flex; align-items: center; gap: 8px; flex: 1;">
                      <span style="font-size: 11px; color: #888; width: 14px; text-align: center;">
                        {{ isCategoryCollapsed(cat.key) ? '▶' : '▼' }}
                      </span>
                      <n-checkbox
                        :checked="isCategoryFullyChecked(cat)"
                        :indeterminate="isCategoryIndeterminate(cat)"
                        @click.stop
                        @update:checked="() => toggleCategoryNode(cat)"
                      />
                      <div>
                        <span style="font-weight: 600; font-size: 13px;">{{ cat.name }}</span>
                        <span style="font-family: monospace; font-size: 11px; margin-left: 6px; color: #18a058;">#{{ cat.moduleCode }}</span>
                        <n-text depth="3" style="font-size: 11px; margin-left: 8px; display: inline-block;">{{ cat.description }}</n-text>
                      </div>
                    </div>

                    <!-- 通道标记与工具计数 -->
                    <n-space size="small" align="center" style="flex-shrink: 0;" @click.stop>
                      <n-tag v-if="cat.supportsApi" size="tiny" type="success" secondary>REST API</n-tag>
                      <n-tag v-if="cat.supportsMcp" size="tiny" type="info" secondary>MCP</n-tag>
                      <n-tag v-if="cat.tools && cat.tools.length > 0" size="tiny" :bordered="false">
                        {{ getCategorySelectedToolCount(cat) }} / {{ cat.tools.length }} 工具
                      </n-tag>
                    </n-space>
                  </div>

                  <!-- 二级 MCP 叶子工具列表 (展开展示) -->
                  <div
                    v-if="!isCategoryCollapsed(cat.key) && cat.tools && cat.tools.length > 0"
                    style="padding: 10px; display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 8px; background: rgba(0,0,0,0.08); border-top: 1px solid rgba(128,128,128,0.12);"
                  >
                    <div
                      v-for="t in cat.tools"
                      :key="t.name"
                      style="border: 1px solid rgba(128,128,128,0.15); border-radius: 6px; padding: 8px 10px; background: rgba(128,128,128,0.05); display: flex; align-items: flex-start; gap: 8px; cursor: pointer;"
                      @click="toggleToolNode(cat, t.name)"
                    >
                      <n-checkbox
                        :checked="isToolChecked(t.name)"
                        @click.stop
                        @update:checked="() => toggleToolNode(cat, t.name)"
                        style="margin-top: 2px;"
                      />
                      <div style="flex: 1; min-width: 0;">
                        <div style="display: flex; justify-content: space-between; align-items: center; gap: 6px;">
                          <span style="font-family: monospace; font-weight: 600; font-size: 12px; color: #2080f0; word-break: break-all;">
                            {{ t.name }}
                          </span>
                          <n-tag
                            size="tiny"
                            :type="t.riskLevel === 'Read' ? 'success' : (t.riskLevel === 'Execute' ? 'info' : 'warning')"
                            secondary
                          >
                            {{ t.riskLevel === 'Read' ? '只读' : (t.riskLevel === 'Execute' ? '执行' : '高危') }}
                          </n-tag>
                        </div>
                        <n-text depth="3" style="font-size: 11px; line-height: 1.3; display: block; margin-top: 3px;">
                          {{ t.description }}
                        </n-text>
                      </div>
                    </div>
                  </div>
                </div>
              </div>
            </div>
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
