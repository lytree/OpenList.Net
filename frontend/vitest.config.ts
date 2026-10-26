import path from "node:path"
import { defineConfig } from "vitest/config"

export default defineConfig({
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
