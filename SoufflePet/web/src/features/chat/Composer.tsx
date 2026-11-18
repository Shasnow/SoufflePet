// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { FileText, Image as ImageIcon, Paperclip, Send, Square, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { AttachmentDto, ChatSnapshotDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useToastStore } from '@/stores/toastStore'
import { ApprovalPicker } from './ApprovalPicker'
import { AskPanel } from './AskPanel'
import { MentionPicker, type MentionEntry } from './MentionPicker'
import { ModelPicker } from './ModelPicker'
import { WorkspacePicker } from './WorkspacePicker'

interface ComposerProps {
  chat: ChatSnapshotDto
  hero?: boolean
}

/** 正在输入的 @ 引用片段：query 是 @ 之后、光标之前的文本。 */
interface MentionToken {
  query: string
  start: number
  end: number
}

/** 找出光标前正在输入的 @ 引用；@ 必须在行首或空白之后，且之后不能有空白。 */
function findMentionToken(value: string, caret: number): MentionToken | null {
  const before = value.slice(0, caret)
  const at = before.lastIndexOf('@')
  if (at < 0) return null
  if (at > 0 && !/\s/.test(before[at - 1])) return null
  const query = before.slice(at + 1)
  if (/\s/.test(query)) return null
  return { query, start: at, end: caret }
}

