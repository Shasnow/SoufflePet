// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, render } from '@testing-library/react'
import type { ChatMessageDto } from '@/bridge/contracts'
import { MessageList } from './MessageList'

vi.mock('@/bridge/client', () => ({ invoke: vi.fn(async () => null) }))

const scrollToMock = vi.fn()

function message(id: string, text = '内容'): ChatMessageDto {
  return { id, kind: 'user', text, time: '' }
}

// jsdom 没有布局，手动给滚动容器一个可控几何；scrollTop 按浏览器语义 clamp。
function stubGeometry(element: HTMLElement) {
  let height = 1000
  const viewport = 400
  let top = 0
  const clamp = (value: number) => Math.max(0, Math.min(value, height - viewport))
  Object.defineProperty(element, 'scrollHeight', { configurable: true, get: () => height })
  Object.defineProperty(element, 'clientHeight', { configurable: true, get: () => viewport })
  Object.defineProperty(element, 'scrollTop', {
    configurable: true,
    get: () => top,
    set: (value: number) => { top = clamp(value) },
  })
  return {
    getTop: () => top,
    setTop: (value: number) => { top = clamp(value) },
    setHeight: (value: number) => { height = value },
  }
}

function renderList(messages: ChatMessageDto[]) {
  const result = render(<MessageList sessionId="s1" messages={messages} generating={false} />)
  const list = result.container.querySelector('.message-list') as HTMLElement
  return { ...result, list }
}

function jumpButton(): HTMLElement | null {
  return document.querySelector('.scroll-jump')
}

beforeEach(() => {
  scrollToMock.mockClear()
  // jsdom 未实现 scrollTo；这里只记录调用、不移动 scrollTop，模拟动画进行中。
  Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
    configurable: true,
    value: scrollToMock,
  })
  // 让各 effect 里的 rAF 同步执行，测试可以全同步断言。
  window.requestAnimationFrame = ((cb: FrameRequestCallback) => {
    cb(0)
    return 1
  }) as typeof window.requestAnimationFrame
  window.cancelAnimationFrame = () => undefined
})

describe('MessageList 回到最新', () => {
  it('平滑滚动的中间态不会把按钮闪回来', () => {
    const { list } = renderList([message('m1'), message('m2')])
    const geo = stubGeometry(list)

    geo.setTop(200)
    fireEvent.scroll(list)
    expect(jumpButton()).not.toBeNull()

    fireEvent.click(jumpButton()!)
    expect(scrollToMock).toHaveBeenCalledWith({ top: 1000, behavior: 'smooth' })
    expect(jumpButton()).toBeNull()

    // 动画途中离底仍很远，但不能据此把 atBottom 判回 false。
    fireEvent.scroll(list)
    expect(jumpButton()).toBeNull()

    // 到底后结束锁定，按钮保持隐藏。
    geo.setTop(600)
    fireEvent.scroll(list)
    expect(jumpButton()).toBeNull()
  })

  it('平滑滚动期间的流式更新不会用瞬时赋值掐断动画', () => {
    const messages = [message('m1'), message('m2')]
    const { list, rerender } = renderList(messages)
    const geo = stubGeometry(list)

    geo.setTop(200)
    fireEvent.scroll(list)
    fireEvent.click(jumpButton()!)

    // 动画继续增长内容并追加消息：pinToBottom 必须让路。
    geo.setHeight(1400)
    rerender(
      <MessageList sessionId="s1" messages={[...messages, message('m3')]} generating />,
    )
    expect(geo.getTop()).toBe(200)
    // 贴底交给 pinToBottom（瞬时赋值 scrollTop），锁定期必须让路；
    // virtualizer 自身也会调 scrollTo 做测量锚定，所以只数我们发起的 smooth 调用。
    const smoothCalls = scrollToMock.mock.calls.filter(
      (call) => (call[0] as ScrollToOptions)?.behavior === 'smooth',
    )
    expect(smoothCalls).toHaveLength(1)

    // 动画到达目标后补一次贴底，并解锁；此后用户手动滚上去按钮会重新出现。
    geo.setTop(1000)
    fireEvent.scroll(list)
    expect(geo.getTop()).toBe(1000)

    geo.setTop(100)
    fireEvent.scroll(list)
    expect(jumpButton()).not.toBeNull()
  })

  it('用户滚轮打断平滑滚动后按真实位置恢复判定', () => {
    const { list } = renderList([message('m1')])
    const geo = stubGeometry(list)

    geo.setTop(200)
    fireEvent.scroll(list)
    fireEvent.click(jumpButton()!)

    fireEvent.wheel(list)
    expect(jumpButton()).not.toBeNull()
    // 打断后新的消息更新不再被锁定，贴底跟随照常工作。
    geo.setTop(600)
    fireEvent.scroll(list)
    expect(jumpButton()).toBeNull()
  })
})
