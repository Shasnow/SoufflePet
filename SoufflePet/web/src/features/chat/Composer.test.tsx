// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi, beforeEach } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ChatSnapshotDto } from '@/bridge/contracts'
import { useChatStore } from '@/stores/chatStore'
import { Composer } from './Composer'

vi.mock('@/bridge/client', () => ({
  invoke: vi.fn(async () => null),
}))

import { invoke } from '@/bridge/client'

const snapshot: ChatSnapshotDto = {
  sessionId: 's1',
  messages: [],
  draft: '',
  attachments: [],
  generating: false,
  models: [
    { key: 'p/m', provider: 'p', name: 'm', modelId: 'm' },
  ],
  selectedModelKey: 'p/m',
  approvalMode: 'manual',
  workspace: { path: 'D:/w', name: 'w' },
  workspaces: [{ path: 'D:/w', name: 'w' }],
  usage: null,
  ask: null,
}

function Harness() {
  const chat = useChatStore((state) => state.bySession[state.activeId ?? '']) ?? snapshot
  return <Composer chat={chat} hero />
}

describe('Composer', () => {
  beforeEach(() => {
    vi.mocked(invoke).mockClear()
    useChatStore.setState({ bySession: { s1: { ...snapshot, draft: '' } }, activeId: 's1' })
  })

  it('sends on Enter and not on Shift+Enter', async () => {
    render(<Harness />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: 'hello' } })
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter', shiftKey: true })
    expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())

    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter', shiftKey: false })
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.send', {
        text: 'hello',
        attachmentIds: [],
      })
    })
  })

  it('does not send while IME composition is active', async () => {
    render(<Harness />)
    const input = screen.getByLabelText('输入消息') as HTMLTextAreaElement

    fireEvent.change(input, { target: { value: 'ni hao' } })
    fireEvent.compositionStart(input)
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
    expect(invoke).not.toHaveBeenCalledWith('chat.send', expect.anything())

    fireEvent.compositionEnd(input)
    fireEvent.keyDown(input, { key: 'Enter', code: 'Enter' })
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('chat.send', expect.objectContaining({ text: 'ni hao' }))
    })
  })

  it('switches the send button to stop while generating', () => {
    useChatStore.setState({
      bySession: { s1: { ...snapshot, generating: true, draft: 'draft' } },
      activeId: 's1',
    })
    render(<Harness />)
    expect(screen.getByLabelText('停止生成')).toBeInTheDocument()
  })

  it('已绑定工作区的会话隐藏工作区切换入口', () => {
    render(<Harness />)
    expect(screen.queryByLabelText('工作区')).toBeNull()
    expect(document.querySelector('.composer-context')).toBeNull()
  })

  it('未绑定工作区的会话仍可手动切换工作区', () => {
    useChatStore.setState({
      bySession: { s1: { ...snapshot, workspace: null, workspaces: [] } },
      activeId: 's1',
    })
    render(<Harness />)
    const chip = screen.getByLabelText('工作区')
    expect(chip.textContent).toContain('选择工作区')
  })

  it('把审批放在附件右侧、模型放在发送左侧，且都在输入框内', () => {
    render(<Harness />)
    const bar = document.querySelector('.composer-bar')
    expect(bar).not.toBeNull()
    const labels = [...bar!.children].map(
      (node) => node.getAttribute('aria-label') ?? node.className,
    )
    expect(labels.slice(0, 2)).toEqual(['添加附件', '审批模式'])
    expect(labels.slice(-2)).toEqual(['选择对话模型', '发送'])
    expect(document.querySelector('.composer-context [aria-label="审批模式"]')).toBeNull()
  })
})