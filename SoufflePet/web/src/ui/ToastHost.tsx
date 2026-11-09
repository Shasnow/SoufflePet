// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { AlertCircle, CheckCircle2, Info, X } from 'lucide-react'
import { useToastStore } from '@/stores/toastStore'

export function ToastHost() {
  const toasts = useToastStore((state) => state.toasts)
  const dismiss = useToastStore((state) => state.dismiss)

  if (toasts.length === 0) return null

  return (
    <div className="toast-stack" role="status" aria-live="polite">
      {toasts.map((toast) => {
        const Icon = toast.level === 'error' ? AlertCircle : toast.level === 'success' ? CheckCircle2 : Info
        return (
          <div key={toast.id} className="toast" data-level={toast.level}>
            <Icon size={16} style={{ flex: 'none' }} />
            <span style={{ flex: 1 }}>{toast.text}</span>
            <button className="icon-btn" aria-label="关闭提示" onClick={() => dismiss(toast.id)}>
              <X size={14} />
            </button>
          </div>
        )
      })}
    </div>
  )
}