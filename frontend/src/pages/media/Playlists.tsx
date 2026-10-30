import { defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import {
  ElButton,
  ElDialog,
  ElEmpty,
  ElForm,
  ElFormItem,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
} from "element-plus"
import { ArrowLeft, Delete, Edit, Plus } from "@element-plus/icons-vue"
import {
  mediaCreatePlaylist,
  mediaDeletePlaylist,
  mediaImageLink,
  mediaListPlaylists,
  mediaUpdatePlaylist,
  type MediaPlaylistSummary,
} from "~/api/media"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 播放列表页 —— `/media/playlists`。
 *
 * 列表是**用户私有**资源（后端按 UserId 隔离），所以这里没有「别人的列表」的概念，
 * 也不需要权限判断：能进到这一页就一定能改自己的列表。
 *
 * 封面用 `preview_item_ids`（后端最多给 5 个）拼四宫格，而不是逐条取详情：
 * 列表页可能有几十个列表，每个列表再展开条目就是几十次请求。
 */

export default defineComponent({
  name: "MediaPlaylists",
  setup() {
    const router = useRouter()

    const playlists = ref<MediaPlaylistSummary[]>([])
    const loading = ref(true)

    const dialogVisible = ref(false)
    /** 正在编辑的列表 id；null 表示新建。 */
    const editingId = ref<number | null>(null)
    const form = ref({ name: "", description: "" })
    const saving = ref(false)

    async function load() {
      loading.value = true
      try {
        const resp = await mediaListPlaylists()
        playlists.value = resp.code === 200 ? (resp.data ?? []) : []
        if (resp.code !== 200) ElMessage.error(resp.message)
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())

    function openCreate() {
      editingId.value = null
      form.value = { name: "", description: "" }
      dialogVisible.value = true
    }

    function openEdit(playlist: MediaPlaylistSummary) {
      editingId.value = playlist.id
      form.value = { name: playlist.name, description: playlist.description ?? "" }
      dialogVisible.value = true
    }

    async function onSave() {
      const name = form.value.name.trim()
      if (!name) {
        ElMessage.warning(t("media.playlist_name_required"))
        return
      }
      saving.value = true
      try {
        const description = form.value.description.trim()
        const resp =
          editingId.value === null
            ? await mediaCreatePlaylist(name, description || undefined)
            : await mediaUpdatePlaylist(editingId.value, { name, description })
        if (resp.code !== 200) {
          ElMessage.error(resp.message || t("global.failed"))
          return
        }
        ElMessage.success(
          editingId.value === null ? t("media.playlist_created") : t("media.saved"),
        )
        dialogVisible.value = false
        await load()
      } finally {
        saving.value = false
      }
    }

    async function onDelete(playlist: MediaPlaylistSummary) {
      try {
        await ElMessageBox.confirm(
          t("media.confirm_delete_playlist", { name: playlist.name }),
          t("media.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await mediaDeletePlaylist(playlist.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message || t("global.failed"))
        return
      }
      ElMessage.success(t("media.playlist_deleted"))
      await load()
    }

    const openDetail = (playlist: MediaPlaylistSummary) =>
      void router.push({ name: "media-playlist", params: { id: playlist.id } })

    /**
     * 四宫格封面：只给**真的有海报**的条目发图片请求。
     *
     * `preview_items[].poster_path` 就是为此加的：早先只拿到 id，只能盲发
     * `/items/{id}/image`，没有海报的条目一律 404（画面能降级成空块，
     * 但浏览器会为每次 404 记一条控制台错误）。
     */
    function renderCover(playlist: MediaPlaylistSummary) {
      const entries = playlist.preview_items.slice(0, 4)
      if (entries.length === 0) return <div class="ol-media-playlist-cover is-empty" />
      const cols = Math.min(2, entries.length)
      return (
        <div class={`ol-media-playlist-cover cols-${cols}`}>
          {entries.map((entry) =>
            entry.poster_path ? (
              <img key={entry.id} src={mediaImageLink(entry.id)} alt={entry.title} loading="lazy" />
            ) : (
              <div key={entry.id} class="ol-media-playlist-cover-cell" title={entry.title} />
            ),
          )}
        </div>
      )
    }

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <ElButton size="small" text={true} onClick={() => router.push({ name: "media-library" })}>
              <ElIcon style={{ marginRight: "4px" }}>
                <ArrowLeft />
              </ElIcon>
              {t("media.back_to_library")}
            </ElButton>
            <span class="ol-media-page-title">{t("media.tab_playlists")}</span>
            <span class="ol-media-page-sub">
              {t("media.playlists_count", { count: playlists.value.length })}
            </span>
          </div>
          <div class="ol-media-tools">
            <ElButton size="small" type="primary" onClick={openCreate}>
              <ElIcon style={{ marginRight: "4px" }}>
                <Plus />
              </ElIcon>
              {t("media.new_playlist")}
            </ElButton>
          </div>
        </div>

        {playlists.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_playlists")} />
        ) : (
          <div class="ol-media-playlist-grid">
            {playlists.value.map((playlist) => (
              <div key={playlist.id} class="ol-media-playlist-card">
                <div onClick={() => openDetail(playlist)}>{renderCover(playlist)}</div>
                <div class="ol-media-playlist-body">
                  <div class="ol-media-playlist-name" title={playlist.name}>
                    {playlist.name}
                  </div>
                  <div class="ol-media-playlist-desc" title={playlist.description ?? ""}>
                    {playlist.description || t("media.playlist_no_desc")}
                  </div>
                  <div class="ol-media-playlist-meta">
                    <span>
                      {playlist.item_count === 1
                        ? t("media.playlist_item_one")
                        : t("media.playlist_items", { count: playlist.item_count })}
                    </span>
                    <span>{formatTime(playlist.updated_at)}</span>
                  </div>
                  <div class="ol-media-playlist-ops">
                    <ElButton size="small" type="primary" plain={true} onClick={() => openDetail(playlist)}>
                      {t("media.open")}
                    </ElButton>
                    <ElButton size="small" text={true} onClick={() => openEdit(playlist)}>
                      <ElIcon>
                        <Edit />
                      </ElIcon>
                    </ElButton>
                    <ElButton size="small" text={true} type="danger" onClick={() => void onDelete(playlist)}>
                      <ElIcon>
                        <Delete />
                      </ElIcon>
                    </ElButton>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}

        <ElDialog
          modelValue={dialogVisible.value}
          title={t(editingId.value === null ? "media.new_playlist" : "media.edit_playlist")}
          width="480px"
          onUpdate:modelValue={(value: boolean) => (dialogVisible.value = value)}
        >
          {{
            default: () => (
              <ElForm labelWidth="110px">
                <ElFormItem label={t("media.playlist_name")} required={true}>
                  <ElInput
                    modelValue={form.value.name}
                    placeholder={t("media.playlist_name_placeholder")}
                    onUpdate:modelValue={(value: string) => (form.value.name = value)}
                  />
                </ElFormItem>
                <ElFormItem label={t("media.playlist_desc")}>
                  <ElInput
                    modelValue={form.value.description}
                    type="textarea"
                    rows={2}
                    onUpdate:modelValue={(value: string) => (form.value.description = value)}
                  />
                </ElFormItem>
              </ElForm>
            ),
            footer: () => (
              <div class="ol-manage-form-actions">
                <ElButton onClick={() => (dialogVisible.value = false)}>{t("media.cancel")}</ElButton>
                <ElButton type="primary" loading={saving.value} onClick={onSave}>
                  {t(editingId.value === null ? "media.create" : "global.save")}
                </ElButton>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
