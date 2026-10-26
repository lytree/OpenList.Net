import { defineComponent } from "vue"
import MilestonePlaceholder from "~/components/MilestonePlaceholder"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 管理台入口。
 *
 * 完整管理台有 12 个模块（见方案 A–H 验收表），M2 交付。
 * 路由 meta.admin 已在守卫里拦非管理员，这里只做入口占位。
 */
export default defineComponent({
  name: "ManagePage",
  setup() {
    const user = useUserStore()
    return () => (
      <MilestonePlaceholder
        title={t("manage.title")}
        milestone="M2"
        points={[
          `当前账号：${user.username || "-"}（${user.isAdmin ? "admin" : "general"}）`,
          "存储管理（含驱动的动态表单）",
          "用户 / 角色 / 权限位编辑",
          "站点设置（样式、预览、全局、SSO、LDAP、S3、FTP…）",
          "任务中心、索引构建、仪表盘",
        ]}
      />
    )
  },
})
