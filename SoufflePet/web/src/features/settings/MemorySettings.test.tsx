// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { MemoryDto } from '@/bridge/contracts'
import { MemorySettings } from './MemorySettings'

vi.mock('@/bridge/client', () => ({ invoke: vi.fn() }))

import { invoke } from '@/bridge/client'

let memory: MemoryDto

const sortEntries = (entries: MemoryDto['entries']) =>
  [...entries].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))

function apply(mutate: (draft: MemoryDto) => void): MemoryDto {
  const draft: MemoryDto = { ...memory, entries: [...memory.entries] }
  mutate(draft)
  memory = { ...draft, entries: sortEntries(draft.entries) }
  return memory
}

describe('MemorySettings', () => {
  beforeEach(() => {
    memory = {
      enabled: true,
      path: 'C:/Users/u/.souffle/memory.json',
      entries: [
        { key: '用户称呼', value: '老板', updatedAt: '2026-11-01T10:00:00+08:00' },
        { key: '常用语言', value: '中文', updatedAt: '2026-11-05T10:00:00+08:00' },
      ],
    }

    vi.mocked(invoke).mockReset()
    vi.mocked(invoke).mockImplementation(async (method: string, params?: unknown) => {
      const input = (params ?? {}) as {
        key?: string
        value?: string
        previousKey?: string
        enabled?: boolean
      }
      switch (method) {
        case 'settings.memory.get':
          return memory
        case 'settings.memory.setEnabled':
          return apply((draft) => {
            draft.enabled = input.enabled === true
          })
        case 'settings.memory.save':
          return apply((draft) => {
            const key = String(input.key ?? '').trim()
            const previousKey = String(input.previousKey ?? '').trim()
            draft.entries = draft.entries.filter(
              (entry) => entry.key !== key && (!previousKey || entry.key !== previousKey),
            )
            draft.entries.push({ key, value: String(input.value ?? '').trim(), updatedAt: new Date().toISOString() })
          })
        case 'settings.memory.delete':
          return apply((draft) => {
            draft.entries = draft.entries.filter((entry) => entry.key !== input.key)
          })
        case 'settings.memory.clear':
          return apply((draft) => {
            draft.entries = []
          })
        default:
          return null
      }
    })
  })

  it('加载记忆条目与存储位置', async () => {
    render(<MemorySettings />)

    await waitFor(() => {
      expect(screen.getByText('用户称呼')).toBeTruthy()
    })
    expect(screen.getByText('老板')).toBeTruthy()
    expect(screen.getByText('常用语言')).toBeTruthy()
    expect(screen.getByText(/memory\.json/)).toBeTruthy()
    expect((screen.getByLabelText('启用长期记忆') as HTMLInputElement).checked).toBe(true)
  })

  it('新增记忆后清空输入框并刷新列表', async () => {
    render(<MemorySettings />)
    await waitFor(() => {
      expect(screen.getByText('用户称呼')).toBeTruthy()
    })

    fireEvent.change(screen.getByLabelText('记忆键'), { target: { value: ' 生日 ' } })
    fireEvent.change(screen.getByLabelText('记忆内容'), { target: { value: ' 11 月 3 日 ' } })
    fireEvent.click(screen.getByRole('button', { name: /添加/ }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.save', { key: '生日', value: '11 月 3 日' })
    })
    await waitFor(() => {
      expect(screen.getByText('生日')).toBeTruthy()
    })
    expect((screen.getByLabelText('记忆键') as HTMLInputElement).value).toBe('')
  })

  it('编辑条目内容并保存', async () => {
    render(<MemorySettings />)
    await waitFor(() => {
      expect(screen.getByText('用户称呼')).toBeTruthy()
    })

    fireEvent.click(screen.getByRole('button', { name: '编辑记忆 用户称呼' }))
    const editor = screen.getByLabelText('编辑记忆内容 用户称呼')
    fireEvent.change(editor, { target: { value: '老板娘' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.save', { key: '用户称呼', value: '老板娘' })
    })
    await waitFor(() => {
      expect(screen.getByText('老板娘')).toBeTruthy()
    })
    expect(screen.queryByLabelText('编辑记忆内容 用户称呼')).toBeNull()
  })

  it('可以重命名记忆键', async () => {
    render(<MemorySettings />)
    await waitFor(() => {
      expect(screen.getByText('用户称呼')).toBeTruthy()
    })

    fireEvent.click(screen.getByRole('button', { name: '编辑记忆 用户称呼' }))
    fireEvent.change(screen.getByLabelText('编辑记忆键 用户称呼'), { target: { value: '昵称' } })
    fireEvent.click(screen.getByRole('button', { name: '保存' }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.save', {
        key: '昵称',
        value: '老板',
        previousKey: '用户称呼',
      })
    })
    await waitFor(() => {
      expect(screen.getByText('昵称')).toBeTruthy()
    })
    expect(screen.queryByText('用户称呼')).toBeNull()
  })

  it('删除条目', async () => {
    render(<MemorySettings />)
    await waitFor(() => {
      expect(screen.getByText('常用语言')).toBeTruthy()
    })

    fireEvent.click(screen.getByRole('button', { name: '删除记忆 常用语言' }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.delete', { key: '常用语言' })
    })
    await waitFor(() => {
      expect(screen.queryByText('常用语言')).toBeNull()
    })
  })

  it('切换开关与确认清空', async () => {
    render(<MemorySettings />)
    await waitFor(() => {
      expect(screen.getByText('用户称呼')).toBeTruthy()
    })

    fireEvent.click(screen.getByLabelText('启用长期记忆'))
    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.setEnabled', { enabled: false })
    })
    expect((screen.getByLabelText('启用长期记忆') as HTMLInputElement).checked).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: /清空全部/ }))
    fireEvent.click(screen.getByRole('button', { name: '清空' }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.memory.clear')
    })
    await waitFor(() => {
      expect(screen.queryByText('用户称呼')).toBeNull()
    })
  })
})