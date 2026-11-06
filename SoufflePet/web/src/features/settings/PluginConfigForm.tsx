// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useEffect, useMemo, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ConfigFieldDto } from '@/bridge/contracts'
import { errorMessage, SettingsError } from './SettingsShared'

interface ConfigSchemaPayload {
  fields: ConfigFieldDto[]
  config: Record<string, unknown>
}

function initialValue(field: ConfigFieldDto, config: Record<string, unknown>): unknown {
  if (field.value !== undefined && field.value !== null) return field.value
  return config[field.key]
}

function displayValue(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (Array.isArray(value)) return value.join('\n')
  return String(value)
}

export function PluginConfigForm({
  pluginId,
  onSaved,
}: {
  pluginId: string
  onSaved: () => void
}) {
  const [schema, setSchema] = useState<ConfigSchemaPayload | null>(null)
  const [values, setValues] = useState<Record<string, unknown>>({})
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let alive = true
    void (async () => {
      try {
        const payload = await invoke<ConfigSchemaPayload>('settings.plugins.getConfigSchema', { id: pluginId })
        if (!alive) return
        setSchema(payload)
        const next: Record<string, unknown> = { ...payload.config }
        for (const field of payload.fields) {
          next[field.key] = initialValue(field, payload.config ?? {})
        }
        setValues(next)
      } catch (loadError) {
        if (alive) setError(`读取配置表单失败：${errorMessage(loadError)}`)
      }
    })()
    return () => {
      alive = false
    }
  }, [pluginId])

  const fields = useMemo(() => schema?.fields ?? [], [schema])

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      // 保留 config 里未出现在 schema 的字段，避免保存表单时丢掉插件自定义项
      const config: Record<string, unknown> = { ...(schema?.config ?? {}) }
      for (const field of fields) {
        const value = values[field.key]
        if (field.kind === 'boolean') config[field.key] = value === true
        else if (field.kind === 'number') config[field.key] = Number(value) || 0
        else if (field.kind === 'list') {
          config[field.key] = Array.isArray(value)
            ? value
            : String(value ?? '')
                .split(/[\n,]/)
                .map((item) => item.trim())
                .filter(Boolean)
        } else config[field.key] = value ?? ''
      }

      await invoke('settings.plugins.saveConfig', { id: pluginId, config })
      onSaved()
    } catch (saveError) {
      setError(`保存失败：${errorMessage(saveError)}`)
    } finally {
      setBusy(false)
    }
  }

  if (!schema) return <SettingsError message={error} />

  return (
    <div className="settings-form">
      <SettingsError message={error} />
      {fields.length === 0 ? (
        <div className="field-hint">该插件没有声明 configSchema，可直接保存空配置。</div>
      ) : (
        fields.map((field) => {
          const value = values[field.key]
          return (
            <div className="field" key={field.key}>
              <label className="field-label" htmlFor={`plugin-${pluginId}-${field.key}`}>
                {field.label || field.key}
              </label>
              {field.kind === 'boolean' ? (
                <label className="settings-toggle">
                  <input
                    id={`plugin-${pluginId}-${field.key}`}
                    type="checkbox"
                    checked={value === true}
                    onChange={(event) => setValues({ ...values, [field.key]: event.target.checked })}
                  />
                  <span>{value === true ? '开启' : '关闭'}</span>
                </label>
              ) : field.kind === 'multiline' || field.kind === 'list' ? (
                <textarea
                  id={`plugin-${pluginId}-${field.key}`}
                  className="textarea"
                  rows={field.kind === 'list' ? 3 : 4}
                  value={displayValue(value)}
                  placeholder={field.kind === 'list' ? '每行一个；也可用逗号分隔' : undefined}
                  onChange={(event) => setValues({ ...values, [field.key]: event.target.value })}
                />
              ) : field.kind === 'select' ? (
                <select
                  id={`plugin-${pluginId}-${field.key}`}
                  className="select"
                  value={displayValue(value)}
                  onChange={(event) => setValues({ ...values, [field.key]: event.target.value })}
                >
                  {(field.options ?? []).map((option) => (
                    <option key={option} value={option}>{option}</option>
                  ))}
                </select>
              ) : (
                <input
                  id={`plugin-${pluginId}-${field.key}`}
                  className="input"
                  type={field.kind === 'number' ? 'number' : field.kind === 'password' ? 'password' : 'text'}
                  value={displayValue(value)}
                  onChange={(event) =>
                    setValues({
                      ...values,
                      [field.key]: field.kind === 'number' ? Number(event.target.value) : event.target.value,
                    })
                  }
                />
              )}
              {field.description ? <div className="field-hint">{field.description}</div> : null}
            </div>
          )
        })
      )}

      <div className="settings-actions">
        <button type="button" className="btn btn-primary" disabled={busy} onClick={() => void save()}>
          {busy ? '保存中…' : '保存配置'}
        </button>
      </div>
    </div>
  )
}