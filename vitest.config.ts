import path from 'path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      Utilities: path.resolve(import.meta.dirname, 'frontend/src/Utilities'),
    },
  },
  test: {
    include: ['frontend/src/**/*.test.ts'],
    environment: 'node',
    setupFiles: ['vitest.setup.ts'],
  },
});


