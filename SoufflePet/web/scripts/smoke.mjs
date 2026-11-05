// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

/*
 * M1 冒烟：500 条长会话、流式发送、设置路由、控制台错误。
 * 用法：pnpm --dir SoufflePet/web smoke
 */

import { chromium } from 'playwright'

const base = process.env.SOULLE_UI_URL ?? 'http://127.0.0.1:5173/app/'
const browser = await chromium.launch({ headless: true })
const page = await browser.newPage({ viewport: { width: 1416, height: 900 }, locale: 'zh-CN' })
const errors = []
page.on('pageerror', (error) => errors.push(`pageerror: ${error.message}`))
page.on('console', (message) => {
  if (message.type() === 'error') errors.push(`console: ${message.text()}`)
})

await page.goto(`${base}?mock=1&scenario=long#/chat/s-long`, { waitUntil: 'networkidle' })
await page.waitForSelector('.message-list', { timeout: 15_000 })
await page.waitForTimeout(400)

const rendered = await page.locator('.message-row').count()
const total = await page.evaluate(() => {
  const host = document.querySelector('.message-list-inner > div')
  return host ? host.getBoundingClientRect().height : 0
})
console.log(`long session: rendered=${rendered} messages, virtual height=${Math.round(total)}px`)
if (rendered === 0 || rendered > 80) {
  errors.push(`virtualizer rendered unexpected count: ${rendered}`)
}

// 滚到顶部再回到底部，验证不会出现空白/异常
await page.evaluate(() => {
  const list = document.querySelector('.message-list')
  if (list) list.scrollTop = 0
})
await page.waitForTimeout(300)
await page.evaluate(() => {
  const list = document.querySelector('.message-list')
  if (list) list.scrollTop = list.scrollHeight
})
await page.waitForTimeout(300)

// 发送一条消息，确认流式追加与停止按钮
const input = page.locator('textarea[aria-label="输入消息"]')
await input.fill('请用三句话总结这次重构的目标。')
await input.press('Enter')
await page.waitForTimeout(700)
const streamingText = await page.locator('.bubble-assistant').last().innerText()
console.log(`streaming sample: ${streamingText.replaceAll('\n', ' ').slice(0, 60)}`)
if (!streamingText.trim()) errors.push('streaming assistant bubble is empty')
const stop = page.locator('button[aria-label="停止生成"]')
if (await stop.count()) await stop.click()
await page.waitForTimeout(300)

// 设置路由
await page.goto(`${base}?mock=1#/settings/appearance`, { waitUntil: 'networkidle' })
await page.waitForSelector('.settings-layout', { timeout: 10_000 })
console.log(`settings title: ${await page.locator('.settings-title').innerText()}`)

await browser.close()

if (errors.length) {
  console.error('smoke errors:')
  for (const error of errors) console.error(`  ${error}`)
  process.exitCode = 1
} else {
  console.log('smoke: ok')
}