import { defineComponent } from "vue"
import { RouterView, useRoute } from "vue-router"
import { ElAside, ElContainer, ElMain, ElMenu, ElMenuItem } from "element-plus"
import { t } from "~/i18n"

/**
 * 管理台外壳（M2）。
 *
 * 左侧 12 模块子导航（仅 Storages 已实现，其余标 `soon` 并禁用），右侧渲染
 * 嵌套路由（`/@manage/storages`、`/@manage/storages/edit/:id`）。整体仍套在
 * `BasicLayout` 之外壳内，故这里只是二级内容区布局。
 */
const MODULES: { labelKey: string; path: string; ready: boolean }[] = [
  { labelKey: "storages", path: "/@manage/storages", ready: true },
  { labelKey: "users", path: "/@manage/users", ready: true },
  { labelKey: "tasks", path: "/@manage/tasks", ready: true },
  { labelKey: "media", path: "/@manage/media", ready: true },
  { labelKey: "shares", path: "/@manage/shares", ready: true },
  { labelKey: "metas", path: "/@manage/metas", ready: true },
  { labelKey: "indexes", path: "/@manage/indexes", ready: true },
  { labelKey: "plugins", path: "/@manage/plugins", ready: false },
  { labelKey: "settings", path: "/@manage/settings", ready: true },
  { labelKey: "backup-restore", path: "/@manage/backup-restore", ready: false },
  { labelKey: "dashboard", path: "/@manage/dashboard", ready: false },
  { labelKey: "about", path: "/@manage/about", ready: false },
]

export default defineComponent({
  name: "ManageLayout",
  setup() {
    const route = useRoute()
    return () => (
      <ElContainer class="ol-manage">
        <ElAside width="200px" class="ol-manage-aside">
          <ElMenu defaultActive={route.path} router={true} class="ol-manage-menu">
            {MODULES.map((m) => (
              <ElMenuItem index={m.path} disabled={!m.ready}>
                <span>
                  {t(`manage.sidemenu.${m.labelKey}`)}
                  {!m.ready ? " (soon)" : ""}
                </span>
              </ElMenuItem>
            ))}
          </ElMenu>
        </ElAside>
        <ElMain class="ol-manage-main">
          <RouterView />
        </ElMain>
      </ElContainer>
    )
  },
})
