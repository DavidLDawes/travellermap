import js from '@eslint/js';
import globals from 'globals';

export default [
  js.configs.recommended,
  {
    rules: {
      'no-unused-vars': 'off',
    },
  },
  {
    files: ['**/*.js'],
    ignores: ['sw.js'],
    languageOptions: {
      globals: {
        ...globals.browser,
        Handlebars: true,
      },
    },
  },
  {
    // Node scripts (test runners and tools)
    files: ['**/*.mjs'],
    languageOptions: {
      globals: {
        ...globals.node,
      },
    },
  },
  {
    // The Cloudflare Worker in front of Cloud Run (deploy/cloudflare)
    files: ['deploy/cloudflare/*.mjs'],
    languageOptions: {
      globals: {
        ...globals.serviceworker,
      },
    },
  },
  {
    files: ['sw.js'],
    languageOptions: {
      globals: {
        ...globals.serviceworker,
      },
    },
  },
];
