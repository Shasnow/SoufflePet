// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useEffect } from 'react'
import { useAppStore } from '@/stores/appStore'
import { useSessionStore } from '@/stores/sessionStore'
import { navigate, useRoute } from './router'
import { AppShell } from './AppShell'
import { ChatPage } from '@/features/chat/ChatPage'
import { ExtensionsPage } from '@/features/settings/ExtensionsPage'
import { SettingsPage } from '@/features/settings/SettingsPage'
import { TasksPage } from '@/features/settings/TasksPage'
import { ToastHost } from '@/ui/ToastHost'
import { ErrorBoundary } from '@/ui/ErrorBoundary'

function BootScreen() {
  return (
    <div className="boot-screen">
      <div style={{ textAlign: 'center' }}>
        <div className="boot-mark" />
        <div>正在连接 SoufflePet…</div>
      </div>
    </div>
  )
}

function BridgeErrorScreen({ message }: { message: string }) {
  return (
    <div className="error-screen" role="alert">
      <div className="error-title">无法连接桌宠宿主</div>
      <div>{message}</div>
      <div style={{ color: 'var(--text-3)', fontSize: 12 }}>
        请确认 WebView2 Runtime 已安装，并从托盘重新打开窗口。
      </div>
      <button className="btn btn-sm" type="button" onClick={() => window.location.reload()}>
        重试
      </button>
    </div>
  )
}

function RouteView() {
  const route = useRoute()

  if (route.kind === 'settings') return <SettingsPage panel={route.panel} />
  if (route.kind === 'extensions') return <ExtensionsPage tab={route.tab} />
  if (route.kind === 'tasks') return <TasksPage tab={route.tab} />
  if (route.kind === 'chat') return <ChatPage sessionId={route.sessionId} />
  return <ChatPage />
}

export function App() {
  const ready = useAppStore((state) => state.ready)
  const bridgeError = useAppStore((state) => state.bridgeError)
  const activeId = useSessionStore((state) => state.activeId)
  const route = useRoute()

  useEffect(() => {
    if (!ready) return
    if (route.kind === 'unknown') {
      navigate({ kind: 'chat', sessionId: activeId ?? undefined }, true)
      return
    }
    if (route.kind === 'chat' && !route.sessionId && activeId) {
      navigate({ kind: 'chat', sessionId: activeId }, true)
    }
  }, [ready, route, activeId])

  useEffect(() => {
    // 宿主 nav.goto 会直接改 hash；这里只负责初始空路由落到当前会话。
    if (!window.location.hash && ready) {
      navigate({ kind: 'chat', sessionId: activeId ?? undefined }, true)
    }
  }, [ready, activeId])

  if (bridgeError) return <BridgeErrorScreen message={bridgeError} />
  if (!ready) return <BootScreen />

  return (
    <ErrorBoundary>
      <AppShell>
        <RouteView />
      </AppShell>
      <ToastHost />
    </ErrorBoundary>
  )
}