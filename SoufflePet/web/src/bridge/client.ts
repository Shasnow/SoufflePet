// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import type { AppSnapshotDto, ChatMessageDto, ChatOp, ThemeDto } from './contracts'
import type { IncomingEnvelope, RequestEnvelope } from './protocol'
import { BridgeError, makeRequest, parseIncoming } from './protocol'
import { createMockHost, mockScenarioFromUrl } from './mock-host'
import { useAppStore } from '@/stores/appStore'
import { useChatStore } from '@/stores/chatStore'
import { useSessionStore } from '@/stores/sessionStore'
import { useThemeStore } from '@/stores/themeStore'
import { useToastStore } from '@/stores/toastStore'
import { useUiStore } from '@/stores/uiStore'
import { navigate, parseHash } from '@/app/router'

declare global {
  interface Window {
    invokeCSharpAction?: (payload: string) => void
    __bridge?: {
      receive?: (payload: unknown) => void
      [key: string]: unknown
    }
  }
}

interface Transport {
  readonly kind: 'native' | 'mock'
  send: (envelope: RequestEnvelope) => void
  attach: (receiver: (data: unknown) => void) => void
  dispose: () => void
}

class NativeTransport implements Transport {
  readonly kind = 'native' as const
  private receiver: ((data: unknown) => void) | null = null

  send(envelope: RequestEnvelope): void {
    if (typeof window.invokeCSharpAction !== 'function') {
      throw new BridgeError('bridge_unavailable', '宿主桥未注入 invokeCSharpAction')
    }
    window.invokeCSharpAction(JSON.stringify(envelope))
  }

  attach(receiver: (data: unknown) => void): void {
    this.receiver = receiver
    window.__bridge = {
      ...(window.__bridge ?? {}),
      receive: (payload: unknown) => this.receiver?.(payload),
    }
  }

  dispose(): void {
    if (window.__bridge) delete window.__bridge.receive
  }
}

class MockTransport implements Transport {
  readonly kind = 'mock' as const
  private receiver: ((data: unknown) => void) | null = null
  private readonly host = createMockHost({ scenario: mockScenarioFromUrl() })

  send(envelope: RequestEnvelope): void {
    // mock 与真实宿主一样走一次 JSON 序列化，避免 mock 内部对象与 store 共享引用
    // （否则流式增量既被原地 mutate 又被 op 追加，会重复一段文本）。
    this.host.send(envelope, (data) => this.receiver?.(JSON.parse(JSON.stringify(data))))
  }

  attach(receiver: (data: unknown) => void): void {
    this.receiver = receiver
  }

  dispose(): void {
    this.host.dispose()
  }
}

type EventHandler = (payload: unknown) => void

export class BridgeClient {
  private transport: Transport
  private readonly pending = new Map<
    number,
    {
      resolve: (value: unknown) => void
      reject: (error: Error) => void
      timer: number
    }
  >()
  private nextId = 1
  private lastRev = 0
  private resyncScheduled = false
  private readonly handlers = new Map<string, Set<EventHandler>>()
  private firstSnapshotDone = false
  private snapshotWaiter: ((ok: boolean) => void) | null = null
  private disposed = false

  readonly isMock: boolean

  constructor(transport: Transport) {
    this.transport = transport
    this.isMock = transport.kind === 'mock'
  }

  async start(): Promise<boolean> {
    this.transport.attach((data) => this.receive(data))

    const arrived = new Promise<boolean>((resolve) => {
      this.snapshotWaiter = resolve
      window.setTimeout(() => {
        if (this.snapshotWaiter === resolve) {
          this.snapshotWaiter = null
          resolve(false)
        }
      }, 10_000)
    })

    this.transport.send(
      makeRequest(this.nextId++, 'hello', {
        protocolVersion: 1,
        route: window.location.hash || undefined,
      }),
    )

    const ok = await arrived
    if (!ok) {
      useAppStore.getState().setBridgeError('宿主桥未响应 hello（10 秒超时）')
    }
    return ok
  }

