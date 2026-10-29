import { defineComponent, ref, watch } from "vue"
import { RouterLink, RouterView, useRoute, useRouter } from "vue-router"
import {
  ElButton,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElIcon,
  ElMenu,
  ElMenuItem,
  ElTooltip,
} from "element-plus"
import {
  ChatDotRound,
  Coin,
  Collection,
  FolderOpened,
  HomeFilled,
  InfoFilled,
  Link,
  List,
  Menu,
  Moon,
  Odometer,
  Search,
  Setting,
  Share,
  Sunny,
  SwitchButton,
  User,
  VideoCamera,
} from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"

/**
 * 管理台外壳（复刻 OpenList-Frontend `pages/manage/index.tsx`）。
 *
 * 旧前端的管理台是**整页独立**的：顶部 64px 标题栏（左侧菜单按钮 + "管理" 标题，
 * 右侧退出登录），左侧 14rem 图标侧栏（阴影、可滚动、底部放语言/主题开关），
 * 右侧内容区独立滚动。它不套在浏览页的外壳里，也不显示 logo。
 *
 * 菜单顺序与图标对齐旧前端；`docs` 指向官方文档，`home` 整页刷新回浏览页。
 */
type ModuleItem = {
  key: string
  path: string
  icon: unknown
}

const MODULES: ModuleItem[] = [
  { key: "dashboard", path: "/@manage/dashboard", icon: Odometer },
  { key: "media", path: "/@manage/media", icon: VideoCamera },
  { key: "settings", path: "/@manage/settings", icon: Setting },
  { key: "tasks", path: "/@manage/tasks", icon: List },
  { key: "users", path: "/@manage/users", icon: User },
  { key: "storages", path: "/@manage/storages", icon: Coin },
  { key: "shares", path: "/@manage/shares", icon: Share },
  { key: "metas", path: "/@manage/metas", icon: Collection },
  { key: "indexes", path: "/@manage/indexes", icon: Search },
  { key: "messenger", path: "/@manage/messenger", icon: ChatDotRound },
  { key: "backup-restore", path: "/@manage/backup-restore", icon: FolderOpened },
  { key: "about", path: "/@manage/about", icon: InfoFilled },
]

const EXTERNALS: ModuleItem[] = [{ key: "docs", path: "https://doc.oplist.org", icon: Link }]

export default defineComponent({
  name: "ManageLayout",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    /** 窄屏（< 900px）下侧栏改为抽屉，点菜单项后自动收起。 */
    const asideOpen = ref(false)
    watch(() => route.fullPath, () => (asideOpen.value = false))

    async function onLogout() {
      await user.logout()
      await router.push({ path: "/@login", query: { redirect: route.fullPath } })
    }

    const renderItem = (item: ModuleItem) => {
      const Icon = item.icon as typeof Setting
      return (
        <ElMenuItem index={item.path}>
          <ElIcon class="ol-manage-item-icon">
            <Icon />
          </ElIcon>
          <span>{t(`manage.sidemenu.${item.key}`)}</span>
        </ElMenuItem>
      )
    }

    return () => (
      <div class="ol-manage">
        <header class="ol-manage-header">
          <div class="ol-manage-header-left">
            <ElButton
              class="ol-manage-burger"
              text={true}
              circle={true}
              onClick={() => (asideOpen.value = !asideOpen.value)}
            >
              <ElIcon>
                <Menu />
              </ElIcon>
            </ElButton>
            <h1 class="ol-manage-title" onClick={() => router.push("/@manage")}>
              {t("manage.title")}
            </h1>
            <span class="ol-manage-site">{settings.siteTitle}</span>
          </div>

          <div class="ol-manage-header-right">
            <ElTooltip content={t("manage.sidemenu.home")} placement="bottom">
              <ElButton text={true} circle={true} onClick={() => router.push("/")}>
                <ElIcon>
                  <HomeFilled />
                </ElIcon>
              </ElButton>
            </ElTooltip>
            <ElTooltip content={t("global.logout")} placement="bottom">
              <ElButton text={true} circle={true} onClick={() => void onLogout()}>
                <ElIcon>
                  <SwitchButton />
                </ElIcon>
              </ElButton>
            </ElTooltip>
          </div>
        </header>

        <div class="ol-manage-body">
          {asideOpen.value && (
            <div class="ol-manage-scrim" onClick={() => (asideOpen.value = false)} />
          )}

          <aside class={["ol-manage-side", asideOpen.value && "is-open"]}>
            <ElMenu
              class="ol-manage-menu"
              defaultActive={route.path}
              collapse={false}
              router={true}
            >
              {MODULES.map(renderItem)}
            </ElMenu>

            <div class="ol-manage-extra">
              <div class="ol-manage-sep" />
              {EXTERNALS.map((item) => (
                <a class="ol-manage-item" href={item.path} target="_blank" rel="noopener">
                  <ElIcon class="ol-manage-item-icon">
                    <Link />
                  </ElIcon>
                  <span>{t(`manage.sidemenu.${item.key}`)}</span>
                </a>
              ))}
              <RouterLink to="/" class="ol-manage-item">
                <ElIcon class="ol-manage-item-icon">
                  <HomeFilled />
                </ElIcon>
                <span>{t("manage.sidemenu.home")}</span>
              </RouterLink>
            </div>

            <div class="ol-manage-side-foot">
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
              <ElButton text={true} circle={true} size="small" onClick={toggle}>
                <ElIcon>{isDark.value ? <Sunny /> : <Moon />}</ElIcon>
              </ElButton>
            </div>
          </aside>

          <main class="ol-manage-content">
            <RouterView />
          </main>
        </div>
      </div>
    )
  },
})
