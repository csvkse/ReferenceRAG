<template>
  <n-space vertical :size="16">
    <!-- Header Bar -->
    <n-card size="small">
      <n-space align="center" justify="space-between">
        <n-space align="center" :size="8">
          <n-icon :component="ChatbubblesOutline" size="20" />
          <n-text strong>AI 对话</n-text>
          <n-tag size="small" type="default" round>{{ sessionId ? sessionId.slice(0, 8) : '—' }}</n-tag>
        </n-space>
        <n-button size="small" secondary @click="newSession" :disabled="streaming">
          <template #icon><n-icon :component="AddOutline" /></template>
          新对话
        </n-button>
      </n-space>
    </n-card>

    <!-- Message List -->
    <n-card
      :style="{ height: 'calc(100vh - 300px)', overflow: 'hidden' }"
      :content-style="{ padding: 0, height: '100%', display: 'flex', flexDirection: 'column' }"
    >
      <n-scrollbar ref="scrollbarRef" style="flex: 1">
        <div style="padding: 16px; display: flex; flex-direction: column; gap: 16px; min-height: 100%">
          <!-- Empty state -->
          <div v-if="messages.length === 0" style="flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 60px 0; gap: 12px">
            <n-icon :component="ChatbubblesOutline" size="52" color="#63e2b7" />
            <n-text depth="3">开始对话吧，可以搜索知识库或查询索引状态</n-text>
          </div>

          <!-- Messages -->
          <div
            v-for="(msg, i) in messages"
            :key="i"
            :style="{ display: 'flex', justifyContent: msg.role === 'user' ? 'flex-end' : 'flex-start' }"
          >
            <div :style="{ maxWidth: '80%', display: 'flex', flexDirection: 'column', gap: '4px', alignItems: msg.role === 'user' ? 'flex-end' : 'flex-start' }">
              <n-card
                size="small"
                :style="{
                  background: msg.role === 'user' ? 'rgba(99,226,183,0.1)' : undefined,
                  borderColor: msg.role === 'user' ? 'rgba(99,226,183,0.3)' : undefined
                }"
              >
                <!-- User message -->
                <div v-if="msg.role === 'user'" style="display: flex; align-items: flex-start; gap: 8px">
                  <div style="flex: 1; white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6">{{ msg.content.trimStart() }}</div>
                  <div v-if="!streaming" style="display: flex; flex-direction: column; gap: 2px; flex-shrink: 0; margin-top: 2px">
                    <n-button text size="tiny" style="opacity: 0.5" title="编辑" @click="startEdit(i, msg.content)">
                      <template #icon><n-icon :component="CreateOutline" size="13" /></template>
                    </n-button>
                    <n-button text size="tiny" style="opacity: 0.5" title="重试" @click="retry(msg.content, i)">
                      <template #icon><n-icon :component="RefreshOutline" size="13" /></template>
                    </n-button>
                  </div>
                </div>

                <!-- Assistant message -->
                <div v-else>
                  <div v-if="rawSet.has(i)" style="white-space: pre-wrap; word-break: break-word; font-size: 14px; line-height: 1.6">{{ msg.content.trimStart() }}<span v-if="streaming && i === messages.length - 1" class="cursor-blink">▌</span></div>
                  <div v-else class="markdown-body" v-html="renderMd(msg.content) + (streaming && i === messages.length - 1 ? '<span class=\'cursor-blink\'>▌</span>' : '')"></div>
                </div>
              </n-card>

              <!-- Assistant actions -->
              <div v-if="msg.role === 'assistant' && msg.content && !(streaming && i === messages.length - 1)" style="display: flex; gap: 4px; padding: 0 4px">
                <n-button text size="tiny" style="opacity: 0.6; font-size: 11px" @click="copyText(msg.content)">
                  <template #icon><n-icon :component="CopyOutline" size="12" /></template>
                  复制
                </n-button>
                <n-button text size="tiny" style="opacity: 0.6; font-size: 11px" @click="toggleRaw(i)">
                  <template #icon><n-icon :component="rawSet.has(i) ? LogoMarkdown : DocumentTextOutline" size="12" /></template>
                  {{ rawSet.has(i) ? 'Markdown' : '原文' }}
                </n-button>
              </div>
            </div>
          </div>

          <!-- Thinking indicator -->
          <div v-if="streaming && (messages.length === 0 || messages[messages.length - 1]?.role === 'user')" style="display: flex">
            <n-card size="small">
              <n-space align="center" :size="8">
                <n-spin size="small" />
                <n-text depth="3" style="font-size: 13px">思考中...</n-text>
              </n-space>
            </n-card>
          </div>
        </div>
      </n-scrollbar>
    </n-card>

    <!-- Input Area -->
    <n-card size="small">
      <n-space vertical :size="8">
        <div style="display: flex; gap: 8px; align-items: flex-end">
          <n-input
            v-model:value="input"
            type="textarea"
            placeholder="输入消息... (Enter 发送，Shift+Enter 换行)"
            :autosize="{ minRows: 2, maxRows: 5 }"
            style="flex: 1"
            :disabled="streaming"
            @keydown.enter="handleKeyDown"
          />
          <n-button
            type="primary"
            :loading="streaming"
            :disabled="!input.trim() || !sessionId || streaming"
            style="height: 60px; padding: 0 16px; flex-shrink: 0"
            @click="send"
          >
            <template #icon><n-icon :component="SendOutline" /></template>
          </n-button>
        </div>
        <n-text depth="3" style="font-size: 12px">
          工具：SearchKnowledge（知识库检索）· GetIndexStatus（索引状态）
        </n-text>
      </n-space>
    </n-card>

    <!-- Edit Modal -->
    <n-modal v-model:show="editVisible" preset="card" title="编辑消息" style="max-width: 520px">
      <n-input
        v-model:value="editText"
        type="textarea"
        :autosize="{ minRows: 3, maxRows: 8 }"
        placeholder="编辑消息内容..."
      />
      <template #footer>
        <n-space justify="end">
          <n-button @click="editVisible = false">取消</n-button>
          <n-button type="primary" @click="confirmEdit" :disabled="!editText.trim()">重新发送</n-button>
        </n-space>
      </template>
    </n-modal>
  </n-space>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted, onActivated, defineOptions } from 'vue'

