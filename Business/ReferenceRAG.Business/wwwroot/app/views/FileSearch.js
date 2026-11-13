import { defineComponent as _defineComponent, ref, computed, watch, onMounted, onActivated } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import {
    useMessage, NSpace, NCard, NInput, NButton, NIcon, NTag, NText, NSelect,
    NInputNumber, NCollapse, NCollapseItem, NEmpty, NSpin, NAlert, NGrid,
    NGi, NFormItem, NTooltip, NDivider, NSwitch
} from 'naive-ui';
import {
    SearchOutline, BookOutline, FolderOutline, CloseOutline, RefreshOutline,
    OptionsOutline, CodeSlashOutline, DocumentTextOutline
} from '@vicons/ionicons5';
import { fileSystemApi, sourcesApi } from '/app/api/index.js';
import { SearchHitAdapter } from '/app/services/reader/SearchHitAdapter.js';
import { TextLayout, resolveLineStyle, resolveGutterStyle, resolveRowStyle } from '/app/services/reader/TextLayoutStrategy.js';
import { LayoutPreferenceKey, readLayoutPreference, writeLayoutPreference } from '/app/services/reader/LayoutPreference.js';

const component = /*@__PURE__*/ _defineComponent({
    name: 'FileSearch',
    setup(__props, { expose: __expose }) {
        __expose();
        const router = useRouter();
        const route = useRoute();
        const message = useMessage();

        const searchQuery = ref('');
        const isRegex = ref(false);
        const caseSensitive = ref(false);
        const selectedSources = ref([]);
        const subPath = ref('');
        const filePattern = ref('');
        const contextLines = ref(1);
        const maxMatches = ref(100);

        const showAdvanced = ref(false);
        // 长行折行：自然语言/Markdown 默认折行，代码可在单行模式下横向滚动（偏好持久化）
        const wrapLines = ref(readLayoutPreference(LayoutPreferenceKey.FILE_SEARCH_WRAP, true));
        watch(wrapLines, (value) => {
            writeLayoutPreference(LayoutPreferenceKey.FILE_SEARCH_WRAP, value);
        });
        const lineTextStyle = computed(() => resolveLineStyle(
            wrapLines.value ? TextLayout.WRAP : TextLayout.NO_WRAP
        ));
        const rowStyle = computed(() => resolveRowStyle());
        const gutterStyle = computed(() => resolveGutterStyle());
        const searching = ref(false);
        const searched = ref(false);
        const searchError = ref('');
        const searchResult = ref(null);
        const sources = ref([]);
        const sourceLoading = ref(false);

        // 已展开的文件列表
        const expandedFiles = ref([]);

        const sourceOptions = computed(() =>
            sources.value.filter(s => s.enabled !== false).map(s => ({
                label: s.name,
                value: s.name
            }))
        );

        const loadSources = async () => {
            sourceLoading.value = true;
            try {
                const res = await sourcesApi.getAll();
                sources.value = res.data || [];
            } catch (err) {
                console.warn('获取数据源失败:', err);
            } finally {
                sourceLoading.value = false;
            }
        };

        const handleClearQuery = () => {
            searchQuery.value = '';
        };

        const handleResetOptions = () => {
            isRegex.value = false;
            caseSensitive.value = false;
            selectedSources.value = [];
            subPath.value = '';
            filePattern.value = '';
            contextLines.value = 1;
            maxMatches.value = 100;
        };

        const updateRouteQuery = () => {
            const query = {};
            if (searchQuery.value.trim()) query.q = searchQuery.value.trim();
            if (selectedSources.value.length === 1) query.source = selectedSources.value[0];
            else if (selectedSources.value.length > 1) query.sources = selectedSources.value.join(',');
            if (subPath.value.trim()) query.subPath = subPath.value.trim();
            if (filePattern.value.trim()) query.filePattern = filePattern.value.trim();
            if (isRegex.value) query.isRegex = 'true';
            if (caseSensitive.value) query.caseSensitive = 'true';

            const currentQ = route.query || {};
            const isDifferent = Object.keys(query).some(k => query[k] !== currentQ[k]) ||
                                Object.keys(currentQ).some(k => query[k] !== currentQ[k]);
            if (isDifferent) {
                router.replace({ query }).catch(() => {});
            }
        };

        const handleSearch = async (syncUrl = true) => {
            const query = searchQuery.value.trim();
            if (!query) {
                message.warning('请输入搜索关键词或正则表达式');
                return;
            }

            if (syncUrl) {
                updateRouteQuery();
            }

            searching.value = true;
            searched.value = true;
            searchError.value = '';
            searchResult.value = null;

            try {
                const req = {
                    pattern: query,
                    isRegex: isRegex.value,
                    caseSensitive: caseSensitive.value,
                    sources: selectedSources.value.length > 0 ? selectedSources.value : null,
                    subPath: subPath.value.trim() || null,
                    filePattern: filePattern.value.trim() || null,
                    contextLines: contextLines.value,
                    maxMatches: maxMatches.value
                };

                const res = await fileSystemApi.search(req);
                searchResult.value = res.data;

                if (res.data?.files?.length > 0) {
                    // 默认展开前 3 个匹配文件
                    expandedFiles.value = res.data.files.slice(0, 3).map(f => f.fullPath);
                } else {
                    expandedFiles.value = [];
                }
            } catch (err) {
                console.error('文件搜索失败:', err);
                const msg = err.response?.data?.error || err.message || '文件搜索执行异常';
                searchError.value = msg;
                message.error(`搜索失败: ${msg}`);
            } finally {
                searching.value = false;
            }
        };

        const handleKeyDown = (e) => {
            if (e.shiftKey) return;
            e.preventDefault();
            handleSearch();
        };

        // 一键直达阅读器
        const jumpToReader = (file, line) => {
            const navTarget = SearchHitAdapter.fromFileMatch(file, line, searchQuery.value, isRegex.value);
            const routeLoc = SearchHitAdapter.toRouteLocation(navTarget);
            router.push(routeLoc);
        };

        // 跳转阅读整个文件头部
        const jumpToFileTop = (file) => {
            const navTarget = {
                sourceName: file.sourceName,
                filePath: file.fullPath,
                fileName: file.relativePath,
                targetLine: 1,
                highlightPattern: searchQuery.value,
                isRegex: isRegex.value
            };
            const routeLoc = SearchHitAdapter.toRouteLocation(navTarget);
            router.push(routeLoc);
        };

        const syncFromRoute = async () => {
            const q = route.query;
            if (!q || Object.keys(q).length === 0) return;

            let hasNewParams = false;
            if (q.source) {
                selectedSources.value = [String(q.source)];
                hasNewParams = true;
            } else if (q.sources) {
                selectedSources.value = String(q.sources).split(',').filter(Boolean);
                hasNewParams = true;
            }

            if (q.subPath !== undefined) {
                subPath.value = String(q.subPath);
                hasNewParams = true;
            }

            if (q.filePattern !== undefined) {
                filePattern.value = String(q.filePattern);
                hasNewParams = true;
            }

            if (q.isRegex !== undefined) {
                isRegex.value = q.isRegex === 'true' || q.isRegex === '1';
                hasNewParams = true;
            }

            if (q.caseSensitive !== undefined) {
                caseSensitive.value = q.caseSensitive === 'true' || q.caseSensitive === '1';
                hasNewParams = true;
            }

            const keyword = q.q || q.pattern;
            if (keyword && String(keyword) !== searchQuery.value) {
                searchQuery.value = String(keyword);
                await handleSearch(false);
            } else if (hasNewParams && searchQuery.value.trim()) {
                await handleSearch(false);
            }
        };

        onMounted(async () => {
            await loadSources();
            await syncFromRoute();
        });

        onActivated(async () => {
            await loadSources();
            await syncFromRoute();
        });

        watch(() => route.query, async (newVal, oldVal) => {
            if (JSON.stringify(newVal) !== JSON.stringify(oldVal)) {
                await syncFromRoute();
            }
        });

        const __returned__ = {
            searchQuery, isRegex, caseSensitive, selectedSources, subPath, filePattern,
            contextLines, maxMatches, showAdvanced, wrapLines, lineTextStyle, rowStyle, gutterStyle,
            searching, searched, searchError,
            searchResult, sources, sourceLoading, expandedFiles, sourceOptions,
            loadSources, handleClearQuery, handleResetOptions, handleSearch, handleKeyDown,
            jumpToReader, jumpToFileTop,
            get SearchOutline() { return SearchOutline; },
            get BookOutline() { return BookOutline; },
            get FolderOutline() { return FolderOutline; },
            get CloseOutline() { return CloseOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get OptionsOutline() { return OptionsOutline; },
            get CodeSlashOutline() { return CodeSlashOutline; },
            get DocumentTextOutline() { return DocumentTextOutline; }
        };
        return __returned__;
    }
});

component.template = `
  <n-space vertical :size="16">
    <!-- 搜索输入区与核心控制栏 -->
    <n-card>
      <n-space vertical :size="12">
        <div style="display: flex; align-items: flex-start; gap: 12px">
          <n-input
            v-model:value="searchQuery"
            type="textarea"
            placeholder="输入文本关键词或正则表达式... (Enter 触发搜索, Shift+Enter 换行)"
            :rows="2"
            autosize
            style="flex: 1; min-width: 0"
            @keydown.enter="handleKeyDown"
          >
            <template #suffix>
              <n-button
                v-if="searchQuery"
                text
                @click="handleClearQuery"
                style="padding: 0 4px"
              >
                <template #icon>
                  <n-icon :component="CloseOutline" />
                </template>
              </n-button>
            </template>
          </n-input>
          <n-button
            type="primary"
            :loading="searching"
            @click="handleSearch"
            :disabled="!searchQuery.trim()"
            style="height: 56px; padding: 0 24px"
          >
            <template #icon><n-icon :component="SearchOutline" /></template>
            搜索
          </n-button>
        </div>

        <!-- 模式开关与高级选项切换 -->
        <div style="display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px">
          <n-space align="center" :size="16">
            <n-tooltip trigger="hover">
              <template #trigger>
                <n-space align="center" :size="4" style="cursor: pointer">
                  <n-switch v-model:value="isRegex" size="small" />
                  <n-text style="font-size: 13px">正则表达式 (.*)</n-text>
                </n-space>
              </template>
              启用基于 .NET 10 NonBacktracking 的线性时间正则扫描，防 ReDoS 攻击
            </n-tooltip>

            <n-tooltip trigger="hover">
              <template #trigger>
                <n-space align="center" :size="4" style="cursor: pointer">
                  <n-switch v-model:value="caseSensitive" size="small" />
                  <n-text style="font-size: 13px">区分大小写 (Aa)</n-text>
                </n-space>
              </template>
              严格区分大小写字母检索
            </n-tooltip>
          </n-space>

          <n-button text size="small" @click="showAdvanced = !showAdvanced">
            <template #icon>
              <n-icon :component="OptionsOutline" />
            </template>
            {{ showAdvanced ? '收起高级筛选' : '高级检索与范围过滤' }}
          </n-button>
        </div>

        <!-- 高级折叠面板 -->
        <n-collapse-transition :show="showAdvanced">
          <n-divider style="margin: 8px 0 12px" />
          <n-grid :cols="4" :x-gap="16" :y-gap="12">
            <n-gi>
              <n-form-item label="限制数据源">
                <n-select
                  v-model:value="selectedSources"
                  :options="sourceOptions"
                  multiple
                  placeholder="全部已授权源"
                  clearable
                />
              </n-form-item>
            </n-gi>
            <n-gi>
              <n-form-item label="相对子目录">
                <n-input
                  v-model:value="subPath"
                  placeholder="如 docs 或 src/core"
                  clearable
                />
              </n-form-item>
            </n-gi>
            <n-gi>
              <n-form-item label="文件通配符">
                <n-input
                  v-model:value="filePattern"
                  placeholder="如 *.md, *.cs, *.json"
                  clearable
                />
              </n-form-item>
            </n-gi>
            <n-gi>
              <n-form-item label="前后上下文行数">
                <n-input-number
                  v-model:value="contextLines"
                  :min="0"
                  :max="5"
                  style="width: 100%"
                />
              </n-form-item>
            </n-gi>
          </n-grid>
          <div style="display: flex; justify-content: flex-end; margin-top: 4px">
            <n-button text size="small" @click="handleResetOptions">
              <template #icon><n-icon :component="RefreshOutline" /></template>
              重置选项为默认
            </n-button>
          </div>
        </n-collapse-transition>
      </n-space>
    </n-card>

    <!-- 搜索统计栏 -->
    <n-card v-if="searchResult" size="small">
      <n-space align="center" justify="space-between">
        <n-space align="center" :size="12">
          <n-tag type="info" size="small" round>
            匹配 {{ searchResult.totalMatches }} 处
          </n-tag>
          <n-tag type="success" size="small" round>
            涉及 {{ searchResult.matchedFilesCount }} 个文件
          </n-tag>
          <n-text depth="3" style="font-size: 12px">
            (共扫描 {{ searchResult.totalFilesScanned }} 个文本文件，耗时 {{ searchResult.elapsedMilliseconds }}ms)
          </n-text>
          <n-tag v-if="searchResult.truncated" type="warning" size="small" round>
            已达到最大匹配上限 ({{ maxMatches }})
          </n-tag>
        </n-space>
        <n-space align="center" :size="6">
          <n-text depth="3" style="font-size: 12px">自动换行</n-text>
          <n-switch v-model:value="wrapLines" size="small" />
        </n-space>
      </n-space>
    </n-card>

    <!-- 错误提示 -->
    <n-alert v-if="searchError && !searching" type="error" title="搜索失败">
      {{ searchError }}
    </n-alert>

    <!-- 搜索空状态 -->
    <n-card v-if="searched && !searching && !searchError && searchResult?.totalMatches === 0">
      <n-empty description="在指定范围内未找到匹配的文本或正则" />
    </n-card>

    <!-- 检索结果列表 (按文件分组) -->
    <n-spin :show="searching">
      <n-space vertical :size="12" v-if="searchResult?.files?.length > 0">
        <n-collapse v-model:expanded-names="expandedFiles">
          <n-collapse-item
            v-for="file in searchResult.files"
            :key="file.fullPath"
            :name="file.fullPath"
          >
            <template #header>
              <n-space align="center" :size="8">
                <n-icon :component="DocumentTextOutline" size="18" color="#18a058" />
                <n-text strong>{{ file.relativePath }}</n-text>
                <n-tag size="small" :bordered="false" type="info">{{ file.sourceName }}</n-tag>
                <n-tag size="small" type="primary" round>{{ file.matchCount }} 处命中</n-tag>
              </n-space>
            </template>

            <template #header-extra>
              <n-button
                size="tiny"
                type="primary"
                secondary
                @click.stop="jumpToFileTop(file)"
              >
                <template #icon><n-icon :component="BookOutline" /></template>
                打开此文件
              </n-button>
            </template>

            <!-- Hunks 连续代码块展示 -->
            <n-space vertical :size="10" style="padding-top: 4px">
              <div
                v-for="(hunk, hIdx) in file.hunks"
                :key="hIdx"
                style="background: rgba(0, 0, 0, 0.03); border: 1px solid rgba(0, 0, 0, 0.08); border-radius: 6px; overflow: hidden"
              >
                <!-- Hunk 标头信息 -->
                <div style="background: rgba(0, 0, 0, 0.04); padding: 4px 12px; display: flex; align-items: center; justify-content: space-between; font-size: 12px">
                  <n-text depth="3">
                    代码块：第 {{ hunk.startLine }} 行 - 第 {{ hunk.endLine }} 行
                  </n-text>
                </div>

                <!-- 逐行代码渲染 -->
                <div :style="{ fontFamily: 'monospace', fontSize: '13px', lineHeight: 1.5, overflowX: wrapLines ? 'hidden' : 'auto', padding: '4px 0' }">
                  <div
                    v-for="line in hunk.lines"
                    :key="line.lineNumber"
                    :style="{
                      ...rowStyle,
                      background: line.isMatch ? 'rgba(255, 230, 100, 0.25)' : 'transparent',
                      padding: '2px 8px',
                      borderLeft: line.isMatch ? '3px solid #f0a020' : '3px solid transparent'
                    }"
                  >
                    <!-- 行号 -->
                    <span :style="{ display: 'inline-block', width: '44px', color: '#888', textAlign: 'right', marginRight: '12px', userSelect: 'none', fontSize: '12px', ...gutterStyle }">
                      {{ line.lineNumber }}
                    </span>

                    <!-- 代码文本 -->
                    <span :style="lineTextStyle">
                      {{ line.text }}
                    </span>

                    <!-- 匹配行跳转阅读器按钮 -->
                    <n-button
                      v-if="line.isMatch"
                      size="tiny"
                      type="warning"
                      ghost
                      @click="jumpToReader(file, line)"
                      style="margin-left: 12px; flex-shrink: 0"
                    >
                      <template #icon><n-icon :component="BookOutline" /></template>
                      跳转第 {{ line.lineNumber }} 行
                    </n-button>
                  </div>
                </div>
              </div>
            </n-space>
          </n-collapse-item>
        </n-collapse>
      </n-space>
    </n-spin>
  </n-space>
`;

export default component;
