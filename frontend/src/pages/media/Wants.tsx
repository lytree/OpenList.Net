import { computed, defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import {
  ElButton,
  ElDialog,
  ElEmpty,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElOption,
  ElSelect,
  ElTag,
} from "element-plus"
import {
  mediaCancelDownload,
  mediaExternalImage,
  mediaListDownloads,
  mediaListLibraries,
  mediaListWants,
  mediaRemoveWant,
  mediaSubmitDownload,
  MediaDownloadStatus,
  type MediaDownload,
  type MediaLibrary,
  type MediaWant,
} from "~/api/media"
import { fetchOfflineDownloadTools } from "~/api/fs"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 待下载（库外想看清单）—— `/media/wants`（需登录）。
 *
 * 这里与 `/media/watch-later` 不是一回事：那边是**已入库条目**上的「稍后看」，
 * 这边是库里还没有的作品 —— 从关注更新流里加进来，凑齐资源地址后交给离线下载，
 * 下载完成 → 扫描入库 → 这一行自动消失（后端回填 `item_id` 后列表不再返回它）。
 *
 * 下载状态由后端每 30 秒轮询一次下载器刷新，页面这边只在打开时取一次 + 提交后刷新，
 * 不自己起定时器：一个后台页面没必要持续打接口。
 */

const PAGE_SIZE = 30

/** 下载器展示名 → 值。后端按名称小写匹配（aria2 / qbit / transmission / simplehttp）。 */
const TOOLS = [
  { value: "aria2", label: "aria2" },
  { value: "qbit", label: "qBittorrent" },
  { value: "transmission", label: "Transmission" },
  { value: "simple_http", label: "SimpleHttp（内置）" },
]

const STATUS_LABEL: Record<number, string> = {
  [MediaDownloadStatus.Pending]: "media.wants_status_pending",
  [MediaDownloadStatus.Downloading]: "media.wants_status_downloading",
  [MediaDownloadStatus.Completed]: "media.wants_status_completed",
  [MediaDownloadStatus.Failed]: "media.wants_status_failed",
  [MediaDownloadStatus.Cancelled]: "media.wants_status_cancelled",
}

const STATUS_TYPE: Record<number, "info" | "primary" | "success" | "danger"> = {
  [MediaDownloadStatus.Pending]: "info",
  [MediaDownloadStatus.Downloading]: "primary",
  [MediaDownloadStatus.Completed]: "success",
  [MediaDownloadStatus.Failed]: "danger",
  [MediaDownloadStatus.Cancelled]: "info",
}

export default defineComponent({
  name: "MediaWantsPage",
  setup() {
    const router = useRouter()

    const wants = ref<MediaWant[]>([])
    const total = ref(0)
    const page = ref(1)
    const loading = ref(false)
    /** wantId → 最近一条下载记录。 */
    const downloads = ref<Map<number, MediaDownload>>(new Map())
    const libraries = ref<MediaLibrary[]>([])
    const tools = ref<string[]>([])

    const dialogVisible = ref(false)
    const submitting = ref(false)
    const current = ref<MediaWant | null>(null)
    const form = ref({ libraryId: 0, tool: "", url: "" })

    async function load() {
      loading.value = true
      try {
        const [resp, dlResp] = await Promise.all([
          mediaListWants(page.value, PAGE_SIZE, false),
          mediaListDownloads(),
        ])
        if (resp.code !== 200) {
          wants.value = []
          total.value = 0
          return
        }
        wants.value = resp.data?.content ?? []
        total.value = resp.data?.total ?? 0

        const map = new Map<number, MediaDownload>()
        for (const row of dlResp.code === 200 ? (dlResp.data ?? []) : []) {
          // 列表按 id 倒序，先到的是更新的那条。
          if (!map.has(row.want_id)) map.set(row.want_id, row)
        }
        downloads.value = map
      } finally {
        loading.value = false
      }
    }

    async function loadOptions() {
      const [libResp, toolResp] = await Promise.all([
        mediaListLibraries(),
        fetchOfflineDownloadTools(),
      ])
      libraries.value = libResp.code === 200 ? (libResp.data ?? []) : []
      tools.value = toolResp.code === 200 ? (toolResp.data ?? []) : []
    }

    onMounted(() => {
      void load()
      void loadOptions()
    })

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    function openDownload(want: MediaWant) {
      current.value = want
      form.value = {
        libraryId: libraries.value[0]?.id ?? 0,
        tool: tools.value[0] ?? "",
        url: "",
      }
      dialogVisible.value = true
    }

    async function submit() {
      if (!current.value) return
      if (!form.value.url.trim()) {
        ElMessage.warning(t("media.wants_url_required"))
        return
      }
      if (!form.value.libraryId) {
        ElMessage.warning(t("media.wants_no_library"))
        return
      }
      submitting.value = true
      try {
        const resp = await mediaSubmitDownload({
          want_id: current.value.id,
          library_id: form.value.libraryId,
          url: form.value.url.trim(),
          tool: form.value.tool || undefined,
        })
        if (resp.code !== 200) {
          ElMessage.error(resp.message)
          return
        }
        dialogVisible.value = false
        ElMessage.success(t("media.wants_submitted"))
        await load()
      } finally {
        submitting.value = false
      }
    }

    async function cancelDownload(row: MediaDownload) {
      try {
        await ElMessageBox.confirm(
          t("media.wants_cancel_confirm", { title: row.title }),
          t("media.wants_cancel"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await mediaCancelDownload(row.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await load()
    }

    async function removeWant(want: MediaWant) {
      const resp = await mediaRemoveWant(want.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      if (wants.value.length === 1 && page.value > 1) page.value -= 1
      await load()
    }

    const posterOf = (want: MediaWant) => mediaExternalImage(want.poster_path)

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <span class="ol-media-page-title">{t("media.wants_title")}</span>
            <span class="ol-media-page-sub">{t("media.item_count", { count: total.value })}</span>
          </div>
        </div>

        {wants.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.wants_empty")} />
        ) : (
          <>
            <div class="ol-wants-list">
              {wants.value.map((want) => {
                const poster = posterOf(want)
                const dl = downloads.value.get(want.id)
                return (
                  <div class="ol-wants-row" key={want.id}>
                    <div class="ol-wants-poster">
                      {poster ? (
                        <img src={poster} alt={want.title} loading="lazy" />
                      ) : (
                        <span class="ol-follow-poster-empty">{want.title.slice(0, 1)}</span>
                      )}
                    </div>
                    <div class="ol-wants-meta">
                      <div class="ol-wants-title">
                        {want.title}
                        {want.year ? <span class="ol-wants-year"> ({want.year})</span> : null}
                      </div>
                      <div class="ol-wants-tags">
                        <ElTag size="small" type="info">
                          {want.media_type === "tv" ? t("media.type_tv") : t("media.type_movie")}
                        </ElTag>
                        {want.release_date ? (
                          <span class="ol-wants-date">{want.release_date}</span>
                        ) : null}
                        {dl ? (
                          <ElTag size="small" type={STATUS_TYPE[dl.status] ?? "info"}>
                            {t(STATUS_LABEL[dl.status] ?? "media.wants_status_pending")}
                            {dl.status === MediaDownloadStatus.Downloading
                              ? ` ${Math.round(dl.progress)}%`
                              : ""}
                          </ElTag>
                        ) : null}
                        {dl?.error ? (
                          <span class="ol-wants-error" title={dl.error}>
                            {dl.error}
                          </span>
                        ) : null}
                      </div>
                    </div>
                    <div class="ol-wants-actions">
                      {dl && (dl.status === MediaDownloadStatus.Pending ||
                        dl.status === MediaDownloadStatus.Downloading) ? (
                        <ElButton size="small" onClick={() => void cancelDownload(dl)}>
                          {t("media.wants_cancel")}
                        </ElButton>
                      ) : (
                        <ElButton size="small" type="primary" onClick={() => openDownload(want)}>
                          {t("media.wants_download")}
                        </ElButton>
                      )}
                      <ElButton size="small" text={true} onClick={() => void removeWant(want)}>
                        {t("media.remove")}
                      </ElButton>
                    </div>
                  </div>
                )
              })}
            </div>
            <FilePager
              page={page.value}
              pageSize={PAGE_SIZE}
              total={total.value}
              pages={pages.value}
              onUpdate:page={(p: number) => {
                page.value = p
                void load()
              }}
            />
          </>
        )}

        <ElDialog
          modelValue={dialogVisible.value}
          title={t("media.wants_download_title", { title: current.value?.title ?? "" })}
          width="520px"
          appendToBody={true}
          onUpdate:modelValue={(v: boolean) => (dialogVisible.value = v)}
        >
          {{
            default: () => (
              <div class="ol-dialog-body">
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">{t("media.wants_library")}</span>
                  <ElSelect
                    modelValue={form.value.libraryId}
                    size="small"
                    style={{ width: "260px" }}
                    onUpdate:modelValue={(v: number) => (form.value.libraryId = Number(v))}
                  >
                    {libraries.value.map((lib) => (
                      <ElOption key={lib.id} label={`${lib.name}（${lib.path}）`} value={lib.id} />
                    ))}
                  </ElSelect>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">{t("media.wants_tool")}</span>
                  <ElSelect
                    modelValue={form.value.tool}
                    size="small"
                    style={{ width: "260px" }}
                    onUpdate:modelValue={(v: string) => (form.value.tool = v)}
                  >
                    {(tools.value.length > 0 ? tools.value : TOOLS.map((x) => x.value)).map(
                      (name) => (
                        <ElOption
                          key={name}
                          label={TOOLS.find((x) => x.value === name.toLowerCase())?.label ?? name}
                          value={name}
                        />
                      ),
                    )}
                  </ElSelect>
                </div>
                <div class="ol-dialog-field">
                  <span class="ol-dialog-label">{t("media.wants_url")}</span>
                  <ElInput
                    modelValue={form.value.url}
                    type="textarea"
                    rows={4}
                    placeholder="magnet:?xt=urn:btih:... 或 https://..."
                    onUpdate:modelValue={(v: string) => (form.value.url = v)}
                  />
                </div>
                <span class="ol-dialog-hint">{t("media.wants_hint")}</span>
              </div>
            ),
            footer: () => (
              <div class="ol-dialog-footer">
                <ElButton onClick={() => (dialogVisible.value = false)}>
                  {t("media.wants_cancel")}
                </ElButton>
                <ElButton type="primary" loading={submitting.value} onClick={() => void submit()}>
                  {t("media.wants_submit")}
                </ElButton>
              </div>
            ),
          }}
        </ElDialog>
      </div>
    )
  },
})
