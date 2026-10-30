import { computed, defineComponent, onMounted, ref, watch } from "vue"
import { useRouter } from "vue-router"
import { ElButton, ElEmpty, ElIcon, ElInput, ElOption, ElSelect, ElTag } from "element-plus"
import { ArrowLeft, Search } from "@element-plus/icons-vue"
import { mediaListPersons, type MediaPersonSummary } from "~/api/media"
import PersonAvatar from "~/components/PersonAvatar"
import FilePager from "~/pages/home/Pager"
import { t } from "~/i18n"

/**
 * 人物（创作者）列表页 —— `/media/persons`。
 *
 * 排序由后端固定为「关联条目数降序」：媒体库里绝大多数人是跑龙套的，
 * 按姓名排序把「谁在这堆片子里出现最多」这个最有信息量的维度排没了。
 *
 * 后端 `item_count` 已经按当前用户的可见库统计过，这里不做二次过滤。
 */

const PAGE_SIZE = 30

/** 角色筛选：空串 = 不筛。值与后端 `role` 参数同名（director / actor ...）。 */
const ROLES: { value: string; labelKey: string }[] = [
  { value: "", labelKey: "media.role_all" },
  { value: "director", labelKey: "media.role_director" },
  { value: "actor", labelKey: "media.role_actor" },
  { value: "writer", labelKey: "media.role_writer" },
]

export default defineComponent({
  name: "MediaPersonList",
  setup() {
    const router = useRouter()

    const keyword = ref("")
    const role = ref("")
    const page = ref(1)
    const persons = ref<MediaPersonSummary[]>([])
    const total = ref(0)
    const loading = ref(false)

    async function load() {
      loading.value = true
      try {
        const resp = await mediaListPersons({
          keyword: keyword.value.trim() || undefined,
          role: role.value || undefined,
          page: page.value,
          per_page: PAGE_SIZE,
        })
        if (resp.code === 200) {
          persons.value = resp.data?.content ?? []
          total.value = resp.data?.total ?? 0
        } else {
          persons.value = []
          total.value = 0
        }
      } finally {
        loading.value = false
      }
    }

    onMounted(() => void load())

    // 关键词防抖：输入停顿后才发请求（与媒体首页同节奏）
    let keywordTimer: ReturnType<typeof setTimeout> | null = null
    watch(keyword, () => {
      if (keywordTimer) clearTimeout(keywordTimer)
      keywordTimer = setTimeout(() => {
        page.value = 1
        void load()
      }, 350)
    })
    watch(role, () => {
      page.value = 1
      void load()
    })
    watch(page, () => void load())

    const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

    const openPerson = (person: MediaPersonSummary) =>
      void router.push({ name: "media-person", params: { id: person.id } })

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
            <span class="ol-media-page-title">{t("media.persons")}</span>
            <span class="ol-media-page-sub">{t("media.person_count", { count: total.value })}</span>
          </div>
          <div class="ol-media-tools">
            <ElInput
              modelValue={keyword.value}
              size="small"
              clearable={true}
              placeholder={t("media.search_person")}
              prefixIcon={Search}
              style={{ width: "200px" }}
              onUpdate:modelValue={(value: string) => (keyword.value = value)}
            />
            <ElSelect
              modelValue={role.value}
              size="small"
              style={{ width: "140px" }}
              onUpdate:modelValue={(value: string) => (role.value = value)}
            >
              {ROLES.map((item) => (
                <ElOption key={item.value || "all"} label={t(item.labelKey)} value={item.value} />
              ))}
            </ElSelect>
          </div>
        </div>

        {persons.value.length === 0 && !loading.value ? (
          <ElEmpty description={t("media.no_persons")} />
        ) : (
          <>
            <div class="ol-media-person-grid">
              {persons.value.map((person) => (
                <div
                  key={person.id}
                  class="ol-media-person-card"
                  onClick={() => openPerson(person)}
                >
                  {/*
                    有头像（NFO 的 <thumb> 或 TMDB 外链）才请求图片；没有就用姓名首字。
                    列表常驻 30 张卡片，无头像的盲请求会换来 30 条 404（见 PersonAvatar）。
                  */}
                  <PersonAvatar
                    personId={person.id}
                    name={person.name}
                    profilePath={person.profile_path}
                  />
                  <div class="ol-media-person-name" title={person.name}>
                    {person.name}
                  </div>
                  <div class="ol-media-person-meta">
                    <span>{t("media.item_count", { count: person.item_count })}</span>
                    {person.roles.map((role) => (
                      <ElTag key={role} size="small" type="info">
                        {role}
                      </ElTag>
                    ))}
                  </div>
                </div>
              ))}
            </div>

            <FilePager
              page={page.value}
              pageSize={PAGE_SIZE}
              total={total.value}
              pages={pages.value}
              onUpdate:page={(p: number) => (page.value = p)}
            />
          </>
        )}
      </div>
    )
  },
})
