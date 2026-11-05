// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useEffect, useState } from 'react'
import { invoke } from '@/bridge/client'
import type { ChatSnapshotDto } from '@/bridge/contracts'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useSessionStore } from '@/stores/sessionStore'
import { useToastStore } from '@/stores/toastStore'
import { GlobalUsageButton } from '@/ui/Usage'
import { chatRoute, navigate } from '@/app/router'
import { Composer } from './Composer'
import { MessageList } from './MessageList'
import { WelcomeHero } from './WelcomeHero'

export function ChatPage({ sessionId }: { sessionId?: string }) {
  const activeId = useSessionStore((state) => state.activeId)
  const setActive = useSessionStore((state) => state.setActive)
  const setChatActive = useChatStore((state) => state.setActive)
  const upsertSnapshot = useChatStore((state) => state.upsertSnapshot)
  const usage = useAppStore((state) => state.usage)
  const pushToast = useToastStore((state) => state.push)
  const [loading, setLoading] = useState(false)
  const [missing, setMissing] = useState(false)

  const targetId = sessionId ?? activeId ?? null
  const chat = useChatStore((state) =>
    targetId ? state.bySession[targetId] ?? null : null,
  )

  useEffect(() => {
    if (!targetId) return
    setActive(targetId)
    setChatActive(targetId)
    if (chat?.sessionId === targetId) return

    let alive = true
    setLoading(true)
    setMissing(false)
    invoke<ChatSnapshotDto>('session.open', { sessionId: targetId })
      .then((snapshot) => {
        if (!alive) return
        upsertSnapshot(snapshot)
        setLoading(false)
        if (!sessionId) navigate(chatRoute(targetId), true)
      })
      .catch((error) => {
        if (!alive) return
        setLoading(false)
        setMissing(true)
        pushToast({ level: 'error', text: `打开会话失败：${error instanceof Error ? error.message : String(error)}` })
      })
    return () => {
      alive = false
    }
  }, [targetId, sessionId, chat?.sessionId, setActive, setChatActive, upsertSnapshot, pushToast])

  if (!targetId) {
    return (
      <div className="error-screen">
        <div className="error-title">还没有会话</div>
        <div>在侧栏点「新会话」开始。</div>
      </div>
    )
  }

  if (!chat && loading) {
    return (
      <div className="chat-page" aria-busy="true">
        <div className="message-list" style={{ padding: 24 }}>
          <div style={{ maxWidth: 760, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 14 }}>
            <div className="skeleton" style={{ height: 48, width: '62%' }} />
            <div className="skeleton" style={{ height: 96, width: '86%' }} />
            <div className="skeleton" style={{ height: 64, width: '54%', alignSelf: 'flex-end' }} />
          </div>
        </div>
      </div>
    )
  }

  if (!chat) {
    return (
      <div className="error-screen">
        <div className="error-title">{missing ? '会话不存在' : '正在加载会话…'}</div>
        {missing ? (
          <button
            type="button"
            className="btn btn-sm"
            onClick={() => navigate(chatRoute(activeId ?? undefined), true)}
          >
            返回当前会话
          </button>
        ) : null}
      </div>
    )
  }

  const empty = chat.messages.length === 0

  return (
    <div className="chat-page">
      {empty ? (
        <WelcomeHero chat={chat} />
      ) : (
        <>
          <MessageList key={chat.sessionId} messages={chat.messages} generating={chat.generating} />
          <Composer chat={chat} />
          <div className="composer-foot">
            <GlobalUsageButton usage={usage} />
          </div>
        </>
      )}
    </div>
  )
}