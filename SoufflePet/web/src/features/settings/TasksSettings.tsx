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

/** 卡片底部左侧的调度方式摘要。 */
function scheduleText(task: TaskDto): string {
  switch (task.kind) {
    case 'once':
      return `单次 ${task.onceDate ?? ''} ${task.onceTime ?? task.timeOfDay ?? ''}`.trim()
    case 'daily':
      return `每天 ${task.timeOfDay ?? '09:00'}`
    case 'weekly': {
      const days = task.weekdays.length
        ? task.weekdays.map((day) => `周${WEEKDAYS[day] ?? ''}`).join('、')
        : '未选星期'
      return `${days} ${task.timeOfDay ?? '09:00'}`
    }
    case 'interval':
      return `每 ${task.intervalMinutes} 分钟`
    default:
      return task.kind
  }
}

/** 卡片底部右侧的下次执行时间。 */
function nextRunText(task: TaskDto): string {
  if (!task.enabled) return '已停用'
  if (!task.nextRunAt) return '待调度'
  const next = new Date(task.nextRunAt)
  if (Number.isNaN(next.getTime())) return '待调度'
  return `下次 ${next.toLocaleString('zh-CN', {
    month: 'numeric',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })}`
}

export function TasksSettings({ view = 'tasks' }: { view?: 'tasks' | 'history' }) {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<TasksListPayload | null>(null)
  const [editing, setEditing] = useState<TaskInputDto | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const load = useCallback(async () => {
    try {
      const payload = await invoke<TasksListPayload>('settings.tasks.list')
      setData(payload)
      setError(null)
    } catch (loadError) {
      setError(`读取定时任务失败：${errorMessage(loadError)}`)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const openNew = () => {
    setEditing(toInput(null))
    setError(null)
  }

  const openEdit = (task: TaskDto) => {
    setEditing(toInput(task))
    setError(null)
  }

  const closeEditor = () => {
    setEditing(null)
    setConfirmDelete(false)
  }

  const save = async () => {
    if (!editing) return
    setBusy(true)
    setError(null)
    try {
      await invoke<TaskDto>('settings.tasks.save', editing)
      await load()
      closeEditor()
      pushToast({ level: 'success', text: '已保存定时任务' })
    } catch (saveError) {
      setError(`保存失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!editing?.id) return
    setBusy(true)
    try {
      await invoke('settings.tasks.delete', { id: editing.id })
      await load()
      closeEditor()
      pushToast({ level: 'success', text: '已删除定时任务' })
    } catch (deleteError) {
      setError(`删除失败：${errorMessage(deleteError)}`)
    } finally {
      setBusy(false)
    }
  }

  const runNow = async () => {
    if (!editing?.id) return
    try {
      await invoke('settings.tasks.runNow', { id: editing.id })
      pushToast({ level: 'info', text: '已触发一次任务' })
      await load()
    } catch (runError) {
      setError(`执行失败：${errorMessage(runError)}`)
    }
  }

  const toggleEnabled = async (task: TaskDto) => {
    try {
      const updated = await invoke<TaskDto>('settings.tasks.setEnabled', {
        id: task.id,
        enabled: !task.enabled,
      })
      if (!updated) {
        await load()
        return
      }
      setData((current) =>
        current
          ? { ...current, tasks: current.tasks.map((item) => (item.id === updated.id ? updated : item)) }
          : current,
      )
    } catch (toggleError) {
      setError(`切换任务状态失败：${errorMessage(toggleError)}`)
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
    if (editing) setEditing({ ...editing, ...patch })
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      {view === 'tasks' ? (
        <>
          <div className="settings-toolbar">
            <span className="settings-toolbar-info">共 {data?.tasks.length ?? 0} 个任务</span>
            <button type="button" className="btn btn-sm btn-primary" onClick={openNew}>
              <Plus size={13} /> 新增任务
            </button>
          </div>

          {!data ? (
            <SettingsEmpty>正在读取定时任务…</SettingsEmpty>
          ) : data.tasks.length === 0 ? (
            <SettingsEmpty>还没有定时任务；点击右上角「新增任务」，到点后桌宠会按内容发起一轮对话。</SettingsEmpty>
          ) : (
            <div className="task-list">
              {data.tasks.map((task) => (
                <article className="task-card" data-enabled={task.enabled.toString()} key={task.id}>
                  <button type="button" className="task-card-main" onClick={() => openEdit(task)}>
                    <span className="task-card-title">{task.name}</span>
                    <span className="task-card-content">{task.content || '没有填写任务内容'}</span>
                    <span className="task-card-foot">
                      <span className="task-card-schedule">
                        <Clock size={12} />
                        {scheduleText(task)}
                      </span>
                      <span className="task-card-next">{nextRunText(task)}</span>
                    </span>
                  </button>
                  <label className="task-card-switch" title={task.enabled ? '停用任务' : '启用任务'}>
                    <input
                      type="checkbox"
                      checked={task.enabled}
                      aria-label={task.enabled ? '停用任务' : '启用任务'}
                      onChange={() => void toggleEnabled(task)}
                    />
                    <span className="task-card-switch-track" aria-hidden="true" />
                  </label>
                </article>
              ))}
            </div>
          )}
        </>
      ) : (
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
      )}

      <Dialog
        open={editing !== null}
        onOpenChange={(open) => {
          if (!open) closeEditor()
        }}
        title={editing?.id ? '编辑定时任务' : '新建定时任务'}
        description="任务内容会作为提示词交给桌宠；启用后由调度器到点触发。"
        className="settings-dialog"
      >
        {editing ? (
          <div className="settings-form" style={{ marginTop: 14 }}>
            <SettingsError message={error} />

            <div className="settings-grid-2">
              <div className="field">
                <label className="field-label" htmlFor="task-name">名称</label>
                <input
                  id="task-name"
                  className="input"
                  value={editing.name}
                  onChange={(event) => update({ name: event.target.value })}
                />
              </div>
              <div className="field">
                <label className="field-label" htmlFor="task-kind">调度方式</label>
                <select
                  id="task-kind"
                  className="select"
                  value={editing.kind}
                  onChange={(event) => {
                    const kind = event.target.value as TaskInputDto['kind']
                    const today = new Date()
                    const onceDate =
                      kind === 'once' && !editing.onceDate
                        ? `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`
                        : editing.onceDate
                    update({ kind, onceDate, onceTime: editing.onceTime ?? editing.timeOfDay ?? '09:00' })
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
                value={editing.content}
                onChange={(event) => update({ content: event.target.value })}
              />
            </div>

            {editing.kind === 'once' ? (
              <div className="settings-grid-2">
                <div className="field">
                  <label className="field-label" htmlFor="task-once-date">日期</label>
                  <input
                    id="task-once-date"
                    className="input"
                    type="date"
                    value={editing.onceDate ?? ''}
                    onChange={(event) => update({ onceDate: event.target.value })}
                  />
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="task-once-time">时间</label>
                  <input
                    id="task-once-time"
                    className="input"
                    type="time"
                    value={editing.onceTime ?? editing.timeOfDay ?? '09:00'}
                    onChange={(event) => update({ onceTime: event.target.value })}
                  />
                </div>
              </div>
            ) : editing.kind === 'interval' ? (
              <div className="field" style={{ maxWidth: 220 }}>
                <label className="field-label" htmlFor="task-interval">间隔（分钟）</label>
                <input
                  id="task-interval"
                  className="input"
                  type="number"
                  min={1}
                  max={43200}
                  value={editing.intervalMinutes}
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
                    value={editing.timeOfDay ?? '09:00'}
                    onChange={(event) => update({ timeOfDay: event.target.value })}
                  />
                </div>
                {editing.kind === 'weekly' ? (
                  <div className="field">
                    <div className="field-label">星期</div>
                    <div className="settings-weekdays">
                      {WEEKDAYS.map((label, day) => (
                        <label className="settings-weekday" key={label}>
                          <input
                            type="checkbox"
                            checked={editing.weekdays.includes(day)}
                            onChange={(event) =>
                              update({
                                weekdays: event.target.checked
                                  ? [...editing.weekdays, day].sort()
                                  : editing.weekdays.filter((item) => item !== day),
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
              checked={editing.enabled}
              onChange={(enabled) => update({ enabled })}
              label="启用该任务"
            />

            <div className="task-dialog-actions">
              {editing.id ? (
                <div className="task-dialog-group">
                  <button type="button" className="btn btn-sm btn-ghost" onClick={() => void runNow()}>
                    <Play size={13} /> 立即执行
                  </button>
                  <button type="button" className="btn btn-sm btn-danger" onClick={() => setConfirmDelete(true)}>
                    <Trash2 size={13} /> 删除
                  </button>
                </div>
              ) : (
                <span />
              )}
              <div className="task-dialog-group">
                <button type="button" className="btn btn-ghost" onClick={closeEditor}>
                  取消
                </button>
                <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void save()}>
                  {busy ? '保存中…' : '保存'}
                </button>
              </div>
            </div>
          </div>
        ) : null}
      </Dialog>

      <Dialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title="删除定时任务"
        description={`「${editing?.name ?? ''}」将从任务列表移除。`}
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
