import { defineConfig } from 'vitest/config';

// vitest (not jest) because the plugin is a Vue 3 + native-ESM + TypeScript codebase that is
// bundled by CloudTAK's Vite: vitest runs the same esbuild/Vite transform pipeline, so
// `.ts` import specifiers, `import.meta`, and `<script setup>` SFCs resolve exactly as they do
// in the real build. Jest would need ts-jest + a Vue transformer + ESM flags to reach parity with
// a toolchain the product does not otherwise use. (Appendix B §0.1 / C-14.)
export default defineConfig({
    test: {
        environment: 'happy-dom',
        include: ['test/**/*.test.ts'],
        restoreMocks: true,
    },
});
