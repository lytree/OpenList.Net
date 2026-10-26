import { computed, defineComponent } from "vue"
import { RouterView } from "vue-router"
import { ElConfigProvider } from "element-plus"
import en from "element-plus/es/locale/lang/en"
import zhCn from "element-plus/es/locale/lang/zh-cn"
import { locale } from "~/i18n"
import { useBrandColor } from "~/composables/useBrandColor"
import { useSettingsStore } from "~/stores/settings"

/**
 * 应用根：只做三件事
 * 1. Element Plus 的语言包跟随 i18n 的 locale（EP 组件内部文案，如分页/日期）；
 * 2. 把站点品牌色注入成 --el-color-primary 系列变量；
 * 3. 渲染 RouterView（布局由路由表里的 BasicLayout 承担，登录页为 blank 布局）。
 */
export default defineComponent({
  name: "App",
  setup() {
    const settings = useSettingsStore()
    useBrandColor(() => settings.mainColor)

    const elementLocale = computed(() => (locale.value.startsWith("zh") ? zhCn : en))

    return () => (
      <ElConfigProvider locale={elementLocale.value}>
        <RouterView />
      </ElConfigProvider>
    )
  },
})
