// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { EXTENSION_TABS, extensionsRoute, navigate, type ExtensionTab } from '@/app/router'
import { PageTabs } from '@/ui/PageTabs'
import { McpSettings } from './McpSettings'
import { PluginsSettings } from './PluginsSettings'
import { SkillsSettings } from './SkillsSettings'

/** 插件、技能、MCP 合并后的独立页面：用页内标签切换，不再拆成多个设置项。 */
export function ExtensionsPage({ tab }: { tab: ExtensionTab }) {
  return (
    <div className="settings-content">
      <div className="settings-content-inner">
        <div className="settings-head">
          <div className="settings-title">插件与扩展</div>
          <div className="settings-desc">
            统一管理插件、技能与 MCP 服务器：它们让桌宠获得额外工具、领域知识和外部服务。
          </div>
        </div>

        <PageTabs
          label="扩展类型"
          tabs={EXTENSION_TABS}
          value={tab}
          onChange={(next) => navigate(extensionsRoute(next))}
        >
          {tab === 'plugins' ? <PluginsSettings /> : null}
          {tab === 'skills' ? <SkillsSettings /> : null}
          {tab === 'mcp' ? <McpSettings /> : null}
        </PageTabs>
      </div>
    </div>
  )
}
