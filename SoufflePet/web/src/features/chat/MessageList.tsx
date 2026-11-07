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
  const contentRef = useRef<HTMLDivElement>(null)
  const atBottomRef = useRef(true)
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

  const updateAtBottom = (value: boolean) => {
    atBottomRef.current = value
    setAtBottom(value)
  }

  // 直接贴到底部，而不是 scrollToIndex：虚拟列表的高度是异步测量的，
  // 流式输出较快时按索引滚动会停在旧高度，触发 onScroll 把 atBottom 判成 false 而暂停跟随。
  const scrollToBottom = (behavior: ScrollBehavior = 'auto') => {
    const element = parentRef.current
    if (!element) return
    element.scrollTo({ top: element.scrollHeight, behavior })
  }

  const pinToBottom = () => {
    if (!atBottomRef.current) return
    const element = parentRef.current
    if (element) element.scrollTop = element.scrollHeight
  }

  useLayoutEffect(() => {
    // 打开会话时贴底；会话切换由父组件 key 强制重新挂载。
    const frame = window.requestAnimationFrame(() => pinToBottom())
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!atBottomRef.current) return
    const frame = window.requestAnimationFrame(() => pinToBottom())
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [messages.length, lastLength])

  // 虚拟列表测量完成后总高度还会继续增长；贴底时持续跟住，避免流式内容变高后被落下。
  useLayoutEffect(() => {
    const content = contentRef.current
    if (!content || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(() => pinToBottom())
    observer.observe(content)
    return () => observer.disconnect()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <div className="message-list-wrap">
      <div
        className="message-list"
        ref={parentRef}
        onScroll={(event) => {
          const element = event.currentTarget
          const distance = element.scrollHeight - element.scrollTop - element.clientHeight
          updateAtBottom(distance < 24)
        }}
      >
        <div className="message-list-inner">
          <div
            ref={contentRef}
            style={{ height: virtualizer.getTotalSize(), position: 'relative' }}
          >
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
      </div>
      {!atBottom ? (
        <button
          type="button"
          className="scroll-jump"
          onClick={() => {
            updateAtBottom(true)
            scrollToBottom('smooth')
          }}
        >
          <ArrowDown size={13} />
          回到最新
        </button>
      ) : null}
    </div>
  )
}