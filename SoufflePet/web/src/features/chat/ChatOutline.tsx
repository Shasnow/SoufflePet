// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useMemo, useRef, useState } from 'react'
import type { ChatMessageDto } from '@/bridge/contracts'

export interface OutlineRound {
  /** 该轮第一条消息在 messages 中的下标，用于虚拟列表跳转。 */
  index: number
  question: string
  answer: string
}

/** 把消息流按用户提问切成「轮」：每条用户消息开启一轮，其后的助手正文并入该轮。 */
export function buildRounds(messages: ChatMessageDto[]): OutlineRound[] {
  const rounds: OutlineRound[] = []
  for (let index = 0; index < messages.length; index++) {
    const message = messages[index]
    if (message.kind === 'user') {
      rounds.push({ index, question: message.text, answer: '' })
      continue
    }
    if (message.kind !== 'assistant' || !message.text) continue
    const current = rounds[rounds.length - 1]
    if (!current) {
      // 定时任务等场景可能没有用户消息，首个助手正文单独成轮。
      rounds.push({ index, question: '', answer: message.text })
    } else {
      current.answer = current.answer ? `${current.answer}\n\n${message.text}` : message.text
    }
  }
  return rounds
}

/** 预览用的纯文本：去掉代码块、公式、链接语法与 Markdown 标记，压缩空白并截断。 */
function plainPreview(text: string, max: number): string {
  const single = text
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/\$\$[\s\S]*?\$\$/g, ' ')
    .replace(/\$[^$\n]+\$/g, ' ')
    .replace(/`([^`]*)`/g, '$1')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, ' ')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[#>*_~|-]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
  if (!single) return ''
  return single.length > max ? `${single.slice(0, max)}…` : single
}

interface ChatOutlineProps {
  messages: ChatMessageDto[]
  onJump: (messageIndex: number) => void
}

export function ChatOutline({ messages, onJump }: ChatOutlineProps) {
  const rounds = useMemo(() => buildRounds(messages), [messages])
  const wrapRef = useRef<HTMLDivElement>(null)
  const [hovered, setHovered] = useState<{ key: number; top: number } | null>(null)

  if (rounds.length < 2) return null

  const hover = (node: HTMLButtonElement, messageIndex: number) => {
    const wrap = wrapRef.current
    if (!wrap) return
    const rect = node.getBoundingClientRect()
    const wrapRect = wrap.getBoundingClientRect()
    const center = rect.top - wrapRect.top + rect.height / 2
    // 预览卡垂直居中于节点，并夹在可视区域内，避免贴边溢出。
    const top = Math.min(Math.max(center, 76), Math.max(76, wrapRect.height - 76))
    setHovered({ key: messageIndex, top })
  }

  const round = rounds.find((item) => item.index === hovered?.key) ?? null
  const title = round ? plainPreview(round.question, 60) || plainPreview(round.answer, 60) : ''
  const body = round?.question ? plainPreview(round.answer, 180) : ''

  return (
    <div className="chat-outline-wrap" ref={wrapRef}>
      <div className="chat-outline" role="navigation" aria-label="会话目录">
        {rounds.map((item) => {
          const label = plainPreview(item.question, 40) || plainPreview(item.answer, 40) || '对话'
          return (
            <button
              key={item.index}
              type="button"
              className="outline-node"
              aria-label={label}
              title={label}
              onMouseEnter={(event) => hover(event.currentTarget, item.index)}
              onFocus={(event) => hover(event.currentTarget, item.index)}
              onMouseLeave={() => setHovered(null)}
              onBlur={() => setHovered(null)}
              onClick={() => onJump(item.index)}
            >
              <span className="outline-tick" />
            </button>
          )
        })}
      </div>
      {round ? (
        <div className="outline-preview" style={{ top: hovered?.top }}>
          <div className="outline-preview-title">{title}</div>
          {body ? <div className="outline-preview-body">{body}</div> : null}
        </div>
      ) : null}
    </div>
  )
}