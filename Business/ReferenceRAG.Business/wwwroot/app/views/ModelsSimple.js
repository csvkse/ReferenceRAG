import { defineComponent as _defineComponent } from 'vue';
import { ref, h, onMounted } from 'vue';
import { NTag, NButton, NSpace, NCode, useMessage } from 'naive-ui';
import { RefreshOutline } from '@vicons/ionicons5';
import { modelsApi, settingsApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'ModelsSimple',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const modelsPath = ref('');
        const savingModelsPath = ref(false);
        const scanning = ref(false);
        const embeddingModels = ref([]);
        const rerankModels = ref([]);
        const currentModel = ref(null);
        const currentRerankModel = ref(null);
        const embeddingLoading = ref(false);
        const rerankLoading = ref(false);
        const currentLoading = ref(false);
        const showSwitchDialog = ref(false);
        const selectedModel = ref(null);
        const switching = ref(false);
        const deleteOldVectors = ref(false);
        const loadModelsPath = async () => {
            try {
                const response = await settingsApi.get();
                const config = response.data;
                modelsPath.value = config.modelsRootPath || 'models';
            }
            catch (error) {
                console.error('Failed to load models path:', error);
                modelsPath.value = 'models';
            }
        };
        const handleSaveModelsPath = async () => {
            savingModelsPath.value = true;
            try {
                await settingsApi.updateModelsPath(modelsPath.value);
                message.success('模型路径已保存');
            }
            catch (error) {
                console.error('Failed to save models path:', error);
                message.error(`保存失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                savingModelsPath.value = false;
            }
        };
        const handleScanModels = async () => {
            scanning.value = true;
            try {
                // 调用扫描接口
                await modelsApi.scanModels();
                message.success('模型扫描完成');
                await Promise.all([
                    loadEmbeddingModels(),
                    loadRerankModels()
                ]);
            }
            catch (error) {
                console.error('Failed to scan models:', error);
                message.error(`扫描失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                scanning.value = false;
            }
        };
        const loadEmbeddingModels = async () => {
            embeddingLoading.value = true;
            try {
                const response = await modelsApi.getAll();
                embeddingModels.value = response.data;
            }
            catch (error) {
                console.error('Failed to load embedding models:', error);
                message.error('加载嵌入模型列表失败');
            }
            finally {
                embeddingLoading.value = false;
            }
        };
        const loadRerankModels = async () => {
            rerankLoading.value = true;
            try {
                const response = await modelsApi.getRerankModels();
                rerankModels.value = response.data;
            }
            catch (error) {
                console.error('Failed to load rerank models:', error);
                message.error('加载重排模型列表失败');
            }
            finally {
                rerankLoading.value = false;
            }
        };
        const loadCurrentModels = async () => {
            currentLoading.value = true;
            try {
                const [embeddingRes, rerankRes] = await Promise.all([
                    modelsApi.getCurrent(),
                    modelsApi.getCurrentRerankModel()
                ]);
                currentModel.value = embeddingRes.data;
                currentRerankModel.value = rerankRes.data;
            }
            catch (error) {
                console.error('Failed to load current models:', error);
            }
            finally {
                currentLoading.value = false;
            }
        };
        const handleSwitchEmbedding = (model) => {
            selectedModel.value = model;
            deleteOldVectors.value = false;
            showSwitchDialog.value = true;
        };
        const handleSwitchRerank = async (model) => {
            if (!model.name)
                return;
            try {
                const response = await modelsApi.switchRerankModel(model.name);
                message.success(response.data.message || `已切换到重排模型: ${model.displayName || model.name}`);
                await loadCurrentModels();
                await loadRerankModels();
            }
            catch (error) {
                console.error('Failed to switch rerank model:', error);
                message.error(`切换失败: ${error.response?.data?.error || error.message}`);
            }
        };
        const confirmSwitch = async () => {
            if (!selectedModel.value || !selectedModel.value.name)
                return;
            switching.value = true;
            try {
                const response = await modelsApi.switch(selectedModel.value.name, deleteOldVectors.value);
                message.success(response.data.message || `已切换到 ${selectedModel.value.displayName}`);
                showSwitchDialog.value = false;
                await loadCurrentModels();
                await loadEmbeddingModels();
            }
            catch (error) {
                console.error('Failed to switch model:', error);
                message.error(`切换失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                switching.value = false;
            }
        };
        const embeddingModelColumns = [
            {
                title: '模型名称',
                key: 'displayName',
                render(row) {
                    return h(NSpace, { align: 'center' }, {
                        default: () => [
                            h('span', row.displayName || row.name),
                            row.name === currentModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
                        ]
                    });
                }
            },
            {
                title: '维度',
                key: 'dimension',
                width: 80
            },
            {
                title: '最大序列长度',
                key: 'maxSequenceLength',
                width: 120
            },
            {
                title: '路径',
                key: 'localPath',
                ellipsis: { tooltip: true }
            },
            {
                title: '操作',
                key: 'actions',
                width: 120,
                render(row) {
                    if (row.name === currentModel.value?.name) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' });
                    }
                    return h(NButton, {
                        type: 'primary',
                        size: 'small',
                        onClick: () => handleSwitchEmbedding(row)
                    }, { default: () => '切换' });
                }
            }
        ];
        const rerankModelColumns = [
            {
                title: '模型名称',
                key: 'displayName',
                render(row) {
                    return h(NSpace, { align: 'center' }, {
                        default: () => [
                            h('span', row.displayName || row.name),
                            row.name === currentRerankModel.value?.name ? h(NTag, { type: 'success', size: 'small' }, { default: () => '当前' }) : null
                        ]
                    });
                }
            },
            {
                title: '维度',
                key: 'dimension',
                width: 80
            },
            {
                title: '最大序列长度',
                key: 'maxSequenceLength',
                width: 120
            },
            {
                title: '路径',
                key: 'localPath',
                ellipsis: { tooltip: true }
            },
            {
                title: '操作',
                key: 'actions',
                width: 120,
                render(row) {
                    if (row.name === currentRerankModel.value?.name) {
                        return h(NTag, { type: 'default', size: 'small' }, { default: () => '使用中' });
                    }
                    return h(NButton, {
                        type: 'primary',
                        size: 'small',
                        onClick: () => handleSwitchRerank(row)
                    }, { default: () => '切换' });
                }
            }
        ];
        onMounted(() => {
            loadModelsPath();
            loadCurrentModels();
            loadEmbeddingModels();
            loadRerankModels();
        });
        const __returned__ = { message, modelsPath, savingModelsPath, scanning, embeddingModels, rerankModels, currentModel, currentRerankModel, embeddingLoading, rerankLoading, currentLoading, showSwitchDialog, selectedModel, switching, deleteOldVectors, loadModelsPath, handleSaveModelsPath, handleScanModels, loadEmbeddingModels, loadRerankModels, loadCurrentModels, handleSwitchEmbedding, handleSwitchRerank, confirmSwitch, embeddingModelColumns, rerankModelColumns, get NTag() { return NTag; }, get NButton() { return NButton; }, get NSpace() { return NSpace; }, get NCode() { return NCode; }, get RefreshOutline() { return RefreshOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- 模型根目录设置 -->\n    <n-card title=\"模型根目录\">\n      <n-form label-placement=\"left\" label-width=\"120\">\n        <n-form-item label=\"模型存储路径\">\n          <n-space align=\"center\" style=\"width: 100%\">\n            <n-input\n              v-model:value=\"modelsPath\"\n              placeholder=\"默认: models\"\n              style=\"width: 300px\"\n            />\n            <n-button\n              type=\"primary\"\n              :loading=\"savingModelsPath\"\n              @click=\"handleSaveModelsPath\"\n            >\n              保存\n            </n-button>\n            <n-button\n              type=\"info\"\n              @click=\"handleScanModels\"\n              :loading=\"scanning\"\n            >\n              扫描模型\n            </n-button>\n          </n-space>\n        </n-form-item>\n      </n-form>\n      <n-alert type=\"info\" style=\"margin-top: 12px\">\n        <template #header>目录结构说明</template>\n        <n-text>\n          模型根目录下应包含以下子目录：<br>\n          • <n-code code=\"Embedding/\" :inline=\"true\" /> - 存放嵌入模型<br>\n          • <n-code code=\"Reranker/\" :inline=\"true\" /> - 存放重排模型<br>\n          每个模型一个文件夹，文件夹名称即为模型名称，需包含 model.onnx 文件\n        </n-text>\n      </n-alert>\n    </n-card>\n\n    <!-- 当前使用的模型 -->\n    <n-card title=\"当前使用的模型\">\n      <n-spin :show=\"currentLoading\">\n        <n-space vertical :size=\"16\">\n          <n-descriptions v-if=\"currentModel\" :column=\"4\" label-placement=\"left\">\n            <n-descriptions-item label=\"嵌入模型\">\n              <n-tag type=\"primary\">{{ currentModel.displayName || currentModel.name }}</n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"维度\">{{ currentModel.dimension }}</n-descriptions-item>\n            <n-descriptions-item label=\"最大序列长度\">{{ currentModel.maxSequenceLength }}</n-descriptions-item>\n            <n-descriptions-item label=\"状态\">\n              <n-tag type=\"success\">使用中</n-tag>\n            </n-descriptions-item>\n          </n-descriptions>\n          <n-text v-else depth=\"3\">未配置嵌入模型</n-text>\n\n          <n-divider style=\"margin: 8px 0\" />\n\n          <n-descriptions v-if=\"currentRerankModel\" :column=\"3\" label-placement=\"left\">\n            <n-descriptions-item label=\"重排模型\">\n              <n-tag type=\"info\">{{ currentRerankModel.displayName || currentRerankModel.name }}</n-tag>\n            </n-descriptions-item>\n            <n-descriptions-item label=\"维度\">{{ currentRerankModel.dimension }}</n-descriptions-item>\n            <n-descriptions-item label=\"状态\">\n              <n-tag type=\"success\">使用中</n-tag>\n            </n-descriptions-item>\n          </n-descriptions>\n          <n-text v-else depth=\"3\">未配置重排模型</n-text>\n        </n-space>\n      </n-spin>\n    </n-card>\n\n    <!-- 嵌入模型列表 -->\n    <n-card title=\"嵌入模型 (Embedding/)\">\n      <template #header-extra>\n        <n-button text @click=\"loadEmbeddingModels\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </template>\n\n      <n-data-table\n        :columns=\"embeddingModelColumns\"\n        :data=\"embeddingModels\"\n        :loading=\"embeddingLoading\"\n        :row-key=\"(row) => row.name || ''\"\n      />\n    </n-card>\n\n    <!-- 重排模型列表 -->\n    <n-card title=\"重排模型 (Reranker/)\">\n      <template #header-extra>\n        <n-button text @click=\"loadRerankModels\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </template>\n\n      <n-data-table\n        :columns=\"rerankModelColumns\"\n        :data=\"rerankModels\"\n        :loading=\"rerankLoading\"\n        :row-key=\"(row) => row.name || ''\"\n      />\n    </n-card>\n\n    <!-- 切换模型确认对话框 -->\n    <n-modal v-model:show=\"showSwitchDialog\" preset=\"dialog\" title=\"切换模型\">\n      <n-space vertical>\n        <n-text>确定要切换到模型 <strong>{{ selectedModel?.displayName }}</strong> 吗？</n-text>\n        <n-alert type=\"info\" title=\"提示\">\n          切换模型后需要重新索引数据以获得最佳效果。\n        </n-alert>\n        <n-form-item v-if=\"selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension\" label=\"旧向量数据\">\n          <n-radio-group v-model:value=\"deleteOldVectors\">\n            <n-space>\n              <n-radio :value=\"false\">保留旧向量数据</n-radio>\n              <n-radio :value=\"true\">删除旧向量数据</n-radio>\n            </n-space>\n          </n-radio-group>\n        </n-form-item>\n        <n-alert v-if=\"deleteOldVectors && selectedModel && currentModel && selectedModel.dimension !== currentModel.dimension\" type=\"error\" style=\"margin-top: 8px\">\n          删除后将无法恢复旧模型的向量数据\n        </n-alert>\n      </n-space>\n      <template #action>\n        <n-button @click=\"showSwitchDialog = false\">取消</n-button>\n        <n-button type=\"primary\" :loading=\"switching\" @click=\"confirmSwitch\">确认切换</n-button>\n      </template>\n    </n-modal>\n  </n-space>\n";
export default component;
