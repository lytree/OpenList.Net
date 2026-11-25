import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRoute, useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElMessage, ElTag } from "element-plus"
import { ArrowLeft } from "@element-plus/icons-vue"
import {
  mediaFollowPerson,
  mediaFollowState,
  mediaGetPerson,
  mediaListPersonItems,
  mediaListItems,
  mediaUnfollowPerson,
  mediaUpdateState,
  type MediaItem,
  type PersonAggregate,
} from "~/api/media"
import MediaGrid from "~/components/MediaGrid"
import PersonAvatar from "~/components/PersonAvatar"
import FilePager from "~/pages/home/Pager"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/**
 * 人物详情页 —— `/media/persons/:id`。
 *
 * 两次请求各管一半：
 * - `GET /media/persons/{id}`（聚合）拿人物信息 + 完整作品列表（带 role/character），
 *   用来算「作品数」与角色标签；
 * - `GET /media/persons/{id}/items`（分页）渲染海报网格。
 *
 * 聚合接口不分页，作品上千的演员一次全返回不划算；只用它取头部信息，
 * 网格走分页接口，两者口径一致（都按可见库过滤）。
 */

const PAGE_SIZE = 30

/** 角色 → i18n 键尾（未知角色原样显示）。 */
const ROLE_KEYS: Record<string, string> = {
  director: "media.role_director",
  actor: "media.role_actor",
  writer: "media.role_writer",
  creator: "media.role_creator",
}

