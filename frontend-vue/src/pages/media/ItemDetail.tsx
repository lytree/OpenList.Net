import { computed, defineComponent, onMounted, onUnmounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElIcon, ElMessage, ElTag } from "element-plus"
import {
  ArrowLeft,
  Refresh,
  Star,
  StarFilled,
  VideoCamera,
  View,
} from "@element-plus/icons-vue"
import {
  MediaItemStatus,
  mediaGetItem,
  mediaGetState,
  mediaImageLink,
  mediaListItemFiles,
  mediaScrapeItem,
  mediaUpdateState,
  type MediaFile,
  type MediaItem,
  type UserMediaState,
} from "~/api/media"
import { fsGet } from "~/api/fs"
import { rawLink } from "~/utils/link"
import { formatTime, humanSize } from "~/utils/format"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"
import MediaPlayer from "./MediaPlayer"

/**
 * 条目详情页（/media/item/:id）。
 *
 * 三块信息各自独立取数，互不阻塞：
 * - 条目元数据（`/media/items/{id}`）；
 * - 文件清单（`/media/items/{id}/files`）—— 播放入口；
 * - 个人状态（`/media/items/{id}/state`）—— 收藏/看过/续播位置。
 *
 * 播放进度的**节流写回**在这里做（播放器只报「现在第几秒」）：
 * 10 秒一次 + 关播放器/离开页面时兜底一次。看完标记交给 ended 事件。
 */

/** 进度写回间隔（秒）。太密会把 /media/items/{id}/state 打成心跳接口。 */
const SAVE_INTERVAL_SECONDS = 10

/** 秒 → h:mm:ss（文件时长显示用）。 */
function formatDuration(seconds?: number | null): string {
  if (!seconds || seconds <= 0) return "-"
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = Math.floor(seconds % 60)
  const mm = String(m).padStart(2, "0")
  const ss = String(s).padStart(2, "0")
  return h > 0 ? `${h}:${mm}:${ss}` : `${m}:${ss}`
}

