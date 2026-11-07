// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Gauge } from 'lucide-react'
import type { GlobalUsageDto, UsageDto } from '@/bridge/contracts'
import { Popover } from './Popover'
import { formatCompactTokens, formatDuration, formatPercent, formatTokens } from './format'

function hitRate(usage: UsageDto): number {
  return usage.inputTokens > 0 ? usage.cachedInputTokens / usage.inputTokens : 0
}

export function UsageCapsule({ usage }: { usage: UsageDto }) {
  const total = usage.inputTokens + usage.outputTokens
  return (
    <Popover
      side="top"
      align="start"
      trigger={
        <button type="button" className="usage-capsule" aria-label="本轮用量">
          <Gauge size={11} />
          用量 {formatCompactTokens(total)} tok
        </button>
      }
    >
      <div className="usage-title">本轮用量</div>
      {usage.model ? <UsageRow label="提供方 / 模型" value={usage.model} /> : null}
      <UsageRow label="总 tokens" value={formatTokens(total)} />
      <UsageRow label="缓存命中" value={formatPercent(hitRate(usage))} />
      <UsageRow label="未缓存输入" value={formatTokens(Math.max(0, usage.inputTokens - usage.cachedInputTokens))} />
      <UsageRow label="缓存读取" value={formatTokens(usage.cachedInputTokens)} />
      <UsageRow label="输出" value={formatTokens(usage.outputTokens)} />
    </Popover>
  )
}

export function UsageRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="usage-row">
      <span className="label">{label}</span>
      <span className="value">{value}</span>
    </div>
  )
}

export function GlobalUsageButton({ usage }: { usage: GlobalUsageDto }) {
  const total = usage.inputTokens + usage.outputTokens
  const rate = usage.inputTokens > 0 ? usage.cachedInputTokens / usage.inputTokens : 0

  return (
    <Popover
      side="top"
      align="start"
      trigger={
        <button type="button" className="usage-foot-btn" title={`${formatTokens(total)} · 缓存命中 ${formatPercent(rate)}`}>
          {`${formatCompactTokens(total)} tok · 缓存命中 ${formatPercent(rate)}`}
        </button>
      }
    >
      <div className="usage-title">累计用量与统计</div>
      <UsageRow label="输入" value={formatTokens(usage.inputTokens)} />
      <UsageRow label="缓存读取" value={formatTokens(usage.cachedInputTokens)} />
      <UsageRow label="输出" value={formatTokens(usage.outputTokens)} />
      <UsageRow label="缓存命中" value={formatPercent(rate)} />
      <div className="menu-sep" />
      <UsageRow label="对话次数" value={`${usage.conversationCount} 次`} />
      <UsageRow label="戳一戳" value={`${usage.pokeCount} 次`} />
      <UsageRow label="摸摸头" value={`${usage.headPatCount} 次`} />
      <UsageRow label="陪伴时长" value={formatDuration(usage.runSeconds)} />
    </Popover>
  )
}