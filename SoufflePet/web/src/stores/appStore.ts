// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { create } from 'zustand'
import type { GlobalUsageDto, WorkspaceDto } from '@/bridge/contracts'

interface AppState {
  ready: boolean
  bridgeError: string | null
  pet: { name: string; initial: string }
  usage: GlobalUsageDto
  workspaces: WorkspaceDto[]
  setReady: (ready: boolean) => void
  setBridgeError: (message: string | null) => void
  setPet: (pet: { name: string; initial: string }) => void
  setUsage: (usage: GlobalUsageDto) => void
  setWorkspaces: (workspaces: WorkspaceDto[]) => void
}

const EMPTY_USAGE: GlobalUsageDto = {
  inputTokens: 0,
  outputTokens: 0,
  cachedInputTokens: 0,
  conversationCount: 0,
  pokeCount: 0,
  headPatCount: 0,
  runSeconds: 0,
}

export const useAppStore = create<AppState>()((set) => ({
  ready: false,
  bridgeError: null,
  pet: { name: '舒芙蕾', initial: '舒' },
  usage: EMPTY_USAGE,
  workspaces: [],
  setReady: (ready) => set({ ready }),
  setBridgeError: (bridgeError) => set({ bridgeError }),
  setPet: (pet) => set({ pet }),
  setUsage: (usage) => set({ usage }),
  setWorkspaces: (workspaces) => set({ workspaces }),
}))