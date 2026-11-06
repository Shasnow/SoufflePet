// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useState } from 'react'
import { invoke } from '@/bridge/client'
import type { AskStateDto } from '@/bridge/contracts'

interface AskPanelProps {
  sessionId: string
  ask: AskStateDto
}

/**
 * 询问工具的内联卡片：只渲染在发起提问的会话里，不遮罩整页。
 * 切到其它会话时组件随对话区卸载，切回来再从该会话的 ask 状态恢复。
 */
export function AskPanel({ sessionId, ask }: AskPanelProps) {
  return <AskCard key={ask.requestId} sessionId={sessionId} ask={ask} />
}

function AskCard({ sessionId, ask }: AskPanelProps) {
  const [index, setIndex] = useState(0)
  // 选项与自由输入分开存：点选项不应把文字灌进下方输入框
  const [selected, setSelected] = useState<Record<number, string>>({})
  const [custom, setCustom] = useState<Record<number, string>>({})

  const questions = ask.questions
  const current = questions[index]
  const currentAnswer = custom[index] ?? ''
  const answerOf = (questionIndex: number) =>
    (custom[questionIndex] ?? '').trim() || selected[questionIndex] || ''
  const answered = questions.every((_, questionIndex) => answerOf(questionIndex).length > 0)
  const label = current ? `${index + 1} / ${questions.length}` : ''

  const clearAt = (source: Record<number, string>, key: number) => {
    if (!(key in source)) return source
    const next = { ...source }
    delete next[key]
    return next
  }

  const choose = (option: string) => {
    setSelected((previous) => ({ ...previous, [index]: option }))
    setCustom((previous) => clearAt(previous, index))
    if (index < questions.length - 1) setIndex(index + 1)
  }

  const typeAnswer = (value: string) => {
    setCustom((previous) => ({ ...previous, [index]: value }))
    setSelected((previous) => clearAt(previous, index))
  }

  const submit = async () => {
    try {
      await invoke('chat.ask', {
        sessionId,
        requestId: ask.requestId,
        action: 'submit',
        payload: {
          answers: questions.map((question, questionIndex) => ({
            question: question.text,
            answer: answerOf(questionIndex),
          })),
        },
      })
    } catch (error) {
      console.warn('submit ask failed', error)
    }
  }

  const cancel = async () => {
    try {
      await invoke('chat.ask', { sessionId, requestId: ask.requestId, action: 'cancel' })
    } catch (error) {
      console.warn('cancel ask failed', error)
    }
  }

  return (
    <div className="ask-card" role="group" aria-label="需要你的确认">
      <div className="ask-card-head">
        <span className="ask-card-title">需要你的确认</span>
        <span className="ask-card-step">{label}</span>
      </div>

      {current ? (
        <>
          <div className="ask-card-question">{current.text}</div>

          <div className="ask-card-options">
            {current.options.map((option) => (
              <button
                key={option.text}
                type="button"
                className="menu-item"
                data-active={selected[index] === option.text ? 'true' : 'false'}
                onClick={() => choose(option.text)}
                style={{ alignItems: 'flex-start' }}
              >
                <span style={{ display: 'flex', flexDirection: 'column', gap: 2, textAlign: 'left' }}>
                  <span>{option.text}</span>
                  {option.description ? (
                    <span style={{ color: 'var(--text-3)', fontSize: 11.5 }}>{option.description}</span>
                  ) : null}
                </span>
              </button>
            ))}
          </div>

          <textarea
            className="textarea"
            rows={2}
            value={currentAnswer}
            placeholder="或者自由输入答案…"
            aria-label="自由输入答案"
            onChange={(event) => typeAnswer(event.target.value)}
          />
        </>
      ) : null}

      <div className="ask-card-actions">
        <button
          type="button"
          className="btn btn-sm btn-ghost"
          disabled={index === 0}
          onClick={() => setIndex((value) => Math.max(0, value - 1))}
        >
          上一题
        </button>
        <button
          type="button"
          className="btn btn-sm btn-ghost"
          disabled={!questions.length || index >= questions.length - 1}
          onClick={() => setIndex((value) => Math.min(questions.length - 1, value + 1))}
        >
          下一题
        </button>
        <span className="spacer" />
        <button type="button" className="btn btn-sm btn-ghost" onClick={() => void cancel()}>
          取消
        </button>
        <button
          type="button"
          className="btn btn-sm btn-primary"
          disabled={!answered}
          onClick={() => void submit()}
        >
          提交
        </button>
      </div>
    </div>
  )
}
