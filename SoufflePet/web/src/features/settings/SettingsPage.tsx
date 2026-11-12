// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { SETTINGS_PANELS, navigate, type SettingsPanel } from '@/app/router'
import { AppearanceSettings } from './AppearanceSettings'
import { LlmSettings } from './LlmSettings'
import { ModelsSettings } from './ModelsSettings'
import { PersonaSettings } from './PersonaSettings'

const DESCRIPTIONS: Record<SettingsPanel, string> = {
  persona: '桌宠的称呼与对话人设：名称会同步到侧栏、窗口标题与输入提示。',
  llm: '按提供商配置接口与密钥，勾选要启用的模型。',
  models: '导入、切换或移除 Live2D 桌宠模型。',
  appearance: '强调色、预设与“跟随系统”由 OKLCH token 驱动。',
}

function PanelBody({ panel }: { panel: SettingsPanel }) {
  switch (panel) {
    case 'persona':
      return <PersonaSettings />
    case 'llm':
      return <LlmSettings />
    case 'models':
      return <ModelsSettings />
    case 'appearance':
      return <AppearanceSettings />
    default:
      return null
  }
}

export function SettingsPage({ panel }: { panel: SettingsPanel }) {
  const meta = SETTINGS_PANELS.find((item) => item.id === panel)

  return (
    <div className="settings-layout">
      <nav className="settings-nav" aria-label="设置分类">
        <div className="settings-nav-title">设置</div>
        {SETTINGS_PANELS.map((item) => (
          <button
            key={item.id}
            type="button"
            className="nav-item"
            data-active={item.id === panel ? 'true' : 'false'}
            onClick={() => navigate({ kind: 'settings', panel: item.id })}
          >
            <span className="nav-label">{item.label}</span>
          </button>
        ))}
      </nav>
      <div className="settings-content">
        <div className="settings-content-inner">
          <div className="settings-head">
            <div className="settings-title">{meta?.label ?? '设置'}</div>
            <div className="settings-desc">{DESCRIPTIONS[panel]}</div>
          </div>
          <PanelBody panel={panel} />
        </div>
      </div>
    </div>
  )
}
