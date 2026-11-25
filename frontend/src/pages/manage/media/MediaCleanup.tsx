import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRouter } from "vue-router"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElIcon,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElSelect,
  ElTag,
} from "element-plus"
import { ArrowLeft, Delete, Refresh } from "@element-plus/icons-vue"
import { VexTable, VexColumn } from "~/components/table"
import {
  mediaDeleteDuplicates,
  mediaDeleteOrphans,
  mediaEmptyTrash,
  mediaListDuplicates,
  mediaListLibraries,
  mediaListOrphans,
  type DuplicateGroup,
  type MediaLibrary,
  type OrphanItem,
} from "~/api/media"
import { humanSize } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 媒体库数据清理。
 *
 * 两块：**孤儿条目**（文件已全部消失，只剩数据库行）和**重复文件**（同大小同时长，
 * 再用前 1 MB 哈希确认）。两块的交互都是「先列出来 → 用户看清楚 → 再删」，
 * 后端不提供一键盲删：误删文件的代价远高于多点一次。
 *
 * 删除口径由库决定：
 * - 孤儿只删数据库行（文件本来就不在了）；
 * - 重复 / 单条删除会碰文件，库配了回收站就移进去，否则直接删。
 *
 * 重复组内提供「保留最早入库的一条，删掉其余」的批量动作 —— 这是清理重复时
 * 最常用的一步，逐个点太慢，而「全部删掉」又会把片子删空。
 */
