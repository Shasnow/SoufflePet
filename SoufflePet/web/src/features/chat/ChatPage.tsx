// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Paperclip } from 'lucide-react'
import { useEffect, useRef, useState, type DragEvent } from 'react'
import { invoke } from '@/bridge/client'
import type { AttachmentDto, ChatSnapshotDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useSessionStore } from '@/stores/sessionStore'
import { useToastStore } from '@/stores/toastStore'
import { GlobalUsageButton } from '@/ui/Usage'
import { chatRoute, navigate } from '@/app/router'
import { Composer } from './Composer'
import { MessageList } from './MessageList'
import { WelcomeHero } from './WelcomeHero'

/** 拖拽附件上限：与宿主 ChatRpc 的校验保持一致。 */
const MaxDropFiles = 20
const MaxDropFileBytes = 20 * 1024 * 1024
const MaxDropTotalBytes = 40 * 1024 * 1024
/** 分块大小：base64 后约 171 KB，仍在桥接 256 KB 载荷契约内。 */
const UploadChunkBytes = 128 * 1024

function hasFilePayload(event: DragEvent): boolean {
  return Array.from(event.dataTransfer.types).includes('Files')
}

/** 页面拿不到本地路径，只能把字节转成 base64 交给宿主落盘。 */
async function toBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer())
  let binary = ''
  const chunk = 0x8000
  for (let offset = 0; offset < bytes.length; offset += chunk) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk))
  }
  return btoa(binary)
}

/** 分块上传一个文件，返回宿主侧的上传 id。 */
async function uploadFile(sessionId: string, file: File): Promise<string> {
  const { uploadId } = await invoke<{ uploadId: string }>('chat.attach.begin', {
    sessionId,
    name: file.name,
    size: file.size,
  })
  for (let offset = 0; offset < file.size; offset += UploadChunkBytes) {
    await invoke('chat.attach.chunk', {
      uploadId,
      data: await toBase64(file.slice(offset, offset + UploadChunkBytes)),
    })
  }
  return uploadId
}

