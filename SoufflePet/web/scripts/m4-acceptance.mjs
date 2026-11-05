// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow
// M4 端到端验收：真实 WebView2 窗口里的迁移、会话 CRUD、流式回复、停止与持久化。

import { join } from 'node:path'
import { chromium } from 'playwright'

const outDir = join(process.cwd(), '.verifybuild', 'ui')
const browser = await chromium.connectOverCDP('http://127.0.0.1:9222')
const pages = browser.contexts().flatMap((context) => context.pages())
const page = pages.find((candidate) => candidate.url().includes('/app/index.html'))
if (!page) {
  console.error('未找到 SPA 页面；已打开：', pages.map((candidate) => candidate.url()))
  process.exit(1)
}

const errors = []
page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))
page.on('console', (message) => {
  if (message.type() === 'error') errors.push(`console: ${message.text()}`)
})

function assert(condition, message) {
  if (!condition) throw new Error(message)
}

await page.reload({ waitUntil: 'networkidle' })
await page.waitForSelector('.app-shell', { timeout: 20_000 })
await page.waitForTimeout(700)

const initial = await page.evaluate(() => ({
  hash: location.hash,
  sessions: [...document.querySelectorAll('.session-item .title')].map((node) => node.textContent?.trim()),
  welcome: Boolean(document.querySelector('.welcome')),
  model: document.querySelector('.composer-tool .tool-text')?.textContent?.trim() ?? '',
}))
console.log('initial:', initial)
assert(initial.sessions.includes('旧会话第一条：你好'), '迁移后的会话标题未出现在侧栏')
assert(initial.model.includes('假模型'), `模型选择未恢复：${initial.model}`)

// 大快照走 /state 分片；虚拟列表默认贴底，滚到顶部核对迁移内容
const initialState = await page.evaluate(() => ({
  stateRequests: performance.getEntriesByType('resource').map((entry) => entry.name).filter((name) => name.includes('/state')),
  scrollHeight: document.querySelector('.message-list')?.scrollHeight ?? 0,
}))
assert(initialState.stateRequests.length > 0, '超过 256KB 的快照没有走 /state 分片')
await page.evaluate(() => {
  const list = document.querySelector('.message-list')
  if (list) list.scrollTop = 0
})
await page.waitForTimeout(500)
const firstUser = await page.evaluate(
  () => document.querySelector('.bubble-user')?.textContent?.trim() ?? '',
)
assert(firstUser.includes('旧会话第一条'), `迁移后的首条用户消息未渲染：${firstUser}`)
assert(initialState.scrollHeight > 2000, '迁移会话的消息高度异常')
await page.evaluate(() => {
  const list = document.querySelector('.message-list')
  if (list) list.scrollTop = list.scrollHeight
})
await page.waitForTimeout(400)
await page.screenshot({ path: join(outDir, 'm4-migrated.png') })

// 新建会话：走侧栏真实交互；此时只是空白草稿，不应立刻出现在侧栏
await page.locator('.nav-new').click()
await page.waitForSelector('.welcome', { timeout: 10_000 })
await page.waitForTimeout(400)
assert(await page.locator('.session-item').count() === 1, '新会话在发出消息前就进了侧栏')
await page.screenshot({ path: join(outDir, 'm4-new-session.png') })

// 发送一条消息，验证真实流式回复
const input = page.locator('textarea[aria-label="输入消息"]')
await input.fill('请给我一条 M4 验收回复')
await input.press('Enter')
await page.waitForSelector('button[aria-label="停止生成"]', { timeout: 10_000 })
await page.waitForFunction(
  () => document.querySelector('.bubble-assistant')?.textContent?.includes('M4 端到端'),
  null,
  { timeout: 20_000 },
)
await page.waitForTimeout(2500)
await page.waitForSelector('button[aria-label="发送"]', { timeout: 20_000 })

const afterSend = await page.evaluate(() => ({
  hash: location.hash,
  draft: document.querySelector('textarea[aria-label="输入消息"]')?.value ?? '',
  assistant: [...document.querySelectorAll('.bubble-assistant')].map((node) => node.textContent ?? ''),
  titles: [...document.querySelectorAll('.session-item .title')].map((node) => node.textContent?.trim()),
  usage: document.querySelector('.usage-capsule')?.textContent?.trim() ?? '',
}))
console.log('after send:', { hash: afterSend.hash, draft: afterSend.draft, titles: afterSend.titles, usage: afterSend.usage })
assert(afterSend.draft === '', '发送成功后输入框没有清空')
assert(afterSend.assistant.some((text) => text.includes('M4 端到端')), '流式回复没有渲染到助手气泡')
assert(afterSend.titles.includes('请给我一条 M4 验收回复'), '首条消息没有生成会话标题')
await page.screenshot({ path: join(outDir, 'm4-stream-done.png') })