defineOptions({ name: 'Chat' })
import { useMessage } from 'naive-ui'
import {
  ChatbubblesOutline, AddOutline, SendOutline, RefreshOutline,
  CreateOutline, CopyOutline, DocumentTextOutline, LogoMarkdown
} from '@vicons/ionicons5'
import { marked } from 'marked'
import { API_URL } from '@/config/env'
import { useAuthStore } from '@/stores/auth'

marked.setOptions({ breaks: true })

interface Message {
  role: 'user' | 'assistant'
  content: string
}

const message = useMessage()
const authStore = useAuthStore()

const sessionId = ref<string | null>(null)
const messages = ref<Message[]>([])
const input = ref('')
const streaming = ref(false)
const scrollbarRef = ref<any>(null)

const rawSet = ref(new Set<number>())
const editVisible = ref(false)
const editText = ref('')
const editIndex = ref(-1)

const renderMd = (content: string): string => {
  const html = marked.parse(content.trimStart())
  return typeof html === 'string' ? html : ''
}

const toggleRaw = (i: number) => {
  const next = new Set(rawSet.value)
  if (next.has(i)) next.delete(i)
  else next.add(i)
  rawSet.value = next
}

const copyText = async (content: string) => {
  await navigator.clipboard.writeText(content).catch(() => {})
  message.success('已复制')
}

const startEdit = (i: number, content: string) => {
  editIndex.value = i
  editText.value = content
  editVisible.value = true
}

const confirmEdit = () => {
  if (!editText.value.trim()) return
  const text = editText.value.trim()
  const idx = editIndex.value
  editVisible.value = false
  messages.value.splice(idx)
  input.value = text
  send()
}

const getHeaders = (): Record<string, string> => {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (authStore.apiKey) headers['X-API-Key'] = authStore.apiKey
  return headers
}

const scrollToBottom = async () => {
  await nextTick()
  scrollbarRef.value?.scrollTo({ top: 999999, behavior: 'smooth' })
}

const createSession = async (): Promise<string> => {
  const res = await fetch(`${API_URL}/chat/sessions`, {
    method: 'POST',
    headers: getHeaders()
  })
  if (!res.ok) throw new Error(`创建会话失败 (${res.status})`)
  const data = await res.json()
  return (data.SessionId ?? data.sessionId) as string
}

const deleteSession = async (id: string) => {
  const headers = getHeaders()
  delete headers['Content-Type']
  await fetch(`${API_URL}/chat/sessions/${id}`, { method: 'DELETE', headers }).catch(() => {})
}

const newSession = async () => {
  if (streaming.value) return
  if (sessionId.value) await deleteSession(sessionId.value)
  messages.value = []
  rawSet.value = new Set()
  try {
    sessionId.value = await createSession()
  } catch (e: any) {
    message.error(e.message || '创建会话失败')
  }
}

