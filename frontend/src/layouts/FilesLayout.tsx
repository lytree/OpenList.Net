import { computed, defineComponent, ref, watch } from "vue"
import { RouterLink, RouterView, useRoute, useRouter } from "vue-router"
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
import { ArrowDown, Menu, Moon, Sunny, SwitchButton } from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { updateMe } from "~/api/auth"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"

/**
 * 文件门面的外壳（原来的 `BasicLayout`）。
 *
 * 复刻 OpenList-Frontend 的 `pages/home` 布局：顶部一条 ~44px 的 logo 栏，下面是
 * **居中定宽**的内容列（`min(99%, 980px)`），最后是仅含「管理 / 登录」入口的页脚。
 * 导航（面包屑）、操作按钮和批量选择条都是浮在内容之上的小挂件，不占版面 ——
 * 文件浏览器的主视觉必须是列表本身。
 *
 * **与媒体门面（`MediaLayout`）完全隔离**：这里不再有「媒体 / 首页」并排的双入口。
 * 文件树是需要登录的私人门面，媒体是对外的公开门面，两者的导航语义不同；
 * 返回媒体的入口收在顶栏左侧一个「媒体」链接里，游客则根本看不到本布局。
 */
export default defineComponent({
  name: "FilesLayout",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    const wide = computed(() => route.meta.wide === true)

    /** 手机端汉堡抽屉：导航与账户操作收进这里，避免顶栏在窄屏挤崩。 */
    const navOpen = ref(false)
    watch(() => route.fullPath, () => (navOpen.value = false))

    /** 顶栏「媒体」入口：媒体是公开门面，登录用户与可访问媒体的游客都看得到。 */
    const showMediaLink = computed(() => user.isLogin || settings.mediaGuestAccess)

    async function onLogout() {
      navOpen.value = false
      await user.logout()
      // 退出后文件树立刻不可见，落到公开的媒体首页。
      await router.push("/media")
    }

    function onUserCommand(command: string) {
      if (command === "logout") void onLogout()
      if (command === "manage") void router.push("/@manage")
      if (command === "password") {
        navOpen.value = false
        password.value = ""
        confirmPassword.value = ""
        passwordVisible.value = true
      }
    }

    /** 单用户产品只保留「修改自己的密码」，不再有用户管理页。 */
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

    function goManage() {
      navOpen.value = false
      void router.push("/@manage")
    }

    return () => (
      <div class="ol-view">
        <header class="ol-view-header el-header">
          <div class="ol-view-header-inner">
            <ElButton
              class="ol-view-menu-btn"
              text={true}
              circle={true}
              onClick={() => (navOpen.value = !navOpen.value)}
            >
              <ElIcon>
                <Menu />
              </ElIcon>
            </ElButton>

            <RouterLink to="/files" class="ol-view-brand" title={settings.siteTitle}>
              {settings.logo ? (
                <img class="ol-view-logo" src={settings.logo} alt={settings.siteTitle} />
              ) : (
                // 没配 logo 时旧前端会渲染一个空的 <img>（页头一片空白）。这里退化成
                // 「首字母方块 + 站点名」，至少有东西可点、站点身份也一目了然。
                <>
                  <span class="ol-view-logo-mark">{(settings.siteTitle || "O").slice(0, 1)}</span>
                  <span class="ol-view-site">{settings.siteTitle}</span>
                </>
              )}
            </RouterLink>

            <nav class="ol-view-links">
              {/* 只留一个「媒体」出口：文件门面里不需要再确认「你在文件门面」。 */}
              {showMediaLink.value && (
                <RouterLink to="/media" class="ol-view-link">
                  {t("global.nav_media")}
                </RouterLink>
              )}
            </nav>

            <div class="ol-view-header-right">
              <ElDropdown
                trigger="click"
                onCommand={(code: string) => setLocale(code)}
                v-slots={{
                  default: () => (
                    <ElButton text={true} size="small" class="ol-view-lang">
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
                <ElButton text={true} circle={true} onClick={toggle}>
                  <ElIcon>{isDark.value ? <Sunny /> : <Moon />}</ElIcon>
                </ElButton>
              </ElTooltip>

              {user.isLogin ? (
                <ElDropdown
                  trigger="click"
                  onCommand={onUserCommand}
                  v-slots={{
                    default: () => (
                      <span class="ol-user">
                        <ElAvatar size={26}>
                          {(user.username || "?").slice(0, 1).toUpperCase()}
                        </ElAvatar>
                        <span class="ol-user-name">{user.username}</span>
                        <ElIcon size={12}>
                          <ArrowDown />
                        </ElIcon>
                      </span>
                    ),
                    dropdown: () => (
                      <ElDropdownMenu>
                        <ElDropdownItem command="manage">{t("global.nav_manage")}</ElDropdownItem>
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
                  onClick={() => router.push({ name: "login", query: { redirect: encodeURIComponent(route.fullPath) } })}
                >
                  {t("login.login")}
                </ElButton>
              )}
            </div>
          </div>
        </header>

        <main class={["ol-view-body", wide.value && "is-wide"]}>
          <div class="ol-view-inner">
            <RouterView />
          </div>
        </main>

        <footer class="ol-view-footer">
          <RouterLink to={user.isLogin ? "/@manage" : "/@login"}>
            {user.isLogin ? t("home.footer.manage") : t("login.login")}
          </RouterLink>
        </footer>

        {/* 手机端汉堡抽屉（断点见 styles/responsive.css）。桌面端这两个节点 display:none。 */}
        <div
          class={["ol-view-drawer-mask", navOpen.value && "is-open"]}
          onClick={() => (navOpen.value = false)}
        />
        <aside class={["ol-view-drawer", navOpen.value && "is-open"]}>
          {showMediaLink.value && (
            <RouterLink
              to="/media"
              class="ol-view-drawer-link"
              onClick={() => (navOpen.value = false)}
            >
              {t("global.nav_media")}
            </RouterLink>
          )}

          <div class="ol-view-drawer-sep" />

          <div class="ol-view-drawer-section">{t("global.language")}</div>
          {availableLanguages.map((lang) => (
            <button
              key={lang.code}
              class="ol-view-drawer-link"
              onClick={() => {
                setLocale(lang.code)
                navOpen.value = false
              }}
              style={{
                border: "none",
                background:
                  lang.code === locale.value ? "var(--el-color-primary-light-9)" : "transparent",
                color: lang.code === locale.value ? "var(--el-color-primary)" : "var(--ol-text)",
                textAlign: "left",
                cursor: "pointer",
                font: "inherit",
              }}
            >
              {lang.name}
            </button>
          ))}

          <div class="ol-view-drawer-row" style={{ padding: "10px 12px" }}>
            <span style={{ fontSize: "14px", color: "var(--ol-text)" }}>{t("home.toolbar.toggle_theme")}</span>
            <ElButton text={true} circle={true} onClick={toggle}>
              <ElIcon>{isDark.value ? <Sunny /> : <Moon />}</ElIcon>
            </ElButton>
          </div>

          <div class="ol-view-drawer-sep" />

          {user.isLogin ? (
            <>
              <div class="ol-view-drawer-user">
                <ElAvatar size={28}>
                  {(user.username || "?").slice(0, 1).toUpperCase()}
                </ElAvatar>
                <span class="ol-user-name">{user.username}</span>
              </div>
              {user.isLogin && (
                <div class="ol-view-drawer-link" onClick={goManage}>
                  {t("global.nav_manage")}
                </div>
              )}
              <div
                class="ol-view-drawer-link"
                onClick={() => {
                  navOpen.value = false
                  password.value = ""
                  confirmPassword.value = ""
                  passwordVisible.value = true
                }}
              >
                {t("global.change_password")}
              </div>
              <div class="ol-view-drawer-link" onClick={() => void onLogout()}>
                {t("global.logout")}
              </div>
            </>
          ) : (
            <ElButton
              type="primary"
              onClick={() => {
                navOpen.value = false
                void router.push({ name: "login", query: { redirect: encodeURIComponent(route.fullPath) } })
              }}
            >
              {t("login.login")}
            </ElButton>
          )}
        </aside>

        <ElDialog
          v-model={passwordVisible.value}
          title={t("global.change_password")}
          width="360px"
        >
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
