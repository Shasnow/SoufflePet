// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'

interface UiState {
  sidebarCollapsed: boolean
  drawerOpen: boolean
  maximized: boolean
  visible: boolean
  toggleSidebar: () => void
  toggleDrawer: () => void
  setDrawerOpen: (open: boolean) => void
  setSidebarCollapsed: (collapsed: boolean) => void
  setWindowState: (state: { visible?: boolean; maximized?: boolean }) => void
}

const SIDEBAR_KEY = 'soufflepet.sidebarCollapsed'

function readSidebarCollapsed(): boolean {
  try {
    return window.localStorage.getItem(SIDEBAR_KEY) === '1'
  } catch {
    return false
  }
}

function persistSidebarCollapsed(collapsed: boolean): void {
  try {
    window.localStorage.setItem(SIDEBAR_KEY, collapsed ? '1' : '0')
  } catch {
    // localStorage 在 WebView 里可用；失败只影响偏好记忆
  }
  // 宿主侧同步（防抖由 UiSidebar 组件负责；这里只发最终值，失败静默）
  void import('@/bridge/client')
    .then(({ getBridge }) => getBridge()?.invoke('settings.appearance.setSidebarCollapsed', { collapsed }))
    .catch(() => undefined)
}

export const useUiStore = create<UiState>()((set, get) => ({
  sidebarCollapsed: readSidebarCollapsed(),
  drawerOpen: false,
  maximized: false,
  visible: true,
  toggleSidebar: () => {
    if (window.innerWidth < 720) {
      set({ drawerOpen: !get().drawerOpen })
      return
    }
    const collapsed = !get().sidebarCollapsed
    set({ sidebarCollapsed: collapsed })
    persistSidebarCollapsed(collapsed)
  },
  toggleDrawer: () => set((state) => ({ drawerOpen: !state.drawerOpen })),
  setDrawerOpen: (drawerOpen) => set({ drawerOpen }),
  setSidebarCollapsed: (sidebarCollapsed) => {
    set({ sidebarCollapsed })
    persistSidebarCollapsed(sidebarCollapsed)
  },
  setWindowState: (state) =>
    set((current) => ({
      visible: state.visible ?? current.visible,
      maximized: state.maximized ?? current.maximized,
    })),
}))