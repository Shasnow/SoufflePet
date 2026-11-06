// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { FileText, Image as ImageIcon, Paperclip, Send, Square, X } from 'lucide-react'
import { useEffect, useRef } from 'react'
import { invoke } from '@/bridge/client'
import type { AttachmentDto, ChatSnapshotDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useToastStore } from '@/stores/toastStore'
import { ApprovalPicker } from './ApprovalPicker'
import { ModelPicker } from './ModelPicker'
import { WorkspacePicker } from './WorkspacePicker'

interface ComposerProps {
  chat: ChatSnapshotDto
  hero?: boolean
}

export function Composer({ chat, hero = false }: ComposerProps) {
  const petName = useAppStore((state) => state.pet.name)
  const setDraft = useChatStore((state) => state.setDraft)
  const applyOps = useChatStore((state) => state.applyOps)
  const pushToast = useToastStore((state) => state.push)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const composing = useRef(false)
  const draftTimer = useRef<number | null>(null)
  const sessionId = chat.sessionId

  useEffect(() => {
    const textarea = textareaRef.current
    if (!textarea) return
    textarea.style.height = 'auto'
    textarea.style.height = `${Math.min(160, Math.max(46, textarea.scrollHeight))}px`
  }, [chat.draft])

  useEffect(() => {
    return () => {
      if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
    }
  }, [])

  const syncDraft = (value: string) => {
    setDraft(value)
    if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
    draftTimer.current = window.setTimeout(() => {
      draftTimer.current = null
      void invoke('chat.draft', { text: value }).catch(() => undefined)
    }, 100)
  }

  const send = async () => {
    if (chat.generating) {
      try {
        await invoke('chat.stop')
      } catch (error) {
        pushToast({ level: 'error', text: `停止失败：${error instanceof Error ? error.message : String(error)}` })
      }
      return
    }

    const text = chat.draft.trim()
    if (!text && chat.attachments.length === 0) return

    try {
      await invoke('chat.send', {
        text,
        attachmentIds: chat.attachments.map((attachment) => attachment.id),
      })
      // 宿主没有 draft 增量 op：发送成功后在页面侧立即清空，避免旧草稿留在输入框
      if (draftTimer.current !== null) {
        window.clearTimeout(draftTimer.current)
        draftTimer.current = null
      }
      setDraft('')
    } catch (error) {
      pushToast({ level: 'error', text: `发送失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const attach = async (mode: 'pick' | 'paste') => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>(
        mode === 'pick' ? 'chat.attach.pick' : 'chat.attach.paste',
      )
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `添加附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeAttachment = async (id: string) => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>('chat.attach.remove', { id })
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `移除附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeLast = () => {
    const last = chat.attachments[chat.attachments.length - 1]
    if (last) void removeAttachment(last.id)
  }

  const canSend = chat.draft.trim().length > 0 || chat.attachments.length > 0 || chat.generating
  // 还没发过消息的新会话（含从工作区新建的）可以随意切换工作区；
  // 一旦有对话内容，绑定工作区的会话就不再提供入口
  const canPickWorkspace = chat.messages.length === 0 || chat.workspace == null

  return (
    <div className="composer-wrap">
      {canPickWorkspace || chat.generating ? (
        <div className="composer-context">
          {canPickWorkspace ? <WorkspacePicker chat={chat} /> : null}
          {chat.generating ? (
            <span style={{ color: 'var(--text-3)', fontSize: 11.5 }}>正在回复，可点右侧停止</span>
          ) : null}
        </div>
      ) : null}

      <div className={`composer ${chat.generating ? 'is-disabled' : ''}`}>
        {chat.attachments.length > 0 ? (
          <div className="composer-attachments">
            {chat.attachments.map((attachment) => (
              <span key={attachment.id} className="attachment-chip" title={attachment.path}>
                {attachment.isImage && attachment.url ? (
                  <img src={attachment.url} alt="" width={18} height={18} style={{ borderRadius: 4, objectFit: 'cover' }} />
                ) : attachment.isImage ? (
                  <ImageIcon size={13} />
                ) : (
                  <FileText size={13} />
                )}
                <span className="name">{attachment.name}</span>
                <button
                  type="button"
                  className="remove"
                  aria-label={`移除附件 ${attachment.name}`}
                  onClick={() => void removeAttachment(attachment.id)}
                >
                  <X size={11} />
                </button>
              </span>
            ))}
          </div>
        ) : null}

        <textarea
          ref={textareaRef}
          className="composer-input"
          value={chat.draft}
          autoFocus={hero}
          placeholder={`和${petName}说点什么…`}
          aria-label="输入消息"
          onChange={(event) => syncDraft(event.target.value)}
          onCompositionStart={() => {
            composing.current = true
          }}
          onCompositionEnd={() => {
            composing.current = false
          }}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && !event.shiftKey && !composing.current) {
              event.preventDefault()
              void send()
              return
            }
            if (event.key === 'Backspace' && chat.draft.length === 0 && chat.attachments.length > 0) {
              event.preventDefault()
              removeLast()
            }
          }}
          onPaste={(event) => {
            const hasFile =
              Array.from(event.clipboardData.files).length > 0 ||
              Array.from(event.clipboardData.items).some((item) => item.kind === 'file')
            if (!hasFile) return
            event.preventDefault()
            void attach('paste')
          }}
        />

        <div className="composer-bar">
          <button type="button" className="composer-tool" aria-label="添加附件" onClick={() => void attach('pick')}>
            <Paperclip size={15} />
          </button>
          <ApprovalPicker mode={chat.approvalMode} />
          <span className="spacer" />
          <ModelPicker chat={chat} />
          <button
            type="button"
            className={`send-btn ${chat.generating ? 'is-stop' : ''}`}
            aria-label={chat.generating ? '停止生成' : '发送'}
            title={chat.generating ? '停止生成' : '发送'}
            disabled={!canSend}
            onClick={() => void send()}
          >
            {chat.generating ? <Square size={14} fill="currentColor" /> : <Send size={15} />}
          </button>
        </div>
      </div>
    </div>
  )
}