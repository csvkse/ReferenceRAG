import { useMessage } from 'naive-ui';
import { defineComponent as _defineComponent } from 'vue';
import { ref, onMounted } from 'vue';
import { NButton, NSpace, NTag, NInput, NInputNumber, NForm, NFormItem, NAlert, NCard, NList, NListItem, NThing, NProgress, NModal, NDescriptions, NDescriptionsItem, NText, NIcon, NEllipsis, NEmpty, NSpin } from 'naive-ui';
import { RefreshOutline, TrashOutline } from '@vicons/ionicons5';
import { bm25IndexApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'BM25Index',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        // State
        const loading = ref(false);
        const indexing = ref(false);
        const clearing = ref(false);
        const searching = ref(false);
        const showClearDialog = ref(false);
        const summary = ref({
            totalIndexedDocuments: 0,
            totalVocabularySize: 0,
            averageDocLength: 0,
            totalFiles: 0,
            totalChunks: 0
        });
        const indexProgress = ref(null);
        const searchResults = ref([]);
        const searchDuration = ref(0);
        const searchForm = ref({
            query: '',
            topK: 10
        });
        // Methods
        const loadSummary = async () => {
            loading.value = true;
            try {
                const response = await bm25IndexApi.getSummary();
                summary.value = response.data;
            }
            catch (error) {
                console.error('Failed to load summary:', error);
                message.error('加载索引概览失败');
            }
            finally {
                loading.value = false;
            }
        };
        const handleIndexAll = async () => {
            indexing.value = true;
            indexProgress.value = {
                progressPercent: 0,
                status: 'default',
                message: '正在建立索引...',
                totalDocuments: 0,
                totalTerms: 0
            };
            try {
                const response = await bm25IndexApi.indexAll();
                const result = response.data;
                indexProgress.value = {
                    progressPercent: result.progressPercent,
                    status: 'success',
                    message: result.message,
                    totalDocuments: result.totalDocuments,
                    totalTerms: result.totalTerms
                };
                message.success('索引建立完成');
                await loadSummary();
            }
            catch (error) {
                console.error('Failed to index:', error);
                indexProgress.value = {
                    progressPercent: 0,
                    status: 'error',
                    message: `索引失败: ${error.response?.data?.error || error.message}`,
                    totalDocuments: 0,
                    totalTerms: 0
                };
                message.error(`索引失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                indexing.value = false;
            }
        };
        const handleClear = async () => {
            clearing.value = true;
            try {
                await bm25IndexApi.clearIndex();
                message.success('索引已清空');
                showClearDialog.value = false;
                await loadSummary();
            }
            catch (error) {
                console.error('Failed to clear index:', error);
                message.error(`清空失败: ${error.response?.data?.error || error.message}`);
            }
            finally {
                clearing.value = false;
            }
        };
        const handleSearch = async () => {
            if (!searchForm.value.query) {
                message.error('请输入查询语句');
                return;
            }
            searching.value = true;
            try {
                const response = await bm25IndexApi.search(searchForm.value.query, searchForm.value.topK);
                const result = response.data;
                searchResults.value = result.results;
                searchDuration.value = result.durationMs;
            }
            catch (error) {
                console.error('Failed to search:', error);
                message.error(`搜索失败: ${error.response?.data?.error || error.message}`);
                searchResults.value = [];
            }
            finally {
                searching.value = false;
            }
        };
        // Lifecycle
        onMounted(() => {
            loadSummary();
        });
        const __returned__ = { message, loading, indexing, clearing, searching, showClearDialog, summary, indexProgress, searchResults, searchDuration, searchForm, loadSummary, handleIndexAll, handleClear, handleSearch, get NButton() { return NButton; }, get NSpace() { return NSpace; }, get NTag() { return NTag; }, get NInput() { return NInput; }, get NInputNumber() { return NInputNumber; }, get NForm() { return NForm; }, get NFormItem() { return NFormItem; }, get NAlert() { return NAlert; }, get NCard() { return NCard; }, get NList() { return NList; }, get NListItem() { return NListItem; }, get NThing() { return NThing; }, get NProgress() { return NProgress; }, get NModal() { return NModal; }, get NDescriptions() { return NDescriptions; }, get NDescriptionsItem() { return NDescriptionsItem; }, get NText() { return NText; }, get NIcon() { return NIcon; }, get NEllipsis() { return NEllipsis; }, get NEmpty() { return NEmpty; }, get NSpin() { return NSpin; }, get RefreshOutline() { return RefreshOutline; }, get TrashOutline() { return TrashOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- BM25 Index Management -->\n    <n-card title=\"BM25索引管理\">\n      <n-tabs type=\"line\" animated>\n        <!-- Summary Tab -->\n        <n-tab-pane name=\"summary\" tab=\"索引概览\">\n          <n-space vertical :size=\"16\">\n            <n-spin :show=\"loading\">\n              <n-descriptions label-placement=\"left\" :column=\"2\" bordered>\n                <n-descriptions-item label=\"已索引文档\">\n                  <n-text strong>{{ summary.totalIndexedDocuments }}</n-text>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"词汇量\">\n                  <n-text strong>{{ summary.totalVocabularySize }}</n-text>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"平均文档长度\">\n                  <n-text strong>{{ summary.averageDocLength?.toFixed(2) || '-' }}</n-text>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"总文件数\">\n                  <n-text strong>{{ summary.totalFiles }}</n-text>\n                </n-descriptions-item>\n                <n-descriptions-item label=\"总分块数\">\n                  <n-text strong>{{ summary.totalChunks }}</n-text>\n                </n-descriptions-item>\n              </n-descriptions>\n            </n-spin>\n          </n-space>\n        </n-tab-pane>\n\n        <!-- Index Operations Tab -->\n        <n-tab-pane name=\"operations\" tab=\"索引操作\">\n          <n-space vertical :size=\"16\">\n            <n-alert type=\"info\">\n              <template #header>提示</template>\n              <n-space vertical>\n                <n-text>全量索引将重新处理所有已存储的文档并建立 BM25 索引。</n-text>\n                <n-text>清空索引将删除所有 BM25 索引数据，此操作不可恢复。</n-text>\n              </n-space>\n            </n-alert>\n\n            <n-space>\n              <n-button\n                type=\"primary\"\n                :loading=\"indexing\"\n                @click=\"handleIndexAll\"\n              >\n                <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n                全量索引\n              </n-button>\n              <n-button\n                type=\"error\"\n                :loading=\"clearing\"\n                @click=\"showClearDialog = true\"\n              >\n                <template #icon><n-icon :component=\"TrashOutline\" /></template>\n                清空索引\n              </n-button>\n              <n-button @click=\"loadSummary\" :loading=\"loading\">\n                <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n                刷新\n              </n-button>\n            </n-space>\n\n            <!-- Index progress -->\n            <n-card v-if=\"indexProgress\" title=\"索引进度\">\n              <n-progress\n                type=\"line\"\n                :percentage=\"indexProgress.progressPercent\"\n                :status=\"indexProgress.status\"\n                :indicator-placement=\"'inside'\"\n              />\n              <n-space vertical :size=\"8\" style=\"margin-top: 12px\">\n                <n-text depth=\"3\">{{ indexProgress.message }}</n-text>\n                <n-text depth=\"3\">文档数: {{ indexProgress.totalDocuments }}</n-text>\n                <n-text depth=\"3\">词汇量: {{ indexProgress.totalTerms }}</n-text>\n              </n-space>\n            </n-card>\n          </n-space>\n        </n-tab-pane>\n\n        <!-- Search Tab -->\n        <n-tab-pane name=\"search\" tab=\"搜索测试\">\n          <n-space vertical :size=\"16\">\n            <n-form :model=\"searchForm\" inline>\n              <n-form-item label=\"查询语句\">\n                <n-input\n                  v-model:value=\"searchForm.query\"\n                  placeholder=\"输入查询关键词\"\n                  style=\"width: 400px\"\n                />\n              </n-form-item>\n              <n-form-item label=\"返回数量\">\n                <n-input-number\n                  v-model:value=\"searchForm.topK\"\n                  :min=\"1\"\n                  :max=\"100\"\n                  style=\"width: 100px\"\n                />\n              </n-form-item>\n              <n-form-item>\n                <n-button type=\"primary\" :loading=\"searching\" @click=\"handleSearch\">\n                  搜索\n                </n-button>\n              </n-form-item>\n            </n-form>\n\n            <!-- Search results -->\n            <n-card v-if=\"searchResults.length > 0\" title=\"搜索结果\">\n              <template #header-extra>\n                <n-text depth=\"3\">耗时: {{ searchDuration }}ms</n-text>\n              </template>\n              <n-list>\n                <n-list-item v-for=\"(result, index) in searchResults\" :key=\"index\">\n                  <n-thing>\n                    <template #header>\n                      <n-space align=\"center\">\n                        <n-tag type=\"info\" size=\"small\">#{{ result.rank }}</n-tag>\n                        <n-text code>{{ result.chunkId }}</n-text>\n                        <n-tag type=\"success\" size=\"small\">Score: {{ result.score.toFixed(4) }}</n-tag>\n                      </n-space>\n                    </template>\n                    <n-ellipsis :line-clamp=\"3\" expand-trigger=\"click\">\n                      {{ result.content }}\n                    </n-ellipsis>\n                  </n-thing>\n                </n-list-item>\n              </n-list>\n            </n-card>\n\n            <n-empty v-else-if=\"searchForm.query && !searching\" description=\"暂无搜索结果\" />\n          </n-space>\n        </n-tab-pane>\n\n      </n-tabs>\n    </n-card>\n\n    <!-- Clear Confirm Dialog -->\n    <n-modal v-model:show=\"showClearDialog\" preset=\"dialog\" title=\"清空索引确认\">\n      <n-alert type=\"error\">\n        <template #header>警告</template>\n        清空索引将删除所有 BM25 索引数据，此操作不可恢复！\n      </n-alert>\n      <template #action>\n        <n-button @click=\"showClearDialog = false\">取消</n-button>\n        <n-button type=\"error\" :loading=\"clearing\" @click=\"handleClear\">确认清空</n-button>\n      </template>\n    </n-modal>\n  </n-space>\n";
export default component;
