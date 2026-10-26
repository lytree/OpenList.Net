import { computed, defineComponent, ref } from "vue"
import { RouterView, useRoute, useRouter } from "vue-router"
import {
  ElAvatar,
  ElButton,
  ElContainer,
  ElAside,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElHeader,
  ElIcon,
  ElMain,
  ElMenu,
  ElMenuItem,
  ElTooltip,
} from "element-plus"
import {
  ArrowDown,
  Expand,
  Fold,
  HomeFilled,
  Moon,
  Setting,
  Sunny,
  SwitchButton,
  VideoCamera,
} from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"
import InboxBell from "~/components/InboxBell"

/**
 * 基础外壳：侧边导航 + 顶部栏 + 内容区。
 *
 * 用 `el-container` 而不是自绘 flex 布局，是为了让内置的折叠/响应式行为受控于 EP，
 * 后续接入移动端断点时只需加 `el-drawer` 包裹侧栏，不动内容区结构。
 */
export default defineComponent({
  name: "BasicLayout",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    const collapsed = ref(false)

    const activeMenu = computed(() => route.path)
    const asideWidth = computed(() => (collapsed.value ? "64px" : "220px"))

    // meta.title 存的是 i18n key（也可能是字面量，t() 找不到时原样返回）
    const pageTitle = computed(() => (route.meta.title ? t(route.meta.title) : ""))

    const navItems = computed(() => {
      const items = [
        { path: "/", label: t("global.nav_home"), icon: HomeFilled },
        { path: "/media", label: t("global.nav_media"), icon: VideoCamera },
      ]
      if (user.isAdmin) {
        items.push({ path: "/@manage", label: t("global.nav_manage"), icon: Setting })
      }
      return items
    })

    async function onLogout() {
      await user.logout()
      await router.push("/")
    }

    function onUserCommand(command: string) {
      if (command === "logout") void onLogout()
      if (command === "manage") void router.push("/@manage")
    }

    const renderNavItem = (item: { path: string; label: string; icon: unknown }) => {
      const Icon = item.icon as typeof HomeFilled
      return (
        <ElMenuItem index={item.path}>
          <ElIcon>
            <Icon />
          </ElIcon>
          <span>{item.label}</span>
        </ElMenuItem>
      )
    }

    return () => (
      <ElContainer class="ol-layout" style={{ height: "100vh" }}>
        <ElAside width={asideWidth.value} class="ol-aside">
          <div class="ol-brand">
            {settings.logo ? (
              <img class="ol-brand-logo" src={settings.logo} alt={settings.siteTitle} />
            ) : (
              <div class="ol-brand-mark">{(settings.siteTitle || "O").slice(0, 1)}</div>
            )}
            {!collapsed.value && <span class="ol-brand-title">{settings.siteTitle}</span>}
          </div>

          <ElMenu
            defaultActive={activeMenu.value}
            collapse={collapsed.value}
            router={true}
            class="ol-menu"
          >
            {navItems.value.map(renderNavItem)}
          </ElMenu>

          <div class="ol-aside-footer">
            <ElButton
              text={true}
              class="ol-collapse-btn"
              onClick={() => (collapsed.value = !collapsed.value)}
            >
              <ElIcon>{collapsed.value ? <Expand /> : <Fold />}</ElIcon>
            </ElButton>
          </div>
        </ElAside>

        <ElContainer>
          <ElHeader class="ol-header" height="56px">
            <div class="ol-header-title">{pageTitle.value}</div>
            <div class="ol-header-actions">
              {/* 站内信铃铛：未登录时组件自身返回 null，不占位 */}
              <InboxBell />

              <ElDropdown
                trigger="click"
                onCommand={(code: string) => setLocale(code)}
                v-slots={{
                  default: () => (
                    <ElButton text={true}>
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
                        <ElAvatar size={28}>
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
                <ElButton type="primary" onClick={() => router.push("/@login")}>
                  {t("login.login")}
                </ElButton>
              )}
            </div>
          </ElHeader>

          <ElMain class="ol-main">
            <RouterView />
          </ElMain>
        </ElContainer>
      </ElContainer>
    )
  },
})
