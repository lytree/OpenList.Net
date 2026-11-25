import { computed, defineComponent, ref, watch } from "vue"
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
  Coin,
  Collection,
  Film,
  FolderOpened,
  HomeFilled,
  InfoFilled,
  List,
  Menu,
  Moon,
  Odometer,
  Search,
  Setting,
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
 * **与旧前端的差别**（按需求收敛）：
 * - 侧栏底部收拢**三条跳转**：首页（`/media`，与站点根路径 `/` 的默认落地一致）、
 *   媒体库（`/media/library`）、资源库（`/files`）。它们都是"离开管理台去看内容"，
 *   与上面那列管理模块不同类；原来的「首页 → /files」改名成资源库（落点本来就是文件树）。
 * - 语言 / 主题从侧栏底部搬到**右上角**：它们是"随手切一下"的全局开关，标题栏才是一直
 *   可见的那条；标题栏右侧不再重复放首页按钮（底部已有）。
 * - 不再提供外链「文档」（`https://doc.oplist.org`）：那一栏在自建部署里多半指向别人的
 *   站点，留着只会误导。
 *
 * 菜单顺序与图标对齐旧前端。
 */
type ModuleItem = {
  key: string
  path: string
  icon: unknown
}

const MODULES: ModuleItem[] = [
  { key: "dashboard", path: "/@manage/dashboard", icon: Odometer },
  { key: "users", path: "/@manage/users", icon: User },
  { key: "media", path: "/@manage/media", icon: VideoCamera },
  { key: "settings", path: "/@manage/settings", icon: Setting },
  { key: "tasks", path: "/@manage/tasks", icon: List },
  { key: "storages", path: "/@manage/storages", icon: Coin },
  { key: "metas", path: "/@manage/metas", icon: Collection },
  { key: "indexes", path: "/@manage/indexes", icon: Search },
  { key: "backup-restore", path: "/@manage/backup-restore", icon: FolderOpened },
  { key: "about", path: "/@manage/about", icon: InfoFilled },
]

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

    /**
     * 侧栏高亮项。子页面（如 `/@manage/media-cleanup`）不在 MODULES 里，
     * 直接拿 `route.path` 会让整个侧栏都没有选中态，所以按「最长前缀」归到父模块。
     */
    const activeModule = computed(() => {
      const hit = MODULES
        .filter((m) => route.path === m.path || route.path.startsWith(m.path))
        .sort((a, b) => b.path.length - a.path.length)[0]
      return hit?.path ?? route.path
    })
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
            {/* 语言 / 主题：全局开关，跟着标题栏常驻。窄屏（≤600px）收起语言，主题留一枚图标。 */}
            <ElDropdown
              trigger="click"
              onCommand={(code: string) => setLocale(code)}
              v-slots={{
                default: () => (
                  <ElButton text={true} size="small" class="ol-manage-lang">
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
              defaultActive={activeModule.value}
              collapse={false}
              router={true}
            >
              {MODULES.map(renderItem)}
            </ElMenu>

            <div class="ol-manage-side-foot">
              {/* 侧栏底部的三条**跳转**（不是管理页）：首页去内容站落地页、媒体库去媒体库
                  页、资源库去文件树。它们是同一个问题的几种视角 —— 「媒体首页的聚合推荐」
                  「刮削后的条目」「磁盘上的原貌」；也正因此不和上面的管理模块混成一列。
                  首页指向 `/media`（与站点根路径 `/` 的默认落地一致），不是 `/@manage`。
                  注意与菜单里的「媒体库」（`/@manage/media`，管库的增删刮削）区分：
                  那一条是改配置，这几条是去看东西。外链「文档」已移除。 */}
              <RouterLink to="/media" class="ol-manage-item" title={t("media.tab_home")}>
                <ElIcon class="ol-manage-item-icon">
                  <HomeFilled />
                </ElIcon>
                <span>{t("media.tab_home")}</span>
              </RouterLink>
              <RouterLink
                to="/media/library"
                class="ol-manage-item"
                title={t("media.tab_libraries")}
              >
                <ElIcon class="ol-manage-item-icon">
                  <Film />
                </ElIcon>
                <span>{t("media.tab_libraries")}</span>
              </RouterLink>
              <RouterLink to="/files" class="ol-manage-item" title={t("media.tab_resources")}>
                <ElIcon class="ol-manage-item-icon">
                  <FolderOpened />
                </ElIcon>
                <span>{t("media.tab_resources")}</span>
              </RouterLink>
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
