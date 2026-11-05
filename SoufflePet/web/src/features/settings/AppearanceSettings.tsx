// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Check, Palette, RotateCcw } from 'lucide-react'
import { useState } from 'react'
import { invoke } from '@/bridge/client'
import { useThemeStore } from '@/stores/themeStore'
import { ACCENT_PRESETS } from '@/theme/presets'
import { hexToOklch, harmonizeAccent, oklchToHex } from '@/theme/oklch'
import { previewAccent } from '@/theme/apply'
import { useToastStore } from '@/stores/toastStore'
import { errorMessage, SettingsCard, SettingsError, Toggle } from './SettingsShared'

export function AppearanceSettings() {
  const theme = useThemeStore((state) => state.theme)
  const setTheme = useThemeStore((state) => state.setTheme)
  const pushToast = useToastStore((state) => state.push)
  const [accent, setAccent] = useState(theme.accent)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const parsed = hexToOklch(accent) ?? { l: theme.accentL, c: theme.accentC, h: theme.accentH }
  const harmonized = harmonizeAccent(parsed)
  const activePreset = ACCENT_PRESETS.find((preset) => oklchToHex(preset.oklch) === accent)?.id

  const preview = (hex: string) => {
    setAccent(hex)
    const applied = previewAccent(hex)
    if (applied) setTheme(applied)
  }

  const apply = async () => {
    setBusy(true)
    setError(null)
    try {
      const updated = await invoke<typeof theme>('settings.appearance.setAccent', { accent })
      setTheme(updated)
      pushToast({ level: 'success', text: '强调色已保存' })
    } catch (applyError) {
      setError(`保存强调色失败：${errorMessage(applyError)}`)
    } finally {
      setBusy(false)
    }
  }

  const setFollow = async (enabled: boolean) => {
    setBusy(true)
    setError(null)
    try {
      const updated = await invoke<typeof theme>('settings.appearance.setFollowSystem', { enabled })
      setTheme(updated)
      setAccent(updated.accent)
    } catch (followError) {
      setError(`切换跟随系统失败：${errorMessage(followError)}`)
    } finally {
      setBusy(false)
    }
  }

  const reset = async () => {
    setBusy(true)
    setError(null)
    try {
      const updated = await invoke<typeof theme>('settings.appearance.reset')
      setTheme(updated)
      setAccent(updated.accent)
      pushToast({ level: 'success', text: '已恢复默认强调色' })
    } catch (resetError) {
      setError(`恢复默认失败：${errorMessage(resetError)}`)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="settings-stack">
      <SettingsError message={error} />

      <SettingsCard
        title="强调色"
        desc="只存一个强调色，其余表面、描边、悬停色由 OKLCH 派生；修改后即时生效。"
        actions={
          <>
            <button type="button" className="btn btn-sm btn-ghost" disabled={busy} onClick={() => void reset()}>
              <RotateCcw size={13} /> 恢复默认
            </button>
            <button type="button" className="btn btn-sm btn-primary" disabled={busy} onClick={() => void apply()}>
              {busy ? '保存中…' : '应用强调色'}
            </button>
          </>
        }
      >
        <div className="settings-presets">
          {ACCENT_PRESETS.map((preset) => {
            const hex = oklchToHex(preset.oklch)
            return (
              <button
                key={preset.id}
                type="button"
                className="settings-preset"
                data-active={(activePreset === preset.id).toString()}
                title={preset.name}
                aria-label={`使用${preset.name}强调色`}
                onClick={() => void (async () => {
                  preview(hex)
                  try {
                    const updated = await invoke<typeof theme>('settings.appearance.setAccent', { accent: hex })
                    setTheme(updated)
                  } catch (presetError) {
                    setError(`切换强调色失败：${errorMessage(presetError)}`)
                  }
                })()}
              >
                <span className="settings-swatch" style={{ background: hex }} />
                <span>{preset.name}</span>
                {activePreset === preset.id ? <Check size={12} /> : null}
              </button>
            )
          })}
        </div>

        <div className="settings-color-row">
          <label className="settings-color-label">
            <Palette size={15} />
            <span>自定义颜色</span>
            <input
              type="color"
              value={accent}
              aria-label="自定义强调色"
              onChange={(event) => preview(event.target.value)}
            />
          </label>
          <span className="settings-color-value">{accent.toUpperCase()}</span>
          <span className="settings-badge" data-state={harmonized.contrast >= 4.5 ? 'enabled' : 'failed'}>
            对比度 {harmonized.contrast.toFixed(1)}:1
          </span>
        </div>

        <div className="settings-sliders">
          <label className="settings-slider-row">
            <span>明度 L</span>
            <input
              type="range"
              min={0.2}
              max={0.95}
              step={0.005}
              value={parsed.l}
              onChange={(event) =>
                preview(oklchToHex({ l: Number(event.target.value), c: parsed.c, h: parsed.h }))
              }
            />
            <span className="settings-slider-value">{parsed.l.toFixed(3)}</span>
          </label>
          <label className="settings-slider-row">
            <span>彩度 C</span>
            <input
              type="range"
              min={0}
              max={0.37}
              step={0.002}
              value={parsed.c}
              onChange={(event) =>
                preview(oklchToHex({ l: parsed.l, c: Number(event.target.value), h: parsed.h }))
              }
            />
            <span className="settings-slider-value">{parsed.c.toFixed(3)}</span>
          </label>
          <label className="settings-slider-row">
            <span>色相 H</span>
            <input
              type="range"
              min={0}
              max={359}
              step={1}
              value={parsed.h}
              onChange={(event) =>
                preview(oklchToHex({ l: parsed.l, c: parsed.c, h: Number(event.target.value) }))
              }
            />
            <span className="settings-slider-value">{parsed.h.toFixed(0)}°</span>
          </label>
        </div>

        <div className="field-hint">
          {harmonized.gamutClamped ? '颜色超出 sRGB 色域，已自动夹回。' : '颜色在 sRGB 色域内。'}
          {harmonized.contrastAdjusted ? ' 为保证可读性，明度已微调。' : ''}
        </div>
      </SettingsCard>

      <SettingsCard title="跟随系统" desc="开启后使用 Windows 系统强调色，忽略上面的自定义颜色。">
        <Toggle
          checked={theme.followSystem}
          disabled={busy}
          onChange={(enabled) => void setFollow(enabled)}
          label="跟随系统强调色"
        />
        <div className="field-hint">
          当前使用：{theme.followSystem ? 'Windows 系统强调色' : '自定义强调色'}。
        </div>
      </SettingsCard>
    </div>
  )
}