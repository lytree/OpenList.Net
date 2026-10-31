import { computed, defineComponent, reactive, ref } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElCheckbox, ElIcon, ElInput, ElMessage, ElTooltip } from "element-plus"
import { Key, Moon, Sunny } from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { LoginError, useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"
import { webauthnSupported } from "~/api"
import AuthBg from "~/components/AuthBg"

/**
 * 登录页（复刻旧前端 `pages/login/index.tsx`）。
 *
 * 版式：满屏色块背景 + 364px 白色卡片；卡片内是「logo + 标题」一行、填充式输入框、
 * 记住账号/忘记密码一行、Clear/Login 并排两个按钮、以游客身份浏览、底部语言+主题图标。
 *
 * 行为对齐旧前端：
 * - 默认走 `/auth/login/hash`（浏览器侧 sha256，密码明文不出网）。
 * - **验证码按需出现**：后端在缺 `otp_code` 时返回 402，这时才把输入框切成 TOTP，
 *   而不是一进页面就摆一个多数人用不到的输入框。
 * - `remember` 只记用户名，不记密码（旧版把密码写进 localStorage，属安全问题，不复刻）。
 * - 成功后回到 `?redirect=`，缺省回首页。
 */
export default defineComponent({
  name: "LoginPage",
  setup() {
    const router = useRouter()
    const route = useRoute()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    const loading = ref(false)
    const passkeyLoading = ref(false)
    /** 后端说还要 TOTP，才把输入框切过去。 */
    const needOtp = ref(false)

    const form = reactive({
      username: localStorage.getItem("username") ?? "",
      password: "",
      otp: "",
      remember: localStorage.getItem("remember-pwd") === "true",
      useLdap: false,
    })

    const logoBroken = ref(false)
    const logo = computed(() => settings.logo)

    function targetAfterLogin(): string {
      const redirect = route.query.redirect
      const raw = typeof redirect === "string" ? decodeURIComponent(redirect) : "/"
      // 只接受站内绝对路径，避免 ?redirect=https://evil 造成开放重定向。
      return raw.startsWith("/") && !raw.startsWith("//") ? raw : "/"
    }

    function persistRemember() {
      localStorage.setItem("remember-pwd", String(form.remember))
      if (form.remember) localStorage.setItem("username", form.username)
      else localStorage.removeItem("username")
    }

    async function submit() {
      if (loading.value) return
      if (!form.username.trim()) {
        ElMessage.warning(t("login.username-tips"))
        return
      }
      if (!needOtp.value && !form.password) {
        ElMessage.warning(t("login.password-tips"))
        return
      }

      loading.value = true
      try {
        await user.login(form.username, form.password, {
          otp_code: form.otp,
          useLdap: form.useLdap,
        })
        persistRemember()
        ElMessage.success(t("login.success"))
        await router.replace(targetAfterLogin())
      } catch (error) {
        if (error instanceof LoginError && error.code === 402 && !needOtp.value) {
          needOtp.value = true
          ElMessage.info(t("login.otp-tips"))
          return
        }
        ElMessage.error((error as Error).message || "Login failed")
      } finally {
        loading.value = false
      }
    }

    function clear() {
      if (needOtp.value) form.otp = ""
      else {
        form.username = ""
        form.password = ""
      }
    }

    async function loginWithPasskey() {
      if (!form.username) {
        ElMessage.warning(t("login.username-tips"))
        return
      }
      passkeyLoading.value = true
      try {
        await user.loginWithPasskey(form.username)
        persistRemember()
        ElMessage.success(t("login.success"))
        await router.replace(targetAfterLogin())
      } catch (error) {
        ElMessage.error((error as Error).message || "Passkey login failed")
      } finally {
        passkeyLoading.value = false
      }
    }

    /**
     * 游客此刻能落脚的门面（设置 → 访问控制）。
     *
     * 两个开关是独立的：媒体默认开、首页默认关，管理员也可能两个都关（纯私有站点）。
     * 都关时站点没有任何「游客可看」的页面，登录页就不该再摆「以游客身份浏览」这个按钮 ——
     * 点了只会被守卫原样弹回来，看起来像页面坏了。
     */
    const guestEntry = computed(() => {
      if (settings.mediaGuestAccess) return "/media"
      if (settings.homeGuestAccess) return "/files"
      return ""
    })

    function guestCanOpen(path: string): boolean {
      if (path.startsWith("/@manage")) return false
      if (path.startsWith("/files")) return settings.homeGuestAccess
      if (path.startsWith("/media")) {
        // 播放列表 / 口味是「按用户」的数据，只有「媒体 + 首页」的开关管不到它们
        if (path.startsWith("/media/playlists") || path.startsWith("/media/taste")) return false
        return settings.mediaGuestAccess
      }
      return Boolean(guestEntry.value)
    }

    async function browseAsGuest() {
      await user.logout()
      // `?redirect=` 指向游客进不去的地方时落回「当前还开着的门面」，
      // 别再把人送进一个会被守卫弹回来的页面（来回死循环）。
      const target = targetAfterLogin()
      await router.replace(guestCanOpen(target) ? target : guestEntry.value || "/@login")
    }

    return () => (
      <div class="ol-auth">
        <form
          class="ol-auth-card"
          onSubmit={(e: Event) => {
            e.preventDefault()
            void submit()
          }}
        >
          <div class="ol-auth-head">
            {logo.value && !logoBroken.value && (
              <img
                class="ol-auth-logo"
                src={logo.value}
                alt=""
                onError={() => (logoBroken.value = true)}
              />
            )}
            <h1 class="ol-auth-title">
              {t("login.login_to")} {settings.siteTitle}
            </h1>
          </div>

          {!needOtp.value ? (
            <>
              <ElInput
                v-model={form.username}
                name="username"
                placeholder={t("login.username-tips")}
                autocomplete="username"
              />
              <ElInput
                v-model={form.password}
                name="password"
                type="password"
                placeholder={t("login.password-tips")}
                autocomplete="current-password"
                showPassword={true}
                onKeydown={(e: Event | KeyboardEvent) => {
                  if ((e as KeyboardEvent).key === "Enter") void submit()
                }}
              />
              <div class="ol-auth-row">
                <ElCheckbox v-model={form.remember} label={t("login.remember")} />
                <a class="ol-auth-forget" href={t("login.forget_url")} target="_blank" rel="noopener">
                  {t("login.forget")}
                </a>
              </div>
            </>
          ) : (
            <ElInput
              v-model={form.otp}
              name="otp"
              placeholder={t("login.otp-tips")}
              maxlength={6}
              autofocus={true}
              onInput={(value: string) => {
                // 6 位满了直接提交，和旧前端一致（少点一次按钮）
                if (/^\d{6}$/.test(value)) void submit()
              }}
              onKeydown={(e: Event | KeyboardEvent) => {
                if ((e as KeyboardEvent).key === "Enter") void submit()
              }}
            />
          )}

          <div class="ol-auth-actions">
            {!needOtp.value && (
              <ElButton class="ol-auth-btn" onClick={clear}>
                {t("login.clear")}
              </ElButton>
            )}
            <ElButton class="ol-auth-btn" type="primary" loading={loading.value} onClick={submit}>
              {t("login.login")}
            </ElButton>
          </div>

          {settings.ldapEnabled && (
            <ElCheckbox v-model={form.useLdap} class="ol-auth-ldap">
              {/* 管理员在「LDAP 设置」里填了提示文案就用它，未填回退多语言默认值 */}
              {settings.ldapLoginTips || t("login.ldap_tips")}
            </ElCheckbox>
          )}

          {guestEntry.value && (
            <ElButton class="ol-auth-btn is-ghost" onClick={() => void browseAsGuest()}>
              {t("login.use_guest")}
            </ElButton>
          )}

          <div class="ol-auth-foot">
            <ElTooltip content={t("home.toolbar.switch_lang")} placement="top">
              <ElButton
                text={true}
                circle={true}
                onClick={() => {
                  const index = availableLanguages.findIndex((l) => l.code === locale.value)
                  setLocale(availableLanguages[(index + 1) % availableLanguages.length]?.code ?? "en")
                }}
              >
                {locale.value.toUpperCase()}
              </ElButton>
            </ElTooltip>
            <ElTooltip content={t("home.toolbar.toggle_theme")} placement="top">
              <ElButton text={true} circle={true} onClick={toggle}>
                <ElIcon>{isDark.value ? <Sunny /> : <Moon />}</ElIcon>
              </ElButton>
            </ElTooltip>
            {settings.webauthnEnabled && webauthnSupported() && (
              <ElTooltip content="Passkey" placement="top">
                <ElButton text={true} circle={true} loading={passkeyLoading.value} onClick={() => void loginWithPasskey()}>
                  <ElIcon>
                    <Key />
                  </ElIcon>
                </ElButton>
              </ElTooltip>
            )}
          </div>
        </form>
        <AuthBg />
      </div>
    )
  },
})
