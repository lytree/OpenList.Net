import { defineStore } from "pinia"
import { computed, ref } from "vue"
import {
  fetchMe,
  getToken,
  hashPassword,
  login as apiLogin,
  loginWithHash,
  loginWithLdap,
  logout as apiLogout,
  persistToken,
  setToken,
  webauthnAssert,
} from "~/api"
import { UserMethods, type Me, type Permission } from "~/api/types"

/**
 * 当前用户 store。
 *
 * 职责边界：只管「我是谁、我能做什么」，不负责跳转。
 * 401 的跳转由 router 在 `onUnauthorized` 里统一处理。
 */
export const useUserStore = defineStore("user", () => {
  const me = ref<Me | null>(null)
  const token = ref<string>(getToken())
  const loading = ref(false)

  const isLogin = computed(() => Boolean(token.value))
  const isAdmin = computed(() => UserMethods.isAdmin(me.value))
  const isGuest = computed(() => UserMethods.isGuest(me.value))
  const username = computed(() => me.value?.username ?? "")

  /** 纯权限位判断，语义与后端 `User.Can*` 一致。 */
  const can = (permission: Permission) => UserMethods.can(me.value, permission)

  function applyToken(next: string) {
    token.value = next
    setToken(next)
  }

  function clear() {
    me.value = null
    token.value = ""
    setToken(null)
  }

  /** 拉取当前用户资料；未登录（无 token）直接返回 null。 */
  async function loadMe(force = false): Promise<Me | null> {
    if (!token.value) {
      me.value = null
      return null
    }
    if (me.value && !force) return me.value
    loading.value = true
    try {
      const resp = await fetchMe()
      if (resp.code === 200) {
        me.value = resp.data
        return me.value
      }
      // token 失效：清掉，交给守卫跳登录。
      if (resp.code === 401) clear()
      me.value = null
      return null
    } finally {
      loading.value = false
    }
  }

  type LoginOptions = { otp_code?: string; useLdap?: boolean }

  /** 用户名密码登录。默认走 `/login/hash`（密码不上行明文）。 */
  async function login(
    username: string,
    password: string,
    options: LoginOptions = {},
  ): Promise<void> {
    const payload = { username, otp_code: options.otp_code ?? "" }
    const resp = options.useLdap
      ? await loginWithLdap({ ...payload, password })
      : await loginWithHash({ ...payload, password: hashPassword(password) })
    if (resp.code !== 200) throw new Error(resp.message)
    applyToken(persistToken(resp.data))
    await loadMe(true)
  }

  /** 明文登录（仅调试后端时用，正常 UI 不走）。 */
  async function loginPlain(username: string, password: string) {
    const resp = await apiLogin({ username, password })
    if (resp.code !== 200) throw new Error(resp.message)
    applyToken(persistToken(resp.data))
    await loadMe(true)
  }

  async function loginWithPasskey(username: string) {
    const data = await webauthnAssert(username)
    applyToken(persistToken(data))
    await loadMe(true)
  }

  async function logout() {
    try {
      if (token.value) await apiLogout()
    } finally {
      clear()
    }
  }

  return {
    me,
    token,
    loading,
    isLogin,
    isAdmin,
    isGuest,
    username,
    can,
    loadMe,
    login,
    loginPlain,
    loginWithPasskey,
    logout,
    clear,
  }
})
