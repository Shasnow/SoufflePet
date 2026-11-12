// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { RotateCcw } from 'lucide-react'
import { useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { PersonaDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useToastStore } from '@/stores/toastStore'
import { errorMessage, SettingsCard, SettingsError } from './SettingsShared'

export function PersonaSettings() {
  const setPet = useAppStore((state) => state.setPet)
  const pushToast = useToastStore((state) => state.push)
  const [loaded, setLoaded] = useState<PersonaDto | null>(null)
  const [name, setName] = useState('')
  const [prompt, setPrompt] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let alive = true
    invoke<PersonaDto>('settings.persona.get')
      .then((persona) => {
        if (!alive) return
        setLoaded(persona)
        setName(persona.petName)
        setPrompt(persona.systemPrompt)
      })
      .catch((loadError) => {
        if (alive) setError(`读取人设失败：${errorMessage(loadError)}`)
      })
    return () => {
      alive = false
    }
  }, [])

  const save = async () => {
    if (!name.trim()) {
      setError('桌宠名称不能为空')
      return
    }
    setBusy(true)
    setError(null)
    try {
      const persona = await invoke<PersonaDto>('settings.persona.save', { petName: name, systemPrompt: prompt })
      setLoaded(persona)
      setName(persona.petName)
      setPrompt(persona.systemPrompt)
      // 侧栏、输入提示与欢迎语读的是 app store，保存后立即反映新称呼。
      setPet({ name: persona.petName, initial: persona.petName.slice(0, 1) || '舒' })
      pushToast({ level: 'success', text: '人设已保存' })
    } catch (saveError) {
      setError(`保存人设失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="桌宠名称"
        desc="显示在侧栏、对话窗口标题、输入提示与欢迎语中；保存后立即生效。"
      >
        <div className="field">
          <label className="field-label" htmlFor="persona-name">
            称呼
          </label>
          <input
            id="persona-name"
            className="input"
            value={name}
            maxLength={20}
            placeholder={loaded?.defaultPetName ?? '舒芙蕾'}
            onChange={(event) => setName(event.target.value)}
          />
          <div className="field-hint">最多 20 个字符，不能为空。</div>
        </div>
      </SettingsCard>

      <SettingsCard
        title="人设提示词"
        desc="作为系统提示词的第一段注入每轮对话，决定桌宠的性格与说话方式；留空表示不注入人设。"
        actions={
          <button
            type="button"
            className="btn btn-sm btn-ghost"
            disabled={!loaded || busy}
            onClick={() => {
              if (loaded) setPrompt(loaded.defaultSystemPrompt)
            }}
          >
            <RotateCcw size={13} /> 填入默认人设
          </button>
        }
      >
        <div className="field">
          <label className="field-label" htmlFor="persona-prompt">
            系统提示词
          </label>
          <textarea
            id="persona-prompt"
            className="textarea"
            rows={10}
            maxLength={4000}
            value={prompt}
            placeholder="例如：你是住在用户电脑桌面上的桌宠，性格活泼、说话简短……"
            onChange={(event) => setPrompt(event.target.value)}
          />
          <div className="field-hint">
            {prompt.trim().length} / 4000 字 · 留空表示不注入人设；清空后模型将按默认助手风格回复。
          </div>
        </div>

        <div className="settings-actions">
          <button type="button" className="btn btn-primary" disabled={busy || !loaded} onClick={() => void save()}>
            {busy ? '保存中…' : '保存人设'}
          </button>
        </div>
      </SettingsCard>
    </div>
  )
}