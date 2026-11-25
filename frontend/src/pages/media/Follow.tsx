import { computed, defineComponent, onMounted, ref } from "vue"
import { useRouter } from "vue-router"
import {
  ElButton,
  ElEmpty,
  ElMessage,
  ElMessageBox,
  ElTag,
} from "element-plus"
import {
  mediaAddWant,
  mediaCheckFollow,
  mediaExternalImage,
  mediaFollowFeed,
  mediaListFollowed,
  mediaUnfollowPerson,
  type FollowFeedEntry,
  type FollowedPerson,
} from "~/api/media"
import PersonAvatar from "~/components/PersonAvatar"
import FilePager from "~/pages/home/Pager"
import { formatTime } from "~/utils/format"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 关注更新 —— `/media/follow`。
 *
 * 两个页签共用一个页面，因为它们是同一件事的两面：
 * - **更新流**：关注的人「第一次见到」的作品，按时间倒序。点海报去库里的条目（已入库），
 *   或者加入待下载（还没入库）。
 * - **已关注**：谁被关注了、上次检查是否失败、还有几部没入库；取关在这里。
 *
 * 读接口对游客开放（和媒体门面同口径），但关注 / 取关 / 检查更新要登录 ——
 * 游客看到按钮会直接去登录页，而不是点了没反应。
 */

const PAGE_SIZE = 30

