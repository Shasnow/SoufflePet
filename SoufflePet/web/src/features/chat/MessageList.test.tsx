// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { afterEach, describe, expect, it, vi } from 'vitest'
import { render, fireEvent } from '@testing-library/react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { MessageList } from './MessageList'

vi.mock('@/bridge/client', () => ({ invoke: vi.fn(async () => null) }))

// 虚拟列表依赖真实布局测量，测试里只保留渲染结构，贴底逻辑由内容高度决定。
vi.mock('@tanstack/react-virtual', () => ({
  useVirtualizer: ({ count }: { count: number }) => ({
    getTotalSize: () => count * 100,
    getVirtualItems: () =>
      Array.from({ length: count }, (_, index) => ({ index, start: index * 100, key: index })),
    measureElement: () => undefined,
    scrollToIndex: () => undefined,
  }),
}))

const time = new Date(2026, 10, 5, 19, 54).toISOString()

const makeMessage = (id: string, text: string): ChatMessageDto => ({
  id,
  kind: 'user',
  text,
  time,
})

describe('MessageList', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('一次新增多个气泡后仍贴住底部', () => {
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockReturnValue(1200)
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(600)

    const { container, rerender } = render(
      <MessageList sessionId="s1" messages={[makeMessage('a', '你好')]} generating={false} />,
    )
    const list = container.querySelector('.message-list') as HTMLElement

    // 模拟虚拟列表异步测量导致的位置漂移
    list.scrollTop = 0
    rerender(
      <MessageList
        sessionId="s1"
        messages={[makeMessage('a', '你好'), makeMessage('b', '在吗'), makeMessage('c', '看看这个')]}
        generating={false}
      />,
    )

    expect(list.scrollTop).toBe(1200)
  })

  it('用户上滚后停止跟随并显示回到最新按钮', () => {
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockReturnValue(1200)
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(600)

    const { container } = render(
      <MessageList sessionId="s1" messages={[makeMessage('a', '你好')]} generating={false} />,
    )
    const list = container.querySelector('.message-list') as HTMLElement

    fireEvent.wheel(list, { deltaY: -120 })
    expect(container.querySelector('.scroll-jump')).not.toBeNull()
  })
})
