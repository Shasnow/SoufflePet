// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'

/**
 * 命令面板的唤起请求：消息操作菜单等不在输入框里的入口，
 * 把「为哪个会话打开面板」放进这个 store，由当前会话的 Composer 消费并弹出面板。
 */
interface SlashPaletteRequest {
  sessionId: string
  /** 每次请求的序号：同一个会话连续点击也要重新打开面板。 */
  nonce: number
}

interface SlashPaletteState {
  request: SlashPaletteRequest | null
  requestPalette: (sessionId: string) => void
  clear: () => void
}

let nextNonce = 1

export const useSlashPaletteStore = create<SlashPaletteState>()((set) => ({
  request: null,
  requestPalette: (sessionId) => set({ request: { sessionId, nonce: nextNonce++ } }),
  clear: () => set({ request: null }),
}))
