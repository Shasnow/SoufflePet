// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Pencil, Plus, Trash2 } from 'lucide-react'
import { useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { MemoryDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { formatDateTime } from '@/ui/format'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError, Toggle } from './SettingsShared'

export function MemorySettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<MemoryDto | null>(null)
  const [key, setKey] = useState('')
  const [value, setValue] = useState('')
  const [editing, setEditing] = useState<{ key: string; value: string; previousKey: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmClear, setConfirmClear] = useState(false)

  useEffect(() => {
    let alive = true
    invoke<MemoryDto>('settings.memory.get')
      .then((memory) => {
        if (alive) setData(memory)
      })
      .catch((loadError) => {
        if (alive) setError(`读取记忆失败：${errorMessage(loadError)}`)
      })
    return () => {
      alive = false
    }
  }, [])

  /** 统一的写操作：成功后用宿主返回的最新列表刷新界面；返回是否成功。 */
  const perform = async (
    request: () => Promise<MemoryDto>,
    failure: string,
    success?: string,
  ): Promise<boolean> => {
    setBusy(true)
    setError(null)
    try {
      setData(await request())
      if (success) pushToast({ level: 'success', text: success })
      return true
    } catch (actionError) {
      setError(`${failure}：${errorMessage(actionError)}`)
      return false
    } finally {
      setBusy(false)
    }
  }

  const toggle = (enabled: boolean) =>
    void perform(() => invoke<MemoryDto>('settings.memory.setEnabled', { enabled }), '切换记忆开关失败')

  const add = async () => {
    if (!key.trim() || !value.trim()) {
      setError('键和内容都不能为空')
      return
    }
    const saved = await perform(
      () => invoke<MemoryDto>('settings.memory.save', { key: key.trim(), value: value.trim() }),
      '保存记忆失败',
      '记忆已保存',
    )
    if (saved) {
      setKey('')
      setValue('')
    }
  }

  const saveEdit = async () => {
    if (!editing) return
    const nextKey = editing.key.trim()
    if (!nextKey || !editing.value.trim()) {
      setError('键和内容都不能为空')
      return
    }
    // 改了键名时带上旧键，让宿主删掉旧条目，避免同一条记忆留下两份。
    const params =
      nextKey === editing.previousKey
        ? { key: nextKey, value: editing.value }
        : { key: nextKey, value: editing.value, previousKey: editing.previousKey }
    const saved = await perform(
      () => invoke<MemoryDto>('settings.memory.save', params),
      '保存记忆失败',
      '记忆已更新',
    )
    if (saved) setEditing(null)
  }

  const remove = async (entryKey: string) => {
    const removed = await perform(
      () => invoke<MemoryDto>('settings.memory.delete', { key: entryKey }),
      '删除记忆失败',
      '记忆已删除',
    )
    if (removed && editing?.previousKey === entryKey) setEditing(null)
  }

  const clearAll = async () => {
    const cleared = await perform(() => invoke<MemoryDto>('settings.memory.clear'), '清空记忆失败', '记忆已清空')
    if (cleared) {
      setEditing(null)
      setConfirmClear(false)
    }
  }

  const entries = data?.entries ?? []

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="长期记忆"
        desc="桌宠通过 remember/forget 工具记住的事实，跨会话共享。关闭后不再注入对话，已保存的内容会保留。"
        actions={
          <button
            type="button"
            className="btn btn-sm btn-ghost"
            disabled={busy || entries.length === 0}
            onClick={() => setConfirmClear(true)}
          >
            <Trash2 size={13} /> 清空全部
          </button>
        }
      >
        <Toggle
          checked={data?.enabled ?? false}
          disabled={busy || !data}
          onChange={toggle}
          label="启用长期记忆"
        />
        <div className="field-hint">{data ? `存储位置：${data.path}` : '正在读取记忆…'}</div>
      </SettingsCard>

      <SettingsCard title="记忆条目" desc={`共 ${entries.length} 条；键最多 30 个字符，修改后立即生效。`}>
        <div className="memory-add">
          <input
            className="input memory-add-key"
            value={key}
            maxLength={30}
            placeholder="键，如：用户称呼"
            aria-label="记忆键"
            onChange={(event) => setKey(event.target.value)}
          />
          <input
            className="input memory-add-value"
            value={value}
            maxLength={500}
            placeholder="内容，如：老板"
            aria-label="记忆内容"
            onChange={(event) => setValue(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault()
                void add()
              }
            }}
          />
          <button type="button" className="btn btn-sm btn-primary" disabled={busy} onClick={() => void add()}>
            <Plus size={13} /> 添加
          </button>
        </div>

        {entries.length === 0 ? (
          <SettingsEmpty>还没有长期记忆。桌宠会在对话中使用 remember 记住你的偏好，也可以在上面手动添加。</SettingsEmpty>
        ) : (
          <div className="memory-list">
            {entries.map((entry) => {
              const isEditing = editing?.previousKey === entry.key
              return (
                <div className="memory-entry" key={entry.key}>
                  <div className="memory-entry-head">
                    <span className="memory-entry-key">{entry.key}</span>
                    <span className="memory-entry-time">{formatDateTime(entry.updatedAt)}</span>
                    {isEditing ? null : (
                      <div className="memory-entry-actions">
                        <button
                          type="button"
                          className="icon-btn"
                          aria-label={`编辑记忆 ${entry.key}`}
                          title="编辑"
                          onClick={() => setEditing({ key: entry.key, value: entry.value, previousKey: entry.key })}
                        >
                          <Pencil size={13} />
                        </button>
                        <button
                          type="button"
                          className="icon-btn"
                          aria-label={`删除记忆 ${entry.key}`}
                          title="删除"
                          disabled={busy}
                          onClick={() => void remove(entry.key)}
                        >
                          <Trash2 size={13} />
                        </button>
                      </div>
                    )}
                  </div>

                  {isEditing ? (
                    <div className="memory-entry-editor">
                      <input
                        className="input"
                        value={editing.key}
                        maxLength={30}
                        aria-label={`编辑记忆键 ${entry.key}`}
                        placeholder="键"
                        onChange={(event) => setEditing({ ...editing, key: event.target.value })}
                      />
                      <textarea
                        className="textarea"
                        rows={3}
                        maxLength={500}
                        aria-label={`编辑记忆内容 ${entry.key}`}
                        value={editing.value}
                        onChange={(event) => setEditing({ ...editing, value: event.target.value })}
                      />
                      <div className="settings-actions">
                        <button type="button" className="btn btn-sm btn-ghost" onClick={() => setEditing(null)}>
                          取消
                        </button>
                        <button
                          type="button"
                          className="btn btn-sm btn-primary"
                          disabled={busy}
                          onClick={() => void saveEdit()}
                        >
                          保存
                        </button>
                      </div>
                    </div>
                  ) : (
                    <div className="memory-entry-value">{entry.value}</div>
                  )}
                </div>
              )
            })}
          </div>
        )}
      </SettingsCard>

      <Dialog
        open={confirmClear}
        onOpenChange={setConfirmClear}
        title="清空长期记忆"
        description="所有已记住的事实会被删除且无法恢复；对话历史不受影响。"
      >
        <div className="dialog-actions">
          <button type="button" className="btn btn-ghost" onClick={() => setConfirmClear(false)}>
            取消
          </button>
          <button type="button" className="btn btn-danger" disabled={busy} onClick={() => void clearAll()}>
            清空
          </button>
        </div>
      </Dialog>
    </div>
  )
}