// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi } from 'vitest'
import { render } from '@testing-library/react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { MessageItem } from './MessageItem'

vi.mock('@/bridge/client', () => ({ invoke: vi.fn(async () => null) }))

const userMessage: ChatMessageDto = {
  id: 'm-user',
  kind: 'user',
  text: '你好',
  time: new Date(2026, 10, 5, 19, 54).toISOString(),
  usage: { inputTokens: 10, outputTokens: 2, cachedInputTokens: 8, model: 'p/m' },
}

describe('MessageItem', () => {
  it('把时间、复制按钮、用量放在气泡外', () => {
    const { container } = render(<MessageItem sessionId="s1" message={userMessage} streaming={false} />)
    const row = container.querySelector('.message-row')
    const bubble = row?.querySelector('.bubble')
    const meta = row?.querySelector('.bubble-meta')

    expect(row).not.toBeNull()
    expect(bubble).not.toBeNull()
    expect(meta).not.toBeNull()
    expect(bubble!.contains(meta!)).toBe(false)
    expect(row!.contains(meta!)).toBe(true)
    expect(meta!.textContent).toContain('19:54')
    expect(meta!.querySelector('[aria-label="复制消息"]')).not.toBeNull()
    expect(meta!.querySelector('.usage-capsule')).not.toBeNull()
  })

  it('工具气泡显示自定义标题', () => {
    const message: ChatMessageDto = {
      id: 'm-tool',
      kind: 'tool',
      text: '调用工具 run_shell_command',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
      tool: {
        name: 'run_shell_command',
        args: '{"command":"npm test"}',
        result: 'ok',
        label: '运行命令 · npm test',
      },
    }

    const { container } = render(<MessageItem sessionId="s1" message={message} streaming={false} />)
    const title = container.querySelector('.bubble-tool .collapse-title')
    expect(title?.textContent).toBe('运行命令 · npm test')
    expect(title?.getAttribute('title')).toBe('run_shell_command')
  })
})
