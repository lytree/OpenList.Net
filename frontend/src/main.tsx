import { createApp } from "vue"
import { VueQueryPlugin } from "@tanstack/vue-query"

// 按需样式由 unplugin-element-plus 注入（见 vite.config.ts）；
// 这里只保留全量层面的暗色变量与项目 token。
import "element-plus/theme-chalk/dark/css-vars.css"
import "./styles/tokens.css"
import "./styles/layout.css"
import "./styles/media-extra.css"
import "./styles/preview-extra.css"
import "./style.css"

import App from "./App"
import router from "./router"
import { pinia } from "./stores"

/**
 * VDOM 优先：标准 createApp。
 * TSX 由 vue-jsx 编译为 VDOM 渲染函数，与 Element Plus / pinia / vue-router /
 * vue-query 完全同构，没有 Vapor 互操作边界（结论见方案文档第 3 节）。
 */
createApp(App)
  .use(pinia)
  .use(router)
  .use(VueQueryPlugin, {
    queryClientConfig: {
      defaultOptions: {
        queries: {
          // 文件列表这类数据靠路由/手动刷新驱动，不做窗口聚焦自动重取，避免闪烁
          refetchOnWindowFocus: false,
          retry: 1,
          staleTime: 30_000,
        },
      },
    },
  })
  .mount("#app")
