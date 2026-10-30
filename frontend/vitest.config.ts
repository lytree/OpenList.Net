import path from "node:path"
import { defineConfig } from "vitest/config"
import vueJsx from "vue-jsx/vite"

export default defineConfig({
  plugins: [vueJsx({ vapor: false })],
  resolve: {
    alias: [{ find: /^~/, replacement: path.resolve(import.meta.dirname, "src") }],
  },
  test: {
    environment: "jsdom",
    include: ["src/**/*.spec.ts"],
    globals: true,
    restoreMocks: true,
  },
})