export function ChatPage({ sessionId }: { sessionId?: string }) {
  const activeId = useSessionStore((state) => state.activeId)
  const setActive = useSessionStore((state) => state.setActive)
  const setChatActive = useChatStore((state) => state.setActive)
  const upsertSnapshot = useChatStore((state) => state.upsertSnapshot)
  const applyOps = useChatStore((state) => state.applyOps)
  const usage = useAppStore((state) => state.usage)
  const pushToast = useToastStore((state) => state.push)
  const [loading, setLoading] = useState(false)
  const [missing, setMissing] = useState(false)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)
  // 已经向宿主确认过的会话：切换会话必须重新 open 一次，让宿主的活动会话与路由一致；
  // 用 ref 去重，避免 upsertSnapshot 引起的重渲染再次触发 open。
  const openedRef = useRef<string | null>(null)

  const targetId = sessionId ?? activeId ?? null
  const chat = useChatStore((state) =>
    targetId ? state.bySession[targetId] ?? null : null,
  )

  useEffect(() => {
    if (!targetId) return
    setActive(targetId)
    setChatActive(targetId)
    if (openedRef.current === targetId) return
    openedRef.current = targetId

    let alive = true
    setLoading(true)
    setMissing(false)
    invoke<ChatSnapshotDto>('session.open', { sessionId: targetId })
      .then((snapshot) => {
        if (!alive) return
        upsertSnapshot(snapshot)
        setLoading(false)
        if (!sessionId) navigate(chatRoute(targetId), true)
      })
      .catch((error) => {
        if (!alive) return
        // 允许下次重试同一个会话
        openedRef.current = null
        setLoading(false)
        setMissing(true)
        pushToast({ level: 'error', text: `打开会话失败：${error instanceof Error ? error.message : String(error)}` })
      })
    return () => {
      alive = false
    }
  }, [targetId, sessionId, setActive, setChatActive, upsertSnapshot, pushToast])

  if (!targetId) {
    return (
      <div className="error-screen">
        <div className="error-title">还没有会话</div>
        <div>在侧栏点「新会话」开始。</div>
      </div>
    )
  }

  if (!chat && loading) {
    return (
      <div className="chat-page" aria-busy="true">
        <div className="message-list" style={{ padding: 24 }}>
          <div style={{ maxWidth: 760, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 14 }}>
            <div className="skeleton" style={{ height: 48, width: '62%' }} />
            <div className="skeleton" style={{ height: 96, width: '86%' }} />
            <div className="skeleton" style={{ height: 64, width: '54%', alignSelf: 'flex-end' }} />
          </div>
        </div>
      </div>
    )
  }

  if (!chat) {
    return (
      <div className="error-screen">
        <div className="error-title">{missing ? '会话不存在' : '正在加载会话…'}</div>
        {missing ? (
          <button
            type="button"
            className="btn btn-sm"
            onClick={() => navigate(chatRoute(activeId ?? undefined), true)}
          >
            返回当前会话
          </button>
        ) : null}
      </div>
    )
  }

  const empty = chat.messages.length === 0

  const addDroppedFiles = async (files: File[]) => {
    if (files.length === 0) return
    if (files.length > MaxDropFiles) {
      pushToast({ level: 'error', text: `一次最多添加 ${MaxDropFiles} 个文件。` })
      return
    }
    if (files.some((file) => file.size === 0 && file.type === '')) {
      pushToast({ level: 'error', text: '暂不支持拖拽文件夹，请拖入文件。' })
      return
    }
    if (files.some((file) => file.size > MaxDropFileBytes)) {
      pushToast({ level: 'error', text: '单个文件不能超过 20 MB。' })
      return
    }
    if (files.reduce((sum, file) => sum + file.size, 0) > MaxDropTotalBytes) {
      pushToast({ level: 'error', text: '一次拖入的文件总大小不能超过 40 MB。' })
      return
    }

    try {
      // 桥梁单条载荷 256 KB：整文件一次性 base64 传不过去，按 128 KB 分块
      const uploads: string[] = []
      for (const file of files) uploads.push(await uploadFile(chat.sessionId, file))

      const result = await invoke<{ attachments: AttachmentDto[]; added: number }>('chat.attach.finish', {
        sessionId: chat.sessionId,
        uploads,
      })
      applyOps(chat.sessionId, [{ op: 'attachments', attachments: result.attachments }])
      pushToast({ level: 'success', text: `已添加 ${result.added} 个附件` })
    } catch (error) {
      pushToast({
        level: 'error',
        text: `添加附件失败：${error instanceof Error ? error.message : String(error)}`,
      })
    }
  }

  const handleDragEnter = (event: DragEvent<HTMLDivElement>) => {
    if (!hasFilePayload(event)) return
    event.preventDefault()
    dragDepth.current += 1
    setDragging(true)
  }

  const handleDragOver = (event: DragEvent<HTMLDivElement>) => {
    if (!hasFilePayload(event)) return
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }

  const handleDragLeave = (event: DragEvent<HTMLDivElement>) => {
    if (!hasFilePayload(event)) return
    dragDepth.current = Math.max(0, dragDepth.current - 1)
    if (dragDepth.current === 0) setDragging(false)
  }

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    if (!hasFilePayload(event)) return
    event.preventDefault()
    dragDepth.current = 0
    setDragging(false)
    void addDroppedFiles(Array.from(event.dataTransfer.files))
  }

  return (
    <div
      className="chat-page"
      onDragEnter={handleDragEnter}
      onDragOver={handleDragOver}
      onDragLeave={handleDragLeave}
      onDrop={handleDrop}
    >
      {dragging ? (
        <div className="drop-overlay" aria-hidden="true">
          <div className="drop-card">
            <Paperclip size={22} />
            <strong>松开以添加附件</strong>
            <span>图片、文本与常见文档，单个不超过 20 MB</span>
          </div>
        </div>
      ) : null}
      {empty ? (
        <WelcomeHero chat={chat} />
      ) : (
        <>
          <MessageList
            key={`messages-${chat.sessionId}`}
            sessionId={chat.sessionId}
            messages={chat.messages}
            generating={chat.generating}
          />
          <Composer key={`composer-${chat.sessionId}`} chat={chat} />
          <div className="composer-foot">
            <GlobalUsageButton usage={usage} />
          </div>
        </>
      )}
    </div>
  )
}