/** 把字节数格式化成附件副标题里的可读大小。 */
function formatSize(bytes?: number): string {
  if (bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return ''
  if (bytes < 1024) return `${Math.round(bytes)} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let index = 0
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024
    index += 1
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[index]}`
}

/** 文件胶囊第二行：扩展名 · 大小，例如「LOG · 33.9 KB」。 */
function attachmentMeta(name: string, size?: number): string {
  const dot = name.lastIndexOf('.')
  const extension = dot > 0 && dot < name.length - 1 ? name.slice(dot + 1).toUpperCase() : 'FILE'
  const readable = formatSize(size)
  return readable ? `${extension} · ${readable}` : extension
}

export function Composer({ chat, hero = false }: ComposerProps) {
  const petName = useAppStore((state) => state.pet.name)
  const setDraft = useChatStore((state) => state.setDraft)
  const applyOps = useChatStore((state) => state.applyOps)
  const pushToast = useToastStore((state) => state.push)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const composing = useRef(false)
  const draftTimer = useRef<number | null>(null)
  // 等待同步给宿主的草稿：卸载（切换会话）时补发一次，避免最后 100ms 的输入丢失
  const pendingDraft = useRef<string | null>(null)
  const sessionId = chat.sessionId
  const [mention, setMention] = useState<MentionToken | null>(null)
  const [mentionEntries, setMentionEntries] = useState<MentionEntry[]>([])
  const [mentionIndex, setMentionIndex] = useState(0)
  const [mentionLoading, setMentionLoading] = useState(false)
  const mentionRequest = useRef(0)

  useEffect(() => {
    const textarea = textareaRef.current
    if (!textarea) return
    textarea.style.height = 'auto'
    textarea.style.height = `${Math.min(160, Math.max(46, textarea.scrollHeight))}px`
  }, [chat.draft])

  useEffect(() => {
    return () => {
      if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
      if (pendingDraft.current !== null) {
        void invoke('chat.draft', { sessionId, text: pendingDraft.current }).catch(() => undefined)
      }
    }
  }, [sessionId])

  useEffect(() => {
    if (!mention) {
      setMentionEntries([])
      return
    }

    const seq = ++mentionRequest.current
    setMentionLoading(true)
    // 输入时逐字查询：防抖后再问宿主，避免每个字符一次 RPC
    const timer = window.setTimeout(() => {
      void invoke<{ entries: MentionEntry[] }>('chat.mention.search', { sessionId, query: mention.query })
        .then((result) => {
          if (mentionRequest.current !== seq) return
          setMentionEntries(result.entries)
          setMentionIndex(0)
        })
        .catch(() => {
          if (mentionRequest.current === seq) setMentionEntries([])
        })
        .finally(() => {
          if (mentionRequest.current === seq) setMentionLoading(false)
        })
    }, 120)

    return () => window.clearTimeout(timer)
  }, [mention, sessionId])

  const syncDraft = (value: string) => {
    setDraft(sessionId, value)
    pendingDraft.current = value
    if (draftTimer.current !== null) window.clearTimeout(draftTimer.current)
    draftTimer.current = window.setTimeout(() => {
      draftTimer.current = null
      pendingDraft.current = null
      void invoke('chat.draft', { sessionId, text: value }).catch(() => undefined)
    }, 100)
  }

  const send = async () => {
    if (chat.generating) {
      try {
        await invoke('chat.stop', { sessionId })
      } catch (error) {
        pushToast({ level: 'error', text: `停止失败：${error instanceof Error ? error.message : String(error)}` })
      }
      return
    }

    const text = chat.draft.trim()
    if (!text && chat.attachments.length === 0) return

    setMention(null)
    try {
      await invoke('chat.send', {
        sessionId,
        text,
        attachmentIds: chat.attachments.map((attachment) => attachment.id),
      })
      // 宿主没有 draft 增量 op：发送成功后在页面侧立即清空，避免旧草稿留在输入框
      if (draftTimer.current !== null) {
        window.clearTimeout(draftTimer.current)
        draftTimer.current = null
      }
      pendingDraft.current = null
      setDraft(sessionId, '')
    } catch (error) {
      pushToast({ level: 'error', text: `发送失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const attach = async (mode: 'pick' | 'paste') => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>(
        mode === 'pick' ? 'chat.attach.pick' : 'chat.attach.paste',
        { sessionId },
      )
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `添加附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeAttachment = async (id: string) => {
    try {
      const result = await invoke<{ attachments: AttachmentDto[] }>('chat.attach.remove', { sessionId, id })
      applyOps(sessionId, [{ op: 'attachments', attachments: result.attachments }])
    } catch (error) {
      pushToast({ level: 'error', text: `移除附件失败：${error instanceof Error ? error.message : String(error)}` })
    }
  }

  const removeLast = () => {
    const last = chat.attachments[chat.attachments.length - 1]
    if (last) void removeAttachment(last.id)
  }

  /** 把选中的候选替换掉 @ 片段：文件补一个空格收尾，目录保留列表继续往下钻。 */
  const pickMention = (entry: MentionEntry) => {
    if (!mention) return
    const inserted = `@${entry.path}${entry.isDirectory ? '/' : ' '}`
    const next = `${chat.draft.slice(0, mention.start)}${inserted}${chat.draft.slice(mention.end)}`
    syncDraft(next)

    const caret = mention.start + inserted.length
    setMention(entry.isDirectory ? { query: `${entry.path}/`, start: mention.start, end: caret } : null)

    const textarea = textareaRef.current
    if (!textarea) return
    window.requestAnimationFrame(() => {
      textarea.focus()
      textarea.setSelectionRange(caret, caret)
    })
  }

  const canSend = chat.draft.trim().length > 0 || chat.attachments.length > 0 || chat.generating
  // 还没发过消息的新会话（含从工作区新建的）可以随意切换工作区；
  // 一旦有对话内容，绑定工作区的会话就不再提供入口
  const canPickWorkspace = chat.messages.length === 0 || chat.workspace == null

  return (
    <div className="composer-wrap">
      {/* 询问工具属于当前会话：内联在输入卡上方，不遮罩页面 */}
      {chat.ask ? <AskPanel sessionId={sessionId} ask={chat.ask} /> : null}

      {canPickWorkspace ? (
        <div className="composer-context">
          <WorkspacePicker chat={chat} />
        </div>
      ) : null}

      <div className={`composer ${chat.generating ? 'is-disabled' : ''}`}>
        {mention ? (
          <MentionPicker
            entries={mentionEntries}
            activeIndex={mentionIndex}
            loading={mentionLoading}
            onHover={setMentionIndex}
            onPick={pickMention}
          />
        ) : null}
        {chat.attachments.length > 0 ? (
          <div className="composer-attachments">
            {chat.attachments.map((attachment) =>
              attachment.isImage ? (
                <span key={attachment.id} className="composer-thumb" title={attachment.name}>
                  {attachment.url ? (
                    <img src={attachment.url} alt={attachment.name} />
                  ) : (
                    <span className="composer-thumb-placeholder">
                      <ImageIcon size={18} />
                    </span>
                  )}
                  <button
                    type="button"
                    className="remove"
                    aria-label={`移除附件 ${attachment.name}`}
                    onClick={() => void removeAttachment(attachment.id)}
                  >
                    <X size={11} />
                  </button>
                </span>
              ) : (
                <span key={attachment.id} className="composer-file" title={attachment.path}>
                  <span className="composer-file-icon">
                    <FileText size={16} />
                  </span>
                  <span className="composer-file-text">
                    <span className="composer-file-name">{attachment.name}</span>
                    <span className="composer-file-meta">{attachmentMeta(attachment.name, attachment.size)}</span>
                  </span>
                  <button
                    type="button"
                    className="remove"
                    aria-label={`移除附件 ${attachment.name}`}
                    onClick={() => void removeAttachment(attachment.id)}
                  >
                    <X size={11} />
                  </button>
                </span>
              ),
            )}
          </div>
        ) : null}

        <textarea
          ref={textareaRef}
          className="composer-input"
          value={chat.draft}
          autoFocus={hero}
          placeholder={`和${petName}说点什么…`}
          aria-label="输入消息"
          onChange={(event) => {
            const value = event.target.value
            syncDraft(value)
            setMention(findMentionToken(value, event.target.selectionStart ?? value.length))
          }}
          onCompositionStart={() => {
            composing.current = true
          }}
          onCompositionEnd={() => {
            composing.current = false
          }}
          onBlur={() => setMention(null)}
          onKeyDown={(event) => {
            if (mention && mentionEntries.length > 0 && !composing.current) {
              if (event.key === 'ArrowDown') {
                event.preventDefault()
                setMentionIndex((index) => (index + 1) % mentionEntries.length)
                return
              }
              if (event.key === 'ArrowUp') {
                event.preventDefault()
                setMentionIndex((index) => (index - 1 + mentionEntries.length) % mentionEntries.length)
                return
              }
              if (event.key === 'Enter' || event.key === 'Tab') {
                event.preventDefault()
                pickMention(mentionEntries[Math.min(mentionIndex, mentionEntries.length - 1)])
                return
              }
            }
            if (event.key === 'Escape' && mention) {
              event.preventDefault()
              setMention(null)
              return
            }
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
          <ApprovalPicker sessionId={sessionId} mode={chat.approvalMode} />
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