import { defineComponent as _defineComponent } from 'vue';
import { ref, nextTick, onMounted, onActivated, onUnmounted } from 'vue';
import {renderMarkdown} from '/shared/safe-markdown.js';
import { useMessage } from 'naive-ui';
import { ChatbubblesOutline, AddOutline, SendOutline, RefreshOutline, CreateOutline, CopyOutline, DocumentTextOutline, LogoMarkdown } from '@vicons/ionicons5';
import { marked } from 'marked';
import { API_URL } from '/app/config/env.js';
import { useAuthStore } from '/app/stores/auth.js';
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
        const renderMd = (content) => {
            const html = renderMarkdown(content.trimStart());
            return typeof html === 'string' ? html : '';
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
        const createSession = async () => {
            const res = await fetch(`${API_URL}/chat/sessions`, {
                method: 'POST',
                headers: getHeaders()
            });
            if (!res.ok)
                throw new Error(`创建会话失败 (${res.status})`);
            const data = await res.json();
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
            messages.value.splice(fromIndex);
            input.value = content;
            send();
        };
        const send = async () => {
            const text = input.value.trim();
            if (!text || !sessionId.value || streaming.value)
                return;
            messages.value.push({ role: 'user', content: text });
            input.value = '';
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
                    body: JSON.stringify({ sessionId: sessionId.value, message: text })
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
        const initSession = async () => {
            if (initialized.value)
                return;
            initialized.value = true;
            try {
                sessionId.value = await createSession();
                const res = await fetch(`${API_URL}/chat/tools`, { headers: getHeaders() });
                if (res.ok)
                    toolDescriptions.value = await res.json();
            }
            catch {
                initialized.value = false;
                message.error('初始化会话失败，请检查服务连接及 Chat 配置');
            }
        };
        onMounted(initSession);
        onActivated(() => { });
        const __returned__ = { cancel, message, authStore, sessionId, messages, input, streaming, scrollbarRef, toolDescriptions, initialized, rawSet, editVisible, editText, editIndex, renderMd, toggleRaw, copyText, startEdit, confirmEdit, getHeaders, scrollToBottom, createSession, deleteSession, newSession, handleKeyDown, retry, send, initSession, get ChatbubblesOutline() { return ChatbubblesOutline; }, get AddOutline() { return AddOutline; }, get SendOutline() { return SendOutline; }, get RefreshOutline() { return RefreshOutline; }, get CreateOutline() { return CreateOutline; }, get CopyOutline() { return CopyOutline; }, get DocumentTextOutline() { return DocumentTextOutline; }, get LogoMarkdown() { return LogoMarkdown; } };
        return __returned__;
    }
});