// 切到迁移会话，验证跨会话切换
const migratedItem = page.locator('.session-item', { hasText: '旧会话第一条' })
await migratedItem.click()
await page.waitForTimeout(700)
const switched = await page.evaluate(() => ({
  hash: location.hash,
  sessions: document.querySelectorAll('.session-item').length,
  messages: document.querySelectorAll('.message-row').length,
}))
console.log('switched:', switched)
assert(switched.hash.includes('#/chat/'), '切换会话后路由未更新')
assert(switched.sessions === 2, `侧栏会话数不对：${switched.sessions}`)
assert(switched.messages > 5, `迁移会话没有渲染：${switched.messages}`)
await page.evaluate(() => {
  const list = document.querySelector('.message-list')
  if (list) list.scrollTop = 0
})
await page.waitForTimeout(400)
const switchedFirstUser = await page.evaluate(
  () => document.querySelector('.bubble-user')?.textContent?.trim() ?? '',
)
assert(switchedFirstUser.includes('旧会话第一条'), '切回迁移会话后首条消息丢失')

// 非活动会话删除：移入回收站并从侧栏消失
const newItem = page.locator('.session-item', { hasText: '请给我一条 M4 验收回复' })
await newItem.locator('.session-more').click()
await page.getByRole('menuitem', { name: '删除会话' }).click()
await page.getByRole('button', { name: '删除' }).click()
await page.waitForTimeout(700)
const afterDelete = await page.evaluate(() => ({
  sessions: [...document.querySelectorAll('.session-item .title')].map((node) => node.textContent?.trim()),
  hash: location.hash,
}))
console.log('after delete:', afterDelete)
assert(afterDelete.sessions.length === 1, '删除会话后侧栏数量不对')
assert(!afterDelete.sessions.includes('请给我一条 M4 验收回复'), '删除的会话仍在侧栏')

// 停止生成：发一条长回复，中途点停止
await input.fill('这条消息用来验证停止生成')
await input.press('Enter')
await page.waitForSelector('button[aria-label="停止生成"]', { timeout: 10_000 })
await page.waitForTimeout(500)
await page.locator('button[aria-label="停止生成"]').click()
await page.waitForSelector('button[aria-label="发送"]', { timeout: 10_000 })
await page.waitForTimeout(300)
const stopped = await page.evaluate(() => ({
  messages: document.querySelectorAll('.message-row').length,
  last: [...document.querySelectorAll('.bubble-assistant')].at(-1)?.textContent ?? '',
}))
console.log('stopped:', stopped)
assert(stopped.last.trim().length > 0, '停止后已有回复被清空')
await page.screenshot({ path: join(outDir, 'm4-stopped.png') })

// 刷新后仍能从 conversation.jsonl 恢复
await page.reload({ waitUntil: 'networkidle' })
await page.waitForSelector('.app-shell', { timeout: 20_000 })
await page.waitForTimeout(900)
const reloaded = await page.evaluate(() => ({
  hash: location.hash,
  sessions: [...document.querySelectorAll('.session-item .title')].map((node) => node.textContent?.trim()),
  messages: document.querySelectorAll('.message-row').length,
  users: [...document.querySelectorAll('.bubble-user')].map((node) => node.textContent ?? ''),
  assistant: [...document.querySelectorAll('.bubble-assistant')].map((node) => node.textContent ?? ''),
}))
console.log('reloaded:', { hash: reloaded.hash, sessions: reloaded.sessions, messages: reloaded.messages })
assert(reloaded.sessions.length === 1, '刷新后会话数量变了')
assert(reloaded.messages > 5, `刷新后消息数不对：${reloaded.messages}`)
assert(reloaded.users.some((text) => text.includes('这条消息用来验证停止生成')), '刷新后用户消息没有恢复')
assert(reloaded.assistant.some((text) => text.includes('这是')), '刷新后停止前的部分回复没有恢复')
await page.screenshot({ path: join(outDir, 'm4-reloaded.png') })

console.log('page errors:', errors.length ? errors : 'none')
await browser.close()
if (errors.length) process.exitCode = 1