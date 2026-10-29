import { computed, defineComponent, onMounted, onUnmounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import {
  ElButton,
  ElDialog,
  ElEmpty,
  ElIcon,
  ElInput,
  ElMessage,
  ElOption,
  ElSelect,
  ElTag,
} from "element-plus"
import {
  ArrowLeft,
  Plus,
  Refresh,
  Star,
  StarFilled,
  VideoCamera,
  View,
} from "@element-plus/icons-vue"
import {
  MediaItemStatus,
  mediaAddToPlaylist,
  mediaApplySubtitle,
  mediaGetItem,
  mediaGetState,
  mediaImageLink,
  mediaListItemFiles,
  mediaListPlaylists,
  mediaReportWatch,
  mediaScrapeItem,
  mediaSearchSubtitles,
  mediaUpdateState,
  type MediaFile,
  type MediaItem,
  type MediaPlaylistSummary,
  type SubtitleCandidate,
  type UserMediaState,
} from "~/api/media"
import { fsGet } from "~/api/fs"
import { rawLink } from "~/utils/link"
import { formatTime, humanSize } from "~/utils/format"
import { createWatchTracker } from "~/utils/watchSession"
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

    /** 观看会话：起播/换文件各开一段，播完或关播放器收尾（口径见 utils/watchSession）。 */
    const watchSession = createWatchTracker(mediaReportWatch)

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
      // 换文件就是新的一段会话：tracker 内部会先把上一段收尾
      watchSession.start(itemId.value, file.id, startAt.value)
    }

    function closePlayer() {
      watchSession.end(lastSavedSeconds)
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
      // 会话心跳与进度写回同频：一个记"看到哪"，一个记"看了多久"
      watchSession.beat(seconds)
      const resp = await mediaUpdateState(itemId.value, {
        progress_seconds: Math.floor(seconds),
      })
      if (resp.code === 200 && resp.data) state.value = resp.data
    }

    function onEnded() {
      // 看完：标记 watched；进度清零，避免「看完了还从 47 分钟续播」
      const endedAt = lastSavedSeconds
      lastSavedSeconds = 0
      watchSession.end(endedAt, true)
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
      watchSession.end(lastSavedSeconds)
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

    // ---- 加入播放列表 ------------------------------------------------------

    const playlistDialog = ref(false)
    const playlists = ref<MediaPlaylistSummary[]>([])
    /** 选中的已有列表 id；0 表示「新建一个」。 */
    const pickedPlaylistId = ref(0)
    const newPlaylistName = ref("")
    const addingToPlaylist = ref(false)

    async function openPlaylistDialog() {
      pickedPlaylistId.value = 0
      newPlaylistName.value = ""
      playlistDialog.value = true
      const resp = await mediaListPlaylists()
      playlists.value = resp.code === 200 ? (resp.data ?? []) : []
    }

    /**
     * 提交「加入播放列表」。
     *
     * 走 `/media/playlists/add` 而不是「先建列表再追加」：后端在这个端点里
     * 一次完成「没有目标列表就新建」，前端少一次往返，也少一个「建好了但没加进去」的中间态。
     */
    async function confirmAddToPlaylist() {
      const current = item.value
      if (!current) return
      const name = newPlaylistName.value.trim()
      if (pickedPlaylistId.value === 0 && !name) {
        ElMessage.warning(t("media.playlist_name_required"))
        return
      }
      addingToPlaylist.value = true
      try {
        const resp = await mediaAddToPlaylist({
          item_id: current.id,
          playlist_id: pickedPlaylistId.value || undefined,
          name: pickedPlaylistId.value ? undefined : name,
        })
        if (resp.code !== 200) {
          ElMessage.error(resp.message || t("global.failed"))
          return
        }
        ElMessage.success(
          t("media.added_to_playlist_named", { name: resp.data?.name ?? name }),
        )
        playlistDialog.value = false
      } finally {
        addingToPlaylist.value = false
      }
    }

    // ---- 字幕 --------------------------------------------------------------

    const SUBTITLE_LANGS = [
      { value: "zh-cn", label: "简体中文" },
      { value: "zh-tw", label: "繁體中文" },
      { value: "en", label: "English" },
      { value: "ja", label: "日本語" },
    ]

    const subtitleDialog = ref(false)
    const subtitleLang = ref("zh-cn")
    const subtitleLoading = ref(false)
    const subtitleCandidates = ref<SubtitleCandidate[]>([])
    /** 是否已搜过：区分「还没搜」与「搜了但没结果」两种空态。 */
    const subtitleSearched = ref(false)
    const applyingSubtitleId = ref("")

    async function searchSubtitles() {
      const current = item.value
      if (!current) return
      subtitleLoading.value = true
      try {
        const resp = await mediaSearchSubtitles(current.title, current.year, subtitleLang.value)
        if (resp.code === 200) {
          subtitleCandidates.value = resp.data ?? []
        } else {
          subtitleCandidates.value = []
          // 未配置 provider 时后端返回非 200，这里如实提示，不要伪装成「没有字幕」
          ElMessage.error(resp.message || t("media.subtitle_not_configured"))
        }
        subtitleSearched.value = true
      } finally {
        subtitleLoading.value = false
      }
    }

    function openSubtitleDialog() {
      subtitleDialog.value = true
      subtitleCandidates.value = []
      subtitleSearched.value = false
      void searchSubtitles()
    }

    async function applySubtitle(candidate: SubtitleCandidate) {
      applyingSubtitleId.value = candidate.id
      try {
        const resp = await mediaApplySubtitle(itemId.value, candidate)
        if (resp.code !== 200) {
          ElMessage.error(resp.message || t("global.failed"))
          return
        }
        ElMessage.success(t("media.subtitle_applied", { path: resp.data ?? "" }))
        subtitleDialog.value = false
      } finally {
        applyingSubtitleId.value = ""
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
                  <ElButton size="small" onClick={openPlaylistDialog}>
                    <ElIcon style={{ marginRight: "4px" }}><Plus /></ElIcon>
                    {t("media.add_to_playlist")}
                  </ElButton>
                  <ElButton size="small" onClick={openSubtitleDialog}>
                    {t("media.subtitles")}
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

          {/* 加入播放列表：选已有列表，或直接填名字让后端新建 */}
          <ElDialog
            modelValue={playlistDialog.value}
            title={t("media.add_to_playlist")}
            width="420px"
            onUpdate:modelValue={(value: boolean) => (playlistDialog.value = value)}
          >
            {{
              default: () => (
                <div class="ol-dialog-stack">
                  <ElSelect
                    modelValue={pickedPlaylistId.value}
                    style={{ width: "100%" }}
                    onUpdate:modelValue={(value: number) => (pickedPlaylistId.value = Number(value))}
                  >
                    <ElOption label={t("media.new_playlist")} value={0} />
                    {playlists.value.map((playlist) => (
                      <ElOption
                        key={playlist.id}
                        label={`${playlist.name} (${playlist.item_count})`}
                        value={playlist.id}
                      />
                    ))}
                  </ElSelect>
                  {pickedPlaylistId.value === 0 ? (
                    <ElInput
                      modelValue={newPlaylistName.value}
                      placeholder={t("media.playlist_name_placeholder")}
                      onUpdate:modelValue={(value: string) => (newPlaylistName.value = value)}
                    />
                  ) : (
                    <div class="ol-dialog-hint">{t("media.pick_playlist_hint")}</div>
                  )}
                </div>
              ),
              footer: () => (
                <div class="ol-manage-form-actions">
                  <ElButton onClick={() => (playlistDialog.value = false)}>{t("media.cancel")}</ElButton>
                  <ElButton type="primary" loading={addingToPlaylist.value} onClick={confirmAddToPlaylist}>
                    {pickedPlaylistId.value === 0 ? t("media.create_and_add") : t("media.add_to_playlist")}
                  </ElButton>
                </div>
              ),
            }}
          </ElDialog>

          {/* 字幕：按标题 + 年份搜，选中即写回媒体文件同目录 */}
          <ElDialog
            modelValue={subtitleDialog.value}
            title={t("media.subtitle_search")}
            width="620px"
            onUpdate:modelValue={(value: boolean) => (subtitleDialog.value = value)}
          >
            {{
              default: () => (
                <div class="ol-dialog-stack">
                  <div class="ol-dialog-hint">{t("media.subtitle_search_hint")}</div>
                  <div class="ol-media-tools">
                    <ElSelect
                      modelValue={subtitleLang.value}
                      size="small"
                      style={{ width: "140px" }}
                      onUpdate:modelValue={(value: string) => {
                        subtitleLang.value = value
                        void searchSubtitles()
                      }}
                    >
                      {SUBTITLE_LANGS.map((lang) => (
                        <ElOption key={lang.value} label={lang.label} value={lang.value} />
                      ))}
                    </ElSelect>
                    <ElButton size="small" loading={subtitleLoading.value} onClick={() => void searchSubtitles()}>
                      {t("media.search")}
                    </ElButton>
                  </div>
                  {subtitleCandidates.value.length === 0 ? (
                    subtitleSearched.value && !subtitleLoading.value ? (
                      <ElEmpty description={t("media.subtitle_empty")} />
                    ) : null
                  ) : (
                    <div class="ol-subtitle-list">
                      {subtitleCandidates.value.map((candidate) => (
                        <div key={candidate.id} class="ol-subtitle-row">
                          <div class="ol-subtitle-name" title={candidate.name}>
                            {candidate.name}
                          </div>
                          <ElTag size="small" type="info">{candidate.format.toUpperCase()}</ElTag>
                          <ElTag size="small">{candidate.language}</ElTag>
                          <span class="ol-media-file-meta">
                            {t("media.subtitle_downloads", { count: candidate.downloads })}
                          </span>
                          <ElButton
                            size="small"
                            type="primary"
                            plain={true}
                            loading={applyingSubtitleId.value === candidate.id}
                            onClick={() => void applySubtitle(candidate)}
                          >
                            {t("media.subtitle_apply")}
                          </ElButton>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              ),
              footer: () => (
                <div class="ol-manage-form-actions">
                  <ElButton onClick={() => (subtitleDialog.value = false)}>{t("media.close")}</ElButton>
                </div>
              ),
            }}
          </ElDialog>
        </div>
      )
    }
  },
})