export default defineComponent({
  name: "MediaFollowPage",
  setup() {
    const router = useRouter()
    const user = useUserStore()

    const tab = ref<"feed" | "people">("feed")
    const entries = ref<FollowFeedEntry[]>([])
    const total = ref(0)
    const page = ref(1)
    const followed = ref<FollowedPerson[]>([])
    const loading = ref(false)
    const checking = ref(false)
    /** 已加入待下载的条目（external id + 类型），用于把按钮切成「已在待下载」。 */
    const wantKeys = ref<Set<string>>(new Set())

    const key = (entry: FollowFeedEntry) => `${entry.media_type}:${entry.external_id}`

    async function loadFeed() {
      loading.value = true
      try {
        const resp = await mediaFollowFeed(page.value, PAGE_SIZE)
        if (resp.code !== 200) {
          entries.value = []
          total.value = 0
          return
        }
        entries.value = resp.data?.content ?? []
        total.value = resp.data?.total ?? 0
        wantKeys.value = new Set(
          entries.value.filter((e) => e.want_id).map((e) => key(e)),
        )
      } finally {
        loading.value = false
      }
    }

    async function loadFollowed() {
      const resp = await mediaListFollowed()
      followed.value = resp.code === 200 ? (resp.data ?? []) : []
    }

    async function loadAll() {
      await Promise.all([loadFeed(), loadFollowed()])
    }

    onMounted(() => void loadAll())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    /** 「检查更新」：不给 person_id 时后端转后台跑，这里只负责提示。 */
    async function check() {
      if (!user.isLogin) {
        void router.push({ name: "login", query: { redirect: "/media/follow" } })
        return
      }
      checking.value = true
      try {
        const resp = await mediaCheckFollow()
        if (resp.code !== 200) {
          ElMessage.error(resp.message)
          return
        }
        ElMessage.success(
          resp.data?.started ? t("media.follow_check_started") : t("media.follow_check_busy"),
        )
        // 后台跑完没有推送，等几秒再取一次，让「刚检查过」的结果看得见。
        window.setTimeout(() => void loadAll(), 4000)
      } finally {
        checking.value = false
      }
    }

    async function addWant(entry: FollowFeedEntry) {
      if (!user.isLogin) {
        void router.push({ name: "login", query: { redirect: "/media/follow" } })
        return
      }
      const resp = await mediaAddWant({
        external_id: entry.external_id,
        media_type: entry.media_type,
        title: entry.title,
        poster_path: entry.poster_url,
        release_date: entry.release_date,
      })
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      const next = new Set(wantKeys.value)
      next.add(key(entry))
      wantKeys.value = next
      ElMessage.success(t("media.follow_added_want"))
    }

    async function unfollow(person: FollowedPerson) {
      if (!user.isLogin) {
        void router.push({ name: "login", query: { redirect: "/media/follow" } })
        return
      }
      try {
        await ElMessageBox.confirm(
          t("media.follow_unfollow_confirm", { name: person.name }),
          t("media.follow_unfollow"),
          { type: "warning" },
        )
      } catch {
        return // 用户取消
      }
      const resp = await mediaUnfollowPerson(person.person_id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await loadAll()
      // 当前页被删空时回退一页，避免停在空白页上。
      if (entries.value.length === 0 && page.value > 1) page.value -= 1
    }

    const openEntry = (entry: FollowFeedEntry) => {
      if (entry.item_id) void router.push({ name: "media-item", params: { id: entry.item_id } })
      else void router.push({ name: "media-wants" })
    }

    return () => (
      <div class="ol-media">
        <div class="ol-media-toolbar">
          <div class="ol-media-title-row">
            <span class="ol-media-page-title">{t("media.follow_title")}</span>
            <span class="ol-media-page-sub">
              {t("media.item_count", { count: total.value })}
            </span>
          </div>
          <div class="ol-media-title-actions">
            <ElButton size="small" loading={checking.value} onClick={() => void check()}>
              {t("media.follow_check")}
            </ElButton>
          </div>
        </div>

        <div class="ol-media-nav">
          <button
            type="button"
            class={["ol-media-chip-tag", tab.value === "feed" && "is-on"]}
            onClick={() => (tab.value = "feed")}
          >
            {t("media.follow_feed")}
          </button>
          <button
            type="button"
            class={["ol-media-chip-tag", tab.value === "people" && "is-on"]}
            onClick={() => (tab.value = "people")}
          >
            {t("media.follow_list")} ({followed.value.length})
          </button>
        </div>

        {tab.value === "people" ? (
          followed.value.length === 0 ? (
            <ElEmpty description={t("media.follow_empty")} />
          ) : (
            <div class="ol-follow-people">
              {followed.value.map((person) => (
                <div class="ol-follow-person" key={person.person_id}>
                  <PersonAvatar
                    personId={person.person_id}
                    name={person.name}
                    profilePath={person.profile_path}
                  />
                  <div class="ol-follow-person-meta">
                    <button
                      type="button"
                      class="ol-follow-person-name"
                      onClick={() =>
                        void router.push({
                          name: "media-person",
                          params: { id: person.person_id },
                        })
                      }
                    >
                      {person.name}
                    </button>
                    <div class="ol-follow-person-tags">
                      {person.new_count > 0 ? (
                        <ElTag size="small" type="warning">
                          {t("media.follow_new_count", { count: person.new_count })}
                        </ElTag>
                      ) : null}
                      <ElTag size="small" type="info">
                        {person.last_checked_at
                          ? t("media.follow_last_checked", {
                              time: formatTime(person.last_checked_at),
                            })
                          : t("media.follow_never_checked")}
                      </ElTag>
                      {person.last_error ? (
                        <ElTag size="small" type="danger">
                          {t("media.follow_failed", { error: person.last_error })}
                        </ElTag>
                      ) : null}
                    </div>
                  </div>
                  <ElButton size="small" text={true} onClick={() => void unfollow(person)}>
                    {t("media.follow_unfollow")}
                  </ElButton>
                </div>
              ))}
            </div>
          )
        ) : entries.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.follow_no_entries")} />
        ) : (
          <>
            <div class="ol-follow-grid">
              {entries.value.map((entry) => {
                const poster = mediaExternalImage(entry.poster_url)
                const added = wantKeys.value.has(key(entry))
                return (
                  <div class="ol-follow-card" key={entry.id}>
                    <button
                      type="button"
                      class="ol-follow-poster"
                      onClick={() => openEntry(entry)}
                      title={entry.title}
                    >
                      {poster ? (
                        <img src={poster} alt={entry.title} loading="lazy" />
                      ) : (
                        <span class="ol-follow-poster-empty">{entry.title.slice(0, 1)}</span>
                      )}
                      {entry.item_id ? (
                        <span class="ol-follow-badge is-in">{t("media.wants_ingested")}</span>
                      ) : null}
                    </button>
                    <div class="ol-follow-card-body">
                      <div class="ol-follow-card-title" title={entry.title}>
                        {entry.title}
                      </div>
                      <div class="ol-follow-card-meta">
                        <span
                          class="ol-follow-person-link"
                          onClick={() =>
                            void router.push({
                              name: "media-person",
                              params: { id: entry.person_id },
                            })
                          }
                        >
                          {entry.person_name}
                        </span>
                        {entry.role ? <span> · {entry.role}</span> : null}
                      </div>
                      <div class="ol-follow-card-date">
                        {entry.release_date || formatTime(entry.first_seen_at).slice(0, 10)}
                      </div>
                      <div class="ol-follow-card-actions">
                        {entry.item_id ? (
                          <ElButton size="small" onClick={() => openEntry(entry)}>
                            {t("media.follow_open_item")}
                          </ElButton>
                        ) : (
                          <ElButton
                            size="small"
                            type={added ? "info" : "primary"}
                            disabled={added}
                            onClick={() => void addWant(entry)}
                          >
                            {added ? t("media.follow_added_want") : t("media.follow_add_want")}
                          </ElButton>
                        )}
                      </div>
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
                void loadFeed()
              }}
            />
          </>
        )}
      </div>
    )
  },
})
