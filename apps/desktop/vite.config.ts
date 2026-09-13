import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({
  base: './',
  plugins: [react()],
  build: {
    rollupOptions: {
      output: {
        manualChunks: {
          mermaid: ['mermaid', 'cytoscape'],
          editor: ['react-markdown', 'remark-gfm', 'diff'],
          terminal: ['@xterm/xterm'],
        },
      },
    },
  },
});