export default defineComponent({
  name: "MediaItemDetail",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    const item = ref<MediaItem | null>(null)
    const files = ref<MediaFile[]>([])
    const state = ref<UserMediaState | null>(null)
    const loading = ref(true)
    const scraping = ref(false)

    /** 正在播放的文件；null 表示播放器关着。 */
    const playing = ref<MediaFile | null>(null)
    const playerSrc = ref("")
    /** 续播位置（条目级进度，不区分文件 —— 见遗留说明）。 */
    const startAt = ref(0)
    let lastSavedSeconds = 0
    let lastSavedAt = 0

    const itemId = computed(() => Number(route.params.id))
    const canManage = computed(() => user.can("manage_media"))
    const genres = computed<string[]>(() => {
      try {
        const raw = item.value?.genres
        return raw ? (JSON.parse(raw) as string[]) : []
      } catch {
        return []
      }
    })

    async function load() {
      loading.value = true
      try {
        const [itemResp, filesResp, stateResp] = await Promise.all([
          mediaGetItem(itemId.value),
          mediaListItemFiles(itemId.value),
          mediaGetState(itemId.value),
        ])
        item.value = itemResp.code === 200 ? itemResp.data : null
        files.value = filesResp.code === 200 ? (filesResp.data ?? []) : []
        state.value = stateResp.code === 200 ? stateResp.data : null
        lastSavedSeconds = state.value?.progress_seconds ?? 0
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())
    watch(itemId, () => void load())

    // ---- 播放 -------------------------------------------------------------

    async function play(file: MediaFile) {
      // 直链要靠 fs/get 拿 raw_url（签名代理 /p/...）；没有 raw_url 就退回 /d。
      const resp = await fsGet(file.path)
      if (resp.code !== 200 || !resp.data) {
        ElMessage.error(resp.message || t("media.play_failed"))
        return
      }
      playing.value = file
      playerSrc.value = rawLink(file.path, resp.data.raw_url, resp.data.sign)
      // 条目级进度：只在播第一个文件时续播，切集从 0 开始（否则每集都从头几秒看起）
      startAt.value = playing.value.id === files.value[0]?.id ? (state.value?.progress_seconds ?? 0) : 0
    }

    function closePlayer() {
      playing.value = null
      playerSrc.value = ""
    }

    function onProgress(seconds: number) {
      if (!playing.value) return
      const now = Date.now()
      const moved = Math.abs(seconds - lastSavedSeconds) >= SAVE_INTERVAL_SECONDS
      if (!moved || now - lastSavedAt < SAVE_INTERVAL_SECONDS * 1000) return
      void saveProgress(seconds)
    }

    async function saveProgress(seconds: number) {
      lastSavedSeconds = seconds
      lastSavedAt = Date.now()
      const resp = await mediaUpdateState(itemId.value, {
        progress_seconds: Math.floor(seconds),
      })
      if (resp.code === 200 && resp.data) state.value = resp.data
    }

    function onEnded() {
      // 看完：标记 watched；进度清零，避免「看完了还从 47 分钟续播」
      lastSavedSeconds = 0
      void mediaUpdateState(itemId.value, { watched: true, progress_seconds: 0 }).then((resp) => {
        if (resp.code === 200 && resp.data) state.value = resp.data
      })
      // 自动连播下一集（按后端返回的文件顺序）
      const index = files.value.findIndex((file) => file.id === playing.value?.id)
      const next = files.value[index + 1]
      if (next) void play(next)
      else closePlayer()
    }

    onUnmounted(() => {
      // 离开页面兜底：播放器组件自己的 onUnmounted 也会 emit，双保险但不重复写
      if (playing.value && lastSavedSeconds > 0) {
        void mediaUpdateState(itemId.value, { progress_seconds: Math.floor(lastSavedSeconds) })
      }
    })

    // ---- 状态开关 ----------------------------------------------------------

    async function patchState(patch: Parameters<typeof mediaUpdateState>[1]) {
      const resp = await mediaUpdateState(itemId.value, patch)
      if (resp.code === 200 && resp.data) state.value = resp.data
      else ElMessage.error(resp.message || "failed")
    }

    const toggleFavorite = () => void patchState({ favorite: !state.value?.favorite })
    const toggleWatched = () =>
      void patchState({
        watched: !state.value?.watched,
        // 手动标看过时把进度清掉，别再来续播提示
        progress_seconds: state.value?.watched ? (state.value?.progress_seconds ?? 0) : 0,
      })
    const toggleLater = () => void patchState({ watch_later: !state.value?.watch_later })

    async function scrape() {
      scraping.value = true
      try {
        const resp = await mediaScrapeItem(itemId.value)
        if (resp.code !== 200) ElMessage.error(resp.message || "failed")
        else ElMessage.success(t("media.scrape_queued"))
        await load()
      } finally {
        scraping.value = false
      }
    }

    const statusText = () => {
      switch (item.value?.status) {
        case MediaItemStatus.Pending:
          return t("media.status_pending")
        case MediaItemStatus.Unmatched:
          return t("media.status_unmatched")
        case MediaItemStatus.Ignored:
          return t("media.status_ignored")
        default:
          return ""
      }
    }

    return () => {
      if (loading.value) return <div class="ol-media-detail">{t("global.loading")}</div>
      const current = item.value
      if (!current) {
        return (
          <div class="ol-media-detail">
            <ElButton size="small" onClick={() => router.back()}>
              <ElIcon style={{ marginRight: "4px" }}><ArrowLeft /></ElIcon>
              {t("media.back_to_library")}
            </ElButton>
            <ElTag type="danger">{t("media.no_items")}</ElTag>
          </div>
        )
      }

      return (
        <div class="ol-media-detail">
          <div class="ol-media-detail-head">
            {current.backdrop_path || current.poster_path ? (
              <img
                class="ol-media-backdrop"
                src={mediaImageLink(current.id, current.backdrop_path ? "backdrop" : "poster")}
                alt=""
              />
            ) : null}
            <div class="ol-media-detail-top">
              <ElButton size="small" onClick={() => router.back()}>
                <ElIcon style={{ marginRight: "4px" }}><ArrowLeft /></ElIcon>
                {t("media.back_to_library")}
              </ElButton>
            </div>
            <div class="ol-media-detail-info">
              {current.poster_path ? (
                <img class="ol-media-detail-poster" src={mediaImageLink(current.id)} alt={current.title} />
              ) : null}
              <div class="ol-media-detail-meta">
                <h2 class="ol-media-detail-title">
                  {current.title}
                  {current.year ? <span class="ol-media-detail-year"> ({current.year})</span> : null}
                </h2>
                <div class="ol-media-detail-tags">
                  {current.rating ? <ElTag size="small" type="warning">★ {current.rating.toFixed(1)}</ElTag> : null}
                  {current.original_title ? <ElTag size="small" type="info">{current.original_title}</ElTag> : null}
                  {current.code ? <ElTag size="small">{current.code}</ElTag> : null}
                  {statusText() ? <ElTag size="small" type="danger">{statusText()}</ElTag> : null}
                  {genres.value.map((genre) => (
                    <ElTag key={genre} size="small" type="info">
                      {genre}
                    </ElTag>
                  ))}
                </div>
                <p class="ol-media-detail-overview">
                  {current.overview || t("media.no_overview")}
                </p>
                <div class="ol-media-detail-actions">
                  <ElButton size="small" type={state.value?.favorite ? "warning" : "default"} onClick={toggleFavorite}>
                    <ElIcon style={{ marginRight: "4px" }}>
                      {state.value?.favorite ? <StarFilled /> : <Star />}
                    </ElIcon>
                    {state.value?.favorite ? t("media.unfavorite") : t("media.favorite")}
                  </ElButton>
                  <ElButton size="small" type={state.value?.watched ? "success" : "default"} onClick={toggleWatched}>
                    <ElIcon style={{ marginRight: "4px" }}><View /></ElIcon>
                    {state.value?.watched ? t("media.mark_unwatched") : t("media.mark_watched")}
                  </ElButton>
                  <ElButton size="small" type={state.value?.watch_later ? "primary" : "default"} onClick={toggleLater}>
                    {t("media.watch_later")}
                  </ElButton>
                  {canManage.value && (
                    <ElButton size="small" loading={scraping.value} onClick={scrape}>
                      <ElIcon style={{ marginRight: "4px" }}><Refresh /></ElIcon>
                      {t("media.scrape")}
                    </ElButton>
                  )}
                </div>
                {state.value && state.value.progress_seconds > 0 && (
                  <div class="ol-media-detail-progress">
                    {t("media.state_progress")}: {formatDuration(state.value.progress_seconds)}
                  </div>
                )}
              </div>
            </div>
          </div>

          {playing.value && playerSrc.value && (
            <MediaPlayer
              key={playing.value.id}
              src={playerSrc.value}
              startAt={startAt.value}
              poster={current.poster_path ? mediaImageLink(current.id) : ""}
              title={`${current.title} · ${playing.value.path.split("/").pop()}`}
              onProgress={onProgress}
              onEnded={onEnded}
              onClose={closePlayer}
            />
          )}

          <div class="ol-media-detail-files">
            <div class="ol-media-section-title">{t("media.files")}</div>
            {files.value.length === 0 ? (
              <div class="ol-dialog-hint">{t("media.no_files")}</div>
            ) : (
              files.value.map((file) => (
                <div
                  key={file.id}
                  class={
                    playing.value?.id === file.id
                      ? "ol-media-file is-active"
                      : "ol-media-file"
                  }
                >
                  <ElIcon class="ol-media-file-icon"><VideoCamera /></ElIcon>
                  <span class="ol-media-file-name" title={file.path}>
                    {file.path.split("/").pop()}
                  </span>
                  <span class="ol-media-file-meta">
                    {file.resolution || "-"} · {formatDuration(file.duration_seconds)} ·{" "}
                    {humanSize(file.size)}
                  </span>
                  <ElButton size="small" type="primary" plain={true} onClick={() => void play(file)}>
                    {t("media.play")}
                  </ElButton>
                </div>
              ))
            )}
          </div>

          <div class="ol-media-detail-foot">
            {t("media.last_scan")}: {formatTime(current.updatedAt)} ·{" "}
            {current.directory_path}
          </div>
        </div>
      )
    }
  },
})
