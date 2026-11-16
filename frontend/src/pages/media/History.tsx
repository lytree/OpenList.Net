import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElOption, ElSelect, ElTag } from "element-plus"
import { VideoPlay } from "@element-plus/icons-vue"
import { mediaImageLink, mediaWatchHistory, type WatchHistoryEntry } from "~/api/media"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 观看历史 —— `/media/history`（需登录）。
 *
 * 数据来自观看会话（`GET /media/watch/history`，按条目聚合），不是「播放进度」：
 * 进度只有一条（当前停在哪儿），历史是「哪天看过什么」。所以这里按**天分组**，
 * 每组是一个「那天你看了这几部」的流水，点一行回到条目详情继续播。
 *
 * 参照 Peach 的 playback history：时间倒序、带缩略图、标出「看完 / 未看完」，
 * 顶部给一个时间窗（近 7 / 30 / 90 天）—— 历史页最常见的用法就是「上周看的那部叫啥」。
 */

/** 时间窗候选。`0` 表示不限（后端按 3650 天算，等价于「全部」）。 */
const WINDOWS = [
  { value: 7, key: "media.history_days_7" },
  { value: 30, key: "media.history_days_30" },
  { value: 90, key: "media.history_days_90" },
  { value: 3650, key: "media.history_all" },
]

export default defineComponent({
  name: "MediaHistoryPage",
  setup() {
    const router = useRouter()

    const days = ref(30)
    const entries = ref<WatchHistoryEntry[]>([])
    const loading = ref(false)

    async function load() {
      loading.value = true
      try {
        // limit 给大一点：历史是「翻流水」，不是分页表格；后端按 limit 截断，超过就没了。
        const resp = await mediaWatchHistory(days.value, 200)
        entries.value = resp.code === 200 ? (resp.data ?? []) : []
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())
    watch(days, () => void load())

    /** 日期段的显示名：今天 / 昨天 / `YYYY-MM-DD`。 */
    function dayLabel(date: string): string {
      const today = new Date()
      const pad = (n: number) => String(n).padStart(2, "0")
      const todayKey = `${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`
      const yesterday = new Date(today.getTime() - 86_400_000)
      const yesterdayKey = `${yesterday.getFullYear()}-${pad(yesterday.getMonth() + 1)}-${pad(
        yesterday.getDate(),
      )}`
      if (date === todayKey) return t("media.history_today")
      if (date === yesterdayKey) return t("media.history_yesterday")
      return date
    }

    /** 按本地日期分组；后端已按时间倒序，这里保持顺序（Map 保持插入顺序）。 */
    const groups = computed(() => {
      const map = new Map<string, WatchHistoryEntry[]>()
      for (const entry of entries.value) {
        const day = formatTime(entry.last_at).slice(0, 10)
        const list = map.get(day)
        if (list) list.push(entry)
        else map.set(day, [entry])
      }
      return [...map.entries()].map(([day, list]) => ({
        day,
        label: dayLabel(day),
        entries: list,
      }))
    })

    /** 汇总：这次时间窗里看了几部、一共多少分钟。 */
    const summary = computed(() => {
      const minutes = Math.round(
        entries.value.reduce((sum, entry) => sum + entry.watched_seconds, 0) / 60,
      )
      return t("media.history_summary", { count: entries.value.length, minutes })
    })

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <span class="ol-media-page-title">{t("media.tab_history")}</span>
            <span class="ol-media-page-sub">{summary.value}</span>
          </div>
          <div class="ol-media-tools">
            <ElSelect
              modelValue={days.value}
              size="small"
              style={{ width: "150px" }}
              onUpdate:modelValue={(value: number) => (days.value = value)}
            >
              {WINDOWS.map((item) => (
                <ElOption key={item.value} label={t(item.key)} value={item.value} />
              ))}
            </ElSelect>
          </div>
        </div>

        {entries.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_watch_history")} />
        ) : (
          <div class="olm-history">
            {groups.value.map((group) => (
              <section key={group.day} class="olm-history-day">
                <div class="olm-history-day-label">{group.label}</div>
                {group.entries.map((entry) => (
                  <div
                    key={`${group.day}-${entry.item_id}`}
                    class="olm-history-row"
                    onClick={() =>
                      void router.push({ name: "media-item", params: { id: entry.item_id } })
                    }
                  >
                    <div class="olm-history-thumb">
                      {entry.poster_path ? (
                        <img src={mediaImageLink(entry.item_id)} alt={entry.title} loading="lazy" />
                      ) : (
                        <div class="olm-history-thumb-fallback">
                          <ElIcon>
                            <VideoPlay />
                          </ElIcon>
                        </div>
                      )}
                    </div>
                    <div class="olm-history-main">
                      <div class="olm-history-title">
                        {entry.title}
                        {entry.year ? <span class="olm-history-year">{entry.year}</span> : null}
                      </div>
                      <div class="olm-history-meta">
                        <span>{formatTime(entry.last_at).slice(11)}</span>
                        <span>·</span>
                        <span>
                          {t("media.minutes_value", {
                            minutes: Math.max(1, Math.round(entry.watched_seconds / 60)),
                          })}
                        </span>
                        {entry.completed ? (
                          <ElTag size="small" type="success">
                            {t("media.history_completed")}
                          </ElTag>
                        ) : (
                          <ElTag size="small" type="info">
                            {t("media.history_unfinished")}
                          </ElTag>
                        )}
                      </div>
                    </div>
                    <ElButton size="small" text={true} class="olm-history-play">
                      {t("media.history_continue")}
                    </ElButton>
                  </div>
                ))}
              </section>
            ))}
          </div>
        )}
      </div>
    )
  },
})
