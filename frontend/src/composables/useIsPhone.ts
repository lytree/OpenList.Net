import { onScopeDispose, ref, type Ref } from "vue"

/**
 * 手机断点（与 `styles/responsive.css` 的 `@media (max-width: 767px)` 保持一致）。
 *
 * 断点写在两处是刻意的：CSS 负责「同结构、改尺寸」，JS 负责「换结构」（如文件列表
 * 从表格换成卡片行）。共用同一个数值，改一处就要同步另一处。
 */
export const PHONE_QUERY = "(max-width: 767px)"

/**
 * 当前视口是否落在手机断点内，且随窗口尺寸变化实时更新。
 *
 * 组件销毁时摘掉监听；`matchMedia` 不存在（非浏览器环境、旧测试环境）时按桌面处理，
 * 让调用方永远拿到一个布尔 ref，不必自己判空。
 */
export function useIsPhone(): Ref<boolean> {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return ref(false)
  const query = window.matchMedia(PHONE_QUERY)
  const isPhone = ref(query.matches)
  const onChange = (event: MediaQueryListEvent) => {
    isPhone.value = event.matches
  }
  query.addEventListener("change", onChange)
  onScopeDispose(() => query.removeEventListener("change", onChange))
  return isPhone
}
