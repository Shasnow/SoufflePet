// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Sparkles } from 'lucide-react'
import type { ChatSnapshotDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { Composer } from './Composer'

export function WelcomeHero({ chat }: { chat: ChatSnapshotDto }) {
  const pet = useAppStore((state) => state.pet)

  return (
    <div className="welcome">
      <div className="welcome-mark">
        <Sparkles size={24} />
      </div>
      <div className="welcome-title">SoufflePet</div>
      <div className="welcome-tagline">和{pet.name}说点什么</div>
      <div className="welcome-composer">
        <Composer chat={chat} hero />
      </div>
    </div>
  )
}