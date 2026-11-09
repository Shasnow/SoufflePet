// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { buildRounds, ChatOutline } from './ChatOutline'

function message(id: string, kind: ChatMessageDto['kind'], text: string): ChatMessageDto {
  return { id, kind, text, time: new Date(2026, 10, 9, 12, 0).toISOString() }
}

describe('buildRounds', () => {
  it('按用户提问切分轮次，助手正文并入同一轮', () => {
    const rounds = buildRounds([
      message('u1', 'user', '第一问'),
      message('r1', 'reasoning', '思考中'),
      message('a1', 'assistant', '第一答'),
      message('a2', 'assistant', '补充回答'),
      message('u2', 'user', '第二问'),
      message('a3', 'assistant', '第二答'),
    ])

    expect(rounds).toHaveLength(2)
    expect(rounds[0]).toMatchObject({ index: 0, question: '第一问', answer: '第一答\n\n补充回答' })
    expect(rounds[1]).toMatchObject({ index: 4, question: '第二问', answer: '第二答' })
  })
})

describe('ChatOutline', () => {
  const messages = [
    message('u1', 'user', '第一问'),
    message('a1', 'assistant', '第一答'),
    message('u2', 'user', '第二问'),
    message('a2', 'assistant', '第二答'),
  ]

  it('每轮渲染一个节点，点击后报告对应消息下标', () => {
    const onJump = vi.fn()
    const { container } = render(<ChatOutline messages={messages} onJump={onJump} />)

    const nodes = container.querySelectorAll('.outline-node')
    expect(nodes).toHaveLength(2)

    fireEvent.click(nodes[1])
    expect(onJump).toHaveBeenCalledWith(2)
  })

  it('hover 节点时预览问题与回答', () => {
    const { container } = render(<ChatOutline messages={messages} onJump={() => undefined} />)

    fireEvent.mouseEnter(container.querySelectorAll('.outline-node')[0])
    expect(container.querySelector('.outline-preview-title')?.textContent).toBe('第一问')
    expect(container.querySelector('.outline-preview-body')?.textContent).toBe('第一答')
  })

  it('只有一轮对话时不显示目录', () => {
    const { container } = render(
      <ChatOutline messages={[message('u1', 'user', '只有一个问题')]} onJump={() => undefined} />,
    )
    expect(container.querySelector('.chat-outline')).toBeNull()
  })
})