component.template = "\n  <n-space vertical :size=\"16\">\n    <!-- Header Bar -->\n    <n-card size=\"small\">\n      <n-space align=\"center\" justify=\"space-between\">\n        <n-space align=\"center\" :size=\"8\">\n          <n-icon :component=\"ChatbubblesOutline\" size=\"20\" />\n          <n-text strong>AI 对话</n-text>\n          <n-tag size=\"small\" type=\"default\" round>{{ sessionId ? sessionId.slice(0, 8) : '—' }}</n-tag>\n        <n-button v-if=\"streaming\" @click=\"cancel\">停止生成</n-button></n-space>\n        <n-button size=\"small\" secondary @click=\"newSession\" :disabled=\"streaming\">\n          <template #icon><n-icon :component=\"AddOutline\" /></template>\n          新对话\n        </n-button>\n      </n-space>\n    </n-card>\n\n    <!-- Message List -->\n    <n-card\n      :style=\"{ height: 'calc(100vh - 300px)', overflow: 'hidden' }\"\n      :content-style=\"{ padding: 0, height: '100%', display: 'flex', flexDirection: 'column' }\"\n    >\n      <n-scrollbar ref=\"scrollbarRef\" style=\"flex: 1\">\n        <div style=\"padding: 16px; display: flex; flex-direction: column; gap: 16px; min-height: 100%\">\n          <!-- Empty state -->\n          <div v-if=\"messages.length === 0\" style=\"flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 60px 0; gap: 12px\">\n            <n-icon :component=\"ChatbubblesOutline\" size=\"52\" color=\"#63e2b7\" />\n            <n-text depth=\"3\">开始对话吧，可以搜索知识库或查询索引状态</n-text>\n          </div>\n\n          <!-- Messages -->\n          <div\n            v-for=\"(msg, i) in messages\"\n            :key=\"i\"\n            :style=\"{ display: 'flex', justifyContent: msg.role === 'user' ? 'flex-end' : 'flex-start' }\"\n          >\n            <div :style=\"{ maxWidth: '80%', display: 'flex', flexDirection: 'column', gap: '4px', alignItems: msg.role === 'user' ? 'flex-end' : 'flex-start' }\">\n              <n-card\n                size=\"small\"\n                :style=\"{\n                  background: msg.role === 'user' ? 'rgba(99,226,183,0.1)' : undefined,\n                  borderColor: msg.role === 'user' ? 'rgba(99,226,183,0.3)' : undefined\n                }\"\n              >\n                <!-- User message -->\n                <div v-if=\"msg.role === 'user'\" style=\"white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6\">{{ msg.content.trimStart() }}</div>\n\n                <!-- Assistant message -->\n                <div v-else>\n                  <div v-if=\"rawSet.has(i)\" style=\"white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6\">{{ msg.content.trimStart() }}<span v-if=\"streaming && i === messages.length - 1\" class=\"cursor-blink\">▌</span></div>\n                  <div v-else class=\"markdown-body\" v-html=\"renderMd(msg.content) + (streaming && i === messages.length - 1 ? '<span class=\\'cursor-blink\\'>▌</span>' : '')\"></div>\n                </div>\n              </n-card>\n\n              <!-- User actions -->\n              <div v-if=\"msg.role === 'user' && !streaming\" style=\"display: flex; gap: 4px; padding: 0 4px\">\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"startEdit(i, msg.content)\">\n                  <template #icon><n-icon :component=\"CreateOutline\" size=\"12\" /></template>\n                  编辑\n                </n-button>\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"retry(msg.content, i)\">\n                  <template #icon><n-icon :component=\"RefreshOutline\" size=\"12\" /></template>\n                  重试\n                </n-button>\n              </div>\n\n              <!-- Assistant actions -->\n              <div v-if=\"msg.role === 'assistant' && msg.content && !(streaming && i === messages.length - 1)\" style=\"display: flex; gap: 4px; padding: 0 4px\">\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"copyText(msg.content)\">\n                  <template #icon><n-icon :component=\"CopyOutline\" size=\"12\" /></template>\n                  复制\n                </n-button>\n                <n-button text size=\"tiny\" style=\"opacity: 0.6; font-size: 11px\" @click=\"toggleRaw(i)\">\n                  <template #icon><n-icon :component=\"rawSet.has(i) ? LogoMarkdown : DocumentTextOutline\" size=\"12\" /></template>\n                  {{ rawSet.has(i) ? 'Markdown' : '原文' }}\n                </n-button>\n              </div>\n            </div>\n          </div>\n\n          <!-- Thinking indicator -->\n          <div v-if=\"streaming && (messages.length === 0 || messages[messages.length - 1]?.role === 'user')\" style=\"display: flex\">\n            <n-card size=\"small\">\n              <n-space align=\"center\" :size=\"8\">\n                <n-spin size=\"small\" />\n                <n-text depth=\"3\" style=\"font-size: 13px\">思考中...</n-text>\n              </n-space>\n            </n-card>\n          </div>\n        </div>\n      </n-scrollbar>\n    </n-card>\n\n    <!-- Input Area -->\n    <n-card size=\"small\">\n      <n-space vertical :size=\"8\">\n        <div style=\"display: flex; gap: 8px; align-items: flex-end\">\n          <n-input\n            v-model:value=\"input\"\n            type=\"textarea\"\n            placeholder=\"输入消息... (Enter 发送，Shift+Enter 换行)\"\n            :autosize=\"{ minRows: 2, maxRows: 5 }\"\n            style=\"flex: 1\"\n            :disabled=\"streaming\"\n            @keydown.enter=\"handleKeyDown\"\n          />\n          <n-button\n            type=\"primary\"\n            :loading=\"streaming\"\n            :disabled=\"!input.trim() || !sessionId || streaming\"\n            style=\"height: 60px; padding: 0 16px; flex-shrink: 0\"\n            @click=\"send\"\n          >\n            <template #icon><n-icon :component=\"SendOutline\" /></template>\n          </n-button>\n        </div>\n        <n-collapse>\n          <n-collapse-item title=\"可用工具\" name=\"tools\">\n            <div style=\"display: flex; flex-direction: column; gap: 4px\">\n              <n-text v-for=\"(tool, idx) in toolDescriptions\" :key=\"idx\" depth=\"3\" style=\"font-size: 12px\">{{ tool }}</n-text>\n            </div>\n          </n-collapse-item>\n        </n-collapse>\n      </n-space>\n    </n-card>\n\n    <!-- Edit Modal -->\n    <n-modal v-model:show=\"editVisible\" preset=\"card\" title=\"编辑消息\" style=\"max-width: 520px\">\n      <n-input\n        v-model:value=\"editText\"\n        type=\"textarea\"\n        :autosize=\"{ minRows: 3, maxRows: 8 }\"\n        placeholder=\"编辑消息内容...\"\n      />\n      <template #footer>\n        <n-space justify=\"end\">\n          <n-button @click=\"editVisible = false\">取消</n-button>\n          <n-button type=\"primary\" @click=\"confirmEdit\" :disabled=\"!editText.trim()\">重新发送</n-button>\n        </n-space>\n      </template>\n    </n-modal>\n  </n-space>\n";
component.__scopeId = "data-v-936330d7";
export default component;
import {ragFetch as fetch} from '/core/transport/index.js';