const handleKeyDown = (e: KeyboardEvent) => {
  if (e.isComposing) return
  if (!e.shiftKey) {
    e.preventDefault()
    send()
  }
}

const retry = (content: string, fromIndex: number) => {
  if (streaming.value) return
  messages.value.splice(fromIndex)
  input.value = content
  send()
}

const send = async () => {
  const text = input.value.trim()
  if (!text || !sessionId.value || streaming.value) return

  messages.value.push({ role: 'user', content: text })
  input.value = ''
  streaming.value = true
  await scrollToBottom()

  const assistantMsg: Message = { role: 'assistant', content: '' }
  messages.value.push(assistantMsg)

  try {
    const res = await fetch(`${API_URL}/chat/stream`, {
      method: 'POST',
      headers: getHeaders(),
      body: JSON.stringify({ sessionId: sessionId.value, message: text })
    })

    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    if (!res.body) throw new Error('无响应体')

    const reader = res.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''

    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue
        const json = line.slice(6).trim()
        if (!json) continue
        try {
          const evt = JSON.parse(json)
          if (evt.type === 'text' && evt.delta) {
            messages.value[messages.value.length - 1].content += evt.delta
            await scrollToBottom()
          } else if (evt.type === 'error') {
            message.error(evt.message || '发生错误')
          }
        } catch { /* ignore parse errors */ }
      }
    }
  } catch (err: any) {
    if (err.name !== 'AbortError') {
      message.error(err.message || '请求失败')
      const last = messages.value[messages.value.length - 1]
      if (last?.role === 'assistant' && !last.content) last.content = '[请求失败]'
    }
  } finally {
    streaming.value = false
    await scrollToBottom()
  }
}

const initSession = async () => {
  if (sessionId.value) return
  try {
    sessionId.value = await createSession()
  } catch {
    message.error('初始化会话失败，请检查服务连接及 Chat 配置')
  }
}

onMounted(initSession)
onActivated(initSession)
</script>

<style scoped>
.cursor-blink {
  animation: blink 1s step-end infinite;
}
@keyframes blink {
  0%, 100% { opacity: 1; }
  50% { opacity: 0; }
}

.markdown-body {
  font-size: 14px;
  line-height: 1.7;
  word-break: break-word;
}
.markdown-body :deep(p) { margin: 0.4em 0; }
.markdown-body :deep(p:first-child) { margin-top: 0; }
.markdown-body :deep(p:last-child) { margin-bottom: 0; }
.markdown-body :deep(h1), .markdown-body :deep(h2), .markdown-body :deep(h3), .markdown-body :deep(h4) {
  font-weight: 700; margin: 0.8em 0 0.4em; line-height: 1.3;
}
.markdown-body :deep(h1) { font-size: 1.3em; }
.markdown-body :deep(h2) { font-size: 1.15em; }
.markdown-body :deep(h3) { font-size: 1.05em; }
.markdown-body :deep(ul), .markdown-body :deep(ol) { padding-left: 1.4em; margin: 0.4em 0; }
.markdown-body :deep(li) { margin: 0.2em 0; }
.markdown-body :deep(code) {
  font-family: monospace; font-size: 0.85em;
  padding: 0.1em 0.35em; border-radius: 4px;
  background: rgba(128,128,128,0.15);
}
.markdown-body :deep(pre) {
  margin: 0.6em 0; border-radius: 8px; overflow-x: auto;
  padding: 0.8em 1em; background: rgba(128,128,128,0.08);
  border: 1px solid rgba(128,128,128,0.15);
}
.markdown-body :deep(pre code) { background: transparent; padding: 0; }
.markdown-body :deep(blockquote) {
  border-left: 3px solid currentColor; opacity: 0.7;
  margin: 0.5em 0; padding: 0.2em 0.8em;
}
.markdown-body :deep(table) {
  border-collapse: collapse; width: 100%; margin: 0.6em 0; font-size: 0.9em;
}
.markdown-body :deep(th), .markdown-body :deep(td) {
  border: 1px solid rgba(128,128,128,0.3); padding: 0.3em 0.7em; text-align: left;
}
.markdown-body :deep(th) { font-weight: 700; background: rgba(128,128,128,0.1); }
.markdown-body :deep(a) { text-decoration: underline; opacity: 0.8; }
.markdown-body :deep(hr) { border: none; border-top: 1px solid rgba(128,128,128,0.3); margin: 0.8em 0; }
.markdown-body :deep(strong) { font-weight: 700; }
.markdown-body :deep(em) { font-style: italic; }
</style>
