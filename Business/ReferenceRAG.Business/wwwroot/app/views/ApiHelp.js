import { useMessage } from 'naive-ui';
import { defineComponent as _defineComponent } from 'vue';
import { ref, computed, onMounted, h, defineComponent, watch } from 'vue';
import { NCard, NTabs, NTabPane, NSpace, NButton, NCode, NTag, NText, NIcon, NInput, NSelect, NSwitch, NSpin, NEmpty, NForm, NFormItem, NGrid, NGridItem, NDescriptions, NDescriptionsItem, NCollapse, NCollapseItem } from 'naive-ui';
import { PlayOutline } from '@vicons/ionicons5';
import axios from 'axios';
import { API_URL } from '/app/config/env.js';
const component = /*@__PURE__*/ _defineComponent({
    __name: 'ApiHelp',
    setup(__props, { expose: __expose }) {
        __expose();
        const message = useMessage();
        // API Key 配置
        const apiKey = ref(localStorage.getItem('rag_api_key') || '');
        const authEnabled = ref(localStorage.getItem('rag_auth_enabled') === 'true');
        const saveApiKey = () => {
            localStorage.setItem('rag_api_key', apiKey.value);
        };
        const toggleAuth = (value) => {
            localStorage.setItem('rag_auth_enabled', String(value));
            if (value && apiKey.value) {
                axios.defaults.headers.common['X-API-Key'] = apiKey.value;
            }
            else {
                delete axios.defaults.headers.common['X-API-Key'];
            }
        };
        const testAuth = async () => {
            try {
                const config = authEnabled.value && apiKey.value
                    ? { headers: { 'X-API-Key': apiKey.value } }
                    : {};
                const response = await axios.get(`${API_URL}/system/status`, config);
                if (response.status === 200) {
                    message.success('认证成功');
                }
            }
            catch (error) {
                const err = error;
                if (err.response?.status === 401) {
                    message.error('认证失败: API Key 无效');
                }
                else {
                    message.error('连接失败');
                }
            }
        };
        // Swagger 数据
        const swaggerData = ref(null);
        const loading = ref(false);
        const apiTags = computed(() => {
            if (!swaggerData.value?.paths)
                return [];
            const tags = new Set();
            Object.values(swaggerData.value.paths).forEach(path => {
                ['get', 'post', 'put', 'delete', 'patch'].forEach(method => {
                    const op = path[method];
                    op?.tags?.forEach(tag => tags.add(tag));
                });
            });
            return Array.from(tags).sort();
        });
        const getEndpointsByTag = (tag) => {
            if (!swaggerData.value?.paths)
                return [];
            const endpoints = [];
            Object.entries(swaggerData.value.paths).forEach(([path, methods]) => {
                ['get', 'post', 'put', 'delete', 'patch'].forEach(method => {
                    const op = methods[method];
                    if (op?.tags?.includes(tag)) {
                        endpoints.push({ path, method, operation: op });
                    }
                });
            });
            return endpoints;
        };
        const tagDisplayNames = {
            AIQuery: 'AI 查询',
            BM25Index: 'BM25 索引',
            Dashboard: '仪表盘',
            Models: '模型管理',
            Performance: '性能测试',
            Settings: '设置',
            Sources: '数据源',
            System: '系统',
            VectorIndex: '向量索引'
        };
        const getTagDisplayName = (tag) => tagDisplayNames[tag] || tag;
        const loadSwagger = async () => {
            loading.value = true;
            try {
                const baseUrl = false ? 'http://localhost:5294' : '';
                const response = await axios.get(`${baseUrl}/swagger/v1/swagger.json`);
                swaggerData.value = response.data;
            }
            catch (error) {
                message.error('加载 API 文档失败');
                console.error(error);
            }
            finally {
                loading.value = false;
            }
        };
        // API Endpoint Card 组件
        const ApiEndpointCard = defineComponent({
            name: 'ApiEndpointCard',
            props: {
                endpoint: { type: Object, required: true },
                schemas: { type: Object, required: true },
                apiKey: { type: String, default: '' }
            },
            emits: ['test'],
            setup(props, { emit }) {
                const expanded = ref(false);
                const copied = ref(false);
                const getMethodColor = (method) => {
                    switch (method.toUpperCase()) {
                        case 'GET': return '#0d7a40';
                        case 'POST': return '#1060c0';
                        case 'PUT': return '#c08010';
                        case 'DELETE': return '#a02040';
                        case 'PATCH': return '#707580';
                        default: return '#707580';
                    }
                };
                const resolveSchema = (ref) => {
                    if (!ref)
                        return null;
                    const schemaName = ref.replace('#/components/schemas/', '');
                    return props.schemas[schemaName];
                };
                const getSchemaExample = (schema) => {
                    if (!schema)
                        return null;
                    const s = schema;
                    if (s.type === 'object' && s.properties) {
                        const props = s.properties;
                        const result = {};
                        Object.entries(props).forEach(([key, value]) => {
                            const prop = value;
                            if (prop.type === 'string') {
                                if (prop.format === 'date-time')
                                    result[key] = new Date().toISOString();
                                else if (prop.enum)
                                    result[key] = prop.enum[0];
                                else
                                    result[key] = '';
                            }
                            else if (prop.type === 'integer' || prop.type === 'number') {
                                result[key] = prop.default ?? 0;
                            }
                            else if (prop.type === 'boolean') {
                                result[key] = prop.default ?? false;
                            }
                            else if (prop.type === 'array') {
                                result[key] = [];
                            }
                            else if (prop.$ref) {
                                result[key] = getSchemaExample(resolveSchema(prop.$ref));
                            }
                        });
                        return result;
                    }
                    return null;
                };
                const getRequestBodyExample = () => {
                    const content = props.endpoint.operation.requestBody?.content;
                    if (!content)
                        return null;
                    const jsonContent = content['application/json'];
                    if (jsonContent?.schema?.$ref) {
                        return getSchemaExample(resolveSchema(jsonContent.schema.$ref));
                    }
                    return null;
                };
                const generateCurl = () => {
                    const baseUrl = window.location.origin;
                    let curl = `curl -X ${props.endpoint.method.toUpperCase()} "${baseUrl}${props.endpoint.path}"`;
                    if (props.apiKey) {
                        curl += ` \\\n  -H "X-API-Key: ${props.apiKey}"`;
                    }
                    const body = getRequestBodyExample();
                    if (body && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(props.endpoint.method.toUpperCase())) {
                        curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify(body, null, 2)}'`;
                    }
                    return curl;
                };
                const copyCurl = async () => {
                    try {
                        await navigator.clipboard.writeText(generateCurl());
                        copied.value = true;
                        setTimeout(() => { copied.value = false; }, 2000);
                    }
                    catch {
                        // fallback
                    }
                };
                const sendToTestTool = async () => {
                    // 复制请求体到剪贴板
                    const body = getRequestBodyExample();
                    if (body) {
                        try {
                            await navigator.clipboard.writeText(JSON.stringify(body, null, 2));
                        }
                        catch {
                            // 忽略复制失败
                        }
                    }
                    emit('test', { path: props.endpoint.path, method: props.endpoint.method, body });
                };
                return () => h('div', {
                    class: 'api-endpoint-card'
                }, [
                    // Header
                    h('div', { class: 'api-endpoint-header' }, [
                        h(NSpace, { align: 'center' }, {
                            default: () => [
                                h(NTag, {
                                    size: 'small',
                                    class: 'method-tag',
                                    style: { background: getMethodColor(props.endpoint.method), color: '#fff', fontWeight: 'bold', minWidth: '60px', textAlign: 'center' }
                                }, { default: () => props.endpoint.method.toUpperCase() }),
                                h('code', { class: 'api-path' }, props.endpoint.path),
                                props.endpoint.operation.summary && h('span', { class: 'api-summary' }, props.endpoint.operation.summary)
                            ]
                        }),
                        h(NSpace, { align: 'center', size: 'small' }, {
                            default: () => [
                                h(NButton, {
                                    size: 'tiny',
                                    onClick: () => { expanded.value = !expanded.value; },
                                    text: true
                                }, { default: () => expanded.value ? '收起' : '详情' }),
                                h(NButton, {
                                    size: 'tiny',
                                    onClick: copyCurl,
                                    type: copied.value ? 'success' : 'default'
                                }, {
                                    default: () => copied.value ? '已复制' : 'cURL'
                                }),
                                h(NButton, {
                                    size: 'tiny',
                                    type: 'primary',
                                    onClick: sendToTestTool
                                }, {
                                    default: () => '测试',
                                    icon: () => h(NIcon, { size: 14 }, { default: () => h(PlayOutline) })
                                })
                            ]
                        })
                    ]),
                    // Expanded content
                    expanded.value && h('div', { class: 'api-endpoint-body' }, [
                        // Parameters
                        props.endpoint.operation.parameters && props.endpoint.operation.parameters.length > 0 && h('div', { class: 'api-section' }, [
                            h('div', { class: 'api-section-title' }, '参数'),
                            h(NDescriptions, { labelPlacement: 'left', bordered: true, size: 'small', column: 1 }, {
                                default: () => props.endpoint.operation.parameters?.map(param => {
                                    const labelParts = [];
                                    if (param.required)
                                        labelParts.push('[必填] ');
                                    labelParts.push(param.name);
                                    labelParts.push(` (${param.in})`);
                                    if (param.schema?.type)
                                        labelParts.push(` (${param.schema.type})`);
                                    return h(NDescriptionsItem, { label: labelParts.join('') }, {
                                        default: () => param.description || '-'
                                    });
                                })
                            })
                        ]),
                        // Request Body
                        props.endpoint.operation.requestBody && h('div', { class: 'api-section' }, [
                            h('div', { class: 'api-section-title' }, [
                                '请求体',
                                props.endpoint.operation.requestBody?.required && h(NTag, { type: 'error', size: 'tiny', style: 'margin-left: 8px' }, { default: () => '必填' })
                            ]),
                            h('div', { class: 'code-block' }, [
                                h(NCode, {
                                    language: 'json',
                                    code: JSON.stringify(getRequestBodyExample(), null, 2),
                                    wordWrap: true
                                })
                            ])
                        ]),
                        // Responses
                        props.endpoint.operation.responses && h('div', { class: 'api-section' }, [
                            h('div', { class: 'api-section-title' }, '响应'),
                            h(NCollapse, {}, {
                                default: () => Object.entries(props.endpoint.operation.responses ?? {}).map(([code, response]) => h(NCollapseItem, {
                                    name: code
                                }, {
                                    header: () => h(NSpace, { align: 'center', size: 'small' }, {
                                        default: () => [
                                            h(NTag, {
                                                type: code.startsWith('2') ? 'success' : code.startsWith('4') ? 'warning' : 'error',
                                                size: 'small'
                                            }, { default: () => code }),
                                            h('span', { class: 'response-desc' }, response.description)
                                        ]
                                    }),
                                    default: () => {
                                        const schema = response.content?.['application/json']?.schema;
                                        if (schema?.$ref) {
                                            const resolved = resolveSchema(schema.$ref);
                                            return h('div', { class: 'code-block' }, [
                                                h(NCode, {
                                                    language: 'json',
                                                    code: JSON.stringify(getSchemaExample(resolved), null, 2),
                                                    wordWrap: true
                                                })
                                            ]);
                                        }
                                        return h('span', { class: 'no-body-hint' }, '无响应体');
                                    }
                                }))
                            })
                        ])
                    ])
                ]);
            }
        });
        // API 测试工具
        const testMethod = ref('GET');
        const testPath = ref('');
        const testBody = ref('');
        const testLoading = ref(false);
        const testResponse = ref('');
        const testResponseStatus = ref(0);
        const testDuration = ref(0);
        const methodOptions = [
            { label: 'GET', value: 'GET' },
            { label: 'POST', value: 'POST' },
            { label: 'PUT', value: 'PUT' },
            { label: 'DELETE', value: 'DELETE' },
            { label: 'PATCH', value: 'PATCH' }
        ];
        const runApiTest = async () => {
            if (!testPath.value) {
                message.warning('请输入 API 路径');
                return;
            }
            testLoading.value = true;
            testResponse.value = '';
            const startTime = Date.now();
            try {
                const config = {
                    method: testMethod.value.toLowerCase(),
                    url: testPath.value
                };
                if (authEnabled.value && apiKey.value) {
                    config.headers = { 'X-API-Key': apiKey.value };
                }
                if (testBody.value && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(testMethod.value)) {
                    config.data = JSON.parse(testBody.value);
                }
                const response = await axios.request(config);
                testResponseStatus.value = response.status;
                testResponse.value = JSON.stringify(response.data, null, 2);
            }
            catch (error) {
                const err = error;
                testResponseStatus.value = err.response?.status || 0;
                testResponse.value = err.response?.data
                    ? JSON.stringify(err.response.data, null, 2)
                    : String(error);
            }
            finally {
                testDuration.value = Date.now() - startTime;
                testLoading.value = false;
            }
        };
        const copyTestCurl = async () => {
            const baseUrl = window.location.origin;
            let curl = `curl -X ${testMethod.value} "${baseUrl}${testPath.value}"`;
            if (authEnabled.value && apiKey.value) {
                curl += ` \\\n  -H "X-API-Key: ${apiKey.value}"`;
            }
            if (testBody.value && ['POST', 'PUT', 'DELETE', 'PATCH'].includes(testMethod.value)) {
                curl += ` \\\n  -H "Content-Type: application/json" \\\n  -d '${testBody.value}'`;
            }
            try {
                await navigator.clipboard.writeText(curl);
                message.success('cURL 已复制到剪贴板');
            }
            catch {
                message.error('复制失败');
            }
        };
        const clearTestResult = () => {
            testPath.value = '';
            testBody.value = '';
            testResponse.value = '';
            testResponseStatus.value = 0;
            testDuration.value = 0;
        };
        const handleTestEndpoint = (endpoint) => {
            testPath.value = endpoint.path;
            testMethod.value = endpoint.method.toUpperCase();
            // 使用传递过来的请求体
            if (endpoint.body) {
                testBody.value = JSON.stringify(endpoint.body, null, 2);
            }
            else {
                testBody.value = '';
            }
            // 滚动到顶部
            window.scrollTo({ top: 0, behavior: 'smooth' });
        };
        // 初始化
        onMounted(() => {
            if (authEnabled.value && apiKey.value) {
                axios.defaults.headers.common['X-API-Key'] = apiKey.value;
            }
            loadSwagger();
        });
        watch([authEnabled, apiKey], ([enabled, key]) => {
            if (enabled && key) {
                axios.defaults.headers.common['X-API-Key'] = key;
            }
            else {
                delete axios.defaults.headers.common['X-API-Key'];
            }
        });
        const __returned__ = { message, apiKey, authEnabled, saveApiKey, toggleAuth, testAuth, swaggerData, loading, apiTags, getEndpointsByTag, tagDisplayNames, getTagDisplayName, loadSwagger, ApiEndpointCard, testMethod, testPath, testBody, testLoading, testResponse, testResponseStatus, testDuration, methodOptions, runApiTest, copyTestCurl, clearTestResult, handleTestEndpoint, get NCard() { return NCard; }, get NTabs() { return NTabs; }, get NTabPane() { return NTabPane; }, get NSpace() { return NSpace; }, get NButton() { return NButton; }, get NCode() { return NCode; }, get NTag() { return NTag; }, get NText() { return NText; }, get NInput() { return NInput; }, get NSelect() { return NSelect; }, get NSwitch() { return NSwitch; }, get NSpin() { return NSpin; }, get NEmpty() { return NEmpty; }, get NForm() { return NForm; }, get NFormItem() { return NFormItem; }, get NGrid() { return NGrid; }, get NGridItem() { return NGridItem; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"20\">\n    <!-- API 测试工具 (移到上方) -->\n    <n-card title=\"API 测试工具\" size=\"small\">\n      <n-space vertical>\n        <n-form label-placement=\"left\" label-width=\"80\">\n          <n-grid :cols=\"24\" :x-gap=\"12\">\n            <n-grid-item :span=\"4\">\n              <n-form-item label=\"方法\">\n                <n-select\n                  v-model:value=\"testMethod\"\n                  :options=\"methodOptions\"\n                />\n              </n-form-item>\n            </n-grid-item>\n            <n-grid-item :span=\"20\">\n              <n-form-item label=\"路径\">\n                <n-input v-model:value=\"testPath\" placeholder=\"/api/...\" />\n              </n-form-item>\n            </n-grid-item>\n          </n-grid>\n          <n-form-item label=\"请求体\">\n            <n-input\n              v-model:value=\"testBody\"\n              type=\"textarea\"\n              placeholder=\"JSON 格式请求体（可选）\"\n              :rows=\"6\"\n              style=\"font-family: monospace\"\n            />\n          </n-form-item>\n        </n-form>\n        <n-space>\n          <n-button type=\"primary\" :loading=\"testLoading\" @click=\"runApiTest\">\n            发送请求\n          </n-button>\n          <n-button @click=\"copyTestCurl\">\n            复制 cURL\n          </n-button>\n          <n-button @click=\"clearTestResult\">\n            清空\n          </n-button>\n        </n-space>\n\n        <n-card v-if=\"testResponse\" title=\"响应结果\" size=\"small\" class=\"response-card\">\n          <template #header-extra>\n            <n-space>\n              <n-tag :type=\"testResponseStatus >= 200 && testResponseStatus < 300 ? 'success' : 'error'\" size=\"small\">\n                {{ testResponseStatus }}\n              </n-tag>\n              <n-text depth=\"3\">{{ testDuration }}ms</n-text>\n            </n-space>\n          </template>\n          <n-code :code=\"testResponse\" language=\"json\" word-wrap />\n        </n-card>\n      </n-space>\n    </n-card>\n\n    <!-- API Key 配置 -->\n    <n-card title=\"API 认证配置\" size=\"small\">\n      <n-space align=\"center\">\n        <n-input\n          v-model:value=\"apiKey\"\n          type=\"password\"\n          placeholder=\"输入 API Key (如果服务启用了认证)\"\n          style=\"width: 400px\"\n          @change=\"saveApiKey\"\n        />\n        <n-switch v-model:value=\"authEnabled\" @update:value=\"toggleAuth\">\n          <template #checked>已启用</template>\n          <template #unchecked>未启用</template>\n        </n-switch>\n        <n-button @click=\"testAuth\">测试认证</n-button>\n      </n-space>\n    </n-card>\n\n    <!-- API 文档 -->\n    <n-card title=\"API 文档\">\n      <template #header-extra>\n        <n-space>\n          <n-button :loading=\"loading\" @click=\"loadSwagger\">\n            刷新\n          </n-button>\n        </n-space>\n      </template>\n\n      <n-spin :show=\"loading\">\n        <n-tabs v-if=\"swaggerData\" type=\"line\" animated>\n          <n-tab-pane\n            v-for=\"tag in apiTags\"\n            :key=\"tag\"\n            :name=\"tag\"\n            :tab=\"getTagDisplayName(tag)\"\n          >\n            <n-space vertical :size=\"12\">\n              <component :is=\"ApiEndpointCard\"\n                v-for=\"(endpoint, index) in getEndpointsByTag(tag)\"\n                :key=\"index\"\n                :endpoint=\"endpoint\"\n                :schemas=\"swaggerData.components?.schemas || {}\"\n                :api-key=\"authEnabled ? apiKey : ''\"\n                @test=\"handleTestEndpoint\"\n              />\n            </n-space>\n          </n-tab-pane>\n        </n-tabs>\n        <n-empty v-else-if=\"!loading\" description=\"加载 API 文档失败\">\n          <template #extra>\n            <n-button @click=\"loadSwagger\">重试</n-button>\n          </template>\n        </n-empty>\n      </n-spin>\n    </n-card>\n  </n-space>\n";
component.__scopeId = "data-v-3a5e9d32";
export default component;
