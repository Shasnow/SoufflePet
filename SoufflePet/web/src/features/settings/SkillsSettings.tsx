// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { FolderOpen, RefreshCw, Search } from 'lucide-react'
import { useCallback, useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { SkillDto } from '@/bridge/contracts'
import { useToastStore } from '@/stores/toastStore'
import { Dialog } from '@/ui/Dialog'
import { errorMessage, SettingsCard, SettingsEmpty, SettingsError, Toggle } from './SettingsShared'

interface SkillsListPayload {
  skills: SkillDto[]
  directories: string[]
}

export function SkillsSettings() {
  const pushToast = useToastStore((state) => state.push)
  const [data, setData] = useState<SkillsListPayload | null>(null)
  const [query, setQuery] = useState('')
  const [detail, setDetail] = useState<SkillDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await invoke<SkillsListPayload>('settings.skills.list'))
      setError(null)
    } catch (loadError) {
      setError(`读取技能失败：${errorMessage(loadError)}`)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const setEnabled = async (skill: SkillDto, enabled: boolean) => {
    try {
      const updated = await invoke<SkillDto>('settings.skills.setEnabled', { name: skill.name, enabled })
      setData((current) =>
        current
          ? { ...current, skills: current.skills.map((item) => (item.name === skill.name ? updated : item)) }
          : current,
      )
    } catch (toggleError) {
      setError(`切换技能失败：${errorMessage(toggleError)}`)
    }
  }

  const open = async (skill: SkillDto) => {
    try {
      const full = await invoke<SkillDto>('settings.skills.detail', { name: skill.name })
      setDetail(full)
    } catch (detailError) {
      setError(`读取技能失败：${errorMessage(detailError)}`)
    }
  }

  const openFolder = async (path: string) => {
    try {
      await invoke('settings.skills.openFolder', { path })
    } catch (folderError) {
      pushToast({ level: 'error', text: `打开目录失败：${errorMessage(folderError)}` })
    }
  }

  const needle = query.trim().toLowerCase()
  const skills = (data?.skills ?? []).filter(
    (skill) =>
      !needle ||
      skill.name.toLowerCase().includes(needle) ||
      skill.description.toLowerCase().includes(needle),
  )

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="技能目录"
        desc="这些目录下的 SKILL.md 会被扫描；启用状态保存在设置文件里。"
        actions={
          <button type="button" className="btn btn-sm btn-ghost" disabled={loading} onClick={() => void load()}>
            <RefreshCw size={13} /> 刷新
          </button>
        }
      >
        <div className="settings-directory-list">
          {data?.directories.map((directory) => (
            <div className="settings-directory" key={directory}>
              <span className="settings-directory-path" title={directory}>{directory}</span>
              <button
                type="button"
                className="icon-btn"
                aria-label={`打开目录 ${directory}`}
                onClick={() => void openFolder(directory)}
              >
                <FolderOpen size={14} />
              </button>
            </div>
          ))}
        </div>
      </SettingsCard>

      <SettingsCard title="技能" desc={`共 ${data?.skills.length ?? 0} 个；停用后不会进入模型提示词。`}>
        <div className="settings-inline">
          <div style={{ position: 'relative', flex: 1 }}>
            <Search
              size={13}
              style={{ position: 'absolute', left: 9, top: '50%', transform: 'translateY(-50%)', color: 'var(--text-3)' }}
            />
            <input
              className="input"
              style={{ paddingLeft: 28 }}
              placeholder="搜索技能…"
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              aria-label="搜索技能"
            />
          </div>
        </div>

        {loading ? (
          <SettingsEmpty>正在读取技能目录…</SettingsEmpty>
        ) : skills.length === 0 ? (
          <SettingsEmpty>{query ? '没有匹配的技能。' : '还没有发现技能；把 SKILL.md 放进上面的目录后刷新。'}</SettingsEmpty>
        ) : (
          <div className="settings-rows">
            {skills.map((skill) => (
              <div className="settings-row" key={skill.name}>
                <button type="button" className="settings-row-main" onClick={() => void open(skill)}>
                  <span className="settings-list-title">{skill.name}</span>
                  <span className="settings-list-desc">
                    {skill.description || '没有说明'} · {skill.source}
                  </span>
                </button>
                <Toggle
                  checked={skill.enabled}
                  onChange={(enabled) => void setEnabled(skill, enabled)}
                  label={skill.enabled ? '已启用' : '已停用'}
                />
              </div>
            ))}
          </div>
        )}
      </SettingsCard>

      <Dialog
        open={detail !== null}
        onOpenChange={(openDialog) => {
          if (!openDialog) setDetail(null)
        }}
        title={detail?.name ?? '技能详情'}
        description={detail?.filePath}
        className="settings-dialog"
      >
        <pre className="settings-skill-content">{detail?.content ?? ''}</pre>
        <div className="dialog-actions">
          <button
            type="button"
            className="btn btn-ghost"
            onClick={() => {
              if (!detail) return
              const directory = detail.filePath.replace(/[\\/][^\\/]+$/, '')
              void openFolder(directory || detail.filePath)
            }}
          >
            <FolderOpen size={13} /> 打开文件所在目录
          </button>
          <button type="button" className="btn btn-primary" onClick={() => setDetail(null)}>
            关闭
          </button>
        </div>
      </Dialog>
    </div>
  )
}