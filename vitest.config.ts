import path from 'path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  resolve: {
    alias: {
      Utilities: path.resolve(__dirname, 'frontend/src/Utilities'),
      typings: path.resolve(__dirname, 'frontend/src/typings')
    }
  },
  test: {
    include: ['frontend/src/**/*.test.ts'],
    environment: 'node'
  }
});
