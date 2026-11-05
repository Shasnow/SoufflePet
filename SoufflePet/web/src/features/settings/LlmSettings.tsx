// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Plus, RefreshCw, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatModelDto, ProviderDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError } from './SettingsShared'

interface LlmListPayload {
  providers: ProviderDto[]
  enabledModels: ChatModelDto[]
  defaultKey: string | null
}

function cloneProvider(provider: ProviderDto): ProviderDto {
  return { ...provider, models: provider.models.map((model) => ({ ...model })) }
}

export function LlmSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<LlmListPayload | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [draft, setDraft] = useState<ProviderDto | null>(null)
  const [originalName, setOriginalName] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [newModel, setNewModel] = useState('')
  const [fetching, setFetching] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [fetchStatus, setFetchStatus] = useState('')
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = useCallback(
    async (selectName?: string) => {
      try {
        const payload = await invoke<LlmListPayload>('settings.llm.list')
        setData(payload)
        const next =
          selectName && payload.providers.some((provider) => provider.name === selectName)
            ? selectName
            : selected && payload.providers.some((provider) => provider.name === selected)
              ? selected
              : payload.providers[0]?.name ?? null
        setSelected(next)
        setError(null)
      } catch (loadError) {
        setError(`读取对话模型失败：${errorMessage(loadError)}`)
      }
    },
    [selected],
  )

  useEffect(() => {
    void load()
    // 只在首次进入面板时加载；切换/保存后会显式刷新。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!data || creating) return
    if (!selected) {
      setDraft(null)
      setOriginalName(null)
      return
    }

    const provider = data.providers.find((item) => item.name === selected) ?? null
    setDraft(provider ? cloneProvider(provider) : null)
    setOriginalName(provider?.name ?? null)
    setFetchStatus('')
    setNewModel('')
    // creating 是新建态：此时 selected 为 null，不能把刚填的草稿清掉。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, selected, creating])

  const startNewProvider = () => {
    const base = '新提供商'
    let name = base
    for (let index = 2; data?.providers.some((provider) => provider.name === name); index++) {
      name = `${base} ${index}`
    }

    setSelected(null)
    setOriginalName(null)
    setCreating(true)
    setDraft({ name, baseUrl: '', apiKey: '', models: [] })
    setFetchStatus('')
    setError(null)
  }

  const save = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      await invoke<ProviderDto>('settings.llm.saveProvider', { ...draft, previousName: originalName })
      setCreating(false)
      await load(draft.name.trim())
      pushToast({ level: 'success', text: '已保存提供商配置' })
    } catch (saveError) {
      setError(`保存失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!selected) return
    setBusy(true)
    try {
      await invoke('settings.llm.deleteProvider', { name: selected })
      setConfirmDelete(false)
      await load()
      pushToast({ level: 'success', text: `已删除提供商「${selected}」` })
    } catch (deleteError) {
      setError(`删除失败：${errorMessage(deleteError)}`)
    } finally {
      setBusy(false)
    }
  }

  const fetchModels = async () => {
    if (!draft || fetching) return
    setFetching(true)
    setFetchStatus('正在获取模型列表…')
    try {
      const result = await invoke<{ modelIds: string[] }>('settings.llm.fetchModels', {
        baseUrl: draft.baseUrl,
        apiKey: draft.apiKey,
      })
      const existing = new Set(draft.models.map((model) => model.modelId.toLowerCase()))
      const additions = result.modelIds.filter((id) => !existing.has(id.toLowerCase()))
      setDraft({
        ...draft,
        models: [
          ...draft.models,
          ...additions.map((modelId) => ({ modelId, isActive: false })),
        ],
      })
      setFetchStatus(
        result.modelIds.length === 0
          ? '接口没有返回模型，可以手动添加'
          : `获取到 ${result.modelIds.length} 个模型，新增 ${additions.length} 个`,
      )
    } catch (fetchError) {
      setFetchStatus(`获取失败：${errorMessage(fetchError)}`)
    } finally {
      setFetching(false)
    }
  }

  const addModel = () => {
    if (!draft) return
    const modelId = newModel.trim()
    if (!modelId) return
    if (draft.models.some((model) => model.modelId.toLowerCase() === modelId.toLowerCase())) {
      setFetchStatus(`「${modelId}」已在列表中`)
      return
    }

    setDraft({ ...draft, models: [...draft.models, { modelId, isActive: true }] })
    setNewModel('')
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard title="默认模型" desc="新会话使用；也可以在每个会话的输入框下方临时切换。">
        <select
          className="select"
          value={data?.defaultKey ?? ''}
          disabled={!data || data.enabledModels.length === 0}
          onChange={async (event) => {
            if (!data) return
            try {
              await invoke('settings.llm.setDefault', { key: event.target.value })
              await load(selected ?? undefined)
            } catch (defaultError) {
              setError(`设置默认模型失败：${errorMessage(defaultError)}`)
            }
          }}
        >
          <option value="">未选择</option>
          {data?.enabledModels.map((model) => (
            <option key={model.key} value={model.key}>
              {model.provider} · {model.name}
            </option>
          ))}
        </select>
      </SettingsCard>

      <div className="settings-split">
        <div className="settings-list">
          <div className="settings-list-head">
            <span>提供商</span>
            <button type="button" className="btn btn-sm btn-ghost" onClick={startNewProvider}>
              <Plus size={13} /> 新增
            </button>
          </div>
          {data?.providers.length === 0 ? (
            <SettingsEmpty>还没有提供商，先新增一个 OpenAI 兼容接口。</SettingsEmpty>
          ) : (
            data?.providers.map((provider) => (
              <button
                key={provider.name}
                type="button"
                className="settings-list-item"
                data-active={(provider.name === selected).toString()}
                onClick={() => {
                  setCreating(false)
                  setSelected(provider.name)
                }}
              >
                <span className="settings-list-title">{provider.name}</span>
                <span className="settings-list-desc">{provider.baseUrl}</span>
              </button>
            ))
          )}
        </div>

        <div className="settings-editor">
          {!draft ? (
            <SettingsEmpty>选择左侧提供商，或新增一个。</SettingsEmpty>
          ) : (
            <SettingsCard
              title={originalName ? `编辑「${originalName}」` : '新建提供商'}
              desc="接口、密钥与该提供商下的模型清单；勾选的模型会出现在对话窗口。"
              actions={
                originalName ? (
                  <button type="button" className="btn btn-sm btn-danger" onClick={() => setConfirmDelete(true)}>
                    <Trash2 size={13} /> 删除
                  </button>
                ) : null
              }
            >
              <div className="settings-form">
                <div className="field">
                  <label className="field-label" htmlFor="llm-name">提供商名称</label>
                  <input
                    id="llm-name"
                    className="input"
                    value={draft.name}
                    onChange={(event) => setDraft({ ...draft, name: event.target.value })}
                  />
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="llm-url">接口地址</label>
                  <input
                    id="llm-url"
                    className="input"
                    placeholder="https://api.example.com/v1"
                    value={draft.baseUrl}
                    onChange={(event) => setDraft({ ...draft, baseUrl: event.target.value })}
                  />
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="llm-key">API 密钥</label>
                  <input
                    id="llm-key"
                    className="input"
                    type="password"
                    value={draft.apiKey}
                    onChange={(event) => setDraft({ ...draft, apiKey: event.target.value })}
                  />
                </div>

                <div className="settings-subhead">
                  <span>模型清单</span>
                  <button type="button" className="btn btn-sm btn-ghost" disabled={fetching} onClick={() => void fetchModels()}>
                    <RefreshCw size={13} /> {fetching ? '获取中…' : '获取模型'}
                  </button>
                </div>
                {fetchStatus ? <div className="field-hint">{fetchStatus}</div> : null}

                <div className="settings-model-list">
                  {draft.models.length === 0 ? (
                    <div className="field-hint">暂无模型；可以手填模型标识，或从接口获取。</div>
                  ) : (
                    draft.models.map((model, index) => (
                      <div className="settings-model-row" key={`${model.modelId}-${index}`}>
                        <input
                          className="checkbox"
                          type="checkbox"
                          checked={model.isActive}
                          onChange={(event) => {
                            const models = draft.models.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, isActive: event.target.checked } : item,
                            )
                            setDraft({ ...draft, models })
                          }}
                          aria-label={`启用 ${model.modelId}`}
                        />
                        <span className="settings-model-id">{model.modelId}</span>
                        <input
                          className="input settings-model-name"
                          placeholder="显示名称（可选）"
                          value={model.name ?? ''}
                          onChange={(event) => {
                            const models = draft.models.map((item, itemIndex) =>
                              itemIndex === index ? { ...item, name: event.target.value } : item,
                            )
                            setDraft({ ...draft, models })
                          }}
                        />
                        <button
                          type="button"
                          className="icon-btn"
                          aria-label={`移除模型 ${model.modelId}`}
                          onClick={() => setDraft({ ...draft, models: draft.models.filter((_, itemIndex) => itemIndex !== index) })}
                        >
                          <Trash2 size={12} />
                        </button>
                      </div>
                    ))
                  )}
                </div>

                <div className="settings-inline">
                  <input
                    className="input"
                    placeholder="手动添加模型标识，如 deepseek-chat"
                    value={newModel}
                    onChange={(event) => setNewModel(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter') {
                        event.preventDefault()
                        addModel()
                      }
                    }}
                  />
                  <button type="button" className="btn btn-sm" onClick={addModel}>
                    添加
                  </button>
                </div>

                <div className="settings-actions">
                  <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void save()}>
                    {busy ? '保存中…' : '保存配置'}
                  </button>
                </div>
              </div>
            </SettingsCard>
          )}
        </div>
      </div>

      <Dialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title="删除提供商"
        description={`「${selected ?? ''}」及其模型配置会被移除，对话窗口将不再显示这些模型。`}
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setConfirmDelete(false)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void remove()}>
            删除
          </button>
        </div>
      </Dialog>
    </div>
  )
}