export default defineComponent({
  name: "MediaPersonDetail",
  setup() {
    const route = useRoute()
    const router = useRouter()
    const user = useUserStore()

    const aggregate = ref<PersonAggregate | null>(null)
    const items = ref<MediaItem[]>([])
    const total = ref(0)
    const page = ref(1)
    const loading = ref(true)
    const favorites = ref<Set<number>>(new Set())
    /** 是否已关注这位创作者（关注 → 新作品更新流）。 */
    const following = ref(false)
    const followBusy = ref(false)

    const personId = computed(() => Number(route.params.id))
    const person = computed(() => aggregate.value?.person ?? null)
    /** 作品里出现过的角色类型（去重后展示为标签）。 */
    const roles = computed(() => {
      const set = new Set<string>()
      for (const entry of aggregate.value?.items ?? []) {
        if (entry.role) set.add(entry.role)
      }
      return [...set]
    })

    async function loadPerson() {
      const resp = await mediaGetPerson(personId.value)
      aggregate.value = resp.code === 200 ? resp.data : null
    }

    async function loadItems() {
      loading.value = true
      try {
        const resp = await mediaListPersonItems(personId.value, page.value, PAGE_SIZE)
        if (resp.code === 200) {
          items.value = resp.data?.content ?? []
          total.value = resp.data?.total ?? 0
        } else {
          items.value = []
          total.value = 0
        }
      } finally {
        loading.value = false
      }
    }

    /** 收藏态：一次拉「我的收藏」首页数据不现实，改为按当前页条目回填。 */
    async function loadFavorites() {
      const resp = await mediaListItems({ state: "favorite", page: 1, per_page: 100 })
      if (resp.code === 200) {
        favorites.value = new Set((resp.data?.content ?? []).map((item) => item.id))
      }
    }

    onMounted(() => {
      void loadPerson()
      void loadItems()
      // 游客没有「我的收藏」，这个请求对 user_id=0 只会拿到空集，不如不发
      if (user.isLogin) void loadFavorites()
      void loadFollowState()
    })
    watch(personId, () => {
      page.value = 1
      void loadPerson()
      void loadItems()
      void loadFollowState()
    })
    watch(page, () => void loadItems())

    /** 关注状态是公开读：游客也能问（私有库里的人物不会出现在结果里，按未关注处理）。 */
    async function loadFollowState() {
      const resp = await mediaFollowState([personId.value])
      following.value = resp.code === 200 ? resp.data?.[String(personId.value)] === true : false
    }

    /**
     * 关注 / 取关。没有 TMDB id 的本地人物关注了也永远不会更新，
     * 后端直接 400 —— 这里把按钮置灰并给出原因，不让用户白点。
     */
    async function toggleFollow() {
      if (!user.isLogin) {
        void router.push({ name: "login", query: { redirect: route.fullPath } })
        return
      }
      followBusy.value = true
      try {
        const resp = following.value
          ? await mediaUnfollowPerson(personId.value)
          : await mediaFollowPerson(personId.value)
        if (resp.code !== 200) {
          ElMessage.error(resp.message)
          return
        }
        following.value = !following.value
      } finally {
        followBusy.value = false
      }
    }

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    async function toggleFavorite(item: MediaItem) {
      // 乐观更新 + 失败回滚（与媒体首页同一套写法）
      const next = !favorites.value.has(item.id)
      const set = new Set(favorites.value)
      if (next) set.add(item.id)
      else set.delete(item.id)
      favorites.value = set
      const resp = await mediaUpdateState(item.id, { favorite: next })
      if (resp.code !== 200) {
        const rollback = new Set(favorites.value)
        if (next) rollback.delete(item.id)
        else rollback.add(item.id)
        favorites.value = rollback
      }
    }

    const roleLabel = (role: string) => (ROLE_KEYS[role] ? t(ROLE_KEYS[role]!) : role)

    return () => {
      const current = person.value
      return (
        <div class="ol-media">
          <div class="ol-media-toolbar">
            <div class="ol-media-title-row">
              <ElButton size="small" text={true} onClick={() => router.push({ name: "media-persons" })}>
                <ElIcon style={{ marginRight: "4px" }}>
                  <ArrowLeft />
                </ElIcon>
                {t("media.back_to_persons")}
              </ElButton>
            </div>
          </div>

          <div class="ol-media-person-head">
            <PersonAvatar
              personId={personId.value}
              name={person.value?.name ?? ""}
              profilePath={person.value?.profile_path}
              large={true}
            />
            <div class="ol-media-person-head-meta">
              <h2 class="ol-media-detail-title-plain">{current?.name ?? t("media.no_person")}</h2>
              <div class="ol-media-detail-tags">
                <ElTag size="small" type="info">
                  {t("media.item_count", { count: total.value })}
                </ElTag>
                {roles.value.map((role) => (
                  <ElTag key={role} size="small">
                    {roleLabel(role)}
                  </ElTag>
                ))}
                {current?.tmdb_id ? <ElTag size="small" type="info">TMDB {current.tmdb_id}</ElTag> : null}
              </div>
              <div class="ol-media-detail-tags">
                {/* 原生 title 挂在 ElButton 上会被 TSX 拒绝，包一层 span 承载提示 */}
                <span title={current?.tmdb_id ? undefined : t("media.follow_no_tmdb")}>
                  <ElButton
                    size="small"
                    type={following.value ? "info" : "primary"}
                    loading={followBusy.value}
                    disabled={!current?.tmdb_id}
                    onClick={() => void toggleFollow()}
                  >
                    {following.value ? t("media.following_person") : t("media.follow_person")}
                  </ElButton>
                </span>
                {!current?.tmdb_id ? (
                  <span class="ol-media-page-sub">{t("media.follow_no_tmdb")}</span>
                ) : null}
              </div>
            </div>
          </div>

          {items.value.length === 0 && !loading.value ? (
            <ElEmpty description={t("media.no_person_items")} />
          ) : (
            <MediaGrid
              items={items.value}
              favoriteIds={[...favorites.value]}
              showFavorite={user.isLogin}
              onOpen={(item: MediaItem) => router.push({ name: "media-item", params: { id: item.id } })}
              onFavorite={(item: MediaItem) => void toggleFavorite(item)}
            />
          )}

          <FilePager
            page={page.value}
            pageSize={PAGE_SIZE}
            total={total.value}
            pages={pages.value}
            onUpdate:page={(p: number) => (page.value = p)}
          />
        </div>
      )
    }
  },
})
