import { createPinia } from "pinia"

/**
 * 单例 pinia。
 *
 * 单独导出实例是为了让 `router.ts` 里的守卫能 `useUserStore(pinia)`：
 * 守卫在首次导航时就会执行，此时若靠 `app.use(pinia)` 注入的 active pinia，
 * 存在「守卫先跑、pinia 还没装」的时序风险。显式传实例可以彻底规避。
 */
export const pinia = createPinia()

export * from "./user"
export * from "./settings"
