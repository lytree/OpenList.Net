import { computed, defineComponent, nextTick, onMounted, onUnmounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElMessage, ElMessageBox, ElTag } from "element-plus"
import { ArrowDown, ArrowLeft, ArrowRight, ArrowUp, Delete, VideoPlay } from "@element-plus/icons-vue"
import {
  mediaBatchStates,
  mediaGetPlaylist,
  mediaGetState,
  mediaImageLink,
  mediaListItemFiles,
  mediaRemovePlaylistItem,
  mediaReorderPlaylist,
  mediaReportWatch,
  mediaUpdateState,
  type MediaFile,
  type MediaItem,
  type MediaPlaylistDetail,
  type UserMediaState,
} from "~/api/media"
import { fsGet } from "~/api/fs"
import { moveIdBy, nextQueuePos, prevQueuePos } from "~/utils/mediaCatalog"
import { rawLink } from "~/utils/link"
import { createWatchTracker } from "~/utils/watchSession"
import { t } from "~/i18n"
import MediaPlayer from "./MediaPlayer"

/**
 * 播放列表详情页 —— `/media/playlists/:id`。
 *
 * 三个职责：排序（走 `POST /items/reorder`，传**完整新顺序**，后端对未列出的成员做
 * 「保持相对顺序追加到末尾」的兜底）、移出、以及**整单连播**。
 *
 * 连播的队列是两层的（条目 → 文件）：一个条目可能由多个分片文件组成，
 * 「下一集」在同一项内是下一片、在项边界上是下一条。这个推进规则抽在
 * `utils/mediaCatalog` 的 `nextQueuePos` / `prevQueuePos` 里，被单测钉住。
 *
 * 为什么在本页播而不再跳详情页：队列语义（上一集/下一集/自动连播）只有持有
 * **完整条目顺序**的地方才能实现，详情页只知道自己那一条。进度写回与详情页同一套
 * 口径（10 秒节流 + 关播放器/离开页面兜底），条目标看过仍然交给 `ended`。
 */

