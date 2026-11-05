// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { SETTINGS_PANELS, navigate, type SettingsPanel } from '@/app/router'
import { AppearanceSettings } from './AppearanceSettings'
import { LlmSettings } from './LlmSettings'
import { McpSettings } from './McpSettings'
import { ModelsSettings } from './ModelsSettings'
import { PluginsSettings } from './PluginsSettings'
import { SkillsSettings } from './SkillsSettings'
import { TasksSettings } from './TasksSettings'

const DESCRIPTIONS: Record<SettingsPanel, string> = {
  llm: '按提供商配置接口与密钥，勾选要启用的模型。',
  mcp: '配置 MCP 服务器，把它们的工具交给桌宠调用。',
  skills: '读取技能目录下的说明，按需交给桌宠使用。',
  plugins: '插件可向模型提供工具，或对回复做后处理。',
  tasks: '到点后由桌宠按任务内容发起一轮对话。',
  models: '导入、切换或移除 Live2D 桌宠模型。',
  appearance: '强调色、预设与“跟随系统”由 OKLCH token 驱动。',
}

function PanelBody({ panel }: { panel: SettingsPanel }) {
  switch (panel) {
    case 'llm':
      return <LlmSettings />
    case 'mcp':
      return <McpSettings />
    case 'skills':
      return <SkillsSettings />
    case 'plugins':
      return <PluginsSettings />
    case 'tasks':
      return <TasksSettings />
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