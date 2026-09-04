import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, h, onMounted, onUnmounted, reactive } from 'vue';
import { useMessage, useDialog, NTag } from 'naive-ui';
import { CheckmarkCircleOutline, AlertCircleOutline, WarningOutline, RefreshOutline, TrashOutline } from '@vicons/ionicons5';
import { systemApi, indexJobsApi, performanceApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'System',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        const dialog = useDialog();
        // Status
        const statusLoading = ref(false);
        const systemStatus = ref(null);
        const indexMetrics = ref(null);
        // Query Metrics
        const metricsLoading = ref(false);
        const queryMetrics = ref(null);
        // System Metrics
        const systemMetricsLoading = ref(false);
        const systemMetrics = ref(null);
        // Alerts
        const alertsLoading = ref(false);
        const alerts = ref([]);
        // Health
        const healthLoading = ref(false);
        const healthResult = ref(null);
        const checkingAlerts = ref(false);
        // Restart
        // const restartLoading = ref(false)
        // Active Jobs
        const jobsLoading = ref(false);
        const activeJobs = ref([]);
        const stoppingJobs = reactive({});
        // Completed Jobs
        const completedJobsLoading = ref(false);
        const completedJobs = ref([]);
        const clearingJobs = ref(false);
        // Search Phase Trace
        const traceLoading = ref(false);
        const resettingTrace = ref(false);
        const searchTrace = ref(null);
        const p95Tag = (val, count) => {
            if (count === 0)
                return '—';
            const type = val > 500 ? 'error' : val > 200 ? 'warning' : 'success';
            return h(NTag, { type, size: 'small', round: true }, () => `${val} ms`);
        };
        const fmtMs = (val, count) => count > 0 ? `${val} ms` : '—';
        const traceColumns = [
            { title: '阶段', key: 'name', width: 180 },
            { title: '样本数', key: 'count', width: 80, align: 'center' },
            {
                title: '均值', key: 'avg', width: 90, align: 'right',
                render: (row) => fmtMs(Math.round(row.avg), row.count)
            },
            {
                title: 'P50', key: 'p50', width: 90, align: 'right',
                render: (row) => fmtMs(row.p50, row.count)
            },
            {
                title: 'P95', key: 'p95', width: 110, align: 'right',
                render: (row) => p95Tag(row.p95, row.count)
            },
            {
                title: 'P99', key: 'p99', width: 90, align: 'right',
                render: (row) => fmtMs(row.p99, row.count)
            },
            {
                title: '最大', key: 'max', width: 90, align: 'right',
                render: (row) => fmtMs(row.max, row.count)
            }
        ];
        const flatStats = (s) => ({
            count: s.count, avg: s.average, p50: s.p50, p95: s.p95, p99: s.p99, max: s.max
        });
        const traceTableData = computed(() => {
            if (!searchTrace.value)
                return [];
            const t = searchTrace.value;
            return [
                { name: 'ONNX 推理', ...flatStats(t.embed) },
                { name: '标题搜索', ...flatStats(t.title) },
                { name: '向量检索', ...flatStats(t.vector) },
                { name: '混合搜索（BM25+向量）', ...flatStats(t.hybrid) },
                { name: '图扩展', ...flatStats(t.graph) },
                { name: 'Cross-Encoder 重排', ...flatStats(t.rerank) },
            ];
        });
        let refreshInterval = null;
        const getStatusColor = (status) => {
            switch (status) {
                case 'healthy': return '#18a058';
                case 'degraded': return '#f0a020';
                case 'unhealthy': return '#d03050';
                default: return '#909399';
            }
        };
        const getStatusIcon = (status) => {
            switch (status) {
                case 'healthy': return CheckmarkCircleOutline;
                case 'degraded': return WarningOutline;
                case 'unhealthy': return AlertCircleOutline;
                default: return WarningOutline;
            }
        };
        const getStatusText = (status) => {
            switch (status) {
                case 'healthy': return '健康';
                case 'degraded': return '降级';
                case 'unhealthy': return '异常';
                default: return '未知';
            }
        };
        const getAlertSeverityType = (severity) => {
            switch (severity) {
                case 'Critical': return 'error';
                case 'Warning': return 'warning';
                case 'Info': return 'info';
                default: return 'default';
            }
        };
        const getJobStatusType = (status) => {
            switch (status) {
                case 'Running': return 'success';
                case 'Pending': return 'info';
                case 'Completed': return 'success';
                case 'Failed': return 'error';
                case 'Cancelled': return 'warning';
                default: return 'default';
            }
        };
        const getProgressStatus = (status) => {
            switch (status) {
                case 'Completed': return 'success';
                case 'Failed': return 'error';
                case 'Cancelled': return 'warning';
                default: return 'default';
            }
        };
        const truncateFileName = (fileName, maxLength = 50) => {
            if (fileName.length <= maxLength)
                return fileName;
            return '...' + fileName.slice(-(maxLength - 3));
        };
        const formatBytes = (bytes) => {
            if (bytes === 0)
                return '0 B';
            const k = 1024;
            const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
            const i = Math.floor(Math.log(bytes) / Math.log(k));
            return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
        };
        const formatUptime = (seconds) => {
            const days = Math.floor(seconds / 86400);
            const hours = Math.floor((seconds % 86400) / 3600);
            const mins = Math.floor((seconds % 3600) / 60);
            if (days > 0)
                return `${days}天 ${hours}小时`;
            if (hours > 0)
                return `${hours}小时 ${mins}分钟`;
            return `${mins}分钟`;
        };
        const formatTime = (timestamp) => {
            if (!timestamp)
                return '-';
            return new Date(timestamp).toLocaleString('zh-CN');
        };
        const loadStatus = async () => {
            statusLoading.value = true;
            try {
                const response = await systemApi.getStatus();
                systemStatus.value = response.data;
                indexMetrics.value = response.data.index;
            }
            catch (error) {
                console.error('Failed to load status:', error);
            }
            finally {
                statusLoading.value = false;
            }
        };
        const loadQueryMetrics = async () => {
            metricsLoading.value = true;
            try {
                const response = await systemApi.getMetricsSummary();
                queryMetrics.value = response.data;
            }
            catch (error) {
                console.error('Failed to load query metrics:', error);
            }
            finally {
                metricsLoading.value = false;
            }
        };
        const loadSystemMetrics = async () => {
            systemMetricsLoading.value = true;
            try {
                const response = await systemApi.getMetrics();
                systemMetrics.value = response.data;
            }
            catch (error) {
                console.error('Failed to load system metrics:', error);
            }
            finally {
                systemMetricsLoading.value = false;
            }
        };
        const loadAlerts = async () => {
            alertsLoading.value = true;
            try {
                const response = await systemApi.getAlerts?.();
                alerts.value = response.data || [];
            }
            catch (error) {
                console.error('Failed to load alerts:', error);
                alerts.value = [];
            }
            finally {
                alertsLoading.value = false;
            }
        };
        const loadActiveJobs = async () => {
            jobsLoading.value = true;
            try {
                const response = await indexJobsApi.getActive();
                activeJobs.value = response.data || [];
            }
            catch (error) {
                console.error('Failed to load active jobs:', error);
                activeJobs.value = [];
            }
            finally {
                jobsLoading.value = false;
            }
        };
        const checkHealth = async () => {
            healthLoading.value = true;
            try {
                const response = await systemApi.getHealth();
                healthResult.value = response.data;
                message.success('健康检查完成');
            }
            catch (error) {
                console.error('Health check failed:', error);
                message.error('健康检查失败');
            }
            finally {
                healthLoading.value = false;
            }
        };
        const checkAlerts = async () => {
            checkingAlerts.value = true;
            try {
                const response = await systemApi.checkAlerts?.();
                message.success(`检测到 ${response.data?.length || 0} 个告警`);
                await loadAlerts();
            }
            catch (error) {
                console.error('Alert check failed:', error);
                message.error('告警检查失败');
            }
            finally {
                checkingAlerts.value = false;
            }
        };
        // const handleRestart = () => {
        //   dialog.warning({
        //     title: '确认重启',
        //     content: '重启服务将暂时中断所有正在进行的操作。确定要重启吗？',
        //     positiveText: '确认重启',
        //     negativeText: '取消',
        //     onPositiveClick: async () => {
        //       restartLoading.value = true
        //       try {
        //         const response = await systemApi.restart()
        //         message.success(`服务正在重启，新进程 ID: ${response.data.newProcessId}`)
        //         // 等待几秒后尝试重新连接
        //         setTimeout(() => {
        //           loadAll()
        //         }, 5000)
        //       } catch (error: any) {
        //         console.error('Restart failed:', error)
        //         message.error(`重启失败: ${error.response?.data?.error || error.message}`)
        //       } finally {
        //         restartLoading.value = false
        //       }
        //     }
        //   })
        // }
        const handleStopJob = async (jobId) => {
            dialog.warning({
                title: '确认中断任务',
                content: `确定要中断索引任务 ${jobId} 吗？已处理的文件将保留。`,
                positiveText: '确认中断',
                negativeText: '取消',
                onPositiveClick: async () => {
                    stoppingJobs[jobId] = true;
                    try {
                        await indexJobsApi.cancelJob(jobId);
                        message.success(`任务 ${jobId} 已中断`);
                        await loadActiveJobs();
                        await loadCompletedJobs();
                    }
                    catch (error) {
                        console.error('Stop job failed:', error);
                        message.error(`中断任务失败: ${error.response?.data?.error || error.message}`);
                    }
                    finally {
                        stoppingJobs[jobId] = false;
                    }
                }
            });
        };
        const loadCompletedJobs = async () => {
            completedJobsLoading.value = true;
            try {
                const response = await indexJobsApi.getHistory();
                completedJobs.value = response.data || [];
            }
            catch (error) {
                console.error('Failed to load completed jobs:', error);
                completedJobs.value = [];
            }
            finally {
                completedJobsLoading.value = false;
            }
        };
        const handleClearCompletedJobs = () => {
            dialog.warning({
                title: '确认清空记录',
                content: '确定要清空所有已完成/已取消的索引任务记录吗？',
                positiveText: '确认清空',
                negativeText: '取消',
                onPositiveClick: async () => {
                    clearingJobs.value = true;
                    try {
                        await indexJobsApi.clearHistory();
                        message.success('已清空所有已完成的索引任务记录');
                        completedJobs.value = [];
                    }
                    catch (error) {
                        console.error('Clear completed jobs failed:', error);
                        message.error(`清空失败: ${error.response?.data?.error || error.message}`);
                    }
                    finally {
                        clearingJobs.value = false;
                    }
                }
            });
        };
        const loadSearchTrace = async () => {
            traceLoading.value = true;
            try {
                const res = await performanceApi.getSearchTrace();
                searchTrace.value = res.data;
            }
            catch (e) {
                console.error('Failed to load search trace:', e);
            }
            finally {
                traceLoading.value = false;
            }
        };
        const handleResetTrace = () => {
            dialog.warning({
                title: '确认重置追踪统计',
                content: '重置后所有阶段耗时数据将清零，不可恢复。',
                positiveText: '确认重置',
                negativeText: '取消',
                onPositiveClick: async () => {
                    resettingTrace.value = true;
                    try {
                        await performanceApi.resetSearchTrace();
                        searchTrace.value = null;
                        message.success('追踪统计已重置');
                    }
                    catch (e) {
                        message.error('重置失败');
                    }
                    finally {
                        resettingTrace.value = false;
                    }
                }
            });
        };
        const loadAll = () => {
            loadStatus();
            loadQueryMetrics();
            loadSystemMetrics();
            loadAlerts();
            loadActiveJobs();
            loadCompletedJobs();
            loadSearchTrace();
        };
        onMounted(() => {
            loadAll();
            // Auto refresh every 30 seconds
            refreshInterval = window.setInterval(loadAll, 30000);
        });
        onUnmounted(() => {
            if (refreshInterval) {
                clearInterval(refreshInterval);
            }
        });
        const __returned__ = { message, dialog, statusLoading, systemStatus, indexMetrics, metricsLoading, queryMetrics, systemMetricsLoading, systemMetrics, alertsLoading, alerts, healthLoading, healthResult, checkingAlerts, jobsLoading, activeJobs, stoppingJobs, completedJobsLoading, completedJobs, clearingJobs, traceLoading, resettingTrace, searchTrace, p95Tag, fmtMs, traceColumns, flatStats, traceTableData, get refreshInterval() { return refreshInterval; }, set refreshInterval(v) { refreshInterval = v; }, getStatusColor, getStatusIcon, getStatusText, getAlertSeverityType, getJobStatusType, getProgressStatus, truncateFileName, formatBytes, formatUptime, formatTime, loadStatus, loadQueryMetrics, loadSystemMetrics, loadAlerts, loadActiveJobs, checkHealth, checkAlerts, handleStopJob, loadCompletedJobs, handleClearCompletedJobs, loadSearchTrace, handleResetTrace, loadAll, get NTag() { return NTag; }, get RefreshOutline() { return RefreshOutline; }, get TrashOutline() { return TrashOutline; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- System Status -->\n    <n-card title=\"系统状态\">\n      <!-- <template #header-extra>\n        <n-space>\n          <n-button type=\"warning\" @click=\"handleRestart\" :loading=\"restartLoading\" :disabled=\"restartLoading\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            重启服务\n          </n-button>\n        </n-space>\n      </template> -->\n      <n-spin :show=\"statusLoading\">\n        <n-grid :cols=\"4\" :x-gap=\"20\">\n          <n-gi>\n            <n-statistic label=\"系统状态\">\n              <template #prefix>\n                <n-icon :color=\"getStatusColor(systemStatus?.status)\">\n                  <component :is=\"getStatusIcon(systemStatus?.status)\" />\n                </n-icon>\n              </template>\n              <span :style=\"{ color: getStatusColor(systemStatus?.status) }\">\n                {{ getStatusText(systemStatus?.status) }}\n              </span>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"活动告警\" :value=\"systemStatus?.activeAlerts?.length || 0\">\n              <template #suffix>\n                <n-text depth=\"3\">个</n-text>\n              </template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"总文件数\" :value=\"indexMetrics?.totalFiles || 0\" />\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"总切片数\" :value=\"indexMetrics?.totalChunks || 0\" />\n          </n-gi>\n        </n-grid>\n      </n-spin>\n    </n-card>\n\n    <!-- Active Index Jobs -->\n    <n-card title=\"正在进行的向量索引任务\">\n      <template #header-extra>\n        <n-button text @click=\"loadActiveJobs\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </template>\n      <n-spin :show=\"jobsLoading\">\n        <n-empty v-if=\"!activeJobs?.length\" description=\"暂无正在进行的索引任务\" />\n        <n-list v-else>\n          <n-list-item v-for=\"job in activeJobs\" :key=\"job.jobId\">\n            <n-thing>\n              <template #header>\n                <n-space align=\"center\">\n                  <n-tag :type=\"getJobStatusType(job.status)\" size=\"small\">\n                    {{ job.status }}\n                  </n-tag>\n                  <n-text strong>任务 {{ job.jobId }}</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    ({{ job.sources?.join(', ') || '所有源' }})\n                  </n-text>\n                </n-space>\n              </template>\n              <template #description>\n                <n-space vertical :size=\"8\">\n                  <n-progress\n                    type=\"line\"\n                    :percentage=\"job.progressPercent\"\n                    :status=\"getProgressStatus(job.status)\"\n                    :indicator-placement=\"'inside'\"\n                  />\n                  <n-space>\n                    <n-text depth=\"3\">\n                      进度: {{ job.processedFiles }} / {{ job.totalFiles }} 文件\n                    </n-text>\n                    <n-text depth=\"3\" v-if=\"job.currentFile\">\n                      | 当前: {{ truncateFileName(job.currentFile) }}\n                    </n-text>\n                    <n-text depth=\"3\" v-if=\"job.errors > 0\">\n                      | 错误: <n-text type=\"error\">{{ job.errors }}</n-text>\n                    </n-text>\n                  </n-space>\n                  <n-space v-if=\"job.startTime\">\n                    <n-text depth=\"3\">\n                      开始时间: {{ formatTime(job.startTime) }}\n                    </n-text>\n                    <n-text depth=\"3\" v-if=\"job.duration\">\n                      | 已用时: {{ job.duration }}\n                    </n-text>\n                  </n-space>\n                </n-space>\n              </template>\n              <template #header-extra>\n                <n-button\n                  v-if=\"job.status === 'Running' || job.status === 'Pending'\"\n                  type=\"error\"\n                  size=\"small\"\n                  :loading=\"stoppingJobs[job.jobId]\"\n                  @click=\"handleStopJob(job.jobId)\"\n                >\n                  中断任务\n                </n-button>\n              </template>\n            </n-thing>\n          </n-list-item>\n        </n-list>\n      </n-spin>\n    </n-card>\n\n    <!-- Completed Index Jobs -->\n    <n-card>\n      <template #header>\n        <n-space align=\"center\">\n          <n-text>已完成/已取消的索引任务</n-text>\n          <n-tag size=\"small\" type=\"info\">最多保留 20 条</n-tag>\n        </n-space>\n      </template>\n      <template #header-extra>\n        <n-space>\n          <n-button\n            type=\"error\"\n            text\n            :disabled=\"!completedJobs?.length\"\n            :loading=\"clearingJobs\"\n            @click=\"handleClearCompletedJobs\"\n          >\n            <template #icon><n-icon :component=\"TrashOutline\" /></template>\n            清空记录\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"jobsLoading\">\n        <n-empty v-if=\"!completedJobs?.length\" description=\"暂无已完成的索引记录\" />\n        <n-list v-else>\n          <n-list-item v-for=\"job in completedJobs\" :key=\"job.jobId\">\n            <n-thing>\n              <template #header>\n                <n-space align=\"center\">\n                  <n-tag :type=\"getJobStatusType(job.status)\" size=\"small\">\n                    {{ job.status === 'Completed' ? '已完成' : '已取消' }}\n                  </n-tag>\n                  <n-text strong>任务 {{ job.jobId }}</n-text>\n                  <n-text depth=\"3\" style=\"font-size: 12px\">\n                    ({{ job.sources?.join(', ') || '所有源' }})\n                  </n-text>\n                </n-space>\n              </template>\n              <template #description>\n                <n-space>\n                  <n-text depth=\"3\">\n                    处理: {{ job.processedFiles }} / {{ job.totalFiles }} 文件\n                  </n-text>\n                  <n-text depth=\"3\" v-if=\"job.errors > 0\">\n                    | 错误: <n-text type=\"error\">{{ job.errors }}</n-text>\n                  </n-text>\n                  <n-text depth=\"3\" v-if=\"job.duration\">\n                    | 耗时: {{ job.duration }}\n                  </n-text>\n                </n-space>\n              </template>\n              <template #header-extra>\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  {{ formatTime(job.endTime) }}\n                </n-text>\n              </template>\n            </n-thing>\n          </n-list-item>\n        </n-list>\n      </n-spin>\n    </n-card>\n\n    <!-- Query Metrics -->\n    <n-card title=\"查询指标\">\n      <n-spin :show=\"metricsLoading\">\n        <n-grid :cols=\"5\" :x-gap=\"20\">\n          <n-gi>\n            <n-statistic label=\"总查询次数\" :value=\"queryMetrics?.totalQueries || 0\" />\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"平均延迟\">\n              <template #default>\n                {{ (queryMetrics?.avgQueryLatencyMs || 0).toFixed(1) }}\n              </template>\n              <template #suffix>ms</template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"P95延迟\">\n              <template #default>\n                {{ (queryMetrics?.p95QueryLatencyMs || 0).toFixed(1) }}\n              </template>\n              <template #suffix>ms</template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"P99延迟\">\n              <template #default>\n                {{ (queryMetrics?.p99QueryLatencyMs || 0).toFixed(1) }}\n              </template>\n              <template #suffix>ms</template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"平均结果数\">\n              <template #default>\n                {{ (queryMetrics?.avgResultsPerQuery || 0).toFixed(1) }}\n              </template>\n            </n-statistic>\n          </n-gi>\n        </n-grid>\n      </n-spin>\n    </n-card>\n\n    <!-- Search Phase Trace -->\n    <n-card>\n      <template #header>\n        <n-space align=\"center\">\n          <n-text>请求追踪统计</n-text>\n          <n-tag v-if=\"searchTrace && searchTrace.totalSearches > 0\" size=\"small\" type=\"info\">\n            共 {{ searchTrace.totalSearches }} 次搜索\n          </n-tag>\n        </n-space>\n      </template>\n      <template #header-extra>\n        <n-space>\n          <n-button text @click=\"loadSearchTrace\">\n            <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n            刷新\n          </n-button>\n          <n-button text type=\"error\" :loading=\"resettingTrace\" @click=\"handleResetTrace\">\n            <template #icon><n-icon :component=\"TrashOutline\" /></template>\n            重置\n          </n-button>\n        </n-space>\n      </template>\n      <n-spin :show=\"traceLoading\">\n        <n-empty v-if=\"!searchTrace || searchTrace.totalSearches === 0\" description=\"尚无搜索追踪记录\" />\n        <n-data-table\n          v-else\n          :columns=\"traceColumns\"\n          :data=\"traceTableData\"\n          size=\"small\"\n          :bordered=\"false\"\n        />\n      </n-spin>\n    </n-card>\n\n    <!-- System Metrics -->\n    <n-card title=\"系统资源\">\n      <n-spin :show=\"systemMetricsLoading\">\n        <n-grid :cols=\"4\" :x-gap=\"20\">\n          <n-gi>\n            <n-statistic label=\"CPU使用率\">\n              <template #default>\n                {{ (systemMetrics?.cpuUsagePercent || 0).toFixed(1) }}\n              </template>\n              <template #suffix>%</template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"内存使用\">\n              <template #default>\n                {{ formatBytes(systemMetrics?.memoryUsedBytes || 0) }}\n              </template>\n              <template #suffix>\n                <n-text depth=\"3\">/ {{ formatBytes(systemMetrics?.memoryTotalBytes || 0) }}</n-text>\n              </template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"磁盘使用\">\n              <template #default>\n                {{ formatBytes(systemMetrics?.diskUsedBytes || 0) }}\n              </template>\n              <template #suffix>\n                <n-text depth=\"3\">/ {{ formatBytes(systemMetrics?.diskTotalBytes || 0) }}</n-text>\n              </template>\n            </n-statistic>\n          </n-gi>\n          <n-gi>\n            <n-statistic label=\"运行时间\">\n              <template #default>\n                {{ formatUptime(systemMetrics?.uptimeSeconds || 0) }}\n              </template>\n            </n-statistic>\n          </n-gi>\n        </n-grid>\n      </n-spin>\n    </n-card>\n\n    <!-- Active Alerts -->\n    <n-card title=\"活动告警\">\n      <template #header-extra>\n        <n-button text @click=\"loadAlerts\">\n          <template #icon><n-icon :component=\"RefreshOutline\" /></template>\n          刷新\n        </n-button>\n      </template>\n      <n-spin :show=\"alertsLoading\">\n        <n-empty v-if=\"!alerts?.length\" description=\"暂无活动告警\" />\n        <n-list v-else>\n          <n-list-item v-for=\"alert in alerts\" :key=\"alert.name\">\n            <n-thing>\n              <template #header>\n                <n-space align=\"center\">\n                  <n-tag :type=\"getAlertSeverityType(alert.severity)\" size=\"small\">\n                    {{ alert.severity }}\n                  </n-tag>\n                  <n-text>{{ alert.name }}</n-text>\n                </n-space>\n              </template>\n              <template #description>\n                <n-text depth=\"3\">{{ alert.message }}</n-text>\n              </template>\n              <template #header-extra>\n                <n-text depth=\"3\" style=\"font-size: 12px\">\n                  {{ formatTime(alert.triggeredAt) }}\n                </n-text>\n              </template>\n            </n-thing>\n          </n-list-item>\n        </n-list>\n      </n-spin>\n    </n-card>\n\n    <!-- Health Check -->\n    <n-card title=\"健康检查\">\n      <n-space>\n        <n-button type=\"primary\" :loading=\"healthLoading\" @click=\"checkHealth\">\n          执行健康检查\n        </n-button>\n        <n-button @click=\"checkAlerts\" :loading=\"checkingAlerts\">\n          检查告警\n        </n-button>\n      </n-space>\n      <n-card v-if=\"healthResult\" size=\"small\" style=\"margin-top: 16px\">\n        <n-descriptions :column=\"3\" label-placement=\"left\">\n          <n-descriptions-item label=\"状态\">\n            <n-tag :type=\"healthResult.status === 'healthy' ? 'success' : 'error'\">\n              {{ healthResult.status }}\n            </n-tag>\n          </n-descriptions-item>\n          <n-descriptions-item label=\"版本\">{{ healthResult.version }}</n-descriptions-item>\n          <n-descriptions-item label=\"检查时间\">{{ formatTime(healthResult.timestamp) }}</n-descriptions-item>\n        </n-descriptions>\n      </n-card>\n    </n-card>\n  </n-space>\n";
export default component;
