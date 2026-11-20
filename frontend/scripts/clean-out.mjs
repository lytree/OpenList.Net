import { existsSync, readdirSync, rmSync } from "node:fs"
import { join } from "node:path"

/**
 * 清掉上一次构建的产物。
 *
 * vite 的 outDir 就是 `frontend/` 根（产物与源码同级），`emptyOutDir` 必然是 false
 * ——打开它会把 src/、node_modules/ 一起清掉。所以「清空」只能按清单做：
 *
 *   index.html                由 src/index.html 构建出来
 *   assets/                   打包出的 js / css / 字体
 *   <public/ 下的每一项>       vite 把 public/ 原样拷到产物根（favicon.svg、ruffle/）
 *
 * 清单与 vite 的输出规则严格对应（产物 = index.html + assets/ + public/*）：
 * 不删就会让旧 hash 的 assets 无限堆积，发版包只增不减；删多一点就会碰源码，
 * 所以目录白名单里那几个永远不删。
 *
 * 调用点：`package.json` 的 `build` 前置执行（`dev` 不需要，dev 不写产物）。
 */

const ROOT = process.cwd()
const PUBLIC = join(ROOT, "public")

/** 源码/依赖目录：即使 public/ 里出现了同名项也绝不删。 */
const PROTECTED = new Set([
  "src",
  "scripts",
  "public",
  "node_modules",
  "package.json",
  "pnpm-lock.yaml",
  "pnpm-workspace.yaml",
  "tsconfig.json",
  "vite.config.ts",
  "vitest.config.ts",
])

const targets = new Set([join(ROOT, "index.html"), join(ROOT, "assets")])

// public/ 的每一项都会出现在产物根，所以它们也是上一次构建的残留。
if (existsSync(PUBLIC)) {
  for (const name of readdirSync(PUBLIC)) {
    if (PROTECTED.has(name)) continue
    targets.add(join(ROOT, name))
  }
}

let removed = 0
for (const target of targets) {
  if (!existsSync(target)) continue
  rmSync(target, { recursive: true, force: true })
  removed++
}
console.log(`[clean] removed ${removed} previous build artifacts from frontend/`)
