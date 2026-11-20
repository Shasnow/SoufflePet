// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
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

  it('流式回复的光标跟随 Markdown 内容，不额外占一行', () => {
    const message: ChatMessageDto = {
      id: 'm-assistant',
      kind: 'assistant',
      text: '好的',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
    }

    const { container } = render(<MessageItem sessionId="s1" message={message} streaming />)
    const bubble = container.querySelector('.bubble-assistant')
    expect(bubble?.classList.contains('is-streaming')).toBe(true)
    // 光标由 CSS 伪元素画在最后一个块末尾，不再作为独立块插入
    expect(container.querySelector('.caret')).toBeNull()
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

  it('系统消息可展开查看原始文本', () => {
    const message: ChatMessageDto = {
      id: 'm-notice',
      kind: 'notice',
      text: '⏰ 定时任务「日报」：整理今天的日报',
      rawText: '<scheduled_task name="日报">\n整理今天的日报，并按项目汇总\n</scheduled_task>',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
    }

    const { container } = render(<MessageItem sessionId="s1" message={message} streaming={false} />)
    expect(container.querySelector('.notice-raw')).toBeNull()
    const toggle = container.querySelector('.notice-toggle') as HTMLButtonElement
    expect(toggle.textContent).toContain('查看原文')

    fireEvent.click(toggle)
    expect(container.querySelector('.notice-raw')?.textContent).toContain('按项目汇总')
    expect(toggle.textContent).toContain('收起原文')

    fireEvent.click(toggle)
    expect(container.querySelector('.notice-raw')).toBeNull()
  })

  it('原始文本与摘要相同时不显示展开入口', () => {
    const message: ChatMessageDto = {
      id: 'm-notice-plain',
      kind: 'notice',
      text: '同一条内容',
      rawText: '同一条内容',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
    }

    const { container } = render(<MessageItem sessionId="s1" message={message} streaming={false} />)
    expect(container.querySelector('.notice-toggle')).toBeNull()
  })
})
