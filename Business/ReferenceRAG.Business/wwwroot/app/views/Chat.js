import { defineComponent as _defineComponent } from 'vue';
import { ref, nextTick, onMounted, onActivated, onUnmounted } from 'vue';
import {renderMarkdown} from '/shared/safe-markdown.js';
import { useMessage } from 'naive-ui';
import { ChatbubblesOutline, AddOutline, SendOutline, RefreshOutline, CreateOutline, CopyOutline, DocumentTextOutline, LogoMarkdown, ImageOutline, CloseOutline } from '@vicons/ionicons5';
import { marked } from 'marked';
import { API_URL } from '/app/config/env.js';
import { useAuthStore } from '/app/stores/auth.js';
import { ragFetch as fetch } from '/core/transport/index.js';
import { modelsApi, settingsApi } from '/app/api/index.js';
const component = /*@__PURE__*/ _defineComponent({
    ...{ name: 'Chat' },
    __name: 'Chat',
    setup(__props, { expose: __expose }) {
        __expose();
        marked.setOptions({ breaks: true });
        const message = useMessage();
        const authStore = useAuthStore();
        const sessionId = ref(null);
        const messages = ref([]);
        const input = ref('');
        const streaming = ref(false);
        let streamController;
        const cancel = () => streamController?.abort();
        onUnmounted(cancel);
        const scrollbarRef = ref(null);
        const toolDescriptions = ref([]);
        const initialized = ref(false);
        const rawSet = ref(new Set());
        const editVisible = ref(false);
        const editText = ref('');
        const editIndex = ref(-1);

        // 多模态图像附件状态管理
        const attachedImages = ref([]);
        const fileInputRef = ref(null);

        const compressImage = (file, maxWidth = 1024, quality = 0.85) => {
            return new Promise((resolve, reject) => {
                const reader = new FileReader();
                reader.onload = (e) => {
                    const img = new Image();
                    img.onload = () => {
                        let { width, height } = img;
                        if (width > maxWidth || height > maxWidth) {
                            if (width > height) {
                                height = Math.round((height * maxWidth) / width);
                                width = maxWidth;
                            } else {
                                width = Math.round((width * maxWidth) / height);
                                height = maxWidth;
                            }
                        }
                        const canvas = document.createElement('canvas');
                        canvas.width = width;
                        canvas.height = height;
                        const ctx = canvas.getContext('2d');
                        ctx.drawImage(img, 0, 0, width, height);
                        const dataUrl = canvas.toDataURL('image/jpeg', quality);
                        resolve(dataUrl);
                    };
                    img.onerror = reject;
                    img.src = e.target.result;
                };
                reader.onerror = reject;
                reader.readAsDataURL(file);
            });
        };

        const addImageFile = async (file) => {
            if (!file || !file.type.startsWith('image/')) {
                message.warning('请选择有效的图片文件');
                return;
            }
            if (attachedImages.value.length >= 4) {
                message.warning('单次最多只可附加 4 张图片');
                return;
            }
            try {
                const dataUrl = await compressImage(file);
                attachedImages.value.push({
                    id: 'img_' + Date.now() + '_' + Math.random().toString(36).slice(2, 6),
                    name: file.name || 'image.jpg',
                    dataUrl,
                    size: file.size
                });
            } catch (err) {
                message.error('图片加载或压缩失败: ' + err.message);
            }
        };

        const removeAttachedImage = (idx) => {
            attachedImages.value.splice(idx, 1);
        };

        const triggerSelectImage = () => {
            fileInputRef.value?.click();
        };

        const handleFileInputChange = (e) => {
            const files = e.target.files;
            if (files) {
                for (let i = 0; i < files.length; i++) {
                    addImageFile(files[i]);
                }
            }
            e.target.value = '';
        };

        const handlePaste = (e) => {
            const items = e.clipboardData?.items;
            if (!items) return;
            for (let i = 0; i < items.length; i++) {
                const item = items[i];
                if (item.type && item.type.indexOf('image') !== -1) {
                    const blob = item.getAsFile();
                    if (blob) {
                        addImageFile(blob);
                        e.preventDefault();
                    }
                }
            }
        };

        const renderMd = (content) => {
            const html = renderMarkdown((content || '').trimStart());
            return typeof html === 'string' ? html : '';
        };
        const renderMsg = (msg, isCurrentStreaming) => {
            if (!isCurrentStreaming && msg._html) {
                return msg._html;
            }
            const html = renderMd(msg.content);
            if (!isCurrentStreaming) {
                msg._html = html;
            }
            return html + (isCurrentStreaming && msg.content ? ' ▌' : '');
        };
        const toggleRaw = (i) => {
            const next = new Set(rawSet.value);
            if (next.has(i))
                next.delete(i);
            else
                next.add(i);
            rawSet.value = next;
        };
        const copyText = async (content) => {
            await navigator.clipboard.writeText(content).catch(() => { });
            message.success('已复制');
        };
        const startEdit = (i, content) => {
            editIndex.value = i;
            editText.value = content;
            editVisible.value = true;
        };
        const confirmEdit = () => {
            if (!editText.value.trim())
                return;
            const text = editText.value.trim();
            const idx = editIndex.value;
            editVisible.value = false;
            messages.value.splice(idx);
            input.value = text;
            send();
        };
        const getHeaders = () => {
            const headers = { 'Content-Type': 'application/json' };
            if (authStore.apiKey)
                headers['X-API-Key'] = authStore.apiKey;
            return headers;
        };
        const scrollToBottom = async () => {
            await nextTick();
            scrollbarRef.value?.scrollTo({ top: 999999, behavior: 'smooth' });
        };
        const parseJsonSafe = async (res) => {
            const text = await res.text();
            if (!text || !text.trim()) {
                throw new Error(`服务响应为空 (HTTP ${res.status})`);
            }
            try {
                return JSON.parse(text);
            } catch (err) {
                throw new Error(`响应解析失败: ${err.message}`);
            }
        };
        const createSession = async () => {
            const res = await fetch(`${API_URL}/chat/sessions`, {
                method: 'POST',
                headers: getHeaders()
            });
            if (!res.ok)
                throw new Error(`创建会话失败 (${res.status})`);
            const data = await parseJsonSafe(res);
            return (data.SessionId ?? data.sessionId);
        };
        const deleteSession = async (id) => {
            const headers = getHeaders();
            delete headers['Content-Type'];
            await fetch(`${API_URL}/chat/sessions/${id}`, { method: 'DELETE', headers }).catch(() => { });
        };
        const newSession = async () => {
            if (streaming.value)
                return;
            if (sessionId.value)
                await deleteSession(sessionId.value);
            messages.value = [];
            rawSet.value = new Set();
            attachedImages.value = [];
            try {
                sessionId.value = await createSession();
            }
            catch (e) {
                message.error(e.message || '创建会话失败');
            }
        };
        const handleKeyDown = (e) => {
            if (e.isComposing)
                return;
            if (!e.shiftKey) {
                e.preventDefault();
                send();
            }
        };
        const retry = (content, fromIndex) => {
            if (streaming.value)
                return;
            const target = messages.value[fromIndex];
            messages.value.splice(fromIndex);
            input.value = content;
            if (target && target.images && target.images.length > 0) {
                attachedImages.value = target.images.map((dataUrl, i) => ({
                    id: 'img_retry_' + i,
                    name: 'image.jpg',
                    dataUrl,
                    size: 0
                }));
            }
            send();
        };
        const send = async () => {
            const text = input.value.trim();
            const imgs = [...attachedImages.value];
            if ((!text && imgs.length === 0) || !sessionId.value || streaming.value)
                return;

            const currentImgs = imgs.map(x => x.dataUrl);
            messages.value.push({
                role: 'user',
                content: text,
                images: currentImgs.length > 0 ? currentImgs : undefined
            });

            input.value = '';
            attachedImages.value = [];
            streaming.value = true;
            streamController = new AbortController();
            await scrollToBottom();
            const assistantMsg = { role: 'assistant', content: '' };
            messages.value.push(assistantMsg);

            try {
                const res = await fetch(`${API_URL}/chat/stream`, {
                    method: 'POST',
                    headers: getHeaders(),
                    signal: streamController.signal,
                    body: JSON.stringify({
                        sessionId: sessionId.value,
                        message: text,
                        images: currentImgs.length > 0 ? currentImgs : undefined
                    })
                });
                if (!res.ok)
                    throw new Error(`HTTP ${res.status}`);
                if (!res.body)
                    throw new Error('无响应体');
                const reader = res.body.getReader();
                const decoder = new TextDecoder();
                let buffer = '';
                while (true) {
                    const { done, value } = await reader.read();
                    if (done)
                        break;
                    buffer += decoder.decode(value, { stream: true });
                    const lines = buffer.split('\n');
                    buffer = lines.pop() ?? '';
                    for (const line of lines) {
                        if (!line.startsWith('data: '))
                            continue;
                        const json = line.slice(6).trim();
                        if (!json)
                            continue;
                        try {
                            const evt = JSON.parse(json);
                            if (evt.type === 'text' && evt.delta) {
                                messages.value[messages.value.length - 1].content += evt.delta;
                                await scrollToBottom();
                            }
                            else if (evt.type === 'replace' && evt.delta !== undefined) {
                                messages.value[messages.value.length - 1].content = evt.delta;
                                await scrollToBottom();
                            }
                            else if (evt.type === 'error') {
                                message.error(evt.message || '发生错误');
                            }
                        }
                        catch { /* ignore parse errors */ }
                    }
                }
            }
            catch (err) {
                if (err.name !== 'AbortError') {
                    message.error(err.message || '请求失败');
                    const last = messages.value[messages.value.length - 1];
                    if (last?.role === 'assistant' && !last.content)
                        last.content = '[请求失败]';
                }
            }
            finally {
                streamController = null;
                streaming.value = false;
                await scrollToBottom();
            }
        };
        const chatBackend = ref('LocalGguf');
        const currentChatModel = ref('');
        const currentChatDisplayName = ref('');
        const chatVariant = ref('');
        const isGpuMode = ref(false);
        const chatModelOptions = ref([]);
        const switchingModel = ref(false);

        const loadChatModelStatus = async () => {
            try {
                const [statusRes, modelsRes, settingsRes] = await Promise.all([
                    modelsApi.getRuntimeStatus().catch(() => null),
                    modelsApi.getDownloadedChatModels().catch(() => null),
                    settingsApi.get().catch(() => null)
                ]);
                if (settingsRes?.data?.chat) {
                    chatBackend.value = settingsRes.data.chat.activeBackend || 'LocalGguf';
                }
                if (statusRes?.data?.chat) {
                    const c = statusRes.data.chat;
                    currentChatModel.value = c.modelName || '';
                    currentChatDisplayName.value = c.displayName || c.modelName || '';
                    chatVariant.value = c.activeVariant || c.quantization || '';
                    isGpuMode.value = !!c.isGpuMode;
                } else if (settingsRes?.data?.chat?.localGguf?.modelName) {
                    currentChatModel.value = settingsRes.data.chat.localGguf.modelName;
                    currentChatDisplayName.value = settingsRes.data.chat.localGguf.modelName;
                }
                const downloaded = modelsRes?.data || [];
                chatModelOptions.value = downloaded.map(m => ({
                    label: m.displayName || m.name,
                    value: m.name
                }));
            } catch (e) {
                console.error('[Chat] loadChatModelStatus failed', e);
            }
        };

        const handleModelChange = async (newModelName) => {
            if (!newModelName || newModelName === currentChatModel.value || switchingModel.value) return;
            switchingModel.value = true;
            try {
                const res = await modelsApi.switchChatModel(newModelName);
                if (res.data?.success !== false) {
                    message.success(`已切换至本地聊天模型: ${newModelName}`);
                    await loadChatModelStatus();
                    await newSession();
                } else {
                    message.error(res.data?.error || '切换模型失败');
                }
            } catch (e) {
                message.error(e.message || '切换模型失败');
            } finally {
                switchingModel.value = false;
            }
        };

        const initSession = async () => {
            if (initialized.value)
                return;
            initialized.value = true;
            try {
                sessionId.value = await createSession();
                const res = await fetch(`${API_URL}/chat/tools`, { headers: getHeaders() });
                if (res.ok) {
                    const data = await parseJsonSafe(res);
                    toolDescriptions.value = Array.isArray(data) ? data : [];
                }
            }
            catch (e) {
                initialized.value = false;
                message.error(e.message || '初始化会话失败，请检查服务连接及 Chat 配置');
            }
        };
        onMounted(async () => {
            await Promise.all([initSession(), loadChatModelStatus()]);
        });
        onActivated(async () => {
            await scrollToBottom();
            loadChatModelStatus();
        });
        const __returned__ = {
            chatBackend, currentChatModel, currentChatDisplayName, chatVariant, isGpuMode, chatModelOptions, switchingModel, loadChatModelStatus, handleModelChange, cancel, message, authStore, sessionId, messages, input, streaming, scrollbarRef, toolDescriptions, initialized, rawSet, editVisible, editText, editIndex,
            attachedImages, fileInputRef, compressImage, addImageFile, removeAttachedImage, triggerSelectImage, handleFileInputChange, handlePaste,
            renderMd, renderMsg, toggleRaw, copyText, startEdit, confirmEdit, getHeaders, scrollToBottom, createSession, deleteSession, newSession, handleKeyDown, retry, send, initSession,
            get ChatbubblesOutline() { return ChatbubblesOutline; },
            get AddOutline() { return AddOutline; },
            get SendOutline() { return SendOutline; },
            get RefreshOutline() { return RefreshOutline; },
            get CreateOutline() { return CreateOutline; },
            get CopyOutline() { return CopyOutline; },
            get DocumentTextOutline() { return DocumentTextOutline; },
            get LogoMarkdown() { return LogoMarkdown; },
            get ImageOutline() { return ImageOutline; },
            get CloseOutline() { return CloseOutline; }
        };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"16\">\n    <!-- Header Bar -->\n    <n-card size=\"small\">\n      <n-space align=\"center\" justify=\"space-between\">\n        <n-space align=\"center\" :size=\"8\" style=\"flex-wrap: wrap\">\n          <n-icon :component=\"ChatbubblesOutline\" size=\"20\" />\n          <n-text strong>AI 对话 (Agent)</n-text>\n\n          <n-divider vertical style=\"margin: 0 4px\" />\n\n          <!-- 本地 GGUF 状态与快捷选择 -->\n          <template v-if=\"chatBackend === 'LocalGguf'\">\n            <n-tag size=\"small\" type=\"success\" round :bordered=\"false\">\n              本地 GGUF\n            </n-tag>\n            <n-select\n              v-if=\"chatModelOptions.length > 0\"\n              :value=\"currentChatModel\"\n              :options=\"chatModelOptions\"\n              :loading=\"switchingModel\"\n              size=\"small\"\n              style=\"width: 230px\"\n              placeholder=\"选择本地聊天模型\"\n              @update:value=\"handleModelChange\"\n            />\n            <n-tag v-else size=\"small\" type=\"warning\" round :bordered=\"false\">\n              {{ currentChatDisplayName || '未下载本地模型' }}\n            </n-tag>\n            <n-tag v-if=\"isGpuMode\" size=\"small\" type=\"success\" round :bordered=\"false\">\n              🚀 GPU 加速\n            </n-tag>\n            <n-tag v-else size=\"small\" type=\"warning\" round :bordered=\"false\">\n              💻 CPU 模式\n            </n-tag>\n            <n-tag v-if=\"chatVariant\" size=\"small\" :bordered=\"false\">\n              {{ chatVariant }}\n            </n-tag>\n          </template>\n\n          <!-- 线上 API 状态 -->\n          <template v-else>\n            <n-tag size=\"small\" type=\"info\" round :bordered=\"false\">\n              线上 API\n            </n-tag>\n            <n-tag size=\"small\" type=\"default\" round>\n              {{ currentChatDisplayName || 'OpenAI API' }}\n            </n-tag>\n          </template>\n\n          <n-divider vertical style=\"margin: 0 4px\" />\n          <n-tag size=\"small\" type=\"default\" round>{{ sessionId ? sessionId.slice(0, 8) : '—' }}</n-tag>\n          <n-button v-if=\"streaming\" size=\"small\" type=\"warning\" ghost @click=\"cancel\">停止生成</n-button>\n        </n-space>\n\n        <n-space align=\"center\" :size=\"8\">\n          <n-button size=\"small\" secondary @click=\"newSession\" :disabled=\"streaming\">\n            <template #icon><n-icon :component=\"AddOutline\" /></template>\n            新对话\n          </n-button>\n        </n-space>\n      </n-space>\n    </n-card>\n\n    <!-- Message List -->\n    <n-card\n      :style=\"{ height: 'calc(100vh - 300px)', overflow: 'hidden' }\"\n      :content-style=\"{ padding: 0, height: '100%', display: 'flex', flexDirection: 'column' }\"\n    >\n      <n-scrollbar ref=\"scrollbarRef\" style=\"flex: 1\">\n        <div style=\"padding: 16px; display: flex; flex-direction: column; gap: 16px; min-height: 100%\">\n          <!-- Empty state -->\n          <div v-if=\"messages.length === 0\" style=\"flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 60px 0; gap: 12px\">\n            <n-icon :component=\"ChatbubblesOutline\" size=\"52\" color=\"#63e2b7\" />\n            <n-text depth=\"3\">开始对话吧，可以搜索知识库、查询索引状态或上传图片提问</n-text>\n          </div>\n\n          <!-- Messages -->\n          <div\n            v-for=\"(msg, i) in messages\"\n            :key=\"i\"\n            :style=\"{ display: 'flex', justifyContent: msg.role === 'user' ? 'flex-end' : 'flex-start' }\"\n          >\n            <div :style=\"{ maxWidth: '80%', display: 'flex', flexDirection: 'column', gap: '4px', alignItems: msg.role === 'user' ? 'flex-end' : 'flex-start' }\">\n              <n-card\n                size=\"small\"\n                :style=\"{\n                  background: msg.role === 'user' ? 'rgba(99,226,183,0.1)' : undefined,\n                  borderColor: msg.role === 'user' ? 'rgba(99,226,183,0.3)' : undefined\n                }\"\n              >\n                <!-- User message -->\n                <div v-if=\"msg.role === 'user'\">\n                  <!-- 图片附件预览 -->\n                  <div v-if=\"msg.images && msg.images.length > 0\" style=\"display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 8px\">\n                    <n-image\n                      v-for=\"(img, imgIdx) in msg.images\"\n                      :key=\"imgIdx\"\n                      :src=\"img\"\n                      width=\"120\"\n                      height=\"80\"\n                      object-fit=\"cover\"\n                      style=\"border-radius: 6px; cursor: pointer; border: 1px solid rgba(99,226,183,0.3)\"\n                    />\n                  </div>\n                  <div v-if=\"msg.content\" style=\"white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6\">{{ msg.content.trimStart() }}</div>\n                </div>\n\n                <!-- Assistant message: 单卡片内聚状态渲染，思考中与流式内容同卡片无缝衔接，杜绝双回复框 -->\n                <div v-else>\n                  <div v-if=\"streaming && i === messages.length - 1 && !msg.content\" style=\"padding: 2px 0\">\n                    <n-space align=\"center\" :size=\"8\">\n                      <n-spin size=\"small\" />\n                      <n-text depth=\"3\" style=\"font-size: 13px\">思考中...</n-text>\n                      <n-text v-if=\"chatBackend === 'LocalGguf'\" depth=\"3\" style=\"font-size: 11px\">（本地模型唤醒或装载显存中）</n-text>\n                    </n-space>\n                  </div>\n                  <div v-else-if=\"rawSet.has(i)\" style=\"white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6\">{{ msg.content.trimStart() }}<span v-if=\"streaming && i === messages.length - 1\" class=\"cursor-blink\">▌</span></div>\n                  <div v-else class=\"markdown-body\" v-html=\"renderMsg(msg, streaming && i === messages.length - 1)\"></div>\n                </div>\n              </n-card>\n\n              <!-- User actions -->\n              <div v-if=\"msg.role === 'user' && !streaming\" style=\"display: flex; gap: 4px; padding: 0 4px\">\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"startEdit(i, msg.content)\">\n                  <template #icon><n-icon :component=\"CreateOutline\" size=\"12\" /></template>\n                  编辑\n                </n-button>\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"retry(msg.content, i)\">\n                  <template #icon><n-icon :component=\"RefreshOutline\" size=\"12\" /></template>\n                  重试\n                </n-button>\n              </div>\n\n              <!-- Assistant actions -->\n              <div v-if=\"msg.role === 'assistant' && msg.content && !(streaming && i === messages.length - 1)\" style=\"display: flex; gap: 4px; padding: 0 4px\">\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"copyText(msg.content)\">\n                  <template #icon><n-icon :component=\"CopyOutline\" size=\"12\" /></template>\n                  复制\n                </n-button>\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"toggleRaw(i)\">\n                  <template #icon><n-icon :component=\"rawSet.has(i) ? LogoMarkdown : DocumentTextOutline\" size=\"12\" /></template>\n                  {{ rawSet.has(i) ? 'Markdown' : '原文' }}\n                </n-button>\n              </div>\n            </div>\n          </div>\n        </div>\n      </n-scrollbar>\n    </n-card>\n\n    <!-- Input Area -->\n    <n-card size=\"small\">\n      <n-space vertical :size=\"8\">\n        <!-- Attached Images Thumbnail Preview -->\n        <div v-if=\"attachedImages.length > 0\" style=\"display: flex; gap: 8px; flex-wrap: wrap; padding-bottom: 4px\">\n          <div\n            v-for=\"(img, idx) in attachedImages\"\n            :key=\"img.id\"\n            style=\"position: relative; width: 64px; height: 64px; border-radius: 6px; overflow: hidden; border: 1px solid rgba(99,226,183,0.4)\"\n          >\n            <img :src=\"img.dataUrl\" style=\"width: 100%; height: 100%; object-fit: cover\" />\n            <div\n              style=\"position: absolute; top: 2px; right: 2px; cursor: pointer; background: rgba(0,0,0,0.65); border-radius: 50%; width: 18px; height: 18px; display: flex; align-items: center; justify-content: center\"\n              @click=\"removeAttachedImage(idx)\"\n              title=\"移除图片\"\n            >\n              <n-icon :component=\"CloseOutline\" size=\"14\" color=\"#fff\" />\n            </div>\n          </div>\n        </div>\n\n        <div style=\"display: flex; gap: 8px; align-items: flex-end\">\n          <input\n            ref=\"fileInputRef\"\n            type=\"file\"\n            accept=\"image/*\"\n            multiple\n            style=\"display: none\"\n            @change=\"handleFileInputChange\"\n          />\n          <n-button\n            secondary\n            size=\"small\"\n            style=\"height: 60px; padding: 0 12px; flex-shrink: 0\"\n            :disabled=\"streaming || attachedImages.length >= 4\"\n            @click=\"triggerSelectImage\"\n            title=\"添加图片附件 (支持截图粘贴、文件上传，最多 4 张)\"\n          >\n            <template #icon><n-icon :component=\"ImageOutline\" size=\"20\" /></template>\n          </n-button>\n          <n-input\n            v-model:value=\"input\"\n            type=\"textarea\"\n            placeholder=\"输入消息或粘贴截图... (Enter 发送，Shift+Enter 换行)\"\n            :autosize=\"{ minRows: 2, maxRows: 5 }\"\n            style=\"flex: 1\"\n            :disabled=\"streaming\"\n            @paste=\"handlePaste\"\n            @keydown.enter=\"handleKeyDown\"\n          />\n          <n-button\n            type=\"primary\"\n            :loading=\"streaming\"\n            :disabled=\"(!input.trim() && attachedImages.length === 0) || !sessionId || streaming\"\n            style=\"height: 60px; padding: 0 16px; flex-shrink: 0\"\n            @click=\"send\"\n          >\n            <template #icon><n-icon :component=\"SendOutline\" /></template>\n          </n-button>\n        </div>\n        <n-collapse>\n          <n-collapse-item title=\"可用工具\" name=\"tools\">\n            <div style=\"display: flex; flex-direction: column; gap: 4px\">\n              <n-text v-for=\"(tool, idx) in toolDescriptions\" :key=\"idx\" depth=\"3\" style=\"font-size: 12px\">{{ tool }}</n-text>\n            </div>\n          </n-collapse-item>\n        </n-collapse>\n      </n-space>\n    </n-card>\n\n    <!-- Edit Modal -->\n    <n-modal v-model:show=\"editVisible\" preset=\"card\" title=\"编辑消息\" style=\"max-width: 520px\">\n      <n-input\n        v-model:value=\"editText\"\n        type=\"textarea\"\n        :autosize=\"{ minRows: 3, maxRows: 8 }\"\n        placeholder=\"编辑消息内容...\"\n      />\n      <template #footer>\n        <n-space justify=\"end\">\n          <n-button @click=\"editVisible = false\">取消</n-button>\n          <n-button type=\"primary\" @click=\"confirmEdit\" :disabled=\"!editText.trim()\">重新发送</n-button>\n        </n-space>\n      </template>\n    </n-modal>\n  </n-space>\n";
component.__scopeId = "data-v-936330d7";
export default component;
