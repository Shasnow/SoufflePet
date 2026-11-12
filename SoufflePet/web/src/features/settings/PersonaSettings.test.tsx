// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { PersonaDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { PersonaSettings } from './PersonaSettings'

const persona: PersonaDto = {
  petName: '小橘',
  systemPrompt: '你是小橘，说话简短。',
  defaultPetName: '舒芙蕾',
  defaultSystemPrompt: '你是「小橘」，一个性格活泼的桌宠。',
}

vi.mock('@/bridge/client', () => ({
  invoke: vi.fn(async (method: string, params?: Record<string, unknown>) => {
    if (method === 'settings.persona.get') return persona
    if (method === 'settings.persona.save') {
      return {
        ...persona,
        petName: String(params?.petName ?? '').trim(),
        systemPrompt: String(params?.systemPrompt ?? '').trim(),
      }
    }
    return null
  }),
}))

import { invoke } from '@/bridge/client'

describe('PersonaSettings', () => {
  beforeEach(() => {
    vi.mocked(invoke).mockClear()
    useAppStore.setState({ pet: { name: '舒芙蕾', initial: '舒' } })
  })

  it('加载后填充称呼与人设提示词', async () => {
    render(<PersonaSettings />)

    await waitFor(() => {
      expect((screen.getByLabelText('称呼') as HTMLInputElement).value).toBe('小橘')
    })
    expect((screen.getByLabelText('系统提示词') as HTMLTextAreaElement).value).toBe('你是小橘，说话简短。')
  })

  it('保存后同步 app store 里的称呼与头像首字', async () => {
    render(<PersonaSettings />)
    await waitFor(() => {
      expect((screen.getByLabelText('称呼') as HTMLInputElement).value).toBe('小橘')
    })

    fireEvent.change(screen.getByLabelText('称呼'), { target: { value: '阿橘' } })
    fireEvent.click(screen.getByRole('button', { name: '保存人设' }))

    await waitFor(() => {
      expect(invoke).toHaveBeenCalledWith('settings.persona.save', {
        petName: '阿橘',
        systemPrompt: '你是小橘，说话简短。',
      })
    })
    await waitFor(() => {
      expect(useAppStore.getState().pet).toEqual({ name: '阿橘', initial: '阿' })
    })
  })

  it('名称为空时拒绝保存', async () => {
    render(<PersonaSettings />)
    await waitFor(() => {
      expect((screen.getByLabelText('称呼') as HTMLInputElement).value).toBe('小橘')
    })

    fireEvent.change(screen.getByLabelText('称呼'), { target: { value: '   ' } })
    fireEvent.click(screen.getByRole('button', { name: '保存人设' }))

    expect(screen.getByRole('alert').textContent).toContain('桌宠名称不能为空')
    expect(vi.mocked(invoke).mock.calls.some(([method]) => method === 'settings.persona.save')).toBe(false)
  })

  it('填入默认人设只改表单，不立即保存', async () => {
    render(<PersonaSettings />)
    await waitFor(() => {
      expect((screen.getByLabelText('系统提示词') as HTMLTextAreaElement).value).toBe('你是小橘，说话简短。')
    })

    fireEvent.click(screen.getByRole('button', { name: /填入默认人设/ }))

    expect((screen.getByLabelText('系统提示词') as HTMLTextAreaElement).value).toBe(persona.defaultSystemPrompt)
    expect(vi.mocked(invoke).mock.calls.some(([method]) => method === 'settings.persona.save')).toBe(false)
  })
})