// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import './theme/boot'
import '@fontsource-variable/inter'
import './styles/tokens.css'
import './styles/base.css'

import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './app/App'
import { startBridge } from './bridge/client'
import { applyTheme } from './theme/apply'
import { useThemeStore } from './stores/themeStore'

applyTheme(useThemeStore.getState().theme)
void startBridge()

const container = document.getElementById('root')
if (!container) throw new Error('缺少 #root 挂载点')

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
)