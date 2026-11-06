// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import {
  Check,
  ChevronDown,
  Copy,
  FileText,
  Info,
  ShieldAlert,
  Wrench,
} from 'lucide-react'
import { memo, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatMessageDto } from '@/bridge/contracts'
import { DiffCard } from '@/features/chat/DiffCard'
import { RenderMarkdown } from '@/ui/Markdown'
import { UsageCapsule } from '@/ui/Usage'
import { firstLine, formatClock, latestLine } from '@/ui/format'

interface MessageItemProps {
  message: ChatMessageDto
  streaming: boolean
}

function reasoningHeader(message: ChatMessageDto): string {
  const line = message.reasoningDone ? firstLine(message.text) : latestLine(message.text)
  if (line) return `思考 · ${line}`
  return message.reasoningDone ? '思考' : '思考中…'
}

function MessageItemInner({ message, streaming }: MessageItemProps) {
  const [copied, setCopied] = useState(false)
  const [deciding, setDeciding] = useState(false)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(message.text)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 1200)
    } catch {
      // 剪贴板不可用
    }
  }

  const toggleExpand = () => {
    void invoke('chat.msg.expand', { messageId: message.id, expanded: !message.expanded }).catch(
      () => undefined,
    )
  }

  const decide = async (approve: boolean) => {
    if (deciding) return
    setDeciding(true)
    try {
      await invoke('chat.approval.decide', { messageId: message.id, approve })
    } catch (error) {
      console.warn('approval failed', error)
    } finally {
      setDeciding(false)
    }
  }

  const meta = (
    <div className="bubble-meta">
      <span className="bubble-time">{formatClock(message.time)}</span>
      {message.usage ? <UsageCapsule usage={message.usage} /> : null}
      <div className="bubble-actions">
        <button type="button" className="icon-btn" aria-label="复制消息" title="复制" onClick={() => void copy()}>
          {copied ? <Check size={12} /> : <Copy size={12} />}
        </button>
      </div>
    </div>
  )

  switch (message.kind) {
    case 'user':
      return (
        <div className="message-row" data-kind="user">
          <div className="bubble bubble-user">
            <div style={{ whiteSpace: 'pre-wrap' }}>{message.text || '（空消息）'}</div>
          </div>
          {meta}
        </div>
      )

    case 'assistant':
      return (
        <div className="message-row" data-kind="assistant">
          <div className="bubble bubble-assistant">
            {message.text ? <RenderMarkdown text={message.text} /> : null}
            {!message.text && streaming ? (
              <span className="streaming-hint">
                <span className="dot-pulse">
                  <span />
                  <span />
                  <span />
                </span>
                正在思考…
              </span>
            ) : null}
            {streaming && message.text ? <span className="caret" /> : null}
          </div>
          {meta}
        </div>
      )

    case 'reasoning':
      return (
        <div className="message-row" data-kind="reasoning">
          <div className="bubble bubble-reasoning">
            <button type="button" className="collapse-head" onClick={toggleExpand}>
              <ChevronDown
                size={13}
                style={{ transform: message.expanded ? 'rotate(0deg)' : 'rotate(-90deg)', transition: 'transform 120ms ease' }}
              />
              <span className="collapse-title">{reasoningHeader(message)}</span>
            </button>
            {message.expanded ? <div className="collapse-body">{message.text}</div> : null}
          </div>
          {meta}
        </div>
      )

    case 'tool': {
      const diffs = message.tool?.diffs ?? []
      return (
        <div className="message-row" data-kind="tool">
          <div className="bubble bubble-tool">
            <button type="button" className="collapse-head" onClick={toggleExpand}>
              <ChevronDown
                size={13}
                style={{ transform: message.expanded ? 'rotate(0deg)' : 'rotate(-90deg)', transition: 'transform 120ms ease' }}
              />
              <Wrench size={13} />
              <span className="collapse-title" title={message.tool?.name}>
                {message.tool?.label ??
                  (message.tool?.name ? `调用工具：${message.tool.name}` : '调用工具')}
              </span>
            </button>
            {diffs.length > 0 ? (
              <div className="diff-list">
                {diffs.map((diff, index) => (
                  <DiffCard key={`${diff.path}-${index}`} diff={diff} />
                ))}
              </div>
            ) : null}
            {message.expanded ? (
              <div className="collapse-body">
                {message.tool?.args && diffs.length === 0 ? (
                  <div className="tool-section">
                    <div className="tool-label">参数</div>
                    <pre className="tool-code">{message.tool.args}</pre>
                  </div>
                ) : null}
                <div className="tool-section">
                  <div className="tool-label">结果</div>
                  <pre className="tool-code">{message.tool?.result || message.text || '（无输出）'}</pre>
                </div>
              </div>
            ) : null}
          </div>
          {meta}
        </div>
      )
    }

    case 'notice':
      return (
        <div className="message-row" data-kind="notice">
          <div className="bubble bubble-notice">
            <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
              <Info size={14} style={{ marginTop: 3, flex: 'none', color: 'var(--text-3)' }} />
              <div style={{ whiteSpace: 'pre-wrap' }}>{message.text}</div>
            </div>
          </div>
          {meta}
        </div>
      )

    case 'approval':
      return (
        <div className="message-row" data-kind="approval">
          <div className="bubble bubble-approval">
            <div style={{ display: 'flex', gap: 8, alignItems: 'flex-start' }}>
              <ShieldAlert size={15} style={{ marginTop: 2, flex: 'none', color: 'var(--warning)' }} />
              <div style={{ minWidth: 0 }}>
                <div style={{ whiteSpace: 'pre-wrap' }}>{message.text}</div>
                {message.approval?.detail ? (
                  <pre className="approval-detail">{message.approval.detail}</pre>
                ) : null}
                {message.approval?.pending ? (
                  <div className="approval-actions">
                    <button
                      type="button"
                      className="btn btn-primary btn-sm"
                      disabled={deciding}
                      onClick={() => void decide(true)}
                    >
                      允许执行
                    </button>
                    <button
                      type="button"
                      className="btn btn-sm btn-danger"
                      disabled={deciding}
                      onClick={() => void decide(false)}
                    >
                      拒绝
                    </button>
                  </div>
                ) : message.approval?.outcome ? (
                  <div className="approval-outcome">
                    {message.approval.outcome === 'approved'
                      ? '已允许执行'
                      : message.approval.outcome === 'rejected'
                        ? '已拒绝执行'
                        : '已取消'}
                  </div>
                ) : null}
              </div>
            </div>
          </div>
          {meta}
        </div>
      )

    case 'attachment':
      return (
        <div className="message-row" data-kind="attachment">
          <div className="bubble bubble-attachment">
            <button
              type="button"
              className="attachment-card"
              style={{ border: 0, background: 'transparent', padding: 0, textAlign: 'left' }}
              onClick={() => {
                if (message.attachment?.path) {
                  void invoke('chat.file.open', { path: message.attachment.path }).catch(() => undefined)
                }
              }}
              title={message.attachment?.path}
            >
              {message.attachment?.isImage && message.attachment.url ? (
                <img className="attachment-thumb" src={message.attachment.url} alt={message.attachment.name} />
              ) : (
                <span className="attachment-icon">
                  <FileText size={18} />
                </span>
              )}
              <span style={{ display: 'block', minWidth: 0 }}>
                <span className="attachment-name">{message.attachment?.name ?? '附件'}</span>
                <span className="attachment-path">{message.attachment?.path ?? ''}</span>
              </span>
            </button>
          </div>
          {meta}
        </div>
      )

    default:
      return null
  }
}

export const MessageItem = memo(MessageItemInner)