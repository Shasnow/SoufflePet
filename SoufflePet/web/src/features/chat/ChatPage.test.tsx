// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ChatMessageDto, ChatSnapshotDto } from '@/bridge/contracts'
import { useChatStore } from '@/stores/chatStore'
import { useSessionStore } from '@/stores/sessionStore'

vi.mock('@/bridge/client', () => ({
  invoke: vi.fn(async () => null),
}))

import { invoke } from '@/bridge/client'
import { ChatPage } from './ChatPage'

const snapshot: ChatSnapshotDto = {
  sessionId: 's1',
  messages: [],
  draft: '',
  attachments: [],
  generating: false,
  models: [{ key: 'p/m', provider: 'p', name: 'm', modelId: 'm' }],
  selectedModelKey: 'p/m',
  approvalMode: 'manual',
  workspace: { path: 'D:/w', name: 'w' },
  workspaces: [{ path: 'D:/w', name: 'w' }],
  usage: null,
  ask: null,
}

const USER_MESSAGE: ChatMessageDto = {
  id: 'm1',
  kind: 'user',
  text: '在吗',
  time: '12:00',
}

/** 底部上下文水位需要非空会话（空会话渲染欢迎页），窗口取 mock 宿主的默认值。 */
function snapshotWithMeter(usage: ChatSnapshotDto['contextUsage']): ChatSnapshotDto {
  return { ...snapshot, messages: [USER_MESSAGE], contextUsage: usage }
}

describe('ChatPage 上下文水位', () => {
  const open = (value: ChatSnapshotDto) => {
    vi.mocked(invoke).mockImplementation((async (method: string) => {
      if (method === 'session.open') return value
      return null
    }) as never)
    useSessionStore.setState({ sessions: [], activeId: 's1' })
    useChatStore.setState({ bySession: { s1: { ...value } }, activeId: 's1' })
    return render(<ChatPage sessionId="s1" />)
  }

  it('还没估算过时只显示窗口大小', () => {
    const { container } = open(
      snapshotWithMeter({ estimatedTokens: null, windowTokens: 131072, triggerTokens: 113869 }),
    )

    expect(container.querySelector('.context-usage-text')?.textContent).toBe('— / 131.1K · —')
    expect(container.querySelector('.context-usage')?.className).not.toContain('is-hot')
  })

  it('显示估算占用与百分比', () => {
    const { container } = open(
      snapshotWithMeter({ estimatedTokens: 65536, windowTokens: 131072, triggerTokens: 113869 }),
    )

    expect(container.querySelector('.context-usage-text')?.textContent).toBe('65.5K / 131.1K · 50%')
    expect(container.querySelector('.context-usage')?.className).not.toContain('is-hot')
  })

  it('到达宿主给的触发线时变黄，而不是按固定 90%', () => {
    const hot = open(
      snapshotWithMeter({ estimatedTokens: 113869, windowTokens: 131072, triggerTokens: 113869 }),
    )
    expect(hot.container.querySelector('.context-usage')?.className).toContain('is-hot')

    // 87% 时压缩已触发（触发线 = 90% 窗口 - 4K 预留），界面必须已经变黄
    const near = open(
      snapshotWithMeter({ estimatedTokens: 110000, windowTokens: 131072, triggerTokens: 113869 }),
    )
    expect(near.container.querySelector('.context-usage')?.className).not.toContain('is-hot')
  })

  it('没有模型（宿主返回 null）时不显示水位', () => {
    const { container } = open(snapshotWithMeter(null))
    expect(container.querySelector('.context-usage')).toBeNull()
  })
})

describe('ChatPage 拖拽附件', () => {
  beforeEach(() => {
    vi.mocked(invoke).mockClear()
    useSessionStore.setState({ sessions: [], activeId: 's1' })
    useChatStore.setState({ bySession: { s1: { ...snapshot } }, activeId: 's1' })
    vi.mocked(invoke).mockImplementation((async (method: string) => {
      if (method === 'session.open') return snapshot
      if (method === 'chat.attach.begin') return { uploadId: 'u1' }
      if (method === 'chat.attach.chunk') return { size: 5 }
      if (method === 'chat.attach.finish') {
        return {
          attachments: [{ id: 'a1', name: 'notes.txt', path: 'D:/att/notes.txt', isImage: false }],
          added: 1,
        }
      }
      return null
    }) as never)
  })

  it('拖入文件时显示提示层，松开后分块把字节发给宿主', async () => {
    const { container } = render(<ChatPage sessionId="s1" />)
    const page = container.querySelector('.chat-page') as HTMLElement
    const file = new File(['hello'], 'notes.txt', { type: 'text/plain' })

    fireEvent.dragEnter(page, { dataTransfer: { types: ['Files'] } })
    expect(screen.getByText('松开以添加附件')).toBeTruthy()

    fireEvent.drop(page, { dataTransfer: { types: ['Files'], files: [file] } })

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.attach.begin', {
        sessionId: 's1',
        name: 'notes.txt',
        size: 5,
      })
      expect(invoke).toHaveBeenCalledWith('chat.attach.chunk', { uploadId: 'u1', data: 'aGVsbG8=' })
      expect(invoke).toHaveBeenCalledWith('chat.attach.finish', { sessionId: 's1', uploads: ['u1'] })
    })
    expect(screen.queryByText('松开以添加附件')).toBeNull()
  })

  it('输入 @ 时列出工作区条目，回车插入引用', async () => {
    vi.mocked(invoke).mockImplementation((async (method: string) => {
      if (method === 'session.open') return snapshot
      if (method === 'chat.mention.search') {
        return { entries: [{ name: 'notes.md', path: 'notes.md', isDirectory: false }] }
      }
      return null
    }) as never)

    render(<ChatPage sessionId="s1" />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement
    fireEvent.change(input, { target: { value: '@no', selectionStart: 3 } })

    const option = await screen.findByRole('option')
    expect(option.textContent).toContain('notes.md')

    fireEvent.keyDown(input, { key: 'Enter' })
    await waitFor(() => expect(input.value).toBe('@notes.md '))
  })
})