  invoke<T>(method: string, params?: unknown, timeoutMs = 30_000): Promise<T> {
    if (this.disposed) {
      return Promise.reject(new BridgeError('bridge_unavailable', '桥已关闭'))
    }

    const id = this.nextId++
    return new Promise<T>((resolve, reject) => {
      const timer = window.setTimeout(() => {
        this.pending.delete(id)
        reject(new BridgeError('timeout', `调用 ${method} 超时（${timeoutMs} ms）`))
      }, timeoutMs)

      this.pending.set(id, {
        resolve: (value) => resolve(value as T),
        reject,
        timer,
      })

      try {
        this.transport.send(makeRequest(id, method, params))
      } catch (error) {
        window.clearTimeout(timer)
        this.pending.delete(id)
        reject(error instanceof Error ? error : new Error(String(error)))
      }
    })
  }

  on(name: string, handler: EventHandler): () => void {
    if (!this.handlers.has(name)) this.handlers.set(name, new Set())
    this.handlers.get(name)!.add(handler)
    return () => this.handlers.get(name)?.delete(handler)
  }

  private emit(name: string, payload: unknown): void {
    for (const handler of this.handlers.get(name) ?? []) {
      try {
        handler(payload)
      } catch (error) {
        console.error(`bridge handler ${name} failed`, error)
      }
    }
  }

  private receive(input: unknown): void {
    if (this.disposed) return
    let value = input
    if (typeof input === 'string') {
      try {
        value = JSON.parse(input)
      } catch {
        console.warn('bridge: invalid JSON from host')
        return
      }
    }

    const envelope = parseIncoming(value)
    if (!envelope) {
      console.warn('bridge: unknown envelope', value)
      return
    }

    if ('id' in envelope) {
      this.settle(envelope)
      return
    }

    if (envelope.type === 'snapshot') {
      this.applySnapshot(envelope.data, envelope.rev)
      return
    }

    if (envelope.type === 'snapshot-file') {
      void this.loadJson(envelope.url).then((data) => {
        if (data) this.applySnapshot(data as AppSnapshotDto, envelope.rev)
      })
      return
    }

    if (envelope.type === 'event-file') {
      void this.loadJson(envelope.url).then((data) => {
        if (data !== undefined) this.dispatchEvent(envelope.name, data, envelope.rev)
      })
      return
    }

    if (envelope.type === 'event') {
      this.dispatchEvent(
        envelope.name,
        envelope.payload,
        envelope.rev ?? this.payloadRev(envelope.payload),
      )
    }
  }

  private settle(envelope: IncomingEnvelope): void {
    if (!('id' in envelope)) return
    const pending = this.pending.get(envelope.id)
    if (!pending) return
    this.pending.delete(envelope.id)
    window.clearTimeout(pending.timer)

    if (envelope.ok) {
      pending.resolve(envelope.result)
    } else {
      pending.reject(
        new BridgeError(
          envelope.error.code as BridgeError['code'],
          envelope.error.message,
          envelope.error.details,
        ),
      )
    }
  }

  private payloadRev(payload: unknown): number | null {
    if (typeof payload !== 'object' || payload === null) return null
    const rev = (payload as Record<string, unknown>).rev
    return typeof rev === 'number' ? rev : null
  }

  private dispatchEvent(name: string, payload: unknown, rev: number | null): void {
    if (rev !== null) {
      if (rev <= this.lastRev) return
      if (rev > this.lastRev + 1) {
        this.scheduleResync()
      }
      this.lastRev = rev
    }

    switch (name) {
      case 'chat.ops': {
        const data = payload as { sessionId?: string; ops?: ChatOp[] }
        if (data.sessionId && Array.isArray(data.ops)) {
          useChatStore.getState().applyOps(data.sessionId, data.ops)
        }
        break
      }
      case 'session.updated': {
        const data = payload as {
          sessions: Parameters<ReturnType<typeof useSessionStore.getState>['setUpdated']>[0]
          activeId: string | null
        }
        useSessionStore.getState().setUpdated(data.sessions, data.activeId)
        break
      }
      case 'host.theme': {
        useThemeStore.getState().setTheme(payload as ThemeDto)
        break
      }
      case 'window.state': {
        const data = payload as { visible?: boolean; maximized?: boolean }
        useUiStore.getState().setWindowState(data)
        break
      }
      case 'toast': {
        useToastStore.getState().push(payload as { level: 'info' | 'success' | 'error'; text: string })
        break
      }
      case 'nav.goto': {
        const route = (payload as { route?: string }).route
        if (route) navigate(parseHash(route))
        break
      }
      case 'host.log': {
        const data = payload as { level?: string; msg?: string }
        console[data.level === 'error' ? 'error' : 'log'](`[host] ${data.msg ?? ''}`)
        break
      }
      default:
        break
    }

    this.emit(name, payload)
  }

