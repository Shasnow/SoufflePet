// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Clock, Play, Plus, Trash2 } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ScheduledTaskLogDto, TaskDto, TaskInputDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError, Toggle } from './SettingsShared'

interface TasksListPayload {
  tasks: TaskDto[]
  logs: ScheduledTaskLogDto[]
}

const WEEKDAYS = ['日', '一', '二', '三', '四', '五', '六']

function toInput(task: TaskDto | null): TaskInputDto {
  if (task) {
    return {
      id: task.id,
      name: task.name,
      content: task.content,
      enabled: task.enabled,
      kind: task.kind,
      onceDate: task.onceDate,
      onceTime: task.onceTime,
      timeOfDay: task.timeOfDay,
      weekdays: [...task.weekdays],
      intervalMinutes: task.intervalMinutes,
    }
  }

  return {
    name: '新任务',
    content: '',
    enabled: true,
    kind: 'daily',
    timeOfDay: '09:00',
    weekdays: [],
    intervalMinutes: 60,
  }
}

export function TasksSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<TasksListPayload | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [draft, setDraft] = useState<TaskInputDto | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = useCallback(async (selectId?: string) => {
    try {
      const payload = await invoke<TasksListPayload>('settings.tasks.list')
      setData(payload)
      const next =
        selectId && payload.tasks.some((task) => task.id === selectId)
          ? selectId
          : payload.tasks[0]?.id ?? null
      setSelectedId(next)
      setError(null)
    } catch (loadError) {
      setError(`读取定时任务失败：${errorMessage(loadError)}`)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!data || !selectedId) return
    const task = data.tasks.find((item) => item.id === selectedId)
    setDraft(task ? toInput(task) : null)
    // 只在选中项变化/列表刷新时同步表单；新建态由 startNew 自己维护。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, selectedId])

  const startNew = () => {
    setSelectedId(null)
    setDraft(toInput(null))
    setError(null)
  }

  const save = async () => {
    if (!draft) return
    setBusy(true)
    setError(null)
    try {
      const saved = await invoke<TaskDto>('settings.tasks.save', draft)
      await load(saved.id)
      pushToast({ level: 'success', text: '已保存定时任务' })
    } catch (saveError) {
      setError(`保存失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!draft?.id) return
    setBusy(true)
    try {
      await invoke('settings.tasks.delete', { id: draft.id })
      setConfirmDelete(false)
      await load()
      pushToast({ level: 'success', text: '已删除定时任务' })
    } catch (deleteError) {
      setError(`删除失败：${errorMessage(deleteError)}`)
    } finally {
      setBusy(false)
    }
  }

  const runNow = async () => {
    if (!draft?.id) return
    try {
      await invoke('settings.tasks.runNow', { id: draft.id })
      pushToast({ level: 'info', text: '已触发一次任务' })
      await load(draft.id)
    } catch (runError) {
      setError(`执行失败：${errorMessage(runError)}`)
    }
  }

  const clearLogs = async () => {
    try {
      await invoke('settings.tasks.clearLogs')
      setData((current) => (current ? { ...current, logs: [] } : current))
    } catch (clearError) {
      setError(`清空日志失败：${errorMessage(clearError)}`)
    }
  }

  const update = (patch: Partial<TaskInputDto>) => {
    if (draft) setDraft({ ...draft, ...patch })
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <div className="settings-split">
        <div className="settings-list">
          <div className="settings-list-head">
            <span>任务</span>
            <button type="button" className="btn btn-sm btn-ghost" onClick={startNew}>
              <Plus size={13} /> 新增
            </button>
          </div>
          {data?.tasks.length === 0 ? (
            <SettingsEmpty>还没有定时任务；到点后桌宠会按内容发起一轮对话。</SettingsEmpty>
          ) : (
            data?.tasks.map((task) => (
              <button
                key={task.id}
                type="button"
                className="settings-list-item"
                data-active={(task.id === selectedId).toString()}
                onClick={() => setSelectedId(task.id)}
              >
                <span className="settings-list-title">{task.name}</span>
                <span className="settings-list-desc">
                  {task.enabled ? '已启用' : '已停用'} · {task.kind}
                  {task.nextRunAt ? ` · 下次 ${new Date(task.nextRunAt).toLocaleString()}` : ''}
                </span>
              </button>
            ))
          )}
        </div>

        <div className="settings-editor">
          {!draft ? (
            <SettingsEmpty>选择左侧任务，或新增一个。</SettingsEmpty>
          ) : (
            <SettingsCard
              title={draft.id ? '编辑定时任务' : '新建定时任务'}
              desc="任务内容会作为提示词交给桌宠；启用后由调度器到点触发。"
              actions={
                draft.id ? (
                  <>
                    <button type="button" className="btn btn-sm btn-ghost" onClick={() => void runNow()}>
                      <Play size={13} /> 立即执行
                    </button>
                    <button type="button" className="btn btn-sm btn-danger" onClick={() => setConfirmDelete(true)}>
                      <Trash2 size={13} /> 删除
                    </button>
                  </>
                ) : null
              }
            >
              <div className="settings-form">
                <div className="settings-grid-2">
                  <div className="field">
                    <label className="field-label" htmlFor="task-name">名称</label>
                    <input
                      id="task-name"
                      className="input"
                      value={draft.name}
                      onChange={(event) => update({ name: event.target.value })}
                    />
                  </div>
                  <div className="field">
                    <label className="field-label" htmlFor="task-kind">调度方式</label>
                    <select
                      id="task-kind"
                      className="select"
                      value={draft.kind}
                      onChange={(event) => {
                        const kind = event.target.value as TaskInputDto['kind']
                        const today = new Date()
                        const onceDate =
                          kind === 'once' && !draft.onceDate
                            ? `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`
                            : draft.onceDate
                        update({ kind, onceDate, onceTime: draft.onceTime ?? draft.timeOfDay ?? '09:00' })
                      }}
                    >
                      <option value="once">单次</option>
                      <option value="daily">每日</option>
                      <option value="weekly">每周</option>
                      <option value="interval">固定间隔</option>
                    </select>
                  </div>
                </div>

                <div className="field">
                  <label className="field-label" htmlFor="task-content">任务内容</label>
                  <textarea
                    id="task-content"
                    className="textarea"
                    rows={4}
                    placeholder="例如：提醒我起来喝水，并关心一下我的状态"
                    value={draft.content}
                    onChange={(event) => update({ content: event.target.value })}
                  />
                </div>

                {draft.kind === 'once' ? (
                  <div className="settings-grid-2">
                    <div className="field">
                      <label className="field-label" htmlFor="task-once-date">日期</label>
                      <input
                        id="task-once-date"
                        className="input"
                        type="date"
                        value={draft.onceDate ?? ''}
                        onChange={(event) => update({ onceDate: event.target.value })}
                      />
                    </div>
                    <div className="field">
                      <label className="field-label" htmlFor="task-once-time">时间</label>
                      <input
                        id="task-once-time"
                        className="input"
                        type="time"
                        value={draft.onceTime ?? draft.timeOfDay ?? '09:00'}
                        onChange={(event) => update({ onceTime: event.target.value })}
                      />
                    </div>
                  </div>
                ) : draft.kind === 'interval' ? (
                  <div className="field" style={{ maxWidth: 220 }}>
                    <label className="field-label" htmlFor="task-interval">间隔（分钟）</label>
                    <input
                      id="task-interval"
                      className="input"
                      type="number"
                      min={1}
                      max={43200}
                      value={draft.intervalMinutes}
                      onChange={(event) => update({ intervalMinutes: Number(event.target.value) || 60 })}
                    />
                  </div>
                ) : (
                  <>
                    <div className="field" style={{ maxWidth: 220 }}>
                      <label className="field-label" htmlFor="task-time">触发时间</label>
                      <input
                        id="task-time"
                        className="input"
                        type="time"
                        value={draft.timeOfDay ?? '09:00'}
                        onChange={(event) => update({ timeOfDay: event.target.value })}
                      />
                    </div>
                    {draft.kind === 'weekly' ? (
                      <div className="field">
                        <div className="field-label">星期</div>
                        <div className="settings-weekdays">
                          {WEEKDAYS.map((label, day) => (
                            <label className="settings-weekday" key={label}>
                              <input
                                type="checkbox"
                                checked={draft.weekdays.includes(day)}
                                onChange={(event) =>
                                  update({
                                    weekdays: event.target.checked
                                      ? [...draft.weekdays, day].sort()
                                      : draft.weekdays.filter((item) => item !== day),
                                  })
                                }
                              />
                              <span>{label}</span>
                            </label>
                          ))}
                        </div>
                      </div>
                    ) : null}
                  </>
                )}

                <Toggle
                  checked={draft.enabled}
                  onChange={(enabled) => update({ enabled })}
                  label="启用该任务"
                />

                <div className="settings-actions">
                  <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void save()}>
                    {busy ? '保存中…' : '保存任务'}
                  </button>
                </div>
              </div>
            </SettingsCard>
          )}
        </div>
      </div>

      <SettingsCard
        title="执行日志"
        desc="最近的任务触发与执行结果。"
        actions={
          <button type="button" className="btn btn-sm btn-ghost" onClick={() => void clearLogs()}>
            清空日志
          </button>
        }
      >
        {data?.logs.length === 0 ? (
          <SettingsEmpty>还没有日志。</SettingsEmpty>
        ) : (
          <div className="settings-log">
            {data?.logs.map((log, index) => (
              <div className="settings-log-line" data-error={log.isError.toString()} key={`${index}-${log.text}`}>
                <Clock size={12} />
                <span>{log.text}</span>
              </div>
            ))}
          </div>
        )}
      </SettingsCard>

      <Dialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title="删除定时任务"
        description={`「${draft?.name ?? ''}」将从任务列表移除。`}
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