import { createApp } from "vue"
import { VueQueryPlugin } from "@tanstack/vue-query"

// 按需样式由 unplugin-element-plus 注入（见 vite.config.ts）；
// 这里只保留全量层面的暗色变量与项目 token。
import "element-plus/theme-chalk/dark/css-vars.css"
import "vxe-table/lib/style.css"
import "./styles/tokens.scss"
import "./styles/vxe-theme.scss"
import "./styles/layout.scss"
import "./styles/media-shell.scss"
import "./styles/media-extra.scss"
import "./styles/preview-extra.scss"
import "./styles/backup-extra.scss"
import "./styles/dashboard-extra.scss"
import "./styles/about-extra.scss"
import "./styles/responsive.scss"

import App from "./App"
import router from "./router"
import { pinia } from "./stores"
import { VxeTableMenuModule } from "vxe-table"

/**
 * VDOM 优先：标准 createApp。
 * TSX 由 vue-jsx 编译为 VDOM 渲染函数，与 Element Plus / pinia / vue-router /
 * vue-query 完全同构，没有 Vapor 互操作边界（结论见方案文档第 3 节）。
 */
const app = createApp(App)

// vxe-table 的右键事件（cell-menu）由「菜单模块」提供，装上才会派发；
// 表格封装用它实现 ElTable 的 row-contextmenu，但不弹 vxe 自带菜单。
VxeTableMenuModule.install(app)

app
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
