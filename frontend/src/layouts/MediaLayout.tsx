import { defineComponent, ref } from "vue"
import { RouterLink, RouterView, useRouter } from "vue-router"
import {
  ElAvatar,
  ElButton,
  ElDialog,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElIcon,
  ElInput,
  ElMessage,
  ElTooltip,
} from "element-plus"
import { ArrowDown, Folder, Moon, Setting, Sunny, SwitchButton } from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { updateMe } from "~/api/auth"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"
import MediaNav from "~/pages/media/MediaNav"

/**
 * 媒体门面的外壳。
 *
 * 与文件门面（`FilesLayout`）**完全独立**：不同的组件、不同的导航、不同的守卫。
 * 理由见 `router.ts` 顶部注释 —— 媒体对游客开放、文件要登录，把两者塞进同一个布局，
 * 每一处都要判断「我现在在哪个门面」，而且游客会在公开页面上看到一个自己进不去的
 * 「文件」入口。
 *
 * 版式参照 Peach：一条常驻顶栏（品牌 + 栏目导航 + 账户），内容区宽幅居中，
 * 导航不占用页面内空间 —— 海报墙页面最忌讳顶部再来一条自己会滚走的按钮条。
 */
export default defineComponent({
  name: "MediaLayout",
  setup() {
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    /** 单用户产品只保留「修改自己的密码」，与文件门面同一套逻辑。 */
    const passwordVisible = ref(false)
    const password = ref("")
    const confirmPassword = ref("")
    const passwordSaving = ref(false)

    async function submitPassword() {
      if (password.value.length < 4) {
        ElMessage.warning(t("global.password_too_short"))
        return
      }
      if (password.value !== confirmPassword.value) {
        ElMessage.warning(t("global.password_mismatch"))
        return
      }
      passwordSaving.value = true
      const resp = await updateMe({ password: password.value })
      passwordSaving.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      passwordVisible.value = false
      ElMessage.success(t("global.password_updated"))
    }

    async function onLogout() {
      await user.logout()
      await router.push("/media")
    }

    function onUserCommand(command: string) {
      if (command === "logout") void onLogout()
      if (command === "manage") void router.push("/@manage")
      if (command === "files") void router.push("/files")
      if (command === "playlists") void router.push({ name: "media-playlists" })
      if (command === "taste") void router.push({ name: "media-taste" })
      if (command === "tasks") void router.push({ name: "media-tasks" })
      if (command === "password") {
        password.value = ""
        confirmPassword.value = ""
        passwordVisible.value = true
      }
    }

    return () => (
      <div class="olm-shell">
        <header class="olm-header">
          <div class="olm-header-inner">
            <RouterLink to="/media" class="olm-brand" title={settings.siteTitle}>
              {settings.logo ? (
                <img class="olm-logo" src={settings.logo} alt={settings.siteTitle} />
              ) : (
                <>
                  <span class="olm-logo-mark">{(settings.siteTitle || "O").slice(0, 1)}</span>
                  <span class="olm-site">{settings.siteTitle}</span>
                </>
              )}
            </RouterLink>

            <MediaNav />

            <div class="olm-actions">
              <ElDropdown
                trigger="click"
                onCommand={(code: string) => setLocale(code)}
                v-slots={{
                  default: () => (
                    <ElButton text={true} size="small" class="olm-lang">
                      {availableLanguages.find((l) => l.code === locale.value)?.name ?? locale.value}
                    </ElButton>
                  ),
                  dropdown: () => (
                    <ElDropdownMenu>
                      {availableLanguages.map((lang) => (
                        <ElDropdownItem command={lang.code} disabled={lang.code === locale.value}>
                          {lang.name}
                        </ElDropdownItem>
                      ))}
                    </ElDropdownMenu>
                  ),
                }}
              />

              <ElTooltip content={t("home.toolbar.toggle_theme")} placement="bottom">
                <ElButton text={true} circle={true} onClick={toggle} class="olm-icon-btn">
                  <ElIcon>{isDark.value ? <Sunny /> : <Moon />}</ElIcon>
                </ElButton>
              </ElTooltip>

              {user.isLogin ? (
                <ElDropdown
                  trigger="click"
                  onCommand={onUserCommand}
                  v-slots={{
                    default: () => (
                      <span class="olm-user">
                        <ElAvatar size={26}>
                          {(user.username || "?").slice(0, 1).toUpperCase()}
                        </ElAvatar>
                        <span class="olm-user-name">{user.username}</span>
                        <ElIcon size={12}>
                          <ArrowDown />
                        </ElIcon>
                      </span>
                    ),
                    dropdown: () => (
                      <ElDropdownMenu>
                        <ElDropdownItem command="playlists">
                          {t("media.tab_playlists")}
                        </ElDropdownItem>
                        <ElDropdownItem command="taste">{t("media.tab_taste")}</ElDropdownItem>
                        <ElDropdownItem command="tasks">{t("media.tab_tasks")}</ElDropdownItem>
                        <ElDropdownItem command="files" divided={true}>
                          <ElIcon>
                            <Folder />
                          </ElIcon>
                          {t("media.goto_files")}
                        </ElDropdownItem>
                        <ElDropdownItem command="manage">
                          <ElIcon>
                            <Setting />
                          </ElIcon>
                          {t("global.nav_manage")}
                        </ElDropdownItem>
                        <ElDropdownItem command="password">
                          {t("global.change_password")}
                        </ElDropdownItem>
                        <ElDropdownItem command="logout" divided={true}>
                          <ElIcon>
                            <SwitchButton />
                          </ElIcon>
                          {t("global.logout")}
                        </ElDropdownItem>
                      </ElDropdownMenu>
                    ),
                  }}
                />
              ) : (
                <ElButton
                  type="primary"
                  size="small"
                  onClick={() => router.push({ name: "login", query: { redirect: "/media" } })}
                >
                  {t("login.login")}
                </ElButton>
              )}
            </div>
          </div>
        </header>

        <main class="olm-body">
          <RouterView />
        </main>

        <footer class="olm-footer">
          <span class="olm-footer-brand">{settings.siteTitle}</span>
          <span class="olm-footer-sep">·</span>
          <RouterLink to="/@manage">{t("media.footer_manage")}</RouterLink>
        </footer>

        <ElDialog v-model={passwordVisible.value} title={t("global.change_password")} width="360px">
          <ElInput
            v-model={password.value}
            type="password"
            showPassword={true}
            placeholder={t("global.new_password")}
          />
          <div style={{ height: "10px" }} />
          <ElInput
            v-model={confirmPassword.value}
            type="password"
            showPassword={true}
            placeholder={t("global.confirm_password")}
          />
          {{
            footer: () => (
              <div>
                <ElButton onClick={() => (passwordVisible.value = false)}>
                  {t("global.cancel")}
                </ElButton>
                <ElButton
                  type="primary"
                  loading={passwordSaving.value}
                  onClick={() => void submitPassword()}
                >
                  {t("global.confirm")}
                </ElButton>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
