// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ChatSnapshotDto } from '@/bridge/contracts'
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
