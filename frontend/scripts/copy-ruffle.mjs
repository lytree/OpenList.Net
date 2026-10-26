import { copyFileSync, existsSync, mkdirSync, readdirSync, statSync } from "node:fs"
import { join } from "node:path"

/**
 * 把 Ruffle 运行时资源复制到 `public/ruffle/`。
 *
 * 为什么需要这一步：`@ruffle-rs/ruffle` 是 **selfhosted** 包 —— 它是一个 IIFE，
 * 运行时按 `document.currentScript` 的目录（默认 publicPath）去加载
 * `core.ruffle.<hash>.js` 与 ~14 MB 的 wasm。交给打包器 import 会被重写路径，
 * 所以只能把这些文件当成**静态资源**放在 web 根下。
 *
 * 为什么不直接提交进 git：wasm 单个 14 MB、两个加 core js 接近 28 MB，
 * 二进制进版本库会让 clone 与 diff 都变难；文件名带 hash，跟着依赖版本走即可重建。
 *
 * 调用点：`package.json` 的 `dev` / `build` 前置执行（pnpm 默认不跑 pre 钩子，
 * 所以显式串在命令里，不依赖 `predev`）。
 *
 * 幂等：目录里已有同尺寸的 ruffle.js 就整体跳过，避免每次启动都拷 28 MB。
 */

const SRC = join(process.cwd(), "node_modules", "@ruffle-rs", "ruffle")
const DEST = join(process.cwd(), "public", "ruffle")

/** 需要复制的文件：入口脚本 + 核心脚本 + wasm。`.map` 只在调试时需要，跳过。 */
const PATTERN = /^(ruffle\.js|core\.ruffle\..*\.js|.*\.wasm)$/

function isUpToDate() {
  const srcEntry = join(SRC, "ruffle.js")
  const destEntry = join(DEST, "ruffle.js")
  if (!existsSync(srcEntry)) {
    console.warn("[ruffle] 找不到 node_modules/@ruffle-rs/ruffle，先跑 pnpm install")
    return true
  }
  if (!existsSync(destEntry)) return false
  if (statSync(srcEntry).size !== statSync(destEntry).size) return false
  // 依赖升级后 wasm/core 的 hash 文件名会变，只比入口脚本会漏拷新资源
  const srcNames = readdirSync(SRC).filter((name) => PATTERN.test(name)).sort()
  const destNames = readdirSync(DEST).filter((name) => PATTERN.test(name)).sort()
  return srcNames.length === destNames.length && srcNames.every((name, i) => name === destNames[i])
}

if (!isUpToDate()) {
  mkdirSync(DEST, { recursive: true })
  const files = readdirSync(SRC).filter((name) => PATTERN.test(name))
  let bytes = 0
  for (const name of files) {
    copyFileSync(join(SRC, name), join(DEST, name))
    bytes += statSync(join(SRC, name)).size
  }
  console.log(`[ruffle] copied ${files.length} files (${(bytes / 1024 / 1024).toFixed(1)} MB) -> public/ruffle`)
} else {
  console.log("[ruffle] public/ruffle is up to date")
}
