// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Blocks, FolderOpen, RefreshCw, Settings2, Upload } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { PluginDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { PluginConfigForm } from './PluginConfigForm'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError, Toggle } from './SettingsShared'

interface PluginsListPayload {
  plugins: PluginDto[]
  installDir: string
}

const STATE_LABELS: Record<string, string> = {
  enabled: '已加载',
  disabled: '已停用',
  loading: '加载中',
  discovered: '已发现',
  failed: '加载失败',
}

export function PluginsSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<PluginsListPayload | null>(null)
  const [configPlugin, setConfigPlugin] = useState<PluginDto | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [importing, setImporting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setData(await invoke<PluginsListPayload>('settings.plugins.list'))
      setError(null)
    } catch (loadError) {
      setError(`读取插件失败：${errorMessage(loadError)}`)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const setEnabled = async (plugin: PluginDto, enabled: boolean) => {
    setBusyId(plugin.id)
    try {
      const updated = await invoke<PluginDto>('settings.plugins.setEnabled', { id: plugin.id, enabled })
      setData((current) =>
        current
          ? { ...current, plugins: current.plugins.map((item) => (item.id === plugin.id ? updated : item)) }
          : current,
      )
    } catch (toggleError) {
      setError(`切换插件失败：${errorMessage(toggleError)}`)
    } finally {
      setBusyId(null)
    }
  }

  const importPlugin = async () => {
    setImporting(true)
    try {
      const result = await invoke<{ success: boolean; message: string; pluginId?: string }>(
        'settings.plugins.import',
      )
      pushToast({ level: result.success ? 'success' : 'info', text: result.message })
      await load()
      if (result.pluginId) {
        const plugin = data?.plugins.find((item) => item.id === result.pluginId)
        if (plugin?.hasConfig) setConfigPlugin(plugin)
      }
    } catch (importError) {
      setError(`导入插件失败：${errorMessage(importError)}`)
    } finally {
      setImporting(false)
    }
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="插件"
        desc="插件可以向模型提供工具，或对回复正文做后处理；启停与配置保存立即生效。"
        actions={
          <>
            <button type="button" className="btn btn-sm btn-ghost" onClick={() => void load()}>
              <RefreshCw size={13} /> 刷新
            </button>
            <button type="button" className="btn btn-sm btn-primary" disabled={importing} onClick={() => void importPlugin()}>
              <Upload size={13} /> {importing ? '导入中…' : '导入插件'}
            </button>
          </>
        }
      >
        <div className="settings-footnote">安装目录：{data?.installDir}</div>
        {!data ? (
          <SettingsEmpty>正在读取插件…</SettingsEmpty>
        ) : data.plugins.length === 0 ? (
          <SettingsEmpty>还没有发现插件；把插件目录放进安装目录后刷新。</SettingsEmpty>
        ) : (
          <div className="settings-rows">
            {data.plugins.map((plugin) => (
              <div className="settings-row" key={plugin.id}>
                <span className="settings-plugin-icon">
                  <Blocks size={16} />
                </span>
                <div className="settings-row-main" style={{ cursor: 'default' }}>
                  <span className="settings-list-title">
                    {plugin.name}
                    <span className="settings-badge">{plugin.builtIn ? '内置' : '用户'}</span>
                    <span className="settings-badge" data-state={plugin.state}>
                      {STATE_LABELS[plugin.state] ?? plugin.state}
                    </span>
                  </span>
                  <span className="settings-list-desc">
                    {plugin.description || '没有说明'}
                    {plugin.version ? ` · v${plugin.version}` : ''}
                    {plugin.toolNames.length > 0 ? ` · ${plugin.toolNames.length} 个工具` : ''}
                  </span>
                  {plugin.error ? <span className="settings-row-error">{plugin.error}</span> : null}
                </div>
                {plugin.hasConfig ? (
                  <button
                    type="button"
                    className="icon-btn"
                    aria-label={`配置插件 ${plugin.id}`}
                    title="插件配置"
                    onClick={() => setConfigPlugin(plugin)}
                  >
                    <Settings2 size={14} />
                  </button>
                ) : null}
                <button
                  type="button"
                  className="icon-btn"
                  aria-label={`打开插件目录 ${plugin.id}`}
                  title={plugin.directory}
                  onClick={async () => {
                    try {
                      await invoke('settings.skills.openFolder', { path: plugin.directory })
                    } catch (openError) {
                      pushToast({ level: 'error', text: `打开目录失败：${errorMessage(openError)}` })
                    }
                  }}
                >
                  <FolderOpen size={14} />
                </button>
                <Toggle
                  checked={plugin.state === 'enabled'}
                  disabled={busyId === plugin.id || plugin.state === 'loading'}
                  onChange={(enabled) => void setEnabled(plugin, enabled)}
                  label={plugin.state === 'enabled' ? '已启用' : '已停用'}
                />
              </div>
            ))}
          </div>
        )}
      </SettingsCard>

      <Dialog
        open={configPlugin !== null}
        onOpenChange={(open) => {
          if (!open) setConfigPlugin(null)
        }}
        title={`配置「${configPlugin?.name ?? ''}」`}
        description="字段来自插件清单的 configSchema；保存后写入 plugin.json 并重载插件。"
        className="settings-dialog"
      >
        {configPlugin ? (
          <PluginConfigForm
            pluginId={configPlugin.id}
            onSaved={() => {
              setConfigPlugin(null)
              void load()
              pushToast({ level: 'success', text: '插件配置已保存' })
            }}
          />
        ) : null}
      </Dialog>
    </div>
  )
}