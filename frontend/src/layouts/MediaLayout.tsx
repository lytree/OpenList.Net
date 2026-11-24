import { defineComponent, ref, watch } from "vue"
import { RouterLink, RouterView, useRoute, useRouter } from "vue-router"
import {
  ElButton,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElIcon,
  ElTooltip,
} from "element-plus"
import { Menu, Moon, Setting, Sunny } from "@element-plus/icons-vue"
import { availableLanguages, locale, setLocale, t } from "~/i18n"
import { useSettingsStore } from "~/stores/settings"
import { useUserStore } from "~/stores/user"
import { useTheme } from "~/composables/useTheme"
import MediaNav from "~/pages/media/MediaNav"

/**
 * 媒体门面的外壳（版式参照 Peach）。
 *
 * 与文件门面（`FilesLayout`）**完全独立**：不同的组件、不同的导航、不同的守卫。
 * 理由见 `router.ts` 顶部注释 —— 媒体对游客开放、文件要登录，把两者塞进同一个布局，
 * 每一处都要判断「我现在在哪个门面」，而且游客会在公开页面上看到一个自己进不去的
 * 「文件」入口。
 *
 * **形式**（`peach-mitao/peach` 的壳）：**左侧常驻竖导航 + 右侧宽幅内容**。左栏顶上放品牌，
 * 下面是一列图标 + 文字的栏目，底部是管理台入口；内容区顶部
 * 只有一条轻量工具条（语言 / 主题）。这样排的理由是媒体侧的栏目最多（首页 / 资源库 /
 * 媒体库 / 演员 / 片商 / 系列，登录后还要加想看 / 历史 / 播放列表 / 口味 / 任务），横排到十项时必然退化成
 * 一条要横向滑动的截断列表；竖排则一直挂在那儿，海报墙也不用再让出顶部一整条的高度。
 *
 * **不显示账户**：媒体是对外的公开内容门面，不是工作台。原来右上角那套「头像 + 用户名 +
 * 下拉菜单」既把私人数据摊在公开页面上，又与文件门面互相引流；现在两侧都不越界 —— 登录
 * 后的身份入口只在文件门面（`/files` 的账户菜单），媒体侧只保留游客态的「登录」按钮，
 * 管理台入口在左栏底部与页脚，按用户的页面（想看 / 历史 / 播放列表 / 口味 / 任务）走左栏
 * 导航。窄屏（≤900px）左栏收成抽屉，工具条左侧出现汉堡键。
 */
export default defineComponent({
  name: "MediaLayout",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const settings = useSettingsStore()
    const user = useUserStore()
    const { isDark, toggle } = useTheme()

    /** 窄屏抽屉开合。换页自动收起，否则点完导航抽屉还盖着内容。 */
    const navOpen = ref(false)
    watch(() => route.fullPath, () => (navOpen.value = false))

    return () => (
      <div class="olm-shell">
        {/* 窄屏抽屉的遮罩：桌面端 display:none（断点见 styles/media-shell.scss）。 */}
        <div
          class={["olm-side-mask", navOpen.value && "is-open"]}
          onClick={() => (navOpen.value = false)}
        />

        <aside class={["olm-side", navOpen.value && "is-open"]}>
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

          <div class="olm-side-scroll">
            <MediaNav />
          </div>

          {user.isLogin && (
            <div class="olm-side-foot">
              {/* 底部只放「管理」：它是去改配置，和左栏那条「去看内容」（首页 / 资源库 /
                  媒体库…）不同类。资源库已回到导航列、挨着首页，见 MediaNav。 */}
              <RouterLink to="/@manage" class="olm-side-link" title={t("global.nav_manage")}>
                <ElIcon class="olm-nav-icon">
                  <Setting />
                </ElIcon>
                <span class="olm-nav-label">{t("global.nav_manage")}</span>
              </RouterLink>
            </div>
          )}
        </aside>

        <div class="olm-main">
          <header class="olm-header">
            <div class="olm-header-inner">
              <ElButton
                class="olm-menu-btn"
                text={true}
                circle={false}
                onClick={() => (navOpen.value = !navOpen.value)}
              >
                <ElIcon>
                  <Menu />
                </ElIcon>
              </ElButton>

              {/* 窄屏左栏是抽屉，品牌在这条工具条上补一份（桌面隐藏）。 */}
              <span class="olm-header-brand">{settings.siteTitle}</span>

              <div class="olm-actions">
                <ElDropdown
                  trigger="click"
                  onCommand={(code: string) => setLocale(code)}
                  v-slots={{
                    default: () => (
                      <ElButton text={true} size="small" class="olm-lang">
                        {availableLanguages.find((l) => l.code === locale.value)?.name ??
                          locale.value}
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

                {!user.isLogin && (
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
        </div>
      </div>
    )
  },
})
