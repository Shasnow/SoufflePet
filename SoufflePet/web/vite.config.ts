// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Shasnow

import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// 产物写入宿主 wwwroot/app，由 LocalFileServer 以 /app/ 前缀提供。
// base 必须是 /app/，否则 WebView 里用 /app/index.html 打开页面时资源路径会落到站点根。
export default defineConfig({
  base: '/app/',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  build: {
    outDir: '../wwwroot/app',
    emptyOutDir: true,
    sourcemap: false,
    chunkSizeWarningLimit: 1600,
    rollupOptions: {
      output: {
        manualChunks: {
          markdown: [
            'react-markdown',
            'remark-gfm',
            'rehype-katex',
            'rehype-sanitize',
            'katex',
          ],
        },
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    watch: {
      // 编辑工具在 Windows 上以「临时目录 + 原子替换」写文件，Vite 会误把 tmpdir 当源码监听并 EBUSY。
      ignored: ['**/*.tmpdir/**', '**/.tmp/**'],
    },
    proxy: {
      // --web-dev 时页面在 5173，宿主的本地文件/大状态 token 仍在 18923。
      '/local-file': 'http://127.0.0.1:18923',
      '/state': 'http://127.0.0.1:18923',
    },
  },
})