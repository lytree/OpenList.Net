import { computed, defineComponent, ref, watch } from "vue"
import { RouterLink, RouterView, useRoute, useRouter } from "vue-router"
import {
  ElAvatar,
  ElButton,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElIcon,
  ElTooltip,
} from "element-plus"
import { ArrowDown, Menu, Moon, Sunny, SwitchButton } from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"
import InboxBell from "~/components/InboxBell"

/**
 * 浏览侧外壳（复刻 OpenList-Frontend 的 `pages/home` 布局）。
 *
 * 旧前端没有左侧导航：顶部是一条约 44px 高的 logo 栏，下面是**居中定宽**的内容列
 * （`min(99%, 980px)`），最后是 "Powered by OpenList | Manage" 页脚。导航（面包屑）、
 * 操作按钮和批量选择条都是浮在内容之上的小挂件，不占版面。
 *
 * 这里的职责只有「顶栏 + 内容列 + 页脚」三件；面包屑与操作条在 `pages/home` 内部，
 * 因为它们依赖文件列表状态。
 */
export default defineComponent({
  name: "BasicLayout",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    const isHome = computed(() => route.name === "home")
    const wide = computed(() => route.meta.wide === true)

    /** 手机端汉堡抽屉：导航与账户操作收进这里，避免顶栏在窄屏挤崩。 */
    const navOpen = ref(false)
    watch(() => route.fullPath, () => (navOpen.value = false))

    const isMedia = computed(() => route.path.startsWith("/media"))

    async function onLogout() {
      navOpen.value = false
      await user.logout()
      await router.push("/")
    }

    function onUserCommand(command: string) {
      if (command === "logout") void onLogout()
      if (command === "manage") void router.push("/@manage")
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

            <RouterLink to="/" class="ol-view-brand" title={settings.siteTitle}>
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
              {/* 媒体库是站点默认落地页（`/` 也指向它），且**游客也能看**，因此对所有
                  人可见；文件浏览（`/files`）需要登录，未登录时不渲染入口 —— 挂在外面
                  只会在点进去时被守卫弹到登录页，等于给游客一个假入口。 */}
              <RouterLink
                to="/media"
                class={["ol-view-link", route.path.startsWith("/media") && "is-active"]}
              >
                {t("global.nav_media")}
              </RouterLink>
              {user.isLogin && (
                <RouterLink to="/files" class={["ol-view-link", isHome.value && "is-active"]}>
                  {t("global.nav_home")}
                </RouterLink>
              )}
            </nav>

            <div class="ol-view-header-right">
              {/* 站内信铃铛：未登录时组件自身返回 null，不占位 */}
              <InboxBell />

              <ElDropdown
                trigger="click"
                onCommand={(code: string) => setLocale(code)}
                v-slots={{
                  default: () => (
                    <ElButton text={true} size="small">
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
                        {user.isAdmin && (
                          <ElDropdownItem command="manage">{t("global.nav_manage")}</ElDropdownItem>
                        )}
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
                <ElButton type="primary" size="small" onClick={() => router.push("/@login")}>
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
          <a href="https://github.com/OpenListTeam/OpenList" target="_blank" rel="noopener">
            {t("home.footer.powered_by")}
          </a>
          <span class="ol-view-footer-sep">|</span>
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
          <RouterLink
            to="/media"
            class={["ol-view-drawer-link", isMedia.value && "is-active"]}
            onClick={() => (navOpen.value = false)}
          >
            {t("global.nav_media")}
          </RouterLink>
          {user.isLogin && (
            <RouterLink
              to="/files"
              class={["ol-view-drawer-link", isHome.value && "is-active"]}
              onClick={() => (navOpen.value = false)}
            >
              {t("global.nav_home")}
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
              {user.isAdmin && (
                <div class="ol-view-drawer-link" onClick={goManage}>
                  {t("global.nav_manage")}
                </div>
              )}
              <div class="ol-view-drawer-link" onClick={() => void onLogout()}>
                {t("global.logout")}
              </div>
            </>
          ) : (
            <ElButton
              type="primary"
              onClick={() => {
                navOpen.value = false
                void router.push("/@login")
              }}
            >
              {t("login.login")}
            </ElButton>
          )}
        </aside>
      </div>
    )
  },
})
