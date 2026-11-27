// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
import { invoke } from '@/bridge/client'
import type { ChatMessageDto } from '@/bridge/contracts'
import { useSlashPaletteStore } from '@/stores/slashPaletteStore'
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

  it('上下文摘要气泡默认展开、按 Markdown 渲染，点击可折叠', () => {
    const message: ChatMessageDto = {
      id: 'm-summary',
      kind: 'summary',
      text: '## 已折叠的对话\n\n- 窗口取默认 128K\n- **保留预算**取窗口的一半',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
      expanded: true,
    }

    const { container } = render(<MessageItem sessionId="s1" message={message} streaming={false} />)
    const bubble = container.querySelector('.bubble-summary')
    expect(bubble?.querySelector('.collapse-title')?.textContent).toBe('上下文摘要')
    // 正文走 Markdown 渲染，不是原样文本
    expect(bubble?.querySelector('h2')?.textContent).toBe('已折叠的对话')
    expect(bubble?.querySelector('strong')?.textContent).toBe('保留预算')
    // 摘要不是对话正文：不显示时间/复制/用量那一行
    expect(container.querySelector('.bubble-meta')).toBeNull()

    fireEvent.click(bubble!.querySelector('.collapse-head') as HTMLButtonElement)
    expect(vi.mocked(invoke).mock.calls.at(-1)).toEqual([
      'chat.msg.expand',
      { sessionId: 's1', messageId: 'm-summary', expanded: false },
    ])
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

  it('消息操作菜单可直接唤起命令，无需输入框', () => {
    useSlashPaletteStore.setState({ request: null })
    const { container } = render(<MessageItem sessionId="s7" message={userMessage} streaming={false} />)

    const button = container.querySelector('[aria-label="命令"]') as HTMLButtonElement
    expect(button).not.toBeNull()
    fireEvent.click(button)
    expect(useSlashPaletteStore.getState().request?.sessionId).toBe('s7')
  })

  it('平台消息等通知气泡也带命令入口', () => {
    useSlashPaletteStore.setState({ request: null })
    const message: ChatMessageDto = {
      id: 'm-platform',
      kind: 'notice',
      text: '[平台消息] qq · 小明：在吗',
      time: new Date(2026, 10, 5, 19, 54).toISOString(),
    }

    const { container } = render(<MessageItem sessionId="s8" message={message} streaming={false} />)
    const button = container.querySelector('.bubble-notice [aria-label="命令"]') as HTMLButtonElement

    expect(button).not.toBeNull()
    fireEvent.click(button)
    expect(useSlashPaletteStore.getState().request?.sessionId).toBe('s8')
  })
})
