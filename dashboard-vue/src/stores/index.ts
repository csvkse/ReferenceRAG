import { defineStore } from 'pinia'
import { ref } from 'vue'
import * as signalR from '@microsoft/signalr'

export interface ProgressUpdate {
  sourceId: string
  sourceName: string
  processedFiles: number
  totalFiles: number
  currentFile: string
  status: 'running' | 'completed' | 'failed'
  error?: string
}

export const useIndexStore = defineStore('index', () => {
  const connection = ref<signalR.HubConnection | null>(null)
  const isConnected = ref(false)
  const progressUpdates = ref<ProgressUpdate[]>([])
  const isIndexing = ref(false)

  const connect = async () => {
    if (connection.value) return

    const hubUrl = '/hubs/index'
    connection.value = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl)
      .withAutomaticReconnect()
      .build()

    connection.value.on('ProgressUpdate', (data: ProgressUpdate) => {
      const index = progressUpdates.value.findIndex(p => p.sourceId === data.sourceId)
      if (index >= 0) {
        progressUpdates.value[index] = data
      } else {
        progressUpdates.value.push(data)
      }

      if (data.status === 'running') {
        isIndexing.value = true
      } else if (data.status === 'completed' || data.status === 'failed') {
        const runningCount = progressUpdates.value.filter(p => p.status === 'running').length
        if (runningCount === 0) {
          isIndexing.value = false
        }
      }
    })

    connection.value.on('IndexComplete', () => {
      isIndexing.value = false
    })

    try {
      await connection.value.start()
      isConnected.value = true
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
