// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useVirtualizer } from '@tanstack/react-virtual'
import { ArrowDown } from 'lucide-react'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { ChatOutline } from './ChatOutline'
import { MessageItem } from './MessageItem'

interface MessageListProps {
  sessionId: string
  messages: ChatMessageDto[]
  generating: boolean
}

export function MessageList({ sessionId, messages, generating }: MessageListProps) {
  const parentRef = useRef<HTMLDivElement>(null)
  const contentRef = useRef<HTMLDivElement>(null)
  // 是否跟随最新消息。用 ref 而非 state，避免测量引起的滚动事件把跟随状态误判掉。
  const followRef = useRef(true)
  // 最近一次程序化贴底的时间，用于忽略由布局变化触发的瞬时滚动事件。
  const lastPinRef = useRef(0)
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

  const setFollow = (next: boolean) => {
    followRef.current = next
    setAtBottom(next)
  }

  const pinToBottom = () => {
    const element = parentRef.current
    if (!element) return
    lastPinRef.current = performance.now()
    element.scrollTop = element.scrollHeight
  }

  const scrollToBottom = (behavior: ScrollBehavior = 'auto') => {
    const element = parentRef.current
    if (!element || messages.length === 0) return
    setFollow(true)
    if (behavior === 'smooth') {
      element.scrollTo({ top: element.scrollHeight, behavior })
      return
    }
    pinToBottom()
  }

  const jumpToMessage = (index: number) => {
    setFollow(false)
    virtualizer.scrollToIndex(index, { align: 'start', behavior: 'smooth' })
  }

  useLayoutEffect(() => {
    // 打开会话时先跳到末项（虚拟列表按需渲染），再统一交给 ResizeObserver 贴底。
    // 会话切换由父组件 key 强制重新挂载。
    if (messages.length > 0) {
      virtualizer.scrollToIndex(messages.length - 1, { align: 'end' })
    }
    const frame = window.requestAnimationFrame(() => pinToBottom())
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useLayoutEffect(() => {
    // 消息增删或流式文本增长时贴底。虚拟列表的高度是异步测量出来的，
    // 一次新增多个气泡时估算高度与实际不符，直接按内容高度贴底更可靠。
    if (!followRef.current) return
    pinToBottom()
    const frame = window.requestAnimationFrame(() => {
      if (followRef.current) pinToBottom()
    })
    return () => window.cancelAnimationFrame(frame)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [messages.length, lastLength])

  useEffect(() => {
    // 内容高度随测量/图片加载等变化时继续贴底，保证视口始终停在最新内容处。
    const element = parentRef.current
    const content = contentRef.current
    if (!element) return
    const observer = new ResizeObserver(() => {
      if (followRef.current) pinToBottom()
    })
    observer.observe(element)
    if (content) observer.observe(content)
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
          if (distance < 24) {
            if (!followRef.current) setFollow(true)
            else setAtBottom(true)
            return
          }
          // 刚贴过底时的滚动事件多由高度测量引起，不视为用户主动上滚。
          if (performance.now() - lastPinRef.current < 200) return
          if (followRef.current) setFollow(false)
        }}
        onWheel={(event) => {
          // 用户主动上滚时立即停止跟随，避免流式输出时被反复拉回底部。
          const element = event.currentTarget
          if (event.deltaY < 0 && followRef.current && element.scrollHeight > element.clientHeight) {
            setFollow(false)
          }
        }}
      >
        <div className="message-list-inner" ref={contentRef}>
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
      </div>
      <ChatOutline messages={messages} onJump={jumpToMessage} />
      {!atBottom ? (
        <button type="button" className="scroll-jump" onClick={() => scrollToBottom('smooth')}>
          <ArrowDown size={13} />
          回到最新
        </button>
      ) : null}
    </div>
  )
}