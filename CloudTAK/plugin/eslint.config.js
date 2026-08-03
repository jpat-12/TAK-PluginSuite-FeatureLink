// Flat ESLint config. Before this file existed `npm run lint` could not execute at all
// ("ESLint couldn't find an eslint.config file") while five ESLint packages were installed to
// support it — Appendix B §0.1.
import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import pluginVue from 'eslint-plugin-vue';
import globals from 'globals';

export default tseslint.config(
    {
        ignores: ['node_modules/**', 'dist/**', 'types/**'],
    },
    js.configs.recommended,
    ...tseslint.configs.recommended,
    ...pluginVue.configs['flat/recommended'],
    {
        files: ['**/*.ts', '**/*.vue'],
        languageOptions: {
            globals: { ...globals.browser },
            parserOptions: {
                parser: tseslint.parser,
                ecmaVersion: 2022,
                sourceType: 'module',
                extraFileExtensions: ['.vue'],
            },
        },
        rules: {
            // The plugin's Vue components use single-quoted attributes throughout and multi-word
            // component names are not a CloudTAK convention — these two would be pure churn.
            'vue/multi-word-component-names': 'off',
            'vue/html-quotes': ['error', 'single'],
            'vue/max-attributes-per-line': 'off',
            'vue/singleline-html-element-content-newline': 'off',
            'vue/html-indent': ['error', 4],
            'vue/attributes-order': 'off',
            '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
            // An empty catch is exactly the defect class C-32 covers; make it an error so a
            // regression cannot land silently.
            'no-empty': ['error', { allowEmptyCatch: false }],
        },
    },
    {
        files: ['test/**/*.ts'],
        languageOptions: { globals: { ...globals.node } },
        rules: {
            '@typescript-eslint/no-explicit-any': 'off',
            '@typescript-eslint/no-unsafe-function-type': 'off',
        },
    },
);
