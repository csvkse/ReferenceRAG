import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, onUnmounted, watch, h } from 'vue';
import { useMessage, NTag, NBadge } from 'naive-ui';
import { DocumentTextOutline, RefreshOutline, CopyOutline, ArrowDownOutline, SearchOutline, CheckmarkCircleOutline, AlertCircleOutline } from '@vicons/ionicons5';
import { logsApi } from '/app/api/index.js';

const component = /*@__PURE__*/ _defineComponent({
    __name: 'Logs',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();

        const activeTab = ref('files'); // 'files' | 'operations'

        // ==========================
        // Tab 1: 文件日志状态
        // ==========================
        const filesLoading = ref(false);
        const files = ref([]);
        const fileSearch = ref('');
        const activeFileName = ref('');

        const contentLoading = ref(false);
        const logContent = ref('');
        const isTruncated = ref(false);
        const tailLines = ref(300);
        const logKeyword = ref('');
        const autoRefreshInterval = ref(0); // 0 = off, 3, 5, 10
        let autoRefreshTimer = null;

        const terminalRef = ref(null);
        const autoScroll = ref(true);

        const tailOptions = [
            { label: '末尾 100 行', value: 100 },
            { label: '末尾 300 行', value: 300 },
            { label: '末尾 1000 行', value: 1000 },
            { label: '末尾 5000 行', value: 5000 }
        ];

        const autoRefreshOptions = [
            { label: '自动刷新: 关闭', value: 0 },
            { label: '自动刷新: 3秒', value: 3 },
            { label: '自动刷新: 5秒', value: 5 },
            { label: '自动刷新: 10秒', value: 10 }
        ];

        const filteredFiles = computed(() => {
            if (!fileSearch.value.trim()) return files.value;
            const kw = fileSearch.value.trim().toLowerCase();
            return files.value.filter(f => f.name.toLowerCase().includes(kw));
        });

        const activeFileObj = computed(() => {
            return files.value.find(f => f.name === activeFileName.value) || null;
        });

        const displayContent = computed(() => {
            if (!logContent.value) return '';
            if (!logKeyword.value.trim()) return logContent.value;
            const kw = logKeyword.value.trim().toLowerCase();
            return logContent.value
                .split('\n')
                .filter(line => line.toLowerCase().includes(kw))
                .join('\n');
        });

        const formatBytes = (bytes) => {
            if (!bytes || bytes <= 0) return '0 B';
            const k = 1024;
            const sizes = ['B', 'KB', 'MB', 'GB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return (bytes / Math.pow(k, i)).toFixed(1) + ' ' + sizes[i];
        };

        const formatTime = (timeStr) => {
            if (!timeStr) return '—';
            try {
                const d = new Date(timeStr);
                return d.toLocaleString('zh-CN', { hour12: false });
            } catch {
                return timeStr;
            }
        };

        const loadFiles = async (preserveActive = true) => {
            filesLoading.value = true;
            try {
                const res = await logsApi.getFiles();
                files.value = res.data || [];
                if (files.value.length > 0) {
                    if (!preserveActive || !activeFileName.value || !files.value.some(f => f.name === activeFileName.value)) {
                        activeFileName.value = files.value[0].name;
                        await loadFileContent();
                    }
                } else {
                    activeFileName.value = '';
                    logContent.value = '';
                }
            } catch (err) {
                console.error('加载日志文件列表失败:', err);
                message.error('加载日志文件列表失败');
            } finally {
                filesLoading.value = false;
            }
        };

        const selectFile = async (name) => {
            if (activeFileName.value === name) return;
            activeFileName.value = name;
            await loadFileContent();
        };

        const loadFileContent = async (silent = false) => {
            if (!activeFileName.value) return;
            if (!silent) contentLoading.value = true;
            try {
                const res = await logsApi.getFileContent(activeFileName.value, tailLines.value);
                logContent.value = res.data?.content || '';
                isTruncated.value = !!res.data?.isTruncated;
                if (autoScroll.value && terminalRef.value) {
                    setTimeout(() => {
                        if (terminalRef.value) {
                            terminalRef.value.scrollTop = terminalRef.value.scrollHeight;
                        }
                    }, 50);
                }
            } catch (err) {
                console.error('读取日志内容失败:', err);
                if (!silent) message.error('读取日志内容失败: ' + (err.response?.data?.error || err.message));
            } finally {
                if (!silent) contentLoading.value = false;
            }
        };

        const copyContent = async () => {
            if (!logContent.value) {
                message.warning('日志内容为空');
                return;
            }
            try {
                await navigator.clipboard.writeText(logContent.value);
                message.success('已复制日志内容到剪贴板');
            } catch {
                const textarea = document.createElement('textarea');
                textarea.value = logContent.value;
                document.body.appendChild(textarea);
                textarea.select();
                document.execCommand('copy');
                document.body.removeChild(textarea);
                message.success('已复制日志内容到剪贴板');
            }
        };

        const downloadFile = () => {
            if (!activeFileName.value) return;
            const url = logsApi.getDownloadUrl(activeFileName.value);
            const a = document.createElement('a');
            a.href = url;
            a.download = activeFileName.value;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
        };

        const onTerminalScroll = () => {
            if (!terminalRef.value) return;
            const { scrollTop, scrollHeight, clientHeight } = terminalRef.value;
            // 距离底部小于 50px 则判定为开启自动滚屏，否则视为用户在往上查看，暂停自动滚到底部
            autoScroll.value = scrollHeight - (scrollTop + clientHeight) < 50;
        };

        const restartAutoRefresh = () => {
            if (autoRefreshTimer) {
                clearInterval(autoRefreshTimer);
                autoRefreshTimer = null;
            }
            if (autoRefreshInterval.value > 0) {
                autoRefreshTimer = setInterval(() => {
                    if (activeTab.value === 'files' && activeFileName.value) {
                        loadFileContent(true);
                    }
                }, autoRefreshInterval.value * 1000);
            }
        };

        watch(autoRefreshInterval, restartAutoRefresh);

        // ==========================
        // Tab 2: 操作审计日志状态
        // ==========================
        const opLoading = ref(false);
        const opItems = ref([]);
        const opTotal = ref(0);
        const opPage = ref(1);
        const opPageSize = ref(20);
        const opKeyword = ref('');

        const opColumns = [
            {
                title: '时间',
                key: 'timestamp',
                width: 170,
                render: (row) => formatTime(row.timestamp)
            },
            {
                title: '操作动作',
                key: 'action',
                width: 160,
                render: (row) => h(NTag, { type: 'info', size: 'small', bordered: false }, { default: () => row.action })
            },
            {
                title: '对象类型',
                key: 'targetType',
                width: 120,
                render: (row) => row.targetType || '—'
            },
            {
                title: '对象名称',
                key: 'targetName',
                width: 180,
                ellipsis: { tooltip: true },
                render: (row) => row.targetName || '—'
            },
            {
                title: '详情',
                key: 'detail',
                ellipsis: { tooltip: true },
                render: (row) => row.detail || '—'
            },
            {
                title: '客户端 IP',
                key: 'clientIp',
                width: 130,
                render: (row) => row.clientIp || '—'
            },
            {
                title: '状态',
                key: 'success',
                width: 90,
                align: 'center',
                render: (row) => h(NTag, {
                    type: row.success ? 'success' : 'error',
                    size: 'small',
                    round: true
                }, { default: () => (row.success ? '成功' : '失败') })
            }
        ];

        const loadOperations = async () => {
            opLoading.value = true;
            try {
                const res = await logsApi.getOperations({
                    page: opPage.value,
                    pageSize: opPageSize.value,
                    keyword: opKeyword.value.trim() || undefined
                });
                opItems.value = res.data?.items || [];
                opTotal.value = res.data?.total || 0;
            } catch (err) {
                console.error('加载操作审计日志失败:', err);
                message.error('加载操作审计日志失败');
            } finally {
                opLoading.value = false;
            }
        };

        const handleOpSearch = () => {
            opPage.value = 1;
            loadOperations();
        };

        const handleOpPageChange = (p) => {
            opPage.value = p;
            loadOperations();
        };

        onMounted(() => {
            loadFiles();
            loadOperations();
        });

        onUnmounted(() => {
            if (autoRefreshTimer) {
                clearInterval(autoRefreshTimer);
                autoRefreshTimer = null;
            }
        });

        const __returned__ = {
            activeTab, filesLoading, files, fileSearch, activeFileName,
            contentLoading, logContent, isTruncated, tailLines, logKeyword,
            autoRefreshInterval, terminalRef, autoScroll, tailOptions, autoRefreshOptions,
            filteredFiles, activeFileObj, displayContent, formatBytes, formatTime,
            loadFiles, selectFile, loadFileContent, copyContent, downloadFile, onTerminalScroll,
            opLoading, opItems, opTotal, opPage, opPageSize, opKeyword, opColumns,
            loadOperations, handleOpSearch, handleOpPageChange,
            get DocumentTextOutline() { return DocumentTextOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get CopyOutline() { return CopyOutline; },
            get ArrowDownOutline() { return ArrowDownOutline; },
            get SearchOutline() { return SearchOutline; },
            get CheckmarkCircleOutline() { return CheckmarkCircleOutline; },
            get AlertCircleOutline() { return AlertCircleOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <div style="display: flex; flex-direction: column; gap: 16px; height: calc(100vh - 120px)">
    <n-card :bordered="false" size="small" style="flex-shrink: 0">
      <div style="display: flex; align-items: center; justify-content: space-between">
        <n-tabs v-model:value="activeTab" type="segment" style="width: 280px">
          <n-tab-pane name="files" tab="系统运行日志" />
          <n-tab-pane name="operations" tab="操作审计日志" />
        </n-tabs>
        <n-space align="center">
          <n-tag v-if="activeTab === 'files' && activeFileObj" type="info" size="small" round>
            {{ activeFileObj.name }} ({{ formatBytes(activeFileObj.lengthBytes) }})
          </n-tag>
          <n-button size="small" secondary @click="activeTab === 'files' ? loadFiles() : loadOperations()">
            <template #icon><n-icon :component="RefreshOutline" /></template>
            刷新列表
          </n-button>
        </n-space>
      </div>
    </n-card>

    <!-- Tab 1: 系统运行日志 -->
    <div v-show="activeTab === 'files'" style="display: flex; gap: 16px; flex: 1; min-height: 0">
      <!-- 左侧：日志文件列表 -->
      <n-card :bordered="false" size="small" style="width: 320px; flex-shrink: 0; display: flex; flex-direction: column" content-style="display: flex; flex-direction: column; height: 100%; padding: 12px">
        <div style="margin-bottom: 12px; display: flex; gap: 8px">
          <n-input v-model:value="fileSearch" placeholder="过滤文件名..." clearable size="small">
            <template #prefix><n-icon :component="SearchOutline" /></template>
          </n-input>
          <n-button size="small" secondary @click="loadFiles(true)" :loading="filesLoading">
            <template #icon><n-icon :component="RefreshOutline" /></template>
          </n-button>
        </div>

        <div style="flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 6px; padding-right: 4px">
          <n-spin :show="filesLoading && !files.length">
            <n-empty v-if="!filteredFiles.length && !filesLoading" description="暂无日志文件" style="margin-top: 40px" />
            <div
              v-for="file in filteredFiles"
              :key="file.name"
              @click="selectFile(file.name)"
              :style="{
                padding: '10px 12px',
                borderRadius: '8px',
                cursor: 'pointer',
                display: 'flex',
                flexDirection: 'column',
                gap: '4px',
                border: activeFileName === file.name ? '1px solid rgba(99, 226, 183, 0.5)' : '1px solid rgba(255, 255, 255, 0.06)',
                backgroundColor: activeFileName === file.name ? 'rgba(99, 226, 183, 0.08)' : 'rgba(255, 255, 255, 0.02)',
                transition: 'all 0.2s ease'
              }"
            >
              <div style="display: flex; align-items: center; justify-content: space-between">
                <span :style="{ fontFamily: 'monospace', fontSize: '13px', fontWeight: 600, color: activeFileName === file.name ? '#63e2b7' : '#e0e0e0', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }">
                  {{ file.name }}
                </span>
                <n-badge v-if="file.isActiveToday" dot type="success" />
              </div>
              <div style="display: flex; justify-content: space-between; font-size: 11px; opacity: 0.6">
                <span>{{ formatBytes(file.lengthBytes) }}</span>
                <span>{{ formatTime(file.lastWriteTime) }}</span>
              </div>
            </div>
          </n-spin>
        </div>
      </n-card>

      <!-- 右侧：日志终端与工具栏 -->
      <n-card :bordered="false" size="small" style="flex: 1; display: flex; flex-direction: column; min-width: 0" content-style="display: flex; flex-direction: column; height: 100%; padding: 12px">
        <!-- 顶部工具栏 -->
        <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; margin-bottom: 12px">
          <div style="display: flex; align-items: center; gap: 8px">
            <n-select v-model:value="tailLines" :options="tailOptions" size="small" style="width: 140px" @update:value="loadFileContent(false)" />
            <n-select v-model:value="autoRefreshInterval" :options="autoRefreshOptions" size="small" style="width: 150px" />
            <n-input v-model:value="logKeyword" placeholder="行内关键字过滤..." size="small" clearable style="width: 180px">
              <template #prefix><n-icon :component="SearchOutline" /></template>
            </n-input>
          </div>

          <div style="display: flex; align-items: center; gap: 8px">
            <n-button size="small" secondary @click="loadFileContent(false)" :loading="contentLoading">
              <template #icon><n-icon :component="RefreshOutline" /></template>
              刷新
            </n-button>
            <n-button size="small" secondary @click="copyContent">
              <template #icon><n-icon :component="CopyOutline" /></template>
              复制
            </n-button>
            <n-button size="small" secondary @click="downloadFile">
              <template #icon><n-icon :component="ArrowDownOutline" /></template>
              下载全量
            </n-button>
          </div>
        </div>

        <div v-if="isTruncated" style="margin-bottom: 8px">
          <n-tag type="warning" size="small" style="width: 100%; justify-content: center">
            ⚠️ 尾部输出已达 512KB 硬限制并截断。完整内容请点击上方「下载全量」获取。
          </n-tag>
        </div>

        <!-- 终端样式视窗 -->
        <div
          ref="terminalRef"
          @scroll="onTerminalScroll"
          :style="{
            flex: 1,
            backgroundColor: '#0f141c',
            border: '1px solid rgba(255, 255, 255, 0.08)',
            borderRadius: '6px',
            padding: '12px 16px',
            fontFamily: 'Consolas, Monaco, monospace',
            fontSize: '12px',
            lineHeight: '1.6',
            color: '#d1d5db',
            overflowY: 'auto',
            whiteSpace: 'pre-wrap',
            wordBreak: 'break-all',
            userSelect: 'text'
          }"
        >
          <n-spin :show="contentLoading && !logContent">
            <template v-if="!displayContent">
              <span style="color: #6b7280">(日志内容为空)</span>
            </template>
            <template v-else>
              {{ displayContent }}
            </template>
          </n-spin>
        </div>
      </n-card>
    </div>

    <!-- Tab 2: 操作审计日志 -->
    <div v-show="activeTab === 'operations'" style="flex: 1; display: flex; flex-direction: column; min-height: 0">
      <n-card :bordered="false" size="small" style="flex: 1; display: flex; flex-direction: column" content-style="display: flex; flex-direction: column; height: 100%; padding: 16px">
        <div style="display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px">
          <div style="display: flex; align-items: center; gap: 8px">
            <n-input v-model:value="opKeyword" placeholder="搜索操作、对象或详情..." clearable size="small" style="width: 260px" @keyup.enter="handleOpSearch">
              <template #prefix><n-icon :component="SearchOutline" /></template>
            </n-input>
            <n-button size="small" type="primary" @click="handleOpSearch">查询</n-button>
            <n-button size="small" secondary @click="loadOperations" :loading="opLoading">刷新</n-button>
          </div>
          <span style="font-size: 13px; opacity: 0.7">共计 {{ opTotal }} 条审计记录</span>
        </div>

        <div style="flex: 1; overflow-y: auto; margin-bottom: 16px">
          <n-data-table
            :columns="opColumns"
            :data="opItems"
            :loading="opLoading"
            size="small"
            :bordered="false"
          />
        </div>

        <div style="display: flex; justify-content: flex-end">
          <n-pagination
            v-model:page="opPage"
            v-model:page-size="opPageSize"
            :item-count="opTotal"
            show-size-picker
            :page-sizes="[10, 20, 50, 100]"
            @update:page="handleOpPageChange"
            @update:page-size="handleOpSearch"
          />
        </div>
      </n-card>
    </div>
  </div>
`;

export default component;
