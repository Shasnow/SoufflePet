// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { ReactNode } from 'react'
import { useEffect } from 'react'
import { useUiStore } from '@/stores/uiStore'
import { useRoute } from './router'
import { Sidebar } from './Sidebar'
import { TopBar } from './TopBar'
import { WindowEdges } from './WindowEdges'

export function AppShell({ children }: { children: ReactNode }) {
  const collapsed = useUiStore((state) => state.sidebarCollapsed)
  const drawerOpen = useUiStore((state) => state.drawerOpen)
  const setDrawerOpen = useUiStore((state) => state.setDrawerOpen)
  const route = useRoute()
  const routeKey =
    route.kind === 'chat'
      ? `chat:${route.sessionId ?? ''}`
      : route.kind === 'settings'
        ? `settings:${route.panel}`
        : 'unknown'

  useEffect(() => {
    if (window.innerWidth >= 720) setDrawerOpen(false)
  }, [routeKey, setDrawerOpen])

  return (
    <div
      className="app-shell"
      data-sidebar-collapsed={collapsed ? 'true' : 'false'}
      data-drawer-open={drawerOpen ? 'true' : 'false'}
    >
      <TopBar />
      <div className="app-body">
        <Sidebar />
        <main className="app-main">{children}</main>
      </div>
      <div
        className="sidebar-scrim"
        role="presentation"
        onClick={() => setDrawerOpen(false)}
      />
      <WindowEdges />
    </div>
  )
}