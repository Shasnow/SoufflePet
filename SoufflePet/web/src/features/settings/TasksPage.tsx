// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { navigate, TASKS_TABS, tasksRoute, type TasksTab } from '@/app/router'
import { PageTabs } from '@/ui/PageTabs'
import { TasksSettings } from './TasksSettings'

/** 自动化任务的独立页面（不再作为设置子页），用页内标签区分任务配置与执行记录。 */
export function TasksPage({ tab }: { tab: TasksTab }) {
  return (
    <div className="settings-content">
      <div className="settings-content-inner">
        <div className="settings-head">
          <div className="settings-title">自动化</div>
          <div className="settings-desc">
            配置定时任务，到点后由桌宠按任务内容发起一轮对话，并在执行历史里留下结果。
          </div>
        </div>

        <PageTabs
          label="自动化视图"
          tabs={TASKS_TABS}
          value={tab}
          onChange={(next) => navigate(tasksRoute(next))}
        >
          {/* 始终复用同一个 TasksSettings，切换标签时保留正在编辑的任务草稿 */}
          <TasksSettings view={tab} />
        </PageTabs>
      </div>
    </div>
  )
}