/** 进度写回间隔（秒）。与 ItemDetail 保持一致。 */
const SAVE_INTERVAL_SECONDS = 10

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

    /**
     * 队列里每一条的个人状态（`item_id → state`），只为打「已看」角标。
     *
     * 一次批量取，不逐条 `GET /items/{id}/state`：队列动辄几十条，逐条打会变成
     * 几十个并发请求。列表接口本身不返回个人状态（元数据全局共享、状态每人不同），
     * 所以角标只能由这里补。
     */
    const states = ref<Record<number, UserMediaState>>({})
    const stateOf = (id: number) => states.value[id]

    // ---- 播放队列 ----------------------------------------------------------

    /** 正在播的条目 id；用 id 而不是下标 —— 重排/移出会让下标漂移。 */
    const playingItemId = ref(0)
    /** 当前条目的文件清单（进队列时才拉，列表页不需要它）。 */
    const playingFiles = ref<MediaFile[]>([])
    /** 当前条目内的文件下标。 */
    const filePos = ref(0)
    const playerSrc = ref("")
    const startAt = ref(0)
    const opening = ref(false)
    /**
     * 正在换源（切集）的窗口。
     *
     * `MediaPlayer` 卸载时会把最后位置 emit 一次，而切集时我们在**同一个 tick** 里就把
     * `playingItemId` 换成了新条目 —— 那次迟到的 emit 会带着旧位置写进新条目的进度。
     * 所以换源期间关掉进度落库，等 Vue 把新播放器挂好（`nextTick`）再开。
     */
    const switching = ref(false)
    /** 播放器报上来的最新位置（节流写回用，关播放器时要落盘）。 */
    let lastSeenSeconds = 0
    let lastSavedSeconds = 0
    let lastSavedAt = 0

    /**
     * 观看会话：一次播放一段。换文件/下一条 = tracker 里换 token；
     * 整个条目播完、关播放器、离开页面各自收尾（口径见 utils/watchSession）。
     */
    const watchSession = createWatchTracker(mediaReportWatch)

    const playingIndex = computed(() => items.value.findIndex((it) => it.id === playingItemId.value))
    const playingItem = computed<MediaItem | null>(() =>
      playingIndex.value >= 0 ? items.value[playingIndex.value] : null,
    )
    const pos = computed(() => ({ item: playingIndex.value, file: filePos.value }))
    const nextPos = computed(() =>
      playingItem.value
        ? nextQueuePos(pos.value, playingFiles.value.length, items.value.length)
        : null,
    )
    const prevPos = computed(() =>
      playingItem.value ? prevQueuePos(pos.value, items.value.length) : null,
    )

    async function load() {
      loading.value = true
      try {
        const resp = await mediaGetPlaylist(playlistId.value)
        if (resp.code === 200) {
          playlist.value = resp.data
          void loadStates()
        } else {
          playlist.value = null
          states.value = {}
          if (resp.code !== 404) ElMessage.error(resp.message)
        }
      } finally {
        loading.value = false
      }
    }

    /** 拉队列里所有条目的个人状态。失败只是没角标，不影响列表本身。 */
    async function loadStates() {
      const ids = items.value.map((entry) => entry.id)
      if (ids.length === 0) {
        states.value = {}
        return
      }
      const resp = await mediaBatchStates(ids)
      if (resp.code !== 200) return
      const map: Record<number, UserMediaState> = {}
      for (const state of resp.data ?? []) map[state.item_id] = state
      states.value = map
    }

    onMounted(() => void load())
    watch(playlistId, () => {
      closePlayer()
      void load()
    })

    /**
     * 从第 `itemIndex` 个条目的第 `fileIndex` 个文件开始播。
     *
     * 每次都重新拉文件清单与个人状态：文件清单可能因为重扫而变，续播位置也只能现取。
     * 跨条目续播是**刻意的**（同详情页规则）：只有当条目有多个文件时，第一个文件才续播，
     * 其余分片从 0 开始，否则每次切到下一片都会跳回上次的位置。
     */
    async function playAt(itemIndex: number, fileIndex = 0) {
      const target = items.value[itemIndex]
      if (!target || opening.value) return
      opening.value = true
      switching.value = true
      try {
        const [filesResp, stateResp] = await Promise.all([
          mediaListItemFiles(target.id),
          mediaGetState(target.id),
        ])
        const files = filesResp.code === 200 ? (filesResp.data ?? []) : []
        if (files.length === 0) {
          ElMessage.warning(t("media.no_files"))
          return
        }
        const safeFile = Math.min(Math.max(fileIndex, 0), files.length - 1)
        const file = files[safeFile]
        if (!file) return
        // 直链要现签（签名代理 /p/...）；拿不到 raw_url 时 rawLink 会退回 /d。
        const link = await fsGet(file.path)
        if (link.code !== 200 || !link.data) {
          ElMessage.error(link.message || t("media.play_failed"))
          return
        }

        const state = stateResp.code === 200 ? stateResp.data : null
        playingItemId.value = target.id
        playingFiles.value = files
        filePos.value = safeFile
        playerSrc.value = rawLink(file.path, link.data.raw_url, link.data.sign)
        lastSavedSeconds = state?.progress_seconds ?? 0
        lastSeenSeconds = lastSavedSeconds
        lastSavedAt = 0
        startAt.value = safeFile === 0 ? lastSavedSeconds : 0
        // 换集 = 新的一段会话（tracker 先把上一段收尾）
        watchSession.start(target.id, file.id, startAt.value)
      } finally {
        opening.value = false
        // 新播放器挂载完再放行进度；提前放行会把旧播放器卸载时的那次 emit 记到新条目上
        void nextTick(() => {
          switching.value = false
        })
      }
    }

    function closePlayer() {
      watchSession.end(lastSeenSeconds)
      void flushProgress()
      playingItemId.value = 0
      playingFiles.value = []
      filePos.value = 0
      playerSrc.value = ""
      startAt.value = 0
      lastSeenSeconds = 0
      lastSavedSeconds = 0
    }

    /** 离开播放器/离开页面时把最后位置补写一次（播放器自己也会 emit，节流保证不重复）。 */
    async function flushProgress() {
      const id = playingItemId.value
      if (!id || switching.value || lastSeenSeconds <= 0) return
      if (Math.abs(lastSeenSeconds - lastSavedSeconds) < 2) return
      await saveProgress(lastSeenSeconds)
    }

    async function saveProgress(seconds: number) {
      const id = playingItemId.value
      if (!id) return
      lastSavedSeconds = seconds
      lastSavedAt = Date.now()
      // 心跳与进度同频：一个记"看到哪"，一个记"看了多久"
      watchSession.beat(seconds)
      // 进度是后台写：失败不弹错（下一次节流还会再写一次），但也不静默吞掉状态
      await mediaUpdateState(id, { progress_seconds: Math.floor(seconds) })
    }

    function onProgress(seconds: number) {
      if (!playingItemId.value || switching.value) return
      lastSeenSeconds = seconds
      const now = Date.now()
      const moved = Math.abs(seconds - lastSavedSeconds) >= SAVE_INTERVAL_SECONDS
      if (!moved || now - lastSavedAt < SAVE_INTERVAL_SECONDS * 1000) return
      void saveProgress(seconds)
    }

    async function onEnded() {
      const current = playingItemId.value
      const lastFile = filePos.value >= playingFiles.value.length - 1
      // 会话边界：播完（条目结尾）与"分片播完接着下一片"都结束当前这一段。
      // 放在这里而不是交给 playAt 里的 start()，是因为 start() 只知道**新**文件的起点（0），
      // 那样这段会话的结束位置会被记成 0。
      watchSession.end(lastSeenSeconds, lastFile)
      // 只有整个条目播完才标「看过」；分片之间不算（否则第一片看完就变成已看）
      if (current && lastFile) {
        lastSavedSeconds = 0
        lastSeenSeconds = 0
        // 看完：标记 watched 且进度清零，避免「看完了还从 47 分钟续播」
        void mediaUpdateState(current, { watched: true, progress_seconds: 0 }).then((resp) => {
          // 角标当场生效：否则要等下次进页面才变成「已看」，连播的成就感就断了
          if (resp.code === 200 && resp.data) states.value = { ...states.value, [current]: resp.data }
        })
      }
      const step = nextPos.value
      if (step) {
        await playAt(step.item, step.file)
        return
      }
      closePlayer()
      if (items.value.length > 0) ElMessage.success(t("media.playlist_finished"))
    }

    const next = () => {
      const step = nextPos.value
      if (step) void playAt(step.item, step.file)
    }
    const previous = () => {
      const step = prevPos.value
      if (step) void playAt(step.item, step.file)
    }

    onUnmounted(() => {
      watchSession.end(lastSeenSeconds)
      void flushProgress()
    })

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
          // 移出后队列变短：已取回的状态里留着被删条目也没害处，但下次渲染前先对齐一次更干净
          void loadStates()
          // 播着的那一条被移出了：停掉播放器，别留一个不在队列里的音轨
          if (playingItemId.value === item.id) closePlayer()
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
      const active = playingItem.value
      const activeFile = playingFiles.value[filePos.value]

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
                {items.value.length === 1
                  ? t("media.playlist_item_one")
                  : t("media.playlist_items", { count: items.value.length })}
              </ElTag>
              <ElButton
                size="small"
                type="primary"
                disabled={items.value.length === 0 || opening.value}
                onClick={() => void playAt(0, 0)}
              >
                <ElIcon style={{ marginRight: "4px" }}>
                  <VideoPlay />
                </ElIcon>
                {t("media.play_all")}
              </ElButton>
            </div>
          </div>

          {current?.description ? (
            <p class="ol-media-playlist-detail-desc">{current.description}</p>
          ) : null}

          {/* 队列导航：位置 + 上一集/下一集。播放器本身只负责「现在第几秒」。 */}
          {playerSrc.value && active ? (
            <div class="ol-media-queue-nav">
              <span class="ol-media-queue-pos">
                {t("media.queue_position", {
                  index: playingIndex.value + 1,
                  total: items.value.length,
                })}
              </span>
              <span class="ol-media-queue-now" title={active.title}>
                {t("media.playing")}: {active.title}
                {playingFiles.value.length > 1 && activeFile
                  ? ` · ${filePos.value + 1}/${playingFiles.value.length}`
                  : ""}
              </span>
              <span title={t("media.prev_item")}>
                <ElButton size="small" disabled={!prevPos.value} onClick={previous}>
                  <ElIcon>
                    <ArrowLeft />
                  </ElIcon>
                </ElButton>
              </span>
              <span title={t("media.next_item")}>
                <ElButton size="small" disabled={!nextPos.value} onClick={next}>
                  <ElIcon>
                    <ArrowRight />
                  </ElIcon>
                </ElButton>
              </span>
            </div>
          ) : null}

          {playerSrc.value && active ? (
            <MediaPlayer
              key={activeFile?.id ?? `item-${active.id}`}
              src={playerSrc.value}
              startAt={startAt.value}
              poster={active.poster_path ? mediaImageLink(active.id) : ""}
              title={
                activeFile
                  ? `${active.title} · ${activeFile.path.split("/").pop()}`
                  : active.title
              }
              onProgress={onProgress}
              onEnded={() => void onEnded()}
              onClose={closePlayer}
            />
          ) : null}

          {items.value.length === 0 && !loading.value ? (
            <ElEmpty description={t("media.no_playlist_items")} />
          ) : (
            <div class="ol-media-queue">
              {items.value.map((item, index) => (
                <div
                  key={item.id}
                  class={
                    item.id === playingItemId.value
                      ? "ol-media-queue-row is-playing"
                      : "ol-media-queue-row"
                  }
                >
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
                      {/* 已看角标：状态按页批量取一次，播完当场翻转（见 onEnded） */}
                      {stateOf(item.id)?.watched ? (
                        <span class="ol-media-queue-badge is-watched">{t("media.watched_count")}</span>
                      ) : null}
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
                    {/* 在这里播，而不是跳详情页：队列语义只有本页知道 */}
                    <ElButton
                      size="small"
                      type="primary"
                      plain={true}
                      loading={opening.value && item.id !== playingItemId.value}
                      onClick={() => void playAt(index, 0)}
                    >
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
