import { defineComponent, ref, onMounted } from "vue"
import { useRouter } from "vue-router"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElIcon,
  ElMessage,
  ElMessageBox,
  ElTable,
  ElTableColumn,
  ElTag,
} from "element-plus"
import { Delete, Edit, Plus, Refresh, VideoPause, VideoPlay } from "@element-plus/icons-vue"
import {
  deleteStorage,
  disableStorage,
  enableStorage,
  listStorages,
  loadAllStorages,
} from "~/api/admin"
import type { StorageWithDriver } from "~/api/types"
import { t } from "~/i18n"

/**
 * 存储管理列表（M2 首个模块）。
 *
 * 表格列来自 `StorageWithDriver`；操作＝编辑 / 启用·禁用 / 删除。
 * 后端 `GET /admin/storage/list` 返回 `{ content, total }`。
 */
export default defineComponent({
  name: "StoragesPage",
  setup() {
    const router = useRouter()
    const list = ref<StorageWithDriver[]>([])
    const loading = ref(false)
    const error = ref("")

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await listStorages()
      loading.value = false
      if (resp.code !== 200) {
        error.value = resp.message
        return
      }
      list.value = resp.data.content
    }
    onMounted(load)

    async function onDelete(row: StorageWithDriver) {
      try {
        await ElMessageBox.confirm(
          t("global.delete_confirm", { name: row.mount_path }),
          t("global.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await deleteStorage(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("global.delete_success"))
      await load()
    }

    async function onToggle(row: StorageWithDriver) {
      const resp = row.disabled ? await enableStorage(row.id) : await disableStorage(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await load()
    }

    async function onReloadAll() {
      const resp = await loadAllStorages()
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("storages.other.start_load_success"))
      await load()
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.sidemenu.storages")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" onClick={onReloadAll}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("storages.other.load_all")}
              </ElButton>
              <ElButton
                size="small"
                type="primary"
                onClick={() => router.push("/@manage/storages/edit")}
              >
                <ElIcon>
                  <Plus />
                </ElIcon>
                {t("global.add")}
              </ElButton>
            </div>
          </div>

          {error.value && (
            <ElAlert type="error" showIcon={true} title={error.value} closable={false} />
          )}

          <ElTable data={list.value} size="small" style={{ width: "100%" }}>
            <ElTableColumn
              label={t("storages.common.mount_path")}
              prop="mount_path"
              minWidth={160}
            />
            <ElTableColumn label={t("storages.common.driver")} width={150}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as StorageWithDriver
                  return <ElTag size="small" type="info">{r.driver_name || r.driver}</ElTag>
                },
              }}
            </ElTableColumn>
            <ElTableColumn label={t("storages.common.order")} prop="order" width={80} />
            <ElTableColumn label={t("storages.common.status")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as StorageWithDriver
                  const off = r.disabled || r.status === "disabled"
                  return (
                    <ElTag size="small" type={off ? "info" : "success"}>
                      {t(`storages.table_fields.status.${off ? "disabled" : "work"}`)}
                    </ElTag>
                  )
                },
              }}
            </ElTableColumn>
            <ElTableColumn
              label={t("storages.common.remark")}
              prop="remark"
              minWidth={140}
              showOverflowTooltip={true}
            />
            <ElTableColumn label={t("global.operations")} width={210} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as StorageWithDriver
                  return (
                    <div class="ol-manage-row-ops">
                      <ElButton
                        size="small"
                        text={true}
                        type="primary"
                        onClick={() => router.push(`/@manage/storages/edit/${r.id}`)}
                      >
                        <ElIcon>
                          <Edit />
                        </ElIcon>
                        {t("global.edit")}
                      </ElButton>
                      <ElButton size="small" text={true} onClick={() => onToggle(r)}>
                        <ElIcon>{r.disabled ? <VideoPlay /> : <VideoPause />}</ElIcon>
                        {r.disabled ? t("global.enable") : t("global.disable")}
                      </ElButton>
                      <ElButton
                        size="small"
                        text={true}
                        type="danger"
                        onClick={() => onDelete(r)}
                      >
                        <ElIcon>
                          <Delete />
                        </ElIcon>
                        {t("global.delete")}
                      </ElButton>
                    </div>
                  )
                },
              }}
            </ElTableColumn>
          </ElTable>
        </ElCard>
      </div>
    )
  },
})
