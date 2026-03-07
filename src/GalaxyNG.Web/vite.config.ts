import { defineConfig } from 'vite'

export default defineConfig({
  build: {
    outDir: '../../src/GalaxyNG.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5000',
      '/hubs': { target: 'ws://localhost:5000', ws: true },
    },
  },
})
