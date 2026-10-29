import { computed, defineComponent, onMounted, reactive, ref } from "vue"
import { useRouter } from "vue-router"
import {
  ElAlert,
  ElButton,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElLink,
  ElMessage,
} from "element-plus"
import { CircleCheckFilled, Lock, User as UserIcon } from "@element-plus/icons-vue"
import { t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useInitStore } from "~/stores/init"
import { initSetup } from "~/api/init"
import AuthBg from "~/components/AuthBg"

/** 初始化向导的 logo 兜底：此阶段站点设置可能为空，不能指望 `logo` 有值。 */
const INIT_LOGO_FALLBACK = "https://res.oplist.org/logo/logo.png"

/**
 * 安装向导（对应旧前端 `pages/init/index.tsx`）。
 *
 * 后端只在「开发模式」或「显式配置了 AdminPassword」时才自动建管理员，其余情况把建号
 * 留给 `POST /api/public/init/setup`。前端如果连这个页面都没有，用户就卡死在登录页：
 * 既没有账号可登，也没有入口去创建账号 —— 这正是本次要修的问题。
 *
 * 与旧前端的差异：Go/.NET 后端都没有 `/public/env_check`（那是 TS Worker 后端专有的
 * 环境自检），因此省掉「环境自检」这一步，向导固定为「填写管理员 → 完成」两步。
 */
export default defineComponent({
  name: "InitPage",
  setup() {    const router = useRouter()
    const settings = useSettingsStore()
    const init = useInitStore()

    const form = reactive({
      username: "admin",
      password: "",
      confirm: "",
      siteTitle: "",
    })

    const loading = ref(false)
    const done = ref(false)
    const error = ref("")
    /** CDN logo 拉不到时（离线部署很常见）直接收起 <img>，别留一个破图标。 */
    const logoBroken = ref(false)

    /** 站点地址：hash 路由下 `origin + pathname` 就是站点根。 */
    const siteUrl = computed(() =>
      typeof window === "undefined" ? "/" : window.location.origin + window.location.pathname,
    )

    onMounted(async () => {
      await settings.load()
      form.siteTitle = settings.siteTitle
      // 已经装过了就直接去登录页：留在向导页只会让用户以为还得再建一次号。
      const initialized = await init.check(true)
      if (initialized) void router.replace("/@login")
    })

    async function submit() {
      error.value = ""
      if (form.password.length < 4) {
        ElMessage.error(t("init.password_too_short"))
        return
      }
      if (form.password !== form.confirm) {
        ElMessage.error(t("init.password_mismatch"))
        return
      }
      loading.value = true
      try {
        const resp = await initSetup({
          username: form.username.trim(),
          password: form.password,
          site_title: form.siteTitle.trim() || undefined,
        })
        if (resp.code !== 200) {
          error.value = resp.message || t("init.failed")
          ElMessage.error(error.value)
          return
        }
        init.markInitialized()
        // 站点名可能刚被改掉，强制重取一次公开设置，登录页与页脚才会用新值。
        await settings.load(true)
        done.value = true
      } catch (e) {
        error.value = String((e as Error)?.message ?? e)
        ElMessage.error(error.value)
      } finally {
        loading.value = false
      }
    }

    function go(path: string) {
      void router.replace(path)
    }

    /** EP 的 keydown 事件类型是 `Event | KeyboardEvent`，这里统一收口。 */
    function onEnterKey(e: Event | KeyboardEvent) {
      if ((e as KeyboardEvent).key === "Enter" && !loading.value) void submit()
    }

    const logo = computed(() => settings.logo || INIT_LOGO_FALLBACK)

    return () => (
      <div class="ol-auth">
        <div class="ol-auth-card is-wide">
          <div class="ol-init-head">
            {!logoBroken.value && (
              <img
                class="ol-init-logo"
                src={logo.value}
                alt=""
                onError={() => (logoBroken.value = true)}
              />
            )}
            <div class="ol-init-head-text">
              <h1 class="ol-init-title">{t("init.title")}</h1>
              <p class="ol-init-sub">
                {t("init.setup_to")} {settings.siteTitle}
              </p>
            </div>
          </div>

          <ol class="ol-init-steps">
            <li class={["ol-init-step", !done.value && "is-active"]}>
              <span class="ol-init-step-no">1</span>
              {t("init.step_admin")}
            </li>
            <li class={["ol-init-step", done.value && "is-active"]}>
              <span class="ol-init-step-no">2</span>
              {t("init.step_finish")}
            </li>
          </ol>

          {!done.value ? (
            <ElForm labelPosition="top">
              {error.value && (
                <ElAlert
                  class="ol-init-alert"
                  type="error"
                  showIcon={true}
                  closable={false}
                  title={error.value}
                />
              )}

              <ElFormItem label={t("init.username-tips")}>
                <ElInput
                  v-model={form.username}
                  placeholder={t("init.username-tips")}
                  prefixIcon={UserIcon}
                  clearable={true}
                />
              </ElFormItem>

              <ElFormItem label={t("init.password-tips")}>
                <ElInput
                  v-model={form.password}
                  type="password"
                  placeholder={t("init.password-tips")}
                  prefixIcon={Lock}
                  showPassword={true}
                />
              </ElFormItem>

              <ElFormItem label={t("init.confirm_password-tips")}>
                <ElInput
                  v-model={form.confirm}
                  type="password"
                  placeholder={t("init.confirm_password-tips")}
                  prefixIcon={Lock}
                  showPassword={true}
                  onKeydown={onEnterKey}
                />
              </ElFormItem>

              <ElFormItem label={t("init.site_title-tips")}>
                <ElInput
                  v-model={form.siteTitle}
                  placeholder={t("init.site_title-tips")}
                  onKeydown={onEnterKey}
                />
              </ElFormItem>

              <ElButton
                type="primary"
                style={{ width: "100%" }}
                loading={loading.value}
                onClick={() => void submit()}
              >
                {t("init.setup")}
              </ElButton>

              <div class="ol-init-foot">
                <ElLink type="info" underline="never" onClick={() => go("/")}>
                  {t("init.done_go_home")}
                </ElLink>
              </div>
            </ElForm>
          ) : (
            <div class="ol-init-done">
              <ElIcon class="ol-init-done-icon" size={40}>
                <CircleCheckFilled />
              </ElIcon>
              <h2 class="ol-init-done-title">{t("init.done_title")}</h2>
              <p class="ol-init-sub">{t("init.done_subtitle")}</p>

              <div class="ol-init-facts">
                <div class="ol-init-fact">
                  <span>{t("init.done_username")}</span>
                  <code>{form.username}</code>
                </div>
                <div class="ol-init-fact">
                  <span>{t("init.done_site_url")}</span>
                  <a href={siteUrl.value} target="_blank" rel="noopener">
                    {siteUrl.value}
                  </a>
                </div>
              </div>

              <div class="ol-init-done-actions">
                <ElButton onClick={() => go("/")}>{t("init.done_go_home")}</ElButton>
                <ElButton type="primary" onClick={() => go("/@login")}>
                  {t("init.done_go_login")}
                </ElButton>
              </div>
            </div>
          )}

          <p class="ol-init-hint">{t("init.finalizing_tip")}</p>
        </div>
        <AuthBg />
      </div>
    )
  },
})
