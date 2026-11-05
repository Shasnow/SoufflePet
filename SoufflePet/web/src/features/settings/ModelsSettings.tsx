// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { CheckCircle2, FileArchive, FolderOpen, RefreshCw, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { Live2DModelDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError } from './SettingsShared'

interface ModelsListPayload {
  models: Live2DModelDto[]
  activeIndex: number
}

export function ModelsSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<ModelsListPayload | null>(null)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await invoke<ModelsListPayload>('settings.models.list'))
      setError(null)
    } catch (loadError) {
      setError(`读取 Live2D 模型失败：${errorMessage(loadError)}`)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const importModel = async (folder: boolean) => {
    setBusy(true)
    try {
      const result = await invoke<{ models: Live2DModelDto[]; message: string }>(
        folder ? 'settings.models.importFolder' : 'settings.models.importArchive',
      )
      pushToast({ level: 'success', text: result.message })
      await load()
    } catch (importError) {
      setError(`导入失败：${errorMessage(importError)}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (removing === null) return
    setBusy(true)
    try {
      const result = await invoke<ModelsListPayload>('settings.models.remove', { index: removing })
      setData(result)
      setRemoving(null)
      pushToast({ level: 'success', text: '已移除模型' })
    } catch (removeError) {
      setError(`移除失败：${errorMessage(removeError)}`)
    } finally {
      setBusy(false)
    }
  }

  const setActive = async (index: number) => {
    try {
      await invoke('settings.models.setActive', { index })
      await load()
      pushToast({ level: 'success', text: '已切换桌宠模型' })
    } catch (activeError) {
      setError(`切换失败：${errorMessage(activeError)}`)
    }
  }

  const validate = async (index: number) => {
    try {
      const result = await invoke<{ ok: boolean; message: string }>('settings.models.validate', { index })
      pushToast({ level: result.ok ? 'success' : 'error', text: result.message })
    } catch (validateError) {
      setError(`校验失败：${errorMessage(validateError)}`)
    }
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="Live2D 模型"
        desc="模型目录在应用目录的 wwwroot/models 下；切换后桌宠会立即重载。"
        actions={
          <>
            <button type="button" className="btn btn-sm btn-ghost" disabled={busy} onClick={() => void importModel(true)}>
              <FolderOpen size={13} /> 导入目录
            </button>
            <button type="button" className="btn btn-sm btn-primary" disabled={busy} onClick={() => void importModel(false)}>
              <FileArchive size={13} /> 导入压缩包
            </button>
            <button type="button" className="icon-btn" aria-label="刷新模型列表" onClick={() => void load()}>
              <RefreshCw size={14} />
            </button>
          </>
        }
      >
        {!data ? (
          <SettingsEmpty>正在读取模型…</SettingsEmpty>
        ) : data.models.length === 0 ? (
          <SettingsEmpty>还没有模型；导入一个含 model3.json 的目录或压缩包。</SettingsEmpty>
        ) : (
          <div className="settings-model-cards">
            {data.models.map((model, index) => (
              <div className="settings-model-card" data-active={(index === data.activeIndex).toString()} key={`${model.name}-${index}`}>
                <div className="settings-model-head">
                  <span className="settings-model-name-big">{model.name}</span>
                  {index === data.activeIndex ? (
                    <span className="settings-badge" data-state="enabled">
                      <CheckCircle2 size={11} /> 当前模型
                    </span>
                  ) : null}
                  {!model.valid ? <span className="settings-badge" data-state="failed">无效</span> : null}
                </div>
                <div className="settings-list-desc">{model.directory}</div>
                <div className="settings-list-desc">
                  游戏表情：{model.gameExpression || '—'} · 音乐表情：{model.musicExpression || '—'}
                </div>
                {model.error ? <div className="settings-row-error">{model.error}</div> : null}
                <div className="settings-card-actions">
                  <button
                    type="button"
                    className="btn btn-sm btn-ghost"
                    disabled={index === data.activeIndex}
                    onClick={() => void setActive(index)}
                  >
                    设为当前
                  </button>
                  <button type="button" className="btn btn-sm btn-ghost" onClick={() => void validate(index)}>
                    校验
                  </button>
                  <button type="button" className="btn btn-sm btn-danger" onClick={() => setRemoving(index)}>
                    <Trash2 size={13} /> 移除
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </SettingsCard>

      <Dialog
        open={removing !== null}
        onOpenChange={(open) => {
          if (!open) setRemoving(null)
        }}
        title="移除 Live2D 模型"
        description={`「${removing !== null ? data?.models[removing]?.name ?? '' : ''}」的模型目录会被删除，无法撤销。`}
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setRemoving(null)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void remove()}>
            移除
          </button>
        </div>
      </Dialog>
    </div>
  )
}