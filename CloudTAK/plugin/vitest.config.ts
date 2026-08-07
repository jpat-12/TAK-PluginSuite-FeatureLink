import { defineConfig } from 'vitest/config';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath } from 'node:url';

const hostStub = fileURLToPath(new URL('./test/stubs/cloudtakHost.ts', import.meta.url));

// vitest (not jest) because the plugin is a Vue 3 + native-ESM + TypeScript codebase that is
// bundled by CloudTAK's Vite: vitest runs the same esbuild/Vite transform pipeline, so
// `.ts` import specifiers, `import.meta`, and `<script setup>` SFCs resolve exactly as they do
// in the real build. Jest would need ts-jest + a Vue transformer + ESM flags to reach parity with
// a toolchain the product does not otherwise use. (Appendix B §0.1 / C-14.)
export default defineConfig({
    // Compiles `<script setup>` SFCs so components can be mounted under test — without it, which
    // logic lives in a .vue file and which lives in lib/ decides whether it is testable at all,
    // and row-level rules like "share/remove only on on-device rows" live in the template.
    plugins: [vue()],
    resolve: {
        alias: [
            // lib/cot.ts dynamically imports two modules that exist only inside a CloudTAK
            // checkout. Vite resolves dynamic-import specifiers statically, so they must be mapped
            // to a stub or the entire module graph fails to load under test. (That coupling is
            // itself the §6 finding: when CloudTAK moves either module, initCot() fails silently
            // and no marker ever reaches the map.)
            { find: '@tak-ps/node-cot/normalize_geojson', replacement: hostStub },
            { find: '../../../src/stores/map.ts', replacement: hostStub },
        ],
    },
    test: {
        environment: 'happy-dom',
        include: ['test/**/*.test.ts'],
        restoreMocks: true,
    },
});