export default defineComponent({
  name: "MediaCleanupPage",
  setup() {
    const router = useRouter()

    const libraries = ref<MediaLibrary[]>([])
    /** 0 = 全部可见库。 */
    const libraryId = ref(0)
    const orphans = ref<OrphanItem[]>([])
    const duplicates = ref<DuplicateGroup[]>([])
    const loading = ref(false)
    const busy = ref(false)
    const error = ref("")

    const currentLibrary = computed(() => libraries.value.find((l) => l.id === libraryId.value) ?? null)
    /** 只有选定单个库、且该库配了回收站，才允许清空回收站。 */
    const canEmptyTrash = computed(() => Boolean(currentLibrary.value?.trash_directory))

    async function loadLibraries() {
      const resp = await mediaListLibraries()
      if (resp.code === 200) libraries.value = resp.data ?? []
    }

    async function load() {
      loading.value = true
      error.value = ""
      try {
        const [orphanResp, dupResp] = await Promise.all([
          mediaListOrphans(libraryId.value),
          mediaListDuplicates(libraryId.value),
        ])
        if (orphanResp.code !== 200 || dupResp.code !== 200) {
          error.value = orphanResp.code !== 200 ? orphanResp.message : dupResp.message
          orphans.value = []
          duplicates.value = []
          return
        }
        orphans.value = orphanResp.data ?? []
        duplicates.value = dupResp.data ?? []
      } finally {
        loading.value = false
      }
    }

    onMounted(async () => {
      await Promise.all([loadLibraries(), load()])
    })
    watch(libraryId, () => void load())

    /** 统一的「确认 → 执行 → 重新拉清单」流程。 */
    async function run(action: () => Promise<{ code: number; message: string }>, successText: string) {
      busy.value = true
      try {
        const resp = await action()
        if (resp.code !== 200) {
          ElMessage.error(resp.message)
          return
        }
        ElMessage.success(successText)
        await load()
      } finally {
        busy.value = false
      }
    }

    async function removeOrphan(row: OrphanItem) {
      try {
        await ElMessageBox.confirm(
          t("media.cleanup.confirm_orphan", { name: row.title }),
          t("media.cleanup.delete_orphan"),
          { type: "warning" },
        )
      } catch {
        return
      }
      await run(() => mediaDeleteOrphans([row.item_id]), t("media.deleted"))
    }

    async function removeAllOrphans() {
      if (orphans.value.length === 0) return
      try {
        await ElMessageBox.confirm(
          t("media.cleanup.confirm_orphan_all", { count: orphans.value.length }),
          t("media.cleanup.delete_all_orphans"),
          { type: "warning" },
        )
      } catch {
        return
      }
      await run(
        () => mediaDeleteOrphans(orphans.value.map((o) => o.item_id)),
        t("media.deleted"),
      )
    }

    /** 组内保留第一个条目（列表已按标题排序，取第一条最直观），删掉其余。 */
    async function removeGroupExtras(group: DuplicateGroup) {
      const extras = group.items.slice(1)
      if (extras.length === 0) return
      try {
        await ElMessageBox.confirm(
          t("media.cleanup.confirm_duplicate_group", { count: extras.length, name: group.items[0].title }),
          t("media.cleanup.delete_duplicate"),
          { type: "warning" },
        )
      } catch {
        return
      }
      await run(
        () => mediaDeleteDuplicates(extras.map((i) => i.item_id)),
        t("media.deleted"),
      )
    }

    async function removeDuplicateItem(item: DuplicateItemLike) {
      try {
        await ElMessageBox.confirm(
          t("media.cleanup.confirm_duplicate", { name: item.title }),
          t("media.cleanup.delete_duplicate"),
          { type: "warning" },
        )
      } catch {
        return
      }
      await run(() => mediaDeleteDuplicates([item.item_id]), t("media.deleted"))
    }

    async function emptyTrash() {
      const lib = currentLibrary.value
      if (!lib) return
      try {
        await ElMessageBox.confirm(
          t("media.cleanup.confirm_empty_trash", { name: lib.name, path: lib.trash_directory ?? "" }),
          t("media.cleanup.empty_trash"),
          { type: "warning" },
        )
      } catch {
        return
      }
      await run(() => mediaEmptyTrash(lib.id), t("media.cleanup.trash_emptied"))
    }

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("media.cleanup.title")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" onClick={() => router.push("/@manage/media")}>
                <ElIcon>
                  <ArrowLeft />
                </ElIcon>
                {t("media.cleanup.back")}
              </ElButton>
              <ElSelect
                modelValue={libraryId.value}
                onUpdate:modelValue={(v: number) => (libraryId.value = v)}
                size="small"
                style={{ width: "180px" }}
                placeholder={t("media.cleanup.all_libraries")}
              >
                <ElOption label={t("media.cleanup.all_libraries")} value={0} />
                {libraries.value.map((lib) => (
                  <ElOption key={lib.id} label={lib.name} value={lib.id} />
                ))}
              </ElSelect>
              <ElButton size="small" loading={loading.value} onClick={() => void load()}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("media.cleanup.scan")}
              </ElButton>
            </div>
          </div>

          <ElAlert
            type="info"
            showIcon={true}
            closable={false}
            class="ol-manage-tip"
            title={t("media.cleanup.hint")}
          />

          {error.value && <ElAlert type="error" showIcon={true} closable={false} title={error.value} />}

          {/* ---------- 孤儿条目 ---------- */}
          <h3 class="ol-manage-h3">
            {t("media.cleanup.orphans")}
            <ElTag size="small" type="info" class="ol-cleanup-count">
              {orphans.value.length}
            </ElTag>
            {orphans.value.length > 0 && (
              <ElButton
                size="small"
                type="danger"
                plain={true}
                loading={busy.value}
                class="ol-cleanup-section-action"
                onClick={() => void removeAllOrphans()}
              >
                {t("media.cleanup.delete_all_orphans")}
              </ElButton>
            )}
          </h3>
          {orphans.value.length === 0 ? (
            <p class="ol-field-help">{t("media.cleanup.no_orphans")}</p>
          ) : (
            <VexTable data={orphans.value} size="small" style={{ width: "100%" }}>
              <VexColumn label={t("media.cleanup.col_title")} minWidth={160} showOverflowTooltip={true}>
                {{
                  default: ({ row }) => {
                    const r = row as OrphanItem
                    return <strong>{r.title}</strong>
                  },
                }}
              </VexColumn>
              <VexColumn label={t("media.field_path")} minWidth={240} showOverflowTooltip={true}>
                {{
                  default: ({ row }) => <span class="ol-mono">{(row as OrphanItem).directory_path}</span>,
                }}
              </VexColumn>
              <VexColumn label={t("media.cleanup.col_library")} width={130}>
                {{
                  default: ({ row }) => (row as OrphanItem).library_name,
                }}
              </VexColumn>
              <VexColumn label={t("media.cleanup.col_missing_files")} width={110} align="center">
                {{
                  default: ({ row }) => (row as OrphanItem).missing_files,
                }}
              </VexColumn>
              <VexColumn label={t("media.cleanup.col_actions")} width={100} align="right">
                {{
                  default: ({ row }) => (
                    <ElButton
                      size="small"
                      type="danger"
                      text={true}
                      loading={busy.value}
                      onClick={() => void removeOrphan(row as OrphanItem)}
                    >
                      <ElIcon>
                        <Delete />
                      </ElIcon>
                    </ElButton>
                  ),
                }}
              </VexColumn>
            </VexTable>
          )}

          {/* ---------- 重复文件 ---------- */}
          <h3 class="ol-manage-h3">
            {t("media.cleanup.duplicates")}
            <ElTag size="small" type="info" class="ol-cleanup-count">
              {duplicates.value.length}
            </ElTag>
          </h3>
          {duplicates.value.length === 0 ? (
            <p class="ol-field-help">{t("media.cleanup.no_duplicates")}</p>
          ) : (
            <div class="ol-cleanup-groups">
              {duplicates.value.map((group, index) => (
                <div class="ol-cleanup-group" key={`${group.size_bytes}-${index}`}>
                  <div class="ol-cleanup-group-head">
                    <span class="ol-cleanup-group-title">
                      {t("media.cleanup.group_summary", {
                        size: humanSize(group.size_bytes),
                        count: group.count,
                      })}
                    </span>
                    {group.items.length > 1 && (
                      <ElButton
                        size="small"
                        type="danger"
                        plain={true}
                        loading={busy.value}
                        onClick={() => void removeGroupExtras(group)}
                      >
                        {t("media.cleanup.keep_first")}
                      </ElButton>
                    )}
                  </div>
                  <VexTable data={group.items} size="small" style={{ width: "100%" }}>
                    <VexColumn label={t("media.cleanup.col_title")} minWidth={140} showOverflowTooltip={true}>
                      {{
                        default: ({ row }) => <strong>{(row as DuplicateItemLike).title}</strong>,
                      }}
                    </VexColumn>
                    <VexColumn label={t("media.cleanup.col_file")} minWidth={240} showOverflowTooltip={true}>
                      {{
                        default: ({ row }) => <span class="ol-mono">{(row as DuplicateItemLike).file_path}</span>,
                      }}
                    </VexColumn>
                    <VexColumn label={t("media.cleanup.col_quality")} width={140}>
                      {{
                        default: ({ row }) => {
                          const r = row as DuplicateItemLike
                          const parts = [r.resolution, r.duration_seconds ? `${Math.round(r.duration_seconds / 60)} min` : null]
                          return parts.filter(Boolean).join(" · ") || "-"
                        },
                      }}
                    </VexColumn>
                    <VexColumn label={t("media.cleanup.col_actions")} width={100} align="right">
                      {{
                        default: ({ row }) => (
                          <ElButton
                            size="small"
                            type="danger"
                            text={true}
                            loading={busy.value}
                            onClick={() => void removeDuplicateItem(row as DuplicateItemLike)}
                          >
                            <ElIcon>
                              <Delete />
                            </ElIcon>
                          </ElButton>
                        ),
                      }}
                    </VexColumn>
                  </VexTable>
                </div>
              ))}
            </div>
          )}

          {/* ---------- 回收站 ---------- */}
          <h3 class="ol-manage-h3">{t("media.cleanup.trash")}</h3>
          {canEmptyTrash.value ? (
            <div class="ol-cleanup-trash">
              <span class="ol-field-help">
                {t("media.cleanup.trash_at", { path: currentLibrary.value?.trash_directory ?? "" })}
              </span>
              <ElButton size="small" type="danger" plain={true} loading={busy.value} onClick={() => void emptyTrash()}>
                {t("media.cleanup.empty_trash")}
              </ElButton>
            </div>
          ) : (
            <p class="ol-field-help">{t("media.cleanup.trash_unavailable")}</p>
          )}
        </ElCard>
      </div>
    )
  },
})

/** 组内条目与单条删除共用同一形状（`DuplicateItem` 的字段子集）。 */
type DuplicateItemLike = {
  item_id: number
  title: string
  file_path: string
  resolution?: string | null
  duration_seconds?: number | null
}
