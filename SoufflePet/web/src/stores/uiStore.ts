// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'

interface UiState {
  sidebarCollapsed: boolean
  sidebarWidth: number
  drawerOpen: boolean
  maximized: boolean
  visible: boolean
  toggleSidebar: () => void
  toggleDrawer: () => void
  setDrawerOpen: (open: boolean) => void
  setSidebarCollapsed: (collapsed: boolean) => void
  setSidebarWidth: (width: number) => void
  setWindowState: (state: { visible?: boolean; maximized?: boolean }) => void
}

const SIDEBAR_KEY = 'soufflepet.sidebarCollapsed'
const SIDEBAR_WIDTH_KEY = 'soufflepet.sidebarWidth'

export const SIDEBAR_MIN_W = 220
export const SIDEBAR_MAX_W = 480

export function clampSidebarWidth(width: number): number {
  return Math.round(Math.min(SIDEBAR_MAX_W, Math.max(SIDEBAR_MIN_W, width)))
}

function readSidebarWidth(): number {
  try {
    const raw = Number(window.localStorage.getItem(SIDEBAR_WIDTH_KEY))
    return Number.isFinite(raw) && raw > 0 ? clampSidebarWidth(raw) : 280
  } catch {
    return 280
  }
}

function persistSidebarWidth(width: number): void {
  try {
    window.localStorage.setItem(SIDEBAR_WIDTH_KEY, String(width))
  } catch {
    // localStorage 不可用时只影响偏好记忆
  }
}

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
  sidebarWidth: readSidebarWidth(),
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
  setSidebarWidth: (width) => {
    const clamped = clampSidebarWidth(width)
    set({ sidebarWidth: clamped })
    persistSidebarWidth(clamped)
  },
  setWindowState: (state) =>
    set((current) => ({
      visible: state.visible ?? current.visible,
      maximized: state.maximized ?? current.maximized,
    })),
}))