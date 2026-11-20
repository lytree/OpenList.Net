import path from "node:path"
import { defineConfig } from "vite"
import vueJsx from "vue-jsx/vite"
import ElementPlus from "unplugin-element-plus/vite"

// 前端基线：Vue 3 + TSX（vue-jsx），**VDOM 优先**。
// 默认全量 VDOM 编译，不含 Vapor 运行时特技，插件生态（pinia/router/query）完全兼容。
//
// 若后续需要为性能敏感区域启用 Vapor 叶子（.vapor.tsx），按以下三步打开
//（已在 vue 3.6.0-rc.12 实测，结论见 docs/unified-media-product-plan.md 第 3 节）：
//   1) 解开下方 vue alias（Vapor 运行时只存在于 with-vapor 构建）
//   2) main.tsx 中 createApp(App).use(vaporInteropPlugin)
//   3) 目标文件命名为 *.vapor.tsx
export default defineConfig(() => {
  return {
    // 源码根是 `src/`（入口 html 也就是 `src/index.html`），**构建产物直接落在
    // `frontend/` 根**：`index.html` + `assets/` + `public/` 的副本与源码同级，
    // 不再有 `frontend/dist/` 这一层。这样发布包里就是 `frontend/index.html`，
    // 后端 `DistDir` 指到 `frontend` 即可，包内少一层无意义的目录。
    //
    // 代价是产物与源码混在一个目录里，所以：
    //   · `emptyOutDir` 必须是 false（父目录里全是源码，清空就没了）；
    //   · 每次构建前先跑 `scripts/clean-out.mjs` 删掉上一次的产物（否则旧 hash 的
    //     assets 会一直堆着，打包体积只增不减）。
    root: "src",
    publicDir: "../public",
    // root 挪进 src/ 后，envDir 默认也跟着变成 src/ —— .env 一直是在 frontend/ 放的，
    // 这里显式改回来，免得以后加 .env 时静默读不到。
    envDir: "..",
    build: {
      outDir: "..",
      emptyOutDir: false,
      assetsDir: "assets",
    },
    resolve: {
      alias: [
        // { find: /^vue$/, replacement: "vue/dist/vue.runtime-with-vapor.esm-browser.prod.js" },
        { find: /^~/, replacement: path.resolve(import.meta.dirname, "src") },
      ],
    },
    plugins: [
      vueJsx({
        vapor: false,
      }),
      // 按需样式：对源码里的 `import { ElXxx } from "element-plus"` 自动补
      // `element-plus/es/components/xxx/style/css`，取代全量 dist/index.css。
      // ElMessage / ElMessageBox 同样按导入位置注入，弹层样式不会丢。
      ElementPlus({}),
    ],
    server: {
      host: "0.0.0.0",
      port: 5173,
      proxy: {
        // /api 之外，/d /p /ad /ap /ae 是后端的签名下载路由（SignEndpoints），
        // 不代理的话 dev 下点「下载」会落进 SPA fallback 返回 index.html。
        "/api": {
          target: process.env.DEV_PROXY_TARGET || "http://localhost:5244",
          changeOrigin: true,
        },
        "/d": { target: process.env.DEV_PROXY_TARGET || "http://localhost:5244", changeOrigin: true },
        "/p": { target: process.env.DEV_PROXY_TARGET || "http://localhost:5244", changeOrigin: true },
        "/ad": { target: process.env.DEV_PROXY_TARGET || "http://localhost:5244", changeOrigin: true },
        "/ap": { target: process.env.DEV_PROXY_TARGET || "http://localhost:5244", changeOrigin: true },
        "/ae": { target: process.env.DEV_PROXY_TARGET || "http://localhost:5244", changeOrigin: true },
      },
    },
  }
})
