import { defineConfig } from 'vite'

export default defineConfig({
  build: {
    outDir: '../../src/GalaxyNG.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 600,
    rollupOptions: {
      output: {
        manualChunks: {
          three: ['three'],
          signalr: ['@microsoft/signalr'],
        },
      },
    },
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5055',
      '/hubs': { target: 'ws://localhost:5055', ws: true },
    },
  },
})