  private applySnapshot(data: AppSnapshotDto, rev: number): void {
    this.lastRev = Math.max(this.lastRev, rev)

    const app = useAppStore.getState()
    app.setPet(data.pet)
    app.setUsage(data.usage)
    app.setReady(true)
    app.setBridgeError(null)

    useSessionStore.getState().setSnapshot(data.sessions, data.activeSessionId)
    useThemeStore.getState().setTheme(data.theme)

    const chat = useChatStore.getState()
    chat.setActive(data.activeSessionId)
    chat.setSnapshot(data.chat, data.activeSessionId)

    if (!this.firstSnapshotDone) {
      this.firstSnapshotDone = true
      const current = parseHash(window.location.hash)
      if (current.kind === 'chat' && !current.sessionId && data.activeSessionId) {
        navigate({ kind: 'chat', sessionId: data.activeSessionId }, true)
      } else if (!window.location.hash && data.route) {
        navigate(parseHash(data.route), true)
      }
    }

    const waiter = this.snapshotWaiter
    if (waiter) {
      this.snapshotWaiter = null
      waiter(true)
    }
  }

  private scheduleResync(): void {
    if (this.resyncScheduled) return
    this.resyncScheduled = true
    window.setTimeout(() => {
      this.resyncScheduled = false
      this.transport.send(makeRequest(this.nextId++, 'app.resync', {}))
    }, 120)
  }

  private async loadJson(url: string): Promise<unknown> {
    try {
      const response = await fetch(url, { cache: 'no-store' })
      if (!response.ok) return undefined
      return await response.json()
    } catch (error) {
      console.warn('bridge: load state file failed', error)
      return undefined
    }
  }

  dispose(): void {
    this.disposed = true
    this.transport.dispose()
    for (const pending of this.pending.values()) {
      window.clearTimeout(pending.timer)
      pending.reject(new BridgeError('bridge_unavailable', '桥已关闭'))
    }
    this.pending.clear()
  }
}

let bridge: BridgeClient | null = null
let starting: Promise<BridgeClient | null> | null = null

export function getBridge(): BridgeClient | null {
  return bridge
}

export function invoke<T>(method: string, params?: unknown, timeoutMs?: number): Promise<T> {
  if (!bridge) {
    return Promise.reject(new BridgeError('bridge_unavailable', '宿主桥尚未连接'))
  }
  return bridge.invoke<T>(method, params, timeoutMs)
}

export async function startBridge(): Promise<BridgeClient | null> {
  if (bridge) return bridge
  if (starting) return starting

  starting = (async () => {
    const forceMock = new URLSearchParams(window.location.search).get('mock') === '1'
    const hasNative = typeof window.invokeCSharpAction === 'function'
    const useMock = forceMock || !hasNative

    if (useMock && !import.meta.env.DEV && !forceMock) {
      useAppStore.getState().setBridgeError('无法连接宿主桥（invokeCSharpAction 未注入）')
      return null
    }

    bridge = new BridgeClient(useMock ? new MockTransport() : new NativeTransport())
    const ok = await bridge.start()
    if (!ok && useMock) {
      useAppStore.getState().setBridgeError('mock 宿主没有返回快照')
    }
    return bridge
  })()

  try {
    return await starting
  } finally {
    starting = null
  }
}

/** 调试/测试用：替换桥（不对外导出到 window，避免生产误用）。 */
export function resetBridgeForTests(instance: BridgeClient | null): void {
  bridge = instance
}

export type { ChatMessageDto }