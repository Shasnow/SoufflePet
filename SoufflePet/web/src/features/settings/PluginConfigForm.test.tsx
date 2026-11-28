// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ConfigFieldDto } from '@/bridge/contracts'
import { PluginConfigForm } from './PluginConfigForm'

const { invokeMock } = vi.hoisted(() => ({ invokeMock: vi.fn() }))
vi.mock('@/bridge/client', () => ({ invoke: invokeMock }))

const fields: ConfigFieldDto[] = [
  { key: 'ratio', label: '采样比例', kind: 'number', value: 0.5 },
  { key: 'name', label: '名称', kind: 'text', value: 'demo' },
]

describe('PluginConfigForm', () => {
  beforeEach(() => {
    invokeMock.mockReset()
    invokeMock.mockImplementation(async (method: string) => {
      if (method === 'settings.plugins.getConfigSchema') {
        return { fields, config: { ratio: 0.5, name: 'demo' } }
      }
      return null
    })
  })

  it('数字框编辑期间保留原始文本，0.01 能正常输入并保存', async () => {
    render(<PluginConfigForm pluginId="p1" onSaved={() => undefined} />)
    const input = (await screen.findByLabelText('采样比例')) as HTMLInputElement
    expect(input.value).toBe('0.5')

    fireEvent.change(input, { target: { value: '0.' } })
    expect(input.value).toBe('0.')

    fireEvent.change(input, { target: { value: '0.01' } })
    expect(input.value).toBe('0.01')

    fireEvent.click(screen.getByRole('button', { name: '保存配置' }))
    await waitFor(() => {
      expect(invokeMock).toHaveBeenCalledWith('settings.plugins.saveConfig', {
        id: 'p1',
        config: { ratio: 0.01, name: 'demo' },
      })
    })
  })

  it('数字框填了非数字时提示错误且不保存', async () => {
    render(<PluginConfigForm pluginId="p1" onSaved={() => undefined} />)
    const input = (await screen.findByLabelText('采样比例')) as HTMLInputElement

    fireEvent.change(input, { target: { value: 'abc' } })
    fireEvent.click(screen.getByRole('button', { name: '保存配置' }))

    expect(await screen.findByText(/需要填写有效数字/)).toBeInTheDocument()
    expect(invokeMock).not.toHaveBeenCalledWith('settings.plugins.saveConfig', expect.anything())
  })
})
