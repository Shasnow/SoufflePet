// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { useState } from 'react'
import { invoke } from '@/bridge/client'
import type { AskStateDto } from '@/bridge/contracts'
import { useChatStore } from '@/stores/chatStore'
import { Dialog } from '@/ui/Dialog'

export function AskPanel() {
  const ask = useChatStore((state) =>
    state.activeId ? state.bySession[state.activeId]?.ask ?? null : null,
  )
  if (!ask) return null
  return <AskDialog key={ask.requestId} ask={ask} />
}

function AskDialog({ ask }: { ask: AskStateDto }) {
  const [index, setIndex] = useState(0)
  const [answers, setAnswers] = useState<Record<number, string>>({})

  const questions = ask.questions
  const current = questions[index]
  const currentAnswer = answers[index]?.trim() ?? ''
  const answered = questions.every((_, questionIndex) => (answers[questionIndex] ?? '').trim().length > 0)
  const label = current ? `${index + 1} / ${questions.length}` : ''

  const submit = async () => {
    try {
      await invoke('chat.ask', {
        requestId: ask.requestId,
        action: 'submit',
        payload: {
          answers: questions.map((question, questionIndex) => ({
            question: question.text,
            answer: (answers[questionIndex] ?? '').trim(),
          })),
        },
      })
    } catch (error) {
      console.warn('submit ask failed', error)
    }
  }

  const cancel = async () => {
    try {
      await invoke('chat.ask', { requestId: ask.requestId, action: 'cancel' })
    } catch (error) {
      console.warn('cancel ask failed', error)
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) void cancel()
      }}
      title="需要你的确认"
      description="回答完所有问题后提交给桌宠；也可以直接取消这次提问。"
      className="ask-dialog"
    >
      {current ? (
        <>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginTop: 14 }}>
            <div style={{ color: 'var(--text-2)', fontSize: 12.5 }}>{label}</div>
            <div style={{ display: 'flex', gap: 4 }}>
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
                disabled={index >= questions.length - 1}
                onClick={() => setIndex((value) => Math.min(questions.length - 1, value + 1))}
              >
                下一题
              </button>
            </div>
          </div>

          <div style={{ marginTop: 10, fontWeight: 600, color: 'var(--text-1)' }}>{current.text}</div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 10 }}>
            {current.options.map((option) => (
              <button
                key={option.text}
                type="button"
                className="menu-item"
                data-active={answers[index] === option.text ? 'true' : 'false'}
                onClick={() =>
                  setAnswers((previous) => {
                    const next = { ...previous, [index]: option.text }
                    if (index < questions.length - 1) setIndex(index + 1)
                    return next
                  })
                }
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

          <div className="field" style={{ marginTop: 12 }}>
            <label className="field-label" htmlFor="ask-custom">
              或者自由输入
            </label>
            <textarea
              id="ask-custom"
              className="textarea"
              rows={2}
              value={currentAnswer}
              placeholder="写下你的答案…"
              onChange={(event) => setAnswers((previous) => ({ ...previous, [index]: event.target.value }))}
            />
          </div>
        </>
      ) : null}

      <div className="dialog-actions">
        <button type="button" className="btn btn-ghost" onClick={() => void cancel()}>
          取消
        </button>
        <button type="button" className="btn btn-primary" disabled={!answered} onClick={() => void submit()}>
          提交
        </button>
      </div>
    </Dialog>
  )
}