import { defineStore } from 'pinia'
import { ref } from 'vue'
import * as signalR from '@microsoft/signalr'
import { HUB_URLS } from '@/config/env'

export interface ProgressUpdate {
  sourceId: string
  sourceName: string
  processedFiles: number
  totalFiles: number
  currentFile: string
  status: 'running' | 'completed' | 'failed'
  error?: string
}

// 与后端 IndexProgressEvent 对齐
interface IndexProgressEvent {
  indexId: string
  processedFiles: number
  totalFiles: number
  currentFile: string
  progressPercent: number
  timestamp: string
}

// 与后端 IndexCompletedEvent 对齐
interface IndexCompletedEvent {
  indexId: string
  totalFiles: number
  totalChunks: number
  totalVectors: number
  duration: string
  completedAt: string
  errors: string[]
}

export const useIndexStore = defineStore('index', () => {
  const connection = ref<signalR.HubConnection | null>(null)
  const isConnected = ref(false)
  const progressUpdates = ref<ProgressUpdate[]>([])
  const isIndexing = ref(false)

  // 正在运行的 indexId 集合，用于准确判断 isIndexing
  const activeJobIds = ref<Set<string>>(new Set())

  const connect = async () => {
    if (connection.value) return

    connection.value = new signalR.HubConnectionBuilder()
      .withUrl(HUB_URLS.index, { withCredentials: false })
      .withAutomaticReconnect()
      .build()

    // 重连后重新加入监控组
    connection.value.onreconnected(async () => {
      await connection.value?.invoke('JoinIndexGroup')
    })

    // IndexStarted：有新任务启动
    connection.value.on('IndexStarted', (data: { indexId: string }) => {
      activeJobIds.value.add(data.indexId)
      isIndexing.value = true
    })

    // IndexProgress：进度更新
    connection.value.on('IndexProgress', (_data: IndexProgressEvent) => {
      // 有进度说明仍在运行
      isIndexing.value = activeJobIds.value.size > 0
    })

    // IndexCompleted：任务完成
    connection.value.on('IndexCompleted', (data: IndexCompletedEvent) => {
      activeJobIds.value.delete(data.indexId)
      if (activeJobIds.value.size === 0) {
        isIndexing.value = false
      }
    })

    // 兼容旧事件名（保留，防止其他页面依赖）
    connection.value.on('IndexComplete', () => {
      activeJobIds.value.clear()
      isIndexing.value = false
    })

    try {
      await connection.value.start()
      isConnected.value = true
      // 加入监控组才能收到广播
      await connection.value.invoke('JoinIndexGroup')
    } catch (err) {
      console.error('SignalR connection error:', err)
      isConnected.value = false
    }
  }

  const disconnect = async () => {
    if (connection.value) {
      await connection.value.stop()
      connection.value = null
      isConnected.value = false
      activeJobIds.value.clear()
      isIndexing.value = false
    }
  }

  const clearProgress = () => {
    progressUpdates.value = []
  }

  return {
    connection,
    isConnected,
    progressUpdates,
    isIndexing,
    connect,
    disconnect,
    clearProgress
  }
})
