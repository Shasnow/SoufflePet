// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { Activity, Gauge } from 'lucide-react'
import type { GlobalUsageDto, UsageDto } from '@/bridge/contracts'
import { Popover } from './Popover'
import { formatCompactTokens, formatDuration, formatPercent, formatSeconds, formatTokens, formatTps } from './format'

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

/** 会话统计：底部胶囊展示轮数/步数/速度，点开显示模型与工具耗时、TTFT、TPS。 */
export function SessionStatsButton({ usage }: { usage: UsageDto }) {
  const rounds = usage.rounds ?? 0
  const steps = usage.steps ?? 0
  const tpsLabel = formatTps(Math.max(0, usage.tokensPerSecond ?? 0))

  return (
    <Popover
      side="top"
      align="start"
      trigger={
        <button type="button" className="session-stats-btn" aria-label="会话统计">
          <Activity size={11} />
          {`${rounds} 轮 ${steps} 步 · ${tpsLabel}`}
        </button>
      }
    >
      <div className="usage-title usage-title-split">
        <span>会话统计</span>
        <span className="meta">{`${rounds} 轮 ${steps} 步`}</span>
      </div>
      <UsageRow label="模型用时" value={formatSeconds(usage.modelSeconds ?? 0)} />
      <UsageRow label="工具调用用时" value={formatSeconds(usage.toolSeconds ?? 0)} />
      <UsageRow label="首 token 平均（TTFT）" value={formatSeconds(usage.averageFirstTokenSeconds ?? 0)} />
      <UsageRow label="输出速度（TPS）" value={tpsLabel} />
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