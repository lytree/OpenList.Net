import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElMessage, ElMessageBox, ElTag } from "element-plus"
import { ArrowDown, ArrowLeft, ArrowUp, Delete, VideoPlay } from "@element-plus/icons-vue"
import {
  mediaGetPlaylist,
  mediaImageLink,
  mediaRemovePlaylistItem,
  mediaReorderPlaylist,
  type MediaItem,
  type MediaPlaylistDetail,
} from "~/api/media"
import { moveIdBy } from "~/utils/mediaCatalog"
import { t } from "~/i18n"

/**
 * 播放列表详情页 —— `/media/playlists/:id`。
 *
 * 重排走 `POST /items/reorder`（传**完整新顺序**），后端对未列出的成员做
 * 「保持相对顺序追加到末尾」的兜底。前端因此只需要算「这一条往前/往后挪一格」
 * 的完整 id 数组，交给纯函数 `moveIdBy` 处理边界（首尾不再移动）。
 *
 * 乐观更新：先改本地顺序再发请求，失败就重新拉取——列表重排是最需要有即时反馈的操作，
 * 等一次网络往返再动会让人以为点错了。
 */

export default defineComponent({
  name: "MediaPlaylistDetail",
  setup() {
    const route = useRoute()
    const router = useRouter()

    const playlist = ref<MediaPlaylistDetail | null>(null)
    const loading = ref(true)
    const busy = ref(false)

    const playlistId = computed(() => Number(route.params.id))
    const items = computed<MediaItem[]>(() => playlist.value?.items ?? [])

    async function load() {
      loading.value = true
      try {
        const resp = await mediaGetPlaylist(playlistId.value)
        if (resp.code === 200) {
          playlist.value = resp.data
        } else {
          playlist.value = null
          if (resp.code !== 404) ElMessage.error(resp.message)
        }
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())
    watch(playlistId, () => void load())

    /**
     * 移动一格。
     *
     * `busy` 是必要的：连点上移时若两个请求并发，后到的 reorder 会拿旧顺序覆盖，
     * 结果看起来「点了没反应」。
     */
    async function move(item: MediaItem, delta: number) {
      const current = playlist.value
      if (!current || busy.value) return
      const ids = current.items.map((entry) => entry.id)
      const nextIds = moveIdBy(ids, item.id, delta)
      // 已经在首/尾：纯函数会原样返回，不必打后端
      if (nextIds.every((id, index) => id === ids[index])) return

      const byId = new Map(current.items.map((entry) => [entry.id, entry]))
      const optimistic = nextIds
        .map((id) => byId.get(id))
        .filter((entry): entry is MediaItem => Boolean(entry))
      playlist.value = { ...current, items: optimistic }

      busy.value = true
      try {
        const resp = await mediaReorderPlaylist(current.id, nextIds)
        if (resp.code === 200) playlist.value = resp.data
        else {
          ElMessage.error(resp.message || t("global.failed"))
          await load()
        }
      } finally {
        busy.value = false
      }
    }

    async function remove(item: MediaItem) {
      const current = playlist.value
      if (!current || busy.value) return
      try {
        await ElMessageBox.confirm(
          t("media.confirm_remove_playlist_item", { title: item.title }),
          t("media.remove"),
          { type: "warning" },
        )
      } catch {
        return
      }
      busy.value = true
      try {
        const resp = await mediaRemovePlaylistItem(current.id, item.id)
        if (resp.code === 200) {
          playlist.value = resp.data
          ElMessage.success(t("media.removed_from_playlist"))
        } else {
          ElMessage.error(resp.message || t("global.failed"))
        }
      } finally {
        busy.value = false
      }
    }

    const openItem = (item: MediaItem) =>
      void router.push({ name: "media-item", params: { id: item.id } })

    return () => {
      const current = playlist.value
      return (
        <div class="ol-media">
          <div class="ol-media-toolbar">
            <div class="ol-media-title-row">
              <ElButton size="small" text={true} onClick={() => router.push({ name: "media-playlists" })}>
                <ElIcon style={{ marginRight: "4px" }}>
                  <ArrowLeft />
                </ElIcon>
                {t("media.all_playlists")}
              </ElButton>
              <span class="ol-media-page-title">{current?.name ?? t("media.tab_playlists")}</span>
              <ElTag size="small" type="info">
                {t("media.playlist_items", { count: items.value.length })}
              </ElTag>
            </div>
          </div>

          {current?.description ? (
            <p class="ol-media-playlist-detail-desc">{current.description}</p>
          ) : null}

          {items.value.length === 0 && !loading.value ? (
            <ElEmpty description={t("media.no_playlist_items")} />
          ) : (
            <div class="ol-media-queue">
              {items.value.map((item, index) => (
                <div key={item.id} class="ol-media-queue-row">
                  <span class="ol-media-queue-index">{index + 1}</span>
                  <div class="ol-media-queue-poster" onClick={() => openItem(item)}>
                    {item.poster_path ? (
                      <img src={mediaImageLink(item.id)} alt={item.title} loading="lazy" />
                    ) : (
                      <div class="ol-media-queue-poster-fallback" />
                    )}
                  </div>
                  <div class="ol-media-queue-meta" onClick={() => openItem(item)}>
                    <div class="ol-media-queue-title" title={item.title}>
                      {item.title}
                    </div>
                    <div class="ol-media-queue-sub">
                      {item.year ? <span>{item.year}</span> : null}
                      {item.code ? <span class="ol-media-code">{item.code}</span> : null}
                    </div>
                  </div>
                  <div class="ol-media-queue-ops">
                    {/* EP 的 ElButton 没有 title 属性，提示挂在 span 上（同管理台写法） */}
                    <span title={t("media.move_up")}>
                      <ElButton
                        size="small"
                        text={true}
                        disabled={index === 0 || busy.value}
                        onClick={() => void move(item, -1)}
                      >
                        <ElIcon>
                          <ArrowUp />
                        </ElIcon>
                      </ElButton>
                    </span>
                    <span title={t("media.move_down")}>
                      <ElButton
                        size="small"
                        text={true}
                        disabled={index === items.value.length - 1 || busy.value}
                        onClick={() => void move(item, 1)}
                      >
                        <ElIcon>
                          <ArrowDown />
                        </ElIcon>
                      </ElButton>
                    </span>
                    <ElButton size="small" type="primary" plain={true} onClick={() => openItem(item)}>
                      <ElIcon style={{ marginRight: "4px" }}>
                        <VideoPlay />
                      </ElIcon>
                      {t("media.play")}
                    </ElButton>
                    <span title={t("media.remove")}>
                      <ElButton
                        size="small"
                        text={true}
                        type="danger"
                        disabled={busy.value}
                        onClick={() => void remove(item)}
                      >
                        <ElIcon>
                          <Delete />
                        </ElIcon>
                      </ElButton>
                    </span>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )
    }
  },
})
