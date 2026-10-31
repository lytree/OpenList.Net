import { defineStore } from "pinia"
import { computed, ref } from "vue"
import {
  fetchPublicSettings,
  getSettingBool,
  type PublicSettings,
} from "~/api/settings"

/**
 * 站点设置 store。
 *
 * 由路由守卫在首次进入时加载一次（登录页也要用，所以不能要求已登录）。
 * 旧前端用的是「模块级可变对象 + getSetting()」，在 Vue 下改成 store，
 * 好处是 `site_title` 等值变化时模板能自动更新。
 */
export const useSettingsStore = defineStore("settings", () => {
  const settings = ref<PublicSettings>({})
  const loaded = ref(false)
  const loading = ref(false)

  async function load(force = false) {
    if (loading.value) return
    if (loaded.value && !force) return
    loading.value = true
    try {
      const resp = await fetchPublicSettings()
      if (resp.code === 200 && resp.data) {
        settings.value = { ...resp.data }
        loaded.value = true
      }
    } finally {
      loading.value = false
    }
  }

  const get = (key: string) => settings.value[key] ?? ""
  const getBool = (key: string) => getSettingBool(settings.value, key)
  const getNumber = (key: string, fallback = 0) => {
    const raw = get(key)
    return raw ? Number(raw) : fallback
  }

  const siteTitle = computed(() => get("site_title") || "OpenList")
  const logo = computed(() => get("logo"))
  const mainColor = computed(
    () => window.OPENLIST_CONFIG?.main_color || get("main_color") || "#1890ff",
  )
  const ldapEnabled = computed(() => getBool("ldap_login_enabled"))
  /** LDAP 登录提示文案（设置项 `ldap_login_tips`）；为空时登录页回退 i18n 文案。 */
  const ldapLoginTips = computed(() => get("ldap_login_tips").trim())
  const webauthnEnabled = computed(() => getBool("webauthn_login_enabled"))

  /**
   * 媒体域是否对游客开放（设置项 `media_guest_access`，默认 true）。
   *
   * 键缺失时按 true 兜底 —— 与后端 `IGuestAccessService` 的默认值、以及
   * `DbInitializer` 的种子**必须一致**，否则会出现「路由放行、接口 401」
   * （或反过来把游客挡在门外）。
   */
  const mediaGuestAccess = computed(() =>
    settings.value.media_guest_access === undefined ? true : getBool("media_guest_access"),
  )
  /** 首页（文件浏览）是否对游客开放（设置项 `home_guest_access`，默认 false）。 */
  const homeGuestAccess = computed(() => getBool("home_guest_access"))

  return {
    settings,
    loaded,
    loading,
    load,
    get,
    getBool,
    getNumber,
    siteTitle,
    logo,
    mainColor,
    ldapEnabled,
    ldapLoginTips,
    webauthnEnabled,
    mediaGuestAccess,
    homeGuestAccess,
  }
})
