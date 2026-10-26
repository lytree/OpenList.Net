import { defineComponent, computed, reactive, ref } from "vue"
import { useRoute, useRouter } from "vue-router"
import {
  ElButton,
  ElCard,
  ElCheckbox,
  ElDivider,
  ElForm,
  ElFormItem,
  ElInput,
  ElLink,
  ElMessage,
  ElSpace,
  ElTag,
  type FormInstance,
  type FormRules,
} from "element-plus"
import { Key, Lock, User as UserIcon } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { webauthnSupported } from "~/api"

/**
 * 登录页。
 *
 * 行为对齐旧前端：
 * - 默认走 `/auth/login/hash`（浏览器侧做 sha256，密码明文不出网）。
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

    const formRef = ref<FormInstance>()
    const loading = ref(false)
    const passkeyLoading = ref(false)

    const form = reactive({
      username: localStorage.getItem("username") ?? "",
      password: "",
      otp: "",
      remember: localStorage.getItem("remember-pwd") === "true",
      useLdap: false,
    })

    const rules = computed<FormRules>(() => ({
      username: [{ required: true, message: t("login.username-tips"), trigger: "blur" }],
      password: [{ required: true, message: t("login.password-tips"), trigger: "blur" }],
    }))

    function targetAfterLogin(): string {
      const redirect = route.query.redirect
      const raw = typeof redirect === "string" ? decodeURIComponent(redirect) : "/"
      // 只接受站内绝对路径，避免 ?redirect=https://evil 造成开放重定向。
      return raw.startsWith("/") && !raw.startsWith("//") ? raw : "/"
    }

    async function submit() {
      const valid = await formRef.value?.validate().catch(() => false)
      if (!valid) return

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
        ElMessage.error((error as Error).message || "Login failed")
      } finally {
        loading.value = false
      }
    }

    function persistRemember() {
      localStorage.setItem("remember-pwd", String(form.remember))
      if (form.remember) localStorage.setItem("username", form.username)
      else localStorage.removeItem("username")
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

    return () => (
      <div class="ol-login-page">
        <ElCard shadow="never" class="ol-login-card">
          {{
            header: () => (
              <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <span style={{ fontWeight: 600 }}>
                  {t("login.login_to")} {settings.siteTitle}
                </span>
                {settings.ldapEnabled && <ElTag size="small">LDAP</ElTag>}
              </div>
            ),
            default: () => (
              <div
                onKeydown={(e: KeyboardEvent) => {
                  if (e.key === "Enter" && !loading.value) void submit()
                }}
              >
              <ElForm
                ref={formRef}
                model={form}
                rules={rules.value}
                labelPosition="top"
              >
                <ElFormItem label={t("login.username-tips")} prop="username">
                  <ElInput
                    v-model={form.username}
                    placeholder={t("login.username-tips")}
                    prefixIcon={UserIcon}
                    clearable={true}
                  />
                </ElFormItem>

                <ElFormItem label={t("login.password-tips")} prop="password">
                  <ElInput
                    v-model={form.password}
                    type="password"
                    placeholder={t("login.password-tips")}
                    prefixIcon={Lock}
                    showPassword={true}
                  />
                </ElFormItem>

                <ElFormItem label={t("login.otp-tips")} prop="otp">
                  <ElInput v-model={form.otp} placeholder={t("login.otp-tips")} maxlength={6} />
                </ElFormItem>

                <div
                  style={{
                    display: "flex",
                    alignItems: "center",
                    justifyContent: "space-between",
                    marginBottom: "16px",
                  }}
                >
                  <ElCheckbox v-model={form.remember}>{t("login.remember")}</ElCheckbox>
                  {settings.ldapEnabled && (
                    <ElCheckbox v-model={form.useLdap}>LDAP</ElCheckbox>
                  )}
                </div>

                <ElButton
                  type="primary"
                  style={{ width: "100%" }}
                  loading={loading.value}
                  onClick={submit}
                >
                  {t("login.login")}
                </ElButton>

                {settings.webauthnEnabled && webauthnSupported() && (
                  <>
                    <ElDivider>或</ElDivider>
                    <ElButton
                      style={{ width: "100%" }}
                      loading={passkeyLoading.value}
                      onClick={loginWithPasskey}
                    >
                      <ElSpace size={6}>
                        <Key />
                        <span>Passkey</span>
                      </ElSpace>
                    </ElButton>
                  </>
                )}

                <div style={{ marginTop: "12px", display: "flex", justifyContent: "space-between" }}>
                  <ElLink type="primary" underline="never" onClick={() => router.push("/")}>
                    {t("login.use_guest")}
                  </ElLink>
                  <ElLink
                    type="info"
                    underline="never"
                    href={t("login.forget_url")}
                    target="_blank"
                  >
                    {t("login.forget")}
                  </ElLink>
                </div>
              </ElForm>
              </div>
            ),
          }}
        </ElCard>
      </div>
    )
  },
})
