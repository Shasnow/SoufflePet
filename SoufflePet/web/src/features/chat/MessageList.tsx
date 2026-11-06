// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useVirtualizer } from '@tanstack/react-virtual'
import { ArrowDown } from 'lucide-react'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { MessageItem } from './MessageItem'

interface MessageListProps {
  sessionId: string
  messages: ChatMessageDto[]
  generating: boolean
}

export function MessageList({ sessionId, messages, generating }: MessageListProps) {
  const parentRef = useRef<HTMLDivElement>(null)
  const [atBottom, setAtBottom] = useState(true)
  const lastMessage = messages[messages.length - 1]
  const lastLength = lastMessage?.text.length ?? 0

  const virtualizer = useVirtualizer({
    count: messages.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 104,
    overscan: 8,
    getItemKey: (index) => messages[index]?.id ?? index,
  })

  const scrollToBottom = (behavior: ScrollBehavior = 'auto') => {
    if (messages.length === 0) return
    virtualizer.scrollToIndex(messages.length - 1, { align: 'end', behavior })
  }

  useLayoutEffect(() => {
    // 打开会话时贴底；会话切换由父组件 key 强制重新挂载。
    const frame = window.requestAnimationFrame(() => scrollToBottom())
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!atBottom) return
    const frame = window.requestAnimationFrame(() => scrollToBottom())
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [messages.length, lastLength, atBottom])

  return (
    <div
      className="message-list"
      ref={parentRef}
      onScroll={(event) => {
        const element = event.currentTarget
        const distance = element.scrollHeight - element.scrollTop - element.clientHeight
        setAtBottom(distance < 24)
      }}
    >
      <div className="message-list-inner">
        <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
          {virtualizer.getVirtualItems().map((item) => {
            const message = messages[item.index]
            if (!message) return null
            return (
              <div
                key={message.id}
                data-index={item.index}
                ref={virtualizer.measureElement}
                style={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  width: '100%',
                  transform: `translateY(${item.start}px)`,
                }}
              >
                <MessageItem
                  sessionId={sessionId}
                  message={message}
                  streaming={generating && item.index === messages.length - 1 && message.kind === 'assistant'}
                />
              </div>
            )
          })}
        </div>
      </div>
      {!atBottom ? (
        <button type="button" className="scroll-jump" onClick={() => scrollToBottom('smooth')}>
          <ArrowDown size={13} />
          回到最新
        </button>
      ) : null}
    </div>
  )
}