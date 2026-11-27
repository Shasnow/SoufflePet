// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { afterEach, describe, expect, it, vi } from 'vitest'
import { BridgeClient } from './client'
import { BridgeError } from './protocol'

/** 只记录发送、从不回答的传输层：用来观察请求是否超时。 */
function silentTransport() {
  return {
    kind: 'native' as const,
    send: () => undefined,
    attach: () => undefined,
    dispose: () => undefined,
  }
}

describe('BridgeClient 的超时', () => {
  afterEach(() => {
    vi.useRealTimers()
  })

  it('默认 30 秒后按 timeout 拒绝，timeoutMs 传 0 则不设超时', async () => {
    vi.useFakeTimers()
    const bridge = new BridgeClient(silentTransport())

    const timedOut = vi.fn()
    const overridden = vi.fn()
    // 无超时的调用（/compact 这类长命令用的就是这个）：一直等宿主的回答
    void bridge
      .invoke('chat.command.execute', {}, 0)
      .catch(() => undefined)
      .then(overridden)
    void bridge
      .invoke('chat.send', {})
      .catch((error) => error)
      .then(timedOut)

    await vi.advanceTimersByTimeAsync(29_000)
    expect(timedOut).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(2_000)
    expect(timedOut).toHaveBeenCalledOnce()
    const error = timedOut.mock.calls[0][0]
    expect(error).toBeInstanceOf(BridgeError)
    expect((error as BridgeError).code).toBe('timeout')

    // 远超默认超时之后，无超时的调用依然在等（不是被静默 reject）
    await vi.advanceTimersByTimeAsync(10 * 60_000)
    expect(overridden).not.toHaveBeenCalled()

    // 但桥关闭时仍要立刻失败，避免悬挂的 Promise
    bridge.dispose()
    await vi.advanceTimersByTimeAsync(0)
    expect(overridden).toHaveBeenCalledOnce()
  })
})
